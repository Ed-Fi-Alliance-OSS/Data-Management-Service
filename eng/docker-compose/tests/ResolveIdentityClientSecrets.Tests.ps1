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

Describe "Resolve-IdentityClientSecretConfiguration" {
    Context "when env-file values are provided" {
        It "returns CONFIG_SERVICE_CLIENT_SECRET for the CMSReadOnlyAccess client" {
            $result = Resolve-IdentityClientSecretConfiguration -EnvValues @{ CONFIG_SERVICE_CLIENT_SECRET = "OverrideReadOnly1234567890!Abcd" }
            $result.CmsReadOnlyAccessClientSecret | Should -Be "OverrideReadOnly1234567890!Abcd"
        }

        It "returns DMS_CONFIG_IDENTITY_CLIENT_SECRET for the DmsConfigurationService client" {
            $result = Resolve-IdentityClientSecretConfiguration -EnvValues @{ DMS_CONFIG_IDENTITY_CLIENT_SECRET = "OverrideFullAccess123456789!Abcd" }
            $result.DmsConfigurationServiceClientSecret | Should -Be "OverrideFullAccess123456789!Abcd"
        }

        It "resolves both clients independently" {
            $result = Resolve-IdentityClientSecretConfiguration -EnvValues @{
                CONFIG_SERVICE_CLIENT_SECRET     = "ReadOnlySecret1234567890!Abcdef"
                DMS_CONFIG_IDENTITY_CLIENT_SECRET = "FullAccessSecret1234567890!Abcd"
            }
            $result.CmsReadOnlyAccessClientSecret | Should -Be "ReadOnlySecret1234567890!Abcdef"
            $result.DmsConfigurationServiceClientSecret | Should -Be "FullAccessSecret1234567890!Abcd"
        }

        It "resolves custom client-secret length bounds from the env file" {
            $result = Resolve-IdentityClientSecretConfiguration -EnvValues @{
                DMS_CONFIG_IDENTITY_CLIENT_SECRET_MINIMUM_LENGTH = "10"
                DMS_CONFIG_IDENTITY_CLIENT_SECRET_MAXIMUM_LENGTH = "200"
            }
            $result.ClientSecretMinimumLength | Should -Be 10
            $result.ClientSecretMaximumLength | Should -Be 200
        }
    }

    Context "when env-file values are missing or blank" {
        It "falls back to the local-dev default for both clients" {
            $result = Resolve-IdentityClientSecretConfiguration -EnvValues @{}
            $result.CmsReadOnlyAccessClientSecret | Should -Be $script:defaultSecret
            $result.DmsConfigurationServiceClientSecret | Should -Be $script:defaultSecret
        }

        It "treats a whitespace-only value as missing" {
            $result = Resolve-IdentityClientSecretConfiguration -EnvValues @{ CONFIG_SERVICE_CLIENT_SECRET = "   " }
            $result.CmsReadOnlyAccessClientSecret | Should -Be $script:defaultSecret
        }

        It "falls back to the default 32/128 length bounds" {
            $result = Resolve-IdentityClientSecretConfiguration -EnvValues @{}
            $result.ClientSecretMinimumLength | Should -Be 32
            $result.ClientSecretMaximumLength | Should -Be 128
        }
    }
}

Describe "Start scripts register identity clients with env-file secrets" {
    # Discovery-time cases: $PSScriptRoot is available during discovery in Pester v5.
    $composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $cases = @("start-local-dms.ps1", "start-published-dms.ps1", "start-local-config.ps1") | ForEach-Object {
        @{ Name = $_; ScriptPath = (Join-Path $composeRoot $_) }
    }

    It "resolves the identity client secrets from the env file in <Name>" -ForEach $cases {
        # The per-client bindings asserted below reference $identityClientSecrets, so the script
        # must populate it from the env file via the shared resolver.
        $content = Get-Content -LiteralPath $ScriptPath -Raw
        $content | Should -Match '\$identityClientSecrets\s*=\s*Resolve-IdentityClientSecretConfiguration\s+-EnvValues\s+\$envValues'
    }

    It "binds each client registration to the matching resolved secret and bounds in <Name>" -ForEach $cases {
        # A presence-only check (does the line contain -NewClientSecret?) is not enough: it would
        # still pass with a hard-coded value, with the same secret reused for both clients, or with
        # the two resolved secrets swapped. Assert the exact resolved property each client requires:
        #   - CMSReadOnlyAccess           -> CmsReadOnlyAccessClientSecret        (CONFIG_SERVICE_CLIENT_SECRET)
        #   - DmsConfigurationService     -> DmsConfigurationServiceClientSecret  (DMS_CONFIG_IDENTITY_CLIENT_SECRET)
        #   - CMSAuthMetadataReadOnlyAccess has no dedicated env-file secret and keeps the default.
        # Both operator-secret clients must also pass the env-file length bounds so a CMS-valid
        # secret is not rejected by the setup scripts' default 32/128 validation.
        $minBoundPattern = '-ClientSecretMinimumLength\s+\$identityClientSecrets\.ClientSecretMinimumLength\b'
        $maxBoundPattern = '-ClientSecretMaximumLength\s+\$identityClientSecrets\.ClientSecretMaximumLength\b'

        $setupLines = Get-Content -LiteralPath $ScriptPath | Where-Object {
            $_ -match '\./setup-(keycloak|openiddict)\.ps1' -and $_ -notmatch '-InitDb'
        }

        $setupLines | Should -Not -BeNullOrEmpty -Because "each start script invokes the identity setup scripts"

        foreach ($line in $setupLines) {
            if ($line -match '-NewClientId\s+"CMSAuthMetadataReadOnlyAccess"') {
                # Intentionally not bound to env-file values; uses the setup defaults.
                $line | Should -Not -Match '\$identityClientSecrets\.' -Because "CMSAuthMetadataReadOnlyAccess has no dedicated env-file secret: $line"
            }
            elseif ($line -match '-NewClientId\s+"CMSReadOnlyAccess"') {
                $line | Should -Match '-NewClientSecret\s+\$identityClientSecrets\.CmsReadOnlyAccessClientSecret\b' -Because "CMSReadOnlyAccess must register CONFIG_SERVICE_CLIENT_SECRET: $line"
                $line | Should -Match $minBoundPattern -Because "CMSReadOnlyAccess must validate with the env-file minimum length: $line"
                $line | Should -Match $maxBoundPattern -Because "CMSReadOnlyAccess must validate with the env-file maximum length: $line"
            }
            else {
                # No -NewClientId => the default DmsConfigurationService (full_access) client.
                $line | Should -Match '-NewClientSecret\s+\$identityClientSecrets\.DmsConfigurationServiceClientSecret\b' -Because "DmsConfigurationService must register DMS_CONFIG_IDENTITY_CLIENT_SECRET: $line"
                $line | Should -Match $minBoundPattern -Because "DmsConfigurationService must validate with the env-file minimum length: $line"
                $line | Should -Match $maxBoundPattern -Because "DmsConfigurationService must validate with the env-file maximum length: $line"
            }
        }
    }
}
