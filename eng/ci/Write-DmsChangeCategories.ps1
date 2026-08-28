# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
    .SYNOPSIS
        Writes the DMS pull request workflow's change-detection outputs to $GITHUB_OUTPUT.
    .DESCRIPTION
        The thin workflow-facing wrapper around Get-DmsChangeCategory. The workflow owns producing
        the changed-file list, because only it knows the diff base; this script owns turning that
        list into GitHub Actions step outputs.

        Flags are emitted as lowercase "true"/"false" because that is what the workflow's `if:`
        expressions compare against.
    .PARAMETER EventName
        The GitHub event name.
    .PARAMETER ChangedFilePath
        A file holding the changed paths, one per line. A missing file is treated as an empty list,
        which is correct for events that never compute a diff.
    .PARAMETER DiffUnavailable
        Set when no trustworthy changed-file list could be produced, forcing the full suite.
    .PARAMETER OutputPath
        Destination for the key=value lines. Defaults to $GITHUB_OUTPUT; when neither is set the
        lines go to standard output so the script can be run by hand.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]
    $EventName,

    [string]
    $ChangedFilePath,

    [switch]
    $DiffUnavailable,

    [string]
    $OutputPath = $env:GITHUB_OUTPUT
)

$ErrorActionPreference = 'Stop'

Import-Module -Name (Join-Path $PSScriptRoot 'dms-change-categories.psm1') -Force

$changedFile = @()
if (-not [string]::IsNullOrWhiteSpace($ChangedFilePath) -and (Test-Path -LiteralPath $ChangedFilePath -PathType Leaf)) {
    $changedFile = @(Get-Content -LiteralPath $ChangedFilePath)
}

$category = Get-DmsChangeCategory `
    -EventName $EventName `
    -ChangedFile $changedFile `
    -DiffUnavailable:$DiffUnavailable

$lines = @(
    $category.PSObject.Properties | ForEach-Object {
        "$($_.Name)=$(([string]$_.Value).ToLowerInvariant())"
    }
)

# Echoed to the log as well as the output file: the classification decides which lanes run, so when
# a lane is unexpectedly skipped this is the first thing worth reading.
$lines | ForEach-Object { Write-Output $_ }

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    Add-Content -LiteralPath $OutputPath -Value $lines
}
