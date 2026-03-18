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
- consumes `WritableRequestBody` when Core applied a writable profile,
- loads/reconstitutes the current stored document for profiled update/upsert flows before invoking the Core-owned stored-state projector,
- flattens JSON into relational row buffers plus logical collection candidates (root + child tables + extensions),
- for update flows that already loaded current state, compares request-derived rowsets to current persisted rowsets and short-circuits successful no-op updates,
- invokes the Core-owned stored-state projector when profile-constrained merge semantics apply,
- persists changed rows in a single transaction using stable-identity merge semantics for collections and profile-aware preservation rules for hidden data,
- surfaces profile contract/runtime failures through dedicated typed error classification rather than DB constraint mapping, and
- relies on database-driven maintenance for derived artifacts required for correctness (propagated reference identity columns, `dms.ReferentialIdentity`, and update-tracking stamps).

Authorization remains out of scope; however, the roundtrip and transaction structures built in this epic should accommodate future authorization queries being batched into the same DB roundtrips. See `reference/design/backend-redesign/design-docs/auth.md` §"Performance improvements over ODS" for the expected roundtrip layout per operation.

Critical-path note: `reference/design/backend-redesign/epics/DEPENDENCIES.md` remains the source of truth for story blocking, but runtime collection-merge execution in this epic does not start until `DMS-1102` / `E15-S04b` (`reference/design/backend-redesign/epics/15-plan-compilation/04b-stable-collection-merge-plans.md`) lands. `DMS-984` consumes that retrofitted merge-plan contract; it must not implement profile-aware stable-identity collection merge execution against the earlier delete-by-parent / `Ordinal`-based write-plan shape. The profile-dependent backend stories in this epic, plus readable profile projection in `DMS-990`, are also blocked on `E07-S01a` / `01a-core-profile-delivery-plan.md`; backend must not guess the missing Core-side profile outputs.

## Stories

- `DMS-981` — `00-core-extraction-location.md` — Core emits concrete JSON locations for document references
- `DMS-982` — `01-reference-and-descriptor-resolution.md` — Bulk resolve `ReferentialId → DocumentId` and validate descriptors
- `TBD` — `01a-core-profile-delivery-plan.md` — Core profile support delivery plan spike (creates the follow-on Core stories that block profiled write/read integration)
- `DMS-1103` — `01b-profile-write-context.md` — Integrate the Core/backend profile write contract
- `DMS-1105` — `01c-current-document-for-profile-projection.md` — Load/reconstitute the current stored document for profiled update/upserts
- `DMS-983` — `02-flattening-executor.md` — Flatten `WritableRequestBody` into row buffers and collection candidates using compiled mapping
- `DMS-984` — `03-persist-and-batch.md` — Persist rows with stable-identity merge semantics and guarded no-op detection (pgsql + mssql); blocked on `DMS-1102` for the executor-facing collection merge-plan contract
- `DMS-985` — `04-propagated-reference-identity-columns.md` — Populate propagated reference identity columns (no reverse-edge table)
- `DMS-986` — `05-write-error-mapping.md` — Map DB constraint errors to DMS error shapes (consistent across dialects)
- `DMS-1104` — `05b-profile-error-classification.md` — Classify and map profile write failures to DMS error shapes
- `DMS-987` — `06-descriptor-writes.md` — Descriptor POST/PUT: maintain `dms.Descriptor` + descriptor referential identities (no per-descriptor tables)
