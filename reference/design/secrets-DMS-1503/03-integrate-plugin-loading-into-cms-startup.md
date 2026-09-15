---
jira: TBD
jira_url: TBD
epic: DMS-1504
source_spike: DMS-1503
---

# Story: Integrate Plugin Loading into Configuration Service Startup

## Description

The plugin loader is host-agnostic and the Configuration Service does not call it.
The spine showed that CMS can have the whole mechanism for very little and deferred it on one ground only: no consuming epic drove it.

This spike is that epic, and this story is where CMS gains both composition phases, per:

- `reference/design/secrets-DMS-1503/design.md` ("### CMS Host Integration", "### Configuration Surface", "### Contract Cardinality")
- `reference/design/plugins-DMS-1462/design.md` ("### Applicability to the Configuration Service", "### The Two Composition Phases", "### Startup Failure Semantics", "### Observability")

Both contracts the registry declares exist after story 02, and neither is called yet.
A resolver registered here resolves nothing until the read seam lands in story 04.

## Technical Implementation

**Citation convention.**
Unprefixed paths are relative to the repository root.
`Config.Frontend/` names `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/`.
`Backend.OpenIddict/` names `src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/`.

**No bootstrap scaffolding is ported, and that is the spine's instruction rather than a shortcut.**
`Config.Frontend/Program.cs` is a plain minimal-API startup: `CreateBuilder` at `:14`, `AddServices()` at `:16`, `Build()` at `:53`, `RunAsync()` at `:113`.
It has no `FileStartupStatusSignal`, no `RunBootstrapPhase`, and no `StartupPhaseExecutor`, and none is added.
The loader reports its own outcomes to `Console.Error` and throws, which is fail-loud behavior with nothing to build first.

**The plugin audit is a direct call rather than a startup task.**
CMS has no startup-task machinery, and inventing an interface, an executor, and an ordering convention to serve one call site is exactly the scaffolding the spine said not to port.
`PluginRegistrationAudit.AuditAsync` returns findings rather than throwing precisely so a caller can report all of them, and the CMS call site does.

**Phase A has to finish before `AddServices` is entered.**
`AddServices` reads configuration from its first statements, so the loader call and the Phase A invocation both sit between `CreateBuilder(args)` at `:14` and `builder.AddServices()` at `:16`.

**The audit input is registered as an instance, after every hook has run.**
Registering an instance activates nothing, and registering it last is what stops a plugin that registered its own from displacing it, because a single-service resolve takes the last registration.

**The lifetime-and-keyedness check is CMS-side because the registry does not carry that metadata.**
A lifetime rule is per-contract metadata the host holds, which is the same reason DMS-1434's transient-only rule for `ICustomResourceValidator` lives on the DMS side.
The keyedness half closes a hole the shared audit deliberately leaves open: it admits a declared contract under a concrete key and counts it as one ordinary claim (`src/plugins/EdFi.Api.Plugins.Hosting/PluginRegistrationAudit.cs:231`, `:88`), while every CMS consumer resolves unkeyed (`Backend.OpenIddict/Repositories/OpenIddictClientRepository.cs:21`).
An unchecked keyed registration would therefore boot green, emit an inventory event saying it registered the contract, and displace nothing.

**The inventory event runs before the audit for an attribution reason.**
An audit failure names a service type, and only the inventory maps that type back to the plugin that registered it.

**The hasher's host-default count is a runtime question, not a source-site count.**
There are four registration sites in source and never four descriptors in one process.
A self-contained PostgreSQL deployment registers the host default twice, once in `ConfigureDatastore` (`Config.Frontend/Infrastructure/WebApplicationBuilderExtensions.cs:206`) and once inside the OpenIddict extension `ConfigureIdentityProvider` calls immediately after it (`:90-91`, `:376`); a Keycloak deployment registers it once.
The engine-specific sites are mutually exclusive and the fourth sits in an overload nothing calls.
The replace-conflict rule is indifferent to the number because it counts only descriptors the per-hook diffs attribute to plugins, and a host default is in nobody's record.

**The image change is the load-bearing risk in this story.**
`.github/workflows/on-config-pullrequest.yml` builds `src/config/Dockerfile` in three places, and the frontend's project reference into `src/plugins/` cannot land in a build stage that cannot restore it.
`src/dms/Dockerfile` already solved this and its solution transfers line for line, with the measurements that produced each line recorded in that file's own comments.
The `parentdir` named context the staging stage reads already exists on every lane that builds this file: `.github/workflows/on-config-pullrequest.yml:395`, `:489`, and `:583`, and `--build-context parentdir=../` in `build-config.ps1`'s `DockerBuild` (`:470`).

**The staging stage cannot be replaced by a `.dockerignore`.**
`src/dms/.dockerignore` filters the DMS build context, and a named context is filtered only by a `.dockerignore` of its own, which `src/` does not have.
A direct copy of the tree would therefore carry a local build's `bin/`, `obj/`, and `results.sarif` into the image, and an `obj/` arriving after the restore lands on top of the Linux-generated `project.assets.json`.
Adding `src/.dockerignore` would change both images' contexts at once, which is why it was rejected for the DMS image and is rejected again here.

**The compose overlay's precedent may not exist yet.**
`eng/docker-compose/plugins-dms.yml` is DMS-1499's output and is not on main, as are the mount variable's entries in `eng/docker-compose/README.md` and `.env.example`.
So this story either lands after DMS-1499 and mirrors a file it can read, or it writes the CMS overlay first and DMS-1499 mirrors this one.
An unconditional mount in the base compose files is rejected because Compose cannot make a bind mount conditional inside one file, so Docker would materialize an empty root-owned directory beside every deployment that never asked for a plugin.

**`HostOwnedServiceTypes` already covers CMS, and the criterion is a test rather than a code change.**
Its prefix list carries `EdFi.DmsConfigurationService.` beside `EdFi.DataManagementService.` (`src/plugins/EdFi.Api.Plugins.Hosting/HostOwnedServiceTypes.cs:38-42`).
The assertion is that a rule written for one host already holds for the other, and the way that stops holding is silently.

## Acceptance Criteria

**Project and image**

- `Config.Frontend` takes a `ProjectReference` on `src/plugins/EdFi.Api.Plugins.Hosting/EdFi.Api.Plugins.Hosting.csproj` with a regenerated `packages.lock.json`.
- No solution change is needed: both plugin projects are already in `src/config/EdFi.DmsConfigurationService.sln` (`:42`, `:44`, `:46`), which DMS-1496 did so this story would not have to.
- `src/config/Dockerfile` has a `pluginsource` stage that copies `plugins/` from the `parentdir` named context and removes `bin/`, `obj/`, and `results.sarif`, mirroring `src/dms/Dockerfile:22-30`.
- `src/config/Dockerfile` copies the plugin `Directory.Build.props`, project files, and lock files ahead of the restore, mirroring `src/dms/Dockerfile:53-57`.
- `src/config/Dockerfile`'s `WORKDIR` moves down one level so the CMS and plugin trees sit beside each other under the common parent holding `.editorconfig`, `Directory.Packages.props`, and `nuget.config`, mirroring `src/dms/Dockerfile:33-43` and `:59`.
- The Dockerfile restore is extended to `EdFi.Api.Plugins.Hosting`, mirroring `src/dms/Dockerfile:87`.
- The plugin sources are copied from the staged tree after the restore, mirroring `src/dms/Dockerfile:109`.
- No `src/.dockerignore` is added and no `COPY --exclude` replaces the staging stage.
- The pull request records why the staging stage is used instead of either alternative.
- `.github/workflows/on-config-pullrequest.yml:119` names `src/plugins/*` in the case that sets `fresh_build_required`.
- `:124` is unchanged, already naming `src/plugins/*` for `config_relevant`.

**Contract registry**

- `CmsPluginContracts` is a frontend-owned static holding CMS's one `PluginContractRegistry`.
- The registry declares `ISecretResolver` as `Replace` and `IClientSecretHasher` as `Replace`.
- `ContractAssemblyNames` is the registry's derived property, not a second list, and yields `EdFi.Api.Plugins` and `EdFi.DmsConfigurationService.Secrets`.
- A test asserts the package id `EdFi.Api.Secrets` is not among `ContractAssemblyNames`.

**Startup wiring**

- `Config.Frontend/Program.cs` calls `PluginLoader.Load(builder.Configuration, CmsPluginContracts.Registry.ContractAssemblyNames)` between `:14` and `:16`.
- `Program.cs` calls `loadedPlugins.ContributeConfiguration(builder.Configuration)` immediately after the loader returns and before `builder.AddServices()`.
- `AddServices` takes the `LoadedPlugins` aggregate and invokes `ContributeServices` with `CmsPluginContracts.Registry` after its own registrations and before `Build()`.
- `AddServices` registers the returned `PluginAuditInput` as a singleton instance in the same call.
- Between `Build()` and `RunAsync()`, CMS resolves the registered `PluginAuditInput`, calls `PluginRegistrationAudit.AuditAsync(input, app.Services)` (`src/plugins/EdFi.Api.Plugins.Hosting/PluginRegistrationAudit.cs:47`), writes every finding, and throws if there is one.
- A test asserts a plugin registering its own `PluginAuditInput` does not displace the host's.

**Lifetime and keyedness check**

- A CMS-side check runs beside the audit over the same input.
- A plugin registering either declared contract in any of these ways fails the boot:
  - as scoped, naming the plugin and the lifetime
  - as transient, naming the plugin and the lifetime
  - under a service key, naming the plugin and the key

**Observability**

- One structured `Information` event is emitted per loaded plugin, carrying `PluginName`, `AssemblyVersion`, `DeclaredFiles`, `RegisteredServiceTypes`, `RemovedDescriptors`, and `HostFirstSubstitutions`.
- A test asserts the inventory event for a fixture plugin appears in the captured log before the audit's failure for the same boot.
- A test with a fixture Phase A source carrying a secret-looking value asserts the captured output contains the source type and neither the configuration key nor its value.

**Configuration**

- `Config.Frontend/appsettings.json` gains `"Plugins": { "Directory": "/app/plugins", "Allowed": "" }`.
- The existing CMS unit and end-to-end suites pass unchanged with no plugin root present.

**Fatal cases**

- Each of these fails host creation with no request served, and writes the failure to `Console.Error`:
  - a misspelled allowlist entry
  - a plugin allowlisted on a missing root
  - a plugin whose `ContributeConfiguration` throws
  - a plugin whose `ContributeServices` throws

**Cardinality**

- Two plugins each registering `ISecretResolver` fails the boot, naming both.
- One plugin registering `ISecretResolver` twice fails the boot, naming the plugin and its registration count.
- One plugin registering `IClientSecretHasher` once loads successfully on both reachable shapes:
  - a self-contained PostgreSQL deployment, where the host registers its own default twice
  - a Keycloak deployment, where the host registers its own default once

**Guard behavior**

- A plugin registering a CMS-owned service type that is no declared contract fails at the audit's displacement check, naming the plugin and the type.
- A plugin removing or overwriting a pre-existing CMS-owned descriptor fails at the wrapper, before the call reaches the real collection.
- A plugin registering its own types, `Microsoft.Extensions.*` options, and an `IHostedService` alongside a declared contract loads unaffected, and its inventory event lists the hosted service.
- A test asserts `HostOwnedServiceTypes.IsHostOwned` returns true for a CMS service type, with no change to `HostOwnedServiceTypes.cs`.

**Integration proof**

- A fixture plugin exercises both phases: its `ContributeConfiguration` adds a source supplying a value CMS reads, and its `ContributeServices` registers an `ISecretResolver`.
- An integration test boots CMS with `Plugins:Directory` pointed at that fixture and `Plugins:Allowed` naming it, and asserts the configuration value resolved to the plugin's.
- The same test asserts the `ISecretResolver` is resolvable from the container.

**Deployment**

- `eng/docker-compose/published-config.yml` and `local-config.yml` are unchanged.
- `eng/docker-compose/plugins-config.yml` carries the `:ro` mount at `/app/plugins` for the CMS service and is added with its own `-f`.
- The mount variable is documented in `eng/docker-compose/README.md` and in the tracked `.env.example`, commented out, carrying the same `:?` note the DMS one has.
- The pull request states whether this overlay lands before or after DMS-1499's `plugins-dms.yml`, and which mirrors which.

**Documentation**

- `docs/CONFIGURATION.md` gains the CMS `Plugins` section, stating:
  - that `Allowed` ships empty and is the only switch
  - that its order is invocation order and contractual for Phase A
  - which keys Phase A cannot supply in CMS, and that CMS has no `AppSettings:StartupStatusFilePath` equivalent
  - that `PluginLoader.Load` binds `Plugins:Directory` and `Plugins:Allowed` before any Phase A hook runs, so no plugin source can supply either

**Build**

- These pass:
  - `dotnet build --no-restore src/config/EdFi.DmsConfigurationService.sln`
  - `dotnet test src/config/EdFi.DmsConfigurationService.sln`
  - `build-config.ps1 E2ETest`

## Tasks

1. Add the frontend's project reference and `CmsPluginContracts`.
2. Add the `PluginLoader.Load` call and the Phase A invocation to `Config.Frontend/Program.cs`.
3. Extend `AddServices` to take the aggregate, invoke `ContributeServices`, and register the audit input as an instance.
4. Add the direct audit call between `Build()` and `RunAsync()`.
5. Add the CMS-side lifetime-and-keyedness check over the audit input.
6. Add the inventory event and the log-hygiene assertion.
7. Extend `src/config/Dockerfile` with the plugin tree and update the workflow's fresh-build filter.
8. Add the `Plugins` configuration section and the `plugins-config.yml` overlay with its documentation entries.
9. Add the both-phases fixture plugin.
10. Add the fatal, cardinality, guard-behavior, and integration test suites.
11. Update `docs/CONFIGURATION.md` with the CMS `Plugins` chapter.
