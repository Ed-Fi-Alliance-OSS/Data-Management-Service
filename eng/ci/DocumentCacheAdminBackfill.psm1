# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# One-time support for .github/workflows/backfill-document-cache-admin.yml, which backfills
# EdFi.Api.DocumentCacheAdmin for releases cut before the DocumentCacheAdmin release chain existed.
# The workflow builds the dispatch commit, as Pkg EdFi.Api.SecurityMetadataTools.yml does, so the
# SBOM and the provenance describe the commit actually built. The version comes from the named stable
# release tag, and Assert-DocumentCacheAdminBackfillSource refuses the run unless the dispatch
# commit's build inputs equal the tag's, the tool README excepted so the package carries the current
# install guidance. Main's build inputs no longer equal v8.1.0's, so the workflow publishes only from
# the backfill/document-cache-admin-8.1.0 branch: main plus one commit, made with
# Restore-DocumentCacheAdminBackfillSource, that restores the tag's build inputs.
#
# Once 8.1.0 is backfilled, delete .github/workflows/backfill-document-cache-admin.yml, this module,
# eng/ci/tests/DocumentCacheAdminBackfill.Tests.ps1, the 'backfill-document-cache-admin.yml publishing
# credentials' Describe in eng/ci/tests/ReleasePublishingCredentials.Tests.ps1, the
# DocumentCacheAdminBackfill.Tests.ps1 entry in the Pester allowlist in
# .github/workflows/on-dms-pullrequest.yml, and from docs/CI-PUBLISHING-CREDENTIALS.md both the
# backfill lines and the 'The one-time DocumentCacheAdmin backfill' section. Keep
# eng/ci/release-assets.psm1, which the release workflows attach with too. The provenance names the
# backfill branch's commit, so replace that branch with the document-cache-admin-backfill-8.1.0 tag on
# the same commit rather than only deleting it.

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module (Join-Path $repoRoot 'eng/verification/ContractPackageComparison.psm1')
Import-Module (Join-Path $repoRoot 'eng/verification/ContractFeed.psm1')
Import-Module (Join-Path $repoRoot 'package-helpers.psm1')
Import-Module (Join-Path $PSScriptRoot 'release-classification.psm1')

$script:packageName = 'EdFi.Api.DocumentCacheAdmin'

<#
.SYNOPSIS
Throws unless the tag is a stable vMAJOR.MINOR.PATCH tag.
#>
function Assert-StableReleaseTag {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $ReleaseTag
    )

    Assert-ReleaseRef -ReleaseRef $ReleaseTag -Prefix v
    if ($ReleaseTag -cnotmatch '\Av[0-9]+\.[0-9]+\.[0-9]+\z') {
        # Assert-ReleaseRef admitted only printable ASCII, so the value is safe to log.
        throw "Release tag '$ReleaseTag' is not a stable vMAJOR.MINOR.PATCH tag. Only a stable release can be backfilled."
    }
}

<#
.SYNOPSIS
Returns the GitHub release for a tag of this repository, or $null when there is none.
#>
function Get-GitHubReleaseByTag {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Tag
    )

    $headers = @{
        Accept = 'application/vnd.github+json'
        Authorization = "Bearer $env:GITHUB_TOKEN"
    }
    try {
        return Invoke-RestMethod -Uri "https://api.github.com/repos/$env:GITHUB_REPOSITORY/releases/tags/$Tag" -Headers $headers
    }
    catch {
        if ($_.Exception.Response.StatusCode -eq 404) { return $null }
        throw
    }
}

<#
.SYNOPSIS
Refuses a backfill tag that is not a stable vMAJOR.MINOR.PATCH tag with a published GitHub release.
#>
function Assert-DocumentCacheAdminBackfillTag {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $ReleaseTag,

        # Returns the GitHub release for a tag, or $null when there is none.
        [scriptblock] $GetRelease = { param($Tag) Get-GitHubReleaseByTag -Tag $Tag }
    )

    Assert-StableReleaseTag -ReleaseTag $ReleaseTag

    $release = & $GetRelease $ReleaseTag
    if ($null -eq $release) {
        throw "Release tag '$ReleaseTag' has no GitHub release."
    }
    if ($release.draft -ne $false) {
        throw "The GitHub release for '$ReleaseTag' is a draft. Only a published release can be backfilled."
    }
    if ($release.prerelease -ne $false) {
        throw "The GitHub release for '$ReleaseTag' is a prerelease. Only a stable release can be backfilled."
    }
}

<#
.SYNOPSIS
Returns the package version a stable release tag names: the tag without its v.
.DESCRIPTION
The backfill stamps this version on what it builds. It never reads MinVer, which versions the
dispatch commit by its height above the tag, and never takes a version from an input.
#>
function Get-DocumentCacheAdminBackfillVersion {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $ReleaseTag
    )

    Assert-StableReleaseTag -ReleaseTag $ReleaseTag
    return $ReleaseTag.Substring(1)
}

<#
.SYNOPSIS
The git pathspecs of the inputs that decide what the backfill build produces.
.DESCRIPTION
Everything the build job's build-dms.ps1 BuildAndPublish and Package commands load or compile, so a
dispatch commit whose inputs equal a release tag's builds the same source as that tag. The build is
not byte-identical to one of the tag: the assemblies carry the dispatch commit's SHA, and the SDK
patch version can differ. The tool README is the one exception: it is packed into the tool, and
shipping the current install guidance is intended.
#>
function Get-DocumentCacheAdminBackfillSourceInput {
    [CmdletBinding()]
    [OutputType([string])]
    param()

    # Every project, props, targets, lock and source file the solution build and the pack compile.
    'src'
    # The build script, the modules it imports at startup, and the modules those import in turn.
    'build-dms.ps1'
    'eng/build-helpers.psm1'
    'eng/docker-compose/effective-schema-hash.psm1'
    'package-helpers.psm1'
    'eng/verification/ContractPackageComparison.psm1'
    'eng/docker-compose/dms-schema-environment.psm1'
    'eng/docker-compose/database-safety.psm1'
    'eng/schema-package-utility.psm1'
    # Repository-wide settings: local tools, analyzer settings, and the line endings git checks
    # every other input out with.
    '.config'
    '.editorconfig'
    '.gitattributes'
    # Read by the SDK and by MSBuild at the repository root when present. Neither exists today.
    'global.json'
    'Directory.Build.rsp'
    # Packed into the tool, and allowed to differ so the package carries the current install guidance.
    ':(exclude)src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md'
}

<#
.SYNOPSIS
Returns the root of the git work tree holding the current directory and the commit a release tag names.
#>
function Resolve-BackfillReleaseTag {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ReleaseTag,

        [Parameter(Mandatory)]
        [string] $Purpose
    )

    # The pathspecs are relative to the repository root, so git runs there wherever this is called.
    $top = & git rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($top)) {
        throw "Cannot $Purpose '$ReleaseTag': the current directory is not in a git work tree."
    }

    $tagCommit = & git -C $top rev-parse --verify --quiet "refs/tags/$ReleaseTag^{commit}"
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($tagCommit)) {
        throw "Release tag '$ReleaseTag' does not resolve to a commit in this checkout. The tag must exist, and the checkout must fetch tags."
    }
    return [pscustomobject]@{ Top = $top; TagCommit = $tagCommit }
}

<#
.SYNOPSIS
Returns each untracked or ignored work tree path that restoring paths HEAD does not track would replace.
.DESCRIPTION
git restore replaces whatever the work tree holds at a path it writes, so this returns any untracked
or ignored file at or under one of the paths, and any file or symbolic link where one of their folders
goes. A file in that place that is one of the changed inputs is left out: HEAD tracks it, and the
restore removes it.
#>
function Get-BackfillRestoreObstruction {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string] $Top,

        [Parameter(Mandatory)]
        [string[]] $Path,

        [Parameter(Mandatory)]
        [string[]] $Changed
    )

    $obstructions = [Collections.Generic.List[string]]::new()
    # With no exclude option, ls-files --others lists ignored files as well as untracked ones.
    $listed = (& git -C $Top --literal-pathspecs ls-files --others -z -- @Path) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "git ls-files could not list the untracked files at the paths to restore: exit $LASTEXITCODE."
    }
    $obstructions.AddRange([string[]] @($listed -split "`0" | Where-Object { $_ }))

    $removed = [Collections.Generic.HashSet[string]]::new($Changed, [StringComparer]::Ordinal)
    foreach ($restored in $Path) {
        $folder = ''
        foreach ($segment in @($restored -split '/' | Select-Object -SkipLast 1)) {
            $folder = if ($folder) { "$folder/$segment" } else { $segment }
            $item = Get-Item -LiteralPath (Join-Path $Top $folder) -Force -ErrorAction Ignore
            if ($null -eq $item -or $removed.Contains($folder)) { break }
            if (-not $item.PSIsContainer -or $item.LinkType) {
                $obstructions.Add($folder)
                break
            }
        }
    }
    $obstructions | Sort-Object -Unique
}

<#
.SYNOPSIS
Refuses a dispatch commit whose build inputs differ from those of the release tag.
.DESCRIPTION
Compares HEAD of the repository holding the current directory with the commit the tag names, over
Get-DocumentCacheAdminBackfillSourceInput. Equal inputs pass. Different inputs throw, naming the tag
and listing every differing input path. A tag that does not resolve, or a comparison git cannot make,
throws too.
#>
function Assert-DocumentCacheAdminBackfillSource {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $ReleaseTag
    )

    Assert-StableReleaseTag -ReleaseTag $ReleaseTag
    $tag = Resolve-BackfillReleaseTag -ReleaseTag $ReleaseTag -Purpose 'compare build inputs with'
    $top = $tag.Top
    $tagCommit = $tag.TagCommit

    $inputs = Get-DocumentCacheAdminBackfillSourceInput
    & git -C $top diff --quiet $tagCommit HEAD -- @inputs
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        Write-Information "The build inputs of this commit equal those of $ReleaseTag ($tagCommit)."
        return
    }
    if ($exitCode -ne 1) {
        throw "git diff could not compare the build inputs with release tag '$ReleaseTag' ($tagCommit): exit $exitCode."
    }

    $changed = @(& git -C $top diff --name-only $tagCommit HEAD -- @inputs)
    if ($LASTEXITCODE -ne 0) {
        throw "git diff could not list the build inputs that differ from release tag '$ReleaseTag' ($tagCommit): exit $LASTEXITCODE."
    }
    throw "The build inputs of this commit differ from those of release tag '$ReleaseTag' ($tagCommit), so it would not build that release. Differing inputs: $($changed -join ', '). Restore-DocumentCacheAdminBackfillSource stages the tag's inputs for a commit that would."
}

<#
.SYNOPSIS
Stages the build inputs of a release tag, so a commit of them passes Assert-DocumentCacheAdminBackfillSource.
.DESCRIPTION
In the repository holding the current directory, restores in the index and the work tree every
Get-DocumentCacheAdminBackfillSourceInput path that differs between HEAD and the commit the tag
names: a path the tag has gets the tag's content, and a path only HEAD has is removed. Everything
else stays as HEAD has it, the workflows and the tool README included. A work tree with uncommitted
changes to tracked files, or with an untracked or ignored file the restore would replace, is refused
before anything is restored, and afterwards the index is compared with the tag over the inputs
Assert-DocumentCacheAdminBackfillSource compares. Nothing is committed.
#>
function Restore-DocumentCacheAdminBackfillSource {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $ReleaseTag
    )

    Assert-StableReleaseTag -ReleaseTag $ReleaseTag
    $tag = Resolve-BackfillReleaseTag -ReleaseTag $ReleaseTag -Purpose 'restore the build inputs of'
    $top = $tag.Top
    $tagCommit = $tag.TagCommit

    $uncommitted = @(& git -C $top status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -ne 0) {
        throw "git status could not read the work tree: exit $LASTEXITCODE."
    }
    if ($uncommitted.Count -gt 0) {
        throw "The work tree has uncommitted changes to tracked files, so the build inputs of '$ReleaseTag' were not restored. Restore them in a clean checkout."
    }

    # Concrete paths only: git restore refuses a pathspec that matches nothing, as the inputs that
    # exist in neither commit do, where git diff accepts it.
    $inputs = Get-DocumentCacheAdminBackfillSourceInput
    $listed = (& git -C $top diff --name-only --no-renames -z $tagCommit HEAD -- @inputs) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "git diff could not list the build inputs that differ from release tag '$ReleaseTag' ($tagCommit): exit $LASTEXITCODE."
    }
    $changed = @($listed -split "`0" | Where-Object { $_ })
    if ($changed.Count -eq 0) {
        Write-Information "The build inputs already equal those of $ReleaseTag ($tagCommit); nothing was restored."
        return
    }

    $listed = (& git -C $top diff --name-only --no-renames --diff-filter=D -z $tagCommit HEAD -- @inputs) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "git diff could not list the build inputs that only release tag '$ReleaseTag' ($tagCommit) has: exit $LASTEXITCODE."
    }
    $tagOnly = @($listed -split "`0" | Where-Object { $_ })
    if ($tagOnly.Count -gt 0) {
        $obstructions = @(Get-BackfillRestoreObstruction -Top $top -Path $tagOnly -Changed $changed)
        if ($obstructions.Count -gt 0) {
            throw "The work tree has untracked or ignored files where the build inputs of '$ReleaseTag' would be restored, so nothing was restored: $($obstructions -join ', '). Move them, or restore in a fresh checkout."
        }
    }

    if (-not $PSCmdlet.ShouldProcess("$($changed.Count) build input path(s)", "Restore from $ReleaseTag")) {
        return
    }

    $pathList = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllText($pathList, ($changed -join "`0"))
        & git -C $top --literal-pathspecs restore --source $tagCommit --staged --worktree --pathspec-from-file $pathList --pathspec-file-nul
        if ($LASTEXITCODE -ne 0) {
            throw "git restore could not restore the build inputs of release tag '$ReleaseTag' ($tagCommit): exit $LASTEXITCODE."
        }
    }
    finally {
        Remove-Item -LiteralPath $pathList -Force
    }

    & git -C $top diff --cached --quiet $tagCommit -- @inputs
    if ($LASTEXITCODE -ne 0) {
        throw "After the restore, the staged build inputs still differ from those of release tag '$ReleaseTag' ($tagCommit): git diff exit $LASTEXITCODE."
    }
    Write-Information "Staged the build inputs of $ReleaseTag ($tagCommit) over $($changed.Count) path(s). Commit them; the commit passes Assert-DocumentCacheAdminBackfillSource."
}

<#
.SYNOPSIS
Returns the SHA256 of the copy of a package version on the feed's Local view.
.DESCRIPTION
The feed is public, so the copy is read anonymously from the Local view's flat container, through the
feed seams the contract publish check uses. A copy that cannot be read throws.
#>
function Get-PublishedPackageSha256 {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string] $ServiceIndexUrl,

        [Parameter(Mandatory)]
        [string] $PackageName,

        [Parameter(Mandatory)]
        [string] $Version
    )

    $viewIndexUrl = Get-ViewScopedServiceIndexUrl -ServiceIndexUrl $ServiceIndexUrl -ViewName Local
    $baseAddress = & (Get-DefaultResolvePackageBaseAddress) $viewIndexUrl ''
    [uri] $baseUri = $null
    if (-not [uri]::TryCreate($baseAddress, [UriKind]::Absolute, [ref] $baseUri) -or $baseUri.Scheme -notin 'http', 'https') {
        throw "The Local view's service index resolved to '$baseAddress', which is not an absolute http or https address."
    }

    $download = Join-Path ([IO.Path]::GetTempPath()) "$([guid]::NewGuid()).nupkg"
    try {
        & (Get-DefaultSavePublishedPackage) $baseAddress.TrimEnd('/') (ConvertTo-NormalizedPackageId -PackageId $PackageName) `
            (ConvertTo-NormalizedPackageVersion -Version $Version) $download ''
        return (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    finally {
        if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
    }
}

<#
.SYNOPSIS
Publishes the exact, verified DocumentCacheAdmin artifact from a completed backfill build.
.DESCRIPTION
No skip-duplicate option is used. A version already in the Local view is downloaded and compared
with this build. The same bytes mean an earlier attempt of this run pushed this artifact before it
failed, so nothing is pushed and the run continues to promotion and the release assets. Different
bytes are refused, and a new dispatch always has different bytes, because its assemblies carry the
dispatch commit's SHA and the package archive is not reproducible.
#>
function Publish-DocumentCacheAdminBackfill {
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
    param(
        [Parameter(Mandatory)]
        [string] $Version,

        [Parameter(Mandatory)]
        [string] $PackageFile,

        [Parameter(Mandatory)]
        [ValidatePattern('^[a-fA-F0-9]{64}$')]
        [string] $Sha256,

        [Parameter(Mandatory)]
        [string] $ServiceIndexUrl,

        [Parameter(Mandatory)]
        [string] $ApiKey
    )

    $packageName = $script:packageName
    if ((Get-FileHash -LiteralPath $PackageFile -Algorithm SHA256).Hash -ine $Sha256) {
        throw "DocumentCacheAdmin package hash differs from the build artifact: $PackageFile"
    }

    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackageFile).Path)
    try {
        $nuspec = $archive.GetEntry("$packageName.nuspec")
        if ($null -eq $nuspec) { throw "Missing $packageName.nuspec in $PackageFile" }
        $reader = [IO.StreamReader]::new($nuspec.Open())
        try { $metadata = ([xml] $reader.ReadToEnd()).package.metadata }
        finally { $reader.Dispose() }
        if ($metadata.id -cne $packageName -or
            (ConvertTo-NormalizedPackageVersion -Version $metadata.version) -cne (ConvertTo-NormalizedPackageVersion -Version $Version)) {
            throw "DocumentCacheAdmin package identity does not match $packageName $Version."
        }
    }
    finally { $archive.Dispose() }

    if (Test-PackageInView -ServiceIndexUrl $ServiceIndexUrl -ViewName Local `
        -PackageName $packageName -Version $Version -ApiKey $ApiKey) {
        $publishedSha256 = Get-PublishedPackageSha256 -ServiceIndexUrl $ServiceIndexUrl -PackageName $packageName -Version $Version
        if ($publishedSha256 -ine $Sha256) {
            throw "$packageName $Version is already published with different bytes (feed SHA256 $publishedSha256, this build $($Sha256.ToLowerInvariant())). The backfill never overwrites or re-pushes a published version."
        }
        Write-Information "$packageName $Version is already published with these exact bytes, so it is not pushed again."
        return
    }

    if ($PSCmdlet.ShouldProcess("$packageName $Version", "Publish to $ServiceIndexUrl")) {
        & dotnet nuget push $PackageFile --api-key $ApiKey --source $ServiceIndexUrl | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "DocumentCacheAdmin package push failed ($LASTEXITCODE)."
        }
    }
}

Export-ModuleMember -Function Assert-DocumentCacheAdminBackfillTag, Get-DocumentCacheAdminBackfillVersion, Get-DocumentCacheAdminBackfillSourceInput, Assert-DocumentCacheAdminBackfillSource, Restore-DocumentCacheAdminBackfillSource, Publish-DocumentCacheAdminBackfill
