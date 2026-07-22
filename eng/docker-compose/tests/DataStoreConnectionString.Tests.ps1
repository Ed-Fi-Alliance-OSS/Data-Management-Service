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

Describe "New-E2EDataStoreConnectionStrings (DMS-1284)" {
    BeforeAll {
        $script:dmsManagementModule = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../Dms-Management.psm1"))
        Import-Module $script:dmsManagementModule -Force
    }

    AfterAll {
        Remove-Module Dms-Management -Force -ErrorAction SilentlyContinue
    }

    It "builds host-side reset and Docker-network registration strings for postgresql (default engine)" {
        $envValues = @{ POSTGRES_PASSWORD = "abcdefgh1!"; POSTGRES_PORT = "5435" }

        $result = New-E2EDataStoreConnectionStrings -EnvironmentValues $envValues -DatabaseName "edfi_e2e"

        $result.AdminConnectionString |
            Should -Be "host=localhost;port=5435;username=postgres;password=abcdefgh1!;database=edfi_e2e;NoResetOnClose=true;"
        $result.RegistrationConnectionString |
            Should -Be "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_e2e;"
    }

    It "builds host-side reset and Docker-network registration strings for mssql" {
        $envValues = @{ MSSQL_SA_PASSWORD = "Abcdefgh1!"; MSSQL_PORT = "1435" }

        $result = New-E2EDataStoreConnectionStrings -DatabaseEngine mssql -EnvironmentValues $envValues -DatabaseName "edfi_e2e"

        $result.AdminConnectionString |
            Should -Be "Server=127.0.0.1,1435;Database=edfi_e2e;User Id=sa;Password=Abcdefgh1!;TrustServerCertificate=true;"
        $result.RegistrationConnectionString |
            Should -Be "Server=dms-mssql,1433;Database=edfi_e2e;User Id=sa;Password=Abcdefgh1!;TrustServerCertificate=true;"
    }

    It "separates the host-side reset host/port from the Docker-network registration host/port" {
        $pg = New-E2EDataStoreConnectionStrings -DatabaseEngine postgresql -EnvironmentValues @{ POSTGRES_PORT = "5435" } -DatabaseName "db"
        $pg.AdminConnectionString | Should -Match "host=localhost;port=5435;"
        $pg.RegistrationConnectionString | Should -Match "host=dms-postgresql;port=5432;"

        $mssql = New-E2EDataStoreConnectionStrings -DatabaseEngine mssql -EnvironmentValues @{ MSSQL_PORT = "1435" } -DatabaseName "db"
        $mssql.AdminConnectionString | Should -Match "Server=127\.0\.0\.1,1435;"
        $mssql.RegistrationConnectionString | Should -Match "Server=dms-mssql,1433;"
    }

    It "honors custom postgresql user, password, port, and database name from the resolved environment" {
        $envValues = @{ POSTGRES_USER = "customuser"; POSTGRES_PASSWORD = "custompass"; POSTGRES_PORT = "6543" }

        $result = New-E2EDataStoreConnectionStrings -DatabaseEngine postgresql -EnvironmentValues $envValues -DatabaseName "custom_db"

        $result.AdminConnectionString |
            Should -Be "host=localhost;port=6543;username=customuser;password=custompass;database=custom_db;NoResetOnClose=true;"
        $result.RegistrationConnectionString |
            Should -Be "host=dms-postgresql;port=5432;username=customuser;password=custompass;database=custom_db;"
    }

    It "honors custom mssql password and port from the resolved environment" {
        $envValues = @{ MSSQL_SA_PASSWORD = "Custom!Pass9"; MSSQL_PORT = "14333" }

        $result = New-E2EDataStoreConnectionStrings -DatabaseEngine mssql -EnvironmentValues $envValues -DatabaseName "custom_db"

        $result.AdminConnectionString |
            Should -Be "Server=127.0.0.1,14333;Database=custom_db;User Id=sa;Password=Custom!Pass9;TrustServerCertificate=true;"
        $result.RegistrationConnectionString |
            Should -Be "Server=dms-mssql,1433;Database=custom_db;User Id=sa;Password=Custom!Pass9;TrustServerCertificate=true;"
    }

    It "appends NoResetOnClose only to the postgresql host-side reset string" {
        $result = New-E2EDataStoreConnectionStrings -DatabaseEngine postgresql -EnvironmentValues @{} -DatabaseName "db"

        $result.AdminConnectionString | Should -Match "NoResetOnClose=true;$"
        $result.RegistrationConnectionString | Should -Not -Match "NoResetOnClose"
    }

    It "falls back to documented dev defaults when the environment omits credentials and ports" {
        $pg = New-E2EDataStoreConnectionStrings -DatabaseEngine postgresql -EnvironmentValues @{} -DatabaseName "db"
        $pg.AdminConnectionString |
            Should -Be "host=localhost;port=5435;username=postgres;password=abcdefgh1!;database=db;NoResetOnClose=true;"

        $mssql = New-E2EDataStoreConnectionStrings -DatabaseEngine mssql -EnvironmentValues @{} -DatabaseName "db"
        $mssql.AdminConnectionString |
            Should -Be "Server=127.0.0.1,1435;Database=db;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;"
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

    It "composes the MSSQL engine overlay after resolving the environment file and before reading env values" {
        $resolveIndex = $script:configureSource.IndexOf('$resolvedEnvironmentFile = Resolve-ConfigureEnvironmentFile -Path $EnvironmentFile')
        $engineIndex = $script:configureSource.IndexOf('$resolvedEnvironmentFile = Resolve-DatabaseEngineEnvironmentFile')
        $readValuesIndex = $script:configureSource.IndexOf('$envValues = ReadValuesFromEnvFile -EnvironmentFile $resolvedEnvironmentFile')

        $resolveIndex | Should -BeGreaterThan -1
        $engineIndex | Should -BeGreaterThan $resolveIndex
        $readValuesIndex | Should -BeGreaterThan $engineIndex

        $script:configureSource | Should -Match 'Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine \$DatabaseEngine -BaseEnvironmentFile \$resolvedEnvironmentFile -DockerComposeRoot \$PSScriptRoot'
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

    It "defaults MSSQL_SA_PASSWORD to the documented dev password when the env file omits it" {
        # Matches the abcdefgh1! default baked into mssql.yml and start-local-dms.ps1's
        # readiness poll, so a custom env file that omits the key does not bring SQL Server
        # up on the default password while leaving the resolved connection string blank.
        $script:configureSource | Should -Match 'Get-EnvValueOrDefault -EnvValues \$envValues -Name "MSSQL_SA_PASSWORD" -DefaultValue "abcdefgh1!"'
    }

    It "honors -DataStoreDatabaseName for the MSSQL database name instead of always reading MSSQL_DB_NAME" {
        # Mirrors the PostgreSQL $postgresDbName resolution so per-instance database names
        # passed via -DataStoreDatabaseName are not silently overridden by the env default
        # on the mssql branch.
        $script:configureSource | Should -Match '\$mssqlDbName\s*=\s*\r?\n\s*if \(\[string\]::IsNullOrWhiteSpace\(\$DataStoreDatabaseName\)\)'
        $script:configureSource | Should -Match 'Get-EnvValueOrDefault -EnvValues \$envValues -Name "MSSQL_DB_NAME" -DefaultValue "edfi_datamanagementservice"'
    }
}

Describe "start-published-dms.ps1 MSSQL data-store wiring (DMS-1255)" {
    BeforeAll {
        $script:startPublishedScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../start-published-dms.ps1"))
        $script:startPublishedSource = Get-Content -LiteralPath $script:startPublishedScript -Raw
    }

    It "builds the MSSQL data-store connection string via New-DataStoreConnectionString for the mssql engine" {
        $script:startPublishedSource | Should -Match '\$dataStoreConnectionString = ""\s*\r?\n\s*if \(\$DatabaseEngine -eq "mssql"\)'
        $script:startPublishedSource | Should -Match '(?s)\$dataStoreConnectionString = New-DataStoreConnectionString[^\r\n]*`\s*-DatabaseEngine "mssql"[^\r\n]*`\s*-DbHost "dms-mssql"'
    }

    It "forwards the resolved connection string to both data-store creation calls" {
        # Both the school-year path and the default single-data-store path must forward it so a
        # published MSSQL startup does not register PostgreSQL-shaped data stores pointing at a
        # dms-postgresql container that the mssql compose set never starts.
        ([regex]::Matches($script:startPublishedSource, '-ConnectionString \$dataStoreConnectionString')).Count |
            Should -BeGreaterOrEqual 2
    }

    It "defaults MSSQL_SA_PASSWORD to the documented dev password when the env file omits it" {
        # Matches the abcdefgh1! default baked into mssql.yml and the script's own
        # Wait-MssqlReady readiness polls.
        $script:startPublishedSource | Should -Match '(?s)\$mssqlPassword =\s*if \(\[string\]::IsNullOrWhiteSpace\(\$envValues\.MSSQL_SA_PASSWORD\)\)\s*\{\s*"abcdefgh1!"'
    }

    It "honors -DataStoreDatabaseName for the MSSQL database name instead of always reading MSSQL_DB_NAME" {
        # Mirrors the PostgreSQL $postgresDbName resolution so a caller-supplied database name
        # is not silently overridden by the env default on the mssql branch.
        $script:startPublishedSource | Should -Match '(?s)\$mssqlDbName =\s*if \(-not \[string\]::IsNullOrWhiteSpace\(\$DataStoreDatabaseName\)\)\s*\{\s*\$DataStoreDatabaseName'
        $script:startPublishedSource | Should -Match 'MSSQL_DB_NAME'
    }
}

Describe "Template workflow MSSQL content gates" {
    BeforeAll {
        $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:minimalWorkflow = Get-Content -LiteralPath (Join-Path $repoRoot ".github/workflows/build-minimal-template.yml") -Raw
        $script:populatedWorkflow = Get-Content -LiteralPath (Join-Path $repoRoot ".github/workflows/build-populated-template.yml") -Raw
    }

    It "does not pass the SQL Server password through sqlcmd or host docker arguments" {
        foreach ($workflow in @($script:minimalWorkflow, $script:populatedWorkflow)) {
            $workflow | Should -Not -Match 'sqlcmd[^\r\n]*\s-P\s'
            $workflow | Should -Not -Match 'docker exec -e "SQLCMDPASSWORD='
        }
    }

    It "uses the password from the running SQL Server container in both gates" {
        $containerCredentialPattern = [regex]::Escape('/bin/bash -c ''export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"; exec "$@"'' -- /opt/mssql-tools18/bin/sqlcmd')

        $script:minimalWorkflow | Should -Match $containerCredentialPattern
        $script:populatedWorkflow | Should -Match $containerCredentialPattern
        $script:minimalWorkflow | Should -Not -Match 'ENVIRONMENT_FILE: \$\{\{ inputs\.environment_file \}\}'
        $script:populatedWorkflow | Should -Not -Match 'ENVIRONMENT_FILE: \$\{\{ inputs\.environment_file \}\}'
    }
}
