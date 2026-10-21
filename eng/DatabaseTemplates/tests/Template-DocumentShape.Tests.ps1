# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

BeforeDiscovery {
    $script:engines = @(
        @{ engine = 'postgresql' }
        @{ engine = 'mssql' }
    )
}

Describe "Assert-DocumentTableShape" -ForEach $script:engines {
    BeforeAll {
        # Import with the module's own directory as the working directory, matching how the
        # template verifier invokes it.
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
        }
        finally {
            Pop-Location
        }

        $script:currentColumns = @('DocumentId', 'DocumentUuid', 'ResourceKeyId', 'CreatedByOwnershipTokenId', 'ContentVersion')
        $script:legacyColumns = $script:currentColumns + 'CreatedAt'
    }

    Context "on <engine>" {
        It "(a) passes for the current five-column table and queries the engine's catalog" {
            $queries = [System.Collections.Generic.List[string]]::new()
            $columns = $script:currentColumns

            { Assert-DocumentTableShape -DatabaseEngine $engine -DatabaseName 'src_db' -QueryRunner { param($Query) $queries.Add($Query); $columns } } |
                Should -Not -Throw

            $queries.Count | Should -Be 1
            if ($engine -eq 'mssql') {
                $queries[0] | Should -BeLike "*FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dms.Document') ORDER BY c.column_id;"
            }
            else {
                $queries[0] | Should -BeLike "*FROM information_schema.columns WHERE table_schema = 'dms' AND table_name = 'Document' ORDER BY ordinal_position;"
            }
        }

        It "(b) throws naming the database when the legacy CreatedAt column is present" {
            $columns = $script:legacyColumns

            { Assert-DocumentTableShape -DatabaseEngine $engine -DatabaseName 'src_db' -QueryRunner { $columns } } |
                Should -Throw "dms.Document in $engine database 'src_db' still carries CreatedAt.*"
        }

        It "(b) rejects a forbidden column regardless of its letter case" {
            $columns = $script:currentColumns + 'createdat'

            { Assert-DocumentTableShape -DatabaseEngine $engine -DatabaseName 'src_db' -QueryRunner { $columns } } |
                Should -Throw "*still carries CreatedAt*"
        }

        It "(b) rejects every column in -ForbiddenColumns" {
            $columns = $script:currentColumns + 'ContentLastModifiedAt'

            { Assert-DocumentTableShape -DatabaseEngine $engine -DatabaseName 'src_db' -ForbiddenColumns @('CreatedAt', 'ContentLastModifiedAt') -QueryRunner { $columns } } |
                Should -Throw "*still carries ContentLastModifiedAt*"
        }

        It "(c) throws 'missing' when the catalog returns no dms.Document columns" {
            { Assert-DocumentTableShape -DatabaseEngine $engine -DatabaseName 'src_db' -QueryRunner { @('', '  ') } } |
                Should -Throw "dms.Document is missing in $engine database 'src_db'."
        }

        It "(c) throws 'unexpected shape' when a required column is absent" {
            { Assert-DocumentTableShape -DatabaseEngine $engine -DatabaseName 'src_db' -QueryRunner { @('DocumentUuid', 'ContentVersion') } } |
                Should -Throw "Unexpected dms.Document shape in $engine database 'src_db': missing DocumentId.*"
        }

        It "(d) rethrows a query-runner failure instead of treating the column as absent" {
            $thrown = $null
            try {
                Assert-DocumentTableShape -DatabaseEngine $engine -DatabaseName 'src_db' -QueryRunner { throw 'connection refused' }
            }
            catch {
                $thrown = $_.Exception.Message
            }

            $thrown | Should -Be "Failed to read the dms.Document columns of $engine database 'src_db': connection refused"
        }

        It "rejects an unsafe database name before running any query" {
            $queries = [System.Collections.Generic.List[string]]::new()

            { Assert-DocumentTableShape -DatabaseEngine $engine -DatabaseName 'src;db' -QueryRunner { param($Query) $queries.Add($Query) } } |
                Should -Throw "Database name 'src;db' contains unsupported characters."
            $queries.Count | Should -Be 0
        }

        It "(e) default runner issues the engine's docker exec command and parses its output" {
            InModuleScope Template-Management -Parameters @{ engine = $engine } {
                param($engine)
                $calls = [System.Collections.Generic.List[object]]::new()
                Mock docker {
                    $calls.Add(@($args))
                    $global:LASTEXITCODE = 0
                    # Blank and padded lines prove the output is trimmed before it is compared.
                    @('', ' DocumentId ', ' DocumentUuid ', ' ResourceKeyId ', ' CreatedByOwnershipTokenId ', ' ContentVersion ', '')
                }

                Assert-DocumentTableShape -DatabaseEngine $engine -ContainerName 'db-container' -DatabaseName 'src_db' -MssqlPassword 'pw1!'

                $calls.Count | Should -Be 1
                if ($engine -eq 'mssql') {
                    ($calls[0] -join '|') | Should -Be (@(
                            'exec', '-e', 'SQLCMDPASSWORD=pw1!', 'db-container', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-d', 'src_db', '-C', '-b', '-h', '-1', '-W', '-Q',
                            "SET NOCOUNT ON; SELECT c.name FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dms.Document') ORDER BY c.column_id;"
                        ) -join '|')
                }
                else {
                    ($calls[0] -join '|') | Should -Be (@(
                            'exec', 'db-container', 'psql', '-U', 'postgres', '-d', 'src_db', '-tA', '-c',
                            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'dms' AND table_name = 'Document' ORDER BY ordinal_position;"
                        ) -join '|')
                }
            }
        }

        It "(e) default runner parses a legacy column out of the docker output" {
            InModuleScope Template-Management -Parameters @{ engine = $engine } {
                param($engine)
                Mock docker {
                    $global:LASTEXITCODE = 0
                    @('DocumentId', 'DocumentUuid', 'ResourceKeyId', 'CreatedByOwnershipTokenId', 'ContentVersion', 'ContentLastModifiedAt', 'CreatedAt')
                }

                { Assert-DocumentTableShape -DatabaseEngine $engine -DatabaseName 'restored_db' } |
                    Should -Throw "dms.Document in $engine database 'restored_db' still carries CreatedAt.*"
            }
        }

        It "(d) default runner fails on a non-zero docker exit code, not with the 'missing' message" {
            InModuleScope Template-Management -Parameters @{ engine = $engine } {
                param($engine)
                Mock docker { $global:LASTEXITCODE = 1 }

                { Assert-DocumentTableShape -DatabaseEngine $engine -DatabaseName 'src_db' } |
                    Should -Throw "Failed to read the dms.Document columns of $engine database 'src_db': Query exited with code 1."
            }
        }
    }
}

Describe "verify-template-restore.ps1 dms.Document shape checks" -ForEach $script:engines {
    BeforeAll {
        # Commands must exist in this scope to be mocked. The script re-imports both modules;
        # plain (non -ModuleName) mocks still take precedence for the script's calls.
        $script:templatesDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Push-Location $script:templatesDir
        try {
            Import-Module (Join-Path $script:templatesDir "Template-Management.psm1") -Force
            Import-Module (Join-Path $script:templatesDir "../Dms-Management.psm1") -Force
            Import-Module (Join-Path $script:templatesDir "Template-Schema.psm1") -Force
            Import-Module (Join-Path $script:templatesDir "Template-RestoreCore.psm1") -Force
            Import-Module (Join-Path $script:templatesDir "Template-RestoreTrust.psm1") -Force
        }
        finally {
            Pop-Location
        }

        $script:verifyScript = Join-Path $script:templatesDir "verify-template-restore.ps1"
    }

    BeforeEach {
        $callLog = [System.Collections.Generic.List[string]]::new()

        Mock Assert-DocumentTableShape { $callLog.Add("Assert-DocumentTableShape:${DatabaseEngine}:${DatabaseName}") }
        # DMS-1401's catalog assertions also run (Assert-TemplateSchema.ps1); render a placeholder script for the
        # mocked docker. Their ordering is pinned in Template-Schema.Tests.ps1.
        $manifest = Join-Path $TestDrive 'reviewed.json'
        '{}' | Set-Content $manifest
        Mock Get-TemplateSchemaAssertionSql { 'catalog assertions' }
        Mock Get-UserSchemaNames { @('dms', 'edfi') }
        Mock Restore-TemplatePackage { $callLog.Add('Restore-TemplatePackage'); 'Fake.nupkg' }
        # The packaged restore manifest is verified against the restored database before the
        # API probe; its own contract and ordering are pinned in Template-Schema.Tests.ps1.
        Mock Get-ChildItem -ParameterFilter { $Filter -eq '*.nupkg' } { [pscustomobject]@{ Name = 'Fake.nupkg'; FullName = 'Fake.nupkg' } }
        Mock Get-ChildItem -ParameterFilter { $Path -like '*template-manifest-verify-*' } { [pscustomobject]@{ Name = 'entry'; FullName = 'entry' } }
        Mock Copy-Item {}
        Mock Expand-Archive {}
        Mock Read-RestoreManifest {
            [pscustomobject]@{ templateKind = 'Minimal'; databaseEngine = $engine; artifactFileName = 'dump'; artifactSha256 = 'sha'; projects = @('ed-fi') }
        }
        Mock Get-FileSha256Hex { 'sha' }
        Mock Get-TemplateSourceCatalogFacts { [pscustomobject]@{ FullInventory = @(); DatabaseLevelObjects = @() } }
        Mock Assert-DmsOnlyInventory { [pscustomobject]@{ ProjectSchemaNames = @('ed-fi') } }
        Mock Assert-RestoreManifestMatchesDatabase {}
        # Serves the Invoke-*Scalar count queries and `docker restart`.
        Mock docker { $global:LASTEXITCODE = 0; '1' }
        Mock Get-CmsToken { $callLog.Add('Get-CmsToken'); 'cms-token' }
        Mock New-DataStoreConnectionString { 'Server=fake' }
        Mock Add-DataStore { 7 }
        Mock Add-Vendor { 3 }
        Mock Add-Application { [pscustomobject]@{ Key = 'key'; Secret = 'secret' } }
        Mock Wait-CmsClientAvailable { }
        Mock Get-DmsToken { 'dms-token' }
        Mock Invoke-WebRequest { [pscustomobject]@{ StatusCode = 200; Content = '[{"id":"a"}]' } }
    }

    Context "on <engine>" {
        It "(f1) a source shape failure stops the script before the restore" {
            Mock Assert-DocumentTableShape { throw "dms.Document in $DatabaseEngine database '$DatabaseName' still carries CreatedAt." } -ParameterFilter { $DatabaseName -eq 'src_db' }

            { & $script:verifyScript -SourceDatabaseName src_db -PackageDirectory $TestDrive -ExpectedTemplateKind Minimal -ExpectedModelManifestPath $manifest -DatabaseEngine $engine } |
                Should -Throw "*database 'src_db' still carries CreatedAt*"

            Should -Invoke Assert-DocumentTableShape -Times 1 -Exactly -ParameterFilter { $DatabaseName -eq 'src_db' -and $DatabaseEngine -eq $engine }
            Should -Invoke Restore-TemplatePackage -Times 0 -Exactly
            Should -Invoke Get-CmsToken -Times 0 -Exactly
        }

        It "(f2) a restored shape failure stops the script before the API probe" {
            Mock Assert-DocumentTableShape { throw "dms.Document in $DatabaseEngine database '$DatabaseName' still carries CreatedAt." } -ParameterFilter { $DatabaseName -eq 'template_restore_verification' }

            { & $script:verifyScript -SourceDatabaseName src_db -PackageDirectory $TestDrive -ExpectedTemplateKind Minimal -ExpectedModelManifestPath $manifest -DatabaseEngine $engine } |
                Should -Throw "*database 'template_restore_verification' still carries CreatedAt*"

            Should -Invoke Restore-TemplatePackage -Times 1 -Exactly
            Should -Invoke Assert-DocumentTableShape -Times 1 -Exactly -ParameterFilter { $DatabaseName -eq 'template_restore_verification' -and $DatabaseEngine -eq $engine }
            Should -Invoke Get-CmsToken -Times 0 -Exactly
            Should -Invoke Invoke-WebRequest -Times 0 -Exactly
        }

        It "(f3) checks the source before the restore and the restored database before the API probe" {
            { & $script:verifyScript -SourceDatabaseName src_db -PackageDirectory $TestDrive -ExpectedTemplateKind Minimal -ExpectedModelManifestPath $manifest -DatabaseEngine $engine } |
                Should -Not -Throw

            ($callLog -join ' > ') | Should -Be (@(
                    "Assert-DocumentTableShape:${engine}:src_db"
                    'Restore-TemplatePackage'
                    "Assert-DocumentTableShape:${engine}:template_restore_verification"
                    'Get-CmsToken'
                ) -join ' > ')
            Should -Invoke Get-CmsToken -Times 1 -Exactly
        }
    }
}
