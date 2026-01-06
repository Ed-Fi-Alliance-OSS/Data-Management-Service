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
- Authorization: [auth.md](auth.md)

## Purpose

To get an impartial review of design strengths and risks from Gemini 3.0 Pro and Claude Opus 4.5. This document is a summary of their combined feedback from being given a framing prompt as "a senior .NET/C# architect with deep expertise in building scalable, high-performance server applications and APIs...", the design documents themselves, and a prompt of "Provide a critical review of this redesign of a backend software architecture."

---

## Strengths

### Tables per resource + stable `DocumentId` foreign keys

- This is the correct strategic pivot for Ed-Fi: it embraces relational storage instead of fighting the database with JSONB-first querying.
- Using stable `BIGINT` surrogate keys for references eliminates relational-level rewrite cascades when natural keys change; only derived artifacts need set-based updates.

### `ReferentialId` retention (uniform natural-identity index)

- Keeping `ReferentialId` avoids either:
  - per-resource identity resolution queries (cross-engine divergence and N+1/batching risks), or
  - denormalizing referenced natural keys into referencing tables (reintroducing cascade pressure).
- UUIDv5 provides a clean, deterministic identity key that supports bulk `ReferentialId → DocumentId` resolution without handwritten per-resource SQL.

### Composite parent+ordinal keys for collections

- Using `(ParentKeyParts..., Ordinal)` keys avoids `INSERT ... RETURNING`/`OUTPUT` identity capture, enabling real batch inserts for nested collections.
- It also preserves collection ordering deterministically for read-path reconstitution.

### Metadata-driven mapping without code generation

- Deriving relational tables/columns from `ApiSchema.json` and compiling read/write plans at startup is the right boundary: it keeps DMS extensible and avoids “rebuild-to-add-a-field”.
- The design’s emphasis on precompiled plans is the key guardrail for runtime performance in a “generic ORM built from metadata” approach.

### Reference-resolution optimization for nested collections

- Carrying concrete JSON locations for extracted references (e.g., `$...addresses[2]...`) enables an efficient `DocumentReferenceInstanceIndex` keyed by ordinal path.
- This is critical to making the no-codegen flattener performant: reference resolution can be bulk and request-scoped, without per-row JSONPath evaluation or per-row hashing.

### Clear separation of required correctness vs optional projections

- The docs clearly separate transactional correctness artifacts (`dms.ReferentialIdentity`, `dms.ReferenceEdge`, representation metadata) from optional eventual projections (`dms.DocumentCache`).
- That separation makes trade-offs explicit and helps avoid accidentally depending on `dms.DocumentCache` for correctness.

### Authorization alignment with ODS-style views

- Moving away from JSONB authorization arrays and aligning to predictable `auth.*` view shapes is a sound direction.
- The performance of this approach will hinge on the EdOrg closure/tuple table and having the right covering indexes for the most common joins.

---

## Risks

### ReferenceEdge Integrity (Highest Operational Risk)
`dms.ReferenceEdge` is treated as a strict derived dependency index for:
- `dms.ReferentialIdentity` cascade recompute (`IsIdentityComponent=true`)
- representation-version cascade (`dms.Document(Etag, LastModifiedAt)`)
- optional `dms.DocumentCache` rebuild targeting
- delete diagnostics (“who references me?”)

If `dms.ReferenceEdge` ever diverges from the actual persisted FK graph (bug in diff logic, manual DB edits, partial failures), the system can become **silently incorrect**:
- cascades miss documents (stale referential ids / stale etags)
- cache rebuild targeting misses documents (stale JSON returned)
- delete conflict messages become incomplete

#### Why it’s tricky
The design currently frames verification/validation as “optional/configurable”. For a system depending on derived tables for correctness, **validation should be mandatory** (at least sampling-based) or correctness becomes “best effort in prod”.

#### Possible actions / mitigations
- **Consistency Watchdog (production feature)**:
  - background process that continuously samples documents and validates:
    - “resolved outgoing FK targets” == “ReferenceEdge children”
  - behavior on discrepancy:
    - either self-heal (rebuild edges for the sampled document) and emit an alert/event, or
    - hard alert + mark instance unhealthy until repaired (deployment choice)
- **Audit & Repair tool (ship early)**:
  - full scan mode (offline/maintenance window): recompute edges from relational tables and repair `dms.ReferenceEdge`
  - targeted mode: rebuild one document’s edges (by `DocumentId`/`DocumentUuid`) and optionally cascade to dependents
- **Sampling-based verification on writes**:
  - verify on a small percentage of commits (e.g., 0.1–1%) in production
  - treat mismatches as “incident-grade” signals (alert immediately)
- **Fail writes on edge-maintenance failure** (already in design):
  - do not allow “fire-and-forget” edge writes

#### Instrumentation ideas
- `ReferenceEdgeWriteRows`, `ReferenceEdgeDiffRowsInserted/Deleted`
- `ReferenceEdgeVerifySampleRate`, `ReferenceEdgeVerifyFailures`
- watchdog: mismatch count by resource type; repair count; time-to-repair

---

### IdentityLock / Phantom-Safe Locking (Complexity + Deadlock/Throughput Risk)
The `dms.IdentityLock` orchestration (Algorithms 1–2 in `transactions-and-concurrency.md`) is the most complex part of the design:
- shared locks on identity-component children before parent writes (Invariant A)
- closure expansion to fixpoint (Algorithm 2)
- SERIALIZABLE semantics for phantom-safe parent-of-closure scans

Failure modes:
- **deadlocks under load** (especially if cycles exist or lock ordering is violated in any edge case)
- **unbounded work** for closure expansion in deep/wide dependency graphs
- **ingest throughput collapse** if many writers contend on shared “hub” locks

#### Specific “hub resource” contention scenario (must benchmark)
Bulk importing large sets where many documents reference the same identity component (e.g., 50,000 `StudentSchoolAssociation` rows referencing the same `School`):

- PostgreSQL: `FOR SHARE` is cheap, but high concurrency can contend on lock manager structures for that single hot row.
- SQL Server: `WITH (HOLDLOCK)` can be aggressive; if write transactions are long, those locks are held longer, compounding contention.

#### Possible actions / mitigations
- **Cycle safety**:
  - reject identity dependency cycles at the resource-type level during startup schema validation (already stated in the draft) and ensure the rule is exhaustive (including transitive cycles).
- **Define a deadlock/serialization retry policy**:
  - max retry attempts
  - jittered exponential backoff strategy
  - what is logged vs. counted vs. surfaced (telemetry + diagnostics)
  - which SQLSTATE / error codes trigger retry (Postgres `40P01`, `40001`; SQL Server deadlock victim)
- **Bound closure expansion work**:
  - hard limits (configurable): max closure size and/or max closure depth
  - fail the transaction (409/500 depending on cause) when limits are exceeded, with actionable diagnostics
- **Benchmark the hub scenario explicitly**:
  - micro-benchmark: 50 concurrent writers acquiring shared locks on the same `IdentityLock(DocumentId)` and then performing typical write work
  - measure throughput, average/99p lock wait time, and deadlock/retry rates
- **Consider “batch lock” optimizations** (if hub contention is real):
  - lock once per batch per hub id (implementation detail depends on ingestion mechanics)
  - ensure batching does not break invariants (especially “child before parent”)

#### Instrumentation (minimum)
- `IdentityLockSharedAcquisitionMs`, `IdentityLockUpdateAcquisitionMs`, `IdentityLockRowsLocked`
- `IdentityClosureSize`, `IdentityClosureIterations`, `IdentityClosureMs`
- deadlock/serialization counters + retries exhausted counter

---

## Read Latency & Reconstitution Overhead
The redesign moves read complexity from:
- “fetch 1 JSONB blob”
to:
- “hydrate root + many child tables + identity projections + descriptor expansions + assemble JSON”

Even with “one command / multiple resultsets”, a deep resource can require many result sets per page. This adds:
- DB CPU cost (joins, sorts by ordinals, projection joins)
- application allocation pressure (many small row objects, JSON assembly work)

#### Guidance / critique
Treating `dms.DocumentCache` as “optional” may not be practical given:
- standard API latency expectations (example target: <200ms for typical reads)
- protecting DB CPU from repeated reconstitution work

#### Possible actions / mitigations
- **Benchmark read paths early**:
  - representative deep resources (many child tables, nested collections)
  - page sizes matching real clients (25/100/200)
  - both “cache hit” and “cache miss (reconstitution)” 99p latency and CPU

#### Instrumentation
- per-resource `GetByIdLatencyMs`, `QueryLatencyMs`
- cache hit rate, rebuild rate, rebuild latency
- per-request result set count and reconstitution CPU time
