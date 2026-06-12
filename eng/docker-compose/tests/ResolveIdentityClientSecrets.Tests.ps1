# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Regression coverage for DMS-1171: the local startup scripts must register the CMS
# identity clients with the same secrets DMS/CMS authenticate with, so that overriding
# CONFIG_SERVICE_CLIENT_SECRET (or DMS_CONFIG_IDENTITY_CLIENT_SECRET) does not produce a
# secret mismatch that breaks CMS token acquisition.

BeforeAll {
    $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    $script:defaultSecret = "ValidClientSecret1234567890!Abcd"
}

Describe "Resolve-IdentityClientSecrets" {
    Context "when env-file values are provided" {
        It "returns CONFIG_SERVICE_CLIENT_SECRET for the CMSReadOnlyAccess client" {
            $result = Resolve-IdentityClientSecrets -EnvValues @{ CONFIG_SERVICE_CLIENT_SECRET = "OverrideReadOnly1234567890!Abcd" }
            $result.CmsReadOnlyAccessClientSecret | Should -Be "OverrideReadOnly1234567890!Abcd"
        }

        It "returns DMS_CONFIG_IDENTITY_CLIENT_SECRET for the DmsConfigurationService client" {
            $result = Resolve-IdentityClientSecrets -EnvValues @{ DMS_CONFIG_IDENTITY_CLIENT_SECRET = "OverrideFullAccess123456789!Abcd" }
            $result.DmsConfigurationServiceClientSecret | Should -Be "OverrideFullAccess123456789!Abcd"
        }

        It "resolves both clients independently" {
            $result = Resolve-IdentityClientSecrets -EnvValues @{
                CONFIG_SERVICE_CLIENT_SECRET     = "ReadOnlySecret1234567890!Abcdef"
                DMS_CONFIG_IDENTITY_CLIENT_SECRET = "FullAccessSecret1234567890!Abcd"
            }
            $result.CmsReadOnlyAccessClientSecret | Should -Be "ReadOnlySecret1234567890!Abcdef"
            $result.DmsConfigurationServiceClientSecret | Should -Be "FullAccessSecret1234567890!Abcd"
        }
    }

    Context "when env-file values are missing or blank" {
        It "falls back to the local-dev default for both clients" {
            $result = Resolve-IdentityClientSecrets -EnvValues @{}
            $result.CmsReadOnlyAccessClientSecret | Should -Be $script:defaultSecret
            $result.DmsConfigurationServiceClientSecret | Should -Be $script:defaultSecret
        }

        It "treats a whitespace-only value as missing" {
            $result = Resolve-IdentityClientSecrets -EnvValues @{ CONFIG_SERVICE_CLIENT_SECRET = "   " }
            $result.CmsReadOnlyAccessClientSecret | Should -Be $script:defaultSecret
        }
    }
}

Describe "Start scripts register identity clients with env-file secrets" {
    # Discovery-time cases: $PSScriptRoot is available during discovery in Pester v5.
    $composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $cases = @("start-local-dms.ps1", "start-published-dms.ps1", "start-local-config.ps1") | ForEach-Object {
        @{ Name = $_; ScriptPath = (Join-Path $composeRoot $_) }
    }

    It "passes -NewClientSecret on every full-access and read-only client registration in <Name>" -ForEach $cases {
        # Every setup-keycloak/setup-openiddict client registration must pass -NewClientSecret,
        # except -InitDb (no client) and the CMSAuthMetadataReadOnlyAccess client (no dedicated
        # env-file secret). A registration without -NewClientSecret silently falls back to the
        # setup script's hard-coded default and reintroduces the secret-mismatch regression.
        $offenders = Get-Content -LiteralPath $ScriptPath | Where-Object {
            $_ -match '\./setup-(keycloak|openiddict)\.ps1' -and
            $_ -notmatch 'InitDb' -and
            $_ -notmatch 'CMSAuthMetadataReadOnlyAccess' -and
            $_ -notmatch 'NewClientSecret'
        }

        $offenders | Should -BeNullOrEmpty
    }
}
