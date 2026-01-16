# Story: Enforce `If-Match` Using Derived `_etag`

## Description

Implement optimistic concurrency checks using derived `_etag`:

- For update operations that support `If-Match`, compare the client-provided `_etag` to the current derived `_etag`.
- Stabilize dependency identity tokens during the check using shared locks on `dms.IdentityLock` rows as needed (per update-tracking concurrency notes).

## Acceptance Criteria

- When `If-Match` equals the current derived `_etag`, the write proceeds.
- When `If-Match` does not match, the request fails with the appropriate HTTP error semantics (e.g., precondition failed).
- The check is representation-sensitive and reflects dependency identity changes.

## Tasks

1. Implement a “compute current derived `_etag`” path usable by update handlers prior to write.
2. Implement `If-Match` comparison and error mapping.
3. Add concurrency stabilization via shared locks where required by the design.
4. Add tests for:
   1. match success,
   2. mismatch failure,
   3. mismatch caused by dependency identity change.

