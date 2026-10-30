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
# developer machines that run it have no YAML module, and a run value's extent is decided by
# indentation alone, which is all it needs.

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
        deeply indented lines, under a plain or quoted run key. The lines are returned unchanged,
        indentation included. A run key whose value is a nested mapping, as under defaults:, is
        not a script and is skipped.

        A run this reader cannot follow is returned with Supported set to false rather than
        skipped: a run key inside a flow mapping (- { run: ... }), and a run value that is an
        alias, an anchor or a tag. The guard refuses those, so a script it cannot read is never a
        script it passed. The content of another key's block scalar, such as a JSON payload, is
        text and is not searched for run keys.
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
    $i = 0
    while ($i -lt $lines.Count) {
        $line = $lines[$i]
        $match = [regex]::Match($line, '^(\s*(?:-\s+)?)(["'']?)run\2:(?:\s+(.*))?$')
        if (-not $match.Success) {
            $block = [regex]::Match($line, '^(\s*(?:-\s+)?)[^\s#{\[][^:]*:\s+[|>][0-9+-]*\s*(?:#.*)?$')
            if ($line -match '[{,]\s*["'']?run["'']?\s*:') {
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
        elseif ($value.Length -eq 0 -and $firstFollowing -match '^\s*[A-Za-z_][\w-]*:(\s|$)') {
            # defaults: run: shell: pwsh
        }
        else {
            [pscustomobject]@{ Line = $i + 1; Text = (@($value) + $following) -join "`n"; Supported = $true }
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
    # the content of another key's block scalar, such as a JSON payload.
    param([string]$Content)

    $lines = ($Content -replace "`r`n", "`n") -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $skip = [regex]::Match($lines[$i], '^(\s*(?:-\s+)?)(?:(["'']?)with\2:\s*(?:#.*)?|[^\s#{\[][^:]*:\s+[|>][0-9+-]*\s*(?:#.*)?)$')
        if ($skip.Success) {
            $i = (Get-ValueEnd $lines $i $skip.Groups[1].Value.Length) - 1
            continue
        }
        $key = [regex]::Match($lines[$i], '^(\s*(?:-\s+)?)(["'']?)env\2:(.*)$')
        if (-not $key.Success) { continue }
        $value = $key.Groups[3].Value -replace '(^|\s)#.*$', '' -replace '(^|\s)[&!]\S*', ''
        if ($value.Trim().Length -gt 0) {
            $normalized = ConvertTo-DotNotation $value
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
            $entry = [regex]::Match($lines[$j], '^\s*(["'']?)([A-Za-z_][\w-]*)\1:(?:\s+(.*))?$')
            if (-not $entry.Success -or $entry.Groups[3].Value -match '^\*') {
                [pscustomobject]@{ Line = $j + 1; Text = $lines[$j]; Supported = $false }
                continue
            }
            $end = Get-ValueEnd $lines $j $childIndent
            [pscustomobject]@{
                Name      = $entry.Groups[2].Value
                Value     = (@($entry.Groups[3].Value) + @(if ($end -gt $j + 1) { $lines[($j + 1)..($end - 1)] })) -join "`n"
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
            foreach ($expression in [regex]::Matches($item.Value, '\$\{\{(.*?)\}\}', 'Singleline')) {
                $normalized = ConvertTo-DotNotation $expression.Groups[1].Value
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
            foreach ($expression in [regex]::Matches($script.Text, '\$\{\{(.*?)\}\}', 'Singleline')) {
                $normalized = ConvertTo-DotNotation $expression.Groups[1].Value
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
