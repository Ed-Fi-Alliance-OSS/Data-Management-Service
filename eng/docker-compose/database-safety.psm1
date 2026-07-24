# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Shared database-name safety guards for E2E provisioning.
.DESCRIPTION
    The repository's safe-name and dedicated-E2E database rules, extracted so both
    provision-e2e-database.ps1 and the Instance Management E2E orchestration validate route-context
    database names with the same logic before any DROP/CREATE mutation. Assert-SafeDatabaseName rejects
    unsupported characters and reserved PostgreSQL/SQL Server system databases; Assert-E2EDatabaseIsDedicated
    additionally rejects names that collide with the primary/CMS databases (by name or by the database
    embedded in the admin/CMS connection strings).
#>

Set-StrictMode -Version Latest

function ConvertFrom-ComposeEnvironmentValue {
    <#
    .SYNOPSIS
        Returns the effective value of a Docker Compose env-file entry, stripping surrounding quotes
        and inline comments the way Docker Compose does.
    #>
    param(
        [AllowEmptyString()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $Value
    }

    $trimmedValue = $Value.Trim()
    $firstCharacter = $trimmedValue[0]

    if ($firstCharacter -in @("'", '"')) {
        $closingQuoteIndex = -1
        $escaped = $false

        for ($index = 1; $index -lt $trimmedValue.Length; $index++) {
            $character = $trimmedValue[$index]

            if ($character -eq "\" -and -not $escaped) {
                $escaped = $true
                continue
            }

            if ($character -eq $firstCharacter -and -not $escaped) {
                $closingQuoteIndex = $index
                break
            }

            $escaped = $false
        }

        if ($closingQuoteIndex -gt 0) {
            $trailingContent = $trimmedValue.Substring($closingQuoteIndex + 1).Trim()
            if ([string]::IsNullOrEmpty($trailingContent) -or $trailingContent.StartsWith("#")) {
                $unquotedValue = $trimmedValue.Substring(1, $closingQuoteIndex - 1)
                if ($firstCharacter -eq "'") {
                    return $unquotedValue.Replace("\'", "'")
                }

                return $unquotedValue.Replace('\"', '"').Replace('\\', '\')
            }
        }
    }

    # Docker Compose treats a # preceded by whitespace as an inline comment for an unquoted
    # value. A # without leading whitespace remains part of the value.
    return ($trimmedValue -replace '[ \t]+#.*$', '').Trim()
}

function Resolve-ComposeEnvRawValue {
    <#
    .SYNOPSIS
        Applies Docker Compose value semantics to a single raw env-file map value: strips surrounding
        quotes and inline comments (ConvertFrom-ComposeEnvironmentValue), then resolves ${VAR}/$VAR
        references EXCEPT when the raw value is single-quoted, which Compose treats as literal (no
        interpolation). Single place that decides convert-then-resolve vs. literal, so the rule applies
        identically to the top-level requested key and to every value reached through a ${VAR} chain.
    #>
    param(
        [hashtable]$EnvironmentValues,
        [AllowEmptyString()][string]$RawValue,
        [int]$Depth = 0
    )

    $converted = ConvertFrom-ComposeEnvironmentValue -Value $RawValue

    if ($RawValue.TrimStart().StartsWith("'")) {
        # Single-quoted: Compose keeps the value literal (quotes stripped), no ${VAR} interpolation.
        return $converted
    }

    return Resolve-ComposeEnvReference -EnvironmentValues $EnvironmentValues -Value $converted -Depth $Depth
}

function Resolve-ComposeEnvReference {
    <#
    .SYNOPSIS
        Resolves ${VAR}/$VAR references in a Compose-converted value against the other environment
        values, following Docker Compose interpolation: a literal '$' is written '$$' and preserved,
        ${NAME}/$NAME expand (recursively, bounded), an unset reference expands to empty, and a value
        set in the process/shell environment wins over the env file. A referenced value is resolved
        through Resolve-ComposeEnvRawValue so its own quoting (single-quote literal) semantics hold.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'EnvironmentValues', Justification = 'Consumed inside the [regex]::Replace callback scriptblock to resolve ${VAR} references; the analyzer does not detect uses within the nested scriptblock.')]
    param(
        [hashtable]$EnvironmentValues,
        [AllowEmptyString()][string]$Value,
        [int]$Depth = 0
    )

    if ([string]::IsNullOrEmpty($Value) -or $Value.IndexOf('$') -lt 0 -or $Depth -ge 8) {
        return $Value
    }

    # Protect Compose's literal-'$' escape ('$$') before resolving any reference, then restore it.
    $placeholder = [char]0x1
    $working = $Value.Replace('$$', $placeholder)

    $working = [regex]::Replace($working, '\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)', {
        param($match)
        $referenceName = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
        # Docker Compose resolves ${VAR} from the shell/process environment with precedence over the
        # env file, then from the env file, then to empty. Honour that so a reference whose value lives
        # only in - or is overridden by - the ambient environment matches what the container receives.
        $ambient = [System.Environment]::GetEnvironmentVariable($referenceName)
        if ($null -ne $ambient) { return $ambient }
        if ($null -ne $EnvironmentValues -and $EnvironmentValues.ContainsKey($referenceName)) {
            return Resolve-ComposeEnvRawValue -EnvironmentValues $EnvironmentValues -RawValue ([string]$EnvironmentValues[$referenceName]) -Depth ($Depth + 1)
        }
        return ""
    })

    return $working.Replace($placeholder, '$')
}

function Get-ComposeResolvedEnvValue {
    <#
    .SYNOPSIS
        Reads an env-file value the way Docker Compose does: a value set for the same key in the
        process/shell environment wins (interpolation precedence); otherwise strips surrounding quotes
        and inline comments and resolves ${VAR}/$VAR references (single-quoted values stay literal).
        Falls back to the documented default when the key is absent, blank, or resolves to empty. This
        is the single Compose-equivalent resolver shared by the connection-string factory, the
        destructive-safety guard, and the E2E startup/provision phases. Never logs the value.
    #>
    param(
        [hashtable]$EnvironmentValues,
        [Parameter(Mandatory)][string]$Name,
        [string]$DefaultValue = ""
    )

    # Docker Compose interpolation gives a value set in the process/shell environment precedence over
    # the same key in the env file. Honour that for the requested key itself: when $Name is set in the
    # ambient environment, the container receives that value verbatim (an interpolation result is not
    # itself re-interpolated), so it wins over the file value. Otherwise use the file value through the
    # shared convert + quote-aware resolver, or the documented default when the key is absent.
    $ambient = [System.Environment]::GetEnvironmentVariable($Name)
    $resolved =
        if ($null -ne $ambient) {
            $ambient
        }
        elseif ($null -eq $EnvironmentValues -or -not $EnvironmentValues.ContainsKey($Name)) {
            $DefaultValue
        }
        else {
            Resolve-ComposeEnvRawValue -EnvironmentValues $EnvironmentValues -RawValue ([string]$EnvironmentValues[$Name])
        }

    if ([string]::IsNullOrWhiteSpace($resolved)) {
        return $DefaultValue
    }

    return $resolved
}

function Get-RequiredComposeResolvedEnvValue {
    <#
    .SYNOPSIS
        Resolves a required env value with Docker Compose precedence (ambient wins, references followed,
        single quotes literal) and throws when it is absent in both the process environment and the env
        file. Used by the E2E startup/provision phases so a required credential/port is read exactly as
        the running stack sees it, and never logs the value.
    #>
    param(
        [hashtable]$EnvironmentValues,
        [Parameter(Mandatory)][string]$Name
    )

    $value = Get-ComposeResolvedEnvValue -EnvironmentValues $EnvironmentValues -Name $Name
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Required environment value '$Name' is not set (checked the process environment and the env file)."
    }

    return $value
}

function Assert-SafeDatabaseName {
    <#
    .SYNOPSIS
        Throws when a database name contains unsupported characters or names a reserved
        PostgreSQL/SQL Server system database, so it can never target shared state.
    #>
    param([string]$DatabaseName)

    if ($DatabaseName -notmatch "^[A-Za-z0-9_]+$") {
        throw "Database name '$DatabaseName' contains unsupported characters."
    }

    if ($DatabaseName -iin @("postgres", "template0", "template1")) {
        throw "Database name '$DatabaseName' is a reserved PostgreSQL system database and cannot be used for E2E provisioning."
    }

    if ($DatabaseName -iin @("master", "model", "msdb", "tempdb")) {
        throw "Database name '$DatabaseName' is a reserved SQL Server system database and cannot be used for E2E provisioning."
    }
}

function Test-ProtectedKeyConfigured {
    <#
    .SYNOPSIS
        Returns true when a protected key is configured for the running stack - present in the env
        file (even with a blank value) or present in the process/shell environment (Docker Compose
        would consume an ambient value even when the file omits it). An explicitly blank env-file
        value still counts as configured: Compose's ${VAR:-default} substitutes the default for a
        blank value, so the running container can be on the compose-file default database while the
        configured value resolves to nothing - the dedicated-E2E guard must then fail closed rather
        than skip the collision check. Only a genuinely absent key is skippable.
    #>
    param(
        [hashtable]$EnvironmentValues,
        [Parameter(Mandatory)][string]$Name
    )

    if ($null -ne [System.Environment]::GetEnvironmentVariable($Name)) {
        return $true
    }

    return $null -ne $EnvironmentValues -and $EnvironmentValues.ContainsKey($Name)
}

function Get-DatabaseNameFromConnectionString {
    <#
    .SYNOPSIS
        Parses the database / initial-catalog name out of an ADO.NET connection string,
        resolving any env-file indirection, so the dedicated-E2E guard can compare it.
    #>
    param(
        [string]$ConnectionString,
        [hashtable]$EnvironmentValues
    )

    $ConnectionString = ConvertFrom-ComposeEnvironmentValue -Value $ConnectionString

    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        return $null
    }

    try {
        $connectionStringBuilder = [System.Data.Common.DbConnectionStringBuilder]::new()
        # DbConnectionStringBuilder implements IDictionary, so PowerShell's adapted view treats
        # `.ConnectionString = ...` as an item named "ConnectionString". PSBase selects the real
        # CLR property and exposes the parsed keys/items.
        $connectionStringBuilder.PSBase.ConnectionString = $ConnectionString

        foreach ($key in $connectionStringBuilder.PSBase.Keys) {
            if ([string]$key -imatch '^(database|initial\s+catalog)$') {
                return Resolve-ComposeEnvRawValue `
                    -EnvironmentValues $EnvironmentValues `
                    -RawValue ([string]$connectionStringBuilder.PSBase.get_Item($key))
            }
        }
    }
    catch {
        throw "Could not safely parse a protected database connection string: $($_.Exception.Message)"
    }

    return $null
}

function Assert-E2EDatabaseIsDedicated {
    <#
    .SYNOPSIS
        Throws unless an E2E route-context database name is safe and dedicated: it must not
        match the primary/CMS database names or the database embedded in the admin/CMS
        connection strings, so E2E provisioning can never drop shared state.
    #>
    param(
        [hashtable]$EnvironmentValues,
        [string]$EnvironmentFilePath,
        [string]$E2EDatabaseName
    )

    Assert-SafeDatabaseName -DatabaseName $E2EDatabaseName

    # Resolve every protected value the way Docker Compose does before comparing it to the E2E reset
    # target: a value set in the process/shell environment wins over the env file, ${VAR} references
    # (including ambient overrides) are followed, and single-quoted values stay literal. Evaluating the
    # raw file value instead would let an ambient MSSQL_DB_NAME / POSTGRES_DB_NAME / admin/CMS
    # connection-string override make the live shared database equal the reset target while this guard
    # sees a different file value and permits a destructive reset/drop.
    #
    # All comparisons are case-insensitive: SQL Server's default collation treats database identifiers
    # case-insensitively, so a case-variant of a protected name IS the same database there and would
    # still be dropped. PostgreSQL names are case-sensitive, so this is stricter than required on that
    # engine - acceptable for a guard in front of DROP DATABASE, where a false positive costs a rename
    # and a false negative drops shared state. The guard fails closed: a protected key that is
    # configured (in the file or the ambient environment) but cannot be resolved throws rather than
    # silently skipping the collision check.
    foreach ($databaseNameKey in @("POSTGRES_DB_NAME", "MSSQL_DB_NAME")) {
        if (-not (Test-ProtectedKeyConfigured -EnvironmentValues $EnvironmentValues -Name $databaseNameKey)) {
            continue
        }

        # A protected database name that is empty (explicitly blank, or an undefined reference) or
        # still contains a '$' (an unresolved or cyclic reference the resolver could not expand)
        # cannot be proven distinct from the reset target, so fail closed. A blank value is not
        # skippable because Compose's ${VAR:-default} would give the running container the compose
        # default database. A real database name never contains '$'.
        $protectedDatabaseName = Get-ComposeResolvedEnvValue -EnvironmentValues $EnvironmentValues -Name $databaseNameKey
        if ([string]::IsNullOrWhiteSpace($protectedDatabaseName) -or $protectedDatabaseName -match '\$') {
            throw "E2E database safety check could not resolve $databaseNameKey in '$EnvironmentFilePath' (blank, or an unresolved or cyclic reference); refusing a destructive reset that cannot be proven dedicated."
        }

        if ($E2EDatabaseName -ieq $protectedDatabaseName) {
            throw "E2E database '$E2EDatabaseName' in '$EnvironmentFilePath' must be dedicated and cannot match $databaseNameKey."
        }
    }

    foreach ($connectionStringKey in @(
            "DATABASE_CONNECTION_STRING_ADMIN",
            "DMS_CONFIG_DATABASE_CONNECTION_STRING"
        )) {
        if (-not (Test-ProtectedKeyConfigured -EnvironmentValues $EnvironmentValues -Name $connectionStringKey)) {
            continue
        }

        $connectionString = Get-ComposeResolvedEnvValue -EnvironmentValues $EnvironmentValues -Name $connectionStringKey
        if ([string]::IsNullOrWhiteSpace($connectionString)) {
            throw "E2E database safety check could not resolve $connectionStringKey in '$EnvironmentFilePath'; refusing a destructive reset that cannot be proven dedicated."
        }

        $connectionStringDatabaseName = Get-DatabaseNameFromConnectionString `
            -ConnectionString $connectionString `
            -EnvironmentValues $EnvironmentValues

        if ([string]::IsNullOrWhiteSpace($connectionStringDatabaseName)) {
            throw "E2E database safety check could not determine a database name from $connectionStringKey in '$EnvironmentFilePath'."
        }

        # A parsed database name that still contains a '$' came from an unresolved or cyclic reference
        # the resolver could not expand; fail closed rather than compare an indeterminate value.
        if ($connectionStringDatabaseName -match '\$') {
            throw "E2E database safety check could not fully resolve the database name from $connectionStringKey in '$EnvironmentFilePath' (unresolved or cyclic reference); refusing a destructive reset that cannot be proven dedicated."
        }

        if ($E2EDatabaseName -ieq $connectionStringDatabaseName) {
            throw "E2E database '$E2EDatabaseName' in '$EnvironmentFilePath' must stay separate from $connectionStringKey."
        }
    }
}

Export-ModuleMember -Function `
    ConvertFrom-ComposeEnvironmentValue, `
    Resolve-ComposeEnvReference, `
    Resolve-ComposeEnvRawValue, `
    Get-ComposeResolvedEnvValue, `
    Get-RequiredComposeResolvedEnvValue, `
    Assert-SafeDatabaseName, `
    Get-DatabaseNameFromConnectionString, `
    Assert-E2EDatabaseIsDedicated
