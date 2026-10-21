# Security Metadata Tools version calculation

`SecurityMetadataToolsPatchVersion` in this directory's project file is the
committed tool patch. It starts at `0`. For a new major/minor release line, reset
it deliberately to `0` in a commit. Increment it in a commit for each subsequent
stable tool publication on that line. Prerelease calculations never change or
consume this patch.

The shared `Get-SecurityMetadataToolsVersion` function in
[`package-helpers.psm1`](../../../../package-helpers.psm1) takes `-AppVersion`
from the existing `Get-VersionNumber` / MinVer release source. It uses that
version's major/minor, the project property's patch, and (by default) the source
prerelease and build labels. `src/config/Directory.Build.props` is not a release
version source for this calculation.

`-StableRelease` removes both labels, returning exactly `major.minor.patch`.
Independent dispatch dry runs and publications must use the same selected
checkout and stable calculation; the publish switch is not a version input.
`-ProjectPath` selects the project file in that checkout, not a patch override.
The declaration must be a single unconditional nonnegative integer, without
leading zeros, within the supported semantic-version integer range. Missing or
invalid declarations fail; the app patch is never a fallback.

| App release source | Committed tool patch | Normal calculation | Stable calculation |
| --- | --- | --- | --- |
| `8.1.0` | `0` | `8.1.0` | `8.1.0` |
| `8.1.0-alpha.0.42` | `1` | `8.1.1-alpha.0.42` | `8.1.1` |
| `8.1.9-beta.2` | `2` | `8.1.2-beta.2` | `8.1.2` |

This establishes the calculation API. Build/package wiring and release workflows
are subsequent DMS-1617 tasks; this helper alone does not change package builds
or publish packages.
