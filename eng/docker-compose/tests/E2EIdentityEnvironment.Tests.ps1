# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

BeforeAll {
    $composeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    Import-Module (Join-Path $composeRoot 'env-utility.psm1') -Force
    $buildScript = Join-Path $composeRoot '../../build-dms.ps1'
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($buildScript, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw "build-dms.ps1 has parse errors: $parseErrors" }
    foreach ($name in 'Invoke-WithE2EIdentityEnvironment', 'Invoke-WithE2ETestProcessContext', 'RunE2E') {
        $definition = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
        }, $true)
        . ([scriptblock]::Create($definition.Extent.Text))
    }

    function Invoke-Execute { param([scriptblock] $Action) & $Action }
    function RunTests { throw 'RunTests must be mocked' }

    $script:identityNames = @(
        'CONFIGURATION_SERVICE_IDENTITY_PROVIDER', 'OAUTH_TOKEN_ENDPOINT', 'DMS_JWT_AUTHORITY',
        'DMS_JWT_METADATA_ADDRESS', 'CONFIGURATION_SERVICE_IDENTITY_AUTHORITY'
    )
    $script:environmentFile = Join-Path $TestDrive '.env.e2e'
    @'
CONFIGURATION_SERVICE_IDENTITY_PROVIDER=self-contained
DMS_JWT_AUTHORITY=http://default-cms:8081
DMS_JWT_METADATA_ADDRESS=http://default-cms:8081/.well-known/openid-configuration
KEYCLOAK_OAUTH_TOKEN_ENDPOINT=http://custom-keycloak:8080/realms/custom/protocol/openid-connect/token
KEYCLOAK_DMS_JWT_AUTHORITY=http://custom-keycloak:8080/realms/custom
KEYCLOAK_DMS_JWT_METADATA_ADDRESS=http://custom-keycloak:8080/realms/custom/.well-known/openid-configuration
KEYCLOAK_CONFIGURATION_SERVICE_IDENTITY_AUTHORITY=http://custom-keycloak-internal:8080/realms/custom
SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT=http://custom-cms:8081/oauth/token
SELF_CONTAINED_DMS_JWT_AUTHORITY=http://custom-cms:8081
SELF_CONTAINED_DMS_JWT_METADATA_ADDRESS=http://custom-cms:8081/.well-known/openid-configuration
'@ | Set-Content -LiteralPath $script:environmentFile
    $script:testSettings = [pscustomobject]@{
        EnvironmentFile = $script:environmentFile
        DataStoreDatabaseName = 'e2e'
        DatabaseEngine = 'postgresql'
        DataStoreAdminConnectionString = 'admin'
        DataStoreConnectionString = 'registration'
        DataStoreSnapshotConnectionString = 'snapshot'
        DmsContainerName = 'ed-fi-api'
        TestResultSuffix = 'e2e-document-cache'
    }
}

Describe 'E2E identity settings survive startup cleanup and reach test child processes' {
    BeforeEach {
        # Imports in AST-extracted functions resolve relative to this test file. Use the real
        # already-imported dotenv reader while bypassing only the repeated import.
        Mock Import-Module {}
        $script:originalEnvironment = @{}
        foreach ($name in $script:identityNames) {
            $script:originalEnvironment[$name] = @{
                Exists = Test-Path -LiteralPath "Env:$name"
                Value = [Environment]::GetEnvironmentVariable($name)
            }
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }
    }

    AfterEach {
        foreach ($name in $script:identityNames) {
            $original = $script:originalEnvironment[$name]
            if ($original.Exists) { Set-Item -LiteralPath "Env:$name" -Value $original.Value }
            else { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        }
    }

    It 'RunE2E passes the selected <Provider> endpoints to a child after startup restored its environment' -ForEach @(
        @{
            Provider = 'keycloak'
            Authority = 'http://custom-keycloak:8080/realms/custom'
            ConfigAuthority = 'http://custom-keycloak-internal:8080/realms/custom'
            Token = 'http://custom-keycloak:8080/realms/custom/protocol/openid-connect/token'
        },
        @{
            Provider = 'self-contained'
            Authority = 'http://custom-cms:8081'
            ConfigAuthority = 'http://custom-cms:8081'
            Token = 'http://custom-cms:8081/oauth/token'
        }
    ) {
        Mock RunTests {
            $script:observedIdentity = & pwsh -NoLogo -NoProfile -Command @'
@{
    Provider = $env:CONFIGURATION_SERVICE_IDENTITY_PROVIDER
    Authority = $env:DMS_JWT_AUTHORITY
    Metadata = $env:DMS_JWT_METADATA_ADDRESS
    ConfigAuthority = $env:CONFIGURATION_SERVICE_IDENTITY_AUTHORITY
    Token = $env:OAUTH_TOKEN_ENDPOINT
} | ConvertTo-Json -Compress
'@ | ConvertFrom-Json
            $LASTEXITCODE | Should -Be 0
        }

        RunE2E -TestFilter 'Category=DocumentCacheHostedHappyPath' -E2ETestSettings $script:testSettings -IdentityProvider $Provider

        Should -Invoke RunTests -Times 1 -Exactly
        $script:observedIdentity.Provider | Should -BeExactly $Provider
        $script:observedIdentity.Authority | Should -BeExactly $Authority
        $script:observedIdentity.Metadata | Should -BeExactly "$Authority/.well-known/openid-configuration"
        $script:observedIdentity.ConfigAuthority | Should -BeExactly $ConfigAuthority
        $script:observedIdentity.Token | Should -BeExactly $Token
        foreach ($name in $script:identityNames) {
            Test-Path -LiteralPath "Env:$name" | Should -BeFalse
        }
    }

    It 'rejects <State> <Prefix>_<Key> before changing the environment or running tests' -ForEach @(
        foreach ($provider in 'keycloak', 'self-contained') {
            $keys = @('OAUTH_TOKEN_ENDPOINT', 'DMS_JWT_AUTHORITY', 'DMS_JWT_METADATA_ADDRESS')
            if ($provider -eq 'keycloak') { $keys += 'CONFIGURATION_SERVICE_IDENTITY_AUTHORITY' }
            foreach ($key in $keys) {
                foreach ($state in 'missing', 'empty', 'whitespace') {
                    @{
                        Provider = $provider
                        Prefix = if ($provider -eq 'keycloak') { 'KEYCLOAK' } else { 'SELF_CONTAINED' }
                        Key = $key
                        State = $state
                    }
                }
            }
        }
    ) {
        $sourceKey = "${Prefix}_$Key"
        $invalidFile = Join-Path $TestDrive '.env.invalid'
        $lines = @(Get-Content -LiteralPath $script:environmentFile | Where-Object { -not $_.StartsWith("$sourceKey=") })
        if ($State -eq 'empty') { $lines += "$sourceKey=" }
        if ($State -eq 'whitespace') { $lines += "$sourceKey=   " }
        $lines | Set-Content -LiteralPath $invalidFile
        foreach ($name in $script:identityNames) { Set-Item -LiteralPath "Env:$name" -Value 'original' }
        Mock Set-Item { Microsoft.PowerShell.Management\Set-Item @PSBoundParameters }
        Mock RunTests {}

        {
            Invoke-WithE2EIdentityEnvironment -EnvironmentFile $invalidFile -IdentityProvider $Provider -Action { RunTests }
        } | Should -Throw "*${sourceKey}*${invalidFile}*${Provider}*"

        Should -Invoke Set-Item -Times 0 -Exactly
        Should -Invoke RunTests -Times 0 -Exactly
        foreach ($name in $script:identityNames) {
            [Environment]::GetEnvironmentVariable($name) | Should -BeExactly 'original'
        }
    }

    It 'overrides stale values and restores each prior <State> state when the action fails' -ForEach @(
        @{ State = 'absent'; Exists = $false; Value = $null },
        @{ State = 'empty'; Exists = $true; Value = '' },
        @{ State = 'whitespace'; Exists = $true; Value = '   ' },
        @{ State = 'nonempty'; Exists = $true; Value = 'stale-provider-setting' }
    ) {
        if ($Exists) {
            foreach ($name in $script:identityNames) { Set-Item -LiteralPath "Env:$name" -Value $Value }
        }

        {
            Invoke-WithE2EIdentityEnvironment -EnvironmentFile $script:environmentFile -IdentityProvider keycloak -Action {
                $env:CONFIGURATION_SERVICE_IDENTITY_PROVIDER | Should -BeExactly 'keycloak'
                $env:DMS_JWT_AUTHORITY | Should -BeExactly 'http://custom-keycloak:8080/realms/custom'
                throw 'test execution failed'
            }
        } | Should -Throw '*test execution failed*'

        foreach ($name in $script:identityNames) {
            Test-Path -LiteralPath "Env:$name" | Should -Be $Exists
            if ($Exists) { [Environment]::GetEnvironmentVariable($name) | Should -BeExactly $Value }
        }
    }
}

Describe 'RunE2E runs the DMS projection reader Keycloak fixture after the tests when asked (DMS-1440)' {
    BeforeAll {
        $definition = $ast.Find({
                param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq 'Invoke-WithDmsProjectionReaderKeycloakContext'
            }, $true)
        . ([scriptblock]::Create($definition.Extent.Text))

        function RunDmsProjectionReaderE2E { param([string] $DatabaseEngine, [switch] $Keycloak) throw 'RunDmsProjectionReaderE2E must be mocked' }

        $script:readerNames = @(
            'KEYCLOAK_READER_E2E_DMS_BASE_URL', 'KEYCLOAK_READER_E2E_CONFIG_SERVICE_URL',
            'KEYCLOAK_READER_E2E_CONFIG_ADMIN_CLIENT_ID', 'KEYCLOAK_READER_E2E_CONFIG_ADMIN_CLIENT_SECRET',
            'KEYCLOAK_READER_E2E_EXPECTED_ISSUER', 'KEYCLOAK_READER_E2E_DATABASE_ENGINE',
            'KEYCLOAK_READER_E2E_DATA_STORE_CONNECTION_STRING'
        )
    }

    BeforeEach {
        Mock Import-Module {}
        $script:testsRan = $false
        $script:originalEnvironment = @{}
        foreach ($name in $script:identityNames + $script:readerNames) {
            $script:originalEnvironment[$name] = @{
                Exists = Test-Path -LiteralPath "Env:$name"
                Value = [Environment]::GetEnvironmentVariable($name)
            }
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }
    }

    AfterEach {
        foreach ($name in $script:identityNames + $script:readerNames) {
            $original = $script:originalEnvironment[$name]
            if ($original.Exists) { Set-Item -LiteralPath "Env:$name" -Value $original.Value }
            else { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        }
    }

    It 'runs the Keycloak fixture after the tests, with this run''s own values in a child, then restores the environment' {
        Mock RunTests { $script:testsRan = $true }
        Mock RunDmsProjectionReaderE2E {
            $script:readerRanAfterTests = $script:testsRan
            $script:observedReader = & pwsh -NoLogo -NoProfile -Command @'
@{
    DmsBaseUrl = $env:KEYCLOAK_READER_E2E_DMS_BASE_URL
    ConfigServiceUrl = $env:KEYCLOAK_READER_E2E_CONFIG_SERVICE_URL
    AdminClientId = $env:KEYCLOAK_READER_E2E_CONFIG_ADMIN_CLIENT_ID
    AdminClientSecret = $env:KEYCLOAK_READER_E2E_CONFIG_ADMIN_CLIENT_SECRET
    ExpectedIssuer = $env:KEYCLOAK_READER_E2E_EXPECTED_ISSUER
    DatabaseEngine = $env:KEYCLOAK_READER_E2E_DATABASE_ENGINE
    DataStoreConnectionString = $env:KEYCLOAK_READER_E2E_DATA_STORE_CONNECTION_STRING
    TestProcessConnectionString = $env:AppSettings__DataStoreConnectionString
} | ConvertTo-Json -Compress
'@ | ConvertFrom-Json
        }

        RunE2E -TestFilter 'Category=MssqlRepresentative' -E2ETestSettings $script:testSettings -IdentityProvider keycloak -RunDmsProjectionReaderKeycloakE2E

        Should -Invoke RunTests -Times 1 -Exactly
        Should -Invoke RunDmsProjectionReaderE2E -Times 1 -Exactly -ParameterFilter { $Keycloak }
        $script:readerRanAfterTests | Should -BeTrue
        $script:observedReader.DmsBaseUrl | Should -Match '^http://localhost:8080/?$'
        $script:observedReader.ConfigServiceUrl | Should -Match '^http://localhost:8081/?$'
        $script:observedReader.AdminClientId | Should -BeExactly 'DmsConfigurationService'
        $script:observedReader.AdminClientSecret | Should -Not -BeNullOrEmpty
        $script:observedReader.ExpectedIssuer | Should -BeExactly 'http://custom-keycloak:8080/realms/custom' `
            -Because 'the expected issuer is the DMS JWT authority the identity environment set for this run'
        $script:observedReader.DatabaseEngine | Should -BeExactly 'postgresql'
        $script:observedReader.DataStoreConnectionString | Should -BeExactly 'registration'
        $script:observedReader.TestProcessConnectionString | Should -BeExactly 'registration' `
            -Because 'the reader runs inside the same E2E test-process context'
        foreach ($name in $script:readerNames) {
            Test-Path -LiteralPath "Env:$name" | Should -BeFalse
        }
    }

    It 'does not run the reader without the switch' {
        Mock RunTests {}
        Mock RunDmsProjectionReaderE2E {}

        RunE2E -TestFilter 'Category=MssqlRepresentative' -E2ETestSettings $script:testSettings -IdentityProvider keycloak

        Should -Invoke RunTests -Times 1 -Exactly
        Should -Invoke RunDmsProjectionReaderE2E -Times 0 -Exactly
    }

    It 'does not run the reader when the tests fail' {
        Mock RunTests { throw 'tests failed' }
        Mock RunDmsProjectionReaderE2E {}

        { RunE2E -TestFilter 'Category=MssqlRepresentative' -E2ETestSettings $script:testSettings -IdentityProvider keycloak -RunDmsProjectionReaderKeycloakE2E } |
            Should -Throw '*tests failed*'

        Should -Invoke RunDmsProjectionReaderE2E -Times 0 -Exactly
    }
}
