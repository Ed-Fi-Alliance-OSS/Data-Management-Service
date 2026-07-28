# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# DMS-1270 Phase 1a: isolated unit coverage for the CMS database topology contract's new
# PowerShell functions (Resolve-CmsDatabaseTopologyEnvironmentFile, Confirm-CmsDatabaseTopologyAgreement,
# Get-DatabaseNameFromResolvedConnectionString, Get-EndpointFromResolvedConnectionString,
# Test-PostgresDuplicateDatabaseError, Test-MssqlDuplicateDatabaseError). No start script, wrapper,
# profile file, .yml file, or database-creation code path is wired to these functions yet - that
# wiring is Phase 1b/2/3, per reference/design/backend-redesign/fixes/DMS-1270.md.

param()

Describe "Resolve-CmsDatabaseTopologyEnvironmentFile" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-cms-topology-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:work -Force | Out-Null
    }

    AfterEach {
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Context "shared mode (switch omitted)" {
        It "returns the base file unchanged when DMS_CONFIG_DATABASE_NAME already aliases the datastore name" {
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            $result | Should -Be $basePath -Because "nothing needs to change: the alias already resolves to the effective shared-mode name"
        }

        It "materializes DMS_CONFIG_DATABASE_NAME into a derived file for an old .env that never defined it" {
            # This is the fix for the old-file gap: a pre-existing developer .env predating this
            # story's template update has no DMS_CONFIG_DATABASE_NAME line at all.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            $result | Should -Not -Be $basePath
            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_datamanagementservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "false"
        }
    }

    Context "separate mode" {
        It "sets DMS_CONFIG_DATABASE_NAME to the fixed edfi_configurationservice literal" {
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_configurationservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "true"
        }

        It "migrates a legacy connection string whose raw text is exactly the datastore-name token" {
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};'
        }

        It "migrates the MSSQL legacy token (not the PostgreSQL one) for an mssql base file" {
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "mssql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be 'Server=dms-mssql,1433;Database=${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
        }

        It "does NOT rewrite a genuinely custom reference that currently resolves to the same value as the datastore name" {
            # Round 7 Blocker 6: matching on the *resolved* value (rather than the exact raw
            # token) could silently rewrite a caller's own, unrelated ${CUSTOM_DATABASE}
            # reference merely because it happens to currently equal the datastore name.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'CUSTOM_DATABASE=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${CUSTOM_DATABASE};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${CUSTOM_DATABASE};' -Because "only the exact legacy token is a migration signature, never a resolved-value coincidence"
        }

        It "never wraps the migrated connection string in single quotes, which would freeze the new reference as a literal" {
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            (Get-Content -LiteralPath $result -Raw) | Should -Not -Match "DMS_CONFIG_DATABASE_CONNECTION_STRING='"
        }
    }

    Context "idempotency and mode transitions" {
        It "returns the same derived path on a repeated separate-mode call (no growth, no re-derivation)" {
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $first = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work
            $second = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $first -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $second | Should -Be $first
            $second | Should -Not -Match '\.topology\.topology'
        }

        It "reverts DMS_CONFIG_DATABASE_NAME to the shared alias when a later call omits the switch (shared -> separate -> shared)" {
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};'
            ) -join "`n") -NoNewline

            $separate = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work
            (ReadValuesFromEnvFile $separate)["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_configurationservice"

            $revertedToShared = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $separate -DatabaseEngine "postgresql" -DockerComposeRoot $script:work
            $revertedToShared | Should -Be $separate -Because "the same deterministic derived path is reused, not a new one"
            (ReadValuesFromEnvFile $revertedToShared)["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_datamanagementservice"
            (ReadValuesFromEnvFile $revertedToShared)["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "false"
        }
    }
}

Describe "Confirm-CmsDatabaseTopologyAgreement" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-cms-topology-confirm-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:work -Force | Out-Null
        # [System.Environment]::SetEnvironmentVariable($name, $null) does not truly unset the
        # variable on this runtime -- it leaves an empty string, which still trips ambient-override
        # detection. Remove-Item on the Env: drive is the only mechanism confirmed to truly clear it.
        Remove-Item Env:\DMS_CONFIG_DATABASE_NAME -ErrorAction SilentlyContinue
    }

    AfterEach {
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
        Remove-Item Env:\DMS_CONFIG_DATABASE_NAME -ErrorAction SilentlyContinue
    }

    Context "shared mode" {
        It "accepts a connection string that agrees with the resolved datastore name" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw
        }

        It "rejects a connection string that disagrees with the resolved datastore name" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=some_other_db;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*some_other_db*"
        }

        It "moves the expected name when an ambient POSTGRES_DB_NAME override is present, matching Compose's own precedence" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=file_named_db',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=ambient_named_db;'
            ) -join "`n") -NoNewline

            try {
                [System.Environment]::SetEnvironmentVariable("POSTGRES_DB_NAME", "ambient_named_db")
                { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw -Because "the ambient override moves the expected value the same way Compose would resolve it"
            }
            finally {
                Remove-Item Env:\POSTGRES_DB_NAME -ErrorAction SilentlyContinue
            }
        }

        It "constructs a concrete default and validates against it when the connection-string key is entirely absent" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw
        }
    }

    Context "separate mode" {
        It "accepts a connection string targeting edfi_configurationservice when the topology marker says separate" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_configurationservice;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw
        }

        It "rejects a connection string still targeting the shared datastore name when the marker says separate" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_datamanagementservice;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*edfi_configurationservice*"
        }
    }

    Context "ambient DMS_CONFIG_DATABASE_NAME conflict (never a resolved read-back)" {
        It "accepts an ambient DMS_CONFIG_DATABASE_NAME that agrees with the effective (separate-mode) contract" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_configurationservice;'
            ) -join "`n") -NoNewline

            try {
                [System.Environment]::SetEnvironmentVariable("DMS_CONFIG_DATABASE_NAME", "edfi_configurationservice")
                { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw
            }
            finally {
                Remove-Item Env:\DMS_CONFIG_DATABASE_NAME -ErrorAction SilentlyContinue
            }
        }

        It "rejects an ambient DMS_CONFIG_DATABASE_NAME that disagrees with the effective contract" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_configurationservice;'
            ) -join "`n") -NoNewline

            try {
                [System.Environment]::SetEnvironmentVariable("DMS_CONFIG_DATABASE_NAME", "some_conflicting_value")
                { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*some_conflicting_value*"
            }
            finally {
                Remove-Item Env:\DMS_CONFIG_DATABASE_NAME -ErrorAction SilentlyContinue
            }
        }
    }

    Context "MSSQL-specific comparison rules" {
        It "accepts a case-different database name (MSSQL is ordinal-case-insensitive)" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=EDFI_DATAMANAGEMENTSERVICE;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } | Should -Not -Throw
        }

        It "splits the MSSQL Server=host,port compound and validates both parts" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,9999;Database=edfi_datamanagementservice;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } | Should -Throw "*9999*"
        }
    }

    Context "PostgreSQL-specific comparison rules" {
        It "rejects a case-different database name (PostgreSQL is ordinal-case-sensitive)" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=EDFI_DATAMANAGEMENTSERVICE;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*EDFI_DATAMANAGEMENTSERVICE*"
        }
    }

    Context "host and port edge cases" {
        It "fails closed when the connection string has no recognized host key at all" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Database=edfi_datamanagementservice;Username=postgres;Password=abcdefgh1!;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*host*"
        }

        It "defaults an omitted port to the engine's standard internal port when the host key is present" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=edfi_datamanagementservice;username=postgres;password=abcdefgh1!;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw
        }

        It "rejects a host that does not match the expected container hostname" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=some-other-host;port=5432;database=edfi_datamanagementservice;username=postgres;password=abcdefgh1!;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*some-other-host*"
        }
    }

    Context "multiple agreeing aliases (must not be rejected as ambiguous)" {
        It "accepts a connection string carrying both Database and Initial Catalog when both agree" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=edfi_datamanagementservice;Initial Catalog=edfi_datamanagementservice;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } | Should -Not -Throw
        }

        It "rejects a connection string carrying two disagreeing database-name aliases" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=edfi_datamanagementservice;Initial Catalog=a_different_database;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } | Should -Throw "*a_different_database*"
        }
    }
}

Describe "Get-DatabaseNameFromResolvedConnectionString / Get-EndpointFromResolvedConnectionString" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force
    }

    It "returns an empty array for a blank connection string" {
        @(Get-DatabaseNameFromResolvedConnectionString -ConnectionString "").Count | Should -Be 0
        @(Get-EndpointFromResolvedConnectionString -ConnectionString "").Count | Should -Be 0
    }

    It "returns every present database-name candidate without picking a single winner" {
        $names = @(Get-DatabaseNameFromResolvedConnectionString -ConnectionString "Database=A;Initial Catalog=B;")
        $names.Count | Should -Be 2
        $names | Should -Contain "A"
        $names | Should -Contain "B"
    }

    It "does not re-resolve a ${...}-shaped literal already present in an already-resolved string" {
        # Simulates the opaque-ambient case: the caller already resolved the whole connection
        # string (e.g. via Get-ComposeResolvedEnvValue), so a literal, un-interpolated ${...}
        # token that survives into the extracted sub-value must be returned verbatim, not
        # resolved a second time.
        $names = @(Get-DatabaseNameFromResolvedConnectionString -ConnectionString 'Database=${SOME_LITERAL_TEXT};')
        $names | Should -Contain '${SOME_LITERAL_TEXT}'
    }

    It "splits a host,port compound into separate Host and Port fields" {
        $endpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString "Server=dms-mssql,1433;Database=x;")
        $endpoints.Count | Should -Be 1
        $endpoints[0].Host | Should -Be "dms-mssql"
        $endpoints[0].Port | Should -Be "1433"
    }

    It "returns a null Port when no comma-separated port segment is present" {
        $endpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString "host=dms-postgresql;database=x;")
        $endpoints[0].Host | Should -Be "dms-postgresql"
        $endpoints[0].Port | Should -BeNullOrEmpty
    }
}

Describe "Test-PostgresDuplicateDatabaseError" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force
    }

    # Fixture text captured empirically (DMS-1270 Phase 1a spike) against a real PostgreSQL 16
    # instance running `psql -v VERBOSITY=sqlstate`: a direct duplicate CREATE DATABASE reported
    # "ERROR:  42P04"; a genuine concurrent race between two \gexec-driven sessions targeting a
    # not-yet-existing database reported "psql:<stdin>:2: ERROR:  23505" on the losing side.
    It "recognizes the empirically-captured 42P04 direct-duplicate format" {
        Test-PostgresDuplicateDatabaseError -CapturedOutput "ERROR:  42P04" | Should -BeTrue
    }

    It "recognizes the empirically-captured 23505 concurrent-race format" {
        Test-PostgresDuplicateDatabaseError -CapturedOutput "psql:<stdin>:2: ERROR:  23505" | Should -BeTrue
    }

    It "does not swallow a different SQLSTATE" {
        Test-PostgresDuplicateDatabaseError -CapturedOutput "ERROR:  42501" | Should -BeFalse
    }

    It "does not swallow malformed or empty output" {
        Test-PostgresDuplicateDatabaseError -CapturedOutput "" | Should -BeFalse
        Test-PostgresDuplicateDatabaseError -CapturedOutput "some unrelated text with no error code" | Should -BeFalse
    }
}

Describe "Test-MssqlDuplicateDatabaseError" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force
    }

    It "recognizes SQL Server error 1801 (database already exists)" {
        Test-MssqlDuplicateDatabaseError -CapturedOutput "Msg 1801, Level 16, State 3, Server x, Line 1" | Should -BeTrue
    }

    It "does not swallow a different error number" {
        Test-MssqlDuplicateDatabaseError -CapturedOutput "Msg 4060, Level 11, State 1" | Should -BeFalse
    }

    It "does not swallow malformed or empty output" {
        Test-MssqlDuplicateDatabaseError -CapturedOutput "" | Should -BeFalse
    }
}

Describe "Exit-code-independent-of-error-text (Phase 1a design invariant)" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force
    }

    It "PostgreSQL detection depends on the SQLSTATE token, not a particular human-readable phrase" {
        # Same SQLSTATE, two different hypothetical locale/verbosity message bodies -- both must
        # be recognized, proving the match is on the code, not the surrounding text.
        Test-PostgresDuplicateDatabaseError -CapturedOutput 'ERROR:  42P04: la base de datos "x" ya existe' | Should -BeTrue
        Test-PostgresDuplicateDatabaseError -CapturedOutput 'ERROR:  42P04: database "x" already exists' | Should -BeTrue
    }
}

Describe "Compose-rendering oracle (empirical parity with local-config.yml / published-config.yml)" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force

        # Captured empirically (DMS-1270 Phase 1a spike) by rendering the genuine, checked-in
        # nested Compose fallback with a real Docker Compose invocation:
        #   docker compose -f postgresql.yml -f local-config.yml --env-file <fixture> config
        # where the fixture env file defined only POSTGRES_DB_NAME=edfi_datamanagementservice and
        # POSTGRES_PASSWORD=abcdefgh1! (DMS_CONFIG_DATABASE_CONNECTION_STRING left entirely absent,
        # matching an old .env predating that key). Docker Compose v5.1.3 rendered
        # DatabaseSettings__DatabaseConnection as the string below, verbatim. This is a frozen
        # empirical fixture, not a live Docker dependency of this suite -- re-capture it manually
        # if the checked-in nested-fallback syntax in those two files ever changes.
        $script:composeRenderedDefault = 'host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;'
    }

    It "the checked-in local-config.yml / published-config.yml nested fallback still matches the captured oracle text" {
        # Guards against silent drift: if either file's nested default is ever edited without
        # re-running the live oracle capture, this fails loudly instead of the fixture going stale.
        $localConfig = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "local-config.yml") -Raw
        $publishedConfig = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "published-config.yml") -Raw
        $expectedNestedSyntax = 'DMS_CONFIG_DATABASE_CONNECTION_STRING:-host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'

        $localConfig | Should -Match ([regex]::Escape($expectedNestedSyntax))
        $publishedConfig | Should -Match ([regex]::Escape($expectedNestedSyntax))
    }

    It "Confirm-CmsDatabaseTopologyAgreement's absent-key default agrees with the real Compose-rendered value" {
        $work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-cms-topology-oracle-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        try {
            $path = Join-Path $work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!'
            ) -join "`n") -NoNewline

            # DMS_CONFIG_DATABASE_CONNECTION_STRING is absent, so the production function must
            # construct the same default Compose renders and validate cleanly against it.
            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw
        }
        finally {
            Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "the extractor functions parse the genuine Compose-rendered oracle string identically to a hand-written fixture" {
        $names = @(Get-DatabaseNameFromResolvedConnectionString -ConnectionString $script:composeRenderedDefault)
        $names | Should -Be @("edfi_datamanagementservice")

        # PostgreSQL's own connection-string shape carries port as a standalone "port=" key, not a
        # host,port compound, so the endpoint extractor (which only splits a comma inside the host
        # alias's own value) reports no Port here -- Confirm-CmsDatabaseTopologyAgreement's caller
        # defaults a null Port to the engine's expected port, which this same fixture exercises above.
        $endpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString $script:composeRenderedDefault)
        $endpoints[0].Host | Should -Be "dms-postgresql"
        $endpoints[0].Port | Should -BeNullOrEmpty
    }
}
