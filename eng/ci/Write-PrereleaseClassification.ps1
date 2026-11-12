# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
    .SYNOPSIS
        Writes whether a dms-pre- or cs-pre- prerelease is a tagged release build to $GITHUB_OUTPUT.
    .DESCRIPTION
        The workflow-facing wrapper around Test-TaggedReleaseBuild. on-prerelease.yml runs it once
        and gates the release-asset jobs and the image tags on its tagged-release output, emitted
        as lowercase "true"/"false" because that is what the workflow's `if:` expressions compare.
    .PARAMETER ReleaseRef
        The prerelease tag the workflow is running for, as github.ref_name reports it.
    .PARAMETER Remote
        The git remote whose v tags decide the answer.
    .PARAMETER OutputPath
        Destination for the key=value line. Defaults to $GITHUB_OUTPUT; when neither is set the line
        goes to standard output only, so the script can be run by hand.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]
    $ReleaseRef,

    [string]
    $Remote = 'origin',

    [string]
    $OutputPath = $env:GITHUB_OUTPUT
)

$ErrorActionPreference = 'Stop'

Import-Module -Name (Join-Path $PSScriptRoot 'release-classification.psm1') -Force

$tagged = Test-TaggedReleaseBuild -ReleaseRef $ReleaseRef -TaggedVersion @(Get-ReleaseTagVersion -Remote $Remote)
$line = "tagged-release=$($tagged.ToString().ToLowerInvariant())"

# Echoed to the log as well as the output file: it decides which image tags are pushed and whether
# release assets are attached.
Write-Output $line

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    Add-Content -LiteralPath $OutputPath -Value $line
}
