---
jira: DMS-TBD
jira_url: https://edfi.atlassian.net/browse/DMS-TBD
---

# Spike: Auth-Check Indexes on `tracked_changes_*` Tables

## Description

The `tracked_changes_*` tables and the shared `tracked_changes_edfi.Descriptor` are emitted with only a clustered/primary key on `ChangeVersion`. `/deletes` and `/keyChanges` apply `ReadChanges` authorization predicates that filter these tables on identity-storage columns (e.g. `Old_<EdOrg>_Unified`, `Old_<Person>_DocumentId`) and, for descriptor namespace-based strategies, on `Old_Namespace` `LIKE` predicates. Without supporting indexes, those predicates fall back to full scans of tables that grow unboundedly with deletes and key-changes.

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
