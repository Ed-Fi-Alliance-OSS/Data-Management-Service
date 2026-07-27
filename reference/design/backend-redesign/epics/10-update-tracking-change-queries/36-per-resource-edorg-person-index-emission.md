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

- Extend `DeriveTrackedChangeIndexInventoryPass` with one single-key `DbIndexKind.Authorization` candidate for each root-scoped non-namespace EdOrg securable scalar column and each `TrackedChangeColumnRole.PersonDocumentId` column, excluding `SharedDescriptor`.
- Support both joined-person columns and zero-hop self-person columns (`PersonJoinName = null`, `CanonicalStorageColumn = DocumentId`, exact self identity path).
- Equality coverage may deduplicate a candidate when a provider-compatible existing PK/UK or index has the same leading column.
- Array-nested EdOrg securables remain outside tracked storage. Only-array-nested and mixed root/array configurations retain the security-configuration behavior specified by Story 34; index derivation does not reinterpret them as dynamic empty sets.
- Run an isolated A/B matrix after Story 34's selected query shapes are fixed. Both providers must demonstrate at least a 20% median elapsed-time or buffer/logical-read improvement on a target EdOrg/person shape, no read regression above 1.20, and no new unstable per-row nested-loop plan before implementation begins.
- Apply the DMS-1185 warm-up, repetition, ordering, and noise controls to bulk tombstone/key-change writes and storage. PostgreSQL must keep the tier's bulk-write median ratio at or below 1.85 and added index storage at or below 40% of the affected tracked-table size; SQL Server must remain at or below 2.00 and 50%, respectively. A provider failure blocks this provider-neutral tier pending redesign.
- Unit tests cover EdOrg scalar, joined-person, self-person, equality dedupe, shared-descriptor exclusion, array-nested exclusion, deterministic ordering, and strict/default missing-column behavior.
- Bump `RelationalMappingVersion`, regenerate the affected DDL/manifests/goldens, and pass PostgreSQL and SQL Server generated-DDL integration smoke tests.
- Namespace predicate changes and tracked namespace indexes are out of scope and owned by Story 37.
