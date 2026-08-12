# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Sets up the Ed-Fi DMS local Docker environment for Instance Management E2E testing.
.DESCRIPTION
    Owns the engine-neutral setup order for the Instance Management route-context stack:
      1. start-local-dms.ps1 -InfraOnly (Config Service, selected engine, resolved environment);
      2. provision all three resolved route-context databases once with generated engine-correct DDL;
      3. verify each database contains the dms.EffectiveSchema singleton and required tables using an
         engine-dispatched helper (PostgreSQL psql/to_regclass, MSSQL sqlcmd/OBJECT_ID) - the
         non-selected provider command is never invoked;
      4. start-local-dms.ps1 -DmsOnly and wait for DMS health.
    DMS is not started until all three schemas are provisioned and verified. Unhealthy infrastructure
    or failed verification fails the setup (never skips).

    Each Docker phase above runs inside the shared schema-settings guard from
    eng/docker-compose/dms-schema-environment.psm1 - individually, not as one sequence - so
    USE_API_SCHEMA_PATH, API_SCHEMA_PATH, and SCHEMA_PACKAGES are absent for every Compose call, the
    selected environment file is the sole authority for the schema package surface, and a phase that
    re-creates one of the three names in this process cannot leave it set for a later phase. The
    caller's original environment is restored exactly, including the absent, empty, whitespace, and
    valued distinctions, and on failure paths. That module explains why an ambient value would
    otherwise win over the environment file.

    After DMS starts, the container's schema settings are verified against the environment file with
    the shared Assert-DmsContainerSchemaEnvironment, the same settings-level check the direct DMS E2E
    wrapper runs, so a settings divergence is reported here rather than as opaque routed-request 503s
    across all three route contexts.

    Suite-owned fixture registration (tenants, vendor, data stores, route contexts, applications) and
    the single post-registration DMS restart are performed by build-dms.ps1 InstanceE2ETest, not here.
.PARAMETER SkipDockerBuild
    Skip rebuilding the local images (reuse the running/built images).
.PARAMETER DataStandardVersion
    Optional Ed-Fi Data Standard version (e.g. "5.2", "6.1") composed into the effective environment.
.PARAMETER DatabaseEngine
    Database engine backing the stack. "postgresql" (default) or "mssql".
.PARAMETER EnvironmentFile
    Base environment file, resolved against eng/docker-compose. Defaults to the route-context env
    file. Standalone runs resolve it once here (data-standard overlay then engine overlay). Its base
    path is used for the teardown guidance regardless of how the resolved file was obtained.
.PARAMETER ResolvedEnvironmentFile
    Fully composed environment file supplied by the build path (build-dms.ps1 InstanceE2ETest), which
    already resolved the data-standard and engine overlays exactly once in
    Get-InstanceE2ETestEnvironmentContext. When supplied it is used verbatim and this script performs
    no further overlay composition; when omitted, this script resolves -EnvironmentFile itself.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Setup script is intentionally host-oriented and uses console progress output.')]
[CmdletBinding()]
param(
    [switch]
    $SkipDockerBuild,

    [string]
    $DataStandardVersion,

    [ValidateSet("postgresql", "mssql")]
    [string]
    $DatabaseEngine = "postgresql",

    [string]
    $EnvironmentFile = "./.env.routeContext.e2e",

    [string]
    $ResolvedEnvironmentFile
)

function Assert-PostgresRouteContextSchema {
    <#
    .SYNOPSIS
    Verifies the dms.EffectiveSchema singleton and required relations exist in a PostgreSQL route
    database via psql/to_regclass. Throws when the query itself fails so a failed invocation is never
    read as "relation absent". The PostgreSQL role is the resolved POSTGRES_USER, not a hardcoded
    superuser, so a stack started with a non-default role still verifies.
    #>
    param([string]$Database, [string]$PostgresUser = "postgres")

    $effectiveSchemaRowCount = (
        docker exec dms-postgresql psql -U $PostgresUser -d $Database -tAc 'SELECT COUNT(*) FROM dms."EffectiveSchema" WHERE "EffectiveSchemaSingletonId" = 1;' |
            Out-String
    ).Trim()

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to query PostgreSQL database '$Database' for the dms.EffectiveSchema singleton row (psql exit code $LASTEXITCODE)."
    }

    if ($effectiveSchemaRowCount -ne "1") {
        throw "Schema verification failed: expected one dms.EffectiveSchema singleton row in test database '$Database' but found '$effectiveSchemaRowCount'."
    }

    $requiredRelations = @(
        '"dms"."EffectiveSchema"',
        '"dms"."Document"',
        '"edfi"."School"',
        '"edfi"."Student"'
    )

    foreach ($relation in $requiredRelations) {
        $regclass = (
            docker exec dms-postgresql psql -U $PostgresUser -d $Database -tAc "SELECT to_regclass('$relation');" |
                Out-String
        ).Trim()

        if ($LASTEXITCODE -ne 0) {
            throw "Failed to query PostgreSQL database '$Database' for relation '$relation' (psql exit code $LASTEXITCODE)."
        }

        if ([string]::IsNullOrWhiteSpace($regclass)) {
            throw "Schema verification failed: expected relational table '$relation' in PostgreSQL test database '$Database'."
        }
    }
}

function Assert-MssqlRouteContextSchema {
    <#
    .SYNOPSIS
    Verifies the dms.EffectiveSchema singleton and required tables exist in a SQL Server route database
    via sqlcmd/OBJECT_ID. Throws when the query itself fails.
    .DESCRIPTION
    sqlcmd runs INSIDE dms-mssql and reads its password from SQLCMDPASSWORD, exported in the container
    from the container-resident MSSQL_SA_PASSWORD (matching the compose healthcheck). The SA password
    is never placed on the host process argument list (no host password flag) and is never injected
    through the exec environment, so it cannot leak through host process arguments or the exec
    environment. -b makes sqlcmd exit non-zero on a SQL error so a failed query is never read as a
    schema result.
    #>
    param([string]$Database)

    # The database name and query travel as bash positional args ($1/$2); only the password stays
    # inside the container as an environment expansion, so it never appears in host args.
    $sqlcmdScript = 'export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"; exec /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -d "$1" -h -1 -W -b -Q "$2"'

    $effectiveSchemaRowCount = (
        docker exec dms-mssql bash -c $sqlcmdScript "sqlcmd-runner" $Database "SET NOCOUNT ON; SELECT COUNT(*) FROM dms.EffectiveSchema WHERE EffectiveSchemaSingletonId = 1;" |
            Out-String
    ).Trim()

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to query SQL Server database '$Database' for the dms.EffectiveSchema singleton row (sqlcmd exit code $LASTEXITCODE)."
    }

    if ($effectiveSchemaRowCount -ne "1") {
        throw "Schema verification failed: expected one dms.EffectiveSchema singleton row in test database '$Database' but found '$effectiveSchemaRowCount'."
    }

    $requiredTables = @(
        "[dms].[EffectiveSchema]",
        "[dms].[Document]",
        "[edfi].[School]",
        "[edfi].[Student]"
    )

    foreach ($table in $requiredTables) {
        $objectId = (
            docker exec dms-mssql bash -c $sqlcmdScript "sqlcmd-runner" $Database "SET NOCOUNT ON; SELECT OBJECT_ID('$table');" |
                Out-String
        ).Trim()

        if ($LASTEXITCODE -ne 0) {
            throw "Failed to query SQL Server database '$Database' for table '$table' (sqlcmd exit code $LASTEXITCODE)."
        }

        if ([string]::IsNullOrWhiteSpace($objectId) -or $objectId -eq "NULL") {
            throw "Schema verification failed: expected relational table '$table' in SQL Server test database '$Database'."
        }
    }
}

function Assert-RouteContextSchemaProvisioned {
    <#
    .SYNOPSIS
    Engine-dispatched schema verification. Only the selected provider's command is invoked.
    #>
    param(
        [string]$Database,
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,
        [string]$PostgresUser = "postgres"
    )

    if ($DatabaseEngine -eq "mssql") {
        Assert-MssqlRouteContextSchema -Database $Database
    }
    else {
        Assert-PostgresRouteContextSchema -Database $Database -PostgresUser $PostgresUser
    }
}

function Assert-RouteContextDatabaseNamesAreDedicated {
    <#
    .SYNOPSIS
    Validates every route-context database name up front via the shared database-safety guard, so no
    infrastructure is started and no database is provisioned until all three names are safe, not a
    reserved system database, and dedicated (never the primary or CMS database). Throwing on any name
    (including a later one) guarantees an earlier database is never provisioned before a bad name fails.
    #>
    param(
        [string[]]$DatabaseNames,
        [hashtable]$EnvironmentValues,
        [string]$EnvironmentFilePath
    )

    foreach ($databaseName in $DatabaseNames) {
        Assert-E2EDatabaseIsDedicated `
            -EnvironmentValues $EnvironmentValues `
            -EnvironmentFilePath $EnvironmentFilePath `
            -E2EDatabaseName $databaseName
    }
}

Write-Host @"
Ed-Fi DMS Local Environment Setup for Instance Management E2E Testing
======================================================================
"@ -ForegroundColor Cyan

# Check if Docker is running
Write-Host "Checking Docker status..." -ForegroundColor Yellow
$dockerCheck = $null
try {
    $dockerCheck = docker version 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Docker command failed"
    }
}
catch {
    Write-Host ""
    Write-Error "Docker is not running or not installed. Please start Docker and try again."
    Write-Host ""
    Write-Host "Error details:" -ForegroundColor Red
    if ($dockerCheck) {
        Write-Host $dockerCheck -ForegroundColor Red
    }
    else {
        Write-Host $_.Exception.Message -ForegroundColor Red
    }
    exit 1
}
Write-Host "Docker is running" -ForegroundColor Green
Write-Host ""

# Store current location and navigate to docker-compose directory
$originalLocation = Get-Location
$dockerComposeDir = Join-Path $PSScriptRoot "../../../../eng/docker-compose"

try {
    Set-Location $dockerComposeDir
    Import-Module ./env-utility.psm1 -Force
    Import-Module ./database-safety.psm1 -Force
    # The schema-settings guard and the post-start container schema verification, both shared with the
    # direct DMS E2E wrapper rather than copied into each.
    #
    # Without -Force, the same rule this module applies to its own nested imports: -Force removes a
    # module session-wide before re-importing it, while a plain import reuses an already-loaded
    # instance. build-dms.ps1 loads this same module for its own guarded call sites before invoking
    # THIS script in-process, so reusing that instance keeps one module serving both.
    Import-Module ./dms-schema-environment.psm1

    # Single environment resolution. The build path (build-dms.ps1 InstanceE2ETest) already composed
    # the data-standard and engine overlays exactly once in Get-InstanceE2ETestEnvironmentContext and
    # passes the result via -ResolvedEnvironmentFile; use it verbatim so setup never recomposes. A
    # standalone run composes here instead (data-standard overlay first, then the engine overlay). The
    # base env file is retained for the teardown guidance in both cases.
    if ([string]::IsNullOrWhiteSpace($ResolvedEnvironmentFile)) {
        $baseEnvironmentFile = Resolve-LocalSettingsEnvironmentFile -Path $EnvironmentFile -DockerComposeRoot $dockerComposeDir
        $resolvedEnvironmentFile = Resolve-DataStandardEnvironmentFile `
            -DataStandardVersion $DataStandardVersion `
            -BaseEnvironmentFile $baseEnvironmentFile `
            -DockerComposeRoot $dockerComposeDir
        $resolvedEnvironmentFile = Resolve-DatabaseEngineEnvironmentFile `
            -DatabaseEngine $DatabaseEngine `
            -BaseEnvironmentFile $resolvedEnvironmentFile `
            -DockerComposeRoot $dockerComposeDir
    }
    else {
        $baseEnvironmentFile = $EnvironmentFile
        $resolvedEnvironmentFile = $ResolvedEnvironmentFile
    }
    $envValues = ReadValuesFromEnvFile $resolvedEnvironmentFile

    # Read the three route-context database names from the resolved environment: require three
    # non-empty distinct names, and never fall back to a fixed name after resolution.
    $databases = @(
        (Get-EnvValue -EnvValues $envValues -Name "INSTANCE_E2E_DATABASE_1_NAME"),
        (Get-EnvValue -EnvValues $envValues -Name "INSTANCE_E2E_DATABASE_2_NAME"),
        (Get-EnvValue -EnvValues $envValues -Name "INSTANCE_E2E_DATABASE_3_NAME")
    )

    for ($i = 0; $i -lt $databases.Count; $i++) {
        if ([string]::IsNullOrWhiteSpace($databases[$i])) {
            throw "INSTANCE_E2E_DATABASE_$($i + 1)_NAME must be set in '$resolvedEnvironmentFile' for Instance Management E2E route-context provisioning."
        }
    }

    if (@($databases | Sort-Object -Unique).Count -ne 3) {
        throw "The three INSTANCE_E2E_DATABASE_*_NAME values must be distinct; got: $($databases -join ', ')."
    }

    # Validate ALL three names (safe characters, not a reserved system database, dedicated vs the
    # primary/CMS databases by name or embedded connection-string database) BEFORE starting any
    # infrastructure or provisioning any database. This runs on the standalone setup path too, so a
    # direct run can never provision database 1 and then reject an unsafe/reserved/protected database 2
    # after a mutation. provision-e2e-database.ps1 re-checks each name when it provisions (defense in
    # depth); this is the earliest gate on this path.
    Assert-RouteContextDatabaseNamesAreDedicated `
        -DatabaseNames $databases `
        -EnvironmentValues $envValues `
        -EnvironmentFilePath $resolvedEnvironmentFile

    $bootstrapDir = Join-Path $dockerComposeDir ".bootstrap"
    if (Test-Path -LiteralPath $bootstrapDir) {
        Write-Output "Removing stale .bootstrap workspace before file-based schema package E2E startup..."
        # Fail fast on cleanup errors: a stale manifest left here would trigger bootstrap mode on the
        # next start-local-dms.ps1 invocation and silently divert the E2E run.
        Remove-Item -LiteralPath $bootstrapDir -Recurse -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $bootstrapDir) {
            throw "Failed to remove stale .bootstrap workspace at '$bootstrapDir'. Resolve any file locks or permissions before re-running setup."
        }
    }

    Write-Host "Starting DMS environment with Instance Management E2E configuration..." -ForegroundColor Green
    Write-Host "Configuration:" -ForegroundColor Yellow
    Write-Host "  - Configuration Service: Enabled" -ForegroundColor Gray
    Write-Host "  - Database Engine: $DatabaseEngine" -ForegroundColor Gray
    Write-Host "  - Environment File: $resolvedEnvironmentFile" -ForegroundColor Gray
    Write-Host "  - Force Rebuild: $(if ($SkipDockerBuild) { "No" } else { "Yes" })" -ForegroundColor Gray
    Write-Host "  - Route Qualifiers: districtId, schoolYear" -ForegroundColor Cyan
    Write-Host "  - Identity Provider: self-contained" -ForegroundColor Gray
    Write-Output "  - Extension Security Metadata: Yes"
    Write-Host ""
    Write-Host "NOTE: Tenant, vendor, instance, and application records are created by the suite-owned fixture in build-dms.ps1" -ForegroundColor Yellow
    Write-Host ""

    Write-Output "Using file-based schema packages from $resolvedEnvironmentFile for E2E (non-bootstrap compatibility path)."

    # The resolved environment file carries the file-based ApiSchema package settings, and process env
    # values win over docker compose --env-file entries, so each phase below runs inside the shared
    # guard that removes USE_API_SCHEMA_PATH, API_SCHEMA_PATH, and SCHEMA_PACKAGES for its duration.
    #
    # Guarded per phase, not once around the sequence. The guard restores the caller's prior state when
    # the phase it wraps returns, so a single guard would remove the three names exactly once, before
    # the first phase - and a phase script runs in this same process, so one that re-creates any of
    # them (start-local-dms.ps1 does exactly that for bootstrap mode) would still be setting it for
    # every later phase.

    # 1. Start only infrastructure and the Configuration Service. DMS starts after all three
    #    route-context schemas are provisioned and verified.
    Write-Host "`nStarting infrastructure and Configuration Service (DMS not yet started)..." -ForegroundColor Cyan
    # The guard is inside each branch rather than around the if/else: exactly one of these runs, and
    # one guard per phase invocation keeps the shape uniform across the wrapper.
    if ($SkipDockerBuild) {
        Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
            ./start-local-dms.ps1 -InfraOnly -EnableConfig -EnvironmentFile $resolvedEnvironmentFile -DatabaseEngine $DatabaseEngine -IdentityProvider self-contained -AddExtensionSecurityMetadata
        }
    }
    else {
        Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
            ./start-local-dms.ps1 -InfraOnly -EnableConfig -EnvironmentFile $resolvedEnvironmentFile -DatabaseEngine $DatabaseEngine -r -IdentityProvider self-contained -AddExtensionSecurityMetadata
        }
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to start DMS infrastructure. Exit code: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    # 2. Provision the three route-context databases once with generated engine-correct DDL, then
    #    verify each with the engine-dispatched schema check.
    Write-Host "`nProvisioning and verifying route-context test databases..." -ForegroundColor Cyan
    $provisionE2EDatabaseScript = Join-Path $dockerComposeDir "provision-e2e-database.ps1"
    # PostgreSQL verification connects as the resolved role, not a hardcoded superuser, so a stack
    # started with a non-default POSTGRES_USER still verifies. Resolved with Compose precedence
    # (ambient wins) because Compose interpolates POSTGRES_USER into the container the same way
    # and provisioning/registration already resolve it ambient-first. MSSQL verification reads its
    # password inside dms-mssql from the container-resident MSSQL_SA_PASSWORD, so no SA password
    # is resolved or passed on the host here.
    $postgresUser = Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "POSTGRES_USER" -DefaultValue "postgres"

    foreach ($db in $databases) {
        # Each provision call is its own guarded phase, for the same reason the starts are.
        Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
            & $provisionE2EDatabaseScript `
                -EnvironmentFile $resolvedEnvironmentFile `
                -DatabaseEngine $DatabaseEngine `
                -DatabaseName $db `
                -Configuration Release
        }

        if ($LASTEXITCODE -ne 0) {
            throw "Failed to provision route-context database '$db' (exit code $LASTEXITCODE)."
        }

        # Outside the guard: this reads the provisioned database with docker exec and never resolves
        # the three schema names.
        Assert-RouteContextSchemaProvisioned -Database $db -DatabaseEngine $DatabaseEngine -PostgresUser $postgresUser
        Write-Host "  Provisioned and verified relational schema: $db" -ForegroundColor Green
    }

    # 3. Start DMS now that all schemas exist, and wait for DMS health.
    Write-Host "`nStarting DMS after route-context database provisioning..." -ForegroundColor Cyan
    Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
        ./start-local-dms.ps1 -DmsOnly -EnableConfig -EnvironmentFile $resolvedEnvironmentFile -DatabaseEngine $DatabaseEngine -IdentityProvider self-contained -AddExtensionSecurityMetadata
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to start DMS service after route-context database provisioning. Exit code: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    # Prove DMS actually came up on the environment file's schema package surface before the suite
    # registers any route context. In its own guard, after the DMS-only start: the check reads a
    # RUNNING container, and the guard keeps the file-only expectation from being contaminated by an
    # ambient override even if a future edit reaches for a Compose-precedence reader.
    # Assert-RouteContextSchemaProvisioned above checks that each database HAS the tables; this checks
    # that the runtime is loading the packages those tables were generated from.
    Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
        Assert-DmsContainerSchemaEnvironment `
            -EnvironmentFilePath $resolvedEnvironmentFile `
            -ContainerName "ed-fi-api"
    }

    Write-Host "`n========================================" -ForegroundColor Green
    Write-Host "Setup Complete!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "The following route-context databases are provisioned and verified:" -ForegroundColor Cyan
    foreach ($db in $databases) {
        Write-Host "  - $db" -ForegroundColor Gray
    }
    Write-Host ""
    $quotedEnvironmentFile = "'" + ($baseEnvironmentFile -replace "'", "''") + "'"
    Write-Host "To tear down this environment, run: ./teardown-local-dms.ps1 -DatabaseEngine $DatabaseEngine -EnvironmentFile $quotedEnvironmentFile" -ForegroundColor Cyan
}
finally {
    Set-Location $originalLocation
}
