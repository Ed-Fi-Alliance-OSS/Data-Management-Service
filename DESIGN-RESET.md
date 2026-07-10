# DMS-1129 Design Reset

## Status

The design simplification reset is complete. The finalized runtime implementation is not complete.

This file is the scope-control and decision record. The authoritative technical design is
[`reference/design/backend-redesign/design-docs/mssql-cascading.md`](reference/design/backend-redesign/design-docs/mssql-cascading.md).
Reproducible measurements are in
[`reference/design/backend-redesign/evidence/dms-1129`](reference/design/backend-redesign/evidence/dms-1129).

The corrected complete-vector screen has passed. The current production model still emits the superseded
reduced-FK/identity-value-trigger shape, so implementation and full-schema qualification remain open. Identity cycles are
outside the supported model and are rejected before provider action assignment.

## Why the Design Was Reset

The previous revision expanded foreign-key pruning into a generalized proof engine, graph-solver protocol, predictive
future-reference language, storage-variant system, mapping-pack certificate, and cross-repository conformance contract.
Those mechanisms were not justified by executable failures or supported-schema measurements.

The reset keeps only behavior required for the supported release contract:

- full-composite document-reference foreign keys;
- complete identity propagation through native cascades;
- correct SQL Server error-1785 action selection for diamonds and overlapping multiple-path conflicts;
- all valid primitive, reference-backed, and mixed identity mutations;
- PostgreSQL's fixed provider behavior; and
- the minimum runtime metadata needed to execute accepted DMS PUT cases.

Everything else must be introduced from a concrete failing fixture or measured provider limit.

## Irreducible Contract

1. The complete-vector storage and provider-action rules are `RelationalMappingVersion = v2`. Implementing them must
   bump the DMS-owned constant and require fresh provisioning; there is no migration or `v1` compatibility mode.
2. Every document-reference FK is full composite.
3. Identity values propagate through native `ON UPDATE CASCADE`; there is no reduced-FK or identity-value propagation
   trigger fallback.
4. Identity cycles are invalid. Semantic cycles fail before vector derivation; physical cycles introduced by
   storage mapping fail before action selection. DMS does not cut or execute cycles.
5. PostgreSQL receives actions mechanically and is never pruned or classified for multiple-path topology.
6. SQL Server alone performs error-1785 duplicate-path classification, selective pruning, and topology fail-fast.
7. SQL Server selection is global and deterministic because overlapping diamonds and parallel conflicts may require
   backtracking.
8. Every pruned SQL Server edge has a structural carrier with the same physical mutation origin, receiver row, and
   complete-vector mapping; covered-edge presence implies carrier presence, and every carrier edge propagates natively
   in the same SQL statement.
9. The runtime supports every independently writable primitive component and subset, one or more reference-backed replacements,
   and mixed primitive/reference changes.
10. SQL Server fails before DDL when exhaustive bounded analysis proves no safe diamond assignment; work-limit
    exhaustion is a distinct result.
11. Ordinary provider-independent model validation applies to both dialects.

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
concrete targets. It retains multiple paths. Identity cycles have already failed provider-independent validation.

SQL Server constructs storage-mapped physical candidates, deduplicates identical candidates, and selects final update
actions globally. The input graph is cycle-free by validation; the retained cascade multigraph must contain at most one
path between every ordered table pair. A covered `NO ACTION` edge must have a retained carrier route that passes the
finite structural relation in the authoritative design. Whole-vector equality makes mutation powersets and symbolic
value proofs unnecessary classifier inputs.

The finalized relational model owns `OnUpdate`; DDL only renders it.

### 3. Simple global SQL Server search — accepted finite design, measurement open

Use deterministic iterative-deepening DFS/backtracking:

1. minimize the number of covered `NO ACTION` edges; then
2. choose the lexicographically smallest structural mode vector.

Before mode search, enumerate structurally eligible carrier routes through the all-native acyclic graph. A route is
eligible only when it has the same physical origin and receiver row as the covered edge, composes to the identical
complete-vector column mapping, satisfies the finite presence implication, and consists entirely of native cascades in
the selected mode vector.

The classifier has one deterministic budget of 1,000,000 work units per derived SQL Server schema. One unit is charged
whenever conflict-core derivation examines an edge, carrier-route DFS extends a route by one edge, mode DFS assigns one
decision edge, graph validation examines one retained edge, or carrier validation examines one precomputed route. The
counter and traversal use structural order and never wall-clock time. Exhausting the budget yields
`CascadeClassificationComplexityExceeded`.

Do not add memoization, canonical solver state, a general cost model, or serialized proof trees unless measured stock,
extension, or adversarial fixtures exceed that bound.

Selected-edge diagnostics retain only the covered edge and selected structural carrier route. Failure diagnostics name
the first failed structural relation. No universal hashes or proof certificates are public contracts.

### 4. Identity cycles, reference resolution, and MetaEd boundary — accepted

MetaEd does not admit recursive identity definitions in the supported input contract. DMS independently validates the
semantic identity-reference graph before complete-vector recursion so malformed, hand-built, or pack-loaded input cannot
bypass that boundary. It repeats the check after storage mapping so table collapse cannot introduce a physical cycle. A
self-loop or directed identity cycle fails with `IdentityCascadeCycleNotSupported`.

Because cycles are not accepted, ordinary reference resolution is sufficient. POST, PUT, newly present references, and
true retargets all require normal pre-write resolution. DMS does not reuse a persisted target for a submitted identity
that does not resolve, predict a future identity, or compile deferred existing-reference metadata.

MetaEd continues to reject recursive authored identity definitions. It may also emit a non-blocking semantic diamond
warning as early author feedback. It does not run DMS's physical candidate search, carrier classification, or work-limit
logic. DMS is the sole blocking authority for SQL Server realizability after canonical storage mapping.

### 5. Minimal artifact contract — accepted constraint

Persist only what a consumer uses:

- relational model and mapping pack: complete FK columns and final actions;
- mapping/runtime projection: ordered lineage-anchor bindings;
- optional manifest diagnostics only after a concrete consumer exists.

SQL Server modes, exhaustive derivation traces, solver state, omission proofs, semantic hashes, and certificate trees are
not mapping/runtime contracts.

## Decision Gates

| Gate | Status | Required consequence |
|---|---|---|
| Complete-vector measured screen | Passed | Keep one complete vector; retain full-schema physical qualification. |
| Simple global search | Design-ready; implementation measurements remain DMS-1258/DMS-1277 work | Use the finite structural carrier relation and 1,000,000-unit deterministic bound; add optimization only for an observed bound failure. |
| Minimal artifact contract | Accepted design constraint; implementation pending | Add fields only for concrete runtime/AOT consumers. |

## Delivery Slices

| Slice | Ownership | Exit condition |
|---|---|---|
| Database evidence and static feasibility | Complete | Computed complete-vector screen and maximum-value probes pass. |
| Complete vectors and physical candidates | DMS-1274 | Deterministic full FK shapes and ordinary anchor-vector resolution are implemented; the centralized mapping version is bumped to `v2`. |
| Provider actions and SQL Server classifier | DMS-1258 | Generated DDL installs and structural-carrier diamond fixtures produce deterministic outcomes within the fixed work budget. |
| Manifest, AOT, and mapping-pack integration | DMS-1276 | Runtime and `v2` pack loading produce equivalent final models and behavior. |
| Full-schema qualification | DMS-1277 | Freshly provisioned `v2` stock, TPDM, extension, adversarial, concurrency, and performance evidence pass. |

All slices are required before the `v2` relational contract is complete.

## Removed Scope

The authoritative design no longer contains:

- a universal semantic-hash or proof-artifact protocol;
- mutation powersets, symbolic value origins, and generalized carrier proof obligations;
- site-specific anchor-set fixed points or omission proofs;
- serialized solver machinery or a provisional-feasible result;
- a generalized predictive future-reference language;
- deferred existing-reference resolution or runtime cycle metadata;
- safe-cycle search, zero-hop cycle carriers, or cycle PUT protocols;
- custom JSON recordset/correlation protocols without an accepted fixture;
- SQL Server proof objects in runtime plans or mapping packs; or
- a blocking MetaEd replica of DMS physical SQL Server classification; or
- PostgreSQL topology classification or SQL Server compatibility failure.

## Verification Boundary

The authoritative design owns the full fixture matrix. The reset is successful as a design exercise because it records a
bounded architecture, evidence-backed decisions, explicit open gates, and independently testable delivery slices. The
implementation is complete only when every remaining delivery slice passes.
