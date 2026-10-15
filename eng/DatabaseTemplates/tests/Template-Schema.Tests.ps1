# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7
Describe 'Combined template schema manifest contract' {
    BeforeAll {
        $script:repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        Import-Module (Join-Path $PSScriptRoot '../Template-Schema.psm1') -Force
        $script:fixture = Join-Path $repo 'src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/compact-descriptor/expected'
        function Get-RenderedOwner([string]$Sql) {
            $json = [regex]::Match($Sql, "(?s)(?:owners jsonb := '|@timestampOwners nvarchar\(max\) = N')(.*?)'").Groups[1].Value
            @($json.Replace("''", "'") | ConvertFrom-Json)
        }
    }

    It 'Includes each root once and composes the compact gate for <Dialect>' -ForEach @(@{ Dialect = 'pgsql' }, @{ Dialect = 'mssql' }) {
        $sql = Get-TemplateSchemaAssertionSql -Dialect $Dialect -ExpectedModelManifestPath (Join-Path $fixture "relational-model.$Dialect.manifest.json")
        $owners = Get-RenderedOwner $sql
        @($owners | ForEach-Object { "$($_.schema).$($_.table)" }) | Should -Be @('dms.Descriptor', 'edfi.ProfileRootOnlyMergeItem', 'edfi.Student')
        $sql | Should -Match 'Timestamp baseline: forbidden column dms.Document.ContentLastModifiedAt'
        $sql | Should -Match 'Compact descriptor baseline:'
        $sql.IndexOf('Timestamp baseline:') | Should -BeLessThan $sql.IndexOf('Compact descriptor baseline:')
    }

    It 'Enumerates the complete authoritative core and extension resource roots for <Dialect>' -ForEach @(@{ Dialect = 'pgsql' }, @{ Dialect = 'mssql' }) {
        $path = Join-Path $repo "src/dms/backend/Fixtures/authoritative/ds-5.2-tpdm/expected/relational-model.$Dialect.manifest.json"
        $model = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
        $model.resource_details.Count | Should -Be $model.resources.Count
        $owners = Get-RenderedOwner (Get-TemplateSchemaAssertionSql -Dialect $Dialect -ExpectedModelManifestPath $path)
        $expected = @('dms.Descriptor') + @($model.resource_details | Where-Object storage_kind -CEQ RelationalTables | ForEach-Object {
                $_.tables | Where-Object scope -CEQ '$' | ForEach-Object { "$($_.schema).$($_.name)" }
            })
        @($owners | ForEach-Object { "$($_.schema).$($_.table)" }) | Should -Be @($expected | Sort-Object -Unique -CaseSensitive)
        @($owners | Where-Object schema -CEQ tpdm).Count | Should -BeGreaterThan 0
    }

    It 'Rejects <Mutation> instead of weakening timestamp expectations' -ForEach @(
        @{ Mutation = 'missing_details'; Diagnostic = '*resource_details*' },
        @{ Mutation = 'missing_root'; Diagnostic = '*exactly one root*' },
        @{ Mutation = 'missing_timestamp'; Diagnostic = '*authoritative stored timestamp*' },
        @{ Mutation = 'old_timestamp_role'; Diagnostic = '*authoritative stored timestamp*' }
    ) {
        $model = Get-Content (Join-Path $fixture 'relational-model.pgsql.manifest.json') -Raw | ConvertFrom-Json -AsHashtable
        $detail = $model.resource_details | Where-Object { $_.resource.resource_name -ceq 'Student' }
        $root = $detail.tables | Where-Object scope -CEQ '$'
        switch ($Mutation) {
            missing_details { $model.resource_details = @($model.resource_details | Where-Object { $_.resource.resource_name -cne 'Student' }) }
            missing_root { $root.scope = '$.children[*]' }
            missing_timestamp { $root.columns = @($root.columns | Where-Object name -CNE ContentLastModifiedAt) }
            old_timestamp_role { ($root.columns | Where-Object name -CEQ ContentLastModifiedAt).kind = 'MirroredContentLastModifiedAt' }
        }
        $path = Join-Path $TestDrive "$Mutation.json"
        $model | ConvertTo-Json -Depth 100 | Set-Content $path
        { Get-TemplateSchemaAssertionSql -Dialect pgsql -ExpectedModelManifestPath $path } | Should -Throw $Diagnostic
    }
}

Describe 'Template restore verification ordering' {
    BeforeAll {
        $script:templates = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        Import-Module (Join-Path $templates 'Template-Management.psm1') -Force
        Import-Module (Join-Path $templates '../Dms-Management.psm1') -Force
        $script:verify = Join-Path $templates 'verify-template-restore.ps1'
        Import-Module (Join-Path $templates 'Template-Schema.psm1') -Force
    }
    BeforeEach {
        $events = [System.Collections.Generic.List[string]]::new()
        $state = @{ Reject = '' }
        $manifest = Join-Path $TestDrive 'reviewed.json'
        '{}' | Set-Content $manifest
        Mock Import-Module {}
        Mock Get-TemplateSchemaAssertionSql {
            param($ExpectedModelManifestPath)
            $ExpectedModelManifestPath | Should -Be $manifest
            'catalog assertions'
        }
        Mock Get-UserSchemaNames { param($DatabaseName) $events.Add("schemas:$DatabaseName"); @('dms', 'edfi', 'tpdm') }
        # DMS-1403's dms.Document shape check runs beside these catalog assertions (source, then restored).
        Mock Assert-DocumentTableShape { param($DatabaseName) $events.Add("shape:$DatabaseName") }
        Mock Restore-TemplatePackage { $events.Add('restore'); 'Combined.nupkg' }
        Mock docker {
            $global:LASTEXITCODE = 0
            if ($args -contains '-i') {
                $database = $args[[array]::IndexOf($args, '-d') + 1]
                $events.Add("catalog:$database")
                if ($database -eq $state.Reject) { throw "Rejected catalog $database" }
            }
            else { '1' }
        }
        Mock Get-CmsToken { $events.Add('api'); throw 'API checkpoint' }
    }

    It 'Checks both catalogs and populated data before API setup for <Engine>' -ForEach @(@{ Engine = 'postgresql' }, @{ Engine = 'mssql' }) {
        { & $verify -SourceDatabaseName source -VerificationDatabaseName restored -ExpectedModelManifestPath $manifest -DatabaseEngine $Engine -RequirePopulatedData } | Should -Throw 'API checkpoint'
        $events | Should -Be @('catalog:source', 'shape:source', 'schemas:source', 'restore', 'catalog:restored', 'schemas:restored', 'shape:restored', 'api')
        Should -Invoke docker -Times 10 -Exactly
        Should -Invoke Restore-TemplatePackage -Times 1 -Exactly
    }

    It 'Rejects an incompatible <Rejected> catalog before API setup for <Engine>' -ForEach @(
        @{ Engine = 'postgresql'; Rejected = 'source' }, @{ Engine = 'postgresql'; Rejected = 'restored' },
        @{ Engine = 'mssql'; Rejected = 'source' }, @{ Engine = 'mssql'; Rejected = 'restored' }
    ) {
        $state.Reject = $Rejected
        { & $verify -SourceDatabaseName source -VerificationDatabaseName restored -ExpectedModelManifestPath $manifest -DatabaseEngine $Engine } | Should -Throw "Rejected catalog $Rejected"
        Should -Invoke Get-CmsToken -Times 0 -Exactly
        Should -Invoke docker -Times 0 -Exactly -ParameterFilter { $args -notcontains '-i' }
        if ($Rejected -eq 'source') { Should -Invoke Restore-TemplatePackage -Times 0 -Exactly }
        else { Should -Invoke Restore-TemplatePackage -Times 1 -Exactly }
    }

    It 'Rejects empty expected manifests before database work' {
        { & $verify -SourceDatabaseName source -ExpectedModelManifestPath '' } | Should -Throw '*empty*'
        Should -Invoke Restore-TemplatePackage -Times 0 -Exactly
        Should -Invoke Get-CmsToken -Times 0 -Exactly
    }
}
