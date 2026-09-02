---
jira: DMS-1060
jira_url: https://edfi.atlassian.net/browse/DMS-1060
---

# Story: Implement Ownership-based Authorization for GET-by-id, POST, PUT, and DELETE

## Description

Implement ownership-based authorization for single-record operations per:

- `reference/design/backend-redesign/design-docs/auth.md`

`CreatorOwnershipTokenId` and `OwnershipTokenIds` come from the tenant-qualified CMS
`ApplicationContext`, not JWT claims. DMS resolves and caches this context through
`GET /v3/apiClients/{clientId}`.

CMS limits assignments to 1,999 ownership tokens; DMS defensively fails at 2,000 or more.

## Acceptance Criteria

- POST stamps every newly created document from `CreatorOwnershipTokenId`, including resources not configured with `OwnershipBased`; a missing creator token stamps null.
- Descriptors: stamping is in scope, enforcement is not. A descriptor create stamps
  `CreatedByOwnershipTokenId` like any other create, because stamping never consults the configured
  strategies. Ownership *enforcement* on descriptors is out of scope: a descriptor GET-by-id, POST, PUT or
  DELETE whose action is configured with `OwnershipBased` stays fail-closed at `501`, so that configuration
  is refused rather than honoured. The operations named in this story's title are those of relationally
  stored resources.
- GET-by-id checks the stored token before hydration and reconstitution and returns 403 on null or mismatch.
- PUT checks the stored token before mutation and never changes it.
- DELETE checks the stored token before deletion.
- `OwnershipTokenIds` authorizes reads and mutations, while the single `CreatorOwnershipTokenId` is only for creation stamping.
- Failures use `AUTH1` with the configured strategy index and map to `auth.md` sections 2.13 and 2.14.
- The write and delete paths co-batch the ownership check into the operation's own command: it is appended to the
  same composite command as the guarded mutation or delete, after the custom-view and namespace statements and
  before the relationship statement, so statement order is precedence order and the `AUTH1` abort discards the
  later statements in that command.
- The co-batch is budget-guarded, so it is not unconditional. When the composite plan does not fit the command's
  parameter budget, ownership runs instead as an ordered segment on the same session and transaction, ahead of the
  mutation or delete. The guard is provider-independent, but in practice only SQL Server's 2,098 usable parameters
  (`MssqlCommandLimits.MaxUserParametersPerCommand`) are low enough to trip it, and a near-ceiling namespace prefix
  list combined with a large token list can trip it well below the 2,000-token cap. That costs commands but
  preserves authorization-before-mutation ordering and the precedence order above.
- GET-by-id is the one path that never co-batches: the ownership check is one additional command, because it is
  not an `AUTH1` statement against the carrier the namespace and relationship checks share, and it runs ahead of
  hydration so a denial is decided before any representation is built. Accepted deviation, recorded alongside the
  authorized-GET-by-id one in
  `reference/design/backend-redesign/epics/07-relational-write-path/08-write-roundtrip-batching.md`.
- Ownership executes last among AND strategies and composes with the other configured strategies.
- PostgreSQL and SQL Server are supported; SQL Server uses parameterized `IN` below 2,000 ownership tokens and fails at 2,000 or more without a TVP.

NOTE: GET-many ownership filtering is implemented by [DMS-1410](https://edfi.atlassian.net/browse/DMS-1410) in `11b-ownership-auth-get-many.md`.
