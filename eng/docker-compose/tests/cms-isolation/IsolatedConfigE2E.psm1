# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Set-StrictMode -Version Latest

function Copy-IsolatedClaimsFixture {
    <#
    .SYNOPSIS
        Copies a test-owned claims directory into a fresh isolated mount without altering its bytes.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([string] $Source, [string] $Destination)

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "The isolated claims fixture must be an existing directory."
    }
    $null = New-Item -ItemType Directory -Path $Destination
    # Preserve relative paths and bytes; neither sort fragments nor flatten their discovery tree.
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force
    return $Destination
}

function Invoke-IsolatedCmsTestRun {
    <#
    .SYNOPSIS
        Retains and validates this launcher's fixed-name TRX before isolated stack teardown.
    .DESCRIPTION
        ExpectedTestNames selects strict policy-scenario validation. Ordinary suite runs may contain
        intentionally skipped backend/provider cases, but must execute a passing test and have no failures.
        Execute and Teardown are the launcher's startup/test and cleanup blocks, injectable in Pester.
    #>
    [CmdletBinding()]
    param(
        [string] $WorkingTrx,
        [string] $ArtifactDirectory,
        [string[]] $ExpectedTestNames = @(),
        [string[]] $ContainerNames = @(),
        [scriptblock] $Execute,
        [scriptblock] $Teardown
    )

    $null = New-Item -ItemType Directory -Path $ArtifactDirectory -Force
    # Preserve an ordinary suite or earlier scenario before removing its fixed-name working result.
    if (Test-Path -LiteralPath $WorkingTrx) {
        Move-Item -LiteralPath $WorkingTrx -Destination (Join-Path $ArtifactDirectory "previous-results.trx")
    }
    $started = [datetime]::UtcNow
    $passed = $false
    try {
        & $Execute
        if (-not (Test-Path -LiteralPath $WorkingTrx)) {
            throw "The isolated CMS run wrote no TRX results; startup/database failures are environment failures, not policy mismatches."
        }
        [xml]$trx = Get-Content -LiteralPath $WorkingTrx -Raw
        $times = $trx.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='Times']")
        if ($null -eq $times -or [datetime]::Parse($times.start).ToUniversalTime() -lt $started) {
            throw "The isolated CMS run returned stale TRX results."
        }
        $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
        if (@($results | Where-Object outcome -eq 'Passed').Count -eq 0 -or @($results | Where-Object outcome -eq 'Failed').Count -gt 0) {
            throw "The isolated CMS run must execute and pass every selected test."
        }
        if ($ExpectedTestNames.Count -gt 0) {
            if (@($results | Where-Object outcome -ne 'Passed').Count -gt 0) {
                throw "The isolated CMS run must execute and pass every selected test."
            }
            if ($results.Count -ne $ExpectedTestNames.Count) {
                throw "The isolated CMS run did not execute exactly the requested scenario."
            }
            foreach ($name in $ExpectedTestNames) {
                if (@($results | Where-Object testName -CEQ $name).Count -ne 1) {
                    throw "The isolated CMS run did not execute the requested test."
                }
            }
        }
        $passed = $true
    }
    finally {
        try {
            # Archive even malformed, stale or failing results, before teardown or another invocation.
            if (Test-Path -LiteralPath $WorkingTrx) {
                Copy-Item -LiteralPath $WorkingTrx -Destination (Join-Path $ArtifactDirectory "results.trx")
            }
            # Deliberately record no exception text, environment, HTTP headers or token responses.
            # Scenario code writes sanitized comparison reports directly into this retained directory.
            $status = if ($passed) { 'Passed' } else { 'Failed: inspect results.trx when present; no complete policy comparison is implied.' }
            Set-Content -LiteralPath (Join-Path $ArtifactDirectory "run-status.txt") -Value $status
        }
        finally {
            if (-not $passed) {
                # Containers disappear during teardown. Retain both output streams, even when
                # startup failed; a missing container or failed capture must not hide the run failure.
                foreach ($container in $ContainerNames) {
                    try {
                        & docker logs --timestamps $container *> (Join-Path $ArtifactDirectory "$container.log")
                        if ($LASTEXITCODE -ne 0) {
                            Write-Warning "Could not capture logs for $container (exit $LASTEXITCODE)." -WarningAction Continue
                        }
                    }
                    catch {
                        Write-Warning "Could not capture logs for $container." -WarningAction Continue
                    }
                }
            }
            if ($passed) {
                & $Teardown
            }
            else {
                # Preserve the run's failure if cleanup fails as well.
                try {
                    & $Teardown
                }
                catch {
                    Write-Warning $_.Exception.Message
                }
            }
        }
    }
}

Export-ModuleMember -Function Copy-IsolatedClaimsFixture, Invoke-IsolatedCmsTestRun
