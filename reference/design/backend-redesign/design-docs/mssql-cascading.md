# SQL Server Foreign-Key Pruning and Identity Propagation

## Status

This document is the authoritative DMS-1129 design after the simplification reset. It defines the smallest known
architecture that preserves full referential integrity, correct SQL Server diamond handling, complete identity-change
support, and the provider boundary between SQL Server and PostgreSQL. Genuine identity cycles are unsupported.

The corrected transitive complete-vector measured screen has executable/reproducible evidence. Implementation and
full-schema qualification remain open. The earlier reciprocal-cycle database POC is recorded as historical evidence only;
cycle execution is not part of the supported contract.

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
3. Identity cycles fail provider-independent validation before vector derivation or provider action assignment. DMS does
   not cut or execute them.
4. PostgreSQL receives fixed actions mechanically. PostgreSQL is never pruned or topology-classified for multiple paths.
5. SQL Server alone classifies the physical cascade graph for error-1785 duplicate reachability, selects covered
   `NO ACTION` edges, and fails before DDL when no safe diamond assignment exists.
6. SQL Server selection is global and may backtrack. Local first-fit pruning is not correct for overlapping diamonds or
   parallel conflicts.
7. Every covered `NO ACTION` edge has a structural carrier with the same physical mutation origin, receiver row, and
   complete-vector mapping; covered-edge presence implies carrier presence, and the route propagates natively in the
   same SQL statement.
8. The runtime covers every independently writable primitive component, every non-empty primitive subset, one or more
   reference-backed replacements, multiple reference replacements, and mixed primitive/reference changes.
9. The finalized relational model, not the DDL emitter, owns the chosen FK actions.

### Non-goals

- Success for arbitrary direct SQL identity changes outside DMS-authorized writes.
- A general graph theorem-prover API, serialized derivation trace, or universal semantic-hash protocol.
- Per-site propagation-vector minimization unless a measured supported-provider limit requires it.
- A generic missing-reference fallback or predictive reference-resolution language.
- A cross-repository requirement that MetaEd reproduce DMS physical FK identities, selected actions, or carrier
  witnesses.
- Mapping-pack changes before an implemented runtime consumer identifies the minimal additional fields it needs.
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

The worst SQL Server and PostgreSQL vectors install, accept maximum-size test values, and cascade a maximum-width public
value plus a lineage anchor. Complete anchors create no new crossing of the measured key/index column, declared-key
payload, or table-column screens. This evidence does not measure total SQL Server row width or PostgreSQL tuple/index
overhead, and it does not replace full generated-schema DDL qualification. It is sufficient for the architecture
choice, so site-minimal anchor closure is not part of the supported contract.

Mapping-pack size cannot yet be measured because the current pack payload is a stub. The conservative relational-manifest
projection grows by 3.25/3.64 percent and generated SQL by 1.26/1.43 percent for DS 5.2/TPDM. Exact pack measurement
belongs to the slice that implements a real pack consumer.

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
3. for each target group, batch-read the ordered lineage-anchor columns from that target's concrete root or abstract
   identity table by the resolved `DocumentId` values, using the same transaction; and
4. attach that ordered anchor tuple to every resolved occurrence for the row materializer.

A target with no lineage anchors needs no second read. A missing target row, duplicate row, wrong target type, vector
arity mismatch, or null required anchor fails reference resolution. The request still supplies public values and the full
FK remains the final correlation check. This is the ordinary value source for all anchor-bearing POST and PUT writes; it
is not deferred-resolution metadata, predictive SQL, or a mutation-case protocol.

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
 delete action,
 semantic FK kind when otherwise-identical roles must remain distinct)
```

`OnUpdate`, generated constraint name, logical JSON path, and SQL Server mode are not part of candidate identity.
Identical physical candidates are deduplicated while retaining every contributing logical site's provenance and presence
predicate. Presence is normalized to the physical-row and optional-reference atoms used by the structural carrier
relation. Distinct parallel candidates remain distinct edges.

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
-> physical identity-cascade cycle validation
-> PostgreSQL fixed actions OR SQL Server global selection
-> finalized TableConstraint.ForeignKey values
-> naming, shortening, manifests, and DDL
```

## 4. PostgreSQL Fixed Actions

PostgreSQL does not execute the SQL Server classifier or search.

For each physical document-reference candidate:

- use full-vector `ON UPDATE CASCADE` when the target is abstract or transitively permits identity changes; and
- use full-vector `ON UPDATE NO ACTION` when the concrete target is genuinely immutable.

These actions are assigned mechanically from target mutability. Multiple cascade paths are retained. Identity cycles have
already failed provider-independent validation. PostgreSQL receives no SQL Server mode or carrier witness, and a
SQL-Server-incompatible multiple-path graph does not fail a PostgreSQL build.

Provider-independent failures remain possible, including an identity cycle, unrepresentable vector, invalid storage
mapping, mismatched vector arity, or ambiguous canonical-column mapping. Those are model failures, not SQL Server
multiple-path classification.

## 5. SQL Server Graph Legality and Structural Carriers

### Physical cascade graph

Build a directed multigraph with one vertex per physical table and one edge per mutable physical candidate, oriented
from referenced target to referencing receiver. Parallel candidates remain parallel edges.

Provider-independent validation guarantees the input identity-cascade graph is acyclic. The retained `NativeCascade`
subgraph must additionally contain at most one directed path between every ordered pair of tables.

Raw incoming-edge count greater than one is not a multiple-path test. Independent parents with disjoint cascade ancestry
are legal.

### Finite structural carrier relation

For a mutable candidate edge `covered` from physical origin table/key `O` to receiver table/row `R`, a carrier is a
directed path from `O` to `R` through other mutable physical candidates. The route is eligible only when all of these
structural checks pass:

1. **Same physical mutation origin.** The route starts at the same target table and ordered target propagation-key
   columns as `covered`. Table reachability alone is insufficient.
2. **Same receiver row.** The route terminates at the same physical receiver table and the composed row-correlation
   mapping is identical to `covered`'s declared receiver-row identity. Correlation must use the same stable `DocumentId`
   or the same complete declared row key; common ancestry is insufficient.
3. **Identical complete-vector mapping.** Compose each path edge's ordered target-to-local column pairs and project the
   composition to the columns originating in `covered`'s target vector. The result must equal `covered`'s complete
   ordered target-to-receiver column mapping exactly, including public values, every lineage anchor, and terminal target
   `DocumentId`; no covered receiver column may have a competing source. A route may carry unrelated columns in addition
   to this identical mapping.
4. **Presence implication.** Candidate derivation normalizes presence to a conjunction of canonical structural atoms:
   physical-row presence identified by its correlation key and optional-reference presence identified by its stored
   presence column. Required references add no atom. For every logical site retained on the deduplicated `covered`
   candidate, the union of route atoms must be a subset of the covered site's atoms. This finite set-containment rule is
   the only implication rule; an implication requiring Boolean inference is not a carrier.
5. **Native same-statement propagation.** Every edge on the selected route is `NativeCascade`. SQL Server therefore
   performs the complete write in the initiating statement. A trigger or later DML statement is never a carrier.

Candidate derivation records ordered column mappings, receiver-row correlation keys, and canonical presence atoms, so
these checks are equality and finite set-membership operations. The classifier does not construct mutation powersets,
symbolic origins, value formulas, or subset-composition proofs. Whole-vector mapping equality covers every primitive
subset, and atomic reference-vector replacement composes across one or multiple reference changes. Those mutation forms
remain required behavioral tests, not solver input.

Before mode selection, enumerate eligible carrier routes in stable edge order through the all-native acyclic graph.
Store each route as its ordered physical-edge identities. A `CoveredNoAction` edge is valid in a mode vector when at
least one of its precomputed routes contains only edges selected as `NativeCascade`.

Reachability alone, common table ancestry, equality of old values, or success for one populated example does not satisfy
the relation.

## 6. Deterministic Bounded Global Selection

Every SQL Server physical document-reference FK receives one final mode:

| Mode | Action | Meaning |
|---|---|---|
| `NativeCascade` | `ON UPDATE CASCADE` | The retained edge performs native propagation. |
| `CoveredNoAction` | `ON UPDATE NO ACTION` | The mutable edge is pruned and has a retained structural carrier route. |
| `ImmutableNoAction` | `ON UPDATE NO ACTION` | The target has no supported identity mutation origin. |

First compute the conflict core from the all-native graph: the decision set is the union of physical edges that
participate in two distinct paths from one origin table to one receiver table, including parallel edges. Edges outside
that set are fixed `NativeCascade`, because removing paths cannot create a new duplicate path.

The selector then uses deterministic bounded iterative-deepening DFS/backtracking. Decision edges use stable structural
order. For `coveredCount = 0..N`, enumerate mode vectors containing exactly that many `CoveredNoAction` values in
lexicographic structural-edge order, with `NativeCascade` ordered before `CoveredNoAction`. Stop at the first valid
complete assignment.

For every complete assignment:

1. count paths in the retained DAG from each origin in structural order, capping each receiver count at two; reject the
   vector when any receiver obtains a second path; and
2. for each `CoveredNoAction` edge in structural order, require at least one precomputed eligible carrier route whose
   edges are all `NativeCascade`.

Partial assignments may be pruned only when the duplicate-path or missing-carrier result is monotone under every
completion.

This traversal makes the first valid assignment exactly the required objective:

1. fewest `CoveredNoAction` edges;
2. lexicographically smallest structural edge-order mode vector.

Selection is database-only. It neither invokes plan compilation nor duplicates write-plan logic. Plan compilation
consumes the selected actions afterward.

The classifier has a single DMS-owned limit of **1,000,000 work units per derived SQL Server schema**. One unit is
charged for each of these deterministic operations:

- extending carrier-route DFS by one physical edge;
- assigning one decision edge during mode-vector DFS;
- examining one physical edge while deriving the all-native conflict core;
- examining one retained edge during duplicate-path validation; or
- examining one precomputed carrier route while validating a covered edge.

Charge the unit before performing the operation. Route enumeration, mode traversal, graph checks, and carrier checks all
draw from the same counter in stable structural order. The bound is independent of wall-clock time, machine speed, and
thread scheduling. A fixture that consumes exactly the last unit completes; attempting the next unit yields
`CascadeClassificationComplexityExceeded` and no DDL.

Do not add a general cost model. Add memoization or reachability-state canonicalization only when measured stock,
extension, or adversarial fixtures exceed this bound.

The selector emits no provisional feasible assignment. It distinguishes:

- `NoSafeSqlServerAssignment`: bounded search completed and proved infeasibility; and
- `CascadeClassificationComplexityExceeded`: the deterministic work bound was reached before the final selected
  assignment or infeasibility was proved.

Because a valid assignment ends its exact-cardinality/lexicographic iteration immediately, there is no state in which a
feasible assignment exists but its required optimum remains unproved.

### Concise diagnostics

For each `CoveredNoAction` edge, keep the smallest auditable carrier witness:

- the structural pruned FK;
- the selected ordered native carrier route;
- the common physical origin key;
- the common receiver-row correlation key;
- the identical complete-vector column mapping; and
- the presence relationship.

A failure witness names the tables, columns, candidates, physical origin, and first failed structural check in deterministic
order. Exhaustive proof trees, omission proofs, canonical hash protocols, and solver-state serialization are not public
contracts.

## 7. Identity Cycles Are Unsupported

The supported MetaEd contract contains no recursive identity definitions. DMS validates that invariant independently so
malformed, hand-built, or mapping-pack-loaded input cannot reach complete-vector recursion or provider action assignment.

Build the semantic identity-reference graph with one vertex per resource identity and an edge from a referenced identity
to the identity that depends on it. Reject every self-loop and directed cycle as
`IdentityCascadeCycleNotSupported`. After canonical storage mapping and physical-candidate deduplication, apply the same
check to the physical cascade graph so table collapse cannot reintroduce a cycle. The diagnostic identifies the validation
stage and names one deterministic cycle in structural order.

This validation is provider-independent. PostgreSQL's ability to install some physical cascade cycles does not expand the
DMS model. SQL Server never searches for a cycle cut, and the runtime never predicts or defers resolution of a future
identity. Every submitted reference must resolve normally before the write.

The reciprocal provider POC has been removed from the normal integration projects. A concise
[historical observation](../evidence/dms-1129/reciprocal-cycle-poc.md) is retained; production fixtures must prove
deterministic semantic and physical cycle rejection.

## 8. Errors and Minimal Public Contracts

### Final relational model

Each finalized document-reference FK in the generic relational/runtime model contains:

- ordered local and target columns;
- final `OnDelete` and `OnUpdate` actions.

DDL consumes these values and never reruns classification.

`NativeCascade`, `CoveredNoAction`, and `ImmutableNoAction` are derivation-local classifier labels. They are not fields on
`TableConstraint.ForeignKey`, `MappingSet`, or the mapping pack. A manifest may add a mode and concise carrier witness
only when a concrete diagnostic consumer requires them.

### Failure convention

Keep the repository's exception-based model-derivation convention. Use a small set of typed categories:

- `IdentityCascadeCycleNotSupported`;
- `PropagationVectorNotRepresentable`;
- `PhysicalForeignKeyCandidateConflict`;
- `NoSafeSqlServerAssignment`;
- `CascadeClassificationComplexityExceeded`.

Each error carries a concise structural witness. Do not replace all builder results with a new global success/proof
artifact.

### Manifests, AOT, and packs

The relational model and mapping pack carry final FK columns/actions. SQL Server modes and witnesses stay
derivation-local unless a concrete manifest diagnostic consumer is established.

Mapping packs also carry each reference site's lineage-anchor bindings so `MappingSet.FromPayload` can reconstruct the
runtime projection. They do not serialize classifier modes, solver state, exhaustive certificates, semantic hashes, or
proof identifiers. Verify runtime/AOT semantic equivalence without adding cycle-specific metadata.

MetaEd owns the authored identity-cycle prohibition. It may emit an early, non-blocking warning for a semantic diamond,
but it does not classify SQL Server realizability, search mode vectors, prove carriers, or report DMS work-limit outcomes.
DMS is the sole blocking authority after canonical storage mapping and owns physical candidate deduplication, physical
cycle validation, selected SQL Server actions, and provider provisioning.

## 9. Verification and Delivery

### Required checked-in fixtures

Storage/candidate fixtures cover primitive identities, one and multiple reference-backed identities, abstract-member and
identity-alias mappings, optional presence, child/collection/extension sites, unified columns, physical deduplication,
parallel candidates, and provider-limit failures.

Behavioral mutation fixtures cover every primitive component and non-empty subset, each reference retarget, multiple
retargets, primitive plus retargets, and all writable groups together. These are matrix tests of the whole-vector rule,
not classifier search cases.

SQL Server fixtures cover sole edges, independent parents, covered/uncovered and overlapping diamonds, parallel edges,
optional-carrier failure, conflicting unified writes, no solution, deterministic work-limit exhaustion, and reversed-input
determinism.

Provider/API fixtures cover errors 1785 and 547 for diamonds, ordinary reference-resolution failure, true retarget, stale
version, rollback, hidden-profile-state preservation, stamping, referential-identity maintenance, change queries, and final
constraint validation. Provider-independent model fixtures cover deterministic self-loop and multi-entity cycle rejection.

### Delivery slices

1. **Database evidence and static feasibility:** stock-schema vector measurements and maximum-value probes.
2. **Complete vectors and candidates:** lineage inventory, storage, propagation keys, physical deduplication, and limits.
3. **Provider actions:** PostgreSQL fixed assignment and SQL Server bounded global diamond selection.
4. **Manifest/AOT/pack integration:** final actions and ordinary lineage-anchor bindings only.
5. **Full-schema qualification:** stock, TPDM, extension, adversarial, concurrency, and performance evidence.

All slices are required before the finalized `v2` relational contract is complete.

### Current evidence gates

| Gate | Current result | Consequence |
|---|---|---|
| Complete vector measured screen | Pass for the transitive DS 5.2 and TPDM key/column screens, including maximum-value provider probes | Do not implement per-site minimal anchor closure; retain full-schema row/index qualification. |
| Identity-cycle boundary | Settled: unsupported | Reject deterministically before vector recursion or provider action assignment; add no cycle runtime protocol. |
| Simple global search | Design-ready; implementation measurement open | Use the finite structural carrier relation and 1,000,000-unit deterministic budget; add optimization only from evidence. |
| Minimal artifact contract | Design constraint; implementation validation pending | Carry final actions and lineage bindings; do not add classifier/proof/hash protocols. |

## Relationship to Legacy ODS

Legacy MetaEd/ODS orients edges from referenced entity to identity-dependent entity, treats raw incoming-edge count as a
multiple-path test, and retains an alphabetically selected source while disabling others. Its “cyclical reference graph”
test is a converging diamond rather than a directed cycle.

DMS does not copy that algorithm. It rejects genuine identity cycles, constructs storage-mapped physical candidates,
distinguishes independent parents from duplicate reachability, searches diamond action assignments globally, and fails
SQL Server derivation instead of silently weakening referential integrity.
