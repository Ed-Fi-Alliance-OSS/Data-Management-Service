---
jira: DMS-1185
jira_url: https://edfi.atlassian.net/browse/DMS-1185
---

# Story: Add Shared Foundation for `/deletes` and `/keyChanges`

## Description

Add the shared runtime foundation used by resource and descriptor `/deletes` and `/keyChanges` endpoints.

This includes route/resource resolution, endpoint classification, paging, totalCount handling, response-field mapping, and shared SQL-planning primitives. The resource-specific endpoint behavior is delivered by separate `/deletes` and `/keyChanges` tickets.

The response payload must use identifying field names as they appear in the resource's `queryFieldMapping` in `ApiSchema.json`, preserving the ODS-compatible API contract.

## Acceptance Criteria

- DMS route resolution identifies `/deletes` and `/keyChanges` for known `{schema}/{resource}` pairs from the effective OpenAPI surface.
- Unknown Change Query resource paths return the not-found behavior defined in `change-queries.md`.
- The endpoint foundation resolves the target `ConcreteResourceModel` or descriptor discriminator.
- The foundation resolves the matching `TrackedChangeTableInfo`.
- Shared paging supports `limit` and `offset` consistently with existing GET-many behavior.
- Shared totalCount support counts after endpoint filters and authorization filters.
- Response shaping maps tracked old/new storage columns back to public query-field names from `queryFieldMapping`.
- Descriptor responses use descriptor public identity fields, not internal descriptor IDs.
- Shared SQL planning can compose change-version windows, tombstone/key-change filters, recreated-resource suppression where applicable, paging, totalCount, and authorization predicates.
- Tests cover route classification, resource resolution, descriptor resolution, paging, totalCount, and field-name mapping without duplicating full endpoint behavior.

## Dependencies

- `12-tracked-change-inventory.md`.
- `18-change-version-parameter-validation.md`.
- `20-openapi-change-query-surface.md`.

## Out of Scope

- Implementing `/deletes` query semantics.
- Implementing `/keyChanges` query semantics.
- Applying `ReadChanges` authorization.
