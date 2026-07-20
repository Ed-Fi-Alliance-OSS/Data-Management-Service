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
    }

    BeforeEach {
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
        $partialPath = Join-Path $script:work ".env.partial-custom-connections"
        Set-Content -LiteralPath $partialPath -Value @"
DMS_CONFIG_DATASTORE=mssql
MSSQL_DB_NAME=custom_database
CMS_DATABASE_NAME=`${MSSQL_DB_NAME}
DATABASE_CONNECTION_STRING_ADMIN=Data Source=custom-admin,1444;Initial Catalog=master;User Id=custom;Password=secret;
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=custom-cms,1444;Database=`${CMS_DATABASE_NAME};User Id=custom;Password=secret;
"@ -NoNewline

        $result = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $partialPath -DockerComposeRoot $script:composeRoot
        $values = ReadValuesFromEnvFile $result

        $values["DATABASE_CONNECTION_STRING_ADMIN"] | Should -Be "Data Source=custom-admin,1444;Initial Catalog=master;User Id=custom;Password=secret;"
        $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] | Should -Be 'Server=custom-cms,1444;Database=${CMS_DATABASE_NAME};User Id=custom;Password=secret;'
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
        } | Should -Throw "*configuration-database mismatch*legacy_config*edfi_datamanagementservice*SeparateConfigDatabase*"
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
        } | Should -Throw "*configuration-database mismatch*legacy_config*shared_database*SeparateConfigDatabase*"
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

    It "requires every current overlay key before short-circuiting composition" {
        $overlayPath = Join-Path $script:composeRoot ".env.mssql"
        $overlayValues = ReadValuesFromEnvFile $overlayPath
        $overlayLines = @(Get-Content -LiteralPath $overlayPath)

        foreach ($missingKey in $overlayValues.Keys) {
            $partialPath = Join-Path $script:work ".env.missing-$missingKey"
            $partialLines = @($overlayLines | Where-Object { $_ -notmatch "^$([regex]::Escape([string]$missingKey))=" })
            Set-Content -LiteralPath $partialPath -Value (($partialLines -join "`n") + "`n") -NoNewline

            $result = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $partialPath -DockerComposeRoot $script:composeRoot
            $result | Should -Not -Be $partialPath -Because "missing overlay key '$missingKey' must force completion"

            $values = ReadValuesFromEnvFile $result
            foreach ($requiredKey in $overlayValues.Keys) {
                $values[[string]$requiredKey] | Should -Not -BeNullOrEmpty -Because "composition must restore required key '$requiredKey'"
            }
        }
    }

    It "fails fast when no .env.mssql overlay exists at the docker-compose root" {
        $emptyComposeRoot = Join-Path $script:work "empty-compose"
        New-Item -ItemType Directory -Path $emptyComposeRoot -Force | Out-Null

        { Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $script:basePath -DockerComposeRoot $emptyComposeRoot } |
            Should -Throw "*no MSSQL engine overlay found*"
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
        # DMS_CONFIG_DATABASE_NAME is the single configuration-database seam and defaults to the
        # DMS datastore database (MSSQL_DB_NAME) for the shared-database default.
        $script:overlayValues["DMS_CONFIG_DATABASE_NAME"] | Should -Be '${MSSQL_DB_NAME}'
        $script:overlayValues["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
            Should -Match '^Server=dms-mssql,1433;Database=\$\{DMS_CONFIG_DATABASE_NAME\};'
        $script:overlayValues["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
            Should -Match '\$\{MSSQL_SA_PASSWORD\}'
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
        $script:exampleEnvironment | Should -Match '(?m)^# DMS_CONFIG_DATABASE_NAME=\$\{MSSQL_DB_NAME\}$'
        $script:exampleEnvironment | Should -Match '(?m)^# DMS_CONFIG_DATABASE_CONNECTION_STRING=.*\$\{DMS_CONFIG_DATABASE_NAME\}.*\$\{MSSQL_SA_PASSWORD\}'
    }
}

Describe "Resolve-ConfigDatabaseTopologyEnvironmentFile" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-config-db-$([Guid]::NewGuid().ToString('N'))"
        $script:composeRoot = Join-Path $script:work "compose"
        New-Item -ItemType Directory -Path $script:composeRoot -Force | Out-Null
        $script:basePath = Join-Path $script:work ".env.base"

        # The resolver's process-environment guard reads the real process environment by default, so
        # clear the configuration-database variables before every test (saving them for restore) to keep
        # the suite hermetic: a dev shell that exports POSTGRES_DB_NAME/MSSQL_DB_NAME/DMS_CONFIG_* must
        # not spuriously trip the guard in the topology/idempotency/validation Contexts, and the
        # integration Context below sets these deliberately within its own tests. Use Remove-Item to
        # truly delete: [Environment]::SetEnvironmentVariable(name, $null) leaves a BLANK value that the
        # guard would then read as an empty override.
        $script:savedAmbientEnv = @{}
        foreach ($ambientName in 'DMS_CONFIG_DATABASE_NAME', 'DMS_CONFIG_DATABASE_CONNECTION_STRING', 'DMS_CONFIG_DATASTORE', 'POSTGRES_DB_NAME', 'MSSQL_DB_NAME') {
            $script:savedAmbientEnv[$ambientName] = [Environment]::GetEnvironmentVariable($ambientName)
            Remove-Item "Env:$ambientName" -ErrorAction SilentlyContinue
        }
    }

    AfterEach {
        foreach ($ambientName in $script:savedAmbientEnv.Keys) {
            if ($null -ne $script:savedAmbientEnv[$ambientName]) { Set-Item "Env:$ambientName" -Value $script:savedAmbientEnv[$ambientName] }
            else { Remove-Item "Env:$ambientName" -ErrorAction SilentlyContinue }
        }
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Context "shared topology (default)" {
        It "resolves the PostgreSQL datastore database as the configuration database and materializes it concretely" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATASTORE=postgresql
DMS_CONFIG_DATABASE_NAME=`${POSTGRES_DB_NAME}
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=abc;database=`${DMS_CONFIG_DATABASE_NAME};
"@ -NoNewline

            $result = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql"

            $result | Should -Not -Be $script:basePath -Because "a base carrying a reference must be materialized to a concrete name"
            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_datamanagementservice"
        }

        It "resolves the SQL Server datastore database as the configuration database" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=mssql
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATASTORE=mssql
DMS_CONFIG_DATABASE_NAME=`${MSSQL_DB_NAME}
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=`${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=abc;
"@ -NoNewline

            $result = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "mssql"

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_datamanagementservice"
        }

        It "honors a customized datastore database name" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=district_local
DMS_CONFIG_DATABASE_NAME=`${POSTGRES_DB_NAME}
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=`${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;
"@ -NoNewline

            $result = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql"

            (ReadValuesFromEnvFile $result)["DMS_CONFIG_DATABASE_NAME"] | Should -Be "district_local"
        }
    }

    Context "separate topology (-SeparateConfigDatabase)" {
        It "selects the dedicated configuration database on PostgreSQL without changing the datastore" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_NAME=`${POSTGRES_DB_NAME}
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=`${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;
"@ -NoNewline

            $result = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql" -SeparateConfigDatabase

            $values = ReadValuesFromEnvFile $result
            $values["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_configurationservice"
            $values["POSTGRES_DB_NAME"] | Should -Be "edfi_datamanagementservice" -Because "the DMS datastore selection must not change"
        }

        It "selects the dedicated configuration database on SQL Server" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_NAME=`${MSSQL_DB_NAME}
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=`${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=abc;
"@ -NoNewline

            $result = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "mssql" -SeparateConfigDatabase

            (ReadValuesFromEnvFile $result)["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_configurationservice"
        }
    }

    Context "idempotency" {
        It "returns the base file unchanged when it already carries the concrete shared name" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=`${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;
"@ -NoNewline

            $result = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql"

            $result | Should -Be $script:basePath
        }

        It "resolves to the datastore database when the switch is omitted, even if the base carries a separate configuration database name" {
            # Per the topology contract, omitting -SeparateConfigDatabase always resolves the
            # configuration database to the DMS datastore database - a base carrying a separate or
            # custom DMS_CONFIG_DATABASE_NAME is reset, not preserved.
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_NAME=edfi_configurationservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=`${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;
"@ -NoNewline

            $result = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql"

            (ReadValuesFromEnvFile $result)["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_datamanagementservice"
        }

        It "is idempotent in separate mode when the switch is re-supplied (no-op)" {
            # Idempotency in separate mode is achieved by forwarding the switch to every phase: a
            # prior phase materialized the concrete separate name, and re-resolving WITH the switch
            # returns the base unchanged.
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_NAME=edfi_configurationservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=`${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;
"@ -NoNewline

            $result = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql" -SeparateConfigDatabase

            $result | Should -Be $script:basePath
            (ReadValuesFromEnvFile $result)["DMS_CONFIG_DATABASE_NAME"] | Should -Be "edfi_configurationservice"
        }
    }

    Context "connection-string validation" {
        It "accepts a connection string that interpolates the configuration-database seam" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_NAME=`${POSTGRES_DB_NAME}
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=`${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;
"@ -NoNewline

            { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql" } |
                Should -Not -Throw
        }

        It "accepts a caller-authored literal that matches the effective PostgreSQL name" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=edfi_datamanagementservice;username=postgres;password=abc;
"@ -NoNewline

            { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql" } |
                Should -Not -Throw
        }

        It "accepts a caller-authored literal that matches the dedicated database in separate mode" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=mssql
DMS_CONFIG_DATASTORE=mssql
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=custom-cms,1444;Initial Catalog=edfi_configurationservice;User Id=sa;Password=abc;
"@ -NoNewline

            { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "mssql" -SeparateConfigDatabase } |
                Should -Not -Throw
        }

        It "resolves a nested caller reference before comparing to the effective name" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
CMS_DATABASE_NAME=`${POSTGRES_DB_NAME}
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=`${CMS_DATABASE_NAME};username=postgres;password=abc;
"@ -NoNewline

            { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql" } |
                Should -Not -Throw
        }

        It "fails fast when a caller literal conflicts with the effective name" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=legacy_config;username=postgres;password=abc;
"@ -NoNewline

            { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql" } |
                Should -Throw "*Configuration-database mismatch*legacy_config*edfi_datamanagementservice*"
        }

        It "fails fast when a nested caller reference conflicts with the effective name" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
CMS_DATABASE_NAME=legacy_config
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=`${CMS_DATABASE_NAME};username=postgres;password=abc;
"@ -NoNewline

            { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql" } |
                Should -Throw "*Configuration-database mismatch*legacy_config*edfi_datamanagementservice*"
        }

        It "fails fast when a caller connection string omits its database" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=mssql
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=custom-cms,1444;User Id=sa;Password=abc;
"@ -NoNewline

            { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "mssql" } |
                Should -Throw "*must include a database*edfi_datamanagementservice*"
        }

        It "fails fast when a caller connection string cannot be parsed" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=mssql
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql;Database="unterminated
"@ -NoNewline

            { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "mssql" } |
                Should -Throw "*not a valid connection string*"
        }

        It "bypasses connection-string validation for a database-only diagnostic caller" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=legacy_config;username=postgres;password=abc;
"@ -NoNewline

            { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql" -SkipCmsDatabaseValidation } |
                Should -Not -Throw
        }
    }

    Context "process-environment precedence guard (Assert-ConfigDatabaseProcessEnvironmentAgreement)" {
        # Docker Compose gives the caller's shell environment precedence over --env-file for EVERY
        # variable when it interpolates ${...}, so a shell-exported name, connection string, or any
        # variable a connection string references would move the CMS container while setup-openiddict.ps1
        # initializes the file-derived database; in shared topology a shell-exported datastore name would
        # likewise split the datastore from CMS. These exercise the guard directly via an injected
        # process-environment map so the real process environment is never mutated.
        It "does not throw when no process-level override is present" {
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues @{} -ProcessEnvironment @{} } |
                Should -Not -Throw
        }

        It "does not throw when the process-level database name agrees with the effective name" {
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues @{} -ProcessEnvironment @{ DMS_CONFIG_DATABASE_NAME = "EDFI_ConfigurationService" } } |
                Should -Not -Throw -Because "the comparison is case-insensitive"
        }

        It "fails fast when the process-level database name conflicts with the effective name" {
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues @{} -ProcessEnvironment @{ DMS_CONFIG_DATABASE_NAME = "stale_db" } } |
                Should -Throw "*process environment sets DMS_CONFIG_DATABASE_NAME='stale_db'*edfi_configurationservice*precedence over --env-file*"
        }

        It "does not throw when a process-level connection string targets the effective name" {
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues @{} -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "host=dms-postgresql;database=edfi_configurationservice;username=postgres;password=abc;" } } |
                Should -Not -Throw
        }

        It "fails fast when a process-level connection string embeds an unexpanded `${...} database reference (compose uses the shell value verbatim)" {
            # docker-compose substitutes a SHELL-exported connection string as final text and does NOT
            # re-interpolate ${...} inside it (verified with `docker compose config`: the container receives
            # the literal ${DMS_CONFIG_DATABASE_NAME}, not the resolved name). The guard must compare the
            # shell value literally and fail fast, not resolve the token into a false match. An env-file
            # connection string, which compose DOES interpolate, is covered by the mergedValues path.
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues @{} -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;' } } |
                Should -Throw '*${DMS_CONFIG_DATABASE_NAME}*edfi_configurationservice*'
        }

        It "resolves a `${DMS_CONFIG_DATABASE_NAME} reference in an ENV-FILE connection string against the effective name (compose interpolates env-file values)" {
            # The env-file connection string IS interpolated by docker-compose, so with no conflicting
            # process-level override the reference resolves to the effective name and the guard passes.
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;' } -ProcessEnvironment @{} } |
                Should -Not -Throw
        }

        It "fails fast when a process-level connection string targets a different database" {
            # DMS_CONFIG_DATASTORE=mssql so the SQL Server shell connection matches the provider engine; this
            # isolates the database-NAME mismatch (legacy_config vs the effective name) from the engine check.
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues @{ DMS_CONFIG_DATASTORE = "mssql" } -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "Server=dms-mssql,1433;Initial Catalog=legacy_config;User Id=sa;Password=abc;" } } |
                Should -Throw "*resolves to 'legacy_config'*edfi_configurationservice*precedence over --env-file*"
        }

        It "fails fast when a process-level connection string targets no database at all" {
            # A database-less shell connection string would leave CMS on the engine default while
            # setup-openiddict.ps1 initializes the effective database - the same split-brain, so it must
            # be rejected rather than silently pass (the connection string is present, so the bare-name
            # fallback does not apply).
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues @{} -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "host=dms-postgresql;port=5432;username=postgres;password=abc;" } } |
                Should -Throw "*targets no database*edfi_configurationservice*"
        }

        It "fails fast when a shell connection string switches the engine to PostgreSQL on a SQL Server stack" {
            # The CMS provider (AppSettings__Datastore) and the connection string are separate compose
            # variables. On a SQL Server stack a shell-exported PostgreSQL connection targeting the CORRECT
            # database name passes the database-name check, but docker-compose leaves the Configuration
            # Service parsing a PostgreSQL connection while its provider stays SQL Server. The old guard
            # validated only the database name and accepted it - hence the shell connection here targets the
            # effective name so ONLY the engine differs.
            $envValues = @{
                DMS_CONFIG_DATASTORE                  = "mssql"
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'Server=dms-mssql,1433;Database=${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "host=dms-postgresql;database=edfi_configurationservice;username=postgres;password=abc;" } } |
                Should -Throw "*engine mismatch*SQL Server*provider*PostgreSQL connection*"
        }

        It "fails fast when a shell connection string switches the engine to SQL Server on a PostgreSQL stack" {
            # The reverse direction: on a PostgreSQL stack a shell-exported SQL Server connection targeting
            # the CORRECT database name is likewise accepted by a database-name-only check while the provider
            # stays PostgreSQL.
            $envValues = @{
                DMS_CONFIG_DATASTORE                  = "postgresql"
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=abc;" } } |
                Should -Throw "*engine mismatch*PostgreSQL*provider*SQL Server connection*"
        }

        It "does not throw when a shell connection string keeps the SQL Server engine and targets the effective name" {
            # An engine-matching shell override that still targets the effective database is a no-op - the
            # engine check must not false-positive on the correct engine.
            $envValues = @{
                DMS_CONFIG_DATASTORE                  = "mssql"
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'Server=dms-mssql,1433;Database=${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=abc;" } } |
                Should -Not -Throw
        }

        It "does not throw when a shell connection string keeps the PostgreSQL engine and targets the effective name" {
            $envValues = @{
                DMS_CONFIG_DATASTORE                  = "postgresql"
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "host=dms-postgresql;database=edfi_configurationservice;username=postgres;password=abc;" } } |
                Should -Not -Throw
        }

        It "fails fast in the standalone lane when a shell SQL Server connection switches the engine on a PostgreSQL stack" {
            # Wrong-engine validation previously existed only in the standalone materialization path, and only
            # for the SQL-Server-stack direction; the PostgreSQL-stack reverse (a shell SQL Server connection)
            # was unguarded there. The guard now covers that direction. In the real standalone lane the guard
            # runs only in self-contained mode (Keycloak defers to CMS EnsureDatabase, which fails loudly on a
            # wrong-engine connection rather than splitting silently); this exercises the guard directly.
            $envValues = @{
                DMS_CONFIG_DATASTORE                  = "postgresql"
                POSTGRES_DB_NAME                      = "edfi_configurationservice"
                DMS_CONFIG_DATABASE_NAME              = '${POSTGRES_DB_NAME}'
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=abc;" } -ConfigDatabaseNameNotMaterialized } |
                Should -Throw "*engine mismatch*PostgreSQL*provider*SQL Server connection*"
        }

        It "fails fast on a PostgreSQL stack with no env-file connection string when a shell SQL Server connection is exported" {
            # Regression guard for the provider signal: keying the engine check off the env-file connection
            # string would SKIP this case (the env file omits the connection string, so docker-compose builds
            # the PostgreSQL fallback). The provider is still PostgreSQL via DMS_CONFIG_DATASTORE, so a
            # shell-exported SQL Server connection is a genuine mismatch that must be caught.
            $envValues = @{
                DMS_CONFIG_DATASTORE     = "postgresql"
                POSTGRES_DB_NAME         = "edfi_configurationservice"
                DMS_CONFIG_DATABASE_NAME = '${POSTGRES_DB_NAME}'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=abc;" } -ConfigDatabaseNameNotMaterialized } |
                Should -Throw "*engine mismatch*PostgreSQL*provider*SQL Server connection*"
        }

        It "fails fast when a shell DMS_CONFIG_DATASTORE override flips the provider away from the env-file connection engine" {
            # DMS_CONFIG_DATASTORE wins at compose precedence and flips AppSettings__Datastore, so the env-file
            # SQL Server connection would be parsed by the now-PostgreSQL provider - caught because the check
            # keys off the provider resolved at shell-over-file precedence, not the env-file connection engine.
            $envValues = @{
                DMS_CONFIG_DATASTORE                  = "mssql"
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'Server=dms-mssql,1433;Database=${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATASTORE = "postgresql" } } |
                Should -Throw "*engine mismatch*PostgreSQL*provider*SQL Server connection*"
        }

        It "fails fast when the process environment exports an EMPTY connection string on a SQL Server stack (compose selects the PostgreSQL fallback)" {
            # docker-compose's ${DMS_CONFIG_DATABASE_CONNECTION_STRING:-<fallback>} treats an empty shell value
            # as unset and substitutes the hardcoded PostgreSQL fallback - wrong on a SQL Server stack (the
            # env-file carries a valid MSSQL connection). So an empty export must be rejected here, not treated
            # as "absent" (which would validate the env-file value and pass).
            $envValues = @{
                DMS_CONFIG_DATASTORE                  = "mssql"
                MSSQL_DB_NAME                         = "edfi_datamanagementservice"
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'Server=dms-mssql,1433;Database=${MSSQL_DB_NAME};User Id=sa;Password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_datamanagementservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "" } -DatastoreKey "MSSQL_DB_NAME" } |
                Should -Throw "*empty DMS_CONFIG_DATABASE_CONNECTION_STRING on a SQL Server stack*edfi_datamanagementservice*"
        }

        It "does not throw when the process environment exports an EMPTY connection string on a PostgreSQL stack (compose fallback is correct)" {
            # On PostgreSQL the compose-file fallback IS the correct connection (host=dms-postgresql, database
            # resolves to ${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}} = the effective database), so an empty
            # shell export is valid - the guard must NOT reject it (verified against `docker compose config`).
            $envValues = @{
                POSTGRES_DB_NAME                      = "edfi_datamanagementservice"
                DMS_CONFIG_DATABASE_NAME              = '${POSTGRES_DB_NAME}'
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_datamanagementservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "" } -DatastoreKey "POSTGRES_DB_NAME" } |
                Should -Not -Throw
        }

        It "fails fast when an EMPTY connection string forces the fallback and a shell DMS_CONFIG_DATABASE_NAME redirects it" {
            # The anchored finding: an exactly-empty shell connection string makes docker-compose's ':-' ignore
            # the env-file connection and use the compose fallback, whose database is
            # ${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}} at shell precedence. The env-file connection is a
            # LITERAL targeting the effective database (so the old guard validated it and passed), but the
            # fallback resolves through the shell DMS_CONFIG_DATABASE_NAME to rogue_db - CMS connects there while
            # setup-openiddict.ps1 initializes the effective database. The guard must validate the fallback ITSELF.
            $envValues = @{
                POSTGRES_DB_NAME                      = "edfi_configurationservice"
                DMS_CONFIG_DATABASE_CONNECTION_STRING = "host=dms-postgresql;database=edfi_configurationservice;username=postgres;password=abc;"
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = ""; DMS_CONFIG_DATABASE_NAME = "rogue_db" } } |
                Should -Throw "*empty DMS_CONFIG_DATABASE_CONNECTION_STRING*fallback*rogue_db*edfi_configurationservice*"
        }

        It "fails fast for the standalone lane when an EMPTY connection string forces the fallback and a shell POSTGRES_DB_NAME redirects it through the seam" {
            # start-local-config.ps1 passes the RAW env file (DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME} is not
            # materialized), so the compose fallback resolves the CMS database through POSTGRES_DB_NAME at shell
            # precedence. No DatastoreKey is passed, so validating the resolved fallback ITSELF - not the datastore
            # key - is what catches the override.
            $envValues = @{
                POSTGRES_DB_NAME                      = "edfi_configurationservice"
                DMS_CONFIG_DATABASE_NAME              = '${POSTGRES_DB_NAME}'
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = ""; POSTGRES_DB_NAME = "rogue_db" } -ConfigDatabaseNameNotMaterialized } |
                Should -Throw "*empty DMS_CONFIG_DATABASE_CONNECTION_STRING*fallback*rogue_db*edfi_configurationservice*"
        }

        It "does not throw for the standalone lane when an EMPTY connection string forces the fallback but the shell is clean" {
            # The fallback resolves through ${POSTGRES_DB_NAME} to the effective database with no override, so an
            # empty export is a no-op - the fallback validation must not false-positive.
            $envValues = @{
                POSTGRES_DB_NAME                      = "edfi_configurationservice"
                DMS_CONFIG_DATABASE_NAME              = '${POSTGRES_DB_NAME}'
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "" } -ConfigDatabaseNameNotMaterialized } |
                Should -Not -Throw
        }

        It "fails fast when the process environment exports a WHITESPACE-only connection string" {
            # ':-' treats a whitespace value as SET, so compose would hand CMS the malformed value verbatim
            # rather than the env-file connection string - invalid on any engine (empty is handled per-engine).
            $envValues = @{
                MSSQL_DB_NAME                         = "edfi_datamanagementservice"
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'Server=dms-mssql,1433;Database=${MSSQL_DB_NAME};User Id=sa;Password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_datamanagementservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "   " } -DatastoreKey "MSSQL_DB_NAME" } |
                Should -Throw "*whitespace-only DMS_CONFIG_DATABASE_CONNECTION_STRING*edfi_datamanagementservice*"
        }

        It "fails fast when the process environment exports a WHITESPACE-only connection string on a PostgreSQL stack" {
            # Whitespace is rejected regardless of engine (the whitespace branch runs before engine detection);
            # the PostgreSQL companion to the MSSQL whitespace case, mirroring the per-engine EMPTY coverage.
            $envValues = @{
                POSTGRES_DB_NAME                      = "edfi_datamanagementservice"
                DMS_CONFIG_DATABASE_NAME              = '${POSTGRES_DB_NAME}'
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_datamanagementservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "   " } -DatastoreKey "POSTGRES_DB_NAME" } |
                Should -Throw "*whitespace-only DMS_CONFIG_DATABASE_CONNECTION_STRING*edfi_datamanagementservice*"
        }

        It "does not throw when the connection string is absent from the process (uses the env-file value)" {
            # An ABSENT key must remain distinct from a present-but-empty one: docker-compose uses the
            # --env-file value when the shell does not export the variable at all.
            $envValues = @{
                DMS_CONFIG_DATASTORE                  = "mssql"
                MSSQL_DB_NAME                         = "edfi_datamanagementservice"
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'Server=dms-mssql,1433;Database=${MSSQL_DB_NAME};User Id=sa;Password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_datamanagementservice" -EnvValues $envValues -ProcessEnvironment @{} -DatastoreKey "MSSQL_DB_NAME" } |
                Should -Not -Throw
        }

        It "fails fast when a shell-exported variable a FILE connection string references would redirect CMS" {
            # The exact finding: the env-file connection string references a custom variable that also
            # comes from the env file; a shell export of that variable wins at docker-compose precedence
            # and redirects CMS while setup-openiddict.ps1 initializes the file-derived name. The old
            # guard resolved the reference from the file and passed.
            $envValues = @{
                CMS_DB                                = "edfi_configurationservice"
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${CMS_DB};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ CMS_DB = "rogue_db" } } |
                Should -Throw "*resolves to 'rogue_db'*edfi_configurationservice*CMS_DB*"
        }

        It "does not throw when a shell-exported referenced variable agrees with the effective name" {
            $envValues = @{
                CMS_DB                                = "edfi_configurationservice"
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${CMS_DB};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ CMS_DB = "edfi_configurationservice" } } |
                Should -Not -Throw
        }

        It "fails fast when a PROCESS connection string embeds an unexpanded `${...} reference (compose uses the shell value verbatim, not the shell variable)" {
            # The finding's exact case. docker-compose substitutes a SHELL-exported connection string as
            # final text and does NOT re-interpolate ${CUSTOM_CMS_DB} inside it, even though CUSTOM_CMS_DB is
            # itself shell-exported to the effective name. The OLD (incorrect) model resolved the token and
            # PASSED, but the Configuration Service receives the literal ${CUSTOM_CMS_DB} - not a real
            # database - so the guard must fail fast on the literal rather than resolve it into a false match.
            $processEnvironment = @{
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${CUSTOM_CMS_DB};username=postgres;password=abc;'
                CUSTOM_CMS_DB                         = "edfi_configurationservice"
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues @{} -ProcessEnvironment $processEnvironment } |
                Should -Throw '*${CUSTOM_CMS_DB}*edfi_configurationservice*'
        }

        It "fails fast in shared topology when a shell-exported POSTGRES_DB_NAME would split the datastore from CMS" {
            $envValues = @{
                POSTGRES_DB_NAME                      = "edfi_datamanagementservice"
                DMS_CONFIG_DATABASE_NAME              = '${POSTGRES_DB_NAME}'
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_datamanagementservice" -EnvValues $envValues -ProcessEnvironment @{ POSTGRES_DB_NAME = "rogue_db" } -DatastoreKey "POSTGRES_DB_NAME" } |
                Should -Throw "*Database-name mismatch*POSTGRES_DB_NAME*rogue_db*edfi_datamanagementservice*"
        }

        It "fails fast in shared topology when a shell-exported MSSQL_DB_NAME would split the datastore from CMS" {
            $envValues = @{
                DMS_CONFIG_DATASTORE                  = "mssql"
                MSSQL_DB_NAME                         = "edfi_datamanagementservice"
                DMS_CONFIG_DATABASE_NAME              = '${MSSQL_DB_NAME}'
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'Server=dms-mssql,1433;Database=${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_datamanagementservice" -EnvValues $envValues -ProcessEnvironment @{ MSSQL_DB_NAME = "rogue_db" } -DatastoreKey "MSSQL_DB_NAME" } |
                Should -Throw "*Database-name mismatch*MSSQL_DB_NAME*rogue_db*edfi_datamanagementservice*"
        }

        It "does not throw in shared topology when a shell-exported datastore name agrees with the effective name" {
            $envValues = @{
                POSTGRES_DB_NAME                      = "edfi_datamanagementservice"
                DMS_CONFIG_DATABASE_NAME              = '${POSTGRES_DB_NAME}'
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_datamanagementservice" -EnvValues $envValues -ProcessEnvironment @{ POSTGRES_DB_NAME = "edfi_datamanagementservice" } -DatastoreKey "POSTGRES_DB_NAME" } |
                Should -Not -Throw
        }

        It "fails fast in separate topology when a shell datastore override would split the datastore from provisioning" {
            # The config database is the dedicated edfi_configurationservice and is unaffected, but the DMS
            # datastore still resolves through POSTGRES_DB_NAME - so a shell override points the DMS
            # container at 'rogue_db' while configure-local-data-store.ps1 / schema provisioning use the
            # env-file 'edfi_datamanagementservice'. Separate topology passes DatastoreKey too, so this is
            # caught (previously this was wrongly asserted as no-throw).
            $envValues = @{
                POSTGRES_DB_NAME                      = "edfi_datamanagementservice"
                DMS_CONFIG_DATABASE_NAME              = '${POSTGRES_DB_NAME}'
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ POSTGRES_DB_NAME = "rogue_db" } -DatastoreKey "POSTGRES_DB_NAME" } |
                Should -Throw "*Database-name mismatch*POSTGRES_DB_NAME*rogue_db*edfi_datamanagementservice*"
        }

        It "does not flag a datastore override that agrees with the env-file datastore in separate topology" {
            # A shell override equal to the env-file datastore name is a no-op (compose and host-side agree).
            $envValues = @{
                POSTGRES_DB_NAME                      = "edfi_datamanagementservice"
                DMS_CONFIG_DATABASE_NAME              = '${POSTGRES_DB_NAME}'
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ POSTGRES_DB_NAME = "edfi_datamanagementservice" } -DatastoreKey "POSTGRES_DB_NAME" } |
                Should -Not -Throw
        }

        It "fails fast when a shell POSTGRES_DB_NAME override redirects a config-lane connection string" {
            # The standalone Configuration Service lane's connection string routes through POSTGRES_DB_NAME
            # (not the seam) and sets no DMS_CONFIG_DATABASE_NAME. A shell override of POSTGRES_DB_NAME
            # moves the CMS container while setup-openiddict.ps1 initializes the file-derived database.
            $envValues = @{
                POSTGRES_DB_NAME                      = "edfi_configurationservice"
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${POSTGRES_DB_NAME};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ POSTGRES_DB_NAME = "rogue_db" } } |
                Should -Throw "*resolves to 'rogue_db'*edfi_configurationservice*POSTGRES_DB_NAME*"
        }

        It "does not throw when a shell DMS_CONFIG_DATABASE_NAME override is inert (the connection string routes through a different key)" {
            # Same config-lane shape: DMS_CONFIG_DATABASE_NAME is not referenced by the connection string,
            # so a shell export of it does not change the database CMS connects to and must not fail.
            $envValues = @{
                POSTGRES_DB_NAME                      = "edfi_configurationservice"
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${POSTGRES_DB_NAME};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_NAME = "unused_db" } } |
                Should -Not -Throw
        }

        It "fails fast when a shell POSTGRES_DB_NAME override redirects a referenced DMS_CONFIG_DATABASE_NAME fallback (no connection string)" {
            # No env-file connection string; DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}, so the compose
            # fallback resolves through POSTGRES_DB_NAME. Resolve-StandaloneCmsConfigurationDatabaseTarget
            # returns DatastoreKey=POSTGRES_DB_NAME so the guard rejects a shell override of it (it would
            # otherwise escape the guard while OpenIddict initializes the env-file database).
            $envValues = @{
                POSTGRES_DB_NAME         = "edfi_configurationservice"
                DMS_CONFIG_DATABASE_NAME = '${POSTGRES_DB_NAME}'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ POSTGRES_DB_NAME = "rogue_db" } -DatastoreKey "POSTGRES_DB_NAME" } |
                Should -Throw "*Database-name mismatch*POSTGRES_DB_NAME*rogue_db*edfi_configurationservice*"
        }

        It "fails fast when an empty shell DMS_CONFIG_DATABASE_NAME makes compose fall back to POSTGRES_DB_NAME (no connection string)" {
            # ':-' treats an exactly-empty shell value as unset, so compose resolves the CMS database through
            # POSTGRES_DB_NAME while OpenIddict initializes the file's literal DMS_CONFIG_DATABASE_NAME.
            $envValues = @{
                POSTGRES_DB_NAME         = "edfi_datamanagementservice"
                DMS_CONFIG_DATABASE_NAME = "edfi_configurationservice"
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_NAME = "" } } |
                Should -Throw "*empty DMS_CONFIG_DATABASE_NAME*POSTGRES_DB_NAME*edfi_datamanagementservice*edfi_configurationservice*"
        }

        It "does not throw when an empty shell DMS_CONFIG_DATABASE_NAME falls back to the same database (no connection string)" {
            # Empty shell value + fallback POSTGRES_DB_NAME equal to the effective name is a no-op.
            $envValues = @{
                POSTGRES_DB_NAME         = "edfi_configurationservice"
                DMS_CONFIG_DATABASE_NAME = '${POSTGRES_DB_NAME}'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_NAME = "" } -DatastoreKey "POSTGRES_DB_NAME" } |
                Should -Not -Throw
        }

        It "fails fast when a WHITESPACE-only shell DMS_CONFIG_DATABASE_NAME is handed to CMS verbatim (no connection string)" {
            # ':-' substitutes the fallback only for an unset or exactly-empty value; a whitespace-only value
            # is non-empty and is passed to CMS verbatim as its database target. POSTGRES_DB_NAME here EQUALS
            # the effective name, so the old IsNullOrWhiteSpace-as-empty branch would model a fallback and
            # pass while CMS actually receives whitespace - the guard must reject instead.
            $envValues = @{
                POSTGRES_DB_NAME         = "edfi_configurationservice"
                DMS_CONFIG_DATABASE_NAME = '${POSTGRES_DB_NAME}'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_NAME = "   " } } |
                Should -Throw "*whitespace-only value*edfi_configurationservice*verbatim*"
        }

        It "fails fast when an empty shell DMS_CONFIG_DATABASE_NAME has no usable POSTGRES_DB_NAME fallback (no connection string)" {
            # Empty triggers the ':-' fallback to ${POSTGRES_DB_NAME}; with POSTGRES_DB_NAME also absent the
            # fallback resolves to no database at all, leaving CMS without a target while OpenIddict
            # initializes the effective one. The old branch passed silently; the guard must reject.
            $envValues = @{
                DMS_CONFIG_DATABASE_NAME = "edfi_configurationservice"
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_configurationservice" -EnvValues $envValues -ProcessEnvironment @{ DMS_CONFIG_DATABASE_NAME = "" } } |
                Should -Throw "*empty DMS_CONFIG_DATABASE_NAME and no usable POSTGRES_DB_NAME*edfi_configurationservice*"
        }

        It "fails fast for the standalone lane (-ConfigDatabaseNameNotMaterialized) when a shell POSTGRES_DB_NAME override redirects the CMS connection string" {
            # start-local-config.ps1 passes the RAW env file - DMS_CONFIG_DATABASE_NAME=${POSTGRES_DB_NAME}
            # is NOT materialized to a literal - so docker-compose re-resolves the CMS database through
            # POSTGRES_DB_NAME with shell precedence. -ConfigDatabaseNameNotMaterialized skips the pin so the
            # connection-string check models that; the default pin would hide the override.
            $envValues = @{
                POSTGRES_DB_NAME                      = "edfi_datamanagementservice"
                DMS_CONFIG_DATABASE_NAME              = '${POSTGRES_DB_NAME}'
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_datamanagementservice" -EnvValues $envValues -ProcessEnvironment @{ POSTGRES_DB_NAME = "rogue_db" } -ConfigDatabaseNameNotMaterialized } |
                Should -Throw "*resolves to 'rogue_db'*edfi_datamanagementservice*precedence over --env-file*"
        }

        It "does not throw for the standalone lane (-ConfigDatabaseNameNotMaterialized) with a clean shell" {
            $envValues = @{
                POSTGRES_DB_NAME                      = "edfi_datamanagementservice"
                DMS_CONFIG_DATABASE_NAME              = '${POSTGRES_DB_NAME}'
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_datamanagementservice" -EnvValues $envValues -ProcessEnvironment @{} -ConfigDatabaseNameNotMaterialized } |
                Should -Not -Throw
        }

        It "keeps DMS_CONFIG_DATABASE_NAME pinned by default so a shell POSTGRES_DB_NAME override does not redirect the CMS connection string (full-stack lane materializes the literal)" {
            # Regression guard for the pin: the full-stack lane materializes DMS_CONFIG_DATABASE_NAME to a
            # literal in a derived file, so a shell POSTGRES_DB_NAME override does NOT redirect the CMS
            # connection (any datastore split is caught separately via DatastoreKey). Without the switch the
            # pin must stay; removing it would make this throw.
            $envValues = @{
                POSTGRES_DB_NAME                      = "edfi_datamanagementservice"
                DMS_CONFIG_DATABASE_NAME              = '${POSTGRES_DB_NAME}'
                DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;'
            }
            { Assert-ConfigDatabaseProcessEnvironmentAgreement -ExpectedDatabaseName "edfi_datamanagementservice" -EnvValues $envValues -ProcessEnvironment @{ POSTGRES_DB_NAME = "rogue_db" } } |
                Should -Not -Throw
        }
    }

    Context "process-environment precedence in the resolver" {
        # Integration coverage that the resolver actually consults the process environment. Each test
        # sets the real process variables deliberately; the Describe-level BeforeEach/AfterEach saves,
        # clears, and restores them, so no state leaks into the sibling Contexts or the matrix suite.
        It "fails fast when a shell-exported DMS_CONFIG_DATABASE_NAME would redirect the Configuration Service" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_NAME=`${POSTGRES_DB_NAME}
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=`${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;
"@ -NoNewline
            $env:DMS_CONFIG_DATABASE_NAME = "stale_db"

            # The base env carries a connection string that routes through ${DMS_CONFIG_DATABASE_NAME},
            # so the shell override is caught by resolving that string at docker-compose precedence.
            { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql" } |
                Should -Throw "*resolves to 'stale_db'*edfi_datamanagementservice*"
        }

        It "allows a shell-exported DMS_CONFIG_DATABASE_NAME that agrees with the effective name" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_NAME=`${POSTGRES_DB_NAME}
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=`${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;
"@ -NoNewline
            $env:DMS_CONFIG_DATABASE_NAME = "edfi_datamanagementservice"

            { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql" } |
                Should -Not -Throw
        }

        It "skips the process-environment guard for a database-only diagnostic caller" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_NAME=`${POSTGRES_DB_NAME}
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=`${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;
"@ -NoNewline
            $env:DMS_CONFIG_DATABASE_NAME = "stale_db"

            { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql" -SkipCmsDatabaseValidation } |
                Should -Not -Throw
        }

        It "fails fast when a shell-exported POSTGRES_DB_NAME would split the datastore from CMS in shared topology" {
            # The config connection string is shielded by the materialized DMS_CONFIG_DATABASE_NAME, but
            # the datastore connection string still resolves through POSTGRES_DB_NAME, so a shell override
            # would silently move the DMS datastore away from the shared configuration database.
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_NAME=`${POSTGRES_DB_NAME}
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=`${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;
"@ -NoNewline
            $env:POSTGRES_DB_NAME = "rogue_db"

            { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql" } |
                Should -Throw "*Database-name mismatch*POSTGRES_DB_NAME*rogue_db*edfi_datamanagementservice*"
        }

        It "fails fast in SEPARATE topology when a shell-exported POSTGRES_DB_NAME would split the datastore from provisioning" {
            # The resolver passes the datastore key in separate topology too, so a shell override of the
            # datastore name - which redirects the DMS container while configure/provision use the env-file
            # value - is caught even though the configuration database is the dedicated edfi_configurationservice.
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_NAME=`${POSTGRES_DB_NAME}
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=`${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;
"@ -NoNewline
            $env:POSTGRES_DB_NAME = "rogue_db"

            { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql" -SeparateConfigDatabase } |
                Should -Throw "*Database-name mismatch*POSTGRES_DB_NAME*rogue_db*edfi_datamanagementservice*"
        }
    }

    It "fails fast when neither a datastore name nor a configuration-database name can be determined" {
        Set-Content -LiteralPath $script:basePath -Value "DMS_DATASTORE=postgresql`nLOG_LEVEL=Warning`n" -NoNewline

        { Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql" } |
            Should -Throw "*could not determine the effective configuration database name*"
    }
}

Describe "Configuration database topology matrix (per-cell DB targeting on the real default env files)" {
    # The earlier suite resolves the topology onto SYNTHETIC temp env files, which is why it did not
    # catch a lagging real default env file. This suite runs the real topology resolver against the
    # actual tracked env files that the switch-capable entry points consume - .env.e2e (build-dms.ps1
    # StartEnvironment default), .env.example (start-local/published default), and .env.mssql (the
    # SQL Server overlay) - and proves, for every engine x topology cell, that CMS targets the correct
    # configuration database while the DMS datastore selection never changes. It is the durable, CI-
    # runnable guard for the ticket's highest-risk acceptance matrix and for the class of default-env
    # regression that slipped through StartEnvironment.
    BeforeAll {
        $script:realComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:realComposeRoot "env-utility.psm1") -Force
        $script:matrixWork = Join-Path ([System.IO.Path]::GetTempPath()) "dms-topology-matrix-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:matrixWork -Force | Out-Null
    }

    AfterAll {
        if (Test-Path -LiteralPath $script:matrixWork) {
            Remove-Item -LiteralPath $script:matrixWork -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    BeforeEach {
        # The resolver's process-environment guard reads the real process environment, so clear the
        # configuration-database variables (saving them for restore) to keep the per-cell resolutions
        # hermetic against a dev shell that exports them. Use Remove-Item to truly delete:
        # [Environment]::SetEnvironmentVariable(name, $null) leaves a BLANK value the guard would read.
        $script:savedAmbientEnv = @{}
        foreach ($ambientName in 'DMS_CONFIG_DATABASE_NAME', 'DMS_CONFIG_DATABASE_CONNECTION_STRING', 'DMS_CONFIG_DATASTORE', 'POSTGRES_DB_NAME', 'MSSQL_DB_NAME') {
            $script:savedAmbientEnv[$ambientName] = [Environment]::GetEnvironmentVariable($ambientName)
            Remove-Item "Env:$ambientName" -ErrorAction SilentlyContinue
        }
    }

    AfterEach {
        foreach ($ambientName in $script:savedAmbientEnv.Keys) {
            if ($null -ne $script:savedAmbientEnv[$ambientName]) { Set-Item "Env:$ambientName" -Value $script:savedAmbientEnv[$ambientName] }
            else { Remove-Item "Env:$ambientName" -ErrorAction SilentlyContinue }
        }
    }

    It "<EnvFile> wires the CMS connection string to the DMS_CONFIG_DATABASE_NAME seam" -ForEach @(
        @{ EnvFile = ".env.e2e" }
        @{ EnvFile = ".env.example" }
        @{ EnvFile = ".env.mssql" }
    ) {
        # Finding 1's exact class: a switch-capable default env file whose CMS connection string does
        # not interpolate the ${DMS_CONFIG_DATABASE_NAME} seam cannot flip the CMS database when the
        # topology switch is supplied. Every such tracked file must define the seam and route through it.
        $values = ReadValuesFromEnvFile (Join-Path $script:realComposeRoot $EnvFile)
        $values["DMS_CONFIG_DATABASE_NAME"] | Should -Not -BeNullOrEmpty -Because "$EnvFile must define the topology seam"
        $values["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
            Should -Match '\$\{DMS_CONFIG_DATABASE_NAME\}' -Because "$EnvFile must route the CMS database through the seam so -SeparateConfigDatabase flips it"
    }

    It "cell <Engine>/<Topology> via <EnvFile>: CMS targets <ExpectedConfigDb>, DMS datastore stays <Datastore>" -ForEach @(
        @{ EnvFile = ".env.e2e";     Engine = "postgresql"; DatastoreKey = "POSTGRES_DB_NAME"; Datastore = "edfi_datamanagementservice"; Topology = "shared";   Separate = $false; ExpectedConfigDb = "edfi_datamanagementservice" }
        @{ EnvFile = ".env.e2e";     Engine = "postgresql"; DatastoreKey = "POSTGRES_DB_NAME"; Datastore = "edfi_datamanagementservice"; Topology = "separate"; Separate = $true;  ExpectedConfigDb = "edfi_configurationservice" }
        @{ EnvFile = ".env.example"; Engine = "postgresql"; DatastoreKey = "POSTGRES_DB_NAME"; Datastore = "edfi_datamanagementservice"; Topology = "shared";   Separate = $false; ExpectedConfigDb = "edfi_datamanagementservice" }
        @{ EnvFile = ".env.example"; Engine = "postgresql"; DatastoreKey = "POSTGRES_DB_NAME"; Datastore = "edfi_datamanagementservice"; Topology = "separate"; Separate = $true;  ExpectedConfigDb = "edfi_configurationservice" }
        @{ EnvFile = ".env.mssql";   Engine = "mssql";      DatastoreKey = "MSSQL_DB_NAME";    Datastore = "edfi_datamanagementservice"; Topology = "shared";   Separate = $false; ExpectedConfigDb = "edfi_datamanagementservice" }
        @{ EnvFile = ".env.mssql";   Engine = "mssql";      DatastoreKey = "MSSQL_DB_NAME";    Datastore = "edfi_datamanagementservice"; Topology = "separate"; Separate = $true;  ExpectedConfigDb = "edfi_configurationservice" }
    ) {
        $basePath = Join-Path $script:realComposeRoot $EnvFile
        $resolved = Resolve-ConfigDatabaseTopologyEnvironmentFile `
            -BaseEnvironmentFile $basePath `
            -DockerComposeRoot $script:matrixWork `
            -DatabaseEngine $Engine `
            -SeparateConfigDatabase:$Separate
        $resolvedValues = ReadValuesFromEnvFile $resolved

        # CMS connection string must resolve to the expected configuration database for this cell.
        $cmsDbNames = @(Get-CmsConnectionStringDatabaseName -ConnectionString $resolvedValues["DMS_CONFIG_DATABASE_CONNECTION_STRING"] -EnvValues $resolvedValues)
        $cmsDbNames | Should -Contain $ExpectedConfigDb -Because "the $Topology topology must point CMS at $ExpectedConfigDb"

        # The DMS datastore selection is topology-invariant (these files define it as a literal).
        $resolvedValues[$DatastoreKey] | Should -Be $Datastore -Because "the DMS datastore database must never change with the topology switch"
    }
}

Describe "Get-CmsConnectionStringDatabaseName" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    It "reads the database from an unquoted PostgreSQL connection string" {
        Get-CmsConnectionStringDatabaseName -ConnectionString "host=dms-postgresql;database=edfi_configurationservice;username=postgres;password=abc;" -EnvValues @{} |
            Should -Be "edfi_configurationservice"
    }

    It "resolves a `${NAME} reference in an unquoted connection string" {
        $envValues = @{ DMS_CONFIG_DATABASE_NAME = "edfi_configurationservice" }
        Get-CmsConnectionStringDatabaseName -ConnectionString 'host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;' -EnvValues $envValues |
            Should -Be "edfi_configurationservice"
    }

    It "reads the database from a DOUBLE-quoted connection string (the anchored finding: compose strips the quotes)" {
        # ReadValuesFromEnvFile keeps the surrounding quotes, but docker-compose removes them, so a quoted
        # .env value is valid and must not be rejected by host-side validation.
        Get-CmsConnectionStringDatabaseName -ConnectionString '"host=dms-postgresql;database=edfi_datamanagementservice;username=postgres;password=abc;"' -EnvValues @{} |
            Should -Be "edfi_datamanagementservice"
    }

    It "resolves a `${NAME} reference inside a DOUBLE-quoted connection string (compose interpolates double quotes)" {
        $envValues = @{ DMS_CONFIG_DATABASE_NAME = "edfi_configurationservice" }
        Get-CmsConnectionStringDatabaseName -ConnectionString '"host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;"' -EnvValues $envValues |
            Should -Be "edfi_configurationservice"
    }

    It "reads the database from a SINGLE-quoted connection string" {
        Get-CmsConnectionStringDatabaseName -ConnectionString "'host=dms-postgresql;database=edfi_configurationservice;username=postgres;password=abc;'" -EnvValues @{} |
            Should -Be "edfi_configurationservice"
    }

    It "keeps a `${NAME} reference literal inside a SINGLE-quoted connection string (compose does not interpolate single quotes)" {
        # Compose preserves single-quoted values verbatim, so the container receives the literal ${...}; the
        # host must observe the same literal (it then fails the agreement check, matching the container) rather
        # than resolve it and initialize a different database.
        $envValues = @{ DMS_CONFIG_DATABASE_NAME = "edfi_configurationservice" }
        Get-CmsConnectionStringDatabaseName -ConnectionString '''host=dms-postgresql;database=${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;''' -EnvValues $envValues |
            Should -Be '${DMS_CONFIG_DATABASE_NAME}'
    }

    It "reads Initial Catalog from a DOUBLE-quoted SQL Server connection string" {
        Get-CmsConnectionStringDatabaseName -ConnectionString '"Server=dms-mssql,1433;Initial Catalog=edfi_datamanagementservice;User Id=sa;Password=abc;"' -EnvValues @{} |
            Should -Be "edfi_datamanagementservice"
    }

    It "returns an empty array when the connection string carries no database key" {
        @(Get-CmsConnectionStringDatabaseName -ConnectionString "host=dms-postgresql;username=postgres;password=abc;" -EnvValues @{}).Count |
            Should -Be 0
    }
}

Describe "Test-MssqlConnectionStringValue" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    It "detects an unquoted SQL Server connection string" {
        Test-MssqlConnectionStringValue -ConnectionString "Server=dms-mssql,1433;Database=x;User Id=sa;Password=abc;" | Should -BeTrue
    }

    It "does not flag an unquoted PostgreSQL connection string" {
        Test-MssqlConnectionStringValue -ConnectionString "host=dms-postgresql;database=x;username=postgres;password=abc;" | Should -BeFalse
    }

    It "detects a DOUBLE-quoted SQL Server connection string (compose strips the quotes)" {
        Test-MssqlConnectionStringValue -ConnectionString '"Server=dms-mssql,1433;Database=x;User Id=sa;Password=abc;"' | Should -BeTrue
    }

    It "detects a SINGLE-quoted SQL Server connection string" {
        Test-MssqlConnectionStringValue -ConnectionString "'Server=dms-mssql,1433;Database=x;User Id=sa;Password=abc;'" | Should -BeTrue
    }

    It "does not flag a DOUBLE-quoted PostgreSQL connection string" {
        Test-MssqlConnectionStringValue -ConnectionString '"host=dms-postgresql;database=x;username=postgres;password=abc;"' | Should -BeFalse
    }

    It "returns false for a blank value" {
        Test-MssqlConnectionStringValue -ConnectionString "" | Should -BeFalse
    }

    It "does not flag a PostgreSQL connection that uses Server= as the Npgsql alias for Host (the finding)" {
        # Server is a legal Npgsql alias for Host; a PostgreSQL connection using it still carries the
        # definitive PostgreSQL keys (port/username), so it must classify as PostgreSQL - mirroring
        # provision-dms-schema.ps1's Resolve-TargetDialect, not the old Server=-means-SQL-Server regex.
        Test-MssqlConnectionStringValue -ConnectionString "Server=dms-postgresql;Port=5432;Username=postgres;Database=edfi_configurationservice;" | Should -BeFalse
    }

    It "does not match ;Server= embedded inside a quoted connection-string value" {
        # Isolate the quote-aware parsing: this string carries NO definitive PostgreSQL key
        # (host/username/port/sslmode) and no real SQL Server key - the only 'Server=' is inside a quoted
        # value. The key parser (DbConnectionStringBuilder) treats it as part of the password value, not a
        # key, so the result is $false; the old raw regex matched the embedded ';Server=' and wrongly
        # returned $true (so this fails on the pre-fix classifier).
        Test-MssqlConnectionStringValue -ConnectionString 'Database=x;Password="p;Server=y"' | Should -BeFalse
    }

    It "classifies an ambiguous Server=/User Id= connection with no definitive PostgreSQL key as SQL Server" {
        # Server and User Id are each also legal Npgsql aliases, so a string carrying only those (no
        # host/username/port/sslmode) is genuinely indistinguishable and defaults to SQL Server, matching
        # Resolve-TargetDialect's residual behavior. This is the .env.mssql ADMIN connection shape.
        Test-MssqlConnectionStringValue -ConnectionString "Server=dms-mssql;Database=x;User Id=sa;Password=abc;" | Should -BeTrue
    }
}

Describe "Resolve-CmsConfigurationDatabaseName" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    It "returns `$null when no connection string is supplied so the caller uses the environment default" {
        foreach ($value in @($null, "", "   ")) {
            Resolve-CmsConfigurationDatabaseName -ConnectionString $value -EnvValues @{ POSTGRES_DB_NAME = "edfi_configurationservice" } |
                Should -BeNullOrEmpty
        }
    }

    It "resolves the PostgreSQL configuration database, expanding a `${...} reference" {
        $envValues = @{ POSTGRES_DB_NAME = "edfi_configurationservice" }
        Resolve-CmsConfigurationDatabaseName -ConnectionString 'host=dms-postgresql;port=5432;username=postgres;password=abc;database=${POSTGRES_DB_NAME};' -EnvValues $envValues |
            Should -Be "edfi_configurationservice"
    }

    It "resolves the SQL Server configuration database from the Initial Catalog alias" {
        Resolve-CmsConfigurationDatabaseName -ConnectionString "Server=dms-mssql,1433;Initial Catalog=edfi_configurationservice;User Id=sa;Password=abc;" -EnvValues @{} |
            Should -Be "edfi_configurationservice"
    }

    It "fails fast when a supplied connection string targets no database" {
        # Database-less strings previously took the silent fallback; they must now fail before any
        # database is created or initialized.
        { Resolve-CmsConfigurationDatabaseName -ConnectionString "Server=dms-mssql,1433;User Id=sa;Password=abc;" -EnvValues @{} } |
            Should -Throw "*must target a configuration database*"
    }

    It "fails fast when a supplied connection string cannot be parsed" {
        # Unparseable strings previously only produced a warning; they must now fail fast.
        { Resolve-CmsConfigurationDatabaseName -ConnectionString 'Server=dms-mssql;Database="unterminated' -EnvValues @{} } |
            Should -Throw "*not a valid connection string*"
    }

    It "fails fast when a supplied connection string references a blank environment value" {
        { Resolve-CmsConfigurationDatabaseName -ConnectionString 'host=dms-postgresql;database=${POSTGRES_DB_NAME};username=postgres;password=abc;' -EnvValues @{ POSTGRES_DB_NAME = "" } } |
            Should -Throw "*cannot be resolved*"
    }

    It "returns the database when Database and Initial Catalog are both present and agree" {
        # SqlClient treats Database and Initial Catalog as synonyms; duplicated-but-equal is not a conflict.
        Resolve-CmsConfigurationDatabaseName -ConnectionString "Server=dms-mssql,1433;Database=edfi_configurationservice;Initial Catalog=edfi_configurationservice;User Id=sa;Password=abc;" -EnvValues @{} |
            Should -Be "edfi_configurationservice"
    }

    It "returns the last-listed alias, matching SqlClient, when Database follows Initial Catalog" {
        # SqlClient uses the last-listed synonym; the derivation must agree so OpenIddict and CMS target
        # the same database. (This connection string is still flagged as conflicting when the values differ
        # - see the next test - so this case exercises the last-wins tie-break for equal-by-intent inputs.)
        Resolve-CmsConfigurationDatabaseName -ConnectionString "Server=dms-mssql,1433;Initial Catalog=edfi_configurationservice;Database=edfi_configurationservice;User Id=sa;Password=abc;" -EnvValues @{} |
            Should -Be "edfi_configurationservice"
    }

    It "fails fast when Database and Initial Catalog target conflicting databases" {
        # Returning the first alias (as before) would initialize OpenIddict in one database while CMS,
        # using SqlClient's last-listed alias, starts against another. Fail clearly instead.
        { Resolve-CmsConfigurationDatabaseName -ConnectionString "Server=dms-mssql,1433;Database=cfg_first;Initial Catalog=cfg_second;User Id=sa;Password=abc;" -EnvValues @{} } |
            Should -Throw "*conflicting database aliases*cfg_first*cfg_second*"
    }
}

Describe "Resolve-StandaloneCmsConfigurationDatabaseTarget" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    It "uses the connection string database when the env file sets one (no datastore-key guard needed)" {
        $envValues = @{
            POSTGRES_DB_NAME                      = "edfi_configurationservice"
            DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=${POSTGRES_DB_NAME};username=postgres;password=abc;'
        }
        $target = Resolve-StandaloneCmsConfigurationDatabaseTarget -EnvValues $envValues
        $target.DatabaseName | Should -Be "edfi_configurationservice"
        $target.DatastoreKey | Should -BeNullOrEmpty
    }

    It "falls back to DMS_CONFIG_DATABASE_NAME (compose fallback) when no connection string is set" {
        # docker-compose uses ${DMS_CONFIG_DATABASE_NAME:-${POSTGRES_DB_NAME}}; a set DMS_CONFIG_DATABASE_NAME
        # wins, and the guard's bare-name check already covers a shell override of it.
        $envValues = @{
            POSTGRES_DB_NAME         = "edfi_datamanagementservice"
            DMS_CONFIG_DATABASE_NAME = "edfi_configurationservice"
        }
        $target = Resolve-StandaloneCmsConfigurationDatabaseTarget -EnvValues $envValues
        $target.DatabaseName | Should -Be "edfi_configurationservice"
        $target.DatastoreKey | Should -BeNullOrEmpty
    }

    It "resolves a `${...} reference in DMS_CONFIG_DATABASE_NAME and preserves the referenced key" {
        # When DMS_CONFIG_DATABASE_NAME is a whole-value ${POSTGRES_DB_NAME} reference, the compose fallback
        # resolves through POSTGRES_DB_NAME, so the referenced key must be returned as DatastoreKey (a shell
        # POSTGRES_DB_NAME override would otherwise escape the guard).
        $envValues = @{
            POSTGRES_DB_NAME         = "edfi_datamanagementservice"
            DMS_CONFIG_DATABASE_NAME = '${POSTGRES_DB_NAME}'
        }
        $target = Resolve-StandaloneCmsConfigurationDatabaseTarget -EnvValues $envValues
        $target.DatabaseName | Should -Be "edfi_datamanagementservice"
        $target.DatastoreKey | Should -Be "POSTGRES_DB_NAME"
    }

    It "resolves a double-quoted `${...} reference in DMS_CONFIG_DATABASE_NAME and preserves the referenced key" {
        # docker-compose interpolates a double-quoted value, so a quoted whole-value reference must be
        # recognized exactly like the unquoted form: DatabaseName resolves AND the guard key is kept. A
        # whitespace-only trim would leave the quotes, fail the reference match, and silently drop DatastoreKey.
        $envValues = @{
            POSTGRES_DB_NAME         = "edfi_datamanagementservice"
            DMS_CONFIG_DATABASE_NAME = '"${POSTGRES_DB_NAME}"'
        }
        $target = Resolve-StandaloneCmsConfigurationDatabaseTarget -EnvValues $envValues
        $target.DatabaseName | Should -Be "edfi_datamanagementservice"
        $target.DatastoreKey | Should -Be "POSTGRES_DB_NAME"
    }

    It "treats a single-quoted DMS_CONFIG_DATABASE_NAME literally (docker-compose does not interpolate single quotes)" {
        # Docker Compose preserves single-quoted values verbatim - it does NOT expand ${...} inside them
        # (verified with `docker compose config`: '${VAR}' renders as the literal ${VAR}). So the effective
        # name is the literal ${MSSQL_DB_NAME} and there is no referenced key to guard; setup-openiddict's
        # identifier guard then fails fast on the unusable literal, matching compose - which would point CMS
        # at a database literally named ${MSSQL_DB_NAME}. Expanding it here would init a different database.
        $envValues = @{
            MSSQL_DB_NAME            = "edfi_datamanagementservice"
            DMS_CONFIG_DATABASE_NAME = '''${MSSQL_DB_NAME}'''
        }
        $target = Resolve-StandaloneCmsConfigurationDatabaseTarget -EnvValues $envValues
        $target.DatabaseName | Should -Be '${MSSQL_DB_NAME}'
        $target.DatastoreKey | Should -BeNullOrEmpty
    }

    It "returns no datastore key for a literal DMS_CONFIG_DATABASE_NAME (a shell name override is caught by the guard's name check)" {
        $envValues = @{
            POSTGRES_DB_NAME         = "edfi_datamanagementservice"
            DMS_CONFIG_DATABASE_NAME = "edfi_configurationservice"
        }
        (Resolve-StandaloneCmsConfigurationDatabaseTarget -EnvValues $envValues).DatastoreKey | Should -BeNullOrEmpty
    }

    It "falls back to POSTGRES_DB_NAME and guards it when neither a connection string nor DMS_CONFIG_DATABASE_NAME is set" {
        # The compose fallback then resolves through POSTGRES_DB_NAME, so a shell POSTGRES_DB_NAME override
        # would redirect it - DatastoreKey tells the guard to reject that.
        $envValues = @{ POSTGRES_DB_NAME = "edfi_configurationservice" }
        $target = Resolve-StandaloneCmsConfigurationDatabaseTarget -EnvValues $envValues
        $target.DatabaseName | Should -Be "edfi_configurationservice"
        $target.DatastoreKey | Should -Be "POSTGRES_DB_NAME"
    }

    It "propagates the fail-fast when a set connection string targets no database" {
        $envValues = @{
            POSTGRES_DB_NAME                      = "edfi_configurationservice"
            DMS_CONFIG_DATABASE_CONNECTION_STRING = "Server=dms-mssql,1433;User Id=sa;Password=abc;"
        }
        { Resolve-StandaloneCmsConfigurationDatabaseTarget -EnvValues $envValues } |
            Should -Throw "*must target a configuration database*"
    }

    It "fails fast when none of the three sources is present" {
        { Resolve-StandaloneCmsConfigurationDatabaseTarget -EnvValues @{ LOG_LEVEL = "Warning" } } |
            Should -Throw "*cannot be determined*"
    }
}

Describe "Resolve-StandaloneCmsConnectionStringMaterialization" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    It "returns null on a PostgreSQL stack (the compose fallback is already the right engine)" {
        $envValues = @{ DMS_CONFIG_DATASTORE = "postgresql"; POSTGRES_DB_NAME = "edfi_configurationservice" }
        Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName "edfi_configurationservice" -ProcessEnvironment @{} |
            Should -BeNullOrEmpty
    }

    It "returns null when the env file already sets a SQL Server connection string" {
        # .env.config.mssql.e2e (the standard MSSQL config lane) sets its own connection string, so
        # compose never reaches the fallback and nothing is materialized.
        $envValues = @{
            DMS_CONFIG_DATASTORE                  = "mssql"
            MSSQL_SA_PASSWORD                     = "abcdefgh1!"
            DMS_CONFIG_DATABASE_CONNECTION_STRING = "Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;"
        }
        Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName "edfi_configurationservice" -ProcessEnvironment @{} |
            Should -BeNullOrEmpty
    }

    It "returns null when a non-empty connection string is shell-exported (docker-compose uses it verbatim)" {
        $envValues = @{ DMS_CONFIG_DATASTORE = "mssql"; MSSQL_SA_PASSWORD = "abcdefgh1!" }
        $processEnvironment = @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "Server=dms-mssql,1433;Database=edfi_configurationservice;User Id=sa;Password=x;TrustServerCertificate=true;" }
        Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName "edfi_configurationservice" -ProcessEnvironment $processEnvironment |
            Should -BeNullOrEmpty
    }

    It "materializes a SQL Server connection when a SQL Server stack has no connection string" {
        # The finding's scenario: DMS_CONFIG_DATASTORE=mssql selects the SQL Server container, but with no
        # connection string compose would substitute the PostgreSQL fallback. Materialize a connection to
        # the running container (dms-mssql,1433) targeting the effective configuration database. Assert
        # behavior (engine + target db via the SAME parser the guard uses), not an exact string, since the
        # value is built with DbConnectionStringBuilder for safe quoting.
        $envValues = @{ DMS_CONFIG_DATASTORE = "mssql"; MSSQL_SA_PASSWORD = "abcdefgh1!" }
        $conn = Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName "edfi_configurationservice" -ProcessEnvironment @{}
        $conn | Should -Match 'Server=dms-mssql,1433'
        Test-MssqlConnectionStringValue -ConnectionString $conn | Should -BeTrue
        Get-CmsConnectionStringDatabaseName -ConnectionString $conn -EnvValues @{} | Should -Be "edfi_configurationservice"
    }

    It "resolves a `${...} reference in MSSQL_SA_PASSWORD into the materialized connection" {
        $envValues = @{
            DMS_CONFIG_DATASTORE = "mssql"
            MSSQL_SA_PASSWORD    = '${MSSQL_ROOT_PASSWORD}'
            MSSQL_ROOT_PASSWORD  = "s3cr3t-pw"
        }
        $conn = Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName "edfi_configurationservice" -ProcessEnvironment @{}
        $reparsed = [System.Data.Common.DbConnectionStringBuilder]::new()
        $reparsed.set_ConnectionString($conn)
        $reparsed["password"] | Should -Be "s3cr3t-pw"
        Get-CmsConnectionStringDatabaseName -ConnectionString $conn -EnvValues @{} | Should -Be "edfi_configurationservice"
    }

    It "escapes an MSSQL_SA_PASSWORD that contains connection-string metacharacters" {
        # A ';' is legal in a SQL Server password. Raw interpolation would end the segment early, so the
        # guard's reparse (Get-CmsConnectionStringDatabaseName) threw for a valid password. The value must
        # round-trip through that same parser, preserving both the password and the target database.
        $envValues = @{ DMS_CONFIG_DATASTORE = "mssql"; MSSQL_SA_PASSWORD = "Str0ng;Pass1" }
        $conn = Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName "edfi_configurationservice" -ProcessEnvironment @{}
        { Get-CmsConnectionStringDatabaseName -ConnectionString $conn -EnvValues @{} } |
            Should -Not -Throw -Because "the guard reparses the materialized string; raw interpolation of a ';' password made it throw"
        Get-CmsConnectionStringDatabaseName -ConnectionString $conn -EnvValues @{} | Should -Be "edfi_configurationservice"
        $reparsed = [System.Data.Common.DbConnectionStringBuilder]::new()
        $reparsed.set_ConnectionString($conn)
        $reparsed["password"] | Should -Be "Str0ng;Pass1"
    }

    It "throws when a SQL Server stack has a shell-exported empty connection string (compose forces the PostgreSQL fallback)" {
        # ':-' treats an empty value as unset, so compose substitutes the PostgreSQL fallback. That is an
        # explicit operator export; fail fast rather than silently materialize over it.
        $envValues = @{ DMS_CONFIG_DATASTORE = "mssql"; MSSQL_SA_PASSWORD = "abcdefgh1!" }
        { Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName "edfi_configurationservice" -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "" } } |
            Should -Throw "*empty DMS_CONFIG_DATABASE_CONNECTION_STRING*"
    }

    It "throws when a SQL Server stack has no connection string and no MSSQL_SA_PASSWORD to build one" {
        $envValues = @{ DMS_CONFIG_DATASTORE = "mssql" }
        { Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName "edfi_configurationservice" -ProcessEnvironment @{} } |
            Should -Throw "*MSSQL_SA_PASSWORD is not set*"
    }

    It "resolves MSSQL_SA_PASSWORD from a shell export in preference to the env file (matches the container's `${MSSQL_SA_PASSWORD} precedence)" {
        # mssql.yml initializes the container with ${MSSQL_SA_PASSWORD:-abcdefgh1!}, giving the shell value
        # precedence over the env file. If materialization read the password from the env file (A) while the
        # container was initialized from the shell (B), CMS - and self-contained OpenIddict init - would
        # receive a connection that cannot authenticate. The materialized password must be the shell value.
        $envValues = @{ DMS_CONFIG_DATASTORE = "mssql"; MSSQL_SA_PASSWORD = "EnvFilePass-A1!" }
        $processEnvironment = @{ MSSQL_SA_PASSWORD = "ShellPass-B1!" }
        $conn = Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName "edfi_configurationservice" -ProcessEnvironment $processEnvironment
        $reparsed = [System.Data.Common.DbConnectionStringBuilder]::new()
        $reparsed.set_ConnectionString($conn)
        $reparsed["password"] | Should -Be "ShellPass-B1!" -Because "docker-compose initializes the container from the shell value, so the materialized connection must embed the same credential"
    }

    It "resolves MSSQL_SA_PASSWORD from a shell export when the env file does not set it" {
        # A shell-only password still reaches the container via compose; materialization must honor it rather
        # than throwing as though no password were available.
        $envValues = @{ DMS_CONFIG_DATASTORE = "mssql" }
        $processEnvironment = @{ MSSQL_SA_PASSWORD = "ShellOnly-B1!" }
        $conn = Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName "edfi_configurationservice" -ProcessEnvironment $processEnvironment
        $reparsed = [System.Data.Common.DbConnectionStringBuilder]::new()
        $reparsed.set_ConnectionString($conn)
        $reparsed["password"] | Should -Be "ShellOnly-B1!"
    }

    It "throws when MSSQL_SA_PASSWORD is shell-exported empty (compose ':-' forces the default password, which must not be masked)" {
        # An empty shell export makes compose fall back to the mssql.yml default password for the container.
        # Silently materializing the env-file password instead would embed a credential the container never
        # uses; fail fast so the operator resolves the discrepancy.
        $envValues = @{ DMS_CONFIG_DATASTORE = "mssql"; MSSQL_SA_PASSWORD = "EnvFilePass-A1!" }
        { Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName "edfi_configurationservice" -ProcessEnvironment @{ MSSQL_SA_PASSWORD = "" } } |
            Should -Throw "*empty MSSQL_SA_PASSWORD*"
    }

    It "throws when a shell-exported connection string is a PostgreSQL connection on a SQL Server stack (wrong engine)" {
        # Any nonempty connection string suppresses materialization, so a PostgreSQL connection - even one
        # targeting the expected database, which the later name-only guard accepts - would point CMS at
        # PostgreSQL while the stack and self-contained OpenIddict use SQL Server. Reject it before honoring.
        $envValues = @{ DMS_CONFIG_DATASTORE = "mssql"; MSSQL_SA_PASSWORD = "abcdefgh1!" }
        $processEnvironment = @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_configurationservice;" }
        { Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName "edfi_configurationservice" -ProcessEnvironment $processEnvironment } |
            Should -Throw "*non-SQL Server connection*"
    }

    It "throws when the env-file connection string is a PostgreSQL connection on a SQL Server stack (wrong engine)" {
        $envValues = @{
            DMS_CONFIG_DATASTORE                  = "mssql"
            MSSQL_SA_PASSWORD                     = "abcdefgh1!"
            DMS_CONFIG_DATABASE_CONNECTION_STRING = "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_configurationservice;"
        }
        { Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName "edfi_configurationservice" -ProcessEnvironment @{} } |
            Should -Throw "*non-SQL Server connection*"
    }

    It "throws when an existing SQL Server connection targets a different database than expected (right engine, wrong database)" {
        # The engine check passes, so the database guard must catch a right-engine connection that would
        # start CMS against a database self-contained OpenIddict never initialized.
        $envValues = @{ DMS_CONFIG_DATASTORE = "mssql"; MSSQL_SA_PASSWORD = "abcdefgh1!" }
        $processEnvironment = @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "Server=dms-mssql,1433;Database=legacy_config;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;" }
        { Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName "edfi_configurationservice" -ProcessEnvironment $processEnvironment } |
            Should -Throw "*Configuration-database mismatch*"
    }

    It "fails fast when a SHELL connection string's database is an unexpanded `${...} reference (compose uses the shell value verbatim)" {
        # docker-compose hands a SHELL-exported connection string to the Configuration Service as final text
        # and does NOT re-interpolate ${...} inside it, so the container receives the literal ${CUSTOM_CMS_DB}
        # - not a real database. Even though CUSTOM_CMS_DB resolves to the effective name in the env file
        # (the OLD model would resolve it and PASS), the guard must compare the shell value literally and
        # fail fast. This covers Keycloak mode, where the later process-environment guard does not run.
        $envValues = @{ DMS_CONFIG_DATASTORE = "mssql"; MSSQL_SA_PASSWORD = "abcdefgh1!"; CUSTOM_CMS_DB = "edfi_configurationservice" }
        $processEnvironment = @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = 'Server=dms-mssql,1433;Database=${CUSTOM_CMS_DB};User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;' }
        { Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName "edfi_configurationservice" -ProcessEnvironment $processEnvironment } |
            Should -Throw '*${CUSTOM_CMS_DB}*edfi_configurationservice*'
    }

    It "resolves a `${...} reference in an ENV-FILE connection string when honoring it (compose interpolates env-file values)" {
        # An env-file connection string IS interpolated by docker-compose, so its ${CUSTOM_CMS_DB} database
        # reference resolves to the effective name and the connection is honored (no materialization) - the
        # asymmetry with the shell case above.
        $envValues = @{
            DMS_CONFIG_DATASTORE                  = "mssql"
            MSSQL_SA_PASSWORD                     = "abcdefgh1!"
            CUSTOM_CMS_DB                         = "edfi_configurationservice"
            DMS_CONFIG_DATABASE_CONNECTION_STRING = 'Server=dms-mssql,1433;Database=${CUSTOM_CMS_DB};User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;'
        }
        Resolve-StandaloneCmsConnectionStringMaterialization -EnvValues $envValues -ConfigDatabaseName "edfi_configurationservice" -ProcessEnvironment @{} |
            Should -BeNullOrEmpty
    }
}

Describe "Resolve-EffectiveMssqlSaPassword" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    It "prefers a shell-exported password over the env-file value (matches the container's `${MSSQL_SA_PASSWORD} precedence)" {
        # docker-compose initializes the SQL Server container from ${MSSQL_SA_PASSWORD:-...} with shell
        # precedence; both the materialized CMS connection and setup-openiddict.ps1's 'sa' login must use
        # that same effective value or they authenticate with the wrong credential.
        $envValues = @{ MSSQL_SA_PASSWORD = "EnvFilePass-A1!" }
        Resolve-EffectiveMssqlSaPassword -EnvValues $envValues -ProcessEnvironment @{ MSSQL_SA_PASSWORD = "ShellPass-B1!" } |
            Should -Be "ShellPass-B1!"
    }

    It "uses a shell-exported password when the env file does not set one" {
        Resolve-EffectiveMssqlSaPassword -EnvValues @{} -ProcessEnvironment @{ MSSQL_SA_PASSWORD = "ShellOnly-B1!" } |
            Should -Be "ShellOnly-B1!"
    }

    It "falls back to the env-file value when no shell value is set, resolving a `${...} reference" {
        $envValues = @{ MSSQL_SA_PASSWORD = '${MSSQL_ROOT_PASSWORD}'; MSSQL_ROOT_PASSWORD = "s3cr3t-pw" }
        Resolve-EffectiveMssqlSaPassword -EnvValues $envValues -ProcessEnvironment @{} |
            Should -Be "s3cr3t-pw"
    }

    It "resolves an indirect `${...} reference at shell precedence (a shell override of the REFERENCED variable wins, matching the container)" {
        # The finding: env-file MSSQL_SA_PASSWORD=${MSSQL_ROOT_PASSWORD}, env-file MSSQL_ROOT_PASSWORD=EnvPass,
        # shell MSSQL_ROOT_PASSWORD=ShellPass. docker-compose interpolates the env-file MSSQL_SA_PASSWORD
        # against the merged environment (shell wins for every variable), so the container is initialized
        # with ShellPass even though MSSQL_SA_PASSWORD itself is not shell-exported. Resolving the reference
        # against the env file alone returned EnvPass and split the host credential from the container's.
        $envValues = @{ MSSQL_SA_PASSWORD = '${MSSQL_ROOT_PASSWORD}'; MSSQL_ROOT_PASSWORD = "EnvPass-A1!" }
        Resolve-EffectiveMssqlSaPassword -EnvValues $envValues -ProcessEnvironment @{ MSSQL_ROOT_PASSWORD = "ShellPass-B1!" } |
            Should -Be "ShellPass-B1!"
    }

    It "resolves an indirect `${...} reference at shell precedence even with a -DefaultValue (full-stack readiness lanes)" {
        # The full-stack lanes (start-local-dms.ps1 / start-published-dms.ps1) pass -DefaultValue for the
        # readiness poll and setup-openiddict.ps1; the shell override of the referenced variable must still
        # win so those lanes authenticate with the container's actual credential, not the compose default.
        $envValues = @{ MSSQL_SA_PASSWORD = '${MSSQL_ROOT_PASSWORD}'; MSSQL_ROOT_PASSWORD = "EnvPass-A1!" }
        Resolve-EffectiveMssqlSaPassword -EnvValues $envValues -ProcessEnvironment @{ MSSQL_ROOT_PASSWORD = "ShellPass-B1!" } -DefaultValue "abcdefgh1!" |
            Should -Be "ShellPass-B1!"
    }

    It "returns the -DefaultValue when an indirect `${...} reference resolves to an ABSENT variable (compose ':-' default, full-stack lanes)" {
        # The finding: MSSQL_SA_PASSWORD=${MSSQL_ROOT_PASSWORD} with MSSQL_ROOT_PASSWORD absent. docker-compose
        # resolves the reference to empty, so mssql.yml's ${MSSQL_SA_PASSWORD:-abcdefgh1!} starts the container
        # on the default. The helper must apply -DefaultValue too, not abort on the unresolved reference.
        $envValues = @{ MSSQL_SA_PASSWORD = '${MSSQL_ROOT_PASSWORD}' }
        Resolve-EffectiveMssqlSaPassword -EnvValues $envValues -ProcessEnvironment @{} -DefaultValue "abcdefgh1!" |
            Should -Be "abcdefgh1!"
    }

    It "returns the -DefaultValue when an indirect `${...} reference resolves to a BLANK variable" {
        $envValues = @{ MSSQL_SA_PASSWORD = '${MSSQL_ROOT_PASSWORD}'; MSSQL_ROOT_PASSWORD = "   " }
        Resolve-EffectiveMssqlSaPassword -EnvValues $envValues -ProcessEnvironment @{} -DefaultValue "abcdefgh1!" |
            Should -Be "abcdefgh1!"
    }

    It "fails fast (no -DefaultValue) when an indirect `${...} reference resolves to an ABSENT variable (standalone lane)" {
        # The standalone lane passes no -DefaultValue: it must fail fast rather than embed a guessed credential,
        # and with the helper's own diagnostic - not abort inside Resolve-EnvValueReference on the unresolved
        # reference (compose would still start the container on the default).
        $envValues = @{ MSSQL_SA_PASSWORD = '${MSSQL_ROOT_PASSWORD}' }
        { Resolve-EffectiveMssqlSaPassword -EnvValues $envValues -ProcessEnvironment @{} } |
            Should -Throw "*MSSQL_SA_PASSWORD is not set*"
    }

    It "throws when the shell exports an empty password (compose ':-' would force the default, which must not be masked)" {
        { Resolve-EffectiveMssqlSaPassword -EnvValues @{ MSSQL_SA_PASSWORD = "EnvFilePass-A1!" } -ProcessEnvironment @{ MSSQL_SA_PASSWORD = "" } } |
            Should -Throw "*empty MSSQL_SA_PASSWORD*"
    }

    It "throws when no password is set in the shell or the env file" {
        { Resolve-EffectiveMssqlSaPassword -EnvValues @{} -ProcessEnvironment @{} } |
            Should -Throw "*MSSQL_SA_PASSWORD is not set*"
    }

    It "returns the -DefaultValue when neither the shell nor the env file sets a password (models compose ':-' default)" {
        # The full-stack lanes pass mssql.yml's compose default so the host still matches the container when
        # the env file omits the password, instead of the standalone lane's fail-fast.
        Resolve-EffectiveMssqlSaPassword -EnvValues @{} -ProcessEnvironment @{} -DefaultValue "abcdefgh1!" |
            Should -Be "abcdefgh1!"
    }

    It "returns the -DefaultValue when the shell exports an empty password (':-' treats empty as unset)" {
        Resolve-EffectiveMssqlSaPassword -EnvValues @{ MSSQL_SA_PASSWORD = "EnvFilePass-A1!" } -ProcessEnvironment @{ MSSQL_SA_PASSWORD = "" } -DefaultValue "abcdefgh1!" |
            Should -Be "abcdefgh1!"
    }

    It "prefers a set shell or env-file value over the -DefaultValue" {
        Resolve-EffectiveMssqlSaPassword -EnvValues @{ MSSQL_SA_PASSWORD = "EnvFilePass-A1!" } -ProcessEnvironment @{} -DefaultValue "abcdefgh1!" |
            Should -Be "EnvFilePass-A1!"
        Resolve-EffectiveMssqlSaPassword -EnvValues @{ MSSQL_SA_PASSWORD = "EnvFilePass-A1!" } -ProcessEnvironment @{ MSSQL_SA_PASSWORD = "ShellPass-B1!" } -DefaultValue "abcdefgh1!" |
            Should -Be "ShellPass-B1!"
    }
}

Describe "Get-ProcessEnvironmentVariableSnapshot / Restore-ProcessEnvironmentVariable" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
        $script:probeName = "DMS_TEST_PROCESS_ENV_RESTORE_PROBE"
    }

    BeforeEach {
        # The restore tests mutate a REAL process variable to prove the snapshot+finally contract; save and
        # clear the probe before each so the suite stays hermetic and nothing leaks into sibling Describes.
        $script:savedProbe = [Environment]::GetEnvironmentVariable($script:probeName)
        Remove-Item "Env:$($script:probeName)" -ErrorAction SilentlyContinue
    }

    AfterEach {
        if ($null -ne $script:savedProbe) { Set-Item "Env:$($script:probeName)" -Value $script:savedProbe }
        else { Remove-Item "Env:$($script:probeName)" -ErrorAction SilentlyContinue }
    }

    Context "Get-ProcessEnvironmentVariableSnapshot" {
        It "captures a set variable's value from an injected process environment" {
            $snapshot = Get-ProcessEnvironmentVariableSnapshot -Name "DMS_CONFIG_DATABASE_CONNECTION_STRING" -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = "Server=dms-mssql,1433;Database=edfi_configurationservice;" }
            $snapshot.WasSet | Should -BeTrue
            $snapshot.OriginalValue | Should -Be "Server=dms-mssql,1433;Database=edfi_configurationservice;"
        }

        It "reports an absent variable as not set from an injected process environment" {
            $snapshot = Get-ProcessEnvironmentVariableSnapshot -Name "DMS_CONFIG_DATABASE_CONNECTION_STRING" -ProcessEnvironment @{}
            $snapshot.WasSet | Should -BeFalse
            $snapshot.OriginalValue | Should -BeNullOrEmpty
        }

        It "snapshots the live process environment when none is injected" {
            $env:DMS_TEST_PROCESS_ENV_RESTORE_PROBE = "live-value"
            $snapshot = Get-ProcessEnvironmentVariableSnapshot -Name $script:probeName
            $snapshot.WasSet | Should -BeTrue
            $snapshot.OriginalValue | Should -Be "live-value"
        }
    }

    Context "Restore-ProcessEnvironmentVariable" {
        It "removes a variable that was not previously set (the materialized-export leak the finding describes)" {
            # Model start-local-config.ps1's happy path: snapshot BEFORE the export, export a materialized SQL
            # Server connection into the process environment, then restore. A caller who ran no prior export
            # must find the variable truly gone afterward - not left blank, which compose interpolation and
            # the agreement guard would read as an empty override.
            $snapshot = Get-ProcessEnvironmentVariableSnapshot -Name $script:probeName
            $env:DMS_TEST_PROCESS_ENV_RESTORE_PROBE = "Server=dms-mssql,1433;Database=edfi_configurationservice;"

            Restore-ProcessEnvironmentVariable -Snapshot $snapshot

            Test-Path "Env:$($script:probeName)" | Should -BeFalse
        }

        It "restores a caller's prior value verbatim rather than the materialized one" {
            $env:DMS_TEST_PROCESS_ENV_RESTORE_PROBE = "caller-authored-connection"
            $snapshot = Get-ProcessEnvironmentVariableSnapshot -Name $script:probeName
            $env:DMS_TEST_PROCESS_ENV_RESTORE_PROBE = "materialized-connection"

            Restore-ProcessEnvironmentVariable -Snapshot $snapshot

            (Get-Item "Env:$($script:probeName)").Value | Should -Be "caller-authored-connection"
        }

        It "restores on the failure path when invoked from a finally block after a throw" {
            # The finding calls out that exceptions must not leave the materialized value behind. Prove the
            # snapshot+finally contract restores to unset even when the protected body throws.
            $snapshot = Get-ProcessEnvironmentVariableSnapshot -Name $script:probeName
            {
                try {
                    $env:DMS_TEST_PROCESS_ENV_RESTORE_PROBE = "materialized-connection"
                    throw "docker compose failed"
                }
                finally {
                    Restore-ProcessEnvironmentVariable -Snapshot $snapshot
                }
            } | Should -Throw "docker compose failed"

            Test-Path "Env:$($script:probeName)" | Should -BeFalse
        }

        It "leaves a caller's prior value intact on the failure path" {
            $env:DMS_TEST_PROCESS_ENV_RESTORE_PROBE = "caller-authored-connection"
            $snapshot = Get-ProcessEnvironmentVariableSnapshot -Name $script:probeName
            {
                try {
                    $env:DMS_TEST_PROCESS_ENV_RESTORE_PROBE = "materialized-connection"
                    throw "docker compose failed"
                }
                finally {
                    Restore-ProcessEnvironmentVariable -Snapshot $snapshot
                }
            } | Should -Throw

            (Get-Item "Env:$($script:probeName)").Value | Should -Be "caller-authored-connection"
        }
    }
}

Describe "Get-EnvValueReferenceKey" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    It "returns the key for an unquoted whole-value reference" {
        Get-EnvValueReferenceKey -Value '${POSTGRES_DB_NAME}' | Should -Be "POSTGRES_DB_NAME"
    }

    It "returns the key for a double-quoted whole-value reference" {
        # The unquoting must match Resolve-EnvValueReference so the guard key is not dropped for a
        # value docker-compose would still interpolate.
        Get-EnvValueReferenceKey -Value '"${POSTGRES_DB_NAME}"' | Should -Be "POSTGRES_DB_NAME"
    }

    It "returns null for a single-quoted value (docker-compose treats single quotes literally, not as a reference)" {
        # docker compose config proves '${VAR}' is preserved literally, so it must NOT be treated as a
        # resolvable reference - otherwise the host would expand it while CMS receives the literal.
        Get-EnvValueReferenceKey -Value '''${MSSQL_DB_NAME}''' | Should -BeNullOrEmpty
    }

    It "returns the key for a whitespace- and quote-padded whole-value reference" {
        Get-EnvValueReferenceKey -Value '  "${POSTGRES_DB_NAME}"  ' | Should -Be "POSTGRES_DB_NAME"
    }

    It "returns null for a literal value" {
        Get-EnvValueReferenceKey -Value "edfi_configurationservice" | Should -BeNullOrEmpty
    }

    It "returns null for a quoted literal value" {
        Get-EnvValueReferenceKey -Value '"edfi_configurationservice"' | Should -BeNullOrEmpty
    }

    It "returns null for a partial or embedded reference (not a whole-value reference)" {
        Get-EnvValueReferenceKey -Value 'prefix_${POSTGRES_DB_NAME}' | Should -BeNullOrEmpty
    }

    It "returns null for an empty value" {
        Get-EnvValueReferenceKey -Value "" | Should -BeNullOrEmpty
    }
}

Describe "Resolve-EnvValueReference -TreatUnresolvedReferenceAsEmpty" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    It "resolves a reference to an ABSENT variable as empty (models docker-compose bare `${VAR})" {
        Resolve-EnvValueReference -Value '${MSSQL_ROOT_PASSWORD}' -EnvValues @{} -TreatUnresolvedReferenceAsEmpty |
            Should -BeNullOrEmpty
    }

    It "resolves a reference to a BLANK variable as empty" {
        Resolve-EnvValueReference -Value '${MSSQL_ROOT_PASSWORD}' -EnvValues @{ MSSQL_ROOT_PASSWORD = "   " } -TreatUnresolvedReferenceAsEmpty |
            Should -BeNullOrEmpty
    }

    It "resolves a NESTED reference whose target is absent as empty (the switch threads through recursion)" {
        Resolve-EnvValueReference -Value '${A}' -EnvValues @{ A = '${B}' } -TreatUnresolvedReferenceAsEmpty |
            Should -BeNullOrEmpty
    }

    It "still THROWS on an absent reference by default (the switch is opt-in; other callers keep the hard error)" {
        { Resolve-EnvValueReference -Value '${MSSQL_ROOT_PASSWORD}' -EnvValues @{} } |
            Should -Throw "*cannot be resolved*absent*"
    }

    It "still resolves a present reference to its value with the switch set" {
        Resolve-EnvValueReference -Value '${MSSQL_ROOT_PASSWORD}' -EnvValues @{ MSSQL_ROOT_PASSWORD = "real-pw" } -TreatUnresolvedReferenceAsEmpty |
            Should -Be "real-pw"
    }
}

Describe "Resolve-ConnectionStringDialect" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    # One 4-valued classifier replacing the Server=-regex / duplicated marker lists. SQL Server keywords
    # come from the built-in SqlConnectionStringBuilder (so Address/Addr/Network Address/UID/PWD are known);
    # PostgreSQL keywords from the documented table. Shared-only aliases are Ambiguous, not forced to MSSQL.
    It "classifies <Name> as <Expected>" -ForEach @(
        @{ Name = 'canonical PostgreSQL'; Cs = 'host=dms-postgresql;port=5432;username=postgres;password=p;database=d;'; Expected = 'PostgreSql' }
        @{ Name = 'PostgreSQL with NoResetOnClose'; Cs = 'host=dms-postgresql;port=5432;username=postgres;password=p;database=d;NoResetOnClose=true;'; Expected = 'PostgreSql' }
        @{ Name = 'PostgreSQL using the Server alias for Host with port and username'; Cs = 'Server=dms-postgresql;Port=5432;Username=postgres;Database=d;'; Expected = 'PostgreSql' }
        @{ Name = 'PostgreSQL using Ssl Mode'; Cs = 'host=pg;Ssl Mode=Require;database=d;'; Expected = 'PostgreSql' }
        @{ Name = 'shipped SQL Server with only shared keys'; Cs = 'Server=dms-mssql,1433;Database=d;User Id=sa;Password=p;TrustServerCertificate=true;'; Expected = 'Ambiguous' }
        @{ Name = 'SQL Server via Data Source and Initial Catalog and Integrated Security'; Cs = 'Data Source=dms-mssql;Initial Catalog=d;Integrated Security=true;'; Expected = 'SqlServer' }
        @{ Name = 'SQL Server via Network Address and UID and PWD (finding 10a)'; Cs = 'Network Address=dms-mssql;Database=d;UID=sa;PWD=p;'; Expected = 'SqlServer' }
        @{ Name = 'SQL Server via Addr'; Cs = 'Addr=dms-mssql;Initial Catalog=d;'; Expected = 'SqlServer' }
        @{ Name = 'ambiguous - only shared aliases'; Cs = 'Server=host;User Id=u;Database=d;Password=p;'; Expected = 'Ambiguous' }
        @{ Name = 'quoted value containing a semicolon-Server is not a key (finding 9)'; Cs = 'Database=d;Password="p;Server=y"'; Expected = 'Ambiguous' }
        @{ Name = 'contradictory - host and Initial Catalog together'; Cs = 'host=pg;Initial Catalog=d;'; Expected = 'Invalid' }
        @{ Name = 'unparseable'; Cs = 'this is not a connection string'; Expected = 'Invalid' }
        @{ Name = 'empty'; Cs = ''; Expected = 'Invalid' }
        @{ Name = 'whitespace only'; Cs = '   '; Expected = 'Invalid' }
    ) {
        Resolve-ConnectionStringDialect -ConnectionString $Cs | Should -BeExactly $Expected
    }
}

Describe "Test-ConnectionStringMatchesEngine" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    # An ambiguous connection is evaluated against the already-selected provider (accepted), never
    # auto-forced to MSSQL; a definitively-other-engine or invalid connection is rejected.
    It "engine <Engine> vs <Name> yields <Expected>" -ForEach @(
        @{ Engine = 'mssql'; Name = 'ambiguous MSSQL-shaped'; Cs = 'Server=dms-mssql,1433;Database=d;User Id=sa;Password=p;TrustServerCertificate=true;'; Expected = $true }
        @{ Engine = 'postgresql'; Name = 'ambiguous accepted for PostgreSQL (finding 10b)'; Cs = 'Server=dms-postgresql;User Id=postgres;Database=d;'; Expected = $true }
        @{ Engine = 'postgresql'; Name = 'definitive SQL Server rejected'; Cs = 'Network Address=dms-mssql;Database=d;UID=sa;PWD=p;'; Expected = $false }
        @{ Engine = 'mssql'; Name = 'definitive PostgreSQL rejected'; Cs = 'host=pg;port=5432;username=u;database=d;'; Expected = $false }
        @{ Engine = 'postgresql'; Name = 'definitive PostgreSQL accepted'; Cs = 'host=pg;port=5432;username=u;database=d;'; Expected = $true }
        @{ Engine = 'mssql'; Name = 'invalid rejected'; Cs = 'garbage'; Expected = $false }
    ) {
        Test-ConnectionStringMatchesEngine -Engine $Engine -ConnectionString $Cs | Should -Be $Expected
    }
}

Describe "Resolve-ComposeVariable (provenance, shell-over-file, colon-dash semantics)" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    # Exact string equality so any trim/unquote/re-interpolation of a value fails visibly. A direct or
    # reference-terminal shell value is FINAL opaque text; the default applies to unset or exactly-empty
    # values (NOT whitespace).
    It "<Name>" -ForEach @(
        @{ Name = 'direct shell value is verbatim'; Ev = @{}; Pe = @{ X = 'ShellPass' }; Def = $null; WantValue = 'ShellPass'; WantSource = 'Shell' }
        @{ Name = 'shell value keeps literal surrounding quotes'; Ev = @{}; Pe = @{ X = '"quoted"' }; Def = $null; WantValue = '"quoted"'; WantSource = 'Shell' }
        @{ Name = 'shell value keeps a literal token'; Ev = @{}; Pe = @{ X = '${TOKEN}' }; Def = $null; WantValue = '${TOKEN}'; WantSource = 'Shell' }
        @{ Name = 'whitespace-only shell value is verbatim, not defaulted'; Ev = @{}; Pe = @{ X = '   ' }; Def = 'D'; WantValue = '   '; WantSource = 'Shell' }
        @{ Name = 'exactly-empty shell value takes the default'; Ev = @{}; Pe = @{ X = '' }; Def = 'D'; WantValue = 'D'; WantSource = 'ComposeDefault' }
        @{ Name = 'direct env-file value'; Ev = @{ X = 'envval' }; Pe = @{}; Def = $null; WantValue = 'envval'; WantSource = 'EnvFile' }
        @{ Name = 'env-file reference through another env-file value'; Ev = @{ X = '${Y}'; Y = 'yval' }; Pe = @{}; Def = $null; WantValue = 'yval'; WantSource = 'EnvFile' }
        @{ Name = 'env-file reference terminating at a shell value is verbatim (finding 5)'; Ev = @{ X = '${Y}' }; Pe = @{ Y = 'shellY' }; Def = $null; WantValue = 'shellY'; WantSource = 'Shell' }
        @{ Name = 'reference to a shell value keeps spaces, quotes, semicolons and dollar (finding 5)'; Ev = @{ X = '${Y}' }; Pe = @{ Y = '  "s;p$e"  ' }; Def = $null; WantValue = '  "s;p$e"  '; WantSource = 'Shell' }
        @{ Name = 'nested env-file reference'; Ev = @{ X = '${Y}'; Y = '${Z}'; Z = 'zval' }; Pe = @{}; Def = $null; WantValue = 'zval'; WantSource = 'EnvFile' }
        @{ Name = 'nested reference terminating at shell'; Ev = @{ X = '${Y}'; Y = '${Z}' }; Pe = @{ Z = 'shellZ' }; Def = $null; WantValue = 'shellZ'; WantSource = 'Shell' }
        @{ Name = 'reference to an unset variable is empty'; Ev = @{ X = '${Y}' }; Pe = @{}; Def = $null; WantValue = ''; WantSource = 'EnvFile' }
        @{ Name = 'reference to an empty env-file value takes the default'; Ev = @{ X = '${Y}'; Y = '' }; Pe = @{}; Def = 'D'; WantValue = 'D'; WantSource = 'ComposeDefault' }
    ) {
        $result = if ($null -ne $Def) {
            Resolve-ComposeVariable -Name 'X' -EnvValues $Ev -ProcessEnvironment $Pe -Default $Def
        }
        else {
            Resolve-ComposeVariable -Name 'X' -EnvValues $Ev -ProcessEnvironment $Pe
        }
        $result.Value | Should -BeExactly $WantValue
        $result.Source | Should -Be $WantSource
    }

    It "a single-quoted env-file value is literal (no interpolation)" {
        $result = Resolve-ComposeVariable -Name 'X' -EnvValues @{ X = "'`${Y}'" } -ProcessEnvironment @{ Y = 'shouldNOTexpand' }
        $result.Value | Should -BeExactly '${Y}'
        $result.Source | Should -Be 'EnvFile'
    }
}

Describe "Resolve-EnvFileValueWithProvenance" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    }

    It "returns a literal env-file value verbatim with Source EnvFile" {
        $r = Resolve-EnvFileValueWithProvenance -Value 'edfi_configurationservice' -EnvValues @{} -ProcessEnvironment @{}
        $r.Value | Should -BeExactly 'edfi_configurationservice'
        $r.Source | Should -Be 'EnvFile'
    }

    It "returns a shell-terminal reference verbatim with Source Shell" {
        $r = Resolve-EnvFileValueWithProvenance -Value '${CMS_DB}' -EnvValues @{} -ProcessEnvironment @{ CMS_DB = 'rogue_db' }
        $r.Value | Should -BeExactly 'rogue_db'
        $r.Source | Should -Be 'Shell'
    }

    It "rejects a partial or embedded reference" {
        { Resolve-EnvFileValueWithProvenance -Value 'prefix_${Y}' -EnvValues @{ Y = 'y' } -ProcessEnvironment @{} } |
            Should -Throw '*unsupported environment expression*'
    }

    It "throws on a cyclic env-file reference" {
        { Resolve-EnvFileValueWithProvenance -Value '${A}' -EnvValues @{ A = '${B}'; B = '${A}' } -ProcessEnvironment @{} } |
            Should -Throw '*cyclic*'
    }
}

Describe "Resolve-EffectiveConfigRuntimeContract" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force

        # Representative env-file shapes (the seam wiring the tracked env files use).
        $script:PgEnv = @{
            DMS_CONFIG_DATASTORE                  = 'postgresql'
            POSTGRES_DB_NAME                      = 'edfi_datamanagementservice'
            POSTGRES_PASSWORD                     = 'pgpw'
            DMS_CONFIG_DATABASE_NAME              = '${POSTGRES_DB_NAME}'
            DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;port=5432;username=postgres;password=${POSTGRES_PASSWORD};database=${DMS_CONFIG_DATABASE_NAME};'
        }
        $script:MssqlEnv = @{
            DMS_CONFIG_DATASTORE                  = 'mssql'
            MSSQL_DB_NAME                         = 'edfi_datamanagementservice'
            MSSQL_SA_PASSWORD                     = 'abcdefgh1!'
            DMS_CONFIG_DATABASE_NAME              = '${MSSQL_DB_NAME}'
            DMS_CONFIG_DATABASE_CONNECTION_STRING = 'Server=dms-mssql,1433;Database=${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
        }
    }

    Context "clean resolution - every consumer field derives from one contract" {
        It "PostgreSQL shared: engine, provider, connection provenance, and OpenIddict target" {
            $c = Resolve-EffectiveConfigRuntimeContract -EnvValues $script:PgEnv -ProcessEnvironment @{} -InfrastructureEngine postgresql -ConfigDatabaseName 'edfi_datamanagementservice' -ConfigDatabaseNameMaterialized
            $c.InfrastructureEngine | Should -Be 'postgresql'
            $c.CmsProviderEngine | Should -Be 'postgresql'
            $c.CmsConnectionString.Source | Should -Be 'EnvFile'
            $c.CmsDatabaseName | Should -Be 'edfi_datamanagementservice'
            $c.OpenIddict.DbType | Should -Be 'Postgresql'
            $c.OpenIddict.DbUser | Should -Be 'postgres'
            $c.OpenIddict.DbName | Should -Be 'edfi_datamanagementservice'
            $c.OpenIddict.DbPassword | Should -BeNullOrEmpty
        }

        It "SQL Server shared: OpenIddict uses the resolved SA password" {
            $c = Resolve-EffectiveConfigRuntimeContract -EnvValues $script:MssqlEnv -ProcessEnvironment @{} -InfrastructureEngine mssql -ConfigDatabaseName 'edfi_datamanagementservice' -ConfigDatabaseNameMaterialized
            $c.OpenIddict.DbType | Should -Be 'MSSQL'
            $c.OpenIddict.DbUser | Should -Be 'sa'
            $c.OpenIddict.DbPassword | Should -Be 'abcdefgh1!'
            $c.MssqlSaPassword.Value | Should -Be 'abcdefgh1!'
        }

        It "SQL Server standalone with no connection string materializes one targeting the config database" {
            $env = @{ DMS_CONFIG_DATASTORE = 'mssql'; MSSQL_SA_PASSWORD = 'abcdefgh1!'; POSTGRES_DB_NAME = 'edfi_configurationservice' }
            $c = Resolve-EffectiveConfigRuntimeContract -EnvValues $env -ProcessEnvironment @{} -InfrastructureEngine mssql -ConfigDatabaseName 'edfi_configurationservice'
            $c.CmsConnectionString.Source | Should -Be 'Materialized'
            Resolve-ConnectionStringDialect -ConnectionString $c.CmsConnectionString.Value | Should -Be 'Ambiguous'
            Get-CmsConnectionStringDatabaseName -ConnectionString $c.CmsConnectionString.Value -EnvValues @{} | Should -Be 'edfi_configurationservice'
        }

        It "PostgreSQL empty shell connection uses the compose fallback (correct engine)" {
            $c = Resolve-EffectiveConfigRuntimeContract -EnvValues $script:PgEnv -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = '' } -InfrastructureEngine postgresql -ConfigDatabaseName 'edfi_datamanagementservice' -ConfigDatabaseNameMaterialized
            $c.CmsConnectionString.Source | Should -Be 'ComposeDefault'
        }
    }

    Context "engine-agreement invariant (fail-fast, before Docker)" {
        It "rejects a shell DMS_CONFIG_DATASTORE that differs from the selected infrastructure engine" {
            { Resolve-EffectiveConfigRuntimeContract -EnvValues $script:PgEnv -ProcessEnvironment @{ DMS_CONFIG_DATASTORE = 'mssql' } -InfrastructureEngine postgresql -ConfigDatabaseName 'edfi_datamanagementservice' -ConfigDatabaseNameMaterialized } |
                Should -Throw '*resolves*mssql*unset it*'
        }

        It "rejects a paired shell provider + matching connection that both differ from the selected engine" {
            { Resolve-EffectiveConfigRuntimeContract -EnvValues $script:PgEnv -ProcessEnvironment @{ DMS_CONFIG_DATASTORE = 'mssql'; DMS_CONFIG_DATABASE_CONNECTION_STRING = 'Server=dms-mssql,1433;Database=edfi_datamanagementservice;User Id=sa;Password=p;TrustServerCertificate=true;' } -InfrastructureEngine postgresql -ConfigDatabaseName 'edfi_datamanagementservice' -ConfigDatabaseNameMaterialized } |
                Should -Throw '*runtime-contract mismatch*'
        }
    }

    Context "connection validation" {
        It "rejects a shell PostgreSQL connection on a SQL Server stack (wrong engine)" {
            { Resolve-EffectiveConfigRuntimeContract -EnvValues $script:MssqlEnv -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=edfi_datamanagementservice;username=postgres;password=p;' } -InfrastructureEngine mssql -ConfigDatabaseName 'edfi_datamanagementservice' -ConfigDatabaseNameMaterialized } |
                Should -Throw '*PostgreSql connection*mssql*'
        }

        It "rejects an empty shell connection on a SQL Server stack (compose PostgreSQL fallback)" {
            { Resolve-EffectiveConfigRuntimeContract -EnvValues $script:MssqlEnv -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = '' } -InfrastructureEngine mssql -ConfigDatabaseName 'edfi_datamanagementservice' -ConfigDatabaseNameMaterialized } |
                Should -Throw '*exported empty on a SQL Server stack*'
        }

        It "rejects a shell connection that resolves to a different database" {
            { Resolve-EffectiveConfigRuntimeContract -EnvValues $script:PgEnv -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;database=rogue_db;username=postgres;password=p;' } -InfrastructureEngine postgresql -ConfigDatabaseName 'edfi_datamanagementservice' -ConfigDatabaseNameMaterialized } |
                Should -Throw "*targets database 'rogue_db'*edfi_datamanagementservice*"
        }

        It "rejects a database-less shell connection" {
            { Resolve-EffectiveConfigRuntimeContract -EnvValues $script:PgEnv -ProcessEnvironment @{ DMS_CONFIG_DATABASE_CONNECTION_STRING = 'host=dms-postgresql;username=postgres;password=p;' } -InfrastructureEngine postgresql -ConfigDatabaseName 'edfi_datamanagementservice' -ConfigDatabaseNameMaterialized } |
                Should -Throw '*targets no database*'
        }
    }

    Context "datastore-name agreement" {
        It "rejects a shell POSTGRES_DB_NAME override that splits containers from host-side tooling" {
            { Resolve-EffectiveConfigRuntimeContract -EnvValues $script:PgEnv -ProcessEnvironment @{ POSTGRES_DB_NAME = 'rogue' } -InfrastructureEngine postgresql -ConfigDatabaseName 'edfi_datamanagementservice' -ConfigDatabaseNameMaterialized } |
                Should -Throw '*POSTGRES_DB_NAME resolves*rogue*'
        }
    }

    Context "SA password provenance (indirect references)" {
        It "returns an indirect shell-sourced SA password verbatim (spaces, semicolons, dollar preserved)" {
            $env = $script:MssqlEnv.Clone(); $env.MSSQL_SA_PASSWORD = '${MSSQL_ROOT_PASSWORD}'
            $c = Resolve-EffectiveConfigRuntimeContract -EnvValues $env -ProcessEnvironment @{ MSSQL_ROOT_PASSWORD = '  Sh;ll$P  ' } -InfrastructureEngine mssql -ConfigDatabaseName 'edfi_datamanagementservice' -ConfigDatabaseNameMaterialized
            $c.MssqlSaPassword.Value | Should -BeExactly '  Sh;ll$P  '
            $c.MssqlSaPassword.Source | Should -Be 'Shell'
        }

        It "applies the compose default when an indirect SA reference is unset" {
            $env = $script:MssqlEnv.Clone(); $env.MSSQL_SA_PASSWORD = '${MSSQL_ROOT_PASSWORD}'
            $c = Resolve-EffectiveConfigRuntimeContract -EnvValues $env -ProcessEnvironment @{} -InfrastructureEngine mssql -ConfigDatabaseName 'edfi_datamanagementservice' -ConfigDatabaseNameMaterialized -MssqlSaPasswordDefault 'abcdefgh1!'
            $c.MssqlSaPassword.Value | Should -Be 'abcdefgh1!'
            $c.MssqlSaPassword.Source | Should -Be 'ComposeDefault'
        }

        It "fails fast (no default) when an indirect SA reference is unset (standalone lane)" {
            $env = $script:MssqlEnv.Clone(); $env.MSSQL_SA_PASSWORD = '${MSSQL_ROOT_PASSWORD}'
            { Resolve-EffectiveConfigRuntimeContract -EnvValues $env -ProcessEnvironment @{} -InfrastructureEngine mssql -ConfigDatabaseName 'edfi_datamanagementservice' -ConfigDatabaseNameMaterialized } |
                Should -Throw '*MSSQL_SA_PASSWORD resolves to a blank value*'
        }
    }
}
