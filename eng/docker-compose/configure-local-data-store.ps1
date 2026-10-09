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

    # Declares that the stack this phase configures was started with -SeparateConfigDatabase, so
    # the Configuration Service owns the dedicated edfi_configurationservice database and the DMS
    # datastore registered below must not land in it. Topology is DECLARED, never inferred from a
    # database-name spelling: pass the same switch the start invocation used. The start script's
    # -InfraOnly guidance prints it for you when it applies. Omit it for the default shared
    # topology, where the datastore and the Configuration Service share one database by design.
    [Switch]$SeparateConfigDatabase,

    # Restore handoff from the bootstrap wrappers (-RestoreTemplate with -SeparateConfigDatabase):
    # the database the restore just replaced. The dedicated Configuration Service database survives
    # a restore, so it may already hold the data store an earlier run registered; with this value the
    # phase reuses that data store when its stored connection string targets this database on the
    # composed database service, creates one when CMS holds none, and refuses anything else. Requires
    # -SeparateConfigDatabase; not valid with -NoDataStore or -SchoolYearRange.
    [string]$RestoreTargetDatabaseName = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Import-Module "$PSScriptRoot/bootstrap-manifest.psm1" -Force -Global
Import-Module "$PSScriptRoot/env-utility.psm1" -Force -Global
# Shared Compose-equivalent resolver: env reads below honour Docker Compose interpolation
# precedence (an ambient process/shell value wins over the env file, ${VAR} references are
# followed, single-quoted values stay literal), so the data store registered in CMS carries
# exactly the credentials, names, and tenant the running containers received.
Import-Module "$PSScriptRoot/database-safety.psm1" -Force
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

    # Compose-equivalent read: the process/shell environment wins over the env file, matching the
    # values Docker Compose interpolates into the running containers.
    return Get-ComposeResolvedEnvValue -EnvironmentValues $EnvValues -Name $Name -DefaultValue $DefaultValue
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
    CONFIG_SERVICE_CLIENT_SECRET are present in the effective environment. Per
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
    Returns $true when the effective environment (env file or ambient process environment, with
    Compose precedence) supplies at least one of the three CONFIG_SERVICE_CLIENT_* keys with a
    non-blank value. Used to gate the optional CMSReadOnlyAccess block so defaults alone do not
    advertise the block as available.
    #>
    param(
        [hashtable]$EnvValues
    )

    foreach ($name in @("CONFIG_SERVICE_CLIENT_ID", "CONFIG_SERVICE_CLIENT_SCOPE", "CONFIG_SERVICE_CLIENT_SECRET")) {
        if (-not [string]::IsNullOrWhiteSpace((Get-ComposeResolvedEnvValue -EnvironmentValues $EnvValues -Name $name))) {
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

function Resolve-RestoreSeparateConfigDataStore {
    <#
    .SYNOPSIS
    Restore with -SeparateConfigDatabase: decides whether the configure phase creates the
    route-unqualified data store or reuses the one the surviving Configuration Service database
    already holds. Returns $null when CMS holds no data store (create), the data store when exactly
    one route-unqualified data store targets the restored database on the composed database service
    (reuse), and throws otherwise.

    .DESCRIPTION
    A restore replaces only the DMS datastore; in separate topology the dedicated Configuration
    Service database keeps the data store an earlier run registered, and CMS rejects a second
    registration under the same name. -NoDataStore's selector is not enough to reuse it: it checks
    only the count and the route contexts, and the provisioning phase that judges a reused data
    store's stored target never runs in restore mode. So the stored connection string is decrypted
    and compared with the registration this phase would create for the restored target:

      - engine: the record's provider (when CMS reports one) and the connection-string form, read
        with the selected engine's own keyword grammar;
      - host and port: exactly one endpoint, equal to the composed database service this phase
        registers (dms-postgresql:5432 / dms-mssql,1433), through the same endpoint parser and port
        comparer the start-phase topology checks use;
      - database: exactly one name, equal (ordinal) to the restored target. A matching name alone is
        not enough; the same name on another server is a different database.

    The registration is never updated: a match is reused with its existing id.

    Refusals name the data store id and which property differs - never the stored connection
    string, its credentials, the decrypted text, or either database name. They happen AFTER the
    restore replaced the target database, so unlike a scratch-validation refusal they do not leave
    the target untouched; DMS is not started.
    #>
    param(
        [object[]]
        $DataStores,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]
        $DatabaseEngine,

        [Parameter(Mandatory)]
        [string]
        $RestoreTargetDatabaseName,

        [Parameter(Mandatory)]
        [hashtable]
        $EnvValues,

        [string]
        $Tenant = "",

        [int]
        $PageSize = 500
    )

    $afterRestore = "This check runs after the restore replaced the target database: the target is restored, DMS was not started, and the scratch-validation guarantee that a refused restore leaves the target untouched does not apply. Correct or remove the stale CMS data store registration, then rerun the restore. The stored connection string and the database names are withheld."

    if ($DataStores.Count -eq 0) {
        return $null
    }

    if ($DataStores.Count -ge $PageSize) {
        throw "Restore with -SeparateConfigDatabase listed $($DataStores.Count) CMS data stores in tenant scope '$(Format-LogSafeText $Tenant)', the query page size, so it cannot prove which one serves the restored target. $afterRestore"
    }

    if ($DataStores.Count -gt 1) {
        $listing = ($DataStores | ForEach-Object {
            "id=$(Format-LogSafeText $_.id) name=$(Format-LogSafeText $_.name)"
        }) -join ", "
        throw "Restore with -SeparateConfigDatabase found $($DataStores.Count) CMS data stores in tenant scope '$(Format-LogSafeText $Tenant)' ($listing); it reuses only a single route-unqualified data store. $afterRestore"
    }

    $dataStore = $DataStores[0]
    $refusalPrefix = "Restore with -SeparateConfigDatabase cannot reuse CMS data store id=$(Format-LogSafeText $dataStore.id) (name=$(Format-LogSafeText $dataStore.name)) for the restored target:"

    $routeContexts = @(Get-DataStoreContexts -Instance $dataStore)
    if ($routeContexts.Count -gt 0) {
        throw "$refusalPrefix it is route-qualified. $afterRestore"
    }

    $expectedProvider = if ($DatabaseEngine -eq "mssql") { "sqlserver" } else { "postgresql" }
    $providerProperty = $dataStore.PSObject.Properties["provider"]
    $provider = if ($null -eq $providerProperty) { "" } else { [string]$providerProperty.Value }
    if (-not [string]::IsNullOrWhiteSpace($provider) -and
        -not $provider.Equals($expectedProvider, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$refusalPrefix its provider is not '$expectedProvider', so it belongs to the other database engine. $afterRestore"
    }

    $connectionStringProperty = $dataStore.PSObject.Properties["connectionString"]
    $protectedConnectionString = if ($null -eq $connectionStringProperty) { "" } else { [string]$connectionStringProperty.Value }
    if ([string]::IsNullOrWhiteSpace($protectedConnectionString)) {
        throw "$refusalPrefix CMS returned no connection string for it. $afterRestore"
    }

    # The decryption helper's failures are fixed sentences that carry no key, cipher text, or
    # decrypted text, so the reason is safe to repeat.
    try {
        $connectionString = ConvertFrom-CmsEncryptedConnectionString `
            -ProtectedConnectionString $protectedConnectionString `
            -EnvValues $EnvValues
    }
    catch {
        throw "$refusalPrefix its connection string could not be read ($($_.Exception.Message)). $afterRestore"
    }

    # The parsers' own failure text can quote the value, so it is replaced by a fixed sentence.
    try {
        $databaseNames = @(Get-DatabaseNameFromResolvedConnectionString -ConnectionString $connectionString -DatabaseEngine $DatabaseEngine)
        $endpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString $connectionString -DatabaseEngine $DatabaseEngine)
    }
    catch {
        throw "$refusalPrefix its decrypted connection string is not a valid $DatabaseEngine connection string. $afterRestore"
    }

    if ($databaseNames.Count -eq 0 -or $endpoints.Count -eq 0) {
        throw "$refusalPrefix its decrypted connection string does not name a $DatabaseEngine server and database, so it belongs to the other database engine or is incomplete. $afterRestore"
    }

    # The endpoint this phase registers: Add-DataStore's PostgreSQL defaults, and the SQL Server
    # connection string Invoke-ConfigureLocalDataStore builds.
    $expectedEndpoint = if ($DatabaseEngine -eq "mssql") {
        [pscustomobject]@{ Host = "dms-mssql"; Port = "1433" }
    }
    else {
        [pscustomobject]@{ Host = "dms-postgresql"; Port = "5432" }
    }

    if ($endpoints.Count -ne 1) {
        throw "$refusalPrefix its connection string names $($endpoints.Count) servers, not the one composed database service. $afterRestore"
    }

    $endpoint = $endpoints[0]
    if (-not ([string]$endpoint.Host).Equals($expectedEndpoint.Host, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$refusalPrefix its server host is not the composed database service '$($expectedEndpoint.Host)'. $afterRestore"
    }

    # An omitted port is the provider default, which is the port this phase registers.
    $port = if ([string]::IsNullOrWhiteSpace([string]$endpoint.Port)) { $expectedEndpoint.Port } else { [string]$endpoint.Port }
    if (-not (Test-PortNumberEquivalent -Left $port -Right $expectedEndpoint.Port)) {
        throw "$refusalPrefix its server port is not $($expectedEndpoint.Port), the composed database service's port. $afterRestore"
    }

    if ($databaseNames.Count -ne 1) {
        throw "$refusalPrefix its connection string names $($databaseNames.Count) databases. $afterRestore"
    }

    if (-not [string]::Equals($databaseNames[0], $RestoreTargetDatabaseName, [System.StringComparison]::Ordinal)) {
        throw "$refusalPrefix its database is not the restored target database. $afterRestore"
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

        [Switch]
        $SeparateConfigDatabase,

        [string]
        $RestoreTargetDatabaseName = ""
    )

    # Refuse to run under a leaked restore-candidate override: this phase talks to CMS and the
    # live data store, and must only ever see the ACTIVE .bootstrap workspace.
    Assert-NoBootstrapRootOverride -PhaseName "configure-local-data-store.ps1"

    $resolvedEnvironmentFile = Resolve-ConfigureEnvironmentFile -Path $EnvironmentFile
    # Compose the MSSQL engine overlay for -DatabaseEngine mssql; this covers direct invocation of
    # this script with a custom -EnvironmentFile (still gets the overlay layered on top) and the
    # bootstrap wrapper path (Resolve-DatabaseEngineEnvironmentFile detects the overlay is already
    # composed via DMS_DATASTORE=mssql and returns the file unchanged, avoiding a
    # derived-of-derived file).
    $resolvedEnvironmentFile = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine $DatabaseEngine -BaseEnvironmentFile $resolvedEnvironmentFile -DockerComposeRoot $PSScriptRoot
    if ($SeparateConfigDatabase) {
        # The separate-topology continuation runs against the operator's ORIGINAL -EnvironmentFile,
        # whose topology marker and CMS seam belong to the derived file the start phase wrote - not
        # to the file in hand. Re-running the start scripts' own second composition step, in the
        # same order and after the same engine overlay, reproduces that same derived artifact
        # (a deterministic .derived/<name>.topology path, recomputed idempotently from the switch),
        # so the datastore guard below asks the live authority about the environment the running
        # stack actually received. Shared mode never calls this, so its effective file - and every
        # value read from it - is unchanged.
        $resolvedEnvironmentFile = Resolve-CmsDatabaseTopologyEnvironmentFile `
            -BaseEnvironmentFile $resolvedEnvironmentFile `
            -DatabaseEngine $DatabaseEngine `
            -SeparateConfigDatabase `
            -DockerComposeRoot $PSScriptRoot
    }
    $envValues = ReadValuesFromEnvFile -EnvironmentFile $resolvedEnvironmentFile
    $cmsUrl = Resolve-CmsBaseUrl -EnvValues $envValues
    $tenant = Get-EnvValueOrDefault -EnvValues $envValues -Name "CONFIG_SERVICE_TENANT"
    $schoolYears = @(Resolve-SchoolYearRange -Range $SchoolYearRange)

    if ($NoDataStore -and $schoolYears.Count -gt 0) {
        throw "Parameters -NoDataStore and -SchoolYearRange are mutually exclusive. Use -NoDataStore to select one existing route-unqualified data store, or -SchoolYearRange to configure route-qualified data stores."
    }

    $restoreReuse = -not [string]::IsNullOrWhiteSpace($RestoreTargetDatabaseName)
    if ($restoreReuse) {
        if (-not $SeparateConfigDatabase) {
            throw "-RestoreTargetDatabaseName requires -SeparateConfigDatabase. It is the restore handoff for a separate-topology stack, whose Configuration Service database survives the restore; a shared-topology restore replaces the database that holds the CMS data store."
        }
        if ($NoDataStore) {
            throw "-RestoreTargetDatabaseName cannot be combined with -NoDataStore. The restore handoff verifies the existing data store against the restored target before reusing it; -NoDataStore selects one without that check."
        }
        if ($schoolYears.Count -gt 0) {
            throw "-RestoreTargetDatabaseName cannot be combined with -SchoolYearRange. A restore replaces exactly one route-unqualified target."
        }
        # A data store this handoff creates must name the database the restore replaced, so an
        # explicit -DataStoreDatabaseName may only repeat it. Refused here, before any CMS write.
        if (-not [string]::IsNullOrEmpty($DataStoreDatabaseName) -and
            -not [string]::Equals($DataStoreDatabaseName, $RestoreTargetDatabaseName, [System.StringComparison]::Ordinal)) {
            throw "-DataStoreDatabaseName names a different database than the one this restore replaced (-RestoreTargetDatabaseName), so the data store would be registered against a database the restore did not populate. The restore has already replaced its target: DMS was not started, and the scratch-validation guarantee that a refused restore leaves the target untouched does not apply. Rerun the restore without -DataStoreDatabaseName (the restore target comes from POSTGRES_DB_NAME or MSSQL_DB_NAME), or with the same name. Both values are withheld."
        }
        # From here on the registration name IS the restore target: every resolution below that
        # honors -DataStoreDatabaseName (PostgreSQL and SQL Server names, the topology guard) now
        # names the restored database instead of the env default.
        $DataStoreDatabaseName = $RestoreTargetDatabaseName
    }

    # Which input a registered name came from, for the topology guards' diagnostics.
    $registrationDatabaseSource = if ($restoreReuse) { "-RestoreTargetDatabaseName" } else { "-DataStoreDatabaseName" }

    $multiTenancyEnabled = (Get-EnvValueOrDefault -EnvValues $envValues -Name "DMS_CONFIG_MULTI_TENANCY").Equals("true", [System.StringComparison]::OrdinalIgnoreCase)
    if ($schoolYears.Count -gt 0 -and $multiTenancyEnabled -and [string]::IsNullOrWhiteSpace($tenant)) {
        throw "Parameter -SchoolYearRange requires CONFIG_SERVICE_TENANT to be set in the environment file when DMS_CONFIG_MULTI_TENANCY=true."
    }

    # Datastore values are resolved BEFORE any CMS call, not just before the registration that
    # consumes them: the separate-topology guard below judges the exact name this run will
    # register, and it has to be able to refuse while CMS is still untouched.
    $postgresPassword = Get-EnvValueOrDefault -EnvValues $envValues -Name "POSTGRES_PASSWORD"
    $postgresDbName =
        if ([string]::IsNullOrWhiteSpace($DataStoreDatabaseName)) {
            Get-EnvValueOrDefault -EnvValues $envValues -Name "POSTGRES_DB_NAME" -DefaultValue "edfi_datamanagementservice"
        }
        else {
            $DataStoreDatabaseName
        }
    $postgresUser = Get-EnvValueOrDefault -EnvValues $envValues -Name "POSTGRES_USER" -DefaultValue "postgres"
    $postgresCredential = ConvertTo-PostgresCredential -UserName $postgresUser -Secret $postgresPassword
    $cmsReadOnlyAccess = Resolve-CmsReadOnlyAccessFromEnv -EnvValues $envValues

    # Resolve the data-store connection string stored in CMS for the DMS datastore. For MSSQL
    # this is the SQL Server form pointing at the dms-mssql container; for PostgreSQL it is left
    # empty so Add-DataStore builds its PostgreSQL connection string from the Postgres* values.
    # provision-dms-schema.ps1 reads this string back and translates the Docker host to the
    # host-side mapped port before invoking SchemaTools.
    $dataStoreConnectionString = ""
    if ($DatabaseEngine -eq "mssql") {
        $mssqlPassword = Get-EnvValueOrDefault -EnvValues $envValues -Name "MSSQL_SA_PASSWORD" -DefaultValue "abcdefgh1!"
        $mssqlDbName =
            if ([string]::IsNullOrWhiteSpace($DataStoreDatabaseName)) {
                Get-EnvValueOrDefault -EnvValues $envValues -Name "MSSQL_DB_NAME" -DefaultValue "edfi_datamanagementservice"
            }
            else {
                $DataStoreDatabaseName
            }
        $dataStoreConnectionString = New-DataStoreConnectionString `
            -DatabaseEngine "mssql" `
            -DbHost "dms-mssql" `
            -Port 1433 `
            -Username "sa" `
            -Password $mssqlPassword `
            -DatabaseName $mssqlDbName
    }

    # Separate-topology guard for the manual configure phase. This phase registers the DMS
    # datastore AFTER the start phase's live topology check has already run, so without a check
    # here the documented `-InfraOnly -SeparateConfigDatabase` continuation can point the datastore
    # at the dedicated Configuration Service database, and later provisioning deploys the DMS
    # schema into it. It runs after the parameter-shape rules above - an invalid shape must keep
    # reporting its own established diagnostic - and before Add-CmsClient, so a refused run leaves
    # no CMS state behind at all: no bootstrap admin client, no tenant, no data store.
    #
    # Gated on a registration actually happening, because that is the limit of what this phase can
    # judge - NOT because the other shape is harmless. -NoDataStore selects an EXISTING data store
    # and registers no name, so the candidate resolved above is not what provisioning will target:
    # the selected record's own STORED connection string is, and this phase never sees it decrypted.
    # Judging the candidate here would be false assurance, so the reused target is judged where it
    # first exists - provision-dms-schema.ps1's -SeparateConfigDatabase guard, in front of
    # SchemaTools. -SchoolYearRange, by contrast, IS judged here: Add-DmsSchoolYearInstances forwards
    # this same resolved name and connection string verbatim to every per-year Add-DataStore - only
    # the data store's display name and route context carry the year - so the single candidate
    # resolved above is exactly what every year registers.
    if ($SeparateConfigDatabase -and -not $NoDataStore) {
        if ($DatabaseEngine -eq "mssql") {
            # SQL Server renders no offline verdict: database names inherit the INSTANCE collation
            # and the equivalence class differs between instances, so the running server - already
            # up by this phase - is asked, through the same authority both start scripts use and
            # against the effective file resolved above. The registered candidate travels as the
            # value a provider RECEIVES (Get-RegisteredDatastoreDatabaseValue), never the raw
            # parameter text, and only when an explicit override supplies it: with no override the
            # registered name IS the effective MSSQL_DB_NAME, which the authority resolves and
            # checks itself, reporting it under that key rather than under a parameter this run
            # never received.
            $registeredDatastoreDatabaseValue = ""
            if (-not [string]::IsNullOrWhiteSpace($DataStoreDatabaseName)) {
                $registeredDatastoreDatabaseValue = Get-RegisteredDatastoreDatabaseValue -DatastoreDatabaseName $DataStoreDatabaseName
            }
            $authorityArguments = @{
                EnvironmentFile = $resolvedEnvironmentFile
                ContainerName = "dms-mssql"
                SaPassword = $mssqlPassword
                RegisteredDatastoreDatabaseName = $registeredDatastoreDatabaseValue
            }
            # The restore handoff's name is reported under its own parameter; other runs keep the
            # authority's default source key.
            if ($restoreReuse) { $authorityArguments.RegisteredDatastoreDatabaseSourceKey = $registrationDatabaseSource }
            Assert-MssqlTopologyPhysicalConsistency @authorityArguments
        }
        else {
            # PostgreSQL's registration transport does have a sound offline verdict, and its own
            # predicate already models it: the name is serialized into the datastore connection
            # string, parsed back by the provider, and created with a QUOTED identifier, so only a
            # name that parses back AS the reserved name collides. This is the predicate the
            # published start script's fail-fast boundary uses - deliberately not the rule that
            # governs POSTGRES_DB_NAME's own initialization path, where postgresql-init.sh hands the
            # name to createdb and only the exact reserved literal collides. The two now agree except
            # for a bare trailing line feed, which this transport can collapse and createdb preserves.
            if (Test-RegisteredDatastoreNameCollidesWithReservedCmsDatabase -DatabaseEngine "postgresql" -DatastoreDatabaseName $postgresDbName) {
                $datastoreNameSource =
                    if ([string]::IsNullOrWhiteSpace($DataStoreDatabaseName)) { "POSTGRES_DB_NAME" }
                    else { $registrationDatabaseSource }
                # Names the source key and the reserved literal only - never the caller's own value.
                throw "The DMS datastore database name resolved from '$datastoreNameSource' must be provably distinct from 'edfi_configurationservice' with -SeparateConfigDatabase: that is the dedicated Configuration Service database, and registering the datastore against it would reintroduce the shared topology the switch opts out of. On PostgreSQL the name is compared as the provider parses it - SchemaTools creates it with a quoted identifier, so nothing folds; only a name that parses back to that reserved name collides, and the measured non-exact case is a trailing line feed, which connection-string parsing removes. The resolved value is withheld."
            }
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

    if ($restoreReuse) {
        # Restore with -SeparateConfigDatabase: the surviving Configuration Service database may
        # already hold the data store; reuse it only after its stored target is verified (see
        # Resolve-RestoreSeparateConfigDataStore). With none, fall through to the normal registration.
        $dataStores = @(Get-DataStore -CmsUrl $cmsUrl -AccessToken $configToken -Tenant $tenant -Limit 500)
        $reusedDataStore = Resolve-RestoreSeparateConfigDataStore `
            -DataStores $dataStores `
            -DatabaseEngine $DatabaseEngine `
            -RestoreTargetDatabaseName $RestoreTargetDatabaseName `
            -EnvValues $envValues `
            -Tenant $tenant
        if ($null -ne $reusedDataStore) {
            Write-Information "Reusing CMS data store id=$(Format-LogSafeText $reusedDataStore.id): its stored connection string targets the restored database on the composed $DatabaseEngine service." -InformationAction Continue
            if ($AddSmokeTestCredentials) {
                Import-Module "$PSScriptRoot/../smoke_test/modules/SmokeTest.psm1" -Force
                Write-Information "Creating smoke test credentials." -InformationAction Continue
                Get-SmokeTestCredential -ConfigServiceUrl $cmsUrl -DataStoreIds @([long]$reusedDataStore.id) -Tenant $tenant | Out-Null
                Write-Information "Smoke test credentials created." -InformationAction Continue
            }

            return ConvertTo-ConfigureResult `
                -DataStoreIds @([long]$reusedDataStore.id) `
                -Tenant $tenant `
                -CmsReadOnlyAccess $cmsReadOnlyAccess
        }
        Write-Information "No CMS data store exists for the restored target yet; registering one." -InformationAction Continue
    }

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
            -DatabaseEngine $DatabaseEngine `
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
        -DatabaseEngine $DatabaseEngine `
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
    -SeparateConfigDatabase:$SeparateConfigDatabase `
    -RestoreTargetDatabaseName $RestoreTargetDatabaseName
