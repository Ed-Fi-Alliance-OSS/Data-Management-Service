---
jira: DMS-1186
jira_url: https://edfi.atlassian.net/browse/DMS-1186
---

# Story: Serve `/deletes` and Establish Shared Change Query Endpoint Foundation

## Description

Implement resource and descriptor `/deletes` endpoints backed by tracked-change
tombstone rows, and establish the shared runtime foundation used by both
`/deletes` and `/keyChanges`.

The endpoint returns deleted resource identifying values in the requested change-version window. It must hide tombstones for resources or descriptors that have been recreated with the same identifying values, including resources whose identity references descriptors that were themselves recreated.

The response contract must remain compatible with ODS.

This story subsumes the retired `22-change-query-endpoint-foundation.md`
(`DMS-1185`) so the shared foundation is proven by the first concrete endpoint
implementation. `24-keychanges-endpoint.md` reuses this foundation for
`/keyChanges`.

## Acceptance Criteria

### Shared endpoint foundation

- DMS route resolution identifies `/deletes` and `/keyChanges` by classifying the trailing path segment and resolving `{schema}/{resource}` through the effective resource model.
- DMS route resolution identifies `/deletes` and `/keyChanges` for known resources regardless of whether the endpoints are emitted in the effective OpenAPI Change Query surface.
- Known resources excluded from the OpenAPI Change Query surface, such as `SchoolYearType`, resolve as known Change Query requests and do not take the unknown-resource not-found path. The final `403` behavior is covered by `26-school-year-type-readchanges-forbidden.md` after `ReadChanges` authorization is applied.
- Unknown `{schema}/{resource}` pairs (resources not present in the effective resource model) return the not-found behavior defined in `change-queries.md`.
- The endpoint foundation resolves the target `ConcreteResourceModel` or descriptor discriminator.
- The foundation resolves the matching `TrackedChangeTableInfo`.
- Shared paging supports `limit` and `offset` consistently with existing GET-many behavior.
- Shared totalCount support counts after endpoint filters and, once `ReadChanges` is applied, authorization filters.
- Response shaping maps tracked old/new storage columns back to public query-field names from `queryFieldMapping`.
- Descriptor reference public fields in shaped responses compose the tracked `Namespace` and `CodeValue` values as a single string in `"<namespace>#<codeValue>"` form.
- Descriptor responses use descriptor public identity fields, not internal descriptor IDs.
- Internal descriptor IDs are not returned in descriptor identity fields or descriptor reference fields.
- Shared SQL planning can compose change-version windows, tombstone/key-change filters, recreated-resource suppression where applicable, paging, totalCount, and authorization predicates when supplied by `25-readchanges-authorization.md`.
- Tests cover route classification, resource resolution, descriptor resolution, known-resource resolution for OpenAPI-excluded resources such as `SchoolYearType`, paging, totalCount, and field-name mapping without duplicating full endpoint behavior.

### `/deletes` behavior

- Each regular resource with Change Query support can serve `GET /data/v3/{schema}/{resource}/deletes`.
- Each descriptor with Change Query support can serve `GET /data/v3/{schema}/{descriptor}/deletes`.
- The endpoint filters tracked-change rows to tombstones by requiring an appropriate `New_*` identity column to be null.
- The endpoint filters by `minChangeVersion` and `maxChangeVersion`.
- The endpoint supports `limit`, `offset`, and `totalCount`.
- Regular resource recreated-resource suppression anti-joins against the live table using identifying storage values, not `DocumentId`.
- Descriptor recreated-resource suppression anti-joins against `dms.Descriptor` using `Discriminator`, `Namespace`, and `CodeValue`.
- Resource suppression resolves descriptor identity references by joining current `dms.Descriptor` rows on stored `Namespace` and `CodeValue`, so recreated descriptors do not cause false delete results.
- Response `keyValues` use public field names from `queryFieldMapping`.
- Descriptor reference values inside `keyValues` compose the tracked `Namespace` and `CodeValue` values as a single string in `"<namespace>#<codeValue>"` form.
- Descriptor `/deletes` responses include public descriptor identity fields only.
- Cascading delete scenarios for abstract-resource families are covered, including a scenario comparable to ODS-4087.
- Tests cover regular resources, descriptors, recreated resources, recreated descriptors, pagination, totalCount, and both dialects.

## Dependencies

- `11-refkey-documentid-ordering.md`.
- `12-tracked-change-inventory.md`.
- `16-tracked-change-trigger-rendering.md`.
- `17-delete-by-id-tombstone-ordering.md`.
- `18-change-version-parameter-validation.md`.
- `20-openapi-change-query-surface.md`.

## Out of Scope

- `ReadChanges` authorization, handled by `25-readchanges-authorization.md`.
- `/keyChanges` query semantics, handled by `24-keychanges-endpoint.md`.
- Snapshot support.
