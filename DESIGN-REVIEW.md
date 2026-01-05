# DMS Backend Redesign (Relational Primary Store) — Design Review

## Scope

Reviewed the redesign documents under `reference/design/backend-redesign/`:

- `overview.md`
- `data-model.md`
- `flattening-reconstitution.md`
- `extensions.md`
- `caching-and-ops.md`
- `auth.md`

Compared against the current PostgreSQL backend design:

- Canonical JSON storage + indexing: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0001_Create_Document_Table.sql`
- Identity index: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0002_Create_Alias_Table.sql`
- Reference maintenance + validation: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0003_Create_Reference_Table.sql`, `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0010_Create_Insert_References_Procedure.sql`, `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0015_Create_Reference_Validation_FKs.sql`
- Trigger-heavy authorization maintenance examples: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0013_Create_StudentSchoolAssociationAuthorization_Triggers.sql`

Referenced the Core/backend boundary and request pipeline in:

- `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`
- `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IDocumentStoreRepository.cs`

## Summary assessment

The redesign is a strong, implementation-oriented proposal. It directly targets the biggest pain points of the current three-table JSONB document store (`Document`/`Alias`/`Reference`) by making relational tables canonical, moving JSON to an optional projection, and replacing “rewrite cascades” with stable `DocumentId` foreign keys and set-based derived-artifact maintenance.

The quality is highest where the design is most explicit about invariants and operational contracts (identity-cascade locking, effective schema fingerprinting, cross-engine guardrails). The biggest remaining gaps are (1) a concrete data migration/backfill and cutover plan from the current JSON store, and (2) production-grade “prove and repair” mechanisms for correctness-critical derived indexes (notably `dms.ReferenceEdge`).

## Strengths

### Clear goals, constraints, and boundaries

- The docs clearly state goals/constraints (relational-first, metadata-driven, no per-resource codegen, PostgreSQL + SQL Server parity, optional JSON projection).
- Core/backend boundary is preserved: Core remains JSON/schema driven (validation + `DocumentInfo` extraction), while the backend owns flattening/reconstitution and query execution.
- Operational contract is explicit: schema changes require migration + restart; runtime schema hot-reload is intentionally removed.

### Data-model direction addresses the current system’s complexity

The redesign simplifies or eliminates several current sources of complexity:

- **Partition-routing and partition-key propagation** (current tables include `DocumentPartitionKey` / `ReferentialPartitionKey` and require “partition-aware” joins and reverse lookups).
- **Reference writes and churn** (current `dms.InsertReferences` is substantial and exists to diff/upsert `dms.Reference` while resolving `dms.Alias`).
- **JSON rewrite cascades** (current update cascades and trigger-based maintenance are tied to JSON-as-source-of-truth).

Replacing those with:

- Relational tables per resource + child tables with composite parent+ordinal keys.
- `dms.ReferentialIdentity` as the primary identity index (absorbing today’s `Alias` role).
- `dms.ReferenceEdge` strictly as a derived reverse index for cascades/diagnostics (not for integrity).
- Optional `dms.DocumentCache` as an eventually consistent projection for fast reads and CDC/indexing.

### Correctness and concurrency are unusually well-specified

- `caching-and-ops.md` provides an implementable locking contract and algorithms (`dms.IdentityLock` + lock ordering + closure expansion + deadlock retry policy).
- The design treats identity maintenance (`dms.ReferentialIdentity`) as a strict derived index (“never stale after commit”) and spells out what must happen in the same transaction.
- ETag/LastModified semantics are defined as **representation metadata** with set-based updates over an impacted set, avoiding “rehash/rewrite” cascades.

### Practical handling of polymorphic references

- The `{AbstractResource}_View` union view approach is pragmatic and metadata-driven, and aligns with how DMS needs to validate membership and reconstitute abstract identity objects.
- Storing polymorphic FK columns as `..._DocumentId` (FK to `dms.Document`) plus application-level membership validation is a reasonable baseline for cross-engine parity.

### Extensions plan is coherent and aligns to relational keys

- `_ext` mapping rules keep extension data isolated (per extension schema) and key-aligned to the base scopes they extend, preserving delete cascade correctness and simplifying reconstitution.
- The minimal `resourceSchema.relational` override block is a good “escape hatch” without returning to full flattening metadata.

### Authorization direction is a clear improvement over current triggers

- The proposed `dms.DocumentSubject` + view-based authorization removes authorization state from JSON columns and avoids trigger-based fanout maintenance on write-heavy tables.
- “Large claim set” handling is explicitly considered for SQL Server (TVPs) vs PostgreSQL (`ANY(array)`).

## Why keep `ReferentialId`

In this redesign, `ReferentialId` remains the deterministic UUIDv5 hash of `(ProjectNamespace, ResourceName, DocumentIdentity)` (from `ApiSchema.json` identity paths). It is stored in `dms.ReferentialIdentity` (absorbing today’s `dms.Alias`) and used as the backend’s uniform “natural identity key”.

### What it is for

- **Uniform identity resolution**: enables a single, metadata-driven natural-key resolver (`ReferentialId → DocumentId`) for write-time reference validation/resolution, POST upsert existence detection, and query-time resolution of reference/descriptor filters (no per-resource natural-key SQL) (`reference/design/backend-redesign/caching-and-ops.md`).
- **Preserves the Core/backend boundary**: Core already computes referential ids for the document and extracted references; the backend turns those into relational `..._DocumentId` FKs via bulk lookups (`reference/design/backend-redesign/flattening-reconstitution.md`).
- **Descriptors fit the same mechanism**: descriptor referential ids are computed from (descriptor resource type + normalized URI), so descriptor resolution uses the same index (`reference/design/backend-redesign/data-model.md`).
- **Polymorphism without extra tables**: supports “superclass/abstract alias” rows so polymorphic references can resolve identities consistently without keeping a separate Alias table (`reference/design/backend-redesign/data-model.md`).

### If we removed it

- The backend needs an alternative identity index or would have to resolve identities by querying/joining per-resource tables on multi-column natural keys (generated per resource from metadata), increasing implementation and cross-engine complexity.
- For **reference-bearing identities**, resolution becomes recursive/join-heavy (resolve referenced identities first, then match), or forces denormalizing referenced natural-key columns back into the referencing tables (reintroducing rewrite/cascade pressure the redesign is explicitly avoiding).
- Bulk resolution of “all refs in a request” shifts from one `IN (...)` against `dms.ReferentialIdentity` to many resource-specific queries, raising N+1 risk and parameterization/batching complexity.
- Polymorphic/abstract identity lookups would need additional mapping tables/views to locate concrete `DocumentId`s from abstract identity values.
- Any caching of “natural identity → `DocumentId`” still needs a canonical composite key or hash; in practice you’d reintroduce a referential-id-like token under another name, unless you make a breaking contract change (e.g., require clients to reference solely by `id`/UUID rather than Ed-Fi-style natural keys).

## Key gaps / risks

### 1) Data migration, cutover, and rollback are underspecified

The phased implementation plan is useful, but there is no concrete plan for migrating existing data from today’s canonical JSONB `dms.Document` into the new per-resource relational tables, nor for safely cutting over production instances:

- Backfill approach (one-shot vs incremental, and whether to block writes during backfill).
- Verification strategy (how to prove relational reconstitution matches existing JSON semantics).
- Coexistence period (dual-write/dual-read) and rollback story.

Recommendation: add a dedicated migration/backfill design with operator workflows, safety checks, and failure handling.

### 2) `dms.ReferenceEdge` completeness is correctness-critical; production proof/repair needs to be explicit

The design correctly calls out that edge coverage is required for:

- identity closure recompute (`IsIdentityComponent=true`)
- representation-version cascades (`Etag`/`LastModifiedAt`)
- cache/projection targeting and delete diagnostics

What’s still missing is a concrete operational “prove it / detect drift / repair it” plan, for example:

- By-construction guarantees tied to FK-column materialization (so edges can’t be “forgotten”).
- Audit queries/jobs (e.g., sample documents: recompute expected edges from tables and compare).
- Repair tools (rebuild edges for one `DocumentId`, or bulk rebuild per resource/schema).

Recommendation: treat “edge audit + rebuild” as a required operational feature, not a nice-to-have.

### 3) Paging/order contract needs implementation alignment

The redesign standardizes on paging by the **resource root table** `DocumentId` (ascending), while the current PostgreSQL backend pages by `CreatedAt` over the JSON `dms.Document` store (see `SqlAction.GetAllDocumentsByResourceNameAsync`). This will need an implementation change to page directly on root tables and join to `dms.Document` only for envelope fields.

Recommendation: pick and document a single ordering contract (likely `CreatedAt, DocumentId` to preserve existing behavior and ensure stable paging), and align indexes and examples accordingly.

### 4) Wide-table/column-limit risk is acknowledged but pushed upstream; needs a gating plan

Inlining object properties into the owning table can yield very wide root tables. SQL Server limits (1024 columns, row size constraints) are mentioned, but the mitigation depends on upstream MetaEd rules.

Recommendation: make the MetaEd constraints a release gate (with validation tests), and consider a documented fallback (e.g., 1:1 split tables) for exceptional resources rather than requiring emergency schema redesigns.

### 5) Operational/observability specifics for background projection and cascades need tightening

The design introduces operationally important moving parts:

- background projector for `dms.DocumentCache`
- targeted rebuilds driven by `dms.ReferenceEdge`
- identity closure recompute with deadlock retries

Recommendation: specify minimum required telemetry and operator controls:

- projector backlog metrics, rebuild failure counts, staleness percentage
- identity closure size distributions, time holding `IdentityLock`, deadlock/retry counts
- explicit “operator actions” (rebuild cache, rebuild edges, validate effective schema)

### 6) Authorization design is directionally strong but needs an “end-to-end” proof

The auth document presents good options, but several aspects remain open:

- deriving the EdOrg hierarchy from metadata vs hard-coding known Ed-Fi parent references
- project scoping of hierarchy/closure when multiple projects coexist
- stable generation and maintenance of `auth.*` views across schema versions

Recommendation: prototype one complete strategy (e.g., Student relationships) against the derived naming rules and measure query-time performance under large claim sets for both engines.

## Suggested next steps

1. Add a migration/backfill/cutover plan (including verification and rollback).
2. Add an operational correctness plan for `dms.ReferenceEdge` (audit + repair).
3. Decide and standardize the paging/order contract; align indexes and examples.
4. Prototype “one resource + one relationship-heavy resource” end-to-end (mapping, edges, identity-cascades, and auth filtering), then measure and iterate.

## Minor consistency notes

- Keep terminology unambiguous between `ProjectNamespace` vs `ProjectName` (docs are mostly consistent but it is easy to confuse in DB naming).
- Call out how descriptor uniqueness and discriminator/type enforcement behave when multiple projects/extensions contribute descriptor types.
- Confirm client-facing semantics when ETag is a monotonic token (not a content hash): `If-Match` failures due to upstream representation changes are intended and should be called out in API contract/testing.
