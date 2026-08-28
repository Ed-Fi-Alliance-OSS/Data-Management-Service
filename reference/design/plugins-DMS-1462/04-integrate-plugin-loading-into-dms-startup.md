---
jira: TBD
jira_url: TBD
epic: TBD
source_spike: DMS-1462
---

# Story: Integrate Plugin Loading into DMS Startup

## Description

The loader, the wrapper, and the guard exist but nothing in DMS calls them.
This story adds the `LoadPlugins` bootstrap phase to `Program.cs`, threads the loaded plugins into `AddServices`, re-emits the load inventory through Serilog once it exists, and proves the whole path with a real fixture plugin loaded by a real host, per:

- `reference/design/plugins-DMS-1462/design.md` ("### The Two Composition Phases" for the Phase B call site and the diagnostics rule, "### Observability", "### What the Stock Image Must Ship", "## Testing Strategy" integration and local-image tiers)

Only Phase B is wired.
Phase A's invocation, source placement, and additive guard land with the secrets spike's first foundation story.

The image changes by one assembly and the compose file by one `volumes:` line; `run.sh` and both Dockerfiles are untouched.
This is what keeps the stock-image claim checkable.

The end-to-end tier here runs against a **locally built** image and proves everything except the word "stock"; draft 07 proves that word against a pulled image once one carries the loader.

## Acceptance Criteria

- `DmsStartupPhases.LoadPlugins` exists and `Program.cs` runs `PluginLoader.Load(builder.Configuration)` inside `RunBootstrapPhase` between `WebApplication.CreateBuilder(args)` and the `ConfigureServices` phase, so it precedes the first configuration read in `AddServices`. A loader fatal takes the existing bootstrap fatal path and the startup status file records a failed `LoadPlugins` phase with the exception type and message, asserted by test.
- `WebApplicationBuilderExtensions.AddServices` takes the loaded plugins and invokes `ContributeServices` through the draft 03 invoker with DMS's `PluginContractRegistry`, after the host's own registrations and before `builder.Build()`.
- Once the logger exists, one structured `Information` event per loaded plugin is emitted with named properties: `PluginName`, `AssemblyVersion`, `EntryAssemblySha256`, `RegisteredServiceTypes`, `RemovedDescriptors`, and `HostFirstSubstitutions`. A test captures the log output and asserts the properties are present and that no configuration key or value appears.
- A `WebApplicationFactory<Program>` integration test boots with `Plugins:Directory` pointed at a fixture plugin directory and `Plugins:Allowed` naming it, and resolves a service only that plugin's `ContributeServices` could have registered.
- Fatal cases at the integration tier substitute `IStartupProcessExit` with a non-exiting double following `Tests.Integration/Doubles/NonExitingStartupProcessExit.cs`, and assert: a misspelled allowlist entry, a plugin allowlisted on a missing root, and a plugin whose hook throws each take the fatal path before any request is served.
- With `Plugins:Allowed` empty and no plugin root present, the host boots exactly as it does on main, asserted by the existing integration suite passing unchanged.
- A directory present under the plugin root but absent from `Plugins:Allowed` produces one warning naming it and is never opened, asserted by placing a directory whose entry assembly is deliberately corrupt and observing a clean boot.
- `docs/CONFIGURATION.md` gains the `Plugins` section with `Directory` and `Allowed`, stating that `Allowed` is the only switch and that its order is invocation order. The full operator and implementer documentation is draft 05; this story adds only what its own configuration surface needs.
- `eng/docker-compose/published-dms.yml` gains `- ${DMS_PLUGINS_MOUNT_SOURCE:-./plugins}:/app/plugins:ro` on the DMS service and `.env.example` documents the variable. `src/dms/run.sh`, `src/dms/Dockerfile`, and `src/dms/Nuget.Dockerfile` are unchanged, asserted by the pull request diff.
- A local end-to-end test builds the DMS image with `build-dms.ps1`, publishes the DMS-1436 fixture validator `--no-self-contained` into a plugin directory, bind-mounts it read-only, allowlists it, and asserts over HTTP that a POST failing the fixture's check returns the custom-validation 400 and that removing the name from `Plugins:Allowed` makes the same POST succeed. This replaces DMS-1436's compiled-in negative control and is the local-image half of the stock-image proof.
- The same test runs Recipe 2 from design.md "### Acquisition" verbatim: the fixture validator packed asset-only into a `.nupkg` in a local folder feed, fetched by the one-shot `fetch-plugins` service, digest-verified, extracted, and loaded, then re-run with a deliberately wrong digest asserting the service fails and DMS never starts.
- `dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration` passes.

## Tasks

1. Add the `LoadPlugins` constant and the `RunBootstrapPhase` call in `Program.cs`, capturing the result for `AddServices`.
2. Thread the loaded plugins into `AddServices` and invoke Phase B through the draft 03 invoker with DMS's registry.
3. Add the post-logger inventory event, re-emitting what the loader wrote to `Console.Error`.
4. Add the `Plugins` section to `appsettings.json` with defaults and to `docs/CONFIGURATION.md`.
5. Add the `volumes:` line to `published-dms.yml` and the variable to `.env.example`.
6. Write the integration tests, reusing the fixture plugin directories from drafts 02 and 03 where they fit and adding a DMS-specific one that registers `ICustomResourceValidator`.
7. Write the local-image end-to-end test covering both recipes and the wrong-digest negative, driving the compose file as written.
