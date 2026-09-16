# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.DESCRIPTION
The three comparisons that decide whether a contract package already on the feed is the same
contract as the one just packed, plus the NuGet identity rules those comparisons rest on.

Separated from Invoke-ContractPublishCheck.ps1 so each rule can be exercised on its own: the
decision script is a short policy over these functions.

Nothing here contacts a feed. Everything takes paths on disk.
#>

$ErrorActionPreference = "Stop"

# NuGet's own parsers, loaded from the installed SDK rather than reimplemented. Hand-rolled parsing
# accepted malformed input as a valid equivalent: "(1.0.0)" is not a range NuGet accepts and became
# an exact pin, and "1.0.0-" is not a version and became 1.0.0. Repairing malformed metadata into a
# value that compares equal is the one outcome a publish gate must never produce.
$script:nuGetAssembliesLoaded = $false

<#
.DESCRIPTION
The directory of the highest installed .NET SDK, which is where NuGet.Versioning.dll and
NuGet.Frameworks.dll ship.

Selection is deterministic: the SDK list is parsed, non-parsing entries are ignored, and the highest
version wins. `dotnet --list-sdks` prints `<version> [<containing directory>]` on every platform,
which is what makes this work on the Linux runner the verification suite runs on.
#>
function Get-NuGetAssemblyDirectory {
    [CmdletBinding()]
    [OutputType([string])]
    param()

    $best = $null

    foreach ($line in (& dotnet --list-sdks)) {
        if ($line -notmatch '^(\S+)\s+\[(.+)\]\s*$') {
            continue
        }

        $parsed = [version] '0.0.0'

        if (-not [version]::TryParse((($Matches[1] -split '-')[0]), [ref] $parsed)) {
            continue
        }

        $candidate = [pscustomobject]@{ Version = $parsed; Path = (Join-Path $Matches[2] $Matches[1]) }

        if ($null -eq $best -or $candidate.Version -gt $best.Version) {
            $best = $candidate
        }
    }

    if ($null -eq $best) {
        throw "Cannot locate a .NET SDK: 'dotnet --list-sdks' listed none this script could parse."
    }

    return $best.Path
}

function Initialize-NuGetVersioning {
    [CmdletBinding()]
    param()

    if ($script:nuGetAssembliesLoaded) {
        return
    }

    $directory = Get-NuGetAssemblyDirectory

    foreach ($name in @("NuGet.Versioning.dll", "NuGet.Frameworks.dll")) {
        $path = Join-Path $directory $name

        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Cannot load NuGet's parsers: $path does not exist."
        }

        # Add-Type is per-process and permanent, so the flag below is what keeps a second call from
        # reloading it.
        Add-Type -Path $path
    }

    $script:nuGetAssembliesLoaded = $true
}

<#
.DESCRIPTION
Sorts strings ordinally.

Sort-Object orders by the current culture even with -CaseSensitive, so the same two canonical lines
can order differently on two machines, and a comparison that walks both lists in step then reports
differences that are only a collation difference. Every canonical list this module produces is
ordered through here instead.
#>
function ConvertTo-OrdinalOrder {
    [CmdletBinding()]
    [OutputType([string[]], [object[]])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]
        $Value
    )

    $sorted = [string[]] @($Value)
    [array]::Sort($sorted, [System.StringComparer]::Ordinal)

    return , $sorted
}

<#
.DESCRIPTION
The canonical form of a dependency's include or exclude attribute.

NuGet reads these as a comma-delimited set of asset groups, so "compile,runtime" and
"runtime, compile" say the same thing and must not demand a version bump. The tokens are trimmed,
lowercased and ordered ordinally; an empty attribute stays empty.
#>
function ConvertTo-CanonicalAssetSet {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $Assets
    )

    if ([string]::IsNullOrWhiteSpace($Assets)) {
        return ""
    }

    # Deduplicated as well as ordered: NuGet reads this as a set of asset groups, so "compile,compile"
    # and "compile" are one statement and must not demand a version bump.
    $tokens = @(
        $Assets -split ',' |
            ForEach-Object { $_.Trim().ToLowerInvariant() } |
            Where-Object { $_.Length -gt 0 } |
            Select-Object -Unique
    )

    if ($tokens.Count -eq 0) {
        return ""
    }

    return (ConvertTo-OrdinalOrder -Value $tokens) -join ','
}

<#
.DESCRIPTION
The NuGet-normalized form of a package version.

Feed URLs, and therefore the comparison that decides whether a version is already published, are
built from this form. 1.0.0.0 and 1.0.0 are one version to NuGet, build metadata is not part of
identity, and a prerelease label differs only by case, so leaving any of that unnormalized would
either look up a version that does not exist or call two names for one version two versions.
#>
function ConvertTo-NormalizedPackageVersion {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $Version
    )

    Initialize-NuGetVersioning

    try {
        $parsed = [NuGet.Versioning.NuGetVersion]::Parse($Version)
    }
    catch {
        throw "'$Version' is not a package version NuGet accepts: $($_.Exception.Message)"
    }

    # Lowercased, because NuGet compares prerelease labels case-insensitively and the flat container
    # addresses a package by its lowercase normalized version. ToNormalizedString keeps the label as
    # written, so two spellings of one version would otherwise be two comparison keys.
    return $parsed.ToNormalizedString().ToLowerInvariant()
}

<#
.DESCRIPTION
The NuGet-normalized form of a package id, which is its lowercase form.
#>
function ConvertTo-NormalizedPackageId {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [string]
        $PackageId
    )

    $trimmed = $PackageId.Trim()

    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        throw "A package id is required, and '$PackageId' is empty."
    }

    return $trimmed.ToLowerInvariant()
}

<#
.DESCRIPTION
The normalized form of a declared dependency version range.

A bare version is a minimum rather than a pin, so it is expanded to the interval NuGet resolves it
as. Without that, "1.0.0" and "[1.0.0, )" would read as a widened range when nothing changed, and a
real widening from "[1.0.0]" to "[1.0.0, 2.0.0)" has to keep reading as the change it is.
#>
function ConvertTo-NormalizedVersionRange {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $Range
    )

    Initialize-NuGetVersioning

    # An omitted version attribute is how a nuspec says "any version", and NuGet resolves it as the
    # all-inclusive range. An empty string in a version attribute is not the same statement, but a
    # nuspec cannot distinguish them, so both take this branch.
    if ([string]::IsNullOrWhiteSpace($Range)) {
        return [NuGet.Versioning.VersionRange]::All.ToNormalizedString().ToLowerInvariant()
    }

    try {
        $parsed = [NuGet.Versioning.VersionRange]::Parse($Range)
    }
    catch {
        throw "'$Range' is not a version range NuGet accepts: $($_.Exception.Message)"
    }

    return $parsed.ToNormalizedString().ToLowerInvariant()
}

<#
.DESCRIPTION
The normalized short folder name of a dependency group's target framework.

net10.0 and .NETCoreApp,Version=v10.0 are one framework written two ways, and a group is selected by
the framework rather than by the spelling. A framework NuGet cannot parse is rejected rather than
compared as written, because an unsupported framework silently changes which group a consumer
resolves.
#>
function ConvertTo-NormalizedTargetFramework {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $Framework
    )

    if ([string]::IsNullOrWhiteSpace($Framework)) {
        return "(any)"
    }

    Initialize-NuGetVersioning

    try {
        $parsed = [NuGet.Frameworks.NuGetFramework]::Parse($Framework.Trim())
    }
    catch {
        throw "'$Framework' is not a target framework NuGet accepts: $($_.Exception.Message)"
    }

    if ($parsed.IsUnsupported) {
        throw "'$Framework' is not a target framework NuGet recognizes."
    }

    return $parsed.GetShortFolderName()
}

<#
.DESCRIPTION
The canonical form of one XML documentation file, as a sorted list of member entries.

Three rules, and each is deliberate.

The serialization is structural and escaped rather than XML-shaped text. Rendering an element as
"<b>must</b>" makes a member whose text is the literal string "<b>must</b>" identical to one that
really marks the word up, and those are different contracts: one shows an implementer angle
brackets, the other shows bold. Every name, attribute and text run is escaped so that no content can
imitate the delimiters around it.

Insignificant whitespace is normalized, so re-wrapping a comment at a different column is not a
contract change. Whitespace inside a <code> element, inside CDATA, or under any node carrying
xml:space="preserve" is kept exactly, including a node inheriting that request from an ancestor as
high as <doc> or the <member> itself. An implementer copies that text and its layout is part of what
they copy, which is also why the document is parsed with whitespace preserved: the default
XmlDocument discards whitespace-only text nodes, and under it a sample indented by one space and the
same sample indented by two are one string.

Everything else is compared ordinally, so a word changed, or a word changed only in case, is a
change. The contract's load-bearing rules live only in these comments, so a rule silently rewritten
at an unchanged version is precisely what this catches.

A file with no members, or with two members sharing one name, is rejected. Neither is a contract
this can compare, and an empty list would compare equal to any other empty list.
#>
function ConvertTo-CanonicalXmlDocumentation {
    [CmdletBinding()]
    # Both types are declared because the returned value is a string[] wrapped by the unary comma,
    # which the analyzer sees as Object[].
    [OutputType([string[]], [object[]])]
    param(
        [Parameter(Mandatory)]
        [string]
        $Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Cannot canonicalize XML documentation: $Path does not exist."
    }

    $document = [System.Xml.XmlDocument]::new()

    # Without this the parser throws away whitespace-only text nodes, and every whitespace
    # distinction inside a code sample disappears before this function ever sees it.
    $document.PreserveWhitespace = $true

    try {
        $document.LoadXml((Get-Content -LiteralPath $Path -Raw))
    }
    catch {
        throw "Cannot canonicalize XML documentation: $Path is not well-formed XML. $($_.Exception.Message)"
    }

    $members = @($document.SelectNodes("/doc/members/member"))

    if ($members.Count -eq 0) {
        throw "Cannot canonicalize XML documentation: $Path declares no members under /doc/members. A contract package with no documented members is not comparable."
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

    $canonical = foreach ($member in $members) {
        $name = $member.GetAttribute("name")

        if ([string]::IsNullOrWhiteSpace($name)) {
            throw "Cannot canonicalize XML documentation: $Path carries a member with no name."
        }

        if (-not $seen.Add($name)) {
            throw "Cannot canonicalize XML documentation: $Path declares '$name' more than once, so its members cannot be compared one to one."
        }

        # Inherited from every ancestor, so xml:space on <doc>, on <members>, or on the member
        # itself governs that member's content.
        $preserve = Test-XmlPreserveSpace -Node $member

        (ConvertTo-CanonicalXmlText -Value $name) + " => " +
        (ConvertTo-CanonicalXmlNode -Node $member -Preserve $preserve)
    }

    # The unary comma is load bearing: PowerShell unwraps a one-element array into a scalar and an
    # empty one into $null on return, and a caller that received $null for "no members" would then
    # compare a null against a null and call two broken packages identical.
    return ConvertTo-OrdinalOrder -Value @($canonical)
}

<#
.DESCRIPTION
True when this node, or any ancestor, asks for whitespace to be preserved.
#>
function Test-XmlPreserveSpace {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlNode]
        $Node
    )

    # Two rules, and the order between them is the point.
    #
    # Anything inside a <code> element keeps its layout, whatever any node in between says. A sample
    # is what an implementer copies, and a nested <b xml:space="default"> inside it does not make
    # the sample's indentation cosmetic. So the whole chain is checked for <code> first.
    #
    # Everywhere else the nearest xml:space wins, including an explicit "default" that closes a
    # preserving ancestor's request. That is what xml:space means, and treating a nearer default as
    # "not mentioned" would keep preserving below it.
    $current = $Node

    while ($null -ne $current) {
        if ($current -is [System.Xml.XmlElement]) {
            $element = [System.Xml.XmlElement] $current

            if ($element.LocalName -ceq "code") {
                return $true
            }
        }

        $current = $current.ParentNode
    }

    $current = $Node

    while ($null -ne $current) {
        if ($current -is [System.Xml.XmlElement]) {
            $element = [System.Xml.XmlElement] $current
            $space = $element.GetAttribute("xml:space")

            if ($space -ceq "preserve") {
                return $true
            }

            if ($space -ceq "default") {
                return $false
            }
        }

        $current = $current.ParentNode
    }

    return $false
}

<#
.DESCRIPTION
One node's children, serialized structurally so that no content can be mistaken for markup.

Text is emitted as T{...}, an element as E{name}A{...}[...], and CDATA as C{...}, with every value
escaped. Child order is preserved because it is the order an implementer reads; attributes are
ordered by name so that a reordering is not a change.
#>
function ConvertTo-CanonicalXmlNode {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlNode]
        $Node,

        # True once this node, or any ancestor, has asked for whitespace to be kept.
        [Parameter(Mandatory)]
        [bool]
        $Preserve
    )

    $builder = [System.Text.StringBuilder]::new()

    foreach ($child in $Node.ChildNodes) {
        $nodeType = $child.NodeType

        if (
            $nodeType -eq [System.Xml.XmlNodeType]::Text -or
            $nodeType -eq [System.Xml.XmlNodeType]::Whitespace -or
            $nodeType -eq [System.Xml.XmlNodeType]::SignificantWhitespace
        ) {
            $text = $child.Value

            if (-not $Preserve) {
                $text = ([regex]::Replace($text, '\s+', ' ')).Trim()

                # A run that was only formatting contributes nothing, so indentation outside a
                # preserved context does not move the canonical form.
                if ($text.Length -eq 0) {
                    continue
                }
            }

            [void] $builder.Append("T{").Append((ConvertTo-CanonicalXmlText -Value $text)).Append("}")

            continue
        }

        if ($nodeType -eq [System.Xml.XmlNodeType]::CDATA) {
            # Always verbatim: an author chose CDATA to stop the parser touching the content.
            [void] $builder.Append("C{").Append((ConvertTo-CanonicalXmlText -Value $child.Value)).Append("}")

            continue
        }

        if ($nodeType -eq [System.Xml.XmlNodeType]::Element) {
            $element = [System.Xml.XmlElement] $child

            # The element's own walk is the whole answer, and inheriting the caller's value with -or
            # would be wrong: Test-XmlPreserveSpace already climbs ancestors and stops at the nearest
            # of code, xml:space="preserve" or xml:space="default", so a nearer default must be able
            # to reset a preserving ancestor. Or-ing the inherited true made that reset unreachable.
            $childPreserve = Test-XmlPreserveSpace -Node $element

            $attributes = ConvertTo-OrdinalOrder -Value @(
                $element.Attributes |
                    ForEach-Object {
                        (ConvertTo-CanonicalXmlText -Value $_.Name) + "=" +
                        (ConvertTo-CanonicalXmlText -Value $_.Value)
                    }
            )

            [void] $builder.Append("E{").Append((ConvertTo-CanonicalXmlText -Value $element.Name)).Append("}")
            [void] $builder.Append("A{").Append($attributes -join ";").Append("}")
            [void] $builder.Append("[")
            [void] $builder.Append((ConvertTo-CanonicalXmlNode -Node $element -Preserve $childPreserve))
            [void] $builder.Append("]")

            continue
        }

        # Comments and processing instructions describe the file rather than the contract.
    }

    return $builder.ToString()
}

<#
.DESCRIPTION
One value, escaped so that it cannot imitate the delimiters this serialization uses.
#>
function ConvertTo-CanonicalXmlText {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $Value
    )

    $escaped = $Value

    # The backslash first, or every escape introduced below would be escaped a second time.
    foreach ($character in @('\', '{', '}', '[', ']', ';', '=')) {
        $escaped = $escaped.Replace($character, '\' + $character)
    }

    return $escaped
}

<#
.DESCRIPTION
The declared dependencies of a nuspec, canonically.

Both layouts are handled. A <dependencies> element may hold <group targetFramework="..."> elements
or bare <dependency> elements, and the bare form is the same statement as a single group with no
framework. Comparing the raw XML would call those two spellings of one dependency set a change.

include and exclude are part of each entry, because they decide what a consumer actually inherits
from the dependency rather than merely which version resolves.
#>
function ConvertTo-CanonicalDependencySet {
    [CmdletBinding()]
    [OutputType([string[]], [object[]])]
    param(
        [Parameter(Mandatory)]
        [string]
        $NuspecPath
    )

    if (-not (Test-Path -LiteralPath $NuspecPath -PathType Leaf)) {
        throw "Cannot read declared dependencies: $NuspecPath does not exist."
    }

    $document = [xml] (Get-Content -LiteralPath $NuspecPath -Raw)
    $declarations = @($document.SelectNodes("//*[local-name()='dependencies']"))

    # More than one dependencies element is not a nuspec NuGet produces, and picking the first would
    # silently compare one declaration while a consumer resolves against another.
    if ($declarations.Count -gt 1) {
        throw "Cannot read declared dependencies: $NuspecPath carries $($declarations.Count) dependencies elements; exactly one is allowed."
    }

    if ($declarations.Count -eq 0) {
        return , [string[]] @()
    }

    $dependencies = $declarations[0]

    $entries = foreach ($node in $dependencies.ChildNodes) {
        if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element) {
            continue
        }

        switch ($node.LocalName) {
            "group" {
                $framework = $node.GetAttribute("targetFramework")

                $declared = @(
                    $node.ChildNodes |
                        Where-Object {
                            $_.NodeType -eq [System.Xml.XmlNodeType]::Element -and
                            $_.LocalName -ceq "dependency"
                        }
                )

                # An empty group is a statement, not an absence. NuGet picks the single best-matching
                # group, so an empty group for a specific framework gives a consumer on that
                # framework no dependencies at all, where removing the group would let a fallback
                # group's dependencies apply instead. Emitting nothing for it would make those two
                # packages compare equal.
                if ($declared.Count -eq 0) {
                    "$(ConvertTo-NormalizedTargetFramework -Framework $framework) | (empty group)"
                }

                foreach ($dependency in $declared) {
                    ConvertTo-CanonicalDependencyEntry -Framework $framework -Dependency $dependency
                }
            }

            "dependency" {
                # The bare form is one group with no target framework.
                ConvertTo-CanonicalDependencyEntry -Framework "" -Dependency $node
            }

            default {
                continue
            }
        }
    }

    $ordered = ConvertTo-OrdinalOrder -Value @($entries)

    # One framework cannot declare one package id twice: NuGet would resolve one of them and the
    # other is dead metadata, so comparing a set that silently held both would be comparing
    # something no consumer sees.
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

    foreach ($entry in $ordered) {
        $identity = ($entry -split '\|')[0..1] -join '|'

        if (-not $seen.Add($identity)) {
            throw "Cannot read declared dependencies: $NuspecPath declares '$($identity.Trim())' more than once."
        }
    }

    return , $ordered
}

<#
.DESCRIPTION
One declared dependency as a single canonical line: its target framework, its normalized id, its
normalized version range, and its include and exclude asset lists.

Split out from ConvertTo-CanonicalDependencySet because both nuspec layouts, grouped and bare, end
up here, and one entry must render identically whichever layout declared it.
#>
function ConvertTo-CanonicalDependencyEntry {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $Framework,

        [Parameter(Mandatory)]
        [System.Xml.XmlElement]
        $Dependency
    )

    $id = $Dependency.GetAttribute("id")

    if ([string]::IsNullOrWhiteSpace($id)) {
        throw "A declared dependency carries no id."
    }

    $range = ConvertTo-NormalizedVersionRange -Range $Dependency.GetAttribute("version")

    # Normalized as sets: NuGet reads these as comma-delimited asset groups, so a reordering is the
    # same statement and must not demand a version bump.
    $include = ConvertTo-CanonicalAssetSet -Assets $Dependency.GetAttribute("include")
    $exclude = ConvertTo-CanonicalAssetSet -Assets $Dependency.GetAttribute("exclude")

    # Normalized rather than compared as written: net10.0 and .NETCoreApp,Version=v10.0 select the
    # same group, so two spellings of one framework must not read as two groups.
    $frameworkText = ConvertTo-NormalizedTargetFramework -Framework $Framework

    return "$frameworkText | $(ConvertTo-NormalizedPackageId -PackageId $id) | $range | include=$include | exclude=$exclude"
}

Export-ModuleMember -Function `
    ConvertTo-OrdinalOrder, `
    ConvertTo-CanonicalAssetSet, `
    ConvertTo-NormalizedPackageVersion, `
    ConvertTo-NormalizedPackageId, `
    ConvertTo-NormalizedVersionRange, `
    ConvertTo-CanonicalXmlDocumentation, `
    ConvertTo-CanonicalXmlNode, `
    ConvertTo-CanonicalXmlText, `
    Test-XmlPreserveSpace, `
    ConvertTo-NormalizedTargetFramework, `
    ConvertTo-CanonicalDependencySet, `
    ConvertTo-CanonicalDependencyEntry
