# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Asserts that fenced blocks in a Markdown document are verbatim copies of the files they name.

.DESCRIPTION
    A document that carries a deployment recipe has a drift problem: the file an operator actually
    composes with is the artifact, and the prose is a copy of it. This asserts the copy, and the
    direction matters. The committed file is the reference; the document is what is checked against
    it. Nothing anywhere drives a recipe parsed back out of Markdown, which would prove the document
    and not the artifact.

    A block is claimed by a marker on the line immediately above its opening fence:

        <!-- embed: eng/docker-compose/plugins-dms.yml -->
        ```yaml
        ...the file, verbatim...
        ```

    Marking the block rather than matching on content is what lets a failure name the file, lets the
    document order its sections freely, and lets a block be found at all when two files differ only
    in a line or two.

    -RequiredEmbed names the paths that must each be embedded exactly once, so a document that
    dropped a recipe fails here rather than passing on the blocks it kept. Every marker found is
    checked for drift whether or not it is required, so an additional embed cannot rot silently;
    requiring one is a separate statement from checking one.

    Trailing whitespace is trimmed from the end of each whole text and nowhere else. That absorbs the
    fence's own line break and the file's trailing newline, which are artifacts of the container
    rather than of the content. It deliberately does not trim per line: a trailing space introduced on
    an interior line is real drift in a file where whitespace is syntax, and it fails.
#>
[CmdletBinding()]
param(
    # The Markdown document whose embedded blocks are checked.
    [Parameter(Mandatory)]
    [string]
    $DocumentPath,

    # Repository-relative paths, exactly as written in the markers, that must each be embedded
    # exactly once. Passed in rather than discovered, because "every file matching a glob must be
    # documented" would conscript any future support or test overlay that has no operator audience.
    # An empty list is allowed and means "check every block that is here, require none". A mandatory
    # parameter that refused an empty array would prompt instead of running, which is a hang on a
    # non-interactive lane rather than a failure.
    [Parameter(Mandatory)]
    [AllowEmptyCollection()]
    [string[]]
    $RequiredEmbed,

    # The root the marker paths are resolved against.
    [string]
    $RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
)

$ErrorActionPreference = "Stop"

# Line endings are normalized on both sides before anything is compared. A checkout with autocrlf
# would otherwise fail every block for a reason that has nothing to do with the content.
function Read-NormalizedText {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Path)

    return ([System.IO.File]::ReadAllText($Path)).Replace("`r`n", "`n")
}

if (-not (Test-Path -LiteralPath $DocumentPath)) {
    throw "Document not found: $DocumentPath"
}

$documentLines = (Read-NormalizedText -Path $DocumentPath) -split "`n"

# The marker is anchored to the whole line so that a mention of the syntax inside prose or inside an
# embedded block cannot be mistaken for a claim.
$markerPattern = '^<!--\s*embed:\s*(\S+)\s*-->$'
$fenceOpenPattern = '^```[A-Za-z0-9_-]*$'

$markers = @()
for ($index = 0; $index -lt $documentLines.Count; $index++) {
    if ($documentLines[$index] -match $markerPattern) {
        $markers += [pscustomobject]@{
            Path = $Matches[1]
            Line = $index
        }
    }
}

foreach ($required in $RequiredEmbed) {
    $matching = @($markers | Where-Object { [string]::Equals($_.Path, $required, [StringComparison]::Ordinal) })

    if ($matching.Count -eq 0) {
        throw "$DocumentPath carries no '<!-- embed: $required -->' marker. Every required file must be embedded exactly once."
    }
    if ($matching.Count -gt 1) {
        $lines = ($matching | ForEach-Object { $_.Line + 1 }) -join ", "
        throw "$DocumentPath carries $($matching.Count) '<!-- embed: $required -->' markers, on lines $lines. A file is embedded exactly once, so that one block is unambiguously the copy."
    }
}

if ($markers.Count -eq 0) {
    throw "$DocumentPath carries no embed markers at all."
}

foreach ($marker in $markers) {
    $markerLineNumber = $marker.Line + 1
    $sourcePath = Join-Path $RepositoryRoot $marker.Path

    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "$DocumentPath line ${markerLineNumber} embeds '$($marker.Path)', which does not exist under $RepositoryRoot."
    }

    $openIndex = $marker.Line + 1
    if ($openIndex -ge $documentLines.Count -or $documentLines[$openIndex] -notmatch $fenceOpenPattern) {
        throw "$DocumentPath line ${markerLineNumber} embeds '$($marker.Path)' but no fenced block opens on the next line."
    }

    $closeIndex = -1
    for ($index = $openIndex + 1; $index -lt $documentLines.Count; $index++) {
        if ([string]::Equals($documentLines[$index], '```', [StringComparison]::Ordinal)) {
            $closeIndex = $index
            break
        }
    }

    if ($closeIndex -lt 0) {
        throw "$DocumentPath line ${markerLineNumber} embeds '$($marker.Path)' in a fenced block that is never closed."
    }

    # An empty block is spelled out rather than sliced. PowerShell's range operator counts down when
    # its start exceeds its end, so slicing an empty block would silently yield a reversed line.
    $embedded = if ($closeIndex -eq $openIndex + 1) {
        ""
    }
    else {
        ($documentLines[($openIndex + 1)..($closeIndex - 1)] -join "`n").TrimEnd()
    }
    $source = (Read-NormalizedText -Path $sourcePath).TrimEnd()

    if (-not [string]::Equals($embedded, $source, [StringComparison]::Ordinal)) {
        $embeddedLines = $embedded -split "`n"
        $sourceLines = $source -split "`n"
        $firstDifference = $null

        for ($index = 0; $index -lt [Math]::Max($embeddedLines.Count, $sourceLines.Count); $index++) {
            $left = if ($index -lt $embeddedLines.Count) { $embeddedLines[$index] } else { '<end of block>' }
            $right = if ($index -lt $sourceLines.Count) { $sourceLines[$index] } else { '<end of file>' }

            if (-not [string]::Equals($left, $right, [StringComparison]::Ordinal)) {
                $firstDifference = "line $($index + 1) of the block: document has '$left', file has '$right'"
                break
            }
        }

        throw (
            "$DocumentPath no longer embeds '$($marker.Path)' verbatim. The file is the artifact and " +
            "the document is the copy, so update the document to match. First difference at $firstDifference."
        )
    }
}

Write-Output "Verified $([System.IO.Path]::GetFileName($DocumentPath)): $($markers.Count) embedded block(s) match their files, including $($RequiredEmbed.Count) required."
