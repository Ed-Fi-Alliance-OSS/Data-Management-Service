# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Asserts that build-dms.ps1's version stamping cannot reach src/plugins/Directory.Build.props.

.DESCRIPTION
    build-dms.ps1's SetDMSAssemblyInfo regenerates src/dms/Directory.Build.props with the DMS
    release version on every BuildAndPublish. The plugin contract must not be caught by that: the
    loader's newer-plugin-on-older-host preflight compares AssemblyVersions, so the contract's
    version has to move when its public surface moves and must not move when it does not. A release-
    stamped contract would refuse a plugin built against an identical surface, naming two versions
    that differ in nothing an implementer can act on.

    That the stamping writes only one path is true by construction today. This asserts it, so that a
    later edit widening the helper's reach fails a check rather than silently changing what a
    published contract version means.

    Run in two steps around a real build:

      ./eng/verification/Assert-PluginsVersionIndependence.ps1 -BaselineDirectory <dir> -CaptureBaseline
      ./build-dms.ps1 BuildAndPublish -Configuration Release -DMSVersion <v> -LockedMode
      ./eng/verification/Assert-PluginsVersionIndependence.ps1 -BaselineDirectory <dir> -ExpectedDMSVersion <v>

    The build in the middle is the real one. Invoking the Package command instead would prove
    nothing: Package dispatches to BuildPackage alone, and only the BuildAndPublish command reaches
    Invoke-SetAssemblyInfo, so a check built on Package would pass on a run that never stamped.

.NOTES
    Three properties are asserted, and the second is what keeps the first from being vacuous.

    1. src/plugins/Directory.Build.props is byte-identical to its baseline.
    2. src/dms/Directory.Build.props carries the generated shape AND its VersionPrefix equals the
       version the build was given. Asserting only that the file changed would pass on a machine
       whose committed props already happened to match the stamped output, and would pass equally
       if some unrelated edit had touched the file; asserting the requested version proves the
       stamping ran and ran with the version under test.
    3. src/dms/Directory.Build.props is restored, byte-exactly, from the captured baseline.

    The restore is from the captured bytes and never from Git, so a developer holding uncommitted
    edits to that file gets their own content back rather than HEAD's. It runs in a finally, so a
    failed assertion still leaves the tree as it was found. And it refuses to write when the current
    content is not the build script's own output, because that means something else changed the file
    and overwriting it would destroy work.

    Two details of that restore are load-bearing and easy to get wrong, so they are stated here.

    Whether the file is the build's output is decided by the generated-file marker alone, and
    deliberately not by the version as well. A run stamped to some other version still produced a
    file the build owns; treating it as a foreign edit would report a conflict that never happened
    and leave the tree stamped.

    A cleanup problem is reported as a warning when an assertion has already failed, and thrown only
    when the assertions passed. PowerShell lets an exception raised in a finally supersede the one
    already in flight, which would replace the real finding with a message about the restore.
#>
[CmdletBinding()]
param(
    # Where the pre-build bytes are held between the two invocations. Must be a dedicated directory:
    # -CaptureBaseline requires it to be absent or empty, and writes exactly two files into it.
    [Parameter(Mandatory)]
    [string]
    $BaselineDirectory,

    # Capture mode. Run this before the build; run without it afterwards to assert.
    [switch]
    $CaptureBaseline,

    # The version passed to the build as -DMSVersion. Required when asserting: it is what the
    # positive control compares the regenerated VersionPrefix against.
    [string]
    $ExpectedDMSVersion,

    [string]
    $RepositoryRoot = (Join-Path $PSScriptRoot "../..")
)

$ErrorActionPreference = "Stop"

$resolvedRoot = (Resolve-Path -LiteralPath $RepositoryRoot).ProviderPath

$trackedFiles = [ordered]@{
    # The file the stamping must never reach. This is the assertion.
    "plugins" = @{
        Path     = Join-Path $resolvedRoot "src/plugins/Directory.Build.props"
        Baseline = "plugins.Directory.Build.props"
    }
    # The file the stamping does rewrite. This is the positive control, and the file restored
    # afterwards.
    "dms"     = @{
        Path     = Join-Path $resolvedRoot "src/dms/Directory.Build.props"
        Baseline = "dms.Directory.Build.props"
    }
}

foreach ($entry in $trackedFiles.Values) {
    if (-not (Test-Path -LiteralPath $entry.Path)) {
        throw "Expected props file was not found: $($entry.Path)"
    }
}

if ($CaptureBaseline) {
    if (Test-Path -LiteralPath $BaselineDirectory) {
        $existingEntries = @(Get-ChildItem -LiteralPath $BaselineDirectory -Force)

        if ($existingEntries.Count -gt 0) {
            throw "Refusing to capture a baseline into $BaselineDirectory : it is not empty, and a stale baseline would be compared against a build it did not precede. Pass a fresh path per invocation."
        }
    }
    else {
        New-Item -ItemType Directory -Path $BaselineDirectory -Force | Out-Null
    }

    foreach ($name in $trackedFiles.Keys) {
        $entry = $trackedFiles[$name]
        Copy-Item -LiteralPath $entry.Path -Destination (Join-Path $BaselineDirectory $entry.Baseline)
    }

    Write-Output "Captured pre-build baselines for $($trackedFiles.Count) props files into $BaselineDirectory."
    return
}

if ([string]::IsNullOrWhiteSpace($ExpectedDMSVersion)) {
    throw "-ExpectedDMSVersion is required when asserting. It must be the version passed to the build as -DMSVersion; without it the positive control cannot tell a real stamping run from a build that never stamped."
}

foreach ($name in $trackedFiles.Keys) {
    $baselinePath = Join-Path $BaselineDirectory $trackedFiles[$name].Baseline

    if (-not (Test-Path -LiteralPath $baselinePath)) {
        throw "No captured baseline for the $name props file at $baselinePath. Run this script with -CaptureBaseline before the build."
    }
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string] $Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

$pluginsPath = $trackedFiles["plugins"].Path
$pluginsBaseline = Join-Path $BaselineDirectory $trackedFiles["plugins"].Baseline
$dmsPath = $trackedFiles["dms"].Path
$dmsBaseline = Join-Path $BaselineDirectory $trackedFiles["dms"].Baseline

$dmsCurrentContent = Get-Content -LiteralPath $dmsPath -Raw
$dmsBaselineHash = Get-FileSha256 -Path $dmsBaseline
$dmsCurrentHash = Get-FileSha256 -Path $dmsPath

# Two different questions, deliberately not one.
#
# "Is this the build script's output at all?" decides whether restoring the baseline over it is
# safe, and it must not depend on which version was stamped: a run stamped to the wrong version
# still produced a file the build owns, and refusing to restore it would leave the tree dirty while
# reporting a foreign edit that never happened.
#
# "Was it stamped with the version under test?" is the positive control, and it needs both halves,
# because the marker alone would also accept a stamping run for some other version.
$dmsIsGeneratedShape =
    $dmsCurrentContent -match "<!--\s*This file is generated by the build script\.\s*-->"
$dmsCarriesExpectedVersion =
    $dmsIsGeneratedShape -and
    $dmsCurrentContent -match "<VersionPrefix>$([regex]::Escape($ExpectedDMSVersion))</VersionPrefix>"

$assertionSucceeded = $false

try {
    # 1. The assertion. The stamping must not have reached the plugin contract's props.
    $pluginsBaselineHash = Get-FileSha256 -Path $pluginsBaseline
    $pluginsCurrentHash = Get-FileSha256 -Path $pluginsPath

    if ($pluginsCurrentHash -ne $pluginsBaselineHash) {
        throw "src/plugins/Directory.Build.props changed during a build run with -DMSVersion $ExpectedDMSVersion (baseline $pluginsBaselineHash, now $pluginsCurrentHash). The plugin contract is versioned on its own public surface and must stay outside SetDMSAssemblyInfo's reach."
    }

    # 2. The positive control. Without this, assertion 1 passes on a build that never stamped
    #    anything, which is the failure mode most likely to go unnoticed.
    if (-not $dmsCarriesExpectedVersion) {
        throw "src/dms/Directory.Build.props does not carry the build script's generated shape with VersionPrefix $ExpectedDMSVersion, so version stamping did not run with the version under test and the assertion above proves nothing. Run ./build-dms.ps1 BuildAndPublish -DMSVersion $ExpectedDMSVersion between the capture and this check; the Package command does not reach Invoke-SetAssemblyInfo."
    }

    Write-Output "Verified src/plugins/Directory.Build.props is byte-identical ($pluginsCurrentHash) after a build stamped to $ExpectedDMSVersion, and that src/dms/Directory.Build.props was regenerated, so the check was not vacuous."
    $assertionSucceeded = $true
}
finally {
    # 3. Restore, from the captured bytes rather than from Git, so uncommitted work in that file
    #    survives. In a finally so a failed assertion above does not leave the tree stamped.
    #
    #    A cleanup problem must never replace the assertion's own diagnostic. PowerShell lets an
    #    exception thrown here supersede the one already in flight, which would report "something
    #    else changed this file" in place of the real finding. So a cleanup failure is thrown only
    #    when the assertions passed, and reported as a warning otherwise.
    $cleanupProblem = $null

    if ($dmsCurrentHash -eq $dmsBaselineHash) {
        Write-Output "src/dms/Directory.Build.props already matches its baseline; nothing to restore."
    }
    elseif ($dmsIsGeneratedShape) {
        Copy-Item -LiteralPath $dmsBaseline -Destination $dmsPath -Force

        $restoredHash = Get-FileSha256 -Path $dmsPath
        if ($restoredHash -ne $dmsBaselineHash) {
            $cleanupProblem = "Failed to restore src/dms/Directory.Build.props from its baseline: expected $dmsBaselineHash, got $restoredHash."
        }
        else {
            Write-Output "Restored src/dms/Directory.Build.props from its captured baseline ($dmsBaselineHash)."
        }
    }
    else {
        # Neither the baseline nor anything the build script wrote. Something else owns this content,
        # and the captured bytes are no longer a safe thing to put back.
        $cleanupProblem = "src/dms/Directory.Build.props is neither its captured baseline nor the build script's generated output, so it was changed by something other than this check. Refusing to overwrite it; inspect the file and restore it yourself if needed. Baseline held at $dmsBaseline."
    }

    if ($null -ne $cleanupProblem) {
        if ($assertionSucceeded) {
            throw $cleanupProblem
        }

        Write-Warning $cleanupProblem
    }
}
