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
    Write-Information "Downloading artifacts-credprovider from $credProviderUrl ..." -InformationAction Continue
    $webClient = New-Object System.Net.WebClient
    $webClient.DownloadFile($credProviderUrl, $downloadPath)

    Write-Information "Download complete." -InformationAction Continue

    if (-not (Test-Path $downloadPath)) {
        throw "'$downloadPath' not found."
    }

    # The provider should be installed in the path: ~/.nuget/plugins/netcore/CredentialProvider.Microsoft/<binaries>
    Write-Information "Extracting $downloadPath ..." -InformationAction Continue
    Expand-Archive -Force -Path $downloadPath -DestinationPath '~/.nuget/'
    Write-Information "The artifacts-credprovider was successfully installed" -InformationAction Continue
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

<#
.DESCRIPTION
Reads one declared version element out of an MSBuild file, for the two contract packages that carry
their own version rather than the DMS release version.

Not exported. The two readers below are the callable surface, because a caller naming its own
element would be free to read a property the compiler does not use.

SelectNodes rather than property access, and a count check rather than SelectSingleNode: property
access on a file that grew a second PropertyGroup returns an array and stringifies into a version no
package will ever carry, and SelectSingleNode would quietly return whichever declaration came first.
Two declarations mean the single-declaration property this whole mechanism rests on has already been
lost, so it is reported rather than resolved.
#>
function Get-DeclaredVersionElement {
    param (
        # The MSBuild file to read.
        [Parameter(Mandatory)]
        [string]
        $Path,

        # The PropertyGroup child element declaring the version.
        [Parameter(Mandatory)]
        [string]
        $ElementName,

        # Names the contract in every failure message, so a lane reports which version it could not
        # read rather than only which file it was looking at.
        [Parameter(Mandatory)]
        [string]
        $ContractDescription
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Cannot read the $ContractDescription version: $Path does not exist."
    }

    $declarations = ([xml] (Get-Content -LiteralPath $Path -Raw)).SelectNodes(
        "//PropertyGroup/$ElementName"
    )

    if ($declarations.Count -gt 1) {
        throw "Cannot read the $ContractDescription version: $Path declares $ElementName more than one time."
    }

    if ($declarations.Count -eq 0 -or [string]::IsNullOrWhiteSpace($declarations[0].InnerText)) {
        throw "Cannot read the $ContractDescription version: $Path declares no $ElementName."
    }

    return $declarations[0].InnerText.Trim()
}

<#
.DESCRIPTION
Reads the plugin contract's own declared version out of src/plugins/Directory.Build.props.

The contract package is versioned on its own and deliberately outside SetDMSAssemblyInfo's reach,
because the plugin loader's newer-plugin-on-older-host preflight compares AssemblyVersions: the
version has to move when the contract's public surface moves and must not move when it does not.
So no caller may substitute the DMS release version here.

The props file is the single declaration. This function is what keeps it single: the pack target
and every verification step that needs the version call this rather than repeating a literal that
could drift from the file the compiler actually reads.

.EXAMPLE
Get-PluginsContractVersion
# Returns: 1.0.0
#>
function Get-PluginsContractVersion {
    param (
        # The props file declaring the contract version. Defaults to the repository's own.
        [string]
        $PropsPath = (Join-Path $PSScriptRoot "src/plugins/Directory.Build.props")
    )

    return Get-DeclaredVersionElement `
        -Path $PropsPath `
        -ElementName "VersionPrefix" `
        -ContractDescription "plugin contract"
}

<#
.DESCRIPTION
Reads the custom-validation contract's own declared version out of its csproj.

The same argument as the sibling above, one file further in. This contract used to be packed at
-p:PackageVersion=$DMSVersion and to inherit its AssemblyVersion from the release-stamped
src/dms/Directory.Build.props, so a validator built against one release's contract would be refused
by an adjacent release whose contract surface was identical, naming two versions that differ in
nothing an implementer can act on. The csproj declares Version, AssemblyVersion and FileVersion,
which override the imported props, and every lane reads them through here.

The csproj is under src/dms rather than beside the plugin contract, so the element is Version rather
than the VersionPrefix its sibling props declares. That is the only difference between the two.

.EXAMPLE
Get-CustomValidationContractVersion
# Returns: 1.0.0
#>
function Get-CustomValidationContractVersion {
    param (
        # The project file declaring the contract version. Defaults to the repository's own.
        [string]
        $ProjectPath = (Join-Path $PSScriptRoot "src/dms/core/EdFi.DataManagementService.CustomValidation/EdFi.DataManagementService.CustomValidation.csproj")
    )

    return Get-DeclaredVersionElement `
        -Path $ProjectPath `
        -ElementName "Version" `
        -ContractDescription "custom-validation contract"
}

Export-ModuleMember -Function Get-VersionNumber, Invoke-Promote, InstallCredentialHandler, Convert-ToAssemblyVersion, Get-PluginsContractVersion, Get-CustomValidationContractVersion
