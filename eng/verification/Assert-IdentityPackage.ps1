# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Asserts on the contents of a packed EdFi.Api.Identity nupkg.

.DESCRIPTION
    Asserts on what is inside the artifact, not that a file appeared. Dropping the readme, the
    license, the assembly, or the XML docs from the csproj would otherwise ship silently; a stray
    PackageReference would quietly widen every implementer's dependency closure; and an accidental
    public type would widen the surface the first published version commits to.

    The package version is asserted against a value the caller derives from the contract's own
    declared version, because this package is deliberately not packed at the DMS release version.

    The packed assembly's AssemblyVersion is asserted, because the loader's
    newer-plugin-on-older-host preflight compares exactly that value. If the packed assembly's
    version ever disagreed with the package version an implementer resolved, the preflight would be
    comparing something other than what the implementer built against, and it would be silently
    blind rather than loudly wrong.

    Unlike the sibling plugin-contract script, the readme is checked for presence and metadata only,
    not for content. IIdentityService's implementer guide has not shipped yet, and IDENTITY.md says
    so in as many words; asserting guide content here would either pin today's placeholder text as
    if it were the real thing or force this script to special-case a placeholder it is not this
    script's job to know about. The guide's own content is a later script's problem once it exists.

    This script adds one assertion the sibling plugin-contract script does not need: the contract's
    load-bearing rules - context-equality, async-token usability, the UniqueId constraints - live
    only in /// comments on the interface and its types, because the interface itself carries almost
    no runtime-checkable shape. A handful of load-bearing phrases from that documentation are
    asserted present in the shipped XML file, each one a phrase rather than a bare identifier, so
    that a doc comment which still names a symbol but has quietly dropped the rule around it cannot
    pass. This does not attempt to be exhaustive over the contract's documented behavior; it is a
    tripwire for the handful of rules that are easiest to silently soften in a rewrite.

    The dependency check is one-sided here rather than two-sided like the sibling plugin-contract
    script's: this contract's public surface names only framework types that resolve without any
    PackageReference, so the only thing that can go wrong is a dependency appearing at all.

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

    # A dedicated extraction directory, which must either not exist or be empty. This script never
    # deletes anything the caller owns, so callers pass a fresh unique path per invocation rather
    # than reusing one. Requiring it to be empty is also what stops a previous extraction from
    # satisfying a presence check for a file the package under test no longer carries.
    [Parameter(Mandatory)]
    [string]
    $ExtractTo,

    # The package id every assertion below is made against. Passed in rather than hardcoded so the
    # id lives in one place per lane and a rename cannot leave this script checking the old one.
    [Parameter(Mandatory)]
    [string]
    $PackageId,

    # The version the package must declare, which the caller reads from the contract's own
    # csproj via Get-PluginsContractVersion. Passed in rather than read here so that this script
    # asserts against the lane's single declared version instead of re-deriving one and agreeing
    # with itself.
    [Parameter(Mandatory)]
    [string]
    $ExpectedPackageVersion,

    # The assembly the package must carry. Asserted by name because the name is part of what an
    # already-compiled identity provider binds to, so changing it is a breaking change that "some
    # dll is present" would not notice.
    [string]
    $AssemblyName = "EdFi.DataManagementService.Identity",

    # The target framework whose lib/ folder must carry the assembly and its XML documentation.
    [string]
    $TargetFramework = "net10.0"
)

$ErrorActionPreference = "Stop"

# The exact public surface the package is allowed to export, nested types included. The project
# builds with GenerateDocumentationFile and TreatWarningsAsErrors, so CS1591 makes documenting every
# public type mandatory, which is what lets the shipped XML file stand in for the type surface here.
$expectedTypes = @(
    "EdFi.DataManagementService.Identity.IdentityAsyncResult",
    "EdFi.DataManagementService.Identity.IdentityCapabilities",
    "EdFi.DataManagementService.Identity.IdentityError",
    "EdFi.DataManagementService.Identity.IdentityRequestContext",
    "EdFi.DataManagementService.Identity.IdentityResult",
    "EdFi.DataManagementService.Identity.IdentityResultStatus",
    "EdFi.DataManagementService.Identity.IIdentityService"
)

# Five short pieces of rule text pulled from the interface's own /// comments, chosen because each
# is a specific, checkable number or comparer rather than prose that could be reworded without
# changing meaning. Each is matched as a phrase, not a bare identifier: "OrdinalIgnoreCase" alone
# would still be present in a doc comment that merely named the type somewhere unrelated, so every
# phrase below also carries the words that state what the identifier is used for. A doc comment
# that renamed the rule - Ordinal for OrdinalIgnoreCase, 1024 for some other ceiling, 32 for some
# other limit - fails these checks even though the symbol it references still compiles.
#
# Matched against the whole file with runs of whitespace collapsed to a single space, because a
# generated XML doc comment wraps at the source line, and the wrap point moves if a sentence
# earlier in the same comment is edited. Matching the raw multi-line text would fail on that
# harmless a reflow as readily as on a real change to the rule.
$requiredXmlDocLandmarks = @(
    # The tenant-name comparison rule on IdentityRequestContext.Tenant: OrdinalIgnoreCase, and
    # specifically for names, not the case-sensitive rule ClientId carries below.
    'OrdinalIgnoreCase"/> for names; a null tenant denotes single-tenant mode',

    # The client-id comparison rule on IdentityRequestContext.ClientId: Ordinal and case-sensitive,
    # the opposite rule from Tenant's. The phrase starts at the closing quote of the cref so it
    # cannot match inside the longer word "OrdinalIgnoreCase" above.
    'Ordinal"/>, case-sensitive and unchanged from the authenticated claim',

    # The 1024-character ceiling on an async result's escaped RequestToken.
    "The 1024-character ceiling bounds the escaped form because escaping only ever expands a token",

    # The 32-character UniqueId limit CreateAsync documents against the checked-in ApiSchema.
    "while the same GUID in 32-character hyphen-free form does",

    # The $[n].property JSONPath form IdentityError.Path documents for an array-item failure.
    'with an array item addressed as <c>$[n].property</c>'
)

$expectedReadme = "IDENTITY.md"

if (-not (Test-Path -LiteralPath $PackageFile)) {
    throw "Expected identity contract package was not found: $PackageFile"
}

# This script never deletes anything the caller owns. A directory holding any package's nuspec is
# not evidence that this script produced it, so the requirement moves to the caller: pass a path
# that does not exist, or one that is empty. Callers use a fresh unique directory per invocation. An
# emptiness requirement preserves the property a delete-and-recreate would give, which is that a
# file left by an earlier extraction must not satisfy a presence check for a file the package under
# test no longer carries.
if (Test-Path -LiteralPath $ExtractTo) {
    $existingEntries = @(Get-ChildItem -LiteralPath $ExtractTo -Force)

    if ($existingEntries.Count -gt 0) {
        throw "Refusing to extract into $ExtractTo : it is not empty, and this script never deletes caller content. Pass a dedicated extraction directory that is empty or does not exist."
    }
}
else {
    New-Item -ItemType Directory -Path $ExtractTo -Force | Out-Null
}

Expand-Archive -LiteralPath $PackageFile -DestinationPath $ExtractTo

[xml]$nuspec = Get-Content -LiteralPath (Join-Path $ExtractTo "$PackageId.nuspec")
$metadata = $nuspec.package.metadata

if ($metadata.id -ne $PackageId) {
    throw "Unexpected package id: $($metadata.id)"
}

# The contract carries its own semantic version rather than the DMS release version. Asserting it
# here is what stops a pack lane from quietly reintroducing -p:PackageVersion=`$DMSVersion.
if ($metadata.version -ne $ExpectedPackageVersion) {
    throw "Unexpected package version: expected $ExpectedPackageVersion, found $($metadata.version). The identity contract is versioned on its own surface, not on the DMS release."
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
# for this package: under it a provider built against 1.3 records a reference to 1.0.0.0, a 1.0 host
# satisfies it, and the missing member surfaces as a MissingMethodException inside a call.
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

# The interface has almost no runtime-checkable shape of its own - a provider is any type that
# implements five methods and a getter - so the behavioral contract lives entirely in the /// text
# above them. A rewrite that keeps every signature but softens or drops a rule would pass every
# check above this one. Read the raw XML rather than parsed member text so a phrase spanning a
# <see cref> - like the two comparer rules below - can still be matched: parsing to InnerText would
# resolve the cref away and delete the very word being checked for.
$xmlDocRawText = [System.IO.File]::ReadAllText($xmlDocPath)
$xmlDocCollapsedText = ($xmlDocRawText -replace '\s+', ' ')

$missingLandmarks = @(
    $requiredXmlDocLandmarks | Where-Object {
        -not $xmlDocCollapsedText.Contains($_, [StringComparison]::Ordinal)
    }
)
if ($missingLandmarks.Count -gt 0) {
    throw (
        "The shipped XML documentation for $($assemblies[0].Name) no longer contains " +
        "$($missingLandmarks.Count) load-bearing rule phrase(s): " +
        "$(($missingLandmarks | ForEach-Object { "'$_'" }) -join ', '). " +
        "A doc comment can still compile and still name the right symbol while quietly dropping the " +
        "rule around it; this package's XML file is the only place a plugin author's IDE shows that rule."
    )
}

# A zero-dependency closure is the contract: this surface names only System.Text.Json.Nodes types,
# which resolve out of the shared framework, so an implementer takes on nothing they did not choose.
# A dependency appearing here means a PackageReference crept into the contract project.
if ($null -ne $metadata.dependencies) {
    $declaredDependencies = @(
        $metadata.dependencies.SelectNodes(".//*[local-name()='dependency']") |
            ForEach-Object { $_.id }
    )
    if ($declaredDependencies.Count -gt 0) {
        throw "Package must have no dependencies, found: $($declaredDependencies -join ', ')"
    }
}

Write-Output "Verified $([System.IO.Path]::GetFileName($PackageFile)): id, version $($metadata.version), metadata, readme, $($assemblies[0].Name) at AssemblyVersion $actualAssemblyVersion, XML docs, $($actualTypes.Count) exported type(s), $($requiredXmlDocLandmarks.Count) documentation landmark(s), and an empty dependency set."
