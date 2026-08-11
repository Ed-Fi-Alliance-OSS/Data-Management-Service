# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Sets up the Ed-Fi DMS local Docker environment for E2E testing
.DESCRIPTION
    This script is a convenience wrapper that runs start-local-dms.ps1 with the standard
    E2E testing configuration. It is the companion to teardown-local-dms.ps1.

    Extension schema packages (Sample, Homograph) are loaded through the file-based SCHEMA_PACKAGES path.
    The -AddExtensionSecurityMetadata switch activates Hybrid claims mode so extension
    claimset fragments are loaded from the AdditionalClaimsets directory mounted at
    /app/additional-claims. This is the non-bootstrap compatibility path; bootstrap mode
    activates staged schema and claims automatically when a manifest is present.

    The script runs (with -DatabaseEngine forwarded to every engine-aware phase):
    ./start-local-dms.ps1 -InfraOnly -EnableConfig -EnvironmentFile <selected env file> -DatabaseEngine <engine> -r -AddExtensionSecurityMetadata
    ./configure-local-data-store.ps1 -EnvironmentFile <selected env file> -DatabaseEngine <engine> -DataStoreDatabaseName <E2E_DATABASE_NAME>
    ./provision-e2e-database.ps1 -EnvironmentFile <selected env file> -DatabaseEngine <engine> -DatabaseName <E2E_DATABASE_NAME>
    ./start-local-dms.ps1 -DmsOnly -EnableConfig -EnvironmentFile <selected env file> -DatabaseEngine <engine> -AddExtensionSecurityMetadata

    Every Docker phase above runs with USE_API_SCHEMA_PATH, API_SCHEMA_PATH, and SCHEMA_PACKAGES
    removed from this process, so the selected environment file is the sole authority for the schema
    package surface of the whole direct setup flow. Docker Compose gives process environment
    variables precedence over --env-file entries, and local-dms.yml resolves each of those three
    with a ${VAR:-default} fallback that treats a present-but-blank value exactly like an unset one.
    An ambient blank or false value is therefore one confirmed way to start DMS on the image-baked
    schemas while provisioning has already stamped the environment file's full package surface, after
    which every data-plane request fails with an EffectiveSchemaHash mismatch. That is a failure mode
    this guard closes; it is not established as the source of any particular reported incident. The
    caller's original environment is restored exactly on completion, including the absent, empty,
    whitespace, and valued distinctions, and on failure paths.

    After DMS starts, the script verifies the container actually received that package surface and
    fails the setup when it did not, so a mismatch from any cause is reported here rather than as an
    HTTP 503 EffectiveSchemaHash failure in every scenario of the suite. Both sides of the comparison
    are read from the environment file, never with Docker Compose precedence.

    On completion the script prints a copyable teardown command carrying the same -DatabaseEngine and
    the resolved -EnvironmentFile, so a custom or MSSQL run is torn down against its own compose
    definition/environment rather than the teardown wrapper's postgresql/.env.e2e defaults:
    ./teardown-local-dms.ps1 -DatabaseEngine <engine> -EnvironmentFile '<resolved env file>'.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Setup script is intentionally host-oriented and uses console progress output.')]
[CmdletBinding()]
param(
    [string] $EnvironmentFile = "./.env.e2e",

    # Optional Ed-Fi Data Standard version (e.g. "5.2", "6.1") composed into the effective environment file.
    [string] $DataStandardVersion,

    # Database engine backing the stack. "postgresql" (default) or "mssql".
    [ValidateSet("postgresql", "mssql")]
    [string] $DatabaseEngine = "postgresql"
)

function Get-DirectSetupTeardownCommand {
    # Builds a copyable teardown command that carries the same engine and environment file this setup
    # used, so a custom or MSSQL run is torn down against its own compose definition/environment rather
    # than the teardown wrapper's postgresql/.env.e2e defaults. The path is single-quoted (with embedded
    # single quotes doubled) so a path containing spaces or quotes is safe to paste back.
    param(
        [Parameter(Mandatory)]
        [string] $DatabaseEngine,

        [Parameter(Mandatory)]
        [string] $EnvironmentFile
    )

    $quotedEnvironmentFile = "'" + ($EnvironmentFile -replace "'", "''") + "'"
    return "./teardown-local-dms.ps1 -DatabaseEngine $DatabaseEngine -EnvironmentFile $quotedEnvironmentFile"
}

function Invoke-WithEnvironmentFileSchemaSettings {
    # Runs the direct setup flow's Docker phases with the three schema package variables absent from
    # this process, so Docker Compose must resolve them from the selected --env-file, and restores
    # the caller's exact prior state afterward.
    #
    # Compose gives process environment variables precedence over --env-file entries, and
    # local-dms.yml resolves all three with a ${VAR:-default} fallback. Because ':-' substitutes the
    # default for an empty value as well as an unset one, an ambient blank value silently wins over
    # the environment file: the DMS container is created with AppSettings__UseApiSchemaPath=false and
    # empty AppSettings__ApiSchemaPath/SCHEMA_PACKAGES, run.sh skips the package download entirely,
    # and DMS loads only the image-baked schemas. Provisioning is not affected, because it reads
    # SCHEMA_PACKAGES from the environment file only - so the database is stamped for the file's full
    # package surface while DMS computes a different runtime hash, and every data-plane request fails
    # with an EffectiveSchemaHash mismatch. That path is confirmed by construction from Compose's
    # documented precedence; it is not a diagnosis of any particular reported incident, and this guard
    # is cheap enough to hold regardless of which cause produced one.
    #
    # The whole phase sequence is guarded rather than only the two start-local-dms.ps1 calls: the
    # environment file is authoritative for the entire direct setup flow, and a Compose call added to
    # any phase later is covered by construction. The configure and provision phases are unaffected
    # by the removal, because neither reads these variables from the process environment.
    #
    # This is deliberately a caller-side guard. start-local-dms.ps1 must not clear these globally:
    # in bootstrap mode it sets them in-process on purpose, so process precedence makes the staged
    # .bootstrap/ApiSchema workspace authoritative.
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Names the schema settings carried by an environment file, matching the equivalent build-dms.ps1 helper.')]
    param(
        [Parameter(Mandatory)]
        [scriptblock] $Action
    )

    $schemaEnvironmentVariableNames = @(
        "USE_API_SCHEMA_PATH",
        "API_SCHEMA_PATH",
        "SCHEMA_PACKAGES"
    )

    # $null distinguishes absent from present-and-empty, which is the distinction the restore below
    # has to reproduce.
    $previousValues = @{}
    foreach ($name in $schemaEnvironmentVariableNames) {
        $previousValues[$name] = [System.Environment]::GetEnvironmentVariable($name)
    }

    try {
        foreach ($name in $schemaEnvironmentVariableNames) {
            # Remove-Item, never an assignment: whether '$env:X = $null' removes the variable or
            # leaves it present-and-blank varies by platform and PowerShell/.NET version, and a blank
            # value satisfies ${VAR:-default} - which is the bug this guard exists to prevent.
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }

        & $Action
    }
    finally {
        # Restore each variable to its exact prior state: re-create it with the verbatim prior value
        # (including empty and whitespace) when it existed, otherwise remove it. This runs on the
        # success path, when the action throws, and when the action calls exit.
        foreach ($name in $schemaEnvironmentVariableNames) {
            if ($null -eq $previousValues[$name]) {
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }
            else {
                [System.Environment]::SetEnvironmentVariable($name, $previousValues[$name])
            }
        }
    }
}

Write-Host @"
Ed-Fi DMS Local Environment Setup for E2E Testing
=================================================
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
    } else {
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
    # Shared Compose-equivalent resolver so this wrapper selects the same E2E target database the
    # provision phase resets (an ambient E2E_DATABASE_NAME override wins over the env file).
    Import-Module ./database-safety.psm1 -Force
    # The post-start container schema verification, shared with the Instance Management E2E wrapper so
    # both flows verify the same thing the same way. It reads the environment file through the same
    # file-only package reader the provision phase uses, so the container is compared against exactly
    # the package surface the database was provisioned for.
    Import-Module ./dms-schema-environment.psm1 -Force

    $baseEnvironmentFile = Resolve-LocalSettingsEnvironmentFile -Path $EnvironmentFile -DockerComposeRoot $dockerComposeDir
    # Compose the data-standard overlay first, then the database-engine overlay (same order as
    # start-local-dms.ps1), so every phase below reads the one resolved file. For postgresql the
    # engine step is a no-op.
    $resolvedEnvironmentFile = Resolve-DataStandardEnvironmentFile `
        -DataStandardVersion $DataStandardVersion `
        -BaseEnvironmentFile $baseEnvironmentFile `
        -DockerComposeRoot $dockerComposeDir
    $resolvedEnvironmentFile = Resolve-DatabaseEngineEnvironmentFile `
        -DatabaseEngine $DatabaseEngine `
        -BaseEnvironmentFile $resolvedEnvironmentFile `
        -DockerComposeRoot $dockerComposeDir
    $envValues = ReadValuesFromEnvFile $resolvedEnvironmentFile
    $e2eDatabaseName = Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "E2E_DATABASE_NAME"

    if ([string]::IsNullOrWhiteSpace($e2eDatabaseName)) {
        throw "E2E_DATABASE_NAME must be set in '$resolvedEnvironmentFile' or the process environment so direct DMS E2E setup creates a CMS data store against the provisioned E2E database."
    }

    $bootstrapDir = Join-Path $dockerComposeDir ".bootstrap"
    if (Test-Path -LiteralPath $bootstrapDir) {
        Write-Output "Removing stale .bootstrap workspace before file-based schema package E2E startup..."
        # Fail fast on cleanup errors: a stale manifest left here would trigger bootstrap mode
        # on the next start-local-dms.ps1 invocation and silently divert the E2E run.
        Remove-Item -LiteralPath $bootstrapDir -Recurse -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $bootstrapDir) {
            throw "Failed to remove stale .bootstrap workspace at '$bootstrapDir'. Resolve any file locks or permissions before re-running setup."
        }
    }

    Write-Host "Starting DMS environment with E2E configuration..." -ForegroundColor Green
    Write-Host "Configuration:" -ForegroundColor Yellow
    Write-Host "  - Search Engine UI: Enabled" -ForegroundColor Gray
    Write-Host "  - Configuration Service: Enabled" -ForegroundColor Gray
    Write-Host "  - Environment File: $resolvedEnvironmentFile" -ForegroundColor Gray
    Write-Host "  - Database Engine: $DatabaseEngine" -ForegroundColor Gray
    Write-Host "  - E2E Database: $e2eDatabaseName" -ForegroundColor Gray
    Write-Host "  - Force Rebuild: Yes" -ForegroundColor Gray
    Write-Output "  - Extension Security Metadata: Yes"
    Write-Host ""

    Write-Output "Using file-based schema packages from $resolvedEnvironmentFile for E2E (non-bootstrap compatibility path)."

    Invoke-WithEnvironmentFileSchemaSettings -Action {
        # Start only the infrastructure and Configuration Service first. DMS starts after the
        # E2E data store exists and the relational schema has been provisioned.
        ./start-local-dms.ps1 -InfraOnly -EnableConfig -EnvironmentFile $resolvedEnvironmentFile -DatabaseEngine $DatabaseEngine -r -AddExtensionSecurityMetadata

        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to start DMS infrastructure. Exit code: $LASTEXITCODE"
            exit $LASTEXITCODE
        }

        # Create the default data store via the configuration phase. start-local-dms.ps1 no longer
        # creates a data store automatically; instance creation is owned by configure-local-data-store.ps1.
        # Config Service is already healthy at this point because the -InfraOnly phase waits for
        # CMS readiness before returning.
        ./configure-local-data-store.ps1 -EnvironmentFile $resolvedEnvironmentFile -DatabaseEngine $DatabaseEngine -DataStoreDatabaseName $e2eDatabaseName

        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to configure local data store. Exit code: $LASTEXITCODE"
            exit $LASTEXITCODE
        }

        Write-Host "`nProvisioning E2E database '$e2eDatabaseName'..." -ForegroundColor Cyan
        ./provision-e2e-database.ps1 -EnvironmentFile $resolvedEnvironmentFile -DatabaseEngine $DatabaseEngine -DatabaseName $e2eDatabaseName

        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to provision E2E database '$e2eDatabaseName'. Exit code: $LASTEXITCODE"
            exit $LASTEXITCODE
        }

        Write-Host "`nStarting DMS after E2E database provisioning..." -ForegroundColor Cyan
        ./start-local-dms.ps1 -DmsOnly -EnableConfig -EnvironmentFile $resolvedEnvironmentFile -DatabaseEngine $DatabaseEngine -AddExtensionSecurityMetadata

        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to start DMS service after E2E database provisioning. Exit code: $LASTEXITCODE"
            exit $LASTEXITCODE
        }

        # Prove DMS actually came up on the environment file's schema package surface before any
        # scenario runs. Inside the guard, so the file-only expectation cannot be contaminated by an
        # ambient override even if a future edit reaches for a Compose-precedence reader.
        Assert-DmsContainerSchemaEnvironment `
            -EnvironmentFilePath $resolvedEnvironmentFile `
            -EnvironmentValues $envValues `
            -ContainerName "ed-fi-api"
    }

    # Pass the fully resolved environment file (data-standard then engine overlay) so teardown uses the
    # same effective environment the setup composed, not the pre-overlay base.
    $teardownCommand = Get-DirectSetupTeardownCommand -DatabaseEngine $DatabaseEngine -EnvironmentFile $resolvedEnvironmentFile
    Write-Host "`nDMS E2E environment setup complete!" -ForegroundColor Green
    Write-Host "To tear down this environment, run: $teardownCommand" -ForegroundColor Cyan
}
finally {
    # Return to original location
    Set-Location $originalLocation
}
