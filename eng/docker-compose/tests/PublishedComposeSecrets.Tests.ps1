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
                $text = Get-Content -Raw -LiteralPath (Join-Path $script:composeRoot $file)
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
}
