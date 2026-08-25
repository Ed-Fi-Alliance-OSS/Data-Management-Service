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

    Trigger state is captured, not just trigger text. The copy path loads with pg_restore
    --disable-triggers, so a trigger that was never switched back on -- including the internal
    constraint triggers that enforce foreign keys -- is exactly the drift this compare exists to
    catch, and it is invisible in a definition-only snapshot.

    Ownership, privileges and routine security attributes are captured for the same reason. The
    restore path loads with pg_restore --no-owner --no-privileges, which drops object ownership and
    every GRANT the DDL issued. DMS PostgreSQL locks the document-projection enqueue functions down:
    they are SECURITY DEFINER, owned by a dedicated role rather than by the deploying superuser, and
    EXECUTE on them is revoked from PUBLIC and from the session user. A restore strips all three,
    leaving functions that still run as SECURITY DEFINER while owned by the superuser with EXECUTE
    back at the PostgreSQL default, which grants it to PUBLIC. pg_get_functiondef carries none of
    that, so a definition-only snapshot hashes the two databases identically while one of them has
    lost the privilege boundary entirely.

    One deparse artifact is normalized, and only one. pg_get_constraintdef spells an array cast two
    ways that PostgreSQL treats as the same expression: a deployment stores CHECK ("Col" IN (...)) on
    a varchar column as (ARRAY['a'::character varying, ...])::text[], and a database restored from a
    dump of it re-parses that text with the cast pushed into the elements, ARRAY[('a'::character
    varying)::text, ...], from then on. The element-wise spelling is rewritten to the gathered one so
    a restore compares equal to the deployment it came from; a different value, element count, cast
    type or column still differs. See ConvertTo-CanonicalConstraintDefinition.

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

if ($Database.Count -eq 2 -and [System.String]::Equals($Database[0], $Database[1], [System.StringComparison]::Ordinal)) {
    throw "Pass two distinct database names when requesting a schema diff. '$($Database[0])' was supplied twice, which would compare a snapshot to itself."
}

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

# pg_get_constraintdef renders an array cast in one of two spellings that PostgreSQL treats as the
# same expression. The DDL's CHECK ("Col" IN ('a', 'b')) on a varchar column is stored -- and
# deparsed by a fresh deployment -- as (ARRAY['a'::character varying, ...])::text[]. pg_dump writes
# that text, a restore re-parses it, and the parser pushes the cast into the elements, so the
# restored catalog deparses ARRAY[('a'::character varying)::text, ...] from then on; a second round
# trip leaves that spelling as it is. Both spell one predicate, so a byte compare of the raw text
# fails a restore against the deployment it came from on that row alone. The element-wise spelling
# is rewritten to the gathered one here, and only when every element carries the same parenthesised
# cast, so the two forms compare equal while a different value, a different element count, a
# different cast type or a different column still differ. Nothing is dropped: the elements, their
# types and their order all survive the rewrite, and an array that does not have that exact shape
# -- uncast elements, mixed casts, a nested array -- is left as the catalog rendered it.
function ConvertTo-CanonicalConstraintDefinition {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Definition)

    # One element of the pushed-down spelling: a parenthesised expression whose own parentheses, if
    # any, sit inside string literals, followed by a scalar cast. The type is a bare or
    # space-separated name such as text or character varying, optionally with a typmod; an array
    # type ends in [] and does not match, so a nested array cast is reported rather than rewritten.
    $element = "\((?<inner>(?:'(?:[^']|'')*'|[^()'])*)\)::(?<type>[A-Za-z_][A-Za-z0-9_]*(?: [A-Za-z_][A-Za-z0-9_]*)*(?:\([0-9, ]*\))?)"
    $elementList = [regex]::new("^(?<element>$element)(?:, (?<element>$element))*$")

    return [regex]::Replace($Definition, 'ARRAY\[(?<body>[^\[\]]*)\]', {
            param($match)

            $list = $elementList.Match($match.Groups["body"].Value)
            if (-not $list.Success) { return $match.Value }

            $inner = [System.Collections.Generic.List[string]]::new()
            $type = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
            foreach ($capture in $list.Groups["element"].Captures) {
                $part = [regex]::Match($capture.Value, "^$element$")
                $inner.Add($part.Groups["inner"].Value)
                [void]$type.Add($part.Groups["type"].Value)
            }

            # Every element cast to the same type is what a pushed-down array cast looks like; anything
            # else is a genuine expression of its own and is not this script's to rewrite.
            if ($type.Count -ne 1) { return $match.Value }

            return "(ARRAY[$($inner -join ', ')])::$(@($type)[0])[]"
        })
}

# -Schema is free text, and sections 11 and 12 read dms."EffectiveSchema" and dms."SchemaComponent"
# by name whatever it says. A misspelled schema therefore still produces those rows, both snapshots
# still agree, and the run reports PASS having compared nothing in the schema the caller asked for.
# The requested names are checked against pg_namespace rather than inferred from rows coming back.
function Get-MissingSchema {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)] [string] $DatabaseName,
        [Parameter(Mandatory)] [string[]] $Name,
        [Parameter(Mandatory)] [string] $ContainerName,
        [Parameter(Mandatory)] [string] $User
    )

    $sql = @"
SELECT nspname
FROM pg_namespace
WHERE nspname IN ($(Get-SchemaLiteralList -Name $Name))
ORDER BY nspname;
"@

    $present = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($row in (Invoke-PsqlQuery -ContainerName $ContainerName -User $User `
                -DatabaseName $DatabaseName -Sql $sql)) {
        $text = ([string]$row).Trim()
        if ($text) { [void]$present.Add($text) }
    }

    # Ordinal comparison: a quoted PostgreSQL identifier is case-sensitive, so a schema that differs
    # only in case is a different schema and has to read as missing.
    return [string[]]@($Name | Where-Object { -not $present.Contains($_) })
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
SELECT 'table|' || n.nspname || '.' || c.relname || '|' || c.relkind::text || '|persistence=' || c.relpersistence::text
    || '|owner=' || pg_get_userbyid(c.relowner)
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname IN ($SchemaList) AND c.relkind IN ('r','p','v','m','f')
ORDER BY n.nspname, c.relname;
"@

        # A generated column keeps its expression in generation_expression; column_default reads NULL
        # for it. DMS PostgreSQL declares GENERATED ALWAYS AS (...) STORED columns, so without both
        # fields a stale or wrong generated expression is invisible here as long as the column name,
        # type and nullability still agree -- and the expression is what the projection depends on.
        #
        # The expression is emitted twice on purpose. The catalog pretty-prints it over several lines
        # -- the unification expressions in this schema are multi-line CASE expressions -- and this
        # file is compared line by line, so the exact text is carried as an md5 to keep one object on
        # one line, in the same way sections 08 and 09 carry routine and view bodies. The
        # whitespace-collapsed text follows only so a diff names what changed; the md5 is what makes
        # the comparison exact, because collapsing whitespace on its own could equate two expressions
        # that differ inside a string literal.
        "03-column"     = @"
SELECT 'column|' || table_schema || '.' || table_name || '|' || ordinal_position || '|' || column_name
    || '|' || data_type
    || '|len=' || COALESCE(character_maximum_length::text, '')
    || '|num=' || COALESCE(numeric_precision::text, '') || ',' || COALESCE(numeric_scale::text, '')
    || '|null=' || is_nullable
    || '|default=' || COALESCE(column_default, '')
    || '|identity=' || COALESCE(is_identity, 'NO') || ',' || COALESCE(identity_generation, '')
    || '|generated=' || COALESCE(is_generated, 'NEVER')
    || ',' || COALESCE(md5(generation_expression), '')
    || ',' || COALESCE(regexp_replace(generation_expression, '\s+', ' ', 'g'), '')
FROM information_schema.columns
WHERE table_schema IN ($SchemaList)
ORDER BY table_schema, table_name, ordinal_position;
"@

        "04-constraint" = @"
SELECT 'constraint|' || n.nspname || '.' || rel.relname || '|' || con.conname || '|' || con.contype::text
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
    || '|owner=' || sequenceowner
FROM pg_sequences
WHERE schemaname IN ($SchemaList)
ORDER BY schemaname, sequencename;
"@

        # tgenabled is part of the snapshot, not a detail: the copy path runs pg_restore with
        # --disable-triggers, which issues DISABLE TRIGGER ALL, and a table left that way has the
        # right trigger definition and no trigger. 'O' and 'A' are enabled, 'D' is disabled and 'R'
        # fires only for a replica session -- the same reading DMS's own catalog validator uses.
        "07-trigger"    = @"
SELECT 'trigger|' || n.nspname || '.' || rel.relname || '|' || tg.tgname
    || '|enabled=' || tg.tgenabled::text
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

        # Foreign-key enforcement is carried by internal constraint triggers, which section
        # 07-trigger excludes and which DISABLE TRIGGER ALL switches off along with everything else:
        # the constraint stays in pg_constraint and stops being enforced. Their generated names embed
        # OIDs and so differ between any two databases, which is why the row is keyed by the
        # constraint and reports only the aggregated enabled state -- deterministic, and comparable
        # across a target and a freshly provisioned reference.
        "13-constraint-trigger" = @"
SELECT 'constraint-trigger|' || n.nspname || '.' || rel.relname || '|' || con.conname
    || '|' || con.contype::text
    || '|enabled=' || string_agg(DISTINCT tg.tgenabled::text, ',' ORDER BY tg.tgenabled::text)
    || '|triggers=' || COUNT(*)::text
FROM pg_trigger tg
JOIN pg_constraint con ON con.oid = tg.tgconstraint
JOIN pg_class rel ON rel.oid = tg.tgrelid
JOIN pg_namespace n ON n.oid = rel.relnamespace
WHERE n.nspname IN ($SchemaList) AND tg.tgconstraint <> 0
GROUP BY n.nspname, rel.relname, con.conname, con.contype
ORDER BY n.nspname, rel.relname, con.conname;
"@

        # Section 08 hashes pg_get_functiondef, which reports the routine's body, language, volatility
        # and its SECURITY DEFINER/INVOKER word -- but never who owns it and never who may execute it.
        # For a SECURITY DEFINER routine the owner IS the privilege it runs with, so a snapshot that
        # omits it says two databases agree while one of them executes the same body as a different
        # role. proconfig is here for the same reason: the enqueue functions pin search_path, which is
        # what makes running as a definer safe, and it is the kind of attribute an ALTER FUNCTION can
        # drop without touching a line of the body.
        "14-routine-security" = @"
SELECT 'routine-security|' || n.nspname || '.' || p.proname || '|' || pg_get_function_identity_arguments(p.oid)
    || '|kind=' || p.prokind::text
    || '|owner=' || pg_get_userbyid(p.proowner)
    || '|security=' || CASE WHEN p.prosecdef THEN 'DEFINER' ELSE 'INVOKER' END
    || '|config=' || COALESCE(array_to_string(p.proconfig, ','), '')
FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname IN ($SchemaList)
ORDER BY n.nspname, p.proname, pg_get_function_identity_arguments(p.oid);
"@

        # proacl is read through COALESCE(..., acldefault(...)) rather than on its own, because NULL
        # there is not "no privileges" -- it is the PostgreSQL default, which grants EXECUTE to
        # PUBLIC. --no-privileges restores exactly that NULL, so the difference between a locked-down
        # routine and one the whole cluster may execute is the difference between an explicit acl and
        # an absent one, and only the expanded form states it. `source` keeps the two distinguishable
        # after expansion: an acl that was never set and one explicitly granted the same privileges
        # are not the same object, and collapsing them would be the over-normalization this section
        # exists to avoid. One row per routine, aggregated in a fixed order, so a routine stays on one
        # line and the diff names it; '<none>' rather than a missing row when every privilege has been
        # revoked, so a routine can never drop out of this section silently.
        "15-routine-grant" = @"
SELECT 'routine-grant|' || n.nspname || '.' || p.proname || '|' || pg_get_function_identity_arguments(p.oid)
    || '|source=' || CASE WHEN p.proacl IS NULL THEN 'default' ELSE 'explicit' END
    || '|acl=' || COALESCE((
        SELECT string_agg(
            (CASE WHEN acl.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(acl.grantee) END)
                || '=' || acl.privilege_type
                || CASE WHEN acl.is_grantable THEN '*' ELSE '' END
                || '/' || pg_get_userbyid(acl.grantor),
            ',' ORDER BY (CASE WHEN acl.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(acl.grantee) END),
                         acl.privilege_type, pg_get_userbyid(acl.grantor), acl.is_grantable)
        FROM aclexplode(COALESCE(p.proacl, acldefault('f', p.proowner))) AS acl), '<none>')
FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname IN ($SchemaList)
ORDER BY n.nspname, p.proname, pg_get_function_identity_arguments(p.oid);
"@

        # The schema-level half of the same GRANT block: the enqueue owner role is granted USAGE on
        # dms and deliberately not CREATE, and the DDL revokes the CREATE it borrows while it repairs
        # function ownership. --no-privileges drops that grant with all the others, and section 10
        # cannot see it -- role_table_grants reports privileges on tables, not on the schema that
        # holds them. Same expansion and same `source` marker as section 15, for the same reasons.
        "16-schema-privilege" = @"
SELECT 'schema-privilege|' || n.nspname
    || '|owner=' || pg_get_userbyid(n.nspowner)
    || '|source=' || CASE WHEN n.nspacl IS NULL THEN 'default' ELSE 'explicit' END
    || '|acl=' || COALESCE((
        SELECT string_agg(
            (CASE WHEN acl.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(acl.grantee) END)
                || '=' || acl.privilege_type
                || CASE WHEN acl.is_grantable THEN '*' ELSE '' END
                || '/' || pg_get_userbyid(acl.grantor),
            ',' ORDER BY (CASE WHEN acl.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(acl.grantee) END),
                         acl.privilege_type, pg_get_userbyid(acl.grantor), acl.is_grantable)
        FROM aclexplode(COALESCE(n.nspacl, acldefault('n', n.nspowner))) AS acl), '<none>')
FROM pg_namespace n
WHERE n.nspname IN ($SchemaList)
ORDER BY n.nspname;
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
                # Section 04 carries pg_get_constraintdef text, the one place the dump round trip
                # respells an expression without changing it.
                if ($section -eq "04-constraint") {
                    $text = ConvertTo-CanonicalConstraintDefinition -Definition $text
                }
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

$missingSchema = [System.Collections.Generic.List[string]]::new()
foreach ($databaseName in $Database) {
    foreach ($name in (Get-MissingSchema -DatabaseName $databaseName -Name $Schema `
                -ContainerName $Container -User $PostgresUser)) {
        $missingSchema.Add("$name (in $databaseName)")
    }
}

if ($missingSchema.Count -gt 0) {
    throw "Requested schema(s) not present: $($missingSchema -join ', '). A snapshot scoped to a schema that does not exist emits no rows for it, so the compare would report PASS having examined nothing."
}

Write-Output "Preflight: every requested schema exists in every requested database."

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

$difference = Compare-Object -ReferenceObject $leftLine -DifferenceObject $rightLine -CaseSensitive

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
