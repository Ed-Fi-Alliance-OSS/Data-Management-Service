# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
    .SYNOPSIS
        Promotes a prerelease image to its release tags in the registry, retrying transient failures.
    .DESCRIPTION
        docker buildx imagetools create copies the source image to every tag inside the registry, so
        each release tag gets every platform and the same index digest as the prerelease image. A
        local pull, tag and push would republish only the runner's platform.

        The release jobs used to pull the prerelease image through the pull-ci-images action, which
        retried transient registry failures. This script keeps a bounded retry around the one
        registry call that replaced that pull. A retry after a partial failure is safe: with a single
        source, imagetools writes the source index unchanged, so a tag an earlier attempt already
        wrote is rewritten with the same digest. When the last attempt fails, the script throws, and
        that fails the step.
    .PARAMETER Source
        The prerelease image reference to promote.
    .PARAMETER Tag
        The release tags to create from Source.
    .PARAMETER MaxAttempts
        Maximum promotion attempts.
    .PARAMETER InitialDelaySeconds
        Wait before the second attempt, in seconds. The wait doubles before each later attempt.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]
    $Source,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string[]]
    $Tag,

    [ValidateRange(1, 10)]
    [int]
    $MaxAttempts = 4,

    [ValidateRange(0, 300)]
    [int]
    $InitialDelaySeconds = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# A failed attempt has to reach the retry loop as an exit code, not as a terminating error.
$PSNativeCommandUseErrorActionPreference = $false

$arguments = @("buildx", "imagetools", "create")
foreach ($name in $Tag) {
    $arguments += @("--tag", $name)
}
$arguments += $Source

$tagList = $Tag -join ", "

for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    Write-Output "Promoting $Source to $tagList (attempt $attempt of $MaxAttempts)."

    # Not captured, so the registry's error reaches the job log.
    docker @arguments
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        Write-Output "Promoted $Source to $tagList on attempt $attempt of $MaxAttempts."
        return
    }

    if ($attempt -eq $MaxAttempts) {
        throw "Failed to promote $Source to $tagList after $MaxAttempts attempt(s); the last exit code was $exitCode. A tag may already have been written; rerunning the job rewrites it with the same digest."
    }

    $delaySeconds = [int] ($InitialDelaySeconds * [Math]::Pow(2, $attempt - 1))
    Write-Output "Promotion failed with exit code $exitCode. Waiting $delaySeconds s before attempt $($attempt + 1) of $MaxAttempts."
    Start-Sleep -Seconds $delaySeconds
}
