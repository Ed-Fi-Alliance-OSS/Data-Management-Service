# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Client-side Compose rendering only. No daemon, containers or HTTP requests are used here.
Describe 'Connect rate limit proxy deployment' {
    BeforeAll {
        $script:composeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

        function Get-ProxyDeployment {
            param([string] $Mode, [hashtable] $Overrides = @{}, [switch] $CmsOnly)

            $environmentFile = Join-Path $TestDrive 'proxy.env'
            $lines = @(Get-Content -LiteralPath (Join-Path $script:composeRoot '.env.e2e'))
            foreach ($key in $Overrides.Keys) {
                $lines += "$key=$($Overrides[$key])"
            }
            Set-Content -LiteralPath $environmentFile -Value $lines

            # Compose prioritizes the process environment. Keep this oracle independent of the
            # developer's stack settings, and restore every value even when rendering fails.
            $saved = @{}
            $variables = @(
                'DMS_CMS_PROXY_SUBNET', 'DMS_CMS_PROXY_IP', 'CMS_PROXY_IP',
                'CONFIGURATION_SERVICE_USE_REVERSE_PROXY_HEADERS',
                'CONFIGURATION_SERVICE_REVERSE_PROXY_KNOWN_PROXIES',
                'CONFIGURATION_SERVICE_REVERSE_PROXY_KNOWN_NETWORKS'
            )
            foreach ($key in $variables) {
                $saved[$key] = [Environment]::GetEnvironmentVariable($key)
                [Environment]::SetEnvironmentVariable($key, $null)
            }

            Push-Location $script:composeRoot
            try {
                $arguments = @('-f', 'postgresql.compose.yml', '-f', "$Mode-config.yml")
                if (-not $CmsOnly) {
                    $arguments += @('-f', "$Mode-dms.yml", '-f', 'dms-cms-proxy.yml')
                }
                $rendered = & docker compose @arguments --env-file $environmentFile config --format json
                if ($LASTEXITCODE -ne 0) {
                    throw "Compose rendering failed with exit code $LASTEXITCODE."
                }
                return ($rendered | Out-String) | ConvertFrom-Json
            }
            finally {
                Pop-Location
                foreach ($key in $variables) {
                    [Environment]::SetEnvironmentVariable($key, $saved[$key])
                }
            }
        }
    }

    It 'preserves disabled forwarding for a <Mode> CMS-only deployment' -TestCases @(
        @{ Mode = 'local' }, @{ Mode = 'published' }
    ) {
        param($Mode)
        $deployment = Get-ProxyDeployment -Mode $Mode -CmsOnly -Overrides @{
            CONFIGURATION_SERVICE_USE_REVERSE_PROXY_HEADERS = 'false'
        }
        $settings = $deployment.services.config.environment
        $settings.AppSettings__ReverseProxy__UseForwardedHeaders | Should -BeExactly 'false'
        $settings.AppSettings__ReverseProxy__KnownProxies | Should -BeNullOrEmpty
        $settings.PSObject.Properties.Name | Should -Not -Contain 'ConnectRateLimit__RequiredProxyAddress'
        $deployment.networks.PSObject.Properties.Name | Should -Not -Contain 'cms-proxy'
        $deployment.services.PSObject.Properties.Name | Should -Not -Contain 'dms'
    }

    It 'preserves explicitly configured forwarding for a <Mode> CMS-only deployment' -TestCases @(
        @{ Mode = 'local' }, @{ Mode = 'published' }
    ) {
        param($Mode)
        $deployment = Get-ProxyDeployment -Mode $Mode -CmsOnly -Overrides @{
            CONFIGURATION_SERVICE_USE_REVERSE_PROXY_HEADERS = 'true'
            CONFIGURATION_SERVICE_REVERSE_PROXY_KNOWN_PROXIES = '10.0.0.5'
        }
        $deployment.services.config.environment.AppSettings__ReverseProxy__UseForwardedHeaders |
            Should -BeExactly 'true'
        $deployment.services.config.environment.AppSettings__ReverseProxy__KnownProxies |
            Should -BeExactly '10.0.0.5'
        $deployment.services.config.environment.PSObject.Properties.Name |
            Should -Not -Contain 'ConnectRateLimit__RequiredProxyAddress'
    }

    It 'routes <Mode> CMS traffic through an exactly trusted, reserved peer' -TestCases @(
        @{ Mode = 'local' }, @{ Mode = 'published' }
    ) {
        param($Mode)
        # Existing environment files keep false. The selected paired overlay must establish trust
        # independently, so a deployment inherited from main still gets isolated source budgets.
        $deployment = Get-ProxyDeployment -Mode $Mode -Overrides @{
            CONFIGURATION_SERVICE_USE_REVERSE_PROXY_HEADERS = 'false'
        }
        $deployment.networks.'cms-proxy'.internal | Should -BeTrue
        $deployment.networks.'cms-proxy'.ipam.config.subnet | Should -BeExactly '172.30.148.0/29'
        $deployment.services.dms.networks.'cms-proxy'.ipv4_address | Should -BeExactly '172.30.148.2'
        $deployment.services.config.networks.'cms-proxy'.ipv4_address | Should -BeExactly '172.30.148.3'
        $deployment.services.dms.extra_hosts | Should -Contain 'ed-fi-api-config=172.30.148.3'
        $settings = $deployment.services.config.environment
        $settings.AppSettings__ReverseProxy__UseForwardedHeaders | Should -BeExactly 'true'
        ($settings.AppSettings__ReverseProxy__KnownProxies -split ',' | Where-Object { $_ }) |
            Should -BeExactly '172.30.148.2'
        $settings.AppSettings__ReverseProxy__KnownNetworks | Should -BeNullOrEmpty
        $settings.ConnectRateLimit__RequiredProxyAddress | Should -BeExactly '172.30.148.2'
    }

    It 'keeps <Mode> routing and trust aligned when the subnet is changed' -TestCases @(
        @{ Mode = 'local' }, @{ Mode = 'published' }
    ) {
        param($Mode)
        $deployment = Get-ProxyDeployment -Mode $Mode -Overrides @{
            DMS_CMS_PROXY_SUBNET = '172.30.151.0/29'
            DMS_CMS_PROXY_IP = '172.30.151.2'
            CMS_PROXY_IP = '172.30.151.3'
        }
        $deployment.networks.'cms-proxy'.ipam.config.subnet | Should -BeExactly '172.30.151.0/29'
        $deployment.services.dms.networks.'cms-proxy'.ipv4_address | Should -BeExactly '172.30.151.2'
        $deployment.services.config.networks.'cms-proxy'.ipv4_address | Should -BeExactly '172.30.151.3'
        $deployment.services.dms.extra_hosts | Should -Contain 'ed-fi-api-config=172.30.151.3'
        $deployment.services.config.environment.AppSettings__ReverseProxy__KnownProxies |
            Should -BeExactly '172.30.151.2,'
        $deployment.services.config.environment.ConnectRateLimit__RequiredProxyAddress |
            Should -BeExactly '172.30.151.2'
    }
}
