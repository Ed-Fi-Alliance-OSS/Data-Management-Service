# DMS-1532 Optional Vendor Namespace Prefixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow CMS vendor POST and PUT requests to accept empty or omitted namespace prefixes while preserving the length limit, present-empty token claim, and DMS fail-closed authorization behavior.

**Architecture:** Remove the two FluentValidation `NotEmpty` rules. Keep existing binding defaults, repository behavior, OpenIddict mapping, and DMS authorization unchanged. For empty namespace prefixes only, configure Keycloak's existing hardcoded mapper with `claim.value` equal to the JSON string literal `""` and `jsonType.label` equal to `JSON`, preserving the empty claim through Keycloak 26.1. Pin the contract with focused unit, hosted HTTP/OpenAPI, and real-stack E2E tests.

**Tech Stack:** .NET 10, C#, FluentValidation, NUnit, FluentAssertions, FakeItEasy, ASP.NET Core `WebApplicationFactory`, Reqnroll, Keycloak, OpenIddict, PostgreSQL, SQL Server.

**Spec:** `docs/superpowers/specs/2026-09-29-dms-1532-optional-vendor-namespace-prefixes-design.md`

## Global Constraints

- Accept `namespacePrefixes: ""` and omission on `POST /v3/vendors` and `PUT /v3/vendors/{id}`.
- Reject explicit JSON `null`; do not normalize it to empty.
- Retain the exact 128-character per-prefix limit and message: `Each NamespacePrefix length must be 128 characters or fewer.`
- Tokens for no-prefix clients must contain one present `namespacePrefixes` claim whose value is `""`.
- Do not change vendor persistence, DMS-1356 update ordering/compensation, OpenIddict production behavior, DMS claim parsing, namespace planning, or ProblemDetails formatting. The approved Keycloak empty-value encoding is the sole provider-production change.
- Do not add migrations, generated DDL, mapping changes, or change `SchemaHashConstants.RelationalMappingVersion`.
- Do not add public documentation, null parity, claim-set compatibility validation, validator abstractions, or unrelated cleanup.
- Follow .NET 10 style, keep variables non-nullable, use `is null`/`is not null`, and use `System.Text.Json`.
- Use NUnit, FluentAssertions, and FakeItEasy in the existing fixture style. Every new assertion must fail if the intended behavior is reverted.

## Review Focus

- Explicit empty and omitted JSON must both reach the repository as `NamespacePrefixes == ""`; Task 1 adds separate hosted POST and PUT cases for each form.
- Explicit `null` must remain a 400 and must not invoke insert/update persistence; Task 1 pins the exact validator and hosted endpoint behavior.
- Boundary values must not drift: 128 characters succeeds and 129 fails with the existing message; Task 1 covers both validators and retains endpoint error-body coverage.
- A no-prefix token claim must be present, not omitted; Tasks 2 and 3 assert exactly one mapper/claim with the empty value on both providers.
- NamespaceBased access with zero prefixes must fail closed with the exact 403 problem type; Task 4 exercises the real CMS-to-token-to-DMS path on the PostgreSQL shard and representative SQL Server lane.

---

## File Map

**Production files modified**

- `src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Model/Vendor/VendorInsertCommand.cs` — remove insert validation's non-empty requirement only.
- `src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Model/Vendor/VendorUpdateCommand.cs` — remove update validation's non-empty requirement only.
- `src/config/backend/EdFi.DmsConfigurationService.Backend.Keycloak/KeycloakClientRepository.cs` — encode only an empty `namespacePrefixes` mapper value as the two-character JSON string literal `""` with JSON type so Keycloak emits a present empty-string claim.

**Contract and regression files modified**

- `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/Model/Vendor/VendorInsertCommandTests.cs`
- `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/Model/Vendor/VendorUpdateCommandTests.cs`
- `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules/VendorModuleTests.cs`
- `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules/MetadataModuleTests.cs`
- `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/KeycloakClientRepositoryTests.cs`
- `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/OpenIddictClientRepositoryTests.cs`
- `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/StepDefinitions/StepDefinitions.cs`
- `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/Features/Vendors.feature`
- `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Security/JwtValidationServiceTests.cs`
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/ChangeQueries/TrackedChangeEndpoints.feature`

Inspect the raw JWT payload JSON for a property of string kind and value `""`; do not use `ValidateNamespace`, because splitting with `RemoveEmptyEntries` cannot distinguish an empty claim from a missing claim.

### Task 1: Change the CMS vendor request contract

**Files:**

- Modify: `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/Model/Vendor/VendorInsertCommandTests.cs:12`
- Modify: `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/Model/Vendor/VendorUpdateCommandTests.cs:12`
- Modify: `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules/VendorModuleTests.cs:121`
- Modify: `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules/MetadataModuleTests.cs:890`
- Modify: `src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Model/Vendor/VendorInsertCommand.cs:23`
- Modify: `src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Model/Vendor/VendorUpdateCommand.cs:22`

**Interfaces:**

- Consumes: Existing `VendorInsertCommand.NamespacePrefixes` and `VendorUpdateCommand.NamespacePrefixes` string defaults, FluentValidation endpoint integration, and `IVendorRepository` commands.
- Produces: Empty and omitted namespace-prefix input accepted as `""`; explicit `null` and overlength prefixes remain validation failures.

- [ ] **Step 1: Rewrite and extend both validator fixtures before changing production code**

For both command types, add or update these exact test methods:

- `Validate_WithEmptyNamespacePrefixes_ShouldPassValidation`
- `Validate_WithDefaultNamespacePrefixes_ShouldPassValidation`
- `Validate_WithNullNamespacePrefixes_ShouldFailWithLengthMessage`
- `Validate_With128CharacterNamespacePrefix_ShouldPassValidation`
- `Validate_With129CharacterNamespacePrefix_ShouldFailWithLengthMessage`

Use these exact outcomes:

```csharp
resultForEmpty.IsValid.Should().BeTrue();
resultForDefaultProperty.IsValid.Should().BeTrue();
resultFor128Characters.IsValid.Should().BeTrue();

resultForNull.Errors.Should().ContainSingle();
resultForNull.Errors[0].PropertyName.Should().Be("NamespacePrefixes");
resultForNull.Errors[0].ErrorMessage.Should().Be(
    "Each NamespacePrefix length must be 128 characters or fewer."
);

resultFor129Characters.Errors.Should().ContainSingle();
resultFor129Characters.Errors[0].PropertyName.Should().Be("NamespacePrefixes");
resultFor129Characters.Errors[0].ErrorMessage.Should().Be(
    "Each NamespacePrefix length must be 128 characters or fewer."
);
```

Use `NamespacePrefixes = null!` only in the explicit-null test. For omission/default, initialize all other required properties but do not set `NamespacePrefixes`.

- [ ] **Step 2: Add hosted HTTP binding and persistence assertions**

In `VendorModuleTests`, add `Post_WithEmptyNamespacePrefixes_ShouldPassEmptyValueToRepository`, `Post_WithOmittedNamespacePrefixes_ShouldPassEmptyValueToRepository`, `Put_WithEmptyNamespacePrefixes_ShouldPassEmptyValueToRepository`, and `Put_WithOmittedNamespacePrefixes_ShouldPassEmptyValueToRepository`. Assert POST returns `201 Created`, PUT returns `204 NoContent`, and capture the command passed to `InsertVendor`/`UpdateVendor` to assert `NamespacePrefixes.Should().BeEmpty()`.

Add `Post_WithNullNamespacePrefixes_ShouldRejectWithoutPersistence` and `Put_WithNullNamespacePrefixes_ShouldRejectWithoutPersistence`; assert `400 BadRequest` and `MustNotHaveHappened()` for the corresponding repository mutation. Keep the existing 129-character response-body tests unchanged.

- [ ] **Step 3: Add the served OpenAPI contract assertion**

Add `OpenApi_Vendor_Request_Schemas_Keep_NamespacePrefixes_Optional_NonNullable_String` to `MetadataModuleTests`. Fetch `/openapi/v1.json`, resolve `VendorInsertCommand` and `VendorUpdateCommand` under `components.schemas`, and assert for each schema:

```csharp
properties.GetProperty("namespacePrefixes").GetProperty("type").GetString().Should().Be("string");
required.EnumerateArray().Select(item => item.GetString()).Should().NotContain("namespacePrefixes");
```

If the served schema unexpectedly lists the property as required, stop and return to design approval; do not add an OpenAPI transformer in this task.

- [ ] **Step 4: Run the focused tests and verify the new acceptance cases fail first**

Run:

```powershell
dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/EdFi.DmsConfigurationService.Backend.Tests.Unit.csproj --filter "FullyQualifiedName~VendorInsertCommandTests|FullyQualifiedName~VendorUpdateCommandTests"
dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj --filter "FullyQualifiedName~VendorModuleTests|FullyQualifiedName~MetadataModuleTests"
```

Expected before the production edit: explicit-empty/default validator and hosted endpoint cases fail because `.NotEmpty()` still rejects `""`; existing null and 129-character cases continue to pass.

- [ ] **Step 5: Make the minimal validator-only production change**

Remove `.NotEmpty()` from the `RuleFor(v => v.NamespacePrefixes)` chain in both command files. Preserve the split flags and message. Change `split != null` to `split is not null` while touching the expression; do not change any other rule.

- [ ] **Step 6: Re-run the focused tests**

Run the two commands from Step 4. Expected: PASS, including the empty, omitted, null, 128, 129, HTTP binding, repository-capture, and OpenAPI assertions.

- [ ] **Step 7: Commit the contract slice**

```powershell
git add src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Model/Vendor/VendorInsertCommand.cs src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Model/Vendor/VendorUpdateCommand.cs src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/Model/Vendor/VendorInsertCommandTests.cs src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/Model/Vendor/VendorUpdateCommandTests.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules/VendorModuleTests.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules/MetadataModuleTests.cs
git commit -m "feat: allow empty vendor namespace prefixes"
```

### Task 2: Pin empty namespace mapper updates for both providers

**Files:**

- Modify: `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/KeycloakClientRepositoryTests.cs:1049`
- Modify: `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/OpenIddictClientRepositoryTests.cs:532`

**Interfaces:**

- Consumes: `IIdentityProviderRepository.UpdateClientNamespaceClaimAsync(string clientUuid, string namespacePrefixes)` and existing mapper-update fixtures.
- Produces: Regression proof that `""` leaves exactly one JSON-typed namespace mapper configured with the JSON string literal `""`, without changing client identity or unrelated claims. Non-empty values retain the existing String mapper. The production adjustment is scoped to Keycloak's namespace-prefix mapper.

- [ ] **Step 1: Extend the Keycloak clearing fixture to specify the wire-safe encoding**

Create `Given_a_namespace_claim_update_clearing_the_existing_claim` from `NamespaceClaimUpdateTestBase`, call `await ActUpdateAsync("")`, and assert:

```csharp
_result.Should().BeOfType<ClientUpdateResult.Success>();
((ClientUpdateResult.Success)_result).ClientUuid.Should().Be(Guid.Parse(_clientUuid));
NamespaceClaims(AppliedClient()).Should().ContainSingle();
ClaimValue(AppliedClient(), "educationOrganizationIds").Should().Be("100");
ClaimValue(AppliedClient(), "dataStoreIds").Should().Be("7,8");
```

Also call `AssertClientIdentityPreserved()` and assert the existing UUID is updated exactly once. Assert the mapper's stored `claim.value` is the two-character JSON string literal `""` and `jsonType.label` is `JSON`; the existing replacement fixture must continue asserting the non-empty value and `String` type.

- [ ] **Step 2: Add the OpenIddict empty-update fixture**

Extend the test-only `ActUpdateAsync` helper to accept a named optional `namespacePrefixes` argument while preserving existing calls, then create `Given_UpdateClientNamespaceClaimAsync_Clearing_The_Existing_Claim`. Call `await ActUpdateAsync(namespacePrefixes: "")` and assert success with the unchanged UUID, one namespace mapper whose `claim.value` is empty, preserved education-organization/data-store mappers, and transaction commit without rollback.

- [ ] **Step 3: Run the provider unit fixtures and observe the new Keycloak assertion fail**

Run:

```powershell
dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/EdFi.DmsConfigurationService.Backend.Tests.Unit.csproj --filter "FullyQualifiedName~namespace_claim_update|FullyQualifiedName~UpdateClientNamespaceClaimAsync"
```

Expected before the production edit: the Keycloak clearing fixture fails because the mapper retains an empty `claim.value` with String type; OpenIddict tests pass. After the production edit below, the full filter passes.

- [ ] **Step 4: Encode only empty Keycloak namespace values for the built-in JSON mapper**

In `NamespacePrefixProtocolMapper`, keep the existing String configuration for non-empty input. For empty input, set `claim.value` to the JSON string literal `""` and `jsonType.label` to `JSON`. Do not alter education-organization, data-store, or role mappers.

- [ ] **Step 5: Re-run the provider fixtures**

Run the filter from Step 3. Expected: PASS, including the empty Keycloak encoding, unchanged non-empty String mapper, and OpenIddict empty mapper tests.

- [ ] **Step 6: Commit provider regression and production change**

```powershell
git add src/config/backend/EdFi.DmsConfigurationService.Backend.Keycloak/KeycloakClientRepository.cs src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/KeycloakClientRepositoryTests.cs src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/OpenIddictClientRepositoryTests.cs
git commit -m "fix: preserve empty namespace claim in Keycloak"
```

### Task 3: Prove CMS creates and clears present-empty token claims

**Files:**

- Modify: `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/StepDefinitions/StepDefinitions.cs:691`
- Modify: `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/Features/Vendors.feature:439`

**Interfaces:**

- Consumes: Existing credential-capture/token-request steps and raw JWT access-token payloads.
- Produces: A Reqnroll assertion that distinguishes a present JSON string claim with value `""` from an omitted claim, plus real-provider create and clear scenarios.

- [ ] **Step 1: Add a present-empty namespace claim step**

Add `Then the token has an empty namespacePrefixes claim`. Parse `access_token` from the current response, decode its payload with `System.Text.Json`, and assert the payload contains `namespacePrefixes` with `JsonValueKind.String` and value `""`. Include the parsed payload in the missing-property assertion for actionable diagnostics.

Do not use `ValidateNamespace`, because its split with `RemoveEmptyEntries` cannot distinguish empty from missing.

- [ ] **Step 2: Add the no-prefix application-creation scenario**

In `Vendors.feature`, add `Scenario: 20 Application for a no-prefix vendor receives a present empty namespace claim`, tagged `@MssqlRepresentative`. Create a vendor with `"namespacePrefixes": ""`, create an application with `Claim06`, capture its credentials, request a token with scope `Claim06`, and assert `200` plus the new present-empty claim step.

- [ ] **Step 3: Add the vendor-clear scenario**

Add `Scenario: 21 Clearing vendor prefixes updates existing clients to a present empty namespace claim`, tagged `@MssqlRepresentative`. Create a vendor with `uri://dms-1532.org`, create an application and capture credentials, prove the initial token carries that prefix, PUT the vendor with `"namespacePrefixes": ""`, request another token using the same credentials, and assert one present empty namespace claim.

- [ ] **Step 4: Run the Vendors feature against both provider variants**

Run:

```powershell
./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -E2ETestFilter "FullyQualifiedName~VendorsFeature"
./build-config.ps1 E2ETest -Configuration Release -IdentityProvider keycloak -E2ETestFilter "FullyQualifiedName~VendorsFeature"
```

Expected: PASS for both providers. The `@MssqlRepresentative` tags also place these scenarios in both providers' representative SQL Server CI matrix.

- [ ] **Step 5: Commit CMS token E2E coverage**

```powershell
git add src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/StepDefinitions/StepDefinitions.cs src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/Features/Vendors.feature
git commit -m "test: verify empty namespace token claims"
```

### Task 4: Prove DMS consumes the empty claim and fails NamespaceBased closed

**Files:**

- Modify: `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Security/JwtValidationServiceTests.cs:360`
- Modify: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/ChangeQueries/TrackedChangeEndpoints.feature:799`

**Interfaces:**

- Consumes: Existing `JwtValidationService`, CMS-backed DMS E2E authorization provisioning, ReadChanges NamespaceBased planning, and namespace ProblemDetails formatter.
- Produces: Proof that a valid explicit-empty claim becomes zero `NamespacePrefix` values and a correctly configured NamespaceBased request returns the exact no-namespaces 403.

- [ ] **Step 1: Add the explicit-empty JWT unit fixture**

Add `Given_A_Valid_Token_With_An_Explicitly_Empty_Namespace_Claim` beside the missing-claims fixture. Build a valid signed token containing `new Claim("namespacePrefixes", "")` plus stable `jti` and `scope` claims, then assert:

```csharp
_principal.Should().NotBeNull();
_clientAuthorizations.Should().NotBeNull();
_clientAuthorizations!.NamespacePrefixes.Should().BeEmpty();
```

Keep `Given_A_Valid_Token_With_Missing_Claims`; the two fixtures prove distinct token contracts.

- [ ] **Step 2: Replace the obsolete blocked-path note with an executable scenario**

In `TrackedChangeEndpoints.feature`, replace the note at the end of the ReadChanges authorization rule with scenario `19 NamespaceBased ReadChanges with no namespace prefixes returns invalid-client ProblemDetails`. Tag it:

```gherkin
@e2e-ci-shard-3
@MssqlRepresentative
@ResetClaimsetsAfterScenario
@reset-data-before-scenario
```

Upload a claim set granting `CrisisTypeDescriptor` access through `NamespaceBased`, assert upload success, authorize that claim set with `namespacePrefixes ""`, and GET `/ed-fi/crisisTypeDescriptors/deletes`. Assert `403`, `content-type: application/problem+json`, a non-empty correlation ID, and the exact response fields:

```json
{
  "type": "urn:ed-fi:api:security:authorization:namespace:invalid-client:no-namespaces",
  "title": "Authorization Denied",
  "status": 403,
  "detail": "There was a problem authorizing the request. The caller has not been configured correctly for accessing resources authorized by Namespace.",
  "correlationId": null,
  "validationErrors": {},
  "errors": [
    "The API client has been given permissions on a resource that uses the 'NamespaceBased' authorization strategy but the client doesn't have any namespace prefixes assigned."
  ]
}
```

- [ ] **Step 3: Run the DMS unit fixture**

Run:

```powershell
dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj --filter "FullyQualifiedName~JwtValidationServiceTests"
```

Expected: PASS, including both missing-claim and explicit-empty-claim fixtures.

- [ ] **Step 4: Run the new E2E path in the CI-representative matrices**

Run from the repository root, allowing each build command to manage teardown/setup as documented:

```powershell
./build-dms.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category=@e2e-ci-shard-3'
./build-dms.ps1 E2ETest -Configuration Release -DatabaseEngine mssql -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category=@MssqlRepresentative'
./build-dms.ps1 E2ETest -Configuration Release -DatabaseEngine mssql -IdentityProvider keycloak -EnvironmentFile './.env.e2e' -TestFilter 'Category=@MssqlRepresentative'
```

Expected: PASS. The new scenario must fail if CMS again rejects empty prefixes, either provider omits the claim, DMS parses it as a prefix, or the authorization response drifts.

- [ ] **Step 5: Commit DMS consumption and authorization coverage**

```powershell
git add src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Security/JwtValidationServiceTests.cs src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/ChangeQueries/TrackedChangeEndpoints.feature
git commit -m "test: verify no-prefix authorization failure"
```

### Task 5: Format, regress, and hand off the ticket outcome

**Files:**

- Modify: Only files already listed if formatting changes them.
- External handoff: DMS-1532 ticket outcome after all verification passes.

**Interfaces:**

- Consumes: Tasks 1-4 and the approved ticket wording in the spec.
- Produces: A formatted, verified branch and an exact behavioral outcome for reviewers and ticket closure.

- [ ] **Step 1: Format the touched C# files**

Run `dotnet csharpier format <file>` for each touched `.cs` file from Tasks 1-4. Do not format unrelated directories.

- [ ] **Step 2: Run all affected unit projects**

```powershell
dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/EdFi.DmsConfigurationService.Backend.Tests.Unit.csproj
dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.csproj
dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj
```

Expected: all PASS.

- [ ] **Step 3: Inspect the final diff for scope and whitespace**

```powershell
git diff --check
git diff --stat
git status --short
```

Confirm the only production changes are the two removed `.NotEmpty()` calls plus optional `is not null` modernization and the empty-only Keycloak namespace-mapper encoding. Confirm there are no repository, migration, mapping-version, OpenIddict-production, DMS-production, or public-documentation changes.

- [ ] **Step 4: Commit formatting only if it changed tracked files**

```powershell
git add src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Model/Vendor/VendorInsertCommand.cs src/config/datamodel/EdFi.DmsConfigurationService.DataModel/Model/Vendor/VendorUpdateCommand.cs src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/Model/Vendor/VendorInsertCommandTests.cs src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/Model/Vendor/VendorUpdateCommandTests.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules/VendorModuleTests.cs src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/Modules/MetadataModuleTests.cs src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/KeycloakClientRepositoryTests.cs src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/OpenIddictClientRepositoryTests.cs src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/StepDefinitions/StepDefinitions.cs src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Security/JwtValidationServiceTests.cs
git commit -m "style: format DMS-1532 changes"
```

Skip this commit if formatting produced no diff.

- [ ] **Step 5: Record the approved ticket outcome**

After every required test is green, use the exact substantive outcome from the specification's **Ticket outcome wording** section in DMS-1532 or the implementation handoff. Do not claim broader Admin API null parity or access for non-NamespaceBased strategies beyond the approved wording.

- [ ] **Step 6: Stop before implementation integration**

Report commits, commands and results, any skipped environment-dependent lane, and the clean/dirty worktree state. Do not merge, open a PR, or modify the external ticket without the corresponding user authority.
