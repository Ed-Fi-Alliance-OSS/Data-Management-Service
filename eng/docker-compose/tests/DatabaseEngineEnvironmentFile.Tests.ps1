# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# DMS-1238: -DatabaseEngine mssql composes the .env.mssql overlay onto the base environment file
# so every phase (configure, provision, and the DMS container itself) agrees on DMS_DATASTORE
# and the SQL Server connection strings, instead of relying on a standalone -EnvironmentFile.

param()

Describe "Test-NativeCommandWithTimeout" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
        $script:pwshPath = (Get-Process -Id $PID).Path
    }

    It "returns true when the native command exits successfully" {
        Test-NativeCommandWithTimeout `
            -FilePath $script:pwshPath `
            -ArgumentList @("-NoProfile", "-Command", "exit 0") `
            -TimeoutSeconds 5 | Should -BeTrue
    }

    It "returns false when the native command completes with a non-zero exit code" {
        Test-NativeCommandWithTimeout `
            -FilePath $script:pwshPath `
            -ArgumentList @("-NoProfile", "-Command", "exit 3") `
            -TimeoutSeconds 5 | Should -BeFalse
    }

    It "returns false and terminates a native command that exceeds the timeout" {
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $result = Test-NativeCommandWithTimeout `
            -FilePath $script:pwshPath `
            -ArgumentList @("-NoProfile", "-Command", "Start-Sleep -Seconds 30") `
            -TimeoutSeconds 1
        $stopwatch.Stop()

        $result | Should -BeFalse
        $stopwatch.Elapsed.TotalSeconds | Should -BeLessThan 5
    }
}

Describe "Resolve-DatabaseEngineEnvironmentFile" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force

        # This Describe now consumes AMBIENT values: the composed environment is evaluated with Compose
        # precedence, so a leftover shell variable can decide the outcome of a test that never mentions
        # it - and an invalid ambient connection string is deliberately unrepairable, which turns an
        # unrelated leak into a hard failure. Every name the base/overlay evaluation can consume is
        # therefore isolated per test. The real overlay's keys are read from the file rather than listed,
        # so the isolation set cannot drift when the overlay gains a setting.
        $script:ambientIsolatedNames = @(
            @(Resolve-DotenvFileSequentially -Path (Join-Path $script:dockerComposeRoot ".env.mssql")).Declarations |
                ForEach-Object { $_.Key }
        ) + @(
            # Topology seam and marker keys, plus the dependency names these tests author themselves.
            'DMS_CONFIG_DATABASE_NAME', 'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE',
            'POSTGRES_DB_NAME', 'POSTGRES_PASSWORD', 'DATABASE_TEMPLATE_PACKAGE',
            'CMS_DATABASE_NAME', 'CMS_DB_OVERRIDE_XYZ', 'WHOLE_CONN', 'LATE_ONE'
        ) | Sort-Object -Unique
    }

    BeforeEach {
        # Snapshot EXISTENCE separately from value: restoring a variable that did not exist by setting it
        # to "" is not the same thing, and on some platforms an empty variable cannot exist at all.
        $script:ambientSnapshot = @{}
        foreach ($name in $script:ambientIsolatedNames) {
            $script:ambientSnapshot[$name] = @{
                Existed = [bool](Test-Path -LiteralPath "Env:\$name")
                Value   = [System.Environment]::GetEnvironmentVariable($name)
            }
            Remove-Item -LiteralPath "Env:\$name" -ErrorAction SilentlyContinue
        }

        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-engine-env-$([Guid]::NewGuid().ToString('N'))"
        $script:composeRoot = Join-Path $script:work "compose"
        New-Item -ItemType Directory -Path $script:composeRoot -Force | Out-Null
        $script:basePath = Join-Path $script:work ".env.base"
        Set-Content -LiteralPath $script:basePath -Value "DMS_DATASTORE=postgresql`nPOSTGRES_DB_NAME=edfi_datamanagementservice`nLOG_LEVEL=Warning`n" -NoNewline

        # A minimal stand-in for the real .env.mssql overlay; the real file's exact key set is
        # covered separately below.
        Set-Content -LiteralPath (Join-Path $script:composeRoot ".env.mssql") -Value @"
MSSQL_SA_PASSWORD=Abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
MSSQL_PORT=1435
DMS_DATASTORE=mssql
DATABASE_CONNECTION_STRING_ADMIN=Server=dms-mssql;Database=`${MSSQL_DB_NAME};User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
DMS_CONFIG_DATASTORE=mssql
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=`${MSSQL_DB_NAME};User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
"@ -NoNewline
    }

    AfterEach {
        # Restore the EXACT prior state, including on failure. A variable that existed is put back with
        # its original value; one that did not exist is removed rather than left as an empty string.
        foreach ($name in $script:ambientIsolatedNames) {
            $prior = $script:ambientSnapshot[$name]
            if ($null -ne $prior -and $prior.Existed) {
                [System.Environment]::SetEnvironmentVariable($name, $prior.Value)
            }
            else {
                Remove-Item -LiteralPath "Env:\$name" -ErrorAction SilentlyContinue
            }
        }

        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "returns the base file unchanged for the default postgresql engine" {
        $result = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "postgresql" -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot
        $result | Should -Be $script:basePath
    }

    It "composes the .env.mssql overlay into a derived file for the mssql engine" {
        $result = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot
        $result | Should -Not -Be $script:basePath

        $values = ReadValuesFromEnvFile $result
        $values["DMS_DATASTORE"] | Should -Be "mssql"
        $values["DATABASE_CONNECTION_STRING_ADMIN"] | Should -Be 'Server=dms-mssql;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
        $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be 'Server=dms-mssql,1433;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'

        # Unrelated base lines survive the composition.
        $values["POSTGRES_DB_NAME"] | Should -Be "edfi_datamanagementservice"
        $values["LOG_LEVEL"] | Should -Be "Warning"
    }

    It "composes the overlay onto a custom base environment file, not just the default .env" {
        $customBasePath = Join-Path $script:work ".env.custom"
        Set-Content -LiteralPath $customBasePath -Value "DMS_DATASTORE=postgresql`nCUSTOM_KEY=custom-value`n" -NoNewline

        $result = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $customBasePath -DockerComposeRoot $script:composeRoot

        $values = ReadValuesFromEnvFile $result
        $values["DMS_DATASTORE"] | Should -Be "mssql"
        $values["CUSTOM_KEY"] | Should -Be "custom-value" -Because "the overlay must land on top of a caller-supplied base file, not only the default env"
    }

    It "is idempotent: returns the base file unchanged when the full overlay signal is already composed" {
        # Mirrors an already-composed derived file (e.g. one the bootstrap wrapper produced and
        # forwarded to start-local-dms.ps1 via -EnvironmentFile): composing again must not
        # produce a derived-of-derived file. Completeness is proved from every overlay key.
        $alreadyComposedPath = Join-Path $script:work ".env.derived"
        $alreadyComposedContent = (Get-Content -LiteralPath (Join-Path $script:composeRoot ".env.mssql") -Raw) +
            "`nDATABASE_TEMPLATE_PACKAGE=EdFi.Api.Minimal.Template.MsSql.5.2.0`n"
        Set-Content -LiteralPath $alreadyComposedPath -Value $alreadyComposedContent -NoNewline

        $result = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $alreadyComposedPath -DockerComposeRoot $script:composeRoot

        $result | Should -Be $alreadyComposedPath
    }

    It "does not treat the former three-key signal as a complete MSSQL overlay" {
        $partialPath = Join-Path $script:work ".env.former-signal"
        Set-Content -LiteralPath $partialPath -Value @"
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
MSSQL_SA_PASSWORD=CustomSecret1!
"@ -NoNewline

        $result = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $partialPath -DockerComposeRoot $script:composeRoot

        $result | Should -Not -Be $partialPath
        $values = ReadValuesFromEnvFile $result
        $values["MSSQL_DB_NAME"] | Should -Not -BeNullOrEmpty
        $values["MSSQL_PORT"] | Should -Not -BeNullOrEmpty
        $values["DATABASE_CONNECTION_STRING_ADMIN"] | Should -Not -BeNullOrEmpty
        $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Not -BeNullOrEmpty
        $values["MSSQL_SA_PASSWORD"] | Should -Be "CustomSecret1!" -Because "a valid caller override must survive completion"
    }

    It "composes the overlay onto a partial env carrying DMS_DATASTORE=mssql without the full overlay signal" {
        # A hand-authored env with only DMS_DATASTORE=mssql must not be mistaken for a
        # wrapper-composed file: it would miss the CMS SQL Server settings and credentials
        # while mssql.yml starts no PostgreSQL container to fall back to.
        $partialPath = Join-Path $script:work ".env.partial"
        Set-Content -LiteralPath $partialPath -Value "DMS_DATASTORE=mssql`n" -NoNewline

        $result = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $partialPath -DockerComposeRoot $script:composeRoot

        $result | Should -Not -Be $partialPath
        $values = ReadValuesFromEnvFile $result
        $values["DMS_DATASTORE"] | Should -Be "mssql"
        $values["DMS_CONFIG_DATASTORE"] | Should -Be "mssql"
        $values["MSSQL_SA_PASSWORD"] | Should -Not -BeNullOrEmpty
    }

    It "fills missing MSSQL keys without clobbering valid custom MSSQL values" {
        $partialPath = Join-Path $script:work ".env.partial-custom"
        Set-Content -LiteralPath $partialPath -Value @"
DMS_DATASTORE=mssql
MSSQL_SA_PASSWORD=CustomSecret1!
MSSQL_DB_NAME=custom_database
MSSQL_PORT=1999
CUSTOM_KEY=preserved
"@ -NoNewline

        $result = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $partialPath -DockerComposeRoot $script:composeRoot
        $values = ReadValuesFromEnvFile $result

        $values["DMS_DATASTORE"] | Should -Be "mssql"
        $values["DMS_CONFIG_DATASTORE"] | Should -Be "mssql"
        $values["MSSQL_SA_PASSWORD"] | Should -Be "CustomSecret1!"
        $values["MSSQL_DB_NAME"] | Should -Be "custom_database"
        $values["MSSQL_PORT"] | Should -Be "1999"
        $values["DATABASE_CONNECTION_STRING_ADMIN"] | Should -Not -BeNullOrEmpty
        $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Not -BeNullOrEmpty
        $values["CUSTOM_KEY"] | Should -Be "preserved"
    }

    It "replaces PostgreSQL connection strings when only one datastore discriminator was changed to MSSQL" {
        $partialPath = Join-Path $script:work ".env.partial-from-postgresql"
        $postgresqlTemplate = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot ".env.template") -Raw
        $partialContent = $postgresqlTemplate.Replace("DMS_DATASTORE=postgresql", "DMS_DATASTORE=mssql")
        Set-Content -LiteralPath $partialPath -Value $partialContent -NoNewline

        $result = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $partialPath -DockerComposeRoot $script:composeRoot
        $values = ReadValuesFromEnvFile $result

        $values["DMS_DATASTORE"] | Should -Be "mssql"
        $values["DMS_CONFIG_DATASTORE"] | Should -Be "mssql"
        $values["DATABASE_CONNECTION_STRING_ADMIN"] | Should -Match '^Server='
        $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Match '^Server='
        $values["DATABASE_CONNECTION_STRING_ADMIN"] | Should -Not -Match '(?i)(?:^|;)\s*host\s*='
        $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Not -Match '(?i)(?:^|;)\s*host\s*='
    }

    It "does not short-circuit a fully populated MSSQL-marked file carrying PostgreSQL connection strings" {
        $contradictoryPath = Join-Path $script:work ".env.contradictory"
        $contradictoryContent = (Get-Content -LiteralPath (Join-Path $script:composeRoot ".env.mssql") -Raw).
            Replace(
                'Server=dms-mssql;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;',
                'host=dms-postgresql;port=5432;database=postgres;username=postgres;password=postgres;'
            ).
            Replace(
                'Server=dms-mssql,1433;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;',
                'host=dms-postgresql;port=5432;database=edfi_datamanagementservice;username=postgres;password=postgres;'
            )
        Set-Content -LiteralPath $contradictoryPath -Value $contradictoryContent -NoNewline

        $result = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $contradictoryPath -DockerComposeRoot $script:composeRoot
        $values = ReadValuesFromEnvFile $result

        $result | Should -Not -Be $contradictoryPath
        $values["DATABASE_CONNECTION_STRING_ADMIN"] | Should -Match '^Server='
        $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Match '^Server='
    }

    It "preserves valid caller-authored MSSQL connection strings while completing a partial file" {
        # CMS_DATABASE_NAME is a literal here. It used to reference ${MSSQL_DB_NAME}, which composition
        # moves into the overlay block, leaving this base-block line ahead of its own dependency - so the
        # composed file rendered Database= empty. The old assertion compared only the preserved line's
        # RAW text and therefore passed anyway; the resolved assertion below would not have.
        $partialPath = Join-Path $script:work ".env.partial-custom-connections"
        Set-Content -LiteralPath $partialPath -Value @"
DMS_CONFIG_DATASTORE=mssql
MSSQL_DB_NAME=custom_database
CMS_DATABASE_NAME=custom_database
DATABASE_CONNECTION_STRING_ADMIN=Data Source=custom-admin,1444;Initial Catalog=master;User Id=custom;Password=secret;
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=custom-cms,1444;Database=`${CMS_DATABASE_NAME};User Id=custom;Password=secret;
"@ -NoNewline

        $result = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $partialPath -DockerComposeRoot $script:composeRoot
        $values = ReadValuesFromEnvFile $result

        $values["DATABASE_CONNECTION_STRING_ADMIN"] | Should -Be "Data Source=custom-admin,1444;Initial Catalog=master;User Id=custom;Password=secret;"
        $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be 'Server=custom-cms,1444;Database=${CMS_DATABASE_NAME};User Id=custom;Password=secret;'

        # The written file must also RENDER one effective CMS declaration with the caller's host,
        # database, and credentials - the raw line alone does not prove that.
        $composed = Resolve-DotenvFileSequentially -Path $result
        @($composed.Declarations | Where-Object { $_.Key -eq 'DMS_CONFIG_DATABASE_CONNECTION_STRING' }).Count |
            Should -Be 1 -Because "composition must leave exactly one declaration of the key"
        $composed.Effective["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
            Should -BeExactly 'Server=custom-cms,1444;Database=custom_database;User Id=custom;Password=secret;'
    }

    # Validation and the write must describe the same artifact, so these assert the RETURNED FILE: one
    # effective connection declaration, and the host, database, and credentials it actually renders.
    # Asserting only "no exception" or only the raw line hid three divergences between the validated
    # model and the written file.
    It "writes one effective CMS declaration rendering the caller's values: <_.label>" -ForEach @(
        @{
            label = 'a preserved alias that depends on an overlay-only default'
            lines = @(
                'DMS_CONFIG_DATABASE_NAME=${MSSQL_DB_NAME}'
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            )
            expectedHost = 'dms-mssql,1433'
            expectedDatabase = 'edfi_datamanagementservice'
            expectedPassword = 'abcdefgh1!'
        }
        @{
            label = 'an export-spelled caller declaration (must not survive alongside the overlay''s)'
            lines = @(
                'MSSQL_DB_NAME=edfi_datamanagementservice'
                'export DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=custom-cms,1444;Database=edfi_datamanagementservice;User Id=custom;Password=secret;'
            )
            expectedHost = 'custom-cms,1444'
            expectedDatabase = 'edfi_datamanagementservice'
            expectedPassword = 'secret'
        }
        @{
            label = 'an outer-quoted caller value'
            lines = @(
                'MSSQL_DB_NAME=edfi_datamanagementservice'
                'DMS_CONFIG_DATABASE_CONNECTION_STRING="Server=custom-cms,1444;Database=edfi_datamanagementservice;User Id=custom;Password=secret;"'
            )
            expectedHost = 'custom-cms,1444'
            expectedDatabase = 'edfi_datamanagementservice'
            expectedPassword = 'secret'
        }
        @{
            label = 'a whole-string reference caller value'
            lines = @(
                'MSSQL_DB_NAME=edfi_datamanagementservice'
                'WHOLE_CONN=Server=custom-cms,1444;Database=edfi_datamanagementservice;User Id=custom;Password=secret;'
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=${WHOLE_CONN}'
            )
            expectedHost = 'custom-cms,1444'
            expectedDatabase = 'edfi_datamanagementservice'
            expectedPassword = 'secret'
        }
        @{
            # Narrowness: a PostgreSQL-shaped caller value must still lose to the overlay default, or a
            # partially-edited file would keep a PostgreSQL CMS target on an MSSQL run.
            label = 'a PostgreSQL-shaped caller value, which the overlay default must replace'
            lines = @(
                'MSSQL_DB_NAME=edfi_datamanagementservice'
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=pg;database=edfi_datamanagementservice;'
            )
            expectedHost = 'dms-mssql,1433'
            expectedDatabase = 'edfi_datamanagementservice'
            expectedPassword = 'abcdefgh1!'
        }
    ) {
        # The REAL overlay, because which keys the overlay owns decides what composition relocates.
        $realComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $path = Join-Path $script:work ".env.written-$([Guid]::NewGuid().ToString('N'))"
        Set-Content -LiteralPath $path -Value ((@(
            'MSSQL_SA_PASSWORD=abcdefgh1!'
            'DMS_DATASTORE=mssql'
            'DMS_CONFIG_DATASTORE=mssql'
        ) + $_.lines) -join "`n") -NoNewline

        $result = Resolve-DatabaseEngineEnvironmentFile `
            -DatabaseEngine "mssql" `
            -BaseEnvironmentFile $path `
            -DockerComposeRoot $realComposeRoot

        $composed = Resolve-DotenvFileSequentially -Path $result
        @($composed.Declarations | Where-Object { $_.Key -eq 'DMS_CONFIG_DATABASE_CONNECTION_STRING' }).Count |
            Should -Be 1 -Because "the written file must carry exactly one declaration of the key"

        $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
        $builder.set_ConnectionString([string]$composed.Effective['DMS_CONFIG_DATABASE_CONNECTION_STRING'])
        [string]$builder['Server'] | Should -BeExactly $_.expectedHost
        [string]$builder['Database'] | Should -BeExactly $_.expectedDatabase
        [string]$builder['Password'] | Should -BeExactly $_.expectedPassword
    }

    # THE AUTHORITY MODEL. For an MSSQL run the final Compose-effective environment - after composition
    # and after ambient precedence - is the only validation authority, and a file rewrite can repair a
    # file-authored value but never an ambient override. These cases prove the invariant rather than
    # merely reaching an exception: each asserts what the FINAL effective environment holds, and the
    # ambient cases additionally assert that no derived file was written and that no credential leaked.
    Context "final-effective-environment authority (DMS-1270)" {
        BeforeAll {
            $script:validMssqlBaseLines = @(
                'MSSQL_SA_PASSWORD=abcdefgh1!'
                'MSSQL_DB_NAME=edfi_datamanagementservice'
                'DMS_DATASTORE=mssql'
                'DMS_CONFIG_DATASTORE=mssql'
                'DATABASE_CONNECTION_STRING_ADMIN=Server=dms-mssql;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
                'DMS_CONFIG_DATABASE_NAME=${MSSQL_DB_NAME}'
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            )
        }

        It "fails clearly when the ambient environment supplies a non-MSSQL <_>" -ForEach @(
            'DATABASE_CONNECTION_STRING_ADMIN'
            'DMS_CONFIG_DATABASE_CONNECTION_STRING'
        ) {
            # Ambient wins over every declaration in the file being written, so excluding the file
            # declaration and re-composing changes nothing: the effective value stays PostgreSQL-shaped.
            # Before this, the function did exactly that and then wrote a derived file anyway, so an
            # MSSQL run proceeded with a PostgreSQL connection string.
            $realComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
            $localRoot = Join-Path $script:work "ambient-authority-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $localRoot -Force | Out-Null
            Copy-Item (Join-Path $realComposeRoot ".env.mssql") (Join-Path $localRoot ".env.mssql")

            # The fixture name deliberately avoids the word this test matches on: the diagnostic embeds
            # the file path, so a filename containing "ambient" would satisfy the assertion by accident
            # and hide a regression in the explanation itself.
            $path = Join-Path $script:work ".env.shellset-$([Guid]::NewGuid().ToString('N'))"
            Set-Content -LiteralPath $path -Value ($script:validMssqlBaseLines -join "`n") -NoNewline

            $secret = 'AmbientSecret1!'
            [System.Environment]::SetEnvironmentVariable(
                $_, "host=dms-postgresql;port=5432;username=postgres;password=$secret;database=leaked_db;")

            $failure = $null
            try { Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $path -DockerComposeRoot $localRoot }
            catch { $failure = $_.Exception.Message }

            $failure | Should -Not -BeNullOrEmpty -Because "a file rewrite cannot repair an ambient override"
            $failure | Should -BeLike "*$_*" -Because "the diagnostic must name the offending key"
            # The explanation must say WHY no rewrite can help, so the operator fixes their shell rather
            # than editing the env file forever.
            $failure | Should -BeLike "*the ambient environment sets*"
            $failure | Should -BeLike "*precedence over every declaration*"
            $failure | Should -Not -BeLike "*$secret*" -Because "a connection string carries credentials"
            $failure | Should -Not -BeLike "*leaked_db*"
            $failure | Should -Not -BeLike "*dms-postgresql*"

            # And nothing may be written: a derived file would change nothing and imply success.
            Test-Path -LiteralPath (Join-Path $localRoot ".derived") | Should -BeFalse
        }

        It "accepts a valid MSSQL ambient override" {
            # Narrowness: ambient precedence is legitimate, and only a NON-MSSQL ambient value is fatal.
            $realComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
            $localRoot = Join-Path $script:work "ambient-valid"
            New-Item -ItemType Directory -Path $localRoot -Force | Out-Null
            Copy-Item (Join-Path $realComposeRoot ".env.mssql") (Join-Path $localRoot ".env.mssql")

            $path = Join-Path $script:work ".env.ambient-valid"
            Set-Content -LiteralPath $path -Value ($script:validMssqlBaseLines -join "`n") -NoNewline

            [System.Environment]::SetEnvironmentVariable(
                'DATABASE_CONNECTION_STRING_ADMIN',
                'Server=other-admin,1444;Database=master;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;')

            $result = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $path -DockerComposeRoot $localRoot
            [string](Get-SequentialEffectiveValue `
                -Evaluation (Resolve-DotenvFileSequentially -Path $result) `
                -Name 'DATABASE_CONNECTION_STRING_ADMIN') |
                Should -BeExactly 'Server=other-admin,1444;Database=master;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
        }

        It "proves every overlay-owned connection-string key MSSQL-shaped in the final environment" {
            # The postcondition's positive side: after repairs, ALL required keys must hold, not just the
            # CMS one the topology validator happens to inspect.
            $realComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
            $path = Join-Path $script:work ".env.all-keys-shaped"
            Set-Content -LiteralPath $path -Value (@(
                'MSSQL_SA_PASSWORD=abcdefgh1!'
                'MSSQL_DB_NAME=edfi_datamanagementservice'
                'DMS_DATASTORE=mssql'
                'DMS_CONFIG_DATASTORE=mssql'
                # Both file-authored strings are PostgreSQL-shaped, so both are repaired from the overlay.
                'DATABASE_CONNECTION_STRING_ADMIN=host=dms-postgresql;port=5432;username=postgres;password=pg;database=postgres;'
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=pg;database=edfi_datamanagementservice;'
            ) -join "`n") -NoNewline

            $result = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $path -DockerComposeRoot $realComposeRoot
            $final = Resolve-DotenvFileSequentially -Path $result

            $overlayConnectionKeys = @(
                @(Resolve-DotenvFileSequentially -Path (Join-Path $realComposeRoot ".env.mssql")).Declarations |
                    ForEach-Object { $_.Key } | Where-Object { $_ -match 'CONNECTION_STRING' } | Sort-Object -Unique
            )
            $overlayConnectionKeys.Count | Should -BeGreaterThan 1 -Because "both the admin and CMS keys must be covered"
            foreach ($key in $overlayConnectionKeys) {
                Test-MssqlConnectionStringValue -ConnectionString ([string](Get-SequentialEffectiveValue -Evaluation $final -Name $key)) |
                    Should -BeTrue -Because "'$key' must be SQL Server-shaped in the final effective environment"
            }
        }
    }

    # Preservation is decided per connection-string KEY, not for all of them together. The admin and CMS
    # strings are independent settings, so an all-or-nothing decision keyed on the CMS string either
    # carried a PostgreSQL admin string into an MSSQL environment or discarded a valid custom admin one.
    It "decides connection-string preservation per key: <_.label>" -ForEach @(
        @{
            label = 'valid MSSQL CMS with a PostgreSQL admin replaces only the admin'
            adminLine = 'DATABASE_CONNECTION_STRING_ADMIN=host=dms-postgresql;port=5432;username=postgres;password=pg;database=postgres;'
            cmsLine = 'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=custom-cms,1444;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
            expectedAdmin = 'Server=dms-mssql;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
            expectedCms = 'Server=custom-cms,1444;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
        }
        @{
            label = 'PostgreSQL CMS with a valid custom MSSQL admin keeps the admin customization'
            adminLine = 'DATABASE_CONNECTION_STRING_ADMIN=Server=custom-admin,1444;Database=master;User Id=custom;Password=secret;'
            cmsLine = 'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=pg;database=edfi_datamanagementservice;'
            expectedAdmin = 'Server=custom-admin,1444;Database=master;User Id=custom;Password=secret;'
            expectedCms = 'Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
        }
    ) {
        $realComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $path = Join-Path $script:work ".env.mixed-shape-$([Guid]::NewGuid().ToString('N'))"
        Set-Content -LiteralPath $path -Value ((@(
            'MSSQL_SA_PASSWORD=abcdefgh1!'
            'MSSQL_DB_NAME=edfi_datamanagementservice'
            'DMS_DATASTORE=mssql'
            'DMS_CONFIG_DATASTORE=mssql'
            $_.adminLine
            $_.cmsLine
        )) -join "`n") -NoNewline

        $result = Resolve-DatabaseEngineEnvironmentFile `
            -DatabaseEngine "mssql" `
            -BaseEnvironmentFile $path `
            -DockerComposeRoot $realComposeRoot

        $composed = Resolve-DotenvFileSequentially -Path $result
        [string](Get-SequentialEffectiveValue -Evaluation $composed -Name 'DATABASE_CONNECTION_STRING_ADMIN') |
            Should -BeExactly $_.expectedAdmin
        [string](Get-SequentialEffectiveValue -Evaluation $composed -Name 'DMS_CONFIG_DATABASE_CONNECTION_STRING') |
            Should -BeExactly $_.expectedCms
    }

    It "repairs a fully populated MSSQL file whose declarations are ordered unsafely" {
        # Every overlay key is present and the CMS string's raw text contains 'Server=', so the
        # completeness proof accepts it - but the connection string is declared BEFORE the seam alias it
        # references, so the ORIGINAL file freezes an empty database. Returning it unchanged would hand
        # back an artifact that was never validated; the composition relocates both into safe order.
        $realComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $path = Join-Path $script:work ".env.complete-but-reordered"
        Set-Content -LiteralPath $path -Value (@(
            'MSSQL_SA_PASSWORD=abcdefgh1!'
            'MSSQL_DB_NAME=edfi_datamanagementservice'
            'MSSQL_PORT=1435'
            'MSSQL_PID=Developer'
            'MSSQL_MEMORY_LIMIT_MB=4096'
            'DMS_DATASTORE=mssql'
            'DMS_CONFIG_DATASTORE=mssql'
            'DATABASE_CONNECTION_STRING_ADMIN=Server=dms-mssql;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            'DMS_CONFIG_DATABASE_NAME=${MSSQL_DB_NAME}'
        ) -join "`n") -NoNewline

        # The original really is broken as authored.
        [string](Get-SequentialEffectiveValue `
            -Evaluation (Resolve-DotenvFileSequentially -Path $path) `
            -Name 'DMS_CONFIG_DATABASE_CONNECTION_STRING') |
            Should -BeLike '*Database=;*' -Because "the alias is declared after the string that references it"

        $result = Resolve-DatabaseEngineEnvironmentFile `
            -DatabaseEngine "mssql" `
            -BaseEnvironmentFile $path `
            -DockerComposeRoot $realComposeRoot

        $result | Should -Not -Be $path -Because "an unchanged return would hand back the broken original"
        [string](Get-SequentialEffectiveValue `
            -Evaluation (Resolve-DotenvFileSequentially -Path $result) `
            -Name 'DMS_CONFIG_DATABASE_CONNECTION_STRING') |
            Should -BeExactly 'Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
    }

    It "repairs a complete MSSQL file whose ADMIN string is ordered before its dependency" {
        # The equivalence proof covers every overlay-owned key, not just the CMS one. Here the CMS string
        # renders correctly but DATABASE_CONNECTION_STRING_ADMIN precedes MSSQL_DB_NAME, so the original
        # renders an empty admin database. It still passes the completeness proof - the raw text contains
        # 'Server=' - so a CMS-only equivalence check would hand the broken original back.
        $realComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $path = Join-Path $script:work ".env.admin-reordered"
        Set-Content -LiteralPath $path -Value (@(
            'MSSQL_SA_PASSWORD=abcdefgh1!'
            'DATABASE_CONNECTION_STRING_ADMIN=Server=dms-mssql;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            'MSSQL_DB_NAME=edfi_datamanagementservice'
            'MSSQL_PORT=1435'
            'MSSQL_PID=Developer'
            'MSSQL_MEMORY_LIMIT_MB=4096'
            'DMS_DATASTORE=mssql'
            'DMS_CONFIG_DATASTORE=mssql'
            'DMS_CONFIG_DATABASE_NAME=${MSSQL_DB_NAME}'
            'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
        ) -join "`n") -NoNewline

        # The original is broken only in the ADMIN string; the CMS string is fine.
        $original = Resolve-DotenvFileSequentially -Path $path
        [string](Get-SequentialEffectiveValue -Evaluation $original -Name 'DATABASE_CONNECTION_STRING_ADMIN') |
            Should -BeLike '*Database=;*'
        [string](Get-SequentialEffectiveValue -Evaluation $original -Name 'DMS_CONFIG_DATABASE_CONNECTION_STRING') |
            Should -BeLike '*Database=edfi_datamanagementservice;*'

        $result = Resolve-DatabaseEngineEnvironmentFile `
            -DatabaseEngine "mssql" `
            -BaseEnvironmentFile $path `
            -DockerComposeRoot $realComposeRoot

        $result | Should -Not -Be $path -Because "the original renders an empty admin database"
        [string](Get-SequentialEffectiveValue `
            -Evaluation (Resolve-DotenvFileSequentially -Path $result) `
            -Name 'DATABASE_CONNECTION_STRING_ADMIN') |
            Should -BeExactly 'Server=dms-mssql;Database=edfi_datamanagementservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
    }

    It "still returns a correctly-ordered complete MSSQL file unchanged" {
        # Narrowness for the equivalence check: idempotent recognition must survive it, or every
        # already-composed handoff would start re-deriving.
        $realComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $path = Join-Path $script:work ".env.complete-ordered"
        Set-Content -LiteralPath $path -Value (@(
            'MSSQL_SA_PASSWORD=abcdefgh1!'
            'MSSQL_DB_NAME=edfi_datamanagementservice'
            'MSSQL_PORT=1435'
            'MSSQL_PID=Developer'
            'MSSQL_MEMORY_LIMIT_MB=4096'
            'DMS_DATASTORE=mssql'
            'DMS_CONFIG_DATASTORE=mssql'
            'DATABASE_CONNECTION_STRING_ADMIN=Server=dms-mssql;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            'DMS_CONFIG_DATABASE_NAME=${MSSQL_DB_NAME}'
            'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
        ) -join "`n") -NoNewline

        Resolve-DatabaseEngineEnvironmentFile `
            -DatabaseEngine "mssql" `
            -BaseEnvironmentFile $path `
            -DockerComposeRoot $realComposeRoot |
            Should -Be $path
    }

    It "recognizes an export-spelled, quoted topology marker in the engine gate too" {
        # The marker's third consumer read it through the legacy parser, so the exported and quoted
        # spellings approved elsewhere in this design were not recognized here and the shared-mode
        # invariant ran against a separate-mode file.
        $realComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $path = Join-Path $script:work ".env.gate-marker"
        Set-Content -LiteralPath $path -Value @"
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
export DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE = "true"
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=some_third_db;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;
"@ -NoNewline

        {
            Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine "mssql" `
                -BaseEnvironmentFile $path `
                -DockerComposeRoot $realComposeRoot
        } | Should -Not -Throw -Because "a separate-mode file is outside the shared-mode invariant's scope"
    }

    It "detects a caller value whose own dependency composition relocates, instead of writing an empty database" {
        # CMS_DATABASE_NAME is not an overlay key, so it stays in the base block; MSSQL_DB_NAME is one, so
        # it moves into the overlay block below. The base-block reference therefore resolves to nothing
        # and the composed file would render Database= empty. Validating a separately-modelled
        # environment could not see this; validating the composed lines does, and it fails loudly.
        $partialPath = Join-Path $script:work ".env.relocated-dependency"
        Set-Content -LiteralPath $partialPath -Value @"
DMS_CONFIG_DATASTORE=mssql
MSSQL_DB_NAME=custom_database
CMS_DATABASE_NAME=`${MSSQL_DB_NAME}
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=custom-cms,1444;Database=`${CMS_DATABASE_NAME};User Id=custom;Password=secret;
"@ -NoNewline

        {
            Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine "mssql" `
                -BaseEnvironmentFile $partialPath `
                -DockerComposeRoot $script:composeRoot
        } | Should -Throw "*must include Database or Initial Catalog*"
    }

    It "fails fast when a fully composed MSSQL environment points CMS at a different database" {
        $mismatchedPath = Join-Path $script:work ".env.mismatched-cms-database"
        $mismatchedContent = (Get-Content -LiteralPath (Join-Path $script:composeRoot ".env.mssql") -Raw).
            Replace(
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=legacy_config;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            )
        Set-Content -LiteralPath $mismatchedPath -Value $mismatchedContent -NoNewline

        {
            Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine "mssql" `
                -BaseEnvironmentFile $mismatchedPath `
                -DockerComposeRoot $script:composeRoot
        } | Should -Throw "*shared-database configuration mismatch*legacy_config*edfi_datamanagementservice*DMS-1270*"
    }

    It "resolves caller-authored CMS database references before enforcing the shared database" {
        $mismatchedPath = Join-Path $script:work ".env.mismatched-cms-reference"
        Set-Content -LiteralPath $mismatchedPath -Value @"
DMS_CONFIG_DATASTORE=mssql
MSSQL_DB_NAME=shared_database
CMS_DATABASE_NAME=legacy_config
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=custom-cms,1444;Database=`${CMS_DATABASE_NAME};User Id=custom;Password=secret;
"@ -NoNewline

        {
            Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine "mssql" `
                -BaseEnvironmentFile $mismatchedPath `
                -DockerComposeRoot $script:composeRoot
        } | Should -Throw "*shared-database configuration mismatch*legacy_config*shared_database*DMS-1270*"
    }

    It "fails fast when a caller-authored CMS MSSQL connection omits its database" {
        $missingDatabasePath = Join-Path $script:work ".env.missing-cms-database"
        Set-Content -LiteralPath $missingDatabasePath -Value @"
DMS_CONFIG_DATASTORE=mssql
MSSQL_DB_NAME=shared_database
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=custom-cms,1444;User Id=custom;Password=secret;
"@ -NoNewline

        {
            Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine "mssql" `
                -BaseEnvironmentFile $missingDatabasePath `
                -DockerComposeRoot $script:composeRoot
        } | Should -Throw "*must include Database or Initial Catalog*shared_database*"
    }

    It "allows only an explicit database-only diagnostic caller to bypass the CMS database invariant" {
        $mismatchedPath = Join-Path $script:work ".env.db-only-mismatched-cms"
        $mismatchedContent = (Get-Content -LiteralPath (Join-Path $script:composeRoot ".env.mssql") -Raw).
            Replace(
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;',
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=legacy_config;User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            )
        Set-Content -LiteralPath $mismatchedPath -Value $mismatchedContent -NoNewline

        $result = Resolve-DatabaseEngineEnvironmentFile `
            -DatabaseEngine "mssql" `
            -BaseEnvironmentFile $mismatchedPath `
            -DockerComposeRoot $script:composeRoot `
            -SkipMssqlCmsDatabaseValidation

        $result | Should -Be $mismatchedPath -Because "DbOnly neither starts CMS nor initializes OpenIddict"
    }

    It "does not apply the shared-database invariant to a file declaring the separate topology" {
        # Regression, DMS-1270 Phase 3: the invariant asserts that CMS and OpenIddict both target
        # MSSQL_DB_NAME, which is false by definition once a run opts into a dedicated CMS database.
        # The configure and provision phases own the DMS datastore, take no part in the CMS seam, and
        # so pass no skip switch here - without this they rejected the configuration the preceding
        # start phase had just established, which is how a full
        # `build-dms.ps1 StartEnvironment -SeparateConfigDatabase -DatabaseEngine mssql` run failed
        # after its infrastructure phase had already succeeded.
        $separatePath = Join-Path $script:work ".env.separate-topology"
        Set-Content -LiteralPath $separatePath -Value @"
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=true
DMS_CONFIG_DATABASE_NAME=edfi_configurationservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
"@ -NoNewline

        {
            Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine "mssql" `
                -BaseEnvironmentFile $separatePath `
                -DockerComposeRoot $script:composeRoot
        } | Should -Not -Throw
    }

    It "does not apply the shared-database invariant to a caller-authored file targeting the reserved dedicated CMS database" {
        # Regression, DMS-1270 post-PR review: the documented standalone continuation runs a
        # -SeparateConfigDatabase start phase and then invokes configure-local-data-store.ps1 /
        # provision-dms-schema.ps1 directly against the SAME caller-authored source file. The
        # topology marker exists only in the start script's derived file, so it is absent here and
        # the reserved database name is the only separate-topology signal the file carries. Without
        # honoring it, the terminal guidance those phases print led straight into a rejection of the
        # topology the run had just established. Both provider synonyms are covered, since a
        # connection string may name the database with either key.
        foreach ($databaseKey in @('Database', 'Initial Catalog')) {
            $path = Join-Path $script:work ".env.reserved-$([Guid]::NewGuid().ToString('N'))"
            Set-Content -LiteralPath $path -Value @"
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;$databaseKey=edfi_configurationservice;User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
"@ -NoNewline

            {
                Resolve-DatabaseEngineEnvironmentFile `
                    -DatabaseEngine "mssql" `
                    -BaseEnvironmentFile $path `
                    -DockerComposeRoot $script:composeRoot
            } | Should -Not -Throw
        }
    }

    It "treats a case-variant of the reserved dedicated CMS database name as the same declaration" {
        # SQL Server database names are case-insensitive, so a case-variant names the same physical
        # database and must be recognized as the same separate-topology declaration.
        $path = Join-Path $script:work ".env.reserved-case"
        Set-Content -LiteralPath $path -Value @"
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=EDFI_ConfigurationService;User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
"@ -NoNewline

        {
            Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine "mssql" `
                -BaseEnvironmentFile $path `
                -DockerComposeRoot $script:composeRoot
        } | Should -Not -Throw
    }

    It "still applies the shared-database invariant when neither separate-topology signal is present" {
        # Guards the narrowness of the two exemptions: a mismatch naming some third database is
        # neither the marker nor the reserved dedicated name, so it is still rejected. The absent and
        # explicitly-false marker forms are both covered.
        foreach ($markerLine in @('', 'DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE=false')) {
            $path = Join-Path $script:work ".env.marker-$([Guid]::NewGuid().ToString('N'))"
            Set-Content -LiteralPath $path -Value @"
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
$markerLine
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=legacy_config;User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
"@ -NoNewline

            {
                Resolve-DatabaseEngineEnvironmentFile `
                    -DatabaseEngine "mssql" `
                    -BaseEnvironmentFile $path `
                    -DockerComposeRoot $script:composeRoot
            } | Should -Throw "*shared-database configuration mismatch*"
        }
    }

    # The continuation signal and the shared-mode invariant both resolve the CMS connection string with
    # the shared Compose-equivalent resolver now, not a narrow single-${NAME} grammar. The two grammars
    # disagreed, and the disagreement split a run in half: an operator-shaped database segment passed
    # the start phase's topology validation and then threw "unsupported environment expression" in the
    # manual phases the start script's own guidance points to.
    It "resolves an operator-shaped database segment the way the start path does: <_.label>" -ForEach @(
        @{
            label = 'nested default resolving to the reserved dedicated name is exempted'
            segment = 'Database=${CMS_DB_OVERRIDE_XYZ:-edfi_configurationservice}'
            extra = ''
            shouldThrow = $false
        }
        @{
            label = 'nested ${A:-${B}} resolving to the shared name passes the invariant'
            segment = 'Database=${CMS_DB_OVERRIDE_XYZ:-${MSSQL_DB_NAME}}'
            extra = ''
            shouldThrow = $false
        }
        @{
            label = 'a default that fires to some third database is still rejected'
            segment = 'Database=${CMS_DB_OVERRIDE_XYZ:-legacy_config}'
            extra = ''
            shouldThrow = $true
        }
        @{
            # The alias is declared in the file itself, as a LITERAL. Referencing ${MSSQL_DB_NAME} here
            # would not hold: MSSQL_DB_NAME is an overlay-owned key, so composition moves it below this
            # base-block line, the alias freezes empty, and ':-' fires after all.
            label = 'a default that does NOT fire, because the alias is defined, targets the shared name'
            segment = 'Database=${DMS_CONFIG_DATABASE_NAME:-legacy_config}'
            extra = 'DMS_CONFIG_DATABASE_NAME=edfi_datamanagementservice'
            shouldThrow = $false
        }
    ) {
        $path = Join-Path $script:work ".env.operator-$([Guid]::NewGuid().ToString('N'))"
        Set-Content -LiteralPath $path -Value @"
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
$($_.extra)
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;$($_.segment);User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
"@ -NoNewline

        $resolve = {
            Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine "mssql" `
                -BaseEnvironmentFile $path `
                -DockerComposeRoot $script:composeRoot
        }

        if ($_.shouldThrow) { $resolve | Should -Throw "*shared-database configuration mismatch*" }
        else { $resolve | Should -Not -Throw }
    }

    # The referenced names are resolved through the sequential model of the base file composed with the
    # overlay, not a ReadValuesFromEnvFile map. That map mis-keys `export KEY=...` and collapses
    # duplicates, which reintroduced the start/continuation split one level down: a dependency the start
    # path resolves would resolve EMPTY here and a valid dedicated target would be rejected.
    It "resolves a dependency of the connection string declared as '<_.declaration>'" -ForEach @(
        @{ declaration = 'export CMS_DB_OVERRIDE_XYZ=edfi_configurationservice'; shouldThrow = $false }
        @{ declaration = 'CMS_DB_OVERRIDE_XYZ = edfi_configurationservice'; shouldThrow = $false }
        @{ declaration = 'export CMS_DB_OVERRIDE_XYZ=legacy_config'; shouldThrow = $true }
    ) {
        $path = Join-Path $script:work ".env.dependency-$([Guid]::NewGuid().ToString('N'))"
        Set-Content -LiteralPath $path -Value @"
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
$($_.declaration)
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=`${CMS_DB_OVERRIDE_XYZ};User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
"@ -NoNewline

        $resolve = {
            Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine "mssql" `
                -BaseEnvironmentFile $path `
                -DockerComposeRoot $script:composeRoot
        }

        if ($_.shouldThrow) { $resolve | Should -Throw "*shared-database configuration mismatch*" }
        else { $resolve | Should -Not -Throw }
    }

    # The MSSQL-shape check used to run on the RAW text, so a value that only looks MSSQL-shaped once
    # resolved reached neither the reserved-name signal nor the shared invariant and was silently
    # skipped. Resolution now happens first, so both judge the same resolved text.
    It "judges a <_.label> connection string instead of skipping it" -ForEach @(
        @{
            label = 'outer-quoted'
            lines = @('DMS_CONFIG_DATABASE_CONNECTION_STRING="Server=dms-mssql,1433;Database=legacy_config;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;"')
            shouldThrow = $true
        }
        @{
            label = 'outer-quoted reserved-name'
            lines = @('DMS_CONFIG_DATABASE_CONNECTION_STRING="Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;"')
            shouldThrow = $false
        }
        @{
            label = 'whole-string reference'
            lines = @(
                'WHOLE_CONN=Server=dms-mssql,1433;Database=legacy_config;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=${WHOLE_CONN}'
            )
            shouldThrow = $true
        }
        @{
            label = 'whole-string reference to the reserved name'
            lines = @(
                'WHOLE_CONN=Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
                'DMS_CONFIG_DATABASE_CONNECTION_STRING=${WHOLE_CONN}'
            )
            shouldThrow = $false
        }
    ) {
        $path = Join-Path $script:work ".env.shape-$([Guid]::NewGuid().ToString('N'))"
        Set-Content -LiteralPath $path -Value ((@(
            'DMS_DATASTORE=mssql'
            'DMS_CONFIG_DATASTORE=mssql'
            'MSSQL_SA_PASSWORD=abcdefgh1!'
            'MSSQL_DB_NAME=edfi_datamanagementservice'
        ) + $_.lines) -join "`n") -NoNewline

        $resolve = {
            Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine "mssql" `
                -BaseEnvironmentFile $path `
                -DockerComposeRoot $script:composeRoot
        }

        if ($_.shouldThrow) { $resolve | Should -Throw "*shared-database configuration mismatch*" }
        else { $resolve | Should -Not -Throw }
    }

    It "still resolves a name supplied only by the overlay default" {
        # Precedence is ambient, then the caller's base file, then the overlay - so a name the base file
        # never declares must still resolve from the overlay rather than coming back empty. The base file
        # here deliberately omits MSSQL_DB_NAME, which the overlay supplies, so both the connection
        # string's reference and the invariant's expected name come from the overlay default.
        $path = Join-Path $script:work ".env.overlay-default"
        Set-Content -LiteralPath $path -Value @"
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
MSSQL_SA_PASSWORD=abcdefgh1!
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=`${MSSQL_DB_NAME};User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
"@ -NoNewline

        {
            Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine "mssql" `
                -BaseEnvironmentFile $path `
                -DockerComposeRoot $script:composeRoot
        } | Should -Not -Throw
    }

    It "sees an export-spelled CMS connection string instead of silently skipping the gate" {
        # The legacy parser stored `export KEY=...` under an `export `-prefixed name, so this gate saw
        # no connection string at all and neither signal nor invariant ran for a file that has one.
        $path = Join-Path $script:work ".env.export-mismatch"
        Set-Content -LiteralPath $path -Value @"
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
export DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=legacy_config;User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
"@ -NoNewline

        {
            Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine "mssql" `
                -BaseEnvironmentFile $path `
                -DockerComposeRoot $script:composeRoot
        } | Should -Throw "*shared-database configuration mismatch*"
    }

    It "exempts an export-spelled connection string that targets the reserved dedicated database" {
        $path = Join-Path $script:work ".env.export-reserved"
        Set-Content -LiteralPath $path -Value @"
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
export DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
"@ -NoNewline

        {
            Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine "mssql" `
                -BaseEnvironmentFile $path `
                -DockerComposeRoot $script:composeRoot
        } | Should -Not -Throw
    }

    It "honors an ambient alias override when deciding whether the reserved name is targeted" {
        # Compose gives the shell precedence, so an ambient DMS_CONFIG_DATABASE_NAME genuinely moves
        # where CMS points and must be what this gate judges.
        $path = Join-Path $script:work ".env.ambient-alias"
        Set-Content -LiteralPath $path -Value @"
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=`${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
"@ -NoNewline

        [System.Environment]::SetEnvironmentVariable("DMS_CONFIG_DATABASE_NAME", "edfi_configurationservice")
        try {
            {
                Resolve-DatabaseEngineEnvironmentFile `
                    -DatabaseEngine "mssql" `
                    -BaseEnvironmentFile $path `
                    -DockerComposeRoot $script:composeRoot
            } | Should -Not -Throw
        }
        finally { Remove-Item Env:\DMS_CONFIG_DATABASE_NAME -ErrorAction SilentlyContinue }
    }

    It "does not treat a partially-reserved connection string as a separate-topology declaration" {
        # The reserved-name signal requires EVERY recognized database-name segment to name the
        # dedicated database. A string carrying both the reserved name and some other database is
        # ambiguous, not a declaration, and must still fail the invariant loudly rather than be
        # waved through - SqlClient keeps the LAST synonym, so the effective target is the one this
        # invariant would otherwise have caught.
        $path = Join-Path $script:work ".env.reserved-mixed"
        Set-Content -LiteralPath $path -Value @"
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=edfi_configurationservice;Initial Catalog=legacy_config;User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
"@ -NoNewline

        {
            Resolve-DatabaseEngineEnvironmentFile `
                -DatabaseEngine "mssql" `
                -BaseEnvironmentFile $path `
                -DockerComposeRoot $script:composeRoot
        } | Should -Throw "*shared-database configuration mismatch*"
    }

    It "does not let an ambient topology marker switch the shared-database invariant off" {
        # The marker is read raw from the file, never through Compose precedence, so a stray shell
        # variable cannot disable a safety check. The connection string names a third database, so
        # the reserved-name signal is not in play either.
        $path = Join-Path $script:work ".env.ambient-marker"
        Set-Content -LiteralPath $path -Value @"
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
MSSQL_SA_PASSWORD=abcdefgh1!
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=legacy_config;User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
"@ -NoNewline

        $had = Test-Path Env:\DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE
        $previous = $env:DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE
        try {
            $env:DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE = "true"
            {
                Resolve-DatabaseEngineEnvironmentFile `
                    -DatabaseEngine "mssql" `
                    -BaseEnvironmentFile $path `
                    -DockerComposeRoot $script:composeRoot
            } | Should -Throw "*shared-database configuration mismatch*"
        }
        finally {
            if ($had) { $env:DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE = $previous }
            else { Remove-Item Env:\DMS_TOPOLOGY_SEPARATE_CONFIG_DATABASE -ErrorAction SilentlyContinue }
        }
    }

    It "requires every current overlay key before short-circuiting composition" {
        # The overlay inventory comes from the SHARED assignment model, the same source the production
        # completeness proof now uses. Deriving it here with ReadValuesFromEnvFile instead made the test
        # blind to exactly the divergence that mattered: that parser stores `export KEY=...` under an
        # `export `-prefixed name, so a key declared that way in the overlay was inventoried under the
        # wrong name and its absence from a base file could never be detected.
        $overlayPath = Join-Path $script:composeRoot ".env.mssql"
        $overlayLines = @([System.IO.File]::ReadAllLines($overlayPath))
        $overlayKeys = @(Resolve-DotenvFileSequentially -Line $overlayLines).Declarations |
            ForEach-Object { $_.Key } | Sort-Object -Unique

        foreach ($missingKey in $overlayKeys) {
            $partialPath = Join-Path $script:work ".env.missing-$missingKey"
            $partialLines = @($overlayLines | Where-Object { -not (Test-DotenvAssignmentLine -Line $_ -Key $missingKey) })
            Set-Content -LiteralPath $partialPath -Value (($partialLines -join "`n") + "`n") -NoNewline

            $result = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $partialPath -DockerComposeRoot $script:composeRoot
            $result | Should -Not -Be $partialPath -Because "missing overlay key '$missingKey' must force completion"

            $completed = Resolve-DotenvFileSequentially -Path $result
            foreach ($requiredKey in $overlayKeys) {
                [string](Get-SequentialEffectiveValue -Evaluation $completed -Name $requiredKey) |
                    Should -Not -BeNullOrEmpty -Because "composition must restore required key '$requiredKey'"
            }
        }
    }

    It "recognizes an export-spelled overlay declaration as composed instead of re-deriving" {
        # The overlay inventory used the legacy parser, which mis-keys `export KEY=...`. That key could
        # then never be proven present in a base file, so an already-composed file was re-derived on
        # every re-entry - the derived-of-derived case the idempotency guard exists to prevent.
        # Built from the REAL overlay: which keys the overlay owns decides what composition can relocate,
        # and the minimal stand-in overlay does not declare the seam alias.
        $realComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $altRoot = Join-Path $script:work "export-overlay-root"
        New-Item -ItemType Directory -Path $altRoot -Force | Out-Null
        $overlayText = [System.IO.File]::ReadAllText((Join-Path $realComposeRoot ".env.mssql"))
        [System.IO.File]::WriteAllText(
            (Join-Path $altRoot ".env.mssql"),
            $overlayText.Replace('MSSQL_PORT=', 'export MSSQL_PORT='))

        # A file already carrying every overlay key, in an order that renders correctly.
        $composedPath = Join-Path $script:work ".env.already-composed-export"
        Set-Content -LiteralPath $composedPath -Value (@(
            'MSSQL_SA_PASSWORD=abcdefgh1!'
            'MSSQL_DB_NAME=edfi_datamanagementservice'
            'MSSQL_PORT=1435'
            'MSSQL_PID=Developer'
            'MSSQL_MEMORY_LIMIT_MB=4096'
            'DMS_DATASTORE=mssql'
            'DMS_CONFIG_DATASTORE=mssql'
            'DATABASE_CONNECTION_STRING_ADMIN=Server=dms-mssql;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
            'DMS_CONFIG_DATABASE_NAME=${MSSQL_DB_NAME}'
            'DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
        ) -join "`n") -NoNewline

        Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $composedPath -DockerComposeRoot $altRoot |
            Should -Be $composedPath
    }

    It "fails fast when no .env.mssql overlay exists at the docker-compose root" {
        $emptyComposeRoot = Join-Path $script:work "empty-compose"
        New-Item -ItemType Directory -Path $emptyComposeRoot -Force | Out-Null

        { Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $script:basePath -DockerComposeRoot $emptyComposeRoot } |
            Should -Throw "*no MSSQL engine overlay found*"
    }
}

Describe "The checked-in PostgreSQL-base profile files carry the CMS database topology seam" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    # The three .env.config.* profiles are deliberately out of scope for this seam, as is
    # DATABASE_CONNECTION_STRING_ADMIN in every file.
    It "aliases the seam and routes the CMS connection string through it: <_>" -ForEach @(
        '.env.e2e', '.env.example', '.env.multitenancy', '.env.routeContext.e2e',
        '.env.smoke', '.env.smoke.ds61', '.env.template', '.env.template.ds61'
    ) {
        $path = Join-Path $script:dockerComposeRoot $_
        $values = ReadValuesFromEnvFile $path

        $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be '${POSTGRES_DB_NAME}' -Because "shared mode aliases the seam to the datastore name"
        $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -BeLike '*database=${DMS_CONFIG_DATABASE_NAME};*' -Because "the connection string must follow the seam, not POSTGRES_DB_NAME directly"

        # Docker Compose resolves --env-file references in file order, so the alias defined below its
        # consumer would resolve to empty and silently produce database= with no value.
        $lines = [System.IO.File]::ReadAllLines($path)
        $aliasIndex = [array]::FindIndex($lines, [Predicate[string]] { param($l) $l -eq 'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}' })
        $connectionIndex = [array]::FindIndex($lines, [Predicate[string]] { param($l) $l -like 'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=*' })
        $aliasIndex | Should -BeGreaterOrEqual 0
        $aliasIndex | Should -BeLessThan $connectionIndex -Because "a forward reference resolves to empty under --env-file"
    }

    It "leaves DATABASE_CONNECTION_STRING_ADMIN on the datastore name: <_>" -ForEach @(
        '.env.e2e', '.env.example', '.env.multitenancy', '.env.routeContext.e2e',
        '.env.smoke', '.env.smoke.ds61', '.env.template', '.env.template.ds61'
    ) {
        # The admin connection string belongs to the DMS datastore, not CMS, and is explicitly out of
        # scope for this seam - a stray migration here would repoint DMS itself at the CMS database.
        $values = ReadValuesFromEnvFile (Join-Path $script:dockerComposeRoot $_)
        if ($values.ContainsKey("DATABASE_CONNECTION_STRING_ADMIN")) {
            $values["DATABASE_CONNECTION_STRING_ADMIN"] | Should -Not -BeLike '*DMS_CONFIG_DATABASE_NAME*'
        }
    }
}

Describe "The real .env.mssql overlay (DMS-1238)" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
        $script:overlayValues = ReadValuesFromEnvFile (Join-Path $script:dockerComposeRoot ".env.mssql")
    }

    It "sets DMS_DATASTORE to mssql" {
        $script:overlayValues["DMS_DATASTORE"] | Should -Be "mssql"
    }

    It "carries the MSSQL credentials and port" {
        $script:overlayValues["MSSQL_SA_PASSWORD"] | Should -Not -BeNullOrEmpty
        $script:overlayValues["MSSQL_DB_NAME"] | Should -Not -BeNullOrEmpty
        $script:overlayValues["MSSQL_PORT"] | Should -Not -BeNullOrEmpty
    }

    It "builds a SQL Server admin connection string referencing the MSSQL credentials" {
        $script:overlayValues["DATABASE_CONNECTION_STRING_ADMIN"] | Should -Match "^Server=dms-mssql;.*TrustServerCertificate=true;$"
        $script:overlayValues["DATABASE_CONNECTION_STRING_ADMIN"] | Should -Match '\$\{MSSQL_DB_NAME\}'
        $script:overlayValues["DATABASE_CONNECTION_STRING_ADMIN"] | Should -Match '\$\{MSSQL_SA_PASSWORD\}'
    }

    It "routes the Configuration Service to SQL Server (single-engine stack)" {
        # DMS-1243 delivered the CMS SQL Server backend, so -DatabaseEngine mssql runs the
        # whole stack on SQL Server: no PostgreSQL container exists to fall back to.
        $script:overlayValues["DMS_CONFIG_DATASTORE"] | Should -Be "mssql"
        $script:overlayValues["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
            Should -Match '^Server=dms-mssql,1433;Database=\$\{DMS_CONFIG_DATABASE_NAME\};'
        $script:overlayValues["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
            Should -Match '\$\{MSSQL_SA_PASSWORD\}'
    }

    It "aliases DMS_CONFIG_DATABASE_NAME to MSSQL_DB_NAME (shared mode, the DMS-1270 CMS database topology seam)" {
        $script:overlayValues["DMS_CONFIG_DATABASE_NAME"] | Should -Be '${MSSQL_DB_NAME}'
    }

    It "does not duplicate SCHEMA_PACKAGES or other keys already carried by the base environment file" {
        # The overlay must stay minimal: bulk config (SCHEMA_PACKAGES, DATABASE_TEMPLATE_PACKAGE,
        # version pins) and any key identical to the base env file are inherited, not repeated.
        $script:overlayValues.ContainsKey("SCHEMA_PACKAGES") | Should -BeFalse
        $script:overlayValues.ContainsKey("DATABASE_TEMPLATE_PACKAGE") | Should -BeFalse
        $script:overlayValues.ContainsKey("POSTGRES_PASSWORD") | Should -BeFalse
        $script:overlayValues.ContainsKey("DATABASE_ISOLATION_LEVEL") | Should -BeFalse
    }

    It "does not carry a non-admin DATABASE_CONNECTION_STRING or identity-provider token endpoints" {
        # The non-admin DATABASE_CONNECTION_STRING is dead: local-dms.yml passes only
        # DATABASE_CONNECTION_STRING_ADMIN into the DMS container. The token-endpoint overrides
        # are engine-agnostic (the DMS container's in-network /oauth/token proxy target is the
        # same for both database engines), so they belong in the base environment file, not
        # this overlay.
        $script:overlayValues.ContainsKey("DATABASE_CONNECTION_STRING") | Should -BeFalse
        $script:overlayValues.ContainsKey("KEYCLOAK_OAUTH_TOKEN_ENDPOINT") | Should -BeFalse
        $script:overlayValues.ContainsKey("SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT") | Should -BeFalse
        $script:overlayValues.ContainsKey("OAUTH_TOKEN_ENDPOINT") | Should -BeFalse
    }
}

Describe "The .env.example MSSQL hint block" {
    BeforeAll {
        $dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $script:exampleEnvironment = Get-Content -LiteralPath (Join-Path $dockerComposeRoot ".env.example") -Raw
    }

    It "defines every variable referenced by the commented CMS SQL Server connection string" {
        $script:exampleEnvironment | Should -Match '(?m)^# MSSQL_DB_NAME=edfi_datamanagementservice$'
        $script:exampleEnvironment | Should -Match '(?m)^# MSSQL_SA_PASSWORD=abcdefgh1!$'
        # The commented block carries its own paired alias line, so the connection string references
        # the topology seam rather than MSSQL_DB_NAME directly - matching the active PostgreSQL block
        # above it and the real .env.mssql overlay.
        $script:exampleEnvironment | Should -Match '(?m)^# DMS_CONFIG_DATABASE_NAME=\$\{MSSQL_DB_NAME\}$'
        $script:exampleEnvironment | Should -Match '(?m)^# DMS_CONFIG_DATABASE_CONNECTION_STRING=.*\$\{DMS_CONFIG_DATABASE_NAME\}.*\$\{MSSQL_SA_PASSWORD\}'
    }

    It "keeps the seam alias above the connection string that references it, in both blocks" {
        # Docker Compose resolves --env-file references in file order, so an alias defined below its
        # consumer resolves to empty. Pin the ordering rather than trusting it to survive edits.
        $lines = $script:exampleEnvironment -split "`r?`n"
        $activeAlias = [array]::FindIndex($lines, [Predicate[string]] { param($l) $l -eq 'DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}' })
        $activeConnection = [array]::FindIndex($lines, [Predicate[string]] { param($l) $l -like 'DMS_CONFIG_DATABASE_CONNECTION_STRING=host=*' })
        $commentedAlias = [array]::FindIndex($lines, [Predicate[string]] { param($l) $l -eq '# DMS_CONFIG_DATABASE_NAME=${MSSQL_DB_NAME}' })
        $commentedConnection = [array]::FindIndex($lines, [Predicate[string]] { param($l) $l -like '# DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=*' })

        $activeAlias | Should -BeGreaterOrEqual 0
        $commentedAlias | Should -BeGreaterOrEqual 0
        $activeAlias | Should -BeLessThan $activeConnection
        $commentedAlias | Should -BeLessThan $commentedConnection
    }
}
