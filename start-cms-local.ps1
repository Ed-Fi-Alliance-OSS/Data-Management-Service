#!/usr/bin/env pwsh
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Builds and starts the Configuration Management Service (CMS) locally in Release mode
.DESCRIPTION
    This script builds the CMS frontend in Release configuration and starts it on port 8081.
    CMS must be started before DMS as DMS depends on CMS for authentication.

    Configuration:
    - Database: PostgreSQL on localhost:5432
    - Database Name: edfi_configurationservice
    - Port: 8081
    - Identity Provider: self-contained (OpenIddict)
    - Environment: Development (uses appsettings.Development.json)

.PARAMETER Port
    The port to run CMS on (default: 8081)

.PARAMETER NoBuild
    Skip the build step and just run the existing build

.PARAMETER Watch
    Run in watch mode (Development configuration only)
#>

[CmdletBinding()]
param(
    [int]$Port = 8081,
    [switch]$NoBuild,
    [switch]$Watch
)

$ErrorActionPreference = "Stop"

Write-Host @"
╔══════════════════════════════════════════════════════════════════════════╗
║  Ed-Fi Configuration Management Service (CMS) - Local Startup            ║
╚══════════════════════════════════════════════════════════════════════════╝
"@ -ForegroundColor Cyan

$projectPath = Join-Path $PSScriptRoot "src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore"
$projectFile = Join-Path $projectPath "EdFi.DmsConfigurationService.Frontend.AspNetCore.csproj"

if (-not (Test-Path $projectFile)) {
    Write-Error "Project file not found at: $projectFile"
    exit 1
}

# Check PostgreSQL connectivity
Write-Host "`nChecking PostgreSQL connectivity..." -ForegroundColor Yellow
try {
    $env:PGPASSWORD = "postgres"
    $pgCheck = & psql -h localhost -p 5432 -U postgres -d edfi_configurationservice -c "SELECT 1" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Cannot connect to PostgreSQL"
    }
    Write-Host "✓ PostgreSQL is accessible" -ForegroundColor Green
}
catch {
    Write-Error @"
Cannot connect to PostgreSQL on localhost:5432
Please ensure:
1. PostgreSQL is running
2. Database 'edfi_configurationservice' exists
3. User 'postgres' with password 'postgres' has access

Error: $_
"@
    exit 1
}
finally {
    $env:PGPASSWORD = $null
}

# Check and initialize OpenIddict keys if needed
Write-Host "Checking OpenIddict initialization..." -ForegroundColor Yellow
try {
    $env:PGPASSWORD = "postgres"
    $keyCheck = & psql -h localhost -p 5432 -U postgres -d edfi_configurationservice -t -c "SELECT COUNT(*) FROM dmscs.OpenIddictKey WHERE IsActive = TRUE;" 2>&1

    if ($LASTEXITCODE -eq 0 -and $keyCheck.Trim() -eq "0") {
        Write-Host "⚠ OpenIddict keys not found. Initializing..." -ForegroundColor Yellow

        # Import crypto module to generate keys
        $cryptoModulePath = Join-Path $PSScriptRoot "eng/docker-compose/OpenIddict-Crypto.psm1"
        Import-Module $cryptoModulePath -Force

        # Create schema and table if needed
        & psql -h localhost -p 5432 -U postgres -d edfi_configurationservice -c "CREATE SCHEMA IF NOT EXISTS dmscs;" 2>&1 | Out-Null
        & psql -h localhost -p 5432 -U postgres -d edfi_configurationservice -c @"
CREATE TABLE IF NOT EXISTS dmscs.OpenIddictKey (
    Id SERIAL PRIMARY KEY,
    KeyId VARCHAR(64) NOT NULL,
    PublicKey BYTEA NOT NULL,
    PrivateKey TEXT NOT NULL,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    ExpiresAt TIMESTAMP,
    IsActive BOOLEAN NOT NULL DEFAULT TRUE
);
"@ 2>&1 | Out-Null

        & psql -h localhost -p 5432 -U postgres -d edfi_configurationservice -c "CREATE EXTENSION IF NOT EXISTS pgcrypto;" 2>&1 | Out-Null

        # Generate and insert key
        $encryptionKey = "QWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXo0NTY3ODkwMTIz"
        $keyInsertSql = New-OpenIddictKeyInsertSql -EncryptionKey $encryptionKey
        & psql -h localhost -p 5432 -U postgres -d edfi_configurationservice -c $keyInsertSql 2>&1 | Out-Null

        Write-Host "✓ OpenIddict keys initialized" -ForegroundColor Green
    }
    else {
        Write-Host "✓ OpenIddict keys already exist" -ForegroundColor Green
    }
}
catch {
    Write-Warning "Could not check/initialize OpenIddict keys: $_"
    Write-Host "You may need to run setup-openiddict.ps1 manually" -ForegroundColor Yellow
}
finally {
    $env:PGPASSWORD = $null
}

# Build the project
if (-not $NoBuild) {
    Write-Host "`nBuilding CMS in Release mode..." -ForegroundColor Yellow
    Push-Location $projectPath
    try {
        dotnet build --configuration Release
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
        Write-Host "✓ Build successful" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}

# Set environment variables
$env:ASPNETCORE_ENVIRONMENT = "Development"

Write-Host @"

Starting CMS...
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Configuration:
  • Environment:    Development
  • Port:           $Port
  • Database:       PostgreSQL (localhost:5432/edfi_configurationservice)
  • Identity:       Self-contained (OpenIddict)
  • Endpoints:
    - Health:       http://localhost:$Port/health
    - Token:        http://localhost:$Port/connect/token
    - OIDC Config:  http://localhost:$Port/.well-known/openid-configuration
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

"@ -ForegroundColor Cyan

Push-Location $projectPath
try {
    if ($Watch) {
        Write-Host "Running in watch mode..." -ForegroundColor Yellow
        dotnet watch run --configuration Development --no-build --urls "http://localhost:$Port"
    }
    else {
        dotnet run --configuration Release --no-build --urls "http://localhost:$Port"
    }
}
finally {
    Pop-Location
    $env:ASPNETCORE_ENVIRONMENT = $null
}
