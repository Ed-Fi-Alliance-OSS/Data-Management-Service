# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Retired Configuration Service variable names are not read as fallbacks; the setup scripts only
# warn. These tests pin which names warn, what each warning tells the operator, and that a
# warning never carries the variable's value.

Describe 'Legacy Configuration Service environment variable names' {
    BeforeAll {
        $script:composeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        Import-Module (Join-Path $script:composeRoot 'env-utility.psm1') -Force
        $script:sentinel = 'SENTINEL-DO-NOT-LEAK'
        $script:legacyProcessNames = @('DMS_CONFIG_LOG_LEVEL', 'CONFIG_SERVICE_TENANT', 'DMS_CONFIG_CLAIMS_PATH')

        # A developer shell or CI runner may already carry a legacy name; set those aside so each test
        # sees only the process variables it sets.
        $script:ambientLegacyVariables = @{}
        foreach ($entry in [Environment]::GetEnvironmentVariables().GetEnumerator()) {
            if ($null -ne (Get-LegacyEnvironmentVariableReplacement -Name ([string]$entry.Key))) {
                $script:ambientLegacyVariables[[string]$entry.Key] = [string]$entry.Value
                [Environment]::SetEnvironmentVariable([string]$entry.Key, $null)
            }
        }

        function Invoke-LegacyWarning {
            param([string[]]$EnvironmentFile)
            $warnings = @()
            Write-LegacyEnvironmentVariableWarning -EnvironmentFile $EnvironmentFile -WarningVariable warnings -WarningAction SilentlyContinue
            return @($warnings | ForEach-Object { [string]$_ })
        }

        function Write-TestEnvironmentFile {
            param([string]$Name, [string[]]$Lines)
            $path = Join-Path $TestDrive $Name
            Set-Content -LiteralPath $path -Value $Lines
            return $path
        }
    }

    AfterAll {
        foreach ($name in $script:ambientLegacyVariables.Keys) {
            [Environment]::SetEnvironmentVariable($name, $script:ambientLegacyVariables[$name])
        }
    }

    AfterEach {
        foreach ($name in $script:legacyProcessNames) {
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }
    }

    Describe 'Get-LegacyEnvironmentVariableReplacement' {
        It 'maps <Name> to <Expected>' -ForEach @(
            @{ Name = 'DMS_CONFIG_DATASTORE'; Expected = 'CONFIGURATION_SERVICE_DATASTORE' }
            @{ Name = 'DMS_CONFIG_DATABASE_ENCRYPTION_KEY'; Expected = 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY' }
            @{ Name = 'CONFIG_SERVICE_URL'; Expected = 'DMS_CONFIGURATION_SERVICE_URL' }
            @{ Name = 'CONFIG_SERVICE_TENANT'; Expected = 'DMS_CONFIGURATION_SERVICE_TENANT' }
            @{ Name = 'KEYCLOAK_DMS_CONFIG_IDENTITY_AUTHORITY'; Expected = 'KEYCLOAK_CONFIGURATION_SERVICE_IDENTITY_AUTHORITY' }
            @{ Name = 'DMS_CONFIG_ENABLE_CLAIMS_RELOAD'; Expected = 'CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING' }
            @{ Name = 'CMS_PLUGINS_MOUNT_SOURCE'; Expected = 'CONFIGURATION_SERVICE_PLUGINS_MOUNT_SOURCE' }
            @{ Name = 'CMS_E2E_API_URL'; Expected = 'CONFIGURATION_SERVICE_E2E_API_URL' }
            @{ Name = 'SECRETS_E2E_CMS_CLIENT_SECRET'; Expected = 'SECRETS_E2E_CONFIGURATION_SERVICE_CLIENT_SECRET' }
        ) {
            Get-LegacyEnvironmentVariableReplacement -Name $Name | Should -BeExactly $Expected
        }

        It 'reports <_> as removed with no replacement' -ForEach @(
            'DMS_CONFIG_USE_EMBEDDED_BASE_CLAIMS'
            'DMS_CONFIG_CLAIMS_PATH'
            'DMS_CONFIG_USE_CLAIMS_PATH'
            'DMS_CONFIG_DANGEROUSLY_ENABLE_DYNAMIC_CLAIMS_LOADING'
            'DMS_CONFIG_ENABLE_APPLICATION_RESET_ENDPOINT'
            'DMS_CONFIG_IDENTITY_SCOPE'
        ) {
            Get-LegacyEnvironmentVariableReplacement -Name $_ | Should -BeExactly ''
        }

        It 'does not classify the current name <_>' -ForEach @(
            'CONFIGURATION_SERVICE_DATASTORE'
            'DMS_CONFIGURATION_SERVICE_URL'
            'KEYCLOAK_CONFIGURATION_SERVICE_IDENTITY_AUTHORITY'
            'CONFIGURATION_SERVICE_PLUGINS_MOUNT_SOURCE'
            'DMS_DATASTORE'
            'dms_config_datastore'
        ) {
            Get-LegacyEnvironmentVariableReplacement -Name $_ | Should -BeNullOrEmpty
            $null -eq (Get-LegacyEnvironmentVariableReplacement -Name $_) | Should -BeTrue
        }
    }

    Describe 'Write-LegacyEnvironmentVariableWarning' {
        It 'writes no warning when only current names are set' {
            $file = Write-TestEnvironmentFile -Name 'current.env' -Lines @(
                'CONFIGURATION_SERVICE_DATASTORE=postgresql'
                'DMS_CONFIGURATION_SERVICE_URL=http://ed-fi-api-config:8081'
            )

            Invoke-LegacyWarning -EnvironmentFile $file | Should -BeNullOrEmpty
        }

        It 'names the replacement for a legacy name in the env file' {
            $file = Write-TestEnvironmentFile -Name 'legacy.env' -Lines @(
                "DMS_CONFIG_DATABASE_ENCRYPTION_KEY=$script:sentinel"
                "export CONFIG_SERVICE_CLIENT_SECRET=$script:sentinel"
            )

            $warnings = Invoke-LegacyWarning -EnvironmentFile $file

            $warnings | Should -HaveCount 2
            $warnings | Should -Contain "Environment variable 'CONFIG_SERVICE_CLIENT_SECRET' in '$file' is no longer read. Rename it to 'DMS_CONFIGURATION_SERVICE_CLIENT_SECRET'."
            $warnings | Should -Contain "Environment variable 'DMS_CONFIG_DATABASE_ENCRYPTION_KEY' in '$file' is no longer read. Rename it to 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY'."
        }

        It 'ignores a commented-out legacy name' {
            $file = Write-TestEnvironmentFile -Name 'commented.env' -Lines @('# DMS_CONFIG_DATASTORE=postgresql')

            Invoke-LegacyWarning -EnvironmentFile $file | Should -BeNullOrEmpty
        }

        It 'warns when the legacy name is set beside its replacement' {
            $file = Write-TestEnvironmentFile -Name 'both.env' -Lines @(
                'DMS_CONFIG_DATASTORE=mssql'
                'CONFIGURATION_SERVICE_DATASTORE=postgresql'
            )

            Invoke-LegacyWarning -EnvironmentFile $file |
                Should -BeExactly "Environment variable 'DMS_CONFIG_DATASTORE' in '$file' is no longer read. Rename it to 'CONFIGURATION_SERVICE_DATASTORE'."
        }

        It 'says a stale name can be removed' {
            $file = Write-TestEnvironmentFile -Name 'stale.env' -Lines @('DMS_CONFIG_USE_CLAIMS_PATH=true')

            Invoke-LegacyWarning -EnvironmentFile $file |
                Should -BeExactly "Environment variable 'DMS_CONFIG_USE_CLAIMS_PATH' in '$file' is no longer read and can be removed."
        }

        It 'names the merged replacement for the Azure claims reload switch' {
            $file = Write-TestEnvironmentFile -Name 'azure.env' -Lines @('DMS_CONFIG_ENABLE_CLAIMS_RELOAD=false')

            Invoke-LegacyWarning -EnvironmentFile $file |
                Should -BeExactly "Environment variable 'DMS_CONFIG_ENABLE_CLAIMS_RELOAD' in '$file' is no longer read. Rename it to 'CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING'."
        }

        It 'warns about a legacy name set only in the process environment' {
            $file = Write-TestEnvironmentFile -Name 'clean.env' -Lines @('CONFIGURATION_SERVICE_LOG_LEVEL=Information')
            $env:DMS_CONFIG_LOG_LEVEL = $script:sentinel
            $env:DMS_CONFIG_CLAIMS_PATH = $script:sentinel

            $warnings = Invoke-LegacyWarning -EnvironmentFile $file

            $warnings | Should -HaveCount 2
            $warnings | Should -Contain "Environment variable 'DMS_CONFIG_LOG_LEVEL' in the process environment is no longer read. Rename it to 'CONFIGURATION_SERVICE_LOG_LEVEL'."
            $warnings | Should -Contain "Environment variable 'DMS_CONFIG_CLAIMS_PATH' in the process environment is no longer read and can be removed."
        }

        It 'warns once per source when both the env file and the process environment set a legacy name' {
            $file = Write-TestEnvironmentFile -Name 'tenant.env' -Lines @("CONFIG_SERVICE_TENANT=$script:sentinel")
            $env:CONFIG_SERVICE_TENANT = $script:sentinel

            $warnings = Invoke-LegacyWarning -EnvironmentFile $file

            $warnings | Should -HaveCount 2
            $warnings | Should -Contain "Environment variable 'CONFIG_SERVICE_TENANT' in '$file' is no longer read. Rename it to 'DMS_CONFIGURATION_SERVICE_TENANT'."
            $warnings | Should -Contain "Environment variable 'CONFIG_SERVICE_TENANT' in the process environment is no longer read. Rename it to 'DMS_CONFIGURATION_SERVICE_TENANT'."
        }

        It 'never writes a legacy variable value' {
            $file = Write-TestEnvironmentFile -Name 'secrets.env' -Lines @(
                "DMS_CONFIG_IDENTITY_CLIENT_SECRET=$script:sentinel"
                "CONFIG_SERVICE_CLIENT_SECRET=`"$script:sentinel`""
                "DMS_CONFIG_IDENTITY_SCOPE=$script:sentinel"
            )
            $env:DMS_CONFIG_LOG_LEVEL = $script:sentinel

            $warnings = Invoke-LegacyWarning -EnvironmentFile $file

            $warnings | Should -HaveCount 4
            ($warnings -join "`n") | Should -Not -Match $script:sentinel
        }
    }

    Describe 'Azure legacy-env-warning.sh' -Skip:(-not (Get-Command bash -ErrorAction SilentlyContinue)) {
        BeforeAll {
            $script:azureHelper = [IO.Path]::GetFullPath((Join-Path $script:composeRoot '../azure-vm/compose/legacy-env-warning.sh'))

            function Invoke-AzureLegacyWarning {
                param([string]$EnvironmentFile)
                $output = & bash -c 'set -euo pipefail; source "$1"; warn_legacy_env_names "$2"' _ $script:azureHelper $EnvironmentFile 2>&1
                if ($LASTEXITCODE -ne 0) { throw "legacy-env-warning.sh failed: $output" }
                return @($output | ForEach-Object { [string]$_ })
            }
        }

        It 'warns with the same replacements as env-utility.psm1 and never prints values' {
            $file = Write-TestEnvironmentFile -Name 'azure-legacy.env' -Lines @(
                "DMS_CONFIG_DATABASE_ENCRYPTION_KEY=$script:sentinel"
                "CONFIG_SERVICE_CLIENT_SECRET=$script:sentinel"
                'DMS_CONFIG_ENABLE_CLAIMS_RELOAD=false'
                'DMS_CONFIG_CLAIMS_PATH=/claims'
                'CONFIGURATION_SERVICE_LOG_LEVEL=Information'
            )
            $env:DMS_CONFIG_LOG_LEVEL = $script:sentinel

            $warnings = Invoke-AzureLegacyWarning -EnvironmentFile $file

            $warnings | Should -HaveCount 5
            $warnings | Should -Contain "WARNING: Environment variable 'DMS_CONFIG_DATABASE_ENCRYPTION_KEY' in '$file' is no longer read. Rename it to 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY'."
            $warnings | Should -Contain "WARNING: Environment variable 'CONFIG_SERVICE_CLIENT_SECRET' in '$file' is no longer read. Rename it to 'DMS_CONFIGURATION_SERVICE_CLIENT_SECRET'."
            $warnings | Should -Contain "WARNING: Environment variable 'DMS_CONFIG_ENABLE_CLAIMS_RELOAD' in '$file' is no longer read. Rename it to 'CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING'."
            $warnings | Should -Contain "WARNING: Environment variable 'DMS_CONFIG_CLAIMS_PATH' in '$file' is no longer read and can be removed."
            $warnings | Should -Contain "WARNING: Environment variable 'DMS_CONFIG_LOG_LEVEL' in the process environment is no longer read. Rename it to 'CONFIGURATION_SERVICE_LOG_LEVEL'."
            ($warnings -join "`n") | Should -Not -Match $script:sentinel
        }

        It 'writes nothing when only current names are set' {
            $file = Write-TestEnvironmentFile -Name 'azure-current.env' -Lines @('CONFIGURATION_SERVICE_LOG_LEVEL=Information')

            Invoke-AzureLegacyWarning -EnvironmentFile $file | Should -BeNullOrEmpty
        }
    }
}
