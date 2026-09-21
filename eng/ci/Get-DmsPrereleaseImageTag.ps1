# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
    .SYNOPSIS
        Writes the DMS image tags and version for a release to $GITHUB_OUTPUT.
    .DESCRIPTION
        The tag computation the prerelease workflow's Publish DMS to Docker Hub job used to carry
        inline. It lives here so the rule can be driven directly by a test rather than only by
        publishing an image and looking at Docker Hub.

        A prerelease now gets a version-specific tag alongside the moving "pre" tag, so a proof that
        has to name one published image can pin it. The moving tag is emitted first and is never
        dropped: deployments that follow it must keep following it.

        The non-alpha branch is a faithful port of the shell it replaces, including that branch's
        fixed-offset strip and its two-field minor form. It is reproduced rather than corrected
        because nothing in this change publishes through it; on-prerelease.yml fires on
        `prereleased`, and correcting a path this change does not exercise would be a release-policy
        change wearing a refactor's clothes.
    .PARAMETER ReleaseRef
        The release tag the workflow is running for, as github.ref_name reports it.
    .PARAMETER ImageName
        The image repository to tag, which the workflow reads from the IMAGE_NAME variable.
    .PARAMETER OutputPath
        Destination for the key=value lines. Defaults to $GITHUB_OUTPUT; when neither is set the
        lines go to standard output only, so the script can be run by hand.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]
    $ReleaseRef,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]
    $ImageName,

    [string]
    $OutputPath = $env:GITHUB_OUTPUT
)

$ErrorActionPreference = 'Stop'

# The prefix every DMS prerelease tag carries, and the number of characters the shell this replaces
# stripped off the front of a ref. They agree today, and the alpha branch below uses the prefix
# rather than the count so that they cannot quietly stop agreeing.
$prereleasePrefix = 'dms-pre-'
$legacyStripLength = 8

# Ordinal, because the shell's [[ $REF =~ "alpha" ]] is a case-sensitive substring test and
# PowerShell's -match is neither case-sensitive nor a substring test by default.
$isPrerelease = $ReleaseRef.Contains('alpha', [System.StringComparison]::Ordinal)

if ($isPrerelease) {
    # Guarding the prefix only on the branch this change alters. The stripped value used to reach
    # one build argument, where a wrong result produced a mislabelled image; it now also reaches a
    # tag that is pushed to Docker Hub, and a malformed tag is published state nobody can take back.
    if (-not $ReleaseRef.StartsWith($prereleasePrefix, [System.StringComparison]::Ordinal)) {
        throw "Release ref '$ReleaseRef' is a prerelease but does not start with '$prereleasePrefix', so no version-specific image tag can be derived from it."
    }

    # No empty-remainder guard: reaching here means the ref contains "alpha" and starts with a
    # prefix that does not, so the remainder cannot be empty.
    $version = $ReleaseRef.Substring($prereleasePrefix.Length)

    # "pre" first, so the moving tag keeps the position it has always been pushed in.
    $tags = @("${ImageName}:pre", "${ImageName}:$version")
}
else {
    # Substring would throw where the shell's ${REF:8} yields an empty string, and this branch is a
    # port rather than a rewrite.
    $version = if ($ReleaseRef.Length -gt $legacyStripLength) { $ReleaseRef.Substring($legacyStripLength) } else { '' }

    # awk -F"." '{print $1"."$2}' over a ref with fewer than two fields prints the first field, a
    # dot, and nothing. Splitting and rejoining two positions reproduces that, including the
    # trailing dot.
    $field = $ReleaseRef.Split('.')
    $minor = "$($field[0]).$(if ($field.Length -gt 1) { $field[1] } else { '' })"

    $tags = @("${ImageName}:$ReleaseRef", "${ImageName}:$minor")
}

$lines = @(
    "DMSTAGS=$($tags -join ',')"
    "VERSION=$version"
)

# Echoed to the log as well as the output file: these decide what is pushed, so when an image
# appears under an unexpected tag this is the first thing worth reading.
$lines | ForEach-Object { Write-Output $_ }

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    Add-Content -LiteralPath $OutputPath -Value $lines
}
