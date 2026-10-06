# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Asserts on the contents of a packed EdFi.Api.Secrets nupkg.

.DESCRIPTION
    Asserts on what is inside the artifact, not that a file appeared. Dropping the readme, the
    license, the assembly, or the XML docs from the csproj would otherwise ship silently; a stray
    PackageReference would quietly widen every implementer's dependency closure; and an accidental
    public type would widen the surface a published version commits to.

    Invoke-ContractPublishCheck.ps1 does not stand in for this. It pushes a version the feed does
    not hold without opening the package, so the first publication of every version, 1.0.0
    included, would otherwise reach the feed with nothing having read what it contains.

    The on-config-pullrequest.yml lane calls this on a pull request that is not a draft and changed
    a config-relevant path, and unconditionally in the merge queue. The prerelease pack job calls it
    on the packed artifact before that artifact is uploaded for publication: it is the check that
    decides what a published version contains, and once published that content cannot be taken
    back.
#>
[CmdletBinding()]
param(
    # The .nupkg to inspect.
    [Parameter(Mandatory)]
    [string]
    $PackageFile,

    # Directory to extract into. Must not be inside the folder feed a consumer restores from.
    # Emptied first, so a previous extraction cannot satisfy a presence check for a file the
    # package under test no longer carries.
    [Parameter(Mandatory)]
    [string]
    $ExtractTo,

    # The package id every assertion below is made against. Passed in rather than hardcoded so the
    # id lives in one place per lane and a rename cannot leave this script checking the old one.
    [Parameter(Mandatory)]
    [string]
    $PackageId,

    # The version the package must declare, which the caller reads from the contract's own csproj
    # via Get-SecretsContractVersion. Passed in rather than read here so that this script asserts
    # against the lane's single declared version instead of re-deriving one and agreeing with
    # itself.
    [Parameter(Mandatory)]
    [string]
    $ExpectedPackageVersion,

    # The assembly the package must carry. It differs from the package id, and an already-compiled
    # plugin binds to it by name, so renaming it is a breaking change "some dll is present" would
    # not notice.
    [string]
    $AssemblyName = "EdFi.DmsConfigurationService.Secrets",

    # The target framework whose lib/ folder must carry the assembly and its XML documentation.
    [string]
    $TargetFramework = "net10.0",

    # The committed implementer guide the packed readme must be a copy of. Passed in with a default
    # so the Pester cases can point it at a fixture without rewriting a tracked file.
    [string]
    $GuidePath = (Join-Path $PSScriptRoot "../../src/config/contracts/EdFi.DmsConfigurationService.Secrets/README.md")
)

$ErrorActionPreference = "Stop"

# The exact public surface the package is allowed to export, nested types included. The project
# generates its XML documentation and builds with warnings as errors, so CS1591 makes documenting
# every public type mandatory, which is what lets the shipped XML file stand in for the type surface.
$expectedTypes = @(
    "EdFi.DmsConfigurationService.Secrets.IClientSecretHasher",
    "EdFi.DmsConfigurationService.Secrets.ISecretResolver",
    "EdFi.DmsConfigurationService.Secrets.SecretReference"
)

# The worked examples the published guide must carry, pinned by claim. The whole-file comparison
# below proves the packed readme is the committed guide; this stops the two from agreeing on a guide
# that has quietly lost an example.
$requiredSampleClaims = @(
    "eng/verification/SecretsPluginExamples/KeyVaultConfigurationPlugin.cs#plugin"
    "eng/verification/SecretsPluginExamples/ParameterStoreConfigurationPlugin.cs#plugin"
    "eng/verification/SecretsPluginExamples/ParameterStoreSecretResolver.cs#resolver"
)

if (-not (Test-Path -LiteralPath $PackageFile)) {
    throw "Expected secrets contract package was not found: $PackageFile"
}

# Refuse to recursively delete anything that is not recognisably a previous extraction of this
# script's own making. Without this the parameter is an arbitrary rm -rf target.
if (Test-Path -LiteralPath $ExtractTo) {
    $existingEntries = @(Get-ChildItem -LiteralPath $ExtractTo -Force)
    $priorExtraction = @(Get-ChildItem -LiteralPath $ExtractTo -Filter "*.nuspec" -File).Count -gt 0

    if ($existingEntries.Count -gt 0 -and -not $priorExtraction) {
        throw "Refusing to empty $ExtractTo : it is not empty and does not look like a previous package extraction. Pass a dedicated scratch directory."
    }

    Remove-Item -LiteralPath $ExtractTo -Recurse -Force
}

Expand-Archive -LiteralPath $PackageFile -DestinationPath $ExtractTo -Force

$nuspecPath = Join-Path $ExtractTo "$PackageId.nuspec"
if (-not (Test-Path -LiteralPath $nuspecPath)) {
    throw "Package does not carry $PackageId.nuspec"
}
[xml]$nuspec = Get-Content -LiteralPath $nuspecPath
$metadata = $nuspec.package.metadata

if ($metadata.id -ne $PackageId) {
    throw "Unexpected package id: $($metadata.id)"
}

# The contract carries its own semantic version rather than the Configuration Service release
# version. Asserting it here is what stops a pack lane from quietly reintroducing a release-stamped
# version.
if ($metadata.version -ne $ExpectedPackageVersion) {
    throw "Unexpected package version: expected $ExpectedPackageVersion, found $($metadata.version). The secrets contract is versioned on its own surface, not on the Configuration Service release."
}

if ($metadata.license.'#text' -ne "Apache-2.0") {
    throw "Missing or unexpected license expression: $($metadata.license.'#text')"
}
if ($metadata.readme -ne "README.md") {
    throw "Missing packed readme: $($metadata.readme)"
}
foreach ($required in "description", "title", "projectUrl") {
    if ([string]::IsNullOrWhiteSpace($metadata.$required)) {
        throw "Missing package metadata: $required"
    }
}
$packedReadmePath = Join-Path $ExtractTo "README.md"
if (-not (Test-Path -LiteralPath $packedReadmePath)) {
    throw "Package does not carry the readme file itself"
}

# The readme is the implementer guide, compared whole, for the reasons
# Assert-CustomValidationPackage.ps1 gives: it fails on a stale artifact, on a readme altered after
# packing, and on a placeholder, none of which a heading survey would notice. Line endings are
# normalized and trailing whitespace trimmed from the end of each whole text only, so an autocrlf
# checkout does not fail it and interior drift still does.
if (-not (Test-Path -LiteralPath $GuidePath)) {
    throw "The committed implementer guide was not found at $GuidePath, so the packed readme cannot be compared against it. Pass -GuidePath."
}

$packedReadme = ([System.IO.File]::ReadAllText($packedReadmePath)).Replace("`r`n", "`n").TrimEnd()
$committedGuide = ([System.IO.File]::ReadAllText($GuidePath)).Replace("`r`n", "`n").TrimEnd()

if (-not [string]::Equals($packedReadme, $committedGuide, [StringComparison]::Ordinal)) {
    $packedLines = $packedReadme -split "`n"
    $committedLines = $committedGuide -split "`n"
    $firstDifference = "the two texts differ in length only"

    for ($index = 0; $index -lt [Math]::Max($packedLines.Count, $committedLines.Count); $index++) {
        $packedLine = if ($index -lt $packedLines.Count) { $packedLines[$index] } else { '<end of packed readme>' }
        $committedLine = if ($index -lt $committedLines.Count) { $committedLines[$index] } else { '<end of committed guide>' }

        if (-not [string]::Equals($packedLine, $committedLine, [StringComparison]::Ordinal)) {
            $firstDifference = "line $($index + 1): package has '$packedLine', repository has '$committedLine'"
            break
        }
    }

    throw (
        "The packed README.md is not the committed implementer guide. Either the package predates an " +
        "edit to the guide and must be repacked, or the packed copy was altered. First difference at " +
        "$firstDifference."
    )
}

foreach ($claim in $requiredSampleClaims) {
    if ($packedReadme -notmatch "(?m)^<!--\s*embed:\s*$([regex]::Escape($claim))\s*-->$") {
        throw "The packed guide carries no '<!-- embed: $claim -->' block, so it publishes a worked example that is not held to compiled source, or has lost one."
    }
}

# The whole point of the package is the assembly. Every other assertion here can pass on a package
# that carries no compilable output at all.
$libFolder = Join-Path $ExtractTo "lib/$TargetFramework"
$assemblies = @(Get-ChildItem -LiteralPath $libFolder -Filter "*.dll" -ErrorAction SilentlyContinue)
if ($assemblies.Count -ne 1) {
    throw "Expected exactly one assembly in lib/$TargetFramework, found $($assemblies.Count)"
}
if ($assemblies[0].BaseName -ne $AssemblyName) {
    throw "Unexpected assembly name: expected $AssemblyName, found $($assemblies[0].BaseName)"
}

# An assembly reference carries the AssemblyVersion, not the package version, so the two must state
# the same contract: the package version's major.minor.patch with a zero revision.
$expectedAssemblyVersion = [version] "$(($ExpectedPackageVersion -split '-', 2)[0]).0"
$actualAssemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName(
    $assemblies[0].FullName
).Version

if ($actualAssemblyVersion -ne $expectedAssemblyVersion) {
    throw "Unexpected AssemblyVersion in $($assemblies[0].Name): expected $expectedAssemblyVersion, found $actualAssemblyVersion. The csproj declares the contract's version, and a release-stamped Directory.Build.props or a /p:AssemblyVersion on the command line must not reach it."
}

# The contracts' load-bearing rules - replace cardinality, singleton and unkeyed registration, the
# resolver's caching obligation - live in the XML doc comments, so the XML file is part of the
# deliverable. It must sit beside its own assembly or the IDE will not find it.
$xmlDocPath = [System.IO.Path]::ChangeExtension($assemblies[0].FullName, ".xml")
if (-not (Test-Path -LiteralPath $xmlDocPath)) {
    throw "Package does not carry the XML documentation file for $($assemblies[0].Name)"
}

# What the package exports is the contract. Select member elements rather than searching the file
# for "T:", which would also match every <see cref> in the documentation prose.
[xml]$xmlDoc = Get-Content -LiteralPath $xmlDocPath
$actualTypes = @(
    $xmlDoc.doc.members.member |
        Where-Object { $_.name -like "T:*" } |
        ForEach-Object { $_.name.Substring(2) } |
        Sort-Object -Unique
)

$unexpected = @($actualTypes | Where-Object { $expectedTypes -notcontains $_ })
$missing = @($expectedTypes | Where-Object { $actualTypes -notcontains $_ })
if ($unexpected.Count -gt 0 -or $missing.Count -gt 0) {
    throw (
        "Published type surface changed. Unexpected: $($unexpected -join ', '). " +
        "Missing: $($missing -join ', '). Update this list only as a deliberate contract change."
    )
}

# A zero-dependency closure is the contract: every type in these signatures resolves out of the
# shared framework, and an implementer takes on nothing they did not choose. A dependency appearing
# here means a PackageReference or ProjectReference crept into the contract project.
if ($null -ne $metadata.dependencies) {
    $declared = $metadata.dependencies.SelectNodes(".//*[local-name()='dependency']")
    if ($declared.Count -gt 0) {
        $names = ($declared | ForEach-Object { $_.id }) -join ", "
        throw "Package must have no dependencies, found: $names"
    }
}

Write-Output "Verified $([System.IO.Path]::GetFileName($PackageFile)): id, version $($metadata.version), metadata, the packed readme is the committed implementer guide carrying all $($requiredSampleClaims.Count) worked examples, $($assemblies[0].Name) at AssemblyVersion $actualAssemblyVersion, XML docs, $($actualTypes.Count) exported types, and empty dependency set."
