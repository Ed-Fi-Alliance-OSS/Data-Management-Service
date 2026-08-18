# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Pure, engine-aware primitives shared by the database-template restore producer
    (Template-Management.psm1) and consumer (bootstrap-restore.psm1): reserved-name policy,
    generated database names, the canonical inventory model, the external restore-manifest
    contract, MSSQL backup file-list handling, and dms.DataStoreIdentity.SourceIdentity SQL.

.DESCRIPTION
    This module deliberately performs no docker, database, filesystem-mutation, or network
    work, and imports no sibling modules, so every function is unit-testable in isolation
    and safe to import from any working directory.
#>

$script:ReservedDatabaseNames = @{
    postgresql = @("postgres", "template0", "template1")
    mssql      = @("master", "model", "msdb", "tempdb")
}

$script:RestoreScratchDatabaseNamePrefix = "edfi_dms_restore_scratch"
$script:RestorePreflightDatabaseNamePrefix = "edfi_dms_restore_preflight"

$script:SupportedRestoreManifestVersion = 1
$script:RestoreManifestFileName = "restore-manifest.json"
$script:RestoreContentProfileDmsDatastoreOnly = "DmsDatastoreOnly"

function Get-RestoreManifestFileName {
    <#
    .SYNOPSIS
    The fixed file name of the external restore manifest packaged beside the database artifact.
    #>
    return $script:RestoreManifestFileName
}

function Test-ReservedDatabaseName {
    <#
    .SYNOPSIS
    Returns $true when the supplied database name is one of the engine's reserved system
    database names. Comparison is case-insensitive over the trimmed value, so a differently
    cased or whitespace-padded spelling of a reserved name is still reserved.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$DatabaseName
    )

    $normalized = $DatabaseName.Trim()
    foreach ($reserved in $script:ReservedDatabaseNames[$DatabaseEngine]) {
        if ($reserved.Equals($normalized, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Assert-SafeRestoreDatabaseName {
    <#
    .SYNOPSIS
    Fails unless a database name is non-empty, restricted to the safe identifier charset the
    template tooling already enforces ([A-Za-z0-9_]), and not a reserved system database name
    for the selected engine. Generic identifier validation alone is not a substitute for the
    reserved-name denylist, so both checks always run.

    .PARAMETER Purpose
    Short human label for the name's role (e.g. "restore target", "scratch"), used only in
    error text so a refusal names which database selection was rejected.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$DatabaseName,

        [string]$Purpose = "database"
    )

    if ([string]::IsNullOrWhiteSpace($DatabaseName)) {
        throw "The $Purpose database name must not be empty."
    }

    # Anchored with \z, not $: the .NET $ anchor tolerates a single trailing newline, which
    # would let a "name`n" spelling through a security-relevant identifier check.
    if ($DatabaseName -notmatch "^[A-Za-z0-9_]+\z") {
        throw "The $Purpose database name contains unsupported characters. Only letters, digits, and underscores are allowed."
    }

    if (Test-ReservedDatabaseName -DatabaseEngine $DatabaseEngine -DatabaseName $DatabaseName) {
        $reservedList = $script:ReservedDatabaseNames[$DatabaseEngine] -join ", "
        throw "The $Purpose database name '$DatabaseName' is a reserved $DatabaseEngine system database name ($reservedList) and can never be created, dropped, or restored by template restore."
    }
}

function New-RestoreGeneratedDatabaseName {
    <#
    .SYNOPSIS
    Builds a generated, non-user-selectable database name: a safe product prefix plus an
    unpredictable 12-hex-character suffix from a cryptographic RNG. The result passes the
    same charset and reserved-name checks as user-selected names on both engines and stays
    within PostgreSQL's 63-byte identifier limit.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Returns a generated name string; no system state is created or changed.')]
    param (
        [Parameter(Mandatory = $true)]
        [string]$Prefix
    )

    # -cnotmatch: the default case-insensitive operator would accept an uppercase prefix
    # through the [a-z] class; \z rejects a trailing newline the $ anchor would tolerate.
    if ($Prefix -cnotmatch "^[a-z][a-z0-9_]*\z") {
        throw "Generated database name prefix '$Prefix' must start with a lowercase letter and contain only lowercase letters, digits, and underscores."
    }

    $suffixBytes = [byte[]]::new(6)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($suffixBytes)
    $suffix = [System.Convert]::ToHexString($suffixBytes).ToLowerInvariant()

    $generatedName = "${Prefix}_$suffix"
    if ($generatedName.Length -gt 63) {
        throw "Generated database name '$generatedName' exceeds the 63-character PostgreSQL identifier limit; use a shorter prefix."
    }

    foreach ($engine in @("postgresql", "mssql")) {
        Assert-SafeRestoreDatabaseName -DatabaseEngine $engine -DatabaseName $generatedName -Purpose "generated"
    }

    return $generatedName
}

function New-RestoreScratchDatabaseName {
    <#
    .SYNOPSIS
    Generated scratch-database name used to validate a restore artifact before any
    selected-target replacement.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Returns a generated name string; no system state is created or changed.')]
    param ()

    return New-RestoreGeneratedDatabaseName -Prefix $script:RestoreScratchDatabaseNamePrefix
}

function New-RestorePreflightDatabaseName {
    <#
    .SYNOPSIS
    Generated preflight-database name substituted for POSTGRES_DB_NAME during database-only
    restore preflight, so fresh-volume PostgreSQL initialization cannot create the selected
    target database.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Returns a generated name string; no system state is created or changed.')]
    param ()

    return New-RestoreGeneratedDatabaseName -Prefix $script:RestorePreflightDatabaseNamePrefix
}

function Get-RestoreSchemaNameExclusion {
    <#
    .SYNOPSIS
    The engine's excluded schema names and prefixes for one of the two distinct schema
    enumeration purposes. The two purposes deliberately differ on PostgreSQL "public" and
    SQL Server "dbo":

    DumpDiscovery mirrors the existing template dump discovery (Get-UserSchemaNames): it
    excludes PostgreSQL "public" and SQL Server "dbo", so package contents are scoped to the
    discovered user schemas only.

    InventoryEnumeration is for the DMS-only content gates and therefore INCLUDES PostgreSQL
    "public" and SQL Server "dbo": those schemas always exist, so hiding contamination inside
    them must be visible to the gate. The gate permits only allowlisted extension bootstrap
    objects there (e.g. pgcrypto installed by the template's own CREATE EXTENSION line);
    everything else in them is contamination.

    .OUTPUTS
    PSCustomObject with ExcludedSchemaName (exact names) and ExcludedSchemaNamePrefix
    (name prefixes, e.g. "pg_" / "db_") for the engine and purpose.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [ValidateSet("DumpDiscovery", "InventoryEnumeration")]
        [string]$Purpose
    )

    if ($DatabaseEngine -eq "mssql") {
        $names =
            if ($Purpose -eq "DumpDiscovery") {
                @("dbo", "guest", "sys", "INFORMATION_SCHEMA")
            }
            else {
                @("guest", "sys", "INFORMATION_SCHEMA")
            }

        return [pscustomobject]@{
            ExcludedSchemaName       = [string[]]$names
            ExcludedSchemaNamePrefix = [string[]]@("db_")
        }
    }

    $names =
        if ($Purpose -eq "DumpDiscovery") {
            @("information_schema", "public")
        }
        else {
            @("information_schema")
        }

    return [pscustomobject]@{
        ExcludedSchemaName       = [string[]]$names
        ExcludedSchemaNamePrefix = [string[]]@("pg_")
    }
}

function Get-RestoreDocumentJsonBaselineType {
    <#
    .SYNOPSIS
    The current authoritative physical storage type of the DocumentJson columns per engine:
    PostgreSQL "jsonb", SQL Server "nvarchar" (native json deferred, so nvarchar(max) is the
    MSSQL baseline). Restore manifests and scratch databases are both validated against this
    single definition; a future storage migration changes only this function.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine
    )

    if ($DatabaseEngine -eq "mssql") {
        return "nvarchar"
    }

    return "jsonb"
}

function Get-RestorePropertyValue {
    <#
    .SYNOPSIS
    StrictMode-safe property read supporting both IDictionary input (producer-built
    hashtables) and PSCustomObject input (ConvertFrom-Json output). Returns $null when the
    property is absent; use Test-RestorePropertyPresent to distinguish absent from JSON null.
    #>
    param (
        [Parameter(Mandatory = $true)]
        $InputObject,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($InputObject -is [System.Collections.IDictionary]) {
        if ($InputObject.Contains($Name)) {
            return $InputObject[$Name]
        }
        return $null
    }

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -ne $property) {
        return $property.Value
    }

    return $null
}

function Test-RestorePropertyPresent {
    <#
    .SYNOPSIS
    StrictMode-safe property presence test for IDictionary and PSCustomObject input.
    #>
    param (
        [Parameter(Mandatory = $true)]
        $InputObject,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($InputObject -is [System.Collections.IDictionary]) {
        return $InputObject.Contains($Name)
    }

    return $null -ne $InputObject.PSObject.Properties[$Name]
}

function Test-RestoreJsonArray {
    <#
    .SYNOPSIS
    Returns $true only when a parsed JSON value is a real array (IList). A scalar or a
    singleton JSON object must never satisfy a contract field that requires an array; the
    permissive @(...) wrapping idiom would silently accept both.
    #>
    param (
        $Value
    )

    return ($Value -is [System.Collections.IList] -and $Value -isnot [string])
}

function Get-RestoreWrappedPropertyValue {
    <#
    .SYNOPSIS
    StrictMode-safe property read that distinguishes an absent property from a present one
    and preserves empty arrays. PowerShell unrolls an empty array at a function return
    boundary, so the value travels wrapped in a result object whose property read does not
    unroll.

    .OUTPUTS
    PSCustomObject { Present = [bool]; Value = <the property value or $null> }.
    #>
    param (
        [Parameter(Mandatory = $true)]
        $InputObject,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if (-not (Test-RestorePropertyPresent -InputObject $InputObject -Name $Name)) {
        return [pscustomobject]@{ Present = $false; Value = $null }
    }

    $value = $null
    if ($InputObject -is [System.Collections.IDictionary]) {
        $value = $InputObject[$Name]
    }
    else {
        $value = $InputObject.PSObject.Properties[$Name].Value
    }

    return [pscustomobject]@{ Present = $true; Value = $value }
}

function ConvertTo-CanonicalJsonStringLiteral {
    <#
    .SYNOPSIS
    Serializes one string as a JSON string literal with fully deterministic escaping:
    backslash, double quote, and every character outside printable ASCII are emitted as
    \u escapes, so the byte output never depends on the PowerShell or .NET JSON
    implementation version. Canonical inventory hashes are computed over this output.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    $builder = [System.Text.StringBuilder]::new()
    $null = $builder.Append('"')
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '"') {
            $null = $builder.Append('\"')
        }
        elseif ($character -eq '\') {
            $null = $builder.Append('\\')
        }
        elseif ([int]$character -lt 0x20 -or [int]$character -gt 0x7E) {
            $null = $builder.AppendFormat([System.Globalization.CultureInfo]::InvariantCulture, '\u{0:x4}', [int]$character)
        }
        else {
            $null = $builder.Append($character)
        }
    }
    $null = $builder.Append('"')

    return $builder.ToString()
}

function ConvertTo-CanonicalInventory {
    <#
    .SYNOPSIS
    Normalizes a raw inventory (schemas with typed objects, optional principals) into the
    canonical, fully sorted model: schemas ordered by name (ordinal), objects within each
    schema ordered by type then name (ordinal), principals ordered (ordinal), object types
    lowercased. Duplicate schema names, duplicate (type, name) pairs within a schema, and
    duplicate principals are data-integrity failures and throw.
    #>
    param (
        [Parameter(Mandatory = $true)]
        $Inventory
    )

    $schemasWrapped = Get-RestoreWrappedPropertyValue -InputObject $Inventory -Name "schemas"
    if (-not $schemasWrapped.Present -or $null -eq $schemasWrapped.Value) {
        throw "Inventory is missing the required 'schemas' array."
    }
    if (-not (Test-RestoreJsonArray -Value $schemasWrapped.Value)) {
        throw "Inventory 'schemas' must be a JSON array, not a single object or scalar."
    }

    $schemaEntries = [System.Collections.Generic.List[object]]::new()
    $seenSchemaNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

    foreach ($rawSchema in $schemasWrapped.Value) {
        if ($null -eq $rawSchema) {
            throw "Inventory contains a null schema entry."
        }
        $schemaName = [string](Get-RestorePropertyValue -InputObject $rawSchema -Name "schemaName")
        if ([string]::IsNullOrWhiteSpace($schemaName)) {
            throw "Inventory contains a schema entry without a non-empty 'schemaName'."
        }
        if (-not $seenSchemaNames.Add($schemaName)) {
            throw "Inventory contains duplicate schema entry '$schemaName'."
        }

        $objectsWrapped = Get-RestoreWrappedPropertyValue -InputObject $rawSchema -Name "objects"
        if (-not $objectsWrapped.Present -or $null -eq $objectsWrapped.Value) {
            throw "Inventory schema '$schemaName' is missing its 'objects' array (an empty schema declares an empty array)."
        }
        if (-not (Test-RestoreJsonArray -Value $objectsWrapped.Value)) {
            throw "Inventory schema '$schemaName' 'objects' must be a JSON array, not a single object or scalar."
        }

        $objectEntries = [System.Collections.Generic.List[object]]::new()
        $seenObjectKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        foreach ($rawObject in $objectsWrapped.Value) {
            if ($null -eq $rawObject) {
                throw "Inventory schema '$schemaName' contains a null object entry."
            }
            $objectName = [string](Get-RestorePropertyValue -InputObject $rawObject -Name "name")
            $objectType = [string](Get-RestorePropertyValue -InputObject $rawObject -Name "type")
            if ([string]::IsNullOrWhiteSpace($objectName) -or [string]::IsNullOrWhiteSpace($objectType)) {
                throw "Inventory schema '$schemaName' contains an object entry without both a non-empty 'name' and 'type'."
            }

            $normalizedType = $objectType.ToLowerInvariant()
            # U+0000 cannot appear inside a name or type, so it is a collision-free composite key separator.
            $objectKey = "$normalizedType`0$objectName"
            if (-not $seenObjectKeys.Add($objectKey)) {
                throw "Inventory schema '$schemaName' contains duplicate object entry (type '$normalizedType', name '$objectName')."
            }

            $objectEntries.Add([pscustomobject]@{ Name = $objectName; Type = $normalizedType })
        }

        $objectEntries.Sort([System.Comparison[object]] {
                param ($left, $right)
                $typeOrder = [string]::CompareOrdinal($left.Type, $right.Type)
                if ($typeOrder -ne 0) { return $typeOrder }
                return [string]::CompareOrdinal($left.Name, $right.Name)
            })

        $schemaEntries.Add([pscustomobject]@{ SchemaName = $schemaName; Objects = $objectEntries })
    }

    $schemaEntries.Sort([System.Comparison[object]] {
            param ($left, $right)
            return [string]::CompareOrdinal($left.SchemaName, $right.SchemaName)
        })

    $principalEntries = [System.Collections.Generic.List[string]]::new()
    $seenPrincipals = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    # An absent or JSON-null principals property means "no principals"; a present value must
    # be a real JSON array.
    $principalsWrapped = Get-RestoreWrappedPropertyValue -InputObject $Inventory -Name "principals"
    $rawPrincipals = @()
    if ($principalsWrapped.Present -and $null -ne $principalsWrapped.Value) {
        if (-not (Test-RestoreJsonArray -Value $principalsWrapped.Value)) {
            throw "Inventory 'principals' must be a JSON array when present, not a single value."
        }
        $rawPrincipals = $principalsWrapped.Value
    }
    foreach ($rawPrincipal in $rawPrincipals) {
        if ($null -eq $rawPrincipal) {
            throw "Inventory contains a null principal entry."
        }
        $principalName = [string]$rawPrincipal
        if ([string]::IsNullOrWhiteSpace($principalName)) {
            throw "Inventory contains an empty principal entry."
        }
        if (-not $seenPrincipals.Add($principalName)) {
            throw "Inventory contains duplicate principal entry '$principalName'."
        }
        $principalEntries.Add($principalName)
    }
    $principalEntries.Sort([System.StringComparer]::Ordinal)

    return [pscustomobject]@{
        Schemas    = $schemaEntries
        Principals = $principalEntries
    }
}

function ConvertTo-CanonicalInventoryJson {
    <#
    .SYNOPSIS
    Serializes an inventory into its canonical JSON form: fixed key order, fully sorted
    entries (ordinal), deterministic character escaping, single-line, LF-free. Producer and
    consumer both hash this exact serialization, so it must never vary across platform,
    culture, or PowerShell version.
    #>
    param (
        [Parameter(Mandatory = $true)]
        $Inventory
    )

    $canonical = ConvertTo-CanonicalInventory -Inventory $Inventory

    $builder = [System.Text.StringBuilder]::new()
    $null = $builder.Append('{"schemas":[')
    $firstSchema = $true
    foreach ($schemaEntry in $canonical.Schemas) {
        if (-not $firstSchema) { $null = $builder.Append(',') }
        $firstSchema = $false
        $null = $builder.Append('{"schemaName":')
        $null = $builder.Append((ConvertTo-CanonicalJsonStringLiteral -Value $schemaEntry.SchemaName))
        $null = $builder.Append(',"objects":[')
        $firstObject = $true
        foreach ($objectEntry in $schemaEntry.Objects) {
            if (-not $firstObject) { $null = $builder.Append(',') }
            $firstObject = $false
            $null = $builder.Append('{"name":')
            $null = $builder.Append((ConvertTo-CanonicalJsonStringLiteral -Value $objectEntry.Name))
            $null = $builder.Append(',"type":')
            $null = $builder.Append((ConvertTo-CanonicalJsonStringLiteral -Value $objectEntry.Type))
            $null = $builder.Append('}')
        }
        $null = $builder.Append(']}')
    }
    $null = $builder.Append('],"principals":[')
    $firstPrincipal = $true
    foreach ($principalEntry in $canonical.Principals) {
        if (-not $firstPrincipal) { $null = $builder.Append(',') }
        $firstPrincipal = $false
        $null = $builder.Append((ConvertTo-CanonicalJsonStringLiteral -Value $principalEntry))
    }
    $null = $builder.Append(']}')

    return $builder.ToString()
}

function Get-CanonicalInventoryHash {
    <#
    .SYNOPSIS
    Lowercase-hex SHA-256 of the canonical inventory JSON (UTF-8 bytes). This is the value
    recorded as the restore manifest's inventorySha256 and independently recomputed by the
    consumer from the scratch database.
    #>
    param (
        [Parameter(Mandatory = $true)]
        $Inventory
    )

    $canonicalJson = ConvertTo-CanonicalInventoryJson -Inventory $Inventory
    $jsonBytes = [System.Text.Encoding]::UTF8.GetBytes($canonicalJson)
    $hashBytes = [System.Security.Cryptography.SHA256]::HashData($jsonBytes)

    return [System.Convert]::ToHexString($hashBytes).ToLowerInvariant()
}

function Assert-RestoreManifestField {
    <#
    .SYNOPSIS
    Shared required-field guard: present, not JSON null, and (for strings) non-empty.
    #>
    param (
        [Parameter(Mandatory = $true)]
        $Manifest,

        [Parameter(Mandatory = $true)]
        [string]$FieldName,

        [switch]$AsNonEmptyString
    )

    if (-not (Test-RestorePropertyPresent -InputObject $Manifest -Name $FieldName)) {
        throw "Restore manifest is missing required field '$FieldName'."
    }

    $value = Get-RestorePropertyValue -InputObject $Manifest -Name $FieldName
    if ($null -eq $value) {
        throw "Restore manifest is missing required field '$FieldName'."
    }

    if ($AsNonEmptyString) {
        if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value)) {
            throw "Restore manifest field '$FieldName' must be a non-empty JSON string."
        }
    }

    return $value
}

function Test-RestoreManifestInteger {
    <#
    .SYNOPSIS
    Returns $true when a parsed JSON value is a real integer (not a string, float, or bool).
    #>
    param (
        $Value
    )

    return ($Value -is [int]) -or ($Value -is [long]) -or ($Value -is [short]) -or ($Value -is [byte])
}

function Assert-RestoreManifestShape {
    <#
    .SYNOPSIS
    Validates an already-parsed restore manifest against the version-1 contract: every
    required field present with the required JSON type and value shape, hashes in lowercase
    64-hex form, the fixed DmsDatastoreOnly content profile, engine-consistent artifact
    extension and compatibility-level rules, and an internally consistent inventory whose
    recomputed canonical hash equals the recorded inventorySha256. Throws a field-specific
    message on the first violation; a package whose manifest fails here is never eligible
    for extraction, Docker startup, or any database work.
    #>
    param (
        [Parameter(Mandatory = $true)]
        $Manifest
    )

    $manifestVersion = Assert-RestoreManifestField -Manifest $Manifest -FieldName "version"
    if (-not (Test-RestoreManifestInteger -Value $manifestVersion)) {
        throw "Restore manifest field 'version' must be an integer manifest format version."
    }
    if ($manifestVersion -ne $script:SupportedRestoreManifestVersion) {
        throw "Restore manifest field 'version' is '$manifestVersion' but only version $($script:SupportedRestoreManifestVersion) is supported."
    }

    $null = Assert-RestoreManifestField -Manifest $Manifest -FieldName "packageId" -AsNonEmptyString
    $null = Assert-RestoreManifestField -Manifest $Manifest -FieldName "packageVersion" -AsNonEmptyString

    $databaseEngine = Assert-RestoreManifestField -Manifest $Manifest -FieldName "databaseEngine" -AsNonEmptyString
    if ($databaseEngine -cnotin @("postgresql", "mssql")) {
        throw "Restore manifest field 'databaseEngine' must be exactly 'postgresql' or 'mssql', but found '$databaseEngine'."
    }

    $templateKind = Assert-RestoreManifestField -Manifest $Manifest -FieldName "templateKind" -AsNonEmptyString
    if ($templateKind -cnotin @("Minimal", "Populated")) {
        throw "Restore manifest field 'templateKind' must be exactly 'Minimal' or 'Populated', but found '$templateKind'."
    }

    $null = Assert-RestoreManifestField -Manifest $Manifest -FieldName "dataStandardVersion" -AsNonEmptyString

    $contentProfile = Assert-RestoreManifestField -Manifest $Manifest -FieldName "contentProfile" -AsNonEmptyString
    if ($contentProfile -cne $script:RestoreContentProfileDmsDatastoreOnly) {
        throw "Restore manifest field 'contentProfile' must be exactly '$($script:RestoreContentProfileDmsDatastoreOnly)', but found '$contentProfile'. Only DMS-datastore-only template packages are eligible for restore."
    }

    # The projects value is read through the wrapped helper (not a plain function return):
    # PowerShell unrolls an empty array at a function return boundary, which would make an
    # empty projects array indistinguishable from an absent field. A scalar or singleton
    # object is rejected: the contract requires a real JSON array.
    $projectsWrapped = Get-RestoreWrappedPropertyValue -InputObject $Manifest -Name "projects"
    if (-not $projectsWrapped.Present -or $null -eq $projectsWrapped.Value) {
        throw "Restore manifest is missing required field 'projects'."
    }
    if (-not (Test-RestoreJsonArray -Value $projectsWrapped.Value)) {
        throw "Restore manifest field 'projects' must be a non-empty JSON array of project endpoint names."
    }
    $projectEntries = @($projectsWrapped.Value)
    if ($projectEntries.Count -eq 0) {
        throw "Restore manifest field 'projects' must be a non-empty JSON array of project endpoint names."
    }
    $seenProjects = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($project in $projectEntries) {
        if ($project -isnot [string] -or [string]::IsNullOrWhiteSpace($project)) {
            throw "Restore manifest field 'projects' contains an entry that is not a non-empty JSON string."
        }
        if (-not $seenProjects.Add($project)) {
            throw "Restore manifest field 'projects' contains duplicate entry '$project'."
        }
    }

    $null = Assert-RestoreManifestField -Manifest $Manifest -FieldName "apiSchemaFormatVersion" -AsNonEmptyString

    $effectiveSchemaHash = Assert-RestoreManifestField -Manifest $Manifest -FieldName "effectiveSchemaHash" -AsNonEmptyString
    if ($effectiveSchemaHash -cnotmatch "^[0-9a-f]{64}\z") {
        throw "Restore manifest field 'effectiveSchemaHash' must be a 64-character lowercase hex SHA-256."
    }

    $resourceKeyCount = Assert-RestoreManifestField -Manifest $Manifest -FieldName "resourceKeyCount"
    if (-not (Test-RestoreManifestInteger -Value $resourceKeyCount) -or $resourceKeyCount -lt 1) {
        throw "Restore manifest field 'resourceKeyCount' must be a positive integer."
    }

    $resourceKeySeedHashB64 = Assert-RestoreManifestField -Manifest $Manifest -FieldName "resourceKeySeedHashB64" -AsNonEmptyString
    $seedHashBytes = $null
    try {
        $seedHashBytes = [System.Convert]::FromBase64String($resourceKeySeedHashB64)
    }
    catch {
        throw "Restore manifest field 'resourceKeySeedHashB64' is not valid base64."
    }
    if ($seedHashBytes.Length -ne 32) {
        throw "Restore manifest field 'resourceKeySeedHashB64' must decode to exactly 32 bytes (SHA-256), but decoded to $($seedHashBytes.Length)."
    }

    $null = Assert-RestoreManifestField -Manifest $Manifest -FieldName "relationalMappingVersion" -AsNonEmptyString
    $null = Assert-RestoreManifestField -Manifest $Manifest -FieldName "engineVersion" -AsNonEmptyString

    $compatibilityLevel = Get-RestorePropertyValue -InputObject $Manifest -Name "databaseCompatibilityLevel"
    if ($databaseEngine -eq "mssql") {
        if (-not (Test-RestoreManifestInteger -Value $compatibilityLevel) -or $compatibilityLevel -lt 90) {
            throw "Restore manifest field 'databaseCompatibilityLevel' is required for mssql and must be an integer SQL Server compatibility level (90 or higher)."
        }
    }
    elseif ($null -ne $compatibilityLevel) {
        throw "Restore manifest field 'databaseCompatibilityLevel' must be omitted or null for postgresql."
    }

    $null = Assert-RestoreManifestField -Manifest $Manifest -FieldName "documentJsonColumnType" -AsNonEmptyString

    $inventorySha256 = Assert-RestoreManifestField -Manifest $Manifest -FieldName "inventorySha256" -AsNonEmptyString
    if ($inventorySha256 -cnotmatch "^[0-9a-f]{64}\z") {
        throw "Restore manifest field 'inventorySha256' must be a 64-character lowercase hex SHA-256."
    }

    $inventory = Assert-RestoreManifestField -Manifest $Manifest -FieldName "inventory"
    $recomputedInventoryHash = Get-CanonicalInventoryHash -Inventory $inventory
    if ($recomputedInventoryHash -cne $inventorySha256) {
        throw "Restore manifest is internally inconsistent: the recomputed canonical inventory hash '$recomputedInventoryHash' does not match the recorded inventorySha256 '$inventorySha256'."
    }

    $artifactFileName = Assert-RestoreManifestField -Manifest $Manifest -FieldName "artifactFileName" -AsNonEmptyString
    if ($artifactFileName -notmatch "^[A-Za-z0-9_.-]+\z") {
        throw "Restore manifest field 'artifactFileName' contains unsupported characters. Only letters, digits, dots, dashes, and underscores are allowed."
    }
    $expectedArtifactExtension = if ($databaseEngine -eq "mssql") { ".bak" } else { ".sql" }
    if (-not $artifactFileName.EndsWith($expectedArtifactExtension, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Restore manifest field 'artifactFileName' must end with '$expectedArtifactExtension' for databaseEngine '$databaseEngine', but found '$artifactFileName'."
    }

    $artifactSha256 = Assert-RestoreManifestField -Manifest $Manifest -FieldName "artifactSha256" -AsNonEmptyString
    if ($artifactSha256 -cnotmatch "^[0-9a-f]{64}\z") {
        throw "Restore manifest field 'artifactSha256' must be a 64-character lowercase hex SHA-256."
    }
}

function Read-RestoreManifest {
    <#
    .SYNOPSIS
    Reads and parses a restore-manifest.json file and validates it against the version-1
    contract via Assert-RestoreManifestShape. Returns the parsed manifest object.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Restore manifest was not found at '$Path'. Packages without a restore manifest are not eligible for restore."
    }

    $rawContent = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
    if ([string]::IsNullOrWhiteSpace($rawContent)) {
        throw "Restore manifest at '$Path' is empty."
    }

    $manifest = $null
    try {
        $manifest = $rawContent | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Restore manifest at '$Path' is not valid JSON: $($_.Exception.Message)"
    }

    Assert-RestoreManifestShape -Manifest $manifest

    return $manifest
}

function New-TemplateRestoreManifest {
    <#
    .SYNOPSIS
    Assembles the version-1 restore manifest from live-catalog facts and package identity,
    computes the canonical inventory hash, and self-validates the result against
    Assert-RestoreManifestShape before returning it, so a producer can never package a
    manifest the consumer contract would reject.

    .OUTPUTS
    Ordered hashtable in the documented field order, ready for JSON serialization.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Returns a manifest object; no system state is created or changed.')]
    param (
        [Parameter(Mandatory = $true)]
        [string]$PackageId,

        [Parameter(Mandatory = $true)]
        [string]$PackageVersion,

        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [ValidateSet("Minimal", "Populated")]
        [string]$TemplateKind,

        [Parameter(Mandatory = $true)]
        [string]$DataStandardVersion,

        [Parameter(Mandatory = $true)]
        [string[]]$ProjectName,

        [Parameter(Mandatory = $true)]
        [string]$ApiSchemaFormatVersion,

        [Parameter(Mandatory = $true)]
        [string]$EffectiveSchemaHash,

        [Parameter(Mandatory = $true)]
        [int]$ResourceKeyCount,

        [Parameter(Mandatory = $true)]
        [string]$ResourceKeySeedHashB64,

        [Parameter(Mandatory = $true)]
        [string]$RelationalMappingVersion,

        [Parameter(Mandatory = $true)]
        [string]$EngineVersion,

        [System.Nullable[int]]$DatabaseCompatibilityLevel = $null,

        [Parameter(Mandatory = $true)]
        [string]$DocumentJsonColumnType,

        [Parameter(Mandatory = $true)]
        $Inventory,

        [Parameter(Mandatory = $true)]
        [string]$ArtifactFileName,

        [Parameter(Mandatory = $true)]
        [string]$ArtifactSha256
    )

    $manifest = [ordered]@{
        version                  = $script:SupportedRestoreManifestVersion
        packageId                = $PackageId
        packageVersion           = $PackageVersion
        databaseEngine           = $DatabaseEngine
        templateKind             = $TemplateKind
        dataStandardVersion      = $DataStandardVersion
        contentProfile           = $script:RestoreContentProfileDmsDatastoreOnly
        projects                 = [string[]]$ProjectName
        apiSchemaFormatVersion   = $ApiSchemaFormatVersion
        effectiveSchemaHash      = $EffectiveSchemaHash
        resourceKeyCount         = $ResourceKeyCount
        resourceKeySeedHashB64   = $ResourceKeySeedHashB64
        relationalMappingVersion = $RelationalMappingVersion
        engineVersion            = $EngineVersion
        documentJsonColumnType   = $DocumentJsonColumnType
        inventory                = (ConvertTo-CanonicalInventoryDocument -Inventory $Inventory)
        inventorySha256          = (Get-CanonicalInventoryHash -Inventory $Inventory)
        artifactFileName         = $ArtifactFileName
        artifactSha256           = $ArtifactSha256
    }

    if ($DatabaseEngine -eq "mssql") {
        if ($null -eq $DatabaseCompatibilityLevel) {
            throw "DatabaseCompatibilityLevel is required for mssql restore manifests."
        }
        $manifest["databaseCompatibilityLevel"] = [int]$DatabaseCompatibilityLevel
    }
    elseif ($null -ne $DatabaseCompatibilityLevel) {
        throw "DatabaseCompatibilityLevel must not be supplied for postgresql restore manifests."
    }

    Assert-RestoreManifestShape -Manifest $manifest

    return $manifest
}

function ConvertTo-CanonicalInventoryDocument {
    <#
    .SYNOPSIS
    Returns the canonical inventory as plain ordered hashtables (sorted schemas/objects/
    principals with lowercase keys) for embedding in the restore manifest, so the packaged
    JSON is itself in canonical order.
    #>
    param (
        [Parameter(Mandatory = $true)]
        $Inventory
    )

    $canonical = ConvertTo-CanonicalInventory -Inventory $Inventory

    $schemas = [System.Collections.Generic.List[object]]::new()
    foreach ($schemaEntry in $canonical.Schemas) {
        $objects = [System.Collections.Generic.List[object]]::new()
        foreach ($objectEntry in $schemaEntry.Objects) {
            $objects.Add([ordered]@{ name = $objectEntry.Name; type = $objectEntry.Type })
        }
        $schemas.Add([ordered]@{ schemaName = $schemaEntry.SchemaName; objects = @($objects) })
    }

    return [ordered]@{
        schemas    = @($schemas)
        principals = [string[]]@($canonical.Principals)
    }
}

function ConvertFrom-MssqlBackupFileList {
    <#
    .SYNOPSIS
    Parses RESTORE FILELISTONLY output (pipe-separated rows) into the backup's data (D) and
    log (L) logical file names. Every data and log row is collected, not just the first of
    each, so multi-file backups relocate every file they list. Throws when either list is
    empty, because a restore command built from an incomplete file list would leave files
    pointing at their original in-container paths.
    #>
    param (
        [AllowEmptyCollection()]
        [AllowNull()]
        [string[]]$FileListOutput,

        [Parameter(Mandatory = $true)]
        [string]$BackupFileName
    )

    $dataLogicalNames = [System.Collections.Generic.List[string]]::new()
    $logLogicalNames = [System.Collections.Generic.List[string]]::new()

    foreach ($line in @($FileListOutput)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $fields = $line -split '\|'
        if ($fields.Count -lt 3) { continue }
        $logicalName = $fields[0].Trim()
        $fileType = $fields[2].Trim()
        if ($fileType -eq "D") { $dataLogicalNames.Add($logicalName) }
        elseif ($fileType -eq "L") { $logLogicalNames.Add($logicalName) }
    }

    if ($dataLogicalNames.Count -eq 0 -or $logLogicalNames.Count -eq 0) {
        throw "Could not determine the data and log logical file names from backup '$BackupFileName'."
    }

    return [pscustomobject]@{
        DataLogicalNames = [string[]]$dataLogicalNames.ToArray()
        LogLogicalNames  = [string[]]$logLogicalNames.ToArray()
    }
}

function New-MssqlRestoreMoveClause {
    <#
    .SYNOPSIS
    Builds one MOVE clause per backup file for RESTORE DATABASE ... WITH MOVE. The primary
    data file and first log keep the plain database-name-derived names, so a single-file
    backup produces the same RESTORE command shape it always has; every additional file gets
    a name suffixed with its own logical name, so each lands at its own deterministic path
    under the target data directory. Unlike the MOVE...FROM side (which only needs
    single-quote escaping inside its N'' literal), an extra logical name is interpolated
    into a new physical path, so it is validated against the same safe-character allow-list
    used for database names before use.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Returns MOVE clause strings; no system state is created or changed.')]
    param (
        [Parameter(Mandatory = $true)]
        [string]$DatabaseName,

        [Parameter(Mandatory = $true)]
        [string[]]$DataLogicalNames,

        [Parameter(Mandatory = $true)]
        [string[]]$LogLogicalNames,

        [Parameter(Mandatory = $true)]
        [string]$BackupFileName,

        [string]$DataDirectory = "/var/opt/mssql/data"
    )

    if ($DatabaseName -notmatch "^[A-Za-z0-9_]+\z") {
        throw "Database name '$DatabaseName' contains unsupported characters."
    }

    $moveClauses = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $DataLogicalNames.Count; $i++) {
        $logicalName = $DataLogicalNames[$i]
        if ($i -eq 0) {
            $physicalName = "$DatabaseName.mdf"
        }
        else {
            if ($logicalName -notmatch "^[A-Za-z0-9_]+\z") {
                throw "Data file logical name '$logicalName' from backup '$BackupFileName' contains unsupported characters and cannot be used to derive a restore path."
            }
            $physicalName = "${DatabaseName}_${logicalName}.ndf"
        }
        $moveClauses.Add("MOVE N'$($logicalName.Replace("'", "''"))' TO N'$DataDirectory/$physicalName'")
    }
    for ($i = 0; $i -lt $LogLogicalNames.Count; $i++) {
        $logicalName = $LogLogicalNames[$i]
        if ($i -eq 0) {
            $physicalName = "${DatabaseName}_log.ldf"
        }
        else {
            if ($logicalName -notmatch "^[A-Za-z0-9_]+\z") {
                throw "Log file logical name '$logicalName' from backup '$BackupFileName' contains unsupported characters and cannot be used to derive a restore path."
            }
            $physicalName = "${DatabaseName}_${logicalName}.ldf"
        }
        $moveClauses.Add("MOVE N'$($logicalName.Replace("'", "''"))' TO N'$DataDirectory/$physicalName'")
    }

    return [string[]]$moveClauses.ToArray()
}

function Get-PostgresqlAllowedExtensionName {
    <#
    .SYNOPSIS
    PostgreSQL extensions whose objects are permitted extension bootstrap: template dumps
    inject CREATE EXTENSION pgcrypto (dms.uuidv5 requires digest()), and its objects install
    into "public". Inventory queries filter ONLY these extensions' objects out, so objects
    from any other extension remain visible to the DMS-only gate as contamination.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns the allow-list of extension names; the singular Name suffix is the repo convention for list-returning helpers.')]
    param ()

    return [string[]]@("pgcrypto")
}

function Get-InventorySchemaQuerySql {
    <#
    .SYNOPSIS
    Engine SQL listing schema names for the given enumeration purpose (one name per row).
    Exclusions come from Get-RestoreSchemaNameExclusion, so dump discovery and the DMS-only
    gate's inventory enumeration stay on their documented, deliberately different scopes.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        [ValidateSet("DumpDiscovery", "InventoryEnumeration")]
        [string]$Purpose
    )

    $exclusion = Get-RestoreSchemaNameExclusion -DatabaseEngine $DatabaseEngine -Purpose $Purpose
    $quotedNames = ($exclusion.ExcludedSchemaName | ForEach-Object { "'" + $_ + "'" }) -join ", "

    if ($DatabaseEngine -eq "mssql") {
        return "SET NOCOUNT ON; SELECT name FROM sys.schemas WHERE name NOT IN ($quotedNames) AND name NOT LIKE 'db[_]%' ORDER BY name;"
    }

    return "SELECT nspname FROM pg_catalog.pg_namespace WHERE nspname !~ '^pg_' AND nspname NOT IN ($quotedNames) ORDER BY nspname;"
}

function Get-InventoryObjectQuerySql {
    <#
    .SYNOPSIS
    Engine SQL listing every inventoried object as pipe-separated "schema|name|type" rows
    over the InventoryEnumeration schema scope. PostgreSQL function names carry their
    identity-argument signature (overloads must stay distinct), triggers are qualified by
    their table, and objects owned by the allow-listed bootstrap extensions are filtered so
    everything else an artifact creates stays visible to the DMS-only gate.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine
    )

    if ($DatabaseEngine -eq "mssql") {
        return @(
            "SET NOCOUNT ON;",
            "SELECT s.name + '|' + o.name + '|' +",
            "  CASE o.type WHEN 'U' THEN 'table' WHEN 'V' THEN 'view' WHEN 'P' THEN 'procedure'",
            "    WHEN 'FN' THEN 'function' WHEN 'IF' THEN 'function' WHEN 'TF' THEN 'function' WHEN 'AF' THEN 'aggregate'",
            "    WHEN 'TR' THEN 'trigger' WHEN 'SO' THEN 'sequence' ELSE LOWER(RTRIM(o.type)) END",
            "FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id",
            "WHERE o.is_ms_shipped = 0",
            "  AND o.type IN ('U','V','P','FN','IF','TF','AF','TR','SO')",
            "  AND s.name NOT IN ('guest','sys','INFORMATION_SCHEMA') AND s.name NOT LIKE 'db[_]%'",
            "ORDER BY 1;"
        ) -join "`n"
    }

    $allowedExtensionList = (@(Get-PostgresqlAllowedExtensionName) | ForEach-Object { "'" + $_ + "'" }) -join ", "

    return @(
        "SELECT n.nspname || '|' || c.relname || '|' ||",
        "  CASE c.relkind WHEN 'r' THEN 'table' WHEN 'p' THEN 'table' WHEN 'v' THEN 'view' WHEN 'm' THEN 'materializedview' WHEN 'S' THEN 'sequence' END",
        "FROM pg_catalog.pg_class c",
        "JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace",
        "WHERE c.relkind IN ('r','p','v','m','S')",
        "  AND n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'",
        "  AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d JOIN pg_catalog.pg_extension e ON e.oid = d.refobjid",
        "                  WHERE d.classid = 'pg_catalog.pg_class'::regclass AND d.objid = c.oid AND d.deptype = 'e' AND e.extname IN ($allowedExtensionList))",
        "UNION ALL",
        "SELECT n.nspname || '|' || p.proname || '(' || pg_catalog.pg_get_function_identity_arguments(p.oid) || ')' || '|' ||",
        "  CASE p.prokind WHEN 'f' THEN 'function' WHEN 'p' THEN 'procedure' WHEN 'a' THEN 'aggregate' WHEN 'w' THEN 'window' END",
        "FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace",
        "WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'",
        "  AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_depend d JOIN pg_catalog.pg_extension e ON e.oid = d.refobjid",
        "                  WHERE d.classid = 'pg_catalog.pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e' AND e.extname IN ($allowedExtensionList))",
        "UNION ALL",
        "SELECT n.nspname || '|' || c.relname || '.' || t.tgname || '|' || 'trigger'",
        "FROM pg_catalog.pg_trigger t JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace",
        "WHERE NOT t.tgisinternal AND n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'",
        "ORDER BY 1;"
    ) -join "`n"
}

function Get-InventoryPrincipalQuerySql {
    <#
    .SYNOPSIS
    SQL Server SQL listing non-built-in database principals (one name per row). The built-in
    dbo/guest/sys/INFORMATION_SCHEMA principals, fixed database roles, the db_* role
    schemas' principals, and the built-in 'public' database role are excluded; anything else
    a backup carries (copied users, roles) is contamination the gate must see. PostgreSQL
    has no per-database principals in a schema-scoped SQL dump, so this query is
    MSSQL-only.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'DatabaseEngine', Justification = 'The ValidateSet-pinned engine parameter IS the contract: it makes a postgresql call site fail at bind time and keeps the query-builder surface uniform across engines.')]
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("mssql")]
        [string]$DatabaseEngine
    )

    return "SET NOCOUNT ON; SELECT name FROM sys.database_principals WHERE is_fixed_role = 0 AND name NOT IN ('dbo','guest','sys','INFORMATION_SCHEMA','public') AND name NOT LIKE 'db[_]%' ORDER BY name;"
}

function Get-EffectiveSchemaRowQuerySql {
    <#
    .SYNOPSIS
    Engine SQL returning the dms.EffectiveSchema singleton as one pipe-separated row:
    ApiSchemaFormatVersion|EffectiveSchemaHash|ResourceKeyCount|ResourceKeySeedHash(hex).
    The seed hash travels as lowercase hex (both engines can render it) and is converted to
    the manifest's base64 form by ConvertFrom-EffectiveSchemaRow.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine
    )

    if ($DatabaseEngine -eq "mssql") {
        return "SET NOCOUNT ON; SELECT [ApiSchemaFormatVersion] + '|' + [EffectiveSchemaHash] + '|' + CONVERT(nvarchar(8), [ResourceKeyCount]) + '|' + LOWER(CONVERT(nvarchar(64), [ResourceKeySeedHash], 2)) FROM [dms].[EffectiveSchema];"
    }

    return 'SELECT "ApiSchemaFormatVersion" || ''|'' || "EffectiveSchemaHash" || ''|'' || "ResourceKeyCount"::text || ''|'' || encode("ResourceKeySeedHash", ''hex'') FROM dms."EffectiveSchema";'
}

function Get-EngineVersionQuerySql {
    <#
    .SYNOPSIS
    Engine SQL returning the live server version as a single scalar row.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine
    )

    if ($DatabaseEngine -eq "mssql") {
        return "SET NOCOUNT ON; SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'));"
    }

    return "SELECT current_setting('server_version');"
}

function Get-DatabaseCompatibilityLevelQuerySql {
    <#
    .SYNOPSIS
    SQL Server SQL returning a database's compatibility level. The database name is
    interpolated into an N'' literal, so it is charset-validated first.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string]$DatabaseName
    )

    if ($DatabaseName -notmatch "^[A-Za-z0-9_]+\z") {
        throw "Database name '$DatabaseName' contains unsupported characters."
    }

    return "SET NOCOUNT ON; SELECT compatibility_level FROM sys.databases WHERE name = N'$DatabaseName';"
}

function Get-DocumentJsonColumnTypeQuerySql {
    <#
    .SYNOPSIS
    Engine SQL returning the physical storage type of dms.Document.DocumentJson from the
    live catalog (the D8a physical-baseline fact; never assumed from configuration).
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine
    )

    if ($DatabaseEngine -eq "mssql") {
        return "SET NOCOUNT ON; SELECT t.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(N'[dms].[Document]') AND c.name = N'DocumentJson';"
    }

    return "SELECT data_type FROM information_schema.columns WHERE table_schema = 'dms' AND table_name = 'Document' AND column_name = 'DocumentJson';"
}

function ConvertFrom-InventoryQueryRow {
    <#
    .SYNOPSIS
    Builds the canonical-inventory input object from raw query rows: every schema row
    becomes a schema entry (including zero-object schemas such as PostgreSQL "public" or
    SQL Server "dbo"), every "schema|name|type" object row lands under its schema, and
    principal rows become the principals list. An object row naming a schema absent from
    the schema rows is a data-integrity failure.
    #>
    param (
        [AllowEmptyCollection()]
        [AllowNull()]
        [string[]]$SchemaRow,

        [AllowEmptyCollection()]
        [AllowNull()]
        [string[]]$ObjectRow,

        [AllowEmptyCollection()]
        [AllowNull()]
        [string[]]$PrincipalRow
    )

    $schemaObjects = [ordered]@{}
    foreach ($rawSchemaRow in @($SchemaRow)) {
        if ([string]::IsNullOrWhiteSpace($rawSchemaRow)) { continue }
        $schemaName = $rawSchemaRow.Trim()
        if ($schemaObjects.Contains($schemaName)) {
            throw "Inventory schema query returned duplicate schema '$schemaName'."
        }
        $schemaObjects[$schemaName] = [System.Collections.Generic.List[object]]::new()
    }

    foreach ($rawObjectRow in @($ObjectRow)) {
        if ([string]::IsNullOrWhiteSpace($rawObjectRow)) { continue }
        $fields = $rawObjectRow -split '\|'
        if ($fields.Count -ne 3) {
            throw "Inventory object query returned a malformed row (expected 'schema|name|type'): '$rawObjectRow'."
        }
        $schemaName = $fields[0].Trim()
        $objectName = $fields[1].Trim()
        $objectType = $fields[2].Trim()
        if ([string]::IsNullOrWhiteSpace($schemaName) -or [string]::IsNullOrWhiteSpace($objectName) -or [string]::IsNullOrWhiteSpace($objectType)) {
            throw "Inventory object query returned a row with an empty field: '$rawObjectRow'."
        }
        if (-not $schemaObjects.Contains($schemaName)) {
            throw "Inventory object query returned object '$objectName' in schema '$schemaName', which the schema query did not report."
        }
        $schemaObjects[$schemaName].Add(@{ name = $objectName; type = $objectType })
    }

    $schemas = [System.Collections.Generic.List[object]]::new()
    foreach ($schemaName in $schemaObjects.Keys) {
        $schemas.Add(@{ schemaName = $schemaName; objects = @($schemaObjects[$schemaName]) })
    }

    $principals = [System.Collections.Generic.List[string]]::new()
    foreach ($rawPrincipalRow in @($PrincipalRow)) {
        if ([string]::IsNullOrWhiteSpace($rawPrincipalRow)) { continue }
        $principals.Add($rawPrincipalRow.Trim())
    }

    return @{
        schemas    = @($schemas)
        principals = [string[]]$principals.ToArray()
    }
}

function Select-InventorySchemaScope {
    <#
    .SYNOPSIS
    Returns a copy of an inventory restricted to the named schemas (used to scope the
    manifest inventory to what the artifact actually contains, e.g. a dms-schema-only
    PostgreSQL dump). Principals are preserved unless -ExcludePrincipals is set (a
    PostgreSQL SQL dump carries no principals, so its artifact scope drops them).
    #>
    param (
        [Parameter(Mandatory = $true)]
        $Inventory,

        [Parameter(Mandatory = $true)]
        [string[]]$SchemaName,

        [switch]$ExcludePrincipals
    )

    $canonical = ConvertTo-CanonicalInventory -Inventory $Inventory
    $selectedNames = [System.Collections.Generic.HashSet[string]]::new([string[]]$SchemaName, [System.StringComparer]::Ordinal)

    $schemas = [System.Collections.Generic.List[object]]::new()
    foreach ($schemaEntry in $canonical.Schemas) {
        if (-not $selectedNames.Contains($schemaEntry.SchemaName)) { continue }
        $objects = [System.Collections.Generic.List[object]]::new()
        foreach ($objectEntry in $schemaEntry.Objects) {
            $objects.Add(@{ name = $objectEntry.Name; type = $objectEntry.Type })
        }
        $schemas.Add(@{ schemaName = $schemaEntry.SchemaName; objects = @($objects) })
    }

    $principals = [string[]]@()
    if (-not $ExcludePrincipals) {
        $principals = [string[]]@($canonical.Principals)
    }

    return @{
        schemas    = @($schemas)
        principals = $principals
    }
}

function ConvertFrom-EffectiveSchemaRow {
    <#
    .SYNOPSIS
    Parses the pipe-separated dms.EffectiveSchema singleton row into typed fields,
    requiring exactly one row, a 64-hex effective schema hash, a positive integer resource
    key count, and a 32-byte hex seed hash (returned base64-encoded for the manifest).
    #>
    param (
        [AllowEmptyCollection()]
        [AllowNull()]
        [string[]]$Row
    )

    $rows = @(@($Row) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($rows.Count -ne 1) {
        throw "Expected exactly one dms.EffectiveSchema row, found $($rows.Count). The source database is not a provisioned DMS datastore."
    }

    $fields = $rows[0].Trim() -split '\|'
    if ($fields.Count -ne 4) {
        throw "dms.EffectiveSchema row is malformed (expected 4 pipe-separated fields): '$($rows[0])'."
    }

    $apiSchemaFormatVersion = $fields[0].Trim()
    $effectiveSchemaHash = $fields[1].Trim()
    $resourceKeyCountText = $fields[2].Trim()
    $seedHashHex = $fields[3].Trim().ToLowerInvariant()

    if ([string]::IsNullOrWhiteSpace($apiSchemaFormatVersion)) {
        throw "dms.EffectiveSchema.ApiSchemaFormatVersion is empty."
    }
    if ($effectiveSchemaHash -cnotmatch "^[0-9a-f]{64}\z") {
        throw "dms.EffectiveSchema.EffectiveSchemaHash is not a 64-character lowercase hex SHA-256: '$effectiveSchemaHash'."
    }

    $resourceKeyCount = 0
    if (-not [int]::TryParse($resourceKeyCountText, [ref]$resourceKeyCount) -or $resourceKeyCount -lt 1) {
        throw "dms.EffectiveSchema.ResourceKeyCount is not a positive integer: '$resourceKeyCountText'."
    }

    if ($seedHashHex -cnotmatch "^[0-9a-f]{64}\z") {
        throw "dms.EffectiveSchema.ResourceKeySeedHash is not 32 bytes of hex: '$seedHashHex'."
    }
    $seedHashBytes = [System.Convert]::FromHexString($seedHashHex)

    return [pscustomobject]@{
        ApiSchemaFormatVersion = $apiSchemaFormatVersion
        EffectiveSchemaHash    = $effectiveSchemaHash
        ResourceKeyCount       = $resourceKeyCount
        ResourceKeySeedHashB64 = [System.Convert]::ToBase64String($seedHashBytes)
    }
}

function Get-TemplateProjectSchemaPartition {
    <#
    .SYNOPSIS
    Partitions inventoried schema names into the DMS-owned roles: the dms schema, the
    optional auth companion, the engine's always-present schema (PostgreSQL public /
    SQL Server dbo), tracked_changes_<project> companions, and resource-project schemas.
    A name is a tracked_changes companion only with the full 'tracked_changes_' prefix
    including the underscore, so lookalikes such as 'tracked_changesx' partition as
    resource schemas and then fail the companion-pairing gate.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [AllowEmptyCollection()]
        [string[]]$SchemaName = @()
    )

    $alwaysPresentSchemaName = if ($DatabaseEngine -eq "mssql") { "dbo" } else { "public" }

    $hasDms = $false
    $hasAuth = $false
    $alwaysPresent = [System.Collections.Generic.List[string]]::new()
    $trackedChangesProjects = [System.Collections.Generic.List[string]]::new()
    $resourceSchemas = [System.Collections.Generic.List[string]]::new()

    foreach ($name in @($SchemaName)) {
        if ([string]::IsNullOrWhiteSpace($name)) { continue }
        if ($name -ceq "dms") { $hasDms = $true; continue }
        if ($name -ceq "auth") { $hasAuth = $true; continue }
        if ($name.Equals($alwaysPresentSchemaName, [System.StringComparison]::OrdinalIgnoreCase)) {
            $alwaysPresent.Add($name)
            continue
        }
        if ($name.StartsWith("tracked_changes_", [System.StringComparison]::Ordinal) -and $name.Length -gt "tracked_changes_".Length) {
            $trackedChangesProjects.Add($name.Substring("tracked_changes_".Length))
            continue
        }
        $resourceSchemas.Add($name)
    }

    $trackedChangesProjects.Sort([System.StringComparer]::Ordinal)
    $resourceSchemas.Sort([System.StringComparer]::Ordinal)

    # Project list order: core first, then the remaining resource schemas in ordinal order.
    $projectSchemaNames = [System.Collections.Generic.List[string]]::new()
    if ($resourceSchemas.Contains("edfi")) {
        $projectSchemaNames.Add("edfi")
    }
    foreach ($resourceSchema in $resourceSchemas) {
        if ($resourceSchema -cne "edfi") { $projectSchemaNames.Add($resourceSchema) }
    }

    return [pscustomobject]@{
        HasDms                     = $hasDms
        HasAuth                    = $hasAuth
        AlwaysPresentSchemaName    = [string[]]$alwaysPresent.ToArray()
        TrackedChangesProjectNames = [string[]]$trackedChangesProjects.ToArray()
        ResourceSchemaNames        = [string[]]$resourceSchemas.ToArray()
        ProjectSchemaNames         = [string[]]$projectSchemaNames.ToArray()
    }
}

function Assert-DmsOnlyInventory {
    <#
    .SYNOPSIS
    The DMS-only content gate shared by the template producer (before dump/packaging) and
    the restore consumer (against the scratch database). Fails, aggregating every
    violation, unless the inventory contains only the exact DMS-owned surface: the dms
    schema (non-empty), the optional auth companion, resource-project schemas each paired
    with their tracked_changes_<project> companion (and vice versa), an empty
    always-present public/dbo schema (beyond allow-listed extension bootstrap, which the
    inventory queries already filter), no dmscs schema, no OpenIddict objects anywhere, and
    (SQL Server) no non-built-in database principals.

    .OUTPUTS
    The schema partition (Get-TemplateProjectSchemaPartition) on success, so callers derive
    the manifest's project list from the same validated facts.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory = $true)]
        $Inventory,

        [string]$SourceDescription = "The database"
    )

    $canonical = ConvertTo-CanonicalInventory -Inventory $Inventory
    $schemaNames = [string[]]@($canonical.Schemas | ForEach-Object { $_.SchemaName })
    $partition = Get-TemplateProjectSchemaPartition -DatabaseEngine $DatabaseEngine -SchemaName $schemaNames

    $violations = [System.Collections.Generic.List[string]]::new()

    if (-not $partition.HasDms) {
        $violations.Add("the 'dms' schema is missing")
    }

    foreach ($schemaEntry in $canonical.Schemas) {
        if ($schemaEntry.SchemaName -ceq "dms" -and @($schemaEntry.Objects).Count -eq 0) {
            $violations.Add("the 'dms' schema contains no objects")
        }

        if ($schemaEntry.SchemaName.Equals("dmscs", [System.StringComparison]::OrdinalIgnoreCase)) {
            $violations.Add("it contains the Configuration Service schema 'dmscs'")
        }

        foreach ($objectEntry in $schemaEntry.Objects) {
            if ($objectEntry.Name.StartsWith("OpenIddict", [System.StringComparison]::OrdinalIgnoreCase)) {
                $violations.Add("it contains identity-state object '$($schemaEntry.SchemaName).$($objectEntry.Name)'")
            }
        }

        foreach ($alwaysPresentName in $partition.AlwaysPresentSchemaName) {
            if ($schemaEntry.SchemaName -ceq $alwaysPresentName -and @($schemaEntry.Objects).Count -gt 0) {
                $objectList = (@($schemaEntry.Objects) | ForEach-Object { $_.Name }) -join ", "
                $violations.Add("the always-present '$alwaysPresentName' schema contains unexpected objects beyond allow-listed extension bootstrap: $objectList")
            }
        }
    }

    # dmscs partitions as a resource schema; skip its (already-reported) pairing violation
    # so the aggregate message stays focused on the real defect.
    $resourceSchemasForPairing = @($partition.ResourceSchemaNames | Where-Object { -not $_.Equals("dmscs", [System.StringComparison]::OrdinalIgnoreCase) })

    foreach ($resourceSchema in $resourceSchemasForPairing) {
        if ($partition.TrackedChangesProjectNames -cnotcontains $resourceSchema) {
            $violations.Add("schema '$resourceSchema' has no tracked_changes_$resourceSchema companion, so it is not an authoritative DMS resource schema")
        }
    }
    foreach ($trackedChangesProject in $partition.TrackedChangesProjectNames) {
        if ($partition.ResourceSchemaNames -cnotcontains $trackedChangesProject) {
            $violations.Add("companion schema 'tracked_changes_$trackedChangesProject' has no matching resource schema '$trackedChangesProject'")
        }
    }

    if ($partition.HasDms -and $resourceSchemasForPairing.Count -gt 0 -and $partition.ResourceSchemaNames -cnotcontains "edfi") {
        $violations.Add("the core resource schema 'edfi' is missing")
    }

    if (@($canonical.Principals).Count -gt 0) {
        $violations.Add("it carries unexpected database principals: $($canonical.Principals -join ', ')")
    }

    if ($violations.Count -gt 0) {
        throw "$SourceDescription is not a dedicated DMS datastore: $($violations -join '; ')."
    }

    return $partition
}

function Get-RelationalMappingVersionFromSource {
    <#
    .SYNOPSIS
    Reads the authoritative RelationalMappingVersion constant from
    SchemaHashConstants.cs, the single in-repo source of that value (it is a hash input,
    not a database column). A missing file or anything other than exactly one constant
    match fails rather than guessing.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [string]$SchemaHashConstantsPath
    )

    if (-not (Test-Path -LiteralPath $SchemaHashConstantsPath -PathType Leaf)) {
        throw "SchemaHashConstants.cs was not found at '$SchemaHashConstantsPath'; the relational mapping version cannot be determined."
    }

    $content = Get-Content -LiteralPath $SchemaHashConstantsPath -Raw -ErrorAction Stop
    $constantMatches = [System.Text.RegularExpressions.Regex]::Matches(
        $content,
        'const\s+string\s+RelationalMappingVersion\s*=\s*"([^"]+)"')

    if ($constantMatches.Count -ne 1) {
        throw "Expected exactly one RelationalMappingVersion constant in '$SchemaHashConstantsPath', found $($constantMatches.Count)."
    }

    return $constantMatches[0].Groups[1].Value
}

function Get-SourceIdentityReseedSql {
    <#
    .SYNOPSIS
    The engine's SQL that replaces the restored dms.DataStoreIdentity.SourceIdentity with a
    newly generated UUID, failing unless exactly the singleton row was updated. Lines are
    joined with LF so the SQL bytes are identical on every platform and git line-ending
    configuration. Template-Management.psm1 retains its own git-eol-normalized here-string
    copy for byte parity of the legacy verification path; this builder is authoritative for
    new restore consumers.
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine
    )

    if ($DatabaseEngine -eq "mssql") {
        return @(
            "SET NOCOUNT ON;",
            "UPDATE [dms].[DataStoreIdentity]",
            "SET [SourceIdentity] = NEWID()",
            "WHERE [DataStoreIdentitySingletonId] = 1;",
            "IF @@ROWCOUNT <> 1",
            "    THROW 50000, N'Restored database is missing the dms.DataStoreIdentity singleton row.', 1;"
        ) -join "`n"
    }

    return @(
        'DO $$',
        "DECLARE",
        "    _updated_count integer;",
        "BEGIN",
        '    UPDATE "dms"."DataStoreIdentity"',
        '    SET "SourceIdentity" = gen_random_uuid()',
        '    WHERE "DataStoreIdentitySingletonId" = 1;',
        "",
        "    GET DIAGNOSTICS _updated_count = ROW_COUNT;",
        "    IF _updated_count <> 1 THEN",
        "        RAISE EXCEPTION 'Restored database is missing the dms.DataStoreIdentity singleton row.';",
        "    END IF;",
        "END",
        '$$;'
    ) -join "`n"
}

function Get-SourceIdentitySelectSql {
    <#
    .SYNOPSIS
    The engine's SQL that returns every dms.DataStoreIdentity.SourceIdentity value as text,
    one row per line, for the post-restore verification (exactly one row, a valid non-empty
    UUID, and a value different from the package's).
    #>
    param (
        [Parameter(Mandatory = $true)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine
    )

    if ($DatabaseEngine -eq "mssql") {
        return "SET NOCOUNT ON; SELECT CONVERT(nvarchar(36), [SourceIdentity]) FROM [dms].[DataStoreIdentity];"
    }

    return 'SELECT "SourceIdentity"::text FROM "dms"."DataStoreIdentity";'
}

function Test-RestoredSourceIdentityValue {
    <#
    .SYNOPSIS
    Verdict for the post-reseed SourceIdentity verification. The restored target is valid
    only when ALL of the following hold: dms.DataStoreIdentity has exactly one row, the
    stored value parses as a UUID, it is not the empty UUID, and it differs from the
    package's SourceIdentity captured during scratch validation (a repeated restore must
    never reuse the package identity).

    .PARAMETER SourceIdentityRow
    The SourceIdentity value rows read from the restored target (already whitespace-filtered
    by the caller's query transport, or not - blank rows are ignored here).

    .PARAMETER PackageSourceIdentity
    The SourceIdentity value the package artifact carried, captured from the scratch
    database before the target restore. Must itself be a valid UUID; a non-UUID value is a
    caller defect, not a target verdict, and throws.

    .OUTPUTS
    PSCustomObject { IsValid = [bool]; Reason = [string] }.
    #>
    param (
        [AllowEmptyCollection()]
        [AllowNull()]
        [string[]]$SourceIdentityRow,

        [Parameter(Mandatory = $true)]
        [string]$PackageSourceIdentity
    )

    $packageGuid = [System.Guid]::Empty
    if (-not [System.Guid]::TryParse($PackageSourceIdentity, [ref]$packageGuid)) {
        throw "PackageSourceIdentity '$PackageSourceIdentity' is not a valid UUID; the package value captured during scratch validation is required for the reseed verification."
    }

    $rows = @(@($SourceIdentityRow) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() })

    if ($rows.Count -ne 1) {
        return [pscustomobject]@{
            IsValid = $false
            Reason  = "Expected exactly one dms.DataStoreIdentity row after restore, found $($rows.Count)."
        }
    }

    $restoredGuid = [System.Guid]::Empty
    if (-not [System.Guid]::TryParse($rows[0], [ref]$restoredGuid)) {
        return [pscustomobject]@{
            IsValid = $false
            Reason  = "Restored dms.DataStoreIdentity.SourceIdentity is not a valid UUID."
        }
    }

    if ($restoredGuid -eq [System.Guid]::Empty) {
        return [pscustomobject]@{
            IsValid = $false
            Reason  = "Restored dms.DataStoreIdentity.SourceIdentity is the empty UUID."
        }
    }

    if ($restoredGuid -eq $packageGuid) {
        return [pscustomobject]@{
            IsValid = $false
            Reason  = "Restored dms.DataStoreIdentity.SourceIdentity still matches the package value; the reseed did not take effect."
        }
    }

    return [pscustomobject]@{
        IsValid = $true
        Reason  = ""
    }
}

Export-ModuleMember -Function `
    Get-RestoreManifestFileName, `
    Test-ReservedDatabaseName, `
    Assert-SafeRestoreDatabaseName, `
    New-RestoreGeneratedDatabaseName, `
    New-RestoreScratchDatabaseName, `
    New-RestorePreflightDatabaseName, `
    Get-RestoreSchemaNameExclusion, `
    Get-RestoreDocumentJsonBaselineType, `
    ConvertTo-CanonicalInventoryJson, `
    Get-CanonicalInventoryHash, `
    Assert-RestoreManifestShape, `
    Read-RestoreManifest, `
    Get-PostgresqlAllowedExtensionName, `
    Get-InventorySchemaQuerySql, `
    Get-InventoryObjectQuerySql, `
    Get-InventoryPrincipalQuerySql, `
    Get-EffectiveSchemaRowQuerySql, `
    Get-EngineVersionQuerySql, `
    Get-DatabaseCompatibilityLevelQuerySql, `
    Get-DocumentJsonColumnTypeQuerySql, `
    ConvertFrom-InventoryQueryRow, `
    Select-InventorySchemaScope, `
    ConvertFrom-EffectiveSchemaRow, `
    Get-TemplateProjectSchemaPartition, `
    Assert-DmsOnlyInventory, `
    Get-RelationalMappingVersionFromSource, `
    New-TemplateRestoreManifest, `
    ConvertFrom-MssqlBackupFileList, `
    New-MssqlRestoreMoveClause, `
    Get-SourceIdentityReseedSql, `
    Get-SourceIdentitySelectSql, `
    Test-RestoredSourceIdentityValue
