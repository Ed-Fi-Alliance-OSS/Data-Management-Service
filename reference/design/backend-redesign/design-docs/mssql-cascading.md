# SQL Server Foreign-Key Pruning and Identity Propagation

## Status

This document is the authoritative DMS-1129 design after the simplification reset. It defines the smallest known
architecture that preserves full referential integrity, safely breakable SQL Server cycles, complete v1 identity-change
support, and the provider boundary between SQL Server and PostgreSQL.

The direct database cycle and corrected transitive complete-vector measured screen have executable/reproducible evidence.
The actual DMS PUT gate is still open: the current executor has a suitable unprofiled transaction seam, but no accepted
cycle has yet executed through the normal DMS API and the profile path still rejects the pre-write miss. Sections that
depend on that gate say so explicitly.

`RelationalMappingVersion` remains `v1`. This is the initial production relational contract; this work adds no mapping
compatibility mode, legacy-schema interpretation, database migration, or version bump.

## 1. Settled Decisions and Non-Goals

### Settled decisions

1. Every document-reference foreign key contains the complete ordered propagation vector. A reduced or
   `DocumentId`-only reference FK is not an alternative.
2. Identity values propagate through native `ON UPDATE CASCADE`. DMS does not use an identity-value propagation trigger.
   Existing triggers for referential-identity, abstract-identity, stamping, and change-query maintenance retain their
   separate responsibilities.
3. PostgreSQL receives fixed actions mechanically. PostgreSQL is never pruned, topology-classified, or failed because
   of cascade topology.
4. SQL Server alone classifies the physical cascade graph for error 1785, selects covered `NO ACTION` edges, and fails
   before DDL when no safe assignment exists.
5. SQL Server selection is global and may backtrack. Local first-fit pruning is not correct for overlapping diamonds,
   parallel edges, or cycles.
6. Cycle membership is not a failure. A cycle is an action-choice problem, and every safely breakable assignment is
   considered within deterministic bounds.
7. Every covered `NO ACTION` edge has an exact same-row, same-value, same-statement-boundary carrier for every applicable
   identity mutation.
8. V1 covers every independently writable primitive component, every non-empty primitive subset, one or more
   reference-backed replacements, multiple reference replacements, and mixed primitive/reference changes.
9. The finalized relational model, not the DDL emitter, owns the chosen FK actions.
10. An assignment that requires an unexecutable future-identity PUT binding is not safe.

### Non-goals

- Success for arbitrary direct SQL identity changes outside DMS-authorized writes.
- A general graph theorem-prover API, serialized derivation trace, or universal semantic-hash protocol.
- Per-site propagation-vector minimization unless a measured supported-provider limit requires it.
- A generic missing-reference fallback or predictive reference-resolution language.
- A cross-repository requirement that MetaEd reproduce DMS physical FK identities, selected actions, or carrier
  witnesses.
- Mapping-pack changes before an implemented runtime consumer identifies the minimal additional fields it needs.
- General root-to-child or cross-table equality propagation outside document-reference identity propagation.

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
otherwise each structural lineage path receives dedicated storage. Recursive authored identity definitions that cannot
produce a finite structural union fail provider-independent model validation. Descriptor values are not
document-reference lineages.

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
overhead, and it does not replace full generated-schema DDL qualification. It is sufficient for the v1 architecture
choice, so site-minimal anchor closure is not part of v1.

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
predicate. Distinct parallel candidates remain distinct edges.

Internal equality and ordering use structural records and ordinal comparers. A durable hash is added only if a concrete
artifact consumer later needs correlation outside the derivation process.

The derivation order is:

```text
reference binding
-> key unification and abstract identity
-> transitive identity mutability
-> complete vectors and propagation keys
-> storage-mapped physical candidates and deduplication
-> PostgreSQL fixed actions OR SQL Server global selection
-> finalized TableConstraint.ForeignKey values
-> naming, shortening, manifests, and DDL
```

## 4. PostgreSQL Fixed Actions

PostgreSQL does not execute the SQL Server classifier or search.

For each physical document-reference candidate:

- use full-vector `ON UPDATE CASCADE` when the target is abstract or transitively permits identity changes; and
- use full-vector `ON UPDATE NO ACTION` when the concrete target is genuinely immutable.

These actions are assigned mechanically from target mutability. Cycles and multiple cascade paths are retained.
PostgreSQL receives no SQL Server mode or carrier witness, and a SQL Server-incompatible graph does not fail a PostgreSQL
build.

Provider-independent failures remain possible, including an unrepresentable vector, invalid storage mapping, mismatched
vector arity, or ambiguous canonical-column mapping. Those are model failures, not topology classification.

PostgreSQL may derive the same minimal deferred-PUT runtime marker from its fixed cascade routes. That is executor-plan
derivation, not PostgreSQL pruning, unsafe-graph detection, or topology fail-fast.

## 5. SQL Server Graph Legality and Carrier Safety

### Physical cascade graph

Build a directed multigraph with one vertex per physical table and one edge per mutable physical candidate, oriented
from referenced target to referencing receiver. Parallel candidates remain parallel edges.

The retained `NativeCascade` subgraph must satisfy SQL Server error-1785 legality:

- it contains no directed cycle; and
- there is at most one retained directed path between every ordered pair of tables.

Raw incoming-edge count greater than one is not a multiple-path test. Independent parents with disjoint cascade ancestry
are legal.

### Mutation model

Identity changes are modeled as independently writable groups:

- each independently writable primitive identity component is one group; and
- each reference-backed replacement is one atomic group containing every public value and stable lineage anchor supplied
  by that reference.

A mutation case is any valid non-empty combination of groups. The analysis covers every primitive subset, each
reference replacement, multiple reference replacements, and mixed combinations. It may represent cases symbolically,
but it must not approximate “identity changed” as one indivisible event.

Each symbolic value retains its mutation origin, origin row, component/lineage, and statement boundary. An AFTER trigger
starts a later boundary and cannot repair an FK checked in the initiating statement.

### Exact carrier obligations

For every complete action assignment and applicable mutation case, prove:

1. **Changed target.** Every present reference to a changed target receives the same complete new vector before its FK is
   checked.
2. **Receiver validity.** A write to a unified receiver column leaves every other present FK reading that column valid.
3. **Single value.** Competing writes to one receiver row/column carry the same symbolic value.
4. **Origin-row correlation.** Changed-target and carrier routes originate from the same mutation row.
5. **Receiver-row correlation.** The carrier reaches the exact receiver row using stable `DocumentId` or another complete
   declared row identity.
6. **Presence implication.** Whenever the covered edge is present, a complete carrier is present.
7. **Statement boundary.** The carrier write is visible at the covered FK's constraint-check boundary.
8. **Subset composition.** The proof remains valid for every supported simultaneous group combination.

A direct origin write to the covered receiver row is an explicit zero-hop carrier. Reachability alone, common table
ancestry, equality of old values, or success for one populated example is not a proof.

## 6. Deterministic Bounded Global Selection

Every SQL Server physical document-reference FK receives one final mode:

| Mode | Action | Meaning |
|---|---|---|
| `NativeCascade` | `ON UPDATE CASCADE` | The retained edge performs native propagation. |
| `CoveredNoAction` | `ON UPDATE NO ACTION` | The mutable edge is pruned and every applicable mutation has an exact carrier. |
| `ImmutableNoAction` | `ON UPDATE NO ACTION` | The target has no supported mutation origin; competing receiver writes are still validated. |

The selector uses deterministic bounded iterative-deepening DFS/backtracking. It introduces decision variables only for
mutable candidates participating in an error-1785 conflict or a value-flow choice. Candidates use stable structural
order. For `coveredCount = 0..N`, enumerate mode vectors containing exactly that many `CoveredNoAction` values in
lexicographic structural-edge order, with `NativeCascade` ordered before `CoveredNoAction`. Stop at the first valid
complete assignment.

For every complete assignment, the selector verifies graph legality, every carrier obligation, optional-reference
presence, and unified-column value agreement. Partial assignments that already violate a monotone graph or carrier
obligation are pruned.

This traversal makes the first valid assignment exactly the required objective:

1. fewest `CoveredNoAction` edges;
2. lexicographically smallest structural edge-order mode vector.

Selection is database-only while the deferred-resolution Gate 2 is open. It neither invokes plan compilation nor
duplicates plan eligibility logic. Plan compilation consumes the selected actions afterward. If Gate 2 proves that the
deterministic database-safe winner is not executable while another database-safe assignment is, introduce only a small
pre-selection `DeferredPutEligibility` input derived before selection; do not make the classifier anticipate the write
plan contract.

Do not add a general cost model. Add memoization or reachability-state canonicalization only when measured stock,
extension, or adversarial fixtures exceed the selected work bound.

The selector emits no provisional feasible assignment. It distinguishes:

- `NoSafeSqlServerAssignment`: bounded search completed and proved infeasibility; and
- `CascadeClassificationComplexityExceeded`: the deterministic work bound was reached before the final selected
  assignment or infeasibility was proved.

Because a valid assignment ends its exact-cardinality/lexicographic iteration immediately, there is no state in which a
feasible assignment exists but its required optimum remains unproved.

Cycle membership never directly produces either result.

### Concise diagnostics

For each `CoveredNoAction` edge, keep the smallest auditable carrier witness:

- the structural pruned FK;
- mutation group or grouped cases;
- retained changed-target route;
- receiver carrier route, including explicit zero-hop origin write;
- complete receiver-row correlation key; and
- statement boundary.

A failure witness names the tables, columns, candidates, mutation origin, and first failed obligation in deterministic
order. Exhaustive proof trees, omission proofs, canonical hash protocols, and solver-state serialization are not public
contracts.

## 7. Concrete Safely Breakable Cycle

The minimum non-vacuous fixture has two public primitive components and reciprocal full FKs:

```text
e_BA: CycleA(Key1, Key2, B_DocumentId)
          -> CycleB(Key1, Key2, DocumentId)
       graph edge CycleB -> CycleA

e_AB: CycleB(Key1, Key2, A_DocumentId)
          -> CycleA(Key1, Key2, DocumentId)
       graph edge CycleA -> CycleB
```

`CycleB` permits direct identity updates. `CycleA` has no direct mutation origin and changes only through `e_BA`. The
safe SQL Server assignment is:

| FK | Mode | Action |
|---|---|---|
| `e_BA` | `NativeCascade` | `ON UPDATE CASCADE` |
| `e_AB` | `CoveredNoAction` | `ON UPDATE NO ACTION` |

For reciprocal rows `a` and `b`:

```text
a.B_DocumentId = b.DocumentId
b.A_DocumentId = a.DocumentId
a.(Key1, Key2) = b.(Key1, Key2)
```

a direct `CycleB` update is the zero-hop receiver carrier for `b`, while `e_BA` is the one-hop changed-target route to
`a`. At the statement constraint boundary, both rows contain the same new component values and `e_AB` remains valid.
The same routes prove `{Key1}`, `{Key2}`, and `{Key1, Key2}`.

If `CycleA` also permits direct identity updates, no one-edge cut covers both origins. Pruning `e_AB` leaves a direct
CycleA update uncovered; pruning `e_BA` leaves a direct CycleB update uncovered; pruning both covers neither. The
classifier must return `NoSafeSqlServerAssignment` before DDL.

Executable provider tests are checked in at:

- [`MssqlCascadeCycleProofTests.cs`](../../../../src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlCascadeCycleProofTests.cs)
- [`PostgresqlCascadeCycleProofTests.cs`](../../../../src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlCascadeCycleProofTests.cs)

They prove that:

- SQL Server installs one trusted `CASCADE` and one trusted `NO ACTION` FK;
- SQL Server propagates each primitive subset and `DBCC CHECKCONSTRAINTS` stays clean;
- the unsupported reverse direct update fails with error 547;
- making both SQL Server edges cascade reproduces error 1785; and
- PostgreSQL installs and executes the reciprocal full-cascade version.

This proves the database constraint boundary assumed by the carrier model. It does not substitute for the DMS PUT gate.

## 8. Narrow DMS PUT Deferred Resolution

### Why ordinary pre-write resolution misses

In the accepted cycle, a PUT of `CycleB` still contains its reference to `CycleA`, expressed with CycleA's future public
identity. That referential identity does not exist before the statement; CycleA receives it from the retained cascade.
Normal pre-write lookup therefore misses even though the persisted binding still identifies the same stable CycleA row.

### Required update-only workflow

Normal lookup always wins. After stored-state authorization and current-state loading, an unresolved binding may defer
only when compiled metadata proves all of these facts for either an unprofiled or profile-constrained PUT:

1. the operation is an identity-changing PUT of an existing document;
2. the binding already exists on the persisted receiver row;
3. its persisted target `DocumentId` is non-null and stable;
4. a selected retained cascade route gives that same target the submitted identity in the initiating statement;
5. the receiver row is already correlated by the normal executor's stable row locator; and
6. every full-vector value other than the deferred terminal target id is supplied by a typed ordinary resolved vector,
   a persisted value proved unchanged, or the initiating origin write.

The executor reuses only the persisted target `DocumentId` and persisted lineage anchors proved unchanged. Submitted
future public values and changing lineage anchors from ordinary resolved vectors or proved origin writes flow through the
ordinary merged row. It does not predict values with custom SQL and does not treat an arbitrary lookup miss as an
unchanged reference.

Before resource DML, the executor:

- completes authorization against the stable persisted target;
- locks subject, receiver, and target rows in deterministic stable-id order;
- rejects a new binding, missing persisted binding, ambiguity, stale row, value disagreement, or unsupported mutation;
  and
- uses existing stable collection-row correlation for collection sites; and
- for a profile-constrained PUT, applies the ordinary Core-produced writable-profile shape and stored-state visibility
  context, preserves every hidden value/row, and proves that the deferred binding is visible and writable rather than
  inferring it from the filtered body.

After the identity-changing statement and before commit, the executor reruns the ordinary bulk referential-identity
resolver inside the same transaction. The submitted future identity must resolve uniquely to the same persisted target
`DocumentId`. A miss, ambiguity, different target, failed FK, stale state, or model/runtime mismatch rolls back all work.

POST and true retargets retain ordinary resolution. A true retarget that resolves normally is never converted into a
deferred existing binding.

### Minimal runtime metadata

The compiled plan needs only a small `DeferredExistingReferenceBinding` marker:

- owning resource and binding/site;
- persisted target `DocumentId` source;
- stable persisted receiver-row locator;
- retained cascade route or equivalent eligibility marker; and
- post-statement same-target resolution requirement.

It does not consume SQL Server carrier proof objects, mutation-case identifiers, predictive projections, JSON recordset
protocols, or custom verification SQL.

### Gate status

The current write executor already opens one transaction, performs stored authorization, allows an unprofiled missing
document reference to survive through current-state loading and proposed authorization, and fails it immediately before
persist. That is the intended insertion point for the unprofiled workflow. The profile-constrained path currently rejects
the same miss before that seam; moving only an eligible existing binding through the normal profile merge/authorization
path is required Gate 2 work, not an accepted non-goal.

The audited seams are [`DefaultRelationalWriteExecutor`](../../../../src/dms/backend/EdFi.DataManagementService.Backend/DefaultRelationalWriteExecutor.cs)
and the request-scoped, miss-memoizing [`ReferenceResolver`](../../../../src/dms/backend/EdFi.DataManagementService.Backend/ReferenceResolver.cs).
The current reduced SQL Server FK and propagation-trigger behavior remains in
[`ReferenceConstraintPass`](../../../../src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/ReferenceConstraintPass.cs)
and [`DeriveTriggerInventoryPass`](../../../../src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveTriggerInventoryPass.cs),
which is why an HTTP test cannot yet exercise this reset design faithfully.

The gate has not passed until the concrete cycle executes through normal unprofiled and profile-constrained DMS PUTs on
both providers with typo, non-correlated, newly-present, stale, true-retarget, rollback, collection-correlation,
hidden-profile-state preservation, stamping, and referential-identity controls. Until then, this section is a bounded
implementation hypothesis, not evidence that the API path works.

Until that gate passes, failure to compile a `DeferredExistingReferenceBinding` is a Slice 3 plan-compilation result, not
`NoSafeSqlServerAssignment`. The classifier remains database-only.

## 9. Errors and Minimal Public Contracts

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

- `PropagationVectorNotRepresentable`;
- `PhysicalForeignKeyCandidateConflict`;
- `NoSafeSqlServerAssignment`;
- `CascadeClassificationComplexityExceeded`; and
- `DeferredExistingReferenceNotExecutable`.

The first four are model derivation/classification errors. `DeferredExistingReferenceNotExecutable` belongs to Slice 3
plan compilation unless Gate 2 later establishes a minimal pre-selection eligibility input. Each error carries a concise
structural witness. Do not replace all builder results with a new global success/proof artifact.

### Manifests, AOT, and packs

The relational model and mapping pack carry final FK columns/actions. Runtime plans carry only executor-consumed
deferred-binding metadata. SQL Server modes and witnesses stay derivation-local unless a concrete manifest diagnostic
consumer is established.

Mapping packs also carry each reference site's lineage-anchor bindings so `MappingSet.FromPayload` can reconstruct the
runtime projection. They do not serialize classifier modes, solver state, exhaustive certificates, semantic hashes, or
proof identifiers. Add deferred-binding fields only when the implemented AOT consumer requires them, and verify
runtime/AOT semantic equivalence at that time.

MetaEd owns early authored-model feedback for SQL Server realizability. DMS remains authoritative for canonical storage,
physical candidate deduplication, selected SQL Server actions, and provider provisioning. MetaEd may exchange authored
semantic paths and outcome categories without reproducing DMS internal identifiers.

## 10. Verification and Delivery

### Required checked-in fixtures

Storage/candidate fixtures cover primitive identities, one and multiple reference-backed identities, abstract-member and
identity-alias mappings, optional presence, child/collection/extension sites, unified columns, physical deduplication,
parallel candidates, and provider-limit failures.

Mutation fixtures cover every primitive component and non-empty subset, each reference retarget, multiple retargets,
primitive plus retargets, and all writable groups together.

SQL Server fixtures cover sole edges, independent parents, covered/uncovered and overlapping diamonds, parallel edges,
safe and unsafe cycles, zero-hop carriers, optional-carrier failure, conflicting unified writes, no solution, deterministic
work-limit exhaustion, and reversed-input determinism.

Provider/API fixtures cover errors 1785 and 547, direct cycle SQL, unprofiled and profile-constrained cycle PUTs,
PostgreSQL full-cascade cycles, old-identity concurrency rejection, typo/future-identity rejection, true retarget, stale
version, rollback, hidden-profile-state preservation, stamping, referential-identity maintenance, change queries, and
final constraint validation.

### Delivery slices

1. **Database evidence and static feasibility:** provider cycle and stock-schema vector measurements.
2. **Complete vectors and candidates:** lineage inventory, storage, propagation keys, physical deduplication, and limits.
3. **Provider actions:** PostgreSQL fixed assignment and SQL Server bounded global selection.
4. **DMS PUT execution:** deferred-resolution POC, unprofiled and profile-constrained cycle PUTs, narrow deferred existing
   bindings, and every v1 mutation form.
5. **Manifest/AOT/pack integration:** only final state and implemented runtime metadata.
6. **Full-schema qualification:** stock, TPDM, extension, adversarial, concurrency, and performance evidence.

All slices are required before the finalized v1 relational contract is complete. DMS-1258 must be narrowed or used as
an umbrella with independently testable child stories.

### Current evidence gates

| Gate | Current result | Consequence |
|---|---|---|
| Complete vector measured screen | Pass for the transitive DS 5.2 and TPDM key/column screens, including maximum-value provider probes | Do not implement per-site minimal anchor closure; retain full-schema row/index qualification. |
| Reciprocal database cycle | Pass on SQL Server and PostgreSQL | Retain zero-hop cycle breaking as a required classifier outcome. |
| Deferred ordinary resolution | Open; unprofiled executor seam identified, profile path and actual DMS PUT not yet proven | Do not freeze or claim the runtime protocol complete. |
| Simple global search | Open until the classifier exists and is measured | Start with deterministic bounded iterative-deepening DFS; add optimization only from evidence. |
| Minimal artifact contract | Design constraint; implementation validation pending | Carry final actions and lineage bindings; do not add classifier/proof/hash protocols. |

## Relationship to Legacy ODS

Legacy MetaEd/ODS orients edges from referenced entity to identity-dependent entity, treats raw incoming-edge count as a
multiple-path test, and retains an alphabetically selected source while disabling others. Its “cyclical reference graph”
test is a converging diamond rather than a directed cycle.

DMS does not copy that algorithm. It constructs storage-mapped physical candidates, distinguishes independent parents
from duplicate reachability, searches action assignments globally, permits coverage-certified cycle cuts, and fails SQL
Server derivation instead of silently weakening referential integrity.
