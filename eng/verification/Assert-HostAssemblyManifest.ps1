# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Asserts on a generated host assembly manifest.

.DESCRIPTION
    Separated from the generator so the same assertions can run in two places with different
    evidentiary weight: against a fixture in the per-pull-request lane, where no released image
    exists, and against the real released image in the prerelease lane. Blurring those two would let
    a fixture stand in for the artifact an operator downloads.

    Every assertion here is made about a **named section** rather than about the text of the
    document. A manifest that listed the right assembly in the wrong section would be wrong in the
    way that matters, and a check that searched the whole file would call it right.

    Three things are asserted, and each catches a specific way the generator could regress.

    The shared-framework section must list Microsoft.Extensions.Configuration.Abstractions. That is
    the assembly a .deps.json-based generator silently omits, because a framework-dependent publish
    records no shared-framework assembly at all, and it is one of the two in the plugin contract's own
    hook signature. Its absence is what a regression to that approach looks like.

    The application section must list EdFi.Api.Plugins at the version src/plugins/Directory.Build.props
    declares. That is the contract a plugin binds to, and the loader's skew preflight compares exactly
    this value, so a manifest stating a different one would send an implementer to the wrong target.

    No section may list EdFi.DataManagementService.ApiSchemaDownloader. That is the entry assembly of
    the second application packed into the image as /app/ApiSchemaDownloader/, so it exists in every
    release and can never legitimately appear here. It is the sentinel for a recursive sweep.

    Nothing here asserts that a given assembly is *absent* from the application section on topology
    grounds. Which third-party assemblies the application carries is a property of its dependency
    closure and is free to change; only the downloader's own entry assembly is a fixed tell.

    EdFi.DataManagementService.CustomValidation's version is deliberately not asserted. That project
    declares no Version, AssemblyVersion or FileVersion, so it carries the Data Management Service
    release version and there is no project-declared contract version to compare against. The story
    that gives it one extends this script in the same pass.
#>
[CmdletBinding()]
param(
    # The generated manifest to inspect.
    [Parameter(Mandatory)]
    [string]
    $ManifestPath,

    # The contract's own declared package version, which callers read with Get-PluginsContractVersion
    # rather than passing a literal, so this asserts against the lane's single declared version
    # instead of re-deriving one and agreeing with itself.
    [Parameter(Mandatory)]
    [string]
    $ExpectedPluginsVersion
)

$ErrorActionPreference = "Stop"

$applicationSection = "Application assemblies"
$contractSection = "Contract assemblies"
$releaseSection = "Release"
$sharedFrameworkPrefix = "Shared framework: "

$sharedFrameworkRequired = "Microsoft.Extensions.Configuration.Abstractions"
$downloaderSentinel = "EdFi.DataManagementService.ApiSchemaDownloader"

if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "Manifest not found: $ManifestPath"
}

# Sections are keyed by heading text and hold their table's data rows. The separator row and the
# header row are dropped, so a section's row count is a count of assemblies rather than of lines.
$sections = [ordered] @{}
$current = $null

foreach ($line in ([System.IO.File]::ReadAllText($ManifestPath)).Replace("`r`n", "`n") -split "`n") {
    if ($line -match '^#{1,6}\s+(.+?)\s*$') {
        $current = $Matches[1]
        if (-not $sections.Contains($current)) {
            $sections[$current] = @()
        }
        continue
    }

    if ($null -eq $current -or -not $line.StartsWith("|")) {
        continue
    }

    $cells = @(($line.Trim().Trim("|") -split "\|") | ForEach-Object { $_.Trim() })

    if ($cells.Count -lt 2) {
        continue
    }
    if ($cells[0] -match '^-{3,}$' -or $cells[0] -eq "Assembly" -or $cells[0] -eq "Fact") {
        continue
    }

    $sections[$current] += [pscustomobject]@{ Key = $cells[0]; Value = $cells[1] }
}

function Assert-SectionPopulated {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Name)

    if (-not $sections.Contains($Name)) {
        throw "$ManifestPath carries no '$Name' section."
    }
    if ($sections[$Name].Count -eq 0) {
        throw "$ManifestPath carries an empty '$Name' section."
    }
}

foreach ($required in @($releaseSection, $contractSection, $applicationSection)) {
    Assert-SectionPopulated -Name $required
}

$frameworkSections = @($sections.Keys | Where-Object { $_.StartsWith($sharedFrameworkPrefix, [StringComparison]::Ordinal) })

if ($frameworkSections.Count -lt 2) {
    throw "$ManifestPath carries $($frameworkSections.Count) shared-framework section(s). The application names two shared frameworks and both belong in the manifest."
}

foreach ($name in $frameworkSections) {
    Assert-SectionPopulated -Name $name
}

# The regression guard against a .deps.json-based generator.
$sharedFrameworkNames = @($frameworkSections | ForEach-Object { $sections[$_] } | ForEach-Object { $_.Key })

if ($sharedFrameworkNames -notcontains $sharedFrameworkRequired) {
    throw "$ManifestPath does not list $sharedFrameworkRequired in a shared-framework section. It is supplied by the runtime rather than by the application, so a manifest built from a framework-dependent .deps.json would omit it."
}

# The sentinel for a recursive sweep. Checked across every section, because listing the downloader's
# entry assembly anywhere would tell an implementer it was part of the compatibility surface.
foreach ($name in $sections.Keys) {
    if (@($sections[$name] | ForEach-Object { $_.Key }) -contains $downloaderSentinel) {
        throw "$ManifestPath lists $downloaderSentinel in '$name'. That assembly lives in /app/ApiSchemaDownloader/, which runs as its own process, so the sweep is reaching below the top level of /app."
    }
}

# AssemblyVersion is a four-part value while the package version is semantic, so the expected value
# is the package version's major.minor.patch with a zero revision. This is the same derivation
# Assert-PluginsPackage.ps1 makes against the packed assembly, and the two have to agree: the loader
# compares this value, so the manifest and the package must state the same one.
$expectedAssemblyVersion = [version] "$(($ExpectedPluginsVersion -split '-', 2)[0]).0"

$pluginsRow = @($sections[$applicationSection] | Where-Object { $_.Key -eq "EdFi.Api.Plugins" })

if ($pluginsRow.Count -ne 1) {
    throw "$ManifestPath lists EdFi.Api.Plugins $($pluginsRow.Count) time(s) in '$applicationSection'. The contract a plugin binds to has to appear exactly once."
}

$actualAssemblyVersion = [version] "0.0.0.0"
if (-not [version]::TryParse($pluginsRow[0].Value, [ref] $actualAssemblyVersion)) {
    throw "$ManifestPath states EdFi.Api.Plugins at '$($pluginsRow[0].Value)', which does not parse as a version."
}

if ($actualAssemblyVersion -ne $expectedAssemblyVersion) {
    throw "$ManifestPath states EdFi.Api.Plugins at $actualAssemblyVersion; src/plugins/Directory.Build.props declares $ExpectedPluginsVersion, so the manifest should state $expectedAssemblyVersion. The loader's skew preflight compares this value."
}

$frameworkSummary = ($frameworkSections | ForEach-Object { "$_ ($($sections[$_].Count))" }) -join "; "

Write-Output "Verified $([System.IO.Path]::GetFileName($ManifestPath)): EdFi.Api.Plugins at $actualAssemblyVersion, $($sections[$applicationSection].Count) application assemblies, $sharedFrameworkRequired present, no $downloaderSentinel row, $frameworkSummary."
