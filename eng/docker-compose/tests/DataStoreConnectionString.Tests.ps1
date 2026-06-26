# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# DMS-1238: the Configuration Service data-store connection string is engine-aware so the local
# MSSQL stack registers a SQL Server connection string the provision phase can then provision.

param()

Describe "New-DataStoreConnectionString (DMS-1238)" {
    BeforeAll {
        $script:dmsManagementModule = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../Dms-Management.psm1"))
        Import-Module $script:dmsManagementModule -Force
    }

    AfterAll {
        Remove-Module Dms-Management -Force -ErrorAction SilentlyContinue
    }

    It "builds a SQL Server connection string for the mssql engine" {
        $cs = New-DataStoreConnectionString `
            -DatabaseEngine mssql `
            -DbHost "dms-mssql" `
            -Port 1433 `
            -Username "sa" `
            -Password "Abcdefgh1!" `
            -DatabaseName "edfi_datamanagementservice"

        $cs | Should -Be "Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=Abcdefgh1!;TrustServerCertificate=true;"
    }

    It "builds a PostgreSQL connection string for the postgresql engine" {
        $cs = New-DataStoreConnectionString `
            -DatabaseEngine postgresql `
            -DbHost "dms-postgresql" `
            -Port 5432 `
            -Username "postgres" `
            -Password "abcdefgh1!" `
            -DatabaseName "edfi_datamanagementservice"

        $cs | Should -Be "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;"
    }

    It "defaults to the PostgreSQL form when no engine is specified" {
        $cs = New-DataStoreConnectionString `
            -DbHost "dms-postgresql" `
            -Port 5432 `
            -Username "postgres" `
            -Password "p" `
            -DatabaseName "db"

        $cs | Should -Match "^host=dms-postgresql;port=5432;"
    }
}

Describe "configure-local-data-store.ps1 MSSQL data-store wiring (DMS-1238)" {
    BeforeAll {
        $script:configureScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../configure-local-data-store.ps1"))
        $script:configureSource = Get-Content -LiteralPath $script:configureScript -Raw
    }

    It "declares a -DatabaseEngine parameter validated to postgresql/mssql" {
        $script:configureSource | Should -Match '\[ValidateSet\("postgresql",\s*"mssql"\)\]'
        $script:configureSource | Should -Match '\$DatabaseEngine'
    }

    It "builds the MSSQL data-store connection string via New-DataStoreConnectionString for the mssql engine" {
        $script:configureSource | Should -Match 'if \(\$DatabaseEngine -eq "mssql"\)'
        $script:configureSource | Should -Match 'New-DataStoreConnectionString'
        $script:configureSource | Should -Match '-DbHost "dms-mssql"'
    }

    It "forwards the resolved connection string to the data-store creation calls" {
        # Both the default single-data-store path and the school-year path must forward it.
        ([regex]::Matches($script:configureSource, '-ConnectionString \$dataStoreConnectionString')).Count |
            Should -BeGreaterOrEqual 2
    }
}
