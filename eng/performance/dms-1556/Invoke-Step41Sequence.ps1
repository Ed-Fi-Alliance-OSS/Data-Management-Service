# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
DMS-1556 step 4.1-H: the healthy baseline-comparison runs on the fixed image, as run for
the record.

.DESCRIPTION
Default settings throughout ('baseline' = no control overlay). For each resource profile
in -ResourceProfiles, two blocks, each starting from a recreated PostgreSQL:

  <profile>-catalog  cold-87x87 + warm-87x87,   -Repetitions each
  <profile>-stress   cold-256x128 + warm-256x128, -Repetitions each

Then the supplementary headroom block under P-runner-approx (P-7.4: an addition to the
default gate, never a replacement): max_connections=200, cold-256x128 + warm-256x128,
-HeadroomRepetitions each. Every run has 5 rounds with body validation, samplers with the
coverage gate, and managed stacks at -StackCaptureOffsetsSeconds into round 1. One dump
per block, after its last run and outside every timed window (-DumpOncePerBlock; a
deviation from one per run, chosen at step 4.1 to limit host memory pressure). The stack is restored to P-runner-approx baseline with a
recreated PostgreSQL at the end. A failing block is reported and the sequence continues.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('p-dev', 'p-runner-approx')]
    [string[]] $ResourceProfiles = @('p-dev', 'p-runner-approx'),

    [ValidateRange(1, 10)]
    [int] $Repetitions = 5,

    [ValidateRange(0, 10)]
    [int] $HeadroomRepetitions = 3,

    [double[]] $StackCaptureOffsetsSeconds = @(0, 2, 8, 14),

    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$setCondition = Join-Path $PSScriptRoot 'Set-Dms1556StackCondition.ps1'
$batch = Join-Path $PSScriptRoot 'Invoke-ControlBatch.ps1'
$blocks = [System.Collections.Generic.List[hashtable]]::new()
foreach ($resourceProfile in $ResourceProfiles) {
    $blocks.Add(@{ Profile = $resourceProfile; Block = "$resourceProfile-catalog"; Condition = 'baseline'; Workloads = @('cold-87x87', 'warm-87x87'); Repetitions = $Repetitions })
    $blocks.Add(@{ Profile = $resourceProfile; Block = "$resourceProfile-stress"; Condition = 'baseline'; Workloads = @('cold-256x128', 'warm-256x128'); Repetitions = $Repetitions })
}
if ($HeadroomRepetitions -gt 0) {
    $blocks.Add(@{ Profile = 'p-runner-approx'; Block = 'p-runner-approx-stress'; Condition = 'headroom'; Workloads = @('cold-256x128', 'warm-256x128'); Repetitions = $HeadroomRepetitions })
}

foreach ($block in $blocks) {
    if (-not $PSCmdlet.ShouldProcess("$($block.Block) / $($block.Condition)", 'Recompose and run block')) { continue }
    Write-Output ("##### {0} / {1} started {2:o}" -f $block.Block, $block.Condition, [DateTime]::UtcNow)
    try {
        & $setCondition -Condition $block.Condition -ResourceProfile $block.Profile -RecreateDb -Confirm:$false
        & $batch -Condition $block.Condition -ResourceProfile $block.Profile -LabelPrefix h41 -BlockLabel $block.Block `
            -Repetitions $block.Repetitions -Workloads $block.Workloads -StackCaptureOffsetsSeconds $StackCaptureOffsetsSeconds `
            -DumpOncePerBlock -OutputDirectory $OutputDirectory
    }
    catch {
        Write-Output "##### $($block.Block) / $($block.Condition) FAILED: $($_.Exception.Message)"
    }
}

if ($PSCmdlet.ShouldProcess('dms-local', 'Restore P-runner-approx baseline with a recreated PostgreSQL')) {
    & $setCondition -Condition baseline -ResourceProfile p-runner-approx -RecreateDb -Confirm:$false
    Write-Output ("##### sequence finished {0:o}" -f [DateTime]::UtcNow)
}
