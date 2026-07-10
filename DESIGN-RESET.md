# DMS-1129 Design Reset

## Status

The design simplification reset is complete. The finalized v1 runtime implementation is not complete.

This file is the scope-control and decision record. The authoritative technical design is
[`reference/design/backend-redesign/design-docs/mssql-cascading.md`](reference/design/backend-redesign/design-docs/mssql-cascading.md).
Reproducible measurements are in
[`reference/design/backend-redesign/evidence/dms-1129`](reference/design/backend-redesign/evidence/dms-1129).

The database cycle and corrected complete-vector screen have passed. The actual cycle has not executed through DMS PUT,
so deferred existing-reference handling remains an open implementation gate under DMS-1275. The current production model
still emits the superseded reduced-FK/identity-value-trigger shape and cannot yet provide that proof.

## Why the Design Was Reset

The previous revision expanded foreign-key pruning into a generalized proof engine, graph-solver protocol, predictive
future-reference language, storage-variant system, mapping-pack certificate, and cross-repository conformance contract.
Those mechanisms were not justified by executable failures or supported-schema measurements.

The reset keeps only behavior required for v1 correctness:

- full-composite document-reference foreign keys;
- complete identity propagation through native cascades;
- SQL Server error-1785 action selection and safe cycle breaking;
- all valid primitive, reference-backed, and mixed identity mutations;
- PostgreSQL's fixed provider behavior; and
- the minimum runtime metadata needed to execute accepted DMS PUT cases.

Everything else must be introduced from a concrete failing fixture or measured provider limit.

## Irreducible Contract

1. `RelationalMappingVersion` remains `v1`.
2. Every document-reference FK is full composite.
3. Identity values propagate through native `ON UPDATE CASCADE`; there is no reduced-FK or identity-value propagation
   trigger fallback.
4. PostgreSQL receives actions mechanically and is never pruned, topology-classified, or failed because of cascade
   topology.
5. SQL Server alone performs error-1785 classification, selective pruning, and topology fail-fast.
6. SQL Server selection is global and deterministic because overlapping diamonds and cycles may require backtracking.
7. Cycle membership is not a failure. Every safely breakable assignment is considered within deterministic bounds.
8. Every pruned SQL Server edge has an exact same-row, same-value, same-statement-boundary carrier for every applicable
   mutation.
9. V1 supports every independently writable primitive component and subset, one or more reference-backed replacements,
   and mixed primitive/reference changes.
10. An accepted cycle must execute through actual unprofiled and profile-constrained DMS PUTs.
11. SQL Server fails before DDL when exhaustive bounded analysis proves no safe assignment; work-limit exhaustion is a
    distinct result.
12. Ordinary provider-independent model validation applies to both dialects.

## Decision Record

### 1. One complete propagation vector per target — accepted

Each target exposes one ordered vector containing:

1. public identity storage values;
2. the finite transitive inventory of stable lineage `DocumentId` anchors; and
3. the target's own `DocumentId` last.

Every incoming reference carries that vector and targets one widened propagation-key unique constraint. Exact same-row,
same-presence storage may be reused; otherwise a structural lineage gets dedicated storage.

The checked-in measurements currently show:

| Measurement | Data Standard 5.2 | Data Standard 5.2 + TPDM |
|---|---:|---:|
| Maximum vector columns | 22 | 27 |
| Maximum lineage anchors | 11 | 15 |
| Maximum SQL Server declared bytes | 1,300 | 1,300 |
| Maximum PostgreSQL four-byte UTF-8 payload | 2,560 | 2,560 |
| Conservative added anchor columns | 238 | 308 |
| Maximum per-table added columns/bytes | 11 / 88 | 20 / 160 |

The computed `anchor_caused_limit_crossings` inventory is empty. Maximum-value provider probes install the full FK,
execute `ON UPDATE CASCADE`, and retain matching child rows. Full generated-schema DDL, total SQL Server row width,
PostgreSQL physical index overhead, and exact mapping-pack size remain implementation qualification.

Site-minimal demand closure, `AnchorSetId` variants, omission proofs, and multiple propagation keys per target are removed.
If implementation later finds a provider failure caused by complete anchors, preserve that fixture and add only the
smallest reduction that fixes it.

#### Ordinary write value source

JSON supplies public identity values but not stable lineage anchors. Ordinary reference resolution therefore produces a
typed `(target DocumentId, ordered lineage-anchor DocumentIds)` result:

1. bulk-resolve referential ids;
2. group successes by compiled target resource;
3. batch-read that target's anchors by `DocumentId` from its concrete root or abstract identity table in the same
   transaction; and
4. attach the tuple to resolved occurrences for O(1) row materialization.

Targets without anchors need no second read. This is ordinary complete-vector materialization, not predictive or deferred
resolution machinery.

### 2. Provider boundary — accepted

PostgreSQL assigns full-vector `CASCADE` to abstract or transitively mutable targets and `NO ACTION` to genuinely immutable
concrete targets. It retains cycles and multiple paths.

SQL Server constructs storage-mapped physical candidates, deduplicates identical candidates, and selects final update
actions globally. The retained cascade multigraph must be acyclic and contain at most one path between every ordered table
pair. Every covered `NO ACTION` edge must pass the exact-carrier obligations in the authoritative design.

The finalized relational model owns `OnUpdate`; DDL only renders it.

### 3. Simple global SQL Server search — accepted direction, measurement open

Start with deterministic iterative-deepening DFS/backtracking:

1. minimize the number of covered `NO ACTION` edges; then
2. choose the lexicographically smallest structural mode vector.

Do not add memoization, canonical solver state, a general cost model, or serialized proof trees unless measured stock,
extension, or adversarial fixtures exceed the selected work bound.

Selected-edge diagnostics retain only a concise structural carrier witness. Failure diagnostics name the first failed
obligation. No universal hashes or proof certificates are public contracts.

### 4. Deferred existing references — open and bounded

An identity-changing PUT can submit a still-present reference using the target's future identity. Ordinary pre-write
lookup misses because the retained cascade has not created that identity yet.

The only retained hypothesis is:

- normal lookup always wins;
- only an already-persisted binding on an existing PUT may defer;
- the persisted target `DocumentId` and only lineage anchors proved unchanged may be reused;
- changing public values come from the submitted/origin write;
- changing lineage anchors come from typed ordinary resolved vectors or proved origin writes;
- subject, receiver, and target rows are correlated and locked by stable identity;
- the normal resolver runs again with a fresh session-bound instance after DML; and
- the submitted identity must resolve uniquely to the same target `DocumentId` before commit.

POST, newly present references, and true retargets use ordinary resolution. A miss, ambiguity, different target, stale
state, failed FK, or contract mismatch rolls back the transaction.

The unprofiled executor has the necessary transaction seam. The profile path currently rejects the miss earlier, so the
gate requires both unprofiled and profile-constrained PUTs, including hidden-state preservation and collection-row
correlation. Until those tests pass, this is an implementation hypothesis rather than a completed runtime protocol.

### 5. Minimal artifact contract — accepted constraint

Persist only what a consumer uses:

- relational model and mapping pack: complete FK columns and final actions;
- mapping/runtime projection: ordered lineage-anchor bindings;
- write plan: only an implemented deferred-binding marker needed by the executor; and
- optional manifest diagnostics only after a concrete consumer exists.

SQL Server modes, exhaustive derivation traces, solver state, omission proofs, semantic hashes, and certificate trees are
not mapping/runtime contracts.

## Decision Gates

| Gate | Status | Required consequence |
|---|---|---|
| Complete-vector measured screen | Passed | Keep one complete vector; retain full-schema physical qualification. |
| Reciprocal provider cycle | Passed | Keep safe zero-hop cycle breaking and PostgreSQL full cascades. |
| Deferred ordinary resolution | Open — DMS-1275 | Do not claim runtime cycle support until both DMS PUT paths pass. |
| Simple global search | Open until DMS-1258 implementation measurements | Add optimization only for an observed bound failure. |
| Minimal artifact contract | Accepted design constraint; implementation pending | Add fields only for concrete runtime/AOT consumers. |

## Delivery Slices

| Slice | Ownership | Exit condition |
|---|---|---|
| Database evidence and static feasibility | Complete | Provider cycle and computed complete-vector screen pass. |
| Complete vectors and physical candidates | DMS-1274 | Deterministic full FK shapes and ordinary anchor-vector resolution are implemented. |
| Provider actions and SQL Server classifier | DMS-1258 | Generated DDL installs and graph/value-flow fixtures produce deterministic outcomes. |
| DMS PUT identity execution | DMS-1275 | Actual unprofiled/profile PUT cycles and all v1 mutation forms pass on both providers. |
| Manifest, AOT, and mapping-pack integration | DMS-1276 | Runtime and pack loading produce equivalent final models and behavior. |
| Full-schema qualification | DMS-1277 | Stock, TPDM, extension, adversarial, concurrency, and performance evidence pass. |

All slices are required before the v1 relational contract is complete.

## Removed Scope

The authoritative design no longer contains:

- a universal semantic-hash or proof-artifact protocol;
- site-specific anchor-set fixed points or omission proofs;
- serialized solver machinery or a provisional-feasible result;
- a generalized predictive future-reference language;
- custom JSON recordset/correlation protocols without an accepted fixture;
- SQL Server proof objects in runtime plans or mapping packs; or
- PostgreSQL topology classification or SQL Server compatibility failure.

## Verification Boundary

The authoritative design owns the full fixture matrix. The reset is successful as a design exercise because it records a
bounded architecture, evidence-backed decisions, explicit open gates, and independently testable delivery slices. The v1
implementation is complete only when every delivery slice passes, especially the actual DMS PUT cycle.
