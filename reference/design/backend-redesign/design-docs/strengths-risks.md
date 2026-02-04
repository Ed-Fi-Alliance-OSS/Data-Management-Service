# Backend Redesign: Strengths and Risks

## Status

Draft.

This document captures strengths and risks for `overview.md`.

- Overview: [overview.md](overview.md)
- Data model: [data-model.md](data-model.md)
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
- Works alongside propagated reference-identity columns: the database stores stable `DocumentId` FKs, while `ReferentialId` provides uniform resolution and upsert detection.

### Full natural-key propagation for document references

- Eliminates a separate reverse-lookup dependency table by materializing indirect impacts as FK-cascade updates to referrers’ stored reference identity columns.
- Improves query compilation for reference-identity query parameters by enabling local predicates on propagated identity columns (no referenced-table subqueries).

### Stored update tracking (stamps + journal)

- Serves `_etag/_lastModifiedDate/ChangeVersion` from stored `dms.Document` stamps (no read-time dependency derivation).
- Uses a narrow, append-only journal (`dms.DocumentChangeEvent`) for scalable Change Query candidate selection.

### Composite parent+ordinal keys for collections

- Composite `(ParentKeyParts..., Ordinal)` keys avoid identity capture and enable batch writes for nested collections.
- Preserves collection ordering deterministically for read-path reconstitution.

### Metadata-driven mapping without code generation

- Derives relational tables/columns from `ApiSchema.json` and compiles read/write plans at startup, keeping DMS extensible without “rebuild-to-add-a-field”.

---

## Risks

### Identity update fan-out (Highest Operational Risk)

Identity updates can synchronously fan out to many rows because:
- identity values are propagated into all direct referrers via `ON UPDATE CASCADE` (or trigger-based propagation where required) when `allowIdentityUpdates=true`, and
- stamping + identity-maintenance triggers execute as part of the same transaction.

Failure modes:
- long-running identity-update transactions,
- lock contention and deadlocks under concurrent writes,
- large log/WAL volume and replication lag.

Mitigations / guidance:
- Keep identity updates operationally rare; consider restricting `AllowIdentityUpdates` and/or running identity updates under controlled operational conditions.
- Implement deadlock retry for write transactions (see [transactions-and-concurrency.md](transactions-and-concurrency.md)).
- Add telemetry for cascaded row counts and stamp/journal write rates to detect “hub” fan-in scenarios early.

### SQL Server cascade-path restrictions (Feasibility + Complexity Risk)

SQL Server may reject FK graphs with “cycles or multiple cascade paths”. The design depends on update propagation, so the DDL generator must:
- use `ON UPDATE CASCADE` where permitted for targets with `allowIdentityUpdates=true`, and
- fall back to trigger-based propagation for restricted edges (deterministic, set-based), without changing correctness semantics.

Risks:
- extra trigger complexity,
- higher likelihood of engine-specific behavior and performance differences.

Mitigations:
- Include cascade feasibility checks (and trigger fallback emission) in DDL generation verification.
- Benchmark representative “hub” resources on both engines.

### Schema width and index pressure (Storage + Write Amplification Risk)

Persisting propagated identity columns for every document reference site increases:
- table width (more columns),
- composite FK count,
- supporting index count (per FK), and
- update work during cascades.

Mitigations:
- Keep propagated columns narrow (identity-only; avoid non-identity denormalization).
- Prefer targeted indexes (supporting FK indexes only; avoid speculative query indexes).
- Benchmark hot resources with many references and deep collections.

### Trigger correctness for stamping and identity maintenance (Correctness Risk)

Correctness depends on generated triggers to:
- stamp `dms.Document` on all representation changes (including FK cascades), and
- maintain `dms.ReferentialIdentity` and abstract identity tables transactionally.

Failure mode: missing or incorrect triggers can cause stale `_etag/_lastModifiedDate/ChangeVersion` or incorrect identity resolution.

Mitigations:
- Make DB-apply smoke tests include journaling and basic stamp behavior (see [ddl-generator-testing.md](ddl-generator-testing.md)).
- Add fixture-based tests covering identity propagation scenarios (identity-component and non-identity references) on both PostgreSQL and SQL Server.

### ReferentialIdentity incorrect mapping (High Correctness/Security Risk)

`dms.ReferentialIdentity` is the canonical resolver for `ReferentialId → DocumentId` (including superclass/abstract alias rows). If a bug ever causes a `ReferentialId` to map to the wrong `DocumentId`, the system can become silently corrupt:
- identity-based upserts can update/overwrite the wrong document,
- references can resolve to incorrect targets,
- downstream authorization decisions can be incorrect (potential data exposure or denial).

Mitigations:
- Treat “same `ReferentialId`, different `DocumentId`” as incident-grade: fail the transaction and emit high-severity diagnostics.
- Add sampling-based verification: recompute expected referential ids from relational source-of-truth and verify round-trip mappings.
- Provide an audit/repair tool to rebuild `dms.ReferentialIdentity` from relational tables.

---

## Read Latency & Reconstitution Overhead

The redesign moves read complexity from “fetch 1 JSON blob” to “hydrate root + many child tables + assemble JSON”. Even with “one command / multiple resultsets”, deep resources can require many result sets per page, adding:
- DB CPU cost (joins, sorts by ordinals),
- application allocation pressure (many row objects, JSON assembly work).

This baseline reduces some previous read overhead by:
- reconstituting reference identity fields from local propagated columns (no referenced-table joins), and
- serving `_etag/_lastModifiedDate/ChangeVersion` from stored stamps (no dependency-token expansion).

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

### `dms.DocumentChangeEvent` (journal growth)

- Append-only journaling can be large at high write rates.
- Retention/partitioning policy becomes mandatory once Change Queries are used in production.

Mitigations:
- Partition/retain by `ChangeVersion` and/or `CreatedAt` as appropriate per engine.
- Expose and enforce `oldestChangeVersion` (see [update-tracking.md](update-tracking.md)).
