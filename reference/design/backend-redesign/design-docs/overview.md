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
4. **Bounded cascades with stable FKs**: Store relationships as stable surrogate keys (`DocumentId`) for referential integrity, but also persist referenced identity natural-key fields alongside each `..._DocumentId` and keep them synchronized via `ON UPDATE CASCADE` when the referenced target allows identity updates (`allowIdentityUpdates=true`), otherwise `ON UPDATE NO ACTION`. This enables correct indirect-update semantics without a reverse-edge index, while constraining cascades to narrow identity columns (not full-row rewrites).
5. **SQL Server + PostgreSQL parity**: The design must be implementable (DDL + CRUD + query) on both engines.
   - Target platforms: the latest generally-available (GA) non-cloud releases of PostgreSQL and SQL Server.

### Constraints / Explicit Decisions

- **ETag/LastModified are representation metadata (required)**: DMS must change API `_etag` and `_lastModifiedDate` when the returned representation changes due to identity cascades (descriptor rows are treated as immutable in this redesign).
  - This redesign stores representation metadata on `dms.Document` and updates it in-transaction. Indirect representation changes are realized as FK-cascade updates to stored reference identity columns on referrers, which then trigger normal stamping. See [update-tracking.md](update-tracking.md).
- **Schema updates are validated, not applied**: DMS does not perform in-place schema changes. On first use of a given database connection string (after instance routing), DMS reads the database’s recorded effective schema fingerprint (the singleton `dms.EffectiveSchema` row + `dms.SchemaComponent` rows keyed by `EffectiveSchemaHash`), caches it per connection string, and selects a matching compiled mapping set. Requests fail fast if no matching mapping is available. In-process schema reload/hot-reload is out of scope for this design.
- **Authorization is out of scope**: authorization storage and query filtering is intentionally deferred and not part of this redesign phase.
- **No code generation**: No generated per-resource C# or “checked-in generated SQL per resource” is required to compile/run DMS.
- **Polymorphic references use abstract identity tables (and may still expose union views)**: For abstract reference targets (e.g., `EducationOrganization`), provision an `{AbstractResource}Identity` table (`DocumentId` + abstract identity fields + `Discriminator` (NOT NULL)) and reference it with composite FKs (including identity columns); abstract identity tables use `ON UPDATE CASCADE` (trigger-maintained) to propagate identity changes. Union views remain useful for query/diagnostics but are no longer required for reference identity projection. See [data-model.md](data-model.md).

## Key Implications vs the Current Three-Table Design

- Today, the backend uses:
  - `dms.Document` as JSONB canonical storage,
  - `dms.Alias` for `ReferentialId → DocumentId` lookup,
  - `dms.Reference` (+ FK) for reference validation and reverse lookups,
  - plus JSON rewrite cascades (`UpdateCascadeHandler`) to keep embedded reference identity values consistent.
- In this redesign, canonical storage is relational (tables per resource). Referencing relationships are stored as stable `DocumentId` FKs, so:
  - the database enforces referential integrity via FKs (no `dms.Reference` required), and
  - responses can reconstitute reference identity values directly from stored reference identity columns (kept consistent via composite FKs with `ON UPDATE CASCADE` only when the target has `allowIdentityUpdates=true`; otherwise `ON UPDATE NO ACTION`), avoiding read-time joins to referenced tables in the common case.
- Identity/URI changes do not rewrite `..._DocumentId` foreign keys, but **do** propagate into stored reference identity columns via `ON UPDATE CASCADE` when `allowIdentityUpdates=true` (otherwise identity updates are rejected and FKs use `ON UPDATE NO ACTION`). Cascades still exist for derived artifacts, but they are handled row-locally:
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
- **Only required Core change in this redesign**: add concrete *JSON location* (with indices) to extracted document references (see “Document references inside nested collections” in [flattening-reconstitution.md](flattening-reconstitution.md)). Descriptors already carry location via `DescriptorReference.Path`.

- Core continues to produce `DocumentInfo` (identity + `ReferentialId` + extracted references/descriptors, including reference locations) and operates on JSON bodies.
- Backend repositories (`IDocumentStoreRepository`, `IQueryHandler`) become responsible for:
  1. **Flattening** incoming JSON into relational tables
  2. **Reference resolution** (natural keys → `DocumentId`)
  3. **Reconstitution** (relational → JSON) for GET/query responses

This preserves the Core/Backend boundary and avoids leaking relational concerns into Core.

## Deep Dives

This redesign is split into focused docs in this directory:

- Data model (tables, constraints, naming, SQL Server parity notes): [data-model.md](data-model.md)
- Flattening & reconstitution (derived mapping, compiled plans, C# shapes): [flattening-reconstitution.md](flattening-reconstitution.md)
- Unified mapping models (shared in-memory shape for DDL/runtime/packs): [compiled-mapping-set.md](compiled-mapping-set.md)
- AOT compilation (optional mapping pack distribution keyed by `EffectiveSchemaHash`): [aot-compilation.md](aot-compilation.md)
- Mapping pack file format (normative `.mpack` schema): [mpack-format-v1.md](mpack-format-v1.md)
- Extensions (`_ext`, resource/common-type extensions, naming): [extensions.md](extensions.md)
- Transactions, concurrency, and cascades (reference validation, transactional cascades, runtime caching): [transactions-and-concurrency.md](transactions-and-concurrency.md)
- Update tracking (stored stamps for `_etag/_lastModifiedDate/ChangeVersion`, change journals): [update-tracking.md](update-tracking.md)
- DDL Generation (builds the relational model and emits/provisions DDL): [ddl-generation.md](ddl-generation.md)
- DDL generator verification harness (goldens, provision smoke, pack validation): [ddl-generator-testing.md](ddl-generator-testing.md)
- Strengths and risks (operational + correctness + performance): [strengths-risks.md](strengths-risks.md)

## Related Changes Implied by This Redesign

 - **Remove schema reload/hot-reload**: The current reload behavior exists primarily for testing convenience. With relational-first storage, DMS uses per-database schema fingerprint validation (`dms.EffectiveSchema`) instead of runtime schema toggles.
 - **Remove legacy SchemaGenerator**: the existing legacy `EdFi.DataManagementService.SchemaGenerator` toolchain is obsolete under the relational-primary-store redesign and will be removed; the DDL generation utility described in [ddl-generation.md](ddl-generation.md) (and its verification harness) is the replacement.
 - **E2E testing approach changes**: Instead of switching schemas in-place, E2E tests should provision separate databases/containers (or separate DMS instances) per schema/version under test.
 - **Fail-fast on schema mismatch**: DMS should verify on first use of a given database connection string that the database schema matches an available effective `ApiSchema.json` mapping set (see `dms.EffectiveSchema`) and reject requests for that database if it does not.

## Risks / Open Questions

1. **Cascade feasibility (SQL Server)**: For targets with `allowIdentityUpdates=true`, `ON UPDATE CASCADE` can hit “multiple cascade paths” / cycle restrictions. Some reference sites may require trigger-based propagation instead of declarative cascades.
2. **Operational fan-out**: an identity update on a “hub” document can synchronously update many referencing rows (now via FK cascades), increasing deadlock and latency risk.
3. **Schema width/index pressure**: persisting referenced identity fields for all document reference sites increases table width and may require additional indexing for query performance.
4. **Schema change management**: this design assumes the database is already provisioned for the configured effective `ApiSchema.json`; DMS only validates mismatch via `dms.EffectiveSchema` (no in-place schema change behavior is defined here).
