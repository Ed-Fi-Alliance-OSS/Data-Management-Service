# Ed-Fi API Plugins

This package defines `EdFiApiPlugin`, the base class a district or vendor implements to extend the
Ed-Fi Data Management Service or the Ed-Fi DMS Configuration Service without rebuilding either one.

A plugin is a directory of assemblies you publish, compiled against this package. An operator drops
that directory into the host's plugin root and names it in an allowlist. The host loads it into an
isolated assembly load context and calls your contribution hook. **No image is derived and nobody
rebuilds the Ed-Fi API.**

This guide is about packaging and delivering a plugin. For the custom-validation contract itself —
what a validator receives and what it returns — see
[CUSTOM-VALIDATION.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/dms/core/EdFi.DataManagementService.CustomValidation/CUSTOM-VALIDATION.md).

> **This contract package is not on the Ed-Fi feed yet.** The loading mechanism is integrated into
> the Data Management Service, but publication of the contract packages is its own step. Watch the
> release notes of a Data Management Service release: they state which contract versions that
> release carries.

## What is here

One public type, `EdFiApiPlugin`, an abstract class with:

- `Name`, an abstract property that must return the name of the directory the plugin is deployed
  into. The host verifies this at load time and a mismatch is fatal.
- `ContributeServices`, a virtual method the host calls before it builds its container. The base
  implementation does nothing, so a plugin overrides only what it needs.

The host calls the hook on every loaded plugin unconditionally. There is no interface to implement,
no per-phase discovery, and no way for an allowlisted plugin to be silently skipped.

## A plugin, end to end

This compiles against this package alone. It is checked against the copy in this repository's
consumer fixture, so it is a sample that has been compiled rather than one that was typed into a
document.

<!-- embed: eng/verification/PluginsConsumer/AcmePlugin.cs#sample -->
```csharp
using EdFi.Api.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Dms.Sample;

public sealed class AcmeSamplePlugin : EdFiApiPlugin
{
    public override string Name => "Acme.Dms.Sample";

    public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new AcmeEndpoint(configuration["Acme:Endpoint"] ?? "https://localhost"));
    }
}

internal sealed class AcmeEndpoint(string address)
{
    public string Address { get; } = address;
}
```

Only the plugin class has to be public; everything else it ships may stay internal, as
`AcmeEndpoint` does.

**The project settings are part of the sample**, because the host requires four names to be one
string and the SDK defaults will not give you that on their own:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <AssemblyName>Acme.Dms.Sample</AssemblyName>
  <RootNamespace>Acme.Dms.Sample</RootNamespace>
</PropertyGroup>
```

### What this sample is not

**It is not a deployable plugin.** It demonstrates the contract: the class shape, the hook, and that
both parameter types resolve for an outside project. A plugin that registers no declared plugin
contract and contributes no configuration source has contributed nothing the host will ever call, and
**startup fails naming it**, listing what it did register.

What a real Data Management Service plugin registers is a declared contract. For custom validation
that is `ICustomResourceValidator`, which needs a reference to `EdFi.Api.CustomValidation` as well as
to this package:

```csharp
services.TryAddEnumerable(
    ServiceDescriptor.Transient<ICustomResourceValidator, StudentIdentityValidator>());
```

That snippet is an illustration and is not mirrored into the consumer fixture, because that fixture
exists to prove this package resolves and compiles on its own.

## Names: four of them, and they must all match

Ordinally, and each mismatch is fatal on its own:

1. the plugin **directory** name under the plugin root;
2. the entry **assembly's file name**, `<Name>.dll`, which is how the host finds it;
3. the loaded assembly's own **`AssemblyName`**, which the file name does not prove;
4. **`EdFiApiPlugin.Name`**, what your class returns.

The project settings above are what make 2 and 3 agree when your project file is not already named
for the plugin. The comparison is ordinal and the released image is Linux, so
`Acme.Dms.Sample` and `acme.dms.sample` are different names — a plugin that loads on a
case-insensitive developer machine can fail in the image on nothing but capitalisation.

## Discovery

The host resolves `<PluginRoot>/<Name>/<Name>.dll` and reflects over its **public exported types**
for an `EdFiApiPlugin` subclass. The entry assembly must expose exactly one **public, non-abstract,
non-generic** subclass with a **public parameterless constructor**.

- Zero is fatal: the operator allowlisted a directory that contributes nothing. An `internal` class
  is invisible here, and so is one whose only constructor takes arguments.
- More than one is fatal: choosing between them would be arbitrary.

Only the entry assembly is reflected over. Nothing else in your directory is scanned.

## Publishing

Publish **framework-dependent**, into a directory named for the plugin:

```shell
dotnet publish --no-self-contained -o out/Acme.Dms.Sample
```

That produces `Acme.Dms.Sample.dll`, `Acme.Dms.Sample.deps.json`, and a flattened copy of every
dependency the host does not already carry. All three matter:

- The **`.deps.json` is required.** A plugin directory without one is fatal, because the plugin's
  private dependencies would not resolve and the failure would land on a request rather than at
  startup.
- A **self-contained publish is fatal.** It writes a `runtimepack` library into the `.deps.json`,
  which the host refuses by name. A RID-specific *framework-dependent* publish
  (`-r <rid> --no-self-contained`) is fine and is how a plugin ships native assets.
- Flattening the dependency graph into a runnable directory is your job, not the operator's. Nothing
  on the deploy path runs a NuGet restore.

## Packaging

A plugin package is **asset-only**. It carries the published directory under
`contentFiles/any/any/<Name>/` and contains no `lib/` or `ref/` entries:

```text
Acme.Dms.Sample.1.2.0.nupkg
└── contentFiles/any/any/Acme.Dms.Sample/
    ├── Acme.Dms.Sample.dll
    ├── Acme.Dms.Sample.deps.json
    └── ...every dependency the host does not carry
```

A conventional library package declares dependencies and expects a restore to resolve them, and
nothing on the deploy path runs one. The package is transport for a directory that was already
proven to run, and `unzip` is all it takes to get it back out.

## Delivery

You publish the package; the **operator** gets it into the plugin root and names it in
`Plugins:Allowed`. There are two recipes — a pre-populated read-only mount, and a one-shot fetch step
that verifies a digest you publish — and both are documented for operators in
[OPERATIONS.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/docs/OPERATIONS.md#plugins).
The allowlist itself is in
[CONFIGURATION.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/docs/CONFIGURATION.md#plugins).

Publish the SHA-256 of your `.nupkg` bytes beside the package. That is the value an operator pins,
and the fetch recipe refuses to extract without it.

## Compatibility

### What you compile against

The compatibility surface is **the contract packages plus the host assembly manifest** for the
Data Management Service version you are targeting. Each release publishes a host assembly manifest
as a release asset beside the SBOM: the name and assembly version of every managed assembly the host
can serve, with the contract versions that release carries at the top.

It exists because of how assemblies resolve. Each plugin gets its own load context, and resolution is
**host-first**: any assembly the host itself carries is served from the host, and your own copy is
used only for assemblies the host does not have. So the host's whole assembly closure is part of what
your plugin must be compatible with, whether or not an assembly appears in a hook signature — and no
contract package announces that closure. The manifest is what lets you see it before you deploy.

**A version the host cannot serve is fatal, not a fallback.** If your plugin declares a *higher*
version of an assembly the host also carries, it is refused by name rather than quietly given its own
copy, because two copies of one assembly means two identities for every type they exchange.

### Two limits on that check, both real

**It fires on major skew, not on minor.** The manifest lists `AssemblyVersion`s and the host compares
`AssemblyVersion`s. `Microsoft.Extensions.*` holds that version stable across a major version, so a
plugin built against a newer *minor* of an assembly the host also carries loads without complaint. Do
not read the manifest as a minor-level compatibility check.

**When it fires depends on where the assembly comes from.** Skew on anything your own `.deps.json`
declares is caught at load, before your plugin is even constructed. The manifest's shared-framework
section is **not** in a framework-dependent `.deps.json` at all, so skew there is caught later, at
first use.

That distinction has a cost worth knowing. If the first use falls inside startup, you get the same
named failure. If it falls on a request — because the assembly is only touched on a request path —
there is no loader frame above it to turn the runtime's error into a readable one, so it surfaces as
a `FileLoadException` inside the request and repeats on **every** request that reaches it, rather
than stopping the process.

### Older plugin, newer host

An older plugin runs on a newer host **by construction**, and that phrase is used for the contract
and for nothing wider. `EdFiApiPlugin` is a base class whose members are virtuals with no-op bodies,
and the compatibility policy for this package is **additive-only for the life of the package**: new
virtual members with no-op bodies, never a new abstract member, never a signature change, never a
removal. Adding a member that way is binary-compatible with every plugin already published.

The same policy binds every interface a plugin implements, including `ICustomResourceValidator`: a
member added after first publication carries a default implementation, and a member that cannot be
defaulted is a **new package id** rather than a major version bump.

The reverse direction is **not** supported and is not silently attempted. A plugin compiled against a
newer contract than the host carries is refused at load, by name — "requires `EdFi.Api.Plugins` >= X,
host carries Y" — rather than failing later with a `MissingMethodException` inside a hook.

The compatibility surface as a whole carries no by-construction guarantee. Only the contract does.

## Trust

**A loaded plugin runs with full process trust.** It can read every connection string, every
decrypted secret, every request body, and every token. The isolated load context isolates **assembly
identity**; it is **not** a security boundary, and nothing constrains what your code does once it is
loaded.

Operators are told the same thing, in the same words, in
[OPERATIONS.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/docs/OPERATIONS.md#plugins).
They are also told that the plugin root must never be writable by the runtime identity, because write
access to it is equivalent to code execution as the host process. Treat your published bytes
accordingly: publish a digest, and keep your build lane as trustworthy as the deployment that trusts
it.

## Do not name an assembly with an Ed-Fi host prefix

**No assembly you ship may be named `EdFi.DataManagementService.*` or
`EdFi.DmsConfigurationService.*`.**

The host decides what is host-owned by assembly name and by nothing else, so a type declared in an
assembly you published as `EdFi.DataManagementService.Acme` is treated as the host's. Your plugin then
fails the host-owned displacement check on its **own** types. Read this as a naming rule, not as an
inexplicable startup failure.

## What you may register, and how

### May

- Your own types, freely.
- `Microsoft.Extensions.*` types, freely.
- The declared plugin contracts, in the form their cardinality requires — see below.
- Additional logging **providers**: `ILoggingBuilder.AddSerilog` and its equivalents are fine.
- Removal or replacement of a descriptor **you** added, which is routine registration work, and of a
  pre-existing framework or third-party descriptor, which is permitted and recorded in the load
  inventory.

### May not

- Register a host-owned service type that is not a declared plugin contract. Fatal, naming you and
  the service type.
- Remove, replace, or overwrite any host-owned descriptor that existed before your hook ran. Fatal.
  Plugins contribute registrations; they do not edit the host's.
- Remove, replace, or overwrite the host's logging registrations — `ILoggerProvider`,
  `ILoggerFactory`, `ILogger`, or `ILogger<>`. **Fatal.** `logging.ClearProviders()` is exactly a
  removal of `ILoggerProvider` and lands here. The host wires its logging before your hook runs, so
  clearing providers would silence the service *and* the inventory record of your plugin doing it.
  Add a provider instead.
- **Add** an unkeyed `ILoggerFactory`, non-generic `ILogger`, or `ILogger<>` registration, open or
  closed. **Fatal**, on `Add`, on `Insert`, and through the indexer alike. Those services are
  resolved singly, so the last registration wins and adding one displaces the host's logging just as
  surely as removing one. `IServiceCollection.AddSerilog` can register an `ILoggerFactory` and is
  therefore refused, while `ILoggingBuilder.AddSerilog` adds a provider and is permitted. Keyed
  logging registrations are unaffected.
- Register a declared plugin contract under a wildcard service key. Fatal: enumerable resolution
  cannot reach a wildcard registration, so the host's startup probe could not cover it.

### The registration form per cardinality is a rule, not a suggestion

The host detects conflicting claims, and its detection depends on the form you use.

- A **fan-in** contract — one where every registered implementation runs — is registered with
  `TryAddEnumerable`.
- A **replace** contract — one with a single claimant — is registered with a plain **`Add`**, never
  with a `TryAdd`. A replace contract has one claimant, so there is nothing to try.

Ignoring that rule fails in two shapes, and the second is the reason it is a rule:

1. A `TryAdd` that declines, when it was your plugin's only contribution, leaves you registering no
   declared contract, and **startup fails naming you**. Annoying, but loud.
2. A `TryAdd` that declines in a plugin that *also* registered something declared is **not detected
   at all**. Your plugin loads, startup succeeds, and the replacement simply never happens.

There is no startup failure waiting to teach you the second case. Follow the rule instead.

## When something goes wrong

An allowlisted plugin that does not load is **fatal**: the host does not start. That is deliberate —
a validation plugin that silently failed to load would mean business rules stop being enforced while
writes keep succeeding.

Two places carry the answer, and they answer different questions.

- **Why startup failed** is in the host's startup status file, a JSON document naming the bootstrap
  phase, the exception type, and the message. During loading the host also writes to standard error,
  because plugin loading runs before the logging pipeline exists.
- **What actually loaded** is in the log, as one `Plugin inventory` event per plugin listing the
  files you declared, the service types you registered, the descriptors you removed, and every
  assembly the host served in place of your own copy. That last list is where a host-first
  substitution shows up after the fact; the host assembly manifest is how you avoid one beforehand.

The full catalogue of failures, grouped by what an operator does about each, is in
[OPERATIONS.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/docs/OPERATIONS.md#plugins).

## Versioning

This package carries its own semantic version, independent of the Data Management Service release
version, because the host compares contract assembly versions when it decides whether a plugin can
run. The version moves when the public surface moves, and only then. Tying it to the release version
would have an 8.3 host refuse a plugin built against an identical 8.4 contract, naming two versions
that differ in nothing you could act on.

A breaking change to this contract is not a version bump; it is a **new package id**, with a new base
class the host discovers alongside the old one for as long as both are supported.

## License

Licensed under the Apache License, Version 2.0. See the LICENSE and NOTICES files in the project root
for more information.
