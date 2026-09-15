---
jira: TBD
jira_url: TBD
epic: DMS-1504
source_spike: DMS-1503
---

# Story: Document and Publish `EdFi.Api.Secrets`

## Description

Every story before this one proves the packaged path against a local folder feed.
This one writes the documentation an operator and an implementer actually use, and publishes the package an external implementer cannot work without, per:

- `reference/design/secrets-DMS-1503/design.md` ("### Trust Model Notes", "### Configuration Surface", "## Level of Effort" for what the documentation owes)
- `reference/design/plugins-DMS-1462/design.md` ("### The Plugin Contract" for the publish policy and the promotion rule)

Publishing is the last ticket rather than the first: blocked by every other story, blocking none, and it burns a package id permanently, so it wants its own review.
The Ed-Fi Alliance ships no vault plugin, for any vault; what it ships is what the ODS documentation ships for the same problem, worked examples an implementer copies, written against `EdFiApiPlugin.ContributeConfiguration` instead of ODS's `IHostConfigurationActivity.ConfigureHost(IHostBuilder)`.

## Acceptance Criteria

- **The operator chapter**, in `docs/`, covers: what a secrets plugin is and the two things it can do; the process-global secrets Phase A serves, named individually (two in DMS, six in CMS, the two certificate passwords marked as absent from `appsettings.json`, and `OtlpLogging:Headers` in each host); the one deployment credential Phase A structurally cannot serve, `DATABASE_CONNECTION_STRING_ADMIN`, consumed by the DMS container entrypoint before the .NET host exists (`src/dms/run.sh:14-16`); the secret reference syntax and where it may appear; `SecretsSettings:CacheExpirationSeconds` and the rotation window it defines; and that rotating any process-global secret takes a restart regardless of the cache, because each is captured once and never re-read.
- The chapter states the rotation order and the reason: rotate first, revoke after the propagation window, because DMS keeps using the pre-rotation value for that window; that an immediate rotation takes a restart of both hosts, since `RefreshInstancesIfExpiredAsync` does nothing when refresh is disabled or the expiration is not positive (`src/dms/core/EdFi.DataManagementService.Core/Configuration/ConfigurationServiceDataStoreProvider.cs:155-165`); and that a rotation retires and rebuilds DMS's connection pool for that data store, since pool ownership keys on the string verbatim.
- The chapter states the trust position in every half: full process trust, operator-controlled allowlist, static vault credential reduces the problem to one secret while ambient workload identity (what both worked examples use) reduces it to none, and, most importantly, that CMS can still produce the resolved value - cached in process for the expiration and returned re-encrypted on every limited-access read across four endpoints (`Config.Frontend/Modules/DataStoreModule.cs:23-24`, `DataStoreDerivativeModule.cs:21-22`). What the mechanism removes is the value at rest in CMS's database and backups; an operator whose requirement is that nothing but the vault can produce the secret is not served, stated plainly.
- The chapter states the hashing consequence: raising `IdentitySettings:ClientSecretHashingIterations` invalidates every client secret hashed at the old count, and the remedy is re-issue.
- **The implementer guide**, shipped as the contract package's readme and cross-linked from `src/plugins/EdFi.Api.Plugins/PLUGINS.md`, covers: the two contracts and their members; replace cardinality with plain `Add` and never `TryAdd`, with the recording-wrapper reason; singleton-and-unkeyed registration and what the host does otherwise; the tenant as an argument; concurrency safety; and what the host does with a resolver that throws, cancels, or returns nothing.
- The guide states what CMS enforces and what it cannot: the resolve timeout bounds one call (a hung resolver fails a read, not a request thread; the unreclaimed-thread residual stated; honour the cancellation token), and nothing enforces that a resolver not log what it resolved, stated as an obligation.
- **Two worked configuration-source examples**, Azure Key Vault and AWS Systems Manager Parameter Store, each a complete `EdFiApiPlugin` subclass overriding `ContributeConfiguration`, reading its own vault address from `bootstrapConfiguration`, adding one source, noting the host places the source, and carrying the ambient-credential note - matching the two examples the ODS page carries for the same services.
- **One worked resolver example** over the same vault client, showing the `(name, tenant)` pair turned into a vault path, a tenant-agnostic deployment ignoring the tenant, and the note that the host caches so the plugin need not.
- Both guides link to `PLUGINS.md` for packaging, delivery, the allowlist, and the trust model rather than restating any of it, and `PLUGINS.md` links back.
- **Publication.** `EdFi.Api.Secrets` is packed and pushed by the prerelease lane under the spine's policy: publish when absent from the feed, skip when present and unchanged, fail when present and different, compared three-way over the normalized public surface, the XML documentation file, and the nuspec dependency list. The CMS lane does not implement that policy today (`build-config.ps1`'s `PushPackage` runs `dotnet nuget push` with no `--skip-duplicate`, `:444`), so this story adds it; DMS-1501 does the same work for `EdFi.Api.Plugins` and is the implementation to follow.
- Release promotion queries the release view first, skips when present, promotes from the prerelease view, and fails naming the package when it is in neither, because a never-published version is absent from both views.
- The package version is passed explicitly rather than derived from the release tag, because this contract's version deliberately does not move with the release.
- A test asserts the `AssemblyVersion` inside the published nupkg equals the package version, and that a build with an explicit release version leaves the packed contract version untouched.
- The scratch consumer from the contract story is extended to compile against the published package once a version exists on the feed.
- `docs/OPERATIONS.md`'s plugin chapter gains the Configuration Service: the two acquisition recipes, the CMS mount target, and the `plugins-config.yml` overlay, asserted equal to the committed file by the document-versus-file check DMS-1500 established.
- `dotnet test src/config/EdFi.DmsConfigurationService.sln` passes and the packed package installs into the scratch consumer.

## Tasks

1. Write the operator chapter (served secrets, out-of-reach values, rotation order and windows, trust position, hashing consequence).
2. Write the implementer guide as the packed readme, cross-linked with `PLUGINS.md`.
3. Write the two configuration-source worked examples and the resolver example.
4. Add the publish policy to the prerelease lane and the promotion rule to the release lane.
5. Extend the scratch consumer to the published package and add the version assertions.
6. Add the CMS section to `docs/OPERATIONS.md` with the document-versus-file check.
