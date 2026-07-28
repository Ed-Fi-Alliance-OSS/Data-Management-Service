---
jira: DMS-1185
jira_url: https://edfi.atlassian.net/browse/DMS-1185
---

# DMS-1185 Spike Findings: Auth-Check Indexes on `tracked_changes_*` Tables

This document records the DMS-1185 spike's analysis, research evidence, proposed design, and dispositions.
Nothing here changes the current normative design: the spike leaves `reference/design/backend-redesign/design-docs/` untouched, and every design change proposed below is owned by a derived ticket, which merges its adopted parts into the normative docs when it lands.

## Per-strategy catalog

DMS-1185 cataloged the scan surfaces the `ReadChanges` strategies touch, from the runtime SQL generators (`TrackedChangeQueryPlanner`, `TrackedChangeAuthorizationSqlEmitter`, `ReadChangesAuthorizationPlanner`, `AuthObjectDefinitions`): the `/deletes` outer query, the shared-descriptor `/deletes` (always `Discriminator IN (2 values)`), the `/keyChanges` CTE, the tracked-change arms of the four `*IncludingDeletes` views (equality probes collectively spanning five `tracked_changes_edfi` association tables; every people-strategy request evaluates the views matching its person subject kinds, making this the highest-frequency surface), and the `dms.Descriptor` identity probes.
The endpoint envelope is explicit because `/deletes` alone performs recreated-resource suppression, while `/keyChanges` authorizes inside `FilteredChanges` before grouping and the first/last joins against that materialized CTE:

| Strategy | Endpoint | Endpoint-specific predicate / join envelope | View and tracked columns probed | Seek index and derivation source | Disposition |
|---|---|---|---|---|---|
| `RelationshipsWithEdOrgsAndPeopleIncludingDeletes` | `/deletes` | Filter tombstones and the `ChangeVersion` window; apply the EdOrg and all declared person predicates; anti-join the live resource by identity before ordering/paging. | Hierarchy plus Student/Contact/Staff `*IncludingDeletes` views; `Old<EdOrg canonical>` and each `Old<person path>_DocumentId`. | Five tracked PA covering indexes from PA literals mapped through old-value names; outer single-key EdOrg/person indexes from the root canonical-column dual check and `PersonDocumentId` role. | PA view arms evidence-gated in Story 33; outer columns deferred to Story 36. |
| `RelationshipsWithEdOrgsAndPeopleIncludingDeletes` | `/keyChanges` | Filter key-change rows and the window inside `FilteredChanges`; apply the same ANDed subjects before `ChangeWindow` grouping and first/last CTE joins. | Same views and old-value columns as `/deletes`. | Same PA and outer candidates; the `ChangeVersion` PK serves only the base scan, not PostgreSQL's later materialized-CTE joins. | Same split disposition. |
| `RelationshipsWithStudentsOnlyIncludingDeletes` | `/deletes` | Filter tombstones/window; require every Student predicate; anti-join the live resource before ordering/paging. | `EducationOrganizationIdToStudentDocumentIdIncludingDeletes`; `Old<student path>_Student_DocumentId`, including self `OldStudent_DocumentId`. | Tracked `StudentSchoolAssociation` PA covering index plus deferred outer person index derived from `PersonDocumentId`. | View arm evidence-gated in Story 33; outer index deferred to Story 36. |
| `RelationshipsWithStudentsOnlyIncludingDeletes` | `/keyChanges` | Apply the Student predicates inside `FilteredChanges` before grouping and first/last CTE joins. | Same view and old Student columns. | Same PA and outer candidates. | Same split disposition. |
| `RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes` | `/deletes` | Filter tombstones/window; require every Student predicate through responsibility; anti-join the live resource. | `EducationOrganizationIdToStudentDocumentIdDeletedResponsibility`; old Student `DocumentId` columns. | Tracked `StudentEducationOrganizationResponsibilityAssociation` PA covering index plus deferred outer person index. | View arm evidence-gated in Story 33; outer index deferred to Story 36. |
| `RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes` | `/keyChanges` | Apply responsibility-based Student predicates inside `FilteredChanges` before grouping and first/last CTE joins. | Same responsibility view and old Student columns. | Same PA and outer candidates. | Same split disposition. |
| `RelationshipsWithEdOrgsOnly` | `/deletes` | Filter tombstones/window; for every EdOrg subject use direct claim match OR normal hierarchy direction; anti-join the live resource. | Hierarchy view; `Old<EdOrg canonical>`. | Deferred single-key tracked EdOrg index from root securable-element canonical resolution; a PA covering index may already provide equality coverage when its key matches. | Query shape Story 34; index evidence/emission Story 36. |
| `RelationshipsWithEdOrgsOnly` | `/keyChanges` | Apply the same direct-or-normal-hierarchy predicate inside `FilteredChanges` before grouping and first/last CTE joins. | Same hierarchy view and old EdOrg column. | Same deferred EdOrg candidate. | Same disposition. |
| `RelationshipsWithEdOrgsOnlyInverted` | `/deletes` | Filter tombstones/window; preserve direct claim match but swap hierarchy subject/claim direction; anti-join the live resource. | Hierarchy view with source/target swapped; `Old<EdOrg canonical>`. | Same deferred single-key EdOrg derivation; inversion changes the predicate, not the index. | Query shape Story 34; index evidence/emission Story 36. |
| `RelationshipsWithEdOrgsOnlyInverted` | `/keyChanges` | Apply the direct-or-inverted-hierarchy predicate inside `FilteredChanges` before grouping and first/last CTE joins. | Same inverted view probe and old EdOrg column. | Same deferred EdOrg candidate. | Same disposition. |
| `NamespaceBased` | `/deletes` | Filter tombstones/window; AND prefix authorization with any relationship OR-group; anti-join live resource/descriptor identity before ordering/paging. | Per-resource `OldNamespace`, or shared-descriptor `OldNamespace` behind `Discriminator`. | Per-resource pattern-capable index from namespace path/canonical dual classification; shared tracked descriptor `(Discriminator, ChangeVersion)` with Namespace residual. | Per-resource work deferred to Authorization Story 22 + Story 37; shared category evidence-gated in Story 33. |
| `NamespaceBased` | `/keyChanges` | Apply the prefix predicate inside `FilteredChanges` before grouping and first/last CTE joins; shared-descriptor `/keyChanges` is empty by contract. | Per-resource `OldNamespace`; no shared-descriptor key-change probe. | Per-resource pattern-capable index from the same dual classification. | Authorization Story 22 selects the live mechanism; Story 37 adapts and emits tracked indexes. |

`NoFurtherAuthorizationRequired` is the no-index control outside the six-strategy catalog: neither endpoint emits a row-level authorization predicate; `/deletes` retains its tombstone/window/live anti-join envelope and `/keyChanges` retains its `FilteredChanges`/`ChangeWindow` envelope, both served initially by the existing `ChangeVersion` primary key.

All probes are equality except the namespace prefix `LIKE`; on the shared descriptor table every shape is preceded by the `Discriminator IN (2 values)` filter, which is why `Discriminator` leads the Tier-1 candidate there.
Securable coverage in tracked-change tables is root-scoped: array-nested (`[*]`) EdOrg and namespace securable paths have no tracked columns by design and fail closed under the corresponding `ReadChanges` strategies (see § "Per-resource securable-element indexes are deferred" below).
Because strategies OR-combine and subjects AND-combine, single-column indexes are the right granularity; the candidate set and its derivation live in § "Indexes on the `tracked_changes*` tables" below.

## Indexes on the `tracked_changes*` tables

Each tracked-change table is created with a single clustered/primary key on `ChangeVersion`.
That key directly serves the initial `ChangeVersion` window scan and the direct `ChangeVersion` ordering used by `/deletes`.
For `/keyChanges`, it serves the base-table scan that populates `FilteredChanges`; it does not serve PostgreSQL's later `(Id, ChangeVersion)` joins against that multiply referenced, materialized CTE result.
SQL Server may inline or otherwise transform the CTE, but the design does not rely on a provider optimizer reusing the base-table index for those later joins.
It does not serve the `ReadChanges` authorization predicates, and it does not serve the tracked-change arms of the `*IncludingDeletes` authorization views, which collectively probe five `tracked_changes_edfi` association tables by old-value columns; every people-strategy request for any resource evaluates the views matching its person subject kinds, so at least one of these arms runs per such request (see § "Per-strategy catalog" above).

DMS-1185 therefore proposes a bounded set of secondary indexes on tracked-change tables, derived by extending the existing index-inventory pass.
The spike records bounded two-provider research evidence.
Story 33 must first evaluate each category with a pinned SQL overlay against the current generated baseline.
An isolated pass makes a category eligible; if both are eligible, a combined interaction gate determines whether both may be selected together, and only the final selection is implemented and rerun against exact generated DDL.

### Tracked-change extension to `DeriveIndexInventoryPass`

Extend the existing set-level `DeriveIndexInventoryPass` and reposition it immediately after `DeriveTrackedChangeInventoryPass`.
This ordering is mechanically safe: `DeriveTriggerInventoryPass` does not read index inventory, `DeriveTrackedChangeInventoryPass` reads trigger inventory rather than index inventory, and the first pass that needs the already-derived PK/UK entries is the later `DeriveAuthorizationIndexInventoryPass`.
The required sequence is therefore `DeriveTriggerInventoryPass`, `DeriveTrackedChangeInventoryPass`, `DeriveIndexInventoryPass`, `DeriveAuthHierarchyPass`, `DeriveAuthorizationIndexInventoryPass`, `ApplyDialectIdentifierShorteningPass`, then canonical ordering.
The existing PK/UK/FK-support and content-version work remains unchanged; after that work, the pass appends tracked-change `DbIndexInfo` entries to the same shared `IndexesInCreateOrder` inventory only for categories in Story 33's final selection.
DDL emission, manifest emission, identifier shortening, and index-name uniqueness validation therefore continue to apply unchanged.
When at least one category is selected, the pass gains a tracked-index-column strictness flag wired from the same strict/default pipeline choice as `DeriveAuthorizationIndexInventoryPass`: the strict pipeline throws when a required column for a selected category is missing, and the default pipeline skips that category's affected table so synthetic test fixtures continue to build.

The pass defines two independently gated candidate categories.

**1. Tracked PrimaryAssociation covering indexes.**
If Story 33 selects the PA category, emit one `DbIndexKind.Authorization` covering index on each of the five PrimaryAssociation tracked-change tables, mapping the live PA `(key, INCLUDE)` pair through the `Old` value-column naming convention:

| Table (in `tracked_changes_edfi`) | Key | INCLUDE |
|---|---|---|
| `StudentSchoolAssociation` | `OldSchoolId_Unified` | `OldStudent_DocumentId` |
| `StudentContactAssociation` | `OldStudent_DocumentId` | `OldContact_DocumentId` |
| `StaffEducationOrganizationAssignmentAssociation` | `OldEducationOrganization_EducationOrganizationId` | `OldStaff_DocumentId` |
| `StaffEducationOrganizationEmploymentAssociation` | `OldEducationOrganization_EducationOrganizationId` | `OldStaff_DocumentId` |
| `StudentEducationOrganizationResponsibilityAssociation` | `OldEducationOrganization_EducationOrganizationId` | `OldStudent_DocumentId` |

These are the tracked-side mirror of the five live PrimaryAssociation covering indexes and make the tracked-change arms of every `*IncludingDeletes` view servable from the index alone (index-only capable on PostgreSQL, subject to visibility-map coverage; a covering seek without lookups on SQL Server).
Names follow the standard `_Auth` authorization-index convention.
After category selection, gating is per table (the tracked table must be present in the inventory and carry both mapped columns), deliberately not the all-five gating the `*IncludingDeletes` views use: an index without its view is harmless, and the views degrade gracefully without indexes.

**2. Shared-descriptor Discriminator index.**
If Story 33 selects the shared-descriptor category, the `SharedDescriptor` tracked-change table gets one `DbIndexKind.Explicit` index with key order `[Discriminator, ChangeVersion]`.
Every descriptor `/deletes` filters the shared table by `Discriminator IN (<bare>, <qualified>)` plus a `ChangeVersion` window with `ORDER BY ChangeVersion`, for all descriptor kinds sharing the table, so `Discriminator` leads and `ChangeVersion` completes the windowed seek.
Because the two-value `IN` produces one seek range per discriminator, the index returns rows in `ChangeVersion` order only within each range; the global `ORDER BY ChangeVersion` still requires the engine to merge or sort the windowed rows, and implementations must not rely on the index alone for the final ordering.
This shares the `Discriminator`-leading shape of `IX_Descriptor_Discriminator_ContentVersion` on the live shared table; the tracked table carries no `ResourceKeyId`, so `Discriminator` remains its only type qualifier.
After category selection, emit only when the model set contains the `SharedDescriptor` tracked-change table; a shared table present without its `Discriminator` system column raises in the strict pipeline and skips in the default pipeline, mirroring category 1's missing-column contract.
A `(Discriminator, OldNamespace)` variant was measured and rejected: after the `Discriminator` seek, the residual namespace `LIKE` filters a single kind's tombstones, and only two descriptors use `NamespaceBased` `ReadChanges` authorization.

Write cost is bounded by design: tracked-change rows are inserted only on delete and key-change.
Measured on PostgreSQL (DMS-1185), the historical combined candidate added roughly one microsecond per row to bulk tombstone inserts (+69% relative on an index-light table, trivial absolute), with covering-index storage around 30% of table size.
Those combined observations motivate the ceilings but do not satisfy either category's independent cost gate.
Workloads dominated by bulk deletes or cascading key changes (year-end purges, delete-and-reload resyncs) are the only ones that see meaningful overhead.

One PostgreSQL read-side regression is known and blocking: with the tracked PA covering index present, narrow-`ChangeVersion`-window `/deletes` against the five PrimaryAssociation resources can flip from a hash anti-join to a join-filter nested loop (measured 0.5s to 2.5s at 10M tombstones, reproducible under forward and reverse controls).
The root cause is the same `UNION`-view cardinality misestimate that defers the per-resource person and EdOrg indexes below.
No regression exception is granted: the emission story's gates hold every measured shape on both providers to the general regression ceiling and reject the flipped plan shape, so the five-PA covering category is expected to remain blocked on PostgreSQL until the subject-cardinality story's query-shape fix removes the misestimate and a rerun with the pinned candidate overlay shows the flip gone on all five resources.
The shared-descriptor category is not implicated in the flip mechanism (descriptor authorization probes no `UNION` view), so the emission story gates and adopts the two categories independently.
Implementation verification uses the reproducible protocol and numeric gates in the Tier-1 emission story rather than absolute timings tied to the original arm64 macOS Docker host.

### Bounded cross-provider research checkpoint

DMS-1185 used a bounded synthetic comparison to expose provider-specific risks and finalize which questions the implementation stories must answer.
It was run on the same Apple M3 Pro Docker host with PostgreSQL 16.8 arm64 and SQL Server 2025 CU7 x64 under emulation; results are compared only within a provider, never across providers.
The seed contained 100k rows in each of the five tracked PrimaryAssociation tables, 500k shared tracked-descriptor rows, 100k regular Grade rows, 100k live Descriptor rows, 10k live rows for each PrimaryAssociation, and 50 authorization subjects.
Each provider ran twelve comparisons: eight PrimaryAssociation authorization-view probes (four views at single-subject and multi-subject cardinalities), two shared tracked-descriptor window probes, and two live descriptor-identity probes.
Each comparison used five unmeasured warm-ups followed by twenty measured baseline/candidate pairs with alternating execution order, matching result counts, and provider plan/read statistics.
Because SQL Server's timer resolution under emulation rounded very short executions, each recorded SQL Server sample averaged ten identical executions.

The checkpoint did not approve either tracked-index category.
PostgreSQL's contact multi-subject view produced a `1.5532` median paired elapsed-time ratio, SQL Server's staff multi-subject view produced `1.7945`, and the shared tracked-descriptor candidate did not meet the 20% minimum-benefit gate on either provider.
The SQL Server live descriptor candidate did pass its isolated checkpoint: after the one protocol-required rerun of an initially noisy Grade comparison (16.6% median absolute deviation), the Grade probe recorded `0.0206` elapsed and `0.1044` logical-read ratios, while the shared-descriptor probe recorded `0.0405` and `0.0357`; counts matched and neither shape exceeded the `1.20` regression ceiling.

These results are directional research, not implementation acceptance evidence: the profile is bounded and synthetic, SQL Server ran under x64 emulation, and the tracked-index comparisons did not cover exact generated DDL, implementation-scale write amplification, storage, all endpoint/window combinations, or district-scale subject cardinalities.
Story 33 therefore owns the deterministic pre-implementation candidate-overlay gate and the post-implementation exact-generated-DDL gate, while Story 35 owns exact-DDL verification and future implementation of the selected SQL Server-only live descriptor index.

### Per-resource securable-element indexes are deferred

Indexes on the outer `ReadChanges` predicate columns of per-resource tracked-change tables (EducationOrganization securable columns, person `Old*_DocumentId` columns, and namespace columns) are deliberately not emitted yet.
DMS-1185 benchmarks showed that the person and EdOrg indexes are a planner hazard on PostgreSQL as long as the authorization predicate is `IN (SELECT ... FROM <UNION view>)`: PostgreSQL estimates the views' deduplicated output at the default 200 distinct rows (measured actuals 20k-80k), and the resulting misestimates flip plans to per-row nested loops and join-filter anti-joins, with measured regressions up to 18x on `/keyChanges` at 10M tombstones.
The first prerequisite is a runtime query-shape decision that exposes real subject-set cardinalities to the planner where provider evidence shows that it is needed (for example, resolving the claim's person and EdOrg subject sets before composing the main query).
After that lands, a separate story adds the EdOrg and person per-resource index rules to the derivation pass; no new inventory records are required.
Person columns are selected by `Role = PersonDocumentId`; EdOrg and namespace columns both surface as `TrackedChangeColumnInfo.Origin` securable-element flag with `Role = Scalar`, so the pass must distinguish them with the runtime planner's dual check: a column is a namespace column when its `SourceJsonPath` matches one of the resource's `SecurableElements.Namespace` paths, or when its `CanonicalStorageColumn` matches the live column such a path resolves to through `SecurableElementLocationResolver` on the resource's root table (child-table resolutions do not participate; the planner accepts root-table steps only - the rule `ReadChangesAuthorizationPlanner.ResolveTrackedColumnForSecurable` applies, preserving the both-sides-agree contract).
Path matching alone is not safe: key-unification deduplication in the tracked-change inventory merges `Origin` flags but keeps the first contributing `SourceJsonPath`, so a unified namespace column can survive carrying an identity path.

Tracked-change securable coverage is root-scoped by design: `DeriveTrackedChangeInventoryPass` skips array-nested (`[*]`) EdOrg and namespace securable paths, because tracked-change rows are one per document while array-nested securables live on child collection tables (the child-table namespace index cases enumerated in [auth.md](../../design-docs/auth.md) for live-side emission).
A resource whose only applicable EdOrg or namespace securable is array-nested therefore has no corresponding tracked column, and the relationship or `NamespaceBased` `ReadChanges` strategy resolves to the fail-closed security-configuration outcome.
The same is true for a mixed root/array EdOrg configuration: the resolvable root subject must not mask the unrepresentable declared array subject, because dropping it would weaken the strategy's AND composition.
This static configuration failure is distinct from a valid root-scoped subject whose runtime resolution produces no ids; that dynamic empty set is a successful match-nothing result.
The per-resource EdOrg/person index story inherits the same root-scoped boundary.
Related PostgreSQL caveat, tracked separately: the production namespace predicate is a parameterized `LIKE ANY(@array)`, which PostgreSQL never rewrites into index boundary conditions regardless of operator class, and under non-C collations even a single constant-pattern prefix `LIKE` needs `varchar_pattern_ops` to seek; the live-side `IX_*_Namespace_Auth` indexes therefore run as full-index-scan-plus-filter today.
On PostgreSQL, namespace indexes are not worth adding until the predicate shape and the index operator class are addressed together.
SQL Server already seeks parameterized prefix `LIKE`.
A dedicated tracked-namespace story consumes the live Authorization story's selected mechanism, adapts the tracked predicate where needed, and validates tracked namespace indexes independently of the subject-cardinality and EdOrg/person work.

### Tracked-index evidence protocol

Every follow-on index story (Change Query Stories 33, 35, 36, and 37) measures against this shared protocol.
Each story defines only what it evaluates, the shapes that matter, and its category- or predicate-specific expectations; the rules below are not restated per story.

Environment and workload:

- Pin the database image/version, statistics preparation, cache-preparation policy, deterministic generator implementation/version, and integer seed.
- SQL Server gates run at both supported database compatibility levels: 170 for newly created databases and 160 for databases restored from SQL Server 2022-built templates, per the SQL Server 2025 deployment contract. A story that narrows support to one level must record that narrowing explicitly.
- Workload floors: tracked-change tables probed by a category or predicate hold at least ten million rows, district-scale authorization subject sets hold at least fifty thousand subjects (the subject-cardinality story's definition), and qualifying-row counts and window selectivities are pinned in the artifact. A run below these floors cannot establish eligibility.

Measurement, for every read and write A/B shape in every phase (isolated overlays, combined overlays, and post-emission reruns):

1. Use the same provisioned data and statistics for baseline and candidate.
2. Run five unmeasured warm-ups per variant.
3. Record twenty measured pairs, alternating `A → B` and `B → A` order.
4. Record raw elapsed time, the provider execution plan, PostgreSQL buffer counts (the shared hit-plus-read totals reported by `EXPLAIN (ANALYZE, BUFFERS)`) or SQL Server logical reads, and the returned/qualifying row counts.
5. Gate regressions on the median of the twenty paired `candidate / baseline` elapsed-time ratios; wherever a gate accepts a read improvement, use the median of the twenty paired buffer-read or logical-read ratios.
6. Treat a run as noisy when either variant's elapsed-time median absolute deviation divided by its median exceeds 15%; rerun the full comparison once, and leave the decision blocked if the second run is also noisy. A read-based benefit claim is valid only from a run that passed this noise check.

Uniform gates:

- Read regression: every measured shape's median elapsed-time ratio is at or below `1.20`, and no shape acquires a story-prohibited plan form.
- Benefit: at least a 20% improvement in median elapsed time or reads on the story-designated shapes.
- Seek use: every adopted index is exercised as a seek by at least one applicable shape. On PostgreSQL the index appears in an Index Scan or Index Only Scan with its leading column in the index condition rather than a filter; on SQL Server an Index Seek carries seek predicates on the key columns, with no scan of that index and no key lookup where the design claims covering. A candidate that is unused or scan-only fails its category or predicate even when other shapes improved.
- Write: each measured table's bulk-write median elapsed-time ratio is at or below `1.85` on PostgreSQL and `2.00` on SQL Server.
- Storage, measured per table with baseline denominators, never aggregated across tables and never against candidate-state sizes: PostgreSQL compares `pg_relation_size` of each added index against `pg_table_size` of its table captured on the baseline before candidate DDL; SQL Server compares the added index's used pages from `sys.dm_db_partition_stats` against the table's baseline used pages. Each table's added index storage is at or below 40% (PostgreSQL) or 50% (SQL Server) of that table's baseline size.
- Provider blocking: one provider's failure blocks the category or predicate on both providers pending reviewed redesign; failures are not waived as run-to-run noise.
- Independence: no category or predicate may borrow benefit or low cost from another. Isolated overlays establish eligibility; combined overlays gate co-emission, covering reads always and writes on every table carrying indexes from more than one adopted category or predicate; a reviewed subset reruns its own restricted combined overlay.
- Post-emission: the exact generated DDL must be semantically identical to the pinned overlay over the benchmark deployment's tables, and the applicable matrix reruns against that DDL; a mismatch or failed gate blocks acceptance.

Rationale: the `1.20` ceiling and 20% minimum benefit require a material improvement while allowing bounded unrelated-plan variation; PostgreSQL's `1.85`/40% cost ceilings bound the measured 1.69x write ratio and approximately 30% storage with explicit margin; SQL Server uses the more conservative `2.00`/50% ceilings because the spike contains no implementation-scale write-amplification measurement against exact generated DDL.
Absolute times are evidence, not gates, because they vary across hosts and CPU architectures.

### Descriptor identity-lookup index decision

`change-queries.md` § "`*_RefKey` index ordering for `/deletes`" defers a dedicated descriptor identity lookup index in DMS v1 on the assumption that descriptor deletes and recreations are rare.
Per the spike's acceptance criteria, DMS-1185 revisited that decision.
The durable reason for deferral is not that descriptor deletes are rare: the same `(Discriminator, CodeValue, Namespace)` probe shape also runs per tombstone row for every resource `/deletes` whose identity includes a descriptor reference.
It is that `dms.Descriptor` is small and every probe leads with `Discriminator`, so the existing `Discriminator`-leading index `IX_Descriptor_Discriminator_ContentVersion` already bounds the probe cost (`UX_Descriptor_Uri_Discriminator` leads on `Uri` and does not serve this probe).
On PostgreSQL, DMS-1185 measured this at 10M tracked rows and found no observable improvement from a dedicated `(Discriminator, Namespace, CodeValue)` index, so the index remains deferred for that provider.
On SQL Server, DMS-1185's isolated bounded comparison holds the tracked candidate fixed and toggles only the live `(Discriminator, Namespace, CodeValue)` index.
After the protocol-required rerun of the initially noisy Grade probe, the Grade shape recorded a `0.0206` median paired elapsed ratio and `0.1044` logical-read ratio; the shared-descriptor shape recorded `0.0405` and `0.0357`, respectively.
Counts matched and both final comparisons passed the 15% noise and `1.20` regression gates, so the spike selects the SQL Server index for future adoption.
Story 35 owns inventory derivation, emission, exact-generated-DDL verification, and the update to `change-queries.md` § "`*_RefKey` index ordering for `/deletes`" when it lands; this spike makes no product or design-doc change.

### DMS-1185 disposition

The spike concludes with the following category dispositions:

| Category | DMS-1185 disposition | Future acceptance state |
|---|---|---|
| Five tracked PrimaryAssociation covering indexes | Not adopted. Historical PostgreSQL results showed benefit, but the narrow-window anti-join flip remains blocking; the bounded follow-up produced a `1.5532` contact multi-subject regression on PostgreSQL and a `1.7945` staff multi-subject regression on SQL Server. | Story 33 evaluates the PA-only overlay on both providers. Independent read, write, storage, and plan-shape gates determine eligibility; emit all five only when the final disposition selects the category, otherwise assert that all five remain absent. |
| Shared tracked-descriptor `(Discriminator, ChangeVersion)` index | Not adopted. The bounded follow-up did not meet the 20% minimum-benefit gate on either provider. | Story 33 evaluates the shared-descriptor-only overlay on both providers. Independent gates determine eligibility; emit the index only when the final disposition selects the category, otherwise assert its absence. |
| Live descriptor identity `(Discriminator, Namespace, CodeValue)` index | Deferred on PostgreSQL; selected for future SQL Server adoption by the isolated comparison. | Story 35 owns SQL Server-only emission, required seek shape, total-cost gates, and exact-DDL verification. PostgreSQL remains unchanged. |
| Per-resource tracked EdOrg/person indexes | Deferred until the subject-cardinality query shape is selected. | Stories 34 and 36 own the query-shape prerequisite and provider-neutral evidence/emission gate. |
| Per-resource tracked Namespace indexes | Deferred until the live Namespace predicate/index mechanism is selected. | Authorization Story 22 and Change Query Story 37 own the prerequisite and tracked implementation gate. |

The bounded follow-up probes are research inputs, not implementation acceptance evidence.
The spike's charter ends at identifying candidates and quantifying research-level benefit and cost; implementation-scale evidence is deliberately owned by each follow-on ticket's gates, so approving this spike schedules those tickets rather than pre-certifying their outcomes.
For each Story 33 category, the pre-implementation decision uses a pinned category-specific overlay against the current generated baseline.
An isolated pass makes a category eligible; if both are eligible, simultaneous selection also requires the combined interaction gate, and a combined failure requires reviewed selection of at most one.
Only the final selection is derived through the model, and its exact generated DDL must pass the same gates before acceptance.
No category may borrow benefit or low cost from another category.
