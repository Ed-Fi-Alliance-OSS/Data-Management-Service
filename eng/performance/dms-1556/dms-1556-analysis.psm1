# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
DMS-1556 capture analysis (step 0.4 onward): pure functions that slice the sampler
captures and container logs to one time window (typically one burst round) and summarize
what each capture measured there. Every function takes file paths and a window, reads
nothing else, and changes nothing, so each is checkable against synthetic or retained
captures.

Clock domains: round windows and docker-stats/host-disk samples use the host clock;
PostgreSQL (log, pg-activity, pg-io), CMS logs, and livemetrics use the Docker VM clock.
The driver records the measured offset per point; windows are padded rather than
shifted, so the offset must stay well below the padding for the slicing to hold.

Sampling limits the functions do NOT hide: livemetrics counters are 1 s snapshots or
1 s rates, pg-activity is 250 ms, docker stats ~500 ms, pg_stat_io is cumulative and
flushed by backends at most about once per second. A sub-second round therefore yields
few samples; instantaneous peaks (busy connections, active backends) can fall between
samples, while persistent state (idle pooled connections, numbackends) and exact
event logs (log_connections) do not. Each summary reports its sample count.
#>

Set-StrictMode -Version Latest

$script:Invariant = [System.Globalization.CultureInfo]::InvariantCulture
$script:SamplerApplicationPrefix = 'dms1556-sampler-'
$script:PgLogPattern = '^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) UTC \[(?<pid>\d+)\] (?<level>[A-Z]+):\s+(?<msg>.*)$'
# /livemetrics streams RFC 7464 JSON text sequences: every record starts with 0x1E.
$script:LivemetricsPattern = '^\x1E?\{"timestamp":"(?<ts>[^"]+)","provider":"(?<provider>[^"]+)","name":"(?<name>[^"]*)".*?"value":(?<value>[-0-9.eE+]+)'

function ConvertTo-DmsUtc {
    <#
    .SYNOPSIS
    Parses an ISO-8601 or PostgreSQL now()::text timestamp into a UTC DateTime; $null
    when the text does not parse.
    #>
    [CmdletBinding()]
    [OutputType([object])]
    param([string] $Text)

    $parsed = [DateTimeOffset]::MinValue
    if ([DateTimeOffset]::TryParse($Text, $script:Invariant, [System.Globalization.DateTimeStyles]::AssumeUniversal, [ref]$parsed)) {
        return $parsed.UtcDateTime
    }
    return $null
}

function Test-DmsInWindow {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)][DateTime] $Utc,
        [Parameter(Mandatory)][DateTime] $StartUtc,
        [Parameter(Mandatory)][DateTime] $EndUtc
    )

    return ($Utc -ge $StartUtc) -and ($Utc -le $EndUtc)
}

function ConvertFrom-DmsDockerSize {
    <#
    .SYNOPSIS
    Converts a docker stats size ("0B", "578kB", "1.2MB", "3GiB") into bytes.
    #>
    [CmdletBinding()]
    [OutputType([double])]
    param([Parameter(Mandatory)][string] $Text)

    if ($Text.Trim() -notmatch '^(?<n>[0-9.]+)\s*(?<u>[A-Za-z]*)$') { return 0.0 }
    $multipliers = @{
        'B' = 1.0; 'kB' = 1e3; 'MB' = 1e6; 'GB' = 1e9; 'TB' = 1e12
        'KiB' = 1024.0; 'MiB' = [Math]::Pow(1024, 2); 'GiB' = [Math]::Pow(1024, 3); 'TiB' = [Math]::Pow(1024, 4)
    }
    $unit = if ($Matches.u) { $Matches.u } else { 'B' }
    $multiplier = if ($multipliers.ContainsKey($unit)) { $multipliers[$unit] } else { 1.0 }
    return [double]::Parse($Matches.n, $script:Invariant) * $multiplier
}

function Get-DmsPgLogEvent {
    <#
    .SYNOPSIS
    Parses a PostgreSQL container log (log_line_prefix '%m [%p] ') into typed events:
    received / authorized / disconnection (with client host joined by backend pid),
    checkpoint-start / checkpoint-complete, slow-statement (log_min_duration_statement),
    lock-wait, error (ERROR/FATAL/PANIC), other.
    #>
    [CmdletBinding()]
    [OutputType([object[]])]
    param([Parameter(Mandatory)][string] $Path)

    $events = [System.Collections.Generic.List[object]]::new()
    if (-not (Test-Path -LiteralPath $Path)) { return , @() }
    $hostByPid = @{}
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        $match = [regex]::Match($line, $script:PgLogPattern)
        if (-not $match.Success) { continue }
        $tsUtc = [DateTime]::SpecifyKind(
            [DateTime]::ParseExact($match.Groups['ts'].Value, 'yyyy-MM-dd HH:mm:ss.fff', $script:Invariant), [DateTimeKind]::Utc)
        $backendPid = [int]$match.Groups['pid'].Value
        $level = $match.Groups['level'].Value
        $message = $match.Groups['msg'].Value
        $kind = 'other'
        $clientHost = ''
        $database = ''
        $user = ''
        $applicationName = ''
        $durationMs = $null

        if ($level -in @('ERROR', 'FATAL', 'PANIC')) {
            $kind = 'error'
            $clientHost = [string]($hostByPid[$backendPid] ?? '')
        }
        elseif ($message -match '^connection received: host=(?<h>\S+)') {
            $kind = 'received'
            $clientHost = $Matches.h
            $hostByPid[$backendPid] = $clientHost
        }
        elseif ($message -match '^connection authorized: user=(?<u>\S+) database=(?<d>\S+)(?: application_name=(?<a>\S+))?') {
            $kind = 'authorized'
            $user = $Matches.u
            $database = $Matches.d
            $applicationName = if ($Matches.ContainsKey('a')) { $Matches.a } else { '' }
            $clientHost = [string]($hostByPid[$backendPid] ?? '')
        }
        elseif ($message -match '^disconnection: session time: \S+ user=(?<u>\S+) database=(?<d>\S+) host=(?<h>\S+)') {
            $kind = 'disconnection'
            $user = $Matches.u
            $database = $Matches.d
            $clientHost = $Matches.h
        }
        elseif ($message -match '^checkpoint starting') { $kind = 'checkpoint-start' }
        elseif ($message -match '^checkpoint complete') { $kind = 'checkpoint-complete' }
        elseif ($message -match '^duration: (?<ms>[0-9.]+) ms') {
            $kind = 'slow-statement'
            $durationMs = [double]::Parse($Matches.ms, $script:Invariant)
        }
        elseif ($message -match 'still waiting for|acquired .* after') { $kind = 'lock-wait' }

        $events.Add([pscustomobject]@{
                tsUtc           = $tsUtc
                pid             = $backendPid
                level           = $level
                kind            = $kind
                host            = $clientHost
                database        = $database
                user            = $user
                applicationName = $applicationName
                durationMs      = $durationMs
                message         = $message
            })
    }
    return , $events.ToArray()
}

function Measure-DmsPgConnectionWindow {
    <#
    .SYNOPSIS
    M-conn from log_connections for one window: physical connections the CMS container
    created to the CMS database (matched by client host AND database), CMS disconnections,
    everything else created in the window grouped by database/application/host, sampler
    connections excluded by the dms1556-sampler- application-name prefix, plus the
    PostgreSQL error, slow-statement, lock-wait, and checkpoint events in the window.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Events,
        [Parameter(Mandatory)][DateTime] $StartUtc,
        [Parameter(Mandatory)][DateTime] $EndUtc,
        [Parameter(Mandatory)][string] $Database,
        [Parameter(Mandatory)][string] $ClientHost
    )

    $inWindow = @($Events | Where-Object { Test-DmsInWindow -Utc $_.tsUtc -StartUtc $StartUtc -EndUtc $EndUtc })
    $authorized = @($inWindow | Where-Object { $_.kind -eq 'authorized' })
    $samplers = @($authorized | Where-Object { $_.applicationName.StartsWith($script:SamplerApplicationPrefix) })
    $cms = @($authorized | Where-Object { $_.host -eq $ClientHost -and $_.database -eq $Database -and -not $_.applicationName.StartsWith($script:SamplerApplicationPrefix) })
    $cmsPids = @($cms | ForEach-Object { $_.pid })
    $others = @($authorized | Where-Object { $_.pid -notin $cmsPids -and -not $_.applicationName.StartsWith($script:SamplerApplicationPrefix) })
    $otherGroups = [ordered]@{}
    foreach ($group in ($others | Group-Object -Property { '{0}/{1}/{2}' -f $_.database, $(if ($_.applicationName) { $_.applicationName } else { '(none)' }), $_.host })) {
        $otherGroups[$group.Name] = $group.Count
    }
    $errors = @($inWindow | Where-Object { $_.kind -eq 'error' })
    $errorGroups = [ordered]@{}
    foreach ($group in ($errors | Group-Object -Property { '{0}: {1}' -f $_.level, ($_.message -replace '\d+', 'N') })) {
        $errorGroups[$group.Name] = $group.Count
    }
    $slow = @($inWindow | Where-Object { $_.kind -eq 'slow-statement' })

    return [pscustomobject]@{
        cmsConnectionsCreated   = $cms.Count
        cmsDisconnections       = @($inWindow | Where-Object { $_.kind -eq 'disconnection' -and $_.host -eq $ClientHost -and $_.database -eq $Database }).Count
        firstCmsCreateUtc       = if ($cms.Count -gt 0) { ($cms | Sort-Object tsUtc)[0].tsUtc.ToString('o') } else { $null }
        lastCmsCreateUtc        = if ($cms.Count -gt 0) { ($cms | Sort-Object tsUtc)[-1].tsUtc.ToString('o') } else { $null }
        otherConnectionsCreated = $otherGroups
        samplerConnectionsExcluded = $samplers.Count
        pgErrors                = $errorGroups
        slowStatements          = $slow.Count
        slowStatementMaxMs      = if ($slow.Count -gt 0) { ($slow | Measure-Object -Property durationMs -Maximum).Maximum } else { $null }
        lockWaits               = @($inWindow | Where-Object { $_.kind -eq 'lock-wait' }).Count
        checkpointsStarted      = @($inWindow | Where-Object { $_.kind -eq 'checkpoint-start' }).Count
        checkpointsCompleted    = @($inWindow | Where-Object { $_.kind -eq 'checkpoint-complete' }).Count
    }
}

function Get-DmsPgActivityWindowSummary {
    <#
    .SYNOPSIS
    Summarizes the pg-activity capture for one database over one window: per-sample
    numbackends (peak, and the last sample before the window as a baseline), client
    backends by state, and ACTIVE client backends by wait_event_type summed over samples
    (backend-samples). Sampler sessions are excluded by application-name prefix.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Database,
        [Parameter(Mandatory)][DateTime] $StartUtc,
        [Parameter(Mandatory)][DateTime] $EndUtc
    )

    $samples = @{}
    $baseline = $null
    $baselineUtc = [DateTime]::MinValue
    if (Test-Path -LiteralPath $Path) {
        foreach ($line in [System.IO.File]::ReadLines($Path)) {
            $fields = $line.Split('|')
            if ($fields.Count -lt 9 -or $fields[2] -ne $Database) { continue }
            $utc = ConvertTo-DmsUtc -Text $fields[0]
            if ($null -eq $utc) { continue }
            $count = [int]$fields[8]
            if ($fields[1] -eq 'numbackends' -and $utc -lt $StartUtc -and $utc -gt $baselineUtc) {
                $baselineUtc = $utc
                $baseline = $count
            }
            if (-not (Test-DmsInWindow -Utc $utc -StartUtc $StartUtc -EndUtc $EndUtc)) { continue }
            $key = $utc.Ticks
            if (-not $samples.ContainsKey($key)) {
                $samples[$key] = @{ numbackends = 0; active = 0; idle = 0; idleInTransaction = 0; waits = @{} }
            }
            $sample = $samples[$key]
            if ($fields[1] -eq 'numbackends') {
                $sample.numbackends = $count
                continue
            }
            if ($fields[7] -ne 'client backend' -or $fields[4].StartsWith($script:SamplerApplicationPrefix)) { continue }
            switch ($fields[5]) {
                'active' {
                    $sample.active += $count
                    $sample.waits[$fields[6]] = [int]($sample.waits[$fields[6]] ?? 0) + $count
                }
                'idle' { $sample.idle += $count }
                'idle in transaction' { $sample.idleInTransaction += $count }
            }
        }
    }

    $values = @($samples.Values)
    $waitTotals = [ordered]@{}
    foreach ($sample in $values) {
        foreach ($entry in $sample.waits.GetEnumerator()) {
            $waitTotals[$entry.Key] = [int]($waitTotals[$entry.Key] ?? 0) + $entry.Value
        }
    }
    return [pscustomobject]@{
        samples                 = $values.Count
        baselineNumbackends     = $baseline
        peakNumbackends         = if ($values.Count -gt 0) { ($values | ForEach-Object { $_.numbackends } | Measure-Object -Maximum).Maximum } else { $null }
        peakActive              = if ($values.Count -gt 0) { ($values | ForEach-Object { $_.active } | Measure-Object -Maximum).Maximum } else { $null }
        peakIdleInTransaction   = if ($values.Count -gt 0) { ($values | ForEach-Object { $_.idleInTransaction } | Measure-Object -Maximum).Maximum } else { $null }
        activeBackendSamplesByWait = $waitTotals
    }
}

function Get-DmsLivemetricsWindowSummary {
    <#
    .SYNOPSIS
    Summarizes the dotnet-monitor livemetrics capture over one window: System.Runtime
    thread-pool counters (thread count with its pre-window baseline, queue length,
    completed items per interval, lock contention), CPU, exceptions, ASP.NET Core
    current requests, and the Npgsql pool counters for one database (busy, idle, and
    busy+idle paired by nearest timestamp).
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][DateTime] $StartUtc,
        [Parameter(Mandatory)][DateTime] $EndUtc,
        [Parameter(Mandatory)][string] $PoolDatabase
    )

    $series = @{}
    $baselineThreads = $null
    $baselineThreadsUtc = [DateTime]::MinValue
    $busyName = 'Busy Connections'
    $idleName = 'Idle Connections'
    $poolMarker = "Database=$PoolDatabase;"
    if (Test-Path -LiteralPath $Path) {
        foreach ($line in [System.IO.File]::ReadLines($Path)) {
            $match = [regex]::Match($line, $script:LivemetricsPattern)
            if (-not $match.Success) { continue }
            $utc = ConvertTo-DmsUtc -Text $match.Groups['ts'].Value
            if ($null -eq $utc) { continue }
            $name = $match.Groups['name'].Value
            if ($name.StartsWith($busyName) -or $name.StartsWith($idleName)) {
                if (-not $name.Contains($poolMarker)) { continue }
                $name = if ($name.StartsWith($busyName)) { 'pool-busy' } else { 'pool-idle' }
            }
            $value = [double]::Parse($match.Groups['value'].Value, $script:Invariant)
            if ($name -eq 'threadpool-thread-count' -and $utc -lt $StartUtc -and $utc -gt $baselineThreadsUtc) {
                $baselineThreadsUtc = $utc
                $baselineThreads = $value
            }
            if (-not (Test-DmsInWindow -Utc $utc -StartUtc $StartUtc -EndUtc $EndUtc)) { continue }
            if (-not $series.ContainsKey($name)) { $series[$name] = [System.Collections.Generic.List[object]]::new() }
            $series[$name].Add([pscustomobject]@{ utc = $utc; value = $value })
        }
    }

    function Get-SeriesMax([string] $Name) {
        if (-not $series.ContainsKey($Name)) { return $null }
        return ($series[$Name] | Measure-Object -Property value -Maximum).Maximum
    }
    function Get-SeriesSum([string] $Name) {
        if (-not $series.ContainsKey($Name)) { return $null }
        return ($series[$Name] | Measure-Object -Property value -Sum).Sum
    }

    $poolTotalMax = $null
    if ($series.ContainsKey('pool-busy') -and $series.ContainsKey('pool-idle')) {
        foreach ($busy in $series['pool-busy']) {
            $nearest = $series['pool-idle'] | Sort-Object { [Math]::Abs(($_.utc - $busy.utc).TotalMilliseconds) } | Select-Object -First 1
            if ([Math]::Abs(($nearest.utc - $busy.utc).TotalMilliseconds) -le 500) {
                $total = $busy.value + $nearest.value
                if ($null -eq $poolTotalMax -or $total -gt $poolTotalMax) { $poolTotalMax = $total }
            }
        }
    }

    return [pscustomobject]@{
        samples                   = if ($series.ContainsKey('threadpool-thread-count')) { $series['threadpool-thread-count'].Count } else { 0 }
        threadPoolThreadsBaseline = $baselineThreads
        threadPoolThreadsMax      = Get-SeriesMax 'threadpool-thread-count'
        threadPoolQueueMax        = Get-SeriesMax 'threadpool-queue-length'
        threadPoolCompletedMax    = Get-SeriesMax 'threadpool-completed-items-count'
        lockContentionTotal       = Get-SeriesSum 'monitor-lock-contention-count'
        cpuUsageMax               = Get-SeriesMax 'cpu-usage'
        exceptionsTotal           = Get-SeriesSum 'exception-count'
        currentRequestsMax        = Get-SeriesMax 'current-requests'
        poolBusyMax               = Get-SeriesMax 'pool-busy'
        poolIdleMax               = Get-SeriesMax 'pool-idle'
        poolTotalMax              = $poolTotalMax
    }
}

function Get-DmsDockerStatsWindowSummary {
    <#
    .SYNOPSIS
    Summarizes one container's docker stats samples over one window: CPU% max/mean
    (100% = one CPU) and the growth of the cumulative BlockIO read/write counters
    between the first and last samples inside the window.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Container,
        [Parameter(Mandatory)][DateTime] $StartUtc,
        [Parameter(Mandatory)][DateTime] $EndUtc
    )

    $rows = [System.Collections.Generic.List[object]]::new()
    if (Test-Path -LiteralPath $Path) {
        foreach ($line in [System.IO.File]::ReadLines($Path)) {
            if ($line -notmatch '"tsUtc":"(?<ts>[^"]+)"' ) { continue }
            $utc = ConvertTo-DmsUtc -Text $Matches.ts
            if ($null -eq $utc -or -not (Test-DmsInWindow -Utc $utc -StartUtc $StartUtc -EndUtc $EndUtc)) { continue }
            if ($line -notmatch ('"Name":"' + [regex]::Escape($Container) + '"')) { continue }
            if ($line -notmatch '"CPUPerc":"(?<cpu>[0-9.]+)%"') { continue }
            $cpu = [double]::Parse($Matches.cpu, $script:Invariant)
            $read = 0.0
            $write = 0.0
            if ($line -match '"BlockIO":"(?<r>[^"/]+)/(?<w>[^"]+)"') {
                $read = ConvertFrom-DmsDockerSize -Text $Matches.r
                $write = ConvertFrom-DmsDockerSize -Text $Matches.w
            }
            $rows.Add([pscustomobject]@{ utc = $utc; cpu = $cpu; read = $read; write = $write })
        }
    }
    $sorted = @($rows | Sort-Object utc)
    return [pscustomobject]@{
        samples           = $sorted.Count
        cpuPercentMax     = if ($sorted.Count -gt 0) { ($sorted | Measure-Object -Property cpu -Maximum).Maximum } else { $null }
        cpuPercentMean    = if ($sorted.Count -gt 0) { [Math]::Round(($sorted | Measure-Object -Property cpu -Average).Average, 2) } else { $null }
        blockReadBytes    = if ($sorted.Count -gt 1) { $sorted[-1].read - $sorted[0].read } else { $null }
        blockWriteBytes   = if ($sorted.Count -gt 1) { $sorted[-1].write - $sorted[0].write } else { $null }
    }
}

function Get-DmsPgIoWindowDelta {
    <#
    .SYNOPSIS
    Growth of the cumulative pg_stat_io (summed over all rows) and pg_stat_bgwriter
    counters between the last sample at or before StartUtc and the first sample at or
    after EndUtc. Backends flush pending I/O stats at most about once per second, so the
    caller should pad EndUtc; the result records which samples bracket the window.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][DateTime] $StartUtc,
        [Parameter(Mandatory)][DateTime] $EndUtc
    )

    $totals = @{}
    if (Test-Path -LiteralPath $Path) {
        foreach ($line in [System.IO.File]::ReadLines($Path)) {
            $parts = $line.Split('|', 3)
            if ($parts.Count -lt 3) { continue }
            $utc = ConvertTo-DmsUtc -Text $parts[0]
            if ($null -eq $utc) { continue }
            $key = $utc.Ticks
            if (-not $totals.ContainsKey($key)) {
                $totals[$key] = @{ utc = $utc; reads = 0.0; writes = 0.0; extends = 0.0; fsyncs = 0.0; checkpoints = 0.0; buffersCheckpoint = 0.0; buffersBackend = 0.0; buffersClean = 0.0 }
            }
            $entry = $totals[$key]
            try { $row = $parts[2] | ConvertFrom-Json } catch { continue }
            if ($parts[1] -eq 'pg_stat_io') {
                foreach ($field in @('reads', 'writes', 'extends', 'fsyncs')) {
                    $value = $row.$field
                    if ($null -ne $value) { $entry[$field] += [double]$value }
                }
            }
            elseif ($parts[1] -eq 'pg_stat_bgwriter') {
                $entry.checkpoints = [double]$row.checkpoints_timed + [double]$row.checkpoints_req
                $entry.buffersCheckpoint = [double]$row.buffers_checkpoint
                $entry.buffersBackend = [double]$row.buffers_backend
                $entry.buffersClean = [double]$row.buffers_clean
            }
        }
    }
    $ordered = @($totals.Values | Sort-Object { $_.utc })
    $before = @($ordered | Where-Object { $_.utc -le $StartUtc }) | Select-Object -Last 1
    $after = @($ordered | Where-Object { $_.utc -ge $EndUtc }) | Select-Object -First 1
    if ($null -eq $before -or $null -eq $after) {
        return [pscustomobject]@{ bracketed = $false; beforeUtc = $null; afterUtc = $null; reads = $null; writes = $null; extends = $null; fsyncs = $null; checkpoints = $null; buffersCheckpoint = $null; buffersBackend = $null; buffersClean = $null }
    }
    $result = [ordered]@{ bracketed = $true; beforeUtc = $before.utc.ToString('o'); afterUtc = $after.utc.ToString('o') }
    foreach ($field in @('reads', 'writes', 'extends', 'fsyncs', 'checkpoints', 'buffersCheckpoint', 'buffersBackend', 'buffersClean')) {
        $result[$field] = $after[$field] - $before[$field]
    }
    return [pscustomobject]$result
}

function Get-DmsHostDiskWindowSummary {
    <#
    .SYNOPSIS
    Summarizes the Windows host PhysicalDisk(_Total) counters over one window (the
    Docker VM's virtual disk I/O lands here): peak transfers/s, peak average latency
    per transfer (ms), and minimum idle percentage.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][DateTime] $StartUtc,
        [Parameter(Mandatory)][DateTime] $EndUtc
    )

    $transfers = [System.Collections.Generic.List[double]]::new()
    $latency = [System.Collections.Generic.List[double]]::new()
    $idle = [System.Collections.Generic.List[double]]::new()
    if (Test-Path -LiteralPath $Path) {
        foreach ($line in [System.IO.File]::ReadLines($Path)) {
            if ($line -notmatch '^"(?<ts>[^"]+)","(?<counter>[^"]+)","(?<value>[^"]+)"$') { continue }
            $utc = ConvertTo-DmsUtc -Text $Matches.ts
            if ($null -eq $utc -or -not (Test-DmsInWindow -Utc $utc -StartUtc $StartUtc -EndUtc $EndUtc)) { continue }
            $value = [double]::Parse($Matches.value, $script:Invariant)
            switch -Wildcard ($Matches.counter) {
                '*disk transfers/sec' { $transfers.Add($value) }
                '*avg. disk sec/transfer' { $latency.Add($value * 1000.0) }
                '*% idle time' { $idle.Add($value) }
            }
        }
    }
    return [pscustomobject]@{
        samples             = $transfers.Count
        transfersPerSecMax  = if ($transfers.Count -gt 0) { ($transfers | Measure-Object -Maximum).Maximum } else { $null }
        latencyMsMax        = if ($latency.Count -gt 0) { [Math]::Round(($latency | Measure-Object -Maximum).Maximum, 2) } else { $null }
        idlePercentMin      = if ($idle.Count -gt 0) { ($idle | Measure-Object -Minimum).Minimum } else { $null }
    }
}

function Get-DmsCmsLogWindowSummary {
    <#
    .SYNOPSIS
    Summarizes CMS JSON (Serilog compact) log lines in one window: 'Request finished'
    lines by path family and status with the server-side maximum elapsed time, and every
    Warning/Error/Fatal line classified by SCOPE, not by cause:
      - request-scoped: the line carries Properties.RequestPath (emitted inside a request);
      - background: WorkerPollFailed or another line without a request path whose
        SourceContext is a hosted/background component;
      - unattributed: no request path and not identifiable as background.
    Scope is where the line was emitted; it does NOT establish that the line caused any
    response status. Status correlation needs the trace id of the affected response.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][DateTime] $StartUtc,
        [Parameter(Mandatory)][DateTime] $EndUtc
    )

    $finished = [ordered]@{}
    $maxElapsedMs = $null
    $nonHarnessRequests = 0
    $scoped = [ordered]@{}
    $background = [ordered]@{}
    $unattributed = [ordered]@{}
    if (Test-Path -LiteralPath $Path) {
        foreach ($line in [System.IO.File]::ReadLines($Path)) {
            # The raw timestamp text is used, never the parsed property: ConvertFrom-Json
            # turns ISO strings into local-time DateTimes, which would shift the window.
            if ($line -notmatch '^\{"Timestamp":"(?<ts>[^"]+)"') { continue }
            $timestampText = $Matches.ts
            $isFinished = $line.Contains('"MessageTemplate":"Request finished')
            $isProblem = $line -match '"Level":"(Warning|Error|Fatal)"'
            if (-not $isFinished -and -not $isProblem) { continue }
            try { $entry = $line | ConvertFrom-Json } catch { continue }
            $utc = ConvertTo-DmsUtc -Text $timestampText
            if ($null -eq $utc -or -not (Test-DmsInWindow -Utc $utc -StartUtc $StartUtc -EndUtc $EndUtc)) { continue }
            $properties = $entry.PSObject.Properties['Properties']
            $props = if ($properties) { $properties.Value } else { $null }
            $requestPath = if ($props -and $props.PSObject.Properties['RequestPath']) { [string]$props.RequestPath } else { '' }
            if (-not $requestPath -and $props -and $props.PSObject.Properties['Path']) { $requestPath = [string]$props.Path }
            $family = $requestPath -replace '/\d+(?=/|$)', '/{id}'

            if ($isFinished) {
                $status = if ($props -and $props.PSObject.Properties['StatusCode']) { [string]$props.StatusCode } else { '?' }
                $key = "$family $status"
                $finished[$key] = [int]($finished[$key] ?? 0) + 1
                if ($props -and $props.PSObject.Properties['ElapsedMilliseconds']) {
                    $elapsed = [double]$props.ElapsedMilliseconds
                    if ($null -eq $maxElapsedMs -or $elapsed -gt $maxElapsedMs) { $maxElapsedMs = $elapsed }
                }
                if ($family -notmatch '/v3/profiles/\{id\}$|/connect/token$|/health$') { $nonHarnessRequests++ }
                continue
            }

            $template = [string]$entry.MessageTemplate
            $exceptionProperty = $entry.PSObject.Properties['Exception']
            $exceptionType = if ($exceptionProperty -and $exceptionProperty.Value) { ([string]$exceptionProperty.Value -split '[\s:]', 2)[0] } else { '' }
            $eventName = if ($props -and $props.PSObject.Properties['Event']) { [string]$props.Event } else { '' }
            $source = if ($props -and $props.PSObject.Properties['SourceContext']) { [string]$props.SourceContext } else { '' }
            $label = '{0} | {1}{2}' -f $entry.Level, $(if ($eventName) { $eventName } else { $template }), $(if ($exceptionType) { " | $exceptionType" } else { '' })
            if ($requestPath) {
                $key = "$label | $family"
                $scoped[$key] = [int]($scoped[$key] ?? 0) + 1
            }
            elseif ($eventName -eq 'WorkerPollFailed' -or $source -match '\.Jobs\.|BackgroundService|HostedService|TokenCleanup') {
                $background[$label] = [int]($background[$label] ?? 0) + 1
            }
            else {
                $key = "$label | $source"
                $unattributed[$key] = [int]($unattributed[$key] ?? 0) + 1
            }
        }
    }
    $sum = { param($map) $total = 0; foreach ($v in $map.Values) { $total += $v }; $total }
    return [pscustomobject]@{
        requestsFinished      = $finished
        serverElapsedMsMax    = $maxElapsedMs
        nonHarnessRequests    = $nonHarnessRequests
        requestScopedProblems = $scoped
        requestScopedTotal    = & $sum $scoped
        backgroundProblems    = $background
        backgroundTotal       = & $sum $background
        unattributedProblems  = $unattributed
        unattributedTotal     = & $sum $unattributed
    }
}

function Get-DmsContainerResourceRecord {
    <#
    .SYNOPSIS
    CPU limit and thread-pool/processor-count/diagnostics environment of one container.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)][string] $ContainerName)

    $nanoCpus = docker inspect $ContainerName --format '{{.HostConfig.NanoCpus}}' 2>$null
    $envLines = docker inspect $ContainerName --format '{{range .Config.Env}}{{println .}}{{end}}' 2>$null
    return [pscustomobject]@{
        container = $ContainerName
        cpuLimit  = if ($nanoCpus -and $nanoCpus -ne '0') { [Math]::Round([long]$nanoCpus / 1e9, 2) } else { 'unlimited' }
        env       = @($envLines | Where-Object { $_ -match '^(DOTNET_ThreadPool|DOTNET_PROCESSOR_COUNT|DOTNET_Diagnostic|Serilog__MinimumLevel)' })
    }
}

function Measure-DmsPgHandshakeWindow {
    <#
    .SYNOPSIS
    Connection-establishment timing for one client host over one window: for every
    backend whose 'connection received' falls in the window, the time from received to
    'connection authorized' (joined by backend pid; the SCRAM exchange plus backend
    start-up), and offsets from StartUtc. Backends rejected before authorization (e.g.
    53300) are counted separately. Optionally returns cumulative received/authorized
    counts at given instants, to align with stack captures.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Events,
        [Parameter(Mandatory)][DateTime] $StartUtc,
        [Parameter(Mandatory)][DateTime] $EndUtc,
        [Parameter(Mandatory)][string] $ClientHost,
        [DateTime[]] $AtUtc = @()
    )

    $received = @($Events | Where-Object { $_.kind -eq 'received' -and $_.host -eq $ClientHost -and (Test-DmsInWindow -Utc $_.tsUtc -StartUtc $StartUtc -EndUtc $EndUtc) })
    $authorizedByPid = @{}
    $errorPids = @{}
    foreach ($pgEvent in $Events) {
        if ($pgEvent.kind -eq 'authorized' -and -not $authorizedByPid.ContainsKey($pgEvent.pid)) { $authorizedByPid[$pgEvent.pid] = $pgEvent.tsUtc }
        if ($pgEvent.kind -eq 'error') { $errorPids[$pgEvent.pid] = $true }
    }
    $gaps = [System.Collections.Generic.List[double]]::new()
    $authorizedOffsets = [System.Collections.Generic.List[double]]::new()
    $authorizedTimes = [System.Collections.Generic.List[DateTime]]::new()
    $rejected = 0
    foreach ($r in $received) {
        if ($authorizedByPid.ContainsKey($r.pid) -and $authorizedByPid[$r.pid] -ge $r.tsUtc) {
            $gaps.Add(($authorizedByPid[$r.pid] - $r.tsUtc).TotalMilliseconds)
            $authorizedOffsets.Add(($authorizedByPid[$r.pid] - $StartUtc).TotalSeconds)
            $authorizedTimes.Add($authorizedByPid[$r.pid])
        }
        elseif ($errorPids.ContainsKey($r.pid)) { $rejected++ }
    }
    function Get-Quantile([System.Collections.Generic.List[double]] $Values, [double] $Q) {
        if ($Values.Count -eq 0) { return $null }
        $sorted = @($Values | Sort-Object)
        return [Math]::Round($sorted[[int][Math]::Min($sorted.Count - 1, [Math]::Floor($Q * $sorted.Count))], 1)
    }
    $receivedOffsets = [System.Collections.Generic.List[double]]::new()
    foreach ($r in $received) { $receivedOffsets.Add(($r.tsUtc - $StartUtc).TotalSeconds) }
    $alignment = @(foreach ($instant in $AtUtc) {
            [pscustomobject]@{
                atUtc      = $instant.ToString('o')
                received   = @($received | Where-Object { $_.tsUtc -le $instant }).Count
                authorized = @($authorizedTimes | Where-Object { $_ -le $instant }).Count
            }
        })
    return [pscustomobject]@{
        received                = $received.Count
        authorized              = $gaps.Count
        rejectedBeforeAuthorization = $rejected
        receivedOffsetP50Seconds = Get-Quantile $receivedOffsets 0.5
        receivedToAuthorizedP50Ms = Get-Quantile $gaps 0.5
        receivedToAuthorizedMaxMs = if ($gaps.Count -gt 0) { [Math]::Round(($gaps | Measure-Object -Maximum).Maximum, 1) } else { $null }
        authorizedOffsetMinSeconds = if ($authorizedOffsets.Count -gt 0) { [Math]::Round(($authorizedOffsets | Measure-Object -Minimum).Minimum, 2) } else { $null }
        authorizedOffsetP50Seconds = Get-Quantile $authorizedOffsets 0.5
        alignment               = $alignment
    }
}

function Get-DmsStackSummary {
    <#
    .SYNOPSIS
    Classifies the threads of one dotnet-monitor /stacks text capture by what they are
    doing (first matching rule wins):
      - resolver-wait: a synchronous Task wait (Task.InternalWait) with
        JsonWebTokenHandler.ValidateSignature below it, i.e. IdentityModel's synchronous
        IssuerSigningKeyResolver call blocking on a Task (F1). resolverFrameVisible
        counts the subset whose CMS resolver lambda frame is explicitly present; when it
        is absent the wait appears directly under ValidateSignature (a JIT-inlined lambda
        is the likely reason, not established here);
      - other-sync-wait: any other synchronous Task wait (Program.Main excluded);
      - scram-compute: Npgsql SCRAM/PBKDF2/HMAC frames on the stack;
      - npgsql-active: other Npgsql frames;
      - threadpool-idle: a worker parked in the thread pool's semaphore;
      - runtime-infrastructure: named runtime/Kestrel/diagnostics threads and Main;
      - other.
    Also returns the thread-pool worker count and the most common frame signatures.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)][string] $Path)

    $counts = [ordered]@{
        'resolver-wait' = 0; 'other-sync-wait' = 0; 'scram-compute' = 0; 'npgsql-active' = 0
        'threadpool-idle' = 0; 'runtime-infrastructure' = 0; 'other' = 0
    }
    $resolverFrameVisible = 0
    $workers = 0
    $signatures = @{}
    $threads = 0
    if (Test-Path -LiteralPath $Path) {
        $text = [System.IO.File]::ReadAllText($Path)
        foreach ($block in ($text -split '(?m)^(?=Thread: )')) {
            # ReadAllText strips the capture's UTF-8 BOM, so blocks start at 'Thread: '.
            if ($block -notmatch '^Thread: ') { continue }
            $threads++
            $lines = @($block -split "`n")
            $header = $lines[0]
            $frames = @($lines | Select-Object -Skip 1 | ForEach-Object { ($_.Trim() -replace '^[^!]+!', '') } | Where-Object { $_ -and $_ -ne '[NativeFrame]' })
            $joined = $frames -join "`n"
            if ($header -match '\.NET TP Worker') { $workers++ }
            $category = if ($joined -match 'Task\.InternalWait' -and $joined -match 'JsonWebTokenHandler\.ValidateSignature') {
                if ($joined -match 'EdFi\.DmsConfigurationService\.[^\n]*(ConfigureIdentityProvider|AddJwtAuthentication)[^\n]*>b__') { $resolverFrameVisible++ }
                'resolver-wait'
            }
            elseif ($joined -match 'Task\.InternalWait' -and $joined -notmatch 'Program\.<Main>') { 'other-sync-wait' }
            elseif ($joined -match 'Npgsql' -and $joined -match 'Scram|Pbkdf2|Rfc2898|HMAC|Hmac') { 'scram-compute' }
            elseif ($joined -match 'Npgsql') { 'npgsql-active' }
            elseif ($frames.Count -gt 0 -and $joined -match 'LowLevelLifoSemaphore|PortableThreadPool\+WorkerThread') { 'threadpool-idle' }
            elseif ($header -match 'Kestrel|\.NET (Sockets|Timer|TP Gate|File Watcher|Counter|EventPipe|Finalizer)' -or $joined -match 'Program\.<Main>|CounterGroup|GateThread|TimerQueue|SocketAsyncEngine|FileSystemWatcher|Heartbeat') { 'runtime-infrastructure' }
            else { 'other' }
            $counts[$category]++
            $signature = '{0}: {1}' -f $category, (($frames | Select-Object -First 3) -join ' < ')
            $signatures[$signature] = [int]($signatures[$signature] ?? 0) + 1
        }
    }
    return [pscustomobject]@{
        path                 = $Path
        threads              = $threads
        threadPoolWorkers    = $workers
        categories           = $counts
        resolverFrameVisible = $resolverFrameVisible
        topSignatures        = @($signatures.GetEnumerator() | Sort-Object -Property Value -Descending | Select-Object -First 5 | ForEach-Object { '{0}x {1}' -f $_.Value, $_.Key })
    }
}

function Get-DmsLivemetricsValueAt {
    <#
    .SYNOPSIS
    The livemetrics sample of one counter nearest to an instant (within 1.5 s), used to
    align a stack capture with thread-pool and pool state. Counter names as in
    Get-DmsLivemetricsWindowSummary ('pool-busy' / 'pool-idle' for the given database).
    #>
    [CmdletBinding()]
    [OutputType([object])]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][DateTime] $AtUtc,
        [Parameter(Mandatory)][string] $Counter,
        [string] $PoolDatabase = 'edfi_datamanagementservice'
    )

    $best = $null
    $bestDistance = [double]::MaxValue
    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        $match = [regex]::Match($line, $script:LivemetricsPattern)
        if (-not $match.Success) { continue }
        $name = $match.Groups['name'].Value
        if ($Counter -in @('pool-busy', 'pool-idle')) {
            $prefix = if ($Counter -eq 'pool-busy') { 'Busy Connections' } else { 'Idle Connections' }
            if (-not ($name.StartsWith($prefix) -and $name.Contains("Database=$PoolDatabase;"))) { continue }
        }
        elseif ($name -ne $Counter) { continue }
        $utc = ConvertTo-DmsUtc -Text $match.Groups['ts'].Value
        if ($null -eq $utc) { continue }
        $distance = [Math]::Abs(($utc - $AtUtc).TotalSeconds)
        if ($distance -lt $bestDistance -and $distance -le 1.5) {
            $bestDistance = $distance
            $best = [double]::Parse($match.Groups['value'].Value, $script:Invariant)
        }
    }
    return $best
}

function ConvertTo-DmsUtcInstant {
    <#
    .SYNOPSIS
    UTC DateTime from a summary timestamp. ConvertFrom-Json already turns ISO strings
    into DateTime values, and casting those to [string] drops the fractional seconds
    that sub-second windows depend on, so DateTime values are converted directly.
    #>
    [CmdletBinding()]
    [OutputType([DateTime])]
    param([Parameter(Mandatory)][object] $Value)

    if ($Value -is [DateTime]) { return $Value.ToUniversalTime() }
    return ([DateTimeOffset]::Parse([string]$Value, $script:Invariant)).UtcDateTime
}

function ConvertTo-DmsFlatText {
    <#
    .SYNOPSIS
    Renders a map (dictionary or object) as "k=v; k=v" for flat CSV columns.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([object] $Map)

    if ($null -eq $Map) { return '' }
    $pairs = if ($Map -is [System.Collections.IDictionary]) { $Map.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" } }
    else { $Map.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" } }
    return (@($pairs) -join '; ')
}

function Measure-DmsVmClockOffset {
    <#
    .SYNOPSIS
    Docker VM clock minus host clock (ms), from the lowest-round-trip of five probes into
    the PostgreSQL container. docker exec start-up sits inside the round trip, so the
    midpoint estimate is biased positive; the magnitude is what matters for padding.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([string] $PgContainerName = 'dms-postgresql')

    $best = $null
    for ($i = 0; $i -lt 5; $i++) {
        $before = [DateTimeOffset]::UtcNow
        $epoch = docker exec $PgContainerName psql -U postgres -d postgres -Atc 'select extract(epoch from clock_timestamp())' 2>$null
        $after = [DateTimeOffset]::UtcNow
        if ($LASTEXITCODE -ne 0 -or -not $epoch) { continue }
        $roundTripMs = ($after - $before).TotalMilliseconds
        $midpointMs = ($before.ToUnixTimeMilliseconds() + $after.ToUnixTimeMilliseconds()) / 2.0
        $offsetMs = [double]::Parse([string]$epoch, $script:Invariant) * 1000.0 - $midpointMs
        if ($null -eq $best -or $roundTripMs -lt $best.roundTripMs) {
            $best = [pscustomobject]@{ offsetMs = [Math]::Round($offsetMs, 1); roundTripMs = [Math]::Round($roundTripMs, 1) }
        }
    }
    return $best
}

function Get-DmsContainerIpAddress {
    <#
    .SYNOPSIS
    First network IP address of a container (the client host PostgreSQL logs for it).
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string] $ContainerName)

    $addresses = docker inspect $ContainerName --format '{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}' 2>$null
    $first = @(([string]$addresses).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)) | Select-Object -First 1
    if (-not $first) { throw "Could not resolve the IP address of $ContainerName." }
    return $first
}

function Get-DmsE2RoundEvidence {
    <#
    .SYNOPSIS
    All per-round measurements for one timed round, each capture sliced with padding
    matched to its sampling: exact events (log_connections, CMS logs) use
    [start, end + EventPadSeconds]; 250 ms pg-activity uses the same; 1 s livemetrics,
    500 ms docker stats, and 1 s host disk use [start, end + CounterPadSeconds];
    cumulative pg_stat_io brackets [start, end + CounterPadSeconds].
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][DateTime] $StartUtc,
        [Parameter(Mandatory)][DateTime] $EndUtc,
        [Parameter(Mandatory)][hashtable] $Captures,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $PgEvents,
        [Parameter(Mandatory)][string] $CmsDatabase,
        [Parameter(Mandatory)][string] $CmsClientHost,
        [string] $CmsContainer = 'ed-fi-api-config-service',
        [string] $PgContainer = 'dms-postgresql',
        [double] $EventPadSeconds = 0.25,
        [double] $CounterPadSeconds = 1.5
    )

    $eventEnd = $EndUtc.AddSeconds($EventPadSeconds)
    $counterEnd = $EndUtc.AddSeconds($CounterPadSeconds)
    return [pscustomobject]@{
        connections = Measure-DmsPgConnectionWindow -Events $PgEvents -StartUtc $StartUtc -EndUtc $eventEnd -Database $CmsDatabase -ClientHost $CmsClientHost
        pgActivity  = Get-DmsPgActivityWindowSummary -Path $Captures.activity -Database $CmsDatabase -StartUtc $StartUtc -EndUtc $eventEnd
        livemetrics = Get-DmsLivemetricsWindowSummary -Path $Captures.livemetrics -StartUtc $StartUtc -EndUtc $counterEnd -PoolDatabase $CmsDatabase
        cmsStats    = Get-DmsDockerStatsWindowSummary -Path $Captures.stats -Container $CmsContainer -StartUtc $StartUtc -EndUtc $counterEnd
        pgStats     = Get-DmsDockerStatsWindowSummary -Path $Captures.stats -Container $PgContainer -StartUtc $StartUtc -EndUtc $counterEnd
        pgIo        = Get-DmsPgIoWindowDelta -Path $Captures.io -StartUtc $StartUtc -EndUtc $counterEnd
        hostDisk    = Get-DmsHostDiskWindowSummary -Path $Captures.disk -StartUtc $StartUtc -EndUtc $counterEnd
        cmsLog      = Get-DmsCmsLogWindowSummary -Path $Captures.cmsLog -StartUtc $StartUtc -EndUtc $eventEnd
    }
}

Export-ModuleMember -Function ConvertTo-DmsUtc, ConvertFrom-DmsDockerSize, Get-DmsPgLogEvent, Measure-DmsPgConnectionWindow, Get-DmsPgActivityWindowSummary, Get-DmsLivemetricsWindowSummary, Get-DmsDockerStatsWindowSummary, Get-DmsPgIoWindowDelta, Get-DmsHostDiskWindowSummary, Get-DmsCmsLogWindowSummary, Get-DmsContainerResourceRecord, Get-DmsE2RoundEvidence, Measure-DmsPgHandshakeWindow, Get-DmsStackSummary, Get-DmsLivemetricsValueAt, ConvertTo-DmsUtcInstant, ConvertTo-DmsFlatText, Measure-DmsVmClockOffset, Get-DmsContainerIpAddress
