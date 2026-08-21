# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Asserts on the contents of a packed EdFi.Api.CustomValidation nupkg.

.DESCRIPTION
    Asserts on what is inside the artifact, not that a file appeared. Dropping the readme, the
    license, the assembly, or the XML docs from the csproj would otherwise ship silently; a stray
    PackageReference would quietly widen every implementer's dependency closure; and an accidental
    public type would widen the surface the first published version commits to.

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

    # The assembly the package must carry. Asserted by name because the name is part of what an
    # already-compiled validator binds to, so changing it is a breaking change that "some dll is
    # present" would not notice.
    [string]
    $AssemblyName = "EdFi.DataManagementService.CustomValidation",

    # The target framework whose lib/ folder must carry the assembly and its XML documentation.
    [string]
    $TargetFramework = "net10.0"
)

$ErrorActionPreference = "Stop"

# The exact public surface the package is allowed to export, nested types included. The project
# builds with GenerateDocumentationFile and TreatWarningsAsErrors, so CS1591 makes documenting every
# public type mandatory, which is what lets the shipped XML file stand in for the type surface here.
$expectedTypes = @(
    "EdFi.DataManagementService.CustomValidation.CustomValidationFailure",
    "EdFi.DataManagementService.CustomValidation.CustomValidationFailure.OnPath",
    "EdFi.DataManagementService.CustomValidation.CustomValidationFailure.OnResource",
    "EdFi.DataManagementService.CustomValidation.CustomValidationOperation",
    "EdFi.DataManagementService.CustomValidation.ICustomResourceValidator",
    "EdFi.DataManagementService.CustomValidation.ValidatedResource",
    "EdFi.DataManagementService.CustomValidation.ValidatedResourceInfo",
    "EdFi.DataManagementService.CustomValidation.ValidationScope"
)

if (-not (Test-Path -LiteralPath $PackageFile)) {
    throw "Expected custom-validation package was not found: $PackageFile"
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

[xml]$nuspec = Get-Content -LiteralPath (Join-Path $ExtractTo "$PackageId.nuspec")
$metadata = $nuspec.package.metadata

if ($metadata.id -ne $PackageId) {
    throw "Unexpected package id: $($metadata.id)"
}
if ($metadata.license.'#text' -ne "Apache-2.0") {
    throw "Missing or unexpected license expression: $($metadata.license.'#text')"
}
if ($metadata.readme -ne "CUSTOM-VALIDATION.md") {
    throw "Missing packed readme: $($metadata.readme)"
}
foreach ($required in "description", "title", "projectUrl") {
    if ([string]::IsNullOrWhiteSpace($metadata.$required)) {
        throw "Missing package metadata: $required"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $ExtractTo "CUSTOM-VALIDATION.md"))) {
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

# A zero-dependency closure is the contract: an implementer takes on nothing they did not choose.
# A dependency appearing here means a PackageReference crept into the contract project.
if ($null -ne $metadata.dependencies) {
    $declared = $metadata.dependencies.SelectNodes(".//*[local-name()='dependency']")
    if ($declared.Count -gt 0) {
        $names = ($declared | ForEach-Object { $_.id }) -join ", "
        throw "Package must have no dependencies, found: $names"
    }
}

Write-Output "Verified $([System.IO.Path]::GetFileName($PackageFile)): id, metadata, readme, $($assemblies[0].Name), XML docs, $($actualTypes.Count) exported types, and empty dependency set."
