# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
DMS-1556 step 0.5: the full control sequence as run for the investigation record.

.DESCRIPTION
Matched pairs, each a baseline block immediately followed by its control block, under
P-runner-approx:

  pair-e3        baseline, e3-certificates   (cold-87x87 + warm-87x87)
  pair-e4        baseline, e4-threads        (cold-87x87 + warm-87x87)
  pair-e5        baseline, e5-pool16         (cold-87x87 + warm-87x87)
  pair-headroom  baseline, headroom          (warm-256x128 only; PostgreSQL recreated
                                              before BOTH blocks so both start from a
                                              freshly started server)

The stack is restored to 'baseline' with a recreated PostgreSQL at the end. A failing
block is reported and the sequence continues; every block verifies its own condition.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateRange(1, 10)]
    [int] $Repetitions = 3,

    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$setCondition = Join-Path $PSScriptRoot 'Set-Dms1556StackCondition.ps1'
$batch = Join-Path $PSScriptRoot 'Invoke-ControlBatch.ps1'
$blocks = @(
    @{ Pair = 'pair-e3'; Condition = 'baseline'; Workloads = @('cold-87x87', 'warm-87x87'); RecreateDb = $false }
    @{ Pair = 'pair-e3'; Condition = 'e3-certificates'; Workloads = @('cold-87x87', 'warm-87x87'); RecreateDb = $false }
    @{ Pair = 'pair-e4'; Condition = 'baseline'; Workloads = @('cold-87x87', 'warm-87x87'); RecreateDb = $false }
    @{ Pair = 'pair-e4'; Condition = 'e4-threads'; Workloads = @('cold-87x87', 'warm-87x87'); RecreateDb = $false }
    @{ Pair = 'pair-e5'; Condition = 'baseline'; Workloads = @('cold-87x87', 'warm-87x87'); RecreateDb = $false }
    @{ Pair = 'pair-e5'; Condition = 'e5-pool16'; Workloads = @('cold-87x87', 'warm-87x87'); RecreateDb = $false }
    @{ Pair = 'pair-headroom'; Condition = 'baseline'; Workloads = @('warm-256x128'); RecreateDb = $true }
    @{ Pair = 'pair-headroom'; Condition = 'headroom'; Workloads = @('warm-256x128'); RecreateDb = $true }
)

foreach ($block in $blocks) {
    if (-not $PSCmdlet.ShouldProcess("$($block.Pair) / $($block.Condition)", 'Recompose and run block')) { continue }
    Write-Output ("##### {0} / {1} started {2:o}" -f $block.Pair, $block.Condition, [DateTime]::UtcNow)
    try {
        & $setCondition -Condition $block.Condition -RecreateDb:$block.RecreateDb -Confirm:$false
        & $batch -Condition $block.Condition -BlockLabel $block.Pair -Repetitions $Repetitions `
            -Workloads $block.Workloads -OutputDirectory $OutputDirectory
    }
    catch {
        Write-Output "##### $($block.Pair) / $($block.Condition) FAILED: $($_.Exception.Message)"
    }
}

if ($PSCmdlet.ShouldProcess('dms-local', 'Restore baseline with a recreated PostgreSQL')) {
    & $setCondition -Condition baseline -RecreateDb -Confirm:$false
    Write-Output ("##### sequence finished {0:o}" -f [DateTime]::UtcNow)
}
