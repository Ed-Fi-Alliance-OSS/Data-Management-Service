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

function Get-NewSchemaFilePath {
    param(
        [string]$DirectoryPath,
        [string[]]$ExistingFiles
    )

    $currentFiles =
        @(Get-ChildItem -Path $DirectoryPath -Filter "ApiSchema*.json" -File -ErrorAction SilentlyContinue) |
        Select-Object -ExpandProperty FullName

    return @($currentFiles | Where-Object { $_ -notin $ExistingFiles })
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

        $existingFiles =
            @(Get-ChildItem -Path $SchemaDirectory -Filter "ApiSchema*.json" -File -ErrorAction SilentlyContinue) |
            Select-Object -ExpandProperty FullName

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

        $newFiles = @(Get-NewSchemaFilePath -DirectoryPath $SchemaDirectory -ExistingFiles $existingFiles)

        if ($newFiles.Count -ne 1) {
            $joinedFiles = if ($newFiles.Count -eq 0) { "<none>" } else { $newFiles -join ", " }
            throw "Expected exactly one ApiSchema*.json file from package $packageName, found $($newFiles.Count): $joinedFiles"
        }

        $schemaFilePath = [System.IO.Path]::GetFullPath($newFiles[0])
        $schemaFiles.Add($schemaFilePath)
        Write-Information "Resolved schema file: $schemaFilePath" -InformationAction Continue
    }

    return $schemaFiles.ToArray()
}

Export-ModuleMember -Function Get-SchemaPackagesFromEnvironmentFile, Resolve-SchemaFilesFromEnvironmentFile
