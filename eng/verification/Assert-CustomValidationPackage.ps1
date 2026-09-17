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
    $TargetFramework = "net10.0",

    # The committed implementer guide the packed readme must be a copy of. Passed in with a default
    # rather than hardcoded, so the Pester cases can point it at a fixture without rewriting a
    # tracked file.
    [string]
    $GuidePath = (Join-Path $PSScriptRoot "../../src/dms/core/EdFi.DataManagementService.CustomValidation/CUSTOM-VALIDATION.md")
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
$packedReadmePath = Join-Path $ExtractTo "CUSTOM-VALIDATION.md"
if (-not (Test-Path -LiteralPath $packedReadmePath)) {
    throw "Package does not carry the readme file itself"
}

# The readme is the implementer guide, and "a file called CUSTOM-VALIDATION.md is present" does not
# say that. What is asserted instead is that the packed copy IS the committed guide, compared whole.
#
# That is one check rather than a list of sections or phrases, and it is the stronger statement:
# it fails on a stale artifact packed before the guide was edited, on a readme mutated after
# packing, and on a guide replaced by a placeholder, none of which a heading survey would notice.
# A word checklist would also pass a guide whose prose had been gutted around the words it looked
# for.
#
# It composes with the document-embed gate to cover the samples, which is the property that
# matters most and which neither check reaches alone. Assert-DocumentEmbeds.ps1 holds the committed
# guide's three fenced blocks to the fixture sources that eng/verification's own consumer check
# compiles and runs; this holds the packed readme to that committed guide. So the samples an
# implementer copies out of the published package are the samples that were compiled.
#
# Line endings are normalized on both sides first: a checkout with autocrlf would otherwise fail
# this for a reason that has nothing to do with the content. Trailing whitespace is trimmed from
# the end of each whole text and nowhere else, absorbing a trailing-newline difference between the
# working tree and the packed copy without hiding drift on an interior line.
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
        "The packed CUSTOM-VALIDATION.md is not the committed implementer guide. Either the package " +
        "predates an edit to the guide and must be repacked, or the packed copy was altered. First " +
        "difference at $firstDifference."
    )
}

# The three sample regions the published guide must carry, pinned by claim rather than by content.
# The equality check above already proves the packed readme matches the repository, so this is not
# a second content check: it is what stops the pair from agreeing on a guide that has quietly lost a
# sample, which would otherwise need the document-embed table to still name all three to be caught.
$requiredSampleClaim = @(
    "eng/verification/CustomValidatorPluginConsumer/StudentIdentityOptions.cs#options"
    "eng/verification/CustomValidatorPluginConsumer/StudentIdentityValidator.cs#validator"
    "eng/verification/CustomValidatorPluginConsumer/StudentIdentityPlugin.cs#plugin"
)

foreach ($claim in $requiredSampleClaim) {
    if ($packedReadme -notmatch "(?m)^<!--\s*embed:\s*$([regex]::Escape($claim))\s*-->$") {
        throw "The packed guide carries no '<!-- embed: $claim -->' block. An implementer needs the validator, the plugin that registers it, and the options type both depend on; a guide missing one of the three publishes code that does not compile where it is read."
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

# The guide and the interface's own XML documentation both tell an implementer how a validator is
# delivered, and an implementer reads whichever they reach first: the IDE tooltip or the packed
# readme. So the two must not disagree, and both are in this one artifact, which is what lets that
# be asserted rather than hoped for.
#
# The delivery path was reversed once already. The interface used to say an implementation "is
# compiled into the host deployment ... it is not loaded from a dropped-in assembly at runtime",
# which the plugin work made false, and the risk this guards is a revert or a merge restoring that
# sentence in one document while the other still describes plugin delivery.
#
# Two properties per document, the same two, which is what "they agree" means here:
#
#   - each names the allowlist key, which is the operative fact of plugin delivery and which a
#     document describing compiled-in delivery would have no reason to mention;
#   - neither carries the obsolete denial.
#
# What is matched is the **denial**, "not loaded from a dropped-in assembly at runtime", and not the
# word "compiled" and not the bare noun phrase "dropped-in assembly at runtime". All three
# distinctions are load-bearing:
#
#   - matching on "compiled" would refuse the accurate prose, in both documents, that compiling a
#     validator into a DMS build remains possible and is not the documented route;
#   - matching the bare noun phrase would refuse the accurate affirmative, "a validator IS loaded
#     from a dropped-in assembly at runtime", which is the true statement of plugin delivery and the
#     opposite of the claim being guarded against.
#
# Whitespace is collapsed on both sides before the comparison, because neither document controls
# where the sentence breaks. An XML doc comment wraps at the source line and a Markdown paragraph
# wraps at the column limit, so the denial reaches this check with newlines and leading slashes
# between its words. Matching the raw text would have missed every real revert and only caught one
# written as a single unbroken line.
function Get-CollapsedProse {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Text)

    return ($Text -replace '\s+', ' ').Trim()
}
# InnerText rather than the summary property. The summary carries child elements - <see cref>,
# <c>Plugins:Allowed</c> - so the property yields an XmlElement whose string form drops exactly the
# words being looked for, and the allowlist key is inside a <c> element. InnerText flattens the
# whole subtree, which is what an implementer's IDE renders.
$validatorSummary = (
    $xmlDoc.doc.members.member |
        Where-Object { $_.name -eq "T:$($AssemblyName).ICustomResourceValidator" } |
        ForEach-Object { $_.SelectSingleNode("summary").InnerText }
) -join "`n"

if ([string]::IsNullOrWhiteSpace($validatorSummary)) {
    throw "The packed XML documentation carries no summary for T:$($AssemblyName).ICustomResourceValidator, so the delivery path it tells an implementer cannot be compared against the guide."
}

$obsoleteDeliveryDenial = "not loaded from a dropped-in assembly at runtime"
$allowlistKey = "Plugins:Allowed"

foreach (
    $document in @(
        [pscustomobject]@{ Name = "ICustomResourceValidator's XML documentation"; Text = $validatorSummary }
        [pscustomobject]@{ Name = "the packed CUSTOM-VALIDATION.md"; Text = $packedReadme }
    )
) {
    $collapsed = Get-CollapsedProse -Text $document.Text

    if ($collapsed.Contains($obsoleteDeliveryDenial, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$($document.Name) still says a validator is '$obsoleteDeliveryDenial', which the plugin delivery path made false: a validator IS loaded from a dropped-in assembly at run time. It ships as a published directory under the plugin root and named in $allowlistKey. Correct it so the guide and the contract's own documentation agree."
    }

    if (-not $collapsed.Contains($allowlistKey, [StringComparison]::Ordinal)) {
        throw "$($document.Name) never mentions $allowlistKey, so it does not state the delivery path a validator actually takes. The guide and the contract's own documentation must agree that a validator is delivered as an allowlisted plugin."
    }
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

Write-Output "Verified $([System.IO.Path]::GetFileName($PackageFile)): id, metadata, the packed readme is the committed implementer guide carrying all $($requiredSampleClaim.Count) sample blocks, the guide and ICustomResourceValidator's documentation agree on plugin delivery, $($assemblies[0].Name), XML docs, $($actualTypes.Count) exported types, and empty dependency set."
