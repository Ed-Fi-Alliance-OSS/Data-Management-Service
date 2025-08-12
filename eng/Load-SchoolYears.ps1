#!/usr/bin/env pwsh
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Standalone script to load school year data into the Ed-Fi Data Management Service (DMS).

.DESCRIPTION
    This script loads school year types into the DMS API. It provides a simple command-line
    interface for loading school years without needing to use the full template management
    system. The script handles authentication and provides detailed feedback during loading.

.PARAMETER StartYear
    The first school year to load. Defaults to 1991.

.PARAMETER EndYear
    The last school year to load. Defaults to 2037.

.PARAMETER CurrentSchoolYear
    Optional. The school year to mark as the current one. If not provided, it will be automatically calculated based on the current date: if current month is after June, uses next year; otherwise uses current year.

.PARAMETER DmsUrl
    The base URL of the Data Management Service API. Defaults to http://localhost:8080.

.PARAMETER CmsUrl
    The base URL of the Configuration Management Service API. Defaults to http://localhost:8081.

.PARAMETER ClaimSetName
    The claim set name to use for authentication. Defaults to 'BootstrapDescriptorsandEdOrgs'.

.EXAMPLE
    .\Load-SchoolYears.ps1

    Loads school years 1991-2037 using default settings (current year: 2025).

.EXAMPLE
    .\Load-SchoolYears.ps1 -StartYear 2020 -EndYear 2030 -CurrentSchoolYear 2024

    Loads school years 2020-2030 with 2024 as the current school year.

.EXAMPLE
    .\Load-SchoolYears.ps1 -DmsUrl "https://api.example.com" -CmsUrl "https://config.example.com"

    Loads school years using remote DMS and CMS URLs.

.NOTES
    This script requires:
    - PowerShell 7 or later
    - Network access to both DMS and CMS services
    - The CMS service must be running and accessible for authentication
    - The DMS service must be running and accessible for data loading
#>

param(
    [int]$StartYear = 1991,
    [int]$EndYear = 2037,
    [int]$CurrentSchoolYear = 0,  # 0 indicates auto-calculate
    [string]$DmsUrl = "http://localhost:8080",
    [string]$CmsUrl = "http://localhost:8081",
    [string]$ClaimSetName = 'BootstrapDescriptorsandEdOrgs'
)

#Requires -Version 7

try {
    Write-Host "Ed-Fi Data Management Service - School Year Loader" -ForegroundColor Cyan
    Write-Host

    # Validate parameters
    if ($StartYear -le 0) {
        throw "StartYear must be a positive integer"
    }
    if ($EndYear -le 0) {
        throw "EndYear must be a positive integer"
    }
    if ($StartYear -gt $EndYear) {
        throw "StartYear ($StartYear) cannot be greater than EndYear ($EndYear)"
    }

    Import-Module (Join-Path $PSScriptRoot "Dms-Management.psm1") -Force

    # Auto-calculate CurrentSchoolYear if not provided (0 means auto-calculate)
    if ($CurrentSchoolYear -eq 0) {
        $CurrentSchoolYear = Get-CurrentSchoolYear
        Write-Host "Auto-calculated Current School Year: $CurrentSchoolYear (based on current date)" -ForegroundColor Cyan
    } else {
        # Validate explicitly provided CurrentSchoolYear
        if ($CurrentSchoolYear -le 0) {
            throw "CurrentSchoolYear must be a positive integer"
        }
    }

    # Validate that CurrentSchoolYear is within the range
    if ($CurrentSchoolYear -lt $StartYear -or $CurrentSchoolYear -gt $EndYear) {
        throw "CurrentSchoolYear ($CurrentSchoolYear) must be between StartYear ($StartYear) and EndYear ($EndYear)"
    }

    Write-Host "Configuration:" -ForegroundColor Yellow
    Write-Host "  DMS URL: $DmsUrl"
    Write-Host "  CMS URL: $CmsUrl"
    Write-Host "  School Years: $StartYear - $EndYear"
    Write-Host "  Current School Year: $CurrentSchoolYear"
    Write-Host "  Claim Set: $ClaimSetName"
    Write-Host

    # Step 1: Setup CMS client and get token
    Write-Host "Step 1: Connecting to Configuration Management Service..." -ForegroundColor Yellow
    Add-CmsClient -CmsUrl $CmsUrl
    $cmsToken = Get-CmsToken -CmsUrl $CmsUrl
    Write-Host "  ✓ CMS connection established" -ForegroundColor Green

    # Step 2: Get DMS authentication credentials
    Write-Host "Step 2: Getting DMS authentication credentials..." -ForegroundColor Yellow

    # Create vendor and application to get key/secret
    $vendorId = Add-Vendor -CmsUrl $CmsUrl -AccessToken $cmsToken
    $credentials = Add-Application -CmsUrl $CmsUrl -VendorId $vendorId -AccessToken $cmsToken -ClaimSetName $ClaimSetName

    $dmsToken = Get-DmsToken -DmsUrl $DmsUrl -Key $credentials.Key -Secret $credentials.Secret
    Write-Host "  ✓ DMS authentication established" -ForegroundColor Green

    # Step 3: Load school years
    Import-Module (Join-Path $PSScriptRoot "SchoolYear-Loader.psm1") -Force
    Write-Host "Step 3: Loading school year data..." -ForegroundColor Yellow
    Invoke-SchoolYearLoader -StartYear $StartYear -EndYear $EndYear -CurrentSchoolYear $CurrentSchoolYear -DmsUrl $DmsUrl -DmsToken $dmsToken

    Write-Host "School year loading completed successfully!" -ForegroundColor Green
    Write-Host
}
catch {
    Write-Host
    Write-Host "ERROR: $_" -ForegroundColor Red
    Write-Host
    exit 1
}
