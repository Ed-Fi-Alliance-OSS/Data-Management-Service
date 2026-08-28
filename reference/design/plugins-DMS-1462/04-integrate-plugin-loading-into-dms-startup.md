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

The image gains two assemblies in the application's own publish output, and `src/dms/Dockerfile`'s build stage has to change so it can produce them: it copies an explicit per-project list inside the `src/dms/` build context and restores `--locked-mode`, and `src/plugins/` sits outside that tree.
The runtime stage, `src/dms/run.sh`, the entry point, `src/dms/Nuget.Dockerfile`, and the base compose files are all untouched, and that is what keeps the stock-image claim checkable.

This story builds its own minimal fixture plugin and does not depend on custom validation.
The custom-validation 400 over HTTP is DMS-1436's proof, and DMS-1436 depends on this story; wiring it the other way would make the two stories mutually blocking.

The end-to-end tier here runs against a **locally built** image and proves everything except the word "stock"; draft 07 proves that word against a pulled image once one carries the loader.

## Acceptance Criteria

- `DmsStartupPhases.LoadPlugins` exists and `Program.cs` runs `PluginLoader.Load(builder.Configuration)` inside `RunBootstrapPhase` between `WebApplication.CreateBuilder(args)` and the `ConfigureServices` phase, so it precedes the first configuration read in `AddServices`. A loader fatal takes the existing bootstrap fatal path and the startup status file records a failed `LoadPlugins` phase with the exception type and message, asserted by test.
- `WebApplicationBuilderExtensions.AddServices` takes the loaded plugins and invokes `ContributeServices` through the draft 03 invoker with DMS's `PluginContractRegistry`, after the host's own registrations and before `builder.Build()`.
- Once the logger exists, one structured `Information` event per loaded plugin is emitted with named properties: `PluginName`, `AssemblyVersion`, `EntryAssemblySha256`, `RegisteredServiceTypes`, `RemovedDescriptors`, and `HostFirstSubstitutions`. A test captures the log output and asserts the properties are present and that no configuration key or value appears.
- A `WebApplicationFactory<Program>` integration test boots with `Plugins:Directory` pointed at a fixture plugin directory and `Plugins:Allowed` naming it, and resolves a service only that plugin's `ContributeServices` could have registered.
- Fatal cases at the integration tier assert on the exception escaping host creation, not on `IStartupProcessExit`. Plugin loading runs pre-`Build()` inside `RunBootstrapPhase`, which writes the failed phase and rethrows (`Program.cs:443-462`), while `IStartupProcessExit` is DI-registered (`Infrastructure/WebApplicationBuilderExtensions.cs:79`) and first resolved after `builder.Build()` (`Program.cs:130`), so substituting it would prove nothing about this phase and a test that passed by substituting it would be passing for the wrong reason. Each of a misspelled allowlist entry, a plugin allowlisted on a missing root, and a plugin whose hook throws is asserted to throw out of `WebApplicationFactory`'s host creation and to leave a failed `LoadPlugins` record in the startup status file naming the reason, with no request ever served.
- With `Plugins:Allowed` empty and no plugin root present, the host boots exactly as it does on main, asserted by the existing integration suite passing unchanged.
- A directory present under the plugin root but absent from `Plugins:Allowed` produces one warning naming it and is never opened, asserted by placing a directory whose entry assembly is deliberately corrupt and observing a clean boot.
- `docs/CONFIGURATION.md` gains the `Plugins` section with `Directory` and `Allowed`, stating that `Allowed` ships empty, that it is the only switch, and that its order is invocation order. It also states the one bootstrap exception: `AppSettings:StartupStatusFilePath` is read at `Program.cs:30-33`, before plugins load, so no plugin source can ever supply it. The full operator and implementer documentation is draft 05; this story adds only what its own configuration surface needs.
- Two new overlay compose files carry the mount, and `eng/docker-compose/published-dms.yml` and `eng/docker-compose/local-dms.yml` are unchanged. `eng/docker-compose/plugins-dms.yml` declares `- ${DMS_PLUGINS_MOUNT_SOURCE:?...}:/app/plugins:ro` on the DMS service for Recipe 1; `eng/docker-compose/plugins-fetch-dms.yml` declares the `fetch-plugins` one-shot service, the named volume, the `depends_on: condition: service_completed_successfully`, and the same mount for Recipe 2. They are alternatives, not additions: `/app/plugins` is one mount target. Each is added with `-f`, following `eng/docker-compose/bootstrap-dms.yml` and `local-dms-diagnostics.yml`. The variable is documented in `eng/docker-compose/README.md` and in the `.env.*` files the plugin end-to-end test uses; the repository has no `.env.example`. An unconditional mount in the base files is rejected because Docker would then create an empty root-owned `./plugins` beside every deployment that never asked for a plugin.
- `src/dms/Dockerfile`'s build stage is changed so it can see `src/plugins/`, and the change is minimal and stated: the plugins tree is placed beside the DMS tree exactly as `src/plugins` sits beside `src/dms`, with the repository's `Directory.Packages.props`, `nuget.config`, and `.editorconfig` copied to those trees' common parent rather than only into `/source`; each new project's `.csproj` and `packages.lock.json` join the restore pass at `:15-29`; and each new project's sources join the second pass at `:31-42`. Both new projects must have committed lock files or `--locked-mode` fails the image build, which is asserted by running `build-dms.ps1 DockerBuild` in this story's own end-to-end test rather than only in CI.
- `src/dms/run.sh`, `src/dms/Nuget.Dockerfile`, the Dockerfile's runtime stage (`:61-92`), and `src/dms/frontend/.../EdFi.DataManagementService.Frontend.AspNetCore.nuspec` are unchanged, asserted by the pull request diff. The nuspec needs nothing because it packs `**` from the publish directory, so the two new assemblies ride along.
- A local end-to-end test builds the DMS image with `build-dms.ps1`, publishes **this story's own** minimal fixture plugin `--no-self-contained` into a plugin directory, mounts it read-only through `plugins-dms.yml`, allowlists it, and asserts on the container's log output that the per-plugin inventory event named the fixture with its version and digests, and that removing the name from `Plugins:Allowed` produces no such event and an otherwise identical boot. The fixture registers a fan-in contract implementation the inventory can name; it does not implement `ICustomResourceValidator` and this story does not depend on DMS-1433. Proving a custom-validation 400 over HTTP against a stock image is DMS-1436's criterion, and DMS-1436 depends on this story.
- A second local end-to-end run exercises Recipe 2 from design.md "### Acquisition" verbatim, in its own Compose deployment through `plugins-fetch-dms.yml` rather than alongside Recipe 1, since both end at `/app/plugins`: the same fixture plugin packed asset-only into a `.nupkg` in a local folder feed, fetched by the one-shot `fetch-plugins` service, digest-verified, extracted, and loaded. A third run with a deliberately wrong digest asserts the service exits non-zero and DMS never starts.
- The two sentences in `src/dms/core/EdFi.DataManagementService.CustomValidation/ICustomResourceValidator.cs:16-17` that tell an implementer the interface "is compiled into the host deployment and registered into DMS's composition; it is not loaded from a dropped-in assembly at runtime" are corrected to describe plugin delivery. That text ships inside the nupkg and shows in an implementer's IDE, and this story is the one that makes it false. The change is to the XML documentation only; no type, member, or signature moves, so `eng/verification/Assert-CustomValidationPackage.ps1`'s exported-type assertions are unaffected.
- `dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration` passes.

## Tasks

1. Add the `LoadPlugins` constant to `DmsStartupPhases` (`Infrastructure/StartupPhaseExecutor.cs:21-31`) and the `RunBootstrapPhase` call in `Program.cs`, capturing the result for `AddServices`.
2. Thread the loaded plugins into `AddServices` and invoke Phase B through the draft 03 invoker with DMS's registry.
3. Add the post-logger inventory event, re-emitting what the loader wrote to `Console.Error`.
4. Add `"Plugins": { "Directory": "/app/plugins", "Allowed": "" }` to `appsettings.json` and the matching section to `docs/CONFIGURATION.md`, including the `AppSettings:StartupStatusFilePath` bootstrap exception.
5. Change the `src/dms/Dockerfile` build stage so it carries `src/plugins/` beside the DMS tree, and commit both new projects' lock files.
6. Add `eng/docker-compose/plugins-dms.yml` and `eng/docker-compose/plugins-fetch-dms.yml`, and document `DMS_PLUGINS_MOUNT_SOURCE` in `eng/docker-compose/README.md`.
7. Correct the `ICustomResourceValidator` XML documentation at `src/dms/core/EdFi.DataManagementService.CustomValidation/ICustomResourceValidator.cs:16-17`.
8. Build this story's own minimal fixture plugin: a class library referencing the packed `EdFi.Api.Plugins` whose `ContributeServices` registers a fan-in contract implementation, published `--no-self-contained` as a test asset.
9. Write the integration tests, reusing the fixture plugin directories from drafts 02 and 03 where they fit.
10. Write the local-image end-to-end runs: Recipe 1 in one deployment, Recipe 2 in a second, and the wrong-digest negative in a third, driving each overlay as written.
