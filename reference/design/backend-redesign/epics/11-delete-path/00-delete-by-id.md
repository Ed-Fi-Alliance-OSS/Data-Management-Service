---
jira: DMS-1010
jira_url: https://edfi.atlassian.net/browse/DMS-1010
---

# Story: Implement Delete-by-Id for Relational Store

## Description

Implement `DeleteDocumentById` behavior for the relational primary store:

1. Resolve `DocumentUuid → DocumentId`.
2. Delete from `dms.Document` (cascades to resource tables, identities, edges).
3. Rely on FK constraints to prevent deleting referenced documents.

Align with `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md`.

## Acceptance Criteria

- Deleting an existing document removes the `dms.Document` row and cascades remove resource rows and derived identity rows.
- Deleting a non-existent document returns a “not found” result without error.
- Deletes run in a transaction and roll back on conflict/failure.

## Authorization Batching Consideration

Authorization is out of scope for this story, but the delete transaction should be designed to allow authorization check statements to be prepended within the same roundtrip. For DELETE, the authorization check against stored values is batched alongside the delete statement in a single roundtrip. See `reference/design/backend-redesign/design-docs/auth-redesign.md` §"Performance improvements over ODS" (DELETE roundtrip #2).

## Tasks

1. Implement `DocumentUuid → DocumentId` resolution in the relational backend.
2. Implement the delete transaction against `dms.Document`.
3. Add integration tests validating successful delete and not-found behavior.

