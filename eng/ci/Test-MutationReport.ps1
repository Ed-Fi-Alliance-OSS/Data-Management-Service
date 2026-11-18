# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Validates one fresh Stryker report and summarizes mutation findings for investigation.
.DESCRIPTION
    Use a separate output directory for each invocation. The workflow also preserves Stryker's
    exit code: a valid report cannot turn a failed build, baseline test, or runner into success.
    Survived and uncovered mutants are informational; incomplete or invalid reports fail.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [Parameter(Mandatory)]
    [string] $Target
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$summary = [System.Collections.Generic.List[string]]::new()
$summary.Add("## Mutation tests: $Target")
$summary.Add('')
$summary.Add("Download artifact **mutation-$Target** for HTML/JSON reports, trace and console logs, and mutant-details.txt.")
$summary.Add('')
$failure = $null

try {
    $reports = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File -Filter 'mutation-report.json')
    if ($reports.Count -ne 1) {
        throw "Expected exactly one mutation-report.json from this run; found $($reports.Count). Inspect console.log and tool-restore.log."
    }

    $report = Get-Content -LiteralPath $reports[0].FullName -Raw | ConvertFrom-Json -AsHashtable
    if ($report -isnot [System.Collections.IDictionary] -or
        $report['files'] -isnot [System.Collections.IDictionary]) {
        throw 'Invalid Stryker report: missing files object.'
    }

    $counts = [ordered]@{
        Killed = 0
        Survived = 0
        NoCoverage = 0
        Timeout = 0
        CompileError = 0
        Ignored = 0
        Pending = 0
    }
    $details = [System.Collections.Generic.List[string]]::new()
    foreach ($file in ($report['files'].Keys | Sort-Object)) {
        $fileResult = $report['files'][$file]
        if ($fileResult -isnot [System.Collections.IDictionary] -or
            $fileResult['mutants'] -isnot [array]) {
            throw "Invalid Stryker report: missing mutants array for $file."
        }
        foreach ($mutant in $fileResult['mutants']) {
            $status = [string]$mutant['status']
            if (-not $counts.Contains($status)) {
                throw "Invalid or incomplete Stryker report: unrecognized status '$status' in $file."
            }
            $counts[$status]++
            if ($status -in @('Survived', 'NoCoverage', 'Timeout', 'Pending')) {
                $start = $mutant['location']['start']
                $details.Add("$status | ${file}:$($start['line']):$($start['column']) | mutant $($mutant['id']) | $($mutant['mutatorName'])")
                # JSON encoding preserves multiline replacements/reasons unambiguously in a text log.
                $details.Add("  Replacement: $(ConvertTo-Json -InputObject $mutant['replacement'] -Compress)")
                $details.Add("  Reason: $(ConvertTo-Json -InputObject $mutant['statusReason'] -Compress)")
                $details.Add("  Covering tests: $(ConvertTo-Json -InputObject $mutant['coveredBy'] -Compress -Depth 10)")
            }
        }
    }

    $detailsPath = Join-Path $OutputDirectory 'mutant-details.txt'
    if ($details.Count -eq 0) { $details.Add('No surviving, uncovered, timed-out, or pending mutants.') }
    $details | Set-Content -LiteralPath $detailsPath -Encoding utf8
    Write-Information ($details -join [Environment]::NewLine) -InformationAction Continue

    $summary.Add('| Status | Mutants |')
    $summary.Add('| --- | ---: |')
    foreach ($status in $counts.Keys) { $summary.Add("| $status | $($counts[$status]) |") }
    $summary.Add('')
    $summary.Add('Timeouts are reported separately; compile errors and ignored mutants are not assertion kills.')

    $executable = $counts.Killed + $counts.Survived + $counts.NoCoverage + $counts.Timeout
    if ($counts.Pending -gt 0) {
        throw "Incomplete mutation run: $($counts.Pending) pending mutant(s)."
    }
    if ($executable -eq 0) {
        throw 'No executable mutants in the report; this does not establish a successful mutation run.'
    }
    $summary.Add('')
    $summary.Add('**Mutation report validated.** Surviving and uncovered mutants are informational and require behavioral triage; this does not establish adequate test coverage. Inspect mutant-details.txt and the HTML report.')
}
catch {
    $failure = $_.Exception.Message
    $summary.Add('')
    $summary.Add("**Mutation report validation failed:** $failure")
}
finally {
    $text = $summary -join [Environment]::NewLine
    $text | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.md') -Encoding utf8
    Write-Information $text -InformationAction Continue
    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        $text | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    }
}

if ($null -ne $failure) { throw $failure }
