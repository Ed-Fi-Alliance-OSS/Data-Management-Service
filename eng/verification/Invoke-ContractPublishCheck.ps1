# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Decides whether a packed contract package may be published at the version it declares.

.DESCRIPTION
    Publish when absent, skip when unchanged, fail when changed.

    A contract's version deliberately does not move with the Data Management Service release, while
    the prerelease workflow fires on every prerelease. So the same version is offered to the feed
    again and again, and two policies are ruled out by that alone: refusing outright when the
    version exists would fail every prerelease after the first, and pushing regardless would depend
    on the feed's own duplicate handling to protect a published contract.

    Comparing the .nupkg bytes is ruled out too. A nupkg is not byte-reproducible across runners, so
    a hash comparison would fail on packaging noise rather than on a changed contract.

    What is compared is three things, each canonicalized first:

      - the assembly's externally consumable surface, read from metadata;
      - the XML documentation, which is where both contracts state their load-bearing rules;
      - the declared nuspec dependencies, which decide what an implementer inherits.

    The packed readme is deliberately not compared. It is prose an implementer reads rather than
    something they compile or resolve against, and a documentation fix must not force a contract
    version bump.

    This script decides; it does not push. The caller acts on ShouldPush.

.NOTES
    Feed access is three injected script blocks so that every behaviour of this policy is testable
    without a network or a credential. The defaults perform the real requests.

    Two failure rules matter more than they look:

      - A package index that answers 404 means the id is absent **only** when the service index it
        was resolved from answered successfully. An unreachable or unauthorized feed must never look
        like an empty one, because the next thing that happens is a push.
      - A download that fails after the version index listed that version is fatal, 404 included.
        The listing and the fetch are the same feed a moment apart, so a disappearance is a feed
        fault rather than an absent package.

.OUTPUTS
    A PSCustomObject with ShouldPush, Reason, PackageId, PackageVersion and Comparisons.
#>
[CmdletBinding()]
param(
    # The .nupkg just packed, whose contents decide what would be published.
    [Parameter(Mandatory)]
    [string]
    $PackageFile,

    # The package id, passed in rather than read from the file so that a lane states the id it means
    # and a rename cannot leave this checking the old one.
    [Parameter(Mandatory)]
    [string]
    $PackageId,

    # The version the contract declares, read by the caller from the contract's own source.
    [Parameter(Mandatory)]
    [string]
    $PackageVersion,

    # A scratch directory for the two extractions and the download. Must be empty or absent.
    [Parameter(Mandatory)]
    [string]
    $WorkingDirectory,

    # The feed's NuGet v3 service index.
    [string]
    $ServiceIndexUrl = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",

    # Personal access token for a feed that requires one. Reading a public feed needs none.
    [string]
    $FeedApiKey = "",

    # Returns the feed's PackageBaseAddress/3.0.0 endpoint, or throws. Its success is what makes a
    # 404 from the package index meaningful.
    [scriptblock]
    $ResolvePackageBaseAddress,

    # Returns @{ Found = <bool>; Versions = @(...) } for a package id, or throws.
    [scriptblock]
    $GetPublishedVersions,

    # Downloads one package to a path, or throws.
    [scriptblock]
    $SavePublishedPackage,

    # The target framework whose lib/ folder carries the contract assembly.
    [string]
    $TargetFramework = "net10.0"
)

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "ContractPackageComparison.psm1") -Force

$surfaceReader = Join-Path $PSScriptRoot "Get-ContractPublicSurface.ps1"

function Get-FeedRequestHeader {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param([string] $ApiKey)

    if ([string]::IsNullOrWhiteSpace($ApiKey)) {
        return @{}
    }

    # Azure Artifacts accepts a PAT as the password of a basic credential; the user name is ignored.
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("nuget:$ApiKey"))

    return @{ Authorization = "Basic $encoded" }
}

# Defaults, used whenever the caller injects nothing. Each one throws with the feed's own status in
# the message, so a lane failure names what the feed said rather than only that something failed.
if ($null -eq $ResolvePackageBaseAddress) {
    $ResolvePackageBaseAddress = {
        param([string] $IndexUrl, [string] $ApiKey)

        $headers = Get-FeedRequestHeader -ApiKey $ApiKey

        try {
            $response = Invoke-RestMethod -Uri $IndexUrl -Headers $headers -Method Get
        }
        catch {
            throw "The feed's service index at $IndexUrl could not be read: $($_.Exception.Message). A feed that cannot be read is not an empty feed."
        }

        $resource = @(
            $response.resources |
                Where-Object { $_.'@type' -like "PackageBaseAddress/3.0.0*" }
        )

        if ($resource.Count -eq 0) {
            throw "The feed's service index at $IndexUrl advertises no PackageBaseAddress/3.0.0 resource."
        }

        return $resource[0].'@id'
    }
}

if ($null -eq $GetPublishedVersions) {
    $GetPublishedVersions = {
        param([string] $BaseAddress, [string] $NormalizedId, [string] $ApiKey)

        $headers = Get-FeedRequestHeader -ApiKey $ApiKey
        $url = "$BaseAddress/$NormalizedId/index.json"

        try {
            $response = Invoke-RestMethod -Uri $url -Headers $headers -Method Get
        }
        catch {
            $status = $_.Exception.Response.StatusCode.value__

            # 404 from the package index of a feed whose service index answered is the documented
            # way NuGet says "this id has never been published here".
            if ($status -eq 404) {
                return @{ Found = $false; Versions = @() }
            }

            throw "The feed answered $status for $url : $($_.Exception.Message). That is not an absent package."
        }

        # A 200 that carries no versions array is a malformed response rather than an empty package
        # index: the endpoint's whole contract is that array, and reading its absence as "no versions
        # published" would turn a broken feed into a push.
        if ($null -eq $response -or $null -eq $response.versions) {
            throw "The feed answered 200 for $url with no versions array. That is a malformed response, not an absent package."
        }

        # Not wrapped with @(). A scalar here means the endpoint returned something other than the
        # array it is defined to return, and auto-wrapping it would hide that behind a one-element
        # list.
        if ($response.versions -isnot [System.Collections.IEnumerable] -or $response.versions -is [string]) {
            throw "The feed answered 200 for $url with a versions property that is not an array. That is a malformed response, not an absent package."
        }

        return @{ Found = $true; Versions = $response.versions }
    }
}

if ($null -eq $SavePublishedPackage) {
    $SavePublishedPackage = {
        param([string] $BaseAddress, [string] $NormalizedId, [string] $NormalizedVersion, [string] $Destination, [string] $ApiKey)

        $headers = Get-FeedRequestHeader -ApiKey $ApiKey
        $url = "$BaseAddress/$NormalizedId/$NormalizedVersion/$NormalizedId.$NormalizedVersion.nupkg"

        try {
            Invoke-WebRequest -Uri $url -Headers $headers -Method Get -OutFile $Destination
        }
        catch {
            throw "The published package at $url could not be downloaded: $($_.Exception.Message). The version index listed this version, so a failure here is a feed fault rather than an absent package."
        }
    }
}

function Initialize-ScratchDirectory {
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([string])]
    param([Parameter(Mandatory)][string] $Path)

    if (Test-Path -LiteralPath $Path) {
        $existing = @(Get-ChildItem -LiteralPath $Path -Force)

        if ($existing.Count -gt 0) {
            throw "Refusing to work in $Path : it is not empty, and this script never deletes caller content. Pass a dedicated directory that is empty or does not exist."
        }
    }
    elseif ($PSCmdlet.ShouldProcess($Path, "Create scratch directory")) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }

    return $Path
}

<#
.DESCRIPTION
Extracts one package and returns the three things compared, failing closed on anything missing.

Every check here exists so that a malformed or empty package fails rather than comparing equal to
another malformed or empty package. "No public types" and "no XML file" are not contract states.
#>
function Read-ContractPackage {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $ExtractTo,
        [Parameter(Mandatory)][string] $ExpectedId,
        [Parameter(Mandatory)][string] $ExpectedVersion,
        [Parameter(Mandatory)][string] $Framework,
        [Parameter(Mandatory)][string] $Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The $Description package was not found: $Path"
    }

    if ((Get-Item -LiteralPath $Path).Length -eq 0) {
        throw "The $Description package at $Path is empty."
    }

    New-Item -ItemType Directory -Path $ExtractTo -Force | Out-Null

    try {
        Expand-Archive -LiteralPath $Path -DestinationPath $ExtractTo
    }
    catch {
        throw "The $Description package at $Path could not be expanded: $($_.Exception.Message)"
    }

    $nuspecFiles = @(Get-ChildItem -LiteralPath $ExtractTo -Filter "*.nuspec" -File)

    if ($nuspecFiles.Count -ne 1) {
        throw "The $Description package carries $($nuspecFiles.Count) nuspec file(s); exactly one is required."
    }

    $metadata = ([xml] (Get-Content -LiteralPath $nuspecFiles[0].FullName -Raw)).package.metadata

    $declaredId = ConvertTo-NormalizedPackageId -PackageId $metadata.id
    $expectedNormalizedId = ConvertTo-NormalizedPackageId -PackageId $ExpectedId

    if ($declaredId -cne $expectedNormalizedId) {
        throw "The $Description package declares id '$($metadata.id)', not '$ExpectedId'."
    }

    $declaredVersion = ConvertTo-NormalizedPackageVersion -Version $metadata.version
    $expectedNormalizedVersion = ConvertTo-NormalizedPackageVersion -Version $ExpectedVersion

    if ($declaredVersion -cne $expectedNormalizedVersion) {
        throw "The $Description package declares version '$($metadata.version)', not '$ExpectedVersion'."
    }

    $libraryFolder = Join-Path $ExtractTo "lib/$Framework"
    $assemblies = @(Get-ChildItem -LiteralPath $libraryFolder -Filter "*.dll" -File -ErrorAction SilentlyContinue)

    if ($assemblies.Count -ne 1) {
        throw "The $Description package carries $($assemblies.Count) assembly/assemblies in lib/$Framework; exactly one is required."
    }

    $documentation = [System.IO.Path]::ChangeExtension($assemblies[0].FullName, ".xml")

    if (-not (Test-Path -LiteralPath $documentation -PathType Leaf)) {
        throw "The $Description package carries no XML documentation beside $($assemblies[0].Name). The contract's rules live in those comments, so a package without them is not comparable."
    }

    $surface = [string[]] @(& $surfaceReader -AssemblyPath $assemblies[0].FullName)

    if ($surface.Count -eq 0) {
        throw "The $Description package's assembly $($assemblies[0].Name) exports nothing an implementer can bind to."
    }

    return [pscustomobject]@{
        Surface       = $surface
        Documentation = ConvertTo-CanonicalXmlDocumentation -Path $documentation
        Dependencies  = ConvertTo-CanonicalDependencySet -NuspecPath $nuspecFiles[0].FullName
        AssemblyName  = $assemblies[0].Name
    }
}

# Ordinal and position-sensitive. Both sides are already sorted where order does not matter, and
# PowerShell's default comparison is case-insensitive, under which a rule reworded only in case
# would read as unchanged.
function Compare-CanonicalList {
    [CmdletBinding()]
    [OutputType([string[]], [object[]])]
    param(
        [Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()][string[]] $Published,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()][string[]] $Packed
    )

    $differences = @(
        Compare-Object `
            -ReferenceObject ([string[]] @($Published)) `
            -DifferenceObject ([string[]] @($Packed)) `
            -CaseSensitive `
            -SyncWindow 0 |
            ForEach-Object {
                if ($_.SideIndicator -eq "<=") {
                    "only in the published package: $($_.InputObject)"
                }
                else {
                    "only in the packed package: $($_.InputObject)"
                }
            }
    )

    return , [string[]] $differences
}

$normalizedId = ConvertTo-NormalizedPackageId -PackageId $PackageId
$normalizedVersion = ConvertTo-NormalizedPackageVersion -Version $PackageVersion

$scratch = Initialize-ScratchDirectory -Path $WorkingDirectory

# The service index first, and on its own. Its success is what licenses reading a 404 from the
# package index as absence rather than as a feed that is down or refusing the credential.
$baseAddress = & $ResolvePackageBaseAddress $ServiceIndexUrl $FeedApiKey

# Validated here rather than only inside the default resolver, so an injected seam is held to the
# same contract. A relative or malformed address would compose a package-index URL that fails in a
# way indistinguishable from an absent package.
[uri] $baseUri = $null

if (
    -not [uri]::TryCreate($baseAddress, [System.UriKind]::Absolute, [ref] $baseUri) -or
    ($baseUri.Scheme -ne "http" -and $baseUri.Scheme -ne "https")
) {
    throw "The feed's service index resolved to '$baseAddress', which is not an absolute http or https address."
}

$baseAddress = $baseAddress.TrimEnd('/')

$published = & $GetPublishedVersions $baseAddress $normalizedId $FeedApiKey

if ($null -eq $published -or $null -eq $published.Found) {
    throw "The feed lookup for $PackageId returned no result. A feed that cannot be read is not an absent package."
}

# A Found that is not a boolean is not an answer. PowerShell would treat any non-empty value as
# true, so a lookup returning a string or an object would decide the branch by accident.
if ($published.Found -isnot [bool]) {
    throw "The feed lookup for $PackageId reported Found as '$($published.Found)', which is not a boolean. That is a malformed result, not an absent package."
}

$publishedVersions = @()

if ($published.Found) {
    if ($null -eq $published.Versions) {
        throw "The feed reported $PackageId as present and listed no versions. That is a malformed response, not an absent version."
    }

    # Every entry is normalized, and none is discarded. Filtering blank or unparseable entries away
    # empties the list, an empty list reads as "this version is not published", and that reads as a
    # push. A version index this reader cannot understand is a feed it cannot trust.
    $publishedVersions = @(
        $published.Versions |
            ForEach-Object {
                if ($null -eq $_ -or [string]::IsNullOrWhiteSpace([string] $_)) {
                    throw "The feed listed a blank version for $PackageId. That is a malformed version index, not an absent version."
                }

                ConvertTo-NormalizedPackageVersion -Version ([string] $_)
            }
    )
}

if (-not $published.Found -or $publishedVersions -notcontains $normalizedVersion) {
    Write-Output "$PackageId $normalizedVersion is not on the feed; it will be published."

    return [pscustomobject]@{
        ShouldPush     = $true
        Reason         = "absent"
        PackageId      = $PackageId
        PackageVersion = $normalizedVersion
        Comparisons    = @()
    }
}

$downloadPath = Join-Path $scratch "$normalizedId.$normalizedVersion.published.nupkg"
& $SavePublishedPackage $baseAddress $normalizedId $normalizedVersion $downloadPath $FeedApiKey

if (-not (Test-Path -LiteralPath $downloadPath -PathType Leaf)) {
    throw "The published $PackageId $normalizedVersion was not written to $downloadPath, although the feed reported no error."
}

$publishedContract = Read-ContractPackage `
    -Path $downloadPath `
    -ExtractTo (Join-Path $scratch "published") `
    -ExpectedId $PackageId `
    -ExpectedVersion $normalizedVersion `
    -Framework $TargetFramework `
    -Description "published"

$packedContract = Read-ContractPackage `
    -Path $PackageFile `
    -ExtractTo (Join-Path $scratch "packed") `
    -ExpectedId $PackageId `
    -ExpectedVersion $normalizedVersion `
    -Framework $TargetFramework `
    -Description "packed"

$comparisons = [ordered]@{
    "public surface"     = Compare-CanonicalList -Published $publishedContract.Surface -Packed $packedContract.Surface
    "XML documentation"  = Compare-CanonicalList -Published $publishedContract.Documentation -Packed $packedContract.Documentation
    "declared dependencies" = Compare-CanonicalList -Published $publishedContract.Dependencies -Packed $packedContract.Dependencies
}

$changed = @($comparisons.Keys | Where-Object { $comparisons[$_].Count -gt 0 })

if ($changed.Count -gt 0) {
    $detail = foreach ($component in $changed) {
        "  $component changed:" + [Environment]::NewLine +
        (($comparisons[$component] | ForEach-Object { "    $_" }) -join [Environment]::NewLine)
    }

    throw (
        "$PackageId $normalizedVersion is already published and its $($changed -join ' and ') differ(s) from what was just packed." +
        [Environment]::NewLine +
        ($detail -join [Environment]::NewLine) +
        [Environment]::NewLine +
        "A published version is immutable. Bump the contract's declared version and pack again."
    )
}

Write-Output "$PackageId $normalizedVersion is already published, unchanged: public surface, XML documentation and declared dependencies all match."

return [pscustomobject]@{
    ShouldPush     = $false
    Reason         = "unchanged"
    PackageId      = $PackageId
    PackageVersion = $normalizedVersion
    Comparisons    = $comparisons
}
