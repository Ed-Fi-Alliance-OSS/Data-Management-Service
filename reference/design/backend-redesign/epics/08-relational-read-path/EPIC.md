# Epic: Relational Read Path (GET + Query)

## Description

Implement the relational read pipeline for:

- GET by id
- GET by query (paged)

per:

- `reference/design/backend-redesign/flattening-reconstitution.md` (reconstitution design + plan shapes)
- `reference/design/backend-redesign/data-model.md` (abstract identity tables, descriptor model)
- `reference/design/backend-redesign/extensions.md` (read-time `_ext` overlay)
- `reference/design/backend-redesign/update-tracking.md` (stored stamps for `_etag/_lastModifiedDate/ChangeVersion`)

Authorization filtering remains out of scope.

## Stories

- `00-hydrate-multiresult.md` — Hydrate root+child tables per page using multi-result sets
- `01-json-reconstitution.md` — Reconstitute JSON (ordering, `_ext` overlay, null handling)
- `02-reference-identity-projection.md` — Reconstitute reference identity fields from local propagated columns
- `03-descriptor-projection.md` — Project descriptor URIs (and optional descriptor fields as needed)
- `04-query-execution.md` — Execute root-table-only queries with deterministic paging
- `05-descriptor-endpoints.md` — Serve descriptor GET/query endpoints from `dms.Descriptor` (no per-descriptor tables)
