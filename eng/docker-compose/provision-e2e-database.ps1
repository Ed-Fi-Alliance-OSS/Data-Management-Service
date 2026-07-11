# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Provisions a dedicated E2E database via SchemaTools DDL provisioning. Supports PostgreSQL
    and SQL Server data stores.
.DESCRIPTION
    Resets (drops if present, then recreates) the E2E database named by -DatabaseName or the
    environment file's E2E_DATABASE_NAME, then runs SchemaTools ddl provision against it. The
    dialect (--dialect pgsql|mssql) and connection shape follow -DatabaseEngine; the SQL Server
    branch mirrors the readiness-wait, host-side target translation, and dialect dispatch
    patterns used by provision-dms-schema.ps1.
#>

[CmdletBinding()]
param(
    [string]$EnvironmentFile = "./.env.e2e",

    [string]$DatabaseName,

    [string]
    [ValidateSet("Debug", "Release")]
    $Configuration = "Release",

    [string]$PostgresContainerName = "dms-postgresql",

    # Database engine overlay selector: composes the .env.mssql overlay onto -EnvironmentFile
    # (Resolve-DatabaseEngineEnvironmentFile) so the reset and SchemaTools steps below target
    # the same engine the caller intends. The default "postgresql" is a no-op via that helper's
    # idempotency guard, so the PostgreSQL invocation is unaffected when this parameter is
    # omitted.
    [ValidateSet("postgresql", "mssql")]
    [string]$DatabaseEngine = "postgresql"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$script:ResolvedSchemaDirectory = $null

Import-Module (Join-Path $PSScriptRoot "../schema-package-utility.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "env-utility.psm1") -Force

if (-not (Get-Command Format-LogSafeText -ErrorAction SilentlyContinue)) {
    function Format-LogSafeText {
        param($Value)

        if ($null -eq $Value) { return "" }
        $text = [string]$Value
        $builder = [System.Text.StringBuilder]::new()
        foreach ($character in $text.ToCharArray()) {
            # Comma is whitelisted so SQL Server "host,port" targets log readably; newlines
            # and other control characters stay stripped, which is what prevents log forging.
            if ([char]::IsLetterOrDigit($character) -or
                $character -eq " " -or
                $character -eq "_" -or
                $character -eq "-" -or
                $character -eq "." -or
                $character -eq ":" -or
                $character -eq "," -or
                $character -eq "/") {
                $null = $builder.Append($character)
            }
        }

        return $builder.ToString()
    }
}

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

function Get-EnvironmentValueMap {
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
        throw "Database name '$DatabaseName' is a reserved PostgreSQL system database and cannot be used for E2E provisioning."
    }

    if ($DatabaseName -iin @("master", "model", "msdb", "tempdb")) {
        throw "Database name '$DatabaseName' is a reserved SQL Server system database and cannot be used for E2E provisioning."
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
        # "Initial Catalog" is the SQL Server alias for "Database" - this script's own
        # Build-ConnectionString emits it for the mssql dialect - so both keys must be
        # recognized or an Initial Catalog-form string bypasses the dedicated-database guard.
        $match = [Regex]::Match($segment, '^(?i)\s*(?:database|initial\s+catalog)\s*=\s*(?<value>.+?)\s*$')

        if ($match.Success) {
            return Resolve-EnvironmentValueReference `
                -Value $match.Groups["value"].Value.Trim() `
                -EnvironmentValues $EnvironmentValues
        }
    }

    return $null
}

function Assert-E2EDatabaseIsDedicated {
    param(
        [hashtable]$EnvironmentValues,
        [string]$EnvironmentFilePath,
        [string]$E2EDatabaseName
    )

    Assert-SafeDatabaseName -DatabaseName $E2EDatabaseName

    # All comparisons are case-insensitive: SQL Server's default collation treats database
    # identifiers case-insensitively, so a case-variant of a protected name IS the same database
    # there and would still be dropped. PostgreSQL names are case-sensitive, so this is stricter
    # than required on that engine - acceptable for a guard in front of DROP DATABASE, where a
    # false positive costs a rename and a false negative drops shared state.
    foreach ($databaseNameKey in @("POSTGRES_DB_NAME", "MSSQL_DB_NAME")) {
        $protectedDatabaseName = Resolve-EnvironmentValueReference `
            -Value ([string]$EnvironmentValues[$databaseNameKey]) `
            -EnvironmentValues $EnvironmentValues

        if (-not [string]::IsNullOrWhiteSpace($protectedDatabaseName) -and $E2EDatabaseName -ieq $protectedDatabaseName) {
            throw "E2E database '$E2EDatabaseName' in '$EnvironmentFilePath' must be dedicated and cannot match $databaseNameKey."
        }
    }

    foreach ($connectionStringKey in @(
            "DATABASE_CONNECTION_STRING_ADMIN",
            "DMS_CONFIG_DATABASE_CONNECTION_STRING"
        )) {
        $connectionStringDatabaseName = Get-DatabaseNameFromConnectionString `
            -ConnectionString ([string]$EnvironmentValues[$connectionStringKey]) `
            -EnvironmentValues $EnvironmentValues

        if (-not [string]::IsNullOrWhiteSpace($connectionStringDatabaseName) -and $E2EDatabaseName -ieq $connectionStringDatabaseName) {
            throw "E2E database '$E2EDatabaseName' in '$EnvironmentFilePath' must stay separate from $connectionStringKey."
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
                Write-Information "PostgreSQL container is ready: $ContainerName" -InformationAction Continue
                return
            }
        }

        if ($attempt -eq 1 -or $attempt % 10 -eq 0) {
            Write-Information "Waiting for PostgreSQL container '$ContainerName' to become ready..." -InformationAction Continue
            docker ps -a --filter "name=^/${ContainerName}$" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
            docker logs --tail 10 $ContainerName 2>$null
        }

        if ($attempt -lt $MaxAttempts) {
            Start-Sleep -Seconds 5
        }
    }

    throw "PostgreSQL container '$ContainerName' did not become ready after $MaxAttempts attempts."
}

function Wait-ForMssql {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The SA password is read as plaintext from the environment file and handed to sqlcmd via the SQLCMDPASSWORD environment variable on docker exec (still visible in host-side docker argv); SecureString adds no protection across that boundary.')]
    param(
        [string]$ContainerName,
        [string]$SaPassword,
        [int]$MaxAttempts = 40
    )

    # SQL Server can take 30+ seconds to accept connections on a cold start. Poll sqlcmd the
    # same way start-local-dms.ps1's Wait-MssqlReady does so the reset/provision steps that
    # follow always find a reachable server.
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        docker exec -e "SQLCMDPASSWORD=$SaPassword" $ContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -Q "SELECT 1" -C -b *> $null

        if ($LASTEXITCODE -eq 0) {
            Write-Information "SQL Server container is ready: $(Format-LogSafeText $ContainerName)" -InformationAction Continue
            return
        }

        if ($attempt -lt $MaxAttempts) {
            Start-Sleep -Seconds 3
        }
    }

    throw "SQL Server container '$(Format-LogSafeText $ContainerName)' did not become ready after $MaxAttempts attempts."
}

function Reset-E2EDatabase {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [string]$ContainerName,
        [string]$DatabaseName,
        [string]$PostgresUsername
    )

    Assert-SafeDatabaseName -DatabaseName $DatabaseName

    if (-not $PSCmdlet.ShouldProcess($DatabaseName, "Reset E2E PostgreSQL database")) {
        return
    }

    $terminateConnectionsSql =
        "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$DatabaseName' AND pid <> pg_backend_pid();"

    & docker exec $ContainerName psql -U $PostgresUsername -d postgres -v ON_ERROR_STOP=1 -c $terminateConnectionsSql

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to terminate active connections for E2E database '$DatabaseName'."
    }

    & docker exec $ContainerName dropdb -U $PostgresUsername --if-exists $DatabaseName

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to drop E2E database '$DatabaseName'."
    }
}

function Reset-E2EMssqlDatabase {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The SA password is read as plaintext from the environment file and handed to sqlcmd via the SQLCMDPASSWORD environment variable on docker exec (still visible in host-side docker argv); SecureString adds no protection across that boundary.')]
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [string]$ContainerName,
        [string]$DatabaseName,
        [string]$SaPassword
    )

    Assert-SafeDatabaseName -DatabaseName $DatabaseName

    if (-not $PSCmdlet.ShouldProcess($DatabaseName, "Reset E2E SQL Server database")) {
        return
    }

    # SQL Server has no equivalent of pg_terminate_backend + dropdb --if-exists: dropping active
    # connections and reclaiming the database is instead a two-statement ALTER DATABASE / DROP
    # DATABASE sequence, each guarded so a database that does not yet exist is a no-op.
    $setSingleUserSql =
        "IF DB_ID(N'$DatabaseName') IS NOT NULL ALTER DATABASE [$DatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;"

    & docker exec -e "SQLCMDPASSWORD=$SaPassword" $ContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -Q $setSingleUserSql -C -b

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to terminate active connections for E2E database '$DatabaseName'."
    }

    $dropDatabaseSql = "DROP DATABASE IF EXISTS [$DatabaseName];"

    & docker exec -e "SQLCMDPASSWORD=$SaPassword" $ContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -Q $dropDatabaseSql -C -b

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to drop E2E database '$DatabaseName'."
    }
}

function Build-ConnectionString {
    param(
        [string]$ServerHost,
        [string]$Port,
        [System.Management.Automation.PSCredential]$Credential,
        [string]$DatabaseName,

        [ValidateSet("pgsql", "mssql")]
        [string]$Dialect = "pgsql"
    )

    if ($Dialect -eq "mssql") {
        return "Server=$ServerHost,$Port;Initial Catalog=$DatabaseName;User ID=$($Credential.UserName);Password=$($Credential.GetNetworkCredential().Password);TrustServerCertificate=true;"
    }

    return "Host=$ServerHost;Port=$Port;Username=$($Credential.UserName);Password=$($Credential.GetNetworkCredential().Password);Database=$DatabaseName;NoResetOnClose=true;"
}

# Dot-sourcing stops here so tests can exercise the functions above without provisioning
# anything (same pattern as load-dms-seed-data.ps1).
if ($MyInvocation.InvocationName -eq '.') { return }

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
$environmentFilePath = Resolve-ScriptRelativePath $EnvironmentFile
$environmentFilePath = Resolve-DatabaseEngineEnvironmentFile `
    -DatabaseEngine $DatabaseEngine `
    -BaseEnvironmentFile $environmentFilePath `
    -DockerComposeRoot $PSScriptRoot

$environmentValues = Get-EnvironmentValueMap $environmentFilePath
# Fixed container name for the single-engine MSSQL stack (mirrors the "dms-mssql" literal used
# by start-local-dms.ps1's Wait-MssqlReady call site); -PostgresContainerName stays parameterized
# for backward compatibility with existing callers.
$mssqlContainerName = "dms-mssql"

if ($DatabaseEngine -eq "mssql") {
    $mssqlPort = Get-RequiredEnvValue -EnvironmentValues $environmentValues -Key "MSSQL_PORT"
    $mssqlSaPassword = Get-RequiredEnvValue -EnvironmentValues $environmentValues -Key "MSSQL_SA_PASSWORD"
}
else {
    $postgresPort = Get-RequiredEnvValue -EnvironmentValues $environmentValues -Key "POSTGRES_PORT"
    $postgresPassword = Get-RequiredEnvValue -EnvironmentValues $environmentValues -Key "POSTGRES_PASSWORD"
}

$e2eDatabaseName =
    if ([string]::IsNullOrWhiteSpace($DatabaseName)) {
        Get-RequiredEnvValue -EnvironmentValues $environmentValues -Key "E2E_DATABASE_NAME"
    }
    else {
        $DatabaseName
    }

if ($DatabaseEngine -eq "mssql") {
    $secureMssqlSaPassword = New-Object System.Security.SecureString
    foreach ($character in $mssqlSaPassword.ToCharArray()) {
        $secureMssqlSaPassword.AppendChar($character)
    }
    $secureMssqlSaPassword.MakeReadOnly()
    $mssqlCredential = [System.Management.Automation.PSCredential]::new(
        "sa",
        $secureMssqlSaPassword
    )
}
else {
    $postgresUsername =
        if ([string]::IsNullOrWhiteSpace([string]$environmentValues["POSTGRES_USER"])) {
            "postgres"
        }
        else {
            [string]$environmentValues["POSTGRES_USER"]
        }
    $securePostgresPassword = New-Object System.Security.SecureString
    foreach ($character in $postgresPassword.ToCharArray()) {
        $securePostgresPassword.AppendChar($character)
    }
    $securePostgresPassword.MakeReadOnly()
    $postgresCredential = [System.Management.Automation.PSCredential]::new(
        $postgresUsername,
        $securePostgresPassword
    )
}

Write-Information "Provisioning E2E database" -InformationAction Continue
Write-Information "Environment file: $environmentFilePath" -InformationAction Continue
if ($DatabaseEngine -eq "mssql") {
    Write-Information "SQL Server container: $(Format-LogSafeText $mssqlContainerName)" -InformationAction Continue
}
else {
    Write-Information "PostgreSQL container: $PostgresContainerName" -InformationAction Continue
}
Write-Information "E2E database: $e2eDatabaseName" -InformationAction Continue
Write-Information "Configuration: $Configuration" -InformationAction Continue

Assert-E2EDatabaseIsDedicated `
    -EnvironmentValues $environmentValues `
    -EnvironmentFilePath $environmentFilePath `
    -E2EDatabaseName $e2eDatabaseName

if ($DatabaseEngine -eq "mssql") {
    Wait-ForMssql -ContainerName $mssqlContainerName -SaPassword $mssqlSaPassword
}
else {
    Wait-ForPostgresql -ContainerName $PostgresContainerName -PostgresUsername $postgresUsername
}

$script:ResolvedSchemaDirectory =
    Join-Path ([System.IO.Path]::GetTempPath()) "dms-e2e-schema-$([Guid]::NewGuid().ToString('N'))"
$schemaFiles = @(Resolve-SchemaFilesFromEnvironmentFile `
        -EnvironmentFilePath $environmentFilePath `
        -Configuration $Configuration `
        -RepoRoot $repoRoot `
        -SchemaDirectory $script:ResolvedSchemaDirectory `
        -DownloaderDotnetRunArgs @("--no-launch-profile"))

try {
    Write-Information "Dropping E2E database if it exists: $e2eDatabaseName" -InformationAction Continue

    $schemaToolsProject = Join-Path $repoRoot "src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj"

    if ($DatabaseEngine -eq "mssql") {
        Reset-E2EMssqlDatabase `
            -ContainerName $mssqlContainerName `
            -DatabaseName $e2eDatabaseName `
            -SaPassword $mssqlSaPassword

        $connectionString = Build-ConnectionString `
            -ServerHost "127.0.0.1" `
            -Port $mssqlPort `
            -Credential $mssqlCredential `
            -DatabaseName $e2eDatabaseName `
            -Dialect "mssql"

        $schemaToolsDialect = "mssql"
    }
    else {
        Reset-E2EDatabase `
            -ContainerName $PostgresContainerName `
            -DatabaseName $e2eDatabaseName `
            -PostgresUsername $postgresUsername

        $connectionString = Build-ConnectionString `
            -ServerHost "127.0.0.1" `
            -Port $postgresPort `
            -Credential $postgresCredential `
            -DatabaseName $e2eDatabaseName

        $schemaToolsDialect = "pgsql"
    }

    Write-Information "Running SchemaTools ddl provision for $e2eDatabaseName" -InformationAction Continue

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
        $schemaToolsDialect,
        "--create-database"
    )

    & dotnet @provisionArgs

    if ($LASTEXITCODE -ne 0) {
        throw "SchemaTools provisioning failed for database '$e2eDatabaseName'."
    }

    Write-Information "E2E database provisioned successfully: $e2eDatabaseName" -InformationAction Continue
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($script:ResolvedSchemaDirectory) -and (Test-Path $script:ResolvedSchemaDirectory)) {
        Remove-Item $script:ResolvedSchemaDirectory -Recurse -Force
    }
}
