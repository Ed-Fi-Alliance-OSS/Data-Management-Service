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
# Workflows are read with YamlDotNet, the YAML library the Actions runner is built on, so quoting,
# flow mappings, explicit keys and aliases are decoded as YAML defines them rather than guessed from
# indentation. Only the places GitHub runs a script or sets the env context are read:
# jobs.<id>.steps[*].run in a workflow, runs.steps[*].run in a composite action, and env at the
# workflow, job and step levels. YamlDotNet leaves a merge key (<<) unexpanded, so what it would
# bring into a step, job or env is not read there, and the guard refuses every merge key.

# The pinned package, checked against the SHA-512 nuget.org records for it before it is loaded.
$script:YamlDotNetVersion = '18.1.0'
$script:YamlDotNetSha512 = 'B9EC6E231B7B90819A5A5A7E2A3E554ED10012205F9712E890B69FA57F570D029BDA369375D8A894B9476C571766B985C5EEAF6B474BAE9BB9D47B594274ABCE'
$script:YamlDotNet = $null

# A ref or tag name the event carries: github.ref, github.ref_name, github.head_ref,
# github.base_ref, github.event.ref, github.event.pull_request.head.ref,
# github.event.release.tag_name, github.event.workflow_run.head_branch and the like, in dot or
# bracket form, and github.workflow_ref, which ends in the ref the workflow file was read from.
# github.ref_type is 'branch' or 'tag', never a name, and is not matched.
$script:RefProperty = 'ref|ref_name|head_ref|base_ref|tag_name|head_branch|target_commitish|workflow_ref'
$script:RefPattern = "(?i)\bgithub(?:\.[\w*-]+)*\.(?:$script:RefProperty)(?![\w-])"

# The secrets context, as secrets.NAME, secrets['NAME'] or toJSON(secrets), and github.token, which
# is secrets.GITHUB_TOKEN. A property or job named like the context (needs.pack-secrets,
# env.SECRETS_PACKAGE_NAME) is not the context.
$script:SecretPattern = '(?i)(?<![\w.-])(?:secrets|github\.token)(?![\w-])'

function Get-YamlDotNet {
    # The YamlDotNet assembly. The package is downloaded once into the temp directory, and its hash
    # is checked on every load, from the same bytes the assembly is then read from. The assembly is
    # loaded from those bytes into a context of its own, so its types are taken from it by name
    # rather than by type literal, which could resolve to another YamlDotNet already loaded.
    if ($script:YamlDotNet) { return $script:YamlDotNet }

    $cache = Join-Path ([System.IO.Path]::GetTempPath()) "yamldotnet.$script:YamlDotNetVersion.nupkg"
    $package = if (Test-Path -LiteralPath $cache) { [System.IO.File]::ReadAllBytes($cache) }
    if (-not $package -or [Convert]::ToHexString([System.Security.Cryptography.SHA512]::HashData($package)) -ne $script:YamlDotNetSha512) {
        $client = [System.Net.Http.HttpClient]::new()
        try {
            $package = $client.GetByteArrayAsync("https://api.nuget.org/v3-flatcontainer/yamldotnet/$script:YamlDotNetVersion/yamldotnet.$script:YamlDotNetVersion.nupkg").GetAwaiter().GetResult()
        }
        finally {
            $client.Dispose()
        }
        if ([Convert]::ToHexString([System.Security.Cryptography.SHA512]::HashData($package)) -ne $script:YamlDotNetSha512) {
            throw "The YamlDotNet $script:YamlDotNetVersion package downloaded from nuget.org does not have the pinned SHA-512."
        }
        # Written aside and moved into place, so a process reading the cache never sees part of it.
        $partial = "$cache.$([guid]::NewGuid().ToString('N'))"
        [System.IO.File]::WriteAllBytes($partial, $package)
        [System.IO.File]::Move($partial, $cache, $true)
    }

    Add-Type -AssemblyName System.IO.Compression
    $zip = [System.IO.Compression.ZipArchive]::new([System.IO.MemoryStream]::new($package))
    try {
        $assembly = [System.IO.MemoryStream]::new()
        $entry = $zip.GetEntry('lib/net8.0/YamlDotNet.dll').Open()
        try { $entry.CopyTo($assembly) } finally { $entry.Dispose() }
    }
    finally {
        $zip.Dispose()
    }
    $script:YamlDotNet = [System.Reflection.Assembly]::Load($assembly.ToArray())
    return $script:YamlDotNet
}

function Read-YamlStream([string]$Content) {
    # The documents in $Content. Throws when it is not YAML, as GitHub refuses such a workflow.
    $stream = [Activator]::CreateInstance((Get-YamlDotNet).GetType('YamlDotNet.RepresentationModel.YamlStream', $true))
    $stream.Load([System.IO.StringReader]::new($Content))
    $stream.Documents
}

function Get-MappingValue($Node, [string]$Key) {
    # Each value under $Key when $Node is a mapping. A key is matched without regard to case, which
    # reads at least everything GitHub reads.
    if ("$($Node.NodeType)" -ne 'Mapping') { return }
    foreach ($pair in $Node.Children) {
        if ("$($pair.Key.NodeType)" -eq 'Scalar' -and $pair.Key.Value -eq $Key) { , $pair.Value }
    }
}

function Get-SequenceItem($Node) {
    if ("$($Node.NodeType)" -ne 'Sequence') { return }
    foreach ($item in $Node.Children) { , $item }
}

function Get-ScalarLine($Node) {
    # The line a scalar's text starts on: a block scalar's starts on the line after its | or >.
    $Node.Start.Line + [int]("$($Node.Style)" -in 'Literal', 'Folded')
}

function Get-WorkflowStep($Root) {
    # Each step GitHub runs, with the id of its job: jobs.<id>.steps in a workflow, and runs.steps,
    # with no job, in a composite action.
    foreach ($jobs in Get-MappingValue $Root 'jobs') {
        if ("$($jobs.NodeType)" -ne 'Mapping') { continue }
        foreach ($job in $jobs.Children) {
            foreach ($step in Get-MappingValue $job.Value 'steps' | ForEach-Object { Get-SequenceItem $_ }) {
                [pscustomobject]@{ Job = $job.Key.Value; Node = $step }
            }
        }
    }
    foreach ($step in Get-MappingValue $Root 'runs' | ForEach-Object { Get-MappingValue $_ 'steps' } | ForEach-Object { Get-SequenceItem $_ }) {
        [pscustomobject]@{ Job = $null; Node = $step }
    }
}

function Get-RunScript($Root) {
    foreach ($step in Get-WorkflowStep $Root) {
        foreach ($run in Get-MappingValue $step.Node 'run') {
            if ("$($run.NodeType)" -ne 'Scalar') { continue }
            [pscustomobject]@{
                Job  = $step.Job
                Step = @(Get-MappingValue $step.Node 'name' | ForEach-Object Value)[0]
                Line = Get-ScalarLine $run
                Text = $run.Value
            }
        }
    }
}

function Get-WorkflowRunScript {
    <#
    .SYNOPSIS
        Returns every run script in workflow or composite action text.
    .DESCRIPTION
        Each result has the id of its job (none in a composite action), the name of its step, the
        script text as YAML decodes it, and the line that text starts on. A run under defaults: or
        anywhere else GitHub does not run a script is not returned. Throws when the text is not
        YAML.
    #>
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $Content
    )

    foreach ($document in Read-YamlStream $Content) {
        Get-RunScript $document.RootNode
    }
}

function Get-EnvEntry($Root) {
    # Every entry of every env mapping that sets the env context: the workflow's, each job's and
    # each step's. An expression that builds the whole env from a ref or secret is returned with
    # Supported set to false, as the guard cannot see which names it sets.
    $envs = @(
        Get-MappingValue $Root 'env'
        foreach ($jobs in Get-MappingValue $Root 'jobs') {
            if ("$($jobs.NodeType)" -eq 'Mapping') {
                foreach ($job in $jobs.Children) { Get-MappingValue $job.Value 'env' }
            }
        }
        foreach ($step in Get-WorkflowStep $Root) { Get-MappingValue $step.Node 'env' }
    )
    foreach ($envNode in $envs) {
        if ("$($envNode.NodeType)" -eq 'Scalar') {
            $normalized = ConvertTo-DotNotation $envNode.Value
            if ($normalized -match $script:RefPattern -or $normalized -match $script:SecretPattern) {
                [pscustomobject]@{ Line = $envNode.Start.Line; Text = $envNode.Value; Supported = $false }
            }
            continue
        }
        if ("$($envNode.NodeType)" -ne 'Mapping') { continue }
        foreach ($entry in $envNode.Children) {
            if ("$($entry.Value.NodeType)" -eq 'Scalar') {
                [pscustomobject]@{ Name = $entry.Key.Value; Value = $entry.Value.Value; Supported = $true }
            }
        }
    }
}

function Get-MergeKey($Root) {
    # The line of every merge key (<<) in the document. A node an alias repeats is visited once.
    $visited = [System.Collections.Generic.HashSet[object]]::new([System.Collections.Generic.ReferenceEqualityComparer]::Instance)
    $pending = [System.Collections.Generic.Stack[object]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $node = $pending.Pop()
        if (-not $visited.Add($node)) { continue }
        if ("$($node.NodeType)" -eq 'Mapping') {
            foreach ($pair in $node.Children) {
                if ("$($pair.Key.NodeType)" -eq 'Scalar' -and $pair.Key.Value -eq '<<' -and "$($pair.Key.Style)" -eq 'Plain') {
                    $pair.Key.Start.Line
                }
                $pending.Push($pair.Key)
                $pending.Push($pair.Value)
            }
        }
        elseif ("$($node.NodeType)" -eq 'Sequence') {
            foreach ($item in $node.Children) { $pending.Push($item) }
        }
    }
}

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
        Matches github.<...>.ref, ref_name, head_ref, base_ref, tag_name, head_branch,
        target_commitish and workflow_ref in dot or bracket form, anywhere inside a ${{ }} expression (including
        one that spans lines or wraps the ref in a function call), and env.NAME where the same
        file defines env NAME, at any level, from such an expression; those findings have Problem
        Ref. Matches the secrets context, github.token and env.NAME defined from either the same way,
        with Problem Secret. Expressions in env:, with: and if: are not run scripts and are not matched. An env
        built by an expression from a ref or secret has Problem Unsupported, a merge key has Problem
        MergeKey, and a file that is not YAML has Problem InvalidYaml. A script text an alias repeats is reported once, where it is written.
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
        $relative = [System.IO.Path]::GetRelativePath($Root, $file) -replace '\\', '/'
        $content = Get-Content -LiteralPath $file -Raw
        if ($null -eq $content) { continue }
        try {
            $documents = Read-YamlStream $content
        }
        catch {
            # PowerShell wraps the YamlException, which carries where the parser stopped.
            $yamlError = $_.Exception.InnerException ?? $_.Exception
            [pscustomobject]@{
                File       = $relative
                Line       = if ($yamlError.PSObject.Properties['Start']) { $yamlError.Start.Line } else { 1 }
                Expression = $yamlError.Message
                Problem    = 'InvalidYaml'
            }
            continue
        }

        $reported = [System.Collections.Generic.HashSet[string]]::new()
        foreach ($document in $documents) {
            $entries = @(Get-EnvEntry $document.RootNode)
            $supported = @($entries | Where-Object Supported)
            $refAliasPattern = Get-EnvAliasPattern (Get-EnvAlias $supported $script:RefPattern)
            $secretAliasPattern = Get-EnvAliasPattern (Get-EnvAlias $supported $script:SecretPattern)

            $findings = @(
                foreach ($line in Get-MergeKey $document.RootNode) {
                    [pscustomobject]@{
                        File       = $relative
                        Line       = $line
                        Expression = '<<'
                        Problem    = 'MergeKey'
                    }
                }

                foreach ($unreadable in $entries | Where-Object { -not $_.Supported }) {
                    [pscustomobject]@{
                        File       = $relative
                        Line       = $unreadable.Line
                        Expression = $unreadable.Text.Trim()
                        Problem    = 'Unsupported'
                    }
                }

                foreach ($script in Get-RunScript $document.RootNode) {
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
            )
            $findings | Where-Object { $reported.Add("$($_.Line)`n$($_.Problem)`n$($_.Expression)") }
        }
    }
}

Export-ModuleMember -Function Get-WorkflowRunScript, Find-WorkflowRefInjection
