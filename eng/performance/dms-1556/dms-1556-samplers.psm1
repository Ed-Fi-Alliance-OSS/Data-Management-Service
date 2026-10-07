# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
DMS-1556 diagnostics samplers as a module, so the burst harness can coordinate them
explicitly around a cold restart: start (against the CURRENT CMS process), verify every
capture is producing data before the burst, and verify afterwards that the captures cover
the whole burst window. Start-DiagnosticsSamplers.ps1 wraps these for standalone use.

Lifecycle contract: whoever calls Start-DmsSamplerSet owns the returned state and must
call Remove-DmsSamplerSet from a finally block. Stop-DmsSamplerSet is the graceful path
(teardown + validation); Remove-DmsSamplerSet is the idempotent emergency path that
never throws, so cleanup on a failure cannot mask the original error. Start itself
cleans up any jobs it created if it fails partway through.

Required captures: pg-activity, pg-io, and docker-stats always; host-disk on Windows
(explicitly reported as unsupported elsewhere); livemetrics when the monitor is required,
with window coverage checked PER required provider (System.Runtime,
Microsoft.AspNetCore.Hosting, Npgsql) - one early sample of a provider does not count as
coverage merely because another provider continues through the burst.

PostgreSQL sampling uses ONE persistent connection per stream (psql \watch inside the
container) with a distinguishable application_name carrying the shared attribution
prefix plus a per-set unique suffix (dms1556-sampler-activity-<id> /
dms1556-sampler-io-<id>), and the activity rows retain datname/usename/application_name,
so sampler connections are self-identifying and excludable from M-conn instead of adding
~5 anonymous connections per second to the connection-creation evidence. Cleanup
terminates ONLY the exact names stored in its own state, so overlapping sampler sets
(e.g. a standalone capture beside a harness run) cannot end each other's sessions.
#>

Set-StrictMode -Version Latest

$script:DataLinePattern = '^\d{4}-\d{2}-\d{2}[ T]'
$script:CsvDataLinePattern = '^"\d{4}-\d{2}-\d{2}'
$script:RequiredLivemetricsProviders = @('System.Runtime', 'Microsoft.AspNetCore.Hosting', 'Npgsql')
$script:CaptureNameByJobKey = @{
    activity    = 'pg-activity'
    io          = 'pg-io'
    stats       = 'docker-stats'
    disk        = 'host-disk'
    livemetrics = 'livemetrics'
}

function Start-DmsSamplerSet {
    <#
    .SYNOPSIS
    Starts the DMS-1556 sampler jobs against the CURRENT CMS process and returns the
    state handle for Wait-DmsSamplerSetReady / Stop-DmsSamplerSet. On a partial startup
    failure it stops the jobs it already created before rethrowing. The caller owns the
    returned state and must call Remove-DmsSamplerSet from a finally block.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Parameters are read inside Start-Job script blocks through $using:, which the analyzer does not track.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Starts local, bounded diagnostics sampling jobs; ShouldProcess prompting would break unattended burst coordination.')]
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory)]
        [string] $Label,

        [Parameter(Mandatory)]
        [string] $OutputDirectory,

        [Parameter(Mandatory)]
        [ValidateRange(10, 3600)]
        [int] $DurationSeconds,

        [string] $PgContainerName = 'dms-postgresql',

        [string] $PgUser = 'postgres',

        [string[]] $StatsContainers = @('ed-fi-api-config-service', 'dms-postgresql'),

        # dotnet-monitor sidecar (local-config-diagnostics.yml); '' disables livemetrics.
        [string] $MonitorBaseUrl = 'http://localhost:52323',

        # Fail instead of skipping when the sidecar or its single process is unavailable.
        # The /livemetrics capture is the source of the runtime counters (System.Runtime
        # thread pool/GC/CPU, Microsoft.AspNetCore.Hosting requests, Npgsql), so
        # coordinated evidence runs must set this.
        [switch] $RequireMonitor
    )

    $null = New-Item -ItemType Directory -Force -Path $OutputDirectory
    $paths = @{
        activity    = Join-Path $OutputDirectory "$Label-pg-activity.psv"
        io          = Join-Path $OutputDirectory "$Label-pg-io.psv"
        stats       = Join-Path $OutputDirectory "$Label-docker-stats.jsonl"
        disk        = Join-Path $OutputDirectory "$Label-host-disk.csv"
        livemetrics = Join-Path $OutputDirectory "$Label-livemetrics.json"
    }

    # The monitor pid is resolved NOW, so a caller that just restarted CMS binds the
    # capture to the new process, never a stale pre-restart one.
    $monitorPid = $null
    if ($MonitorBaseUrl) {
        try {
            $processes = @(Invoke-RestMethod -Uri "$MonitorBaseUrl/processes" -TimeoutSec 10)
            if ($processes.Count -eq 1) {
                $monitorPid = $processes[0].pid
            }
            elseif ($RequireMonitor) {
                throw "Expected exactly one process at $MonitorBaseUrl, found $($processes.Count)."
            }
            else {
                Write-Output "livemetrics skipped: expected one process at $MonitorBaseUrl, found $($processes.Count)."
            }
        }
        catch {
            if ($RequireMonitor) {
                throw "dotnet-monitor sidecar required but unavailable at ${MonitorBaseUrl}: $($_.Exception.Message)"
            }
            Write-Output "livemetrics skipped: $MonitorBaseUrl unreachable ($($_.Exception.Message))."
        }
    }
    elseif ($RequireMonitor) {
        throw 'RequireMonitor was set but MonitorBaseUrl is empty.'
    }

    # One persistent psql connection per stream; \watch re-runs the buffered query on a
    # bounded count, so the loop self-terminates and never spawns per-tick connections.
    # Output is unaligned, '|'-separated, one data row per line starting with now()::text;
    # stderr is merged into the file deliberately so a failing sampler is diagnosable.
    # The application names keep the shared attribution prefix but carry a per-set unique
    # suffix, so this set's cleanup can never terminate an overlapping set's sessions.
    $setId = [guid]::NewGuid().ToString('N').Substring(0, 8)
    $activityAppName = "dms1556-sampler-activity-$setId"
    $ioAppName = "dms1556-sampler-io-$setId"
    $activityConnInfo = "dbname=postgres application_name=$activityAppName"
    $ioConnInfo = "dbname=postgres application_name=$ioAppName"
    $activityTicks = [int][Math]::Ceiling($DurationSeconds / 0.25)
    $activityQuery = "SELECT now()::text, 'activity', coalesce(datname,''), coalesce(usename,''), coalesce(application_name,''), coalesce(state,'(none)'), coalesce(wait_event_type,'(none)'), backend_type, count(*)::text FROM pg_stat_activity GROUP BY 1,2,3,4,5,6,7,8 UNION ALL SELECT now()::text, 'numbackends', datname, '', '', '', '', '', numbackends::text FROM pg_stat_database WHERE datname IS NOT NULL AND numbackends > 0"
    $activityInput = "$activityQuery`n\watch i=0.25 c=$activityTicks`n"

    $ioQuery = "SELECT now()::text, 'pg_stat_io', row_to_json(x)::text FROM pg_stat_io x WHERE reads > 0 OR writes > 0 OR fsyncs > 0 UNION ALL SELECT now()::text, 'pg_stat_bgwriter', row_to_json(b)::text FROM pg_stat_bgwriter b"
    $ioInput = "$ioQuery`n\watch i=1 c=$DurationSeconds`n"

    $state = @{
        Label           = $Label
        Paths           = $paths
        Jobs            = @{}
        PgContainerName = $PgContainerName
        PgUser          = $PgUser
        # The exact application names owned by THIS set; cleanup terminates only these.
        PgApplicationNames = @($activityAppName, $ioAppName)
        MonitorBaseUrl  = $MonitorBaseUrl
        MonitorPid      = $monitorPid
        MonitorRequired = [bool]$RequireMonitor
        DiskSupported   = [bool]$IsWindows
        DurationSeconds = $DurationSeconds
        StartedUtc      = [DateTime]::UtcNow
        CleanedUp       = $false
    }

    try {
        $state.Jobs.activity = Start-Job -Name "$Label-pg-activity" -ScriptBlock {
            $using:activityInput |
                & docker exec -i $using:PgContainerName psql -U $using:PgUser -d $using:activityConnInfo -X -q -A -t -F '|' 2>&1 |
                Set-Content -LiteralPath ($using:paths).activity -Encoding utf8
        }

        $state.Jobs.io = Start-Job -Name "$Label-pg-io" -ScriptBlock {
            $using:ioInput |
                & docker exec -i $using:PgContainerName psql -U $using:PgUser -d $using:ioConnInfo -X -q -A -t -F '|' 2>&1 |
                Set-Content -LiteralPath ($using:paths).io -Encoding utf8
        }

        # docker stats streams a refresh roughly every 500 ms when stdout is not a TTY.
        # The job bounds itself: breaking the pipeline at the deadline closes docker's
        # stdout pipe and ends the docker process, so nothing owned by the job can
        # outlive the window even if the parent never stops it.
        $state.Jobs.stats = Start-Job -Name "$Label-docker-stats" -ScriptBlock {
            $deadline = [DateTime]::UtcNow.AddSeconds(($using:DurationSeconds) + 15)
            $containers = $using:StatsContainers
            & docker stats --format '{{json .}}' @containers 2>$null | ForEach-Object {
                if ([DateTime]::UtcNow -gt $deadline) { break }
                # docker stats emits terminal control sequences (e.g. erase-line) even piped.
                $line = ($_ -replace "`e\[[0-9;]*[A-Za-z]", '').Trim()
                if ($line.StartsWith('{') -and $line.EndsWith('}')) {
                    '{{"tsUtc":"{0}","stat":{1}}}' -f ([DateTime]::UtcNow.ToString('o')), $line
                }
            } | Add-Content -LiteralPath ($using:paths).stats -Encoding utf8
        }

        if ($state.DiskSupported) {
            $state.Jobs.disk = Start-Job -Name "$Label-host-disk" -ScriptBlock {
                $counters = @(
                    '\PhysicalDisk(_Total)\Disk Transfers/sec'
                    '\PhysicalDisk(_Total)\Avg. Disk sec/Transfer'
                    '\PhysicalDisk(_Total)\% Idle Time'
                )
                Get-Counter -Counter $counters -SampleInterval 1 -MaxSamples $using:DurationSeconds | ForEach-Object {
                    foreach ($sample in $_.CounterSamples) {
                        [pscustomobject]@{
                            tsUtc   = $_.Timestamp.ToUniversalTime().ToString('o')
                            counter = $sample.Path
                            value   = [Math]::Round($sample.CookedValue, 4)
                        }
                    }
                } | Export-Csv -LiteralPath ($using:paths).disk -NoTypeInformation -Encoding utf8
            }
        }

        if ($null -ne $monitorPid) {
            $state.Jobs.livemetrics = Start-Job -Name "$Label-livemetrics" -ScriptBlock {
                try {
                    Invoke-WebRequest -Uri "$($using:MonitorBaseUrl)/livemetrics?pid=$($using:monitorPid)&durationSeconds=$($using:DurationSeconds)" `
                        -OutFile ($using:paths).livemetrics -TimeoutSec (($using:DurationSeconds) + 30)
                }
                catch {
                    Set-Content -LiteralPath ($using:paths).livemetrics -Value ('livemetrics capture failed: {0}' -f $_.Exception.Message) -Encoding utf8
                }
            }
        }
    }
    catch {
        # Partial startup: stop whatever was already created, then rethrow the original.
        Remove-DmsSamplerSet -State $state
        throw
    }

    return $state
}

function Wait-DmsSamplerSetReady {
    <#
    .SYNOPSIS
    Blocks until every required capture has produced at least one data line, so a burst
    launched afterwards is guaranteed to fall inside every sampler's window. Throws when
    a capture fails to produce data (a skipped or failed sampler must never look like a
    successful invocation). On a throw the caller's finally block is responsible for
    Remove-DmsSamplerSet; this function does not clean up, so the state stays inspectable.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [hashtable] $State,

        [ValidateRange(5, 300)]
        [int] $TimeoutSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $pending = [System.Collections.Generic.List[string]]::new()

        if (-not (Test-DmsCaptureHasData -Path $State.Paths.activity -Pattern $script:DataLinePattern)) {
            $pending.Add('pg-activity')
        }
        if (-not (Test-DmsCaptureHasData -Path $State.Paths.io -Pattern $script:DataLinePattern)) {
            $pending.Add('pg-io')
        }
        if (-not (Test-DmsCaptureHasData -Path $State.Paths.stats -Pattern '^\{"tsUtc"')) {
            $pending.Add('docker-stats')
        }
        if ($State.DiskSupported -and -not (Test-DmsCaptureHasData -Path $State.Paths.disk -Pattern $script:CsvDataLinePattern)) {
            $pending.Add('host-disk')
        }
        if ($null -ne $State.MonitorPid -and -not (Test-DmsCaptureHasData -Path $State.Paths.livemetrics -Pattern '"timestamp"')) {
            $pending.Add('livemetrics')
        }

        if ($pending.Count -eq 0) {
            return
        }

        foreach ($entry in $State.Jobs.GetEnumerator()) {
            $captureName = $script:CaptureNameByJobKey[$entry.Key]
            if ($captureName -and $entry.Value.State -in @('Failed', 'Completed') -and $pending -contains $captureName) {
                $output = (Receive-Job -Job $entry.Value -Keep -ErrorAction SilentlyContinue) -join '; '
                throw "Sampler '$($entry.Key)' ended before producing data (state $($entry.Value.State)). Job output: $output. Inspect $($State.Paths[$entry.Key]) for errors."
            }
        }

        if ([DateTime]::UtcNow -ge $deadline) {
            throw "Samplers not producing data within $TimeoutSeconds s: $($pending -join ', '). Inspect the capture files for errors."
        }
        Start-Sleep -Milliseconds 500
    }
}

function Test-DmsCaptureHasData {
    <#
    .SYNOPSIS
    True when the capture file exists and contains at least one line matching Pattern.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Pattern
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $false
    }
    try {
        return [bool](Select-String -LiteralPath $Path -Pattern $Pattern -Quiet -ErrorAction Stop)
    }
    catch {
        return $false
    }
}

function Stop-DmsSamplerSet {
    <#
    .SYNOPSIS
    Waits out the sampler window, stops the jobs, and validates the captures via
    Get-DmsSamplerValidation. With a burst window supplied, coverage is required for
    every required capture and per required livemetrics provider. Returns a report whose
    requiredFailures list is non-empty when the evidence is unusable; the caller decides
    whether to throw. After a successful Stop, Remove-DmsSamplerSet is a no-op.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Stops the local diagnostics sampling jobs this module started; ShouldProcess prompting would break unattended burst coordination.')]
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [hashtable] $State,

        [DateTime] $BurstStartUtc,

        [DateTime] $BurstEndUtc,

        # Stop the capture jobs now instead of waiting out the remainder of the sampler
        # window. For a burst that finished well inside a generously sized window this
        # trades no evidence away: the coverage validation below still judges the
        # captures by their actual sample timestamps against the burst window, so a
        # too-early stop surfaces as a requiredFailures entry, never silently. Callers
        # should let the samplers record a few seconds past the burst end first.
        [switch] $SkipWindowWait
    )

    $warnings = [System.Collections.Generic.List[string]]::new()

    # The bounded jobs (\watch counts, livemetrics duration, disk MaxSamples, the stats
    # deadline) self-terminate; wait out the remainder of the window plus tool-startup
    # slack before declaring them stuck, unless the caller opted into an early stop.
    $bounded = @($State.Jobs.GetEnumerator() | Where-Object { $_.Key -in @('activity', 'io', 'livemetrics', 'disk') } | ForEach-Object { $_.Value })
    if (-not $SkipWindowWait -and $bounded.Count -gt 0) {
        $elapsed = ([DateTime]::UtcNow - $State.StartedUtc).TotalSeconds
        $remaining = [Math]::Max(5, [int]($State.DurationSeconds - $elapsed) + 30)
        $null = Wait-Job -Job $bounded -Timeout $remaining
    }

    foreach ($entry in $State.Jobs.GetEnumerator()) {
        $job = $entry.Value
        if ($job.State -eq 'Running') {
            if (-not $SkipWindowWait -and $entry.Key -in @('activity', 'io', 'livemetrics', 'disk')) {
                $warnings.Add("Sampler '$($entry.Key)' was still running after its window and was stopped; its capture may be truncated.")
            }
            Stop-Job -Job $job
        }
        $jobErrors = @()
        try {
            $null = Receive-Job -Job $job -ErrorAction SilentlyContinue -ErrorVariable jobErrors
        }
        catch {
            $jobErrors += $_
        }
        foreach ($jobError in $jobErrors) {
            $warnings.Add("Sampler '$($entry.Key)' reported: $jobError")
        }
        Remove-Job -Job $job -Force
    }
    Stop-DmsPgSamplerSession -State $State
    $State.Jobs = @{}
    $State.CleanedUp = $true

    $validationArguments = @{
        Paths           = $State.Paths
        MonitorPid      = $State.MonitorPid
        MonitorRequired = $State.MonitorRequired
        DiskSupported   = $State.DiskSupported
    }
    if ($PSBoundParameters.ContainsKey('BurstStartUtc')) { $validationArguments.BurstStartUtc = $BurstStartUtc }
    if ($PSBoundParameters.ContainsKey('BurstEndUtc')) { $validationArguments.BurstEndUtc = $BurstEndUtc }
    $validation = Get-DmsSamplerValidation @validationArguments

    return [pscustomobject]@{
        label                = $State.Label
        monitorPid           = $State.MonitorPid
        durationSeconds      = $State.DurationSeconds
        files                = $State.Paths
        pgActivity           = $validation.pgActivity
        pgIo                 = $validation.pgIo
        dockerStats          = $validation.dockerStats
        hostDisk             = $validation.hostDisk
        livemetrics          = $validation.livemetrics
        # /livemetrics is the capture that supplies the runtime counters: System.Runtime
        # (thread pool, lock contention, CPU, GC, working set), ASP.NET Core Hosting
        # (current/failed requests) and Npgsql (connection pool usage).
        livemetricsProviders = $validation.livemetricsProviders
        requiredFailures     = @($validation.requiredFailures)
        warnings             = @(@($warnings) + @($validation.warnings))
    }
}

function Remove-DmsSamplerSet {
    <#
    .SYNOPSIS
    Emergency cleanup for a sampler set: stops and removes any jobs still owned by the
    state. Idempotent (a no-op after a successful Stop-DmsSamplerSet) and never throws,
    so calling it from a finally block cannot mask the original error. Killing a job
    does NOT end its docker clients on Windows - they are orphaned with their pipes
    still open (established during the lifecycle validation) - so the PostgreSQL sampler
    sessions are terminated server-side via Stop-DmsPgSamplerSession, which also ends
    the orphaned exec clients; the docker stats client exits on its own once its
    reader is gone, and the \watch counts bound the pg sessions regardless.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Emergency cleanup of jobs this module started; must run unprompted from finally blocks.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [hashtable] $State
    )

    if ($State.CleanedUp) {
        return
    }
    foreach ($entry in @($State.Jobs.GetEnumerator())) {
        try {
            if ($entry.Value.State -eq 'Running') {
                Stop-Job -Job $entry.Value -ErrorAction SilentlyContinue
            }
            Remove-Job -Job $entry.Value -Force -ErrorAction SilentlyContinue
        }
        catch {
            Write-Verbose "Cleanup of sampler job '$($entry.Key)' failed: $($_.Exception.Message)"
        }
    }
    try {
        Stop-DmsPgSamplerSession -State $State
    }
    catch {
        Write-Verbose "Terminating in-container sampler sessions failed: $($_.Exception.Message)"
    }
    $State.Jobs = @{}
    $State.CleanedUp = $true
}

function Stop-DmsPgSamplerSession {
    <#
    .SYNOPSIS
    Terminates THIS set's sampler backends in the PostgreSQL container, matched by the
    exact application names stored in the state - never by prefix, so an overlapping
    sampler set's sessions are left untouched. Killing a sampler's PowerShell job
    orphans its docker exec client on Windows (child processes are not killed with the
    job), and the orphaned client keeps the exec stream open, so the in-container psql
    \watch session never sees a broken pipe; terminating it server-side ends both the
    session and the orphaned client immediately. A no-op when the sessions already
    exited (the \watch counts bound them regardless). Never throws.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Terminates only this sampler set''s own self-identified sessions; must run unprompted from finally blocks.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [hashtable] $State
    )

    try {
        # The names are module-generated (fixed prefix + hex suffix), so inlining them in
        # the SQL literal is safe.
        $nameList = (@($State.PgApplicationNames) | ForEach-Object { "'$_'" }) -join ', '
        if (-not $nameList) { return }
        $null = docker exec $State.PgContainerName psql -U $State.PgUser -d postgres -Atc "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name IN ($nameList)" 2>$null
    }
    catch {
        Write-Verbose "pg_terminate_backend sweep failed: $($_.Exception.Message)"
    }
}

function Get-DmsSamplerValidation {
    <#
    .SYNOPSIS
    Pure validation of a sampler set's capture files: data presence for every required
    capture, window coverage when a burst window is supplied, sampler attribution in
    pg-activity, and per-required-provider coverage for livemetrics. Takes only paths
    and flags so it is testable against synthetic capture files.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [hashtable] $Paths,

        [object] $MonitorPid = $null,

        [bool] $MonitorRequired = $false,

        [bool] $DiskSupported = $true,

        [DateTime] $BurstStartUtc,

        [DateTime] $BurstEndUtc
    )

    $requiredFailures = [System.Collections.Generic.List[string]]::new()
    $warnings = [System.Collections.Generic.List[string]]::new()
    $hasWindow = ($PSBoundParameters.ContainsKey('BurstStartUtc') -and $PSBoundParameters.ContainsKey('BurstEndUtc'))

    function Test-RequiredCapture {
        param([string] $Name, [pscustomobject] $Summary)
        if ($Summary.rows -eq 0) {
            $requiredFailures.Add("$Name produced no data rows")
        }
        elseif ($hasWindow -and -not (Test-DmsWindowCovered -Summary $Summary -StartUtc $BurstStartUtc -EndUtc $BurstEndUtc)) {
            $requiredFailures.Add("$Name does not cover the burst window (samples $($Summary.firstUtc)..$($Summary.lastUtc))")
        }
    }

    $activity = Get-DmsDelimitedCaptureSummary -Path $Paths.activity
    Test-RequiredCapture -Name 'pg-activity' -Summary $activity
    if ($activity.rows -gt 0 -and $activity.samplerRows -eq 0) {
        $requiredFailures.Add('pg-activity rows never show the dms1556-sampler application_name; sampler connections are not attributable/excludable')
    }

    $io = Get-DmsDelimitedCaptureSummary -Path $Paths.io
    Test-RequiredCapture -Name 'pg-io' -Summary $io

    $stats = Get-DmsJsonlCaptureSummary -Path $Paths.stats -TimestampProperty 'tsUtc'
    Test-RequiredCapture -Name 'docker-stats' -Summary $stats

    $disk = $null
    if ($DiskSupported) {
        $disk = Get-DmsCsvCaptureSummary -Path $Paths.disk
        Test-RequiredCapture -Name 'host-disk' -Summary $disk
    }
    else {
        $disk = [pscustomobject]@{ path = $Paths.disk; rows = 0; firstUtc = $null; lastUtc = $null; skipped = 'not supported on this platform; collect /proc/diskstats on the runner instead' }
    }

    $livemetrics = $null
    $providerSummaries = @()
    if ($null -ne $MonitorPid -or $MonitorRequired) {
        $livemetrics = Get-DmsLivemetricsSummary -Path $Paths.livemetrics
        $providerSummaries = @($livemetrics.providers)
        if ($MonitorRequired) {
            if ($livemetrics.rows -eq 0) {
                $requiredFailures.Add('livemetrics produced no samples')
            }
            else {
                # Coverage is checked per required provider: a provider whose samples
                # stop before the burst ends is missing evidence even while another
                # provider continues through the window.
                foreach ($required in $script:RequiredLivemetricsProviders) {
                    $providerSummary = $providerSummaries | Where-Object { $_.provider -eq $required }
                    if ($null -eq $providerSummary -or $providerSummary.rows -eq 0) {
                        $requiredFailures.Add("livemetrics is missing the '$required' provider; it is the capture that must supply those counters")
                    }
                    elseif ($hasWindow -and -not (Test-DmsWindowCovered -Summary $providerSummary -StartUtc $BurstStartUtc -EndUtc $BurstEndUtc)) {
                        $requiredFailures.Add("livemetrics provider '$required' does not cover the burst window (samples $($providerSummary.firstUtc)..$($providerSummary.lastUtc))")
                    }
                }
            }
        }
    }
    else {
        $livemetrics = [pscustomobject]@{ path = $Paths.livemetrics; rows = 0; firstUtc = $null; lastUtc = $null; skipped = 'monitor disabled or unavailable (not required for this run)' }
    }

    return [pscustomobject]@{
        pgActivity           = $activity
        pgIo                 = $io
        dockerStats          = $stats
        hostDisk             = $disk
        livemetrics          = $livemetrics
        livemetricsProviders = $providerSummaries
        requiredFailures     = @($requiredFailures)
        warnings             = @($warnings)
    }
}

function Get-DmsDelimitedCaptureSummary {
    <#
    .SYNOPSIS
    Summarizes a '|'-separated psql capture whose data rows start with now()::text:
    row count, sampler-attributed row count, and first/last sample timestamps.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)][string] $Path)

    $rows = 0
    $samplerRows = 0
    $firstUtc = $null
    $lastUtc = $null
    if (Test-Path -LiteralPath $Path) {
        foreach ($line in [System.IO.File]::ReadLines($Path)) {
            if ($line -notmatch $script:DataLinePattern) { continue }
            $rows++
            if ($line -match 'dms1556-sampler') { $samplerRows++ }
            $timestampText = $line.Split('|')[0]
            $parsed = [DateTimeOffset]::MinValue
            if ([DateTimeOffset]::TryParse($timestampText, [ref]$parsed)) {
                $utc = $parsed.UtcDateTime
                if ($null -eq $firstUtc -or $utc -lt $firstUtc) { $firstUtc = $utc }
                if ($null -eq $lastUtc -or $utc -gt $lastUtc) { $lastUtc = $utc }
            }
        }
    }
    return [pscustomobject]@{
        path        = $Path
        rows        = $rows
        samplerRows = $samplerRows
        firstUtc    = if ($firstUtc) { $firstUtc.ToString('o') } else { $null }
        lastUtc     = if ($lastUtc) { $lastUtc.ToString('o') } else { $null }
    }
}

function Get-DmsJsonlCaptureSummary {
    <#
    .SYNOPSIS
    Summarizes a JSON-lines capture by a named timestamp property (row count plus
    first/last timestamps), via regex rather than full JSON parsing so multi-thousand-
    line captures summarize quickly.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $TimestampProperty
    )

    $rows = 0
    $firstUtc = $null
    $lastUtc = $null
    $pattern = '"' + [regex]::Escape($TimestampProperty) + '"\s*:\s*"([^"]+)"'
    if (Test-Path -LiteralPath $Path) {
        foreach ($line in [System.IO.File]::ReadLines($Path)) {
            $match = [regex]::Match($line, $pattern)
            if (-not $match.Success) { continue }
            $rows++
            $parsed = [DateTimeOffset]::MinValue
            if ([DateTimeOffset]::TryParse($match.Groups[1].Value, [ref]$parsed)) {
                $utc = $parsed.UtcDateTime
                if ($null -eq $firstUtc -or $utc -lt $firstUtc) { $firstUtc = $utc }
                if ($null -eq $lastUtc -or $utc -gt $lastUtc) { $lastUtc = $utc }
            }
        }
    }
    return [pscustomobject]@{
        path     = $Path
        rows     = $rows
        firstUtc = if ($firstUtc) { $firstUtc.ToString('o') } else { $null }
        lastUtc  = if ($lastUtc) { $lastUtc.ToString('o') } else { $null }
    }
}

function Get-DmsCsvCaptureSummary {
    <#
    .SYNOPSIS
    Summarizes an Export-Csv capture whose first column is a quoted ISO timestamp
    (row count plus first/last timestamps).
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)][string] $Path)

    $rows = 0
    $firstUtc = $null
    $lastUtc = $null
    if (Test-Path -LiteralPath $Path) {
        foreach ($line in [System.IO.File]::ReadLines($Path)) {
            if ($line -notmatch $script:CsvDataLinePattern) { continue }
            $rows++
            $timestampText = $line.Split(',')[0].Trim('"')
            $parsed = [DateTimeOffset]::MinValue
            if ([DateTimeOffset]::TryParse($timestampText, [ref]$parsed)) {
                $utc = $parsed.UtcDateTime
                if ($null -eq $firstUtc -or $utc -lt $firstUtc) { $firstUtc = $utc }
                if ($null -eq $lastUtc -or $utc -gt $lastUtc) { $lastUtc = $utc }
            }
        }
    }
    return [pscustomobject]@{
        path     = $Path
        rows     = $rows
        firstUtc = if ($firstUtc) { $firstUtc.ToString('o') } else { $null }
        lastUtc  = if ($lastUtc) { $lastUtc.ToString('o') } else { $null }
    }
}

function Get-DmsLivemetricsSummary {
    <#
    .SYNOPSIS
    Summarizes a dotnet-monitor livemetrics capture per provider: row count and
    first/last sample timestamps for each provider seen, plus totals.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)][string] $Path)

    $byProvider = @{}
    $rows = 0
    $firstUtc = $null
    $lastUtc = $null
    $pattern = '"timestamp"\s*:\s*"([^"]+)".*?"provider"\s*:\s*"([^"]+)"'
    if (Test-Path -LiteralPath $Path) {
        foreach ($line in [System.IO.File]::ReadLines($Path)) {
            $match = [regex]::Match($line, $pattern)
            if (-not $match.Success) { continue }
            $parsed = [DateTimeOffset]::MinValue
            if (-not [DateTimeOffset]::TryParse($match.Groups[1].Value, [ref]$parsed)) { continue }
            $rows++
            $utc = $parsed.UtcDateTime
            if ($null -eq $firstUtc -or $utc -lt $firstUtc) { $firstUtc = $utc }
            if ($null -eq $lastUtc -or $utc -gt $lastUtc) { $lastUtc = $utc }
            $provider = $match.Groups[2].Value
            if (-not $byProvider.ContainsKey($provider)) {
                $byProvider[$provider] = @{ rows = 0; first = $utc; last = $utc }
            }
            $entry = $byProvider[$provider]
            $entry.rows++
            if ($utc -lt $entry.first) { $entry.first = $utc }
            if ($utc -gt $entry.last) { $entry.last = $utc }
        }
    }
    $providers = @($byProvider.GetEnumerator() | Sort-Object -Property Key | ForEach-Object {
            [pscustomobject]@{
                provider = $_.Key
                rows     = $_.Value.rows
                firstUtc = $_.Value.first.ToString('o')
                lastUtc  = $_.Value.last.ToString('o')
            }
        })
    return [pscustomobject]@{
        path      = $Path
        rows      = $rows
        firstUtc  = if ($firstUtc) { $firstUtc.ToString('o') } else { $null }
        lastUtc   = if ($lastUtc) { $lastUtc.ToString('o') } else { $null }
        providers = $providers
    }
}

function Get-DmsLogSignalCount {
    <#
    .SYNOPSIS
    Counts log lines matching Pattern, optionally skipping lines that also match
    ExcludePattern - e.g. counting TimeoutException lines while excluding background
    WorkerPollFailed noise that cannot establish request-path correlation.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Pattern,
        [string] $ExcludePattern
    )

    if (-not (Test-Path -LiteralPath $Path)) { return 0 }
    $matched = @(Select-String -LiteralPath $Path -Pattern $Pattern -SimpleMatch)
    if ($ExcludePattern) {
        $matched = @($matched | Where-Object { $_.Line -notmatch [regex]::Escape($ExcludePattern) })
    }
    return $matched.Count
}

function Get-DmsE1RunClassification {
    <#
    .SYNOPSIS
    Classifies one E1 run under spec section 3.2. Deliberately takes NO log-signal
    inputs: R-500 requires the 500 to correlate with the profile-path/JWKS evidence by
    request, path, or stack, and no automated count establishes that (a background
    worker timeout in the same window must never promote a 500). Any 500 therefore
    returns 'R-500-pending-manual' for manual classification against the correlated
    logs. R-slow evidence is provisional per the spec.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][bool] $Has500,

        # Null when the run produced no summary (e.g. the harness failed before rounds).
        [object] $MaxP99Ms = $null
    )

    if ($Has500) { return 'R-500-pending-manual' }
    if ($null -eq $MaxP99Ms) { return 'unclassified(no summary)' }
    if ([double]$MaxP99Ms -ge 5000) { return 'R-slow(provisional)' }
    return 'neither'
}

function Test-DmsWindowCovered {
    <#
    .SYNOPSIS
    True when the capture summary's first/last samples enclose [StartUtc, EndUtc].
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][pscustomobject] $Summary,
        [Parameter(Mandatory)][DateTime] $StartUtc,
        [Parameter(Mandatory)][DateTime] $EndUtc
    )

    if (-not $Summary.firstUtc -or -not $Summary.lastUtc) {
        return $false
    }
    $first = ([DateTimeOffset]::Parse($Summary.firstUtc)).UtcDateTime
    $last = ([DateTimeOffset]::Parse($Summary.lastUtc)).UtcDateTime
    return ($first -le $StartUtc.ToUniversalTime()) -and ($last -ge $EndUtc.ToUniversalTime())
}

Export-ModuleMember -Function Start-DmsSamplerSet, Wait-DmsSamplerSetReady, Stop-DmsSamplerSet, Remove-DmsSamplerSet, Get-DmsSamplerValidation, Test-DmsCaptureHasData, Get-DmsDelimitedCaptureSummary, Get-DmsJsonlCaptureSummary, Get-DmsCsvCaptureSummary, Get-DmsLivemetricsSummary, Test-DmsWindowCovered, Get-DmsLogSignalCount, Get-DmsE1RunClassification
