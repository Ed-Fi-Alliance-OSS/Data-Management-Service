---
jira: TBD
jira_url: TBD
epic: DMS-1504
source_spike: DMS-1503
---

# Story: Document and Publish `EdFi.Api.Secrets`

## Description

Every story before this one proves the packaged path against a local folder feed and publishes nothing.
This one writes the documentation an operator and an implementer actually use, and publishes the package an external implementer cannot work without, per:

- `reference/design/secrets-DMS-1503/design.md` ("### Trust Model Notes", "### Configuration Surface", "## Level of Effort")
- `reference/design/plugins-DMS-1462/design.md` ("### The Plugin Contract" for the publish policy and the promotion rule)

Publishing is the last ticket rather than the first: blocked by every other story, blocking none, and it burns a package id permanently, so it wants its own review.

## Technical Implementation

**Citation convention.**
Unprefixed paths are relative to the repository root.
`Config.Frontend/` names `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/`.

**The Alliance ships no vault plugin, for any vault.**
An Ed-Fi-authored Azure Key Vault or AWS Parameter Store plugin would commit the Alliance to a cloud SDK's release cadence, authentication surface, and CVEs for a component every deployment configures differently.
The ODS documentation's own answer to the same problem is worked examples an implementer copies, and this story writes the DMS equivalents, against `EdFiApiPlugin.ContributeConfiguration` instead of ODS's `IHostConfigurationActivity.ConfigureHost(IHostBuilder)`.

**The CMS lane does not implement the publish policy today, so this story writes it.**
`build-config.ps1`'s `PushPackage` runs `dotnet nuget push` with no `--skip-duplicate` (`:444`).
DMS-1501 does the same work for `EdFi.Api.Plugins` in the DMS lane and is the implementation to follow, but it is a port rather than a copy: CMS artifacts ride the `cs-` tag prefix through `attach-cs-artifacts-to-release`.

**Promotion queries the release view first because absence from the prerelease view means two different things.**
A version already promoted by an earlier release is absent from the prerelease view, and so is a version that was never published at all.
Treating absence as "already promoted" would let a missed publish log "nothing to do" and exit zero.

**The package version is passed explicitly rather than derived from the release tag.**
`Invoke-Promote` derives the version from the release tag, and this contract's version deliberately does not move with the platform release, so promoting at the release version would ask the feed for a version that does not exist.

**The trust position has to be stated in full, including the part that does not favor the feature.**
CMS can still produce the resolved value: it is cached in process for the expiration and returned re-encrypted on every limited-access read across four endpoints.
What the mechanism removes is the value at rest in CMS's database and its backups.
An operator whose requirement is that nothing but the vault can produce the secret is not served by this, and the chapter says so rather than leaving them to discover it.

**Rotation has an order and a reason, and getting it backwards loses reads.**
Rotate first and revoke after the propagation window, because DMS keeps using the pre-rotation value for that window.
An immediate rotation takes a restart of both hosts, since `RefreshInstancesIfExpiredAsync` does nothing when refresh is disabled or the expiration is not positive (`src/dms/core/EdFi.DataManagementService.Core/Configuration/ConfigurationServiceDataStoreProvider.cs:155-165`).
A rotation also retires and rebuilds DMS's connection pool for that data store, because pool ownership keys on the connection string verbatim.

**Process-global secrets rotate on a different clock from connection strings.**
Each is captured once and never re-read, so rotating one takes a restart regardless of any cache setting.

## Acceptance Criteria

**Operator chapter**

- A chapter in `docs/` states what a secrets plugin is and the two things it can do, and states:
  - the process-global secrets Phase A serves, named individually: two in DMS, six in CMS, and `OtlpLogging:Headers` in each host
  - that `IdentitySettings:CertificatePassword` and `IdentitySettings:DevCertificatePassword` are absent from `appsettings.json`
  - that `DATABASE_CONNECTION_STRING_ADMIN` is the one deployment credential Phase A structurally cannot serve, because `src/dms/run.sh:14-16` parses it before the .NET host exists
  - the secret reference syntax and where it may appear
  - `SecretsSettings:CacheExpirationSeconds` and the rotation window it defines
  - that rotating any process-global secret takes a restart regardless of the cache, because each is captured once and never re-read
- The chapter states the rotation rules:
  - rotate first, revoke after the propagation window
  - an immediate rotation takes a restart of both hosts, citing `src/dms/core/EdFi.DataManagementService.Core/Configuration/ConfigurationServiceDataStoreProvider.cs:155-165`
  - a rotation retires and rebuilds DMS's connection pool for that data store
- The chapter states the trust position in every half:
  - full process trust and an operator-controlled allowlist
  - a static vault credential reduces the problem to one secret, and ambient workload identity reduces it to none
  - CMS can still produce the resolved value, cached in process and returned re-encrypted on every limited-access read across the four endpoints (`Config.Frontend/Modules/DataStoreModule.cs:23-24`, `Config.Frontend/Modules/DataStoreDerivativeModule.cs:21-22`)
  - plainly, that an operator whose requirement is that nothing but the vault can produce the secret is not served
- The chapter states that raising `IdentitySettings:ClientSecretHashingIterations` invalidates every client secret hashed at the old count, with re-issue as the remedy.

**Implementer guide**

- The guide ships as the contract package's readme and is cross-linked from `src/plugins/EdFi.Api.Plugins/PLUGINS.md`, which links back.
- The guide documents both contracts and their members, and states:
  - replace cardinality with a plain `Add` and never a `TryAdd`, with the recording-wrapper reason
  - singleton and unkeyed registration, and what the host does otherwise
  - that the tenant is an argument, and why
  - the concurrency-safety obligation
  - what the host does with a resolver that throws, cancels, or returns nothing
- The guide states what CMS cannot enforce:
  - the resolve timeout bounds one call, a hung resolver fails a read rather than a request thread, the unreclaimed-thread residual, and the obligation to honour the cancellation token
  - nothing enforces that a resolver not log what it resolved, stated as an implementer obligation
- Both guides link to `PLUGINS.md` for packaging, delivery, the allowlist, and the trust model rather than restating any of it.

**Worked examples**

- An Azure Key Vault example is a complete `EdFiApiPlugin` subclass overriding `ContributeConfiguration`, reading its vault address from `bootstrapConfiguration`, adding one source, noting that the host places it, and carrying the ambient-credential note.
- An AWS Systems Manager Parameter Store example meets the same criteria.
- One resolver example over the same vault client shows the `(name, tenant)` pair turned into a vault path, a tenant-agnostic deployment ignoring the tenant, and the note that the host caches so the plugin need not.

**Publication**

- The prerelease lane packs and pushes `EdFi.Api.Secrets`, with three outcomes:
  - publishes when the id and version are absent from the feed
  - skips, exiting zero, when present and unchanged
  - fails when present and different, naming the id, the version, and which comparison differed
- The comparison covers three things, each normalized: the assembly's public surface, the XML documentation file, and the nuspec dependency list.
- The package version is passed explicitly rather than derived from the release tag.
- The release lane has three outcomes:
  - skips when the version is present in the release view, which it queries first
  - promotes from the prerelease view when the version is absent from the release view
  - fails, naming the package and version, when the version is in neither view
- Tests assert the version independence:
  - the `AssemblyVersion` inside the published nupkg equals the package version
  - a build with an explicit release version leaves the packed contract version untouched
- The scratch consumer from story 02 is extended to compile against the published package once a version exists on the feed.

**Operations documentation**

- `docs/OPERATIONS.md`'s plugin chapter gains the Configuration Service: the two acquisition recipes, the CMS mount target, and the `plugins-config.yml` overlay.
- That overlay content is asserted equal to the committed file by the document-versus-file check DMS-1500 established.

**Build**

- `dotnet test src/config/EdFi.DmsConfigurationService.sln` passes, and the packed package installs into the scratch consumer.

## Tasks

1. Write the operator chapter: served secrets, out-of-reach values, rotation order and windows, trust position, and the hashing consequence.
2. Write the implementer guide as the packed readme and cross-link it with `PLUGINS.md`.
3. Write the two configuration-source worked examples and the resolver example.
4. Add the publish-when-absent, skip-when-unchanged, fail-when-changed policy to the config prerelease lane.
5. Add the three-outcome promotion step to the release lane with an explicit version.
6. Extend the scratch consumer to the published package and add the version assertions.
7. Add the Configuration Service section to `docs/OPERATIONS.md` with the document-versus-file check.
