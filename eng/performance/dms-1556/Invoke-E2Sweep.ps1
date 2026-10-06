# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
DMS-1556 step 0.4 driver: the E2 warm concurrency sweep with per-round M-conn, pool,
PostgreSQL, disk, and thread-pool evidence.

.DESCRIPTION
Runs -Repetitions sweeps over -Points ("TxN" = TotalRequests x MaxConcurrency) against
the running dms-local stack. Odd repetitions run the points in the listed order, even
repetitions in reverse, so slow drift in the host cannot masquerade as a concurrency
trend. Every point is independent of the points before it:

  1. Warm-up (untimed, recorded separately): CMS restart (token minted first, as in E1),
     then 87 requests at -WarmupConcurrency (default 1) covering every seeded profile, so
     JIT, the Npgsql data source, the key paths, and PostgreSQL buffers are warm while the
     CMS pool holds only what a serial pass needs.
  2. Timed burst: -Rounds rounds at T/N with coordinated samplers and body validation,
     separated by -InterRoundDelaySeconds so each round's samples are separable. Round 1
     therefore measures pool GROWTH from a warm process; later rounds measure the
     steady-state pool (Npgsql keeps idle connections for 300 s).
  3. Container logs for the point window, CMS client IP (for M-conn attribution), clock
     offset between host and Docker VM, per-round analysis (dms-1556-analysis.psm1), then
     one dump OUTSIDE the timed windows for Worker Min Limit (Q17) unless -SkipDumps.

Writes e2-<profile>-index.json (environment, every point with its per-round evidence) and
e2-<profile>-rounds.csv (one flat row per timed round). A point whose harness run fails is
recorded and the sweep continues. Nothing here classifies a mechanism: the outputs are
measurements, and any non-200 is flagged for manual correlation.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Parameters are read by nested functions through script scope; the analyzer does not track that usage.')]
[CmdletBinding()]
param(
    # Evidence label for the resource profile in effect; verified against the container
    # CPU limits before the sweep starts (2 CPUs on CMS and PostgreSQL for p-runner-approx).
    [ValidateSet('p-dev', 'p-runner-approx')]
    [string] $ResourceProfile = 'p-runner-approx',

    [ValidateRange(1, 10)]
    [int] $Repetitions = 3,

    [ValidatePattern('^\d+x\d+$')]
    [string[]] $Points = @('87x4', '87x8', '87x16', '87x32', '87x64', '87x87', '256x128'),

    [ValidateRange(1, 20)]
    [int] $Rounds = 5,

    [ValidateRange(0, 60)]
    [int] $InterRoundDelaySeconds = 3,

    [ValidateRange(1, 87)]
    [int] $WarmupConcurrency = 1,

    [ValidateRange(60, 3600)]
    [int] $SamplerDurationSeconds = 240,

    [string] $CmsContainerName = 'ed-fi-api-config-service',

    [string] $PgContainerName = 'dms-postgresql',

    [string] $CmsDatabase = 'edfi_datamanagementservice',

    [string[]] $SamplerStatsContainers = @('ed-fi-api-config-service', 'dms-postgresql', 'ed-fi-api'),

    [string] $MonitorBaseUrl = 'http://localhost:52323',

    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),

    [switch] $SkipDumps
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
Import-Module (Join-Path $PSScriptRoot 'dms-1556-analysis.psm1') -Force
$harness = Join-Path $PSScriptRoot 'Invoke-CmsProfileBurst.ps1'

function Get-WorkloadLabel {
    [CmdletBinding()]
    [OutputType([string])]
    param([int] $Total, [int] $Concurrency)

    if ($Total -eq 87 -and $Concurrency -eq 87) { return 'catalog-87x87' }
    if ($Total -eq 256 -and $Concurrency -eq 128) { return 'stress-256x128' }
    return "workload-${Total}x${Concurrency}"
}

function Measure-VmClockOffset {
    # Docker VM clock minus host clock, from the lowest-round-trip of five probes. The
    # docker exec startup sits inside the round trip, so the midpoint estimate is biased
    # toward a positive offset; the magnitude is what matters for window padding.
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param()

    $best = $null
    for ($i = 0; $i -lt 5; $i++) {
        $before = [DateTimeOffset]::UtcNow
        $epoch = docker exec $PgContainerName psql -U postgres -d postgres -Atc 'select extract(epoch from clock_timestamp())' 2>$null
        $after = [DateTimeOffset]::UtcNow
        if ($LASTEXITCODE -ne 0 -or -not $epoch) { continue }
        $roundTripMs = ($after - $before).TotalMilliseconds
        $midpointMs = ($before.ToUnixTimeMilliseconds() + $after.ToUnixTimeMilliseconds()) / 2.0
        $offsetMs = [double]::Parse([string]$epoch, [System.Globalization.CultureInfo]::InvariantCulture) * 1000.0 - $midpointMs
        if ($null -eq $best -or $roundTripMs -lt $best.roundTripMs) {
            $best = [pscustomobject]@{ offsetMs = [Math]::Round($offsetMs, 1); roundTripMs = [Math]::Round($roundTripMs, 1) }
        }
    }
    return $best
}

function Get-CmsClientHost {
    [CmdletBinding()]
    [OutputType([string])]
    param()

    $addresses = docker inspect $CmsContainerName --format '{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}' 2>$null
    $first = @(([string]$addresses).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)) | Select-Object -First 1
    if (-not $first) { throw "Could not resolve the IP address of $CmsContainerName." }
    return $first
}

function Get-LatestSummary {
    [CmdletBinding()]
    [OutputType([object])]
    param([Parameter(Mandatory)][string] $Label)

    $file = Get-ChildItem $OutputDirectory -Filter "$Label-*-summary.json" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $file) { return $null }
    return Get-Content $file.FullName -Raw | ConvertFrom-Json
}

function ConvertTo-UtcInstant {
    # ConvertFrom-Json already turns the summaries' ISO timestamps into DateTime values;
    # casting those to [string] would drop the fractional seconds that sub-second round
    # windows depend on, so DateTime values are converted directly.
    [CmdletBinding()]
    [OutputType([DateTime])]
    param([Parameter(Mandatory)][object] $Value)

    if ($Value -is [DateTime]) { return $Value.ToUniversalTime() }
    return ([DateTimeOffset]::Parse([string]$Value, [System.Globalization.CultureInfo]::InvariantCulture)).UtcDateTime
}

function ConvertTo-FlatText {
    # Renders an ordered map as "k=v; k=v" for the flat CSV.
    [CmdletBinding()]
    [OutputType([string])]
    param([object] $Map)

    if ($null -eq $Map) { return '' }
    $pairs = if ($Map -is [System.Collections.IDictionary]) { $Map.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" } }
    else { $Map.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" } }
    return (@($pairs) -join '; ')
}

# --- Environment record and profile guard -------------------------------------------
$cmsResources = Get-DmsContainerResourceRecord -ContainerName $CmsContainerName
$pgResources = Get-DmsContainerResourceRecord -ContainerName $PgContainerName
$expectedCpu = if ($ResourceProfile -eq 'p-runner-approx') { 2 } else { 'unlimited' }
if ([string]$cmsResources.cpuLimit -ne [string]$expectedCpu -or [string]$pgResources.cpuLimit -ne [string]$expectedCpu) {
    throw "Resource profile '$ResourceProfile' expects CPU limit '$expectedCpu' on CMS and PostgreSQL; found CMS=$($cmsResources.cpuLimit), PostgreSQL=$($pgResources.cpuLimit). Recompose the stack first."
}
$pgSettings = docker exec $PgContainerName psql -U postgres -d postgres -Atc "select name || '=' || setting from pg_settings where name in ('max_connections','superuser_reserved_connections','shared_buffers','checkpoint_timeout','max_wal_size') order by name" 2>$null
$cmsConnectionKeys = @(docker inspect $CmsContainerName --format '{{range .Config.Env}}{{println .}}{{end}}' 2>$null |
        Where-Object { $_ -like 'DatabaseSettings__DatabaseConnection=*' } |
        ForEach-Object { ($_ -replace '^[^=]+=', '').Split(';', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { ($_ -split '=', 2)[0].Trim() } })
$otherContainers = @(docker ps --format '{{.Names}}' 2>$null | Where-Object { $_ -notin @($CmsContainerName, $PgContainerName, 'ed-fi-api', 'cms-dotnet-monitor') })
$environmentRecord = [pscustomobject]@{
    resourceProfile       = $ResourceProfile
    capturedUtc           = [DateTime]::UtcNow.ToString('o')
    hostProcessorCount    = [Environment]::ProcessorCount
    dockerVm              = (docker info --format 'NCPU={{.NCPU}} MemTotal={{.MemTotal}} Kernel={{.KernelVersion}}' 2>$null)
    cms                   = $cmsResources
    postgres              = $pgResources
    postgresSettings      = @($pgSettings)
    cmsConnectionStringKeys = $cmsConnectionKeys
    otherRunningContainers = $otherContainers
    points                = $Points
    rounds                = $Rounds
    interRoundDelaySeconds = $InterRoundDelaySeconds
    warmupConcurrency     = $WarmupConcurrency
}
Write-Output "E2 sweep, profile ${ResourceProfile}: CMS cpu=$($cmsResources.cpuLimit), PG cpu=$($pgResources.cpuLimit); points $($Points -join ', '); $Repetitions repetitions"
if ($otherContainers.Count -gt 0) {
    Write-Output "Note: other containers running on the host (recorded as noise): $($otherContainers -join ', ')"
}

$pointRecords = [System.Collections.Generic.List[object]]::new()
$roundRows = [System.Collections.Generic.List[object]]::new()

for ($rep = 1; $rep -le $Repetitions; $rep++) {
    $order = if ($rep % 2 -eq 1) { @($Points) } else { @($Points)[($Points.Count - 1)..0] }
    $position = 0
    foreach ($point in $order) {
        $position++
        $parts = $point.Split('x')
        $total = [int]$parts[0]
        $concurrency = [int]$parts[1]
        $workload = Get-WorkloadLabel -Total $total -Concurrency $concurrency
        $baseLabel = "$workload-e2-$ResourceProfile-rep$rep"
        $warmLabel = "$baseLabel-warm"
        $burstLabel = "$baseLabel-burst"
        $pointStartUtc = [DateTime]::UtcNow.AddSeconds(-1)
        Write-Output "=== Rep $rep, position $position/$($order.Count): $workload ==="
        $clock = Measure-VmClockOffset

        $warmError = $null
        try {
            & $harness -TotalRequests 87 -MaxConcurrency $WarmupConcurrency -Rounds 1 -Cold -ValidateBodies `
                -CmsContainerName $CmsContainerName -OutputDirectory $OutputDirectory -RunLabel $warmLabel | Out-Null
        }
        catch {
            $warmError = $_.Exception.Message
            Write-Output "Warm-up failed: $warmError"
        }
        $warmSummary = Get-LatestSummary -Label $warmLabel

        $burstError = $null
        if (-not $warmError) {
            try {
                & $harness -TotalRequests $total -MaxConcurrency $concurrency -Rounds $Rounds -ValidateBodies `
                    -InterRoundDelaySeconds $InterRoundDelaySeconds -WithSamplers -SamplerDurationSeconds $SamplerDurationSeconds `
                    -MonitorBaseUrl $MonitorBaseUrl -SamplerStatsContainers $SamplerStatsContainers `
                    -CmsContainerName $CmsContainerName -OutputDirectory $OutputDirectory -RunLabel $burstLabel |
                    ForEach-Object { if ($_ -match '^\s+statuses:') { Write-Output "  $($_.Trim())" } }
            }
            catch {
                # Recorded, not fatal to the sweep: a coverage failure is itself evidence.
                $burstError = $_.Exception.Message
                Write-Output "Burst reported: $burstError"
            }
        }
        $burstSummary = if ($warmError) { $null } else { Get-LatestSummary -Label $burstLabel }

        $pointStartIso = $pointStartUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
        $cmsLogPath = Join-Path $OutputDirectory "$baseLabel-cms.log"
        $pgLogPath = Join-Path $OutputDirectory "$baseLabel-pg.log"
        docker logs --since $pointStartIso $CmsContainerName 2>&1 | Set-Content -LiteralPath $cmsLogPath -Encoding utf8
        docker logs --since $pointStartIso $PgContainerName 2>&1 | Set-Content -LiteralPath $pgLogPath -Encoding utf8
        $cmsHost = Get-CmsClientHost
        $pgEvents = Get-DmsPgLogEvent -Path $pgLogPath

        $warmConnections = $null
        if ($warmSummary) {
            $warmRound = @($warmSummary.roundSummaries)[0]
            $warmConnections = Measure-DmsPgConnectionWindow -Events $pgEvents -StartUtc (ConvertTo-UtcInstant $warmRound.startUtc) `
                -EndUtc (ConvertTo-UtcInstant $warmRound.endUtc).AddSeconds(0.25) -Database $CmsDatabase -ClientHost $cmsHost
        }

        $roundEvidence = [System.Collections.Generic.List[object]]::new()
        $preBurst = $null
        if ($burstSummary -and $burstSummary.samplerReport) {
            $files = $burstSummary.samplerReport.files
            $captures = @{
                activity    = [string]$files.activity
                io          = [string]$files.io
                stats       = [string]$files.stats
                disk        = [string]$files.disk
                livemetrics = [string]$files.livemetrics
                cmsLog      = $cmsLogPath
            }
            $burstStart = ConvertTo-UtcInstant $burstSummary.burstWindow.startUtc
            # Pre-burst state: what the warm-up and token mint left in the pool, read from
            # the samples just before round 1.
            $preBurst = Get-DmsE2RoundEvidence -StartUtc $burstStart.AddSeconds(-3) -EndUtc $burstStart.AddSeconds(-0.3) `
                -Captures $captures -PgEvents $pgEvents -CmsDatabase $CmsDatabase -CmsClientHost $cmsHost `
                -CmsContainer $CmsContainerName -PgContainer $PgContainerName -EventPadSeconds 0 -CounterPadSeconds 0
            foreach ($round in $burstSummary.roundSummaries) {
                $start = ConvertTo-UtcInstant $round.startUtc
                $end = ConvertTo-UtcInstant $round.endUtc
                $evidence = Get-DmsE2RoundEvidence -StartUtc $start -EndUtc $end -Captures $captures -PgEvents $pgEvents `
                    -CmsDatabase $CmsDatabase -CmsClientHost $cmsHost -CmsContainer $CmsContainerName -PgContainer $PgContainerName
                $roundEvidence.Add([pscustomobject]@{ round = [int]$round.round; summary = $round; evidence = $evidence })

                $statuses = $round.statusHistogram
                $ok = if ($statuses.PSObject.Properties['200']) { [int]$statuses.'200' } else { 0 }
                $roundRows.Add([pscustomobject][ordered]@{
                        rep                    = $rep
                        position               = $position
                        point                  = $point
                        workload               = $workload
                        round                  = [int]$round.round
                        startUtc               = $start.ToString('o')
                        windowMs               = [Math]::Round(($end - $start).TotalMilliseconds, 1)
                        requests               = [int]$round.requests
                        status200              = $ok
                        nonOk                  = [int]$round.requests - $ok
                        statuses               = ConvertTo-FlatText $statuses
                        bodyInvalid            = $round.bodyInvalidCount
                        p50Ms                  = $round.p50Ms
                        p95Ms                  = $round.p95Ms
                        p99Ms                  = $round.p99Ms
                        maxMs                  = $round.maxMs
                        peakOverlap            = $round.measuredPeakOverlap
                        cmsConnCreated         = $evidence.connections.cmsConnectionsCreated
                        cmsConnClosed          = $evidence.connections.cmsDisconnections
                        otherConnCreated       = ConvertTo-FlatText $evidence.connections.otherConnectionsCreated
                        pgErrors               = ConvertTo-FlatText $evidence.connections.pgErrors
                        slowStatements         = $evidence.connections.slowStatements
                        checkpoints            = $evidence.connections.checkpointsStarted
                        cmsBackendsBaseline    = $evidence.pgActivity.baselineNumbackends
                        cmsBackendsPeak        = $evidence.pgActivity.peakNumbackends
                        cmsActivePeak          = $evidence.pgActivity.peakActive
                        activeWaits            = ConvertTo-FlatText $evidence.pgActivity.activeBackendSamplesByWait
                        pgActivitySamples      = $evidence.pgActivity.samples
                        poolBusyMax            = $evidence.livemetrics.poolBusyMax
                        poolIdleMax            = $evidence.livemetrics.poolIdleMax
                        poolTotalMax           = $evidence.livemetrics.poolTotalMax
                        tpThreadsBaseline      = $evidence.livemetrics.threadPoolThreadsBaseline
                        tpThreadsMax           = $evidence.livemetrics.threadPoolThreadsMax
                        tpQueueMax             = $evidence.livemetrics.threadPoolQueueMax
                        tpCompletedMax         = $evidence.livemetrics.threadPoolCompletedMax
                        lockContention         = $evidence.livemetrics.lockContentionTotal
                        cmsCpuRuntimeMax       = $evidence.livemetrics.cpuUsageMax
                        livemetricsSamples     = $evidence.livemetrics.samples
                        cmsCpuDockerMax        = $evidence.cmsStats.cpuPercentMax
                        pgCpuDockerMax         = $evidence.pgStats.cpuPercentMax
                        pgCpuDockerMean        = $evidence.pgStats.cpuPercentMean
                        pgBlockWriteBytes      = $evidence.pgStats.blockWriteBytes
                        pgIoReads              = $evidence.pgIo.reads
                        pgIoWrites             = $evidence.pgIo.writes
                        pgIoFsyncs             = $evidence.pgIo.fsyncs
                        hostDiskTransfersMax   = $evidence.hostDisk.transfersPerSecMax
                        hostDiskLatencyMsMax   = $evidence.hostDisk.latencyMsMax
                        hostDiskIdleMin        = $evidence.hostDisk.idlePercentMin
                        serverElapsedMsMax     = $evidence.cmsLog.serverElapsedMsMax
                        nonHarnessRequests     = $evidence.cmsLog.nonHarnessRequests
                        cmsRequestScopedProblems = $evidence.cmsLog.requestScopedTotal
                        cmsBackgroundProblems  = $evidence.cmsLog.backgroundTotal
                        cmsUnattributedProblems = $evidence.cmsLog.unattributedTotal
                    })
            }
        }

        $workerMinLimit = $null
        if (-not $SkipDumps) {
            try {
                & (Join-Path $PSScriptRoot 'Get-CmsThreadPoolMinLimit.ps1') -MonitorBaseUrl $MonitorBaseUrl `
                    -CmsContainerName $CmsContainerName -OutputDirectory $OutputDirectory | Out-Null
                $dumpRecord = Get-ChildItem $OutputDirectory -Filter 'cms-threadpool-*.json' | Sort-Object LastWriteTime | Select-Object -Last 1
                if ($dumpRecord) { $workerMinLimit = (Get-Content $dumpRecord.FullName -Raw | ConvertFrom-Json).workerMinLimit }
            }
            catch {
                Write-Output "Dump collection failed: $($_.Exception.Message)"
            }
        }

        $nonOkTotal = 0
        $has500 = $false
        if ($burstSummary) {
            foreach ($round in $burstSummary.roundSummaries) {
                foreach ($property in $round.statusHistogram.PSObject.Properties) {
                    if ($property.Name -ne '200') { $nonOkTotal += [int]$property.Value }
                    if ($property.Name -eq '500') { $has500 = $true }
                }
            }
        }
        $pointRecords.Add([pscustomobject]@{
                rep                  = $rep
                position             = $position
                point                = $point
                workload             = $workload
                warmLabel            = $warmLabel
                burstLabel           = $burstLabel
                warmError            = $warmError
                burstError           = $burstError
                clock                = $clock
                cmsClientHost        = $cmsHost
                warmup               = if ($warmSummary) { @($warmSummary.roundSummaries)[0] } else { $null }
                warmupConnections    = $warmConnections
                preBurst             = $preBurst
                # Outer @() around the if-expression: PowerShell unwraps an empty array
                # returned by a statement expression, which would record $null here.
                samplerRequiredFailures = @(if ($burstSummary -and $burstSummary.samplerReport) { $burstSummary.samplerReport.requiredFailures } else { 'no sampler report' })
                nonOkTotal           = $nonOkTotal
                needsManualCorrelation = ($nonOkTotal -gt 0)
                has500               = $has500
                workerMinLimit       = $workerMinLimit
                rounds               = $roundEvidence
            })
        Write-Output ("Point done: non-200={0} coverageFailures={1} workerMinLimit={2} clockOffsetMs={3}" -f `
            $nonOkTotal, @($pointRecords[-1].samplerRequiredFailures).Count, $workerMinLimit, $(if ($clock) { $clock.offsetMs } else { 'n/a' }))
    }
}

$index = [pscustomobject]@{
    resourceProfile = $ResourceProfile
    repetitions     = $Repetitions
    environment     = $environmentRecord
    points          = $pointRecords
}
$indexPath = Join-Path $OutputDirectory "e2-$ResourceProfile-index.json"
$index | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $indexPath -Encoding utf8
$roundsPath = Join-Path $OutputDirectory "e2-$ResourceProfile-rounds.csv"
$roundRows | Export-Csv -LiteralPath $roundsPath -NoTypeInformation -Encoding utf8
Write-Output "Index written to $indexPath"
Write-Output "Round table written to $roundsPath ($($roundRows.Count) rows)"
