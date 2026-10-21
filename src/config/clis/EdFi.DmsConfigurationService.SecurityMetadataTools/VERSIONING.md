# Security Metadata Tools versioning

This is the maintainer version policy for `EdFi.Api.SecurityMetadataTools`.
Keep the build and release workflows linked to this document as they are
implemented; update the implementation status below in the same changes.

## Version source and calculation

`SecurityMetadataToolsPatchVersion` in the
[tool project](EdFi.DmsConfigurationService.SecurityMetadataTools.csproj) is the
committed tool patch. It starts at `0`. For a new major/minor release line, reset
it deliberately to `0` in a commit. Increment it in a commit for each subsequent
stable tool publication on that line. Prerelease calculations never change or
consume this patch.

The shared `Get-SecurityMetadataToolsVersion` function in
[`package-helpers.psm1`](../../../../package-helpers.psm1) takes `-AppVersion`
from the existing `Get-VersionNumber` / MinVer release source. It uses that
version's major/minor, the project property's patch, and (by default) the source
prerelease and build labels. The existing release source uses MinVer and the
checkout's `v` tag history; `Get-VersionNumber -projectPrefix "cs"` exposes the
app version as the `dms-v` workflow output. Pass that version string to
`-AppVersion`, not the function's console output or a `cs-v` tag string.
`src/config/Directory.Build.props` is not a release version source for this
calculation, and the app's patch does not determine the tool's patch.

`-StableRelease` removes both labels, returning exactly `major.minor.patch`.
Independent dispatch dry runs and publications must use the same selected
checkout and stable calculation; the publish switch is not a version input.
`-ProjectPath` selects the project file in that checkout, not a patch override.
The declaration must be a single unconditional nonnegative integer, without
leading zeros, within the supported semantic-version integer range. Missing or
invalid declarations fail; the app patch is never a fallback.

From the repository root, these read-only examples use an illustrative app
version. With the current committed tool patch `0`, they return
`8.1.0-alpha.0.42` and `8.1.0`, respectively:

```powershell
Import-Module ./package-helpers.psm1
Get-SecurityMetadataToolsVersion -AppVersion '8.1.9-alpha.0.42'
Get-SecurityMetadataToolsVersion -AppVersion '8.1.9-alpha.0.42' -StableRelease
```

For release work, use the app version calculated from the selected checkout's
history. There is no separate tool version override. Calculation never edits the
project, allocates a patch, or queries the feed to choose a version.

| App release source | Committed tool patch | Normal calculation | Stable calculation |
| --- | --- | --- | --- |
| `8.1.0` | `0` | `8.1.0` | `8.1.0` |
| `8.1.0-alpha.0.42` | `1` | `8.1.1-alpha.0.42` | `8.1.1` |
| `8.1.9-beta.2` | `2` | `8.1.2-beta.2` | `8.1.2` |

## Stable publication sequence

A new major/minor line starts with a deliberately committed patch `0`. Every
subsequent stable tool publication on that line requires a committed increment,
whether it accompanies an app release or ships independently. Prerelease builds
retain the app source's suffix without consuming a stable patch. For example:

| Publication | App version source | Committed tool patch | Stable tool version |
| --- | --- | --- | --- |
| Initial normal app release | `8.1.0` | `0` | `8.1.0` |
| Independent useful tool correction | `8.1.0-alpha.0.42` | `1` | `8.1.1` |
| Later normal app release | `8.1.9` | `2` | `8.1.2` |

After independently publishing `8.1.1`, leaving patch `1` in a later normal
release would request the already-published tool version `8.1.1`. Prepare patch
`2` before that publication. The app and tool patches need not match. Reset the
tool patch to `0` only when intentionally beginning a new major/minor line; do
not reset it for another app release on the same line.

## Release preparation and recovery

The following procedure is the policy for the pending release automation; it
does not mean these publication paths are available yet.

1. Select the source ref containing the intended tool changes and committed
   patch. Verify its app major/minor through the existing MinVer release source
   with full version-source history. Use that same source commit for calculation,
   build, package verification, and publication. A moving default branch must not
   replace the selected release source.
2. Run the applicable unit, versioning, package installation/conversion, and
   upload checks. The planned tool dependency on the CMS validator bundles the
   independently versioned Secrets contract: preserve its declared assembly
   version. The normal lane must pass its contract check/publication gates; an
   independent tool release must verify a matching already-published Secrets
   contract under the existing contract comparison rules. Resolve a missing,
   incompatible, or unreadable dependency before publishing the tool.
3. Dry-run the independent workflow with `publish_package=false` on the selected
   ref, then publish from that same ref with `publish_package=true`. Both modes
   use `-StableRelease` and therefore the identical three-part version, even
   from an app prerelease source. The switch controls push/promotion only.
   Dry runs must validate the installed package and produce SBOM/provenance
   without pushing or promoting it.
4. A new publication attempt must refuse an exact tool version already on the
   feed. Do not overwrite it, use skip-duplicate behavior, or silently select a
   different version. Prepare and commit the next tool patch on the intended
   release ref, repeat validation and the dry run, then start a new publication
   attempt. No new stable publication is needed merely to repeat a dry run.
5. Promote the exact version retained by the successful package build/push,
   using that package's version and artifact identity rather than the app tag,
   a wildcard, or the current default branch. Record the source commit, exact
   version, package hash, workflow run, SBOM/provenance, and installation results.

If push succeeds but promotion fails, rerun **only the failed promotion job in
the same workflow run**. It must consume the original build's exact version.
Do not rebuild, push again, repeat the duplicate-publication check, or increment
the patch for this recovery. Promotion changes the view of an existing package;
it is not another publication. A new publication attempt for those same bytes
still encounters the duplicate-version refusal above.

## Implementation status and build modes

The patch property and `Get-SecurityMetadataToolsVersion` API are implemented.
The helper supports normal calculation (preserving prerelease/build labels) and
`-StableRelease` calculation (removing both labels). Neither mode publishes
anything. Run its existing verification from the repository root:

```powershell
Invoke-Pester -Path eng/ci/tests/SecurityMetadataToolsVersioning.Tests.ps1 -CI -Output Detailed
```

The remaining integration points are tracked by DMS-1617:

| Entry point | Current status and intended integration |
| --- | --- |
| [build-config.ps1](../../../../build-config.ps1) | Existing `Build -Configuration Release -DmsCSVersion <app-version> -LockedMode` builds the solution with the app version. `Package` currently packages the Configuration Service application. DMS-1617-10 will add `-PackageTarget SecurityMetadataTools` and rebuild/pack the tool at its calculated version; that option is not available yet. |
| [on-config-merge-or-tag.yml](../../../../.github/workflows/on-config-merge-or-tag.yml) | Existing `cs-pre` release entry point. DMS-1617-12 must retain the triggering source SHA in release creation; the current payload still targets `main`. |
| [on-prerelease.yml](../../../../.github/workflows/on-prerelease.yml) and [on-release.yml](../../../../.github/workflows/on-release.yml) | Existing app release workflows. DMS-1617-12 will add the tool's normal `cs-` package chain and exact-version promotion. No tool publication/promotion jobs are wired yet. |
| `.github/workflows/Pkg EdFi.Api.SecurityMetadataTools.yml` | Planned DMS-1617-13 dispatch-only workflow; the file does not exist yet. It will use the selected ref, `publish_package`, stable calculation, dependency verification, and a separately retryable promotion job as described above. |

Direct project builds/packs and the current solution build do not yet apply the
shared tool calculation. Their version output is development scaffolding, not
evidence of the intended release version. Update this status and the concrete
commands when each integration lands; keep the detailed policy here rather than
duplicating it in workflows.
