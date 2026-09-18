# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#requires -version 7

$ErrorActionPreference = "Stop"

# NuGet identity is defined once, in the module the publish check already uses, and the promotion
# functions below compare feed-listed versions against a contract's declared version by exactly the
# same rules. A second copy of "what makes two versions the same version" is how the release lane
# and the publish lane would come to disagree.
Import-Module (Join-Path $PSScriptRoot "eng/verification/ContractPackageComparison.psm1") -Force

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
        $PackageName,

        # The exact version to promote, for a package whose version does not move with the release.
        # Omitted, the version is derived from ReleaseRef as it always has been, which is what the
        # three release-stamped callers rely on.
        [String]
        $Version
    )

    $version = if ([string]::IsNullOrWhiteSpace($Version)) { $ReleaseRef -replace "v", "" } else { $Version }

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

<#
.DESCRIPTION
The view-scoped form of a feed's NuGet v3 service index.

Azure Artifacts addresses a feed's views by suffixing the feed name, so the Release view of
.../_packaging/EdFi/nuget/v3/index.json is .../_packaging/EdFi@Release/nuget/v3/index.json, and the
Local view, which holds every version pushed to the feed, is .../_packaging/EdFi@Local/.... Every
lookup names its view this way, so the population a step asked is stated in the URL it used rather
than left implicit in an unscoped address.

A URL that does not carry a _packaging/<feed>/ segment is rejected rather than rewritten, because a
silently unmodified URL would leave the view implicit.

.EXAMPLE
Get-ViewScopedServiceIndexUrl -ServiceIndexUrl "https://pkgs.dev.azure.com/o/p/_packaging/EdFi/nuget/v3/index.json" -ViewName "Release"
#>
function Get-ViewScopedServiceIndexUrl {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]
        $ServiceIndexUrl,

        [Parameter(Mandatory)]
        [string]
        $ViewName
    )

    if ([string]::IsNullOrWhiteSpace($ServiceIndexUrl)) {
        throw "Cannot scope a service index to the $ViewName view: no service index URL was supplied."
    }

    $pattern = '(?<prefix>/_packaging/)(?<feed>[^/@]+)(?<suffix>/)'

    if ($ServiceIndexUrl -notmatch $pattern) {
        throw "Cannot scope '$ServiceIndexUrl' to the $ViewName view: it carries no /_packaging/<feed>/ segment."
    }

    return [regex]::Replace(
        $ServiceIndexUrl,
        $pattern,
        { param($match) $match.Groups['prefix'].Value + $match.Groups['feed'].Value + "@$ViewName" + $match.Groups['suffix'].Value },
        1
    )
}

<#
.DESCRIPTION
Whether a package version is a member of one feed view.

Every failure here is fatal on purpose. This answer decides a mutation: at release time an absence
from the Release view leads to a promotion, and an absence from the Local view fails the release. A
feed that cannot be read, or that answers with something other than a version index, must never be
read as "not a member".

The request seam is injectable so that every outcome is testable without a feed or a credential.
The default mirrors the rules Invoke-ContractPublishCheck.ps1 applies: the service index must answer
before a package-index 404 may be read as absence, and any other status is an error.
#>
function Test-PackageInView {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        # The feed's unscoped NuGet v3 service index, which is scoped to the view here.
        [Parameter(Mandatory)]
        [string]
        $ServiceIndexUrl,

        [Parameter(Mandatory)]
        [string]
        $ViewName,

        [Parameter(Mandatory)]
        [string]
        $PackageName,

        [Parameter(Mandatory)]
        [string]
        $Version,

        [string]
        $ApiKey = "",

        # Returns the versions of $PackageName in the view addressed by the supplied index URL, as a
        # hashtable of Found and Versions, or throws.
        [scriptblock]
        $GetViewVersions
    )

    $viewIndexUrl = Get-ViewScopedServiceIndexUrl -ServiceIndexUrl $ServiceIndexUrl -ViewName $ViewName

    if ($null -eq $GetViewVersions) {
        $GetViewVersions = ${function:Get-FeedViewVersion}
    }

    $result = & $GetViewVersions $viewIndexUrl $PackageName $ApiKey

    if ($null -eq $result -or $result.Found -isnot [bool]) {
        throw "The $ViewName view lookup for $PackageName returned no usable result. A view that cannot be read is not a view without the package."
    }

    if (-not $result.Found) {
        return $false
    }

    if ($null -eq $result.Versions) {
        throw "The $ViewName view reported $PackageName as present and listed no versions. That is a malformed response, not an absent version."
    }

    $normalized = ConvertTo-NormalizedPackageVersion -Version $Version

    foreach ($listed in $result.Versions) {
        if ($null -eq $listed -or [string]::IsNullOrWhiteSpace([string] $listed)) {
            throw "The $ViewName view listed a blank version for $PackageName. That is a malformed version index, not an absent version."
        }

        if ((ConvertTo-NormalizedPackageVersion -Version ([string] $listed)) -ceq $normalized) {
            return $true
        }
    }

    return $false
}

<#
.DESCRIPTION
The default view lookup: resolve the view's package base address, then ask for the package's
versions.
#>
function Get-FeedViewVersion {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [string]
        $ViewIndexUrl,

        [Parameter(Mandatory)]
        [string]
        $PackageName,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $ApiKey
    )

    $headers = @{}

    if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
        # Azure Artifacts accepts a PAT as the password of a basic credential; the user name is
        # ignored.
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("nuget:$ApiKey"))
        $headers = @{ Authorization = "Basic $encoded" }
    }

    try {
        $index = Invoke-RestMethod -Uri $ViewIndexUrl -Headers $headers -Method Get
    }
    catch {
        throw "The view's service index at $ViewIndexUrl could not be read: $($_.Exception.Message). A view that cannot be read is not a view without the package."
    }

    $resource = @($index.resources | Where-Object { $_.'@type' -like "PackageBaseAddress/3.0.0*" })

    if ($resource.Count -eq 0) {
        throw "The view's service index at $ViewIndexUrl advertises no PackageBaseAddress/3.0.0 resource."
    }

    $baseAddress = $resource[0].'@id'
    [uri] $baseUri = $null

    if (
        -not [uri]::TryCreate($baseAddress, [System.UriKind]::Absolute, [ref] $baseUri) -or
        ($baseUri.Scheme -ne "http" -and $baseUri.Scheme -ne "https")
    ) {
        throw "The view's service index resolved to '$baseAddress', which is not an absolute http or https address."
    }

    $normalizedId = ConvertTo-NormalizedPackageId -PackageId $PackageName
    $url = "$($baseAddress.TrimEnd('/'))/$normalizedId/index.json"

    try {
        $response = Invoke-RestMethod -Uri $url -Headers $headers -Method Get
    }
    catch {
        $status = $_.Exception.Response.StatusCode.value__

        # 404 from a view's package index, reached through a service index that answered, is how
        # NuGet says the id is not in this view.
        if ($status -eq 404) {
            return @{ Found = $false; Versions = @() }
        }

        throw "The view answered $status for $url : $($_.Exception.Message). That is not an absent package."
    }

    if ($null -eq $response -or $null -eq $response.versions) {
        throw "The view answered 200 for $url with no versions array. That is a malformed response, not an absent package."
    }

    if ($response.versions -isnot [System.Collections.IEnumerable] -or $response.versions -is [string]) {
        throw "The view answered 200 for $url with a versions property that is not an array. That is a malformed response, not an absent package."
    }

    return @{ Found = $true; Versions = $response.versions }
}

<#
.DESCRIPTION
Promotes one contract package into the release view, with three outcomes and no fourth.

A contract's version deliberately does not move with the release, so every release after the first
offers a version that is already promoted. Absence from the feed cannot be the test for that,
because a version that was never pushed is absent from the feed too: treating absence as "already
promoted" would let a missed publish exit zero.

So two views are asked:

  present in the release view          -> already promoted, nothing to do
  absent there, present in Local       -> promote it
  absent from both                     -> fail, naming the package and the version

The Local view is the feed's own population: every version pushed to the feed is a member, and the
prerelease lane pushes into it and promotes into nothing else. That is the sense in which this
repository has always used "pre-release" for the unscoped feed (eng/Package-Management.psm1 reads
the unscoped index as its pre-release source and the Release view as its release source), so it is
the population a not-yet-released contract version is looked for in. Absence from it means the exact
version is not on the feed to be promoted now, whether it was never pushed or has since been
deleted; either way the release cannot proceed on that contract.

The message is returned, not also written to the output stream: a caller that captured it would
otherwise receive it twice.

.EXAMPLE
Invoke-ContractPromotion -PackagesURL $url -Username $user -Password $secret -ServiceIndexUrl $index -PackageName EdFi.Api.Plugins -Version 1.0.0
#>
function Invoke-ContractPromotion {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Passed through to Invoke-Promote in a splat the rule does not follow.')]
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [String]
        $PackagesURL,

        [Parameter(Mandatory)]
        [String]
        $Username,

        [Parameter(Mandatory)]
        [SecureString]
        $Password,

        # The feed's unscoped NuGet v3 service index, scoped to each view for the lookups.
        [Parameter(Mandatory)]
        [String]
        $ServiceIndexUrl,

        [Parameter(Mandatory)]
        [String]
        $PackageName,

        # The contract's own declared version, read by the caller from the contract's own source.
        [Parameter(Mandatory)]
        [String]
        $Version,

        [String]
        $ReleaseViewName = "Release",

        # The view that holds every version pushed to the feed, whether or not it has been released.
        [String]
        $PublishedViewName = "Local",

        [String]
        $ApiKey = "",

        [scriptblock]
        $GetViewVersions,

        # Performs the promotion. Injectable so the outcomes are testable with no feed mutation.
        [scriptblock]
        $Promote
    )

    $lookup = @{
        ServiceIndexUrl = $ServiceIndexUrl
        PackageName     = $PackageName
        Version         = $Version
        ApiKey          = $ApiKey
    }

    if ($null -ne $GetViewVersions) {
        $lookup.GetViewVersions = $GetViewVersions
    }

    if (Test-PackageInView @lookup -ViewName $ReleaseViewName) {
        return "$PackageName $Version is already in the $ReleaseViewName view; nothing to do."
    }

    if (-not (Test-PackageInView @lookup -ViewName $PublishedViewName)) {
        throw "$PackageName $Version is in neither the $ReleaseViewName nor the $PublishedViewName view. A version that is not on the feed cannot be promoted; check that the prerelease published it and that it has not been deleted."
    }

    if ($null -eq $Promote) {
        $Promote = {
            param($Arguments)

            Invoke-Promote @Arguments
        }
    }

    if ($PSCmdlet.ShouldProcess("$PackageName $Version", "Promote to the $ReleaseViewName view")) {
        & $Promote @{
            PackagesURL = $PackagesURL
            Username    = $Username
            Password    = $Password
            ViewId      = $ReleaseViewName.ToLowerInvariant()
            ReleaseRef  = $Version
            Version     = $Version
            PackageName = $PackageName
        }
    }

    return "$PackageName $Version promoted to the $ReleaseViewName view."
}

Export-ModuleMember -Function `
    Get-VersionNumber, `
    Invoke-Promote, `
    InstallCredentialHandler, `
    Convert-ToAssemblyVersion, `
    Get-PluginsContractVersion, `
    Get-CustomValidationContractVersion, `
    Get-ViewScopedServiceIndexUrl, `
    Test-PackageInView, `
    Get-FeedViewVersion, `
    Invoke-ContractPromotion
