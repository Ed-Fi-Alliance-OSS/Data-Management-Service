# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Describe "New-DataStandardDerivedEnvFile" {
    BeforeAll {
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force

        function script:New-TempDir {
            $path = Join-Path ([System.IO.Path]::GetTempPath()) "dms-ds-env-$([Guid]::NewGuid().ToString('N'))"
            New-Item -ItemType Directory -Path $path -Force | Out-Null
            return $path
        }

        # A base file that mirrors the real env files: a multi-line SCHEMA_PACKAGES block plus
        # scalars that must be preserved or overridden.
        $script:baseContent = @"
POSTGRES_DB_NAME=edfi_datamanagementservice
USE_API_SCHEMA_PATH=true
API_SCHEMA_PATH=/app/ApiSchema
DATABASE_TEMPLATE_PACKAGE=EdFi.Api.Populated.Template.PostgreSql.5.2.0
# A comment that must survive
SCHEMA_PACKAGES='[
  {
    "version": "1.0.333",
    "name": "EdFi.DataStandard52.ApiSchema"
  }
]'
LOG_LEVEL=Warning
"@
    }

    BeforeEach {
        $script:work = New-TempDir
        $script:basePath = Join-Path $script:work ".env.base"
        $script:overlayPath = Join-Path $script:work ".env.ds61"
        $script:targetPath = Join-Path $script:work ".env.derived"
        Set-Content -LiteralPath $script:basePath -Value $script:baseContent -NoNewline
    }

    AfterEach {
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "replaces the multi-line SCHEMA_PACKAGES block with the overlay value" {
        Set-Content -LiteralPath $script:overlayPath -Value @"
DMS_CONFIG_DATA_STANDARD_VERSION=6.1
DATABASE_TEMPLATE_PACKAGE=EdFi.Api.Populated.Template.PostgreSql.6.1.0
SCHEMA_PACKAGES='[{"version":"2.0.0","name":"EdFi.DataStandard61.ApiSchema"}]'
"@

        New-DataStandardDerivedEnvFile -BaseEnvironmentFile $script:basePath -OverlayEnvironmentFile $script:overlayPath -TargetPath $script:targetPath

        $values = ReadValuesFromEnvFile $script:targetPath
        $values["SCHEMA_PACKAGES"] | Should -BeLike '*EdFi.DataStandard61.ApiSchema*'
        $values["SCHEMA_PACKAGES"] | Should -Not -BeLike '*DataStandard52*'

        # The old multi-line DS 5.2 JSON lines must not linger in the derived file.
        $raw = Get-Content -LiteralPath $script:targetPath -Raw
        $raw | Should -Not -Match 'DataStandard52'
    }

    It "overrides scalar keys and adds new keys from the overlay" {
        Set-Content -LiteralPath $script:overlayPath -Value @"
DMS_CONFIG_DATA_STANDARD_VERSION=6.1
DATABASE_TEMPLATE_PACKAGE=EdFi.Api.Populated.Template.PostgreSql.6.1.0
SCHEMA_PACKAGES='[{"version":"2.0.0","name":"EdFi.DataStandard61.ApiSchema"}]'
"@

        New-DataStandardDerivedEnvFile -BaseEnvironmentFile $script:basePath -OverlayEnvironmentFile $script:overlayPath -TargetPath $script:targetPath
        $values = ReadValuesFromEnvFile $script:targetPath

        $values["DATABASE_TEMPLATE_PACKAGE"] | Should -Be "EdFi.Api.Populated.Template.PostgreSql.6.1.0"
        $values["DMS_CONFIG_DATA_STANDARD_VERSION"] | Should -Be "6.1"
    }

    It "preserves unrelated base lines and comments" {
        Set-Content -LiteralPath $script:overlayPath -Value @"
SCHEMA_PACKAGES='[{"version":"2.0.0","name":"EdFi.DataStandard61.ApiSchema"}]'
"@

        New-DataStandardDerivedEnvFile -BaseEnvironmentFile $script:basePath -OverlayEnvironmentFile $script:overlayPath -TargetPath $script:targetPath
        $values = ReadValuesFromEnvFile $script:targetPath
        $raw = Get-Content -LiteralPath $script:targetPath -Raw

        $values["POSTGRES_DB_NAME"] | Should -Be "edfi_datamanagementservice"
        $values["USE_API_SCHEMA_PATH"] | Should -Be "true"
        $values["LOG_LEVEL"] | Should -Be "Warning"
        $raw | Should -Match 'A comment that must survive'
    }

    It "fails fast when the base environment file is missing" {
        Set-Content -LiteralPath $script:overlayPath -Value "SCHEMA_PACKAGES='[]'"
        { New-DataStandardDerivedEnvFile -BaseEnvironmentFile (Join-Path $script:work "nope.env") -OverlayEnvironmentFile $script:overlayPath -TargetPath $script:targetPath } |
            Should -Throw "*base environment file not found*"
    }

    It "fails fast when the overlay file is missing" {
        { New-DataStandardDerivedEnvFile -BaseEnvironmentFile $script:basePath -OverlayEnvironmentFile (Join-Path $script:work "missing.ds99") -TargetPath $script:targetPath } |
            Should -Throw "*data standard overlay file not found*"
    }
}

Describe "Get-DataStandardOverlayToken" {
    BeforeAll {
        Import-Module (Join-Path ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))) "env-utility.psm1") -Force
    }

    It "maps '5.2' to 'ds52'" { Get-DataStandardOverlayToken "5.2" | Should -Be "ds52" }
    It "maps '6.1' to 'ds61'" { Get-DataStandardOverlayToken "6.1" | Should -Be "ds61" }
    It "passes an existing token through" { Get-DataStandardOverlayToken "ds52" | Should -Be "ds52" }
    It "throws on a non-numeric version" { { Get-DataStandardOverlayToken "abc" } | Should -Throw }
}

Describe "Resolve-DataStandardEnvironmentFile" {
    BeforeAll {
        Import-Module (Join-Path ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))) "env-utility.psm1") -Force
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-resolve-$([Guid]::NewGuid().ToString('N'))"
        $script:composeRoot = Join-Path $script:work "compose"
        New-Item -ItemType Directory -Path $script:composeRoot -Force | Out-Null
        $script:basePath = Join-Path $script:work ".env.base"
        Set-Content -LiteralPath $script:basePath -Value "DATABASE_TEMPLATE_PACKAGE=EdFi.Api.Populated.Template.PostgreSql.5.2.0`nLOG_LEVEL=Warning`n" -NoNewline
        Set-Content -LiteralPath (Join-Path $script:composeRoot ".env.ds61") -Value "DMS_CONFIG_DATA_STANDARD_VERSION=6.1`nDATABASE_TEMPLATE_PACKAGE=EdFi.Api.Populated.Template.PostgreSql.6.1.0`n" -NoNewline
    }

    AfterEach {
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "returns the base file unchanged when no version is requested" {
        $result = Resolve-DataStandardEnvironmentFile -DataStandardVersion "" -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot
        $result | Should -Be $script:basePath
    }

    It "composes the overlay into a derived file when a version is requested" {
        $result = Resolve-DataStandardEnvironmentFile -DataStandardVersion "6.1" -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot
        $result | Should -Not -Be $script:basePath
        $values = ReadValuesFromEnvFile $result
        $values["DMS_CONFIG_DATA_STANDARD_VERSION"] | Should -Be "6.1"
        $values["DATABASE_TEMPLATE_PACKAGE"] | Should -Be "EdFi.Api.Populated.Template.PostgreSql.6.1.0"
    }

    It "fails fast when the overlay for the version is missing" {
        { Resolve-DataStandardEnvironmentFile -DataStandardVersion "9.9" -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot } |
            Should -Throw "*no overlay for data standard version '9.9'*"
    }

    It "composes a prefixed overlay and reflects the prefix in the derived name" {
        # The bootstrap wrapper passes -OverlayPrefix ".env.bootstrap" to select the
        # local-bootstrap surfaces; the derived file name carries the prefix segment so both
        # derivations coexist under .derived/.
        Set-Content -LiteralPath (Join-Path $script:composeRoot ".env.bootstrap.ds61") -Value "DMS_CONFIG_DATA_STANDARD_VERSION=6.1`nSCHEMA_PACKAGES='[]'`n" -NoNewline

        $result = Resolve-DataStandardEnvironmentFile -DataStandardVersion "6.1" -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -OverlayPrefix ".env.bootstrap"

        [System.IO.Path]::GetFileName($result) | Should -Be ".env.base.bootstrap.ds61"
        $values = ReadValuesFromEnvFile $result
        $values["DMS_CONFIG_DATA_STANDARD_VERSION"] | Should -Be "6.1"
    }

    It "fails fast when the prefixed overlay is missing without falling back to the shared overlay" {
        # .env.ds61 exists in the fixture, but the bootstrap prefix must not silently fall back
        # to the shared E2E-surface overlay.
        { Resolve-DataStandardEnvironmentFile -DataStandardVersion "6.1" -BaseEnvironmentFile $script:basePath -DockerComposeRoot $script:composeRoot -OverlayPrefix ".env.bootstrap" } |
            Should -Throw "*no overlay for data standard version '6.1'*"
    }
}
