# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Copies Northridge dataset tables from a published dump into a freshly provisioned database, and
    records the invariant checkpoints that prove the result stayed correct.

.DESCRIPTION
    Provisioning is create-only and there is no migration path for the DMS document store, so moving a
    published dataset onto a newer schema means provisioning a fresh database and copying the data in.
    Three things about that copy are easy to get wrong and are handled explicitly here.

    Triggers. The current schema carries more than a thousand triggers. A plain data-only restore fires
    the per-row projection stamping triggers, which rewrite ContentVersion and ContentLastModifiedAt on
    every copied row and burn the change-version sequence millions of times; fires the
    referential-identity triggers, which rewrite dms.ReferentialIdentity; and fires the statement-level
    document enqueue trigger, which floods dms.DocumentProjectionWork. The copy therefore runs with
    --disable-triggers, and because that also suppresses foreign-key enforcement, integrity is
    re-established afterwards by explicit assertions rather than by the loader.

    dms.Descriptor. The published dump predates dms.Descriptor.ResourceKeyId, which is NOT NULL with no
    default, so a plain restore of that table cannot succeed. Its rows are loaded into a staging schema
    and inserted with the value derived from dms.Document. The target schema is never altered to
    accommodate the load: a temporary nullability change that was not reverted perfectly would defeat
    the schema compare that follows.

    Provisioning-owned rows. dms.ResourceKey, dms.SchemaComponent, dms.EffectiveSchema,
    dms.DataStoreIdentity, dms.DocumentCacheState, dms.DocumentProjectionWork, and dms.DocumentCache
    are owned by provisioning. Copying any of them would produce a hand-edited fingerprint or a stale
    source identity, so the copy is driven by an explicit allow-list and an unexpected table in the
    dump is a failure rather than a silent inclusion.

    After the load, four things are asserted that a row-count reconciliation cannot see. Sequence
    positions, because a sequence left at its fresh-database value only surfaces as a collision on the
    first write after restore. Referential integrity, because --disable-triggers suppressed foreign-key
    enforcement during the load. Stamp distributions, because a trigger that fired would have rewritten
    ContentVersion in place and left the row count identical. And the fingerprint and singleton state,
    against a freshly provisioned reference database or explicit expected values.

    Checkpoint mode re-measures and re-asserts the same invariants later: DMS then starts, serves reads,
    and accepts writes, all of which can touch the objects the post-copy assertions just checked.

.PARAMETER Mode
    'Copy' loads the dataset and runs the post-copy assertions. 'Checkpoint' only measures and reports
    the invariants, for use after smoke, after writes, before the dump, and after a restore test.

.PARAMETER DumpPath
    Copy mode: path to the extracted custom-format dump, reachable from this host.

.PARAMETER SourceDatabase
    Copy mode: database holding the dump restored as-is, used as the row-count reference.

.PARAMETER TargetDatabase
    Freshly provisioned database to load, or the database to measure in checkpoint mode.

.PARAMETER CheckpointName
    Checkpoint mode: label recorded with the measurement, for example C2.

.PARAMETER ExpectedSourceIdentity
    Checkpoint mode: assert the source identity equals this value, proving the database was not
    re-provisioned underneath the run. Omit on the first checkpoint.

.PARAMETER ReferenceDatabase
    A freshly provisioned database to take the expected fingerprint and singleton cache state from.
    Preferred over the explicit expected values: it cannot go stale when the schema moves.

.PARAMETER ExpectedEffectiveSchemaHash
    Expected dms.EffectiveSchema.EffectiveSchemaHash, for checkpoints taken after the reference
    database has been dropped. Required together with the other two expected values when
    -ReferenceDatabase is not supplied.

.PARAMETER ExpectedResourceKeyCount
    Expected dms.EffectiveSchema.ResourceKeyCount.

.PARAMETER ExpectedResourceKeySeedHash
    Expected dms.EffectiveSchema.ResourceKeySeedHash, lowercase hex.

.PARAMETER OutputDirectory
    Directory for assertion and checkpoint records. Use a location outside the repository.

.PARAMETER Container
    Name of the running PostgreSQL container.

.PARAMETER PostgresUser
    PostgreSQL superuser. Superuser rights are required: --disable-triggers needs them.

.EXAMPLE
    ./Copy-NorthridgeDataForward.ps1 -Mode Copy -DumpPath /w/nr.dump -SourceDatabase northridge_source `
        -TargetDatabase northridge_target -OutputDirectory /tmp/nr -WhatIf

    Prints the allow-list and the plan without contacting a database.

.EXAMPLE
    ./Copy-NorthridgeDataForward.ps1 -Mode Checkpoint -TargetDatabase northridge_target `
        -CheckpointName C2 -OutputDirectory /tmp/nr
#>

[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = "Copy")]
param(
    [Parameter(ParameterSetName = "Copy")]
    [Parameter(ParameterSetName = "Checkpoint")]
    [ValidateSet("Copy", "Checkpoint")]
    [string]
    $Mode = "Copy",

    [Parameter(Mandatory, ParameterSetName = "Copy")]
    [string]
    $DumpPath,

    [Parameter(Mandatory, ParameterSetName = "Copy")]
    [string]
    $SourceDatabase,

    [Parameter(Mandatory, ParameterSetName = "Copy")]
    [Parameter(Mandatory, ParameterSetName = "Checkpoint")]
    [string]
    $TargetDatabase,

    [Parameter(Mandatory, ParameterSetName = "Checkpoint")]
    [string]
    $CheckpointName,

    [Parameter(ParameterSetName = "Checkpoint")]
    [string]
    $ExpectedSourceIdentity,

    [Parameter(ParameterSetName = "Copy")]
    [Parameter(ParameterSetName = "Checkpoint")]
    [string]
    $ReferenceDatabase,

    [Parameter(ParameterSetName = "Copy")]
    [Parameter(ParameterSetName = "Checkpoint")]
    [string]
    $ExpectedEffectiveSchemaHash,

    [Parameter(ParameterSetName = "Copy")]
    [Parameter(ParameterSetName = "Checkpoint")]
    [long]
    $ExpectedResourceKeyCount = 0,

    [Parameter(ParameterSetName = "Copy")]
    [Parameter(ParameterSetName = "Checkpoint")]
    [string]
    $ExpectedResourceKeySeedHash,

    [Parameter(Mandatory, ParameterSetName = "Copy")]
    [Parameter(Mandatory, ParameterSetName = "Checkpoint")]
    [string]
    $OutputDirectory,

    [string]
    $Container = "dms-postgresql",

    [string]
    $PostgresUser = "postgres"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Provisioning-owned. Copying any of these is what would produce a hand-edited fingerprint or a stale
# source identity, so they are named here and excluded by name, not by luck.
$script:ProvisioningOwnedTable = @(
    "ResourceKey",
    "SchemaComponent",
    "EffectiveSchema",
    "DataStoreIdentity",
    "DocumentCacheState",
    "DocumentProjectionWork",
    "DocumentCache"
)

# dms tables that carry dataset rows. Descriptor is copied separately because of its derived column.
$script:DmsDataTable = @("Document", "ReferentialIdentity")
$script:DmsDerivedTable = "Descriptor"
$script:BulkSchema = @("edfi", "tracked_changes_edfi", "auth")
$script:StagingSchema = "northridge_staging"

# Intentionally duplicated across the scripts in this directory rather than extracted into a shared
# module, to keep this directory to its reviewed file set.
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
        -v ON_ERROR_STOP=1 --no-align --tuples-only --quiet 2>&1

    if ($LASTEXITCODE -ne 0) {
        throw "psql failed against '$DatabaseName' (exit $LASTEXITCODE): $($output -join [Environment]::NewLine)"
    }

    return $output
}

function Get-ScalarValue {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string] $DatabaseName,
        [Parameter(Mandatory)] [string] $Sql
    )

    $rows = Invoke-PsqlQuery -ContainerName $Container -User $PostgresUser `
        -DatabaseName $DatabaseName -Sql $Sql

    foreach ($row in $rows) {
        $text = ([string]$row).Trim()
        if (-not [string]::IsNullOrWhiteSpace($text)) {
            return $text
        }
    }

    return ""
}

function Get-DataTableList {
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory)] [string] $DatabaseName)

    $schemaLiteral = ($script:BulkSchema | ForEach-Object { "'$_'" }) -join ", "

    $sql = @"
SELECT table_schema || '.' || table_name
FROM information_schema.tables
WHERE table_schema IN ($schemaLiteral) AND table_type = 'BASE TABLE'
ORDER BY table_schema, table_name;
"@

    $rows = Invoke-PsqlQuery -ContainerName $Container -User $PostgresUser `
        -DatabaseName $DatabaseName -Sql $sql

    return [string[]]@($rows | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
}

function Get-RowCountMap {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)] [string] $DatabaseName,
        [Parameter(Mandatory)] [string[]] $QualifiedTable
    )

    # One statement per run, built as a UNION ALL, so 610 tables cost one round trip instead of 610.
    $part = foreach ($table in $QualifiedTable) {
        $schemaName, $tableName = $table.Split(".", 2)
        $quoted = '"' + $schemaName.Replace('"', '""') + '"."' + $tableName.Replace('"', '""') + '"'
        "SELECT '$table' AS t, COUNT(*) AS c FROM $quoted"
    }

    $sql = ($part -join "`nUNION ALL`n") + "`nORDER BY t;"

    $rows = Invoke-PsqlQuery -ContainerName $Container -User $PostgresUser `
        -DatabaseName $DatabaseName -Sql $sql

    $map = @{}
    foreach ($row in $rows) {
        $text = ([string]$row).Trim()
        if ($text -match '^(?<name>\S+)\|(?<count>\d+)$') {
            $map[$Matches["name"]] = [long]$Matches["count"]
        }
    }

    return $map
}

function Measure-Invariant {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param([Parameter(Mandatory)] [string] $DatabaseName)

    return [ordered]@{
        OwnershipTokenNotNull  = [long](Get-ScalarValue -DatabaseName $DatabaseName -Sql `
                'SELECT COUNT(*) FROM dms."Document" WHERE "CreatedByOwnershipTokenId" IS NOT NULL;')
        ProjectionWorkRow      = [long](Get-ScalarValue -DatabaseName $DatabaseName -Sql `
                'SELECT COUNT(*) FROM dms."DocumentProjectionWork";')
        DocumentCacheRow       = [long](Get-ScalarValue -DatabaseName $DatabaseName -Sql `
                'SELECT COUNT(*) FROM dms."DocumentCache";')
        CacheStateLifecycle    = Get-ScalarValue -DatabaseName $DatabaseName -Sql `
            'SELECT "ProjectionLifecycleState" FROM dms."DocumentCacheState" WHERE "StateId" = 1;'
        CacheAheadRecovery     = Get-ScalarValue -DatabaseName $DatabaseName -Sql `
            'SELECT "CacheAheadRecoveryRequired"::text FROM dms."DocumentCacheState" WHERE "StateId" = 1;'
        SourceIdentity         = Get-ScalarValue -DatabaseName $DatabaseName -Sql `
            'SELECT "SourceIdentity"::text FROM dms."DataStoreIdentity" WHERE "DataStoreIdentitySingletonId" = 1;'
        EffectiveSchemaHash    = Get-ScalarValue -DatabaseName $DatabaseName -Sql `
            'SELECT "EffectiveSchemaHash" FROM dms."EffectiveSchema" WHERE "EffectiveSchemaSingletonId" = 1;'
        ResourceKeyCount       = [long](Get-ScalarValue -DatabaseName $DatabaseName -Sql `
                'SELECT "ResourceKeyCount" FROM dms."EffectiveSchema" WHERE "EffectiveSchemaSingletonId" = 1;')
        ResourceKeySeedHash    = Get-ScalarValue -DatabaseName $DatabaseName -Sql `
            'SELECT encode("ResourceKeySeedHash", ''hex'') FROM dms."EffectiveSchema" WHERE "EffectiveSchemaSingletonId" = 1;'
        DocumentRow            = [long](Get-ScalarValue -DatabaseName $DatabaseName -Sql `
                'SELECT COUNT(*) FROM dms."Document";')
        StagingSchemaPresent   = [long](Get-ScalarValue -DatabaseName $DatabaseName -Sql `
                "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = '$script:StagingSchema';")
    }
}

# Resolves the values the fingerprint and cache state must equal. Preferring a freshly provisioned
# reference database over literals keeps the check honest when the schema moves: a hard-coded hash
# would have to be edited by the same person whose change it is supposed to catch. The literals are
# only the fallback for checkpoints taken after the reference database has been dropped.
function Resolve-ExpectedInvariant {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [string] $ReferenceDatabaseName,
        [string] $EffectiveSchemaHash,
        [long] $ResourceKeyCount,
        [string] $ResourceKeySeedHash
    )

    if (-not [string]::IsNullOrWhiteSpace($ReferenceDatabaseName)) {
        return [ordered]@{
            Source              = "reference database '$ReferenceDatabaseName'"
            EffectiveSchemaHash = Get-ScalarValue -DatabaseName $ReferenceDatabaseName -Sql `
                'SELECT "EffectiveSchemaHash" FROM dms."EffectiveSchema" WHERE "EffectiveSchemaSingletonId" = 1;'
            ResourceKeyCount    = [long](Get-ScalarValue -DatabaseName $ReferenceDatabaseName -Sql `
                    'SELECT "ResourceKeyCount" FROM dms."EffectiveSchema" WHERE "EffectiveSchemaSingletonId" = 1;')
            ResourceKeySeedHash = Get-ScalarValue -DatabaseName $ReferenceDatabaseName -Sql `
                'SELECT encode("ResourceKeySeedHash", ''hex'') FROM dms."EffectiveSchema" WHERE "EffectiveSchemaSingletonId" = 1;'
            CacheStateLifecycle = Get-ScalarValue -DatabaseName $ReferenceDatabaseName -Sql `
                'SELECT "ProjectionLifecycleState" FROM dms."DocumentCacheState" WHERE "StateId" = 1;'
            CacheAheadRecovery  = Get-ScalarValue -DatabaseName $ReferenceDatabaseName -Sql `
                'SELECT "CacheAheadRecoveryRequired"::text FROM dms."DocumentCacheState" WHERE "StateId" = 1;'
        }
    }

    if ([string]::IsNullOrWhiteSpace($EffectiveSchemaHash) -or $ResourceKeyCount -le 0 -or
        [string]::IsNullOrWhiteSpace($ResourceKeySeedHash)) {
        throw "The fingerprint cannot be asserted. Pass -ReferenceDatabase, or all of -ExpectedEffectiveSchemaHash, -ExpectedResourceKeyCount and -ExpectedResourceKeySeedHash. A checkpoint that records the fingerprint without checking it would report success while the value is wrong."
    }

    return [ordered]@{
        Source              = "explicit expected values"
        EffectiveSchemaHash = $EffectiveSchemaHash
        ResourceKeyCount    = $ResourceKeyCount
        ResourceKeySeedHash = $ResourceKeySeedHash
        # Provisioning seeds these two literals and nothing in this workflow moves them.
        CacheStateLifecycle = "Disabled"
        CacheAheadRecovery  = "false"
    }
}

function Test-Invariant {
    [CmdletBinding()]
    [OutputType([System.Collections.Generic.List[string]])]
    param(
        [Parameter(Mandatory)] [System.Collections.Specialized.OrderedDictionary] $Measurement,
        [Parameter(Mandatory)] [System.Collections.Specialized.OrderedDictionary] $Expected,
        [string] $ExpectedIdentity
    )

    $failure = [System.Collections.Generic.List[string]]::new()

    # Every value Measure-Invariant records is checked here. A recorded-but-unchecked value would let
    # a checkpoint pass while part of the evidence table it produces is wrong.
    if ($Measurement.EffectiveSchemaHash -ne $Expected.EffectiveSchemaHash) {
        $failure.Add("dms.EffectiveSchema.EffectiveSchemaHash is '$($Measurement.EffectiveSchemaHash)', expected '$($Expected.EffectiveSchemaHash)'")
    }
    if ($Measurement.ResourceKeyCount -ne $Expected.ResourceKeyCount) {
        $failure.Add("dms.EffectiveSchema.ResourceKeyCount is $($Measurement.ResourceKeyCount), expected $($Expected.ResourceKeyCount)")
    }
    if ($Measurement.ResourceKeySeedHash -ne $Expected.ResourceKeySeedHash) {
        $failure.Add("dms.EffectiveSchema.ResourceKeySeedHash is '$($Measurement.ResourceKeySeedHash)', expected '$($Expected.ResourceKeySeedHash)'")
    }
    if ($Measurement.CacheAheadRecovery -ne $Expected.CacheAheadRecovery) {
        $failure.Add("dms.DocumentCacheState.CacheAheadRecoveryRequired is '$($Measurement.CacheAheadRecovery)', expected '$($Expected.CacheAheadRecovery)'")
    }
    if ($Measurement.CacheStateLifecycle -ne $Expected.CacheStateLifecycle) {
        $failure.Add("dms.DocumentCacheState.ProjectionLifecycleState is '$($Measurement.CacheStateLifecycle)', expected '$($Expected.CacheStateLifecycle)'")
    }

    if ($Measurement.OwnershipTokenNotNull -ne 0) {
        $failure.Add("dms.Document.CreatedByOwnershipTokenId is non-null in $($Measurement.OwnershipTokenNotNull) row(s); the dataset requires NULL throughout")
    }
    if ($Measurement.ProjectionWorkRow -ne 0) {
        $failure.Add("dms.DocumentProjectionWork holds $($Measurement.ProjectionWorkRow) row(s); the artifact ships this table empty")
    }
    if ($Measurement.DocumentCacheRow -ne 0) {
        $failure.Add("dms.DocumentCache holds $($Measurement.DocumentCacheRow) row(s); the artifact ships this table empty")
    }
    if ([string]::IsNullOrWhiteSpace($Measurement.SourceIdentity) -or
        $Measurement.SourceIdentity -eq "00000000-0000-0000-0000-000000000000") {
        $failure.Add("dms.DataStoreIdentity.SourceIdentity is missing or the zero UUID")
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedIdentity) -and
        $Measurement.SourceIdentity -ne $ExpectedIdentity) {
        $failure.Add("dms.DataStoreIdentity.SourceIdentity is '$($Measurement.SourceIdentity)', expected '$ExpectedIdentity'; the database may have been re-provisioned")
    }
    if ($Measurement.StagingSchemaPresent -ne 0) {
        $failure.Add("staging schema '$script:StagingSchema' still exists and would appear in the schema compare")
    }

    return $failure
}

# A sequence left at its fresh-database position is invisible to a row-count reconciliation and only
# surfaces on the first write after restore, as a primary-key or unique-constraint collision. The dump
# carries SEQUENCE SET entries, but "the loader probably applied them" is not evidence, so each
# sequence is compared against the maximum value actually present in the copied data.
function Test-SequencePosition {
    [CmdletBinding()]
    [OutputType([System.Collections.Generic.List[string]])]
    param([Parameter(Mandatory)] [string] $DatabaseName)

    $failure = [System.Collections.Generic.List[string]]::new()

    # dms.Document is the high-water mark for ChangeVersionSequence: it is the only table whose
    # ContentVersion/IdentityVersion default from that sequence, and the projection copies of
    # ContentVersion are stamped from the document row, so they cannot exceed it. dms.Descriptor is
    # included anyway because its stamping trigger draws from the same sequence.
    $sql = @'
SELECT 'ChangeVersionSequence|' ||
       COALESCE(pg_sequence_last_value('dms."ChangeVersionSequence"'::regclass), 0)::text || '|' ||
       GREATEST(
           COALESCE((SELECT MAX(GREATEST("ContentVersion", "IdentityVersion")) FROM dms."Document"), 0),
           COALESCE((SELECT MAX("ContentVersion") FROM dms."Descriptor"), 0)
       )::text
UNION ALL
SELECT 'DocumentIdentitySequence|' ||
       COALESCE(pg_sequence_last_value(
           pg_get_serial_sequence('dms."Document"', 'DocumentId')::regclass), 0)::text || '|' ||
       COALESCE((SELECT MAX("DocumentId") FROM dms."Document"), 0)::text;
'@

    $rows = Invoke-PsqlQuery -ContainerName $Container -User $PostgresUser `
        -DatabaseName $DatabaseName -Sql $sql

    # CollectionItemId is spread over every projection collection table, so the maximum has to be taken
    # across all of them rather than from one place.
    $collectionSql = @'
SELECT string_agg(
    format('SELECT COALESCE(MAX(%I), 0) AS v FROM %I.%I', column_name, table_schema, table_name),
    ' UNION ALL ')
FROM information_schema.columns
WHERE column_name = 'CollectionItemId' AND table_schema IN ('edfi', 'tracked_changes_edfi');
'@
    $collectionUnion = Get-ScalarValue -DatabaseName $DatabaseName -Sql $collectionSql

    if (-not [string]::IsNullOrWhiteSpace($collectionUnion)) {
        $maxSql = @"
SELECT 'CollectionItemIdSequence|' ||
       COALESCE(pg_sequence_last_value('dms."CollectionItemIdSequence"'::regclass), 0)::text || '|' ||
       (SELECT COALESCE(MAX(v), 0) FROM ($collectionUnion) AS m)::text;
"@
        $rows = @($rows) + @(Invoke-PsqlQuery -ContainerName $Container -User $PostgresUser `
                -DatabaseName $DatabaseName -Sql $maxSql)
    }
    else {
        $failure.Add("no CollectionItemId columns were found, so CollectionItemIdSequence could not be checked")
    }

    foreach ($row in $rows) {
        $text = ([string]$row).Trim()
        if ([string]::IsNullOrWhiteSpace($text)) { continue }

        $part = $text.Split("|")
        if ($part.Count -ne 3) { continue }

        $name = $part[0]
        $position = [long]$part[1]
        $maximum = [long]$part[2]

        # Information stream, not Write-Output: this function returns the failure list, and anything
        # written to the success stream would be returned alongside it and counted as a failure.
        Write-Information "  sequence: $name at $position, maximum value in data $maximum" -InformationAction Continue

        if ($position -lt $maximum) {
            $failure.Add("$name is at $position but the copied data reaches $maximum; the first write after restore would collide")
        }
    }

    return $failure
}

# --disable-triggers also suppresses foreign-key enforcement, so referential integrity is not a
# property the loader established. These are the three references that would break the dataset
# silently: a document with no resource key, a descriptor with no document, an identity with no owner.
function Test-ReferentialIntegrity {
    [CmdletBinding()]
    [OutputType([System.Collections.Generic.List[string]])]
    param([Parameter(Mandatory)] [string] $DatabaseName)

    $failure = [System.Collections.Generic.List[string]]::new()

    $check = [ordered]@{
        "Document -> ResourceKey"            = 'SELECT COUNT(*) FROM dms."Document" d LEFT JOIN dms."ResourceKey" rk ON rk."ResourceKeyId" = d."ResourceKeyId" WHERE rk."ResourceKeyId" IS NULL;'
        "Descriptor -> Document"             = 'SELECT COUNT(*) FROM dms."Descriptor" x LEFT JOIN dms."Document" d ON d."DocumentId" = x."DocumentId" WHERE d."DocumentId" IS NULL;'
        "ReferentialIdentity -> Document"    = 'SELECT COUNT(*) FROM dms."ReferentialIdentity" r LEFT JOIN dms."Document" d ON d."DocumentId" = r."DocumentId" WHERE d."DocumentId" IS NULL;'
    }

    foreach ($name in $check.Keys) {
        $orphan = [long](Get-ScalarValue -DatabaseName $DatabaseName -Sql $check[$name])
        Write-Information "  orphans $name : $orphan" -InformationAction Continue
        if ($orphan -ne 0) {
            $failure.Add("$orphan orphaned row(s) for $name; foreign keys were not enforced during the load")
        }
    }

    return $failure
}

# Row counts cannot see a rewritten stamp: the trigger that would corrupt these values updates rows in
# place, leaving the count identical. Comparing the distribution is what detects it.
function Get-StampDistribution {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)] [string] $DatabaseName,
        [Parameter(Mandatory)] [string[]] $SampleTable
    )

    $part = [System.Collections.Generic.List[string]]::new()
    $part.Add('SELECT ''dms.Document'' AS t, COUNT(*)::text || ''|'' ||
        COALESCE(MIN("ContentVersion")::text, ''-'') || ''|'' || COALESCE(MAX("ContentVersion")::text, ''-'') || ''|'' ||
        COALESCE(MIN("IdentityVersion")::text, ''-'') || ''|'' || COALESCE(MAX("IdentityVersion")::text, ''-'') || ''|'' ||
        COALESCE(MIN("ContentLastModifiedAt")::text, ''-'') || ''|'' || COALESCE(MAX("ContentLastModifiedAt")::text, ''-'') || ''|'' ||
        COALESCE(MIN("IdentityLastModifiedAt")::text, ''-'') || ''|'' || COALESCE(MAX("IdentityLastModifiedAt")::text, ''-'') || ''|'' ||
        COALESCE(MIN("CreatedAt")::text, ''-'') || ''|'' || COALESCE(MAX("CreatedAt")::text, ''-'') AS v
    FROM dms."Document"')

    foreach ($table in $SampleTable) {
        $schemaName, $tableName = $table.Split(".", 2)
        $quoted = '"' + $schemaName.Replace('"', '""') + '"."' + $tableName.Replace('"', '""') + '"'
        $part.Add("SELECT '$table' AS t, COUNT(*)::text || '|' ||
            COALESCE(MIN(""ContentVersion"")::text, '-') || '|' || COALESCE(MAX(""ContentVersion"")::text, '-') || '|' ||
            COALESCE(MIN(""ContentLastModifiedAt"")::text, '-') || '|' || COALESCE(MAX(""ContentLastModifiedAt"")::text, '-') AS v
        FROM $quoted")
    }

    $sql = ($part -join "`nUNION ALL`n") + "`nORDER BY t;"

    $rows = Invoke-PsqlQuery -ContainerName $Container -User $PostgresUser `
        -DatabaseName $DatabaseName -Sql $sql

    $map = @{}
    foreach ($row in $rows) {
        $text = ([string]$row).Trim()
        $separatorIndex = $text.IndexOf("|")
        if ($separatorIndex -gt 0) {
            $map[$text.Substring(0, $separatorIndex)] = $text.Substring($separatorIndex + 1)
        }
    }

    return $map
}

function Save-Record {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Content
    )

    $parent = Split-Path -Path $Path -Parent
    if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent)) {
        New-Item -Path $parent -ItemType Directory -Force | Out-Null
    }

    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

# ---------------- Checkpoint mode ----------------

if ($Mode -eq "Checkpoint") {

    Write-Output "Checkpoint '$CheckpointName' on database '$TargetDatabase'"

    if (-not $PSCmdlet.ShouldProcess($TargetDatabase, "Measure invariants for checkpoint $CheckpointName")) {
        Write-Output "WhatIf: no database was contacted."
        return
    }

    $expected = Resolve-ExpectedInvariant -ReferenceDatabaseName $ReferenceDatabase `
        -EffectiveSchemaHash $ExpectedEffectiveSchemaHash `
        -ResourceKeyCount $ExpectedResourceKeyCount `
        -ResourceKeySeedHash $ExpectedResourceKeySeedHash

    Write-Output "Expected values from: $($expected.Source)"

    $measurement = Measure-Invariant -DatabaseName $TargetDatabase

    foreach ($key in $measurement.Keys) {
        Write-Output ("  {0,-22}: {1}" -f $key, $measurement[$key])
    }

    Write-Output ""
    $sequenceFailure = Test-SequencePosition -DatabaseName $TargetDatabase

    $line = foreach ($key in $measurement.Keys) { "$key=$($measurement[$key])" }
    Save-Record -Path (Join-Path $OutputDirectory "checkpoint.$CheckpointName.$TargetDatabase.txt") `
        -Content ((($line) -join "`n") + "`n")

    $failure = Test-Invariant -Measurement $measurement -Expected $expected `
        -ExpectedIdentity $ExpectedSourceIdentity
    foreach ($item in $sequenceFailure) { $failure.Add($item) }

    Write-Output ""
    if ($failure.Count -eq 0) {
        Write-Output "PASS: checkpoint $CheckpointName -- every invariant at its expected value."
        return
    }

    foreach ($item in $failure) { Write-Output "FAIL: $item" }
    throw "Checkpoint $CheckpointName failed: $($failure -join '; ')"
}

# ---------------- Copy mode ----------------

Write-Output "Copy plan"
Write-Output "  dump              : $DumpPath"
Write-Output "  source database   : $SourceDatabase"
Write-Output "  target database   : $TargetDatabase"
Write-Output "  bulk schemas      : $($script:BulkSchema -join ', ')"
Write-Output "  dms tables copied : $($script:DmsDataTable -join ', ') and $script:DmsDerivedTable (derived)"
Write-Output "  never copied      : $($script:ProvisioningOwnedTable -join ', ')"
Write-Output "  trigger handling  : pg_restore --data-only --disable-triggers (requires superuser)"
Write-Output "  derived column    : dms.Descriptor.ResourceKeyId from dms.Document via $script:StagingSchema"
Write-Output "  post-copy checks  : row counts both directions, sequence positions, referential integrity, stamp distributions, checkpoint C1"

if (-not $PSCmdlet.ShouldProcess($TargetDatabase, "Copy Northridge dataset from $DumpPath")) {
    Write-Output ""
    Write-Output "WhatIf: no database was contacted and no data was copied."
    return
}

if (-not (Test-Path -LiteralPath $DumpPath)) {
    throw "Dump not found: $DumpPath"
}

# Guard: the target must be freshly provisioned. Copying into a populated database would silently
# double rows, and the row-count reconciliation would then be comparing against a moving target.
$targetDocumentRow = [long](Get-ScalarValue -DatabaseName $TargetDatabase -Sql 'SELECT COUNT(*) FROM dms."Document";')
if ($targetDocumentRow -ne 0) {
    throw "Target '$TargetDatabase' already holds $targetDocumentRow document(s). Provision a fresh database."
}

# Guard: the resource-key seed must be identical, or every copied ResourceKeyId means something
# different in the target than it did in the source.
$sourceSeed = Get-ScalarValue -DatabaseName $SourceDatabase -Sql `
    'SELECT "ResourceKeyCount"::text || ''|'' || encode("ResourceKeySeedHash", ''hex'') FROM dms."EffectiveSchema";'
$targetSeed = Get-ScalarValue -DatabaseName $TargetDatabase -Sql `
    'SELECT "ResourceKeyCount"::text || ''|'' || encode("ResourceKeySeedHash", ''hex'') FROM dms."EffectiveSchema";'

if ($sourceSeed -ne $targetSeed) {
    throw "Resource-key seed differs. Source '$sourceSeed' target '$targetSeed'. ResourceKeyId values are ordinal, so copying across a changed seed would mis-attribute every document."
}
Write-Output ""
Write-Output "Resource-key seed matches source and target: $targetSeed"

$bulkTable = Get-DataTableList -DatabaseName $SourceDatabase
Write-Output "Bulk data tables discovered in source: $($bulkTable.Count)"

$restoreArgument = @(
    "--data-only", "--disable-triggers", "--no-owner", "--no-privileges",
    "-U", $PostgresUser, "-d", $TargetDatabase
)

foreach ($table in $script:DmsDataTable) {
    $restoreArgument += @("-n", "dms", "-t", $table)
}
foreach ($table in $bulkTable) {
    $schemaName, $tableName = $table.Split(".", 2)
    $restoreArgument += @("-n", $schemaName, "-t", $tableName)
}

Write-Output "Restoring $($script:DmsDataTable.Count + $bulkTable.Count) table(s) with triggers disabled..."

$containerDumpPath = "/tmp/northridge-dataforward.dump"
docker cp $DumpPath "${Container}:${containerDumpPath}"
if ($LASTEXITCODE -ne 0) { throw "docker cp of the dump into '$Container' failed." }

try {
    docker exec $Container pg_restore @restoreArgument $containerDumpPath
    if ($LASTEXITCODE -ne 0) { throw "pg_restore reported exit $LASTEXITCODE." }
}
finally {
    docker exec -u 0 $Container rm -f $containerDumpPath | Out-Null
}

Write-Output "Deriving dms.Descriptor.ResourceKeyId via staging schema '$script:StagingSchema'..."

# Loading Descriptor into staging and inserting with the derived value keeps the target schema
# untouched. Altering the real table's nullability and reverting it would risk defeating the schema
# compare that follows.
$stagingSetup = @"
DROP SCHEMA IF EXISTS "$script:StagingSchema" CASCADE;
CREATE SCHEMA "$script:StagingSchema";
CREATE TABLE "$script:StagingSchema"."Descriptor" AS
SELECT * FROM dms."Descriptor" WITH NO DATA;
ALTER TABLE "$script:StagingSchema"."Descriptor" DROP COLUMN "ResourceKeyId";
SELECT 'staging ready';
"@
Invoke-PsqlQuery -ContainerName $Container -User $PostgresUser -DatabaseName $TargetDatabase -Sql $stagingSetup | Out-Null

docker cp $DumpPath "${Container}:${containerDumpPath}"
if ($LASTEXITCODE -ne 0) { throw "docker cp of the dump for the Descriptor load failed." }

try {
    # Restored into the staging schema by rewriting the search_path, so the dump's unqualified COPY
    # lands on the staging copy rather than the real table.
    $descriptorSql = docker exec $Container pg_restore --data-only --no-owner --no-privileges `
        -n dms -t $script:DmsDerivedTable -f - $containerDumpPath
    if ($LASTEXITCODE -ne 0) { throw "pg_restore of dms.Descriptor to text reported exit $LASTEXITCODE." }

    $redirected = ($descriptorSql -join "`n").Replace('dms."Descriptor"', """$script:StagingSchema"".""Descriptor""")
    $redirected | docker exec -i $Container psql -U $PostgresUser -d $TargetDatabase -v ON_ERROR_STOP=1 --quiet
    if ($LASTEXITCODE -ne 0) { throw "loading dms.Descriptor into staging reported exit $LASTEXITCODE." }
}
finally {
    docker exec -u 0 $Container rm -f $containerDumpPath | Out-Null
}

$deriveSql = @"
SET session_replication_role = replica;
INSERT INTO dms."Descriptor" ("DocumentId", "ResourceKeyId", "Namespace", "CodeValue", "ShortDescription",
    "Description", "EffectiveBeginDate", "EffectiveEndDate", "Discriminator", "Uri", "ContentVersion",
    "ContentLastModifiedAt")
SELECT s."DocumentId", d."ResourceKeyId", s."Namespace", s."CodeValue", s."ShortDescription",
    s."Description", s."EffectiveBeginDate", s."EffectiveEndDate", s."Discriminator", s."Uri",
    s."ContentVersion", s."ContentLastModifiedAt"
FROM "$script:StagingSchema"."Descriptor" s
JOIN dms."Document" d ON d."DocumentId" = s."DocumentId";
SET session_replication_role = DEFAULT;
SELECT 'descriptor derived';
"@
Invoke-PsqlQuery -ContainerName $Container -User $PostgresUser -DatabaseName $TargetDatabase -Sql $deriveSql | Out-Null

$stagingCount = [long](Get-ScalarValue -DatabaseName $TargetDatabase -Sql `
        "SELECT COUNT(*) FROM ""$script:StagingSchema"".""Descriptor"";")
$derivedCount = [long](Get-ScalarValue -DatabaseName $TargetDatabase -Sql 'SELECT COUNT(*) FROM dms."Descriptor";')
$mismatchCount = [long](Get-ScalarValue -DatabaseName $TargetDatabase -Sql @'
SELECT COUNT(*) FROM dms."Descriptor" x
JOIN dms."Document" d ON d."DocumentId" = x."DocumentId"
WHERE x."ResourceKeyId" <> d."ResourceKeyId";
'@)

Write-Output "  staging rows $stagingCount -> dms.Descriptor rows $derivedCount, disagreeing ResourceKeyId $mismatchCount"

if ($stagingCount -ne $derivedCount -or $mismatchCount -ne 0) {
    throw "Descriptor derivation failed: staging=$stagingCount derived=$derivedCount mismatch=$mismatchCount."
}

Invoke-PsqlQuery -ContainerName $Container -User $PostgresUser -DatabaseName $TargetDatabase `
    -Sql "DROP SCHEMA ""$script:StagingSchema"" CASCADE; SELECT 'staging dropped';" | Out-Null
Write-Output "  staging schema dropped"

Write-Output ""
Write-Output "Reconciling row counts, both directions..."

$allTable = @($script:DmsDataTable | ForEach-Object { "dms.$_" }) + @("dms.$script:DmsDerivedTable") + $bulkTable
$sourceCount = Get-RowCountMap -DatabaseName $SourceDatabase -QualifiedTable $allTable
$targetCount = Get-RowCountMap -DatabaseName $TargetDatabase -QualifiedTable $allTable

$countFailure = [System.Collections.Generic.List[string]]::new()
foreach ($table in ($sourceCount.Keys + $targetCount.Keys | Sort-Object -Unique)) {
    $inSource = $sourceCount.ContainsKey($table)
    $inTarget = $targetCount.ContainsKey($table)

    if (-not $inTarget) { $countFailure.Add("$table missing from target"); continue }
    if (-not $inSource) { $countFailure.Add("$table missing from source"); continue }
    if ($sourceCount[$table] -ne $targetCount[$table]) {
        $countFailure.Add("$table source=$($sourceCount[$table]) target=$($targetCount[$table])")
    }
}

Write-Output "  tables compared: $($allTable.Count)"
Write-Output "  count differences: $($countFailure.Count)"

Write-Output ""
Write-Output "Checking sequence positions..."
$sequenceFailure = Test-SequencePosition -DatabaseName $TargetDatabase

Write-Output ""
Write-Output "Checking referential integrity..."
$integrityFailure = Test-ReferentialIntegrity -DatabaseName $TargetDatabase

Write-Output ""
Write-Output "Comparing stamp distributions..."

# The ten largest projection tables. A stamping trigger that fired during the load would have rewritten
# ContentVersion and ContentLastModifiedAt in place, which no row count can see.
$sampleTable = @(
    $targetCount.GetEnumerator() |
    Where-Object { $_.Key.StartsWith("edfi.", [System.StringComparison]::Ordinal) -and $_.Value -gt 0 } |
    Sort-Object -Property Value -Descending |
    Select-Object -First 10 -ExpandProperty Key
)
Write-Output "  sampled projection tables: $($sampleTable.Count)"

$sourceStamp = Get-StampDistribution -DatabaseName $SourceDatabase -SampleTable $sampleTable
$targetStamp = Get-StampDistribution -DatabaseName $TargetDatabase -SampleTable $sampleTable

$stampFailure = [System.Collections.Generic.List[string]]::new()
foreach ($name in ($sourceStamp.Keys + $targetStamp.Keys | Sort-Object -Unique)) {
    $sourceValue = if ($sourceStamp.ContainsKey($name)) { $sourceStamp[$name] } else { "absent" }
    $targetValue = if ($targetStamp.ContainsKey($name)) { $targetStamp[$name] } else { "absent" }

    if ($sourceValue -ne $targetValue) {
        $stampFailure.Add("$name stamp distribution differs: source=$sourceValue target=$targetValue")
    }
}
Write-Output "  stamp distributions compared: $(($sourceStamp.Keys + $targetStamp.Keys | Sort-Object -Unique).Count), differing: $($stampFailure.Count)"

$record = [System.Collections.Generic.List[string]]::new()
foreach ($table in ($allTable | Sort-Object)) {
    $sourceValue = if ($sourceCount.ContainsKey($table)) { $sourceCount[$table] } else { "absent" }
    $targetValue = if ($targetCount.ContainsKey($table)) { $targetCount[$table] } else { "absent" }
    $record.Add("$table`t$sourceValue`t$targetValue")
}
Save-Record -Path (Join-Path $OutputDirectory "rowcount.$SourceDatabase-vs-$TargetDatabase.tsv") `
    -Content ((($record) -join "`n") + "`n")

Write-Output ""
Write-Output "Measuring checkpoint C1..."

$expected = Resolve-ExpectedInvariant -ReferenceDatabaseName $ReferenceDatabase `
    -EffectiveSchemaHash $ExpectedEffectiveSchemaHash `
    -ResourceKeyCount $ExpectedResourceKeyCount `
    -ResourceKeySeedHash $ExpectedResourceKeySeedHash

Write-Output "Expected values from: $($expected.Source)"

$measurement = Measure-Invariant -DatabaseName $TargetDatabase
foreach ($key in $measurement.Keys) {
    Write-Output ("  {0,-22}: {1}" -f $key, $measurement[$key])
}

$line = foreach ($key in $measurement.Keys) { "$key=$($measurement[$key])" }
Save-Record -Path (Join-Path $OutputDirectory "checkpoint.C1.$TargetDatabase.txt") `
    -Content ((($line) -join "`n") + "`n")

# Every failure from every check is collected before anything throws, so one run reports the whole
# picture instead of stopping at the first problem and hiding the rest.
$invariantFailure = Test-Invariant -Measurement $measurement -Expected $expected

$allFailure = [System.Collections.Generic.List[string]]::new()
foreach ($item in $countFailure) { $allFailure.Add("row count: $item") }
foreach ($item in $sequenceFailure) { $allFailure.Add("sequence: $item") }
foreach ($item in $integrityFailure) { $allFailure.Add("integrity: $item") }
foreach ($item in $stampFailure) { $allFailure.Add("stamp: $item") }
foreach ($item in $invariantFailure) { $allFailure.Add("invariant: $item") }

Write-Output ""
if ($allFailure.Count -gt 0) {
    $allFailure | Select-Object -First 40 | ForEach-Object { Write-Output "FAIL: $_" }
    if ($allFailure.Count -gt 40) {
        Write-Output "  ... $($allFailure.Count - 40) more"
    }
    throw "Post-copy validation failed with $($allFailure.Count) problem(s)."
}

Write-Output "PASS: copy complete. Row counts reconcile both directions, sequences are beyond the copied maxima, no orphaned references, stamp distributions unchanged, checkpoint C1 clean."
Write-Output "Record the source identity '$($measurement.SourceIdentity)' and pass it as -ExpectedSourceIdentity to later checkpoints."
