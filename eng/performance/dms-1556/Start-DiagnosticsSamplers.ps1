# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
DMS-1556 samplers: PostgreSQL activity/IO, docker stats, host disk, and CMS live metrics.

.DESCRIPTION
Runs for -DurationSeconds and writes raw capture files under -OutputDirectory:

  <label>-pg-activity.csv   pg_stat_activity grouped by state/wait_event_type/backend_type
                            plus per-database numbackends, sampled every ~250 ms.
  <label>-pg-io.csv         pg_stat_io (active rows) and pg_stat_bgwriter as row_to_json,
                            sampled every ~1 s.
  <label>-docker-stats.jsonl docker stats stream (one JSON object per container per
                            refresh), each line prefixed with a host UTC timestamp.
  <label>-host-disk.csv     Windows PhysicalDisk(_Total) counters at 1 s (dev profile
                            only; the CI-runner equivalent is /proc/diskstats).
  <label>-livemetrics.json  dotnet-monitor /livemetrics capture for the window, when
                            -MonitorBaseUrl is reachable.

Start this immediately before a burst; it runs the samplers as background jobs, waits
out the window, then stops and collects them.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Parameters are read inside Start-Job script blocks through $using:, which the analyzer does not track.')]
[CmdletBinding()]
param(
    [ValidateRange(5, 3600)]
    [int] $DurationSeconds = 90,

    [string] $Label = ('samplers-{0}' -f ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))),

    [string] $PgContainerName = 'dms-postgresql',

    [string] $PgUser = 'postgres',

    [string[]] $StatsContainers = @('ed-fi-api-config-service', 'dms-postgresql'),

    # dotnet-monitor sidecar base URL (local-config-diagnostics.yml). Empty disables the
    # live-metrics capture.
    [string] $MonitorBaseUrl = 'http://localhost:52323',

    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
$activityPath = Join-Path $OutputDirectory "$Label-pg-activity.csv"
$ioPath = Join-Path $OutputDirectory "$Label-pg-io.csv"
$statsPath = Join-Path $OutputDirectory "$Label-docker-stats.jsonl"
$diskPath = Join-Path $OutputDirectory "$Label-host-disk.csv"
$liveMetricsPath = Join-Path $OutputDirectory "$Label-livemetrics.json"

# Sampler loops run inside the postgres container (alpine sh + busybox sleep, which
# accepts fractional seconds), so per-tick overhead is one local psql call, not one
# docker exec round-trip.
$activityTicks = $DurationSeconds * 4
$activitySql = @"
COPY (
  SELECT now()::text, 'activity', coalesce(state,'(none)'), coalesce(wait_event_type,'(none)'), backend_type, count(*)::text
  FROM pg_stat_activity GROUP BY 1,2,3,4,5
  UNION ALL
  SELECT now()::text, 'numbackends', datname, '', '', numbackends::text
  FROM pg_stat_database WHERE datname IS NOT NULL AND numbackends > 0
) TO STDOUT WITH (FORMAT csv)
"@ -replace '\r?\n', ' '
$activityLoop = "i=0; while [ `$i -lt $activityTicks ]; do psql -U $PgUser -d postgres -Atc `"$activitySql`"; i=`$((i+1)); sleep 0.25; done"

$ioTicks = $DurationSeconds
$ioSql = @"
COPY (
  SELECT now()::text, 'pg_stat_io', row_to_json(x)::text FROM pg_stat_io x
  WHERE reads > 0 OR writes > 0 OR fsyncs > 0
  UNION ALL
  SELECT now()::text, 'pg_stat_bgwriter', row_to_json(b)::text FROM pg_stat_bgwriter b
) TO STDOUT WITH (FORMAT csv)
"@ -replace '\r?\n', ' '
$ioLoop = "i=0; while [ `$i -lt $ioTicks ]; do psql -U $PgUser -d postgres -Atc `"$ioSql`"; i=`$((i+1)); sleep 1; done"

$jobs = @()

$jobs += Start-Job -Name "$Label-pg-activity" -ScriptBlock {
    & docker exec $using:PgContainerName sh -c $using:activityLoop 2>&1 |
        Set-Content -LiteralPath $using:activityPath -Encoding utf8
}

$jobs += Start-Job -Name "$Label-pg-io" -ScriptBlock {
    & docker exec $using:PgContainerName sh -c $using:ioLoop 2>&1 |
        Set-Content -LiteralPath $using:ioPath -Encoding utf8
}

# docker stats streams a refresh roughly every 500 ms when stdout is not a TTY; the job
# is stopped from here once the window closes.
$jobs += Start-Job -Name "$Label-docker-stats" -ScriptBlock {
    $containers = $using:StatsContainers
    & docker stats --format '{{json .}}' @containers 2>$null | ForEach-Object {
        # docker stats emits terminal control sequences (e.g. erase-line) even when piped.
        $line = ($_ -replace "`e\[[0-9;]*[A-Za-z]", '').Trim()
        if ($line.StartsWith('{') -and $line.EndsWith('}')) {
            '{{"tsUtc":"{0}","stat":{1}}}' -f ([DateTime]::UtcNow.ToString('o')), $line
        }
    } | Add-Content -LiteralPath $using:statsPath -Encoding utf8
}

if ($IsWindows) {
    $jobs += Start-Job -Name "$Label-host-disk" -ScriptBlock {
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
        } | Export-Csv -LiteralPath $using:diskPath -NoTypeInformation -Encoding utf8
    }
}
else {
    Write-Output 'Not Windows: host disk counters skipped (use /proc/diskstats on the runner).'
}

if ($MonitorBaseUrl) {
    # The sidecar's target process is not marked default, so every endpoint needs an
    # explicit pid; resolve it up front and skip the capture when the sidecar is down.
    $monitorPid = $null
    try {
        $processes = @(Invoke-RestMethod -Uri "$MonitorBaseUrl/processes" -TimeoutSec 10)
        if ($processes.Count -eq 1) {
            $monitorPid = $processes[0].pid
        }
        else {
            Write-Output "livemetrics skipped: expected one process at $MonitorBaseUrl, found $($processes.Count)."
        }
    }
    catch {
        Write-Output "livemetrics skipped: $MonitorBaseUrl unreachable ($($_.Exception.Message))."
    }

    if ($null -ne $monitorPid) {
        $jobs += Start-Job -Name "$Label-livemetrics" -ScriptBlock {
            try {
                Invoke-WebRequest -Uri "$($using:MonitorBaseUrl)/livemetrics?pid=$($using:monitorPid)&durationSeconds=$($using:DurationSeconds)" `
                    -OutFile $using:liveMetricsPath -TimeoutSec (($using:DurationSeconds) + 30)
            }
            catch {
                Set-Content -LiteralPath $using:liveMetricsPath -Value ('livemetrics capture failed: {0}' -f $_.Exception.Message) -Encoding utf8
            }
        }
    }
}

Write-Output "Sampling for $DurationSeconds seconds ($($jobs.Count) samplers)..."
# The container-side loops self-terminate; the grace period lets them flush before the
# streaming jobs are stopped.
Start-Sleep -Seconds ($DurationSeconds + 5)

foreach ($job in $jobs) {
    if ($job.State -eq 'Running') {
        Stop-Job -Job $job
    }
    Receive-Job -Job $job -ErrorAction SilentlyContinue | Out-Null
    Remove-Job -Job $job -Force
}

$produced = @($activityPath, $ioPath, $statsPath, $diskPath, $liveMetricsPath) | Where-Object { Test-Path -LiteralPath $_ }
Write-Output 'Sampler outputs:'
$produced | ForEach-Object { Write-Output "  $_" }
