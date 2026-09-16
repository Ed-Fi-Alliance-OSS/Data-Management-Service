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
        [string]
        $Version
    )

    $trimmed = $Version.Trim()

    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        throw "A package version is required, and '$Version' is empty."
    }

    # Build metadata is not part of a package's identity, so it is dropped rather than compared.
    $withoutMetadata = ($trimmed -split '\+', 2)[0]
    $parts = $withoutMetadata -split '-', 2
    $release = $parts[0]
    $prerelease = if ($parts.Count -gt 1) { $parts[1] } else { "" }

    $segments = $release -split '\.'

    if ($segments.Count -lt 2 -or $segments.Count -gt 4) {
        throw "Cannot normalize package version '$Version': its release portion has $($segments.Count) segment(s)."
    }

    $numbers = foreach ($segment in $segments) {
        $parsed = 0

        if (-not [int]::TryParse($segment, [ref] $parsed) -or $parsed -lt 0) {
            throw "Cannot normalize package version '$Version': '$segment' is not a release segment."
        }

        $parsed
    }

    $numbers = @($numbers)

    while ($numbers.Count -lt 3) {
        $numbers += 0
    }

    # A fourth segment survives only when it is non-zero, which is exactly NuGet's rule.
    if ($numbers.Count -eq 4 -and $numbers[3] -eq 0) {
        $numbers = $numbers[0..2]
    }

    $normalized = ($numbers -join '.')

    if ($prerelease.Length -gt 0) {
        # NuGet compares prerelease labels case-insensitively, so they are lowercased rather than
        # compared as written. Two packages whose labels differ only in case are one version.
        $normalized += "-" + $prerelease.ToLowerInvariant()
    }

    return $normalized
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

    $trimmed = $Range.Trim()

    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        # An omitted range means any version, which is what NuGet resolves it as.
        return "(, )"
    }

    if ($trimmed[0] -ne '[' -and $trimmed[0] -ne '(') {
        return "[" + (ConvertTo-NormalizedPackageVersion -Version $trimmed) + ", )"
    }

    $open = $trimmed[0]
    $close = $trimmed[-1]

    if ($close -ne ']' -and $close -ne ')') {
        throw "Cannot normalize version range '$Range': it opens with '$open' and does not close."
    }

    $inner = $trimmed.Substring(1, $trimmed.Length - 2)
    $bounds = $inner -split ',', 2

    $lower = $bounds[0].Trim()
    $upper = if ($bounds.Count -gt 1) { $bounds[1].Trim() } else { "" }

    # A single value in brackets is an exact pin, not an open interval.
    if ($bounds.Count -eq 1) {
        if ([string]::IsNullOrWhiteSpace($lower)) {
            throw "Cannot normalize version range '$Range': it declares no version."
        }

        $pinned = ConvertTo-NormalizedPackageVersion -Version $lower

        return "[$pinned]"
    }

    $lowerText = if ([string]::IsNullOrWhiteSpace($lower)) { "" } else { ConvertTo-NormalizedPackageVersion -Version $lower }
    $upperText = if ([string]::IsNullOrWhiteSpace($upper)) { "" } else { ConvertTo-NormalizedPackageVersion -Version $upper }

    return "$open$lowerText, $upperText$close"
}

<#
.DESCRIPTION
The canonical form of one XML documentation file, as a sorted list of member entries.

Two rules, and each is deliberate.

Insignificant whitespace is normalized, so re-wrapping a comment at a different column is not a
contract change. Whitespace inside a <code> element, or under any node carrying
xml:space="preserve", is kept exactly, because an implementer copies that text and its layout is
part of what they copy.

Everything else is compared ordinally, so a word changed, or a word changed only in case, is a
change. The contract's load-bearing rules live only in these comments, so a rule silently rewritten
at an unchanged version is precisely what this catches.
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

    $document = [xml] (Get-Content -LiteralPath $Path -Raw)

    # The <assembly> element names the assembly rather than describing the contract, and the
    # comparison is between two copies of one contract, so it carries no information here.
    $members = $document.SelectNodes("/doc/members/member")

    if ($null -eq $members) {
        throw "Cannot canonicalize XML documentation: $Path carries no /doc/members."
    }

    $canonical = foreach ($member in $members) {
        $name = $member.GetAttribute("name")

        if ([string]::IsNullOrWhiteSpace($name)) {
            throw "Cannot canonicalize XML documentation: $Path carries a member with no name."
        }

        "$name => " + (ConvertTo-CanonicalXmlNode -Node $member -Preserve $false)
    }

    # The unary comma is load bearing: PowerShell unwraps a one-element array into a scalar and an
    # empty one into $null on return, and a caller that received $null for "no members" would then
    # compare a null against a null and call two broken packages identical.
    return , [string[]] @($canonical | Sort-Object -CaseSensitive)
}

<#
.DESCRIPTION
One node's canonical text. Attributes are ordered by name so that an attribute reordering is not a
change, and child order is preserved because it is the order an implementer reads.
#>
function ConvertTo-CanonicalXmlNode {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)]
        [System.Xml.XmlNode]
        $Node,

        # True once any ancestor has asked for whitespace to be kept.
        [Parameter(Mandatory)]
        [bool]
        $Preserve
    )

    $builder = [System.Text.StringBuilder]::new()

    foreach ($child in $Node.ChildNodes) {
        switch ($child.NodeType) {
            ([System.Xml.XmlNodeType]::Text) {
                $text = $child.Value

                if (-not $Preserve) {
                    $text = ([regex]::Replace($text, '\s+', ' ')).Trim()
                }

                [void] $builder.Append($text)
            }

            ([System.Xml.XmlNodeType]::CDATA) {
                # Always verbatim: an author chose CDATA to stop the parser touching the content.
                [void] $builder.Append("<![CDATA[").Append($child.Value).Append("]]>")
            }

            ([System.Xml.XmlNodeType]::Element) {
                $element = [System.Xml.XmlElement] $child

                # <code> is the sample an implementer copies, and xml:space="preserve" is the
                # explicit request. Either one keeps the layout of everything below it.
                $childPreserve = $Preserve -or
                    $element.LocalName -ceq "code" -or
                    ($element.GetAttribute("xml:space") -ceq "preserve")

                $attributes = @(
                    $element.Attributes |
                        Sort-Object -Property Name -CaseSensitive |
                        ForEach-Object { "$($_.Name)=`"$($_.Value)`"" }
                )

                [void] $builder.Append("<").Append($element.Name)

                if ($attributes.Count -gt 0) {
                    [void] $builder.Append(" ").Append($attributes -join " ")
                }

                [void] $builder.Append(">")
                [void] $builder.Append((ConvertTo-CanonicalXmlNode -Node $element -Preserve $childPreserve))
                [void] $builder.Append("</").Append($element.Name).Append(">")
            }

            default {
                # Comments and processing instructions describe the file rather than the contract.
                continue
            }
        }
    }

    return $builder.ToString()
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
    $dependencies = $document.SelectSingleNode("//*[local-name()='dependencies']")

    if ($null -eq $dependencies) {
        return , [string[]] @()
    }

    $entries = foreach ($node in $dependencies.ChildNodes) {
        if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element) {
            continue
        }

        switch ($node.LocalName) {
            "group" {
                $framework = $node.GetAttribute("targetFramework")

                foreach ($dependency in $node.ChildNodes) {
                    if (
                        $dependency.NodeType -ne [System.Xml.XmlNodeType]::Element -or
                        $dependency.LocalName -cne "dependency"
                    ) {
                        continue
                    }

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

    return , [string[]] @($entries | Sort-Object -CaseSensitive)
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
    $include = $Dependency.GetAttribute("include")
    $exclude = $Dependency.GetAttribute("exclude")

    $frameworkText = if ([string]::IsNullOrWhiteSpace($Framework)) { "(any)" } else { $Framework.Trim() }

    return "$frameworkText | $(ConvertTo-NormalizedPackageId -PackageId $id) | $range | include=$include | exclude=$exclude"
}

Export-ModuleMember -Function `
    ConvertTo-NormalizedPackageVersion, `
    ConvertTo-NormalizedPackageId, `
    ConvertTo-NormalizedVersionRange, `
    ConvertTo-CanonicalXmlDocumentation, `
    ConvertTo-CanonicalXmlNode, `
    ConvertTo-CanonicalDependencySet, `
    ConvertTo-CanonicalDependencyEntry
