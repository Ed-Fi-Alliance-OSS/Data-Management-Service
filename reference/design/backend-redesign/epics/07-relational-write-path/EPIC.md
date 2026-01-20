# Epic: Relational Write Path (POST/PUT)

## Description

Implement the relational write pipeline for POST upsert and PUT by id, per:

- `reference/design/backend-redesign/flattening-reconstitution.md` (flattening design + plan shapes)
- `reference/design/backend-redesign/transactions-and-concurrency.md` (reference validation, transaction boundary, propagation/stamping)
- `reference/design/backend-redesign/extensions.md` (write-time `_ext` handling)

Core produces validated JSON + extracted references; the backend:
- resolves references/descriptors in bulk,
- flattens JSON into relational row buffers (root + child tables + extensions),
- persists rows in a single transaction with replace semantics for collections,
- relies on database-driven maintenance for derived artifacts required for correctness (propagated reference identity columns, `dms.ReferentialIdentity`, and update-tracking stamps).

Authorization remains out of scope.

## Stories

- `00-core-extraction-location.md` — Core emits concrete JSON locations for document references
- `01-reference-and-descriptor-resolution.md` — Bulk resolve `ReferentialId → DocumentId` and validate descriptors
- `02-flattening-executor.md` — Flatten JSON to row buffers (root/children/_ext) using compiled mapping
- `03-persist-and-batch.md` — Persist rows with batching/parameter-limit handling (pgsql + mssql)
- `04-propagated-reference-identity-columns.md` — Populate propagated reference identity columns (no reverse-edge table)
- `05-write-error-mapping.md` — Map DB constraint errors to DMS error shapes (consistent across dialects)
- `06-descriptor-writes.md` — Descriptor POST/PUT: maintain `dms.Descriptor` + descriptor referential identities (no per-descriptor tables)
