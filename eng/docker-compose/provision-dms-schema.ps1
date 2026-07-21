# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Schema provisioning phase. Supports PostgreSQL and SQL Server data stores.
.DESCRIPTION
    Invokes SchemaTools against each distinct effective target database from the
    selected CMS instances. The dialect (--dialect pgsql|mssql) is auto-detected from
    the shape of the data store connection string CMS hands back: PostgreSQL uses
    host/database/username keys, SQL Server uses Server/Database/User Id keys. The
    Docker-internal database host is translated to the host-side mapped port before
    SchemaTools runs (dms-postgresql -> localhost:POSTGRES_PORT,
    dms-mssql -> 127.0.0.1,MSSQL_PORT).

    See command-boundaries.md Section 3.5 for the phase contract and
    01-schema-deployment-safety.md for the DMS-1151 story.
#>

[CmdletBinding()]
param(
    [string]$EnvironmentFile,
    [Alias("InstanceId")]
    [long[]]$DataStoreId = @(),
    [int[]]$SchoolYear = @(),

    # Database engine overlay selector for direct invocation of this script. Composes the
    # .env.mssql overlay onto -EnvironmentFile (Resolve-DatabaseEngineEnvironmentFile) so the
    # dialect guard in New-ProvisionTarget and the host-side connection-string translation in
    # Convert-CmsConnectionStringToHostSideTarget see the same engine the caller intends. The
    # bootstrap wrapper does not pass this parameter: its -EnvironmentFile is already composed,
    # and the default "postgresql" is a no-op via Resolve-DatabaseEngineEnvironmentFile's
    # idempotency guard (an env already carrying DMS_DATASTORE=mssql is returned unchanged).
    [ValidateSet("postgresql", "mssql")]
    [string]$DatabaseEngine = "postgresql"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Import-Module "$PSScriptRoot/bootstrap-manifest.psm1" -Force -Global
Import-Module "$PSScriptRoot/bootstrap-schema-tool.psm1" -Force -Global
Import-Module "$PSScriptRoot/bootstrap-schema-workspace.psm1" -Force -Global
Import-Module "$PSScriptRoot/env-utility.psm1" -Force -Global
Import-Module "$PSScriptRoot/../Dms-Management.psm1" -Force

if (-not (Get-Command Format-LogSafeText -ErrorAction SilentlyContinue)) {
    function Format-LogSafeText {
        param($Value)

        if ($null -eq $Value) { return "" }
        $text = [string]$Value
        $builder = [System.Text.StringBuilder]::new()
        foreach ($character in $text.ToCharArray()) {
            # Comma is whitelisted so SQL Server "host,port" targets log readably; newlines
            # and other control characters stay stripped, which is what prevents log forging.
            if ([char]::IsLetterOrDigit($character) -or
                $character -eq " " -or
                $character -eq "_" -or
                $character -eq "-" -or
                $character -eq "." -or
                $character -eq ":" -or
                $character -eq "," -or
                $character -eq "/") {
                $null = $builder.Append($character)
            }
        }

        return $builder.ToString()
    }
}

if (-not (Get-Command Format-LogSafePath -ErrorAction SilentlyContinue)) {
    function Format-LogSafePath {
        param($Value)

        if ($null -eq $Value) { return "" }
        $text = [string]$Value
        $builder = [System.Text.StringBuilder]::new()
        foreach ($character in $text.ToCharArray()) {
            if (-not [char]::IsControl($character)) {
                $null = $builder.Append($character)
            }
        }

        return $builder.ToString()
    }
}

function Resolve-ProvisionEnvironmentFile {
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

function Get-ProvisionProperty {
    param(
        $Object,

        [string[]]
        $Names
    )

    foreach ($name in $Names) {
        if ($Object -is [System.Collections.IDictionary] -and $Object.ContainsKey($name)) {
            return $Object[$name]
        }

        $property = $Object.PSObject.Properties[$name]
        if ($null -ne $property) {
            return $property.Value
        }
    }

    return $null
}

function Get-ProvisionRouteContexts {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns a collection of route contexts; the plural noun reflects the return shape.')]
    param(
        $Instance
    )

    $routeContexts = Get-ProvisionProperty -Object $Instance -Names @("dataStoreContexts", "DataStoreContexts", "routeContexts", "RouteContexts")
    if ($null -eq $routeContexts -or $routeContexts -is [string]) {
        return @()
    }

    return @($routeContexts)
}

function Resolve-EnvPlaceholdersInText {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive: $EnvValues is consumed inside the nested regex replacement script block.')]
    param(
        [string]
        $Text,

        [hashtable]
        $EnvValues
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return ""
    }

    return [regex]::Replace(
        $Text,
        '\$\{(?<Name>[A-Za-z0-9_]+)\}',
        {
            param($match)

            $name = $match.Groups["Name"].Value
            if (-not $EnvValues.ContainsKey($name)) {
                throw "Connection string references env value '$(Format-LogSafeText $name)', but that key is absent from the environment file."
            }

            return [string]$EnvValues[$name]
        }
    )
}

function ConvertTo-ConnectionStringBuilder {
    <#
    .SYNOPSIS
    Parses a connection string into a [System.Data.Common.DbConnectionStringBuilder]. Unlike a
    naive ';' split, this correctly handles quoted values that themselves contain ';' or '=' (for
    example password="abc;123"). Returns $null instead of throwing when -AllowParseFailure is set
    and the input is not a valid connection string (used to detect CMS-encrypted base64 blobs).

    Callers must use the explicit get_/set_ accessors on the returned builder: PowerShell's
    property/indexer sugar (.ConnectionString, ['key'], .Keys) misbehaves on this
    IDictionary-implementing type and silently fails to parse.
    #>
    param(
        [string]
        $ConnectionString,

        [switch]
        $AllowParseFailure
    )

    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    try {
        $builder.set_ConnectionString($ConnectionString)
    }
    catch {
        if ($AllowParseFailure) {
            return $null
        }

        throw "CMS data store connection string is not a valid connection string."
    }

    return $builder
}

function Get-ConnectionStringValue {
    <#
    .SYNOPSIS
    Returns the first non-empty value among the supplied case-insensitive keys, or $null when none
    is present. Used instead of direct indexer access because PowerShell's ['key'] sugar misbehaves
    on DbConnectionStringBuilder.
    #>
    param(
        [System.Data.Common.DbConnectionStringBuilder]
        $Builder,

        [string[]]
        $Keys
    )

    foreach ($key in $Keys) {
        if ($Builder.ContainsKey($key)) {
            $value = [string]$Builder.get_Item($key)
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                return $value
            }
        }
    }

    return $null
}

function Set-ConnectionStringValue {
    <#
    .SYNOPSIS
    Updates or adds a value on the builder via set_Item: it overwrites the key when present or adds it
    when absent, so host/port can be mutated without duplicating keys or disturbing any other stored
    option. Unrelated options and their values are preserved; the emitted keyword casing is whatever the
    builder normalizes it to.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Mutates an in-memory builder object; no system state changes and no -WhatIf surface.')]
    param(
        [System.Data.Common.DbConnectionStringBuilder]
        $Builder,

        [string]
        $Key,

        [string]
        $Value
    )

    $Builder.set_Item($Key, $Value)
}

function Get-DatabaseNameFromConnectionString {
    param(
        [string]
        $ConnectionString,

        [switch]
        $AllowMissing
    )

    # AllowParseFailure: a CMS-encrypted connection string is an opaque base64 blob that is not a
    # valid connection string. Treat an unparseable value as "no database name" so the caller falls
    # through to the decryption path rather than throwing here.
    $builder = ConvertTo-ConnectionStringBuilder -ConnectionString $ConnectionString -AllowParseFailure
    if ($null -ne $builder) {
        $databaseName = Get-ConnectionStringValue -Builder $builder -Keys @("database", "initial catalog")
        if (-not [string]::IsNullOrWhiteSpace($databaseName)) {
            return $databaseName
        }
    }

    if ($AllowMissing) {
        return $null
    }

    throw "CMS data store connection string did not contain a database name."
}

function Resolve-ExpectedProvisioningDialect {
    <#
    .SYNOPSIS
    Resolves the SchemaTools dialect the effective environment expects for provisioning targets,
    from the effective DMS_DATASTORE value: mssql -> mssql, postgresql -> pgsql. A missing or
    blank value defaults to postgresql -> pgsql, matching local-dms.yml's compose-level default
    (AppSettings__Datastore: ${DMS_DATASTORE:-postgresql}).
    #>
    param(
        [hashtable]
        $EnvValues
    )

    $engineValue = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "DMS_DATASTORE" -DefaultValue "postgresql"
    $expectedDialect = if ($engineValue -eq "mssql") { "mssql" } else { "pgsql" }

    return [pscustomobject]@{
        EngineValue = $engineValue
        ExpectedDialect = $expectedDialect
    }
}

function Convert-MssqlCmsConnectionStringToHostSideTarget {
    <#
    .SYNOPSIS
    Builds an effective host-side SQL Server provisioning target from a CMS-stored data store
    connection string. Translates the Docker-internal server (Server=dms-mssql[,1433]) to the
    host-side 127.0.0.1,MSSQL_PORT while preserving the user, password, database, and every
    other stored option. A non-Docker server (e.g. an external SQL Server configured per
    instance) is preserved as-is.
    #>
    param(
        [Parameter(Mandatory)]
        [System.Data.Common.DbConnectionStringBuilder]
        $Builder,

        [Parameter(Mandatory)]
        [hashtable]
        $EnvValues
    )

    # SQL Server accepts either Database or Initial Catalog for the database name.
    $databaseName = Get-ConnectionStringValue -Builder $Builder -Keys @("database", "initial catalog")
    if ([string]::IsNullOrWhiteSpace($databaseName)) {
        throw "CMS data store connection string did not contain a database name."
    }

    $server = Get-ConnectionStringValue -Builder $Builder -Keys @("server", "data source")
    if ([string]::IsNullOrWhiteSpace($server)) {
        throw "CMS data store connection string is missing the server key."
    }

    $instanceUser = Get-ConnectionStringValue -Builder $Builder -Keys @("user id", "uid")
    if ([string]::IsNullOrWhiteSpace($instanceUser)) {
        throw "CMS data store connection string is missing the user id key."
    }

    # SQL Server encodes host and port together as "host,port" (and named instances as
    # "host\instance"). Split off the host to decide whether this is the Docker-internal target.
    $serverHost = ($server -split ',')[0].Trim()

    $effectiveServer = $server
    if ($serverHost.Equals("dms-mssql", [System.StringComparison]::OrdinalIgnoreCase)) {
        $mssqlPort = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "MSSQL_PORT" -DefaultValue "1435"
        # mssql.yml publishes this port on 127.0.0.1 only (IPv4); use the literal address so
        # SqlClient cannot resolve "localhost" to ::1 first and stall or fail on an IPv6 host.
        $effectiveServer = "127.0.0.1,$mssqlPort"
    }

    # Mutate only the server; every other stored option (password, TrustServerCertificate,
    # Encrypt, a password containing ';' or '=', etc.) is carried through verbatim and correctly
    # re-quoted by the builder. Set whichever server key the source used so we never duplicate it.
    $serverKey = if ($Builder.ContainsKey("data source")) { "data source" } else { "server" }
    Set-ConnectionStringValue -Builder $Builder -Key $serverKey -Value $effectiveServer

    $hostConnectionString = $Builder.get_ConnectionString()

    # The port is encoded inside the server value for SQL Server; surface it for the summary.
    $effectivePort =
        if ($effectiveServer -match ',') { (($effectiveServer -split ',', 2)[1]).Trim() }
        else { "1433" }

    # TargetKey identifies an effective provisioning target so two instances pointing at the same
    # physical database share a single SchemaTools invocation. The server value already encodes
    # host + port.
    $targetKey = ("{0}|{1}|{2}|{3}" -f
        "mssql",
        $effectiveServer.ToLowerInvariant(),
        $databaseName.ToLowerInvariant(),
        $instanceUser.ToLowerInvariant())

    return [pscustomobject]@{
        Dialect = "mssql"
        Host = $effectiveServer
        Port = $effectivePort
        Username = $instanceUser
        DatabaseName = $databaseName
        HostConnectionString = $hostConnectionString
        TargetKey = $targetKey
    }
}

function Convert-CmsConnectionStringToHostSideTarget {
    <#
    .SYNOPSIS
    Builds an effective host-side provisioning target from a single CMS-stored data store
    connection string. Translates the Docker-internal PostgreSQL hostname/port to the host-side
    mapped port while preserving the instance-specific username, password, and database name.
    Non-Docker hosts (e.g. external PostgreSQL servers configured per instance) are preserved
    as-is. SQL Server connection strings are dispatched to
    Convert-MssqlCmsConnectionStringToHostSideTarget.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $ConnectionString,

        [Parameter(Mandatory)]
        [hashtable]
        $EnvValues
    )

    $builder = ConvertTo-ConnectionStringBuilder -ConnectionString $ConnectionString
    # The provisioning dialect is the effective datastore provider (DMS_DATASTORE), not inferred from the
    # connection string. SchemaTools provisions both (--dialect pgsql|mssql); New-ProvisionTarget has
    # already verified the stored connection's shape matches this engine (stale-datastore guard).
    $dialect = (Resolve-ExpectedProvisioningDialect -EnvValues $EnvValues).ExpectedDialect

    if ($dialect -eq "mssql") {
        return Convert-MssqlCmsConnectionStringToHostSideTarget -Builder $builder -EnvValues $EnvValues
    }

    # PostgreSQL path: the surviving keys are database / host / username.
    $databaseName = Get-ConnectionStringValue -Builder $builder -Keys @("database")
    if ([string]::IsNullOrWhiteSpace($databaseName)) {
        throw "CMS data store connection string did not contain a database name."
    }

    $instanceHost = Get-ConnectionStringValue -Builder $builder -Keys @("host")
    if ([string]::IsNullOrWhiteSpace($instanceHost)) {
        throw "CMS data store connection string is missing the host key."
    }

    $instanceUser = Get-ConnectionStringValue -Builder $builder -Keys @("username")
    if ([string]::IsNullOrWhiteSpace($instanceUser)) {
        throw "CMS data store connection string is missing the username key."
    }

    # PostgreSQL canonically defaults to 5432 when port is omitted. Docker-internal targets
    # (host=dms-postgresql) keep 5432 here and are translated to localhost:POSTGRES_PORT by
    # the host-side translation block below.
    $portValue = Get-ConnectionStringValue -Builder $builder -Keys @("port")
    $instancePort = if (-not [string]::IsNullOrWhiteSpace($portValue)) { $portValue } else { "5432" }

    # Translate Docker-internal PostgreSQL coordinates to host-side coordinates. Any other
    # host (e.g. an external managed PostgreSQL server) is left untouched so per-instance
    # routing is preserved.
    $effectiveHost = $instanceHost
    $effectivePort = $instancePort
    if ($instanceHost.Equals("dms-postgresql", [System.StringComparison]::OrdinalIgnoreCase) -and
        $instancePort -eq "5432") {
        $effectiveHost = "localhost"
        $effectivePort = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "POSTGRES_PORT" -DefaultValue "5432"
    }

    # Mutate only host and port. Every other option the CMS stored (SSL Mode, Trust Server
    # Certificate, Timeout, Pooling, a password containing ';' or '=', etc.) is carried through
    # verbatim by the builder, which also re-quotes values correctly. Setting port also adds it
    # when the source omitted one, so the provisioning target always carries an explicit,
    # reachable port (5432 for an external host, or the host-side mapped POSTGRES_PORT).
    Set-ConnectionStringValue -Builder $builder -Key "host" -Value $effectiveHost
    Set-ConnectionStringValue -Builder $builder -Key "port" -Value $effectivePort

    $hostConnectionString = $builder.get_ConnectionString()

    # TargetKey identifies an effective provisioning target. Two instances pointing at the
    # same physical database (same dialect, translated host, port, database, and user) share
    # a provisioning invocation. Differing on any field - including username - produces a
    # separate target so we never collapse instances that happen to share a database name on
    # different hosts or under different roles.
    $targetKey = ("{0}|{1}|{2}|{3}|{4}" -f
        $dialect,
        $effectiveHost.ToLowerInvariant(),
        $effectivePort,
        $databaseName.ToLowerInvariant(),
        $instanceUser.ToLowerInvariant())

    return [pscustomobject]@{
        Dialect = $dialect
        Host = $effectiveHost
        Port = $effectivePort
        Username = $instanceUser
        DatabaseName = $databaseName
        HostConnectionString = $hostConnectionString
        TargetKey = $targetKey
    }
}

function ConvertFrom-CmsEncryptedConnectionString {
    param(
        [string]
        $ProtectedConnectionString,

        [hashtable]
        $EnvValues
    )

    $encryptionKey = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "DMS_CONFIG_DATABASE_ENCRYPTION_KEY"
    if ([string]::IsNullOrWhiteSpace($encryptionKey)) {
        throw "CMS data store connection string is encrypted, but DMS_CONFIG_DATABASE_ENCRYPTION_KEY is not set in the environment file."
    }

    try {
        $encryptedBytes = [Convert]::FromBase64String($ProtectedConnectionString)
    }
    catch {
        throw "CMS data store connection string did not contain a database name and was not valid CMS encrypted base64."
    }

    if ($encryptedBytes.Length -le 16) {
        throw "CMS data store encrypted connection string payload is invalid."
    }

    $keyText = $encryptionKey.PadRight(32, "0").Substring(0, 32)
    $keyBytes = [System.Text.Encoding]::UTF8.GetBytes($keyText)
    $iv = [byte[]]::new(16)
    [Array]::Copy($encryptedBytes, 0, $iv, 0, 16)

    $cipherText = [byte[]]::new($encryptedBytes.Length - 16)
    [Array]::Copy($encryptedBytes, 16, $cipherText, 0, $cipherText.Length)

    $aes = [System.Security.Cryptography.Aes]::Create()
    # CMS encrypts connection strings with AES-CBC / PKCS7; set explicitly rather than relying on .NET defaults.
    $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
    $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
    try {
        $aes.Key = $keyBytes
        $aes.IV = $iv
        $decryptor = $aes.CreateDecryptor()
        try {
            $plainTextBytes = $decryptor.TransformFinalBlock($cipherText, 0, $cipherText.Length)
            return [System.Text.Encoding]::UTF8.GetString($plainTextBytes)
        }
        finally {
            $decryptor.Dispose()
        }
    }
    catch {
        throw "CMS data store encrypted connection string could not be decrypted with DMS_CONFIG_DATABASE_ENCRYPTION_KEY."
    }
    finally {
        $aes.Dispose()
    }
}

function Resolve-CmsInstanceConnectionString {
    param(
        [string]
        $ConnectionString,

        [hashtable]
        $EnvValues
    )

    $resolvedConnectionString = Resolve-EnvPlaceholdersInText -Text $ConnectionString -EnvValues $EnvValues
    if ($null -ne (Get-DatabaseNameFromConnectionString -ConnectionString $resolvedConnectionString -AllowMissing)) {
        return $resolvedConnectionString
    }

    $decryptedConnectionString = ConvertFrom-CmsEncryptedConnectionString `
        -ProtectedConnectionString $resolvedConnectionString `
        -EnvValues $EnvValues

    return Resolve-EnvPlaceholdersInText -Text $decryptedConnectionString -EnvValues $EnvValues
}


function Resolve-ProvisionTargetInstances {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns the collection of selected target instances; the plural noun reflects the return shape.')]
    param(
        [object[]]
        $Instances,

        [Alias("InstanceId")]
        [long[]]
        $DataStoreId = @(),

        [int[]]
        $SchoolYear = @(),

        [string]
        $Tenant = ""
    )

    if ($DataStoreId.Count -gt 0 -and $SchoolYear.Count -gt 0) {
        throw "-DataStoreId and -SchoolYear are mutually exclusive. Pass only one selector."
    }

    if ($DataStoreId.Count -gt 0) {
        $selected = [System.Collections.ArrayList]::new()
        foreach ($id in $DataStoreId) {
            $matchedInstances = @($Instances | Where-Object { [long](Get-ProvisionProperty -Object $_ -Names @("id", "Id")) -eq [long]$id })
            if ($matchedInstances.Count -eq 0) {
                throw "Data store $(Format-LogSafeText $id) was not found in CMS for tenant '$(Format-LogSafeText $Tenant)'."
            }

            $null = $selected.Add($matchedInstances[0])
        }

        return @($selected)
    }

    if ($SchoolYear.Count -gt 0) {
        $selected = [System.Collections.ArrayList]::new()
        foreach ($year in $SchoolYear) {
            $matchedInstances = @($Instances | Where-Object {
                $matched = $false
                foreach ($routeContext in Get-ProvisionRouteContexts -Instance $_) {
                    $contextKey = [string](Get-ProvisionProperty -Object $routeContext -Names @("contextKey", "ContextKey"))
                    $contextValue = [string](Get-ProvisionProperty -Object $routeContext -Names @("contextValue", "ContextValue"))
                    if ($contextKey -eq "schoolYear" -and $contextValue -eq [string]$year) {
                        $matched = $true
                        break
                    }
                }
                $matched
            })

            if ($matchedInstances.Count -eq 0) {
                throw "No data store found with route context schoolYear=$(Format-LogSafeText $year) for tenant '$(Format-LogSafeText $Tenant)'."
            }

            if ($matchedInstances.Count -gt 1) {
                $ids = ($matchedInstances | ForEach-Object { Get-ProvisionProperty -Object $_ -Names @("id", "Id") }) -join ", "
                throw "Multiple data stores found with route context schoolYear=$(Format-LogSafeText $year) (data store ids: $(Format-LogSafeText $ids)). Clean up duplicate CMS state before provisioning."
            }

            $null = $selected.Add($matchedInstances[0])
        }

        return @($selected)
    }

    if ($Instances.Count -eq 0) {
        throw "No data stores found in CMS. Run configure-local-data-store.ps1 before provisioning schemas."
    }

    if ($Instances.Count -gt 1) {
        $listing = ($Instances | ForEach-Object {
            "id=$(Format-LogSafeText (Get-ProvisionProperty -Object $_ -Names @('id', 'Id'))) name=$(Format-LogSafeText (Get-ProvisionProperty -Object $_ -Names @('name', 'Name')))"
        }) -join "`n"
        throw "Multiple data stores exist; cannot auto-select. Pass -DataStoreId or -SchoolYear to target specific data stores:`n$listing"
    }

    return @($Instances[0])
}

function New-ProvisionTarget {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Builds an in-memory provisioning target object; no system state changes and no -WhatIf surface.')]
    param(
        $Instance,

        [hashtable]
        $EnvValues
    )

    $rawDataStoreId = Get-ProvisionProperty -Object $Instance -Names @("id", "Id")
    if ($null -eq $rawDataStoreId) {
        throw "CMS data store is missing an id."
    }
    $dataStoreId = [long]$rawDataStoreId
    $connectionString = [string](Get-ProvisionProperty -Object $Instance -Names @("connectionString", "ConnectionString"))
    if ([string]::IsNullOrWhiteSpace($connectionString)) {
        throw "CMS data store $(Format-LogSafeText $dataStoreId) does not include a connection string."
    }

    $resolvedConnectionString = Resolve-CmsInstanceConnectionString `
        -ConnectionString $connectionString `
        -EnvValues $EnvValues

    # Stale-datastore guard. configure-local-data-store.ps1 -NoDataStore can select a pre-existing
    # route-unqualified CMS data store from a previous run with the OTHER database engine, which would
    # otherwise be provisioned silently while DMS starts against DMS_DATASTORE from this same environment.
    # The engine is NOT inferred from a keyword table: configure writes a PostgreSQL data store connection
    # with a `host=` key and a SQL Server one with a `Server=` key (no host), so the stored connection's
    # shape must match the effective DMS_DATASTORE engine. Runs before the host-side conversion so the
    # diagnostic is precise (the conversion would otherwise fail on a missing engine-specific key).
    $expectedProvisioningDialect = Resolve-ExpectedProvisioningDialect -EnvValues $EnvValues
    $storedBuilder = ConvertTo-ConnectionStringBuilder -ConnectionString $resolvedConnectionString
    # A PostgreSQL data store connection carries a `host` key; a SQL Server one carries `server` /
    # `data source` (never host). A connection with neither is not engine-identifiable here, so let the
    # host-side conversion below raise its specific missing-key diagnostic rather than guess an engine.
    $storedEngineValue =
        if ($storedBuilder.ContainsKey("host")) { "postgresql" }
        elseif ($storedBuilder.ContainsKey("server") -or $storedBuilder.ContainsKey("data source")) { "mssql" }
        else { $null }
    if ($null -ne $storedEngineValue -and $storedEngineValue -ne $expectedProvisioningDialect.EngineValue) {
        $dataStoreName = [string](Get-ProvisionProperty -Object $Instance -Names @("name", "Name"))
        $storedDatabaseName = [string](Get-ConnectionStringValue -Builder $storedBuilder -Keys @("database", "initial catalog"))
        throw "CMS data store $(Format-LogSafeText $dataStoreId) (name=$(Format-LogSafeText $dataStoreName), database=$(Format-LogSafeText $storedDatabaseName)) is a '$(Format-LogSafeText $storedEngineValue)' connection, but the environment's DMS_DATASTORE is '$(Format-LogSafeText $expectedProvisioningDialect.EngineValue)'. This usually means a stale data store from a previous run with the other database engine is being reused (commonly selected via -NoDataStore). Reset the local CMS state with start-local-dms.ps1 -d -v and rerun bootstrap-local-dms.ps1 -DatabaseEngine <engine>, or, when invoking the phase commands directly, make sure the effective environment file's DMS_DATASTORE matches the data store's engine (composed automatically by -DatabaseEngine)."
    }

    $target = Convert-CmsConnectionStringToHostSideTarget `
        -ConnectionString $resolvedConnectionString `
        -EnvValues $EnvValues

    $routeContexts = @(
        Get-ProvisionRouteContexts -Instance $Instance | ForEach-Object {
            [pscustomobject]@{
                ContextKey = [string](Get-ProvisionProperty -Object $_ -Names @("contextKey", "ContextKey"))
                ContextValue = [string](Get-ProvisionProperty -Object $_ -Names @("contextValue", "ContextValue"))
            }
        }
    )

    return [pscustomobject]@{
        DataStoreId = $dataStoreId
        RouteContexts = $routeContexts
        Dialect = $target.Dialect
        Host = $target.Host
        Port = $target.Port
        Username = $target.Username
        DatabaseName = $target.DatabaseName
        HostConnectionString = $target.HostConnectionString
        TargetKey = $target.TargetKey
    }
}

function Invoke-DmsSchemaProvision {
    param(
        [string]
        $ToolPath,

        [string[]]
        $SchemaPaths,

        [string]
        $ConnectionString,

        [string]
        $DatabaseName,

        [ValidateSet("pgsql", "mssql")]
        [string]
        $Dialect = "pgsql"
    )

    $arguments = @("ddl", "provision")
    foreach ($schemaPath in $SchemaPaths) {
        $arguments += @("--schema", $schemaPath)
    }
    $arguments += @(
        "--connection-string", $ConnectionString,
        "--dialect", $Dialect,
        "--create-database"
    )

    Write-Information "Invoking api-schema-tools ddl provision for database $(Format-LogSafeText $DatabaseName) with $($SchemaPaths.Count) schema file(s)." -InformationAction Continue

    if ($ToolPath.EndsWith(".ps1", [System.StringComparison]::OrdinalIgnoreCase)) {
        & pwsh -NoLogo -NoProfile -File $ToolPath @arguments
    }
    else {
        & $ToolPath @arguments
    }

    $exitCode = $LASTEXITCODE
    Write-Information "api-schema-tools exit code for database $(Format-LogSafeText $DatabaseName): $(Format-LogSafeText $exitCode)." -InformationAction Continue
    if ($exitCode -ne 0) {
        throw "api-schema-tools ddl provision failed for database $(Format-LogSafeText $DatabaseName) with exit code $(Format-LogSafeText $exitCode)."
    }
}

function Get-ProvisionIdeGuidance {
    <#
    .SYNOPSIS
    Builds the IDE next-step guidance block emitted by provision-dms-schema.ps1 after a
    successful pre-start provisioning. Returns a list of guidance lines. Pure / side-effect-
    free so the guidance can be exercised in tests independently of console formatting.
    #>
    param(
        [Parameter(Mandatory)]
        $SchemaWorkspace,

        [object[]]
        $ProvisionedTargets = @()
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("--- Schema Provisioning Summary ---")

    if ($ProvisionedTargets.Count -eq 0) {
        $lines.Add("No data store targets were provisioned (selectors matched zero data stores).")
    }
    else {
        $lines.Add("Provisioned $($ProvisionedTargets.Count) database target(s):")
        foreach ($target in $ProvisionedTargets) {
            $dataStoreList = ($target.DataStoreIds | ForEach-Object { [string]$_ }) -join ", "
            $lines.Add("  - database=$(Format-LogSafeText $target.DatabaseName) host=$(Format-LogSafeText $target.Host) port=$(Format-LogSafeText $target.Port) user=$(Format-LogSafeText $target.Username) data-store-ids=[$dataStoreList] status=$($target.Status)")
        }
    }

    $lines.Add("")
    $lines.Add("--- IDE next-step guidance ---")
    $lines.Add("Bootstrap manifest:      $(Format-LogSafePath $SchemaWorkspace.BootstrapManifestPath)")
    $lines.Add("  ApiSchema manifest:    $(Format-LogSafePath $SchemaWorkspace.ApiSchemaManifestPath)")
    if ($SchemaWorkspace.EffectiveSchemaHash) {
        $lines.Add("  Effective schema hash: $(Format-LogSafeText $SchemaWorkspace.EffectiveSchemaHash)")
    }
    $lines.Add("")
    $lines.Add("Expected DMS runtime appsettings for IDE-hosted launch (staged workspace is runtime-authoritative):")
    $apiSchemaRoot = [System.IO.Path]::GetDirectoryName($SchemaWorkspace.ApiSchemaManifestPath)
    $lines.Add("  AppSettings__UseApiSchemaPath = true")
    $lines.Add("  AppSettings__ApiSchemaPath    = $(Format-LogSafePath $apiSchemaRoot)")
    $lines.Add("Note: in bootstrap mode these flags are activated automatically; the staged workspace is runtime-authoritative.")
    $lines.Add("      IDE-hosted DMS reads the staged schema directly via ApiSchemaPath (UseApiSchemaPath=true).")

    return $lines.ToArray()
}

function Test-CmsReadOnlyAccessEnvPresent {
    <#
    .SYNOPSIS
    Returns $true when the env file explicitly supplies at least one of the three
    CONFIG_SERVICE_CLIENT_* keys with a non-blank value. Used to gate the optional
    CMSReadOnlyAccess guidance so defaults alone do not advertise the block as available.
    #>
    param(
        [hashtable]
        $EnvValues
    )

    if ($null -eq $EnvValues) {
        return $false
    }

    foreach ($name in @("CONFIG_SERVICE_CLIENT_ID", "CONFIG_SERVICE_CLIENT_SCOPE", "CONFIG_SERVICE_CLIENT_SECRET")) {
        if ($EnvValues.ContainsKey($name) -and -not [string]::IsNullOrWhiteSpace([string]$EnvValues[$name])) {
            return $true
        }
    }

    return $false
}

function Get-ProvisionCmsReadOnlyAccessGuidance {
    <#
    .SYNOPSIS
    Produces optional CMSReadOnlyAccess guidance lines for IDE-hosted launch. Returns an empty
    array when none of the three CONFIG_SERVICE_CLIENT_* keys are explicitly present in the
    env file. Per command-boundaries.md Section 3.4, "may include" means "include when actually
    populated"; a default-derived client id alone does not satisfy that contract.
    #>
    param(
        [hashtable]
        $EnvValues
    )

    if (-not (Test-CmsReadOnlyAccessEnvPresent -EnvValues $EnvValues)) {
        return @()
    }

    $clientId = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "CONFIG_SERVICE_CLIENT_ID" -DefaultValue "CMSReadOnlyAccess"
    $scope = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "CONFIG_SERVICE_CLIENT_SCOPE" -DefaultValue "edfi_admin_api/readonly_access"
    $secret = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "CONFIG_SERVICE_CLIENT_SECRET"
    $encryptionKey = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "DMS_CONFIG_DATABASE_ENCRYPTION_KEY"

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("")
    $lines.Add("Configuration Service read-only access (configured by start-local-dms.ps1's identity setup):")
    $lines.Add("  ConfigurationServiceSettings__ClientId = $(Format-LogSafeText $clientId)")
    $lines.Add("  ConfigurationServiceSettings__Scope    = $(Format-LogSafeText $scope)")
    if ([string]::IsNullOrWhiteSpace($secret)) {
        $lines.Add("  ConfigurationServiceSettings__ClientSecret = (set in your IDE/env; not staged by provisioning)")
    }
    else {
        $lines.Add("  ConfigurationServiceSettings__ClientSecret = (present in environment file)")
    }
    if ([string]::IsNullOrWhiteSpace($encryptionKey)) {
        $lines.Add("  ConfigurationServiceSettings__EncryptionKey = (set to DMS_CONFIG_DATABASE_ENCRYPTION_KEY from your .env file; .env.example default DefaultEncryptionKey32CharactersX1)")
    }
    else {
        $lines.Add("  ConfigurationServiceSettings__EncryptionKey = (present in environment file as DMS_CONFIG_DATABASE_ENCRYPTION_KEY)")
    }

    return $lines.ToArray()
}

function Write-ProvisionSummary {
    <#
    .SYNOPSIS
    Writes the post-provisioning summary and the IDE next-step guidance derived from the
    staged schema workspace and the targets just provisioned. The staged schema workspace is
    runtime-authoritative in bootstrap mode; this guidance reflects the active runtime path.
    #>
    param(
        [Parameter(Mandatory)]
        [hashtable]
        $EnvValues,

        [Parameter(Mandatory)]
        $SchemaWorkspace,

        [object[]]
        $ProvisionedTargets = @()
    )

    $guidance = Get-ProvisionIdeGuidance -SchemaWorkspace $SchemaWorkspace -ProvisionedTargets $ProvisionedTargets
    foreach ($line in $guidance) {
        Write-Information $line -InformationAction Continue
    }

    foreach ($line in Get-ProvisionCmsReadOnlyAccessGuidance -EnvValues $EnvValues) {
        Write-Information $line -InformationAction Continue
    }
}

function Invoke-ProvisionDmsSchema {
    param(
        [string]
        $EnvironmentFile,

        [Alias("InstanceId")]
        [long[]]
        $DataStoreId = @(),

        [int[]]
        $SchoolYear = @(),

        [ValidateSet("postgresql", "mssql")]
        [string]
        $DatabaseEngine = "postgresql"
    )

    if ($DataStoreId.Count -gt 0 -and $SchoolYear.Count -gt 0) {
        throw "-DataStoreId and -SchoolYear are mutually exclusive. Pass only one selector."
    }

    $resolvedEnvironmentFile = Resolve-ProvisionEnvironmentFile -Path $EnvironmentFile
    # Compose the MSSQL engine overlay for -DatabaseEngine mssql; mirrors
    # configure-local-data-store.ps1 and start-local-dms.ps1. Direct invocation with
    # -DatabaseEngine mssql layers the overlay onto a custom -EnvironmentFile; wrapper-forwarded
    # files are already composed and no-op via the DMS_DATASTORE guard in
    # Resolve-DatabaseEngineEnvironmentFile. Like configure, this phase composes the overlay only for
    # the DMS datastore; the Configuration Service engine/connection/database agreement is owned and
    # validated by the start scripts (Resolve-EffectiveConfigRuntimeContract), so it is not re-checked here.
    $resolvedEnvironmentFile = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine $DatabaseEngine -BaseEnvironmentFile $resolvedEnvironmentFile -DockerComposeRoot $PSScriptRoot
    $envValues = ReadValuesFromEnvFile -EnvironmentFile $resolvedEnvironmentFile
    $cmsUrl = Resolve-CmsBaseUrl -EnvValues $envValues
    $tenant = Get-EnvValueOrDefault -EnvValues $envValues -Name "CONFIG_SERVICE_TENANT"

    $schemaWorkspace = Resolve-BootstrapSchemaWorkspace
    $schemaPaths = [string[]]@($schemaWorkspace.CoreSchemaPath) + [string[]]@($schemaWorkspace.ExtensionSchemaPaths)
    Write-Information "Schema workspace ready. Core schema: $(Format-LogSafeText $schemaWorkspace.CoreSchemaPath). Extensions: $($schemaWorkspace.ExtensionSchemaPaths.Count)." -InformationAction Continue

    # DMS-1151: bootstrap admin token acquisition. Per command-boundaries.md Section 3.4/Section 3.5,
    # configure-local-data-store.ps1 owns the /connect/register side effect for the bootstrap
    # admin client; this phase is auth-consumer only. Client id/secret are resolved through the
    # shared -EnvironmentFile helper so configure and provision always agree on the admin client
    # (DMS_BOOTSTRAP_ADMIN_CLIENT_ID / DMS_BOOTSTRAP_ADMIN_CLIENT_SECRET). If authentication
    # fails, surface an actionable error rather than silently re-registering against a CMS
    # environment where /connect/register may be locked down.
    $bootstrapAdmin = Resolve-BootstrapAdminClient -EnvValues $envValues
    try
    {
        $configToken = Get-CmsToken `
            -CmsUrl $cmsUrl `
            -ClientId $bootstrapAdmin.ClientId `
            -ClientSecret $bootstrapAdmin.ClientSecret
    }
    catch
    {
        throw "Bootstrap admin client '$(Format-LogSafeText $bootstrapAdmin.ClientId)' could not be authenticated against $(Format-LogSafeText $cmsUrl). Run configure-local-data-store.ps1 first to register the bootstrap admin client, or refresh its credentials. Underlying error: $(Format-LogSafeText ($_.Exception.Message))"
    }

    $instances = @(Get-DataStore -CmsUrl $cmsUrl -AccessToken $configToken -Tenant $tenant -Limit 500)
    if ($instances.Count -ge 500) {
        throw "Data store count reached the CMS query page size (500); pagination is not implemented in this bootstrap provisioning path. Reduce data stores or implement paging before provisioning."
    }
    $selectedInstances = Resolve-ProvisionTargetInstances `
        -Instances $instances `
        -DataStoreId $DataStoreId `
        -SchoolYear $SchoolYear `
        -Tenant $tenant

    $targets = @($selectedInstances | ForEach-Object { New-ProvisionTarget -Instance $_ -EnvValues $envValues })
    # Group by the effective provisioning target identity (dialect + translated host + port +
    # database + user). Grouping only by database name would silently collapse two instances
    # that share a database name on different physical hosts or under different users.
    $groups = $targets | Group-Object -Property TargetKey
    $schemaTool = Resolve-DmsSchemaTool -RequestedPath $env:DMS_SCHEMA_TOOL_PATH
    Write-Information "api-schema-tools resolved: $(Format-LogSafeText $schemaTool)." -InformationAction Continue

    $provisionedTargets = [System.Collections.ArrayList]::new()

    foreach ($group in $groups) {
        $target = @($group.Group)[0]
        $dataStoreIds = @($group.Group | ForEach-Object { [long]$_.DataStoreId })
        $ids = ($dataStoreIds | ForEach-Object { [string]$_ }) -join ", "
        Write-Information "Provisioning target database $(Format-LogSafeText $target.DatabaseName) on $(Format-LogSafeText $target.Host):$(Format-LogSafeText $target.Port) for data store id(s): $(Format-LogSafeText $ids)." -InformationAction Continue
        Invoke-DmsSchemaProvision `
            -ToolPath $schemaTool `
            -SchemaPaths $schemaPaths `
            -ConnectionString $target.HostConnectionString `
            -DatabaseName $target.DatabaseName `
            -Dialect $target.Dialect

        $null = $provisionedTargets.Add([pscustomobject]@{
            DatabaseName = $target.DatabaseName
            Host = $target.Host
            Port = $target.Port
            Dialect = $target.Dialect
            Username = $target.Username
            DataStoreIds = [long[]]$dataStoreIds
            Status = "Provisioned"
        })
    }

    Write-ProvisionSummary `
        -EnvValues $envValues `
        -SchemaWorkspace $schemaWorkspace `
        -ProvisionedTargets @($provisionedTargets)
}

if ($MyInvocation.InvocationName -eq '.') { return }

Invoke-ProvisionDmsSchema `
    -EnvironmentFile $EnvironmentFile `
    -DataStoreId $DataStoreId `
    -SchoolYear $SchoolYear `
    -DatabaseEngine $DatabaseEngine
