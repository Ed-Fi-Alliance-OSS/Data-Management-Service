# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Captures a normalized catalog snapshot of a DMS PostgreSQL database, and diffs two snapshots.

.DESCRIPTION
    Provisioning is create-only, so the only way to prove that a data-copied database matches the
    schema a fresh deployment produces is to compare their catalogs. This script captures one
    deterministic text snapshot per database and diffs two of them; an empty diff is the pass.

    The snapshot is scoped to the DMS-owned schemas. dmscs and its deploy journal are owned by the
    Configuration Service, which has its own versioned migration path, and are validated separately --
    diffing them against a DMS-only reference database would report differences that mean nothing.

    Ordering is explicit in every query and type spellings come from the catalog rather than from DDL
    text, so two runs against the same database produce byte-identical output and a textual diff is
    meaningful.

.PARAMETER Database
    Database to snapshot. Repeat the parameter, or pass two values, to snapshot and then diff both.

.PARAMETER OutputDirectory
    Directory for the snapshot files. Use a location outside the repository: snapshots are generated
    artifacts and must never be committed.

.PARAMETER Container
    Name of the running PostgreSQL container.

.PARAMETER PostgresUser
    PostgreSQL user for psql.

.PARAMETER Schema
    Schemas to include. Defaults to the DMS-owned set.

.EXAMPLE
    ./Compare-DmsSchemaSnapshot.ps1 -Database northridge_target, northridge_reference -OutputDirectory /tmp/nr

    Captures both snapshots and reports whether they differ.

.EXAMPLE
    ./Compare-DmsSchemaSnapshot.ps1 -Database northridge_target -OutputDirectory /tmp/nr

    Captures a single snapshot without diffing.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateCount(1, 2)]
    [string[]]
    $Database,

    [Parameter(Mandatory)]
    [string]
    $OutputDirectory,

    [string]
    $Container = "dms-postgresql",

    [string]
    $PostgresUser = "postgres",

    [string[]]
    $Schema = @("dms", "edfi", "tracked_changes_edfi", "auth")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Intentionally duplicated across the scripts in this directory rather than extracted into a shared
# module, to keep this directory to its reviewed file set. It is the whole of the database access
# surface: everything else composes SQL text.
function Invoke-PsqlQuery {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)] [string] $ContainerName,
        [Parameter(Mandatory)] [string] $User,
        [Parameter(Mandatory)] [string] $DatabaseName,
        [Parameter(Mandatory)] [string] $Sql
    )

    $output = $Sql | docker exec -i $ContainerName psql -U $User -d $DatabaseName `
        -v ON_ERROR_STOP=1 --no-align --tuples-only --field-separator='|' --quiet 2>&1

    if ($LASTEXITCODE -ne 0) {
        throw "psql failed against '$DatabaseName' (exit $LASTEXITCODE): $($output -join [Environment]::NewLine)"
    }

    return $output
}

function Get-SchemaLiteralList {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [string[]] $Name)

    return ($Name | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ", "
}

# Each query is prefixed with a stable section label so the diff points at a kind of object rather
# than a line number, and every query carries a total ORDER BY so output is deterministic.
function Get-SnapshotQueryMap {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param([Parameter(Mandatory)] [string] $SchemaList)

    return [ordered]@{
        "01-schema"     = @"
SELECT 'schema|' || nspname
FROM pg_namespace
WHERE nspname IN ($SchemaList)
ORDER BY nspname;
"@

        "02-table"      = @"
SELECT 'table|' || n.nspname || '.' || c.relname || '|' || c.relkind || '|persistence=' || c.relpersistence
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname IN ($SchemaList) AND c.relkind IN ('r','p','v','m','f')
ORDER BY n.nspname, c.relname;
"@

        "03-column"     = @"
SELECT 'column|' || table_schema || '.' || table_name || '|' || ordinal_position || '|' || column_name
    || '|' || data_type
    || '|len=' || COALESCE(character_maximum_length::text, '')
    || '|num=' || COALESCE(numeric_precision::text, '') || ',' || COALESCE(numeric_scale::text, '')
    || '|null=' || is_nullable
    || '|default=' || COALESCE(column_default, '')
    || '|identity=' || COALESCE(is_identity, 'NO') || ',' || COALESCE(identity_generation, '')
FROM information_schema.columns
WHERE table_schema IN ($SchemaList)
ORDER BY table_schema, table_name, ordinal_position;
"@

        "04-constraint" = @"
SELECT 'constraint|' || n.nspname || '.' || rel.relname || '|' || con.conname || '|' || con.contype
    || '|' || pg_get_constraintdef(con.oid)
FROM pg_constraint con
JOIN pg_class rel ON rel.oid = con.conrelid
JOIN pg_namespace n ON n.oid = rel.relnamespace
WHERE n.nspname IN ($SchemaList)
ORDER BY n.nspname, rel.relname, con.conname;
"@

        "05-index"      = @"
SELECT 'index|' || schemaname || '.' || tablename || '|' || indexname || '|' || indexdef
FROM pg_indexes
WHERE schemaname IN ($SchemaList)
ORDER BY schemaname, tablename, indexname;
"@

        "06-sequence"   = @"
SELECT 'sequence|' || schemaname || '.' || sequencename || '|type=' || data_type
    || '|start=' || COALESCE(start_value::text, '') || '|inc=' || COALESCE(increment_by::text, '')
FROM pg_sequences
WHERE schemaname IN ($SchemaList)
ORDER BY schemaname, sequencename;
"@

        "07-trigger"    = @"
SELECT 'trigger|' || n.nspname || '.' || rel.relname || '|' || tg.tgname
    || '|' || pg_get_triggerdef(tg.oid)
FROM pg_trigger tg
JOIN pg_class rel ON rel.oid = tg.tgrelid
JOIN pg_namespace n ON n.oid = rel.relnamespace
WHERE n.nspname IN ($SchemaList) AND NOT tg.tgisinternal
ORDER BY n.nspname, rel.relname, tg.tgname;
"@

        "08-routine"    = @"
SELECT 'routine|' || n.nspname || '.' || p.proname || '|' || pg_get_function_identity_arguments(p.oid)
    || '|' || md5(pg_get_functiondef(p.oid))
FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname IN ($SchemaList)
ORDER BY n.nspname, p.proname, pg_get_function_identity_arguments(p.oid);
"@

        "09-view"       = @"
SELECT 'view|' || schemaname || '.' || viewname || '|' || md5(definition)
FROM pg_views
WHERE schemaname IN ($SchemaList)
ORDER BY schemaname, viewname;
"@

        "10-grant"      = @"
SELECT 'grant|' || table_schema || '.' || table_name || '|' || grantee || '|' || privilege_type
FROM information_schema.role_table_grants
WHERE table_schema IN ($SchemaList)
ORDER BY table_schema, table_name, grantee, privilege_type;
"@

        # The fingerprint rows are part of the schema contract, not incidental data: a hand-edited
        # EffectiveSchema row is exactly the failure the runtime 503 check exists to catch.
        "11-fingerprint" = @"
SELECT 'fingerprint|' || "ApiSchemaFormatVersion" || '|' || "EffectiveSchemaHash"
    || '|' || "ResourceKeyCount" || '|' || encode("ResourceKeySeedHash", 'hex')
FROM dms."EffectiveSchema"
ORDER BY "EffectiveSchemaSingletonId";
"@

        "12-component"  = @"
SELECT 'component|' || "EffectiveSchemaHash" || '|' || "ProjectEndpointName" || '|' || "ProjectName"
    || '|' || "ProjectVersion" || '|' || "IsExtensionProject"
FROM dms."SchemaComponent"
ORDER BY "ProjectEndpointName", "ProjectName";
"@
    }
}

function Export-SchemaSnapshot {
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)] [string] $DatabaseName,
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [System.Collections.Specialized.OrderedDictionary] $QueryMap,
        [Parameter(Mandatory)] [string] $ContainerName,
        [Parameter(Mandatory)] [string] $User
    )

    $lines = [System.Collections.Generic.List[string]]::new()

    foreach ($section in $QueryMap.Keys) {
        Write-Verbose "Snapshotting $DatabaseName section $section"
        $rows = Invoke-PsqlQuery -ContainerName $ContainerName -User $User `
            -DatabaseName $DatabaseName -Sql $QueryMap[$section]

        foreach ($row in $rows) {
            $text = [string]$row
            if (-not [string]::IsNullOrWhiteSpace($text)) {
                $lines.Add("$section|$($text.Trim())")
            }
        }
    }

    # LF endings and no BOM, so the file is byte-comparable across platforms.
    $content = ($lines -join "`n") + "`n"
    [System.IO.File]::WriteAllText($Path, $content, [System.Text.UTF8Encoding]::new($false))

    return $lines.Count
}

$schemaList = Get-SchemaLiteralList -Name $Schema
$queryMap = Get-SnapshotQueryMap -SchemaList $schemaList

Write-Output "Schemas in scope: $($Schema -join ', ')"
Write-Output "Sections: $($queryMap.Keys -join ', ')"
Write-Output "Databases: $($Database -join ', ')"
Write-Output "Output directory: $OutputDirectory"

if (-not $PSCmdlet.ShouldProcess($OutputDirectory, "Capture schema snapshot for $($Database -join ' and ')")) {
    Write-Output "WhatIf: no database was contacted and no file was written."
    return
}

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -Path $OutputDirectory -ItemType Directory -Force | Out-Null
}

$snapshotPath = @{}

foreach ($databaseName in $Database) {
    $path = Join-Path -Path $OutputDirectory -ChildPath "schema-snapshot.$databaseName.txt"
    $rowCount = Export-SchemaSnapshot -DatabaseName $databaseName -Path $path -QueryMap $queryMap `
        -ContainerName $Container -User $PostgresUser
    $snapshotPath[$databaseName] = $path
    Write-Output "Captured $rowCount rows for '$databaseName' -> $path"
}

if ($Database.Count -eq 1) {
    Write-Output "Single database requested; nothing to diff."
    return
}

$leftName, $rightName = $Database
$leftLine = Get-Content -LiteralPath $snapshotPath[$leftName]
$rightLine = Get-Content -LiteralPath $snapshotPath[$rightName]

$difference = Compare-Object -ReferenceObject $leftLine -DifferenceObject $rightLine

if ($null -eq $difference) {
    Write-Output ""
    Write-Output "PASS: schema snapshots for '$leftName' and '$rightName' are identical across $($Schema -join ', ')."
    return
}

$diffPath = Join-Path -Path $OutputDirectory -ChildPath "schema-diff.$leftName-vs-$rightName.txt"
$diffText = $difference | ForEach-Object {
    $marker = if ($_.SideIndicator -eq "<=") { "only-in-$leftName" } else { "only-in-$rightName" }
    "$marker`t$($_.InputObject)"
}
[System.IO.File]::WriteAllText($diffPath, (($diffText -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))

Write-Output ""
Write-Output "FAIL: $($difference.Count) schema differences between '$leftName' and '$rightName'."
Write-Output "Diff written to $diffPath"
$diffText | Select-Object -First 40 | ForEach-Object { Write-Output "  $_" }
if ($difference.Count -gt 40) {
    Write-Output "  ... $($difference.Count - 40) more; see $diffPath"
}

throw "Schema compare failed with $($difference.Count) differences."
