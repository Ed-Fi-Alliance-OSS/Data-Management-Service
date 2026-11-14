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

All test counts come from the individual TRX results; for Pester, from Pester's own result object. "Not executed" counts every individual `NotExecuted` result. The console summaries and the TRX counters miss most of these: the DMS unit console reported 10 skipped against 2,531 in the TRX, and the CMS integration counters reported 0. §3.6 gives the reasons.

**Local (real engine):**

| # | Lane | Passed | Failed | Not executed | Verdict |
| --- | --- | ---: | ---: | ---: | --- |
| V10 | Static: `RelationalMappingVersion`, mapping/DDL diff, lock files | 3 checks | 0 | — | Pass: `v3`. The only mapping-set code changes are log messages, which no longer print the schema hash. Lock-file changes are all under `src/config`: the reader E2E project, the backend's HTTP dependency, and a one-line regeneration in 10 projects |
| V1 | CSharpier on all 169 C# files the branch touches | 169 | 0 | — | Pass |
| V2 | DMS unit (`build-dms.ps1 UnitTest`) | 26,406 | 745 | 2,531 | Pass against baseline (§3.1, §3.6) |
| V2 | `Category=InstanceFixtureUnit` | 49 | 0 | 0 | Pass |
| V5a | CMS unit | 12,046 | 0 | 2 | Pass |
| V9 | CI Pester `$paths` (85 files, one process, Pester 5.7.1) | 6,369 | 343 | 183 skipped, 0 not run | Pass against baseline on Windows (§3.3); Linux CI is authoritative |
| V4a/b | DMS API integration, PostgreSQL / plugin | 506 / 638 | 0 | 0 | Pass, 20 of 20 projection tests |
| V3a | DMS backend integration, PostgreSQL | 1,518 | 0 | 9 | Pass |
| V5b | CMS integration, PostgreSQL | 2,066 | 0 | 62 | Pass |
| V7 | Instance E2E, PostgreSQL: reader project + Reqnroll | 38 + 137 | 0 | 0 | Pass; guard `28 of 28 live tests passed; 38 of 38 tests passed` |
| V8a | DMS E2E, DS 5.2, self-contained: Discovery 01 + the projection scenario | 2 | 0 | 0 | Pass |
| V8b | DMS E2E, DS 6.1 stack: `@StandardVersion-6_1` | 12 | 0 | 0 | Pass, includes the DS 6.1 Discovery scenario |
| V8c | DMS E2E, Keycloak: `(Category=@MssqlRepresentative)&(Category!=@MssqlOnly)`, then the Keycloak reader | 59 + 5 | 0 | 0 | Pass; guard `5 of 5 named live tests passed`; first full representative run after the cleanup fix |
| V6 | CMS E2E, self-contained, PostgreSQL | 325 | 0 | 610 | Pass; every not-executed test is gated by configuration (§3.6) |

**SQL Server through LocalDB (substitute for the CI engine):**

| # | Lane | Passed | Failed | Not executed | Verdict |
| --- | --- | ---: | ---: | ---: | --- |
| V4c | DMS API integration, SQL Server | 512 | 0 | 1 | Pass, 21 of 21 projection tests |
| V3b | DMS backend integration, `MssqlCiShard1..4` | 1,668 | 48 | 2 | Shards 1–3 pass. Shard 4's 48 failures are a LocalDB deviation that matches `main` exactly (§3.2) |
| V5b | CMS integration, SQL Server | 2,166 | 0 | 66 | Pass |

**CI only (pending; gate in spec §10):**
- SQL Server Instance E2E with the reader and its guard.
- SQL Server DMS E2E with Keycloak, including the Keycloak reader fixture and its guard line.
- SQL Server DMS E2E, self-contained.
- The SQL Server API, backend (shard 4 included) and CMS integration jobs on a real SQL Server engine.
- CMS E2E on SQL Server (representative and multitenant).
- The Linux Pester lane, and every test that does not execute on Windows (§3.6).

## 2. Acceptance criteria: proving tests run in this validation

For each AC, the table lists what the proving tests assert, which lane ran them and on which engine.
- "Unit" means the test passed in V2 (DMS) or V5a (CMS). Those lanes had no failure outside `main`'s baseline assemblies.
- "API" names tests from `Given_{Postgresql,Mssql}_EducationOrganizationProjection*`.

| AC | Key assertion | Proving tests (lane, engine) | Status |
| --- | --- | --- | --- |
| 1 Discovery-advertised endpoint | Discovery advertises the two projection URL templates and the contract version; CMS reads only through them | Unit `DiscoveryModuleTests`, `EducationOrganizationProjectionEndpointModuleTests`, `DmsDiscoveryClientTests`, `ProjectionUrlTemplateResolverTests`; V8a/V8b Discovery 01 (DS 5.2 and 6.1); V7 "Discovery advertises the projection and token URL templates…" and "…walks the hierarchy two items at a time through the Discovery URLs"; reader `Given_the_seeded_store_read_two_items_a_page`, `Given_a_store_registered_without_a_school_year_context` (no page request) | Proven locally (PostgreSQL) |
| 2 Least-privilege bearer; expired tokens refused | Exact 401/403/500 per credential shape; expired → 401, the same claims unexpired → 200 | API `It_answers_the_authorization_matrix`, `It_rejects_a_bearer_token_outside_its_lifetime` (V4, both engines); unit `ServiceClaimAuthorizationMiddlewareTests`; V7 "Only a credential holding the projection claim…" and "The projection credential is denied by the resource API…"; V8a/V8c projection scenario (resource-only credential refused); V8c Keycloak reader `It_refuses_a_resource_only_credential_at_the_page_stage`, `It_reads_every_page_with_a_token_keycloak_issued` | Proven locally (PostgreSQL; SQL Server via LocalDB for the API tests); SQL Server Keycloak waits on CI |
| 3 Provider-neutral HTTP reader | Discovery once, one token, then only pages; no DMS or database dependency | Unit `ProjectionServiceTokenProviderTests`, `EducationOrganizationProjectionReaderTests`, `…CheckpointTests`, `ProjectionResponseParserTests`, `RedactionTests`, `BackendProjectBoundaryTests`; reader `It_reads_discovery_once_then_one_token_then_one_walk` (V7, PostgreSQL); Keycloak `It_sends_one_discovery_get_one_token_post_to_the_advertised_url_and_then_only_pages` and `It_resolves_the_production_reader` (V8c) | Proven locally; SQL Server Keycloak waits on CI |
| 4 Target resolution; failures classified with no rows | Missing, cross-tenant, not routable, unavailable and not provisioned each give their classified failure and no items | API `It_answers_each_target_state`, `It_isolates_tenants_stores_and_route_contexts`, `It_serves_a_changed_route_context_only_after_a_catalog_refresh`, `It_keeps_a_not_provisioned_verdict_until_the_host_restarts` (V4, both engines); V7 "A data store answers only in its own tenant and route context"; reader `Given_a_data_store_id_the_tenant_does_not_have`, `Given_a_store_whose_database_does_not_exist`, `Given_a_store_whose_database_was_never_provisioned`, `Given_a_store_registered_after_dms_loaded_the_tenant_catalog` (V7) | **Partly proven.** Proven across components: the classified failures, and a new store id served on its first read. Proven at the DMS component boundary only: the same registered id going from invisible to visible, and the restart that clears a not-provisioned verdict (§5) |
| 5, 13 Exactly SEA/ESC/LEA/School; extensions excluded | Four arms only; a seeded `PostSecondaryInstitution` is absent | Unit `EducationOrganizationProjectionSqlCompilerTests`; backend `Given_A_{Postgresql,Mssql}_Education_Organization_Projection_Set_Reader` (V3, both engines; SQL Server in shard 3); API walk test (V4, both engines) | Proven locally |
| 6 `edfi.*` discriminators only | No `Ed-Fi:` value exposed | API walk test (V4, both engines); V7 walk scenario; unit handler `Given_A_Stored_Discriminator_Outside_The_Allowlist` | Proven locally |
| 7 Parent precedence | Exact parent per precedence row | Backend set-reader fixtures (V3, both engines); API walk test; V7 walk scenario; reader `It_returns_every_item_with_its_parent_in_id_order`, Keycloak `It_reads_the_seeded_hierarchy_with_its_parents` | Proven locally |
| 8 Contradictions → stable non-transient problem | 409 `projection-data-invalid` | API `It_fails_closed_on_contradictory_relationships` (V4, both engines); backend set-reader contradiction cases (V3, both engines); unit `ProjectionSetValidatorTests` | Proven locally |
| 9 Deterministic int64 pages, empty store, opaque cursor | Pages in identifier order; changed set refused; cursor lifetime honored; empty store gives one empty page | API `It_refuses_a_changed_set_and_honors_the_cursor_lifetime`, `It_refuses_invalid_parameters_cursors_and_versions` (V4, both engines); backend `…_Under_Concurrent_Transactions` (V3, both engines); reader `Given_a_provisioned_store_with_no_education_organization` | Proven locally |
| 10 No success before every page | Any failed page leaves no items | Unit `EducationOrganizationProjectionReaderTests`; reader `Given_dms_answers_503_on_page_3`, `Given_the_projection_changes_on_page_2_twice` / `_four_times`, `Given_the_caller_cancels_while_page_2_is_outstanding`, `Given_a_transport_fault_on_page_2`, `Given_a_dms_base_url_that_closes_every_connection` (V7) | Proven locally |
| 11 Transient vs non-transient | Altered schema → `SchemaIncompatible`; unreachable host, lock timeout or deadlock → transient | Backend `…_Over_An_Altered_Schema` (V3, both engines); API `It_maps_read_failures_behind_a_cached_fingerprint_verdict`, `It_answers_an_unavailable_mapping_without_its_diagnostics`, `It_answers_a_lock_timeout_as_target_unavailable`, `It_answers_the_rate_limit_with_the_inherited_problem` (V4, both engines); unit failure and error-code tests | Proven locally |
| 12 Cancellation on both sides | A cancelled read stops and keeps no partial result | API `It_abandons_the_read_when_the_client_disconnects` (V4, both engines); unit handler and checkpoint cancellation tests; reader caller-cancel case (V7) | Proven locally |
| 14, 15 CMS never reads DMS tables; administrative access stays elsewhere | No database API anywhere in the reader; a complete read reaches only the DMS authority | Unit `BackendProjectBoundaryTests` (V5a); reader runtime evidence (V7, V8c) | Proven locally (source scan plus runtime evidence) |

**The ticket's verification bullets:**
- "Unavailable DMS" and "unavailable target data store": V7 reader.
- "Representative hierarchies, int64, empty store": AC 7 and 9.
- "Newly created store not yet visible": proven across components only for a **new** id registered after DMS loaded its catalog, which is served on its first read (V7). The same id going from not registered to visible is proven only at the DMS catalog boundary, against a CMS test double (V4). It is **not** proven across the real CMS → DMS boundary (§5).

## 3. Triage records (spec §10)

### 3.1 DMS unit (V2)

745 failures, the same 745 test names as `main`'s baseline (SchemaTools 598, Backend.Cdc 147).
- For 734, the exception type, normalized message and first frame all match.
- The other 11 are SchemaTools CDC provisioning tests. In the parallel run they reach the same exception type and message through a different first frame: `LocalCdcWorkflowJournalStore.EnsureDirectory` instead of `CdcWorkflowJournalValidation.Require`, with a `%TEMP%` teardown error.
- I reran that fixture twice in isolation on each tree. All four runs were identical, with the 11 failing in `Require`.
- The branch changes no CDC or SchemaTools file.

**Pre-existing**; which first frame these tests hit depends on test order on Windows.

### 3.2 SQL Server backend shard 4 (V3b)

48 of 700 executed tests failed. I reran the failing fixtures with the same filter on the same LocalDB instance, on `main` and on the branch: 69 tests on each side, 21 passed, 48 failed. The 48 failure records (test, exception type, normalized message, first project frame) differ in **0** cases, and they are the same tests that failed in the full shard. The branch touches none of these fixtures.
- 47 come from SQL Server Express limits: change data capture is unavailable (`MssqlCdcProviderAccessRetry`, `MssqlCdcSourcePositionAdapterTests`, `MssqlCdcProviderArtifacts*`), and so is backup compression (`MssqlGeneratedDdlBackupBaselineDatabase`).
- 1 is `DocumentCacheAdministrativeMutex`: the principal reads `dbo` under integrated security.

**Recorded as a LocalDB environment deviation.** CI's SQL Server shard 4 must pass them.

### 3.3 Pester (V9)

The first one-process run, at `b812d0dce`, found one **regression**. `BootstrapEntryPointWorkflow` requires `-DataStandardVersion` to be the last parameter of `build-dms.ps1`, and step 3.2 had declared the new switch after it. Fixed in `0d6e2e560` by declaring the switch first; the four affected files pass when run on their own (401 of 401).

I then ran the same `$paths` list in one process on `main` and on the branch, under the same conditions: `main` 6,332 passed and 338 failed; branch 6,369 passed and 343 failed.
- **338 are identical** (test and normalized message). Most of them are Windows host conditions: no `chmod`, `/bin/bash` or Unix file modes. In a single process, `pwsh`, `git` and `docker` also stop being found by later files.
- **5 exist only on the branch**, and all are new tests: four `BuildScriptTestGuards` cases and one `E2EIdentityEnvironment` case.
  - Each fails with "the term `pwsh` is not recognized". The same failure hits 44 of `main`'s tests in the same run, including the sibling `E2EIdentityEnvironment` cases.
  - All five pass when their files run in their own process at `0d6e2e560`.
  - Under §10 they cannot count as pre-existing, because `main` has no such tests. They are recorded as explained by the shared-process `PATH` loss.

The Linux CI lane is authoritative.

### 3.4 Format baseline

V1 checked only the files the branch touches. `main`'s own CSharpier failures in `ClaimsHierarchyManager.cs` and `ClaimSetRepository.cs` are outside that set and unchanged.

### 3.5 Timing watch list

Two of `main`'s tests failed once each in earlier full-suite runs on this branch. Neither cause was proven. Both stay on the watch list.

| Test | Earlier observation | Phase 5 (full DMS unit run, `b812d0dce`) |
| --- | --- | --- |
| `Given_CdcScenarioRunnerAccounting.It_records_noncooperative_work_as_failed_disposal_with_a_bounded_finalization` (`EdFi.DataManagementService.Tests.Unit`) | Failed once in the step 1.1 run (`"Running"` vs `"Failed"`; depends on a 100 ms finalization bound). Passed 5/5 in isolation on the merged and the `main` builds, and its assembly passed 652/652 twice | **Passed** (137 ms) |
| `Given_DocumentCacheReadAccelerationCoordinator.It_records_timed_out_direct_fill_duration_when_timeout_occurs_after_a_successful_candidate` (`Backend.Tests.Unit`) | Failed once in the step 2.1 run (expected log entry missing; races a real 25 ms timeout against a 30 s delayed candidate). Passed 5/5 in isolation on both builds, and its assembly passed 4,884/0 twice | **Passed** (56 ms) |

One more passing run does not prove that either test is stable. Both remain plausible timing flakes under full-suite parallel load, cause not proven. Neither is in the branch diff.

### 3.6 Not-executed results

**DMS unit (V2):** 2,531 on the branch against 2,530 on `main`'s baseline, by assembly:

| Assembly | Branch | `main` |
| --- | ---: | ---: |
| Backend.Cdc | 2,388 | 2,388 |
| SchemaTools | 129 | 129 |
| Core | 8 | 7 |
| DocumentCacheAdmin | 3 | 3 |
| Frontend.AspNetCore | 2 | 2 |
| Backend.Ddl | 1 | 1 |

Reasons on the branch:
- 2,518: `OneTimeSetUp: Not supported on Windows` (whole fixtures)
- 5: `Only supported on Linux`
- 6: symlink creation or Unix file modes unavailable
- 1: an MSSQL-only assertion
- 1: an on-demand measurement fixture

By test identity, exactly one result is not executed only on the branch: `It_reports_the_handler_cost`, the branch's own on-demand handler measurement. None is not executed only on `main`. **Implication:** about 2,500 tests, nearly all CDC and SchemaTools fixtures the branch does not change, have no local evidence on this host. Their only evidence is Linux CI.

**Other lanes:**

| Lane | Not executed | Reasons |
| --- | ---: | --- |
| CMS unit (Backend) | 2 | Timing-evidence collector, run only in its documented controlled environment |
| DMS API integration, SQL Server | 1 | An existing ignore of a non-projection test: a Section key-change PUT with a StudentSectionAssociation referrer fails on SQL Server (tracked separately) |
| DMS backend integration, PostgreSQL | 9 | On-demand measurement and plan evidence (6 deep-offset plan and timing fixtures, 1 component performance, 1 latency gate), plus the branch's projection cap measurement `Given_A_Postgresql_Education_Organization_Projection_Set_At_The_Cap` |
| DMS backend integration, SQL Server shard 4 | 2 | Two alias tests require a `localhost` or `127.0.0.1` data source; LocalDB connects through a named pipe |
| CMS integration, PostgreSQL / SQL Server | 62 / 66 | Operational probes, run manually with `Category=OperationalProbe`: 36 / 38 from one probe set, 26 / 28 from another |
| CMS E2E, self-contained | 610 | All gated by configuration: 560 Keycloak characterization; 14 that require the Keycloak identity provider; 10 that require a multi-tenant CMS; 9 secret-resolution proofs (run only by their own script); 8 fan-out measurement (run only with its flag); 5 policy scenarios (isolated script); 2 timing-evidence collectors; 2 isolated-run preflight checks |

Every other lane has 0 not-executed results. The Pester counts come from Pester itself (183 skipped, 0 not run).

## 4. Deviations from the spec

- SQL Server runs on LocalDB (Express) rather than a SQL Server container. That causes §3.2 and the 2 not-executed tests in shard 4 (§3.6), and every SQL Server lane is also listed as CI-pending.
- The never-provisioned reader fixture uses the engine's system database (`postgres`/`master`) instead of the CMS database (step 2.7, accepted).
- The stored correlation id follows the sanitizer, and the header is also used when a problem body has no id (step 2.3, accepted).
- `-RunDmsProjectionReaderKeycloakE2E` is declared before `-DataStandardVersion` (fix to step 3.2, `0d6e2e560`).

## 5. Open limits

- **Restart after provisioning (Q7/M14).** A not-provisioned verdict lasts until the DMS host restarts.
  - Component level: tested (API `It_keeps_a_not_provisioned_verdict_until_the_host_restarts`, both engines).
  - Across components: only the not-provisioned classification is tested (reader `Given_a_store_whose_database_was_never_provisioned`). Recovery after a restart is derived from the source, not tested.
- **Same-id visibility across the real CMS → DMS boundary.** For the same registered id, the sequence hidden → 404 → visible → 200, with exact reload counts, is proven at the DMS catalog boundary only. Across components, only a newly registered id is proven: it is served on its first read without a restart. The same-id case remains a reported gap.
- **SQL Server with Keycloak.** The Keycloak reader ran locally on PostgreSQL only. The SQL Server Keycloak lane and its guard line are CI-only.
- **Linux-only evidence.** Linux Pester, and about 2,500 unit tests that do not execute on Windows (§3.6).
- **Timing watch list.** Two of `main`'s timing-dependent tests passed this time, with causes unproven (§3.5).

**Proposed follow-up for M14 (not filed).** An Instance E2E reader case in three steps:
1. Register a store whose database is missing, and read it; the result should be "not provisioned".
2. Provision the database and restart the DMS container.
3. Assert that the next read succeeds.

This would turn the source-derived recovery claim into a cross-component test. Alternatively, a product change could re-check the verdict after a bounded interval, which removes the restart requirement.

## 6. What is proven locally and what waits on CI

| Coverage | ACs | Evidence and limits |
| --- | --- | --- |
| Fully proven locally on PostgreSQL | 1 and 5–15 | Their SQL Server proving tests that are not E2E also passed on LocalDB: the projection API tests (20 of 20 on PostgreSQL, 21 of 21 on SQL Server) and the backend projection fixtures (24 PostgreSQL tests, and 26 SQL Server tests in shard 3) |
| Partly proven | 4 | Classified failures and new-store visibility: proven across components. Same-id visibility and the restart that clears a not-provisioned verdict: proven only at the DMS component boundary; the cross-component cases stay open (§5) |
| Proven on PostgreSQL, SQL Server Keycloak pending | 2 and 3 | Proven in both identity modes; their SQL Server Keycloak evidence is CI-only |

Every SQL Server lane in the §10 gate table still needs its CI evidence on the tested head before completion or merge approval.
