# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Checks of the collection lanes' failure paths with synthetic command output. Nothing here starts
# Docker, LocalDB, SQL Server, a build, a deployment or the collector: the native commands the lanes
# call are replaced by functions of the same name, which PowerShell resolves before executables.
#
#   pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.7.1; Invoke-Pester -Path ./collection.Tests.ps1 -Output Detailed"
#
# The stand-ins run inside the lane script's own scope, where the test file's script scope is not
# visible, so the scenario they read and the helpers they call are global, and removed afterwards.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalVars', '', Justification = 'The command stand-ins run in the lane script scope and can only reach global state.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalFunctions', '', Justification = 'Stand-ins for native commands must be global to be resolved from the lane script.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseDeclaredVarsMoreThanAssignments', '', Justification = 'Pester shares BeforeAll and BeforeEach variables with the It blocks, which the analyzer does not follow.')]
param()

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'CollectionLane.psm1') -Force
    $global:LaneAssemblies = Get-FingerprintedAssemblyName
    $global:LaneSentinel = 'SENTINEL-NOT-FOR-EVIDENCE'

    function global:Get-TestHash([string] $Text) {
        [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
    }

    function global:Get-ValidSetting([string] $Datastore) {
        [ordered]@{
            AppSettings__Datastore                          = $Datastore
            AppSettings__IdentityProvider                   = 'self-contained'
            AppSettings__MultiTenancy                       = 'false'
            IdentitySettings__ClientSecretHashingIterations = '210000'
            IdentitySettings__AllowRegistration             = 'true'
        }
    }

    function global:Get-ValidFingerprint {
        $fingerprints = [ordered]@{}
        foreach ($assembly in $global:LaneAssemblies) { $fingerprints[$assembly] = Get-TestHash $assembly }
        $fingerprints
    }

    # A results file listing the given tests of the given class, each with the given outcome.
    function global:Write-TestTrx([string] $Path, [string[]] $TestName, [string] $ClassName, [string] $Outcome = 'Passed') {
        $definitions = ''
        $results = ''
        foreach ($name in $TestName) {
            $id = [guid]::NewGuid()
            $definitions += "<UnitTest name=`"$name`" id=`"$id`"><TestMethod className=`"$ClassName`" name=`"$name`" /></UnitTest>"
            $results += "<UnitTestResult testId=`"$id`" testName=`"$name`" outcome=`"$Outcome`" />"
        }
        "<?xml version=`"1.0`"?><TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`"><Results>$results</Results><TestDefinitions>$definitions</TestDefinitions></TestRun>" |
            Set-Content -LiteralPath $Path -Encoding utf8NoBOM
    }

    # The samples and header one collector run writes.
    function global:Write-TestSample([string] $OutputDir, [string] $RunLabel, [string] $Backend, [int] $Rounds, [int] $Seed, [int] $RowCount = -1) {
        if ($RowCount -lt 0) { $RowCount = $Rounds * 3 }
        $stem = "$RunLabel-endpoint-$Backend-$((Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'"))"
        $rows = @('round,arm,position,elapsed_us') + @(1..$RowCount | ForEach-Object { "0,absent,0,1.0" })
        $rows | Set-Content -LiteralPath (Join-Path $OutputDir "$stem.csv") -Encoding utf8NoBOM
        [ordered]@{ run_label = $RunLabel; boundary = "endpoint-$Backend"; rounds = $Rounds; seed = $Seed; samples_file = "$stem.csv"; arms = @('absent', 'wrong_secret', 'unapproved_correct_secret') } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir "$stem.json") -Encoding utf8NoBOM
    }

    function global:Get-EvidenceText([string] $OutputDir) {
        (Get-ChildItem -LiteralPath $OutputDir -File -Recurse | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    }
}

Describe 'Assert-LaneRoot' {
    BeforeAll {
        $collector = Join-Path $TestDrive 'collector'
        New-Item -ItemType Directory -Force -Path $collector | Out-Null
    }

    It 'refuses an evidence folder inside the collector checkout' {
        { Assert-LaneRoot -CollectorRoot $collector -OutputDir (Join-Path $collector 'reference\a5-pilot') -WorkRoot (Join-Path $TestDrive 'work') -TargetRoot (Join-Path $TestDrive 'target') } | Should -Throw '*OutputDir*'
    }

    It 'refuses a work folder inside the collector checkout' {
        { Assert-LaneRoot -CollectorRoot $collector -OutputDir (Join-Path $TestDrive 'stage') -WorkRoot (Join-Path $collector 'work') -TargetRoot (Join-Path $TestDrive 'target') } | Should -Throw '*WorkRoot*'
    }

    It 'refuses an evidence folder inside the work folder' {
        { Assert-LaneRoot -CollectorRoot $collector -OutputDir (Join-Path $TestDrive 'work\stage') -WorkRoot (Join-Path $TestDrive 'work') -TargetRoot (Join-Path $TestDrive 'target') } | Should -Throw '*contain each other*'
    }

    It 'accepts separate folders outside the checkout' {
        { Assert-LaneRoot -CollectorRoot $collector -OutputDir (Join-Path $TestDrive 'stage') -WorkRoot (Join-Path $TestDrive 'work') -TargetRoot (Join-Path $TestDrive 'target') } | Should -Not -Throw
    }
}

Describe 'ConvertFrom-Sha256Listing' {
    BeforeAll {
        $valid = @($global:LaneAssemblies | ForEach-Object { "$(Get-TestHash $_)  $_" })
    }

    It 'returns the five named fingerprints' {
        (ConvertFrom-Sha256Listing -Lines $valid).Keys | Should -Be $global:LaneAssemblies
    }

    It 'refuses a listing with one assembly missing' {
        { ConvertFrom-Sha256Listing -Lines $valid[0..3] } | Should -Throw '*missing*'
    }

    It 'refuses an empty listing' {
        { ConvertFrom-Sha256Listing -Lines @() } | Should -Throw '*missing*'
    }

    It 'refuses a malformed digest' {
        { ConvertFrom-Sha256Listing -Lines (@("not-a-digest  $($global:LaneAssemblies[0])") + $valid[1..4]) } | Should -Throw '*malformed*'
    }

    It 'refuses an unexpected file' {
        { ConvertFrom-Sha256Listing -Lines ($valid + "$(Get-TestHash 'x')  Other.dll") } | Should -Throw '*Unexpected*'
    }

    It 'refuses a repeated file' {
        { ConvertFrom-Sha256Listing -Lines ($valid + $valid[0]) } | Should -Throw '*twice*'
    }
}

Describe 'Assert-RunSetting' {
    It 'accepts the pre-registered values' {
        { Assert-RunSetting -Value (Get-ValidSetting 'postgresql') -Datastore postgresql } | Should -Not -Throw
    }

    It 'refuses <Name> = <Value>' -ForEach @(
        @{ Name = 'IdentitySettings__ClientSecretHashingIterations'; Value = '100000' }
        @{ Name = 'AppSettings__MultiTenancy'; Value = 'true' }
        @{ Name = 'IdentitySettings__AllowRegistration'; Value = 'false' }
        @{ Name = 'AppSettings__IdentityProvider'; Value = 'keycloak' }
        @{ Name = 'AppSettings__Datastore'; Value = 'mssql' }
    ) {
        $settings = Get-ValidSetting 'postgresql'
        $settings[$Name] = $Value
        { Assert-RunSetting -Value $settings -Datastore postgresql } | Should -Throw "*$Name*"
    }

    It 'refuses a missing value' {
        $settings = Get-ValidSetting 'mssql'
        $settings.Remove('AppSettings__MultiTenancy')
        { Assert-RunSetting -Value $settings -Datastore mssql } | Should -Throw '*no observed value*'
    }
}

Describe 'Compare-LaneSnapshot' {
    BeforeAll {
        function Get-TestSnapshot([string] $Container = 'c1') {
            [ordered]@{ container_id = $Container; settings = (Get-ValidSetting 'postgresql'); assemblies = (Get-ValidFingerprint) }
        }
    }

    It 'finds nothing between identical snapshots' {
        Compare-LaneSnapshot -Before (Get-TestSnapshot) -After (Get-TestSnapshot) -IdentityField container_id | Should -BeNullOrEmpty
    }

    It 'reports a changed container' {
        (Compare-LaneSnapshot -Before (Get-TestSnapshot 'c1') -After (Get-TestSnapshot 'c2') -IdentityField container_id) | Should -Contain 'container_id changed'
    }

    It 'reports a fingerprint missing on both sides instead of comparing them equal' {
        $before = Get-TestSnapshot
        $after = Get-TestSnapshot
        $before.assemblies.Remove($global:LaneAssemblies[2])
        $after.assemblies.Remove($global:LaneAssemblies[2])
        (Compare-LaneSnapshot -Before $before -After $after -IdentityField container_id) | Should -Contain "assembly $($global:LaneAssemblies[2]) is missing"
    }

    It 'reports a changed setting' {
        $after = Get-TestSnapshot
        $after.settings['IdentitySettings__ClientSecretHashingIterations'] = '1000'
        (Compare-LaneSnapshot -Before (Get-TestSnapshot) -After $after -IdentityField container_id) | Should -Contain 'setting IdentitySettings__ClientSecretHashingIterations changed'
    }
}

Describe 'Assert-CollectorRun' {
    BeforeEach {
        $folder = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $folder | Out-Null
        $trx = Join-Path $folder 'run.trx'
        $started = (Get-Date).ToUniversalTime().AddSeconds(-5)
        $fixture = "EdFi.DmsConfigurationService.Tests.E2E.Timing.$(Get-CollectorFixtureName)"
        $tests = @('It_records_one_sample_per_arm_per_round', 'It_writes_the_run_header')
        $arguments = @{ TrxPath = $trx; OutputDir = $folder; RunLabel = 'pilot-a5'; Backend = 'postgresql'; Rounds = 20; Seed = 301; StartedUtc = $started }
    }

    It 'accepts a run of the fixture''s two tests with this run''s samples' {
        Write-TestTrx $trx $tests $fixture
        Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301
        (Assert-CollectorRun -ExitCode 0 @arguments).sample_rows | Should -Be 60
    }

    It 'refuses a nonzero exit' {
        Write-TestTrx $trx $tests $fixture
        Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301
        { Assert-CollectorRun -ExitCode 1 @arguments } | Should -Throw '*exited 1*'
    }

    It 'refuses a run that matched no test' {
        Write-TestTrx $trx @() $fixture
        Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301
        { Assert-CollectorRun -ExitCode 0 @arguments } | Should -Throw '*reported 0 tests*'
    }

    It 'refuses tests of another class' {
        Write-TestTrx $trx $tests 'Some.Other.Fixture'
        Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301
        { Assert-CollectorRun -ExitCode 0 @arguments } | Should -Throw '*not the collector fixture*'
    }

    It 'refuses a failed test' {
        Write-TestTrx $trx $tests $fixture 'Failed'
        Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301
        { Assert-CollectorRun -ExitCode 0 @arguments } | Should -Throw '*did not pass*'
    }

    It 'refuses a run that wrote no samples' {
        Write-TestTrx $trx $tests $fixture
        { Assert-CollectorRun -ExitCode 0 @arguments } | Should -Throw '*found 0 and 0*'
    }

    It 'refuses a samples file with the wrong number of rows' {
        Write-TestTrx $trx $tests $fixture
        Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301 -RowCount 59
        { Assert-CollectorRun -ExitCode 0 @arguments } | Should -Throw '*59 rows*'
    }

    It 'refuses a header for another seed' {
        Write-TestTrx $trx $tests $fixture
        Write-TestSample $folder 'pilot-a5' 'postgresql' 20 999
        { Assert-CollectorRun -ExitCode 0 @arguments } | Should -Throw '*does not match*'
    }
}

Describe 'lane-postgresql.ps1 with synthetic command output' {
    BeforeAll {
        $lane = Join-Path $PSScriptRoot 'lane-postgresql.ps1'
        $global:LaneFixtureClass = "EdFi.DmsConfigurationService.Tests.E2E.Timing.$(Get-CollectorFixtureName)"

        # The stand-ins read $global:LaneScenario, which each test sets.
        function global:git { $global:LASTEXITCODE = 0; if ($args -contains 'rev-parse') { '1111111111111111111111111111111111111111' } }
        function global:Get-NetTCPConnection { $null }
        function global:Invoke-WebRequest { [pscustomobject]@{ StatusCode = 200 } }
        function global:pwsh {
            $global:LaneScenario.Calls.Add("pwsh $($args -join ' ')")
            $global:LASTEXITCODE = if (($args -join ' ') -like '*teardown*') { $global:LaneScenario.TeardownExit } else { 0 }
        }
        function global:docker {
            $global:LASTEXITCODE = 0
            $line = $args -join ' '
            if ($line -like 'images*' -and $global:LaneScenario.ImagesThrow) { throw "image listing failed: $($global:LaneSentinel)" }
            if ($line -like 'ps *' -or $line -like 'images*') { return }
            if ($line -like 'image inspect*') { return '[{"Created":"2026-10-24T00:00:00Z"}]' }
            if ($line -like 'inspect*') {
                $global:LaneScenario.Inspections++
                $id = if ($global:LaneScenario.Inspections -gt 1 -and $global:LaneScenario.ChangeContainer) { 'container-2' } else { 'container-1' }
                $environment = @($global:LaneScenario.Settings.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
                return (@([ordered]@{ Id = $id; Name = '/ed-fi-api-config-service'; Image = 'sha256:image'; RestartCount = 0; State = @{ Running = $true; StartedAt = 'started' }; Config = @{ Env = $environment } }) | ConvertTo-Json -Depth 5 -AsArray)
            }
            if ($line -like 'exec * sh -c*') { return $global:LaneScenario.HashLines }
            if ($line -like 'exec * cat*') { return '{}' }
            throw "unexpected docker call: $line"
        }
        function global:dotnet {
            $line = $args -join ' '
            $global:LASTEXITCODE = 0
            if ($line -like 'build-server*') { return }
            $global:LaneScenario.Calls.Add('dotnet test')
            "runner output with $($global:LaneSentinel)"
            $results = $args[[array]::IndexOf($args, '--results-directory') + 1]
            $logger = [string]$args[[array]::IndexOf($args, '--logger') + 1]
            $trx = Join-Path $results ($logger -replace '^trx;LogFileName=', '')
            Write-TestTrx $trx $global:LaneScenario.TestNames $global:LaneFixtureClass
            if ($global:LaneScenario.WriteSamples) {
                Write-TestSample $env:TIMING_EVIDENCE_OUTPUT_DIR $env:TIMING_EVIDENCE_RUN_LABEL 'postgresql' ([int]$env:TIMING_EVIDENCE_ROUNDS) ([int]$env:TIMING_EVIDENCE_SEED)
            }
            $global:LASTEXITCODE = $global:LaneScenario.CollectorExit
        }
    }

    AfterAll {
        Remove-Item -LiteralPath Function:\global:git, Function:\global:Get-NetTCPConnection, Function:\global:Invoke-WebRequest, Function:\global:pwsh, Function:\global:docker, Function:\global:dotnet -ErrorAction SilentlyContinue
    }

    BeforeEach {
        $run = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $collector = Join-Path $run 'collector'
        $target = Join-Path $run 'target'
        $stage = Join-Path $run 'stage'
        $work = Join-Path $run 'work'
        New-Item -ItemType Directory -Force -Path $collector, $target | Out-Null
        '{"commit":"b2b8d5df47e5e48c46f9ca26fe0f2a58b787c399"}' | Set-Content -LiteralPath (Join-Path $target 'target.json')
        $global:LaneScenario = @{
            Calls          = [System.Collections.Generic.List[string]]::new()
            Settings       = (Get-ValidSetting 'postgresql')
            HashLines      = @($global:LaneAssemblies | ForEach-Object { "$(Get-TestHash $_)  $_" })
            TestNames      = @('It_records_one_sample_per_arm_per_round', 'It_writes_the_run_header')
            WriteSamples   = $true
            CollectorExit  = 0
            TeardownExit   = 0
            ChangeContainer = $false
            ImagesThrow    = $false
            Inspections    = 0
        }
        $arguments = @{ RunLabel = 'pilot-a5'; Rounds = 20; WarmupRounds = 0; Seed = 301; OutputDir = $stage; CollectorRoot = $collector; TargetRoot = $target; WorkRoot = $work }
    }

    AfterEach {
        Get-ChildItem Env:TIMING_EVIDENCE_*, Env:CONFIGURATION_SERVICE_E2E_API_URL -ErrorAction SilentlyContinue | Remove-Item -ErrorAction SilentlyContinue
    }

    It 'exits 0 when every stage succeeds, and keeps runner output out of the evidence' {
        & $lane @arguments
        $LASTEXITCODE | Should -Be 0
        (Get-Content (Get-ChildItem $stage -Filter 'provenance-*.json').FullName -Raw | ConvertFrom-Json).succeeded | Should -BeTrue
        Get-EvidenceText $stage | Should -Not -Match $global:LaneSentinel
        (Get-ChildItem $work -Filter 'collector-*.log' | Get-Content -Raw) | Should -Match $global:LaneSentinel
    }

    It 'exits 1 when the collector fails, and keeps its output out of the evidence' {
        $global:LaneScenario.CollectorExit = 1
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        Get-EvidenceText $stage | Should -Not -Match $global:LaneSentinel
    }

    It 'exits 1 when the collector ran no test' {
        $global:LaneScenario.TestNames = @()
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
    }

    It 'exits 1 when the collector wrote no samples' {
        $global:LaneScenario.WriteSamples = $false
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
    }

    It 'exits 1 without collecting when the iteration count is not the pre-registered one' {
        $global:LaneScenario.Settings['IdentitySettings__ClientSecretHashingIterations'] = '100000'
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        $global:LaneScenario.Calls | Should -Not -Contain 'dotnet test'
    }

    It 'exits 1 without collecting when an assembly fingerprint is missing' {
        $global:LaneScenario.HashLines = $global:LaneScenario.HashLines[0..3]
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        $global:LaneScenario.Calls | Should -Not -Contain 'dotnet test'
    }

    It 'exits 1 when a different container answers after collection' {
        $global:LaneScenario.ChangeContainer = $true
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        (Get-Content (Get-ChildItem $stage -Filter 'provenance-*.json').FullName -Raw | ConvertFrom-Json).deployment_differences | Should -Contain 'container_id changed'
    }

    It 'exits 1 when teardown fails after a successful collection' {
        $global:LaneScenario.TeardownExit = 1
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
    }

    It 'keeps exception text out of the evidence and in the work folder' {
        $global:LaneScenario.ImagesThrow = $true
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        Get-EvidenceText $stage | Should -Not -Match $global:LaneSentinel
        (Get-ChildItem $work -Filter '*.diagnostics.txt' | Get-Content -Raw) | Should -Match $global:LaneSentinel
    }

    It 'refuses an evidence folder inside the collector checkout before writing anything' {
        $arguments.OutputDir = Join-Path $collector 'reference\a5-pilot'
        { & $lane @arguments } | Should -Throw '*OutputDir*'
        Test-Path -LiteralPath $arguments.OutputDir | Should -BeFalse
    }
}

Describe 'lane-localdb.ps1 with synthetic command output, before the service starts' {
    BeforeAll {
        $lane = Join-Path $PSScriptRoot 'lane-localdb.ps1'
        function global:git { $global:LASTEXITCODE = 0; if ($args -contains 'rev-parse') { '1111111111111111111111111111111111111111' } }
        function global:Get-NetTCPConnection { $null }
        function global:docker { $global:LASTEXITCODE = 0 }
        function global:sqllocaldb {
            $global:LASTEXITCODE = 0
            if ($args.Count -eq 1 -and $args[0] -eq 'info') { return 'DMS1490' }
            if ($args[0] -eq 'info') { return @('Version: 17.0.4025.3', 'Instance pipe name: np:\\.\pipe\LOCALDB#TEST\tsql\query') }
        }
        function global:sqlcmd { $global:LASTEXITCODE = 0 }
    }

    AfterAll {
        Remove-Item -LiteralPath Function:\global:git, Function:\global:Get-NetTCPConnection, Function:\global:docker, Function:\global:sqllocaldb, Function:\global:sqlcmd -ErrorAction SilentlyContinue
        Remove-Module env-utility, database-safety, bootstrap-manifest, OpenIddict-Crypto -ErrorAction SilentlyContinue
    }

    BeforeEach {
        $run = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $collector = Join-Path $run 'collector'
        $target = Join-Path $run 'target'
        $stage = Join-Path $run 'stage'
        $work = Join-Path $run 'work'
        $bin = Join-Path $target 'src\config\frontend\EdFi.DmsConfigurationService.Frontend.AspNetCore\bin\Release\net10.0'
        $compose = Join-Path $target 'eng\docker-compose'
        New-Item -ItemType Directory -Force -Path $collector, $bin, $compose | Out-Null
        $fingerprints = [ordered]@{}
        foreach ($assembly in $global:LaneAssemblies) {
            Set-Content -LiteralPath (Join-Path $bin $assembly) -Value "assembly $assembly" -NoNewline
            $fingerprints[$assembly] = (Get-FileHash -LiteralPath (Join-Path $bin $assembly) -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        @{ commit = 'b2b8d5df47e5e48c46f9ca26fe0f2a58b787c399'; host_release = @{ fingerprints = $fingerprints } } | ConvertTo-Json -Depth 4 |
            Set-Content -LiteralPath (Join-Path $target 'target.json')
        # Stand-ins for the target's own docker-compose modules; the values function reads $env:LANE_TEST_ITERATIONS.
        @'
function ReadValuesFromEnvFile($Path) {
    @{ POSTGRES_DB_NAME = 'lane_test'; MSSQL_SA_PASSWORD = 'SENTINEL-NOT-FOR-EVIDENCE'; CONFIGURATION_SERVICE_IDENTITY_ALLOW_REGISTRATION = 'true'; CONFIGURATION_SERVICE_IDENTITY_HASHING_ITERATIONS = $env:LANE_TEST_ITERATIONS }
}
Export-ModuleMember -Function ReadValuesFromEnvFile
'@ | Set-Content -LiteralPath (Join-Path $compose 'env-utility.psm1')
        'function Initialize-E2EClaimsWorkspace { "claims" }; Export-ModuleMember -Function Initialize-E2EClaimsWorkspace' | Set-Content -LiteralPath (Join-Path $compose 'bootstrap-manifest.psm1')
        '' | Set-Content -LiteralPath (Join-Path $compose 'database-safety.psm1')
        '' | Set-Content -LiteralPath (Join-Path $compose 'OpenIddict-Crypto.psm1')
        $env:LANE_TEST_ITERATIONS = '210000'
        $arguments = @{ RunLabel = 'pilot-a5'; Rounds = 20; WarmupRounds = 0; Seed = 302; OutputDir = $stage; CollectorRoot = $collector; TargetRoot = $target; WorkRoot = $work }
    }

    AfterEach {
        Remove-Item Env:LANE_TEST_ITERATIONS, Env:AppSettings__Datastore, Env:SQLCMDPASSWORD -ErrorAction SilentlyContinue
    }

    It 'exits 1 without starting the service when the iteration count is not the pre-registered one' {
        $env:LANE_TEST_ITERATIONS = '100000'
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        (Get-Content (Get-ChildItem $stage -Filter 'provenance-*.json').FullName -Raw | ConvertFrom-Json).failures[0].error_type | Should -Not -BeNullOrEmpty
        Get-ChildItem $work -Filter 'cms-*.log' | Should -BeNullOrEmpty
    }

    It 'removes an inherited configuration variable from the launch and records only its name' {
        $env:AppSettings__Datastore = 'postgresql'
        $env:LANE_TEST_ITERATIONS = '100000'
        & $lane @arguments
        $provenance = Get-Content (Get-ChildItem $stage -Filter 'provenance-*.json').FullName -Raw | ConvertFrom-Json
        $provenance.removed_inherited_configuration_variable_names | Should -Contain 'AppSettings__Datastore'
        $provenance.launch_settings.AppSettings__Datastore | Should -Be 'mssql'
    }

    It 'exits 1 before any database work when the target assemblies changed since preparation' {
        Add-Content -LiteralPath (Join-Path $bin $global:LaneAssemblies[1]) -Value 'changed'
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        Test-Path (Join-Path $work 'db') | Should -BeFalse
    }

    It 'keeps the secret values it reads out of the evidence' {
        $env:LANE_TEST_ITERATIONS = '100000'
        & $lane @arguments
        Get-EvidenceText $stage | Should -Not -Match $global:LaneSentinel
    }
}

AfterAll {
    Remove-Item -LiteralPath Function:\global:Get-TestHash, Function:\global:Get-ValidSetting, Function:\global:Get-ValidFingerprint, Function:\global:Write-TestTrx, Function:\global:Write-TestSample, Function:\global:Get-EvidenceText -ErrorAction SilentlyContinue
    Remove-Variable -Name LaneScenario, LaneSentinel, LaneAssemblies, LaneFixtureClass -Scope Global -ErrorAction SilentlyContinue
}
