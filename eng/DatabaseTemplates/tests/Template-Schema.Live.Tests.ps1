# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7
# Containers must be exclusively owned disposable test instances. Tests create/drop only
# uniquely named databases. The synthetic package is built by the producer's own catalog-facts,
# DMS-only gate, dump, and restore-manifest steps, so it proves the real restore/catalog/manifest/
# data path; API registration is intercepted at its first call (full serveability is the release
# matrix).
$physicalDriftCases = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'fixtures/mssql-physical-schema-drift.json') -Raw |
    ConvertFrom-Json -AsHashtable
foreach ($case in @(
        @{ Engine = 'postgresql'; Dialect = 'pgsql'; Container = $env:DMS_TEMPLATE_TEST_PG_CONTAINER },
        @{ Engine = 'mssql'; Dialect = 'mssql'; Container = $env:DMS_TEMPLATE_TEST_MSSQL_CONTAINER }
    )) {
    Describe 'Live combined template restore gate (<Engine>)' -Tag DatabaseIntegration -ForEach @($case) -Skip:([string]::IsNullOrWhiteSpace($case.Container)) {
        BeforeAll {
            $templates = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
            $repo = [System.IO.Path]::GetFullPath((Join-Path $templates '../..'))
            $fixture = Join-Path $repo 'src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/compact-descriptor/expected'
            $script:manifest = Join-Path $fixture "relational-model.$Dialect.manifest.json"
            $script:verify = Join-Path $templates 'verify-template-restore.ps1'
            Import-Module (Join-Path $templates 'Template-Management.psm1') -Force
            Import-Module (Join-Path $templates '../Dms-Management.psm1') -Force
            Import-Module (Join-Path $templates 'Template-Schema.psm1') -Force
            # BeforeEach mocks Import-Module, so the verifier's manifest helpers must already be loaded.
            Import-Module (Join-Path $templates 'Template-RestoreCore.psm1') -Force
            Import-Module (Join-Path $templates 'Template-RestoreTrust.psm1') -Force
            $password = $env:DMS_TEMPLATE_TEST_MSSQL_PASSWORD ?? 'EdFi_Dms1!'
            $source = 't17_source_' + [Guid]::NewGuid().ToString('N')
            $script:restored = 't17_restored_' + [Guid]::NewGuid().ToString('N')
            $master = if ($Engine -eq 'mssql') { 'master' } else { 'postgres' }

            function Invoke-TestSql([string]$Database, [string]$Sql) {
                if ($Engine -eq 'mssql') {
                    $result = $Sql | & docker exec -i -e "SQLCMDPASSWORD=$password" $Container /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -d $Database -C -I -b -h -1 -W
                }
                else {
                    $result = $Sql | & docker exec -i $Container psql -X -U postgres -d $Database -v ON_ERROR_STOP=1 -tA -f -
                }
                if ($LASTEXITCODE -ne 0) { throw "Test SQL failed in $Database" }
                return $result
            }
            function Invoke-TestPackageCapture([string]$Label, $CapturedSourceFacts = $null) {
                $directory = Join-Path $TestDrive $Label
                $staging = Join-Path $directory 'staging'
                New-Item -ItemType Directory $staging | Out-Null
                $artifactName = if ($Engine -eq 'mssql') { 'Focused.Template.bak' } else { 'Focused.Template.sql' }
                # The verifier checks the packaged restore manifest against the restored database, so
                # build the package the way Build-TemplateNuGetPackage does: capture the source facts,
                # pass the DMS-only gate, dump, and write restore-manifest.json beside the artifact.
                & (Get-Module Template-Management) {
                    param($Engine, $Container, $Database, $SaSecret, $Directory, $ArtifactName, $CapturedSourceFacts)
                    $schemas = @(Get-UserSchemaNames -DatabaseEngine $Engine -ContainerName $Container -DatabaseName $Database -MssqlPassword $SaSecret)
                    $facts = $CapturedSourceFacts ?? (Get-TemplateSourceCatalogFacts -DatabaseEngine $Engine -ContainerName $Container -DatabaseName $Database -MssqlPassword $SaSecret -ArtifactSchemaName ([string[]]$schemas))
                    $partition = Assert-DmsOnlyTemplateSource -Facts $facts -DatabaseEngine $Engine -DatabaseName $Database
                    Invoke-DatabaseDump -DatabaseEngine $Engine -ContainerName $Container -DatabaseName $Database -DatabaseSchemas $schemas -BackupDirectory $Directory -BackupFileName $ArtifactName -MssqlPassword $SaSecret | Out-Null
                    Write-TemplateRestoreManifest -Facts $facts -Partition $partition -Config @{ Id = 'Focused.Template'; DatabaseBackupName = $ArtifactName } `
                        -PackageVersion '0.0.0-test' -DatabaseEngine $Engine -TemplateKind Minimal -StandardVersion '5.2.0' -BackupDirectory $Directory | Out-Null
                } $Engine $Container $source $password $staging $artifactName $CapturedSourceFacts
                Compress-Archive -Path (Join-Path $staging '*') -DestinationPath (Join-Path $directory 'package.zip')
                Move-Item (Join-Path $directory 'package.zip') (Join-Path $directory 'Focused.Template.nupkg')
                Remove-Item -LiteralPath $staging -Recurse -Force
                return $directory
            }
            # Re-adds one removed dms.Document column (the combined tree drops both ContentLastModifiedAt and
            # CreatedAt), so each legacy shape is proven rejected while the other column stays absent.
            function Invoke-TestDocumentStampMutation([bool]$Present, [string]$Column = 'ContentLastModifiedAt') {
                $constraint = "DF_Test_Old$Column"
                if ($Engine -eq 'mssql') {
                    $sql = if ($Present) { "ALTER TABLE dms.Document ADD $Column datetime2(7) NOT NULL CONSTRAINT $constraint DEFAULT SYSUTCDATETIME();" }
                    else { "ALTER TABLE dms.Document DROP CONSTRAINT $constraint; ALTER TABLE dms.Document DROP COLUMN $Column;" }
                }
                else {
                    $sql = if ($Present) { "ALTER TABLE dms.""Document"" ADD COLUMN ""$Column"" timestamptz NOT NULL DEFAULT now();" }
                    else { "ALTER TABLE dms.""Document"" DROP COLUMN ""$Column"";" }
                }
                Invoke-TestSql $source $sql | Out-Null
            }
            $fingerprintSql = if ($Engine -eq 'mssql') { 'SET NOCOUNT ON; SELECT EffectiveSchemaHash FROM dms.EffectiveSchema;' }
            else { 'SELECT "EffectiveSchemaHash" FROM dms."EffectiveSchema";' }
            Invoke-TestSql $master "CREATE DATABASE $source;" | Out-Null
            $ddl = Join-Path $fixture "$Dialect.sql"
            $remoteDdl = if ($Engine -eq 'mssql') { "/var/opt/mssql/data/$source.sql" } else { "/tmp/$source.sql" }
            & docker cp $ddl "$($Container):$remoteDdl"
            if ($LASTEXITCODE -ne 0) { throw 'Could not copy fixture DDL' }
            if ($Engine -eq 'mssql') {
                & docker exec -e "SQLCMDPASSWORD=$password" $Container /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -d $source -C -I -b -i $remoteDdl | Out-Null
            }
            else {
                & docker exec $Container psql -X -U postgres -d $source -v ON_ERROR_STOP=1 -f $remoteDdl | Out-Null
            }
            if ($LASTEXITCODE -ne 0) { throw 'Could not provision fixture' }
            & docker exec $Container rm -f $remoteDdl
            # This focused fixture has an extension-only sample schema. Add the empty
            # companion required by the template inventory gate for this synthetic package.
            Invoke-TestSql $source 'CREATE SCHEMA tracked_changes_sample;' | Out-Null
            $seed = if ($Engine -eq 'mssql') {
                @'
DECLARE @key smallint = (SELECT ResourceKeyId FROM dms.ResourceKey WHERE ProjectName = 'Ed-Fi' AND ResourceName = 'SchoolTypeDescriptor');
DECLARE @ids TABLE (id bigint);
INSERT INTO dms.Document (DocumentUuid, ResourceKeyId) OUTPUT inserted.DocumentId INTO @ids VALUES (NEWID(), @key);
INSERT INTO dms.Descriptor (DocumentId, ResourceKeyId, Namespace, CodeValue, ShortDescription)
SELECT id, @key, 'uri://template', 'school', 'School' FROM @ids;
'@
            }
            else {
                @'
WITH doc AS (INSERT INTO dms."Document" ("DocumentUuid", "ResourceKeyId")
SELECT gen_random_uuid(), "ResourceKeyId" FROM dms."ResourceKey" WHERE "ProjectName" = 'Ed-Fi' AND "ResourceName" = 'SchoolTypeDescriptor'
RETURNING "DocumentId", "ResourceKeyId")
INSERT INTO dms."Descriptor" ("DocumentId", "ResourceKeyId", "Namespace", "CodeValue", "ShortDescription")
SELECT "DocumentId", "ResourceKeyId", 'uri://template', 'school', 'School' FROM doc;
'@
            }
            Invoke-TestSql $source $seed | Out-Null
            $fingerprint = @(Invoke-TestSql $source $fingerprintSql)
            $script:correctPackage = Invoke-TestPackageCapture 'combined'
            Invoke-TestDocumentStampMutation $true
            try {
                @(Invoke-TestSql $source $fingerprintSql) | Should -Be $fingerprint
                $script:oldPackage = Invoke-TestPackageCapture 'prechange'
            }
            finally { Invoke-TestDocumentStampMutation $false }
            Invoke-TestDocumentStampMutation $true 'CreatedAt'
            try {
                @(Invoke-TestSql $source $fingerprintSql) | Should -Be $fingerprint
                $script:createdAtPackage = Invoke-TestPackageCapture 'prechange-createdat'
            }
            finally { Invoke-TestDocumentStampMutation $false 'CreatedAt' }
        }
        BeforeEach {
            # Preserve the real, already imported modules while replacing only the API boundary.
            Mock Import-Module {}
            Mock Get-CmsToken { throw 'API checkpoint after live catalogs and data' }
        }
        AfterAll {
            if ($source) {
                if ($Engine -eq 'mssql') {
                    Invoke-TestSql $master "IF DB_ID('$script:restored') IS NOT NULL BEGIN ALTER DATABASE [$script:restored] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$script:restored]; END; ALTER DATABASE [$source] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$source];" | Out-Null
                }
                else { Invoke-TestSql $master "DROP DATABASE IF EXISTS $script:restored WITH (FORCE); DROP DATABASE IF EXISTS $source WITH (FORCE);" | Out-Null }
            }
        }

        It 'Restores the correct package through both catalogs and data checks to the API boundary' {
            { & $script:verify -SourceDatabaseName $source -VerificationDatabaseName $script:restored -ExpectedTemplateKind Minimal -PackageDirectory $script:correctPackage -ExpectedModelManifestPath $script:manifest -DatabaseEngine $Engine -ContainerName $Container -MssqlPassword $password } | Should -Throw 'API checkpoint after live catalogs and data'
            Should -Invoke Get-CmsToken -Times 1 -Exactly
            @(Invoke-TestSql $script:restored $fingerprintSql) | Should -Be $fingerprint
        }
        It 'Rejects <shape> during native SQL Server source inspection' -Skip:($Engine -ne 'mssql') -ForEach @(
            $physicalDriftCases | Where-Object check -eq 'MssqlPhysicalSchemaVersion'
        ) {
            Invoke-TestSql $source $Sql | Out-Null
            try {
                { Get-TemplateSourceCatalogFacts -DatabaseEngine $Engine -ContainerName $Container -DatabaseName $source -MssqlPassword $password } |
                    Should -Throw '*physical schema marker*'
            }
            finally {
                Invoke-TestSql $source $Undo | Out-Null
            }
        }
        It 'Rejects <Name> during native SQL Server source inspection' -Skip:($Engine -ne 'mssql') -ForEach @(
            @{ Name = 'a markerless v1 baseline'; Column = 'nvarchar(max) NOT NULL'; HideMarker = $true; Message = '*physical schema marker*' }
            @{ Name = 'a legacy text column with a v2 marker'; Column = 'nvarchar(max) NOT NULL'; HideMarker = $false; Message = '*Cannot verify native json*' }
            @{ Name = 'a nullable native column'; Column = 'json NULL'; HideMarker = $false; Message = '*enabled and trusted CK_DocumentCache_IsJsonObject*' }
        ) {
            # The fixture seeds relational data directly and leaves the cache empty.
            $replaceColumn = @'
ALTER TABLE dms.DocumentCache DROP CONSTRAINT CK_DocumentCache_IsJsonObject;
ALTER TABLE dms.DocumentCache DROP COLUMN DocumentJson;
GO
'@
            $addCheck = 'ALTER TABLE dms.DocumentCache ADD CONSTRAINT CK_DocumentCache_IsJsonObject CHECK (ISJSON([DocumentJson], OBJECT) = 1);'
            try {
                Invoke-TestSql $source "$replaceColumn`nALTER TABLE dms.DocumentCache ADD DocumentJson $Column;" | Out-Null
                Invoke-TestSql $source $addCheck | Out-Null
                if ($HideMarker) {
                    Invoke-TestSql $source "EXEC sys.sp_rename 'dms.MssqlPhysicalSchema', 'SavedMssqlPhysicalSchema';" | Out-Null
                }
                { Get-TemplateSourceCatalogFacts -DatabaseEngine $Engine -ContainerName $Container -DatabaseName $source -MssqlPassword $password } |
                    Should -Throw $Message
            }
            finally {
                if ($HideMarker) {
                    Invoke-TestSql $source "IF OBJECT_ID('dms.SavedMssqlPhysicalSchema', 'U') IS NOT NULL EXEC sys.sp_rename 'dms.SavedMssqlPhysicalSchema', 'MssqlPhysicalSchema';" | Out-Null
                }
                Invoke-TestSql $source "$replaceColumn`nALTER TABLE dms.DocumentCache ADD DocumentJson json NOT NULL;" | Out-Null
                Invoke-TestSql $source $addCheck | Out-Null
            }
        }
        It 'Rejects a native SQL Server source and restored package with <shape>' -Skip:($Engine -ne 'mssql') -ForEach @(
            $physicalDriftCases | Where-Object check -eq 'MssqlDocumentCacheObjectCheck'
            @{
                shape = 'weaker object check'
                Sql = 'ALTER TABLE dms.DocumentCache DROP CONSTRAINT CK_DocumentCache_IsJsonObject; ALTER TABLE dms.DocumentCache ADD CONSTRAINT CK_DocumentCache_IsJsonObject CHECK (ISJSON([DocumentJson]) = 1);'
                Undo = 'ALTER TABLE dms.DocumentCache DROP CONSTRAINT CK_DocumentCache_IsJsonObject; ALTER TABLE dms.DocumentCache ADD CONSTRAINT CK_DocumentCache_IsJsonObject CHECK (ISJSON([DocumentJson], OBJECT) = 1);'
            }
        ) {
            $healthyFacts = Get-TemplateSourceCatalogFacts -DatabaseEngine $Engine -ContainerName $Container -DatabaseName $source -MssqlPassword $password
            Invoke-TestSql $source $Sql | Out-Null
            try {
                { Invoke-TestPackageCapture "invalid-$shape" } |
                    Should -Throw "*enabled and trusted CK_DocumentCache_IsJsonObject*"
                Test-Path (Join-Path $TestDrive "invalid-$shape/staging/Focused.Template.bak") | Should -BeFalse
                # Simulate a producer that did not inspect the CHECK, with a valid artifact hash.
                $invalidPackage = Invoke-TestPackageCapture "drifted-$shape" $healthyFacts
            }
            finally {
                Invoke-TestSql $source $Undo | Out-Null
            }
            { & $script:verify -SourceDatabaseName $source -VerificationDatabaseName $script:restored -ExpectedTemplateKind Minimal -PackageDirectory $invalidPackage -ExpectedModelManifestPath $script:manifest -DatabaseEngine $Engine -ContainerName $Container -MssqlPassword $password } |
                Should -Throw "*enabled and trusted CK_DocumentCache_IsJsonObject*"
            Should -Invoke Get-CmsToken -Times 0 -Exactly
        }
        It 'Rejects a disabled singleton CHECK before packaging or serving a restore' -Skip:($Engine -ne 'mssql') {
            $healthyFacts = Get-TemplateSourceCatalogFacts -DatabaseEngine $Engine -ContainerName $Container -DatabaseName $source -MssqlPassword $password
            Invoke-TestSql $source 'ALTER TABLE dms.MssqlPhysicalSchema NOCHECK CONSTRAINT CK_MssqlPhysicalSchema_Singleton;' | Out-Null
            try {
                { Invoke-TestPackageCapture 'disabled-singleton' } | Should -Throw '*physical schema marker*'
                Test-Path (Join-Path $TestDrive 'disabled-singleton/staging/Focused.Template.bak') | Should -BeFalse
                $invalidPackage = Invoke-TestPackageCapture 'drifted-singleton' $healthyFacts
            }
            finally {
                Invoke-TestSql $source 'ALTER TABLE dms.MssqlPhysicalSchema WITH CHECK CHECK CONSTRAINT CK_MssqlPhysicalSchema_Singleton;' | Out-Null
            }
            { & $script:verify -SourceDatabaseName $source -VerificationDatabaseName $script:restored -ExpectedTemplateKind Minimal -PackageDirectory $invalidPackage -ExpectedModelManifestPath $script:manifest -DatabaseEngine $Engine -ContainerName $Container -MssqlPassword $password } |
                Should -Throw '*physical schema marker*'
            Should -Invoke Get-CmsToken -Times 0 -Exactly
        }
        It 'Rejects a pre-change restored package with the same fingerprint before API setup' {
            { & $script:verify -SourceDatabaseName $source -VerificationDatabaseName $script:restored -ExpectedTemplateKind Minimal -PackageDirectory $script:oldPackage -ExpectedModelManifestPath $script:manifest -DatabaseEngine $Engine -ContainerName $Container -MssqlPassword $password } | Should -Throw "Template schema catalog assertions failed for '$script:restored'."
            Should -Invoke Get-CmsToken -Times 0 -Exactly
            @(Invoke-TestSql $script:restored $fingerprintSql) | Should -Be $fingerprint
        }
        It 'Rejects the old source before restoration or API setup' {
            Invoke-TestDocumentStampMutation $true
            try {
                # A deliberately nonexistent package proves the source gate precedes restore.
                { & $script:verify -SourceDatabaseName $source -VerificationDatabaseName $script:restored -ExpectedTemplateKind Minimal -PackageDirectory (Join-Path $TestDrive 'never-restored') -ExpectedModelManifestPath $script:manifest -DatabaseEngine $Engine -ContainerName $Container -MssqlPassword $password } | Should -Throw "Template schema catalog assertions failed for '$source'."
                Should -Invoke Get-CmsToken -Times 0 -Exactly
                @(Invoke-TestSql $source $fingerprintSql) | Should -Be $fingerprint
            }
            finally { Invoke-TestDocumentStampMutation $false }
        }
        It 'Rejects a pre-change restored package that still carries CreatedAt before API setup' {
            { & $script:verify -SourceDatabaseName $source -VerificationDatabaseName $script:restored -ExpectedTemplateKind Minimal -PackageDirectory $script:createdAtPackage -ExpectedModelManifestPath $script:manifest -DatabaseEngine $Engine -ContainerName $Container -MssqlPassword $password } | Should -Throw "*database '$script:restored' still carries CreatedAt*"
            Should -Invoke Get-CmsToken -Times 0 -Exactly
            @(Invoke-TestSql $script:restored $fingerprintSql) | Should -Be $fingerprint
        }
        It 'Rejects an old source that still carries CreatedAt before restoration or API setup' {
            Invoke-TestDocumentStampMutation $true 'CreatedAt'
            try {
                # A deliberately nonexistent package proves the source gate precedes restore.
                { & $script:verify -SourceDatabaseName $source -VerificationDatabaseName $script:restored -ExpectedTemplateKind Minimal -PackageDirectory (Join-Path $TestDrive 'never-restored') -ExpectedModelManifestPath $script:manifest -DatabaseEngine $Engine -ContainerName $Container -MssqlPassword $password } | Should -Throw "*database '$source' still carries CreatedAt*"
                Should -Invoke Get-CmsToken -Times 0 -Exactly
                @(Invoke-TestSql $source $fingerprintSql) | Should -Be $fingerprint
            }
            finally { Invoke-TestDocumentStampMutation $false 'CreatedAt' }
        }
    }
}
