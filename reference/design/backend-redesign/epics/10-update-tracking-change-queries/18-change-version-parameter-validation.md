---
jira: Unassigned
---

# Story: Validate Change-Version Query Parameters

## Description

Implement shared validation for `minChangeVersion` and `maxChangeVersion` on Change Query endpoints and live resource or descriptor GET-many endpoints.

Validation must preserve the ODS-compatible ProblemDetails shape described in `change-queries.md`.

## Acceptance Criteria

- `minChangeVersion` and `maxChangeVersion` are parsed as integer values greater than or equal to `0`.
- If both values are supplied, `minChangeVersion` must be less than or equal to `maxChangeVersion`.
- Invalid `minChangeVersion` parsing returns `400 Bad Request` with error text `MinChangeVersion must be a numeric value greater than or equal to 0.`
- Invalid `maxChangeVersion` parsing returns `400 Bad Request` with error text `MaxChangeVersion must be a numeric value greater than or equal to 0.`
- A range where `minChangeVersion > maxChangeVersion` returns `400 Bad Request` with error text `MinChangeVersion must be less than or equal to MaxChangeVersion.`
- The ProblemDetails response uses:
  - type `urn:ed-fi:api:bad-request:parameter-validation-failed`
  - title `Parameter Validation Failed`
  - detail `Parameters supplied to the request were invalid.`
- Validation is shared by live GET-many, `/deletes`, and `/keyChanges` rather than duplicated in each handler.
- Tests cover valid ranges, missing bounds, invalid values, negative values, and inverted ranges.

## Dependencies

- Existing request validation middleware and ProblemDetails infrastructure.

## Out of Scope

- Snapshot-header validation.
- Feature-disabled ProblemDetails.
- Authorization failures.
