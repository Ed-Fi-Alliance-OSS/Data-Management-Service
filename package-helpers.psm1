# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#requires -version 7

$ErrorActionPreference = "Stop"

<#
.DESCRIPTION
Builds a pre-release version number based on the last tag in the commit history
and the number of commits since then.
#>
function Get-VersionNumber {
    param (
        [string]
        $projectPrefix = "dms"
    )

    $prefix = "v"

    # Install the MinVer CLI tool
    &dotnet tool install --global minver-cli

    $version = $(&minver -t $prefix)

    "dms-v=$version" | Out-File -FilePath $env:GITHUB_OUTPUT -Append

    $dmsSemver = "$projectPrefix-v$($version)"
    "dms-semver=$dmsSemver" | Out-File -FilePath $env:GITHUB_OUTPUT -Append

    $assemblyVersion = Convert-ToAssemblyVersion $version
    "$projectPrefix-assembly-version=$assemblyVersion" | Out-File -FilePath $env:GITHUB_OUTPUT -Append

    Write-Output "dms-v is set to: $version"
    Write-Output "dms-semver is set to: $dmsSemver"
    Write-Output "$projectPrefix-assembly-version is set to: $assemblyVersion"
}

<#
.DESCRIPTION
Promotes a package in Azure Artifacts to a view, e.g. pre-release or release.
#>
function Invoke-Promote {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive')]
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
    param(
        # NuGet Packages API URL
        [Parameter(Mandatory = $true)]
        [String]
        $PackagesURL,

        # Azure Artifacts user name
        [Parameter(Mandatory = $true)]
        [String]
        $Username,

        # Azure Artifacts password
        [Parameter(Mandatory = $true)]
        [SecureString]
        $Password,

        # View to promote into
        [Parameter(Mandatory = $true)]
        [String]
        $ViewId,

        # Git ref (short) for the release tag ex: v1.3.5
        [Parameter(Mandatory = $true)]
        $ReleaseRef,

        # Name of the Package
        [Parameter(Mandatory = $true)]
        [String]
        $PackageName
    )

    $version = $ReleaseRef -replace "v", ""

    $body = @{
        data      = @{
            viewId = $ViewId
        }
        operation = 0
        packages  = @(
            @{
                id = $PackageName
                version = $version
            }
        )
    } | ConvertTo-Json

    $parameters = @{
        Method      = "POST"
        ContentType = "application/json"
        Credential  = New-Object -TypeName PSCredential -ArgumentList $Username, $Password
        URI         = "$PackagesURL/nuget/packagesBatch?api-version=5.0-preview.1"
        Body        = $body
    }

    Write-Output "Web request parameters:"
    $parameters | Out-Host

    if ($PSCmdlet.ShouldProcess($PackagesURL)) {
        $response = Invoke-WebRequest @parameters -UseBasicParsing
        $response | ConvertTo-Json -Depth 10 | Out-Host
    }
}

<#
.DESCRIPTION
Installs Credential Handler, used for authentication when uploading packages to an Azure Feed.
#>
function InstallCredentialHandler {
    # Does the same as: iex "& { $(irm https://aka.ms/install-artifacts-credprovider.ps1) }"
    # but this brings support for installing the provider on Linux.
    # Additionally, it's less likely to hit GitHub rate limits because this downloads it directly, instead of making a
    # request to https://api.github.com/repos/Microsoft/artifacts-credprovider/releases/latest to infer the latest version.

    $downloadPath = Join-Path ([IO.Path]::GetTempPath()) 'cred-provider.zip'

    $credProviderUrl = 'https://github.com/microsoft/artifacts-credprovider/releases/download/v1.4.1/Microsoft.Net6.NuGet.CredentialProvider.zip'
    Write-Host "Downloading artifacts-credprovider from $credProviderUrl ..."
    $webClient = New-Object System.Net.WebClient
    $webClient.DownloadFile($credProviderUrl, $downloadPath)

    Write-Host "Download complete."

    if (-not (Test-Path $downloadPath)) {
        throw "'$downloadPath' not found."
    }

    # The provider should be installed in the path: ~/.nuget/plugins/netcore/CredentialProvider.Microsoft/<binaries>
    Write-Host "Extracting $downloadPath ..."
    Expand-Archive -Force -Path $downloadPath -DestinationPath '~/.nuget/'
    Write-Host "The artifacts-credprovider was successfully installed" -ForegroundColor Green
}

<#
.SYNOPSIS
Converts a MinVer-style semantic version string to a four-part assembly version.

.DESCRIPTION
Takes a MinVer-style semver (e.g. '0.7.1-alpha.0.83') and returns a four-part
numeric version suitable for use as an assembly/file version (e.g. '0.7.1.83').

Rules:
  - Split on '-'; the first part supplies Major.Minor.Patch.
  - If a pre-release tail is present, the last numeric segment of that tail
    becomes the fourth part (build height). If no numeric segment exists, 0 is used.
  - If there is no pre-release tail, the fourth part is 0.

.PARAMETER Version
The MinVer-style semver string to convert.

.EXAMPLE
Convert-ToAssemblyVersion '0.7.1-alpha.0.83'
# Returns: 0.7.1.83

.EXAMPLE
Convert-ToAssemblyVersion '1.2.3'
# Returns: 1.2.3.0
#>
function Convert-ToAssemblyVersion {
    param (
        [Parameter(Mandatory = $true)]
        [string]
        $Version
    )

    $parts = $Version -split '-', 2
    $mmp = $parts[0]

    if ($parts.Length -gt 1) {
        $prerelease = $parts[1]
        $numericSegments = $prerelease -split '\.' | Where-Object { $_ -match '^\d+$' }
        if ($numericSegments) {
            $height = @($numericSegments)[-1]
        }
        else {
            $height = 0
        }
    }
    else {
        $height = 0
    }

    return "$mmp.$height"
}

Export-ModuleMember -Function Get-VersionNumber, Invoke-Promote, InstallCredentialHandler, Convert-ToAssemblyVersion
