# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Import-Module (Join-Path $PSScriptRoot "Dms-Management.psm1") -Force

<#
.SYNOPSIS
    Loads school year types into the Ed-Fi Data Management Service (DMS).

.DESCRIPTION
    Iterates through a range of school years and posts each as a `schoolYearType` entity
    to the DMS API. Marks one year as the current school year. This function helps seed
    the system with school year data typically required before loading other sample data.

.PARAMETER StartYear
    The first school year to load. Defaults to 1991.

.PARAMETER EndYear
    The last school year to load. Defaults to 2037.

.PARAMETER CurrentSchoolYear
    The school year to mark as the current one. If set to 0, it will be automatically calculated based on the current date: if current month is after June, uses next year; otherwise uses current year. Defaults to 0 (auto-calculate).

.PARAMETER DmsUrl
    The base URL of the Data Management Service API.

.PARAMETER DmsToken
    The authentication token used to authorize requests to the DMS API.

.EXAMPLE
    Invoke-SchoolYearLoader -DmsUrl "http://localhost:8080" -DmsToken $token

    Loads school years 1991-2037 with auto-calculated current school year based on current date.

.EXAMPLE
    Invoke-SchoolYearLoader -StartYear 2020 -EndYear 2030 -CurrentSchoolYear 2024 -DmsUrl "http://localhost:8080" -DmsToken $token

    Loads school years 2020-2030 with 2024 explicitly set as the current school year.

.NOTES
    This function requires the helper function `Invoke-Api` from the Dms-Management module to send HTTP requests.
    Each school year is posted as a separate API call to the /data/ed-fi/schoolYearTypes endpoint.
#>
function Invoke-SchoolYearLoader {
    param (
        [int]$StartYear = 1991,
        [int]$EndYear = 2037,
        [int]$CurrentSchoolYear = 0,  # 0 indicates auto-calculate
        [Parameter(Mandatory = $true)]
        [string]$DmsUrl,
        [Parameter(Mandatory = $true)]
        [string]$DmsToken
    )

    # Auto-calculate CurrentSchoolYear if not provided (0 means auto-calculate)
    if ($CurrentSchoolYear -eq 0) {
        $CurrentSchoolYear = Get-CurrentSchoolYear
        Write-Host "Auto-calculated Current School Year: $CurrentSchoolYear (based on current date)" -ForegroundColor Cyan
    }

    Write-Host "Loading school years $StartYear to $EndYear (current: $CurrentSchoolYear)..." -ForegroundColor Yellow

    for ($year = $StartYear; $year -le $EndYear; $year++) {
        $schoolYearType = @{
            schoolYear            = $year
            currentSchoolYear     = ($year -eq $CurrentSchoolYear)
            schoolYearDescription = "$($year - 1)-$year"
        }

        $invokeParams = @{
            Method      = 'Post'
            BaseUrl     = $DmsUrl
            RelativeUrl = 'data/ed-fi/schoolYearTypes'
            ContentType = 'application/json'
            Body        = ($schoolYearType | ConvertTo-Json -Depth 5)
            Headers     = @{ Authorization = "bearer $DmsToken" }
        }

        try {
            Invoke-Api @invokeParams | Out-Null
        }
        catch {
            Write-Error "Failed to load school year $year`: $_"
            throw
        }
    }

    Write-Host
    Write-Host "School Years Loaded Successfully!" -ForegroundColor Green
    Write-Host
}

Export-ModuleMember -Function Invoke-SchoolYearLoader
