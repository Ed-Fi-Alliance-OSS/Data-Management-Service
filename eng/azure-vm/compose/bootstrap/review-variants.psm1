# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Review-variant applications for the security-review environment: four claim-set / EdOrg /
# namespace combinations added to each of the three deployments (single-tenant, tenant1,
# tenant2), on top of the three EdFiSandbox applications bootstrap.ps1 creates.

# No -Force: reuse an already-loaded Dms-Management (bootstrap.ps1 imports it first), so the
# -Insecure certificate defaults set on that instance also cover the calls made from here.
Import-Module (Join-Path $PSScriptRoot "../../../Dms-Management.psm1")

function Read-ReviewEnvFile {
    <#
    .SYNOPSIS
        Reads compose/.env the way bootstrap.ps1 does: KEY=VALUE lines, comments skipped, one pair
        of matching surrounding quotes stripped (docker compose strips them too).
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { throw "Env file not found: $Path" }
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ($trimmed -eq "" -or $trimmed.StartsWith("#")) { continue }
        $index = $trimmed.IndexOf("=")
        if ($index -lt 1) { continue }
        $value = $trimmed.Substring($index + 1).Trim()
        if ($value.Length -ge 2 -and (($value[0] -eq '"' -and $value[-1] -eq '"') -or ($value[0] -eq "'" -and $value[-1] -eq "'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $values[$trimmed.Substring(0, $index).Trim()] = $value
    }
    return $values
}

function Get-ReviewVariant {
    <#
    .SYNOPSIS
        Returns the claim-set / EdOrg / namespace variants every deployment receives.
    .DESCRIPTION
        Grand Bend ISD is LEA 255901; 255901107 is one of its schools. The AssessmentVendor
        variant uses a namespace outside the sample data on purpose, so namespace-based
        authorization has nothing to grant it.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param()

    return @(
        @{ Name = "SISVendor District"; ClaimSet = "SISVendor"; EducationOrganizationIds = @([long]255901); NamespacePrefixes = "uri://ed-fi.org/" },
        @{ Name = "SISVendor School"; ClaimSet = "SISVendor"; EducationOrganizationIds = @([long]255901107); NamespacePrefixes = "uri://ed-fi.org/" },
        @{ Name = "AssessmentVendor District"; ClaimSet = "AssessmentVendor"; EducationOrganizationIds = @([long]255901); NamespacePrefixes = "uri://one.example.com" },
        @{ Name = "EdFiSandbox District"; ClaimSet = "EdFiSandbox"; EducationOrganizationIds = @([long]255901); NamespacePrefixes = "uri://ed-fi.org/" }
    )
}

function Get-ReviewDeployment {
    <#
    .SYNOPSIS
        Returns the three deployments with the CMS URL, tenant, and data store name bootstrap.ps1 uses.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)][string]$BaseUrl,
        [string]$SchoolYear = "2025",
        [string]$Tenant1 = "tenant1",
        [string]$Tenant2 = "tenant2"
    )

    $base = $BaseUrl.TrimEnd("/")
    return @(
        @{ Label = "single-tenant"; Code = "ST"; CmsUrl = "$base/st-config/"; Tenant = ""; DataStoreName = "Single-Tenant Data Store"; DmsUrl = "http://st-dms:8080/st-dms" },
        @{ Label = "multi-tenant/$Tenant1"; Code = "T1"; CmsUrl = "$base/mt-config/"; Tenant = $Tenant1; DataStoreName = "MT Data Store ($Tenant1 $SchoolYear)"; DmsUrl = "http://mt-dms:8080/mt-dms/$Tenant1/$SchoolYear" },
        @{ Label = "multi-tenant/$Tenant2"; Code = "T2"; CmsUrl = "$base/mt-config/"; Tenant = $Tenant2; DataStoreName = "MT Data Store ($Tenant2 $SchoolYear)"; DmsUrl = "http://mt-dms:8080/mt-dms/$Tenant2/$SchoolYear" }
    )
}

function Add-ReviewVariantSet {
    <#
    .SYNOPSIS
        Creates the review-variant applications in each deployment and returns their credentials.
    .DESCRIPTION
        Re-runnable: a variant whose application already exists is skipped with a warning (its
        secret cannot be read back; reset it through the CMS if it was lost), and an existing
        vendor is reused because POST /v3/vendors rejects a repeated company.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject[]])]
    param(
        [Parameter(Mandatory)][hashtable[]]$Deployment,
        [Parameter(Mandatory)][string]$AdminClientId,
        [Parameter(Mandatory)][string]$AdminClientSecret,
        [hashtable[]]$Variant = (Get-ReviewVariant)
    )

    # Resolve every deployment's data store first, so a missing one fails before anything is created.
    $plans = foreach ($d in $Deployment) {
        $token = Get-CmsToken -CmsUrl $d.CmsUrl -ClientId $AdminClientId -ClientSecret $AdminClientSecret
        $headers = @{ Authorization = "Bearer $token" }
        if ($d.Tenant) { $headers["Tenant"] = $d.Tenant }

        # Invoke-RestMethod emits a JSON array as ONE object, so filter a variable, never
        # @(Invoke-RestMethod ...) directly: that form tests the whole array and passes every row.
        $allDataStores = Invoke-RestMethod -Uri "$($d.CmsUrl)v3/dataStores?offset=0&limit=500" -Headers $headers
        $dataStore = @($allDataStores | Where-Object { $_.name -eq $d.DataStoreName })
        if ($dataStore.Count -ne 1) {
            throw "Expected exactly one data store named '$($d.DataStoreName)' in $($d.Label), found $($dataStore.Count)."
        }
        @{ Deployment = $d; Token = $token; Headers = $headers; DataStoreId = [long]$dataStore[0].id }
    }

    $created = [System.Collections.Generic.List[object]]::new()
    foreach ($plan in $plans) {
        $d = $plan.Deployment
        $token = $plan.Token
        $headers = $plan.Headers
        $allApplications = Invoke-RestMethod -Uri "$($d.CmsUrl)v3/applications?offset=0&limit=500" -Headers $headers
        $existingNames = @($allApplications | ForEach-Object { $_.applicationName })
        $allVendors = Invoke-RestMethod -Uri "$($d.CmsUrl)v3/vendors?offset=0&limit=500" -Headers $headers

        foreach ($v in $Variant) {
            # dmscs.ApiClient.Name copies the application name and is VARCHAR(50).
            $applicationName = "Security Review $($d.Code) $($v.Name)"
            if ($applicationName.Length -gt 50) {
                throw "Application name '$applicationName' is $($applicationName.Length) characters; the limit is 50."
            }
            if ($existingNames -contains $applicationName) {
                Write-Warning "'$applicationName' already exists in $($d.Label); skipped."
                continue
            }

            $company = "Security Review Vendor ($($d.Code) $($v.Name))"
            $vendor = @($allVendors | Where-Object { $_.company -eq $company })
            $vendorId = if ($vendor.Count -eq 1) { $vendor[0].id } else {
                Add-Vendor -CmsUrl $d.CmsUrl -Company $company -NamespacePrefixes $v.NamespacePrefixes -AccessToken $token -Tenant $d.Tenant
            }
            $application = Add-Application -CmsUrl $d.CmsUrl -ApplicationName $applicationName -ClaimSetName $v.ClaimSet `
                -VendorId $vendorId -AccessToken $token -EducationOrganizationIds $v.EducationOrganizationIds `
                -DataStoreIds @($plan.DataStoreId) -Tenant $d.Tenant
            $created.Add([pscustomobject]@{
                    Environment = $d.Label
                    Application = $applicationName
                    ClaimSet    = $v.ClaimSet
                    EdOrgIds    = ($v.EducationOrganizationIds -join ",")
                    Namespace   = $v.NamespacePrefixes
                    Key         = $application.Key
                    Secret      = $application.Secret
                })
        }
    }

    return $created.ToArray()
}

Export-ModuleMember -Function Read-ReviewEnvFile, Get-ReviewVariant, Get-ReviewDeployment, Add-ReviewVariantSet
