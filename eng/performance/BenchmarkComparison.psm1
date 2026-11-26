# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    The DMS-1019 benchmark comparison wrapper: alternating baseline/candidate side runs of the
    performance harness, each sampled by a host/Docker collector, then the comparison report.

.DESCRIPTION
    Every external effect goes through a small function (git, docker, dotnet, the overlay copy,
    the collector, the clock, host facts and the host lock) so the orchestration can be tested
    with Pester mocks. Those are the only Windows-only calls; the module imports anywhere.

    Invoke-BenchmarkComparison returns { ExitCode; InvocationDirectory }. InvocationDirectory is
    the directory setup created, or $null when setup failed before creating it. ExitCode:
      1  setup failed (no side ran), or the comparison report could not be produced or read;
      otherwise the exitCode recorded in comparison.json (0 acceptable or suspected regression,
      2 invalid).
#>

Set-StrictMode -Version Latest

$script:OverlayPrefix = 'src/dms/tests/EdFi.DataManagementService.Performance.Harness'
$script:HarnessProject = "$script:OverlayPrefix/EdFi.DataManagementService.Performance.Harness.csproj"
$script:ProfilePath = "$script:OverlayPrefix/Configuration/benchmark-profile.json"
$script:ModulePath = 'eng/performance/BenchmarkComparison.psm1'
$script:ScriptPath = 'eng/performance/invoke-benchmark-comparison.ps1'
$script:Purposes = @('Candidate', 'Calibration', 'Sizing', 'Repeatability', 'Detection', 'BoundaryProbe')
$script:MinimumQualificationPairs = 5
$script:HostMetrics = @('host-cpu-percent', 'host-available-mb')
$script:DockerMetrics = @('db-cpu-percent', 'db-memory-percent', 'db-block-io-bytes')
$script:RequiredMetrics = $script:HostMetrics + $script:DockerMetrics
$script:CollectorReadyTimeoutSeconds = 30
$script:TrailingSampleTimeoutSeconds = 10
$script:Utf8NoBom = [Text.UTF8Encoding]::new($false)
$script:Invariant = [Globalization.CultureInfo]::InvariantCulture

# ------------------------------------------------------------------ external effects (mockable)

function Invoke-BenchmarkGit {
    param([Parameter(Mandatory)][string] $Repository, [Parameter(Mandatory)][string[]] $ArgumentList)
    $output = @(git -C $Repository @ArgumentList 2>&1 | ForEach-Object { "$_" })
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Invoke-BenchmarkDocker {
    param([Parameter(Mandatory)][string[]] $ArgumentList)
    $output = @(docker @ArgumentList 2>$null | ForEach-Object { "$_" })
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Invoke-BenchmarkDotnet {
    <#
    .SYNOPSIS
        Runs dotnet in a child process with the given variables (an empty value removes one),
        tees its output to LogPath, and returns the exit code.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Streams the dotnet output to the operator console; the function returns the exit code, so Write-Output would corrupt the pipeline result.')]
    param(
        [Parameter(Mandatory)][string[]] $ArgumentList,
        [hashtable] $Environment = @{},
        [Parameter(Mandatory)][string] $LogPath
    )
    $saved = @{}
    foreach ($name in $Environment.Keys) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, [string]$Environment[$name])
    }
    try {
        & dotnet @ArgumentList 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $LogPath | Write-Host
        return $LASTEXITCODE
    }
    finally {
        foreach ($name in $saved.Keys) {
            [Environment]::SetEnvironmentVariable($name, $saved[$name])
        }
    }
}

function Copy-HarnessOverlay {
    <#
    .SYNOPSIS
        Replaces the worktree's harness directory with the source one, so stale files from a
        reused worktree cannot survive. The target must sit inside the worktree.
    #>
    param([Parameter(Mandatory)][string] $SourceRoot, [Parameter(Mandatory)][string] $Worktree)
    $relative = $script:OverlayPrefix -replace '/', [IO.Path]::DirectorySeparatorChar
    $source = Join-Path $SourceRoot $relative
    $worktreeRoot = (Resolve-Path -LiteralPath $Worktree).Path
    $target = Join-Path $worktreeRoot $relative
    if (-not $target.StartsWith($worktreeRoot + [IO.Path]::DirectorySeparatorChar)) {
        throw "Refusing to replace '$target': it is not inside the worktree '$worktreeRoot'."
    }
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
    robocopy $source $target /E /XD bin obj /NFL /NDL /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "Copying the harness overlay failed with robocopy exit code $LASTEXITCODE."
    }
}

function Get-BenchmarkUtcNow {
    return [DateTime]::UtcNow
}

function Wait-BenchmarkDelay {
    param([Parameter(Mandatory)][double] $Seconds)
    Start-Sleep -Milliseconds ([int]($Seconds * 1000))
}

function Open-BenchmarkHostLock {
    <#
    .SYNOPSIS
        Takes the exclusive host lock; throws when another comparison holds it. Exclusivity
        comes from FileShare.None, so the file is opened for reading only: a lock file created
        by an elevated session is read-only to ordinary users (ProgramData's inherited ACL),
        and a read-write open would deny every other account.
    #>
    $directory = Join-Path $env:ProgramData 'EdFi'
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $path = Join-Path $directory 'dms-benchmark.lock'
    try {
        return [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Read, [IO.FileShare]::None)
    }
    catch [UnauthorizedAccessException] {
        throw "Cannot open the host lock '$path' for reading: $($_.Exception.Message)"
    }
    catch [IO.IOException] {
        throw "Another benchmark comparison holds the host lock '$path'."
    }
}

function Get-BenchmarkHostFact {
    <#
    .SYNOPSIS
        The host, Docker and database-container facts environment.json records for one side.
    #>
    param(
        [Parameter(Mandatory)][string] $ContainerName,
        [Parameter(Mandatory)][string] $OtherContainerName,
        [Parameter(Mandatory)][string] $DataPath
    )
    $plan = (powercfg /getactivescheme) -join ' '
    $powerPlan = if ($plan -match '\(([^)]+)\)') { $Matches[1] } else { $plan.Trim() }
    $batteries = @(Get-CimInstance -ClassName Win32_Battery -ErrorAction SilentlyContinue)
    # BatteryStatus 2: on AC power. A machine without a battery is on AC by definition.
    $acOnline = $batteries.Count -eq 0 -or @($batteries | Where-Object BatteryStatus -EQ 2).Count -gt 0

    $info = (Invoke-BenchmarkDocker -ArgumentList @('info', '--format', '{{.NCPU}} {{.MemTotal}}')).Output -join ''
    $vmCpus, $vmMemory = $info.Trim() -split '\s+'
    $dockerVersion = ((Invoke-BenchmarkDocker -ArgumentList @('version', '--format', '{{.Server.Version}}')).Output -join '').Trim()
    $mounts = ((Invoke-BenchmarkDocker -ArgumentList @('inspect', $ContainerName, '--format', '{{json .Mounts}}')).Output -join '') | ConvertFrom-Json
    # HostConfig.Tmpfs is absent (not null) when no tmpfs is configured.
    $hostConfig = ((Invoke-BenchmarkDocker -ArgumentList @('inspect', $ContainerName, '--format', '{{json .HostConfig}}')).Output -join '') | ConvertFrom-Json
    $tmpfs = $hostConfig.PSObject.Properties['Tmpfs']?.Value
    $other = (Invoke-BenchmarkDocker -ArgumentList @('ps', '--filter', "name=^$OtherContainerName$", '--format', '{{.Names}}')).Output -join ''
    $competing = @(Get-CimInstance -ClassName Win32_Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessId -ne $PID -and "$($_.CommandLine)" -match 'Performance\.Harness' }).Count -gt 0

    return [ordered]@{
        osFamily                      = $IsWindows ? 'Windows' : ($IsLinux ? 'Linux' : 'macOS')
        architecture                  = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        osBuild                       = [Environment]::OSVersion.Version.ToString()
        powerPlan                     = $powerPlan
        acPowerOnline                 = $acOnline
        dockerVersion                 = $dockerVersion
        dockerVmCpus                  = [int]$vmCpus
        dockerVmMemoryBytes           = [long]$vmMemory
        databaseCpuLimit              = Get-BenchmarkDatabaseCpuLimit -HostConfig $hostConfig -VmCpus ([int]$vmCpus)
        # Unlimited (0) containers get the VM's allocation, which is what they can use.
        databaseMemoryLimitBytes      = [long]$hostConfig.Memory -gt 0 ? [long]$hostConfig.Memory : [long]$vmMemory
        storageMountType              = Get-BenchmarkStorageMountType -Mounts $mounts -Tmpfs $tmpfs -DataPath $DataPath
        competingHarnessProcess       = $competing
        otherProviderContainerRunning = -not [string]::IsNullOrWhiteSpace($other)
    }
}

function Start-BenchmarkCollector {
    <#
    .SYNOPSIS
        Starts the host samplers (Get-Counter every HostIntervalSeconds) and the Docker samplers
        (docker stats --no-stream) as thread jobs appending to SideDirectory/samples.csv. Each
        row carries the phase current when it was taken; a Docker row is stamped when its poll
        completes. Neither source keeps its nominal interval exactly: Get-Counter fires about
        every 1.01 s, and one docker poll takes 1-2 s, so a window starting in that slack would
        have no bracketing sample within one interval. Two host samplers therefore run offset by
        half an interval, and three Docker pollers start their polls on a fixed schedule offset
        by a third of an interval (a fixed schedule, so varying poll durations cannot make them
        drift together). Every measured window is then bracketed well within one nominal interval.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal helper invoked non-interactively; it only starts the side''s own sampler jobs, so -WhatIf/-Confirm semantics add no value.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive: ContainerName and HostIntervalSeconds are consumed inside the thread-job scriptblocks through $using:.')]
    param(
        [Parameter(Mandatory)][string] $SideDirectory,
        [Parameter(Mandatory)][string] $ContainerName,
        [Parameter(Mandatory)][double] $HostIntervalSeconds,
        [Parameter(Mandatory)][double] $DockerIntervalSeconds
    )
    $samplesPath = Join-Path $SideDirectory 'samples.csv'
    [IO.File]::WriteAllText($samplesPath, "timestampUtc,phase,metric,value`n", $script:Utf8NoBom)
    $shared = [hashtable]::Synchronized(@{ Phase = 'startup'; Errors = [Collections.ArrayList]::Synchronized([Collections.ArrayList]::new()) })

    $append = {
        param($shared, $path, [string[]] $rows)
        [Threading.Monitor]::Enter($shared.SyncRoot)
        try { [IO.File]::AppendAllText($path, ($rows -join "`n") + "`n") }
        finally { [Threading.Monitor]::Exit($shared.SyncRoot) }
    }

    $hostSampler = {
        param([double] $OffsetSeconds)
        Start-Sleep -Milliseconds ([int]($OffsetSeconds * 1000))
        $shared = $using:shared
        $append = [scriptblock]::Create($using:append)
        $path = $using:samplesPath
        $interval = [int]$using:HostIntervalSeconds
        $invariant = [Globalization.CultureInfo]::InvariantCulture
        try {
            Get-Counter -Counter '\Processor(_Total)\% Processor Time', '\Memory\Available MBytes' `
                -SampleInterval $interval -Continuous | ForEach-Object {
                $stamp = $_.Timestamp.ToUniversalTime().ToString('o')
                $phase = $shared.Phase
                $cpu = $_.CounterSamples | Where-Object Path -Like '*processor time'
                $memory = $_.CounterSamples | Where-Object Path -Like '*available mbytes'
                & $append $shared $path @(
                    "$stamp,$phase,host-cpu-percent,$($cpu.CookedValue.ToString($invariant))",
                    "$stamp,$phase,host-available-mb,$($memory.CookedValue.ToString($invariant))"
                )
            }
        }
        catch { [void]$shared.Errors.Add("host sampler: $_") }
    }
    $hostJobs = @(
        Start-ThreadJob -ScriptBlock $hostSampler -ArgumentList 0
        Start-ThreadJob -ScriptBlock $hostSampler -ArgumentList ($HostIntervalSeconds / 2)
    )

    $origin = [DateTime]::UtcNow
    $dockerPoller = {
        param([double] $OffsetSeconds)
        $shared = $using:shared
        $append = [scriptblock]::Create($using:append)
        $path = $using:samplesPath
        $container = $using:ContainerName
        $invariant = [Globalization.CultureInfo]::InvariantCulture
        $interval = [TimeSpan]::FromSeconds($using:DockerIntervalSeconds)
        $units = @{ 'B' = 1; 'kB' = 1e3; 'MB' = 1e6; 'GB' = 1e9; 'TB' = 1e12; 'KiB' = 1024; 'MiB' = 1048576; 'GiB' = 1073741824 }
        function ConvertTo-ByteCount([string] $text) {
            if ($text.Trim() -match '^([\d.]+)\s*([A-Za-z]+)$') { return [double]::Parse($Matches[1], $invariant) * $units[$Matches[2]] }
            return 0
        }
        $next = ([DateTime]$using:origin).AddSeconds($OffsetSeconds)
        while ($true) {
            $wait = $next - [DateTime]::UtcNow
            if ($wait -gt [TimeSpan]::Zero) { Start-Sleep -Milliseconds ([int]$wait.TotalMilliseconds) }
            try {
                $json = (docker stats --no-stream --format '{{json .}}' $container 2>$null) -join ''
                $stamp = [DateTime]::UtcNow.ToString('o')
                $phase = $shared.Phase
                $stats = $json | ConvertFrom-Json
                $blockIo = ($stats.BlockIO -split '/' | ForEach-Object { ConvertTo-ByteCount $_ } | Measure-Object -Sum).Sum
                & $append $shared $path @(
                    "$stamp,$phase,db-cpu-percent,$(([double]::Parse($stats.CPUPerc.TrimEnd('%'), $invariant)).ToString($invariant))",
                    "$stamp,$phase,db-memory-percent,$(([double]::Parse($stats.MemPerc.TrimEnd('%'), $invariant)).ToString($invariant))",
                    "$stamp,$phase,db-block-io-bytes,$($blockIo.ToString($invariant))"
                )
            }
            catch { [void]$shared.Errors.Add("docker sampler: $_") }
            # The next slot on this poller's schedule; a poll that overran skips missed slots.
            do { $next = $next.Add($interval) } while ($next -le [DateTime]::UtcNow)
        }
    }
    $dockerJobs = @(0..2 | ForEach-Object { Start-ThreadJob -ScriptBlock $dockerPoller -ArgumentList ($DockerIntervalSeconds * $_ / 3) })

    return [pscustomobject]@{ SamplesPath = $samplesPath; Shared = $shared; Jobs = $hostJobs + $dockerJobs }
}

function Set-BenchmarkCollectorPhase {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal helper invoked non-interactively; it only updates the in-memory phase label shared with the sampler jobs.')]
    param([Parameter(Mandatory)] $Collector, [Parameter(Mandatory)][string] $Phase)
    $Collector.Shared.Phase = $Phase
}

function Get-BenchmarkCollectorSample {
    <#
    .SYNOPSIS
        The samples flushed so far, skipping a partly written last row.
    #>
    param([Parameter(Mandatory)] $Collector)
    $samples = [Collections.Generic.List[object]]::new()
    [Threading.Monitor]::Enter($Collector.Shared.SyncRoot)
    try { $lines = [IO.File]::ReadAllLines($Collector.SamplesPath) }
    finally { [Threading.Monitor]::Exit($Collector.Shared.SyncRoot) }
    foreach ($line in ($lines | Select-Object -Skip 1)) {
        $fields = $line -split ','
        $stamp = [DateTime]::MinValue
        if ($fields.Count -eq 4 -and [DateTime]::TryParse($fields[0], $script:Invariant, [Globalization.DateTimeStyles]::AdjustToUniversal, [ref]$stamp)) {
            $samples.Add([pscustomobject]@{ TimestampUtc = $stamp; Phase = $fields[1]; Metric = $fields[2] })
        }
    }
    return $samples.ToArray()
}

function Stop-BenchmarkCollector {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal helper invoked non-interactively; it only stops the side''s own sampler jobs.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Benchmark tooling intentionally writes collector problems to the operator console.')]
    param([Parameter(Mandatory)] $Collector)
    $Collector.Jobs | Stop-Job -ErrorAction SilentlyContinue
    $Collector.Jobs | Remove-Job -Force -ErrorAction SilentlyContinue
    foreach ($problem in $Collector.Shared.Errors) {
        Write-Host "Collector: $problem"
    }
}

function Wait-BenchmarkCollectorReady {
    <#
    .SYNOPSIS
        True once both a host and a Docker sample have arrived (the Wait-DmsSamplerSetReady
        pattern); false at the timeout.
    #>
    param([Parameter(Mandatory)] $Collector, [int] $TimeoutSeconds = $script:CollectorReadyTimeoutSeconds)
    $deadline = (Get-BenchmarkUtcNow).AddSeconds($TimeoutSeconds)
    while ($true) {
        $metrics = @(Get-BenchmarkCollectorSample -Collector $Collector | ForEach-Object Metric)
        if (@($script:HostMetrics | Where-Object { $_ -in $metrics }).Count -gt 0 -and
            @($script:DockerMetrics | Where-Object { $_ -in $metrics }).Count -gt 0) {
            return $true
        }
        if ((Get-BenchmarkUtcNow) -ge $deadline) {
            return $false
        }
        Wait-BenchmarkDelay -Seconds 0.5
    }
}

# ------------------------------------------------------------------ pure helpers

function Wait-BenchmarkCollectorTrailingSample {
    <#
    .SYNOPSIS
        Waits until every required metric has a sample stamped at or after the side's exit, so
        the last measured window has its end-bracketing sample, bounded at TimeoutSeconds
        (dms-1556-samplers.psm1 Stop-DmsSamplerSet guidance). Returns whether it timed out and
        the metrics still missing; the caller stops the collector either way.
    #>
    param(
        [Parameter(Mandatory)] $Collector,
        [Parameter(Mandatory)][DateTime] $SideExitUtc,
        [int] $TimeoutSeconds = $script:TrailingSampleTimeoutSeconds
    )
    $deadline = $SideExitUtc.AddSeconds($TimeoutSeconds)
    while ($true) {
        $samples = @(Get-BenchmarkCollectorSample -Collector $Collector)
        $missing = @($script:RequiredMetrics | Where-Object {
                $metric = $_
                @($samples | Where-Object { $_.Metric -eq $metric -and $_.TimestampUtc -ge $SideExitUtc }).Count -eq 0
            })
        if ($missing.Count -eq 0) {
            return [pscustomobject]@{ TimedOut = $false; Missing = @() }
        }
        if ((Get-BenchmarkUtcNow) -ge $deadline) {
            return [pscustomobject]@{ TimedOut = $true; Missing = $missing }
        }
        Wait-BenchmarkDelay -Seconds 0.5
    }
}

function Get-BenchmarkStorageMountType {
    <#
    .SYNOPSIS
        The mount type backing the database data path: tmpfs if any tmpfs covers it, otherwise
        the type of the deepest mount at or above it, or 'container' when none does.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive: DataPath is consumed inside the $covers scriptblock.')]
    param($Mounts, $Tmpfs, [Parameter(Mandatory)][string] $DataPath)
    $covers = { param([string] $destination) $DataPath -eq $destination -or $DataPath.StartsWith($destination.TrimEnd('/') + '/') -or $destination.StartsWith($DataPath.TrimEnd('/') + '/') }
    if ($null -ne $Tmpfs) {
        foreach ($name in $Tmpfs.PSObject.Properties.Name) {
            if (& $covers $name) { return 'tmpfs' }
        }
    }
    $matching = @(@($Mounts) | Where-Object { $null -ne $_ -and (& $covers $_.Destination) } |
            Sort-Object { $_.Destination.Length } -Descending)
    if ($matching.Count -eq 0) { return 'container' }
    if (@($matching | Where-Object Type -EQ 'tmpfs').Count -gt 0) { return 'tmpfs' }
    return $matching[0].Type
}

function Get-BenchmarkDatabaseCpuLimit {
    <#
    .SYNOPSIS
        The CPUs the database container can use: the smallest of its --cpus (NanoCpus), its CFS
        quota (CpuQuota / CpuPeriod), the CPUs its --cpuset-cpus list allows, and the VM's CPUs.
        Docker sets these independently, so reading only NanoCpus would record a quota-capped or
        pinned container as unrestricted. A CpuPeriod of 0 is Docker's default of 100000 us.
    #>
    param([Parameter(Mandatory)] $HostConfig, [Parameter(Mandatory)][int] $VmCpus)
    $limits = [Collections.Generic.List[decimal]]::new()
    $limits.Add([decimal]$VmCpus)
    if ([long]$HostConfig.NanoCpus -gt 0) {
        $limits.Add([decimal]$HostConfig.NanoCpus / 1000000000)
    }
    if ([long]$HostConfig.CpuQuota -gt 0) {
        $period = [long]$HostConfig.CpuPeriod -gt 0 ? [long]$HostConfig.CpuPeriod : 100000
        $limits.Add([decimal]$HostConfig.CpuQuota / $period)
    }
    if (-not [string]::IsNullOrWhiteSpace($HostConfig.CpusetCpus)) {
        $count = 0
        foreach ($range in $HostConfig.CpusetCpus.Split(',')) {
            $first, $last = $range.Trim().Split('-')
            $count += $null -eq $last ? 1 : [int]$last - [int]$first + 1
        }
        $limits.Add([decimal]$count)
    }
    return ($limits | Sort-Object)[0]
}

function Read-BenchmarkProfile {
    param([Parameter(Mandatory)][string] $Path)
    $bytes = [IO.File]::ReadAllBytes($Path)
    return [pscustomobject]@{
        Profile = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        Sha256  = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    }
}

function ConvertFrom-BenchmarkInjection {
    <#
    .SYNOPSIS
        Parses '<scenarioId>=<factor>;...' into an ordinal-sorted dictionary of decimals.
    #>
    param([string] $Text)
    $factors = [Collections.Generic.SortedDictionary[string, decimal]]::new([StringComparer]::Ordinal)
    if ([string]::IsNullOrWhiteSpace($Text)) { return , $factors }
    foreach ($entry in ($Text -split ';' | ForEach-Object Trim | Where-Object { $_ })) {
        $parts = $entry -split '=' | ForEach-Object Trim
        $factor = [decimal]0
        if ($parts.Count -ne 2 -or -not [decimal]::TryParse($parts[1], [Globalization.NumberStyles]::AllowDecimalPoint, $script:Invariant, [ref]$factor) -or $factor -le 0 -or $factor -gt 5) {
            throw "-InjectSlowdown entry '$entry' must be <scenarioId>=<factor> with a factor in (0, 5]."
        }
        if ($factors.ContainsKey($parts[0])) { throw "-InjectSlowdown lists '$($parts[0])' twice." }
        $factors[$parts[0]] = $factor
    }
    return , $factors
}

function ConvertTo-BenchmarkInjectionText {
    param($Factors)
    return ($Factors.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value.ToString($script:Invariant))" }) -join ';'
}

function Get-BenchmarkProfileFactor {
    <#
    .SYNOPSIS
        The Detection/BoundaryProbe factors: the frozen detection.injectionFactors (or
        smokeWorkload.injectionFactors under -Smoke), scaled for BoundaryProbe by
        threshold / target. Throws when any demonstration scenario's factor is null.
    #>
    param([Parameter(Mandatory)] $BenchmarkProfile, [Parameter(Mandatory)][string] $Provider, [Parameter(Mandatory)][string] $Purpose, [switch] $Smoke)
    $source = $Smoke ? $BenchmarkProfile.smokeWorkload.injectionFactors : $BenchmarkProfile.detection.injectionFactors.$Provider
    $factors = [Collections.Generic.SortedDictionary[string, decimal]]::new([StringComparer]::Ordinal)
    foreach ($scenario in $BenchmarkProfile.detection.scenarios) {
        $value = $null -eq $source ? $null : $source.$scenario
        if ($null -eq $value) {
            throw "Purpose $Purpose needs a $($Smoke ? 'smokeWorkload' : 'frozen detection') injection factor for '$scenario' ($Provider); it is null."
        }
        $factor = [decimal]$value
        if ($Purpose -eq 'BoundaryProbe') {
            $threshold = [decimal]$BenchmarkProfile.comparison.p50Thresholds.$Provider.$scenario
            $factor = [Math]::Round($factor * $threshold / [decimal]$BenchmarkProfile.detection.targetIncrease, 4, [MidpointRounding]::AwayFromZero)
            if ($factor -le 0 -or $factor -gt 5) {
                throw "The boundary-probe factor $factor for '$scenario' is outside (0, 5]."
            }
        }
        $factors[$scenario] = $factor
    }
    return , $factors
}

function Assert-BenchmarkPurpose {
    <#
    .SYNOPSIS
        The purpose rules. Throws with the first violated rule.
    #>
    param(
        [Parameter(Mandatory)][string] $Purpose,
        [Parameter(Mandatory)][string] $BaselineCommit,
        [Parameter(Mandatory)][string] $CandidateCommit,
        [Parameter(Mandatory)][string] $ApprovedReferenceCommit,
        [Parameter(Mandatory)][int] $Pairs,
        [bool] $InjectionGiven
    )
    if ($Purpose -ne 'Candidate' -and ($BaselineCommit -ne $ApprovedReferenceCommit -or $CandidateCommit -ne $ApprovedReferenceCommit)) {
        throw "Purpose $Purpose requires baseline = candidate = the approved reference $ApprovedReferenceCommit."
    }
    if ($Purpose -in @('Calibration', 'Repeatability') -and $InjectionGiven) {
        throw "Purpose $Purpose injects nothing; remove -InjectSlowdown."
    }
    if ($Purpose -in @('Detection', 'BoundaryProbe') -and $InjectionGiven) {
        throw "Purpose $Purpose takes its factors from the profile; remove -InjectSlowdown."
    }
    if ($Purpose -in @('Repeatability', 'Detection') -and $Pairs -lt $script:MinimumQualificationPairs) {
        throw "Purpose $Purpose needs at least $($script:MinimumQualificationPairs) pairs; got $Pairs."
    }
    if ($Purpose -eq 'Sizing' -and -not $InjectionGiven) {
        throw 'Purpose Sizing requires -InjectSlowdown.'
    }
}

function Resolve-BenchmarkCommit {
    param([Parameter(Mandatory)][string] $Repository, [Parameter(Mandatory)][string] $Commit)
    $result = Invoke-BenchmarkGit -Repository $Repository -ArgumentList @('rev-parse', '--verify', "$Commit^{commit}")
    $resolved = ($result.Output -join '').Trim()
    if ($result.ExitCode -ne 0 -or $resolved -notmatch '^[0-9a-f]{40}$') {
        throw "Cannot resolve '$Commit' to a 40-hex commit."
    }
    return $resolved
}

function Assert-BenchmarkCleanSource {
    param([Parameter(Mandatory)][string] $SourceRoot)
    $result = Invoke-BenchmarkGit -Repository $SourceRoot -ArgumentList @('status', '--porcelain', '--untracked-files=all', '--', $script:OverlayPrefix, $script:ModulePath, $script:ScriptPath)
    if ($result.ExitCode -ne 0) { throw 'git status failed on the source repository.' }
    $dirty = @($result.Output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Substring(3).Trim() })
    if ($dirty.Count -gt 0) {
        throw "The harness, profile, module or script is dirty or untracked; commit it first so the recorded harness commit matches what runs: $($dirty -join ', ')"
    }
}

function Assert-BenchmarkCleanOverlay {
    param([Parameter(Mandatory)][string] $Worktree)
    $result = Invoke-BenchmarkGit -Repository $Worktree -ArgumentList @('status', '--porcelain')
    if ($result.ExitCode -ne 0) { throw "git status failed in '$Worktree'." }
    $outside = @($result.Output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
            ($_.Substring(3).Trim() -replace '\\', '/').TrimEnd('/')
        } | Where-Object { -not ($_ -eq $script:OverlayPrefix -or $_.StartsWith($script:OverlayPrefix + '/')) })
    if ($outside.Count -gt 0) {
        throw "The worktree '$Worktree' is dirty outside the harness overlay: $($outside -join ', ')"
    }
}

function Resolve-BenchmarkContainerIdentity {
    param([Parameter(Mandatory)][string] $ContainerName)
    $tag = ((Invoke-BenchmarkDocker -ArgumentList @('inspect', $ContainerName, '--format', '{{.Config.Image}}')).Output -join '').Trim()
    $imageId = ((Invoke-BenchmarkDocker -ArgumentList @('inspect', $ContainerName, '--format', '{{.Image}}')).Output -join '').Trim()
    if (-not $tag -or -not $imageId) {
        throw "Cannot resolve the image of container '$ContainerName'. Is it running?"
    }
    $repoDigest = ((Invoke-BenchmarkDocker -ArgumentList @('image', 'inspect', $imageId, '--format', '{{index .RepoDigests 0}}')).Output -join '').Trim()
    if (-not $repoDigest -or $repoDigest -eq '<no value>' -or -not $repoDigest.Contains('@')) {
        throw "Cannot resolve a registry digest for container '$ContainerName'; refusing to run without a recorded digest."
    }
    return [pscustomobject]@{ Tag = $tag.Split('@')[0]; Digest = $repoDigest.Split('@')[1] }
}

# Copied from invoke-traditional-baseline.ps1 (the two existing wrappers already duplicate it).
function Resolve-ContainerEndpointFromPortBindingJson {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $PortBindingJson,
        [Parameter(Mandatory = $true)][string] $ContainerPort,
        [Parameter(Mandatory = $true)][string] $ContainerName
    )
    if ([string]::IsNullOrWhiteSpace($PortBindingJson) -or $PortBindingJson.Trim() -eq 'null') {
        throw "Container '$ContainerName' reports no port bindings; cannot resolve its published $ContainerPort endpoint."
    }
    $portMap = $PortBindingJson | ConvertFrom-Json
    $portProperty = $portMap.PSObject.Properties[$ContainerPort]
    if ($null -eq $portProperty -or $null -eq $portProperty.Value -or @($portProperty.Value).Count -eq 0) {
        throw "Container '$ContainerName' does not publish $ContainerPort to the host; refusing to run against an unreachable pinned container."
    }
    $binding = @($portProperty.Value)[0]
    $bindingHost = ([string]$binding.HostIp).Trim()
    # Wildcard and blank bindings are reachable on the loopback name; anything else (for
    # example an explicit 127.0.0.1 bind) is used verbatim.
    if ($bindingHost -eq '' -or $bindingHost -eq '0.0.0.0' -or $bindingHost -eq '::') {
        $bindingHost = 'localhost'
    }
    $portValue = 0
    if (-not [int]::TryParse(([string]$binding.HostPort).Trim(), [ref] $portValue) -or $portValue -lt 1 -or $portValue -gt 65535) {
        throw "Container '$ContainerName' publishes $ContainerPort with an unusable host port '$($binding.HostPort)'."
    }
    return [pscustomobject]@{ Host = $bindingHost; Port = $portValue }
}

# Copied from invoke-traditional-baseline.ps1.
function ConvertTo-PostgresEndpointPinnedConnectionString {
    param(
        [Parameter(Mandatory = $true)][string] $ConnectionString,
        [Parameter(Mandatory = $true)][string] $EndpointHost,
        [Parameter(Mandatory = $true)][int] $EndpointPort
    )
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    try {
        # PSBase reaches the real ConnectionString property: PowerShell adapts this type
        # through its type descriptor, which exposes keywords as properties instead.
        $builder.PSBase.ConnectionString = $ConnectionString
    }
    catch {
        throw "The PostgreSQL connection-string template could not be parsed: $($_.Exception.Message)"
    }
    # Npgsql accepts Server as an alias of Host; every endpoint synonym must go so the
    # template cannot override the pinned endpoint.
    foreach ($endpointKey in @('host', 'server', 'port')) {
        [void]$builder.Remove($endpointKey)
    }
    $builder['host'] = $EndpointHost
    $builder['port'] = [string]$EndpointPort
    return $builder.PSBase.ConnectionString
}

# Copied from invoke-traditional-baseline.ps1.
function ConvertTo-MssqlEndpointPinnedConnectionString {
    param(
        [Parameter(Mandatory = $true)][string] $ConnectionString,
        [Parameter(Mandatory = $true)][string] $EndpointHost,
        [Parameter(Mandatory = $true)][int] $EndpointPort
    )
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    try {
        # PSBase reaches the real ConnectionString property: PowerShell adapts this type
        # through its type descriptor, which exposes keywords as properties instead.
        $builder.PSBase.ConnectionString = $ConnectionString
    }
    catch {
        throw "The SQL Server connection-string template could not be parsed: $($_.Exception.Message)"
    }
    # SqlClient's Data Source synonyms; every one must go so the template cannot override
    # the pinned endpoint.
    foreach ($endpointKey in @('data source', 'server', 'address', 'addr', 'network address')) {
        [void]$builder.Remove($endpointKey)
    }
    $builder['data source'] = "$EndpointHost,$EndpointPort"
    return $builder.PSBase.ConnectionString
}

function Write-BenchmarkJson {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)] $Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 8) + "`n", $script:Utf8NoBom)
}

function Write-BenchmarkEnvironment {
    param(
        [Parameter(Mandatory)][string] $SideDirectory,
        [Parameter(Mandatory)][string] $RunId,
        [Parameter(Mandatory)][int] $Pair,
        [Parameter(Mandatory)][string] $Role,
        [Parameter(Mandatory)] $Facts,
        [object[]] $Phases = @(),
        [bool] $TrailingSampleTimeout = $false,
        [string[]] $MissingTrailingMetrics = @(),
        # The side process's exit code; null until the process has exited.
        [Nullable[int]] $SideExitCode = $null
    )
    $environment = [ordered]@{
        schemaVersion = 'benchmark-environment 1.0.0'
        runId         = $RunId
        pair          = $Pair
        role          = $Role
    }
    foreach ($key in $Facts.Keys) { $environment[$key] = $Facts[$key] }
    $environment.phases = @($Phases)
    $environment.trailingSampleTimeout = $TrailingSampleTimeout
    $environment.missingTrailingMetrics = @($MissingTrailingMetrics)
    $environment.sideExitCode = $SideExitCode
    Write-BenchmarkJson -Path (Join-Path $SideDirectory 'environment.json') -Value $environment
}

function New-BenchmarkPhase {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Builds an in-memory phase record only; no system state changes.')]
    param([string] $Name, [DateTime] $Start, [DateTime] $End)
    return [ordered]@{ name = $Name; startUtc = $Start.ToString('o'); endUtc = $End.ToString('o') }
}

# ------------------------------------------------------------------ the comparison

function Invoke-BenchmarkSide {
    <#
    .SYNOPSIS
        One side: collector, readiness, environment.json, settle and idle, the side process,
        the trailing samples, then the final environment.json. Returns whether it succeeded.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Benchmark tooling intentionally writes operator progress to the console; the function returns whether the side succeeded, so Write-Output would corrupt the pipeline result.')]
    param([Parameter(Mandatory)] $Context, [Parameter(Mandatory)] $Side)
    $sideDirectory = Join-Path $Context.InvocationDirectory ('pairs/{0:D2}/{1}' -f $Side.pair, $Side.role)
    New-Item -ItemType Directory -Force -Path $sideDirectory | Out-Null
    $rules = $Context.Profile.environment
    Write-Host "Pair $($Side.pair) $($Side.role) (position $($Side.position)) -> $sideDirectory"

    $collector = Start-BenchmarkCollector -SideDirectory $sideDirectory -ContainerName $Context.ContainerName `
        -HostIntervalSeconds $rules.hostSampleIntervalSeconds -DockerIntervalSeconds $rules.dockerSampleIntervalSeconds
    $stopped = $false
    try {
        if (-not (Wait-BenchmarkCollectorReady -Collector $collector)) {
            Write-Host "Pair $($Side.pair) $($Side.role): the collector produced no host and Docker samples; the side failed."
            return $false
        }

        $facts = Get-BenchmarkHostFact -ContainerName $Context.ContainerName -OtherContainerName $Context.OtherContainerName -DataPath $Context.DataPath
        Write-BenchmarkEnvironment -SideDirectory $sideDirectory -RunId $Context.RunId -Pair $Side.pair -Role $Side.role -Facts $facts

        $settleStart = Get-BenchmarkUtcNow
        Set-BenchmarkCollectorPhase -Collector $collector -Phase 'settle'
        Wait-BenchmarkDelay -Seconds $rules.settleSeconds
        $idleStart = Get-BenchmarkUtcNow
        Set-BenchmarkCollectorPhase -Collector $collector -Phase 'idle'
        Wait-BenchmarkDelay -Seconds $rules.idleSeconds
        $sideStart = Get-BenchmarkUtcNow
        Set-BenchmarkCollectorPhase -Collector $collector -Phase 'side'

        $environment = @{} + $Context.SideEnvironment
        $environment['PERF_RESULTS_DIR'] = (Resolve-Path -LiteralPath $sideDirectory).Path
        $environment['PERF_BENCHMARK_ROLE'] = $Side.role
        $environment['PERF_BENCHMARK_PAIR'] = "$($Side.pair)"
        $environment['PERF_BENCHMARK_POSITION'] = "$($Side.position)"
        $environment['PERF_INJECT_SLOWDOWN'] = $Side.role -eq 'candidate' ? $Context.InjectionText : ''
        $worktree = $Context.Worktrees[$Side.role -eq 'baseline' ? $Context.BaselineCommit : $Context.CandidateCommit]
        $exitCode = Invoke-BenchmarkDotnet -Environment $environment -LogPath (Join-Path $sideDirectory 'side.log') -ArgumentList @(
            'test', (Join-Path $worktree $script:HarnessProject), '-c', 'Release', '--no-build',
            '--filter', "FullyQualifiedName~Given_$($Context.ProviderClass)_BenchmarkSideRun")
        $sideExit = Get-BenchmarkUtcNow

        $trailing = Wait-BenchmarkCollectorTrailingSample -Collector $collector -SideExitUtc $sideExit
        Stop-BenchmarkCollector -Collector $collector
        $stopped = $true
        $sideEnd = Get-BenchmarkUtcNow
        if ($trailing.TimedOut) {
            Write-Host "Pair $($Side.pair) $($Side.role): no trailing sample for $($trailing.Missing -join ', ') within $($script:TrailingSampleTimeoutSeconds) s."
        }

        Write-BenchmarkEnvironment -SideDirectory $sideDirectory -RunId $Context.RunId -Pair $Side.pair -Role $Side.role -Facts $facts `
            -Phases @(
            (New-BenchmarkPhase -Name 'settle' -Start $settleStart -End $idleStart),
            (New-BenchmarkPhase -Name 'idle' -Start $idleStart -End $sideStart),
            (New-BenchmarkPhase -Name 'side' -Start $sideStart -End $sideEnd)) `
            -TrailingSampleTimeout $trailing.TimedOut -MissingTrailingMetrics $trailing.Missing -SideExitCode $exitCode

        if ($exitCode -ne 0) {
            Write-Host "Pair $($Side.pair) $($Side.role): the side process exited with $exitCode; see side.log."
            return $false
        }
        return $true
    }
    catch {
        Write-Host "Pair $($Side.pair) $($Side.role) failed: $_"
        return $false
    }
    finally {
        if (-not $stopped) {
            Stop-BenchmarkCollector -Collector $collector
        }
    }
}

function Initialize-BenchmarkComparison {
    <#
    .SYNOPSIS
        Setup: everything that must hold before any side runs. Throws on the first failure.
        Returns the run context.
    #>
    param([Parameter(Mandatory)][hashtable] $Options)
    $o = $Options
    Assert-BenchmarkCleanSource -SourceRoot $o.SourceRoot
    $loaded = Read-BenchmarkProfile -Path (Join-Path $o.SourceRoot $script:ProfilePath)
    $benchmarkProfile = $loaded.Profile

    $baseline = Resolve-BenchmarkCommit -Repository $o.SourceRoot -Commit $o.BaselineCommit
    $candidate = Resolve-BenchmarkCommit -Repository $o.SourceRoot -Commit ($o.CandidateCommit ? $o.CandidateCommit : $o.BaselineCommit)
    $harness = Resolve-BenchmarkCommit -Repository $o.SourceRoot -Commit 'HEAD'
    if ($baseline -ne $benchmarkProfile.approvedReferenceCommit) {
        throw "The baseline $baseline is not the approved reference $($benchmarkProfile.approvedReferenceCommit)."
    }
    foreach ($commit in @($baseline, $candidate) | Select-Object -Unique) {
        $floor = $benchmarkProfile.supportedRange.floorCommit
        if ((Invoke-BenchmarkGit -Repository $o.SourceRoot -ArgumentList @('merge-base', '--is-ancestor', $floor, $commit)).ExitCode -ne 0) {
            throw "Commit $commit does not descend from the supported floor $floor."
        }
    }

    $pairs = $o.Pairs -gt 0 ? $o.Pairs : [int]$benchmarkProfile.comparison.pairs
    $injectionGiven = -not [string]::IsNullOrWhiteSpace($o.InjectSlowdown)
    Assert-BenchmarkPurpose -Purpose $o.Purpose -BaselineCommit $baseline -CandidateCommit $candidate `
        -ApprovedReferenceCommit $benchmarkProfile.approvedReferenceCommit -Pairs $pairs -InjectionGiven $injectionGiven
    $injection = $o.Purpose -in @('Detection', 'BoundaryProbe') `
        ? (Get-BenchmarkProfileFactor -BenchmarkProfile $benchmarkProfile -Provider $o.Provider -Purpose $o.Purpose -Smoke:$o.Smoke) `
        : (ConvertFrom-BenchmarkInjection -Text $o.InjectSlowdown)

    $now = Get-BenchmarkUtcNow
    $name = '{0}-{1}-{2}-{3}' -f $o.Provider, $o.Purpose, $now.ToString("yyyyMMdd'T'HHmmss'Z'", $script:Invariant), $candidate.Substring(0, 8)
    $invocationDirectory = Join-Path $o.ResultsDirectory $name
    if ((Test-Path -LiteralPath $invocationDirectory) -and @(Get-ChildItem -LiteralPath $invocationDirectory -Force).Count -gt 0) {
        throw "The invocation directory '$invocationDirectory' already exists and is not empty."
    }

    $mssql = $o.Provider -eq 'mssql'
    $variable = $mssql ? 'ConnectionStrings__MssqlAdmin' : 'ConnectionStrings__DatabaseConnection'
    foreach ($required in @('ConnectionStrings__DatabaseConnection', $variable) | Select-Object -Unique) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($required))) {
            throw "$required must be set (a credential and option template; its endpoint is rewritten)."
        }
    }

    $worktrees = @{}
    foreach ($commit in @($baseline, $candidate) | Select-Object -Unique) {
        $worktree = Join-Path $o.WorktreeRoot $commit.Substring(0, 12)
        if (-not (Test-Path -LiteralPath $worktree)) {
            if ((Invoke-BenchmarkGit -Repository $o.SourceRoot -ArgumentList @('worktree', 'add', '--detach', $worktree, $commit)).ExitCode -ne 0) {
                throw "Cannot create the worktree '$worktree' at $commit."
            }
        }
        $head = ((Invoke-BenchmarkGit -Repository $worktree -ArgumentList @('rev-parse', 'HEAD')).Output -join '').Trim()
        if ($head -ne $commit) {
            throw "The worktree '$worktree' is at '$head', not $commit."
        }
        Copy-HarnessOverlay -SourceRoot $o.SourceRoot -Worktree $worktree
        Assert-BenchmarkCleanOverlay -Worktree $worktree
        $exitCode = Invoke-BenchmarkDotnet -LogPath (Join-Path $o.ResultsDirectory "build-$($commit.Substring(0, 12)).log") `
            -ArgumentList @('build', (Join-Path $worktree $script:HarnessProject), '-c', 'Release')
        if ($exitCode -ne 0) {
            throw "Building the harness in '$worktree' failed with exit code $exitCode."
        }
        $worktrees[$commit] = $worktree
    }

    $container = $mssql ? $o.MssqlContainerName : $o.PostgresContainerName
    $identity = Resolve-BenchmarkContainerIdentity -ContainerName $container
    $expectedDigest = $mssql ? $o.ExpectedMssqlDigest : $o.ExpectedPostgresDigest
    if ($identity.Digest -ne $expectedDigest) {
        throw "Container '$container' runs digest $($identity.Digest) but $expectedDigest is expected."
    }
    $portJson = (Invoke-BenchmarkDocker -ArgumentList @('inspect', $container, '--format', '{{json .NetworkSettings.Ports}}')).Output -join ''
    $endpoint = Resolve-ContainerEndpointFromPortBindingJson -PortBindingJson $portJson -ContainerPort ($mssql ? '1433/tcp' : '5432/tcp') -ContainerName $container
    $template = [Environment]::GetEnvironmentVariable($variable)
    $pinned = $mssql `
        ? (ConvertTo-MssqlEndpointPinnedConnectionString -ConnectionString $template -EndpointHost $endpoint.Host -EndpointPort $endpoint.Port) `
        : (ConvertTo-PostgresEndpointPinnedConnectionString -ConnectionString $template -EndpointHost $endpoint.Host -EndpointPort $endpoint.Port)
    $dataPath = '/var/opt/mssql'
    if (-not $mssql) {
        $envLines = (Invoke-BenchmarkDocker -ArgumentList @('inspect', $container, '--format', '{{range .Config.Env}}{{println .}}{{end}}')).Output
        $pgData = @($envLines | Where-Object { $_ -like 'PGDATA=*' }) | Select-Object -First 1
        $dataPath = $pgData ? $pgData.Substring(7) : '/var/lib/postgresql/data'
    }

    $order = [Collections.Generic.List[object]]::new()
    for ($pair = 1; $pair -le $pairs; $pair++) {
        $roles = $pair % 2 -eq 1 ? @('baseline', 'candidate') : @('candidate', 'baseline')
        $order.Add([ordered]@{ pair = $pair; role = $roles[0]; position = 1 })
        $order.Add([ordered]@{ pair = $pair; role = $roles[1]; position = 2 })
    }
    $runId = 'run-' + [guid]::NewGuid().ToString('N')
    $plan = [ordered]@{
        schemaVersion   = 'benchmark-run-plan 1.0.0'
        runId           = $runId
        provider        = $o.Provider
        purpose         = $o.Purpose
        smoke           = [bool]$o.Smoke
        baselineCommit  = $baseline
        candidateCommit = $candidate
        harnessCommit   = $harness
        profileSha256   = $loaded.Sha256
        injection       = [ordered]@{}
        order           = @($order)
    }
    foreach ($entry in $injection.GetEnumerator()) { $plan.injection[$entry.Key] = $entry.Value }

    New-Item -ItemType Directory -Force -Path $invocationDirectory | Out-Null
    Write-BenchmarkJson -Path (Join-Path $invocationDirectory 'run-plan.json') -Value $plan

    $other = $mssql ? $o.PostgresContainerName : $o.MssqlContainerName
    return [pscustomobject]@{
        InvocationDirectory = (Resolve-Path -LiteralPath $invocationDirectory).Path
        Profile             = $benchmarkProfile
        Plan                = $plan
        RunId               = $runId
        BaselineCommit      = $baseline
        CandidateCommit     = $candidate
        Worktrees           = $worktrees
        ContainerName       = $container
        OtherContainerName  = $other
        DataPath            = $dataPath
        ProviderClass       = $mssql ? 'Mssql' : 'Postgresql'
        InjectionText       = ConvertTo-BenchmarkInjectionText -Factors $injection
        SideEnvironment     = @{
            $variable                 = $pinned
            'PERF_RUNNER_COMMIT'      = $harness
            'PERF_IMAGE_TAG'          = $identity.Tag
            'PERF_IMAGE_DIGEST'       = $identity.Digest
            'PERF_STORAGE_NOTE'       = $o.StorageNote
            'PERF_BENCHMARK_RUN_ID'   = $runId
            'PERF_BENCHMARK_WORKLOAD' = $o.Smoke ? 'smokeWorkload' : 'workload'
        }
    }
}

function Invoke-BenchmarkComparison {
    <#
    .SYNOPSIS
        Runs one benchmark comparison and returns its exit code and invocation directory (see
        the module description). Provider and purpose are matched case-insensitively, pgsql is
        an alias of postgresql, and the run uses the canonical spelling throughout.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Benchmark tooling intentionally writes operator progress and the comparison report to the console; the function returns its outcome object, so Write-Output would corrupt the pipeline result.')]
    param(
        [Parameter(Mandatory)][string] $BaselineCommit,
        [string] $CandidateCommit,
        [Parameter(Mandatory)][ValidateSet('postgresql', 'pgsql', 'mssql')][string] $Provider,
        [Parameter(Mandatory)][string] $ResultsDirectory,
        [ValidateRange(0, 99)][int] $Pairs = 0,
        [string] $WorktreeRoot,
        [switch] $Smoke,
        [ValidateSet('Candidate', 'Calibration', 'Sizing', 'Repeatability', 'Detection', 'BoundaryProbe')][string] $Purpose = 'Candidate',
        [string] $InjectSlowdown,
        [string] $PostgresContainerName = 'dms-postgresql',
        [string] $MssqlContainerName = 'dms-mssql-integration-2025',
        [string] $ExpectedPostgresDigest = 'sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873',
        [string] $ExpectedMssqlDigest = 'sha256:86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a',
        [string] $StorageNote = 'local docker volume, not tmpfs',
        [string] $SourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
    )
    if (-not $WorktreeRoot) {
        $WorktreeRoot = Join-Path (Split-Path $SourceRoot -Parent) 'dms-bench'
    }
    # ValidateSet ignores case, but the report accepts only the canonical tokens, so a run
    # must never carry the spelling it was invoked with.
    $Provider = $Provider -eq 'pgsql' ? 'postgresql' : $Provider.ToLowerInvariant()
    $Purpose = $script:Purposes | Where-Object { $_ -eq $Purpose }
    $options = @{
        BaselineCommit = $BaselineCommit; CandidateCommit = $CandidateCommit; Provider = $Provider
        ResultsDirectory = $ResultsDirectory; Pairs = $Pairs; WorktreeRoot = $WorktreeRoot; Smoke = [bool]$Smoke
        Purpose = $Purpose; InjectSlowdown = $InjectSlowdown; PostgresContainerName = $PostgresContainerName
        MssqlContainerName = $MssqlContainerName; ExpectedPostgresDigest = $ExpectedPostgresDigest
        ExpectedMssqlDigest = $ExpectedMssqlDigest; StorageNote = $StorageNote; SourceRoot = $SourceRoot
    }

    $lock = $null
    try {
        try {
            $lock = Open-BenchmarkHostLock
            New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
            $context = Initialize-BenchmarkComparison -Options $options
        }
        catch {
            Write-Host "Setup failed; no side ran: $_"
            return [pscustomobject]@{ ExitCode = 1; InvocationDirectory = $null }
        }

        $outcome = { param([int] $ExitCode) [pscustomobject]@{ ExitCode = $ExitCode; InvocationDirectory = $context.InvocationDirectory } }
        Write-Host "Run $($context.RunId): $Provider $Purpose, $($context.Plan.order.Count / 2) pairs, injection '$($context.InjectionText)' -> $($context.InvocationDirectory)"
        foreach ($side in $context.Plan.order) {
            if (-not (Invoke-BenchmarkSide -Context $context -Side $side)) {
                Write-Host 'Stopping after the first failed side; the comparison still runs and judges the evidence.'
                break
            }
        }

        $reportExit = Invoke-BenchmarkDotnet -Environment @{ 'PERF_BENCHMARK_DIR' = $context.InvocationDirectory } `
            -LogPath (Join-Path $context.InvocationDirectory 'comparison.log') -ArgumentList @(
            'test', (Join-Path $context.Worktrees[$context.BaselineCommit] $script:HarnessProject), '-c', 'Release', '--no-build',
            '--filter', 'FullyQualifiedName~Given_BenchmarkComparisonRun')
        if ($reportExit -ne 0) {
            Write-Host "The comparison report could not be produced (exit code $reportExit); see comparison.log."
            return & $outcome 1
        }
        try {
            $comparison = Get-Content -Raw -LiteralPath (Join-Path $context.InvocationDirectory 'comparison.json') -ErrorAction Stop | ConvertFrom-Json
            $exitCode = [int]$comparison.exitCode
        }
        catch {
            Write-Host "comparison.json is missing or unreadable: $_"
            return & $outcome 1
        }
        $markdown = Join-Path $context.InvocationDirectory 'comparison.md'
        if (Test-Path -LiteralPath $markdown) {
            Write-Host (Get-Content -Raw -LiteralPath $markdown)
        }
        Write-Host "Comparison $($comparison.result), exit code ${exitCode}: $($context.InvocationDirectory)"
        return & $outcome $exitCode
    }
    finally {
        if ($null -ne $lock) { $lock.Dispose() }
    }
}

Export-ModuleMember -Function @(
    'Invoke-BenchmarkComparison',
    'Wait-BenchmarkCollectorTrailingSample',
    'Get-BenchmarkStorageMountType',
    'Get-BenchmarkDatabaseCpuLimit'
)
