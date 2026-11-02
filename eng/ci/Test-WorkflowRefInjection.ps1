# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
    .SYNOPSIS
        Fails when a workflow or composite action writes a ref or tag name, or a secret, into a run
        script.
    .DESCRIPTION
        Scans .github/workflows/*.yml and *.yaml and .github/actions/**/action.yml and action.yaml
        with eng/ci/workflow-ref-guard.psm1, reports each finding as a GitHub error annotation, and
        exits 1 when there is any. Pass the ref or secret through env and read "$NAME" (bash) or
        $env:NAME (pwsh) instead.
    .PARAMETER Root
        The repository root to scan. Defaults to the repository this script is in.
#>
[CmdletBinding()]
param(
    [string]
    $Root = (Join-Path $PSScriptRoot '../..')
)

$ErrorActionPreference = 'Stop'

Import-Module -Name (Join-Path $PSScriptRoot 'workflow-ref-guard.psm1') -Force

$Root = (Resolve-Path -LiteralPath $Root).Path
$files = @(
    Get-ChildItem -LiteralPath (Join-Path $Root '.github/workflows') -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.yml', '.yaml' }
    Get-ChildItem -LiteralPath (Join-Path $Root '.github/actions') -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in 'action.yml', 'action.yaml' }
) | ForEach-Object FullName

$findings = @(Find-WorkflowRefInjection -Path $files -Root $Root)

foreach ($finding in $findings) {
    $message = if ($finding.Problem -eq 'InvalidYaml') {
        "This file is not YAML GitHub can read: $($finding.Expression)"
    }
    elseif ($finding.Problem -eq 'MergeKey') {
        "A merge key (<<) can bring in a run or env this guard does not read. Write the mapping out in full."
    }
    elseif ($finding.Problem -eq 'Unsupported') {
        "'$($finding.Expression)' builds the whole env from a ref or secret, so this guard cannot see which names it sets. Write env as a mapping of names to values."
    }
    else {
        "$($finding.Expression) is written into a run script. Pass it through env: and read it as `"`$NAME`" (bash) or `$env:NAME (pwsh)."
    }
    Write-Output "::error file=$($finding.File),line=$($finding.Line)::$message"
}

if ($findings.Count -gt 0) {
    Write-Output "$($findings.Count) finding(s) in run scripts across $($files.Count) file(s)."
    exit 1
}

Write-Output "No ref, tag or secret expression in a run script across $($files.Count) file(s)."
