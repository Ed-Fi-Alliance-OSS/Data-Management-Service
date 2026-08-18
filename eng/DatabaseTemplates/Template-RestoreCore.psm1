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

    $rawSchemas = Get-RestorePropertyValue -InputObject $Inventory -Name "schemas"
    if ($null -eq $rawSchemas) {
        throw "Inventory is missing the required 'schemas' array."
    }

    $schemaEntries = [System.Collections.Generic.List[object]]::new()
    $seenSchemaNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

    foreach ($rawSchema in @($rawSchemas)) {
        $schemaName = [string](Get-RestorePropertyValue -InputObject $rawSchema -Name "schemaName")
        if ([string]::IsNullOrWhiteSpace($schemaName)) {
            throw "Inventory contains a schema entry without a non-empty 'schemaName'."
        }
        if (-not $seenSchemaNames.Add($schemaName)) {
            throw "Inventory contains duplicate schema entry '$schemaName'."
        }

        $objectEntries = [System.Collections.Generic.List[object]]::new()
        $seenObjectKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
        $rawObjects = Get-RestorePropertyValue -InputObject $rawSchema -Name "objects"
        foreach ($rawObject in @($rawObjects)) {
            if ($null -eq $rawObject) { continue }
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
    $rawPrincipals = Get-RestorePropertyValue -InputObject $Inventory -Name "principals"
    foreach ($rawPrincipal in @($rawPrincipals)) {
        # An absent or JSON-null principals property means "no principals", not an entry.
        if ($null -eq $rawPrincipal) { continue }
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

    # The projects value is read inline rather than through the field helper: PowerShell
    # unrolls an empty array at a function return boundary, which would make an empty
    # projects array indistinguishable from an absent field.
    if (-not (Test-RestorePropertyPresent -InputObject $Manifest -Name "projects")) {
        throw "Restore manifest is missing required field 'projects'."
    }
    # Plain branch assignments, not a captured if/else expression: PowerShell flattens an
    # empty-array branch value of a captured if/else expression to $null.
    $projectsRaw = $null
    if ($Manifest -is [System.Collections.IDictionary]) {
        $projectsRaw = $Manifest["projects"]
    }
    else {
        $projectsRaw = $Manifest.PSObject.Properties["projects"].Value
    }
    if ($null -eq $projectsRaw) {
        throw "Restore manifest is missing required field 'projects'."
    }
    $projectEntries = @($projectsRaw)
    if ($projectEntries.Count -eq 0) {
        throw "Restore manifest field 'projects' must be a non-empty array of project endpoint names."
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
    if ($effectiveSchemaHash -cnotmatch "^[0-9a-f]{64}$") {
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
    if ($inventorySha256 -cnotmatch "^[0-9a-f]{64}$") {
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
    if ($artifactSha256 -cnotmatch "^[0-9a-f]{64}$") {
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

    if ($DatabaseName -notmatch "^[A-Za-z0-9_]+$") {
        throw "Database name '$DatabaseName' contains unsupported characters."
    }

    $moveClauses = [System.Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $DataLogicalNames.Count; $i++) {
        $logicalName = $DataLogicalNames[$i]
        if ($i -eq 0) {
            $physicalName = "$DatabaseName.mdf"
        }
        else {
            if ($logicalName -notmatch "^[A-Za-z0-9_]+$") {
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
            if ($logicalName -notmatch "^[A-Za-z0-9_]+$") {
                throw "Log file logical name '$logicalName' from backup '$BackupFileName' contains unsupported characters and cannot be used to derive a restore path."
            }
            $physicalName = "${DatabaseName}_${logicalName}.ldf"
        }
        $moveClauses.Add("MOVE N'$($logicalName.Replace("'", "''"))' TO N'$DataDirectory/$physicalName'")
    }

    return [string[]]$moveClauses.ToArray()
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
    ConvertFrom-MssqlBackupFileList, `
    New-MssqlRestoreMoveClause, `
    Get-SourceIdentityReseedSql, `
    Get-SourceIdentitySelectSql, `
    Test-RestoredSourceIdentityValue
