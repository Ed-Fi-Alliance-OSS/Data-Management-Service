---
jira: DMS-1286
jira_url: https://edfi.atlassian.net/browse/DMS-1286
---

# Story: Add Real-MSSQL Integration Coverage for NamespaceBased CRUD Authorization

## Description

Exercise the existing NamespaceBased authorization implementation through the production MSSQL backend and a
real SQL Server. Shared compiler and unit coverage is not sufficient to certify provider exception decoding,
authorization/mutation ordering, SQL Server parameter limits, and rollback safety.

`DMS-1057` remains the owner of NamespaceBased semantics. This story validates the MSSQL provider boundary and
fixes only defects required to preserve that existing contract.

## Coverage Matrix

| Operation | Required MSSQL behavior |
| --- | --- |
| GET-many | Include matching namespaces; exclude non-matching, null, and empty values before paging and `totalCount`. |
| GET-by-id | Deny mismatches and uninitialized stored namespaces before hydration or document exposure. |
| POST create | Authorize the proposed namespace and write no document, descriptor, or resource rows on denial. |
| PUT / POST-as-update | Authorize stored values before proposed values and before precondition/mutation handling; preserve all rows on denial. |
| DELETE | Authorize the stored namespace before `If-Match` evaluation or deletion and preserve all rows on denial. |
| Descriptors | Apply the same proposed/stored NamespaceBased contract for supported descriptor operations. |
| Mixed strategies | Compose NamespaceBased as an AND condition around the relationship-strategy OR group. |

## Provider Failure Contract

- Exercise actual `SqlException` failures produced by the generated MSSQL authorization path.
- Decode valid `AUTH1 - ns1|...` payloads into the established typed namespace mismatch, stored-uninitialized,
  proposed-missing, or stale-target outcomes.
- Malformed or unmappable payloads fail closed through the established 500 Security Configuration Error
  ProblemDetails response. Do not expose raw provider text or SQL details.
- Add a provider-specific extractor only if real-engine tests demonstrate that generic `SqlException.Message`
  parsing is insufficient or unstable.

## Parameter Limits

Use the established NamespaceBased SQL Server contract from `DMS-1057`: supported prefix counts execute with
parameterized predicates, while an over-limit request fails deterministically before partial authorization or
mutation. Do not introduce a namespace-prefix TVP as part of this coverage story.

## Acceptance Criteria

- Real-SQL-Server integration tests cover matching success and mismatch denial for GET-many, GET-by-id, POST,
  PUT, POST-as-update, and DELETE.
- Ordinary resources and descriptors are represented for the operation families they support.
- Authorization filtering precedes paging and `totalCount`; single-record denial precedes hydration.
- Stored authorization precedes proposed authorization and precondition/mutation work for update operations;
  stored DELETE authorization precedes `If-Match` evaluation and deletion.
- For both ordinary resources and descriptors, an existing DELETE target for which NamespaceBased
  authorization and `If-Match` would both fail returns 403 rather than 412. After authorization succeeds, the
  normal `If-Match` result is preserved. Collision tests cover both orderings against real SQL Server.
- Every denied write leaves `dms.Document`, descriptor, root, child, extension, identity, and tracking state
  unchanged.
- Mixed NamespaceBased/relationship authorization uses the established AND/OR composition.
- Valid MSSQL `AUTH1` payloads map to typed failures; malformed payloads return sanitized security-configuration
  failures.
- Supported prefix counts run successfully, and over-limit requests fail before issuing a partial write.
- Required tests run in configured MSSQL CI and fail on an unavailable or misconfigured SQL Server.
- Existing PostgreSQL NamespaceBased tests continue to pass unchanged.

## Non-Goals

- Reimplementing NamespaceBased authorization.
- Building the MSSQL Docker E2E runner or duplicating its public-boundary scenario matrix (`DMS-1284`).
- ReadChanges NamespaceBased authorization.
- Ownership-based or custom view-based authorization.
- Authorization performance optimization (`DMS-1065`).
- Changing public ProblemDetails text except to correct a demonstrated MSSQL parity defect.

## Design References

- [`../../design-docs/auth.md`](../../design-docs/auth.md)
- [`../14-authorization/08-namespace-auth-strategy.md`](../14-authorization/08-namespace-auth-strategy.md)
- [`../14-authorization/20-configuration-problem-details.md`](../14-authorization/20-configuration-problem-details.md)
- [`04-mssql-docker-e2e.md`](04-mssql-docker-e2e.md)
