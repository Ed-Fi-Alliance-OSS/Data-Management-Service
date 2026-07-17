# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Docker smoke: the engine-by-topology-by-identity-provider configuration-database
    acceptance matrix.

.DESCRIPTION
    Durable, repeatable proof of the ticket's highest-risk acceptance matrix - the
    {PostgreSQL, SQL Server} x {shared, separate} x {self-contained, keycloak} cells - against a
    live Docker stack. For each cell it starts the real stack through build-dms.ps1 StartEnvironment
    (the same entry point whose default-env gap slipped through review), then inspects the physical
    databases and asserts schema placement:

      - Shared: a single datastore database (edfi_datamanagementservice) carries BOTH the CMS
        'dmscs' schema and the DMS 'dms' schema, and no separate edfi_configurationservice exists.
      - Separate: the dedicated edfi_configurationservice database carries the CMS 'dmscs' schema,
        the datastore database carries the DMS 'dms' schema and NOT 'dmscs', and the DMS datastore
        selection is unchanged.

    It also asserts the identity-provider-specific creation owner required by the acceptance
    criteria. The guarded setup-openiddict.ps1 -InitDb path owns pre-CMS creation on the
    self-contained path, while CMS EnsureDatabase owns creation on the Keycloak path. Each cell
    captures the StartEnvironment output and asserts the OpenIddict init step ran on self-contained
    and did NOT run on keycloak; a passing keycloak/separate cell (where -InitDb provably did not
    run, yet the dedicated configuration database exists with the CMS schema) therefore proves CMS
    EnsureDatabase created it.

    The script is intentionally MANUAL: it is not a *.Tests.ps1 spec, is not discovered by the
    Pester suite, and is not wired into CI (it needs a Docker daemon). It formalizes the manual
    engine-by-topology-by-identity-provider validation the reviewer asked to be made durable, so the
    acceptance matrix is repeatable and observable rather than an ad-hoc console session. The continuous, CI-runnable
    regression guard for the same contract lives in DatabaseEngineEnvironmentFile.Tests.ps1
    ("Configuration database topology matrix").

    Prerequisites:
      - Docker daemon running.
      - PowerShell 7+ (pwsh).
      - A local DMS image (or pass -UsePublishedImage). With -SkipDockerBuild the existing image
        is reused; otherwise build-dms.ps1 builds it.
      - Network access to the Ed-Fi package feed for ApiSchema staging (StartEnvironment provisions
        the DMS schema). A feed outage surfaces as a StartEnvironment step failure, not a topology
        assertion failure.

    Between cells the stack for BOTH engines is torn down with volumes, because a stale volume from
    a prior cell can otherwise leak into the next engine or topology.

    Exit code: 0 when every selected cell passes; non-zero when any cell fails.

.PARAMETER Engines
    Engines to run. Defaults to both.

.PARAMETER Topologies
    Topologies to run. Defaults to both.

.PARAMETER IdentityProviders
    Identity providers to run. Defaults to both: "self-contained" (guarded setup-openiddict.ps1
    -InitDb owns pre-CMS creation) and "keycloak" (CMS EnsureDatabase owns creation). Keycloak cells
    are slower; narrow to "self-contained" for a faster run.

.PARAMETER SkipDockerBuild
    Reuse the existing local DMS image instead of rebuilding (forwarded to StartEnvironment).

.PARAMETER UsePublishedImage
    Run against the published image instead of a locally built one (forwarded to StartEnvironment).

.PARAMETER SkipTeardown
    Leave the last cell's stack running after the matrix completes (useful for debugging a failure).
    Cells still tear down between iterations so each starts clean.

.PARAMETER ResultsPath
    Optional path; if supplied, writes a JSON summary of the run (per-cell status + timings).

.EXAMPLE
    # Full matrix (engine x topology x identity provider) against the existing local image
    pwsh ./Invoke-ConfigDatabaseTopologyMatrixSmoke.ps1 -SkipDockerBuild

.EXAMPLE
    # Only the Keycloak creation-owner cells (CMS EnsureDatabase creates the dedicated database)
    pwsh ./Invoke-ConfigDatabaseTopologyMatrixSmoke.ps1 -IdentityProviders keycloak -Topologies separate -SkipDockerBuild

.EXAMPLE
    # Only the two self-contained separate-topology cells, leaving the last stack up for inspection
    pwsh ./Invoke-ConfigDatabaseTopologyMatrixSmoke.ps1 -IdentityProviders self-contained -Topologies separate -SkipTeardown
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Manual smoke script intentionally writes operator progress and step banners to the console.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive: parameters are consumed inside nested script blocks and helper functions.')]
[CmdletBinding()]
param(
    [ValidateSet("postgresql", "mssql")]
    [string[]]$Engines = @("postgresql", "mssql"),

    [ValidateSet("shared", "separate")]
    [string[]]$Topologies = @("shared", "separate"),

    [ValidateSet("self-contained", "keycloak")]
    [string[]]$IdentityProviders = @("self-contained", "keycloak"),

    [switch]$SkipDockerBuild,

    [switch]$UsePublishedImage,

    [switch]$SkipTeardown,

    [string]$ResultsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:DockerComposeRoot = Split-Path -Parent $PSScriptRoot
$script:RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $script:DockerComposeRoot "../.."))
$script:BuildScript = Join-Path $script:RepoRoot "build-dms.ps1"
$script:PwshPath = (Get-Process -Id $PID).Path
$script:CellResults = [System.Collections.Generic.List[pscustomobject]]::new()

$script:DatastoreDatabase = "edfi_datamanagementservice"
$script:SeparateConfigDatabase = "edfi_configurationservice"
$script:CmsSchema = "dmscs"
$script:DmsSchema = "dms"

function Write-SmokeBanner {
    param([string]$Label)

    $banner = "=" * 78
    Write-Host ""
    Write-Host $banner
    Write-Host "[topology-smoke] $Label"
    Write-Host $banner
}

function Get-EnvFileValue {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Name
    )

    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ($trimmed.StartsWith("#") -or -not $trimmed.Contains("=")) { continue }
        $key, $value = $trimmed -split "=", 2
        if ($key.Trim() -eq $Name) { return $value.Trim() }
    }
    return $null
}

function Get-EngineFact {
    param([Parameter(Mandatory)] [string]$Engine)

    if ($Engine -eq "mssql") {
        return [pscustomobject]@{
            Container = "dms-mssql"
            Password  = Get-EnvFileValue -Path (Join-Path $script:DockerComposeRoot ".env.mssql") -Name "MSSQL_SA_PASSWORD"
        }
    }

    return [pscustomobject]@{
        Container = "dms-postgresql"
        Password  = Get-EnvFileValue -Path (Join-Path $script:DockerComposeRoot ".env.e2e") -Name "POSTGRES_PASSWORD"
    }
}

# Returns the set of non-system database names present in the running engine container.
function Get-DatabaseName {
    param([Parameter(Mandatory)] [pscustomobject]$Engine, [Parameter(Mandatory)] [pscustomobject]$Facts)

    if ($Engine.Name -eq "mssql") {
        $out = docker exec -e "SQLCMDPASSWORD=$($Facts.Password)" $Facts.Container `
            /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -h -1 -W `
            -Q "SET NOCOUNT ON; SELECT name FROM sys.databases WHERE name NOT IN ('master','tempdb','model','msdb');" 2>&1
    }
    else {
        $out = docker exec -e "PGPASSWORD=$($Facts.Password)" $Facts.Container `
            psql -U postgres -d postgres -tA `
            -c "SELECT datname FROM pg_database WHERE datname NOT IN ('postgres','template0','template1');" 2>&1
    }
    if ($LASTEXITCODE -ne 0) { throw "Listing databases in $($Facts.Container) failed: $out" }

    return @($out | ForEach-Object { "$_".Trim() } | Where-Object { $_ -ne "" })
}

# Returns the set of schema names present in the named database, or $null when the database is absent.
function Get-SchemaName {
    param(
        [Parameter(Mandatory)] [pscustomobject]$Engine,
        [Parameter(Mandatory)] [pscustomobject]$Facts,
        [Parameter(Mandatory)] [string]$Database
    )

    if ($Engine.Name -eq "mssql") {
        $out = docker exec -e "SQLCMDPASSWORD=$($Facts.Password)" $Facts.Container `
            /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -h -1 -W `
            -Q "SET NOCOUNT ON; USE [$Database]; SELECT name FROM sys.schemas;" 2>&1
    }
    else {
        $out = docker exec -e "PGPASSWORD=$($Facts.Password)" $Facts.Container `
            psql -U postgres -d $Database -tA `
            -c "SELECT schema_name FROM information_schema.schemata;" 2>&1
    }
    if ($LASTEXITCODE -ne 0) { throw "Listing schemas in $Database ($($Facts.Container)) failed: $out" }

    return @($out | ForEach-Object { "$_".Trim() } | Where-Object { $_ -ne "" })
}

function Invoke-MatrixTeardown {
    param([string]$Label = "teardown both engines")

    Write-Host "[topology-smoke] $Label ..."
    Push-Location $script:DockerComposeRoot
    try {
        # Down both engines with volumes so no stale datastore or config volume leaks into the next
        # cell. Both compose projects are torn down: dms-local (local image) and dms-published
        # (-UsePublishedImage starts the stack in that project via start-published-dms.ps1), so the
        # clean-cell guarantee holds in either mode. A down on an absent project is a harmless no-op.
        # Errors are non-fatal (a stack may simply not be running).
        $projects = @(
            @{ Name = "dms-local";     Dms = "local-dms.yml";    Config = "local-config.yml" }
            @{ Name = "dms-published"; Dms = "published-dms.yml"; Config = "published-config.yml" }
        )
        foreach ($project in $projects) {
            docker compose -f postgresql.yml -f $project.Dms -f kafka.yml -f $project.Config -f keycloak.yml --env-file ./.env.e2e -p $project.Name down -v --remove-orphans 2>&1 | Out-Null
            docker compose -f mssql.yml -f $project.Dms -f kafka.yml -f $project.Config -f keycloak.yml --env-file ./.env.e2e -p $project.Name down -v --remove-orphans 2>&1 | Out-Null
        }
    }
    finally {
        Pop-Location
    }
}

function Invoke-Cell {
    param(
        [Parameter(Mandatory)] [string]$Engine,
        [Parameter(Mandatory)] [string]$Topology,
        [Parameter(Mandatory)] [string]$IdentityProvider
    )

    $separate = ($Topology -eq "separate")
    $expectedConfigDb = if ($separate) { $script:SeparateConfigDatabase } else { $script:DatastoreDatabase }
    $creationOwner = if ($IdentityProvider -eq "keycloak") { "CMS EnsureDatabase" } else { "setup-openiddict.ps1 -InitDb" }
    $engineObj = [pscustomobject]@{ Name = $Engine }
    $facts = Get-EngineFact -Engine $Engine

    Write-SmokeBanner "cell $Engine / $Topology / $IdentityProvider  (CMS -> $expectedConfigDb, DMS -> $($script:DatastoreDatabase), creation owner -> $creationOwner)"

    $startTime = Get-Date
    $status = "ok"
    $errorMessage = $null

    try {
        Invoke-MatrixTeardown -Label "pre-cell teardown"

        # Start the real stack through the StartEnvironment entry point for this cell. Run build-dms.ps1
        # in a child pwsh process so its module imports, location stack, and $LASTEXITCODE stay isolated
        # from this loop across cells; a failure surfaces as a non-zero child exit code.
        $startArgs = @("-NoProfile", "-File", $script:BuildScript, "StartEnvironment", "-DatabaseEngine", $Engine, "-IdentityProvider", $IdentityProvider)
        if ($separate) { $startArgs += "-SeparateConfigDatabase" }
        if ($SkipDockerBuild) { $startArgs += "-SkipDockerBuild" }
        if ($UsePublishedImage) { $startArgs += "-UsePublishedImage" }

        Write-Host "[topology-smoke] $($script:PwshPath) $($startArgs -join ' ')"
        # Capture the child output (still echoed via Tee-Object) so the creation owner can be asserted
        # from the observed startup steps. $LASTEXITCODE remains the native pwsh exit code across the
        # trailing cmdlet in the pipeline.
        & $script:PwshPath @startArgs 2>&1 | Tee-Object -Variable startOutput
        if ($LASTEXITCODE -ne 0) {
            throw "StartEnvironment for $Engine/$Topology/$IdentityProvider exited with code $LASTEXITCODE."
        }
        $startText = ($startOutput | Out-String)

        # Identity-provider-specific creation owner: setup-openiddict.ps1 -InitDb runs only on the
        # self-contained path (its OpenIddict key-store init prints a distinctive banner). On the
        # keycloak path it must NOT run - CMS EnsureDatabase owns configuration-database creation - so
        # a passing keycloak/separate cell (dedicated database present with the CMS schema below,
        # without any -InitDb) proves CMS EnsureDatabase created it.
        $ranOpenIddictInit = $startText -match 'Init db public and private keys for OpenIddict'
        if ($IdentityProvider -eq "keycloak") {
            if ($ranOpenIddictInit) {
                throw "keycloak cell ran setup-openiddict.ps1 -InitDb; CMS EnsureDatabase must own configuration-database creation on the Keycloak path."
            }
        }
        else {
            if (-not $ranOpenIddictInit) {
                throw "self-contained cell did not run setup-openiddict.ps1 -InitDb; the OpenIddict path must own pre-CMS configuration-database creation."
            }
        }

        # Inspect the physical databases and assert schema placement.
        $databases = Get-DatabaseName -Engine $engineObj -Facts $facts
        Write-Host "[topology-smoke] databases present: $($databases -join ', ')"

        if ($script:DatastoreDatabase -notin $databases) {
            throw "Datastore database '$($script:DatastoreDatabase)' is missing (found: $($databases -join ', '))."
        }

        $datastoreSchemas = Get-SchemaName -Engine $engineObj -Facts $facts -Database $script:DatastoreDatabase
        Write-Host "[topology-smoke] $($script:DatastoreDatabase) schemas: $($datastoreSchemas -join ', ')"

        if ($script:DmsSchema -notin $datastoreSchemas) {
            throw "DMS schema '$($script:DmsSchema)' is missing from the datastore database '$($script:DatastoreDatabase)'."
        }

        if ($separate) {
            # Under the separate topology the two schemas must be fully disjoint across the two
            # databases: the dedicated configuration database holds the CMS schema and NOT the DMS
            # schema, and the datastore database holds the DMS schema and NOT the CMS schema. Asserting
            # both directions of both schemas is required so a duplicated or misdirected DMS schema in
            # the CMS database cannot pass despite the "restore/DMS never targets the CMS database" contract.
            if ($expectedConfigDb -notin $databases) {
                throw "Separate configuration database '$expectedConfigDb' is missing (found: $($databases -join ', '))."
            }
            $configSchemas = Get-SchemaName -Engine $engineObj -Facts $facts -Database $expectedConfigDb
            Write-Host "[topology-smoke] $expectedConfigDb schemas: $($configSchemas -join ', ')"

            if ($script:CmsSchema -notin $configSchemas) {
                throw "CMS schema '$($script:CmsSchema)' is missing from the configuration database '$expectedConfigDb'."
            }
            if ($script:CmsSchema -in $datastoreSchemas) {
                throw "CMS schema '$($script:CmsSchema)' unexpectedly present in the datastore database '$($script:DatastoreDatabase)' under the separate topology."
            }
            if ($script:DmsSchema -in $configSchemas) {
                throw "DMS schema '$($script:DmsSchema)' unexpectedly present in the configuration database '$expectedConfigDb' under the separate topology; the DMS datastore schema must never target the CMS database."
            }
        }
        else {
            # Shared: the datastore database holds BOTH schemas and no separate config database exists.
            if ($script:CmsSchema -notin $datastoreSchemas) {
                throw "CMS schema '$($script:CmsSchema)' is missing from the shared datastore database '$($script:DatastoreDatabase)'."
            }
            if ($script:SeparateConfigDatabase -in $databases) {
                throw "Separate configuration database '$($script:SeparateConfigDatabase)' unexpectedly present under the shared topology."
            }
        }

        Write-Host "[topology-smoke] PASS: $Engine / $Topology / $IdentityProvider" -ForegroundColor Green
    }
    catch {
        $status = "failed"
        $errorMessage = $_.Exception.Message
        Write-Host "[topology-smoke] FAIL: $Engine / $Topology / $IdentityProvider -> $errorMessage" -ForegroundColor Red
    }
    finally {
        $duration = (Get-Date) - $startTime
        $script:CellResults.Add([pscustomobject]@{
            Engine           = $Engine
            Topology         = $Topology
            IdentityProvider = $IdentityProvider
            CreationOwner    = $creationOwner
            ExpectedConfigDb = $expectedConfigDb
            Status           = $status
            DurationSeconds  = [math]::Round($duration.TotalSeconds, 2)
            Error            = $errorMessage
        })
    }
}

# ---------------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------------
try {
    docker info 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Docker daemon is not reachable. Start Docker and retry." }
}
catch {
    Write-Host "[topology-smoke] Preflight failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

try {
    foreach ($engine in $Engines) {
        foreach ($topology in $Topologies) {
            foreach ($identityProvider in $IdentityProviders) {
                Invoke-Cell -Engine $engine -Topology $topology -IdentityProvider $identityProvider
            }
        }
    }
}
finally {
    if (-not $SkipTeardown) {
        Invoke-MatrixTeardown -Label "final teardown"
    }
    else {
        Write-Host "[topology-smoke] -SkipTeardown set; leaving the last cell's stack running."
    }
}

Write-SmokeBanner "matrix summary"
$script:CellResults |
    Select-Object Engine, Topology, IdentityProvider, CreationOwner, ExpectedConfigDb, Status, DurationSeconds, Error |
    Format-Table -AutoSize | Out-Host

if ($ResultsPath) {
    $script:CellResults | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ResultsPath -Encoding utf8
    Write-Host "[topology-smoke] wrote results to $ResultsPath"
}

$failedCount = @($script:CellResults | Where-Object { $_.Status -ne "ok" }).Count
if ($failedCount -gt 0) {
    Write-Host "[topology-smoke] $failedCount of $($script:CellResults.Count) cell(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host "[topology-smoke] all $($script:CellResults.Count) cell(s) passed." -ForegroundColor Green
exit 0
