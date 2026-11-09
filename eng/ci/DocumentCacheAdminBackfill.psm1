# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# One-time support for the EdFi.Api.DocumentCacheAdmin release backfill. Deleted together with
# .github/workflows/backfill-document-cache-admin.yml once 8.1.0 has been backfilled.

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module (Join-Path $repoRoot 'eng/verification/ContractPackageComparison.psm1')
Import-Module (Join-Path $repoRoot 'package-helpers.psm1')
Import-Module (Join-Path $PSScriptRoot 'release-classification.psm1')

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
        [scriptblock] $GetRelease = {
            param($Tag)
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
    )

    Assert-ReleaseRef -ReleaseRef $ReleaseTag -Prefix v
    # Only printable ASCII reaches the log, so the refused value cannot start a workflow command.
    $shown = [regex]::Replace($ReleaseTag, '[^\x20-\x7E]', '?')
    if ($ReleaseTag -cnotmatch '\Av[0-9]+\.[0-9]+\.[0-9]+\z') {
        throw "Release tag '$shown' is not a stable vMAJOR.MINOR.PATCH tag. Only a stable release can be backfilled."
    }

    $release = & $GetRelease $ReleaseTag
    if ($null -eq $release) {
        throw "Release tag '$shown' has no GitHub release."
    }
    if ($release.draft -ne $false) {
        throw "The GitHub release for '$shown' is a draft. Only a published release can be backfilled."
    }
    if ($release.prerelease -ne $false) {
        throw "The GitHub release for '$shown' is a prerelease. Only a stable release can be backfilled."
    }
}

<#
.SYNOPSIS
Refuses a build version that is not exactly the release tag without its v.
#>
function Assert-DocumentCacheAdminBackfillVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ReleaseTag,

        [Parameter(Mandatory)]
        [string] $Version
    )

    if ($ReleaseTag.Length -eq 0 -or $Version -cne $ReleaseTag.Substring(1)) {
        throw "Version '$Version' is not the version of release tag '$ReleaseTag'."
    }
}

<#
.SYNOPSIS
Publishes the exact, verified DocumentCacheAdmin artifact from a completed backfill build.
No skip-duplicate option is used: a version already in the Local view is refused.
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

    $packageName = 'EdFi.Api.DocumentCacheAdmin'
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
        throw "$packageName $Version is already published. The backfill never overwrites or re-pushes a published version."
    }

    if ($PSCmdlet.ShouldProcess("$packageName $Version", "Publish to $ServiceIndexUrl")) {
        & dotnet nuget push $PackageFile --api-key $ApiKey --source $ServiceIndexUrl | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "DocumentCacheAdmin package push failed ($LASTEXITCODE)."
        }
    }
}

Export-ModuleMember -Function Assert-DocumentCacheAdminBackfillTag, Assert-DocumentCacheAdminBackfillVersion, Publish-DocumentCacheAdminBackfill
