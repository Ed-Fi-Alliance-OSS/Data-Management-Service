---
jira: TBD
jira_url: TBD
epic: TBD
source_spike: DMS-1462
---

# Story: Add the Recording Service Collection and Cardinality Guard

## Description

The loader returns plugin instances; something has to invoke `ContributeServices` on each, know what each one registered, and refuse the registrations the design refuses.
This story adds the recording `IServiceCollection` wrapper, the host-held cardinality registry, the Phase B invocation, and the four guard checks fed by the wrapper, per:

- `reference/design/plugins-DMS-1462/design.md` ("### Contract Cardinality", "### Startup Failure Semantics" for the wrapper and guard rows, "### Observability" for the inventory event)
- `reference/design/custom-validation-DMS-1345/03-add-custom-validator-composition-seam-and-startup-guard.md` (DMS-1434), the post-container startup guard this story extends

Two of the checks run at the wrapper, before a call reaches the real collection: removal or overwrite of a pre-existing host-owned descriptor, and `Clear`.
Four run after the container is built: two claims on one replace-cardinality contract, a plugin registering a host-owned service type that is no declared contract, a plugin that registered no declared contract at all, and an activation probe over every declared-contract registration.
The first three key on one test, whether a `ServiceType` is declared in an assembly whose **assembly name** begins `EdFi.DataManagementService.` or `EdFi.DmsConfigurationService.`, and design.md records that a further rule is the signal the wrapper has become something it should not be.

**The post-container checks are a startup task of their own, in the frontend, not additions to Core's.**
DMS-1434's guard is an `IDmsStartupTask` in `EdFi.DataManagementService.Core`, which cannot see the per-plugin records this story produces in `EdFi.Api.Plugins.Hosting` without a project reference that would invert the dependency and drag the contract assembly into every Core consumer.
So `EdFi.Api.Plugins.Hosting` exposes a pure audit function over the records and the contract registry, returning findings, and the DMS frontend owns a thin `IDmsStartupTask` that calls it, ordering just above DMS-1434's guard inside the same executed 200-299 window.
Core is untouched by this story.

**The wrapper cannot see a declined `TryAdd`, so replace-cardinality descriptors are masked instead.**
`TryAdd` reads `Count` and the indexer, compares, and returns without handing the candidate descriptor to the collection, so a decline arrives at the wrapper as an anonymous scan with no service type and no implementation type attached.
Rather than promise a record nothing can produce, the wrapper presents each plugin a view in which descriptors for replace-cardinality contracts are invisible, so a claim on one always lands as an `Add` and is always attributed.
Fan-in contracts are not masked, because `TryAddEnumerable`'s deduplication is behaviour a fan-in registration relies on.

This story depends on draft 02 for the instances and on DMS-1434 for the guard it runs beside.

## Acceptance Criteria

- `RecordingServiceCollection` implements `IServiceCollection`, delegates every member to a real collection, and records per plugin every descriptor added and every descriptor removed or overwritten, with the service type and implementation involved. It records **no** declined `TryAdd` calls, and a test documents why by driving `TryAddSingleton<IFoo, Foo2>` against a wrapper over a collection that already holds `IFoo` and asserting the wrapper observed only `Count` and indexer reads and never saw `Foo2`. That test exists so nobody later adds an acceptance criterion the seam cannot satisfy.
- The wrapper masks replace-cardinality contract descriptors from the view it presents: `Count`, the indexer, `IndexOf`, `Contains`, and enumeration all skip them, whoever added them, for the duration of one plugin's hook. Proven three ways: a plugin enumerating the collection during its hook does not see a replace-contract descriptor the host registered; the same plugin does see a fan-in contract descriptor and the framework descriptors `AddServices` left behind; and the real collection still contains the masked descriptor after the hook returns, asserted directly rather than through the wrapper.
- A `PluginContractRegistry` type in `EdFi.Api.Plugins.Hosting` holds a set of `(Type contract, Cardinality cardinality)` entries the host constructs and passes to the invoker; DMS's registry names `ICustomResourceValidator` as fan-in. The registry is never read from configuration or from a plugin. Because it is host-supplied, a fixture registry declaring a fixture replace contract is what the masking and conflict tests below use, so neither depends on a real replace contract existing yet.
- `LoadedPlugins.ContributeServices(IServiceCollection, IConfiguration, PluginContractRegistry)` invokes each plugin's hook in allowlist order through its own recording wrapper, writes `invoking ContributeServices on <plugin>` to `Console.Error` **before** each call, and turns a thrown exception into a fatal naming the plugin and the phase. A test has the fixture hook throw and asserts the announcement is already on the channel.
- Removing, replacing, or assigning through the indexer over a descriptor that was present before the plugin's hook began **and** whose `ServiceType` is host-owned is fatal at the wrapper, naming the plugin and the service type, and the real collection is asserted unchanged. Proven with `services.Replace(...)` on `IDocumentStoreRepository`, `RemoveAll<T>()` over a host type, and an indexer assignment over a pre-existing host-owned slot. `Clear` is always fatal.
- Removing a pre-existing **non-host** descriptor is permitted and recorded. Proven with a fixture calling `RemoveAll<IHttpMessageHandlerBuilderFilter>()` after the host registered one: the plugin loads, and its inventory lists the removal with the displaced implementation type.
- A plugin that registers a descriptor and then replaces it is unaffected, which pins the pre-existing scope.
- A fixture plugin calling the real `AddHttpClient()`, `AddOptions<T>().Bind(...)`, `AddLogging()`, and one real cloud SDK client registration, against a collection DMS's own `AddServices` has already populated, loads with nothing rejected. This is a test against the libraries as shipped, not against a fixture that imitates them. The cloud SDK is `Microsoft.Extensions.Azure`, whose `AddAzureClients` is the registration that exercises the `Replace`/`RemoveAll` behaviour this criterion is about; `Azure.Extensions.AspNetCore.Configuration.Secrets` is the configuration-provider package and registers nothing, so it is the wrong one to reach for. `src/Directory.Packages.props` pins no `Azure*` package today, so whichever is chosen is added there, since the repository manages package versions centrally (`src/Directory.Packages.props:3`).
- Two plugins each registering a fixture replace-cardinality contract through `TryAdd` are both named in the fatal along with the contract. Without masking the second `TryAdd` would decline silently and the conflict would be invisible, so the test is written to fail if masking is removed.
- **One** plugin registering two descriptors for the same fixture replace-cardinality contract is fatal on its own, naming the plugin and the contract. Proven with one hook calling `TryAdd` twice with different implementation types and, separately, with one hook calling `Add` twice. Without this criterion the case passes and DI's last-wins silently picks one, which is the outcome the replace cardinality's "0 or 1" exists to refuse.
- A plugin registering `IDocumentStoreRepository`, host-owned and no declared contract, is fatal naming the plugin and the type; a plugin registering its own types, `Microsoft.Extensions.*` options, and an `IHostedService` beside a declared contract loads unaffected, and its inventory lists the hosted service. A separate case pins that the predicate keys on assembly identity rather than package identity: `ICustomResourceValidator` is declared in the assembly `EdFi.DataManagementService.CustomValidation`, so it matches the host-owned predicate and is admitted only because the declared-contract exemption is evaluated first; a test asserts that registering it succeeds and that removing it from the registry makes the same registration fatal.
- In the startup guard: a plugin whose hook registered only its own types is fatal as having contributed no declared contract, naming the plugin and listing what it did register. The message names the likeliest cause, a plugin allowlisted on the wrong host.
- Every registration of a declared plugin contract is resolved once from a throwaway scope and the instances discarded; a registration that cannot be constructed is fatal, naming the plugin, the contract, and the activation exception. Proven with a fixture plugin registering a fan-in contract implementation whose constructor takes a service nobody registered. This is the plugin guard's own probe: DMS-1434's activation probe covers `ICustomResourceValidator` because that contract's story asked for it, and a contract added by a later companion document would otherwise arrive with no activation at all.
- The per-plugin inventory record carries every service type registered, every non-host removal, the host-first version substitutions from draft 02, and that draft's per-assembly digests, and a test asserts the record feeds the guard's checks and the logged event from one source rather than two.
- The four post-container checks live in a frontend-owned `IDmsStartupTask` whose `Order` is in 200-299 and above DMS-1434's guard, reading findings from a pure audit function in `EdFi.Api.Plugins.Hosting`. A test asserts the task actually executed during a real `WebApplicationFactory<Program>` boot, following DMS-1434's own precedent for that assertion, and the pull request diff is asserted to contain no change under `src/dms/core/`.
- The DMS-1434 guard's existing criteria continue to pass unchanged: transient-only audit, descriptor-shape audit, activation probe, `Order` in 200-299, and post-container placement.
- `dotnet test src/plugins/EdFi.Api.Plugins.Hosting.Tests.Unit` and `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit` pass.

## Tasks

1. Add `Cardinality`, `PluginContractRegistry`, and the host-owned-type test as one shared predicate.
2. Add `RecordingServiceCollection` with the per-plugin record, the pre-existing snapshot, the replace-contract masking projection, and the two wrapper-side fatals.
3. Add the Phase B invoker on `LoadedPlugins` with the pre-call announcement and the throw-to-fatal wrapping.
4. Add the audit function in `EdFi.Api.Plugins.Hosting` (replace-conflict, displacement, no-contract, activation) over the per-plugin records and the registry, and the frontend-owned `IDmsStartupTask` that calls it and takes the existing fatal path. Do not modify DMS-1434's guard or anything under `src/dms/core/`.
5. Build the fixture plugins this story needs as test assets under `src/plugins/EdFi.Api.Plugins.Hosting.Tests.Unit/Fixtures/`, beside draft 02's: replace-contract claimants (two plugins, and one plugin claiming twice), host-type registrant, own-types-only registrant, framework-removal caller, unsatisfiable-dependency registrant, and the real-helpers caller. The real-helpers fixture is the exception: it must run against a collection DMS's own `AddServices` has populated, so its test lives in `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit`, which already references the frontend, and the fixture plugin assembly it loads is built from the same `Fixtures/` tree. Nothing in this arrangement gives `src/plugins/` a reference to the frontend.
6. Write the wrapper tests, the guard tests, and the single-source inventory test.
