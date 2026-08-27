# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
Pulls container images, retrying transient registry failures with jittered exponential backoff.

.DESCRIPTION
One CI job builds the DMS and Configuration Service images, and many downstream jobs then pull those
same tags at nearly the same moment. An unguarded pull turns a momentary registry error into a failed
lane, and a retry with a fixed or purely exponential delay turns those jobs into a synchronized herd
that re-hits the registry in lockstep.

Each image carries its own attempt budget. After a failed attempt the wait is drawn uniformly from
[base/2, base), where base doubles per attempt until it reaches MaxDelaySeconds. Neither bound of
that band ever decreases, so waits trend upward, but the bands stop being disjoint once the cap
binds: every band past the one where the base pins to MaxDelaySeconds is identical to it, and a cap
that falls between two doublings makes that first pinned band overlap its predecessor as well.
Either way a later retry can draw a shorter wait than an earlier one. What holds at every attempt is
the random draw, which is what keeps concurrent jobs from retrying in unison.

Docker's output is never captured, so whatever the registry said about a failure stays visible in the
job log, and the final failure names the image and the attempt count rather than letting a missing
image pass as success.

.PARAMETER Images
Newline-separated image references to pull. Blank and whitespace-only lines are ignored.

.PARAMETER MaxAttempts
Maximum pull attempts per image.

.PARAMETER InitialDelaySeconds
Backoff base for the first retry, in seconds.

.PARAMETER MaxDelaySeconds
Upper bound on the backoff base, in seconds.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [AllowEmptyString()]
    [string]
    $Images,

    [ValidateRange(1, 100)]
    [int]
    $MaxAttempts = 5,

    [ValidateRange(1, 3600)]
    [int]
    $InitialDelaySeconds = 3,

    [ValidateRange(1, 3600)]
    [int]
    $MaxDelaySeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# A failed pull has to reach the retry loop as an exit code. Where a non-zero native exit is promoted
# to a terminating error, the first transient failure would abort the run instead of being retried.
$PSNativeCommandUseErrorActionPreference = $false

# A block scalar in the calling workflow contributes a trailing newline, so the empty-line filter is
# required. YAML strips the block's indentation before the value ever arrives, which leaves the Trim
# defensive against a hand-edited input rather than necessary for the call sites.
$imageList = @(
    $Images -split "\r?\n" |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_.Length -gt 0 }
)

if ($imageList.Count -eq 0) {
    # A mis-wired input must fail here. Pulling nothing and reporting success would let a job run on
    # whatever images happen to be on the runner.
    throw "No images were supplied to pull. Provide one image reference per line in the 'images' input."
}

foreach ($image in $imageList) {
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        Write-Output "Pulling $image (attempt $attempt of $MaxAttempts)."

        # Deliberately uncaptured: no assignment, pipeline, or subexpression, so Docker's progress and
        # its registry error stream straight to the job log and $LASTEXITCODE is Docker's own. The `--`
        # keeps an image reference from ever being parsed as a flag.
        docker pull -- $image

        if ($LASTEXITCODE -eq 0) {
            Write-Output "Pulled $image on attempt $attempt of $MaxAttempts."
            break
        }

        Write-Output "docker pull failed for $image with exit code $LASTEXITCODE on attempt $attempt of $MaxAttempts. The Docker output above carries the registry error."

        if ($attempt -eq $MaxAttempts) {
            throw "Failed to pull $image after $MaxAttempts attempt(s)."
        }

        # Equal jitter over an exponential base that doubles until it reaches $MaxDelaySeconds. Each
        # wait is at least half the base and less than the base, so the bands trend upward, but they
        # stop being disjoint once the cap binds: bands repeat while the base is pinned, and a cap
        # that falls between two doublings makes the first pinned band overlap its predecessor too.
        # Consecutive waits are therefore not strictly increasing. The random component, not the
        # growth, is what stops concurrent jobs retrying in unison.
        # The [double] cast is load-bearing. Without it PowerShell binds Math.Min(Int32, Int32) and
        # narrows the doubling term instead of widening the cap, so once the term passes Int32 the
        # script dies on a conversion error partway through a budget ValidateRange calls legal.
        $baseSeconds = [Math]::Min([double] $MaxDelaySeconds, $InitialDelaySeconds * [Math]::Pow(2, $attempt - 1))
        $halfMilliseconds = [int] ($baseSeconds * 500)
        $delayMilliseconds = $halfMilliseconds + (Get-Random -Minimum 0 -Maximum $halfMilliseconds)

        Write-Output "Waiting $delayMilliseconds ms before attempt $($attempt + 1) of $MaxAttempts for $image."
        Start-Sleep -Milliseconds $delayMilliseconds
    }
}
