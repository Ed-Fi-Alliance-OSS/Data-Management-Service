# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Bootstrap entry script intentionally writes operator progress to the console.')]
[CmdletBinding()]
param(
    [string]$EnvironmentFile,
    [switch]$NoDmsInstance,
    [string]$SchoolYearRange = "",
    [switch]$AddSmokeTestCredentials
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Import-Module "$PSScriptRoot/bootstrap-manifest.psm1" -Force -Global
Import-Module "$PSScriptRoot/env-utility.psm1" -Force -Global
Import-Module "$PSScriptRoot/../Dms-Management.psm1" -Force

$script:BootstrapDmsInstanceClientId = "dms-instance-admin"
$script:BootstrapDmsInstanceClientSecret = "ValidClientSecret1234567890!Abcd"

if (-not (Get-Command Format-LogSafeText -ErrorAction SilentlyContinue)) {
    function Format-LogSafeText {
        param($Value)

        if ($null -eq $Value) { return "" }
        $text = [string]$Value
        $builder = [System.Text.StringBuilder]::new()
        foreach ($character in $text.ToCharArray()) {
            if ([char]::IsLetterOrDigit($character) -or
                $character -eq " " -or
                $character -eq "_" -or
                $character -eq "-" -or
                $character -eq "." -or
                $character -eq ":" -or
                $character -eq "/") {
                $null = $builder.Append($character)
            }
        }

        return $builder.ToString()
    }
}

function Resolve-ConfigureEnvironmentFile {
    param(
        [string]
        $Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        $defaultEnv = Join-Path $PSScriptRoot ".env"
        $fallbackEnv = Join-Path $PSScriptRoot ".env.example"
        $Path = if (Test-Path -LiteralPath $defaultEnv) { $defaultEnv } else { $fallbackEnv }
    }
    elseif (-not [System.IO.Path]::IsPathRooted($Path)) {
        $Path = [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path))
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Environment file not found: $(Format-LogSafeText $Path)."
    }

    return [System.IO.Path]::GetFullPath($Path)
}

function Get-EnvValueOrDefault {
    param(
        [hashtable]
        $EnvValues,

        [string]
        $Name,

        [string]
        $DefaultValue = ""
    )

    if ($EnvValues.ContainsKey($Name) -and -not [string]::IsNullOrWhiteSpace([string]$EnvValues[$Name])) {
        return [string]$EnvValues[$Name]
    }

    return $DefaultValue
}

function Get-DmsInstanceRouteContexts {
    param(
        $Instance
    )

    $property = $Instance.PSObject.Properties["dmsInstanceRouteContexts"]
    if ($null -eq $property -or $null -eq $property.Value -or $property.Value -is [string]) {
        return @()
    }

    return @($property.Value)
}

function ConvertTo-ConfigureResult {
    param(
        [long[]]
        $InstanceIds = @(),

        [object[]]
        $RouteContexts = @(),

        [string]
        $Tenant = "",

        [int[]]
        $SchoolYears = @()
    )

    return [pscustomobject]@{
        InstanceIds = [long[]]@($InstanceIds)
        RouteContexts = @($RouteContexts)
        Tenant = $Tenant
        SchoolYears = [int[]]@($SchoolYears)
        HasRouteQualifiedInstances = (@($RouteContexts).Count -gt 0)
    }
}

function Resolve-SchoolYearRange {
    param(
        [string]
        $Range
    )

    if ([string]::IsNullOrWhiteSpace($Range)) {
        return [int[]]@()
    }

    if ($Range -notmatch '^(\d{4})-(\d{4})$') {
        throw "Invalid -SchoolYearRange '$(Format-LogSafeText $Range)'. Expected StartYear-EndYear (e.g. 2024-2025)."
    }

    $startYear = [int]$Matches[1]
    $endYear = [int]$Matches[2]
    if ($startYear -gt $endYear) {
        throw "Invalid -SchoolYearRange '$(Format-LogSafeText $Range)'. StartYear ($startYear) must be less than or equal to EndYear ($endYear)."
    }

    return [int[]]@($startYear..$endYear)
}

function Get-ExistingCompatibleInstance {
    param(
        [object[]]
        $Instances,

        [string]
        $Tenant
    )

    if ($Instances.Count -eq 0) {
        throw "-NoDmsInstance was supplied, but no existing DMS instances were found in the current tenant scope '$(Format-LogSafeText $Tenant)'. Create one route-unqualified CMS instance, or omit -NoDmsInstance."
    }

    if ($Instances.Count -gt 1) {
        $listing = ($Instances | ForEach-Object {
            "id=$(Format-LogSafeText $_.id) name=$(Format-LogSafeText $_.instanceName)"
        }) -join ", "
        throw "-NoDmsInstance requires exactly one existing DMS instance in tenant scope '$(Format-LogSafeText $Tenant)'. Found $($Instances.Count): $listing. Clean up CMS state or run with explicit configuration inputs."
    }

    $instance = $Instances[0]
    $routeContexts = @(Get-DmsInstanceRouteContexts -Instance $instance)
    if ($routeContexts.Count -gt 0) {
        $contextList = ($routeContexts | ForEach-Object { "$($_.contextKey)=$($_.contextValue)" }) -join ", "
        throw "-NoDmsInstance found one existing instance, but it is route-qualified ($contextList). -NoDmsInstance supports exactly one route-unqualified instance; clean up CMS state or use -SchoolYearRange."
    }

    return $instance
}

function Invoke-ConfigureLocalDmsInstance {
    param(
        [string]
        $EnvironmentFile,

        [switch]
        $NoDmsInstance,

        [string]
        $SchoolYearRange = "",

        [switch]
        $AddSmokeTestCredentials
    )

    $resolvedEnvironmentFile = Resolve-ConfigureEnvironmentFile -Path $EnvironmentFile
    $envValues = ReadValuesFromEnvFile -EnvironmentFile $resolvedEnvironmentFile
    $cmsUrl = Resolve-CmsBaseUrl -EnvValues $envValues
    $tenant = Get-EnvValueOrDefault -EnvValues $envValues -Name "CONFIG_SERVICE_TENANT"
    $schoolYears = @(Resolve-SchoolYearRange -Range $SchoolYearRange)

    if ($NoDmsInstance -and $schoolYears.Count -gt 0) {
        throw "Parameters -NoDmsInstance and -SchoolYearRange are mutually exclusive. Use -NoDmsInstance to select one existing route-unqualified instance, or -SchoolYearRange to configure route-qualified instances."
    }

    $multiTenancyEnabled = (Get-EnvValueOrDefault -EnvValues $envValues -Name "DMS_CONFIG_MULTI_TENANCY").Equals("true", [System.StringComparison]::OrdinalIgnoreCase)
    if ($schoolYears.Count -gt 0 -and $multiTenancyEnabled -and [string]::IsNullOrWhiteSpace($tenant)) {
        throw "Parameter -SchoolYearRange requires CONFIG_SERVICE_TENANT to be set in the environment file when DMS_CONFIG_MULTI_TENANCY=true."
    }

    Write-Information "Registering CMS bootstrap client for DMS instance configuration." -InformationAction Continue
    Add-CmsClient `
        -CmsUrl $cmsUrl `
        -ClientId $script:BootstrapDmsInstanceClientId `
        -ClientSecret $script:BootstrapDmsInstanceClientSecret `
        -DisplayName "DMS Instance Setup Administrator"

    $configToken = Get-CmsToken `
        -CmsUrl $cmsUrl `
        -ClientId $script:BootstrapDmsInstanceClientId `
        -ClientSecret $script:BootstrapDmsInstanceClientSecret

    if ($multiTenancyEnabled -and -not [string]::IsNullOrWhiteSpace($tenant)) {
        Write-Information "Ensuring local CMS tenant exists: $(Format-LogSafeText $tenant)." -InformationAction Continue
        try {
            Add-Tenant -CmsUrl $cmsUrl -AccessToken $configToken -TenantName $tenant | Out-Null
        }
        catch {
            Write-Warning "Tenant creation was skipped or already satisfied for '$(Format-LogSafeText $tenant)'. $(Format-LogSafeText ($_.Exception.Message))"
        }
    }

    $postgresPassword = Get-EnvValueOrDefault -EnvValues $envValues -Name "POSTGRES_PASSWORD"
    $postgresDbName = Get-EnvValueOrDefault -EnvValues $envValues -Name "POSTGRES_DB_NAME" -DefaultValue "edfi_datamanagementservice"
    $postgresUser = Get-EnvValueOrDefault -EnvValues $envValues -Name "POSTGRES_USER" -DefaultValue "postgres"

    if ($NoDmsInstance) {
        Write-Information "Selecting existing route-unqualified DMS instance from CMS." -InformationAction Continue
        $instances = @(Get-DmsInstances -CmsUrl $cmsUrl -AccessToken $configToken -Tenant $tenant)
        $selectedInstance = Get-ExistingCompatibleInstance -Instances $instances -Tenant $tenant
        return ConvertTo-ConfigureResult `
            -InstanceIds @([long]$selectedInstance.id) `
            -Tenant $tenant
    }

    if ($schoolYears.Count -gt 0) {
        Write-Information "Creating DMS instances for school years $($schoolYears[0])-$($schoolYears[-1])." -InformationAction Continue
        $instances = Add-DmsSchoolYearInstances `
            -CmsUrl $cmsUrl `
            -AccessToken $configToken `
            -StartYear $schoolYears[0] `
            -EndYear $schoolYears[-1] `
            -PostgresPassword $postgresPassword `
            -PostgresDbName $postgresDbName `
            -PostgresUser $postgresUser `
            -Tenant $tenant

        $instanceIds = @($instances | ForEach-Object { [long]$_.InstanceId })
        $routeContexts = @(
            $instances | ForEach-Object {
                [pscustomobject]@{
                    InstanceId = [long]$_.InstanceId
                    ContextKey = "schoolYear"
                    ContextValue = [string]$_.Year
                }
            }
        )

        if ($AddSmokeTestCredentials) {
            Import-Module "$PSScriptRoot/../smoke_test/modules/SmokeTest.psm1" -Force
            Write-Information "Creating smoke test credentials." -InformationAction Continue
            Get-SmokeTestCredentials -ConfigServiceUrl $cmsUrl | Out-Null
            Write-Information "Smoke test credentials created." -InformationAction Continue
        }

        return ConvertTo-ConfigureResult `
            -InstanceIds $instanceIds `
            -RouteContexts $routeContexts `
            -Tenant $tenant `
            -SchoolYears $schoolYears
    }

    Write-Information "Creating default route-unqualified DMS instance." -InformationAction Continue
    $instanceId = Add-DmsInstance `
        -CmsUrl $cmsUrl `
        -AccessToken $configToken `
        -PostgresPassword $postgresPassword `
        -PostgresDbName $postgresDbName `
        -PostgresUser $postgresUser `
        -InstanceName "Local Development Instance" `
        -InstanceType "Development" `
        -Tenant $tenant

    if ($AddSmokeTestCredentials) {
        Import-Module "$PSScriptRoot/../smoke_test/modules/SmokeTest.psm1" -Force
        Write-Information "Creating smoke test credentials." -InformationAction Continue
        Get-SmokeTestCredentials -ConfigServiceUrl $cmsUrl | Out-Null
        Write-Information "Smoke test credentials created." -InformationAction Continue
    }

    return ConvertTo-ConfigureResult `
        -InstanceIds @([long]$instanceId) `
        -Tenant $tenant
}

if ($MyInvocation.InvocationName -eq '.') { return }

Invoke-ConfigureLocalDmsInstance `
    -EnvironmentFile $EnvironmentFile `
    -NoDmsInstance:$NoDmsInstance `
    -SchoolYearRange $SchoolYearRange `
    -AddSmokeTestCredentials:$AddSmokeTestCredentials
