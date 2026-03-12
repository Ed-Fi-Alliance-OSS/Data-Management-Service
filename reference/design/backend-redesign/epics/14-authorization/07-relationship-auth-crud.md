---
jira: DMS-1056
jira_url: https://edfi.atlassian.net/browse/DMS-1056
---

# Story: Implement Relationship-based Authorization Strategies for GET-by-id, POST, PUT, and DELETE

## Description

Implement the relationship-based authorization strategies for the GET-by-id, POST, PUT, and DELETE scenarios per:

- `reference/design/backend-redesign/design-docs/auth.md`

## Acceptance Criteria

- GET-by-id: An authorization check is executed (using EXISTS subqueries) against the stored values before reconstitution. If unauthorized, a 403 Forbidden response is returned with appropriate ProblemDetails; no reconstitution occurs.
- POST (new resource): An authorization check is executed against the values from the request body before inserting into dms.Document. If unauthorized, the insert does not happen and a 403 Forbidden response is returned.
- POST (existing resource / upsert as update): Authorization follows the same rules as PUT — check stored values first, then check new values (if the resource allows updates to its identifying values).
- PUT: Two authorization checks are performed:
  - First, authorize using the currently stored values (abort if unauthorized).
  - Second, authorize using the new values from the request body (only if the resource allows updates to its identifying values; abort if unauthorized).
  Both checks are batched in the same roundtrip as reference resolution and reconstitution.
- DELETE: An authorization check is executed against the stored values before deletion. If unauthorized, the delete does not happen and a 403 Forbidden response is returned.
- Each strategy type correctly determines which securable elements participate (same rules as [DMS-1055](https://edfi.atlassian.net/browse/DMS-1055)).
- Inverted strategies correctly swap Source/Target filtering in EXISTS subqueries.
- When multiple relationship-based strategies are configured for the same resource, they are combined with OR semantics (the EXISTS clauses for each strategy are wrapped in parentheses and combined with OR).
- When authorization fails, the AUTH1 error code is thrown with the strategy index in the message (e.g., 'Unauthorized, index: 0'), allowing the C# code to map the failure to the specific strategy and generate ProblemDetails.
- When multiple relationship-based (OR) strategies are configured and authorization fails, all OR strategies are evaluated and their error hints are combined/concatenated in the ProblemDetails response (not just the first failure). See `auth.md` §"Authorization Failure Hints" for the hint table and formatting rules.
- ProblemDetails follow the structure defined in `auth.md` §"ProblemDetails", including:
  - The `type`, `title`, `detail`, `errors`, and `correlationId` fields per RFC 9457.
  - Securable element paths are translated to user-friendly names (e.g. `$.schoolReference.schoolId` → `SchoolId`).
  - When a single securable element is involved, the error uses the singular form; when multiple, the plural form listing all elements.
  - EdOrg claims are shown in the error (up to 5, then `...`).
- Auth checks are batched with other statements in the same DB roundtrip (e.g., reconstitution for GET-by-id, delete for DELETE, insert for POST) to match the roundtrip targets defined in the design doc.
- Works for both PostgreSQL and SQL Server, using the throw_error function in PostgreSQL and the CAST('AUTH1 - ...' AS INT) pattern in SQL Server for aborting batches.
- For SQL Server, when the token's EdOrgId list has fewer than 2,000 entries, use a parameterized IN clause; otherwise, use a TVP.
