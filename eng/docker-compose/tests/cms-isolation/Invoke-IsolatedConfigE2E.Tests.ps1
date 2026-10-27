# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalVars', '', Justification = 'Native-command stubs called from another script scope share test-owned capture variables; AfterEach removes them.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseDeclaredVarsMoreThanAssignments', '', Justification = 'EnvironmentFile and ClaimsFixtureDirectory are consumed by the real E2ETests function loaded from its AST.')]
[CmdletBinding()]
param()

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'IsolatedConfigE2E.psm1') -Force
    function Write-TestTrx {
        param([string] $Path, [string] $Name = 'It_checks_policy', [string] $Outcome = 'Passed', [datetime] $Started = [datetime]::UtcNow)
        Set-Content -LiteralPath $Path -Value "<TestRun><Times start='$($Started.ToString('O'))'/><Results><UnitTestResult testName='$Name' outcome='$Outcome'/></Results></TestRun>"
    }
}

Describe 'Isolated CMS result lifecycle' {
    BeforeEach {
        $script:runRoot = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory $script:runRoot
        $script:workingTrx = Join-Path $script:runRoot 'suite.trx'
        $script:artifacts = Join-Path $script:runRoot 'postgresql/Hybrid/5.2/run1'
        $script:events = [Collections.Generic.List[string]]::new()
        $script:invokeArguments = @{
            WorkingTrx = $script:workingTrx
            ArtifactDirectory = $script:artifacts
            ExpectedTestNames = @('It_checks_policy')
            Execute = {
                Test-Path $script:workingTrx | Should -BeFalse
                $script:events.Add('execute')
                Write-TestTrx $script:workingTrx
                Set-Content (Join-Path $script:artifacts 'comparison.txt') 'CMS authorization metadata matches.'
            }
            Teardown = {
                Test-Path (Join-Path $script:artifacts 'run-status.txt') | Should -BeTrue
                $script:events.Add('teardown')
            }
        }
    }

    It 'preserves ordinary results and comparison evidence before teardown' {
        Set-Content $script:workingTrx 'ordinary-suite-results'
        Invoke-IsolatedCmsTestRun @script:invokeArguments
        (Get-Content (Join-Path $script:artifacts 'previous-results.trx') -Raw).Trim() | Should -Be 'ordinary-suite-results'
        (Get-Content (Join-Path $script:artifacts 'results.trx') -Raw) | Should -Match 'It_checks_policy'
        (Get-Content (Join-Path $script:artifacts 'comparison.txt') -Raw).Trim() | Should -Be 'CMS authorization metadata matches.'
        $script:events | Should -Be @('execute', 'teardown')
    }

    It 'retains distinct results for sequential scenarios and versions' {
        Invoke-IsolatedCmsTestRun @script:invokeArguments
        $first = $script:artifacts
        $script:artifacts = Join-Path $script:runRoot 'mssql/Drift/6.1/run2'
        $script:invokeArguments.ArtifactDirectory = $script:artifacts
        Invoke-IsolatedCmsTestRun @script:invokeArguments
        Test-Path (Join-Path $first 'results.trx') | Should -BeTrue
        Test-Path (Join-Path $script:artifacts 'results.trx') | Should -BeTrue
        Test-Path (Join-Path $script:artifacts 'previous-results.trx') | Should -BeTrue
        Test-Path (Join-Path $first 'comparison.txt') | Should -BeTrue
    }

    It 'preserves intentional skips in ordinary suites while still requiring an executed passing test' {
        $script:invokeArguments.ExpectedTestNames = @()
        $script:invokeArguments.Execute = {
            Set-Content $script:workingTrx "<TestRun><Times start='$([datetime]::UtcNow.ToString('O'))'/><Results><UnitTestResult testName='It_checks_policy' outcome='Passed'/><UnitTestResult testName='OtherBackend' outcome='NotExecuted'/></Results></TestRun>"
        }
        Invoke-IsolatedCmsTestRun @script:invokeArguments
        (Get-Content (Join-Path $script:artifacts 'run-status.txt') -Raw).Trim() | Should -Be 'Passed'
    }

    It 'rejects missing results rather than accepting the previous ordinary run' {
        Write-TestTrx $script:workingTrx
        $script:invokeArguments.Execute = { }
        { Invoke-IsolatedCmsTestRun @script:invokeArguments } | Should -Throw '*no TRX results*'
        Test-Path (Join-Path $script:artifacts 'previous-results.trx') | Should -BeTrue
        Test-Path (Join-Path $script:artifacts 'results.trx') | Should -BeFalse
        $script:events | Should -Be @('teardown')
    }

    It 'rejects a stale result copied back during execution and archives it' {
        $script:invokeArguments.Execute = { Write-TestTrx $script:workingTrx -Started ([datetime]::UtcNow.AddDays(-1)) }
        { Invoke-IsolatedCmsTestRun @script:invokeArguments } | Should -Throw '*stale TRX*'
        Test-Path (Join-Path $script:artifacts 'results.trx') | Should -BeTrue
        $script:events | Should -Be @('teardown')
    }

    It 'rejects <Outcome> tests and retains their failure evidence' -TestCases @(@{ Outcome = 'Failed' }, @{ Outcome = 'NotExecuted' }) {
        param($Outcome)
        $script:outcome = $Outcome
        $script:invokeArguments.Execute = { Write-TestTrx $script:workingTrx -Outcome $script:outcome }
        { Invoke-IsolatedCmsTestRun @script:invokeArguments } | Should -Throw '*execute and pass*'
        (Get-Content (Join-Path $script:artifacts 'results.trx') -Raw) | Should -Match $Outcome
        $script:events | Should -Be @('teardown')
    }

    It 'rejects zero results' {
        $script:invokeArguments.Execute = {
            Set-Content $script:workingTrx "<TestRun><Times start='$([datetime]::UtcNow.ToString('O'))'/><Results/></TestRun>"
        }
        { Invoke-IsolatedCmsTestRun @script:invokeArguments } | Should -Throw '*execute and pass*'
    }

    It 'rejects an unintended test even when it passed' {
        $script:invokeArguments.Execute = { Write-TestTrx $script:workingTrx -Name 'It_checks_something_else' }
        { Invoke-IsolatedCmsTestRun @script:invokeArguments } | Should -Throw '*requested test*'
    }

    It 'rejects extra results rather than accepting a broader filter' {
        $script:invokeArguments.Execute = {
            Set-Content $script:workingTrx "<TestRun><Times start='$([datetime]::UtcNow.ToString('O'))'/><Results><UnitTestResult testName='It_checks_policy' outcome='Passed'/><UnitTestResult testName='Other' outcome='Passed'/></Results></TestRun>"
        }
        { Invoke-IsolatedCmsTestRun @script:invokeArguments } | Should -Throw '*exactly the requested scenario*'
    }

    It 'archives failing results and tears down after an injected execution failure without logging its secrets' {
        $script:invokeArguments.Execute = {
            Write-TestTrx $script:workingTrx -Outcome 'Failed'
            throw 'injected credential=never-log-this'
        }
        { Invoke-IsolatedCmsTestRun @script:invokeArguments } | Should -Throw '*injected*'
        Test-Path (Join-Path $script:artifacts 'results.trx') | Should -BeTrue
        (Get-Content (Join-Path $script:artifacts 'run-status.txt') -Raw) | Should -Not -Match 'never-log-this'
        $script:events | Should -Be @('teardown')
    }

    It 'archives malformed results and still tears down' {
        $script:invokeArguments.Execute = { Set-Content $script:workingTrx 'not xml' }
        { Invoke-IsolatedCmsTestRun @script:invokeArguments } | Should -Throw
        (Get-Content (Join-Path $script:artifacts 'results.trx') -Raw).Trim() | Should -Be 'not xml'
        $script:events | Should -Be @('teardown')
    }
}

Describe 'Claims fixture preparation' {
    It 'copies nested fixture bytes without changing the source' {
        $source = Join-Path $TestDrive 'source'
        $destination = Join-Path $TestDrive 'prepared'
        $null = New-Item -ItemType Directory (Join-Path $source 'nested') -Force
        $bytes = [byte[]]@(239,187,191,123,13,10,125)
        [IO.File]::WriteAllBytes((Join-Path $source 'nested/input-claims.json'), $bytes)
        Copy-IsolatedClaimsFixture $source $destination | Should -Be $destination
        [IO.File]::ReadAllBytes((Join-Path $destination 'nested/input-claims.json')) | Should -Be $bytes
        [IO.File]::ReadAllBytes((Join-Path $source 'nested/input-claims.json')) | Should -Be $bytes
    }

    It 'rejects a missing fixture directory' {
        { Copy-IsolatedClaimsFixture (Join-Path $TestDrive 'absent') (Join-Path $TestDrive 'unused') } | Should -Throw '*existing directory*'
    }
}

Describe 'build-config claims mount selection' {
    BeforeAll {
        # Exercise the actual E2ETests body without executing the build script's command dispatch.
        $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
        $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'build-config.ps1'), [ref]$null, [ref]$null)
        $functionAst = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'E2ETests' }, $true)
        . ([scriptblock]::Create($functionAst.Extent.Text))
        function Invoke-Execute { param([scriptblock] $Action) & $Action }
        function Invoke-Step { param([scriptblock] $Action) $null = $Action }
        function PublishEffectiveRoleClaimSettings { }
    }

    BeforeEach {
        $script:mountRoot = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $compose = Join-Path $script:mountRoot 'eng/docker-compose'
        $null = New-Item -ItemType Directory $compose -Force
        Set-Content (Join-Path $compose 'start-local-config.ps1') @'
param($EnvironmentFile, $IdentityProvider, [switch] $AddE2EClaimSets, [switch] $r)
$global:CapturedCmsStartup = @{ AddE2EClaimSets = $AddE2EClaimSets.IsPresent; Mount = $env:CONFIGURATION_SERVICE_CLAIMS_MOUNT_SOURCE; Rebuild = $r.IsPresent }
if ($env:CMS_TEST_INJECT_FAILURE -eq 'true') { throw 'injected startup failure' }
'@
        Set-Content (Join-Path $compose 'env-utility.psm1') 'function ReadValuesFromEnvFile { param($Path) return @{} }; Export-ModuleMember -Function ReadValuesFromEnvFile'
        $script:priorMount = [Environment]::GetEnvironmentVariable('CONFIGURATION_SERVICE_CLAIMS_MOUNT_SOURCE')
        $script:priorValues = @{}
        foreach ($name in @('CONFIGURATION_SERVICE_DATASTORE', 'CONFIGURATION_SERVICE_MULTI_TENANCY', 'POSTGRES_PASSWORD', 'POSTGRES_PORT', 'POSTGRES_DB_NAME', 'MSSQL_SA_PASSWORD', 'MSSQL_PORT', 'CONFIGURATION_SERVICE_E2E_API_URL')) {
            $script:priorValues[$name] = [Environment]::GetEnvironmentVariable($name)
        }
        $env:CONFIGURATION_SERVICE_CLAIMS_MOUNT_SOURCE = 'caller-mount'
        $EnvironmentFile = 'test.env'
        $ClaimsFixtureDirectory = ''
        Push-Location $script:mountRoot
    }

    AfterEach {
        Pop-Location
        $script:priorValues['CONFIGURATION_SERVICE_CLAIMS_MOUNT_SOURCE'] = $script:priorMount
        foreach ($name in $script:priorValues.Keys) {
            if ($null -eq $script:priorValues[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
            else { [Environment]::SetEnvironmentVariable($name, $script:priorValues[$name]) }
        }
        Remove-Item Env:CMS_TEST_INJECT_FAILURE -ErrorAction SilentlyContinue
        Remove-Variable CapturedCmsStartup -Scope Global -ErrorAction SilentlyContinue
    }

    It 'keeps default E2E staging enabled' {
        E2ETests -SkipDockerBuild -IdentityProvider 'self-contained'
        $global:CapturedCmsStartup.AddE2EClaimSets | Should -BeTrue
        $global:CapturedCmsStartup.Rebuild | Should -BeFalse
        $env:CONFIGURATION_SERVICE_CLAIMS_MOUNT_SOURCE | Should -Be 'caller-mount'
    }

    It 'mounts an explicit relative fixture and bypasses default staging' {
        $null = New-Item -ItemType Directory 'fixture'
        $ClaimsFixtureDirectory = './fixture'
        E2ETests -IdentityProvider 'self-contained'
        $global:CapturedCmsStartup.AddE2EClaimSets | Should -BeFalse
        $global:CapturedCmsStartup.Rebuild | Should -BeTrue
        $global:CapturedCmsStartup.Mount | Should -Be (Join-Path $script:mountRoot 'fixture')
        $env:CONFIGURATION_SERVICE_CLAIMS_MOUNT_SOURCE | Should -Be 'caller-mount'
    }

    It 'restores the mount and location after startup failure' {
        $null = New-Item -ItemType Directory 'fixture'
        $ClaimsFixtureDirectory = './fixture'
        $env:CMS_TEST_INJECT_FAILURE = 'true'
        { E2ETests -IdentityProvider 'self-contained' } | Should -Throw '*injected startup failure*'
        $global:CapturedCmsStartup.AddE2EClaimSets | Should -BeFalse
        $env:CONFIGURATION_SERVICE_CLAIMS_MOUNT_SOURCE | Should -Be 'caller-mount'
        (Get-Location).Path | Should -Be $script:mountRoot
    }

    It 'rejects a missing explicit directory without falling back to default staging' {
        $ClaimsFixtureDirectory = './missing'
        { E2ETests -IdentityProvider 'self-contained' } | Should -Throw '*existing directory*'
        Get-Variable CapturedCmsStartup -Scope Global -ErrorAction SilentlyContinue | Should -BeNullOrEmpty
        $env:CONFIGURATION_SERVICE_CLAIMS_MOUNT_SOURCE | Should -Be 'caller-mount'
    }
}

Describe 'Isolated launcher environment and cleanup' {
    BeforeAll {
        # Run the launcher itself in a tiny checkout; replace only native/build boundaries.
        function dotnet {
            if ($args[0] -eq 'tool') {
                $global:CmsLauncherinstallArguments = @($args)
                $toolPath = $args[[array]::IndexOf($args, '--tool-path') + 1]
                $global:CmsLauncherinstallScratch = Split-Path $toolPath
                $global:CmsLauncherinstallCache = $env:NUGET_PACKAGES
                $null = New-Item -ItemType Directory $toolPath -Force
                $null = New-Item -ItemType Directory $env:NUGET_PACKAGES -Force
                Set-Content (Join-Path $toolPath $(if ($IsWindows) { 'security-metadata-tools.exe' } else { 'security-metadata-tools' })) 'installed shim'
                $config = $args[[array]::IndexOf($args, '--configfile') + 1]
                [xml] $configuration = Get-Content $config -Raw
                @($configuration.configuration.packageSources.add).Count | Should -Be 1
                $configuration.configuration.packageSources.add.key | Should -Be 'local'
                $configuration.configuration.packageSources.add.value | Should -Be (Join-Path $global:CmsLauncherinstallScratch 'feed')
                $configuration.SelectSingleNode('/configuration/packageSources/clear') | Should -Not -BeNullOrEmpty
                $configuration.SelectSingleNode('/configuration/fallbackPackageFolders/clear') | Should -Not -BeNullOrEmpty
                $configuration.SelectSingleNode('/configuration/packageSourceMapping/clear') | Should -Not -BeNullOrEmpty
                $global:LASTEXITCODE = if ($global:CmsLauncherinstallFailure) { 1 } else { 0 }
                return
            }
            if ($args[0] -eq 'test') {
                $logger = $args | Where-Object { $_ -like 'trx;LogFileName=*' }
                $path = $logger.Substring('trx;LogFileName='.Length)
                Set-Content $path "<TestRun><Results><UnitTestResult testName='It_sends_requests_to_the_selected_configuration_service' outcome='Passed'/><UnitTestResult testName='It_cleans_the_selected_database' outcome='Passed'/></Results></TestRun>"
            }
            if ($args -contains 'merge') {
                $global:CmsLaunchermergeArguments = @($args)
                $global:CmsLauncherprepared = $env:CONFIGURATION_SERVICE_CLAIMS_MOUNT_SOURCE
                @(Get-ChildItem -LiteralPath $global:CmsLauncherprepared -Force).Count | Should -Be 0
                if ($global:CmsLaunchermergeFailure) {
                    $global:LASTEXITCODE = 1
                    return
                }
                $outputPath = $args[[array]::IndexOf($args, '--output') + 1]
                [IO.File]::WriteAllBytes($outputPath, [byte[]]@(239,187,191,123,13,10,125))
            }
            $global:LASTEXITCODE = 0
        }
        function docker { $global:LASTEXITCODE = 0 }
        function pwsh {
            if ($args -match 'Test-SecurityMetadataToolsPackage.ps1$') {
                $global:CmsLauncherpackageArguments = @($args)
                Set-Content (Join-Path $global:CmsLaunchercheckout 'EdFi.Api.SecurityMetadataTools.8.1.0-alpha.0.42.nupkg') 'validated package'
                $global:LASTEXITCODE = if ($global:CmsLauncherpackageFailure) { 1 } else { 0 }
                return
            }
            $global:CmsLaunchercapturedPolicy = @{}
            foreach ($name in @('CONFIGURATION_SERVICE_CLAIMS_SOURCE', 'CONFIGURATION_SERVICE_DATA_STANDARD_VERSION', 'CONFIGURATION_SERVICE_CLAIMS_DIRECTORY', 'CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING', 'CONFIGURATION_SERVICE_CLAIMS_MOUNT_SOURCE', 'CONFIGURATION_SERVICE_E2E_API_URL', 'POSTGRES_PORT', 'SECURITY_METADATA_TOOLS_SERVICE_RUNNER', 'SECURITY_METADATA_TOOLS_USE_INSTALLED_TOOL', 'SECURITY_METADATA_TOOLS_INSTALLED_EXECUTABLE', 'SECURITY_METADATA_TOOLS_EXPECTED_VERSION')) {
                $global:CmsLaunchercapturedPolicy[$name] = [Environment]::GetEnvironmentVariable($name)
            }
            $global:CmsLaunchercapturedArguments = @($args)
            $global:CmsLauncherprepared = $env:CONFIGURATION_SERVICE_CLAIMS_MOUNT_SOURCE
            if ($global:CmsLaunchermergeArguments) {
                @(Get-ChildItem -LiteralPath $global:CmsLauncherprepared -Force).Name | Should -Be @('Claims.json')
                [IO.File]::ReadAllBytes((Join-Path $global:CmsLauncherprepared 'Claims.json')) | Should -Be ([byte[]]@(239,187,191,123,13,10,125))
                if ($global:CmsLaunchermutateInput) { Add-Content (Join-Path $global:CmsLauncherprepared 'Claims.json') 'changed' }
            } elseif ($args -match 'SecurityMetadataToolsDrift') {
                [IO.File]::ReadAllBytes((Join-Path $global:CmsLauncherprepared 'Claims.json')) | Should -Be ([byte[]]@(123,125,10))
                if ($global:CmsLaunchermutateInput) { Add-Content (Join-Path $global:CmsLauncherprepared 'Claims.json') 'changed' }
            } elseif ($args -match 'SecurityMetadataToolsUpload') {
                @(Get-ChildItem -LiteralPath $global:CmsLauncherprepared -Force).Count | Should -Be 0
            } else {
                [IO.File]::ReadAllBytes((Join-Path $global:CmsLauncherprepared 'input-claims.json')) | Should -Be ([byte[]]@(123,125,10))
            }
            $env:NODE_OPTIONS | Should -BeNullOrEmpty
            $trxPath = Join-Path $global:CmsLaunchercheckout 'TestResults/EdFi.DmsConfigurationService.Tests.E2E.trx'
            $null = New-Item -ItemType Directory (Split-Path $trxPath) -Force
            $testName = if ($args -contains '-E2ETestFilter') {
                $filter = $args[[array]::IndexOf($args, '-E2ETestFilter') + 1]
                if ($filter -match 'Name=(?<Name>[^)]+)') { $Matches.Name } else { 'It_checks_policy' }
            } else { 'It_checks_policy' }
            if ($global:CmsLauncherwrongTest) { $testName = 'Wrong_test' }
            Write-TestTrx $trxPath -Name $testName -Outcome $(if ($global:CmsLauncherinjectFailure) { 'Failed' } else { 'Passed' })
            Set-Content (Join-Path $env:SECURITY_METADATA_TOOLS_EVIDENCE_DIRECTORY 'comparison.txt') 'sanitized policy evidence'
            $global:LASTEXITCODE = if ($global:CmsLauncherinjectFailure) { 1 } else { 0 }
        }
    }

    BeforeEach {
        $global:CmsLaunchercheckout = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $launcherDirectory = Join-Path $global:CmsLaunchercheckout 'eng/docker-compose/tests/cms-isolation'
        $null = New-Item -ItemType Directory $launcherDirectory -Force
        Copy-Item (Join-Path $PSScriptRoot 'Invoke-IsolatedConfigE2E.ps1') $launcherDirectory
        Copy-Item (Join-Path $PSScriptRoot 'IsolatedConfigE2E.psm1') $launcherDirectory
        $verification = Join-Path $global:CmsLaunchercheckout 'eng/verification'
        $null = New-Item -ItemType Directory $verification
        Copy-Item (Join-Path $PSScriptRoot '../../../verification/SecurityMetadataToolsPackage.psm1') $verification
        # Modules have their own command lookup scope; replace only the native boundary in this copy.
        Add-Content (Join-Path $verification 'SecurityMetadataToolsPackage.psm1') ("`nfunction dotnet {`n" + ${function:dotnet}.ToString() + "`n}")
        Set-Content (Join-Path $global:CmsLaunchercheckout 'package-helpers.psm1') @'
function Get-SecurityMetadataToolsVersion {
    param($AppVersion)
    if ($AppVersion -ne '8.1.9-alpha.0.42') { throw 'Wrong application version supplied' }
    return '8.1.0-alpha.0.42'
}
Export-ModuleMember -Function Get-SecurityMetadataToolsVersion
'@
        Set-Content (Join-Path $global:CmsLaunchercheckout 'eng/build-helpers.psm1') @'
function Get-RequiredTestAssembly {
    param($SolutionRoot, $Filter, $Configuration)
    return [IO.FileInfo]::new((Join-Path $SolutionRoot 'fake.Tests.E2E.dll'))
}
Export-ModuleMember -Function Get-RequiredTestAssembly
'@
        Set-Content (Join-Path $global:CmsLaunchercheckout 'eng/docker-compose/start-local-config.ps1') @'
param([switch] $d, [switch] $v, $EnvironmentFile)
$global:CmsTeardownCalls++
$global:LASTEXITCODE = 0
'@
        Set-Content (Join-Path $global:CmsLaunchercheckout 'eng/docker-compose/test.env') @'
CONFIGURATION_SERVICE_DATASTORE=postgresql
POSTGRES_DB_NAME=test_cms
CONFIGURATION_SERVICE_CLAIMS_SOURCE=Embedded
CONFIGURATION_SERVICE_DATA_STANDARD_VERSION=5.2
CONFIGURATION_SERVICE_CLAIMS_DIRECTORY=wrong
CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=true
CONFIGURATION_SERVICE_CLAIMS_MOUNT_SOURCE=wrong
'@
        $fixture = Join-Path $global:CmsLaunchercheckout 'fixture'
        $null = New-Item -ItemType Directory $fixture
        [IO.File]::WriteAllBytes((Join-Path $fixture 'input-claims.json'), [byte[]]@(123,125,10))
        $global:CmsLauncherlauncherArguments = @{
            EnvironmentFile = './test.env'
            ClaimsFixtureDirectory = $fixture
            ClaimsSource = 'Filesystem'
            DataStandardVersion = '6.1'
            E2ETestFilter = 'TestCategory=FutureScenario'
        }
        $global:CmsLauncheroriginalNodeOptions = [Environment]::GetEnvironmentVariable('NODE_OPTIONS')
        $env:NODE_OPTIONS = '--test-unsupported-option'
        $global:CmsLauncherbeforeEnvironment = @{}
        Get-ChildItem Env: | ForEach-Object { $global:CmsLauncherbeforeEnvironment[$_.Name] = $_.Value }
        $global:CmsTeardownCalls = 0
        $global:CmsLauncherinjectFailure = $false
        $global:CmsLauncherwrongTest = $false
        $global:CmsLaunchermergeArguments = @()
        $global:CmsLaunchermergeFailure = $false
        $global:CmsLaunchermutateInput = $false
        $global:CmsLauncherinstallFailure = $false
        $global:CmsLauncherpackageFailure = $false
        $global:CmsLauncherinstallScratch = ''
        $global:CmsLauncherinstallArguments = @()
        $global:CmsLauncherpackageArguments = @()
        $global:CmsLauncherlauncher = Join-Path $launcherDirectory 'Invoke-IsolatedConfigE2E.ps1'
    }

    AfterEach {
        # Imported copies must not contaminate other Pester files that mock the real named modules.
        Get-Module | Where-Object { $_.Path.StartsWith($global:CmsLaunchercheckout + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal) } | Remove-Module
        if ($null -eq $global:CmsLauncheroriginalNodeOptions) { Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue }
        else { $env:NODE_OPTIONS = $global:CmsLauncheroriginalNodeOptions }
        Remove-Variable CmsTeardownCalls -Scope Global -ErrorAction SilentlyContinue
        Get-Variable CmsLauncher* -Scope Global | Remove-Variable -Scope Global
    }

    It 'pins policy settings in both environments and restores settings after success' {
        & $global:CmsLauncherlauncher @global:CmsLauncherlauncherArguments
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_CLAIMS_SOURCE | Should -Be 'Filesystem'
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_DATA_STANDARD_VERSION | Should -Be '6.1'
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_CLAIMS_DIRECTORY | Should -Be '/app/additional-claims'
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING | Should -Be 'false'
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_E2E_API_URL | Should -Be 'http://localhost:8191'
        $global:CmsLaunchercapturedPolicy.POSTGRES_PORT | Should -Be '5536'
        $global:CmsLaunchercapturedPolicy.SECURITY_METADATA_TOOLS_SERVICE_RUNNER | Should -Be 'true'
        $global:CmsLaunchercapturedPolicy.SECURITY_METADATA_TOOLS_USE_INSTALLED_TOOL | Should -Be 'false'
        $global:CmsLaunchercapturedPolicy.SECURITY_METADATA_TOOLS_INSTALLED_EXECUTABLE | Should -BeNullOrEmpty
        $global:CmsLaunchercapturedArguments | Should -Contain '-ClaimsFixtureDirectory'
        $global:CmsLaunchercapturedArguments | Should -Contain $global:CmsLauncherprepared
        $derived = Get-Content (Join-Path $global:CmsLaunchercheckout 'eng/docker-compose/.derived/test.env.isolated')
        foreach ($name in $global:CmsLaunchercapturedPolicy.Keys | Where-Object { $_ -notlike 'SECURITY_*' }) {
            $derived | Should -Contain "$name=$($global:CmsLaunchercapturedPolicy[$name])"
        }
        $global:CmsTeardownCalls | Should -Be 2
        Test-Path $global:CmsLauncherprepared | Should -BeFalse
        @(Get-ChildItem (Join-Path $global:CmsLaunchercheckout 'TestResults/cms-isolation/postgresql/CustomFixture/6.1') -Recurse -Filter results.trx).Count | Should -Be 1
        $after = @{}
        Get-ChildItem Env: | ForEach-Object { $after[$_.Name] = $_.Value }
        ($after | ConvertTo-Json -Compress) | Should -Be ($global:CmsLauncherbeforeEnvironment | ConvertTo-Json -Compress)
    }

    It 'selects exactly the Hybrid <Version> case and retains versioned evidence' -TestCases @(@{ Version = '5.2' }, @{ Version = '6.1' }) {
        param($Version)
        $fixture = Join-Path $global:CmsLaunchercheckout 'src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/TestData/Claims/SecurityMetadataTools/Hybrid/fragments'
        $null = New-Item -ItemType Directory $fixture -Force
        [IO.File]::WriteAllBytes((Join-Path $fixture 'input-claims.json'), [byte[]]@(123,125,10))
        & $global:CmsLauncherlauncher -EnvironmentFile './test.env' -SecurityMetadataToolsScenario Hybrid -DataStandardVersion $Version -SkipDockerBuild
        $expectedName = "It_matches_reviewed_Hybrid_permissions_for_DS_$($Version.Replace('.', '_'))"
        $global:CmsLaunchercapturedArguments | Should -Contain "(TestCategory=SecurityMetadataToolsService)&(TestCategory=SecurityMetadataToolsHybrid)&(Name=$expectedName)"
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_CLAIMS_SOURCE | Should -Be 'Hybrid'
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_DATA_STANDARD_VERSION | Should -Be $Version
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING | Should -Be 'false'
        $results = @(Get-ChildItem (Join-Path $global:CmsLaunchercheckout "TestResults/cms-isolation/postgresql/Hybrid/$Version") -Recurse -Filter results.trx)
        $results.Count | Should -Be 1
        (Get-Content $results[0].FullName -Raw) | Should -Match $expectedName
        $global:CmsTeardownCalls | Should -Be 2
        Test-Path $global:CmsLauncherprepared | Should -BeFalse
        $after = @{}
        Get-ChildItem Env: | ForEach-Object { $after[$_.Name] = $_.Value }
        ($after | ConvertTo-Json -Compress) | Should -Be ($global:CmsLauncherbeforeEnvironment | ConvertTo-Json -Compress)
    }

    It 'merges before Filesystem startup, mounts exact bytes only, and restores the environment' {
        $fixture = Join-Path $global:CmsLaunchercheckout 'src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/TestData/Claims/SecurityMetadataTools/Hybrid/fragments'
        $null = New-Item -ItemType Directory $fixture -Force
        [IO.File]::WriteAllBytes((Join-Path $fixture 'input-claims.json'), [byte[]]@(123,125,10))
        & $global:CmsLauncherlauncher -EnvironmentFile './test.env' -SecurityMetadataToolsScenario Filesystem -DataStandardVersion '5.2' -SkipDockerBuild
        $global:CmsLaunchermergeArguments | Should -Contain $fixture
        $global:CmsLaunchermergeArguments | Should -Contain '5.2'
        $global:CmsLaunchercapturedArguments | Should -Contain '(TestCategory=SecurityMetadataToolsService)&(TestCategory=SecurityMetadataToolsFilesystem)&(Name=It_loads_unmodified_merge_output_through_Filesystem_startup)'
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_CLAIMS_SOURCE | Should -Be 'Filesystem'
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_DATA_STANDARD_VERSION | Should -Be '5.2'
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING | Should -Be 'false'
        $results = @(Get-ChildItem (Join-Path $global:CmsLaunchercheckout 'TestResults/cms-isolation/postgresql/Filesystem/5.2') -Recurse -Filter results.trx)
        $results.Count | Should -Be 1
        $evidence = $results[0].DirectoryName
        [IO.File]::ReadAllBytes((Join-Path $evidence 'merged.json')) | Should -Be ([byte[]]@(239,187,191,123,13,10,125))
        (Get-Content (Join-Path $evidence 'source-sha256-before.json') -Raw) | Should -Be (Get-Content (Join-Path $evidence 'source-sha256-after.json') -Raw)
        $global:CmsTeardownCalls | Should -Be 2
        Test-Path $global:CmsLauncherprepared | Should -BeFalse
        $after = @{}
        Get-ChildItem Env: | ForEach-Object { $after[$_.Name] = $_.Value }
        ($after | ConvertTo-Json -Compress) | Should -Be ($global:CmsLauncherbeforeEnvironment | ConvertTo-Json -Compress)
    }

    It 'starts Upload base-only with its gate enabled and cleans up after <Outcome>' -TestCases @(
        @{ Outcome = 'success'; Fails = $false }, @{ Outcome = 'failure'; Fails = $true }
    ) {
        param($Outcome, $Fails)
        $global:CmsLauncherinjectFailure = $Fails
        if ($Outcome -eq 'failure') {
            { & $global:CmsLauncherlauncher -EnvironmentFile './test.env' -SecurityMetadataToolsScenario Upload -DataStandardVersion '5.2' -SkipDockerBuild } | Should -Throw '*execution failed*'
        } else {
            & $global:CmsLauncherlauncher -EnvironmentFile './test.env' -SecurityMetadataToolsScenario Upload -DataStandardVersion '5.2' -SkipDockerBuild
        }
        $global:CmsLaunchermergeArguments | Should -BeNullOrEmpty
        $global:CmsLaunchercapturedArguments | Should -Contain '(TestCategory=SecurityMetadataToolsService)&(TestCategory=SecurityMetadataToolsUpload)&(Name=It_uploads_unmodified_merge_output_and_verifies_stored_permissions)'
        $global:CmsLaunchercapturedArguments | Should -Contain '-ClaimsFixtureDirectory'
        $global:CmsLaunchercapturedArguments | Should -Contain $global:CmsLauncherprepared
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_CLAIMS_SOURCE | Should -Be 'Hybrid'
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_DATA_STANDARD_VERSION | Should -Be '5.2'
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING | Should -Be 'true'
        $derived = Get-Content (Join-Path $global:CmsLaunchercheckout 'eng/docker-compose/.derived/test.env.isolated')
        $derived | Should -Contain 'CONFIGURATION_SERVICE_CLAIMS_SOURCE=Hybrid'
        $derived | Should -Contain 'CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=true'
        $results = @(Get-ChildItem (Join-Path $global:CmsLaunchercheckout 'TestResults/cms-isolation/postgresql/Upload/5.2') -Recurse -Filter results.trx)
        $results.Count | Should -Be 1
        (Get-Content $results[0].FullName -Raw) | Should -Match 'It_uploads_unmodified_merge_output_and_verifies_stored_permissions'
        $global:CmsTeardownCalls | Should -Be 2
        Test-Path $global:CmsLauncherprepared | Should -BeFalse
        $after = @{}
        Get-ChildItem Env: | ForEach-Object { $after[$_.Name] = $_.Value }
        ($after | ConvertTo-Json -Compress) | Should -Be ($global:CmsLauncherbeforeEnvironment | ConvertTo-Json -Compress)
    }

    It 'loads the independent Drift fixture with management disabled and cleans up after <Outcome>' -TestCases @(
        @{ Outcome = 'success'; Fails = $false }, @{ Outcome = 'failure'; Fails = $true }
    ) {
        param($Outcome, $Fails)
        $fixture = Join-Path $global:CmsLaunchercheckout 'src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/TestData/Claims/SecurityMetadataTools/Drift'
        $null = New-Item -ItemType Directory $fixture -Force
        [IO.File]::WriteAllBytes((Join-Path $fixture 'Claims.json'), [byte[]]@(123,125,10))
        $global:CmsLauncherinjectFailure = $Fails
        if ($Outcome -eq 'failure') {
            { & $global:CmsLauncherlauncher -EnvironmentFile './test.env' -SecurityMetadataToolsScenario Drift -SkipDockerBuild } | Should -Throw '*execution failed*'
        } else {
            & $global:CmsLauncherlauncher -EnvironmentFile './test.env' -SecurityMetadataToolsScenario Drift -SkipDockerBuild
        }
        $global:CmsLaunchermergeArguments | Should -BeNullOrEmpty
        $global:CmsLaunchercapturedArguments | Should -Contain '(TestCategory=SecurityMetadataToolsService)&(TestCategory=SecurityMetadataToolsDrift)&(Name=It_detects_a_precise_stored_policy_edit_from_an_editable_baseline)'
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_CLAIMS_SOURCE | Should -Be 'Filesystem'
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_DATA_STANDARD_VERSION | Should -Be '5.2'
        $global:CmsLaunchercapturedPolicy.CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING | Should -Be 'false'
        $derived = Get-Content (Join-Path $global:CmsLaunchercheckout 'eng/docker-compose/.derived/test.env.isolated')
        $derived | Should -Contain 'CONFIGURATION_SERVICE_CLAIMS_SOURCE=Filesystem'
        $derived | Should -Contain 'CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=false'
        $results = @(Get-ChildItem (Join-Path $global:CmsLaunchercheckout 'TestResults/cms-isolation/postgresql/Drift/5.2') -Recurse -Filter results.trx)
        $results.Count | Should -Be 1
        $evidence = $results[0].DirectoryName
        (Get-Content $results[0].FullName -Raw) | Should -Match 'It_detects_a_precise_stored_policy_edit_from_an_editable_baseline'
        (Get-Content (Join-Path $evidence 'source-sha256-before.json') -Raw) | Should -Be (Get-Content (Join-Path $evidence 'source-sha256-after.json') -Raw)
        [IO.File]::ReadAllBytes((Join-Path $fixture 'Claims.json')) | Should -Be ([byte[]]@(123,125,10))
        $global:CmsTeardownCalls | Should -Be 2
        Test-Path $global:CmsLauncherprepared | Should -BeFalse
        $after = @{}
        Get-ChildItem Env: | ForEach-Object { $after[$_.Name] = $_.Value }
        ($after | ConvertTo-Json -Compress) | Should -Be ($global:CmsLauncherbeforeEnvironment | ConvertTo-Json -Compress)
    }

    It 'runs the same Drift test with a local installation and cleans up after <Outcome>' -TestCases @(
        @{ Outcome = 'success' }, @{ Outcome = 'service failure' },
        @{ Outcome = 'install failure' }, @{ Outcome = 'package failure' }
    ) {
        param($Outcome)
        $fixture = Join-Path $global:CmsLaunchercheckout 'src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/TestData/Claims/SecurityMetadataTools/Drift'
        $null = New-Item -ItemType Directory $fixture -Force
        # Existing startup stub asserts the exact fixture bytes.
        [IO.File]::WriteAllBytes((Join-Path $fixture 'Claims.json'), [byte[]]@(123,125,10))
        $global:CmsLauncherinjectFailure = $Outcome -eq 'service failure'
        $global:CmsLauncherinstallFailure = $Outcome -eq 'install failure'
        $global:CmsLauncherpackageFailure = $Outcome -eq 'package failure'
        $invoke = {
            & $global:CmsLauncherlauncher -EnvironmentFile './test.env' -SecurityMetadataToolsScenario Drift -UseInstalledTool -AppVersion '8.1.9-alpha.0.42' -Configuration Release -SkipDockerBuild
        }
        switch ($Outcome) {
            'success' { & $invoke }
            'service failure' { $invoke | Should -Throw '*execution failed*' }
            'install failure' { $invoke | Should -Throw '*installation failed*' }
            'package failure' { $invoke | Should -Throw '*package verification failed*' }
        }
        $global:CmsLauncherpackageArguments | Should -Contain '8.1.9-alpha.0.42'
        $global:CmsLauncherpackageArguments | Should -Contain 'Release'
        $mountSetting = Get-Content (Join-Path $global:CmsLaunchercheckout 'eng/docker-compose/.derived/test.env.isolated') | Where-Object { $_ -like 'CONFIGURATION_SERVICE_CLAIMS_MOUNT_SOURCE=*' }
        $mount = $mountSetting.Substring('CONFIGURATION_SERVICE_CLAIMS_MOUNT_SOURCE='.Length)
        Test-Path $mount | Should -BeFalse
        $global:CmsTeardownCalls | Should -Be 2
        if ($Outcome -ne 'package failure') {
            $global:CmsLauncherinstallArguments[0..2] | Should -Be @('tool', 'install', 'EdFi.Api.SecurityMetadataTools')
            $global:CmsLauncherinstallArguments | Should -Contain '8.1.0-alpha.0.42'
            $global:CmsLauncherinstallArguments | Should -Contain '--no-cache'
            $global:CmsLauncherinstallCache | Should -Be (Join-Path $global:CmsLauncherinstallScratch 'nuget-packages')
            Test-Path $global:CmsLauncherinstallScratch | Should -BeFalse
        }
        if ($Outcome -in @('success', 'service failure')) {
            $global:CmsLaunchercapturedArguments | Should -Contain '(TestCategory=SecurityMetadataToolsService)&(TestCategory=SecurityMetadataToolsDrift)&(Name=It_detects_a_precise_stored_policy_edit_from_an_editable_baseline)'
            $global:CmsLaunchercapturedPolicy.SECURITY_METADATA_TOOLS_USE_INSTALLED_TOOL | Should -Be 'true'
            $global:CmsLaunchercapturedPolicy.SECURITY_METADATA_TOOLS_EXPECTED_VERSION | Should -Be '8.1.0-alpha.0.42'
            $executable = $global:CmsLaunchercapturedPolicy.SECURITY_METADATA_TOOLS_INSTALLED_EXECUTABLE
            $executable | Should -Be (Join-Path $global:CmsLauncherinstallScratch "tool/$(if ($IsWindows) { 'security-metadata-tools.exe' } else { 'security-metadata-tools' })")
            Test-Path $global:CmsLauncherprepared | Should -BeFalse
            $evidence = @(Get-ChildItem (Join-Path $global:CmsLaunchercheckout 'TestResults/cms-isolation') -Recurse -Filter installed-package.json)
            $evidence.Count | Should -Be 1
            (Get-Content $evidence[0] -Raw | ConvertFrom-Json).Executable | Should -Be $executable
        }
        $after = @{}
        Get-ChildItem Env: | ForEach-Object { $after[$_.Name] = $_.Value }
        ($after | ConvertTo-Json -Compress) | Should -Be ($global:CmsLauncherbeforeEnvironment | ConvertTo-Json -Compress)
    }

    It 'rejects unsupported installed-tool selections before starting any stack' -TestCases @(
        @{ Selection = @{ SecurityMetadataToolsScenario = 'Hybrid'; UseInstalledTool = $true; AppVersion = '8.1.9-alpha.0.42' } },
        @{ Selection = @{ SecurityMetadataToolsScenario = 'Drift'; UseInstalledTool = $true } },
        @{ Selection = @{ SecurityMetadataToolsScenario = 'Drift'; AppVersion = '8.1.9-alpha.0.42' } }
    ) {
        param($Selection)
        $selectedArguments = $Selection
        { & $global:CmsLauncherlauncher -EnvironmentFile './test.env' @selectedArguments } | Should -Throw '*requires*'
        $global:CmsTeardownCalls | Should -Be 0
        $global:CmsLauncherpackageArguments | Should -BeNullOrEmpty
    }

    It 'detects changed Drift input and still removes the stack and mount' {
        $fixture = Join-Path $global:CmsLaunchercheckout 'src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/TestData/Claims/SecurityMetadataTools/Drift'
        $null = New-Item -ItemType Directory $fixture -Force
        [IO.File]::WriteAllBytes((Join-Path $fixture 'Claims.json'), [byte[]]@(123,125,10))
        $global:CmsLaunchermutateInput = $true
        { & $global:CmsLauncherlauncher -EnvironmentFile './test.env' -SecurityMetadataToolsScenario Drift } | Should -Throw '*changed the mounted fixture*'
        $global:CmsTeardownCalls | Should -Be 2
        Test-Path $global:CmsLauncherprepared | Should -BeFalse
    }

    It 'rejects Drift 6.1 before starting any stack' {
        { & $global:CmsLauncherlauncher -EnvironmentFile './test.env' -SecurityMetadataToolsScenario Drift -DataStandardVersion '6.1' } | Should -Throw '*requires DataStandardVersion 5.2*'
        $global:CmsTeardownCalls | Should -Be 0
    }

    It 'rejects Upload 6.1 before starting any stack' {
        { & $global:CmsLauncherlauncher -EnvironmentFile './test.env' -SecurityMetadataToolsScenario Upload -DataStandardVersion '6.1' } | Should -Throw '*requires DataStandardVersion 5.2*'
        $global:CmsTeardownCalls | Should -Be 0
    }

    It 'rejects a failed merge before startup and removes the empty mount' {
        $fixture = Join-Path $global:CmsLaunchercheckout 'src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/TestData/Claims/SecurityMetadataTools/Hybrid/fragments'
        $null = New-Item -ItemType Directory $fixture -Force
        Set-Content (Join-Path $fixture 'input-claims.json') '{}'
        $global:CmsLaunchermergeFailure = $true
        { & $global:CmsLauncherlauncher -EnvironmentFile './test.env' -SecurityMetadataToolsScenario Filesystem } | Should -Throw '*merge failed*'
        Get-Variable CmsLaunchercapturedArguments -Scope Global -ErrorAction SilentlyContinue | Should -BeNullOrEmpty
        $global:CmsTeardownCalls | Should -Be 2
        Test-Path $global:CmsLauncherprepared | Should -BeFalse
    }

    It 'detects changed Filesystem input and still removes the stack and mount' {
        $fixture = Join-Path $global:CmsLaunchercheckout 'src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/TestData/Claims/SecurityMetadataTools/Hybrid/fragments'
        $null = New-Item -ItemType Directory $fixture -Force
        Set-Content (Join-Path $fixture 'input-claims.json') '{}'
        $global:CmsLaunchermutateInput = $true
        { & $global:CmsLauncherlauncher -EnvironmentFile './test.env' -SecurityMetadataToolsScenario Filesystem } | Should -Throw '*changed the merged document*'
        $global:CmsTeardownCalls | Should -Be 2
        Test-Path $global:CmsLauncherprepared | Should -BeFalse
    }

    It 'rejects Filesystem 6.1 before starting any stack' {
        { & $global:CmsLauncherlauncher -EnvironmentFile './test.env' -SecurityMetadataToolsScenario Filesystem -DataStandardVersion '6.1' } | Should -Throw '*requires DataStandardVersion 5.2*'
        $global:CmsTeardownCalls | Should -Be 0
    }

    It 'rejects the wrong executed Hybrid case and cleans up' {
        $fixture = Join-Path $global:CmsLaunchercheckout 'src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/TestData/Claims/SecurityMetadataTools/Hybrid/fragments'
        $null = New-Item -ItemType Directory $fixture -Force
        [IO.File]::WriteAllBytes((Join-Path $fixture 'input-claims.json'), [byte[]]@(123,125,10))
        $global:CmsLauncherwrongTest = $true
        { & $global:CmsLauncherlauncher -EnvironmentFile './test.env' -SecurityMetadataToolsScenario Hybrid } | Should -Throw '*requested test*'
        $global:CmsTeardownCalls | Should -Be 2
        Test-Path $global:CmsLauncherprepared | Should -BeFalse
    }

    It 'rejects overrides that could change the named scenario contract' -TestCases @(
        @{ Override = @{ ClaimsSource = 'Filesystem' } },
        @{ Override = @{ ClaimsFixtureDirectory = 'custom' } },
        @{ Override = @{ EnableUnrestrictedClaimsLoading = $true } },
        @{ Override = @{ E2ETestFilter = 'OtherTest' } }
    ) {
        param($Override)
        $scenarioOverrides = $Override
        { & $global:CmsLauncherlauncher -EnvironmentFile './test.env' -SecurityMetadataToolsScenario Hybrid @scenarioOverrides } | Should -Throw '*Named policy scenarios own*'
        $global:CmsTeardownCalls | Should -Be 0
    }

    It 'archives failure results before cleanup and restores temporary process settings' {
        $global:CmsLauncherinjectFailure = $true
        { & $global:CmsLauncherlauncher @global:CmsLauncherlauncherArguments } | Should -Throw '*execution failed*'
        $global:CmsTeardownCalls | Should -Be 2
        Test-Path $global:CmsLauncherprepared | Should -BeFalse
        $results = @(Get-ChildItem (Join-Path $global:CmsLaunchercheckout 'TestResults/cms-isolation') -Recurse -Filter results.trx)
        $results.Count | Should -Be 1
        (Get-Content $results[0].FullName -Raw) | Should -Match "outcome='Failed'"
        @(Get-ChildItem (Join-Path $global:CmsLaunchercheckout 'TestResults/cms-isolation') -Recurse -Filter comparison.txt).Count | Should -Be 1
        $after = @{}
        Get-ChildItem Env: | ForEach-Object { $after[$_.Name] = $_.Value }
        ($after | ConvertTo-Json -Compress) | Should -Be ($global:CmsLauncherbeforeEnvironment | ConvertTo-Json -Compress)
    }
}
