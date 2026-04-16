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

Import-Module (Join-Path $PSScriptRoot "schema-package-utility.psm1") -Force

function Resolve-RepoPath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$environmentFilePath = Resolve-RepoPath $EnvironmentFile

if (-not (Test-Path $environmentFilePath)) {
    throw "Environment file not found: $environmentFilePath"
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

$schemaToolsProject = Join-Path $repoRoot "src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj"
$schemaFiles = @(Resolve-SchemaFilesFromEnvironmentFile `
        -EnvironmentFilePath $environmentFilePath `
        -Configuration $Configuration `
        -RepoRoot $repoRoot `
        -SchemaDirectory $schemaDirectory)

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
