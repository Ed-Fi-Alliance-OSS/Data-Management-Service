# DMS-853 Resource Action Write Endpoints Design

## Status

Approved in chat on 2026-09-18. This is an implementation-ready specification, not an implementation plan.

## Scope

Add the five Admin API v3-compatible claim-set resource-action write operations to the DMS Configuration Service:

- `POST /v3/claimSets/{claimSetId}/resourceClaimActions`
- `PUT /v3/claimSets/{claimSetId}/resourceClaimActions/{resourceClaimId}`
- `DELETE /v3/claimSets/{claimSetId}/resourceClaimActions/{resourceClaimId}`
- `POST /v3/claimSets/{claimSetId}/resourceClaimActions/{resourceClaimId}/overrideAuthorizationStrategy`
- `POST /v3/claimSets/{claimSetId}/resourceClaimActions/{resourceClaimId}/resetAuthorizationStrategies`

The endpoints must accept the Admin API v3 payload shape and success statuses while translating integer identifiers at the API/application boundary into targeted mutations of the existing `ClaimsHierarchy` object graph.

## Exclusions

- Do not reimplement the DMS-1148 GET endpoints.
- Do not add legacy Admin API association tables such as `ClaimSetResourceClaims`, `ResourceClaimActions`, or `ClaimSetResourceClaimActions`.
- Do not redesign `ClaimsHierarchy`, claim-set import/export, or broader claim-set CRUD.
- Do not introduce DMS-only request payloads in place of the Admin API-compatible contract.
- Do not update broader design/security reference docs unless product explicitly accepts that doc scope. Generated/OpenAPI documentation for these routes is in scope.

## Authoritative Inputs

The story `.plans/ref/DMS-853.md` is authoritative. The human and nano pre-specs plus `.plans/ref/DMS-583-pre-spec-review-2.md` are supporting analysis. Older DMS-853 text and `reference/design/configuration-service/CLAIMSET-MGMT.md` are historical context where they conflict with the current story.

Relevant checked repository anchors:

- `ClaimSetModule` has claim-set CRUD, copy, import, and export but no resource-action write routes.
- `ResourceClaimModule` has the four DMS-1148 read endpoints and is a regression surface only.
- `ClaimsHierarchy` stores claim-set assignments, action grants, and authorization-strategy overrides.
- `ResourceClaimRepository` contains private ID-to-hierarchy projection logic that should be shared or refactored for writes rather than duplicated.
- `IClaimSetRepository.GetActions` and `GetAuthorizationStrategies` provide action and strategy metadata.
- PostgreSQL and MSSQL both have current resource-claim and claim-set repository implementations in this branch, so both datastore paths are in scope unless explicitly descoped.

## Chosen Design

Add a focused claim-set resource-action write surface next to the existing claim-set routes. This may live in `ClaimSetModule` or a small sibling endpoint module if that keeps route code readable. Use the existing `MapSecuredPost`, `MapSecuredPut`, and `MapSecuredDelete` patterns and existing CMS response conventions.

Add Admin API-compatible request models near the claim-set data models. Preserve schema names, JSON property names, integer `claimSetId`, integer `resourceClaimId`, `resourceClaimActions`, `actionName`, `authStrategyIds`, and `authorizationStrategies`. Preserve the ODS schema title typo `OverrideAuthStategyOnClaimSetRequest` for compatibility.

Extend the claim-set repository boundary with targeted operations for grant, modify, revoke, override, and reset. PostgreSQL and MSSQL implementations should use the existing transaction, hierarchy load/save, and optimistic concurrency style already used by claim-set update/delete/import/copy.

Add object-graph mutation methods to `ClaimsHierarchyManager` or a focused hierarchy mutation helper. Mutations operate on `Claim`, `ClaimSet`, and `ClaimSetAction` instances, not JSON strings. The helper must find one resource claim node by resolved claim URI, mutate only that node's claim-set association, and preserve all other hierarchy state.

Expose or add a shared resource-claim ID resolver based on the current `dmscs.ResourceClaim` plus hierarchy projection pattern. The resolver should return the canonical hierarchy claim URI/node for an integer `resourceClaimId` and should not couple write validation to read response projection details beyond the required ID-to-claim lookup.

## Rationale

This is the smallest responsible solution that satisfies the story. It preserves the current DMS storage design, avoids legacy join-table compatibility infrastructure, reuses established secured write and hierarchy concurrency behavior, and directly addresses the known gap: targeted single-resource mutation.

Rejected alternatives:

- Legacy Admin API association tables: explicitly out of scope and inconsistent with the approved DMS claim-set design.
- Calling import/remove-and-recreate for single-resource writes: too broad and known to skip existing entries instead of editing them.
- Direct JSON patch/string manipulation: fragile and unnecessary because the hierarchy already has object models.
- A separate compatibility service/storage path: unnecessary infrastructure and a concurrency risk.

## Contracts

### Grant

`POST /v3/claimSets/{claimSetId}/resourceClaimActions`

Request schema title: `AddResourceClaimActionsOnClaimSetRequest`.

Required fields: `claimSetId`, `resourceClaimId`, `resourceClaimActions`.

Success: `201 Created` with location `/v3/claimSets/{claimSetId}/resourceClaimActions/{resourceClaimId}`.

Behavior: enabled request actions become the persisted action set for the targeted claim-set/resource-claim association. Omitted or disabled actions are not persisted. Repeating the same request is idempotent.

### Modify

`PUT /v3/claimSets/{claimSetId}/resourceClaimActions/{resourceClaimId}`

Request schema title: `EditResourceClaimActionsOnClaimSetRequest`.

Required fields: `claimSetId`, `resourceClaimId`, `resourceClaimActions`.

Success: `204 No Content`.

Behavior: same targeted replacement semantics as grant. Both route/body IDs must match before mutation.

### Revoke

`DELETE /v3/claimSets/{claimSetId}/resourceClaimActions/{resourceClaimId}`

Success: `204 No Content`.

Behavior: remove the target claim-set/resource-claim association, including action-specific overrides nested under that association. Do not remove the global resource claim, default authorization metadata, or unrelated claim-set/resource associations.

### Override Authorization Strategy

`POST /v3/claimSets/{claimSetId}/resourceClaimActions/{resourceClaimId}/overrideAuthorizationStrategy`

Request schema title: `OverrideAuthStategyOnClaimSetRequest`.

Required fields: `claimSetId`, `resourceClaimId`, `actionName`, `authorizationStrategies`.

Success: `200 OK`.

Behavior: `authorizationStrategies` names are canonical for validation and persistence. `authStrategyIds` are accepted for payload parity only when every supplied ID resolves to the same canonical names. IDs without names are invalid. If IDs and names disagree, the request is invalid. The action must resolve to a configured action and must already be enabled for the target association. The override replaces the previous override set for that action.

### Reset Authorization Strategies

`POST /v3/claimSets/{claimSetId}/resourceClaimActions/{resourceClaimId}/resetAuthorizationStrategies`

Request body: none.

Success: `200 OK`.

Behavior: remove all claim-set-specific authorization-strategy overrides for enabled actions on the target association. Reset is idempotent when the target association exists but has no overrides.

## Validation And Failure Handling

Use existing CMS validation and failure response conventions:

- Missing claim set: `404 Not Found`.
- Unresolved resource claim ID or globally existing resource claim that is not associated with the addressed claim set for PUT, DELETE, override, or reset: target association not found, `404 Not Found`.
- System-reserved claim set: `400 Bad Request`.
- Route/body ID mismatch: validation failure, `400 Bad Request`.
- Null or empty `resourceClaimActions`: validation failure.
- Action collection with no enabled actions: validation failure.
- Invalid action names: validation failure.
- Duplicate action names: invalid using case-insensitive canonical comparison.
- Null or empty `authorizationStrategies` for override: validation failure.
- Unknown authorization strategy names or IDs: validation failure.
- Both strategy IDs and names supplied but resolving to different canonical strategy sets: validation failure.
- Override action not enabled for the target association: validation failure.
- Persistent hierarchy optimistic-concurrency conflict: `409 Conflict`.

All validation and lookup failures must occur before saving the hierarchy. Negative tests should prove the hierarchy is unchanged, not only that the response status is correct.

## Data Flow

1. Endpoint performs route/body guard checks and basic request validation.
2. Repository loads claim-set metadata, including name and system-reserved flag, in the current tenant context.
3. Repository resolves `resourceClaimId` through global resource-claim metadata and the hierarchy node projection.
4. Repository loads action and authorization-strategy metadata.
5. Repository loads the single `ClaimsHierarchy`.
6. Hierarchy mutation helper applies one targeted object-graph mutation.
7. Repository saves through `IClaimsHierarchyRepository.SaveClaimsHierarchy` with the current `LastModifiedDate`.
8. Existing conflict/unknown/not-found result mapping produces public responses.
9. Existing claim-set GET/export paths expose the persisted change.

## Preservation Rules

Every operation must preserve:

- unrelated resource claims and hierarchy branches;
- sibling claim-set associations on the same resource claim;
- other claim sets;
- default authorization strategies;
- unrelated action grants and overrides;
- child/parent structure;
- existing ordering where the current object graph and serialization preserve it.

Action replacement on the target association removes actions omitted from the enabled set. Because overrides are nested under `ClaimSetAction`, removing an action also removes that action's overrides. This is intended and avoids stale overrides for disabled actions.

## Compatibility Notes

The ODS Admin API v3 reference proves the five methods, routes, schema titles, fields, and success codes. DMS intentionally differs where the current story is stricter:

- DMS rejects override route/body `resourceClaimId` mismatch.
- DMS requires supplied `authStrategyIds` and `authorizationStrategies` to agree.

These are story-authorized DMS behaviors, not literal ODS behaviors. Tests and API documentation should describe them as DMS contract rules.

Use `System.Text.Json` and current DMS conventions. Do not introduce `Newtonsoft.Json`.

## AC Mapping

| AC | Design behavior | Verification |
| --- | --- | --- |
| AC-01 | POST grant adds/replaces enabled actions for one association and returns 201 with location. | Module/API test for payload/status/location; repository/manager tests for exact enabled-action set, disabled/omitted removal, idempotency, and unrelated preservation. |
| AC-02 | PUT modifies one existing target association and returns 204. | Route/body guard tests; targeted replacement tests; no broad hierarchy reconstruction; idempotent repeat request. |
| AC-03 | DELETE removes only target association and nested overrides. | Manager/repository tests for override removal and preservation of global/default/unrelated state; API status test. |
| AC-04 | POST override resolves names/IDs, rejects disagreement, requires enabled action, stores names, and returns 200. | API and repository tests for names-only success, matching IDs+names success, disagreement failure, invalid ID/name/action failure, disabled action failure, replacement, and read-back. |
| AC-05 | POST reset with no body removes target overrides and returns 200. | API no-body/status test; repository tests for overrides present, no overrides idempotency, absent association not-found, and unrelated preservation. |
| AC-06 | Invalid/unresolved/reserved/mismatched requests fail without mutation. | Negative tests with before/after hierarchy equality and expected 400/404/409 mappings. |
| AC-07 | All operations preserve non-target hierarchy state. | Deep structural preservation tests across siblings, parents, child branches, other claim sets, defaults, overrides, and serialized ordering where applicable. |
| AC-08 | HTTP contract remains Admin API-shaped with POST override/reset. | Served OpenAPI assertions for routes, methods, schema titles/fields, request shapes, and success statuses; API-level contract tests. |
| AC-09 | Writes persist through existing `ClaimsHierarchy` path and are observable through current reads/export. | Write-read/export roundtrip tests and persistence reload coverage. |
| AC-10 | DMS-1148 GET endpoints remain correct and unchanged. | Regression coverage for `ResourceClaimModule` unit tests, E2E feature scenarios, and PostgreSQL/MSSQL resource-claim repository tests as applicable. |
| AC-11 | Coverage spans positive, negative, preservation, idempotency, status codes, ID-to-name resolution, and generated docs. | Combined manager, repository, module/API, OpenAPI, and E2E/API-level test coverage. |

## Dependencies

- Current `ClaimsHierarchy` repository and concurrency behavior.
- Current claim-set, resource-claim, action, and authorization-strategy metadata.
- Current PostgreSQL and MSSQL CMS datastore implementations.
- Existing secured write policies and middleware.
- DMS-1148 GET implementation as a regression surface.

## Approved Deviations

- Keep story's stricter-than-ODS override route/body resource ID validation.
- Keep story's stricter-than-ODS `authStrategyIds` plus `authorizationStrategies` consistency validation.
- Treat target-association absence as not found, not a successful no-op.
- Do not invent duplicate-strategy rejection beyond the required ID/name agreement and canonical persistence rules.
- Detect duplicate actions case-insensitively.

## Remaining Blockers

None for implementation planning.

Optional product scope decision before or during planning: whether to update `reference/design/configuration-service/CLAIMSET-MGMT.md` in this story or track it separately. Generated/OpenAPI documentation remains required either way.

## Handoff To Planning

The implementation plan should start from:

- request models and validators;
- shared resource-claim ID resolution;
- targeted hierarchy mutation helper methods;
- claim-set repository operations for PostgreSQL and MSSQL;
- secured route mapping and response translation;
- focused manager/repository/module/OpenAPI/E2E/regression tests.

Do not begin implementation from this document directly. Use `superpowers:writing-plans` to create the task-by-task implementation plan.
