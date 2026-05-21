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
- The namespace column to check is resolved from the resource's Namespace securable element in ApiSchema.json. The column is directly available on whichever table owns the reference, with no transitive joins needed. For non-nested paths this is the root resource table; for array-nested paths this is the child collection table that owns the reference.
- When authorization fails, an AUTH1 error is thrown with the strategy index in the message (e.g., 'Unauthorized, index: 0'), aborting the batch and allowing C# to map the failure to the correct strategy for ProblemDetails.
- Auth checks are batched in the same DB roundtrip as other statements (reconstitution, insert, delete, etc.) to match the roundtrip targets in the design doc.
- Works for both PostgreSQL and SQL Server:
  - PostgreSQL: Use `LIKE ANY(ARRAY[...])` with parameterized prefix values.
  - SQL Server: When the client has fewer than 2,000 namespace prefixes, use parameterized OR chains of LIKE clauses. When >= 2,000, throw an error (no TVP is used for namespace prefixes).
- Namespace-based is combined with AND when other strategy types are also configured for the resource. It executes before relationship-based (OR) strategies.
- This story replaces the temporary DMS-1055 GET-many 501 Not Implemented behavior for NamespaceBased in mixed strategy configurations. NamespaceBased is applied as an AND filter with the relationship strategy OR group instead of causing the unsupported mixed-strategy failure.
- This story replaces the temporary DMS-1056 GET-by-id and DELETE 501 Not Implemented behavior for `NamespaceBased` in single-record relational authorization.
- This story closes the DMS-1162 descriptor POST authorization gap introduced while routing relational POST authorization to backend-planned relationship authorization. Descriptor POST must no longer be able to bypass `NamespaceBased` because the generic POST relationship preflight does not run for descriptors.
- Re-enable `@relational-backend` and the appropriate `@relational-ci-shard-*` tag on the NamespaceBased E2E scenarios temporarily excluded during the DMS-1056 EdOrg-only slice because they require NamespaceBased relational CRUD authorization. Restore both tags on:
  - `Features/Descriptors/DescriptorCaseInsensitiveValidation.feature`: scenario 1.
  - `Features/Descriptors/DeleteDescriptorsValidation.feature`: scenario 01.
  - `Features/Authorization/NamespaceAuthorization.feature`: scenarios 01, 03, 04, 10, and 12.
  - `Features/Extensions/TpdmExtension.feature`: scenario 04.
- Re-enable `@relational-backend` and the appropriate `@relational-ci-shard-*` tag on the NamespaceBased E2E scenarios temporarily excluded during DMS-1162 because they require NamespaceBased relational POST/PUT authorization. Restore both tags on:
  - `Features/Profiles/ProfileCollectionFiltering.feature`: scenario 08, "IncludeOnly nested filter profile is currently unsupported on read".
  - `Features/Profiles/ProfileCollectionFiltering.feature`: scenario 09, "ExcludeOnly nested filter profile is currently unsupported on read".
  - `Features/Profiles/ProfileCollectionFiltering.feature`: scenario 10, "IncludeOnly nested filter profile is currently unsupported on write".
  - `Features/Profiles/ProfileCollectionFiltering.feature`: scenario 11, "ExcludeOnly nested filter profile is currently unsupported on write".
  - `Features/Authorization/NamespaceAuthorization.feature`: scenario 09, "Ensure client can create a resource in the ns2 namespace".
  - `Features/Authorization/NamespaceAuthorization.feature`: scenario 11, "Ensure client can update a resource in the ns2 namespace".
- ProblemDetails follow `auth.md` §"ProblemDetails", specifically:
  - §2.9 — No namespace prefixes configured on the API client.
  - §2.10 — Namespace value uninitialized (existing data).
  - §2.11 — Namespace value missing (proposed data).
  - §2.12 — Namespace mismatch (prefix does not match).

## Deferred Write Authorization Guard

DMS-1005 temporarily deferred the fail-closed relational write authorization guard in
`RelationalDocumentStoreRepository` because the guard rejected all write requests whose
effective authorization strategy was not `NoFurtherAuthorizationRequired`. That behavior
blocked current relational E2E setup data for profile tests: descriptor seed POSTs made with
`EdFiSandbox` use `NamespaceBased` authorization, so the guard returned 403 before the
descriptor write handler could persist values such as `EducationOrganizationCategoryDescriptor`
and `GradeLevelDescriptor`.

DMS-1162 expanded relational backend-planned authorization to POST so relationship POST-create
could classify raw strategy names in the backend. Descriptor POST still exits from
`RelationalDocumentStoreRepository.UpsertDocument` into `DescriptorWriteHandler` before generic
resource relationship preflight, and `DescriptorWriteRequest` does not currently carry the raw
authorization strategies. Until this story implements real descriptor namespace authorization,
the current branch should keep descriptor POST out of any backend-planned POST path that would
drop the legacy namespace guard. Do not replace that temporary behavior with a descriptor POST
fail-closed result; descriptor seed writes with matching namespace prefixes must continue to work.

Until this story restores real relational write authorization, `If-Match` handling from DMS-1005 can still return
`412` before a final `403` decision for existing targets; restoring the authorization-before-precondition ordering is
part of closing this deferred guard.

When this story implements `NamespaceBased`, restore write authorization behavior as real
strategy execution, not as the temporary "only NoFurtherAuthorizationRequired is allowed" guard.
Remove the DMS-1057 TODO comments in:

- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalDocumentStoreRepository.cs`
  - `UpsertDocument`
  - `UpdateDocumentById`
  - `DeleteDocumentById`
  - the non-If-Match DELETE execution branch in `DeleteDocumentByIdAsync`

The restored behavior should run in the same transactional/roundtrip shape as the write operation:

- POST create: authorize the proposed namespace from the request body before insert.
- POST upsert-as-update: authorize the stored namespace first, then authorize the proposed namespace before update.
- PUT: authorize the stored namespace first, then authorize the proposed namespace before update.
- DELETE: authorize the stored namespace before delete.
- Descriptor writes must participate in the same NamespaceBased rules. Descriptor POST/PUT should authorize the proposed descriptor `namespace`; descriptor PUT/DELETE should authorize the stored descriptor namespace before mutation.
- Descriptor POST/PUT must carry the effective authorization strategy context into `DescriptorWriteHandler`, or otherwise execute equivalent namespace authorization before the descriptor insert/update path. The implementation must not depend on the generic resource write executor relationship preflight, because descriptors bypass that executor.
- If authorization fails, no insert/update/delete should execute, and the result should map to the AUTH1 ProblemDetails cases listed above.

### Tests To Restore Or Replace

The DMS-1005 unit tests currently document the temporary deferral with names like:

- `It_defers_relational_post_authorization_until_namespace_authorization_is_implemented`
- `It_defers_relational_put_authorization_until_namespace_authorization_is_implemented`
- `It_defers_relational_delete_authorization_until_namespace_authorization_is_implemented`

When NamespaceBased write authorization is implemented, replace those tests with coverage that proves:

- POST, PUT, and DELETE with a matching namespace prefix are allowed to reach the write executor or descriptor handler.
- POST create with a proposed namespace outside the client's prefixes fails before insert.
- POST upsert-as-update fails before update when the stored namespace is unauthorized, even if the proposed namespace would be authorized.
- POST upsert-as-update fails before update when the stored namespace is authorized but the proposed namespace is unauthorized.
- PUT follows the same stored-then-proposed authorization order.
- DELETE fails before delete when the stored namespace is unauthorized.
- Descriptor setup scenarios that create descriptors in `uri://ed-fi.org` with an EdFiSandbox-style token and matching namespace prefix still pass.
- Descriptor POST with a namespace outside the client's configured prefixes fails before the descriptor `dms.Document` and `dms.Descriptor` rows are inserted.
- Descriptor POST through the relational pipeline proves the effective `NamespaceBased` strategy cannot be lost when POST is routed for backend-planned relationship authorization.
- Descriptor PUT fails before mutation when either the stored descriptor namespace or proposed descriptor namespace is unauthorized, following the stored-then-proposed order.
- Descriptor DELETE fails before deletion when the stored descriptor namespace is unauthorized.
- The failing-path tests assert that the executor/delete command is not called and that the result is translated to the expected authorization failure.
