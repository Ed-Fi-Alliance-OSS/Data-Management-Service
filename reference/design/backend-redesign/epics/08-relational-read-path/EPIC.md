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
- `reference/design/backend-redesign/design-docs/profiles.md` (readable profile projection ownership boundary)
- `reference/design/backend-redesign/design-docs/update-tracking.md` (stored stamps for `_lastModifiedDate/ChangeVersion`, hash-based `_etag`)

The backend reconstitutes the full stored document. When readable profile semantics apply, Core owns the final readable projection; backend serializers do not reimplement profile filtering.

Authorization filtering remains out of scope; however, the hydration and query execution structures built in this epic should accommodate future authorization checks being batched into the same DB roundtrips (for GET-by-id) and authorization filtering being embedded in page selection queries (for GET-many). See `reference/design/backend-redesign/design-docs/auth.md` §"Performance improvements over ODS" for the expected roundtrip layout per operation.

## Stories

- `DMS-989` — `00-hydrate-multiresult.md` — Hydrate root+child tables per page using multi-result sets
- `DMS-990` — `01-json-reconstitution.md` — Reconstitute JSON (ordering, `_ext` overlay, null handling)
- `DMS-991` — `02-reference-identity-projection.md` — Reconstitute reference identity fields from local propagated columns
- `DMS-992` — `03-descriptor-projection.md` — Project descriptor URIs (and optional descriptor fields as needed)
- `DMS-993` — `04-query-execution.md` — Execute root-table-only queries with deterministic paging
- `DMS-994` — `05-descriptor-endpoints.md` — Serve descriptor GET/query endpoints from `dms.Descriptor` (no per-descriptor tables)
- `DMS-622` — `06-link-injection.md` — Inject `{ rel, href }` link objects into reference properties of GET responses (ODS parity)
