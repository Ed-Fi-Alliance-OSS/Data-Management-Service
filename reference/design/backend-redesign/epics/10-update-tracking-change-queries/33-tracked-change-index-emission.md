---
jira: TBD
jira_url: TBD
---

# Story: Evaluate and Conditionally Emit Tier-1 Auth-Check Indexes on `tracked_changes_*` Tables

## Description

Evaluate the two Tier-1 candidate categories proposed by spike DMS-1185 and specified in `22-spike-findings.md` § "Indexes on the `tracked_changes*` tables", then implement only categories that pass their independent gates: the five tracked PrimaryAssociation covering indexes and the shared-descriptor `(Discriminator, ChangeVersion)` index.
Any adopted category is derived by the tracked-change extension to the existing `DeriveIndexInventoryPass`.

The PA candidates back the tracked-change arms of the four `*IncludingDeletes` authorization views (every people-strategy `/deletes` and `/keyChanges` request evaluates the views matching its person subject kinds, so at least one tracked arm runs per such request); the shared-descriptor candidate backs the `Discriminator` filter every descriptor `/deletes` applies to the shared tracked-change table.
Measured improvements on PostgreSQL at 10M tombstones: 3.5-10x on view evaluation, 1.5-10x on descriptor `/deletes`, 3x on `/keyChanges` and single-school `/deletes`, at ~1 µs/row bulk-insert overhead.
One blocking PostgreSQL regression is known and recorded in `22-spike-findings.md` § "Indexes on the `tracked_changes*` tables" (the narrow-window `/deletes` anti-join flip on the PA resources); no regression exception is granted.

Per-resource securable-element indexes are explicitly out of scope (deferred pending the runtime query-shape change; see the follow-on subject-cardinality story).
This story has candidate-evaluation, gated-implementation, and exact-DDL verification phases.
The spike's bounded probes inform the candidate set but are not implementation acceptance evidence.
Before product code or goldens change, the candidate-evaluation phase starts from the current generated DDL and applies pinned provider-specific SQL overlays for the five-PA category and shared-descriptor category separately.
The overlays are benchmark inputs, not shipping implementations.
Passing the isolated gates on both providers makes a category eligible, not yet selected for implementation.
When only one category is eligible, it is selected; when both are eligible, both are selected only if the combined interaction gate passes, otherwise reviewed disposition selects at most one.
The implementation phase derives only the finally selected category or categories through `DeriveIndexInventoryPass`; the final phase reruns the applicable matrix against exact generated DDL.
The two index categories are gated and emitted independently, and a category cannot use the other category's benefit or low cost to pass.
The PA category is expected to be blocked on PostgreSQL by the known anti-join flip until the subject-cardinality story's shape fix lands and its candidate-overlay rerun shows the flip removed.
If Phase 1 blocks the PA category, this story completes with the shared-descriptor disposition and records the PA category as blocked, not rejected; it does not remain open.
The subject-cardinality story's shape fix and candidate-overlay rerun then trigger a dedicated PA re-evaluation ticket that reruns the PA-only eligibility, any required combined-interaction gate, and the emission and post-implementation phases under this story's recorded contracts.
Any other provider failure records the result and returns the design for review without emitting that category on either provider.

## Evidence Protocol and Gates

Environment pinning, workload floors, measurement and noise controls, seek-use requirements, write and storage ceilings, provider-blocking semantics, independence rules, and post-emission verification follow `22-spike-findings.md` § "Tracked-index evidence protocol".
The harness may extend the cross-provider workflow from `../12-ops-guardrails/04-performance-benchmarks.md`, and its artifact must additionally pin the story-specific data shape:

- schema and row cardinalities, at or above the protocol's workload floors;
- value distributions and correlations for EdOrg, Student, Contact, Staff, descriptor, and PrimaryAssociation keys;
- exact `ChangeVersion` bounds, qualifying-row counts, and selectivity for each of the five window shapes (no bounds, min-only, max-only, both-bounds full, both-bounds narrow);
- live/tombstone identity overlap, including the proportion of recreated live identities exercised by `/deletes`;
- authorization-subject construction for single-subject and district-scale cases;
- exact query text and parameters.

The current generated DDL is the baseline.
Candidate overlays must pin the exact provider SQL, schema/name, key order, INCLUDE columns, and category membership for each proposed index.
The mandatory variants are baseline, PA-only, and shared-descriptor-only.
If both categories pass independently, run the combined variant through the full read matrix to detect plan interactions; the combined result cannot change either category's independent cost decision.
Candidate evaluation does not require or permit a temporary shipping implementation.

The mandatory read matrix covers:

- all four `*IncludingDeletes` views at single-subject and district-scale subject cardinalities (eight view cells per provider);
- the complete `/deletes` and `/keyChanges` × resource × `ChangeVersion` window × subject-cardinality cross-product: both endpoints, each of the five PrimaryAssociation resources plus one non-PrimaryAssociation resource using a people strategy, the four window predicate forms the planner emits as distinct SQL, expanded to five measured shapes (no bounds, min-only, max-only, and both-bounds at full and narrow selectivity; the two both-bounds shapes share SQL text and differ only in pinned parameter selectivity, and the harness must not deduplicate them), and single-subject and district-scale authorization sets (120 endpoint cells per provider), with every cell also recording the separate `totalCount` count statement as its own measured shape;
- shared-descriptor `/deletes` across the same five window shapes, each also recording its count statement;
- a functional assertion that shared-descriptor `/keyChanges` remains empty by contract.

The cost matrix covers bulk tombstone and key-change inserts separately for each of the five PA tracked tables plus each table's resulting PA-index storage, and bulk shared-descriptor tombstone inserts plus resulting shared-descriptor-index storage.

Category-specific gates on top of the protocol:

- PostgreSQL PA category: in the isolated PA-only comparison, every tracked PA index is used by at least one corresponding `*IncludingDeletes` arm, the four-view matrix provides the protocol's required benefit, and no shape acquires the per-row join-filter/nested-loop anti-join form. If the narrow-window `/deletes` flip reproduces on any PrimaryAssociation resource, the PA category is blocked pending the subject-cardinality story's shape fix and candidate-overlay rerun; record the failing plans and ratios.
- PostgreSQL shared-descriptor category: in the isolated shared-descriptor-only comparison, at least one descriptor `/deletes` window provides the protocol's required benefit.
- SQL Server PA category: in the isolated PA-only comparison, every tracked PA index is used by at least one corresponding `*IncludingDeletes` arm as a covering seek without lookups, and the four-view matrix provides the protocol's required benefit.
- SQL Server shared-descriptor category: in the isolated shared-descriptor-only comparison, at least one descriptor `/deletes` window provides the protocol's required benefit.
- The protocol's write and per-table baseline storage ceilings apply to each category's affected tables: each of the five PA tracked tables for the PA category, and the shared tracked-descriptor table for the shared-descriptor category.

Both providers must pass all applicable benefit, regression, and cost gates for a category before it becomes eligible for provider-neutral implementation.
A category's cost ratios compare its isolated candidate overlay with the same baseline and use only that category's affected writes, index bytes, and table bytes.
A provider failure blocks that category's emission on both providers until the design is revised with reviewed evidence (for the PA category's known PostgreSQL anti-join flip, until the subject-cardinality story's shape fix and rerun clear it); the result is not waived as run-to-run noise.
If both categories pass independently, their combined variant must also satisfy every applicable `1.20` read-regression ceiling and prohibited-plan-shape rule before both may be emitted together.
A combined failure preserves the isolated results but blocks simultaneous emission and returns the combined design for review; at most one category may proceed only after that review selects it.
Combined benefit or cost never substitutes for an isolated category gate.
The ceiling values and their rationale live in the protocol section and are not restated here.

## Acceptance Criteria

- Before product code or goldens change, Phase 1 checks in the deterministic harness, pinned baseline and category-specific candidate overlays, and dated raw/result package; it executes the mandatory matrix on both providers, records independent eligibility for each category, and records the final selection after any required combined review.
- A failed or non-selected category remains absent from `IndexesInCreateOrder`, generated DDL, and manifests. If neither category is selected, close the index-emission portion with the reviewed evidence and no index or golden changes.
- As the common structural change, reposition the existing `DeriveIndexInventoryPass` immediately after `DeriveTrackedChangeInventoryPass` and before `DeriveAuthHierarchyPass`, while preserving its existing PK/UK/FK-support and content-version output. Add tracked-change index derivation only for categories in the final Phase 1 selection. When the repositioning ships, update `key-unification.md` § "Recommended placement in `RelationalModelSetPasses` order" to reflect the new position; this documentation update is unconditional and independent of category adoption.
- Pass-order tests pin both strict/default pipelines and prove that repositioning alone leaves the shared index inventory byte-for-byte unchanged.
- Selected categories are emitted exactly as specified in `22-spike-findings.md` § "Indexes on the `tracked_changes*` tables": the five PA covering indexes with per-table gating for the PA category, the `[Discriminator, ChangeVersion]` index for the shared-descriptor category, the strict/default missing-column behavior, and the existing naming conventions. Design decisions live in that section; this ticket implements them without re-deciding.
- Emits nothing else: no per-resource securable, person, or namespace indexes, and no entries for `Resource`/`ConcreteAbstract` tables beyond the five PA tables.
- If either category is emitted, the physical schema change requires a `RelationalMappingVersion` bump; the bump and its locked-hash bless procedure are deferred to a dedicated mapping-version ticket rather than owned here; the first such ticket is filed with the follow-on tickets after spike approval and must land no later than the first story that ships a physical index change, and each later independently released batch of physical index changes requires its own bump ticket for that release.
- Unit, golden, and integration coverage matches the recorded category dispositions on both providers: adopted categories present and rejected categories absent across inventory, generated DDL, and manifests, including strict/default behavior, deterministic ordering, identifier shortening, and regenerated goldens. The concrete test and fixture inventory is owned by this ticket's tasking.
- When a category is adopted, this ticket merges the adopted design from `22-spike-findings.md` § "Indexes on the `tracked_changes*` tables" into the normative docs: the index specification and test assertions into `change-queries.md`, and the `DbIndexInfo` contract notes into `compiled-mapping-set.md`.
- After emitting a category, verify that its exact generated DDL is semantically identical to the pinned candidate overlay, then rerun the complete applicable matrix against that generated DDL. A mismatch or failed post-implementation gate blocks acceptance rather than being described as run-to-run noise.

## Tasking Input

Review-derived notes recorded at spike approval; this ticket's tasking adopts each into acceptance criteria or rejects it with recorded rationale. These are input, not acceptance criteria.

- Cross the shared-descriptor matrix cells with both descriptor authorization shapes on both providers: `NoFurtherAuthorizationRequired` (no row-level predicate) and `NamespaceBased` (adds the `OldNamespace` residual), across the five window shapes and both page and count statements.
