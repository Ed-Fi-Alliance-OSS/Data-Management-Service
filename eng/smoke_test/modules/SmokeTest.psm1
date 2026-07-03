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

        [ValidateSet("EdFi.Api.TestSdk", "EdFi.Api.Sdk", "EdFi.OdsApi.Sdk")]
        [string]
        $SdkNamespace = "EdFi.Api.TestSdk",

        # Explicit OAuth token URL for the smoke test console. Without it the console
        # resolves the token endpoint from the DMS discovery document, which only works
        # when the advertised endpoint is reachable from the console's host. Stacks that
        # advertise a Docker-internal endpoint must pass the host-reachable DMS token
        # proxy here (e.g. http://localhost:8080/oauth/token); explicit values win over
        # discovery in the console.
        [string]
        $OAuthUrl
    )

    $options = @(
        "-b", $BaseUrl,
        "-k", $Key,
        "-s", $Secret,
        "-t", $TestSet,
        "-c", $SdkNamespace
    )

    if ($OAuthUrl) {
        $options += @("-o", $OAuthUrl)
    }

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

    $path = (Join-Path -Path ($ToolPath).Trim() -ChildPath "tools/net*/any/EdFi.SmokeTest.Console.dll")
    $path = Resolve-Path $path
    &dotnet $path $options
}

function Get-SmokeTestCredential {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing smoke-test helper intentionally writes progress to the console.')]
    [OutputType([hashtable])]
    [CmdletBinding()]
    param (
        [string]
        [Parameter(Mandatory = $True)]
        $ConfigServiceUrl,

        [string]
        $SysAdminId = "smoke-test-admin",

        [string]
        $SysAdminSecret = "SdfH)98&JkSdfH)98&JkSdfH)98&JkSdfH)9",

        [string]
        $VendorName = "Smoke Test Vendor",

        [string]
        $ApplicationName = "Smoke Test Application",

        [string]
        $ClaimSetName = "EdFiSandbox",

        [long[]]
        $EducationOrganizationIds = @(255901, 19255901, 100000, 200000, 300000),

        [long[]]
        $DataStoreIds = @(),

        [string]
        $Tenant = ""
    )

    Write-Host "Creating smoke test credentials via Configuration Service..."

    try {
        # Step 1: Create system administrator credentials using Dms-Management module
        Write-Host "Creating system administrator credentials..."
        Add-CmsClient -CmsUrl $ConfigServiceUrl -ClientId $SysAdminId -ClientSecret $SysAdminSecret -DisplayName "Smoke Test Administrator"

        # Step 2: Get configuration service token using Dms-Management module
        Write-Host "Obtaining configuration service token..."
        $configToken = Get-CmsToken -CmsUrl $ConfigServiceUrl -ClientId $SysAdminId -ClientSecret $SysAdminSecret

        # Step 3: Get available data stores
        if ($DataStoreIds.Count -gt 0) {
            $dataStoreIds = [long[]]@($DataStoreIds)
            Write-Host "Using supplied data store IDs: $($dataStoreIds -join ', ')"
        }
        else {
            Write-Host "Retrieving available data stores..."
            $dataStores = Get-DataStore -CmsUrl $ConfigServiceUrl -AccessToken $configToken -Tenant $Tenant

            if ($dataStores -and $dataStores.Count -gt 0) {
                $dataStoreIds = @($dataStores[0].id)
                Write-Host "Found data store with ID: $($dataStoreIds[0])"
            }
            else {
                Write-Warning "No data stores found. Application will be created without data store association."
                $dataStoreIds = @()
            }
        }

        # Step 4: Create vendor using Dms-Management module
        Write-Host "Creating vendor..."
        $vendorId = Add-Vendor -CmsUrl $ConfigServiceUrl -Company $VendorName -ContactName "Smoke Test Contact" -ContactEmailAddress "smoketest@example.com" -NamespacePrefixes "uri://ed-fi.org,uri://gbisd.edu,uri://tpdm.ed-fi.org,uri://sample.ed-fi.org" -AccessToken $configToken -Tenant $Tenant

        Write-Host "Vendor created with ID: $vendorId"

        # Step 5: Create application using Dms-Management module
        Write-Host "Creating application..."
        $credentials = Add-Application -CmsUrl $ConfigServiceUrl -ApplicationName $ApplicationName -ClaimSetName $ClaimSetName -VendorId $vendorId -AccessToken $configToken -EducationOrganizationIds $EducationOrganizationIds -DataStoreIds $dataStoreIds -Tenant $Tenant

        $key = $credentials.Key
        $secret = $credentials.Secret

        Write-Host "Application created successfully. Credentials were returned to the caller and were not written to logs."

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

Set-Alias -Name Get-SmokeTestCredentials -Value Get-SmokeTestCredential

Export-ModuleMember -Function * -Alias Get-SmokeTestCredentials
