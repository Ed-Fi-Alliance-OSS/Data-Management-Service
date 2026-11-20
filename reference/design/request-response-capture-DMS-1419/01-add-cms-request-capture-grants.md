---
jira:
epic: DMS-1418
source_spike: DMS-1419
---

# Story: Add Request-Capture Grants to the Configuration Service

## TL;DR

Administrators can turn on request/response capture for one API client for a limited time, and turn it off early, through the Configuration Service.
Every grant is kept, never overwritten, as the record of who enabled capture, for which client, and when, for as long as the client exists; CMS also logs each grant and its end.

## Description

Capture is enabled per API client by a grant in CMS, and every DMS replica polls CMS for the grants that are active.
This story builds the grant: its table, its endpoints, its validation and history, and the read DMS polls.
DMS does nothing with a grant until the grant-polling and capture-pipeline stories land.

Per `reference/design/request-response-capture-DMS-1419/design.md` ("## D2: Control Surface", "### The Grant").

**Citation convention.**
Unprefixed paths are relative to the repository root.
`Config.Frontend/` names `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/`.
`Backend.Postgresql/` and `Backend.Mssql/` name the corresponding directories under `src/config/backend/EdFi.DmsConfigurationService.`.

## Acceptance Criteria

**Schema**

- DbUp scripts `0039` in `Backend.Postgresql/Deploy/Scripts/` and `Backend.Mssql/Deploy/Scripts/` create `dmscs.RequestCaptureGrant` with:
    - `Id`, the grant id
    - `ApiClientId`, a foreign key to the API client that cascades on its deletion
    - `Mode`, `Failures` or `All`
    - `StartsAt` and `ExpiresAt`
    - `CreatedBy`, `EndedAt`, and `EndedBy`
- A grant's tenant is its API client's tenant, through the client's application and vendor.

**Endpoints**

- `POST /v3/apiClients/{id}/requestCapture` (`MapSecuredPost`) creates a grant:
    - the body names a `mode`, defaulting to `Failures`, and a required duration in minutes
    - CMS sets `StartsAt` to the time of creation and `ExpiresAt` to `StartsAt` plus the duration
    - it answers 201 with the grant
- `POST` refuses with 400 a missing, zero, negative, or over-maximum duration, and an unknown mode.
- `POST` refuses with 404 an API client that does not exist in the caller's tenant.
- `POST` refuses with 409 a client that already has an active grant. The rule holds under concurrent requests: the insert transaction locks the API client row before it checks for an active grant, since a unique index cannot express "`ExpiresAt` in the future".
- `DELETE /v3/apiClients/{id}/requestCapture` (`MapSecuredDelete`) ends the client's active grant by setting `EndedAt` and `EndedBy`, and answers 204. With no active grant it answers 404 and changes nothing.
- `GET /v3/apiClients/{id}/requestCapture` (`MapSecuredGet`) returns the client's grants, active, expired, and ended, newest first.
- `GET /v3/requestCaptureGrants/active` (`MapLimitedAccess`) returns every grant of the caller's tenant that is not ended and whose `ExpiresAt` is in the future, with exactly these fields:
    - the grant id
    - the client key, the API client's `ClientId`, not its row id
    - the tenant
    - the mode
    - `StartsAt` and `ExpiresAt`
- The active read is scoped by the `Tenant` header like every other DMS-facing read, and returns no grant of another tenant.

**History and audit**

- `CreatedBy` and `EndedBy` come from `IAuditContext`.
- No code path updates a grant row except the single `EndedAt` and `EndedBy` assignment, which applies only to a row where both are null.
- CMS logs a structured event when a grant is created and when it is ended, carrying the grant id, the API client id, the mode, `StartsAt`, `ExpiresAt`, and the caller, and nothing from a request payload.

**Configuration**

- `RequestCapture:MaximumDurationMinutes` sets the maximum duration, defaulting to 1440. A non-positive value fails startup.

**Tests**

- Units cover duration and expiry validation, the maximum, mode parsing and its default, an unknown client, the 409 conflict, and the audit identity.
- Integration tests on both engines cover:
    - a row ended once is never changed again, and a second `DELETE` answers 404
    - history returns every grant, newest first
    - the active read omits ended and expired grants, and grants of another tenant
    - deleting the API client deletes its grants
    - two concurrent `POST`s for one client create one grant, and the other answers 409
- Authorization tests show the admin scope can create and end a grant, a read-only caller can read history, and any other caller is refused.
- The CMS end-to-end suite covers the HTTP contract of the four endpoints.

## Dependencies

None.

## Estimate

2 SP.
