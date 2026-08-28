# Plugin Infrastructure Design

## Status

Approved 2026-08-27 by the spike's ticket owner, in session, as the design output of spike `DMS-1462`.
The approval review produced four revisions, each folded in below rather than tracked separately: the contract package is versioned independently of the DMS release; the class-versus-interface argument is corrected and an additive policy is handed to the per-type contracts; the recording wrapper's removal rule is scoped to host-owned service types; and the host's assembly manifest is published per release as part of the compatibility surface.

**Revision 5 (2026-08-27, panel review).**
A multi-model review of the approved document and its drafts found forty confirmed defects.
The load-bearing ones are corrected here rather than tracked as follow-ups, because each of them made a story unimplementable as written.

- **Version skew is detected by comparing versions, not by catching an exception.** A probe on `net10.0` showed `AssemblyLoadContext.Default.LoadFromAssemblyName` throws `FileNotFoundException` both when the host lacks an assembly and when the host carries an older version than the plugin asked for, strong-named or not, so the `FileLoadException` arm the earlier revision shipped was dead code and a version-skewed plugin would silently have received its private copy. Host-first resolution is now by **simple name**, with an explicit version comparison and an eager `.deps.json` preflight. See [Load Isolation](#load-isolation).
- **Plugin configuration sources are placed below the *last* environment variable source, not the first.** `WebApplication.CreateBuilder(args)` installs twelve sources on `net10.0`, not five, and the first environment variable source sits *below* `appsettings.json`. The earlier placement would have let `appsettings.json` shadow a vault value. Probe-verified. See [The Two Composition Phases](#the-two-composition-phases).
- **Declined `TryAdd` calls are not observable at the `IServiceCollection` seam, so the wrapper stops pretending to record them.** A declined `TryAddSingleton<IFoo, Foo2>` touches only `Count` and the indexer; the candidate descriptor never reaches the wrapper. Replace-cardinality contracts are instead **masked** from the view each plugin sees, so a claim can never silently decline. See [Contract Cardinality](#contract-cardinality).
- **The stock image change is not zero.** `src/dms/Dockerfile` builds from a per-project `COPY` list inside the `src/dms/` context, so a new `src/plugins/` tree has to reach it. The "no Dockerfile change" claim is withdrawn and the change is assigned to the host-integration story. See [What the Stock Image Must Ship](#what-the-stock-image-must-ship).
- **The host assembly manifest is generated from the built image, not from the build stage's `.deps.json`.** A framework-dependent publish omits every shared-framework assembly, including the two abstractions in the hook signatures, and `/app/Frontend` exists only in the build stage. See [Load Isolation](#load-isolation).
- **The plugin attribution checks live in a frontend-owned startup task, not in Core's.** Core cannot see `EdFi.Api.Plugins.Hosting`'s per-plugin records without a project reference it must not take. See [Where the Code Lives](#where-the-code-lives).
- **`EdFi.Api.CustomValidation` gets its own semantic version too**, and the loader's preflight covers every declared contract package rather than only `EdFi.Api.Plugins`. See [The Plugin Contract](#the-plugin-contract).
- Smaller corrections folded in without their own bullet: the self-contained discriminator is a `runtimepack` library entry rather than a RID in `runtimeTarget`; unmanaged native assets are supported and their failure mode is stated; allowlist entries carry a name-shape rule and a containment check; the two acquisition recipes are proven in two deployments rather than one, because they cannot share a mount target; the compose mount is an overlay file rather than an unconditional line; `Allowed` ships empty; the inventory hashes every assembly the plugin's own context loaded; several citations, the ODS module-ordering claim, and the `IClientSecretHasher` registration count are corrected.

Three findings were examined and **refuted**, and are recorded here so they are not re-litigated: that draft 03 references a Phase A hook (the text is in this document's [Testing Strategy](#testing-strategy) and is already scoped to the secrets story); that the pull-request lane's pack step is cited at the wrong lines; and that draft 03's cloud-SDK criterion forces a `src/plugins` to frontend project reference (the draft already offers the frontend suite as a home).

It specifies the shared plugin mechanism: how third-party code reaches a stock DMS or CMS image, how it is verified, how it is loaded, and the two points in startup at which it may contribute.
It does not specify the per-type contracts.
Those are companion documents written after this one is approved, and tickets are filed only after those are approved in turn.

DMS-1462 names three plugin types: Secrets Manager, Custom Validation, Identity Validation.
They are not one shape.
The central claim is that a single **delivery** mechanism serves all three, on both hosts, provided the **composition** shapes stay distinct.

- [README.md](./README.md) - spike manifest, story index, filing gate
- [ods-precedent.md](./ods-precedent.md) - the Ed-Fi ODS/API survey this design draws on and departs from
- Jira: [DMS-1462](https://edfi.atlassian.net/browse/DMS-1462)
- Consuming epics: [DMS-1345](https://edfi.atlassian.net/browse/DMS-1345) Custom Validation, [DMS-1412](https://edfi.atlassian.net/browse/DMS-1412) Identity Management, [DMS-1414](https://edfi.atlassian.net/browse/DMS-1414) UniqueId Validation

**Citation convention.**
Unprefixed paths are relative to the repository root.
`Program.cs`, `Infrastructure/`, and `Content/` are relative to `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/`.
`Config.Frontend/` names `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/`.
Anything under `src/dms/core/` or `src/dms/clis/` is written in full, because the frontend carries folders of the same name.
References to `design.md` without a directory mean `reference/design/custom-validation-DMS-1345/design.md`.
ODS/API citations live in [ods-precedent.md](./ods-precedent.md), not here.

---

## Table of Contents

- [Goals and Non-Goals](#goals-and-non-goals)
- [Problem Statement](#problem-statement)
- [Design](#design)
  - [Decision: drop-in delivery into a stock image](#decision-drop-in-delivery-into-a-stock-image)
  - [The Unit of Delivery](#the-unit-of-delivery)
  - [Acquisition](#acquisition)
  - [Discovery](#discovery)
  - [Load Isolation](#load-isolation)
  - [The Plugin Contract](#the-plugin-contract)
  - [The Two Composition Phases](#the-two-composition-phases)
  - [Contract Cardinality](#contract-cardinality)
  - [Trust Model and Verification](#trust-model-and-verification)
  - [Startup Failure Semantics](#startup-failure-semantics)
  - [Observability](#observability)
  - [Configuration Surface](#configuration-surface)
  - [What the Stock Image Must Ship](#what-the-stock-image-must-ship)
  - [The Secrets Spike](#the-secrets-spike)
  - [Applicability to the Configuration Service](#applicability-to-the-configuration-service)
- [Where the Code Lives](#where-the-code-lives)
- [Divergence from the Custom Validation Epic](#divergence-from-the-custom-validation-epic)
- [Rejected Alternatives](#rejected-alternatives)
- [Out of Scope and Deferred](#out-of-scope-and-deferred)
- [Testing Strategy](#testing-strategy)
- [Level of Effort](#level-of-effort)
- [Cross-References](#cross-references)

---

## Goals and Non-Goals

### Goals

- One delivery mechanism serving all three plugin types, so a second plugin type does not mean a second loader.
- An implementer builds and versions a plugin against published Ed-Fi contract packages and a published per-release host assembly manifest, with no DMS source tree and no DMS build.
- An operator adds a plugin to a **published stock image** at deploy time. No image is derived, rebuilt, or re-derived on a DMS release.
- Both composition moments DMS needs are served: contributing configuration before configuration is read, and contributing services before the container is built.
- A plugin can displace neither a host assembly nor a host service registration, and shared type identity is structural rather than a list somebody has to maintain.
- Acquiring a plugin from a published package at deploy time is a documented recipe that needs no DMS code, with the integrity step stated and mandatory.
- A plugin the operator asked for that did not load stops the process rather than degrading it silently.

### Non-Goals

- Sandboxing or privilege reduction. A loaded plugin runs with full process trust; see [Trust Model and Verification](#trust-model-and-verification).
- Hot reload. Plugins load once at startup; adding, removing, or upgrading one is a restart.
- Unifying a plugin's dependency closure with the host's. The design isolates what it can and shares what it must.
- An Ed-Fi catalog, vetting process, or signing authority for third-party plugins.
- The per-type contracts. This document fixes their cardinality and their loading, not their members.

---

## Problem Statement

Three epics have arrived at the same blocked step, and the same missing thing blocks each.

**Custom validation** (DMS-1345) shipped a public contract in DMS-1432 and specified compiled-in composition in DMS-1434: "the deployment adds one call to that extension at DMS's composition root."
That sentence assumes a deployment that builds DMS.

**Identity management** (DMS-1412) records the requirement as "Swagger is set already and the API portion is a placeholder - the clients actually create the implementation via plugin."
The word plugin is load-bearing and has no DMS referent today.

**UniqueId validation** (DMS-1414) is documentation-shaped and depends on whatever custom validation ships, so it inherits the same assumption.

**Secrets** has no epic and is not designed here.
DMS reads `ConfigurationServiceSettings:ClientSecret` and `ConfigurationServiceSettings:EncryptionKey` from its own configuration and obtains every data store connection string from the Configuration Service, decrypting it with that shared key (`src/dms/core/EdFi.DataManagementService.Core/Configuration/DmsConnectionStringProvider.cs`, `src/dms/core/EdFi.DataManagementService.Core/Configuration/ConnectionStringDecryptionService.cs`).
CMS reads four more from `Config.Frontend/appsettings.json`.
An operator who keeps those in a secret store has no seam to reach today.
Secrets is named here because it is the type that proves the mechanism must serve **two** lifecycle moments rather than one, and because a mechanism designed around validators alone would not.
Its contracts, and everything multi-tenant about them, go to a separate spike; see [The Secrets Spike](#the-secrets-spike).

**The distribution model is what makes compiled-in composition the wrong default.**
DMS is not distributed as a source tree an operator builds.
`src/dms/Dockerfile` publishes `--self-contained false` into `/app/Frontend`, and the released image, `src/dms/Nuget.Dockerfile`, does not build at all: it downloads the `EdFi.Api` package from the Ed-Fi Azure Artifacts feed and unzips it into `/app`.
An operator running DMS has an image and a configuration file.
Telling that operator to add a line to `Infrastructure/WebApplicationBuilderExtensions.cs` is telling them to acquire and maintain a DMS build they do not have, and to repeat it on every DMS release.
No document in the repository describes how to do that, because it has never been a supported path.

So the problem is not how DMS invokes third-party code.
Three epics already know how to invoke it.
The problem is **how third-party code gets into a process the operator did not build**, and nothing in DMS answers that.

---

## Design

### Decision: drop-in delivery into a stock image

An implementer publishes a plugin as a directory of assemblies compiled against published Ed-Fi contract packages.
An operator gets that directory into a **published stock image** at deploy time, names it in an allowlist, and restarts.
The host discovers it, loads it into an isolated assembly load context, and invokes its contribution hooks at the two points in startup where contributions are possible.

Compiling a plugin into the host is not withdrawn as a capability, and the in-repo test fixtures use it.
What changes is that it stops being the documented route.

**Two things are ruled out by constraint, not by preference.**

A **derived image** is the obvious alternative and it is the one this design exists to avoid.
`FROM edfialliance/ed-fi-api:8.0.0` plus `COPY ./plugin /app/plugins/` needs no loader, no acquisition code, and no allowlist.
It also means every implementation owns an image, re-derives it on every DMS release, and inherits responsibility for a base image it did not build.
The requirement is a stock image per deployment, not a derived one per implementation, so acquisition happens at **deploy time**, outside the image, and the image is never rebuilt.

**Compiled-in composition** is ruled out by the distribution model above.

### The Unit of Delivery

A plugin is a **directory**, not a DLL.

```text
/app/plugins/
  Acme.Dms.Identity/
    Acme.Dms.Identity.dll          <- entry assembly, named for the directory
    Acme.Dms.Identity.deps.json    <- dependency manifest, produced by dotnet publish
    Acme.Http.Client.dll           <- the plugin's own closure
```

The directory name is the **plugin name** and the entry assembly is `<PluginName>.dll` inside it.
No new manifest format is introduced: the `.deps.json` that `dotnet publish` already emits is the dependency manifest, and the directory name carries identity.

The csproj `AssemblyName` is the plugin name.
The directory, the entry assembly file, and the `Name` property must all equal it, and each mismatch is fatal on its own.

Ownership of the closure is therefore unambiguous.
Every file under `Acme.Dms.Identity/` belongs to that plugin and to nothing else, which is what makes both per-plugin isolation and per-plugin verification possible.

The implementer's workflow is an ordinary publish:

```shell
dotnet publish Acme.Dms.Identity.csproj --no-self-contained -o out/Acme.Dms.Identity
```

`--no-self-contained` keeps the shared framework out of the plugin's output.
A self-contained publish would be survivable under host-first resolution, because the host wins every assembly it can resolve, but it bloats the directory with hundreds of assemblies that will never be used, so it is rejected at load and the implementer finds out immediately rather than shipping it; see [Startup Failure Semantics](#startup-failure-semantics) for the row and the discriminator.

### Acquisition

**There is one mechanism, and it is a directory.**
The loader reads a plugin root and cannot tell how the bytes got there.
**DMS ships no fetcher.**
Getting a plugin directory into the root is a deployment step, it happens before the process starts, and it is documented as two recipes rather than built as a feature.
`Plugins:Allowed` says which directories under the root may load; nothing in the host configuration says where their bytes come from.

```json
"Plugins": {
  "Directory": "/app/plugins",
  "Allowed": "Sea.Dms.StudentIdValidator,Acme.Dms.Identity"
}
```

**`Allowed` is a comma-delimited ordered list of plugin names.**
Whitespace around each name is trimmed, and the order is the invocation order for both phases, contractual for Phase A exactly as it would be for an array.

```text
Plugins__Allowed=Sea.Dms.StudentIdValidator,Acme.Dms.Identity
```

**The delimited string is a convention, not a parser.**
ASP.NET Core's own `AllowedHosts` is a delimited string in the very `appsettings.json` DMS ships (`src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json:2`), and DMS already binds a comma-delimited configuration string of its own: `AppSettings:AllowIdentityUpdateOverrides` is declared as a `string` (`src/dms/core/EdFi.DataManagementService.Core/Configuration/AppSettings.cs:24`) and split where it is used (`src/dms/core/EdFi.DataManagementService.Core/ApiService.cs:253`).
Here the split happens once, in .NET, where the section is bound: trim, split, reject duplicates.
An indexed array would bind cleanly from JSON and badly from the environment, which is where Compose `environment:` blocks and Kubernetes `env[].name` set it, and a dictionary keyed by name would enumerate alphabetically and lose the order Phase A depends on.

**Recipe 1, a pre-populated root.**
The operator publishes plugin directories on the host and bind-mounts the tree read-only.

```yaml
# eng/docker-compose/plugins-dms.yml
services:
  dms:
    volumes:
      - ${DMS_PLUGINS_MOUNT_SOURCE:?set the host path holding the plugin directories}:/app/plugins:ro
```

This is the shape `eng/docker-compose/bootstrap-dms.yml:8` already uses for ApiSchema and `eng/docker-compose/published-config.yml:61` already uses for additional claim sets.
Docker creates the mount target inside the container, the loader finds a populated directory, and nothing else happens.

**The mount lives in its own overlay file, and that is a decision rather than a filing convention.**
`published-dms.yml` and `local-dms.yml` declare no `volumes:` block on the DMS service today, and adding one unconditionally would make Docker materialize an empty, root-owned `./plugins` beside every deployment that never asked for a plugin, which is a directory an operator then has to explain.
Compose has no way to make a bind mount conditional inside one file, so the mount goes in `eng/docker-compose/plugins-dms.yml`, an overlay a deployment adds with `-f` exactly as `bootstrap-dms.yml` and `local-dms-diagnostics.yml` are already added when their feature is in use.
`DMS_PLUGINS_MOUNT_SOURCE` is declared with `:?` rather than a default for the same reason: a deployment that composed the overlay in meant to supply a path, and a silent default would recreate the empty directory the overlay exists to avoid.
The base compose files are therefore unchanged, which is the same property [What the Stock Image Must Ship](#what-the-stock-image-must-ship) claims for the image.

**Recipe 2, a pinned package fetched by the deployment.**
A deployment that would rather pull a published package than provision a host volume does so in a step that runs before DMS and owns nothing DMS owns: an init container on Kubernetes, a one-shot service Compose's `depends_on: condition: service_completed_successfully` orders ahead of DMS.
The step downloads the `.nupkg`, verifies it against a digest the operator pinned, extracts the plugin directory out of it into a volume, and exits; DMS then mounts that volume read-only exactly as in Recipe 1.

```yaml
# eng/docker-compose/plugins-fetch-dms.yml
services:
  fetch-plugins:
    # Pinned by digest, as every third-party image in eng/docker-compose/ already is
    # (postgresql.yml:8 pins postgres:16.8-alpine by digest). The operator substitutes
    # the digest of the alpine 3 image they have approved; a mutable tag would leave the
    # step that verifies the package digest running on bytes nobody pinned.
    image: alpine:3.XX@sha256:<digest>
    environment:
      PLUGIN_PACKAGE_URL: ${ACME_PLUGIN_PACKAGE_URL}
    volumes:
      - plugins:/out
    command: >
      sh -ec '
        wget -qO /tmp/acme.nupkg "$$PLUGIN_PACKAGE_URL" &&
        echo "3f2a...  /tmp/acme.nupkg" | sha256sum -c - &&
        rm -rf /out/Acme.Dms.Identity &&
        unzip -q /tmp/acme.nupkg "contentFiles/any/any/Acme.Dms.Identity/*" -d /tmp/x &&
        mv /tmp/x/contentFiles/any/any/Acme.Dms.Identity /out/'
  dms:
    depends_on:
      fetch-plugins:
        condition: service_completed_successfully
    volumes:
      - plugins:/app/plugins:ro
volumes:
  plugins:
```

`PLUGIN_PACKAGE_URL` is the package's download address in the feed's `PackageBaseAddress` form, `<base>/<id>/<version>/<id>.<version>.nupkg` in lower case, where `<base>` is what the feed's `index.json` publishes for that resource; it differs between feed hosts, which is one more reason the URL belongs to the deployment and not to DMS.
The Kubernetes form is the same four commands in an `initContainers` entry writing to an `emptyDir` the DMS container mounts `readOnly: true`.
`docs/OPERATIONS.md` carries both forms verbatim, and the end-to-end proof in [Testing Strategy](#testing-strategy) runs the Compose one.
Recipe 2's overlay carries the whole `fetch-plugins` service, the named volume, and the DMS service's mount, so it is an alternative to Recipe 1's overlay rather than an addition to it: `/app/plugins` is one mount target and two overlays cannot both claim it.
A deployment picks one recipe per DMS service, which is why the end-to-end proof runs the two recipes as two deployments.

Three properties of this recipe are why it is the recipe rather than a DMS feature.
It keeps the plugin root **read-only to the runtime identity** in every deployment, so Control 1 in [Trust Model and Verification](#trust-model-and-verification) holds without exception, and it is compatible with `readOnlyRootFilesystem`, which a fetcher writing into `/app/plugins` on the container filesystem is not.
Feed credentials, proxies, and mirrors are the deployment's business, so a private feed works the day an operator needs one, with the same secret-mounting facilities they already use for everything else.
And the integrity step, `sha256sum -c` against a value the operator wrote down, is exactly the control a DMS-owned fetcher would have implemented, minus the code.

**Clearing `<PluginRoot>/<Name>/` before extracting** is part of the recipe rather than tidiness, and the recipe above does it.
It is what makes the digest a statement about what is **on disk** rather than only about what was downloaded.
Without it, changing a pinned version would leave files from the previous version sitting beside the new ones, covered by no digest and referenced by no `.deps.json`, and the next operator to read that directory would have no way to tell which version they were looking at.

**A plugin package is asset-only.**
It carries an already-published directory under `contentFiles/any/any/<PluginName>/` and contains no `lib/` or `ref/` entries, mirroring the rule the ApiSchema package shape already states.
A conventional library package declares dependencies and expects a restore to resolve them, and nothing on the deploy path runs a restore.
Flattening a dependency graph into a runnable directory is what `dotnet publish` does, and the implementer is the party who should run it: they own the closure, they can see the conflicts, and they can test the result.
The package is transport for a directory that was already proven to run, and `unzip` is all it takes to get it back out.

**A DMS-owned fetcher was designed and is deferred, not rejected.**
[Rejected Alternatives](#rejected-alternatives) records the shape it would take, a name-keyed `Plugins:Packages` map beside `Allowed` served by a generalized `ApiSchemaDownloader`, and the reasons it does not ship in the first release: the recipe above delivers the same pinned, verified acquisition with no DMS code, the fetcher is the only foundation story that would change a shipped load-bearing CLI, and its two advantages over the recipe, one fewer container and one fewer place to write the digest, are real and small.
[Out of Scope and Deferred](#out-of-scope-and-deferred) states what would bring it back.

### Discovery

For each name in `Plugins:Allowed`, in order, the loader resolves `<PluginRoot>/<Name>/<Name>.dll`, verifies it, loads it, and reflects over its public exported types for an `EdFiApiPlugin` subclass.

Discovery is driven by the **allowlist**, not by a directory scan.
ODS scans a folder and filters what it finds by marker interface.
Driving from the allowlist means a directory nobody asked for is never opened, never probed, and never has its metadata read, and it converts "the operator misspelled the directory name" from a silent skip into a named startup failure.

Only the entry assembly is reflected over.
Naming it by convention removes the question ODS answers with a throwaway probe context and a forced garbage collection.

### Load Isolation

Each plugin loads into **its own non-collectible `AssemblyLoadContext`**, backed by an `AssemblyDependencyResolver` over the plugin's `.deps.json`.
Resolution is **host-first**: any assembly the host itself carries is served from the default context, and the plugin's own copy is used only for assemblies the host does not have.

**Host-first resolution asks by simple name, and compares versions itself.**
This is the one place the design cannot delegate to the default binder, and a probe settled why.
`AssemblyLoadContext.Default.LoadFromAssemblyName` throws `FileNotFoundException` in **both** of the cases that need opposite treatment: when the host does not carry the assembly at all, and when the host carries an *older* version than the plugin's reference declares.
Measured on `net10.0`: a request for `System.Runtime, Version=99.0.0.0` against a host carrying `10.0.0.0` throws `FileNotFoundException`, with or without the public key token, and so does a request for a non-strong-named `HostLib, Version=2.0.0.0` against a host carrying `1.0.0.0`.
A `catch (FileNotFoundException)` that falls through to the resolver therefore hands a version-skewed plugin its private copy silently, which is precisely the identity split host-first exists to prevent.
A `catch (FileLoadException)` arm beside it never runs.

So the loader asks the default context by **simple name only**, which carries no version constraint and cannot fail on version grounds, and then compares what came back against what the plugin asked for.

```csharp
internal sealed class PluginLoadContext(string pluginName, string entryAssemblyPath)
    : AssemblyLoadContext(pluginName, isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(entryAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Ask by simple name so the default binder's own version check cannot turn
        // "the host carries an older copy" into "the host does not have it".
        Assembly? hostAssembly = TryLoadFromHost(assemblyName.Name!);

        if (hostAssembly is null)
        {
            // Genuinely plugin-private: the host carries nothing by this name.
            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        Version? hostVersion = hostAssembly.GetName().Version;
        if (assemblyName.Version is { } requested && hostVersion is not null && hostVersion < requested)
        {
            // The host has this assembly and carries less than the plugin asked for.
            // Falling through to the plugin's private copy is the identity split
            // host-first exists to prevent.
            // Name is the base class's own copy of pluginName; reading the primary-constructor
            // parameter here instead would capture it a second time (CS9107, an error under
            // this repository's TreatWarningsAsErrors).
            throw new PluginVersionSkewException(Name!, assemblyName.Name!, requested, hostVersion);
        }

        // Anything the host can serve wins, so every type the host and the plugin
        // exchange has one identity.
        return hostAssembly;
    }

    private static Assembly? TryLoadFromHost(string simpleName)
    {
        try
        {
            return Default.LoadFromAssemblyName(new AssemblyName(simpleName));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
```

**A version the host cannot serve is fatal, not a fallback**, and the fatal names the plugin, the assembly, the version requested, and the version the host carries.
The host's copy is served whenever it is greater than or equal to what the plugin declared, which is the direction the upgrade story needs and the direction the default binder would have allowed anyway.
`Microsoft.Extensions.*` assemblies keep a stable assembly version across a major, so this bites on major skew - a plugin built against 11.x packages running in a 10.x host - which is the expected case when DMS lags a major and a vendor does not.

**The check runs eagerly, before any type is loaded, and the `Load` override is the backstop.**
`Load` is called lazily, when a type resolution first needs an assembly, so a skewed dependency a hook happens not to touch would otherwise be discovered on a request rather than at startup, and "fatal at load" would be a promise the design does not keep.
The loader therefore runs a **preflight** over the plugin's `.deps.json` before constructing the plugin: for every `runtime` entry it declares, it reads the entry's `assemblyVersion`, asks the host for that simple name, and applies the same comparison.
Every assembly the plugin ships is covered, which is every assembly the plugin's closure resolved at publish time; the `Load` override then covers anything the manifest did not name.
The two share one comparison, so there is one rule and two moments at which it fires.

**Throwing from inside `Load` is wrapped, and the loader unwraps it.**
Measured: an exception raised inside the `Load` override reaches the caller as a `FileLoadException` naming the assembly, with the thrown exception preserved as its `InnerException`.
The loader unwraps that chain when it reports, so an operator reads "plugin 'Acme.Dms.Identity' requires HostLib >= 2.0.0.0, host carries 1.0.0.0" rather than "An operation is not legal in the current state."

**Unmanaged dependencies are supported, and their failure is lazy by construction.**
A plugin carrying a cloud SDK or a database client carries `runtimes/<rid>/native/` assets, and nothing in the managed `Load` override reaches them.
`PluginLoadContext` therefore also overrides `LoadUnmanagedDll` and returns `_resolver.ResolveUnmanagedDllToPath(unmanagedDllName)`, falling back to the base implementation when the resolver has no answer.
DMS's own image already ships a `runtimes/` tree for `Microsoft.Data.SqlClient` (`src/dms/Dockerfile:81`), so this is ordinary rather than exotic, and declaring it unsupported would rule out the SDKs the three plugin types are most likely to use.
Host-first does not apply: a native library has no assembly identity to split, and each plugin's context resolves its own.
What the design does **not** do is verify at load that a declared native asset exists for the image's RID, because `.deps.json` describes native assets per RID and the runtime resolves them lazily; a missing one surfaces as a `DllNotFoundException` on first use, and [Startup Failure Semantics](#startup-failure-semantics) records that as a known gap rather than implying a check that does not exist.

**Host-first is not a preference. An enumerated set of shared assemblies does not work, and the failure is not exotic.**

The natural alternative is to unify a named list - the Ed-Fi contracts plus the abstractions that appear in the hook signatures - and let the resolver serve everything else from the plugin directory.
That alternative was built and run.
A `net10.0` class library referencing `Microsoft.Extensions.Options.ConfigurationExtensions` and `Microsoft.Extensions.Configuration`, published `--no-self-contained`, emits its own copies of seven assemblies:

```text
Microsoft.Extensions.Configuration.Abstractions.dll      Microsoft.Extensions.Options.ConfigurationExtensions.dll
Microsoft.Extensions.Configuration.Binder.dll            Microsoft.Extensions.Options.dll
Microsoft.Extensions.Configuration.dll                   Microsoft.Extensions.Primitives.dll
Microsoft.Extensions.DependencyInjection.Abstractions.dll
```

Only the two Abstractions assemblies are in the hook signatures.
`Microsoft.Extensions.Primitives` is not, and it holds `IChangeToken`, which is the return type of `IConfiguration.GetReloadToken()` and `IConfigurationProvider.GetReloadToken()`.
Unifying the Abstractions while isolating Primitives splits `IChangeToken` into two identities across an interface the host calls.
Running the same plugin under both strategies:

```text
enumerated set / Phase A -> TypeLoadException: Method 'GetReloadToken' in type
                            'Microsoft.Extensions.Configuration.Memory.MemoryConfigurationProvider'
                            does not have an implementation.
enumerated set / Phase B -> TypeLoadException: Method 'GetReloadToken' in type
                            'Microsoft.Extensions.Configuration.ConfigurationSection'
                            does not have an implementation.
host-first     / Phase A -> configuration value supplied by the plugin, read back correctly
host-first     / Phase B -> service registered by the plugin, resolved, options bound correctly
```

The plugin **loads cleanly** in every case and fails when the hook runs.
The plugin that fails is the most ordinary one possible: it reads configuration and binds options, which all three plugin types do.
An enumerated set would therefore have to grow to cover the whole `Microsoft.Extensions.*` surface plus anything a future hook signature touches, and every omission is a `TypeLoadException` an implementer hits and DMS has to diagnose.
Host-first states the intent directly and has no list to maintain.

**What this keeps and what it gives up.**
A plugin still gets its own copy of anything the host does not carry, which is where real collisions live: two vendors shipping different versions of an HTTP client, a cloud SDK, or a serializer.
What it gives up is a plugin overriding a version of an assembly the host **does** have.
That is the correct trade: a plugin cannot silently displace a host **assembly**, which was the standing objection to drop-in loading in the first place, and the host's version is the one the host was tested against.
Displacing a host **service** is a different question with a different answer, and [Contract Cardinality](#contract-cardinality) closes it.

**The consequence is that the host's whole assembly closure is part of the compatibility surface, and it is published as such.**
A plugin is compiled against the contract packages, but under host-first it is *served* every assembly the host carries, whether or not that assembly appears in a hook signature, and a request for a higher version of any of them is fatal.
That closure changes with every DMS release and no contract package announces it.
Think of a guest cooking in someone else's kitchen: they may bring any gadget the kitchen lacks, but if the kitchen already owns a knife they use the kitchen's knife, so they need to know what the kitchen owns before they pack.
Each DMS release therefore publishes a **host assembly manifest**: the assembly name and version of every managed assembly the host can serve, attached to the GitHub release beside the SBOM, with the contract versions that release carries stated at the top.

**The manifest is generated from the built runtime image, not from the build stage's `.deps.json`.**
Two facts rule the obvious approach out.
`src/dms/Dockerfile:56-57` publishes `--self-contained false`, so the produced `.deps.json` omits every `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` assembly - including `Microsoft.Extensions.Configuration.Abstractions` and `Microsoft.Extensions.DependencyInjection.Abstractions`, the two in the hook signatures - and a manifest built from it would omit exactly what an implementer most needs.
And `/app/Frontend` exists only in the build stage: the runtime stage copies into `/app` (`src/dms/Dockerfile:75-86`), the NuGet-based image unzips the `EdFi.Api` package's `DataManagementService/` folder into `/app` (`src/dms/Nuget.Dockerfile:23`), and the release artifact carries that same folder.
The generator therefore runs against the built image and emits three sections: every managed assembly under `/app` with its `AssemblyVersion`; the two shared frameworks the app's `runtimeconfig.json` names, each with the version the image actually carries and every assembly in its shared-framework directory; and, at the top, the runtime base image reference and digest the Dockerfile pins, since that is what fixes the second section.

The implementer guide states that the compatibility surface is the contract packages **plus** that manifest, and that a plugin targeting a dependency newer than the manifest lists will be refused at load.
The inventory event in [Observability](#observability) records the same substitutions after the fact; the manifest is what lets an implementer avoid them before deploying.

**Contract versioning follows from host-first and is why the contract is a class.**
A plugin compiled against `EdFi.Api.Plugins` 1.0 running in a host carrying 1.1 binds against the host's copy.
See [The Plugin Contract](#the-plugin-contract) for why that is safe.

**The cost is that plugin types are invisible to name-based resolution.**
Anything that takes a type name and searches the default context will not find a type a plugin brought.
This design does not bump into that, because the host holds the instance the loader constructed and calls methods on it directly, but any future contributor surface has to respect the same constraint.

Contexts are non-collectible because nothing unloads them.

One consequence of routing through `Default.LoadFromAssemblyName` is that any `AssemblyLoadContext.Default.Resolving` handler the host registers will also run for a plugin's requests.
DMS registers none today; if one is ever added, it becomes part of the host-first surface and must not resolve into a plugin directory.

### The Plugin Contract

Shipped from `EdFi.Api.Plugins`, following the naming DMS-1432 established for `EdFi.Api.CustomValidation`.
The base class is named for the package prefix rather than for DMS, because CMS hosts the same loader and the same contract, and a type named for one of the two hosts would be wrong on the other.
The name is unchangeable once the package is published, so it is settled here rather than during implementation.

```csharp
public abstract class EdFiApiPlugin
{
    /// Must equal the plugin directory name. Verified at load; a mismatch is fatal.
    public abstract string Name { get; }

    /// Phase A. Override to contribute configuration sources before configuration is read.
    /// Not in the first published contract; added by the secrets foundation stories.
    /// See The Two Composition Phases.
    public virtual void ContributeConfiguration(
        IConfigurationBuilder configurationBuilder, IConfiguration bootstrapConfiguration) { }

    /// Phase B. Override to contribute service registrations before the container is built.
    public virtual void ContributeServices(
        IServiceCollection services, IConfiguration configuration) { }
}
```

**An abstract class with virtual no-ops, rather than a marker interface plus optional contributor interfaces.**
Three things follow, and the third is the reason.

The host calls both phases unconditionally.
There is no `is IConfigurationContributor` test, no per-phase discovery, and no way for a plugin to be allowlisted and silently contribute nothing to a phase the operator expected.

The implementer overrides one method.
A secrets plugin that injects a configuration source and registers a resolver overrides both; a validator plugin overrides one and never sees Phase A.

**Adding a phase later is binary-compatible with plugins already published.**
This is the decisive property under host-first resolution.
A third-party plugin is compiled against one version of the contract and then binds against whatever version the host carries, and those versions will differ, because the whole point is that the operator upgrades DMS without rebuilding the plugin.
Adding an abstract member to an **interface**, or a member without a body, breaks every implementation compiled against the previous version: the type no longer satisfies the interface and fails to load.
Adding a virtual method with a no-op body to a **base class** breaks nothing, and neither does adding a **default interface method** with a body, which C# has supported since version 8.
The two shapes are therefore equally capable of additive evolution, and the class is chosen as a preference rather than a necessity: it has one way to add a member, it needs no explicit-implementation rule for an implementer to get wrong, and the host calls the member on a concrete instance without the interface-dispatch caveats a default method carries.
What is a necessity is the **policy**, and it binds the per-type contracts as much as this one.

The stated compatibility policy is additive-only **for the life of the package**: new virtual members with no-op bodies, never a new abstract member, never a signature change, never a removal.
Not "within a major version".
The loader compares `AssemblyVersion`s and the default binder binds a reference to `1.3.0.0` against a loaded `2.0.0.0` without complaint, so a 2.0 that removed or changed a virtual would load a 1.x plugin cleanly and fail with `MissingMethodException` inside a hook, which is exactly the raw error the check below exists to prevent.
A major-must-match check would close that, at the cost of forcing every vendor to rebuild on every contract major, which is the rebuild the design exists to remove.
So a breaking change to this contract is not a version bump; it is a **new package id**, with a new base class the loader discovers alongside the old one for as long as both are supported.
The contract is one base class with no-op virtuals, so there is no realistic pressure toward a breaking change, and "never" is a commitment the shape can keep.
That makes an **older plugin on a newer host** supported by construction across any number of releases, which is the direction the upgrade story needs.
The reverse direction is not supported and is not silently attempted: a plugin compiled against a contract package N+1 cannot run on a host carrying N, because a member it overrides may not exist.
The loader reads the entry assembly's references to every declared contract assembly before it loads a single type out of it, so that case is a named fatal, "plugin requires EdFi.Api.Plugins >= X, host carries Y", rather than a raw type-load error an implementer has to interpret.

**The same policy applies to every per-type contract a plugin implements, and those are interfaces.**
`ICustomResourceValidator` ships in `EdFi.Api.CustomValidation` and is resolved host-first exactly like `EdFiApiPlugin`, so a member added to it without a body would break every published validator in the same way.
Each companion document inherits this rule as a constraint on its contract's shape: a member added to a plugin-implemented interface after first publication carries a default implementation, and a member that cannot be defaulted is a new package id.
The rule is stated here once so that no companion document rediscovers it, and the implementer guide states it to implementers.

**The skew preflight covers every contract package, not only `EdFi.Api.Plugins`.**
The named fatal below reads the entry assembly's references, and it reads all of them that name a declared contract assembly: `EdFi.Api.Plugins` today, `EdFi.Api.CustomValidation` as soon as a validator plugin exists, and each per-type contract as its companion document adds one.
The host holds the contract set already, because [Contract Cardinality](#contract-cardinality) makes it host-held metadata, so "which references are contract references" needs nothing new.
Without that, a validator compiled against a newer `EdFi.Api.CustomValidation` would load cleanly on an older host and fail with a `MissingMethodException` on the first matching write, which is the raw error the check exists to prevent.

**Every contract package is versioned on its own, not on the DMS release.**
The skew check reads an `AssemblyVersion`, so that version has to mean something: it must move when the contract surface moves and must **not** move when it does not.
Tying it to the DMS release version fails the second half.
`build-dms.ps1` regenerates `src/dms/Directory.Build.props` with `VersionPrefix` equal to the release version (`SetDMSAssemblyInfo`), so every assembly under `src/dms/` carries the release as its `AssemblyVersion`.
Under that scheme a vendor compiling against the contract shipped with 8.4 would be refused by an 8.3 host whose contract surface is identical, and the fatal would name two versions that differ in nothing an implementer can act on.

`EdFi.Api.Plugins` therefore carries its own semantic version, `1.0.0` for the first published contract and `1.1.0` when `ContributeConfiguration` lands, declared in `src/plugins/Directory.Build.props` and deliberately outside `SetDMSAssemblyInfo`'s reach.

**`EdFi.Api.CustomValidation` needs the same treatment, and today it does not have it.**
It is packed with `-p:PackageVersion=$DMSVersion` (`build-dms.ps1:1832`) and inherits its `AssemblyVersion` from the regenerated `src/dms/Directory.Build.props`, so a plugin compiled against the 8.4 contract would be refused by an 8.3 host over a surface that never changed.
The argument that produced the independent version for `EdFi.Api.Plugins` applies unchanged to every package a plugin compiles against, so it applies here.
The project stays where it is - it is merged, it is referenced by Core, and moving it would reopen a shipped story for no gain - and instead declares `Version`, `AssemblyVersion`, and `FileVersion` in its own csproj, which wins over the imported `Directory.Build.props`, with the pack target using the project's version rather than `$DMSVersion`.
`EdFi.Api.CustomValidation` starts at `1.0.0`, matching the surface DMS-1432 shipped.
The [divergence ledger](#divergence-from-the-custom-validation-epic) records this as an amendment to a merged story, and the publishing story owns it.

For each contract package, `AssemblyVersion`, `FileVersion`, and `PackageVersion` are one value, and the pack lane asserts the packed assembly's `AssemblyVersion` equals the package version.
The common .NET convention of holding `AssemblyVersion` at `major.0.0.0` is ruled out for the same reason: under it a plugin built against 1.3 records a reference to `1.0.0.0`, a 1.0 host satisfies it, and the missing member surfaces as a `MissingMethodException` inside a hook.
Without the pack-lane assertion the check is silently blind and nothing else in the design notices.
The release notes for each DMS version state which contract versions that version carries, and the host assembly manifest states them at the top, which is the one fact an implementer needs to pick a target.

The direction the binder allows is the direction the design needs.
The default context binds a reference to `1.0.0.0` against a loaded `1.3.0.0` without complaint, so an older plugin on a newer host still loads, and a reference to `1.3.0.0` against a loaded `1.0.0.0` is refused, which is the case the loader turns into the named fatal.

The entry assembly must expose exactly one public non-abstract `EdFiApiPlugin` subclass with a public parameterless constructor.
Zero is fatal, because the operator allowlisted a directory that contributes nothing.
More than one is fatal, because choosing between them would be arbitrary.

Inside the hooks a plugin writes ordinary registration code, the same code DMS-1434 already asked implementers to write, called by the loader instead of by a composition root:

```csharp
public override void ContributeServices(IServiceCollection services, IConfiguration configuration)
{
    services.Configure<AcmeOptions>(configuration.GetSection("Acme"));
    services.TryAddEnumerable(
        ServiceDescriptor.Transient<ICustomResourceValidator, StudentIdentityValidator>());
}
```

### The Two Composition Phases

DMS needs contributions at two moments, and a mechanism offering only one cannot serve secrets at all.

**Phase A, configuration contribution.**
A plugin that supplies configuration values must supply them before anything reads configuration, and no DI extensibility reaches that point.
That must happen after `WebApplication.CreateBuilder(args)` and before `builder.AddServices()`, which is the first thing that reads a plugin-resolvable configuration value: `AddServices` calls `ConfigureLogging()` on its first line (`Infrastructure/WebApplicationBuilderExtensions.cs:38`), which reads the `Serilog` section.

One key is read earlier and is deliberately out of Phase A's reach.
`Program.cs:30-33` reads `AppSettings:StartupStatusFilePath` to construct the bootstrap status signal, before plugin loading has happened at all, and it must: that signal is how a loader fatal is reported, so it cannot depend on anything a plugin contributed.
`docs/CONFIGURATION.md` states the exception rather than leaving an operator to discover that one key ignores a plugin source.

**Phase B, service contribution.**
Validators, identity services, secret resolvers, and secret hashers are DI registrations, contributed while `IServiceCollection` is still open.

Both phases run inside `Program.cs` before `builder.Build()`, so **one load serves both**.

**Phase A is decided here and built with the secrets foundations, not with this spike's stories.**
Nothing on this spike's table consumes it: custom validation and identity are Phase B.
The reason it is designed now is that the secrets spike must build on a mechanism that is *decided*, and a design that settled only Phase B would have to be amended, along with its in-flight stories, the moment secrets needed configuration.
That argument needs the design to exist; it does not need the code to exist.
And the base class was chosen precisely so that a phase added later is binary-compatible with every plugin already published, so shipping Phase A code ahead of its first consumer would spend the property the class shape buys for no consumer.
Concretely: the first published `EdFi.Api.Plugins` carries `Name` and `ContributeServices` only.
The secrets spike's first foundation story adds `ContributeConfiguration` as the first exercise of the additive policy, together with the loader's Phase A invocation, the source placement below the operator's explicit sources, the additive `Sources` guard, the Contribute row of the cardinality table, the `docs/CONFIGURATION.md` precedence order, and the Phase A rows of the test plan.
The member is withheld rather than shipped uninvoked because a virtual the host never calls is a hook a plugin can override to no effect, which is the silent no-op this design refuses everywhere else.
Every Phase A statement in this document is therefore a decision the secrets spike inherits, and the [Level of Effort](#level-of-effort) table marks the rows it owns.

The sketch below is the **end state**, after the secrets foundation story has added Phase A.
The host-integration story in this spike ships everything in it except the `ContributeConfiguration` line.

```csharp
var builder = WebApplication.CreateBuilder(args);
var bootstrapStartupStatusSignal = new FileStartupStatusSignal(
    builder.Configuration.GetValue<string>("AppSettings:StartupStatusFilePath"), Console.Error);

RunBootstrapPhase(
    DmsStartupPhases.LoadPlugins,
    "Loading plugins.", "Loaded plugins.",
    "Loading plugins failed before the application host was built.",
    () =>
    {
        loadedPlugins = PluginLoader.Load(builder.Configuration);
        // Added by the secrets foundation story, together with the member it calls.
        loadedPlugins.ContributeConfiguration(builder.Configuration);
    });

RunBootstrapPhase(DmsStartupPhases.ConfigureServices, /* ... */, () =>
{
    builder.Services.AddHttpClient();
    builder.AddServices(loadedPlugins);
});
```

**Diagnostics do not depend on a logger, and the loader does not depend on DMS's bootstrap scaffolding.**
Plugin loading runs before the logger exists.
Serilog's static API is not available as a fallback: DMS configures Serilog through `webAppBuilder.Logging.AddSerilog(configureLogging, dispose: true)` (`Infrastructure/WebApplicationBuilderExtensions.cs:125`) without assigning the static `Log.Logger`, so a `Log.Warning` at load time would write nowhere.
The loader therefore writes its own outcomes to `Console.Error` and throws on failure, which needs nothing from either host.
DMS additionally wraps the call in `RunBootstrapPhase`, inheriting the structured, machine-readable, CI-collected startup record it already produces (`Program.cs:443`).
Load outcomes are re-emitted through the real logger once it exists.
This is what makes [Applicability to the Configuration Service](#applicability-to-the-configuration-service) cheap: the wrapper is a host's choice, not the loader's requirement.

**Phase A is deliberately narrower than the ODS equivalent.**
ODS hands a plugin the whole `IHostBuilder`, which lets it replace the service provider factory, add hosted services, and reconfigure logging, none of which is the stated purpose.
DMS hands it an `IConfigurationBuilder`, so a Phase A plugin can reach configuration and nothing else.
Anything a plugin wants to register belongs in Phase B, where it is visible to the startup guard.

**Handing over an `IConfigurationBuilder` buys configuration-only. It does not buy additive-only.**
`IConfigurationBuilder.Sources` is a mutable list, so a plugin holding the builder can remove or reorder sources it did not add.
Control 4 in [Trust Model and Verification](#trust-model-and-verification) holds regardless, because `Plugins` was consumed to decide what loads before Phase A ran, so no removal or reordering can reach the allowlist.
What is exposed is everything else: a plugin that drops the `appsettings.json` source, or lifts its own source above another plugin's, changes how the whole host resolves configuration and leaves no trace of having done it.
The loader therefore snapshots `Sources` before each Phase A call and compares it after, and it is **fatal** if any pre-existing source was removed or moved, naming the plugin.
Adding is the only permitted operation, and the guard is what makes the narrowing a property rather than an expectation.

**An operator's explicit override outranks a plugin, and there are two explicit surfaces, not one.**
A plugin calling `configurationBuilder.Add(source)` appends, which would place its source above **both** of the operator's explicit surfaces, so `Plugins__Allowed` is not the only thing at stake; `dotnet EdFi.DataManagementService.Frontend.AspNetCore.dll --AppSettings:Foo=bar` would lose to a plugin as well.
Re-adding the environment source after Phase A, which was the earlier decision here, restores one surface and forgets the other.

**Where a plugin's source has to sit is an empirical question, and the answer is not where an obvious reading of `CreateBuilder` suggests.**
`WebApplication.CreateBuilder(args)` (`Program.cs:29`) does not install five sources.
Measured on `net10.0`, it installs twelve, in this order, later winning over earlier:

```text
[0]  Memory                                    [6]  Json  appsettings.{Environment}.json
[1]  EnvironmentVariables  prefix ASPNETCORE_  [7]  Json  <assembly>.settings.json
[2]  Memory                                    [8]  Json  <assembly>.settings.{Environment}.json
[3]  EnvironmentVariables  prefix DOTNET_      [9]  EnvironmentVariables  no prefix
[4]  CommandLine                               [10] CommandLine
[5]  Json  appsettings.json                    [11] Chained (the host's own configuration)
```

In a Development environment a user-secrets source sits between `[8]` and `[9]`.
The two host-level sources at `[1]` and `[3]` are prefix-filtered and sit **below** `appsettings.json`, and so does the host's own command-line source at `[4]`.
The operator-facing environment and command-line surfaces are `[9]` and `[10]`.
Placing a plugin's sources "immediately below the first environment variable source" would put them at index `[1]`, under `appsettings.json`, where the empty `ConfigurationServiceSettings:ClientSecret` and `:EncryptionKey` that `appsettings.json` ships would shadow the values a vault plugin supplied.
That is the exact inversion of the commitment this section makes.

The loader therefore places plugin sources rather than trusting where `Add` put them.
After each Phase A hook returns and the additive guard above has passed, the loader moves the sources that plugin appended to sit **immediately below the last environment variable source** that `CreateBuilder` installed, which is the unprefixed one at `[9]`, preserving their relative order.
"Last environment variable source" is the rule rather than an index, because the count varies with the environment and the runtime is free to add sources; the loader finds the highest-indexed `EnvironmentVariablesConfigurationSource` present when Phase A began and inserts there.
Among plugin sources, later still wins; all of them sit above every JSON source; and none of them sits above the operator's environment or command-line source, because those two were never moved.
Probe-verified end to end on `net10.0`: with that placement, a key set only by the plugin resolves to the plugin's value even when `appsettings.json` carries an empty string for it, a key also set in the environment resolves to the environment's, and a key also passed on the command line resolves to the command line's.

The loader adds no source of its own, and nothing waits for the `AddEnvironmentVariables()` call inside `AddServices` (`Infrastructure/WebApplicationBuilderExtensions.cs:46`), which sits eight lines after `ConfigureLogging()` has already read the `Serilog` section (`:38`) and would have left one section resolved under a different precedence than every other.
That call appends a thirteenth source above everything, so in DMS as shipped the unprefixed environment already outranks the command line for every key read after `AddServices` has run; the placement above does not depend on it and does not change it.
The reordering guard applies to plugins and not to the loader: the loader is host code, it moves only what the plugin just added, and the sources that were there before the hook keep their positions and their order.

The rationale is operational.
An operator who sets a value in the environment or on the command line must not find it silently stopped working because they installed a plugin for an unrelated purpose.
It is also the more secure default, since those are the surfaces the deployment controls and a plugin source is the surface third-party code controls.
The accepted trade is that a value present in the environment beats the vault, which is the correct reading of "explicit override": an operator who wants the vault to supply a value does not also set that value in the environment.
`docs/CONFIGURATION.md` states the resulting order - environment variables, then command-line arguments, then plugin sources in allowlist order, then `appsettings.json` and the other JSON sources - rather than leaving it to be discovered, and states that DMS's own `AddEnvironmentVariables()` at `Infrastructure/WebApplicationBuilderExtensions.cs:46` is why the environment sits above the command line rather than below it.

### Contract Cardinality

Each plugin contract declares one of three cardinalities, and cardinality is metadata the **host** holds about each contract, not something a plugin declares, so a plugin cannot opt out of being counted.

| Cardinality | Meaning | Contracts | Registration | Conflict |
| --- | --- | --- | --- | --- |
| **Fan-in** | N implementations, all invoked | `ICustomResourceValidator`, shipped in DMS-1432 | `TryAddEnumerable`, transient | None; more is more |
| **Replace** | 0 or 1, displacing a host default | identity service (DMS-1412); secret resolver and `IClientSecretHasher` come from the secrets spike | Single registration | **Fatal.** Two claims aborts startup, whether they come from two plugins or from one plugin registering twice |
| **Contribute** | N configuration sources, order declared | Phase A sources | Placed in allowlist order below the operator's explicit sources | None; later wins among plugin sources, and the order is the operator's. Command-line arguments and environment variables sit above all of them |

**Attribution comes from a recording wrapper, and the replace-conflict case needs one thing more.**
The loader hands each plugin an `IServiceCollection` implementation that delegates to the real one and records every descriptor that plugin adds, removes, or overwrites.
The plugin writes ordinary code and every extension method works unchanged, because they all extend `IServiceCollection`.

The wrapper is necessary rather than convenient, because attribution by diffing the collection cannot say *which* plugin added what once more than one has run, and because the removal rules below need to fire before a call reaches the real collection rather than after.

**A declined `TryAdd` is invisible at this seam, and the design says so rather than promising otherwise.**
An earlier revision had the wrapper record every `TryAdd`-family call that declined, with the service type and implementation involved.
A probe showed that is not implementable.
`ServiceCollectionDescriptorExtensions.TryAdd` reads `Count` and the indexer, compares the candidate's `ServiceType` against what it finds, and returns without ever handing the candidate to the collection, so a declined `TryAddSingleton<IFoo, Foo2>` reaches the wrapper as an anonymous scan and `Foo2` is never seen at all.
`TryAddEnumerable` sweeps the whole collection the same way, so even the service type under consideration is ambiguous.
No wrapper over `IServiceCollection` can recover that information, and a design that claimed to would have shipped an acceptance criterion nothing could satisfy.

**So the design removes the decline instead of trying to observe it.**
Cardinality is host-held metadata, so the host knows exactly which service types are **replace**-cardinality contracts, and there are few of them.
The wrapper presents each plugin a view of the collection in which descriptors for replace-cardinality contracts are **masked**: `Count`, the indexer, and enumeration skip them, whoever registered them.
A `TryAdd` against a replace contract therefore never finds a match, always calls `Add`, and always lands in the wrapper with the full descriptor.
Every claim on a replace contract is then attributed, and two claimants, whether from two plugins or from one plugin registering twice, are both named in the fatal.
Nothing is removed from the real collection; the masking is a read-only projection that lasts for the duration of one plugin's hook.

The masking is narrow on purpose.
Fan-in contracts are **not** masked, because `TryAddEnumerable`'s deduplication by implementation type is behaviour a fan-in registration legitimately relies on, and because duplicate fan-in entries are harmless by that row's own definition.
Everything else in the collection is visible unchanged, so the vendor and framework helpers that scan it behave exactly as they do outside a plugin.
The one behavioural difference an implementer could notice is that a plugin asking "has anyone registered the identity service already?" sees no, which is a question the design forbids a plugin from conditioning on in any case: a plugin contributes, it does not negotiate.

**The wrapper records removals too, and removing a host-owned descriptor the plugin did not add is fatal.**
`IServiceCollection` is an `IList<ServiceDescriptor>`, so `Remove`, `RemoveAt`, `Clear`, and the indexer setter are on the interface, and `services.Replace(...)`, `RemoveAll<T>()`, and their relatives are extension methods that call them.
A wrapper that recorded only adds would let `services.Replace(ServiceDescriptor.Singleton<IDocumentStoreRepository, Mine>())` displace a host default while the displacement check below saw an ordinary add of a host type.
A plugin has no legitimate reason to remove a host-owned descriptor: it contributes registrations, it does not edit the host's.
So the wrapper makes any call that removes or overwrites a descriptor **that was present before the plugin's hook began and whose `ServiceType` is declared in a host assembly** fatal, naming the plugin and the service type, before the call reaches the real collection.
`Clear` is always fatal because it necessarily removes such descriptors.

The rule is scoped twice, and both scopes exist so that it never rejects a legitimate plugin.
It is scoped to pre-existing descriptors because ordinary registration helpers remove and replace their **own** descriptors as a matter of course: framework and vendor extension methods (`AddHttpClient`, cloud SDK client registrations, options builders) call `Replace` and `RemoveAll` internally over descriptors they added a few lines earlier, and the wrapper already holds the set each plugin added, so "did this plugin add it" is a lookup.
It is scoped to **host-owned service types** because the same helpers also `Replace` and `RemoveAll` over framework descriptors the host registered before the hook began, and DMS cannot enumerate every vendor SDK that does so.
A rule that made those fatal would reject an ordinary plugin the first time a vendor SDK tidied a `Microsoft.Extensions.*` registration, and the failure would name a type the implementer never wrote, which is the outcome this section's bar forbids.
Removals and overwrites of pre-existing **non-host** descriptors are therefore permitted and **recorded**: each one appears in the per-plugin inventory event in [Observability](#observability) with the service type and the implementation it displaced, which is the same treatment the displacement check below gives to a plugin registering `IHostedService` or `IConfigureOptions<T>`, and for the same reason.
The recording-wrapper story's acceptance criteria run the real helpers a plugin will call, `AddHttpClient`, `AddOptions<T>().Bind(...)`, `AddLogging`, and at least one cloud SDK's `Add*Clients` registration, through the wrapper after the host's own `AddServices` has populated it, and assert none is rejected; that is a test against the libraries as shipped, not against a fixture that imitates them.

**A plugin that registers no declared plugin contract is fatal.**
The same reasoning that makes zero `EdFiApiPlugin` subclasses fatal applies one level down.
A plugin whose hooks ran and registered only its own types has contributed nothing the host will ever call, and the operator who allowlisted it believes otherwise.
The realistic way this happens is not carelessness but the wrong host: a DMS validator plugin allowlisted on CMS.
CMS carries no `EdFi.DataManagementService.CustomValidation` assembly - the assembly published as the package `EdFi.Api.CustomValidation` - so host-first serves the plugin's private copy, `ICustomResourceValidator` registers against a type identity nothing in CMS consumes, and startup would otherwise succeed with the plugin silently inert.
The recording wrapper already holds every descriptor the plugin added and the host already holds the contract set, so the check is an intersection, and a plugin that contributed only Phase A sources satisfies it through the Contribute row rather than through a Phase B registration.

**Replace conflicts are fatal rather than last-wins, and that is a deliberate divergence from ODS**, which resolves the same situation by ordering modules on a three-value rank and letting the last one win.
The rank is `ICustomModule` implementations last, then classes whose name begins `Override`, then everything else, and `OrderBy` is stable, so two vendor plugin modules both implementing `ICustomModule` tie.
The tie is broken by the enumeration order of `AppDomain.CurrentDomain.GetAssemblies()`, which is to say by which vendor's assembly happened to load first.
So which vendor's identity service is live is decided neither by the operator nor by anything either vendor can see, which is a stronger reason to refuse the rule than the class-name reading that this section previously gave.
Two plugins claiming one replace-contract is an operator error and the operator is the only party who can resolve it.
See [ods-precedent.md](./ods-precedent.md) for the citation.

**A host service type is not a plugin's to claim.**
The host already holds the set of plugin contracts, because cardinality is host-held metadata, so it can also tell a plugin contract apart from anything else a plugin registers.
Every descriptor a plugin adds passes through the recording wrapper, so the check needs nothing the wrapper is not already collecting: a registration whose `ServiceType` is declared in a host assembly - any assembly whose **assembly name** begins `EdFi.DataManagementService.` or `EdFi.DmsConfigurationService.` - and is not a declared plugin contract is **fatal**, naming the plugin and the service type.

**The predicate keys on assembly identity, and that is not the same as package identity anywhere in this design.**
`ICustomResourceValidator` is declared in the assembly `EdFi.DataManagementService.CustomValidation`, which the csproj packs under the id `EdFi.Api.CustomValidation` (`src/dms/core/EdFi.DataManagementService.CustomValidation/EdFi.DataManagementService.CustomValidation.csproj:8`).
So the contract's own service type *does* match the host-owned predicate, and it is admitted only because the declared-contract exemption is checked first.
`EdFiApiPlugin`'s assembly is named `EdFi.Api.Plugins`, matching its package id, and does not match the predicate at all.
Wherever this document says a package id, it means the thing an implementer writes in a `PackageReference`; wherever a check reasons about what code is host-owned, it means the assembly name, and the two are written out separately for exactly this reason.
`services.AddSingleton<IDocumentStoreRepository, Mine>()` is the case this catches.
It compiles, it is legal DI, the recording wrapper sees it land, and without the check the host would silently run a third-party document store that no contract and no cardinality row ever admitted.
Types the plugin itself owns are unrestricted, and so are BCL and `Microsoft.Extensions.*` types, because options, `HttpClient`, and their like are how a plugin does ordinary work.
The check lives in the plugin startup guard, fed by the same recording wrapper as the replace-conflict check; [Where the Code Lives](#where-the-code-lives) says why that guard is a separate startup task from DMS-1434's.

**This is a guardrail against an implementer misreading the contract, not an isolation property, and it should not be described or extended as one.**
Under the trust model below a plugin has full process trust, so a party that wants to run its own document store can do so by means no wrapper sees.
What the check catches is the honest mistake: an implementer who read "contribute services" as "register whatever DMS resolves" and would otherwise learn about it from a production defect.
Host-first resolution protects assembly identity because a plugin cannot choose to be served a different host assembly; nothing here protects DI identity in that sense, and the design does not claim to.
Every wrapper and guard check in this section is a mistake-detector, and the bar for adding another is that it catches a plausible implementer error and never rejects a legitimate plugin.
Three of the fatal rules key on one test, whether a `ServiceType` is declared in a host assembly: host-owned removal and overwrite, which fires at the wrapper, and host-owned displacement and no-contract-registered, which fire in the guard.
The replace-conflict rule is the exception and does not use that test at all: it counts claims against the host's own cardinality registry, which is a closed list the host writes.
If a future revision finds itself adding a fourth rule keyed on the host-owned test, or widening that test, that is the signal it has become a policy engine over a party it cannot actually constrain, and the answer is the inventory event, not another fatal row.

**What that exemption leaves open, stated so nobody reads more into the check than it does.**
`IHostedService`, `IStartupFilter`, `IConfigureOptions<T>` over any framework options type, and `IHttpMessageHandlerBuilderFilter` are all `Microsoft.Extensions.*` or framework service types, and a plugin registering any of them reshapes the host through ordinary, permitted DI: a background task, a middleware pipeline change, a Kestrel or JSON setting, a handler on every outbound request.
The check protects **DMS-owned service identity** and nothing wider.
Restricting those types would break the ordinary work the exemption exists for, and under the stated trust model, full process trust, forbidding them would protect nothing an attacker could not do another way.
What the design owes instead is a record: every service type a plugin registers, not only the plugin contracts, appears in the per-plugin inventory event in [Observability](#observability), so an operator can see that a validator plugin also registered a hosted service without reading its source.

### Trust Model and Verification

**A loaded plugin runs with full process trust.**
It can read every connection string, every decrypted secret, every request body, and every token.
.NET has no code-access security, does not verify strong names on `LoadFromAssemblyPath`, and does not check Authenticode signatures at load on any platform.
An `AssemblyLoadContext` isolates assembly identity; it is not a security boundary.
Every control below is about **integrity of what gets loaded**, not about containing it once loaded.

**Write access to the plugin root is equivalent to code execution as the host process.**
That sentence is the threat model, and because DMS ships no fetcher, the posture is the same for every deployment: **the runtime identity never writes the plugin root.**
Both acquisition recipes in [Acquisition](#acquisition) end with the root mounted read-only into the DMS container, and whatever wrote it, an operator's publish step or a one-shot fetch step, ran as a different identity in a different container before DMS started.

**Control 1, filesystem permissions.**
The plugin root must be writable by the deployment identity and not by the runtime identity, and the `:ro` mount both recipes use is how a container deployment states that.
`docs/OPERATIONS.md` states this as a requirement rather than a recommendation.
DMS cannot verify it and does not claim to.

**Control 2, allowlist.**
`Plugins:Allowed` names each plugin directory, and nothing outside the plugin root is opened.
That holds because of a rule rather than by construction: `Path.Combine(root, name)` discards `root` entirely when `name` is rooted, and `<root>/../x/x.dll` resolves outside it, so an allowlist entry is required to be a single path segment matching `^[A-Za-z0-9][A-Za-z0-9._-]*$` and the composed path is separately checked to canonically sit under the root.
Both are fatal rows in [Startup Failure Semantics](#startup-failure-semantics).
This is a completeness control, not an integrity one: it stops an unintended directory from loading and stops nothing an attacker with write access to the configuration can do, since such an attacker can simply add a legitimate name.

**Control 3, the package digest, verified by the deployment.**
Recipe 2 pins a SHA-256 over the **`.nupkg` bytes** and refuses to extract on a mismatch, with `sha256sum -c` against a value the operator wrote down.
It is the identical control a DMS-owned fetcher would have implemented, and it sits where the download happens rather than in DMS, so the bytes that reach the plugin root are bytes the operator approved whether or not DMS was ever involved.
A feed that serves package hashes does not substitute for the pinned value: the point of pinning is that the **operator** approved these bytes, not that the feed vouches for them.

Digesting the package rather than the extracted directory is deliberate.
A `.nupkg` is a single file with a natural digest an operator computes with `shasum -a 256` and a build system emits for free.
Digesting a directory instead would mean specifying a content-manifest algorithm - path ordering, separator normalization, symlinks, case-insensitive filesystems - that every operator and every build tool would have to reimplement identically, to verify an artifact derived from one that already had a digest.

This **strengthens the shipped precedent**.
`SCHEMA_PACKAGES` pins a name, a version, and a feed, and verifies no digest at all (`src/dms/run.sh:46-54`).

**Control 4, the allowlist's own provenance.**
The allowlist governs what may execute, so it must not be readable from a source the plugin root controls.
`Plugins` is read from `appsettings.json`, environment variables, and command-line arguments only.
A Phase A plugin cannot contribute to it, because Phase A runs after the allowlist has been consumed to decide what loads.
This ordering is a security property, not an implementation detail, and is asserted by test.

**What this does not protect against**, stated so it is not rediscovered:

- A supply-chain compromise of a package the operator deliberately pinned. The digest proves the bytes did not change; it says nothing about what they do.
- An operator with write access to both the plugin root and the configuration, who updates a directory and its digest together.
- Anything a plugin does after it loads. No capability restriction, no resource limit, no audit beyond the load inventory.
- A plugin that hangs. Both hooks are synchronous and unbounded, so a plugin that blocks hangs startup. This is deliberate: a timeout would mean continuing without a plugin the operator required, which [Startup Failure Semantics](#startup-failure-semantics) refuses. What the design owes an operator instead is attribution, and [Observability](#observability) supplies it: the loader announces each hook before entering it, so the last line written names the plugin and the phase it hung in.

Stated as a whole: the bar is that an attacker must already be able to write where only the deployment identity can write, or substitute bytes matching an operator-pinned digest.
No combination of controls available in .NET lowers it further.

### Startup Failure Semantics

**An allowlisted plugin that does not load is fatal.**

| Condition | Outcome |
| --- | --- |
| Plugin root missing and `Plugins:Allowed` empty or absent | Continue silently. No plugins were asked for |
| Plugin root missing, `Plugins:Allowed` non-empty | **Fatal.** Plugins were asked for and cannot be delivered |
| Allowlisted directory missing, or entry assembly missing | **Fatal**, naming the expected path |
| A name repeated in `Plugins:Allowed`, before or after trimming | **Fatal**. The allowlist is ambiguous about what the operator approved |
| A name in `Plugins:Allowed` that is not a single path segment: rooted, containing `/` or `\\`, equal to `.` or `..`, or otherwise outside `^[A-Za-z0-9][A-Za-z0-9._-]*$` | **Fatal**, naming the entry and the rule. Checked before the path is composed, because `Path.Combine(root, "/etc")` discards the root entirely and `<root>/../x/x.dll` resolves outside it |
| A composed plugin path that does not canonically sit under the plugin root | **Fatal**, naming the entry and both paths. A second, belt-and-braces check with `Path.GetFullPath`, so the containment claim in [Trust Model and Verification](#trust-model-and-verification) rests on a comparison rather than on the name rule alone |
| Entry assembly fails to load | **Fatal**, with the load exception |
| Entry assembly references a newer version of any declared contract package than the host carries | **Fatal**, naming the plugin, the contract, the version required, and the version the host carries. Read from the entry assembly's references before any type is loaded, so it never surfaces as a raw type-load error |
| The plugin's `.deps.json` declares a higher `assemblyVersion` for an assembly the host also carries | **Fatal**, naming the plugin, the assembly, the version declared, and the version the host carries. Detected by the preflight, before the plugin is constructed. Serving the plugin's private copy would split the type identity host-first exists to keep |
| A managed assembly the plugin requests at run time resolves to a host copy older than the reference declares | **Fatal**, from the `Load` override, naming the same four things. This is the backstop for a reference the `.deps.json` preflight did not cover; the runtime wraps it in a `FileLoadException`, which the loader unwraps before reporting |
| Plugin `.deps.json` declares a `runtimepack` library, which a self-contained publish produces | **Fatal**, naming the plugin. The discriminator is the library entry, not a runtime identifier in `runtimeTarget`: `dotnet publish -r <rid> --no-self-contained` also names the RID there, and a RID-specific framework-dependent publish is legitimate and is how a plugin ships native assets |
| An unmanaged library a plugin declares cannot be resolved | **Not detected at load.** Native resolution is lazy by construction, so it surfaces as a `DllNotFoundException` on first use. Recorded here as a known gap rather than implied to be checked |
| Plugin directory has no `.deps.json` | **Fatal**, naming the plugin. Its private closure would not resolve, failing at first use rather than at load |
| Entry assembly exposes zero or multiple `EdFiApiPlugin` subclasses | **Fatal**, naming what was found |
| `EdFiApiPlugin.Name` does not match the directory name | **Fatal**, naming both |
| A contributor hook throws | **Fatal**, naming the plugin and the phase |
| A Phase A hook removes or reorders a configuration source it did not add | **Fatal**, naming the plugin. The `Sources` list is snapshotted before each call and compared after; adding is the only permitted operation |
| Two plugins register the same replace-cardinality contract | **Fatal**, naming both plugins and the contract |
| A plugin removes, replaces, or overwrites a host-owned service descriptor that was present before its hook began | **Fatal**, naming the plugin and the service type, before the call reaches the real collection. Plugins contribute registrations; they do not edit the host's. Removing or replacing a descriptor the plugin itself added is permitted, because registration helpers do that internally, and removing a pre-existing framework or third-party descriptor is permitted and recorded in the inventory event, because vendor helpers do that too |
| A plugin registers no declared plugin contract and contributes no configuration source | **Fatal**, naming the plugin and listing what it did register. It ran and contributed nothing the host will call; the likeliest cause is a plugin allowlisted on the wrong host |
| A plugin registers a service type declared in a host assembly that is not a declared plugin contract | **Fatal**, naming the plugin and the service type. A guardrail against an implementer misreading the contract, not an isolation property; see [Contract Cardinality](#contract-cardinality) |
| A registration of a declared plugin contract cannot be constructed | **Fatal**, via the plugin startup guard, which resolves every declared-contract registration once from a throwaway scope and discards the instances. This is the plugin guard's own activation probe, not the per-contract one: DMS-1434's guard activates `ICustomResourceValidator` because that contract's own story asked it to, and a contract added later would otherwise arrive with no activation at all |
| A directory under the plugin root that is not allowlisted | Warn and ignore. It was never opened |

**Fail-fatal diverges from ODS**, which catches, logs, and continues when a module cannot be constructed or registered, degrading a broken extension to silently absent.
design.md already refused that for custom validation and this document keeps the refusal.

Skip-and-continue is right when a missing plugin degrades a capability whose absence the operator can see.
For these three types the absence is invisible and what goes quiet is correctness or security posture:

- A **secrets** plugin that fails to load means the values it would have supplied are absent, so the host falls back to whatever `appsettings.json` holds. In the best case that is a startup failure somewhere less obvious; in the worst case it is a development default.
- An **identity** plugin that fails to load means the Identity API endpoints answer as though no identity system is configured.
- A **validation** plugin that fails to load means business rules stop being enforced while writes keep succeeding. This is the worst of the three, because nothing surfaces and the data is wrong afterwards.

**The startup guard becomes more valuable, not less.**
DMS-1434 argues for a post-container guard because "the implementer is exactly the party the guard exists to check."
Under drop-in delivery the implementer's code was compiled somewhere else entirely.
The guard's lifetime and descriptor-shape audit is the only thing between a third-party registration mistake and a captive-dependency defect in production, and its post-container placement is what makes it work regardless of contribution order.
It transfers unchanged, and the plugin mechanism adds four checks beside it that it did not need when the registrations were first-party: the replace-conflict check, the host-service displacement check, the no-contract-registered check, and the declared-contract activation probe, all fed by the recording wrapper, which additionally rejects host-owned removals and overwrites before they reach the collection.
Those four live in a startup task of their own; [Where the Code Lives](#where-the-code-lives) says why they cannot live in Core's.

### Observability

The trust model admits this design cannot contain a plugin.
That makes the record of what loaded an **audit record** rather than a convenience: an incident responder has to be able to say exactly which third-party code was in the process, at what version, from which bytes.

**The log is the authoritative inventory.**
Once the logger exists, one structured `Information` event per loaded plugin, with named properties rather than one concatenated string: name, the entry assembly's version, and the file name, `AssemblyVersion`, and SHA-256 of **every** assembly loaded into that plugin's own context from the plugin directory, the entry assembly included.
Hashing only the entry assembly would not support the claim above: a plugin's third-party closure is where the code an incident responder cares about usually lives, and the entry assembly's digest says nothing about it.
The set is collected after both hooks have returned, by enumerating the plugin's `AssemblyLoadContext.Assemblies` and keeping those whose location sits under the plugin directory, which is exactly the set host-first did not serve from the host.
DMS did not fetch the bytes and holds no pinned digest for them, so the loader computes the digests it can vouch for, over the files it loaded by path; that is what lets a responder match a running process to a published artifact after the fact.
The event also carries what the plugin actually contributed, which is the question operators ask most and which costs almost nothing here: **every** service type it registered, plugin contracts and everything else alike, comes from the recording `IServiceCollection` wrapper that [Contract Cardinality](#contract-cardinality) already needs for conflict detection, and the Phase A contribution comes from diffing `IConfigurationBuilder.Sources` around the call.
Listing the non-contract registrations is not decoration: it is the only visibility the design offers into a plugin registering a hosted service or an options configurator, which the displacement check deliberately permits.

The event carries two more things.
First, every assembly the host served where the plugin's `.deps.json` declared a different version, as a name, the version the plugin declared, and the version the host carries.
Host-first substitution is silent and almost always correct, which is exactly why it needs a record.
This is the diagnostic for the one report that would otherwise arrive with no evidence behind it, "it works on my machine and not in DMS."
Second, every pre-existing non-host descriptor the plugin removed or overwrote, as the service type and the implementation it displaced, since [Contract Cardinality](#contract-cardinality) permits those and this event is the only trace they leave.

**The startup status file records the phase, not the inventory, and that is a property of the file rather than a choice.**
`FileStartupStatusSignal.Write` calls `File.WriteAllText` (`Infrastructure/StartupStatusSignal.cs`), so the file holds exactly one document and the `LoadPlugins` record is replaced by the next phase's write milliseconds later.
It is the right place for **why startup failed**, because `WriteFailed` captures the exception type and message and the file then persists in that state since the process stops.
It is the wrong place for **what loaded**, and nothing should be built that parses an inventory out of it.

**Before the logger exists, `Console.Error`.**
Plugin loading necessarily runs before logging is configured, and it is the only channel CMS has without the bootstrap scaffolding DMS carries.
The loader writes one line per allowlisted name, in allowlist order, whether it succeeded or not, and the fatal reason when it did not.
It also writes `invoking <phase> on <plugin>` **before** each hook, not only the outcome after it.
That extra line is what makes a hang diagnosable.
[Trust Model and Verification](#trust-model-and-verification) accepts that a plugin blocking in a hook hangs startup with no timeout, so the last line on the channel has to name the plugin and the phase it was entered with, rather than leaving an operator with a silent process and a list of plugins.
Everything on that channel is re-emitted through the real logger once it exists, so an operator collecting only application logs still sees the full inventory.

**Configuration values are never logged.**
A Phase A plugin exists to carry secrets into configuration, so a loader that logged what it contributed would be a loader that logged secrets.
The Phase A record names the **source types** a plugin added and nothing else: not the keys, not the values.
This is a rule rather than an implementation detail, and it is asserted by test.

**No queryable endpoint, deliberately.**
`/health`, `/health/document-cache`, and Discovery are all unauthenticated, and a plugin inventory served there would tell any caller precisely which third-party code with full process trust is loaded and at which version.
The ODS precedent does not argue otherwise: its `VersionController` at `[Route("")]` exposes `dataModels` built from the domain model's schemas, so a plugin-contributed extension appears as a data model because a client needs to know which data models the API serves.
It exposes no assembly, version, or plugin identity.
None of the three types here contributes anything to the client contract, so there is nothing a caller needs and nothing to publish.
If a later plugin type does contribute something client-visible, it belongs in Discovery as part of the contract, still not as an inventory of loaded code.

**Not logged per request.**
Validators run on every write, and logging which plugin handled which request would put a real cost on the write path for information the startup inventory already gives once.

### Configuration Surface

One host-owned section, shared by every plugin type:

```json
"Plugins": {
  "Directory": "/app/plugins",
  "Allowed": "Sea.Dms.StudentIdValidator,Acme.Dms.Identity"
}
```

This is the **only** configuration surface for plugins, and it says what may load and nothing about where the bytes came from.
Acquisition has no configuration in DMS because DMS does not acquire; see [Acquisition](#acquisition).

The illustration above shows two names because an empty one shows nothing; the shipped default is different.
`appsettings.json` ships `"Plugins": { "Directory": "/app/plugins", "Allowed": "" }`, following the convention `AppSettings:AllowIdentityUpdateOverrides` already sets for a delimited list with no entries (`src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json:4`).
An empty `Allowed` is the continue-silently row in [Startup Failure Semantics](#startup-failure-semantics), so a deployment that adopts nothing boots exactly as it does today, and every populated `Allowed` in this document is an example rather than a default.

`Directory` defaults to `/app/plugins` and is resolved against `AppContext.BaseDirectory` when relative.
`Allowed` is a comma-delimited list of plugin names with surrounding whitespace trimmed, each of which must be a single path segment, and its order is the invocation order for both phases.
It is contractual for Phase A, where a later plugin's configuration sources win over an earlier plugin's, and where the environment variable and command-line sources sit above all of them; see [The Two Composition Phases](#the-two-composition-phases).
For Phase B it is deterministic but deliberately not contractual: it does determine fan-in invocation order, and the custom validation design already declares the observable consequence out of contract, since each validator sees its own deep clone and "message order is therefore explicitly not part of this contract and a client must not depend on it" (design.md, "Failure Surfacing").
The whole section is bindable from environment variables in the standard way, `Plugins__Allowed=...`, which is how the container deployment sets it.

**This diverges from DMS-1434 and should not be papered over.**
That story states "there is no DMS-owned configuration section for this feature and no switch that turns it on or off."
Under drop-in delivery a validator runs if and only if its directory is named in `Plugins:Allowed`, so the allowlist is a switch and `docs/CONFIGURATION.md` gains a section, which also states the Phase A precedence order.
What survives is the narrower claim: there is no *per-feature* switch, no `CustomValidation:Enabled`, and the only question ever asked is whether a plugin was allowlisted.

### What the Stock Image Must Ship

The no-rebuild promise is about **implementer** rebuilds and is delivered by a one-time **Ed-Fi** image change.
Those are different things and the distinction belongs in the operations documentation.

This requires one DMS release.
No image published today contains a loader, so "stock image" means the next stock image, not an existing one.

| File | Change |
| --- | --- |
| `src/dms/run.sh` | **none** |
| `src/dms/Dockerfile` | **build stage only.** It must see `src/plugins/` |
| `src/dms/Nuget.Dockerfile` | **none** |
| `src/dms/frontend/.../EdFi.DataManagementService.Frontend.AspNetCore.nuspec` | **none** |

`src/dms/run.sh` is unchanged.
Neither Dockerfile creates `/app/ApiSchema`; `src/dms/run.sh:44` does it with `mkdir -p` after reading the configured path.
The plugin root needs less than that, because a bind mount creates its own target and a missing root with an empty allowlist is the continue-silently row.

**The Dockerfile is not unchanged, and an earlier revision was wrong to say it was.**
`src/dms/Dockerfile` builds from an explicit per-project list: it copies each project's `.csproj` and `packages.lock.json` (`:15-26`), runs `dotnet restore --locked-mode` (`:28-29`), then copies each project's sources (`:31-42`).
Its build context is `src/dms/` with `src/` available as the named context `parentdir` (`eng/docker-compose/local-dms.yml:11-13`), and `WORKDIR` is `/source`, so `src/plugins/` is outside the tree the build stage currently assembles and the frontend's project reference into it would not resolve.
The host-integration story therefore changes the build stage so that the plugins tree sits beside the DMS tree exactly as `src/plugins` sits beside `src/dms`, with the repository's shared `Directory.Packages.props`, `nuget.config`, and `.editorconfig` at their common parent rather than only inside `/source`, and adds each new project's `.csproj` and `packages.lock.json` to the restore pass and its sources to the second pass.
Both new projects must carry committed lock files or `--locked-mode` fails the build, which is the failure this change is most likely to produce and the reason it is called out rather than left to be discovered in CI.
Nothing in the runtime stage changes: the publish output already carries the two new assemblies, `:77` copies `/app/Frontend/*.dll` into `/app`, and the `EdFi.Api` nuspec packs the whole publish directory into `DataManagementService/`, which is what `src/dms/Nuget.Dockerfile:23` unzips.

The loader ships as ordinary host code in `EdFi.Api.Plugins.Hosting.dll`, and the contract it and every plugin share ships in `EdFi.Api.Plugins.dll`.
The image change is therefore two assemblies in the application's own publish output and nothing in the image's shell or runtime layout, which is what keeps the stock-image claim checkable: the runtime stage, `run.sh`, the entry point, and the base compose files are all untouched, and a deployment that wants plugins adds one overlay compose file.

`EdFi.DataManagementService.ApiSchemaDownloader` is **not** generalized.
It keeps its hard-coded content root (`src/dms/clis/EdFi.DataManagementService.ApiSchemaDownloader/Services/ApiSchemaDownloader.cs:21`), its four command-line options, and its single caller in `src/dms/run.sh:53`, and the regression obligation that generalizing it would have carried does not exist.
If the deferred fetcher in [Out of Scope and Deferred](#out-of-scope-and-deferred) is ever brought back, generalizing the downloader rather than writing a second client is still the recommendation, for the reasons recorded there.

### The Secrets Spike

**The Secrets Manager plugin type is not designed here.**
All of it goes to its own spike, and that spike must cover two things together:

1. **The Secrets Manager plugin type itself** - its contracts, lifetimes, caching behavior, multi-tenant dimension, and the `IClientSecretHasher` relocation CMS would need.
2. **The DMS equivalent of [external configuration of ODS connection strings](https://docs.ed-fi.org/reference/ed-fi-api/platform-dev-guide/configuration/external-configuration-of-ods-connection-strings/)** - an operator supplying data store connection strings from outside the application's own configuration.

They are one spike because in ODS they are one mechanism.
**In DMS they cannot be**, and that is the single most important thing this spine hands over.
This spine designs only the mechanism that spike will build on.

**The spike starts when this one merges. Its tickets wait for the foundations.**
The secrets spike is design work and needs a mechanism that is *decided*, not one that is *running*, so it can begin as soon as this document merges and proceed in parallel with the foundation stories.
What it cannot do is deliver: every ticket it produces depends on the full plugin foundations being in place, because a secrets plugin is a plugin.

That split is why [The Two Composition Phases](#the-two-composition-phases) keeps Phase A even though nothing in this spike consumes it.
The secrets spike designs against this document while the foundations are mid-implementation.
A Phase B-only mechanism would put it in the position of having to amend a merged design and its in-flight stories, which is the rework cycle the filing gate exists to prevent.

This section records what that spike inherits, so it does not rediscover it.
The findings are the reason the deferral is sound rather than merely convenient: the obvious precedent does not transfer, and following it would have produced a design that cannot work here.

**Tenants are runtime data in both hosts, not configuration.**

- CMS resolves a tenant from a `Tenant` request header and validates it against `ITenantRepository`, gated on `AppSettings:MultiTenancy` (`Config.Frontend/Middleware/TenantResolutionMiddleware.cs`). Tenants are created and removed through `/v3/tenants` while the process runs.
- DMS validates a tenant against `IDataStoreProvider`'s cache and reloads from the Configuration Service on a miss (`Content/TenantValidator.cs`), so the tenant set is not stable even within one process lifetime.
- Per-tenant connection strings never pass through `IConfiguration`. `DmsConnectionStringProvider.GetConnectionString(long dataStoreId, string? tenant)` reads them from the Configuration Service at runtime (`src/dms/core/EdFi.DataManagementService.Core/Configuration/DmsConnectionStringProvider.cs`).

**The ODS answer does not transfer, and the spike should not start from it.**
ODS solves per-tenant secrets by injecting `Tenants:<name>:OdsInstances:<id>:ConnectionString` into `IConfiguration` before the host is built.
That works because in ODS the tenant set **is** configuration.
Here it is not, so a startup-time configuration source keyed by tenant is stale the moment a tenant is added.
ODS remains the right precedent for the *loading seam*, which this design adopts and narrows as Phase A.
It is the wrong precedent for the *data shape*.

**And the capability lands in CMS, not DMS.**
The linked ODS page is about an operator supplying connection strings from outside the application, and in this architecture DMS does not hold them: CMS does, encrypted under `DatabaseSettings:EncryptionKey`, and DMS fetches and decrypts them at runtime.
An operator who wants those values to come from a vault therefore needs **CMS** to resolve them, not DMS.
The spike's primary host is CMS even though the capability is described against an API that has no separate configuration service.

**What Phase A can serve here is the process-global secrets, and that is worth having on its own.**
DMS's `ConfigurationServiceSettings:ClientSecret` and `:EncryptionKey`, and CMS's four, are singular values read once at startup.
A Phase A plugin backed by a vault serves all six with no tenant dimension at all.

**What a per-tenant contract would have to look like.**
Inputs to the spike, not decisions taken here:

- A Phase B service, not a Phase A configuration source, because it must be callable after tenant resolution rather than before configuration is read.
- Taking the tenant as an argument rather than reading ambient state, because a plugin instance is loaded once per process and outlives any tenant.
- Async and cache-aware, because a vault round trip per request is not viable on the write path.
- Replace cardinality, so the fatal two-claimant rule already specified applies unchanged.

**Nothing in this spine precludes it.**
Phase B registrations may use any lifetime, including scoped.
The DMS-1434 guard's transient-only audit is specific to `ICustomResourceValidator` and does not generalize; what every declared contract gets from the plugin guard is the activation probe, which resolves each registration once and would catch a secrets resolver with an unsatisfiable dependency.
A lifetime rule for a per-tenant secrets contract, if that contract wants one, is the secrets spike's to state and its story's to enforce, the same way custom validation stated its own.
A tenant-parameterized contract is an ordinary interface in an ordinary package, and the mechanism does not need to know that it is tenant-aware.

### Applicability to the Configuration Service

The loader is host-agnostic, so CMS can have it for very little.
**No consuming epic drives it today**, though: with secrets deferred to its own spike, custom validation and identity are both DMS-side, and CMS's own candidate contract is a secrets contract.
This section is therefore an applicability analysis showing the mechanism generalizes, not scheduled work.
The [Level of Effort](#level-of-effort) table marks both CMS rows deferred.

CMS also owns a **replace**-cardinality contract already: `IClientSecretHasher`, the seam behind `IdentitySettings:ClientSecretHashingIterations`, which an operator with a mandated KDF would want to substitute.
Making it a plugin contract requires moving it, because it lives in `EdFi.DmsConfigurationService.Backend.OpenIddict` and is registered in four places: `Config.Frontend/Infrastructure/WebApplicationBuilderExtensions.cs:203`, `src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql/OpenIddict/MssqlOpenIddictServiceExtensions.cs:35`, and `src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/OpenIddict/PostgresOpenIddictServiceExtensions.cs` at both `:37` and `:93`.
A contract a plugin compiles against cannot ship from an assembly named for one identity provider, so relocating it to a neutral CMS contract package is prerequisite work for that type.
This belongs in the secrets companion document.

**CMS lacks the bootstrap scaffolding DMS has.**
`Config.Frontend/Program.cs` is a plain minimal-API startup: `CreateBuilder` at line 14, `AddServices()` at line 16, `Build()` at line 53.
There is no `FileStartupStatusSignal`, no `RunBootstrapPhase`, and no `StartupPhaseExecutor`.
This costs nothing, because the loader reports its own outcomes to `Console.Error` and throws.
CMS calls the loader directly and gets fail-loud behavior; porting a phase wrapper is a later choice, not a prerequisite.

CMS's image needs nothing either.
Because acquisition is a deployment step rather than an image feature, the fact that CMS ships no NuGet client is irrelevant: both recipes in [Acquisition](#acquisition) end in a read-only mount, and `published-config.yml:60` already declares a `volumes:` block for that mount to join.
The image asymmetry that a DMS-owned fetcher would have created between the two hosts does not arise.

---

## Where the Code Lives

Two new projects under a new `src/plugins/`, which sits beside `src/dms` and `src/config` under the `src/Directory.Packages.props` that already governs both.

| Project | Packaged as | Referenced by |
| --- | --- | --- |
| `EdFi.Api.Plugins` | `EdFi.Api.Plugins`, published | Third-party plugins, and `EdFi.Api.Plugins.Hosting` by project reference. Nothing else |
| `EdFi.Api.Plugins.Hosting` | not packaged | Both host frontends, by project reference. **Not** `EdFi.DataManagementService.Core` |

The cardinality registry lives in `EdFi.Api.Plugins.Hosting` as a value the **host** passes to the loader: a set of `(Type contract, Cardinality cardinality)` entries, one per declared plugin contract, which the recording wrapper and the plugin startup guard consult for the replace-conflict, displacement, no-contract, and activation checks.
Each host supplies its own set, DMS's naming `ICustomResourceValidator` today, and each companion document adds its contract as one entry there; the registry is never read from configuration or from a plugin.

**The plugin guard is a second startup task, in the frontend, and Core never sees `src/plugins/`.**
DMS-1434's guard is an `IDmsStartupTask` in Core (`src/dms/core/EdFi.DataManagementService.Core/Startup/IDmsStartupTask.cs:21`), and Core is the wrong home for the plugin checks: they read the per-plugin records that `EdFi.Api.Plugins.Hosting` produces, and giving Core a project reference into `src/plugins/` would invert the dependency, drag the contract assembly into every Core consumer, and widen the Dockerfile change above from the frontend to Core.
So the four plugin checks are a separate `IDmsStartupTask` implementation owned by the **DMS frontend**, which already references both Core and `EdFi.Api.Plugins.Hosting`.
`EdFi.Api.Plugins.Hosting` stays host-agnostic and knows nothing about `IDmsStartupTask`: it exposes the per-plugin records and a pure audit function over them and the contract registry, returning findings, and the frontend's task calls it and takes the existing fatal path.
Its `Order` sits in the same 200-299 band as DMS-1434's guard and above it, so both run inside the same executed window and the plugin checks see a collection the validator audit has already accepted.
CMS gets the same shape when CMS integration is taken up: its own thin task over the same audit function, or a direct call, since CMS has no startup-task machinery today.

The contract package and the loader are separate assemblies deliberately.
Under host-first resolution the contract assembly is shared with every plugin, so its public surface is a compatibility commitment to third parties forever; the loader's is not, and it must never become one.
The contract references only `Microsoft.Extensions.Configuration.Abstractions` and `Microsoft.Extensions.DependencyInjection.Abstractions`, keeping the closure an implementer inherits minimal.

`EdFi.Api.*` is the correct prefix for a package both hosts consume: CMS already publishes as `EdFi.Api.ConfigurationService`, so the prefix names the Ed-Fi API platform rather than DMS specifically.

`src/plugins/` needs its own `Directory.Build.props`.
There is no `src/Directory.Build.props`; `src/dms/` and `src/config/` each carry their own, so a third top-level folder inherits nothing from them.
What `src/dms/Directory.Build.props` actually carries is narrower than an earlier revision claimed: `AssemblyVersion`, `FileVersion`, `InformationalVersion`, `Product`, `Authors`, `Company`, `TreatWarningsAsErrors`, `ErrorLog`, `RestorePackagesWithLockFile`, and two analyzer `PackageReference`s.
The target framework, `Nullable`, `ImplicitUsings`, and `GenerateDocumentationFile` are per-csproj in this repository, and the license-header and formatting conventions come from `.editorconfig` and csharpier rather than from any props file.
So `src/plugins/Directory.Build.props` copies the version-metadata, `Product`/`Authors`/`Company`, `TreatWarningsAsErrors`, `ErrorLog`, `RestorePackagesWithLockFile`, and analyzer items, and the two csproj files declare their own `TargetFramework`, `Nullable`, and `GenerateDocumentationFile` the way every project under `src/dms/` already does.
`src/dms/Directory.Build.targets` is **not** copied: it exists to materialize the bundled ApiSchema manifest and has no counterpart under `src/plugins/`.
`src/plugins/Directory.Build.props` also carries the contract's own version, hand-maintained and **not** regenerated by `build-dms.ps1`'s `SetDMSAssemblyInfo`, which stamps the release version onto `src/dms/Directory.Build.props` and must not reach this file.
[The Plugin Contract](#the-plugin-contract) explains why: the newer-plugin-on-older-host check compares assembly versions, so the contract's `AssemblyVersion` must move with its surface and only with its surface, and the pack lane asserts it equals the package version.
`EdFi.Api.Plugins.Hosting` is not packaged and is referenced by project, so it inherits the same file and its version is irrelevant.

DMS and CMS have separate solutions (`src/dms/EdFi.DataManagementService.sln`, `src/config/EdFi.DmsConfigurationService.sln`), so both new projects are added to both.
A project present in one solution and missing from another is a known failure mode in this repository, and `--locked-mode` does not catch a missing project entry.

`src/dms/EdFi.DataManagementService-Docker.sln` is deliberately **not** updated.
It carries six projects against the main solution's forty-four, omitting `EdFi.DataManagementService.CustomValidation` and `Core.External` among others, and nothing in the repository builds it: `build-dms.ps1:165` knows only `EdFi.DataManagementService.sln`, and no workflow names it.
Adding entries to a solution nothing builds would produce bookkeeping that cannot go stale detectably, which is the opposite of the failure mode the paragraph above is guarding against.
If that solution is ever revived, it needs the projects it is already missing before it needs these two.

---

## Divergence from the Custom Validation Epic

DMS-1462's frame allows revising the custom validation composition decision and requires that any divergence be flagged rather than absorbed.
`DMS-1432` is merged and stays merged; every other row is an open story.

| Ticket | Status | Effect | Why |
| --- | --- | --- | --- |
| **DMS-1432** Abstractions contract, its six public types | Done | **Unchanged.** The six public types stand exactly as shipped | `EdFiApiPlugin` lands in `EdFi.Api.Plugins`, not in `EdFi.Api.CustomValidation`. The contract keeps no dependency on the delivery mechanism |
| **DMS-1432** Contract version scheme | Done | **Amended.** `EdFi.Api.CustomValidation` gets its own semantic version, starting at `1.0.0`, declared in its csproj and packed at that version rather than at `$DMSVersion` | The loader's newer-plugin-on-older-host preflight compares `AssemblyVersion`s across every contract package. Under the release-stamped scheme it would refuse a validator built against the 8.4 contract on an 8.3 host whose surface is identical. Owned by draft 06, which is also where the packed-`AssemblyVersion` assertion lands |
| **DMS-1432** `ICustomResourceValidator` XML documentation | Done | **Corrected.** Two sentences become false when this mechanism ships | `src/dms/core/EdFi.DataManagementService.CustomValidation/ICustomResourceValidator.cs:16-17` tells an implementer the interface "is compiled into the host deployment and registered into DMS's composition; it is not loaded from a dropped-in assembly at runtime", and that text ships inside the nupkg and shows in an implementer's IDE. Owned by draft 04, the story that makes it false; the implementer guide restates the corrected version |
| **DMS-1432** Publishing deferral | Done | **Stands.** Publishing is release-gated, not a prerequisite | The per-PR lane already packs the contract and compiles a scratch consumer against the packed nupkg (`.github/workflows/on-dms-pullrequest.yml:364-386`), with package source mapping so the local artifact cannot be substituted. Every internal story proves the packaged path with no feed. Publishing is what an **external** implementer needs, and it lands when the epic is ready to be consumed |
| **DMS-1433** Fan-in pipeline step | Open | **Unchanged** | The step resolves `IEnumerable<ICustomResourceValidator>` from the request scope and does not care how entries got there |
| **DMS-1434** Composition seam | Open | **Partially superseded.** The seam changes; the guard does not | "One call at DMS's composition root" becomes "the loader invokes `ContributeServices`." The guard, its post-container placement, its `Order` in 200-299, and its lifetime audit all transfer unchanged and matter more |
| **DMS-1434** No config section | Open | **Superseded.** `Plugins` is a host-owned section and the allowlist is a switch | See [Configuration Surface](#configuration-surface). The narrower claim that survives is that there is no per-feature toggle |
| **DMS-1435** Implementer guide | Open | **Rewritten and narrowed.** Authoring a validator and registering it inside a plugin hook, with packaging, layout, acquisition, and the trust model linked out to `PLUGINS.md` rather than restated | The audience changes from someone who builds DMS to someone who does not, and the delivery half of the subject now belongs to a guide that serves every plugin type rather than one |
| **DMS-1436** End-to-end proof | Open | **Widened.** The fixture validator is packaged, pinned, and loaded into a pulled stock image | Proving the compiled-in path proves a path no implementer will take |

**The contract survived the delivery reversal intact.**
design.md's "Rejected Alternatives" predicted it, promising a runtime-loading stream would "inherit this contract, the fan-in step, the failure surfacing, and the startup guard unchanged, and add only a discovery-and-registration path that feeds the same collection."
This document is that stream and the prediction held.
The separation that made it hold, a contract package naming only BCL types and its own, is worth preserving in the identity and secrets contracts too.

**Publishing is the epic's last step, not its first.**
Drop-in delivery makes the published package the only thing an **external** implementer can compile against, which is a real change from compiled-in delivery.
It does not make publishing a prerequisite for building the mechanism.
`build-dms.ps1` already packs `EdFi.Api.CustomValidation` and the per-PR lane already compiles a scratch consumer against that packed nupkg from a local folder feed, so the packaged consumption path is proven on every pull request without a feed and without burning a package id.
`EdFi.Api.Plugins` follows the same lane for the same reason.
Both are published together once the epic is ready to be consumed, and the [Level of Effort](#level-of-effort) table treats that as a leaf that everything blocks and that blocks nothing.

---

## Rejected Alternatives

| Alternative | Disposition | Reason |
| --- | --- | --- |
| Per-plugin directory, isolated context, allowlist-driven, two phases, acquisition as a deploy-time recipe rather than a DMS feature | **Adopted** | Serves all three types and both phases from one load, keeps a stock image, keeps the plugin root read-only to the runtime in every deployment, and isolates what can be isolated without splitting shared type identity |
| **A derived image per implementation**, `FROM edfialliance/ed-fi-api` plus `COPY` | **Rejected by constraint** | Simplest possible mechanism and needs no loader at all, but every implementation then owns an image and re-derives it on every DMS release. The requirement is a stock image per deployment, not a derived one per implementation |
| Compiled-in composition as the documented route | **Demoted, not removed** | Presumes a deployment that builds DMS, which the distribution model contradicts. Survives as the in-repo fixture route |
| **An enumerated set of shared assemblies**, isolating everything else | **Rejected, and probe-refuted** | Splits `IChangeToken` identity and throws `TypeLoadException` on the most ordinary plugin there is. See [Load Isolation](#load-isolation) |
| Load into the default context, as ODS does | Rejected | First-wins by simple name, ordering that `Directory.GetFiles` does not define, and a shadowed host assembly surfacing as an unrelated failure later. Isolation costs one class |
| One load context for all plugins | Rejected | Cheaper, but reintroduces conflicts between two plugins carrying different versions of one dependency, the likeliest real collision once more than one vendor ships |
| Collectible contexts, to support unloading | Rejected | Unload is a non-goal, and collectibility adds indirection and a class of lifetime bugs for a capability nothing asks for |
| **A marker interface plus optional contributor interfaces** | Rejected | Adding a phase later would break every plugin binary compiled against the previous contract. A base class with virtual no-ops makes phase addition binary-compatible, which host-first resolution makes mandatory |
| A `plugin.json` manifest per plugin | Rejected | `.deps.json` already carries the dependency graph and the directory name already carries identity. A second format needs its own versioning and its own validation errors |
| Scan the plugin root and filter, rather than drive from the allowlist | Rejected | Reading metadata from a directory nobody named is work done on an attacker's behalf, and it turns a misspelled entry into a silent skip |
| Last-wins for replace contracts, as ODS does by module ordering | Rejected | ODS ranks modules `ICustomModule` last, then `Override`-prefixed names, then the rest, and `OrderBy` is stable, so two vendor plugin modules tie and the winner is decided by `AppDomain` assembly enumeration order. The operator installed both and is the only one who can say which was intended |
| **Diffing `IServiceCollection` around each hook** for attribution | Rejected | Once two plugins have run, a diff cannot say which one added what, and the wrapper's removal rules have to fire before a call reaches the real collection rather than after |
| **Recording declined `TryAdd` calls** in the wrapper, to detect two claimants on a replace contract | **Rejected, and probe-refuted at panel review.** It was the earlier decision here | `TryAdd` reads `Count` and the indexer, compares, and returns without ever handing the candidate descriptor to the collection, so the wrapper sees an anonymous scan and never sees the implementation type; `TryAddEnumerable` sweeps the whole collection, so even the service type is ambiguous. No `IServiceCollection` wrapper can recover it. Replace-cardinality descriptors are masked from each plugin's view instead, so the decline never happens |
| **Masking every contract, not only replace contracts**, from a plugin's view | Rejected | `TryAddEnumerable`'s deduplication by implementation type is behaviour a fan-in registration legitimately relies on, and duplicate fan-in entries are harmless by that row's own definition. Masking is scoped to the few types where a silent decline is the failure |
| **A DMS-owned fetcher**: a generalized `ApiSchemaDownloader` invoked from `run.sh`, driven by a name-keyed `Plugins:Packages` map beside `Allowed`, each entry carrying `Version`, `FeedUrl`, and a mandatory `Sha256`, with a digest-matched `.nupkg` cache and a fatal cross-check against `Allowed` | **Deferred**, fully designed | This was the earlier decision here and its shape is recorded so it can return without redesign. It does not ship first because Recipe 2 in [Acquisition](#acquisition) delivers the identical pinned, verified acquisition with no DMS code; because it is the only foundation story that changes a shipped, load-bearing CLI and so carries a regression obligation to the ApiSchema path; because it writes into `/app/plugins` on the container filesystem, which breaks `readOnlyRootFilesystem` hardening and forces the trust posture to become per-entry rather than uniform; because its cache claim, that a feed outage does not block a restart, is only true with persistent storage most pod restarts do not have; and because a secrets plugin fetched from a private feed would need that feed's credential in plain configuration, a circularity the deployment-side recipe does not have. Its two advantages over the recipe, one fewer container and one fewer place to write the digest, are real and small |
| **A separate `PLUGIN_PACKAGES` acquisition list**, mirroring `SCHEMA_PACKAGES`, if the fetcher returns | Rejected | Not because acquisition and enablement must be one structure, but because they must be one **section** read once. A separate variable is read by `run.sh` before the process exists and by nothing inside it, so the two can genuinely diverge across a restart and the digest ends up outside the surface that governs execution. ApiSchema has no allowlist for its package list to drift against, so the shapes were never parallel |
| **An indexed array of allowlist objects**, `Allowed: [{ Name, ... }]` | Rejected | Binds cleanly from JSON and badly from the environment, which is where deployments set it. Every plugin becomes `Plugins__Allowed__0__Name` and up, the operator tracks free indexes, and Compose and Kubernetes both surface that form. A delimited list reads at a glance |
| **A dictionary keyed by plugin name** | Rejected | `IConfiguration` enumerates a section's children in alphabetical order, so Phase A invocation order would become a function of plugin names rather than of the operator's intent. Phase A order is contractual, so the ordered list has to be an ordered list |
| **Plugin configuration sources last, above environment variables and command-line arguments** | Rejected | It is what an external secret store would prefer, and it was the first decision here. It means an operator's explicit override silently stops taking effect once an unrelated plugin is installed, which is both the more surprising rule and the less secure one, since the environment and the command line are the surfaces the deployment controls. See [The Two Composition Phases](#the-two-composition-phases) |
| **Placing plugin sources below the *first* environment variable source** | **Rejected, probe-refuted at panel review.** It was the third decision here | `CreateBuilder` installs twelve sources on `net10.0`, and the first two environment sources are prefix-filtered and sit *below* `appsettings.json`. The placement would have put a vault's `ConfigurationServiceSettings:ClientSecret` under the empty string `appsettings.json` ships, inverting the precedence this design commits to. The rule is the *last* environment variable source |
| **An unconditional `volumes:` mount in `published-dms.yml` and `local-dms.yml`** | Rejected at panel review | Compose cannot make a bind mount conditional inside one file, so Docker would materialize an empty root-owned `./plugins` beside every deployment that never asked for a plugin. The mount goes in an overlay file added with `-f`, following `bootstrap-dms.yml` |
| **Adding the new projects to `EdFi.DataManagementService-Docker.sln`** | Rejected at panel review | Nothing in the repository builds it and it already omits projects the Docker build needs, so an entry there could go stale with nothing to notice. See [Where the Code Lives](#where-the-code-lives) |
| **Generating the host assembly manifest from the published `.deps.json`** | Rejected at panel review | A `--self-contained false` publish omits every shared-framework assembly, including both abstractions in the hook signatures, and `/app/Frontend` exists only in the build stage. The manifest is generated against the built runtime image |
| **Putting the plugin attribution checks in Core's startup guard** | Rejected at panel review | Core would need a project reference into `src/plugins/` to read the per-plugin records, inverting the dependency and dragging the contract assembly into every Core consumer. A second `IDmsStartupTask` in the frontend reads them and calls a pure audit function in the hosting assembly |
| **Re-adding the environment variable source after Phase A** to restore operator precedence | Rejected | It was the second decision here. It restores the environment and forgets the command line, which `CreateBuilder(args)` installs above the environment, so `--AppSettings:Foo=bar` would still lose to a plugin. Moving the plugin's sources below both is one list operation and reads the environment once |
| **Recording only the descriptors a plugin adds** | Rejected | `services.Replace(...)` and `RemoveAll<T>()` are ordinary extension methods over the `IList<ServiceDescriptor>` members, so a plugin could displace a host default while the wrapper saw a plain add or nothing. Removals and overwrites of pre-existing host-owned descriptors are fatal at the wrapper |
| **Making every removal fatal**, including a plugin's own descriptors | Rejected | Framework and vendor registration helpers `Replace` and `RemoveAll` their own descriptors internally, so the rule would reject an ordinary plugin the first time a vendor SDK tidied its registrations, naming a type the implementer never wrote. The wrapper already knows what each plugin added, so scoping the rule to pre-existing descriptors is a lookup |
| **Making every removal of a pre-existing descriptor fatal**, whatever its service type | Rejected at approval review | The same vendor helpers also `Replace` and `RemoveAll` over framework descriptors the host registered before the hook, and DMS cannot enumerate which SDKs do. The fatal is scoped to host-owned service types, which the displacement check already identifies by assembly, and everything else is recorded in the inventory event. That keeps the wrapper at three rules keyed on one host-owned-type test, rather than the policy engine this section warns against |
| **Versioning `EdFi.Api.Plugins` on the DMS release**, regenerated by `SetDMSAssemblyInfo` like every `src/dms/` assembly | Rejected at approval review | The skew check would then refuse a plugin built against the 8.4 contract on an 8.3 host whose contract surface is identical, naming two versions that differ in nothing an implementer can act on. The contract carries its own semantic version, moving only when its surface moves |
| **Treating a plugin that registers no plugin contract as loaded** | Rejected | It ran and contributed nothing the host will call, most plausibly because it was allowlisted on the wrong host. Zero `EdFiApiPlugin` subclasses is already fatal for the same reason one level up |
| **Falling back to the plugin's private copy when the host cannot serve a version** | Rejected | It produces the split type identity host-first exists to prevent, discovered later as a `TypeLoadException` inside a hook. Named fatal instead |
| **Distinguishing "host lacks it" from "host refused the version" by exception type**, `FileNotFoundException` versus `FileLoadException` | **Rejected, probe-refuted at panel review.** It was the earlier decision here | Measured on `net10.0`: `Default.LoadFromAssemblyName` throws `FileNotFoundException` in both cases, strong-named or not, so the `FileLoadException` arm never runs and a `catch (FileNotFoundException)` silently serves the private copy. The loader asks by simple name and compares versions itself |
| **Relying on the lazy `Load` override alone** for version-skew detection | Rejected | `Load` fires when a type resolution first needs an assembly, so a skewed dependency no hook happens to touch would surface on a request rather than at startup, and "fatal at load" would not be true. An eager `.deps.json` preflight runs first and the override is the backstop |
| **Declaring native assets unsupported** and refusing a plugin that carries `runtimes/` | Rejected | The cloud SDKs and database clients these plugin types will carry ship native assets, DMS's own image already carries a `runtimes/` tree for `Microsoft.Data.SqlClient`, and `AssemblyDependencyResolver` already answers the question. A `LoadUnmanagedDll` override is three lines |
| **Keying the self-contained check on a runtime identifier in `runtimeTarget`** | **Rejected, probe-refuted at panel review** | `dotnet publish -r osx-arm64 --no-self-contained` also produces `".NETCoreApp,Version=v10.0/osx-arm64"`, so the check would refuse the legitimate RID-specific framework-dependent publish that native assets require. A `runtimepack` library entry is the discriminator that separates the two |
| **Trusting Phase A's narrow surface to keep it additive** | Rejected | `IConfigurationBuilder.Sources` is mutable, so the surface delivers configuration-only and not additive-only. A snapshot comparison around each call costs one list copy |
| **Letting a plugin register host-owned service types unchecked** | Rejected | An implementer who misreads "contribute services" can displace a host default through ordinary DI and learn about it from a production defect. The host already holds the contract set and the recording wrapper already sees every descriptor, so the guardrail is nearly free. It is a mistake-detector, not an isolation boundary, and is framed as such |
| **A directory content-manifest digest** in the recipe | Rejected | Requires specifying path ordering, separator normalization, symlink, and case-sensitivity rules that every operator and build tool must reimplement identically, to verify an artifact derived from one that already has a natural digest |
| Consume a conventional library package with `lib/`, resolving at deploy time | Rejected | Puts NuGet restore, version unification, and conflict resolution on the deploy path, and out of reach of `unzip`. Flattening a closure is what `dotnet publish` does and the implementer can see and test the result |
| Out-of-process plugins over gRPC or HTTP | Rejected | Real process isolation, and the only option that would actually contain a plugin, but it needs a wire contract per type, a latency budget on the write path, service-to-service auth, and a second deployable per plugin. Revisit only against a stated isolation requirement |
| Ship the mechanism and keep the trust model implicit | Rejected | Drop-in loading is the one thing here that changes the security posture, and leaving what it does and does not protect against unstated is a wrong answer to a question operators will ask |

---

## Out of Scope and Deferred

| Item | Status | What would bring it back |
| --- | --- | --- |
| The per-type contracts | Companion documents | This document fixes only their cardinality and their loading |
| **Per-tenant secret resolution, and secrets contracts generally** | **A separate spike** | Nothing. It is deferred by decision, not by dependency. See [The Secrets Spike](#the-secrets-spike) |
| `IClientSecretHasher` relocation out of `Backend.OpenIddict` | The secrets spike | It is a secrets contract, and moving it is prerequisite work for that type rather than for this mechanism |
| **A DMS-owned fetcher** driven by a `Plugins:Packages` map, see [Rejected Alternatives](#rejected-alternatives) for the recorded shape | Deferred | A deployment target that cannot run an init container or a one-shot service ahead of DMS, and evidence that operators are hitting it. Compose and Kubernetes both can, so the trigger is a platform this design has not met |
| NuGet package **signature** verification | Deferred | A policy requiring author or repository provenance beyond an operator-pinned digest. The pinned digest already proves the operator approved these bytes |
| Plugins that add HTTP endpoints | Deferred | A plugin type that owns a route. DMS-1412 states the Identity API surface is DMS-owned with the plugin supplying the implementation behind it. Adding it later is a third virtual on `EdFiApiPlugin` |
| Hot reload and unloading | Out of scope | Nothing; a restart is the supported path |
| Plugin-to-plugin dependencies | Out of scope | Each closure is its own; two plugins exchange types only through host contracts |
| A capability or permission model for plugins | Out of scope | Nothing available in .NET delivers it in-process |
| An Ed-Fi signing authority or plugin catalog | Out of scope | A governance decision, not a technical one |
| Migrating the ODS plugin format | Out of scope | An ODS plugin is not a DMS plugin; no source or binary compatibility is attempted |
| Logging sinks | Out of scope | DMS and CMS remain complete applications for logging, with structured console and file output and the built-in OTLP exporter. Nothing here changes how a sink is chosen or configured |

---

## Testing Strategy

The loader is testable without a host and the host integration is testable without a real plugin, so the expensive combination is needed only for the end-to-end proof.

**Unit, over the loader.**
Fixture plugin directories built as test assets: a well-formed plugin; one with no `EdFiApiPlugin` subclass; one with two; one whose `Name` disagrees with its directory; one whose entry assembly is corrupt; one published self-contained, whose `.deps.json` therefore carries a `runtimepack` library; one published `-r <rid> --no-self-contained`, whose `runtimeTarget` names a RID and which must load, since that is how a plugin ships native assets; one carrying a private copy of the contract assembly, which asserts sharing actually happens rather than merely being intended; and two plugins carrying different major versions of one third-party dependency, which asserts isolation still works for what the host does not have.
Allowlist entries that are rooted, contain a separator, or are `..` each get a case asserting the entry is rejected before any path is composed.
Each fatal row in [Startup Failure Semantics](#startup-failure-semantics) gets a case that asserts the failure is fatal rather than logged.

**Unit, over load isolation specifically.**
A plugin that reads configuration and binds options through `IOptions<T>` is the regression test for the `IChangeToken` split.
It fails with `TypeLoadException` under an enumerated shared set and passes under host-first, so it pins the decision rather than restating it.
A second case asserts a plugin compiled against contract version N still loads against contract version N+1 carrying an added virtual member.
The reverse direction gets its own case: a plugin whose entry assembly references contract version N+1 on a host carrying N is fatal with both versions named, and the assertion is that the failure came from reading the assembly's references rather than from loading a type, since a type-load error would prove the check ran too late.
A third case covers version skew on a shared assembly: a plugin declaring a higher version of an assembly the host carries is fatal **before the plugin is constructed**, from the `.deps.json` preflight, naming the assembly and both versions, and it is asserted not to have silently loaded the plugin's private copy.
A fourth covers the backstop: a reference the preflight could not see resolves through the `Load` override, and the test asserts the loader reports the named skew rather than the runtime's `FileLoadException` wrapper, by asserting on the message an operator would read.
A probe test records what `Default.LoadFromAssemblyName` actually does on that skew, so the policy is pinned by measurement rather than by an assumed exception type; the measurement this design was written against is that it throws `FileNotFoundException` for both "absent" and "older", which is why the loader asks by simple name.
A fifth asserts a plugin carrying a native asset resolves it through `LoadUnmanagedDll` and calls into it.

**Unit, over binding the `Plugins` section.**
`Allowed` is parsed from a delimited string with surrounding whitespace, empty elements, and a single name, and the resulting order is asserted to be the order written rather than any sorted order, since Phase A order is contractual.
A name repeated in `Allowed`, including a repeat that only appears after trimming, is fatal.
`Plugins__Allowed` set as an environment variable binds to the same ordered list as the JSON form, which is the form deployments actually use.

Phase A cases throughout this section land with the secrets foundation story that adds the phase, not with this spike's stories.

**Unit, over attribution and the recording wrapper.**
Two plugins claiming one replace-contract through `TryAdd` are both named in the failure, and a single plugin registering two descriptors for one replace-contract through `TryAdd` is fatal on its own, naming the plugin and the contract.
Both cases exist because the wrapper masks replace-cardinality descriptors from each plugin's view, so a direct test asserts that masking: a plugin enumerating the collection during its hook does not see a replace-contract descriptor the host registered, while it does see a fan-in contract descriptor and every framework descriptor, and the real collection is asserted to still contain the masked one afterwards.
A separate test asserts the design's own limit rather than a capability: a declined `TryAdd` over a **fan-in** contract is not recorded anywhere and the inventory does not claim it was, so nobody later builds a check on information the seam does not carry.
A plugin that registers `IDocumentStoreRepository`, a host-owned service type that is not a declared plugin contract, is fatal and names both the plugin and the type, while a plugin that registers its own types, `Microsoft.Extensions.*` options, and an `IHostedService` alongside a declared contract loads unaffected, which is what keeps the check from being a blanket ban on registering anything, and its inventory event is asserted to list the hosted service.
A plugin that calls `services.Replace(...)` on a host default, one that calls `RemoveAll<T>()` over a host type, and one that assigns through the indexer over a pre-existing host-owned slot are each fatal at the wrapper, naming the plugin and the service type, and the real collection is asserted unchanged.
A plugin that calls `RemoveAll<IHttpMessageHandlerBuilderFilter>()` over a framework descriptor the host registered loads unaffected and its inventory event is asserted to list the removal with the displaced implementation type, which pins the host-owned scope of the rule.
A plugin that registers a descriptor and then replaces it, and one that calls the real `AddHttpClient`, `AddOptions<T>().Bind(...)`, `AddLogging`, and a real cloud SDK client registration against a collection the host's own `AddServices` has already populated, each load unaffected, which pins the rule to pre-existing descriptors and proves it against the helpers as shipped rather than against imitations.
A plugin whose Phase B hook registers only its own types and whose Phase A hook adds nothing is fatal as contributing no plugin contract, and the same plugin with one Phase A source added is not, which pins the rule that a Contribute-only plugin satisfies the check.
A Phase A plugin that removes a pre-existing configuration source, and one that reorders it, are each fatal and named, while one that only appends is unaffected.
One test asserts a Phase A plugin cannot influence `Plugins:Allowed`, which is the security property from [Trust Model and Verification](#trust-model-and-verification) rather than an implementation detail.

**Unit, over observability.**
A plugin whose Phase A source carries a value that looks like a secret is loaded, and the captured log output is asserted to contain the source **type** and to contain neither the key nor the value.
That is the [Observability](#observability) rule stated as a test rather than as a convention, and it is the one that silently stops holding if someone later "improves" the diagnostics.
A second case asserts the per-plugin event carries the registered service types, which proves the recording wrapper feeds the log and not only the conflict check.
A third asserts that a plugin whose `.deps.json` declares an older version of an assembly the host carries loads successfully and that the inventory event lists that assembly with both versions, which is the only evidence a host-first substitution ever leaves.
The loader is also asserted to write its `invoking <phase> on <plugin>` line before the hook runs, by having the fixture hook throw and asserting the announcement is already on the channel, since a line written only after the hook is worthless for a hang.

**Integration, over host startup.**
A `WebApplicationFactory<Program>` boot with a real fixture plugin directory, asserting an observable effect of both phases: a configuration value only the Phase A hook could have supplied, and a resolvable service only the Phase B hook could have registered.
Fatal cases assert on the exception escaping host creation, because plugin loading happens before the container exists.
`RunBootstrapPhase` writes the failed phase to the startup status file and rethrows (`Program.cs:443-462`), so a loader fatal propagates out of the `WebApplicationFactory` host build; `IStartupProcessExit` is DI-registered (`Infrastructure/WebApplicationBuilderExtensions.cs:79`) and is not resolved until after `builder.Build()` (`Program.cs:130`), so substituting it would prove nothing about this phase.
The assertion is therefore the escaping exception plus the failed `LoadPlugins` record in the status file, and the `IStartupProcessExit` double stays where it belongs, on the post-container guard checks that DMS-1434 and the plugin startup task run.
One test asserts a key supplied both by a Phase A source and by an environment variable resolves to the environment value, and a second asserts the same for a key supplied on the command line, which are the precedence rules from [The Two Composition Phases](#the-two-composition-phases) and the ones an operator would otherwise discover by outage.
A third asserts the loader added no source of its own: the set of non-plugin sources in `Sources` after Phase A is the set that was there before it, so the earlier re-add approach cannot creep back in.
One test asserts the startup status file carries a `LoadPlugins` phase record.
CMS gets the equivalent for its own `Program` **when CMS host integration is taken up**, asserting fail-loud behavior without a phase wrapper.
No draft in this spike carries it, because no consuming epic drives CMS plugin loading; the [Level of Effort](#level-of-effort) table marks that row deferred and this obligation travels with it.

**End-to-end, the load-bearing one.**
A fixture plugin is published `--no-self-contained`, packed asset-only, pinned by digest, and loaded by a **pulled** `edfialliance/ed-fi-api` image that this test never builds.
Both recipes are proven, in **two** deployments rather than one.
They cannot share a deployment: each ends with a mount at `/app/plugins`, and one container has one mount target, so a single Compose project would have to merge them into something neither document publishes.
So the tier runs the Recipe 1 overlay in one deployment with a plugin pre-placed under a bind-mounted root, tears it down, and runs the Recipe 2 overlay in a second with the plugin delivered by the one-shot `fetch-plugins` service from a local folder feed, then a third time with the digest deliberately wrong to assert the service fails and DMS never starts.
Each is driven verbatim from `docs/OPERATIONS.md`, which is the point: running the recipe as published rather than an equivalent is what makes it documentation an operator can paste.
The assertion is that a stock published image runs third-party code with no image derived and no DMS rebuilt.
That is the whole claim of this design and the one assertion that fails if the claim is wrong.

This tier cannot run before the first published image carries the loader, so it is its own post-release ticket rather than a note attached to another story, and the [Level of Effort](#level-of-effort) table carries it as such.
Until it runs, the tier below it proves the same mechanism against a locally built image, which tests everything except the one word "stock".

**Consumer proof.**
The per-PR lane already packs `EdFi.Api.CustomValidation` and compiles a scratch consumer against the produced nupkg (`eng/verification/CustomValidationConsumer/`).
The same lane extends to `EdFi.Api.Plugins`, and the fixture plugin is built against the packed nupkgs rather than project references, so CI exercises the packaged path an implementer takes.
The lane gains one assertion neither package needed before, applied to **both**: the `AssemblyVersion` of the assembly inside each packed nupkg equals that package's version.
It is the assertion that keeps the newer-plugin-on-older-host check from going blind, and it is the kind of thing that breaks when someone tidies a props file.
`EdFi.Api.CustomValidation` needs it too now that it carries its own version, and the same lane asserts that running the build with an explicit `-DMSVersion` leaves both packed versions untouched, which is what proves `SetDMSAssemblyInfo` no longer reaches either contract.

---

## Level of Effort

Spine only.
Per-type contracts, their pipeline or endpoint wiring, and their documentation are costed in the companion documents.

| Work | Size | Notes |
| --- | --- | --- |
| `EdFi.Api.Plugins` contract package, packed and consumer-verified | Small | One base class; follows the DMS-1432 pack-and-assert lane exactly |
| `EdFi.Api.Plugins.Hosting`: discovery, load contexts, `.deps.json` skew preflight, Phase B hook invocation, recording wrapper, load inventory | Medium | The load context is the only genuinely subtle code, and its decisions - resolve host-first by simple name, compare versions explicitly, fatal when the host carries less - are probe-pinned by tests. The inventory is nearly free: the recording wrapper already tracks contributions for conflict detection, and the version-skew list falls out of the `.deps.json` the preflight already reads |
| New `src/plugins/` folder: its own `Directory.Build.props` carrying the contract's independent version, both projects into the two solutions CI builds | Small | `src/` has no shared props file, so a third top-level folder inherits nothing. `AssemblyVersion` must track the contract version, not the release, or the contract-skew check is either blind or noisy. A missing project entry is a known failure mode here and `--locked-mode` does not catch it |
| Independent version for `EdFi.Api.CustomValidation`, in its csproj and its pack target | Small | The same argument that gave `EdFi.Api.Plugins` its own version. Touches a merged story's project without touching its types |
| DMS host integration: one `LoadPlugins` constant, both call sites, and the build-stage Dockerfile change | Small | `RunBootstrapPhase` and the `DmsStartupPhases` constants (`Program.cs:443-462` and `Infrastructure/StartupPhaseExecutor.cs:23-30`) already exist. The Dockerfile change is mechanical but load-bearing: two more `COPY` pairs, the shared props at the trees' common parent, and two committed lock files, or `--locked-mode` fails the image build |
| CMS host integration | Small, **deferred** | The loader reports its own outcomes, so there is no scaffolding to port. With secrets deferred, no consuming epic drives CMS plugin loading; see [Applicability to the Configuration Service](#applicability-to-the-configuration-service) |
| Replace-conflict, host-service displacement, no-contract-registered, and declared-contract activation checks in a frontend-owned startup task; host-owned removal and overwrite rejection plus replace-contract masking in the recording wrapper, other removals inventoried | Small | Runs beside the DMS-1434 guard rather than inside it, because Core cannot see the hosting assembly's per-plugin records. The checks need the host contract set the cardinality metadata already holds |
| Phase A: `ContributeConfiguration` on the contract, its invocation, plugin sources placed below the environment and command-line sources, the additive `Sources` guard, and the Contribute-only exemption in the no-contract check | Small, **owned by the secrets foundations** | Decided here, built there; see [The Two Composition Phases](#the-two-composition-phases). One list move in the loader plus the Phase A test cases |
| Pulled-stock-image end-to-end proof | Small, **post-release** | Its own ticket, not a note on another story, because it cannot run until the first published image carries the loader. Blocked by that release and blocking nothing |
| Publish `EdFi.Api.Plugins` and `EdFi.Api.CustomValidation` | Small | **Release-gated leaf.** Blocked by every other row and blocking none of them. Burns two package ids permanently, so it wants its own review |
| Test assets, including the deliberately broken, contract-skewed, and dependency-version-skewed fixtures | Medium | Building broken assemblies as test assets is fiddly, and the two skew fixtures have to be built against versions the host does not carry |
| `docs/CONFIGURATION.md`, `docs/OPERATIONS.md` with both acquisition recipes verbatim, the two overlay compose files, the plugin implementer guide, host assembly manifest generated and attached by the release lane | Medium | `PLUGINS.md` is the plugin-delivery guide; the DMS-1435 rewrite is the validator-authoring one and links to it. The recipes are load-bearing documentation, exercised by the end-to-end tier as written. The manifest is one script run against the built image plus one upload step in `on-prerelease.yml` |

No row changes shipped behavior on a path DMS already ships.
Deferring the fetcher removed the only one that did, and with it the regression obligation to the ApiSchema path.
The one risk worth naming is the ordering of the end-to-end proof, and it is handled by sequencing rather than by mitigation.
The stock-image claim can only be proven in full against an image that already contains the loader, so the pulled-image tier is a post-release ticket of its own.
Until it runs the mechanism is proven against a locally built image, which tests everything except the one word "stock".

---

## Cross-References

- [ods-precedent.md](./ods-precedent.md) - the ODS/API survey, its two seams, and what this design refuses
- `reference/design/custom-validation-DMS-1345/design.md` - the contract, fan-in step, failure surfacing, and startup guard this design inherits; its "Rejected Alternatives" predicted this document
- `reference/design/custom-validation-DMS-1345/03-add-custom-validator-composition-seam-and-startup-guard.md` - the guard that transfers unchanged and the seam that does not
- `src/dms/run.sh:31-54` - the `SCHEMA_PACKAGES` block, which pins no digest and which this design does not extend, and its `__` environment binding at `:35`
- `src/dms/clis/EdFi.DataManagementService.ApiSchemaDownloader/` - the shipped NuGet client this design leaves unchanged, and the one the deferred fetcher would generalize
- `build-dms.ps1`, `SetDMSAssemblyInfo` - regenerates `src/dms/Directory.Build.props` with `VersionPrefix` equal to the release version; must **not** reach `src/plugins/Directory.Build.props`, whose version is the contract's own
- `build-dms.ps1:1832` - packs `EdFi.Api.CustomValidation` with `-p:PackageVersion=$DMSVersion`, which the independent-version amendment replaces
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/WebApplicationBuilderExtensions.cs:38,:46` - the first configuration read, which fixes where Phase A has to run, and the `AddEnvironmentVariables()` call that appends a thirteenth source above everything, which is why the environment outranks the command line in DMS as shipped
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Program.cs:29-33` - `CreateBuilder(args)`, whose twelve sources fix what "operator override" has to mean, and the bootstrap status signal's read of `AppSettings:StartupStatusFilePath`, the one key Phase A cannot supply
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Program.cs:443-462` - `RunBootstrapPhase`, which writes the failed phase and rethrows, so a loader fatal escapes host creation rather than reaching `IStartupProcessExit`
- `src/dms/Dockerfile:15-29,:56-57,:75-86` - the per-project `COPY` list and `--locked-mode` restore the build stage change has to extend, the framework-dependent publish that keeps the shared framework out of the `.deps.json`, and the runtime stage that copies into `/app`
- `src/dms/core/EdFi.DataManagementService.Core/Startup/IDmsStartupTask.cs:21` - the Core interface both startup guards implement, and the reason the plugin guard lives in the frontend rather than in Core
- `eng/verification/CustomValidationConsumer/` - the packed-package consumer proof this design extends
- `eng/docker-compose/bootstrap-dms.yml`, `eng/docker-compose/published-config.yml` - the existing `:ro` mount convention
- `docs/CONFIGURATION.md`, `docs/OPERATIONS.md` - gain the `Plugins` section and the plugin-root permission requirement
