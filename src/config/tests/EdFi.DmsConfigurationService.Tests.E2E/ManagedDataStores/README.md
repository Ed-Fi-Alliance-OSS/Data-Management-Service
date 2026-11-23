# Managed data store external HTTP qualification

`Given_external_managed_data_store_startup` is an explicit NUnit fixture. It launches the actual built CMS executable in Production, using its shipped `appsettings.json`, a generated database and an ephemeral encrypted signing certificate. It does not use `WebApplicationFactory`, replace authentication, or invoke the Reqnroll cleanup hooks.

Build the CMS frontend and this E2E project, then securely configure these environment variables in the same process that runs the tests:

| Variable | Purpose |
| --- | --- |
| `DMS_MANAGED_E2E_CMS_DLL` | Absolute path to the freshly built `EdFi.DmsConfigurationService.Frontend.AspNetCore.dll`, alongside its runtime files and shipped settings |
| `ConnectionStrings__PostgresqlDmsAdmin` | Admin connection to an isolated PostgreSQL test server |
| `ConnectionStrings__MssqlDmsAdmin` | Admin connection to an isolated SQL Server test server; TCP is used for qualification |

The accounts must be able to create/deploy/drop test databases. Do not echo their values or pass credentials as command arguments. No physical DMS target or template package is needed for this startup/disabled-gate slice.

From the repository root:

```powershell
dotnet test src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/EdFi.DmsConfigurationService.Tests.E2E.csproj --no-build --no-restore --filter 'FullyQualifiedName~Given_external_managed_data_store_startup'
```

Require two passing cases and zero skips. Missing lane settings skip the explicit cases; a skipped run is not qualification. The filter explicitly selects them; normal E2E runs do not launch this fixture automatically.

For each provider, the fixture proves that omitting the management setting refuses an unconfigured production startup for the expected reason. A fresh process with explicit `false` starts normally and issues real self-contained tokens. Anonymous and insufficient-scope requests fail before the disabled response; an administrator receives the stable disabled 400 before malformed body/query/ID parsing. Ordinary reads still work and no managed aggregate/job is written. Limited-client permissions are assigned directly as fixture data before requesting its real token. Captured console/stderr and HTTP error responses are checked for known credentials, tokens and a request canary; this is not an audit of every log sink.

The child receives secrets through its private environment, never command arguments. Its HTTP listener uses loopback and a generated port with `/managed-e2e` path base; the fixture waits for the child's structured listening-address confirmation before sending requests. The test owns its `Process` instance, generated database and temporary directory; cleanup never searches for processes to kill and never controls Rider or Docker. Avoid running the stock `setup-local-cms.ps1`/`teardown-local-cms.ps1` against an existing `cs-local` stack for this fixture.

This startup fixture is partial DMS-1439 step 6.1 evidence. It does not cover enabled create/delete or physical provisioning/readiness; the fixtures below add those boundaries. Keycloak, TLS and mixed-job behavior remain separate. See the [qualification matrix](../../../../../reference/design/managed-data-stores-DMS-1439/step-6.1-qualification.md) for remaining slices.

## Enabled lifecycle

`Given_external_managed_data_store_lifecycle` adds two explicit cases, one per same-provider CMS/target pair. It uses real startup readiness and the shipped worker settings, without replacing services, claiming jobs manually or editing retry/lease fields. It provisions a genuine Minimal target and deletes it through external HTTP. Both Minimal and Sample packages are signed with an ephemeral test key and allowlisted for the real startup contract; the source downloads are unchanged. Sample provisioning through this external fixture is not covered.

In addition to the settings above, configure:

| Variable | Purpose |
| --- | --- |
| `DMS_TEMPLATE_PACKAGE_DIRECTORY` | Existing genuine packages, exactly one Minimal and one populated/Sample per provider |
| `DMS_PROVISIONING_PSQL_PATH` | Absolute path to psql 18 or later for the PostgreSQL lane |
| `DMS_PROVISIONING_MSSQL_STAGING_PATH` | Existing shared staging directory as seen by CMS |
| `DMS_PROVISIONING_MSSQL_STAGING_SERVER_PATH` | The same staging directory as seen by SQL Server |

The isolated-server admin also needs permission to create/drop a test runtime principal and assign its read grants. PostgreSQL template prerequisites, including `edfi_dms_enqueue_owner`, must already be installed. The fixture creates a separate non-admin read principal before startup: PostgreSQL `pg_read_all_data`, or SQL Server `CONNECT ANY DATABASE` and `SELECT ALL USER SECURABLES`. These test-server grants qualify runtime reads, not resource CRUD/CDC permission deployment.

Use `FullyQualifiedName~Given_external_managed_data_store_lifecycle` for the two new cases, or `FullyQualifiedName~Given_external_managed_data_store` for those plus the two startup regressions. Require zero skips. Assertions cover the create `jobQueuedResult` (`jobId`/`message`), absolute Location/path base, Created publication, ordinary café/cafe coexistence and filtering, canonical duplicate rejection, ordinary managed-link guards, runtime identity/schema reads from the published encrypted connection, the delete 202 `jobQueuedResult`, retained tombstone history, physical absence and both returned job IDs completed through `/v3/jobs/{jobId}`. Known secrets are checked in managed/ordinary responses and captured output.

Cleanup stops only the owned CMS child, then drops its generated catalog and runtime principal. It does **not** drop an unexplained physical target by name after failure. Compare server inventories and inspect ownership before cleaning such residue. The private package/operation directory is test-owned; the existing SQL staging root and containers are never removed. Feature re-enable/mixed jobs, Keycloak, TLS, full runtime CRUD, and live endpoint rollover remain separate qualification work.

## Closed readiness and restart

`Given_external_managed_data_store_readiness` adds two explicit cases with the same configuration prerequisites. Each creates a genuine Minimal target, waits for its job to complete, and stops only the owned CMS process while keeping the catalog, certificate and physical target. It withholds its signed Minimal package copy before starting a second process; the real startup probe must report `ArtifactMissing` and NotReady.

Valid managed POST and eligible DELETE both return 503 without Location. Complete aggregate/job/catalog row hashes and public responses must remain unchanged; the original physical identity remains readable and the refused target is absent. Restoring the package file while that process lives must still yield the same refusals and unchanged state. A third process must report Ready, delete the original target, then create and delete the exact name previously refused. All four jobs must complete on their first attempt, with no manual claim or state updates.

Run with `FullyQualifiedName~Given_external_managed_data_store_readiness`. For shared deployment-helper regression coverage, append `|FullyQualifiedName~Given_external_managed_data_store_startup`; require four passes and zero skips. The process stop occurs only after job settlement, so this fixture establishes readiness re-evaluation at restart, not interruption recovery. It does not exercise previously accepted unfinished jobs executing under a later closed gate, transient outages after acceptance, mixed jobs or re-enablement; those need their own evidence. It never modifies the source package downloads, existing containers or Rider.

## Accepted work paused by closed readiness

`Given_external_managed_data_store_readiness_pause` runs one explicit case per provider, covering both unfinished create and unfinished delete. It first creates a genuine Minimal target, then starts a Ready host with the existing `JobSettings:WorkerEnabled=false` option. HTTP accepts a new create and a delete of the existing target; each returned `jobId` must be the aggregate's job, and both jobs must be Pending, unclaimed and at attempt zero. No database update manufactures those states. While the process is stopped, the fixture seeds one unknown-type job due behind both managed jobs; there is no unrelated production handler or public enqueue API.

A new host with its worker enabled and the fixture package withheld reports `ArtifactMissing`/NotReady. Its worker settles the unknown job to Error, which is the positive progress observation. Complete aggregate/job/catalog snapshots of the managed work must match the paused state: no claim, attempt, exhaustion or physical effect. The uncreated target remains absent and the delete target and its ordinary row remain readable.

After restoring the package and restarting Ready, the same job IDs complete on attempt one with unchanged identity, endpoint, target and delete pin. The create publishes exactly one ordinary row, the deleted store's ordinary row returns 404, and its target is absent. The remaining target is deleted through HTTP before fixture disposal; no target is removed outside the API.

Run only `FullyQualifiedName~Given_external_managed_data_store_readiness_pause`; require two passes and zero skips. This is an acceptance-to-first-execution boundary. Interruption after physical create/drop is covered by the provider process-recovery tests, which add a closed-readiness restart before healthy recovery. All worker timing settings remain at their shipped values; only worker enablement changes to establish the initial queue. Records that already ended in CreateError/DeleteError are not revived by this behavior.

## Mixed jobs during management pause and re-enablement

`Given_external_managed_data_store_suppression` adds one explicit case per provider with the same provisioning prerequisites. A Ready production host creates a genuine Minimal target. A second Ready host with only its worker disabled accepts another create and a delete through HTTP. Both jobs are Pending, unclaimed and at attempt zero before shutdown. The fixture then seeds unrelated durable jobs and one due daily schedule while the process is stopped; no public enqueue/schedule API exists. The managed jobs lead the due queue.

A new process with management false and the worker enabled must refuse new managed POST/DELETE, while the production job services still dispatch an unknown type and a case-only near-match, exhaust an unrelated five-attempt job, materialize one scheduled occurrence and prune an old completed job. The production host has no unrelated successful handler: dispatched unknown jobs must settle with the exact unsupported-type error. This proves dispatcher/executor progress, not successful unrelated business work or HTTP enqueue validation. Schedule advancement, occurrence linkage, job types, fences, attempts and cleared leases are asserted. The default retention, scheduler, polling and lease settings are unchanged.

The scheduler therefore also runs the education organization refresh schedule, which the fixture asserts is enabled. Its refresh jobs read a DMS that does not exist and can end in Error. The error assertions count exactly the seeded jobs and the unrelated schedule's occurrence; any other job in Error must be a refresh job.

After those positive progress observations, complete managed aggregate/job/catalog snapshots must still match the paused state. The uncreated target remains absent and the pending-delete target remains present. A fresh management-enabled process must complete the original create/delete job IDs on attempt one with unchanged identity, endpoint and delete pin, exactly one published row and the expected physical states. The remaining target is deleted through HTTP before fixture disposal. No managed job or lifecycle row is seeded, reset, claimed or rescheduled manually.

Run only `FullyQualifiedName~Given_external_managed_data_store_suppression`; require two passes and zero skips. This slice tests Pending/attempt-zero suppression and restart re-enablement. Expired leases, already-exhausted managed records and successful unrelated handlers remain separate provider/process evidence; the seeded ordinary exhaustion row does not establish five actual handler attempts. External Sample creation, runtime CRUD/CDC and the other qualification-matrix limits remain open. Existing unexplained residue is never removed.
