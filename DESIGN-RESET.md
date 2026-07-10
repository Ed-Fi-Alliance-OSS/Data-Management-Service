# DMS-1129 Design Reset

## Status

The design simplification reset is complete. The finalized runtime implementation is not complete.

This file is the scope-control and decision record. The authoritative technical design is
[`reference/design/backend-redesign/design-docs/mssql-cascading.md`](reference/design/backend-redesign/design-docs/mssql-cascading.md).
Reproducible measurements are in
[`reference/design/backend-redesign/evidence/dms-1129`](reference/design/backend-redesign/evidence/dms-1129).

The authored-identity-only complete-vector screen passed, but it predates post-key-unification effective dependency
promotion and is now a lower-bound baseline rather than the final `v2` capacity gate. DMS-1274 must regenerate it with
storage-promoted lineages before the storage shape is frozen. The current production model still emits the superseded
reduced-FK/identity-value-trigger shape, so implementation and full-schema qualification remain open.

## Why the Design Was Reset

The previous revision expanded foreign-key pruning into a generalized proof engine, graph-solver protocol, predictive
future-reference language, storage-variant system, serialized certificate, and cross-repository conformance contract.
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
4. MetaEd must reject authored semantic identity cycles. After reference binding and key unification, DMS derives the
   effective identity-dependency graph: an authored identity reference remains an edge, and any document reference whose
   mapped local canonical storage overlaps the receiver's public identity storage is promoted to one atomic effective
   identity dependency with the target `DocumentId` as its lineage anchor. A storage-promoted reference must be
   structurally required; an optional overlap fails as `PropagationVectorNotRepresentable` rather than creating
   conditional lineage. DMS rejects cycles in that effective graph before vector recursion on both providers. DMS does
   not cut or execute those cycles.
5. PostgreSQL receives actions mechanically and is never pruned or classified for broader physical cascade topology.
   Every physical edge omitted from the effective identity graph must be proved origin-terminal after canonical mapping.
6. SQL Server alone performs error-1785 physical-cycle and duplicate-path classification, selective diamond pruning, and
   topology fail-fast. Physical-cycle detection is the incomplete result of the same topological sort used for path
   counting, not a separate solver. PostgreSQL is not rejected for SQL Server physical topology.
7. SQL Server selection is global and deterministic because overlapping diamonds and parallel conflicts may require
   backtracking.
8. Every pruned SQL Server edge is safe for every `InitiatingOriginFact` that can update its referenced target key. For
   each fact, retained native source-update and carrier routes start from the same correlated root row, reach the same
   receiver row, carry the origin-affected target-vector columns identically, are implied by covered-edge presence, and
   execute in the same SQL statement.
9. The runtime supports every independently writable primitive component and subset, one or more reference-backed replacements,
   and mixed primitive/reference changes.
10. SQL Server fails before DDL when its all-native physical graph is cyclic or exhaustive bounded analysis proves no safe
    diamond assignment; work-limit exhaustion is a distinct result.
11. Ordinary provider-independent model validation applies to both dialects.
12. Multiple FKs may write one canonical receiver column only when every writer under each `InitiatingOriginFact` starts
    from the same physical root row, composes the same root storage column to that receiver, and executes in the same
    initiating statement.
13. Abstractness is not mutability. An abstract target is mutable iff at least one effective-schema concrete member is
    transitively mutable.

## Decision Record

### 1. One complete propagation vector per target — accepted

Each target exposes one ordered vector containing:

1. public identity storage values;
2. the finite transitive inventory of stable lineage `DocumentId` anchors; and
3. the target's own `DocumentId` last.

Every incoming reference carries that vector and targets one widened propagation-key unique constraint. Exact same-row,
same-presence storage may be reused; otherwise a structural lineage gets dedicated storage.

The checked-in pre-promotion capacity-screen baseline currently shows:

| Measurement | Data Standard 5.2 | Data Standard 5.2 + TPDM |
|---|---:|---:|
| Maximum vector columns | 22 | 27 |
| Maximum lineage anchors | 11 | 15 |
| Maximum SQL Server declared bytes | 1,300 | 1,300 |
| Maximum PostgreSQL four-byte UTF-8 payload | 2,560 | 2,560 |
| Conservative added anchor columns | 238 | 308 |
| Maximum per-table added columns/bytes | 11 / 88 | 20 / 160 |

Under authored-only lineage discovery, the computed `anchor_caused_limit_crossings` inventory is empty. Maximum-value
provider probes install the full FK,
execute `ON UPDATE CASCADE`, and retain matching child rows for the nine-column widest-declared-byte case. The
27-column widest-count case is not probed. Full generated-schema DDL, total SQL Server row width, PostgreSQL physical
index overhead, actual target-unique/FK-supporting index sizes, reference-resolution round trips, representative row
counts, and write/cascade timing remain implementation qualification. These values are not the final effective-dependency
screen because the checked-in measurement currently filters on authored `IsIdentityComponent` only.

DMS-1274 owns an early representative physical row/index and write-amplification gate before the `v2` storage shape is
treated as fixed. DMS-1277 retains exhaustive full-schema qualification across supported and adversarial schemas.

Site-minimal demand closure, `AnchorSetId` variants, omission proofs, and multiple propagation keys per target are removed.
If implementation later finds a provider failure caused by complete anchors, preserve that fixture and add only the
smallest reduction that fixes it.

#### Ordinary write value source

JSON supplies public identity values but not stable lineage anchors. Ordinary reference resolution therefore produces a
typed `(target DocumentId, ordered lineage-anchor DocumentIds)` result:

1. bulk-resolve referential ids;
2. group successes by compiled target resource;
3. batch-read that target's anchors by `DocumentId` from its concrete root or abstract identity table in the same
   transaction, using a batched/multi-result command where supported; and
4. attach the tuple to resolved occurrences for O(1) row materialization.

Targets without anchors need no anchor read. DMS-1277 must measure the actual command/round-trip count; one command per
distinct target group is not assumed to be free. This is ordinary complete-vector materialization, not predictive or
deferred resolution machinery.

### 2. Provider boundary — accepted

Concrete and abstract identity mutability are computed together to a fixed point over the post-key-unification effective
identity-dependency graph. An abstract identity is mutable only when at least one of its effective-schema concrete members
is transitively mutable. A promoted dependency is atomic and contributes the same target-`DocumentId` lineage anchor,
origin flow, and replacement behavior as an authored identity reference. PostgreSQL assigns full-vector
`CASCADE` to targets mutable under that closure and `NO ACTION` to genuinely immutable concrete or abstract targets. It
retains multiple paths. Effective identity cycles have already failed provider-independent validation.

SQL Server constructs storage-mapped physical candidates, deduplicates identical candidates, and selects final update
actions globally. Every physical `ON UPDATE CASCADE` FK participates as a decision or fixed edge. Immediately after
candidate deduplication, one stable topological pass either supplies the order later used for path counting or fails as
`SqlServerCascadeCycleNotSupported`. The retained cascade multigraph must contain at most one path between every ordered
table pair.
A covered `NO ACTION` edge must pass the finite origin-aware structural relation in the authoritative design for every
fact and source-update flow that can change its referenced key. Origin-column composition makes mutation powersets and
symbolic value proofs unnecessary classifier inputs.

Before either provider assigns actions, shared-receiver validation requires every writer under each possible initiating
fact to originate at the same physical root row and compose the same root storage column into the grouped receiver column
in the same statement. Same root table/key shape, source column, and statement without the same correlated root row is not
safe; neither is the same root row and statement with different composed source-column mappings.

The finalized relational model owns `OnUpdate`; DDL only renders it.

### 3. Simple global SQL Server search — accepted finite design, measurement open

Try the all-native graph first. The pre-promotion static reconstruction reports 22 candidates for DS 5.2 and 23 for DS
5.2 plus TPDM, with no SQL Server physical cycles or duplicate-reachability pairs. DMS-1274 must inventory every
post-key-unification promoted dependency and confirm or replace those counts; DMS-1258 must reproduce the resulting v2
counts and return through this fast path.

Only when the fast path finds duplicate reachability, derive the conflict core without enumerating routes and run
deterministic DFS/backtracking over its decision edges. Use stable structural edge order, try native before covered, and
stop at the first valid assignment. Minimum-prune optimization is deferred unless DMS-1277 measures a material supported
write-performance benefit.

Validate each covered edge on demand in the current retained graph for every `InitiatingOriginFact` and source-update flow
that can change its referenced target key. Source-update and carrier routes must start at the same correlated root row,
reach the same receiver row, compose the origin-affected target-vector columns identically, satisfy finite presence
implication, and propagate natively in the same statement. This admits sibling diamonds while a directly mutable sibling
still contributes its own fact and requires a carrier from itself. Traversal is prefix-state-sensitive: backtrack scratch
composition, revisit a vertex reached through a different mapping, correlation, or presence state, and do not use
vertex-only memoization. Do not store route arrays.

The classifier has one deterministic budget of 1,000,000 work units per derived SQL Server schema. Charge one unit only
before assigning a mode to one conflict-core decision edge during DFS/backtracking or visiting one directed edge during
cycle/topological, path-count, conflict-core, legality, or on-demand carrier DFS. Column-mapping comparisons, correlation
elements, presence atoms, vertex initialization, and scratch-buffer resets do not define additional work-unit kinds.
Graph checks and search draw from the same counter in structural order, never wall-clock time. A fixture that consumes
exactly the last unit completes; attempting the next unit yields `CascadeClassificationComplexityExceeded`. Reusable
buffers are an implementation optimization; implementations still must not enumerate or retain carrier-route arrays.

Do not add memoization, canonical solver state, a general cost model, or serialized proof trees unless measured stock,
extension, or adversarial fixtures exceed that bound.

Selected-edge diagnostics retain only the covered edge, applicable initiating fact and source-update route, and selected
structural carrier route. Failure diagnostics name the first failed structural relation. No universal hashes or proof
certificates are public contracts.

### 4. Authored and effective identity cycles, SQL Server physical cycles, reference resolution, and MetaEd boundary — accepted

MetaEd does not currently enforce the required recursive-identity prohibition. METAED-1667 must implement deterministic
authored identity-cycle validation before the ODS relational cascade enhancer so a reachable cycle cannot loop in that
enhancer.

DMS performs its one provider-independent cycle guard after reference binding and key unification but before abstract
lineage or complete-vector recursion. Start with every authored identity reference. Promote any other document reference
when at least one of its mapped local canonical public-value columns is also canonical storage for the receiver's public
identity. Promotion is atomic: the whole reference becomes an effective identity dependency and its target `DocumentId`
becomes a lineage anchor. Require a storage-promoted reference to be structurally required; reject an optional overlap as
`PropagationVectorNotRepresentable`. Reject every self-loop or directed cycle in this effective identity-dependency graph as
`IdentityCascadeCycleNotSupported`, with the witness identifying whether each edge is authored or storage-promoted. This
small graph guard subsumes the authored semantic check for malformed or hand-built input; it is not a general cycle
subsystem.

Use this same effective graph for concrete/abstract mutability, complete-vector lineage, `InitiatingOriginFact`
provenance, and shared-receiver validation. Any physical cascade edge omitted from it must be proven origin-terminal:
after canonical mapping it cannot update a receiver propagation-key column. A fixed physical edge that does update such a
column must be represented in the effective flow model or fail derivation as an unsupported mapping.

MetaEd's authored semantic check and DMS's effective identity check do not prove SQL Server physical acyclicity. DMS's
physical graph also contains origin-terminal mutable non-identity reference sites and fixed physical update cascades. For
example, otherwise-valid mutual non-identity references whose mapped local storage is disjoint from both receivers'
propagation keys can form a SQL Server-prohibited physical cycle without forming an effective identity cycle. SQL Server
reports an incomplete all-native topological sort as `SqlServerCascadeCycleNotSupported`. PostgreSQL performs no
corresponding broader physical-topology rejection, and SQL Server does not search for a cycle cut.

Because effective identity cycles are not accepted, ordinary reference resolution is sufficient. POST, PUT, newly present
references, and true retargets all require normal pre-write resolution. DMS does not reuse a persisted target for a
submitted identity that does not resolve, predict a future identity, or compile deferred existing-reference metadata.

MetaEd must reject recursive authored identity definitions. That cycle rejection is the first and only METAED-1667
delivery. A non-blocking semantic diamond warning is outside METAED-1667 and requires a separate ticket if still wanted.
MetaEd does not run DMS's key-unification-aware effective dependency derivation, physical candidate search, carrier
classification, or work-limit logic. DMS retains the effective-graph guard and is the sole blocking authority for SQL
Server physical realizability after canonical storage mapping.

### 5. Minimal artifact contract — accepted constraint

Persist only what a current consumer uses:

- relational model: complete FK columns and final actions;
- runtime mapping projection: target anchor-read records and ordered local lineage-anchor bindings;
- optional manifest diagnostics only after a concrete consumer exists.

SQL Server modes, exhaustive derivation traces, solver state, omission proofs, semantic hashes, and certificate trees are
not mapping/runtime contracts.

## Decision Gates

| Gate | Status | Required consequence |
|---|---|---|
| Complete-vector measured screen | Authored-only pre-promotion baseline passed; effective-dependency remeasurement open | Keep one complete vector; DMS-1274 must regenerate vector/anchor/storage counts after canonical-overlap promotion and pass the representative physical-storage gate before freezing `v2`; retain DMS-1277 full-schema, widest-count, round-trip, and performance qualification. |
| Cycle boundary | Authored cycles and post-key-unification effective identity cycles fail on both providers; broader SQL Server physical cycles fail its normal legality pass | Keep MetaEd's authored guard plus one small DMS effective-graph guard before vector recursion. Promote canonical identity overlaps atomically with target lineage anchors. Reuse SQL Server's topological ordering to report broader origin-terminal physical cycles; add no PostgreSQL physical-topology rejection or cycle-cut/runtime protocol. |
| Stock all-native topology | Pre-promotion static reconstruction reports 22/23 mutable candidates and no conflicts | DMS-1274 must inventory storage-promoted dependencies and confirm or replace the baseline; DMS-1258 reproduces the resulting implemented v2 counts and returns before conflict-core search. |
| Simple global search | Design-ready; implementation measurements remain DMS-1258/DMS-1277 work | Use on-demand structural carriers and the 1,000,000-unit deterministic bound; add minimum-prune optimization only for measured write-performance benefit. |
| Minimal artifact contract | Accepted design constraint; implementation pending | Add fields only for current runtime or manifest consumers. |

## Delivery Slices

| Slice | Ownership | Exit condition |
|---|---|---|
| Database evidence and static feasibility | Pre-promotion baseline complete | Authored-only capacity numbers and focused widest-byte provider probes pass; DMS-1274 owns regeneration with effective dependencies. |
| Complete vectors and physical candidates | DMS-1274 | Effective identity dependencies are derived after key unification; optional promotion fails as unrepresentable; authored and required storage-promoted cycles fail before vector recursion; promoted references contribute atomic target lineage, mutability, and origin provenance; deterministic full FK shapes and ordinary anchor-vector resolution are implemented; representative physical row/index size and write-amplification pass an early architecture gate; the centralized mapping version is bumped to `v2`. |
| Provider actions and SQL Server classifier | DMS-1258 | SQL Server physical cycles fail from its normal topological legality pass; stock schemas take the all-native fast path; generated DDL installs and structural-carrier diamond fixtures produce deterministic first-feasible outcomes within the fixed work budget. |
| Runtime mapping and manifest integration | DMS-1276 | Current runtime mappings and manifests expose complete vectors, final actions, target anchor-read records, and aligned local columns without classifier internals. |
| Full-schema qualification | DMS-1277 | Freshly provisioned `v2` stock, TPDM, extension, and adversarial DDL pass; exhaustive row/index sizes, widest-count vectors, representative row counts, reference-resolution round trips, concurrency, and write/cascade timing pass and confirm the early DMS-1274 gate. |

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
- provider-independent rejection of broader SQL Server-only origin-terminal physical cascade topology;
- custom JSON recordset/correlation protocols without an accepted fixture;
- SQL Server proof objects in runtime plans or manifests; or
- a blocking MetaEd replica of DMS physical SQL Server classification; or
- PostgreSQL topology classification or SQL Server compatibility failure.

## Verification Boundary

The authoritative design owns the full fixture matrix. The reset is successful as a design exercise because it records a
bounded architecture, evidence-backed decisions, explicit open gates, and independently testable delivery slices. The
implementation is complete only when every remaining delivery slice passes.
