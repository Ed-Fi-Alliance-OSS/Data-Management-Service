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

function Resolve-EnvironmentValueReference {
    <#
    .SYNOPSIS
        Resolves a single ${OTHER_KEY} env-file indirection against the supplied value map,
        following chains and throwing on unresolved, blank, or cyclic references.
    #>
    param(
        [string]$Value,
        [hashtable]$EnvironmentValues,
        [System.Collections.Generic.HashSet[string]]$VisitedKeys
    )

    $Value = ConvertFrom-ComposeEnvironmentValue -Value $Value

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $Value
    }

    $match = [Regex]::Match($Value, '^\$\{(?<key>[^}]+)\}$')

    if (-not $match.Success) {
        return $Value
    }

    $referencedKey = $match.Groups["key"].Value
    if (-not $EnvironmentValues.ContainsKey($referencedKey)) {
        throw "Environment value reference '$Value' could not be resolved because '$referencedKey' is not defined."
    }

    $resolvedValue = [string]$EnvironmentValues[$referencedKey]
    if ([string]::IsNullOrWhiteSpace($resolvedValue)) {
        throw "Environment value reference '$Value' could not be resolved because '$referencedKey' is blank."
    }

    if ($null -eq $VisitedKeys) {
        $VisitedKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    }
    if (-not $VisitedKeys.Add($referencedKey)) {
        throw "Environment value reference '$Value' is cyclic at '$referencedKey'."
    }

    try {
        return Resolve-EnvironmentValueReference `
            -Value $resolvedValue `
            -EnvironmentValues $EnvironmentValues `
            -VisitedKeys $VisitedKeys
    }
    finally {
        $null = $VisitedKeys.Remove($referencedKey)
    }
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
                return Resolve-EnvironmentValueReference `
                    -Value ([string]$connectionStringBuilder.PSBase.get_Item($key)) `
                    -EnvironmentValues $EnvironmentValues
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

    # All comparisons are case-insensitive: SQL Server's default collation treats database
    # identifiers case-insensitively, so a case-variant of a protected name IS the same database
    # there and would still be dropped. PostgreSQL names are case-sensitive, so this is stricter
    # than required on that engine - acceptable for a guard in front of DROP DATABASE, where a
    # false positive costs a rename and a false negative drops shared state.
    foreach ($databaseNameKey in @("POSTGRES_DB_NAME", "MSSQL_DB_NAME")) {
        $protectedDatabaseName = Resolve-EnvironmentValueReference `
            -Value ([string]$EnvironmentValues[$databaseNameKey]) `
            -EnvironmentValues $EnvironmentValues

        if (-not [string]::IsNullOrWhiteSpace($protectedDatabaseName) -and $E2EDatabaseName -ieq $protectedDatabaseName) {
            throw "E2E database '$E2EDatabaseName' in '$EnvironmentFilePath' must be dedicated and cannot match $databaseNameKey."
        }
    }

    foreach ($connectionStringKey in @(
            "DATABASE_CONNECTION_STRING_ADMIN",
            "DMS_CONFIG_DATABASE_CONNECTION_STRING"
        )) {
        $connectionString = Resolve-EnvironmentValueReference `
            -Value ([string]$EnvironmentValues[$connectionStringKey]) `
            -EnvironmentValues $EnvironmentValues

        if ([string]::IsNullOrWhiteSpace($connectionString)) {
            continue
        }

        $connectionStringDatabaseName = Get-DatabaseNameFromConnectionString `
            -ConnectionString $connectionString `
            -EnvironmentValues $EnvironmentValues

        if ([string]::IsNullOrWhiteSpace($connectionStringDatabaseName)) {
            throw "E2E database safety check could not determine a database name from $connectionStringKey in '$EnvironmentFilePath'."
        }

        if ($E2EDatabaseName -ieq $connectionStringDatabaseName) {
            throw "E2E database '$E2EDatabaseName' in '$EnvironmentFilePath' must stay separate from $connectionStringKey."
        }
    }
}

Export-ModuleMember -Function `
    ConvertFrom-ComposeEnvironmentValue, `
    Assert-SafeDatabaseName, `
    Resolve-EnvironmentValueReference, `
    Get-DatabaseNameFromConnectionString, `
    Assert-E2EDatabaseIsDedicated
