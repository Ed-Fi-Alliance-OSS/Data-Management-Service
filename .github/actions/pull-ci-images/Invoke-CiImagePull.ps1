# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
Pulls container images, retrying transient registry failures with jittered exponential backoff.

.DESCRIPTION
One CI job builds the DMS and Configuration Service images, and many downstream jobs then pull those
same tags at nearly the same moment. An unguarded pull turns a momentary registry error into a failed
lane, and a retry with a fixed or purely exponential delay turns those jobs into a synchronized herd
that re-hits the registry in lockstep.

Each image carries its own attempt budget. After a failed attempt the wait is drawn uniformly from
[base/2, base), where base doubles per attempt until it reaches MaxDelaySeconds. The delay therefore
grows on every retry while the base is still climbing, and successive retries share one band once the
cap is reached. Either way the random draw keeps concurrent jobs from retrying in unison.

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

# A block scalar in the calling workflow always contributes a trailing newline, and its values are
# indented, so trimming and dropping empties is required rather than cosmetic.
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

        # Equal jitter over an exponential base that doubles until it reaches $MaxDelaySeconds. Every
        # wait is at least half the base and less than the base, so delays grow per retry while the
        # base is still climbing and share one band after it caps. The random component is what keeps
        # concurrent jobs from retrying in unison, at either stage.
        $baseSeconds = [Math]::Min($MaxDelaySeconds, $InitialDelaySeconds * [Math]::Pow(2, $attempt - 1))
        $halfMilliseconds = [int] ($baseSeconds * 500)
        $delayMilliseconds = $halfMilliseconds + (Get-Random -Minimum 0 -Maximum $halfMilliseconds)

        Write-Output "Waiting $delayMilliseconds ms before attempt $($attempt + 1) of $MaxAttempts for $image."
        Start-Sleep -Milliseconds $delayMilliseconds
    }
}
