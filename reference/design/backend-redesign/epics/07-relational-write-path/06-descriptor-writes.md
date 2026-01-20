# Story: Descriptor POST/PUT Writes Maintain `dms.Descriptor` (No Per-Descriptor Tables)

## Description

Implement descriptor resource write behavior that persists descriptor resources into core tables only:

- `dms.Document` provides `DocumentId`, `DocumentUuid`, `ResourceKeyId`, and update-tracking stamps.
- `dms.Descriptor` stores descriptor fields and derived `Uri`.
- `dms.ReferentialIdentity` stores the descriptor referential id (descriptor type + normalized `Uri`) used for descriptor reference resolution.

Descriptor writes must be compatible with existing DMS descriptor endpoint behavior (create, update of non-identity fields, delete).

## Acceptance Criteria

- POST to a descriptor resource:
  - allocates a `DocumentUuid` (if not supplied) and creates/updates `dms.Document` with the descriptor resource `ResourceKeyId`,
  - inserts `dms.Descriptor` keyed by `DocumentId`,
  - inserts/updates the descriptor referential identity row in `dms.ReferentialIdentity`.
- PUT to a descriptor resource:
  - allows updates to non-identity fields (`Description`, `ShortDescription`, `EffectiveBeginDate`, `EffectiveEndDate`, etc.),
  - enforces descriptor identity immutability (reject changes to `Namespace` and `CodeValue` that would change `Uri`).
- Descriptor writes do not require any per-descriptor tables and do not flow through the generic “flatten-to-project-schema” write executor.
- Descriptor write errors are mapped consistently (uniqueness conflicts, missing descriptor doc, etc.).
- Integration tests cover:
  - create descriptor,
  - update descriptor non-identity fields,
  - rejection of identity changes (`Namespace`/`CodeValue`),
  - delete descriptor and constraint behavior when referenced.

## Tasks

1. Add a descriptor write handler path keyed by “resource is descriptor” (using the effective schema metadata).
2. Implement derived `Uri` computation (and normalization) consistent with Core and descriptor reference resolution.
3. Implement descriptor referential-id maintenance in `dms.ReferentialIdentity` for descriptor documents.
4. Ensure update tracking stamps apply to descriptor writes (descriptor document `_etag/_lastModifiedDate` changes on update).
5. Add integration coverage for descriptor create/update/delete and identity immutability.
