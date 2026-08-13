---
jira: TBD
jira_url: TBD
epic: DMS-1345
source_spike: DMS-1346
---

# Story: Add the Custom-Validator Abstractions Contract

## Description

DMS's four validator interfaces are all `internal` to `EdFi.DataManagementService.Core`, so nothing outside that assembly can implement or replace them without modifying core source. This story ships the public, versioned contract a district or vendor implements against, per:

- `reference/design/custom-validation-DMS-1345/design.md` ("### Validator Contract" defines the types; "### Resource Applicability" and "### Per-Invocation Inputs" define their semantics)

The contract is five public top-level types in `EdFi.DataManagementService.Core.External`: `ICustomResourceValidator`, `ValidationFailure` (with nested `OnPath` and `OnResource`), `ValidatedResource`, `CustomValidationOperation`, and `ValidationScope`.

Shipping the types is only half the work. `EdFi.DataManagementService.Core.External.csproj` declares `IsPackable=true`, but nothing packs or publishes that assembly today, so the contract is not consumable by an implementer until a pack-and-publish path exists. This story adds that path.

This story ships the contract only. It does not wire any pipeline step to consume it and does not implement any concrete validator.

## Acceptance Criteria

- `ICustomResourceValidator`, `ValidationFailure` (with `OnPath` and `OnResource`), `ValidatedResource`, `CustomValidationOperation`, and `ValidationScope` exist as public types in `EdFi.DataManagementService.Core.External`, with signatures matching design.md "### Validator Contract".
- The contract adds no new package references to `EdFi.DataManagementService.Core.External`. Every member type is either BCL (`JsonNode`, `Task`, `CancellationToken`, `IReadOnlyList<>`, `IReadOnlyDictionary<>`) or already public in that assembly (`ProjectName`, `ResourceName`, `ResourceInfo`, `TraceId`, `RouteQualifierName`, `RouteQualifierValue`), so the csproj diff carries no `PackageReference` change and no lock-file regeneration is required.
- No registration-hook interface is added: `grep -rn "ICustomValidatorRegistration" src/` returns no matches on the story's branch. An implementer registers through an ordinary `IServiceCollection` extension in their own assembly.
- No store-read facade is added: `grep -rn "ICustomValidationStoreReader" src/` returns no matches on the story's branch.
- `ValidationFailure` has a private parameterless base constructor and exactly two sealed, non-positional nested `record` cases, each with an explicit validating constructor and get-only properties.
- A unit test proves `OnPath` throws `ArgumentException` for a null, empty, or non-`$`-rooted path and for a null or empty message, and that `OnResource` throws for a null or empty message.
- A unit test proves that within the abstractions assembly's own public construction surface, `OnResource` is the only way to express a failure carrying no path. Asserted reflectively over `ValidationFailure`'s public and protected constructors, where the expected set is exactly one member, the compiler-synthesized copy constructor. The test names that constructor as expected rather than asserting an empty set, because C# forbids restricting it (CS8878), and carries a comment recording that cross-assembly derivation through it is closed at the consumption point by the fan-in step rather than here.
- A `build-dms.ps1` package target produces a nupkg whose package id is `EdFi.Api.Core.External`, proven by invoking the target locally and asserting on the id inside the produced nuspec rather than on the file's existence. The id follows the repository's existing `EdFi.Api*` convention and cannot be changed after first publication without breaking every consumer that compiled against it.
- The produced nupkg contains the implementer-guide markdown at the `PackagePath` the csproj declares, proven by extracting the package. A markdown file placed beside the csproj is not packed by default SDK behaviour, so a `PackageReadmeFile` plus a matching packed content item is required. A placeholder document satisfies this criterion if the implementer-guide story has not yet landed.
- `.github/workflows/on-prerelease.yml` packs and pushes the new package alongside the existing API and SchemaTools packages, and `.github/workflows/on-release.yml` carries a matching promote step. Both are required, since the release workflow only promotes what the prerelease workflow published.
- A scratch class library whose only reference is the produced `EdFi.Api.Core.External` package compiles a type implementing `ICustomResourceValidator`, proving the package is self-sufficient for its documented purpose.
- `dotnet build src/dms/EdFi.DataManagementService.sln` succeeds with `TreatWarningsAsErrors` unchanged on `EdFi.DataManagementService.Core.External.csproj`.
- None of the four existing internal validator interfaces are modified: `git diff --stat origin/main...HEAD -- src/dms/core/EdFi.DataManagementService.Core/Validation/` produces no output.

## Tasks

1. Add the five public types under `src/dms/core/EdFi.DataManagementService.Core.External/Validation/`, using namespace `EdFi.DataManagementService.Core.External.Validation`, mirroring the folder-to-namespace convention already used in that project.
2. Implement `ValidationFailure` exactly as design.md specifies: private parameterless base constructor, two sealed non-positional nested records, explicit validating constructors, get-only properties.
3. Reuse `ProjectName`, `ResourceName`, `ResourceInfo`, `TraceId`, `RouteQualifierName`, and `RouteQualifierValue` from the same assembly rather than declaring parallel types. Declare `ValidationScope.RouteQualifiers` as `IReadOnlyDictionary<,>`, and state in its XML documentation that the declared type is documentation rather than protection, the defensive copy being made by the fan-in step.
4. Add unit tests in `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit` covering constructor validation and the reflective single-path-less-constructor assertion.
5. Add a `Core.External` package target to `build-dms.ps1` following the `BuildSchemaToolsPackage` shape, with package id `EdFi.Api.Core.External`.
6. Add `PackageReadmeFile` and the matching packed content item to the csproj, pointing at the document the implementer-guide story owns.
7. Add pack and push jobs to `on-prerelease.yml` and a promote step to `on-release.yml`, following the existing API and SchemaTools jobs.
8. Build the scratch consumer library against the produced package to prove self-sufficiency, and record the result in the pull request.
