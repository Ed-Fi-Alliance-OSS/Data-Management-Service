# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Asserts on the contents of a packed EdFi.Api.Plugins nupkg.

.DESCRIPTION
    Asserts on what is inside the artifact, not that a file appeared. Dropping the readme, the
    license, the assembly, or the XML docs from the csproj would otherwise ship silently; a stray
    PackageReference would quietly widen every implementer's dependency closure; and an accidental
    public type would widen the surface the first published version commits to.

    Two assertions here have no counterpart in the sibling custom-validation script, and both exist
    because a plugin binds against this contract at runtime rather than only at compile time.

    The package version is asserted against a value the caller derives from the contract's own
    declared version, because this package is deliberately not packed at the DMS release version.

    The packed assembly's AssemblyVersion is asserted, because the loader's
    newer-plugin-on-older-host preflight compares exactly that value. If the packed assembly's
    version ever disagreed with the package version an implementer resolved, the preflight would be
    comparing something other than what the implementer built against, and it would be silently
    blind rather than loudly wrong.

    The dependency check is two-sided. An added dependency widens what an implementer inherits; a
    removed one means a type in the shipped signatures no longer resolves for them. A check that
    only looked for surprises would pass on the second.

    Only the per-PR lane calls this today, because the package is deliberately built and never
    published. Whoever wires the publishing lane calls it there too: it is the check that decides
    what the first published version contains, and once published that content cannot be taken back.
#>
[CmdletBinding()]
param(
    # The .nupkg to inspect.
    [Parameter(Mandatory)]
    [string]
    $PackageFile,

    # Directory to extract into. Must not be inside the folder feed the consumer restores from.
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

    # The version the package must declare, which the caller reads from the contract's own
    # Directory.Build.props via Get-PluginsContractVersion. Passed in rather than read here so that
    # this script asserts against the lane's single declared version instead of re-deriving one and
    # agreeing with itself.
    [Parameter(Mandatory)]
    [string]
    $ExpectedPackageVersion,

    # The assembly the package must carry. Asserted by name because the name is part of what an
    # already-compiled plugin binds to, so changing it is a breaking change that "some dll is
    # present" would not notice.
    [string]
    $AssemblyName = "EdFi.Api.Plugins",

    # The target framework whose lib/ folder must carry the assembly and its XML documentation.
    [string]
    $TargetFramework = "net10.0"
)

$ErrorActionPreference = "Stop"

# The exact public surface the package is allowed to export, nested types included. The project
# builds with GenerateDocumentationFile and TreatWarningsAsErrors, so CS1591 makes documenting every
# public type mandatory, which is what lets the shipped XML file stand in for the type surface here.
$expectedTypes = @(
    "EdFi.Api.Plugins.EdFiApiPlugin"
)

# The exact dependency closure an implementer inherits. Both entries are here because EdFiApiPlugin's
# shipped hook signature names IConfiguration and IServiceCollection, so both assemblies are in the
# public surface rather than in the implementation. Nothing is added for an implementer's
# convenience, and nothing may be dropped while a signature still names it.
$expectedDependencies = @(
    "Microsoft.Extensions.Configuration.Abstractions",
    "Microsoft.Extensions.DependencyInjection.Abstractions"
)

$expectedReadme = "PLUGINS.md"

if (-not (Test-Path -LiteralPath $PackageFile)) {
    throw "Expected plugin contract package was not found: $PackageFile"
}

# Refuse to recursively delete anything that is not recognisably a previous extraction of this
# script's own making. Without this the parameter is an arbitrary rm -rf target.
#
# "Contains a nuspec" is not on its own a safe test, because the caller could point this at a
# directory that happens to hold one. So the directory must ALSO be one this script would itself
# have produced: a resolved path that is not a filesystem root, not the repository root, and not an
# ancestor of either this script or the package under test.
if (Test-Path -LiteralPath $ExtractTo) {
    $resolvedExtractTo = (Resolve-Path -LiteralPath $ExtractTo).ProviderPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    )
    $repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../..")).ProviderPath
    $packageDirectory = [System.IO.Path]::GetDirectoryName(
        (Resolve-Path -LiteralPath $PackageFile).ProviderPath
    )

    if ([string]::IsNullOrEmpty($resolvedExtractTo)) {
        throw "Refusing to empty a filesystem root: $ExtractTo"
    }

    foreach ($protected in @($repositoryRoot, $packageDirectory, $PSScriptRoot)) {
        $resolvedProtected = (Resolve-Path -LiteralPath $protected).ProviderPath
        if (
            $resolvedProtected -eq $resolvedExtractTo -or
            $resolvedProtected.StartsWith(
                $resolvedExtractTo + [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase
            )
        ) {
            throw "Refusing to empty $ExtractTo : it contains $resolvedProtected. Pass a dedicated scratch directory."
        }
    }

    $existingEntries = @(Get-ChildItem -LiteralPath $ExtractTo -Force)
    $priorExtraction = @(Get-ChildItem -LiteralPath $ExtractTo -Filter "*.nuspec" -File).Count -gt 0

    if ($existingEntries.Count -gt 0 -and -not $priorExtraction) {
        throw "Refusing to empty $ExtractTo : it is not empty and does not look like a previous package extraction. Pass a dedicated scratch directory."
    }

    Remove-Item -LiteralPath $ExtractTo -Recurse -Force
}

Expand-Archive -LiteralPath $PackageFile -DestinationPath $ExtractTo -Force

[xml]$nuspec = Get-Content -LiteralPath (Join-Path $ExtractTo "$PackageId.nuspec")
$metadata = $nuspec.package.metadata

if ($metadata.id -ne $PackageId) {
    throw "Unexpected package id: $($metadata.id)"
}

# The contract carries its own semantic version rather than the DMS release version. Asserting it
# here is what stops a pack lane from quietly reintroducing -p:PackageVersion=`$DMSVersion.
if ($metadata.version -ne $ExpectedPackageVersion) {
    throw "Unexpected package version: expected $ExpectedPackageVersion, found $($metadata.version). The plugin contract is versioned on its own surface, not on the DMS release."
}

if ($metadata.license.'#text' -ne "Apache-2.0") {
    throw "Missing or unexpected license expression: $($metadata.license.'#text')"
}
if ($metadata.readme -ne $expectedReadme) {
    throw "Missing packed readme: $($metadata.readme)"
}
foreach ($required in "description", "title", "projectUrl") {
    if ([string]::IsNullOrWhiteSpace($metadata.$required)) {
        throw "Missing package metadata: $required"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $ExtractTo $expectedReadme))) {
    throw "Package does not carry the readme file itself"
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

# What the loader's skew preflight actually compares. AssemblyVersion is a four-part value while the
# package version is semantic, so the expected value is the package version's major.minor.patch with
# a zero revision. Holding AssemblyVersion at major.0.0.0, the common .NET convention, is ruled out
# for this package: under it a plugin built against 1.3 records a reference to 1.0.0.0, a 1.0 host
# satisfies it, and the missing member surfaces as a MissingMethodException inside a hook.
$expectedAssemblyVersion = [version] "$(($ExpectedPackageVersion -split '-', 2)[0]).0"
$actualAssemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName(
    $assemblies[0].FullName
).Version

if ($actualAssemblyVersion -ne $expectedAssemblyVersion) {
    throw "Unexpected AssemblyVersion in $($assemblies[0].Name): expected $expectedAssemblyVersion, found $actualAssemblyVersion. The loader's plugin-skew preflight compares this value, so it must equal the package version."
}

# The contract's rules live in the XML doc comments, so the XML file is part of the deliverable. It
# must sit beside its own assembly or the IDE will not find it.
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

# The dependency closure an implementer inherits is part of the contract, so this is checked in both
# directions. Unlike the sibling contract, an empty set here would be wrong: two assemblies are named
# by the shipped hook signature and must resolve for anyone who references this package.
$declaredDependencies = @()
if ($null -ne $metadata.dependencies) {
    $declaredDependencies = @(
        $metadata.dependencies.SelectNodes(".//*[local-name()='dependency']") |
            ForEach-Object { $_.id } |
            Sort-Object -Unique
    )
}

$unexpectedDependencies = @($declaredDependencies | Where-Object { $expectedDependencies -notcontains $_ })
$missingDependencies = @($expectedDependencies | Where-Object { $declaredDependencies -notcontains $_ })
if ($unexpectedDependencies.Count -gt 0 -or $missingDependencies.Count -gt 0) {
    throw (
        "Package dependency set changed. Unexpected: $($unexpectedDependencies -join ', '). " +
        "Missing: $($missingDependencies -join ', '). The contract declares exactly the assemblies " +
        "its own public signatures name, and nothing else."
    )
}

Write-Output "Verified $([System.IO.Path]::GetFileName($PackageFile)): id, version $($metadata.version), metadata, readme, $($assemblies[0].Name) at AssemblyVersion $actualAssemblyVersion, XML docs, $($actualTypes.Count) exported type(s), and dependencies $($declaredDependencies -join ', ')."
