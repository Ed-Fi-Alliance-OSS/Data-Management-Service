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
It adds `ContributeConfiguration` to the contract, invokes it from the DMS loader, places contributed sources where the spine's rule says they must sit, guards the contribution as additive, and carries the Phase A rows of the spine's test plan.

Every decision here was taken by the spine and is implemented rather than revisited, per:

- `reference/design/plugins-DMS-1462/design.md` ("### The Two Composition Phases", "### Contract Cardinality", "### The Plugin Contract", "## Testing Strategy")
- `reference/design/secrets-DMS-1503/design.md` ("### Phase A: The Process-Global Secrets")

Nothing in CMS is touched.
The CMS half of Phase A arrives with CMS host integration, which depends on this story.

## Technical Implementation

**Citation convention.**
Unprefixed paths are relative to the repository root.
`Program.cs` and `Infrastructure/` are relative to `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/`, following the plugin spine's drafts.

**The hook shape is inherited and not redesigned.**
The signature, the second `bootstrapConfiguration` parameter, and the plugin's freedom to read it are all the spine's decisions.
`ConfigurationManager` implements both `IConfigurationBuilder` and `IConfiguration`, so the aggregate passes one object as both arguments.

**`bootstrapConfiguration` is readable, and calling it read-only would be an overclaim.**
`IConfiguration` exposes a settable indexer, so a plugin holding one can write a key through it.
The merged contract already says exactly this about the sibling parameter `ContributeServices` receives.
A write through it is a documented trust assumption rather than something the guard catches, and it belongs in the same sentence as the other trust assumption below.

**The additive `Sources` guard catches removal and reordering, and cannot catch mutation.**
The loader snapshots `configurationBuilder.Sources` before each hook and compares after.
A pre-existing source object whose own properties a plugin mutates is the same object in the same position, so the comparison reports clean.
That limit is stated to implementers rather than papered over.

**Placement is a rule, not an index.**
Contributed sources move immediately below the last `EnvironmentVariablesConfigurationSource` present when Phase A began, preserving relative order among a plugin's own additions.
The spine measured that the host's source list differs in length between hosts and across versions, so an index would silently drift while the rule does not.
The loader adds no source of its own.

**The version move is the first exercise of the additive-only policy.**
`EdFi.Api.Plugins` goes from `1.0.0` to `1.1.0`, which the spine specified for exactly this member.
A plugin compiled against `1.0.0` must load and run unchanged on a `1.1.0` host, taking the base no-op body, and that is asserted rather than assumed.

**The compatibility fixture moves to `1.2.0` so production `1.1.0` stays a distinct subset.**
The fixture exists to represent a contract surface strictly larger than production's.
With production at `1.1.0` the fixture's current `1.1.0` would no longer be larger, so it moves up one and `ContributeConfiguration` is copied into its surface, leaving `DescribeCapabilities` as its one added virtual.

**The skew-refusal assertion has to tighten or it passes by accident.**
`src/plugins/EdFi.Api.Plugins.Hosting.Tests.Unit/PluginLoaderVersionTests.cs:87` asserts the refusal message contains `1.0.0`, and `1.0.0` is a substring of `1.1.0.0`.
Once the host carries the new version that assertion would pass whatever the message said, so it is pinned to the host's actual version.

**The precedence proof is a unit test over the host's real source list rather than an integration test.**
`WebApplicationFactory` cannot pass command-line configuration arguments, and the shipped container installs no command-line source at all, because `src/dms/run.sh:118` starts the application with no arguments.
An integration test therefore could not construct the case that distinguishes the three outcomes.

**The environment-over-command-line rule carries a qualifier, and the spine requires it.**
DMS's own `AddEnvironmentVariables()` call at `Infrastructure/WebApplicationBuilderExtensions.cs:44` is what puts the environment above the command line, and it only does so for keys read after that call.
Two reads predate it and the command line wins for both: Serilog's configuration, read by `ConfigureLogging` on the first line of `AddServices` (`:36`), and `Plugins:Allowed`, consumed in the `LoadPlugins` bootstrap phase.
The spine's citations of `:38` and `:46` have drifted by two lines; this story cites the current ones.

**Three values are out of Phase A's reach, and one of them is a secret.**
`AppSettings:StartupStatusFilePath` is read at `Program.cs:30-33` before plugins load, because that file is how a loader fatal is reported.
The `Plugins` section itself is consumed to decide what loads, which is what makes the allowlist a surface no plugin can reach.
`DATABASE_CONNECTION_STRING_ADMIN` carries elevated database credentials and is parsed by `src/dms/run.sh:14-16` in the shell before the .NET host exists, so no configuration source of any origin can serve it.

**The integration fixture supplies a real key rather than an invented one.**
`ConfigurationServiceSettings:EncryptionKey` ships as an empty string in `appsettings.json`, which is precisely the shadowing case the placement rule exists to prevent.
A test against an invented key would pass without exercising that.

## Acceptance Criteria

**Contract**

- `EdFiApiPlugin` declares `public virtual void ContributeConfiguration(IConfigurationBuilder configurationBuilder, IConfiguration bootstrapConfiguration)` with an empty body.
- Both parameters carry XML documentation, and neither is described as read-only.
- The XML documentation states that a write through `bootstrapConfiguration`, and mutation of a pre-existing source object, are trust assumptions the host does not enforce.
- `src/plugins/Directory.Build.props` declares contract version `1.1.0`.

**Loader**

- `LoadedPlugins` declares `public void ContributeConfiguration(ConfigurationManager configuration)` and invokes each plugin's hook in allowlist order, passing its argument as both parameters.
- The loader writes `invoking ContributeConfiguration on <plugin>` to its diagnostics channel before each hook, matching `ContributeServices`.
- The loader snapshots `configurationBuilder.Sources` before each hook and compares after.
- Each of these fails the boot, naming the plugin:
  - a plugin that removes a pre-existing source
  - a plugin that reorders a pre-existing source
- A plugin that only appends sources completes without error.
- After each hook returns and the guard passes, the loader moves that plugin's appended sources immediately below the last `EnvironmentVariablesConfigurationSource` present when Phase A began, preserving their relative order.
- The loader adds no configuration source of its own.

**Host wiring**

- `Program.cs` calls `loadedPlugins.ContributeConfiguration(builder.Configuration)` inside the existing `LoadPlugins` bootstrap phase, after `PluginLoader.Load` returns and before the `ConfigureServices` phase.

**Cardinality**

- The spine's cardinality table gains its `Contribute` row, covering a plugin whose only contribution is a Phase A source.

**No-contract-registered check**

- A plugin that contributes nothing in either phase still fails the boot.
- A plugin that added one Phase A source and registered no service does not fail the boot.
- A plugin whose Phase A source was later removed by another plugin does not fail the boot, because the Phase A record is read historically.

**Compatibility fixture**

- The fixture contract is at `1.2.0`, carries `ContributeConfiguration`, and keeps `DescribeCapabilities` as its one added virtual.
- The fixture directory, project, and `PluginFixtures` symbol are renamed from `1_1` to `1_2`, and their comments updated.
- Every version literal is updated: `src/plugins/EdFi.Api.Plugins.Hosting.Tests.Unit/Fixtures/Hosts/PluginHostRunner/Program.cs:35`, `src/plugins/EdFi.Api.Plugins.Hosting.Tests.Unit/PluginContractCompatibilityTests.cs:86`, `:315-323`, `:408`, `:423`, `:450-452`, `:501-502`.
- `src/plugins/EdFi.Api.Plugins.Hosting.Tests.Unit/PluginLoaderVersionTests.cs:87` asserts the skew-refusal message against the host's actual version rather than against `1.0.0`.

**Tests**

- A unit test over the host's real source list asserts all three precedence outcomes:
  - a plugin-supplied key wins over the empty string `appsettings.json` ships
  - an environment value wins over a plugin-supplied value
  - a command-line value wins over a plugin-supplied value
- A test asserts the diagnostics announcement is on the channel when a fixture hook throws.
- A test asserts a Phase A source cannot change `Plugins:Allowed`.
- A test asserts `Acme.OldContract`, which overrides only `ContributeServices`, loads and runs on the `1.1.0` host.
- `Acme.NewerContract` is unchanged, remaining built against `Acme.Contract2`.
- An integration test boots `WebApplicationFactory<Program>` with a fixture plugin whose Phase A source supplies `ConfigurationServiceSettings:EncryptionKey`, asserting:
  - DMS resolves the plugin's value
  - a key also set in the environment resolves to the environment's value
  - the loader added no source of its own

**Documentation**

- `docs/CONFIGURATION.md` states:
  - the precedence order: environment variables, command-line arguments, plugin sources in allowlist order, then `appsettings.json` and the other JSON sources
  - that the environment outranks the command line only for keys read after `Infrastructure/WebApplicationBuilderExtensions.cs:44`, naming Serilog's configuration and `Plugins:Allowed` as the two reads where the command line wins
  - the three values Phase A cannot supply: `AppSettings:StartupStatusFilePath`, the `Plugins` section, and `DATABASE_CONNECTION_STRING_ADMIN`
- `src/plugins/EdFi.Api.Plugins/PLUGINS.md` states the Phase A implementer rules: the two parameters, additive-only contribution, what the guard does and does not catch, and that allowlist order is contractual.

**Build**

- These pass:
  - `dotnet test src/plugins/EdFi.Api.Plugins.Hosting.Tests.Unit/EdFi.Api.Plugins.Hosting.Tests.Unit.csproj`
  - `dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration`

## Tasks

1. Add `ContributeConfiguration` to `EdFiApiPlugin` with its XML documentation, and move `src/plugins/Directory.Build.props` to `1.1.0`.
2. Add the `LoadedPlugins.ContributeConfiguration` aggregate hook with diagnostics announcements.
3. Add the additive `Sources` guard and the placement step to the loader.
4. Call the aggregate hook from the `LoadPlugins` bootstrap phase in `Program.cs`.
5. Add the `Contribute` cardinality row and extend the no-contract-registered check with its historically-read `Contribute`-only exemption.
6. Move the compatibility fixture to `1.2.0`: rename directory, project, and symbol, copy the new member into its surface, and update every version literal.
7. Tighten the skew-refusal assertion in `PluginLoaderVersionTests.cs`.
8. Add the unit tests: precedence, guard fatal and permitted cases, diagnostics announcement, allowlist isolation, and the exemption in both directions.
9. Add the three integration cases against a Phase A fixture plugin.
10. Update `docs/CONFIGURATION.md` and `PLUGINS.md`.
