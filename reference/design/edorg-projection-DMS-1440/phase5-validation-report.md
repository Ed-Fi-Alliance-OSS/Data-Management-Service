# DMS-1440 Phase 5 — final validation report

Run 2026-10-06 on Windows 11 (local Docker Desktop), against branch `DMS-1440`.

| | |
| --- | --- |
| Validated commit | `afcb8938e` (branch head before this report's commit; the report and spec change are documentation only) |
| Merge-base with `origin/main` | `5c964676f` (fetched 2026-10-06; the branch is 43 commits ahead and 6 behind; not rebased for this run) |
| Validation tree | Detached worktree `C:\dev\ed-fi\DMS-1440-validate` at `afcb8938e`, outside the main checkout, so the plugin-fixture NU1008 problem of nested worktrees does not apply and the custom-validation plugin fixtures were built and run |
| Merge-base tree | Detached worktree `C:\dev\ed-fi\DMS-1440-mergebase` at `5c964676f`, used only for the failure comparisons below |
| Evidence | `C:\dev\ed-fi\reviews\DMS-1440-phase5\` (logs, TRX files, summaries, the runner scripts). Paths below are relative to it. |
| Pushed | Nothing |

## 1. Result summary

Projection-specific tests passed. Broader lanes had the failures and skips detailed below; no branch regression was identified. Every remaining failure except one also fails, by test name, on the merge-base; the exception is a `DocumentCacheWriter` telemetry test that failed once in the full SQL Server shard 4 and passed in isolation on both the branch and the merge-base (§4). These results establish validation of `afcb8938e`, not of a future merged version.

| Lane | Passed | Failed | Skipped / not run | Verdict |
| --- | ---: | ---: | ---: | --- |
| Formatting (CSharpier, scoped directories) | — | 22 files | — | Pre-existing: none of the 22 files is in the branch diff |
| DMS unit (`build-dms.ps1 UnitTest`) | 20,992 | 644 | 10 | Only Backend.Cdc (63) and SchemaTools (581): identical names on the merge-base |
| Instance fixture unit (`Category=InstanceFixtureUnit`) | 37 | 0 | 0 | Pass |
| DMS backend integration, PostgreSQL | 1,331 | 0 | 9 explicit | Pass |
| DMS backend integration, SQL Server shards 1–4 | 1,444 | 3 | 0 | Shard 3 rerun clean; shard 4's 3 failures reconciled in §4 |
| DMS API integration, PostgreSQL | 353 | 0 | 0 | Pass |
| DMS API integration, SQL Server | 329 | 0 | 0 | Pass |
| DMS API plugin integration | 78 | 0 | 0 | Pass |
| Backend CDC integration (non-database) | 499 | 3 | 0 | Identical names on the merge-base |
| SchemaTools integration (no DB / PostgreSQL / SQL Server) | 47 / 81 / 87 | 0 / 7 / 4 | 0 / 8 / 15 | Branch failures are a subset of the merge-base's |
| CMS unit | 4,444 | 0 | 0 | Pass |
| CMS integration (PostgreSQL / SQL Server) | 964 / 1,050 | 0 | 0 | Pass |
| CMS E2E (PostgreSQL / SQL Server representative / SQL Server multi-tenant) | 235 / 24 / 10 | 0 | 10 / 0 / 0 | Pass (baseline skip set) |
| Instance Management E2E, PostgreSQL (reader + Reqnroll) | 30 + 117 | 0 | 0 | Pass; guard 20 of 20 live |
| Instance Management E2E, SQL Server (reader + Reqnroll) | 30 + 117 | 0 | 0 | Pass; guard 20 of 20 live |
| DMS E2E DS 6.1 version-coupled (PostgreSQL / SQL Server) | 12 / 12 | 0 | 0 | Pass, includes the DS 6.1 Discovery scenario |
| DMS E2E DS 5.2 Discovery scenario (focused) | 1 | 0 | 0 | Pass |
| DMS E2E DS 5.2 shard 4 (full) | — | — | interrupted, 0 results | Stopped by the host's low-memory reaper; replaced by the focused run (accepted) |
| CI Pester lane (57 files) | 3,451 | 6 | 72 | All 6 also fail on the merge-base; 5 platform-dependent, 1 cause not investigated |

## 2. Environment and run notes

- **Containers.** DMS integration: `dms-pg-integration-1440` (PostgreSQL 16, `127.0.0.1:5438`, tmpfs, `max_locks_per_transaction=256`, `wal_level=logical`) and `dms-mssql-integration-1440` (SQL Server 2025, `127.0.0.1:14341`, 8 GB, tmpfs 4 GB, `MSSQL_MEMORY_LIMIT_MB=4096`, Agent running). CMS integration: `cms-pg-integration-1440` (5432, trust) and `cms-mssql-integration-1440` (`127.0.0.1:14340`). All four were restarted before the integration lanes and stopped before the E2E lanes, to keep memory for the E2E stacks.
- **Images.** Each CMS E2E lane rebuilt `ed-fi-api-config-local` from the validation tree. `InstanceE2ETest` (without `-SkipDockerBuild`) removed and rebuilt both local images per engine; the DMS image the later DMS E2E lanes ran is `ed-fi-api-local:latest` `c1ecc59ed23d`, built 15:19 by the SQL Server Instance lane from the validation tree. `E2ETest` builds `local/ed-fi-api` while compose runs `ed-fi-api-local`, so the DS 6.1 and Discovery runs used `c1ecc59ed23d` — same source commit.
- **Standby (first SQL Server shard 3 pass).** The machine entered Modern Standby 10:26–11:59 (Windows Kernel-Power events 506/507). The Docker VM froze mid-provisioning; the four fixtures provisioning at that moment hit `Execution Timeout Expired` after 1 h 37 m. A keep-awake request was held afterwards; it was later stopped by the host's low-memory reaper.
- **Low-memory reaper.** Claude Code stopped the background E2E batch during the full DS 5.2 shard 4 lane, before any test result. The DS 6.1 lanes in the same batch had completed.
- **Process isolation.** Each CMS E2E, Instance E2E and DMS E2E lane ran in its own `pwsh -NoProfile` process with `NODE_OPTIONS` cleared.

## 3. Lane detail

### 3.1 Formatting

`dotnet csharpier check src/dms/core src/dms/backend src/dms/frontend src/dms/tests src/config` (CSharpier 1.2.5): 22 files reported, **0 in the branch diff** (`git diff --name-only 5c964676f afcb8938e`), so each is byte-identical to the merge-base. They include `ClaimSetRepository.cs` (both engines), `ClaimsHierarchyManager.cs`, several CMS and DMS test files, the `CustomValidation` plugin tests and `CustomValidationFixturePlugin.targets`. Evidence: `format/csharpier-out.txt`, `format/branch-files.txt`.

### 3.2 DMS unit

`./build-dms.ps1 Build -Configuration Release` (the first attempt failed on a file lock: two projects published the `Acme.DmsContributor` plugin fixture concurrently; the incremental retry succeeded with 0 warnings and 0 errors), then `./build-dms.ps1 UnitTest -Configuration Release`.

| Project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Core | 7,015 | 0 | 7 |
| Backend | 4,198 | 0 | 0 |
| Backend.Plans | 2,528 | 0 | 0 |
| Backend.Ddl | 1,336 | 0 | 1 |
| Frontend.AspNetCore | 1,179 | 0 | 2 |
| Backend.RelationalModel | 907 | 0 | 0 |
| Tests.Unit | 622 | 0 | 0 |
| Performance.Harness | 487 | 0 | 0 |
| DocumentCacheAdmin | 197 | 0 | 0 |
| Backend.Cdc | 2,225 | 63 | 0 |
| SchemaTools | 298 | 581 | 0 |

The two failing projects were rerun on the branch build and on the merge-base, with the same command: 63/63 and 581/581 failing names identical (0 differences). The cause is the known Windows CDC workflow journal (`CdcWorkflowStateException: CDC workflow state: Unavailable`) and Windows path assertions. Evidence: `unit/dms-unit.log`, `unit/br-*.fail`, `unit/mb-*.fail`.

`dotnet test src/dms/tests/EdFi.InstanceManagement.Tests.E2E/... --no-build --filter "Category=InstanceFixtureUnit"` (the CI step beside the unit suite): 37 of 37. Evidence: `unit/instance-fixture-unit.log`.

### 3.3 DMS integration

Every `*.Tests.Integration` assembly of `src/dms`, with each CI job's filter, from the Release build: `run-integration.ps1`. Connection strings: `ConnectionStrings__DatabaseConnection=host=127.0.0.1;port=5438;…` and `ConnectionStrings__MssqlAdmin=Server=127.0.0.1,14341;…`. Evidence: `integration/<lane>.trx|.log`, `integration/summary.txt`.

| Lane (filter) | Total | Passed | Failed | Not run | Time |
| --- | ---: | ---: | ---: | ---: | --- |
| Backend PostgreSQL (all) | 1,340 | 1,331 | 0 | 9 | 9 m |
| Backend SQL Server `MssqlCiShard1` | 279 | 279 | 0 | 0 | 12 m |
| Backend SQL Server `MssqlCiShard2` | 245 | 245 | 0 | 0 | 14 m |
| Backend SQL Server `MssqlCiShard3`, first pass | 316 | 307 | 9 | 0 | 2 h (standby) |
| Backend SQL Server `MssqlCiShard3`, rerun | 316 | 316 | 0 | 0 | 21 m |
| Backend SQL Server `MssqlCiShard4` | 607 | 604 | 3 | 0 | 39 m |
| API `Category=PostgresqlIntegration` | 353 | 353 | 0 | 0 | 6 m |
| API `Category=MssqlIntegration` | 329 | 329 | 0 | 0 | 30 m |
| API `Category=PluginIntegration` | 78 | 78 | 0 | 0 | 1 m |
| Backend CDC `Category!=DatabaseIntegration` | 502 | 499 | 3 | 0 | <1 m |
| SchemaTools `Category!=DatabaseIntegration` | 47 | 47 | 0 | 0 | <1 m |
| SchemaTools `Category=PostgresqlIntegration` | 96 | 81 | 7 | 8 | 3 m |
| SchemaTools `Category=MssqlIntegration` | 106 | 87 | 4 | 15 | 11 m |

- **Backend PostgreSQL, 9 not run.** All are `[Explicit]` measurement fixtures: DMS-1331 deep-offset measurements (6), DMS-1313 document-cache performance evidence (1), the warm steady-state write latency gate (1) and this ticket's `Given_A_Postgresql_Education_Organization_Projection_Set_At_The_Cap` (1, opt-in by design).
- **Projection fixtures.** Every projection test in the lanes that carry them passed: the PostgreSQL provider reader (backend PostgreSQL lane), the SQL Server provider reader, altered-schema and concurrent-transaction fixtures (shard 3 rerun), and the API projection scenarios (both API lanes).
- **API lanes.** The §11 filters (`Category=PostgresqlIntegration` / `MssqlIntegration`) were used; they include CI's `Category=ApiIntegration&…` sets. No `[Ignore]`d projection test remains (the step 2.8 SQL Server lone-surrogate test runs and passes).

### 3.4 CMS

From the validation tree, `run-cms.ps1`, with `ConnectionStrings__MssqlAdmin=Server=127.0.0.1,14340;…`:

| Command | Result |
| --- | --- |
| `./build-config.ps1 Build -Configuration Release` | Succeeded |
| `./build-config.ps1 UnitTest -Configuration Release` | Backend.Tests.Unit 2,585 / 0 / 0; Frontend.AspNetCore.Tests.Unit 1,859 / 0 / 0 |
| `./build-config.ps1 IntegrationTest -Configuration Release` | Backend.Postgresql 964 / 0 / 0; Backend.Mssql 1,050 / 0 / 0 (0 skipped proves the SQL Server variable reached the process) |

The unit total includes `BackendProjectBoundaryTests` (no CMS → DMS reference) and the CMS `RedactionTests`. Evidence: `cms/`.

### 3.5 CMS E2E

`run-cms-e2e.ps1`, one fresh process per lane; each lane's log names `EdFi.DmsConfigurationService.Tests.E2E.dll` and a totals line (not the empty-assembly false green). Evidence: `cms-e2e/`.

| Command | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained` | 235 | 0 | 10 |
| `… -EnvironmentFile "./.env.config.mssql.e2e" -E2ETestFilter "TestCategory=MssqlRepresentative"` | 24 | 0 | 0 |
| `… -EnvironmentFile "./.env.config.mssql.multitenant.e2e" -E2ETestFilter "TestCategory=MssqlMultitenantRepresentative"` | 10 | 0 | 0 |

The PostgreSQL lane's 10 skips are the multi-tenant `Tenants` and `Jobs` scenarios, which only the multi-tenant lane runs; it matches the step 1.1 run (235 / 0 / 10). ApiClients 09 and 15 also print as skipped and are excluded from the totals (undefined step bindings, pre-existing). The SQL Server lanes ran against `dms-mssql`.

### 3.6 Instance Management E2E (cross-component)

`./build-dms.ps1 InstanceE2ETest -Configuration Release -DatabaseEngine postgresql|mssql` (`run-instance-e2e.ps1`, images rebuilt per engine). Evidence: `instance-e2e/<engine>.log`, `instance-e2e/<engine>-trx/`.

| Engine | Reader project (`Tests.DmsProjectionE2E`) | Reader TRX guard | Reqnroll Instance suite | Time |
| --- | --- | --- | --- | --- |
| PostgreSQL | 30 / 0 / 0 | `DMS projection reader E2E: 20 of 20 live tests passed; 30 of 30 tests passed.` | 117 / 0 / 0 | 14 m |
| SQL Server | 30 / 0 / 0 | `DMS projection reader E2E: 20 of 20 live tests passed; 30 of 30 tests passed.` | 117 / 0 / 0 | 17 m |

The reader TRX files are `EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.postgresql.trx` and `….mssql.trx`. The reader ran before the Reqnroll suite, as approved in step 4.2.

### 3.7 DMS E2E: DS 6.1 and Discovery

`run-dms-e2e.ps1` (fresh process per lane) and one foreground run. Evidence: `dms-e2e/`.

| Command | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| `./build-dms.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -DataStandardVersion 6.1 -TestFilter 'Category=@StandardVersion-6_1'` | 12 | 0 | 0 |
| Same, `-SkipDockerBuild -DatabaseEngine mssql` | 12 | 0 | 0 |
| `./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category=@e2e-ci-shard-4'` | interrupted | — | 0 results |
| `./build-dms.ps1 E2ETest -Configuration Release -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Name=_01GETReturnsTheRootDiscoveryAPIDocument'` | 1 | 0 | 0 |

- The DS 6.1 lane's 12 scenarios include `_01GETReturnsTheRootDiscoveryAPIDocumentDS6_1` (the CI comment still says eight scenarios; the category now selects twelve).
- The focused run's TRX (`dms-e2e/ds52-discovery-trx/…filtered.trx`) records exactly one result, `_01GETReturnsTheRootDiscoveryAPIDocument` (the DS 5.2 variant), passed; the DMS container ran `c1ecc59ed23d` with the DS 5.2 ApiSchema packages and `AppSettings__Datastore=postgresql`. The `Name=` filter excludes the DS 6.1 variant, whose name ends in `DS6_1`.
- The full shard 4 lane is recorded as **interrupted, zero results**; the reviewer accepted the focused Discovery run in its place.

### 3.8 CI Pester lane

The 57 paths of `run-bootstrap-pester-tests` in `.github/workflows/on-dms-pullrequest.yml`, Pester 5.7.1, run in the foreground in slices (`run-pester.ps1`; a backgrounded run loses `docker`/`dotnet`/`pwsh` from `PATH`). Evidence: `pester/`.

| Slice (file numbers) | Passed | Failed | Skipped | Failed containers / blocks |
| --- | ---: | ---: | ---: | --- |
| 1–5 | 389 | 2 | 1 | 0 / 0 |
| 6–15 | 770 | 0 | 0 | 0 / 0 |
| 16–31 | 1,020 | 0 | 43 | 0 / 0 |
| 32 (AzureVmDeployment) | 19 | 0 | 0 | 0 / 0 |
| 33–46 | 889 | 4 | 0 | 0 / 0 |
| 47–57 | 364 | 0 | 28 | 0 / 0 |
| **Total** | **3,451** | **6** | **72** | **0 / 0** |

Branch-changed or reviewer-named files, run individually: `EducationOrganizationProjectionToggleWiring` 5 / 0 / 0, `InstanceE2EForwarding` 69 / 0 / 0, `DmsPullRequestCiBudget` 125 / 0 / 0, `BuildScriptTestGuards` 12 / 0 / 0.

**Failures** (all six also fail on the merge-base tree, which has no generated E2E state; `pester/mergebase-failing-files.txt`). Five are platform-dependent; the `E2EEngineForwarding` failure reproduces on the merge-base but its cause has not been investigated, so it is not classified as Windows-only:

| File :: test | Reason |
| --- | --- |
| `BootstrapSchemaAndSecuritySelection` :: run.sh clears stale generated package output… | `chmod` is not a Windows command (Linux-only step) |
| `BootstrapSeedDelivery` :: Resolve-BootstrapDataStandard rejects a missing extracted directory with the resolved path | The expected message is a `-like` pattern built from a Windows path, whose backslashes act as wildcard escapes |
| `E2EEngineForwarding` :: composes an explicit feature overlay before the data-standard and database-engine overlays | `Expected New-DataStandardDerivedEnvFile to be called 1 times exactly, but was called 0 times`; cause not investigated (identical on the merge-base) |
| `E2ETeardownSafety` :: blocks recursive cleanup for inventoried nested state… | `CreateDirectory` with Unix file modes is not supported on Windows |
| `E2ETeardownSafety` :: cleans private API CDC artifacts only after every down succeeds (×2) | Same `CreateDirectory` Unix-mode exception |

**Skips (72):** `MssqlPhysicalDistinctnessLive` 36 and `MssqlCollationParity` 7 (live SQL Server parity, no server configured), `SchemaSnapshotSecurity` 13, `ScheduledMergeQueueEnqueue` 12, `StockImageProofWorkflow` 2, `NorthridgeFailClosedGuards` 1 (symbolic links need administrator rights), `BootstrapSchemaAndSecuritySelection` 1.

## 4. Remaining failures and their merge-base results

| Lane | Failure(s) | Merge-base result | Reason |
| --- | --- | --- | --- |
| DMS unit, Backend.Cdc | 63 | Same 63 names | Windows CDC workflow journal (`LocalCdcWorkflowJournalStore`) |
| DMS unit, SchemaTools | 581 | Same 581 names | Same journal plus Windows path assertions |
| Backend SQL Server shard 3, first pass | 9 (`…Propagated_Reference_Identity_Runtime_With_The_Authoritative_DS52_Survey_Fixture` ×3, `…Relational_Write_Smoke_With_The_Authoritative_Sample_StudentAcademicRecord_Fixture` ×4, `…ClassPeriod_To_BellSchedule_Child_Binding` ×1, `…Projection_Set_Reader_Over_An_Altered_Schema.It_reports_every_arm_of_another_column_type_as_a_data_type_incompatibility` ×1) | Not needed: the branch rerun passed 316 / 316 | Modern Standby 10:26–11:59 froze the Docker VM during provisioning; `Execution Timeout Expired` after 1 h 37 m |
| Backend SQL Server shard 4 | 3 (below) | The 2 alias-mutex failures reproduce alone on both; the third did not reproduce (passes alone on both) | See the reconciliation |
| Backend CDC integration | 3 × `Given_CdcControllerFixtureHooks.It_reopens_the_same_durable_state_after_a_journal_write_interruption(…)` | Same 3 names | Windows CDC workflow journal |
| SchemaTools PostgreSQL | 7 failed + 8 not run | Same 15 names | 3 × `psql` not on the Windows `PATH`; 4 × managed provisioning (`CdcWorkflowStateException`, `CreateDatabaseIfNotExists` assertion) in `Given_Managed_Database_Provisioning_With_A_Real_Provider("pgsql")`; 8 CDC tests skip because SchemaTools reads its own `appsettings.json` and reached the 5432 server (`wal_level=replica`) |
| SchemaTools SQL Server | 4 failed + 15 not run | The same 4 + 15, plus 7 more failures (120 s provisioning timeouts) | Managed provisioning fixture (`"mssql"`), as above; 15 tests require `DMS_CDC_ISOLATED_SQLSERVER=1` |
| CI Pester lane | 6 | Same 6 names | 5 platform-dependent; `E2EEngineForwarding` cause not investigated; see §3.8 |

**Shard 4 reconciliation (604 / 607).** 607 executed = 604 passed + 3 failed:

1. `Given_A_Mssql_DocumentCacheAdministrativeMutex.It_serializes_alias_connections_to_the_same_database` — `TimeoutException` (6 s); fails alone on the branch and on the merge-base.
2. `Given_A_Mssql_RepresentationRestampStore.It_serializes_aliases_of_the_same_physical_database_through_the_administrative_mutex` — `TimeoutException` (7 s); fails alone on the branch and on the merge-base.
3. `Given_A_Mssql_DocumentCacheWriter.It_retries_transient_locked_lifecycle_read_failures_before_classification` — telemetry assertion in the full shard; did **not** reproduce: the isolated reruns on the branch and on the merge-base both passed.

Items 1 and 2: the tests' `LoopbackDataSourceAlias` rewrites `127.0.0.1` to `localhost` to reach the same server under another name; the local container publishes on `127.0.0.1` only, so the alias connection tries `::1` first and times out. CI's container publishes on all interfaces. Not re-verified by rebinding the port. Item 3: the test relies on a 250 ms delay to provoke a 100 ms lock timeout, which supports timing sensitivity under shard load, and neither the test nor the writer is in the branch diff; its cause is not conclusively established. Accepted at review as a nonblocking residual risk. Neither file nor the code under test is in the branch diff. Evidence: `integration/backend-ms-4*.trx`.

## 5. Review checklist (§11)

| Item | Evidence |
| --- | --- |
| Authentication denial matrix, four credential shapes | Instance projection feature (both engines, 117 / 117) and both API lanes |
| Tenant isolation | Instance projection feature (cross-tenant 404), API lanes, reader E2E |
| Exact hierarchy values incl. slot-corruption and non-selected references; nullable fields; large ids | API lanes and backend provider-reader fixtures (both engines) |
| Empty-store precondition | Reader E2E scenario (b), both engines |
| §4.6 concurrency suite, both engines | Backend PostgreSQL lane and shard 3 rerun |
| Stage-separated classification (dropped column / changed type permanent; unresolvable host transient), both providers | Backend provider-reader fixtures, both engines |
| Engine-appropriate Unicode body bound | API lanes (both engines) |
| Cache lag (store registered after DMS start → reload on miss) | Reader E2E scenario (d), both engines |
| Failed later pages through the production reader | Reader E2E scenarios (f), (g), both engines |
| Trace-level redaction in both services | DMS Core and Frontend unit suites; CMS `RedactionTests` (unit) |
| `RelationalMappingVersion == "v3"` | `SchemaHashConstants.cs` line 28, unchanged |
| No CMS → DMS reference | CMS `BackendProjectBoundaryTests` (in the 2,585) |
| Lock files | See below |

The evidence column maps each item to the suites that carry it; the projection tests in those suites passed (SQL Server shard 3 in its rerun). The per-test inventory was approved at the corresponding steps and was not re-derived here.

**Lock files.** `git diff 5c964676f afcb8938e -- '*packages.lock.json'` touches only CMS projects and contains exactly the two approved exceptions:

1. **Step 3.1** (`Microsoft.Extensions.Http`): `EdFi.DmsConfigurationService.Backend/packages.lock.json` +56 lines adding `Microsoft.Extensions.Http` and its transitive `Microsoft.Extensions.Configuration`, `.Configuration.Binder`, `.Diagnostics` and `.Options.ConfigurationExtensions`; one added project-dependency line in each of the nine dependent CMS projects (Installer, Keycloak, OpenIddict, Mssql, Postgresql, both integration test projects, Backend.Tests.Unit, Frontend.AspNetCore.Tests.Unit).
2. **Step 4.2** (new test project): `src/config/tests/EdFi.DmsConfigurationService.Tests.DmsProjectionE2E/packages.lock.json`, a new file (312 lines).

No DMS lock file changed. The validation builds left the validation tree clean (`git status` empty).

## 6. Corrections recorded in the spec

- **Discovery selection.** `DiscoveryAPI.feature` scenario 01 (DS 5.2) carries `@e2e-ci-shard-4`, not shard 1 as §11 said; its DS 6.1 variant carries `@StandardVersion-6_1` and runs only in the DS 6.1 lanes. §11 now selects the DS 5.2 scenario by name and runs the DS 6.1 lanes on both engines.
- **Integration commands.** `build-dms.ps1 IntegrationTest` runs every `*.Tests.Integration` assembly whatever `-DatabaseEngine` says. §11 now lists the CI jobs' assemblies and filters.
- **Pester lane** and the **nested-worktree** note added to §11.

## Appendix A. Step 2.5 and 2.6 measurement tables

Unchanged since their approval at step 2.6; reproduced from `docs/EDUCATION-ORGANIZATION-PROJECTION.md` at `afcb8938e` (sections "Effect on writers and measured cost" and "Handler cost and complete reads").

#### Effect on writers and measured cost

Because every page reads the whole set, a complete read of `N` items in pages
of `limit` reads the set `ceil(N / limit)` times. At the default cap (50,000
items) and default page size (2,000), that is 25 reads.

- **PostgreSQL.** `REPEATABLE READ` reads a snapshot, so ordinary row writes
  (`INSERT`, `UPDATE`, `DELETE`) neither block the read nor are blocked by it.
  A conflicting table-level lock, such as one taken by DDL or `LOCK TABLE`,
  does block the read, up to the read's lock timeout.
- **SQL Server.** `SERIALIZABLE` holds shared and range locks on the rows of
  the four education organization tables until the read's transaction ends.
  A writer to one of those tables can wait for the read in progress. When other
  readers or writers are queued too, waits form blocking chains, so no general
  bound on a writer's wait follows from the duration of one read. A writer
  transaction that holds a row lock the read needs and then needs one the read
  holds deadlocks with it, and SQL Server aborts one of the two. When the read is
  aborted, DMS answers `503 target-unavailable` for the client to retry. DMS
  provisioning enables read committed snapshot and snapshot isolation on the
  databases it creates, but the read uses `SERIALIZABLE`, which takes these
  locks either way.
  After the read, DMS returns the session to `READ COMMITTED`. A connection
  whose cleanup cannot be confirmed is not reused.

  The following were observed in the measurements below, and the plan or the
  victim choice could differ elsewhere. The read took the four tables in the
  order State Education Agency, Education Service Center, Local Education
  Agency, then School. A transaction that updated a School and then a Local
  Education Agency deadlocked with it, and SQL Server chose the read, which had
  written nothing, as the victim every time.

A provider read that succeeds is not the same as a complete read of the set. A
complete read also needs every page's digest to match the first page's, so any
committed change to projected content between pages restarts it with `409
projection-changed`. Reducing lock conflicts, for example with snapshot
isolation per page, would not prevent those restarts.

Provider measurements at the cap (step 2.5) follow. Read times cover the
provider reader alone, without validation, hashing or serialization. Each writer
phase runs four concurrent writers for 2,000 operations, first alone and then
while one client reads the set back-to-back, which is how a full walk reads.
Every number counts provider reads only: a full walk can still restart, as
described above.

**With production writes.** The hierarchy is created through the API, and the
writers change it through the API with `PUT`. They rename the state agency,
service centers, local agencies and schools, and they change relationships:
moving a School to another Local Education Agency and changing a Local Education
Agency's parent. SQL Server runs with the isolation settings DMS provisioning
enables on new databases (read committed snapshot and snapshot isolation).

| | PostgreSQL 16 | SQL Server 2025 |
| --- | --- | --- |
| Read time, median (min-max of 10) | 125 ms (76-187) | 148 ms (113-272) |
| Writes alone: p50 / p95 / max | 13 / 56 / 104 ms | 12 / 140 / 484 ms |
| Writes during reads: p50 / p95 / max | 21 / 51 / 94 ms | 19 / 213 / 466 ms |
| Writes during reads, p95 by kind | 47-56 ms, every kind | School renames and moves 37-46 ms; state agency, service center and local agency writes 208-227 ms |
| Lock waits during reads | writers waiting in 9 of 378 samples | 828 waits, 75.2 s total; writers waiting in 766 of 919 samples, the read in 20 |
| Write failures; deadlocks | none; 0 | none; 0 |
| Reads that succeeded | 93 of 93 | 167 of 167 |

**Stress case, without production writes.** Direct SQL writers on a database
without read committed snapshot. The writers include a transaction that updates a
School and then a Local Education Agency, the reverse of the order the read took
the tables in.

| | PostgreSQL 16 | SQL Server 2025 |
| --- | --- | --- |
| Bytes received per read | 5.25 MB (105 bytes per row) | 6.24 MB (125 bytes per row) |
| Single-row writers during reads: p95; deadlocks; reads that succeeded | not run separately | 101 ms; 0; 123 of 123 |
| With the reverse-order transaction, during reads: p95; deadlocks; reads that succeeded | 6.3 ms; 0; 13 of 13 | 98 ms; 65; **10 of 75** |

Every one of the 65 SQL Server deadlocks had the same shape. The read held
key-range locks on Local Education Agency rows and waited for a School row,
while the writer held that School row and waited for a Local Education Agency
row. The read was the victim each time. The production write pipeline did not
reproduce this shape.

Notes:

- **Lock waits** on SQL Server are the server's `LCK_*` totals over the phase,
  for readers and writers together. On both engines, waiting sessions are also
  sampled every 20 ms and split into writers and the read by statement text.
- **Test conditions:** one workstation; local containers with their data on
  tmpfs; 1 state education agency, 10 service centers, about 1,000 local
  education agencies and about 49,000 schools; names of about 30 characters.
- **Limits:** these are indications, not guarantees. Bytes scale with name
  length. Handler cost (validation, digest and response) is measured
  separately, below.

#### Handler cost and complete reads

Every page also validates and hashes the whole set before it returns its
items. Measured at the cap (50,000 items) and the default page size (2,000),
so a complete read is 25 pages.

**Handler alone** (step 2.6), with the set already read, so no database time.
Two runs, median per page:

| Per page | Median | 95th percentile |
| --- | --- | --- |
| Handler time | 11-18 ms | 29-34 ms |
| Validation / parent selection / digest | 2.0-4.1 / 1.3 / 3.7-8.2 ms | 6.7 / 16 / 5.4-9.9 ms |
| Managed memory allocated | 6.5 MB | 6.5-6.6 MB |
| Of which: validation / parent selection / digest / response items | 1.6 / 3.4 / 0.0 / 1.5 MB | same |
| Response body (2,000 items) | 322 KB | 322 KB |

A 25-page read spends 0.35-0.56 s in the handler (median of five). The digest
formats its canonical bytes into one reusable buffer; before it did, it
allocated 23.4 MB a page and the handler 30 MB, about 750 MB over a complete
read.

**Complete reads.** These measurements run the request parsing and the handler
over the production provider reader against the databases described above, one
page after another, in the order a client reads. Runtime: .NET 10.0.12, Release
build, workstation garbage collection with concurrent collection on, 16
processors; the test process, which also hosts the API and the writers, peaked
at 0.9 GB, and at least 3.2 GB of physical memory stayed free.

| | PostgreSQL 16 | SQL Server 2025 |
| --- | --- | --- |
| Complete read, no writes (median of 3) | 3.0 s | 4.4 s |
| Provider reads per complete read | 2.3 s | 3.8 s |
| Non-read time per page, median (95th percentile) | 16 ms (70 ms) | 10 ms (72 ms) |
| One change committed after page 5 | page 6 refused; restarted read completes; 3.8 s in all | page 6 refused; restarted read completes; 5.5 s in all |

**Reads during writes.** A logical read here follows the Configuration
Service's rule: on `projection-changed` it restarts without a cursor, at most 3
times (4 attempts). Logical reads run back-to-back while the writers are
active; a read in progress when the writers stop runs to its end and is counted
separately.

| Writes to projected content | PostgreSQL 16 | SQL Server 2025 |
| --- | --- | --- |
| Four concurrent writers, 2,000 `PUT`s (writers active 10 s / 28 s, no write failures) | 0 of 9 completed while writers were active, all 9 used up their restarts; every attempt refused on page 2; 1 completed after the writers stopped | 0 of 17 completed while writers were active, all 17 used up their restarts; every attempt refused on page 2; 1 completed after the writers stopped |
| One rename every 10 s, for 60 s (no write failures) | 25 of 25 completed while writing, 1-2 attempts each; 1 more after | 12 of 12 completed while writing, 1-2 attempts each; 1 more after |
| One rename every 2 s, for 60 s (no write failures) | 5 of 11 completed while writing; 6 used up their restarts; 1 more after | 2 of 7 completed while writing; 5 used up their restarts; 1 more after |

A read completes only when no change to projected content commits while it is
in progress, so whether reads complete depends on how often projected content
changes, not on provider reads succeeding. Every provider read in these runs
succeeded. Changes less frequent than one complete read (seconds at the cap)
cost at most a restart or two. A steady stream of changes prevents a complete
read at the cap, and the read reports `projection-changed` once its restarts
are used up.

These measurements do not show that a read can complete while projected
content keeps changing. A read that
runs out of restarts fails as a whole. The Configuration Service discards the
incomplete attempt and keeps its previous snapshot.
