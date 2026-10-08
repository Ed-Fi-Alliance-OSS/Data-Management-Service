# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
Regenerates the stack tables (e5-<block>-<condition>-stacks.csv) of retained
Invoke-ControlBatch.ps1 blocks with the current analysis module - no workload runs.

.DESCRIPTION
For every e5-*-index.json in -OutputDirectory, reads each run's burst summary (round
stack captures with their requested/completed times, sampler file paths), its
PostgreSQL log, and its CMS client host, and rebuilds one Get-DmsStackCaptureRecord row
per capture: the capture as the interval it is, with connection counts at both
boundaries and counter ranges across it, and the corrected thread classification. The
previous table is kept beside the new one as -stacks.superseded.csv the first time.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),

    [string] $CmsDatabase = 'edfi_datamanagementservice'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'dms-1556-analysis.psm1') -Force

foreach ($indexFile in Get-ChildItem $OutputDirectory -Filter 'e5-*-index.json') {
    $index = Get-Content $indexFile.FullName -Raw | ConvertFrom-Json
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($run in $index.runs) {
        $summaryFile = Get-ChildItem $OutputDirectory -Filter "$($run.baseLabel)-burst-*-summary.json" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime | Select-Object -Last 1
        if (-not $summaryFile) { continue }
        $summary = Get-Content $summaryFile.FullName -Raw | ConvertFrom-Json
        if (-not $summary.samplerReport) { continue }
        $pgEvents = Get-DmsPgLogEvent -Path (Join-Path $OutputDirectory "$($run.baseLabel)-pg.log")
        foreach ($round in $summary.roundSummaries) {
            foreach ($capture in @(@($round.stackCaptures) | Where-Object { $_ })) {
                $record = Get-DmsStackCaptureRecord -Capture $capture -StackPath (Join-Path $OutputDirectory $capture.file) `
                    -RoundStartUtc (ConvertTo-DmsUtcInstant $round.startUtc) -RoundEndUtc (ConvertTo-DmsUtcInstant $round.endUtc) `
                    -PgEvents $pgEvents -ClientHost $run.cmsClientHost -LivemetricsPath ([string]$summary.samplerReport.files.livemetrics) `
                    -PoolDatabase $CmsDatabase
                $row = [ordered]@{ block = $index.block; condition = $index.condition; workload = $run.workload; rep = $run.rep; round = [int]$round.round }
                foreach ($property in $record.PSObject.Properties) { $row[$property.Name] = $property.Value }
                $rows.Add([pscustomobject]$row)
            }
        }
    }
    $stacksPath = Join-Path $OutputDirectory "e5-$($index.block)-$($index.condition)-stacks.csv"
    $supersededPath = Join-Path $OutputDirectory "e5-$($index.block)-$($index.condition)-stacks.superseded.csv"
    if ((Test-Path -LiteralPath $stacksPath) -and -not (Test-Path -LiteralPath $supersededPath)) {
        Move-Item -LiteralPath $stacksPath -Destination $supersededPath
    }
    $rows | Export-Csv -LiteralPath $stacksPath -NoTypeInformation -Encoding utf8
    Write-Output "$($index.block)/$($index.condition): $($rows.Count) stack capture records -> $(Split-Path -Leaf $stacksPath)"
}
