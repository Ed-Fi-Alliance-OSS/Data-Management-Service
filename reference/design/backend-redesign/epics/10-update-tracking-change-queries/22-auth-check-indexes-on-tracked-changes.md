---
jira: DMS-1185
jira_url: https://edfi.atlassian.net/browse/DMS-1185
---

# Spike: Auth-Check Indexes on `tracked_changes_*` Tables

## Description

The `tracked_changes_*` tables and the shared `tracked_changes_edfi.Descriptor` are emitted with only a clustered/primary key on `ChangeVersion`. `/deletes` and `/keyChanges` apply `ReadChanges` authorization predicates that filter these tables on identity-storage columns (e.g. `OldSchoolId_Unified`, `OldStudent_DocumentId`) and, for descriptor namespace-based strategies, on `OldNamespace` `LIKE` predicates. Without supporting indexes, those predicates fall back to full scans of tables that grow unboundedly with deletes and key-changes.

ODS has the same gap — its `tracked_changes_*` tables also lack the indexes that would back the EdOrg/People auth joins (see [`change-queries.md`](../../design-docs/change-queries.md) "Authorization" section and the recreated-resource anti-join in `/deletes`). DMS preserved that shape in v1; this spike defines what to add and how to derive it.

Refer to `reference/design/backend-redesign/design-docs/change-queries.md` § "Authorization" and § "/deletes endpoints" for the relevant join shapes.

## Acceptance Criteria

- Catalog the per-strategy join shapes used by `/deletes` and `/keyChanges` against `tracked_changes_*` and `tracked_changes_edfi.Descriptor`, covering `RelationshipsWithEdOrgsAndPeopleIncludingDeletes`, `RelationshipsWithStudentsOnlyIncludingDeletes`, `RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes`, `RelationshipsWithEdOrgsOnly`, `RelationshipsWithEdOrgsOnlyInverted`, and `NamespaceBased`.
- For each shape, identify the index that would let it seek rather than scan. Account for descriptor `Discriminator` ordering on the shared table.
- Decide which indexes derive from existing inventory (e.g. `TrackedChangePersonJoinInfo`, `SecurableElements` paths, key-unification canonical columns) and which require new derivation passes.
- Propose a new or extended derivation pass so the indexes are emitted from the derived model rather than ad-hoc per table.
- Quantify expected benefit vs. write-amplification cost. Tracked-change inserts only fire on delete/key-change rows, so the write cost is bounded; the proposal must call out which workloads see meaningful write overhead.
- Revisit the descriptor identity-lookup index that the pre-spike `change-queries.md` § "`*_RefKey` index ordering for `/deletes`" text deferred ("DMS v1 will not add a separate descriptor identity lookup index"). Decide whether this spike subsumes that decision or defers it again.
- Once the proposal is reviewed and approved, create the implementation tickets that derive the inventory entries, emit the DDL for PostgreSQL and SQL Server, and add fixture coverage. Link those follow-on tickets back to this spike.

## Outcome

The candidate design is recorded in `change-queries.md` § "Indexes on the `tracked_changes*` tables" (new) and § "`*_RefKey` index ordering for `/deletes`" (deferral rationale corrected).
PostgreSQL evidence supports the candidate; the provider-neutral decisions remain incomplete until the verification gate below records SQL Server evidence.
Summary against the acceptance criteria:

**Catalog (AC 1-2).**
Five scan surfaces were cataloged from the runtime SQL generators (`TrackedChangeQueryPlanner`, `TrackedChangeAuthorizationSqlEmitter`, `ReadChangesAuthorizationPlanner`, `AuthObjectDefinitions`): the `/deletes` outer query, the shared-descriptor `/deletes` (always `Discriminator IN (2 values)`), the `/keyChanges` CTE, the tracked-change arms of the four `*IncludingDeletes` views (equality probes collectively spanning five `tracked_changes_edfi` association tables; every people-strategy request evaluates the views matching its person subject kinds, making this the highest-frequency surface), and the `dms.Descriptor` identity probes.
Per-strategy shapes (subjects AND within a strategy, strategies OR across, `NamespaceBased` AND-ed with the relationship OR-group):

| Strategy | Direction | Subjects | View probed by the predicate | Tracked `Old*` column probed | Index disposition |
|---|---|---|---|---|---|
| `NoFurtherAuthorizationRequired` | n/a | none | none | none (window + tombstone filters only) | PK on `ChangeVersion` suffices |
| `RelationshipsWithEdOrgsOnly` | Normal | one per EdOrg securable | `auth.EducationOrganizationIdToEducationOrganizationId` (subject `TargetEducationOrganizationId`, claim `SourceEducationOrganizationId`), OR-ed with a direct `= ANY(claims)` arm | `Old<EdOrg canonical column>`, e.g. `OldSchoolId_Unified` | Tier 1 where it leads a PA covering index; otherwise deferred |
| `RelationshipsWithEdOrgsOnlyInverted` | Inverted | one per EdOrg securable | same view with subject/claim columns swapped | same | same |
| `RelationshipsWithEdOrgsAndPeopleIncludingDeletes` | Normal | EdOrg AND Student AND Contact AND Staff securables | hierarchy view plus `...StudentDocumentIdIncludingDeletes`, `...ContactDocumentIdIncludingDeletes`, `...StaffDocumentIdIncludingDeletes` | EdOrg column plus one `Old<person>_DocumentId` per person path | view arms served by Tier 1; outer columns deferred |
| `RelationshipsWithStudentsOnlyIncludingDeletes` | Normal | Student securables | `auth.EducationOrganizationIdToStudentDocumentIdIncludingDeletes` | `Old<path>_Student_DocumentId` (self-identity `OldStudent_DocumentId` on Student) | view arm served by Tier 1; outer column deferred |
| `RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes` | Normal | Student securables | `auth.EducationOrganizationIdToStudentDocumentIdDeletedResponsibility` | same shape | same |
| `NamespaceBased` | n/a | resolved namespace column | none (prefix `LIKE` against token prefixes; `LIKE ANY(@array)` on PostgreSQL, OR-chain on SQL Server) | `OldNamespace` (per-resource), or shared-descriptor `OldNamespace` behind the `Discriminator` filter | shared table covered by the Tier-1 `(Discriminator, ChangeVersion)` candidate; per-resource deferred pending predicate-shape + operator-class work |

All probes are equality except the namespace prefix `LIKE`; on the shared descriptor table every shape is preceded by the `Discriminator IN (2 values)` filter, which is why `Discriminator` leads the Tier-1 index there.
Securable coverage in tracked-change tables is root-scoped: array-nested (`[*]`) EdOrg and namespace securable paths (the child-table cases `auth.md` enumerates for live-side indexing, e.g. GraduationPlan's nested namespace) have no tracked columns by design and fail with a security-configuration outcome under the corresponding `ReadChanges` strategies; a resolvable root path does not mask an unrepresentable array sibling.
Because strategies OR-combine and subjects AND-combine, single-column indexes are the right granularity.

**Derivation (AC 3-4).**
One new pass, `DeriveTrackedChangeIndexInventoryPass`, ordered after `DeriveAuthorizationIndexInventoryPass`; zero new tracked-change inventory contracts (inputs are `TrackedChangeColumnInfo.Origin`, `Role`, `SourceJsonPath`, `CanonicalStorageColumn`, `PersonJoinName`, the PA literals, and shared-descriptor system columns).
The dedicated pass is required because `DeriveIndexInventoryPass` runs before `DeriveTrackedChangeInventoryPass`; that ordering is load-bearing.

**Benefit vs write cost (AC 5).**
The completed measurements are PostgreSQL 16.8 with golden-faithful DDL and planner-exact queries at 2M/10M tombstones, on an arm64 macOS Docker host using tmpfs.
The same candidates remain pending on SQL Server; no PostgreSQL plan exception or timing conclusion transfers automatically to that provider.

| Candidate or decision | PostgreSQL evidence | SQL Server evidence | Current disposition |
|---|---|---|---|
| Five tracked PrimaryAssociation covering indexes | 3.5-10x faster `*IncludingDeletes` view evaluation; approximately 3x on `/keyChanges` and single-school `/deletes` | Pending reproducible A/B | Tier-1 candidate; Story 33 is blocked on the SQL Server gate |
| Shared tracked descriptor `(Discriminator, ChangeVersion)` | 1.5-10x faster descriptor `/deletes` | Pending reproducible A/B | Tier-1 candidate; Story 33 is blocked on the SQL Server gate |
| Narrow-window PA-resource `/deletes` exception | Anti-join flip measured at 0.5s to 2.5s (4.7x in the recorded comparison) | No exception established | PostgreSQL-only conditional exception, capped by the protocol below |
| Per-resource EdOrg/person outer-predicate indexes | Regressions up to 18x while authorization remains `IN (SELECT ... FROM <UNION view>)` | Pending query-shape evaluation | Deferred until the cardinality-visible query-shape story |
| Per-resource namespace indexes | Prefix predicate/index mechanism unresolved | Existing prefix `LIKE` seek behavior is unaffected | Deferred until the Authorization epic's namespace mechanism lands |
| Live descriptor identity `(Discriminator, Namespace, CodeValue)` | No observable improvement at 10M tracked rows | Pending isolated candidate comparison | Deferred on PostgreSQL only; provider-neutral decision remains open |

PostgreSQL write amplification for the Tier-1 candidate was approximately 1 µs/row on bulk tombstone inserts (+69% relative on an index-light table), with covering-index storage around 30% of table size.
Only bulk delete/key-change workloads notice.

**Descriptor identity-lookup (AC 6).**
The probe is far more frequent than the v1 note claimed: it runs per tombstone row for every resource with descriptor identity fields.
PostgreSQL re-defers the candidate because `dms.Descriptor` is small, all probes are `Discriminator`-first, and the dedicated index produced no observable gain at 10M tracked rows.
The SQL Server decision remains open until the protocol below compares descriptor `/deletes` and a regular resource `/deletes` whose identity contains a descriptor while holding the tracked Tier-1 set fixed and toggling only the live descriptor candidate.

**Additional finding.**
Under non-C PostgreSQL collations (the pinned `postgres:16.8-alpine` image initializes `en_US.utf8`), plain B-trees never use prefix `LIKE` as an index boundary; the live-side `IX_*_Namespace_Auth` indexes run as full-index-scan-plus-filter today.
`varchar_pattern_ops` produces true range seeks.
SQL Server is unaffected.

## Provider verification protocol and completion gate

DMS-1185 concludes with a PostgreSQL-supported candidate, not a provider-neutral authorization to implement.
The provider-neutral adoption decision is intentionally gated on Story 33's evidence phase, which owns the checked-in benchmark harness and two-provider result artifact; its implementation phase cannot begin until both providers pass.
The harness may extend the cross-provider workflow from `../12-ops-guardrails/04-performance-benchmarks.md`, but the DMS-1185 artifact must pin the tracked-change-specific schema, seed cardinalities, exact query text and parameters, qualifying-row counts, database image/version, statistics preparation, and cache-preparation policy.

The mandatory matrix covers:

- all four `*IncludingDeletes` views at single-subject and district-scale subject cardinalities;
- descriptor `/deletes` at full and narrow `ChangeVersion` windows;
- regular-resource `/deletes` at full and narrow windows for both a PrimaryAssociation resource and a non-PrimaryAssociation resource using a people strategy;
- `/keyChanges` for those same resource classes and subject cardinalities;
- bulk tombstone/key-change inserts and the resulting candidate-index storage.

For every read or write A/B shape:

1. Use the same provisioned data and statistics for baseline and candidate.
2. Run five unmeasured warm-ups per variant.
3. Record twenty measured pairs, alternating `A → B` and `B → A` order.
4. Record raw elapsed time, the provider execution plan, PostgreSQL buffer counts or SQL Server logical reads, and the returned/qualifying row counts.
5. Use the median of the twenty paired `candidate / baseline` elapsed-time ratios as the gate.
6. Treat a run as noisy when either variant's median absolute deviation divided by its median exceeds 15%; rerun the full comparison once, and leave the decision blocked if the second run is also noisy.

Provider-specific read gates:

- PostgreSQL: every tracked PA index is used by at least one corresponding `*IncludingDeletes` arm, and the four-view matrix demonstrates at least a 20% improvement in median elapsed time or buffer reads. The shared descriptor candidate demonstrates at least a 20% improvement in median elapsed time or buffer reads in at least one descriptor `/deletes` window. Every measured read shape has a median elapsed-time ratio at or below 1.20 except the exact checked-in narrow-window PA-resource `/deletes` fixture, whose anti-join flip is permitted only at or below 5.0. No other shape may acquire that per-row join-filter/nested-loop form.
- SQL Server: no PostgreSQL exception is inherited. Every tracked PA index is used by at least one corresponding `*IncludingDeletes` arm as a covering seek without lookups, and the four-view matrix demonstrates at least a 20% improvement in median elapsed time or logical reads. The shared descriptor candidate demonstrates at least a 20% improvement in median elapsed time or logical reads in at least one descriptor `/deletes` window. Every measured read shape has a median elapsed-time ratio at or below 1.20.
- Live descriptor identity candidate on SQL Server: defer it only if it improves neither median elapsed time nor logical reads by at least 20% in either required descriptor-probe shape. If it reaches that threshold without causing another measured read shape to exceed 1.20, adopt it for SQL Server through the dialect-aware inventory.

Provider-specific cost gates:

- PostgreSQL: the Tier-1 candidate's bulk-write median elapsed-time ratio is at or below 1.85, and its added index storage is at or below 40% of the five PA plus shared-descriptor tracked-table size.
- SQL Server: the Tier-1 candidate's bulk-write median elapsed-time ratio is at or below 2.00, and its added index storage is at or below 50% of the corresponding tracked-table size.

Both providers must pass all applicable benefit, regression, and cost gates before the provider-neutral Tier-1 implementation begins.
A failure blocks emission on both providers until the design is revised with reviewed evidence; the result is not waived as run-to-run noise.
The 1.20 regression ceiling and 20% minimum benefit require a material improvement while allowing bounded unrelated-plan variation; PostgreSQL's 5.0 exception rounds the measured 4.7x result upward, and its 1.85/40% cost ceilings bound the measured 1.69x write ratio and approximately 30% storage with explicit margin.
SQL Server uses the more conservative 2.00/50% cost ceilings because no provider measurement exists yet.
Absolute times are evidence, not gates, because they vary across hosts and CPU architectures.

## Follow-on stories

- `33-tracked-change-index-emission.md` - Produce the provider evidence, then emit Tier-1 auth-check indexes on `tracked_changes_*` tables only if both providers pass
- `../14-authorization/22-namespace-auth-index-prefix-like.md` - Select and implement the PostgreSQL live Namespace predicate/index mechanism
- `34-readchanges-subject-cardinality.md` - Select and implement provider-appropriate subject-cardinality query shapes
- `35-mssql-descriptor-identity-index.md` - Consume the SQL Server descriptor comparison and finalize its dialect-specific index disposition
- `36-per-resource-edorg-person-index-emission.md` - After Stories 33 and 34, emit per-resource EdOrg/person tracked-change indexes
- `37-tracked-namespace-index-emission.md` - After Stories 33 and Authorization 22, adapt the tracked Namespace predicate and emit tracked namespace indexes
