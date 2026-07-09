# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# DMS-1238: -DatabaseEngine mssql composes the .env.mssql overlay onto the base environment file
# so every phase (configure, provision, and the DMS container itself) agrees on DMS_DATASTORE
# and the SQL Server connection strings, instead of relying on a standalone -EnvironmentFile.

param()

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
DMS_DATASTORE=mssql
DATABASE_CONNECTION_STRING=Server=dms-mssql;Database=`${MSSQL_DB_NAME};User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
DATABASE_CONNECTION_STRING_ADMIN=Server=dms-mssql;Database=`${MSSQL_DB_NAME};User Id=sa;Password=`${MSSQL_SA_PASSWORD};TrustServerCertificate=true;
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
        $values["DATABASE_CONNECTION_STRING"] | Should -Be 'Server=dms-mssql;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'
        $values["DATABASE_CONNECTION_STRING_ADMIN"] | Should -Be 'Server=dms-mssql;Database=${MSSQL_DB_NAME};User Id=sa;Password=${MSSQL_SA_PASSWORD};TrustServerCertificate=true;'

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

    It "is idempotent: returns the base file unchanged when DMS_DATASTORE=mssql is already composed" {
        # Mirrors an already-composed derived file (e.g. one the bootstrap wrapper produced and
        # forwarded to start-local-dms.ps1 via -EnvironmentFile): composing again must not
        # produce a derived-of-derived file.
        $alreadyComposedPath = Join-Path $script:work ".env.derived"
        Set-Content -LiteralPath $alreadyComposedPath -Value "DMS_DATASTORE=mssql`nMSSQL_SA_PASSWORD=Abcdefgh1!`n" -NoNewline

        $result = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine "mssql" -BaseEnvironmentFile $alreadyComposedPath -DockerComposeRoot $script:composeRoot

        $result | Should -Be $alreadyComposedPath
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
