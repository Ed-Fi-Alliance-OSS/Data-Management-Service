# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Sets up the Ed-Fi DMS local Docker environment for Instance Management E2E testing
.DESCRIPTION
    This script starts the Docker stack and creates the 3 test databases.
    Tenant, instance, and Kafka infrastructure creation is handled by the tests themselves.

    The script runs:
    ./start-local-dms.ps1 -EnableKafkaUI -EnableConfig -EnvironmentFile ./.env.routeContext.e2e -r -AddExtensionSecurityMetadata -IdentityProvider self-contained
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
Write-Host "Docker is running" -ForegroundColor Green
Write-Host ""

# Store current location and navigate to docker-compose directory
$originalLocation = Get-Location
$dockerComposeDir = Join-Path $PSScriptRoot "../../../../eng/docker-compose"

try {
    Set-Location $dockerComposeDir

    Write-Host "Starting DMS environment with Instance Management E2E configuration..." -ForegroundColor Green
    Write-Host "Configuration:" -ForegroundColor Yellow
    Write-Host "  - Kafka UI: Enabled" -ForegroundColor Gray
    Write-Host "  - Configuration Service: Enabled" -ForegroundColor Gray
    Write-Host "  - Environment File: ./.env.routeContext.e2e" -ForegroundColor Gray
    Write-Host "  - Force Rebuild: Yes" -ForegroundColor Gray
    Write-Host "  - Extension Security Metadata: Yes" -ForegroundColor Gray
    Write-Host "  - Route Qualifiers: districtId, schoolYear" -ForegroundColor Cyan
    Write-Host "  - Identity Provider: self-contained" -ForegroundColor Gray
    Write-Host ""
    Write-Host "NOTE: Tenant, instance, and Kafka infrastructure will be created by tests" -ForegroundColor Yellow
    Write-Host ""

    # Run the start script - NO instance creation
    ./start-local-dms.ps1 -EnableKafkaUI -EnableConfig -EnvironmentFile ./.env.routeContext.e2e -r -AddExtensionSecurityMetadata -IdentityProvider self-contained -AddDmsInstance:$false

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to start DMS environment. Exit code: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    # Create the three test databases
    Write-Host "`nCreating test databases..." -ForegroundColor Cyan

    $databases = @(
        "edfi_datamanagementservice_d255901_sy2024",
        "edfi_datamanagementservice_d255901_sy2025",
        "edfi_datamanagementservice_d255902_sy2024"
    )

    foreach ($db in $databases) {
        docker exec dms-postgresql psql -U postgres -c "CREATE DATABASE $db;" 2>&1 | Out-Null
        Write-Host "  Created database: $db" -ForegroundColor Green
    }

    # Export schema from main database
    Write-Host "`nExporting schema from main database..." -ForegroundColor Cyan
    $tempSchemaFile = [System.IO.Path]::GetTempFileName()
    docker exec dms-postgresql pg_dump -U postgres -d edfi_datamanagementservice --schema-only > $tempSchemaFile
    Write-Host "  Schema exported successfully" -ForegroundColor Green

    # Apply schema to each test database
    Write-Host "`nApplying schema to test databases..." -ForegroundColor Cyan
    foreach ($db in $databases) {
        Get-Content $tempSchemaFile | docker exec -i dms-postgresql psql -U postgres -d $db
        Write-Host "  Schema applied to: $db" -ForegroundColor Green
    }

    # Clean up temp file
    Remove-Item $tempSchemaFile -ErrorAction SilentlyContinue

    Write-Host "`n========================================" -ForegroundColor Green
    Write-Host "Setup Complete!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "The following databases are ready:" -ForegroundColor Cyan
    foreach ($db in $databases) {
        Write-Host "  - $db" -ForegroundColor Gray
    }
    Write-Host ""
    Write-Host "Kafka topics and Debezium connectors will be created by tests." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "To tear down this environment, run: ./teardown-local-dms.ps1" -ForegroundColor Cyan
}
finally {
    Set-Location $originalLocation
}
