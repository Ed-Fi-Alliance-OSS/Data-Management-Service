# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Checks of the collection lanes' failure paths with synthetic command output. Nothing here starts
# Docker, LocalDB, SQL Server, a build, a deployment or the collector: the native commands the lanes
# call are replaced by functions of the same name, which PowerShell resolves before executables.
# Power readings are synthetic too; no test calls the native power function.
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

    # Power stand-ins. The module reads the native status through Read-SystemPowerStatus; an alias
    # resolves before any function, even inside the module and after a lane imports it again, so
    # no test reaches the real GetSystemPowerStatus. powercfg is replaced by a function of its name.
    # Each read takes the next step of $global:PowerScenario.Sequence; the last step repeats.
    $global:BalancedScheme = '381b4222-f694-41f0-9685-ff5bb260df2e'

    function global:New-PowerStep([string] $Kind) {
        $step = @{
            NativeThrows       = $false
            Succeeded          = $true
            Win32Error         = 0
            ACLineStatus       = [byte]1
            BatteryFlag        = [byte]8
            BatteryLifePercent = [byte]80
            SystemStatusFlag   = [byte]0
            SchemeThrows       = $false
            SchemeExit         = 0
            SchemeOutput       = @("Power Scheme GUID: $($global:BalancedScheme)  (Balanced)")
        }
        switch ($Kind) {
            'online' { }
            'offline' { $step.ACLineStatus = [byte]0 }
            'unknown' { $step.ACLineStatus = [byte]255 }
            # A failed call leaves the structure undefined; these values must not be reported.
            'call_failed' { $step.Succeeded = $false; $step.Win32Error = 87 }
            'saver_on' { $step.SystemStatusFlag = [byte]1 }
            'wrong_scheme' { $step.SchemeOutput = @('Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance)') }
            'scheme_missing' { $step.SchemeOutput = @() }
            'scheme_malformed' { $step.SchemeOutput = @('Power Scheme GUID: 381b4222-f694-41f0  (Balanced)') }
            'scheme_twice' { $step.SchemeOutput = @("Power Scheme GUID: $($global:BalancedScheme)", 'Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c') }
            'scheme_exit' { $step.SchemeExit = 1 }
            'scheme_throws' { $step.SchemeThrows = $true }
            'native_throws' { $step.NativeThrows = $true }
            default { throw "Unknown power step $Kind." }
        }
        $step
    }

    function global:Set-PowerSequence([string[]] $Kind) {
        $global:PowerScenario = @{ Sequence = @($Kind | ForEach-Object { New-PowerStep $_ }); Reads = 0 }
    }

    function global:Get-PowerStep {
        $global:PowerScenario.Sequence[[Math]::Min($global:PowerScenario.Reads, $global:PowerScenario.Sequence.Count) - 1]
    }

    function global:Get-TestPowerStatus {
        $global:PowerScenario.Reads++
        $step = Get-PowerStep
        if ($step.NativeThrows) { throw "native power read failed: $($global:LaneSentinel)" }
        [pscustomobject]@{
            Succeeded          = $step.Succeeded
            Win32Error         = $step.Win32Error
            ACLineStatus       = $step.ACLineStatus
            BatteryFlag        = $step.BatteryFlag
            BatteryLifePercent = $step.BatteryLifePercent
            SystemStatusFlag   = $step.SystemStatusFlag
        }
    }

    function global:powercfg {
        $step = Get-PowerStep
        if ($step.SchemeThrows) { throw "powercfg failed: $($global:LaneSentinel)" }
        $global:LASTEXITCODE = $step.SchemeExit
        $step.SchemeOutput
    }

    Set-Alias -Name Read-SystemPowerStatus -Value Get-TestPowerStatus -Scope Global
    Set-PowerSequence 'online'

    function global:Get-RefusalLine([string] $OutputDir) {
        $path = Join-Path $OutputDir 'refusals.log'
        if (-not (Test-Path -LiteralPath $path)) { return , @() }
        , @(Get-Content -LiteralPath $path | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
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

    It 'refuses an evidence folder that contains the collector checkout' {
        $outer = Join-Path $TestDrive 'outer'
        $nested = Join-Path $outer 'collector'
        New-Item -ItemType Directory -Force -Path $nested | Out-Null
        { Assert-LaneRoot -CollectorRoot $nested -OutputDir $outer -WorkRoot (Join-Path $TestDrive 'private\runs') -TargetRoot (Join-Path $TestDrive 'private\target') } |
            Should -Throw '*OutputDir must not equal, contain or lie inside the collector checkout*'
    }

    It 'refuses a work folder that contains the collector checkout' {
        { Assert-LaneRoot -CollectorRoot $collector -OutputDir (Join-Path $TestDrive 'elsewhere\stage') -WorkRoot $TestDrive -TargetRoot (Join-Path $TestDrive 'elsewhere\target') } |
            Should -Throw '*WorkRoot must not equal, contain or lie inside the collector checkout*'
    }

    It 'refuses a target that is the collector checkout itself' {
        { Assert-LaneRoot -CollectorRoot $collector -OutputDir (Join-Path $TestDrive 'stage') -WorkRoot (Join-Path $TestDrive 'work') -TargetRoot "$collector\" } |
            Should -Throw '*TargetRoot must not equal, contain or lie inside the collector checkout*'
    }

    It 'refuses an evidence folder that is the target' {
        { Assert-LaneRoot -CollectorRoot $collector -OutputDir (Join-Path $TestDrive 'target') -WorkRoot (Join-Path $TestDrive 'work') -TargetRoot (Join-Path $TestDrive 'target') } |
            Should -Throw '*OutputDir and TargetRoot*'
    }

    It 'resolves a relative folder against the current location, as the later file operations do' {
        Push-Location -LiteralPath $collector
        try {
            { Assert-LaneRoot -CollectorRoot $collector -OutputDir 'stage' -WorkRoot (Join-Path $TestDrive 'work') -TargetRoot (Join-Path $TestDrive 'target') } | Should -Throw '*OutputDir*'
        }
        finally {
            Pop-Location
        }
    }

    It 'refuses a folder that reaches the collector checkout through dot segments' {
        { Assert-LaneRoot -CollectorRoot $collector -OutputDir (Join-Path $TestDrive 'stage\..\collector\a5-pilot') -WorkRoot (Join-Path $TestDrive 'work') -TargetRoot (Join-Path $TestDrive 'target') } | Should -Throw '*OutputDir*'
    }

    It 'refuses a folder that passes through a junction' {
        $link = Join-Path $TestDrive 'link'
        New-Item -ItemType Junction -Path $link -Target $collector | Out-Null
        try {
            { Assert-LaneRoot -CollectorRoot $collector -OutputDir (Join-Path $link 'stage') -WorkRoot (Join-Path $TestDrive 'work') -TargetRoot (Join-Path $TestDrive 'target') } | Should -Throw '*junction*'
        }
        finally {
            # Removes the link only, never the folder it points to.
            [System.IO.Directory]::Delete($link)
        }
    }

    It 'refuses a folder inside the collector checkout named by its short name' {
        $long = Join-Path $TestDrive 'collector with a long name'
        New-Item -ItemType Directory -Force -Path $long | Out-Null
        $short = (New-Object -ComObject Scripting.FileSystemObject).GetFolder($long).ShortPath
        if ($short -eq $long) { Set-ItResult -Skipped -Because 'this volume does not generate short names' }
        { Assert-LaneRoot -CollectorRoot $long -OutputDir (Join-Path $short 'stage') -WorkRoot (Join-Path $TestDrive 'work') -TargetRoot (Join-Path $TestDrive 'target') } | Should -Throw '*OutputDir*'
    }

    It 'accepts the documented layout, with the work folder and the targets under one private parent' {
        $repository = Join-Path $TestDrive 'layout\repository'
        New-Item -ItemType Directory -Force -Path $repository | Out-Null
        { Assert-LaneRoot -CollectorRoot $repository -OutputDir (Join-Path $TestDrive 'layout\stage\pilot') -WorkRoot (Join-Path $TestDrive 'layout\work\runs') -TargetRoot (Join-Path $TestDrive 'layout\work\targets\a5') } |
            Should -Not -Throw
    }
}

Describe 'Resolve-LanePath' {
    It 'returns the full path without dot segments or a trailing separator' {
        Resolve-LanePath (Join-Path $TestDrive 'a\.\b\..\c\') | Should -Be (Join-Path $TestDrive 'a\c')
    }

    It 'keeps a drive root intact' {
        Resolve-LanePath 'C:\' | Should -Be 'C:\'
    }
}

Describe 'Assert-OutsideWorktree' {
    BeforeAll {
        # Shaped as git prints it: forward slashes, one block per worktree.
        $first = Join-Path $TestDrive 'repository'
        $second = Join-Path $TestDrive 'worktrees\other'
        $listing = @(
            "worktree $($first -replace '\\', '/')", 'HEAD 1111111111111111111111111111111111111111', 'branch refs/heads/main', '',
            "worktree $($second -replace '\\', '/')", 'HEAD 2222222222222222222222222222222222222222', 'branch refs/heads/other', ''
        )
    }

    It 'refuses a new folder inside a worktree other than the first' {
        { Assert-OutsideWorktree -Name 'Destination' -Path (Join-Path $second 'targets\a5') -WorktreeListing $listing } | Should -Throw '*Destination*worktree*'
    }

    It 'refuses a folder that contains a worktree' {
        { Assert-OutsideWorktree -Name 'Destination' -Path (Join-Path $TestDrive 'worktrees') -WorktreeListing $listing } | Should -Throw '*Destination*worktree*'
    }

    It 'refuses a worktree itself' {
        { Assert-OutsideWorktree -Name 'Destination' -Path $first -WorktreeListing $listing } | Should -Throw '*Destination*worktree*'
    }

    It 'refuses an empty listing' {
        { Assert-OutsideWorktree -Name 'Destination' -Path (Join-Path $TestDrive 'work\targets\a5') -WorktreeListing @() } | Should -Throw '*no worktrees*'
    }

    It 'accepts a sibling folder' {
        { Assert-OutsideWorktree -Name 'Destination' -Path (Join-Path $TestDrive 'work\targets\a5') -WorktreeListing $listing } | Should -Not -Throw
    }
}

Describe 'prepare-target.ps1 with synthetic command output' {
    BeforeAll {
        $prepare = Join-Path $PSScriptRoot 'prepare-target.ps1'
        $commit = 'b2b8d5df47e5e48c46f9ca26fe0f2a58b787c399'

        # git lists the scenario's worktrees and stops at archive; tar and dotnet only record a call.
        function global:git {
            $line = $args -join ' '
            $global:LaneScenario.Calls.Add("git $line")
            $global:LASTEXITCODE = 0
            if ($line -like '*worktree list --porcelain') {
                $global:LASTEXITCODE = $global:LaneScenario.ListExit
                return $global:LaneScenario.Worktrees | ForEach-Object { "worktree $($_ -replace '\\', '/')"; 'HEAD 1111111111111111111111111111111111111111'; '' }
            }
            if ($line -like '*rev-parse*') { return '3333333333333333333333333333333333333333' }
            if ($line -like '*archive*') { throw 'archive-reached' }
        }
        function global:tar { $global:LaneScenario.Calls.Add('tar') }
        function global:dotnet { $global:LaneScenario.Calls.Add('dotnet') }
    }

    AfterAll {
        Remove-Item -LiteralPath Function:\global:git, Function:\global:tar, Function:\global:dotnet -ErrorAction SilentlyContinue
    }

    BeforeEach {
        $run = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $repository = Join-Path $run 'repository'
        $other = Join-Path $run 'worktrees\other'
        New-Item -ItemType Directory -Force -Path $repository, $other | Out-Null
        $global:LaneScenario = @{
            Calls     = [System.Collections.Generic.List[string]]::new()
            Worktrees = @($repository, $other)
            ListExit  = 0
        }
    }

    It 'refuses a new child of a registered worktree before creating it or extracting anything' {
        $destination = Join-Path $other 'targets\a5'
        { & $prepare -RepositoryRoot $repository -Commit $commit -Destination $destination -BuildHostRelease } | Should -Throw '*Destination*worktree*'
        Test-Path -LiteralPath (Join-Path $other 'targets') | Should -BeFalse
        $global:LaneScenario.Calls | Should -Be @("git -C $repository worktree list --porcelain")
    }

    It 'refuses a destination that contains a worktree, before creating it' {
        $destination = Join-Path $run 'worktrees'
        Remove-Item -LiteralPath $destination -Recurse -Force
        { & $prepare -RepositoryRoot $repository -Commit $commit -Destination $destination } | Should -Throw '*Destination*worktree*'
        Test-Path -LiteralPath $destination | Should -BeFalse
        $global:LaneScenario.Calls | Should -Be @("git -C $repository worktree list --porcelain")
    }

    It 'stops before creating anything when the worktrees cannot be listed' {
        $global:LaneScenario.ListExit = 128
        $destination = Join-Path $run 'work\targets\a5'
        { & $prepare -RepositoryRoot $repository -Commit $commit -Destination $destination } | Should -Throw '*Listing the worktrees*'
        Test-Path -LiteralPath (Join-Path $run 'work') | Should -BeFalse
    }

    It 'lets an external sibling destination through to extraction' {
        $destination = Join-Path $run 'work\targets\a5'
        { & $prepare -RepositoryRoot $repository -Commit $commit -Destination $destination } | Should -Throw '*archive-reached*'
        Test-Path -LiteralPath $destination | Should -BeTrue
        $global:LaneScenario.Calls | Should -Not -Contain 'tar'
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

    It 'accepts samples whose file times read a timer tick before the recorded start' {
        Write-TestTrx $trx $tests $fixture
        Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301
        Get-ChildItem -LiteralPath $folder -Filter 'pilot-a5-*' | ForEach-Object { $_.LastWriteTimeUtc = $started.AddMilliseconds(-15) }
        (Assert-CollectorRun -ExitCode 0 @arguments).sample_rows | Should -Be 60
    }

    It 'refuses samples written a minute before the run started' {
        Write-TestTrx $trx $tests $fixture
        Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301
        Get-ChildItem -LiteralPath $folder -Filter 'pilot-a5-*' | ForEach-Object { $_.LastWriteTimeUtc = $started.AddMinutes(-1) }
        { Assert-CollectorRun -ExitCode 0 @arguments } | Should -Throw '*found 0 and 0*'
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

Describe 'Get-PowerSource and power condition P' {
    It 'reports <Kind> as unmet <Unmet>' -ForEach @(
        @{ Kind = 'online'; Unmet = @() }
        @{ Kind = 'offline'; Unmet = @('ac_line_offline') }
        @{ Kind = 'unknown'; Unmet = @('ac_line_unknown') }
        @{ Kind = 'call_failed'; Unmet = @('call_failed') }
        @{ Kind = 'saver_on'; Unmet = @('battery_saver_on') }
        @{ Kind = 'wrong_scheme'; Unmet = @('scheme_not_balanced') }
        @{ Kind = 'scheme_missing'; Unmet = @('scheme_query_failed') }
        @{ Kind = 'scheme_malformed'; Unmet = @('scheme_query_failed') }
        @{ Kind = 'scheme_twice'; Unmet = @('scheme_query_failed') }
        @{ Kind = 'scheme_exit'; Unmet = @('scheme_query_failed') }
        @{ Kind = 'scheme_throws'; Unmet = @('scheme_query_failed') }
    ) {
        Set-PowerSequence $Kind
        $unmet = Get-PowerConditionFailure (Get-PowerSource)
        $unmet | Should -Be $Unmet
        $global:PowerScenario.Reads | Should -Be 1
    }

    It 'keeps the raw fields of a successful call and records no error, even a stale one' {
        Set-PowerSequence 'unknown'
        $global:PowerScenario.Sequence[0].Win32Error = 5
        $reading = Get-PowerSource
        $reading.call_succeeded | Should -BeTrue
        $reading.win32_error | Should -BeNullOrEmpty
        $reading.ac_line_status | Should -Be 255
        $reading.battery_flag | Should -Be 8
        $reading.battery_life_percent | Should -Be 80
        $reading.system_status_flag | Should -Be 0
        $reading.scheme_guid | Should -Be $global:BalancedScheme
    }

    It 'records the Win32 error of a failed call and none of its undefined power fields' {
        Set-PowerSequence 'call_failed'
        $reading = Get-PowerSource
        $reading.call_succeeded | Should -BeFalse
        $reading.win32_error | Should -Be 87
        $reading.ac_line_status | Should -BeNullOrEmpty
        $reading.system_status_flag | Should -BeNullOrEmpty
    }

    It 'records a failed scheme query as <Code>, never its output' -ForEach @(
        @{ Kind = 'scheme_missing'; Code = 'no_guid' }
        @{ Kind = 'scheme_malformed'; Code = 'no_guid' }
        @{ Kind = 'scheme_twice'; Code = 'several_guids' }
        @{ Kind = 'scheme_exit'; Code = 'exit_nonzero' }
        @{ Kind = 'scheme_throws'; Code = 'command_error' }
    ) {
        Set-PowerSequence $Kind
        $reading = Get-PowerSource
        $reading.scheme_query_succeeded | Should -BeFalse
        $reading.scheme_query_failure | Should -Be $Code
        $reading.scheme_guid | Should -BeNullOrEmpty
        ($reading | ConvertTo-Json) | Should -Not -Match $global:LaneSentinel
    }

    It 'reports a missing reading as an observation error' {
        $unmet = Get-PowerConditionFailure $null
        $unmet | Should -Be @('observation_error')
    }

    It 'does not accept <Name> by loose comparison' -ForEach @(
        @{ Name = 'a string call result'; Field = 'call_succeeded'; Value = 'True'; Unmet = 'call_failed' }
        @{ Name = 'a string line status'; Field = 'ac_line_status'; Value = '1'; Unmet = 'ac_line_unexpected' }
        @{ Name = 'a Boolean line status'; Field = 'ac_line_status'; Value = $true; Unmet = 'ac_line_unexpected' }
        @{ Name = 'an upper-case scheme'; Field = 'scheme_guid'; Value = '381B4222-F694-41F0-9685-FF5BB260DF2E'; Unmet = 'scheme_not_balanced' }
    ) {
        Set-PowerSequence 'online'
        $reading = Get-PowerSource
        $reading[$Field] = $Value
        $unmet = Get-PowerConditionFailure $reading
        $unmet | Should -Be @($Unmet)
    }

    It 'loads the native type once across repeated imports of the module' {
        { Import-Module (Join-Path $PSScriptRoot 'CollectionLane.psm1') -Force } | Should -Not -Throw
        { Import-Module (Join-Path $PSScriptRoot 'CollectionLane.psm1') -Force } | Should -Not -Throw
        @([System.AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $null -ne $_.GetType('DmsTimingEvidence.SystemPower', $false) }).Count | Should -Be 1
    }

    # The declaration only; nothing calls the native function. SYSTEM_POWER_STATUS is four BYTEs
    # and two DWORDs (12 bytes), and GetSystemPowerStatus returns a Win32 BOOL and sets the last error.
    It 'declares SYSTEM_POWER_STATUS with the documented size and field offsets' {
        $status = [DmsTimingEvidence.SystemPower].GetNestedType('SystemPowerStatus', [System.Reflection.BindingFlags]::NonPublic)
        [System.Runtime.InteropServices.Marshal]::SizeOf([type]$status) | Should -Be 12
        $offsets = [ordered]@{}
        foreach ($field in 'ACLineStatus', 'BatteryFlag', 'BatteryLifePercent', 'SystemStatusFlag', 'BatteryLifeTime', 'BatteryFullLifeTime') {
            $offsets[$field] = [int][System.Runtime.InteropServices.Marshal]::OffsetOf([type]$status, $field)
        }
        @($offsets.Values) | Should -Be @(0, 1, 2, 3, 4, 8)
        @($status.GetFields() | ForEach-Object { $_.FieldType.Name }) | Should -Be @('Byte', 'Byte', 'Byte', 'Byte', 'UInt32', 'UInt32')
    }

    It 'declares GetSystemPowerStatus as a kernel32 call that returns a BOOL and sets the last error' {
        $method = [DmsTimingEvidence.SystemPower].GetMethod('GetSystemPowerStatus', [System.Reflection.BindingFlags]'NonPublic, Static')
        $import = [System.Reflection.CustomAttributeExtensions]::GetCustomAttribute[System.Runtime.InteropServices.DllImportAttribute]($method)
        $import.Value | Should -Be 'kernel32.dll'
        $import.SetLastError | Should -BeTrue
        $method.ReturnParameter.GetCustomAttributes([System.Runtime.InteropServices.MarshalAsAttribute], $false)[0].Value |
            Should -Be ([System.Runtime.InteropServices.UnmanagedType]::Bool)
        $method.GetParameters()[0].IsOut | Should -BeTrue
    }
}

Describe 'ConvertTo-PowerCheckRecord and Add-PowerRefusal' {
    BeforeEach {
        $folder = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $folder | Out-Null
        $log = Join-Path $folder 'refusals.log'
    }

    It 'copies only the reading''s own fields' {
        Set-PowerSequence 'offline'
        $reading = Get-PowerSource
        $reading.extra = $global:LaneSentinel
        $record = ConvertTo-PowerCheckRecord -Check before_setup -Reading $reading
        $record.reading.Keys | Should -Not -Contain 'extra'
        $record.condition_met | Should -BeFalse
        $record.unmet | Should -Be @('ac_line_offline')
    }

    It 'appends one line per refusal and never rewrites earlier lines' {
        'earlier line' | Set-Content -LiteralPath $log -Encoding utf8NoBOM
        Set-PowerSequence 'offline', 'saver_on'
        Add-PowerRefusal -Path $log -Lane 'endpoint-postgresql' -RunLabel 'pilot-a6' -Record (ConvertTo-PowerCheckRecord -Check before_setup -Reading (Get-PowerSource))
        Add-PowerRefusal -Path $log -Lane 'endpoint-postgresql' -RunLabel 'pilot-a6' -Record (ConvertTo-PowerCheckRecord -Check before_launch -Reading (Get-PowerSource))
        $lines = @(Get-Content -LiteralPath $log)
        $lines.Count | Should -Be 3
        $lines[0] | Should -Be 'earlier line'
        ($lines[1] | ConvertFrom-Json).unmet | Should -Be @('ac_line_offline')
        ($lines[1] | ConvertFrom-Json).reading.ac_line_status | Should -Be 0
        ($lines[2] | ConvertFrom-Json).check | Should -Be 'before_launch'
        ($lines[2] | ConvertFrom-Json).unmet | Should -Be @('battery_saver_on')
    }

    It 'refuses to record a check that met P' {
        Set-PowerSequence 'online'
        { Add-PowerRefusal -Path $log -Lane 'endpoint-mssql' -RunLabel 'pilot-a6' -Record (ConvertTo-PowerCheckRecord -Check before_setup -Reading (Get-PowerSource)) } | Should -Throw '*not a refusal*'
        Test-Path -LiteralPath $log | Should -BeFalse
    }

    It 'refuses to record a check after the launch as a refusal' {
        Set-PowerSequence 'offline'
        { Add-PowerRefusal -Path $log -Lane 'endpoint-mssql' -RunLabel 'pilot-a6' -Record (ConvertTo-PowerCheckRecord -Check after_exit -Reading (Get-PowerSource)) } | Should -Throw '*part of an attempt*'
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
        Set-PowerSequence 'online'
        $arguments = @{ RunLabel = 'pilot-a5'; Rounds = 20; WarmupRounds = 0; Seed = 301; OutputDir = $stage; CollectorRoot = $collector; TargetRoot = $target; WorkRoot = $work }
        $readProvenance = { Get-Content (Get-ChildItem $stage -Filter 'provenance-*.json').FullName -Raw | ConvertFrom-Json }
        $setupCalls = { @($global:LaneScenario.Calls | Where-Object { $_ -like '*setup-local-cms.ps1*' }) }
        $teardownCalls = { @($global:LaneScenario.Calls | Where-Object { $_ -like '*teardown-local-cms.ps1*' }) }
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

    It 'checks power before setup, before the launch and after the exit of a successful run' {
        & $lane @arguments
        $LASTEXITCODE | Should -Be 0
        $provenance = & $readProvenance
        @($provenance.power_checks.check) | Should -Be @('before_setup', 'before_launch', 'after_exit')
        @($provenance.power_checks | Where-Object { -not $_.condition_met }) | Should -BeNullOrEmpty
        $provenance.attempt_started | Should -BeTrue
        $provenance.environment_protocol.version | Should -Be 'E1'
        $provenance.environment_protocol.complete | Should -BeFalse
        Get-RefusalLine $stage | Should -BeNullOrEmpty
    }

    It 'refuses before setup, without setup or collection, when the power source is <Kind>' -ForEach @(
        @{ Kind = 'offline'; Unmet = 'ac_line_offline' }
        @{ Kind = 'unknown'; Unmet = 'ac_line_unknown' }
        @{ Kind = 'call_failed'; Unmet = 'call_failed' }
        @{ Kind = 'saver_on'; Unmet = 'battery_saver_on' }
        @{ Kind = 'wrong_scheme'; Unmet = 'scheme_not_balanced' }
        @{ Kind = 'scheme_exit'; Unmet = 'scheme_query_failed' }
    ) {
        Set-PowerSequence $Kind
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        & $setupCalls | Should -BeNullOrEmpty
        & $teardownCalls | Should -BeNullOrEmpty
        $global:LaneScenario.Calls | Should -Not -Contain 'dotnet test'
        $refusals = Get-RefusalLine $stage
        $refusals.Count | Should -Be 1
        $refusals[0].check | Should -Be 'before_setup'
        $refusals[0].lane | Should -Be 'endpoint-postgresql'
        $refusals[0].unmet | Should -Be @($Unmet)
        $provenance = & $readProvenance
        $provenance.attempt_started | Should -BeFalse
        $provenance.refused_at | Should -Be 'before_setup'
    }

    It 'refuses before the launch, after setup, and still tears the stack down' {
        Set-PowerSequence 'online', 'offline'
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        (& $setupCalls).Count | Should -Be 1
        (& $teardownCalls).Count | Should -Be 1
        $global:LaneScenario.Calls | Should -Not -Contain 'dotnet test'
        $refusals = Get-RefusalLine $stage
        $refusals.Count | Should -Be 1
        $refusals[0].check | Should -Be 'before_launch'
        (& $readProvenance).attempt_started | Should -BeFalse
    }

    It 'fails the attempt, without a refusal, when power is lost by the time the collector exits' {
        Set-PowerSequence 'online', 'online', 'offline'
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        $global:LaneScenario.Calls | Should -Contain 'dotnet test'
        (& $teardownCalls).Count | Should -Be 1
        $provenance = & $readProvenance
        $provenance.attempt_started | Should -BeTrue
        @($provenance.failures.stage) | Should -Be @('power-after-exit')
        $provenance.power_checks[2].unmet | Should -Be @('ac_line_offline')
        Get-RefusalLine $stage | Should -BeNullOrEmpty
    }

    It 'checks power after a failed collector and records both failures' {
        Set-PowerSequence 'online', 'online', 'saver_on'
        $global:LaneScenario.CollectorExit = 1
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        $provenance = & $readProvenance
        @($provenance.failures.stage) | Should -Be @('power-after-exit', 'run')
        $provenance.power_checks[2].check | Should -Be 'after_exit'
    }

    It 'checks power after a failed collector even when power held' {
        $global:LaneScenario.CollectorExit = 1
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        $provenance = & $readProvenance
        $provenance.power_checks[2].check | Should -Be 'after_exit'
        $provenance.power_checks[2].condition_met | Should -BeTrue
        @($provenance.failures.stage) | Should -Be @('run')
    }

    It 'keeps every refusal across runs' {
        Set-PowerSequence 'offline'
        & $lane @arguments
        Set-PowerSequence 'saver_on'
        & $lane @arguments
        $refusals = Get-RefusalLine $stage
        $refusals.Count | Should -Be 2
        $refusals[0].unmet | Should -Be @('ac_line_offline')
        $refusals[1].unmet | Should -Be @('battery_saver_on')
    }

    It 'refuses when the power observation itself fails, and keeps its text out of the evidence' {
        Set-PowerSequence 'native_throws'
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        & $setupCalls | Should -BeNullOrEmpty
        (Get-RefusalLine $stage)[0].unmet | Should -Be @('observation_error')
        Get-EvidenceText $stage | Should -Not -Match $global:LaneSentinel
        (Get-ChildItem $work -Filter '*.diagnostics.txt' | Get-Content -Raw) | Should -Match $global:LaneSentinel
    }
}

Describe 'lane-localdb.ps1 with synthetic command output, before the service starts' {
    BeforeAll {
        $lane = Join-Path $PSScriptRoot 'lane-localdb.ps1'
        function global:git { $global:LASTEXITCODE = 0; if ($args -contains 'rev-parse') { '1111111111111111111111111111111111111111' } }
        function global:Get-NetTCPConnection { $null }
        function global:docker { $global:LASTEXITCODE = 0 }
        function global:sqllocaldb {
            $global:LaneScenario.Calls.Add("sqllocaldb $($args -join ' ')")
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
        $global:LaneScenario = @{ Calls = [System.Collections.Generic.List[string]]::new() }
        Set-PowerSequence 'online'
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

    It 'refuses before any LocalDB or database work when the power source is <Kind>' -ForEach @(
        @{ Kind = 'offline'; Unmet = 'ac_line_offline' }
        @{ Kind = 'call_failed'; Unmet = 'call_failed' }
        @{ Kind = 'scheme_throws'; Unmet = 'scheme_query_failed' }
    ) {
        Set-PowerSequence $Kind
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        $global:LaneScenario.Calls | Should -BeNullOrEmpty
        Test-Path (Join-Path $work 'db') | Should -BeFalse
        $refusals = Get-RefusalLine $stage
        $refusals.Count | Should -Be 1
        $refusals[0].check | Should -Be 'before_setup'
        $refusals[0].lane | Should -Be 'endpoint-mssql'
        $refusals[0].unmet | Should -Be @($Unmet)
        $provenance = Get-Content (Get-ChildItem $stage -Filter 'provenance-*.json').FullName -Raw | ConvertFrom-Json
        $provenance.attempt_started | Should -BeFalse
        $provenance.refused_at | Should -Be 'before_setup'
        Get-EvidenceText $stage | Should -Not -Match $global:LaneSentinel
    }

    It 'checks power before setup and proceeds to LocalDB when it holds' {
        $env:LANE_TEST_ITERATIONS = '100000'
        & $lane @arguments
        $provenance = Get-Content (Get-ChildItem $stage -Filter 'provenance-*.json').FullName -Raw | ConvertFrom-Json
        @($provenance.power_checks.check) | Should -Be @('before_setup')
        $provenance.power_checks[0].condition_met | Should -BeTrue
        $global:LaneScenario.Calls | Should -Contain 'sqllocaldb start DMS1490'
        Get-RefusalLine $stage | Should -BeNullOrEmpty
    }
}

AfterAll {
    Remove-Item -LiteralPath Function:\global:Get-TestHash, Function:\global:Get-ValidSetting, Function:\global:Get-ValidFingerprint, Function:\global:Write-TestTrx, Function:\global:Write-TestSample, Function:\global:Get-EvidenceText -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath Alias:\Read-SystemPowerStatus, Function:\global:Get-TestPowerStatus, Function:\global:powercfg, Function:\global:New-PowerStep, Function:\global:Set-PowerSequence, Function:\global:Get-PowerStep, Function:\global:Get-RefusalLine -ErrorAction SilentlyContinue
    Remove-Variable -Name LaneScenario, LaneSentinel, LaneAssemblies, LaneFixtureClass, PowerScenario, BalancedScheme -Scope Global -ErrorAction SilentlyContinue
}
