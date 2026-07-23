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
    selected CMS instances. The dialect (--dialect pgsql|mssql) is the effective
    DMS_DATASTORE provider (postgresql -> pgsql, mssql -> mssql; an unsupported value
    fails at the engine-token boundary), NOT inferred from the data store connection
    string. The CMS-stored connection is then parsed ONLY by the exact runtime provider
    (SchemaTools `connection inspect`), so provider-valid aliases (e.g. Server=, User Id=, DB=)
    are accepted exactly as the runtime accepts them; only a database is required. A connection
    the selected provider rejects is a stale data store ONLY when the OTHER provider accepts it,
    otherwise it is simply invalid for the selected provider. The Docker-internal database host is
    translated to the host-side mapped port before SchemaTools runs
    (dms-postgresql -> localhost:POSTGRES_PORT, dms-mssql -> 127.0.0.1,MSSQL_PORT).

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

function Test-CmsEncryptedConnectionStringShape {
    <#
    .SYNOPSIS
    Pure STRUCTURAL test for a CMS-encrypted connection string. Returns $true only when the resolved value is
    a standard base64 payload - the base64 alphabet with optional '=' padding and nothing else - that decodes
    to a possible AES-CBC payload: at least 32 bytes (a 16-byte IV plus at least one 16-byte block) and a whole
    number of 16-byte blocks. It interprets NO connection-string vocabulary, so a provider-valid
    plaintext connection using ANY alias (e.g. DB=, Server=, User Id=) is never misread as ciphertext, and a
    real connection string - which always carries a ';' or a mid-string '=' - can never match. This is the
    plaintext-vs-ciphertext gate BEFORE the exact-provider `connection inspect`; the generic
    DbConnectionStringBuilder is deliberately NOT used, so it cannot become a second parsing authority.
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [AllowNull()]
        [string]
        $Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $false
    }
    # A connection string carries ';', spaces, or a mid-string '='; a base64 blob is only the base64 alphabet
    # with up to two trailing '=' padding characters.
    if ($Value -notmatch '^[A-Za-z0-9+/]+={0,2}$') {
        return $false
    }
    try {
        $decodedBytes = [Convert]::FromBase64String($Value)
    }
    catch {
        return $false
    }

    # The CMS payload is a 16-byte AES IV followed by AES-CBC ciphertext, which is a positive whole number of
    # 16-byte blocks; a genuine blob is therefore at least 32 bytes and a multiple of 16. This rejects short or
    # oddly-sized base64 tokens (e.g. a 24-byte value) that cannot be an AES-CBC payload.
    return ($decodedBytes.Length -ge 32 -and ($decodedBytes.Length % 16) -eq 0)
}

function Resolve-ExpectedProvisioningDialect {
    <#
    .SYNOPSIS
    Resolves the SchemaTools dialect the effective environment expects for provisioning targets,
    from the effective DMS_DATASTORE value: mssql -> mssql, postgresql -> pgsql. A missing or blank
    value defaults to postgresql -> pgsql, matching local-dms.yml's compose-level default
    (AppSettings__Datastore: ${DMS_DATASTORE:-postgresql}). The value is canonicalized through the
    single engine-token boundary (ConvertTo-CanonicalDatabaseEngine): case variants ('MSSQL') resolve,
    and an unsupported token (a 'mysql' typo) THROWS here rather than silently mapping to PostgreSQL.
    #>
    param(
        [hashtable]
        $EnvValues
    )

    # Apply the blank/missing PostgreSQL default first, then canonicalize: only 'postgresql' / 'mssql'
    # (case-insensitive) survive; anything else fails at the explicit engine authority, never as PostgreSQL.
    $engineValue = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "DMS_DATASTORE" -DefaultValue "postgresql"
    if ([string]::IsNullOrWhiteSpace($engineValue)) {
        $engineValue = "postgresql"
    }
    $canonicalEngine = ConvertTo-CanonicalDatabaseEngine -Engine $engineValue
    $expectedDialect = if ($canonicalEngine -eq "mssql") { "mssql" } else { "pgsql" }

    return [pscustomobject]@{
        EngineValue = $canonicalEngine
        ExpectedDialect = $expectedDialect
    }
}

function Test-ProvisionTargetEquivalent {
    <#
    .SYNOPSIS
    Provider-aware equivalence for two effective provisioning targets, using the actual comparers (never a
    derived lowercase string key, which does not implement OrdinalIgnoreCase). Two targets are equivalent -
    and share one SchemaTools invocation - only when their engine (ordinal), translated host
    (OrdinalIgnoreCase), effective port (normalized integer), database (the engine's own comparer via
    Get-DatabaseNameComparer: PostgreSQL case-sensitive, SQL Server case-insensitive), and username/auth
    identity (ordinal, blank tolerated) all match.
    #>
    param(
        [Parameter(Mandatory)] $Left,
        [Parameter(Mandatory)] $Right
    )

    if (-not [System.StringComparer]::Ordinal.Equals([string]$Left.Engine, [string]$Right.Engine)) {
        return $false
    }
    if (-not [System.StringComparer]::OrdinalIgnoreCase.Equals([string]$Left.Host, [string]$Right.Host)) {
        return $false
    }

    # Compare the port as a normalized integer so "5432" and "05432" are one target; if either side is not an
    # integer, fall back to an exact ordinal comparison rather than silently coercing.
    $leftPortValue = 0
    $rightPortValue = 0
    $leftPortIsInt = [int]::TryParse([string]$Left.Port, [ref]$leftPortValue)
    $rightPortIsInt = [int]::TryParse([string]$Right.Port, [ref]$rightPortValue)
    if ($leftPortIsInt -and $rightPortIsInt) {
        if ($leftPortValue -ne $rightPortValue) { return $false }
    }
    elseif (-not [System.StringComparer]::Ordinal.Equals([string]$Left.Port, [string]$Right.Port)) {
        return $false
    }

    $databaseComparer = Get-DatabaseNameComparer -Engine ([string]$Left.Engine)
    if (-not $databaseComparer.Equals([string]$Left.DatabaseName, [string]$Right.DatabaseName)) {
        return $false
    }
    if (-not [System.StringComparer]::Ordinal.Equals([string]$Left.Username, [string]$Right.Username)) {
        return $false
    }
    return $true
}

function Group-ProvisionTarget {
    <#
    .SYNOPSIS
    Partitions effective provisioning targets into equivalence groups using Test-ProvisionTargetEquivalent
    (the comparers themselves), so two instances pointing at the same physical database under the engine's
    identity semantics share one SchemaTools invocation. Returns a list of { Representative; Targets } - the
    representative is the first target of each group; Targets carries every member (for the collected data
    store ids). No string key and no Group-Object, so an OrdinalIgnoreCase relation that a lowercase key would
    mis-collapse (e.g. the Kelvin sign) is grouped correctly, and no delimiter can collapse distinct targets.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure function: partitions in-memory records; no system state changes and no -WhatIf surface.')]
    param(
        [object[]]
        $Targets = @()
    )

    $groups = [System.Collections.Generic.List[object]]::new()
    foreach ($target in $Targets) {
        $matchedGroup = $null
        foreach ($group in $groups) {
            if (Test-ProvisionTargetEquivalent -Left $group.Representative -Right $target) {
                $matchedGroup = $group
                break
            }
        }
        if ($null -ne $matchedGroup) {
            [void]$matchedGroup.Targets.Add($target)
        }
        else {
            $members = [System.Collections.Generic.List[object]]::new()
            [void]$members.Add($target)
            $groups.Add([pscustomobject]@{ Representative = $target; Targets = $members })
        }
    }

    return , $groups.ToArray()
}

function Convert-CmsConnectionStringToHostSideTarget {
    <#
    .SYNOPSIS
    Builds an effective host-side provisioning target from a single CMS-stored data store connection string.
    The connection string is parsed ONLY by the exact runtime provider (SchemaTools `connection inspect`),
    never a generic PowerShell builder, so a provider-valid alias (e.g. the Npgsql aliases Server= for Host
    and User Id= for Username) is accepted exactly as the Configuration Service accepts it at runtime.

    The provisioning engine is the effective DMS_DATASTORE (canonicalized), never inferred from the string.
    Classification of a string the selected provider rejects:
      * valid under the OTHER engine  -> a genuine stale data store from a previous run with the other engine;
      * invalid under both engines     -> simply invalid for the selected provider (no stale claim);
      * valid but no database          -> provider-valid but incomplete (no provisioning target).
    PowerShell interprets the canonical endpoint ONLY to recognize the Docker-internal service and choose the
    host-side override coordinates; it never rebuilds the connection string. The ORIGINAL connection string is
    returned unchanged for `ddl provision`; when a Docker-internal endpoint is recognized, OverrideHost/
    OverridePort direct SchemaTools (the exact provider) to rewrite the endpoint. External endpoints receive
    neither override and are passed through verbatim.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $ConnectionString,

        [Parameter(Mandatory)]
        [hashtable]
        $EnvValues,

        [Parameter(Mandatory)]
        [object]
        $SchemaToolPath
    )

    $engine = (Resolve-ExpectedProvisioningDialect -EnvValues $EnvValues).EngineValue
    $dialect = if ($engine -eq "mssql") { "mssql" } else { "pgsql" }

    $inspection = Invoke-ConnectionStringInspection -Engine $engine -ConnectionString $ConnectionString -SchemaToolPath $SchemaToolPath

    if (-not $inspection.valid) {
        # The selected provider rejected the string. Cross-engine staleness is established ONLY by whether the
        # OTHER provider accepts it - never asserted otherwise.
        $otherEngine = if ($engine -eq "mssql") { "postgresql" } else { "mssql" }
        # A tool-contract/version failure on the other-engine probe is NOT a datastore signal, so it is allowed
        # to PROPAGATE (surfacing as rebuild guidance with data-store context) rather than being coerced into a
        # stale/invalid classification. Only a cleanly returned valid=$true/$false drives the decision below.
        $otherValid = (Invoke-ConnectionStringInspection -Engine $otherEngine -ConnectionString $ConnectionString -SchemaToolPath $SchemaToolPath).valid

        if ($otherValid) {
            throw "the stored connection string is a valid '$otherEngine' connection but not a valid '$engine' connection ($($inspection.error)). This is a stale data store from a previous run with the other database engine. Reset the local CMS state with start-local-dms.ps1 -d -v and rerun bootstrap-local-dms.ps1 -DatabaseEngine <engine>, or make sure the effective environment file's DMS_DATASTORE matches the data store's engine (composed automatically by -DatabaseEngine)."
        }
        throw "the stored connection string is not a valid '$engine' connection: $($inspection.error)."
    }

    $databaseName = [string]$inspection.database
    if ([string]::IsNullOrWhiteSpace($databaseName)) {
        throw "the stored connection string is a valid '$engine' connection but specifies no database (Database/Initial Catalog), so there is no provisioning target."
    }

    $canonicalHost = [string]$inspection.host
    # Username may legitimately be blank (e.g. SQL Server integrated authentication); it is not required.
    $canonicalUser = [string]$inspection.username

    # Docker-internal -> host translation is the ONLY endpoint interpretation PowerShell performs. When a
    # Docker-internal service on its container-internal DEFAULT port is recognized, SchemaTools (the exact
    # provider) rewrites the endpoint at `ddl provision` time via the overrides; otherwise the ORIGINAL
    # connection string is passed through unchanged. The effective host/port feed the summary and the
    # equivalence grouping.
    $overrideHost = $null
    $overridePort = $null
    if ($engine -eq "mssql") {
        # SQL Server data source: an optional case-insensitive "tcp:" protocol prefix, then host[,port] (a
        # named instance is "host\instance"). Recognize the Docker-internal server ONLY on its default
        # container port 1433 (explicit or omitted); any OTHER port targets a different endpoint than DMS uses
        # and must not be silently redirected.
        $dataSource = $canonicalHost -replace '^\s*tcp:', ''
        $serverParts = $dataSource -split ",", 2
        $serverHost = $serverParts[0].Trim()
        $serverPort = if ($serverParts.Count -eq 2) { $serverParts[1].Trim() } else { "1433" }
        # Compare the port NUMERICALLY so a zero-padded default (e.g. "01433") is still the container port 1433;
        # any other numeric port targets a different endpoint and gets no override.
        $serverPortValue = 0
        $serverPortIsContainerDefault = [int]::TryParse($serverPort, [ref]$serverPortValue) -and $serverPortValue -eq 1433
        if ($serverHost.Equals("dms-mssql", [System.StringComparison]::OrdinalIgnoreCase) -and $serverPortIsContainerDefault) {
            $overrideHost = "127.0.0.1"
            # mssql.yml publishes this port on 127.0.0.1 (IPv4) only; the literal address avoids an IPv6
            # "localhost" resolution stall.
            $overridePort = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "MSSQL_PORT" -DefaultValue "1435"
        }
        if ($null -ne $overrideHost) {
            $effectiveHost = $overrideHost
            $effectivePort = [string]$overridePort
        }
        else {
            # External SQL Server: the COMPLETE provider-returned data source IS the endpoint identity - it
            # encodes host, an optional named instance, and an optional port. Do NOT split off a synthetic 1433,
            # which would collapse a named instance (resolved via SQL Browser to a dynamic port) with an
            # explicit ",1433". The port stays inside the data source, so the separate effective port is null.
            $effectiveHost = $canonicalHost
            $effectivePort = $null
        }
    }
    else {
        # Npgsql exposes Host and Port (Port defaults to 5432 when the string omits it). Recognize the
        # Docker-internal PostgreSQL endpoint dms-postgresql only on the container-internal 5432.
        $serverPort = if ($null -ne $inspection.port) { [string]$inspection.port } else { "5432" }
        # Compare the port NUMERICALLY, symmetric with the SQL Server branch.
        $serverPortValue = 0
        $serverPortIsContainerDefault = [int]::TryParse($serverPort, [ref]$serverPortValue) -and $serverPortValue -eq 5432
        if ($canonicalHost.Equals("dms-postgresql", [System.StringComparison]::OrdinalIgnoreCase) -and $serverPortIsContainerDefault) {
            $overrideHost = "localhost"
            $overridePort = Get-EnvValueOrDefault -EnvValues $EnvValues -Name "POSTGRES_PORT" -DefaultValue "5432"
        }
        if ($null -ne $overrideHost) {
            $effectiveHost = $overrideHost
            $effectivePort = [string]$overridePort
        }
        else {
            $effectiveHost = $canonicalHost
            $effectivePort = $serverPort
        }
    }

    return [pscustomobject]@{
        Engine = $engine
        Dialect = $dialect
        Host = $effectiveHost
        Port = $effectivePort
        Username = $canonicalUser
        DatabaseName = $databaseName
        ConnectionString = $ConnectionString
        OverrideHost = $overrideHost
        OverridePort = $overridePort
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
    # Detect CMS ciphertext by SHAPE only (a base64 payload), never by parsing connection-string vocabulary,
    # so a provider-valid plaintext string using an alias (DB=, Server=, User Id=) is passed straight to the
    # exact-provider path and only a genuine encrypted blob is routed to decryption.
    if (-not (Test-CmsEncryptedConnectionStringShape -Value $resolvedConnectionString)) {
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
        [object]
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

    # The provisioning target is built by parsing the stored connection with the exact runtime provider
    # (Convert-CmsConnectionStringToHostSideTarget -> `connection inspect`), never a generic keyword builder,
    # so a provider-valid alias is accepted exactly as the runtime accepts it. Any classification failure it
    # raises - a stale wrong-engine data store, invalid-for-the-selected-provider, incomplete (no database),
    # or a tool-contract/version failure - is surfaced here with the data-store id/name context, preserving
    # the specific message rather than relabeling everything as stale.
    try {
        $target = Convert-CmsConnectionStringToHostSideTarget `
            -ConnectionString $resolvedConnectionString `
            -EnvValues $EnvValues `
            -SchemaToolPath $SchemaToolPath
    }
    catch {
        $dataStoreName = [string](Get-ProvisionProperty -Object $Instance -Names @("name", "Name"))
        throw "CMS data store $(Format-LogSafeText $dataStoreId) (name=$(Format-LogSafeText $dataStoreName)): $($_.Exception.Message)"
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
        Engine = $target.Engine
        Dialect = $target.Dialect
        Host = $target.Host
        Port = $target.Port
        Username = $target.Username
        DatabaseName = $target.DatabaseName
        ConnectionString = $target.ConnectionString
        OverrideHost = $target.OverrideHost
        OverridePort = $target.OverridePort
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
        $Dialect = "pgsql",

        [AllowNull()]
        [AllowEmptyString()]
        [string]
        $OverrideHost,

        [AllowNull()]
        [AllowEmptyString()]
        [string]
        $OverridePort
    )

    # The connection string is passed to SchemaTools verbatim (never rewritten in PowerShell). When the target
    # is the Docker-internal service, the host-side endpoint override is applied by the exact provider inside
    # `ddl provision` via --override-host / --override-port.
    $arguments = @("ddl", "provision")
    foreach ($schemaPath in $SchemaPaths) {
        $arguments += @("--schema", $schemaPath)
    }
    $arguments += @(
        "--connection-string", $ConnectionString,
        "--dialect", $Dialect,
        "--create-database"
    )
    if (-not [string]::IsNullOrWhiteSpace($OverrideHost)) {
        $arguments += @("--override-host", $OverrideHost, "--override-port", $OverridePort)
    }

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

    # Resolve ONE host SchemaTools executable up front and use it for every target inspection AND every
    # `ddl provision` call below. An explicit-but-missing DMS_SCHEMA_TOOL_PATH fails here, with no fallback.
    $schemaTool = Resolve-DmsSchemaTool -RequestedPath $env:DMS_SCHEMA_TOOL_PATH
    Write-Information "api-schema-tools resolved: $(Format-LogSafeText $schemaTool)." -InformationAction Continue

    # Build ALL provisioning targets (parsing each stored connection with the exact provider via that tool)
    # BEFORE any DDL executes.
    $targets = @($selectedInstances | ForEach-Object { New-ProvisionTarget -Instance $_ -EnvValues $envValues -SchemaToolPath $schemaTool })
    # Group by the effective provisioning target identity (engine + translated endpoint + database + auth).
    # Partition by the engine's actual identity comparers (Group-ProvisionTarget), never a derived lowercase
    # string key: a PostgreSQL case-distinct database (SchoolDb vs schooldb) stays separate while SQL Server
    # case variants group, matching Get-DatabaseNameComparer exactly.
    $groups = Group-ProvisionTarget -Targets $targets

    $provisionedTargets = [System.Collections.ArrayList]::new()

    foreach ($group in $groups) {
        $target = $group.Representative
        $dataStoreIds = @($group.Targets | ForEach-Object { [long]$_.DataStoreId })
        $ids = ($dataStoreIds | ForEach-Object { [string]$_ }) -join ", "
        Write-Information "Provisioning target database $(Format-LogSafeText $target.DatabaseName) on $(Format-LogSafeText $target.Host):$(Format-LogSafeText $target.Port) for data store id(s): $(Format-LogSafeText $ids)." -InformationAction Continue
        Invoke-DmsSchemaProvision `
            -ToolPath $schemaTool `
            -SchemaPaths $schemaPaths `
            -ConnectionString $target.ConnectionString `
            -DatabaseName $target.DatabaseName `
            -Dialect $target.Dialect `
            -OverrideHost $target.OverrideHost `
            -OverridePort $target.OverridePort

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
