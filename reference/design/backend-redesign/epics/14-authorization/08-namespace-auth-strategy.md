---
jira: DMS-1057
jira_url: https://edfi.atlassian.net/browse/DMS-1057
---

# Story: Implement Namespace-based Authorization Strategy

## Description

Implement the namespace-based authorization strategy for all CRUD operations per:

- `reference/design/backend-redesign/design-docs/auth.md`

## Acceptance Criteria

- GET-many: Results are filtered so that only resources whose Namespace column matches at least one of the API client's configured namespace prefixes (via LIKE prefix match) are returned. Resources with a NULL namespace are excluded.
- GET-by-id: An authorization check is executed against the stored namespace value before reconstitution. If the resource's namespace does not match any of the client's prefixes, a 403 Forbidden response is returned and no reconstitution occurs. If the stored namespace is NULL, the request is unauthorized.
- POST (new resource): An authorization check is executed against the namespace value from the request body before inserting into dms.Document. If unauthorized, the insert does not happen and a 403 Forbidden response is returned.
- POST (upsert as update): Authorization follows the same rules as PUT — check stored values first, then check new values.
- PUT: Two authorization checks are performed:
  - First, authorize using the currently stored namespace value (abort if unauthorized).
  - Second, authorize using the new namespace value from the request body (abort if unauthorized).
- DELETE: An authorization check is executed against the stored namespace value before deletion. If unauthorized, the delete does not happen and a 403 Forbidden response is returned.
- The namespace column to check is resolved from the resource's Namespace securable element in ApiSchema.json. The column is always directly available on the resource's root table (no transitive joins needed).
- When authorization fails, an AUTH1 error is thrown with the strategy index in the message (e.g., 'Unauthorized, index: 0'), aborting the batch and allowing C# to map the failure to the correct strategy for ProblemDetails.
- Auth checks are batched in the same DB roundtrip as other statements (reconstitution, insert, delete, etc.) to match the roundtrip targets in the design doc.
- Works for both PostgreSQL and SQL Server:
  - PostgreSQL: Use `LIKE ANY(ARRAY[...])` with parameterized prefix values.
  - SQL Server: When the client has fewer than 2,000 namespace prefixes, use parameterized OR chains of LIKE clauses. When >= 2,000, throw an error (no TVP is used for namespace prefixes).
- Namespace-based is combined with AND when other strategy types are also configured for the resource. It executes before relationship-based (OR) strategies.
- ProblemDetails follow `auth.md` §"ProblemDetails", specifically:
  - §2.9 — No namespace prefixes configured on the API client.
  - §2.10 — Namespace value uninitialized (existing data).
  - §2.11 — Namespace value missing (proposed data).
  - §2.12 — Namespace mismatch (prefix does not match).
