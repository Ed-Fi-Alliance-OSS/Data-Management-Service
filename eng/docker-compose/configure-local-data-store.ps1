# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Bootstrap entry script intentionally writes operator progress to the console.')]
[CmdletBinding()]
param(
    [string]$EnvironmentFile,
    [switch]$NoDataStore,
    [string]$SchoolYearRange = "",
    [string]$DataStoreDatabaseName = "",
    [switch]$AddSmokeTestCredentials,

    # Database engine for the DMS datastore. "mssql" registers an MSSQL-shaped data-store
    # connection string (Server=dms-mssql;...) instead of the PostgreSQL form, and composes the
    # .env.mssql overlay onto -EnvironmentFile (no-op when the env is already composed, e.g. via
    # the bootstrap wrapper) so the MSSQL_* values used here come from the same source as the
    # other phases. The Configuration Service uses the selected engine and shares the DMS
    # database in the default local topology.
    [ValidateSet("postgresql", "mssql")]
    [string]$DatabaseEngine = "postgresql",

    # Separate configuration-database topology. Only used to reject an explicit -DataStoreDatabaseName
    # replacement that would collide with the dedicated configuration database (edfi_configurationservice)
    # under the engine's identity policy; the datastore registration otherwise converges on the
    # Compose-resolved topology anchor. Topology is never inferred from a name.
    [switch]$SeparateConfigDatabase
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

function Get-DataStoreContexts {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns a collection of route contexts; the plural noun reflects the return shape.')]
    param(
        $Instance
    )

    $property = $Instance.PSObject.Properties["dataStoreContexts"]
    if ($null -eq $property -or $null -eq $property.Value -or $property.Value -is [string]) {
        return @()
    }

    return @($property.Value)
}

function ConvertTo-ConfigureResult {
    <#
    .SYNOPSIS
    Builds the structured success-pipeline object emitted by configure-local-data-store.ps1.
    See command-boundaries.md Section 3.4: the result must be JSON-compatible and contain
    SelectedDataStoreIds. CMSReadOnlyAccess fields are included when the configured local flow
    has access to them so IDE next-step guidance can quote them without scraping prose.
    #>
    param(
        [long[]]
        $DataStoreIds = @(),

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
        SelectedDataStoreIds = [long[]]@($DataStoreIds)
        DataStoreIds = [long[]]@($DataStoreIds)
        RouteContexts = @($RouteContexts)
        Tenant = $Tenant
        SchoolYears = [int[]]@($SchoolYears)
        HasRouteQualifiedDataStores = (@($RouteContexts).Count -gt 0)
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

function Get-ExistingCompatibleDataStore {
    param(
        [object[]]
        $DataStores,

        [string]
        $Tenant
    )

    if ($DataStores.Count -eq 0) {
        throw "-NoDataStore was supplied, but no existing data stores were found in the current tenant scope '$(Format-LogSafeText $Tenant)'. Create one route-unqualified CMS data store, or omit -NoDataStore."
    }

    if ($DataStores.Count -gt 1) {
        $listing = ($DataStores | ForEach-Object {
            "id=$(Format-LogSafeText $_.id) name=$(Format-LogSafeText $_.name)"
        }) -join ", "
        throw "-NoDataStore requires exactly one existing data store in tenant scope '$(Format-LogSafeText $Tenant)'. Found $($DataStores.Count): $listing. Clean up CMS state or run with explicit configuration inputs."
    }

    $dataStore = $DataStores[0]
    $routeContexts = @(Get-DataStoreContexts -Instance $dataStore)
    if ($routeContexts.Count -gt 0) {
        $contextList = ($routeContexts | ForEach-Object { "$(Format-LogSafeText $_.contextKey)=$(Format-LogSafeText $_.contextValue)" }) -join ", "
        throw "-NoDataStore found one existing data store, but it is route-qualified ($contextList). -NoDataStore supports exactly one route-unqualified data store; clean up CMS state or use -SchoolYearRange."
    }

    return $dataStore
}

function Invoke-ConfigureLocalDataStore {
    param(
        [string]
        $EnvironmentFile,

        [switch]
        $NoDataStore,

        [string]
        $SchoolYearRange = "",

        [string]
        $DataStoreDatabaseName = "",

        [switch]
        $AddSmokeTestCredentials,

        [ValidateSet("postgresql", "mssql")]
        [string]
        $DatabaseEngine = "postgresql",

        [switch]
        $SeparateConfigDatabase
    )

    $resolvedEnvironmentFile = Resolve-ConfigureEnvironmentFile -Path $EnvironmentFile
    # Compose the MSSQL engine overlay for -DatabaseEngine mssql; this covers direct invocation of
    # this script with a custom -EnvironmentFile (still gets the overlay layered on top) and the
    # bootstrap wrapper path (Resolve-DatabaseEngineEnvironmentFile detects the overlay is already
    # composed via DMS_DATASTORE=mssql and returns the file unchanged, avoiding a
    # derived-of-derived file). This phase composes the overlay only for the DMS datastore; the
    # Configuration Service engine/connection/database agreement is owned and validated by the start
    # scripts (Resolve-EffectiveConfigRuntimeContract), so it is not re-checked here.
    $resolvedEnvironmentFile = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine $DatabaseEngine -BaseEnvironmentFile $resolvedEnvironmentFile -DockerComposeRoot $PSScriptRoot
    $envValues = ReadValuesFromEnvFile -EnvironmentFile $resolvedEnvironmentFile
    $cmsUrl = Resolve-CmsBaseUrl -EnvValues $envValues
    $tenant = Get-EnvValueOrDefault -EnvValues $envValues -Name "CONFIG_SERVICE_TENANT"
    $schoolYears = @(Resolve-SchoolYearRange -Range $SchoolYearRange)

    if ($NoDataStore -and $schoolYears.Count -gt 0) {
        throw "Parameters -NoDataStore and -SchoolYearRange are mutually exclusive. Use -NoDataStore to select one existing route-unqualified data store, or -SchoolYearRange to configure route-qualified data stores."
    }

    $multiTenancyEnabled = (Get-EnvValueOrDefault -EnvValues $envValues -Name "DMS_CONFIG_MULTI_TENANCY").Equals("true", [System.StringComparison]::OrdinalIgnoreCase)
    if ($schoolYears.Count -gt 0 -and $multiTenancyEnabled -and [string]::IsNullOrWhiteSpace($tenant)) {
        throw "Parameter -SchoolYearRange requires CONFIG_SERVICE_TENANT to be set in the environment file when DMS_CONFIG_MULTI_TENANCY=true."
    }

    # Resolve and VALIDATE the registered DMS datastore target BEFORE any CMS mutation (bootstrap client,
    # tenant, and data-store creation below), so an invalid explicit -DataStoreDatabaseName replacement fails
    # early rather than after partial CMS state is created. Skipped entirely for -NoDataStore, which selects an
    # existing CMS record and creates no registration (so it neither resolves the Compose anchor nor needs a
    # datastore connection). Registration converges on the Compose-resolved topology anchor (the single
    # datastore-name authority, Resolve-RegisteredDatastoreTarget) unless an explicit replacement is supplied;
    # the same one compose resolution yields the SQL Server SA password so the stored MSSQL connection matches
    # the credential the container was initialized with, even under a shell override.
    # NOTE: the local variable MUST NOT be named $datastoreDatabaseName - PowerShell variable names are
    # case-insensitive, so that would alias the $DataStoreDatabaseName parameter and null it before the
    # resolver reads it. Use a distinct name.
    $registeredDatastoreName = $null
    $dataStoreConnectionString = ""
    if (-not $NoDataStore) {
        $resolvedDatastoreCompose = Get-ComposeResolvedConfiguration `
            -ComposeFiles @("-f", (Join-Path $PSScriptRoot $(if ($DatabaseEngine -eq "mssql") { "mssql.yml" } else { "postgresql.yml" }))) `
            -EnvironmentFile $resolvedEnvironmentFile `
            -ProjectName "dms-local" `
            -InfrastructureEngine $DatabaseEngine
        $registeredDatastoreName = Resolve-RegisteredDatastoreTarget `
            -InfrastructureEngine $DatabaseEngine `
            -RequestedDatabaseName $DataStoreDatabaseName `
            -TopologyDatastoreDatabaseName $resolvedDatastoreCompose.TopologyDatastoreDatabaseName `
            -SeparateConfigDatabase:$SeparateConfigDatabase

        if ($DatabaseEngine -eq "mssql") {
            # SQL Server form pointing at the dms-mssql container; PostgreSQL is left empty so Add-DataStore
            # builds its connection string from the Postgres* values. provision-dms-schema.ps1 reads this back
            # and translates the Docker host to the host-side mapped port before invoking SchemaTools.
            $dataStoreConnectionString = New-DataStoreConnectionString `
                -DatabaseEngine "mssql" `
                -DbHost "dms-mssql" `
                -Port 1433 `
                -Username "sa" `
                -Password $resolvedDatastoreCompose.MssqlSaPassword `
                -DatabaseName $registeredDatastoreName
        }
    }

    # DMS-1151: bootstrap admin token acquisition. Add-CmsClient is idempotent (existing
    # client ids return a warning and continue) and is the only documented /connect/register
    # side effect for the configure/provision phases. Client id/secret are resolved through
    # the shared -EnvironmentFile helper so this phase and provision-dms-schema.ps1 agree on
    # the admin client (DMS_BOOTSTRAP_ADMIN_CLIENT_ID / DMS_BOOTSTRAP_ADMIN_CLIENT_SECRET).
    $bootstrapAdmin = Resolve-BootstrapAdminClient -EnvValues $envValues
    Write-Information "Acquiring CMS bootstrap admin token for data store configuration." -InformationAction Continue
    Add-CmsClient `
        -CmsUrl $cmsUrl `
        -ClientId $bootstrapAdmin.ClientId `
        -ClientSecret $bootstrapAdmin.ClientSecret `
        -DisplayName "Data Store Setup Administrator"

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
    $postgresUser = Get-EnvValueOrDefault -EnvValues $envValues -Name "POSTGRES_USER" -DefaultValue "postgres"
    # $registeredDatastoreName and $dataStoreConnectionString were resolved and validated above (before any CMS
    # mutation, and skipped for -NoDataStore). $postgresDbName is $null on the -NoDataStore path, which returns
    # an existing data store without registering one.
    $postgresDbName = $registeredDatastoreName
    $postgresCredential = ConvertTo-PostgresCredential -UserName $postgresUser -Secret $postgresPassword
    $cmsReadOnlyAccess = Resolve-CmsReadOnlyAccessFromEnv -EnvValues $envValues

    if ($NoDataStore) {
        Write-Information "Selecting existing route-unqualified data store from CMS." -InformationAction Continue
        $dataStores = @(Get-DataStore -CmsUrl $cmsUrl -AccessToken $configToken -Tenant $tenant)
        $selectedDataStore = Get-ExistingCompatibleDataStore -DataStores $dataStores -Tenant $tenant
        if ($AddSmokeTestCredentials) {
            Import-Module "$PSScriptRoot/../smoke_test/modules/SmokeTest.psm1" -Force
            Write-Information "Creating smoke test credentials." -InformationAction Continue
            Get-SmokeTestCredential -ConfigServiceUrl $cmsUrl -DataStoreIds @([long]$selectedDataStore.id) -Tenant $tenant | Out-Null
            Write-Information "Smoke test credentials created." -InformationAction Continue
        }

        return ConvertTo-ConfigureResult `
            -DataStoreIds @([long]$selectedDataStore.id) `
            -Tenant $tenant `
            -CmsReadOnlyAccess $cmsReadOnlyAccess
    }

    if ($schoolYears.Count -gt 0) {
        Write-Information "Creating data stores for school years $($schoolYears[0])-$($schoolYears[-1])." -InformationAction Continue
        $dataStores = Add-DmsSchoolYearInstances `
            -CmsUrl $cmsUrl `
            -AccessToken $configToken `
            -StartYear $schoolYears[0] `
            -EndYear $schoolYears[-1] `
            -PostgresCredential $postgresCredential `
            -PostgresDbName $postgresDbName `
            -ConnectionString $dataStoreConnectionString `
            -Tenant $tenant

        $dataStoreIds = @($dataStores | ForEach-Object { [long]$_.DataStoreId })
        $routeContexts = @(
            $dataStores | ForEach-Object {
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
            Get-SmokeTestCredential -ConfigServiceUrl $cmsUrl -DataStoreIds $dataStoreIds -Tenant $tenant | Out-Null
            Write-Information "Smoke test credentials created." -InformationAction Continue
        }

        return ConvertTo-ConfigureResult `
            -DataStoreIds $dataStoreIds `
            -RouteContexts $routeContexts `
            -Tenant $tenant `
            -SchoolYears $schoolYears `
            -CmsReadOnlyAccess $cmsReadOnlyAccess
    }

    Write-Information "Creating default route-unqualified data store." -InformationAction Continue
    $dataStoreId = Add-DataStore `
        -CmsUrl $cmsUrl `
        -AccessToken $configToken `
        -PostgresCredential $postgresCredential `
        -PostgresDbName $postgresDbName `
        -ConnectionString $dataStoreConnectionString `
        -Name "Local Development Data Store" `
        -DataStoreType "Development" `
        -Tenant $tenant

    if ($AddSmokeTestCredentials) {
        Import-Module "$PSScriptRoot/../smoke_test/modules/SmokeTest.psm1" -Force
        Write-Information "Creating smoke test credentials." -InformationAction Continue
        Get-SmokeTestCredential -ConfigServiceUrl $cmsUrl -DataStoreIds @([long]$dataStoreId) -Tenant $tenant | Out-Null
        Write-Information "Smoke test credentials created." -InformationAction Continue
    }

    return ConvertTo-ConfigureResult `
        -DataStoreIds @([long]$dataStoreId) `
        -Tenant $tenant `
        -CmsReadOnlyAccess $cmsReadOnlyAccess
}

if ($MyInvocation.InvocationName -eq '.') { return }

Invoke-ConfigureLocalDataStore `
    -EnvironmentFile $EnvironmentFile `
    -NoDataStore:$NoDataStore `
    -SchoolYearRange $SchoolYearRange `
    -DataStoreDatabaseName $DataStoreDatabaseName `
    -AddSmokeTestCredentials:$AddSmokeTestCredentials `
    -DatabaseEngine $DatabaseEngine `
    -SeparateConfigDatabase:$SeparateConfigDatabase
