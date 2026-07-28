# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# DMS-1270 Phase 1a: isolated unit coverage for the CMS database topology contract's new
# PowerShell functions (Resolve-CmsDatabaseTopologyEnvironmentFile, Confirm-CmsDatabaseTopologyAgreement,
# ConvertTo-DotenvSafeEnvValue, Get-DatabaseNameFromResolvedConnectionString,
# Get-EndpointFromResolvedConnectionString, Get-CmsDatabaseTopologyDefaultConnectionString,
# Test-PostgresDuplicateDatabaseError, Test-MssqlDuplicateDatabaseError).
#
# DMS-1270 Phase 1b: wiring-level coverage (below, "Phase 1b wiring" Describe blocks) for the
# MSSQL-only topology-write sequence now wired into start-local-dms.ps1, start-published-dms.ps1,
# and bootstrap-wrapper.psm1's own pre-resolution chain. PostgreSQL remains untouched (a temporary
# guard fails fast) until Phase 2.

param()

Describe "Resolve-CmsDatabaseTopologyEnvironmentFile" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force

        # Round 8 finding: after the ambient-aware fix below, this function reads ambient
        # POSTGRES_DB_NAME/MSSQL_DB_NAME too, so a leftover value from the developer's own shell (or
        # a prior test) can now change these tests' outcome. Snapshot/clear/restore every ambient
        # variable either function under test consumes, not just the one a given test happens to set.
        $script:ambientKeys = @(
            "POSTGRES_DB_NAME", "MSSQL_DB_NAME", "POSTGRES_PASSWORD", "MSSQL_SA_PASSWORD",
            "DMS_CONFIG_DATABASE_NAME", "DMS_CONFIG_DATABASE_CONNECTION_STRING", "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"
        )
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-cms-topology-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:work -Force | Out-Null

        $script:ambientSnapshot = @{}
        foreach ($key in $script:ambientKeys) {
            $script:ambientSnapshot[$key] = [System.Environment]::GetEnvironmentVariable($key)
            Remove-Item "Env:\$key" -ErrorAction SilentlyContinue
        }
    }

    AfterEach {
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
        foreach ($key in $script:ambientKeys) {
            if ($null -eq $script:ambientSnapshot[$key]) {
                Remove-Item "Env:\$key" -ErrorAction SilentlyContinue
            }
            else {
                [System.Environment]::SetEnvironmentVariable($key, $script:ambientSnapshot[$key])
            }
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

        It "reflects an ambient POSTGRES_DB_NAME override in the materialized DMS_CONFIG_DATABASE_NAME" {
            # Round 8 Blocker 1: the write side must resolve the datastore name the same
            # Compose-precedence-aware way Confirm-CmsDatabaseTopologyAgreement does, since an
            # ambient override genuinely moves the running database container.
            [System.Environment]::SetEnvironmentVariable("POSTGRES_DB_NAME", "ambient_override_db")
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=file_named_db',
                'POSTGRES_PASSWORD=abcdefgh1!'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            (ReadValuesFromEnvFile $result)["DMS_CONFIG_DATABASE_NAME"] | Should -Be "ambient_override_db"
        }

        It "recognizes an already-aliased file as unchanged even while an ambient override is active" {
            # The idempotency comparison must resolve the CURRENT DMS_CONFIG_DATABASE_NAME the same
            # ambient-aware way, or an active override would make an already-correct alias look
            # "changed" on every call, needlessly freezing a live alias into a derived file.
            [System.Environment]::SetEnvironmentVariable("POSTGRES_DB_NAME", "ambient_override_db")
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=file_named_db',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            $result | Should -Be $basePath -Because "Compose would resolve the existing alias to the same ambient value, so no rewrite is needed"
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

        It "guarantees DMS_CONFIG_DATABASE_NAME precedes DMS_CONFIG_DATABASE_CONNECTION_STRING in the derived file" {
            # Empirically confirmed against a real Docker Compose invocation (DMS-1270 Phase 1a Round 9
            # spike): --env-file interpolation is order-dependent, like shell `source` semantics - a
            # ${VAR} reference resolves only against variables defined EARLIER in the same file. A
            # forward reference (the referenced key's own definition appears later) resolves to empty.
            # Write-DerivedEnvFile appends a genuinely new key after whatever the base file already
            # contains, so introducing DMS_CONFIG_DATABASE_NAME into a base file that already defines
            # DMS_CONFIG_DATABASE_CONNECTION_STRING - exactly today's checked-in templates' shape -
            # would otherwise leave the migrated ${DMS_CONFIG_DATABASE_NAME} reference resolving to
            # empty at real Compose render time. See Move-EnvFileKeyBeforeAnotherKey.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $lines = Get-Content -LiteralPath $result
            $nameIndex = -1
            $connectionStringIndex = -1
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($nameIndex -lt 0 -and $lines[$i] -match '^DMS_CONFIG_DATABASE_NAME=') { $nameIndex = $i }
                if ($connectionStringIndex -lt 0 -and $lines[$i] -match '^DMS_CONFIG_DATABASE_CONNECTION_STRING=') { $connectionStringIndex = $i }
            }

            $nameIndex | Should -BeGreaterThan -1
            $connectionStringIndex | Should -BeGreaterThan -1
            $nameIndex | Should -BeLessThan $connectionStringIndex
        }

        It "migrates the token inside an outer double-quoted dotenv connection string, preserving the outer quotes" {
            # Round 10 Blocker 1: Get-EnvValue returns the raw dotenv value verbatim, including any
            # outer dotenv-level quote wrapper. Without stripping it first, the scanner mistook the
            # wrapper's opening quote for an ADO.NET value-quote, swallowing every real ';' inside as
            # "quoted" and finding no segments at all - the token was left unmigrated.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING="host=dms-postgresql;database=${POSTGRES_DB_NAME};"'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be '"host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};"' -Because "only the token changes; the outer double quotes are preserved exactly as authored"
        }

        It "migrates the token inside an outer single-quoted dotenv connection string, preserving the outer quotes" {
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                "DMS_CONFIG_DATABASE_CONNECTION_STRING='host=dms-postgresql;database=`${POSTGRES_DB_NAME};'"
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be "'host=dms-postgresql;database=`${DMS_CONFIG_DATABASE_NAME};'"
        }

        It "migrates the token inside an outer double-quoted dotenv value carrying a trailing inline comment, preserving both" {
            # Round 11 Blocker 2: Get-EnvValue returns the raw dotenv value verbatim, including a
            # trailing inline comment after the closing quote. The prior "last character equals the
            # opening quote" check mistook the comment's own trailing character for proof the value
            # was not quoted at all, so the wrapper went undetected and the token stayed unmigrated.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING="host=dms-postgresql;database=${POSTGRES_DB_NAME};" # keep'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be '"host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};" # keep' -Because "the outer quotes and the trailing comment are both preserved byte-for-byte; only the inner token changes"

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $result -DatabaseEngine "postgresql" } | Should -Not -Throw -Because "the migrated file must validate cleanly end to end through the validator"
        }

        It "migrates the token when the connection string contains regex replacement-directive sequences elsewhere (`$&, `$0)" {
            # Round 11 Blocker 1: Write-DerivedEnvFile's underlying Regex.Replace call previously
            # treated the replacement string as a REPLACEMENT PATTERN, so a literal '$&' or '$0'
            # anywhere in the caller-authored value (a password, for instance) was corrupted -
            # duplicating the entire matched line into the middle of the value - rather than written
            # verbatim.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;password=p$&q$0r;database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be 'host=dms-postgresql;password=p$&q$0r;database=${DMS_CONFIG_DATABASE_NAME};' -Because "the password must survive verbatim; only the database segment's token changes"
        }

        It "does NOT rewrite the legacy token when it appears outside the database segment" {
            # Round 8 Blocker 2: a blind Contains/Replace across the whole connection string could
            # rewrite the token inside an unrelated segment (here, the password) that merely happens
            # to carry the identical literal text. Only the database-segment's own value is a
            # migration signature.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_DB_NAME};database=custom;'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_DB_NAME};database=custom;' -Because "the token is not in a recognized database-name key's value, so it must be left untouched"
        }

        It "does NOT rewrite the legacy token when it appears inside a quoted, unrelated segment" {
            # Round 9 Blocker 1: a plain regex lookbehind/lookahead has no concept of quoting, so a
            # ';' inside a quoted password value was mistaken for a real segment boundary, letting the
            # token text embedded in that unrelated quoted value be matched and rewritten.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Password="keep;Database=${POSTGRES_DB_NAME};inside-password";Database=custom;'
            ) -join "`n") -NoNewline

            $result = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be 'Password="keep;Database=${POSTGRES_DB_NAME};inside-password";Database=custom;' -Because "the token is inside a quoted password value, not a real Database= segment, and the real Database=custom segment does not match the legacy token either"
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

        It "preserves a datastore name containing a literal '$' intact across a shared -> separate -> shared transition" {
            # Round 10 Blocker 2: an unquoted written value like tenant$db, once re-read (by Compose or
            # by Get-ComposeResolvedEnvValue), has $db misinterpreted as a reference to an unset "db"
            # variable and silently collapses to just "tenant". ConvertTo-DotenvSafeEnvValue must quote
            # any concrete value containing '$'.
            $basePath = Join-Path $script:work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                "POSTGRES_DB_NAME='tenant`$db'",
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};'
            ) -join "`n") -NoNewline

            $separate = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $script:work
            $revertedToShared = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $separate -DatabaseEngine "postgresql" -DockerComposeRoot $script:work

            $revertedValues = ReadValuesFromEnvFile $revertedToShared
            $revertedValues["DMS_CONFIG_DATABASE_NAME"] | Should -Be "'tenant`$db'" -Because "the written value itself must be quoted"
            (Get-ComposeResolvedEnvValue -EnvironmentValues $revertedValues -Name "DMS_CONFIG_DATABASE_NAME") | Should -Be 'tenant$db' -Because "re-reading it must not lose the literal `$db suffix to interpolation"
        }
    }
}

Describe "Get-DotenvClosingQuoteIndex" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    It "returns -1 for a value with no outer quote wrapper" {
        Get-DotenvClosingQuoteIndex -RawValue "host=dms-postgresql;database=x;" | Should -Be -1
    }

    It "finds the closing quote for a simple double-quoted value" {
        $value = '"host=dms-postgresql;database=x;"'
        Get-DotenvClosingQuoteIndex -RawValue $value | Should -Be ($value.Length - 1)
    }

    It "finds the closing quote when a trailing inline comment follows it" {
        # Round 11 Blocker 2.
        $value = '"host=dms-postgresql;database=x;" # keep'
        $expectedIndex = '"host=dms-postgresql;database=x;"'.Length - 1
        Get-DotenvClosingQuoteIndex -RawValue $value | Should -Be $expectedIndex
    }

    It "returns -1 when trailing content after a candidate closing quote is neither empty nor a comment" {
        Get-DotenvClosingQuoteIndex -RawValue '"host=dms-postgresql" ;database=x;' | Should -Be -1
    }

    It "does not treat a backslash-escaped quote as the closing quote" {
        $value = '"host=dms-postgresql;pwd=\"escaped\";database=x;"'
        Get-DotenvClosingQuoteIndex -RawValue $value | Should -Be ($value.Length - 1)
    }
}

Describe "ConvertTo-DotenvSafeEnvValue" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    It "leaves an ordinary alphanumeric value bare" {
        ConvertTo-DotenvSafeEnvValue -Value "edfi_datamanagementservice" | Should -Be "edfi_datamanagementservice"
    }

    It "leaves the bare marker values 'true'/'false' unquoted" {
        ConvertTo-DotenvSafeEnvValue -Value "true" | Should -Be "true"
        ConvertTo-DotenvSafeEnvValue -Value "false" | Should -Be "false"
    }

    It "single-quotes a value containing a space" {
        ConvertTo-DotenvSafeEnvValue -Value "has space" | Should -Be "'has space'"
    }

    It "single-quotes a value containing a '#'" {
        ConvertTo-DotenvSafeEnvValue -Value "value#tag" | Should -Be "'value#tag'"
    }

    It "single-quotes a value containing a '`$'" {
        # Round 10 Blocker 2: Resolve-ComposeEnvReference matches a bare `$NAME (no braces required),
        # so an unquoted value like tenant`$db would have `$db misread as a reference and collapse to
        # "tenant" once re-read. Single-quoting suppresses interpolation entirely.
        ConvertTo-DotenvSafeEnvValue -Value 'tenant$db' | Should -Be "'tenant`$db'"
    }

    It "single-quotes a value opening with a quote character" {
        ConvertTo-DotenvSafeEnvValue -Value "'already-quoted" | Should -Be "'\'already-quoted'"
    }

    It "backslash-escapes an embedded apostrophe, never doubling it" {
        ConvertTo-DotenvSafeEnvValue -Value "value with a ' apostrophe" | Should -Be "'value with a \' apostrophe'"
    }
}

Describe "Confirm-CmsDatabaseTopologyAgreement" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force

        # Round 8 Blocker 8: snapshot/clear/restore every ambient variable either function consumes,
        # not just DMS_CONFIG_DATABASE_NAME - a leftover shell value for a datastore name, password,
        # or the whole connection string can otherwise silently alter this suite's outcome.
        $script:ambientKeys = @(
            "POSTGRES_DB_NAME", "MSSQL_DB_NAME", "POSTGRES_PASSWORD", "MSSQL_SA_PASSWORD",
            "DMS_CONFIG_DATABASE_NAME", "DMS_CONFIG_DATABASE_CONNECTION_STRING", "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"
        )
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-cms-topology-confirm-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:work -Force | Out-Null

        $script:ambientSnapshot = @{}
        foreach ($key in $script:ambientKeys) {
            $script:ambientSnapshot[$key] = [System.Environment]::GetEnvironmentVariable($key)
            Remove-Item "Env:\$key" -ErrorAction SilentlyContinue
        }
    }

    AfterEach {
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
        foreach ($key in $script:ambientKeys) {
            if ($null -eq $script:ambientSnapshot[$key]) {
                Remove-Item "Env:\$key" -ErrorAction SilentlyContinue
            }
            else {
                [System.Environment]::SetEnvironmentVariable($key, $script:ambientSnapshot[$key])
            }
        }
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

        It "rejects a connection string whose explicit port disagrees, even though the host matches" {
            # Round 8 Blocker 4: PostgreSQL's own connection-string shape carries port as a standalone
            # "port=" key. Before the fix, the endpoint extractor never looked at that key at all, so
            # a wrong explicit port was silently defaulted to the expected port and accepted.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=9999;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*9999*"
        }

        It "moves the expected name when an ambient POSTGRES_DB_NAME override is present, matching Compose's own precedence" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=file_named_db',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=ambient_named_db;'
            ) -join "`n") -NoNewline

            [System.Environment]::SetEnvironmentVariable("POSTGRES_DB_NAME", "ambient_named_db")
            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw -Because "the ambient override moves the expected value the same way Compose would resolve it"
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

            [System.Environment]::SetEnvironmentVariable("DMS_CONFIG_DATABASE_NAME", "edfi_configurationservice")
            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Not -Throw
        }

        It "rejects an ambient DMS_CONFIG_DATABASE_NAME that disagrees with the effective contract" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_configurationservice;'
            ) -join "`n") -NoNewline

            [System.Environment]::SetEnvironmentVariable("DMS_CONFIG_DATABASE_NAME", "some_conflicting_value")
            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*some_conflicting_value*"
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

        It "does not honor a standalone Port= key for MSSQL (SqlClient does not support that keyword)" {
            # Round 9 Blocker 2: honoring a standalone Port= for MSSQL would accept a keyword the real
            # SqlClient provider does not recognize, defaulting to the expected port instead of failing.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql;Port=9999;Database=edfi_datamanagementservice;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } | Should -Not -Throw -Because "the standalone Port= key is not a real MSSQL keyword and must be ignored, defaulting to the expected port 1433"
        }

        It "fails clearly, without constructing a default, when the connection string is entirely absent" {
            # Round 8 Blocker 5 / spec Phase 1 rule: neither .yml file has an engine-aware inline
            # fallback for MSSQL yet, so guessing a default here could accept a connection Compose
            # itself would never render. Must fail clearly instead.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } | Should -Throw "*required for MSSQL*"
        }

        It "fails clearly, without constructing a default, when the connection string is ambient-blank" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            [System.Environment]::SetEnvironmentVariable("DMS_CONFIG_DATABASE_CONNECTION_STRING", "")

            # A present-but-blank ambient value is only representable on Windows: on Unix, .NET
            # deletes the variable when it is set to an empty string, so the scenario under test
            # cannot be established and the file value (a valid connection string) would be used
            # instead. Assert the precondition rather than letting the platform silently decide
            # whether this test means anything.
            if ($null -eq [System.Environment]::GetEnvironmentVariable("DMS_CONFIG_DATABASE_CONNECTION_STRING")) {
                Set-ItResult -Skipped -Because "this platform cannot represent a present-but-blank ambient environment variable"
                return
            }

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } | Should -Throw "*required for MSSQL*"
        }

        It "fails closed for a PostgreSQL-only host alias (Host=) that MSSQL does not recognize" {
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Host=dms-mssql;Database=edfi_datamanagementservice;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "mssql" } | Should -Throw "*host*"
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

        It "fails closed for an MSSQL-only host alias (Address=) that PostgreSQL does not recognize" {
            # Round 8 Blocker 4: host-key recognition must be engine-specific, not a single union
            # applied to both engines.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Address=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_datamanagementservice;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*host*"
        }

        It "rejects a comma-bearing PostgreSQL Host= value as a literal (malformed) host, rather than splitting it and hiding an explicit Port=" {
            # Round 9 Blocker 2: Npgsql has no Server=host,port compound - a comma in a PostgreSQL
            # Host= value is not a port separator. Before the fix, splitting it anyway extracted
            # "dms-postgresql" as Host (matching the expected host) and the comma-compound's second
            # half as Port, silently hiding the disagreeing explicit standalone Port=9999 key behind a
            # port that was never really specified that way. After the fix, the comma is not split, so
            # the whole value "dms-postgresql,5432" is correctly rejected as not matching the expected
            # host "dms-postgresql" - a comma-bearing value is simply not a valid PostgreSQL host.
            $path = Join-Path $script:work ".env"
            Set-Content -LiteralPath $path -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Host=dms-postgresql,5432;Port=9999;username=postgres;password=${POSTGRES_PASSWORD};database=edfi_datamanagementservice;'
            ) -join "`n") -NoNewline

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $path -DatabaseEngine "postgresql" } | Should -Throw "*dms-postgresql,5432*"
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
        @(Get-EndpointFromResolvedConnectionString -ConnectionString "" -DatabaseEngine "postgresql").Count | Should -Be 0
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

    It "splits an MSSQL host,port compound into separate Host and Port fields" {
        $endpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString "Server=dms-mssql,1433;Database=x;" -DatabaseEngine "mssql")
        $endpoints.Count | Should -Be 1
        $endpoints[0].Host | Should -Be "dms-mssql"
        $endpoints[0].Port | Should -Be "1433"
    }

    It "extracts a PostgreSQL standalone port key when the host value carries no comma compound" {
        # Round 8 Blocker 4: PostgreSQL's own shape (host=...;port=...;) is not a host,port compound
        # - the port must still be recognized from its own standalone key.
        $endpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString "host=dms-postgresql;port=9999;database=x;" -DatabaseEngine "postgresql")
        $endpoints[0].Host | Should -Be "dms-postgresql"
        $endpoints[0].Port | Should -Be "9999"
    }

    It "returns a null Port when neither a comma compound nor a standalone port key is present" {
        $endpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString "host=dms-postgresql;database=x;" -DatabaseEngine "postgresql")
        $endpoints[0].Host | Should -Be "dms-postgresql"
        $endpoints[0].Port | Should -BeNullOrEmpty
    }

    It "does not split a comma inside a PostgreSQL Host= value (Npgsql has no host,port compound)" {
        # Round 9 Blocker 2: splitting the comma hides an explicit standalone Port= key behind
        # whatever port a coincidental comma in the host value produced.
        $endpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString "Host=dms-postgresql,5432;Port=9999;Database=x;" -DatabaseEngine "postgresql")
        $endpoints[0].Host | Should -Be "dms-postgresql,5432"
        $endpoints[0].Port | Should -Be "9999"
    }

    It "does not honor a standalone Port= key for MSSQL (SqlClient does not support that keyword)" {
        $endpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString "Server=dms-mssql;Port=1433;Database=x;" -DatabaseEngine "mssql")
        $endpoints[0].Host | Should -Be "dms-mssql"
        $endpoints[0].Port | Should -BeNullOrEmpty
    }

    It "does not recognize an MSSQL-only alias (Address=) for PostgreSQL" {
        @(Get-EndpointFromResolvedConnectionString -ConnectionString "Address=some-host;Database=x;" -DatabaseEngine "postgresql").Count | Should -Be 0
    }

    It "does not recognize a PostgreSQL-only alias (Host=) for MSSQL" {
        @(Get-EndpointFromResolvedConnectionString -ConnectionString "Host=some-host;Database=x;" -DatabaseEngine "mssql").Count | Should -Be 0
    }

    It "recognizes Server= for both engines" {
        @(Get-EndpointFromResolvedConnectionString -ConnectionString "Server=some-host;Database=x;" -DatabaseEngine "postgresql").Count | Should -Be 1
        @(Get-EndpointFromResolvedConnectionString -ConnectionString "Server=some-host;Database=x;" -DatabaseEngine "mssql").Count | Should -Be 1
    }
}

Describe "Get-CmsDatabaseTopologyDefaultConnectionString" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "database-safety.psm1") -Force
    }

    It "constructs the exact shape local-config.yml / published-config.yml's nested fallback renders" {
        $result = Get-CmsDatabaseTopologyDefaultConnectionString -ExpectedHost "dms-postgresql" -ExpectedPort "5432" -ExpectedDatabaseName "edfi_datamanagementservice" -PostgresPassword "abcdefgh1!"
        $result | Should -Be 'host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;'
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

    It "does not swallow a bare '1801' that is not in the structured error-number position" {
        # Round 8 Blocker 7: the prior regex ('\b1801\b') matched a standalone "1801" anywhere in the
        # output - a row count, a line number, or any other unrelated number could be misclassified
        # as the benign race. Only the anchored "Msg 1801," form counts.
        Test-MssqlDuplicateDatabaseError -CapturedOutput "Rows affected: 1801" | Should -BeFalse
        Test-MssqlDuplicateDatabaseError -CapturedOutput "(1801 rows affected)" | Should -BeFalse
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

        # Captured empirically (DMS-1270 Phase 1a Round 9 spike) by running the production
        # Resolve-CmsDatabaseTopologyEnvironmentFile function (separate mode, against a base file
        # shaped exactly like today's checked-in templates - connection string present,
        # DMS_CONFIG_DATABASE_NAME absent) and feeding the ACTUAL resulting derived file to a real
        # Docker Compose invocation:
        #   docker compose -f postgresql.yml -f local-config.yml --env-file <derived file> config
        # This uncovered a genuine bug, since fixed (Move-EnvFileKeyBeforeAnotherKey): Docker
        # Compose's --env-file interpolation is order-dependent, like shell `source` semantics - a
        # forward reference (DMS_CONFIG_DATABASE_NAME's line appearing after the connection string
        # that references it) rendered database= as EMPTY, not the intended database name. After the
        # fix, Docker Compose v5.1.3 rendered DatabaseSettings__DatabaseConnection as the string below.
        $script:composeRenderedMigratedSeparateMode = 'host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_configurationservice;'
    }

    BeforeEach {
        # Round 9 Blocker 4: this block calls Confirm-CmsDatabaseTopologyAgreement (and now
        # Resolve-CmsDatabaseTopologyEnvironmentFile too) without clearing ambient state, so a
        # leftover shell value for a datastore name, password, or the connection string itself could
        # silently change what these tests exercise - the same hermeticity already applied to the
        # other two Describe blocks.
        $script:ambientKeys = @(
            "POSTGRES_DB_NAME", "MSSQL_DB_NAME", "POSTGRES_PASSWORD", "MSSQL_SA_PASSWORD",
            "DMS_CONFIG_DATABASE_NAME", "DMS_CONFIG_DATABASE_CONNECTION_STRING", "DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"
        )
        $script:ambientSnapshot = @{}
        foreach ($key in $script:ambientKeys) {
            $script:ambientSnapshot[$key] = [System.Environment]::GetEnvironmentVariable($key)
            Remove-Item "Env:\$key" -ErrorAction SilentlyContinue
        }
    }

    AfterEach {
        foreach ($key in $script:ambientKeys) {
            if ($null -eq $script:ambientSnapshot[$key]) {
                Remove-Item "Env:\$key" -ErrorAction SilentlyContinue
            }
            else {
                [System.Environment]::SetEnvironmentVariable($key, $script:ambientSnapshot[$key])
            }
        }
    }

    It "the checked-in local-config.yml / published-config.yml nested fallback still matches the captured oracle text" {
        # Guards against silent drift: if either file's nested default is ever edited without
        # re-running the live oracle capture, this fails loudly instead of the fixture going stale.
        # The database segment is itself a nested default so the fallback honors the topology seam:
        # DMS_CONFIG_DATABASE_NAME when set, POSTGRES_DB_NAME otherwise. Re-captured live against
        # Docker Compose for all three shapes (explicit key, seam set, seam absent) when this form
        # was introduced; both frozen oracle strings below matched the render unchanged.
        $localConfig = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "local-config.yml") -Raw
        $publishedConfig = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "published-config.yml") -Raw
        $expectedNestedSyntax = 'DMS_CONFIG_DATABASE_CONNECTION_STRING:-host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}};'

        $localConfig | Should -Match ([regex]::Escape($expectedNestedSyntax))
        $publishedConfig | Should -Match ([regex]::Escape($expectedNestedSyntax))
    }

    It "the nested fallback resolves the seam for separate mode and the datastore name otherwise" {
        # Both captured oracle strings come from the same nested fallback, differing only in whether
        # DMS_CONFIG_DATABASE_NAME was set - which is precisely the behavior the nesting exists for.
        # Asserting the resolver agrees with both renders pins the seam's two outcomes together.
        $sharedValues = @{ POSTGRES_DB_NAME = 'edfi_datamanagementservice'; POSTGRES_PASSWORD = 'abcdefgh1!' }
        $separateValues = @{ POSTGRES_DB_NAME = 'edfi_datamanagementservice'; POSTGRES_PASSWORD = 'abcdefgh1!'; DMS_CONFIG_DATABASE_NAME = 'edfi_configurationservice' }

        $sharedName = Get-ComposeResolvedEnvValue -EnvironmentValues $sharedValues -Name "DMS_CONFIG_DATABASE_NAME" -DefaultValue $sharedValues.POSTGRES_DB_NAME
        $separateName = Get-ComposeResolvedEnvValue -EnvironmentValues $separateValues -Name "DMS_CONFIG_DATABASE_NAME" -DefaultValue $separateValues.POSTGRES_DB_NAME

        (Get-CmsDatabaseTopologyDefaultConnectionString -ExpectedHost "dms-postgresql" -ExpectedPort "5432" -ExpectedDatabaseName $sharedName -PostgresPassword 'abcdefgh1!') |
            Should -BeExactly $script:composeRenderedDefault
        (Get-CmsDatabaseTopologyDefaultConnectionString -ExpectedHost "dms-postgresql" -ExpectedPort "5432" -ExpectedDatabaseName $separateName -PostgresPassword 'abcdefgh1!') |
            Should -BeExactly $script:composeRenderedMigratedSeparateMode
    }

    It "Get-CmsDatabaseTopologyDefaultConnectionString's construction matches the real Compose-rendered value byte-for-byte" {
        # Round 8 Blocker 6: the prior oracle test only checked "does not throw" on the production
        # validator, which proves internal self-consistency but not that the constructed default is
        # textually identical to what Compose actually renders. Comparing the extracted, independently
        # testable construction function directly against the captured oracle string closes that gap.
        $constructed = Get-CmsDatabaseTopologyDefaultConnectionString -ExpectedHost "dms-postgresql" -ExpectedPort "5432" -ExpectedDatabaseName "edfi_datamanagementservice" -PostgresPassword "abcdefgh1!"
        $constructed | Should -BeExactly $script:composeRenderedDefault
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

        # PostgreSQL's own connection-string shape carries port as a standalone "port=" key - now
        # correctly recognized (Round 8 Blocker 4 fix) rather than silently defaulted.
        $endpoints = @(Get-EndpointFromResolvedConnectionString -ConnectionString $script:composeRenderedDefault -DatabaseEngine "postgresql")
        $endpoints[0].Host | Should -Be "dms-postgresql"
        $endpoints[0].Port | Should -Be "5432"
    }

    It "the production migration function's actual derived file, run through Confirm-CmsDatabaseTopologyAgreement, agrees with the real Compose-rendered migrated value" {
        # Round 9 Blocker 3: the prior oracle only covered the absent-key default construction, never
        # the migration/serialization path - which is exactly where the order-dependent-interpolation
        # bug this round found and fixed was hiding. This exercises the real, unmodified production
        # function end to end and validates its output against the empirically-captured oracle above.
        $work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-cms-topology-migration-oracle-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        try {
            $basePath = Join-Path $work ".env.base"
            Set-Content -LiteralPath $basePath -Value (@(
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'POSTGRES_PASSWORD=abcdefgh1!',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${POSTGRES_DB_NAME};'
            ) -join "`n") -NoNewline

            $derived = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $basePath -DatabaseEngine "postgresql" -SeparateConfigDatabase -DockerComposeRoot $work

            { Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $derived -DatabaseEngine "postgresql" } | Should -Not -Throw -Because "the migrated file must validate against its own now-separate-mode target"

            $migratedConnectionString = Get-ComposeResolvedEnvValue -EnvironmentValues (ReadValuesFromEnvFile $derived) -Name "DMS_CONFIG_DATABASE_CONNECTION_STRING"
            $migratedConnectionString | Should -BeExactly $script:composeRenderedMigratedSeparateMode -Because "this is the exact value a real Docker Compose invocation rendered for this same derived file"
        }
        finally {
            Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Describe "start-local-dms.ps1 / start-published-dms.ps1 CMS database topology wiring (DMS-1270 Phase 1b)" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force

        # Runs a start script with a "docker" stand-in in place and reports everything the run is
        # asserted on: the recorded docker invocations, the terminating error, and the files the run
        # added under eng/docker-compose/.derived/.
        #
        # The stand-in is a global PowerShell *function* named docker, not an executable on PATH.
        # PowerShell resolves functions ahead of external commands, and function lookup walks the
        # scope chain into the `&`-invoked start script, so this intercepts every `docker ...` call
        # with no PATH manipulation and no platform-specific shim file. That matters because the
        # registered CI job for these tests runs on ubuntu-latest, where a docker.cmd would not be
        # resolved as `docker` at all and the runner's real Docker would be invoked instead - which
        # for `docker compose ... up` would start actual containers.
        #
        # It succeeds for the `network` subcommands the start scripts issue before any compose call,
        # then fails the first `compose` subcommand. That first compose invocation already carries
        # the complete -f file set, so the recorded arguments are the real compose file set the
        # script built, and the immediately-following exit-code check turns the failure into a
        # specific, assertable error rather than an ambient "docker is missing" one. The start
        # scripts' own topology wiring is pure PowerShell that runs to completion before any of
        # this, so its derived-file side effects are observable too.
        #
        # Deliberately a single function rather than composed helpers: `& $ScriptBlock` executes in a
        # child scope, so a nested helper cannot assign a result back into the caller's scope, and a
        # nested arrangement silently reported $null instead.
        #
        # The start scripts always resolve their compose root to their own directory, so the real
        # .derived/ is the only place they will write. Every fixture therefore uses a GUID-bearing
        # env-file leaf name, so derived files (named <leaf>.<token>) cannot collide between tests or
        # overwrite a developer's existing .derived/.env.mssql.
        function script:Invoke-StartScript {
            param([scriptblock]$ScriptBlock)

            # -Force is required on every .derived enumeration in this Describe, not optional
            # tidiness: derived files are all dot-prefixed, and Linux PowerShell treats a leading dot
            # as hidden, so without it the snapshots come back empty, every derived-file assertion
            # silently matches nothing, and the cleanup leaves files behind. Windows has no such
            # attribute, so omitting it passes locally and fails only on the ubuntu-latest CI runner.
            $derivedDir = Join-Path $script:dockerComposeRoot ".derived"
            $before = @{}
            if (Test-Path $derivedDir) {
                foreach ($name in (Get-ChildItem $derivedDir -Name -Force)) { $before[$name] = $true }
            }

            # The stand-in has to be global to be visible inside the invoked start script, but the
            # list it records into is captured by closure rather than parked in a global variable,
            # so nothing of this harness leaks into the session beyond the function itself.
            $recorded = [System.Collections.Generic.List[string]]::new()
            $hadRealDocker = $null -ne (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue)
            $caught = $null
            try {
                # Recorded as a single space-joined string per invocation, which is what the
                # assertions match against. The callers splat array variables (the compose -f file
                # set, the up flags), and a PowerShell function receives each of those as a single
                # array object rather than the flattened argv a native command would get - so
                # enumerate one level through the pipeline before joining, or the whole file set
                # renders as "System.Object[]" and every file-set assertion silently matches nothing.
                Set-Item -Path Function:\global:docker -Value {
                    $flattened = @($args | ForEach-Object { $_ })
                    $recorded.Add(($flattened -join " "))
                    if ($flattened.Count -gt 0 -and $flattened[0] -eq "compose") {
                        $global:LASTEXITCODE = 1
                    }
                    else {
                        $global:LASTEXITCODE = 0
                    }
                }.GetNewClosure()

                & $ScriptBlock
            }
            catch {
                $caught = $_
            }
            finally {
                Remove-Item Function:\docker -Force -ErrorAction SilentlyContinue
            }

            # The interception must have been the thing that stopped the run; if a real docker
            # executable were reached instead, these tests would be starting containers.
            if ($hadRealDocker -and (Get-Command docker -CommandType Function -ErrorAction SilentlyContinue)) {
                throw "The docker stand-in outlived the run; refusing to continue with a live docker on PATH."
            }

            $after = if (Test-Path $derivedDir) { @(Get-ChildItem $derivedDir -Name -Force) } else { @() }
            $newDerived = @($after | Where-Object { -not $before.ContainsKey($_) })
            $invocations = @($recorded)

            return [PSCustomObject]@{
                Invocations     = $invocations
                ComposeCommand  = ($invocations | Where-Object { $_ -like "compose *" } | Select-Object -First 1)
                Error           = $caught
                ErrorMessage    = if ($null -ne $caught) { $caught.Exception.Message } else { $null }
                NewDerivedFiles = $newDerived
                TopologyFile    = ($newDerived | Where-Object { $_ -like "*.topology" } | Select-Object -First 1)
            }
        }

        # Reads a derived file produced by a run under test.
        function script:ReadDerivedTopologyFile {
            param([string]$Name)
            return ReadValuesFromEnvFile (Join-Path (Join-Path $script:dockerComposeRoot ".derived") $Name)
        }

        # Writes a base env file under a unique leaf name and returns its path. Extra lines are
        # appended after the shared minimum.
        function script:New-WiringEnvFile {
            param([string[]]$AdditionalLines = @())

            $path = Join-Path $script:work ".env.wiring-$([Guid]::NewGuid().ToString('N'))"
            $lines = @(
                'POSTGRES_PASSWORD=abcdefgh1!',
                'POSTGRES_DB_NAME=edfi_datamanagementservice',
                'DMS_CONFIG_IDENTITY_PROVIDER=self-contained'
            ) + $AdditionalLines
            Set-Content -LiteralPath $path -NoNewline -Value ($lines -join "`n")
            return $path
        }

        # A base env file already declaring the MSSQL engine, so the engine overlay is recognized as
        # composed and the CMS connection string under test is the one that gets validated.
        function script:New-MssqlWiringEnvFile {
            param([string]$CmsConnectionString)

            return New-WiringEnvFile -AdditionalLines @(
                'MSSQL_SA_PASSWORD=abcdefgh1!',
                'MSSQL_DB_NAME=edfi_datamanagementservice',
                'DMS_DATASTORE=mssql',
                'DMS_CONFIG_DATASTORE=mssql',
                "DMS_CONFIG_DATABASE_CONNECTION_STRING=$CmsConnectionString"
            )
        }
    }

    AfterAll {
        # Defensive: Invoke-StartScript removes this itself, including on failure.
        Remove-Item Function:\docker -Force -ErrorAction SilentlyContinue
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-startscript-wiring-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:work -Force | Out-Null
        $derivedDir = Join-Path $script:dockerComposeRoot ".derived"
        $script:derivedBefore = @{}
        if (Test-Path $derivedDir) {
            foreach ($name in (Get-ChildItem $derivedDir -Name -Force)) { $script:derivedBefore[$name] = $true }
        }
    }

    AfterEach {
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
        $derivedDir = Join-Path $script:dockerComposeRoot ".derived"
        if (Test-Path $derivedDir) {
            foreach ($name in (Get-ChildItem $derivedDir -Name -Force)) {
                if (-not $script:derivedBefore.ContainsKey($name)) {
                    Remove-Item (Join-Path $derivedDir $name) -Force -ErrorAction SilentlyContinue
                }
            }
        }
    }

    # Pester's discovery/run-phase separation does not reliably close over a plain PowerShell
    # `foreach` loop variable referenced inside `It` script blocks, so the two entry-point scripts
    # are covered by duplicated Context blocks (below) rather than a loop over their names.

    Context "start-local-dms.ps1" {
        It "postgresql separate mode: migrates DMS_CONFIG_DATABASE_NAME and reaches the docker boundary" {
            # PostgreSQL is at parity with SQL Server: the same topology-write sequence runs, so
            # -SeparateConfigDatabase is accepted rather than rejected by an engine guard.
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -SeparateConfigDatabase -DatabaseEngine postgresql -EnvironmentFile $envFile -InfraOnly *>$null
            }

            $run.ErrorMessage | Should -Not -BeLike "*not yet supported*"
            $run.TopologyFile | Should -Not -BeNullOrEmpty -Because "separate mode must write a topology-derived file on PostgreSQL too"

            $values = ReadDerivedTopologyFile -Name $run.TopologyFile
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_configurationservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "true"

            $run.ComposeCommand | Should -BeLike "*--env-file *$($run.TopologyFile)*"
        }

        It "postgresql shared mode: writes the seam without redirecting it away from POSTGRES_DB_NAME" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine postgresql -EnvironmentFile $envFile -InfraOnly *>$null
            }

            $run.TopologyFile | Should -Not -BeNullOrEmpty -Because "the seam is written unconditionally so old .env files predating the key still resolve"
            $values = ReadDerivedTopologyFile -Name $run.TopologyFile
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_datamanagementservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "false"

            $run.ComposeCommand | Should -Not -BeNullOrEmpty
        }

        It "shared mode (switch omitted): does not migrate DMS_CONFIG_DATABASE_NAME away from its .env.mssql alias" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -EnvironmentFile $envFile -InfraOnly *>$null
            }

            # Only the engine-overlay composition (<leaf>.mssql) is expected: the checked-in
            # .env.mssql already aliases DMS_CONFIG_DATABASE_NAME to MSSQL_DB_NAME, so the topology
            # function correctly recognizes shared mode as already-correct and writes nothing
            # further - no additional ".topology" derived file.
            $run.NewDerivedFiles | Should -HaveCount 1
            $run.NewDerivedFiles[0] | Should -BeLike "*.mssql"
            $run.TopologyFile | Should -BeNullOrEmpty

            # The run must have reached the docker boundary and stopped exactly there, not failed
            # earlier for an unrelated reason that would make the assertions above vacuous.
            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "wiring must complete and reach the compose invocation"
            $run.ErrorMessage | Should -BeLike "*Failed to start SQL Server. Exit code 1*"
        }

        It "separate mode (-SeparateConfigDatabase): migrates DMS_CONFIG_DATABASE_NAME to edfi_configurationservice" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly *>$null
            }

            $run.TopologyFile | Should -Not -BeNullOrEmpty -Because "separate mode must write a further-derived file on top of the engine overlay"

            $values = ReadDerivedTopologyFile -Name $run.TopologyFile
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_configurationservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "true"

            # The derived topology file must be the one actually handed to Compose, not merely
            # written and then dropped on the floor.
            $run.ComposeCommand | Should -BeLike "*--env-file *$($run.TopologyFile)*"
            $run.ErrorMessage | Should -BeLike "*Failed to start SQL Server. Exit code 1*"
        }

        It "-DmsOnly: cmsParticipates is false, so the Phase 2 postgresql guard never fires (today's -DmsOnly shape is preserved)" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DmsOnly -SeparateConfigDatabase -DatabaseEngine postgresql -EnvironmentFile $envFile *>$null
            }

            $run.ErrorMessage | Should -Not -BeLike "*Phase 2*" -Because "-DmsOnly is excluded from cmsParticipates, so the whole gate (including the postgresql guard) must be skipped, not just bypassed with a different error"
            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "the run must proceed past the gate to the docker boundary"
        }

        It "-DmsOnly: does not write a CMS topology-derived file even with -SeparateConfigDatabase (cmsParticipates is false, so the topology functions never run)" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-local-dms.ps1" -DmsOnly -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile *>$null
            }

            $run.TopologyFile | Should -BeNullOrEmpty -Because "Resolve-CmsDatabaseTopologyEnvironmentFile must not run when cmsParticipates is false"
            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "the run must proceed to the docker boundary"
        }
    }

    Context "start-published-dms.ps1" {
        It "postgresql separate mode: migrates DMS_CONFIG_DATABASE_NAME and reaches the docker boundary" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -SeparateConfigDatabase -DatabaseEngine postgresql -EnvironmentFile $envFile -InfraOnly *>$null
            }

            $run.ErrorMessage | Should -Not -BeLike "*not yet supported*"
            $run.TopologyFile | Should -Not -BeNullOrEmpty -Because "separate mode must write a topology-derived file on PostgreSQL too"

            $values = ReadDerivedTopologyFile -Name $run.TopologyFile
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_configurationservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "true"

            $run.ComposeCommand | Should -BeLike "*--env-file *$($run.TopologyFile)*"
        }

        It "postgresql shared mode: writes the seam without redirecting it away from POSTGRES_DB_NAME" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine postgresql -EnvironmentFile $envFile -InfraOnly *>$null
            }

            $run.TopologyFile | Should -Not -BeNullOrEmpty
            $values = ReadDerivedTopologyFile -Name $run.TopologyFile
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_datamanagementservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "false"

            $run.ComposeCommand | Should -Not -BeNullOrEmpty
        }

        It "shared mode (switch omitted): does not migrate DMS_CONFIG_DATABASE_NAME away from its .env.mssql alias" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -EnvironmentFile $envFile -InfraOnly *>$null
            }

            $run.NewDerivedFiles | Should -HaveCount 1
            $run.NewDerivedFiles[0] | Should -BeLike "*.mssql"
            $run.TopologyFile | Should -BeNullOrEmpty

            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "wiring must complete and reach the compose invocation"
            $run.ErrorMessage | Should -BeLike "*Failed to start SQL Server. Exit code 1*"
        }

        It "separate mode (-SeparateConfigDatabase): migrates DMS_CONFIG_DATABASE_NAME to edfi_configurationservice" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile -InfraOnly *>$null
            }

            $run.TopologyFile | Should -Not -BeNullOrEmpty -Because "separate mode must write a further-derived file on top of the engine overlay"

            $values = ReadDerivedTopologyFile -Name $run.TopologyFile
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_configurationservice"
            $values["DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE"] | Should -Be "true"

            $run.ComposeCommand | Should -BeLike "*--env-file *$($run.TopologyFile)*"
            $run.ErrorMessage | Should -BeLike "*Failed to start SQL Server. Exit code 1*"
        }

        It "-DmsOnly: cmsParticipates is false, so the Phase 2 postgresql guard never fires (today's -DmsOnly shape is preserved)" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DmsOnly -SeparateConfigDatabase -DatabaseEngine postgresql -EnvironmentFile $envFile *>$null
            }

            $run.ErrorMessage | Should -Not -BeLike "*Phase 2*" -Because "-DmsOnly is excluded from cmsParticipates, so the whole gate (including the postgresql guard) must be skipped, not just bypassed with a different error"
            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "the run must proceed past the gate to the docker boundary"
        }

        It "-DmsOnly: does not write a CMS topology-derived file even with -SeparateConfigDatabase (cmsParticipates is false, so the topology functions never run)" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -DmsOnly -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile *>$null
            }

            $run.TopologyFile | Should -BeNullOrEmpty -Because "Resolve-CmsDatabaseTopologyEnvironmentFile must not run when cmsParticipates is false"
            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "the run must proceed to the docker boundary"
        }
    }

    # The published script's CMS-participation gate is narrower than the local script's: CMS must
    # also actually be in the compose set. A bare published Keycloak start (no -EnableConfig, no
    # -InfraOnly, not bootstrap mode, no -SeparateConfigDatabase) omits published-config.yml
    # entirely, so CMS never runs and this story's topology validator must not pass judgment on its
    # endpoint. These cover both halves behaviorally: the compose file set as actually built, and
    # the gate that follows from it.
    Context "start-published-dms.ps1 Configuration Service participation" {
        It "omits published-config.yml for a bare Keycloak start (CMS opt-in preserved)" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -IdentityProvider keycloak -EnvironmentFile $envFile *>$null
            }

            $run.ComposeCommand | Should -Not -BeNullOrEmpty
            $run.ComposeCommand | Should -Not -BeLike "*published-config.yml*"
        }

        It "includes published-config.yml for -SeparateConfigDatabase under Keycloak, which would otherwise omit it" {
            $envFile = New-WiringEnvFile

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -IdentityProvider keycloak -DatabaseEngine mssql -SeparateConfigDatabase -EnvironmentFile $envFile *>$null
            }

            $run.ComposeCommand | Should -BeLike "*published-config.yml*" -Because "CMS must actually run to create the dedicated database"
            $run.TopologyFile | Should -Not -BeNullOrEmpty -Because "CMS participates in this shape, so the topology sequence must run"
        }

        It "includes published-config.yml for -EnableConfig and for self-contained identity" {
            $enableConfigRun = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -IdentityProvider keycloak -EnableConfig -EnvironmentFile (New-WiringEnvFile) *>$null
            }
            $enableConfigRun.ComposeCommand | Should -BeLike "*published-config.yml*"

            $selfContainedRun = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -IdentityProvider self-contained -EnvironmentFile (New-WiringEnvFile) *>$null
            }
            $selfContainedRun.ComposeCommand | Should -BeLike "*published-config.yml*"
        }

        It "does not run this story's topology validator for a bare Keycloak start that omits CMS" {
            # CMS is absent from the compose set, so Confirm-CmsDatabaseTopologyAgreement must not
            # run and no topology-derived file may be written. A consistent (shared) CMS connection
            # string keeps the legacy shared-database check satisfied, isolating the gate itself as
            # the behavior under test.
            $envFile = New-MssqlWiringEnvFile -CmsConnectionString 'Server=dms-mssql,1433;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -IdentityProvider keycloak -DatabaseEngine mssql -EnvironmentFile $envFile *>$null
            }

            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "the run must reach the docker boundary rather than being rejected by a validator that should not have run"
            $run.ComposeCommand | Should -Not -BeLike "*published-config.yml*"
            $run.TopologyFile | Should -BeNullOrEmpty -Because "the topology sequence is gated on CMS participation"
            $run.ErrorMessage | Should -Not -BeLike "*topology*"
        }

        It "keeps today's legacy shared-database rejection for a bare Keycloak start whose CMS database name disagrees" {
            # Non-participating shapes must keep running Assert-MssqlCmsDatabaseIsShared exactly as
            # they do today (the spec's own requirement), so a CMS connection string naming a
            # different database is still rejected here - by the legacy DMS-1255 check, not by this
            # story's topology validator. Only the database name differs: that check inspects the
            # Database/Initial Catalog aliases and nothing else, so the database name alone is what
            # drives this rejection.
            $envFile = New-MssqlWiringEnvFile -CmsConnectionString 'Server=dms-mssql,1433;Database=a_totally_different_db;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -IdentityProvider keycloak -DatabaseEngine mssql -EnvironmentFile $envFile *>$null
            }

            $run.ErrorMessage | Should -BeLike "*shared-database configuration mismatch*"
            $run.ErrorMessage | Should -BeLike "*a_totally_different_db*"
            $run.ErrorMessage | Should -Not -BeLike "*CMS database topology mismatch*" -Because "the legacy check owns this non-participating shape, not this story's validator"
            $run.Invocations | Should -BeNullOrEmpty -Because "the legacy check rejects the invocation before any docker call, exactly as it does today"
        }

        It "accepts a custom CMS host under bare Keycloak, proving the endpoint validator did not run" {
            # The sharpest available probe of the participation gate: host and port are checked only
            # by Confirm-CmsDatabaseTopologyAgreement, never by the legacy database-name check. So a
            # CMS connection string whose database name agrees but whose host is not dms-mssql must
            # be accepted for this non-participating shape - if the gate were wrongly broad the new
            # validator would run and reject the host. Complements the database-mismatch test above,
            # which the legacy check alone can explain.
            $envFile = New-MssqlWiringEnvFile -CmsConnectionString 'Server=some-other-host,1433;Database=${MSSQL_DB_NAME};User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'

            $run = Invoke-StartScript {
                & "$script:dockerComposeRoot/start-published-dms.ps1" -IdentityProvider keycloak -DatabaseEngine mssql -EnvironmentFile $envFile *>$null
            }

            $run.ComposeCommand | Should -Not -BeNullOrEmpty -Because "a custom CMS host is irrelevant when CMS never starts, so the run must reach the docker boundary"
            $run.ComposeCommand | Should -Not -BeLike "*published-config.yml*"
            $run.ErrorMessage | Should -Not -BeLike "*topology*" -Because "Confirm-CmsDatabaseTopologyAgreement checks the host and must not have run"
            $run.TopologyFile | Should -BeNullOrEmpty
        }
    }
}
