# Backend Redesign (Tables per Resource) — Design Review

## Scope

Reviewed redesign documents in `reference/design/backend-redesign/`:
- `overview.md`
- `data-model.md`
- `flattening-reconstitution.md`
- `caching-and-ops.md`
- `extensions.md`
- `auth.md`

Reviewed current backend implementation for comparison:
- Current three-table store DDL and related logic in `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/`
  - Especially `dms.Document`, `dms.Alias`, `dms.Reference`, reference validation FK, and `dms.InsertReferences(...)`
- Current identity/representation cascade behavior (JSON rewrite) in `src/dms/core/EdFi.DataManagementService.Core/Backend/UpdateCascadeHandler.cs`
- Core/Backend boundary and pipeline context in `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`

## Executive Summary

This is a strong, coherent redesign direction with unusually good attention to *correctness-critical* details (identity resolution, transactional cascades, lock ordering, and “no N+1” IO plans) while staying aligned to the DMS goal of being metadata-driven by `ApiSchema.json` (no per-resource codegen).

The biggest quality gaps are not in the core relational modeling, but in **completeness of the proposal as an operational migration plan**:
- the overview doc is missing the “Suggested Implementation Phases” section it references (and other docs point to),
- there is little/no plan for **migrating existing document-store data** to the relational model (cutover/compatibility),
- multi-tenancy / “DmsInstance” / partitioning assumptions are not made explicit,
- authorization is acknowledged as a starter and is not yet implementation-ready at the same level of detail as the other docs.

If the next iteration closes those gaps, this design is high-quality and implementable.

## What’s High Quality

### 1) Correctness and concurrency are treated as first-class

- The decision to keep `ReferentialId` as the uniform identity key (`dms.ReferentialIdentity`) is well-argued and fits DMS’s metadata-driven goals.
- The locking and closure-expansion spec around `dms.IdentityLock` + `dms.ReferenceEdge(IsIdentityComponent=true)` in `caching-and-ops.md` is concrete enough to implement and test.
- “Strictness collapses” failure modes are explicitly called out: `dms.ReferenceEdge` completeness is treated as correctness-critical (not a best-effort cache).

### 2) Clear separation of concerns and compatibility with existing Core architecture

- The design keeps DMS Core responsible for canonicalization/validation/identity extraction and uses the backend for flattening/reconstitution.
- The one required Core change (carry concrete JSON location for document references inside nested collections) is specific and justified, and it matches an existing pattern (`DescriptorReference.Path`).

### 3) A pragmatic approach to performance (batching and avoiding N+1)

- Flattening and reconstitution are designed around compiled per-resource plans and batched DB operations (multi-row inserts/bulk copy; multi-result-set hydration).
- Composite parent+ordinal keys are a solid choice to avoid `INSERT ... RETURNING` round trips for nested collections.
- Reference and descriptor resolution is explicitly bulked (and cacheable).

### 4) Cross-engine intent is visible

- Many DDL examples explicitly cover PostgreSQL and SQL Server, and the design avoids engine-specific “magic” as a baseline (e.g., prefers application-level derived maintenance and view-based patterns where practical).
- The design anticipates SQL Server “large claim set” constraints (TVPs) for authorization joins.

### 5) Operationally-minded, especially around CDC and projection

- Treating `dms.DocumentCache` as an optional projection (and explicitly preferring eventual consistency) is a good tradeoff: it enables CDC/indexing without making correctness depend on cache cascades.

## Key Risks / Gaps to Address (Quality Blockers)

These are the areas where the design docs are not yet “complete enough to build” or where a wrong assumption could force major rework.

### A) Missing implementation phases and broken cross-document references

- `reference/design/backend-redesign/overview.md` lists “Suggested Implementation Phases” but does not provide it.
- `caching-and-ops.md` points to overview’s “Suggested Implementation Phases”, so the cross-doc narrative currently dead-ends.

Recommendation:
- Make `overview.md` the “source of truth” for the end-to-end plan: phases, milestones, and a compatibility/cutover strategy.
- Add a short “Definition of Done” per phase (schema, query parity, auth parity, migration readiness, etc.).

### B) No migration plan from the current JSON document store

The redesign describes *schema migrations going forward* (ApiSchema changes) but does not describe a strategy for converting the **existing** canonical JSON store (`dms.Document` JSONB + `dms.Alias` + `dms.Reference`) to the new relational primary store.

This is the largest real-world delivery risk because it forces decisions about:
- backfill mechanics (single-shot vs incremental),
- downtime window expectations,
- “dual write” or “read old + write new” transitional modes,
- how to keep `_etag`/`_lastModifiedDate` semantics consistent during migration,
- how OpenSearch/CDC integrations behave during and after cutover.

Recommendation:
- Add a dedicated migration document or an `overview.md` section covering at least:
  - data backfill approach (including referential-id and edge population),
  - cutover strategy (feature flag, per-tenant/instance rollout),
  - rollback strategy,
  - required invariants to validate after backfill (counts, identity uniqueness, FK validity, edge completeness sampling).

### C) Multi-tenancy and partitioning assumptions are not explicit

The current PostgreSQL backend uses partition keys (`DocumentPartitionKey`, `ReferentialPartitionKey`) and list partitions (0..15) for `dms.Document`, `dms.Alias`, and `dms.Reference`.

The redesign largely removes partition keys and depends on:
- a single `dms.Document(DocumentId)` identity PK, and
- per-resource tables keyed by `DocumentId`.

Open questions the redesign should answer explicitly:
- Is a “DMS instance” (`DmsInstanceId`) expected to map to a separate database, schema, or shared tables?
- If shared tables are possible, do `dms.*` and project schemas need an instance discriminator column?
- If not shared, is partitioning still required for the target scale? If yes, what is the cross-engine approach?

Recommendation:
- Add a short “tenancy/instance model” section that states the intended deployment topology and how it impacts physical schema (and indexing/partitioning choices).

### D) Authorization design is not yet at the same readiness level

`auth.md` is clear that it is a starting point; that honesty is good, but it means:
- the overall redesign is not yet “end-to-end ready” for parity with the current production authorization behavior.

Specific gaps to close:
- A concrete derived mapping from existing DMS authorization metadata to the proposed `dms.DocumentSubject` maintenance (including nested securable elements).
- A definitive plan for EdOrg hierarchy/closure maintenance without triggers (current system uses triggers and JSONB hierarchy arrays).
- Performance expectations for authorization-aware paging queries at scale (especially on SQL Server).

Recommendation:
- Expand `auth.md` to the same level of concreteness as `caching-and-ops.md`:
  - explicit table/view DDL conventions,
  - write path steps (where in the transaction, lock considerations),
  - query-time predicates integrated into the page-keyset query shape,
  - test strategy (golden tests vs current behavior).

## Design-Level Concerns (Not Blockers, but Should Be Resolved)

### 1) Query paging strategy (offset-based) and ordering contract

The redesign proposes ordering collection queries by root-table `DocumentId` and applying `OFFSET/LIMIT`.
- This is simple and consistent, but large offsets will degrade (common in relational paging).

Recommendation:
- Consider keyset pagination as a later phase (e.g., `afterDocumentId`) while keeping offset for backward compatibility if required.
- If ordering must match current semantics, validate whether “CreatedAt” ordering is required; the current PostgreSQL backend orders by `CreatedAt`.

### 2) `dms.ReferenceEdge` granularity tradeoff (collapsed edges)

Collapsing `dms.ReferenceEdge` to `(ParentDocumentId, ChildDocumentId)` and OR-ing `IsIdentityComponent` improves write-path simplicity and avoids per-site edge explosion, but it gives up “which path/column referenced me?” fidelity.

Recommendation:
- Confirm the reduced diagnostic granularity is acceptable (or add a separate, non-critical diagnostic-only mechanism if path-level reporting is still required).

### 3) Replace strategy for child tables (write amplification)

The replace strategy (delete all child rows, then insert) is a good baseline for semantics alignment with “replace document”, but it can be expensive for large collections.

Recommendation:
- Keep as v1, but document expected worst-case costs and the criteria for adding diff-based updates later.

### 4) Schema guardrails and failure modes are good, but need a “supported subset” checklist

`flattening-reconstitution.md` calls out unsupported JSON Schema constructs (e.g., `$ref`/`oneOf`/type unions) and relies on fail-fast compilation/migration.

Recommendation:
- Add a concise list of supported schema constructs and a link to where MetaEd/schema generation enforces them.

## Alignment With Current Backend (What This Solves)

The redesign directly targets pain points in the current architecture:

- Current canonical storage is JSON (`dms.Document.EdfiDoc`), with `dms.Alias` and `dms.Reference` plus database-enforced reference validation. Identity updates require rewriting JSON of referencing documents (`UpdateCascadeHandler` and recursive updates in the PostgreSQL backend).
- The redesign stores stable `DocumentId` foreign keys in relational tables, letting the database enforce integrity with FKs and eliminating cross-document JSON rewrites. Reference identity values are reconstituted at read time using identity projection plans, and only derived artifacts (identity index, `_etag`/`_lastModifiedDate`, optional cached JSON) require cascades.

This is a meaningful simplification that should reduce churn and make query/indexing behavior more predictable.

## Recommended Next Steps for the Docs

1. Fix broken/missing sections:
   - add “Suggested Implementation Phases” to `reference/design/backend-redesign/overview.md` and ensure other docs link correctly.
2. Add a real migration plan from the existing store (backfill + cutover + rollback).
3. State tenancy/instance/partitioning assumptions explicitly.
4. Expand authorization to implementation-ready detail or clearly phase it (what works without full auth parity, what doesn’t).
