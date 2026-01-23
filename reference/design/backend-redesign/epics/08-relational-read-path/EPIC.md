---
jira: DMS-988
jira_url: https://edfi.atlassian.net/browse/DMS-988
---


# Epic: Relational Read Path (GET + Query)

## Description

Implement the relational read pipeline for:

- GET by id
- GET by query (paged)

per:

- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (reconstitution design + plan shapes)
- `reference/design/backend-redesign/design-docs/data-model.md` (abstract identity tables, descriptor model)
- `reference/design/backend-redesign/design-docs/extensions.md` (read-time `_ext` overlay)
- `reference/design/backend-redesign/design-docs/update-tracking.md` (stored stamps for `_etag/_lastModifiedDate/ChangeVersion`)

Authorization filtering remains out of scope.

## Stories

- `DMS-989` — `00-hydrate-multiresult.md` — Hydrate root+child tables per page using multi-result sets
- `DMS-990` — `01-json-reconstitution.md` — Reconstitute JSON (ordering, `_ext` overlay, null handling)
- `DMS-991` — `02-reference-identity-projection.md` — Reconstitute reference identity fields from local propagated columns
- `DMS-992` — `03-descriptor-projection.md` — Project descriptor URIs (and optional descriptor fields as needed)
- `DMS-993` — `04-query-execution.md` — Execute root-table-only queries with deterministic paging
- `DMS-994` — `05-descriptor-endpoints.md` — Serve descriptor GET/query endpoints from `dms.Descriptor` (no per-descriptor tables)
