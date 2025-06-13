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
    [OutputType([String])]
    param(
        [Parameter(Mandatory=$true)]
        [string]
        $PackageName,

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

    $lowerId = $PackageName.ToLower()
    # Lookup available packages
    $package = Invoke-RestMethod "$($packageService)$($lowerId)/index.json"
    # Sort by SemVer
    $versions = Invoke-SemanticSort $package.versions

    if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
        Write-Host -ForegroundColor Yellow "No version specified. Using latest available version."
        $versionSearch = $versions[-1]
    }
    else {
        # pad this out to three part semver if only partial
        switch ($PackageVersion.Split('.').Length) {
            1 { $versionSearch = "$PackageVersion.*.*" }
            2 { $versionSearch = "$PackageVersion.*" }
            default { $versionSearch = $PackageVersion }
        }
    }

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
    Download and extract the Ed-Fi SmokeTest Console.

.OUTPUTS
    String containing the name of the created directory, e.g.
    `.packages/edfi.suite3.smoketest.3.3.1`.

.EXAMPLE
    Get-SmokeTestTool -PackageVersion 5

.EXAMPLE
    Get-SmokeTestTool -PackageVersion 6 -PreRelease

#>
function Get-SmokeTestTool {
    param (
        # Requested version, example: "5" (latest 5.x.y), "5.3" (latest 5.3.y), "5.3.123" (exact)
        [Parameter(Mandatory=$true)]
        [string]
        $PackageVersion,

        # Enable usage of prereleases
        [Switch]
        $PreRelease
    )

    Get-NugetPackage -PackageName "EdFi.Suite3.SmokeTest.Console" `
        -PreRelease:$PreRelease `
        -PackageVersion $PackageVersion | Out-String
}

<#
.SYNOPSIS
    Download the Ed-Fi Api Sdk dll.

.OUTPUTS
    String containing the sdk dll path, e.g.
    `sdk/EdFi.OdsApi.Sdk.dll`.
#>
function Get-ApiSdkDll {

    $zip = "EdFi.OdsApi.Sdk.zip"

    $resourceUrl = "https://odsassets.blob.core.windows.net/public/project-tanager/sdk/5.1.0/$zip"

    $directory = "sdk"
    $file = "EdFi.OdsApi.Sdk.dll"

    if (!(Test-Path $directory -PathType Container)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    Push-Location $directory
    $fullPath = Join-Path -Path $directory -ChildPath $file

    if ($null -ne (Get-ChildItem $file -ErrorAction SilentlyContinue)) {
        Pop-Location
        return $fullPath
    }

    try {
        Invoke-WebRequest $resourceUrl -OutFile $zip
        Expand-Archive $zip -DestinationPath $(Get-Location)
        Remove-Item $zip
        return $fullPath
    }
    catch {
        throw $_
    }
    finally {
        Pop-Location
    }
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

<#
    .SYNOPSIS
    Creates a new .csproj file configured for generating a NuGet package using dotnet pack.

    .DESCRIPTION
    This function generates a .csproj file with standard NuGet metadata, enabling packaging via dotnet pack.

    .PARAMETER CsprojPath
    The full path (including filename) to create the .csproj file.

    .PARAMETER Id
    The package ID (used as AssemblyName and PackageId).

    .PARAMETER Description
    The package description (required).

    .PARAMETER Version
    The package version. Defaults to "0.0.0".

    .PARAMETER Title
    The package title (optional).

    .PARAMETER Authors
    Author(s) of the package (optional).

    .PARAMETER ProjectUrl
    URL of the project's homepage (optional).

    .PARAMETER Copyright
    Copyright text. Defaults to the current year and Ed-Fi notice.

    .PARAMETER Summary
    A brief summary of the package (optional).

    .PARAMETER License
    SPDX license identifier (e.g., Apache-2.0). Required to include license metadata.

    .PARAMETER ForceOverwrite
    If specified, will overwrite an existing .csproj file.

    .EXAMPLE
    New-CsprojForNuget -CsprojPath ".\MyPackage.csproj" -Id "MyTool" -Description "A helper tool" -ForceOverwrite
#>
function New-CsprojForNuget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$CsprojPath,

        [Parameter(Mandatory = $true)]
        [string]$Id,

        [Parameter(Mandatory = $true)]
        [string]$Description,

        [string]$Version = "0.0.0",
        [string]$Title,
        [string]$Authors,
        [string]$ProjectUrl,
        [string]$Copyright = "Copyright © $((Get-Date).Year) Ed-Fi Alliance, LLC and Contributors",
        [string]$License = "Apache-2.0",
        [switch]$ForceOverwrite
    )

    if (Test-Path $CsprojPath) {
        if (-not $ForceOverwrite) {
            throw "File '$CsprojPath' already exists. Use -ForceOverwrite to overwrite."
        }
        Remove-Item -Path $CsprojPath -Force
    }

    $xml = New-Object System.Xml.XmlDocument

    $project = $xml.CreateElement("Project")
    $project.SetAttribute("Sdk", "Microsoft.NET.Sdk")
    $xml.AppendChild($project) | Out-Null

    $propertyGroup = $xml.CreateElement("PropertyGroup")
    $project.AppendChild($propertyGroup) | Out-Null

    $elements = @{
        TargetFramework                 = "netstandard2.0"
        PackageId                       = $Id
        Version                         = $Version
        Authors                         = $Authors
        PackageProjectUrl               = $ProjectUrl
        Copyright                       = $Copyright
        Description                     = $Description
        PackageLicenseExpression        = $License
        GeneratePackageOnBuild          = "true"
        OutputType                      = "None"
        IsPackable                      = "true"
        NoBuild                         = "true"
        IncludeBuildOutput              = "false"
        IncludeRepositoryUrl            = "false"
        SuppressDependenciesWhenPacking = "true"
    }

    foreach ($key in $elements.Keys) {
        $value = $elements[$key]
        if ($value) {
            $node = $xml.CreateElement($key)
            $node.InnerText = $value
            $propertyGroup.AppendChild($node) | Out-Null
        }
    }

    $xml.Save($CsprojPath)
}


<#
    .SYNOPSIS
    Adds file entries to a .csproj file for packaging using dotnet pack.

    .DESCRIPTION
    This function modifies a `.csproj` file by inserting `<None>` elements into an `<ItemGroup>` section.
    Each entry maps a source file to a target path within the package. The function validates that
    each source exists and is not a directory, and then adds it to the XML structure of the .csproj file.

    .PARAMETER SourceTargetPair
    A required array of hashtables, each representing a source/target mapping.
    Each hashtable must contain:
    - `source`: A path or array of paths to files to include.
    - `target`: The relative path inside the NuGet package.

    .PARAMETER CsprojPath
    The path to the .csproj file to update. Must exist and be a valid XML file.

    .EXAMPLE
    Add-FileToCsProjForNuget -SourceTargetPair @(
        @{ source = "bin/MyLibrary.dll"; target = "lib/net6.0" },
        @{ source = "readme.md"; target = "content" }
    ) -CsprojPath "./MyPackage.csproj"

    Adds MyLibrary.dll and readme.md to the specified .csproj under their respective target paths.
#>
function Add-FileToCsProjForNuget {
    [CmdletBinding()]
    param(
        [parameter(mandatory = $true)]
        [ValidateScript( {
            if ($_.target -and $_.source) {
                $true
            } else {
                Write-Host "SourceTargetPair entry failed validation: $($_ | Out-String)";
                $false
            }
        })]
        [hashtable[]]$SourceTargetPair,

        [Parameter(Mandatory = $true)]
        [string]$CsprojPath
    )

    $resolvedCsprojPath = Resolve-Path -Path $CsprojPath
    [xml]$xml = Get-Content -Path $resolvedCsprojPath

    $filesItemGroup = $xml.CreateElement('ItemGroup')

    foreach ($pair in $sourceTargetPair) {
        $target = $pair.target -replace '[\\/]', [IO.Path]::DirectorySeparatorChar
        $sources = @($pair.source)
        foreach ($source in $sources) {
            $resolvedSource = Resolve-Path -Path $source -ErrorAction SilentlyContinue
            if (-not $resolvedSource) {
                Write-Warning "Source file not found: $source"
                continue
            }

            if ((Get-Item $resolvedSource).PSIsContainer) {
                continue  # skip directories
            }

            $fileElement = $xml.CreateElement("None")
            $fileElement.SetAttribute("Include", $resolvedSource.Path)
            $fileElement.SetAttribute("Pack", "true")
            $fileElement.SetAttribute("PackagePath", $target)


            Write-Verbose "Adding file: src='$($resolvedSource.Path)' target='$target'"
            $filesItemGroup.AppendChild($fileElement) | Out-Null
        }
    }

    $projectElem = $xml.SelectSingleNode('//Project')
    $projectElem.AppendChild($filesItemGroup) | Out-Null

    $xml.Save($resolvedCsprojPath.Path)
}

Export-ModuleMember -Function `
    Get-SampleData, Get-NugetPackage, Get-BulkLoadClient, Get-SouthridgeSampleData, Get-SmokeTestTool, Get-ApiSdkDll, New-CsprojForNuget, Add-FileToCsProjForNuget
