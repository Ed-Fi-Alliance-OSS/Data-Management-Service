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
    [string]$DatabaseEngine = "postgresql",

    # Declares that the stack this phase provisions was started with -SeparateConfigDatabase, so
    # the Configuration Service owns the dedicated edfi_configurationservice database and no DMS
    # schema may be deployed into it. Topology is DECLARED, never inferred from a database-name
    # spelling: pass the same switch the start invocation used. The start script's -InfraOnly
    # guidance prints it for you when it applies. Omit it for the default shared topology, where
    # the datastore and the Configuration Service share one database by design.
    #
    # This matters even though the phase takes its targets from what CMS already holds: a reused
    # data store (configure -NoDataStore) carries a STORED connection string this phase resolves,
    # decrypts, and hands to SchemaTools, so the dedicated database can be the effective target
    # without any name having been registered by this run.
    [Switch]$SeparateConfigDatabase
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Import-Module "$PSScriptRoot/bootstrap-manifest.psm1" -Force -Global
Import-Module "$PSScriptRoot/bootstrap-schema-tool.psm1" -Force -Global
Import-Module "$PSScriptRoot/bootstrap-schema-workspace.psm1" -Force -Global
Import-Module "$PSScriptRoot/env-utility.psm1" -Force -Global
# Shared Compose-equivalent resolver: env reads below honour Docker Compose interpolation
# precedence (an ambient process/shell value wins over the env file), so the SchemaTools host-side
# translation targets the same published port/credentials the containers received.
Import-Module "$PSScriptRoot/database-safety.psm1" -Force
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

    # Compose-equivalent read: the process/shell environment wins over the env file, matching the
    # values Docker Compose interpolates into the running containers.
    return Get-ComposeResolvedEnvValue -EnvironmentValues $EnvValues -Name $Name -DefaultValue $DefaultValue
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

    .DESCRIPTION
    FIRST-LISTED, not provider-effective. That is the right answer for a key family whose members
    cannot both appear in one string (PostgreSQL's host / database / username each have a single
    spelling here), and for asking whether ANY member is present at all. It is the wrong answer for
    deciding which member of a SQL Server synonym family the provider will resolve, because SqlClient
    keeps the LAST occurrence: see Get-MssqlProviderConnectionStringBuilder, which owns that
    decision.
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

function Get-ConnectionStringFamilyKey {
    <#
    .SYNOPSIS
    Returns the builder's own key spellings that belong to a synonym family, in the builder's key
    order. Empty when the string carries none.

    .DESCRIPTION
    Materialized into an array before being returned because DbConnectionStringBuilder.Keys is a live
    view over the builder: a caller that removes keys while enumerating it would mutate the collection
    under its own loop.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns the collection of family key spellings present; the plural noun would read as a different contract.')]
    param(
        [Parameter(Mandatory)]
        [System.Data.Common.DbConnectionStringBuilder]
        $Builder,

        [Parameter(Mandatory)]
        [string[]]
        $Aliases
    )

    return @(
        foreach ($key in $Builder.PSBase.Keys) {
            if ($Aliases -contains ([string]$key).ToLowerInvariant()) { [string]$key }
        }
    )
}

function Resolve-MssqlProviderAssemblyPath {
    <#
    .SYNOPSIS
    Locates the Microsoft.Data.SqlClient assembly shipped WITH the resolved api-schema-tools
    distribution - the exact build whose SqlConnectionStringBuilder will reparse whatever connection
    string this phase hands the tool. Returns $null when the distribution carries none.

    .DESCRIPTION
    Two layouts, probed in this order for a reason:

      - A BUILD output carries RID-specific implementations under runtimes/<rid>/lib/<tfm>/ and, at
        the top level, a reference facade whose constructor throws "Microsoft.Data.SqlClient is not
        supported on this platform". The .NET host picks the RID asset at runtime; Add-Type does not.
        So the runtimes/ candidate must be preferred - loading the facade first would occupy the
        assembly identity and make the real implementation unloadable afterwards.
      - A PUBLISH output has its RID assets already flattened next to the apphost and no runtimes/
        directory, so the top-level copy there IS the implementation.

    Only the current platform's RID family is considered: 'win' on Windows, 'unix' elsewhere. Nothing
    is downloaded, restored, or resolved from a NuGet cache - the assembly either ships with the tool
    that is about to run or it does not.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $SchemaToolPath
    )

    $toolDirectory = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($SchemaToolPath))
    if ([string]::IsNullOrWhiteSpace($toolDirectory) -or -not (Test-Path -LiteralPath $toolDirectory -PathType Container)) {
        return $null
    }

    $ridFamily = if ($IsWindows) { "win" } else { "unix" }
    $ridRoot = Join-Path (Join-Path $toolDirectory "runtimes") $ridFamily
    if (Test-Path -LiteralPath $ridRoot -PathType Container) {
        # Highest target-framework directory first, matching what the host would bind for a modern tool.
        $ridCandidate = @(
            Get-ChildItem -LiteralPath $ridRoot -Recurse -File -Filter "Microsoft.Data.SqlClient.dll" -ErrorAction SilentlyContinue |
                Sort-Object -Property FullName -Descending
        ) | Select-Object -First 1
        if ($null -ne $ridCandidate) {
            return $ridCandidate.FullName
        }
    }

    $flatCandidate = Join-Path $toolDirectory "Microsoft.Data.SqlClient.dll"
    if (Test-Path -LiteralPath $flatCandidate -PathType Leaf) {
        return $flatCandidate
    }

    return $null
}

function New-MssqlProviderConnectionStringBuilder {
    <#
    .SYNOPSIS
    Parses a SQL Server connection string with the Microsoft.Data.SqlClient build the resolved
    api-schema-tools ships, so a synonym-precedence verdict here is the verdict the tool itself will
    reach when it reparses the same text.

    .DESCRIPTION
    The provider must be the tool's own, not the in-box legacy System.Data.SqlClient. The two do not
    accept the same keyword set: Host Name In Certificate, Server Certificate, and Enclave
    Attestation Url are all valid to Microsoft.Data.SqlClient and rejected outright by the legacy
    provider, so parsing the whole string with the legacy builder would refuse external SQL Server
    datastores that SchemaTools accepts.

    Loaded via Add-Type from the path Resolve-MssqlProviderAssemblyPath selects, then PROBED by
    constructing an empty builder: the reference facade shipped at the top level of a build output
    loads happily and only fails on construction, so probing is what distinguishes "the provider is
    here" from "a stub is here". A failed probe is reported as an unavailable provider rather than
    surfacing later as a confusing parse error.

    Diagnostics never echo the parsed value: the provider's own unsupported-keyword message would
    carry connection-string text.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Constructs an in-memory provider builder; no system state changes and no -WhatIf surface.')]
    param(
        [Parameter(Mandatory)]
        [string]
        $ConnectionString,

        [Parameter(Mandatory)]
        [string]
        $SchemaToolPath
    )

    if (-not ([System.Management.Automation.PSTypeName]"Microsoft.Data.SqlClient.SqlConnectionStringBuilder").Type) {
        $assemblyPath = Resolve-MssqlProviderAssemblyPath -SchemaToolPath $SchemaToolPath
        if ([string]::IsNullOrWhiteSpace($assemblyPath)) {
            throw "The api-schema-tools distribution at '$(Format-LogSafePath $SchemaToolPath)' does not ship a loadable Microsoft.Data.SqlClient assembly, so this phase cannot determine which member of a repeated SQL Server connection-string synonym family that tool would resolve. Rewrite the data store connection string to name the server and the database once each, or provide a tool distribution that includes the provider."
        }

        Add-Type -Path $assemblyPath
    }

    try {
        # Probe first: the top-level facade in a build output constructs no builder at all.
        $null = [Microsoft.Data.SqlClient.SqlConnectionStringBuilder]::new()
    }
    catch {
        throw "The Microsoft.Data.SqlClient assembly loaded from the api-schema-tools distribution is a platform reference facade, not a usable implementation, so this phase cannot determine which member of a repeated SQL Server connection-string synonym family SchemaTools would resolve. Rewrite the data store connection string to name the server and the database once each."
    }

    try {
        return [Microsoft.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    }
    catch {
        throw "CMS data store connection string is not a valid SQL Server connection string for the Microsoft.Data.SqlClient provider SchemaTools parses it with. The resolved value is withheld."
    }
}

function Get-MssqlEffectiveConnectionStringValue {
    <#
    .SYNOPSIS
    Returns the value SchemaTools' provider will resolve for one SQL Server synonym family, without
    subjecting the rest of the connection string to that provider's keyword validation.

    .DESCRIPTION
    Which member of a family wins is a question only when a string names more than one of them, and
    that is the only case this consults the provider for:

      - NO family key present: no value.
      - EXACTLY ONE family key present: that key's value, read from the generic
        System.Data.Common.DbConnectionStringBuilder. Already provider-faithful - a key repeated on
        its own resolves to its last occurrence under SqlClient, and the generic builder likewise
        holds the last value for a repeated key.
      - TWO OR MORE family keys present: genuinely ambiguous, and neither builder already in play can
        settle it. Get-ConnectionStringValue over an ordered alias list returns the FIRST listed
        synonym, so "Database=safe;Initial Catalog=edfi_configurationservice" reports 'safe' while a
        reparse resolves 'edfi_configurationservice' - one database validated, a different one
        deployed into. The generic builder knows no synonyms at all, and its key order is not
        occurrence order: a repeated key keeps its FIRST position while taking its LAST value
        ("Database=first;Initial Catalog=second;Database=last" enumerates database before initial
        catalog), so no traversal of that collection reconstructs provider precedence. Only here is
        the tool's own provider loaded and asked.

    Scoping the provider to the ambiguous case is what keeps a Microsoft.Data.SqlClient-only keyword
    from being rejected on the common path: an unambiguous string is never handed to a strict keyword
    validator at all, and every option it carries passes through the generic builder untouched.
    #>
    param(
        [Parameter(Mandatory)]
        [System.Data.Common.DbConnectionStringBuilder]
        $Builder,

        [Parameter(Mandatory)]
        [string[]]
        $Aliases,

        [Parameter(Mandatory)]
        [string]
        $ProviderPropertyName,

        [Parameter(Mandatory)]
        [string]
        $ConnectionString,

        [Parameter(Mandatory)]
        [string]
        $SchemaToolPath
    )

    $presentKeys = @(Get-ConnectionStringFamilyKey -Builder $Builder -Aliases $Aliases)

    if ($presentKeys.Count -eq 0) {
        return $null
    }

    if ($presentKeys.Count -eq 1) {
        return [string]$Builder.get_Item($presentKeys[0])
    }

    $provider = New-MssqlProviderConnectionStringBuilder `
        -ConnectionString $ConnectionString `
        -SchemaToolPath $SchemaToolPath

    return [string]$provider.$ProviderPropertyName
}

function Split-MssqlServerEndpoint {
    <#
    .SYNOPSIS
    Splits a SqlClient data-source value into its host and its comma-delimited port, removing one
    leading "tcp:" protocol prefix so endpoint decisions see the host itself.

    .DESCRIPTION
    "Server=tcp:localhost,<port>" is documented SqlClient syntax
    (learn.microsoft.com/troubleshoot/sql/connect/use-server-name-parameter-connection-string) and
    reaches the same server "localhost,<port>" does. Comparing the raw value instead leaves the
    prefixed spelling looking like a hostname of its own: it escapes the Docker-internal translation
    (tcp:dms-mssql,1433 stays untranslated) and it reads as an external server to the separate-topology
    guard, which is the whole point of the guard.

    Deliberately narrow, and MSSQL-only: exactly one "tcp:" prefix, then the existing comma split.
    Named pipes, IPv6 literals, "(local)", ".", LocalDB, and named instances are untouched - each is a
    distinct endpoint grammar, and inventing rules for them here would decide reachability questions
    this phase has no way to verify.
    #>
    param(
        [string]
        $DataSource
    )

    $text = ([string]$DataSource).Trim()
    if ($text.StartsWith("tcp:", [System.StringComparison]::OrdinalIgnoreCase)) {
        $text = $text.Substring(4).Trim()
    }

    $parts = $text.Split(',', 2)

    return [pscustomobject]@{
        HostName = $parts[0].Trim()
        Port = if ($parts.Count -gt 1) { $parts[1].Trim() } else { "" }
    }
}

function Set-ConnectionStringFamilyValue {
    <#
    .SYNOPSIS
    Writes one effective value for a synonym family onto the builder and removes every OTHER member of
    that family, so the rendered string carries exactly one key the provider can resolve the family
    from and no surviving synonym can override it on reparse.

    .DESCRIPTION
    The retained key is the family member the source string actually used first, so an unrelated
    keyword's own spelling is never rewritten - only the family is collapsed. When the source carried
    no member of the family, the first alias is used.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Mutates an in-memory builder object; no system state changes and no -WhatIf surface.')]
    param(
        [System.Data.Common.DbConnectionStringBuilder]
        $Builder,

        [string[]]
        $Aliases,

        [string]
        $Value
    )

    $presentKeys = @(Get-ConnectionStringFamilyKey -Builder $Builder -Aliases $Aliases)

    $retainedKey = if ($presentKeys.Count -gt 0) { $presentKeys[0] } else { $Aliases[0] }
    foreach ($key in $presentKeys) {
        if (-not [string]::Equals($key, $retainedKey, [System.StringComparison]::OrdinalIgnoreCase)) {
            $null = $Builder.PSBase.Remove($key)
        }
    }

    Set-ConnectionStringValue -Builder $Builder -Key $retainedKey -Value $Value
}

function Get-DatabaseNameFromConnectionString {
    param(
        [string]
        $ConnectionString,

        [switch]
        $AllowMissing
    )

    # A PRESENCE probe, not an effective-value decision: its one caller
    # (Resolve-CmsInstanceConnectionString) asks only "does this text carry a database name at all?"
    # to tell a readable connection string from CMS ciphertext. Which member of a SQL Server synonym
    # family the provider would select is decided later, by
    # Convert-MssqlCmsConnectionStringToHostSideTarget.
    #
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

function Resolve-TargetDialect {
    param(
        [System.Data.Common.DbConnectionStringBuilder]
        $Builder
    )

    # The data store connection string CMS hands back determines the dialect: SQL Server uses
    # Server / Data Source / Initial Catalog / User Id keys; PostgreSQL uses host / database /
    # username / port / sslmode. SchemaTools provisions both (api-schema-tools ddl provision
    # --dialect pgsql|mssql).
    #
    # host, username, port, and sslmode are checked first and are each a definitive PostgreSQL
    # marker: none of the four is a valid SqlClient connection-string keyword. SqlClient
    # identifies the user via User ID/UID (not Username), encodes the port inside
    # "Server=host,port" rather than a standalone Port key, and controls encryption via
    # Encrypt/TrustServerCertificate rather than SSL Mode. This also fixes a PostgreSQL
    # connection string that uses Server (a legal Npgsql alias for Host) instead of Host: it
    # still carries one of these four definitive keys and resolves to pgsql before the mssql
    # marker loop below ever sees the ambiguous Server key.
    #
    # Residual assumption: Server and User Id are themselves ambiguous (each is also a legal
    # Npgsql alias for Host/Username respectively), so a connection string carrying only
    # ambiguous alias keys and none of the four definitive PostgreSQL keys - e.g. Server= plus
    # User Id= with no host/username/port/sslmode - is genuinely indistinguishable from SQL
    # Server and defaults to mssql via the marker loop below.
    $definitivePostgresqlMarkers = @("host", "username", "port", "sslmode")
    foreach ($marker in $definitivePostgresqlMarkers) {
        if ($Builder.ContainsKey($marker)) {
            return "pgsql"
        }
    }

    $mssqlMarkers = @("server", "data source", "initial catalog", "user id", "trusted_connection")
    foreach ($marker in $mssqlMarkers) {
        if ($Builder.ContainsKey($marker)) {
            return "mssql"
        }
    }

    throw "CMS data store connection string carries none of the PostgreSQL keys (host, username, port, sslmode) and no SQL Server key. Cannot determine the provisioning dialect."
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

function Get-LocalComposeDatabaseHostSideEndpoint {
    <#
    .SYNOPSIS
    The host-side coordinates of the LOCAL Compose database service for a dialect: the exact values
    the two host-side translations below substitute for the Docker-internal host.

    .DESCRIPTION
    Extracted so the translation and anything that later asks "is this target the local Compose
    database?" cannot answer from two different sets of literals. SQL Server encodes host and port
    together, so its endpoint is one "127.0.0.1,<port>" value; PostgreSQL keeps them separate.
    #>
    param(
        [Parameter(Mandatory)]
        [ValidateSet("pgsql", "mssql")]
        [string]
        $Dialect,

        [Parameter(Mandatory)]
        [hashtable]
        $EnvValues
    )

    if ($Dialect -eq "mssql") {
        # mssql.yml publishes this port on 127.0.0.1 only (IPv4); use the literal address so
        # SqlClient cannot resolve "localhost" to ::1 first and stall or fail on an IPv6 host.
        $mssqlPort = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "MSSQL_PORT" -DefaultValue "1435"
        return [pscustomobject]@{
            Host = "127.0.0.1,$mssqlPort"
            Port = $mssqlPort
        }
    }

    return [pscustomobject]@{
        Host = "localhost"
        Port = (Get-EnvValueOrDefault -EnvValues $EnvValues -Name "POSTGRES_PORT" -DefaultValue "5432")
    }
}

function Test-ProvisionTargetIsLocalComposeDatabase {
    <#
    .SYNOPSIS
    True when a provisioning target's already-translated endpoint IS the local Compose database
    service this environment publishes.

    .DESCRIPTION
    Compares the target's effective host/port - the identity the host-side translation already
    produced, and the same fields TargetKey is built from - against the published port from
    Get-LocalComposeDatabaseHostSideEndpoint. A Docker-internal target has already been rewritten to
    the local coordinates by this point, and a target authored directly against those coordinates is
    the same physical server, so both answer true.

    The host is matched as a LOOPBACK EQUIVALENCE CLASS, not against one canonical spelling, and the
    SQL Server host is compared as a parsed part rather than inside its combined "host,port" text.
    Both Compose services publish on 127.0.0.1 (postgresql.yml, mssql.yml) while the PostgreSQL
    translation writes "localhost" and the SQL Server translation writes "127.0.0.1" - so exact text
    equality against either canonical spelling leaves the other spelling of the SAME server looking
    external, and a stored connection string may legitimately carry either. The port must still
    match the configured published port EXACTLY: another server listening on a different port of
    this same host is a different instance.

    An external server (a managed PostgreSQL, a shared SQL Server) keeps its own host and answers
    false. That distinction is load-bearing for the separate-topology guard: this provisioner
    supports per-data-store external servers and already treats identical database names on
    different hosts as different targets, so a database named 'edfi_configurationservice' on some
    other server is not the local Configuration Service database and no local authority - least of
    all the dms-mssql container's collation - can say anything about it.
    #>
    param(
        [Parameter(Mandatory)]
        $Target,

        [Parameter(Mandatory)]
        [hashtable]
        $EnvValues
    )

    $localEndpoint = Get-LocalComposeDatabaseHostSideEndpoint -Dialect $Target.Dialect -EnvValues $EnvValues

    # SQL Server carries host and port in one value ("host,port") and may carry a "tcp:" protocol
    # prefix; Split-MssqlServerEndpoint yields the host part alone from either spelling, so
    # "tcp:localhost,<port>" is recognized as the same server "localhost,<port>" is rather than read as
    # a hostname of its own. The port is already available separately on the target for both dialects.
    $targetHostName =
        if ($Target.Dialect -eq "mssql") { (Split-MssqlServerEndpoint -DataSource $Target.Host).HostName }
        else { ([string]$Target.Host).Trim() }

    $isLoopbackHost = @("localhost", "127.0.0.1") -contains $targetHostName.ToLowerInvariant()

    # The port is compared NUMERICALLY, not as text, and by the rules the PROVIDERS use rather than a
    # stricter set of our own. '05544' and '5544' are the same TCP port, and so is '+5544': Npgsql
    # accepts a leading sign and leading zeros alike (measured - Port=+5544, Port=+05544 and Port= 5544
    # all resolve to 5544), so any spelling this predicate refuses to parse becomes a target classified
    # as EXTERNAL that the provider will nonetheless connect to on the local port - which is how a
    # padded or signed spelling of the local Compose port escaped the separate-topology guard.
    #
    # NumberStyles::Integer is therefore the right set: a leading sign and surrounding whitespace, but no
    # thousands separator and no decimal point, both of which Npgsql rejects outright. The 1-65535 range
    # check still stands, so a negative value - which parses here but which Npgsql refuses to set as a
    # port at all - is never claimed equivalent, and neither is anything else that is not a port. A
    # genuinely different numeric port stays external, as before. Both engines share this predicate, so
    # both are corrected by it.
    [int]$targetPortNumber = 0
    [int]$localPortNumber = 0
    $portsAreEquivalent =
        [int]::TryParse(
            ([string]$Target.Port).Trim(),
            [System.Globalization.NumberStyles]::Integer,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$targetPortNumber) -and
        [int]::TryParse(
            ([string]$localEndpoint.Port).Trim(),
            [System.Globalization.NumberStyles]::Integer,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$localPortNumber) -and
        $targetPortNumber -ge 1 -and $targetPortNumber -le 65535 -and
        $localPortNumber -ge 1 -and $localPortNumber -le 65535 -and
        $targetPortNumber -eq $localPortNumber

    return ($isLoopbackHost -and $portsAreEquivalent)
}

function Convert-MssqlCmsConnectionStringToHostSideTarget {
    <#
    .SYNOPSIS
    Builds an effective host-side SQL Server provisioning target from a CMS-stored data store
    connection string. Translates the Docker-internal server (Server=dms-mssql[,1433]) to the
    host-side 127.0.0.1,MSSQL_PORT while preserving the user, password, database, and every
    other stored option. A non-Docker server (e.g. an external SQL Server configured per
    instance) is preserved as-is.

    .DESCRIPTION
    ONE canonicalization boundary: the server and database every field below is derived from - and
    that the separate-topology guard therefore judges - are the ones SqlClient selects, and the
    HostConnectionString handed to SchemaTools carries exactly those and no synonym able to override
    them. Validating a separately reconstructed name and then passing the original ambiguous string is
    the defect this shape exists to make unrepresentable.

    Both inputs are needed and neither substitutes for the other: -ConnectionString is the only
    faithful input for provider precedence (the builder's own round-trip already collapses a repeated
    key onto its first position, changing which occurrence a reparse would see), while -Builder carries
    the source's keyword spellings and every unrelated stored option through to the rendered result.
    #>
    param(
        [Parameter(Mandatory)]
        [System.Data.Common.DbConnectionStringBuilder]
        $Builder,

        [Parameter(Mandatory)]
        [string]
        $ConnectionString,

        [Parameter(Mandatory)]
        [hashtable]
        $EnvValues,

        # The resolved api-schema-tools path, so an ambiguous synonym family is settled by the
        # Microsoft.Data.SqlClient build that very tool ships. Resolved before this runs; resolution
        # executes no DDL.
        [Parameter(Mandatory)]
        [string]
        $SchemaToolPath
    )

    # Full SqlClient synonym families, so collapsing one leaves no other member of it behind:
    # Data Source also answers to Server / Addr / Address / Network Address, and Initial Catalog to
    # Database. Same alias sets database-safety.psm1 recognizes for this engine, and pinned against the
    # provider's own synonym table by the SchemaTools unit tests.
    $serverAliases = @("server", "data source", "addr", "address", "network address")
    $databaseAliases = @("database", "initial catalog")
    # User ID also answers to UID and to plain User - the complete family measured in the provider the
    # tool ships, and pinned by the SchemaTools unit tests. Omitting 'user' both mis-resolved the
    # principal (a lone User= read as no user at all) and left the family uncollapsed.
    $userAliases = @("user id", "uid", "user")

    $databaseName = Get-MssqlEffectiveConnectionStringValue `
        -Builder $Builder `
        -Aliases $databaseAliases `
        -ProviderPropertyName "InitialCatalog" `
        -ConnectionString $ConnectionString `
        -SchemaToolPath $SchemaToolPath
    if ([string]::IsNullOrWhiteSpace($databaseName)) {
        throw "CMS data store connection string did not contain a database name."
    }

    $server = Get-MssqlEffectiveConnectionStringValue `
        -Builder $Builder `
        -Aliases $serverAliases `
        -ProviderPropertyName "DataSource" `
        -ConnectionString $ConnectionString `
        -SchemaToolPath $SchemaToolPath
    if ([string]::IsNullOrWhiteSpace($server)) {
        throw "CMS data store connection string is missing the server key."
    }

    # Collapsed like the other two families below, not merely resolved: leaving a losing user-id alias in
    # the emitted string let SchemaTools reparse a DIFFERENT principal than Username and TargetKey name.
    $instanceUser = Get-MssqlEffectiveConnectionStringValue `
        -Builder $Builder `
        -Aliases $userAliases `
        -ProviderPropertyName "UserID" `
        -ConnectionString $ConnectionString `
        -SchemaToolPath $SchemaToolPath
    if ([string]::IsNullOrWhiteSpace($instanceUser)) {
        throw "CMS data store connection string is missing the user id key."
    }

    # The prefix-stripped host decides the Docker-internal translation, so "tcp:dms-mssql,1433"
    # translates exactly as "dms-mssql,1433" does instead of surviving as an untranslatable hostname.
    $endpoint = Split-MssqlServerEndpoint -DataSource $server

    $effectiveServer = $server
    if ($endpoint.HostName.Equals("dms-mssql", [System.StringComparison]::OrdinalIgnoreCase)) {
        $effectiveServer = (Get-LocalComposeDatabaseHostSideEndpoint -Dialect "mssql" -EnvValues $EnvValues).Host
    }

    # Write back only the three synonym families, each collapsed onto the single key the source used for
    # it. Every other stored option (password, TrustServerCertificate, Encrypt, a quoted password
    # containing ';' or '=', etc.) is carried through verbatim and correctly re-quoted by the builder.
    # The server value keeps any "tcp:" prefix it came with - that prefix selects the protocol SqlClient
    # connects over, so it is normalized for endpoint decisions but never stripped from the string
    # SchemaTools receives.
    Set-ConnectionStringFamilyValue -Builder $Builder -Aliases $serverAliases -Value $effectiveServer
    Set-ConnectionStringFamilyValue -Builder $Builder -Aliases $databaseAliases -Value $databaseName
    Set-ConnectionStringFamilyValue -Builder $Builder -Aliases $userAliases -Value $instanceUser

    $hostConnectionString = $Builder.get_ConnectionString()

    # The port is encoded inside the server value for SQL Server; surface it for the summary.
    $effectiveEndpoint = Split-MssqlServerEndpoint -DataSource $effectiveServer
    $effectivePort =
        if (-not [string]::IsNullOrWhiteSpace($effectiveEndpoint.Port)) { $effectiveEndpoint.Port }
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
        $EnvValues,

        [Parameter(Mandatory)]
        [string]
        $SchemaToolPath
    )

    $builder = ConvertTo-ConnectionStringBuilder -ConnectionString $ConnectionString
    $dialect = Resolve-TargetDialect -Builder $builder

    if ($dialect -eq "mssql") {
        # The ORIGINAL text travels with the builder: provider precedence is decided from occurrence
        # order, which the builder no longer represents once it has parsed.
        return Convert-MssqlCmsConnectionStringToHostSideTarget `
            -Builder $builder `
            -ConnectionString $ConnectionString `
            -EnvValues $EnvValues `
            -SchemaToolPath $SchemaToolPath
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
        $localEndpoint = Get-LocalComposeDatabaseHostSideEndpoint -Dialect "pgsql" -EnvValues $EnvValues
        $effectiveHost = $localEndpoint.Host
        $effectivePort = $localEndpoint.Port
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
        $EnvValues,

        [Parameter(Mandatory)]
        [string]
        $SchemaToolPath
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
    $target = Convert-CmsConnectionStringToHostSideTarget `
        -ConnectionString $resolvedConnectionString `
        -EnvValues $EnvValues `
        -SchemaToolPath $SchemaToolPath

    # configure-local-data-store.ps1 -NoDataStore can select a pre-existing route-unqualified
    # CMS data store without checking its connection-string dialect, so a stale data store from
    # a previous run with the other database engine can otherwise be provisioned silently while
    # DMS itself starts against DMS_DATASTORE from this same environment. Catch the mismatch
    # here, where the connection string has already been decrypted (if needed) and its dialect
    # resolved.
    $expectedProvisioningDialect = Resolve-ExpectedProvisioningDialect -EnvValues $EnvValues
    if ($target.Dialect -ne $expectedProvisioningDialect.ExpectedDialect) {
        $dataStoreName = [string](Get-ProvisionProperty -Object $Instance -Names @("name", "Name"))
        throw "CMS data store $(Format-LogSafeText $dataStoreId) (name=$(Format-LogSafeText $dataStoreName), database=$(Format-LogSafeText $target.DatabaseName)) resolved to dialect '$(Format-LogSafeText $target.Dialect)', but the environment's DMS_DATASTORE is '$(Format-LogSafeText $expectedProvisioningDialect.EngineValue)' (expected dialect '$(Format-LogSafeText $expectedProvisioningDialect.ExpectedDialect)'). This usually means a stale data store from a previous run with the other database engine is being reused (commonly selected via -NoDataStore). Reset the local CMS state with start-local-dms.ps1 -d -v and rerun bootstrap-local-dms.ps1 -DatabaseEngine <engine>, or, when invoking the phase commands directly, make sure the effective environment file's DMS_DATASTORE matches the data store's engine (composed automatically by -DatabaseEngine)."
    }

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

function Assert-SeparateTopologyProvisionTarget {
    <#
    .SYNOPSIS
    Separate-topology guard at the provisioning boundary: refuses a target database that is the
    dedicated Configuration Service database, before SchemaTools is invoked against it.

    .DESCRIPTION
    The configure phase can only judge a name it is about to REGISTER. With -NoDataStore it
    registers none - it selects an existing CMS data store - so the value that decides where DMS
    schema lands is the STORED connection string on that record, which only this phase resolves.
    This is that missing boundary, and it is the reason the switch reaches this phase at all: a
    reused data store pointing at edfi_configurationservice would otherwise receive DMS DDL in
    separate mode.

    Judged here rather than earlier because $Target is the first place the real database name
    exists: New-ProvisionTarget has already resolved ${...} placeholders, decrypted a CMS-encrypted
    connection string, detected the dialect, and extracted the database. Parsing the raw
    connectionString CMS hands back would be false assurance - it is base64 ciphertext.

    No new comparer, query, parser, or collation rule: each engine's established authority answers,
    on the value that engine's provider will actually receive.

      - PostgreSQL: Test-RegisteredDatastoreNameCollidesWithReservedCmsDatabase, the same offline
        predicate the registration path uses. Sound here for the same reason: SchemaTools creates
        the database with a QUOTED identifier, so nothing folds and only a name that parses back AS
        the reserved name collides.
      - SQL Server: Assert-MssqlTopologyPhysicalConsistency, which asks the RUNNING instance under
        its own collation, because a name's physical identity there depends on the instance
        collation and no offline comparer can decide it. The candidate is labelled with the fixed
        CMS_DATA_STORE_CONNECTION_STRING source key so the diagnostic names where it came from.

    Diagnostics name the fixed source key, the data store id, and the reserved literal only - never
    the stored connection string, its credentials, the decrypted text, or the resolved database.
    #>
    param(
        [Parameter(Mandatory)]
        $Target,

        [Parameter(Mandatory)]
        [hashtable]
        $EnvValues,

        [Parameter(Mandatory)]
        [string]
        $EnvironmentFile
    )

    # Scoped to the local Compose database service, which is the only server the dedicated
    # 'edfi_configurationservice' database lives on. A data store pointed at an external server -
    # a shape this phase supports, and one whose targets are already kept distinct from local ones
    # by TargetKey - has its own namespace: a database of that name there is a different physical
    # database, and asking the local instance about it would answer for the wrong server entirely.
    if (-not (Test-ProvisionTargetIsLocalComposeDatabase -Target $Target -EnvValues $EnvValues)) {
        return
    }

    if ($Target.Dialect -eq "mssql") {
        $mssqlPassword = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "MSSQL_SA_PASSWORD" -DefaultValue "abcdefgh1!"
        Assert-MssqlTopologyPhysicalConsistency `
            -EnvironmentFile $EnvironmentFile `
            -ContainerName "dms-mssql" `
            -SaPassword $mssqlPassword `
            -RegisteredDatastoreDatabaseName $Target.DatabaseName `
            -RegisteredDatastoreDatabaseSourceKey "CMS_DATA_STORE_CONNECTION_STRING"
        return
    }

    if (Test-RegisteredDatastoreNameCollidesWithReservedCmsDatabase `
            -DatabaseEngine "postgresql" `
            -DatastoreDatabaseName $Target.DatabaseName) {
        throw "CMS data store $(Format-LogSafeText $Target.DataStoreId) resolves to the dedicated 'edfi_configurationservice' database, which -SeparateConfigDatabase reserves for the Configuration Service. Provisioning would deploy the DMS schema into it and reintroduce the shared topology the switch opts out of. The target database name came from 'CMS_DATA_STORE_CONNECTION_STRING' - the connection string stored on that data store record - so re-register or reset the data store rather than changing this phase's inputs. The resolved value is withheld."
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
        $lines.Add("  ConfigurationServiceSettings__EncryptionKey = (set to DMS_CONFIG_DATABASE_ENCRYPTION_KEY from your .env file; .env.example default secret!_32_chars_xxxxxxxxxxxxxxx)")
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
        $DatabaseEngine = "postgresql",

        [Switch]
        $SeparateConfigDatabase
    )

    if ($DataStoreId.Count -gt 0 -and $SchoolYear.Count -gt 0) {
        throw "-DataStoreId and -SchoolYear are mutually exclusive. Pass only one selector."
    }

    $resolvedEnvironmentFile = Resolve-ProvisionEnvironmentFile -Path $EnvironmentFile
    # Compose the MSSQL engine overlay for -DatabaseEngine mssql; mirrors
    # configure-local-data-store.ps1 and start-local-dms.ps1. Direct invocation with
    # -DatabaseEngine mssql layers the overlay onto a custom -EnvironmentFile; wrapper-forwarded
    # files are already composed and no-op via the DMS_DATASTORE guard in
    # Resolve-DatabaseEngineEnvironmentFile.
    $resolvedEnvironmentFile = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine $DatabaseEngine -BaseEnvironmentFile $resolvedEnvironmentFile -DockerComposeRoot $PSScriptRoot
    if ($SeparateConfigDatabase) {
        # Same second composition step configure-local-data-store.ps1 runs, in the same order and
        # after the same engine overlay: the separate-topology continuation is invoked with the
        # operator's ORIGINAL -EnvironmentFile, while the topology marker the MSSQL authority reads
        # lives in the derived file the start phase wrote. Recomputing it idempotently from the
        # switch reproduces that same deterministic .derived/<name>.topology artifact, so the guard
        # below asks the live authority about the environment the running stack actually received.
        # Shared mode never calls this, so its effective file - and every value read from it -
        # is unchanged.
        $resolvedEnvironmentFile = Resolve-CmsDatabaseTopologyEnvironmentFile `
            -BaseEnvironmentFile $resolvedEnvironmentFile `
            -DatabaseEngine $DatabaseEngine `
            -SeparateConfigDatabase `
            -DockerComposeRoot $PSScriptRoot
    }
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

    # Resolved BEFORE the targets are built, because building an MSSQL target may need the
    # Microsoft.Data.SqlClient build this very distribution ships in order to settle a repeated
    # connection-string synonym family the way the tool itself would. Resolution locates an executable
    # and reads no database and deploys no DDL; every topology check below still completes before
    # Invoke-DmsSchemaProvision is ever called.
    $schemaTool = Resolve-DmsSchemaTool -RequestedPath $env:DMS_SCHEMA_TOOL_PATH
    Write-Information "api-schema-tools resolved: $(Format-LogSafeText $schemaTool)." -InformationAction Continue

    $targets = @($selectedInstances | ForEach-Object {
        New-ProvisionTarget -Instance $_ -EnvValues $envValues -SchemaToolPath $schemaTool
    })

    # Separate-topology guard for EVERY selected target, whether this run's configure phase
    # registered its name or reused an existing data store's stored connection string. It runs
    # after the targets are built - each target's DatabaseName is the resolved, decrypted,
    # dialect-aware database SchemaTools will receive - and after the tool is resolved but before it is
    # invoked, so a refusal deploys no DDL anywhere, including into targets that would have been
    # provisioned first.
    if ($SeparateConfigDatabase) {
        foreach ($candidateTarget in $targets) {
            Assert-SeparateTopologyProvisionTarget `
                -Target $candidateTarget `
                -EnvValues $envValues `
                -EnvironmentFile $resolvedEnvironmentFile
        }
    }

    # Group by the effective provisioning target identity (dialect + translated host + port +
    # database + user). Grouping only by database name would silently collapse two instances
    # that share a database name on different physical hosts or under different users.
    $groups = $targets | Group-Object -Property TargetKey

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
    -DatabaseEngine $DatabaseEngine `
    -SeparateConfigDatabase:$SeparateConfigDatabase
