# Story: Project Descriptor URIs from `dms.Descriptor`

## Description

Implement read-time projection of descriptor URI strings:

- Descriptor values are stored as `..._DescriptorId` FKs.
- Responses must include the descriptor URI string at the descriptor JSON path.

Descriptor identities (URIs) are treated as immutable in this redesign (`reference/design/backend-redesign/data-model.md`).

## Acceptance Criteria

- For each `..._DescriptorId` column, the response contains the descriptor URI string from `dms.Descriptor.Uri`.
- Descriptor nullability rules are respected (null fk → no descriptor string).
- Projection is batched (no per-row descriptor lookups).

## Tasks

1. Implement batched descriptor lookup by `DocumentId` for all `..._DescriptorId` values in a page/read.
2. Integrate descriptor URI projection into reconstitution at the correct JSON paths.
3. Add unit/integration tests for descriptor projection behavior (required and optional descriptors).
