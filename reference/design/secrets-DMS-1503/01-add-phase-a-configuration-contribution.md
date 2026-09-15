---
jira: TBD
jira_url: TBD
epic: DMS-1504
source_spike: DMS-1503
---

# Story: Add Phase A Configuration Contribution to the Plugin Contract

## Description

The plugin spine designed two composition phases and built one.
`EdFiApiPlugin` ships with `Name` and `ContributeServices` and no Phase A member (`src/plugins/EdFi.Api.Plugins/EdFiApiPlugin.cs`), because the spine withheld a hook the host would not call: a virtual the host never invokes is a hook a plugin can override to no effect.

This story is the first consumer arriving.
It adds `ContributeConfiguration` to the contract, invokes it from the loader, places what a plugin contributed where the design says it must sit, guards the contribution as additive, and carries the Phase A rows of the spine's test plan, per:

- `reference/design/plugins-DMS-1462/design.md` ("### The Two Composition Phases" for the hook shape, the invocation point, the placement rule, and the additive `Sources` guard; "### Contract Cardinality" for the `Contribute` row; "### The Plugin Contract" for the additive-only policy and the version move; "## Testing Strategy" for the Phase A cases)
- `reference/design/secrets-DMS-1503/design.md` ("### Phase A: The Process-Global Secrets" for the values it serves and the ones it cannot)

Every decision here was taken by the spine and is implemented rather than revisited.
This is the first exercise of the contract's additive-only policy: a plugin compiled against `EdFi.Api.Plugins` 1.0.0 must load and run on a host carrying 1.1.0 without being rebuilt, and that is asserted rather than assumed.

**Citation convention.** Unprefixed paths are relative to the repository root, and `Program.cs` and `Infrastructure/` are relative to `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/`, following the plugin spine's drafts.

Nothing in CMS is touched.
The CMS half of Phase A arrives with CMS host integration, which depends on this story.

## Acceptance Criteria

- `EdFiApiPlugin` gains `public virtual void ContributeConfiguration(IConfigurationBuilder configurationBuilder, IConfiguration bootstrapConfiguration)` with an empty body and XML documentation for both parameters. The documentation does not call `bootstrapConfiguration` read-only; a write through it is a documented trust assumption, matching what the merged contract already says about `ContributeServices`, and `PLUGINS.md` states both in one place.
- `src/plugins/Directory.Build.props` moves the contract version from `1.0.0` to `1.1.0`. The loader's skew preflight compares exactly this value.
- `LoadedPlugins` gains `public void ContributeConfiguration(ConfigurationManager configuration)`, passing the one argument as both parameters of each plugin's hook (`ConfigurationManager` implements both interfaces), invoking hooks in allowlist order.
- The loader writes `invoking ContributeConfiguration on <plugin>` to its diagnostics channel before each hook, matching `ContributeServices`. A test asserts the announcement is on the channel when a fixture hook throws.
- **The additive `Sources` guard.** The loader snapshots `configurationBuilder.Sources` before each Phase A call and compares after; any pre-existing source removed or moved is fatal, naming the plugin; adding is the only permitted operation. Mutation of a pre-existing source object is undetectable at this seam and is stated as a trust assumption in the XML documentation and `PLUGINS.md`.
- **Placement.** After each hook returns and the guard passes, the loader moves that plugin's appended sources immediately below the last `EnvironmentVariablesConfigurationSource` present when Phase A began, preserving relative order. The loader adds no source of its own. "Last environment source" is the rule rather than an index, per the spine's measurements.
- A unit test over the host's real source list asserts the three precedence outcomes: a plugin key beats the empty string `appsettings.json` ships, an environment value beats the plugin, a command-line value beats the plugin. Unit rather than integration, because `WebApplicationFactory` cannot pass configuration arguments and the stock container installs no command-line source (`src/dms/run.sh:118`).
- Fatal-versus-permitted cases each their own test: a plugin removing a pre-existing source is fatal and named; one reordering is fatal and named; one that only appends is unaffected.
- One test asserts a Phase A plugin cannot influence `Plugins:Allowed`, which was consumed before Phase A ran.
- **The `Contribute`-only exemption in the no-contract-registered check** reads the Phase A record historically: a plugin contributing nothing anywhere stays fatal; the same plugin with one Phase A source added is not; a source added and then removed by a later plugin still counts. Both directions asserted.
- `Program.cs` invokes `loadedPlugins.ContributeConfiguration(builder.Configuration)` inside the existing `LoadPlugins` bootstrap phase, after `PluginLoader.Load` returns and before the `ConfigureServices` phase. `AddServices` reads the `Serilog` section on its first line (`Infrastructure/WebApplicationBuilderExtensions.cs:36`; the merged spine's `:38` has drifted and this story cites the current line).
- `docs/CONFIGURATION.md` documents the values Phase A cannot supply: `AppSettings:StartupStatusFilePath` (read at `Program.cs:30-33` before plugins load), the `Plugins` section (consumed to decide what loads), and `DATABASE_CONNECTION_STRING_ADMIN`, the one that is a secret, parsed by `src/dms/run.sh:14-16` in the shell before the .NET host exists.
- `docs/CONFIGURATION.md` states the precedence order (environment, command line, plugin sources in allowlist order, JSON sources) and why the environment outranks the command line: DMS's own `AddEnvironmentVariables()` at `Infrastructure/WebApplicationBuilderExtensions.cs:44`, qualified to keys read after that call; Serilog's configuration and `Plugins:Allowed` predate it and the command line wins there. (The spine's `:46` citation has drifted; this story cites the current line.)
- **The compatibility fixture moves to `1.2.0`** so production 1.1.0 stays distinct and a subset: `ContributeConfiguration` is copied into the fixture surface, `DescribeCapabilities` remains its one added virtual, and every version literal updates - `Fixtures/Hosts/PluginHostRunner/Program.cs:35`, `PluginContractCompatibilityTests.cs:86`, `:315-323`, `:408`, `:423`, `:450-452`, `:501-502` - with the fixture directory, project, and `PluginFixtures` symbol renamed from `1_1` to `1_2` and their comments corrected.
- `Acme.OldContract`, overriding only `ContributeServices`, runs on the newer host with `ContributeConfiguration` taking the base no-op body. `Acme.NewerContract` is untouched (built against the separate `Acme.Contract2`). The skew-refusal message assertion at `PluginLoaderVersionTests.cs:87` tightens to the host's actual version, since `1.0.0` being a substring of `1.1.0.0` would otherwise pass by accident.
- An integration test boots `WebApplicationFactory<Program>` with a fixture plugin whose Phase A source supplies `ConfigurationServiceSettings:EncryptionKey`, asserting DMS resolves the plugin's value over the empty string `appsettings.json` ships; a real key, because the empty-string default is the shadowing case the placement rule exists to prevent. A second case asserts a key also set in the environment resolves to the environment's value. A third asserts the loader added no source of its own.
- `src/plugins/EdFi.Api.Plugins/PLUGINS.md` gains the Phase A implementer rules: the two parameters, additive-only contribution, what the guard does and does not catch, and that allowlist order is contractual for Phase A.
- `dotnet test src/plugins/EdFi.Api.Plugins.Hosting.Tests.Unit/EdFi.Api.Plugins.Hosting.Tests.Unit.csproj` and `dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration` pass.

## Tasks

1. Add `ContributeConfiguration` to the contract with its XML documentation and move the contract version to 1.1.0.
2. Add the `LoadedPlugins` aggregate hook, the loader's invocation with diagnostics announcements, the additive `Sources` guard, and the placement step.
3. Wire the Phase A invocation into DMS's `LoadPlugins` bootstrap phase in `Program.cs`.
4. Extend the no-contract-registered check with the historically-recorded `Contribute`-only exemption.
5. Move the compatibility fixture to 1.2.0, copy the new member into its surface, and update every version literal, directory, project, and symbol name.
6. Add the unit tests (precedence, guard, allowlist isolation, exemption) and the three integration cases.
7. Update `docs/CONFIGURATION.md` (precedence order with its qualifier, out-of-reach values) and `PLUGINS.md` (Phase A implementer rules).
