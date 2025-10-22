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

    # Step 5: Create vendor for data loading
    Write-Host "`n5. Creating data load vendor..." -ForegroundColor Yellow
    try {
        $vendorId = Add-Vendor `
            -CmsUrl $cmsUrl `
            -Company "Data Load Vendor" `
            -ContactName "Data Loader" `
            -ContactEmailAddress "dataload@example.com" `
            -NamespacePrefixes "uri://ed-fi.org" `
            -AccessToken $adminToken

        Write-Host "   ✓ Vendor created with ID: $vendorId" -ForegroundColor Green
    }
    catch {
        if ($_ -match "already exists" -or $_ -match "409") {
            Write-Host "   ℹ Vendor already exists, retrieving existing vendor..." -ForegroundColor Gray
            # If vendor exists, we need to get its ID - for now, assume ID 1
            $vendorId = 1
        }
        else {
            Write-Warning "Failed to create vendor: $_"
            throw
        }
    }

    # Step 6: Create data load application with EdfiSandbox ClaimSet
    Write-Host "`n6. Creating data load application..." -ForegroundColor Yellow
    try {
        $dataloadCreds = Add-Application `
            -CmsUrl $cmsUrl `
            -VendorId $vendorId `
            -ApplicationName "Data Load Application" `
            -ClaimSetName "EdfiSandbox" `
            -EducationOrganizationIds @(255901, 19255901) `
            -AccessToken $adminToken

        Write-Host "   ✓ Data load application created" -ForegroundColor Green
        Write-Host "   Key: $($dataloadCreds.Key)" -ForegroundColor Gray
        Write-Host "   Secret: $($dataloadCreds.Secret)" -ForegroundColor Gray
    }
    catch {
        if ($_ -match "already exists" -or $_ -match "409") {
            Write-Host "   ℹ Data load application already exists" -ForegroundColor Gray
            Write-Warning "Please use existing credentials from dataload-creds.json"
            $dataloadCreds = $null
        }
        else {
            Write-Warning "Failed to create data load application: $_"
            throw
        }
    }

    # Step 7: Save credentials to dataload-creds.json
    if ($dataloadCreds) {
        Write-Host "`n7. Saving credentials to dataload-creds.json..." -ForegroundColor Yellow
        $credsFilePath = Join-Path $PSScriptRoot "dataload-creds.json"
        $credsObject = @{
            key = $dataloadCreds.Key
            secret = $dataloadCreds.Secret
            claimSet = "EdfiSandbox"
            namespacePrefixes = "uri://ed-fi.org"
            educationOrganizationIds = @(255901, 19255901)
        }
        $credsObject | ConvertTo-Json -Depth 10 | Set-Content -Path $credsFilePath
        Write-Host "   ✓ Credentials saved to dataload-creds.json" -ForegroundColor Green
    }

    Write-Host "`n✅ CMS initialization complete!" -ForegroundColor Green
    Write-Host "`nYou can now start DMS using:" -ForegroundColor Cyan
    Write-Host "  ./start-dms-local.ps1" -ForegroundColor White
    if ($dataloadCreds) {
        Write-Host "`nData load credentials are available in dataload-creds.json" -ForegroundColor Cyan
    }
}
catch {
    Write-Error "Initialization failed: $_"
    Write-Host "`nStack trace:" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
    exit 1
}
