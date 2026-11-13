# DMS-1237 Spike: Multi-Tenancy E2E Test Strategy

## 1. Document control

| Field | Value |
| --- | --- |
| Ticket | [DMS-1237 — Test suite for multi-tenancy](https://edfi.atlassian.net/browse/DMS-1237) (Spike, fix version Ed-Fi API v8.2) |
| Decision | Option 2: complete focused routed-endpoint coverage in the Instance Management E2E suite and keep the standard E2E suite unchanged |
| Decided | 2026-11-11 |
| Analysis baseline | `Ed-Fi-Alliance-OSS/Data-Management-Service` at [`38c75853`](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/tree/38c758539907e4c3861ee6261400d16e263fb903) |
| Coverage owner | `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/` ([README](../../../src/dms/tests/EdFi.InstanceManagement.Tests.E2E/README.md#routed-metadata-and-token-introspection-coverage)) |
| Related design | [`reference/design/multitenancy-analysis.md`](../../design/multitenancy-analysis.md), [`docs/DATABASE-SEGMENTATION-STRATEGY.md`](../../../docs/DATABASE-SEGMENTATION-STRATEGY.md) |

The analysis was static: sources, workflows, Jira issues and pull requests were inspected. Passing results reported in historical pull requests are not treated as current results.

## 2. Summary

DMS-1237 asked how to keep multi-tenant and route-qualified (district and school-year) deployments from regressing. It offered two alternatives:

1. Adapt the full standard E2E suite and run it weekly against routed deployments.
2. Bring selective coverage of the affected endpoints (dependencies, `token_info`, XSD metadata) into the Instance Management E2E suite.

**Decision: option 2.** Each routing regression found so far (DMS-1230, DMS-1262, DMS-1505, DMS-1660) sat at an endpoint boundary that is mapped separately from the common resource pipeline: Discovery, metadata, XSD and token endpoints. Each was fixed with focused Instance-suite coverage. That suite already runs a real tenant plus district/year deployment on PostgreSQL and SQL Server in every pull request.

The remaining gap was narrow:
- `tokenInfo` was advertised but never invoked on a routed deployment.
- XSD coverage downloaded a hard-coded file instead of following the advertised links.
- Dependency and OpenAPI metadata were fetched, but their content was not checked.

The follow-up in Section 8 implements coverage for those gaps without new CI lanes, product changes or fixture changes. Runtime verification on both engines remains pending.

## 3. Background

### 3.1 The motivating defect and later fixes

| Change | Ticket | What happened | Relevance |
| --- | --- | --- | --- |
| [#757](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/757) (2025-12) | [DMS-886](https://edfi.atlassian.net/browse/DMS-886) | Added the tenant route prefix but kept raw-path regex parsing for XSD routes. | Introduced the defect class. |
| [#1048](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1048) (2026-06) | [DMS-1230](https://edfi.atlassian.net/browse/DMS-1230) | A tenant prefix broke XSD parsing, which affected BulkLoadClient. Fixed by binding route values instead of parsing raw paths, and by stripping only the `files` suffix instead of every `files` substring. Added unit tests and a routed XSD E2E check that returns 200 and XML. | The spike's trigger. The fix already follows option 2, but its E2E check downloads one hard-coded file. |
| [#1104](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1104) (2026-07) | [DMS-1262](https://edfi.atlassian.net/browse/DMS-1262) | Discovery now advertises the DMS token proxy; root and fully qualified token routes coexist. | An Instance scenario requests a token from the advertised `oauth` URL. Tests must not fall back to a direct identity-provider URL. |
| [#1263](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1263) (2026-09) | [DMS-1505](https://edfi.atlassian.net/browse/DMS-1505) | Metadata and XSD routing and validation aligned with Discovery; the Instance suite began fetching advertised metadata URLs. | URL-string assertions alone had not caught the defect. |
| [#1431](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1431) (2026-11) | [DMS-1660](https://edfi.atlassian.net/browse/DMS-1660) | Root and incomplete multi-tenant metadata paths return a guidance 404 before any validator or CMS call; single-tenant behavior is kept. | Supersedes DMS-886's earlier suggestion of a generic missing-tenant 400 for these paths. |
| [#1214](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1214) (2026-09) | DMS-1474 | Shared build artifacts, declarative CI guard tests, and draft and path-sensitive cost controls. | A new full-suite matrix would expand this CI budget; option 2 does not. |

None of these fixes is to be reimplemented. [DMS-861](https://edfi.atlassian.net/browse/DMS-861) and its children (DMS-852, 875, 880, 881, 886, 887) are historical context only. They are not DMS-1237's parent and add no acceptance criteria. DMS-852 originally assigned routed coverage to the Instance suite.

### 3.2 Design direction

The 2025 [Confluence spike design](https://edfi.atlassian.net/wiki/spaces/BD/pages/1055391745/Spike+Design+for+multitenancy+and+instance+management) recommended per-instance schemas and OpenSearch. The current repository direction ([`multitenancy-analysis.md`](../../design/multitenancy-analysis.md)) drops OpenSearch and uses one database per instance. This strategy uses the existing database-per-instance fixture and makes no storage-design decision.

CMS tenancy and student-data isolation are different things:
- CMS scopes administrative rows by tenant.
- DMS resolves the instance databases the caller is authorized for.

## 4. Questions investigated

DMS-1237 has no numbered acceptance criteria. It poses these questions:

| # | Question | Answer |
| --- | --- | --- |
| Q1 | How exposed are multi-tenant and school-year routes to regressions such as DMS-1230? | The original defect is fixed. Tenant isolation, route segregation, error, cursor and change-query behavior is already covered by Instance scenarios on both engines. The remaining exposure is in endpoint-specific route mapping and advertised child links, not in the shared resource pipeline. |
| Q2 | Should the full standard suite run weekly on routed deployments, and how would its features adapt? | Changing the base URL is not enough (Section 6). Adapting the suite needs tenant-aware provisioning, route-composed paths, correctly targeted resets, per-tenant credentials and different URL and schema expectations. Not selected. |
| Q3 | Should selective dependency, `token_info` and XSD tests be added to the Instance suite? | Yes, by completing partial coverage rather than adding it from scratch (Section 5). Selected. |

Two related questions were also settled:
- New tests do not need to wait for a weekly run, because the Instance shards already feed the pull-request CI gate for both engines.
- No new client abstraction is needed, because `DmsApiClient.GetByLocationAsync` and `PostByLocationAsync` already request arbitrary advertised URLs.

## 5. Coverage before this spike

### 5.1 Instance Management E2E suite

- **Fixture:** a real deployment with tenancy and district/school-year qualifiers.
  - It has two tenants with their applications and three separately provisioned databases: `Tenant_255901/255901/2024`, `Tenant_255901/255901/2025` and `Tenant_255902/255902/2024`.
  - `build-dms.ps1 InstanceE2ETest` provisions the databases, registers the fixture in CMS, restarts DMS once, then runs the tests.
  - Scenarios hydrate fixture state per scenario and only clean up what they own.
- **CI:** two shards per engine (PostgreSQL and SQL Server) run on every pull request through the DMS CI gate.
- **Existing routed scenarios:** `RouteQualifierSegregation`, `TenantSegregation`, `RouteQualifierErrors`, `OwaspTenantIsolation`, `CursorPartitionRouteContext`, and the two discovery features below.
- **`RouteQualifierDiscovery.feature`:**
  - Requests a token from Discovery's advertised `oauth` URL.
  - Fetches the advertised dependency, specification-list and XSD-section URLs, but only checks reachability.
  - Checks `tokenInfo` only as a URL string.
- **`TenantAwareDiscovery.feature`:**
  - Downloads a hard-coded core XSD and checks 200 and XML.
  - Keeps tenant-only XSD access working when qualifiers are configured.

### 5.2 Other coverage

- **Standard E2E suite:**
  - Owns the breadth of resource, authorization, discovery, XSD and token-introspection behavior, on a single-tenant deployment with no route qualifiers (`.env.e2e`).
  - The weekend build runs it in that same configuration.
- **Frontend unit tests:**
  - `FixedRoutePatternTests` cover the four tenancy and qualifier prefix shapes.
  - `XsdMetaDataModuleTests` cover the XSD route matrix, advertised child links, and the `files`-substring regression.
  - `MetaDataModuleTests` cover the missing-context guidance 404 and verify that no validator or profile call is made.
  - E2E tests cannot prove the absence of a CMS call, so those assertions stay in the unit tests.

### 5.3 Gap

| Endpoint | Covered on a routed deployment | Missing |
| --- | --- | --- |
| `tokenInfo` | Advertised URL string only | A real authenticated call; the negative status contract; cross-tenant rejection |
| XSD metadata | Section list reachable; one hard-coded file downloaded | Following section → advertised `files` → advertised file, with real content |
| Dependencies | Reachable | Content and representation checks |
| OpenAPI metadata | Specification list reachable | Following the advertised Resources document and checking its content |

## 6. Options considered

| Dimension | Option 1: full standard suite, weekly, on routed deployments | Option 2: focused Instance-suite coverage |
| --- | --- | --- |
| Fit to observed defects | Broad replay catches route defects only after the suite is correctly adapted. Repeating resource scenarios does not follow advertised child links. | Targets exactly the separately mapped boundaries where every defect occurred: route registration, advertised URLs, content retrieval, context-bound introspection. |
| Precedent | No weekly multi-tenant full-suite lane exists. | #1048, #1104, #1263 and #1431 each extended this suite for these failures. |
| Fixture compatibility | `AuthorizationDataProvider.CreateClientCredentials` sends no tenant header and creates no route contexts. It creates stores that point at one configured database, and resets that database. | Two tenants, three distinct databases, correct tenant headers, per-scenario hydration and ownership guards already exist. |
| Path composition | `StepDefinitions.AddDataPrefixIfNecessary` is not a tenant-prefix composer; it would put a tenant-prefixed metadata path under `data/`. | Follows the URLs the server advertises; nothing is reconstructed. |
| Assertion compatibility | Standard discovery and XSD features expect Sample and Homograph schemas plus core and TPDM, and many expected URLs are unqualified. The routed fixture uses core and TPDM only. | Asserts the routed fixture's own contracts; exhaustive model and content checks stay in the standard suite. |
| Feedback and cost | Weekly feedback; needs a new workflow and ongoing harness maintenance; CI cost was not measured. | Runs in existing pull-request shards on both engines; no new lane. |
| Residual risk | Broadest resource replay once adapted, but the adaptation itself adds regression surface. | Does not prove every resource operation or every independent routing-flag combination. |

## 7. Decision and rationale

**Option 2 is selected.** It is the simplest responsible continuation of coverage that already exists.

Common resource operations share one tenant, JWT and store-resolution pipeline. Discovery, metadata, XSD and token endpoints are each mapped separately. Testing those boundaries is therefore more directly tied to the observed failures than replaying every resource scenario.

Kept as they are:
- the standard suite's breadth;
- the Instance suite's isolation, error, cursor and change-query coverage;
- the frontend routing unit tests.

No weekly matrix or general route-adaptation framework is added.

**Reconsider option 1** if either of these happens:
- an escaped resource-operation defect on a routed deployment cannot be addressed with focused coverage;
- a release requirement explicitly calls for exhaustive configuration replay.

Option 1 is technically possible. Nothing here shows it to be useless or gives a cost ratio, so measure its CI cost and scope it separately.

## 8. Follow-up coverage

These conditions define the bounded follow-up. They are not Jira acceptance criteria. All scenarios use the existing fixture, `@InstanceFixture`, `@InstanceCleanup` and `@instance-management-ci-shard-1`.

This documentation spike also includes the focused feature and binding changes described below. That extends the investigation into implementation of the selected option, using the existing harness to make the recommendation concrete. It does not change product behavior or storage design.

**Implementation and verification status:** F1–F5 are implemented in source, and the README documents coverage ownership and limits. F6 remains incomplete until PostgreSQL and SQL Server execution evidence establishes scenario selection, passing results, skips and timing for the revision being verified. Static inspection and compilation do not establish those runtime results. The verification conditions below remain required; this decision does not claim the follow-up has passed them.

| ID | Behavior | Verification |
| --- | --- | --- |
| F1 | For each of the three fixture routes: get the concrete Discovery URLs anonymously; get a fresh token for that route's owning tenant through the advertised `oauth` URL; POST JSON `{ "Token": token }` to the advertised `tokenInfo` with the same token as bearer. | Status is exactly 200. `active` is `true`. `client_id` equals that tenant's fixture key. `claim_set.name` is `E2E-NoFurtherAuthRequiredClaimSet`. `namespace_prefixes` is `["uri://ed-fi.org"]`. `Cache-Control` includes `no-store`. The full resource, profile and hierarchy payload is not snapshotted. |
| F2 | On the first tenant's 2024 `tokenInfo`: (a) a valid body token with no bearer header; (b) a valid owning-tenant bearer with a non-matching body token, using the same literal as the standard suite. | (a) exactly 401 with a Bearer challenge; (b) exactly 400 with the established token-mismatch error. Success assertions never run on negative responses. |
| F3 | Discover the second tenant's 2024 `tokenInfo`, then POST the first tenant's token as both bearer and body. The credential owner stays distinct from the route owner. | Exactly 404 (no authorized tenant and store match) and no introspection payload. F1's second-tenant success is the positive control, so a broken route cannot pass as isolation. |
| F4 | On the first tenant's fully qualified 2024 Discovery, anonymously follow `xsdMetadata` → section `ed-fi` → its advertised `files` URL → the advertised URL whose file name is exactly `Ed-Fi-Core.xsd`. | Every hop returns exactly 200. Each concrete URL keeps the tenant and qualifiers. The file has media type `application/xml` and root `{http://www.w3.org/2001/XMLSchema}schema`. Placeholder URLs and missing or ambiguous selections fail. The existing direct-file and tenant-only scenarios stay as compatibility checks. |
| F5 | On the same route: GET the advertised `dependencies`; GET the advertised `openApiMetadata`, select Resources, and GET its `endpointUri`. | Dependencies: exactly 200, `application/json`, an array whose entries have string `resource`, integer `order` and string-array `operations`, including `/ed-fi/students`. Resources document: exactly 200, `application/json`, an OpenAPI 3.x version field, and `paths` containing `/ed-fi/students`. Child URLs keep the route context. Load-order numbers are not asserted. |
| F6 | Document coverage ownership and limits in the Instance README. | Run `build-dms.ps1 InstanceE2ETest` on both PostgreSQL and SQL Server. The TRX must show the changed and new scenarios selected, a nonzero count, all passed, and no unexpected skips. Standard lanes, shard 2, the identity-plugin slice and CI gate and filter contracts stay intact. |

The `/ed-fi/students` witnesses in F5 were chosen for the existing core plus TPDM fixture. They deliberately avoid the Sample and Homograph expectations of the standard suite.

**Where the coverage lives** (paths relative to `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/`):
- F4 and F5: `Features/InstanceManagement/RouteQualifierDiscovery.feature` with `StepDefinitions/RouteQualifierStepDefinitions.cs`.
- F1–F3: `Features/InstanceManagement/RouteQualifierTokenIntrospection.feature` with `StepDefinitions/TokenIntrospectionStepDefinitions.cs`.
- Both reuse `Management/{DmsApiClient,TokenHelper,InstanceManagementContext,InstanceFixtureHydrator}.cs`.
- Expected credentials come from `CredentialsByTenant`, not the legacy first-tenant `ClientKey`.

## 9. Contracts the tests rely on

**Token introspection** (`TokenInfoModule`, `ApiService.CreateGetTokenInfoPipeline`, `GetTokenInfoHandler`):
- `tokenInfo` is an authenticated POST, not a public GET. It accepts JSON or form bodies; the routed tests use JSON only.
- The request flows through `FixedRoutePattern`, then the common tenant, JWT and authorized-store pipeline, then target and database validation, then the handler.
- The handler compares header and body tokens. It reads the claim set, application and profiles scoped to the tenant, and reads education organizations from the selected database.
- Status contract:

| Condition | Status |
| --- | --- |
| Success | 200 with `active: true`, caller `client_id`, claim-set fields and `Cache-Control: no-store` |
| Missing or invalid bearer | 401 |
| Missing body token, or header/body mismatch | 400 |
| No authorized tenant and store match | 404 |
| No authorized stores | 403 |
| Ambiguous store match | 400 |
| Missing store connection | 503 |

The 403, ambiguous-match 400 and 503 cases remain contracts but are not new scenarios in this scope.

**Metadata** (`DiscoveryEndpointModule`, `MetadataEndpointModule`, `XsdMetadataEndpointModule`):
- Metadata is public. Under tenancy, the routing context is validated; a JWT is not.
- Discovery puts the tenant first, then the configured qualifiers.
- Root or partial Discovery documents return 200 with `{placeholder}` templates. These are never requested literally.
- Missing-context multi-tenant metadata routes return a 404 `application/problem+json` with guidance and a correlation id, before any CMS access.
- That rule does not apply to the tenant-only XSD compatibility routes. Single-tenant root metadata stays usable.
- Unknown tenant or context and unknown XSD file are separate negative cases.

## 10. Test design rules

- **Fail fast.** Missing fixture state, absent links, URLs that are not concrete, and a wrong status, representation or content each fail immediately.
- **No fallbacks.** Do not use a cached token or a direct identity-provider fallback; either could hide a broken advertised `oauth` URL.
- **Client isolation.** Use separate anonymous and authenticated clients. Never change shared default authorization headers. Keep request and response state local to the scenario. Copy Discovery values before later requests replace the last response; never keep an element of a disposed `JsonDocument`.
- **No secrets in output.**
  - Never print tokens, fixture keys or secrets, Authorization headers, request bodies or raw introspection payloads.
  - Compare identities as booleans with a fixed safe message.
  - Keep credentials out of feature text, and sanitize diagnostics derived from client input.
- **Fixture invariants.**
  - The suite depends on three databases whose `EffectiveSchema` matches each engine, on fixture registration before the single post-registration restart, and on per-scenario hydration, ownership-guarded cleanup, environment restoration and `NODE_OPTIONS` handling. These stay as they are.
  - New scenarios make no data writes or resets, no CMS provisioning, no per-scenario restarts and no timing sleeps.
- **Keep failures distinct.** 401, 400 and 404 mean different things. An infrastructure or provisioning failure is not a product assertion result.
- **No duplicates.** Do not add unit tests for behavior the frontend routing unit tests already cover.

## 11. Out of scope

- Full-suite routed replay, new deployment modes or CI jobs, and copying resource or authorization suites.
- Assertions on education-organization hierarchy or profile contents, and new hierarchy seeding.
- Product endpoint, status or authorization changes.
- CMS changes and fixture lifecycle changes.
- Schema packages, DDL, effective-schema hash or mapping-version changes.
- CDC and OpenSearch.
- New dependencies and generic client or test frameworks.

No product defect was found. A defect found later needs its own diagnosis and agreed scope before any product code changes.

## 12. Residual risks and limits

| Risk or limit | Impact | Mitigation |
| --- | --- | --- |
| The combined tenant plus district/year fixture is not an exhaustive replay of independent routing flags or resources. | A defect specific to single-tenant-with-qualifiers or tenant-without-qualifiers deployments could escape E2E. | The frontend unit tests cover all four prefix shapes, and the standard suite covers resource breadth. Add a narrow lane only if a flag-specific branch is found uncovered. |
| Successful introspection on two years of one tenant shows each route works. It does not prove year-specific introspection data isolation. | Database-sensitive education-organization data is not checked per year. | Accepted. Testing it would need distinguishable scenario-owned data, which is out of scope. Scenarios must not assume an empty or non-empty hierarchy or profiles. |
| Runtime cost and the incremental CI time were not measured during the analysis. | No claim of zero regressions or of a cost ratio between the options. | Inspect both-engine TRX and timing when the follow-up is verified (F6). |
| Story wording predates #1048, #1104, #1263 and #1431, so part of the "bring in" request was already done. | Readers may expect endpoints to be missing entirely. | Section 5 records what existed. |
| DMS-750's "Deployment Mode and Routing" PDF and the linked ODS customization page could not be read. | No ODS-parity claim can be made. | No ODS-parity requirement is introduced. |
| The PostgreSQL Instance workflow uploads diagnostics directly, while the SQL Server one passes them through the sanitizer. | An existing asymmetry, outside this scope. | New tests must not add credential-bearing output. Workflow hardening is a separate change. |

## 13. References

**Jira:**
- [DMS-1237](https://edfi.atlassian.net/browse/DMS-1237)
- [DMS-1230](https://edfi.atlassian.net/browse/DMS-1230)
- [DMS-1262](https://edfi.atlassian.net/browse/DMS-1262)
- [DMS-1505](https://edfi.atlassian.net/browse/DMS-1505)
- [DMS-1660](https://edfi.atlassian.net/browse/DMS-1660)
- [DMS-750](https://edfi.atlassian.net/browse/DMS-750)
- [DMS-861](https://edfi.atlassian.net/browse/DMS-861), with children [DMS-852](https://edfi.atlassian.net/browse/DMS-852), [DMS-875](https://edfi.atlassian.net/browse/DMS-875), [DMS-880](https://edfi.atlassian.net/browse/DMS-880), [DMS-881](https://edfi.atlassian.net/browse/DMS-881), [DMS-886](https://edfi.atlassian.net/browse/DMS-886), [DMS-887](https://edfi.atlassian.net/browse/DMS-887)

**Pull requests:**
- [#757](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/757)
- [#1048](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1048)
- [#1104](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1104)
- [#1127](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1127) (SQL Server Instance lanes)
- [#1214](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1214)
- [#1263](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1263)
- [#1431](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1431)

**Design:**
- [Confluence spike design (v9, 2025-07-24)](https://edfi.atlassian.net/wiki/spaces/BD/pages/1055391745/Spike+Design+for+multitenancy+and+instance+management)
- [`reference/design/multitenancy-analysis.md`](../../design/multitenancy-analysis.md)
- [`docs/DATABASE-SEGMENTATION-STRATEGY.md`](../../../docs/DATABASE-SEGMENTATION-STRATEGY.md)
- [`docs/MULTI-TENANCY-GETTING-STARTED.md`](../../../docs/MULTI-TENANCY-GETTING-STARTED.md)
- [`docs/SCHOOL_YEAR_INSTANCES.md`](../../../docs/SCHOOL_YEAR_INSTANCES.md)

**Repository sources** (paths relative to the repository root):

| Area | Sources |
| --- | --- |
| Instance suite | `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/` — `Features/InstanceManagement/*.feature`, `StepDefinitions/{RouteQualifierStepDefinitions,TokenIntrospectionStepDefinitions}.cs`, `Management/{DmsApiClient,TokenHelper,InstanceFixtureHydrator,InstanceManagementContext}.cs`, `Hooks/{InstanceFixtureHooks,InstanceManagementCleanupHooks}.cs`, `README.md` |
| Fixture and CI | `build-dms.ps1` (`InstanceE2ETests`, `Register-InstanceE2EFixture`, `RunInstanceE2E`); `eng/docker-compose/.env.routeContext.e2e`; `.github/workflows/on-dms-pullrequest.yml` (Instance jobs, `dms-ci-gate`); `eng/ci/tests/DmsPullRequestCiBudget.Tests.ps1` |
| Routing and metadata | `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/` — `Modules/{DiscoveryEndpointModule,MetadataEndpointModule,XsdMetadataEndpointModule,TokenEndpointModule,TokenInfoModule}.cs`, `Infrastructure/FixedRoutePattern.cs`, `Content/MetadataRouteValidator.cs`; unit tests in `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/` — `Modules/{MetaDataModuleTests,XsdMetaDataModuleTests}.cs`, `Infrastructure/FixedRoutePatternTests.cs` |
| Introspection | `src/dms/core/EdFi.DataManagementService.Core/` — `ApiService.cs` (`CreateGetTokenInfoPipeline`), `Handler/GetTokenInfoHandler.cs`, `Middleware/{JwtAuthenticationMiddleware,ResolveDataStoreMiddleware,TenantValidationMiddleware}.cs`, `Security/ConfigurationServiceClaimSetProvider.cs` |
| Standard suite (option 1 analysis) | `src/dms/tests/EdFi.DataManagementService.Tests.E2E/` — `StepDefinitions/StepDefinitions.cs` (`AddDataPrefixIfNecessary`), `Authorization/AuthorizationDataProvider.cs` (`CreateClientCredentials`), `Features/General/{DiscoveryAPI,XSDMetadata}.feature`, `Features/Security/TokenIntrospection.feature`; `eng/docker-compose/.env.e2e`; `.github/workflows/dms-weekend-build.yml` |
