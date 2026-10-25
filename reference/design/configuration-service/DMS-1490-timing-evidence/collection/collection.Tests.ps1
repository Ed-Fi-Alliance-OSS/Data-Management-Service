# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Checks of the collection lanes' failure paths with synthetic command output. Nothing here starts
# Docker, LocalDB, SQL Server, a build, a deployment or the collector: the native commands the lanes
# call are replaced by functions of the same name, which PowerShell resolves before executables.
# Power readings are synthetic too; no test calls a native power function.
#
#   pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion 5.7.1; Invoke-Pester -Path ./collection.Tests.ps1 -Output Detailed"
#
# The stand-ins run inside the lane script's own scope, where the test file's script scope is not
# visible, so the scenario they read and the helpers they call are global, and removed afterwards.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalVars', '', Justification = 'The command stand-ins run in the lane script scope and can only reach global state.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidGlobalFunctions', '', Justification = 'Stand-ins for native commands must be global to be resolved from the lane script.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseDeclaredVarsMoreThanAssignments', '', Justification = 'Pester shares BeforeAll and BeforeEach variables with the It blocks, which the analyzer does not follow.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Stand-ins keep the parameters of the commands and callbacks they replace, used or not.')]
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

    # The measured samples, warm-up samples and header one collector run writes (timing-evidence/2):
    # every round holds the three arms once, with one timestamp pair. -EditMeasured and -EditWarmup
    # receive and return the data rows; -Header overrides header fields; -RowCount keeps only that
    # many measured rows; -NoWarmup writes no warm-up file.
    $global:TestSamplesColumns = 'round,arm,position,elapsed_us,round_started_qpc,round_started_utc'

    function global:New-TestSampleRowSet([int] $Rounds, [long] $FirstQpc) {
        $arms = @('absent', 'unapproved_correct_secret', 'wrong_secret')
        $start = [datetime]::new(2026, 10, 24, 12, 0, 0, [DateTimeKind]::Utc)
        , @(for ($round = 0; $round -lt $Rounds; $round++) {
                $qpc = $FirstQpc + ($round * 30000)
                $utc = $start.AddMilliseconds($round * 3).ToString('o')
                for ($slot = 0; $slot -lt 3; $slot++) { "$round,$($arms[($round + $slot) % 3]),$slot,1000.000,$qpc,$utc" }
            })
    }

    function global:Write-TestSample {
        param(
            [string] $OutputDir, [string] $RunLabel, [string] $Backend, [int] $Rounds, [int] $Seed,
            [int] $RowCount = -1, [int] $WarmupRounds = 2, [hashtable] $Header = @{},
            [scriptblock] $EditMeasured = $null, [scriptblock] $EditWarmup = $null, [switch] $NoWarmup
        )
        $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")
        $stem = "$RunLabel-endpoint-$Backend-$stamp"
        $warmupName = "$RunLabel-warmup-endpoint-$Backend-$stamp.csv"
        $warmupRows = New-TestSampleRowSet $WarmupRounds 1000000
        $measuredRows = New-TestSampleRowSet $Rounds 9000000
        if ($RowCount -ge 0) { $measuredRows = @($measuredRows | Select-Object -First $RowCount) }
        if ($null -ne $EditMeasured) { $measuredRows = @(& $EditMeasured $measuredRows) }
        if ($null -ne $EditWarmup) { $warmupRows = @(& $EditWarmup $warmupRows) }
        @($global:TestSamplesColumns) + $measuredRows | Set-Content -LiteralPath (Join-Path $OutputDir "$stem.csv") -Encoding utf8NoBOM
        if (-not $NoWarmup) { @($global:TestSamplesColumns) + $warmupRows | Set-Content -LiteralPath (Join-Path $OutputDir $warmupName) -Encoding utf8NoBOM }
        $fields = [ordered]@{
            schema        = 'timing-evidence/2'
            run_label     = $RunLabel
            boundary      = "endpoint-$Backend"
            rounds        = $Rounds
            warmup_rounds = $WarmupRounds
            seed          = $Seed
            arms          = @('absent', 'wrong_secret', 'unapproved_correct_secret')
            samples_file  = "$stem.csv"
            measured_rows = $Rounds * 3
            warmup_file   = $warmupName
            warmup_rows   = $WarmupRounds * 3
        }
        foreach ($key in $Header.Keys) { $fields[$key] = $Header[$key] }
        $fields | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDir "$stem.json") -Encoding utf8NoBOM
    }

    function global:Get-EvidenceText([string] $OutputDir) {
        (Get-ChildItem -LiteralPath $OutputDir -File -Recurse | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
    }

    # Power stand-ins. The module reads the native status through Read-SystemPowerStatus and the
    # active scheme through Read-ActivePowerScheme; an alias resolves before any function, even
    # inside the module and after a lane imports it again, so no test reaches GetSystemPowerStatus
    # or PowerGetActiveScheme. Each read takes the next step of $global:PowerScenario.Sequence; the
    # last step repeats.
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
            SchemeSucceeded    = $true
            SchemeStatus       = [uint32]0
            SchemeFailure      = $null
            SchemeGuid         = $global:BalancedScheme
        }
        switch ($Kind) {
            'online' { }
            'offline' { $step.ACLineStatus = [byte]0 }
            'unknown' { $step.ACLineStatus = [byte]255 }
            # A failed call leaves the structure undefined; these values must not be reported.
            'call_failed' { $step.Succeeded = $false; $step.Win32Error = 87 }
            'saver_on' { $step.SystemStatusFlag = [byte]1 }
            'wrong_scheme' { $step.SchemeGuid = '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c' }
            # The native reader's own failures: a nonzero status, a null buffer, an unreadable or
            # unreleasable buffer, an empty GUID, and a failure without a code.
            'scheme_status' { $step.SchemeSucceeded = $false; $step.SchemeStatus = [uint32]2; $step.SchemeFailure = 'status_nonzero'; $step.SchemeGuid = $null }
            'scheme_null' { $step.SchemeSucceeded = $false; $step.SchemeFailure = 'null_result'; $step.SchemeGuid = $null }
            'scheme_read_error' { $step.SchemeSucceeded = $false; $step.SchemeFailure = 'read_error'; $step.SchemeGuid = $null }
            'scheme_release_error' { $step.SchemeSucceeded = $false; $step.SchemeFailure = 'release_error'; $step.SchemeGuid = $null }
            'scheme_empty' { $step.SchemeSucceeded = $false; $step.SchemeFailure = 'empty_guid'; $step.SchemeGuid = $null }
            'scheme_no_code' { $step.SchemeSucceeded = $false; $step.SchemeStatus = $null; $step.SchemeFailure = $null; $step.SchemeGuid = $null }
            # A reported success whose GUID is not in the normalized form.
            'scheme_invalid' { $step.SchemeGuid = '381B4222-F694-41F0-9685-FF5BB260DF2E' }
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

    function global:Get-TestActiveScheme {
        $step = Get-PowerStep
        if ($step.SchemeThrows) { throw "scheme query failed: $($global:LaneSentinel)" }
        [pscustomobject]@{ Succeeded = $step.SchemeSucceeded; Status = $step.SchemeStatus; Failure = $step.SchemeFailure; SchemeGuid = $step.SchemeGuid }
    }

    Set-Alias -Name Read-SystemPowerStatus -Value Get-TestPowerStatus -Scope Global
    Set-Alias -Name Read-ActivePowerScheme -Value Get-TestActiveScheme -Scope Global
    Set-PowerSequence 'online'

    function global:Get-RefusalLine([string] $OutputDir) {
        $path = Join-Path $OutputDir 'refusals.log'
        if (-not (Test-Path -LiteralPath $path)) { return , @() }
        , @(Get-Content -LiteralPath $path | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    }

    # Sampler stand-ins. The power file's columns are pinned here, independently of the module.
    $global:SamplerColumns = @('run_id', 'seq', 'qpc', 'utc', 'observation_error', 'call_succeeded', 'win32_error', 'ac_line_status', 'battery_flag', 'battery_life_percent', 'system_status_flag', 'scheme_query_succeeded', 'scheme_query_status', 'scheme_query_failure', 'scheme_guid')
    $global:QpcFrequency = [long]10000000

    # One spec per row: seq, offset (seconds after the first row), power kind, run id, clock skew.
    function global:New-TestSampleSpec([int] $Count, [double] $Step = 1, [string] $RunId = 'run-1') {
        , @(0..($Count - 1) | ForEach-Object { @{ Seq = $_ + 1; Offset = $_ * $Step; Kind = 'online'; RunId = $RunId; Skew = 0.0 } })
    }

    function global:ConvertTo-TestSampleLine([object[]] $Spec, [long] $StartQpc = 1000000000, [datetime] $StartUtc = [datetime]::new(2026, 10, 24, 12, 0, 0, [DateTimeKind]::Utc)) {
        , @($Spec | ForEach-Object {
                $online = $_.Kind -ne 'observation_error'
                $schemeFailed = $_.Kind -eq 'scheme_failed'
                $fields = @(
                    $_.RunId,
                    $_.Seq,
                    ($StartQpc + [long]($_.Offset * $global:QpcFrequency)),
                    $StartUtc.AddSeconds($_.Offset + $_.Skew).ToString('o'),
                    $(if ($online) { 'false' } else { 'true' }),
                    $(if ($online) { 'true' } else { '' }),
                    '',
                    $(if (-not $online) { '' } elseif ($_.Kind -eq 'offline') { '0' } else { '1' }),
                    $(if ($online) { '8' } else { '' }),
                    $(if ($online) { '80' } else { '' }),
                    $(if ($online) { '0' } else { '' }),
                    $(if (-not $online) { '' } elseif ($schemeFailed) { 'false' } else { 'true' }),
                    $(if (-not $online) { '' } elseif ($schemeFailed) { '2' } else { '0' }),
                    $(if ($schemeFailed) { 'status_nonzero' } else { '' }),
                    $(if ($online -and -not $schemeFailed) { $global:BalancedScheme } else { '' })
                )
                $fields -join ','
            })
    }

    $global:TestStartUtc = [datetime]::new(2026, 10, 24, 11, 59, 0, [DateTimeKind]::Utc)
    $global:TestCommit = '1111111111111111111111111111111111111111'

    # A collector checkout's Release output (two files) and the build record for it.
    function global:New-TestCollectorBuild([string] $CollectorRoot, [string] $ManifestPath) {
        $output = Join-Path $CollectorRoot 'src\config\tests\EdFi.DmsConfigurationService.Tests.E2E\bin\Release\net10.0'
        New-Item -ItemType Directory -Force -Path (Join-Path $output 'runtimes') | Out-Null
        Set-Content -LiteralPath (Join-Path $output 'EdFi.DmsConfigurationService.Tests.E2E.dll') -Value 'collector assembly' -NoNewline
        Set-Content -LiteralPath (Join-Path $output 'runtimes\native.dll') -Value 'native' -NoNewline
        Get-CollectorBuildManifest -CollectorRoot $CollectorRoot -Commit $global:TestCommit -Tree $global:TestCommit -BuildCommand 'dotnet build collector -c Release --no-incremental --nologo' -SdkVersion '10.0.100' |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ManifestPath -Encoding utf8NoBOM
        $output
    }

    function global:Write-TestReady {
        param(
            [string] $Path, [string] $RunId, [int] $ProcessId, [string] $FirstRow,
            [string] $RunLabel = 'pilot', [string] $LaneStamp = 'stamp', [datetime] $StartUtc = $global:TestStartUtc,
            [string] $Schema = 'power-sampler-ready/2', [string[]] $Omit = @()
        )
        $identity = [ordered]@{ run_label = $RunLabel; lane_stamp = $LaneStamp; sampler_pid = $ProcessId; sampler_start_utc = $StartUtc.ToString('o') }
        foreach ($field in $Omit) { $identity.Remove($field) }
        [ordered]@{ schema = $Schema; run_id = $RunId; identity = $identity; first_row = $FirstRow } |
            ConvertTo-Json | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
    }

    # A process stand-in that behaves like a process: HasExited and ExitCode come from its state
    # (ExitAfterChecks makes it exit after that many HasExited reads); WaitForExit runs the state's
    # OnWait action and reports whether it has exited, exiting on the first wait when ExitsOnStop is
    # set. Kill terminates it, unless KillThrows (throws, as a kill of a protected or already exited
    # process can), KillIneffective (returns, but the process lives on) or ExitsDuringKill (the
    # process exits on its own and the kill throws).
    function global:New-TestProcess([hashtable] $State) {
        foreach ($name in 'Exited', 'ExitCode', 'ExitsOnStop', 'Killed', 'OnWait', 'KillThrows', 'KillIneffective', 'ExitsDuringKill', 'ExitAfterChecks', 'StartTime') {
            if (-not $State.ContainsKey($name)) { $State[$name] = $null }
        }
        if ($null -eq $State.ExitsOnStop) { $State.ExitsOnStop = $true }
        if ($null -eq $State.ExitCode) { $State.ExitCode = 0 }
        if ($null -eq $State.StartTime) { $State.StartTime = $global:TestStartUtc }
        $State.WaitCalls = [System.Collections.Generic.List[int]]::new()
        $State.Checks = 0
        $process = [pscustomobject]@{ Id = $State.Id; State = $State; StartTime = $State.StartTime }
        $process | Add-Member -MemberType ScriptProperty -Name HasExited -Value {
            $this.State.Checks++
            if ($null -ne $this.State.ExitAfterChecks -and $this.State.Checks -gt $this.State.ExitAfterChecks) { $this.State.Exited = $true }
            [bool]$this.State.Exited
        }
        $process | Add-Member -MemberType ScriptProperty -Name ExitCode -Value { $this.State.ExitCode }
        $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {
            param([int] $Milliseconds)
            $this.State.WaitCalls.Add($Milliseconds)
            if ($null -ne $this.State.OnWait) { & $this.State.OnWait }
            if ($this.State.Exited) { return $true }
            if ($this.State.ExitsOnStop -and $this.State.WaitCalls.Count -eq 1) { $this.State.Exited = $true; return $true }
            $false
        }
        $process | Add-Member -MemberType ScriptMethod -Name Kill -Value {
            param([bool] $Tree)
            $this.State.Killed = $true
            if ($this.State.ExitsDuringKill) { $this.State.Exited = $true; throw [System.InvalidOperationException]::new('The process has already exited.') }
            if ($this.State.KillThrows) { throw [System.ComponentModel.Win32Exception]::new(5) }
            if (-not $this.State.KillIneffective) { $this.State.Exited = $true }
        }
        $process
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
        Remove-Item -LiteralPath Function:\git, Function:\tar, Function:\dotnet -ErrorAction SilentlyContinue
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
        $arguments = @{ TrxPath = $trx; OutputDir = $folder; RunLabel = 'pilot-a5'; Backend = 'postgresql'; Rounds = 20; WarmupRounds = 2; Seed = 301; StartedUtc = $started }
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
        { Assert-CollectorRun -ExitCode 0 @arguments } | Should -Throw '*found 0, 0 and 0*'
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
        { Assert-CollectorRun -ExitCode 0 @arguments } | Should -Throw '*found 0, 0 and 0*'
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

    It 'returns the warm-up file and both row counts with the schema' {
        Write-TestTrx $trx $tests $fixture
        Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301
        $summary = Assert-CollectorRun -ExitCode 0 @arguments
        $summary.schema | Should -Be 'timing-evidence/2'
        $summary.warmup_file | Should -BeLike 'pilot-a5-warmup-endpoint-postgresql-*.csv'
        $summary.warmup_rows | Should -Be 6
        $summary.sample_rows | Should -Be 60
    }

    It 'accepts a run with no warm-up rounds, whose warm-up file holds only the column row' {
        Write-TestTrx $trx $tests $fixture
        Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301 -WarmupRounds 0
        $arguments.WarmupRounds = 0
        (Assert-CollectorRun -ExitCode 0 @arguments).warmup_rows | Should -Be 0
    }

    It 'never selects the warm-up file with the measured pattern' {
        Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301
        @(Get-ChildItem -LiteralPath $folder -Filter 'pilot-a5-endpoint-postgresql-*.csv').Name | Should -Not -BeLike '*warmup*'
        @(Get-ChildItem -LiteralPath $folder -Filter 'pilot-a5-endpoint-postgresql-*.csv').Count | Should -Be 1
        @(Get-ChildItem -LiteralPath $folder -Filter 'pilot-a5-warmup-endpoint-postgresql-*.csv').Count | Should -Be 1
    }

    It 'refuses a run label that contains the segment warmup, whose pattern could reach another label''s warm-up file' {
        Write-TestTrx $trx $tests $fixture
        $arguments.RunLabel = 'pilot-warmup'
        { Assert-CollectorRun -ExitCode 0 @arguments } | Should -Throw '*segment "warmup"*'
    }

    It 'refuses <Name>' -ForEach @(
        @{ Name = 'a run with no warm-up file'; Sample = @{ NoWarmup = $true }; Message = '*found 1, 0 and 1*' }
        @{ Name = 'a header of the previous schema'; Sample = @{ Header = @{ schema = 'timing-evidence/1' } }; Message = '*timing-evidence/2*' }
        @{ Name = 'a header for another warm-up count'; Sample = @{ Header = @{ warmup_rounds = 3 } }; Message = '*does not match*' }
        @{ Name = 'a header naming another warm-up file'; Sample = @{ Header = @{ warmup_file = 'other.csv' } }; Message = '*different warm-up file*' }
        @{ Name = 'a header with another measured row count'; Sample = @{ Header = @{ measured_rows = 59 } }; Message = '*row counts*' }
        @{ Name = 'a header with another warm-up row count'; Sample = @{ Header = @{ warmup_rows = 5 } }; Message = '*row counts*' }
        @{ Name = 'a header naming other arms'; Sample = @{ Header = @{ arms = @('absent', 'wrong_secret', 'c0_no_derivation') } }; Message = '*three endpoint arms*' }
        @{ Name = 'a warm-up file missing a row'; Sample = @{ EditWarmup = { param($r) , @($r | Select-Object -SkipLast 1) } }; Message = '*warm-up samples file has 5 rows*' }
        @{ Name = 'a measured file with the previous columns'; Sample = @{ EditMeasured = { param($r) , @($r) } ; Header = @{} ; OldColumns = $true }; Message = '*measured samples file does not have*' }
        @{ Name = 'a round holding one arm twice'; Sample = @{ EditMeasured = { param($r) $r[1] = $r[1] -replace ',unapproved_correct_secret,', ',absent,' -replace ',wrong_secret,', ',absent,'; , $r } }; Message = '*each arm and position once*' }
        @{ Name = 'a round outside the expected range'; Sample = @{ EditMeasured = { param($r) $r[0] = $r[0] -replace '^0,', '20,'; , $r } }; Message = '*outside 0..19*' }
        @{ Name = 'two timestamps in one round'; Sample = @{ EditMeasured = { param($r) $f = $r[1].Split(','); $f[4] = '9000001'; $r[1] = $f -join ','; , $r } }; Message = '*more than one round timestamp*' }
        @{ Name = 'a round that does not start after the one before it'; Sample = @{ EditMeasured = { param($r) $r = @($r | ForEach-Object { $f = $_.Split(','); if ($f[0] -eq '1') { $f[4] = '9000000' }; $f -join ',' }); , $r } }; Message = '*does not start after*' }
        @{ Name = 'a malformed round time'; Sample = @{ EditMeasured = { param($r) $r = @($r | ForEach-Object { $f = $_.Split(','); if ($f[0] -eq '2') { $f[5] = '2026-10-24 12:00:00' }; $f -join ',' }); , $r } }; Message = '*row * of the measured samples file is malformed*' }
        @{ Name = 'a negative sample'; Sample = @{ EditMeasured = { param($r) $r[4] = $r[4] -replace ',1000\.000,', ',-1.000,'; , $r } }; Message = '*malformed*' }
        @{ Name = 'a warm-up row with an unknown arm'; Sample = @{ EditWarmup = { param($r) $r[0] = $r[0] -replace ',absent,', ',other,'; , $r } }; Message = '*warm-up samples file names an unexpected arm*' }
    ) {
        Write-TestTrx $trx $tests $fixture
        $options = @{}
        foreach ($key in $Sample.Keys) { if ($key -ne 'OldColumns') { $options[$key] = $Sample[$key] } }
        Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301 @options
        if ($Sample.OldColumns) {
            $measured = (Get-ChildItem -LiteralPath $folder -Filter 'pilot-a5-endpoint-postgresql-*.csv')[0].FullName
            $lines = @(Get-Content -LiteralPath $measured)
            $lines[0] = 'round,arm,position,elapsed_us'
            $lines | Set-Content -LiteralPath $measured -Encoding utf8NoBOM
        }
        { Assert-CollectorRun -ExitCode 0 @arguments } | Should -Throw $Message
    }

    It 'accepts a zero sample' {
        Write-TestTrx $trx $tests $fixture
        Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301 -EditMeasured { param($r) $r[4] = $r[4] -replace ',1000\.000,', ',0.000,'; , $r }
        (Assert-CollectorRun -ExitCode 0 @arguments).sample_rows | Should -Be 60
    }

    It 'refuses a <Phase> sample of <Value>, which parses but is not finite' -ForEach @(
        @{ Phase = 'measured'; Value = 'NaN' }
        @{ Phase = 'warm-up'; Value = 'NaN' }
        @{ Phase = 'measured'; Value = 'Infinity' }
        @{ Phase = 'warm-up'; Value = 'Infinity' }
        @{ Phase = 'measured'; Value = ('1' + ('0' * 400) + '.000') }
    ) {
        Write-TestTrx $trx $tests $fixture
        $value = $Value
        $edit = { param($r) $r[1] = $r[1] -replace ',1000\.000,', ",$value,"; , $r }.GetNewClosure()
        if ($Phase -eq 'measured') { Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301 -EditMeasured $edit }
        else { Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301 -EditWarmup $edit }
        { Assert-CollectorRun -ExitCode 0 @arguments } | Should -Throw "*of the $Phase samples file has a sample that is not finite and non-negative*"
    }

    It 'refuses a negative <Phase> sample' -ForEach @(@{ Phase = 'measured' }, @{ Phase = 'warm-up' }) {
        Write-TestTrx $trx $tests $fixture
        $edit = { param($r) $r[1] = $r[1] -replace ',1000\.000,', ',-1.000,'; , $r }
        if ($Phase -eq 'measured') { Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301 -EditMeasured $edit }
        else { Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301 -EditWarmup $edit }
        { Assert-CollectorRun -ExitCode 0 @arguments } | Should -Throw "*of the $Phase samples file is malformed*"
    }

    # The whole round changes, so that only the UTC-form check can refuse it: the round's rows still
    # share one timestamp pair, and its qpc still rises.
    It 'refuses a whole <Phase> round whose time is <Name>' -ForEach @(
        @{ Phase = 'measured'; Name = 'without a suffix'; Change = 'drop' }
        @{ Phase = 'measured'; Name = 'with an offset'; Change = 'offset' }
        @{ Phase = 'warm-up'; Name = 'without a suffix'; Change = 'drop' }
        @{ Phase = 'warm-up'; Name = 'with an offset'; Change = 'offset' }
    ) {
        Write-TestTrx $trx $tests $fixture
        $change = $Change
        $edit = {
            param($r)
            , @($r | ForEach-Object {
                    $f = $_.Split(',')
                    if ($f[0] -eq '1') { $f[5] = if ($change -eq 'drop') { $f[5].TrimEnd('Z') } else { $f[5].TrimEnd('Z') + '+03:00' } }
                    $f -join ','
                })
        }.GetNewClosure()
        if ($Phase -eq 'measured') { Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301 -EditMeasured $edit }
        else { Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301 -EditWarmup $edit }
        { Assert-CollectorRun -ExitCode 0 @arguments } | Should -Throw "*of the $Phase samples file has a round time that is not in the UTC 'o' form ending in Z*"
    }

    It 'refuses a <Phase> round time that is UTC but not in the writer''s exact layout as malformed' -ForEach @(@{ Phase = 'measured' }, @{ Phase = 'warm-up' }) {
        Write-TestTrx $trx $tests $fixture
        $edit = { param($r) , @($r | ForEach-Object { $f = $_.Split(','); if ($f[0] -eq '0') { $f[5] = $f[5] -replace '\.0000000Z$', '.0Z' }; $f -join ',' }) }
        if ($Phase -eq 'measured') { Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301 -EditMeasured $edit }
        else { Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301 -EditWarmup $edit }
        { Assert-CollectorRun -ExitCode 0 @arguments } | Should -Throw "*of the $Phase samples file is malformed*"
    }

    It 'refuses a second warm-up file for the run' {
        Write-TestTrx $trx $tests $fixture
        Write-TestSample $folder 'pilot-a5' 'postgresql' 20 301
        Copy-Item -LiteralPath (Get-ChildItem -LiteralPath $folder -Filter 'pilot-a5-warmup-*.csv')[0].FullName -Destination (Join-Path $folder 'pilot-a5-warmup-endpoint-postgresql-20991231T000000Z.csv')
        { Assert-CollectorRun -ExitCode 0 @arguments } | Should -Throw '*found 1, 2 and 1*'
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
        @{ Kind = 'scheme_status'; Unmet = @('scheme_query_failed') }
        @{ Kind = 'scheme_null'; Unmet = @('scheme_query_failed') }
        @{ Kind = 'scheme_read_error'; Unmet = @('scheme_query_failed') }
        @{ Kind = 'scheme_release_error'; Unmet = @('scheme_query_failed') }
        @{ Kind = 'scheme_empty'; Unmet = @('scheme_query_failed') }
        @{ Kind = 'scheme_no_code'; Unmet = @('scheme_query_failed') }
        @{ Kind = 'scheme_invalid'; Unmet = @('scheme_query_failed') }
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

    It 'records a failed scheme query as <Code> with status <Status>, never an exception text' -ForEach @(
        @{ Kind = 'scheme_status'; Code = 'status_nonzero'; Status = 2 }
        @{ Kind = 'scheme_null'; Code = 'null_result'; Status = 0 }
        @{ Kind = 'scheme_read_error'; Code = 'read_error'; Status = 0 }
        @{ Kind = 'scheme_release_error'; Code = 'release_error'; Status = 0 }
        @{ Kind = 'scheme_empty'; Code = 'empty_guid'; Status = 0 }
        @{ Kind = 'scheme_no_code'; Code = 'interop_error'; Status = $null }
        @{ Kind = 'scheme_invalid'; Code = 'invalid_result'; Status = 0 }
        @{ Kind = 'scheme_throws'; Code = 'interop_error'; Status = $null }
    ) {
        Set-PowerSequence $Kind
        $reading = Get-PowerSource
        $reading.scheme_query_succeeded | Should -BeFalse
        $reading.scheme_query_failure | Should -Be $Code
        $reading.scheme_query_status | Should -Be $Status
        $reading.scheme_guid | Should -BeNullOrEmpty
        ($reading | ConvertTo-Json) | Should -Not -Match $global:LaneSentinel
    }

    It 'records a successful scheme query with status 0 and the normalized GUID' {
        Set-PowerSequence 'online'
        $reading = Get-PowerSource
        $reading.scheme_query_succeeded | Should -BeTrue
        $reading.scheme_query_status | Should -Be 0
        $reading.scheme_query_failure | Should -BeNullOrEmpty
        $reading.scheme_guid | Should -BeExactly $global:BalancedScheme
    }

    It 'keeps the power-status error and the scheme status apart' {
        Set-PowerSequence 'call_failed'
        $reading = Get-PowerSource
        $reading.win32_error | Should -Be 87
        $reading.scheme_query_status | Should -Be 0
        $reading.scheme_query_succeeded | Should -BeTrue
        Set-PowerSequence 'scheme_status'
        $global:PowerScenario.Sequence[0].Win32Error = 5
        $reading = Get-PowerSource
        $reading.win32_error | Should -BeNullOrEmpty
        $reading.scheme_query_status | Should -Be 2
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

    It 'declares PowerGetActiveScheme as a powrprof call whose status is its DWORD return, not the last error' {
        $method = [DmsTimingEvidence.SystemPower].GetMethod('PowerGetActiveScheme', [System.Reflection.BindingFlags]'NonPublic, Static')
        $import = [System.Reflection.CustomAttributeExtensions]::GetCustomAttribute[System.Runtime.InteropServices.DllImportAttribute]($method)
        $import.Value | Should -Be 'powrprof.dll'
        $import.SetLastError | Should -BeFalse
        $method.ReturnType | Should -Be ([uint32])
        @($method.GetParameters().ParameterType.Name) | Should -Be @('IntPtr', 'IntPtr&')
        $method.GetParameters()[1].IsOut | Should -BeTrue
    }

    It 'declares LocalFree, which releases the scheme buffer, as a kernel32 call on a pointer' {
        $method = [DmsTimingEvidence.SystemPower].GetMethod('LocalFree', [System.Reflection.BindingFlags]'NonPublic, Static')
        $import = [System.Reflection.CustomAttributeExtensions]::GetCustomAttribute[System.Runtime.InteropServices.DllImportAttribute]($method)
        $import.Value | Should -Be 'kernel32.dll'
        $method.ReturnType | Should -Be ([System.IntPtr])
        @($method.GetParameters().ParameterType.Name) | Should -Be @('IntPtr')
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

Describe 'Active scheme query interpretation' {
    # InterpretActiveScheme with an injected query, GUID read and release. Buffers are real
    # unmanaged memory from AllocHGlobal (LocalAlloc on Windows), so the GUID read and LocalFree
    # release are exercised without calling PowerGetActiveScheme.
    BeforeAll {
        $power = [DmsTimingEvidence.SystemPower]
        $newBuffer = {
            param([guid] $Value)
            $buffer = [System.Runtime.InteropServices.Marshal]::AllocHGlobal(16)
            [System.Runtime.InteropServices.Marshal]::StructureToPtr($Value, $buffer, $false)
            $buffer
        }
    }

    BeforeEach {
        $calls = @{ Read = 0; Release = [System.Collections.Generic.List[long]]::new() }
        $recordRelease = [Func[IntPtr, bool]] {
            param([IntPtr] $Buffer)
            $calls.Release.Add($Buffer.ToInt64())
            [System.Runtime.InteropServices.Marshal]::FreeHGlobal($Buffer)
            $true
        }.GetNewClosure()
        $readGuid = [Func[IntPtr, guid]] { param([IntPtr] $Buffer) $calls.Read++; [DmsTimingEvidence.SystemPower]::ReadSchemeGuid($Buffer) }.GetNewClosure()
    }

    It 'returns the GUID of a successful query and releases its buffer exactly once' {
        $buffer = & $newBuffer ([guid]$global:BalancedScheme)
        $query = [Func[DmsTimingEvidence.SchemeQueryResult]] { [DmsTimingEvidence.SchemeQueryResult]@{ Status = 0; Buffer = $buffer } }.GetNewClosure()
        $result = $power::InterpretActiveScheme($query, $readGuid, $recordRelease)
        $result.Succeeded | Should -BeTrue
        $result.Status | Should -Be 0
        $result.SchemeGuid | Should -BeExactly $global:BalancedScheme
        @($calls.Release) | Should -Be @($buffer.ToInt64())
    }

    It 'releases a real LocalAlloc buffer with the real release' {
        $buffer = & $newBuffer ([guid]$global:BalancedScheme)
        $query = [Func[DmsTimingEvidence.SchemeQueryResult]] { [DmsTimingEvidence.SchemeQueryResult]@{ Status = 0; Buffer = $buffer } }.GetNewClosure()
        $result = $power::InterpretActiveScheme($query, [Func[IntPtr, guid]] $power::ReadSchemeGuid, [Func[IntPtr, bool]] $power::ReleaseSchemeGuid)
        $result.Succeeded | Should -BeTrue
        $result.SchemeGuid | Should -BeExactly $global:BalancedScheme
    }

    It 'reports a nonzero status without reading or releasing the buffer it does not own' {
        # The buffer is not real memory, so the read and release here only count; neither may run.
        $query = [Func[DmsTimingEvidence.SchemeQueryResult]] { [DmsTimingEvidence.SchemeQueryResult]@{ Status = 2; Buffer = [IntPtr]0x1234 } }
        $countRead = [Func[IntPtr, guid]] { param([IntPtr] $Buffer) $calls.Read++; [guid]::NewGuid() }.GetNewClosure()
        $countRelease = [Func[IntPtr, bool]] { param([IntPtr] $Buffer) $calls.Release.Add($Buffer.ToInt64()); $true }.GetNewClosure()
        $result = $power::InterpretActiveScheme($query, $countRead, $countRelease)
        $result.Succeeded | Should -BeFalse
        $result.Status | Should -Be 2
        $result.Failure | Should -Be 'status_nonzero'
        $calls.Read | Should -Be 0
        $calls.Release.Count | Should -Be 0
    }

    It 'reports a successful status with a null buffer as null_result, releasing nothing' {
        $query = [Func[DmsTimingEvidence.SchemeQueryResult]] { [DmsTimingEvidence.SchemeQueryResult]@{ Status = 0; Buffer = [IntPtr]::Zero } }
        $result = $power::InterpretActiveScheme($query, $readGuid, $recordRelease)
        $result.Failure | Should -Be 'null_result'
        $calls.Release.Count | Should -Be 0
    }

    It 'reports <Name> as <Failure> and still releases the buffer once' -ForEach @(
        # Literal: -ForEach data is built at discovery, before BeforeAll sets the globals.
        @{ Name = 'a read that throws'; Failure = 'read_error'; ReadThrows = $true; Value = '381b4222-f694-41f0-9685-ff5bb260df2e' }
        @{ Name = 'an empty GUID'; Failure = 'empty_guid'; ReadThrows = $false; Value = [guid]::Empty.ToString() }
    ) {
        $buffer = & $newBuffer ([guid]$Value)
        $query = [Func[DmsTimingEvidence.SchemeQueryResult]] { [DmsTimingEvidence.SchemeQueryResult]@{ Status = 0; Buffer = $buffer } }.GetNewClosure()
        $read = if ($ReadThrows) { [Func[IntPtr, guid]] { param([IntPtr] $Buffer) throw 'unreadable' } } else { $readGuid }
        $result = $power::InterpretActiveScheme($query, $read, $recordRelease)
        $result.Succeeded | Should -BeFalse
        $result.Failure | Should -Be $Failure
        @($calls.Release) | Should -Be @($buffer.ToInt64())
    }

    It 'fails the reading as release_error when the release <Name>' -ForEach @(
        @{ Name = 'reports failure'; Throws = $false }
        @{ Name = 'throws'; Throws = $true }
    ) {
        $buffer = & $newBuffer ([guid]$global:BalancedScheme)
        $query = [Func[DmsTimingEvidence.SchemeQueryResult]] { [DmsTimingEvidence.SchemeQueryResult]@{ Status = 0; Buffer = $buffer } }.GetNewClosure()
        $throws = $Throws
        $release = [Func[IntPtr, bool]] { param([IntPtr] $Buffer) [System.Runtime.InteropServices.Marshal]::FreeHGlobal($Buffer); if ($throws) { throw 'release failed' }; $false }.GetNewClosure()
        $result = $power::InterpretActiveScheme($query, $readGuid, $release)
        $result.Succeeded | Should -BeFalse
        $result.Failure | Should -Be 'release_error'
        $result.SchemeGuid | Should -BeNullOrEmpty
    }

    It 'reports a query that throws, or returns nothing, as interop_error, releasing nothing' -ForEach @(
        @{ Query = { throw 'interop failed' } }
        @{ Query = { $null } }
    ) {
        $result = $power::InterpretActiveScheme([Func[DmsTimingEvidence.SchemeQueryResult]] $Query, $readGuid, $recordRelease)
        $result.Failure | Should -Be 'interop_error'
        $result.Status | Should -BeNullOrEmpty
        $calls.Release.Count | Should -Be 0
    }

    It 'refuses to load over an older definition of the native type left in the session' {
        # A fresh process, so that this session's real type is not in the way; nothing native runs.
        $module = Join-Path $PSScriptRoot 'CollectionLane.psm1'
        $script = "Add-Type -TypeDefinition 'namespace DmsTimingEvidence { public static class SystemPower { public static int Read() { return 0; } } }'; try { Import-Module '$module' -Force; 'imported' } catch { 'refused: ' + `$_.Exception.Message }"
        $output = & ([System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName) -NoProfile -NonInteractive -Command $script
        ($output -join ' ') | Should -Match 'refused: An older DmsTimingEvidence.SystemPower is loaded'
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

Describe 'Test-SamplerCoverage' {
    BeforeAll {
        # Rows every second from qpc 1000000000; the collection runs from 2.5 s to 6.5 s.
        $start = [long]1000000000
        $launch = $start + [long](2.5 * $global:QpcFrequency)
        $exit = $start + [long](6.5 * $global:QpcFrequency)
        $coverage = {
            param([object[]] $Spec)
            $lines = @($global:SamplerColumns -join ',') + (ConvertTo-TestSampleLine $Spec)
            $failures = Test-SamplerCoverage -Line $lines -RunId 'run-1' -QpcFrequency $global:QpcFrequency -LaunchQpc $launch -ExitQpc $exit
            , $failures
        }
    }

    It 'holds for contiguous rows that bracket the collection and all meet P' {
        $failures = & $coverage (New-TestSampleSpec 10)
        $failures | Should -BeNullOrEmpty
    }

    It 'fails <Code> when <Name>' -ForEach @(
        @{ Name = 'the file has no rows'; Code = 'no_rows'; Change = { param($s) , @() } }
        @{ Name = 'the first row comes after the launch'; Code = 'no_row_before_launch'; Change = { param($s) , @($s | ForEach-Object { $_.Offset += 3; $_ }) } }
        @{ Name = 'the last row comes before the exit'; Code = 'no_row_after_exit'; Change = { param($s) , @($s[0..5]) } }
        @{ Name = 'a row is missing'; Code = 'seq_not_contiguous'; Change = { param($s) , @($s[0..3] + $s[5..9]) } }
        @{ Name = 'a row repeats a seq'; Code = 'seq_not_contiguous'; Change = { param($s) $s[4].Seq = 4; , $s } }
        @{ Name = 'a gap inside the window exceeds 3 s'; Code = 'gap_exceeded'; Change = { param($s) foreach ($row in $s[5..9]) { $row.Offset += 2.5 }; , $s } }
        @{ Name = 'the wall clock steps 1.5 s against qpc'; Code = 'clock_step'; Change = { param($s) foreach ($row in $s[4..9]) { $row.Skew = 1.5 }; , $s } }
        @{ Name = 'qpc does not increase'; Code = 'qpc_not_increasing'; Change = { param($s) $s[4].Offset = $s[3].Offset; , $s } }
        @{ Name = 'a row names another run'; Code = 'identity_changed'; Change = { param($s) $s[7].RunId = 'run-2'; , $s } }
        @{ Name = 'a row inside the window is offline'; Code = 'power_unmet_in_window'; Change = { param($s) $s[4].Kind = 'offline'; , $s } }
        @{ Name = 'the bracketing row before the launch is offline'; Code = 'power_unmet_in_window'; Change = { param($s) $s[2].Kind = 'offline'; , $s } }
        @{ Name = 'the bracketing row after the exit is offline'; Code = 'power_unmet_in_window'; Change = { param($s) $s[7].Kind = 'offline'; , $s } }
        @{ Name = 'a row inside the window could not be observed'; Code = 'power_unmet_in_window'; Change = { param($s) $s[5].Kind = 'observation_error'; , $s } }
    ) {
        $spec = & $Change (New-TestSampleSpec 10)
        $failures = & $coverage $spec
        $failures | Should -Contain $Code
    }

    It 'accepts a gap of exactly 3 s and a clock difference of exactly 1 s' {
        $spec = New-TestSampleSpec 10
        foreach ($row in $spec[5..9]) { $row.Offset += 2 }
        foreach ($row in $spec[6..9]) { $row.Skew = 1 }
        $failures = & $coverage $spec
        $failures | Should -BeNullOrEmpty
    }

    It 'ignores an offline row and a long gap before the bracketing row' {
        $spec = New-TestSampleSpec 10
        $spec[0].Kind = 'offline'
        $spec[0].Offset = -10
        $failures = & $coverage $spec
        $failures | Should -BeNullOrEmpty
    }

    It 'refuses <Name> instead of reading it loosely' -ForEach @(
        @{ Name = 'a capitalised Boolean'; Change = { param($l) $l -replace ',true,,1,', ',True,,1,' } }
        @{ Name = 'a line cut short'; Change = { param($l) $l.Substring(0, $l.LastIndexOf(',')) } }
        @{ Name = 'a line with an extra field'; Change = { param($l) "$l,extra" } }
        @{ Name = 'an empty line'; Change = { param($l) '' } }
    ) {
        $lines = ConvertTo-TestSampleLine (New-TestSampleSpec 10)
        $lines[4] = & $Change $lines[4]
        $failures = Test-SamplerCoverage -Line (@($global:SamplerColumns -join ',') + $lines) -RunId 'run-1' -QpcFrequency $global:QpcFrequency -LaunchQpc $launch -ExitQpc $exit
        $failures | Should -Be @('row_malformed')
    }

    It 'refuses a file whose header is not the power columns' {
        $lines = @(($global:SamplerColumns | Select-Object -SkipLast 1) -join ',') + (ConvertTo-TestSampleLine (New-TestSampleSpec 10))
        $failures = Test-SamplerCoverage -Line $lines -RunId 'run-1' -QpcFrequency $global:QpcFrequency -LaunchQpc $launch -ExitQpc $exit
        $failures | Should -Be @('header_malformed')
    }

    It 'reads a row that could not be observed, with its empty trailing fields, as an observation error' {
        $spec = New-TestSampleSpec 1
        $spec[0].Kind = 'observation_error'
        $row = ConvertFrom-PowerSampleRow (ConvertTo-TestSampleLine $spec)[0]
        $row.observation_error | Should -BeTrue
        $row.scheme_guid | Should -BeNullOrEmpty
    }

    It 'refuses a line of <Count> fields by its field count' -ForEach @(@{ Count = 13 }, @{ Count = 15 }) {
        $fields = [System.Collections.Generic.List[string]](ConvertTo-TestSampleLine (New-TestSampleSpec 1))[0].Split(',')
        if ($Count -lt $fields.Count) { $fields.RemoveAt($fields.Count - 1) } else { $fields.Add('extra') }
        { ConvertFrom-PowerSampleRow ($fields -join ',') } | Should -Throw '*wrong number of fields*'
    }

    It 'reads the scheme query status and failure of a row whose query failed' {
        $spec = New-TestSampleSpec 1
        $spec[0].Kind = 'scheme_failed'
        $row = ConvertFrom-PowerSampleRow (ConvertTo-TestSampleLine $spec)[0]
        $row.scheme_query_succeeded | Should -BeFalse
        $row.scheme_query_status | Should -Be 2
        $row.scheme_query_failure | Should -Be 'status_nonzero'
        $unmet = Get-PowerConditionFailure $row
        $unmet | Should -Be @('scheme_query_failed')
    }

    It 'parses <Value> in <Column> as malformed' -ForEach @(
        @{ Column = 'scheme_query_status'; Value = '-1' }
        @{ Column = 'seq'; Value = '-1' }
        @{ Column = 'ac_line_status'; Value = '1.0' }
        @{ Column = 'observation_error'; Value = 'False' }
        @{ Column = 'utc'; Value = '2026-10-24 12:00:00' }
    ) {
        $fields = (ConvertTo-TestSampleLine (New-TestSampleSpec 1))[0].Split(',')
        $fields[[array]::IndexOf($global:SamplerColumns, $Column)] = $Value
        { ConvertFrom-PowerSampleRow ($fields -join ',') } | Should -Throw
    }
}

Describe 'Wait-PowerSamplerReady' {
    BeforeEach {
        $folder = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $folder | Out-Null
        $ready = Join-Path $folder 'ready.json'
        $onlineRow = (ConvertTo-TestSampleLine (New-TestSampleSpec 1 -RunId 'run-1'))[0]
        # The injected clock is monotonic seconds; a pause advances it by its length.
        $clock = @{ Seconds = 1000.0; Pauses = 0; WriteAt = 0; Row = $onlineRow; ReadyPath = $ready }
        $wait = @{
            RunId     = 'run-1'
            RunLabel  = 'pilot'
            LaneStamp = 'stamp'
            Clock     = { $clock.Seconds }.GetNewClosure()
            Pause     = {
                param([int] $Milliseconds)
                $clock.Pauses++
                # A wait that ignores its deadline must fail here, not loop for ever.
                if ($clock.Pauses -gt 1000) { throw 'The wait did not end at its deadline.' }
                $clock.Seconds += $Milliseconds / 1000.0
                if ($clock.Pauses -eq $clock.WriteAt) { Write-TestReady $clock.ReadyPath 'run-1' 4242 $clock.Row }
            }.GetNewClosure()
        }
        $process = New-TestProcess @{ Id = 4242 }
    }

    It 'returns the first row when the ready record arrives before the timeout' {
        $clock.WriteAt = 3
        $result = Wait-PowerSamplerReady -Process $process -ReadyPath $ready @wait
        $result.failure | Should -BeNullOrEmpty
        $result.first_row.seq | Should -Be 1
        $clock.Pauses | Should -Be 3
    }

    It 'times out after 30 s of the injected clock' {
        $result = Wait-PowerSamplerReady -Process $process -ReadyPath $ready @wait
        $result.failure | Should -Be 'sampler_ready_timeout'
        $clock.Pauses | Should -Be 150
    }

    It 'refuses a ready record that appears only at the deadline' {
        $clock.WriteAt = 150
        $result = Wait-PowerSamplerReady -Process $process -ReadyPath $ready @wait
        $result.failure | Should -Be 'sampler_ready_timeout'
        Test-Path -LiteralPath $ready | Should -BeTrue
    }

    It 'stops waiting as soon as the sampler has exited' {
        $process.State.Exited = $true
        $result = Wait-PowerSamplerReady -Process $process -ReadyPath $ready @wait
        $result.failure | Should -Be 'sampler_exited_before_ready'
        $clock.Pauses | Should -Be 0
    }

    It 'refuses a valid ready record when the sampler has already exited' {
        Write-TestReady $ready 'run-1' 4242 $onlineRow
        $process.State.Exited = $true
        (Wait-PowerSamplerReady -Process $process -ReadyPath $ready @wait).failure | Should -Be 'sampler_exited_before_ready'
    }

    It 'refuses a valid ready record when the sampler exits while it is read' {
        Write-TestReady $ready 'run-1' 4242 $onlineRow
        $process.State.ExitAfterChecks = 1
        (Wait-PowerSamplerReady -Process $process -ReadyPath $ready @wait).failure | Should -Be 'sampler_exited_before_ready'
        $process.State.Checks | Should -Be 2
    }

    It 'refuses a ready record for <Name>' -ForEach @(
        @{ Name = 'another run'; Field = @{ RunId = 'run-2' } }
        @{ Name = 'another process'; Field = @{ ProcessId = 1 } }
        @{ Name = 'another process start time'; Field = @{ StartUtc = [datetime]::new(2026, 10, 24, 11, 59, 1, [DateTimeKind]::Utc) } }
        @{ Name = 'another run label'; Field = @{ RunLabel = 'final' } }
        @{ Name = 'another lane stamp'; Field = @{ LaneStamp = 'other' } }
        @{ Name = 'a first row that is not row 1'; Field = @{}; Seq = 2 }
    ) {
        $spec = New-TestSampleSpec 1 -RunId 'run-1'
        if ($null -ne $Seq) { $spec[0].Seq = $Seq }
        $arguments = @{ Path = $ready; RunId = 'run-1'; ProcessId = 4242; FirstRow = (ConvertTo-TestSampleLine $spec)[0] }
        foreach ($key in $Field.Keys) { $arguments[$key] = $Field[$key] }
        Write-TestReady @arguments
        (Wait-PowerSamplerReady -Process $process -ReadyPath $ready @wait).failure | Should -Be 'sampler_identity_mismatch'
    }

    It 'refuses a ready record that is <Name> as malformed' -ForEach @(
        @{ Name = 'not JSON'; Text = '{ not json' }
        @{ Name = 'of another schema'; Field = @{ Schema = 'power-sampler-ready/0' } }
        @{ Name = 'of the previous schema, without the scheme status'; Field = @{ Schema = 'power-sampler-ready/1' } }
        @{ Name = 'missing the start time'; Field = @{ Omit = @('sampler_start_utc') } }
        @{ Name = 'missing the process id'; Field = @{ Omit = @('sampler_pid') } }
        @{ Name = 'missing the run label'; Field = @{ Omit = @('run_label') } }
    ) {
        if ($null -ne $Text) { $Text | Set-Content -LiteralPath $ready }
        else {
            $arguments = @{ Path = $ready; RunId = 'run-1'; ProcessId = 4242; FirstRow = $onlineRow }
            foreach ($key in $Field.Keys) { $arguments[$key] = $Field[$key] }
            Write-TestReady @arguments
        }
        (Wait-PowerSamplerReady -Process $process -ReadyPath $ready @wait).failure | Should -Be 'sampler_ready_malformed'
    }

    It 'refuses a sampler whose start time cannot be read' {
        Write-TestReady $ready 'run-1' 4242 $onlineRow
        $process | Add-Member -MemberType ScriptProperty -Name StartTime -Value { throw [System.ComponentModel.Win32Exception]::new(5) } -Force
        (Wait-PowerSamplerReady -Process $process -ReadyPath $ready @wait).failure | Should -Be 'sampler_identity_mismatch'
    }

    It 'refuses a first row that does not meet P, and records it as a refusal' {
        $spec = New-TestSampleSpec 1 -RunId 'run-1'
        $spec[0].Kind = 'offline'
        Write-TestReady $ready 'run-1' 4242 (ConvertTo-TestSampleLine $spec)[0]
        $result = Wait-PowerSamplerReady -Process $process -ReadyPath $ready @wait
        $result.failure | Should -Be 'sampler_ready_not_p'
        $result.unmet | Should -Be @('ac_line_offline')
        $record = ConvertTo-SamplerRefusalRecord -Ready $result
        $record.check | Should -Be 'before_launch'
        $record.unmet | Should -Be @('sampler_ready_not_p', 'ac_line_offline')
        $record.reading.ac_line_status | Should -Be 0
    }

    It 'records a sampler that never became ready as a refusal without a reading' {
        $process.State.Exited = $true
        $record = ConvertTo-SamplerRefusalRecord -Ready (Wait-PowerSamplerReady -Process $process -ReadyPath $ready @wait)
        $record.unmet | Should -Be @('sampler_exited_before_ready')
        $record.reading | Should -BeNullOrEmpty
    }
}

Describe 'Close-PowerSampler' {
    BeforeEach {
        $stop = Join-Path $TestDrive "stop-$([guid]::NewGuid().ToString('N'))"
    }

    It 'signals the sampler and records a clean, confirmed stop' {
        $process = New-TestProcess @{ Id = 1 }
        $result = Close-PowerSampler -Process $process -StopPath $stop
        Test-Path -LiteralPath $stop | Should -BeTrue
        $process.State.WaitCalls | Should -Be @(10000)
        $result.terminated | Should -BeTrue
        $result.kill_requested | Should -BeFalse
        $failures = Get-SamplerLifecycleFailure $result
        $failures | Should -BeNullOrEmpty
    }

    It 'reports <Codes> when <Name>' -ForEach @(
        @{ Name = 'the sampler had already exited'; Codes = @('sampler_dead_at_exit'); State = @{ Id = 1; Exited = $true } }
        @{ Name = 'the sampler exits nonzero'; Codes = @('sampler_exit_nonzero'); State = @{ Id = 1; ExitCode = 1 } }
        @{ Name = 'the sampler is killed after the timeout'; Codes = @('sampler_slow_shutdown'); State = @{ Id = 1; ExitsOnStop = $false } }
        @{ Name = 'the kill throws and the sampler lives on'; Codes = @('sampler_slow_shutdown', 'sampler_not_terminated'); State = @{ Id = 1; ExitsOnStop = $false; KillThrows = $true } }
        @{ Name = 'the kill returns but the sampler lives on'; Codes = @('sampler_slow_shutdown', 'sampler_not_terminated'); State = @{ Id = 1; ExitsOnStop = $false; KillIneffective = $true } }
        @{ Name = 'the sampler exits on its own as the kill throws'; Codes = @('sampler_slow_shutdown'); State = @{ Id = 1; ExitsOnStop = $false; ExitsDuringKill = $true } }
    ) {
        $process = New-TestProcess $State.Clone()
        $failures = Get-SamplerLifecycleFailure (Close-PowerSampler -Process $process -StopPath $stop)
        $failures | Should -Be $Codes
    }

    It 'confirms a forced termination by the wait after the kill' {
        $process = New-TestProcess @{ Id = 1; ExitsOnStop = $false }
        $result = Close-PowerSampler -Process $process -StopPath $stop
        $result.kill_requested | Should -BeTrue
        $result.kill_error | Should -BeNullOrEmpty
        $result.terminated | Should -BeTrue
        $process.State.WaitCalls | Should -Be @(10000, 5000)
    }

    It 'records the kill error type and no termination when the kill fails' {
        $process = New-TestProcess @{ Id = 1; ExitsOnStop = $false; KillThrows = $true }
        $result = Close-PowerSampler -Process $process -StopPath $stop
        $result.kill_requested | Should -BeTrue
        $result.kill_error | Should -Be 'System.ComponentModel.Win32Exception'
        $result.terminated | Should -BeFalse
    }

    It 'does not count a kill as termination when the wait after it reports the process alive' {
        $process = New-TestProcess @{ Id = 1; ExitsOnStop = $false; KillIneffective = $true }
        $result = Close-PowerSampler -Process $process -StopPath $stop
        $result.kill_error | Should -BeNullOrEmpty
        $result.terminated | Should -BeFalse
    }

    It 'confirms termination when the sampler exits on its own as the kill throws' {
        $process = New-TestProcess @{ Id = 1; ExitsOnStop = $false; ExitsDuringKill = $true }
        $result = Close-PowerSampler -Process $process -StopPath $stop
        $result.kill_error | Should -Be 'System.InvalidOperationException'
        $result.terminated | Should -BeTrue
    }
}

Describe 'Invoke-PowerSampler' {
    BeforeEach {
        $folder = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $folder | Out-Null
        $paths = @{
            PowerPath    = Join-Path $folder 'power.csv'
            OptionalPath = Join-Path $folder 'optional.csv'
            ReadyPath    = Join-Path $folder 'ready.json'
            StopPath     = Join-Path $folder 'stop'
        }
        # Each clock read advances 0.1 ms; a pause advances by its length. Nothing sleeps.
        $time = @{ Qpc = [long]1000000000; Utc = [datetime]::new(2026, 10, 24, 12, 0, 0, [DateTimeKind]::Utc); Pauses = [System.Collections.Generic.List[int]]::new(); StopAfter = 0; StopPath = $paths.StopPath; PowerKinds = @('online'); Reads = 0 }
        $online = [ordered]@{ utc = 'x'; call_succeeded = $true; win32_error = $null; ac_line_status = 1; battery_flag = 8; battery_life_percent = 80; system_status_flag = 0; scheme_query_succeeded = $true; scheme_query_status = 0; scheme_query_failure = $null; scheme_guid = $global:BalancedScheme }
        $loop = @{
            RunId          = 'run-1'
            Identity       = [ordered]@{ run_label = 'pilot'; sampler_pid = 4242 }
            Role           = [ordered]@{ docker_vm = [ordered]@{ pid = 7; start_utc = 'x'; na = $null } }
            OptionalColumn = @('available_mbytes', 'available_mbytes_na', 'cpu_background_floored')
            ReadOptional   = { param([long] $Qpc) [ordered]@{ available_mbytes = '512.000'; available_mbytes_na = ''; cpu_background_floored = 'false' } }
            QpcFrequency   = $global:QpcFrequency
            ReadClock      = { $time.Qpc += 1000; $time.Utc = $time.Utc.AddTicks(1000); [ordered]@{ qpc = $time.Qpc; utc = $time.Utc } }.GetNewClosure()
            Pause          = {
                param([int] $Milliseconds)
                $time.Pauses.Add($Milliseconds)
                # A loop that ignores its stop signal must fail here, not run for ever.
                if ($time.Pauses.Count -gt 100) { throw 'The sampler loop did not stop.' }
                $time.Qpc += [long]$Milliseconds * 10000
                $time.Utc = $time.Utc.AddMilliseconds($Milliseconds)
                if ($time.StopAfter -gt 0 -and $time.Pauses.Count -eq $time.StopAfter) { 'stop' | Set-Content -LiteralPath $time.StopPath }
            }.GetNewClosure()
            ReadPower      = {
                $time.Reads++
                $kind = $time.PowerKinds[[Math]::Min($time.Reads, $time.PowerKinds.Count) - 1]
                if ($kind -eq 'throws') { throw 'power read failed' }
                $reading = [ordered]@{}
                foreach ($key in $online.Keys) { $reading[$key] = $online[$key] }
                if ($kind -eq 'offline') { $reading.ac_line_status = 0 }
                $reading
            }.GetNewClosure()
        }
    }

    It 'writes one power row and one optional row per tick, with contiguous seq' {
        Invoke-PowerSampler @loop @paths -MaxTicks 4
        $rows = @(Import-Csv -LiteralPath $paths.PowerPath)
        @($rows.seq) | Should -Be @('1', '2', '3', '4')
        (Get-Content -LiteralPath $paths.PowerPath -TotalCount 1) | Should -Be ($global:SamplerColumns -join ',')
        @(Import-Csv -LiteralPath $paths.OptionalPath).Count | Should -Be 4
        $failures = Test-SamplerCoverage -Line (Get-Content -LiteralPath $paths.PowerPath) -RunId 'run-1' -QpcFrequency $global:QpcFrequency -LaunchQpc ([long]$rows[0].qpc) -ExitQpc ([long]$rows[3].qpc)
        $failures | Should -BeNullOrEmpty
    }

    It 'writes the ready record after the first row, with the identity, roles, columns and that row' {
        Invoke-PowerSampler @loop @paths -MaxTicks 2
        $ready = Get-Content -LiteralPath $paths.ReadyPath -Raw | ConvertFrom-Json
        $ready.run_id | Should -Be 'run-1'
        $ready.identity.sampler_pid | Should -Be 4242
        $ready.roles.docker_vm.pid | Should -Be 7
        @($ready.optional_columns) | Should -Be $loop.OptionalColumn
        $ready.first_row | Should -Be (Get-Content -LiteralPath $paths.PowerPath)[1]
    }

    It 'appends the power row of a tick before reading its optional values' {
        $seen = [System.Collections.Generic.List[int]]::new()
        $power = $paths.PowerPath
        $loop.ReadOptional = { param([long] $Qpc) $seen.Add(@(Import-Csv -LiteralPath $power).Count); $null }.GetNewClosure()
        Invoke-PowerSampler @loop @paths -MaxTicks 3
        @($seen) | Should -Be @(1, 2, 3)
    }

    It 'records NA with counter_error, never a number, when the optional read fails' {
        $loop.ReadOptional = { param([long] $Qpc) throw 'counter read failed' }
        Invoke-PowerSampler @loop @paths -MaxTicks 2
        $optional = @(Import-Csv -LiteralPath $paths.OptionalPath)
        @($optional.available_mbytes) | Should -Be @('NA', 'NA')
        @($optional.available_mbytes_na) | Should -Be @('counter_error', 'counter_error')
        @($optional.cpu_background_floored) | Should -Be @('NA', 'NA')
        @(Import-Csv -LiteralPath $paths.PowerPath).Count | Should -Be 2
    }

    It 'records a power read that throws as an observation error, which cannot meet P' {
        $time.PowerKinds = @('online', 'throws', 'online')
        Invoke-PowerSampler @loop @paths -MaxTicks 3
        $rows = @(Import-Csv -LiteralPath $paths.PowerPath)
        $rows[1].observation_error | Should -Be 'true'
        $rows[1].call_succeeded | Should -Be ''
        $failures = Test-SamplerCoverage -Line (Get-Content -LiteralPath $paths.PowerPath) -RunId 'run-1' -QpcFrequency $global:QpcFrequency -LaunchQpc ([long]$rows[0].qpc) -ExitQpc ([long]$rows[2].qpc)
        $failures | Should -Be @('power_unmet_in_window')
    }

    It 'takes one last tick after the stop signal, then ends' {
        $time.StopAfter = 2
        Invoke-PowerSampler @loop @paths
        @(Import-Csv -LiteralPath $paths.PowerPath).Count | Should -Be 3
        $time.Pauses.Count | Should -Be 2
    }

    It 'schedules each tick one interval after the first, on qpc' {
        Invoke-PowerSampler @loop @paths -MaxTicks 4
        $rows = @(Import-Csv -LiteralPath $paths.PowerPath)
        foreach ($wait in $time.Pauses) { $wait | Should -BeGreaterThan 990; $wait | Should -BeLessOrEqual 1000 }
        for ($i = 1; $i -lt $rows.Count; $i++) {
            # The wait is floored to whole milliseconds, so a tick may start up to 1 ms early.
            (([long]$rows[$i].qpc - [long]$rows[0].qpc) / $global:QpcFrequency) | Should -BeGreaterOrEqual ($i - 0.001)
            (([long]$rows[$i].qpc - [long]$rows[0].qpc) / $global:QpcFrequency) | Should -BeLessThan ($i + 0.01)
        }
    }

    It 'shows a blocked optional read as a coverage gap' {
        $loop.ReadOptional = { param([long] $Qpc) $time.Qpc += 50000000; $time.Utc = $time.Utc.AddSeconds(5); $null }.GetNewClosure()
        Invoke-PowerSampler @loop @paths -MaxTicks 3
        $rows = @(Import-Csv -LiteralPath $paths.PowerPath)
        $failures = Test-SamplerCoverage -Line (Get-Content -LiteralPath $paths.PowerPath) -RunId 'run-1' -QpcFrequency $global:QpcFrequency -LaunchQpc ([long]$rows[0].qpc) -ExitQpc ([long]$rows[2].qpc)
        $failures | Should -Be @('gap_exceeded')
    }

    It 'refuses to overwrite an existing <File>' -ForEach @(@{ File = 'PowerPath' }, @{ File = 'OptionalPath' }, @{ File = 'ReadyPath' }) {
        'earlier' | Set-Content -LiteralPath $paths[$File]
        { Invoke-PowerSampler @loop @paths -MaxTicks 1 } | Should -Throw '*already exists*'
        Get-Content -LiteralPath $paths[$File] | Should -Be 'earlier'
    }
}

Describe 'Host sample state and optional values' {
    BeforeEach {
        # Counters return the values in $hostState.Counter; a missing name cannot be created. CPU
        # readings come from $hostState.Cpu (process id -> start ticks and CPU seconds, or null).
        $hostState = @{
            Counter = @{ '% Processor Time' = 60.0; 'Available MBytes' = 512.0; '% Processor Utility' = 70.0; '% Processor Performance' = 90.0; 'Processor Frequency' = 2400.0; 'Processor Queue Length' = 1.0 }
            Throws  = @{}
            Cpu     = @{ 7 = @{ start_ticks = 70; cpu_seconds = 10.0 }; 9 = @{ start_ticks = 90; cpu_seconds = 1.0 } }
            Candidates = @{ docker_vm = , @(@{ pid = 7; start_ticks = 70 }) }
            DiscoveryThrows = $false
            CpuThrows = @{}
        }
        $newState = {
            param([System.Collections.IDictionary] $Role = [ordered]@{ docker_vm = @{} })
            # A local copy of the reference: GetNewClosure captures this scope's variables only.
            $hostState = $hostState
            Initialize-HostSampleState -Role $Role -Qpc 0 -QpcFrequency $global:QpcFrequency -LogicalCpus 4 -SamplerProcessId 9 `
                -NewCounter {
                    param([string] $Category, [string] $Counter, [string] $Instance)
                    if (-not $hostState.Counter.ContainsKey($Counter)) { throw 'no such counter' }
                    $fake = [pscustomobject]@{ Name = $Counter; Host = $hostState }
                    $fake | Add-Member -MemberType ScriptMethod -Name NextValue -Value { if ($this.Host.Throws.ContainsKey($this.Name)) { throw 'read failed' }; $this.Host.Counter[$this.Name] }
                    $fake
                }.GetNewClosure() `
                -FindCandidate {
                    param([string] $Name, $Spec)
                    if ($hostState.DiscoveryThrows) { throw 'role discovery failed' }
                    $found = $hostState.Candidates[$Name]
                    if ($null -eq $found) { , @() } else { , @($found) }
                }.GetNewClosure() `
                -ReadProcessCpu {
                    param([int] $ProcessId)
                    if ($hostState.CpuThrows.ContainsKey($ProcessId)) { throw [System.ComponentModel.Win32Exception]::new(5) }
                    $reading = $hostState.Cpu[$ProcessId]
                    if ($null -eq $reading) { $null } else { [ordered]@{ start_ticks = $reading.start_ticks; cpu_seconds = $reading.cpu_seconds } }
                }.GetNewClosure()
        }.GetNewClosure()
        $second = $global:QpcFrequency
    }

    It 'reports CPU as the share of all logical CPUs over the qpc interval' {
        $state = & $newState
        $hostState.Cpu[7].cpu_seconds = 12.0
        $hostState.Cpu[9].cpu_seconds = 1.2
        $row = Read-HostSample -State $state -Qpc $second
        $row.cpu_docker_vm_pct | Should -Be '50.000'
        $row.cpu_sampler_pct | Should -Be '5.000'
        $row.cpu_background_pct | Should -Be '5.000'
        $row.cpu_background_floored | Should -Be 'false'
        $row.processor_time_pct | Should -Be '60.000'
    }

    It 'floors background CPU at 0 and flags it' {
        $state = & $newState
        $hostState.Cpu[7].cpu_seconds = 13.0
        $row = Read-HostSample -State $state -Qpc $second
        $row.cpu_background_pct | Should -Be '0.000'
        $row.cpu_background_floored | Should -Be 'true'
    }

    It 'records a counter that cannot be created as counter_unavailable on every tick, never as 0' {
        $hostState.Counter.Remove('Available MBytes')
        $state = & $newState
        foreach ($tick in 1, 2) {
            $row = Read-HostSample -State $state -Qpc ($tick * $second)
            $row.available_mbytes | Should -Be 'NA'
            $row.available_mbytes_na | Should -Be 'counter_unavailable'
        }
    }

    It 'records a counter read that fails, or returns <Value>, as counter_error' -ForEach @(@{ Value = 'NaN' }, @{ Value = 'Infinity' }, @{ Value = 'throws' }) {
        $state = & $newState
        if ($Value -eq 'throws') { $hostState.Throws['Available MBytes'] = $true } else { $hostState.Counter['Available MBytes'] = [double]::Parse($Value, [cultureinfo]::InvariantCulture) }
        $row = Read-HostSample -State $state -Qpc $second
        $row.available_mbytes | Should -Be 'NA'
        $row.available_mbytes_na | Should -Be 'counter_error'
    }

    It 'makes background CPU NA, with the reason, when total CPU is unavailable' {
        $hostState.Counter.Remove('% Processor Time')
        $row = Read-HostSample -State (& $newState) -Qpc $second
        $row.cpu_background_pct | Should -Be 'NA'
        $row.cpu_background_pct_na | Should -Be 'counter_unavailable'
        $row.cpu_background_floored | Should -Be 'NA'
    }

    It 'records a role with <Name> at the handshake as <Code> for the whole run' -ForEach @(
        @{ Name = 'no process'; Code = 'process_absent'; Candidates = @() }
        @{ Name = 'two processes'; Code = 'process_ambiguous'; Candidates = @(@{ pid = 7; start_ticks = 70 }, @{ pid = 8; start_ticks = 80 }) }
        @{ Name = 'an unreadable start time'; Code = 'process_ambiguous'; Candidates = @(@{ pid = 7; start_ticks = $null }) }
    ) {
        $hostState.Candidates.docker_vm = $Candidates
        $state = & $newState
        foreach ($tick in 1, 2) {
            $row = Read-HostSample -State $state -Qpc ($tick * $second)
            $row.cpu_docker_vm_pct | Should -Be 'NA'
            $row.cpu_docker_vm_pct_na | Should -Be $Code
            $row.cpu_background_pct_na | Should -Be $Code
        }
        (Get-HostSampleRole -State $state).docker_vm.na | Should -Be $Code
    }

    It 'records a role whose <Name> as process_changed from then on' -ForEach @(
        @{ Name = 'start time changed'; Change = { param($h) $h.Cpu[7] = @{ start_ticks = 71; cpu_seconds = 0.5 } } }
        @{ Name = 'process is gone'; Change = { param($h) $h.Cpu.Remove(7) } }
    ) {
        $state = & $newState
        & $Change $hostState
        (Read-HostSample -State $state -Qpc $second).cpu_docker_vm_pct_na | Should -Be 'process_changed'
        $hostState.Cpu[7] = @{ start_ticks = 70; cpu_seconds = 11.0 }
        $row = Read-HostSample -State $state -Qpc (2 * $second)
        $row.cpu_docker_vm_pct | Should -Be 'NA'
        $row.cpu_docker_vm_pct_na | Should -Be 'process_changed'
    }

    It 'publishes each role as its id and start time only' {
        $state = & $newState
        $roles = Get-HostSampleRole -State $state
        @($roles.Keys) | Should -Be @('docker_vm', 'sampler')
        $roles.docker_vm.pid | Should -Be 7
        $roles.docker_vm.start_utc | Should -Be ([datetime]::new(70, [DateTimeKind]::Utc).ToString('o'))
        @($roles.docker_vm.Keys) | Should -Be @('pid', 'start_utc', 'na')
    }

    It 'lists every value column with its reason column, in a fixed order' {
        $columns = Get-HostSampleColumn -State (& $newState ([ordered]@{ cms = @{}; localdb = @{} }))
        $columns | Should -Be @(
            'processor_time_pct', 'processor_time_pct_na', 'processor_utility_pct', 'processor_utility_pct_na',
            'processor_performance_pct', 'processor_performance_pct_na', 'processor_frequency_mhz', 'processor_frequency_mhz_na',
            'processor_queue_length', 'processor_queue_length_na', 'available_mbytes', 'available_mbytes_na',
            'cpu_cms_pct', 'cpu_cms_pct_na', 'cpu_localdb_pct', 'cpu_localdb_pct_na', 'cpu_sampler_pct', 'cpu_sampler_pct_na',
            'cpu_background_pct', 'cpu_background_pct_na', 'cpu_background_floored')
    }

    It 'records a role as counter_error, and keeps going, when its discovery throws' {
        $hostState.DiscoveryThrows = $true
        $state = & $newState
        (Get-HostSampleRole -State $state).docker_vm.na | Should -Be 'counter_error'
        (Get-HostSampleRole -State $state).docker_vm.pid | Should -BeNullOrEmpty
        $row = Read-HostSample -State $state -Qpc $second
        $row.cpu_docker_vm_pct | Should -Be 'NA'
        $row.cpu_docker_vm_pct_na | Should -Be 'counter_error'
        $row.cpu_background_pct_na | Should -Be 'counter_error'
        $row.processor_time_pct | Should -Be '60.000'
    }

    It 'records the sampler as counter_error, and keeps going, when its own CPU read throws' {
        $hostState.CpuThrows[9] = $true
        $state = & $newState
        (Get-HostSampleRole -State $state).sampler.na | Should -Be 'counter_error'
        $row = Read-HostSample -State $state -Qpc $second
        $row.cpu_sampler_pct | Should -Be 'NA'
        $row.cpu_sampler_pct_na | Should -Be 'counter_error'
        $row.cpu_docker_vm_pct | Should -Not -Be 'NA'
    }

    It 'still writes power rows and a ready record when role discovery and the sampler CPU read throw' {
        $hostState.DiscoveryThrows = $true
        $hostState.CpuThrows[9] = $true
        $state = & $newState
        $folder = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $folder | Out-Null
        $time = @{ Qpc = [long]0; Utc = [datetime]::new(2026, 10, 24, 12, 0, 0, [DateTimeKind]::Utc) }
        $online = [ordered]@{ utc = 'x'; call_succeeded = $true; win32_error = $null; ac_line_status = 1; battery_flag = 8; battery_life_percent = 80; system_status_flag = 0; scheme_query_succeeded = $true; scheme_query_status = 0; scheme_query_failure = $null; scheme_guid = $global:BalancedScheme }
        Invoke-PowerSampler -RunId 'run-1' -Identity ([ordered]@{ sampler_pid = 9 }) -Role (Get-HostSampleRole -State $state) -OptionalColumn (Get-HostSampleColumn -State $state) `
            -ReadOptional { param([long] $Qpc) Read-HostSample -State $state -Qpc $Qpc }.GetNewClosure() `
            -PowerPath (Join-Path $folder 'power.csv') -OptionalPath (Join-Path $folder 'optional.csv') -ReadyPath (Join-Path $folder 'ready.json') -StopPath (Join-Path $folder 'stop') `
            -QpcFrequency $global:QpcFrequency -MaxTicks 2 -ReadPower { $online }.GetNewClosure() `
            -ReadClock { $time.Qpc += 1000; $time.Utc = $time.Utc.AddTicks(1000); [ordered]@{ qpc = $time.Qpc; utc = $time.Utc } }.GetNewClosure() `
            -Pause { param([int] $Milliseconds) $time.Qpc += [long]$Milliseconds * 10000; $time.Utc = $time.Utc.AddMilliseconds($Milliseconds) }.GetNewClosure()
        $ready = Get-Content -LiteralPath (Join-Path $folder 'ready.json') -Raw | ConvertFrom-Json
        $ready.roles.docker_vm.na | Should -Be 'counter_error'
        $ready.roles.sampler.na | Should -Be 'counter_error'
        $failures = Test-SamplerCoverage -Line (Get-Content -LiteralPath (Join-Path $folder 'power.csv')) -RunId 'run-1' -QpcFrequency $global:QpcFrequency -LaunchQpc 1000 -ExitQpc 5000000
        $failures | Should -BeNullOrEmpty
        @(Import-Csv -LiteralPath (Join-Path $folder 'optional.csv')).cpu_background_pct_na | Should -Be @('counter_error', 'counter_error')
    }

    It 'refuses an unknown role' {
        { & $newState ([ordered]@{ browser = @{} }) } | Should -Throw '*Unknown sampler role*'
    }
}

Describe 'Role selection' {
    It 'resolves <Name>' -ForEach @(
        @{ Name = 'no candidate as absent'; Candidates = @(); Na = 'process_absent' }
        @{ Name = 'two candidates as ambiguous'; Candidates = @(@{ pid = 1; start_ticks = 1 }, @{ pid = 2; start_ticks = 2 }); Na = 'process_ambiguous' }
        @{ Name = 'a candidate without a start time as ambiguous'; Candidates = @(@{ pid = 1; start_ticks = $null }); Na = 'process_ambiguous' }
        @{ Name = 'one candidate as its identity'; Candidates = @(@{ pid = 1; start_ticks = 5 }); Na = $null }
    ) {
        $resolved = Resolve-RoleIdentity -Candidate $Candidates
        $resolved.na | Should -Be $Na
        if ($null -eq $Na) { $resolved.identity.pid | Should -Be 1; $resolved.identity.start_ticks | Should -Be 5 }
    }

    It 'selects the sqlservr of the named LocalDB instance only, and drops its command line' {
        $processes = @(
            @{ pid = 1; command_line = "sqlservr.exe -c -d`"C:\Users\u\AppData\Local\Microsoft\Microsoft SQL Server Local DB\Instances\DMS1490\master.mdf`" $($global:LaneSentinel)" }
            @{ pid = 2; command_line = 'sqlservr.exe -d"C:\Users\u\AppData\Local\Microsoft\Microsoft SQL Server Local DB\Instances\DMS14900\master.mdf"' }
            @{ pid = 3; command_line = 'sqlservr.exe -d"C:\Users\u\AppData\Local\Microsoft\Microsoft SQL Server Local DB\Instances\MSSQLLocalDB\master.mdf"' }
        )
        $selected = Select-LocalDbCandidate -Process $processes -Instance 'DMS1490'
        $selected.Count | Should -Be 1
        $selected[0].pid | Should -Be 1
        ($selected | ConvertTo-Json -Depth 3) | Should -Not -Match $global:LaneSentinel
    }

    It 'quotes <Value> for the sampler command line' -ForEach @(
        @{ Value = 'plain'; Quoted = 'plain' }
        @{ Value = 'C:\with space\host.csv'; Quoted = '"C:\with space\host.csv"' }
        @{ Value = 'C:\with space\'; Quoted = '"C:\with space\\"' }
        @{ Value = 'say "hi"'; Quoted = '"say \"hi\""' }
        @{ Value = ''; Quoted = '""' }
    ) {
        & (Get-Module CollectionLane) { param($Text) ConvertTo-CommandLineArgument $Text } $Value | Should -BeExactly $Quoted
    }
}

Describe 'Collector build identity' {
    BeforeEach {
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $collector = Join-Path $root 'collector'
        $manifest = Join-Path $root 'collector-build.json'
        $output = New-TestCollectorBuild $collector $manifest
        $check = @{ CollectorRoot = $collector; ManifestPath = $manifest; ExpectedCommit = $global:TestCommit; HeadCommit = $global:TestCommit; HeadTree = $global:TestCommit }
        $edit = {
            param([scriptblock] $Change)
            $record = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json -AsHashtable
            & $Change $record
            $record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifest -Encoding utf8NoBOM
        }
    }

    It 'accepts the recorded build and returns the record''s hash and file count' {
        $result = Assert-CollectorBuild @check
        $result.files | Should -Be 2
        $result.manifest_sha256 | Should -Be (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    It 'records every output file with its size and SHA-256, and how it was built' {
        $record = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        @($record.files.path) | Should -Be @('EdFi.DmsConfigurationService.Tests.E2E.dll', 'runtimes/native.dll')
        $record.files[0].size | Should -Be 18
        $record.files[0].sha256 | Should -Be (Get-FileHash -LiteralPath (Join-Path $output 'EdFi.DmsConfigurationService.Tests.E2E.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
        $record.build.command | Should -BeLike 'dotnet build*--no-incremental*'
        $record.output | Should -Be 'src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/bin/Release/net10.0'
    }

    It 'refuses an output with <Name>' -ForEach @(
        @{ Name = 'a changed file of the same size'; Change = { param($o) Set-Content -LiteralPath (Join-Path $o 'runtimes\native.dll') -Value 'NATIVE' -NoNewline }; Code = '*collector_files_differ: 0 missing, 0 extra, 1 changed*' }
        @{ Name = 'a missing file'; Change = { param($o) Remove-Item -LiteralPath (Join-Path $o 'runtimes\native.dll') }; Code = '*1 missing, 0 extra*' }
        @{ Name = 'an extra file'; Change = { param($o) Set-Content -LiteralPath (Join-Path $o 'stale.pdb') -Value 'stale' }; Code = '*0 missing, 1 extra*' }
    ) {
        & $Change $output
        { Assert-CollectorBuild @check } | Should -Throw $Code
    }

    It 'refuses an output folder that holds a junction' {
        New-Item -ItemType Junction -Path (Join-Path $output 'linked') -Target $TestDrive | Out-Null
        try { { Assert-CollectorBuild @check } | Should -Throw '*junction*' }
        finally { [System.IO.Directory]::Delete((Join-Path $output 'linked')) }
    }

    It 'refuses a checkout at <Name>' -ForEach @(
        @{ Name = 'another commit than the approved one'; Key = 'ExpectedCommit' }
        @{ Name = 'another HEAD than the record'; Key = 'HeadCommit' }
        @{ Name = 'another tree than the record'; Key = 'HeadTree' }
    ) {
        $check[$Key] = '2222222222222222222222222222222222222222'
        { Assert-CollectorBuild @check } | Should -Throw '*collector_commit_mismatch*'
    }

    It 'refuses an expected commit that is not a full id' {
        $check.ExpectedCommit = '1111111'
        { Assert-CollectorBuild @check } | Should -Throw '*collector_expected_commit_invalid*'
    }

    It 'refuses, before opening any listed file, a record with <Name>' -ForEach @(
        @{ Name = 'another schema'; Change = { param($r) $r.schema = 'collector-build/0' } }
        @{ Name = 'another output folder'; Change = { param($r) $r.output = 'src/config/bin' } }
        @{ Name = 'a short commit'; Change = { param($r) $r.commit = '1111' } }
        @{ Name = 'no build command'; Change = { param($r) $r.build.command = '' } }
        @{ Name = 'no files'; Change = { param($r) $r.files = @() } }
        @{ Name = 'a path that climbs out'; Change = { param($r) $r.files[0].path = '../../../secret.txt' } }
        @{ Name = 'a rooted path'; Change = { param($r) $r.files[0].path = 'C:/Windows/win.ini' } }
        @{ Name = 'a backslash path'; Change = { param($r) $r.files[1].path = 'runtimes\native.dll' } }
        @{ Name = 'an empty segment'; Change = { param($r) $r.files[1].path = 'runtimes//native.dll' } }
        @{ Name = 'a path listed twice in another case'; Change = { param($r) $r.files[1].path = 'EDFI.DmsConfigurationService.Tests.E2E.dll' } }
        @{ Name = 'a malformed hash'; Change = { param($r) $r.files[0].sha256 = 'abc' } }
        @{ Name = 'a negative size'; Change = { param($r) $r.files[0].size = -1 } }
    ) {
        & $edit $Change
        { Assert-CollectorBuild @check } | Should -Throw '*collector_manifest_invalid*'
    }

    It 'refuses a record that is not JSON' {
        '{ not json' | Set-Content -LiteralPath $manifest
        { Assert-CollectorBuild @check } | Should -Throw '*collector_manifest_invalid*'
    }

    It 'hashes a file as git hash-object --no-filters does' {
        $file = Join-Path $root 'crlf.txt'
        [System.IO.File]::WriteAllBytes($file, [byte[]](0x61, 0x0D, 0x0A, 0x62))
        Get-GitBlobId $file | Should -Be ((git hash-object --no-filters $file) | Out-String).Trim()
    }
}

Describe 'Target source identity' {
    BeforeEach {
        # A small repository with a tracked file under bin/, and a target extracted from its commit.
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $repository = Join-Path $root 'repository'
        $target = Join-Path $root 'target'
        New-Item -ItemType Directory -Force -Path (Join-Path $repository 'src'), (Join-Path $repository 'bin') | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $repository 'src\a.txt'), "alpha`n")
        [System.IO.File]::WriteAllText((Join-Path $repository 'bin\tracked.txt'), "tracked`n")
        [System.IO.File]::WriteAllText((Join-Path $repository 'README.md'), "readme`n")
        git -C $repository init -q 2>$null
        git -C $repository -c core.autocrlf=false add -A 2>$null
        git -C $repository -c core.autocrlf=false -c user.name=test -c user.email=test@example.invalid commit -q -m initial 2>$null
        $commit = ((git -C $repository rev-parse HEAD) | Out-String).Trim()
        $tree = ((git -C $repository rev-parse 'HEAD^{tree}') | Out-String).Trim()
        Copy-Item -LiteralPath $repository -Destination $target -Recurse
        Remove-Item -LiteralPath (Join-Path $target '.git') -Recurse -Force
        @{ commit = $commit; tree = $tree } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target 'target.json')
        $check = @{ RepositoryRoot = $repository; TargetRoot = $target; Commit = $commit; Tree = $tree }
    }

    It 'accepts the pinned files with target.json and build output under bin and obj directories' {
        New-Item -ItemType Directory -Force -Path (Join-Path $target 'obj'), (Join-Path $target 'src\bin\Release') | Out-Null
        Set-Content -LiteralPath (Join-Path $target 'obj\project.assets.json') -Value '{}'
        Set-Content -LiteralPath (Join-Path $target 'src\bin\Release\app.dll') -Value 'dll'
        $summary = Assert-TargetSource @check
        $summary.tracked_files | Should -Be 3
        $summary.verified_files | Should -Be 3
        $summary.allowed_extra | Should -Be 3
    }

    It 'refuses <Name>' -ForEach @(
        @{ Name = 'a tracked file whose line endings changed'; Change = { param($t) [System.IO.File]::WriteAllText((Join-Path $t 'src\a.txt'), "alpha`r`n") }; Code = '*1 changed*' }
        @{ Name = 'a changed tracked file under a bin directory'; Change = { param($t) [System.IO.File]::WriteAllText((Join-Path $t 'bin\tracked.txt'), "changed`n") }; Code = '*1 changed*' }
        @{ Name = 'a missing tracked file'; Change = { param($t) Remove-Item -LiteralPath (Join-Path $t 'README.md') }; Code = '*1 missing*' }
        @{ Name = 'an extra source file'; Change = { param($t) Set-Content -LiteralPath (Join-Path $t 'src\extra.cs') -Value 'x' }; Code = '*1 extra*' }
        @{ Name = 'an extra file whose folder only contains the word bin'; Change = { param($t) New-Item -ItemType Directory -Path (Join-Path $t 'binary') | Out-Null; Set-Content -LiteralPath (Join-Path $t 'binary\x.txt') -Value 'x' }; Code = '*1 extra*' }
        @{ Name = 'an extra file named like obj'; Change = { param($t) Set-Content -LiteralPath (Join-Path $t 'src\obj') -Value 'x' }; Code = '*1 extra*' }
        @{ Name = 'an extra target.json below the root'; Change = { param($t) Set-Content -LiteralPath (Join-Path $t 'src\target.json') -Value '{}' }; Code = '*1 extra*' }
    ) {
        & $Change $target
        { Assert-TargetSource @check } | Should -Throw "*target_source_differs*"
        { Assert-TargetSource @check } | Should -Throw $Code
    }

    It 'refuses a target.json naming another commit' {
        @{ commit = '2222222222222222222222222222222222222222'; tree = $check.Tree } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target 'target.json')
        { Assert-TargetSource @check } | Should -Throw '*target_metadata_mismatch*'
    }

    It 'refuses an approved tree that the commit does not have, whatever target.json says' {
        $check.Tree = '3333333333333333333333333333333333333333'
        { Assert-TargetSource @check } | Should -Throw '*target_tree_mismatch*'
    }

    It 'refuses a target that holds a junction' {
        New-Item -ItemType Junction -Path (Join-Path $target 'linked') -Target $root | Out-Null
        try { { Assert-TargetSource @check } | Should -Throw '*target_reparse_point*' }
        finally { [System.IO.Directory]::Delete((Join-Path $target 'linked')) }
    }
}

Describe 'Target source generated files' {
    BeforeEach {
        # A repository with the two claims fragment folders and two projects, and a target extracted
        # from its commit that holds what setup and a build generate: two staged claims, an analyzer
        # log beside a tracked project and build output.
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $repository = Join-Path $root 'repository'
        $target = Join-Path $root 'target'
        $additional = 'src/config/backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaims'
        $fragments = 'src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/TestData/Claims/Fragments'
        $workspace = 'eng/docker-compose/.e2e-claims'
        $write = {
            param([string] $Root, [string] $Relative, [string] $Text)
            $file = Join-Path $Root $Relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file) | Out-Null
            [System.IO.File]::WriteAllText($file, $Text)
        }
        $files = [ordered]@{
            'src/a.txt'                                = "alpha`n"
            "$additional/001-alpha-claims.json"        = '{"alpha":1}'
            "$additional/003-shared-claims.json"       = '{"shared":"additional"}'
            "$additional/README.md"                    = "readme`n"
            "$additional/nested/009-nested-claims.json" = '{"nested":1}'
            "$fragments/002-beta-claims.json"          = '{"beta":2}'
            "$fragments/003-Shared-claims.json"        = '{"shared":"fragments"}'
            'proj/App/App.csproj'                      = '<Project />'
            'proj/Lib/Lib.csproj'                      = '<Project />'
            'proj/Lib/results.sarif'                   = 'tracked log'
            'proj/Docs/notes.txt'                      = "notes`n"
        }
        foreach ($file in $files.GetEnumerator()) { & $write $repository $file.Key $file.Value }
        git -C $repository init -q 2>$null
        git -C $repository -c core.autocrlf=false add -A 2>$null
        git -C $repository -c core.autocrlf=false -c user.name=test -c user.email=test@example.invalid commit -q -m initial 2>$null
        $commit = ((git -C $repository rev-parse HEAD) | Out-String).Trim()
        $tree = ((git -C $repository rev-parse 'HEAD^{tree}') | Out-String).Trim()
        Copy-Item -LiteralPath $repository -Destination $target -Recurse
        Remove-Item -LiteralPath (Join-Path $target '.git') -Recurse -Force
        @{ commit = $commit; tree = $tree } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target 'target.json')
        $check = @{ RepositoryRoot = $repository; TargetRoot = $target; Commit = $commit; Tree = $tree }

        # Copies a tracked file's bytes from the repository to a path in the target.
        $stage = {
            param([string] $Source, [string] $Destination)
            $file = Join-Path $target $Destination
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $file) | Out-Null
            Copy-Item -LiteralPath (Join-Path $repository $Source) -Destination $file
        }
        & $stage "$additional/001-alpha-claims.json" "$workspace/001-alpha-claims.json"
        & $stage "$fragments/002-beta-claims.json" "$workspace/002-beta-claims.json"
        & $write $target 'proj/App/results.sarif' '{"runs":[]}'
        & $write $target 'src/bin/Release/app.dll' 'dll'
        $blob = { param([string] $Path) ((git -C $repository rev-parse "HEAD:$Path") | Out-String).Trim() }
        $fixture = @{ Target = $target; Write = $write; Stage = $stage; Additional = $additional; Fragments = $fragments; Workspace = $workspace }
    }

    It 'accepts verified staged claims and an analyzer log beside a tracked project, and records each by category' {
        $summary = Assert-TargetSource @check
        $summary.tracked_files | Should -Be 11
        $summary.verified_files | Should -Be 11
        $summary.extra | Should -BeNullOrEmpty
        $summary.allowed_extra | Should -Be 2
        $summary.staged_claims_verified | Should -Be 2
        ConvertTo-Json -Compress -InputObject $summary.staged_claims | Should -Be (ConvertTo-Json -Compress -InputObject @(
                [ordered]@{ path = "$workspace/001-alpha-claims.json"; source = "$additional/001-alpha-claims.json"; blob = (& $blob "$additional/001-alpha-claims.json") }
                [ordered]@{ path = "$workspace/002-beta-claims.json"; source = "$fragments/002-beta-claims.json"; blob = (& $blob "$fragments/002-beta-claims.json") }
            ))
        $logHash = (Get-FileHash -LiteralPath (Join-Path $target 'proj/App/results.sarif') -Algorithm SHA256).Hash.ToLowerInvariant()
        ConvertTo-Json -Compress -InputObject $summary.analyzer_logs | Should -Be (ConvertTo-Json -Compress -InputObject @([ordered]@{ path = 'proj/App/results.sarif'; sha256 = $logHash }))
        $summary.untracked_files | Should -Be 5
        ConvertTo-Json -Compress -InputObject $summary.allowed_extra_by_category | Should -Be '{"target_json_or_build_output":2,"staged_claims":2,"analyzer_logs":1}'
    }

    It 'matches a staged name to its source without case, as the staging helper does' {
        Remove-Item -LiteralPath (Join-Path $target "$workspace/001-alpha-claims.json")
        & $stage "$additional/001-alpha-claims.json" "$workspace/001-ALPHA-claims.json"
        $summary = Assert-TargetSource @check
        $summary.staged_claims[0].path | Should -BeExactly "$workspace/001-ALPHA-claims.json"
        $summary.staged_claims[0].source | Should -BeExactly "$additional/001-alpha-claims.json"
    }

    It 'still verifies a tracked analyzer log' {
        & $write $target 'proj/Lib/results.sarif' 'changed log'
        { Assert-TargetSource @check } | Should -Throw '*0 missing, 1 changed, 0 extra (first: proj/Lib/results.sarif)*'
    }

    It 'refuses <Name>, counted as extra' -ForEach @(
        @{ Name = 'a staged fragment whose bytes changed'; Expected = '.e2e-claims/001-alpha-claims.json'; Arrange = { param($f) & $f.Write $f.Target "$($f.Workspace)/001-alpha-claims.json" '{"alpha":2}' } }
        @{ Name = 'a staged name holding another fragment''s blob'; Expected = '.e2e-claims/002-beta-claims.json'; Arrange = { param($f) Remove-Item -LiteralPath (Join-Path $f.Target "$($f.Workspace)/002-beta-claims.json"); & $f.Stage "$($f.Additional)/001-alpha-claims.json" "$($f.Workspace)/002-beta-claims.json" } }
        @{ Name = 'an unknown staged claims file'; Expected = '.e2e-claims/099-unknown-claims.json'; Arrange = { param($f) & $f.Write $f.Target "$($f.Workspace)/099-unknown-claims.json" '{}' } }
        @{ Name = 'a staged copy of a tracked non-claims source file'; Expected = '.e2e-claims/README.md'; Arrange = { param($f) & $f.Stage "$($f.Additional)/README.md" "$($f.Workspace)/README.md" } }
        @{ Name = 'a staged fragment copy under another extension'; Expected = '.e2e-claims/001-alpha-claims.json.bak'; Arrange = { param($f) & $f.Stage "$($f.Additional)/001-alpha-claims.json" "$($f.Workspace)/001-alpha-claims.json.bak" } }
        @{ Name = 'a fragment copy in a subfolder of the workspace'; Expected = '.e2e-claims/sub/001-alpha-claims.json'; Arrange = { param($f) & $f.Stage "$($f.Additional)/001-alpha-claims.json" "$($f.Workspace)/sub/001-alpha-claims.json" } }
        @{ Name = 'a fragment copy below bin inside the workspace'; Expected = '.e2e-claims/bin/001-alpha-claims.json'; Arrange = { param($f) & $f.Stage "$($f.Additional)/001-alpha-claims.json" "$($f.Workspace)/bin/001-alpha-claims.json" } }
        @{ Name = 'build output below obj inside the workspace'; Expected = '.e2e-claims/obj/project.assets.json'; Arrange = { param($f) & $f.Write $f.Target "$($f.Workspace)/obj/project.assets.json" '{}' } }
        @{ Name = 'a fragment copy in a .e2e-claims folder at another path'; Expected = 'src/eng/docker-compose/.e2e-claims/001-alpha-claims.json'; Arrange = { param($f) & $f.Stage "$($f.Additional)/001-alpha-claims.json" "src/$($f.Workspace)/001-alpha-claims.json" } }
        @{ Name = 'a copy of a fragment tracked only in a subfolder of a source'; Expected = '.e2e-claims/009-nested-claims.json'; Arrange = { param($f) & $f.Stage "$($f.Additional)/nested/009-nested-claims.json" "$($f.Workspace)/009-nested-claims.json" } }
        @{ Name = 'a fragment name tracked in both sources (first source''s bytes)'; Expected = '.e2e-claims/003-shared-claims.json'; Arrange = { param($f) & $f.Stage "$($f.Additional)/003-shared-claims.json" "$($f.Workspace)/003-shared-claims.json" } }
        @{ Name = 'a fragment name tracked in both sources (second source''s bytes)'; Expected = '.e2e-claims/003-Shared-claims.json'; Arrange = { param($f) & $f.Stage "$($f.Fragments)/003-Shared-claims.json" "$($f.Workspace)/003-Shared-claims.json" } }
        @{ Name = 'an analyzer log in a folder without a project'; Expected = 'proj/Docs/results.sarif'; Arrange = { param($f) & $f.Write $f.Target 'proj/Docs/results.sarif' '{}' } }
        @{ Name = 'an analyzer log at the target root'; Expected = 'results.sarif'; Arrange = { param($f) & $f.Write $f.Target 'results.sarif' '{}' } }
        @{ Name = 'an analyzer log in a subfolder of a project'; Expected = 'proj/App/sub/results.sarif'; Arrange = { param($f) & $f.Write $f.Target 'proj/App/sub/results.sarif' '{}' } }
        @{ Name = 'an analyzer log named in another case'; Expected = 'proj/App/Results.sarif'; Arrange = { param($f) Remove-Item -LiteralPath (Join-Path $f.Target 'proj/App/results.sarif'); & $f.Write $f.Target 'proj/App/Results.sarif' '{}' } }
    ) {
        & $Arrange $fixture
        { Assert-TargetSource @check } | Should -Throw "*target_source_differs: 0 missing, 0 changed, 1 extra (first: *$Expected)*"
    }

    It 'refuses an analyzer log beside a project file that is not tracked' {
        & $write $target 'proj/Untracked/Untracked.csproj' '<Project />'
        & $write $target 'proj/Untracked/results.sarif' '{}'
        { Assert-TargetSource @check } | Should -Throw '*0 missing, 0 changed, 2 extra (first: *proj/Untracked/results.sarif*'
    }
}

Describe 'Analysis identity' {
    BeforeAll {
        # A script's identity as the record stores it, computed here independently of the module.
        function Get-AnalysisScriptIdentityForTest([string] $Path, [string] $VersionName) {
            $version = ([regex]::Match([System.IO.File]::ReadAllText($Path), "(?m)^$VersionName = `"([^`"]+)`"$")).Groups[1].Value
            [ordered]@{ blob = Get-GitBlobId $Path; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant(); version = $version }
        }
    }

    BeforeEach {
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $repository = Join-Path $root 'repository'
        $evidence = Join-Path $repository 'reference\design\configuration-service\DMS-1490-timing-evidence'
        New-Item -ItemType Directory -Force -Path $evidence | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $evidence 'analyze_timing.py'), "METHOD_VERSION = `"1`"`nprint('analysis')`n")
        [System.IO.File]::WriteAllText((Join-Path $evidence 'diagnostics.py'), "DIAGNOSTICS_VERSION = `"1`"`nprint('diagnostics')`n")
        git -C $repository init -q 2>$null
        git -C $repository -c core.autocrlf=false add -A 2>$null
        git -C $repository -c core.autocrlf=false -c user.name=test -c user.email=test@example.invalid commit -q -m initial 2>$null
        $commit = ((git -C $repository rev-parse HEAD) | Out-String).Trim()
        $sums = Join-Path $root 'SHA256SUMS.txt'
        $hash = (Get-FileHash -LiteralPath (Join-Path $evidence 'analyze_timing.py') -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  50  analyze_timing.py" | Set-Content -LiteralPath $sums
        $new = @{ RepositoryRoot = $repository; Commit = $commit; EvidenceManifestPath = $sums; PythonVersion = '3.12.10'; NumpyVersion = '2.5.2' }
        $record = Join-Path $root 'analysis-manifest.json'
        $check = @{ RepositoryRoot = $repository; ManifestPath = $record; ExpectedCommit = $commit }
        $commitAll = { param([string] $Message) git -C $repository -c core.autocrlf=false add -A 2>$null; git -C $repository -c core.autocrlf=false -c user.name=test -c user.email=test@example.invalid commit -q -m $Message 2>$null; ((git -C $repository rev-parse HEAD) | Out-String).Trim() }
    }

    It 'pins both scripts at the approved commit with the fixed parameters, and re-checks them' {
        $manifest = Get-AnalysisManifest @new
        $manifest.scripts.analyze_timing.blob | Should -Be ((git -C $repository rev-parse "$commit`:reference/design/configuration-service/DMS-1490-timing-evidence/analyze_timing.py") | Out-String).Trim()
        $manifest.scripts.diagnostics.version | Should -Be '1'
        $manifest.parameters.resamples | Should -Be 5000
        $manifest.parameters.delta_us | Should -Be 250
        @($manifest.parameters.candidates) | Should -Be @(500, 1000, 2000, 4000)
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $record
        $verified = Assert-AnalysisIdentity @check
        $verified.manifest_sha256 | Should -Be (Get-FileHash -LiteralPath $record -Algorithm SHA256).Hash.ToLowerInvariant()
        $verified.paths.analyze_timing | Should -Be (Join-Path $evidence 'analyze_timing.py')
        $verified.paths.diagnostics | Should -Be (Join-Path $evidence 'diagnostics.py')
    }

    It 'refuses a record checked against another approved commit' {
        Get-AnalysisManifest @new | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $record
        $check.ExpectedCommit = 'f' * 40
        { Assert-AnalysisIdentity @check } | Should -Throw '*analysis_commit_mismatch*'
    }

    It 'refuses a record that names another commit than the approved one' {
        $manifest = Get-AnalysisManifest @new
        $manifest.commit = 'f' * 40
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $record
        { Assert-AnalysisIdentity @check } | Should -Throw '*analysis_commit_mismatch*'
    }

    It 'refuses a self-consistent record of a script that is not the approved commit''s' {
        $manifest = Get-AnalysisManifest @new
        Add-Content -LiteralPath (Join-Path $evidence 'diagnostics.py') -Value '# edited'
        $edited = Get-AnalysisScriptIdentityForTest (Join-Path $evidence 'diagnostics.py') 'DIAGNOSTICS_VERSION'
        $manifest.scripts.diagnostics.blob = $edited.blob
        $manifest.scripts.diagnostics.sha256 = $edited.sha256
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $record
        { Assert-AnalysisIdentity @check } | Should -Throw '*analysis_script_unpinned*'
    }

    It 'refuses a self-consistent record of a commit whose script declares another version' {
        $manifest = Get-AnalysisManifest @new
        [System.IO.File]::WriteAllText((Join-Path $evidence 'diagnostics.py'), "DIAGNOSTICS_VERSION = `"2`"`n")
        $versionTwo = & $commitAll 'v2'
        $edited = Get-AnalysisScriptIdentityForTest (Join-Path $evidence 'diagnostics.py') 'DIAGNOSTICS_VERSION'
        $manifest.commit = $versionTwo
        $manifest.scripts.diagnostics.blob = $edited.blob
        $manifest.scripts.diagnostics.sha256 = $edited.sha256
        $manifest.scripts.diagnostics.version = $edited.version
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $record
        $check.ExpectedCommit = $versionTwo
        { Assert-AnalysisIdentity @check } | Should -Throw '*analysis_version_mismatch*'
    }

    It 'refuses an expected commit that is not a full id' {
        Get-AnalysisManifest @new | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $record
        $check.ExpectedCommit = $commit.Substring(0, 12)
        { Assert-AnalysisIdentity @check } | Should -Throw '*analysis_expected_commit_invalid*'
    }

    It 'refuses to pin a script that differs from the approved commit' {
        Add-Content -LiteralPath (Join-Path $evidence 'diagnostics.py') -Value '# edited'
        { Get-AnalysisManifest @new } | Should -Throw '*analysis_script_differs*'
    }

    It 'refuses an analysis script that no longer matches the evidence manifest' {
        'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff  50  analyze_timing.py' | Set-Content -LiteralPath $new.EvidenceManifestPath
        { Get-AnalysisManifest @new } | Should -Throw '*does not match the evidence manifest*'
    }

    It 'refuses a script that declares another version' {
        [System.IO.File]::WriteAllText((Join-Path $evidence 'diagnostics.py'), "DIAGNOSTICS_VERSION = `"2`"`n")
        $new.Commit = & $commitAll 'v2'
        { Get-AnalysisManifest @new } | Should -Throw '*analysis_version_mismatch*'
    }

    It 'refuses, immediately before use, a script changed after it was pinned' {
        Get-AnalysisManifest @new | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $record
        Add-Content -LiteralPath (Join-Path $evidence 'analyze_timing.py') -Value '# edited'
        { Assert-AnalysisIdentity @check } | Should -Throw '*analysis_script_differs*'
    }

    It 'refuses a record whose <Key> is not the pinned value' -ForEach @(@{ Key = 'resamples'; Value = 1000 }, @{ Key = 'delta_us'; Value = 25 }, @{ Key = 'seed'; Value = 2 }) {
        $manifest = Get-AnalysisManifest @new
        $manifest.parameters[$Key] = $Value
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $record
        { Assert-AnalysisIdentity @check } | Should -Throw "*record's $Key is not the pinned value*"
    }
}

Describe 'Launch record' {
    BeforeEach {
        $folder = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $folder | Out-Null
        $path = Get-LaunchRecordPath -OutputDir $folder -Backend postgresql -RunLabel 'e1-pilot-a5' -Seed 401
    }

    It 'names one record per backend, run label and seed' {
        Split-Path -Leaf $path | Should -Be 'launch-endpoint-postgresql-e1-pilot-a5-seed401.json'
        Split-Path -Leaf (Get-LaunchRecordPath -OutputDir $folder -Backend mssql -RunLabel 'e1-pilot-a5' -Seed 402) | Should -Be 'launch-endpoint-mssql-e1-pilot-a5-seed402.json'
    }

    It 'writes the record' {
        Add-LaunchRecord -Path $path -Record ([ordered]@{ schema = 'timing-launch/1'; seed = 401 })
        (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).seed | Should -Be 401
    }

    It 'refuses a record that already exists, and leaves it as it was' {
        Add-LaunchRecord -Path $path -Record ([ordered]@{ seed = 401; run = 'first' })
        { Add-LaunchRecord -Path $path -Record ([ordered]@{ seed = 401; run = 'second' }) } | Should -Throw
        (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).run | Should -Be 'first'
    }

    It 'throws when the record cannot be created' {
        $unreachable = Join-Path $folder 'missing\launch.json'
        { Add-LaunchRecord -Path $unreachable -Record ([ordered]@{ seed = 401 }) } | Should -Throw
        Test-Path -LiteralPath $unreachable | Should -BeFalse
    }
}

Describe 'Invoke-SamplerPreflight' {
    BeforeEach {
        $folder = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $folder | Out-Null
        $powerPath = Join-Path $folder 'power.csv'
        $stopPath = Join-Path $folder 'stop'
        # Rows every second over 12 s from qpc 1e9; the window runs from 0.5 s to 10.5 s.
        $writeRows = { param([double] $Step = 1, [int] $Count = 12) @(($global:SamplerColumns -join ',')) + (ConvertTo-TestSampleLine (New-TestSampleSpec $Count -Step $Step -RunId 'run-1')) | Set-Content -LiteralPath $powerPath -Encoding utf8NoBOM }
        & $writeRows
        $online = [ordered]@{ utc = 'x'; call_succeeded = $true; win32_error = $null; ac_line_status = 1; battery_flag = 8; battery_life_percent = 80; system_status_flag = 0; scheme_query_succeeded = $true; scheme_query_status = 0; scheme_query_failure = $null; scheme_guid = $global:BalancedScheme }
        $state = @{ Id = 4242 }
        $process = New-TestProcess $state
        # Whole milliseconds, so that fifty 200 ms pauses are exactly 10 s.
        $time = @{ Milliseconds = 0; Qpc = [System.Collections.Generic.Queue[long]]::new([long[]]@(1005000000, 1105000000)); Pauses = 0; Closes = 0 }
        $ready = @{ failure = $null; unmet = @(); ready = @{ roles = [ordered]@{ localdb = [ordered]@{ pid = $null; start_utc = $null; na = 'process_absent' } } } }
        $preflight = @{
            RunId     = 'run-1'
            PowerPath = $powerPath
            QpcFrequency = $global:QpcFrequency
            WindowSeconds = 10
            ReadPower = { $online }.GetNewClosure()
            Open      = { $process }.GetNewClosure()
            WaitReady = { param($Process) $ready }.GetNewClosure()
            Close     = { param($Process) $time.Closes++; Close-PowerSampler -Process $Process -StopPath $stopPath }.GetNewClosure()
            ReadQpc   = { $time.Qpc.Dequeue() }.GetNewClosure()
            Clock     = { $time.Milliseconds / 1000.0 }.GetNewClosure()
            Pause     = { param([int] $Milliseconds) $time.Pauses++; if ($time.Pauses -gt 1000) { throw 'The preflight window did not end.' }; $time.Milliseconds += $Milliseconds }.GetNewClosure()
        }
    }

    It 'passes a ready sampler that covers its window and stops cleanly, and reports the roles it found' {
        $result = Invoke-SamplerPreflight @preflight
        $result.outcome | Should -Be 'passed'
        $result.power.check | Should -Be 'preflight'
        $result.power.condition_met | Should -BeTrue
        $result.roles.localdb.na | Should -Be 'process_absent'
        $result.observed_seconds | Should -Be 10
        $result.stop.terminated | Should -BeTrue
        @($result.coverage_failures) | Should -BeNullOrEmpty
        $time.Closes | Should -Be 1
        $time.Pauses | Should -Be 50
        $result.statement | Should -BeLike '*qualifies no later collection*'
    }

    It 'fails, and still stops the sampler, when it is not ready' {
        $ready.failure = 'sampler_ready_timeout'
        $result = Invoke-SamplerPreflight @preflight
        $result.outcome | Should -Be 'failed'
        $result.ready_failure | Should -Be 'sampler_ready_timeout'
        $result.observed_seconds | Should -BeNullOrEmpty
        $time.Closes | Should -Be 1
    }

    It 'fails when the sampler exits during the window' {
        $state.ExitAfterChecks = 3
        $result = Invoke-SamplerPreflight @preflight
        $result.outcome | Should -Be 'failed'
        $result.exited_early | Should -BeTrue
        @($result.lifecycle_failures) | Should -Contain 'sampler_dead_at_exit'
    }

    It 'fails when the power reading does not meet P, even with a healthy sampler' {
        $online.ac_line_status = 0
        $result = Invoke-SamplerPreflight @preflight
        $result.outcome | Should -Be 'failed'
        $result.power.unmet | Should -Be @('ac_line_offline')
    }

    It 'fails when the sampler''s rows leave a gap in the window' {
        & $writeRows 5 4
        $result = Invoke-SamplerPreflight @preflight
        $result.outcome | Should -Be 'failed'
        @($result.coverage_failures) | Should -Contain 'gap_exceeded'
    }

    It 'fails when termination cannot be confirmed' {
        $state.ExitsOnStop = $false
        $state.KillIneffective = $true
        $result = Invoke-SamplerPreflight @preflight
        $result.outcome | Should -Be 'failed'
        @($result.lifecycle_failures) | Should -Contain 'sampler_not_terminated'
    }

    It 'stops the sampler even when waiting for readiness throws' {
        $preflight.WaitReady = { param($Process) throw 'ready check failed' }
        { Invoke-SamplerPreflight @preflight } | Should -Throw '*ready check failed*'
        $time.Closes | Should -Be 1
    }
}

Describe 'stage-e1.ps1 with synthetic commands' {
    BeforeAll {
        $global:DriverCommit = 'abcdefabcdefabcdefabcdefabcdefabcdefabcd'
        $collectionPath = 'reference\design\configuration-service\DMS-1490-timing-evidence\collection'

        function global:git {
            $line = $args -join ' '
            $global:DriverScenario.Calls.Add("git $line")
            $global:LASTEXITCODE = 0
            if ($line -like '*status --porcelain') { if ($global:DriverScenario.Dirty) { ' M file.cs' }; return }
            if ($line -like '*rev-parse --show-toplevel') { return $global:DriverScenario.TopLevel }
            if ($line -like '*rev-parse HEAD^{tree}') { return '9999999999999999999999999999999999999999' }
            if ($line -like '*rev-parse HEAD') { return $global:DriverScenario.Head }
        }
        function global:dotnet {
            $line = $args -join ' '
            $global:DriverScenario.Calls.Add("dotnet $line")
            $global:LASTEXITCODE = 0
            if ($line -like 'build *') { $global:LASTEXITCODE = $global:DriverScenario.BuildExit; return }
            if ($line -eq '--version') { return '10.0.100' }
            if ($line -eq '--list-sdks') { return '10.0.100 [C:\sdk]' }
        }
        function global:docker { $global:LASTEXITCODE = 0; '28.0.0' }
        function global:sqllocaldb { $global:LASTEXITCODE = 0; 'Microsoft SQL Server 2022 (16.0.1000.6)' }
        function global:python {
            $line = $args -join ' '
            $global:DriverScenario.Calls.Add("python $line")
            $global:LASTEXITCODE = 0
            if ($line -like '*sys.version*') { return '3.12.10' }
            if ($line -like '*numpy.__version__*') { return '2.5.2' }
            if ($line -like '* diagnose *') {
                $global:LASTEXITCODE = $global:DriverScenario.DiagnoseExit
                if ($global:DriverScenario.DiagnoseExit -eq 2) { return }
                $csv = $args[[array]::IndexOf($args, '--csv') + 1]
                return ([ordered]@{ input = (Split-Path -Leaf $csv); flags = [ordered]@{ stop_for_review = ($global:DriverScenario.DiagnoseExit -eq 3) } } | ConvertTo-Json)
            }
            if ($line -like '* fit *') { return '{"mu": 9.9, "sigma": 0.1, "rho": 0.2, "drift_sd": 3.5}' }
            if ($line -like '* plan *' -or $line -like '* simulate *') { return '{}' }
        }
        # A lane run: records its arguments, writes the launch record a launched run writes, and the
        # provenance the scenario asks for. An interrupted run stops after its launch record.
        function global:pwsh {
            $global:DriverScenario.Calls.Add("pwsh $($args -join ' ')")
            $lane = Split-Path -Leaf $args[[array]::IndexOf($args, '-File') + 1]
            $backend = if ($lane -eq 'lane-postgresql.ps1') { 'postgresql' } else { 'mssql' }
            $stage = $args[[array]::IndexOf($args, '-OutputDir') + 1]
            $seed = $args[[array]::IndexOf($args, '-Seed') + 1]
            $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'")
            if ($global:DriverScenario.LaneAttempted -and $global:DriverScenario.WriteLaunchRecord) {
                '{"schema": "timing-launch/1"}' | Set-Content -LiteralPath (Join-Path $stage "launch-endpoint-$backend-e1-pilot-a5-seed$seed.json")
            }
            if ($global:DriverScenario.LaneInterrupted) { $global:LASTEXITCODE = 1; return }
            [ordered]@{
                attempt_started = $global:DriverScenario.LaneAttempted
                succeeded       = ($global:DriverScenario.LaneExit -eq 0)
                collector_run   = [ordered]@{ samples_file = "e1-pilot-a5-endpoint-$backend-$stamp.csv" }
            } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage "provenance-endpoint-$backend-e1-pilot-a5-$stamp.json")
            $global:LASTEXITCODE = $global:DriverScenario.LaneExit
        }

        # The identity and preflight functions answer from the scenario and record the arguments they
        # were given; the driver's own logic and wiring is what is tested.
        function global:Get-DriverStandIn([string] $Name, [string] $Detail) {
            $global:DriverScenario.Calls.Add("$Name $Detail".Trim())
            $global:DriverScenario.Counts[$Name] = 1 + [int]$global:DriverScenario.Counts[$Name]
            if ($global:DriverScenario.Throw -contains $Name -or $global:DriverScenario.ThrowOnCall[$Name] -eq $global:DriverScenario.Counts[$Name]) { throw "${Name}: stand-in failure" }
            [ordered]@{ stand_in = $Name; files = 2; manifest_sha256 = ('0' * 64); tracked_files = 3; allowed_extra = 1 }
        }
        function global:Get-StandInCollectorBuildManifest { param($CollectorRoot, $Commit, $Tree, $BuildCommand, $SdkVersion) Get-DriverStandIn 'Get-CollectorBuildManifest' "$CollectorRoot|$Commit" }
        function global:Assert-StandInCollectorBuild { param($CollectorRoot, $ManifestPath, $ExpectedCommit, $HeadCommit, $HeadTree) Get-DriverStandIn 'Assert-CollectorBuild' "$CollectorRoot|$ExpectedCommit|$HeadCommit" }
        function global:Assert-StandInTargetSource { param($RepositoryRoot, $TargetRoot, $Commit, $Tree) Get-DriverStandIn 'Assert-TargetSource' "$RepositoryRoot|$TargetRoot|$Commit|$Tree" }
        function global:Get-StandInAnalysisManifest { param($RepositoryRoot, $Commit, $EvidenceManifestPath, $PythonVersion, $NumpyVersion) Get-DriverStandIn 'Get-AnalysisManifest' "$RepositoryRoot|$Commit" }
        function global:Assert-StandInAnalysisIdentity {
            param($RepositoryRoot, $ManifestPath, $ExpectedCommit)
            $result = Get-DriverStandIn 'Assert-AnalysisIdentity' "$RepositoryRoot|$ExpectedCommit"
            $evidence = Join-Path $RepositoryRoot 'reference\design\configuration-service\DMS-1490-timing-evidence'
            $result.paths = [ordered]@{ analyze_timing = Join-Path $evidence 'analyze_timing.py'; diagnostics = Join-Path $evidence 'diagnostics.py' }
            $result
        }
        function global:Invoke-StandInSamplerPreflight { $global:DriverScenario.Calls.Add('Invoke-SamplerPreflight'); [ordered]@{ schema = 'sampler-preflight/1'; outcome = $global:DriverScenario.PreflightOutcome } }
        $standIns = @{
            'Get-CollectorBuildManifest' = 'Get-StandInCollectorBuildManifest'
            'Assert-CollectorBuild'      = 'Assert-StandInCollectorBuild'
            'Assert-TargetSource'        = 'Assert-StandInTargetSource'
            'Get-AnalysisManifest'       = 'Get-StandInAnalysisManifest'
            'Assert-AnalysisIdentity'    = 'Assert-StandInAnalysisIdentity'
            'Invoke-SamplerPreflight'    = 'Invoke-StandInSamplerPreflight'
        }
        foreach ($name in $standIns.Keys) { Set-Alias -Name $name -Value $standIns[$name] -Scope Global }
    }

    AfterAll {
        Remove-Item -LiteralPath Function:\git, Function:\dotnet, Function:\docker, Function:\sqllocaldb, Function:\python, Function:\pwsh, Function:\Get-DriverStandIn -ErrorAction SilentlyContinue
        foreach ($name in $standIns.Keys) { Remove-Item -LiteralPath "Alias:\$name", "Function:\$($standIns[$name])" -ErrorAction SilentlyContinue }
        Remove-Variable -Name DriverScenario, DriverCommit -Scope Global -ErrorAction SilentlyContinue
    }

    BeforeEach {
        $root = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        # The driver runs from a copy in a checkout of its own, as it does from the real one.
        $repository = Join-Path $root 'repository'
        $collection = Join-Path $repository $collectionPath
        New-Item -ItemType Directory -Force -Path $collection | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'stage-e1.ps1'), (Join-Path $PSScriptRoot 'CollectionLane.psm1') -Destination $collection
        $driver = Join-Path $collection 'stage-e1.ps1'
        $evidence = Split-Path -Parent $collection
        $paths = @{
            Stage   = Join-Path $root 'stage-e1\pilot'
            Work    = Join-Path $root 'work\runs-e1'
            Targets = Join-Path $root 'work\targets'
        }
        New-Item -ItemType Directory -Force -Path (Join-Path $paths.Targets 'a5') | Out-Null
        $global:DriverScenario = @{
            Calls = [System.Collections.Generic.List[string]]::new(); Counts = @{}; Head = $global:DriverCommit; TopLevel = $repository.Replace('\', '/'); Dirty = $false; BuildExit = 0
            LaneExit = 0; LaneAttempted = $true; WriteLaunchRecord = $true; LaneInterrupted = $false; DiagnoseExit = 0; PreflightOutcome = 'passed'; Throw = @(); ThrowOnCall = @{}
        }
        $run = { param([string] $Step) & $driver -Step $Step -ExpectedCommit $global:DriverCommit @paths; $LASTEXITCODE }
        $log = { param([string] $Step) Get-Content -LiteralPath (Join-Path $paths.Stage "$($Step.ToLowerInvariant())-commands.log") -Raw }
        $calls = { param([string] $Pattern) @($global:DriverScenario.Calls | Where-Object { $_ -like $Pattern }) }
        $indexOf = { param([string] $Line) $global:DriverScenario.Calls.IndexOf($Line) }
        $before = { param([string] $Line) $global:DriverScenario.Calls[$global:DriverScenario.Calls.IndexOf($Line) - 1] }
        # Earlier steps run through the driver itself, so a later step meets their real records.
        $completeStage0 = { (& $run 'Stage0') | Should -Be 0; $global:DriverScenario.Calls.Clear(); $global:DriverScenario.Counts.Clear() }
        $passPreflight = { & $completeStage0; (& $run 'Preflight') | Should -Be 0; $global:DriverScenario.Calls.Clear(); $global:DriverScenario.Counts.Clear() }
        $diagnostics = { param([string] $Backend, [bool] $Stop) @{ input = "x-$Backend.csv"; flags = @{ stop_for_review = $Stop } } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $paths.Stage "analysis\diagnostics-$Backend.json") }
        $targetCall = { param([string] $Name, [string] $Commit, [string] $Tree) "Assert-TargetSource $repository|$(Join-Path $paths.Targets $Name)|$Commit|$Tree" }
        $a5 = @('a5', 'b2b8d5df47e5e48c46f9ca26fe0f2a58b787c399', 'd420aa2c27a94a9d3736af7c82fd7ce4298e121e')
        $baseline = @('baseline', '9a19802ddae9b23b103cd92dd6c27a8a48814cb7', 'd5d810184f6f34e8732e1ce2ad06d79509c0cf4f')
        $identityCall = "Assert-AnalysisIdentity $repository|$($global:DriverCommit)"
    }

    It 'runs Stage0 with literal commands and writes its records, without any lane or diagnostics' {
        & $run 'Stage0' | Should -Be 0
        foreach ($name in 'collector-build.json', 'target-verification.json', 'analysis-manifest.json', 'toolchain.json', 'stage0.json') { Test-Path -LiteralPath (Join-Path $paths.Stage $name) | Should -BeTrue }
        $text = & $log 'Stage0'
        $text | Should -Match ([regex]::Escape("dotnet build $(Join-Path $repository 'src\config\tests\EdFi.DmsConfigurationService.Tests.E2E\EdFi.DmsConfigurationService.Tests.E2E.csproj') -c Release --no-incremental --nologo -> exit 0"))
        $text | Should -Match ([regex]::Escape("python -I -B $(Join-Path $evidence 'analyze_timing.py') selftest -> exit 0"))
        $text | Should -Match 'DONE Stage0; stop for review'
        & $calls 'pwsh *' | Should -BeNullOrEmpty
        & $calls '* diagnose *' | Should -BeNullOrEmpty
    }

    It 'verifies each prepared target at its own folder against its own pins' {
        & $run 'Stage0' | Should -Be 0
        & $calls 'Assert-TargetSource *' | Should -Be @((& $targetCall @a5), (& $targetCall @baseline))
        $recorded = Get-Content -LiteralPath (Join-Path $paths.Stage 'target-verification.json') -Raw | ConvertFrom-Json
        $recorded.targets_root | Should -Be $paths.Targets
        @($recorded.targets.PSObject.Properties.Name) | Should -Be @('a5', 'baseline')
    }

    It 'pins the collector build and the analysis scripts to the approved commit, in its own checkout' {
        & $run 'Stage0' | Should -Be 0
        & $calls 'Get-CollectorBuildManifest *' | Should -Be @("Get-CollectorBuildManifest $repository|$($global:DriverCommit)")
        & $calls 'Assert-CollectorBuild *' | Should -Be @("Assert-CollectorBuild $repository|$($global:DriverCommit)|$($global:DriverCommit)")
        & $calls 'Get-AnalysisManifest *' | Should -Be @("Get-AnalysisManifest $repository|$($global:DriverCommit)")
        foreach ($line in & $calls 'git *') { $line | Should -BeLike "git -C $repository *" }
    }

    It 'checks the analysis identity immediately before each selftest, and records completion last' {
        & $run 'Stage0' | Should -Be 0
        foreach ($script in 'analyze_timing.py', 'diagnostics.py') {
            & $before @(& $calls "python *$script selftest")[0] | Should -Be $identityCall
        }
        $completion = Get-Content -LiteralPath (Join-Path $paths.Stage 'stage0.json') -Raw | ConvertFrom-Json
        $completion.expected_commit | Should -Be $global:DriverCommit
        $completion.targets_root | Should -Be $paths.Targets
        $completion.records.'collector-build.json' | Should -Be (Get-FileHash -LiteralPath (Join-Path $paths.Stage 'collector-build.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    It 'stops Stage0 before the selftests, and incomplete, when the analysis identity fails' {
        $global:DriverScenario.Throw = @('Assert-AnalysisIdentity')
        & $run 'Stage0' | Should -Be 1
        & $calls '* selftest' | Should -BeNullOrEmpty
        Test-Path -LiteralPath (Join-Path $paths.Stage 'stage0.json') | Should -BeFalse
    }

    It 'stops Stage0 before building when <Name>' -ForEach @(
        @{ Name = 'the checkout is not clean'; Change = @{ Dirty = $true } }
        @{ Name = 'HEAD is not the approved commit'; Change = @{ Head = '1234567890123456789012345678901234567890' } }
        @{ Name = 'the script is not at its checkout''s top level'; Change = @{ TopLevel = 'C:/elsewhere/checkout' } }
    ) {
        foreach ($key in $Change.Keys) { $global:DriverScenario[$key] = $Change[$key] }
        & $run 'Stage0' | Should -Be 1
        & $calls 'dotnet build *' | Should -BeNullOrEmpty
        Test-Path -LiteralPath (Join-Path $paths.Stage 'collector-build.json') | Should -BeFalse
        & $log 'Stage0' | Should -Match 'STOPPED Stage0'
    }

    It 'stops Stage0 without a build record when the build fails' {
        $global:DriverScenario.BuildExit = 1
        & $run 'Stage0' | Should -Be 1
        Test-Path -LiteralPath (Join-Path $paths.Stage 'collector-build.json') | Should -BeFalse
    }

    It 'stops Stage0 before the analysis record when target verification fails' {
        $global:DriverScenario.Throw = @('Assert-TargetSource')
        & $run 'Stage0' | Should -Be 1
        Test-Path -LiteralPath (Join-Path $paths.Stage 'analysis-manifest.json') | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $paths.Stage 'stage0.json') | Should -BeFalse
        & $calls '* selftest' | Should -BeNullOrEmpty
    }

    It 'never overwrites an earlier Stage0 record' {
        New-Item -ItemType Directory -Force -Path $paths.Stage | Out-Null
        '{}' | Set-Content -LiteralPath (Join-Path $paths.Stage 'collector-build.json')
        & $run 'Stage0' | Should -Be 1
        $global:DriverScenario.Calls | Should -BeNullOrEmpty
        Get-Content -LiteralPath (Join-Path $paths.Stage 'collector-build.json') -Raw | Should -Match '^\{\}'
    }

    It 'runs the PostgreSQL pilot once with the reserved settings, then its diagnostics, and nothing else' {
        & $passPreflight
        & $run 'PilotPostgreSql' | Should -Be 0
        $lanes = @(& $calls 'pwsh *')
        $lanes.Count | Should -Be 1
        $lanes[0] | Should -BeLike "pwsh -NoProfile -File $(Join-Path $collection 'lane-postgresql.ps1') -RunLabel e1-pilot-a5 -Rounds 200 -WarmupRounds 1200 -Seed 401 *"
        $lanes[0] | Should -BeLike "*-CollectorRoot $repository -TargetRoot $(Join-Path $paths.Targets 'a5') *"
        $lanes[0] | Should -BeLike "*-CollectorBuildManifest $(Join-Path $paths.Stage 'collector-build.json') -ExpectedCollectorCommit $($global:DriverCommit)"
        & $calls '*lane-localdb.ps1*' | Should -BeNullOrEmpty
        $diagnose = @(& $calls '* diagnose *')
        $diagnose.Count | Should -Be 1
        $diagnose[0] | Should -BeLike "python -I -B $(Join-Path $evidence 'diagnostics.py') diagnose --csv $(Join-Path $paths.Stage 'e1-pilot-a5-endpoint-postgresql-')*.csv --delta 250 --resamples 5000 --seed 1"
        & $before $diagnose[0] | Should -Be $identityCall
        Test-Path -LiteralPath (Join-Path $paths.Stage 'analysis\diagnostics-postgresql.json') | Should -BeTrue
    }

    It 'verifies target A5 again against its pins before the lane, and records it' {
        & $passPreflight
        & $run 'PilotPostgreSql' | Should -Be 0
        & $calls 'Assert-TargetSource *' | Should -Be @(& $targetCall @a5)
        $verified = & $indexOf (& $targetCall @a5)
        $verified | Should -BeGreaterOrEqual 0
        $verified | Should -BeLessThan (& $indexOf @(& $calls 'pwsh *')[0])
        $check = Get-ChildItem -LiteralPath $paths.Stage -Filter 'target-check-postgresql-*.json'
        @($check).Count | Should -Be 1
        (Get-Content -LiteralPath $check.FullName -Raw | ConvertFrom-Json).targets_root | Should -Be $paths.Targets
    }

    It 'refuses the pilot, launching nothing, when <Name>' -ForEach @(
        @{ Name = 'Stage0 never ran'; Arrange = { param($c) } }
        @{ Name = 'Stage0 failed'; Arrange = { param($c) $global:DriverScenario.Throw = @('Assert-TargetSource'); (& $c.Run 'Stage0') | Should -Be 1; $global:DriverScenario.Throw = @() } }
        @{ Name = 'a Stage0 record changed after Stage0 completed'; Arrange = { param($c) & $c.PassPreflight; Add-Content -LiteralPath (Join-Path $c.Paths.Stage 'collector-build.json') -Value ' ' } }
        @{ Name = 'the preflight never ran'; Arrange = { param($c) & $c.CompleteStage0 } }
        @{ Name = 'the preflight failed'; Arrange = { param($c) & $c.CompleteStage0; $global:DriverScenario.PreflightOutcome = 'failed'; (& $c.Run 'Preflight') | Should -Be 1 } }
        @{ Name = 'the preflight belongs to another Stage0'; Arrange = { param($c) & $c.PassPreflight; $p = Join-Path $c.Paths.Stage 'preflight.json'; (Get-Content -LiteralPath $p -Raw) -replace '"stage0_sha256": "[0-9a-f]+"', ('"stage0_sha256": "' + ('1' * 64) + '"') | Set-Content -LiteralPath $p } }
        @{ Name = 'another targets folder is given than the one Stage0 verified'; Arrange = { param($c) & $c.PassPreflight; $c.Paths.Targets = Join-Path (Split-Path -Parent $c.Paths.Targets) 'other-targets' } }
        @{ Name = 'target A5 no longer matches its pins'; Arrange = { param($c) & $c.PassPreflight; $global:DriverScenario.Throw = @('Assert-TargetSource') } }
    ) {
        & $Arrange @{ Run = $run; Paths = $paths; CompleteStage0 = $completeStage0; PassPreflight = $passPreflight }
        $global:DriverScenario.Calls.Clear()
        & $run 'PilotPostgreSql' | Should -Be 1
        & $calls 'pwsh *' | Should -BeNullOrEmpty
        & $calls 'python *' | Should -BeNullOrEmpty
        Test-Path -LiteralPath (Join-Path $paths.Stage 'launch-endpoint-postgresql-e1-pilot-a5-seed401.json') | Should -BeFalse
    }

    It 'refuses <Step> when the checkout is <Name> after Stage0' -ForEach @(
        @{ Step = 'Preflight'; Name = 'no longer clean'; Change = @{ Dirty = $true } }
        @{ Step = 'PilotPostgreSql'; Name = 'no longer clean'; Change = @{ Dirty = $true } }
        @{ Step = 'PilotPostgreSql'; Name = 'at another commit'; Change = @{ Head = '1234567890123456789012345678901234567890' } }
        @{ Step = 'Planning'; Name = 'at another commit'; Change = @{ Head = '1234567890123456789012345678901234567890' } }
    ) {
        if ($Step -eq 'Preflight') { & $completeStage0 } else { & $passPreflight }
        & $diagnostics 'postgresql' $false
        & $diagnostics 'mssql' $false
        if ($Step -eq 'PilotPostgreSql') { Remove-Item -LiteralPath (Join-Path $paths.Stage 'analysis\diagnostics-postgresql.json') }
        foreach ($key in $Change.Keys) { $global:DriverScenario[$key] = $Change[$key] }
        & $run $Step | Should -Be 1
        $global:DriverScenario.Calls | Should -Not -Contain 'Invoke-SamplerPreflight'
        & $calls 'pwsh *' | Should -BeNullOrEmpty
        & $calls 'python *' | Should -BeNullOrEmpty
        & $calls 'Assert-TargetSource *' | Should -BeNullOrEmpty
    }

    It 'does not reach target verification when the targets folder is not the one Stage0 verified' {
        & $passPreflight
        $paths.Targets = Join-Path (Split-Path -Parent $paths.Targets) 'other-targets'
        & $run 'PilotPostgreSql' | Should -Be 1
        & $calls 'Assert-TargetSource *' | Should -BeNullOrEmpty
        & $log 'PilotPostgreSql' | Should -Match 'The targets folder is not the one Stage0 verified'
    }

    It 'refuses another launch of a seed whose earlier run stopped after its launch record, before any provenance' {
        & $passPreflight
        $global:DriverScenario.LaneInterrupted = $true
        & $run 'PilotPostgreSql' | Should -Be 1
        Get-ChildItem -LiteralPath $paths.Stage -Filter 'provenance-*.json' | Should -BeNullOrEmpty
        $global:DriverScenario.LaneInterrupted = $false
        & $run 'PilotPostgreSql' | Should -Be 1
        @(& $calls 'pwsh *').Count | Should -Be 1
        & $calls '* diagnose *' | Should -BeNullOrEmpty
        & $log 'PilotPostgreSql' | Should -Match 'Seed 401 was already launched \(launch-endpoint-postgresql-e1-pilot-a5-seed401\.json\)'
    }

    It 'stops before the diagnostics when a run reports a launch but left no launch record' {
        & $passPreflight
        $global:DriverScenario.WriteLaunchRecord = $false
        & $run 'PilotPostgreSql' | Should -Be 1
        & $calls '* diagnose *' | Should -BeNullOrEmpty
    }

    It 'stops the pilot without diagnostics when the lane fails' {
        & $passPreflight
        $global:DriverScenario.LaneExit = 1
        & $run 'PilotPostgreSql' | Should -Be 1
        & $calls '* diagnose *' | Should -BeNullOrEmpty
    }

    It 'stops with exit 3, keeping the diagnostics, when they raise a flag' {
        & $passPreflight
        $global:DriverScenario.DiagnoseExit = 3
        & $run 'PilotPostgreSql' | Should -Be 3
        (Get-Content -LiteralPath (Join-Path $paths.Stage 'analysis\diagnostics-postgresql.json') -Raw | ConvertFrom-Json).flags.stop_for_review | Should -BeTrue
        & $log 'PilotPostgreSql' | Should -Match 'a flag was raised; the stage stops for review'
    }

    It 'stops with exit 1 when the diagnostics refuse their input' {
        & $passPreflight
        $global:DriverScenario.DiagnoseExit = 2
        & $run 'PilotPostgreSql' | Should -Be 1
    }

    It 'stops before the diagnostics when the analysis identity no longer holds' {
        & $passPreflight
        $global:DriverScenario.Throw = @('Assert-AnalysisIdentity')
        & $run 'PilotPostgreSql' | Should -Be 1
        & $calls '* diagnose *' | Should -BeNullOrEmpty
    }

    It 'refuses a pilot whose seed was already attempted, before launching anything' {
        & $passPreflight
        '{"attempt_started": true}' | Set-Content -LiteralPath (Join-Path $paths.Stage 'provenance-endpoint-postgresql-e1-pilot-a5-20261024T000000Z.json')
        & $run 'PilotPostgreSql' | Should -Be 1
        & $calls 'pwsh *' | Should -BeNullOrEmpty
        & $log 'PilotPostgreSql' | Should -Match 'Seed 401 was already attempted'
    }

    It 'allows a pilot whose earlier run was only refused before the launch' {
        & $passPreflight
        '{"attempt_started": false}' | Set-Content -LiteralPath (Join-Path $paths.Stage 'provenance-endpoint-postgresql-e1-pilot-a5-20261024T000000Z.json')
        & $run 'PilotPostgreSql' | Should -Be 0
    }

    It 'refuses the SQL Server pilot <Name>' -ForEach @(
        @{ Name = 'before the PostgreSQL pilot''s diagnostics exist'; Flagged = $null }
        @{ Name = 'when the PostgreSQL pilot raised a flag'; Flagged = $true }
    ) {
        & $passPreflight
        if ($null -ne $Flagged) { & $diagnostics 'postgresql' $Flagged }
        & $run 'PilotSqlServer' | Should -Be 1
        & $calls 'pwsh *' | Should -BeNullOrEmpty
    }

    It 'runs the SQL Server pilot with seed 402 after a flag-free PostgreSQL pilot' {
        & $passPreflight
        & $diagnostics 'postgresql' $false
        & $run 'PilotSqlServer' | Should -Be 0
        @(& $calls 'pwsh *')[0] | Should -BeLike "pwsh -NoProfile -File $(Join-Path $collection 'lane-localdb.ps1') -RunLabel e1-pilot-a5 -Rounds 200 -WarmupRounds 1200 -Seed 402 *"
    }

    It 'plans every candidate N for both backends with the pinned parameters, choosing nothing' {
        & $completeStage0
        & $diagnostics 'postgresql' $false
        & $diagnostics 'mssql' $false
        & $run 'Planning' | Should -Be 0
        $simulations = & $calls '* simulate *'
        $simulations.Count | Should -Be 8
        foreach ($rounds in 500, 1000, 2000, 4000) { @($simulations | Where-Object { $_ -like "* --rounds $rounds --delta 250 --mu 9.9 --sigma 0.1 --rho 0.2 --drift-sd 3.5 --replicates 500 --resamples 5000 --seed 1" }).Count | Should -Be 2 }
        @(& $calls '* plan *')[0] | Should -BeLike '* --candidates 500 1000 2000 4000 --resamples 5000 --seed 1'
        & $calls 'pwsh *' | Should -BeNullOrEmpty
    }

    It 'checks the analysis identity immediately before every planning command, and runs the script it verified' {
        & $completeStage0
        & $diagnostics 'postgresql' $false
        & $diagnostics 'mssql' $false
        & $run 'Planning' | Should -Be 0
        $analyses = @(& $calls 'python *')
        $analyses.Count | Should -Be 12
        foreach ($line in $analyses) {
            $line | Should -BeLike "python -I -B $(Join-Path $evidence 'analyze_timing.py') *"
            & $before $line | Should -Be $identityCall
        }
    }

    It 'stops planning before the next command when a script changes between commands' {
        & $completeStage0
        & $diagnostics 'postgresql' $false
        & $diagnostics 'mssql' $false
        $global:DriverScenario.ThrowOnCall = @{ 'Assert-AnalysisIdentity' = 3 }
        & $run 'Planning' | Should -Be 1
        @(& $calls 'python *').Count | Should -Be 2
        Test-Path -LiteralPath (Join-Path $paths.Stage 'analysis\simulate-postgresql-500.json') | Should -BeFalse
    }

    It 'refuses planning when a pilot raised a flag' {
        & $completeStage0
        & $diagnostics 'postgresql' $false
        & $diagnostics 'mssql' $true
        & $run 'Planning' | Should -Be 1
        & $calls 'python *' | Should -BeNullOrEmpty
    }

    It 'records the preflight, with the Stage0 it follows, and stops with exit <Exit> when it <Outcome>' -ForEach @(@{ Outcome = 'passed'; Exit = 0 }, @{ Outcome = 'failed'; Exit = 1 }) {
        & $completeStage0
        $global:DriverScenario.PreflightOutcome = $Outcome
        & $run 'Preflight' | Should -Be $Exit
        $preflight = Get-Content -LiteralPath (Join-Path $paths.Stage 'preflight.json') -Raw | ConvertFrom-Json
        $preflight.outcome | Should -Be $Outcome
        $preflight.expected_commit | Should -Be $global:DriverCommit
        $preflight.stage0_sha256 | Should -Be (Get-FileHash -LiteralPath (Join-Path $paths.Stage 'stage0.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        & $run 'Preflight' | Should -Be 1
        @($global:DriverScenario.Calls | Where-Object { $_ -eq 'Invoke-SamplerPreflight' }).Count | Should -Be 1
    }

    It 'refuses the preflight before Stage0 has completed' {
        & $run 'Preflight' | Should -Be 1
        $global:DriverScenario.Calls | Should -Not -Contain 'Invoke-SamplerPreflight'
    }

    It 'refuses a staging folder inside the collector checkout' {
        $paths.Stage = Join-Path $repository 'stage'
        { & $driver -Step Stage0 -ExpectedCommit $global:DriverCommit @paths } | Should -Throw '*OutputDir*'
    }

    It 'refuses an expected commit that is not a full id' {
        { & $driver -Step Stage0 -ExpectedCommit 'abc' @paths } | Should -Throw
    }

    It 'takes no checkout other than its own, so none can be checked while another runs' {
        $other = Join-Path $root 'other-checkout'
        { & $driver -Step Stage0 -ExpectedCommit $global:DriverCommit -Repository $other @paths } | Should -Throw '*Repository*'
        $global:DriverScenario.Calls | Should -BeNullOrEmpty
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
            $global:LaneScenario.LaunchRecordAtLaunch = @(Get-ChildItem -LiteralPath $env:TIMING_EVIDENCE_OUTPUT_DIR -Filter 'launch-endpoint-postgresql-*.json').Count -eq 1
            "runner output with $($global:LaneSentinel)"
            $results = $args[[array]::IndexOf($args, '--results-directory') + 1]
            $logger = [string]$args[[array]::IndexOf($args, '--logger') + 1]
            $trx = Join-Path $results ($logger -replace '^trx;LogFileName=', '')
            Write-TestTrx $trx $global:LaneScenario.TestNames $global:LaneFixtureClass
            if ($global:LaneScenario.WriteSamples) {
                Write-TestSample $env:TIMING_EVIDENCE_OUTPUT_DIR $env:TIMING_EVIDENCE_RUN_LABEL 'postgresql' ([int]$env:TIMING_EVIDENCE_ROUNDS) ([int]$env:TIMING_EVIDENCE_SEED) -WarmupRounds ([int]$env:TIMING_EVIDENCE_WARMUP_ROUNDS)
            }
            # A sampler that dies by the exit dies while the collector runs, after its readiness.
            if ($global:SamplerScenario.DeadAtExit -and $null -ne $global:SamplerScenario.Process) { $global:SamplerScenario.Process.State.Exited = $true }
            $global:LASTEXITCODE = $global:LaneScenario.CollectorExit
        }

        # The sampler stand-in, reached through an alias as the power reader is. It writes the
        # ready record and first row at once; when the lane signals stop (WaitForExit), it writes
        # the rows up to that moment at $global:SamplerScenario.Step seconds apart, so that they
        # bracket the collection as a real sampler's rows would. Nothing sleeps.
        function global:Open-TestSampler {
            param([string] $ScriptPath, [System.Collections.IDictionary] $Argument, [string] $StandardOutputPath, [string] $StandardErrorPath)
            $scenario = $global:SamplerScenario
            $scenario.Arguments = $Argument
            $startQpc = [System.Diagnostics.Stopwatch]::GetTimestamp()
            $startUtc = [datetime]::UtcNow
            $state = @{ Id = 4242; ExitsOnStop = -not $scenario.SlowShutdown; KillIneffective = $scenario.KillIneffective; ExitCode = $scenario.ExitCode; Exited = $scenario.ExitBeforeReady; Written = $false }
            $process = New-TestProcess $state
            $scenario.Process = $process
            if ($scenario.ExitBeforeReady) { return $process }
            $first = New-TestSampleSpec 1 -RunId $Argument.RunId
            $first[0].Kind = $scenario.ReadyKind
            $firstLine = (ConvertTo-TestSampleLine $first -StartQpc $startQpc -StartUtc $startUtc)[0]
            Set-Content -LiteralPath $Argument.PowerPath -Value @(($global:SamplerColumns -join ','), $firstLine) -Encoding utf8NoBOM
            Write-TestReady -Path $Argument.ReadyPath -RunId $Argument.RunId -ProcessId 4242 -FirstRow $firstLine -RunLabel $Argument.RunLabel -LaneStamp $Argument.LaneStamp
            if ($scenario.DeadBeforeLaunch) { $state.Exited = $true }
            $state.OnWait = {
                if ($state.Written) { return }
                $state.Written = $true
                $elapsed = ([System.Diagnostics.Stopwatch]::GetTimestamp() - $startQpc) / $global:QpcFrequency
                $count = [int][Math]::Ceiling($elapsed / $scenario.Step) + 1
                $spec = New-TestSampleSpec $count -Step $scenario.Step -RunId $Argument.RunId
                $spec[$count - 1].Kind = $scenario.LastKind
                $lines = ConvertTo-TestSampleLine $spec -StartQpc $startQpc -StartUtc $startUtc
                Add-Content -LiteralPath $Argument.PowerPath -Value $lines[1..($count - 1)] -Encoding utf8NoBOM
            }.GetNewClosure()
            $process
        }
        Set-Alias -Name Open-PowerSampler -Value Open-TestSampler -Scope Global
    }

    AfterAll {
        Remove-Item -LiteralPath Function:\git, Function:\Get-NetTCPConnection, Function:\Invoke-WebRequest, Function:\pwsh, Function:\docker, Function:\dotnet, Function:\Open-TestSampler, Alias:\Open-PowerSampler -ErrorAction SilentlyContinue
    }

    BeforeEach {
        $run = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $collector = Join-Path $run 'collector'
        $target = Join-Path $run 'target'
        $stage = Join-Path $run 'stage'
        $work = Join-Path $run 'work'
        New-Item -ItemType Directory -Force -Path $collector, $target | Out-Null
        $collectorOutput = New-TestCollectorBuild $collector (Join-Path $run 'collector-build.json')
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
        $global:SamplerScenario = @{ ExitBeforeReady = $false; ReadyKind = 'online'; LastKind = 'online'; Step = 1; DeadBeforeLaunch = $false; DeadAtExit = $false; SlowShutdown = $false; KillIneffective = $false; ExitCode = 0; Arguments = $null; Process = $null }
        $arguments = @{ RunLabel = 'pilot-a5'; Rounds = 20; WarmupRounds = 0; Seed = 301; OutputDir = $stage; CollectorRoot = $collector; TargetRoot = $target; WorkRoot = $work; CollectorBuildManifest = (Join-Path $run 'collector-build.json'); ExpectedCollectorCommit = $global:TestCommit }
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
        $provenance.environment_protocol.scheme_query_provider | Should -Be 'PowerGetActiveScheme (powrprof.dll)'
        Get-RefusalLine $stage | Should -BeNullOrEmpty
    }

    It 'refuses before setup, without setup or collection, when the power source is <Kind>' -ForEach @(
        @{ Kind = 'offline'; Unmet = 'ac_line_offline' }
        @{ Kind = 'unknown'; Unmet = 'ac_line_unknown' }
        @{ Kind = 'call_failed'; Unmet = 'call_failed' }
        @{ Kind = 'saver_on'; Unmet = 'battery_saver_on' }
        @{ Kind = 'wrong_scheme'; Unmet = 'scheme_not_balanced' }
        @{ Kind = 'scheme_status'; Unmet = 'scheme_query_failed' }
        @{ Kind = 'scheme_null'; Unmet = 'scheme_query_failed' }
        @{ Kind = 'scheme_release_error'; Unmet = 'scheme_query_failed' }
        @{ Kind = 'scheme_invalid'; Unmet = 'scheme_query_failed' }
        @{ Kind = 'scheme_throws'; Unmet = 'scheme_query_failed' }
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

    It 'fails the attempt, without a refusal, when the scheme query fails by the time the collector exits' {
        Set-PowerSequence 'online', 'online', 'scheme_status'
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        $global:LaneScenario.Calls | Should -Contain 'dotnet test'
        $provenance = & $readProvenance
        @($provenance.failures.stage) | Should -Be @('power-after-exit')
        $provenance.power_checks[2].unmet | Should -Be @('scheme_query_failed')
        $provenance.power_checks[2].reading.scheme_query_status | Should -Be 2
        $provenance.power_checks[2].reading.scheme_query_failure | Should -Be 'status_nonzero'
        Get-RefusalLine $stage | Should -BeNullOrEmpty
    }

    It 'refuses before the launch, after setup, when the scheme query fails there' {
        Set-PowerSequence 'online', 'scheme_null'
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        $global:LaneScenario.Calls | Should -Not -Contain 'dotnet test'
        (& $teardownCalls).Count | Should -Be 1
        $refusal = (Get-RefusalLine $stage)[0]
        $refusal.check | Should -Be 'before_launch'
        $refusal.reading.scheme_query_failure | Should -Be 'null_result'
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

    It 'records the verified collector build in provenance' {
        & $lane @arguments
        $LASTEXITCODE | Should -Be 0
        $build = (& $readProvenance).collector_build
        $build.commit | Should -Be $global:TestCommit
        $build.files | Should -Be 2
        $build.manifest_sha256 | Should -Be (Get-FileHash -LiteralPath $arguments.CollectorBuildManifest -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    It 'writes the seed''s launch record before the collector launches, and names it in provenance' {
        & $lane @arguments
        $LASTEXITCODE | Should -Be 0
        $launch = Join-Path $stage 'launch-endpoint-postgresql-pilot-a5-seed301.json'
        $record = Get-Content -LiteralPath $launch -Raw | ConvertFrom-Json
        $record.schema | Should -Be 'timing-launch/1'
        $record.seed | Should -Be 301
        $record.run_label | Should -Be 'pilot-a5'
        $provenanceFile = Get-ChildItem $stage -Filter 'provenance-*.json'
        $record.provenance_file | Should -Be $provenanceFile.Name
        $record.sampler_run_id | Should -Be $global:SamplerScenario.Arguments.RunId
        (& $readProvenance).launch_record | Should -Be 'launch-endpoint-postgresql-pilot-a5-seed301.json'
        $global:LaneScenario.LaunchRecordAtLaunch | Should -BeTrue
    }

    It 'stops before setup, launching nothing, when the seed already has a launch record' {
        New-Item -ItemType Directory -Force -Path $stage | Out-Null
        $launch = Join-Path $stage 'launch-endpoint-postgresql-pilot-a5-seed301.json'
        '{"earlier": true}' | Set-Content -LiteralPath $launch
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        & $setupCalls | Should -BeNullOrEmpty
        $global:LaneScenario.Calls | Should -Not -Contain 'dotnet test'
        (& $readProvenance).attempt_started | Should -BeFalse
        Get-Content -LiteralPath $launch -Raw | Should -Match '"earlier": true'
    }

    It 'does not launch the collector when the launch record cannot be written' {
        function global:Add-TestLaunchRecordFailure { param($Path, $Record) throw 'the launch record could not be written' }
        Set-Alias -Name Add-LaunchRecord -Value Add-TestLaunchRecordFailure -Scope Global
        try {
            & $lane @arguments
            $LASTEXITCODE | Should -Be 1
            $global:LaneScenario.Calls | Should -Not -Contain 'dotnet test'
            $provenance = & $readProvenance
            $provenance.attempt_started | Should -BeFalse
            $provenance.launch_record | Should -BeNullOrEmpty
            @($provenance.failures.stage) | Should -Be @('run')
            (& $teardownCalls).Count | Should -Be 1
        }
        finally {
            Remove-Item -LiteralPath Alias:\Add-LaunchRecord, Function:\Add-TestLaunchRecordFailure -ErrorAction SilentlyContinue
        }
    }

    It 'leaves the seed unused when the run is refused before the launch' {
        Set-PowerSequence 'online', 'offline'
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        Test-Path -LiteralPath (Join-Path $stage 'launch-endpoint-postgresql-pilot-a5-seed301.json') | Should -BeFalse
    }

    It 'refuses before setup, without collecting, when the collector build <Name>' -ForEach @(
        @{ Name = 'has a changed file'; Change = { param($o, $a) Add-Content -LiteralPath (Join-Path $o 'EdFi.DmsConfigurationService.Tests.E2E.dll') -Value 'x' } }
        @{ Name = 'has an extra file'; Change = { param($o, $a) Set-Content -LiteralPath (Join-Path $o 'stale.dll') -Value 'stale' } }
        @{ Name = 'is missing a file'; Change = { param($o, $a) Remove-Item -LiteralPath (Join-Path $o 'runtimes\native.dll') } }
        @{ Name = 'is not for the approved commit'; Change = { param($o, $a) $a.ExpectedCollectorCommit = '2222222222222222222222222222222222222222' } }
    ) {
        & $Change $collectorOutput $arguments
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        & $setupCalls | Should -BeNullOrEmpty
        $global:LaneScenario.Calls | Should -Not -Contain 'dotnet test'
        $provenance = & $readProvenance
        $provenance.attempt_started | Should -BeFalse
        $provenance.collector_build | Should -BeNullOrEmpty
        @($provenance.power_checks) | Should -BeNullOrEmpty
    }

    It 'starts the sampler with the docker_vm role and requires its coverage of a successful run' {
        & $lane @arguments
        $LASTEXITCODE | Should -Be 0
        $global:SamplerScenario.Arguments.DockerVm | Should -BeTrue
        $global:SamplerScenario.Arguments.Contains('CmsProcessId') | Should -BeFalse
        $global:SamplerScenario.Arguments.PowerPath | Should -BeLike (Join-Path $stage 'host-power-postgresql-pilot-a5-*.csv')
        $global:SamplerScenario.Arguments.StopPath | Should -BeLike (Join-Path $work '*')
        $sampler = (& $readProvenance).sampler
        $sampler.ready_failure | Should -BeNullOrEmpty
        @($sampler.coverage_failures) | Should -BeNullOrEmpty
        $sampler.power_rows | Should -BeGreaterOrEqual 2
        $sampler.stop.stopped_in_time | Should -BeTrue
        $sampler.launch_qpc | Should -BeLessThan $sampler.exit_qpc
        $global:SamplerScenario.Process.State.WaitCalls[0] | Should -Be 10000
    }

    It 'refuses before the launch, and tears down, when the sampler <Name>' -ForEach @(
        @{ Name = 'exits before it is ready'; Change = @{ ExitBeforeReady = $true }; Unmet = @('sampler_exited_before_ready') }
        @{ Name = 'dies after writing its ready record'; Change = @{ DeadBeforeLaunch = $true }; Unmet = @('sampler_exited_before_ready') }
        @{ Name = 'reports a first row that does not meet P'; Change = @{ ReadyKind = 'offline' }; Unmet = @('sampler_ready_not_p', 'ac_line_offline') }
        @{ Name = 'reports a first row whose scheme query failed'; Change = @{ ReadyKind = 'scheme_failed' }; Unmet = @('sampler_ready_not_p', 'scheme_query_failed') }
    ) {
        foreach ($key in $Change.Keys) { $global:SamplerScenario[$key] = $Change[$key] }
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        $global:LaneScenario.Calls | Should -Not -Contain 'dotnet test'
        (& $teardownCalls).Count | Should -Be 1
        $refusals = Get-RefusalLine $stage
        $refusals.Count | Should -Be 1
        $refusals[0].check | Should -Be 'before_launch'
        @($refusals[0].unmet) | Should -Be $Unmet
        $provenance = & $readProvenance
        $provenance.attempt_started | Should -BeFalse
        $provenance.sampler.ready_failure | Should -Be $Unmet[0]
        $provenance.sampler.cleanup_stop | Should -Not -BeNullOrEmpty
    }

    It 'fails the attempt with <Code> when the sampler <Name>' -ForEach @(
        @{ Name = 'leaves a gap over 3 s'; Change = @{ Step = 5 }; Code = 'gap_exceeded' }
        @{ Name = 'reports power lost after the exit'; Change = @{ LastKind = 'offline' }; Code = 'power_unmet_in_window' }
        @{ Name = 'reports a failed scheme query after the exit'; Change = @{ LastKind = 'scheme_failed' }; Code = 'power_unmet_in_window' }
        @{ Name = 'has died by the exit'; Change = @{ DeadAtExit = $true }; Code = 'sampler_dead_at_exit' }
        @{ Name = 'does not stop in time'; Change = @{ SlowShutdown = $true }; Code = 'sampler_slow_shutdown' }
        @{ Name = 'exits nonzero'; Change = @{ ExitCode = 1 }; Code = 'sampler_exit_nonzero' }
    ) {
        foreach ($key in $Change.Keys) { $global:SamplerScenario[$key] = $Change[$key] }
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        $global:LaneScenario.Calls | Should -Contain 'dotnet test'
        (& $teardownCalls).Count | Should -Be 1
        $provenance = & $readProvenance
        $provenance.attempt_started | Should -BeTrue
        @($provenance.failures.stage) | Should -Be @('sampler-coverage')
        @($provenance.sampler.coverage_failures) | Should -Contain $Code
        Get-RefusalLine $stage | Should -BeNullOrEmpty
    }

    It 'kills a sampler that does not stop in time, confirms its termination and does not stop it again' {
        $global:SamplerScenario.SlowShutdown = $true
        & $lane @arguments
        $global:SamplerScenario.Process.State.Killed | Should -BeTrue
        $sampler = (& $readProvenance).sampler
        $sampler.stop.kill_requested | Should -BeTrue
        $sampler.stop.terminated | Should -BeTrue
        $sampler.cleanup_stop | Should -BeNullOrEmpty
        @($global:SamplerScenario.Process.State.WaitCalls) | Should -Be @(10000, 5000)
    }

    It 'tries once more in cleanup, and reports a sampler that survives the kill' {
        $global:SamplerScenario.SlowShutdown = $true
        $global:SamplerScenario.KillIneffective = $true
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        $provenance = & $readProvenance
        $provenance.sampler.stop.terminated | Should -BeFalse
        @($provenance.sampler.coverage_failures) | Should -Contain 'sampler_not_terminated'
        $provenance.sampler.cleanup_stop.terminated | Should -BeFalse
        @($provenance.failures.stage) | Should -Be @('sampler-coverage', 'sampler-stop')
        @($global:SamplerScenario.Process.State.WaitCalls) | Should -Be @(10000, 5000, 10000, 5000)
        (Get-ChildItem $stage -Filter 'lane-postgresql-*.log' | Get-Content -Raw) | Should -Not -Match 'SAMPLER stopped during cleanup'
    }

    It 'judges the sampler after a failed collector and records both failures' {
        $global:SamplerScenario.Step = 5
        $global:LaneScenario.CollectorExit = 1
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        @((& $readProvenance).failures.stage) | Should -Be @('sampler-coverage', 'run')
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
        Remove-Item -LiteralPath Function:\git, Function:\Get-NetTCPConnection, Function:\docker, Function:\sqllocaldb, Function:\sqlcmd -ErrorAction SilentlyContinue
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
        $collectorOutput = New-TestCollectorBuild $collector (Join-Path $run 'collector-build.json')
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
        $arguments = @{ RunLabel = 'pilot-a5'; Rounds = 20; WarmupRounds = 0; Seed = 302; OutputDir = $stage; CollectorRoot = $collector; TargetRoot = $target; WorkRoot = $work; CollectorBuildManifest = (Join-Path $run 'collector-build.json'); ExpectedCollectorCommit = $global:TestCommit }
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

    It 'stops before any LocalDB work when the seed already has a launch record' {
        New-Item -ItemType Directory -Force -Path $stage | Out-Null
        '{"earlier": true}' | Set-Content -LiteralPath (Join-Path $stage 'launch-endpoint-mssql-pilot-a5-seed302.json')
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        $global:LaneScenario.Calls | Should -BeNullOrEmpty
        (Get-Content (Get-ChildItem $stage -Filter 'provenance-*.json').FullName -Raw | ConvertFrom-Json).attempt_started | Should -BeFalse
    }

    It 'refuses before any LocalDB work when the collector build has a changed file' {
        Add-Content -LiteralPath (Join-Path $collectorOutput 'EdFi.DmsConfigurationService.Tests.E2E.dll') -Value 'x'
        & $lane @arguments
        $LASTEXITCODE | Should -Be 1
        $global:LaneScenario.Calls | Should -BeNullOrEmpty
        Test-Path (Join-Path $work 'db') | Should -BeFalse
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
    Remove-Item -LiteralPath Function:\Get-TestHash, Function:\Get-ValidSetting, Function:\Get-ValidFingerprint, Function:\Write-TestTrx, Function:\Write-TestSample, Function:\New-TestSampleRowSet, Function:\Get-EvidenceText -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath Alias:\Read-SystemPowerStatus, Alias:\Read-ActivePowerScheme, Function:\Get-TestPowerStatus, Function:\Get-TestActiveScheme, Function:\New-PowerStep, Function:\Set-PowerSequence, Function:\Get-PowerStep, Function:\Get-RefusalLine -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath Function:\New-TestSampleSpec, Function:\ConvertTo-TestSampleLine, Function:\Write-TestReady, Function:\New-TestProcess, Function:\New-TestCollectorBuild -ErrorAction SilentlyContinue
    Remove-Variable -Name TestCommit, TestStartUtc, TestSamplesColumns, LaneScenario, LaneSentinel, LaneAssemblies, LaneFixtureClass, PowerScenario, BalancedScheme, SamplerScenario, SamplerColumns, QpcFrequency -Scope Global -ErrorAction SilentlyContinue
}
