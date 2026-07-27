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
- Propose extensions to `DeriveIndexInventoryPass` so the indexes are emitted from the derived model rather than ad-hoc per table.
- Quantify expected benefit vs. write-amplification cost. Tracked-change inserts only fire on delete/key-change rows, so the write cost is bounded; the proposal must call out which workloads see meaningful write overhead.
- Cover the descriptor identity-lookup index that `change-queries.md` § "`*_RefKey` index ordering for `/deletes`" defers ("DMS v1 will not add a separate descriptor identity lookup index"). Decide whether this spike subsumes that decision or defers it again.
- Once the proposal is reviewed and approved, create the implementation tickets that derive the inventory entries, emit the DDL for PostgreSQL and SQL Server, and add fixture coverage. Link those follow-on tickets back to this spike.

## Outcome

The normative design landed in `change-queries.md` § "Indexes on the `tracked_changes*` tables" (new) and § "`*_RefKey` index ordering for `/deletes`" (deferral rationale corrected).
Summary of decisions against the acceptance criteria:

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
| `NamespaceBased` | n/a | resolved namespace column | none (prefix `LIKE` against token prefixes; `LIKE ANY(@array)` on PostgreSQL, OR-chain on SQL Server) | `OldNamespace` (per-resource), or shared-descriptor `OldNamespace` behind the `Discriminator` filter | shared table served by Tier-1 `(Discriminator, ChangeVersion)`; per-resource deferred pending predicate-shape + operator-class work |

All probes are equality except the namespace prefix `LIKE`; on the shared descriptor table every shape is preceded by the `Discriminator IN (2 values)` filter, which is why `Discriminator` leads the Tier-1 index there.
Securable coverage in tracked-change tables is root-scoped: array-nested (`[*]`) EdOrg and namespace securable paths (the child-table cases `auth.md` enumerates for live-side indexing, e.g. GraduationPlan's nested namespace) have no tracked columns by design and fail closed under the corresponding `ReadChanges` strategies; see `change-queries.md` § "Per-resource securable-element indexes are deferred".
Because strategies OR-combine and subjects AND-combine, single-column indexes are the right granularity.

**Derivation (AC 3-4).**
One new pass, `DeriveTrackedChangeIndexInventoryPass`, ordered after `DeriveAuthorizationIndexInventoryPass`; zero new inventory contracts (inputs are `TrackedChangeColumnInfo.Origin/Role/PersonJoinName`, the PA literals, and shared-descriptor system columns).
The AC's "extend `DeriveIndexInventoryPass`" is honored in spirit, not literally: that pass runs before `DeriveTrackedChangeInventoryPass`, and its position is load-bearing.

**Benefit vs write cost (AC 5), measured on PostgreSQL 16.8 with golden-faithful DDL and planner-exact queries at 2M/10M tombstones, on an arm64 macOS Docker host (tmpfs).**
Timing absolutes are environment- and CPU-architecture-specific (the team develops on Windows/amd64 and Linux); the durable evidence is plan shapes and same-run relative factors, which is how the implementation stories gate.
Adopted (Tier 1): the five tracked PrimaryAssociation covering indexes plus the shared-descriptor `(Discriminator, ChangeVersion)` index; measured 3.5-10x on `*IncludingDeletes` view evaluation, 1.5-10x on descriptor `/deletes`, 3x on `/keyChanges` and single-school `/deletes`.
One bounded read-side regression is known, disclosed in the design doc, and accepted: narrow-window `/deletes` against the five PA resources themselves can flip anti-join shape on PostgreSQL (0.5s to 2.5s at 10M rows, reproducible under forward and reverse controls); same root cause as the Tier-2 deferral, expected to be removed by the subject pre-resolution story.
Deferred (Tier 2): per-resource securable/person/namespace outer-predicate indexes; PostgreSQL's default-200 distinct estimate for the `UNION` views' output makes them plan hazards (reproducible regressions up to 18x on `/keyChanges`), until a runtime query-shape change exposes real subject-set cardinalities.
Write amplification: ~1 µs/row on bulk tombstone inserts (+69% relative on an index-light table), covering-index storage ~30% of table size; only bulk delete/key-change workloads notice.

**Descriptor identity-lookup (AC 6).**
Re-deferred with corrected rationale, and this spike closes the question: the probe is far more frequent than the v1 note claimed (per tombstone row for every resource with descriptor identity fields), but `dms.Descriptor` is small and all probes are `Discriminator`-first, so existing `Discriminator`-leading indexes already bound the cost (no observable gain measured at 10M tracked rows).

**Additional finding.**
Under non-C PostgreSQL collations (the pinned `postgres:16.8-alpine` image initializes `en_US.utf8`), plain B-trees never use prefix `LIKE` as an index boundary; the live-side `IX_*_Namespace_Auth` indexes run as full-index-scan-plus-filter today.
`varchar_pattern_ops` produces true range seeks.
SQL Server is unaffected.

## Follow-on stories

- `33-tracked-change-index-emission.md` - Emit Tier-1 auth-check indexes on `tracked_changes_*` tables
- `34-readchanges-subject-preresolution.md` - Pre-resolve `ReadChanges` authorization subjects and emit per-resource tracked-change indexes
- `35-namespace-auth-index-prefix-like.md` - Make namespace authorization indexes serve prefix `LIKE` on PostgreSQL
