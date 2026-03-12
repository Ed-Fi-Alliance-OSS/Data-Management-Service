---
jira: DMS-1063
jira_url: https://edfi.atlassian.net/browse/DMS-1063
---

# Story: Implement View-based Authorization Strategy for GET-by-id, POST, PUT, and DELETE

## Description

Implement the view-based (custom) authorization strategy for single-record operations (GET-by-id, POST, PUT, DELETE) per:

- `reference/design/backend-redesign/design-docs/auth.md`

## Acceptance Criteria

- GET-by-id: An EXISTS subquery is executed against the custom auth view using the stored basis resource DocumentId before reconstitution. If the resource's basis resource key is not found in the view, a 403 Forbidden is returned and no reconstitution occurs.
- GET-by-id with nullable mapping: If the basis resource maps to a nullable/non-PK column in the resource and the stored value is NULL, the request is unauthorized and returns 403 Forbidden.
- POST (new resource): An EXISTS subquery is executed against the custom auth view using the basis resource DocumentId resolved from the request body values. If unauthorized, the insert does not happen and a 403 Forbidden is returned.
- PUT: Two authorization checks are performed:
  - First, authorize using the currently stored basis resource DocumentId (abort if unauthorized).
  - Second, authorize using the new basis resource DocumentId from the request body (abort if unauthorized).
- DELETE: An authorization check is executed against the stored basis resource DocumentId before deletion. If unauthorized, the delete does not happen and a 403 Forbidden is returned.
- The basis resource is extracted from the strategy name using the `{BasisResource}With{SomeDescription}` convention, prioritizing standard resources over extensions.
- The join path from the resource table to the basis resource is resolved using `ResolveSecurableElementColumnPath(sourceResourceFullName, targetResourceFullName)`.
- When authorization fails, an AUTH1 error is thrown with the strategy index in the message (e.g., 'Unauthorized, index: N'), aborting the batch and allowing ProblemDetails generation.
- Auth checks are batched in the same DB roundtrip as other statements (reconstitution, insert, delete, etc.) to match the roundtrip targets in the design doc.
- View-based strategies are combined with AND semantics alongside other AND strategies and execute before relationship-based (OR) strategies.
- Works for both PostgreSQL and SQL Server, using the dms.throw_error function in PostgreSQL and the CAST('AUTH1 - ...' AS INT) pattern in SQL Server for aborting batches.
- ProblemDetails follow `auth.md` §"ProblemDetails", specifically §2.4 (no relationships without EdOrg claims), §2.7 (custom view element uninitialized), and §2.8 (custom view element missing).
- When the custom auth view does not exist or returns invalid columns, DMS returns HTTP 500 with `type: urn:ed-fi:api:system` (same as ODS behavior). See `auth.md` §"View-based authorization strategy".
- Tests cover the next scenarios:
  - Basis resource = descriptor
  - Basis resource = Student
  - Basis resource = target resource
  - Basis resource = concrete education organization (like School)
  - Basis resource = abstract resource (like EducationOrganization)
