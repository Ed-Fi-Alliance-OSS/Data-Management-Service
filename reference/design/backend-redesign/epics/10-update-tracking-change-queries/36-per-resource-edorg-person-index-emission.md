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
- Enumerate every candidate index in the harness artifact (at current DS 5.2 scale roughly 90 EdOrg scalar and 60 person columns across the per-resource tracked tables) and group the candidates under schema-derivable emission predicates keyed by index class (EdOrg scalar, joined-person, self-person) and key-column scalar type. Emission is decided per predicate because `DeriveIndexInventoryPass` sees only the derived model and never deployment cardinality: every tracked table matching an adopted predicate receives its index deterministically, including extension-project resources and tables absent from the benchmark schema.
- Within each predicate, stratify the benchmarked candidates by tracked-table cardinality tier and name at least one measured representative table per stratum, including each stratum's largest-cardinality table. Predicate membership, strata, and representatives are recorded in the artifact before measurement; strata exist to prove a predicate safe across the cardinality range and never participate in the emission rule.
- After Story 34's selected query shapes are fixed, run an isolated predicate-only overlay for each emission predicate on both providers, over every representative table's applicable shapes: normal and inverted EdOrg strategies, `/deletes` and `/keyChanges`, full and narrow `ChangeVersion` windows, and single-subject and district-scale subject cardinalities. A predicate is eligible only when at least one representative shape in its largest-cardinality stratum demonstrates a 20% median elapsed-time or buffer/logical-read improvement, every measured shape in every stratum stays at or below the 1.20 regression ceiling, and no measured shape acquires a new unstable per-row nested-loop plan. Isolation comes from the predicate-only overlay, not post-hoc attribution; predicates may not borrow benefit or low cost from one another.
- Rerun the read matrix with the combined overlay of all eligible predicates; every measured shape must stay at or below the 1.20 ceiling with no prohibited plan shape before implementation begins. A combined failure preserves the isolated results, blocks simultaneous emission, and returns the selection for review, which may adopt a subset of the eligible predicates. An adopted subset containing more than one predicate must rerun the combined overlay and pass the same ceilings before implementation; a single-predicate subset is covered by its isolated result. Members of a non-adopted or unmeasured predicate are not emitted.
- Apply Story 33's warm-up, repetition, ordering, and noise controls to bulk tombstone/key-change writes on each representative table in the isolated overlays: each measured table's bulk-write median elapsed-time ratio must stay at or below 1.85 on PostgreSQL and 2.00 on SQL Server.
- Verify storage exhaustively rather than by representative: after building the full adopted index set at implementation scale, every affected tracked table must keep its added per-resource index storage at or below 40% of that table's size on PostgreSQL and 50% on SQL Server. A provider failure blocks the affected predicates on both providers pending redesign.
- Unit tests cover EdOrg scalar, joined-person, self-person, equality dedupe, shared-descriptor exclusion, array-nested exclusion, deterministic ordering, and strict/default missing-column behavior.
- Bump `RelationalMappingVersion`, regenerate the affected DDL/manifests/goldens, and pass PostgreSQL and SQL Server generated-DDL integration smoke tests.
- Namespace predicate changes and tracked namespace indexes are out of scope and owned by Story 37.
