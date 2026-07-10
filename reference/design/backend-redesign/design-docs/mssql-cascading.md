# SQL Server Foreign-Key Pruning and Identity Propagation

## Status

This document is the authoritative DMS-1129 design after the simplification reset. It defines the smallest known
architecture that preserves full referential integrity, correct SQL Server diamond handling, complete identity-change
support, and the provider boundary between SQL Server and PostgreSQL. Genuine identity cycles are unsupported.

The corrected transitive complete-vector measured screen has executable/reproducible evidence. It is a capacity screen,
not an operational storage or performance measurement. Implementation and full-schema qualification remain open. The
earlier reciprocal-cycle database POC is recorded as historical evidence only; cycle execution is not part of the
supported contract.

The complete-vector storage and provider-action changes are `RelationalMappingVersion = v2`. The implementation slice
must bump `SchemaHashConstants.RelationalMappingVersion` when these rules land so an unchanged ApiSchema cannot match a
database provisioned with the older reduced-FK mapping. Operators must provision a fresh database; DMS adds no mapping
compatibility mode, legacy-schema interpretation, or migration.

## 1. Settled Decisions and Non-Goals

### Settled decisions

1. Every document-reference foreign key contains the complete ordered propagation vector. A reduced or
   `DocumentId`-only reference FK is not an alternative.
2. Identity values propagate through native `ON UPDATE CASCADE`. DMS does not use an identity-value propagation trigger.
   Existing triggers for referential-identity, abstract-identity, stamping, and change-query maintenance retain their
   separate responsibilities.
3. Authored semantic identity cycles fail provider-independent validation before vector derivation. DMS does not cut or
   execute them. MetaEd must perform the authoring-time check, and DMS repeats the same small graph guard at its input
   boundary.
4. PostgreSQL receives fixed actions mechanically. PostgreSQL is never pruned or classified for physical cascade
   topology.
5. SQL Server alone classifies the physical cascade graph for error-1785 cycles and duplicate reachability, selects
   covered `NO ACTION` edges, and fails before DDL when the physical all-native graph has a cycle or no safe diamond
   assignment exists. Physical-cycle failure is the incomplete result of the same topological sort needed for path
   counting, not a separate cycle solver. A SQL Server physical cycle does not make an otherwise-valid PostgreSQL mapping
   fail.
6. SQL Server selection is global and may backtrack. Local first-fit pruning is not correct for overlapping diamonds or
   parallel conflicts.
7. Every covered `NO ACTION` edge is safe for every `InitiatingOriginFact` that can update its referenced target key. For
   each such fact, a retained native route from the same initiating root row reaches the same receiver row and carries
   exactly the target-vector columns affected by that origin; covered-edge presence implies both source-update and carrier
   presence, and both routes execute in the same SQL statement.
8. The runtime covers every independently writable primitive component, every non-empty primitive subset, one or more
   reference-backed replacements, multiple reference replacements, and mixed primitive/reference changes.
9. The finalized relational model, not the DDL emitter, owns the chosen FK actions.
10. Abstractness does not imply mutability. An abstract identity target is mutable only when at least one of its concrete
    members in the effective schema is transitively mutable.
11. Topology legality does not prove value-flow safety. Before either provider assigns actions, reject any canonical
    receiver column that multiple mutable FKs can write unless shared-receiver validation succeeds independently for
    every possible `InitiatingOriginFact`, with every writer reached from the same initiating root row in the same
    database statement and carrying the same root storage column into that receiver column.

### Non-goals

- Success for arbitrary direct SQL identity changes outside DMS-authorized writes.
- A general graph theorem-prover API, serialized derivation trace, or universal semantic-hash protocol.
- Per-site propagation-vector minimization unless a measured supported-provider limit requires it.
- A generic missing-reference fallback or predictive reference-resolution language.
- A cross-repository requirement that MetaEd reproduce DMS physical FK identities, selected actions, or carrier
  witnesses.
- Runtime or manifest derivation traces beyond the explicit target anchor-read contract required by current execution.
- General root-to-child or cross-table equality propagation outside document-reference identity propagation.
- General cycle support, cycle-cut search, zero-hop cycle carriers, or deferred future-identity resolution.

Ordinary provider-independent model validation still applies to both dialects. The PostgreSQL policy above is only a
statement about cascade topology.

## 2. Complete Propagation-Vector Storage

### One complete transitive vector per target

For each reference target `T`, derive one ordered `CompletePropagationVector(T)`:

1. every public identity storage value in target identity order;
2. the complete transitive lineage-anchor union described below; and
3. `T.DocumentId` last.

An identity-contributing document reference is one atomic direct lineage even when it supplies several public identity
values. Its stable anchor is the referenced row's `DocumentId`. For each direct lineage `T -> U`, append that anchor and
then every lineage anchor in `CompletePropagationVector(U)` in `U`'s order. Apply this recursively in the target's stable
structural reference order. The result is the finite transitive union of stable rows whose public values can flow through
`T`'s identity-reference chains. Exact duplicate storage may be reused only with the same-row and presence proof below;
otherwise each structural lineage path receives dedicated storage. Recursive authored identity definitions fail
provider-independent validation as `IdentityCascadeCycleNotSupported`. Descriptor values are not document-reference
lineages.

The transitive union is required even though only the direct references are independently replaceable on `T`. If `T`
identifies through `U`, `U` identifies through `V`, and a receiver key-unifies `T`'s inherited `V` value with a direct
`V` reference, a `U -> V` retarget must propagate both the new public value and `V.DocumentId` through `T`. A direct-only
inventory would combine the new public value with the receiver's old `V.DocumentId` and invalidate that full FK.

Every incoming reference to `T` carries the same vector, and `T` exposes one corresponding propagation-key unique
constraint. The current single `*_RefKey` constraint is widened when anchors are present; DMS does not create one key
variant per incoming site.

An inherited anchor is stored once: the local lineage-anchor column carried by `T`'s direct identity reference to `U` is
also the column `T` exposes in its own complete vector. Do not create a second target-only copy. An existing direct
`..._DocumentId` on `T` may replace that column only when the exact reuse conditions below hold.

The local vector uses canonical storage after key unification. A local anchor column may be reused only when structural
metadata proves all of the following:

- the local value identifies the same referenced target row;
- every correlated public component maps to the same canonical storage;
- required/optional presence is equivalent; and
- the value is maintained in the same statement boundary.

Otherwise the incoming site receives a dedicated anchor column. All-or-none reference checks cover public identity
values, lineage anchors, and terminal target `DocumentId` together.

### Why the anchor is required

In Data Standard 5.2, `Session` identity includes School and SchoolYear references. `CourseOffering` contains a Session
reference and a direct School reference whose School id is key-unified with the Session identity value. A Session
School retarget must propagate both the new School id and `Session.School_DocumentId`; propagating only the public value
would combine one School's id with another School's stable row id.

With the complete model, every Session reference uses:

```text
(SchoolId,
 SchoolYear,
 SessionName,
 School_DocumentId,
 SchoolYear_DocumentId,
 Session.DocumentId)
```

The CourseOffering site may reuse its exact direct School anchor. It still carries the SchoolYear anchor. An unrelated
Session referrer receives the same complete vector rather than a narrower site-specific variant. The extra storage is
intentional; schema minimization is not a correctness requirement.

### Measured feasibility screen and implementation validation

Implemented derivation must validate the final vector and receiver table against applicable supported-provider limits.
It fails with `PropagationVectorNotRepresentable` rather than dropping an anchor or weakening a foreign key.

The checked-in measurement tool and report are under [`../evidence/dms-1129`](../evidence/dms-1129). The stock-schema
results are:

| Measurement | Data Standard 5.2 | Data Standard 5.2 + TPDM |
|---|---:|---:|
| Incoming document-reference sites | 318 | 349 |
| Referenced targets | 72 | 81 |
| Widened target propagation keys | 43 | 51 |
| Maximum complete lineage anchors in one target vector | 11 | 15 |
| Maximum vector columns | 22 | 27 |
| Conservative added anchor occurrences | 238 | 308 |
| Maximum added columns/bytes on one table | 11 / 88 | 20 / 160 |
| Maximum SQL Server declared vector bytes | 1,300 | 1,300 |
| Maximum PostgreSQL four-byte UTF-8 payload | 2,560 | 2,560 |
| Additional unique constraints | 0 | 0 |

The focused SQL Server and PostgreSQL probes install the nine-column, widest-declared-byte vector, accept maximum-size
test values, and cascade a maximum-width public value plus a lineage anchor. They do not exercise the 27-column
widest-count vector. Complete anchors create no new crossing of the measured key/index column, declared-key payload, or
table-column screens. This evidence does not measure total SQL Server row width, PostgreSQL tuple/index overhead, actual
target-unique or FK-supporting index sizes, or representative write/cascade timing. It does not replace full
generated-schema DDL qualification. It is sufficient to reject site-minimal anchor closure as unjustified complexity, but
the `v2` storage shape is not frozen until DMS-1274 passes the early representative physical row/index and
write-amplification gate.

The conservative relational-manifest projection grows by 3.25/3.64 percent and generated SQL by 1.26/1.43 percent for
DS 5.2/TPDM.

### Ordinary write source for lineage anchors

Reference JSON supplies the target's public identity values, but it does not supply the stable `DocumentId` anchors in
the target's complete vector. Ordinary successful reference resolution therefore returns a typed resolved vector, not
only the terminal target `DocumentId`:

```text
(target DocumentId, ordered target lineage-anchor DocumentIds)
```

The resolver obtains it without a per-occurrence query:

1. resolve all submitted referential ids through `dms.ReferentialIdentity` as one ordinary bulk operation;
2. group successful document references by compiled target resource;
3. batch-read each target group's ordered lineage-anchor columns from its concrete root or abstract identity table by the
   resolved `DocumentId` values, using the same transaction and a batched/multi-result command where supported; and
4. attach that ordered anchor tuple to every resolved occurrence for the row materializer.

A target with no lineage anchors needs no anchor read. The implementation must expose and qualify the actual database
round-trip count; issuing one command per distinct target group is not assumed to be free. A missing target row, duplicate
row, wrong target type, vector
arity mismatch, or null required anchor fails reference resolution. The request still supplies public values and the full
FK remains the final correlation check. This is the ordinary value source for all anchor-bearing POST and PUT writes; it
is not deferred-resolution metadata, predictive SQL, or a mutation-case protocol.

Runtime mappings carry one explicit target anchor-read record keyed by target resource:

```text
TargetResource
TargetTable
DocumentIdColumn
OrderedAnchorColumns
```

`TargetTable` names the concrete root or abstract identity table. Incoming site bindings carry only their aligned local
anchor columns. Runtime uses the target record directly and never infers an abstract table or anchor order from incoming
FKs, resource naming conventions, or repeated lineage paths.

## 3. Physical Foreign-Key Candidate Derivation

Candidate construction occurs after reference binding, key unification, abstract-identity derivation, transitive
identity mutability, and complete-vector storage are final.

For each logical reference site:

1. resolve the local binding table and target identity table;
2. map every local and target vector item through canonical storage;
3. align public values, lineage anchors, and terminal `DocumentId` positionally;
4. validate presence and all-or-none semantics; and
5. create a physical candidate before choosing `OnUpdate`.

A candidate is identified by typed structural values:

```text
(local table,
 ordered local storage columns,
 target table,
 ordered target storage columns,
 delete action)
```

`OnUpdate`, generated constraint name, logical JSON path, and SQL Server mode are not part of candidate identity.
Semantic roles never split an otherwise-identical physical candidate. Identical physical candidates are deduplicated
while retaining every contributing logical site's role, provenance, and presence predicate. Presence is normalized to
the physical-row and optional-reference atoms used by the structural carrier relation. Parallel candidates remain
distinct edges only when at least one physical identity component above differs.

Derivation-local provenance also supplies the possible `InitiatingOriginFact` and origin-flow states used after
deduplication. Those facts, mappings, correlations, and statement boundaries are not candidate identity and never split
an otherwise-identical physical constraint.

Internal equality and ordering use structural records and ordinal comparers. A durable hash is added only if a concrete
artifact consumer later needs correlation outside the derivation process.

The derivation order is:

```text
semantic identity-cycle validation
-> reference binding
-> key unification and abstract identity
-> transitive identity mutability
-> complete vectors and propagation keys
-> storage-mapped physical candidates and deduplication
-> SQL Server only: topologically order the all-native physical graph or fail its physical-cycle legality check
-> shared-receiver value-flow validation
-> PostgreSQL fixed actions OR SQL Server duplicate-reachability selection using the retained topological order
-> finalized TableConstraint.ForeignKey values
-> naming, shortening, manifests, and DDL
```

## 4. Effective Mutability and PostgreSQL Fixed Actions

### Effective-schema mutability closure

Compute concrete and abstract identity mutability together to a fixed point:

1. seed each concrete resource from its own `AllowIdentityUpdates` value;
2. mark a concrete resource transitively mutable when an identity-contributing reference targets a mutable concrete or
   abstract identity;
3. mark an abstract identity mutable when at least one of its effective-schema concrete members is transitively mutable;
   and
4. repeat steps 2 and 3 until neither set changes.

An abstract resource with only immutable concrete members is immutable. Abstractness by itself is never a cascade reason.
The implementation must replace the current behavior in `TransitiveIdentityMutabilityPass`, which treats every abstract
target as mutable, and must carry the resulting derivation-local abstract mutability into candidate action assignment.

With this closure, the current static stock-schema reconstruction has a deliberately cheap expected path:

| Schema | Mutable document-reference candidates | Cycle | Duplicate-reachability pair |
|---|---:|---|---|
| Data Standard 5.2 | 22 | None | None |
| Data Standard 5.2 + TPDM | 23 | None | None |

DMS-1258 must reproduce these counts from implemented v2 candidates and prove that both schemas return from the
all-native SQL Server fast path without entering backtracking.

Before implementation begins, update the DMS-1258 delivery description to this contract. It must require the
provider-independent shared-receiver validation, on-demand stable-order origin-aware carrier DFS, native-first
first-feasible search, and the effort accounting in Section 6. Remove requirements for immediate-target-only carriers,
precomputed carrier routes, fewest-prunes optimization, and the superseded work-unit categories. DMS-1258 is not an
alternate algorithm specification; this document is authoritative.

### Shared canonical receiver value-flow validation

SQL Server topology rules and immediate FK enforcement are separate constraints. Independent parents with disjoint
cascade ancestry are topology-legal, but they are not value-flow-safe when their composite FKs share a writable receiver
column. For example, if `B(X, BId)` and `C(X, CId)` independently cascade into `A(X, BId, CId)`, changing only `B.X`
updates `A.X` and immediately invalidates `A`'s unchanged full FK to `C`.

Microsoft documents the single-path cascade-tree restriction separately from the referential actions that update or
reject changes: [MSSQLSERVER_1785](https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/mssqlserver-1785-database-engine-error?view=sql-server-ver17)
and [Primary and foreign key constraints](https://learn.microsoft.com/en-us/sql/relational-databases/tables/primary-and-foreign-key-constraints?view=sql-server-ver17).

An `InitiatingOriginFact` is the tuple `(directly mutable root table, ordered root propagation key, root-row correlation,
statement boundary)` seeded by one directly authorized identity mutation. `Root-row correlation` is structural: paths
share it only when canonical metadata proves they originate at the same physical root row, using the same stable
`DocumentId` or the same complete declared row key. Equal current values, the same root table/key shape, and the same
statement do not prove the same fact.

The fact is shared derivation-local input to value-flow and covered-edge validation. Route state composes the fact's root
columns, root-row correlation, and presence atoms through native edges. Validation is quantified independently over every
possible fact; it must not substitute a common upstream fact for a target's own fact when that target is both directly and
transitively mutable. A target's directly authorized identity update therefore contributes its own fact even when an
upstream fact can also reach it.

After canonical storage mapping and effective mutability are known, group mutable physical FK candidates by writable
receiver column. For each group with more than one candidate, enumerate every possible `InitiatingOriginFact` that can
reach any candidate in the group. The group is valid only when derivation proves, for every such fact, that all writers:

1. are reached from the same physical root row under that fact;
2. compose the same root storage column through their native routes into the grouped receiver column; and
3. propagate to the receiver as part of the same initiating database statement.

Derive these facts from the same identity-mutability graph. Native cascades preserve an `InitiatingOriginFact`, while
trigger maintenance starts a new statement boundary. For every fact that can reach any candidate in the shared-column
group, every candidate must have a native route from the same correlated root row carrying the same root storage column to
the receiver under that fact. A missing candidate route, different root-row correlation, different composed source-column
mapping, different root key, or crossed statement boundary fails validation. Do not reuse SQL Server's backtracking
classifier for this check.

Same fact and statement are insufficient without source-column equality. For example, let `R(P, Q, DocumentId)` be a
mutable root and let one receiver hold `FK1 (X, Y, D1) -> R(P, Q, DocumentId)` and
`FK2 (Z, X, D2) -> R(P, Q, DocumentId)`. Both writers can share one fact and statement, but `FK1` maps `P` to `X` while
`FK2` maps `Q` to `X`. When `P = Q` initially and only `P` changes, no final `X` can satisfy both FKs. This mapping fails
provider-independent validation before PostgreSQL action assignment or SQL Server selection.

The same root table, source column, and statement are also insufficient without root-row correlation. Let
`R(P, Q, DocumentId)` contain two rows whose `P` values are equal but whose `Q` and `DocumentId` values differ. If one
receiver path derives `X` from the first row and another derives the same `X` from the second, changing only the first
row's `P` updates one path and immediately invalidates the other full FK. Those paths have distinct
`InitiatingOriginFact` values and fail validation even though both compose `R.P` to `X`.

Two paths do not share an `InitiatingOriginFact` merely because they have equal current values, the same root resource,
logical ancestry, or naming. A target with its own supported identity update contributes its own fact, even if it is also
transitively mutable from a common upstream target. Concrete-to-abstract identity maintenance does not satisfy the
same-statement rule: the abstract table is updated by an `AFTER` trigger in a later DML statement. Apply the validation to
concrete/concrete, concrete/abstract, and abstract/abstract candidate pairs alike.

The initial implementation fails the mapping as `ConflictingUnifiedCascadeWritesNotSupported`; it does not split a
previously unified column or defer constraint checking. This validation is provider-independent and runs before the
PostgreSQL fixed assignment and before SQL Server's all-native fast path. It does not reject independent parents whose
FKs write disjoint receiver columns.

### PostgreSQL fixed actions

PostgreSQL does not execute the SQL Server classifier or search.

For each physical document-reference candidate:

- use full-vector `ON UPDATE CASCADE` when the target is mutable under the effective-schema closure; and
- use full-vector `ON UPDATE NO ACTION` when the target is genuinely immutable, whether concrete or abstract.

These actions are assigned mechanically from target mutability. Multiple cascade paths are retained. Semantic identity
cycles and unsafe shared-column writers have already failed provider-independent validation. PostgreSQL receives no SQL
Server mode or carrier witness, and a SQL-Server-incompatible physical graph does not fail a PostgreSQL build.

Other provider-independent failures remain possible, including an unrepresentable vector, invalid storage mapping,
mismatched vector arity, or ambiguous canonical-column mapping. Those are model failures, not SQL Server multiple-path
classification.

## 5. SQL Server Graph Legality and Structural Carriers

### Physical cascade graph

Build a directed multigraph with one vertex per physical table. Include every physical FK that can have
`ON UPDATE CASCADE`, oriented from referenced target to referencing receiver:

- a mutable document-reference candidate is a decision edge; and
- any other physical FK already required to cascade on update is a fixed edge.

Parallel candidates remain parallel edges. Fixed edges participate in cycle validation, duplicate-reachability legality,
conflict-core derivation, and carrier reachability. They are never silently ignored or converted to decision edges.
Every graph edge exposes its ordered target/local FK column pairs. A fixed edge may serve as part of a carrier only when
derivation can also establish the required row-correlation and presence metadata; otherwise it remains in legality checks
but is ineligible during origin-aware carrier traversal.

For SQL Server, topologically sort the all-native physical cascade graph immediately after candidate deduplication. If the
sort cannot consume every vertex, fail as `SqlServerCascadeCycleNotSupported`. This is the same stable-order topological
pass later reused for path counting; there is no cycle-cut search, carrier proof, or separate physical-cycle subsystem.
The retained graph of fixed edges plus `NativeCascade` decision edges must additionally contain at most one directed path
between every ordered pair of tables.

This physical check is SQL Server-specific. The semantic identity graph rejected before vector recursion contains only
identity-contributing references. The physical graph also contains mutable document-reference candidates whose local
reference does not contribute to the receiver's identity. Two resources can therefore have valid mutual non-identity
references while producing a SQL Server-prohibited pair of physical `ON UPDATE CASCADE` edges. MetaEd's semantic check
cannot establish SQL Server physical acyclicity, and PostgreSQL is not rejected solely for that topology.

Raw incoming-edge count greater than one is not a multiple-path test. Independent parents with disjoint cascade ancestry
are topology-legal. They reach this classifier only if the provider-independent shared-receiver validation has also
proved their value flows safe; independent parents that write the same canonical receiver column have already failed.

### Finite origin-aware structural carrier relation

For a mutable candidate edge `covered` from referenced target `S` to receiver row `R`, enumerate every
`InitiatingOriginFact` whose native flow can update at least one column of `S`'s propagation key. For each fact, identify a
retained native **source-update route** from that fact's root row to `S`; the route is zero-hop when `S` itself is directly
mutable. Then find a retained native **carrier route** from the same fact and correlated root row to `R`, excluding
`covered`. The edge is coverable only when every possible source-update flow has such a carrier. This admits a sibling
diamond such as `Root -> B -> Receiver` and `Root -> C -> Receiver`, while a direct identity update of `C` still requires
an alternate route beginning at `C`.

An origin-aware carrier is eligible only when all of these structural checks pass:

1. **Same initiating fact and root row.** The source-update and carrier routes start from the same
   `InitiatingOriginFact`, including identical root-row correlation. Common root table/key shape or equal current values is
   insufficient.
2. **Same receiver row.** Compose the source-update route with `covered`. The carrier must terminate at the same physical
   receiver table and row, with identical correlation through the same stable `DocumentId` or complete declared row key.
3. **Identical affected-vector mapping.** Compose the origin columns through the source-update route and then `covered`.
   Project to the columns of `S`'s target vector that the fact can change. The carrier's origin-to-receiver composition
   must equal that projected mapping exactly; no affected receiver column may have a competing source. Columns of `S`'s
   vector that the fact cannot change need no redundant write. For a directly mutable `S`, the fact includes every
   independently writable primitive component and every atomic reference vector, so this equality covers their arbitrary
   supported combinations.
4. **Presence implication.** Candidate derivation normalizes presence to a conjunction of canonical structural atoms:
   physical-row presence identified by its correlation key and optional-reference presence identified by its stored
   presence column. Required references add no atom. For every logical site retained on deduplicated `covered`, the union
   of source-update and carrier route atoms must be a subset of the covered site's atoms. This finite set-containment rule
   is the only implication rule; an implication requiring Boolean inference is not a carrier.
5. **Native same-statement propagation.** Every edge on both routes is either a fixed native cascade or a
   `NativeCascade` decision edge under the fact's statement boundary. A trigger or later DML statement starts a different
   fact and never supplies a carrier for the earlier statement.

Candidate derivation records origin facts, ordered column mappings, row-correlation keys, and canonical presence atoms, so
these checks are column composition, equality, and finite set-membership operations. The classifier does not construct
mutation powersets, symbolic value formulas, or subset-composition proofs. One origin-column mapping covers arbitrary
primitive subsets, and a direct reference replacement contributes its atomic complete-vector mapping. Those mutation
forms remain required behavioral tests, not solver input.

Shared-receiver validation follows the column-sensitive origin-flow provenance derived from the acyclic semantic
identity-mutation graph; it does not use SQL Server's table-only physical topology as a provider-independent reachability
graph. This keeps the validation finite for PostgreSQL mappings whose physical FK topology would be rejected by SQL
Server. A non-identity reference candidate can terminate an origin flow at its receiver, but because its local columns do
not change that receiver's propagation key, it cannot extend the flow around a physical table cycle.

Do not enumerate or retain carrier-route arrays. When validating a `CoveredNoAction` edge, traverse source-update and
carrier routes on demand in stable order against the current retained graph, excluding the covered edge from the carrier.
Compose origin-column mapping, row correlation, and presence atoms in reusable scratch state, and restore that state when
backtracking over an edge. Traversal is prefix-state-sensitive: a vertex reached through another prefix with a different
composed mapping, row correlation, or presence-atom set must be visited and explored again. Do not use a global
visited-vertex set or vertex-only memoization. Stop at the first carrier for each source-update flow; discard scratch routes
after retaining only the final diagnostic witness. A graph can have exponentially many routes, so route lists are not
derivation artifacts.

Reachability alone, common table ancestry, equality of old values, or success for one populated example does not satisfy
the relation.

## 6. Deterministic Bounded Global Selection

Every SQL Server physical document-reference FK receives one final mode:

| Mode | Action | Meaning |
|---|---|---|
| `NativeCascade` | `ON UPDATE CASCADE` | The retained edge performs native propagation. |
| `CoveredNoAction` | `ON UPDATE NO ACTION` | The mutable edge is pruned and has a retained structural carrier route. |
| `ImmutableNoAction` | `ON UPDATE NO ACTION` | The target is immutable under the effective concrete/abstract closure. |

The SQL Server all-native graph has already been topologically ordered, and provider-independent shared-receiver
value-flow validation has completed. If the graph has no duplicate-reachability pair, accept it immediately; no conflict
core, carrier traversal, or backtracking is required.

Only after the fast path fails, compute the conflict core without enumerating routes. Count all-native paths from each
origin in topological order, capping counts at two. For each conflicting origin/receiver pair, use forward and reverse
reachability to add every decision edge lying on a route between that pair. Fixed edges remain present in the graph but
are not decisions. An illegal pair whose routes contain no decision edge yields `NoSafeSqlServerAssignment` immediately.
Decision edges outside the core remain `NativeCascade`, because pruning cannot create a new path.

Search the conflict core with deterministic bounded DFS/backtracking. Decision edges use stable structural order and each
edge tries `NativeCascade` before `CoveredNoAction`. Stop at the first valid complete assignment. Minimum-prune
optimization is not a correctness requirement and is not part of the initial implementation; add it only if DMS-1277
shows that the first-feasible policy materially harms supported write performance.

For every complete assignment:

1. count paths in the retained DAG from each origin in structural order, capping each receiver count at two; reject the
   vector when any receiver obtains a second path; and
2. for each `CoveredNoAction` edge in structural order, run the on-demand origin-aware carrier traversal against that
   retained graph for every applicable `InitiatingOriginFact` and source-update flow.

Partial assignments may be pruned only when the duplicate-path or missing-carrier result is monotone under every
completion.

The first valid assignment is the deterministic result. Stable edge order and native-first branch order make identical
physical inputs produce identical actions; they do not claim global prune optimality.

Selection is database-only. It neither invokes plan compilation nor duplicates write-plan logic. Plan compilation
consumes the selected actions afterward.

The classifier has a single DMS-owned limit of **1,000,000 work units per derived SQL Server schema**. Charge one unit
before either algorithmic event:

- assigning a mode to one conflict-core decision edge during DFS/backtracking; or
- visiting one directed edge during cycle/topological, path-count, conflict-core, legality, or on-demand carrier DFS.

Column-mapping comparisons, correlation elements, presence atoms, vertex initialization, and scratch-buffer resets do not
define additional work-unit kinds. Their sizes are already bounded by the relational model and provider limits. Reusable
buffers and other allocation reductions are implementation optimizations to adopt when measurements justify them, not
part of the classifier contract. Implementations still must not enumerate or retain carrier-route arrays. The counter is
a deterministic algorithmic effort bound, not a wall-clock promise.

Graph checks, mode traversal, and on-demand carrier checks draw from the same counter in stable structural order. A
fixture that consumes exactly the last unit completes; attempting the next unit yields
`CascadeClassificationComplexityExceeded` and no DDL.

Do not add a general cost model. Add memoization or reachability-state canonicalization only when measured stock,
extension, or adversarial fixtures exceed this bound.

The selector emits no provisional feasible assignment. It distinguishes:

- `NoSafeSqlServerAssignment`: bounded search completed and proved infeasibility; and
- `CascadeClassificationComplexityExceeded`: the deterministic work bound was reached before the final selected
  assignment or infeasibility was proved.

Because the required result is first-feasible rather than minimum-prune, a valid assignment ends the search immediately.

### Concise diagnostics

For each `CoveredNoAction` edge, keep the smallest auditable carrier witness:

- the structural pruned FK;
- the applicable `InitiatingOriginFact` and source-update route;
- the selected ordered native carrier route;
- the common correlated root row;
- the common receiver-row correlation key;
- the identical affected-vector column mapping; and
- the presence relationship.

A failure witness names the tables, columns, candidates, applicable `InitiatingOriginFact`, source-update flow, and first
failed structural check in deterministic order. Exhaustive proof trees, omission proofs, canonical hash protocols, and
solver-state serialization are not public contracts.

## 7. Semantic Identity Cycles and SQL Server Physical Cycles

MetaEd does not currently enforce the required recursive-identity prohibition, and the existing ODS relational cascade
enhancer has no visited/cycle guard. The first and only required METAED-1667 delivery is deterministic identity-cycle
validation before that enhancer runs; otherwise a reachable cycle can make it follow references indefinitely. The
validator rejects self-loops and directed cycles with one stable-order cycle witness. The ticket must describe this as a
new prohibition, not preservation of an existing validator. A semantic-diamond warning is outside this delivery; if it
is still wanted, create a separate explicit follow-up.

DMS validates the same invariant independently so malformed or hand-built input cannot reach complete-vector recursion.
This is a small stable-order DFS or topological guard over the semantic identity graph, not a general cycle-analysis
subsystem.

Build the semantic identity-reference graph with one vertex per resource identity and an edge from a referenced identity
to the identity that depends on it. Reject every self-loop and directed cycle as
`IdentityCascadeCycleNotSupported`. The diagnostic names one deterministic semantic cycle in structural order.

Semantic validation is provider-independent. It does not prove that SQL Server's broader physical update-cascade graph is
acyclic: that graph also contains mutable non-identity reference sites and any other fixed physical update cascades. As
part of normal SQL Server legality analysis, topologically sort the all-native physical graph. An incomplete sort fails as
`SqlServerCascadeCycleNotSupported`. PostgreSQL performs no corresponding physical-topology rejection. SQL Server never
searches for a cycle cut, and the runtime never predicts or defers resolution of a future identity. Every submitted
reference must resolve normally before the write.

The reciprocal provider POC has been removed from the normal integration projects. A concise
[historical observation](../evidence/dms-1129/reciprocal-cycle-poc.md) is retained; production fixtures must prove
deterministic semantic rejection on both providers and deterministic physical-cycle failure on SQL Server.

## 8. Errors and Minimal Public Contracts

### Final relational model

Each finalized document-reference FK in the generic relational/runtime model contains:

- ordered local and target columns;
- final `OnDelete` and `OnUpdate` actions.

DDL consumes these values and never reruns classification.

`NativeCascade`, `CoveredNoAction`, and `ImmutableNoAction` are derivation-local classifier labels. They are not fields on
`TableConstraint.ForeignKey` or `MappingSet`. A manifest may add a mode and concise carrier witness only when a concrete
diagnostic consumer requires them.

### Failure convention

Keep the repository's exception-based model-derivation convention. Use a small set of typed categories:

- `IdentityCascadeCycleNotSupported`;
- `SqlServerCascadeCycleNotSupported`;
- `ConflictingUnifiedCascadeWritesNotSupported`;
- `PropagationVectorNotRepresentable`;
- `PhysicalForeignKeyCandidateConflict`;
- `NoSafeSqlServerAssignment`;
- `CascadeClassificationComplexityExceeded`.

Each error carries a concise structural witness. Do not replace all builder results with a new global success/proof
artifact.

### Runtime mappings and manifests

The relational model carries final FK columns/actions. Runtime mappings carry one explicit target anchor-read record per
referenced document target and each reference site's positionally aligned local anchor columns. SQL Server modes and
witnesses stay derivation-local unless a concrete manifest diagnostic consumer is established. Runtime plans do not carry
repeated target lineage paths, classifier modes, solver state, exhaustive certificates, semantic hashes, proof
identifiers, or cycle-specific metadata.

MetaEd owns implementing the authored identity-cycle prohibition before its ODS relational enhancer. METAED-1667 does
not include semantic-diamond analysis. MetaEd does not classify SQL Server realizability, search mode vectors, prove
carriers, or report DMS work-limit outcomes. DMS retains its independent semantic validation and is the sole blocking
authority after canonical storage mapping; it owns shared-receiver value-flow validation, physical candidate
deduplication, SQL Server physical-topology legality, selected SQL Server actions, and provider provisioning.

## 9. Verification and Delivery

### Required checked-in fixtures

Storage/candidate fixtures cover primitive identities, one and multiple reference-backed identities, abstract-member and
identity-alias mappings, optional presence, child/collection/extension sites, unified columns, physical deduplication,
parallel candidates, semantic-role provenance collapsed into one physical candidate, and provider-limit failures.

Behavioral mutation fixtures cover every primitive component and non-empty subset, each reference retarget, multiple
retargets, primitive plus retargets, and all writable groups together. These are matrix tests of the whole-vector rule,
not classifier search cases.

SQL Server fixtures cover the stock all-native fast path, sole edges, independent parents with disjoint receiver storage,
covered/uncovered direct-versus-indirect and sibling diamonds, overlapping diamonds, parallel edges, fixed cascading
edges, optional-carrier failure, a directly mutable sibling that prevents coverage, no solution, deterministic work-limit
exhaustion, reversed-input determinism, and a physical cycle formed by otherwise-valid mutual non-identity references.
The physical-cycle fixture fails from the normal topological legality pass as `SqlServerCascadeCycleNotSupported` and is
not sent through cycle-cut search. Include a prefix-state-sensitive carrier fixture in which two structural
prefixes converge on the same intermediate table with different composed mapping, correlation, or presence state and only
the later prefix yields an eligible carrier. Provider-independent fixtures cover conflicting unified writes:
independently mutable parents sharing one receiver column, including a concrete/abstract pair, fail as
`ConflictingUnifiedCascadeWritesNotSupported` before either provider assigns actions or SQL Server takes the fast path;
same-`InitiatingOriginFact`, same-root-row, same-source-column, same-statement writers remain valid. Include both the
crossed-key-position case above and the same-root-table/source-column but different-root-row case to prove they fail.

Provider/API fixtures cover errors 1785 and 547 for diamonds, ordinary reference-resolution failure, true retarget, stale
version, rollback, hidden-profile-state preservation, stamping, referential-identity maintenance, change queries, and final
constraint validation. Provider-independent model fixtures cover deterministic self-loop and multi-entity cycle rejection.

### Delivery slices

1. **Database evidence and static feasibility:** stock-schema vector measurements and maximum-value probes.
2. **Complete vectors and candidates:** lineage inventory, storage, propagation keys, physical deduplication, limits, and
   an early representative physical row/index and write-amplification gate before the `v2` storage shape is frozen.
3. **Provider actions:** PostgreSQL fixed assignment and SQL Server physical-cycle legality plus bounded global diamond
   selection.
4. **Runtime mapping and manifest integration:** final actions, target anchor-read records, and aligned local anchor
   columns only.
5. **Full-schema qualification:** stock, TPDM, extension, and adversarial generated DDL; widest-count provider coverage;
   exhaustive row and target-unique/FK-supporting index sizes at representative row counts; reference-resolution round
   trips; concurrency; and write/cascade timing. This expands and confirms the early slice-2 physical-storage gate.

All slices are required before the finalized `v2` relational contract is complete.

### Current evidence gates

| Gate | Current result | Consequence |
|---|---|---|
| Complete vector measured screen | Pass for the transitive DS 5.2 and TPDM key/column screens; the focused provider probe covers the nine-column widest-byte case, not the 27-column widest-count case | Do not implement per-site minimal anchor closure; require the early DMS-1274 representative physical-storage gate before freezing `v2`, then retain DMS-1277 full-schema row/index and widest-count qualification. |
| Cycle boundary | Semantic identity cycles are unsupported on both providers; SQL Server physical cascade cycles fail its legality pass | Keep MetaEd plus the small DMS semantic guard before vector recursion. Reuse SQL Server's required topological ordering to report physical cycles; add no physical-cycle rejection to PostgreSQL and no cycle-cut/runtime protocol. |
| Stock all-native topology | Static reconstruction reports 22/23 candidates and no conflicts for DS 5.2/TPDM | Reproduce from implemented v2 candidates and return before conflict-core search. |
| Simple global search | Design-ready; implementation measurement open | Use on-demand origin-aware carrier checks and the 1,000,000-unit budget counting only decision assignments and edge visits; add minimum-prune optimization only from performance evidence. |
| Minimal artifact contract | Design constraint; implementation validation pending | Carry final actions, target anchor-read records, and aligned local anchor columns; do not add classifier/proof/hash protocols. |

## Relationship to Legacy ODS

Legacy MetaEd/ODS orients edges from referenced entity to identity-dependent entity, treats raw incoming-edge count as a
multiple-path test, and retains an alphabetically selected source while disabling others. Its “cyclical reference graph”
test is a converging diamond rather than a directed cycle.

DMS does not copy that algorithm. It rejects genuine semantic identity cycles, reports SQL Server physical cycles through
the normal topological legality pass, constructs storage-mapped physical candidates,
rejects unsafe shared-column writers independently of topology, distinguishes safe independent parents from duplicate
reachability, validates both direct-versus-indirect and sibling diamonds against every initiating root-row flow, searches
diamond action assignments globally, and fails SQL Server derivation instead of silently weakening referential integrity.
