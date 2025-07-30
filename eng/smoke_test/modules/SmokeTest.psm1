# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7
$ErrorActionPreference = "Stop"

# Add System.Web assembly for URL encoding
Add-Type -AssemblyName System.Web

function Invoke-SmokeTestUtility {

    [CmdletBinding()]
    param (
        [string]
        [Parameter(Mandatory = $True)]
        $BaseUrl,

        [string]
        [Parameter(Mandatory = $True)]
        $Key,

        [string]
        [Parameter(Mandatory = $True)]
        $Secret,

        [string]
        [Parameter(Mandatory = $True)]
        $ToolPath,

        [ValidateSet("NonDestructiveApi", "NonDestructiveSdk", "DestructiveSdk")]
        [Parameter(Mandatory = $True)]
        $TestSet,

        [string]
        $SdkPath
    )

    $options = @(
        "-b", $BaseUrl,
        "-k", $Key,
        "-s", $Secret,
        "-t", $TestSet
    )

    if($TestSet -ne "NonDestructiveApi")
    {
        if(!$SdkPath)
        {
            Write-Error "Please provide valid SDK path"
            return
        }
        $options += @("-l", $SdkPath)
    }

    $previousForegroundColor = $host.UI.RawUI.ForegroundColor
    $host.UI.RawUI.ForegroundColor = "Cyan"
    Write-Output $ToolPath $options
    $host.UI.RawUI.ForegroundColor = $previousForegroundColor

    $path = (Join-Path -Path ($ToolPath).Trim() -ChildPath "tools/net8.0/any/EdFi.SmokeTest.Console.dll")
    &dotnet $path $options
}

function Get-SmokeTestCredentials {
    [CmdletBinding()]
    param (
        [string]
        [Parameter(Mandatory = $True)]
        $ConfigServiceUrl,

        [string]
        $SysAdminId = "smoke-test-admin",

        [string]
        $SysAdminSecret = "SmokeTest123!",

        [string]
        $VendorName = "Smoke Test Vendor",

        [string]
        $ApplicationName = "Smoke Test Application",

        [string]
        $ClaimSetName = "EdFiSandbox",

        [int[]]
        $EducationOrganizationIds = @(255901, 19255901, 2559011, 200000, 100000, 300000, 255901044, 255901001, 255901107, 51, 54)
    )

    Write-Host "Creating smoke test credentials via Configuration Service..."

    # URL encode the secret for form data
    $encodedSysAdminSecret = [System.Web.HttpUtility]::UrlEncode($SysAdminSecret)

    try {
        # Step 1: Create system administrator credentials
        Write-Host "Creating system administrator credentials..."
        $registerBody = "ClientId=$SysAdminId&ClientSecret=$encodedSysAdminSecret&DisplayName=Smoke Test Administrator"
        
        Invoke-RestMethod -Uri "$ConfigServiceUrl/connect/register" `
            -Method Post `
            -ContentType "application/x-www-form-urlencoded" `
            -Body $registerBody `
            -ErrorAction SilentlyContinue | Out-Null

        # Step 2: Get configuration service token
        Write-Host "Obtaining configuration service token..."
        $tokenBody = "client_id=$SysAdminId&client_secret=$encodedSysAdminSecret&grant_type=client_credentials&scope=edfi_admin_api/full_access"
        
        $tokenResponse = Invoke-RestMethod -Uri "$ConfigServiceUrl/connect/token" `
            -Method Post `
            -ContentType "application/x-www-form-urlencoded" `
            -Body $tokenBody

        $configToken = $tokenResponse.access_token

        # Step 3: Create vendor
        Write-Host "Creating vendor..."
        $vendorData = @{
            company = $VendorName
            contactName = "Smoke Test Contact"
            contactEmailAddress = "smoketest@example.com"
            namespacePrefixes = "uri://ed-fi.org,uri://gbisd.edu,uri://tpdm.ed-fi.org"
        } | ConvertTo-Json

        $vendorResponse = Invoke-RestMethod -Uri "$ConfigServiceUrl/v2/vendors" `
            -Method Post `
            -ContentType "application/json" `
            -Headers @{ Authorization = "bearer $configToken" } `
            -Body $vendorData

        # Extract vendor ID from location header or response
        if ($vendorResponse.PSObject.Properties.Name -contains "id") {
            $vendorId = $vendorResponse.id
        } else {
            # If vendor already exists, try to get it by name
            $existingVendors = Invoke-RestMethod -Uri "$ConfigServiceUrl/v2/vendors" `
                -Method Get `
                -Headers @{ Authorization = "bearer $configToken" }
            
            $existingVendor = $existingVendors | Where-Object { $_.company -eq $VendorName }
            if ($existingVendor) {
                $vendorId = $existingVendor.id
            } else {
                throw "Failed to create or find vendor"
            }
        }

        Write-Host "Vendor created with ID: $vendorId"

        # Step 4: Create application
        Write-Host "Creating application..."
        $applicationData = @{
            vendorId = $vendorId
            applicationName = $ApplicationName
            claimSetName = $ClaimSetName
            educationOrganizationIds = $EducationOrganizationIds
        } | ConvertTo-Json

        $applicationResponse = Invoke-RestMethod -Uri "$ConfigServiceUrl/v2/applications" `
            -Method Post `
            -ContentType "application/json" `
            -Headers @{ Authorization = "bearer $configToken" } `
            -Body $applicationData

        $key = $applicationResponse.key
        $secret = $applicationResponse.secret

        Write-Host "Application created successfully!"
        Write-Host "Key: $key"
        Write-Host "Secret: $secret"

        return @{
            Key = $key
            Secret = $secret
            VendorId = $vendorId
            ApplicationName = $ApplicationName
        }
    }
    catch {
        Write-Error "Failed to create smoke test credentials: $($_.Exception.Message)"
        throw
    }
}

Export-ModuleMember *
