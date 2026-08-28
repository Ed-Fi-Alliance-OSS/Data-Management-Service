---
jira: TBD
jira_url: TBD
epic: TBD
source_spike: DMS-1462
---

# Story: Publish `EdFi.Api.Plugins` and `EdFi.Api.CustomValidation`

## Description

Every story before this one proves the packaged path from a local folder feed and publishes nothing.
An external implementer can compile against neither contract until both are on the Ed-Fi feed, so this story publishes them, per:

- `reference/design/plugins-DMS-1462/design.md` ("## Divergence from the Custom Validation Epic", "Publishing is the epic's last step, not its first")
- `reference/design/custom-validation-DMS-1345/design.md` ("### Publishing deferral")
- `reference/design/custom-validation-DMS-1345/01-add-custom-validator-abstractions-contract.md` (DMS-1432), whose suspended prerelease and release criteria are the starting point here

Publishing burns two package ids permanently, so this story is the release-gated leaf: blocked by every other foundation story and by DMS-1433, DMS-1435, and DMS-1436, and blocking none of them.
It is filed with the others so the dependency graph is complete, and worked last.

**Both packages carry their own semantic version, and today only one of them does.**
`EdFi.Api.Plugins` is packed at the version `src/plugins/Directory.Build.props` declares, per draft 01.
`EdFi.Api.CustomValidation` is packed at `-p:PackageVersion=$DMSVersion` (`build-dms.ps1:1832`) and inherits its `AssemblyVersion` from the release-stamped `src/dms/Directory.Build.props`, and this story changes that, because the loader's newer-plugin-on-older-host preflight compares `AssemblyVersion`s across every contract package: under the release-stamped scheme a validator built against the 8.4 contract would be refused by an 8.3 host whose contract surface is identical, naming two versions that differ in nothing an implementer can act on.
The project stays where it is; it declares `Version`, `AssemblyVersion`, and `FileVersion` in its own csproj, which override the imported `Directory.Build.props`, and the pack target uses the project's version.
That is an amendment to a merged story's project without touching any of its types, and design.md's divergence ledger records it as such.

## Acceptance Criteria

- `.github/workflows/on-prerelease.yml` carries the full per-package artifact pipeline for both packages, matching what `EdFi.Api`, `EdFi.Api.SchemaTools`, and `EdFi.Api.ConfigurationService` already have: a pack job exposing `hash-code`, an `sbom-create` job producing an SPDX 2.2 manifest through `Microsoft.Sbom.DotNetTool` and exposing `sbom-hash-code`, a `provenance-create` job calling `Ed-Fi-Alliance-OSS/slsa-github-generator/.github/workflows/generator_generic_slsa3.yml@v2.0.0` with a `provenance-name` unique to each package, a publish job, and SBOM-plus-provenance upload inside `attach-dms-artifacts-to-release`.
- `.github/workflows/on-release.yml` carries a `Promote ... Package` step for each in `promote-azure-artifact`.
- `EdFi.Api.CustomValidation` declares `Version`, `AssemblyVersion`, and `FileVersion` of `1.0.0` in `src/dms/core/EdFi.DataManagementService.CustomValidation/EdFi.DataManagementService.CustomValidation.csproj`, matching the surface DMS-1432 shipped, and `build-dms.ps1`'s `CustomValidation` pack target packs the project's own version instead of `-p:PackageVersion=$DMSVersion` (`build-dms.ps1:1832`). A test or build-lane step runs the build with an explicit `-DMSVersion` and asserts the packed `EdFi.Api.CustomValidation` version and the contained assembly's `AssemblyVersion` are both `1.0.0` regardless of it, which is the assertion that proves `SetDMSAssemblyInfo` no longer reaches the contract.
- Each publish job publishes the version its own project declares, not `$DMSVersion`, and refuses to run if a package of that version is already on the feed, so an unbumped contract cannot be republished with different content.
- Each pack job asserts the packed assembly's `AssemblyVersion` equals the package version before publishing, reusing draft 01's assertion script for `EdFi.Api.Plugins` and extending `eng/verification/Assert-CustomValidationPackage.ps1` with the same assertion for `EdFi.Api.CustomValidation`.
- A dry run of the prerelease workflow on a branch, with publish steps replaced by `--dry-run` or an `if: false` guard, produces both nupkgs, both SBOMs, and both provenance artifacts, recorded in the pull request.
- After the first real prerelease, a scratch project outside the repository restores both packages from the Ed-Fi feed by id and version and compiles a validator plugin against them, recorded on the ticket.
- `docs/CONFIGURATION.md`, `PLUGINS.md`, and `CUSTOM-VALIDATION.md` name the published ids and the feed, replacing any "built but not yet published" language.

## Tasks

1. Move `EdFi.Api.CustomValidation` off the release-stamped version: declare its version in its own csproj and change the `CustomValidation` pack target to use it.
2. Lift DMS-1432's suspended prerelease and release criteria into concrete jobs for `EdFi.Api.CustomValidation`.
3. Add the parallel jobs for `EdFi.Api.Plugins` with its own version source and the already-published refusal.
4. Add the `AssemblyVersion` gate ahead of publish for both packages, and the `SetDMSAssemblyInfo` non-interference assertion for `EdFi.Api.CustomValidation`.
5. Run the dry run and record it.
6. After the prerelease, run the external restore proof and record it.
7. Update the three documents' publishing language.
