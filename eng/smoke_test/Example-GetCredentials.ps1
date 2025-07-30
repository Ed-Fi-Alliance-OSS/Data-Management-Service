# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Example script demonstrating how to use the Get-SmokeTestCredentials function.

.DESCRIPTION
    This script shows how to use the new Get-SmokeTestCredentials function
    to automatically create a vendor and application in the Configuration Service
    and obtain the necessary key and secret for smoke testing.

.PARAMETER ConfigServiceUrl
    The URL of the Configuration Service (default: http://localhost:8081)

.EXAMPLE
    .\Example-GetCredentials.ps1
    
    Creates smoke test credentials using the default Configuration Service URL.

.EXAMPLE
    .\Example-GetCredentials.ps1 -ConfigServiceUrl "http://localhost:5126"
    
    Creates smoke test credentials using a custom Configuration Service URL.
#>

param(
    [string]
    $ConfigServiceUrl = "http://localhost:8081"
)

$ErrorActionPreference = "Stop"

# Import the SmokeTest module
Import-Module ./modules/SmokeTest.psm1 -Force

try {
    Write-Host "Creating smoke test credentials..." -ForegroundColor Green
    
    # Get credentials using the new function
    $credentials = Get-SmokeTestCredentials -ConfigServiceUrl $ConfigServiceUrl
    
    Write-Host "`nCredentials created successfully!" -ForegroundColor Green
    Write-Host "Key: $($credentials.Key)" -ForegroundColor Yellow
    Write-Host "Secret: $($credentials.Secret)" -ForegroundColor Yellow
    Write-Host "Vendor ID: $($credentials.VendorId)" -ForegroundColor Cyan
    Write-Host "Application Name: $($credentials.ApplicationName)" -ForegroundColor Cyan
    
    Write-Host "`nYou can now use these credentials for smoke testing:" -ForegroundColor Green
    Write-Host "Example:" -ForegroundColor White
    Write-Host "  ./Invoke-NonDestructiveApiTests.ps1 -BaseUrl `"http://localhost:8080`" -Key `"$($credentials.Key)`" -Secret `"$($credentials.Secret)`""
    
} catch {
    Write-Error "Failed to create smoke test credentials: $($_.Exception.Message)"
    exit 1
}
