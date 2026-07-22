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
        $script:overlayValues["DMS_CONFIG_DATABASE_NAME"] | Should -Be '${MSSQL_DB_NAME:-edfi_datamanagementservice}'
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
        $script:exampleEnvironment | Should -Match '(?m)^# DMS_CONFIG_DATABASE_NAME=\$\{MSSQL_DB_NAME:-edfi_datamanagementservice\}$'
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
        It "keeps DMS_CONFIG_DATABASE_NAME as the PostgreSQL datastore-key reference (Compose resolves it, no PowerShell interpolation)" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATASTORE=postgresql
DMS_CONFIG_DATABASE_NAME=`${POSTGRES_DB_NAME}
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;port=5432;username=postgres;password=abc;database=`${DMS_CONFIG_DATABASE_NAME};
"@ -NoNewline

            $result = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql"

            # The base already carries the seam reference, so shared mode is an idempotent no-op; the
            # configuration database follows the datastore because Docker Compose resolves ${POSTGRES_DB_NAME}.
            (ReadValuesFromEnvFile $result)["DMS_CONFIG_DATABASE_NAME"] | Should -BeExactly '${POSTGRES_DB_NAME:-edfi_datamanagementservice}'
        }

        It "keeps DMS_CONFIG_DATABASE_NAME as the SQL Server datastore-key reference" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=mssql
MSSQL_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATASTORE=mssql
DMS_CONFIG_DATABASE_NAME=`${MSSQL_DB_NAME}
DMS_CONFIG_DATABASE_CONNECTION_STRING=Server=dms-mssql,1433;Database=`${DMS_CONFIG_DATABASE_NAME};User Id=sa;Password=abc;
"@ -NoNewline

            $result = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "mssql"

            (ReadValuesFromEnvFile $result)["DMS_CONFIG_DATABASE_NAME"] | Should -BeExactly '${MSSQL_DB_NAME:-edfi_datamanagementservice}'
        }

        It "materializes the datastore-key reference even when the base carries a non-seam DMS_CONFIG_DATABASE_NAME (custom datastore honored via Compose)" {
            # A custom datastore name (district_local) is honored because the materialized reference
            # ${POSTGRES_DB_NAME} lets Docker Compose resolve it - the resolver never bakes a concrete literal.
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=district_local
DMS_CONFIG_DATABASE_NAME=some_stale_literal
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=`${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;
"@ -NoNewline

            $result = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql"

            $result | Should -Not -Be $script:basePath -Because "a base carrying a non-seam value is materialized to the datastore-key reference"
            (ReadValuesFromEnvFile $result)["DMS_CONFIG_DATABASE_NAME"] | Should -BeExactly '${POSTGRES_DB_NAME:-edfi_datamanagementservice}'
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
        It "returns the base file unchanged when it already carries the default-bearing datastore-key reference (shared no-op)" {
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_NAME=`${POSTGRES_DB_NAME:-edfi_datamanagementservice}
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=`${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;
"@ -NoNewline

            $result = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql"

            $result | Should -Be $script:basePath
        }

        It "resets a stale separate configuration-database name to the datastore-key reference when the switch is omitted" {
            # Per the topology contract, omitting -SeparateConfigDatabase always resolves the configuration
            # database to the DMS datastore (the seam reference) - a base carrying a separate or custom
            # DMS_CONFIG_DATABASE_NAME is reset, not preserved.
            Set-Content -LiteralPath $script:basePath -Value @"
DMS_DATASTORE=postgresql
POSTGRES_DB_NAME=edfi_datamanagementservice
DMS_CONFIG_DATABASE_NAME=edfi_configurationservice
DMS_CONFIG_DATABASE_CONNECTION_STRING=host=dms-postgresql;database=`${DMS_CONFIG_DATABASE_NAME};username=postgres;password=abc;
"@ -NoNewline

            $result = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql"

            (ReadValuesFromEnvFile $result)["DMS_CONFIG_DATABASE_NAME"] | Should -BeExactly '${POSTGRES_DB_NAME:-edfi_datamanagementservice}'
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

    It "materializes the datastore-key reference without inspecting the datastore value (blank-datastore validation is the contract's job)" {
        # The resolver no longer parses or validates the datastore value in PowerShell; a base with no
        # POSTGRES_DB_NAME still materializes the ${POSTGRES_DB_NAME} reference, and the runtime contract
        # rejects a blank Compose-resolved anchor.
        Set-Content -LiteralPath $script:basePath -Value "DMS_DATASTORE=postgresql`nLOG_LEVEL=Warning`n" -NoNewline

        $result = Resolve-ConfigDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -DatabaseEngine "postgresql"
        (ReadValuesFromEnvFile $result)["DMS_CONFIG_DATABASE_NAME"] | Should -BeExactly '${POSTGRES_DB_NAME:-edfi_datamanagementservice}'
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

        # The topology resolver materializes DMS_CONFIG_DATABASE_NAME to the datastore-key REFERENCE in
        # shared mode (Docker Compose resolves ${<key>} to the datastore) and to the dedicated literal in
        # separate mode. Because the sibling test proves every tracked env file routes the CMS connection
        # string through the ${DMS_CONFIG_DATABASE_NAME} seam, this hermetically proves CMS targets
        # $ExpectedConfigDb when Compose interpolates it (the end-to-end compose oracle confirms the same in
        # the docker-gated RuntimeConfigContract suite).
        $expectedSeam = if ($Separate) { $ExpectedConfigDb } else { "`${${DatastoreKey}:-edfi_datamanagementservice}" }
        $resolvedValues["DMS_CONFIG_DATABASE_NAME"] | Should -BeExactly $expectedSeam -Because "the $Topology topology materializes $(if ($Separate) { "the dedicated literal $ExpectedConfigDb" } else { "the default-bearing datastore-key reference (Compose resolves it to $ExpectedConfigDb)" })"

        # The DMS datastore selection is topology-invariant (these files define it as a literal).
        $resolvedValues[$DatastoreKey] | Should -Be $Datastore -Because "the DMS datastore database must never change with the topology switch"
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
