---
jira: DMS-1158
jira_url: https://edfi.atlassian.net/browse/DMS-1158
---

# Story: Implement People-involved Relationship-based Authorization for GET-by-id, POST, PUT, and DELETE

## Description

Implement People-involved relationship-based authorization for GET-by-id, POST, PUT, and DELETE by consuming the shared People relationship authorization core from [DMS-1056](https://edfi.atlassian.net/browse/DMS-1056) and the GET-many People integration from [DMS-1095](https://edfi.atlassian.net/browse/DMS-1095), per:

- `reference/design/backend-redesign/design-docs/auth.md`

## Dependencies

- Depends on [DMS-1095](https://edfi.atlassian.net/browse/DMS-1095).
- DMS-1095 depends on [DMS-1056](https://edfi.atlassian.net/browse/DMS-1056), so this story also inherits the shared People relationship authorization core.

## Acceptance Criteria

- The following relationship-based strategies are implemented for GET-by-id, POST, PUT, and DELETE:
  - `RelationshipsWithEdOrgsAndPeople`
  - `RelationshipsWithEdOrgsAndPeopleInverted`
  - `RelationshipsWithPeopleOnly`
  - `RelationshipsWithStudentsOnly`
  - `RelationshipsWithStudentsOnlyThroughResponsibility`
- GET-by-id: An authorization check is executed using `EXISTS` subqueries against the stored values before reconstitution. If unauthorized, a 403 Forbidden response is returned with appropriate ProblemDetails; no reconstitution occurs.
- POST (new resource): An authorization check is executed against the proposed values from the request body before inserting into `dms.Document`. If unauthorized, the insert does not happen and a 403 Forbidden response is returned.
- POST (existing resource / upsert as update): Authorization follows the same rules as PUT - check stored values first, then check proposed values when the resource allows updates to its identifying values.
- PUT: Two authorization checks are performed:
  - First, authorize using the currently stored values; abort if unauthorized.
  - Second, authorize using the proposed values from the request body only if the resource allows updates to its identifying values; abort if unauthorized.
  - Both checks are batched in the same roundtrip as reference resolution and reconstitution.
- DELETE: An authorization check is executed against the stored values before deletion. If unauthorized, the delete does not happen and a 403 Forbidden response is returned.
- People-related securable elements (Student, Contact, Staff) use the DocumentId join-path, auth-view, through-responsibility, hint, and parameterization metadata supplied by DMS-1056.
- For the EducationOrganization portion of mixed EdOrg-and-People strategies, use the ODS-parity DMS concrete root-table subject scope established by DMS-1055; child-table EdOrg predicates are not introduced by this story unless explicitly added by a later design change.
- Inverted strategies correctly swap Source/Target filtering in `EXISTS` subqueries for the EducationOrganization hierarchy table.
- When multiple relationship-based strategies are configured for the same resource, they are combined with OR semantics. The `EXISTS` clauses for each strategy are wrapped in parentheses and combined with OR.
- Multiple securable subjects inside one relationship strategy are combined with AND semantics.
- When authorization fails, the `AUTH1` error code is thrown with the strategy index in the message (e.g., `Unauthorized, index: 0`), allowing the C# code to map the failure to the specific strategy and generate ProblemDetails.
- When multiple relationship-based OR strategies are configured and authorization fails, all OR strategies are evaluated and their error hints are combined/concatenated in the ProblemDetails response, not just the first failure. See `auth.md` "Authorization Failure Hints" for the hint table and formatting rules.
- ProblemDetails follow the structure defined in `auth.md` "ProblemDetails", including:
  - The `type`, `title`, `detail`, `errors`, and `correlationId` fields per RFC 9457.
  - Securable element paths are translated to user-friendly names (e.g., `$.studentReference.studentUniqueId` -> `StudentUniqueId`).
  - When a single securable element is involved, the error uses the singular form; when multiple are involved, the error uses the plural form and lists all elements.
  - EdOrg claims are shown in the error, up to 5 claims followed by `...`.
- Auth checks are batched with other statements in the same DB roundtrip (e.g., reconstitution for GET-by-id, delete for DELETE, insert for POST) to match the roundtrip targets defined in the design doc.
- Works for both PostgreSQL and SQL Server, using the `dms.throw_error` function in PostgreSQL and the `CAST('AUTH1 - ...' AS INT)` pattern in SQL Server for aborting batches.
- For SQL Server, when the token's EdOrgId list has fewer than 2,000 entries, use a parameterized `IN` clause; otherwise, use a TVP of type `dms.BigIntTable`.
- This story replaces the temporary not-implemented behavior for People-involved relationship-based GET-by-id, POST, PUT, and DELETE operations.
