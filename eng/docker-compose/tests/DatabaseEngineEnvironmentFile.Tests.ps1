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
        $script:overlayValues["DMS_CONFIG_DATABASE_CONNECTION_STRING"] |
            Should -Match '^Server=dms-mssql,1433;Database=\$\{MSSQL_DB_NAME\};'
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
        $script:exampleEnvironment | Should -Match '(?m)^# DMS_CONFIG_DATABASE_CONNECTION_STRING=.*\$\{MSSQL_DB_NAME\}.*\$\{MSSQL_SA_PASSWORD\}'
    }
}
