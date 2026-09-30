# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Test-only: bind selected marked invocations using the shipped parameter block. Never run
# arbitrary Markdown or a wrapper body here. Callers retain their existing controlled seams.
function Get-CdcRunbookCode {
    param([string] $Id, [string] $Markdown = '')
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    if (-not $Markdown) { $Markdown = Get-Content (Join-Path $repo 'reference/cdc-documentation/operations-runbook.md') -Raw }
    $start = [regex]::Escape("<!-- cdc-snippet: $Id -->")
    $end = [regex]::Escape("<!-- /cdc-snippet: $Id -->")
    if ([regex]::Matches($Markdown, $start).Count -ne 1 -or [regex]::Matches($Markdown, $end).Count -ne 1) {
        throw "Missing or duplicate CDC snippet: $Id"
    }
    $match = [regex]::Match($Markdown, "(?s)$start\s*``````powershell\r?\n(?<code>.*?)\r?\n``````\s*$end")
    if (-not $match.Success -or $match.Groups['code'].Value.Contains('```')) { throw "Invalid CDC snippet fence: $Id" }
    return $match.Groups['code'].Value
}

function Get-CdcRunbookInvocation {
    param([string] $Id, [string] $FixtureRoot, [string] $Markdown = '')
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    $code = Get-CdcRunbookCode -Id $Id -Markdown $Markdown
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($code, [ref]$null, [ref]$errors)
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

# Live callers use the same marked argument binding as the Contract fixtures, then run
# the shipped wrapper in a child process. stdout/stderr stay in the private fixture root.
# Nothing here implements provisioning, lifecycle, readiness, or connector mutation.
function Invoke-CdcRunbookLiveWrapper {
    param([string] $Id, [string] $FixtureRoot, [int] $TimeoutSeconds = 600)
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    Import-Module (Join-Path $repo 'eng/docker-compose/env-utility.psm1') -DisableNameChecking
    $invocation = Get-CdcRunbookInvocation -Id $Id -FixtureRoot $FixtureRoot
    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($value in @('-NoProfile', '-NonInteractive', '-File', (Join-Path $repo $invocation.Path))) { $arguments.Add($value) }
    foreach ($key in $invocation.Parameters.Keys) {
        $arguments.Add("-$key")
        $value = $invocation.Parameters[$key]
        if ($value -isnot [Management.Automation.SwitchParameter] -and $value -isnot [bool]) { $arguments.Add([string]$value) }
    }
    $phase = if ($Id -match 'bootstrap|e2e-setup') { 'Setup' } else { 'Test' }
    Set-CdcRunbookOperation -Operation $Id -Phase $phase -TimeoutSeconds $TimeoutSeconds
    $result = Invoke-NativeCommandWithInput -FilePath 'pwsh' -ArgumentList $arguments.ToArray() -InputText '' -TimeoutSeconds $TimeoutSeconds
    Complete-CdcRunbookOperation $result
    $prefix = Join-Path $FixtureRoot ($Id + '-' + [guid]::NewGuid().ToString('N'))
    $result.StandardOutput | Set-Content -LiteralPath "$prefix.stdout"
    $result.StandardError | Set-Content -LiteralPath "$prefix.stderr"
    if (-not $IsWindows) { & chmod 600 "$prefix.stdout" "$prefix.stderr" }
    return [pscustomobject]@{ SnippetId = $Id; ExitCode = $result.ExitCode; FailureKind = $result.FailureKind; LogPrefix = $prefix }
}

# Live fixture progress is private input to the allowlisted qualification exporter.
# Persist before invoking work so a killed child still has an attributable operation.
function Save-CdcRunbookProgress {
    if (-not $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY) { return }
    $path = Join-Path $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY 'runbook-progress.json'
    $temporary = "$path.tmp"
    try {
        $script:runbookProgress | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporary -ErrorAction Stop
        [IO.File]::Move($temporary, $path, $true)
    } catch {
        # Diagnostic collection must never replace an assertion/process failure.
        $script:runbookProgress.CollectionFailed = $true
    }
}

function Set-CdcRunbookOperation {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Records test-only progress in the private temporary diagnostic directory; never changes deployment state.')]
    param([string] $Operation, [ValidateSet('Setup', 'Test', 'Teardown')][string] $Phase = 'Test', [int] $TimeoutSeconds = 0)
    if (-not (Get-Variable runbookProgress -Scope Script -ErrorAction SilentlyContinue)) { return }
    if ((Get-Variable runbookOperation -Scope Script -ErrorAction SilentlyContinue) -and $script:runbookOperationCase -eq $script:runbookCase -and $script:runbookOperation.Status -eq 'Running') {
        $script:runbookOperation.Status = 'Completed'
        $script:runbookOperation.ElapsedMilliseconds = $script:runbookTimer.ElapsedMilliseconds
    }
    $script:runbookOperationCase = $script:runbookCase
    $script:runbookOperation = [ordered]@{ Operation = $Operation; Phase = $Phase; Status = 'Running' }
    if ($TimeoutSeconds -gt 0) { $script:runbookOperation.TimeoutSeconds = $TimeoutSeconds }
    if (-not $script:runbookProgress.Contains($script:runbookCase)) { $script:runbookProgress[$script:runbookCase] = @() }
    $script:runbookProgress[$script:runbookCase] += $script:runbookOperation
    $script:runbookTimer = [Diagnostics.Stopwatch]::StartNew()
    Save-CdcRunbookProgress
}

function Complete-CdcRunbookOperation {
    param($Result)
    if (-not (Get-Variable runbookOperation -Scope Script -ErrorAction SilentlyContinue)) { return }
    $script:runbookOperation.Status = 'Completed'
    $script:runbookOperation.ElapsedMilliseconds = $script:runbookTimer.ElapsedMilliseconds
    if ($null -ne $Result) {
        $script:runbookOperation.ExitCode = $Result.ExitCode
        $timedOut = $Result.PSObject.Properties['TimedOut'] -and $Result.TimedOut
        $script:runbookOperation.TimedOut = [bool]$timedOut
        $script:runbookOperation.FailureKind = if ($timedOut -and $Result.FailureKind -eq 'None') { 'Timeout' } else { $Result.FailureKind }
    }
    Save-CdcRunbookProgress
}
