# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7
Describe 'Compact descriptor manifest inventory' {
    BeforeAll {
        $repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        Import-Module (Join-Path $PSScriptRoot '../Compact-Descriptor.psm1') -Force
        $script:fixture = Join-Path $repo 'src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/compact-descriptor/expected'
    }

    It 'Selects exactly canonical storage, including extension/nested/abstract/composite shapes for <dialect>' -ForEach @(@{ Dialect = 'pgsql' }, @{ Dialect = 'mssql' }) {
        $inventory = Read-CompactDescriptorInventory -Dialect $dialect -ExpectedModelManifestPath (Join-Path $fixture "relational-model.$dialect.manifest.json")
        $inventory.columns.Count | Should -Be 7
        $inventory.aliases.name | Should -Be @('PrimarySchoolTypeDescriptor_DescriptorId', 'SecondarySchoolTypeDescriptor_DescriptorId')
        $inventory.data_schemas | Should -Be @('edfi', 'sample', 'tracked_changes_edfi')
        @($inventory.columns | Where-Object { $_.schema -ceq 'sample' }).Count | Should -Be 1
        @($inventory.columns | Where-Object { $_.table -ceq 'DescriptorSubjectIdentity' }).Count | Should -Be 1
        @($inventory.columns | Where-Object { $_.table -ceq 'ProfileRootOnlyMergeItemItemNested' }).Count | Should -Be 1
        @($inventory.columns | Where-Object { $_.name -ceq 'PrimarySchoolTypeDescriptor_DescriptorId' }).Count | Should -Be 0
        @($inventory.columns | Where-Object { $_.scalar_type -cne 'Int32' }).Count | Should -Be 0
        $composite = @($inventory.constraints | Where-Object { $_.definition.name -ceq 'FK_ProfileRootOnlyMergeItem_StudentReference_RefKey' })
        $composite.Count | Should -Be 1
        $composite[0].definition.columns | Should -Be @('StudentReference_StudentUniqueId', 'StudentReference_SchoolTypeDescriptor_DescriptorId', 'StudentReference_DocumentId')
        $composite[0].definition.target_columns | Should -Be @('StudentUniqueId', 'SchoolTypeDescriptor_DescriptorId', 'DocumentId')
        @($inventory.indexes | Where-Object { $_.name -ceq 'UX_Student_RefKey' }).Count | Should -Be 1
    }

    It 'Rejects omitted inventory fields and incomplete resource details' {
        $model = Get-Content (Join-Path $fixture 'relational-model.pgsql.manifest.json') -Raw | ConvertFrom-Json -AsHashtable
        $model.Remove('resource_details')
        $path = Join-Path $TestDrive 'missing.json'
        ConvertTo-Json -InputObject $model -Depth 100 | Set-Content $path
        { Read-CompactDescriptorInventory -Dialect pgsql -ExpectedModelManifestPath $path } | Should -Throw '*missing resource_details*'
    }

    It 'De-duplicates repeated physical tables across resource_details' {
        $model = Get-Content (Join-Path $fixture 'relational-model.pgsql.manifest.json') -Raw | ConvertFrom-Json -AsHashtable
        $student = $model.resource_details | Where-Object { $_.resource.resource_name -ceq 'Student' }
        $root = $model.resource_details | Where-Object { $_.resource.resource_name -ceq 'ProfileRootOnlyMergeItem' }
        $root.tables += $student.tables[0]
        $path = Join-Path $TestDrive 'shared-table.json'
        ConvertTo-Json -InputObject $model -Depth 100 | Set-Content $path
        $inventory = Read-CompactDescriptorInventory -Dialect pgsql -ExpectedModelManifestPath $path
        $inventory.columns.Count | Should -Be 7
        @($inventory.columns | Where-Object { $_.table -ceq 'Student' }).Count | Should -Be 1
    }

    It 'Rejects wide aliases even though they are excluded from writable storage' {
        $model = Get-Content (Join-Path $fixture 'relational-model.pgsql.manifest.json') -Raw | ConvertFrom-Json -AsHashtable
        $alias = $model.resource_details.tables.columns | Where-Object { $_.kind -ceq 'DescriptorFk' -and $_.storage.kind -ceq 'UnifiedAlias' } | Select-Object -First 1
        $alias.type.kind = 'Int64'
        $path = Join-Path $TestDrive 'wide-alias.json'
        ConvertTo-Json -InputObject $model -Depth 100 | Set-Content $path
        { Read-CompactDescriptorInventory -Dialect pgsql -ExpectedModelManifestPath $path } | Should -Throw '*Pre-compact*'
    }

    It 'Rejects an alias without canonical stored descriptor coverage' {
        $model = Get-Content (Join-Path $fixture 'relational-model.pgsql.manifest.json') -Raw | ConvertFrom-Json -AsHashtable
        $alias = $model.resource_details.tables.columns | Where-Object { $_.kind -ceq 'DescriptorFk' -and $_.storage.kind -ceq 'UnifiedAlias' } | Select-Object -First 1
        $alias.storage.canonical_column = 'MissingCanonicalColumn'
        $path = Join-Path $TestDrive 'missing-canonical.json'
        ConvertTo-Json -InputObject $model -Depth 100 | Set-Content $path
        { Read-CompactDescriptorInventory -Dialect pgsql -ExpectedModelManifestPath $path } | Should -Throw '*Missing canonical*'
    }

    It 'Rejects old physically stored URI in the shared descriptor model' {
        $model = Get-Content (Join-Path $fixture 'relational-model.pgsql.manifest.json') -Raw | ConvertFrom-Json -AsHashtable
        $shared = $model.resource_details | Where-Object { $_.storage_kind -ceq 'SharedDescriptorTable' } | Select-Object -First 1
        $shared.shared_descriptor_table.columns += @{ name = 'Uri'; kind = 'Scalar'; type = @{ kind = 'String' }; storage = @{ kind = 'Stored' }; is_nullable = $false }
        $path = Join-Path $TestDrive 'old-shared-uri.json'
        ConvertTo-Json -InputObject $model -Depth 100 | Set-Content $path
        { Read-CompactDescriptorInventory -Dialect pgsql -ExpectedModelManifestPath $path } | Should -Throw '*Pre-compact shared*'
    }
}
