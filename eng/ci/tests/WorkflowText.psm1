# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Shared by the workflow wiring tests: reads a job, its needs, its steps and a step's run script from
# raw workflow text, with no YAML parser, and reads a run script with the PowerShell parser, so line
# breaks and backtick continuations in a call do not change what a test checks.

function Get-WorkflowJobText {
    <#
        .SYNOPSIS
            Returns a job's lines under jobs:, from its header up to the next job or top-level key.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $Workflow,

        [Parameter(Mandatory)]
        [string]
        $Id
    )

    $lines = $Workflow -split '\r?\n'
    $start = -1
    $inJobs = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -cmatch '^jobs:[ \t]*(?:#.*)?$') { $inJobs = $true; continue }
        if ($inJobs -and $lines[$i] -cmatch "^  $([regex]::Escape($Id)):[ \t]*(?:#.*)?$") { $start = $i; break }
    }
    if ($start -lt 0) {
        throw "Job $Id was not found."
    }
    $end = $start + 1
    while ($end -lt $lines.Count -and $lines[$end] -notmatch '^ {0,2}\S') { $end++ }
    return ($lines[$start..($end - 1)] -join "`n")
}

function Get-WorkflowJobNeed {
    <#
        .SYNOPSIS
            Returns the jobs a job needs, as written.
        .DESCRIPTION
            Reads the inline form (needs: a), the one-line bracket form (needs: [a, b]) and the list
            form, ignoring comment lines.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $Job
    )

    $bracket = [regex]::Match($Job, '(?m)^    needs:[ \t]*\[(.*?)\][ \t]*$')
    if ($bracket.Success) {
        return @(
            $bracket.Groups[1].Value -split ',' |
                ForEach-Object { $_.Trim() } |
                Where-Object { $_ }
        )
    }

    $inline = [regex]::Match($Job, '(?m)^    needs:[ \t]+(\S.*?)\s*$')
    if ($inline.Success) { return @($inline.Groups[1].Value) }

    $list = [regex]::Match($Job, '(?m)^    needs:[ \t]*\r?\n((?:      - .+\r?\n?|\s*#.*\r?\n?)+)')
    if (-not $list.Success) { return @() }
    return @(
        $list.Groups[1].Value -split '\r?\n' |
            Where-Object { $_ -match '^      - (\S+)' } |
            ForEach-Object { $Matches[1] }
    )
}

function Get-WorkflowJobStep {
    <#
        .SYNOPSIS
            Returns each step of a job, from its '      - ' line up to the next one.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $Job
    )

    $steps = [Collections.Generic.List[string]]::new()
    $inSteps = $false
    foreach ($line in $Job -split '\r?\n') {
        if ($line -cmatch '^    steps:[ \t]*(?:#.*)?$') { $inSteps = $true; continue }
        if (-not $inSteps) { continue }
        # A key at the job's own level ends the steps; a comment there does not.
        if ($line -match '^ {0,4}[^ #]') { break }
        if ($line.StartsWith('      - ')) { $steps.Add($line) }
        elseif ($steps.Count -gt 0) { $steps[$steps.Count - 1] += "`n$line" }
    }
    return $steps.ToArray()
}

function Get-WorkflowStepRun {
    <#
        .SYNOPSIS
            Returns the block scalar under a step's run: key, without its indentation.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $Step
    )

    $run = [Collections.Generic.List[string]]::new()
    $inRun = $false
    foreach ($line in $Step -split '\r?\n') {
        if ($line -cmatch '^        run: \|[ \t]*$') { $inRun = $true; continue }
        if (-not $inRun) { continue }
        if ($line -match '^[ \t]*$') { $run.Add(''); continue }
        if (-not $line.StartsWith('          ')) { break }
        $run.Add($line.Substring(10))
    }
    return ($run -join "`n")
}

function Get-PowerShellScriptAst {
    <#
        .SYNOPSIS
            Parses a script with the PowerShell parser, and throws when it does not parse.
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $Script
    )

    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($Script, [ref] $null, [ref] $errors)
    if (@($errors).Count -gt 0) {
        throw "The script does not parse as PowerShell: $($errors[0].Message)"
    }
    return $ast
}

function Get-PowerShellCommand {
    <#
        .SYNOPSIS
            Returns every call of a command, by its exact name, in a parsed script.
    #>
    param(
        [Parameter(Mandatory)]
        [Management.Automation.Language.Ast]
        $Ast,

        [Parameter(Mandatory)]
        [string]
        $Name
    )

    $commandName = $Name
    return @($Ast.FindAll({
                param($node)
                $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq $commandName
            }, $true))
}

function Get-PowerShellCommandArgument {
    <#
        .SYNOPSIS
            Returns each named parameter of a call, mapped to the element that is its argument.
    #>
    param(
        [Parameter(Mandatory)]
        [Management.Automation.Language.CommandAst]
        $Command
    )

    $arguments = @{}
    $elements = $Command.CommandElements
    for ($i = 1; $i -lt $elements.Count; $i++) {
        $parameter = $elements[$i]
        if ($parameter -isnot [Management.Automation.Language.CommandParameterAst]) { continue }
        $value = $parameter.Argument
        if ($null -eq $value -and $i + 1 -lt $elements.Count -and $elements[$i + 1] -isnot [Management.Automation.Language.CommandParameterAst]) {
            $i++
            $value = $elements[$i]
        }
        $arguments[$parameter.ParameterName] = $value
    }
    return $arguments
}

function ConvertTo-ReleaseAssetText {
    <#
        .SYNOPSIS
            Returns each hashtable in a Publish-ReleaseAsset -Asset argument as 'Name|Path|Comparison'.
        .DESCRIPTION
            Each value is as written, less the quotes of a constant. Throws when a hashtable has keys
            other than exactly Name, Path and Comparison.
    #>
    param(
        [Parameter(Mandatory)]
        [Management.Automation.Language.Ast]
        $List
    )

    foreach ($table in $List.FindAll({ param($node) $node -is [Management.Automation.Language.HashtableAst] }, $true)) {
        $values = @{}
        foreach ($pair in $table.KeyValuePairs) {
            $expression = if ($pair.Item2 -is [Management.Automation.Language.PipelineAst]) { $pair.Item2.GetPureExpression() }
            $values[$pair.Item1.Extent.Text] = if ($expression -is [Management.Automation.Language.StringConstantExpressionAst]) { $expression.Value } else { $pair.Item2.Extent.Text }
        }
        $keys = @($values.Keys | Sort-Object) -join ','
        if ($keys -cne 'Comparison,Name,Path') {
            throw "A release asset names $keys; each must name exactly its Name, Path and Comparison."
        }
        "$($values['Name'])|$($values['Path'])|$($values['Comparison'])"
    }
}

Export-ModuleMember -Function Get-WorkflowJobText, Get-WorkflowJobNeed, Get-WorkflowJobStep, Get-WorkflowStepRun,
    Get-PowerShellScriptAst, Get-PowerShellCommand, Get-PowerShellCommandArgument, ConvertTo-ReleaseAssetText
