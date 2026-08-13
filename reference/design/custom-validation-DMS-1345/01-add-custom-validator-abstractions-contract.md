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

The contract is five public top-level types in `EdFi.DataManagementService.Core.External`: `ICustomResourceValidator`, `CustomValidationFailure` (with nested `OnPath` and `OnResource`), `ValidatedResource`, `CustomValidationOperation`, and `ValidationScope`.

Shipping the types is only half the work. `EdFi.DataManagementService.Core.External.csproj` declares `IsPackable=true`, but nothing packs or publishes that assembly today, so the contract is not consumable by an implementer until a pack-and-publish path exists. This story adds that path, and removes the assembly's unused `Microsoft.CodeAnalysis` package reference first, so the first published version does not commit implementers to the Roslyn compiler platform as a dependency of a two-member interface.

This story ships the contract only. It does not wire any pipeline step to consume it and does not implement any concrete validator.

## Acceptance Criteria

- `ICustomResourceValidator`, `CustomValidationFailure` (with `OnPath` and `OnResource`), `ValidatedResource`, `CustomValidationOperation`, and `ValidationScope` exist as public types in `EdFi.DataManagementService.Core.External`, with signatures matching design.md "### Validator Contract".
- The contract's own member types require no new package references. Every one is either BCL (`JsonNode`, `Task`, `CancellationToken`, `IReadOnlyList<>`, `IReadOnlyDictionary<>`) or already public in that assembly (`ProjectName`, `ResourceName`, `ResourceInfo`, `TraceId`, `RouteQualifierName`, `RouteQualifierValue`).
- The unused `Microsoft.CodeAnalysis` package reference is removed from `EdFi.DataManagementService.Core.External.csproj` and the lock file is regenerated. No file in the project references a Roslyn API, and that one reference is what pulls the Roslyn packages, the six `System.Composition.*` packages, and `Humanizer.Core` into every consumer's dependency closure - 16 of the 26 packages the assembly currently resolves to.
- `Microsoft.Extensions.Options` and `Microsoft.Extensions.DependencyInjection.Abstractions` are added as explicit `PackageReference`s. Both already arrive transitively through `Microsoft.Extensions.Logging`, so this changes no resolved version; it makes the registration extension's dependencies declared rather than inherited from a reference the project does not otherwise use. Note for whoever picks this up: of the two logging packages, only `Microsoft.Extensions.Logging.Abstractions` is used, and only for `EventId`.
- The produced nupkg's declared dependencies are exactly `Be.Vlaanderen.Basisregisters.Generators.Guid.Deterministic`, `Sandwych.QuickGraph.Core`, `Microsoft.Extensions.Logging`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options`, and `Microsoft.Extensions.DependencyInjection.Abstractions`, and the public types it exports from the validation namespace are exactly `ICustomResourceValidator`, `CustomValidationFailure` (with `OnPath` and `OnResource`), `ValidatedResource`, `CustomValidationOperation`, and `ValidationScope`. Both are asserted against the extracted package. The type-surface half is what fails if a registration-hook interface or a store-read facade is ever added, which a `grep` for a name that exists nowhere on any branch cannot do. An implementer registers through an ordinary `IServiceCollection` extension in their own assembly, and this version's contract offers no store access.
- `CustomValidationFailure` has a private parameterless base constructor and exactly two sealed, non-positional nested `record` cases, each with an explicit validating constructor and get-only properties.
- A unit test proves `OnPath` throws `ArgumentException` for a null, empty, bare `"$"`, or otherwise non-`$.`-prefixed path and for a null or empty message, and that `OnResource` throws for a null or empty message. Bare `"$"` is asserted explicitly because it is `$`-rooted yet still path-less, which is `OnResource`'s case.
- A unit test proves that within the abstractions assembly's own public construction surface, `OnResource` is the only way to express a failure carrying no path. Asserted reflectively over `CustomValidationFailure`'s public and protected constructors, where the expected set is exactly one member, the compiler-synthesized copy constructor. The test names that constructor as expected rather than asserting an empty set, because C# forbids restricting it (CS8878), and carries a comment recording that cross-assembly derivation through it is closed at the consumption point by the fan-in step rather than here.
- A `build-dms.ps1` package target produces a nupkg whose package id is `EdFi.Api.Core.External`, proven by invoking the target locally and asserting on the id inside the produced nuspec rather than on the file's existence. The id follows the repository's existing `EdFi.Api*` convention and cannot be changed after first publication without breaking every consumer that compiled against it.
- The produced nupkg contains the implementer-guide markdown at the `PackagePath` the csproj declares, proven by extracting the package. A markdown file placed beside the csproj is not packed by default SDK behaviour, so a `PackageReadmeFile` plus a matching packed content item is required. A placeholder document satisfies this criterion if the implementer-guide story has not yet landed.
- `.github/workflows/on-prerelease.yml` packs and pushes the new package alongside the existing API and SchemaTools packages, and `.github/workflows/on-release.yml` carries a matching promote step. Both are required, since the release workflow only promotes what the prerelease workflow published.
- A scratch class library whose only reference is the produced `EdFi.Api.Core.External` package compiles both a type implementing `ICustomResourceValidator` and the registration extension exactly as design.md "### Registration and Composition" documents it, including the `Action<TOptions>` overload of `Configure` and the `TryAddEnumerable` call. Implementing the interface alone would exercise no dependency beyond BCL types and would not prove the package can compile the documented registration.
- `dotnet build src/dms/EdFi.DataManagementService.sln` succeeds with `TreatWarningsAsErrors` unchanged on `EdFi.DataManagementService.Core.External.csproj`.

## Tasks

1. Add the five public types under `src/dms/core/EdFi.DataManagementService.Core.External/Validation/`, using namespace `EdFi.DataManagementService.Core.External.Validation`, mirroring the folder-to-namespace convention already used in that project.
2. Implement `CustomValidationFailure` exactly as design.md specifies: private parameterless base constructor, two sealed non-positional nested records, explicit validating constructors, get-only properties.
3. Reuse `ProjectName`, `ResourceName`, `ResourceInfo`, `TraceId`, `RouteQualifierName`, and `RouteQualifierValue` from the same assembly rather than declaring parallel types. Declare `ValidationScope.RouteQualifiers` as `IReadOnlyDictionary<,>`, and state in its XML documentation that the declared type is documentation rather than protection, the defensive copy being made by the fan-in step.
4. Add unit tests in `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit` covering constructor validation and the reflective single-path-less-constructor assertion.
5. Adjust `EdFi.DataManagementService.Core.External.csproj`'s package references and regenerate the lock file, confirming the build still succeeds: remove the unused `Microsoft.CodeAnalysis`, and add `Microsoft.Extensions.Options` and `Microsoft.Extensions.DependencyInjection.Abstractions` explicitly. Do this before adding the package target, so the first packed output already carries the intended dependency set.
6. Add a `Core.External` package target to `build-dms.ps1` following the `BuildSchemaToolsPackage` shape, with package id `EdFi.Api.Core.External`.
7. Add `PackageReadmeFile` and the matching packed content item to the csproj, pointing at the document the implementer-guide story owns.
8. Add pack and push jobs to `on-prerelease.yml` and a promote step to `on-release.yml`, following the existing API and SchemaTools jobs.
9. Build the scratch consumer library against the produced package, compiling both the validator and the documented registration extension, and record the result in the pull request.
