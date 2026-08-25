# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Coverage for the security half of Compare-DmsSchemaSnapshot.ps1, and for the restore recipe step
# that repairs what the restore drops.
#
# WHAT THIS PROVES: a snapshot that reports two databases equivalent has actually compared the
# PostgreSQL security attributes DMS depends on, and the README's restore recipe puts those
# attributes back before it claims equivalence. The recipe loads with
# pg_restore --no-owner --no-privileges, which silently drops object ownership and every GRANT the
# DDL issued. The DMS document-projection enqueue functions are SECURITY DEFINER, owned by a
# dedicated role, with EXECUTE revoked from PUBLIC, so a bare restore leaves a function that still
# runs as a definer while owned by the superuser and executable by the whole cluster.
# pg_get_functiondef carries none of that, so a definition-only snapshot hashes both databases
# identically and reports PASS. Step 5b of the recipe re-applies the DDL's Security and Grants
# phase; the structural Describe below holds that block statement by statement to the emitter's
# authoritative fixture, and the live Describe runs dump -> bare restore -> 5b -> compare.
#
# The structural Describes read the real query map out of the script, the real recipe out of the
# README and the real Phase 9 out of the DDL fixture (no database) and run everywhere. The live
# Describes are OPT-IN, in the same convention as MssqlPhysicalDistinctnessLive.Tests.ps1: they
# self-skip when the fixture variable is unset, so every CI lane and hermetic run touches no docker.
#   DMS_NORTHRIDGE_PG_FIXTURE_CONTAINER  name of a running, DISPOSABLE PostgreSQL container
#   DMS_NORTHRIDGE_PG_FIXTURE_USER       superuser for psql (optional; defaults to postgres)
# DISPOSABLE is not decoration: the fixtures build and drop their own databases and cluster roles,
# because the ownership and privilege shapes under test are the subject and cannot be borrowed
# from a shared server.

BeforeDiscovery {
    $script:pgFixtureEnabled = -not [string]::IsNullOrWhiteSpace($env:DMS_NORTHRIDGE_PG_FIXTURE_CONTAINER)
}

BeforeAll {
    $script:northridgeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $script:compareScript = Join-Path $script:northridgeRoot "Compare-DmsSchemaSnapshot.ps1"

    # Compare-DmsSchemaSnapshot.ps1 has mandatory parameters and runs its work at file scope, so its
    # helpers are lifted out by AST rather than dot-sourced -- the same extraction style the
    # docker-compose suites use against build-dms.ps1.
    function script:Get-ScriptFunctionText {
        param(
            [Parameter(Mandatory)] [string] $ScriptPath,
            [Parameter(Mandatory)] [string] $FunctionName
        )
        $parseError = $null
        $token = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$token, [ref]$parseError)
        if ($parseError.Count -gt 0) {
            throw "'$ScriptPath' does not parse: $(($parseError | ForEach-Object { $_.Message }) -join '; ')"
        }
        $functionAst = $ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $FunctionName
            }, $true) | Select-Object -First 1
        if ($null -eq $functionAst) { throw "Function '$FunctionName' was not found in '$ScriptPath'." }
        return $functionAst.Extent.Text
    }

    . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:compareScript -FunctionName "Get-SchemaLiteralList")))
    . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:compareScript -FunctionName "Get-SnapshotQueryMap")))
    . ([scriptblock]::Create((Get-ScriptFunctionText -ScriptPath $script:compareScript -FunctionName "ConvertTo-CanonicalConstraintDefinition")))
    $script:exportSnapshotText = Get-ScriptFunctionText -ScriptPath $script:compareScript -FunctionName "Export-SchemaSnapshot"

    $script:queryMap = Get-SnapshotQueryMap -SchemaList (Get-SchemaLiteralList -Name @("dms", "edfi"))

    # The recipe under test, read from the README the consumer reads. The repair block is the SQL the
    # recipe writes between <<'REPAIR_SQL' and REPAIR_SQL, so the live scenario runs exactly the
    # documented statements rather than a copy that could drift from them.
    $readme = Get-Content -Raw -LiteralPath (Join-Path $script:northridgeRoot "README.md")
    $script:recipe = [regex]::Match($readme, '(?ms)^```shell\r?\n(?<recipe>.*?)^```').Groups["recipe"].Value
    # The heredoc line carries its own `|| { ...; exit 1; }` guard after the tag; the body starts on
    # the next line.
    $script:repairSql = [regex]::Match($script:recipe, "(?ms)<<'REPAIR_SQL'[^\r\n]*\r?\n(?<sql>.*?)^REPAIR_SQL\s*$").Groups["sql"].Value
    # The recipe with its comment lines removed, for assertions about what it runs rather than what
    # it explains -- the comments name pg_dump and nextval() in order to say why they are not used.
    $script:activeRecipe = (($script:recipe -split "\r?\n") | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"

    # Step 6 of the recipe, the content gate, as the SQL its heredoc feeds psql; and the sequences
    # Copy-NorthridgeDataForward.ps1 restores from the archive and asserts, read from that script so
    # the gate is held to the copy tool rather than to a list restated here.
    $step6 = [regex]::Match($script:recipe, '(?ms)^# 6\. Verify the restore.*?(?=^# 7\. )').Value
    $script:contentGateSql = [regex]::Match($step6, "(?ms)<<'SQL'\r?\n(?<sql>.*?)^SQL\s*$").Groups["sql"].Value
    $copyTool = Get-Content -Raw -LiteralPath (Join-Path $script:northridgeRoot "Copy-NorthridgeDataForward.ps1")
    $script:copyToolSequence = @([regex]::Matches(
            [regex]::Match($copyTool, '(?ms)\$script:DmsSequence = @\((?<list>.*?)\)').Groups["list"].Value,
            '"(?<name>[^"]+)"') | ForEach-Object { $_.Groups["name"].Value })
    $script:copyToolCollectionPredicate = [regex]::Match($copyTool,
        "(?m)^WHERE (?<predicate>column_name = 'CollectionItemId' AND table_schema IN \([^)]*\));").Groups["predicate"].Value

    # Phase 9 ("Security and Grants") of the emitted DMS PostgreSQL DDL, read from the emitter's
    # authoritative DS 5.2 fixture -- the artifact's own schema set -- rather than restated here. The
    # phase ends where the resource schemas begin.
    $ddlFixturePath = [System.IO.Path]::GetFullPath((Join-Path $script:northridgeRoot "../../src/dms/backend/Fixtures/authoritative/ds-5.2/expected/pgsql.sql"))
    $ddl = Get-Content -Raw -LiteralPath $ddlFixturePath
    $script:securityPhase = [regex]::Match($ddl, '(?ms)^-- Phase 9: Security and Grants\r?\n-- =+\r?\n(?<body>.*?)(?=^CREATE SCHEMA IF NOT EXISTS )').Groups["body"].Value

    # The statements of a security block that change catalog state, in order: the top-level GRANT /
    # REVOKE / ALTER FUNCTION / SET ROLE / RESET ROLE lines, plus the GRANT / REVOKE / ALTER FUNCTION
    # text the DDL's DO blocks run through EXECUTE. The role-creation and membership checks are
    # deliberately not among them: the recipe requires the role to exist already and refuses to
    # create one. Whitespace is collapsed so a re-wrapped line still compares equal.
    function script:Get-SecurityStatement {
        param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Sql)
        $statement = [System.Collections.Generic.List[string]]::new()
        foreach ($line in ($Sql -split "\r?\n")) {
            $trimmed = $line.Trim()
            if ($line -match '^(GRANT|REVOKE|ALTER FUNCTION|SET ROLE|RESET ROLE)\b' -and $trimmed.EndsWith(";")) {
                $statement.Add(($trimmed -replace '\s+', ' '))
            }
            foreach ($executed in [regex]::Matches($line, "EXECUTE '((?:GRANT|REVOKE|ALTER FUNCTION)[^']*)'")) {
                $statement.Add((($executed.Groups[1].Value.Trim() + ";") -replace '\s+', ' '))
            }
        }
        return @($statement)
    }

    # Fixture access shared by the live Describes: the same container and superuser, the same psql
    # transport, and one compare helper that reads the diff file the recipe itself keeps.
    $script:container = $env:DMS_NORTHRIDGE_PG_FIXTURE_CONTAINER
    $script:pgUser =
        if ([string]::IsNullOrWhiteSpace($env:DMS_NORTHRIDGE_PG_FIXTURE_USER)) { "postgres" }
        else { $env:DMS_NORTHRIDGE_PG_FIXTURE_USER }

    function script:Invoke-FixtureSql {
        param(
            [Parameter(Mandatory)] [string] $DatabaseName,
            [Parameter(Mandatory)] [string] $Sql
        )
        $output = $Sql | docker exec -i $script:container psql -U $script:pgUser -d $DatabaseName `
            -v ON_ERROR_STOP=1 --quiet 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "fixture psql failed against '$DatabaseName' (exit $LASTEXITCODE): $($output -join [Environment]::NewLine)"
        }
    }

    # A failing compare throws, so its output is collected through the pipeline as it is produced:
    # assigning the whole invocation to a variable would discard everything the script wrote before
    # the terminating error. The written diff file is the artifact the recipe keeps and is what the
    # per-drift assertions read.
    function script:Invoke-Compare {
        param([Parameter(Mandatory)] [ValidateCount(2, 2)] [string[]] $DatabaseName)
        $outputDirectory = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
        $captured = [System.Collections.Generic.List[string]]::new()
        $failed = $false
        try {
            & $script:compareScript -Database $DatabaseName -OutputDirectory $outputDirectory `
                -Schema "dms" -Container $script:container -PostgresUser $script:pgUser 2>&1 |
                ForEach-Object { $captured.Add([string]$_) }
        }
        catch {
            $failed = $true
            $captured.Add([string]$_)
        }
        $diffPath = Join-Path $outputDirectory "schema-diff.$($DatabaseName[0])-vs-$($DatabaseName[1]).txt"
        $diff = if (Test-Path -LiteralPath $diffPath) { Get-Content -LiteralPath $diffPath -Raw } else { "" }
        return [pscustomobject]@{
            Failed = $failed
            Output = ($captured -join [Environment]::NewLine)
            Diff   = $diff
        }
    }
}

Describe "Snapshot query map covers PostgreSQL routine security" {
    It "captures routine identity, owner, definer state and configuration" {
        # The four attributes the finding names, plus proconfig: the enqueue functions pin
        # search_path, which is what makes running as a definer safe, and ALTER FUNCTION can drop it
        # without touching a line of the body.
        $sql = $script:queryMap["14-routine-security"]
        $sql | Should -Not -BeNullOrEmpty -Because "a routine-security section must exist"
        $sql | Should -Match "pg_get_function_identity_arguments" -Because "overloads are only distinguishable by signature"
        $sql | Should -Match "pg_get_userbyid\(p\.proowner\)" -Because "a SECURITY DEFINER routine runs as its owner"
        $sql | Should -Match "p\.prosecdef"
        $sql | Should -Match "'DEFINER'" -Because "the definer state must be readable in the diff, not encoded"
        $sql | Should -Match "'INVOKER'"
        $sql | Should -Match "p\.proconfig"
    }

    It "expands routine EXECUTE privileges through the PostgreSQL default rather than reading proacl alone" {
        # This is the exact false-pass. --no-privileges restores proacl as NULL, and NULL is not
        # "no privileges": it is the built-in default, which grants EXECUTE to PUBLIC. Reading the
        # column on its own would report a wide-open routine as having no grants at all.
        $sql = $script:queryMap["15-routine-grant"]
        $sql | Should -Not -BeNullOrEmpty -Because "a routine-grant section must exist"
        $sql | Should -Match "aclexplode" -Because "an aclitem array has to be expanded to be compared"
        $sql | Should -Match "acldefault\('f', p\.proowner\)" -Because "NULL proacl means the default ACL, which grants EXECUTE to PUBLIC"
        $sql | Should -Match "COALESCE\(p\.proacl," -Because "the default must stand in for a NULL proacl, not be skipped"
        $sql | Should -Match "'PUBLIC'" -Because "grantee 0 is PUBLIC and must be named as such in the diff"
        $sql | Should -Match "'<none>'" -Because "a routine whose privileges are all revoked must still emit a row"
    }

    It "captures schema ownership and schema-level privileges" {
        # The schema half of the same GRANT block: the enqueue owner is granted USAGE on dms and
        # deliberately not CREATE. information_schema.role_table_grants (section 10) reports
        # privileges on tables, never on the schema that holds them.
        $sql = $script:queryMap["16-schema-privilege"]
        $sql | Should -Not -BeNullOrEmpty -Because "a schema-privilege section must exist"
        $sql | Should -Match "pg_get_userbyid\(n\.nspowner\)"
        $sql | Should -Match "aclexplode"
        $sql | Should -Match "acldefault\('n', n\.nspowner\)"
        $sql | Should -Match "COALESCE\(n\.nspacl,"
    }

    It "captures relation and sequence ownership, the other half of what --no-owner drops" {
        $script:queryMap["02-table"] | Should -Match "pg_get_userbyid\(c\.relowner\)"
        $script:queryMap["06-sequence"] | Should -Match "sequenceowner"
    }

    It "captures whether a table privilege was granted WITH GRANT OPTION" {
        # The same privilege_type with the grant option is a wider grant, and sections 15 and 16
        # already carry grantability for routines and schemas; a table grant that gained it would
        # otherwise compare equal to one that did not.
        $script:queryMap["10-grant"] | Should -Match "'\|grantable=' \|\| is_grantable" -Because "grant option drift must be visible in the row"
        $script:queryMap["10-grant"] | Should -Match "(?s)ORDER BY .*, is_grantable;" -Because "the flag must take part in the total order"
    }

    It "captures who granted a table privilege" {
        # PostgreSQL records the grantor in every ACL entry, and step 5b runs its REVOKEs as the owner
        # role precisely so that grantor matches a fresh deployment. Two grants alike in table, grantee,
        # privilege and grant option but not in grantor are two grants, as sections 15 and 16 already
        # read them for routines and schemas.
        $script:queryMap["10-grant"] | Should -Match "'\|grantor=' \|\| grantor" -Because "grantor drift must be visible in the row"
        $script:queryMap["10-grant"] | Should -Match "(?s)ORDER BY .*privilege_type, grantor, is_grantable;" -Because "the grantor must take part in the total order"
    }

    It "distinguishes an absent ACL from an explicitly granted identical one" {
        # Guards against over-normalizing: a routine whose ACL was never set and one explicitly
        # granted the same privileges are not the same object, and after expansion they would read
        # alike without this marker.
        foreach ($section in @("15-routine-grant", "16-schema-privilege")) {
            $script:queryMap[$section] | Should -Match "'default'" -Because "$section must mark a NULL ACL"
            $script:queryMap[$section] | Should -Match "'explicit'" -Because "$section must mark a set ACL"
        }
    }

    It "orders every section totally, so two runs of the same database are byte-comparable" {
        # Applies to every section, present and future: an unordered query makes the whole snapshot
        # non-deterministic and a textual diff meaningless.
        foreach ($section in $script:queryMap.Keys) {
            $script:queryMap[$section] | Should -Match "(?s)ORDER BY" -Because "section $section must order its rows"
        }
    }

    It "aggregates the privilege sections in a fixed order" {
        # string_agg without ORDER BY is free to emit its inputs in any order, which would make the
        # aggregated ACL text differ between runs of the same database.
        foreach ($section in @("15-routine-grant", "16-schema-privilege")) {
            $script:queryMap[$section] | Should -Match "(?s)string_agg\(.*ORDER BY" -Because "section $section must aggregate deterministically"
        }
    }
}

Describe "Schema compare fails on restored security drift" -Skip:(-not $script:pgFixtureEnabled) {
    BeforeAll {
        $script:ownerRole = "dms_nr_fixture_owner"
        # A second role that holds SELECT WITH GRANT OPTION, so a grant can be recorded with a grantor
        # other than the superuser and only the grantor differs between two databases.
        $script:grantorRole = "dms_nr_fixture_grantor"
        $script:fixtureDatabase = @(
            "dms_nr_fx_base",
            "dms_nr_fx_clone",
            "dms_nr_fx_owner",
            "dms_nr_fx_secdef",
            "dms_nr_fx_exec"
        )

        function script:Remove-Fixture {
            foreach ($name in $script:fixtureDatabase) {
                docker exec $script:container dropdb -U $script:pgUser --maintenance-db=postgres --if-exists -- $name 2>&1 | Out-Null
            }
            Invoke-FixtureSql -DatabaseName "postgres" -Sql "DROP ROLE IF EXISTS $script:ownerRole; DROP ROLE IF EXISTS $script:grantorRole;"
        }

        # The shape every fixture database shares. Sections 11 and 12 read dms."EffectiveSchema" and
        # dms."SchemaComponent" by name, so they have to exist for the snapshot to run at all.
        $script:commonDdl = @"
CREATE SCHEMA dms;
CREATE TABLE dms."EffectiveSchema"("EffectiveSchemaSingletonId" int PRIMARY KEY, "ApiSchemaFormatVersion" text, "EffectiveSchemaHash" text, "ResourceKeyCount" int, "ResourceKeySeedHash" bytea);
CREATE TABLE dms."SchemaComponent"("EffectiveSchemaHash" text, "ProjectEndpointName" text, "ProjectName" text, "ProjectVersion" text, "IsExtensionProject" boolean);
GRANT CREATE ON SCHEMA dms TO $script:ownerRole;
"@

        # The locked-down shape a fresh provision produces: SECURITY DEFINER, owned by a dedicated
        # role, search_path pinned, EXECUTE revoked from PUBLIC, USAGE but not CREATE on the schema.
        $script:lockedDdl = @"
$script:commonDdl
CREATE FUNCTION dms.probe() RETURNS int LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog AS 'SELECT 1';
ALTER FUNCTION dms.probe() OWNER TO $script:ownerRole;
REVOKE CREATE ON SCHEMA dms FROM $script:ownerRole;
GRANT USAGE ON SCHEMA dms TO $script:ownerRole;
SET ROLE $script:ownerRole;
REVOKE EXECUTE ON FUNCTION dms.probe() FROM PUBLIC;
RESET ROLE;
"@

        # Identical body, identical definer state, identical EXECUTE revoke -- only the owner differs.
        $script:ownerDriftDdl = @"
$script:commonDdl
CREATE FUNCTION dms.probe() RETURNS int LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog AS 'SELECT 1';
REVOKE CREATE ON SCHEMA dms FROM $script:ownerRole;
GRANT USAGE ON SCHEMA dms TO $script:ownerRole;
REVOKE EXECUTE ON FUNCTION dms.probe() FROM PUBLIC;
"@

        # Identical body and owner -- only the definer state differs.
        $script:securityDriftDdl = @"
$script:commonDdl
CREATE FUNCTION dms.probe() RETURNS int LANGUAGE sql SECURITY INVOKER SET search_path = pg_catalog AS 'SELECT 1';
ALTER FUNCTION dms.probe() OWNER TO $script:ownerRole;
REVOKE CREATE ON SCHEMA dms FROM $script:ownerRole;
GRANT USAGE ON SCHEMA dms TO $script:ownerRole;
SET ROLE $script:ownerRole;
REVOKE EXECUTE ON FUNCTION dms.probe() FROM PUBLIC;
RESET ROLE;
"@

        # The REVOKE never happened -- exactly what --no-privileges leaves behind.
        $script:executeDriftDdl = @"
$script:commonDdl
CREATE FUNCTION dms.probe() RETURNS int LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog AS 'SELECT 1';
ALTER FUNCTION dms.probe() OWNER TO $script:ownerRole;
REVOKE CREATE ON SCHEMA dms FROM $script:ownerRole;
GRANT USAGE ON SCHEMA dms TO $script:ownerRole;
"@

        Remove-Fixture
        Invoke-FixtureSql -DatabaseName "postgres" -Sql "CREATE ROLE $script:ownerRole NOLOGIN; CREATE ROLE $script:grantorRole NOLOGIN;"
        foreach ($name in $script:fixtureDatabase) {
            $created = docker exec $script:container createdb -U $script:pgUser --maintenance-db=postgres -- $name 2>&1
            if ($LASTEXITCODE -ne 0) { throw "could not create fixture database '$name': $created" }
        }

        Invoke-FixtureSql -DatabaseName "dms_nr_fx_base" -Sql $script:lockedDdl
        # An independently built database of the same shape: equivalent inputs must still report PASS.
        Invoke-FixtureSql -DatabaseName "dms_nr_fx_clone" -Sql $script:lockedDdl
        # One drift per database, so each assertion names exactly one cause.
        Invoke-FixtureSql -DatabaseName "dms_nr_fx_owner" -Sql $script:ownerDriftDdl
        Invoke-FixtureSql -DatabaseName "dms_nr_fx_secdef" -Sql $script:securityDriftDdl
        Invoke-FixtureSql -DatabaseName "dms_nr_fx_exec" -Sql $script:executeDriftDdl
    }

    AfterAll {
        Remove-Fixture
    }

    It "reports PASS for two independently built databases of the same shape" {
        # The guard against over-sensitivity: the new sections must not manufacture a difference
        # between databases that really are equivalent.
        $result = Invoke-Compare -DatabaseName @("dms_nr_fx_base", "dms_nr_fx_clone")
        $result.Failed | Should -BeFalse -Because "equivalent databases must not fail: $($result.Output)"
        $result.Output | Should -Match "PASS: schema snapshots"
    }

    It "fails when a routine keeps its body but loses its owner" {
        # The narrowest case, and the one nothing else catches: pg_get_functiondef does not carry the
        # owner, so section 08 hashes these two databases identically.
        $result = Invoke-Compare -DatabaseName @("dms_nr_fx_base", "dms_nr_fx_owner")
        $result.Failed | Should -BeTrue -Because "lost function ownership must fail the compare"
        $result.Diff | Should -Match "routine-security" -Because "the diff must name the section: $($result.Output)"
        $result.Diff | Should -Match "owner=$script:ownerRole"
        $result.Diff | Should -Match "owner=$script:pgUser"
        $result.Diff | Should -Not -Match "08-routine\|" -Because "the body is unchanged, so only the security sections may differ: $($result.Diff)"
    }

    It "fails when a routine keeps its body but is no longer SECURITY DEFINER" {
        $result = Invoke-Compare -DatabaseName @("dms_nr_fx_base", "dms_nr_fx_secdef")
        $result.Failed | Should -BeTrue -Because "a lost SECURITY DEFINER must fail the compare"
        $result.Diff | Should -Match "security=DEFINER" -Because "the diff must name the state: $($result.Output)"
        $result.Diff | Should -Match "security=INVOKER"
    }

    It "fails when a routine keeps its body but its EXECUTE privileges differ" {
        # The restored side has proacl NULL, which is the PostgreSQL default and grants EXECUTE to
        # PUBLIC. Nothing but section 15 sees it.
        $result = Invoke-Compare -DatabaseName @("dms_nr_fx_base", "dms_nr_fx_exec")
        $result.Failed | Should -BeTrue -Because "lost EXECUTE grants must fail the compare"
        $result.Diff | Should -Match "routine-grant" -Because "the diff must name the section: $($result.Output)"
        $result.Diff | Should -Match "PUBLIC=EXECUTE"
        $result.Diff | Should -Not -Match "08-routine\|" -Because "the body is unchanged, so only the privilege section may differ: $($result.Diff)"
    }

    It "fails when a schema loses the USAGE grant the enqueue owner depends on" {
        # The schema-level half of the same GRANT block. Applied and reverted here rather than given
        # its own database, so the only difference from the baseline is the schema grant itself.
        Invoke-FixtureSql -DatabaseName "dms_nr_fx_clone" -Sql "REVOKE USAGE ON SCHEMA dms FROM $script:ownerRole;"
        try {
            $result = Invoke-Compare -DatabaseName @("dms_nr_fx_base", "dms_nr_fx_clone")
            $result.Failed | Should -BeTrue -Because "a lost schema grant must fail the compare"
            $result.Diff | Should -Match "schema-privilege" -Because "the diff must name the section: $($result.Output)"
            $result.Diff | Should -Match "$script:ownerRole=USAGE"
        }
        finally {
            Invoke-FixtureSql -DatabaseName "dms_nr_fx_clone" -Sql "GRANT USAGE ON SCHEMA dms TO $script:ownerRole;"
        }
    }

    It "fails when a table grant differs only in its grant option" {
        # Both databases hold the same SELECT grant; only the clone's carries WITH GRANT OPTION.
        # Section 10 has to read that as a difference, and the diff has to say which side may re-grant.
        Invoke-FixtureSql -DatabaseName "dms_nr_fx_base" -Sql "GRANT SELECT ON dms.""SchemaComponent"" TO $script:ownerRole;"
        Invoke-FixtureSql -DatabaseName "dms_nr_fx_clone" -Sql "GRANT SELECT ON dms.""SchemaComponent"" TO $script:ownerRole WITH GRANT OPTION;"
        try {
            $result = Invoke-Compare -DatabaseName @("dms_nr_fx_base", "dms_nr_fx_clone")
            $result.Failed | Should -BeTrue -Because "a grant option is a wider privilege and must fail the compare: $($result.Output)"
            $result.Diff | Should -Match "`t10-grant\|grant\|dms\.SchemaComponent\|$script:ownerRole\|SELECT\|grantor=$script:pgUser\|grantable=NO" -Because "the diff must show the plain grant: $($result.Diff)"
            $result.Diff | Should -Match "`t10-grant\|grant\|dms\.SchemaComponent\|$script:ownerRole\|SELECT\|grantor=$script:pgUser\|grantable=YES" -Because "the diff must show the grant option: $($result.Diff)"
        }
        finally {
            foreach ($name in @("dms_nr_fx_base", "dms_nr_fx_clone")) {
                Invoke-FixtureSql -DatabaseName $name -Sql "REVOKE SELECT ON dms.""SchemaComponent"" FROM $script:ownerRole;"
            }
        }
    }

    It "fails when a table grant differs only in who granted it" {
        # Step 5b runs its REVOKEs as the owner role precisely so the recorded grantor matches a fresh
        # deployment; a section that keyed a grant by table, grantee, privilege and grant option alone
        # would read two grants from different grantors as one. Both databases give the grantor role
        # USAGE on the schema and SELECT WITH GRANT OPTION on the table, and the owner role SELECT --
        # from the superuser on the base, from the grantor role on the clone -- so the only difference
        # is the grantor recorded on the owner role's grant.
        foreach ($name in @("dms_nr_fx_base", "dms_nr_fx_clone")) {
            Invoke-FixtureSql -DatabaseName $name -Sql "GRANT USAGE ON SCHEMA dms TO $script:grantorRole; GRANT SELECT ON dms.""SchemaComponent"" TO $script:grantorRole WITH GRANT OPTION;"
        }
        Invoke-FixtureSql -DatabaseName "dms_nr_fx_base" -Sql "GRANT SELECT ON dms.""SchemaComponent"" TO $script:ownerRole;"
        Invoke-FixtureSql -DatabaseName "dms_nr_fx_clone" -Sql "SET ROLE $script:grantorRole; GRANT SELECT ON dms.""SchemaComponent"" TO $script:ownerRole; RESET ROLE;"
        try {
            $result = Invoke-Compare -DatabaseName @("dms_nr_fx_base", "dms_nr_fx_clone")
            $result.Failed | Should -BeTrue -Because "a different grantor is a different grant: $($result.Output)"
            $result.Diff | Should -Match "`t10-grant\|grant\|dms\.SchemaComponent\|$script:ownerRole\|SELECT\|grantor=$script:pgUser\|grantable=NO" -Because "the base's grant came from the superuser: $($result.Diff)"
            $result.Diff | Should -Match "`t10-grant\|grant\|dms\.SchemaComponent\|$script:ownerRole\|SELECT\|grantor=$script:grantorRole\|grantable=NO" -Because "the clone's grant came from the grantor role: $($result.Diff)"
            $result.Diff | Should -Not -Match "\|$script:grantorRole\|SELECT\|" -Because "the grantor role's own grant is the same on both sides and must not read as drift: $($result.Diff)"
        }
        finally {
            # Revoking the grantor role's privilege cascades to the grant it made; the superuser's grant
            # to the owner role on the base is revoked on its own.
            foreach ($name in @("dms_nr_fx_base", "dms_nr_fx_clone")) {
                Invoke-FixtureSql -DatabaseName $name -Sql "REVOKE SELECT ON dms.""SchemaComponent"" FROM $script:grantorRole CASCADE; REVOKE SELECT ON dms.""SchemaComponent"" FROM $script:ownerRole; REVOKE USAGE ON SCHEMA dms FROM $script:grantorRole;"
            }
        }
    }

    It "writes a byte-identical snapshot on a repeated run of the same database" {
        # Determinism is what makes a textual diff meaningful; a snapshot that varied run to run
        # would report differences that mean nothing and hide the ones that do.
        $first = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
        $second = Join-Path $TestDrive ([Guid]::NewGuid().ToString("N"))
        foreach ($directory in @($first, $second)) {
            & $script:compareScript -Database "dms_nr_fx_base" -OutputDirectory $directory `
                -Schema "dms" -Container $script:container -PostgresUser $script:pgUser | Out-Null
        }
        $firstBytes = [System.IO.File]::ReadAllBytes((Join-Path $first "schema-snapshot.dms_nr_fx_base.txt"))
        $secondBytes = [System.IO.File]::ReadAllBytes((Join-Path $second "schema-snapshot.dms_nr_fx_base.txt"))
        $firstBytes.Length | Should -BeGreaterThan 0 -Because "an empty snapshot would compare equal to another empty one"
        [System.Convert]::ToBase64String($secondBytes) | Should -BeExactly ([System.Convert]::ToBase64String($firstBytes))
    }
}

Describe "Restore recipe repairs the security metadata the compare checks" {
    # The README's restore uses pg_restore --no-owner --no-privileges, and the compare reads exactly
    # the ownership and privileges those flags drop. The two are consistent only because the recipe
    # re-applies the DDL's Security and Grants phase (step 5b) and proves the result against the
    # deployment itself, set aside by a rename before the restore (step 5c). These cases hold the
    # recipe to the emitter: the repair block must be the DDL's own statements, no more and no fewer,
    # in the DDL's order, and the rename, the restore, the repair and the proof must sit in that order.

    It "restores with the flags that drop ownership and privileges, then repairs, then proves" {
        $script:recipe | Should -Match '(?m)^docker exec dms-postgresql pg_restore -U "\$DBUSER" -d "\$DB" --no-owner --no-privileges'
        $script:repairSql | Should -Not -BeNullOrEmpty -Because "the recipe must write a REPAIR_SQL block"

        $restoreAt = $script:recipe.IndexOf('pg_restore -U "$DBUSER" -d "$DB"')
        $repairAt = $script:recipe.IndexOf("<<'REPAIR_SQL'")
        $proofAt = $script:recipe.IndexOf("# 5c.")
        $contentCheckAt = $script:recipe.IndexOf("# 6. Verify the restore")
        $restoreAt | Should -BeGreaterThan -1
        $repairAt | Should -BeGreaterThan $restoreAt -Because "the repair must follow the restore it repairs"
        $proofAt | Should -BeGreaterThan $repairAt -Because "the proof must follow the repair"
        $contentCheckAt | Should -BeGreaterThan $proofAt -Because "the content checks must run on a proven database"
    }

    It "applies the repair block to the restored artifact only, transactionally and fail-closed" {
        # The reference is the deployment itself and needs no repair; repairing it would mask a block
        # incomplete in the same way on both sides. The one application reads the file the heredoc
        # wrote and runs under -1 (one transaction) and ON_ERROR_STOP (abort at the first error).
        # The write is guarded too: a heredoc that fails to land must stop the recipe rather than let
        # a stale repair.sql from an earlier attempt be applied in its place.
        $script:recipe | Should -Match '(?m)^cat > "\$ART/repair\.sql" <<''REPAIR_SQL'' \|\| \{ .*; exit 1; \}$' -Because "writing the repair block must be fail-closed like applying it"
        $script:repairSql | Should -Match '(?m)^DO \$\$' -Because "the extracted block must start at the SQL, not at the guard"
        $script:repairSql | Should -Not -Match 'exit 1' -Because "the guard must not be read as part of the SQL fed to psql"

        $application = @([regex]::Matches($script:recipe, '(?m)^.*psql .*-f - < "\$ART/repair\.sql".*$') | ForEach-Object { $_.Value })
        $application.Count | Should -Be 1 -Because "the artifact restore is repaired; the deployment must not be touched"
        foreach ($line in $application) {
            $line | Should -Match '-v ON_ERROR_STOP=1' -Because "a failed statement must stop psql, not be reported and skipped: $line"
            $line | Should -Match '\s-1\s' -Because "a partial repair must roll back rather than leave a half-repaired database: $line"
            $line | Should -Match '-U "\$DBUSER"' -Because "the live superuser, not a default: $line"
            $line | Should -Match '-d "\$DB"' -Because "the restored artifact is the database being repaired: $line"
        }
        $script:recipe | Should -Not -Match '-d "\$REF"' -Because "nothing in the recipe may connect to the reference deployment to write to it"

        $script:repairSql | Should -Match "to_regrole\('edfi_dms_enqueue_owner'\) IS NULL" -Because "the guard must name the role the DDL creates"
        $script:repairSql | Should -Match 'RAISE EXCEPTION' -Because "a missing role must stop the recipe"
        $script:repairSql | Should -Not -Match 'CREATE ROLE' -Because "the recipe must not create a role shaped differently from the DDL's"
    }

    It "keeps the fresh deployment as the reference and compares the restored artifact against it" {
        # The reference is the database step 4 deployed, renamed before the artifact is restored --
        # not a dump of it restored beside the artifact, which would have needed the same repair and
        # so could not have caught a repair block wrong in the same way on both sides. The rename is
        # generated server-side from psql variables through format('%I'), so no database name is
        # pasted into SQL text, and it is guarded like every other step.
        $script:activeRecipe | Should -Not -Match 'pg_dump' -Because "the deployment is kept, not dumped and restored"
        $script:activeRecipe | Should -Not -Match 'deployed\.dump'

        $stopAt = $script:recipe.IndexOf('docker stop ed-fi-api ed-fi-api-config-service')
        $referenceAt = $script:recipe.IndexOf('REF="${DB}_reference"')
        $staleDropAt = $script:recipe.IndexOf('dropdb -U "$DBUSER" --maintenance-db=postgres --if-exists -- "$REF"')
        $rename = [regex]::Match($script:recipe, '(?m)^docker exec -i dms-postgresql psql -U "\$DBUSER" -d postgres -v ON_ERROR_STOP=1 -q \\\r?\n\s+-v db="\$DB" -v ref="\$REF" -f - <<''SQL'' \|\| \\\r?\n\s+\{ echo [^\n]*; exit 1; \}\r?\nSELECT format\(''ALTER DATABASE %I RENAME TO %I'', :''db'', :''ref''\) \\gexec\r?\nSQL$')
        $createAt = $script:recipe.IndexOf('createdb -U "$DBUSER" --maintenance-db=postgres -- "$DB"')
        $restoreAt = $script:recipe.IndexOf('pg_restore -U "$DBUSER" -d "$DB"')
        $repairAt = $script:recipe.IndexOf("<<'REPAIR_SQL'")
        $compare = [regex]::Match($script:recipe, '(?m)^.*Compare-DmsSchemaSnapshot\.ps1 -Database \$env:DB, \$env:REF .*-PostgresUser \$env:DBUSER.*$')
        $referenceDropAt = $script:recipe.IndexOf('dropdb -U "$DBUSER" --maintenance-db=postgres -- "$REF"')

        $stopAt | Should -BeGreaterThan -1
        $referenceAt | Should -BeGreaterThan $stopAt -Because "nothing may hold a connection while the deployment is renamed"
        $staleDropAt | Should -BeGreaterThan $referenceAt -Because "a reference left by an earlier attempt must be dropped before the rename can collide with it"
        $rename.Success | Should -BeTrue -Because "the deployment must be renamed through psql variables and format('%I'), fail-closed"
        $rename.Index | Should -BeGreaterThan $staleDropAt
        $createAt | Should -BeGreaterThan $rename.Index -Because "the artifact's database is created only once the deployment is safely out of the way"
        $restoreAt | Should -BeGreaterThan $createAt
        $repairAt | Should -BeGreaterThan $restoreAt
        $compare.Success | Should -BeTrue -Because "the compare must run the script's two-database mode on the live superuser"
        $compare.Index | Should -BeGreaterThan $repairAt -Because "the proof follows the repair"
        $referenceDropAt | Should -BeGreaterThan $compare.Index -Because "the reference is scratch once the compare has passed"

        [regex]::Matches($script:recipe, '(?m)^docker exec dms-postgresql pg_restore ').Count | Should -Be 1 -Because "one restore, the artifact into `$DB; the reference is never restored"
        [regex]::Matches($script:activeRecipe, 'Compare-DmsSchemaSnapshot\.ps1').Count | Should -Be 1 -Because "one live compare, against the deployment itself"
        $script:recipe | Should -Not -Match 'dropdb [^\n]* -- "\$DB"' -Because "the deployment is renamed, never dropped"
    }

    It "never pastes a database name into SQL text" {
        # A supported POSTGRES_DB_NAME need not be a well-behaved identifier. Names reach the server
        # only as dropdb/createdb arguments after `--`, as psql -d connections, or as psql variables
        # quoted server-side by format('%I'); every heredoc that carries SQL is quoted, so nothing
        # the shell holds expands into it, and every inline SQL argument is single-quoted.
        $active = $script:activeRecipe
        foreach ($heredoc in [regex]::Matches($active, '<<\s*(?<tag>\S+)')) {
            $heredoc.Groups["tag"].Value | Should -Match "^'[A-Z_]+'$" -Because "an unquoted heredoc would expand shell variables into SQL: $($heredoc.Value)"
        }
        $joined = $active -replace '\\\n\s*', ' '
        foreach ($line in ($joined -split "\n" | Where-Object { $_ -match '\bpsql\b' -and $_ -match '\s-(c|tAc)\s' })) {
            $line | Should -Match '\s-(c|tAc)\s+''' -Because "an inline SQL argument must be single-quoted so the shell cannot expand into it: $line"
        }
        $active | Should -Not -Match 'ALTER DATABASE\s+"?\$' -Because "the rename must go through format('%I'), never a pasted name"
        foreach ($line in ($joined -split "\n" | Where-Object { $_ -match '\b(dropdb|createdb)\b' })) {
            $line | Should -Match ' -- "\$(DB|REF)"' -Because "dropdb/createdb must take the name as a guarded argument: $line"
        }
    }

    # The two cases below pin the README's REPAIR_SQL block to the emitter's Phase 9, statement for
    # statement and in order. When Phase 9 changes in CoreDdlEmitter -- a new grant, a renamed role, a
    # reordered revoke -- they fail on purpose, and loosening them is not the fix. The coupling has to be
    # settled deliberately: the restore recipe is valid only against the DMS revision the artifact
    # records, so either the README block is updated to the new Phase 9 verbatim alongside an artifact
    # re-published from a deployment at that revision, or the recipe keeps its block and states that it
    # targets the recorded revision only, with the new DDL reached through the copy-forward rather than
    # a restore. In either case regenerate the authoritative fixture (pgsql.sql) rather than editing it,
    # and re-run the live dump -> restore -> repair -> compare Describe below before publishing.
    It "re-applies every statement of the DDL's Security and Grants phase and nothing else" {
        $script:securityPhase | Should -Not -BeNullOrEmpty -Because "the authoritative fixture must carry a Phase 9"
        $ddlStatement = @(Get-SecurityStatement -Sql $script:securityPhase | Select-Object -Unique)
        $recipeStatement = @(Get-SecurityStatement -Sql $script:repairSql | Select-Object -Unique)
        $ddlStatement.Count | Should -BeGreaterThan 5 -Because "the extraction must have found the phase's statements: $($ddlStatement -join ' | ')"

        foreach ($statement in $ddlStatement) {
            $recipeStatement | Should -Contain $statement -Because "the DDL applies it and the restore dropped it"
        }
        foreach ($statement in $recipeStatement) {
            $ddlStatement | Should -Contain $statement -Because "the recipe must not apply anything the DDL does not"
        }
    }

    It "runs the DDL's top-level statements in the DDL's order, with the EXECUTE revokes as the owner role" {
        $ddlTopLevel = @(($script:securityPhase -split "\r?\n") |
                Where-Object { $_ -match '^(GRANT|REVOKE|SET ROLE|RESET ROLE)\b.*;\s*$' } |
                ForEach-Object { $_.Trim() -replace '\s+', ' ' })
        $recipeStatement = @(Get-SecurityStatement -Sql $script:repairSql)
        $ddlTopLevel.Count | Should -BeGreaterThan 5

        $previous = -1
        foreach ($statement in $ddlTopLevel) {
            $index = [array]::IndexOf($recipeStatement, $statement)
            $index | Should -BeGreaterThan $previous -Because "'$statement' must come after the statement the DDL emits before it"
            $previous = $index
        }

        # A fresh deployment records the owner role as grantor because the DDL revokes under SET ROLE;
        # the recipe has to do the same or the ACL differs by grantor. The ownership transfer precedes
        # the revokes: REVOKE as the owner role only works once the role owns the function.
        $setRole = [array]::IndexOf($recipeStatement, 'SET ROLE "edfi_dms_enqueue_owner";')
        $resetRole = [array]::IndexOf($recipeStatement, 'RESET ROLE;')
        $setRole | Should -BeGreaterThan -1
        $resetRole | Should -BeGreaterThan $setRole
        $revoke = @($recipeStatement | Where-Object { $_ -like 'REVOKE EXECUTE ON FUNCTION*' })
        $revoke.Count | Should -Be 4 -Because "PUBLIC and SESSION_USER, on both enqueue functions"
        foreach ($statement in $revoke) {
            $index = [array]::IndexOf($recipeStatement, $statement)
            ($index -gt $setRole -and $index -lt $resetRole) | Should -BeTrue -Because "'$statement' must run under SET ROLE"
        }
        $alterOwner = @($recipeStatement | Where-Object { $_ -like 'ALTER FUNCTION*OWNER TO*' })
        $alterOwner.Count | Should -Be 2
        foreach ($statement in $alterOwner) {
            [array]::IndexOf($recipeStatement, $statement) | Should -BeLessThan $setRole -Because "'$statement' must precede the revokes"
        }
    }
}

Describe "Constraint snapshot normalizes PostgreSQL's two array-cast spellings and nothing else" {
    # pg_get_constraintdef renders CHECK ("Col" IN (...)) on a varchar column as (ARRAY[...])::text[]
    # in the database the DDL deployed, and as ARRAY[(...)::text, ...] in a database restored from a
    # dump of it. Section 04 has to read those as one constraint -- step 5c compares a restore
    # against the deployment itself -- while still reading a changed predicate as a difference. The
    # spellings below are the catalog's own, captured from PostgreSQL 16 before and after a
    # pg_dump/pg_restore round trip of the DDL's dms."DocumentCacheState".
    BeforeAll {
        $script:deployedSpelling = 'CHECK ((("ProjectionLifecycleState")::text = ANY ((ARRAY[''Disabled''::character varying, ''Resetting''::character varying, ''Rebuilding''::character varying, ''Tracking''::character varying])::text[])))'
        $script:restoredSpelling = 'CHECK ((("ProjectionLifecycleState")::text = ANY (ARRAY[(''Disabled''::character varying)::text, (''Resetting''::character varying)::text, (''Rebuilding''::character varying)::text, (''Tracking''::character varying)::text])))'
    }

    It "reads the restored spelling as the deployed one" {
        ConvertTo-CanonicalConstraintDefinition -Definition $script:restoredSpelling | Should -BeExactly $script:deployedSpelling
    }

    It "leaves the deployed spelling as it is, so a deployment's snapshot does not change" {
        ConvertTo-CanonicalConstraintDefinition -Definition $script:deployedSpelling | Should -BeExactly $script:deployedSpelling
    }

    It "is idempotent" {
        $once = ConvertTo-CanonicalConstraintDefinition -Definition $script:restoredSpelling
        ConvertTo-CanonicalConstraintDefinition -Definition $once | Should -BeExactly $once
    }

    It "carries a string literal's own quotes and parentheses through the rewrite" {
        # PostgreSQL doubles a quote inside a literal, and a literal may hold parentheses; the element
        # pattern has to read them as text rather than as expression structure.
        ConvertTo-CanonicalConstraintDefinition -Definition 'CHECK ((("Z")::text = ANY (ARRAY[(''it''''s (odd)''::character varying)::text, (''plain''::character varying)::text])))' |
            Should -BeExactly 'CHECK ((("Z")::text = ANY ((ARRAY[''it''''s (odd)''::character varying, ''plain''::character varying])::text[])))'
    }

    It "still reads a changed, missing or added value, a different cast type and a different column as differences" {
        # The negative control: each variant is the restored spelling with one real change, and none
        # may normalize to the deployment.
        $variant = [ordered]@{
            "changed value"  = $script:restoredSpelling.Replace("'Tracking'", "'Trackin'")
            "missing value"  = $script:restoredSpelling.Replace(", ('Tracking'::character varying)::text", "")
            "added value"    = $script:restoredSpelling.Replace("('Tracking'::character varying)::text]", "('Tracking'::character varying)::text, ('Archived'::character varying)::text]")
            "different cast" = $script:restoredSpelling.Replace(")::text,", ")::character varying,").Replace(")::text]", ")::character varying]")
            "other column"   = $script:restoredSpelling.Replace('"ProjectionLifecycleState"', '"Other"')
        }
        foreach ($name in $variant.Keys) {
            $variant[$name] | Should -Not -BeExactly $script:restoredSpelling -Because "the $name variant must actually differ from the baseline"
            ConvertTo-CanonicalConstraintDefinition -Definition $variant[$name] |
                Should -Not -BeExactly $script:deployedSpelling -Because "a $name is real drift and must still fail the compare"
        }
    }

    It "does not rewrite an array that is not a uniformly pushed-down cast" {
        # Anything but "every element is (expr)::same-type" is an expression of its own and is
        # reported as the catalog rendered it -- a false difference at worst, never a false match.
        foreach ($untouched in @(
                'CHECK (("N" = ANY (ARRAY[1, 2, 3])))',
                'CHECK (("X" = ANY (ARRAY[''a''::text, ''b''::text])))',
                'ARRAY[(''a''::character varying)::text, ''b''::text]',
                'ARRAY[(''a''::character varying)::text, (''b''::character varying)::integer]',
                'ARRAY[(''a''::text)::text[], (''b''::text)::text[]]'
            )) {
            ConvertTo-CanonicalConstraintDefinition -Definition $untouched | Should -BeExactly $untouched
        }
    }

    It "is applied to the constraint section of the snapshot and to no other" {
        $script:exportSnapshotText | Should -Match '"04-constraint"' -Because "the rewrite must be scoped to the pg_get_constraintdef rows"
        [regex]::Matches($script:exportSnapshotText, 'ConvertTo-CanonicalConstraintDefinition').Count | Should -Be 1
    }
}

Describe "Restore recipe content gate checks every sequence the copied data draws from" {
    # Step 6 proves the restored database holds the published dataset. Row counts cannot see a
    # sequence left at its fresh-database position, which surfaces as a primary-key collision on the
    # first write, so every sequence the copy tool restores from the archive and asserts is measured
    # here too, against the data it numbers, from last_value, is_called and the increment -- never
    # nextval(), which would move the sequence being checked.

    It "names the same sequences Copy-NorthridgeDataForward.ps1 restores, and no fewer" {
        $script:copyToolSequence.Count | Should -Be 3 -Because "the copy tool lists three: $($script:copyToolSequence -join ', ')"
        foreach ($sequence in $script:copyToolSequence) {
            $schema, $name = $sequence.Split(".")
            $quoted = "$schema.`"$name`""
            $script:contentGateSql | Should -Match ([regex]::Escape("FROM $quoted s, pg_sequence q")) -Because "$sequence must be read from its own relation, where is_called lives"
            $script:contentGateSql | Should -Match ([regex]::Escape("WHERE q.seqrelid = '$quoted'::regclass")) -Because "the increment for $sequence must come from pg_sequence"
        }
    }

    It "computes each next value from last_value, is_called and the increment, never from nextval()" {
        # The SQL comments name nextval() to say why it is not called; the statements must not call it.
        $statement = (($script:contentGateSql -split "\r?\n") | Where-Object { $_ -notmatch '^\s*--' }) -join "`n"
        $statement | Should -Not -Match 'nextval\s*\('
        [regex]::Matches($script:contentGateSql, [regex]::Escape('s.last_value + CASE WHEN s.is_called THEN q.seqincrement ELSE 0 END')).Count |
            Should -Be $script:copyToolSequence.Count -Because "every sequence uses the same next-value reasoning"
    }

    It "measures each sequence against the data it numbers, with the collection tables read from the catalog" {
        $script:contentGateSql | Should -Match ([regex]::Escape('> COALESCE((SELECT MAX("DocumentId") FROM dms."Document"), 0)')) -Because "Document_DocumentId_seq numbers dms.Document"
        $script:contentGateSql | Should -Match 'MAX\(GREATEST\("ContentVersion", "IdentityVersion"\)\)' -Because "ChangeVersionSequence spans both document versions"
        $script:contentGateSql | Should -Match ([regex]::Escape('MAX("ContentVersion") FROM dms."Descriptor"')) -Because "the descriptor stamping trigger draws from ChangeVersionSequence too"
        $script:contentGateSql | Should -Match '> collection_max\)' -Because "CollectionItemIdSequence is measured against the gathered collection maximum"
        # The same catalog predicate as the copy tool, so the two cannot disagree on which tables count.
        $script:copyToolCollectionPredicate | Should -Not -BeNullOrEmpty -Because "the copy tool's predicate must have been read"
        $script:contentGateSql | Should -Match ([regex]::Escape($script:copyToolCollectionPredicate))
        $script:contentGateSql | Should -Match "format\('SELECT COALESCE\(MAX\(%I\), 0\) AS v FROM %I\.%I'" -Because "table and column names must be quoted as identifiers, never pasted"
        $script:contentGateSql | Should -Match '(?s)IF collection_sql IS NULL THEN\s+RAISE EXCEPTION' -Because "no collection tables at all must stop the gate, not pass it"
    }

    It "asserts that every written check ran, and the prose, the assertion and the notice agree on how many" {
        $written = [regex]::Matches($script:contentGateSql, "(?m)^\s*(?:UNION ALL )?SELECT '[^']+'(?: AS item)?,").Count
        $written | Should -BeGreaterThan 17 -Because "the gate grew by the two sequence checks"
        [regex]::Match($script:contentGateSql, 'IF checked <> (?<n>\d+) THEN').Groups["n"].Value | Should -Be "$written"
        $script:contentGateSql | Should -Match "only % of $written checks ran"
        $script:contentGateSql | Should -Match "restore verified: all $written published values and invariants match"
        $script:recipe | Should -Match "#\s+Expect: NOTICE: restore verified: all $written published values and invariants match"
        $script:recipe | Should -Match "This block compares $written values"
    }

    It "verifies the projection lifecycle singleton at the values the copy tool asserts" {
        # Row counts on DocumentProjectionWork and DocumentCache say the queues are empty; they say
        # nothing about dms."DocumentCacheState", the singleton that tells DMS whether a projection is
        # running. Provisioning seeds it Disabled with no cache-ahead recovery pending, and
        # Copy-NorthridgeDataForward.ps1 asserts exactly that at every checkpoint, so the gate holds the
        # restored artifact to the same two literals -- read from the copy tool, not restated -- and to
        # exactly one row.
        $copyTool = Get-Content -Raw -LiteralPath (Join-Path $script:northridgeRoot "Copy-NorthridgeDataForward.ps1")
        $lifecycle = [regex]::Match($copyTool, 'CacheStateLifecycle\s*=\s*"(?<v>[^"]+)"').Groups["v"].Value
        $recovery = [regex]::Match($copyTool, 'CacheAheadRecovery\s*=\s*"(?<v>[^"]+)"').Groups["v"].Value
        $lifecycle | Should -Not -BeNullOrEmpty -Because "the copy tool's expected lifecycle state must have been read"
        $recovery | Should -Not -BeNullOrEmpty -Because "the copy tool's expected recovery flag must have been read"

        $script:contentGateSql | Should -Match ([regex]::Escape("SELECT 'dms.""DocumentCacheState"" rows', '1',")) -Because "exactly one lifecycle row"
        $script:contentGateSql | Should -Match ([regex]::Escape('(SELECT COUNT(*)::text FROM dms."DocumentCacheState")'))
        $script:contentGateSql | Should -Match ([regex]::Escape("SELECT 'dms.""DocumentCacheState"".""ProjectionLifecycleState""', '$lifecycle',")) -Because "the gate must want the copy tool's lifecycle state"
        $script:contentGateSql | Should -Match ([regex]::Escape('(SELECT "ProjectionLifecycleState" FROM dms."DocumentCacheState" WHERE "StateId" = 1)'))
        $script:contentGateSql | Should -Match ([regex]::Escape("SELECT 'dms.""DocumentCacheState"".""CacheAheadRecoveryRequired""', '$recovery',")) -Because "the gate must want the copy tool's recovery flag"
        $script:contentGateSql | Should -Match ([regex]::Escape('(SELECT "CacheAheadRecoveryRequired"::text FROM dms."DocumentCacheState" WHERE "StateId" = 1)'))
        # The prose wraps across comment lines, so the two words are matched separately.
        $script:recipe | Should -Match "nine dms tables" -Because "the prose counts the tables the block reads"
        $script:recipe | Should -Not -Match "eight dms tables"
    }
}

Describe "Restore recipe: a bare restore fails the compare and step 5b repairs it" -Skip:(-not $script:pgFixtureEnabled) {
    BeforeAll {
        # Two databases: one provisioned the way the DDL provisions -- the objects Phase 9 touches,
        # then Phase 9 itself, verbatim from the authoritative fixture -- and one restored from its
        # dump with the recipe's flags. The role is the DDL's own, so it is created only if this
        # cluster does not have it and dropped only if this run created it: on a stack that has
        # bootstrapped DMS it already exists, owns objects, and is not this fixture's to remove.
        # dms."DocumentCacheState" carries the DDL's own varchar column and CHECK ... IN constraint,
        # the one row a dump round trip respells, so the repaired compare below is the recipe's step
        # 5c in miniature: a restore against the deployment itself, deparse artifact included.
        $script:recipeDatabase = @("dms_nr_fx_deployed", "dms_nr_fx_restored")
        $script:recipeDump = "/tmp/dms_nr_fx_deployed.dump"
        $roleExists = docker exec $script:container psql -U $script:pgUser -d postgres -tAc "SELECT 1 FROM pg_roles WHERE rolname = 'edfi_dms_enqueue_owner';" 2>&1
        if ($LASTEXITCODE -ne 0) { throw "could not query pg_roles: $roleExists" }
        $script:createdEnqueueOwner = (($roleExists | Out-String).Trim() -ne "1")

        function script:Remove-RecipeFixture {
            foreach ($name in $script:recipeDatabase) {
                docker exec $script:container dropdb -U $script:pgUser --maintenance-db=postgres --if-exists -- $name 2>&1 | Out-Null
            }
            docker exec -u 0 $script:container rm -f $script:recipeDump 2>&1 | Out-Null
            if ($script:createdEnqueueOwner) {
                Invoke-FixtureSql -DatabaseName "postgres" -Sql 'DROP ROLE IF EXISTS "edfi_dms_enqueue_owner";'
            }
        }

        # The objects Phase 9 names, in the shapes the sections read. The section 11 and 12 tables
        # are needed for the snapshot to run; the identity column gives section 06 a sequence whose
        # owner --no-owner also rewrites. The Phase 8 GRANT EXECUTE ... TO SESSION_USER that precedes
        # Phase 9 in the DDL is omitted: Phase 9 revokes it again, so it leaves no trace to compare.
        $script:deployedDdl = @"
CREATE SCHEMA "dms";
CREATE TABLE dms."EffectiveSchema"("EffectiveSchemaSingletonId" int PRIMARY KEY, "ApiSchemaFormatVersion" text, "EffectiveSchemaHash" text, "ResourceKeyCount" int, "ResourceKeySeedHash" bytea);
CREATE TABLE dms."SchemaComponent"("EffectiveSchemaHash" text, "ProjectEndpointName" text, "ProjectName" text, "ProjectVersion" text, "IsExtensionProject" boolean);
CREATE TABLE "dms"."DocumentCacheState"("StateId" smallint PRIMARY KEY, "ProjectionLifecycleState" varchar(16) NOT NULL, "CacheAheadRecoveryRequired" boolean NOT NULL DEFAULT false, CONSTRAINT "CK_DocumentCacheState_Lifecycle" CHECK ("ProjectionLifecycleState" IN ('Disabled', 'Resetting', 'Rebuilding', 'Tracking')));
CREATE TABLE "dms"."DocumentProjectionWork"("DocumentProjectionWorkId" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, "DocumentId" bigint NOT NULL);
CREATE FUNCTION "dms"."TF_Document_EnqueueProjectionInsert"() RETURNS TRIGGER LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS `$func`$ BEGIN RETURN NULL; END `$func`$;
CREATE FUNCTION "dms"."TF_Document_EnqueueProjectionUpdate"() RETURNS TRIGGER LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS `$func`$ BEGIN RETURN NULL; END `$func`$;
$script:securityPhase
"@

        Remove-RecipeFixture
        foreach ($name in $script:recipeDatabase) {
            $created = docker exec $script:container createdb -U $script:pgUser --maintenance-db=postgres -- $name 2>&1
            if ($LASTEXITCODE -ne 0) { throw "could not create fixture database '$name': $created" }
        }
        Invoke-FixtureSql -DatabaseName "dms_nr_fx_deployed" -Sql $script:deployedDdl

        # The recipe's own transport: a custom-format dump, restored with --no-owner --no-privileges
        # --exit-on-error into an empty database.
        $dumped = docker exec $script:container pg_dump -U $script:pgUser -Fc -f $script:recipeDump dms_nr_fx_deployed 2>&1
        if ($LASTEXITCODE -ne 0) { throw "pg_dump failed: $dumped" }
        $restored = docker exec $script:container pg_restore -U $script:pgUser -d dms_nr_fx_restored --no-owner --no-privileges --exit-on-error $script:recipeDump 2>&1
        if ($LASTEXITCODE -ne 0) { throw "pg_restore failed: $restored" }

        # Runs the README's REPAIR_SQL block exactly as the recipe does: one transaction, stop on error.
        function script:Invoke-RecipeRepair {
            param([Parameter(Mandatory)] [string] $DatabaseName)
            $output = $script:repairSql | docker exec -i $script:container psql -U $script:pgUser -d $DatabaseName `
                -v ON_ERROR_STOP=1 -q -1 -f - 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "the recipe's repair block failed against '$DatabaseName' (exit $LASTEXITCODE): $($output -join [Environment]::NewLine)"
            }
        }

        # Step 6's three DocumentCacheState checks, each as the got-expression the recipe runs and the
        # value it wants, read from the gate SQL so the live case below evaluates the recipe's own text.
        $script:lifecycleCheck = @([regex]::Matches($script:contentGateSql,
                "(?m)^\s*UNION ALL SELECT '(?<item>dms\.""DocumentCacheState""[^']*)', '(?<want>[^']*)',\r?\n\s*(?<got>\(SELECT [^\r\n]*DocumentCacheState[^\r\n]*\))\s*$"))

        # Which of the three checks would report a mismatch against the deployed fixture as it stands.
        function script:Get-LifecycleMismatch {
            $mismatch = [ordered]@{}
            foreach ($check in $script:lifecycleCheck) {
                $sql = "SELECT (" + $check.Groups["got"].Value + ") IS DISTINCT FROM '" + $check.Groups["want"].Value + "';"
                $answer = $sql | docker exec -i $script:container psql -U $script:pgUser -d dms_nr_fx_deployed `
                    -v ON_ERROR_STOP=1 -tA 2>&1
                if ($LASTEXITCODE -ne 0) { throw "could not evaluate '$sql': $answer" }
                $mismatch[$check.Groups["item"].Value] = ((($answer | Out-String).Trim()) -eq "t")
            }
            return $mismatch
        }
    }

    AfterAll {
        Remove-RecipeFixture
    }

    It "fails the compare after a bare restore, in the sections the repair exists for" {
        # The premise of the recipe step. --no-owner hands every object to the restoring user, which
        # is the deploying user too, so table and sequence owners agree and only the functions the
        # DDL assigns to the enqueue owner differ; --no-privileges resets every ACL to the PostgreSQL
        # default, so the routine, table and schema grants differ. The bodies are untouched, so
        # section 08 must be silent.
        $result = Invoke-Compare -DatabaseName @("dms_nr_fx_deployed", "dms_nr_fx_restored")
        $result.Failed | Should -BeTrue -Because "a bare restore must not compare equal to a deployment: $($result.Output)"
        foreach ($section in @("10-grant", "14-routine-security", "15-routine-grant", "16-schema-privilege")) {
            $result.Diff | Should -Match "`t$section\|" -Because "the drift $section reads is what the flags drop: $($result.Diff)"
        }
        foreach ($section in @("02-table", "06-sequence", "08-routine")) {
            $result.Diff | Should -Not -Match "`t$section\|" -Because "$section is not what a same-user restore changes: $($result.Diff)"
        }
        $result.Diff | Should -Not -Match "`t04-constraint\|" -Because "the respelled CHECK constraint is the same predicate and must not read as drift: $($result.Diff)"
        $result.Diff | Should -Match "owner=edfi_dms_enqueue_owner"
        $result.Diff | Should -Match "PUBLIC=EXECUTE"
    }

    It "passes the compare once the recipe's repair block has run" {
        Invoke-RecipeRepair -DatabaseName "dms_nr_fx_restored"
        $result = Invoke-Compare -DatabaseName @("dms_nr_fx_deployed", "dms_nr_fx_restored")
        $result.Failed | Should -BeFalse -Because "the repaired restore must be the deployed schema, ownership and privileges included: $($result.Output)$($result.Diff)"
        $result.Output | Should -Match "PASS: schema snapshots"
    }

    It "leaves the compare passing when the repair block is run again" {
        # A consumer who re-runs step 5b must not drift away from the deployment.
        Invoke-RecipeRepair -DatabaseName "dms_nr_fx_restored"
        $result = Invoke-Compare -DatabaseName @("dms_nr_fx_deployed", "dms_nr_fx_restored")
        $result.Failed | Should -BeFalse -Because "the repair must be idempotent: $($result.Output)$($result.Diff)"
    }

    It "still fails when the restored CHECK constraint really differs" {
        # The live negative control for the deparse normalization: the two spellings of one
        # predicate compare equal above, so a different predicate must not. The restored side gets
        # the same constraint with one value fewer, then gets the original back.
        Invoke-FixtureSql -DatabaseName "dms_nr_fx_restored" -Sql @"
ALTER TABLE "dms"."DocumentCacheState" DROP CONSTRAINT "CK_DocumentCacheState_Lifecycle";
ALTER TABLE "dms"."DocumentCacheState" ADD CONSTRAINT "CK_DocumentCacheState_Lifecycle" CHECK ("ProjectionLifecycleState" IN ('Disabled', 'Resetting', 'Rebuilding'));
"@
        try {
            $result = Invoke-Compare -DatabaseName @("dms_nr_fx_deployed", "dms_nr_fx_restored")
            $result.Failed | Should -BeTrue -Because "a changed value list is real drift: $($result.Output)"
            $result.Diff | Should -Match "`t04-constraint\|constraint\|dms\.DocumentCacheState\|CK_DocumentCacheState_Lifecycle\|" -Because "the diff must name the constraint: $($result.Diff)"
            $result.Diff | Should -Match "'Tracking'" -Because "the side that still holds the value must show it"
        }
        finally {
            Invoke-FixtureSql -DatabaseName "dms_nr_fx_restored" -Sql @"
ALTER TABLE "dms"."DocumentCacheState" DROP CONSTRAINT "CK_DocumentCacheState_Lifecycle";
ALTER TABLE "dms"."DocumentCacheState" ADD CONSTRAINT "CK_DocumentCacheState_Lifecycle" CHECK ("ProjectionLifecycleState" IN ('Disabled', 'Resetting', 'Rebuilding', 'Tracking'));
"@
        }
    }

    It "reads the projection lifecycle singleton in step 6, so a missing row and a wrong value are mismatches" {
        # Step 6 holds dms."DocumentCacheState" to exactly one row, Disabled, with no cache-ahead
        # recovery pending. Each state the gate has to tell apart is put into the deployed fixture and
        # the recipe's own three expressions are asked whether they would report it: no row must
        # mismatch on all three rather than pass on NULL, the seeded row must match on all three, a row
        # mid-rebuild must mismatch on both values, and a second row must fail the singleton count.
        $script:lifecycleCheck.Count | Should -Be 3 -Because "rows, lifecycle state and recovery flag"
        $table = '"dms"."DocumentCacheState"'
        $column = '("StateId", "ProjectionLifecycleState", "CacheAheadRecoveryRequired")'
        Invoke-FixtureSql -DatabaseName "dms_nr_fx_deployed" -Sql "DELETE FROM $table;"
        try {
            @((Get-LifecycleMismatch).Values) | Should -Not -Contain $false -Because "with no row every check must mismatch, not pass on NULL"

            Invoke-FixtureSql -DatabaseName "dms_nr_fx_deployed" -Sql "INSERT INTO $table $column VALUES (1, 'Disabled', false);"
            @((Get-LifecycleMismatch).Values) | Should -Not -Contain $true -Because "the seeded singleton is what the artifact ships"

            Invoke-FixtureSql -DatabaseName "dms_nr_fx_deployed" -Sql "UPDATE $table SET ""ProjectionLifecycleState"" = 'Rebuilding', ""CacheAheadRecoveryRequired"" = true;"
            $mismatch = Get-LifecycleMismatch
            $mismatch['dms."DocumentCacheState" rows'] | Should -BeFalse -Because "one row is still one row"
            $mismatch['dms."DocumentCacheState"."ProjectionLifecycleState"'] | Should -BeTrue -Because "a projection mid-rebuild is not the shipped state"
            $mismatch['dms."DocumentCacheState"."CacheAheadRecoveryRequired"'] | Should -BeTrue -Because "pending recovery is not the shipped state"

            Invoke-FixtureSql -DatabaseName "dms_nr_fx_deployed" -Sql "INSERT INTO $table $column VALUES (2, 'Disabled', false);"
            (Get-LifecycleMismatch)['dms."DocumentCacheState" rows'] | Should -BeTrue -Because "a second row is not the singleton"
        }
        finally {
            Invoke-FixtureSql -DatabaseName "dms_nr_fx_deployed" -Sql "DELETE FROM $table;"
        }
    }
}
