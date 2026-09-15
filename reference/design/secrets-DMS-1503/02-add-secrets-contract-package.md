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
- `reference/design/plugins-DMS-1462/design.md` ("### The Plugin Contract", "### Applicability to the Configuration Service")

No plugin loads yet.
This story ships a contract and a corrected host default; nothing calls a resolver until CMS host integration lands.

## Technical Implementation

**Citation convention.**
Unprefixed paths are relative to the repository root.
`Config.Frontend/` names `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/`.
`Backend/`, `Backend.OpenIddict/`, `Backend.Postgresql/`, and `Backend.Mssql/` name the corresponding directories under `src/config/backend/EdFi.DmsConfigurationService.`.

**The package id and the assembly name are different strings and are never used interchangeably.**
`EdFi.Api.*` is the prefix for a contract package an Ed-Fi API host consumes.
The assembly and namespace follow the tree the project lives in, which is what `EdFi.Api.Identity` already does by building from `src/dms/core/EdFi.DataManagementService.Identity/`.
An assembly reference carries an assembly name, so the loader's skew preflight matches on assembly names, and a check written against the package id would silently check nothing.

**Both contracts ship in one package because they are one type's contracts.**
The additive-only policy is a property of a package, so two packages would mean two versions, two entries in the skew preflight, and two package ids burned permanently, to separate two interfaces the same plugin will usually implement.

**`SecretReference` is a record so the contract can gain an input additively.**
The additive-only policy forbids changing a member's signature for the life of the package, so a two-parameter method could never gain a third input.
A record can gain a property with a default, which is the same additive move a virtual with a no-op body is for the base class.

**`ClientSecretHasher` stays where it is while its interface moves.**
The implementation is the host default and reads `IdentityOptions`, an OpenIddict type, so moving it would drag that dependency into the contract package.

**The version has to survive two command-line stamping lanes, and a csproj declaration only survives one.**
A csproj-declared `AssemblyVersion` beats the props file `SetDMSAssemblyInfo` regenerates (`build-config.ps1:143-160`).
It loses to a global property, and both `Compile` (`:185-189`) and `PublishApi` (`:206-210`) pass `/p:AssemblyVersion=$DmsCSAssemblyVersion`, supplied by `.github/workflows/on-prerelease.yml:591`, which propagates through project references.
So the exclusion has to be explicit, and the proof has to read both lanes' outputs rather than one.

**The Docker lane is a third stamping path and it is removed rather than excluded.**
`src/config/Dockerfile` supplies `ASSEMBLY_VERSION` (`:33`) and expands it into `/p:AssemblyVersion` and `/p:FileVersion` (`:39-40`), fed by a build-arg pair in `build-config.ps1`'s `DockerBuild` (`:463-467`).
Only the locally built image is affected, because the pulled image comes from `src/config/Nuget.Dockerfile`, which compiles nothing (`:23-25`).
This mirrors the removal `src/dms/Dockerfile` is taking in the still-open DMS-1499; that file still carries the pre-change state (`:112`, `:120-121`).

**The Dockerfile's per-project `COPY` list cannot restore a project outside it.**
`src/config/Dockerfile` copies project files one by one (`:14-20`, `:22`, `:24-30`), so a newly referenced project has to be added there in the same story that adds the reference, or the locked restore fails in the image build.

**The exclusion mechanism has to survive a direct solution build as well as the scripted lanes.**
`GlobalPropertiesToRemove` on the referencing `ProjectReference` items is the candidate; whatever is used, the pull request names it and confirms both paths.

**The stored hash format carries no iteration count.**
`Backend.OpenIddict/Services/ClientSecretHasher.cs:52-55` stores a version byte, a salt length, the salt, and the subkey, and verification derives with the configured count (`:97`) rather than with one read back from the stored value.
That is what makes a changed count invalidate existing secrets.

**The hashing-iterations key is dead today, and the contract cannot inherit a dead knob.**
`Backend.OpenIddict/Extensions/OpenIddictServiceCollectionExtensions.cs:26` is the repository's only `services.Configure<IdentityOptions>`.
It assigns thirteen properties and the iteration count is not among them, so `ClientSecretHasher` always reads the model default of `210000` (`Backend.OpenIddict/Models/IdentityOptions.cs:73`, read at `Backend.OpenIddict/Services/ClientSecretHasher.cs:38` and `:97`).
The spine handed this question here explicitly: the contract has to name a key that actually binds rather than inheriting either of the two dead ones.

**`DMS_CONFIG_IDENTITY_HASHING_ITERATIONS` stays, and removing it would break the bootstrap.**
`eng/docker-compose/setup-openiddict.ps1` takes it as `$HashIterations` (`:36`) and hashes every bootstrapped client secret with it (`:212`), through a resolver that throws by key name when the value is configured nowhere.
Every base `.env*` file under `eng/docker-compose/` defines it at `210000`.
Re-pointing the compose mapping to the live key makes the bootstrap's hash count and the host's verify count read one knob, where today they agree only by both happening to be `210000`.

**No working deployment's behavior changes.**
The repository's own examples all set `210000`, which equals the model default, so the key becoming live changes nothing for them.
A deployment that had set the variable to something else was already broken, because the bootstrap has always hashed at the variable's value while the host verified at the model default.

## Acceptance Criteria

**Contract project**

- `src/config/contracts/EdFi.DmsConfigurationService.Secrets/` exists and packs as package id `EdFi.Api.Secrets`.
- The assembly name and root namespace are `EdFi.DmsConfigurationService.Secrets`.
- A test asserts the assembly name inside the packed nupkg is `EdFi.DmsConfigurationService.Secrets`.
- The csproj declares `Version`, `AssemblyVersion`, and `FileVersion` of `1.0.0`.
- A test asserts the `AssemblyVersion` of the assembly inside the packed nupkg equals the package version.

**Contract surface**

- `ISecretResolver` declares exactly one member: `ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)`.
- `SecretReference` is a `sealed record` with members `string Name` and `string? Tenant`.
- `IClientSecretHasher` is declared in the package with its three members and signatures unchanged from `Backend.OpenIddict/Services/IClientSecretHasher.cs`.
- `ClientSecretHasher` remains in `Backend.OpenIddict`.
- XML documentation on both contracts states: replace cardinality with a plain `Add` and never a `TryAdd`; singleton and unkeyed registration; and that the tenant is an argument because a plugin instance outlives every tenant.
- XML documentation on `SecretReference` states that `Tenant` is null in a single-tenant deployment and carries the tenant name in a multi-tenant one.

**Version stamping**

- A test runs `build-config.ps1 BuildAndPublish` with explicit `-DmsCSVersion` and `-DmsCSAssemblyVersion` and asserts `EdFi.DmsConfigurationService.Secrets.dll` carries `1.0.0.0` in:
    - the `Compile` output
    - the `PublishApi` output
- The pull request names the exclusion mechanism used and confirms it also holds for a direct solution build.
- `src/config/Dockerfile` no longer declares `ASSEMBLY_VERSION` (`:33`) and no longer expands `/p:AssemblyVersion` or `/p:FileVersion` (`:39-40`), with nothing replacing them.
- `build-config.ps1`'s `DockerBuild` no longer passes the matching build-arg pair (`:463-467`).
- A test builds the local image with an explicit `-DmsCSAssemblyVersion` and asserts the contract dll inside carries `1.0.0.0` while a CMS assembly carries the committed props value.

**Relocation**

- These four registration sites compile against the new namespace: `Config.Frontend/Infrastructure/WebApplicationBuilderExtensions.cs:206`, `Backend.Postgresql/OpenIddict/PostgresOpenIddictServiceExtensions.cs:37` and `:93`, and `Backend.Mssql/OpenIddict/MssqlOpenIddictServiceExtensions.cs:35`.
- Both consumers compile against the new namespace: `Backend.OpenIddict/Repositories/OpenIddictClientRepository.cs:21` and `Backend.OpenIddict/Services/OpenIddictTokenManager.cs:28`.
- The unreachable overload at `Backend.Postgresql/OpenIddict/PostgresOpenIddictServiceExtensions.cs:93` is updated because it compiles and is not deleted.

**Project wiring**

- `Backend`, `Backend.OpenIddict`, `Backend.Postgresql`, `Backend.Mssql`, and `Config.Frontend` each take a `ProjectReference` on the contract project.
- Each of those five projects has a regenerated `packages.lock.json`.
- The contract project is in `src/config/EdFi.DmsConfigurationService.sln`, asserted by a test.
- `src/config/Dockerfile`'s build stage copies the new project's csproj, lock file, and sources, added to the per-project `COPY` list at `:14-20`, `:22`, and `:24-30`.

**Hashing-iterations repair**

- `Backend.OpenIddict/Extensions/OpenIddictServiceCollectionExtensions.cs:26` assigns the iteration count from `IdentitySettings:ClientSecretHashingIterations` with a default of `210000`.
- The model property at `Backend.OpenIddict/Models/IdentityOptions.cs:73` is renamed from `HashingIterations` to `ClientSecretHashingIterations`.
- `eng/docker-compose/published-config.yml:54` and `eng/docker-compose/local-config.yml:58` re-point `IdentitySettings__HashingIterations` to `IdentitySettings__ClientSecretHashingIterations`, still fed by `DMS_CONFIG_IDENTITY_HASHING_ITERATIONS`.
- `DMS_CONFIG_IDENTITY_HASHING_ITERATIONS` remains defined in every `.env*` file that defines it today, and `eng/docker-compose/setup-openiddict.ps1` is unchanged.
- Tests pin the repair:
    - `IdentitySettings:HashingIterations` set alone leaves the hasher at the default, so the two keys are never both live
    - a non-default `IdentitySettings:ClientSecretHashingIterations` reaches `ClientSecretHasher` (this assertion fails on main today)
    - the value at `Config.Frontend/appsettings.json:45` equals the model default
    - a secret hashed at one iteration count fails verification at another

**Verification lane**

- A per-pull-request lane packs `EdFi.Api.Secrets` into a local folder feed and compiles a scratch consumer implementing both contracts, following `eng/verification/CustomValidationConsumer/`.
- That lane asserts the packed assembly version equals the package version.
- `.github/workflows/on-config-pullrequest.yml` runs the lane through the existing `src/config/*` relevance case (`:124`).

**Build**

- These pass:
    - `dotnet build --no-restore src/config/EdFi.DmsConfigurationService.sln`
    - `dotnet test src/config/EdFi.DmsConfigurationService.sln`
- The existing CMS unit and end-to-end suites pass unchanged for a deployment that sets neither iteration key.

**Documentation**

- `docs/CONFIGURATION.md` documents `IdentitySettings:ClientSecretHashingIterations` with its default and states that raising it invalidates every client secret hashed at the old count, with re-issue as the remedy.

## Tasks

1. Add the contract project with `ISecretResolver`, `SecretReference`, package metadata, and the implementer-obligation XML documentation.
2. Move `IClientSecretHasher` into the package and update the four registration sites, both consumers, and the affected tests.
3. Exclude the contract from the `Compile` and `PublishApi` stamping lanes and add the two-lane version proof.
4. Remove the Docker lane's `AssemblyVersion` stamping from `src/config/Dockerfile` and `build-config.ps1`, and add the image version proof.
5. Bind `IdentitySettings:ClientSecretHashingIterations`, rename the model property, and re-point the two compose mappings.
6. Add the hashing repair tests and the unbound-key assertion.
7. Add the Dockerfile build-stage entries, the five project references with lock files, and the solution-membership test.
8. Add the pack-and-consumer-verify lane to the config pull-request workflow.
9. Update `docs/CONFIGURATION.md`.
