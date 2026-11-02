# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Opt-in metadata only. Raw command output and exception messages stay private.

function Write-CdcCleanupEvent {
    param([Collections.IDictionary] $Checkpoint)
    if (-not $env:CDC_API_CLEANUP_EVENTS) { return }
    try {
        # Bound collection as well as export. Diagnostic failures never replace cleanup results.
        if ((Get-Item -LiteralPath $env:CDC_API_CLEANUP_EVENTS -ErrorAction Stop).Length -ge 131072) { return }
        $Checkpoint | ConvertTo-Json -Depth 6 -Compress | Add-Content -LiteralPath $env:CDC_API_CLEANUP_EVENTS -ErrorAction Stop
    } catch { Write-Warning 'CDC_API_CLEANUP_DIAGNOSTICS_UNAVAILABLE' -WarningAction Continue }
}

function Invoke-CdcCleanupStep {
    <# .SYNOPSIS
    Executes cleanup with opt-in, bounded metadata checkpoints and preserves the original failure.
    #>
    param(
        [Parameter(Mandatory)][Alias('Step')][string] $CleanupStep,
        [ValidateSet('PowerShell', 'docker', 'api-schema-tools')][string] $Command = 'PowerShell',
        [Parameter(Mandatory)][scriptblock] $Action,
        [switch] $NativeCommand
    )
    if (-not $env:CDC_API_CLEANUP_EVENTS) { & $Action; return }
    $checkpoint = [ordered]@{ Step = $CleanupStep; Command = $Command; Status = 'Running'
        StartedUtc = [DateTimeOffset]::UtcNow.ToString('O'); ElapsedMilliseconds = 0L }
    Write-CdcCleanupEvent $checkpoint
    $timer = [Diagnostics.Stopwatch]::StartNew()
    if ($NativeCommand) { $PSNativeCommandUseErrorActionPreference = $false }
    try {
        & $Action
        if ($NativeCommand) {
            $checkpoint.ExitCode = $LASTEXITCODE
            if ($LASTEXITCODE -ne 0) { throw 'CDC cleanup command failed.' }
        }
        $checkpoint.Status = 'Passed'
    } catch {
        $checkpoint.Status = 'Failed'
        try {
            $ErrorActionPreference = 'Stop'
            . (Join-Path $PSScriptRoot '../ci/cdc-runbook-diagnostics.ps1')
            $checkpoint.Failure = Get-CdcRunbookErrorDiagnostic $_ -Category ChildProcessFailure -Phase Teardown
        } catch { # Do not replace the cleanup exception if metadata cannot be collected.
            $checkpoint.Failure = @{}
        }
        throw
    } finally {
        $checkpoint.ElapsedMilliseconds = $timer.ElapsedMilliseconds
        Write-CdcCleanupEvent $checkpoint
    }
}

Export-ModuleMember -Function Invoke-CdcCleanupStep
