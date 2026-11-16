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

This is partial DMS-1439 step 6.1 evidence. Enabled create/delete, physical provisioning/readiness, closed-readiness restart behavior, Keycloak, TLS and mixed-job behavior are not covered here. See the [qualification matrix](../../../../../reference/design/managed-data-stores-DMS-1439/step-6.1-qualification.md) for remaining slices.
