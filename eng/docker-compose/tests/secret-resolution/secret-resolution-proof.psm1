# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# The secret resolution harness's verdict on a test run, kept apart from Invoke-SecretResolutionE2E.ps1
# so the pull request lane can exercise it without a stack: the harness itself runs only on a schedule,
# so a break here would otherwise surface only there.

Set-StrictMode -Version Latest

<#
    Every proof the run must have executed and passed. A filter that matches nothing, or a proof that
    ignores itself because a setting did not reach it, leaves dotnet test exiting zero, so the exit
    code alone is not evidence. Renaming or adding a proof means updating this list;
    SecretResolutionProof.Tests.ps1 fails until it matches the proofs the test class declares.
#>
$script:ExpectedProofs = @(
    "It_returns_the_resolved_password_from_the_configuration_service",
    "It_lets_dms_write_and_read_a_resource_through_the_resolved_credential",
    "It_resolves_the_value_the_store_held_first",
    "It_observed_the_inside_read_inside_the_window",
    "It_keeps_the_previous_value_inside_the_window",
    "It_does_not_return_the_rotated_value_before_the_window_can_have_closed",
    "It_returns_the_rotated_value_once_the_window_has_closed",
    "It_keeps_returning_the_rotated_value",
    "It_returns_only_the_previous_or_the_rotated_value"
)

function Get-ExpectedSecretResolutionProof {
    <#
    .SYNOPSIS
    Returns the name of every SecretResolutionPlugin proof a run must have executed and passed.
    #>
    $script:ExpectedProofs
}

function Assert-ProofsExecuted {
    <#
    .SYNOPSIS
    Throws unless the TRX results file shows every expected SecretResolutionPlugin proof executed
    exactly once and every result passed.

    .PARAMETER TrxPath
    The TRX results file the proof run wrote.
    #>
    param([Parameter(Mandatory)] [string] $TrxPath)

    if (-not (Test-Path -LiteralPath $TrxPath)) {
        throw "The SecretResolutionPlugin proofs wrote no results to $TrxPath."
    }

    $content = Get-Content -LiteralPath $TrxPath -Raw
    if ([string]::IsNullOrWhiteSpace($content)) {
        throw "The SecretResolutionPlugin results file $TrxPath is empty."
    }

    # Selected by local name, so a TRX with no Results element reaches the message below instead of
    # failing strict mode's property access.
    [xml]$trx = $content
    $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    if ($results.Count -eq 0) {
        throw "No SecretResolutionPlugin proofs ran; the filter matched nothing."
    }

    $notPassed = @($results | Where-Object { $_.outcome -ne "Passed" })
    if ($notPassed.Count -gt 0) {
        throw "SecretResolutionPlugin proofs did not pass: $(($notPassed | ForEach-Object { "$($_.testName) ($($_.outcome))" }) -join ', ')."
    }

    foreach ($name in $script:ExpectedProofs) {
        if (@($results | Where-Object { $_.testName -eq $name }).Count -ne 1) {
            throw "The SecretResolutionPlugin proof $name did not execute exactly once."
        }
    }

    Write-Output "All $($script:ExpectedProofs.Count) SecretResolutionPlugin proofs executed and passed."
}

Export-ModuleMember -Function Assert-ProofsExecuted, Get-ExpectedSecretResolutionProof
