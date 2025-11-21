# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Sets up the Ed-Fi DMS local Docker environment for Instance Management E2E testing
.DESCRIPTION
    This script is a convenience wrapper that runs start-local-dms.ps1 with the standard
    E2E testing configuration for instance management tests. It is the companion to teardown-local-dms.ps1.

    The script runs:
    ./start-local-dms.ps1 -EnableKafkaUI -EnableConfig -EnvironmentFile ./.env.e2e -r -AddExtensionSecurityMetadata
#>

[CmdletBinding()]
param()

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

    Write-Host "Starting DMS environment with Instance Management E2E configuration..." -ForegroundColor Green
    Write-Host "Configuration:" -ForegroundColor Yellow
    Write-Host "  - Search Engine UI: Enabled" -ForegroundColor Gray
    Write-Host "  - Configuration Service: Enabled" -ForegroundColor Gray
    Write-Host "  - Environment File: ./.env.routeContext.e2e" -ForegroundColor Gray
    Write-Host "  - Force Rebuild: Yes" -ForegroundColor Gray
    Write-Host "  - Extension Security Metadata: Yes" -ForegroundColor Gray
    Write-Host "  - Route Qualifiers: districtId, schoolYear" -ForegroundColor Cyan
    Write-Host "  - Identity Provider: self-contained" -ForegroundColor Gray
    Write-Host "  - Add Test DMS Instances: Yes (3 instances with IDs 1, 2, 3)" -ForegroundColor Cyan
    Write-Host ""

    # Run the start script with Route Context E2E configuration
    # -AddDmsInstance creates 3 test instances with route contexts for Kafka topic-per-instance testing
    ./start-local-dms.ps1 -EnableKafkaUI -EnableConfig -EnvironmentFile ./.env.routeContext.e2e -r -AddExtensionSecurityMetadata -IdentityProvider self-contained -AddDmsInstance

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to start DMS environment. Exit code: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    Write-Host "`nDMS Instance Management E2E environment setup complete!" -ForegroundColor Green
    Write-Host "To tear down this environment, run: ./teardown-local-dms.ps1" -ForegroundColor Cyan
}
finally {
    # Return to original location
    Set-Location $originalLocation
}
