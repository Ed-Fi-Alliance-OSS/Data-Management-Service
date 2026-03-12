# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param(
    [string]$EnvironmentFile = "./eng/docker-compose/.env.e2e",

    [string]
    [ValidateSet("Debug", "Release")]
    $Configuration = "Release",

    [string]
    [ValidateSet("pgsql", "mssql", "both")]
    $Dialect = "pgsql",

    [string]$OutputDirectory = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-RepoPath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

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

function Get-NewSchemaFiles {
    param(
        [string]$DirectoryPath,
        [string[]]$ExistingFiles
    )

    $currentFiles =
        @(Get-ChildItem -Path $DirectoryPath -Filter "ApiSchema*.json" -File -ErrorAction SilentlyContinue) |
        Select-Object -ExpandProperty FullName

    return @($currentFiles | Where-Object { $_ -notin $ExistingFiles })
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$environmentFilePath = Resolve-RepoPath $EnvironmentFile

if (-not (Test-Path $environmentFilePath)) {
    throw "Environment file not found: $environmentFilePath"
}

$schemaToolsProject = Join-Path $repoRoot "src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj"
$downloaderProject = Join-Path $repoRoot "src/dms/clis/EdFi.DataManagementService.ApiSchemaDownloader/EdFi.DataManagementService.ApiSchemaDownloader.csproj"

$environmentFileContent = Get-Content -Path $environmentFilePath -Raw
$schemaPackagesJson = Get-QuotedEnvJson -Content $environmentFileContent -Key "SCHEMA_PACKAGES"
$schemaPackages = @($schemaPackagesJson | ConvertFrom-Json)

if ($schemaPackages.Count -eq 0) {
    throw "SCHEMA_PACKAGES did not contain any packages."
}

$preflightRoot =
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        Join-Path ([System.IO.Path]::GetTempPath()) "dms-schema-preflight-$([Guid]::NewGuid().ToString('N'))"
    }
    else {
        Resolve-RepoPath $OutputDirectory
    }

$schemaDirectory = Join-Path $preflightRoot "ApiSchema"
$ddlOutputDirectory = Join-Path $preflightRoot "ddl"

New-Item -ItemType Directory -Path $schemaDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $ddlOutputDirectory -Force | Out-Null

Write-Host "Running DMS schema compile preflight"
Write-Host "Environment file: $environmentFilePath"
Write-Host "Dialect: $Dialect"
Write-Host "Configuration: $Configuration"
Write-Host "Schema download directory: $schemaDirectory"
Write-Host "DDL output directory: $ddlOutputDirectory"

$schemaFiles = New-Object System.Collections.Generic.List[string]

foreach ($schemaPackage in $schemaPackages) {
    $packageName = [string]$schemaPackage.name
    $packageVersion = [string]$schemaPackage.version
    $feedUrl = [string]$schemaPackage.feedUrl

    Write-Host "Downloading package: $packageName $packageVersion"

    $existingFiles =
        @(Get-ChildItem -Path $schemaDirectory -Filter "ApiSchema*.json" -File -ErrorAction SilentlyContinue) |
        Select-Object -ExpandProperty FullName

    & dotnet run --no-build --configuration $Configuration --project $downloaderProject -- `
        -p $packageName `
        -v $packageVersion `
        -f $feedUrl `
        -d $schemaDirectory

    if ($LASTEXITCODE -ne 0) {
        throw "ApiSchema download failed for package $packageName $packageVersion."
    }

    $newFiles = @(Get-NewSchemaFiles -DirectoryPath $schemaDirectory -ExistingFiles $existingFiles)

    if ($newFiles.Count -ne 1) {
        $joinedFiles = if ($newFiles.Count -eq 0) { "<none>" } else { $newFiles -join ", " }
        throw "Expected exactly one ApiSchema*.json file from package $packageName, found $($newFiles.Count): $joinedFiles"
    }

    $schemaFilePath = [System.IO.Path]::GetFullPath($newFiles[0])
    $schemaFiles.Add($schemaFilePath)
    Write-Host "Resolved schema file: $schemaFilePath"
}

Write-Host "Compiling schema inputs:"
$schemaFiles | ForEach-Object { Write-Host "  $_" }

$schemaEmitArgs = @(
    "run",
    "--no-build",
    "--configuration",
    $Configuration,
    "--project",
    $schemaToolsProject,
    "--",
    "ddl",
    "emit",
    "--schema"
) + $schemaFiles + @(
    "--output",
    $ddlOutputDirectory,
    "--dialect",
    $Dialect
)

& dotnet @schemaEmitArgs

if ($LASTEXITCODE -ne 0) {
    throw "DMS schema compile preflight failed."
}

Write-Host "DMS schema compile preflight completed successfully."
