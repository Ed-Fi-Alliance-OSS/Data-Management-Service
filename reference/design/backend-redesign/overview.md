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
4. **Minimize cascade impact**: Use stable surrogate keys (`DocumentId`) and FK relationships so natural-key changes do not require rewriting referencing resource rows. Cascades still exist for derived artifacts (`ReferentialId`, API `_etag` / `_lastModifiedDate`, optional cached JSON), but should be bounded and set-based.
5. **SQL Server + PostgreSQL parity**: The design must be implementable (DDL + CRUD + query) on both engines.

### Constraints / Explicit Decisions

- **Cached JSON is optional (preferred)**: The relational representation is the canonical source of truth. DMS **may** maintain `dms.DocumentCache` as an eventually consistent **projection** for faster GET/query responses and CDC/indexing, but it is not required for correctness.
  - Preferred maintenance: background/write-driven projection (not strict transactional cascades).
  - When enabled, materialize documents independently of API cache misses so CDC consumers see fully materialized documents.
  - Rationale and operational details: see [transactions-and-concurrency.md](transactions-and-concurrency.md) (`dms.DocumentCache` section).
- **ETag/LastModified are representation metadata (required)**: DMS must change API `_etag` and `_lastModifiedDate` when the returned representation changes due to identity/descriptor cascades.
  - Use an **opaque “representation version” token** in `dms.Document` (not a JSON/content hash) and update it with **set-based cascades** (similar to `dms.ReferentialIdentity` recompute) to minimize cascade cost.
  - Strictness: `CacheTargets` computation (1-hop referrers over `dms.ReferenceEdge`) must be phantom-safe; this design uses SERIALIZABLE semantics on the edge scan (see `transactions-and-concurrency.md`).
- **Schema updates are validated, not applied**: DMS does not perform in-place schema changes; it validates on startup that the database matches the configured effective `ApiSchema.json` fingerprint (see `dms.EffectiveSchema`) and refuses to start/serve if it does not. In-process schema reload/hot-reload is out of scope for this design.
- **Authorization companion doc**: Authorization storage and query filtering for this redesign is described in [auth.md](auth.md).
- **No code generation**: No generated per-resource C# or “checked-in generated SQL per resource” is required to compile/run DMS.
- **Polymorphic references use union views**: For abstract reference targets (e.g., `EducationOrganization`), store `..._DocumentId` as an FK to `dms.Document(DocumentId)` for existence and standardize membership validation + identity projection on `{AbstractResource}_View` (derived from `ApiSchema.json` `abstractResources`; see [data-model.md](data-model.md)).

## Key Implications vs the Current Three-Table Design

- Today, the backend uses:
  - `dms.Document` as JSONB canonical storage,
  - `dms.Alias` for `ReferentialId → DocumentId` lookup,
  - `dms.Reference` (+ FK) for reference validation and reverse lookups,
  - plus JSON rewrite cascades (`UpdateCascadeHandler`) to keep embedded reference identity values consistent.
- In this redesign, canonical storage is relational (tables per resource). Referencing relationships are stored as stable `DocumentId` FKs, so:
  - the database enforces referential integrity via FKs (no `dms.Reference` required), and
  - responses reconstitute reference identity values from current referenced rows at read time (no rewrite of referencing rows).
- Identity/URI changes do not require rewriting relational data. Cascades still exist for **derived artifacts** (set-based; made concurrency-correct via `dms.IdentityLock`):
  - `dms.ReferentialIdentity` (required; transactional recompute so `ReferentialId → DocumentId` is never stale after commit)
  - `dms.Document` representation metadata (`Etag`, `LastModifiedAt`) which drives API `_etag` / `_lastModifiedDate`
  - optional cached JSON (`dms.DocumentCache`) rebuild/refresh (eventual)
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
- Extensions (`_ext`, resource/common-type extensions, naming): [extensions.md](extensions.md)
- Transactions, concurrency, and cascades (reference validation, transactional cascades, runtime caching): [transactions-and-concurrency.md](transactions-and-concurrency.md)
- Authorization (subject model + view-based options): [auth.md](auth.md)
- Strengths and risks (operational + correctness + performance): [strengths-risks.md](strengths-risks.md)

## Related Changes Implied by This Redesign

 - **Remove schema reload/hot-reload**: The current reload behavior exists primarily for testing convenience. With relational-first storage, DMS uses startup schema validation (`dms.EffectiveSchema`) instead of runtime schema toggles.
 - **E2E testing approach changes**: Instead of switching schemas in-place, E2E tests should provision separate databases/containers (or separate DMS instances) per schema/version under test.
 - **Fail-fast on schema mismatch**: DMS should verify on startup that the database schema matches the configured effective `ApiSchema.json` set (core + extensions) fingerprint (see `dms.EffectiveSchema`) and refuse to start/serve if it does not.

## Risks / Open Questions

1. **Strict materialization cost (if enabled)**: strict transactional `dms.DocumentCache` maintenance (including identity/URI cascades) can add write-time work and fan out.
   - Mitigation: prefer eventual cache mode; reserve strict mode for deployments that explicitly require representation-sensitive cascades.
2. **Edge correctness**: referential-id and representation-version cascades depend on complete `dms.ReferenceEdge` coverage (including nested collection refs).
   - Mitigation: add invariant checks/audits and build high-coverage tests around derived bindings; fail writes on edge maintenance failures.
3. **ReferenceEdge operational load**: required edge maintenance adds overhead; naive “delete-all then insert-all” can churn.
   - Mitigation: diff-based upsert (stage + insert missing + delete stale) and careful indexing.
4. **Schema change management**: this design assumes the database is already provisioned for the configured effective `ApiSchema.json`; DMS only validates mismatch via `dms.EffectiveSchema` (no in-place schema change behavior is defined here).
