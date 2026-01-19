# Story: Enforce `If-Match` Using Stored Representation Stamps

## Description

Implement optimistic concurrency checks using stored representation stamps:

- For update operations that support `If-Match`, compare the client-provided `_etag` to the current `_etag` served from `dms.Document.ContentVersion`.
- No dependency locking is required because indirect impacts are materialized as local updates that bump the same stamp.

## Acceptance Criteria

- When `If-Match` equals the current `_etag`, the write proceeds.
- When `If-Match` does not match, the request fails with the appropriate HTTP error semantics (e.g., precondition failed).
- The check is representation-sensitive and reflects dependency identity changes.

## Tasks

1. Implement a “read current stored `_etag`” path usable by update handlers prior to write.
2. Implement `If-Match` comparison and error mapping.
3. Add tests for:
   1. match success,
   2. mismatch failure,
   3. mismatch caused by dependency identity change.
