# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
DMS-1556 step 0.5 driver: one block of runs for one experiment condition (a baseline or
one control), with managed stacks, per-round evidence, handshake timing, and dumps.

.DESCRIPTION
Before anything runs, the live container state is verified against -Condition (CPU
caps for the resource profile; certificate mode only for e3; the thread-pool knob only
for e4; Maximum Pool Size=16 only for e5; max_connections=200 only for headroom), so a
block can never be labeled with a condition the stack is not in. Recompose with
Set-Dms1556StackCondition.ps1 first.

Workloads (every run starts from a CMS restart, so process and pool state are reset the
same way in every condition):
  cold-87x87   - E1 shape: token minted, CMS restart, burst immediately (round 1 cold).
  warm-87x87   - E2 shape: restart, untimed serial warm-up (87 requests at concurrency 1),
                 then the burst; round 1 is the first concurrent burst after warm-up.
  warm-256x128 - the E2 stress shape (slot-exhaustion prone; keep it in its own blocks).
Each burst: -Rounds rounds, -InterRoundDelaySeconds apart, samplers with coverage gate,
body validation, managed stacks at -StackCaptureOffsetsSeconds into round 1. After the
run: container logs, per-round evidence, round-1 handshake timing aligned with every
stack capture (thread-pool threads/queue, pool busy, connections received/authorized
at the capture's request time), then one dump OUTSIDE the timed windows (Q17).

Repetitions alternate the workload order. Outputs e5-<block>-<condition>-index.json,
-rounds.csv, and -stacks.csv. Nothing here assigns a verdict.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Parameters are read by nested functions through script scope; the analyzer does not track that usage.')]
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('baseline', 'e3-certificates', 'e4-threads', 'e5-pool16', 'headroom')]
    [string] $Condition,

    # Distinguishes matched pairs, e.g. 'pair-e3' for the baseline and control blocks
    # that belong together.
    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z0-9-]+$')]
    [string] $BlockLabel,

    [ValidateSet('p-dev', 'p-runner-approx')]
    [string] $ResourceProfile = 'p-runner-approx',

    [ValidateRange(1, 10)]
    [int] $Repetitions = 3,

    [ValidateSet('cold-87x87', 'warm-87x87', 'warm-256x128')]
    [string[]] $Workloads = @('cold-87x87', 'warm-87x87'),

    [ValidateRange(1, 20)]
    [int] $Rounds = 5,

    [ValidateRange(0, 60)]
    [int] $InterRoundDelaySeconds = 3,

    [double[]] $StackCaptureOffsetsSeconds = @(2, 8, 14),

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

function Get-LatestSummary {
    [CmdletBinding()]
    [OutputType([object])]
    param([Parameter(Mandatory)][string] $Label)

    $file = Get-ChildItem $OutputDirectory -Filter "$Label-*-summary.json" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $file) { return $null }
    return Get-Content $file.FullName -Raw | ConvertFrom-Json
}

function Get-ConditionState {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param()

    $envLines = @(docker inspect $CmsContainerName --format '{{range .Config.Env}}{{println .}}{{end}}' 2>$null)
    $connection = [string](@($envLines | Where-Object { $_ -like 'DatabaseSettings__DatabaseConnection=*' }) | Select-Object -First 1)
    $maxPool = if ($connection -match '(?i)Maximum Pool Size=(\d+)|Max Pool Size=(\d+)') { [int]($Matches[1] ?? $Matches[2]) } else { $null }
    $forceMin = [string](@($envLines | Where-Object { $_ -like 'DOTNET_ThreadPool_ForceMinWorkerThreads=*' }) | ForEach-Object { ($_ -split '=', 2)[1] } | Select-Object -First 1)
    $useCertificates = [string](@($envLines | Where-Object { $_ -like 'IdentitySettings__UseCertificates=*' }) | ForEach-Object { ($_ -split '=', 2)[1] } | Select-Object -First 1)
    $maxConnections = [int](docker exec $PgContainerName psql -U postgres -d postgres -Atc 'show max_connections' 2>$null)
    return [pscustomobject]@{
        cms                    = Get-DmsContainerResourceRecord -ContainerName $CmsContainerName
        postgres               = Get-DmsContainerResourceRecord -ContainerName $PgContainerName
        useCertificates        = $useCertificates
        forceMinWorkerThreads  = $forceMin
        connectionMaxPoolSize  = $maxPool
        postgresMaxConnections = $maxConnections
    }
}

function Test-ConditionState {
    [CmdletBinding()]
    [OutputType([object[]])]
    param([Parameter(Mandatory)][pscustomobject] $State)

    $problems = [System.Collections.Generic.List[string]]::new()
    $expectedCpu = if ($ResourceProfile -eq 'p-runner-approx') { '2' } else { 'unlimited' }
    if ([string]$State.cms.cpuLimit -ne $expectedCpu -or [string]$State.postgres.cpuLimit -ne $expectedCpu) {
        $problems.Add("CPU limits CMS=$($State.cms.cpuLimit) PG=$($State.postgres.cpuLimit), expected $expectedCpu")
    }
    $isE3 = $Condition -eq 'e3-certificates'
    if (($State.useCertificates -eq 'true') -ne $isE3) { $problems.Add("IdentitySettings__UseCertificates='$($State.useCertificates)'") }
    $isE4 = $Condition -eq 'e4-threads'
    if (($State.forceMinWorkerThreads -eq '0x80') -ne $isE4 -or (-not $isE4 -and $State.forceMinWorkerThreads)) { $problems.Add("DOTNET_ThreadPool_ForceMinWorkerThreads='$($State.forceMinWorkerThreads)'") }
    $isE5 = $Condition -eq 'e5-pool16'
    if (($State.connectionMaxPoolSize -eq 16) -ne $isE5 -or (-not $isE5 -and $null -ne $State.connectionMaxPoolSize)) { $problems.Add("connection string Maximum Pool Size=$($State.connectionMaxPoolSize)") }
    $expectedMaxConnections = if ($Condition -eq 'headroom') { 200 } else { 100 }
    if ($State.postgresMaxConnections -ne $expectedMaxConnections) { $problems.Add("max_connections=$($State.postgresMaxConnections), expected $expectedMaxConnections") }
    return , @($problems)
}

$conditionState = Get-ConditionState
$stateProblems = Test-ConditionState -State $conditionState
if ($stateProblems.Count -gt 0) {
    throw "Stack is not in condition '$Condition': $($stateProblems -join '; '). Run Set-Dms1556StackCondition.ps1 first."
}
Write-Output ("Block {0} / {1} ({2}): verified - certificates={3} forceMin='{4}' maxPool={5} max_connections={6}" -f $BlockLabel, $Condition, $ResourceProfile,
    $conditionState.useCertificates, $conditionState.forceMinWorkerThreads, $conditionState.connectionMaxPoolSize, $conditionState.postgresMaxConnections)

$runRecords = [System.Collections.Generic.List[object]]::new()
$roundRows = [System.Collections.Generic.List[object]]::new()
$stackRows = [System.Collections.Generic.List[object]]::new()

for ($rep = 1; $rep -le $Repetitions; $rep++) {
    $order = if ($rep % 2 -eq 1) { @($Workloads) } else { @($Workloads)[($Workloads.Count - 1)..0] }
    foreach ($workload in $order) {
        $isCold = $workload.StartsWith('cold-')
        $shape = $workload.Split('-')[1].Split('x')
        $total = [int]$shape[0]
        $concurrency = [int]$shape[1]
        $baseLabel = "$workload-e5-$BlockLabel-$Condition-rep$rep"
        $burstLabel = "$baseLabel-burst"
        $warmLabel = "$baseLabel-warm"
        $runStartUtc = [DateTime]::UtcNow.AddSeconds(-1)
        Write-Output "=== $baseLabel ==="
        $clock = Measure-DmsVmClockOffset -PgContainerName $PgContainerName

        $warmError = $null
        if (-not $isCold) {
            try {
                & $harness -TotalRequests 87 -MaxConcurrency 1 -Rounds 1 -Cold -ValidateBodies `
                    -CmsContainerName $CmsContainerName -OutputDirectory $OutputDirectory -RunLabel $warmLabel | Out-Null
            }
            catch {
                $warmError = $_.Exception.Message
                Write-Output "Warm-up failed: $warmError"
            }
        }

        $burstError = $null
        if (-not $warmError) {
            $burstArguments = @{
                TotalRequests              = $total
                MaxConcurrency             = $concurrency
                Rounds                     = $Rounds
                InterRoundDelaySeconds     = $InterRoundDelaySeconds
                ValidateBodies             = $true
                WithSamplers               = $true
                SamplerDurationSeconds     = $SamplerDurationSeconds
                MonitorBaseUrl             = $MonitorBaseUrl
                SamplerStatsContainers     = $SamplerStatsContainers
                StackCaptureOffsetsSeconds = $StackCaptureOffsetsSeconds
                StackCaptureRounds         = @(1)
                CmsContainerName           = $CmsContainerName
                OutputDirectory            = $OutputDirectory
                RunLabel                   = $burstLabel
                Cold                       = $isCold
            }
            try {
                & $harness @burstArguments | ForEach-Object { if ($_ -match '^\s+statuses:') { Write-Output "  $($_.Trim())" } }
            }
            catch {
                $burstError = $_.Exception.Message
                Write-Output "Burst reported: $burstError"
            }
        }
        $burstSummary = if ($warmError) { $null } else { Get-LatestSummary -Label $burstLabel }

        $sinceIso = $runStartUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
        $cmsLogPath = Join-Path $OutputDirectory "$baseLabel-cms.log"
        $pgLogPath = Join-Path $OutputDirectory "$baseLabel-pg.log"
        docker logs --since $sinceIso $CmsContainerName 2>&1 | Set-Content -LiteralPath $cmsLogPath -Encoding utf8
        docker logs --since $sinceIso $PgContainerName 2>&1 | Set-Content -LiteralPath $pgLogPath -Encoding utf8
        $cmsHost = Get-DmsContainerIpAddress -ContainerName $CmsContainerName
        $pgEvents = Get-DmsPgLogEvent -Path $pgLogPath

        $roundEvidence = [System.Collections.Generic.List[object]]::new()
        $handshakeRound1 = $null
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
            foreach ($round in $burstSummary.roundSummaries) {
                $start = ConvertTo-DmsUtcInstant $round.startUtc
                $end = ConvertTo-DmsUtcInstant $round.endUtc
                $evidence = Get-DmsE2RoundEvidence -StartUtc $start -EndUtc $end -Captures $captures -PgEvents $pgEvents `
                    -CmsDatabase $CmsDatabase -CmsClientHost $cmsHost -CmsContainer $CmsContainerName -PgContainer $PgContainerName
                $stackInstants = @(@($round.stackCaptures) | Where-Object { $_ } | ForEach-Object { ConvertTo-DmsUtcInstant $_.requestedUtc })
                $handshake = Measure-DmsPgHandshakeWindow -Events $pgEvents -StartUtc $start -EndUtc $end.AddSeconds(0.25) -ClientHost $cmsHost -AtUtc $stackInstants
                if ([int]$round.round -eq 1) { $handshakeRound1 = $handshake }
                $roundEvidence.Add([pscustomobject]@{ round = [int]$round.round; summary = $round; evidence = $evidence; handshake = $handshake })

                $statuses = $round.statusHistogram
                $ok = if ($statuses.PSObject.Properties['200']) { [int]$statuses.'200' } else { 0 }
                $roundRows.Add([pscustomobject][ordered]@{
                        block = $BlockLabel; condition = $Condition; workload = $workload; rep = $rep; round = [int]$round.round
                        startUtc = $start.ToString('o'); windowMs = [Math]::Round(($end - $start).TotalMilliseconds, 1)
                        requests = [int]$round.requests; status200 = $ok; nonOk = [int]$round.requests - $ok
                        statuses = ConvertTo-DmsFlatText $statuses; bodyInvalid = $round.bodyInvalidCount
                        p50Ms = $round.p50Ms; p95Ms = $round.p95Ms; p99Ms = $round.p99Ms; maxMs = $round.maxMs
                        peakOverlap = $round.measuredPeakOverlap
                        cmsConnCreated = $evidence.connections.cmsConnectionsCreated; cmsConnClosed = $evidence.connections.cmsDisconnections
                        pgErrors = ConvertTo-DmsFlatText $evidence.connections.pgErrors
                        handshakeReceived = $handshake.received; handshakeP50Ms = $handshake.receivedToAuthorizedP50Ms
                        handshakeMaxMs = $handshake.receivedToAuthorizedMaxMs; rejectedBeforeAuth = $handshake.rejectedBeforeAuthorization
                        cmsBackendsPeak = $evidence.pgActivity.peakNumbackends; cmsActivePeak = $evidence.pgActivity.peakActive
                        poolBusyMax = $evidence.livemetrics.poolBusyMax; poolTotalMax = $evidence.livemetrics.poolTotalMax
                        tpThreadsBaseline = $evidence.livemetrics.threadPoolThreadsBaseline; tpThreadsMax = $evidence.livemetrics.threadPoolThreadsMax
                        tpQueueMax = $evidence.livemetrics.threadPoolQueueMax
                        cmsCpuDockerMax = $evidence.cmsStats.cpuPercentMax; pgCpuDockerMax = $evidence.pgStats.cpuPercentMax; pgCpuDockerMean = $evidence.pgStats.cpuPercentMean
                        pgIoWrites = $evidence.pgIo.writes; hostDiskLatencyMsMax = $evidence.hostDisk.latencyMsMax
                        slowStatements = $evidence.connections.slowStatements; checkpoints = $evidence.connections.checkpointsStarted
                        cmsRequestScopedProblems = $evidence.cmsLog.requestScopedTotal; cmsBackgroundProblems = $evidence.cmsLog.backgroundTotal
                        cmsUnattributedProblems = $evidence.cmsLog.unattributedTotal; nonHarnessRequests = $evidence.cmsLog.nonHarnessRequests
                    })

                foreach ($capture in @(@($round.stackCaptures) | Where-Object { $_ })) {
                    $at = ConvertTo-DmsUtcInstant $capture.requestedUtc
                    $stackPath = Join-Path $OutputDirectory $capture.file
                    $stack = Get-DmsStackSummary -Path $stackPath
                    $aligned = @($handshake.alignment | Where-Object { (ConvertTo-DmsUtcInstant $_.atUtc) -eq $at }) | Select-Object -First 1
                    $stackRows.Add([pscustomobject][ordered]@{
                            block = $BlockLabel; condition = $Condition; workload = $workload; rep = $rep; round = [int]$round.round
                            offsetSeconds = $capture.offsetSeconds
                            requestedAfterRoundStartS = [Math]::Round(($at - $start).TotalSeconds, 2)
                            captureMs = [Math]::Round(((ConvertTo-DmsUtcInstant $capture.completedUtc) - $at).TotalMilliseconds, 0)
                            roundEndedBeforeCapture = ($end -lt $at)
                            captureError = $capture.error
                            threads = $stack.threads; tpWorkers = $stack.threadPoolWorkers
                            resolverWait = $stack.categories['resolver-wait']; resolverFrameVisible = $stack.resolverFrameVisible
                            otherSyncWait = $stack.categories['other-sync-wait']; scramCompute = $stack.categories['scram-compute']
                            npgsqlActive = $stack.categories['npgsql-active']; tpIdle = $stack.categories['threadpool-idle']
                            other = $stack.categories['other']
                            tpThreadsAt = Get-DmsLivemetricsValueAt -Path $captures.livemetrics -AtUtc $at -Counter 'threadpool-thread-count'
                            tpQueueAt = Get-DmsLivemetricsValueAt -Path $captures.livemetrics -AtUtc $at -Counter 'threadpool-queue-length'
                            poolBusyAt = Get-DmsLivemetricsValueAt -Path $captures.livemetrics -AtUtc $at -Counter 'pool-busy' -PoolDatabase $CmsDatabase
                            connReceivedBy = if ($aligned) { $aligned.received } else { $null }
                            connAuthorizedBy = if ($aligned) { $aligned.authorized } else { $null }
                            file = $capture.file
                        })
                }
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
        if ($burstSummary) {
            foreach ($round in $burstSummary.roundSummaries) {
                foreach ($property in $round.statusHistogram.PSObject.Properties) {
                    if ($property.Name -ne '200') { $nonOkTotal += [int]$property.Value }
                }
            }
        }
        $runRecords.Add([pscustomobject]@{
                rep = $rep; workload = $workload; baseLabel = $baseLabel; warmError = $warmError; burstError = $burstError
                clock = $clock; cmsClientHost = $cmsHost; workerMinLimit = $workerMinLimit
                samplerRequiredFailures = @(if ($burstSummary -and $burstSummary.samplerReport) { $burstSummary.samplerReport.requiredFailures } else { 'no sampler report' })
                nonOkTotal = $nonOkTotal; needsManualCorrelation = ($nonOkTotal -gt 0)
                handshakeRound1 = $handshakeRound1; rounds = $roundEvidence
            })
        $r1 = if ($burstSummary) { @($burstSummary.roundSummaries)[0] } else { $null }
        Write-Output ("Run done: round1 p50={0} ms, non-200={1}, coverageFailures={2}, workerMinLimit={3}, handshake p50={4} ms" -f `
            $(if ($r1) { $r1.p50Ms } else { 'n/a' }), $nonOkTotal, @($runRecords[-1].samplerRequiredFailures).Count, $workerMinLimit,
            $(if ($handshakeRound1) { $handshakeRound1.receivedToAuthorizedP50Ms } else { 'n/a' }))
    }
}

$prefix = Join-Path $OutputDirectory "e5-$BlockLabel-$Condition"
[pscustomobject]@{
    block           = $BlockLabel
    condition       = $Condition
    resourceProfile = $ResourceProfile
    capturedUtc     = [DateTime]::UtcNow.ToString('o')
    conditionState  = $conditionState
    otherRunningContainers = @(docker ps --format '{{.Names}}' 2>$null | Where-Object { $_ -notin @($CmsContainerName, $PgContainerName, 'ed-fi-api', 'cms-dotnet-monitor') })
    workloads       = $Workloads
    rounds          = $Rounds
    interRoundDelaySeconds = $InterRoundDelaySeconds
    stackCaptureOffsetsSeconds = $StackCaptureOffsetsSeconds
    runs            = $runRecords
} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath "$prefix-index.json" -Encoding utf8
$roundRows | Export-Csv -LiteralPath "$prefix-rounds.csv" -NoTypeInformation -Encoding utf8
$stackRows | Export-Csv -LiteralPath "$prefix-stacks.csv" -NoTypeInformation -Encoding utf8
Write-Output "Block written: $prefix-index.json ($($roundRows.Count) rounds, $($stackRows.Count) stack captures)"
