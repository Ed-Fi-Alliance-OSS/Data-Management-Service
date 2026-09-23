# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Test-only: bind selected marked invocations using the shipped parameter block. Never run
# arbitrary Markdown or a wrapper body here. Callers retain their existing controlled seams.
function Get-CdcRunbookInvocation {
    param([string] $Id, [string] $FixtureRoot, [string] $Markdown = '')
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    if (-not $Markdown) { $Markdown = Get-Content (Join-Path $repo 'reference/cdc-documentation/operations-runbook.md') -Raw }
    $start = [regex]::Escape("<!-- cdc-snippet: $Id -->")
    $end = [regex]::Escape("<!-- /cdc-snippet: $Id -->")
    if ([regex]::Matches($Markdown, $start).Count -ne 1 -or [regex]::Matches($Markdown, $end).Count -ne 1) {
        throw "Missing or duplicate CDC snippet: $Id"
    }
    $match = [regex]::Match($Markdown, "(?s)$start\s*``````powershell\r?\n(?<code>.*?)\r?\n``````\s*$end")
    if (-not $match.Success -or $match.Groups['code'].Value.Contains('```')) { throw "Invalid CDC snippet fence: $Id" }
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($match.Groups['code'].Value, [ref]$null, [ref]$errors)
    if ($errors.Count) { throw "Invalid CDC snippet syntax: $Id" }
    $commands = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'pwsh' }, $true))
    if ($commands.Count -ne 1) { throw "Expected one wrapper invocation: $Id" }
    $elements = $commands[0].CommandElements
    $path = ($elements[1].SafeGetValue() -replace '^\./', '')
    if ($path -notin @('eng/docker-compose/bootstrap-local-dms.ps1', 'eng/docker-compose/bootstrap-published-dms.ps1',
        'eng/docker-compose/start-local-dms.ps1', 'src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1', 'build-dms.ps1')) {
        throw "Unsupported CDC wrapper: $Id"
    }
    $source = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo $path), [ref]$null, [ref]$null)
    $metadata = [Management.Automation.CommandMetadata]::new((Get-Command (Join-Path $repo $path)))
    $parameters = @{}
    for ($i = 2; $i -lt $elements.Count; $i++) {
        $element = $elements[$i]
        if ($element -isnot [Management.Automation.Language.CommandParameterAst]) {
            if ($path -eq 'build-dms.ps1' -and $i -eq 2 -and $element.SafeGetValue() -eq 'E2ETest') { $parameters.Command = 'E2ETest'; continue }
            throw "Unexpected positional wrapper argument: $Id"
        }
        $name = $element.ParameterName
        if ($parameters.ContainsKey($name) -or -not $metadata.Parameters.ContainsKey($name) -or $null -ne $element.Argument) { throw "Invalid wrapper argument: $name" }
        if ($metadata.Parameters[$name].ParameterType -eq [switch]) { $parameters[$name] = $true; continue }
        $i++
        if ($i -ge $elements.Count) { throw "Missing wrapper argument: $name" }
        $value = $elements[$i]
        if ($value.Extent.Text -in @("(Resolve-Path './eng/docker-compose/.env').Path", "(Resolve-Path './eng/docker-compose/.env.e2e').Path", '$environmentFile')) {
            $environmentName = if ($Id -like '*e2e*') { '.env.e2e' } else { '.env' }
            $expectedExpression = "(Resolve-Path './eng/docker-compose/$environmentName').Path"
            $expression = $value.Extent.Text
            if ($expression -eq '$environmentFile') {
                $assignments = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and
                    $n.Left.Extent.Text -eq '$environmentFile' }, $true))
                if ($assignments.Count -ne 1) { throw "Missing or ambiguous fixture environment: $Id" }
                $expression = $assignments[0].Right.Extent.Text
            }
            if ($expression -cne $expectedExpression) { throw "Undeclared fixture environment: $Id" }
            $resolved = Join-Path $FixtureRoot $environmentName
        }
        elseif ($value -is [Management.Automation.Language.StringConstantExpressionAst]) {
            $resolved = $value.SafeGetValue()
            # Declared fixture substitution: only the documented local settings/state prefix.
            if ($resolved.StartsWith('./.local/cdc/')) { $resolved = Join-Path $FixtureRoot $resolved.Substring(2) }
        }
        else { throw "Undeclared wrapper expression: $Id" }
        $parameters[$name] = $resolved
    }
    $binder = [scriptblock]::Create("[CmdletBinding()]`n" + $source.ParamBlock.Extent.Text + "`n" + 'return $PSBoundParameters')
    $bound = & $binder @parameters
    return @{ Path = $path; Parameters = $bound }
}
