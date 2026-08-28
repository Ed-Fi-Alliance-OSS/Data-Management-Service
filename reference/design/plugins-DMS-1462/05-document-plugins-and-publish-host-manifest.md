---
jira: TBD
jira_url: TBD
epic: TBD
source_spike: DMS-1462
---

# Story: Document Plugins for Operators and Implementers and Publish the Host Assembly Manifest

## Description

Once the mechanism runs, nothing an operator or an implementer would find explains how to use it, and one fact they need does not exist yet anywhere: what assemblies the host carries.
This story writes the three documents the design owes and adds the per-release host assembly manifest, per:

- `reference/design/plugins-DMS-1462/design.md` ("### Acquisition" for the two recipes, "### Trust Model and Verification" for the operator requirements, "### Load Isolation" for the host manifest and the compatibility surface, "### The Plugin Contract" for the versioning and additive policy, "### Startup Failure Semantics" for the fatal catalogue)

The three documents are: the `Plugins` section of `docs/CONFIGURATION.md` completed, a new plugins chapter in `docs/OPERATIONS.md` carrying both acquisition recipes verbatim, and `PLUGINS.md`, the implementer guide packed into `EdFi.Api.Plugins` as its readme.
The recipes are load-bearing documentation: draft 04 runs them as written, and a change to either must run there too.

The host manifest exists because host-first resolution makes the host's whole assembly closure part of what a plugin is compatible with, and no contract package announces it.

DMS-1435, the custom validation implementer guide, is a separate document with a separate audience: it tells an implementer how to write a validator; this guide tells them how to package and deliver any plugin.
DMS-1435 links here for delivery and this guide links there for the validator contract.

## Acceptance Criteria

- `docs/CONFIGURATION.md`'s `Plugins` section states `Directory`, `Allowed` and that it ships empty, the trim-split rule, the single-path-segment name rule, that order is invocation order, that `Allowed` is the only switch and there is no per-feature toggle, and the configuration precedence order the design fixes: environment variables, then command-line arguments, then plugin sources in allowlist order, then `appsettings.json` and the other JSON sources. It states why the environment sits above the command line, which is DMS's own `AddEnvironmentVariables()` at `Infrastructure/WebApplicationBuilderExtensions.cs:46`, and it states the one bootstrap exception, `AppSettings:StartupStatusFilePath`. The plugin-sources row is marked as landing with Phase A.
- `docs/OPERATIONS.md` gains a plugins chapter stating the plugin root must be writable by the deployment identity and never by the runtime identity, that the `:ro` mount is how a container deployment states that, and that DMS does not verify it. It carries Recipe 1 and Recipe 2 from design.md "### Acquisition" verbatim in Compose form, as the two overlay files `eng/docker-compose/plugins-dms.yml` and `eng/docker-compose/plugins-fetch-dms.yml`, and Recipe 2 in Kubernetes `initContainers` form. It states that the two are alternatives rather than additions, because both end at the single `/app/plugins` mount target; that the overlay is added with `-f` and the base compose files are unchanged; that clearing `<PluginRoot>/<Name>/` before extracting is part of the recipe; how to compute the digest to pin; and that the `fetch-plugins` image is pinned by digest, following the repository's own convention for third-party images (`eng/docker-compose/postgresql.yml:8`), with the published recipe showing an `alpine:3.XX@sha256:<digest>` placeholder the operator replaces with a digest they have approved.
- The operations chapter groups every fatal row from design.md "### Startup Failure Semantics" by what the operator does about it (fix the allowlist, fix the deployment, contact the implementer, resolve a conflict between two plugins) rather than by mechanism, and states where the reason appears: the startup status file for why, the log for what loaded.
- `PLUGINS.md` in `src/plugins/EdFi.Api.Plugins/` replaces its placeholder with the implementer guide, containing a compiling sample of an `EdFiApiPlugin` subclass overriding `Name` and `ContributeServices`, the `dotnet publish --no-self-contained -o out/<Name>` command, the directory layout with the triple-equality rule for directory, `AssemblyName`, and `Name`, and the asset-only package shape under `contentFiles/any/any/<Name>/`.
- The guide states the compatibility surface as the contract packages **plus** the host assembly manifest for the DMS version targeted, that an older plugin runs on a newer host by construction, that a newer plugin on an older host is a named fatal at load, and that a dependency newer than the manifest lists is refused at load. It states the additive-only policy for `EdFiApiPlugin` and for every plugin-implemented interface, and that a breaking change is a new package id.
- The guide states that a plugin runs with full process trust, that the load context isolates assembly identity and is not a security boundary, and that the operator is told the same in `docs/OPERATIONS.md`.
- The guide states what a plugin may and may not register: its own types and `Microsoft.Extensions.*` types freely; declared plugin contracts through `TryAddEnumerable` or a single registration according to the contract's cardinality; never a host-owned service type; never a removal of a host-owned descriptor. It states that framework descriptors a vendor helper removes are permitted and appear in the inventory event.
- The sample is mirrored verbatim into `eng/verification/PluginsConsumer/` and compiles against the packed nupkg alone.
- A script under `eng/` emits `host-assembly-manifest.md` by inspecting the **built runtime image**, not the build stage's `.deps.json`. It emits three sections: the contract versions the release carries and the runtime base image reference and digest `src/dms/Dockerfile:61` pins; one row per managed assembly under `/app` with name and `AssemblyVersion`; and, for each of the two shared frameworks the application's `runtimeconfig.json` names, the version the image carries and one row per assembly in that framework's directory. Generating it from the published `.deps.json` instead is explicitly rejected in the story and in the design: `src/dms/Dockerfile:56-57` publishes `--self-contained false`, so that file omits every shared-framework assembly, including `Microsoft.Extensions.Configuration.Abstractions` and `Microsoft.Extensions.DependencyInjection.Abstractions`, the two an implementer most needs; and `/app/Frontend` exists only in the build stage, while the runtime image and the released `DataManagementService/` folder both use `/app`.
- A test asserts the generated manifest lists `Microsoft.Extensions.Configuration.Abstractions`, which is the assembly a `.deps.json`-based generator would silently omit, so a regression to that approach fails rather than shipping a manifest that looks complete.
- `on-prerelease.yml` runs the generator against the image it builds and attaches the result to the GitHub release beside the SBOM, and `on-release.yml` promotes it with the other assets. A CI assertion confirms the manifest lists `EdFi.Api.Plugins` at the version `src/plugins/Directory.Build.props` declares and `EdFi.Api.CustomValidation` at the version its csproj declares.
- `eng/verification/Assert-PluginsPackage.ps1` asserts the packed readme is `PLUGINS.md` and is non-placeholder.

## Tasks

1. Complete the `CONFIGURATION.md` section draft 04 started.
2. Write the `OPERATIONS.md` plugins chapter with both recipes copied from design.md and the fatal catalogue regrouped by operator action.
3. Write `PLUGINS.md` against the actually shipped signatures, not against design.md.
4. Mirror the sample into `eng/verification/PluginsConsumer/`.
5. Add the manifest script driven against the built image, the prerelease attach step, the release promote step, the contract-version assertion, and the shared-framework-assembly assertion.
6. Cross-link `CUSTOM-VALIDATION.md` (DMS-1435) and `PLUGINS.md` in both directions.
