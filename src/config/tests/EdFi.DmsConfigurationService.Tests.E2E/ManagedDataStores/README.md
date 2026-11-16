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

This startup fixture is partial DMS-1439 step 6.1 evidence. It does not cover enabled create/delete or physical provisioning/readiness; the lifecycle fixture below adds that boundary. Closed-readiness restart behavior, Keycloak, TLS and mixed-job behavior remain separate. See the [qualification matrix](../../../../../reference/design/managed-data-stores-DMS-1439/step-6.1-qualification.md) for remaining slices.

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

Use `FullyQualifiedName~Given_external_managed_data_store_lifecycle` for the two new cases, or `FullyQualifiedName~Given_external_managed_data_store` for those plus the two startup regressions. Require zero skips. Assertions cover bodyless acceptance, absolute Location/path base, Created publication, ordinary café/cafe coexistence and filtering, canonical duplicate rejection, ordinary managed-link guards, runtime identity/schema reads from the published encrypted connection, bodyless delete, retained tombstone history, physical absence and completed worker jobs. Known secrets are checked in managed/ordinary responses and captured output.

Cleanup stops only the owned CMS child, then drops its generated catalog and runtime principal. It does **not** drop an unexplained physical target by name after failure. Compare server inventories and inspect ownership before cleaning such residue. The private package/operation directory is test-owned; the existing SQL staging root and containers are never removed. Closed-readiness/restart, feature re-enable/mixed jobs, Keycloak, TLS, full runtime CRUD, and live endpoint rollover remain separate qualification work.
