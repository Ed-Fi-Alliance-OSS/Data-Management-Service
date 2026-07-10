# Backend Redesign: Relational Primary Store (Tables per Resource)

## Status

Draft. This is an initial design proposal for replacing the current three-table document store (`Document`/`Alias`/`Reference`) with a relational model using tables per resource, while keeping DMS behavior metadata-driven via `ApiSchema.json`.

## Table of Contents

- [Goals and Constraints](#goals-and-constraints)
- [Key Implications vs the Current Three-Table Design](#key-implications-vs-the-current-three-table-design)
- [Why keep ReferentialId](#why-keep-referentialid)
- [High-Level Architecture](#high-level-architecture)
- [Deep Dives](#deep-dives)
- [Related Changes Implied by This Redesign](#related-changes-implied-by-this-redesign)
- [Risks / Open Questions](#risks--open-questions)

## Goals and Constraints

### Goals

1. **Relational-first storage**: Store resources in traditional relational tables (one root table per resource, plus child tables for collections).
2. **Metadata-driven behavior**: Continue to drive validation, identity/reference extraction, and query semantics using `ApiSchema.json` (no handwritten per-resource code).
3. **Low coupling to document shape**: Avoid hard-coding resource shapes in C#; schema awareness comes from metadata + conventions.
4. **Bounded cascades with stable FKs**: Store relationships as full-composite vectors containing referenced identity
   values, complete transitive lineage anchors, and stable target `DocumentId`. PostgreSQL assigns fixed actions
   mechanically and is never pruned, topology-classified, or failed because of cascade topology. SQL Server globally
   selects error-1785-legal actions and permits exact-carrier `NO ACTION` cuts for diamonds or safely breakable cycles.
   Every FK keeps the complete vector; there is no reduced-FK or identity-value trigger fallback (see
   [mssql-cascading.md](mssql-cascading.md)). Key-unified bindings may be presence-gated aliases of canonical storage;
   see [key-unification.md](key-unification.md).
5. **Cross-engine support**: The design must be implementable (DDL + CRUD + query) on both PostgreSQL and SQL Server, with shared behavior where practical and explicit engine-specific behavior where the engines diverge.
   - Target platforms: the latest generally-available (GA) non-cloud releases of PostgreSQL and SQL Server.

### Constraints / Explicit Decisions

- **ETag/LastModified are representation metadata (required)**: DMS must change API `_etag` and `_lastModifiedDate` when the full resource-state representation before readable profile projection changes, including due to identity cascades. Readable profile filtering can change the returned response shape, but it does not create a separate metadata surface. Descriptor identity/URI is immutable, while descriptor metadata fields are mutable and affect only the descriptor resource's own representation.
  - This redesign stores representation metadata on `dms.Document` and updates it in-transaction. Indirect representation changes are realized as FK-cascade updates to canonical stored identity columns that back the local reference-identity bindings (which may be generated/persisted aliases under key unification), which then trigger normal stamping. See [update-tracking.md](update-tracking.md).
- **Schema updates are validated, not applied**: DMS does not perform in-place schema changes. On first use of a given database connection string (after instance routing), DMS reads the database’s recorded effective schema fingerprint (the singleton `dms.EffectiveSchema` row + `dms.SchemaComponent` rows keyed by `EffectiveSchemaHash`), caches it per connection string, and selects a matching compiled mapping set. Requests fail fast if no matching mapping is available. In-process schema reload/hot-reload is out of scope for this design.
- **Authentication & authorization**: request authentication (token validation) and authorization (data access decisions) are addressed in [auth.md](auth.md). This redesign assumes:
  - requests are authenticated before any schema-dependent work (mapping selection, queries, writes), and
  - authorization is applied in the backend at the SQL layer (page selection + CRUD checks) using `auth.*` companion objects and token-derived authorization context.
- **No code generation**: No generated per-resource C# or “checked-in generated SQL per resource” is required to compile/run DMS.
- **Polymorphic references use abstract identity tables and abstract union views**: For abstract targets, provision an
  `{AbstractResource}Identity` table and target it with the same complete-vector/provider-action rules as concrete
  resources, including SQL Server safe cycle breaking. Also provision `{AbstractResource}_View` for diagnostics; see
  [data-model.md](data-model.md) and [mssql-cascading.md](mssql-cascading.md).

## Key Implications vs the Current Three-Table Design

- Today, the backend uses:
  - `dms.Document` as JSONB canonical storage,
  - `dms.Alias` for `ReferentialId → DocumentId` lookup,
  - `dms.Reference` (+ FK) for reference validation and reverse lookups,
  - plus JSON rewrite cascades (`UpdateCascadeHandler`) to keep embedded reference identity values consistent.
- In this redesign, canonical storage is relational (tables per resource). Referencing relationships are stored as stable `DocumentId` FKs, so:
  - the database enforces referential integrity via FKs (no `dms.Reference` required), and
  - responses reconstitute reference identity values from local bindings. Complete public/anchor vectors stay consistent
    through PostgreSQL fixed actions or SQL Server globally selected actions, including safe cycle cuts (see
    [mssql-cascading.md](mssql-cascading.md)).
- Identity/URI changes do not rewrite terminal target `..._DocumentId` values, but **do** propagate complete public and
  lineage-anchor values. Cascades still exist for derived artifacts, but they are handled row-locally:
  - `dms.ReferentialIdentity` is maintained transactionally by per-resource triggers that recompute referential ids from locally present identity columns (including propagated reference identity values).
  - update tracking metadata is maintained by normal stamping on `dms.Document` (no read-time dependency derivation required); see [update-tracking.md](update-tracking.md).
- Identity uniqueness is enforced by:
  - `dms.ReferentialIdentity` (for all identities, including reference-bearing), and
  - the resource root table’s natural-key unique constraint (including FK `..._DocumentId` columns) as a relational guardrail.

## Why keep ReferentialId

`ReferentialId` is the deterministic UUIDv5 hash of `(ProjectName, ResourceName, DocumentIdentity)` that DMS Core computes (`ProjectName` is the MetaEd project name like `EdFi`, not the URL project segment like `ed-fi`).

This redesign keeps it and stores it in `dms.ReferentialIdentity(ReferentialId → DocumentId)` (absorbing today’s `dms.Alias`) as the backend’s uniform “natural identity key”.

### What it is for

- **Uniform identity resolution without per-resource SQL**: one metadata-driven lookup (`ReferentialId → DocumentId`) supports:
  - write-time reference validation/resolution,
  - POST upsert existence detection, and
  - query-time resolution of reference and descriptor filters.
- **Preserves the Core/Backend boundary**: Core continues to compute referential ids for the written document and extracted references; the backend turns those into relational `..._DocumentId` FKs via bulk lookups.
- **Descriptors use the same mechanism**: descriptor referential ids are computed from (descriptor resource type + normalized URI), so descriptor resolution uses the same index.
- **Polymorphism without an extra alias table**: superclass/abstract alias referential ids preserve current polymorphic reference behavior in a single identity index.

### If we removed it

- The backend needs an alternative identity index, or it must resolve identities by querying/joining per-resource tables on multi-column natural keys (derived from metadata), increasing implementation complexity and cross-engine divergence.
- For reference-bearing identities, resolution becomes recursive/join-heavy (resolve referenced identities first, then match), or forces denormalizing referenced natural-key columns into referencing tables (reintroducing rewrite/cascade pressure the redesign is avoiding).
- Bulk “resolve all refs in a request” becomes many resource-specific queries instead of one `IN (...)` lookup, raising N+1 and batching/parameterization risks.
- Abstract identity lookups require additional mapping tables/views to find concrete `DocumentId`s from abstract identity values.

## High-Level Architecture

Keep DMS Core mostly intact:

- Core remains the home of API canonicalization, validation, identity extraction, and referential-id computation.
- For baseline non-profile relational writes, the required Core extraction-model change is to add concrete *JSON location* (with indices) to extracted document references (see “Document references inside nested collections” in [flattening-reconstitution.md](flattening-reconstitution.md)). Descriptors already carry location via `DescriptorReference.Path`.
- Profile-constrained collection merges add a second Core/backend contract: Core supplies an optional request-scoped `ProfileAppliedWriteRequest` with a `WritableRequestBody`; backend then loads the current stored document and invokes a Core-owned projector to derive `ProfileAppliedWriteContext` (`VisibleStoredBody`, `StoredScopeStates`, and `VisibleStoredCollectionRows`) so merge/delete decisions come from Core-projected stored state rather than backend-owned profile evaluation.
- Core MUST reject any writable profile definition that excludes a field required to compute the compiled semantic identity of a persisted multi-item collection scope.

- Core continues to produce `DocumentInfo` (identity + `ReferentialId` + extracted references/descriptors, including reference locations) and operates on JSON bodies. When profile-specific collection filtering applies, Core also provides the request-scoped write-shaping input described above.
- Backend repositories (`IDocumentStoreRepository`, `IQueryHandler`) become responsible for:
  1. **Flattening** incoming JSON into relational tables
  2. **Reference resolution** (natural keys → `DocumentId`)
  3. **Reconstitution** (relational → JSON) for GET/query responses

This keeps persistence-state loading in backend while keeping profile semantics centralized in Core; visibility comes from profile-shaped stored JSON, while row matching remains backend-owned via compiled semantic identities.

## Deep Dives

This redesign is split into focused docs in this directory:

- Data model (tables, constraints, naming, SQL Server parity notes): [data-model.md](data-model.md)
- Authentication & authorization (token-derived context, DB companion objects, query filtering/batching): [auth.md](auth.md)
- Key unification (canonical columns + generated aliases; presence-gated when optional): [key-unification.md](key-unification.md)
- Flattening & reconstitution (derived mapping, compiled plans, C# shapes): [flattening-reconstitution.md](flattening-reconstitution.md)
- Profiles (Core/backend contract for readable/writable filtering, hidden-data preservation, and profile-scoped merges): [profiles.md](profiles.md)
- Unified mapping models (shared in-memory shape for DDL/runtime/packs): [compiled-mapping-set.md](compiled-mapping-set.md)
- AOT compilation (optional mapping pack distribution keyed by `EffectiveSchemaHash`): [aot-compilation.md](aot-compilation.md)
- Mapping pack file format (normative `.mpack` schema): [mpack-format-v1.md](mpack-format-v1.md)
- Extensions (`_ext`, resource/common-type extensions, naming): [extensions.md](extensions.md)
- Transactions, concurrency, and cascades (reference validation, transactional cascades, runtime caching): [transactions-and-concurrency.md](transactions-and-concurrency.md)
- Update tracking (stored stamps for `_etag/_lastModifiedDate/ChangeVersion`): [update-tracking.md](update-tracking.md)
- Change Queries (`/deletes`, `/keyChanges`, `/availableChangeVersions`, `ContentVersion` mirror, `tracked_changes_*` tables): [change-queries.md](change-queries.md)
- DDL Generation (builds the relational model and emits/provisions DDL): [ddl-generation.md](ddl-generation.md)
- DDL generator verification harness (goldens, provision smoke, pack validation): [ddl-generator-testing.md](ddl-generator-testing.md)
- Strengths and risks (operational + correctness + performance): [strengths-risks.md](strengths-risks.md)

## Related Changes Implied by This Redesign

 - **Remove schema reload/hot-reload**: The current reload behavior exists primarily for testing convenience. With relational-first storage, DMS uses per-database schema fingerprint validation (`dms.EffectiveSchema`) instead of runtime schema toggles.
 - **Remove legacy SchemaGenerator**: the existing legacy `EdFi.DataManagementService.SchemaGenerator` toolchain is obsolete under the relational-primary-store redesign and will be removed; the DDL generation utility described in [ddl-generation.md](ddl-generation.md) (and its verification harness) is the replacement.
 - **E2E testing approach changes**: Instead of switching schemas in-place, E2E tests should provision separate databases/containers (or separate DMS instances) per schema/version under test.
 - **Fail-fast on schema mismatch**: DMS should verify on first use of a given database connection string that the database schema matches an available effective `ApiSchema.json` mapping set (see `dms.EffectiveSchema`) and reject requests for that database if it does not.

## Risks / Open Questions

1. **Cascade feasibility (SQL Server)**: Error 1785 is handled by deterministic bounded global physical action
   selection. Exact-carrier cuts may safely break diamonds or cycles; cycle membership alone is not a failure. Proved
   no-solution and work-limit exhaustion are distinct, with no reduced-FK/trigger fallback. See
   [mssql-cascading.md](mssql-cascading.md).
2. **Operational fan-out**: an identity update on a “hub” document can synchronously update many referencing rows (via PostgreSQL FK cascades or SQL Server native `ON UPDATE CASCADE` on eligible edges), increasing deadlock and latency risk.
3. **Schema width/index pressure**: persisting referenced identity fields for all document reference sites increases table width and may require additional indexing for query performance.
4. **Schema change management**: this design assumes the database is already provisioned for the configured effective `ApiSchema.json`; DMS only validates mismatch via `dms.EffectiveSchema` (no in-place schema change behavior is defined here).
