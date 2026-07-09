# Backend Redesign: Strengths and Risks

## Status

Draft.

This document captures strengths and risks for `overview.md`.

- Overview: [overview.md](overview.md)
- Data model: [data-model.md](data-model.md)
- Key unification deep dive: [key-unification.md](key-unification.md)
- Flattening & reconstitution deep dive: [flattening-reconstitution.md](flattening-reconstitution.md)
- Extensions: [extensions.md](extensions.md)
- Transactions, concurrency, and cascades: [transactions-and-concurrency.md](transactions-and-concurrency.md)
- DDL Generation: [ddl-generation.md](ddl-generation.md)

## Purpose

Capture major strengths and risks of the baseline redesign, with an emphasis on operational correctness and cross-engine feasibility.

---

## Strengths

### Tables per resource + stable `DocumentId` foreign keys

- Embraces relational storage instead of JSONB-first querying.
- Stable `BIGINT` surrogate keys keep reference integrity stable across identity changes.

### `ReferentialId` retention (uniform natural-identity index)

- Keeps a single generic resolution path for all identities (`ReferentialId → DocumentId`) without per-resource identity resolution SQL.
- Works alongside per-site binding identity columns (which may be generated/persisted aliases under key unification): the database stores stable `DocumentId` FKs, while `ReferentialId` provides uniform resolution and upsert detection.

### Full natural-key propagation for document references

- Eliminates a separate reverse-lookup dependency table by materializing indirect impacts through the certified dialect
  FK action assignment into referrers’ canonical stored identity-part columns, with per-site binding columns available
  for query compilation and reconstitution.
- Improves query compilation for reference-identity query parameters by enabling local predicates on per-site binding identity columns (no referenced-table subqueries).

### Key unification for equality-constrained identity parts (single source of truth)

- Stores each equality-constrained unification class in a single canonical physical column (writable; participates in composite FKs and cascades).
- Preserves per-site / per-path binding columns as generated/computed, persisted aliases (presence-gated where needed), keeping query compilation and reconstitution stable.
- Prevents DB-level drift: only the canonical column is written; aliases deterministically project the canonical value and cannot diverge.

### Stored update tracking (stamps + tracked-change rows)

- Serves `_lastModifiedDate/ChangeVersion` from stored `dms.Document` stamps and computes `_etag` from deterministic canonical JSON for the full resource-state document before readable profile projection, excluding response decorations such as `link` (no read-time dependency derivation).
- Uses per-resource `ContentVersion` / `ContentLastModifiedAt` mirror columns (single-table range filter for `?minChangeVersion=X&maxChangeVersion=Y` reads) and per-resource `tracked_changes_*` tables (for `/deletes` and `/keyChanges`) for scalable Change Query candidate selection. See [change-queries.md](change-queries.md).

### Stable collection identities + merge semantics for collections

- `CollectionItemId` preserves physical child-row identity across profile-scoped merges, nested descendants, and extension scopes.
- Root document scope plus sibling `Ordinal` still support deterministic keyset hydration and read-path reconstitution.

### Metadata-driven mapping without code generation

- Derives relational tables/columns from `ApiSchema.json` and compiles read/write plans at startup, keeping DMS extensible without “rebuild-to-add-a-field”.

---

## Risks

### Identity update fan-out (Highest Operational Risk)

Identity updates can synchronously fan out to many rows because:
- identity values are propagated into direct referrers through the certified full-composite physical FK assignment;
  PostgreSQL evaluates fixed actions, while SQL Server jointly selects modes for value-flow safety and error 1785 (see
  [mssql-cascading.md](mssql-cascading.md)), and
- stamping + identity-maintenance triggers execute as part of the same transaction.

Failure modes:
- long-running identity-update transactions,
- lock contention and deadlocks under concurrent writes,
- large log/WAL volume and replication lag.

Mitigations / guidance:
- Keep identity updates operationally rare; consider restricting `AllowIdentityUpdates` and/or running identity updates under controlled operational conditions.
- Implement deadlock retry for write transactions (see [transactions-and-concurrency.md](transactions-and-concurrency.md)).
- Add telemetry for cascaded row counts and stamp / `tracked_changes_*` write rates to detect “hub” fan-in scenarios early.

### Cross-engine value flow and SQL Server cascade paths (Feasibility + Complexity Risk)

Key unification can cause several logical references and parent identities to converge on one canonical receiver column. DDL legality does not establish runtime safety: an update through one FK can invalidate another FK that reads that column, including an FK from an independent parent. The DDL generator must:
- map logical references through canonical storage and deduplicate them into full-composite physical FK candidates,
- derive cross-engine, statement-scoped `ValueFlowAnalysis` facts and proof obligations over exact changed components and
  every FK that may read a cascade-written canonical column,
- include component lineage, same-origin-row correlation, reference co-presence, and statement-boundary compatibility
  (including abstract-identity maintenance triggers) in those obligations,
- evaluate PostgreSQL's fixed action assignment against all obligations, and
- on SQL Server, jointly select `NativeCascade` / `NoPropagation` modes satisfying both the obligations and error 1785,
  then certify coverage against the final assignment.

Shared columns and table reachability alone are not coverage. Every physical FK remains full composite, and there is no `DocumentId`-only shape or identity-value propagation trigger fallback. See [mssql-cascading.md](mssql-cascading.md).

Risks:
- extra derivation complexity (physical-FK canonicalization, labeled value-flow obligations, and deterministic joint SQL
  Server mode selection),
- higher likelihood of engine-specific behavior and performance differences.
- key unification can increase the chance of “multiple cascade paths”: shared canonical columns can participate in multiple composite FKs, creating multi-edge cascades.

Mitigations:
- Include cross-engine `ValueFlowAnalysis` fixtures, PostgreSQL fixed-assignment certification, and SQL Server joint-mode
  selection plus full-composite RI checks in DDL generation verification; see
  [mssql-cascading.md](mssql-cascading.md).
- Benchmark representative “hub” resources on both engines.

### Key unification complexity (Generated aliases + synthetic presence flags)

Key unification introduces generated/computed, persisted alias columns (often presence-gated) plus canonical storage columns and sometimes synthetic `..._Present` presence flags for optional non-reference paths. This adds complexity and new failure modes:
- DDL: more generated columns + CHECK constraints (e.g., presence `NullOrTrue`) with engine-specific syntax.
- Indexing: deciding whether to index canonical storage columns vs per-site binding aliases, and ensuring predicates respect presence gating.
- Write planning: writers must compute canonical values and presence flags deterministically, never write alias columns, and fail fast on conflicting “both present, different values”.
- Diagnostics: equality constraints must be reported as `applied`/`redundant`/`ignored` deterministically, and descriptor-FK de-duplication + unification conflicts must be surfaced for debugging.

### Schema width and index pressure (Storage + Write Amplification Risk)

Persisting per-site binding identity columns for every document reference site (plus canonical + optional synthetic presence columns under key unification) increases:
- table width (more columns),
- composite FK count,
- supporting index count (per FK), and
- update work during cascades.

Mitigations:
- Keep binding columns narrow (identity-only; avoid non-identity denormalization).
- Prefer targeted indexes (supporting FK indexes only; avoid speculative query indexes).
- Benchmark hot resources with many references and deep collections.

### Trigger correctness for stamping and identity maintenance (Correctness Risk)

Correctness depends on generated triggers to:
- stamp `dms.Document` on all representation changes, including referrer-row updates produced by the certified FK action
  assignment. The ordinary `*_Stamp` and identity-maintenance triggers then fire on those row updates exactly as they do
  for direct writes, and
- maintain `dms.ReferentialIdentity` and abstract identity tables transactionally.

These abstract-identity and referential-identity *maintenance* triggers remain. Their statement boundaries are inputs to
`ValueFlowAnalysis` and the resulting dialect certification. The retired SQL Server identity-*value* propagation trigger
(`MssqlIdentityPropagationTrigger`) is not a fallback; safe propagation uses the certified physical FK actions (see
[mssql-cascading.md](mssql-cascading.md)).

Failure mode: missing or incorrect triggers can cause stale `_etag/_lastModifiedDate/ChangeVersion` or incorrect identity resolution.

Mitigations:
- Make DB-apply smoke tests include stamping and `tracked_changes_*` population behavior (see [ddl-generator-testing.md](ddl-generator-testing.md)).
- Add fixture-based tests covering identity propagation scenarios (identity-component and non-identity references),
  optional-site presence combinations, independent parents, component-lineage mismatches, and abstract-trigger statement
  boundaries on both PostgreSQL and SQL Server.

### ReferentialIdentity incorrect mapping (High Correctness/Security Risk)

`dms.ReferentialIdentity` is the canonical resolver for `ReferentialId → DocumentId` (including superclass/abstract alias rows). If a bug ever causes a `ReferentialId` to map to the wrong `DocumentId`, the system can become silently corrupt:
- identity-based upserts can update/overwrite the wrong document,
- references can resolve to incorrect targets,
- downstream authorization decisions can be incorrect (potential data exposure or denial).

Mitigations:
- Treat “same `ReferentialId`, different `DocumentId`” as incident-grade: fail the transaction and emit high-severity diagnostics.
- Add sampling-based verification: recompute expected referential ids from relational source-of-truth and verify round-trip mappings.
- Provide an audit/repair tool to rebuild `dms.ReferentialIdentity` from relational tables.

### Authorization correctness and performance (Security Risk)

The redesign enforces authorization at the SQL layer using `auth.*` companion objects and token-derived authorization context (see `auth.md`). Failure modes include:
- **Incorrect authorization** (data exposure or denial): wrong securable-element→column resolution, missing joins to `dms.Document` for ownership checks, or incorrect `auth.*` view/table maintenance.
- **Unbounded latency**: missing/incorrect indexes on `auth.*` objects or on resource columns used in authorization predicates/joins can turn authorization into table scans on hot paths (GET-many, PUT).
- **Stale security metadata**: caching token-derived authorization context (claim sets, namespace prefixes, ownership tokens, EdOrgIds) without appropriate TTL/eviction can apply outdated policy after configuration changes.

Mitigations:
- Include `auth.*` objects, authorization-required indexes, and representative authorization query execution in the DDL generator verification harness (see `ddl-generator-testing.md`).
- Add end-to-end authorization fixtures that exercise each strategy category (namespace, ownership, relationship, custom view) against both PostgreSQL and SQL Server.
- Treat missing/invalid auth metadata as fail-closed for the request (deny) and emit high-severity diagnostics; do not “skip auth” on errors.
- Use short TTLs for cross-request auth-context caches and best-effort eviction on security configuration changes.

---

## Read Latency & Reconstitution Overhead

The redesign moves read complexity from “fetch 1 JSON blob” to “hydrate root + many child tables + assemble JSON”. Even with “one command / multiple resultsets”, deep resources can require many result sets per page, adding:
- DB CPU cost (joins, sorts by ordinals),
- application allocation pressure (many row objects, JSON assembly work).

This baseline reduces some previous read overhead by:
- reconstituting reference identity fields from local binding columns (no referenced-table joins), and
- computing `_etag` from the canonical JSON form of the full resource-state document before readable profile projection, excluding response decorations such as `link`, while serving `_lastModifiedDate/ChangeVersion` from stored stamps (no dependency-token expansion).

Guidance:
- Benchmark read paths early with representative deep resources and realistic page sizes (25/100/200).

---

## Scale Risks (Large Deployments)

At very large scale (e.g., ~100M documents), several tables and behaviors become operational concerns.

### `dms.Document` (~100M rows)

- Hot updates to `ContentVersion/ContentLastModifiedAt` can increase write amplification (every representation change writes `dms.Document`).
- Random UUID index insertion (`DocumentUuid`, `dms.ReferentialIdentity.ReferentialId`) can increase fragmentation/bloat under sustained ingest.

Mitigations:
- Keep `dms.Document` indexes narrow and purpose-built.
- Plan for UUID index maintenance (engine-appropriate fillfactor, autovacuum/rebuild cadence).

### `tracked_changes_*` growth and `*_Stamp` write amplification

- Per-resource `tracked_changes_*` tables and the shared `tracked_changes_edfi.Descriptor` accumulate deletes (tombstones) and key-changes; high-delete or long-running deployments can grow these tables significantly.
- DMS does not automate truncation of these tables in v1; see [change-queries.md](change-queries.md) §"Operational considerations: tracked-change table volume" for the manual truncation guidance and the loss-of-visibility trade-off.
- Each `*_Stamp` trigger does three things per affected document: bump `dms.Document.ContentVersion` / `ContentLastModifiedAt`, mirror those values onto the resource root (or `dms.Descriptor`), and append tombstone / key-change rows to the corresponding `tracked_changes_*` table when applicable. Benchmark the trigger path under representative write load.
