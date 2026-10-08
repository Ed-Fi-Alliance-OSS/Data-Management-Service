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
        'DMS_CONFIG_IDENTITY_PROVIDER', 'OAUTH_TOKEN_ENDPOINT', 'DMS_JWT_AUTHORITY',
        'DMS_JWT_METADATA_ADDRESS', 'DMS_CONFIG_IDENTITY_AUTHORITY'
    )
    $script:environmentFile = Join-Path $TestDrive '.env.e2e'
    @'
DMS_CONFIG_IDENTITY_PROVIDER=self-contained
DMS_JWT_AUTHORITY=http://default-cms:8081
DMS_JWT_METADATA_ADDRESS=http://default-cms:8081/.well-known/openid-configuration
KEYCLOAK_OAUTH_TOKEN_ENDPOINT=http://custom-keycloak:8080/realms/custom/protocol/openid-connect/token
KEYCLOAK_DMS_JWT_AUTHORITY=http://custom-keycloak:8080/realms/custom
KEYCLOAK_DMS_JWT_METADATA_ADDRESS=http://custom-keycloak:8080/realms/custom/.well-known/openid-configuration
KEYCLOAK_DMS_CONFIG_IDENTITY_AUTHORITY=http://custom-keycloak-internal:8080/realms/custom
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
    Provider = $env:DMS_CONFIG_IDENTITY_PROVIDER
    Authority = $env:DMS_JWT_AUTHORITY
    Metadata = $env:DMS_JWT_METADATA_ADDRESS
    ConfigAuthority = $env:DMS_CONFIG_IDENTITY_AUTHORITY
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
            if ($provider -eq 'keycloak') { $keys += 'DMS_CONFIG_IDENTITY_AUTHORITY' }
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
                $env:DMS_CONFIG_IDENTITY_PROVIDER | Should -BeExactly 'keycloak'
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
