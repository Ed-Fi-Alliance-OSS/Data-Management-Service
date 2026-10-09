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

        Which branch a ref takes is the classify-release job's decision (release-classification.psm1),
        not the ref's spelling: an ordinary main build gets the moving tag, and a build for a v tag
        gets the release tags. The release branch is a faithful port of the shell it replaces,
        including its fixed-offset strip and its two-field minor form; for a dms-pre- ref the strip
        removes exactly the prefix, so it yields the version.
    .PARAMETER ReleaseRef
        The release tag the workflow is running for, as github.ref_name reports it.
    .PARAMETER TaggedRelease
        "true" when the prerelease was built for a v tag, "false" for an ordinary main build, as
        Write-PrereleaseClassification.ps1 writes it. Anything else, including the empty value a
        skipped classify job leaves, is refused rather than guessed.
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
    [ValidateSet('true', 'false')]
    [string]
    $TaggedRelease,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]
    $ImageName,

    [string]
    $OutputPath = $env:GITHUB_OUTPUT
)

$ErrorActionPreference = 'Stop'

# The prefix every DMS prerelease tag carries, and the number of characters the shell this replaces
# stripped off the front of a ref. They agree today, and the ordinary-build branch below uses the
# prefix rather than the count so that they cannot quietly stop agreeing.
$prereleasePrefix = 'dms-pre-'
$legacyStripLength = 8

$isPrerelease = $TaggedRelease -eq 'false'

if ($isPrerelease -and -not $ReleaseRef.StartsWith($prereleasePrefix, [System.StringComparison]::Ordinal)) {
    # A prerelease ref without the prefix gets what the shell gave it: the moving tag and the
    # fixed-offset strip. No version-specific tag is derived, because the stripped value is not a
    # version and a pushed tag is published state nobody can take back. Refusing instead would stop
    # a publication the shell used to complete.
    $version = if ($ReleaseRef.Length -gt $legacyStripLength) { $ReleaseRef.Substring($legacyStripLength) } else { '' }
    $tags = @("${ImageName}:pre")
}
elseif ($isPrerelease) {
    # Before the "alpha" test was replaced, containing "alpha" guaranteed a non-empty remainder.
    # The classification gives no such guarantee, so an empty version is refused here rather than
    # pushed as a bare "image:" tag.
    $version = $ReleaseRef.Substring($prereleasePrefix.Length)
    if ($version -eq '') {
        throw "Release ref '$ReleaseRef' names no version after '$prereleasePrefix'."
    }

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
