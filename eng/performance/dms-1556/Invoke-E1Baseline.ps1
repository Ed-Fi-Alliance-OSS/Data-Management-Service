# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
DMS-1556 step 0.3 driver: the E1 baseline runs for one resource profile.

.DESCRIPTION
Runs -Runs repetitions of the E1 workload (one cold round plus Rounds-1 warm rounds at
TotalRequests/MaxConcurrency, body validation, coordinated samplers) against the running
dms-local stack, and after each run:

  - captures the CMS and PostgreSQL container logs for the run window
    (<label>-cms.log / <label>-pg.log) and counts the correlation signals the spec's
    R-500 definition names (NpgsqlException / TimeoutException in CMS,
    'Failed to fetch public keys' JWKS lines);
  - collects one full dump OUTSIDE the timed burst window (after the run) and records
    Worker Min Limit (Q17), unless -SkipDumps;
  - snapshots the container resource settings (cpu limits, DOTNET_PROCESSOR_COUNT,
    DOTNET_ThreadPool_ForceMinWorkerThreads) and host processor count;
  - classifies the run under the spec's spec section 3.2 definitions: R-500 (a 500 whose window has
    the correlated CMS signals), R-500-uncorrelated (a 500 without them - listed for
    manual review, not silently promoted), R-slow (no 500, any round p99 >= 5 s), or
    neither. R-slow evidence is provisional per the spec.

Writes e1-<profile>-index.json summarizing every run. Individual run failures (including
sampler-coverage failures) are recorded and the remaining runs continue.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Parameters are read by nested functions through script scope; the analyzer does not track that usage.')]
[CmdletBinding()]
param(
    # Evidence label for the resource profile in effect (the profile itself is applied by
    # composing/removing eng/docker-compose/local-resource-runner-approx.yml beforehand).
    [Parameter(Mandatory)]
    [ValidateSet('p-dev', 'p-runner-approx')]
    [string] $ResourceProfile,

    [ValidateRange(1, 20)]
    [int] $Runs = 5,

    [ValidateRange(1, 20)]
    [int] $Rounds = 5,

    [int] $TotalRequests = 87,

    [int] $MaxConcurrency = 87,

    [ValidateRange(60, 3600)]
    [int] $SamplerDurationSeconds = 900,

    [string] $CmsContainerName = 'ed-fi-api-config-service',

    [string] $PgContainerName = 'dms-postgresql',

    [string[]] $SamplerStatsContainers = @('ed-fi-api-config-service', 'dms-postgresql', 'ed-fi-api'),

    [string] $MonitorBaseUrl = 'http://localhost:52323',

    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),

    [switch] $SkipDumps
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory

function Get-ContainerResourceRecord {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)][string] $ContainerName)

    $nanoCpus = docker inspect $ContainerName --format '{{.HostConfig.NanoCpus}}' 2>$null
    $envLines = docker inspect $ContainerName --format '{{range .Config.Env}}{{println .}}{{end}}' 2>$null
    return [pscustomobject]@{
        container = $ContainerName
        cpuLimit  = if ($nanoCpus -and $nanoCpus -ne '0') { [Math]::Round([long]$nanoCpus / 1e9, 2) } else { 'unlimited' }
        env       = @($envLines | Where-Object { $_ -match '^(DOTNET_ThreadPool|DOTNET_PROCESSOR_COUNT|DOTNET_Diagnostic)' })
    }
}

function Get-LogSignalCount {
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Pattern
    )

    if (-not (Test-Path -LiteralPath $Path)) { return 0 }
    return @(Select-String -LiteralPath $Path -Pattern $Pattern -SimpleMatch).Count
}

$environmentRecord = [pscustomobject]@{
    resourceProfile    = $ResourceProfile
    capturedUtc        = [DateTime]::UtcNow.ToString('o')
    hostProcessorCount = [Environment]::ProcessorCount
    cms                = Get-ContainerResourceRecord -ContainerName $CmsContainerName
    postgres           = Get-ContainerResourceRecord -ContainerName $PgContainerName
}
Write-Output "E1 baseline, profile ${ResourceProfile}: CMS cpu=$($environmentRecord.cms.cpuLimit), PG cpu=$($environmentRecord.postgres.cpuLimit), host cores=$($environmentRecord.hostProcessorCount)"

$runRecords = [System.Collections.Generic.List[object]]::new()
for ($run = 1; $run -le $Runs; $run++) {
    $label = "catalog-${TotalRequests}x${MaxConcurrency}-e1-$ResourceProfile-run$run"
    $runStartUtc = [DateTime]::UtcNow
    $runStartIso = $runStartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
    Write-Output "=== Run ${run}/${Runs} ($label) ==="

    $harnessError = $null
    try {
        & (Join-Path $PSScriptRoot 'Invoke-CmsProfileBurst.ps1') `
            -TotalRequests $TotalRequests -MaxConcurrency $MaxConcurrency -Rounds $Rounds `
            -Cold -ValidateBodies -WithSamplers -SamplerDurationSeconds $SamplerDurationSeconds `
            -MonitorBaseUrl $MonitorBaseUrl -SamplerStatsContainers $SamplerStatsContainers `
            -CmsContainerName $CmsContainerName -OutputDirectory $OutputDirectory -RunLabel $label
    }
    catch {
        # Recorded, not fatal to the batch: a coverage failure is itself evidence.
        $harnessError = $_.Exception.Message
        Write-Output "Run ${run} reported: $harnessError"
    }

    # Correlated container logs for exactly this run's window.
    $cmsLogPath = Join-Path $OutputDirectory "$label-cms.log"
    $pgLogPath = Join-Path $OutputDirectory "$label-pg.log"
    docker logs --since $runStartIso $CmsContainerName 2>&1 | Set-Content -LiteralPath $cmsLogPath -Encoding utf8
    docker logs --since $runStartIso $PgContainerName 2>&1 | Set-Content -LiteralPath $pgLogPath -Encoding utf8

    $cmsNpgsql = Get-LogSignalCount -Path $cmsLogPath -Pattern 'NpgsqlException'
    $cmsTimeout = Get-LogSignalCount -Path $cmsLogPath -Pattern 'TimeoutException'
    $cmsJwksFailures = Get-LogSignalCount -Path $cmsLogPath -Pattern 'Failed to fetch public keys'

    # One dump per run, collected OUTSIDE the timed burst window (Q17).
    $workerMinLimit = $null
    if (-not $SkipDumps) {
        try {
            & (Join-Path $PSScriptRoot 'Get-CmsThreadPoolMinLimit.ps1') -MonitorBaseUrl $MonitorBaseUrl `
                -CmsContainerName $CmsContainerName -OutputDirectory $OutputDirectory | Out-Null
            $dumpRecord = Get-ChildItem $OutputDirectory -Filter 'cms-threadpool-*.json' |
                Sort-Object LastWriteTime | Select-Object -Last 1
            if ($dumpRecord) {
                $workerMinLimit = (Get-Content $dumpRecord.FullName -Raw | ConvertFrom-Json).workerMinLimit
            }
        }
        catch {
            Write-Output "Dump collection failed for run ${run}: $($_.Exception.Message)"
        }
    }

    # Read back the summary the harness wrote (present even when it threw on coverage).
    $summaryFile = Get-ChildItem $OutputDirectory -Filter "$label-*-summary.json" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    $statusTotals = @{}
    $p99PerRound = @()
    $peakOverlapPerRound = @()
    $bodyInvalidTotal = 0
    $ttffPerRound = @()
    $coverageFailures = @('summary missing')
    if ($summaryFile) {
        $summary = Get-Content $summaryFile.FullName -Raw | ConvertFrom-Json
        foreach ($round in $summary.roundSummaries) {
            foreach ($property in $round.statusHistogram.PSObject.Properties) {
                $statusTotals[$property.Name] = [int]($statusTotals[$property.Name] ?? 0) + [int]$property.Value
            }
            $p99PerRound += [double]$round.p99Ms
            $peakOverlapPerRound += [int]$round.measuredPeakOverlap
            if ($null -ne $round.bodyInvalidCount) { $bodyInvalidTotal += [int]$round.bodyInvalidCount }
            $ttffPerRound += $round.timeToFirstFailureMs
        }
        # Outer @() around the if-expression: PowerShell unwraps a statement-expression's
        # output on assignment, so an empty failures array would otherwise become $null.
        $coverageFailures = @(
            if ($summary.samplerReport) { $summary.samplerReport.requiredFailures } else { 'no sampler report' }
        )
    }

    $has500 = [int]($statusTotals['500'] ?? 0) -gt 0
    $transportErrors = [int]($statusTotals['0'] ?? 0)
    $maxP99 = if ($p99PerRound.Count -gt 0) { ($p99PerRound | Measure-Object -Maximum).Maximum } else { $null }
    $correlatedSignals = $cmsNpgsql + $cmsTimeout + $cmsJwksFailures

    $classification = if ($has500 -and $correlatedSignals -gt 0) { 'R-500' }
    elseif ($has500) { 'R-500-uncorrelated' }
    elseif ($null -ne $maxP99 -and $maxP99 -ge 5000) { 'R-slow(provisional)' }
    else { 'neither' }

    $record = [pscustomobject]@{
        run                 = $run
        label               = $label
        startedUtc          = $runStartUtc.ToString('o')
        harnessError        = $harnessError
        statusTotals        = $statusTotals
        transportErrors     = $transportErrors
        bodyInvalidTotal    = $bodyInvalidTotal
        p99MsPerRound       = $p99PerRound
        timeToFirstFailureMsPerRound = $ttffPerRound
        peakOverlapPerRound = $peakOverlapPerRound
        cmsNpgsqlExceptions = $cmsNpgsql
        cmsTimeoutExceptions = $cmsTimeout
        cmsJwksFetchFailures = $cmsJwksFailures
        workerMinLimit      = $workerMinLimit
        samplerCoverageFailures = $coverageFailures
        classification      = $classification
    }
    $runRecords.Add($record)
    Write-Output ("Run ${run}: statuses [{0}] maxP99={1}ms overlap={2} coverageFailures={3} -> {4}" -f `
        (($statusTotals.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '),
        $maxP99, (($peakOverlapPerRound | Measure-Object -Maximum).Maximum), @($coverageFailures).Count, $classification)

    Start-Sleep -Seconds 5
}

$index = [pscustomobject]@{
    resourceProfile = $ResourceProfile
    workload        = "catalog-${TotalRequests}x${MaxConcurrency}"
    rounds          = $Rounds
    environment     = $environmentRecord
    runs            = $runRecords
}
$indexPath = Join-Path $OutputDirectory "e1-$ResourceProfile-index.json"
$index | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $indexPath -Encoding utf8
Write-Output "Index written to $indexPath"
Write-Output ("Classifications: " + (($runRecords | ForEach-Object { $_.classification }) -join ', '))
