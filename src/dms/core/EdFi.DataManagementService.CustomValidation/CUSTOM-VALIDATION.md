# Ed-Fi API Custom Validation Abstractions

This package defines `ICustomResourceValidator`, the contract a district or vendor implements to add
custom resource validation to the Ed-Fi Data Management Service, and this document is the
implementer guide for it.

> **A validator is registered from a plugin.**
>
> The Data Management Service's write pipeline resolves registered `ICustomResourceValidator`
> instances and invokes those whose `AppliesTo` matches the current request's resource, and a
> validator reaches that pipeline through a plugin's `ContributeServices` hook. How a plugin is
> built, published, packaged, and delivered into a host is
> [PLUGINS.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/plugins/EdFi.Api.Plugins/PLUGINS.md).
> Registering one is not inert at startup: a startup guard audits every registration and aborts
> startup if a validator is registered in a shape DMS would not resolve, so a registration mistake
> fails the process rather than passing silently.
> The release notes of a Data Management Service release state which contract versions that release
> carries, which is what tells you the version of this package to build against for a given host.
>
> **Links out of this readme point at the current documentation on `main`**, not at the
> documentation for the package version you resolved.

## What is here

- `ICustomResourceValidator` - the validator contract, declaring the resources it applies to and a
  single `ValidateAsync` entry point.
- `CustomValidationFailure` - a closed hierarchy of exactly two failure cases, `OnPath` for a
  failure tied to a JSON path and `OnResource` for a failure about the document as a whole.
- `ValidatedResource` - the applicability declaration, naming a project and a resource.
- `ValidatedResourceInfo`, `ValidationScope`, and `CustomValidationOperation` - the per-invocation
  inputs.

Each type carries its rules in its XML documentation, which ships with this package, so an IDE shows
them at the point of use. Where this guide and that documentation overlap, they are written to agree.

## These types are the contract's own

The inputs a validator receives are declared by this package rather than borrowed from the Data
Management Service's internal model, and they are deliberately plain: strings rather than
branded types.

That is what keeps this package small and its dependency list empty, and it means the Data
Management Service can change its internal model without that being a breaking change to anything
compiled against this contract. `ValidatedResourceInfo` in particular is a projection of what the
service knows about a resource, carrying the fields a validator has a use for and nothing else.

`CustomValidationFailure` is a closed hierarchy with exactly two constructible cases, so handling
both is handling all of them. The compiler does not know that, and a two-arm switch expression still
reports CS8509, so a consumer building with warnings as errors adds a discard arm.

## A validator, end to end

Three files. Every one of them is compiled, and run, by this repository's own verification lane
before this document ships, and a check holds each block below to the file it came from. So these
are samples that have been compiled rather than samples that look right.

The rule is a small one on purpose: a student's unique id must carry a district-configured prefix.
Nothing in it reaches outside the process, which is what lets it be a sample that actually runs.
What it does not show is a validator doing real I/O, because a faked call is the one thing a
compiling sample cannot prove. [The cost of I/O on the write path](#the-cost-of-io-on-the-write-path)
states those rules instead.

### The options type

Ordinary implementer code, naming no Ed-Fi type and needing no package. It is here because both
files below depend on it.

<!-- embed: eng/verification/CustomValidatorPluginConsumer/StudentIdentityOptions.cs#options -->
```csharp
namespace Acme.Dms.StudentIdentity;

public sealed class StudentIdentityOptions
{
    /// <summary>
    /// The prefix every student unique id in this deployment is required to carry.
    /// </summary>
    public string RequiredPrefix { get; set; } = string.Empty;
}
```

### The validator

<!-- embed: eng/verification/CustomValidatorPluginConsumer/StudentIdentityValidator.cs#validator -->
```csharp
using System.Text.Json.Nodes;
using EdFi.DataManagementService.CustomValidation;
using Microsoft.Extensions.Options;

namespace Acme.Dms.StudentIdentity;

public sealed class StudentIdentityValidator : ICustomResourceValidator
{
    private readonly StudentIdentityOptions _options;

    // Trivial by obligation, not by taste: DMS resolves every registered validator on every write
    // request before it reads any AppliesTo, so this constructor runs for writes to resources this
    // validator has nothing to say about. Reading IOptions<T>.Value is the whole of it.
    public StudentIdentityValidator(IOptions<StudentIdentityOptions> options)
    {
        _options = options.Value;
    }

    // Built once and handed back by reference. This getter is read on every write request for
    // every registered validator, before any filtering, so it must stay this cheap.
    public IReadOnlyList<ValidatedResource> AppliesTo { get; } = [new ValidatedResource("Ed-Fi", "Student")];

    public Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
        JsonNode document,
        ValidatedResourceInfo resource,
        CustomValidationOperation operation,
        ValidationScope scope,
        string traceId,
        CancellationToken cancellationToken
    )
    {
        // Observed and allowed to propagate. A validator must not catch this and report a
        // validation failure instead: on a request the client has already aborted, DMS rethrows it
        // rather than turning it into a 500, and swallowing it would answer 400 for a request that
        // no longer has a caller.
        cancellationToken.ThrowIfCancellationRequested();

        // The document is read, never written. The parameter is a JsonNode and nothing in the type
        // system stops a validator mutating it; not doing so is a contract rule.
        //
        // Read as a JSON string rather than through GetValue<string>(), which throws when the
        // member holds a number. A validator cannot assume every value arrives coerced to its
        // schema type: a deployment that sets AppSettings:BypassTypeCoercion removes that step from
        // the pipeline. Anything that is not a JSON string is treated as nothing to check.
        string studentUniqueId =
            document["studentUniqueId"] is JsonValue submitted
            && submitted.TryGetValue(out string? submittedText)
                ? submittedText ?? string.Empty
                : string.Empty;

        // Nothing to check rather than a failure to report. A validator reads a document it did not
        // construct, so it defends against a member being absent instead of assuming its own rule's
        // input is there. Reporting a failure here would reject writes over the shape of the body
        // rather than over the rule.
        if (studentUniqueId.Length == 0)
        {
            return NoFailures;
        }

        if (studentUniqueId.StartsWith(_options.RequiredPrefix, StringComparison.Ordinal))
        {
            return NoFailures;
        }

        // The submitted value is deliberately not quoted back. A failure message reaches the 400
        // body, and keeping submitted data out of it is what lets a deployment log these messages
        // if it chooses to.
        return Task.FromResult<IReadOnlyList<CustomValidationFailure>>([
            new CustomValidationFailure.OnPath(
                "$.studentUniqueId",
                $"A {resource.ResourceName} unique id must begin with "
                    + $"'{_options.RequiredPrefix}' in this deployment."
            ),
        ]);
    }

    // An empty list, never null. A null return is not a substitute for one and DMS treats it as a
    // hard failure.
    private static Task<IReadOnlyList<CustomValidationFailure>> NoFailures { get; } =
        Task.FromResult<IReadOnlyList<CustomValidationFailure>>([]);
}
```

The absent-value handling is this sample's choice, not a guarantee the contract makes: a validator
reads a document it did not construct, and defending against a member it needs is cheaper than
assuming. A different deployment might reasonably report a failure in the same place.

### The plugin that registers it

<!-- embed: eng/verification/CustomValidatorPluginConsumer/StudentIdentityPlugin.cs#plugin -->
```csharp
using EdFi.Api.Plugins;
using EdFi.DataManagementService.CustomValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Acme.Dms.StudentIdentity;

public sealed class StudentIdentityPlugin : EdFiApiPlugin
{
    // Must equal the name of the directory this plugin is published into and named by in
    // Plugins:Allowed. The host treats a mismatch as fatal.
    public override string Name => "Acme.Dms.StudentIdentity";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        // The Action<TOptions> overload, from Microsoft.Extensions.Options. Registered first, so
        // it supplies the deployment-independent default.
        services.Configure<StudentIdentityOptions>(options => options.RequiredPrefix = "S");

        // The section-binding overload, from Microsoft.Extensions.Options.ConfigurationExtensions.
        // Registered second, so a deployment that sets StudentIdentity:RequiredPrefix in its own
        // configuration overrides the default above; one that sets nothing keeps it. Both forms
        // register an IConfigureOptions<StudentIdentityOptions> and they run in registration
        // order.
        services.Configure<StudentIdentityOptions>(configuration.GetSection("StudentIdentity"));

        // The registration shape DMS's startup guard accepts: TryAddEnumerable, Transient,
        // unkeyed, and an implementation type rather than a shared instance or a factory delegate.
        // TryAddEnumerable is the form this contract requires because it adds to the collection
        // rather than replacing it: an earlier plugin's validator survives this call, and this one
        // survives a later plugin's. Running every registered validator is DMS's own doing, not
        // this helper's; its fan-in step resolves them all on each write and invokes the ones whose
        // AppliesTo matches.
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<ICustomResourceValidator, StudentIdentityValidator>()
        );
    }
}
```

### Which package each line needs

Neither contract package supplies the complete options and configuration machinery, so you declare
the rest yourself. The DI and configuration **abstractions** do arrive through `EdFi.Api.Plugins`,
because its own hook signature names `IServiceCollection` and `IConfiguration` and a contract cannot
decline a dependency its signatures require. What no contract supplies is the options binding, and
that is a property you inherit rather than a gap: the contracts oblige a consumer to nothing they did
not choose.

| What the samples name | Which package it comes from |
| --- | --- |
| `ICustomResourceValidator`, `CustomValidationFailure`, `ValidatedResource`, `ValidatedResourceInfo`, `ValidationScope`, `CustomValidationOperation` | `EdFi.Api.CustomValidation`, which declares no dependency of its own |
| `EdFiApiPlugin` | `EdFi.Api.Plugins` |
| `IServiceCollection`, `ServiceDescriptor`, `TryAddEnumerable` | `Microsoft.Extensions.DependencyInjection.Abstractions`, which `EdFi.Api.Plugins` also declares because its hook signature names `IServiceCollection` |
| `IConfiguration` and `GetSection` | `Microsoft.Extensions.Configuration.Abstractions`, declared by `EdFi.Api.Plugins` for the same reason |
| `IOptions<T>` and `Configure<T>(Action<T>)` | `Microsoft.Extensions.Options`, which you declare |
| `Configure<T>(IConfigurationSection)` | `Microsoft.Extensions.Options.ConfigurationExtensions`, which you declare |

The last row is the one that surprises people. `OptionsConfigurationServiceCollectionExtensions`
lives in `Microsoft.Extensions.Options.ConfigurationExtensions`, so without that package
`services.Configure<T>(configuration.GetSection("..."))` does not compile even though
`Microsoft.Extensions.Options` is present. It also brings
`Microsoft.Extensions.Configuration.Binder` transitively, which is what actually performs the
binding.

Declare exactly what your own code uses, and no more. That is what keeps your project's dependency
closure the one you chose. The verification fixture declares both Ed-Fi contracts and explicitly
references `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Options`,
and `Microsoft.Extensions.Options.ConfigurationExtensions`. It reaches
`Microsoft.Extensions.Configuration.Abstractions` transitively through `EdFi.Api.Plugins`
rather than naming it. Declaring it as well is also reasonable, and is what the plugin contract's own
sample project does, on the argument that code compiled against a signature should name the assembly
it compiles against.

### Getting it into a host

A deployment adds no line of code anywhere. It publishes your plugin into a directory under the
plugin root and names that directory in the allowlist:

```shell
dotnet publish --no-self-contained -o out/Acme.Dms.StudentIdentity
```

```text
Plugins__Directory=/app/plugins
Plugins__Allowed=Acme.Dms.StudentIdentity
```

`Allowed` ships empty, which loads nothing, and only a directory named there is considered at all: a
directory sitting under the root and absent from `Allowed` is never opened. Being allowlisted is what
gets a plugin looked at, not a guarantee it loads; it still has to pass the host's load-time checks,
and an allowlisted plugin that fails one of them is fatal rather than skipped. The directory name, the
assembly name, and the plugin's `Name` property must all be the same string; the host treats a
mismatch as one of those fatals.

The publish command, the package shape, the two delivery recipes, the fatal catalogue, and what a
plugin may and may not register are all
[PLUGINS.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/plugins/EdFi.Api.Plugins/PLUGINS.md)'s
subject, and the allowlist itself is documented for operators in
[CONFIGURATION.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/docs/CONFIGURATION.md#plugins).
This guide does not restate them.

Compiling a validator into a DMS build remains possible and is not the documented route.

## When a validator runs

Writes only: the upsert (POST) and update-by-id (PUT) pipelines. `CustomValidationOperation` tells
you which one, and it is named after those pipelines rather than after the HTTP verbs. No read,
delete, or query path invokes a validator.

Within a write, a validator runs after the request's claim-set and resource-action authorization, and
after DMS's own core document validation, but **before** the relationship, namespace, ownership, and
custom-view authorization that the backend decides while performing the write. Two consequences to
plan for:

- A validator is invoked, and any I/O it performs is performed, for documents whose caller will
  ultimately be refused.
- When a validator returns failures, the caller receives that 400 instead of the 403 they would
  otherwise have been given.

## What `document` is

The **profile-effective body**: the profile-shaped writable surface when a writable profile applied
to the request, and the parsed request body otherwise. It is never the raw submitted bytes, and each
validator receives its own copy, so one validator cannot change what a later one sees.

What follows from that:

- **Version metadata.** With no writable profile, the body carries a server-assigned
  `_lastModifiedDate` the client never sent, because injection happens before a validator sees it. A
  writable profile's surface is built *before* that injection and does not carry the property at all.
  A validator that must behave the same either way should not depend on its presence.
- **Coercion.** Date-format and date-time coercion have run. Broader request-value coercion has too,
  unless the deployment sets `AppSettings:BypassTypeCoercion`, which removes that coercion step from
  the pipeline. Core document validation still runs either way; what a validator should not assume is
  that every value was rewritten into its schema type.
- **Which members are present.** A writable profile's member filter decides, and which members
  survive depends on the filter's mode: an `IncludeOnly` filter keeps only the members it names, an
  `ExcludeOnly` filter keeps everything except those, and `IncludeAll` keeps everything. So an
  ordinary member your rule reads can be absent under some profiles and present under others. Write
  the rule so it does not depend on which.
  **Resource identity is the exception in every mode**: root resource identity members are preserved
  even when a profile's filter would hide them, so a natural-key member such as a student's
  `studentUniqueId` is there regardless. Do not generalize either half. The surrogate document
  identifier `$.id` is *not* a resource identity member and so gets no such preservation; see
  [Why the document body is not a substitute for identity](#why-the-document-body-is-not-a-substitute-for-identity).
- **It is read-only.** The parameter is a `JsonNode` and nothing in the type system stops you
  mutating it. Not mutating it is a contract rule.

## Declaring applicability: AppliesTo

Matching against the request's resource is **exact and ordinal** on both `ProjectName` and
`ResourceName`. There is no wildcard and no case-insensitive form, so a typo'd or wrong-cased entry
never matches and your validator simply never runs for that resource.

A mistake here surfaces as a **startup warning rather than a failure**, and that is deliberate: an
entry may legitimately name an extension resource a given deployment does not carry. You get a
warning when an entry matches no resource in the effective schema, when a name is outside the ASCII
letters and digits a resource name may contain, and when a validator declares no entries at all. A
deployment that reads its startup log sees the typo; one that does not gets a validator that never
runs.

**The getter's cost is a rule, not a preference.** DMS reads `AppliesTo` on every write request for
every registered validator, before any filtering happens. So it must be cheap, synchronous, and free
of I/O. A lookup inside the getter slows every write the deployment serves, including writes to
resources your validator does not apply to and writes whose `ValidateAsync` is never called. Build
the list once, as the sample does, and hand it back.

## Constructors run on every write

DMS resolves every registered validator from the request scope before it reads any `AppliesTo`, so
**your constructor runs on every write request whether or not your resource matched.** Keep it
trivial. Reading `IOptions<T>.Value` is the right amount of work; opening a connection is not.

**A validator's constructor must not require per-request state.** That is a rule you follow, not one
the platform enforces for you. The startup guard resolves your validator once from a throwaway scope,
so it catches a constructor dependency that cannot be resolved at all. It does **not** catch a
service that constructs cleanly and throws when its request-scoped state is read: that one starts
cleanly and fails later, on a request.

## Lifetime and registration shape

One shape is accepted:

```csharp
services.TryAddEnumerable(
    ServiceDescriptor.Transient<ICustomResourceValidator, MyValidator>()
);
```

Transient, unkeyed, and an implementation type. The startup guard audits these registrations and
aborts the process rather than letting a validator silently never run. It rejects:

- a lifetime other than `Transient`;
- an `ImplementationInstance`, which is a shared instance rather than an implementation type;
- an `ImplementationFactory`, which is a factory delegate rather than an implementation type;
- a registration against `IEnumerable<ICustomResourceValidator>`, which would replace the collection
  DMS resolves rather than contribute to it;
- a keyed registration naming no implementation type, which contributes nothing to the unkeyed
  collection DMS resolves.

It separately aborts when a type your registrations show to be a validator is not among the instances
DMS's own resolution actually returns, because only a registration that contributes to
`IEnumerable<ICustomResourceValidator>` ever runs.

**Why the factory rejection needs a workaround, and what it is.** The rejected shape includes
`ServiceDescriptor.Transient<ICustomResourceValidator, MyValidator>(sp => new MyValidator(...))`,
which is otherwise a perfectly ordinary way to pass a constructor argument that is not itself a
service. Supply that argument through options instead: bind an options type and take `IOptions<T>` in
the constructor, exactly as the sample does. The guard's own message says the same thing, so the
documentation and the runtime failure agree.

**`TryAddEnumerable`, and what it does not do.** It is required because it adds to the collection
rather than replacing it: an earlier plugin's validator survives your call, and yours survives a
later plugin's. Running every registered validator is DMS's own doing, not this helper's. Its fan-in
step resolves them all on each write and invokes the ones whose `AppliesTo` matches, which is what
lets any number of plugins each contribute a validator.

## Reporting failures

Return the failures you found, or an empty list. **A null return is not a substitute for an empty
list** and is treated as a hard failure, as is a null `Task` and a null element in the list.

The two cases map onto the two buckets DMS's own 400 body already carries.

`OnPath` produces a `validationErrors` entry, keyed by the JSON path:

```json
{
  "detail": "Data validation failed. See 'validationErrors' for details.",
  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
  "title": "Data Validation Failed",
  "status": 400,
  "correlationId": "0HN7C4NQEXAMPLE",
  "validationErrors": {
    "$.studentUniqueId": [
      "A Student unique id must begin with 'SEA-' in this deployment."
    ]
  },
  "errors": []
}
```

`OnResource` produces an `errors` entry, and any `errors`-arm failure selects the plainer bad-request
shape even when `validationErrors` is also populated:

```json
{
  "detail": "The request could not be processed. See 'errors' for details.",
  "type": "urn:ed-fi:api:bad-request",
  "title": "Bad Request",
  "status": 400,
  "correlationId": "0HN7C4NQEXAMPLE",
  "validationErrors": {},
  "errors": [
    "The external student identity service is not configured, so Student cannot be verified."
  ]
}
```

A bare `"$."` is a valid, non-degenerate `JsonPath`: it is DMS's own document-level
`validationErrors` key, so use `OnPath("$.", ...)` when you want parity with a core document-level
failure. A bare `"$"` is rejected, because it is not `"$."`-prefixed.

**Accumulation.** Every applicable validator runs; there is no early exit among them on returned
failures. Failures accumulate across all of them before any response is produced, so two validators
reporting the same path both survive, and a client sees every rule's complaint at once rather than
one per round trip.

**Message order is not part of the contract.** Neither the order of entries within `errors`, nor the
order of messages within a `validationErrors` entry, is contracted. A client must not depend on it.

## When a validator throws

Returning a failure and throwing are different acts with different outcomes, and the distinction is
the whole of what you need to design around.

- **Return failures for validation outcomes.** That is the 400 above.
- **Let genuine faults throw.** An exception that escapes your validator is not silently swallowed
  and never becomes a quiet success. Most exception types reach the host's catch-all arm, which
  records the exception on the request and answers a logged 500. That write persists nothing.
- **An exception halts the validators after yours.** Accumulation applies to returned failures, not
  to throws: the first one ends the request, so validators that had not run yet do not run.

Three cases are not that simple, and two of them can reach you without you intending it.

**Cancellation.** An `OperationCanceledException` raised on a request the client has already aborted
**propagates rather than becoming a 500**. So do not swallow the token's cancellation and report a
validation failure instead: you would be answering 400 for a request that no longer has a caller.
Observe the token and let it throw.

**A circuit breaker in your own client.** The host answers `Polly.CircuitBreaker.BrokenCircuitException`
as a retriable 503 with a `Retry-After` header rather than as a 500. If your validator calls an
external system through an HTTP client with a Polly circuit breaker, that type can escape your code
without your having chosen it, and the caller will be told the *service* is unavailable. If that is
not what you want said about your dependency, catch it yourself and decide what to report.

**Core's authorization types.** Two of the specially handled types belong to DMS's own authorization
machinery, one answered as a 403 and one as a problem+json 500. Neither is reachable through either
contract package: getting at them means referencing a host assembly, which forfeits this package's
compatibility promise (see [Supported versus possible](#supported-versus-possible)), and throwing one
to steer the response is unsupported.

Do not build a table of the host's exception handling and design against it. It is the host's, it has
gained arms before, and it can gain more. The stable rule is the first two bullets.

## The cost of I/O on the write path

The fan-in step **awaits every applicable validator** before the request reaches its handler, and it
invokes them one at a time rather than concurrently. So:

- A slow external system makes every matching POST and PUT slow. An unreachable one makes them fail.
- **The only timeout is the one your own client is configured with.** The contract imposes none, and
  there is no per-validator budget DMS enforces.
- A timeout surfaces as a 500, not as a skipped validator. There is no "carry on without this rule"
  behavior to fall back to.

DMS records each validator's elapsed time per request at `Debug` level, which is what a deployment
reaches for when a validator is slow. It is logged under the
`EdFi.DataManagementService.Core.ApiService` category, so a log-level override scoped to the
middleware's own namespace surfaces nothing.

Design a validator that calls out over the network with all of that in view: a short client timeout,
a circuit breaker whose exception you handle yourself, and a rule that is worth the latency it adds
to every matching write.

## Scoping a rule to less than the deployment

**A registration belongs to a whole DMS deployment, not to one district.** There is no per-district,
per-tenant, or per-route registration: a validator that applies to a resource applies to that
resource for **every district and every route the deployment serves**, unless it inspects the
`ValidationScope` it is handed and decides otherwise.

Two different things are scoped differently, and it is worth keeping them apart. `AppliesTo` narrows
which **resources** reach your `ValidateAsync`; nothing narrows which **districts** do. And your
constructor is narrower than neither: it runs on every write that reaches the step, whatever the
resource and whatever the route (see [Constructors run on every write](#constructors-run-on-every-write)).

- `Tenant` is **null in every single-tenant deployment.** A rule keyed on it silently never matches
  unless multi-tenancy is enabled.
- `RouteQualifiers` is how a district is normally identified. It carries the qualifiers the write was
  routed through, such as district and school year, and is empty in a deployment with no route
  qualifiers.

Read `ValidationScope` as a carrier, not a value to compare. It is a record, so it has value
equality, but that equality compares `RouteQualifiers` with the default comparer for a dictionary
interface, and the BCL dictionary types do not override equality. Two scopes holding equal entries in
different dictionary instances are therefore not equal.

## Compatibility

### The package version is the contract's own

The published id is `EdFi.Api.CustomValidation`. The assembly inside it is
`EdFi.DataManagementService.CustomValidation`; the two deliberately differ, and the difference
matters when you read a load-time error (see below).

The semver promise covers this contract's own types and nothing else.

**The version policy for the published contract** is that the version is the contract's own rather
than the DMS release version, so a validator built against contract `1.0.0` will be compatible with
every DMS release that carries `1.0.0`, whatever that release is numbered, and a release's notes state
which contract versions it carries. Tying the version to the release version instead would have a host
refuse a validator built against an identical contract, naming two versions that differ in nothing an
implementer could act on.

**That policy is what the package carries, published or built locally.** The project file declares
`Version`, `AssemblyVersion` and `FileVersion` itself, so a nupkg packed from this repository is
`EdFi.Api.CustomValidation.1.0.0.nupkg` with an assembly at `1.0.0.0` whatever release version the
build was given; the build and release lanes assert exactly that on every pull request and on every
prerelease. The sibling `EdFi.Api.Plugins` contract declares its own version the same way, in
`src/plugins/Directory.Build.props`, so the two numbers move independently of each other and of the
Data Management Service release. Read a package's version from the nupkg's own metadata, never from
the DMS release number or from the other contract's version. [Getting the package](#getting-the-package)
below names the feed, and [Versioning](#versioning) states what moves the number.

### Additive-only, for the life of the package

The contract follows an **additive-only policy for the life of the package**. A member added to
`ICustomResourceValidator` after first publication carries a default implementation. A change that
cannot be defaulted is a **new package id**, with a new contract the host discovers alongside the old
one for as long as both are supported, rather than a major version bump of this one.

That policy is what makes the guarantee below hold by construction. It also means the obvious
worry mostly does not arise: a genuinely breaking change to `ICustomResourceValidator` or
`CustomValidationFailure` would require every validator to be recompiled, which is exactly why the
policy forbids one.

### Older validator, newer host

Runs, by construction. Under host-first resolution the host serves its own copy of the contract
assembly to your plugin whatever version you compiled against, and the additive-only policy means
the host's copy still satisfies the surface you compiled against. There is no rebuild precondition.

### Newer validator, older host

Refused at load, by name, **before any type is loaded**. For example, a plugin requiring contract
assembly version `2.0.0.0` cannot load into a host carrying `1.0.0.0`. The diagnostic identifies the
plugin, contract assembly, required version, and host version; its exact wording can vary:

```text
plugin 'Acme.Dms.StudentIdentity' requires 'EdFi.DataManagementService.CustomValidation' >= 2.0.0.0, host carries 1.0.0.0
```

**That message names the assembly, not the package id you wrote in a `PackageReference`.** The check
reads your entry assembly's references from metadata, and an assembly reference carries an assembly
name, so it says `EdFi.DataManagementService.CustomValidation` where your csproj says
`EdFi.Api.CustomValidation`. Searching your project file for the name in the message would find
nothing; this is why.

### The case neither direction catches

A `ValidateAsync` body that reaches a member existing at compile time and not on the deployed host
is **not** caught at load, because that method is not JIT-compiled until it is first called. It
surfaces as a logged 500 on the first write that matches your `AppliesTo`. The process starts
cleanly and stays up until such a write arrives.

Within **this contract's own surface** the additive-only policy means there is nothing here to hit.
Everything else your plugin binds to is a different matter, and supported dependencies are not
exempt. The load check compares assembly versions, and the `Microsoft.Extensions.*` packages this
guide tells you to declare hold theirs stable across a major version, so building against a newer
*minor* of one of them and calling a member it added passes the check and lands here instead. See
[Two limits on that check](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/plugins/EdFi.Api.Plugins/PLUGINS.md#two-limits-on-that-check-both-real).

A validator that reached around the contract into host assemblies, which
[Supported versus possible](#supported-versus-possible) tells you not to do, widens the exposure
further, because nothing about those assemblies is version-disciplined on your behalf.

### Target framework

A compatibility axis alongside the package version, because a plugin is served the **host's** shared
framework rather than its own. This contract targets `net10.0`. A DMS runtime major-version bump
therefore changes the set of loadable validators without any package version changing.

## Trust

**A loaded validator runs in the DMS process with full process trust.** It can read every connection
string, every decrypted secret, every request body, and every token, and do anything that process can
do.

The isolated load context isolates **assembly identity**. It is **not** a security boundary, and
nothing constrains what your code does once it is loaded. The trust boundary is whoever controls the
plugin root and the allowlist.

The trust model is
[PLUGINS.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/plugins/EdFi.Api.Plugins/PLUGINS.md#trust)'s
subject, and the controls an operator applies, including that the plugin root must never be writable
by the runtime identity, are in
[OPERATIONS.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/docs/OPERATIONS.md#plugins).

## What this contract does not give you

### No store access

This version's contract gives a validator **no access to stored data**. Everything a rule can decide,
it decides from the document in hand, the resource it belongs to, the operation, and the scope. A rule
that requires a lookup against what is already persisted cannot be expressed within the supported
surface.

A store-read capability is a **recorded deferred item**, not an oversight. It is additive to this
surface, since a validator would obtain it by constructor injection rather than as a new parameter,
so adding it later breaks no signature. Until it is documented as a contract capability, host
services you reach by constructor injection are outside this contract and carry none of its
compatibility guarantees.

### Supported versus possible

A validator is in-process code, and nothing stops it doing more than this contract offers. You can
reference a host assembly and constructor-inject services such as `IDocumentStoreRepository` or
`IQueryHandler`, which are public and registered in the host. Such a validator works.

What it forfeits is this package's compatibility promise. Those assemblies carry no semver commitment
to validator authors, so a host upgrade can change or remove what you depended on, and the
[additive-only guarantee](#additive-only-for-the-life-of-the-package) covers none of it. It also
broadens your exposure to [the failure mode that neither compatibility direction
catches](#the-case-neither-direction-catches), which supported dependencies can already reach.

This is stated as a line you are choosing to cross, not an enforcement. There is none.

### The ODS UniqueId rule, and both of its causes

The ODS/API rule that "a person's UniqueId cannot be modified" is **not expressible under this
version's contract**, and it needs two things this version lacks rather than one.

1. **A store read.** The rule compares the submitted UniqueId against the **persisted** one, and this
   contract offers no way to read it.
2. **The persisted document's identity.** The rule keys that read on the persisted document's own
   identifier, and `ValidateAsync` never receives one. There is no `DocumentUuid` parameter, and
   `ValidationScope` carries route qualifiers rather than the route.

**Both are recorded deferred items**, store access and document identity, and neither absence is an
oversight. Store access alone would not close this gap: the rule also requires a reliable identifier
for the persisted document.

### Why the document body is not a substitute for identity

The obvious move after reading the above is to reach for `$.id` in the document. It does not hold.

- On `Update`, the pipeline does require a body `id` matching the route id. But that check reads the
  **parsed request body**, while your validator receives the **profile-effective** body, and a
  writable profile's member filter can hide `id` from the latter while the former still carries it.
  An `IncludeOnly` content type drops `id` unavoidably: it keeps only the members it names, and `id`
  is server-generated, so no profile is permitted to name it. The surrogate `$.id` is not a resource
  identity member either, so the identity preservation that keeps a natural key such as
  `studentUniqueId` present does **not** keep `$.id` present. It can therefore be absent from what
  you receive even on a request that was required to send it.
- On `Upsert`, the body carries no `id` at all by construction. A submitted `id` is rejected with a
  400 before a validator ever runs.

So the body gives you an identifier on some writes and not others, which is not an identity you can
write a rule against.

## Packaging and delivery

This document is the validator contract. Getting an implementation of it into a running host - the
project settings, the publish command, the package shape, the two delivery recipes, the compatibility
surface including the host assembly manifest, the trust model, and what a plugin may and may not
register - is
[PLUGINS.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/plugins/EdFi.Api.Plugins/PLUGINS.md).

## Getting the package

This package is published to the Ed-Fi Azure Artifacts feed:

```text
https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json
```

```xml
<PackageReference Include="EdFi.Api.CustomValidation" Version="[1.0.0]" />
```

A validator reaches the running service through a plugin, so a validator project normally also
references `EdFi.Api.Plugins` from the same feed. The two contracts version independently: this one
declares its version in its own project file and the plugin contract declares its own in
`src/plugins/Directory.Build.props`, so neither number moves because the other did, and neither moves
with the Data Management Service release.

Pin the version exactly, in brackets, as above; a bare version is a minimum rather than a pin. The
release notes of the Data Management Service release you are targeting state which contract versions
that release carries.

## Versioning

This package's version moves when what you compile and resolve against moves, which is three things:
the assembly's public and protected surface, the XML documentation that ships beside it, and the
package's declared dependencies. The rules below live only in `///` comments, so a rule rewritten
there is a changed contract even though no signature moved; the publish lane compares all three
against the version already on the feed and refuses to republish a version whose contract differs.
A changed readme is not one of them.

## Dependencies

**This package declares none.** Everything in its signatures resolves from the framework, so taking
it on commits you to nothing else, and that empty closure is part of the contract rather than an
accident of the current implementation.

Writing a validator and the plugin that registers it requires additional dependencies.
[Which package each line needs](#which-package-each-line-needs) maps the sample APIs to their
packages: `EdFi.Api.Plugins` for the plugin base class and, through it,
`Microsoft.Extensions.DependencyInjection.Abstractions` and
`Microsoft.Extensions.Configuration.Abstractions` for the hook's own signature; then
`Microsoft.Extensions.Options` and `Microsoft.Extensions.Options.ConfigurationExtensions`, which you
declare yourself, for the two forms of options configuration.

## License

Licensed under the Apache License, Version 2.0. See the LICENSE and NOTICES files in the project root
for more information.
