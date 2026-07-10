# DMS-1129 Design Reset

## Status

Implemented scope-control and decision record for the DMS-1129 foreign-key pruning and identity-propagation design.

This document defines how to reduce the current design to the simplest responsible architecture while preserving the
required SQL Server cycle behavior, complete v1 identity-change support, and PostgreSQL provider policy.

The documentation reset and collateral restoration are complete. Slice 0 now proves the reciprocal provider cycle and
passes the corrected transitive complete-vector key-width/column-count screen for Data Standard 5.2 and TPDM. The actual
DMS PUT gate remains open because the current production model still uses the superseded SQL Server
reduced-FK/propagation-trigger path; a faithful HTTP POC
requires the narrow portions of delivery Slices 1 through 3. The reset therefore defines the bounded implementation
direction without claiming the runtime cycle is already executable.

## Why Reset the Design Now

The current branch contains a documentation-only design revision spanning 51 files and approximately 5,200 added lines.
It has grown from foreign-key pruning into a broader proof engine, graph solver, runtime resolution protocol, storage
model, mapping-pack protocol, and cross-repository conformance system.

The design contains important correctness improvements, particularly:

- full-composite foreign keys;
- global SQL Server action selection;
- safely breakable cycle support;
- reference-backed and mixed identity-change modeling; and
- recognition that future reference identities must be executable through DMS PUT.

However, implementation should not begin until those required behaviors are separated from optimizations, serialized
proof machinery, and generalized protocols that have not yet been justified by executable evidence or full-schema
measurements.

This is the cheapest point to reset the design because:

- the branch contains design documentation rather than production implementation;
- `RelationalMappingVersion` remains `v1`;
- there is no production compatibility or migration requirement; and
- the current work can be reconstructed around a smaller normative core without preserving an implemented protocol.

## Objective

Produce a small, evidence-backed design with one clear derivation path:

```text
Effective schema
    |
    v
Complete propagation vectors + physical FK candidates
    |
    +-- PostgreSQL: assign fixed actions mechanically
    |
    +-- SQL Server: globally select legal, safe actions
                              |
                              v
                    Final relational model
                        |             |
                        v             v
                       DDL      minimal PUT metadata (Gate 2)
```

The final architecture must be easy to explain, independently testable in delivery slices, and no more general than the
accepted DMS behavior requires.

## Irreducible Contract

The reset must preserve the following non-negotiable decisions.

1. `RelationalMappingVersion` remains `v1`.
2. Every document-reference foreign key is full composite.
3. Identity propagation uses native foreign-key cascades. There is no identity-value propagation trigger or reduced-FK
   fallback.
4. PostgreSQL receives fixed full-composite actions. It is never pruned, topology-classified, or failed because of
   cascade topology.
5. SQL Server alone performs error-1785 graph classification, selective pruning, and topology fail-fast.
6. SQL Server action selection is global because overlapping diamonds and cycles may require backtracking.
7. Cycles are action-choice problems, not automatic failures. Every safely breakable cycle must be considered for a safe
   assignment.
8. Every pruned SQL Server edge must be maintained by an exact same-row, same-statement-boundary carrier.
9. V1 supports all valid identity changes:
   - every independently writable primitive component;
   - every non-empty primitive subset;
   - one or more reference-backed replacements;
   - multiple reference-backed replacements; and
   - mixed primitive and reference changes in one operation.
10. An accepted safe cycle must execute through an actual DMS PUT, not only through direct SQL.
11. SQL Server derivation fails before DDL when no safe action assignment exists.
12. Ordinary provider-independent model validation still applies to both dialects.

Every other mechanism must be justified by either a failing correctness fixture or a measured database/platform limit.

## Simplification Hypothesis 1: Complete Intrinsic Lineage Vectors

### Decision: Accepted

The reproducible measurements in
`reference/design/backend-redesign/evidence/dms-1129/complete-vector-feasibility.md` and focused maximum-value provider
probes pass Gate 1's corrected transitive screen. Data Standard 5.2 reaches 22 columns and 1,300 declared SQL Server
bytes; TPDM reaches 27 columns. The conservative model adds 238/308 anchor columns respectively, with maximum per-table
increases of 11/20 `BIGINT` columns (88/160 bytes). No complete vector crosses a screened key-column, table-column, or
nonclustered unique-key threshold because of its anchors, and the worst SQL Server and PostgreSQL vectors accept
maximum-size values. This does not measure total SQL Server row width or PostgreSQL tuple/index overhead.

V1 therefore uses one complete vector per target. Site-minimal demand closure, `AnchorSetId` variants, and omission
proofs are removed. Full generated-schema qualification remains an implementation-slice requirement.

### Proposal

Use one complete transitive propagation vector per reference target instead of site-specific minimal anchor closure.

The vector contains, in order:

1. the target's public identity values;
2. the finite transitive union of stable `DocumentId` anchors reachable through the target's identity-contributing
   reference chains; and
3. the target's own `DocumentId` last.

For each direct target lineage `T -> U`, include `U.DocumentId` followed by every lineage anchor carried by `U`'s complete
vector. Traverse recursively in stable structural reference order. Every incoming reference to the target carries that
same vector. The target exposes one corresponding propagation key rather than one key variant per demanded anchor subset.
This transitive union is what replaces site-demand closure; a direct-root-only inventory is not complete.

### What This Removes

If feasible, this eliminates:

- site-specific least-fixed-point anchor demand;
- `AnchorSetId` variants;
- anchor omission proofs;
- multiple propagation-key variants per target;
- multiple target unique constraints created only for anchor subsets;
- propagation-vector selection protocols across DDL, runtime, and mapping packs; and
- a substantial portion of the current proof and stable-ID surface.

### Correctness Rationale

The complete vector preserves the important Session/CourseOffering behavior and its transitive generalization: when a
reference-backed identity is retargeted, its public identity values and every stable lineage `DocumentId` reachable
through that chain propagate together. A downstream full FK therefore cannot combine the public values of one referenced
row with the stable id of another.

This is intentionally a correctness-first storage model. Schema minimization is not a correctness requirement.

### Feasibility Evidence

The checked-in measurement derives these Data Standard 5.2 and TPDM statistics for both dialects:

- maximum propagation-vector column count;
- maximum encoded key bytes;
- added fixed-width row contribution (not total row width);
- number of added anchor columns;
- number of new unique constraints;
- projected generic manifest/DDL increase (actual mapping-pack increase is unavailable because the pack payload is not
  implemented); and
- every case that exceeds the screened key/index column, declared-key payload, or table-column thresholds.

If complete-vector propagation exceeds a real supported-provider limit, reintroduce minimal demand closure as a separate,
measured subsystem. Do not retain it preemptively merely because it produces narrower keys.

## Simplification Hypothesis 2: Deferred Ordinary Reference Resolution

### Decision: Open and Bounded

The current executor provides the intended unprofiled transaction seam: stored authorization precedes normal lookup; an
unprofiled missing reference already survives through current-state load and proposed authorization for precedence; and
it is rejected immediately before DML. A fresh ordinary resolver can run after persistence and before commit. The
profile-constrained path currently rejects the miss earlier, so equivalent profile merge/authorization behavior is an
explicit Gate 2 requirement.

The POC has not passed. Current SQL Server derivation still strips identity columns from mutable reference FKs and emits
identity-value propagation triggers, so an HTTP test would not exercise the reset architecture until the relevant parts
of Slices 1 through 3 exist. Retain only the narrow hypothesis below; do not restore the generalized protocol while the
gate is open.

The source audit is concrete: [`DefaultRelationalWriteExecutor`](src/dms/backend/EdFi.DataManagementService.Backend/DefaultRelationalWriteExecutor.cs)
opens the transaction, completes stored/proposed authorization, rejects the deferred miss immediately before DML, and
has a verification slot between persist and commit; [`ReferenceResolver`](src/dms/backend/EdFi.DataManagementService.Backend/ReferenceResolver.cs)
memoizes misses, so post-write verification needs a fresh instance. The incompatible current SQL Server shape is emitted
by [`ReferenceConstraintPass`](src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/ReferenceConstraintPass.cs)
and [`DeriveTriggerInventoryPass`](src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/SetPasses/DeriveTriggerInventoryPass.cs).

### Problem

A safely broken cycle can make a submitted, still-present reference name the target's future identity. Before the
initiating identity update, that referential identity does not yet exist in `dms.ReferentialIdentity`, even though the
reference continues to identify the same stable target row.

The current design addresses this with compiled per-case correlation queries, predictive future-value bindings, custom
JSON recordset inputs, locking plans, instance-scoped overrides, and post-write verification commands.

### Proposal

Test a narrower existing-PUT-only workflow:

1. Resolve all submitted references normally in bulk.
2. After stored-state authorization and current-state loading, defer an unresolved binding only when compiled metadata
   establishes that:
   - the binding already exists on the persisted row;
   - the target `DocumentId` is stable and non-null;
   - the submitted operation is an identity-changing PUT;
   - the selected native-cascade route gives that same target the submitted identity in the initiating statement; and
   - the persisted receiver row is already correlated by the normal write executor's stable row identity.
3. Reuse only the persisted target `DocumentId` and vector items proved unchanged. Source changing public values from
   submitted/origin bindings and changing lineage anchors from ordinary resolved inputs or proved origin writes.
4. Let the full FK and native cascade enforce the same-boundary relationship.
5. Immediately rerun the ordinary bulk referential-identity resolver inside the transaction.
6. Require the submitted future referential identity to resolve to the same persisted target `DocumentId`.
7. Roll back on a miss, ambiguity, different target id, stale state, failed FK, or any model/runtime mismatch.

Normal lookup always wins. This is not a generic "lookup missed, reuse the old id" fallback.

### Required Safeguards

The POC must establish:

- authorization can complete before resource DML using the stable persisted target;
- subject and persisted receiver rows are locked in deterministic order;
- collection sites reuse existing stable collection-row correlation rather than inventing request-ordinal identity;
- reference retargets that resolve normally are not mistaken for unchanged future-identity references;
- full FKs reject a typo or non-correlated future identity;
- after-statement ordinary resolution observes the new referential identity and the same target `DocumentId`;
- profile-constrained PUT uses the normal Core-produced writable-profile/current-state contracts and preserves hidden
  values and rows;
- stamping, referential-identity maintenance, change queries, and no-op behavior remain correct; and
- any failure rolls back all transactional work.

### Potential Simplification

If the POC succeeds, retain only a narrow compiled marker describing which existing bindings may defer and the retained
cascade route that justifies it. Remove:

- per-`MutationCaseId` resolution-plan proliferation;
- predictive anchor-projection plans for this case;
- custom pre-write correlation SQL when existing stored-row correlation is sufficient;
- custom JSON recordset protocols;
- separate post-write verification SQL where the ordinary resolver supplies the required check; and
- generalized future-reference protocols not needed by an accepted fixture.

If Gate 2 passes, PostgreSQL must construct the equivalent narrow write metadata from its fixed full-cascade actions.
That is runtime plan derivation, not PostgreSQL topology classification, pruning, or fail-fast.

## Minimal SQL Server Classifier

The design must retain exhaustive safety analysis while avoiding a public theorem-prover protocol.

### Physical Candidate

A physical FK candidate is identified structurally by:

- local table;
- ordered local storage columns;
- target table;
- ordered target storage columns;
- delete action; and
- semantic FK kind where necessary to distinguish otherwise identical constraint roles.

The update action is assigned after candidate construction and is not part of candidate identity. Contributing logical
sites and presence semantics remain attached as provenance.

Use typed structural records and ordinal ordering during derivation. Do not require a universal SHA-256 identifier
protocol for internal equality or ordering.

### Mutation Model

Model identity changes as independently writable groups:

- each independently writable primitive component is one group;
- each reference-backed replacement is one atomic group containing all public values and stable lineage anchors supplied
  by that reference; and
- a mutation case is any valid non-empty combination of groups.

The analysis must cover primitive subsets, individual and multiple reference replacements, and mixed combinations.
Symbolic values remain associated with their mutation origin, origin row, component/lineage, and statement boundary.

### Search

Use deterministic, bounded iterative-deepening DFS/backtracking over mutable candidates that participate in an error-1785
conflict or a value-flow choice. Enumerate assignments by increasing `CoveredNoAction` count and then by the stable
structural edge-order mode vector, with `NativeCascade` ordered before `CoveredNoAction`. The first valid assignment is
therefore the required optimum.

For each complete assignment:

1. verify the retained `ON UPDATE CASCADE` graph is acyclic;
2. verify there is at most one retained path between every ordered table pair;
3. verify every pruned edge has an exact carrier for every applicable mutation case;
4. verify the carrier reaches the same receiver row with the same complete vector in the same constraint-check boundary;
5. verify optional-reference presence implications;
6. verify competing writes to unified receiver columns carry the same symbolic value.

Cycle membership alone is never a failure condition. The selector does not call or anticipate write-plan compilation.

### Selection Objective

Choose among valid assignments using:

1. fewest `CoveredNoAction` mutable edges;
2. the lexicographically smallest stable structural edge-order mode vector.

This is deliberately database-only while Gate 2 is open. Plan compilation consumes the selected actions afterward. Gate
2 must prove that the deterministic database-safe assignment is executable through DMS PUT. Only if that fixture fails
while another database-safe assignment could work may Slice 3 introduce a minimal pre-selection
`DeferredPutEligibility` input; do not duplicate or predict write-plan logic inside the classifier.

### Complexity Policy

Start with the simplest deterministic bounded search. Add memoization, reachability-state canonicalization, or additional
optimization only if measured fixtures require it.

Never emit a provisional feasible assignment. Keep these failures distinct:

- search exhausted with no safe assignment; and
- deterministic work limit reached before the final selection or infeasibility was proved.

### Diagnostics

Safety validation remains exhaustive internally. Persistence does not need to reproduce the complete derivation trace.

For a selected pruned edge, retain a concise carrier witness containing only what is needed to audit the decision:

- pruned physical FK;
- mutation origin/group or grouped cases;
- changed-target retained route;
- receiver carrier route, including an explicit zero-hop origin write;
- complete receiver-row correlation key; and
- statement boundary.

For failure, return an actionable deterministic witness naming the involved tables, columns, FK candidates, mutation
origin, and failed obligation. Do not serialize exhaustive proof trees, omission proofs, or canonical hashes unless a
real consumer is identified.

## Minimal Public Contracts

### Derived Relational Model

For each finalized document-reference FK, the generic relational model contains:

- the full ordered local and target FK columns;
- final `OnDelete` and `OnUpdate` actions.

The classifier may use `NativeCascade`, `CoveredNoAction`, and `ImmutableNoAction` as derivation-local labels. They are
not part of `TableConstraint.ForeignKey`, `MappingSet`, or the mapping pack. Add a manifest-only mode or concise carrier
diagnostic only when a concrete diagnostic consumer requires it.

DDL generation consumes the final actions and never reruns classification.

### Runtime Write Plan

The runtime plan should contain only metadata the executor directly consumes. For future-identity handling, prefer a
small deferred-binding record containing:

- owning resource and binding/site;
- persisted target `DocumentId` source;
- stable persisted receiver-row locator;
- retained cascade route or other minimal eligibility evidence; and
- the final post-statement same-target resolution requirement.

Do not expose SQL Server proof objects to runtime.

### Mapping Packs

Mapping packs carry final FK `on_update` actions and the `ReferenceLineageAnchorBinding` inventory needed to reconstruct
the runtime projection. They do not carry derivation-local SQL Server modes, solver state, exhaustive certificates, or
proof identifiers.

Defer only the Gate 2 deferred-binding fields until runtime implementation identifies the minimal AOT metadata actually
consumed. Generic table, column, constraint, and write-binding payloads carry the complete vector without a new
cross-cutting protocol.

### Failure Convention

Retain the repository's exception-based model-derivation convention. Introduce a small stable set of typed error
categories with concise structured witnesses. Do not replace all existing builder results with a new global success
artifact unless multiple implemented consumers demonstrably require one.

## Documentation Reset

Reconstruct the design from a small normative skeleton rather than trimming the current revision paragraph by paragraph.

### Authoritative Design Outline

Rewrite `reference/design/backend-redesign/design-docs/mssql-cascading.md` around:

1. settled decisions and non-goals;
2. complete propagation-vector storage;
3. physical FK candidate derivation;
4. PostgreSQL fixed actions;
5. SQL Server graph legality and carrier safety;
6. deterministic bounded global selection;
7. the concrete safely breakable cycle;
8. narrow DMS PUT deferred resolution;
9. errors and concise diagnostics; and
10. verification and delivery slices.

### Initially Aligned Documents

Initially update only the documents that define direct contracts:

- `reference/design/backend-redesign/design-docs/mssql-cascading.md`;
- `reference/design/backend-redesign/design-docs/data-model.md`;
- `reference/design/backend-redesign/design-docs/ddl-generation.md`;
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md`;
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md`; and
- `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md`.

Restore pack, AOT, presentation, summary, and unrelated epic documents close to their pre-revision state. Reapply changes
only when an accepted implementation slice establishes a concrete dependency.

This is a scope-control mechanism, not a documentation omission. Once the central contracts stabilize, dependent
documents receive small outcome-oriented updates rather than copies of derivation internals.

## Delivery Slices

DMS-1258 must not remain one monolithic implementation unit. It may act as an umbrella or be narrowed to the physical
classifier, but implementation work should be delivered in independently testable slices.

All slices are required before the finalized v1 relational contract is complete.

### Slice 0: Database Evidence and Static Feasibility

Deliverables:

- minimal reciprocal two-table SQL Server cycle fixture;
- DDL with one `CASCADE` and one `NO ACTION` FK;
- single-component and all-component identity updates;
- negative classification when both sides allow direct identity updates;
- PostgreSQL full-cascade equivalent;
- Data Standard 5.2 and TPDM complete-vector statistics; and
- a written Gate 1 decision and an explicit open/bounded Gate 2 decision.

Exit criterion: the load-bearing database cycle is executable and the storage hypothesis has a reproducible measured
screen. Runtime PUT feasibility is owned wholly by Slice 3.

### Slice 1: Complete Vectors and Physical Candidates

Deliverables:

- direct reference-backed lineage inventory and its complete transitive union;
- complete propagation vector per target;
- reference-site anchor storage and write bindings;
- target propagation keys;
- storage-mapped physical candidate construction;
- candidate deduplication before action selection;
- full-composite FK finalization; and
- provider-independent limit validation.

Exit criterion: fixtures derive deterministic complete FK shapes without provider action classification.

### Slice 2: Provider Actions and SQL Server Classifier

Deliverables:

- mechanical PostgreSQL action assignment with no topology classifier;
- SQL Server physical cascade multigraph;
- deterministic bounded global selection;
- exact carrier validation, including zero-hop origin writes;
- safely breakable and unbreakable cycle handling;
- database-only deterministic tie-breaking;
- concise selected-action diagnostics; and
- deterministic no-solution versus complexity-limit failures.

Exit criterion: generated DDL installs on both providers and all graph/value-flow fixtures produce the expected outcome.

### Slice 3: DMS PUT Identity Execution

Deliverables:

- deferred ordinary-resolution POC;
- actual DMS PUT through the accepted cycle on both providers;
- update-only deferred existing-reference handling;
- normal-lookup-first behavior;
- stable target and receiver-row correlation;
- authorization and locking rules;
- unprofiled and profile-constrained PUT behavior with hidden-state preservation;
- after-statement ordinary-resolution verification;
- rollback on every mismatch;
- primitive identity subsets;
- one and multiple reference retargets;
- mixed primitive/reference changes;
- root, child, collection, and extension receiver sites; and
- concurrency and stale-state tests.

Exit criterion: every accepted fixture executes through the normal DMS API and preserves full referential integrity.

### Slice 4: Manifest, AOT, and Mapping-Pack Integration

Deliverables:

- final actions in the relational-model manifest, plus manifest-only SQL Server diagnostics only if a concrete consumer
  requires them;
- minimal runtime deferred-binding metadata;
- runtime/AOT semantic equivalence;
- mapping-pack validation for the finalized full vectors and actions; and
- removal of unused proof, hash, and certificate protocol fields.

Exit criterion: runtime compilation and pack loading produce equivalent finalized models and write behavior without
serializing solver internals.

### Slice 5: Full-Schema Qualification

Deliverables:

- full Data Standard 5.2 and TPDM derivation;
- representative extension and adversarial graph fixtures;
- reversed-input determinism tests;
- SQL Server and PostgreSQL provider integration;
- referential-integrity concurrency regression coverage;
- update-tracking and maintenance-trigger validation;
- solver-state and derivation-time measurements; and
- mapping-pack and schema-size measurements.

Exit criterion: the design is demonstrably feasible at supported-schema scale and operationally bounded.

## Verification Matrix

Checked-in fixtures must cover at least:

### Storage and Candidate Derivation

- primitive-only identity;
- one reference-backed identity;
- multiple independent reference-backed identities;
- abstract-member identity mapping;
- identity rename/alias mapping;
- optional and required reference presence;
- child, collection, and extension receiver sites;
- key-unified receiver columns;
- logical sites collapsing to one physical FK;
- distinct parallel physical FKs; and
- provider key/row limit failure.

### Identity Mutations

- every independently writable primitive component;
- every non-empty primitive subset;
- each reference retarget independently;
- multiple reference retargets together;
- primitive plus one retarget;
- primitive plus multiple retargets; and
- all writable changes at once.

### SQL Server Selection

- sole cascade edge;
- legal independent parents;
- covered and uncovered diamonds;
- overlapping diamonds requiring backtracking;
- parallel-edge conflict;
- safely breakable cycle;
- unbreakable cycle;
- zero-hop origin-write carrier;
- optional carrier/presence failure;
- conflicting unified-column writes;
- no-solution result; and
- deterministic work-limit result.

### Provider and API Behavior

- SQL Server error-1785 reproduction;
- SQL Server error-547 negative cases;
- successful concrete cycle DDL and direct SQL mutation;
- actual DMS PUT for the concrete cycle;
- profile-constrained DMS PUT for the concrete cycle;
- PostgreSQL full-cascade cycle behavior;
- old-identity concurrent insert rejection;
- typo/non-correlated future identity rejection;
- true retarget behavior;
- stale-version behavior;
- no DML or full rollback on invalid operations;
- update tracking and stamping after cascades; and
- full `DBCC CHECKCONSTRAINTS`/provider-equivalent validation.

## Decision Gates

### Gate 1: Complete Vector Measured Screen

**Current result: Pass for the corrected transitive architecture screen.** The stock-schema key/index column counts,
declared key payloads, table column counts, added fixed-width contribution, and focused maximum-value probes fit after
recursively including anchors inherited through identity-reference chains. Site-minimal anchor closure is removed from
the authoritative design. Total SQL Server row width, PostgreSQL tuple/index overhead, and full generated-schema DDL
remain Slice 5 qualification.

- **Pass:** DS 5.2 and TPDM introduce no anchor-caused crossing in the measured screen. Remove minimal per-site anchor
  closure from the design.
- **Fail:** retain measurements and design the smallest demand-reduction mechanism that fixes the actual failing cases.

### Gate 2: Deferred Ordinary Resolution

**Current result: Open.** The unprofiled executor seam is confirmed, but the profile path rejects the same miss earlier
and the actual cycle has not executed through either DMS PUT path. The narrow hypothesis remains provisional and no
generalized replacement protocol is accepted.

While open, SQL Server selection remains database-only. If the selected assignment cannot execute but another
database-safe assignment can, define the smallest pre-selection `DeferredPutEligibility` input in Slice 3 and feed that
fact into selection; do not make the classifier compile or duplicate write plans.

- **Pass:** the actual unprofiled and profile-constrained DMS PUT cycle, authorization, locking, collection correlation,
  hidden-state preservation, and post-statement resolution work safely. Replace the generalized future-resolution
  protocol.
- **Fail:** preserve the failing fixture and add only the missing correlation/prediction capability required by that
  fixture.

### Gate 3: Simple Global Search

**Current result: Open until Slice 2.** Start with deterministic bounded iterative-deepening DFS and measure it before
adding solver infrastructure.

- **Pass:** the deterministic iterative-deepening DFS stays within selected state/work bounds on stock, extension, and
  adversarial fixtures. Do not add memoization or solver infrastructure.
- **Fail:** optimize the observed bottleneck while preserving the same public contract and deterministic failure policy.

### Gate 4: Minimal Artifact Contract

**Current result: Design constraint; implementation validation pending.** No proof/hash/global artifact protocol is
retained.

- **Pass:** final FK actions, lineage-anchor bindings, optional manifest-only SQL Server diagnostics, and minimal
  deferred-binding metadata serve their concrete consumers. Do not introduce a global proof artifact.
- **Fail:** add a shared artifact only for the concrete fields consumed by two or more implemented producers.

## Immediate Next Steps

1. **Done:** preserve this document as the reset scope-control and decision record.
2. **Done:** Slice 0 proves the provider cycle and passes the measured complete-vector screen; the actual DMS PUT belongs
   to Slice 3 and remains the open Gate 2 item in DMS-1275.
3. **Done:** check in and execute the reciprocal SQL Server and PostgreSQL provider fixtures.
4. **Open — DMS-1275:** exercise the same case through actual DMS PUT using deferred ordinary resolution.
5. **Done:** check in reproducible Data Standard 5.2 and TPDM complete-vector statistics and maximum-value probes.
6. **Done:** record Gate 1 as passed and Gate 2 as open/bounded.
7. **Done:** rewrite `mssql-cascading.md` from the small authoritative outline.
8. **Done:** restore collateral documents to `origin/DMS-1129` and reapply only direct v1/provider/cycle outcomes.
9. **Done:** narrow DMS-1258 to provider actions and create DMS-1274 through DMS-1277 for the remaining slices.
10. **Done:** align DMS-1129, DMS-1258, and METAED-1667 descriptions with the simplified provider boundary and remove
    the shared proof/hash/corpus protocol requirements.

## Definition of a Successful Reset

The reset is complete when:

- the concrete safely breakable cycle is executable through SQL Server and DMS PUT;
- PostgreSQL has no topology-classification or topology-failure path;
- all v1 identity mutation forms are represented by checked-in fixtures;
- the complete-vector architecture screen is measured and decided, with full row/index/DDL qualification explicit;
- the SQL Server selector is global, deterministic, bounded, and understandable;
- runtime future-reference handling is no more general than demonstrated cases require;
- derivation proofs remain internal validation rather than a cross-system serialization protocol;
- DDL, runtime, and pack consumers receive only the final state they need;
- implementation work is split into independently testable slices; and
- the authoritative design can be reviewed without reconstructing its logic across dozens of documents.
