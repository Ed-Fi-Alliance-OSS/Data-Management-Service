# Stryker.NET Adoption Plan

This plan covers the first mutation-testing step only:

- Add pinned Stryker.NET local tooling.
- Add targeted unit-test mutation configs.
- Start manual mutation runs against high-value targets.

Quality gates, score thresholds, integration-test mutation runs, E2E mutation runs, and CI automation are intentionally deferred.

## Current Fit

The repository already has NUnit, FluentAssertions, FakeItEasy, Coverlet, and local dotnet tool manifests. Mutation testing should complement existing line and branch coverage by showing where tests execute code but do not assert behavior strongly enough.

Use Stryker.NET for .NET mutation testing. Stryker mutates one project under test per run. Many DMS and CMS unit test projects reference multiple production projects, so each mutation run must explicitly name the production project it is mutating.

Reference:

- https://stryker-mutator.io/docs/stryker-net/configuration/

## Phase 1: Pin Stryker.NET Tooling

Add `dotnet-stryker` to the local tool manifests that developers are likely to use:

- `.config/dotnet-tools.json`
- `src/dms/.config/dotnet-tools.json`
- `src/config/.config/dotnet-tools.json`

Use the same pinned version everywhere. As of June 23, 2026, `dotnet tool search dotnet-stryker` reports `4.15.0` as the latest version.

Suggested implementation commands:

```bash
dotnet tool install dotnet-stryker --version 4.15.0

pushd src/dms
dotnet tool install dotnet-stryker --version 4.15.0
popd

pushd src/config
dotnet tool install dotnet-stryker --version 4.15.0
popd
```

If the tool is already installed, use `dotnet tool update dotnet-stryker --version 4.15.0` from the same directories.

After updating manifests, verify tool restore from the repository root and from each nested area:

```bash
dotnet tool restore

pushd src/dms
dotnet tool restore
popd

pushd src/config
dotnet tool restore
popd
```

## Phase 2: Add Targeted Stryker Configs

Add a `stryker-config.json` file to each selected unit test project directory. Keep the first configs narrow and manual-friendly:

- Use `"configuration": "Release"` to match normal validation builds.
- Use `"project"` to identify the single production project under test.
- Use `"reporters": ["html", "json", "progress"]`.
- Keep `"thresholds": { "high": 0, "low": 0, "break": 0 }` until score gates are intentionally introduced.
- Use `"mutate"` includes/excludes to avoid generated files, test support, assembly info, `bin`, and `obj`.

### Target 1: DMS Backend Plans

Production project:

- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/EdFi.DataManagementService.Backend.Plans.csproj`

Test project:

- `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj`

Why this target is first:

- It contains high-value planning logic for relational SQL, authorization, profiles, hydration, and change queries.
- Surviving mutants here are likely to expose weak behavioral assertions rather than simple coverage gaps.

Config location:

- `src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/stryker-config.json`

Initial config shape:

```json
{
  "stryker-config": {
    "solution": "../../EdFi.DataManagementService.sln",
    "project": "EdFi.DataManagementService.Backend.Plans.csproj",
    "configuration": "Release",
    "target-framework": "net10.0",
    "reporters": ["html", "json", "progress"],
    "thresholds": {
      "high": 0,
      "low": 0,
      "break": 0
    },
    "mutate": [
      "**/*.cs",
      "!**/Properties/AssemblyInfo.cs",
      "!**/bin/**",
      "!**/obj/**"
    ]
  }
}
```

### Target 2: DMS Backend Relational Model

Production project:

- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/EdFi.DataManagementService.Backend.RelationalModel.csproj`

Test project:

- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj`

Why this target is early:

- It owns schema/table/key mapping behavior that affects PostgreSQL, MSSQL, DDL, reads, and writes.
- Mutation runs should expose missing edge-case assertions around identity, descriptors, references, and relational shape.

Config location:

- `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit/stryker-config.json`

Use the same config shape as Target 1, changing:

```json
"project": "EdFi.DataManagementService.Backend.RelationalModel.csproj"
```

### Target 3: DMS Backend DDL

Production project:

- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/EdFi.DataManagementService.Backend.Ddl.csproj`

Test project:

- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/EdFi.DataManagementService.Backend.Ddl.Tests.Unit.csproj`

Why this target is early:

- DDL generation is deterministic and assertion-friendly.
- Mutants in quoting, ordering, column shape, constraints, and dialect-specific branches should usually be killable with focused tests.

Config location:

- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/stryker-config.json`

Use the same config shape as Target 1, changing:

```json
"project": "EdFi.DataManagementService.Backend.Ddl.csproj"
```

### Target 4: DMS Core Validation And Authorization

Production project:

- `src/dms/core/EdFi.DataManagementService.Core/EdFi.DataManagementService.Core.csproj`

Test project:

- `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj`

Why this target is useful:

- Core owns validation, request handling, pipeline, response, and security behavior.
- The unit test project references many production projects, so the Stryker config must force mutation to `EdFi.DataManagementService.Core.csproj`.

Config location:

- `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/stryker-config.json`

Use the same config shape as Target 1, changing:

```json
"project": "EdFi.DataManagementService.Core.csproj"
```

Initial mutate scope:

```json
"mutate": [
  "Handler/**/*.cs",
  "Middleware/**/*.cs",
  "Response/**/*.cs",
  "Security/**/*.cs",
  "Validation/**/*.cs",
  "!**/Properties/AssemblyInfo.cs",
  "!**/bin/**",
  "!**/obj/**"
]
```

Do not mutate all of Core on the first pass. Expand after the first manual runs are understood.

### Target 5: CMS Backend Commands And Validators

Production project:

- `src/config/backend/EdFi.DmsConfigurationService.Backend/EdFi.DmsConfigurationService.Backend.csproj`

Test project:

- `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/EdFi.DmsConfigurationService.Backend.Tests.Unit.csproj`

Why this target is useful:

- CMS command, validator, claims, authorization metadata, and service behavior is heavily branch-oriented and unit-testable.
- The test project also references provider projects, so mutate only the backend project first.

Config location:

- `src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/stryker-config.json`

Use the same config shape as Target 1, changing:

```json
"solution": "../../EdFi.DmsConfigurationService.sln",
"project": "EdFi.DmsConfigurationService.Backend.csproj"
```

Initial mutate scope:

```json
"mutate": [
  "AuthorizationMetadata/**/*.cs",
  "Claims/**/*.cs",
  "ClaimsDataLoader/**/*.cs",
  "Models/**/*.cs",
  "Services/**/*.cs",
  "!**/Properties/AssemblyInfo.cs",
  "!**/bin/**",
  "!**/obj/**"
]
```

## Phase 3: Manual Runs To Improve Test Quality

Start with manual Stryker runs. The immediate goal is not a score. The goal is to identify survived mutants that show missing or weak assertions.

### First Broad Runs

DMS Backend Plans:

```bash
cd src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit
dotnet tool restore
dotnet stryker --config-file stryker-config.json
```

DMS Backend Relational Model:

```bash
cd src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit
dotnet tool restore
dotnet stryker --config-file stryker-config.json
```

DMS Backend DDL:

```bash
cd src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit
dotnet tool restore
dotnet stryker --config-file stryker-config.json
```

DMS Core:

```bash
cd src/dms/core/EdFi.DataManagementService.Core.Tests.Unit
dotnet tool restore
dotnet stryker --config-file stryker-config.json
```

CMS Backend:

```bash
cd src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit
dotnet tool restore
dotnet stryker --config-file stryker-config.json
```

Open the generated HTML report under the test project's `StrykerOutput` directory after each run.

### Focused Runs While Fixing Tests

When a broad run is noisy, narrow the mutation scope to one folder or file:

```bash
dotnet stryker --config-file stryker-config.json --mutate "ChangeQueries/**/*.cs"
dotnet stryker --config-file stryker-config.json --mutate "Security/AuthorizationValidation/**/*.cs"
dotnet stryker --config-file stryker-config.json --mutate "Models/ClaimSets/**/*.cs"
```

When a known test fixture should kill a mutant, narrow the test run with a VSTest filter:

```bash
dotnet stryker --config-file stryker-config.json --test-case-filter "FullyQualifiedName~NamespaceAuthorization"
dotnet stryker --config-file stryker-config.json --test-case-filter "FullyQualifiedName~Profile"
dotnet stryker --config-file stryker-config.json --test-case-filter "FullyQualifiedName~ClaimSet"
```

Use focused runs while editing tests, then re-run the broader target before considering that area improved.

## Manual Improvement Workflow

For each selected target:

1. Run the target's broad Stryker config once.
2. Open the HTML report and sort survived mutants by production file.
3. Ignore mutation score for now.
4. Pick a small cluster of survived mutants in one production area.
5. Decide whether each mutant represents:
   - A real missing assertion.
   - Missing edge-case input.
   - Overly broad test setup that never observes the changed behavior.
   - An equivalent mutant where the changed code is semantically identical.
   - Dead or redundant production code.
6. Improve tests using normal repo style: NUnit `Given_` fixtures, `Setup` for arrange and act, `It_` tests for assertions, FluentAssertions for result checks. Only use FakeItEasy very sparingly where a mock is the only possible way to test. In general, mocks are discouraged.
7. Prefer externally visible assertions over implementation assertions. For SQL and DDL code, assert generated shape, parameter binding, ordering, quoting, and dialect differences. For authorization and validation, assert exact allow/deny/failure behavior and problem detail content.
8. Re-run the focused Stryker command for the edited area.
9. Re-run the broad target command when the local cluster is clean.

Do not add CI gates, repository-wide score targets, or integration-test mutation runs during this phase.

## Recommended Starting Order

1. `EdFi.DataManagementService.Backend.Plans`
2. `EdFi.DataManagementService.Backend.RelationalModel`
3. `EdFi.DataManagementService.Backend.Ddl`
4. `EdFi.DataManagementService.Core`
5. `EdFi.DmsConfigurationService.Backend`

This order favors deterministic, unit-testable business logic first. It should produce actionable survived mutants before spending time on slower or more environment-dependent test suites.

## Done Criteria For This Phase

This phase is complete when:

- `dotnet-stryker` is pinned in the root, DMS, and CMS local tool manifests.
- The five initial unit test projects have committed `stryker-config.json` files.
- Each target can complete at least one manual Stryker run locally.
- The team has used at least one report from each target to add or strengthen focused unit tests.
- No mutation score gate, integration-test mutation run, E2E mutation run, or CI requirement has been introduced yet.
