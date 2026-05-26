---
jira: DMS-1182
jira_url: https://edfi.atlassian.net/browse/DMS-1182
---

# Story: Filter Live GET-Many Endpoints by Change Version

## Description

Add `minChangeVersion` and `maxChangeVersion` support to live resource and descriptor GET-many endpoints.

For regular resources, page selection filters the concrete root table's mirrored `ContentVersion` directly. For descriptors, page selection filters `dms.Descriptor.ContentVersion` with the descriptor `Discriminator` predicate. The change-version predicate must not join to `dms.Document`.

Response metadata remains sourced from `dms.Document` for now.

## Acceptance Criteria

- Live regular resource GET-many endpoints accept `minChangeVersion` and `maxChangeVersion`.
- Live descriptor GET-many endpoints accept `minChangeVersion` and `maxChangeVersion`.
- Regular resource page-selection SQL filters the concrete root alias by mirrored `ContentVersion`.
- Descriptor page-selection SQL filters `dms.Descriptor.ContentVersion` and includes the descriptor `Discriminator` predicate.
- The change-version predicate does not require a `dms.Document` join for regular resource page selection.
- The planner uses this path for every resource with a `MirroredContentVersion` column and has no non-mirror fallback for in-scope relational tables.
- `SchoolYearType` uses the live GET-many ChangeVersion filter path; its exclusion from `/deletes` and `/keyChanges` does not exclude it from live `minChangeVersion` and `maxChangeVersion` filtering.
- Pagination and totalCount apply after the change-version filter.
- Query filtering and authorization filtering continue to compose with the change-version predicate.
- Tests assert emitted SQL shape for at least:
  - `ed-fi/students`
  - `ed-fi/schoolYearTypes`
  - one descriptor endpoint
  - one extension-project resource endpoint.
- Integration tests prove resources outside the range are excluded and resources inside the range are returned.

## Out of Scope

- `/deletes`, `/keyChanges`, and `/availableChangeVersions`.
- Serving `_lastModifiedDate` or per-item `ChangeVersion` from the mirror instead of `dms.Document`.
