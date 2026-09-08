# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Computes which scheduled-smoke-test matrix legs a workflow run should execute.

.DESCRIPTION
    The scheduled smoke-test workflow builds and exercises four legs: the cross product of two
    Data Standard versions (5.2.0, 6.1.0) and two database engines (postgresql, mssql). Running
    every leg on every pull request is wasteful when a change can only affect a subset of them, so
    this script classifies the PR's changed files against a path map and returns only the legs a
    change could plausibly affect. Scheduled and manually dispatched runs are not narrowed; they
    always run the full matrix.

    Safety rules (the result is never empty and never under-selects):
      - Any event other than "pull_request" (schedule, workflow_dispatch, etc.) returns all four
        legs unconditionally.
      - On "pull_request", every changed file is classified against the path map below; the
        result is the union of the legs selected by any changed file, in the canonical leg order.
      - A changed file that does not match any pattern, an empty/absent changed-file list, or a
        pattern-match failure each fall back to selecting all four legs.

    Path pattern glob semantics:
      - A pattern with no wildcard is compared to each changed file ordinally (exact match); for
        example "eng/docker-compose/.env.smoke" does not match "eng/docker-compose/.env.smoke.ds61".
      - "*" matches zero or more characters within a single path segment; it never matches "/".
      - "**" matches zero or more whole path segments, including zero. "eng/smoke_test/**" matches
        every path nested beneath "eng/smoke_test/". "**/OpenApiDocument.cs" matches a nested file
        such as "src/.../OpenApiDocument.cs" as well as a root-level "OpenApiDocument.cs" (the zero
        segment case).
    Patterns are converted to anchored regular expressions to implement these semantics.

.PARAMETER EventName
    The GitHub Actions event name (e.g. "pull_request", "schedule", "workflow_dispatch").

.PARAMETER ChangedFiles
    Repo-relative changed file paths. Only consulted when EventName is "pull_request".

.PARAMETER ListPathPatterns
    When set, emit every path pattern in the classification map, one per line, and exit without
    emitting legs; EventName and ChangedFiles are not consulted and must not be supplied. This seam
    is consumed by a workflow drift-guard test that asserts the workflow's own trigger paths and
    this script's path map never go out of sync.
#>
[CmdletBinding(DefaultParameterSetName = "Select")]
param(
    [Parameter(Mandatory, ParameterSetName = "Select")]
    [string]$EventName,

    [Parameter(ParameterSetName = "Select")]
    [string[]]$ChangedFiles = @(),

    [Parameter(Mandatory, ParameterSetName = "List")]
    [switch]$ListPathPatterns
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Canonical leg order. Every full-matrix selection and every union selection below preserves this
# order, regardless of the order in which changed files or path-map entries are enumerated.
$allLegs = @(
    [ordered]@{
        standard_version  = "5.2.0"
        database_engine   = "postgresql"
        package_name      = "EdFi.Api.Smoke.Template.PostgreSql.5.2.0"
        provenance_file   = "populated.template.intoto.jsonl"
        environment_file  = "./.env.smoke"
        run_ods_sdk_tests = $true
    }
    [ordered]@{
        standard_version  = "6.1.0"
        database_engine   = "postgresql"
        package_name      = "EdFi.Api.Smoke.Template.PostgreSql.6.1.0"
        provenance_file   = "populated.template.6.1.0.intoto.jsonl"
        environment_file  = "./.env.smoke.ds61"
        run_ods_sdk_tests = $false
    }
    [ordered]@{
        standard_version  = "5.2.0"
        database_engine   = "mssql"
        package_name      = "EdFi.Api.Smoke.Template.MsSql.5.2.0"
        provenance_file   = "populated.template.mssql.intoto.jsonl"
        environment_file  = "./.env.smoke"
        run_ods_sdk_tests = $true
    }
    [ordered]@{
        standard_version  = "6.1.0"
        database_engine   = "mssql"
        package_name      = "EdFi.Api.Smoke.Template.MsSql.6.1.0"
        provenance_file   = "populated.template.mssql.6.1.0.intoto.jsonl"
        environment_file  = "./.env.smoke.ds61"
        run_ods_sdk_tests = $false
    }
)

$legPostgres52 = 0
$legPostgres61 = 1
$legMssql52 = 2
$legMssql61 = 3
$allLegIndices = @($legPostgres52, $legPostgres61, $legMssql52, $legMssql61)

# Pattern -> the indices (into $allLegs) that a matching changed file selects.
$pathPatternMap = [ordered]@{
    "eng/docker-compose/mssql.yml"                   = @($legMssql52, $legMssql61)
    "eng/docker-compose/.env.mssql"                  = @($legMssql52, $legMssql61)
    "eng/docker-compose/postgresql.yml"              = @($legPostgres52, $legPostgres61)
    "eng/docker-compose/.env.smoke"                  = @($legPostgres52, $legMssql52)
    "eng/docker-compose/.env.smoke.ds61"             = @($legPostgres61, $legMssql61)
    ".github/workflows/scheduled-smoke-test.yml"     = $allLegIndices
    ".github/workflows/build-populated-template.yml" = $allLegIndices
    "build-sdk.ps1"                                  = $allLegIndices
    "eng/smoke_test/**"                              = $allLegIndices
    "eng/sdkGen/**"                                  = $allLegIndices
    "eng/DatabaseTemplates/**"                       = $allLegIndices
    "eng/docker-compose/start-local-dms.ps1"         = $allLegIndices
    "eng/docker-compose/local-dms.yml"               = $allLegIndices
    "eng/docker-compose/local-config.yml"            = $allLegIndices
    "eng/docker-compose/env-utility.psm1"            = $allLegIndices
    "eng/Dms-Management.psm1"                        = $allLegIndices
    "src/Directory.Packages.props"                   = $allLegIndices
    "**/OpenApiDocument.cs"                          = $allLegIndices
}

if ($ListPathPatterns) {
    foreach ($pattern in $pathPatternMap.Keys) {
        Write-Output $pattern
    }
    return
}

function ConvertTo-PathPatternRegex {
    <#
    Converts one path-glob pattern (see the script's comment-based help for the "*"/"**"
    semantics) into a single anchored, ordinal regular expression string.
    #>
    param([Parameter(Mandatory)][string]$Pattern)

    $doubleStarMarker = "{0}DBLSTAR{0}" -f [char]1
    $singleStarMarker = "{0}SGLSTAR{0}" -f [char]1

    $withMarkers = $Pattern.Replace("**", $doubleStarMarker).Replace("*", $singleStarMarker)
    $escaped = [regex]::Escape($withMarkers)

    $doubleStarPattern = [regex]::Escape($doubleStarMarker)
    $singleStarPattern = [regex]::Escape($singleStarMarker)

    # "/**/ " in the middle of a pattern matches zero or more whole path segments.
    $escaped = $escaped -replace ('/' + $doubleStarPattern + '/'), '(?:.*/)?'
    # A leading "**/ " also matches a root-level path (the zero-segment case).
    $escaped = $escaped -replace ('^' + $doubleStarPattern + '/'), '(?:.*/)?'
    # A trailing "/** " matches everything nested beneath the preceding segment.
    $escaped = $escaped -replace ('/' + $doubleStarPattern + '$'), '(?:/.*)?'
    # A "**" left standing alone (not adjacent to a slash) matches anything.
    $escaped = $escaped -replace $doubleStarPattern, '.*'
    # A single "*" matches within one path segment only.
    $escaped = $escaped -replace $singleStarPattern, '[^/]*'

    return '^' + $escaped + '$'
}

function Get-SelectedLegIndexSet {
    param(
        [Parameter(Mandatory)][string]$EventName,
        [string[]]$ChangedFiles,
        [System.Collections.Specialized.OrderedDictionary]$PathPatternMap,
        [int[]]$AllLegIndices
    )

    if ($EventName -ne "pull_request") {
        return $AllLegIndices
    }

    $files = @($ChangedFiles | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($files.Count -eq 0) {
        return $AllLegIndices
    }

    $patternRegexes = [ordered]@{}
    foreach ($pattern in $PathPatternMap.Keys) {
        $patternRegexes[$pattern] = ConvertTo-PathPatternRegex -Pattern $pattern
    }

    $selected = New-Object 'bool[]' $AllLegIndices.Count

    foreach ($file in $files) {
        $matchedAny = $false
        try {
            foreach ($pattern in $PathPatternMap.Keys) {
                if ($file -cmatch $patternRegexes[$pattern]) {
                    $matchedAny = $true
                    foreach ($index in $PathPatternMap[$pattern]) {
                        $selected[$index] = $true
                    }
                }
            }
        }
        catch {
            # A pattern-match failure is treated the same as an unmapped file: fail safe to the
            # full matrix rather than silently under-selecting.
            return $AllLegIndices
        }

        if (-not $matchedAny) {
            return $AllLegIndices
        }
    }

    return @($AllLegIndices | Where-Object { $selected[$_] })
}

$selectedIndices = Get-SelectedLegIndexSet `
    -EventName $EventName `
    -ChangedFiles $ChangedFiles `
    -PathPatternMap $pathPatternMap `
    -AllLegIndices $allLegIndices

$selectedLegs = @($selectedIndices | ForEach-Object { [PSCustomObject]$allLegs[$_] })

Write-Output ($selectedLegs | ConvertTo-Json -Compress -AsArray)
