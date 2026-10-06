# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
DMS-1556 step 4.1-O: injected dependency outages on the fixed image, with every non-200
response classified by the stage that produced it.

.DESCRIPTION
Scenarios (each run starts from a CMS restart):

  pause-warm   - spec 4.1-O. Restart, untimed serial warm-up (the startup snapshot is
                 loaded), then rounds of -PauseTotalRequests requests through 87 slots,
                 -InterRoundDelaySeconds apart; `docker pause` of PostgreSQL
                 -PauseDurationSeconds long, starting -PauseOffsetSeconds into round
                 -PauseRound. A round of 870 lasts over a second, so the pause lands
                 mid-round with requests in flight at every stage (a warm 87-request round
                 finishes in ~0.2 s, faster than `docker pause` takes effect). The snapshot stays usable
                 throughout, so the authentication-stage dependency is the token-status
                 store. With a warm pool no dependency timeout elapses within 20 s
                 (pooled commands wait; Npgsql CommandTimeout is 30 s), so requests are
                 delayed rather than failed.
  pause-warm-long - the same with -LongPauseDurationSeconds (default 40 s), longer than
                 Npgsql's 30 s CommandTimeout. Observed at step 4.1: the command timeout
                 did not end the requests held by the frozen server; they completed with
                 200 at unpause. Only requests that needed a new physical connection
                 failed, at the 15 s open timeout.
  keylock-cold - key-store-only outage on a cold process: the token is minted, one
                 session takes LOCK TABLE dmscs."OpenIddictKey" ACCESS EXCLUSIVE, CMS is
                 restarted under the lock (its startup load cannot read the keys), and
                 rounds run until well after the lock is released. Protected requests and
                 JWKS have no usable snapshot until a load succeeds.

A JWKS probe runs beside the rounds (-JwksProbeIntervalMilliseconds); stacks are captured
while the dependency is held. After each run the CMS log is read back and each non-200
response is classified through its correlation id:

  authentication       - the boundary's 'Authentication could not reach a decision' Error
                         names the dependency category (SigningKeyStore | TokenStatusStore);
  post-authentication  - an Error on the request's /v3/profiles path and no boundary line
                         (the unchanged profile-repository failure, F8);
  unclassified         - neither.

The run also counts the fixed code's dependency log signals (spec 4.9) in the window, and
the retired 'Failed to fetch public keys for JWKS' string only to show it is absent by
construction (it proves nothing). Outputs o41-<scenario>-<profile>-rep<n>-*.json and an
o41-<profile>-index.json. Nothing here assigns a verdict.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Parameters are read by nested functions through script scope; the analyzer does not track that usage.')]
[CmdletBinding()]
param(
    [ValidateSet('pause-warm', 'pause-warm-long', 'keylock-cold')]
    [string[]] $Scenarios = @('pause-warm', 'pause-warm-long', 'keylock-cold'),

    [ValidateSet('p-dev', 'p-runner-approx')]
    [string] $ResourceProfile = 'p-runner-approx',

    [ValidateRange(1, 10)]
    [int] $Repetitions = 2,

    [ValidateRange(5, 200)]
    [int] $Rounds = 35,

    # keylock-cold rounds: enough that the run keeps observing for well over the recovery
    # bound (82 s) after the lock is released. Refused rounds are short, so 35 rounds can
    # end before a backed-off load attempt is due.
    [ValidateRange(5, 400)]
    [int] $LockRounds = 80,

    [ValidateRange(0, 60)]
    [int] $InterRoundDelaySeconds = 2,

    [ValidateRange(1, 100)]
    [int] $PauseRound = 4,

    [double] $PauseOffsetSeconds = 0.4,

    [ValidateRange(87, 100000)]
    [int] $PauseTotalRequests = 870,

    [ValidateRange(1, 120)]
    [int] $PauseDurationSeconds = 20,

    [ValidateRange(1, 120)]
    [int] $LongPauseDurationSeconds = 40,

    # Long enough that rounds run inside the lock after the restart, health, and sampler
    # readiness (about 25 s in the shake-down).
    [ValidateRange(1, 300)]
    [int] $LockDurationSeconds = 75,

    [ValidateRange(100, 10000)]
    [int] $JwksProbeIntervalMilliseconds = 500,

    [ValidateRange(60, 3600)]
    [int] $SamplerDurationSeconds = 420,

    [string] $CmsContainerName = 'ed-fi-api-config-service',

    [string] $PgContainerName = 'dms-postgresql',

    [string] $CmsDatabase = 'edfi_datamanagementservice',

    [string[]] $SamplerStatsContainers = @('ed-fi-api-config-service', 'dms-postgresql', 'ed-fi-api'),

    [string] $MonitorBaseUrl = 'http://localhost:52323',

    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
Import-Module (Join-Path $PSScriptRoot 'dms-1556-analysis.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'dms-1556-step41.psm1') -Force
$harness = Join-Path $PSScriptRoot 'Invoke-CmsProfileBurst.ps1'

# The provider's backoff is min(5 * 2^(n-1), 60) s with +/-20 % jitter, and one load is
# bounded by LoadTimeout (10 s by default): the documented recovery bound once the
# dependency is back is max backoff + LoadTimeout.
$maxBackoffSeconds = 60 * 1.2
$loadTimeoutSeconds = 10
$recoveryBoundSeconds = $maxBackoffSeconds + $loadTimeoutSeconds

function Get-LatestSummary {
    [CmdletBinding()]
    [OutputType([object])]
    param([Parameter(Mandatory)][string] $Label)

    $file = Get-ChildItem $OutputDirectory -Filter "$Label-*-summary.json" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $file) { return $null }
    return Get-Content $file.FullName -Raw | ConvertFrom-Json
}

function Get-OutageTimeline {
    # Instants relative to the fault: when it was applied and removed, the last non-200
    # completion, the first 200 completion after removal, and the JWKS status per phase.
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][object[]] $Responses,
        [object[]] $Jwks = @(),
        [Parameter(Mandatory)][pscustomobject] $Fault
    )

    $record = $Fault.record
    $applied = if ($Fault.kind -eq 'LockKeyTable') { ConvertTo-DmsUtcInstant $Fault.lockGrantedUtc } else { ConvertTo-DmsUtcInstant $record.appliedUtc }
    $removed = ConvertTo-DmsUtcInstant $record.removedUtc
    $completion = { param($r) ConvertTo-DmsUtcInstant $r.bodyCompletedUtc }
    $nonOk = @($Responses | Where-Object { $_.statusCode -ne 200 } | Sort-Object { & $completion $_ })
    $okAfter = @($Responses | Where-Object { $_.statusCode -eq 200 -and (& $completion $_) -gt $removed } | Sort-Object { & $completion $_ })
    $lastNonOk = if ($nonOk.Count) { & $completion $nonOk[-1] } else { $null }
    $firstOkAfter = if ($okAfter.Count) { & $completion $okAfter[0] } else { $null }
    $phase = {
        param($utc)
        if ($utc -lt $applied) { 'before' } elseif ($utc -le $removed) { 'during' } else { 'after' }
    }
    $jwksPhases = [ordered]@{}
    foreach ($group in ($Jwks | Group-Object -Property { '{0}:{1}:{2}' -f (& $phase (ConvertTo-DmsUtcInstant $_.requestedUtc)), $_.statusCode, $_.keyCount })) {
        $jwksPhases[$group.Name] = $group.Count
    }
    $jwksOkAfter = @($Jwks | Where-Object { [int]$_.statusCode -eq 200 -and (ConvertTo-DmsUtcInstant $_.completedUtc) -gt $removed } |
            Sort-Object { ConvertTo-DmsUtcInstant $_.completedUtc })
    $jwksNonOkAfter = @($Jwks | Where-Object { [int]$_.statusCode -ne 200 -and (ConvertTo-DmsUtcInstant $_.completedUtc) -gt $removed })
    return [pscustomobject]@{
        faultAppliedUtc               = $applied.ToString('o')
        faultRemovedUtc               = $removed.ToString('o')
        faultHeldSeconds              = [Math]::Round(($removed - $applied).TotalSeconds, 2)
        firstNonOkCompletionUtc       = if ($nonOk.Count) { (& $completion $nonOk[0]).ToString('o') } else { $null }
        lastNonOkCompletionUtc        = if ($lastNonOk) { $lastNonOk.ToString('o') } else { $null }
        lastNonOkAfterRemovalSeconds  = if ($lastNonOk) { [Math]::Round(($lastNonOk - $removed).TotalSeconds, 2) } else { $null }
        firstOkAfterRemovalSeconds    = if ($firstOkAfter) { [Math]::Round(($firstOkAfter - $removed).TotalSeconds, 2) } else { $null }
        recoveryBoundSeconds          = $recoveryBoundSeconds
        # Recovery is claimed only when a 200 was OBSERVED after removal and no failure
        # came later than the bound; a run that ends before recovery proves nothing.
        recoveredWithinBound          = ($null -ne $firstOkAfter) -and (-not $lastNonOk -or ($lastNonOk - $removed).TotalSeconds -le $recoveryBoundSeconds) -and
            (($firstOkAfter - $removed).TotalSeconds -le $recoveryBoundSeconds)
        okResponsesAfterRemoval       = $okAfter.Count
        observedAfterRemovalSeconds   = [Math]::Round(((@($Responses | ForEach-Object { & $completion $_ }) | Measure-Object -Maximum).Maximum - $removed).TotalSeconds, 2)
        jwksByPhaseStatusKeys         = $jwksPhases
        jwksFirstOkAfterRemovalSeconds = if ($jwksOkAfter.Count) { [Math]::Round(((ConvertTo-DmsUtcInstant $jwksOkAfter[0].completedUtc) - $removed).TotalSeconds, 2) } else { $null }
        jwksNonOkAfterRemoval         = $jwksNonOkAfter.Count
    }
}

$cms = Get-DmsContainerResourceRecord -ContainerName $CmsContainerName
$pg = Get-DmsContainerResourceRecord -ContainerName $PgContainerName
$expectedCpu = if ($ResourceProfile -eq 'p-runner-approx') { '2' } else { 'unlimited' }
if ([string]$cms.cpuLimit -ne $expectedCpu -or [string]$pg.cpuLimit -ne $expectedCpu) {
    throw "CPU limits CMS=$($cms.cpuLimit) PG=$($pg.cpuLimit) do not match $ResourceProfile ($expectedCpu). Recompose with Set-Dms1556StackCondition.ps1 first."
}
$envLines = @(docker inspect $CmsContainerName --format '{{range .Config.Env}}{{println .}}{{end}}' 2>$null)
$controlEnv = @($envLines | Where-Object { $_ -match '^(DOTNET_ThreadPool_ForceMinWorkerThreads|IdentitySettings__UseCertificates|IdentitySettings__SigningKey)' })
$connection = [string](@($envLines | Where-Object { $_ -like 'DatabaseSettings__DatabaseConnection=*' }) | Select-Object -First 1)
if ($controlEnv.Count -gt 0 -or $connection -match '(?i)Max(imum)? Pool Size') {
    throw "CMS is not at default settings: $(@($controlEnv) -join '; ') $connection"
}
$imageId = [string](docker inspect $CmsContainerName --format '{{.Image}}')

$runs = [System.Collections.Generic.List[object]]::new()
for ($rep = 1; $rep -le $Repetitions; $rep++) {
    foreach ($scenario in $Scenarios) {
        $baseLabel = "o41-$scenario-$ResourceProfile-rep$rep"
        $burstLabel = "$baseLabel-burst"
        Write-Output "=== $baseLabel ==="
        $runStartUtc = [DateTime]::UtcNow.AddSeconds(-1)
        $clock = Measure-DmsVmClockOffset -PgContainerName $PgContainerName
        $runError = $null
        $follower = $null
        $common = @{
            MaxConcurrency                = 87
            InterRoundDelaySeconds        = $InterRoundDelaySeconds
            ValidateBodies                = $true
            WithSamplers                  = $true
            SamplerDurationSeconds        = $SamplerDurationSeconds
            MonitorBaseUrl                = $MonitorBaseUrl
            SamplerStatsContainers        = $SamplerStatsContainers
            JwksProbeIntervalMilliseconds = $JwksProbeIntervalMilliseconds
            CmsContainerName              = $CmsContainerName
            OutputDirectory               = $OutputDirectory
            RunLabel                      = $burstLabel
            FaultPgContainerName          = $PgContainerName
            FaultDatabase                 = $CmsDatabase
        }
        try {
            if ($scenario -like 'pause-warm*') {
                $pauseSeconds = if ($scenario -eq 'pause-warm-long') { $LongPauseDurationSeconds } else { $PauseDurationSeconds }
                & $harness -TotalRequests 87 -MaxConcurrency 1 -Rounds 1 -Cold -ValidateBodies `
                    -CmsContainerName $CmsContainerName -OutputDirectory $OutputDirectory -RunLabel "$baseLabel-warm" | Out-Null
                # A pause run logs ~30,000 requests at Debug level: more than Docker's
                # json-file rotation (5 x 50 MB) retains, so `docker logs` afterwards would
                # miss the start of the burst. Stream the log during the burst instead (the
                # warm-up restart is over, and nothing restarts CMS until the run ends).
                $follower = Start-Process -FilePath docker -ArgumentList @('logs', '--follow', '--since', $runStartUtc.ToString('yyyy-MM-ddTHH:mm:ssZ'), $CmsContainerName) `
                    -RedirectStandardOutput (Join-Path $OutputDirectory "$baseLabel-cms.stdout.tmp") `
                    -RedirectStandardError (Join-Path $OutputDirectory "$baseLabel-cms.stderr.tmp") -NoNewWindow -PassThru
                & $harness @common -Rounds $Rounds -TotalRequests $PauseTotalRequests -FaultKind PausePostgres -FaultRound $PauseRound -FaultOffsetSeconds $PauseOffsetSeconds `
                    -FaultDurationSeconds $pauseSeconds -StackCaptureRounds @($PauseRound) `
                    -StackCaptureOffsetsSeconds @(($PauseOffsetSeconds + 3), ($PauseOffsetSeconds + 10)) |
                    ForEach-Object { if ($_ -match 'statuses:' -and $_ -notmatch "statuses: 200=$PauseTotalRequests;") { Write-Output "  $($_.Trim())" } }
            }
            else {
                & $harness @common -Rounds $LockRounds -TotalRequests 87 -Cold -FaultKind LockKeyTable -FaultRound 0 -FaultDurationSeconds $LockDurationSeconds `
                    -StackCaptureRounds @(1) -StackCaptureOffsetsSeconds @(1, 5) |
                    ForEach-Object { if ($_ -match 'statuses:' -and $_ -notmatch 'statuses: 200=87;') { Write-Output "  $($_.Trim())" } }
            }
        }
        catch {
            $runError = $_.Exception.Message
            Write-Output "Run reported: $runError"
        }

        $summary = Get-LatestSummary -Label $burstLabel
        $sinceIso = $runStartUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
        $cmsLogPath = Join-Path $OutputDirectory "$baseLabel-cms.log"
        $pgLogPath = Join-Path $OutputDirectory "$baseLabel-pg.log"
        if ($follower) {
            Start-Sleep -Seconds 3
            Stop-Process -Id $follower.Id -ErrorAction SilentlyContinue
            $follower.WaitForExit(10000) | Out-Null
            $streamed = @("$baseLabel-cms.stdout.tmp", "$baseLabel-cms.stderr.tmp") | ForEach-Object { Join-Path $OutputDirectory $_ }
            Get-Content -LiteralPath $streamed | Set-Content -LiteralPath $cmsLogPath -Encoding utf8
            Remove-Item -LiteralPath $streamed
            $follower = $null
        }
        else {
            docker logs --since $sinceIso $CmsContainerName 2>&1 | Set-Content -LiteralPath $cmsLogPath -Encoding utf8
        }
        docker logs --since $sinceIso $PgContainerName 2>&1 | Set-Content -LiteralPath $pgLogPath -Encoding utf8

        $record = [ordered]@{ scenario = $scenario; rep = $rep; baseLabel = $baseLabel; runError = $runError; clock = $clock }
        if ($summary) {
            $entries = Get-DmsCmsLogEntry -Path $cmsLogPath
            $rows = [System.Collections.Generic.List[object]]::new()
            foreach ($round in $summary.roundSummaries) {
                foreach ($row in (Import-Csv -LiteralPath (Join-Path $OutputDirectory $round.csv))) { $rows.Add($row) }
            }
            $responses = @(Get-DmsResponseClassification -Rows $rows.ToArray() -Entries $entries)
            $responses | Export-Csv -LiteralPath (Join-Path $OutputDirectory "$baseLabel-responses.csv") -NoTypeInformation -Encoding utf8
            $jwks = if ($summary.jwksProbe -and $summary.jwksProbe.file) { @(Import-Csv -LiteralPath (Join-Path $OutputDirectory $summary.jwksProbe.file)) } else { @() }
            $windowStart = ConvertTo-DmsUtcInstant $summary.startedUtc
            $windowEnd = ConvertTo-DmsUtcInstant $summary.burstWindow.endUtc
            $stageCounts = [ordered]@{}
            foreach ($group in ($responses | Group-Object -Property { '{0}|{1}|{2}' -f $_.statusCode, $_.stage, $_.category } | Sort-Object Name)) {
                $stageCounts[$group.Name] = $group.Count
            }
            $stackRecords = @(foreach ($round in $summary.roundSummaries) {
                    foreach ($capture in @(@($round.stackCaptures) | Where-Object { $_ })) {
                        $stack = Get-DmsStackSummary -Path (Join-Path $OutputDirectory $capture.file)
                        $authSyncWaits = Get-DmsAuthenticationSyncWaitCount -Path (Join-Path $OutputDirectory $capture.file)
                        [pscustomobject]@{
                            round = [int]$round.round; offsetSeconds = $capture.offsetSeconds
                            requestedUtc = $capture.requestedUtc; completedUtc = $capture.completedUtc; error = $capture.error
                            threads = $stack.threads; threadPoolWorkers = $stack.threadPoolWorkers
                            categories = $stack.categories; authenticationSyncWaits = $authSyncWaits
                            topSignatures = $stack.topSignatures
                        }
                    }
                })
            $record.summaryRunId = $summary.runId
            $record.samplerRequiredFailures = @(if ($summary.samplerReport) { $summary.samplerReport.requiredFailures } else { 'no sampler report' })
            $record.fault = $summary.fault
            $record.responseCount = $responses.Count
            $record.byStatusStageCategory = $stageCounts
            $record.non200WithoutDependencyContract = @($responses | Where-Object { $_.statusCode -eq 503 -and -not $_.dependencyContract }).Count
            $record.invalid200Bodies = @($responses | Where-Object { $_.statusCode -eq 200 -and $_.bodyValid -ne 'True' }).Count
            $record.timeline = if ($summary.fault -and $summary.fault.record) { Get-OutageTimeline -Responses $responses -Jwks $jwks -Fault $summary.fault } else { $null }
            # A log that starts after the burst began (rotation) cannot classify every
            # response or count every signal; the run is then flagged, never trusted.
            # Judged against the burst start: a cold run's harness starts before the CMS
            # restart, and the idle old process may log nothing in between.
            $firstLogged = Get-DmsCmsLogFirstUtc -Path $cmsLogPath
            $record.cmsLogFirstUtc = if ($firstLogged) { $firstLogged.ToString('o') } else { $null }
            $record.cmsLogComplete = [bool]($firstLogged -and $firstLogged -le (ConvertTo-DmsUtcInstant $summary.burstWindow.startUtc))
            $record.signals = Get-DmsSignalCount -Entries $entries -StartUtc $windowStart -EndUtc $windowEnd.AddSeconds(5)
            $record.stacks = $stackRecords
            if ($record.timeline) {
                $removedUtc = ConvertTo-DmsUtcInstant $record.timeline.faultRemovedUtc
                $published = @($record.signals.publications | Where-Object { (ConvertTo-DmsUtcInstant $_.utc) -gt $removedUtc } | Select-Object -First 1)
                $record.timeline | Add-Member -NotePropertyName firstPublicationAfterRemovalSeconds -NotePropertyValue $(
                    if ($published) { [Math]::Round(((ConvertTo-DmsUtcInstant $published[0].utc) - $removedUtc).TotalSeconds, 2) } else { $null })
            }
            $record.livemetrics = if ($summary.samplerReport) {
                Get-DmsLivemetricsWindowSummary -Path ([string]$summary.samplerReport.files.livemetrics) -StartUtc $windowStart -EndUtc $windowEnd -PoolDatabase $CmsDatabase
            } else { $null }
            $faultSummary = if ($record.timeline) {
                'held {0} s; last non-200 {1} s after removal; first 200 {2} s after removal' -f $record.timeline.faultHeldSeconds,
                $record.timeline.lastNonOkAfterRemovalSeconds, $record.timeline.firstOkAfterRemovalSeconds
            } else { 'no fault record' }
            Write-Output ("Run done: {0}; {1}; JWKS {2}" -f (($stageCounts.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '),
                $faultSummary, $(if ($record.timeline) { ($record.timeline.jwksByPhaseStatusKeys.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ' } else { 'n/a' }))
        }
        $runRecord = [pscustomobject]$record
        $runRecord | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory "$baseLabel-outage.json") -Encoding utf8
        $runs.Add($runRecord)
    }
}

$indexPath = Join-Path $OutputDirectory "o41-$ResourceProfile-index.json"
[pscustomobject]@{
    resourceProfile        = $ResourceProfile
    capturedUtc            = [DateTime]::UtcNow.ToString('o')
    cmsImage               = $imageId
    cms                    = $cms
    postgres               = $pg
    postgresMaxConnections = [int](docker exec $PgContainerName psql -U postgres -d postgres -Atc 'show max_connections' 2>$null)
    recoveryBoundSeconds   = $recoveryBoundSeconds
    scenarios              = $Scenarios
    pause                  = [pscustomobject]@{ totalRequests = $PauseTotalRequests; maxConcurrency = 87; round = $PauseRound; offsetSeconds = $PauseOffsetSeconds; durationSeconds = $PauseDurationSeconds; longDurationSeconds = $LongPauseDurationSeconds }
    lockDurationSeconds    = $LockDurationSeconds
    rounds                 = $Rounds
    lockRounds             = $LockRounds
    interRoundDelaySeconds = $InterRoundDelaySeconds
    runs                   = $runs
} | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $indexPath -Encoding utf8
Write-Output "Outage index written: $indexPath ($($runs.Count) runs)"
