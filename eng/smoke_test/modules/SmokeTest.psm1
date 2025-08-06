# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7
$ErrorActionPreference = "Stop"

# Import the Dms-Management module for vendor/application management
Import-Module "$PSScriptRoot/../../Dms-Management.psm1" -Force

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
        $SdkPath,

        [ValidateSet("EdFi.DmsApi.TestSdk", "EdFi.DmsApi.Sdk")]
        [string]
        $SdkNamespace = "EdFi.DmsApi.TestSdk"
    )

    $options = @(
        "-b", $BaseUrl,
        "-k", $Key,
        "-s", $Secret,
        "-t", $TestSet,
        "-c", $SdkNamespace
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
        $SysAdminSecret = "SmokeTest12!",

        [string]
        $VendorName = "Smoke Test Vendor",

        [string]
        $ApplicationName = "Smoke Test Application",

        [string]
        $ClaimSetName = "EdFiSandbox",

        [int[]]
        $EducationOrganizationIds = @(255901, 19255901, 100000, 200000, 300000)
    )

    Write-Host "Creating smoke test credentials via Configuration Service..."

    try {
        # Step 1: Create system administrator credentials using Dms-Management module
        Write-Host "Creating system administrator credentials..."
        Add-CmsClient -CmsUrl $ConfigServiceUrl -ClientId $SysAdminId -ClientSecret $SysAdminSecret -DisplayName "Smoke Test Administrator"

        # Step 2: Get configuration service token using Dms-Management module
        Write-Host "Obtaining configuration service token..."
        $configToken = Get-CmsToken -CmsUrl $ConfigServiceUrl -ClientId $SysAdminId -ClientSecret $SysAdminSecret

        # Step 3: Create vendor using Dms-Management module
        Write-Host "Creating vendor..."
        $vendorId = Add-Vendor -CmsUrl $ConfigServiceUrl -Company $VendorName -ContactName "Smoke Test Contact" -ContactEmailAddress "smoketest@example.com" -NamespacePrefixes "uri://ed-fi.org,uri://gbisd.edu,uri://tpdm.ed-fi.org" -AccessToken $configToken

        Write-Host "Vendor created with ID: $vendorId"

        # Step 4: Create application using Dms-Management module
        Write-Host "Creating application..."
        $credentials = Add-Application -CmsUrl $ConfigServiceUrl -ApplicationName $ApplicationName -ClaimSetName $ClaimSetName -VendorId $vendorId -AccessToken $configToken -EducationOrganizationIds $EducationOrganizationIds

        $key = $credentials.Key
        $secret = $credentials.Secret

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
