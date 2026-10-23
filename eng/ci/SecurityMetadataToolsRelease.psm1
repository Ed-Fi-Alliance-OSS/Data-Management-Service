# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module (Join-Path $repoRoot 'package-helpers.psm1')

<#
.SYNOPSIS
Build and validate the stable tool; dry runs and publications execute exactly this path.
#>
function Invoke-SecurityMetadataToolsReleaseBuild {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $AppVersion)

    $version = Get-SecurityMetadataToolsVersion -AppVersion $AppVersion -StableRelease
    # Passing the stable source through the existing smoke/build helper preserves the
    # committed patch while removing app prerelease/build labels in both modes.
    pwsh -NoProfile -File (Join-Path $repoRoot 'eng/verification/Test-SecurityMetadataToolsPackage.ps1') -AppVersion $version | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'SecurityMetadataTools package validation failed.' }
    $packageFile = Join-Path $repoRoot "EdFi.Api.SecurityMetadataTools.$version.nupkg"
    return [pscustomobject]@{
        Version = $version
        PackageFile = $packageFile
        Sha256 = (Get-FileHash -LiteralPath $packageFile -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

<#
.SYNOPSIS
Require the published Secrets public surface, XML documentation and dependencies to match.
.DESCRIPTION
Prerequisite using the existing read-only verifier in both modes. Never publishes Secrets.
#>
function Assert-PublishedSecretsContract {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $ServiceIndexUrl,
        [string] $ApiKey = '',
        # Existing verifier's read-only feed seams, for controlled behavioral tests.
        [hashtable] $FeedAccess = @{}
    )

    $version = Get-SecretsContractVersion
    $scratch = Join-Path ([IO.Path]::GetTempPath()) "security-metadata-secrets-$([guid]::NewGuid())"
    try {
        $project = Join-Path $repoRoot 'src/config/contracts/EdFi.DmsConfigurationService.Secrets/EdFi.DmsConfigurationService.Secrets.csproj'
        dotnet pack $project --configuration Release --output $scratch -p:RestoreLockedMode=true | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Secrets contract packing failed.' }
        $decision = & (Join-Path $repoRoot 'eng/verification/Invoke-ContractPublishCheck.ps1') `
            -PackageFile (Join-Path $scratch "EdFi.Api.Secrets.$version.nupkg") `
            -PackageId 'EdFi.Api.Secrets' -PackageVersion $version `
            -WorkingDirectory (Join-Path $scratch 'check') `
            -ServiceIndexUrl $ServiceIndexUrl -FeedApiKey $ApiKey @FeedAccess
        if ($decision.ShouldPush -or $decision.Reason -cne 'unchanged') {
            throw "Secrets contract $version must already be published and match the selected ref before publishing SecurityMetadataTools. Publish it through its own release process first."
        }
    }
    finally {
        if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
    }
}

<#
.SYNOPSIS
Verify the published dependency, then publish the exact validated tool artifact.
#>
function Publish-SecurityMetadataToolsRelease {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Version,
        [Parameter(Mandatory)][string] $PackageFile,
        [Parameter(Mandatory)][string] $Sha256,
        [Parameter(Mandatory)][string] $ServiceIndexUrl,
        [Parameter(Mandatory)][string] $ApiKey,
        [hashtable] $FeedAccess = @{}
    )

    Assert-PublishedSecretsContract -ServiceIndexUrl $ServiceIndexUrl -ApiKey $ApiKey -FeedAccess $FeedAccess
    Publish-SecurityMetadataToolsPackage -Version $Version -PackageFile $PackageFile `
        -Sha256 $Sha256 -ServiceIndexUrl $ServiceIndexUrl -ApiKey $ApiKey
}

Export-ModuleMember -Function Invoke-SecurityMetadataToolsReleaseBuild, Assert-PublishedSecretsContract, Publish-SecurityMetadataToolsRelease
