# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Sets up only the infrastructure services (PostgreSQL, Kafka, etc.) for local DMS/CMS development
.DESCRIPTION
    This script starts only the supporting infrastructure services in Docker, allowing you to run
    DMS and Configuration Service locally via dotnet run. This avoids the need to build Docker images
    with Alpine Linux dependencies.

    Infrastructure services started:
    - PostgreSQL
    - Kafka + Kafka Connect
    - Kafka UI (optional)

    Services NOT started (run these locally):
    - DMS (Data Management Service)
    - Configuration Service
#>

[CmdletBinding()]
param(
    # Enable KafkaUI
    [Switch]
    $EnableKafkaUI,

    # Environment file
    [string]
    $EnvironmentFile = "./.env.e2e",

    # Identity provider type
    [string]
    [ValidateSet("keycloak", "self-contained")]
    $IdentityProvider="self-contained"
)

Write-Host @"
Ed-Fi DMS Infrastructure Setup (for Local Development)
======================================================
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

    # Import environment utility
    Import-Module ./env-utility.psm1 -Force
    $envValues = ReadValuesFromEnvFile $EnvironmentFile

    # Configure identity provider
    $env:DMS_CONFIG_IDENTITY_PROVIDER=$IdentityProvider
    Write-Output "Identity Provider: $IdentityProvider"

    if($IdentityProvider -eq "keycloak") {
        $env:OAUTH_TOKEN_ENDPOINT = $envValues.KEYCLOAK_OAUTH_TOKEN_ENDPOINT
        $env:DMS_JWT_AUTHORITY = $envValues.KEYCLOAK_DMS_JWT_AUTHORITY
        $env:DMS_JWT_METADATA_ADDRESS = $envValues.KEYCLOAK_DMS_JWT_METADATA_ADDRESS
        $env:DMS_CONFIG_IDENTITY_AUTHORITY = $envValues.KEYCLOAK_DMS_JWT_AUTHORITY
    }
    elseif ($IdentityProvider -eq "self-contained") {
        $env:OAUTH_TOKEN_ENDPOINT = $envValues.SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT
        $env:DMS_JWT_AUTHORITY = $envValues.SELF_CONTAINED_DMS_JWT_AUTHORITY
        $env:DMS_JWT_METADATA_ADDRESS = $envValues.SELF_CONTAINED_DMS_JWT_METADATA_ADDRESS
        $env:DMS_CONFIG_IDENTITY_AUTHORITY = $envValues.SELF_CONTAINED_DMS_JWT_AUTHORITY
    }

    # Build list of compose files (infrastructure only - no DMS/Config)
    $files = @(
        "-f", "postgresql.yml",
        "-f", "kafka.yml"
    )

    if ($EnableKafkaUI) {
        $files += @("-f", "kafka-ui.yml")
    }

    Write-Host "Starting infrastructure services..." -ForegroundColor Green
    Write-Host "Services to start:" -ForegroundColor Yellow
    Write-Host "  - PostgreSQL" -ForegroundColor Gray
    Write-Host "  - Kafka + Kafka Connect" -ForegroundColor Gray
    if ($EnableKafkaUI) {
        Write-Host "  - Kafka UI" -ForegroundColor Gray
    }
    Write-Host ""
    Write-Host "Services to run locally (not in Docker):" -ForegroundColor Yellow
    Write-Host "  - DMS (Data Management Service)" -ForegroundColor Magenta
    Write-Host "  - Configuration Service" -ForegroundColor Magenta
    Write-Host ""

    # Create network if it doesn't exist
    $existingNetwork = docker network ls --filter name="dms" -q
    if (! $existingNetwork) {
        Write-Host "Creating Docker network 'dms'..." -ForegroundColor Yellow
        docker network create dms
    }

    # Start Keycloak if using that identity provider
    if($IdentityProvider -eq "keycloak") {
        Write-Output "Starting Keycloak..."
        docker compose -f keycloak.yml --env-file $EnvironmentFile -p dms-local up --detach
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to start Keycloak. Exit code $LASTEXITCODE"
        }

        Write-Output "Running setup-keycloak.ps1 scripts..."
        ./setup-keycloak.ps1
        ./setup-keycloak.ps1 -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access"
        ./setup-keycloak.ps1 -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access"
    }

    # Start PostgreSQL
    Write-Output "Starting PostgreSQL..."
    docker compose -f postgresql.yml --env-file $EnvironmentFile -p dms-local up --detach
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to start PostgreSQL. Exit code $LASTEXITCODE"
    }

    Write-Host "Waiting for PostgreSQL to be ready..." -ForegroundColor Yellow
    Start-Sleep 20

    # Setup OpenIddict if using self-contained identity
    if($IdentityProvider -eq "self-contained") {
        Write-Output "Init db public and private keys for OpenIddict..."
        ./setup-openiddict.ps1 -InitDb -InsertData:$false -EnvironmentFile $EnvironmentFile
    }

    # Start Kafka and related services
    Write-Output "Starting Kafka services..."
    docker compose $files --env-file $EnvironmentFile -p dms-local up --detach

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to start infrastructure services. Exit code $LASTEXITCODE."
    }

    Write-Host "Waiting for services to be ready..." -ForegroundColor Yellow
    Start-Sleep 20

    # Setup connectors
    Write-Output "Running connector setup..."
    ./setup-connectors.ps1 $EnvironmentFile

    Write-Host "`nInfrastructure setup complete!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Cyan
    Write-Host "1. Run DMS locally:" -ForegroundColor White
    Write-Host "   cd src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore" -ForegroundColor Gray
    Write-Host "   dotnet run" -ForegroundColor Gray
    Write-Host ""
    Write-Host "2. Run Configuration Service locally:" -ForegroundColor White
    Write-Host "   cd src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore" -ForegroundColor Gray
    Write-Host "   dotnet run" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Connection strings:" -ForegroundColor Cyan
    Write-Host "  PostgreSQL: host=localhost;port=5432;username=postgres;password=$($envValues.POSTGRES_PASSWORD);database=$($envValues.POSTGRES_DB_NAME)" -ForegroundColor Gray
    Write-Host "  Kafka: localhost:9092" -ForegroundColor Gray
    Write-Host ""
    Write-Host "To tear down infrastructure, run: ./teardown-infrastructure-only.ps1" -ForegroundColor Cyan
}
catch {
    Write-Host ""
    Write-Error "Setup failed: $_"
    Write-Host ""
    Write-Host "Stack trace:" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
    exit 1
}
finally {
    # Return to original location
    Set-Location $originalLocation
}
