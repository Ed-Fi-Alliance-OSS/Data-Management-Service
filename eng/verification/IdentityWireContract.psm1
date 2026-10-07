# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.DESCRIPTION
The identity wire-contract gate: compares the normalized document the host serves at the gated
commit against the baseline of the last published identity contract version.

The anchor is the baseline file as it existed at the commit a published package records in its
nuspec (/package/metadata/repository/@commit). It is deliberately not the golden checked out at the
gated commit: comparing a change against the very file the change edited would pass every edit.

Decision, by what the feed says about the package id:

  - The id is absent (404 from a feed whose service index answered): no identity contract has been
    published, so the served document is compared with the baseline committed at HEAD, which is the
    reviewed initial baseline. Outcome: initial.
  - The id is listed: the highest published version that is not greater than the contract version is
    the anchor version. If every published version is greater, the contract version went backwards
    and the gate fails. The anchor version's nupkg is downloaded and its recorded commit read.
      - Equal versions: the served document must equal the baseline at the anchor commit, byte for
        byte after LF normalization. Outcome: unchanged.
      - A greater contract version: the baseline at the anchor commit and the served document go to
        the compatibility review, and the published and packed packages are compared by what the
        same-version publish comparison covers: the provider surface, the XML documentation and the
        declared dependencies. Hard failures (see Get-WireDifferenceVerdict and
        Get-SurfaceAdditionVerdict) can never be waived; every other difference needs an exact entry
        in eng/verification/IdentityWireCompatibility/<published>-to-<current>.json, and an entry
        matching nothing is stale and fails. Outcome: increment-reviewed.

Every unusable anchor fails with a message naming which part was unusable, and none falls back to
the current golden.

Feed access is three injected script blocks with the shapes Invoke-ContractPublishCheck.ps1 uses, so
every branch is testable without a network. The defaults perform the real requests, and a 404 from
the package index means "absent" only when the service index answered.

Nothing here contacts a feed except through those blocks, and nothing here pushes.
#>

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "ContractPackageComparison.psm1") -Force
# Not -Force: a reload would discard the HTTP mocks the suites install on this module.
Import-Module (Join-Path $PSScriptRoot "ContractFeed.psm1")

<#
.DESCRIPTION
Runs git against a repository and returns the exit code, stdout and stderr, so a caller reads the
exit code from the command itself. Stdout is read as UTF-8 without any newline translation.
#>
function Invoke-GitCommand {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string[]] $Arguments
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new("git")
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false
    $startInfo.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
    $startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0"

    foreach ($argument in @("-C", $RepositoryRoot, "-c", "core.autocrlf=false") + $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()

    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Stdout   = $stdout.GetAwaiter().GetResult()
        Stderr   = $stderr.GetAwaiter().GetResult().Trim()
    }
}

function ConvertTo-LfText {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Text)

    return $Text.Replace("`r`n", "`n")
}

<#
.DESCRIPTION
Reads the commit a published package records in /package/metadata/repository/@commit, failing with
a message that names which part of the anchor is unusable.
#>
function Get-PackageAnchorCommit {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string] $PackagePath,
        [Parameter(Mandatory)][string] $Description
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)

    try {
        $entries = @($archive.Entries | Where-Object { $_.FullName -notlike "*/*" -and $_.FullName -like "*.nuspec" })

        if ($entries.Count -ne 1) {
            throw "The $Description package carries $($entries.Count) nuspec file(s); exactly one is required to read its anchor commit."
        }

        $reader = [System.IO.StreamReader]::new($entries[0].Open())

        try {
            $nuspecText = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    $nuspec = [xml] $nuspecText
    $repository = $nuspec.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='repository']")

    if ($null -eq $repository) {
        throw "The $Description package's nuspec has no repository element, so it records no commit to anchor the baseline to."
    }

    $commit = $repository.GetAttribute("commit")

    if ([string]::IsNullOrWhiteSpace($commit)) {
        throw "The $Description package's nuspec repository element has no commit attribute, so it records no commit to anchor the baseline to."
    }

    if ($commit -notmatch '^[0-9a-fA-F]{40}$') {
        throw "The $Description package's nuspec records commit '$commit', which is not a 40 character hexadecimal commit id."
    }

    return $commit.ToLowerInvariant()
}

function Get-JsonDocumentText {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string] $Text,
        [Parameter(Mandatory)][string] $Description
    )

    try {
        $null = ConvertFrom-Json -InputObject $Text -Depth 100
    }
    catch {
        throw "The $Description is not parseable JSON: $($_.Exception.Message)"
    }

    return (ConvertTo-LfText -Text $Text)
}

function Write-UnifiedDiff {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $BaselineText,
        [Parameter(Mandatory)][string] $ServedText
    )

    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) "identity-wire-diff-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null

    try {
        $baselineFile = Join-Path $scratch "baseline.json"
        $servedFile = Join-Path $scratch "served.json"
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllText($baselineFile, $BaselineText, $utf8)
        [System.IO.File]::WriteAllText($servedFile, $ServedText, $utf8)

        $diff = Invoke-GitCommand -RepositoryRoot $RepositoryRoot -Arguments @("diff", "--no-index", "--no-color", "--", $baselineFile, $servedFile)

        # git diff --no-index exits 1 when the files differ, and anything above that is a failure.
        if ($diff.ExitCode -gt 1) {
            return "(the diff could not be produced: $($diff.Stderr))"
        }

        return $diff.Stdout
    }
    finally {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}

function Assert-ServedEqualsBaseline {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [Parameter(Mandatory)][string] $BaselineText,
        [Parameter(Mandatory)][string] $ServedText,
        [Parameter(Mandatory)][string] $Description
    )

    if ([string]::Equals($BaselineText, $ServedText, [System.StringComparison]::Ordinal)) {
        return
    }

    $diff = Write-UnifiedDiff -RepositoryRoot $RepositoryRoot -BaselineText $BaselineText -ServedText $ServedText

    throw (
        "The identity wire contract the host serves differs from $Description." + [Environment]::NewLine +
        "A published contract version is immutable. Revert the change, or increment the contract version so the change is reviewed for compatibility." +
        [Environment]::NewLine + $diff
    )
}

# The compatibility review of a version increment. Everything below works on System.Text.Json nodes
# rather than PowerShell hashtables: PowerShell hashtables compare keys case-insensitively, and the
# document has properties that differ only in case.

$script:HttpMethods = @("get", "put", "post", "delete", "options", "head", "patch", "trace")
$script:DocumentationKeywords = @("description", "summary", "example", "examples")
$script:ChildSchemaKeywords = @("items", "additionalProperties", "unevaluatedProperties", "not", "if", "then", "else", "contains", "propertyNames")
$script:NamedSchemaKeywords = @("properties", "patternProperties", "$defs", "definitions", "dependentSchemas")
$script:ListedSchemaKeywords = @("allOf", "anyOf", "oneOf", "prefixItems")
$script:VersionStampKey = "x-edfi-identity-contract-version"

function ConvertTo-JsonPointer {
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Segment)

    if ($Segment.Count -eq 0) {
        return "/"
    }

    return "/" + (($Segment | ForEach-Object { $_.Replace("~", "~0").Replace("/", "~1") }) -join "/")
}

function Get-PointerTail {
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Segment,
        [Parameter(Mandatory)][int] $Skip
    )

    return , [string[]] @($Segment | Select-Object -Skip $Skip)
}

function Test-JsonNodeEqual {
    [CmdletBinding()]
    [OutputType([bool])]
    param($Left, $Right)

    if ($null -eq $Left -or $null -eq $Right) {
        return ($null -eq $Left -and $null -eq $Right)
    }

    if ($Left -isnot [System.Text.Json.Nodes.JsonValue] -or $Right -isnot [System.Text.Json.Nodes.JsonValue]) {
        return $false
    }

    return [string]::Equals($Left.ToJsonString(), $Right.ToJsonString(), [System.StringComparison]::Ordinal)
}

<#
.DESCRIPTION
Collects one difference per JSON pointer between two documents: objects are compared by key and
arrays by index. A difference is added, removed or changed; a container is never reported as a whole
unless one side lacks it or the two sides have different JSON kinds.
#>
function Find-JsonDifference {
    [CmdletBinding()]
    [OutputType([void])]
    param(
        $Old,
        $New,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Path,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[object]] $Sink
    )

    $oldObject = $Old -is [System.Text.Json.Nodes.JsonObject]
    $newObject = $New -is [System.Text.Json.Nodes.JsonObject]
    $oldArray = $Old -is [System.Text.Json.Nodes.JsonArray]
    $newArray = $New -is [System.Text.Json.Nodes.JsonArray]

    if ($oldObject -and $newObject) {
        $keys = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)

        foreach ($pair in $Old.GetEnumerator()) { $null = $keys.Add($pair.Key) }
        foreach ($pair in $New.GetEnumerator()) { $null = $keys.Add($pair.Key) }

        foreach ($key in $keys) {
            $childPath = [string[]] ($Path + $key)
            $inOld = $Old.ContainsKey($key)
            $inNew = $New.ContainsKey($key)

            if ($inOld -and $inNew) {
                Find-JsonDifference -Old $Old[$key] -New $New[$key] -Path $childPath -Sink $Sink
            }
            elseif ($inNew) {
                $Sink.Add([pscustomobject]@{ Segments = $childPath; Change = "added"; Old = $null; New = $New[$key] })
            }
            else {
                $Sink.Add([pscustomobject]@{ Segments = $childPath; Change = "removed"; Old = $Old[$key]; New = $null })
            }
        }

        return
    }

    if ($oldArray -and $newArray) {
        $count = [Math]::Max($Old.Count, $New.Count)

        for ($index = 0; $index -lt $count; $index++) {
            $childPath = [string[]] ($Path + [string] $index)

            if ($index -ge $Old.Count) {
                $Sink.Add([pscustomobject]@{ Segments = $childPath; Change = "added"; Old = $null; New = $New[$index] })
            }
            elseif ($index -ge $New.Count) {
                $Sink.Add([pscustomobject]@{ Segments = $childPath; Change = "removed"; Old = $Old[$index]; New = $null })
            }
            else {
                Find-JsonDifference -Old $Old[$index] -New $New[$index] -Path $childPath -Sink $Sink
            }
        }

        return
    }

    if (-not (Test-JsonNodeEqual -Left $Old -Right $New)) {
        $Sink.Add([pscustomobject]@{ Segments = $Path; Change = "changed"; Old = $Old; New = $New })
    }
}

<#
.DESCRIPTION
The component a $ref reaches, as its decoded group and name, or $null when the reference is not a
local pointer into #/components/<group>/<name>. Whatever follows those two tokens, the reference
reaches the whole component, which is the conservative reading: everything the component holds
counts as referenced.

A $ref is a URI, so its fragment is percent-decoded before it is read as a JSON pointer, and each
pointer token is then decoded ~1 first and ~0 second (RFC 6901, sections 4 and 6).
#>
function ConvertFrom-ComponentReference {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Reference)

    if (-not $Reference.StartsWith("#", [System.StringComparison]::Ordinal)) {
        return $null
    }

    $pointer = [System.Uri]::UnescapeDataString($Reference.Substring(1))
    $match = [regex]::Match($pointer, '^/components/([^/]+)/([^/]+)(/.*)?$')

    if (-not $match.Success) {
        return $null
    }

    return [pscustomobject]@{
        Group = $match.Groups[1].Value.Replace("~1", "/").Replace("~0", "~")
        Name  = $match.Groups[2].Value.Replace("~1", "/").Replace("~0", "~")
    }
}

function Find-ComponentReference {
    [CmdletBinding()]
    [OutputType([void])]
    param(
        $Node,
        $Document,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.HashSet[string]] $Seen
    )

    if ($Node -is [System.Text.Json.Nodes.JsonArray]) {
        foreach ($item in $Node) {
            Find-ComponentReference -Node $item -Document $Document -Seen $Seen
        }

        return
    }

    if ($Node -isnot [System.Text.Json.Nodes.JsonObject]) {
        return
    }

    foreach ($pair in $Node.GetEnumerator()) {
        $isReference = [string]::Equals($pair.Key, '$ref', [System.StringComparison]::Ordinal) -and
            $pair.Value -is [System.Text.Json.Nodes.JsonValue] -and
            $pair.Value.GetValueKind() -eq [System.Text.Json.JsonValueKind]::String

        if (-not $isReference) {
            Find-ComponentReference -Node $pair.Value -Document $Document -Seen $Seen
            continue
        }

        $component = ConvertFrom-ComponentReference -Reference ($pair.Value.GetValue[string]())

        # Find-UnsupportedReference fails the gate on any other form.
        if ($null -eq $component) {
            continue
        }

        $key = "$($component.Group)/$($component.Name)"

        if ($Seen.Add($key)) {
            # Explicit assignments: a JsonObject returned from an if expression would be enumerated.
            $target = $null
            $components = $Document["components"]

            if ($components -is [System.Text.Json.Nodes.JsonObject]) {
                $group = $components[$component.Group]

                if ($group -is [System.Text.Json.Nodes.JsonObject]) {
                    $target = $group[$component.Name]
                }
            }

            Find-ComponentReference -Node $target -Document $Document -Seen $Seen
        }
    }
}

<#
.DESCRIPTION
Collects the path of every $ref in the document that ConvertFrom-ComponentReference cannot read.
The gate classifies a change by the components the operations reach, so a reference it cannot
follow would leave what it points at read as unreferenced, and a breaking change there as one a
review record could approve.
#>
function Find-UnsupportedReference {
    [CmdletBinding()]
    [OutputType([void])]
    param(
        $Node,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Path,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[object]] $Sink
    )

    if ($Node -is [System.Text.Json.Nodes.JsonArray]) {
        for ($index = 0; $index -lt $Node.Count; $index++) {
            Find-UnsupportedReference -Node $Node[$index] -Path ([string[]] ($Path + [string] $index)) -Sink $Sink
        }

        return
    }

    if ($Node -isnot [System.Text.Json.Nodes.JsonObject]) {
        return
    }

    foreach ($pair in $Node.GetEnumerator()) {
        $childPath = [string[]] ($Path + $pair.Key)
        $isReference = [string]::Equals($pair.Key, '$ref', [System.StringComparison]::Ordinal) -and
            $pair.Value -is [System.Text.Json.Nodes.JsonValue] -and
            $pair.Value.GetValueKind() -eq [System.Text.Json.JsonValueKind]::String

        if (-not $isReference) {
            Find-UnsupportedReference -Node $pair.Value -Path $childPath -Sink $Sink
            continue
        }

        if ($null -eq (ConvertFrom-ComponentReference -Reference ($pair.Value.GetValue[string]()))) {
            $Sink.Add($childPath)
        }
    }
}

function Find-ProblemTypeValue {
    [CmdletBinding()]
    [OutputType([void])]
    param(
        $Node,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.HashSet[string]] $Seen
    )

    if ($Node -is [System.Text.Json.Nodes.JsonArray]) {
        foreach ($item in $Node) {
            Find-ProblemTypeValue -Node $item -Seen $Seen
        }
    }
    elseif ($Node -is [System.Text.Json.Nodes.JsonObject]) {
        foreach ($pair in $Node.GetEnumerator()) {
            $text = Get-JsonStringValue -Node $pair.Value

            if ([string]::Equals($pair.Key, "type", [System.StringComparison]::Ordinal) -and $null -ne $text -and $text.StartsWith("urn:", [System.StringComparison]::Ordinal)) {
                $null = $Seen.Add($text)
            }

            Find-ProblemTypeValue -Node $pair.Value -Seen $Seen
        }
    }
}

function Get-JsonStringValue {
    [CmdletBinding()]
    [OutputType([string])]
    param($Node)

    if ($Node -is [System.Text.Json.Nodes.JsonValue] -and $Node.GetValueKind() -eq [System.Text.Json.JsonValueKind]::String) {
        return $Node.GetValue[string]()
    }

    return $null
}

<#
.DESCRIPTION
Builds what the classifier needs to know about the two documents: the component entries reachable
by $ref from the request side or the response side of an operation present in both documents, and
the problem type URNs each document carries. Components reachable only from a new operation, or from
nothing, are not part of an existing operation's contract.
#>
function Get-WireClassificationContext {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)] $OldDocument, [Parameter(Mandatory)] $NewDocument)

    $request = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $response = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

    $oldPaths = $OldDocument["paths"]
    $newPaths = $NewDocument["paths"]

    if ($oldPaths -is [System.Text.Json.Nodes.JsonObject] -and $newPaths -is [System.Text.Json.Nodes.JsonObject]) {
        foreach ($pathPair in $oldPaths.GetEnumerator()) {
            if (-not $newPaths.ContainsKey($pathPair.Key)) {
                continue
            }

            foreach ($side in @(@{ Item = $pathPair.Value; Document = $OldDocument }, @{ Item = $newPaths[$pathPair.Key]; Document = $NewDocument })) {
                $item = $side.Item

                if ($item -isnot [System.Text.Json.Nodes.JsonObject]) {
                    continue
                }

                Find-ComponentReference -Node $item["parameters"] -Document $side.Document -Seen $request

                foreach ($method in $script:HttpMethods) {
                    $inBoth = $pathPair.Value -is [System.Text.Json.Nodes.JsonObject] -and
                        $newPaths[$pathPair.Key] -is [System.Text.Json.Nodes.JsonObject] -and
                        $pathPair.Value.ContainsKey($method) -and $newPaths[$pathPair.Key].ContainsKey($method)

                    if (-not $inBoth -or $item[$method] -isnot [System.Text.Json.Nodes.JsonObject]) {
                        continue
                    }

                    Find-ComponentReference -Node $item[$method]["requestBody"] -Document $side.Document -Seen $request
                    Find-ComponentReference -Node $item[$method]["parameters"] -Document $side.Document -Seen $request
                    Find-ComponentReference -Node $item[$method]["responses"] -Document $side.Document -Seen $response
                }
            }
        }
    }

    $oldTypes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $newTypes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    Find-ProblemTypeValue -Node $OldDocument -Seen $oldTypes
    Find-ProblemTypeValue -Node $NewDocument -Seen $newTypes

    return [pscustomobject]@{ Request = $request; Response = $response; OldTypes = $oldTypes; NewTypes = $newTypes }
}

function Get-Verdict {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][ValidateSet("hard", "review")][string] $Class,
        [Parameter(Mandatory)][string] $Category
    )

    return [pscustomobject]@{ Class = $Class; Category = $Category }
}

# Keyword-aware walk of a schema: a pointer that lands on documentation or an x- extension needs a
# review record, and a pointer that lands on anything else is a constraint change.
function Get-SchemaVerdict {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Rest,
        [Parameter(Mandatory)][string] $Side
    )

    $hard = Get-Verdict -Class hard -Category "$Side-schema"

    if ($Rest.Count -eq 0) {
        return $hard
    }

    $keyword = $Rest[0]

    if ($script:DocumentationKeywords -ccontains $keyword -or $keyword.StartsWith("x-", [System.StringComparison]::Ordinal)) {
        return Get-Verdict -Class review -Category "documentation"
    }

    if ($script:NamedSchemaKeywords -ccontains $keyword -or $script:ListedSchemaKeywords -ccontains $keyword) {
        if ($Rest.Count -lt 2) {
            return $hard
        }

        return Get-SchemaVerdict -Rest (Get-PointerTail -Segment $Rest -Skip 2) -Side $Side
    }

    if ($script:ChildSchemaKeywords -ccontains $keyword) {
        if ($Rest.Count -lt 2) {
            return $hard
        }

        return Get-SchemaVerdict -Rest (Get-PointerTail -Segment $Rest -Skip 1) -Side $Side
    }

    return $hard
}

function Get-ProblemTypeVerdict {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)] $Difference,
        [Parameter(Mandatory)] $Context,
        [Parameter(Mandatory)][ValidateSet("example", "type")][string] $Level
    )

    if ($Level -eq "example") {
        $oldType = if ($Difference.Old -is [System.Text.Json.Nodes.JsonObject] -and $Difference.Old["value"] -is [System.Text.Json.Nodes.JsonObject]) { Get-JsonStringValue -Node $Difference.Old["value"]["type"] } else { $null }
        $newType = if ($Difference.New -is [System.Text.Json.Nodes.JsonObject] -and $Difference.New["value"] -is [System.Text.Json.Nodes.JsonObject]) { Get-JsonStringValue -Node $Difference.New["value"]["type"] } else { $null }
    }
    else {
        $oldType = Get-JsonStringValue -Node $Difference.Old
        $newType = Get-JsonStringValue -Node $Difference.New
    }

    $added = $null -ne $newType -and -not $Context.OldTypes.Contains($newType)
    $removed = $null -ne $oldType -and -not $Context.NewTypes.Contains($oldType)

    if ($added -or $removed) {
        return Get-Verdict -Class hard -Category "response-problem-type"
    }

    return Get-Verdict -Class review -Category "documentation"
}

function Get-MediaTypeVerdict {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Rest,
        [Parameter(Mandatory)][string] $Side,
        [Parameter(Mandatory)] $Difference,
        [Parameter(Mandatory)] $Context
    )

    if ($Rest.Count -eq 0) {
        return Get-Verdict -Class hard -Category "$Side-media-type"
    }

    switch -CaseSensitive ($Rest[0]) {
        "schema" {
            return Get-SchemaVerdict -Rest (Get-PointerTail -Segment $Rest -Skip 1) -Side $Side
        }
        "example" {
            if ($Side -eq "response" -and $Rest.Count -eq 2 -and $Rest[1] -ceq "type") {
                return Get-ProblemTypeVerdict -Difference $Difference -Context $Context -Level type
            }

            return Get-Verdict -Class review -Category "documentation"
        }
        "examples" {
            if ($Side -eq "response" -and $Rest.Count -eq 2) {
                return Get-ProblemTypeVerdict -Difference $Difference -Context $Context -Level example
            }

            if ($Side -eq "response" -and $Rest.Count -eq 4 -and $Rest[2] -ceq "value" -and $Rest[3] -ceq "type") {
                return Get-ProblemTypeVerdict -Difference $Difference -Context $Context -Level type
            }

            return Get-Verdict -Class review -Category "documentation"
        }
        default {
            return Get-Verdict -Class hard -Category "$Side-media-type"
        }
    }
}

function Get-ContentVerdict {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Rest,
        [Parameter(Mandatory)][string] $Side,
        [Parameter(Mandatory)] $Difference,
        [Parameter(Mandatory)] $Context
    )

    if ($Rest.Count -le 1) {
        return Get-Verdict -Class hard -Category "$Side-media-type"
    }

    return Get-MediaTypeVerdict -Rest (Get-PointerTail -Segment $Rest -Skip 1) -Side $Side -Difference $Difference -Context $Context
}

function Get-HeaderVerdict {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Rest)

    if ($Rest.Count -gt 0) {
        if ($script:DocumentationKeywords -ccontains $Rest[0]) {
            return Get-Verdict -Class review -Category "documentation"
        }

        if ($Rest[0] -ceq "schema") {
            return Get-SchemaVerdict -Rest (Get-PointerTail -Segment $Rest -Skip 1) -Side response
        }
    }

    return Get-Verdict -Class hard -Category "response-header"
}

function Get-ResponseVerdict {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Rest,
        [Parameter(Mandatory)] $Difference,
        [Parameter(Mandatory)] $Context
    )

    if ($Rest.Count -eq 0) {
        return Get-Verdict -Class hard -Category "response-status-code"
    }

    switch -CaseSensitive ($Rest[0]) {
        "description" {
            return Get-Verdict -Class review -Category "documentation"
        }
        "content" {
            return Get-ContentVerdict -Rest (Get-PointerTail -Segment $Rest -Skip 1) -Side response -Difference $Difference -Context $Context
        }
        "headers" {
            if ($Rest.Count -le 2) {
                return Get-Verdict -Class hard -Category "response-header"
            }

            return Get-HeaderVerdict -Rest (Get-PointerTail -Segment $Rest -Skip 2)
        }
        default {
            return Get-Verdict -Class hard -Category "response-status-code"
        }
    }
}

function Get-ParameterVerdict {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Rest,
        [Parameter(Mandatory)] $Difference,
        [Parameter(Mandatory)] $Context
    )

    if ($Rest.Count -gt 0) {
        if ($script:DocumentationKeywords -ccontains $Rest[0]) {
            return Get-Verdict -Class review -Category "documentation"
        }

        if ($Rest[0] -ceq "schema") {
            return Get-SchemaVerdict -Rest (Get-PointerTail -Segment $Rest -Skip 1) -Side request
        }

        if ($Rest[0] -ceq "content") {
            return Get-ContentVerdict -Rest (Get-PointerTail -Segment $Rest -Skip 1) -Side request -Difference $Difference -Context $Context
        }
    }

    return Get-Verdict -Class hard -Category "request-parameter"
}

function Get-RequestBodyVerdict {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Rest,
        [Parameter(Mandatory)] $Difference,
        [Parameter(Mandatory)] $Context
    )

    if ($Rest.Count -gt 0) {
        if ($Rest[0] -ceq "description") {
            return Get-Verdict -Class review -Category "documentation"
        }

        if ($Rest[0] -ceq "content") {
            return Get-ContentVerdict -Rest (Get-PointerTail -Segment $Rest -Skip 1) -Side request -Difference $Difference -Context $Context
        }
    }

    return Get-Verdict -Class hard -Category "request-body"
}

function Get-OperationVerdict {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Rest,
        [Parameter(Mandatory)] $Difference,
        [Parameter(Mandatory)] $Context
    )

    switch -CaseSensitive ($Rest[0]) {
        "requestBody" {
            return Get-RequestBodyVerdict -Rest (Get-PointerTail -Segment $Rest -Skip 1) -Difference $Difference -Context $Context
        }
        "parameters" {
            if ($Rest.Count -le 2) {
                return Get-Verdict -Class hard -Category "request-parameter"
            }

            return Get-ParameterVerdict -Rest (Get-PointerTail -Segment $Rest -Skip 2) -Difference $Difference -Context $Context
        }
        "responses" {
            if ($Rest.Count -le 1) {
                return Get-Verdict -Class hard -Category "response-status-code"
            }

            return Get-ResponseVerdict -Rest (Get-PointerTail -Segment $Rest -Skip 2) -Difference $Difference -Context $Context
        }
        { $_ -ceq "security" -or $_ -ceq "callbacks" } {
            return Get-Verdict -Class hard -Category "security"
        }
        default {
            return Get-Verdict -Class review -Category "documentation"
        }
    }
}

function Get-ComponentVerdict {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Rest,
        [Parameter(Mandatory)] $Difference,
        [Parameter(Mandatory)] $Context
    )

    if ($Rest.Count -eq 0) {
        return Get-Verdict -Class hard -Category "unclassified"
    }

    $group = $Rest[0]

    if ($group -ceq "securitySchemes") {
        return Get-Verdict -Class hard -Category "security"
    }

    if ($Rest.Count -eq 1) {
        if ($Difference.Change -eq "added") {
            return Get-Verdict -Class review -Category "new-component-group"
        }

        return Get-Verdict -Class hard -Category "unclassified"
    }

    if ($group -ceq "examples") {
        return Get-Verdict -Class review -Category "documentation"
    }

    if (@("schemas", "responses", "headers", "parameters", "requestBodies") -cnotcontains $group) {
        return Get-Verdict -Class hard -Category "unclassified"
    }

    $key = "$group/$($Rest[1])"
    $inRequest = $Context.Request.Contains($key)
    $inResponse = $Context.Response.Contains($key)

    if (-not $inRequest -and -not $inResponse) {
        return Get-Verdict -Class review -Category "unreferenced-component"
    }

    $side = if ($inRequest -and $inResponse) { "request+response" } elseif ($inRequest) { "request" } else { "response" }
    $tail = Get-PointerTail -Segment $Rest -Skip 2

    switch -CaseSensitive ($group) {
        "schemas" { return Get-SchemaVerdict -Rest $tail -Side $side }
        "responses" { return Get-ResponseVerdict -Rest $tail -Difference $Difference -Context $Context }
        "headers" { return Get-HeaderVerdict -Rest $tail }
        "parameters" { return Get-ParameterVerdict -Rest $tail -Difference $Difference -Context $Context }
        default { return Get-RequestBodyVerdict -Rest $tail -Difference $Difference -Context $Context }
    }
}

<#
.DESCRIPTION
Classifies one difference by where its pointer lands: hard (never waivable) or review (needs a
review record entry matching its pointer and change). Anything the rules do not recognize fails
closed, as hard or as review, never as a pass.
#>
function Get-WireDifferenceVerdict {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)] $Difference,
        [Parameter(Mandatory)] $Context
    )

    $segments = [string[]] $Difference.Segments
    $root = $segments[0]

    switch -CaseSensitive ($root) {
        "paths" {
            if ($segments.Count -eq 1) {
                return Get-Verdict -Class hard -Category "removed-path"
            }

            if ($segments.Count -eq 2) {
                if ($Difference.Change -eq "added") {
                    return Get-Verdict -Class review -Category "new-path"
                }

                return Get-Verdict -Class hard -Category "removed-path"
            }

            $member = $segments[2]

            if ($member -ceq "parameters") {
                if ($segments.Count -le 4) {
                    return Get-Verdict -Class hard -Category "request-parameter"
                }

                return Get-ParameterVerdict -Rest (Get-PointerTail -Segment $segments -Skip 4) -Difference $Difference -Context $Context
            }

            if ($script:HttpMethods -ccontains $member) {
                if ($segments.Count -eq 3) {
                    if ($Difference.Change -eq "added") {
                        return Get-Verdict -Class review -Category "new-operation"
                    }

                    return Get-Verdict -Class hard -Category "removed-operation"
                }

                return Get-OperationVerdict -Rest (Get-PointerTail -Segment $segments -Skip 3) -Difference $Difference -Context $Context
            }

            return Get-Verdict -Class review -Category "documentation"
        }
        "components" {
            return Get-ComponentVerdict -Rest (Get-PointerTail -Segment $segments -Skip 1) -Difference $Difference -Context $Context
        }
        "security" {
            return Get-Verdict -Class hard -Category "security"
        }
        default {
            return Get-Verdict -Class review -Category "documentation"
        }
    }
}

<#
.DESCRIPTION
Diffs the published baseline against the served document and classifies every difference. The
version stamp the host adds to the served document is not a difference. A $ref in the served
document that the gate cannot follow is a hard failure whether or not it changed, because nothing
behind it can be classified.
#>
function Get-WireCompatibilityFinding {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $BaselineText,
        [Parameter(Mandatory)][string] $ServedText
    )

    $old = [System.Text.Json.Nodes.JsonNode]::Parse($BaselineText)
    $new = [System.Text.Json.Nodes.JsonNode]::Parse($ServedText)

    if ($old -isnot [System.Text.Json.Nodes.JsonObject] -or $new -isnot [System.Text.Json.Nodes.JsonObject]) {
        throw "The baseline and the served document must both be JSON objects to be compared for compatibility."
    }

    $sink = [System.Collections.Generic.List[object]]::new()
    Find-JsonDifference -Old $old -New $new -Path ([string[]] @()) -Sink $sink

    $context = Get-WireClassificationContext -OldDocument $old -NewDocument $new
    $findings = [System.Collections.Generic.List[pscustomobject]]::new()

    foreach ($difference in $sink) {
        if ($difference.Segments.Count -eq 1 -and [string]::Equals($difference.Segments[0], $script:VersionStampKey, [System.StringComparison]::Ordinal)) {
            continue
        }

        $verdict = Get-WireDifferenceVerdict -Difference $difference -Context $context

        $findings.Add([pscustomobject]@{
                Pointer  = ConvertTo-JsonPointer -Segment $difference.Segments
                Change   = $difference.Change
                Class    = $verdict.Class
                Category = $verdict.Category
            })
    }

    $unsupported = [System.Collections.Generic.List[object]]::new()
    Find-UnsupportedReference -Node $new -Path ([string[]] @()) -Sink $unsupported

    foreach ($segments in $unsupported) {
        $findings.Add([pscustomobject]@{
                Pointer  = ConvertTo-JsonPointer -Segment $segments
                Change   = "present"
                Class    = "hard"
                Category = "unsupported-reference"
            })
    }

    return $findings.ToArray()
}

<#
.DESCRIPTION
Extracts the one entry of a package that $Match selects to the work directory and returns its path.
Exactly one is required: a missing entry is not a contract state that could compare equal to
another.
#>
function Save-PackageEntry {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][System.IO.Compression.ZipArchive] $Archive,
        [Parameter(Mandatory)][scriptblock] $Match,
        [Parameter(Mandatory)][string] $EntryDescription,
        [Parameter(Mandatory)][string] $Extension,
        [Parameter(Mandatory)][string] $Description,
        [Parameter(Mandatory)][string] $WorkDirectory
    )

    $entry = @($Archive.Entries | Where-Object $Match)

    if ($entry.Count -ne 1) {
        throw "The $Description package carries $($entry.Count) $EntryDescription entries; exactly one is required to compare the package."
    }

    $path = Join-Path $WorkDirectory "$([guid]::NewGuid().ToString('N'))$Extension"
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry[0], $path)

    return $path
}

<#
.DESCRIPTION
Reads what a version increment compares from one package, each in the canonical form the
same-version publish comparison uses: the provider surface of the contract assembly, the XML
documentation beside it, and the dependencies the nuspec declares.
#>
function Get-PackageContract {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $PackagePath,
        [Parameter(Mandatory)][string] $Description,
        [Parameter(Mandatory)][string] $WorkDirectory
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $assemblyEntry = "lib/net10.0/EdFi.DataManagementService.Identity.dll"
    $documentationEntry = "lib/net10.0/EdFi.DataManagementService.Identity.xml"

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        throw "The $Description package was not found: $PackagePath"
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)

    try {
        $common = @{ Archive = $archive; Description = $Description; WorkDirectory = $WorkDirectory }
        $assemblyPath = Save-PackageEntry @common -Match { $_.FullName -ceq $assemblyEntry } -EntryDescription $assemblyEntry -Extension ".dll"
        $documentationPath = Save-PackageEntry @common -Match { $_.FullName -ceq $documentationEntry } -EntryDescription $documentationEntry -Extension ".xml"
        $nuspecPath = Save-PackageEntry @common -Match { $_.FullName -notlike "*/*" -and $_.FullName -like "*.nuspec" } -EntryDescription "root .nuspec" -Extension ".nuspec"
    }
    finally {
        $archive.Dispose()
    }

    # Not wrapped in @(): both canonicalizers return their array as one object, and wrapping it would
    # make a list of one array, which [string[]] would then join into a single line.
    return [pscustomobject]@{
        Surface       = [string[]] @(& (Join-Path $PSScriptRoot "Get-ContractPublicSurface.ps1") -AssemblyPath $assemblyPath)
        Documentation = ConvertTo-CanonicalXmlDocumentation -Path $documentationPath
        Dependencies  = ConvertTo-CanonicalDependencySet -NuspecPath $nuspecPath
    }
}

<#
.DESCRIPTION
Classifies one line the packed provider surface adds. $PackedTypes is ordered longest first, so a
member is matched to its own type and never to a shorter name that merely prefixes it.

  - A member or CLOSURE line of a type the published surface does not have needs a review record.
    No existing provider implements, derives from or constructs a type that did not exist, so
    nothing on it is an obligation on one. An existing type that starts implementing a new interface
    changes its own TYPE line, which fails as a changed line.
  - On an interface that already existed, an abstract member is one every existing provider would
    have to implement, so it fails; a member with a default implementation is inherited by existing
    providers, so it needs a review record like any other addition.
  - On any other existing type, a required property or field fails, an abstract member fails, and so
    does a CLOSURE line, which means a private protected abstract member now shuts out every
    external deriver.
  - Any other added type or member needs a review record.
  - A line of a kind the surface reader does not emit, or a member whose type is not in the packed
    surface, fails: the gate cannot say what it is.
#>
function Get-SurfaceAdditionVerdict {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $Line,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.HashSet[string]] $PublishedTypes,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.HashSet[string]] $PublishedInterfaces,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $PackedTypes
    )

    $kind, $rest = $Line -split " ", 2

    if ($kind -ceq "TYPE") {
        return Get-Verdict -Class review -Category "surface-addition"
    }

    if ($kind -ceq "CLOSURE") {
        $name = ([string] $rest -split " ", 2)[0]
        $owner = $PackedTypes | Where-Object { $_ -ceq $name } | Select-Object -First 1
    }
    elseif (@("METHOD", "PROPERTY", "FIELD", "EVENT") -ccontains $kind) {
        $owner = $PackedTypes | Where-Object { ([string] $rest).StartsWith("$_.", [System.StringComparison]::Ordinal) } | Select-Object -First 1
    }
    else {
        return Get-Verdict -Class hard -Category "surface-unrecognized"
    }

    if ($null -eq $owner) {
        return Get-Verdict -Class hard -Category "surface-unrecognized"
    }

    if (-not $PublishedTypes.Contains($owner)) {
        return Get-Verdict -Class review -Category "surface-new-type-member"
    }

    if ($kind -ceq "CLOSURE") {
        return Get-Verdict -Class hard -Category "surface-closure"
    }

    # A method's own modifiers, or a property's or event's accessor modifiers, such as
    # modifiers=abstract,virtual or get=protected internal:abstract,virtual. An accessibility can be
    # two words.
    $isAbstract = $Line -cmatch ' (modifiers|get|set|add|remove)=([a-z]+( [a-z]+)?:)?[^ ]*\babstract\b'

    if ($PublishedInterfaces.Contains($owner)) {
        if ($isAbstract) {
            return Get-Verdict -Class hard -Category "surface-interface-member"
        }

        return Get-Verdict -Class review -Category "surface-interface-default-member"
    }

    if (@("PROPERTY", "FIELD") -ccontains $kind -and $Line.EndsWith(" required=true", [System.StringComparison]::Ordinal)) {
        return Get-Verdict -Class hard -Category "surface-required-member"
    }

    if ($isAbstract) {
        return Get-Verdict -Class hard -Category "surface-abstract-member"
    }

    return Get-Verdict -Class review -Category "surface-addition"
}

<#
.DESCRIPTION
Compares the published and packed provider surfaces ordinally. A removed or changed line fails, and
Get-SurfaceAdditionVerdict classifies each added line.
#>
function Get-SurfaceCompatibilityFinding {
    [CmdletBinding()]
    [OutputType([pscustomobject[]])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $PublishedSurface,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]] $PackedSurface
    )

    $publishedSet = [System.Collections.Generic.HashSet[string]]::new([string[]] $PublishedSurface, [System.StringComparer]::Ordinal)
    $packedSet = [System.Collections.Generic.HashSet[string]]::new([string[]] $PackedSurface, [System.StringComparer]::Ordinal)

    $publishedTypes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $publishedInterfaces = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

    foreach ($line in ($PublishedSurface | Where-Object { $_.StartsWith("TYPE ", [System.StringComparison]::Ordinal) })) {
        $name = ($line -split " ")[1]
        $null = $publishedTypes.Add($name)

        if ($line -clike "* kind=interface *") {
            $null = $publishedInterfaces.Add($name)
        }
    }

    $packedTypes = [string[]] @(
        $PackedSurface |
            Where-Object { $_.StartsWith("TYPE ", [System.StringComparison]::Ordinal) } |
            ForEach-Object { ($_ -split " ")[1] } |
            Sort-Object -Property Length -Descending
    )

    $findings = [System.Collections.Generic.List[pscustomobject]]::new()

    foreach ($line in ($PublishedSurface | Where-Object { -not $packedSet.Contains($_) })) {
        $findings.Add([pscustomobject]@{ Pointer = "SURFACE:$line"; Change = "removed"; Class = "hard"; Category = "surface-removed-or-changed" })
    }

    foreach ($line in ($PackedSurface | Where-Object { -not $publishedSet.Contains($_) })) {
        $verdict = Get-SurfaceAdditionVerdict -Line $line -PublishedTypes $publishedTypes -PublishedInterfaces $publishedInterfaces -PackedTypes $packedTypes

        $findings.Add([pscustomobject]@{
                Pointer  = "SURFACE:$line"
                Change   = "added"
                Class    = $verdict.Class
                Category = $verdict.Category
            })
    }

    return $findings.ToArray()
}

<#
.DESCRIPTION
Compares the published and packed XML documentation member by member, and the declared dependencies
by target framework and package id. Every difference needs a review record. The contract's rules
live in that documentation, so a reworded rule can ask more of an existing provider than it did,
and a dependency changes what every implementer inherits. Automation can classify neither, so
neither ships unreviewed. A documentation pointer names the member's documentation id, and a
dependency pointer names its target framework and package id.
#>
function Get-PackageMetadataCompatibilityFinding {
    [CmdletBinding()]
    [OutputType([pscustomobject[]])]
    param(
        [Parameter(Mandatory)][pscustomobject] $PublishedContract,
        [Parameter(Mandatory)][pscustomobject] $PackedContract
    )

    $comparisons = @(
        @{
            Prefix    = "XMLDOC:"
            Category  = "package-documentation"
            Published = $PublishedContract.Documentation
            Packed    = $PackedContract.Documentation
            # The canonical line is "<escaped id> => <content>", and the escaping covers "=", so the
            # first " => " ends the id. The pointer carries the id unescaped.
            Key       = { param([string] $Line) [regex]::Replace(($Line -split " => ", 2)[0], '\\(.)', '$1') }
        }
        @{
            Prefix    = "DEPENDENCY:"
            Category  = "package-dependency"
            Published = $PublishedContract.Dependencies
            Packed    = $PackedContract.Dependencies
            # The canonical line is "<framework> | <id> | <range> | include=... | exclude=...", or
            # "<framework> | (empty group)".
            Key       = { param([string] $Line) (($Line -split " \| ") | Select-Object -First 2) -join " | " }
        }
    )

    $findings = [System.Collections.Generic.List[pscustomobject]]::new()

    foreach ($comparison in $comparisons) {
        $published = [System.Collections.Generic.SortedDictionary[string, string]]::new([System.StringComparer]::Ordinal)
        $packed = [System.Collections.Generic.SortedDictionary[string, string]]::new([System.StringComparer]::Ordinal)

        foreach ($line in $comparison.Published) { $published[(& $comparison.Key $line)] = $line }
        foreach ($line in $comparison.Packed) { $packed[(& $comparison.Key $line)] = $line }

        foreach ($key in $published.Keys) {
            if (-not $packed.ContainsKey($key)) {
                $change = "removed"
            }
            elseif (-not [string]::Equals($published[$key], $packed[$key], [System.StringComparison]::Ordinal)) {
                $change = "changed"
            }
            else {
                continue
            }

            $findings.Add([pscustomobject]@{ Pointer = "$($comparison.Prefix)$key"; Change = $change; Class = "review"; Category = $comparison.Category })
        }

        foreach ($key in ($packed.Keys | Where-Object { -not $published.ContainsKey($_) })) {
            $findings.Add([pscustomobject]@{ Pointer = "$($comparison.Prefix)$key"; Change = "added"; Class = "review"; Category = $comparison.Category })
        }
    }

    return $findings.ToArray()
}

<#
.DESCRIPTION
Reads the review record for a version increment, or returns no entries when the file does not
exist. A malformed record fails; it is never read leniently.
#>
function Get-ReviewRecordEntry {
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string] $Directory,
        [Parameter(Mandatory)][string] $From,
        [Parameter(Mandatory)][string] $To
    )

    $none = , [pscustomobject[]] @()

    if ([string]::IsNullOrWhiteSpace($Directory)) {
        return $none
    }

    $path = Join-Path $Directory "$From-to-$To.json"

    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return $none
    }

    try {
        $record = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($path)) -Depth 20
    }
    catch {
        throw "The review record $path is not parseable JSON: $($_.Exception.Message)"
    }

    if ($record -isnot [pscustomobject] -or [string] $record.from -cne $From -or [string] $record.to -cne $To) {
        throw "The review record $path must be an object with from '$From' and to '$To'."
    }

    if ($null -eq $record.reviewed -or $record.reviewed -is [string] -or $record.reviewed -isnot [System.Collections.IEnumerable]) {
        throw "The review record $path must carry a 'reviewed' array."
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $entries = [System.Collections.Generic.List[pscustomobject]]::new()

    foreach ($entry in @($record.reviewed)) {
        if ($entry -isnot [pscustomobject] -or $entry.pointer -isnot [string] -or [string]::IsNullOrWhiteSpace($entry.pointer)) {
            throw "A review record entry in $path has no pointer."
        }

        if (@("added", "removed", "changed") -cnotcontains $entry.change) {
            throw "The review record entry for $($entry.pointer) in $path has change '$($entry.change)'; it must be added, removed or changed."
        }

        if ($entry.reason -isnot [string] -or [string]::IsNullOrWhiteSpace($entry.reason)) {
            throw "The review record entry for $($entry.pointer) in $path has no reason. Say who reviewed what and why it is compatible."
        }

        if (-not $seen.Add("$($entry.change)|$($entry.pointer)")) {
            throw "The review record $path lists $($entry.change) $($entry.pointer) more than once."
        }

        $entries.Add([pscustomobject]@{ Pointer = $entry.pointer; Change = $entry.change })
    }

    return , $entries.ToArray()
}

<#
.DESCRIPTION
Applies a review record to the findings and fails with every hard-fail difference, then every
needs-review difference the record does not cover, then every stale record entry.
#>
function Assert-CompatibilityReviewed {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]] $Finding,
        [Parameter(Mandatory)][AllowEmptyCollection()][pscustomobject[]] $Entry,
        [Parameter(Mandatory)][string] $From,
        [Parameter(Mandatory)][string] $To
    )

    $recorded = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($item in $Entry) { $null = $recorded.Add("$($item.Change)|$($item.Pointer)") }

    $found = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($item in $Finding) { $null = $found.Add("$($item.Change)|$($item.Pointer)") }

    $hard = @($Finding | Where-Object { $_.Class -eq "hard" })
    $unreviewed = @($Finding | Where-Object { $_.Class -eq "review" -and -not $recorded.Contains("$($_.Change)|$($_.Pointer)") })
    $stale = @($Entry | Where-Object { -not $found.Contains("$($_.Change)|$($_.Pointer)") })
    $waived = @($Entry | Where-Object { $found.Contains("$($_.Change)|$($_.Pointer)") } | Where-Object {
            $key = "$($_.Change)|$($_.Pointer)"
            @($hard | Where-Object { "$($_.Change)|$($_.Pointer)" -ceq $key }).Count -gt 0
        })

    if ($hard.Count -eq 0 -and $unreviewed.Count -eq 0 -and $stale.Count -eq 0) {
        return [pscustomobject]@{ Reviewed = @($Finding | Where-Object { $_.Class -eq "review" }).Count }
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("The identity wire contract change from $From to $To is not compatible as it stands.")

    if ($hard.Count -gt 0) {
        $lines.Add("Hard-fail differences (a review record cannot waive these; they break existing clients or add a new obligation on existing providers):")
        foreach ($item in $hard) { $lines.Add("  [$($item.Category)] $($item.Change) $($item.Pointer)") }
    }

    if ($waived.Count -gt 0) {
        $lines.Add("The review record names hard-fail differences, which it cannot waive:")
        foreach ($item in $waived) { $lines.Add("  $($item.Change) $($item.Pointer)") }
    }

    if ($unreviewed.Count -gt 0) {
        $lines.Add("Differences that need an entry in eng/verification/IdentityWireCompatibility/$From-to-$To.json:")
        foreach ($item in $unreviewed) { $lines.Add("  [$($item.Category)] $($item.Change) $($item.Pointer)") }
    }

    if ($stale.Count -gt 0) {
        $lines.Add("Stale review record entries that match no difference:")
        foreach ($item in $stale) { $lines.Add("  $($item.Change) $($item.Pointer)") }
    }

    throw ($lines -join [Environment]::NewLine)
}


<#
.SYNOPSIS
Decides whether the served identity wire document may ship at the declared contract version.

.OUTPUTS
One object typed EdFi.IdentityWireContractDecision with Outcome (initial | unchanged |
increment-reviewed), PublishedVersion (null when nothing is published) and AnchorCommit (null for
initial).
#>
function Invoke-IdentityWireContractGate {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][string] $ServedDocumentPath,
        [Parameter(Mandatory)][string] $ContractVersion,
        [string] $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path,
        [string] $BaselinePath = "src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/OpenApi/Fixtures/identity-v2-wire-baseline.json",
        [string] $PackageId = "EdFi.Api.Identity",
        [string] $ServiceIndexUrl = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",
        [string] $FeedApiKey = "",
        [scriptblock] $ResolvePackageBaseAddress,
        [scriptblock] $GetPublishedVersions,
        [scriptblock] $SavePublishedPackage,

        # The nupkg packed at the gated commit; required when the contract version is an increment.
        [string] $PackedPackageFile = "",

        # Directory of <published>-to-<current>.json review records.
        [string] $ReviewRecordDirectory = (Join-Path $PSScriptRoot "IdentityWireCompatibility")
    )

    if ($null -eq $ResolvePackageBaseAddress) { $ResolvePackageBaseAddress = Get-DefaultResolvePackageBaseAddress }
    if ($null -eq $GetPublishedVersions) { $GetPublishedVersions = Get-DefaultPublishedVersionLookup }
    if ($null -eq $SavePublishedPackage) { $SavePublishedPackage = Get-DefaultSavePublishedPackage }

    $normalizedContractVersion = ConvertTo-NormalizedPackageVersion -Version $ContractVersion
    $normalizedId = ConvertTo-NormalizedPackageId -PackageId $PackageId
    $repoRelativePath = $BaselinePath.Replace("\", "/")

    if (-not (Test-Path -LiteralPath $ServedDocumentPath -PathType Leaf)) {
        throw "The served document was not found: $ServedDocumentPath"
    }

    $servedText = Get-JsonDocumentText -Text ([System.IO.File]::ReadAllText($ServedDocumentPath)) -Description "served document at $ServedDocumentPath"

    function Get-GateDecision {
        param([string] $Outcome, $PublishedVersion, $AnchorCommit)

        return [pscustomobject]@{
            PSTypeName       = "EdFi.IdentityWireContractDecision"
            Outcome          = $Outcome
            PublishedVersion = $PublishedVersion
            AnchorCommit     = $AnchorCommit
        }
    }

    function Read-BaselineAt {
        param([string] $Revision, [string] $Description)

        $show = Invoke-GitCommand -RepositoryRoot $RepositoryRoot -Arguments @("show", "${Revision}:$repoRelativePath")

        if ($show.ExitCode -ne 0) {
            throw "The baseline $repoRelativePath is absent at $Description ($($show.Stderr))."
        }

        return Get-JsonDocumentText -Text $show.Stdout -Description "baseline $repoRelativePath at $Description"
    }

    # The service index first, on its own: its success is what licenses reading a 404 as absence.
    $baseAddress = & $ResolvePackageBaseAddress $ServiceIndexUrl $FeedApiKey

    [uri] $baseUri = $null

    if (
        -not [uri]::TryCreate($baseAddress, [System.UriKind]::Absolute, [ref] $baseUri) -or
        ($baseUri.Scheme -ne "http" -and $baseUri.Scheme -ne "https")
    ) {
        throw "The feed's service index resolved to '$baseAddress', which is not an absolute http or https address."
    }

    $baseAddress = $baseAddress.TrimEnd('/')

    $published = & $GetPublishedVersions $baseAddress $normalizedId $FeedApiKey

    if ($null -eq $published -or $null -eq $published.Found -or $published.Found -isnot [bool]) {
        throw "The feed lookup for $PackageId returned no usable result. A feed that cannot be read is not an absent package."
    }

    if (-not $published.Found) {
        $headText = Read-BaselineAt -Revision "HEAD" -Description "HEAD"

        Assert-ServedEqualsBaseline -RepositoryRoot $RepositoryRoot -BaselineText $headText -ServedText $servedText -Description "the reviewed initial baseline committed at HEAD"

        Write-Information "$PackageId is not on the feed; the served document equals the reviewed initial baseline." -InformationAction Continue

        return Get-GateDecision -Outcome "initial" -PublishedVersion $null -AnchorCommit $null
    }

    if ($null -eq $published.Versions) {
        throw "The feed reported $PackageId as present and listed no versions. That is a malformed response, not an absent version."
    }

    # NuGet.Versioning is already loaded: ConvertTo-NormalizedPackageVersion above initializes it.
    $contract = [NuGet.Versioning.NuGetVersion]::Parse($normalizedContractVersion)

    $candidates = @(
        $published.Versions | ForEach-Object {
            if ($null -eq $_ -or [string]::IsNullOrWhiteSpace([string] $_)) {
                throw "The feed listed a blank version for $PackageId. That is a malformed version index."
            }

            [NuGet.Versioning.NuGetVersion]::Parse((ConvertTo-NormalizedPackageVersion -Version ([string] $_)))
        }
    )

    if ($candidates.Count -eq 0) {
        throw "The feed reported $PackageId as present and listed no versions. That is a malformed response, not an absent version."
    }

    $eligible = @($candidates | Where-Object { $_.CompareTo($contract) -le 0 } | Sort-Object)

    if ($eligible.Count -eq 0) {
        throw "Every published $PackageId version ($(($candidates | Sort-Object | ForEach-Object { $_.ToNormalizedString() }) -join ', ')) is greater than the contract version $normalizedContractVersion. The contract version went backwards."
    }

    $anchorVersion = $eligible[-1].ToNormalizedString().ToLowerInvariant()

    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) "identity-wire-anchor-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null

    try {
        $downloadPath = Join-Path $scratch "$normalizedId.$anchorVersion.nupkg"
        & $SavePublishedPackage $baseAddress $normalizedId $anchorVersion $downloadPath $FeedApiKey

        if (-not (Test-Path -LiteralPath $downloadPath -PathType Leaf)) {
            throw "The published $PackageId $anchorVersion was not written to $downloadPath, although the feed reported no error."
        }

        $anchorCommit = Get-PackageAnchorCommit -PackagePath $downloadPath -Description "published $PackageId $anchorVersion"

        if ($anchorVersion -cne $normalizedContractVersion) {
            if ([string]::IsNullOrWhiteSpace($PackedPackageFile)) {
                throw "The contract version $normalizedContractVersion is greater than the published $PackageId $anchorVersion, which requires -PackedPackageFile to compare the packages."
            }

            $publishedContract = Get-PackageContract -PackagePath $downloadPath -Description "published $PackageId $anchorVersion" -WorkDirectory $scratch
            $packedContract = Get-PackageContract -PackagePath $PackedPackageFile -Description "packed $PackageId" -WorkDirectory $scratch
        }
    }
    finally {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }

    $commitSpec = "$anchorCommit^{commit}"
    $present = Invoke-GitCommand -RepositoryRoot $RepositoryRoot -Arguments @("cat-file", "-e", $commitSpec)

    if ($present.ExitCode -ne 0) {
        # Fetched at most once: a shallow checkout is the usual reason the commit is missing.
        $null = Invoke-GitCommand -RepositoryRoot $RepositoryRoot -Arguments @("fetch", "--no-tags", "origin", $anchorCommit)
        $present = Invoke-GitCommand -RepositoryRoot $RepositoryRoot -Arguments @("cat-file", "-e", $commitSpec)
    }

    if ($present.ExitCode -ne 0) {
        throw "The anchor commit $anchorCommit recorded by published $PackageId $anchorVersion is not reachable in this repository, and fetching it from origin did not make it available."
    }

    $anchorText = Read-BaselineAt -Revision $anchorCommit -Description "commit $anchorCommit (published $PackageId $anchorVersion)"

    if ($anchorVersion -ceq $normalizedContractVersion) {
        Assert-ServedEqualsBaseline -RepositoryRoot $RepositoryRoot -BaselineText $anchorText -ServedText $servedText -Description "the baseline published with $PackageId $anchorVersion (commit $anchorCommit)"

        Write-Information "$PackageId $anchorVersion is published and the served document equals its baseline." -InformationAction Continue

        return Get-GateDecision -Outcome "unchanged" -PublishedVersion $anchorVersion -AnchorCommit $anchorCommit
    }

    $findings = @(Get-WireCompatibilityFinding -BaselineText $anchorText -ServedText $servedText) +
        @(Get-SurfaceCompatibilityFinding -PublishedSurface $publishedContract.Surface -PackedSurface $packedContract.Surface) +
        @(Get-PackageMetadataCompatibilityFinding -PublishedContract $publishedContract -PackedContract $packedContract)

    $entries = Get-ReviewRecordEntry -Directory $ReviewRecordDirectory -From $anchorVersion -To $normalizedContractVersion
    $result = Assert-CompatibilityReviewed -Finding $findings -Entry $entries -From $anchorVersion -To $normalizedContractVersion

    Write-Information "$PackageId $normalizedContractVersion is an increment over $anchorVersion; $($result.Reviewed) difference(s) reviewed and none hard-fail." -InformationAction Continue

    return Get-GateDecision -Outcome "increment-reviewed" -PublishedVersion $anchorVersion -AnchorCommit $anchorCommit
}

Export-ModuleMember -Function Invoke-IdentityWireContractGate
