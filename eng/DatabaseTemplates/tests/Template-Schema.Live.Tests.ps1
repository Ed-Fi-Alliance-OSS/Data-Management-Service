# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7
# Containers must be exclusively owned disposable test instances. Tests create/drop only
# uniquely named databases. The synthetic package proves the real restore/catalog/data path;
# API registration is intercepted at its first call (full serveability is the release matrix).
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
            function Invoke-TestPackageCapture([string]$Label) {
                $directory = Join-Path $TestDrive $Label
                New-Item -ItemType Directory $directory | Out-Null
                if ($Engine -eq 'mssql') {
                    $remote = "/var/opt/mssql/data/$source.bak"
                    Invoke-TestSql $master "BACKUP DATABASE [$source] TO DISK = N'$remote' WITH INIT;" | Out-Null
                    $artifact = Join-Path $directory 'template.bak'
                    & docker cp "$($Container):$remote" $artifact
                    if ($LASTEXITCODE -ne 0) { throw 'Could not copy test backup' }
                    & docker exec $Container rm -f $remote
                }
                else {
                    $artifact = Join-Path $directory 'template.sql'
                    & docker exec $Container pg_dump -U postgres --no-owner --no-privileges $source | Set-Content $artifact
                    if ($LASTEXITCODE -ne 0) { throw 'Could not dump test database' }
                }
                Compress-Archive -Path $artifact -DestinationPath (Join-Path $directory 'package.zip')
                Move-Item (Join-Path $directory 'package.zip') (Join-Path $directory 'Focused.Template.nupkg')
                return $directory
            }
            function Invoke-TestDocumentStampMutation([bool]$Present) {
                if ($Engine -eq 'mssql') {
                    $sql = if ($Present) { 'ALTER TABLE dms.Document ADD ContentLastModifiedAt datetime2(7) NOT NULL CONSTRAINT DF_Test_OldStamp DEFAULT SYSUTCDATETIME();' }
                    else { 'ALTER TABLE dms.Document DROP CONSTRAINT DF_Test_OldStamp; ALTER TABLE dms.Document DROP COLUMN ContentLastModifiedAt;' }
                }
                else {
                    $sql = if ($Present) { 'ALTER TABLE dms."Document" ADD COLUMN "ContentLastModifiedAt" timestamptz NOT NULL DEFAULT now();' }
                    else { 'ALTER TABLE dms."Document" DROP COLUMN "ContentLastModifiedAt";' }
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
            { & $script:verify -SourceDatabaseName $source -VerificationDatabaseName $script:restored -PackageDirectory $script:correctPackage -ExpectedModelManifestPath $script:manifest -DatabaseEngine $Engine -ContainerName $Container -MssqlPassword $password } | Should -Throw 'API checkpoint after live catalogs and data'
            Should -Invoke Get-CmsToken -Times 1 -Exactly
            @(Invoke-TestSql $script:restored $fingerprintSql) | Should -Be $fingerprint
        }
        It 'Rejects a pre-change restored package with the same fingerprint before API setup' {
            { & $script:verify -SourceDatabaseName $source -VerificationDatabaseName $script:restored -PackageDirectory $script:oldPackage -ExpectedModelManifestPath $script:manifest -DatabaseEngine $Engine -ContainerName $Container -MssqlPassword $password } | Should -Throw "Template schema catalog assertions failed for '$script:restored'."
            Should -Invoke Get-CmsToken -Times 0 -Exactly
            @(Invoke-TestSql $script:restored $fingerprintSql) | Should -Be $fingerprint
        }
        It 'Rejects the old source before restoration or API setup' {
            Invoke-TestDocumentStampMutation $true
            try {
                # A deliberately nonexistent package proves the source gate precedes restore.
                { & $script:verify -SourceDatabaseName $source -VerificationDatabaseName $script:restored -PackageDirectory (Join-Path $TestDrive 'never-restored') -ExpectedModelManifestPath $script:manifest -DatabaseEngine $Engine -ContainerName $Container -MssqlPassword $password } | Should -Throw "Template schema catalog assertions failed for '$source'."
                Should -Invoke Get-CmsToken -Times 0 -Exactly
                @(Invoke-TestSql $source $fingerprintSql) | Should -Be $fingerprint
            }
            finally { Invoke-TestDocumentStampMutation $false }
        }
    }
}
