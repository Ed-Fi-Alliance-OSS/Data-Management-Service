#!/usr/bin/env pwsh
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Initializes CMS with required clients and DMS instance for local development
.DESCRIPTION
    This script creates the necessary API clients and DMS instance configuration
    in CMS to allow DMS to connect and authenticate properly.
#>

$ErrorActionPreference = "Stop"

Write-Host "Initializing CMS configuration for local DMS..." -ForegroundColor Cyan

# Import the management module
$modulePath = Join-Path $PSScriptRoot "eng/Dms-Management.psm1"
Import-Module $modulePath -Force

$cmsUrl = "http://localhost:8081"

try {
    # Step 1: Create the initial admin client for setup
    Write-Host "`n1. Creating initial admin client..." -ForegroundColor Yellow
    try {
        Add-CmsClient -CmsUrl $cmsUrl -ClientId "dms-local-admin" -ClientSecret "LocalSetup1!" -DisplayName "Local DMS Setup Administrator"
        Write-Host "   ✓ Admin client created" -ForegroundColor Green
    }
    catch {
        if ($_ -match "already exists" -or $_ -match "409") {
            Write-Host "   ℹ Admin client already exists" -ForegroundColor Gray
        }
        else {
            throw
        }
    }

    # Step 2: Get token for admin client
    Write-Host "`n2. Obtaining admin token..." -ForegroundColor Yellow
    $adminToken = Get-CmsToken -CmsUrl $cmsUrl -ClientId "dms-local-admin" -ClientSecret "LocalSetup1!"
    Write-Host "   ✓ Token obtained" -ForegroundColor Green

    # Step 3: Create the DMS auth metadata client using the registration endpoint
    Write-Host "`n3. Creating DMS auth metadata client..." -ForegroundColor Yellow
    try {
        Add-CmsClient -CmsUrl $cmsUrl -ClientId "CMSAuthMetadataReadOnlyAccess" -ClientSecret "s3creT@09" -DisplayName "DMS Auth Metadata Reader"
        Write-Host "   ✓ DMS auth client created" -ForegroundColor Green
    }
    catch {
        if ($_ -match "already exists" -or $_ -match "409") {
            Write-Host "   ℹ DMS auth client already exists" -ForegroundColor Gray
        }
        else {
            Write-Warning "Failed to create DMS auth client: $_"
        }
    }

    # Step 4: Create DMS instance
    Write-Host "`n4. Creating DMS instance..." -ForegroundColor Yellow
    try {
        $instanceId = Add-DmsInstance `
            -CmsUrl $cmsUrl `
            -AccessToken $adminToken `
            -PostgresPassword "postgres" `
            -PostgresDbName "edfi_datamanagementservice" `
            -PostgresHost "localhost" `
            -PostgresPort 5432 `
            -PostgresUser "postgres" `
            -InstanceName "Local Development Instance" `
            -InstanceType "Development"

        Write-Host "   ✓ DMS Instance created with ID: $instanceId" -ForegroundColor Green
    }
    catch {
        if ($_ -match "already exists" -or $_ -match "409") {
            Write-Host "   ℹ DMS instance already exists" -ForegroundColor Gray
        }
        else {
            Write-Warning "Failed to create DMS instance: $_"
        }
    }

    Write-Host "`n✅ CMS initialization complete!" -ForegroundColor Green
    Write-Host "`nYou can now start DMS using:" -ForegroundColor Cyan
    Write-Host "  ./start-dms-local.ps1" -ForegroundColor White
}
catch {
    Write-Error "Initialization failed: $_"
    Write-Host "`nStack trace:" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
    exit 1
}
