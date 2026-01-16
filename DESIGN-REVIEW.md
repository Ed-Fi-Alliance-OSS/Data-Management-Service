# DMS Backend Redesign (DB): Design Review

## Scope / Inputs Reviewed

- Current primary-store implementation:
  - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0001_Create_Document_Table.sql`
  - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0002_Create_Alias_Table.sql`
  - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0003_Create_Reference_Table.sql`
  - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/0010_Create_Insert_References_Procedure.sql`
  - Query path: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs`
- Core runtime architecture boundary:
  - `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`
  - Core → backend interface: `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IDocumentStoreRepository.cs`, `src/dms/core/EdFi.DataManagementService.Core.External/Interface/IQueryHandler.cs`
  - Identity/reference extraction models: `src/dms/core/EdFi.DataManagementService.Core.External/Model/DocumentInfo.cs`
  - Current cascade mechanism (document-store rewrite): `src/dms/core/EdFi.DataManagementService.Core/Backend/UpdateCascadeHandler.cs`
- Metadata sources:
  - `docs/API-SCHEMA-DOCUMENTATION.md`
  - Relational flattening reference: `reference/design/flattening-metadata-design.md`
  - Draft redesign: `reference/design/backend-redesign/overview.md`, `reference/design/backend-redesign/data-model.md`,
    `reference/design/backend-redesign/flattening-reconstitution.md`,
    `reference/design/backend-redesign/extensions.md`,
    `reference/design/backend-redesign/transactions-and-concurrency.md`

Per request, I **ignored** existing SQL scripts/tables/columns relating to **authorization** (e.g., `SecurityElements` and the various `...Authorization...` columns/tables/triggers) and did not critique the authorization replacement design in `reference/design/backend-redesign/auth.md` beyond noting DB interactions where relevant.

---

## Current “Three-Table” Design (What It Is, and Why It Hurts)

### What it is (core, ignoring auth)

The current PostgreSQL store is centered around:

- `dms.Document` (partitioned by `DocumentPartitionKey`): canonical storage of the full API document in `EdfiDoc` JSONB plus metadata (uuid, resource, timestamps).
- `dms.Alias` (partitioned by `ReferentialPartitionKey`): the **identity index** mapping `ReferentialId` → `(DocumentPartitionKey, DocumentId)`.
- `dms.Reference` (partitioned by `ParentDocumentPartitionKey`): tracks references between documents, storing `ParentDocument*`, `AliasId`, and denormalized `ReferencedDocument*` to support reverse lookups and partition-pruned access.
  - Enforced reference validation is optional via FK `dms.Reference(ReferentialPartitionKey, AliasId)` → `dms.Alias`.
  - Write-time maintenance is handled through `dms.InsertReferences(...)` (temp table staging + diff detection + upsert + delete stale).

Querying is currently “document-first”: `GET` queries filter `dms.Document.EdfiDoc` using JSONB containment (`EdfiDoc @> ...`) and page by `ORDER BY CreatedAt OFFSET ... LIMIT ...`.

### Strengths

- Minimal physical schema surface (3 “core” tables) and schema evolution is trivial (JSON).
- Identity resolution is uniform (`ReferentialId` hash + `Alias` lookup).
- Reference validation is centralized (via `Reference` + FK toggle).

### Pain points / drivers for redesign

- **`dms.Reference` write churn and contention**: even with staging/diff optimizations, it’s a hot table during upserts/updates and participates in deadlocks (also supported by `docs/DEADLOCK-ANALYSIS.md`).
- **Partitioning tradeoffs**:
  - Partitioning by `DocumentPartitionKey` avoids some index hotspots, but **many queries touch all partitions** and pagination by OFFSET gets expensive at high offsets (see `reference/design/document-query-indexing-design.md`).
- **Read-time complexity**: JSONB filtering and reconstruction-heavy workloads are difficult to index well without additional projections.
- **Write-time cascade complexity**: to keep embedded reference identities consistent, Core’s `UpdateCascadeHandler` rewrites referencing JSON and forces `_etag/_lastModifiedDate` updates as a content-driven cascade.

These are all consistent motivators for moving toward “tables per resource” and removing the “everything is JSONB” path as the canonical store.

---

## Draft Redesign Summary (backend-redesign)

The draft proposes:

- Canonical storage becomes **relational tables per resource** (root + child tables for arrays, preserving order with `Ordinal`).
- Keep a small `dms` schema layer for cross-cutting concerns:
  - `dms.Document` as canonical per-document metadata (`DocumentId`, `DocumentUuid`, project/resource/version, `Etag`, `LastModifiedAt`, etc.).
  - `dms.ReferentialIdentity` as the uniform identity index (`ReferentialId → DocumentId`), absorbing today’s `Alias`.
  - `dms.ReferenceEdge` as a **resolved** reverse-edge index (`ParentDocumentId → ChildDocumentId`, `IsIdentityComponent`) for cascades, diagnostics, and optional cache invalidation (not for referential integrity).
  - `dms.Descriptor` as a unified descriptor table (ODS-like) used as the FK target for descriptor columns and for URI expansion/type diagnostics.
  - `dms.EffectiveSchema` / `dms.SchemaComponent` to validate that the DB schema matches the effective `ApiSchema.json` set (hash + project versions).
  - `dms.IdentityLock` to coordinate strict identity correctness and phantom-safe closure computations under concurrency.
  - Optional `dms.DocumentCache` as an eventually consistent projection of fully reconstituted JSON to accelerate GET/query and support CDC/indexing.
- No per-resource code generation:
  - Derive mapping from existing ApiSchema sections (`jsonSchemaForInsert`, `documentPathsMapping`, `identityJsonPaths`, `arrayUniquenessConstraints`, `abstractResources`, etc.).
  - Minimal `ApiSchema.json` addition: a `relational` block with `rootTableNameOverride` and `nameOverrides` escape hatch (by restricted JSONPath).
  - Compile resource-specific plans at startup/migrator-time (model/plan/execution layers).
- Preserve the Core/Backend boundary:
  - Core still performs validation, canonicalization, and **computes referential ids**.
  - Backend becomes responsible for flattening, reference resolution to `DocumentId`, and reconstitution.
  - Only required Core change: add **concrete JSON location** to extracted document references (parallel to descriptor reference `Path`).

Overall direction is sound and aligns with the goals stated in `reference/design/backend-redesign/overview.md`.

---

## Feedback

### What looks strong / aligned with requirements

- **Correct direction on canonical storage**: moving canonical truth to relational tables addresses the query/indexing and churn issues that are hard to solve in the three-table JSONB model.
- **Keeping `ReferentialId` is pragmatic**:
  - preserves the uniform resolution mechanism without per-resource “natural key join” SQL,
  - avoids denormalizing referenced natural keys into referencing tables (and thereby reintroducing rewrite cascades),
  - keeps Core/back-end responsibilities clean.
- **Derived mapping vs full flattening metadata**: using `jsonSchemaForInsert` + `documentPathsMapping` + identity/uniqueness metadata avoids bloating ApiSchema and reduces coupling versus explicit `flatteningMetadata` enumeration.
- **Composite parent+ordinal keys** are a good choice for:
  - avoiding `RETURNING`/`OUTPUT` key capture,
  - enabling bulk inserts for nested collections,
  - preserving array order deterministically.
- **Separating `ReferenceEdge` from referential integrity** is the right way to avoid recreating `dms.Reference` pain:
  - resolved edges (`DocumentId` pairs) are cheap to query,
  - diff-based maintenance can reduce churn to 0 for “references unchanged” updates.
- **Effective schema fingerprinting** (`dms.EffectiveSchemaHash`) is an important operational guardrail once schema migration is introduced.

### Key gaps / clarifications to address before implementation

#### 1) “No code generation” needs an explicit operational interpretation

The draft says “no checked-in generated SQL per resource” but allows a migrator to produce and execute DDL from metadata. The user requirement is phrased more strongly (“avoid any kind of code generation”).

Recommendation:
- Explicitly define the constraint as: **no generated artifacts required at build time**, but allow **runtime model/plan compilation** and **DDL emission/execution** by a migrator tool (which is effectively “generation”, but not checked-in codegen).
- If *even migrator DDL generation* is disallowed, then the “tables per resource” approach becomes operationally impractical; this needs a decision now.

#### 2) Naming rules are currently underspecified (high risk for cross-engine drift)

`data-model.md` naming rules are intentionally brief, but physical naming is the primary place you can accidentally “bind to document shape” and create migration churn.

Recommendations:
- Specify the **exact deterministic normalization and truncation algorithm** (including hash length, casing, separator policy, reserved word handling, and collision resolution).
- Treat naming behavior as versioned (already hinted via “relational mapping version” in `EffectiveSchemaHash`).
- Prefer making `resourceSchema.relational.nameOverrides` keyable by existing stable identifiers (if available) rather than only JSONPath; if JSONPath is kept, ensure it is validated against derived elements (the draft does this) and document how renames affect migration.

#### 3) Query semantics and indexing strategy should be made concrete

The redesign expects `queryFieldMapping` to map to root-table columns (no array boundary crossings). That matches current behavior (the current JSONB containment implementation cannot meaningfully support `[*]` in queryField paths anyway).

Recommendations:
- Add a schema-time (migrator/startup) **fail-fast validator**:
  - reject `queryFieldMapping` paths that cannot be mapped to root-table columns, and
  - reject paths with array wildcards (`[*]`) unless/until child-table query support is explicitly designed.
- Define an **index creation policy** driven by ApiSchema:
  - at minimum, create indexes to support common query patterns from `queryFieldMapping` (or a curated subset to avoid index explosion),
  - include a strategy for reference/descriptor filter columns (`..._DocumentId`, `..._DescriptorId`).
- Confirm or document the ordering contract change:
  - current query orders by `dms.Document.CreatedAt`; draft proposes ordering by root `DocumentId`.
  - If ordering is intentionally “unspecified but stable”, document that, and pick the simplest stable ordering (`DocumentId` is fine).

#### 4) Identity-correctness locking is necessary but expensive; scope it deliberately

The `dms.IdentityLock` + closure algorithms are the most complex part of the redesign, and also the place that can regress ingest throughput if applied indiscriminately.

Recommendations:
- Make the lock/cascade behavior explicitly **dependent on ApiSchema**:
  - If a resource has `allowIdentityUpdates=false` and its identity does not depend on other documents’ identities, you can often avoid expensive closure recompute logic on its updates (but still need to handle upstream descriptor/identity changes).
- Make cycle detection a first-class requirement (already stated): reject identity graphs with cycles at **resource-type** level.
- Start with a conservative implementation (deadlock-retry + strict lock ordering) but plan instrumentation:
  - measure “locks acquired per request”, “closure size”, “cascade time”, “retry count”.
- Consider whether representation-version bump strictness needs to be as strong as described for the first iteration:
  - ETag correctness on upstream identity changes is important, but the additional SERIALIZABLE edge-scan protection is the heaviest part.
  - If initial scope can accept rare “missed bump” windows (with eventual correction), call that out explicitly; otherwise keep the strict version but isolate/benchmark it early.

#### 5) `dms.DocumentCache` is “optional” but looks operationally important

Without `dms.DocumentCache`, GET/query reconstitution will frequently perform multi-table hydration + identity projections + descriptor lookups. That may be acceptable for small pages, but likely becomes expensive under real workloads and also complicates CDC.

Recommendations:
- Treat `dms.DocumentCache` as “optional for correctness” but likely **default-on for production**.
- Define a durable rebuild mechanism:
  - how staleness is detected (`Etag` equality is good),
  - how rebuild work is queued (in-memory queue vs DB queue/outbox) so it survives restarts,
  - how rebuild fan-out is bounded when upstream identities change.

#### 6) Descriptor strategy: clarify canonical source and maintenance responsibilities

The unified `dms.Descriptor` table is consistent with the older flattening design and ODS patterns, and is valuable for FK enforcement and URI projection.

Recommendations:
- Clarify whether descriptor resources still get per-resource tables (tables-per-resource rule) or whether descriptors are “special-cased” to canonical storage in `dms.Descriptor` (+ optional per-descriptor extension tables).
- Specify how `dms.Descriptor` is maintained transactionally on descriptor writes and descriptor identity changes (namespace/codeValue changes imply URI change and must drive `ReferentialIdentity` + ETag cascades for dependents).

#### 7) Documentation completeness / consistency nits

- `reference/design/backend-redesign/overview.md` lists “Glossary” and “Suggested Implementation Phases” in the TOC but does not include those sections.
- `flattening-reconstitution.md` says to fail on `$ref`/`allOf`/`oneOf`/`anyOf` in one place, but also says `$ref` should be resolved; reconcile this to “support the subset MetaEd actually emits”.

---

## Suggested Implementation Approach (Pragmatic, Risk-Reducing)

1. **Prototype end-to-end on 1–2 representative resources**:
   - one with nested collections (`School` addresses → periods),
   - one with reference-bearing identity (`StudentSchoolAssociation`).
2. **Make the migrator/model builder the shared core**:
   - one deterministic `RelationalResourceModelBuilder` used by migrator and runtime (as draft proposes),
   - strict schema validation with clear failure messages.
3. **Defer “delta updates” for collections**:
   - start with replace semantics (delete + bulk insert) and measure.
4. **Implement `DocumentReference.Path` enhancement early**:
   - it’s the only required Core change and unlocks correct FK placement for nested references without per-row hashing/JSONPath evaluation.
5. **Instrument and benchmark the locking/cascade path early**:
   - identity closure size, retries, and time spent will decide whether the strict algorithm is viable without further optimization.

---

## Bottom Line

The draft redesign is a strong and well-reasoned path away from the current three-table JSON document store, and it cleanly preserves the Core/Backend boundary while remaining metadata-driven and avoiding per-resource codegen.

The main work to “close” the design is: clarify the operational meaning of “no code generation”, fully specify deterministic physical naming (to avoid cross-engine drift), and validate/benchmark the identity-locking/cascade model early to ensure it will scale under realistic ingest workloads.

