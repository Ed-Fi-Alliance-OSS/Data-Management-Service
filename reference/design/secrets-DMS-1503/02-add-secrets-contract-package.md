---
jira: TBD
jira_url: TBD
epic: DMS-1504
source_spike: DMS-1503
---

# Story: Add the `EdFi.Api.Secrets` Contract Package and Relocate `IClientSecretHasher`

## Description

A plugin cannot claim a contract that does not exist, and it cannot compile against one that ships from an assembly named for an identity provider half the deployments do not use.
This story creates the Configuration Service's secrets contract package, defines the secret resolver in it, moves `IClientSecretHasher` into it, and repairs the configuration key the host's own hasher needs, per:

- `reference/design/secrets-DMS-1503/design.md` ("### The Contract Package", "### The `IClientSecretHasher` Relocation", "### Contract Cardinality")
- `reference/design/plugins-DMS-1462/design.md` ("### The Plugin Contract" for the additive-only policy and the package-versus-assembly-name rule, "### Applicability to the Configuration Service" for the relocation's provenance)

No plugin loads yet.
This story ships a contract and a corrected host default; nothing calls a resolver until CMS host integration lands.
The hashing-iterations repair travels with the relocation because the spine handed the question here explicitly: the contract has to name a key that actually binds rather than inheriting either of the two dead ones.

## Acceptance Criteria

- A new project `src/config/contracts/EdFi.DmsConfigurationService.Secrets/` packs as `EdFi.Api.Secrets` with assembly and root namespace `EdFi.DmsConfigurationService.Secrets`, following `EdFi.Api.Identity` from `EdFi.DataManagementService.Identity`. A test asserts the assembly name inside the package, because the skew preflight matches assembly names and a package id would silently check nothing.
- The project declares its own `AssemblyVersion`, `1.0.0`, in its own csproj, independent of the CMS release version.
- **The contract is excluded from both command-line stamping lanes, and the proof reads both lanes' outputs.** The csproj declaration beats `SetDMSAssemblyInfo`'s regenerated props (`build-config.ps1:143-160`), but `Compile` (`:185-189`) and `PublishApi` (`:206-210`) pass `/p:AssemblyVersion=$DmsCSAssemblyVersion` as a global property (supplied by `.github/workflows/on-prerelease.yml:591`), which propagates through project references. A test runs `build-config.ps1 BuildAndPublish` with explicit `-DmsCSVersion` and `-DmsCSAssemblyVersion` and asserts `1.0.0.0` on `EdFi.DmsConfigurationService.Secrets.dll` in the `Compile` output and in the `PublishApi` output; the pull request names the exclusion mechanism used (`GlobalPropertiesToRemove` on the referencing `ProjectReference` items, or equivalent that also holds for a direct solution build).
- A test asserts the `AssemblyVersion` of the assembly inside the packed nupkg equals the package version.
- **`src/config/Dockerfile` stops stamping `AssemblyVersion` on the publish command line, with nothing replacing it**: the `ASSEMBLY_VERSION` argument (`:33`) and the `/p:AssemblyVersion` and `/p:FileVersion` expansions (`:39-40`) come out, and the matching build-arg pair comes out of `build-config.ps1`'s `DockerBuild` (`:463-467`). Only the locally built image changes: the pulled image comes from `src/config/Nuget.Dockerfile`, which compiles nothing (`:23-25`). This mirrors the removal `src/dms/Dockerfile` is taking in the still-open DMS-1499; that file still carries the pre-change state (`:112`, `:120-121`). A test builds the local image with an explicit `-DmsCSAssemblyVersion` and asserts the contract dll inside carries `1.0.0.0` while a CMS assembly carries the committed props value.
- `ISecretResolver` has one member, `ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)`, and `SecretReference` is a `sealed record (string Name, string? Tenant)`, a record so the contract can gain an input additively. `Tenant` is null single-tenant and the tenant name multi-tenant.
- `IClientSecretHasher` moves from `Backend.OpenIddict/Services/IClientSecretHasher.cs` into the package, members and signatures unchanged. `ClientSecretHasher` does not move: it is the host default and reads `IdentityOptions`, an OpenIddict type.
- All four registration sites and both consumers compile against the new namespace: `Config.Frontend/Infrastructure/WebApplicationBuilderExtensions.cs:206`, `Backend.Postgresql/OpenIddict/PostgresOpenIddictServiceExtensions.cs:37` and `:93` (an overload nothing calls; it changes because it compiles, and deleting it is out of scope), `Backend.Mssql/OpenIddict/MssqlOpenIddictServiceExtensions.cs:35`, `Backend.OpenIddict/Repositories/OpenIddictClientRepository.cs:21`, and `Backend.OpenIddict/Services/OpenIddictTokenManager.cs:28`. The criterion is that the solution builds, tests included.
- `src/config/Dockerfile`'s build stage gains the new project's csproj, lock file, and sources in this story, because its per-project `COPY` list (`:14-20`, `:22`, `:24-30`) cannot restore a referenced project outside it.
- `Backend`, `Backend.OpenIddict`, `Backend.Postgresql`, `Backend.Mssql`, and `Config.Frontend` take the `ProjectReference` with regenerated `packages.lock.json` files. A test asserts the project is present in `src/config/EdFi.DmsConfigurationService.sln`, because `--locked-mode` does not catch a missing project entry.
- XML documentation on both contracts states the implementer obligations: replace cardinality with a plain `Add` and never a `TryAdd`, singleton and unkeyed registration, and the tenant as an argument because a plugin instance outlives every tenant.
- **`IdentitySettings:ClientSecretHashingIterations` becomes the live key**: the repository's only `Configure<IdentityOptions>` (`Backend.OpenIddict/Extensions/OpenIddictServiceCollectionExtensions.cs:26`) gains the assignment with default `210000`, and the model property renames from `HashingIterations` to `ClientSecretHashingIterations` (`Backend.OpenIddict/Models/IdentityOptions.cs:73`, read at `Backend.OpenIddict/Services/ClientSecretHasher.cs:38` and `:97`).
- `eng/docker-compose/published-config.yml:54` and `eng/docker-compose/local-config.yml:58` re-point `IdentitySettings__HashingIterations` to `IdentitySettings__ClientSecretHashingIterations`, still fed by `DMS_CONFIG_IDENTITY_HASHING_ITERATIONS`. The variable stays: `eng/docker-compose/setup-openiddict.ps1` hashes every bootstrapped client secret with it (`:36`, `:212`) through a resolver that throws when it is configured nowhere, and every base `.env*` file under `eng/docker-compose/` defines it. The re-point makes the bootstrap's hash count and the host's verify count read one knob; see design.md, "The `IClientSecretHasher` Relocation", for why no working deployment's behavior changes.
- `IdentitySettings:HashingIterations` remains unbound, asserted by a test, so both keys are never live at once.
- Tests pin the repair: a non-default `IdentitySettings:ClientSecretHashingIterations` reaches `ClientSecretHasher` (the assertion that fails on main today); the shipped `Config.Frontend/appsettings.json:45` value equals the model default; and a secret hashed at one count fails verification at another, pinning the stored-format property (`ClientSecretHasher.cs:52-55` stores no count; `:97` derives at the configured one).
- `docs/CONFIGURATION.md` gains the key with its default and the consequence: raising it invalidates every client secret hashed at the old count, and the remedy is to re-issue them.
- A per-pull-request lane packs `EdFi.Api.Secrets` into a local folder feed and compiles a scratch consumer implementing both contracts, following `eng/verification/CustomValidationConsumer/`, asserting the packed assembly version equals the package version. `.github/workflows/on-config-pullrequest.yml` runs it via the existing `src/config/*` relevance case (`:124`).
- The existing CMS unit and end-to-end suites pass unchanged for a deployment that sets neither iteration key.
- `dotnet build --no-restore src/config/EdFi.DmsConfigurationService.sln` and `dotnet test src/config/EdFi.DmsConfigurationService.sln` pass.

## Tasks

1. Add the contract project with `ISecretResolver`, `SecretReference`, package metadata, and the implementer-obligation XML documentation.
2. Move `IClientSecretHasher` into the package and update the four registration sites, both consumers, and the affected tests.
3. Exclude the contract from the two command-line stamping lanes, remove the Docker lane's stamping, and add the two-lane and image version proofs.
4. Bind `IdentitySettings:ClientSecretHashingIterations`, rename the model property, and re-point the two compose files.
5. Add the Dockerfile build-stage entries, the five project references with lock files, and the solution-membership test.
6. Add the pack-and-consumer-verify lane to the config pull-request workflow.
7. Update `docs/CONFIGURATION.md` with the live key and its re-issue consequence.
