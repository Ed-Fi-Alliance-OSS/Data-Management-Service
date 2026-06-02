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

    return Resolve-LocalSettingsEnvironmentFile -Path $Path -DockerComposeRoot $PSScriptRoot
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

    return Get-EnvValue -EnvValues $EnvValues -Name $Name -DefaultValue $DefaultValue
}

function Get-DmsInstanceRouteContexts {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns a collection of route contexts; the plural noun reflects the return shape.')]
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
    <#
    .SYNOPSIS
    Builds the structured success-pipeline object emitted by configure-local-dms-instance.ps1.
    See command-boundaries.md Section 3.4: the result must be JSON-compatible and contain
    SelectedInstanceIds. CMSReadOnlyAccess fields are included when the configured local flow
    has access to them so IDE next-step guidance can quote them without scraping prose.
    #>
    param(
        [long[]]
        $InstanceIds = @(),

        [object[]]
        $RouteContexts = @(),

        [string]
        $Tenant = "",

        [int[]]
        $SchoolYears = @(),

        [hashtable]
        $CmsReadOnlyAccess = $null
    )

    $result = [ordered]@{
        # SelectedInstanceIds is the documented property name. InstanceIds is kept as a
        # backward-compatible alias so existing callers (e.g. older wrapper checkouts during
        # rollout) continue to work; new code should prefer SelectedInstanceIds.
        SelectedInstanceIds = [long[]]@($InstanceIds)
        InstanceIds = [long[]]@($InstanceIds)
        RouteContexts = @($RouteContexts)
        Tenant = $Tenant
        SchoolYears = [int[]]@($SchoolYears)
        HasRouteQualifiedInstances = (@($RouteContexts).Count -gt 0)
    }

    if ($null -ne $CmsReadOnlyAccess -and $CmsReadOnlyAccess.Count -gt 0) {
        $result["CMSReadOnlyAccess"] = $CmsReadOnlyAccess
    }

    return [pscustomobject]$result
}

function Resolve-CmsReadOnlyAccessFromEnv {
    <#
    .SYNOPSIS
    Builds the optional CMSReadOnlyAccess block included in the configure result. Returns
    $null when none of CONFIG_SERVICE_CLIENT_ID, CONFIG_SERVICE_CLIENT_SCOPE, or
    CONFIG_SERVICE_CLIENT_SECRET are explicitly present in the env file. Per
    command-boundaries.md Section 3.4, "may include" means "include when actually populated"; a
    default-derived client id alone does not satisfy that contract. The client id/scope/secret
    come from the local environment file (start-local-dms.ps1's provider-specific local
    identity setup writes them); this helper does not contact CMS.
    #>
    param(
        [hashtable]$EnvValues
    )

    if ($null -eq $EnvValues -or -not (Test-CmsReadOnlyAccessEnvPresent -EnvValues $EnvValues)) {
        return $null
    }

    $clientId = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "CONFIG_SERVICE_CLIENT_ID" -DefaultValue "CMSReadOnlyAccess"
    $scope = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "CONFIG_SERVICE_CLIENT_SCOPE" -DefaultValue "edfi_admin_api/readonly_access"
    $secret = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "CONFIG_SERVICE_CLIENT_SECRET"

    $block = @{
        ClientId = $clientId
        Scope = $scope
    }
    if (-not [string]::IsNullOrWhiteSpace($secret)) {
        $block["ClientSecret"] = $secret
    }

    return $block
}

function Test-CmsReadOnlyAccessEnvPresent {
    <#
    .SYNOPSIS
    Returns $true when the env file explicitly supplies at least one of the three
    CONFIG_SERVICE_CLIENT_* keys with a non-blank value. Used to gate the optional
    CMSReadOnlyAccess block so defaults alone do not advertise the block as available.
    #>
    param(
        [hashtable]$EnvValues
    )

    foreach ($name in @("CONFIG_SERVICE_CLIENT_ID", "CONFIG_SERVICE_CLIENT_SCOPE", "CONFIG_SERVICE_CLIENT_SECRET")) {
        if ($EnvValues.ContainsKey($name) -and -not [string]::IsNullOrWhiteSpace([string]$EnvValues[$name])) {
            return $true
        }
    }

    return $false
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
        $contextList = ($routeContexts | ForEach-Object { "$(Format-LogSafeText $_.contextKey)=$(Format-LogSafeText $_.contextValue)" }) -join ", "
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

    # DMS-1151: bootstrap admin token acquisition. Add-CmsClient is idempotent (existing
    # client ids return a warning and continue) and is the only documented /connect/register
    # side effect for the configure/provision phases. Client id/secret are resolved through
    # the shared -EnvironmentFile helper so this phase and provision-dms-schema.ps1 agree on
    # the admin client (DMS_BOOTSTRAP_ADMIN_CLIENT_ID / DMS_BOOTSTRAP_ADMIN_CLIENT_SECRET).
    $bootstrapAdmin = Resolve-BootstrapAdminClient -EnvValues $envValues
    Write-Information "Acquiring CMS bootstrap admin token for DMS instance configuration." -InformationAction Continue
    Add-CmsClient `
        -CmsUrl $cmsUrl `
        -ClientId $bootstrapAdmin.ClientId `
        -ClientSecret $bootstrapAdmin.ClientSecret `
        -DisplayName "DMS Instance Setup Administrator"

    $configToken = Get-CmsToken `
        -CmsUrl $cmsUrl `
        -ClientId $bootstrapAdmin.ClientId `
        -ClientSecret $bootstrapAdmin.ClientSecret

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
    $cmsReadOnlyAccess = Resolve-CmsReadOnlyAccessFromEnv -EnvValues $envValues

    if ($NoDmsInstance) {
        Write-Information "Selecting existing route-unqualified DMS instance from CMS." -InformationAction Continue
        $instances = @(Get-DataStore -CmsUrl $cmsUrl -AccessToken $configToken -Tenant $tenant)
        $selectedInstance = Get-ExistingCompatibleInstance -Instances $instances -Tenant $tenant
        if ($AddSmokeTestCredentials) {
            Import-Module "$PSScriptRoot/../smoke_test/modules/SmokeTest.psm1" -Force
            Write-Information "Creating smoke test credentials." -InformationAction Continue
            Get-SmokeTestCredentials -ConfigServiceUrl $cmsUrl -DataStoreIds @([long]$selectedInstance.id) -Tenant $tenant | Out-Null
            Write-Information "Smoke test credentials created." -InformationAction Continue
        }

        return ConvertTo-ConfigureResult `
            -InstanceIds @([long]$selectedInstance.id) `
            -Tenant $tenant `
            -CmsReadOnlyAccess $cmsReadOnlyAccess
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

        $instanceIds = @($instances | ForEach-Object { [long]$_.DataStoreId })
        $routeContexts = @(
            $instances | ForEach-Object {
                [pscustomobject]@{
                    DataStoreId = [long]$_.DataStoreId
                    ContextKey = "schoolYear"
                    ContextValue = [string]$_.Year
                }
            }
        )

        if ($AddSmokeTestCredentials) {
            Import-Module "$PSScriptRoot/../smoke_test/modules/SmokeTest.psm1" -Force
            Write-Information "Creating smoke test credentials." -InformationAction Continue
            Get-SmokeTestCredentials -ConfigServiceUrl $cmsUrl -DataStoreIds $instanceIds -Tenant $tenant | Out-Null
            Write-Information "Smoke test credentials created." -InformationAction Continue
        }

        return ConvertTo-ConfigureResult `
            -InstanceIds $instanceIds `
            -RouteContexts $routeContexts `
            -Tenant $tenant `
            -SchoolYears $schoolYears `
            -CmsReadOnlyAccess $cmsReadOnlyAccess
    }

    Write-Information "Creating default route-unqualified DMS instance." -InformationAction Continue
    $instanceId = Add-DataStore `
        -CmsUrl $cmsUrl `
        -AccessToken $configToken `
        -PostgresPassword $postgresPassword `
        -PostgresDbName $postgresDbName `
        -PostgresUser $postgresUser `
        -Name "Local Development Instance" `
        -DataStoreType "Development" `
        -Tenant $tenant

    if ($AddSmokeTestCredentials) {
        Import-Module "$PSScriptRoot/../smoke_test/modules/SmokeTest.psm1" -Force
        Write-Information "Creating smoke test credentials." -InformationAction Continue
        Get-SmokeTestCredentials -ConfigServiceUrl $cmsUrl -DataStoreIds @([long]$instanceId) -Tenant $tenant | Out-Null
        Write-Information "Smoke test credentials created." -InformationAction Continue
    }

    return ConvertTo-ConfigureResult `
        -InstanceIds @([long]$instanceId) `
        -Tenant $tenant `
        -CmsReadOnlyAccess $cmsReadOnlyAccess
}

if ($MyInvocation.InvocationName -eq '.') { return }

Invoke-ConfigureLocalDmsInstance `
    -EnvironmentFile $EnvironmentFile `
    -NoDmsInstance:$NoDmsInstance `
    -SchoolYearRange $SchoolYearRange `
    -AddSmokeTestCredentials:$AddSmokeTestCredentials
