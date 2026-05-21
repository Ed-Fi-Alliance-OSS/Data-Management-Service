---
jira: DMS-1008
jira_url: https://edfi.atlassian.net/browse/DMS-1008
---

# Story: Ensure Descriptor Writes Stamp Correctly (`dms.Descriptor`)

## Description

When descriptor resources are stored in `dms.Descriptor`, updates to descriptor fields must still:

- bump the descriptor document’s stored representation stamps on `dms.Document`, and

Successful descriptor updates that produce no persisted-row changes must not bump stamps.

This story ensures update tracking remains correct for descriptor resources without requiring per-descriptor tables.

## Acceptance Criteria

- INSERT/UPDATE/DELETE of a descriptor resource causes correct update-tracking behavior for the descriptor document:
  - updates to descriptor fields bump `dms.Document.ContentVersion/ContentLastModifiedAt`,
  - unchanged descriptor PUT leaves `dms.Document.ContentVersion/ContentLastModifiedAt` unchanged,
  - identity immutability is enforced so `Uri` does not change (unless explicitly supported later),
- Trigger coverage includes `dms.Descriptor` changes (not just project-schema root/child/_ext tables).
- Integration tests validate:
  - descriptor PUT changes `_etag/_lastModifiedDate/ChangeVersion` for the descriptor document,
  - descriptor updates do not require any dependency expansion or special casing at read time.

## Tasks

1. Emit per-dialect triggers/functions so updates to `dms.Descriptor` stamp the owning `dms.Document` row.
2. Add an integration test that:
   1. creates a descriptor,
   2. updates a non-identity field,
   3. asserts stored stamps,
   4. repeats the same PUT and asserts stamps remain unchanged.

## Transition Notes

`DMS-1005` currently stamps descriptor representation changes manually in `DescriptorWriteHandler` by updating
`dms.Document.ContentVersion` and `dms.Document.ContentLastModifiedAt` after changed `dms.Descriptor` updates. That is
a temporary bridge for `If-Match` correctness until this story moves descriptor stamping ownership into
`dms.Descriptor` triggers.

