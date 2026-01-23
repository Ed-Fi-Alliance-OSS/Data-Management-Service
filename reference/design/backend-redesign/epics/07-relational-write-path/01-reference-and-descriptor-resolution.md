---
jira: DMS-982
jira_url: https://edfi.atlassian.net/browse/DMS-982
---

# Story: Bulk Reference and Descriptor Resolution (Write-Time Validation)

## Description

Implement request-scoped resolution and validation for all extracted references during POST/PUT:

- Deduplicate referential ids across the request.
- Resolve `ReferentialId → DocumentId` in bulk via `dms.ReferentialIdentity`.
- Validate descriptors via `dms.Descriptor` (and expected discriminator/type in application code where required).
- Provide actionable error reporting that includes the reference’s concrete JSON location.

Align with `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md` (“Reference Validation”).

## Acceptance Criteria

- All referential id lookups are performed in bulk (no per-reference DB queries).
- Missing referenced documents cause the request to fail with an error that identifies the reference JSON path instance.
- Descriptor validation ensures the referenced `DocumentId` is present in `dms.Descriptor` (and optionally matches the expected discriminator).
- Resolution uses per-request memoization so duplicates do not cause duplicate work.

## Tasks

1. Implement a request-scoped resolver that accepts extracted references and returns `DocumentId`s.
2. Implement dialect-specific bulk lookup patterns (IN/TVP/array) with parameter-limit handling.
3. Implement descriptor validation queries and discriminator checks.
4. Add unit/integration tests covering:
   1. dedupe behavior,
   2. missing reference failure with path,
   3. descriptor type mismatch failure.

