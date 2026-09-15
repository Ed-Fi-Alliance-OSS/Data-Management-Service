---
jira: TBD
jira_url: TBD
epic: DMS-1504
source_spike: DMS-1503
---

# Story: Integrate Plugin Loading into Configuration Service Startup

## Description

The loader is host-agnostic and CMS does not call it.
The spine showed that CMS can have the whole mechanism for very little and deferred it on one ground only: no consuming epic drove it.
This spike is that epic, and this story is where CMS gains both composition phases, per:

- `reference/design/secrets-DMS-1503/design.md` ("### CMS Host Integration", "### Configuration Surface", "### Contract Cardinality")
- `reference/design/plugins-DMS-1462/design.md` ("### Applicability to the Configuration Service", "### The Two Composition Phases", "### Startup Failure Semantics", "### Observability")

The integration is the minimal one the spine described and no bootstrap scaffolding is ported: `Config.Frontend/Program.cs` is a plain minimal-API startup (`CreateBuilder` at `:14`, `AddServices()` at `:16`, `Build()` at `:53`, `RunAsync()` at `:113`), the loader reports its own outcomes to `Console.Error` and throws, and the plugin audit is a direct call rather than a startup task.

Both contracts the registry declares exist and neither is called yet; a resolver registered here resolves nothing until the read seam lands in the next story.
The image change is the load-bearing risk: the frontend's project reference into `src/plugins/` cannot land in a build stage that cannot restore it, and `src/dms/Dockerfile` already carries the worked solution.

## Acceptance Criteria

- `Config.Frontend` takes a `ProjectReference` on `src/plugins/EdFi.Api.Plugins.Hosting/EdFi.Api.Plugins.Hosting.csproj` with a regenerated `packages.lock.json`. Both plugin projects are already in `src/config/EdFi.DmsConfigurationService.sln` (`:42`, `:44`, `:46`).
- **`src/config/Dockerfile` gains the plugin tree**, mirroring `src/dms/Dockerfile`: the `pluginsource` filtering stage (`src/dms/Dockerfile:22-30`), the plugin `Directory.Build.props`, project files, and lock files copied ahead of the restore (`:53-57`), `WORKDIR` moved down one level so both trees sit under the common parent holding `.editorconfig`, `Directory.Packages.props`, and `nuget.config` (`:33-43`, `:59`), the restore extended to `EdFi.Api.Plugins.Hosting` (`:87`), and the plugin sources copied after the restore (`:109`). The `parentdir` named context already exists on every lane (`.github/workflows/on-config-pullrequest.yml:395`, `:489`, `:583`; `build-config.ps1:470`).
- The staging stage is not replaced by a `src/.dockerignore` or `COPY --exclude`, and the pull request records why: a named context is filtered only by its own `.dockerignore`, which `src/` does not have, and adding one would change both images' contexts at once.
- `.github/workflows/on-config-pullrequest.yml:119` gains `src/plugins/*` in the case that sets `fresh_build_required`; `:124` already names it for `config_relevant`.
- `CmsPluginContracts` is a frontend-owned static holding CMS's `PluginContractRegistry` with two `Replace` entries, `ISecretResolver` and `IClientSecretHasher`. `ContractAssemblyNames` yields `EdFi.Api.Plugins` and `EdFi.DmsConfigurationService.Secrets`; a test asserts the package id `EdFi.Api.Secrets` is not among them.
- `Config.Frontend/Program.cs` calls `PluginLoader.Load(builder.Configuration, CmsPluginContracts.Registry.ContractAssemblyNames)` between `:14` and `:16`, then `loadedPlugins.ContributeConfiguration(builder.Configuration)` immediately after, because `AddServices` reads configuration from its first statements.
- `AddServices` takes the `LoadedPlugins` aggregate, invokes `ContributeServices` with the registry after its own registrations and before `Build()`, and registers the returned `PluginAuditInput` as a singleton instance in the same call. A test asserts a plugin registering its own `PluginAuditInput` does not displace the host's.
- **The plugin audit is a direct call** between `Build()` and `RunAsync()`: CMS resolves the registered input, calls `PluginRegistrationAudit.AuditAsync(input, app.Services)` (`src/plugins/EdFi.Api.Plugins.Hosting/PluginRegistrationAudit.cs:47`), writes every finding, and throws if there is one.
- **The lifetime-and-keyedness check is a CMS-side check beside the audit, over the same input.** Both declared contracts must be registered as singletons and unkeyed; a plugin registering either as scoped or transient, or under a service key, is refused with the plugin and the lifetime or key named. The shared audit deliberately admits a declared contract under a concrete key as one ordinary claim (`PluginRegistrationAudit.cs:231`, `:88`), while every CMS consumer resolves unkeyed (`Backend.OpenIddict/Repositories/OpenIddictClientRepository.cs:21`), so an unchecked keyed registration would boot green and displace nothing. A test asserts each refusal, including a plugin registering `IClientSecretHasher` under a key failing startup rather than loading inert.
- **The inventory event is emitted before the audit runs**: one structured `Information` event per loaded plugin with `PluginName`, `AssemblyVersion`, `DeclaredFiles`, `RegisteredServiceTypes`, `RemovedDescriptors`, and `HostFirstSubstitutions`, matching DMS's shape. A test asserts the event precedes the audit's failure in a failing boot's captured log.
- No configuration key or value contributed by a Phase A source appears in the log, asserted with a fixture source carrying a secret-looking value: the captured output contains the source type and neither the key nor the value.
- `Config.Frontend/appsettings.json` gains `"Plugins": { "Directory": "/app/plugins", "Allowed": "" }`; the existing CMS suites pass unchanged with no plugin root present.
- A fixture plugin exercising both phases proves the integration: its Phase A source supplies a value CMS reads and its `ContributeServices` registers an `ISecretResolver`; an integration test boots CMS against it via `Plugins:Directory` and `Plugins:Allowed` and asserts both effects. No fixture in the spine exercises both phases, so this one is new.
- Fatal cases assert on the exception escaping host creation and on the `Console.Error` output: a misspelled allowlist entry, a plugin allowlisted on a missing root, a throwing `ContributeConfiguration`, and a throwing `ContributeServices` each fail the boot with no request served.
- Cardinality: two plugins each registering `ISecretResolver` is fatal naming both; one plugin registering it twice is fatal with the count; one plugin registering `IClientSecretHasher` once loads with the host's own registrations present, asserted on both reachable shapes - self-contained, where the host registers the default twice (`Config.Frontend/Infrastructure/WebApplicationBuilderExtensions.cs:206` and via `:90-91`/`:376`), and Keycloak, where it registers once. The fourth source site sits in an overload nothing calls.
- A plugin registering a CMS-owned type that is no declared contract is fatal at the audit's displacement check, naming plugin and type; one removing or overwriting a pre-existing CMS descriptor is fatal at the wrapper; one registering its own types, `Microsoft.Extensions.*` options, and an `IHostedService` beside a declared contract loads unaffected, with the hosted service in its inventory event.
- **`HostOwnedServiceTypes` needs no change for CMS, proven rather than assumed**: its prefix list already carries `EdFi.DmsConfigurationService.` (`src/plugins/EdFi.Api.Plugins.Hosting/HostOwnedServiceTypes.cs:38-42`), and the criterion is a test, because the way that stops holding is silently.
- `eng/docker-compose/published-config.yml` and `local-config.yml` are unchanged; a `plugins-config.yml` overlay carries the `:ro` mount at `/app/plugins`, added with its own `-f`, following the shape of DMS-1499's `plugins-dms.yml` - which does not exist on main yet, so the pull request states which overlay lands first and which mirrors. The mount variable gets the same documentation homes the DMS one has: `eng/docker-compose/README.md` and the tracked `.env.example`, commented out, with the `:?` note.
- `docs/CONFIGURATION.md` gains the CMS `Plugins` section: `Allowed` ships empty and is the only switch, its order is invocation order and contractual for Phase A, CMS has no `AppSettings:StartupStatusFilePath` equivalent, and the `Plugins` section itself is out of any plugin's reach.
- `dotnet build --no-restore src/config/EdFi.DmsConfigurationService.sln`, `dotnet test src/config/EdFi.DmsConfigurationService.sln`, and `build-config.ps1 E2ETest` pass.

## Tasks

1. Add the frontend's project reference, `CmsPluginContracts`, and the `Program.cs` loader call with the Phase A invocation.
2. Extend `AddServices` to take the aggregate, invoke `ContributeServices`, and register the audit input; add the direct audit call after `Build()`.
3. Add the CMS-side lifetime-and-keyedness check over the audit input.
4. Add the inventory event and the log-hygiene assertion.
5. Extend `src/config/Dockerfile` with the plugin tree and update the workflow's fresh-build filter.
6. Add the `Plugins` configuration section, the `plugins-config.yml` overlay, and the compose documentation entries.
7. Add the both-phases fixture plugin and the fatal, cardinality, displacement, and integration test suites.
8. Update `docs/CONFIGURATION.md` with the CMS `Plugins` chapter.
