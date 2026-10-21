# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

BeforeAll {
    $script:launcherPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'Invoke-TokenContractE2E.ps1'))
    $script:launcherText = Get-Content $script:launcherPath -Raw
    $parseErrors = $null
    $tokens = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:launcherPath, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw "Launcher parse failed: $($parseErrors[0].Message)" }

    foreach ($functionName in @(
        'Get-TokenContractOverlayLines',
        'Get-TokenContractUpstreamUri',
        'Get-TokenContractTestFilter',
        'Get-TokenContractBuildArguments',
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
    It 'is present and exposes the planned provider, engine, environment, and build switches' {
        Test-Path $script:launcherPath | Should -BeTrue
        $script:launcherText | Should -Match 'IdentityProvider'
        $script:launcherText | Should -Match 'DatabaseEngine'
        $script:launcherText | Should -Match 'EnvironmentFile'
        $script:launcherText | Should -Match 'SkipDockerBuild'
        $script:launcherText | Should -Match 'UsePrebuiltOutput'
    }

    It 'overlays only the selected container token endpoint and correlation input header' {
        $selfContained = @(Get-TokenContractOverlayLines -Provider self-contained)
        $keycloak = @(Get-TokenContractOverlayLines -Provider keycloak)

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
        $lines = @(Get-TokenContractOverlayLines -Provider self-contained -Qualified)
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

    It 'binds the build script parameters by name and forwards requested switches' {
        $parameters = Get-TokenContractBuildArguments `
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
        $withoutSwitches = Get-TokenContractBuildArguments `
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

    It 'restores the previous test marker after both success and failure' {
        $hadMarker = Test-Path Env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE
        $previousMarker = $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE
        $hadDmsImage = Test-Path Env:DMS_DOCKER_IMAGE
        $previousDmsImage = $env:DMS_DOCKER_IMAGE
        try {
            $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE = 'previous'
            $env:DMS_DOCKER_IMAGE = 'previous-image'
            Invoke-WithTokenContractProxyEnvironment -Action {
                $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE | Should -Be '1'
                $env:DMS_DOCKER_IMAGE | Should -Be 'local/ed-fi-api'
            }
            $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE | Should -Be 'previous'
            $env:DMS_DOCKER_IMAGE | Should -Be 'previous-image'

            { Invoke-WithTokenContractProxyEnvironment -Action { throw 'fixture failure' } } | Should -Throw '*fixture failure*'
            $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE | Should -Be 'previous'
            $env:DMS_DOCKER_IMAGE | Should -Be 'previous-image'
        }
        finally {
            if ($hadMarker) { $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE = $previousMarker }
            else { Remove-Item Env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE -ErrorAction SilentlyContinue }
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

    It 'keeps host-run authentication on the proxy loopback port only during the dedicated lane' {
        $settingsPath = Join-Path $TestDrive 'appsettings.json'
        '{"AuthenticationService":"unchanged"}' | Set-Content $settingsPath
        $hadMarker = Test-Path Env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE
        $previousMarker = $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE
        $script:IdentityProvider = 'self-contained'
        try {
            $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE = '1'
            SetAuthenticationServiceURL -E2EDirectory $TestDrive
            (Get-Content $settingsPath -Raw | ConvertFrom-Json).AuthenticationService | Should -Be 'http://127.0.0.1:18987/token'

            Remove-Item Env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE -ErrorAction SilentlyContinue
            SetAuthenticationServiceURL -E2EDirectory $TestDrive
            (Get-Content $settingsPath -Raw | ConvertFrom-Json).AuthenticationService | Should -Be 'http://ed-fi-api-config:8081/connect/token'
        }
        finally {
            if ($hadMarker) { $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE = $previousMarker }
            else { Remove-Item Env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE -ErrorAction SilentlyContinue }
        }
    }
}
