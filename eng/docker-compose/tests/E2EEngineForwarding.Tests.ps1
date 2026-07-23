# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# DMS-1284: the standard DMS E2E orchestration resolves the environment once (data-standard overlay
# first, then the database-engine overlay) and builds two opaque connection strings from the resolved
# values. These tests invoke the module-level primitives that orchestration composes (rather than
# asserting on build-dms.ps1 text) to prove default/explicit engine behavior, that the engine overlay
# is applied on top of a prior (data-standard) composition, and that the resolved environment's values
# flow into the connection strings the test process consumes.

param()

Describe "E2E engine resolution and connection-string forwarding (DMS-1284)" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
        Import-Module ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../Dms-Management.psm1"))) -Force
    }

    AfterAll {
        Remove-Module Dms-Management -Force -ErrorAction SilentlyContinue
    }

    Context "engine defaulting" {
        It "produces the PostgreSQL connection strings when no engine is specified" {
            $result = New-E2EDataStoreConnectionStrings -EnvironmentValues @{} -DatabaseName "edfi_e2e"

            $result.AdminConnectionString | Should -Match "^host=localhost;"
            $result.RegistrationConnectionString | Should -Match "^host=dms-postgresql;"
        }
    }

    Context "resolve-then-build flow with the engine overlay applied on top of a prior composition" {
        BeforeEach {
            $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-e2e-engine-$([Guid]::NewGuid().ToString('N'))"
            $script:composeRoot = Join-Path $script:work "compose"
            New-Item -ItemType Directory -Path $script:composeRoot -Force | Out-Null

            # A base that already carries a data-standard marker (as if Resolve-DataStandardEnvironmentFile
            # had run first) plus the E2E database name. The engine overlay must be applied on top and must
            # preserve the prior values.
            $script:basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $script:basePath -NoNewline -Value @"
DMS_DATASTORE=postgresql
DMS_CONFIG_DATASTORE=postgresql
DATA_STANDARD_MARKER=ds61
E2E_DATABASE_NAME=edfi_datamanagementservice_e2e
POSTGRES_DB_NAME=edfi_datamanagementservice
"@

            # Minimal stand-in for the real .env.mssql overlay (the real key set is covered by
            # DatabaseEngineEnvironmentFile.Tests.ps1); it flips the datastore keys to mssql.
            Set-Content -LiteralPath (Join-Path $script:composeRoot ".env.mssql") -NoNewline -Value @"
MSSQL_SA_PASSWORD=Abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
MSSQL_PORT=1435
DMS_DATASTORE=mssql
DATABASE_CONNECTION_STRING_ADMIN=Server=dms-mssql;Database=`${MSSQL_DB_NAME};User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
DMS_CONFIG_DATASTORE=mssql
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=`${MSSQL_DB_NAME};User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
"@
        }

        AfterEach {
            if (Test-Path -LiteralPath $script:work) {
                Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It "flips the datastore to mssql while preserving the prior data-standard values" {
            $resolved = Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine mssql `
                -BaseEnvironmentFile $script:basePath `
                -DockerComposeRoot $script:composeRoot `
                -SkipMssqlCmsDatabaseValidation

            $values = ReadValuesFromEnvFile $resolved
            $values["DMS_DATASTORE"] | Should -Be "mssql"
            $values["DATA_STANDARD_MARKER"] | Should -Be "ds61"
            $values["E2E_DATABASE_NAME"] | Should -Be "edfi_datamanagementservice_e2e"
        }

        It "builds mssql connection strings from the resolved environment and the E2E database name" {
            $resolved = Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine mssql `
                -BaseEnvironmentFile $script:basePath `
                -DockerComposeRoot $script:composeRoot `
                -SkipMssqlCmsDatabaseValidation
            $values = ReadValuesFromEnvFile $resolved

            $connectionStrings = New-E2EDataStoreConnectionStrings `
                -DatabaseEngine mssql `
                -EnvironmentValues $values `
                -DatabaseName ([string]$values["E2E_DATABASE_NAME"])

            $connectionStrings.AdminConnectionString |
                Should -Be "Server=127.0.0.1,1435;Database=edfi_datamanagementservice_e2e;User Id=sa;Password=Abcdefgh1!;TrustServerCertificate=true"
            $connectionStrings.RegistrationConnectionString |
                Should -Be "Server=dms-mssql,1433;Database=edfi_datamanagementservice_e2e;User Id=sa;Password=Abcdefgh1!;TrustServerCertificate=true"
        }

        It "keeps the datastore as mssql when the engine overlay is composed again (idempotent)" {
            $resolvedOnce = Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine mssql `
                -BaseEnvironmentFile $script:basePath `
                -DockerComposeRoot $script:composeRoot `
                -SkipMssqlCmsDatabaseValidation
            $resolvedTwice = Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine mssql `
                -BaseEnvironmentFile $resolvedOnce `
                -DockerComposeRoot $script:composeRoot `
                -SkipMssqlCmsDatabaseValidation

            (ReadValuesFromEnvFile $resolvedTwice)["DMS_DATASTORE"] | Should -Be "mssql"
        }

        It "leaves the base file unchanged for the postgresql engine (no-op overlay)" {
            $resolved = Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine postgresql `
                -BaseEnvironmentFile $script:basePath `
                -DockerComposeRoot $script:composeRoot

            $resolved | Should -Be $script:basePath
            (ReadValuesFromEnvFile $resolved)["DMS_DATASTORE"] | Should -Be "postgresql"
        }
    }
}
