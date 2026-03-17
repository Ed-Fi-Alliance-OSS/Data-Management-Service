---
jira: DMS-987
jira_url: https://edfi.atlassian.net/browse/DMS-987
---

# Story: Descriptor POST/PUT Writes Maintain `dms.Descriptor` (No Per-Descriptor Tables)

## Description

Implement descriptor resource write behavior that persists descriptor resources into core tables only:

- `dms.Document` provides `DocumentId`, `DocumentUuid`, `ResourceKeyId`, and update-tracking stamps.
- `dms.Descriptor` stores descriptor fields and derived `Uri`.
- `dms.ReferentialIdentity` stores the descriptor referential id (descriptor type + normalized `Uri`) used for descriptor reference resolution.

Descriptor writes must be compatible with existing DMS descriptor endpoint behavior (create, update of non-identity fields, delete).
For descriptor `PUT`, unchanged non-identity fields should be treated as a successful no-op rather than a representation change.

## Acceptance Criteria

- POST to a descriptor resource:
  - allocates a `DocumentUuid` (if not supplied) and creates/updates `dms.Document` with the descriptor resource `ResourceKeyId`,
  - inserts `dms.Descriptor` keyed by `DocumentId`,
  - inserts/updates the descriptor referential identity row in `dms.ReferentialIdentity`.
- PUT to a descriptor resource:
  - allows updates to non-identity fields (`Description`, `ShortDescription`, `EffectiveBeginDate`, `EffectiveEndDate`, etc.),
  - enforces descriptor identity immutability (reject changes to `Namespace` and `CodeValue` that would change `Uri`),
  - and short-circuits as a successful no-op when the persisted descriptor values are unchanged.
- Descriptor writes do not require any per-descriptor tables and do not flow through the generic “flatten-to-project-schema” write executor.
- Descriptor write errors are mapped consistently (uniqueness conflicts, missing descriptor doc, etc.).
- Integration tests cover:
  - create descriptor,
  - update descriptor non-identity fields,
  - unchanged descriptor PUT that preserves `_etag/_lastModifiedDate/ChangeVersion`,
  - rejection of identity changes (`Namespace`/`CodeValue`),
  - delete descriptor and constraint behavior when referenced.

## Tasks

1. Add a descriptor write handler path keyed by “resource is descriptor” (using the effective schema metadata).
2. Implement derived `Uri` computation (and normalization) consistent with Core and descriptor reference resolution.
3. Implement descriptor referential-id maintenance in `dms.ReferentialIdentity` for descriptor documents.
4. Implement descriptor no-op comparison / guarded update behavior before issuing `UPDATE` against `dms.Descriptor`.
5. Ensure update tracking stamps apply when descriptor representation changes and remain unchanged on successful no-op updates.
6. Add integration coverage for descriptor create/update/delete, no-op update behavior, and identity immutability.
