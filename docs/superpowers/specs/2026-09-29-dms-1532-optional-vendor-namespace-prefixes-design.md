# DMS-1532: Optional Vendor Namespace Prefixes

## Status and authority

**Status:** Conversational design approved on 2026-09-29; written specification awaiting review.

The authoritative requirement is [`.plans/ref/DMS-1532.md`](../../../.plans/ref/DMS-1532.md). The approved DMS-1356 vendor-update design governs the existing PUT workflow. The human and nano pre-specs and their review are supporting analysis, not requirements.

When sources disagree, this specification uses the following order:

1. DMS-1532 acceptance criteria and the decisions approved during brainstorming.
2. The approved [DMS-1356 vendor namespace-update design](../../../reference/design/configuration-service/DMS-1356-vendor-namespace-update-consistency.md).
3. Current code on branch `DMS-1532`.
4. Supporting pre-spec analysis and historical references.

## Objective

Allow `POST /v3/vendors` and `PUT /v3/vendors/{id}` to accept an empty string or an omitted `namespacePrefixes` property while preserving the existing 128-character per-prefix limit, identity-provider consistency guarantees, token contract, and fail-closed DMS authorization behavior.

## Approved decisions

- `namespacePrefixes: ""` and an omitted property are valid for POST and PUT.
- Explicit JSON `null` remains invalid. This intentionally stops short of full Admin API null parity.
- The existing 128-character CMS limit remains authoritative; the story does not adopt Admin API's different validation.
- AC4 is documented in the ticket outcome and executable regression evidence. No new public OpenAPI or operator prose is required.
- A client with no prefixes receives a present but empty `namespacePrefixes` claim. The claim is not omitted and empty prefixes never grant namespace-derived access.

## Scope

### Production change

Change only these validators:

- `src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Model/Vendor/VendorInsertCommand.cs`
- `src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Model/Vendor/VendorUpdateCommand.cs`

Remove `.NotEmpty()` from each `NamespacePrefixes` rule. Retain the existing split/trim/length predicate and validation message. The predicate continues to reject `null`; omission continues to bind to the existing `""` property default. If the touched expression is mechanically modernized from `!= null` to `is not null`, that is a style-only change with no contract effect.

### Verification change

Add or adjust focused coverage in the existing validator, frontend module, provider, CMS E2E, DMS security, DMS E2E, and OpenAPI contract test locations described below.

### Exclusions

- No database schema, migration, repository, generated DDL, or relational mapping change.
- No `SchemaHashConstants.RelationalMappingVersion` change.
- No changes to `VendorModule.Update` ordering, locks, UUID synchronization, compensation, or responses.
- No changes to identity-provider production code, token generation, `ApiClientDetailsProvider`, namespace planning, or failure-response formatting unless implementation evidence contradicts the verified current behavior and the design is re-approved.
- No vendor/claim-set authorization-strategy compatibility validation.
- No explicit-null normalization.
- No public documentation prose or examples.
- No unrelated validator deduplication or broader authorization cleanup.

## Chosen design and rationale

The smallest responsible design is a validator-only runtime change with cross-boundary regression proof.

Both relational repositories already normalize an empty string to zero vendor-prefix rows and return an empty string on read. Both identity providers already create and update a namespace mapper with the supplied string, including `""`. OpenIddict token generation emits mapper values even when empty; Keycloak's hardcoded String mapper is configured for access-token inclusion. DMS already defaults a missing claim to `""`, splits with `RemoveEmptyEntries`, and therefore produces zero `NamespacePrefix` values for either an empty or missing claim.

Changing those downstream components would duplicate established behavior and risk changing security semantics. Extracting the two matching validator predicates into a new abstraction would add indirection for two small rules whose command validators otherwise have distinct contracts.

Rejected alternatives:

1. **Endpoint normalization or nullable request DTOs.** This would duplicate existing defaults, add mapping code, and expand or obscure the approved `null` behavior.
2. **Omit empty claims or special-case them in DMS.** This would conflict with AC3's present-empty claim and modify already-correct security code.
3. **Prevent a no-prefix vendor from using NamespaceBased.** This directly contradicts AC4 and Admin API compatibility; DMS authorization must fail closed at request time instead.

## Contracts, components, and data flows

### POST vendor

1. JSON `"namespacePrefixes": ""` binds as `""`; omission leaves the command's `""` default; explicit `null` binds as `null`.
2. The retained predicate accepts the first two forms, rejects `null`, and rejects any supplied trimmed non-empty prefix longer than 128 characters.
3. `VendorModule.InsertVendor` retains its existing repository call and 201/200 response semantics.
4. PostgreSQL and SQL Server repositories split with `RemoveEmptyEntries`, so an empty value creates zero prefix rows. A subsequent read returns `NamespacePrefixes == ""`.

### PUT vendor

1. The same binding and validation rules apply after the route/body identifier guard.
2. The approved DMS-1356 workflow remains intact: resolve affected clients, acquire application locks, reread authoritative state, update provider clients first, persist returned UUIDs under guards, compensate failures, and call `UpdateVendor` last.
3. Passing `""` clears each affected client's namespace mapper without changing its identity or unrelated claims.
4. A pre-commit provider failure leaves the vendor row unchanged; other failures retain DMS-1356's existing compensation and response contracts.

### Application or API-client creation

1. CMS reads the vendor's stored empty namespace string.
2. `ApplicationModule` and `ApiClientModule` pass that string independently from the claim-set name to `CreateClientAsync`.
3. No compatibility check is added between vendor prefixes and claim-set strategies.
4. Keycloak and OpenIddict create a present mapper whose claim name is `namespacePrefixes` and whose value is `""`.

### Token consumption and authorization

1. The issued access token contains `namespacePrefixes: ""`.
2. `ApiClientDetailsProvider` produces an empty namespace-prefix collection without parsing failure.
3. If the selected action invokes NamespaceBased, namespace planning returns `NoPrefixesConfigured` before issuing a database authorization query.
4. DMS responds with HTTP 403 and type `urn:ed-fi:api:security:authorization:namespace:invalid-client:no-namespaces`.
5. Actions governed by other authorization strategies retain their existing behavior. An empty namespace claim neither grants nor removes access belonging to those independent strategies.

## Failure handling and regression safeguards

- **Explicit null:** remains a 400 validation failure through the retained predicate. Add an explicit regression so later parity work cannot silently change it.
- **Length boundary:** a 128-character supplied prefix succeeds; a 129-character prefix retains the current message, `Each NamespacePrefix length must be 128 characters or fewer.`
- **Mixed comma input:** retain current `RemoveEmptyEntries | TrimEntries` behavior; this story does not redefine the prefix grammar.
- **Provider failures:** use the existing DMS-1356 classification, compensation, sanitization, and logging. Do not introduce empty-prefix-specific error handling.
- **Authorization:** verify the exact fail-closed 403 and type. Do not assert merely that access is unsuccessful.
- **Claim representation:** tests must distinguish a present empty claim from an omitted claim.
- **HTTP behavior:** tests must exercise JSON binding through the hosted endpoint. Direct validator or repository tests alone do not prove omission behavior.
- **Storage:** existing PostgreSQL and SQL Server empty-prefix fixtures are sufficient. Duplicate repository tests are unnecessary unless implementation uncovers a specific regression.
- **OpenAPI:** assert that the served POST and PUT request schemas do not list `namespacePrefixes` as required. No schema transformer is planned. If the assertion exposes a current mismatch, stop and obtain design approval before adding schema infrastructure.

## Verification design

No tests were run while producing this specification. Implementation should add the following evidence and then run the relevant existing project and E2E lanes in the implementation plan.

### Validator contract

Update:

- `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/Model/Vendor/VendorInsertCommandTests.cs`
- `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/Model/Vendor/VendorUpdateCommandTests.cs`

For each command, prove:

- `""` is valid.
- The property default, representing omission, is valid.
- Explicit `null` is invalid.
- A 128-character prefix is valid.
- A 129-character prefix is invalid with the existing property/message contract.

### HTTP and OpenAPI contracts

Extend `VendorModuleTests.cs` with hosted HTTP cases for:

- POST with `""` -> established create/upsert success status and repository command value `""`.
- POST with the property omitted -> the same.
- PUT with `""` -> 204 and repository command value `""`.
- PUT with the property omitted -> 204 and repository command value `""`.
- POST and PUT with explicit `null` -> 400 and no repository mutation.
- Existing 129-character POST/PUT failures remain unchanged.

Extend `MetadataModuleTests.cs` to resolve the served vendor POST and PUT request schemas and assert that `namespacePrefixes` is a string property not listed in `required`.

### Provider and token contracts

Extend the existing namespace-update fixtures in:

- `OpenIddictClientRepositoryTests.cs`
- `KeycloakClientRepositoryTests.cs`

Pass `""` through `UpdateClientNamespaceClaimAsync` and assert exactly one namespace mapper remains with an empty value, client identity remains stable, unrelated mappers remain intact, and the normal success/commit path is used.

Extend the CMS E2E vendor/application flow and JWT helper to prove, on both configured provider variants:

- Creating an application for a no-prefix vendor yields a token with a present empty `namespacePrefixes` claim.
- Updating a vendor from a non-empty prefix to `""` updates existing client tokens to the same present empty claim.

Use the generic claim-reading pattern already present in `JwtTokenValidator`; do not use `ValidateNamespace`, because splitting with `RemoveEmptyEntries` cannot distinguish an empty claim from a missing claim.

### DMS consumption and effective access

Add a focused `JwtValidationServiceTests` fixture for a valid token containing an explicit empty namespace claim. Assert successful validation and an empty `ClientAuthorizations.NamespacePrefixes` collection. Keep the existing missing-claims fixture because it verifies a distinct contract.

Convert the obsolete blocked-path note in `TrackedChangeEndpoints.feature` into an executable NamespaceBased ReadChanges scenario. The scenario must:

1. Provision a vendor with no namespace prefixes through CMS.
2. Create a client using a claim set whose exercised action uses NamespaceBased.
3. Obtain a token successfully.
4. Call the protected DMS endpoint.
5. Assert HTTP 403 and the exact no-namespaces problem type and response contract.

Tag the scenario for the normal PostgreSQL shard and `@MssqlRepresentative`. Current CI then exercises the self-contained provider in the pull-request PostgreSQL lane and both providers in the representative SQL Server lane; the existing Keycloak scheduled lanes provide additional provider coverage.

## Acceptance-criterion traceability

| Acceptance criterion | Behavior and affected components | Meaningful verification |
|---|---|---|
| AC1: POST and PUT accept empty or absent prefixes | Remove `.NotEmpty()` in both command validators; existing defaults and repositories handle empty values | Validator cases plus four hosted HTTP cases. Existing PostgreSQL/MSSQL storage fixtures remain reference evidence. |
| AC2: supplied prefix maximum remains enforced | Retain the split/trim predicate and message | Exact 128 success and 129 failure for insert/update; retain endpoint error-body coverage. |
| AC3: token has an empty claim and DMS treats it as no prefixes | Preserve both providers' mapper behavior, token generation, and `ApiClientDetailsProvider` parsing | Provider update tests, real-provider CMS create/clear token E2E, and an explicit-empty DMS JWT validation fixture. |
| AC4: no-prefix vendor plus NamespaceBased is allowed and effective access is explicit | Add no CMS cross-validation; preserve DMS `NoPrefixesConfigured` authorization outcome | End-to-end provisioning succeeds, token issuance succeeds, and the NamespaceBased request returns the exact 403/type. Record the approved ticket-outcome wording below. |

## Dependencies, deviations, and blockers

### Dependencies

- DMS-1356 is an architectural invariant, not work to redo.
- The existing Keycloak and self-contained provider implementations and CI matrices supply the integration seams.
- The ODS Admin API repository is compatibility context only; this story changes no Admin API code. At the reviewed `ed115fd8` reference, its vendor models permit null and apply different length validation, which explains but does not override this story's approved CMS contract.

### Approved deviations

- Explicit `null` remains invalid even though Admin API accepts it.
- CMS keeps the 128-character limit rather than adopting Admin API's differing create/edit validation.
- Ticket-outcome wording plus automated evidence satisfies AC4; no public prose is added.

### Remaining blockers

None.

The supplied story contains no epic identifier, so an authoritative epic-sibling inventory could not be established. DMS-405 content was also unavailable. Neither gap affects an acceptance criterion or implementation choice. If the OpenAPI assertion unexpectedly reveals a required property, that is a new conflicting fact and requires design re-approval before adding production schema machinery.

## Ticket outcome wording

Use this substantive outcome when closing DMS-1532:

> CMS accepts vendors with empty or omitted namespace prefixes and allows their applications to use claim sets containing NamespaceBased authorization. Tokens for those clients contain an empty `namespacePrefixes` claim. DMS interprets that claim as zero configured prefixes; for a correctly configured resource, an operation that invokes NamespaceBased fails closed with HTTP 403 and problem type `urn:ed-fi:api:security:authorization:namespace:invalid-client:no-namespaces` until matching prefixes are assigned. Operations governed by other authorization strategies are unchanged.

## Reconciliation notes and supporting artifacts

The supporting [human pre-spec](../../../.plans/pre-spec/DMS-1532-pre-spec.md), [nano pre-spec](../../../.plans/pre-spec/DMS-1532-pre-spec-nano.md), and [review report](../../../.plans/ref/DMS-1532-pre-spec-review/pre-spec-review.md) agree on the validator delta but differ from current code in consequential areas. This specification carries forward the review's repository-verified corrections:

- Current PUT is provider-first and repository-last under the implemented DMS-1356 design, not the database-first flow described by both pre-specs.
- OpenIddict and Keycloak empty-claim behavior is resolved by their current mapper and token paths.
- DMS's zero-prefix result is the exact no-namespaces 403, not an inferred generic authorization denial.
- The story's claimed focused empty-prefix DMS unit test was not found. The current missing-claim test is retained and a distinct empty-claim case is required.
- DMS-428 relocated the rule; it did not introduce `.NotEmpty()`. Historical intent is not used as a requirement.
- `reference/DMS-1039/AdminApi-CMS GAP Analysis.md` contains namespace prefixes only in examples and supplies no contrary decision.
- The closest approved behavioral analogy, [DMS-1513](../../../reference/design/identity-DMS-1413/00a-support-empty-datastore-assignments-in-cms-api-clients.md), supports optional configuration without relaxed authorization, but its datastore-claim mapper behavior is not treated as proof for namespace claims.

## Handoff boundary

After this written specification is approved, invoke `superpowers:writing-plans`. The implementation plan should order work from failing contract tests to the two validator edits, then provider/token and DMS E2E evidence, and finally the ticket-outcome update. It must not introduce any excluded production change without returning to design approval.
