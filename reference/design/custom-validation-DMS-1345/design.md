# Custom Validation Design

## Status

Draft.
This document is the design output of spike `DMS-1346` under epic `DMS-1345`; the ticket drafts beside it are filed only after it is reviewed and approved (see [README.md](./README.md) "Filing Gate").

This document describes the **custom validation extension point** for the Data Management Service.
The feature lets a district or vendor enforce its own business rules on documents as they are written, with the rules living in their own versioned assembly rather than in DMS core source: a public abstractions contract the implementer compiles against, registered into DMS's composition at build time, and invoked by a Core-authored fan-in step on the POST and PUT write paths.

**Delta 2026-08-27: delivery is a plugin, not compiled-in.**
The separate design stream this document deferred to ran as spike DMS-1462 and reversed the delivery decision: a validator reaches DMS as a plugin directory loaded by the host at startup, named in `Plugins:Allowed`, with its registration code invoked from an `EdFiApiPlugin.ContributeServices` hook rather than from DMS's composition root (`reference/design/plugins-DMS-1462/design.md`, "## Divergence from the Custom Validation Epic").
The prediction under [Rejected Alternatives](#rejected-alternatives) held: the contract, the fan-in step, the failure surfacing, and the startup guard transfer unchanged, and only the composition seam and the two documents that described it change.
This document is not rewritten.
Where it says "compiled-in", "composition root", or "no configuration section", the spine document is authoritative, and the three affected ticket drafts beside this file carry the change in place.
Compiling a validator into a DMS build remains possible and is the in-repo fixture route; it is no longer the documented one.

- [README.md](./README.md) - epic overview and ticket index
- [01-add-custom-validator-abstractions-contract.md](./01-add-custom-validator-abstractions-contract.md) - the contract types
- [02-add-custom-validator-fan-in-pipeline-step.md](./02-add-custom-validator-fan-in-pipeline-step.md) - the fan-in step and failure surfacing
- [03-add-custom-validator-composition-seam-and-startup-guard.md](./03-add-custom-validator-composition-seam-and-startup-guard.md) - the composition seam and startup guard
- [04-document-custom-validator-implementer-guide.md](./04-document-custom-validator-implementer-guide.md) - the implementer guide
- [05-prove-custom-validation-end-to-end.md](./05-prove-custom-validation-end-to-end.md) - end-to-end proof and scenario grounding
- Jira: [DMS-1346](https://edfi.atlassian.net/browse/DMS-1346) (spike authoring this document)
- Epic: [DMS-1345](https://edfi.atlassian.net/browse/DMS-1345)
- Downstream consumer: [DMS-1414](https://edfi.atlassian.net/browse/DMS-1414) UniqueId Validation, documentation-shaped and explicitly dependent on this design

**Citation convention.**
Paths beginning `EdFi.Ods.` are Ed-Fi ODS/API, relative to `Application/` in the Ed-Fi-ODS repository at commit `90c75ffed3fc2bc0dafa14a2600b3a0d050f82e9` (committed 2026-06-22).
That commit is the head of merged pull request [#1350](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/pull/1350), whose `DMS-1226` branch was deleted on merge, so it is not reachable as a branch on the public remote.
Fetch it with `git fetch origin refs/pull/1350/head` to reproduce any ODS citation below.
All other paths are DMS and are project-relative: an unprefixed path is relative to `src/dms/core/EdFi.DataManagementService.Core/`; a `Core.External/`, `Core.Tests.Unit/`, `Backend/`, `Backend.Ddl/`, or `Backend.External/` prefix names the sibling project; `Program.cs`, `AspNetCoreFrontend.cs`, `Infrastructure/`, and `Modules/` are relative to `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/`; and build scripts, workflows, and `docs/` are relative to the repository root.
A bare file name repeats a file cited in full earlier in this document.

---

## Table of Contents

- [Custom Validation Design](#custom-validation-design)
  - [Status](#status)
  - [Table of Contents](#table-of-contents)
  - [Goals and Non-Goals](#goals-and-non-goals)
    - [Goals](#goals)
    - [Non-Goals](#non-goals)
  - [Problem Statement](#problem-statement)
  - [Design](#design)
    - [Decision: a public contract composed into the host](#decision-a-public-contract-composed-into-the-host)
    - [Validator Contract](#validator-contract)
    - [Resource Applicability](#resource-applicability)
    - [Per-Invocation Inputs](#per-invocation-inputs)
    - [Registration and Composition](#registration-and-composition)
    - [Lifetime and Resolution](#lifetime-and-resolution)
    - [Pipeline Placement](#pipeline-placement)
    - [Startup Failure Semantics](#startup-failure-semantics)
  - [Failure Surfacing](#failure-surfacing)
    - [Response Shape](#response-shape)
    - [Accumulation Semantics](#accumulation-semantics)
    - [Exceptions and Non-400 Outcomes](#exceptions-and-non-400-outcomes)
  - [Verb Coverage](#verb-coverage)
  - [Security Posture](#security-posture)
  - [Versioning and Compatibility](#versioning-and-compatibility)
  - [ODS/API Precedent](#odsapi-precedent)
  - [Rejected Alternatives](#rejected-alternatives)
  - [Driving Scenarios](#driving-scenarios)
  - [Out of Scope](#out-of-scope)
    - [Deferred Follow-On Work](#deferred-follow-on-work)
  - [Testing Strategy](#testing-strategy)
  - [Level of Effort](#level-of-effort)
  - [Cross-References](#cross-references)

---

## Goals and Non-Goals

### Goals

1. **Validation logic lives outside DMS core source.** A district or vendor writes its rules in its own assembly, against a published package, versioned and tested on its own cadence. DMS source carries the seam, not the rules.
2. **Document-oriented and async contract.** DMS has no generated typed resource classes, and one of the three driving scenarios is I/O-bound, so the contract takes a `JsonNode` and returns a `Task`. Both are deliberate forks from the ODS signature.
3. **Compatibility breaks surface at build time.** Because the validator is compiled into the deployment, an incompatible contract change fails the implementer's own build rather than a production startup.
4. **Fail loud at startup.** A validator that is registered wrongly, or cannot be constructed, terminates the process rather than failing its first matching write.
5. **Response parity with core validation.** A custom validator's 400 reuses DMS's existing failure factories and `detail` literals rather than inventing its own, so its body is indistinguishable from the core 400 that takes the same arm. An `OnPath` failure matches a core schema-validation 400 (`ForDataValidation`). An `OnResource` failure matches the `ForBadRequest` 400 that `Middleware/ParseBodyMiddleware.cs:22` and `Middleware/ValidateMatchingDocumentUuidsMiddleware.cs:35` already emit, and has no core schema-validation counterpart at all, since `DocumentValidator` always returns an empty `errors` array (`Validation/DocumentValidator.cs:94`) and so never reaches the `ForBadRequest` arm. The two factories differ in `type` and `title`, not only `detail` (`Response/FailureResponse.cs:83-84`, `:99-100`), so the parity claim is per-arm and not blanket.
6. **Applicability declared as data.** A validator names the resources it applies to, so an I/O-capable validator is never invoked for a write it has nothing to say about.
7. **No new validation logic in Core.** Core gains a seam and an orchestration step, not rules.

### Non-Goals

- Runtime delivery of a validator assembly that was not part of the build. Deferred to a future plugin-delivery spike (see [Rejected Alternatives](#rejected-alternatives)).
- Validation on GET or DELETE (see [Verb Coverage](#verb-coverage)).
- Any store-read capability for validators; this version's contract exposes no access to stored data. This has a named consequence for DMS-1414, recorded under [Driving Scenarios](#driving-scenarios).
- Out-of-process validation (see [Rejected Alternatives](#rejected-alternatives)).
- A wildcard in `AppliesTo`.
- Implementing any of the three driving scenarios; they are requirement drivers, not deliverables.

---

## Problem Statement

DMS defines four write-path resource-document validator interfaces, all `internal` to `EdFi.DataManagementService.Core`, each with exactly one implementation.
(Other validator interfaces exist and are out of scope here, none of them validating a resource document on the write path: `IApiSchemaValidator` and `IProfileDataValidator` are also internal to Core, the document-cache validators are public in Core, and `IResourceKeyValidator` is declared in `Core.External/Backend/IResourceKeyValidator.cs:14` with its implementation registered in Core.)

| Interface | Declared at | Method |
| --- | --- | --- |
| `IDocumentValidator` | `Validation/DocumentValidator.cs:18` | `(string[], Dictionary<string, string[]>) Validate(RequestInfo requestInfo)` (`:23`) |
| `IEqualityConstraintValidator` | `Validation/EqualityConstraintValidator.cs:14` | `Dictionary<string, string[]> Validate(JsonNode? documentBody, IEnumerable<EqualityConstraint> equalityConstraints)` (`:22-25`) |
| `IDecimalValidator` | `Validation/DecimalValidator.cs:13` | `Dictionary<string, string[]> Validate(JsonNode documentBody, IEnumerable<DecimalValidationInfo> decimalValidationInfos)` (`:15-18`) |
| `IMatchingDocumentUuidsValidator` | `Validation/MatchingDocumentUuidsValidator.cs:10` | `bool Validate(RequestInfo requestInfo)` (`:16`) |

Three properties of that set constrain any extension point built next to it.
All four are `internal`, so nothing outside the Core assembly can implement or replace them.
All four take the parsed document or a piece of it, `RequestInfo` (which wraps `ParsedBody`) or a raw `JsonNode`, never a typed domain object, and none takes a resource discriminator: the calling middleware supplies the schema, constraints, or path context, and the validator itself is resource-agnostic.
Each is registered exactly once as the sole implementation of its interface (`DmsCoreServiceExtensions.cs:84-87`), with no `IEnumerable<T>` collection registration anywhere for any validator interface.

Wiring is by hand rather than by container.
Validator middleware objects are `new`-ed directly inside `ApiService` with the resolved validator passed in: three in the POST (Upsert) pipeline (`ApiService.cs:230`, `:231`, `:233`) and four in the PUT (Update) pipeline (`:323`, `:324`, `:326`, `:327`). Those line numbers are not contiguous because `ProfileWritePipelineMiddleware` sits between them (`:232`, `:325`) and is the one write-path middleware resolved from the container rather than `new`-ed.
`PipelineOrderingTests.cs` inspects the step-type sequence these factories build, but its existing assertions pin only pairwise positions of other steps, so reordering the four validator middlewares fails no test today.

So there is no seam at all.
Adding a rule today means writing it inside `EdFi.DataManagementService.Core`, registering it in `DmsCoreServiceExtensions.cs`, and inserting a middleware into `ApiService.cs`, which puts every implementer's business rules into the product's own source tree.
That is what epic DMS-1345 exists to change.

---

## Design

### Decision: a public contract composed into the host

Custom validation is delivered by a **public abstractions contract that an implementer compiles against and registers into DMS's composition**.

The contract is a versioned, publicly shipped set of types living outside `EdFi.DataManagementService.Core`, which a validator implements and which Core composes into a DI `IEnumerable<T>` collection.
A public surface is net-new under any delivery mechanism, since today's four interfaces are `internal`, and the collection fan-in it relies on is already a production pattern here: `IDmsStartupTask` is `public` (`Startup/IDmsStartupTask.cs:21`), registered through seven independent `AddSingleton` calls (`DmsCoreServiceExtensions.cs:68-74`), and consumed as `IEnumerable<IDmsStartupTask>` by `DmsStartupOrchestrator` (`Startup/DmsStartupOrchestrator.cs:16`).
Registration uses `TryAddEnumerable` rather than `AddTransient`, so a registration path invoked more than once contributes one entry rather than duplicates; the in-repo precedent for that specific call is `Backend.Ddl/CdcProviderSetupService.cs:1017`, `:1020`, not the `AddSingleton` fan-in above.

Core's own addition is one `internal` pipeline step that resolves the collection and invokes each entry.
`IPipelineStep` stays `internal` (`Pipeline/IPipelineStep.cs:21`, with `InternalsVisibleTo` granted only to test assemblies and the FakeItEasy proxy assembly at `EdFi.DataManagementService.Core.csproj:44-51`), so no out-of-Core assembly implements it; a Core-authored wrapper over the public collection keeps Core's responsibility at orchestration rather than validation.

**What this does and does not buy.**
The implementer's *rules* leave DMS source: they live in an assembly that implementer owns, versions, and tests, referencing a published package.
What does not leave is the *build*: the deployment composing DMS must reference that assembly and call its registration, so a deployment adopting custom validation is customizing its DMS build.
That is a real cost and it is stated rather than implied.
It buys two things in exchange, and the second is the reason this is a reasonable first version: every registration is visible in source control and reviewable, and an incompatible contract change fails the implementer's build instead of a production process start.

This is also what the reference implementation does for its own validators.
All three ODS production `IResourceValidator` implementations are compiled in and registered by modules shipping inside ODS assemblies, not delivered as plugins (see [ODS/API Precedent](#odsapi-precedent)).

### Validator Contract

Six public top-level types, and no more (`CustomValidationFailure` carries the two nested cases shown below):

```csharp
public sealed record ValidatedResource(string ProjectName, string ResourceName);

public sealed record ValidatedResourceInfo(
    string ProjectName,
    string ResourceName,
    string ResourceVersion
);

public sealed record ValidationScope(
    string? Tenant,
    IReadOnlyDictionary<string, string> RouteQualifiers
);

public enum CustomValidationOperation
{
    Upsert,
    Update
}

public interface ICustomResourceValidator
{
    IReadOnlyList<ValidatedResource> AppliesTo { get; }

    Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
        JsonNode document,
        ValidatedResourceInfo resource,
        CustomValidationOperation operation,
        ValidationScope scope,
        string traceId,
        CancellationToken cancellationToken
    );
}
```

**The contract declares its own inputs.**
An earlier revision reused `ProjectName`, `ResourceName`, `ResourceInfo`, `TraceId`, `RouteQualifierName`, and `RouteQualifierValue` from `EdFi.DataManagementService.Core.External` rather than duplicating them.
That is reversed: the contract lives in its own project, `EdFi.DataManagementService.CustomValidation`, and declares `ValidatedResource`, `ValidatedResourceInfo`, and `ValidationScope` itself, with plain `string` members rather than branded types.

The reason is that reuse and a small package are incompatible.
A published package cannot depend on an unpublished assembly, so a contract that names `Core.External`'s types can only ship by publishing `Core.External` whole, roughly 174 public types under semver so an implementer can name five, or by relocating those types out of it.
Declaring them here avoids both, and buys something on its own account: DMS's internal model and the published contract stop being the same thing, so adding a field to `ResourceInfo` for an internal reason is no longer a change to a contract other people compile against.

The cost is a projection step where DMS invokes a validator, building `ValidatedResourceInfo` from its own `ResourceInfo`.
That is the same boundary that already constructs `ValidationScope` from `FrontendRequest` rather than passing the live instance, so it adds a few lines to a mapping that has to happen anyway rather than introducing a new kind of work.
`Core` takes a project reference on the contract project; `Core.External` neither references it nor is referenced by it.

Everything `ValidateAsync` receives is either a BCL type or a type this contract declares, so nothing in it obliges a validator to compile against `Core.External`, `Backend.External`, or any other host assembly.

There is no registration-hook interface in the contract.
An earlier revision carried one so a dropped-in assembly could bind its own options during a folder scan; with compiled-in delivery the implementer writes an ordinary `IServiceCollection` extension method and calls `Configure<T>` directly, so a hook interface would add a type without adding a capability (see [Registration and Composition](#registration-and-composition)).

**Document-oriented, not typed.** DMS has no generated per-resource C# classes anywhere in `src/dms` and no `*.generated.cs` file in the tree, and all four existing validator interfaces already take the document or a piece of it.
A typed contract would mean inventing a class hierarchy DMS does not have.
**ODS divergence:** ODS validates a strongly typed generated request model (`EdFi.Ods.Api/Infrastructure/Pipelines/Steps/ValidateResourceModel.cs:19`, `:32`), and custom ODS validators recover the resource type from `@object.GetType().BaseType` (`EdFi.Ods.Features/UniqueIdIntegration/Validation/UniqueIdNotChangedEntityValidator.cs:32-33`); that half of the ODS design cannot be ported.

**Async.** The scenarios include external identity lookups, which need network I/O inside a single validation call, and DMS's pipeline is already `async` throughout (`IPipelineStep.Execute` returns `Task`, `Pipeline/IPipelineStep.cs:21-24`).
**ODS divergence:** `IObjectValidator.ValidateObject` is synchronous end to end (`EdFi.Ods.Common/IObjectValidator.cs:14-27`), which forces every I/O-bound ODS validator to block a thread.

**The result type.** `CustomValidationFailure` is a closed hierarchy with exactly two constructible cases, matching the two buckets DMS's 400 body already carries:

```csharp
public abstract record CustomValidationFailure
{
    private CustomValidationFailure() { }

    // Closes the hierarchy: only a case declared in this assembly can satisfy this, so no
    // concrete case can be declared outside it. See **Sealing limit** below. Each case below
    // overrides it with an empty body.
    private protected abstract void EnsureClosed();

    public sealed record OnPath : CustomValidationFailure
    {
        public OnPath(string jsonPath, string message)
        {
            // throws ArgumentException unless jsonPath is "$."-prefixed and message is non-empty
            JsonPath = jsonPath;
            Message = message;
        }

        public string JsonPath { get; }

        public string Message { get; }
    }

    public sealed record OnResource : CustomValidationFailure
    {
        public OnResource(string message)
        {
            // throws ArgumentException unless message is non-empty
            Message = message;
        }

        public string Message { get; }
    }
}
```

`OnPath` carries a `"$."`-prefixed JSON path plus a message, following the convention `DocumentValidator` already emits (`Validation/DocumentValidator.cs:308`, `:315`).
`OnResource` carries a message with no path, for a failure about the document as a whole such as a cross-field or external-lookup rejection.

**A bare `"$."` is a valid `OnPath` value, not a degenerate one.**
It is DMS's own document-level `validationErrors` key: `DocumentValidator` initializes `propertyName` to `string.Empty` (`Validation/DocumentValidator.cs:226`) and emits `"$." + propertyName` (`:315`), so an empty `InstanceLocation` produces exactly `"$."`, and `Middleware/ParseBodyMiddleware.cs:37` ships that key in production.
So the two cases are not "has a path" versus "has none"; both can describe the whole document, and they differ in which bucket they land in.
`OnPath("$.", message)` produces a `validationErrors["$."]` entry and the `ForDataValidation` shape core schema validation already uses for a document-level failure; `OnResource(message)` produces an `errors` entry and the `ForBadRequest` shape.
An implementer wanting parity with core's document-level failures uses `OnPath("$.", ...)`.
Rejecting `"$."` would leave a document-level `validationErrors` entry inexpressible through this contract while DMS itself emits one.

The cases are non-positional so that the validating constructor is the only way to build one, and that validation is what stops a malformed path such as `""` or `"$"` from reaching the two-bucket rule.
A positional record can host the same validation in a property initializer, so this is a choice to keep the surface minimal - no `Deconstruct`, no positional pattern - rather than a limitation of positional records.

**Sealing limit, and how it was closed.** The private base constructor closes ordinary construction, but the compiler synthesizes a protected copy constructor on every unsealed record and forbids restricting *that constructor* (declaring it `private protected` fails with CS8878, probe-verified), so a private constructor alone does not close the hierarchy.
An earlier revision of this design concluded the hierarchy therefore could not be closed in-assembly at all.
That is wrong, and the implementation closes it: an abstract `private protected` member on the base record cannot be overridden from another assembly, so the first concrete external type in any derivation chain fails with CS0534.
An external *abstract* record chaining the copy constructor still compiles, which is harmless because it can never be instantiated.
Probe-verified in both directions, with a negative control.
The consumption point keeps its exhaustive switch over `OnPath` and `OnResource` regardless, now as defense in depth rather than as the primary guarantee.
A `null` return from `ValidateAsync` is treated the same way and throws, rather than being coerced to an empty list.
Coercing it would make a validator that mistakenly returns `null` indistinguishable from one that ran and passed, which is the silent-success outcome this design refuses everywhere else.
This closes the ODS gap where a member-less `ValidationResult` disappears from `validationErrors` entirely (`EdFi.Ods.Api/ExceptionHandling/ErrorTranslator.cs:54-60`).

### Resource Applicability

`AppliesTo` declares, as data, which resources a validator runs against.
The fan-in step skips `ValidateAsync` entirely when the current request's `(ResourceInfo.ProjectName, ResourceInfo.ResourceName)` pair is absent from the list, so an I/O-bound validator never pays for writes it has nothing to say about.
`AppliesTo` must therefore be cheap, synchronous, and free of I/O; it is read on every write request for every registered validator.

**Matching is exact and ordinal.** `ProjectName` and `ResourceName` are record structs wrapping strings with default equality, so a typo'd or wrong-cased entry never matches and its validator silently never runs.
The startup guard logs every registered validator's `AppliesTo` list and raises a prominent warning for an entry matching no resource in the effective ApiSchema, without failing startup, since an entry can legitimately target an extension resource absent from the current deployment.
Those strings originate in implementer code, so they are logged through the existing `LoggingSanitizer.SanitizeForLogging` convention (`Utilities/LoggingSanitizer.cs:25`) rather than interpolated raw.

**No wildcard.** A validator that must cover every resource enumerates every pair by hand, and the Data Standard 5.2 fixture carries 349 resource schemas, so "every resource" is not a list anyone maintains correctly.
A wildcard would put an I/O-capable validator on every write in the data model, which is the cost `AppliesTo` exists to avoid, and would silently absorb the typo case the exact-match rule is designed to surface.
Adding one later is additive to `ValidatedResource` and breaks no signature.

### Per-Invocation Inputs

`document` is the **profile-effective body**, not always `ParsedBody`.
On a write restricted by a writable profile, `RequestInfo.ParsedBody` intentionally remains the raw submitted body, and the repo's convention requires every validator running after profile-write shaping to validate the shaped body, so hidden submitted data is accepted and ignored rather than rejected (`Middleware/ProfileWriteValidationBody.cs:14-22`).
The fan-in step sits after `ProfileWritePipelineMiddleware`, so it sources the document from `ProfileWriteValidationBody.Effective(requestInfo)`.
Consequence: `InjectVersionMetadataToEdFiDocumentMiddleware` mutates `ParsedBody` only (`Middleware/InjectVersionMetadataToEdFiDocumentMiddleware.cs:20-22`), so validators see injected version metadata on non-profile writes and not on writable-profile writes; domain paths are unaffected either way.

`resourceInfo` is the `ResourceInfo` the pipeline already built (`Pipeline/RequestInfo.cs:83`), and `traceId` is the `TraceId` already carried at `RequestInfo.FrontendRequest.TraceId` (`Core.External/Frontend/FrontendRequest.cs:39`), the identifier every existing middleware logs against.

`operation` distinguishes the two write pipelines.
It cannot be typed as DMS's own `RequestMethod`, which is `internal` (`Model/RequestMethod.cs:8`), so the contract defines its own enum named after DMS's pipeline vocabulary (`CreateUpsertPipeline`/`UpsertHandler` for POST, `CreateUpdatePipeline`/`UpdateByIdHandler` for PUT) rather than after the HTTP verbs.

`scope` answers which slice of the deployment a write belongs to.
A registration is a property of a DMS deployment, not of a district, so every registered validator runs for every write the host serves; without `scope`, a rule could only mean "for this entire process", never "for this one district".
It carries two members because DMS partitions requests two independent ways: `Tenant` comes from `FrontendRequest.Tenant` (`Core.External/Frontend/FrontendRequest.cs:50`) and is null in every single-tenant deployment, while `RouteQualifiers` is gated on its own configuration (`AspNetCoreFrontend.cs:540-545`), which is why a single-tenant host can still route to several stores on a template documented as `"/{districtId}/{schoolYear}/data/{**dmsPath}"` (`Modules/CoreEndpointModule.cs:52`).
In DMS a district is normally a route qualifier rather than a tenant, so a scope carrying `Tenant` alone would be unusable in the common deployment shape.
`RouteQualifiers` is a defensive copy: the request declares it as a mutable `Dictionary<,>` that implements `IReadOnlyDictionary<,>`, so passing the instance through would let a validator downcast and mutate the live request.
Bundling both into a record rather than adding two parameters keeps a future partitioning dimension additive.
Data-store identity as such is not exposed: `scope` carries the inputs that select a store, not the selected store.

`cancellationToken` is the request-abort token.
An async contract built for I/O-bound validators must let an external call be cancelled when the client disconnects, and adding the parameter later would be a breaking interface change.
Core already has a request-abort token and this design reuses it rather than introducing a second one.
`RequestInfo.RequestCancellationToken` (`Pipeline/RequestInfo.cs:207`) is set from an optional `CancellationToken` parameter on the entry point, and `ApiService.Get` already passes one into the `RequestInfo` constructor (`ApiService.cs:567-578`); the token is consumed across Core for resilience contexts and outbound calls (`Handler/Utility.cs:185`, `Middleware/JwtAuthenticationMiddleware.cs:66`, `Middleware/ResolveMappingSetMiddleware.cs:99`).
That machinery arrived with [DMS-1315](https://edfi.atlassian.net/browse/DMS-1315) (`d1f92c067`, PR #1160) and is on `main` but not on this branch's merge base, so it is cited against `main`.

Only `IApiService.Get` accepts a token today; `Upsert` and `UpdateById` do not, so `RequestCancellationToken` is `default` on every write.
This design therefore extends the two write entry points with the same optional parameter `Get` carries, assigns it into `RequestInfo` the same way, and has the fan-in step hand the validator `requestInfo.RequestCancellationToken`.
Adding a token to `FrontendRequest` instead would create a second, parallel abort path for the same request, which is why that earlier shape is rejected.
An optional parameter added to an existing `IApiService` method is source-compatible for existing callers, which is the precedent `Get` set.

`CoreExceptionLoggingMiddleware` already rethrows a cancelled request's `OperationCanceledException` ahead of its catch-all (`Middleware/CoreExceptionLoggingMiddleware.cs:52-55`, guarded on `RequestCancellationToken.IsCancellationRequested`), so a validator's takes that same arm once writes carry a real token: the abort propagates rather than becoming a logged 500 for a client that has already disconnected.
That is inherited behaviour, not a new decision, and it is the reason this design no longer pins validator cancellation to the generic arm.

**Mutation policy.** A validator receives `document` read-only, but `JsonNode` is a mutable BCL type, so this is a contract rule the type system cannot enforce, the same position DMS takes for pipeline-step state generally (`Pipeline/IPipelineStep.cs:18`).
The fan-in step passes each validator its own `DeepClone()` of the profile-effective document, so a validator that ignores the rule can only mutate its own copy.
One shared clone would protect the request body but not the validators from each other, and since they run sequentially an earlier mutation would silently change what a later one sees, a defect that depends on registration order; no clone at all protects nothing and the mutation reaches the terminal handler and is persisted.
The cost is N in-memory tree copies per write, where N is the number of validators whose `AppliesTo` matched rather than the number registered.
If a deployment with many broadly scoped validators over large documents ever makes that measurable, a copy-on-write or read-only `JsonNode` wrapper is the escalation path.

### Registration and Composition

An implementer ships an assembly that references the published abstractions package, implements `ICustomResourceValidator`, and exposes one ordinary `IServiceCollection` extension method:

```csharp
public static IServiceCollection AddDistrictValidators(
    this IServiceCollection services,
    Action<ExternalIdentityOptions> configureIdentity)
{
    services.Configure(configureIdentity);

    services.TryAddEnumerable(
        ServiceDescriptor.Transient<ICustomResourceValidator, StudentIdentityValidator>());

    return services;
}
```

The sample takes an `Action<TOptions>` rather than an `IConfiguration` section deliberately, and the reason is the package's dependency set, which is empty.
`EdFi.Api.CustomValidation` declares no `PackageReference` at all, on purpose (`src/dms/core/EdFi.DataManagementService.CustomValidation/EdFi.DataManagementService.CustomValidation.csproj:21-35`), so **neither** form compiles against the package alone: `TryAddEnumerable` needs `Microsoft.Extensions.DependencyInjection.Abstractions`, the `Action<TOptions>` overload of `Configure` needs `Microsoft.Extensions.Options`, and the `GetSection` form additionally needs `Microsoft.Extensions.Options.ConfigurationExtensions`.
An implementer declares whichever of those they use from their own assembly, which is theirs to decide and which is exactly what `eng/verification/CustomValidationConsumer/CustomValidationConsumer.csproj:57-58` does; the contract does not oblige them to any of it and does not carry the dependency on their behalf.
The `Action<TOptions>` form is preferred in the sample because it needs one fewer package, not because the other is unreachable.

The deployment references that assembly and adds one call at DMS's composition root, `WebApplicationBuilderExtensions.AddServices` (`Infrastructure/WebApplicationBuilderExtensions.cs:32`), alongside the calls already there:

```csharp
webAppBuilder.Services
    .AddDmsDefaultConfiguration(...)
    .AddDistrictValidators(options =>
        webAppBuilder.Configuration.GetSection("ExternalIdentity").Bind(options));
```

That is the whole delivery mechanism.
Note where the configuration plumbing lives: the deployment's composition root is an ASP.NET Core host that already has the full shared framework, so reading a section there costs nothing, while the implementer's own assembly stays compilable against the abstractions package alone.
The implementer supplies their own options type and the deployment supplies its values, so **custom validation adds no DMS-owned configuration surface**: there is no section to document in `docs/CONFIGURATION.md` and no option that turns the feature on or off, because a deployment that registered no validators has none.

**Core's own contribution is the guard, not the registration.** Core ships an extension that registers the startup guard specified under [Startup Failure Semantics](#startup-failure-semantics), and the frontend calls it once, unconditionally, the way it already calls `AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)` (`DmsCoreServiceExtensions.cs:238-241`, called at `Infrastructure/WebApplicationBuilderExtensions.cs:243`).
Core is the right home for it because the collection being guarded is Core's own, and hosting it there keeps the guard's internals `internal` rather than forcing new public Core API purely to be callable from the frontend.

**Ordering must not matter, and that is a design constraint rather than a convention.** The implementer's registration call may sit before or after Core's guard call, and an implementer is exactly the party the guard exists to check, so a rule of the form "register the validators before the guard" would be broken by the party it protects and would fail silently.
Core's extension therefore captures the live collection (`services.AddSingleton<IServiceCollection>(services)`) and the guard reads the final descriptor set after the container is built, when every registration source has contributed by construction.
An earlier revision anchored this audit at "the last statement of the loader's registration extension", which worked only because a loader owned every registration; with the implementer owning registration, a post-container guard is what restores the guarantee.

### Lifetime and Resolution

**Transient only.** The four existing internal validators are registered `AddTransient` (`DmsCoreServiceExtensions.cs:84-87`), and `ICustomResourceValidator` implementations follow the same convention, in the collection-shaped `TryAddEnumerable` form.
Singleton registration is prohibited because a singleton that constructor-injects a scoped host service resolves it once and holds it captive for every later request.
No type in this contract is scoped, but a validator's constructor can take anything the host registers, so the defect stays reachable through an implementer's own choices.

**The rule is enforced by the startup guard, not documented.** The guard inspects every `ICustomResourceValidator` service descriptor and aborts startup on any that is not transient.
It checks descriptor shape as well as lifetime: `ServiceDescriptor.Transient<ICustomResourceValidator>(_ => capturedInstance)` reports a transient lifetime while handing every request the same object, so a descriptor carrying an `ImplementationInstance` or an `ImplementationFactory` for this service type aborts startup alongside a non-transient one.
This does cost an implementer something real, and the ban is a deliberate conservative call rather than a free one.
`ServiceDescriptor.Transient<ICustomResourceValidator, MyValidator>(sp => new MyValidator(...))` is a legitimate per-resolution factory that `TryAddEnumerable` accepts, and it is the ordinary way to pass a constructor argument that is not itself a service.
The guard rejects it anyway, because a factory descriptor records what a delegate returns, not whether the delegate constructs anything: `sp => capturedInstance` and `sp => new MyValidator(...)` are the same shape to the audit, and only the second is safe.
The supported route for a non-service constructor argument is therefore `Configure<TOptions>` plus constructor injection of `IOptions<TOptions>`, which the implementer guide states.
The guard is unconditional Core behavior, running whether or not any validator is registered, so a deployment with none still fails loudly the day one is added wrongly.

**Resolved per request, from the request's own scope.** DMS's existing validators are `new`-ed into pipelines exactly once, inside factory methods cached forever by `Lazy<PipelineProvider>` fields ("pipelines are built once since schema is now stable", `ApiService.cs:156-161`), which is safe only because none of them carries a scoped dependency.
The fan-in step instead resolves `IEnumerable<ICustomResourceValidator>` from `requestInfo.ScopedServiceProvider` (`Pipeline/RequestInfo.cs:193-201`) inside its own `Execute`, the pattern `UpsertHandler` (`Handler/UpsertHandler.cs:32-34`) and `QueryRequestHandler` (`Handler/QueryRequestHandler.cs:31-32`) already use for scoped repositories.
Two consequences follow.
A validator must not accumulate mutable instance state across invocations, the same no-object-state convention `Pipeline/IPipelineStep.cs:18` already states.
And every registered validator's constructor and dependency graph run on every POST and PUT, matching or not, because `AppliesTo` gates `ValidateAsync` and not construction, so constructors must stay trivial.

**Constructor injection surface.** Whatever an implementer can construct through the host's `IServiceCollection`, a validator's constructor can take.
`services.AddHttpClient()` is already called in Core (`DmsCoreServiceExtensions.cs:248`), registering `IHttpClientFactory`, which an existing Core registration already resolves alongside `IOptions<JwtAuthenticationOptions>` to retrieve OIDC metadata over HTTP (`DmsCoreServiceExtensions.cs:251-265`) and which is safe to inject into a component of any lifetime.

### Pipeline Placement

The step is an internal `IPipelineStep`, `CustomResourceValidationMiddleware`, `new`-ed into the Upsert and Update pipelines the way the four core validators already are.
Its slot is **immediately after `ProvideAuthorizationFiltersMiddleware` and immediately before the terminal handler**: between `:243` and `UpsertHandler` at `:244` in `CreateUpsertPipeline`, and between `:337` and `UpdateByIdHandler` at `:338` in `CreateUpdatePipeline`.
It is therefore the last gate before persistence.

Three constraints fix that position.

1. **After core validation.** Each core validator calls `next()` only when it found no failure and otherwise assigns `requestInfo.FrontendResponse` and returns (`Middleware/ValidateDocumentMiddleware.cs:30-33`, `:34-69`), and `PipelineProvider.RunInternal` advances only through that callback (`Pipeline/PipelineProvider.cs:23-29`), so core schema failure is not a case the step must check for.
This does not make the document a validator receives schema-valid.
Schema validation runs on `ParsedBody` before profile shaping (`ApiService.cs:230`, `:232`), and a writable profile can strip schema-required members while `deferCreatabilityViolations: true` (`Middleware/ProfileWritePipelineMiddleware.cs:150`) lets the request continue, so a validator reading the profile-effective body can see a document missing members the schema requires.
2. **After `BuildResourceInfoMiddleware`** (`:234-237` in Upsert, `:328-331` in Update), which populates `RequestInfo.ResourceInfo` (`Middleware/BuildResourceInfoMiddleware.cs:24-31`), needed by both `AppliesTo` filtering and the `resourceInfo` parameter.
3. **After `ResourceActionAuthorizationMiddleware`** (`:242` in Upsert, `:336` in Update), so a client denied by resource/action authorization gets its authorization response before any validator runs. Custom validators can perform outbound HTTP; running them earlier would let denied requests trigger that I/O and let a validator's 400 displace the authorization response.

Running last also means `RequestInfo.AuthorizationStrategyEvaluators` is already populated (`Middleware/ProvideAuthorizationFiltersMiddleware.cs:51`), which this version does not consume but a later store-reading version would need.

**Authorization boundary.** The invariant is narrower than "fully authorized".
Namespace and relationship authorization is not a pipeline decision: both are evaluated inside the backend write, downstream of the terminal handler and therefore downstream of every custom validator (`Backend/RelationalDocumentStoreRepository.cs:1437`, `:1476`, `:1819`).
A request that will ultimately be refused on those grounds still runs every applicable validator first, can trigger validator I/O it will not benefit from, and can receive a validator 400 in place of the 403 it would otherwise have received.
Ordering the two the other way is not available, because those checks run in the same backend session as the write they guard, which is exactly where custom validation must not run.

**Observability.** The request path needs its own logging, because Scenario 1 puts third-party network I/O on the write path with no timeout the contract controls, and per-validator timing is then the only diagnostic a deployment has.
The step logs, against the request's `TraceId` the way every existing middleware does (for example `Middleware/ValidateDocumentMiddleware.cs:23-26`): at Debug, each applicable validator's type name and elapsed time; and when a validator returns failures, one record naming that validator and its failure count.
Validator type names originate in implementer code, so they are logged through `LoggingSanitizer.SanitizeForLogging` (`Utilities/LoggingSanitizer.cs:25`), the same treatment `AppliesTo` entries get at startup.
Failure *messages* are not logged, because they can quote submitted document values.

**Ordering is pinned mechanically.** `PipelineOrderingTests.cs` reflects into `ApiService`'s private factory methods and inspects the concrete step-type sequence each builds (`Core.Tests.Unit/Pipeline/PipelineOrderingTests.cs:38-59`); the `Given_The_Routed_Resource_Pipelines` fixture already exercises both write pipelines this way (for example `:474-491`), and shipping this step requires an assertion of the same shape.

### Startup Failure Semantics

A validator that is registered wrongly, or that cannot be constructed, aborts DMS startup and terminates the process.
**No custom validator ever serves a request unless it passed the startup guard.**

This is a deliberate refusal of the ODS precedent for extension registration, where a module that throws while being constructed or handed to `RegisterModule` is caught, logged, and skipped (`EdFi.Ods.Api/Startup/OdsStartupBase.cs:379-387`), degrading a broken extension to silently absent validation.
The catch covers only that much: Autofac defers a module's own `Load` body to container build, outside the cited `try`, so a module that throws while performing its registrations is not swallowed but takes the process down.
It is the swallowing half that this design refuses.

Fatal conditions: a descriptor that is not transient; a descriptor carrying an `ImplementationFactory`; and a registered validator the container cannot construct.
An `ImplementationInstance` descriptor is fatal too, but it is not an independent condition: `ServiceDescriptor` only produces one at `Singleton` lifetime, so the lifetime check already catches every such descriptor and a test for it cannot fail while the lifetime test passes.

**The operator-facing message will name the wrong phase.**
An `Order` in the 200s runs inside the `[0, 299]` window that `Program.cs:163-169` labels `InitializeApiSchemas`, whose failure text is "API schema initialization failed. DMS cannot start with invalid schemas." (`:167`).
A deployment whose schemas are fine but whose validator registration is wrong therefore gets a fatal message about schemas.
The failing task is still named in `DmsStartupOrchestrator`'s own `Critical` record (`Startup/DmsStartupOrchestrator.cs:93-97`), so the real cause is recoverable from the log, and this design accepts the mislabel rather than adding a startup phase for one guard.
The implementer guide and the guard's own log record carry the accurate wording.

**One guard, running after the container is built.** It is an `IDmsStartupTask`, so it runs through the frontend's existing fatal-startup path: `DmsStartupOrchestrator` catches any non-cancellation exception, logs it at `Critical`, and rethrows it wrapped (`Startup/DmsStartupOrchestrator.cs:93-97`), and `StartupPhaseExecutor.RunFatalAsync` (`Infrastructure/StartupPhaseExecutor.cs:88-116`) logs a fatal failure and calls `IStartupProcessExit.Exit`, implemented in production as `Environment.Exit` (`:13-19`).
The guard does three things: audits the captured descriptor set for lifetime and shape; resolves the full `IEnumerable<ICustomResourceValidator>` once from a throwaway scope and discards the instances; and logs each validator's `AppliesTo`, warning on entries matching no resource in the effective ApiSchema.

The activation half closes a failure MS DI would otherwise defer: constructors resolve lazily, `ValidateOnBuild` is not enabled outside Development, and the fan-in step resolves per request, so an unsatisfiable constructor dependency would otherwise surface as a 500 on the first write, matching or not.

**Registering an `IDmsStartupTask` is not by itself enough to make it run.**
The AspNetCore frontend never calls `RunAllAsync` (`Startup/DmsStartupOrchestrator.cs:30`, which has no production caller); it calls `RunByOrderRangeAsync` over `[0, 299]`, `[300, 399]`, and `[400, 499]` (`Program.cs:315-317`, `:329-331`, `:341-343`, bounded by `DmsStartupTaskOrderRanges`, `Startup/IDmsStartupTask.cs:10-14`), so a task whose `Order` falls outside those windows is registered, never executed, and never complained about.
The guard's `Order` must therefore sit inside an executed window and above `LoadAndBuildEffectiveSchemaTask`'s `Order => 100` (`Startup/LoadAndBuildEffectiveSchemaTask.cs:34`), because the `AppliesTo` warning reads the effective ApiSchema that task builds.
Any value in 101-299 satisfies both; the 200s is this design's preference, keeping the guard visibly after schema loading.
DMS's existing registration-validation guards sit lower (`Order => 50` and `Order => 55`, `Startup/ValidateDatabaseFingerprintReaderRegistrationTask.cs:19`, `Startup/ValidateResourceKeyRowReaderRegistrationTask.cs:19`), and this guard deliberately does not join them: both run before `LoadAndBuildEffectiveSchemaTask` (`Order => 100`), whose effective ApiSchema the `AppliesTo` warning reads.
That preference conflicts with the doc comment labelling 200-299 "Schema processing" (`Startup/IDmsStartupTask.cs:27`), which is introduced as a recommendation (`:25`) and enforced by nothing; the implementation records the mismatch at the `Order` declaration and proves the guard actually executed rather than merely being registered.

**What the guard guarantees** is constructibility: a dependency the container cannot supply, or a constructor that throws when resolved outside a request.
That is narrower than "no validator depends on per-request state", and the implementer guide inherits the distinction rather than overclaiming.
For DMS's current registrations the narrow guarantee happens to suffice in both directions: a constructor reading `IDataStoreSelection` throws in an empty scope (`Configuration/DataStoreSelection.cs:36-42`), while a validator reading it inside `ValidateAsync` finds it populated by `ResolveDataStoreMiddleware` (`Middleware/ResolveDataStoreMiddleware.cs:175`) far upstream of the fan-in slot.

---

## Failure Surfacing

### Response Shape

DMS already builds its schema-validation 400 from the same two buckets `CustomValidationFailure` is designed around: `FailureResponse.CreateBaseJsonObject` (`Response/FailureResponse.cs:49-73`) serializes a `Dictionary<string, string[]>` onto `validationErrors` and a `string[]` onto `errors`.

Custom failures fold into those collections rather than a parallel shape.
Every `OnPath` failure is appended to `validationErrors` under its own `JsonPath`, grouping multiple messages per path the way `DocumentValidator.ValidationErrorsFrom` already groups schema violations (`:254-266`); every `OnResource` failure is appended to `errors`.
The step then calls the same two factories `ValidateDocumentMiddleware` calls, under the same selection rule (`errors.Length > 0` picks `ForBadRequest`, otherwise `ForDataValidation`), passing that middleware's exact `detail` literals: `"The request could not be processed. See 'errors' for details."` and `"Data validation failed. See 'validationErrors' for details."` (`Middleware/ValidateDocumentMiddleware.cs:38-54`).

The `detail` value is specified rather than left to the implementer because it is serialized into every 400 body as a client-visible member (`Response/FailureResponse.cs:49-61`), so an implementer choosing their own string is exactly what would make custom-validator 400s distinguishable.
With it fixed, a client cannot tell from the body whether a 400 on a given arm came from core validation or a custom validator.
(This is the write path's 400 shape, not literally every DMS 400: a known query failure returns 400 with an empty body, `Handler/QueryRequestHandler.cs:80`.)

### Accumulation Semantics

Every applicable validator that returns normally contributes its failures; there is no early exit among custom validators on returned failures.
A throw is a different path: sequential execution stops there, later validators do not run, and the request ends with the exception outcome below.

Validators are invoked **sequentially**, not concurrently.
Two validators in the same request scope that inject the same scoped host service share one instance of it, and .NET DI scoped services carry no thread-safety guarantee, so concurrency is not licensed here even though each validator holds its own document copy.

When every validator returns normally the merged failure **set** is order-independent, since merging is list accumulation with no observable intermediate state.
Order **within** each bucket is not: `errors` and each `validationErrors[path]` are JSON arrays, and messages appear in invocation order, which follows DI registration order, which follows the order the implementer's registrations ran.
Message order is therefore explicitly not part of this contract and a client must not depend on it.

Custom validators do not run at all once any earlier step has failed, because the pipeline never reaches them.
That set is larger than the four core validators: `ExtractDocumentInfoMiddleware` and both array-uniqueness validators (`ApiService.cs:238-240`, `:332-334`) also gate `next()`, as do the route, body, profile, and authorization steps upstream.

### Exceptions and Non-400 Outcomes

A validator returning a non-empty list always produces a 400, since neither constructible case maps to any other status.
A validator that throws is not a validation result at all.

`CoreExceptionLoggingMiddleware`, the second step of every DMS pipeline (`ApiService.cs:176-186`, itself at `:181`), wraps every later step in one catch chain (`Middleware/CoreExceptionLoggingMiddleware.cs:25-69`) with three special-cased arms ahead of the catch-all. Two are Core authorization internals rather than contract surface: `AuthorizationException` (public, but declared in the internal Core assembly at `Security/AuthorizationException.cs:11`, so not referenceable from the abstractions package) becomes a 403, and `CustomViewAuthorizationValidationException` becomes a ProblemDetails 500.
The third is cancellation: an `OperationCanceledException` on a request whose `RequestCancellationToken` is cancelled is rethrown rather than converted (`:52-55`), which is the arm a validator's own cancellation takes (see [Per-Invocation Inputs](#per-invocation-inputs)).
Everything else becomes a 500: the catch-all arm (`Middleware/CoreExceptionLoggingMiddleware.cs:56-68`) calls `FailureResponse.ForServerErrorMessageBody` (`Response/FailureResponse.cs:454`) and captures the exception onto `requestInfo.CaughtException`, so the outer logging middleware attaches it to the structured failure log.

**ODS divergence:** ODS maintains a hand-curated rethrow allowlist so specific exception types reach dedicated translations (`EdFi.Ods.Common/Extensions/ValidatorExtensions.cs:36-50`; of the three, only `ProfileMethodUsageException` carries a non-400 status, 405).
DMS needs no equivalent: an abstractions-only validator cannot reference the 403-mapped type, so every unhandled validator exception becomes a loud, logged 500.
A custom validator cannot produce a silent success, only a 400 it explicitly returned or a 500 it did not catch.

---

## Verb Coverage

Custom validation runs on **POST and PUT only**.

GET is untouched: `CreateGetByIdPipeline` and `CreateQueryPipeline` (`ApiService.cs:250-268`, `:270-293`) contain none of the four core validators and gain no custom-validator step.

DELETE is out of scope, and this is not a DMS deferral.
The ODS reference implementation has no delete-time resource validation either: `ValidateResourceModel<,,,>` registers only into the Upsert pipeline (`EdFi.Ods.Api/Infrastructure/Pipelines/Factories/PipelineStepsProviders.cs:59`), and the Delete pipeline provider returns only a delete step (`:70-79`).
Extending custom validation to DELETE is a new capability beyond what either codebase does today.

---

## Security Posture

A validator is ordinary in-process code in the same process and trust boundary as DMS Core, with no isolation: it can do anything the DMS process can do.

Compiled-in delivery makes that boundary an ordinary software-supply-chain boundary rather than a new one.
The validator assembly is referenced by the deployment's build, its registration is a line in source control, and both are reviewable by whoever reviews that deployment's code.
There is no runtime filesystem location that grants code execution, and therefore nothing new to lock down operationally.
This is a material advantage over runtime assembly loading, where write access to a scanned directory is equivalent to code execution inside the API process, and it is one reason the compiled path is the right first version.

---

## Versioning and Compatibility

The abstractions package is the complete compile-time surface a validator needs, versioned with ordinary semver.
Adding a member to `ICustomResourceValidator`, or changing `CustomValidationFailure`, is a breaking major-version bump requiring every validator to be recompiled.

**Breaks surface at build time, provided the validator is rebuilt.** Because the validator is compiled into the same build as the host, the implementer's own compiler is what reports an incompatible contract change, and NuGet resolves one version of the abstractions assembly for the whole output.
That guarantee is conditional on the deployment building the validator against the host's abstractions version, from source or by rebuilding its package.
A deployment that instead references a *prebuilt* validator assembly compiled against an older major gets no compiler error: NuGet unifies to the host's version without failing the restore, and the mismatch becomes a runtime failure instead.
This design does not leave that silent, but the guard's reach over it is partial, and the two halves of the breaking-change definition above fall on opposite sides of the line.

The guard resolves every registered `ICustomResourceValidator` once from a throwaway scope and reads each one's `AppliesTo` to log it (see [Startup Failure Semantics](#startup-failure-semantics)), so a break the CLR resolves at type load, or one reached by executing the constructor or the `AppliesTo` getter, aborts startup before the process serves traffic.
Adding a member to `ICustomResourceValidator` is in that half: the stale validator no longer implements the interface, and the CLR throws `TypeLoadException` naming the unimplemented member as soon as the type loads.

What the guard does not reach is a member reference reachable only from the `ValidateAsync` body, which is not resolved until `ValidateAsync` is JIT-compiled, and that happens on the first write whose resource matches that validator's `AppliesTo`.
Changing `CustomValidationFailure` is in this half: a validator whose `ValidateAsync` constructs an `OnPath` against a constructor that the host's newer contract no longer declares constructs cleanly, reports its `AppliesTo` cleanly, and then throws `MissingMethodException` on that first matching write.
Both halves are probe-verified with a two-assembly swap: one validator compiled against an older contract, run unrecompiled against hosts carrying each kind of change.
The `MissingMethodException` takes the catch-all arm and becomes the logged 500 of [Exceptions and Non-400 Outcomes](#exceptions-and-non-400-outcomes), so that write fails loudly and persists nothing, but the deployment starts clean and stays up until a matching write arrives.

The outcome therefore degrades from a build failure to a loud failure, never to a silent one: a startup abort for the load-time half, and a logged 500 on the first matching write for the rest.
Forcing `ValidateAsync` to JIT at startup would close the remainder; this version accepts it instead, since the escaping case is already loud and depends on a deployment shipping a validator binary it never rebuilt, which is the thing the implementer guide tells it not to do.
Compiled-in delivery still removes the type-identity and assembly-probing failures the runtime-loading alternative would add.

**Target framework.** A validator is compiled into the host's build and loaded into its runtime, so its target framework must be compatible with the host's; the contract project targets `net10.0` (`CustomValidation/EdFi.DataManagementService.CustomValidation.csproj:3`).
A DMS runtime major-version bump therefore changes the set of buildable validators without any package version changing, which the implementer guide states alongside the semver rule.

**Hosting the contract in `Core.External` was reversed.**
This design originally put the contract in `Core.External` and accepted publishing that assembly, on the reasoning that a separate project was avoidable complexity.
Two things overturned it.
Packaging `Core.External` places its whole public seam under semver, 88 `.cs` files carrying `IApiService`, `FrontendRequest`, the backend result types, and the security contracts, so that an implementer can name five validator types.
And publishing it also publishes its dependency closure: it declares five `PackageReference`s, none carrying `PrivateAssets`, of which `Microsoft.CodeAnalysis` is unused and alone accounts for 16 of the project's 26 lock-file entries, including every Roslyn package, the six `System.Composition.*` packages, and `Humanizer.Core`.
Left as it was, a district implementing this contract would have taken a compile-and-publish dependency on the Roslyn compiler platform.

The contract therefore lives in its own project, `EdFi.DataManagementService.CustomValidation`, packaged as `EdFi.Api.CustomValidation`, declaring its own inputs and no `PackageReference` at all.
`Core` references it; `Core.External` does not, and is unchanged by this epic.
Retiring `Core.External`'s unused references remains worth doing, but it is now ordinary cleanup belonging to whoever wants it rather than prerequisite work for this contract.

**Supported versus possible.** A validator is in-process code that can constructor-inject anything the host registers, and `IDocumentStoreRepository` and `IQueryHandler` are public interfaces registered scoped in the host (`Backend.External/RepositoryContracts.cs:13`, `:39`) whose assembly the deployment already references, so an implementer can reach services this contract does not offer.
Such a validator works and nothing stops it.
What it forfeits is the compatibility promise, since those assemblies carry no semver commitment to validator authors.
The implementer guide states this line rather than implying an enforcement that does not exist.

---

## ODS/API Precedent

The `IResourceValidator` framing comes from the driving discussion, not from the epic.
Epic DMS-1345 carries no description; its entire text is the summary "Implement Custom Validation extension point in DMS. Required for custom business rules."
The Confluence record of the workgroup discussion is where the reference implementation is named: "Custom validation scenarios were discussed for extending `IResourceValidator`", closing with "No additional custom validation patterns were identified beyond extending `IResourceValidator`" ([page 2588835841](https://edfi.atlassian.net/wiki/spaces/GOV/pages/2588835841), "July 2026 - Ed-Fi Data Management Service Workgroup").
The reference implementation was therefore surveyed to decide what ports and what does not.

**The current interface, recorded rather than only cited.** DMS-1346 asks for the definition of the current interface as part of this work, so it is reproduced here in full (`EdFi.Ods.Common/IObjectValidator.cs:14-27`):

```csharp
public interface IObjectValidator
{
    ICollection<ValidationResult> ValidateObject(object @object);
}

public interface IResourceValidator : IObjectValidator { }
```

That is the entire surface.
`ValidationResult` is `System.ComponentModel.DataAnnotations.ValidationResult`, whose `MemberNames` carries the member paths ODS converts into `validationErrors` keys and whose `ErrorMessage` carries the message (`EdFi.Ods.Api/ExceptionHandling/ErrorTranslator.cs:54-60`).
`IResourceValidator` declares no members of its own; its doc comment describes it as "a strongly typed interface specifically for validating incoming API resources".
Its whole mechanical role is to be the service type a validator is registered as and the collection the write pipeline resolves: `ValidateResourceModel` constructor-injects `IEnumerable<IResourceValidator>` (`ValidateResourceModel.cs:23`, `:25`) and calls the inherited method through an extension typed on `IEnumerable<IObjectValidator>` (`EdFi.Ods.Common/Extensions/ValidatorExtensions.cs:19`), so the narrower interface is what separates resource validation from any other use of `IObjectValidator`.
Each property of that shape is dispositioned in the table below.

**ODS's own validators are compiled in, not delivered as plugins.** All three production `IResourceValidator` implementations ship inside ODS assemblies and are registered by Autofac modules that ship with them: `DataAnnotationsResourceValidator` unconditionally in `EdFi.Ods.Api` (`EdFi.Ods.Api/Container/Modules/ApplicationModule.cs:305-307`), and `UniqueIdNotChangedEntityValidator` plus `EnsureUniqueIdAlreadyExistsEntityValidator` in `EdFi.Ods.Features` (`EdFi.Ods.Features/Container/Modules/UniqueIdIntegrationModule.cs:38-44`), behind a `ConditionalModule` gated on `ApiFeature.UniqueIdValidation` (`:19`, `:24`).
There are zero test-only implementations; the test-side FluentValidation samples reach a different contract, `IExplicitObjectValidator`.
ODS's plugin folder is the mechanism by which a *third party* gets an assembly into the process, after which its Autofac module is picked up by an AppDomain-wide scan (`EdFi.Ods.Api/Helpers/TypeHelper.cs:42-51`, registered at `EdFi.Ods.Api/Startup/OdsStartupBase.cs:355-372`) that also picks up compiled-in modules.
Both delivery paths therefore feed one registration seam, and nothing scans for `IResourceValidator` itself.

| ODS property | Evidence | Disposition for DMS |
| --- | --- | --- |
| `IResourceValidator : IObjectValidator`, a zero-member marker used as a DI discriminator | `EdFi.Ods.Common/IObjectValidator.cs:14-27` | **Adopted in spirit.** DMS uses a distinct public interface resolved as a collection, for the same reason. |
| Compiled-in registration through a module shipping with the validator | `ApplicationModule.cs:305-307`, `UniqueIdIntegrationModule.cs:38-44` | **Adopted.** DMS's equivalent is an implementer-authored `IServiceCollection` extension called at the composition root. |
| Typed input: validators receive the generated request model | `ValidateResourceModel.cs:19`, `:32`; `EdFi.Ods.Standard/Standard/6.1.0/Requests/Requests.generated.cs:20866` | **Refused.** DMS has no generated typed resource classes; the contract is document-oriented. |
| Synchronous `ValidateObject` | `IObjectValidator.cs:14-27` | **Refused.** DMS's pipeline is async and two driving scenarios are I/O-bound. |
| POST/PUT only; Delete pipeline has no validation step | `PipelineStepsProviders.cs:59`, `:70-79` | **Adopted.** See [Verb Coverage](#verb-coverage). |
| Accumulate-then-400 with a `validationErrors` path map | `ValidatorExtensions.cs:19-60`; `ErrorTranslator.cs:49-71` | **Adopted.** See [Response Shape](#response-shape). |
| Plugin folder: probe in a throwaway context, check for `IPluginMarker`, then load for real | `EdFi.Ods.Api/Helpers/AssemblyLoaderHelper.cs:274-322` | **Deferred** to a future plugin-delivery spike. It is a delivery mechanism layered on the same registration seam, so deferring it costs this design nothing. |
| Feature-flag gating of a validator set (`ConditionalModule`) | `UniqueIdIntegrationModule.cs:19`, `:24` | **Not adopted.** In DMS a validator is active if it is registered, and `AppliesTo` is the only narrowing. An implementer wanting a toggle reads their own configuration in their own registration extension. |
| Module ordering: `ICustomModule` last, then `Override`-prefixed, then the rest | `TypeHelper.cs:22-51` | **Not needed.** Ordering keys on the module itself, not its origin. DMS has no module system and no last-wins requirement. |

Three ODS behaviors are refused explicitly, each because it converts a defect into silence.

1. **Member-less results vanish.** `ErrorTranslator.cs:54-60` adds model errors only inside `foreach (string memberName in validationResult.MemberNames)`, so a result with no member names never reaches `modelState` and never appears in `validationErrors`, dropping its message while still causing a 400. Refused by `CustomValidationFailure`'s two constructible cases plus the exhaustive consumption switch.
2. **Module construction and registration-call failures are swallowed.** `OdsStartupBase.cs:379-387` catches, logs, and continues; failures inside a module's deferred `Load` body escape that catch and are not swallowed. Refused by [Startup Failure Semantics](#startup-failure-semantics).
3. **`ValidationState` is dead code.** `SetValid()`/`SetInvalid()` write only when `ValidationState.Current` is non-null (`EdFi.Ods.Api/Validation/ObjectValidatorBase.cs:22-30`), `Current` reads log4net's `ThreadContext.Properties` (`EdFi.Ods.Common/ValidationState.cs:14-18`), and no production path assigns it: the controllers build a separate local instance for the `PutContext` (`EdFi.Ods.Api/Controllers/DataManagementControllerBase.cs:298`, `:366`) that never reaches the thread context. Even populated it would be last-writer-wins, and the response decision (`:312`, `:399`) consults the aggregated list instead. Refused by having no ambient state at all: `ValidateAsync` takes everything as parameters and returns everything as a value.

---

## Rejected Alternatives

| Alternative | Disposition | Reason |
| --- | --- | --- |
| Public abstractions contract plus DI collection fan-in, composed at build time | **Adopted** | The only way to give DMS a versioned public contract at all; the fan-in mechanic is already production behavior for `IDmsStartupTask`; matches how ODS registers its own validators |
| Runtime loading from a dropped-in assembly | **Deferred** | A delivery mechanism over the same seam, with its own trust-boundary, packaging, and assembly-identity design. It would need its own design stream; not filed, since no deployment has asked for it |
| Out-of-process validation (webhook or sidecar) | Rejected | No write-path HTTP infrastructure exists to extend; every requirement would be net-new; no validated need for process isolation |

**Runtime assembly loading is deferred, not rejected.** It is the mechanism ODS uses to let a third party add a validator without touching the product's build, and it is the natural next step for any deployment that cannot customize its DMS build.
It is deferred because it is separable and expensive: it introduces a trust boundary where filesystem write access equals code execution, a packaging contract for what may sit in the scanned directory, assembly-identity and dependency-resolution rules for the load context, and its own startup failure taxonomy, none of which the contract or the pipeline seam depends on.
Nothing in this design forecloses it: the follow-up spike inherits `ICustomResourceValidator`, the fan-in step, the failure surfacing, and the startup guard unchanged, and adds only a discovery-and-registration path that feeds the same collection.
That is the same layering ODS has, where the plugin folder gets an assembly into the process and the ordinary registration scan does the rest.

**Out-of-process** would require a versioned wire contract, a synchronous call on the write path with an explicit latency budget, a fail-open versus fail-closed policy, service-to-service authentication independent of the client-facing OAuth flow (the document body can carry PII), a new deployable process per environment, and its own drift-detection machinery against the ApiSchema version DMS is serving.
DMS's write path has zero outbound HTTP today, a grep across `Core/Middleware` and `Core/Handler` for `HttpClient` returning no matches, so this does not extend a seam, it creates one.
Its one genuine advantage is process isolation, and no requirement on record demonstrates a need for isolation strong enough to justify the cost.
Rejected for the write path as currently scoped, not deferred.

---

## Driving Scenarios

Three scenarios from the driving discussion motivated this epic, recorded verbatim on [Confluence page 2588835841](https://edfi.atlassian.net/wiki/spaces/GOV/pages/2588835841) as validating students against external identity systems, making optional collections such as Race and Language required, and preventing Special Education and Title I program associations from being posted as generic program associations.
Each is a requirement driver only: none is implemented here, and none is proposed as a validator DMS Core ships.
They exist to show the contract can express them through an implementer's own assembly.
Resource names and field paths were verified against the Data Standard 5.2 ApiSchema fixture (`src/dms/backend/Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json`); the per-scenario grounding is carried in [05-prove-custom-validation-end-to-end.md](./05-prove-custom-validation-end-to-end.md).

| Scenario | `AppliesTo` | Reads | Injects | Failure case | Contract fit |
| --- | --- | --- | --- | --- | --- |
| 1. Validate students against an external identity system | `Student` | `$.studentUniqueId` | `IHttpClientFactory` + own `IOptions<T>` | `OnResource` → `errors` | Async signature plus constructor injection; no extension needed |
| 2. Make optional collections required for specific implementations | `StudentEducationOrganizationAssociation` | `$.races`, `$.languages` | none | `OnPath` → `validationErrors` | Document parameter plus `ValidationScope` for per-district scoping; no extension needed |
| 3. Keep Special Education and Title I programs off the generic program association | `StudentProgramAssociation` | `$.programReference.programTypeDescriptor` | none | `OnPath` → `validationErrors` | Document parameter alone, parsing the descriptor URI fragment; no extension needed |

Three consequences are design-level rather than per-scenario.

**I/O on the write path.** Scenario 1's external call sits directly on the write path, because the fan-in step awaits every applicable validator before the terminal handler.
The contract provides no timeout, retry, or fail-open policy for a validator's own outbound call; whatever timeout the validator's own client carries is the only one that exists, and a throw from it becomes a 500 rather than a skipped validator.
An implementer adopting this shape accepts that every write to that resource depends on the external system's availability, with no partial-degradation path.

**Descriptor content is out of reach.** Scenario 3 is expressible against descriptor **URIs**, because a descriptor URI's fragment is by construction the descriptor's `codeValue`, so the rule is fragment parsing.
A district that loads its own `ProgramTypeDescriptor` values and wants a rule against descriptor **content** would need the descriptor document itself, which this version's contract does not provide.

**A known downstream consumer is only partly served, the no-store-read non-goal being the larger of two reasons.**
DMS-1414 asks for "a documented way to achieve ODS/API UniqueId validation behaviour in DMS via the custom validation extension point", and DMS-1415 carries the acceptance criterion "Confirmed with the custom validation work (DMS-1345 / DMS-1346) that `IResourceValidator` can express UniqueId validation".
Both tickets carry the `needs-description` label, and DMS-1415 is an explicit placeholder whose own description says it is "provisional and drawn from the two sources above, not from refinement" and is to be replaced once the scope is agreed.
What follows therefore answers the criterion as currently written, and should be revisited if refinement changes it.
ODS implements that behaviour as two validators, and they land differently here.
`EnsureUniqueIdAlreadyExistsEntityValidator` (`EdFi.Ods.Features/UniqueIdIntegration/Validation/EnsureUniqueIdAlreadyExistsEntityValidator.cs:22`) checks that an upstream pipeline step already resolved the submitted UniqueId to an internal id; the *kind* of rule is expressible here, though a DMS equivalent is shaped differently because there is no such upstream step and no typed model.
`UniqueIdNotChangedEntityValidator` (`EdFi.Ods.Features/UniqueIdIntegration/Validation/UniqueIdNotChangedEntityValidator.cs:16`) injects `IPersonUniqueIdToIdCache` and calls `GetUniqueId(objType.Name, objectWithIdentifier.Id)` to fetch the **persisted** UniqueId before comparing it to the submitted one (`:32-45`).
That rule needs previously-stored state, and this version's contract supplies none, so "a person's UniqueId cannot be modified" is **not expressible on the supported surface**.
An implementer can still inject their own cache, exactly as ODS does, but two things stop that from closing the gap: nothing in DMS supplies a supported way to populate that cache from DMS's own data, and the cache lookup needs the persisted document's identifier, which `ValidateAsync` does not receive (see [Deferred Follow-On Work](#deferred-follow-on-work) for why the body does not supply it either).
The honest answer to DMS-1415 is therefore that the not-changed rule is out of scope for this version rather than achievable through documentation alone.
Whether to close that gap by adding a store-read capability is recorded under [Deferred Follow-On Work](#deferred-follow-on-work) and should be decided with DMS-1414 rather than assumed.

---

## Out of Scope

- ~~Runtime loading of a validator assembly that was not part of the build.~~ **No longer out of scope.** Spike DMS-1462 designed it and made it the delivery path; see the Status delta at the top of this document and `reference/design/plugins-DMS-1462/`.
- Validation on GET or DELETE.
- Store reads from a validator. An earlier revision carried a `ICustomValidationStoreReader` facade exposing `Task<JsonNode?> GetDescriptorByUri(...)`; it is cut. The backend's own contracts cannot serve it directly, since `IQueryRequest`/`IGetRequest` demand a mapping set, query elements, authorization evaluators, and paging inputs that a validator does not hold, their concrete implementations are internal to Core, and neither is keyed by descriptor URI. A facade would also have to collapse `QueryResult`'s seven cases (`Core.External/Backend/QueryResult.cs:25-65`) into `JsonNode?`, giving an infrastructure failure and a genuine not-found the same representation, so a database outage would surface as a validator-authored 400 saying the descriptor does not exist. Designing that error contract deserves its own evidence.
- Implementing any driving scenario.
- Publishing the contract package. See **Publishing deferral** immediately below.

### Publishing deferral

The abstractions-contract story builds the package but does not publish it.
`EdFi.Api.CustomValidation` is packed from `EdFi.DataManagementService.CustomValidation`, its contents are asserted, and a scratch consumer is compiled against the produced nupkg, all in the per-PR lane.
No prerelease pack, SBOM, provenance, or publish job exists, and no release promote step, so nothing reaches a feed.
This design originally had the contract story publish as well, and every passage below that says so is superseded by this one.

The line is drawn at what can be undone.
Packing produces an artifact a build throws away, so it costs nothing to be wrong about, and building it now is what proves the csproj metadata, the dependency closure, and the consumer story actually work.
Publishing burns a package id and a version permanently, and nothing can consume the contract until the fan-in step lands, so a package published now is one no host can run.

Publishing is taken up at the end of the epic, when there is a host that runs validators and an implementer guide to ship inside the package.
Note also that this epic's delivery mechanism is compiled-in: a deployment adds one call at its own composition root, which means it already builds from source and could reference the contract project directly.
The package becomes load-bearing under the deferred runtime-loading model, where a validator is built independently of the host and needs a versioned contract to compile against.

### Deferred Follow-On Work

| Deferred item | Reason |
| --- | --- |
| ~~Runtime assembly loading (the plugin spike)~~ | **Delivered as the decision, not deferred.** Spike DMS-1462 designed it and it became the documented delivery path; see the Status delta at the top of this document. It inherited this contract, the fan-in step, the failure surfacing, and the startup guard unchanged, exactly as predicted here. |
| Store-read capability for validators | Additive to the contract surface (a validator obtains it by constructor injection, not as a parameter), so adding it later breaks no signature. Needs its own error-contract design. It is one of two things the ODS UniqueId not-changed rule needs, not the only one: that rule keys on the persisted document's identifier (`EdFi.Ods.Features/UniqueIdIntegration/Validation/UniqueIdNotChangedEntityValidator.cs:39`), and this version's `ValidateAsync` exposes neither a `DocumentUuid` nor the route, so DMS-1414 needs a document-identity capability alongside store access. The document body does not stand in for it: an `Upsert` body carries no `id` by construction (`Middleware/RejectResourceIdentifierMiddleware.cs:35-45`), and although an `Update` body must carry one matching the route id (`Validation/MatchingDocumentUuidsValidator.cs:23-27`), a writable profile can strip it from the profile-effective body the validator receives, since an `IncludeOnly` member filter keeps only the members the profile names (`Profile/WritableRequestShaper.cs:661-670`). |
| A wildcard in `AppliesTo` | Additive to `ValidatedResource`. Only worth adding against a real requirement for breadth, which is arguably a different extension point. |
| ~~Distinct handling for `OperationCanceledException`~~ | **Closed, not deferred.** Core already rethrows a cancelled request's `OperationCanceledException` ahead of its catch-all (`Middleware/CoreExceptionLoggingMiddleware.cs:52-55`), so a validator's inherits that outcome and there is nothing for the fan-in ticket to decide. |
| A copy-on-write or read-only `JsonNode` wrapper | Replaces the per-validator deep clone if that cost ever becomes measurable on a bulk-load path. |

---

## Testing Strategy

**Contract unit tests** (abstractions package):

- `OnPath` throws for a null or empty path, for any path that is not `"$."`-prefixed (including bare `"$"`), and for an empty message; `OnResource` throws for an empty message. A bare `"$."` is accepted and asserted as accepted, since it is DMS's document-level `validationErrors` key.
- Within the abstractions assembly's own public construction surface, `OnResource` is the only way to express a path-less failure. Asserted reflectively; the expected constructor set is exactly the synthesized copy constructor, since CS8878 forbids restricting it. Reaching that constructor from another assembly does not yield a usable third case, per **Sealing limit**.
- A scratch library referencing only the published package compiles a validator and the registration extension exactly as [Registration and Composition](#registration-and-composition) documents it, including the `Action<TOptions>` overload of `Configure`, proving the package's dependency set is sufficient for the documented usage.

**Fan-in step unit tests:**

- A matching validator runs; its `OnPath` failure lands in `validationErrors` keyed by that path and its `OnResource` failure lands in `errors`.
- A non-matching validator is never invoked, asserted on call count.
- Two validators returning `OnPath` for the **same** path both appear under that key, matching the per-path grouping `ValidationErrorsFrom` performs. Without this, a `validationErrors[path] = messages` implementation passes everything else while silently dropping a message.
- Two applicable validators accumulate rather than short-circuit.
- Validators run sequentially: each of two fakes records entry and exit, and the first must exit before the second is entered, so a `Task.WhenAll` implementation fails.
- Each validator receives its own document instance: the first mutates what it was given, the second sees the unmutated document, and the request body is unchanged.
- `operation` is `Upsert` on POST and `Update` on PUT, both asserted, so a swapped mapping fails.
- `resourceInfo`, `traceId`, and the cancellation token a validator receives are the request's own, asserted on captured arguments.
- `ValidationScope` carries the tenant (including the null single-tenant case) and the route qualifiers, asserted **separately**: a single-tenant routed deployment has a null tenant and non-empty qualifiers, so an implementation populating only `Tenant` would pass a tenant-only test while leaving that shape unable to scope a rule.
- The scope's qualifier dictionary is a defensive copy, proven by downcasting to `Dictionary<,>`, mutating, and asserting the request is unchanged. Asserting that `IReadOnlyDictionary<,>` has no `Add` proves nothing, since a pass-through would pass it.
- A validator that throws produces a 500 through the existing catch-all, not a 400 and not an escaping exception.
- A concrete `CustomValidationFailure` subtype other than the two cases cannot be declared outside the abstractions assembly at all (CS0534), so this is a compile-time guarantee rather than a runtime one, and there is no external third case left to test. The remaining gap is a third case added *inside* `EdFi.DataManagementService.CustomValidation` without updating the consumption switch. No test in `Core.Tests.Unit` can construct that, the assembly being a separate one with no `InternalsVisibleTo` grant, so the fan-in story chooses between a throwing default arm that no test can reach and relying on the compiler to flag the unhandled case at the switch site. That choice belongs to the fan-in story; this design does not pre-empt it, and it must not be closed by adding a friend grant, which would reopen the hierarchy (see **Sealing limit**).
- A custom-validator 400 is byte-identical to the core 400 taking the same arm in `detail`, `type`, `title`, and `status`: `detail` against the literals `ValidateDocumentMiddleware` passes on each arm (`Middleware/ValidateDocumentMiddleware.cs:41`, `:50`), and the other three against the arm's factory (`Response/FailureResponse.cs:83-85` for `ForDataValidation`, `:99-101` for `ForBadRequest`). Asserting `detail` alone would not catch a hand-built body, since `CreateBaseJsonObject` serializes `type` and `title` into every 400 and the two factories differ in both. The comparison is against those literals rather than against a produced core schema-validation 400, since `DocumentValidator` always returns an empty `errors` array (`Validation/DocumentValidator.cs:94`) and so never produces an `errors`-arm 400.
- The document is the profile-shaped body under a writable profile and `ParsedBody` otherwise.
- A validator returning failures produces a log record naming that validator against the request's `TraceId`, and that record contains no failure message text.

**Pipeline ordering tests:** the step sits immediately after `ProvideAuthorizationFiltersMiddleware` and immediately before the terminal handler in both write pipelines; and neither read pipeline nor the delete pipeline contains it. The delete pipeline is asserted alongside the read pipelines because validation on DELETE is a stated non-goal, and without that assertion an implementation that added the step there would satisfy every other criterion.

**Frontend tests:** `AspNetCoreFrontend.FromRequest` assigns `HttpContext.RequestAborted`, confirmed once by removing the assignment and observing the failure.
Adding the property without assigning it would leave every validator on `CancellationToken.None` forever with every other test green.

**Startup guard tests** (booted host, no database):

- A non-transient descriptor, a descriptor carrying an `ImplementationInstance`, and a descriptor carrying an `ImplementationFactory` each abort startup. The factory case is what forces the descriptor-shape half of the audit; a lifetime-only audit passes it while every request shares one validator instance.
- A validator with an unsatisfiable constructor dependency aborts startup rather than failing the first matching write.
- The guard runs with **no** validators registered and does not fail, so a deployment that has adopted nothing still boots.
- Registration order does not matter: a validator registered *after* Core's guard-registering extension is still audited and still activated. This is what proves the guard reads the final descriptor set post-container rather than whatever had been registered at its own call site.
- `AppliesTo` entries matching no resource in the effective ApiSchema produce a warning and not a failure, proven with a fixture validator naming a nonexistent resource.
- The guard is proven to have **executed**, not merely registered, and the test fails if its `Order` moves outside an executed window.

**End-to-end**, through the documented composition seam: a POST to a matching resource with a failing document returns the documented 400; the same POST with a passing document succeeds; a PUT behaves identically; and a non-matching resource is unaffected.

---

## Level of Effort

Medium.
Four implementation surfaces, in dependency order. They do not map one-to-one onto the five ticket drafts: surface 3 covers drafts 03 and 04, which are separate stories because the guide depends on all three preceding ones. See README.md "## Ticket Drafts" for the authoritative work-item list.

1. **Abstractions contract** - six public top-level types in their own project, `EdFi.DataManagementService.CustomValidation`, plus the pack-and-verify path in the per-PR lane. The prerelease pack/SBOM/provenance/push jobs and the release promote step are deferred out of this surface; see **Publishing deferral**.
2. **Fan-in pipeline step** - the `internal` step, the `validationErrors`/`errors` merge, per-request resolution from the request scope, the observability records, and extending the two write entry points with the optional `CancellationToken` parameter `Get` already carries so `RequestInfo.RequestCancellationToken` is populated on writes.
3. **Composition seam, startup guard, and implementer guide** - the Core extension that registers the guard and captures the collection, the unconditional startup guard itself, the documented registration call at the composition root, and the guide packaged with the abstractions package.
4. **End-to-end proof** - a fixture validator registered through the documented seam and driven over real HTTP.

No changes to the four existing validator interfaces.
No new pipeline abstraction: `IPipelineStep` stays `internal`.
No DMS-owned configuration surface, and therefore no `docs/CONFIGURATION.md` section and no appsettings key.
No assembly loading, no load-context management, and no new trust boundary.
No new response shape.

---

## Cross-References

- [DMS-1346](https://edfi.atlassian.net/browse/DMS-1346) - the design spike authoring this document.
- [DMS-1345](https://edfi.atlassian.net/browse/DMS-1345) - the parent epic.
- [DMS-1414](https://edfi.atlassian.net/browse/DMS-1414) / [DMS-1415](https://edfi.atlassian.net/browse/DMS-1415) - UniqueId Validation, a documentation-shaped consumer that depends on this design and is partly blocked by the store-read non-goal (see [Driving Scenarios](#driving-scenarios)).
- [README.md](./README.md) - epic overview, out-of-scope table, and ticket index in dependency order.
- [01-add-custom-validator-abstractions-contract.md](./01-add-custom-validator-abstractions-contract.md) through [05-prove-custom-validation-end-to-end.md](./05-prove-custom-validation-end-to-end.md) - the implementation stories realizing this design.
- Driving discussion: [Confluence page 2588835841](https://edfi.atlassian.net/wiki/spaces/GOV/pages/2588835841), "July 2026 - Ed-Fi Data Management Service Workgroup" (space `GOV`).
