# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# DMS-1019: the benchmark comparison wrapper's exit contract and orchestration, with every
# external effect (git, docker, dotnet, the overlay copy, the collector, host facts, the host
# lock and the clock) mocked. No database, Docker or Windows host is needed.

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../BenchmarkComparison.psm1') -Force

    $script:Reference = 'df7e23425beef2de46fa98b3b5b48578cb2d3692'
    $script:Other = 'c' * 40
    $script:Harness = 'd' * 40
    $script:Digest = 'sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873'
    $script:ProfileFile = Join-Path $PSScriptRoot '../../../src/dms/tests/EdFi.DataManagementService.Performance.Harness/Configuration/benchmark-profile.json'
    $script:Write = 'student-put-collection-update'
    $script:Read = 'traditional-offset-zero/25'
    $script:Metrics = @('host-cpu-percent', 'host-available-mb', 'db-cpu-percent', 'db-memory-percent', 'db-block-io-bytes')
    $script:SavedPg = $env:ConnectionStrings__DatabaseConnection
    $script:SavedMssql = $env:ConnectionStrings__MssqlAdmin
    $env:ConnectionStrings__DatabaseConnection = 'Host=elsewhere;Port=1;Username=u;Password=p;Database=d'
    $env:ConnectionStrings__MssqlAdmin = 'Server=elsewhere,1;User Id=sa;Password=p'

    # Returns the exit code, or with -Outcome the whole { ExitCode; InvocationDirectory } object.
    function script:Invoke-Comparison {
        param([hashtable] $Parameters = @{}, [switch] $Outcome)
        $arguments = @{
            BaselineCommit   = $script:Reference
            Provider         = 'postgresql'
            ResultsDirectory = $script:Results
            WorktreeRoot     = $script:WorktreeRoot
            SourceRoot       = $script:SourceRoot
            Pairs            = 2
            Purpose          = 'Calibration'
        }
        foreach ($key in $Parameters.Keys) { $arguments[$key] = $Parameters[$key] }
        $result = Invoke-BenchmarkComparison @arguments
        return $Outcome ? $result : $result.ExitCode
    }

    function script:Get-SideCall {
        return @($script:DotnetCalls | Where-Object { $_.Arguments -match 'BenchmarkSideRun' })
    }

    function script:Get-RunPlan {
        $plan = Get-ChildItem -Path $script:Results -Recurse -Filter 'run-plan.json' | Select-Object -First 1
        return Get-Content -Raw -LiteralPath $plan.FullName | ConvertFrom-Json
    }

    function script:New-Sample {
        param([DateTime] $Stamp, [string[]] $Metric = $script:Metrics)
        return @($Metric | ForEach-Object { [pscustomobject]@{ TimestampUtc = $Stamp; Phase = 'side'; Metric = $_ } })
    }
}

AfterAll {
    $env:ConnectionStrings__DatabaseConnection = $script:SavedPg
    $env:ConnectionStrings__MssqlAdmin = $script:SavedMssql
    Remove-Module BenchmarkComparison -Force -ErrorAction SilentlyContinue
}

Describe 'Invoke-BenchmarkComparison' {
    BeforeEach {
        $script:SourceRoot = Join-Path $TestDrive "source-$([guid]::NewGuid().ToString('N'))"
        $script:Results = Join-Path $TestDrive "results-$([guid]::NewGuid().ToString('N'))"
        $script:WorktreeRoot = Join-Path $TestDrive "worktrees-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:SourceRoot | Out-Null

        $script:BenchmarkProfile = Get-Content -Raw -LiteralPath $script:ProfileFile | ConvertFrom-Json
        $script:Clock = [DateTime]::new(2026, 11, 23, 12, 0, 0, [DateTimeKind]::Utc)
        $script:DotnetCalls = [Collections.Generic.List[object]]::new()
        $script:DirtySource = $false
        $script:PreFloor = @()
        $script:BuildExit = 0
        $script:SideExits = @{}
        $script:ReportExit = 0
        $script:WriteComparisonJson = $true
        $script:ComparisonResult = 'acceptable'
        $script:ComparisonExit = 0
        $script:ImageDigest = $script:Digest
        $script:CollectorReady = $true
        $script:SampleCalls = 0
        $script:SideExitAt = $null
        $script:StopCalls = [Collections.Generic.List[object]]::new()

        Mock -ModuleName BenchmarkComparison Write-Host { }
        Mock -ModuleName BenchmarkComparison Open-BenchmarkHostLock { [IO.MemoryStream]::new() }
        Mock -ModuleName BenchmarkComparison Read-BenchmarkProfile {
            [pscustomobject]@{ Profile = $script:BenchmarkProfile; Sha256 = 'a' * 64 }
        }
        Mock -ModuleName BenchmarkComparison Get-BenchmarkUtcNow { $script:Clock }
        Mock -ModuleName BenchmarkComparison Wait-BenchmarkDelay { $script:Clock = $script:Clock.AddSeconds($Seconds) }
        Mock -ModuleName BenchmarkComparison Copy-HarnessOverlay { }
        Mock -ModuleName BenchmarkComparison Invoke-BenchmarkGit {
            $result = { param($ExitCode, $Output) [pscustomobject]@{ ExitCode = $ExitCode; Output = @($Output) } }
            switch ($ArgumentList[0]) {
                'status' {
                    if ($Repository -eq $script:SourceRoot) {
                        return & $result 0 ($script:DirtySource ? @(' M src/dms/tests/EdFi.DataManagementService.Performance.Harness/Edited.cs') : @())
                    }
                    # A worktree: dirty only inside the overlay.
                    return & $result 0 @(' M src/dms/tests/EdFi.DataManagementService.Performance.Harness/Results/X.cs')
                }
                'rev-parse' {
                    if ($ArgumentList[1] -eq 'HEAD') {
                        $commit = @($script:Reference, $script:Other) | Where-Object { $Repository.EndsWith($_.Substring(0, 12)) }
                        return & $result 0 $commit
                    }
                    $name = $ArgumentList[2] -replace '\^\{commit\}$', ''
                    if ($name -eq 'HEAD') { return & $result 0 $script:Harness }
                    if ($name -match '^[0-9a-f]{40}$') { return & $result 0 $name }
                    return & $result 128 "fatal: bad revision '$name'"
                }
                'merge-base' { return & $result ($ArgumentList[3] -in $script:PreFloor ? 1 : 0) @() }
                'worktree' {
                    New-Item -ItemType Directory -Force -Path $ArgumentList[3] | Out-Null
                    return & $result 0 @()
                }
            }
            return & $result 0 @()
        }
        Mock -ModuleName BenchmarkComparison Invoke-BenchmarkDocker {
            $format = $ArgumentList[-1]
            $output = switch -Wildcard ($format) {
                '{{.Config.Image}}' { 'postgres:18.6-alpine' }
                '{{.Image}}' { 'sha256:' + ('e' * 64) }
                '{{index .RepoDigests 0}}' { "postgres@$($script:ImageDigest)" }
                '{{json .NetworkSettings.Ports}}' { '{"5432/tcp":[{"HostIp":"0.0.0.0","HostPort":"5432"}],"1433/tcp":[{"HostIp":"127.0.0.1","HostPort":"1433"}]}' }
                '*Config.Env*' { 'PGDATA=/var/lib/postgresql/data' }
                default { '' }
            }
            [pscustomobject]@{ ExitCode = 0; Output = @($output) }
        }
        Mock -ModuleName BenchmarkComparison Invoke-BenchmarkDotnet {
            $call = [pscustomobject]@{ Arguments = $ArgumentList -join ' '; Environment = $Environment ? $Environment.Clone() : @{}; LogPath = $LogPath; Clock = $script:Clock }
            $script:DotnetCalls.Add($call)
            if ($ArgumentList[0] -eq 'build') { return $script:BuildExit }
            if ($call.Arguments -match 'BenchmarkSideRun') {
                $script:SideExitAt = $script:Clock
                $index = @($script:DotnetCalls | Where-Object { $_.Arguments -match 'BenchmarkSideRun' }).Count
                return $script:SideExits.ContainsKey($index) ? $script:SideExits[$index] : 0
            }
            if ($script:WriteComparisonJson) {
                $directory = $Environment['PERF_BENCHMARK_DIR']
                [IO.File]::WriteAllText((Join-Path $directory 'comparison.json'), (@{ result = $script:ComparisonResult; exitCode = $script:ComparisonExit } | ConvertTo-Json))
                [IO.File]::WriteAllText((Join-Path $directory 'comparison.md'), "# Benchmark comparison`n")
            }
            return $script:ReportExit
        }
        Mock -ModuleName BenchmarkComparison Start-BenchmarkCollector { [pscustomobject]@{ SideDirectory = $SideDirectory } }
        Mock -ModuleName BenchmarkComparison Wait-BenchmarkCollectorReady { $script:CollectorReady }
        Mock -ModuleName BenchmarkComparison Set-BenchmarkCollectorPhase { }
        Mock -ModuleName BenchmarkComparison Get-BenchmarkCollectorSample {
            $script:SampleCalls++
            New-Sample -Stamp $script:Clock.AddSeconds(1)
        }
        Mock -ModuleName BenchmarkComparison Stop-BenchmarkCollector {
            $script:StopCalls.Add([pscustomobject]@{ SampleCalls = $script:SampleCalls; Clock = $script:Clock })
        }
        Mock -ModuleName BenchmarkComparison Get-BenchmarkHostFact {
            [ordered]@{
                osFamily = 'Windows'; architecture = 'x64'; osBuild = '10.0.26300'; powerPlan = 'High performance'
                acPowerOnline = $true; dockerVersion = '29.0.0'; dockerVmCpus = 8; dockerVmMemoryBytes = 16000000000
                databaseCpuLimit = [decimal]8; databaseMemoryLimitBytes = 16000000000; storageMountType = 'volume'
                competingHarnessProcess = $false; otherProviderContainerRunning = $false
            }
        }
    }

    Context 'Setup failures return 1 and run no side' {
        It 'Given the host lock is held' {
            Mock -ModuleName BenchmarkComparison Open-BenchmarkHostLock { throw 'Another benchmark comparison holds the host lock.' }

            $result = Invoke-Comparison -Outcome
            $result.ExitCode | Should -Be 1
            # No directory of its own, so the entry script leaves its transcript where it is.
            $result.InvocationDirectory | Should -BeNullOrEmpty
            $script:DotnetCalls | Should -BeNullOrEmpty
        }

        It 'Given a dirty harness source' {
            $script:DirtySource = $true

            Invoke-Comparison | Should -Be 1
            $script:DotnetCalls | Should -BeNullOrEmpty
        }

        It 'Given a commit that does not resolve to 40-hex' {
            Invoke-Comparison @{ Purpose = 'Candidate'; CandidateCommit = 'no-such-branch' } | Should -Be 1
            $script:DotnetCalls | Should -BeNullOrEmpty
        }

        It 'Given an unapproved baseline' {
            Invoke-Comparison @{ Purpose = 'Candidate'; BaselineCommit = $script:Other; CandidateCommit = $script:Other } | Should -Be 1
            $script:DotnetCalls | Should -BeNullOrEmpty
        }

        It 'Given a pre-floor commit' {
            $script:PreFloor = @($script:Other)

            Invoke-Comparison @{ Purpose = 'Candidate'; CandidateCommit = $script:Other } | Should -Be 1
            $script:DotnetCalls | Should -BeNullOrEmpty
        }

        It 'Given a build failure' {
            $script:BuildExit = 1

            Invoke-Comparison | Should -Be 1
            Get-SideCall | Should -BeNullOrEmpty
            @($script:DotnetCalls).Count | Should -Be 1
        }

        It 'Given a digest mismatch' {
            $script:ImageDigest = 'sha256:' + ('f' * 64)

            Invoke-Comparison | Should -Be 1
            Get-SideCall | Should -BeNullOrEmpty
        }

        It 'Given a missing connection-string template' {
            $saved = $env:ConnectionStrings__DatabaseConnection
            $env:ConnectionStrings__DatabaseConnection = ''
            try {
                Invoke-Comparison | Should -Be 1
            }
            finally {
                $env:ConnectionStrings__DatabaseConnection = $saved
            }
            $script:DotnetCalls | Should -BeNullOrEmpty
        }

        It 'Given an existing non-empty invocation directory' {
            $existing = Join-Path $script:Results "postgresql-Calibration-20261123T120000Z-$($script:Reference.Substring(0, 8))"
            New-Item -ItemType Directory -Force -Path $existing | Out-Null
            Set-Content -LiteralPath (Join-Path $existing 'run-plan.json') -Value '{}'

            Invoke-Comparison | Should -Be 1
            $script:DotnetCalls | Should -BeNullOrEmpty
        }
    }

    Context 'Purpose rules' {
        It 'Given <Purpose> with a candidate other than the approved reference' -ForEach @(
            @{ Purpose = 'Calibration' }, @{ Purpose = 'Sizing' }, @{ Purpose = 'Repeatability' },
            @{ Purpose = 'Detection' }, @{ Purpose = 'BoundaryProbe' }
        ) {
            Invoke-Comparison @{ Purpose = $Purpose; CandidateCommit = $script:Other; Pairs = 5; InjectSlowdown = $Purpose -eq 'Sizing' ? "$script:Write=0.2" : '' } |
                Should -Be 1
            $script:DotnetCalls | Should -BeNullOrEmpty
        }

        It 'Given <Purpose> with injection' -ForEach @(@{ Purpose = 'Calibration' }, @{ Purpose = 'Repeatability' }) {
            Invoke-Comparison @{ Purpose = $Purpose; Pairs = 5; InjectSlowdown = "$script:Write=0.2" } | Should -Be 1
            $script:DotnetCalls | Should -BeNullOrEmpty
        }

        It 'Given <Purpose> with -Pairs 4' -ForEach @(@{ Purpose = 'Repeatability' }, @{ Purpose = 'Detection' }) {
            $script:BenchmarkProfile.detection.injectionFactors.postgresql.$script:Write = 0.3
            $script:BenchmarkProfile.detection.injectionFactors.postgresql.$script:Read = 0.25

            Invoke-Comparison @{ Purpose = $Purpose; Pairs = 4 } | Should -Be 1
            $script:DotnetCalls | Should -BeNullOrEmpty
        }

        It 'Given Sizing without -InjectSlowdown' {
            Invoke-Comparison @{ Purpose = 'Sizing' } | Should -Be 1
            $script:DotnetCalls | Should -BeNullOrEmpty
        }

        It 'Given Detection with null factors in the profile' {
            Invoke-Comparison @{ Purpose = 'Detection'; Pairs = 5 } | Should -Be 1
            $script:DotnetCalls | Should -BeNullOrEmpty
        }

        It 'Given <Purpose> with a hand-supplied -InjectSlowdown' -ForEach @(@{ Purpose = 'Detection' }, @{ Purpose = 'BoundaryProbe' }) {
            $script:BenchmarkProfile.detection.injectionFactors.postgresql.$script:Write = 0.3
            $script:BenchmarkProfile.detection.injectionFactors.postgresql.$script:Read = 0.25

            Invoke-Comparison @{ Purpose = $Purpose; Pairs = 5; InjectSlowdown = "$script:Write=0.3" } | Should -Be 1
            $script:DotnetCalls | Should -BeNullOrEmpty
        }

        It 'Given Detection, the factors come from the frozen profile and reach candidate sides only' {
            $script:BenchmarkProfile.detection.injectionFactors.postgresql.$script:Write = 0.3
            $script:BenchmarkProfile.detection.injectionFactors.postgresql.$script:Read = 0.25

            Invoke-Comparison @{ Purpose = 'Detection'; Pairs = 5 } | Should -Be 0

            $plan = Get-RunPlan
            $plan.injection.$script:Write | Should -Be 0.3
            $plan.injection.$script:Read | Should -Be 0.25
            $candidate = @(Get-SideCall | Where-Object { $_.Environment['PERF_BENCHMARK_ROLE'] -eq 'candidate' })
            $candidate.Count | Should -Be 5
            $candidate | ForEach-Object { $_.Environment['PERF_INJECT_SLOWDOWN'] | Should -Be "$script:Write=0.3;$script:Read=0.25" }
        }

        It 'Given BoundaryProbe, factor x threshold / target is recorded' {
            $script:BenchmarkProfile.detection.injectionFactors.postgresql.$script:Write = 0.3
            $script:BenchmarkProfile.detection.injectionFactors.postgresql.$script:Read = 0.25

            Invoke-Comparison @{ Purpose = 'BoundaryProbe'; Pairs = 2 } | Should -Be 0

            $plan = Get-RunPlan
            # 0.3 x 0.10 / 0.20 and 0.25 x 0.10 / 0.20
            $plan.injection.$script:Write | Should -Be 0.15
            $plan.injection.$script:Read | Should -Be 0.125
        }

        It 'Given a smoke Detection, the smokeWorkload factors drive both scenarios' {
            Invoke-Comparison @{ Purpose = 'Detection'; Pairs = 5; Smoke = $true } | Should -Be 0

            $plan = Get-RunPlan
            $plan.smoke | Should -BeTrue
            $plan.injection.$script:Write | Should -Be 1.0
            $plan.injection.$script:Read | Should -Be 1.0
            Get-SideCall | ForEach-Object { $_.Environment['PERF_BENCHMARK_WORKLOAD'] | Should -Be 'smokeWorkload' }
        }
    }

    Context 'The run' {
        It 'Given three pairs, the plan alternates and injection reaches candidate sides only' {
            Invoke-Comparison @{ Purpose = 'Candidate'; CandidateCommit = $script:Other; Pairs = 3; InjectSlowdown = "$script:Write=0.4" } |
                Should -Be 0

            $plan = Get-RunPlan
            @($plan.order | ForEach-Object { "$($_.pair)$($_.role[0])$($_.position)" }) |
                Should -Be @('1b1', '1c2', '2c1', '2b2', '3b1', '3c2')
            $plan.runId | Should -Match '^run-[0-9a-f]{32}$'
            $plan.harnessCommit | Should -Be $script:Harness
            $plan.profileSha256 | Should -Be ('a' * 64)

            $sides = Get-SideCall
            @($sides | ForEach-Object { "$($_.Environment['PERF_BENCHMARK_PAIR'])$($_.Environment['PERF_BENCHMARK_ROLE'][0])" }) |
                Should -Be @('1b', '1c', '2c', '2b', '3b', '3c')
            foreach ($side in $sides) {
                $candidate = $side.Environment['PERF_BENCHMARK_ROLE'] -eq 'candidate'
                $side.Environment['PERF_INJECT_SLOWDOWN'] | Should -Be ($candidate ? "$script:Write=0.4" : '')
                $side.Environment['PERF_BENCHMARK_RUN_ID'] | Should -Be $plan.runId
                $side.Arguments | Should -Match ([regex]::Escape(($candidate ? $script:Other : $script:Reference).Substring(0, 12)))
                $side.Environment['ConnectionStrings__DatabaseConnection'] -split ';' | Should -Contain 'host=localhost'
                $side.Environment['ConnectionStrings__DatabaseConnection'] -split ';' | Should -Contain 'port=5432'
            }
            @($script:DotnetCalls | Where-Object { $_.Arguments -like 'build*' }).Count | Should -Be 2
        }

        It 'Given relative -ResultsDirectory and -WorktreeRoot under a location other than the process directory, both resolve against that location' {
            # Set-Location never moves the process directory, which [IO.File] and git -C use, so
            # a relative path used to reach the run-plan write unresolved and fail after the builds.
            $location = New-Item -ItemType Directory -Path (Join-Path $TestDrive "location-$([guid]::NewGuid().ToString('N'))")
            Push-Location -LiteralPath $location.FullName
            try {
                # As PowerShell spells it (TEMP may be an 8.3 path).
                $script:Location = $PWD.ProviderPath
                [Environment]::CurrentDirectory | Should -Not -Be $script:Location
                $result = Invoke-Comparison -Outcome @{ ResultsDirectory = 'relative-results'; WorktreeRoot = 'relative-worktrees' }
            }
            finally {
                Pop-Location
            }

            $result.ExitCode | Should -Be 0
            $result.InvocationDirectory | Should -BeLike (Join-Path -Path $script:Location -ChildPath 'relative-results' -AdditionalChildPath '*')
            Join-Path $result.InvocationDirectory 'run-plan.json' | Should -Exist
            Should -Invoke -ModuleName BenchmarkComparison Invoke-BenchmarkGit -Times 1 -Exactly -ParameterFilter {
                $ArgumentList[0] -eq 'worktree' -and
                $ArgumentList[3] -eq (Join-Path -Path $script:Location -ChildPath 'relative-worktrees' -AdditionalChildPath $script:Reference.Substring(0, 12))
            }
        }

        It 'Given a side process that fails, the loop stops and the comparison still decides (2)' {
            $script:SideExits = @{ 2 = 1 }
            $script:ComparisonResult = 'invalid'
            $script:ComparisonExit = 2

            Invoke-Comparison @{ Pairs = 3 } | Should -Be 2
            @(Get-SideCall).Count | Should -Be 2
            $script:DotnetCalls[-1].Arguments | Should -Match 'Given_BenchmarkComparisonRun'
        }

        It 'Given the final side process fails after writing its evidence, its environment.json records the exit code' {
            # Two pairs run 1b, 1c, 2c, 2b: the fourth side is the last one, so no later side
            # goes missing and only the recorded exit code lets the comparison see the failure.
            $script:SideExits = @{ 4 = 1 }

            Invoke-Comparison | Out-Null
            @(Get-SideCall).Count | Should -Be 4

            $recorded = @{}
            foreach ($file in Get-ChildItem -Path $script:Results -Recurse -Filter 'environment.json') {
                $environment = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json
                $recorded["$($environment.pair)$($environment.role[0])"] = $environment.sideExitCode
            }
            $recorded['2b'] | Should -Be 1
            foreach ($side in @('1b', '1c', '2c')) { $recorded[$side] | Should -Be 0 }
        }

        It 'Given the comparison step fails' {
            $script:ReportExit = 1

            Invoke-Comparison | Should -Be 1
        }

        It 'Given comparison.json is missing' {
            $script:WriteComparisonJson = $false

            Invoke-Comparison | Should -Be 1
        }

        It 'Given a valid comparison with a suspected regression' {
            $script:ComparisonResult = 'suspected-regression'

            $result = Invoke-Comparison -Outcome
            @($result).Count | Should -Be 1
            $result.ExitCode | Should -Be 0
            $result.ExitCode | Should -BeOfType [int]
            $plan = Get-ChildItem -Path $script:Results -Recurse -Filter 'run-plan.json'
            $result.InvocationDirectory | Should -Be $plan.DirectoryName
        }

        It 'Given an invalid comparison' {
            $script:ComparisonResult = 'invalid'
            $script:ComparisonExit = 2

            Invoke-Comparison | Should -Be 2
        }

        It 'Given the collector never becomes ready, the side fails and the run returns 2' {
            $script:CollectorReady = $false
            $script:ComparisonResult = 'invalid'
            $script:ComparisonExit = 2

            Invoke-Comparison | Should -Be 2
            Get-SideCall | Should -BeNullOrEmpty
            $script:StopCalls.Count | Should -Be 1
            $script:DotnetCalls[-1].Arguments | Should -Match 'Given_BenchmarkComparisonRun'
        }

        It 'Given a retry after a partial failure, it gets its own directory and run id and leaves the first untouched' {
            $script:SideExits = @{ 1 = 1 }
            Invoke-Comparison | Should -Be 0
            $first = Get-ChildItem -LiteralPath $script:Results -Directory
            $firstFiles = @{}
            Get-ChildItem -LiteralPath $first.FullName -Recurse -File |
                ForEach-Object { $firstFiles[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName).Hash }
            $firstRunId = (Get-Content -Raw (Join-Path $first.FullName 'run-plan.json') | ConvertFrom-Json).runId

            $script:SideExits = @{}
            $script:DotnetCalls.Clear()
            Invoke-Comparison | Should -Be 0

            $second = Get-ChildItem -LiteralPath $script:Results -Directory | Where-Object FullName -NE $first.FullName
            @($second).Count | Should -Be 1
            (Get-Content -Raw (Join-Path $second.FullName 'run-plan.json') | ConvertFrom-Json).runId | Should -Not -Be $firstRunId
            $script:DotnetCalls[-1].Environment['PERF_BENCHMARK_DIR'] | Should -Be $second.FullName
            Get-SideCall | ForEach-Object { $_.Environment['PERF_RESULTS_DIR'] | Should -BeLike "$($second.FullName)*" }
            foreach ($file in $firstFiles.Keys) {
                (Get-FileHash -LiteralPath $file).Hash | Should -Be $firstFiles[$file]
            }
            @(Get-ChildItem -LiteralPath $first.FullName -Recurse -File).Count | Should -Be $firstFiles.Count
        }

        It 'Given the side writes environment.json with its phases' {
            Invoke-Comparison @{ Pairs = 1 } | Should -Be 0

            $environment = Get-ChildItem -LiteralPath $script:Results -Recurse -Filter 'environment.json' | Select-Object -First 1 |
                ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json }
            $environment.schemaVersion | Should -Be 'benchmark-environment 1.0.0'
            @($environment.phases.name) | Should -Be @('settle', 'idle', 'side')
            $environment.trailingSampleTimeout | Should -BeFalse
            $environment.storageMountType | Should -Be 'volume'
        }
    }

    Context 'Provider and purpose spelling' {
        # The report accepts only the canonical tokens, so the plan must never carry the
        # spelling the run was invoked with.
        It 'Given -Provider <Provider> -Purpose <Purpose>, the run uses <Expected>' -ForEach @(
            @{ Provider = 'pgsql'; Purpose = 'Calibration'; Expected = 'postgresql Calibration' }
            @{ Provider = 'PostgreSQL'; Purpose = 'calibration'; Expected = 'postgresql Calibration' }
            @{ Provider = 'PGSQL'; Purpose = 'CALIBRATION'; Expected = 'postgresql Calibration' }
        ) {
            Invoke-Comparison @{ Provider = $Provider; Purpose = $Purpose } | Should -Be 0

            $plan = Get-RunPlan
            "$($plan.provider) $($plan.purpose)" | Should -BeExactly $Expected
            (Get-ChildItem -LiteralPath $script:Results -Directory).Name | Should -BeLikeExactly 'postgresql-Calibration-*'
        }

        It 'Given the entry script, its Provider and Purpose accept exactly what the module accepts' {
            $script = Get-Command (Join-Path $PSScriptRoot '../invoke-benchmark-comparison.ps1')
            $module = Get-Command Invoke-BenchmarkComparison
            $accepted = { param($command, $name) @($command.Parameters[$name].Attributes | Where-Object { $_ -is [ValidateSet] }).ValidValues }
            foreach ($name in 'Provider', 'Purpose') {
                & $accepted $script $name | Should -Be (& $accepted $module $name)
            }
            & $accepted $script 'Provider' | Should -Contain 'pgsql'
        }
    }

    Context 'Trailing samples' {
        It 'Given the side exits before the next collector sample, the collector stops only once every metric has one' {
            Mock -ModuleName BenchmarkComparison Get-BenchmarkCollectorSample {
                $script:SampleCalls++
                # Host samples arrive right after the exit; Docker's only on the third poll.
                $late = $script:SampleCalls -ge 3 ? $script:Metrics : @('host-cpu-percent', 'host-available-mb')
                @(New-Sample -Stamp $script:SideExitAt.AddSeconds(-1)) + @(New-Sample -Stamp $script:SideExitAt.AddSeconds(0.2) -Metric $late)
            }

            Invoke-Comparison @{ Pairs = 1; Purpose = 'Calibration' } | Out-Null

            # Pair 1 baseline first: its three polls, then the stop.
            $script:StopCalls[0].SampleCalls | Should -Be 3
            $script:StopCalls.Count | Should -Be 2
        }

        It 'Given a trailing sample never arrives, the collector stops at the 10 s bound and the timeout is recorded' {
            Mock -ModuleName BenchmarkComparison Get-BenchmarkCollectorSample {
                $script:SampleCalls++
                @(New-Sample -Stamp $script:SideExitAt.AddSeconds(-1)) +
                @(New-Sample -Stamp $script:SideExitAt.AddSeconds(0.2) -Metric @('host-cpu-percent', 'host-available-mb'))
            }

            Invoke-Comparison @{ Pairs = 1; Purpose = 'Calibration' } | Out-Null

            $script:StopCalls.Count | Should -Be 2
            $waited = ($script:StopCalls[0].Clock - $script:DotnetCalls.Where({ $_.Arguments -match 'BenchmarkSideRun' })[0].Clock).TotalSeconds
            $waited | Should -BeGreaterOrEqual 10
            $waited | Should -BeLessThan 11
            $environment = Get-ChildItem -LiteralPath $script:Results -Recurse -Filter 'environment.json' |
                Where-Object FullName -Match 'baseline' | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json }
            $environment.trailingSampleTimeout | Should -BeTrue
            @($environment.missingTrailingMetrics) | Should -Be @('db-cpu-percent', 'db-memory-percent', 'db-block-io-bytes')
        }
    }
}

Describe 'invoke-benchmark-comparison.ps1' {
    # The committed entry script runs in a child pwsh beside a stub module, so only its own
    # transcript handling is under test.
    BeforeEach {
        $script:Entry = Join-Path $TestDrive "entry-$([guid]::NewGuid().ToString('N'))"
        $script:EntryResults = Join-Path $TestDrive "results-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:Entry | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../invoke-benchmark-comparison.ps1') -Destination $script:Entry
    }

    It 'Given another comparison holds the host lock and writes its plan meanwhile, the rejected transcript stays out of its directory' {
        # The stub is the rejected invocation: while it fails setup, the lock holder writes its
        # run plan, after this invocation's transcript started.
        Set-Content -LiteralPath (Join-Path $script:Entry 'BenchmarkComparison.psm1') -Value @'
function Invoke-BenchmarkComparison {
    param($ResultsDirectory)
    $other = New-Item -ItemType Directory -Force -Path (Join-Path $ResultsDirectory 'postgresql-Calibration-other')
    Set-Content -LiteralPath (Join-Path $other.FullName 'run-plan.json') -Value '{}'
    Write-Host 'Setup failed; no side ran: Another benchmark comparison holds the host lock.'
    [pscustomobject]@{ ExitCode = 1; InvocationDirectory = $null }
}
'@

        & (Get-Process -Id $PID).Path -NoProfile -NonInteractive -File (Join-Path $script:Entry 'invoke-benchmark-comparison.ps1') `
            -BaselineCommit ('a' * 40) -Provider postgresql -ResultsDirectory $script:EntryResults | Out-Null

        $LASTEXITCODE | Should -Be 1
        Join-Path $script:EntryResults 'postgresql-Calibration-other/wrapper.log' | Should -Not -Exist
        $transcripts = @(Get-ChildItem -LiteralPath $script:EntryResults -Filter 'wrapper-*.log')
        $transcripts.Count | Should -Be 1
        $transcripts[0].Name | Should -Match '^wrapper-\d{8}T\d{6}Z-[0-9a-f]{8}\.log$'
        Get-Content -Raw -LiteralPath $transcripts[0].FullName | Should -Match 'holds the host lock'
    }

    It 'Given -Provider pgsql and a run that named its directory, the transcript becomes its wrapper.log' {
        Set-Content -LiteralPath (Join-Path $script:Entry 'BenchmarkComparison.psm1') -Value @'
function Invoke-BenchmarkComparison {
    param($ResultsDirectory, $Provider)
    $own = New-Item -ItemType Directory -Force -Path (Join-Path $ResultsDirectory 'postgresql-Calibration-own')
    Write-Host "Ran provider $Provider"
    [pscustomobject]@{ ExitCode = 2; InvocationDirectory = $own.FullName }
}
'@

        & (Get-Process -Id $PID).Path -NoProfile -NonInteractive -File (Join-Path $script:Entry 'invoke-benchmark-comparison.ps1') `
            -BaselineCommit ('a' * 40) -Provider pgsql -ResultsDirectory $script:EntryResults | Out-Null

        $LASTEXITCODE | Should -Be 2
        $log = Join-Path $script:EntryResults 'postgresql-Calibration-own/wrapper.log'
        $log | Should -Exist
        Get-Content -Raw -LiteralPath $log | Should -Match 'Ran provider pgsql'
        Get-ChildItem -LiteralPath $script:EntryResults -Filter 'wrapper-*.log' | Should -BeNullOrEmpty
    }
}

Describe 'Get-BenchmarkStorageMountType' {
    It 'Given a named volume above the data path' {
        $mounts = '[{"Type":"volume","Destination":"/var/lib/postgresql"}]' | ConvertFrom-Json
        Get-BenchmarkStorageMountType -Mounts $mounts -Tmpfs $null -DataPath '/var/lib/postgresql/18/docker' | Should -Be 'volume'
    }

    It 'Given a bind mount at the data path' {
        $mounts = '[{"Type":"bind","Destination":"/var/opt/mssql"}]' | ConvertFrom-Json
        Get-BenchmarkStorageMountType -Mounts $mounts -Tmpfs $null -DataPath '/var/opt/mssql' | Should -Be 'bind'
    }

    It 'Given a tmpfs over the data path' {
        $tmpfs = '{"/var/lib/postgresql/data":""}' | ConvertFrom-Json
        Get-BenchmarkStorageMountType -Mounts @() -Tmpfs $tmpfs -DataPath '/var/lib/postgresql/data' | Should -Be 'tmpfs'
    }

    It 'Given no mount covering the data path' {
        $mounts = '[{"Type":"volume","Destination":"/backups"}]' | ConvertFrom-Json
        Get-BenchmarkStorageMountType -Mounts $mounts -Tmpfs $null -DataPath '/var/opt/mssql' | Should -Be 'container'
    }
}

Describe 'Get-BenchmarkDatabaseCpuLimit' {
    # Shaped like `docker inspect --format '{{json .HostConfig}}'` on an 8-CPU Docker VM.
    It 'Given <Name>, it records <Expected> CPUs' -ForEach @(
        @{ Name = 'an unrestricted container'; HostConfig = '{"NanoCpus":0,"CpuQuota":0,"CpuPeriod":0,"CpusetCpus":""}'; Expected = 8 }
        @{ Name = '--cpus 2'; HostConfig = '{"NanoCpus":2000000000,"CpuQuota":0,"CpuPeriod":0,"CpusetCpus":""}'; Expected = 2 }
        @{ Name = 'a 50 ms quota per 100 ms pinned to one CPU'; HostConfig = '{"NanoCpus":0,"CpuQuota":50000,"CpuPeriod":100000,"CpusetCpus":"0"}'; Expected = 0.5 }
        @{ Name = 'a quota with the default period'; HostConfig = '{"NanoCpus":0,"CpuQuota":300000,"CpuPeriod":0,"CpusetCpus":""}'; Expected = 3 }
        @{ Name = '--cpuset-cpus 0-3,6'; HostConfig = '{"NanoCpus":0,"CpuQuota":0,"CpuPeriod":0,"CpusetCpus":"0-3,6"}'; Expected = 5 }
        @{ Name = 'overlapping --cpuset-cpus 0-3,2-5'; HostConfig = '{"NanoCpus":0,"CpuQuota":0,"CpuPeriod":0,"CpusetCpus":"0-3,2-5"}'; Expected = 6 }
    ) {
        $config = $HostConfig | ConvertFrom-Json
        Get-BenchmarkDatabaseCpuLimit -HostConfig $config -VmCpus 8 | Should -Be ([decimal]$Expected)
    }
}

Describe 'Get-BenchmarkDatabaseCpuset' {
    It 'Given <CpusetCpus>, it records <Expected>' -ForEach @(
        @{ CpusetCpus = ''; Expected = '' }
        @{ CpusetCpus = '0-3'; Expected = '0,1,2,3' }
        @{ CpusetCpus = '3,2,1,0'; Expected = '0,1,2,3' }
        @{ CpusetCpus = '0-3,2-5'; Expected = '0,1,2,3,4,5' }
        @{ CpusetCpus = ' 10,8-9 '; Expected = '8,9,10' }
    ) {
        Get-BenchmarkDatabaseCpuset -CpusetCpus $CpusetCpus | Should -Be $Expected
    }

    It 'Given two pins of the same size on different CPUs, the limits match but the sets do not' {
        $first = '{"NanoCpus":0,"CpuQuota":0,"CpuPeriod":0,"CpusetCpus":"0-3"}' | ConvertFrom-Json
        $second = '{"NanoCpus":0,"CpuQuota":0,"CpuPeriod":0,"CpusetCpus":"4-7"}' | ConvertFrom-Json

        Get-BenchmarkDatabaseCpuLimit -HostConfig $second -VmCpus 8 | Should -Be (Get-BenchmarkDatabaseCpuLimit -HostConfig $first -VmCpus 8)
        Get-BenchmarkDatabaseCpuset -CpusetCpus $second.CpusetCpus | Should -Not -Be (Get-BenchmarkDatabaseCpuset -CpusetCpus $first.CpusetCpus)
    }
}
