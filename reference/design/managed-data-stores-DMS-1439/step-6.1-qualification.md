# DMS-1439 step 6.1 qualification matrix

Status: **incomplete**. This is a coverage and remaining-work index, not acceptance of the ticket or permission to push. It follows the revision-9 contract recorded in [contract-provenance.md](contract-provenance.md). It does not change that contract, the user-authorized AC9 clarification, or the phase boundaries. Snapshot persistence and cleanup remain DMS-1441 work.

Source baseline: `bd9d27ab6`. The reviewer approved `b251d90b8` and `bd9d27ab6` as partial qualification slices. Earlier implementation reviews and focused test reports remain evidence for their specific boundaries; they are not a fresh all-provider or external E2E run.

## Live qualification recorded so far

| Slice | PostgreSQL | SQL Server | Limits |
| --- | --- | --- | --- |
| Guarded physical interleavings, step 4.0b (`3334481da`) | Seven physical cases executed and passed | Seven TCP physical cases executed and passed | Real fences and physical engines; this alone did not qualify runtime handler retry scheduling |
| Genuine Minimal and Sample recovery (`b251d90b8`) | Both cases passed | Both cases passed on SQL Server 2025 TCP | Actual handlers, physical provisioning, runtime reads and DMS cache; controlled authentication/readiness, manually claimed jobs, in-process host recreation |
| Process death/restart and exhaustion (`bd9d27ab6`) | Final eight-case selection passed | **Complete eight-case selection passed on the final commit**, 0 failed and 0 skipped | Actual child-process death and production worker loop; controlled authentication/readiness and accelerated expiry after the child exits |

The final SQL process selection ran on 2026-11-15 against **17.0.5065.9 over TCP**. Filter: `TestCategory=DataStoreManageProcessRecovery`; 8 passed, 0 failed, 0 skipped; elapsed 236.08 seconds. It supersedes the earlier split evidence for SQL retry and exhaustion. The private SQL-2025 configuration was loaded in the same PowerShell process as the tests. No test source, timeout, or production code changed for the rerun. Database inventory was unchanged; cleanup checks found no test runtime principals or Crash-named targets on either provider. No container was changed or unverified database residue removed.

The dated local review reports retain the sanitized TRX, console summaries, server facts and cleanup comparisons. Those artifacts are local validation evidence, not checked-in credentials or a claim that CI ran these selections.

## Reading the matrix

The numbers below follow all 36 rows of the approved spec's section 7. Fixture names refer to `.cs` files in the mirrored [PostgreSQL](../../../src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration/) and [SQL Server](../../../src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration/) integration projects unless stated otherwise.

- **P**: real provider repository/transaction tests. Handler fixtures can simulate the physical provisioner.
- **H**: HTTP pipeline hosted in-process, with controlled dependencies/authentication. This is not external HTTP E2E.
- **L**: real physical provisioner/target tests, including the live slices above.
- **W**: separate worker processes with real physical targets and the production worker loop.
- **U**: unit/contract tests. Frozen conformance-fixture assertions alone do not establish runtime behavior.

The table maps existing evidence; a row with a named fixture is not automatically closed. Explicit gaps below identify compositions or deployment boundaries not established by those fixtures. No unrelated suites were rerun to create this index.

| # | Required boundary | Existing coverage and material limit |
| --- | --- | --- |
| 1 | Create acceptance rollback after row/enqueue/before commit | **P** `DataStoreManageAcceptanceTests`: exception/cancellation at row and enqueue, invalid envelopes, failed final linkage, competing name/target winners |
| 2 | Durable pin before physical work; retry pin reuse | **P/U** `DataStoreManageCreateHandlerTests`, backend unit `ManagedDataStores/CreateManagedDataStoreHandlerTests`; **L/W** persisted pin and identity survive recovery |
| 3 | Physical create before CMS publication | **L/W** `DataStoreManageLiveRecoveryTests`, `DataStoreManageProcessRecoveryTests`: reconcile same physical state and publish one ordinary row |
| 4 | Publication rollback between ordinary insert/link/Created | **P** `DataStoreManagePublicationTests`: trigger/write-boundary and precommit failure, encryption/audit rollback |
| 5 | Publication committed but job completion lost | **W** process tests: reclaim and exhaustion preserve Created, link and timestamps; completed retry has no duplicate row |
| 6 | Last lease expires at MaxAttempts | **P/W** `DataStoreManageTerminalOutcomeTests` and process tests: atomic Error projection without another handler claim; process tests set only the dead job's expiry deadline |
| 7 | Delete acceptance rollback | **P** `DataStoreManageDeleteAcceptanceTests`: invalid job linkage/envelope, write failures/cancellation and competing acceptance |
| 8 | Physical drop then CMS failure | **L/W** live/process tests: physical absence with retained ordinary row before successful retry; already-absent reconciliation |
| 9 | Unlink/catalog/dependents/tombstone rollback | **P** `DataStoreManageDeletionTests`: each write boundary rolls back; history retained only on commit; **W** before/after finalization commit |
| 10 | Lease loss, fence wait and stale worker | **P/L** retry/publication/deletion and `DataStoreManageAdmissionTests`; guarded holder/marker-session interleavings. Controlled crash tests do not prove arbitrary network partitions |
| 11 | Old create resumes after successor delete | **L** admission fixture `It_refuses_delayed_create_after_a_successor_creates_and_deletes_the_target` |
| 12 | Old delete resumes after replacement | **L** admission fixture `It_refuses_delayed_delete_after_a_new_identity_reuses_the_name` and holder-loss interleavings |
| 13 | Retry/backoff and lifecycle failure roll back together | **P** `DataStoreManageRetryOutcomeTests`: failures between writes, unknown commit acknowledgement, stale ownership and lock-wait expiry |
| 14 | Populated catalog dependents; unrelated resources survive | **P** deletion fixture: assignments, contexts and derivatives, preserved clients/applications and rollback; **L** live recovery adds physical deletion. These are complementary fixtures |
| 15 | Dependent insertion races with ordinary deletion | **P** deletion fixture `It_serializes_each_dependent_insert_with_deletion_in_both_orders` |
| 16 | Changed pin, identity mismatch, unknown target | **P/U/L** handler/publication/admission fixtures plus existing `DataStoreProvisioning` engine suites. Simulated handler failure classification is distinguished from physical ownership checks |
| 17 | Ordinary PUT/DELETE vs linking; insert/rename vs reservation | **P/H** `DataStoreManagedLinkTests`, `DataStoreNamespaceTests`, acceptance/publication fixtures and frontend `Infrastructure/OrdinaryDataStoreManagedLinkTests` |
| 18 | Existing ordinary café and managed cafe on migrated providers | **P** publication fixture accepts both distinct keys and queries each separately; SQL namespace fixture uses an AI display collation different from the database default. HTTP acceptance through final publication in one scenario remains **G1/G2** |
| 19 | Insert/rename vs managed reservation, both orders | **P** acceptance fixture tests insert/rename, both orders, café/cafe and Store/store, null/selected tenants. It stops at acceptance counts; publication uses a separate seeded fixture. Continue the actual winning operation through publication: **G2** |
| 20 | Legacy uniqueness replacement, preflight and rollback | **P** `DataStoreNameComparisonKeyUpgradeTests`, `DataStoreCanonicalUniquenessUpgradeTests`, `DataStoreManageUpgradeTests`: direct writes, journaled-definition drift, collisions, rollback/replay and null tenant; custom-catalog release inventory still required |
| 21 | Canonical name/type filtering after coexistence/rename | **P/H** `DataStoreQueryTests`, canonical-uniqueness fixture and publication café/cafe lookups; shared expected query vectors |
| 22 | Provider-equivalent text ordering and paging | **P/H** query fixtures: filters before paging, Unicode ordering, ties/directions and migrated keys |
| 23 | Unfiltered DMS/operational catalog consumers | **P/U/L** query fixtures, DMS `ConfigurationServiceDataStoreProviderTests`, bootstrap name-selection Pester cases and live DMS cache fixture. Previously skipped real-SqlClient bootstrap comparisons are not promoted to passing |
| 24 | Closed readiness before POST/eligible DELETE | **P/H** acceptance, delete-acceptance, activation and frontend HTTP fixtures: 503/no mutation and precedence. Readiness is controlled; deployed probe coverage remains **G1** |
| 25 | Previously accepted jobs execute under closed readiness | **P/H** `DataStoreManageActivationTests`: real executor and atomic Error, controlled closed probe; no silent revival on fresh host. External startup/worker composition remains **G1** |
| 26 | Readiness stays latched; remediation requires restart | **U/H/P** frontend `Configuration/ManagedDataStoreStartupTests` and provider activation fixture; external readiness observation remains **G1** |
| 27 | Transient provisioning outage after acceptance | **P/H** activation fixture injects `LockTimeout`; **L** admission fixture establishes the real lock timeout; live recovery injects finalization rollback. A real physical transient feeding the production worker's scheduled retry is still **G3** |
| 28 | Retention cannot erase unprojected terminal evidence | **P** terminal-outcome fixture `It_prevents_retention_from_erasing_an_uncommitted_terminal_projection`: prune during held transaction returns zero, then prunes after commit while lifecycle evidence survives |
| 29 | Disabled management with mixed jobs/schedules | **P/U** `DataStoreManagementSuppressionTests` and job policy/startup tests: exact managed types suppressed; other jobs and recurring work continue. External disabled deployment belongs to **G1** |
| 30 | Tenant A/B/null names and global physical target reservation | **P/H** namespace, acceptance, endpoint-acceptance, managed-read and HTTP fixtures; tenant selection is partitioning, not authorization |
| 31 | Endpoint/provider changes across an operation | **U/P** backend `ManagedDataStores/ManagedDataStoreEndpointTests`, handler identity checks and `DataStoreProvisioning/DmsDataStoreProvisioningEndpointTests`; no live mixed-provider conclusion from parser/DI tests: **G4** |
| 32 | Handler endpoint A, provisioner snapshot B, absent target on B | **U/P** guarded provisioning endpoint tests and handler admission checks: refusal before target work/finalization. A two-server live rollover is not established by these unit cases |
| 33 | Resolver changes after captured admin snapshot | **U/P** provisioning endpoint tests plus acceptance `It_persists_the_single_resolved_snapshot_even_after_the_source_rotates`; holder/work/cleanup binding contract |
| 34 | Runtime changes before/after snapshot capture | **U/P** backend runtime-connection unit fixture and `DataStoreManageRuntimeConnectionTests`: captured endpoint validation and single encryption, both provider adapters; malformed literal fragments and resolver-returned references |
| 35 | SQL target publishes ordinary provider sqlserver | **L** SQL live recovery verifies routing token and uses the actual DMS catalog consumer; aggregate/artifact token stays mssql. External CMS HTTP deployment remains **G1**, mixed catalogs **G4** |
| 36 | Eventual DMS cache visibility | **L/U** live fixture and DMS cache tests: expiry-triggered refresh/decryption, no callback. This does not establish a deployed DMS resource CRUD workload |

The API matrix outside the numbered rows has **H/P/U** coverage in frontend `Modules/DataStoreManageHttpTests`, frontend startup tests, provider `DataStoreManageHttpTests`, `DataStoreManageReadTests`, delete-acceptance tests, and managed model/request tests. This includes all delete states, explicit nulls/history, request/query validation, selected-tenant behavior, authorization policies, absolute locations, disabled/default-enabled settings and redacted responses. Both identity-provider configurations are exercised in-process; this does not substitute for real token issuance/validation over external HTTP.

## Focused work still required

**G1 — External CMS E2E.** Add and execute a narrowly filtered CMS E2E fixture against separately running production CMS processes, with real token issuance/authentication, startup validation/readiness, worker scheduling and actual target engines. Start with PostgreSQL→PostgreSQL and SQL Server→SQL Server: bodyless 202/absolute Location, polling to Created, ordinary routing publication, managed-link 409, duplicate 400, delete 204 and retained Deleted history. Include real unauthorized/insufficient-scope requests and verify secret-free HTTP/log output. Exercise the specified disabled/closed-readiness and restart boundaries separately rather than pretending a successful create covers them. Keep each test-owned deployment/database and cleanup isolated.

The stock `setup-local-cms.ps1`/`teardown-local-cms.ps1` were inspected: they take no parameters and target `cs-local`; that stack is already running on the test machine. Teardown also removes volumes and matches existing CMS container names. Do not run them against that unrelated stack. The repository's `eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.ps1` supplies an isolated project/network/ports and a test filter, but its launcher still provisions a local database container. That does not supply the remote SQL Server 2025/shared staging used here. Adapt the isolated test setup narrowly for the remote provider, or run an owned native CMS process with the same production configuration contract. Record the chosen setup and actual E2E evidence; merely locating a harness is not qualification. Rebuild any newly tested containerized application code. No existing container or Rider process may be stopped.

**G2 — Join reservation competition to publication.** Extend the existing real-provider race cases to continue each successful managed acceptance through the persisted job/aggregate into fenced publication, rather than constructing a fresh publication seed. Retain insert/rename, both commit orders, Store/store versus café/cafe, selected/null tenants and SQL AI display collation. Assert one accepted managed operation, the expected ordinary rows, exact queries, and unchanged losing operations. Reuse the existing real-gate synchronization; no arbitrary sleep. Add external café/cafe acceptance/publication to G1 rather than rerunning all migration tests.

**G3 — Real transient to scheduled worker recovery.** Use a real competing physical target lock to produce the specific transient LockTimeout after acceptance, require atomic Pending/backoff plus CreateFailed/DeleteFailed, then release the lock and let the production worker complete a later attempt. Assert persisted identity/pin, physical/catalog state and no duplicates. Existing process-death cases cover reclaim/exhaustion; they do not drive this transient path. Keep production timeouts unchanged and use explicit synchronization. If the test advances a due time, disclose that control rather than claiming elapsed-time scheduling coverage.

**G4 — Mixed CMS/target qualification.** Both target engines are now available. Existing DI and builder tests select providers independently; the live tests use same-provider CMS/target pairs. Add narrowly scoped PostgreSQL-CMS→SQL-target and SQL-CMS→PostgreSQL-target live cases, preserving token distinctions and runtime endpoint binding. Record separately any two-server endpoint-rotation checks still lacking live evidence; unit endpoint-refusal tests are not a live rollover test.

After those slices, update this matrix against their actual results and review any remaining section-7 gaps. Overlapping exhaustion sweeps, actual abstract-socket connectivity, custom-catalog residual unique-index qualification, earlier bootstrap skips, the fake-psql failure and SQL error 3980 retain their prior unresolved/unverified status. This audit does not establish that those concerns were caused by DMS-1439 or silently waive them. Full runtime resource CRUD/CDC permissions are not established by the successful runtime reads.

Phase 6.2 operations/acceptance closure has not started. A complete matrix and external E2E evidence must be reviewed before step 6.1 is called complete; a push requires separate explicit authorization.
