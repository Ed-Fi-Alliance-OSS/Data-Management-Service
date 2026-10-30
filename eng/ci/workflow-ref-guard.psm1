# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# GitHub substitutes ${{ }} expressions into a run script before the shell reads it, and git allows
# shell metacharacters in branch and tag names, so a ref name written into a script is code. A
# secret written there is code as well, and is also part of the script text the runner saves and
# a failing command may echo. A workflow passes either through env instead
# (env: REF: ${{ github.ref_name }}) and the script reads "$REF" or $env:REF. This module finds the
# expressions that break that rule.
#
# It reads workflow text line by line rather than through a YAML library: the runners and the
# developer machines that run it have no YAML module, a run value's extent is decided by
# indentation alone, and the annotations need line numbers. What it cannot place, it refuses.

# A ref or tag name the event carries: github.ref, github.ref_name, github.head_ref,
# github.base_ref, github.event.ref, github.event.pull_request.head.ref,
# github.event.release.tag_name, github.event.workflow_run.head_branch and the like, in dot or
# bracket form. github.ref_type is 'branch' or 'tag', never a name, and is not matched.
$script:RefProperty = 'ref|ref_name|head_ref|base_ref|tag_name|head_branch|target_commitish'
$script:RefPattern = "(?i)\bgithub(?:\.[\w*-]+)*\.(?:$script:RefProperty)(?![\w-])"

# The secrets context, as secrets.NAME, secrets['NAME'] or toJSON(secrets). A property or job
# named like it (needs.pack-secrets, env.SECRETS_PACKAGE_NAME) is not the context.
$script:SecretPattern = '(?i)(?<![\w.-])secrets(?![\w-])'

function ConvertTo-DotNotation {
    # github['ref_name'] and github . ref_name are github.ref_name.
    param([string]$Expression)

    $Expression -replace "\[\s*'([^']*)'\s*\]", '.$1' -replace '\[\s*"([^"]*)"\s*\]', '.$1' -replace '\s*\.\s*', '.'
}

function Get-WorkflowExpression([string]$Text) {
    # Every ${{ }} expression in $Text, found as GitHub finds them: the expression ends at the first
    # }} outside a single-quoted string, so format('{{{0}}}', github.ref_name) is one expression.
    # A quote toggles the string, which also handles '' inside one. An unclosed expression runs to
    # the end of the text.
    $start = $Text.IndexOf('${{', [StringComparison]::Ordinal)
    while ($start -ge 0) {
        $inString = $false
        $end = $Text.Length
        for ($k = $start + 3; $k -lt $Text.Length - 1; $k++) {
            if ($Text[$k] -eq "'") {
                $inString = -not $inString
            }
            elseif (-not $inString -and $Text[$k] -eq '}' -and $Text[$k + 1] -eq '}') {
                $end = $k + 2
                break
            }
        }
        $value = $Text.Substring($start, $end - $start)
        [pscustomobject]@{
            Index = $start
            Value = $value
            Inner = $value.Substring(3) -replace '\}\}$', ''
        }
        $start = $Text.IndexOf('${{', $end, [StringComparison]::Ordinal)
    }
}

function ConvertFrom-QuotedScalar([string]$Text) {
    # The value YAML reads from a single- or double-quoted scalar, which is what GitHub sees:
    # 'github[''ref_name'']' is github['ref_name']. Text that is not quoted is returned unchanged.
    # Every line break is kept and an escape never becomes one, so a line counted in the result is
    # a line of the file; anything after the closing quote, such as a comment, is dropped.
    $quote = if ($Text.Length -gt 0) { $Text[0] }
    if ($quote -ne "'" -and $quote -ne '"') { return $Text }

    $decoded = [System.Text.StringBuilder]::new()
    for ($k = 1; $k -lt $Text.Length; $k++) {
        $c = $Text[$k]
        if ($quote -eq "'" -and $c -eq "'") {
            if ($k + 1 -lt $Text.Length -and $Text[$k + 1] -eq "'") {
                [void]$decoded.Append("'")
                $k++
                continue
            }
            break
        }
        if ($quote -eq '"' -and $c -eq '"') { break }
        if ($quote -eq '"' -and $c -eq '\' -and $k + 1 -lt $Text.Length) {
            $k++
            $escape = $Text[$k]
            $hexLength = switch -CaseSensitive ($escape) { 'x' { 2 } 'u' { 4 } 'U' { 8 } default { 0 } }
            $code = 0
            $char = if ($escape -in '"', '\', '/', "`n") {
                # \ before a line break continues the line; the break itself is kept.
                $escape
            }
            elseif ($escape -ceq 't') {
                "`t"
            }
            elseif ($hexLength -gt 0 -and $k + $hexLength -lt $Text.Length -and
                [int]::TryParse($Text.Substring($k + 1, $hexLength), 'AllowHexSpecifier', $null, [ref]$code) -and
                $code -ge 0 -and $code -le 0x10FFFF -and ($code -lt 0xD800 -or $code -gt 0xDFFF)) {
                $k += $hexLength
                [char]::ConvertFromUtf32($code) -replace '[\r\n]', ' '
            }
            else {
                # \n, \0 and the other control escapes; a space keeps expressions apart as they are.
                ' '
            }
            [void]$decoded.Append($char)
            continue
        }
        [void]$decoded.Append($c)
    }
    return $decoded.ToString()
}

function Get-JobIdLine([string[]]$Lines) {
    # The indexes of the lines that name a job. A job may be called run, env or with, and its
    # contents are still read: a job id is never a key of a step.
    $ids = [System.Collections.Generic.HashSet[int]]::new()
    $jobs = [array]::FindIndex($Lines, [Predicate[string]] { param($l) $l -match '^(["'']?)jobs\1\s*:\s*(?:#.*)?$' })
    if ($jobs -lt 0) { return , $ids }
    $childIndent = $null
    for ($j = $jobs + 1; $j -lt $Lines.Count; $j++) {
        if ($Lines[$j].Trim().Length -eq 0 -or $Lines[$j].TrimStart().StartsWith('#')) { continue }
        $indent = Get-Indent $Lines[$j]
        if ($indent -eq 0) { break }
        if ($null -eq $childIndent) { $childIndent = $indent }
        if ($indent -eq $childIndent) { [void]$ids.Add($j) }
    }
    return , $ids
}

function Get-Indent([string]$Line) {
    $Line.Length - $Line.TrimStart().Length
}

function Get-ValueEnd([string[]]$Lines, [int]$Index, [int]$KeyColumn) {
    # The line after the last one belonging to the key on line $Index: its value continues while
    # lines are blank or indented past the key.
    $end = $Index + 1
    while ($end -lt $Lines.Count -and ($Lines[$end].Trim().Length -eq 0 -or (Get-Indent $Lines[$end]) -gt $KeyColumn)) {
        $end++
    }
    return $end
}

function Get-WorkflowRunScript {
    <#
    .SYNOPSIS
        Returns every run value in workflow or action text, with the line its text starts on.
    .DESCRIPTION
        A run value is everything GitHub hands the shell: a block scalar (| or >, with any
        chomping or indentation indicator), or a plain or quoted scalar that may continue on more
        deeply indented lines, under a plain or quoted run key, with or without space before the
        colon. A block scalar's lines are returned unchanged, indentation included; a quoted
        scalar is returned as YAML decodes it, with its line breaks kept. A run key whose value is
        a nested mapping, as under defaults:, is not a script, and the lines under it are read on.
        A job called run is a job, and its steps are read.

        A run this reader cannot follow is returned with Supported set to false rather than
        skipped: a run key inside a flow mapping (- { run: ... }), an explicit ? key, and a run
        value that is an alias, an anchor or a tag. The guard refuses those, so a script it cannot
        read is never a script it passed. The content of another key's block scalar, such as a
        JSON payload, is text and is not searched for run keys.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $Content
    )

    $lines = ($Content -replace "`r`n", "`n") -split "`n"
    $jobIds = Get-JobIdLine $lines
    $i = 0
    while ($i -lt $lines.Count) {
        $line = $lines[$i]
        $match = [regex]::Match($line, '^(\s*(?:-\s+)?)(["'']?)run\2\s*:(?:\s+(.*))?$')
        if ($jobIds.Contains($i) -or -not $match.Success) {
            $block = [regex]::Match($line, '^(\s*(?:-\s+)?)[^\s#{\[][^:]*:\s+[|>][0-9+-]*\s*(?:#.*)?$')
            if ($line -match '[{,]\s*["'']?run["'']?\s*:' -or $line -match '^\s*(?:-\s+)?\?(\s|$)') {
                [pscustomobject]@{ Line = $i + 1; Text = $line; Supported = $false }
                $i++
            }
            elseif ($block.Success) {
                $i = Get-ValueEnd $lines $i $block.Groups[1].Value.Length
            }
            else {
                $i++
            }
            continue
        }

        $keyColumn = $match.Groups[1].Value.Length
        $value = $match.Groups[3].Value.Trim()
        $end = Get-ValueEnd $lines $i $keyColumn
        $following = @(if ($end -gt $i + 1) { $lines[($i + 1)..($end - 1)] })
        $firstFollowing = $following | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1

        if ($value -match '^[*&!]') {
            [pscustomobject]@{ Line = $i + 1; Text = $line; Supported = $false }
        }
        elseif ($value -match '^[|>][0-9+-]*(\s+#.*)?$') {
            [pscustomobject]@{ Line = $i + 2; Text = $following -join "`n"; Supported = $true }
        }
        elseif ($value.Length -eq 0 -and $firstFollowing -match '^\s*[A-Za-z_][\w-]*\s*:(\s|$)') {
            # defaults: run: shell: pwsh. The lines under it are still read, in case it is not that.
            $end = $i + 1
        }
        else {
            [pscustomobject]@{ Line = $i + 1; Text = ConvertFrom-QuotedScalar ((@($value) + $following) -join "`n"); Supported = $true }
        }
        $i = $end
    }
}

function Get-EnvEntry {
    # Every entry of every env mapping, at any level, with its whole value: a value that continues
    # on more deeply indented lines, such as a block scalar, includes those lines.
    #
    # An env this reader cannot follow is returned with Supported set to false, as an unreadable run
    # is: a flow mapping (env: { ... }), an alias or merge key standing for entries, and an entry
    # whose value is an alias. Each could carry a secret or ref into env.NAME without it being seen.
    # An env value that is a single expression is unreadable only when it names a ref or secret.
    # Action inputs under with: never reach a script, so an input called env is not searched, nor is
    # the content of another key's block scalar, such as a JSON payload. A job called env or with is
    # a job, and its env is read. A quoted value is read as YAML decodes it.
    param([string]$Content)

    $lines = ($Content -replace "`r`n", "`n") -split "`n"
    $jobIds = Get-JobIdLine $lines
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($jobIds.Contains($i)) { continue }
        $skip = [regex]::Match($lines[$i], '^(\s*(?:-\s+)?)(?:(["'']?)with\2\s*:\s*(?:#.*)?|[^\s#{\[][^:]*:\s+[|>][0-9+-]*\s*(?:#.*)?)$')
        if ($skip.Success) {
            $i = (Get-ValueEnd $lines $i $skip.Groups[1].Value.Length) - 1
            continue
        }
        $key = [regex]::Match($lines[$i], '^(\s*(?:-\s+)?)(["'']?)env\2\s*:(.*)$')
        if (-not $key.Success) { continue }
        $value = $key.Groups[3].Value -replace '(^|\s)#.*$', '' -replace '(^|\s)[&!]\S*', ''
        if ($value.Trim().Length -gt 0) {
            $normalized = ConvertTo-DotNotation (ConvertFrom-QuotedScalar $value.Trim())
            if ($value -match '^\s*[{\[*]' -or $normalized -match $script:RefPattern -or $normalized -match $script:SecretPattern) {
                [pscustomobject]@{ Line = $i + 1; Text = $lines[$i]; Supported = $false }
            }
            continue
        }
        $envIndent = $key.Groups[1].Value.Length
        $childIndent = $null
        for ($j = $i + 1; $j -lt $lines.Count; $j++) {
            if ($lines[$j].Trim().Length -eq 0 -or $lines[$j].TrimStart().StartsWith('#')) { continue }
            $indent = Get-Indent $lines[$j]
            if ($indent -le $envIndent) { break }
            if ($null -eq $childIndent) { $childIndent = $indent }
            if ($indent -ne $childIndent) { continue }
            $entry = [regex]::Match($lines[$j], '^\s*(["'']?)([A-Za-z_][\w-]*)\1\s*:(?:\s+(.*))?$')
            if (-not $entry.Success -or $entry.Groups[3].Value -match '^\*') {
                [pscustomobject]@{ Line = $j + 1; Text = $lines[$j]; Supported = $false }
                continue
            }
            $end = Get-ValueEnd $lines $j $childIndent
            [pscustomobject]@{
                Name      = $entry.Groups[2].Value
                Value     = ConvertFrom-QuotedScalar ((@($entry.Groups[3].Value.Trim()) + @(if ($end -gt $j + 1) { $lines[($j + 1)..($end - 1)] })) -join "`n")
                Supported = $true
            }
        }
    }
}

function Get-EnvAliasPattern([System.Collections.Generic.HashSet[string]]$Alias) {
    # Matches env.NAME for each name in $Alias, or nothing when it is empty.
    if ($Alias.Count -eq 0) { return '(?!)' }
    "(?i)\benv\.(?:$(($Alias | ForEach-Object { [regex]::Escape($_) }) -join '|'))(?![\w-])"
}

function Get-EnvAlias {
    # The names of env entries whose value has an expression matching $Pattern, directly or through
    # another such entry. ${{ env.NAME }} in a script pastes the value back into the script text just
    # as the original expression would.
    param([object[]]$Entry, [string]$Pattern)

    $alias = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    do {
        $aliasPattern = Get-EnvAliasPattern $alias
        $added = $false
        foreach ($item in $Entry) {
            if ($alias.Contains($item.Name)) { continue }
            foreach ($expression in Get-WorkflowExpression $item.Value) {
                $normalized = ConvertTo-DotNotation $expression.Inner
                if ($normalized -match $Pattern -or $normalized -match $aliasPattern) {
                    $added = $alias.Add($item.Name)
                    break
                }
            }
        }
    } while ($added)
    return , $alias
}

function Find-WorkflowRefInjection {
    <#
    .SYNOPSIS
        Returns each ref, tag or secret expression written into a run script in the given files.
    .DESCRIPTION
        Matches github.<...>.ref, ref_name, head_ref, base_ref, tag_name, head_branch and
        target_commitish in dot or bracket form, anywhere inside a ${{ }} expression (including
        one that spans lines or wraps the ref in a function call), and env.NAME where the same
        file defines env NAME, at any level, from such an expression; those findings have Problem
        Ref. Matches the secrets context and env.NAME defined from it the same way, with Problem
        Secret. Expressions in env:, with: and if: are not run scripts and are not matched. A run
        or env the reader cannot follow is returned with Problem Unsupported.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [string[]]
        $Path,

        # The paths are reported relative to this directory.
        [string]
        $Root = (Get-Location).Path
    )

    foreach ($file in $Path) {
        $content = Get-Content -LiteralPath $file -Raw
        if ($null -eq $content) { continue }
        $entries = @(Get-EnvEntry $content | Where-Object Supported)
        $refAliasPattern = Get-EnvAliasPattern (Get-EnvAlias $entries $script:RefPattern)
        $secretAliasPattern = Get-EnvAliasPattern (Get-EnvAlias $entries $script:SecretPattern)
        $relative = [System.IO.Path]::GetRelativePath($Root, $file) -replace '\\', '/'

        foreach ($unreadable in Get-EnvEntry $content | Where-Object { -not $_.Supported }) {
            [pscustomobject]@{
                File       = $relative
                Line       = $unreadable.Line
                Expression = $unreadable.Text.Trim()
                Problem    = 'Unsupported'
            }
        }

        foreach ($script in Get-WorkflowRunScript -Content $content) {
            if (-not $script.Supported) {
                [pscustomobject]@{
                    File       = $relative
                    Line       = $script.Line
                    Expression = $script.Text.Trim()
                    Problem    = 'Unsupported'
                }
                continue
            }
            foreach ($expression in Get-WorkflowExpression $script.Text) {
                $normalized = ConvertTo-DotNotation $expression.Inner
                $problem = if ($normalized -match $script:RefPattern -or $normalized -match $refAliasPattern) {
                    'Ref'
                }
                elseif ($normalized -match $script:SecretPattern -or $normalized -match $secretAliasPattern) {
                    'Secret'
                }
                if ($problem) {
                    [pscustomobject]@{
                        File       = $relative
                        Line       = $script.Line + ([regex]::Matches($script.Text.Substring(0, $expression.Index), "`n")).Count
                        Expression = $expression.Value -replace '\s+', ' '
                        Problem    = $problem
                    }
                }
            }
        }
    }
}

Export-ModuleMember -Function Get-WorkflowRunScript, Find-WorkflowRefInjection
