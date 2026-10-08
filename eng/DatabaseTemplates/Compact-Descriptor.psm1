# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-CompactDescriptorInventory {
    <#
    .SYNOPSIS
        Reads the reviewed, implementation-generated relational-model manifest, never a database catalog.
    .PARAMETER ExpectedModelManifestPath
        FixtureRunner/DerivedModelSetManifestEmitter output with ALL resource_details included.
    #>
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)][string]$ExpectedModelManifestPath,
        [Parameter(Mandatory)][ValidateSet('pgsql', 'mssql')][string]$Dialect
    )

    $model = Get-Content -LiteralPath $ExpectedModelManifestPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($field in @('dialect', 'effective_schema_hash', 'relational_mapping_version', 'projects', 'resources',
            'resource_details', 'abstract_identity_tables', 'indexes', 'tracked_change_tables')) {
        if (-not $model.Contains($field)) { throw "Incomplete compact descriptor manifest: missing $field." }
    }
    if ($model.dialect -cne $Dialect) { throw "Manifest dialect '$($model.dialect)' does not match '$Dialect'." }
    if ($model.effective_schema_hash -cnotmatch '^[a-f0-9]{64}$' -or $model.relational_mapping_version -notmatch '^v[3-9][0-9]*$|^v[1-9][0-9]+$') {
        throw 'Stale or invalid compact descriptor manifest fingerprint/version.'
    }
    $resources = @{}
    foreach ($resource in $model.resources) {
        $key = "$($resource.project_name)/$($resource.resource_name)"
        if ($resources.ContainsKey($key)) { throw "Duplicate manifest resource: $key." }
        $resources[$key] = $resource
    }
    if ($resources.Count -eq 0 -or $model.resource_details.Count -ne $resources.Count) {
        throw 'Incomplete resource_details coverage in compact descriptor manifest.'
    }
    $tables = @{}
    $abstractTables = @{}
    $covered = @{}
    $sharedCount = 0
    foreach ($detail in $model.resource_details) {
        $key = "$($detail.resource.project_name)/$($detail.resource.resource_name)"
        if (-not $resources.ContainsKey($key) -or $covered.ContainsKey($key)) { throw "Invalid resource_details coverage: $key." }
        $covered[$key] = $true
        if ($detail.storage_kind -cne $resources[$key].storage_kind -or $detail.physical_schema -cne $resources[$key].physical_schema) {
            throw "Mismatched resource_details schema/storage: $key."
        }
        if ($detail.storage_kind -ceq 'SharedDescriptorTable') {
            $sharedCount++
            $shared = $detail.shared_descriptor_table
            $compact = @($shared.columns | Where-Object { $_.name -ceq 'DescriptorId' })
            $document = @($shared.columns | Where-Object { $_.name -ceq 'DocumentId' })
            if ($shared.schema -cne 'dms' -or $shared.name -cne 'Descriptor' -or
                $shared.key_columns.Count -ne 1 -or $shared.key_columns[0].name -cne 'DescriptorId' -or
                $compact.Count -ne 1 -or $compact[0].type.kind -cne 'Int32' -or $compact[0].is_nullable -or
                $document.Count -ne 1 -or $document[0].type.kind -cne 'Int64' -or $document[0].is_nullable -or
                @($shared.columns | Where-Object { $_.name -cin @('Uri', 'Discriminator') }).Count -ne 0 -or
                $shared.identity_metadata.physical_row_identity_columns.Count -ne 1 -or
                $shared.identity_metadata.physical_row_identity_columns[0] -cne 'DescriptorId' -or
                $shared.identity_metadata.root_scope_locator_columns.Count -ne 1 -or
                $shared.identity_metadata.root_scope_locator_columns[0] -cne 'DocumentId') {
                throw "Pre-compact shared descriptor model: $key."
            }
        }
        elseif ($detail.storage_kind -cne 'RelationalTables' -or $detail.tables.Count -eq 0) {
            throw "Missing physical tables for $key."
        }
        foreach ($table in $detail.tables) {
            $tableKey = "$($table.schema)/$($table.name)"
            $serialized = ConvertTo-Json -InputObject $table -Depth 100 -Compress
            if ($tables.ContainsKey($tableKey) -and (ConvertTo-Json -InputObject $tables[$tableKey] -Depth 100 -Compress) -cne $serialized) {
                throw "Conflicting physical table definitions: $tableKey."
            }
            $tables[$tableKey] = $table
        }
    }
    foreach ($abstract in $model.abstract_identity_tables) {
        $table = $abstract.table
        $tableKey = "$($table.schema)/$($table.name)"
        if ($tables.ContainsKey($tableKey)) { throw "Duplicate abstract physical table: $tableKey." }
        $tables[$tableKey] = $table
        $abstractTables[$tableKey] = $true
    }
    $history = @($model.tracked_change_tables | Where-Object { $_.kind -ceq 'SharedDescriptor' })
    if ($sharedCount -eq 0 -or $history.Count -ne 1 -or $history[0].table.schema -cne 'tracked_changes_edfi' -or
        $history[0].table.name -cne 'Descriptor' -or
        @($history[0].system_columns | Where-Object { $_.role -ceq 'ResourceKeyId' -and $_.column -ceq 'ResourceKeyId' -and -not $_.is_nullable }).Count -ne 1 -or
        @($history[0].system_columns | Where-Object { $_.role -ceq 'Discriminator' }).Count -ne 0) {
        throw 'Missing or pre-compact shared descriptor/history baseline.'
    }

    $columns = [System.Collections.Generic.List[object]]::new()
    $constraints = [System.Collections.Generic.List[object]]::new()
    $indexes = [System.Collections.Generic.List[object]]::new()
    foreach ($tableKey in @($tables.Keys | Sort-Object -CaseSensitive)) {
        $table = $tables[$tableKey]
        foreach ($column in $table.columns | Where-Object { $_.kind -ceq 'DescriptorFk' }) {
            if ($column.type.kind -cne 'Int32') { throw "Pre-compact descriptor column/alias: $tableKey/$($column.name)." }
            if ($column.storage.kind -ceq 'UnifiedAlias') {
                $canonical = @($table.columns | Where-Object { $_.name -ceq $column.storage.canonical_column -and $_.storage.kind -ceq 'Stored' -and $_.kind -ceq 'DescriptorFk' })
                if ($canonical.Count -ne 1) { throw "Missing canonical descriptor storage for alias: $tableKey/$($column.name)." }
            }
            elseif ($column.storage.kind -cne 'Stored') { throw "Unsupported descriptor storage: $tableKey/$($column.name)." }
        }
        $stored = @($table.columns | Where-Object { $_.kind -ceq 'DescriptorFk' -and $_.storage.kind -ceq 'Stored' })
        $names = @($stored | ForEach-Object { $_.name })
        foreach ($column in $stored) {
            if ($column.type.kind -cne 'Int32') { throw "Pre-compact descriptor column: $tableKey/$($column.name)." }
            $direct = @($table.constraints | Where-Object {
                    $_.kind -ceq 'ForeignKey' -and $_.columns.Count -eq 1 -and $_.columns[0] -ceq $column.name -and
                    $_.target_table.schema -ceq 'dms' -and $_.target_table.name -ceq 'Descriptor' -and
                    $_.target_columns.Count -eq 1 -and $_.target_columns[0] -ceq 'DescriptorId' -and $_.on_delete -ceq 'NoAction' -and $_.on_update -ceq 'NoAction'
                })
            # Abstract identity projections inherit the type from their concrete source; they do
            # not declare a second direct descriptor FK in the generated model.
            if (-not $abstractTables.ContainsKey($tableKey) -and $direct.Count -ne 1) { throw "Missing compact descriptor FK: $tableKey/$($column.name)." }
            $columns.Add(@{ schema = $table.schema; table = $table.name; name = $column.name; scalar_type = $column.type.kind; is_nullable = $column.is_nullable })
        }
        foreach ($constraint in $table.constraints) {
            if ($constraint.kind -cin @('ForeignKey', 'Unique') -and @($constraint.columns | Where-Object { $_ -cin $names }).Count -gt 0) {
                $constraints.Add(@{ schema = $table.schema; table = $table.name; definition = $constraint })
            }
        }
        foreach ($index in $model.indexes) {
            if ($index.table.schema -ceq $table.schema -and $index.table.name -ceq $table.name -and
                @($index.key_columns | Where-Object { $_ -cin $names }).Count -gt 0) {
                $indexes.Add($index)
            }
        }
    }
    foreach ($index in $model.indexes | Where-Object { $_.table.schema -ceq 'tracked_changes_edfi' -and $_.table.name -ceq 'Descriptor' }) {
        $indexes.Add($index)
    }
    if (@($indexes | Where-Object { $_.table.schema -ceq 'tracked_changes_edfi' -and $_.key_columns[0] -ceq 'ResourceKeyId' }).Count -eq 0) {
        throw 'Missing ResourceKeyId-routed descriptor history index inventory.'
    }
    $requiredColumns = @(
        @{ schema = 'dms'; table = 'Descriptor'; name = 'DescriptorId'; scalar_type = 'Int32'; is_nullable = $false },
        @{ schema = 'dms'; table = 'Descriptor'; name = 'DocumentId'; scalar_type = 'Int64'; is_nullable = $false },
        @{ schema = 'dms'; table = 'Descriptor'; name = 'ResourceKeyId'; scalar_type = 'Int16'; is_nullable = $false },
        @{ schema = 'tracked_changes_edfi'; table = 'Descriptor'; name = 'ResourceKeyId'; scalar_type = 'Int16'; is_nullable = $false }
    )
    foreach ($definition in @(
            @{ kind = 'Unique'; name = 'UX_Descriptor_DocumentId'; columns = @('DocumentId') },
            @{ kind = 'ForeignKey'; name = 'FK_Descriptor_Document'; columns = @('DocumentId'); target_table = @{ schema = 'dms'; name = 'Document' }; target_columns = @('DocumentId'); on_delete = 'Cascade'; on_update = 'NoAction' }
        )) { $constraints.Add(@{ schema = 'dms'; table = 'Descriptor'; definition = $definition }) }
    foreach ($index in @(
            @{ name = 'PK_Descriptor'; table = @{ schema = 'dms'; name = 'Descriptor' }; kind = 'PrimaryKey'; is_unique = $true; key_columns = @('DescriptorId') },
            @{ name = 'IX_Descriptor_ResourceKeyId_DocumentId'; table = @{ schema = 'dms'; name = 'Descriptor' }; kind = 'Explicit'; is_unique = $false; key_columns = @('ResourceKeyId', 'DocumentId') },
            @{ name = 'IX_Descriptor_ResourceKeyId_ContentVersion_DocumentId'; table = @{ schema = 'dms'; name = 'Descriptor' }; kind = 'Explicit'; is_unique = $false; key_columns = @('ResourceKeyId', 'ContentVersion', 'DocumentId') },
            @{ name = 'PK_tracked_changes_edfi_Descriptor'; table = @{ schema = 'tracked_changes_edfi'; name = 'Descriptor' }; kind = 'PrimaryKey'; is_unique = $true; key_columns = @('ChangeVersion') }
        )) { $indexes.Add($index) }
    $expectedResources = @($model.resources) + @($model.abstract_identity_tables | ForEach-Object { $_.resource })
    return @{ dialect = $Dialect; effective_schema_hash = $model.effective_schema_hash; projects = @($model.projects);
        resources = $expectedResources; columns = @($columns.ToArray()); required_columns = $requiredColumns;
        constraints = @($constraints.ToArray()); indexes = @($indexes.ToArray()) }
}

function Get-CompactDescriptorAssertionSql {
    <#
    .SYNOPSIS
        Renders reusable provider catalog assertions from the reviewed compact-descriptor baseline.
    .PARAMETER ExpectedModelManifestPath
        Complete implementation-generated relational-model manifest for the exact core/extension set.
    .PARAMETER Dialect
        Provider dialect of both the manifest and the database to inspect.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$ExpectedModelManifestPath,
        [Parameter(Mandatory)][ValidateSet('pgsql', 'mssql')][string]$Dialect
    )
    $inventory = Read-CompactDescriptorInventory -ExpectedModelManifestPath $ExpectedModelManifestPath -Dialect $Dialect
    # Keep JSON on short lines: sqlcmd's stdin parser truncates long physical lines.
    $json = ConvertTo-Json -InputObject $inventory -Depth 100
    if ($Dialect -ceq 'pgsql') {
        # A dollar-quoted DO body ends at its delimiter even inside an SQL string literal.
        # JSON's Unicode escape preserves dollar characters without exposing that delimiter.
        $json = $json.Replace('$', '\u0024')
    }
    $sql = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Assert-CompactDescriptor.$Dialect.sql") -Raw
    return $sql.Replace('__EXPECTED_INVENTORY__', $json.Replace("'", "''"))
}

Export-ModuleMember -Function Read-CompactDescriptorInventory, Get-CompactDescriptorAssertionSql
