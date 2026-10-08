# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
DMS-1556 step 4.1: aggregates the retained fixed-image captures into the tables the
investigation record cites (no workload runs here).

.DESCRIPTION
Reads every h41-<block>-<condition>-index.json / -rounds.csv / -stacks.csv (4.1-H, from
Invoke-Step41Sequence.ps1) and every o41-<profile>-index.json (4.1-O, from
Invoke-Step41Outage.ps1) under -OutputDirectory and writes h41-report.md and
h41-report.json there:

  - per block and workload: runs, responses by status, 200s with an invalid body, round-1
    and rounds-2-5 latency, peak overlap, thread-pool threads/queue, pool busy, CMS
    connections created, PostgreSQL 53300 rejections, Worker Min Limit, sampler coverage;
  - every non-200 of the healthy runs classified by stage through its correlation id;
  - the fixed code's dependency log signals per workload (the retired baseline string
    counted only to show it is absent by construction);
  - managed stacks by capture offset, including authentication frames in a sync wait;
  - per outage run: stage-classified responses, the dependency contract on every 503,
    the timeline against the recovery bound, JWKS by phase, signals, and stacks.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'dms-1556-analysis.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'dms-1556-step41.psm1') -Force

function Format-Range {
    [CmdletBinding()]
    [OutputType([string])]
    param([object[]] $Values, [int] $Digits = 0)

    $numbers = @($Values | Where-Object { $null -ne $_ -and "$_" -ne '' } | ForEach-Object { [double]$_ })
    if ($numbers.Count -eq 0) { return 'n/a' }
    $min = [Math]::Round(($numbers | Measure-Object -Minimum).Minimum, $Digits)
    $max = [Math]::Round(($numbers | Measure-Object -Maximum).Maximum, $Digits)
    if ($min -eq $max) { return "$min" }
    return "$min–$max"
}

function Format-Map {
    [CmdletBinding()]
    [OutputType([string])]
    param([System.Collections.IDictionary] $Map)

    if ($null -eq $Map -or $Map.Count -eq 0) { return '—' }
    # Markdown cells: a literal '|' in a key would split the cell.
    return (@($Map.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key -replace '\|', ' / ') × $($_.Value)" }) -join '; ')
}

function Add-Count {
    [CmdletBinding()]
    param([System.Collections.IDictionary] $Map, [string] $Key, [int] $By = 1)

    $Map[$Key] = [int]($Map[$Key] ?? 0) + $By
}

$md = [System.Collections.Generic.List[string]]::new()
$json = [ordered]@{ generatedUtc = [DateTime]::UtcNow.ToString('o'); healthy = @(); outage = @() }

# ---------------------------------------------------------------- 4.1-H
$indexFiles = @(Get-ChildItem $OutputDirectory -Filter 'h41-*-index.json' | Sort-Object Name)
$md.Add('## 4.1-H healthy baseline-comparison runs')
$md.Add('')
$md.Add('| Block / condition | Workload | Runs | Responses by status | 200 with invalid body | Round-1 p50 / max (ms) | Rounds 2–5 p50 (ms) | Max any round (ms) | Peak overlap | TP threads max (queue max), round 1 | Pool busy max | CMS conns created, round 1 | PG 53300 lines | Worker Min Limit | Coverage failures |')
$md.Add('| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |')
$stackLines = [System.Collections.Generic.List[string]]::new()
$classLines = [System.Collections.Generic.List[string]]::new()
$signalLines = [System.Collections.Generic.List[string]]::new()
foreach ($indexFile in $indexFiles) {
    $index = Get-Content $indexFile.FullName -Raw | ConvertFrom-Json -DateKind String
    $prefix = $indexFile.FullName -replace '-index\.json$', ''
    $roundRows = @(Import-Csv "$prefix-rounds.csv")
    $stackRows = if (Test-Path "$prefix-stacks.csv") { @(Import-Csv "$prefix-stacks.csv") } else { @() }
    foreach ($workload in @($index.workloads)) {
        $runs = @($index.runs | Where-Object { $_.workload -eq $workload })
        $rows = @($roundRows | Where-Object { $_.workload -eq $workload })
        $statuses = [ordered]@{}
        $invalid200 = 0
        $classified = [ordered]@{}
        $signals = [ordered]@{}
        $pg53300 = 0
        foreach ($run in $runs) {
            foreach ($round in @($run.rounds)) {
                foreach ($property in $round.summary.statusHistogram.PSObject.Properties) { Add-Count -Map $statuses -Key $property.Name -By ([int]$property.Value) }
            }
            $summaryFile = Get-ChildItem $OutputDirectory -Filter "$($run.baseLabel)-burst-*-summary.json" | Sort-Object LastWriteTime | Select-Object -Last 1
            $cmsLog = Join-Path $OutputDirectory "$($run.baseLabel)-cms.log"
            $pgLog = Join-Path $OutputDirectory "$($run.baseLabel)-pg.log"
            if (Test-Path $pgLog) { $pg53300 += @(Select-String -LiteralPath $pgLog -SimpleMatch 'sorry, too many clients already').Count }
            if (-not $summaryFile -or -not (Test-Path $cmsLog)) { continue }
            $summary = Get-Content $summaryFile.FullName -Raw | ConvertFrom-Json -DateKind String
            $responseRows = @(foreach ($round in $summary.roundSummaries) { Import-Csv (Join-Path $OutputDirectory $round.csv) })
            $invalid200 += @($responseRows | Where-Object { $_.statusCode -eq '200' -and $_.bodyValid -ne 'True' }).Count
            $entries = Get-DmsCmsLogEntry -Path $cmsLog
            if (@($responseRows | Where-Object { $_.statusCode -ne '200' }).Count -gt 0) {
                foreach ($response in @(Get-DmsResponseClassification -Rows $responseRows -Entries $entries | Where-Object { $_.statusCode -ne 200 })) {
                    $contract = if ($response.statusCode -eq 503) { if ($response.dependencyContract) { ', contract ok' } else { ', CONTRACT VIOLATED' } } else { '' }
                    Add-Count -Map $classified -Key ('{0} {1}{2}{3} ← {4}' -f $response.statusCode, $response.stage,
                        $(if ($response.category) { " ($($response.category))" } else { '' }), $contract, $response.detail)
                }
            }
            $windowStart = ConvertTo-DmsUtcInstant $summary.burstWindow.startUtc
            $windowEnd = ConvertTo-DmsUtcInstant $summary.burstWindow.endUtc
            $counts = (Get-DmsSignalCount -Entries $entries -StartUtc $windowStart -EndUtc $windowEnd.AddSeconds(5)).counts
            foreach ($name in $counts.Keys) {
                foreach ($detail in $counts[$name].byLevelCategoryTrigger.GetEnumerator()) { Add-Count -Map $signals -Key "$name [$($detail.Key)]" -By $detail.Value }
                if ($counts[$name].total -eq 0 -and -not $signals.Contains("$name [none]")) { $signals["$name [none]"] = 0 }
            }
        }
        $round1 = @($rows | Where-Object { $_.round -eq '1' })
        $later = @($rows | Where-Object { $_.round -ne '1' })
        $label = "$($index.block) / $($index.condition)"
        $md.Add(('| {0} | {1} | {2} | {3} | {4} | {5} / {6} | {7} | {8} | {9} | {10} ({11}) | {12} | {13} | {14} | {15} | {16} |' -f $label, $workload, $runs.Count,
                (Format-Map $statuses), $invalid200,
                (Format-Range $round1.p50Ms), (Format-Range $round1.maxMs), (Format-Range $later.p50Ms), (Format-Range $rows.maxMs),
                (Format-Range $rows.peakOverlap), (Format-Range $round1.tpThreadsMax), (Format-Range $round1.tpQueueMax),
                (Format-Range $rows.poolBusyMax), (Format-Range $round1.cmsConnCreated), $pg53300,
                ((@($runs | ForEach-Object { $_.workerMinLimit }) | Select-Object -Unique) -join ', '),
                (@($runs | ForEach-Object { @($_.samplerRequiredFailures).Count } | Measure-Object -Sum).Sum)))
        if ($classified.Count -gt 0) {
            $classLines.Add("- **$label, $workload**: $(@($classified.GetEnumerator() | ForEach-Object { "$($_.Value) × $($_.Key)" }) -join '; ')")
        }
        $signalLines.Add("- **$label, $workload**: $(@($signals.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '; ')")

        foreach ($offsetGroup in (@($stackRows | Where-Object { $_.workload -eq $workload }) | Group-Object offsetSeconds | Sort-Object { [double]$_.Name })) {
            $captures = @($offsetGroup.Group)
            $authSync = @($captures | ForEach-Object { Get-DmsAuthenticationSyncWaitCount -Path (Join-Path $OutputDirectory $_.file) })
            $roundEnds = [ordered]@{}
            foreach ($capture in $captures) { Add-Count -Map $roundEnds -Key $capture.roundEnd }
            $stackLines.Add(('| {0} | {1} | {2} | {3} → {4} | {5} | {6} | {7} | {8} | {9} | {10} / {11} | {12} → {13} | {14} / {15} | {16} |' -f $label, $workload, $offsetGroup.Name,
                    (Format-Range $captures.requestedS 2), (Format-Range $captures.completedS 2), (Format-Map $roundEnds), (Format-Range $captures.tpWorkers),
                    (Format-Range $captures.resolverWait), (Format-Range $authSync), (Format-Range $captures.otherSyncWait),
                    (Format-Range $captures.npgsqlActive), (Format-Range $captures.activeOther),
                    (Format-Range $captures.authorizedAtRequest), (Format-Range $captures.authorizedAtCompletion),
                    ((@($captures.tpThreadsRange) | Select-Object -Unique) -join ', '), ((@($captures.tpQueueRange) | Select-Object -Unique) -join ', '),
                    (Format-Range $captures.tpParked)))
        }
        $json.healthy += [pscustomobject]@{ block = $index.block; condition = $index.condition; resourceProfile = $index.resourceProfile; workload = $workload
            runs = $runs.Count; statuses = $statuses; invalid200 = $invalid200; nonOkClassified = $classified; signals = $signals; pg53300 = $pg53300
            cmsImage = $index.conditionState.cmsImage; effectiveConfiguration = $index.conditionState.effectiveConfiguration }
    }
}
$md.Add('')
$md.Add('### Non-200 responses of the healthy runs, by stage (correlation id → CMS log)')
$md.Add('')
if ($classLines.Count -eq 0) { $md.Add('None.') } else { $classLines | ForEach-Object { $md.Add($_) } }
$md.Add('')
$md.Add('### Dependency log signals in the healthy burst windows (summed over runs)')
$md.Add('')
$signalLines | ForEach-Object { $md.Add($_) }
$md.Add('')
$md.Add('### Managed stacks, round 1')
$md.Add('')
$md.Add('| Block / condition | Workload | Offset (s) | Interval (s) | Round end vs capture | TP workers | Resolver wait | Auth frame in sync wait | Other sync wait | Active (Npgsql / other) | Authorized: request → completion | Threads / queue across interval | Parked |')
$md.Add('| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |')
$stackLines | ForEach-Object { $md.Add($_) }

# ---------------------------------------------------------------- 4.1-O
$md.Add('')
$md.Add('## 4.1-O injected outage runs')
$md.Add('')
$md.Add('| Profile | Run | CMS log complete | Fault held (s) | Responses by status / stage / category | 503 without the dependency contract | 200 with invalid body | Last non-200 after removal (s) | First 200 after removal (s) | Snapshot published after removal (s) | Observed after removal (s) | Recovered within bound | JWKS by phase:status:keys | JWKS first 200 after removal (s) | Stacks: workers / parked / auth sync waits | TP threads max (queue max) |')
$md.Add('| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |')
$outageSignalLines = [System.Collections.Generic.List[string]]::new()
foreach ($indexFile in @(Get-ChildItem $OutputDirectory -Filter 'o41-*-index.json' | Sort-Object Name)) {
    $index = Get-Content $indexFile.FullName -Raw | ConvertFrom-Json -DateKind String
    foreach ($run in @($index.runs)) {
        $stages = [ordered]@{}
        if ($run.PSObject.Properties['byStatusStageCategory'] -and $run.byStatusStageCategory) {
            foreach ($property in $run.byStatusStageCategory.PSObject.Properties) { $stages[$property.Name] = $property.Value }
        }
        $timeline = if ($run.PSObject.Properties['timeline']) { $run.timeline } else { $null }
        $jwks = [ordered]@{}
        if ($timeline) { foreach ($property in $timeline.jwksByPhaseStatusKeys.PSObject.Properties) { $jwks[$property.Name] = $property.Value } }
        $stacks = if ($run.PSObject.Properties['stacks']) { @($run.stacks) } else { @() }
        $stackText = (@($stacks | ForEach-Object { '{0}/{1}/{2}' -f $_.threadPoolWorkers, $_.categories.'threadpool-parked', $_.authenticationSyncWaits }) -join ', ')
        $live = if ($run.PSObject.Properties['livemetrics']) { $run.livemetrics } else { $null }
        $member = { param($object, $name) if ($object -and $object.PSObject.Properties[$name] -and $null -ne $object.$name) { $object.$name } else { 'n/a' } }
        # Completeness is recomputed from the retained files: the CMS log's first line must
        # not be later than the burst start (a later first line means rotation lost lines).
        $logComplete = 'n/a'
        $burstSummaryFile = Get-ChildItem $OutputDirectory -Filter "$($run.baseLabel)-burst-*-summary.json" | Sort-Object LastWriteTime | Select-Object -Last 1
        $runLog = Join-Path $OutputDirectory "$($run.baseLabel)-cms.log"
        if ($burstSummaryFile -and (Test-Path $runLog)) {
            $burstStart = ConvertTo-DmsUtcInstant ((Get-Content $burstSummaryFile.FullName -Raw | ConvertFrom-Json -DateKind String).burstWindow.startUtc)
            $firstLine = Get-DmsCmsLogFirstUtc -Path $runLog
            $logComplete = [bool]($firstLine -and $firstLine -le $burstStart)
        }
        $md.Add(('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13} | {14} | {15} |' -f $index.resourceProfile, $run.baseLabel,
                $logComplete,
                $(if ($timeline) { $timeline.faultHeldSeconds } else { 'n/a' }), (Format-Map $stages),
                $(if ($run.PSObject.Properties['non200WithoutDependencyContract']) { $run.non200WithoutDependencyContract } else { 'n/a' }),
                $(if ($run.PSObject.Properties['invalid200Bodies']) { $run.invalid200Bodies } else { 'n/a' }),
                $(if ($timeline -and $null -ne $timeline.lastNonOkAfterRemovalSeconds) { $timeline.lastNonOkAfterRemovalSeconds } else { '—' }),
                $(if ($timeline) { $timeline.firstOkAfterRemovalSeconds } else { 'n/a' }),
                (& $member $timeline 'firstPublicationAfterRemovalSeconds'), (& $member $timeline 'observedAfterRemovalSeconds'),
                $(if ($timeline) { $timeline.recoveredWithinBound } else { 'n/a' }), (Format-Map $jwks),
                $(if ($timeline) { $timeline.jwksFirstOkAfterRemovalSeconds } else { 'n/a' }), $stackText,
                $(if ($live) { '{0} ({1})' -f $live.threadPoolThreadsMax, $live.threadPoolQueueMax } else { 'n/a' })))
        if ($run.PSObject.Properties['signals'] -and $run.signals) {
            $parts = @(foreach ($property in $run.signals.counts.PSObject.Properties) {
                    $details = @($property.Value.byLevelCategoryTrigger.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '
                    '{0}={1}{2}' -f $property.Name, $property.Value.total, $(if ($details) { " [$details]" } else { '' })
                })
            $loads = @($run.signals.loadFailures | ForEach-Object { "$($_.utc.Substring(11, 12)) $($_.message -replace '\. .*$', '')" })
            $publications = @($run.signals.publications | ForEach-Object { "$($_.utc.Substring(11, 12)) $($_.message)" })
            $outageSignalLines.Add("- **$($run.baseLabel)**: $($parts -join '; ')$(if ($loads) { ". Load failures: $($loads -join ' | ')" }). Publications: $(if ($publications) { $publications -join ' | ' } else { 'none' })")
        }
        $json.outage += $run
    }
}
$md.Add('')
$md.Add('### Dependency log signals per outage run')
$md.Add('')
$outageSignalLines | ForEach-Object { $md.Add($_) }

$md | Set-Content -LiteralPath (Join-Path $OutputDirectory 'h41-report.md') -Encoding utf8
[pscustomobject]$json | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'h41-report.json') -Encoding utf8
Write-Output "Report written: $(Join-Path $OutputDirectory 'h41-report.md')"
