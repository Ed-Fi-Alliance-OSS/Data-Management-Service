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

CMS limits assignments to 1,999 ownership tokens; DMS defensively fails at 2,000 or more when
`OwnershipTokenIds` is evaluated for an existing stored document. A POST that resolves to create does
not evaluate `OwnershipTokenIds`, because no stored ownership token exists yet; it stamps
`CreatorOwnershipTokenId` and proceeds.

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
- A POST that resolves to create does not evaluate `OwnershipTokenIds`, so the 2,000-token defensive
  cap does not deny a true create. A POST that resolves to update applies the cap after target
  resolution and fails closed before DML.
- `OwnershipTokenIds` authorizes reads and mutations, while the single `CreatorOwnershipTokenId` is only for creation stamping.
- Failures use `AUTH1` with the configured strategy index and map to `auth.md` sections 2.13 and 2.14.
- POST and PUT run the ownership check as a statement in the write's first-phase command, after the
  custom-view and namespace statements and before the relationship statement and the current-state
  hydration that command also carries, so statement order is precedence order. It is not in the same
  command as the mutation: the proposed checks and the DML form a second command, because they bind
  values taken from the finalized merged root row, which does not exist until the first command's
  hydration is decoded and the merge runs. A denial aborts the later first-phase statements and the
  second command is never sent, so no DML is issued.
- DELETE co-batches the ownership check into the same command as the deletes on the ordinary path, in
  that same custom-view then namespace then ownership then relationship order. The `AUTH1` abort
  therefore discards the deletes that rode the command with it, which is why a denied delete leaves the
  row present.
- Either arrangement is budget-guarded, so neither is unconditional. When the composite plan does not fit
  the command's parameter budget, ownership runs instead as an ordered segment on the same session and
  transaction, ahead of the hydration or the deletes it would otherwise have shared a command with. The
  guard is provider-independent, but in practice only SQL Server's 2,098 usable parameters
  (`MssqlCommandLimits.MaxUserParametersPerCommand`) are low enough to trip it, and a near-ceiling
  namespace prefix list combined with a large token list can trip it well below the 2,000-token cap. That
  costs commands but preserves authorization-before-mutation ordering and the precedence order above.
- GET-by-id runs the ownership check as one added command ahead of hydration, rather than in the
  operation's database roundtrip. This accepted deviation follows the existing stored-authorization
  GET-by-id shape and is recorded alongside the authorized-GET-by-id one in
  `reference/design/backend-redesign/epics/07-relational-write-path/08-write-roundtrip-batching.md`.
- Ownership executes last among AND strategies and composes with the other configured strategies.
- PostgreSQL and SQL Server are supported; SQL Server uses parameterized `IN` below 2,000 ownership tokens and fails at 2,000 or more without a TVP.

NOTE: GET-many ownership filtering is implemented by [DMS-1410](https://edfi.atlassian.net/browse/DMS-1410) in `11b-ownership-auth-get-many.md`.
