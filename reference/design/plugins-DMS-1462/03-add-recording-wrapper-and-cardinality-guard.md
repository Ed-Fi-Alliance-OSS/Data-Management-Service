---
jira: TBD
jira_url: TBD
epic: TBD
source_spike: DMS-1462
---

# Story: Add the Recording Service Collection and Cardinality Guard

## Description

The loader returns plugin instances; something has to invoke `ContributeServices` on each, know what each one registered, and refuse the registrations the design refuses.
This story adds the recording `IServiceCollection` wrapper, the host-held cardinality registry, the Phase B invocation, and the three guard checks fed by the wrapper, per:

- `reference/design/plugins-DMS-1462/design.md` ("### Contract Cardinality", "### Startup Failure Semantics" for the wrapper and guard rows, "### Observability" for the inventory event)
- `reference/design/custom-validation-DMS-1345/03-add-custom-validator-composition-seam-and-startup-guard.md` (DMS-1434), the post-container startup guard this story extends

Two of the checks run at the wrapper, before a call reaches the real collection: removal or overwrite of a pre-existing host-owned descriptor, and `Clear`.
Three run in the startup guard after the container is built: two plugins claiming one replace-cardinality contract, a plugin registering a host-owned service type that is no declared contract, and a plugin that registered no declared contract at all.
All five key on one test, whether a `ServiceType` is declared in an `EdFi.DataManagementService.*` or `EdFi.DmsConfigurationService.*` assembly, and design.md records that a further rule is the signal the wrapper has become something it should not be.

The wrapper is necessary rather than convenient: `TryAdd` and `TryAddEnumerable` produce no collection diff when they decline, which is exactly the replace-conflict case.

This story depends on draft 02 for the instances and on DMS-1434 for the guard it extends.

## Acceptance Criteria

- `RecordingServiceCollection` implements `IServiceCollection`, delegates every member to a real collection, and records per plugin: every descriptor added, every descriptor removed or overwritten, and every `TryAdd`-family call that declined, with the service type and implementation involved.
- A `PluginContractRegistry` type in `EdFi.Api.Plugins.Hosting` holds a set of `(Type contract, Cardinality cardinality)` entries the host constructs and passes to the invoker; DMS's registry names `ICustomResourceValidator` as fan-in. The registry is never read from configuration or from a plugin.
- `LoadedPlugins.ContributeServices(IServiceCollection, IConfiguration, PluginContractRegistry)` invokes each plugin's hook in allowlist order through its own recording wrapper, writes `invoking ContributeServices on <plugin>` to `Console.Error` **before** each call, and turns a thrown exception into a fatal naming the plugin and the phase. A test has the fixture hook throw and asserts the announcement is already on the channel.
- Removing, replacing, or assigning through the indexer over a descriptor that was present before the plugin's hook began **and** whose `ServiceType` is host-owned is fatal at the wrapper, naming the plugin and the service type, and the real collection is asserted unchanged. Proven with `services.Replace(...)` on `IDocumentStoreRepository`, `RemoveAll<T>()` over a host type, and an indexer assignment over a pre-existing host-owned slot. `Clear` is always fatal.
- Removing a pre-existing **non-host** descriptor is permitted and recorded. Proven with a fixture calling `RemoveAll<IHttpMessageHandlerBuilderFilter>()` after the host registered one: the plugin loads, and its inventory lists the removal with the displaced implementation type.
- A plugin that registers a descriptor and then replaces it is unaffected, which pins the pre-existing scope.
- A fixture plugin calling the real `AddHttpClient()`, `AddOptions<T>().Bind(...)`, `AddLogging()`, and one real cloud SDK client registration (`Azure.Extensions.AspNetCore` or the AWS equivalent, whichever the fixture can reference without a feed credential), against a collection DMS's own `AddServices` has already populated, loads with nothing rejected. This is a test against the libraries as shipped.
- In the startup guard: two plugins each registering a fixture replace-cardinality contract through `TryAdd` are both named in the fatal along with the contract, which a collection diff cannot see because the second `TryAdd` declined silently.
- In the startup guard: a plugin registering `IDocumentStoreRepository`, host-owned and no declared contract, is fatal naming the plugin and the type; a plugin registering its own types, `Microsoft.Extensions.*` options, and an `IHostedService` beside a declared contract loads unaffected, and its inventory lists the hosted service.
- In the startup guard: a plugin whose hook registered only its own types is fatal as having contributed no declared contract, naming the plugin and listing what it did register. The message names the likeliest cause, a plugin allowlisted on the wrong host.
- The per-plugin inventory record carries every service type registered, every non-host removal, and the host-first version substitutions from draft 02, and a test asserts the record feeds the guard's checks and the logged event from one source rather than two.
- The DMS-1434 guard's existing criteria continue to pass unchanged: transient-only audit, descriptor-shape audit, activation probe, `Order` in 200-299, and post-container placement. The three new checks are added to the same task rather than a second one.
- `dotnet test src/plugins/EdFi.Api.Plugins.Hosting.Tests.Unit` and `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit` pass.

## Tasks

1. Add `Cardinality`, `PluginContractRegistry`, and the host-owned-type test as one shared predicate.
2. Add `RecordingServiceCollection` with the per-plugin record, the pre-existing snapshot, and the two wrapper-side fatals.
3. Add the Phase B invoker on `LoadedPlugins` with the pre-call announcement and the throw-to-fatal wrapping.
4. Extend the DMS-1434 guard with the replace-conflict, displacement, and no-contract checks, reading the per-plugin records.
5. Build the fixture plugins this story needs as test assets beside draft 02's: replace-contract claimants, host-type registrant, own-types-only registrant, framework-removal caller, and the real-helpers caller.
6. Write the wrapper tests, the guard tests, and the single-source inventory test.
