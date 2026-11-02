# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Client-side Compose rendering only. No daemon, containers or HTTP requests are used here.
#
# The published Compose files require their secret settings instead of falling back to a default
# (DMS-1652). Each negative case renders only the service under test: the database encryption key
# feeds both services, so rendering them together would let one guard hide a regression in the
# other. Rendered output is parsed, never printed.
Describe 'Published Compose required secrets' {
    BeforeAll {
        $script:composeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        $script:databaseKeySentinel = 'dms1652-db-key-sentinel-0000000000'
        $script:adminSentinel = 'host=sentinel-host;port=5432;username=sentinel;password=sentinel;database=sentinel;'
        $script:identityKeySentinel = 'dms1652-id-key-sentinel-0000000000'
        $script:fullDeployment = @('postgresql.compose.yml', 'published-dms.compose.yml', 'published-config.compose.yml', 'dms-cms-proxy.yml')

        function Get-SentinelLines {
            param([string[]] $Lines)
            $Lines = Set-Declaration -Lines $Lines -Key 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY' -Value $script:databaseKeySentinel
            $Lines = Set-Declaration -Lines $Lines -Key 'CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY' -Value $script:identityKeySentinel
            return Set-Declaration -Lines $Lines -Key 'DATABASE_CONNECTION_STRING_ADMIN' -Value $script:adminSentinel
        }

        function Get-EnvironmentLines {
            param([string] $Name)
            # Get-Content drops each line terminator; the rendered file is rewritten with LF below.
            return @(Get-Content -LiteralPath (Join-Path $script:composeRoot $Name))
        }

        function Remove-Declaration {
            param([string[]] $Lines, [string] $Key)
            return @($Lines | Where-Object { $_ -notmatch "^\s*(export\s+)?$([regex]::Escape($Key))=" })
        }

        function Set-Declaration {
            param([string[]] $Lines, [string] $Key, [string] $Value)
            return @(Remove-Declaration -Lines $Lines -Key $Key) + "$Key=$Value"
        }

        function Set-State {
            param([string[]] $Lines, [string] $Key, [string] $State)
            switch ($State) {
                'unset' { return Remove-Declaration -Lines $Lines -Key $Key }
                'empty' { return Set-Declaration -Lines $Lines -Key $Key -Value '' }
                'quoted-empty' { return Set-Declaration -Lines $Lines -Key $Key -Value '""' }
            }
            throw "Unknown state '$State'."
        }

        function Invoke-PublishedRender {
            param([string[]] $Files, [string[]] $Lines)

            # LF like the committed env files: only line endings are normalized, never values.
            $environmentFile = Join-Path $TestDrive 'render.env'
            [IO.File]::WriteAllText($environmentFile, (($Lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))

            # Compose prioritizes the process environment, so an inherited value could satisfy a
            # guard. Clear every name the rendered files or the env file can read, and restore each
            # one afterwards, even when rendering fails.
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($file in $Files) {
                $filePath = if ([IO.Path]::IsPathRooted($file)) { $file } else { Join-Path $script:composeRoot $file }
                $text = Get-Content -Raw -LiteralPath $filePath
                foreach ($match in [regex]::Matches($text, '\$\{([A-Za-z_][A-Za-z0-9_]*)')) {
                    [void]$names.Add($match.Groups[1].Value)
                }
            }
            foreach ($line in $Lines) {
                if ($line -match '^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)=') { [void]$names.Add($Matches[1]) }
            }
            foreach ($name in 'COMPOSE_PROJECT_NAME', 'COMPOSE_FILE', 'COMPOSE_PROFILES', 'COMPOSE_ENV_FILES') {
                [void]$names.Add($name)
            }

            $saved = @{}
            foreach ($name in $names) {
                $saved[$name] = [Environment]::GetEnvironmentVariable($name)
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }

            $errorFile = Join-Path $TestDrive 'render.stderr'
            $arguments = @('compose', '-p', 'dms1652render', '--project-directory', $script:composeRoot)
            foreach ($file in $Files) { $arguments += @('-f', $file) }
            $arguments += @('--env-file', $environmentFile, 'config', '--format', 'json')

            Push-Location $script:composeRoot
            try {
                $rendered = & docker @arguments 2> $errorFile
                $exitCode = $LASTEXITCODE
            }
            finally {
                Pop-Location
                foreach ($name in $names) {
                    # Restoring $null through SetEnvironmentVariable can leave an empty variable
                    # behind; remove the originally absent ones instead.
                    if ($null -eq $saved[$name]) {
                        Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
                    }
                    else {
                        [Environment]::SetEnvironmentVariable($name, $saved[$name])
                    }
                }
            }

            return [pscustomobject]@{
                ExitCode = $exitCode
                StdErr = (Get-Content -Raw -LiteralPath $errorFile) ?? ''
                Config = if ($exitCode -eq 0) { ($rendered | Out-String) | ConvertFrom-Json } else { $null }
            }
        }

        function Assert-RequiredValueFailure {
            param($Result, [string] $Service, [string] $Key, [string] $Variable, [string] $Doc)
            $Result.ExitCode | Should -Not -Be 0
            $Result.StdErr | Should -Match ([regex]::Escape("services.$Service.environment.$Key"))
            $Result.StdErr | Should -Match ([regex]::Escape("required variable $Variable is missing a value"))
            $Result.StdErr | Should -Match ([regex]::Escape($Doc))
        }
    }

    Context 'published-dms.compose.yml rendered without the Configuration Service' {
        It 'rejects <Variable> when it is <State>' -TestCases @(
            @{ Key = 'ConfigurationServiceSettings__EncryptionKey'; Variable = 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY'; Doc = 'docs/DOCKER.md'; State = 'unset' }
            @{ Key = 'ConfigurationServiceSettings__EncryptionKey'; Variable = 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY'; Doc = 'docs/DOCKER.md'; State = 'empty' }
            @{ Key = 'ConfigurationServiceSettings__EncryptionKey'; Variable = 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY'; Doc = 'docs/DOCKER.md'; State = 'quoted-empty' }
            @{ Key = 'DATABASE_CONNECTION_STRING_ADMIN'; Variable = 'DATABASE_CONNECTION_STRING_ADMIN'; Doc = 'docs/OPERATIONS.md'; State = 'unset' }
            @{ Key = 'DATABASE_CONNECTION_STRING_ADMIN'; Variable = 'DATABASE_CONNECTION_STRING_ADMIN'; Doc = 'docs/OPERATIONS.md'; State = 'empty' }
            @{ Key = 'DATABASE_CONNECTION_STRING_ADMIN'; Variable = 'DATABASE_CONNECTION_STRING_ADMIN'; Doc = 'docs/OPERATIONS.md'; State = 'quoted-empty' }
        ) {
            param($Key, $Variable, $Doc, $State)
            $lines = Set-State -Lines (Get-EnvironmentLines '.env.e2e') -Key $Variable -State $State

            $result = Invoke-PublishedRender -Files @('postgresql.compose.yml', 'published-dms.compose.yml') -Lines $lines

            Assert-RequiredValueFailure -Result $result -Service 'dms' -Key $Key -Variable $Variable -Doc $Doc
        }

        It 'is not satisfied by a value inherited from the calling process' {
            $original = [Environment]::GetEnvironmentVariable('CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY')
            try {
                $env:CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY = 'ambient-value'
                $lines = Remove-Declaration -Lines (Get-EnvironmentLines '.env.e2e') -Key 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY'

                $result = Invoke-PublishedRender -Files @('postgresql.compose.yml', 'published-dms.compose.yml') -Lines $lines

                Assert-RequiredValueFailure -Result $result -Service 'dms' -Key 'ConfigurationServiceSettings__EncryptionKey' `
                    -Variable 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY' -Doc 'docs/DOCKER.md'
                $env:CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY | Should -BeExactly 'ambient-value'
            }
            finally {
                if ($null -eq $original) {
                    Remove-Item -LiteralPath 'Env:CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY' -ErrorAction SilentlyContinue
                }
                else {
                    $env:CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY = $original
                }
            }
        }

        It 'passes explicit PostgreSQL values through unchanged' {
            $lines = Get-EnvironmentLines '.env.e2e'
            $lines = Set-Declaration -Lines $lines -Key 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY' -Value $script:databaseKeySentinel
            $lines = Set-Declaration -Lines $lines -Key 'DATABASE_CONNECTION_STRING_ADMIN' -Value $script:adminSentinel

            $result = Invoke-PublishedRender -Files @('postgresql.compose.yml', 'published-dms.compose.yml') -Lines $lines

            $result.ExitCode | Should -Be 0
            $settings = $result.Config.services.dms.environment
            $settings.ConfigurationServiceSettings__EncryptionKey | Should -BeExactly $script:databaseKeySentinel
            $settings.DATABASE_CONNECTION_STRING_ADMIN | Should -BeExactly $script:adminSentinel
        }

        It 'passes the SQL Server overlay admin connection string through, expanded' {
            # Known inputs replace the overlay's own declarations in place, so they still precede
            # the admin line that references them. Compose resolves an env file line by line.
            $overlay = Get-EnvironmentLines '.env.mssql'
            @($overlay -match '^MSSQL_DB_NAME=').Count | Should -Be 1
            @($overlay -match '^MSSQL_SA_PASSWORD=').Count | Should -Be 1
            $overlay = $overlay -replace '^MSSQL_DB_NAME=.*$', 'MSSQL_DB_NAME=dms1652_sentinel_db'
            $overlay = $overlay -replace '^MSSQL_SA_PASSWORD=.*$', 'MSSQL_SA_PASSWORD=Dms1652SentinelPw1'
            $lines = @(Get-EnvironmentLines '.env.e2e') + $overlay
            $lines = Set-Declaration -Lines $lines -Key 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY' -Value $script:databaseKeySentinel

            $result = Invoke-PublishedRender -Files @('mssql.compose.yml', 'published-dms.compose.yml') -Lines $lines

            $result.ExitCode | Should -Be 0
            $result.Config.services.dms.environment.DATABASE_CONNECTION_STRING_ADMIN |
                Should -BeExactly 'Server=dms-mssql;Database=dms1652_sentinel_db;User Id=sa;Password=Dms1652SentinelPw1;TrustServerCertificate=true;'
        }

        It 'renders a Keycloak deployment without the Configuration Service and without an identity key' {
            $lines = Get-EnvironmentLines '.env.e2e'
            $lines = Remove-Declaration -Lines $lines -Key 'CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY'
            $lines = Set-Declaration -Lines $lines -Key 'CONFIGURATION_SERVICE_IDENTITY_PROVIDER' -Value 'keycloak'

            $result = Invoke-PublishedRender -Files @('postgresql.compose.yml', 'published-dms.compose.yml', 'keycloak.compose.yml') -Lines $lines

            $result.ExitCode | Should -Be 0
            $result.Config.services.PSObject.Properties.Name | Should -Not -Contain 'config'
        }
    }

    Context 'published-config.compose.yml rendered without DMS' {
        It 'rejects <Variable> when it is <State>' -TestCases @(
            @{ Key = 'DatabaseSettings__EncryptionKey'; Variable = 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY'; State = 'unset' }
            @{ Key = 'DatabaseSettings__EncryptionKey'; Variable = 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY'; State = 'empty' }
            @{ Key = 'DatabaseSettings__EncryptionKey'; Variable = 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY'; State = 'quoted-empty' }
            @{ Key = 'IdentitySettings__EncryptionKey'; Variable = 'CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY'; State = 'unset' }
            @{ Key = 'IdentitySettings__EncryptionKey'; Variable = 'CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY'; State = 'empty' }
            @{ Key = 'IdentitySettings__EncryptionKey'; Variable = 'CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY'; State = 'quoted-empty' }
        ) {
            param($Key, $Variable, $State)
            $lines = Set-State -Lines (Get-EnvironmentLines '.env.e2e') -Key $Variable -State $State

            $result = Invoke-PublishedRender -Files @('postgresql.compose.yml', 'published-config.compose.yml') -Lines $lines

            Assert-RequiredValueFailure -Result $result -Service 'config' -Key $Key -Variable $Variable -Doc 'docs/DOCKER.md'
        }

        It 'requires the identity key even when Keycloak is the identity provider' {
            # Compose cannot see the identity provider, so the guard applies whenever CMS is composed.
            $lines = Remove-Declaration -Lines (Get-EnvironmentLines '.env.e2e') -Key 'CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY'
            $lines = Set-Declaration -Lines $lines -Key 'CONFIGURATION_SERVICE_IDENTITY_PROVIDER' -Value 'keycloak'

            $result = Invoke-PublishedRender -Files @('postgresql.compose.yml', 'published-config.compose.yml', 'keycloak.compose.yml') -Lines $lines

            Assert-RequiredValueFailure -Result $result -Service 'config' -Key 'IdentitySettings__EncryptionKey' `
                -Variable 'CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY' -Doc 'docs/DOCKER.md'
        }

        It 'passes explicit <Engine> values through unchanged' -TestCases @(
            @{ Engine = 'PostgreSQL'; DatabaseFile = 'postgresql.compose.yml'; Overlays = @() }
            @{ Engine = 'SQL Server'; DatabaseFile = 'mssql.compose.yml'; Overlays = @('.env.mssql') }
        ) {
            param($Engine, $DatabaseFile, $Overlays)
            $lines = @(Get-EnvironmentLines '.env.e2e')
            foreach ($overlay in $Overlays) { $lines += Get-EnvironmentLines $overlay }
            $lines = Get-SentinelLines -Lines $lines

            $result = Invoke-PublishedRender -Files @($DatabaseFile, 'published-config.compose.yml') -Lines $lines

            $result.ExitCode | Should -Be 0
            $settings = $result.Config.services.config.environment
            $settings.DatabaseSettings__EncryptionKey | Should -BeExactly $script:databaseKeySentinel
            $settings.IdentitySettings__EncryptionKey | Should -BeExactly $script:identityKeySentinel
        }
    }

    Context 'published DMS and Configuration Service rendered together' {
        It 'passes explicit values through and gives both services the same database key' {
            $lines = Get-SentinelLines -Lines (Get-EnvironmentLines '.env.e2e')

            $result = Invoke-PublishedRender -Files $script:fullDeployment -Lines $lines

            $result.ExitCode | Should -Be 0
            $result.Config.services.dms.environment.ConfigurationServiceSettings__EncryptionKey | Should -BeExactly $script:databaseKeySentinel
            $result.Config.services.config.environment.DatabaseSettings__EncryptionKey | Should -BeExactly $script:databaseKeySentinel
            $result.Config.services.config.environment.IdentitySettings__EncryptionKey | Should -BeExactly $script:identityKeySentinel
            $result.Config.services.dms.environment.DATABASE_CONNECTION_STRING_ADMIN | Should -BeExactly $script:adminSentinel
        }

        It 'renders a Keycloak deployment that supplies the identity key' {
            $lines = Get-SentinelLines -Lines (Get-EnvironmentLines '.env.e2e')
            $lines = Set-Declaration -Lines $lines -Key 'CONFIGURATION_SERVICE_IDENTITY_PROVIDER' -Value 'keycloak'

            $result = Invoke-PublishedRender -Files ($script:fullDeployment + 'keycloak.compose.yml') -Lines $lines

            $result.ExitCode | Should -Be 0
            $result.Config.services.config.environment.AppSettings__IdentityProvider | Should -BeExactly 'keycloak'
        }

        It 'stops when the shared database key is missing' {
            $lines = Remove-Declaration -Lines (Get-EnvironmentLines '.env.e2e') -Key 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY'

            $result = Invoke-PublishedRender -Files $script:fullDeployment -Lines $lines

            # Either service's guard may report first; the isolated contexts above pin each one.
            $result.ExitCode | Should -Not -Be 0
            $result.StdErr | Should -Match ([regex]::Escape('required variable CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY is missing a value'))
        }

        Context 'with a deployment-owned override that removes the vault-served keys' {
            BeforeAll {
                # dms-cms-proxy.yml is left out on purpose. It also sets environment on config, and
                # Compose drops a later !reset of a service whose environment an intermediate file
                # also sets, so with it these resets would not apply. That happens with or without the
                # required-value guards; these cases only cover how the guards interact with !reset.
                $script:vaultDeployment = @('postgresql.compose.yml', 'published-dms.compose.yml', 'published-config.compose.yml')
                $script:vaultOverride = Join-Path $TestDrive 'vault-override.yml'
                $override = @(
                    'services:'
                    '  dms:'
                    '    environment:'
                    '      ConfigurationServiceSettings__EncryptionKey: !reset null'
                    '      DATABASE_CONNECTION_STRING_ADMIN: !reset null'
                    '  config:'
                    '    environment:'
                    '      DatabaseSettings__EncryptionKey: !reset null'
                    '      IdentitySettings__EncryptionKey: !reset null'
                )
                [IO.File]::WriteAllText($script:vaultOverride, (($override -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
            }

            It 'removes the keys from the containers when the env file keeps their values set' {
                $lines = Get-SentinelLines -Lines (Get-EnvironmentLines '.env.e2e')

                $result = Invoke-PublishedRender -Files ($script:vaultDeployment + $script:vaultOverride) -Lines $lines

                $result.ExitCode | Should -Be 0
                $dms = $result.Config.services.dms.environment.PSObject.Properties.Name
                $dms | Should -Not -Contain 'ConfigurationServiceSettings__EncryptionKey'
                $dms | Should -Not -Contain 'DATABASE_CONNECTION_STRING_ADMIN'
                $config = $result.Config.services.config.environment.PSObject.Properties.Name
                $config | Should -Not -Contain 'DatabaseSettings__EncryptionKey'
                $config | Should -Not -Contain 'IdentitySettings__EncryptionKey'
            }

            It 'still requires the env file values, because Compose interpolates before applying the reset' {
                $lines = Remove-Declaration -Lines (Get-EnvironmentLines '.env.e2e') -Key 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY'

                $result = Invoke-PublishedRender -Files ($script:vaultDeployment + $script:vaultOverride) -Lines $lines

                $result.ExitCode | Should -Not -Be 0
                $result.StdErr | Should -Match ([regex]::Escape('required variable CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY is missing a value'))
            }
        }

        It 'renders the committed <Name> environment' -TestCases @(
            @{ Name = '.env.example'; Files = @('.env.example'); Engine = 'postgresql' }
            @{ Name = '.env.e2e'; Files = @('.env.e2e'); Engine = 'postgresql' }
            @{ Name = '.env.routeContext.e2e'; Files = @('.env.routeContext.e2e'); Engine = 'postgresql' }
            @{ Name = '.env.cursorpartitions.e2e'; Files = @('.env.cursorpartitions.e2e'); Engine = 'postgresql' }
            @{ Name = '.env.multitenancy'; Files = @('.env.multitenancy'); Engine = 'postgresql' }
            @{ Name = '.env.smoke'; Files = @('.env.smoke'); Engine = 'postgresql' }
            @{ Name = '.env.smoke.ds61'; Files = @('.env.smoke.ds61'); Engine = 'postgresql' }
            @{ Name = '.env.template'; Files = @('.env.template'); Engine = 'postgresql' }
            @{ Name = '.env.template.ds61'; Files = @('.env.template.ds61'); Engine = 'postgresql' }
            @{ Name = '.env.e2e with .env.mssql'; Files = @('.env.e2e', '.env.mssql'); Engine = 'mssql' }
            @{ Name = '.env.smoke with .env.mssql'; Files = @('.env.smoke', '.env.mssql'); Engine = 'mssql' }
            @{ Name = '.env.e2e with Keycloak'; Files = @('.env.e2e'); Engine = 'postgresql'; Keycloak = $true }
        ) {
            param($Name, $Files, $Engine, $Keycloak)
            $lines = @()
            foreach ($file in $Files) { $lines += Get-EnvironmentLines $file }
            $composeFiles = @(if ($Engine -eq 'mssql') { 'mssql.compose.yml' } else { 'postgresql.compose.yml' }) + $script:fullDeployment[1..3]
            if ($Keycloak) {
                $lines = Set-Declaration -Lines $lines -Key 'CONFIGURATION_SERVICE_IDENTITY_PROVIDER' -Value 'keycloak'
                $composeFiles += 'keycloak.compose.yml'
            }

            $result = Invoke-PublishedRender -Files $composeFiles -Lines $lines

            $result.StdErr | Should -Not -Match 'required variable'
            $result.ExitCode | Should -Be 0
        }
    }

    Context 'local Compose files' {
        It 'keep their fallbacks, so local stacks render without the published-file values' {
            $lines = Get-EnvironmentLines '.env.e2e'
            foreach ($key in 'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY', 'CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY', 'DATABASE_CONNECTION_STRING_ADMIN') {
                $lines = Remove-Declaration -Lines $lines -Key $key
            }

            $result = Invoke-PublishedRender -Files @('postgresql.compose.yml', 'local-dms.compose.yml', 'local-config.compose.yml') -Lines $lines

            $result.ExitCode | Should -Be 0
        }
    }
}
