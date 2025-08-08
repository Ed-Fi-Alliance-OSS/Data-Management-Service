# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Sets up the Ed-Fi DMS Configuration Service local Docker environment for E2E testing
.DESCRIPTION
    This script is a convenience wrapper that runs start-local-config.ps1 with the standard
    E2E testing configuration. It uses the isolated cs-local Docker stack (not dms-local)
    to match CI/CD behavior and avoid database contamination.
    
    The script runs:
    ./start-local-config.ps1 -EnvironmentFile ./.env.config.e2e -r
#>

[CmdletBinding()]
param()

Write-Host @"
Ed-Fi DMS Configuration Service Local Environment Setup for E2E Testing
=======================================================================
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
Write-Host "Docker is running ✓" -ForegroundColor Green
Write-Host ""

# Store current location and navigate to docker-compose directory
$originalLocation = Get-Location
$dockerComposeDir = Join-Path $PSScriptRoot "../../../../eng/docker-compose"

try {
    Set-Location $dockerComposeDir
    
    Write-Host "Starting CMS environment with E2E configuration..." -ForegroundColor Green
    Write-Host "Configuration:" -ForegroundColor Yellow
    Write-Host "  - Docker Stack: cs-local (isolated from dms-local)" -ForegroundColor Gray
    Write-Host "  - Environment File: ./.env.config.e2e" -ForegroundColor Gray
    Write-Host "  - Force Rebuild: Yes" -ForegroundColor Gray
    Write-Host "  - Services: PostgreSQL, Keycloak, Configuration Service" -ForegroundColor Gray
    Write-Host ""

    # Run the start script with E2E configuration (matches CI/CD behavior)
    ./start-local-config.ps1 -EnvironmentFile "./.env.config.e2e" -r

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to start CMS environment. Exit code: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    Write-Host "`nCMS E2E environment setup complete!" -ForegroundColor Green
    Write-Host "This uses the isolated 'cs-local' Docker stack to match CI/CD behavior." -ForegroundColor Cyan
    Write-Host "To tear down this environment, run: ./teardown-local-cms.ps1" -ForegroundColor Cyan
}
finally {
    # Return to original location
    Set-Location $originalLocation
}