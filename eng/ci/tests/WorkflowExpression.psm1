# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Shared by the workflow wiring tests: reads a job's if: condition from raw workflow text and
# evaluates it for one run, with no YAML parser, the way the tests that use it read workflows.

function Get-WorkflowJobCondition {
    <#
        .SYNOPSIS
            Returns the expression inside a job's single-line if: ${{ ... }}, or an empty string.
    #>
    param([string] $Job)

    return [regex]::Match($Job, '(?m)^    if: \$\{\{ (?<expression>.+) \}\}\r?$').Groups['expression'].Value
}

function Test-WorkflowExpression {
    <#
        .SYNOPSIS
            Evaluates a GitHub Actions expression for one run.
        .DESCRIPTION
            Each github.*, inputs.*, vars.* and needs.*.result name is read from Run, startsWith
            and the comparison operators become PowerShell with GitHub's case-insensitive string
            comparison, and !cancelled() is true. A name Run does not supply throws when
            evaluation reaches it, and any other token outside that vocabulary fails to parse, so a
            changed condition cannot pass by being ignored.
    #>
    param([string] $Expression, [hashtable] $Run)

    $translated = $Expression.Replace('!cancelled()', '$true')
    $translated = [regex]::Replace($translated, "startsWith\(([A-Za-z_.]+), '([^']*)'\)", '([string]$1).StartsWith(''$2'', [StringComparison]::OrdinalIgnoreCase)')
    # A name is looked up only when evaluation reaches it, so && and || short-circuit as in GitHub.
    $translated = [regex]::Replace($translated, '\b(?:github|inputs|vars|needs)\.[A-Za-z0-9_.-]*[A-Za-z0-9_]', "(& `$Read `$Run '`$0')")
    $translated = $translated.
        Replace('!=', '-ine').
        Replace('==', '-ieq').
        Replace('&&', '-and').
        Replace('||', '-or').
        Replace('!(', '-not (')
    # Bare true and false only; a quoted 'true' stays a string.
    $translated = [regex]::Replace($translated, "(?<![$'])\b(true|false)\b(?!')", '$$$1')
    $read = {
        param([hashtable] $Values, [string] $Name)
        if (-not $Values.ContainsKey($Name)) { throw "The expression reads $Name, which this run does not supply." }
        $Values[$Name]
    }
    try {
        return [bool](& ([scriptblock]::Create("param(`$Read, `$Run) $translated")) $read $Run)
    }
    catch {
        throw "Could not evaluate '$Expression': $($_.Exception.Message)"
    }
}

Export-ModuleMember -Function Get-WorkflowJobCondition, Test-WorkflowExpression
