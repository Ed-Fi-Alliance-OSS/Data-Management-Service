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

function ConvertFrom-ComposeEnvironmentValue {
    param(
        [AllowEmptyString()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $Value
    }

    $trimmedValue = $Value.Trim()
    $firstCharacter = $trimmedValue[0]

    if ($firstCharacter -in @("'", '"')) {
        $closingQuoteIndex = -1
        $escaped = $false

        for ($index = 1; $index -lt $trimmedValue.Length; $index++) {
            $character = $trimmedValue[$index]

            if ($character -eq "\" -and -not $escaped) {
                $escaped = $true
                continue
            }

            if ($character -eq $firstCharacter -and -not $escaped) {
                $closingQuoteIndex = $index
                break
            }

            $escaped = $false
        }

        if ($closingQuoteIndex -gt 0) {
            $trailingContent = $trimmedValue.Substring($closingQuoteIndex + 1).Trim()
            if ([string]::IsNullOrEmpty($trailingContent) -or $trailingContent.StartsWith("#")) {
                $unquotedValue = $trimmedValue.Substring(1, $closingQuoteIndex - 1)
                if ($firstCharacter -eq "'") {
                    return $unquotedValue.Replace("\'", "'")
                }

                return $unquotedValue.Replace('\"', '"').Replace('\\', '\')
            }
        }
    }

    # Docker Compose treats a # preceded by whitespace as an inline comment for an unquoted
    # value. A # without leading whitespace remains part of the value.
    return ($trimmedValue -replace '[ \t]+#.*$', '').Trim()
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
        $value = ConvertFrom-ComposeEnvironmentValue -Value $line.Substring($separatorIndex + 1)
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

function Assert-E2EDatabaseIsDedicated {
    param(
        # Concrete, already-resolved protected database targets: records of @{ Source; DatabaseName }.
        # Every name is resolved UPSTREAM by the single authorities - Docker Compose for interpolation
        # (Get-ComposeResolvedConfiguration) and the api-schema-tools 'connection validate' verb for
        # connection-string parsing (Resolve-EffectiveConfigRuntimeContract / Get-CmsConnectionStringDatabaseName).
        # This guard never reads or expands a ${...} expression itself; there is no second interpolation model.
        [object[]]$ProtectedDatabaseTarget,
        [string]$EnvironmentFilePath,
        [string]$E2EDatabaseName
    )

    Assert-SafeDatabaseName -DatabaseName $E2EDatabaseName

    # Fail closed: a protected target that did not resolve to a concrete name aborts the reset rather than
    # silently skipping a comparison in front of a destructive DROP DATABASE.
    foreach ($target in $ProtectedDatabaseTarget) {
        if ([string]::IsNullOrWhiteSpace([string]$target.DatabaseName)) {
            throw "E2E database safety check could not resolve a concrete database name for the $($target.Source) in '$EnvironmentFilePath'; refusing to reset '$E2EDatabaseName'."
        }
    }

    # Case-insensitive on purpose: SQL Server identifiers are case-insensitive, so a case variant IS the same
    # database there and would still be dropped. PostgreSQL names are case-sensitive, so this is stricter than
    # required on that engine - the deliberately conservative choice in front of DROP DATABASE, where a false
    # positive costs a rename and a false negative drops shared state.
    foreach ($target in $ProtectedDatabaseTarget) {
        if ($E2EDatabaseName -ieq [string]$target.DatabaseName) {
            throw "E2E database '$E2EDatabaseName' in '$EnvironmentFilePath' must be dedicated and cannot match the $($target.Source) ('$([string]$target.DatabaseName)')."
        }
    }
}

function Wait-ForPostgresql {
    param(
        [string]$ContainerName,
        [string]$PostgresUsername,
        [int]$TimeoutSeconds = 150
    )

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    $attempt = 0
    while ([datetime]::UtcNow -lt $deadline) {
        $attempt++
        $remainingSeconds = [math]::Max(1, [math]::Ceiling(($deadline - [datetime]::UtcNow).TotalSeconds))
        $probeArguments = @("exec", $ContainerName, "psql", "-U", $PostgresUsername, "-d", "postgres", "-c", "SELECT 1;")

        if (Test-NativeCommandWithTimeout -FilePath "docker" -ArgumentList $probeArguments -TimeoutSeconds ([math]::Min(10, $remainingSeconds))) {
            Write-Information "PostgreSQL container is ready: $ContainerName" -InformationAction Continue
            return
        }

        if ($attempt -eq 1 -or $attempt % 10 -eq 0) {
            Write-Information "Waiting for PostgreSQL container '$ContainerName' to become ready..." -InformationAction Continue
            $null = Test-NativeCommandWithTimeout -FilePath "docker" -ArgumentList @("ps", "-a", "--filter", "name=^/${ContainerName}$", "--format", "table {{.Names}}\t{{.Status}}\t{{.Ports}}") -TimeoutSeconds ([math]::Min(10, $remainingSeconds))
            $null = Test-NativeCommandWithTimeout -FilePath "docker" -ArgumentList @("logs", "--tail", "10", $ContainerName) -TimeoutSeconds ([math]::Min(10, $remainingSeconds))
        }

        if ([datetime]::UtcNow -lt $deadline) {
            Start-Sleep -Seconds ([math]::Min(5, [math]::Max(1, [math]::Floor(($deadline - [datetime]::UtcNow).TotalSeconds))))
        }
    }

    throw "PostgreSQL container '$ContainerName' did not become ready within $TimeoutSeconds seconds."
}

function Wait-ForMssql {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The SA password is read as plaintext from the environment file and handed to sqlcmd via the SQLCMDPASSWORD environment variable on docker exec (still visible in host-side docker argv); SecureString adds no protection across that boundary.')]
    param(
        [string]$ContainerName,
        [string]$SaPassword,
        [int]$TimeoutSeconds = 120
    )

    # SQL Server can take 30+ seconds to accept connections on a cold start. Poll sqlcmd the
    # same way start-local-dms.ps1's Wait-MssqlReady does so the reset/provision steps that
    # follow always find a reachable server.
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        $remainingSeconds = [math]::Max(1, [math]::Ceiling(($deadline - [datetime]::UtcNow).TotalSeconds))
        $probeArguments = @(
            "exec", "-e", "SQLCMDPASSWORD=$SaPassword", $ContainerName,
            "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa",
            "-Q", "SELECT 1", "-C", "-b"
        )
        if (Test-NativeCommandWithTimeout -FilePath "docker" -ArgumentList $probeArguments -TimeoutSeconds ([math]::Min(10, $remainingSeconds))) {
            Write-Information "SQL Server container is ready: $(Format-LogSafeText $ContainerName)" -InformationAction Continue
            return
        }

        if ([datetime]::UtcNow -lt $deadline) {
            Start-Sleep -Seconds ([math]::Min(3, [math]::Max(1, [math]::Floor(($deadline - [datetime]::UtcNow).TotalSeconds))))
        }
    }

    throw "SQL Server container '$(Format-LogSafeText $ContainerName)' did not become ready within $TimeoutSeconds seconds."
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

# Resolve the concrete protected database targets through the SINGLE authorities the stack itself uses -
# Docker Compose for interpolation and the api-schema-tools 'connection validate' verb for connection-string
# parsing - never a second PowerShell interpolation/parsing model. The safety-relevant Compose fields (db
# datastore key, config DatabaseSettings__DatabaseConnection, dms DATABASE_CONNECTION_STRING_ADMIN) are
# byte-identical between the local and published DMS/config compose files (locked by the parity test in
# RuntimeConfigContract.Tests.ps1), so this local compose set renders the same protected names the E2E stack
# runs under, regardless of image lane, without threading the caller's file selection through.
Import-Module (Join-Path $PSScriptRoot "bootstrap-schema-tool.psm1") -Force
$safetyDatabaseComposeFile = if ($DatabaseEngine -eq "mssql") { "mssql.yml" } else { "postgresql.yml" }
$safetyComposeFiles = @("-f", $safetyDatabaseComposeFile, "-f", "local-dms.yml", "-f", "local-config.yml")
$resolvedCompose = Get-ComposeResolvedConfiguration `
    -ComposeFiles $safetyComposeFiles `
    -EnvironmentFile $environmentFilePath `
    -ProjectName "dms-e2e-safety" `
    -InfrastructureEngine $DatabaseEngine
$connectionValidator = Resolve-DmsConnectionValidator -RequestedPath $env:DMS_SCHEMA_TOOL_PATH -DmsImage $resolvedCompose.DmsImage
$safetyContract = Resolve-EffectiveConfigRuntimeContract `
    -InfrastructureEngine $DatabaseEngine `
    -ConfigServiceIncluded $true `
    -DmsServiceIncluded $true `
    -ResolvedConfigProvider $resolvedCompose.ConfigProvider `
    -ResolvedDmsProvider $resolvedCompose.DmsProvider `
    -ResolvedCmsConnectionString $resolvedCompose.CmsConnectionString `
    -SchemaToolPath $connectionValidator `
    -ResolvedMssqlSaPassword $resolvedCompose.MssqlSaPassword `
    -ResolvedTopologyDatastoreDatabaseName $resolvedCompose.TopologyDatastoreDatabaseName

# The DMS admin/readiness connection is parsed by the provider verb; require EXACTLY one concrete target so a
# zero- or multi-target parse fails before any reset rather than under-protecting the destructive DROP.
$adminDatabaseTargets = @(Get-CmsConnectionStringDatabaseName `
        -Engine $DatabaseEngine `
        -ConnectionString $resolvedCompose.DmsAdminConnectionString `
        -SchemaToolPath $connectionValidator)
if ($adminDatabaseTargets.Count -ne 1) {
    throw "E2E database safety check expected exactly one provider-parsed DMS admin database target from DATABASE_CONNECTION_STRING_ADMIN, but found $($adminDatabaseTargets.Count). Refusing to reset '$e2eDatabaseName'."
}

$protectedDatabaseTargets = @(
    @{ Source = "topology datastore anchor"; DatabaseName = $safetyContract.TopologyDatastoreDatabaseName }
    @{ Source = "CMS persistence target"; DatabaseName = $safetyContract.CmsDatabaseName }
    @{ Source = "DMS admin/readiness target"; DatabaseName = $adminDatabaseTargets[0] }
)

Assert-E2EDatabaseIsDedicated `
    -ProtectedDatabaseTarget $protectedDatabaseTargets `
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
