# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
DMS-1556 samplers, standalone wrapper over dms-1556-samplers.psm1.

.DESCRIPTION
Starts the sampler set, verifies every capture is producing data, runs for
-DurationSeconds, stops the set, and prints the validation report. Writes under
-OutputDirectory:

  <label>-pg-activity.psv   pg_stat_activity grouped by datname/usename/application_name/
                            state/wait_event_type/backend_type plus per-database
                            numbackends, ~250 ms, over ONE persistent connection
                            (application_name=dms1556-sampler-activity).
  <label>-pg-io.psv         pg_stat_io (active rows) and pg_stat_bgwriter as row_to_json,
                            ~1 s, over one persistent connection
                            (application_name=dms1556-sampler-io).
  <label>-docker-stats.jsonl docker stats stream, timestamped per line.
  <label>-host-disk.csv     Windows PhysicalDisk(_Total) counters at 1 s (dev profile;
                            the CI-runner equivalent is /proc/diskstats).
  <label>-livemetrics.json  dotnet-monitor /livemetrics for the window - the capture that
                            supplies the runtime counters (System.Runtime,
                            Microsoft.AspNetCore.Hosting, Npgsql).

For coordinated evidence runs, prefer Invoke-CmsProfileBurst.ps1 -WithSamplers, which
sequences restart, new-process discovery, sampler readiness, burst, and coverage checks.
This wrapper is for ad-hoc observation; without a burst window it validates data presence
and attribution, not coverage.

Exits non-zero when a required capture failed, so a skipped or broken sampler never looks
like a successful invocation.
#>
[CmdletBinding()]
param(
    [ValidateRange(10, 3600)]
    [int] $DurationSeconds = 90,

    [string] $Label = ('samplers-{0}' -f ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))),

    [string] $PgContainerName = 'dms-postgresql',

    [string] $PgUser = 'postgres',

    [string[]] $StatsContainers = @('ed-fi-api-config-service', 'dms-postgresql'),

    # dotnet-monitor sidecar base URL (local-config-diagnostics.yml). Empty disables the
    # live-metrics capture.
    [string] $MonitorBaseUrl = 'http://localhost:52323',

    # Treat the dotnet-monitor capture as required rather than best-effort.
    [switch] $RequireMonitor,

    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'dms-1556-samplers.psm1') -Force

$state = $null
try {
    $state = Start-DmsSamplerSet -Label $Label -OutputDirectory $OutputDirectory `
        -DurationSeconds $DurationSeconds -PgContainerName $PgContainerName -PgUser $PgUser `
        -StatsContainers $StatsContainers -MonitorBaseUrl $MonitorBaseUrl -RequireMonitor:$RequireMonitor

    Wait-DmsSamplerSetReady -State $state
    Write-Output "Samplers ready; sampling for $DurationSeconds seconds..."
    Start-Sleep -Seconds $DurationSeconds

    $report = Stop-DmsSamplerSet -State $state
    $report | ConvertTo-Json -Depth 6 | Write-Output

    if (@($report.requiredFailures).Count -gt 0) {
        Write-Error "Required sampler captures failed: $(@($report.requiredFailures) -join '; ')"
        exit 1
    }
}
finally {
    if ($null -ne $state) {
        # No-op after a successful Stop-DmsSamplerSet; otherwise stops any still-owned
        # sampler jobs without masking the original error.
        Remove-DmsSamplerSet -State $state
    }
}
