---
jira: DMS-980
jira_url: https://edfi.atlassian.net/browse/DMS-980
---


# Epic: Relational Write Path (POST/PUT)

## Description

Implement the relational write pipeline for POST upsert and PUT by id, per:

- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (flattening design + plan shapes)
- `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md` (reference validation, transaction boundary, propagation/stamping)
- `reference/design/backend-redesign/design-docs/extensions.md` (write-time `_ext` handling)

Core produces validated JSON + extracted references; the backend:
- resolves references/descriptors in bulk,
- flattens JSON into relational row buffers (root + child tables + extensions),
- persists rows in a single transaction with replace semantics for collections,
- relies on database-driven maintenance for derived artifacts required for correctness (propagated reference identity columns, `dms.ReferentialIdentity`, and update-tracking stamps).

Authorization remains out of scope.

## Stories

- `DMS-981` — `00-core-extraction-location.md` — Core emits concrete JSON locations for document references
- `DMS-982` — `01-reference-and-descriptor-resolution.md` — Bulk resolve `ReferentialId → DocumentId` and validate descriptors
- `DMS-983` — `02-flattening-executor.md` — Flatten JSON to row buffers (root/children/_ext) using compiled mapping
- `DMS-984` — `03-persist-and-batch.md` — Persist rows with batching/parameter-limit handling (pgsql + mssql)
- `DMS-985` — `04-propagated-reference-identity-columns.md` — Populate propagated reference identity columns (no reverse-edge table)
- `DMS-986` — `05-write-error-mapping.md` — Map DB constraint errors to DMS error shapes (consistent across dialects)
- `DMS-987` — `06-descriptor-writes.md` — Descriptor POST/PUT: maintain `dms.Descriptor` + descriptor referential identities (no per-descriptor tables)
