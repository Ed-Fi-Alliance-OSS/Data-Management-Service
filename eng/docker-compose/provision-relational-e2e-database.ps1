# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param(
    [string]$EnvironmentFile = "./.env.e2e.relational",

    [string]
    [ValidateSet("Debug", "Release")]
    $Configuration = "Release",

    [string]$PostgresContainerName = "dms-postgresql"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$script:ResolvedSchemaDirectory = $null

Import-Module (Join-Path $PSScriptRoot "../schema-package-utility.psm1") -Force

function Resolve-ScriptRelativePath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    if (Test-Path $Path) {
        return [System.IO.Path]::GetFullPath([string](Resolve-Path $Path))
    }

    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Path))
}

function Read-EnvironmentValues {
    param([string]$EnvironmentFilePath)

    $environmentValues = @{}

    foreach ($line in Get-Content $EnvironmentFilePath) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line -match "^\s*#") {
            continue
        }

        $separatorIndex = $line.IndexOf("=")

        if ($separatorIndex -lt 0) {
            continue
        }

        $key = $line.Substring(0, $separatorIndex).Trim()
        $value = $line.Substring($separatorIndex + 1).Trim()
        $environmentValues[$key] = $value
    }

    return $environmentValues
}

function Get-RequiredEnvValue {
    param(
        [hashtable]$EnvironmentValues,
        [string]$Key
    )

    $value = [string]$EnvironmentValues[$Key]

    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Required environment value '$Key' was not found in the environment file."
    }

    return $value
}

function Assert-SafeDatabaseName {
    param([string]$DatabaseName)

    if ($DatabaseName -notmatch "^[A-Za-z0-9_]+$") {
        throw "Database name '$DatabaseName' contains unsupported characters."
    }

    if ($DatabaseName -iin @("postgres", "template0", "template1")) {
        throw "Database name '$DatabaseName' is a reserved PostgreSQL system database and cannot be used for relational E2E provisioning."
    }
}

function Resolve-EnvironmentValueReference {
    param(
        [string]$Value,
        [hashtable]$EnvironmentValues
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $Value
    }

    $match = [Regex]::Match($Value, '^\$\{(?<key>[^}]+)\}$')

    if (-not $match.Success) {
        return $Value
    }

    $resolvedValue = [string]$EnvironmentValues[$match.Groups["key"].Value]

    if ([string]::IsNullOrWhiteSpace($resolvedValue)) {
        return $Value
    }

    return $resolvedValue
}

function Get-DatabaseNameFromConnectionString {
    param(
        [string]$ConnectionString,
        [hashtable]$EnvironmentValues
    )

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        return $null
    }

    foreach ($segment in ($ConnectionString -split ";")) {
        $match = [Regex]::Match($segment, '^(?i)\s*database\s*=\s*(?<value>.+?)\s*$')

        if ($match.Success) {
            return Resolve-EnvironmentValueReference `
                -Value $match.Groups["value"].Value.Trim() `
                -EnvironmentValues $EnvironmentValues
        }
    }

    return $null
}

function Assert-RelationalDatabaseIsDedicated {
    param(
        [hashtable]$EnvironmentValues,
        [string]$EnvironmentFilePath,
        [string]$RelationalDatabaseName
    )

    Assert-SafeDatabaseName -DatabaseName $RelationalDatabaseName

    $bootstrapDatabaseName = Resolve-EnvironmentValueReference `
        -Value ([string]$EnvironmentValues["POSTGRES_DB_NAME"]) `
        -EnvironmentValues $EnvironmentValues

    if (-not [string]::IsNullOrWhiteSpace($bootstrapDatabaseName) -and $RelationalDatabaseName -ceq $bootstrapDatabaseName) {
        throw "Relational E2E database '$RelationalDatabaseName' in '$EnvironmentFilePath' must be dedicated and cannot match POSTGRES_DB_NAME."
    }

    foreach ($connectionStringKey in @(
            "DATABASE_CONNECTION_STRING",
            "DATABASE_CONNECTION_STRING_ADMIN",
            "DMS_CONFIG_DATABASE_CONNECTION_STRING"
        )) {
        $connectionStringDatabaseName = Get-DatabaseNameFromConnectionString `
            -ConnectionString ([string]$EnvironmentValues[$connectionStringKey]) `
            -EnvironmentValues $EnvironmentValues

        if (-not [string]::IsNullOrWhiteSpace($connectionStringDatabaseName) -and $RelationalDatabaseName -ceq $connectionStringDatabaseName) {
            throw "Relational E2E database '$RelationalDatabaseName' in '$EnvironmentFilePath' must stay separate from $connectionStringKey."
        }
    }
}

function Wait-ForPostgresql {
    param(
        [string]$ContainerName,
        [string]$PostgresUsername,
        [int]$MaxAttempts = 30
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $containerStatus = docker ps --filter "name=^/${ContainerName}$" --format "{{.Status}}"

        if (-not [string]::IsNullOrWhiteSpace($containerStatus)) {
            docker exec $ContainerName psql -U $PostgresUsername -d postgres -c "SELECT 1;" 2>$null | Out-Null

            if ($LASTEXITCODE -eq 0) {
                Write-Host "PostgreSQL container is ready: $ContainerName" -ForegroundColor Green
                return
            }
        }

        if ($attempt -eq 1 -or $attempt % 10 -eq 0) {
            Write-Host "Waiting for PostgreSQL container '$ContainerName' to become ready..." -ForegroundColor Yellow
            docker ps -a --filter "name=^/${ContainerName}$" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
            docker logs --tail 10 $ContainerName 2>$null
        }

        if ($attempt -lt $MaxAttempts) {
            Start-Sleep -Seconds 5
        }
    }

    throw "PostgreSQL container '$ContainerName' did not become ready after $MaxAttempts attempts."
}

function Reset-RelationalDatabase {
    param(
        [string]$ContainerName,
        [string]$DatabaseName,
        [string]$PostgresUsername
    )

    Assert-SafeDatabaseName -DatabaseName $DatabaseName

    $terminateConnectionsSql =
        "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$DatabaseName' AND pid <> pg_backend_pid();"

    & docker exec $ContainerName psql -U $PostgresUsername -d postgres -v ON_ERROR_STOP=1 -c $terminateConnectionsSql

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to terminate active connections for relational database '$DatabaseName'."
    }

    & docker exec $ContainerName dropdb -U $PostgresUsername --if-exists $DatabaseName

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to drop relational database '$DatabaseName'."
    }
}

function Build-ConnectionString {
    param(
        [string]$ServerHost,
        [string]$Port,
        [string]$Username,
        [string]$Password,
        [string]$DatabaseName
    )

    return "Host=$ServerHost;Port=$Port;Username=$Username;Password=$Password;Database=$DatabaseName;NoResetOnClose=true;"
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$environmentFilePath = Resolve-ScriptRelativePath $EnvironmentFile

$environmentValues = Read-EnvironmentValues $environmentFilePath
$postgresPort = Get-RequiredEnvValue -EnvironmentValues $environmentValues -Key "POSTGRES_PORT"
$postgresPassword = Get-RequiredEnvValue -EnvironmentValues $environmentValues -Key "POSTGRES_PASSWORD"
$relationalDatabaseName = Get-RequiredEnvValue -EnvironmentValues $environmentValues -Key "RELATIONAL_E2E_DATABASE_NAME"
$postgresUsername =
    if ([string]::IsNullOrWhiteSpace([string]$environmentValues["POSTGRES_USER"])) {
        "postgres"
    }
    else {
        [string]$environmentValues["POSTGRES_USER"]
    }

Write-Host "Provisioning relational E2E database" -ForegroundColor Cyan
Write-Host "Environment file: $environmentFilePath"
Write-Host "PostgreSQL container: $PostgresContainerName"
Write-Host "Relational database: $relationalDatabaseName"
Write-Host "Configuration: $Configuration"

Assert-RelationalDatabaseIsDedicated `
    -EnvironmentValues $environmentValues `
    -EnvironmentFilePath $environmentFilePath `
    -RelationalDatabaseName $relationalDatabaseName

Wait-ForPostgresql -ContainerName $PostgresContainerName -PostgresUsername $postgresUsername

$script:ResolvedSchemaDirectory =
    Join-Path ([System.IO.Path]::GetTempPath()) "dms-relational-e2e-schema-$([Guid]::NewGuid().ToString('N'))"
$schemaFiles = @(Resolve-SchemaFilesFromEnvironmentFile `
        -EnvironmentFilePath $environmentFilePath `
        -Configuration $Configuration `
        -RepoRoot $repoRoot `
        -SchemaDirectory $script:ResolvedSchemaDirectory `
        -DownloaderDotnetRunArgs @("--no-launch-profile"))

try {
    Write-Host "Dropping relational database if it exists: $relationalDatabaseName" -ForegroundColor Yellow
    Reset-RelationalDatabase `
        -ContainerName $PostgresContainerName `
        -DatabaseName $relationalDatabaseName `
        -PostgresUsername $postgresUsername

    $schemaToolsProject = Join-Path $repoRoot "src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj"
    $connectionString = Build-ConnectionString `
        -ServerHost "127.0.0.1" `
        -Port $postgresPort `
        -Username $postgresUsername `
        -Password $postgresPassword `
        -DatabaseName $relationalDatabaseName

    Write-Host "Running SchemaTools ddl provision for $relationalDatabaseName" -ForegroundColor Cyan

    $provisionArgs = @(
        "run",
        "--no-launch-profile",
        "--configuration",
        $Configuration,
        "--project",
        $schemaToolsProject,
        "--",
        "ddl",
        "provision",
        "--schema"
    ) + $schemaFiles + @(
        "--connection-string",
        $connectionString,
        "--dialect",
        "pgsql",
        "--create-database"
    )

    & dotnet @provisionArgs

    if ($LASTEXITCODE -ne 0) {
        throw "SchemaTools provisioning failed for database '$relationalDatabaseName'."
    }

    Write-Host "Relational E2E database provisioned successfully: $relationalDatabaseName" -ForegroundColor Green
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($script:ResolvedSchemaDirectory) -and (Test-Path $script:ResolvedSchemaDirectory)) {
        Remove-Item $script:ResolvedSchemaDirectory -Recurse -Force
    }
}
