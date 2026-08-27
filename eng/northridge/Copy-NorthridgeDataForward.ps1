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
    source identity, so the copy selects individual TABLE DATA entries from the archive table of
    contents. Schema and table name filters cannot express that: pg_restore ORs -n against -n and -t
    against -t, then ANDs the two groups, so naming a table that exists in more than one selected
    schema pulls in every copy of it. tracked_changes_edfi.Descriptor and dms.Descriptor share a name,
    which is exactly how dms.Descriptor reached a load that was supposed to exclude it. Those lists
    stand in for discovery in the dms schema, so before the load they are held to the catalog of both
    databases: every dms base table in either must be on exactly one of them, or the run stops.

    After the load, four things are asserted that a row-count reconciliation cannot see. Sequence
    positions, because a sequence left at its fresh-database value only surfaces as a collision on the
    first write after restore. Referential integrity, because --disable-triggers suppressed foreign-key
    enforcement during the load: every foreign key declared in the DMS-owned schemas is validated
    against the data from the catalog, and no trigger may be left disabled, since a declared constraint
    whose enforcement trigger is still off is not a constraint. Stamp distributions, because a trigger
    that fired would have rewritten ContentVersion in place and left the row count identical. And the
    fingerprint, document count and singleton state, against a freshly provisioned reference database or
    explicit expected values.

    Checkpoint mode re-measures and re-asserts the Northridge-wide checkpoint invariants later:
    fingerprint and singleton state, document count, cache/projection emptiness, source identity,
    staging cleanup, and sequence positions. Copy-specific checks that need the source database
    -- row-count reconciliation, referential-integrity sweep, and stamp distribution comparison --
    remain part of copy mode and the clean-slate restore validation.

.PARAMETER Mode
    'Copy' loads the dataset and runs the post-copy assertions. 'Checkpoint' measures, asserts and
    records the checkpoint invariants, for use after smoke, after writes, before the dump, and after
    a restore test.

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

.PARAMETER ExpectedDocumentCount
    Required in both modes: the dms.Document row count the dataset must hold at the point this run
    measures it. The dataset gains documents partway through the workflow, so this is a phase value
    and not one number for the whole workflow. For the v80 Northridge artifact: 10576794 up to and
    including the copy's C1 checkpoint and the C2 checkpoint after it, both of which are measured
    before Add-NorthridgeGapDocument.ps1 adds the seven documents the source artifact was missing;
    10576801 from C3 onward, which is the count the published artifact carries. Required rather than
    optional because the checkpoint record is acceptance evidence, and a document count that is
    measured into that record without being compared to anything is a number, not a check.

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
        -TargetDatabase northridge_target -OutputDirectory /tmp/nr -ExpectedDocumentCount 10576794 `
        -ReferenceDatabase northridge_reference -WhatIf

    Prints the allow-list and the plan without contacting a database. The count is the pre-gap one,
    because copy mode records and asserts C1 as soon as the copy finishes, and the seven gap
    documents are added later.

.EXAMPLE
    ./Copy-NorthridgeDataForward.ps1 -Mode Checkpoint -TargetDatabase northridge_target `
        -CheckpointName C2 -OutputDirectory /tmp/nr -ReferenceDatabase northridge_reference `
        -ExpectedDocumentCount 10576794

    Takes the expected fingerprint and cache state from the freshly provisioned reference database.
    C2 follows the smoke test and still precedes the gap documents, so it carries the same pre-gap
    count as C1. C3 onward pass 10576801.

.EXAMPLE
    ./Copy-NorthridgeDataForward.ps1 -Mode Checkpoint -TargetDatabase northridge_restoretest `
        -CheckpointName C5 -OutputDirectory /tmp/nr -ExpectedDocumentCount 10576801 `
        -ExpectedEffectiveSchemaHash <hash> -ExpectedResourceKeyCount 351 -ExpectedResourceKeySeedHash <hash>

    For a checkpoint taken after the reference database has been dropped. One of these two forms is
    required: a checkpoint with no expected values is refused rather than run unasserted.
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

    [Parameter(Mandatory, ParameterSetName = "Copy")]
    [Parameter(Mandatory, ParameterSetName = "Checkpoint")]
    [ValidateRange([System.Management.Automation.ValidateRangeKind]::Positive)]
    [long]
    $ExpectedDocumentCount,

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

# Every pair of databases this script reads has to be two databases. A target measured against itself
# as its own reference agrees with itself on the fingerprint and the cache state, and a target
# reconciled against itself as its own source agrees with itself on every row count and every stamp
# distribution: each is a PASS that proves nothing, and nothing below can tell it from a real one.
# Ordinal, because a quoted PostgreSQL identifier is case-sensitive.
$databaseByRole = [ordered]@{
    "-TargetDatabase"    = $TargetDatabase
    "-SourceDatabase"    = $SourceDatabase
    "-ReferenceDatabase" = $ReferenceDatabase
}
foreach ($shared in @($databaseByRole.GetEnumerator() |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_.Value) } |
            Group-Object -Property Value -CaseSensitive |
            Where-Object { $_.Count -gt 1 })) {
    throw "$(@($shared.Group | ForEach-Object { $_.Key }) -join ' and ') name the same database '$($shared.Name)'. A database measured or reconciled against itself agrees with itself on every value, which is a PASS that proves nothing; each role needs its own database."
}

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

# Runs inside the database container, on the file pg_restore wrote there. Positional parameters: the
# emitted SQL, the file to write, the staging schema. Exactly one line may be the COPY header naming
# dms."Descriptor" -- the count is checked before and after -- and only that line is rewritten: the
# pattern is anchored to the start of the line and to the COPY keyword, so a data row carrying the
# table's name in a value is untouched. Any failure exits non-zero, and the load never runs on it.
$script:DescriptorRedirectScript = @'
set -eu
in=$1; out=$2; staging=$3
header='^COPY dms\."Descriptor" ('
count=$(grep -c -e "$header" "$in" || true)
if [ "$count" != 1 ]; then
    echo "expected exactly one COPY header naming dms.\"Descriptor\" in $in, found $count" >&2
    exit 2
fi
sed -e "s/$header/COPY \"$staging\".\"Descriptor\" (/" "$in" > "$out"
redirected=$(grep -c -e "^COPY \"$staging\".\"Descriptor\" (" "$out" || true)
if [ "$redirected" != 1 ]; then
    echo "the COPY header was not re-pointed at $staging in $out, found $redirected" >&2
    exit 3
fi
'@

# The DMS-owned schemas, matching the default set Compare-DmsSchemaSnapshot.ps1 diffs. Both the
# foreign-key sweep and the disabled-trigger check are scoped to these, so neither can drift from
# what the schema compare covers.
$script:DmsOwnedSchema = @("dms") + $script:BulkSchema

# The dms sequences whose positions the copied data depends on. Restored from the archive's own
# SEQUENCE SET entries so the target inherits the producer positions rather than fresh-database ones.
$script:DmsSequence = @(
    "dms.ChangeVersionSequence",
    "dms.CollectionItemIdSequence",
    "dms.Document_DocumentId_seq"
)

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
    param(
        [Parameter(Mandatory)] [string] $DatabaseName,
        [string[]] $Schema = $script:BulkSchema
    )

    $schemaLiteral = ($Schema | ForEach-Object { "'$_'" }) -join ", "

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

# Discovery is by schema, so a bulk schema that is absent from a database -- or present with no base
# table -- contributes nothing to that database's list, and the two lists can agree while both lack
# it: the copy would then restore edfi and never ask about auth, and the row-count reconciliation
# walks the same list. Every bulk schema has to contribute at least one base table on both sides
# before either list is trusted.
function Get-BulkSchemaCoverageFailure {
    [CmdletBinding()]
    [OutputType([System.Object[]])]
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $SourceTable,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $TargetTable,
        [Parameter(Mandatory)] [string[]] $Schema
    )

    $failure = [System.Collections.Generic.List[string]]::new()
    foreach ($side in @("source", "target")) {
        $table = if ($side -eq "source") { $SourceTable } else { $TargetTable }
        foreach ($schemaName in $Schema) {
            # Ordinal: schema names are quoted identifiers like the table names they prefix.
            $prefix = "$schemaName."
            $contributed = @($table | Where-Object { $_.StartsWith($prefix, [System.StringComparison]::Ordinal) }).Count
            if ($contributed -eq 0) {
                $failure.Add("schema '$schemaName' contributes no base table in the $side, so nothing from it would be copied or reconciled")
            }
        }
    }

    # Comma operator, as in Get-DmsTableClassificationFailure: an empty result must stay a collection.
    return ,$failure
}

# The dms schema is not discovered the way the bulk schemas are: its tables are named in the lists at
# the top, because each is handled differently -- never copied, copied, or derived. A list is an
# allow-list only while it is complete. A dms base table that exists in the catalog and is on none of
# the lists is neither restored nor reconciled: the copy skips it, the row-count reconciliation never
# asks about it, and the run prints PASS over a dataset with a hole in it. So the lists are held to
# the catalog of both databases before anything is loaded: every dms base table in either must be on
# exactly one list, and the tables that carry dataset rows must exist in both.
function Get-DmsTableClassificationFailure {
    [CmdletBinding()]
    # Object[] rather than List[string]: the comma operator that stops an empty result unrolling to
    # $null wraps the return, and the declared type has to match what callers actually receive.
    [OutputType([System.Object[]])]
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $SourceTable,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $TargetTable
    )

    $failure = [System.Collections.Generic.List[string]]::new()

    $classification = [ordered]@{
        "provisioning-owned" = @($script:ProvisioningOwnedTable | ForEach-Object { "dms.$_" })
        "copied data"        = @($script:DmsDataTable | ForEach-Object { "dms.$_" })
        "derived"            = @("dms.$script:DmsDerivedTable")
    }

    # A name on two lists would be excluded by one and copied by the other, and which wins is not a
    # question this script should have to answer.
    $classOf = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
    foreach ($class in $classification.Keys) {
        foreach ($table in $classification[$class]) {
            if ($classOf.ContainsKey($table)) {
                $failure.Add("$table is classified twice, as $($classOf[$table]) and as $class")
                continue
            }
            $classOf[$table] = $class
        }
    }

    # Ordinal throughout: a quoted PostgreSQL identifier is case-sensitive, so dms.document and
    # dms.Document would be two tables, one of them unclassified.
    $sourceSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$SourceTable, [System.StringComparer]::Ordinal)
    $targetSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$TargetTable, [System.StringComparer]::Ordinal)
    $catalogTable = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($table in $SourceTable) { [void]$catalogTable.Add($table) }
    foreach ($table in $TargetTable) { [void]$catalogTable.Add($table) }

    foreach ($table in $catalogTable) {
        if ($classOf.ContainsKey($table)) { continue }
        $side = if ($sourceSet.Contains($table) -and $targetSet.Contains($table)) { "source and target" }
        elseif ($sourceSet.Contains($table)) { "source" }
        else { "target" }
        $failure.Add("$table is a base table in the $side and is on none of the lists, so it would be neither copied nor reconciled")
    }

    # Provisioning-owned tables are not checked the other way: the source is an older schema and may
    # lack a table provisioning creates today, and nothing is copied from them. The tables that carry
    # dataset rows are checked on both sides, because a copied table absent from either is a copy that
    # cannot happen or has nowhere to land.
    foreach ($table in @($classification["copied data"]) + @($classification["derived"])) {
        if (-not $sourceSet.Contains($table)) { $failure.Add("$table is not a base table in the source, so there is nothing to copy") }
        if (-not $targetSet.Contains($table)) { $failure.Add("$table is not a base table in the target, so the copy has nowhere to land") }
    }

    # Comma operator: PowerShell unrolls an empty collection to $null on return, so a caller doing
    # .Count on a clean result would fail -- a bug that can only fire when there is nothing to report.
    return ,$failure
}

# Every non-blank row is '<schema>.<table>|<count>', one per requested table. A row that does not parse
# is refused rather than skipped, because a skipped row is a table that dropped out of both maps at
# once and compared equal by absence; a repeated table is refused because the comparison cannot
# attribute it; and the parsed set is held to the requested set, as the stamp distribution is, so a
# table that produced no count -- or a count for a table nobody asked about -- stops the run by name.
function ConvertTo-RowCountMap {
    [CmdletBinding()]
    [OutputType([System.Collections.Generic.Dictionary[string, long]])]
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Row,
        [Parameter(Mandatory)] [string[]] $ExpectedTable
    )

    # Ordinal, like every other map keyed by a table name here.
    $map = [System.Collections.Generic.Dictionary[string, long]]::new([System.StringComparer]::Ordinal)
    foreach ($item in $Row) {
        $text = ([string]$item).Trim()
        # psql --tuples-only can end its output with an empty element; that is not a row.
        if ([string]::IsNullOrWhiteSpace($text)) { continue }

        if ($text -notmatch '^(?<name>.+)\|(?<count>\d+)$') {
            throw "Row count row '$text' is not '<schema>.<table>|<count>'; refusing to drop it, because a dropped row compares equal by absence."
        }

        $name = $Matches["name"]
        if ($map.ContainsKey($name)) {
            throw "Row count reported '$name' twice; the union produced a row the comparison cannot attribute."
        }
        $map[$name] = [long]$Matches["count"]
    }

    $expected = [System.Collections.Generic.HashSet[string]]::new([string[]]$ExpectedTable, [System.StringComparer]::Ordinal)
    $missing = @($ExpectedTable | Where-Object { -not $map.ContainsKey($_) } | Sort-Object)
    $unexpected = @($map.Keys | Where-Object { -not $expected.Contains($_) } | Sort-Object)
    if ($missing.Count -gt 0 -or $unexpected.Count -gt 0) {
        $missingText = if ($missing.Count -gt 0) { $missing -join ', ' } else { 'none' }
        $unexpectedText = if ($unexpected.Count -gt 0) { $unexpected -join ', ' } else { 'none' }
        throw "Row counts cover $($map.Count) table(s) for $($ExpectedTable.Count) requested. Missing: $missingText. Unexpected: $unexpectedText."
    }

    return $map
}

function Get-RowCountMap {
    [CmdletBinding()]
    [OutputType([System.Collections.Generic.Dictionary[string, long]])]
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

    return ConvertTo-RowCountMap -Row @($rows) -ExpectedTable $QualifiedTable
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
    # Object[] rather than List[string]: the comma operator that stops an empty result unrolling to
    # $null wraps the return, and the declared type has to match what callers actually receive.
    [OutputType([System.Object[]])]
    param(
        [Parameter(Mandatory)] [System.Collections.Specialized.OrderedDictionary] $Measurement,
        [Parameter(Mandatory)] [System.Collections.Specialized.OrderedDictionary] $Expected,
        [Parameter(Mandatory)] [long] $ExpectedDocumentRow,
        [string] $ExpectedIdentity
    )

    $failure = [System.Collections.Generic.List[string]]::new()

    # Every value Measure-Invariant records is checked here. A recorded-but-unchecked value would let
    # a checkpoint pass while part of the evidence table it produces is wrong.
    if ($Measurement.EffectiveSchemaHash -cne $Expected.EffectiveSchemaHash) {
        $failure.Add("dms.EffectiveSchema.EffectiveSchemaHash is '$($Measurement.EffectiveSchemaHash)', expected '$($Expected.EffectiveSchemaHash)'")
    }
    if ($Measurement.ResourceKeyCount -ne $Expected.ResourceKeyCount) {
        $failure.Add("dms.EffectiveSchema.ResourceKeyCount is $($Measurement.ResourceKeyCount), expected $($Expected.ResourceKeyCount)")
    }
    if ($Measurement.ResourceKeySeedHash -cne $Expected.ResourceKeySeedHash) {
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
    if ($Measurement.DocumentRow -ne $ExpectedDocumentRow) {
        $failure.Add("dms.Document holds $($Measurement.DocumentRow) row(s), expected $ExpectedDocumentRow")
    }

    # Comma operator: PowerShell unrolls an empty collection to $null on return, so a caller doing
    # .Count on a clean result would fail -- a bug that can only fire when there is nothing to report.
    return ,$failure
}

# A sequence left at its fresh-database position is invisible to a row-count reconciliation and only
# surfaces on the first write after restore, as a primary-key or unique-constraint collision. The dump
# carries SEQUENCE SET entries, but "the loader probably applied them" is not evidence, so each
# sequence is compared against the maximum value actually present in the copied data.
#
# What is compared is the value the next write would receive, not the recorded position. nextval()
# returns last_value + increment once is_called is true and last_value itself while it is false, so a
# sequence left by setval(seq, max, false) reports a position that looks high enough and still hands
# the next writer a value the data already holds.
function Get-SequenceStateSql {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [string] $QualifiedSequence,
        [Parameter(Mandatory)] [string] $MaximumExpression
    )

    # last_value and is_called come from the sequence relation and the increment from pg_sequence,
    # because is_called is exposed nowhere else: the pg_sequences view omits it, and
    # pg_sequence_last_value() answers NULL rather than the position while it is false. Nothing here
    # calls nextval(), which would move the sequence this check exists to inspect.
    return @"
SELECT '$Label|' || s.last_value::text || '|' || s.is_called::text || '|' || q.seqincrement::text
    || '|' || ($MaximumExpression)::text
FROM $QualifiedSequence s, pg_sequence q
WHERE q.seqrelid = '$QualifiedSequence'::regclass;
"@
}

function Test-SequencePosition {
    [CmdletBinding()]
    # Object[] rather than List[string]: the comma operator that stops an empty result unrolling to
    # $null wraps the return, and the declared type has to match what callers actually receive.
    [OutputType([System.Object[]])]
    param([Parameter(Mandatory)] [string] $DatabaseName)

    $failure = [System.Collections.Generic.List[string]]::new()

    $target = [System.Collections.Generic.List[hashtable]]::new()

    # dms.Document is the high-water mark for ChangeVersionSequence: it is the only table whose
    # ContentVersion defaults from that sequence, and the projection copies of ContentVersion are
    # stamped from the document row, so they cannot exceed it. dms.Descriptor is included anyway
    # because its stamping trigger draws from the same sequence.
    $target.Add(@{
            Label    = "ChangeVersionSequence"
            Sequence = 'dms."ChangeVersionSequence"'
            Maximum  = 'GREATEST(COALESCE((SELECT MAX("ContentVersion") FROM dms."Document"), 0), COALESCE((SELECT MAX("ContentVersion") FROM dms."Descriptor"), 0))'
        })

    # Resolved from the column rather than assumed. The name is needed as an identifier and not only as
    # a regclass, because is_called has to be read from the sequence relation itself.
    $documentSequence = Get-ScalarValue -DatabaseName $DatabaseName -Sql `
        'SELECT pg_get_serial_sequence(''dms."Document"'', ''DocumentId'');'

    if ([string]::IsNullOrWhiteSpace($documentSequence)) {
        $failure.Add("dms.Document.DocumentId owns no sequence, so its position could not be checked")
    }
    else {
        $target.Add(@{
                Label    = "DocumentIdentitySequence"
                Sequence = $documentSequence
                Maximum  = 'COALESCE((SELECT MAX("DocumentId") FROM dms."Document"), 0)'
            })
    }

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
        $target.Add(@{
                Label    = "CollectionItemIdSequence"
                Sequence = 'dms."CollectionItemIdSequence"'
                Maximum  = "(SELECT COALESCE(MAX(v), 0) FROM ($collectionUnion) AS m)"
            })
    }
    else {
        $failure.Add("no CollectionItemId columns were found, so CollectionItemIdSequence could not be checked")
    }

    $rows = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $target) {
        $stateSql = Get-SequenceStateSql -Label $item.Label -QualifiedSequence $item.Sequence `
            -MaximumExpression $item.Maximum

        foreach ($row in (Invoke-PsqlQuery -ContainerName $Container -User $PostgresUser `
                    -DatabaseName $DatabaseName -Sql $stateSql)) {
            $text = ([string]$row).Trim()
            if (-not [string]::IsNullOrWhiteSpace($text)) { $rows.Add($text) }
        }
    }

    # A sequence that reported no state is unchecked, and the loop below has nothing to disagree with.
    if ($rows.Count -ne $target.Count) {
        $failure.Add("$($rows.Count) sequence state row(s) came back for $($target.Count) requested sequence(s), so at least one sequence was not checked")
    }

    foreach ($text in $rows) {
        $part = $text.Split("|")
        if ($part.Count -ne 5) {
            $failure.Add("sequence state '$text' could not be read, so the sequence it describes is unchecked")
            continue
        }

        $name = $part[0]
        $position = [long]$part[1]
        $increment = [long]$part[3]
        $maximum = [long]$part[4]

        # is_called::text renders 'true' or 'false' server-side, so the spelling does not depend on how
        # psql happens to display booleans. Anything else is refused rather than read as false, which
        # would silently turn the next-value comparison back into a comparison against the position.
        if ($part[2] -eq "true") {
            $isCalled = $true
        }
        elseif ($part[2] -eq "false") {
            $isCalled = $false
        }
        else {
            $failure.Add("$name reported is_called='$($part[2])', which is neither true nor false, so its next value could not be computed")
            continue
        }

        # What the next nextval() hands out: the position plus the increment once the sequence has been
        # called, and the position itself while it has not.
        $nextValue = if ($isCalled) { $position + $increment } else { $position }

        # Information stream, not Write-Output: this function returns the failure list, and anything
        # written to the success stream would be returned alongside it and counted as a failure.
        Write-Information "  sequence: $name at $position (is_called=$($part[2]), increment=$increment), next value $nextValue, maximum value in data $maximum" -InformationAction Continue

        if ($increment -le 0) {
            $failure.Add("$name has increment $increment; a non-ascending sequence is not something this check can prove safe")
            continue
        }

        if ($nextValue -le $maximum) {
            $failure.Add("$name is at $position with is_called=$($part[2]) and increment $increment, so the next value would be $nextValue while the copied data already reaches $maximum; the first write after restore would collide")
        }
    }

    # Comma operator: PowerShell unrolls an empty collection to $null on return, so a caller doing
    # .Count on a clean result would fail -- a bug that can only fire when there is nothing to report.
    return ,$failure
}

# --disable-triggers also suppresses foreign-key enforcement, so referential integrity is not a
# property the loader established. Three references are named explicitly because they are the ones
# whose breakage would ruin the dataset silently: a document with no resource key, a descriptor with
# no document, an identity with no owner. Naming three leaves every other foreign key in the schema
# unproven, though, and the relational projection declares them across hundreds of tables -- so every
# constraint is then re-checked from the catalog rather than by hand.
function Test-ReferentialIntegrity {
    [CmdletBinding()]
    # Object[] rather than List[string]: the comma operator that stops an empty result unrolling to
    # $null wraps the return, and the declared type has to match what callers actually receive.
    [OutputType([System.Object[]])]
    param(
        [Parameter(Mandatory)] [string] $DatabaseName,
        # The row counts already measured on the target. A child table with no rows cannot hold a
        # violation, so its constraints are skipped -- and the skip is counted and printed, because a
        # sweep that quietly checked nothing would print what a clean one prints. A table the map does
        # not mention is validated rather than skipped. IDictionary rather than hashtable: the caller
        # passes an Ordinal dictionary, and a [hashtable] parameter would copy it into a Hashtable
        # with a comparer of PowerShell's choosing; passing it through keeps one comparer for every
        # map keyed by a table name.
        [Parameter(Mandatory)] [System.Collections.IDictionary] $RowCount
    )

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

    foreach ($item in (Test-ForeignKeyValidity -DatabaseName $DatabaseName -RowCount $RowCount)) {
        $failure.Add($item)
    }

    # --disable-triggers is DISABLE TRIGGER ALL, which switches off the internal constraint triggers
    # that enforce foreign keys along with the projection triggers. A table left that way satisfies
    # every count and every anti-join above and then accepts a violating row on the first write, so
    # valid data is only half of what has to hold here. 'O' and 'A' are enabled and anything else is
    # not, which is the reading DMS's own catalog validator uses.
    $schemaLiteral = ($script:DmsOwnedSchema | ForEach-Object { "'$_'" }) -join ", "
    $disabledTrigger = [long](Get-ScalarValue -DatabaseName $DatabaseName -Sql @"
SELECT COUNT(*)
FROM pg_trigger tg
JOIN pg_class rel ON rel.oid = tg.tgrelid
JOIN pg_namespace n ON n.oid = rel.relnamespace
WHERE n.nspname IN ($schemaLiteral) AND tg.tgenabled::text NOT IN ('O', 'A');
"@)
    Write-Information "  triggers left disabled: $disabledTrigger" -InformationAction Continue
    if ($disabledTrigger -ne 0) {
        $failure.Add("$disabledTrigger trigger(s) in $($script:DmsOwnedSchema -join ', ') are still disabled after the load; the foreign keys among them are declared but not enforced")
    }

    # Comma operator: PowerShell unrolls an empty collection to $null on return, so a caller doing
    # .Count on a clean result would fail -- a bug that can only fire when there is nothing to report.
    return ,$failure
}

# Every foreign key in the DMS-owned schemas, validated against the data actually present. The
# statements are generated from pg_constraint rather than written out, so the sweep follows the schema
# instead of a list that goes stale, and MATCH SIMPLE semantics are reproduced exactly: a row with any
# null key column satisfies the constraint and must not be reported as a violation.
#
# This is the expensive check in the run, and it is the only one that answers the question the load
# leaves open. pg_restore --disable-triggers means no constraint was enforced while the rows arrived,
# and PostgreSQL will not re-validate a constraint it already believes to be valid, so the data has to
# be tested directly.
function Test-ForeignKeyValidity {
    [CmdletBinding()]
    # Object[] rather than List[string]: the comma operator that stops an empty result unrolling to
    # $null wraps the return, and the declared type has to match what callers actually receive.
    [OutputType([System.Object[]])]
    param(
        [Parameter(Mandatory)] [string] $DatabaseName,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $RowCount
    )

    $failure = [System.Collections.Generic.List[string]]::new()
    $schemaLiteral = ($script:DmsOwnedSchema | ForEach-Object { "'$_'" }) -join ", "

    # Counted straight from the catalog, independently of the generator below, so the generator can be
    # held to it. A constraint the generator silently failed to produce a statement for would otherwise
    # be indistinguishable from a constraint with no violations.
    $constraintTotal = [long](Get-ScalarValue -DatabaseName $DatabaseName -Sql @"
SELECT COUNT(*)
FROM pg_constraint con
JOIN pg_class cl ON cl.oid = con.conrelid
JOIN pg_namespace n ON n.oid = cl.relnamespace
WHERE con.contype = 'f' AND n.nspname IN ($schemaLiteral);
"@)

    # One row per constraint: the child table, a tab, then the statement that validates it. A tab
    # separates them because the statement text itself contains the pipe the label is built from.
    $generatorSql = @"
WITH fk AS (
    SELECT con.oid AS conoid, con.conname, con.conrelid, con.confrelid, con.conkey, con.confkey,
           n.nspname AS child_schema, cl.relname AS child_table,
           fn.nspname AS parent_schema, fcl.relname AS parent_table
    FROM pg_constraint con
    JOIN pg_class cl ON cl.oid = con.conrelid
    JOIN pg_namespace n ON n.oid = cl.relnamespace
    JOIN pg_class fcl ON fcl.oid = con.confrelid
    JOIN pg_namespace fn ON fn.oid = fcl.relnamespace
    WHERE con.contype = 'f' AND n.nspname IN ($schemaLiteral)
),
predicate AS (
    SELECT f.conoid,
           string_agg(format('t.%I IS NOT NULL', ca.attname), ' AND ' ORDER BY k.ord) AS not_null_test,
           string_agg(format('p.%I = t.%I', pa.attname, ca.attname), ' AND ' ORDER BY k.ord) AS join_test
    FROM fk f
    CROSS JOIN LATERAL unnest(f.conkey, f.confkey) WITH ORDINALITY AS k(child_att, parent_att, ord)
    JOIN pg_attribute ca ON ca.attrelid = f.conrelid AND ca.attnum = k.child_att
    JOIN pg_attribute pa ON pa.attrelid = f.confrelid AND pa.attnum = k.parent_att
    GROUP BY f.conoid
)
SELECT f.child_schema || '.' || f.child_table || E'\t' || format(
    'SELECT %L || ''|'' || COUNT(*)::text FROM %I.%I t WHERE (%s) AND NOT EXISTS (SELECT 1 FROM %I.%I p WHERE %s)',
    f.child_schema || '.' || f.child_table || '|' || f.conname,
    f.child_schema, f.child_table, p.not_null_test,
    f.parent_schema, f.parent_table, p.join_test)
FROM fk f
JOIN predicate p ON p.conoid = f.conoid
ORDER BY f.child_schema, f.child_table, f.conname;
"@

    $generated = @(Invoke-PsqlQuery -ContainerName $Container -User $PostgresUser `
            -DatabaseName $DatabaseName -Sql $generatorSql)

    $statement = [System.Collections.Generic.List[string]]::new()
    $skipped = 0

    foreach ($row in $generated) {
        $text = ([string]$row).Trim()
        if ([string]::IsNullOrWhiteSpace($text)) { continue }

        $tabIndex = $text.IndexOf("`t")
        if ($tabIndex -lt 1) { continue }

        $childTable = $text.Substring(0, $tabIndex)
        if ($RowCount.ContainsKey($childTable) -and [long]$RowCount[$childTable] -eq 0) {
            $skipped++
            continue
        }

        $statement.Add($text.Substring($tabIndex + 1))
    }

    # No constraint found means the generator matched nothing, not that the data is clean. Reporting
    # zero violations out of zero checks is the exact shape of evidence this script exists to refuse.
    if ($constraintTotal -eq 0) {
        $failure.Add("no foreign key constraint was found in $($script:DmsOwnedSchema -join ', '), so nothing was validated; a clean result from an empty sweep proves nothing")
        return ,$failure
    }

    if (($statement.Count + $skipped) -ne $constraintTotal) {
        $failure.Add("$constraintTotal foreign key constraint(s) exist in $($script:DmsOwnedSchema -join ', ') but the sweep accounted for $($statement.Count + $skipped); the remainder was never validated")
        return ,$failure
    }

    # Batched, rather than one statement per constraint or one statement for all of them: thousands of
    # round trips is slow, and a single query with thousands of branches is slow to plan.
    $violation = [System.Collections.Generic.List[string]]::new()
    $answered = 0
    $batchSize = 100

    for ($index = 0; $index -lt $statement.Count; $index += $batchSize) {
        $length = [Math]::Min($batchSize, $statement.Count - $index)
        $batch = $statement.GetRange($index, $length)

        $rows = Invoke-PsqlQuery -ContainerName $Container -User $PostgresUser `
            -DatabaseName $DatabaseName -Sql (($batch -join "`nUNION ALL`n") + ";")

        foreach ($row in $rows) {
            $text = ([string]$row).Trim()
            if ([string]::IsNullOrWhiteSpace($text)) { continue }

            if ($text -notmatch '^(?<name>.+)\|(?<count>\d+)$') {
                # Skipping an unreadable row would drop a check without dropping the appearance of one.
                $failure.Add("unreadable row from the foreign key sweep: '$text'")
                continue
            }

            $answered++
            if ([long]$Matches["count"] -ne 0) {
                $violation.Add("$($Matches["name"]) has $($Matches["count"]) row(s) whose foreign key has no matching parent")
            }
        }
    }

    # Every statement sent has to come back with a result. A count read from fewer answers than checks
    # is a partial sweep reported as a complete one.
    if ($answered -ne $statement.Count) {
        $failure.Add("the foreign key sweep sent $($statement.Count) check(s) and read back $answered result(s); the difference was not validated")
    }

    Write-Information ("  foreign keys checked: {0} of {1} constraint(s), {2} skipped as empty child table, {3} answered, {4} violated" -f `
            $statement.Count, $constraintTotal, $skipped, $answered, $violation.Count) -InformationAction Continue

    foreach ($item in ($violation | Sort-Object)) {
        $failure.Add("$item; foreign keys were not enforced during the load")
    }

    # Comma operator: PowerShell unrolls an empty collection to $null on return, so a caller doing
    # .Count on a clean result would fail -- a bug that can only fire when there is nothing to report.
    return ,$failure
}

# One row per requested table, or the comparison is not the one the plan announced. A row without the
# separator is refused rather than skipped, because a skipped row is a table that dropped out of both
# maps at once and compared equal by absence; and the parsed set is held to the requested set, because
# a table that produced no row -- or a row for a table nobody asked about -- means the union did not
# run the way it was built.
function ConvertTo-StampDistributionMap {
    [CmdletBinding()]
    [OutputType([System.Collections.Generic.Dictionary[string, string]])]
    param(
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Row,
        [Parameter(Mandatory)] [string[]] $ExpectedTable
    )

    # Ordinal, like every other map keyed by a table name here: a hashtable literal would read two
    # tables differing only in case as one and compare the wrong distributions to each other.
    $map = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::Ordinal)
    foreach ($item in $Row) {
        $text = ([string]$item).Trim()
        # psql --tuples-only can end its output with an empty element; that is not a row.
        if ([string]::IsNullOrWhiteSpace($text)) { continue }

        $separatorIndex = $text.IndexOf("|")
        if ($separatorIndex -lt 1) {
            throw "Stamp distribution row '$text' has no table name before its first separator; refusing to drop it, because a dropped row compares equal by absence."
        }

        $name = $text.Substring(0, $separatorIndex)
        if ($map.ContainsKey($name)) {
            throw "Stamp distribution reported '$name' twice; the union produced a row the comparison cannot attribute."
        }
        $map[$name] = $text.Substring($separatorIndex + 1)
    }

    $expected = [System.Collections.Generic.HashSet[string]]::new([string[]]$ExpectedTable, [System.StringComparer]::Ordinal)
    $missing = @($ExpectedTable | Where-Object { -not $map.ContainsKey($_) } | Sort-Object)
    $unexpected = @($map.Keys | Where-Object { -not $expected.Contains($_) } | Sort-Object)
    if ($missing.Count -gt 0 -or $unexpected.Count -gt 0) {
        $missingText = if ($missing.Count -gt 0) { $missing -join ', ' } else { 'none' }
        $unexpectedText = if ($unexpected.Count -gt 0) { $unexpected -join ', ' } else { 'none' }
        throw "Stamp distribution covers $($map.Count) table(s) for $($ExpectedTable.Count) requested. Missing: $missingText. Unexpected: $unexpectedText."
    }

    return $map
}

# Row counts cannot see a rewritten stamp: the trigger that would corrupt these values updates rows in
# place, leaving the count identical. Comparing the distribution is what detects it.
function Get-StampDistribution {
    [CmdletBinding()]
    [OutputType([System.Collections.Generic.Dictionary[string, string]])]
    param(
        [Parameter(Mandatory)] [string] $DatabaseName,
        [Parameter(Mandatory)] [string[]] $SampleTable
    )

    $part = [System.Collections.Generic.List[string]]::new()
    $part.Add('SELECT ''dms.Document'' AS t, COUNT(*)::text || ''|'' ||
        COALESCE(MIN("ContentVersion")::text, ''-'') || ''|'' || COALESCE(MAX("ContentVersion")::text, ''-'') || ''|'' ||
        COALESCE(MIN("ContentLastModifiedAt")::text, ''-'') || ''|'' || COALESCE(MAX("ContentLastModifiedAt")::text, ''-'') || ''|'' ||
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

    return ConvertTo-StampDistributionMap -Row @($rows) -ExpectedTable (@("dms.Document") + $SampleTable)
}

# pg_restore's -n and -t filters are OR-ed within each kind and AND-ed across kinds, so together they
# cannot express a schema-qualified allow-list: passing '-t Descriptor' for
# tracked_changes_edfi.Descriptor while '-n dms' is also in effect selects dms.Descriptor as well --
# the one table this copy must never restore, because its ResourceKeyId has to be derived. Selecting
# individual archive entries from the dump's own table of contents is what an allow-list actually is.
function Select-ArchiveEntry {
    [CmdletBinding()]
    [OutputType([System.Collections.Specialized.OrderedDictionary])]
    param(
        [Parameter(Mandatory)] [string] $ContainerName,
        [Parameter(Mandatory)] [string] $ArchivePath,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $QualifiedTable,
        [AllowEmptyCollection()] [string[]] $QualifiedSequence = @(),
        # The bulk load must never see dms.Descriptor; the staging load exists precisely to fetch it.
        # One helper serving both needs the distinction passed in, not assumed.
        [switch] $AllowDerivedTable
    )

    $requestedTable = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($table in $QualifiedTable) { [void]$requestedTable.Add($table) }

    $requestedSequence = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($sequence in $QualifiedSequence) { [void]$requestedSequence.Add($sequence) }

    $tocLine = docker exec $ContainerName pg_restore -l $ArchivePath
    if ($LASTEXITCODE -ne 0) {
        throw "pg_restore -l failed against '$ArchivePath' (exit $LASTEXITCODE)."
    }

    $selectedLine = [System.Collections.Generic.List[string]]::new()
    $selectedTable = [System.Collections.Generic.List[string]]::new()
    $selectedSequence = [System.Collections.Generic.List[string]]::new()
    $seenTable = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $seenSequence = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

    foreach ($line in $tocLine) {
        $text = [string]$line

        # Archive TOC rows look like:
        #   "14346; 0 16795 TABLE DATA dms Document postgres"
        #   "15158; 0 0 SEQUENCE SET dms ChangeVersionSequence postgres"
        if ($text -match '^\s*\d+;\s+\d+\s+\d+\s+TABLE DATA\s+(?<schema>\S+)\s+(?<name>\S+)\s') {
            $qualified = "$($Matches['schema']).$($Matches['name'])"
            if (-not $requestedTable.Contains($qualified)) { continue }
            if (-not $seenTable.Add($qualified)) {
                throw "The archive holds more than one TABLE DATA entry for '$qualified'; refusing to guess which to restore."
            }
            $selectedLine.Add($text)
            $selectedTable.Add($qualified)
            continue
        }

        if ($text -match '^\s*\d+;\s+\d+\s+\d+\s+SEQUENCE SET\s+(?<schema>\S+)\s+(?<name>\S+)\s') {
            $qualified = "$($Matches['schema']).$($Matches['name'])"
            if (-not $requestedSequence.Contains($qualified)) { continue }
            if (-not $seenSequence.Add($qualified)) {
                throw "The archive holds more than one SEQUENCE SET entry for '$qualified'; refusing to guess which to restore."
            }
            $selectedLine.Add($text)
            $selectedSequence.Add($qualified)
        }
    }

    # A requested entry with no archive counterpart means the dump is not the one this copy was
    # written for. Restoring the remainder would produce a target that reconciles against nothing.
    $missingTable = @($requestedTable | Where-Object { -not $seenTable.Contains($_) } | Sort-Object)
    if ($missingTable.Count -gt 0) {
        throw "The archive has no TABLE DATA entry for $($missingTable.Count) requested table(s): $(($missingTable | Select-Object -First 10) -join ', ')."
    }

    $missingSequence = @($requestedSequence | Where-Object { -not $seenSequence.Contains($_) } | Sort-Object)
    if ($missingSequence.Count -gt 0) {
        throw "The archive has no SEQUENCE SET entry for $($missingSequence.Count) requested sequence(s): $($missingSequence -join ', ')."
    }

    # Belt and braces: nothing provisioning-owned may reach the list even if the caller asked for it.
    # dms.Descriptor is forbidden unless the caller is the staging load that exists to fetch it.
    $forbiddenName = @($script:ProvisioningOwnedTable | ForEach-Object { "dms.$_" })
    if (-not $AllowDerivedTable) {
        $forbiddenName += "dms.$script:DmsDerivedTable"
    }

    $forbidden = @($selectedTable | Where-Object { $forbiddenName -contains $_ })
    if ($forbidden.Count -gt 0) {
        throw "The filtered restore list contains provisioning-owned or derived object(s): $($forbidden -join ', ')."
    }

    return [ordered]@{
        Line     = $selectedLine
        Table    = $selectedTable
        Sequence = $selectedSequence
    }
}

# Without --exit-on-error, pg_restore does not stop at a failed COPY: it skips the entry, carries on
# through the rest of the archive, and summarises what it swallowed as "errors ignored on restore: N".
# The bulk load hit exactly that. The status does end up non-zero, so the exit code is checked first
# here, but by the time it is returned the database already holds a partial load, and the summary line
# is the only thing that says which entries are missing -- so the output is scanned as well, and both
# restores pass --exit-on-error so the run stops at the first failure instead of the last.
function Assert-RestoreOutputClean {
    [CmdletBinding()]
    param(
        # AllowNull matters more than it looks: a pg_restore that succeeds quietly emits nothing at
        # all, so the captured output is $null rather than an empty array. Without this the check
        # fails to bind precisely when the restore went well, and never runs on the happy path.
        [Parameter(Mandatory)] [AllowNull()] [AllowEmptyCollection()] [object[]] $Output,
        [Parameter(Mandatory)] [int] $ExitCode,
        [Parameter(Mandatory)] [string] $Description
    )

    $text = @($Output | ForEach-Object { [string]$_ })
    $problem = @($text | Where-Object {
            $_ -match 'errors ignored on restore' -or
            $_ -match 'warning: errors ignored' -or
            $_ -match 'ERROR:' -or
            $_ -match 'pg_restore: error'
        })

    if ($ExitCode -ne 0) {
        throw "$Description reported exit $ExitCode.$(if ($problem.Count -gt 0) { ' ' + ($problem -join ' | ') })"
    }

    if ($problem.Count -gt 0) {
        throw "$Description exited 0 but reported errors in its output, so an entry was skipped rather than restored: $($problem -join ' | ')"
    }
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

function Get-OrdinalSortedUnique {
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory)] [System.Collections.IEnumerable] $Value)

    $set = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($item in $Value) {
        if ($null -ne $item) {
            [void]$set.Add([string]$item)
        }
    }

    return [string[]]@($set)
}

function Get-CheckpointExpectedValue {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string] $Key,
        [Parameter(Mandatory)] [System.Collections.Specialized.OrderedDictionary] $Expected,
        [Parameter(Mandatory)] [long] $ExpectedDocumentRow,
        [string] $ExpectedIdentity
    )

    switch ($Key) {
        "OwnershipTokenNotNull" { return "0" }
        "ProjectionWorkRow" { return "0" }
        "DocumentCacheRow" { return "0" }
        "CacheStateLifecycle" { return [string]$Expected.CacheStateLifecycle }
        "CacheAheadRecovery" { return [string]$Expected.CacheAheadRecovery }
        "EffectiveSchemaHash" { return [string]$Expected.EffectiveSchemaHash }
        "ResourceKeyCount" { return [string]$Expected.ResourceKeyCount }
        "ResourceKeySeedHash" { return [string]$Expected.ResourceKeySeedHash }
        "SourceIdentity" {
            if (-not [string]::IsNullOrWhiteSpace($ExpectedIdentity)) {
                return $ExpectedIdentity
            }
            return "non-zero UUID"
        }
        "StagingSchemaPresent" { return "0" }
        "DocumentRow" { return [string]$ExpectedDocumentRow }
        default {
            # Measure-Invariant and this switch are two lists that have to agree. A key measured there
            # and not named here would be written to the record with a blank expected value, which is
            # the shape of a check that never ran, so an unknown key stops the record instead.
            throw "Measured value '$Key' has no expected value. Add it to Get-CheckpointExpectedValue and to Test-Invariant, or stop measuring it."
        }
    }
}

function Format-CheckpointRecord {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string] $CheckpointName,
        [Parameter(Mandatory)] [string] $DatabaseName,
        [Parameter(Mandatory)] [System.Collections.Specialized.OrderedDictionary] $Measurement,
        [Parameter(Mandatory)] [System.Collections.Specialized.OrderedDictionary] $Expected,
        [Parameter(Mandatory)] [long] $ExpectedDocumentRow,
        [string] $ExpectedIdentity,
        [Parameter(Mandatory)] [System.Collections.Generic.List[string]] $Failure
    )

    $line = [System.Collections.Generic.List[string]]::new()
    $result = if ($Failure.Count -eq 0) { "PASS" } else { "FAIL" }
    $line.Add("Checkpoint=$CheckpointName")
    $line.Add("Database=$DatabaseName")
    $line.Add("Result=$result")
    $line.Add("FailureCount=$($Failure.Count)")
    $line.Add("ExpectedSource=$($Expected.Source)")

    foreach ($key in $Measurement.Keys) {
        $expectedValue = Get-CheckpointExpectedValue -Key $key -Expected $Expected `
            -ExpectedDocumentRow $ExpectedDocumentRow -ExpectedIdentity $ExpectedIdentity
        $line.Add("$key=$($Measurement[$key]) expected=$expectedValue")
    }

    if ($Failure.Count -eq 0) {
        $line.Add("Assertion=recorded checks passed")
    } else {
        foreach ($item in $Failure) { $line.Add("Failure=$item") }
    }

    return (($line) -join "`n") + "`n"
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

    # Built as a List rather than reassigned from a function result, so an empty return cannot turn
    # this into $null before .Count is read.
    $failure = [System.Collections.Generic.List[string]]::new()
    foreach ($item in (Test-Invariant -Measurement $measurement -Expected $expected `
                -ExpectedDocumentRow $ExpectedDocumentCount -ExpectedIdentity $ExpectedSourceIdentity)) {
        $failure.Add($item)
    }
    foreach ($item in $sequenceFailure) { $failure.Add($item) }

    Save-Record -Path (Join-Path $OutputDirectory "checkpoint.$CheckpointName.$TargetDatabase.txt") `
        -Content (Format-CheckpointRecord -CheckpointName $CheckpointName -DatabaseName $TargetDatabase `
            -Measurement $measurement -Expected $expected -ExpectedDocumentRow $ExpectedDocumentCount `
            -ExpectedIdentity $ExpectedSourceIdentity -Failure $failure)

    Write-Output ""
    if ($failure.Count -eq 0) {
        Write-Output "PASS: checkpoint $CheckpointName -- every checkpoint invariant at its expected value."
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
Write-Output "  dms table check   : every dms base table in source and target on exactly one of the lists above, before the load"
Write-Output "  trigger handling  : pg_restore --data-only --disable-triggers (requires superuser)"
Write-Output "  derived column    : dms.Descriptor.ResourceKeyId from dms.Document via $script:StagingSchema"
Write-Output "  expected documents: $ExpectedDocumentCount"
Write-Output "  post-copy checks  : row counts both directions, sequence positions, every foreign key in $($script:DmsOwnedSchema -join '/'), no trigger left disabled, stamp distributions, checkpoint C1"

if (-not $PSCmdlet.ShouldProcess($TargetDatabase, "Copy Northridge dataset from $DumpPath")) {
    Write-Output ""
    Write-Output "WhatIf: no database was contacted and no data was copied."
    return
}

if (-not (Test-Path -LiteralPath $DumpPath)) {
    throw "Dump not found: $DumpPath"
}

# Resolved here, before the restore, not next to the checkpoint that consumes it. Resolve-ExpectedInvariant
# is where a missing -ReferenceDatabase and missing expected values are refused, and discovering that
# after a multi-hour load means the load has to be run again for nothing. The reference database is a
# separate freshly provisioned database that this run never writes to, so reading it early and
# asserting against it later reads the same values.
$expected = Resolve-ExpectedInvariant -ReferenceDatabaseName $ReferenceDatabase `
    -EffectiveSchemaHash $ExpectedEffectiveSchemaHash `
    -ResourceKeyCount $ExpectedResourceKeyCount `
    -ResourceKeySeedHash $ExpectedResourceKeySeedHash

Write-Output "Expected values from: $($expected.Source)"
Write-Output "Expected document count: $ExpectedDocumentCount"

# Guard: the target must be freshly provisioned. Copying into a populated database would silently
# double rows, and the row-count reconciliation would then be comparing against a moving target.
$targetDocumentRow = [long](Get-ScalarValue -DatabaseName $TargetDatabase -Sql 'SELECT COUNT(*) FROM dms."Document";')
if ($targetDocumentRow -ne 0) {
    throw "Target '$TargetDatabase' already holds $targetDocumentRow document(s). Provision a fresh database."
}

# Guard: the resource-key seed must be identical, or every copied ResourceKeyId means something
# different in the target than it did in the source.
$sourceSeed = Get-ScalarValue -DatabaseName $SourceDatabase -Sql `
    'SELECT "ResourceKeyCount"::text || ''|'' || encode("ResourceKeySeedHash", ''hex'') FROM dms."EffectiveSchema" WHERE "EffectiveSchemaSingletonId" = 1;'
$targetSeed = Get-ScalarValue -DatabaseName $TargetDatabase -Sql `
    'SELECT "ResourceKeyCount"::text || ''|'' || encode("ResourceKeySeedHash", ''hex'') FROM dms."EffectiveSchema" WHERE "EffectiveSchemaSingletonId" = 1;'

if ([string]::IsNullOrWhiteSpace($sourceSeed) -or [string]::IsNullOrWhiteSpace($targetSeed)) {
    throw "Resource-key seed cannot be read. Source '$sourceSeed' target '$targetSeed'. A blank seed check would prove nothing."
}

if ($sourceSeed -ne $targetSeed) {
    throw "Resource-key seed differs. Source '$sourceSeed' target '$targetSeed'. ResourceKeyId values are ordinal, so copying across a changed seed would mis-attribute every document."
}
Write-Output ""
Write-Output "Resource-key seed matches source and target: $targetSeed"

# Discovery has to come from both databases. A table present only in the freshly provisioned target is
# absent from a source-derived list, so it is never restored -- and the row-count reconciliation below
# walks that same list, which means the run would pass with a current-schema projection table empty.
# The two sets are compared here, before the dump is copied in and before anything is loaded, so a
# schema that has drifted stops the run instead of producing a dataset with a hole in it.
$sourceBulkTable = Get-DataTableList -DatabaseName $SourceDatabase
$targetBulkTable = Get-DataTableList -DatabaseName $TargetDatabase
Write-Output "Bulk data tables discovered in source: $($sourceBulkTable.Count), in target: $($targetBulkTable.Count)"

# Every bulk schema has to be in both lists before the lists are compared to each other: two lists that
# both lack a schema agree, and the copy would restore the rest and report PASS around the hole.
$coverageFailure = Get-BulkSchemaCoverageFailure -SourceTable $sourceBulkTable -TargetTable $targetBulkTable -Schema $script:BulkSchema
if ($coverageFailure.Count -gt 0) {
    throw "Bulk schema coverage is incomplete between source '$SourceDatabase' and target '$TargetDatabase': $($coverageFailure -join '; '). Every schema in $($script:BulkSchema -join '/') must hold base tables on both sides before the copy can be trusted."
}
Write-Output "  every bulk schema contributes base tables on both sides"

# Ordinal comparison: a quoted PostgreSQL identifier is case-sensitive, so two names differing only in
# case are two different tables and have to be reported rather than matched to each other.
$sourceTableSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$sourceBulkTable, [System.StringComparer]::Ordinal)
$targetTableSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$targetBulkTable, [System.StringComparer]::Ordinal)
$sourceOnlyTable = @($sourceBulkTable | Where-Object { -not $targetTableSet.Contains($_) })
$targetOnlyTable = @($targetBulkTable | Where-Object { -not $sourceTableSet.Contains($_) })

if ($sourceOnlyTable.Count -gt 0 -or $targetOnlyTable.Count -gt 0) {
    throw "Bulk table sets differ between source '$SourceDatabase' and target '$TargetDatabase' across $($script:BulkSchema -join '/'). Source-only ($($sourceOnlyTable.Count)): $($sourceOnlyTable -join ', '). Target-only ($($targetOnlyTable.Count)): $($targetOnlyTable -join ', ')."
}

Write-Output "  source and target bulk table sets are identical"
$bulkTable = $sourceBulkTable

# The dms schema is handled by name rather than discovered, so its lists are held to the catalog the
# same way, on both databases and before the dump is copied in: a dms table on none of them would be
# neither copied nor reconciled, and the run would print PASS over the hole.
$sourceDmsTable = Get-DataTableList -DatabaseName $SourceDatabase -Schema @("dms")
$targetDmsTable = Get-DataTableList -DatabaseName $TargetDatabase -Schema @("dms")
$classificationFailure = Get-DmsTableClassificationFailure -SourceTable $sourceDmsTable -TargetTable $targetDmsTable
if ($classificationFailure.Count -gt 0) {
    throw "dms base tables are not fully classified between source '$SourceDatabase' and target '$TargetDatabase': $($classificationFailure -join '; '). Every dms base table must be named in this script as provisioning-owned, copied data or derived before the copy can be trusted."
}
Write-Output "  dms base tables classified exactly once: $($targetDmsTable.Count) in target, $($sourceDmsTable.Count) in source"

$requestedTable = @($script:DmsDataTable | ForEach-Object { "dms.$_" }) + $bulkTable

$containerDumpPath = "/tmp/northridge-dataforward.dump"
$containerListPath = "/tmp/northridge-dataforward.list"

try {
    # Inside the try, so a copy that fails part-way is removed by the finally like everything else.
    docker cp $DumpPath "${Container}:${containerDumpPath}"
    if ($LASTEXITCODE -ne 0) { throw "docker cp of the dump into '$Container' failed." }

    # A TOC filtered to TABLE DATA alone drops the archive's SEQUENCE SET entries, which is how the
    # sequences stay at their fresh-database position while the copied data runs to millions. The row
    # counts would still reconcile and the first write after restore would collide, so the sequence
    # entries are selected explicitly alongside the tables.
    $selection = Select-ArchiveEntry -ContainerName $Container -ArchivePath $containerDumpPath `
        -QualifiedTable $requestedTable -QualifiedSequence $script:DmsSequence

    Write-Output "Requested $($requestedTable.Count) table(s); selected $($selection.Table.Count) TABLE DATA entr(ies)."
    Write-Output "Requested $($script:DmsSequence.Count) sequence(s); selected $($selection.Sequence.Count) SEQUENCE SET entr(ies): $($selection.Sequence -join ', ')."
    Write-Output "  dms.Descriptor excluded from the bulk list (derived column, loaded separately)."

    if ($selection.Table.Count -ne $requestedTable.Count) {
        throw "Selected $($selection.Table.Count) archive entries for $($requestedTable.Count) requested tables."
    }
    if ($selection.Sequence.Count -ne $script:DmsSequence.Count) {
        throw "Selected $($selection.Sequence.Count) sequence entries for $($script:DmsSequence.Count) requested sequences."
    }

    # The filtered list is audit evidence: it records exactly which archive entries were restored.
    $listContent = (($selection.Line) -join "`n") + "`n"
    Save-Record -Path (Join-Path $OutputDirectory "restore-list.$TargetDatabase.txt") -Content $listContent
    $listContent | docker exec -i $Container sh -c "cat > $containerListPath"
    if ($LASTEXITCODE -ne 0) { throw "writing the filtered restore list into '$Container' failed." }

    Write-Output "Restoring $($selection.Table.Count) table(s) with triggers disabled..."

    # --exit-on-error stops at the first failure instead of skipping the table and continuing; the
    # output scan below then catches anything that still slips through with a zero exit code.
    $restoreOutput = docker exec $Container pg_restore `
        --data-only --disable-triggers --no-owner --no-privileges --exit-on-error `
        -U $PostgresUser -d $TargetDatabase -L $containerListPath $containerDumpPath 2>&1
    $restoreExit = $LASTEXITCODE

    Save-Record -Path (Join-Path $OutputDirectory "restore-output.$TargetDatabase.txt") `
        -Content ((@($restoreOutput | ForEach-Object { [string]$_ }) -join "`n") + "`n")

    foreach ($line in $restoreOutput) {
        $text = [string]$line
        if (-not [string]::IsNullOrWhiteSpace($text)) { Write-Output "  pg_restore: $text" }
    }

    Assert-RestoreOutputClean -Output $restoreOutput -ExitCode $restoreExit -Description "Bulk pg_restore"
}
finally {
    docker exec -u 0 $Container rm -f $containerDumpPath $containerListPath | Out-Null
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

$containerDescriptorSqlPath = "/tmp/northridge-descriptor.sql"
$containerStagingSqlPath = "/tmp/northridge-descriptor.staging.sql"

try {
    docker cp $DumpPath "${Container}:${containerDumpPath}"
    if ($LASTEXITCODE -ne 0) { throw "docker cp of the dump for the Descriptor load failed." }

    # Exact TOC selection here too. '-n dms -t Descriptor' is unambiguous because a single schema is
    # selected, but selecting the archive entry keeps one mechanism for both restores rather than two
    # with different failure modes.
    $descriptorSelection = Select-ArchiveEntry -ContainerName $Container `
        -ArchivePath $containerDumpPath -QualifiedTable @("dms.$script:DmsDerivedTable") -AllowDerivedTable

    $descriptorListContent = (($descriptorSelection.Line) -join "`n") + "`n"
    Save-Record -Path (Join-Path $OutputDirectory "restore-list.descriptor.txt") -Content $descriptorListContent
    $descriptorListContent | docker exec -i $Container sh -c "cat > $containerListPath"
    if ($LASTEXITCODE -ne 0) { throw "writing the Descriptor restore list into '$Container' failed." }

    # Emitted as text into a file inside the container and re-pointed there, so the dump's COPY lands
    # on the staging copy rather than the real table, which cannot accept the artifact's column list.
    # The SQL never crosses into a PowerShell string: pg_restore's diagnostics stay on stderr, where
    # the scan below reads them, instead of being merged into the text that reaches psql; no host
    # encoding or newline handling touches the data rows; and the rewrite is anchored to the one COPY
    # header line, so a data row that happens to carry the table's name is never text a replacement
    # could match. The rewrite refuses to run unless exactly one header names dms."Descriptor".
    $descriptorRestoreOutput = docker exec $Container pg_restore --data-only --no-owner --no-privileges `
        --exit-on-error -L $containerListPath -f $containerDescriptorSqlPath $containerDumpPath 2>&1
    Assert-RestoreOutputClean -Output $descriptorRestoreOutput -ExitCode $LASTEXITCODE `
        -Description "pg_restore of dms.Descriptor to text"

    $redirectOutput = $script:DescriptorRedirectScript | docker exec -i $Container sh -s `
        $containerDescriptorSqlPath $containerStagingSqlPath $script:StagingSchema 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "The Descriptor COPY header was not re-pointed at the staging table (exit $LASTEXITCODE): $(@($redirectOutput | ForEach-Object { [string]$_ }) -join ' | '). Refusing to run SQL that would target dms.Descriptor directly."
    }

    $stagingLoadOutput = docker exec $Container psql -U $PostgresUser -d $TargetDatabase `
        -v ON_ERROR_STOP=1 --quiet -f $containerStagingSqlPath 2>&1
    Assert-RestoreOutputClean -Output $stagingLoadOutput -ExitCode $LASTEXITCODE `
        -Description "Loading dms.Descriptor into staging"
}
finally {
    docker exec -u 0 $Container rm -f $containerDumpPath $containerListPath `
        $containerDescriptorSqlPath $containerStagingSqlPath | Out-Null
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

# Walked over the table list, not over the union of what parsed: the parser holds each map to that
# list, and this loop proves it again, so a table with no count on either side is a failure named here
# rather than an absence that compares equal to an absence.
$countFailure = [System.Collections.Generic.List[string]]::new()
foreach ($table in (Get-OrdinalSortedUnique -Value $allTable)) {
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
$integrityFailure = Test-ReferentialIntegrity -DatabaseName $TargetDatabase -RowCount $targetCount

Write-Output ""
Write-Output "Comparing stamp distributions..."

# The largest stamp-bearing projection tables. A stamping trigger that fired during the load would
# have rewritten ContentVersion and ContentLastModifiedAt in place, which no row count can see.
#
# Only root tables carry those columns. A collection table's stamp trigger bumps its PARENT's
# ContentVersion rather than its own, so a child table has nothing to compare and querying it for
# ContentVersion is an error, not a finding.
$stampBearing = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($row in (Invoke-PsqlQuery -ContainerName $Container -User $PostgresUser -DatabaseName $TargetDatabase -Sql @'
SELECT 'edfi.' || c.table_name
FROM information_schema.columns c
WHERE c.table_schema = 'edfi' AND c.column_name = 'ContentVersion'
  AND EXISTS (SELECT 1 FROM information_schema.columns c2
              WHERE c2.table_schema = 'edfi' AND c2.table_name = c.table_name
                AND c2.column_name = 'ContentLastModifiedAt')
ORDER BY 1;
'@)) {
    $text = ([string]$row).Trim()
    if ($text) { [void]$stampBearing.Add($text) }
}

$sampleTable = @(
    $targetCount.GetEnumerator() |
    Where-Object {
        $_.Key.StartsWith("edfi.", [System.StringComparison]::Ordinal) -and
        $_.Value -gt 0 -and
        $stampBearing.Contains($_.Key)
    } |
    Sort-Object -Property Value -Descending |
    Select-Object -First 10 -ExpandProperty Key
)
Write-Output "  stamp-bearing projection tables: $($stampBearing.Count); sampled: $($sampleTable.Count)"
if ($sampleTable.Count -eq 0) {
    throw "No stamp-bearing projection table carried rows, so the stamp comparison would prove nothing."
}

$sourceStamp = Get-StampDistribution -DatabaseName $SourceDatabase -SampleTable $sampleTable
$targetStamp = Get-StampDistribution -DatabaseName $TargetDatabase -SampleTable $sampleTable

$stampFailure = [System.Collections.Generic.List[string]]::new()
foreach ($name in (Get-OrdinalSortedUnique -Value (@($sourceStamp.Keys) + @($targetStamp.Keys)))) {
    $sourceValue = if ($sourceStamp.ContainsKey($name)) { $sourceStamp[$name] } else { "absent" }
    $targetValue = if ($targetStamp.ContainsKey($name)) { $targetStamp[$name] } else { "absent" }

    if ($sourceValue -ne $targetValue) {
        $stampFailure.Add("$name stamp distribution differs: source=$sourceValue target=$targetValue")
    }
}
Write-Output "  stamp distributions compared: $((Get-OrdinalSortedUnique -Value (@($sourceStamp.Keys) + @($targetStamp.Keys))).Count), differing: $($stampFailure.Count)"

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
Write-Output "Expected values from: $($expected.Source)"

$measurement = Measure-Invariant -DatabaseName $TargetDatabase
foreach ($key in $measurement.Keys) {
    Write-Output ("  {0,-22}: {1}" -f $key, $measurement[$key])
}

# Every failure from every check is collected before anything throws, so one run reports the whole
# picture instead of stopping at the first problem and hiding the rest.
$invariantFailure = Test-Invariant -Measurement $measurement -Expected $expected `
    -ExpectedDocumentRow $ExpectedDocumentCount

$allFailure = [System.Collections.Generic.List[string]]::new()
foreach ($item in $countFailure) { $allFailure.Add("row count: $item") }
foreach ($item in $sequenceFailure) { $allFailure.Add("sequence: $item") }
foreach ($item in $integrityFailure) { $allFailure.Add("integrity: $item") }
foreach ($item in $stampFailure) { $allFailure.Add("stamp: $item") }
foreach ($item in $invariantFailure) { $allFailure.Add("invariant: $item") }

Save-Record -Path (Join-Path $OutputDirectory "checkpoint.C1.$TargetDatabase.txt") `
    -Content (Format-CheckpointRecord -CheckpointName "C1" -DatabaseName $TargetDatabase `
        -Measurement $measurement -Expected $expected -ExpectedDocumentRow $ExpectedDocumentCount `
        -Failure $allFailure)

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
