# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

function Get-QuotedEnvJson {
    param(
        [string]$Content,
        [string]$Key
    )

    $pattern = "(?ms)^[ \t]*$([Regex]::Escape($Key))='(?<value>\[.*?\])'"
    $match = [Regex]::Match($Content, $pattern)

    if (-not $match.Success) {
        throw "Unable to find quoted JSON env value for '$Key'."
    }

    return $match.Groups["value"].Value
}

function Resolve-PackageRelativePath {
    param(
        [string]$PackageRoot,
        [string]$RelativePath,
        [string]$Description
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath)) {
        throw "$Description path is missing from package manifest."
    }

    if ([System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "$Description path '$RelativePath' from package manifest must be relative."
    }

    foreach ($pathPart in ($RelativePath -split "[/\\]")) {
        if ($pathPart -eq "..") {
            throw "$Description path '$RelativePath' from package manifest contains parent-directory traversal."
        }
    }

    $fullPackageRoot = [System.IO.Path]::GetFullPath($PackageRoot)
    $fullPath = [System.IO.Path]::GetFullPath((Join-Path $fullPackageRoot $RelativePath))
    $relativeToPackageRoot = [System.IO.Path]::GetRelativePath($fullPackageRoot, $fullPath)

    if (
        [System.IO.Path]::IsPathRooted($relativeToPackageRoot) `
            -or $relativeToPackageRoot -eq ".." `
            -or $relativeToPackageRoot.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal) `
            -or $relativeToPackageRoot.StartsWith("..$([System.IO.Path]::AltDirectorySeparatorChar)", [StringComparison]::Ordinal)
    ) {
        throw "$Description path '$RelativePath' from package manifest resolves outside the package directory."
    }

    return $fullPath
}

function Resolve-SchemaFileFromPackageManifest {
    param(
        [string]$SchemaDirectory,
        [string]$PackageName
    )

    $packageRoot = Join-Path (Join-Path $SchemaDirectory "Packages") $PackageName
    $manifestPath = Join-Path $packageRoot "package-manifest.json"

    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "ApiSchema package manifest not found for package ${PackageName}: $manifestPath"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifestVersion = [string]$manifest.version

    if ($manifestVersion -ne "1") {
        throw "Unsupported ApiSchema package manifest version '$manifestVersion' in $manifestPath."
    }

    $schemaFilePath = Resolve-PackageRelativePath `
        -PackageRoot $packageRoot `
        -RelativePath ([string]$manifest.schemaPath) `
        -Description "ApiSchema"

    if (-not (Test-Path -LiteralPath $schemaFilePath -PathType Leaf)) {
        throw "ApiSchema file declared by package manifest was not found for package ${PackageName}: $schemaFilePath"
    }

    return $schemaFilePath
}

<#
.SYNOPSIS
    Reads the schema package definitions from an environment file.
#>
function Get-SchemaPackagesFromEnvironmentFile {
    param([string]$EnvironmentFilePath)

    $environmentFileContent = Get-Content -Path $EnvironmentFilePath -Raw
    $schemaPackagesJson = Get-QuotedEnvJson -Content $environmentFileContent -Key "SCHEMA_PACKAGES"
    $schemaPackages = @($schemaPackagesJson | ConvertFrom-Json)

    if ($schemaPackages.Count -eq 0) {
        throw "SCHEMA_PACKAGES did not contain any packages."
    }

    return $schemaPackages
}

<#
.SYNOPSIS
    Downloads the configured schema packages and returns the resolved schema file paths.
#>
function Resolve-SchemaFilesFromEnvironmentFile {
    param(
        [string]$EnvironmentFilePath,
        [string]$Configuration,
        [string]$RepoRoot,
        [string]$SchemaDirectory,
        [string[]]$DownloaderDotnetRunArgs = @("--no-build")
    )

    $downloaderProject = Join-Path $RepoRoot "src/dms/clis/EdFi.DataManagementService.ApiSchemaDownloader/EdFi.DataManagementService.ApiSchemaDownloader.csproj"
    $schemaPackages = @(Get-SchemaPackagesFromEnvironmentFile -EnvironmentFilePath $EnvironmentFilePath)

    New-Item -ItemType Directory -Path $SchemaDirectory -Force | Out-Null

    $schemaFiles = New-Object System.Collections.Generic.List[string]

    foreach ($schemaPackage in $schemaPackages) {
        $packageName = [string]$schemaPackage.name
        $packageVersion = [string]$schemaPackage.version
        $feedUrl = [string]$schemaPackage.feedUrl

        Write-Information "Downloading schema package: $packageName $packageVersion" -InformationAction Continue

        $downloadArgs = @(
            "run"
        ) + @($DownloaderDotnetRunArgs) + @(
            "--configuration",
            $Configuration,
            "--project",
            $downloaderProject,
            "--",
            "-p",
            $packageName,
            "-v",
            $packageVersion,
            "-f",
            $feedUrl,
            "-d",
            $SchemaDirectory
        )

        $downloadOutput = & dotnet @downloadArgs 2>&1

        if ($downloadOutput) {
            $downloadOutput | ForEach-Object {
                Write-Information $_ -InformationAction Continue
            }
        }

        if ($LASTEXITCODE -ne 0) {
            throw "ApiSchema download failed for package $packageName $packageVersion."
        }

        $schemaFilePath = Resolve-SchemaFileFromPackageManifest `
            -SchemaDirectory $SchemaDirectory `
            -PackageName $packageName
        $schemaFiles.Add($schemaFilePath)
        Write-Information "Resolved schema file: $schemaFilePath" -InformationAction Continue
    }

    return $schemaFiles.ToArray()
}

Export-ModuleMember -Function Get-SchemaPackagesFromEnvironmentFile, Resolve-SchemaFilesFromEnvironmentFile
