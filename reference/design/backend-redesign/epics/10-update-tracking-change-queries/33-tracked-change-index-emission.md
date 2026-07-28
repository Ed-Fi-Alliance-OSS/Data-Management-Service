---
jira: TBD
jira_url: TBD
---

# Story: Evaluate and Conditionally Emit Tier-1 Auth-Check Indexes on `tracked_changes_*` Tables

## Description

Evaluate the two Tier-1 candidate categories proposed by spike DMS-1185 and specified in `change-queries.md` § "Indexes on the `tracked_changes*` tables", then implement only categories that pass their independent gates: the five tracked PrimaryAssociation covering indexes and the shared-descriptor `(Discriminator, ChangeVersion)` index.
Any adopted category is derived by the tracked-change extension to the existing `DeriveIndexInventoryPass`.

The PA candidates back the tracked-change arms of the four `*IncludingDeletes` authorization views (every people-strategy `/deletes` and `/keyChanges` request evaluates the views matching its person subject kinds, so at least one tracked arm runs per such request); the shared-descriptor candidate backs the `Discriminator` filter every descriptor `/deletes` applies to the shared tracked-change table.
Measured improvements on PostgreSQL at 10M tombstones: 3.5-10x on view evaluation, 1.5-10x on descriptor `/deletes`, 3x on `/keyChanges` and single-school `/deletes`, at ~1 µs/row bulk-insert overhead.
One blocking PostgreSQL regression is known and recorded in `change-queries.md` § "Indexes on the `tracked_changes*` tables" (the narrow-window `/deletes` anti-join flip on the PA resources); no regression exception is granted.

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
Any other provider failure records the result and returns the design for review without emitting that category on either provider.

## Evidence Protocol and Gates

The harness may extend the cross-provider workflow from `../12-ops-guardrails/04-performance-benchmarks.md`, but its artifact must pin:

- the deterministic generator implementation/version and integer seed;
- schema and row cardinalities;
- value distributions and correlations for EdOrg, Student, Contact, Staff, descriptor, and PrimaryAssociation keys;
- exact `ChangeVersion` bounds, qualifying-row counts, and selectivity for the full and narrow windows;
- live/tombstone identity overlap, including the proportion of recreated live identities exercised by `/deletes`;
- authorization-subject construction for single-subject and district-scale cases;
- exact query text and parameters;
- database image/version, statistics preparation, and cache-preparation policy.

The current generated DDL is the baseline.
Candidate overlays must pin the exact provider SQL, schema/name, key order, INCLUDE columns, and category membership for each proposed index.
The mandatory variants are baseline, PA-only, and shared-descriptor-only.
If both categories pass independently, run the combined variant through the full read matrix to detect plan interactions; the combined result cannot change either category's independent cost decision.
Candidate evaluation does not require or permit a temporary shipping implementation.

The mandatory read matrix covers:

- all four `*IncludingDeletes` views at single-subject and district-scale subject cardinalities (eight view cells per provider);
- the complete `/deletes` and `/keyChanges` × resource × `ChangeVersion` window × subject-cardinality cross-product: both endpoints, each of the five PrimaryAssociation resources plus one non-PrimaryAssociation resource using a people strategy, full and narrow windows, and single-subject and district-scale authorization sets (48 endpoint cells per provider);
- shared-descriptor `/deletes` at full and narrow `ChangeVersion` windows;
- a functional assertion that shared-descriptor `/keyChanges` remains empty by contract.

The cost matrix covers bulk tombstone and key-change inserts separately for each of the five PA tracked tables plus the resulting combined PA-index storage, and bulk shared-descriptor tombstone inserts plus resulting shared-descriptor-index storage.

For every read or write A/B shape:

1. Use the same provisioned data and statistics for baseline and candidate.
2. Run five unmeasured warm-ups per variant.
3. Record twenty measured pairs, alternating `A → B` and `B → A` order.
4. Record raw elapsed time, the provider execution plan, PostgreSQL buffer counts or SQL Server logical reads, and the returned/qualifying row counts.
5. Use the median of the twenty paired `candidate / baseline` elapsed-time ratios as the gate.
6. Treat a run as noisy when either variant's median absolute deviation divided by its median exceeds 15%; rerun the full comparison once, and leave the decision blocked if the second run is also noisy.

Provider- and category-specific read gates:

- PostgreSQL PA category: in the isolated PA-only comparison, every tracked PA index is used by at least one corresponding `*IncludingDeletes` arm, the four-view matrix demonstrates at least a 20% improvement in median elapsed time or buffer reads, every PA-applicable view and endpoint shape has a median elapsed-time ratio at or below 1.20, and no shape acquires the per-row join-filter/nested-loop anti-join form. If the narrow-window `/deletes` flip reproduces on any PrimaryAssociation resource, the PA category is blocked pending the subject-cardinality story's shape fix and candidate-overlay rerun; record the failing plans and ratios.
- PostgreSQL shared-descriptor category: in the isolated shared-descriptor-only comparison, at least one descriptor `/deletes` window improves median elapsed time or buffer reads by 20%, and both windows have median elapsed-time ratios at or below 1.20.
- SQL Server PA category: in the isolated PA-only comparison, every tracked PA index is used by at least one corresponding `*IncludingDeletes` arm as a covering seek without lookups, the four-view matrix demonstrates at least a 20% improvement in median elapsed time or logical reads, and every PA-applicable view and endpoint shape has a median elapsed-time ratio at or below 1.20.
- SQL Server shared-descriptor category: in the isolated shared-descriptor-only comparison, at least one descriptor `/deletes` window improves median elapsed time or logical reads by 20%, and both windows have median elapsed-time ratios at or below 1.20.

Provider-specific cost gates:

- PostgreSQL PA category: for each of the five PA tracked tables, every measured tombstone and key-change bulk-write median elapsed-time ratio is at or below 1.85; the five added PA indexes occupy at most 40% of those five tracked tables' combined size.
- PostgreSQL shared-descriptor category: its shared tracked-descriptor bulk-write median elapsed-time ratio is at or below 1.85, and the added shared-descriptor index occupies at most 40% of the shared tracked-descriptor table size.
- SQL Server PA category: for each of the five PA tracked tables, every measured tombstone and key-change bulk-write median elapsed-time ratio is at or below 2.00; the five added PA indexes occupy at most 50% of those five tracked tables' combined size.
- SQL Server shared-descriptor category: its shared tracked-descriptor bulk-write median elapsed-time ratio is at or below 2.00, and the added shared-descriptor index occupies at most 50% of the shared tracked-descriptor table size.

Both providers must pass all applicable benefit, regression, and cost gates for a category before it becomes eligible for provider-neutral implementation.
A category's cost ratios compare its isolated candidate overlay with the same baseline and use only that category's affected writes, index bytes, and table bytes.
A provider failure blocks that category's emission on both providers until the design is revised with reviewed evidence (for the PA category's known PostgreSQL anti-join flip, until the subject-cardinality story's shape fix and rerun clear it); the result is not waived as run-to-run noise.
If both categories pass independently, their combined variant must also satisfy every applicable `1.20` read-regression ceiling and prohibited-plan-shape rule before both may be emitted together.
A combined failure preserves the isolated results but blocks simultaneous emission and returns the combined design for review; at most one category may proceed only after that review selects it.
Combined benefit or cost never substitutes for an isolated category gate.
The 1.20 regression ceiling and 20% minimum benefit require a material improvement while allowing bounded unrelated-plan variation; PostgreSQL's 1.85/40% cost ceilings bound the measured 1.69x write ratio and approximately 30% storage with explicit margin.
SQL Server uses the more conservative 2.00/50% cost ceilings because the spike does not contain an implementation-scale write-amplification measurement against exact generated DDL.
Absolute times are evidence, not gates, because they vary across hosts and CPU architectures.

## Acceptance Criteria

- Before product code or goldens change, Phase 1 checks in the deterministic harness, pinned baseline and category-specific candidate overlays, and dated raw/result package; it executes the mandatory matrix on both providers, records independent eligibility for each category, and records the final selection after any required combined review.
- A failed or non-selected category remains absent from `IndexesInCreateOrder`, generated DDL, and manifests. If neither category is selected, close the index-emission portion with the reviewed evidence and no index or golden changes.
- As the common structural change, reposition the existing `DeriveIndexInventoryPass` immediately after `DeriveTrackedChangeInventoryPass` and before `DeriveAuthHierarchyPass`, while preserving its existing PK/UK/FK-support and content-version output. Add tracked-change index derivation only for categories in the final Phase 1 selection.
- Pass-order tests pin both strict/default pipelines and prove that repositioning alone leaves the shared index inventory byte-for-byte unchanged.
- If either category is selected, the pass takes a tracked-index-column strictness flag wired from the same `CreateStrict`/`CreateDefault` pipeline choice as `DeriveAuthorizationIndexInventoryPass`: strict throws when a required column for a selected category is missing, while default skips that category's affected table.
- If the PA category is selected, emit the five covering indexes from the table in `change-queries.md`: `DbIndexKind.Authorization`, key and INCLUDE columns mapped through `TrackedChangeNameConventions.OldValueColumn` from the existing PA literals, names via `ConstraintNaming.BuildAuthorizationIndexName`.
- PA per-table gating: emit only when the category is selected and the tracked table is present in `TrackedChangeInventory` with both mapped columns; strict pipeline throws on a missing literal column, default pipeline skips.
- If the shared-descriptor category is selected, emit its `DbIndexKind.Explicit` index with key columns `[Discriminator, ChangeVersion]` sourced from the `SharedDescriptor` table's system columns and named through `ConstraintNaming.BuildExplicitIndexName`.
- Emits nothing else: no per-resource securable, person, or namespace indexes, and no entries for `Resource`/`ConcreteAbstract` tables beyond the five PA tables.
- No DDL-emitter or manifest-emitter code changes are needed; assert the entries flow through `RelationalModelDdlEmitter.EmitIndexes` (both dialects) and `DerivedModelSetManifestEmitter.WriteIndexes` unchanged.
- Preserve the `DbIndexInfo.IncludeColumns` and `DbIndexKind.Explicit` contracts in `compiled-mapping-set.md`, including the tracked PA covering-index use and the query-performance use of `Explicit`.
- If either category is emitted, bump `RelationalMappingVersion` with the locked-hash bless procedure.
- Unit tests (`DeriveIndexInventoryPassTests`, using `CommonInventoryTestSchemaBuilder`) conditionally cover the recorded category dispositions: PA table present/absent and, when adopted, missing mapped columns under strict (throws) and default (skips); shared-descriptor emission and its absence when the category is rejected or the model set has no descriptor resources; when adopted, missing `Discriminator` under strict and default; rejected-category absence; no per-resource emission; existing-inventory preservation after repositioning; deterministic ordering; and identifier-shortening interplay for long tracked-change table names.
- If either category is emitted, regenerate goldens (`UPDATE_GOLDENS=1`, full suite): `Fixtures/authoritative/{sample,ds-5.2,ds-5.2-tpdm}` (pgsql.sql, mssql.sql, ddl.manifest.json, relational-model manifests), `Backend.Ddl.Tests.Unit/Fixtures/ddl-emission` including a new focused tracked-change-index case, `Backend.IntegrationFixtures`, and `RelationalModel.Tests.Unit` fixture families.
- `Backend.{Postgresql,Mssql}.Tests.Integration` generated-DDL authoritative smoke tests prove every adopted category is present and every rejected category is absent.
- After emitting a category, verify that its exact generated DDL is semantically identical to the pinned candidate overlay, then rerun the complete applicable matrix against that generated DDL. A mismatch or failed post-implementation gate blocks acceptance rather than being described as run-to-run noise.
