# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Compact-Descriptor.psm1')

function Get-TemplateSchemaAssertionSql {
    <#
    .SYNOPSIS
        Renders timestamp and compact-descriptor assertions from the reviewed exact-schema manifest.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$ExpectedModelManifestPath,
        [Parameter(Mandatory)][ValidateSet('pgsql', 'mssql')][string]$Dialect
    )

    # The sole stored-reference inventory reader also validates complete resource_details,
    # provider, fingerprint and the implementation's compact-descriptor baseline.
    $compactSql = Get-CompactDescriptorAssertionSql -ExpectedModelManifestPath $ExpectedModelManifestPath -Dialect $Dialect
    $model = Get-Content -LiteralPath $ExpectedModelManifestPath -Raw | ConvertFrom-Json -AsHashtable
    $owners = [System.Collections.Generic.List[object]]::new()
    # Descriptor stamps are core DDL columns, not included in shared_descriptor_table.columns.
    $owners.Add(@{ schema = 'dms'; table = 'Descriptor' })
    foreach ($detail in $model.resource_details | Where-Object { $_.storage_kind -ceq 'RelationalTables' }) {
        $key = "$($detail.resource.project_name)/$($detail.resource.resource_name)"
        $roots = @($detail.tables | Where-Object { $_.scope -ceq '$' })
        if ($roots.Count -ne 1) { throw "Timestamp baseline: expected exactly one root for $key." }
        $root = $roots[0]
        $stamp = @($root.columns | Where-Object { $_.name -ceq 'ContentLastModifiedAt' })
        if ($stamp.Count -ne 1 -or $stamp[0].kind -cne 'AuthoritativeContentLastModifiedAt' -or
            $stamp[0].type.kind -cne 'DateTime' -or $stamp[0].is_nullable -or $stamp[0].storage.kind -cne 'Stored') {
            throw "Timestamp baseline: missing authoritative stored timestamp for $key ($($root.schema).$($root.name))."
        }
        $owners.Add(@{ schema = $root.schema; table = $root.name })
    }
    # Shared descriptor types intentionally map to one owner. Extension resources are
    # selected by root scope just like core resources; extension child tables are excluded.
    $owners = @($owners | Sort-Object -Property schema, table -Unique -CaseSensitive)
    $json = ConvertTo-Json -InputObject $owners -Depth 100
    if ($Dialect -ceq 'pgsql') { $json = $json.Replace('$', '\u0024') }
    $timestampSql = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Assert-AuthoritativeTimestamp.$Dialect.sql") -Raw
    return $timestampSql.Replace('__EXPECTED_OWNERS__', $json.Replace("'", "''")) + "`n" + $compactSql
}

Export-ModuleMember -Function Get-TemplateSchemaAssertionSql
