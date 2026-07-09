---
jira: DMS-1174
jira_url: https://edfi.atlassian.net/browse/DMS-1174
---

# Story: Emit `*_RefKey` Indexes with `DocumentId` Last

## Description

Move `DocumentId` to the last column in every generated `*_RefKey` unique index and matching composite foreign-key target.

The `/deletes` endpoint suppresses tombstones for resources that were deleted and recreated under a new `DocumentId`. That probe joins the current live table by the resource's identifying storage values, not by `DocumentId`. Placing `DocumentId` first makes the uniqueness index poorly shaped for this query. Keeping `DocumentId` in the index but moving it last preserves the uniqueness contract and improves the recreated-resource anti-join path.

## Acceptance Criteria

- Generated `*_RefKey` unique indexes order key columns as canonical public-identity storage columns first, complete
  intrinsic lineage anchors next, and target `DocumentId` last.
- Composite foreign keys that target `*_RefKey` use the same target-column order.
- The uniqueness contract for resource reference keys is unchanged.
- DDL fixture coverage proves the ordering for:
  - a simple resource,
  - a resource with descriptor identity values,
  - a resource using key-unification storage columns,
  - an extension-project resource.
- Existing reference and descriptor resolution tests continue to pass.
- The change is reflected consistently in PostgreSQL and SQL Server DDL.

## Out of Scope

- Adding new descriptor identity lookup indexes for descriptor `/deletes`.
- Adding tracked-change table auth-performance indexes.
