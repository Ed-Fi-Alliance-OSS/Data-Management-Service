# DMS-1440 sync with `main`: final local validation (Phase 5)

Run 2026-11-13 on Windows 11 ARM64, against branch `DMS-1440` after the merge of `main` at `ff84a3444`.

| | |
| --- | --- |
| Validated commit | `0d6e2e560`. Batches 1–2 ran at `b812d0dce`. The only change between the two is the parameter order in `build-dms.ps1`, which none of those lanes runs. The Pester lane was rerun at `0d6e2e560`. |
| Baseline | `main` at `ff84a3444`, in a detached worktree, used for every failure comparison below |
| PostgreSQL | PostgreSQL 18 container for the DMS and CMS integration lanes; the E2E stacks bring their own |
| SQL Server | LocalDB `DMS1440` (SQL Server 17.0.4025, **Express Edition**) in place of SQL Server: the amd64 SQL Server image does not run on this ARM64 host. Provisioning concurrency is 1. |
| Evidence | Local logs and TRX files (session scratchpad); not published |
| Pushed | Nothing |

## 1. Lanes

**Local (real engine):**

| # | Lane | Passed | Failed | Skipped / not run | Verdict |
| --- | --- | ---: | ---: | ---: | --- |
| V10 | Static: `RelationalMappingVersion`, mapping/DDL diff, lock files | 3 checks | 0 | — | Pass: `v3`; the only mapping-set code changes are log messages, which no longer print the schema hash; lock-file changes are all under `src/config` (the reader E2E project, the backend's HTTP dependency, and a one-line regeneration in 10 projects) |
| V1 | CSharpier on all 169 C# files the branch touches | 169 | 0 | — | Pass |
| V2 | DMS build + unit + `InstanceFixtureUnit` | 26,505 | 745 | 10 | Pass against baseline (§3.1) |
| V5a | CMS build + unit | 12,046 | 0 | 0 | Pass |
| V9 | CI Pester `$paths` (85 files, one process, Pester 5.7.1) | 6,369 | 343 | 183 | Pass against baseline on Windows (§3.3); Linux CI is authoritative |
| V4a/b | DMS API integration, PostgreSQL / plugin | 506 / 638 | 0 | 0 | Pass, 20 of 20 projection tests |
| V3a | DMS backend integration, PostgreSQL | 1,518 | 0 | 9 on demand | Pass |
| V5b | CMS integration, PostgreSQL | 2,066 | 0 | 0 | Pass |
| V7 | Instance E2E, PostgreSQL: reader project + Reqnroll | 38 + 137 | 0 | 0 | Pass; guard `28 of 28 live tests passed; 38 of 38 tests passed` |
| V8a | DMS E2E, DS 5.2, self-contained: Discovery 01 + the projection scenario | 2 | 0 | 0 | Pass |
| V8b | DMS E2E, DS 6.1 stack: `@StandardVersion-6_1` | 12 | 0 | 0 | Pass, includes the DS 6.1 Discovery scenario |
| V8c | DMS E2E, Keycloak: `(Category=@MssqlRepresentative)&(Category!=@MssqlOnly)`, then the Keycloak reader | 59 + 5 | 0 | 0 | Pass; guard `5 of 5 named live tests passed`; first full representative run after the cleanup fix |
| V6 | CMS E2E, self-contained, PostgreSQL | 325 | 0 | 608 | Pass; the skips are by configuration (§3.5) |

**SQL Server through LocalDB (substitute for the CI engine):**

| # | Lane | Passed | Failed | Skipped | Verdict |
| --- | --- | ---: | ---: | ---: | --- |
| V4c | DMS API integration, SQL Server | 512 | 0 | 1 | Pass, 21 of 21 projection tests; the skip is an existing code-level ignore of a non-projection test |
| V3b | DMS backend integration, `MssqlCiShard1..4` | 1,668 | 48 | 0 | Shards 1–3 pass; shard 4's 48 failures are a LocalDB deviation that matches `main` exactly (§3.2) |
| V5b | CMS integration, SQL Server | 2,166 | 0 | 0 | Pass, no SQL Server skips |

**CI only (pending; gate in spec §10):** SQL Server Instance E2E with the reader and its guard; SQL Server DMS E2E with Keycloak, including the Keycloak reader fixture and its guard line; SQL Server DMS E2E self-contained; and the SQL Server API, backend (shard 4 included) and CMS integration jobs on a real SQL Server engine. Also pending in CI: CMS E2E on SQL Server (representative and multitenant) and the Linux Pester lane.

## 2. Acceptance criteria: proving tests run in this validation

The table lists, for each AC, what the proving tests assert, which lane ran them and on which engine. "Unit" means the test passed in V2 (DMS) or V5a (CMS); those lanes had no failure outside `main`'s baseline assemblies. "API" names tests from `Given_{Postgresql,Mssql}_EducationOrganizationProjection*`.

| AC | Key assertion | Proving tests (lane, engine) | Status |
| --- | --- | --- | --- |
| 1 Discovery-advertised endpoint | Discovery advertises the two projection URL templates and the contract version; CMS reads only through them | Unit `DiscoveryModuleTests`, `EducationOrganizationProjectionEndpointModuleTests`, `DmsDiscoveryClientTests`, `ProjectionUrlTemplateResolverTests`; V8a/V8b Discovery 01 (DS 5.2 and 6.1); V7 "Discovery advertises the projection and token URL templates…" and "…walks the hierarchy two items at a time through the Discovery URLs"; reader `Given_the_seeded_store_read_two_items_a_page`, `Given_a_store_registered_without_a_school_year_context` (no page request) | Proven locally (PostgreSQL) |
| 2 Least-privilege bearer; expired tokens refused | Exact 401/403/500 per credential shape; expired → 401, the same claims unexpired → 200 | API `It_answers_the_authorization_matrix`, `It_rejects_a_bearer_token_outside_its_lifetime` (V4, both engines); unit `ServiceClaimAuthorizationMiddlewareTests`; V7 "Only a credential holding the projection claim…" and "The projection credential is denied by the resource API…"; V8a/V8c projection scenario (resource-only credential refused); V8c Keycloak reader `It_refuses_a_resource_only_credential_at_the_page_stage`, `It_reads_every_page_with_a_token_keycloak_issued` | Proven locally (PostgreSQL; SQL Server via LocalDB for the API tests); SQL Server Keycloak waits on CI |
| 3 Provider-neutral HTTP reader | Discovery once, one token, then only pages; no DMS or database dependency | Unit `ProjectionServiceTokenProviderTests`, `EducationOrganizationProjectionReaderTests`, `…CheckpointTests`, `ProjectionResponseParserTests`, `RedactionTests`, `BackendProjectBoundaryTests`; reader `It_reads_discovery_once_then_one_token_then_one_walk` (V7, PostgreSQL), Keycloak `It_sends_one_discovery_get_one_token_post_to_the_advertised_url_and_then_only_pages` and `It_resolves_the_production_reader` (V8c) | Proven locally; SQL Server Keycloak waits on CI |
| 4 Target resolution; failures classified with no rows | Missing, cross-tenant, not routable, unavailable and not provisioned each give their classified failure and no items | API `It_answers_each_target_state`, `It_isolates_tenants_stores_and_route_contexts`, `It_serves_a_changed_route_context_only_after_a_catalog_refresh`, `It_keeps_a_not_provisioned_verdict_until_the_host_restarts` (V4, both engines); V7 "A data store answers only in its own tenant and route context"; reader `Given_a_data_store_id_the_tenant_does_not_have`, `Given_a_store_whose_database_does_not_exist`, `Given_a_store_whose_database_was_never_provisioned`, `Given_a_store_registered_after_dms_loaded_the_tenant_catalog` (V7) | Proven locally, with the limits in §5 |
| 5, 13 Exactly SEA/ESC/LEA/School; extensions excluded | Four arms only; a seeded `PostSecondaryInstitution` is absent | Unit `EducationOrganizationProjectionSqlCompilerTests`; backend `Given_A_{Postgresql,Mssql}_Education_Organization_Projection_Set_Reader` (V3, both engines; SQL Server in shard 3); API walk test (V4, both engines) | Proven locally |
| 6 `edfi.*` discriminators only | No `Ed-Fi:` value exposed | API walk test (V4, both engines); V7 walk scenario; unit handler `Given_A_Stored_Discriminator_Outside_The_Allowlist` | Proven locally |
| 7 Parent precedence | Exact parent per precedence row | Backend set-reader fixtures (V3, both engines); API walk test; V7 walk scenario; reader `It_returns_every_item_with_its_parent_in_id_order`, Keycloak `It_reads_the_seeded_hierarchy_with_its_parents` | Proven locally |
| 8 Contradictions → stable non-transient problem | 409 `projection-data-invalid` | API `It_fails_closed_on_contradictory_relationships` (V4, both engines); backend set-reader contradiction cases (V3, both engines); unit `ProjectionSetValidatorTests` | Proven locally |
| 9 Deterministic int64 pages, empty store, opaque cursor | Pages in identifier order; changed set refused; cursor lifetime honored; empty store gives one empty page | API `It_refuses_a_changed_set_and_honors_the_cursor_lifetime`, `It_refuses_invalid_parameters_cursors_and_versions` (V4, both engines); backend `…_Under_Concurrent_Transactions` (V3, both engines); reader `Given_a_provisioned_store_with_no_education_organization` | Proven locally |
| 10 No success before every page | Any failed page leaves no items | Unit `EducationOrganizationProjectionReaderTests`; reader `Given_dms_answers_503_on_page_3`, `Given_the_projection_changes_on_page_2_twice` / `_four_times`, `Given_the_caller_cancels_while_page_2_is_outstanding`, `Given_a_transport_fault_on_page_2`, `Given_a_dms_base_url_that_closes_every_connection` (V7) | Proven locally |
| 11 Transient vs non-transient | Altered schema → `SchemaIncompatible`; unreachable host, lock timeout or deadlock → transient | Backend `…_Over_An_Altered_Schema` (V3, both engines); API `It_maps_read_failures_behind_a_cached_fingerprint_verdict`, `It_answers_an_unavailable_mapping_without_its_diagnostics`, `It_answers_a_lock_timeout_as_target_unavailable`, `It_answers_the_rate_limit_with_the_inherited_problem` (V4, both engines); unit failure and error-code tests | Proven locally |
| 12 Cancellation on both sides | A cancelled read stops and keeps no partial result | API `It_abandons_the_read_when_the_client_disconnects` (V4, both engines); unit handler and checkpoint cancellation tests; reader caller-cancel case (V7) | Proven locally |
| 14, 15 CMS never reads DMS tables; administrative access stays elsewhere | No database API anywhere in the reader; a complete read reaches only the DMS authority | Unit `BackendProjectBoundaryTests` (V5a); reader runtime evidence (V7, V8c) | Proven locally (source scan plus runtime evidence) |

**The ticket's verification bullets.** "Unavailable DMS" and "unavailable target data store": V7 reader. "Representative hierarchies, int64, empty store": AC 7 and 9. "Newly created store not yet visible": V4 and V7. That one has the cross-component limit in §5.

## 3. Triage records (spec §10)

### 3.1 DMS unit (V2)

745 failures, the same 745 test names as `main`'s baseline (SchemaTools 598, Backend.Cdc 147). For 734, the exception type, normalized message and first frame all match. The other 11 SchemaTools CDC provisioning tests reach the same exception type and message through a different first frame in the parallel run (`LocalCdcWorkflowJournalStore.EnsureDirectory` instead of `CdcWorkflowJournalValidation.Require`, with a `%TEMP%` teardown error). I reran that fixture twice in isolation on each tree, and all four runs were identical (the 11 at `Require`). The branch changes no CDC or SchemaTools file. **Pre-existing**; the first frame depends on test order on Windows.

### 3.2 SQL Server backend shard 4 (V3b)

48 of 700 failed. I reran the failing fixtures with the same filter on the same LocalDB instance, on `main` and on the branch: 69 tests, 21 passed and 48 failed on each side. The 48 failure records (test, exception type, normalized message, first project frame) differ in **0** cases, and they are the same tests that failed in the full shard. The branch touches none of these fixtures.

- 47 come from SQL Server Express limits: change data capture is unavailable (`MssqlCdcProviderAccessRetry`, `MssqlCdcSourcePositionAdapterTests`, `MssqlCdcProviderArtifacts*`), and so is backup compression (`MssqlGeneratedDdlBackupBaselineDatabase`).
- 1 is `DocumentCacheAdministrativeMutex`: the principal reads `dbo` under integrated security.

**Recorded as a LocalDB environment deviation.** CI's SQL Server shard 4 must pass them.

### 3.3 Pester (V9)

The first one-process run, at `b812d0dce`, found one **regression**. `BootstrapEntryPointWorkflow` requires `-DataStandardVersion` to be the last parameter of `build-dms.ps1`, and step 3.2 had declared the new switch after it. Fixed in `0d6e2e560` by declaring the switch first; the four affected files pass when run on their own (401 of 401).

Then the same `$paths` list ran in one process on `main` and on the branch, under the same conditions: `main` 6,332 passed and 338 failed; branch 6,369 passed and 343 failed.

- **338 are identical** (test and normalized message). Most of them are Windows host conditions: no `chmod`, `/bin/bash` or Unix file modes. In a single process, `pwsh`, `git` and `docker` also stop being found by later files.
- **5 exist only on the branch.** All are new tests: four `BuildScriptTestGuards` cases and one `E2EIdentityEnvironment` case. Each fails with "the term `pwsh` is not recognized", the same failure that hits 44 of `main`'s tests in the same run, including the sibling `E2EIdentityEnvironment` cases. All five pass when their files run in their own process at `0d6e2e560`.
- Under §10 these 5 cannot be pre-existing, because `main` has no such tests. They are recorded as explained by the shared-process `PATH` loss.

The Linux CI lane is authoritative.

### 3.4 Format baseline

V1 checked only the files the branch touches. `main`'s own CSharpier failures in `ClaimsHierarchyManager.cs` and `ClaimSetRepository.cs` are outside that set and unchanged.

### 3.5 CMS E2E skips (V6)

All 608 skips are gated by configuration, not caused by failures:
- 560: Keycloak characterization runs only with `CONFIGURATION_SERVICE_IDENTITY_PROVIDER=keycloak`.
- The rest: fixtures that need the secret-resolution script, the fan-out measurement flag or the isolated policy script, and the scenarios gated on them.

## 4. Deviations from the spec

- SQL Server runs on LocalDB (Express) rather than a SQL Server container. That accounts for §3.2, and every SQL Server lane is also listed as CI-pending.
- The never-provisioned reader fixture uses the engine's system database (`postgres`/`master`) instead of the CMS database (step 2.7, accepted).
- The stored correlation id follows the sanitizer, and the header is also used when a problem body has no id (step 2.3, accepted).
- `-RunDmsProjectionReaderKeycloakE2E` is declared before `-DataStandardVersion` (fix to step 3.2, `0d6e2e560`).

## 5. Open limits

- **Restart after provisioning (Q7/M14).** A not-provisioned verdict persists until the DMS host restarts. That is tested at the component level (API `It_keeps_a_not_provisioned_verdict_until_the_host_restarts`, both engines). Across components, only the not-provisioned classification is tested (reader `Given_a_store_whose_database_was_never_provisioned`); recovery after a restart is derived from the source, not tested.
- **Same-id visibility across the real CMS → DMS boundary.** Hidden → 404 → visible → 200 for the same registered id, with exact reload counts, is proven at the DMS catalog boundary only. Across components, only a newly registered id is proven (served on its first read without a restart). The same-id case stays a reported gap.
- **SQL Server with Keycloak.** The Keycloak reader ran locally on PostgreSQL only. The SQL Server Keycloak lane and its guard line are CI-only.
- **Linux Pester.** Its result comes from CI.

**Proposed follow-up for M14 (not filed).** Add an Instance E2E reader case in three steps: register a store whose database is missing, read it (not provisioned), then provision the database. Restart the DMS container, then assert that the next read succeeds. This would turn the source-derived recovery claim into a cross-component test. Alternatively, a product change could re-check the verdict after a bounded interval, which removes the restart requirement.

## 6. What is proven locally and what waits on CI

ACs 1 and 4–15 are proven locally on PostgreSQL. Their SQL Server proving tests also passed on LocalDB, except the E2E cases; in particular, the projection API tests (20 of 20 on PostgreSQL, 21 of 21 on SQL Server) and the backend projection fixtures (24 PostgreSQL tests, and 26 SQL Server tests in shard 3) passed. ACs 2 and 3 are proven locally on PostgreSQL in both identity modes, but their SQL Server Keycloak evidence waits on CI. Every SQL Server lane in the §10 gate table still needs its CI evidence on the tested head before completion or merge approval.
