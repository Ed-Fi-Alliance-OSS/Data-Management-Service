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

    The script runs:
    ./start-local-dms.ps1 -EnableConfig -EnvironmentFile <selected env file> -r -AddExtensionSecurityMetadata
    ./configure-local-data-store.ps1 -EnvironmentFile <selected env file> -DataStoreDatabaseName <E2E_DATABASE_NAME>
    ./provision-e2e-database.ps1 -EnvironmentFile <selected env file>
    docker restart ed-fi-api
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Setup script is intentionally host-oriented and uses console progress output.')]
[CmdletBinding()]
param(
    [string] $EnvironmentFile = "./.env.e2e",

    # Optional Ed-Fi Data Standard version (e.g. "5.2", "6.1") forwarded to start-local-dms.ps1.
    [string] $DataStandardVersion
)

function Wait-DmsHealthy {
    param(
        [string] $DmsBaseUrl,
        [int] $TimeoutSeconds = 60
    )

    $healthUrl = "$($DmsBaseUrl.TrimEnd('/'))/health"
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)

    while ($true) {
        try {
            $response = Invoke-WebRequest -Uri $healthUrl -Method Get -TimeoutSec 5 -ErrorAction Stop
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                Write-Host "DMS service is healthy." -ForegroundColor Green
                return
            }
        }
        catch {
            $null = $_
        }

        if ([datetime]::UtcNow -ge $deadline) {
            throw "DMS health check timed out after $TimeoutSeconds seconds. Endpoint: $healthUrl"
        }

        Start-Sleep -Seconds 2
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

    $resolvedEnvironmentFile = Resolve-LocalSettingsEnvironmentFile -Path $EnvironmentFile -DockerComposeRoot $dockerComposeDir
    $envValues = ReadValuesFromEnvFile $resolvedEnvironmentFile
    $e2eDatabaseName = Get-EnvValue -EnvValues $envValues -Name "E2E_DATABASE_NAME"

    if ([string]::IsNullOrWhiteSpace($e2eDatabaseName)) {
        throw "E2E_DATABASE_NAME must be set in '$resolvedEnvironmentFile' so direct DMS E2E setup creates a CMS data store against the provisioned E2E database."
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
    Write-Host "  - E2E Database: $e2eDatabaseName" -ForegroundColor Gray
    Write-Host "  - Force Rebuild: Yes" -ForegroundColor Gray
    Write-Output "  - Extension Security Metadata: Yes"
    Write-Host ""

    Write-Output "Using file-based schema packages from $resolvedEnvironmentFile for E2E (non-bootstrap compatibility path)."

    # Run the start script with E2E configuration
    ./start-local-dms.ps1 -EnableConfig -EnvironmentFile $resolvedEnvironmentFile -r -AddExtensionSecurityMetadata -DataStandardVersion $DataStandardVersion

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to start DMS environment. Exit code: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    # Create the default data store via the configuration phase. start-local-dms.ps1 no longer
    # creates a data store automatically; instance creation is owned by configure-local-data-store.ps1.
    # Config Service is already healthy at this point (start-local-dms.ps1 with -EnableConfig waits
    # for CMS readiness before returning).
    #
    # This non-bootstrap E2E flow intentionally keeps the full start rather than the
    # -InfraOnly/-DmsOnly split. The DMS container restarts until this step lands the data store
    # (non-bootstrap compatibility flow).
    ./configure-local-data-store.ps1 -EnvironmentFile $resolvedEnvironmentFile -DataStoreDatabaseName $e2eDatabaseName

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to configure local data store. Exit code: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    Write-Host "`nProvisioning E2E database '$e2eDatabaseName'..." -ForegroundColor Cyan
    ./provision-e2e-database.ps1 -EnvironmentFile $resolvedEnvironmentFile

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to provision E2E database '$e2eDatabaseName'. Exit code: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    Write-Host "`nRestarting DMS container to discard cached database state..." -ForegroundColor Cyan
    $restartOutput = docker restart ed-fi-api 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host $restartOutput -ForegroundColor Red
        Write-Error "Failed to restart DMS container after E2E database provisioning. Exit code: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    Write-Host $restartOutput -ForegroundColor Gray
    $dmsBaseUrl = Resolve-DockerLocalDmsBaseUrl -EnvValues $envValues
    Write-Host "Waiting for DMS to become healthy at $dmsBaseUrl..." -ForegroundColor Yellow
    Wait-DmsHealthy -DmsBaseUrl $dmsBaseUrl

    Write-Host "`nDMS E2E environment setup complete!" -ForegroundColor Green
    Write-Host "To tear down this environment, run: ./teardown-local-dms.ps1" -ForegroundColor Cyan
}
finally {
    # Return to original location
    Set-Location $originalLocation
}
