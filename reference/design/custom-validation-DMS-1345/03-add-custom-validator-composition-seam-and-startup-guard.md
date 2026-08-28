---
jira: DMS-1434
jira_url: https://edfi.atlassian.net/browse/DMS-1434
epic: DMS-1345
source_spike: DMS-1346
---

# Story: Add the Custom-Validator Startup Guard

## Description

The contract gives an implementer something to implement and the fan-in step gives DMS something that invokes it, but nothing yet checks that a validator that reached the container is registered correctly. This story adds that check, per:

- `reference/design/custom-validation-DMS-1345/design.md` ("### Lifetime and Resolution", "### Startup Failure Semantics")

**Scope change 2026-08-27: this story is the guard only. The composition seam moved to the plugin spine.**
The original story also owned delivery: compiled-in, one call at DMS's composition root, no configuration section and no switch.
Spike DMS-1462 reversed that decision (`reference/design/plugins-DMS-1462/design.md`, "## Divergence from the Custom Validation Epic"): a validator reaches the container as a plugin, its `ContributeServices` hook is invoked by the plugin loader, and `Plugins:Allowed` is the switch.
The seam is delivered by the spine's host-integration story.
The spine's four plugin-attribution checks (replace-conflict, host-service displacement, no-contract-registered, and a declared-contract activation probe) are delivered by its recording-wrapper story as a **separate** `IDmsStartupTask` in the frontend, ordered just above this one inside the same executed 200-299 window, because they read per-plugin records that live in `EdFi.Api.Plugins.Hosting` and Core must not take a project reference into `src/plugins/`.
So this story's guard is not extended by the spine and Core is not touched by it.
Everything about the guard below is unchanged, and matters more: under drop-in delivery the implementer's registration code was compiled somewhere else entirely, and this guard is the only thing between a third-party registration mistake and a captive-dependency defect in production.

An implementer still writes the same registration code, `TryAddEnumerable` plus options binding; the call site is the plugin hook rather than the composition root.

Core's contribution is the guard, not the registration. Core ships an extension the frontend calls once, unconditionally, which registers a startup guard and captures the live `IServiceCollection` so the guard can read the final descriptor set after the container is built.

Reading the descriptors post-container rather than at the extension's own call site is the load-bearing decision in this story. The plugin loader's invocation of implementer hooks may sit before or after Core's, and the implementer is exactly the party the guard exists to check, so any "register your validators before the guard" rule would be broken by the party it protects, and would fail silently. A post-container guard removes ordering from the problem entirely, which is also why the plugin spine's own checks can be a sibling startup task rather than an edit to this one: both read the final descriptor set, so neither depends on contribution order or on the other.

This story depends on the abstractions-contract story. It does not depend on the fan-in step: the guard audits and activates registrations regardless of whether anything consumes them.

## Acceptance Criteria

- A Core-owned `IServiceCollection` extension registers the startup guard and captures the live service collection, and the frontend calls it exactly once, unconditionally, from `WebApplicationBuilderExtensions.AddServices`, following the call-site shape `AddJwtAuthentication` already uses.
- The guard is an `IDmsStartupTask` (`src/dms/core/EdFi.DataManagementService.Core/Startup/IDmsStartupTask.cs:21`) whose `Order` falls in 200-299, so it runs inside an executed `RunByOrderRangeAsync` window and after `LoadAndBuildEffectiveSchemaTask`, whose effective ApiSchema the `AppliesTo` warning reads. The plugin spine's task takes a higher `Order` in the same band, so this guard's criteria stay independent of whether that task exists.
- The `Order` declaration carries a comment recording that `IDmsStartupTask`'s own "Recommended ranges" comment labels 200-299 "Schema processing", which this guard is not, and that the label is a recommendation enforced by nothing. This keeps the value from being silently "corrected" later.
- The guard is proven to have actually executed during a real host startup rather than merely being registered, asserted on an observable effect after a `WebApplicationFactory<Program>` boot. The test must fail if the `Order` is moved outside an executed window, confirmed once by moving it. Without this, a task registered outside those windows is never run and never complained about, and every fail-loud guarantee in this epic evaporates without a single error.
- A registration that is not transient aborts startup, proven by a test registering a singleton `ICustomResourceValidator` and asserting the guard takes the fatal startup path rather than serving any request. Because the guard is a post-container startup task, that failure surfaces through `StartupPhaseExecutor`'s fatal path and `IStartupProcessExit.Exit`, not from the host build. The test replaces `IStartupProcessExit` with a non-exiting double, following the two precedents already in the repository: `Tests.Integration/Doubles/NonExitingStartupProcessExit.cs` and `RecordingStartupProcessExit` in `StartupPhaseExecutorTests.cs:18`. Without that substitution the production registration (`Infrastructure/WebApplicationBuilderExtensions.cs:75`) calls `Environment.Exit` and terminates the test runner.
- A descriptor carrying an `ImplementationInstance` aborts startup. This one cannot fail independently of the lifetime criterion above, since `ServiceDescriptor` only produces an `ImplementationInstance` descriptor at `Singleton` lifetime; it is listed so the audit's intent is explicit, not because it adds coverage.
- A descriptor carrying an `ImplementationFactory` aborts startup, proven with `services.Add(ServiceDescriptor.Transient<ICustomResourceValidator>(_ => sharedInstance))`. That descriptor reports a transient lifetime while handing every request the same object, so a lifetime-only audit passes it; this criterion is what forces the descriptor-shape half of the audit. Use `Add` and not `TryAddEnumerable` here, since `TryAddEnumerable` throws `ArgumentException` for a factory descriptor whose implementation type is indistinguishable from the service type and the fixture would fail before reaching the audit.
- A transient, implementation-type-based descriptor is accepted.
- A validator with an unsatisfiable constructor dependency aborts startup rather than failing the first matching write, proven the same way: the guard's activation probe takes the fatal startup path, with `IStartupProcessExit` substituted, before any request is served.
- A validator registered **after** Core's guard-registering extension is still audited and still activated, which is the position a plugin-contributed registration will be in. This is what proves the guard reads the final descriptor set post-container rather than whatever had been registered at its own call site, and an implementation that audits inline at the extension's call site must fail it.
- The guard runs and does not fail when no validator is registered at all, so a deployment that has adopted nothing still boots.
- The guard logs each registered validator's `AppliesTo` entries through `LoggingSanitizer.SanitizeForLogging`, since those strings originate in implementer code.
- An `AppliesTo` entry matching no resource in the effective ApiSchema produces a prominent startup warning and not a failure, proven with a fixture validator naming a nonexistent resource. It warns rather than fails because an entry can legitimately target an extension resource absent from the current deployment.
- SUPERSEDED by the scope change above. The `Plugins` configuration section belongs to the plugin spine's host-integration story; this story still adds no configuration of its own.
- `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit` passes.

## Tasks

1. Add the Core-owned composition extension beside `DmsCoreServiceExtensions`, modeled on `AddJwtAuthentication`'s shape and call-site pattern. It registers the startup guard and captures the live `IServiceCollection` so the guard can read final descriptors.
2. Call that extension exactly once, unconditionally, from `WebApplicationBuilderExtensions.AddServices`. Custom-validator composition is datastore-independent, so it is not routed through the per-datastore branches.
3. Implement the guard as an `IDmsStartupTask` performing three checks in one pass: audit every `ICustomResourceValidator` descriptor for transient lifetime and implementation-type-based shape; resolve the full `IEnumerable<ICustomResourceValidator>` once from a throwaway scope and discard the instances; and log each validator's `AppliesTo`, warning on entries matching no resource in the effective ApiSchema.
4. Pin the guard's `Order` in the 200s and add the comment recording the band-label mismatch.
5. Write the startup-abort tests in `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit`, registering fixture validators through the test host's `ConfigureServices` seam, which stands in for the plugin hook the spine adds later. That seam reaches the guard because the guard is a post-container startup task. Substitute `IStartupProcessExit` with a non-exiting double in every one of these tests, following the existing precedents, and assert on what the double recorded; the production implementation calls `Environment.Exit`.
6. Write the ordering-independence test by registering a fixture validator after the Core extension has run, and confirm once that an inline-audit implementation fails it.
7. Confirm once, by temporarily moving the `Order` outside an executed window, that the guard-executed test fails.
