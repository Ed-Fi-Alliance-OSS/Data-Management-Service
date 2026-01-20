# Story: Ensure Descriptor Writes Stamp and Journal Correctly (`dms.Descriptor`)

## Description

When descriptor resources are stored in `dms.Descriptor`, updates to descriptor fields must still:

- bump the descriptor document’s stored representation stamps on `dms.Document`, and
- emit the corresponding `dms.DocumentChangeEvent` journal row (via triggers on `dms.Document` when `ContentVersion` changes).

This story ensures update tracking remains correct for descriptor resources without requiring per-descriptor tables.

## Acceptance Criteria

- INSERT/UPDATE/DELETE of a descriptor resource causes correct update-tracking behavior for the descriptor document:
  - updates to descriptor fields bump `dms.Document.ContentVersion/ContentLastModifiedAt`,
  - identity immutability is enforced so `Uri` does not change (unless explicitly supported later),
  - journal rows are emitted for descriptor document representation changes.
- Trigger coverage includes `dms.Descriptor` changes (not just project-schema root/child/_ext tables).
- Integration tests validate:
  - descriptor PUT changes `_etag/_lastModifiedDate/ChangeVersion` for the descriptor document,
  - descriptor updates do not require any dependency expansion or special casing at read time.

## Tasks

1. Emit per-dialect triggers/functions so updates to `dms.Descriptor` stamp the owning `dms.Document` row.
2. Ensure existing `dms.Document` journaling triggers emit journal rows when `ContentVersion` changes for descriptor documents.
3. Add an integration test that:
   1. creates a descriptor,
   2. updates a non-identity field,
   3. asserts stored stamps and journal behavior.
