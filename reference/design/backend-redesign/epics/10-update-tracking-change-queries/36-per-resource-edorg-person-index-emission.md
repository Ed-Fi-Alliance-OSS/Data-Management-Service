---
jira: TBD
jira_url: TBD
---

# Story: Emit Per-Resource EdOrg and Person Tracked-Change Indexes

## Description

Story 34 establishes the provider-appropriate `ReadChanges` relationship query shape and freezes its cross-provider performance baseline.
This story then evaluates and emits only the per-resource EdOrg and person tracked-change indexes, keeping index effects isolated from the runtime rewrite and from Namespace prefix work.
It depends on Stories 33 and 34.

## Acceptance Criteria

- Extend the tracked-change portion of `DeriveIndexInventoryPass` with one single-key `DbIndexKind.Authorization` candidate for each root-scoped non-namespace EdOrg securable scalar column and each `TrackedChangeColumnRole.PersonDocumentId` column, excluding `SharedDescriptor`.
- Support both joined-person columns and zero-hop self-person columns (`PersonJoinName = null`, `CanonicalStorageColumn = DocumentId`, exact self identity path).
- Equality coverage may deduplicate a candidate when a provider-compatible existing PK/UK or index has the same leading column.
- Array-nested EdOrg securables remain outside tracked storage. Only-array-nested and mixed root/array configurations retain the security-configuration behavior specified by Story 34; index derivation does not reinterpret them as dynamic empty sets.
- Enumerate every candidate index in the harness artifact (at current DS 5.2 scale roughly 90 EdOrg scalar and 60 person columns across the per-resource tracked tables) and partition the candidates into pinned equivalence groups keyed by index class (EdOrg scalar, joined-person, self-person), key-column scalar type, and tracked-table cardinality tier. Each group names at least one measured representative table, which must include the group's largest-cardinality table; group membership and representatives are recorded in the artifact before measurement.
- Run the A/B matrix after Story 34's selected query shapes are fixed, on both providers, with the full candidate set applied, over every representative table's applicable shapes: normal and inverted EdOrg strategies, `/deletes` and `/keyChanges`, full and narrow `ChangeVersion` windows, and single-subject and district-scale subject cardinalities. Every equivalence group must demonstrate at least a 20% median elapsed-time or buffer/logical-read improvement on at least one of its representative shapes, every measured shape must stay at or below the 1.20 regression ceiling, and no measured shape may acquire a new unstable per-row nested-loop plan before implementation begins.
- Emission is per equivalence group: a group is emitted only when its representatives pass every read, write, and plan-shape gate on both providers; members of a failing or unmeasured group are not emitted, a rerun after excluding failed groups must still pass every gate before implementation begins, and groups may not borrow benefit or low cost from one another.
- Apply Story 33's warm-up, repetition, ordering, and noise controls to bulk tombstone/key-change writes on each representative table: each measured table's bulk-write median elapsed-time ratio must stay at or below 1.85 on PostgreSQL and 2.00 on SQL Server.
- Verify storage exhaustively rather than by representative: after building the full emitted index set at implementation scale, every affected tracked table must keep its added per-resource index storage at or below 40% of that table's size on PostgreSQL and 50% on SQL Server. A provider failure blocks the affected groups on both providers pending redesign.
- Unit tests cover EdOrg scalar, joined-person, self-person, equality dedupe, shared-descriptor exclusion, array-nested exclusion, deterministic ordering, and strict/default missing-column behavior.
- Bump `RelationalMappingVersion`, regenerate the affected DDL/manifests/goldens, and pass PostgreSQL and SQL Server generated-DDL integration smoke tests.
- Namespace predicate changes and tracked namespace indexes are out of scope and owned by Story 37.
