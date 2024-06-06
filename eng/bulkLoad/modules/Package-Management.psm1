# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7


# Azure DevOps hosts the Ed-Fi packages, and it requires TLS 1.2
if (-not [Net.ServicePointManager]::SecurityProtocol.HasFlag([Net.SecurityProtocolType]::Tls12)) {
    [Net.ServicePointManager]::SecurityProtocol += [Net.SecurityProtocolType]::Tls12
}

<#
.SYNOPSIS
    Sorts versions semantically.

.DESCRIPTION
    Semantic Version sorting means that "5.3.111" comes before "5.3.2", despite
    2 being greater than 1.

.EXAMPLE
    Invoke-SemanticSort @("5.1.1", "5.1.11", "5.2.9")

    Output: @("5.2.9", "5.1.11", "5.1.1")
#>
function Invoke-SemanticSort {
    param(
        [Parameter(Mandatory=$true)]
        [string[]]
        $Versions
    )

    $Versions `
        | Select-Object {$_.Split(".")} `
        | Sort-Object {$_.'$_.Split(".")'[0], $_.'$_.Split(".")'[1], $_.'$_.Split(".")'[2]} -Descending `
        | ForEach-Object { $_.'$_.Split(".")' -Join "." }
}

<#
.SYNOPSIS
    Downloads and extracts the latest compatible version of a NuGet package.

.DESCRIPTION
    Uses the [NuGet Server API](https://docs.microsoft.com/en-us/nuget/api/overview)
    to look for the latest compatible version of a NuGet package, where version is
    all or part of a Semantic Version. For example, if $PackageVersion = "5", this
    will download the most recent 5.minor.patch version. If $PackageVersion = "5.3",
    then it download the most recent 5.3.patch version. And if $PackageVersion = "5.3.1",
    then it will look for the exact version 5.3.1 and fail if it does not exist.

.OUTPUTS
    Directory name containing the downloaded files.

.EXAMPLE
    Get-NugetPackage -PackageName "EdFi.Suite3.RestApi.Databases" -PackageVersion "5.3"
#>
function Get-NugetPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory=$true)]
        [string]
        $PackageName,

        [Parameter(Mandatory=$true)]
        [string]
        $PackageVersion,

        # URL for the pre-release package feed
        [string]
        $PreReleaseServiceIndex = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",

        # URL for the release package feed
        [string]
        $ReleaseServiceIndex = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi%40Release/nuget/v3/index.json",

        # Enable usage of prereleases
        [Switch]
        $PreRelease
    )

    # Pre-releases
    $nugetServicesURL = $ReleaseServiceIndex
    if ($PreRelease) {
        $nugetServicesURL = $PreReleaseServiceIndex
    }

    # The first URL just contains metadata for looking up more useful services
    $nugetServices = Invoke-RestMethod $nugetServicesURL

    $packageService = $nugetServices.resources `
                        | Where-Object { $_."@type" -like "PackageBaseAddress*" } `
                        | Select-Object -Property "@id" -ExpandProperty "@id"

    # pad this out to three part semver
    $versionSearch
    switch ($PackageVersion.split(".").length) {
        1 { $versionSearch = "$PackageVersion.*.*"}
        2 { $versionSearch = "$PackageVersion.*" }
        3 { $versionSearch = $PackageVersion }
        default: { throw @"
Invalid version string ``$($PackageVersion)``. Should be one, two, or three components from a Semantic Version"
"@.Trim()
}
    }
    $lowerId = $PackageName.ToLower()

    # Lookup available packages
    $package = Invoke-RestMethod "$($packageService)$($lowerId)/index.json"

    # Sort by SemVer
    $versions = Invoke-SemanticSort $package.versions

    # Find the first available version that matches the requested version
    $version = $versions | Where-Object { $_ -like $versionSearch } | Select-Object -First 1

    if ($null -eq $version) {
        throw "Version ``$($PackageVersion)`` does not exist yet."
    }

    $file = "$($lowerId).$($version)"
    $zip = "$($file).zip"
    $packagesDir = ".packages"
    New-Item -Path $packagesDir -Force -ItemType Directory | Out-Null

    Push-Location $packagesDir

    if ($null -ne (Get-ChildItem $file -ErrorAction SilentlyContinue)) {
        # Already exists, don't re-download
        Pop-Location
        return "$($packagesDir)/$($file)"
    }

    try {
        Invoke-RestMethod "$($packageService)$($lowerId)/$($version)/$($file).nupkg" -OutFile $zip

        Expand-Archive $zip -Force

        Remove-Item $zip
    }
    catch {
        throw $_
    }
    finally {
        Pop-Location
    }

    "$($packagesDir)/$($file)"
}

<#
.SYNOPSIS
    Download and extract the Data Standard sample files.

.OUTPUTS
    String containing the name of the created directory, e.g.
    `.packages/edfi.datastandard.sampledata.3.3.1-b`.

.EXAMPLE
    Get-SampleData -PackageVersion 3

.EXAMPLE
    Get-SampleData -PackageVersion 4 -PreRelease

#>
function Get-SampleData {
    param (
        # Requested version, example: "3" (latest 3.x.y), "3.3" (latest 3.3.y), "3.3.1-b" (exact)
        [Parameter(Mandatory=$true)]
        [string]
        $PackageVersion,

        # Enable usage of prereleases
        [Switch]
        $PreRelease
    )

    Get-NugetPackage -PackageName "EdFi.DataStandard.SampleData" `
        -PreRelease:$PreRelease `
        -PackageVersion $PackageVersion | Out-String
}

<#
.SYNOPSIS
    Download and extract the Ed-Fi Client Side Bulk Loader.

.OUTPUTS
    String containing the name of the created directory, e.g.
    `.packages/edfi.datastandard.sampledata.3.3.1-b`.

.EXAMPLE
    Get-BulkLoadClient -PackageVersion 5

.EXAMPLE
    Get-BulkLoadClient -PackageVersion 6 -PreRelease

#>
function Get-BulkLoadClient {
    param (
        # Requested version, example: "5" (latest 5.x.y), "5.3" (latest 5.3.y), "5.3.123" (exact)
        [Parameter(Mandatory=$true)]
        [string]
        $PackageVersion,

        # Enable usage of prereleases
        [Switch]
        $PreRelease
    )

    Get-NugetPackage -PackageName "EdFi.Suite3.BulkLoadClient.Console" `
        -PreRelease:$PreRelease `
        -PackageVersion $PackageVersion | Out-String
}

<#
.SYNOPSIS
    Download and extract the Southridge Data.

.OUTPUTS
    String containing the name of the created directory, e.g.
    `.packages/southridge`.

.EXAMPLE
    Get-SouthridgeSampleData

#>
function Get-SouthridgeSampleData {

    try {

      if(-not (Get-Module 7Zip4PowerShell -ListAvailable)){
         Install-Module -Name 7Zip4PowerShell -Force
      }

      $file = "southridge-xml-2023"
      $zip = "$($file).7z"
      $packagesDir = ".packages"

      New-Item -Path $packagesDir -Force -ItemType Directory | Out-Null

      Push-Location $packagesDir

    if ($null -ne (Get-ChildItem $file -ErrorAction SilentlyContinue)) {
        # Already exists, don't re-download
        Pop-Location
        return "$($packagesDir)/$($file)"
    }

      Invoke-WebRequest -Uri "https://odsassets.blob.core.windows.net/public/Northridge/$($zip)" `
        -OutFile $zip | Out-String

      Expand-7Zip $zip $(Get-Location)

      Remove-Item $zip

      return "$($packagesDir)/$($file)"
    }
    catch {
        throw $_
    }
    finally {
        Pop-Location
    }


}

Export-ModuleMember -Function Get-SampleData, Get-NugetPackage, Get-BulkLoadClient, Get-SouthridgeSampleData
