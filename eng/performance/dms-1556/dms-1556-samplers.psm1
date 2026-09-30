# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
DMS-1556 diagnostics samplers as a module, so the burst harness can coordinate them
explicitly around a cold restart: start (against the CURRENT CMS process), verify every
capture is producing data before the burst, and verify afterwards that the captures cover
the whole burst window. Start-DiagnosticsSamplers.ps1 wraps these for standalone use.

PostgreSQL sampling uses ONE persistent connection per stream (psql \watch inside the
container) with a distinguishable application_name (dms1556-sampler-activity /
dms1556-sampler-io), and the activity rows retain datname/usename/application_name, so
sampler connections are self-identifying and excludable from M-conn instead of adding
~5 anonymous connections per second to the connection-creation evidence.
#>

Set-StrictMode -Version Latest

$script:DataLinePattern = '^\d{4}-\d{2}-\d{2}[ T]'

function Start-DmsSamplerSet {
    <#
    .SYNOPSIS
    Starts the DMS-1556 sampler jobs against the CURRENT CMS process and returns the
    state handle for Wait-DmsSamplerSetReady / Stop-DmsSamplerSet.
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
    $activityTicks = [int][Math]::Ceiling($DurationSeconds / 0.25)
    $activityQuery = "SELECT now()::text, 'activity', coalesce(datname,''), coalesce(usename,''), coalesce(application_name,''), coalesce(state,'(none)'), coalesce(wait_event_type,'(none)'), backend_type, count(*)::text FROM pg_stat_activity GROUP BY 1,2,3,4,5,6,7,8 UNION ALL SELECT now()::text, 'numbackends', datname, '', '', '', '', '', numbackends::text FROM pg_stat_database WHERE datname IS NOT NULL AND numbackends > 0"
    $activityInput = "$activityQuery`n\watch i=0.25 c=$activityTicks`n"

    $ioQuery = "SELECT now()::text, 'pg_stat_io', row_to_json(x)::text FROM pg_stat_io x WHERE reads > 0 OR writes > 0 OR fsyncs > 0 UNION ALL SELECT now()::text, 'pg_stat_bgwriter', row_to_json(b)::text FROM pg_stat_bgwriter b"
    $ioInput = "$ioQuery`n\watch i=1 c=$DurationSeconds`n"

    $jobs = @{}

    $jobs.activity = Start-Job -Name "$Label-pg-activity" -ScriptBlock {
        $using:activityInput |
            & docker exec -i $using:PgContainerName psql -U $using:PgUser -d 'dbname=postgres application_name=dms1556-sampler-activity' -X -q -A -t -F '|' 2>&1 |
            Set-Content -LiteralPath ($using:paths).activity -Encoding utf8
    }

    $jobs.io = Start-Job -Name "$Label-pg-io" -ScriptBlock {
        $using:ioInput |
            & docker exec -i $using:PgContainerName psql -U $using:PgUser -d 'dbname=postgres application_name=dms1556-sampler-io' -X -q -A -t -F '|' 2>&1 |
            Set-Content -LiteralPath ($using:paths).io -Encoding utf8
    }

    # docker stats streams a refresh roughly every 500 ms when stdout is not a TTY; the
    # job is stopped by Stop-DmsSamplerSet once the window closes.
    $jobs.stats = Start-Job -Name "$Label-docker-stats" -ScriptBlock {
        $containers = $using:StatsContainers
        & docker stats --format '{{json .}}' @containers 2>$null | ForEach-Object {
            # docker stats emits terminal control sequences (e.g. erase-line) even piped.
            $line = ($_ -replace "`e\[[0-9;]*[A-Za-z]", '').Trim()
            if ($line.StartsWith('{') -and $line.EndsWith('}')) {
                '{{"tsUtc":"{0}","stat":{1}}}' -f ([DateTime]::UtcNow.ToString('o')), $line
            }
        } | Add-Content -LiteralPath ($using:paths).stats -Encoding utf8
    }

    if ($IsWindows) {
        $jobs.disk = Start-Job -Name "$Label-host-disk" -ScriptBlock {
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
        $jobs.livemetrics = Start-Job -Name "$Label-livemetrics" -ScriptBlock {
            try {
                Invoke-WebRequest -Uri "$($using:MonitorBaseUrl)/livemetrics?pid=$($using:monitorPid)&durationSeconds=$($using:DurationSeconds)" `
                    -OutFile ($using:paths).livemetrics -TimeoutSec (($using:DurationSeconds) + 30)
            }
            catch {
                Set-Content -LiteralPath ($using:paths).livemetrics -Value ('livemetrics capture failed: {0}' -f $_.Exception.Message) -Encoding utf8
            }
        }
    }

    return @{
        Label           = $Label
        Paths           = $paths
        Jobs            = $jobs
        MonitorBaseUrl  = $MonitorBaseUrl
        MonitorPid      = $monitorPid
        MonitorRequired = [bool]$RequireMonitor
        DurationSeconds = $DurationSeconds
        StartedUtc      = [DateTime]::UtcNow
    }
}

function Wait-DmsSamplerSetReady {
    <#
    .SYNOPSIS
    Blocks until every started capture has produced at least one data line, so a burst
    launched afterwards is guaranteed to fall inside every sampler's window. Throws when
    a capture fails to produce data (a skipped or failed sampler must never look like a
    successful invocation).
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
        if (-not (Test-DmsCaptureHasData -Path $State.Paths.stats -Pattern '^\{"tsUtc"')) {
            $pending.Add('docker-stats')
        }
        if ($null -ne $State.MonitorPid -and -not (Test-DmsCaptureHasData -Path $State.Paths.livemetrics -Pattern '"timestamp"')) {
            $pending.Add('livemetrics')
        }

        if ($pending.Count -eq 0) {
            return
        }

        $captureNameByJobKey = @{ activity = 'pg-activity'; stats = 'docker-stats'; livemetrics = 'livemetrics' }
        foreach ($entry in $State.Jobs.GetEnumerator()) {
            $captureName = $captureNameByJobKey[$entry.Key]
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
    Waits out the sampler window, stops the streaming jobs, and validates the captures.
    With a burst window supplied, coverage is required: the run's runtime evidence must
    span [BurstStartUtc, BurstEndUtc]. Returns a report whose requiredFailures list is
    non-empty when the evidence is unusable; the caller decides whether to throw.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Stops the local diagnostics sampling jobs this module started; ShouldProcess prompting would break unattended burst coordination.')]
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [hashtable] $State,

        [DateTime] $BurstStartUtc,

        [DateTime] $BurstEndUtc
    )

    $requiredFailures = [System.Collections.Generic.List[string]]::new()
    $warnings = [System.Collections.Generic.List[string]]::new()

    # The bounded jobs (\watch counts, livemetrics duration) self-terminate; wait out the
    # remainder of the window plus tool-startup slack before declaring them stuck.
    $elapsed = ([DateTime]::UtcNow - $State.StartedUtc).TotalSeconds
    $remaining = [Math]::Max(5, [int]($State.DurationSeconds - $elapsed) + 30)
    $bounded = @($State.Jobs.GetEnumerator() | Where-Object { $_.Key -in @('activity', 'io', 'livemetrics', 'disk') } | ForEach-Object { $_.Value })
    if ($bounded.Count -gt 0) {
        $null = Wait-Job -Job $bounded -Timeout $remaining
    }

    foreach ($entry in $State.Jobs.GetEnumerator()) {
        $job = $entry.Value
        if ($job.State -eq 'Running') {
            if ($entry.Key -in @('activity', 'io', 'livemetrics')) {
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

    $activity = Get-DmsDelimitedCaptureSummary -Path $State.Paths.activity
    $io = Get-DmsDelimitedCaptureSummary -Path $State.Paths.io
    $stats = Get-DmsJsonlCaptureSummary -Path $State.Paths.stats -TimestampProperty 'tsUtc'
    $livemetrics = $null
    $providers = @()
    if ($null -ne $State.MonitorPid) {
        $livemetrics = Get-DmsJsonlCaptureSummary -Path $State.Paths.livemetrics -TimestampProperty 'timestamp'
        if (Test-Path -LiteralPath $State.Paths.livemetrics) {
            $providers = @(Select-String -LiteralPath $State.Paths.livemetrics -Pattern '"provider":"([^"]+)"' -AllMatches |
                    ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        }
    }

    $hasWindow = ($PSBoundParameters.ContainsKey('BurstStartUtc') -and $PSBoundParameters.ContainsKey('BurstEndUtc'))

    if ($activity.rows -eq 0) {
        $requiredFailures.Add('pg-activity produced no data rows')
    }
    elseif ($hasWindow -and -not (Test-DmsWindowCovered -Summary $activity -StartUtc $BurstStartUtc -EndUtc $BurstEndUtc)) {
        $requiredFailures.Add("pg-activity does not cover the burst window (samples $($activity.firstUtc)..$($activity.lastUtc))")
    }
    if ($activity.rows -gt 0 -and $activity.samplerRows -eq 0) {
        $requiredFailures.Add('pg-activity rows never show the dms1556-sampler application_name; sampler connections are not attributable/excludable')
    }

    if ($State.MonitorRequired) {
        if ($null -eq $livemetrics -or $livemetrics.rows -eq 0) {
            $requiredFailures.Add('livemetrics produced no samples')
        }
        else {
            if ($hasWindow -and -not (Test-DmsWindowCovered -Summary $livemetrics -StartUtc $BurstStartUtc -EndUtc $BurstEndUtc)) {
                $requiredFailures.Add("livemetrics does not cover the burst window (samples $($livemetrics.firstUtc)..$($livemetrics.lastUtc))")
            }
            foreach ($required in @('System.Runtime', 'Microsoft.AspNetCore.Hosting', 'Npgsql')) {
                if ($providers -notcontains $required) {
                    $requiredFailures.Add("livemetrics is missing the '$required' provider; it is the capture that must supply those counters")
                }
            }
        }
    }

    if ($io.rows -eq 0) { $warnings.Add('pg-io produced no data rows.') }
    if ($stats.rows -eq 0) { $warnings.Add('docker-stats produced no data rows.') }

    return [pscustomobject]@{
        label            = $State.Label
        monitorPid       = $State.MonitorPid
        durationSeconds  = $State.DurationSeconds
        files            = $State.Paths
        pgActivity       = $activity
        pgIo             = $io
        dockerStats      = $stats
        livemetrics      = $livemetrics
        # /livemetrics is the capture that supplies the runtime counters: System.Runtime
        # (thread pool, lock contention, CPU, GC, working set), ASP.NET Core Hosting
        # (current/failed requests) and Npgsql (connection pool usage).
        livemetricsProviders = $providers
        requiredFailures = @($requiredFailures)
        warnings         = @($warnings)
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

Export-ModuleMember -Function Start-DmsSamplerSet, Wait-DmsSamplerSetReady, Stop-DmsSamplerSet, Test-DmsCaptureHasData, Get-DmsDelimitedCaptureSummary, Get-DmsJsonlCaptureSummary, Test-DmsWindowCovered
