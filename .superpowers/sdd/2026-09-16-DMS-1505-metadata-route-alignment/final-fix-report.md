# DMS-1505 Final Review Remediation

Date: 2026-09-16

Approved scope: `docs/superpowers/specs/2026-09-16-DMS-1505-metadata-route-alignment-design.md` and `docs/superpowers/plans/2026-09-16-DMS-1505-metadata-route-alignment.md`.

Reviewed baseline: `c51602d5663c0424546e6df3c336fe34e61ce0d1` through original HEAD `1968b230`, followed by the remediation diff. All five requested findings have implementation changes committed. No finding remains blocked. Runtime correctness is not claimed: the user explicitly prohibited tests and builds.

## Findings and exact fixes

### F1: Restore namespace imports

- `Content/MetadataRouteValidator.cs`: import `EdFi.DataManagementService.Core.Configuration` for `IDataStoreProvider` and `DataStore`; explicitly alias frontend `AppSettings` to avoid the core/frontend name collision.
- `Modules/MetadataEndpointModule.cs`: restore the core configuration import; retain the explicit frontend settings alias.
- `Modules/XsdMetadataEndpointModule.cs`: restore the frontend configuration import for `AppSettings`.
- `Modules/XsdMetaDataModuleTests.cs`: import core configuration for the existing provider/store fixtures and alias frontend settings for the new routing matrix.
- Static check: resolved these types against their source declarations and inspected settings references for ambiguity. Compilation remains unverified.

### F2: Preserve unqualified XSD routes without duplicate mappings

- `XsdMetadataEndpointModule.MapEndpoints` always maps the three unqualified XSD endpoints through `MapXsdEndpoints` and adds the fixed-prefix set only when `FixedRoutePattern.Build` returns a nonempty prefix.
- The same handlers and validator serve both sets. An unqualified request has no tenant/configured qualifier keys, so it remains valid even with multi-tenancy and qualifiers enabled.
- Added a seven-case XSD routing matrix that follows section-list links to file lists and XML content: unqualified requests under all four configuration combinations, tenant-only with no qualifiers, qualifier-only, and full tenant/qualifier context. Successful requests also guard against duplicate-route ambiguity.
- Static check: traced the empty/nonempty prefix branches and all three endpoint registrations. New tests were inspected, not executed.

### F3: Make Change Queries sibling links robust

- `MetadataEndpointModule.GetSections` trims the request path's trailing slash and checks its terminal `/metadata/specifications` suffix with `OrdinalIgnoreCase` before slicing the URL.
- For matching paths, remove only the final `/specifications` segment, preserving tenant, qualifiers, metadata casing, and `PathBase`. When the expected suffix is absent, fall back to `RootUrl()/metadata`, avoiding negative-index slicing.
- Expanded the link regression cases for mixed case, trailing slash, qualified/unqualified paths, `PathBase`, empty/root paths, and an unrelated path.
- Static check: reviewed both branches and the literal expected link prefixes; `RootUrl` already owns `PathBase`, so it is not appended a second time.

### F4: Reject blank configured context values

- `MetadataRouteValidator` distinguishes absent context keys from present keys before extracting values. Only a request with neither tenant nor configured qualifier keys takes the unqualified compatibility path.
- Once context is present, reject blank/null/non-string tenant values, a missing required tenant, and missing/blank/null/non-string configured qualifiers with the existing 404 response. Preserve tenant validation and exact datastore route-context matching with case-insensitive values.
- Dynamic endpoint keys such as `section` and `fileName` remain outside context validation.
- Added regression cases for empty/whitespace/null values, incomplete qualified contexts, and unqualified XSD-style dynamic keys while qualifiers are configured.
- Static check: traced early returns and the unchanged datastore key/count/value matching. No runtime validation performed.

### F5: Align TenantAwareDiscovery XSD contracts

- `TenantAwareDiscovery.feature` now requests XSD sections and file content using `Tenant_255901/255901/2024`, matching the suite's full configured route context. The invalid-tenant case also includes both qualifiers, so it exercises tenant validation rather than a missing route.
- Reused the existing generic metadata-path step; added the expected 404 contract for tenant-only XSD paths when qualifiers are enabled.
- Existing tenant-only frontend coverage remains; the new routing matrix explicitly sets qualifiers to empty for tenant-only success and follows all XSD child links.
- Static check: inspected feature-to-step bindings, request paths, fixture context, and the explicit tenant-only configuration. E2E execution remains deferred.

Paths above are under `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore`, its sibling `.Tests.Unit` project, or `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Features/InstanceManagement` as appropriate.

## Static verification and self-review

- Read approved spec/plan and inspected the requested baseline-to-HEAD diff plus the remediation diff.
- `git diff --check -- src/dms`: passed before staging.
- `git diff --cached --check`: passed before the remediation commit.
- Post-commit `git diff --check`: passed; only the pre-existing tools-manifest CRLF warning was printed.
- Direct `dotnet csharpier format` could not resolve the local tool. The normal pre-commit hook subsequently ran `dotnet tool run csharpier format` successfully: **Formatted 5 files**, hook exit successful. The commit includes that formatting; no hooks were bypassed.
- Self-review covered imports, route cardinality, compatibility paths, tenant-only/full-context behavior, suffix boundaries, `PathBase`, and validation ordering.
- Changed-file inventory confirms no changes to Discovery implementation, resource API routing, authentication, CMS/schema, or `SchemaHashConstants.RelationalMappingVersion`. No new Newtonsoft.Json usage or dependencies.
- Existing `.config/dotnet-tools.json` modification, untracked task/progress artifacts, and REST-client file were preserved and excluded from these commits.
- No subagents were dispatched. Review/implementation/verification skills guided scope and evidence tracking; explicit user instructions superseded their test-execution and delegation steps.

## Commits

- `088a3080e4b3d383e580c1e07333ef014152d87a` — `fix: resolve DMS-1505 metadata final review findings` (all F1–F5 code, contract, and regression-case changes).
- This report is committed separately as `docs: record DMS-1505 final review remediation`.

## Remaining verification gaps

- **No tests or build were run**, including baseline, frontend unit tests, integration tests, and E2E. Added/updated tests have not been demonstrated to fail before or pass after the fixes.
- Compilation, ASP.NET routing behavior, XML streaming, case-insensitive HTTP requests, and the configured Instance Management E2E contracts still require CI or a later authorized verification run.
- Suggested later checks: frontend unit project (metadata, XSD, and fixed-route fixtures), then prepared Instance Management E2E (`RouteQualifierDiscovery`, `TenantAwareDiscovery`, `RouteQualifierErrors`) and single-tenant Discovery regression coverage.
