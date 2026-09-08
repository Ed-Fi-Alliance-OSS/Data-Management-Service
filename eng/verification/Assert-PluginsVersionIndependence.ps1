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
    2. src/dms/Directory.Build.props matches, in full, what SetDMSAssemblyInfo writes for the version
       the build was given. Asserting only that the file changed would pass on a machine whose
       committed props already happened to match the stamped output, and would pass equally if some
       unrelated edit had touched the file.
    3. src/dms/Directory.Build.props is restored, byte-exactly, from the captured baseline.

    The restore is from the captured bytes and never from Git, so a developer holding uncommitted
    edits to that file gets their own content back rather than HEAD's. It runs in a finally, so a
    failed assertion still leaves the tree as it was found.

    Three details of that restore are load-bearing and easy to get wrong, so they are stated here.

    Whether the file is the build's output is decided by comparing the complete content against the
    template's expansion, and by nothing weaker. An earlier revision tested for the generated-file
    marker comment: a developer can edit an already-stamped file and the marker survives, so the
    check called the edit the build's output, passed, and restored the baseline over it. Anything
    that is neither the baseline nor those exact bytes is now left untouched, marker or no marker,
    and the baseline stays on disk for recovery.

    The file is re-hashed immediately before the write, so a change arriving between validation and
    restoration is refused rather than overwritten.

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
    $RepositoryRoot = (Join-Path $PSScriptRoot "../.."),

    # The build script whose SetDMSAssemblyInfo template defines what a stamped
    # src/dms/Directory.Build.props must look like. A parameter so a test can point this at a
    # fixture copy rather than at the live script.
    [string]
    $BuildScriptPath
)

$ErrorActionPreference = "Stop"

$resolvedRoot = (Resolve-Path -LiteralPath $RepositoryRoot).ProviderPath

if ([string]::IsNullOrWhiteSpace($BuildScriptPath)) {
    $BuildScriptPath = Join-Path $resolvedRoot "build-dms.ps1"
}

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

function ConvertTo-NormalizedText {
    <#
    .SYNOPSIS
        Content with line endings and any trailing newline normalised away.
    .DESCRIPTION
        The comparison below is about content, not about how a given checkout or writer happened to
        encode line breaks. Without this a CRLF working copy would report every stamped file as
        unrecognised and refuse every restore.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Value)

    return ($Value -replace "`r`n", "`n").TrimEnd("`n")
}

function Get-ExpectedStampedPropsContent {
    <#
    .SYNOPSIS
        The complete content SetDMSAssemblyInfo writes for one version.
    .DESCRIPTION
        Read out of the build script and expanded, rather than restated here. A second copy of that
        template would be free to drift from the one the build actually writes, and this check's
        whole job is to know the difference between the build's output and somebody's edit.

        Expansion goes through PowerShell's own string expander rather than through hand-written
        substitution, so the template behaves exactly as it does at its real call site. That is not
        a nicety: the template contains a malformed subexpression which expands to nothing, and any
        hand-rolled substitution would have to reproduce that defect deliberately to stay faithful.
        The defect itself is recorded elsewhere and is not this story's to fix.
    #>
    param(
        [Parameter(Mandatory)][string] $BuildScript,
        [Parameter(Mandatory)][string] $Version
    )

    if (-not (Test-Path -LiteralPath $BuildScript)) {
        throw "Cannot determine what a stamped src/dms/Directory.Build.props should contain: $BuildScript does not exist."
    }

    $scriptText = Get-Content -LiteralPath $BuildScript -Raw

    # Anchored on the call SetDMSAssemblyInfo makes, the same way
    # eng/docker-compose/tests/LockFileParity.Tests.ps1 locates it. If the template is ever moved or
    # rewritten this fails loudly rather than silently comparing against nothing.
    $templateMatch = [regex]::Match(
        $scriptText,
        '(?s)Invoke-RegenerateFile\s+"\$solutionRoot/Directory\.Build\.props"\s+@"\r?\n(?<template>.*?)\r?\n"@'
    )

    if (-not $templateMatch.Success) {
        throw "Could not locate the SetDMSAssemblyInfo props template in $BuildScript. This check compares the stamped file against that template's expansion, so it cannot run until the anchor is updated to match the build script."
    }

    # The template names $assembly_version and $maintainers. The first is the version under test; the
    # second is a build-script variable, read from the same file so the two cannot disagree.
    $maintainersMatch = [regex]::Match($scriptText, '\$maintainers\s*=\s*"(?<value>[^"]*)"')

    if (-not $maintainersMatch.Success) {
        throw "Could not read the maintainers value from $BuildScript, which the props template expands into its Authors and Company elements."
    }

    # Bound in this scope so ExpandString resolves them; the names must match the template's.
    # Set-Variable rather than plain assignment because these are read only by the expander at
    # runtime, which static analysis cannot see: as assignments they read as dead stores.
    Set-Variable -Name "assembly_version" -Value $Version
    Set-Variable -Name "maintainers" -Value $maintainersMatch.Groups['value'].Value

    return $ExecutionContext.InvokeCommand.ExpandString($templateMatch.Groups['template'].Value)
}

$pluginsPath = $trackedFiles["plugins"].Path
$pluginsBaseline = Join-Path $BaselineDirectory $trackedFiles["plugins"].Baseline
$dmsPath = $trackedFiles["dms"].Path
$dmsBaseline = Join-Path $BaselineDirectory $trackedFiles["dms"].Baseline

$dmsBaselineHash = Get-FileSha256 -Path $dmsBaseline
$dmsCurrentHash = Get-FileSha256 -Path $dmsPath

# Ownership is decided by comparing the whole file against what SetDMSAssemblyInfo writes for this
# version, and by nothing weaker.
#
# An earlier revision tested for the generated-file marker comment instead. That is not evidence of
# anything: a developer can edit an already-stamped file - change a property, add an element - and
# the marker survives. The check then called the edit "the build's output", passed, and restored the
# baseline over it, destroying the edit while reporting success.
#
# So a file that is neither the captured baseline nor these exact bytes is left alone, marker or no
# marker, and the baseline stays on disk for whoever needs to recover from it.
$expectedStamped = ConvertTo-NormalizedText -Value (
    Get-ExpectedStampedPropsContent -BuildScript $BuildScriptPath -Version $ExpectedDMSVersion
)
# -ceq, not -eq. PowerShell's -eq on strings is case-insensitive, which would let a case-only edit -
# <Product>ed-fi api</Product> in place of <Product>Ed-Fi API</Product> - be claimed as the build's
# own output and overwritten. Ordinal case-sensitive comparison is the whole point of the check.
$dmsIsStampedOutput =
    (ConvertTo-NormalizedText -Value (Get-Content -LiteralPath $dmsPath -Raw)) -ceq $expectedStamped

$assertionSucceeded = $false

try {
    # 1. The assertion. The stamping must not have reached the plugin contract's props.
    $pluginsBaselineHash = Get-FileSha256 -Path $pluginsBaseline
    $pluginsCurrentHash = Get-FileSha256 -Path $pluginsPath

    if ($pluginsCurrentHash -ne $pluginsBaselineHash) {
        throw "src/plugins/Directory.Build.props changed during a build run with -DMSVersion $ExpectedDMSVersion (baseline $pluginsBaselineHash, now $pluginsCurrentHash). The plugin contract is versioned on its own public surface and must stay outside SetDMSAssemblyInfo's reach."
    }

    # 2. The positive control. Without it, assertion 1 passes on a build that never stamped anything,
    #    which is the failure mode most likely to go unnoticed. It compares the whole file, so a
    #    stamped file someone then edited fails here too rather than being waved through.
    if (-not $dmsIsStampedOutput) {
        throw "src/dms/Directory.Build.props does not match what SetDMSAssemblyInfo writes for $ExpectedDMSVersion, so this run proves nothing about version stamping. Either the build did not run with that version - the Package command does not reach Invoke-SetAssemblyInfo, only BuildAndPublish does - or the stamped file was modified afterwards. It has been left untouched; the pre-build baseline is at $dmsBaseline."
    }

    Write-Output "Verified src/plugins/Directory.Build.props is byte-identical ($pluginsCurrentHash) after a build stamped to $ExpectedDMSVersion, and that src/dms/Directory.Build.props matches SetDMSAssemblyInfo's complete output for that version, so the check was not vacuous."
    $assertionSucceeded = $true
}
finally {
    # 3. Restore, from the captured bytes rather than from Git, so uncommitted work in that file
    #    survives. In a finally so a failed assertion above does not leave the tree stamped.
    #
    #    A cleanup problem must never replace the assertion's own diagnostic. PowerShell lets an
    #    exception thrown here supersede the one already in flight, which would report a restore
    #    complaint in place of the real finding. So a cleanup failure is thrown only when the
    #    assertions passed, and reported as a warning otherwise.
    $cleanupProblem = $null

    if ($dmsCurrentHash -eq $dmsBaselineHash) {
        Write-Output "src/dms/Directory.Build.props already matches its baseline; nothing to restore."
    }
    elseif ($dmsIsStampedOutput) {
        # Re-read immediately before writing. Ownership was established earlier, and this narrows the
        # window in which something could have changed the file since: the write is only made over
        # content still identical to what was validated.
        $hashAtRestore = Get-FileSha256 -Path $dmsPath

        if ($hashAtRestore -ne $dmsCurrentHash) {
            $cleanupProblem = "src/dms/Directory.Build.props changed between validation and restoration (was $dmsCurrentHash, now $hashAtRestore). Refusing to overwrite it; the pre-build baseline is at $dmsBaseline."
        }
        else {
            Copy-Item -LiteralPath $dmsBaseline -Destination $dmsPath -Force

            $restoredHash = Get-FileSha256 -Path $dmsPath
            if ($restoredHash -ne $dmsBaselineHash) {
                $cleanupProblem = "Failed to restore src/dms/Directory.Build.props from its baseline: expected $dmsBaselineHash, got $restoredHash."
            }
            else {
                Write-Output "Restored src/dms/Directory.Build.props from its captured baseline ($dmsBaselineHash)."
            }
        }
    }
    else {
        # Neither the baseline nor SetDMSAssemblyInfo's output for this version. Whatever it is, this
        # check did not produce it and has no basis for overwriting it.
        $cleanupProblem = "src/dms/Directory.Build.props is neither its captured baseline nor SetDMSAssemblyInfo's output for $ExpectedDMSVersion, so this check has no basis for claiming it. Refusing to overwrite it; the pre-build baseline is at $dmsBaseline if you need to restore it yourself."
    }

    if ($null -ne $cleanupProblem) {
        if ($assertionSucceeded) {
            throw $cleanupProblem
        }

        Write-Warning $cleanupProblem
    }
}
