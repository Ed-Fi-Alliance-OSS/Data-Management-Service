#!/usr/bin/env pwsh
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Builds and starts the Data Management Service (DMS) locally in Release mode
.DESCRIPTION
    This script builds the DMS frontend in Release configuration and starts it on port 8080.
    DMS depends on CMS for authentication, so CMS must be running first.

    Configuration:
    - Database: PostgreSQL on localhost:5432
    - Database Name: edfi_datamanagementservice
    - Port: 8080
    - Authentication: Via CMS on localhost:8081
    - Environment: Development (uses appsettings.Development.json)

.PARAMETER Port
    The port to run DMS on (default: 8080)

.PARAMETER NoBuild
    Skip the build step and just run the existing build

.PARAMETER Watch
    Run in watch mode (Development configuration only)
#>

[CmdletBinding()]
param(
    [int]$Port = 8080,
    [switch]$NoBuild,
    [switch]$Watch
)

$ErrorActionPreference = "Stop"

Write-Host @"
╔══════════════════════════════════════════════════════════════════════════╗
║  Ed-Fi Data Management Service (DMS) - Local Startup                     ║
╚══════════════════════════════════════════════════════════════════════════╝
"@ -ForegroundColor Cyan

$projectPath = Join-Path $PSScriptRoot "src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore"
$projectFile = Join-Path $projectPath "EdFi.DataManagementService.Frontend.AspNetCore.csproj"

if (-not (Test-Path $projectFile)) {
    Write-Error "Project file not found at: $projectFile"
    exit 1
}

# Check PostgreSQL connectivity
Write-Host "`nChecking PostgreSQL connectivity..." -ForegroundColor Yellow
try {
    $env:PGPASSWORD = "postgres"
    $pgCheck = & psql -h localhost -p 5432 -U postgres -d edfi_datamanagementservice -c "SELECT 1" 2>&1
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
2. Database 'edfi_datamanagementservice' exists
3. User 'postgres' with password 'postgres' has access

Error: $_
"@
    exit 1
}
finally {
    $env:PGPASSWORD = $null
}

# Check CMS connectivity
Write-Host "Checking CMS connectivity..." -ForegroundColor Yellow
try {
    $cmsHealth = Invoke-WebRequest -Uri "http://localhost:8081/health" -TimeoutSec 5 -UseBasicParsing 2>&1
    if ($cmsHealth.StatusCode -eq 200) {
        Write-Host "✓ CMS is running and healthy" -ForegroundColor Green
    }
}
catch {
    Write-Warning @"
Cannot connect to CMS on http://localhost:8081
DMS requires CMS to be running for authentication.

Please start CMS first using:
  ./start-cms-local.ps1

Then start DMS in a separate terminal.
"@
    $response = Read-Host "Do you want to continue anyway? (y/N)"
    if ($response -ne 'y' -and $response -ne 'Y') {
        exit 1
    }
}

# Build the project
if (-not $NoBuild) {
    Write-Host "`nBuilding DMS in Release mode..." -ForegroundColor Yellow
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

Starting DMS...
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
Configuration:
  • Environment:    Development
  • Port:           $Port
  • Database:       PostgreSQL (localhost:5432/edfi_datamanagementservice)
  • Query Handler:  PostgreSQL
  • Authentication: CMS at http://localhost:8081
  • Endpoints:
    - Health:       http://localhost:$Port/health
    - Metadata:     http://localhost:$Port/metadata
    - Data:         http://localhost:$Port/data
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
