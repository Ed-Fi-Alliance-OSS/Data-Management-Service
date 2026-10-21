# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../../database-safety.psm1') -Force
    $script:launcherPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Invoke-TokenContractE2E.ps1'))
    $script:launcherText = Get-Content $script:launcherPath -Raw
    $parseErrors = $null
    $tokens = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:launcherPath, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw "Launcher parse failed: $($parseErrors[0].Message)" }

    foreach ($functionName in @(
        'Get-TokenContractOverlayLine',
        'Get-TokenContractUpstreamUri',
        'Get-TokenContractTestFilter',
        'Get-TokenContractBuildArgument',
        'Invoke-WithTokenContractProxyEnvironment',
        'Assert-TokenContractTrx',
        'Assert-NativeCommandSucceeded',
        'Build-TokenContractE2ETestAssembly'
    )) {
        $functionAst = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName }, $true) | Select-Object -First 1
        if ($null -eq $functionAst) { throw "Function '$functionName' was not found in the launcher." }
        . ([scriptblock]::Create($functionAst.Extent.Text))
    }

    $buildScriptPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../build-dms.ps1'))
    $buildTokens = $null
    $buildParseErrors = $null
    $buildAst = [System.Management.Automation.Language.Parser]::ParseFile($buildScriptPath, [ref]$buildTokens, [ref]$buildParseErrors)
    if ($buildParseErrors.Count -gt 0) { throw "build-dms.ps1 parse failed: $($buildParseErrors[0].Message)" }
    $runE2eAst = $buildAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'RunE2E' }, $true) | Select-Object -First 1
    if ($null -eq $runE2eAst) { throw 'RunE2E was not found in build-dms.ps1.' }
    . ([scriptblock]::Create($runE2eAst.Extent.Text))

    $setAuthenticationServiceAst = $buildAst.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'SetAuthenticationServiceURL' }, $true) | Select-Object -First 1
    if ($null -eq $setAuthenticationServiceAst) { throw 'SetAuthenticationServiceURL was not found in build-dms.ps1.' }
    . ([scriptblock]::Create($setAuthenticationServiceAst.Extent.Text))
}

Describe 'DMS-1606 token contract E2E launcher' {
    BeforeEach {
        $script:previousProviderEnvironment = @{}
        foreach ($key in @('SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT', 'KEYCLOAK_OAUTH_TOKEN_ENDPOINT', 'CMS_TOKEN_URL', 'CORRELATION_ID_HEADER')) {
            $script:previousProviderEnvironment[$key] = [Environment]::GetEnvironmentVariable($key)
            [Environment]::SetEnvironmentVariable($key, $null)
        }
    }

    AfterEach {
        foreach ($key in $script:previousProviderEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($key, $script:previousProviderEnvironment[$key])
        }
    }

    It 'is present and exposes the planned provider, engine, environment, and build switches' {
        Test-Path $script:launcherPath | Should -BeTrue
        $script:launcherText | Should -Match 'IdentityProvider'
        $script:launcherText | Should -Match 'DatabaseEngine'
        $script:launcherText | Should -Match 'EnvironmentFile'
        $script:launcherText | Should -Match 'SkipDockerBuild'
        $script:launcherText | Should -Match 'UsePrebuiltOutput'
    }

    It 'overlays only the selected container token endpoint and correlation input header' {
        $selfContained = @(Get-TokenContractOverlayLine -Provider self-contained)
        $keycloak = @(Get-TokenContractOverlayLine -Provider keycloak)

        $selfContained | Should -Be @(
            'SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT=http://dms-token-proxy:8080/token',
            'CORRELATION_ID_HEADER=X-DMS-1606-Correlation'
        )
        $keycloak | Should -Be @(
            'KEYCLOAK_OAUTH_TOKEN_ENDPOINT=http://dms-token-proxy:8080/token',
            'CORRELATION_ID_HEADER=X-DMS-1606-Correlation'
        )
        ($selfContained -join "`n") | Should -Not -Match 'JWT|METADATA|localhost|upstream'
    }

    It 'adds only the route qualifier to the second overlay' {
        $lines = @(Get-TokenContractOverlayLine -Provider self-contained -Qualified)
        $lines | Should -Be @(
            'SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT=http://dms-token-proxy:8080/token',
            'CORRELATION_ID_HEADER=X-DMS-1606-Correlation',
            'ROUTE_QUALIFIER_SEGMENTS=schoolYear'
        )
    }

    It 'preserves the selected real provider URI as the fixed forwarding target' {
        $values = @{
            SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT = 'http://ed-fi-api-config:8081/connect/token'
            KEYCLOAK_OAUTH_TOKEN_ENDPOINT = 'http://dms-keycloak:8080/realms/edfi/protocol/openid-connect/token'
            SELF_CONTAINED_DMS_JWT_AUTHORITY = 'http://ed-fi-api-config:8081'
            KEYCLOAK_DMS_JWT_AUTHORITY = 'http://localhost:8045/realms/edfi'
        }

        (Get-TokenContractUpstreamUri -Provider self-contained -EnvironmentValues $values) |
            Should -Be 'http://ed-fi-api-config:8081/connect/token'
        (Get-TokenContractUpstreamUri -Provider keycloak -EnvironmentValues $values) |
            Should -Be 'http://dms-keycloak:8080/realms/edfi/protocol/openid-connect/token'
        { Get-TokenContractUpstreamUri -Provider keycloak -EnvironmentValues @{ KEYCLOAK_OAUTH_TOKEN_ENDPOINT = 'file:///tmp/token' } } |
            Should -Throw '*not an HTTP URI*'
    }

    It 'selects provider-specific engine exclusions for root and qualified passes' {
        (Get-TokenContractTestFilter -Engine postgresql) | Should -Be '(Category=@DMS-1606)&(Category!=@TokenContractQualified)&(Category!=@MssqlOnly)'
        (Get-TokenContractTestFilter -Engine mssql) | Should -Be '(Category=@DMS-1606)&(Category!=@TokenContractQualified)&(Category!=@PostgresqlOnly)'
        (Get-TokenContractTestFilter -Engine postgresql -Qualified) | Should -Be '(Category=@TokenContractQualified)&(Category!=@MssqlOnly)'
        (Get-TokenContractTestFilter -Engine mssql -Qualified) | Should -Be '(Category=@TokenContractQualified)&(Category!=@PostgresqlOnly)'
    }

    It 'resolves supported Compose endpoint values before validating them' -TestCases @(
        @{ Raw = '"http://ed-fi-api-config:8081/connect/token"' },
        @{ Raw = "'http://ed-fi-api-config:8081/connect/token'" },
        @{ Raw = 'http://ed-fi-api-config:8081/connect/token # provider endpoint' },
        @{ Raw = '${CMS_TOKEN_URL}' },
        @{ Raw = '"${CMS_TOKEN_URL}" # provider endpoint' }
    ) {
        param($Raw)
        $values = @{
            CMS_TOKEN_URL = 'http://ed-fi-api-config:8081/connect/token'
            SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT = $Raw
        }
        Get-TokenContractUpstreamUri -Provider self-contained -EnvironmentValues $values |
            Should -Be 'http://ed-fi-api-config:8081/connect/token'
    }

    It 'honors ambient precedence for both the endpoint and its references' {
        $values = @{
            CMS_TOKEN_URL = 'http://file-provider/token'
            SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT = '${CMS_TOKEN_URL}'
        }
        $env:CMS_TOKEN_URL = 'http://ambient-reference/token'
        Get-TokenContractUpstreamUri -Provider self-contained -EnvironmentValues $values |
            Should -Be 'http://ambient-reference/token'
        $env:SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT = 'http://ambient-endpoint/token'
        Get-TokenContractUpstreamUri -Provider self-contained -EnvironmentValues $values |
            Should -Be 'http://ambient-endpoint/token'
    }

    It 'scopes the selected endpoint and correlation override and restores them on failure' -TestCases @(
        @{ IdentityProvider = 'self-contained'; Key = 'SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT'; OtherKey = 'KEYCLOAK_OAUTH_TOKEN_ENDPOINT' },
        @{ IdentityProvider = 'keycloak'; Key = 'KEYCLOAK_OAUTH_TOKEN_ENDPOINT'; OtherKey = 'SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT' }
    ) {
        param($IdentityProvider, $Key, $OtherKey)
        $selectedProvider = $IdentityProvider
        foreach ($hadPreviousValues in @($false, $true)) {
            if ($hadPreviousValues) {
                [Environment]::SetEnvironmentVariable($Key, 'http://original-provider/token')
                $env:CORRELATION_ID_HEADER = 'Original-Correlation'
            }
            [Environment]::SetEnvironmentVariable($OtherKey, 'http://unselected-provider/token')
            foreach ($fail in @($false, $true)) {
                $invoke = {
                    Invoke-WithTokenContractProxyEnvironment -Provider $selectedProvider -SkipBuild -Action {
                        [Environment]::GetEnvironmentVariable($Key) | Should -Be 'http://dms-token-proxy:8080/token'
                        [Environment]::GetEnvironmentVariable($OtherKey) | Should -Be 'http://unselected-provider/token'
                        $env:CORRELATION_ID_HEADER | Should -Be 'X-DMS-1606-Correlation'
                        if ($fail) { throw 'fixture failure' }
                    }
                }
                if ($fail) { $invoke | Should -Throw '*fixture failure*' }
                else { & $invoke }
                if ($hadPreviousValues) {
                    [Environment]::GetEnvironmentVariable($Key) | Should -Be 'http://original-provider/token'
                    $env:CORRELATION_ID_HEADER | Should -Be 'Original-Correlation'
                }
                else {
                    [Environment]::GetEnvironmentVariable($Key) | Should -BeNullOrEmpty
                    Test-Path Env:CORRELATION_ID_HEADER | Should -BeFalse
                }
            }
        }
    }

    It 'binds the build script parameters by name and forwards requested switches' {
        $parameters = Get-TokenContractBuildArgument `
            -Provider keycloak -Engine mssql `
            -EnvironmentPath '/repo/eng/docker-compose/.env.e2e' -OverlayPath '/repo/.derived/overlay.env' `
            -TestFilter '(Category=@TokenContractQualified)&(Category!=@PostgresqlOnly)' `
            -BuildConfiguration Release -SkipBuild -PrebuiltOutput

        $expectedParameters = @{
            Command = 'E2ETest'
            Configuration = 'Release'
            IdentityProvider = 'keycloak'
            DatabaseEngine = 'mssql'
            EnvironmentFile = '/repo/eng/docker-compose/.env.e2e'
            EnvironmentOverlayFile = '/repo/.derived/overlay.env'
            TestFilter = '(Category=@TokenContractQualified)&(Category!=@PostgresqlOnly)'
            SkipDockerBuild = $true
            UsePrebuiltOutput = $true
        }
        $parameters.Count | Should -Be $expectedParameters.Count
        foreach ($key in $expectedParameters.Keys) {
            $parameters.ContainsKey($key) | Should -BeTrue
            $parameters[$key] | Should -Be $expectedParameters[$key]
        }
        $withoutSwitches = Get-TokenContractBuildArgument `
            -Provider self-contained -Engine postgresql `
            -EnvironmentPath '/repo/.env.e2e' -OverlayPath '/repo/overlay.env' `
            -TestFilter 'Category=@DMS-1606' -BuildConfiguration Debug
        $withoutSwitches.SkipDockerBuild | Should -BeFalse
        $withoutSwitches.UsePrebuiltOutput | Should -BeFalse
    }

    It 'propagates nonzero native build, probe, and test command results' {
        { Assert-NativeCommandSucceeded -Operation 'proxy probe' -ExitCode 0 } | Should -Not -Throw
        { Assert-NativeCommandSucceeded -Operation 'proxy probe' -ExitCode 17 } |
            Should -Throw '*proxy probe failed with exit code 17*'
        { Assert-NativeCommandSucceeded -Operation 'token test' -ExitCode 1 } |
            Should -Throw '*token test failed with exit code 1*'
    }

    It 'builds the E2E assembly for local runs and skips the build for prebuilt CI output' {
        function dotnet {
            $script:capturedDotnetArguments = @($args)
            $global:LASTEXITCODE = 0
        }

        Build-TokenContractE2ETestAssembly -ProjectPath '/repo/tests/E2E.csproj' -BuildConfiguration Release
        $script:capturedDotnetArguments | Should -Be @('build', '/repo/tests/E2E.csproj', '--configuration', 'Release', '-v', 'minimal')

        $script:capturedDotnetArguments = @()
        Build-TokenContractE2ETestAssembly -ProjectPath '/repo/tests/E2E.csproj' -BuildConfiguration Release -PrebuiltOutput
        $script:capturedDotnetArguments.Count | Should -Be 0

        function dotnet {
            $global:LASTEXITCODE = 9
        }
        { Build-TokenContractE2ETestAssembly -ProjectPath '/repo/tests/E2E.csproj' -BuildConfiguration Release } |
            Should -Throw '*failed with exit code 9*'
    }

    It 'restores the previous proxy environment after both success and failure' {
        $hadMarker = Test-Path Env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE
        $previousMarker = $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE
        $hadDmsImage = Test-Path Env:DMS_DOCKER_IMAGE
        $previousDmsImage = $env:DMS_DOCKER_IMAGE
        $hadAuthenticationService = Test-Path Env:AppSettings__AuthenticationService
        $previousAuthenticationService = $env:AppSettings__AuthenticationService
        try {
            $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE = 'previous'
            $env:DMS_DOCKER_IMAGE = 'previous-image'
            $env:AppSettings__AuthenticationService = 'http://original-provider/token'
            Invoke-WithTokenContractProxyEnvironment -Action {
                $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE | Should -Be '1'
                $env:DMS_DOCKER_IMAGE | Should -Be 'local/ed-fi-api'
                $env:AppSettings__AuthenticationService | Should -Be 'http://127.0.0.1:18987/token'
            }
            $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE | Should -Be 'previous'
            $env:DMS_DOCKER_IMAGE | Should -Be 'previous-image'
            $env:AppSettings__AuthenticationService | Should -Be 'http://original-provider/token'

            { Invoke-WithTokenContractProxyEnvironment -Action { throw 'fixture failure' } } | Should -Throw '*fixture failure*'
            $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE | Should -Be 'previous'
            $env:DMS_DOCKER_IMAGE | Should -Be 'previous-image'
            $env:AppSettings__AuthenticationService | Should -Be 'http://original-provider/token'

            Remove-Item Env:AppSettings__AuthenticationService
            Invoke-WithTokenContractProxyEnvironment -Action {
                $env:AppSettings__AuthenticationService | Should -Be 'http://127.0.0.1:18987/token'
            }
            Test-Path Env:AppSettings__AuthenticationService | Should -BeFalse

            { Invoke-WithTokenContractProxyEnvironment -Action { throw 'fixture failure' } } | Should -Throw '*fixture failure*'
            Test-Path Env:AppSettings__AuthenticationService | Should -BeFalse
        }
        finally {
            if ($hadMarker) { $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE = $previousMarker }
            else { Remove-Item Env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE -ErrorAction SilentlyContinue }
            if ($hadDmsImage) { $env:DMS_DOCKER_IMAGE = $previousDmsImage }
            else { Remove-Item Env:DMS_DOCKER_IMAGE -ErrorAction SilentlyContinue }
            if ($hadAuthenticationService) { $env:AppSettings__AuthenticationService = $previousAuthenticationService }
            else { Remove-Item Env:AppSettings__AuthenticationService -ErrorAction SilentlyContinue }
        }
    }

    It 'preserves the configured CI image when Docker builds are skipped' {
        $hadDmsImage = Test-Path Env:DMS_DOCKER_IMAGE
        $previousDmsImage = $env:DMS_DOCKER_IMAGE
        try {
            $env:DMS_DOCKER_IMAGE = 'ghcr.io/example/dms:commit-sha'
            Invoke-WithTokenContractProxyEnvironment -SkipBuild -Action {
                $env:DMS_DOCKER_IMAGE | Should -Be 'ghcr.io/example/dms:commit-sha'
            }
            $env:DMS_DOCKER_IMAGE | Should -Be 'ghcr.io/example/dms:commit-sha'

            { Invoke-WithTokenContractProxyEnvironment -SkipBuild -Action {
                $env:DMS_DOCKER_IMAGE | Should -Be 'ghcr.io/example/dms:commit-sha'
                throw 'fixture failure'
            } } | Should -Throw '*fixture failure*'
            $env:DMS_DOCKER_IMAGE | Should -Be 'ghcr.io/example/dms:commit-sha'

            Remove-Item Env:DMS_DOCKER_IMAGE
            Invoke-WithTokenContractProxyEnvironment -SkipBuild -Action {
                Test-Path Env:DMS_DOCKER_IMAGE | Should -BeFalse
            }
            Test-Path Env:DMS_DOCKER_IMAGE | Should -BeFalse
        }
        finally {
            if ($hadDmsImage) { $env:DMS_DOCKER_IMAGE = $previousDmsImage }
            else { Remove-Item Env:DMS_DOCKER_IMAGE -ErrorAction SilentlyContinue }
        }
    }

    It 'rejects zero-test, skipped, and missing-scenario TRX results' {
        $zeroPath = Join-Path $TestDrive 'zero.trx'
        '<TestRun><ResultSummary><Counters total="0" failed="0" notExecuted="0" /></ResultSummary><Results /></TestRun>' | Set-Content $zeroPath
        { Assert-TokenContractTrx -Path $zeroPath -Name root -RequiredScenarioPrefixes @('18 A JSON provider 401') } |
            Should -Throw '*selected zero test cases*'

        $skippedPath = Join-Path $TestDrive 'skipped.trx'
        '<TestRun><ResultSummary><Counters total="1" failed="0" notExecuted="1" /></ResultSummary><Results><UnitTestResult testName="18AJSONProvider401" /></Results></TestRun>' | Set-Content $skippedPath
        { Assert-TokenContractTrx -Path $skippedPath -Name root -RequiredScenarioPrefixes @('18 A JSON provider 401') } |
            Should -Throw '*not executed*'

        $missingPath = Join-Path $TestDrive 'missing-scenario.trx'
        '<TestRun><ResultSummary><Counters total="1" failed="0" notExecuted="0" /></ResultSummary><Results><UnitTestResult testName="19AnHTMLProvider401" /></Results></TestRun>' | Set-Content $missingPath
        { Assert-TokenContractTrx -Path $missingPath -Name root -RequiredScenarioPrefixes @('18 A JSON provider 401') } |
            Should -Throw '*did not contain required scenario*'
    }

    It 'excludes fixture cases from ordinary filters and preserves dedicated lane selections' {
        function Invoke-WithE2EIdentityEnvironment {
            param([string] $EnvironmentFile, [string] $IdentityProvider, [scriptblock] $Action)
            & $Action
        }
        function Invoke-WithE2ETestProcessContext {
            param([pscustomobject] $E2ETestSettings, [scriptblock] $Action)
            & $Action
        }
        function Invoke-Execute {
            param([scriptblock] $Action)
            & $Action
        }
        function RunTests {
            param([string] $TestFilter, [string] $ResultNameSuffix)
            $script:capturedE2EFilter = $TestFilter
        }

        $settings = [pscustomobject]@{ EnvironmentFile = '/repo/.env.e2e'; TestResultSuffix = 'e2e' }
        $hadMarker = Test-Path Env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE
        $previousMarker = $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE
        try {
            Remove-Item Env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE -ErrorAction SilentlyContinue
            $exclusions = '(Category!=@TokenContractProxy)&(Category!=@TokenContractQualified)'
            foreach ($filter in @('', '(Category=@e2e-ci-shard-1)',
                '(Category!=@StandardVersion-6_1)&(Category!=@DocumentCacheHostedHappyPath)&(Category!=@MssqlOnly)',
                '(Category!=@StandardVersion-6_1)&(Category!=@DocumentCacheHostedHappyPath)&(Category!=@PostgresqlOnly)',
                '(Category=@DMS-1606)|(Category=@e2e-ci-shard-1)')) {
                RunE2E -TestFilter $filter -E2ETestSettings $settings -IdentityProvider self-contained
                $expected = if ([string]::IsNullOrWhiteSpace($filter)) { $exclusions } else { "($filter)&$exclusions" }
                $script:capturedE2EFilter | Should -Be $expected
            }

            $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE = '1'
            foreach ($engine in @('postgresql', 'mssql')) {
                foreach ($qualified in @($false, $true)) {
                    $filter = Get-TokenContractTestFilter -Engine $engine -Qualified:$qualified
                    RunE2E -TestFilter $filter -E2ETestSettings $settings -IdentityProvider self-contained
                    $script:capturedE2EFilter | Should -Be $filter
                }
            }
        }
        finally {
            if ($hadMarker) { $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE = $previousMarker }
            else { Remove-Item Env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE -ErrorAction SilentlyContinue }
        }
    }

    It 'leaves compiled settings unchanged during successful and failed proxy runs' {
        $settingsPath = Join-Path $TestDrive 'appsettings.json'
        $originalBytes = [Text.Encoding]::UTF8.GetBytes("{`n  `"AuthenticationService`": `"http://original-provider/token`"`n}`n")
        [IO.File]::WriteAllBytes($settingsPath, $originalBytes)
        $settingsDirectory = $TestDrive

        Invoke-WithTokenContractProxyEnvironment -Action {
            SetAuthenticationServiceURL -E2EDirectory $settingsDirectory
            $env:AppSettings__AuthenticationService | Should -Be 'http://127.0.0.1:18987/token'
        }
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($settingsPath)) | Should -BeExactly ([Convert]::ToBase64String($originalBytes))

        { Invoke-WithTokenContractProxyEnvironment -Action {
            SetAuthenticationServiceURL -E2EDirectory $settingsDirectory
            throw 'fixture failure'
        } } | Should -Throw '*fixture failure*'
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($settingsPath)) | Should -BeExactly ([Convert]::ToBase64String($originalBytes))
    }

    It 'keeps ordinary authentication settings for both supported providers' {
        $settingsPath = Join-Path $TestDrive 'appsettings.json'
        '{"AuthenticationService":"unchanged"}' | Set-Content $settingsPath
        $hadMarker = Test-Path Env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE
        $previousMarker = $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE
        try {
            Remove-Item Env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE -ErrorAction SilentlyContinue
            $script:IdentityProvider = 'self-contained'
            SetAuthenticationServiceURL -E2EDirectory $TestDrive
            (Get-Content $settingsPath -Raw | ConvertFrom-Json).AuthenticationService | Should -Be 'http://ed-fi-api-config:8081/connect/token'

            $script:IdentityProvider = 'keycloak'
            SetAuthenticationServiceURL -E2EDirectory $TestDrive
            (Get-Content $settingsPath -Raw | ConvertFrom-Json).AuthenticationService | Should -Be 'http://dms-keycloak:8080/realms/edfi/protocol/openid-connect/token'
        }
        finally {
            if ($hadMarker) { $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE = $previousMarker }
            else { Remove-Item Env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE -ErrorAction SilentlyContinue }
        }
    }
}
