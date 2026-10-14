# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Opt in on a disposable PostgreSQL container. All databases and files are fixture-owned.
BeforeDiscovery {
    $script:enabled = -not [string]::IsNullOrWhiteSpace($env:DMS_NORTHRIDGE_PG_FIXTURE_CONTAINER)
}

Describe 'Legacy dump carry-forward into native compact descriptor storage with <Form> types' -Skip:(-not $script:enabled) -ForEach @(
    @{ Form = 'dot-qualified and unqualified'; CoreLiveType = 'SchoolTypeDescriptor'; SampleLiveType = 'Sample.SchoolTypeDescriptor'; CoreHistoryType = 'Ed-Fi.SchoolTypeDescriptor'; SampleHistoryType = 'Sample.SchoolTypeDescriptor' },
    @{ Form = 'colon-qualified'; CoreLiveType = 'Ed-Fi:SchoolTypeDescriptor'; SampleLiveType = 'Sample:SchoolTypeDescriptor'; CoreHistoryType = 'Ed-Fi:SchoolTypeDescriptor'; SampleHistoryType = 'Sample:SchoolTypeDescriptor' }
) {
    BeforeAll {
        $script:repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        $script:container = $env:DMS_NORTHRIDGE_PG_FIXTURE_CONTAINER
        $script:user = if ($env:DMS_NORTHRIDGE_PG_FIXTURE_USER) { $env:DMS_NORTHRIDGE_PG_FIXTURE_USER } else { 'postgres' }
        $script:fixture = Join-Path $script:repo 'src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/compact-descriptor/expected'
        $script:manifest = Join-Path $script:fixture 'relational-model.pgsql.manifest.json'
        Import-Module (Join-Path $script:repo 'eng/DatabaseTemplates/Compact-Descriptor.psm1') -Force
        $script:inventory = Read-CompactDescriptorInventory -Dialect pgsql -ExpectedModelManifestPath $script:manifest
        $script:copy = Join-Path $script:repo 'eng/northridge/Copy-NorthridgeDataForward.ps1'
        $script:suffix = [guid]::NewGuid().ToString('N')
        $script:source = "nr_source_$script:suffix"
        $script:target = "nr_target_$script:suffix"
        $script:reference = "nr_ref_$script:suffix"
        $script:databases = @($script:source, $script:target, $script:reference)
        $script:dump = Join-Path $TestDrive 'legacy.dump'
        $script:outputDir = Join-Path $TestDrive 'evidence'
        function script:Query {
            param([string]$Database, [string]$Sql)
            $result = $Sql | docker exec -i $script:container psql -U $script:user -d $Database -v ON_ERROR_STOP=1 -Atq 2>&1
            if ($LASTEXITCODE -ne 0) { throw "Fixture SQL failed: $($result -join [Environment]::NewLine)" }
            return @($result | ForEach-Object { [string]$_ })
        }
        $ddl = Get-Content (Join-Path $script:fixture 'pgsql.sql') -Raw
        # This synthetic baseline has no relationship authorization tables. Add a small companion
        # witness on each side to exercise the tool's existing mandatory auth coverage and FK sweep.
        $ddl += '
CREATE SCHEMA auth; CREATE TABLE auth."CarryForwardWitness" ("DocumentId" bigint PRIMARY KEY REFERENCES dms."Document" ("DocumentId"));'.Replace('\n', "`n")
        # A representative legacy dump: preserve the exact current schema set and existing non-
        # descriptor objects, replace only the legacy physical descriptor contracts before seeding.
        $script:compactDdl = $ddl
        $legacy = $ddl.Replace('    "DescriptorId" int GENERATED ALWAYS AS IDENTITY NOT NULL,', '')
        $legacy = $legacy.Replace('CONSTRAINT "PK_Descriptor" PRIMARY KEY ("DescriptorId")', 'CONSTRAINT "PK_Descriptor" PRIMARY KEY ("DocumentId")')
        $legacy = $legacy.Replace('REFERENCES "dms"."Descriptor" ("DescriptorId")', 'REFERENCES "dms"."Descriptor" ("DocumentId")')
        $legacy = $legacy.Replace('CREATE TABLE IF NOT EXISTS "dms"."Descriptor"' + "`n(", 'CREATE TABLE IF NOT EXISTS "dms"."Descriptor"' + "`n(" + "`n    " + '"Uri" varchar(306) NOT NULL, "Discriminator" varchar(256) NOT NULL,')
        $names = @($script:inventory.columns.name) + @($script:inventory.aliases.name) | Sort-Object -Unique -CaseSensitive
        foreach ($name in $names) { $legacy = $legacy.Replace('"' + $name + '" integer ', '"' + $name + '" bigint ') }
        $historyStart = $legacy.IndexOf('CREATE TABLE IF NOT EXISTS "tracked_changes_edfi"."Descriptor"')
        $historyEnd = $legacy.IndexOf(');', $historyStart) + 2
        $history = $legacy.Substring($historyStart, $historyEnd - $historyStart).Replace('"ResourceKeyId" smallint NOT NULL', '"Discriminator" varchar(256) NOT NULL')
        $legacy = $legacy.Substring(0, $historyStart) + $history + $legacy.Substring($historyEnd)
        $legacy = $legacy.Replace('ON "tracked_changes_edfi"."Descriptor" ("ResourceKeyId", "ChangeVersion")', 'ON "tracked_changes_edfi"."Descriptor" ("Discriminator", "ChangeVersion")')
        foreach ($db in $script:databases) {
            docker exec $script:container createdb -U $script:user $db
            if ($LASTEXITCODE -ne 0) { throw "Cannot create fixture $db" }
            Query $db $(if ($db -ceq $script:source) { $legacy } else { $ddl }) | Out-Null
        }
        $seedSql = @'
BEGIN;
SET LOCAL session_replication_role = replica;
INSERT INTO dms."Document" ("DocumentId", "DocumentUuid", "ResourceKeyId", "ContentVersion", "ContentLastModifiedAt", "CreatedAt") OVERRIDING SYSTEM VALUE VALUES
(5000000042, '00000000-0000-0000-0000-000000000042', 3, 101, '2026-01-02Z', '2026-01-01Z'),
(5000000043, '00000000-0000-0000-0000-000000000043', 5, 102, '2026-01-03Z', '2026-01-01Z'),
(5000000100, '00000000-0000-0000-0000-000000000100', 4, 103, '2026-01-04Z', '2026-01-01Z'),
(5000000200, '00000000-0000-0000-0000-000000000200', 2, 104, '2026-01-05Z', '2026-01-01Z');
INSERT INTO dms."Descriptor" ("DocumentId", "ResourceKeyId", "Namespace", "CodeValue", "ShortDescription", "Uri", "Discriminator", "ContentVersion", "ContentLastModifiedAt") VALUES
(5000000042, 3, 'uri://Example/Type', 'A#B', 'Core', 'uri://Example/Type#A#B', '__CORE_LIVE_TYPE__', 101, '2026-01-02Z'),
(5000000043, 5, 'uri://Example/Type', 'A#B', 'Sample', 'uri://Example/Type#A#B', '__SAMPLE_LIVE_TYPE__', 102, '2026-01-03Z');
INSERT INTO dms."ReferentialIdentity" ("ReferentialId", "DocumentId", "ResourceKeyId") VALUES
('00000000-0000-0000-0000-000000000001', 5000000042, 3), ('00000000-0000-0000-0000-000000000002', 5000000043, 5),
('00000000-0000-0000-0000-000000000003', 5000000100, 4), ('00000000-0000-0000-0000-000000000004', 5000000200, 2);
INSERT INTO edfi."Student" ("DocumentId", "SchoolTypeDescriptor_DescriptorId", "FirstName", "StudentUniqueId", "ContentVersion", "ContentLastModifiedAt") VALUES
(5000000100, 5000000042, 'Test', 'student-100', 103, '2026-01-04Z');
INSERT INTO edfi."DescriptorSubjectIdentity" ("DocumentId", "SchoolTypeDescriptor_DescriptorId", "Discriminator") VALUES (5000000100, 5000000042, 'Ed-Fi:Student');
INSERT INTO edfi."ProfileRootOnlyMergeItem" ("DocumentId", "ProfileRootOnlyMergeItemId", "PrimarySchoolTypeDescriptor_DescriptorId_Present", "PrimarySchoolTypeDescriptor_Unified_DescriptorId", "StudentReference_DocumentId", "StudentReference_StudentUniqueId", "StudentReference_SchoolTypeDescriptor_DescriptorId", "ContentVersion", "ContentLastModifiedAt") VALUES
(5000000200, 200, true, 5000000042, 5000000100, 'student-100', 5000000042, 104, '2026-01-05Z');
INSERT INTO sample."ProfileRootOnlyMergeItemExtension" ("DocumentId", "SchoolTypeDescriptor_DescriptorId") VALUES (5000000200, 5000000043);
INSERT INTO edfi."ProfileRootOnlyMergeItemItem" ("CollectionItemId", "Ordinal", "ProfileRootOnlyMergeItem_DocumentId", "SchoolTypeDescriptor_DescriptorId") VALUES (9001, 0, 5000000200, 5000000042);
INSERT INTO edfi."ProfileRootOnlyMergeItemItemNested" ("CollectionItemId", "Ordinal", "ParentCollectionItemId", "ProfileRootOnlyMergeItem_DocumentId", "SchoolTypeDescriptor_DescriptorId") VALUES (9002, 0, 9001, 5000000200, 5000000043), (9003, 1, 9001, 5000000200, NULL);
INSERT INTO tracked_changes_edfi."Descriptor" ("Discriminator", "OldNamespace", "OldCodeValue", "Id", "DocumentId", "ChangeVersion", "CreatedAt") VALUES
('__CORE_HISTORY_TYPE__', 'uri://Deleted', 'Core', '00000000-0000-0000-0000-000000000901', 6000000901, 201, '2026-01-06Z'),
('__SAMPLE_HISTORY_TYPE__', 'uri://Deleted', 'Sample', '00000000-0000-0000-0000-000000000902', 6000000902, 202, '2026-01-07Z');
INSERT INTO auth."CarryForwardWitness" ("DocumentId") VALUES (5000000100);
SELECT setval(pg_get_serial_sequence('dms."Document"', 'DocumentId'), 5000000200);
SELECT setval('dms."CollectionItemIdSequence"', 9003);
SELECT setval('dms."ChangeVersionSequence"', 202);
COMMIT;
'@
        Query $script:source $seedSql.Replace('__CORE_LIVE_TYPE__', $CoreLiveType).Replace('__SAMPLE_LIVE_TYPE__', $SampleLiveType).Replace('__CORE_HISTORY_TYPE__', $CoreHistoryType).Replace('__SAMPLE_HISTORY_TYPE__', $SampleHistoryType) | Out-Null
        # Offset target native allocation so neither compact IDs nor maxima equal source keys.
        Query $script:target 'SELECT setval(pg_get_serial_sequence(''dms."Descriptor"'', ''DescriptorId''), 41, false);' | Out-Null
        $script:originalIdentity = Query $script:target 'SELECT "SourceIdentity" FROM dms."DataStoreIdentity";'
        docker exec $script:container pg_dump -U $script:user -Fc -f /tmp/nr-compact-fixture.dump $script:source
        if ($LASTEXITCODE -ne 0) { throw 'Fixture dump failed' }
        docker cp "${script:container}:/tmp/nr-compact-fixture.dump" $script:dump
        if ($LASTEXITCODE -ne 0) { throw 'Fixture dump copy failed' }
        $script:copyOutput = @(& $script:copy -Mode Copy -DumpPath $script:dump -SourceDatabase $script:source -TargetDatabase $script:target `
            -ReferenceDatabase $script:reference -ExpectedDocumentCount 4 -ExpectedModelManifestPath $script:manifest `
            -OutputDirectory $script:outputDir -Container $script:container -PostgresUser $script:user)
    }
    AfterAll {
        foreach ($db in $script:databases) { docker exec $script:container dropdb -U $script:user --if-exists $db | Out-Null }
        docker exec $script:container rm -f /tmp/nr-compact-fixture.dump | Out-Null
    }

    It 'Executes actual dump conversion with independent native keys and retained key-map evidence' {
        $script:copyOutput -join "`n" | Should -Match 'PASS: copy complete'
        (Query $script:target 'SELECT "DescriptorId" || ''|'' || "DocumentId" FROM dms."Descriptor" ORDER BY "DescriptorId";') | Should -Be @('41|5000000042', '42|5000000043')
        $map = Import-Csv (Join-Path $script:outputDir "descriptor-key-map.$script:target.tsv") -Delimiter "`t"
        $map.DocumentId | Should -Be @('5000000042', '5000000043')
        $map.DescriptorId | Should -Be @('41', '42')
    }
    It 'Preserves each owning document type and reconstructs the exact legacy URI' {
        (Query $script:target 'SELECT s."DocumentId" || ''|'' || s."ResourceKeyId" || ''|'' || d."ResourceKeyId" || ''|'' || s."Namespace" || ''#'' || s."CodeValue" FROM dms."Descriptor" s JOIN dms."Document" d ON d."DocumentId" = s."DocumentId" ORDER BY s."DocumentId";') | Should -Be @(
            '5000000042|3|3|uri://Example/Type#A#B', '5000000043|5|5|uri://Example/Type#A#B')
        (Query $script:target 'SELECT "Namespace" || ''#'' || "CodeValue" FROM dms."Descriptor" ORDER BY "DocumentId";') | Should -Be (Query $script:source 'SELECT "Uri" FROM dms."Descriptor" ORDER BY "DocumentId";')
    }
    It 'Remaps every stored root/copied/collection/nested/extension/abstract key and preserves composite FKs and aliases' {
        foreach ($column in $script:inventory.columns) {
            $value = if ($column.schema -ceq 'sample' -or $column.table -ceq 'ProfileRootOnlyMergeItemItemNested') { '42' } else { '41' }
            $actual = Query $script:target "SELECT DISTINCT `"$($column.name)`" FROM `"$($column.schema)`".`"$($column.table)`" WHERE `"$($column.name)`" IS NOT NULL;"
            $actual | Should -Be @($value)
        }
        (Query $script:target 'SELECT "PrimarySchoolTypeDescriptor_DescriptorId" || ''|'' || COALESCE("SecondarySchoolTypeDescriptor_DescriptorId"::text, ''NULL'') || ''|'' || "StudentReference_DocumentId" FROM edfi."ProfileRootOnlyMergeItem";') | Should -Be @('41|NULL|5000000100')
        (Query $script:target 'SELECT count(*) FROM edfi."ProfileRootOnlyMergeItemItemNested" WHERE "SchoolTypeDescriptor_DescriptorId" IS NULL;') | Should -Be @('1')
        Query $script:target (Get-CompactDescriptorAssertionSql -Dialect pgsql -ExpectedModelManifestPath $script:manifest) | Out-Null
    }
    It 'Routes tombstones by qualified ResourceKeyId without live rows and preserves all history values' {
        (Query $script:target 'SELECT "ResourceKeyId" || ''|'' || "DocumentId" || ''|'' || "ChangeVersion" || ''|'' || "Id" || ''|'' || "OldCodeValue" FROM tracked_changes_edfi."Descriptor" ORDER BY "ChangeVersion";') | Should -Be @(
            '3|6000000901|201|00000000-0000-0000-0000-000000000901|Core', '5|6000000902|202|00000000-0000-0000-0000-000000000902|Sample')
        (Query $script:target 'SELECT count(*) FROM dms."Document" WHERE "DocumentId" IN (6000000901, 6000000902);') | Should -Be @('0')
        foreach ($name in @('Id', 'DocumentId', 'ChangeVersion', 'OldNamespace', 'OldCodeValue', 'CreatedAt')) {
            $sql = "SELECT json_agg(`"$name`" ORDER BY `"ChangeVersion`")::text FROM tracked_changes_edfi.`"Descriptor`";"
            (Query $script:target $sql) | Should -Be (Query $script:source $sql)
        }
    }
    It 'Preserves exact document/RI/stamp values, source identity and cleanup and passes checkpoint' {
        foreach ($table in @('dms.Document', 'dms.ReferentialIdentity')) {
            $schema, $name = $table.Split('.')
            $sql = "SELECT json_agg(t ORDER BY `"DocumentId`")::text FROM `"$schema`".`"$name`" t;"
            (Query $script:target $sql) | Should -Be (Query $script:source $sql)
        }
        foreach ($table in @('dms.Descriptor', 'edfi.Student', 'edfi.ProfileRootOnlyMergeItem')) {
            $schema, $name = $table.Split('.')
            $sql = "SELECT json_agg(json_build_array(`"DocumentId`", `"ContentVersion`", `"ContentLastModifiedAt`") ORDER BY `"DocumentId`")::text FROM `"$schema`".`"$name`";"
            (Query $script:target $sql) | Should -Be (Query $script:source $sql)
        }
        $output = @(& $script:copy -Mode Checkpoint -CheckpointName C2 -TargetDatabase $script:target -ReferenceDatabase $script:reference `
            -ExpectedDocumentCount 4 -ExpectedSourceIdentity $script:originalIdentity -ExpectedModelManifestPath $script:manifest `
            -OutputDirectory $script:outputDir -Container $script:container -PostgresUser $script:user)
        $output -join "`n" | Should -Match 'PASS: checkpoint C2'
        (Query $script:target "SELECT count(*) FROM pg_namespace WHERE nspname = 'northridge_staging';") | Should -Be @('0')
    }
    It 'Rejects colliding allocator checkpoints without consuming or repairing the sequence' {
        Query $script:target 'SELECT setval(pg_get_serial_sequence(''dms."Descriptor"'', ''DescriptorId''), 42, false);' | Out-Null
        try {
            { & $script:copy -Mode Checkpoint -CheckpointName Collision -TargetDatabase $script:target -ReferenceDatabase $script:reference `
                -ExpectedDocumentCount 4 -ExpectedModelManifestPath $script:manifest -OutputDirectory $script:outputDir `
                -Container $script:container -PostgresUser $script:user } | Should -Throw '*DescriptorIdentitySequence*collide*'
            (Query $script:target 'SELECT last_value || ''|'' || is_called FROM dms."Descriptor_DescriptorId_seq";') | Should -Be @('42|false')
        } finally { Query $script:target 'SELECT setval(pg_get_serial_sequence(''dms."Descriptor"'', ''DescriptorId''), 42, true);' | Out-Null }
    }
    It 'Stops the actual copy on <name> and cleans its staging' -ForEach @(
        @{ Name = 'unmapped stored reference'; Mutation = 'UPDATE edfi."Student" SET "SchoolTypeDescriptor_DescriptorId" = 5000000999'; Error = '*Unmapped descriptor reference*' },
        @{ Name = 'unknown deleted type'; Mutation = 'UPDATE tracked_changes_edfi."Descriptor" SET "Discriminator" = ''Missing.SchoolTypeDescriptor'' WHERE "ChangeVersion" = 201'; Error = '*Unknown or ambiguous descriptor history type*' },
        @{ Name = 'unknown colon-qualified deleted type'; Mutation = 'UPDATE tracked_changes_edfi."Descriptor" SET "Discriminator" = ''Missing:SchoolTypeDescriptor'' WHERE "ChangeVersion" = 201'; Error = '*Unknown or ambiguous descriptor history type*' },
        @{ Name = 'colon-qualified live type from the wrong project'; Mutation = 'UPDATE dms."Descriptor" SET "Discriminator" = ''Sample:SchoolTypeDescriptor'' WHERE "DocumentId" = 5000000042'; Error = '*Legacy descriptor owner/type/whole URI mismatch*' },
        @{ Name = 'ambiguous unqualified deleted type'; Mutation = 'UPDATE tracked_changes_edfi."Descriptor" SET "Discriminator" = ''SchoolTypeDescriptor'' WHERE "ChangeVersion" = 201'; Error = '*Unknown or ambiguous descriptor history type*' }
    ) {
        $id = [guid]::NewGuid().ToString('N')
        $badSource = "nr_bad_source_$id"
        $badTarget = "nr_bad_target_$id"
        $badDump = Join-Path $TestDrive "$id.dump"
        try {
            docker exec $script:container createdb -U $script:user -T $script:source $badSource
            if ($LASTEXITCODE -ne 0) { throw 'Cannot clone legacy fixture source' }
            docker exec $script:container createdb -U $script:user $badTarget
            if ($LASTEXITCODE -ne 0) { throw 'Cannot create rejected copy target' }
            Query $badTarget $script:compactDdl | Out-Null
            Query $badSource "SET session_replication_role = replica; $mutation;" | Out-Null
            docker exec $script:container pg_dump -U $script:user -Fc -f /tmp/nr-rejected.dump $badSource
            if ($LASTEXITCODE -ne 0) { throw 'Cannot dump rejected source' }
            docker cp "${script:container}:/tmp/nr-rejected.dump" $badDump
            if ($LASTEXITCODE -ne 0) { throw 'Cannot copy rejected dump' }
            { & $script:copy -Mode Copy -DumpPath $badDump -SourceDatabase $badSource -TargetDatabase $badTarget `
                -ReferenceDatabase $script:reference -ExpectedDocumentCount 4 -ExpectedModelManifestPath $script:manifest `
                -OutputDirectory (Join-Path $TestDrive $id) -Container $script:container -PostgresUser $script:user } | Should -Throw $error
            (Query $badTarget "SELECT count(*) FROM pg_namespace WHERE nspname = 'northridge_staging';") | Should -Be @('0')
            (Query $badTarget 'SELECT count(*) FROM pg_trigger WHERE tgenabled = ''D'';') | Should -Be @('0')
        } finally {
            docker exec $script:container dropdb -U $script:user --if-exists $badTarget | Out-Null
            docker exec $script:container dropdb -U $script:user --if-exists $badSource | Out-Null
            docker exec $script:container rm -f /tmp/nr-rejected.dump | Out-Null
        }
    }
    It 'Allows the next ordinary descriptor insert without collision while preserving bigint document allocation' {
        $result = Query $script:target @'
BEGIN;
INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId") VALUES ('00000000-0000-0000-0000-000000000044', 3) RETURNING "DocumentId";
INSERT INTO dms."Descriptor" ("DocumentId", "ResourceKeyId", "Namespace", "CodeValue", "ShortDescription")
SELECT "DocumentId", 3, 'uri://New', 'Next', 'Next' FROM dms."Document" WHERE "DocumentUuid" = '00000000-0000-0000-0000-000000000044' RETURNING "DescriptorId";
ROLLBACK;
'@
        $result | Should -Be @('5000000201', '43')
    }
}
