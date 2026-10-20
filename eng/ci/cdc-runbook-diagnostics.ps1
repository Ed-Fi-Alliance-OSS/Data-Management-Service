# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Only fixed metadata crosses the private Pester/process boundary. Never serialize
# ErrorRecord messages, assertion values, command text, arguments or host paths.
function ConvertTo-CdcRunbookWorkerDiagnostic {
    param([Collections.IDictionary] $Value)
    $safe = [ordered]@{}
    foreach ($field in @('Status', 'Stage', 'Health')) {
        $allowed = switch ($field) {
            Status { @('Completed', 'NotFound', 'Ambiguous', 'CommandFailed', 'TimedOut', 'OutputLimit', 'CollectionFailed') }
            Stage { @('Discovery', 'Inspect', 'Logs') }
            Health { @('none', 'starting', 'healthy', 'unhealthy') }
        }
        if ($Value[$field] -is [string] -and $Value[$field] -cin $allowed) { $safe[$field] = $Value[$field] }
    }
    foreach ($field in @('Exists', 'Running', 'Restarting', 'Paused', 'OomKilled', 'JavaOutOfMemory', 'BrokerConnectionFailure', 'UnknownLogSignature')) {
        if ($Value[$field] -is [bool]) { $safe[$field] = $Value[$field] }
    }
    foreach ($field in @('ExitCode', 'CommandExitCode', 'RestartCount')) {
        if ($Value[$field] -is [int] -or $Value[$field] -is [long]) {
            if ($field -ne 'RestartCount' -or $Value[$field] -ge 0) { $safe[$field] = $Value[$field] }
        }
    }
    return $safe
}

function ConvertTo-CdcRunbookDiagnostic {
    param([Collections.IDictionary] $Value, [switch] $Child)
    $safe = [ordered]@{}
    foreach ($field in @('Phase', 'Category', 'Status', 'FailureKind')) {
        $allowed = switch ($field) {
            Phase { @('Setup', 'Test', 'Teardown', 'Export') }
            Category { @('AssertionFailed', 'PesterFailure', 'BlockFailed', 'ContainerFailed', 'ExportFailed', 'CollectionFailed', 'ChildProcessFailure') }
            Status { @('Running', 'Completed') }
            FailureKind { @('None', 'StartFailure', 'StdinFailure', 'Timeout', 'TerminationFailure', 'OutputFailure', 'WaitFailure') }
        }
        if ($Value[$field] -is [string] -and $Value[$field] -cin $allowed) { $safe[$field] = $Value[$field] }
    }
    # Operations are repository-owned snippet names or fixed fixture steps.
    $snippets = Get-Content (Join-Path $PSScriptRoot '../../reference/cdc-documentation/operations-runbook.md') -Raw
    $operations = @([regex]::Matches($snippets, '<!-- cdc-snippet: (cdc-[a-z0-9-]+) -->') | ForEach-Object { $_.Groups[1].Value }) +
        @('ownership-check', 'fixture-inputs', 'provider-principal', 'verify-deployment', 'verify-claims', 'verify-capture', 'verify-snapshot', 'image-evidence', 'retire-workspace')
    if ($Value['Operation'] -is [string] -and $Value['Operation'] -cin $operations) { $safe.Operation = $Value.Operation }
    foreach ($field in @('Line', 'ElapsedMilliseconds', 'TimeoutSeconds', 'ExitCode')) {
        $number = $Value[$field]
        if (($number -is [int] -or $number -is [long]) -and
            ($field -eq 'ExitCode' -or $number -ge 0)) { $safe[$field] = $number }
    }
    if ($Value['TimedOut'] -is [bool]) { $safe.TimedOut = $Value.TimedOut }
    $sources = @('eng/docker-compose/tests/RunbookSetup.Live.Tests.ps1',
        'eng/docker-compose/tests/cdc-runbook-snippets.ps1', 'eng/docker-compose/tests/cdc-runbook-lifecycle.ps1',
        'eng/ci/tests/CdcQualification.Tests.ps1',
        'src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1') +
        @(Get-ChildItem (Join-Path $PSScriptRoot '../docker-compose') -File | Where-Object {
            $_.Extension -in @('.ps1', '.psm1')
        } | ForEach-Object { 'eng/docker-compose/' + $_.Name })
    if ($Value['Source'] -is [string] -and $Value['Source'] -cin $sources) { $safe.Source = $Value.Source }
    if ($Value['ErrorCategory'] -is [string] -and $Value['ErrorCategory'] -cin [Enum]::GetNames([Management.Automation.ErrorCategory])) {
        $safe.ErrorCategory = $Value.ErrorCategory
    }
    $exceptionTypes = @('System.Management.Automation.RuntimeException', 'System.Management.Automation.ActionPreferenceStopException',
        'System.IO.IOException', 'System.IO.FileNotFoundException', 'System.IO.DirectoryNotFoundException',
        'System.UnauthorizedAccessException', 'System.TimeoutException', 'System.Net.Http.HttpRequestException',
        'System.InvalidOperationException', 'System.ArgumentException', 'System.Text.Json.JsonException')
    if ($Value['ExceptionType'] -is [string] -and $Value['ExceptionType'] -cin $exceptionTypes) {
        $safe.ExceptionType = $Value.ExceptionType
    }
    if ($Value['HResult'] -is [int] -or $Value['HResult'] -is [long]) { $safe.HResult = $Value.HResult }
    $reasons = @('InventoryInvalid', 'ScopeMismatch', 'ContradictoryScope', 'ContradictoryComposeInputs',
        'ChangedSelectedComposeFile', 'ChangedConfiguration', 'ChangedEnvironment',
        'CleanupInventoryInvalid', 'ProtectedState', 'PeerConfiguration', 'ChangedGeneratedConfiguration',
        'CleanupFlushFailed', 'ControllerRejected', 'UnverifiedStop', 'WorkerNotReady', 'UnverifiedCleanup')
    if ($Value['Reason'] -is [string] -and $Value['Reason'] -cin $reasons) { $safe.Reason = $Value.Reason }
    if ($Value['ControllerOperation'] -is [string] -and $Value['ControllerOperation'] -cin @('stop', 'start', 'start-worker', 'retire')) {
        $safe.ControllerOperation = $Value.ControllerOperation
    }
    if ($Value['ControllerExitCode'] -is [int] -or $Value['ControllerExitCode'] -is [long]) { $safe.ControllerExitCode = $Value.ControllerExitCode }
    if ($Value['ControllerFailureCodes'] -is [array]) {
        $codes = @($Value.ControllerFailureCodes | Where-Object {
            $_ -is [string] -and $_ -cmatch '^(Request|WorkflowState|Projection|ProviderSetup|Kafka|Connect|Worker|Metrics|WriterPublication)/(InvalidInput|Unavailable|Timeout|AuthenticationFailed|Conflict|ValidationFailed)\z'
        } | Select-Object -Unique -First 16)
        if ($codes.Count -gt 0) { $safe.ControllerFailureCodes = $codes }
    }
    if (-not $Child -and $Value.Contains('ChildFailures')) {
        $safe.ChildFailures = @($Value.ChildFailures | Select-Object -First 8 | Where-Object {
            $_ -is [Collections.IDictionary]
        } | ForEach-Object { ConvertTo-CdcRunbookDiagnostic $_ -Child })
    }
    return $safe
}

function Get-CdcRunbookErrorDiagnostic {
    param($Record, [string] $Category = 'PesterFailure', [string] $Phase = 'Test')
    $value = [ordered]@{ Phase = $Phase; Category = $Category }
    if ($Record.FullyQualifiedErrorId -eq 'PesterAssertionFailed') { $value.Category = 'AssertionFailed' }
    if ($null -ne $Record.CategoryInfo) { $value.ErrorCategory = [string]$Record.CategoryInfo.Category }
    if ($null -ne $Record.Exception) {
        $value.ExceptionType = $Record.Exception.GetType().FullName
        $value.HResult = $Record.Exception.HResult
        # Exact repository-owned guard text maps to fixed codes; never export arbitrary messages.
        $value.Reason = switch -CaseSensitive ($Record.Exception.Message) {
            'Inventory' { 'InventoryInvalid' }
            'Scope' { 'ScopeMismatch' }
            'Contradictory scope' { 'ContradictoryScope' }
            'Contradictory Compose inputs' { 'ContradictoryComposeInputs' }
            'Changed selected Compose file' { 'ChangedSelectedComposeFile' }
            'Changed configuration' { 'ChangedConfiguration' }
            'Changed environment' { 'ChangedEnvironment' }
            'Cleanup inventory' { 'CleanupInventoryInvalid' }
            'Protected state' { 'ProtectedState' }
            'Peer configuration' { 'PeerConfiguration' }
            'Changed generated configuration' { 'ChangedGeneratedConfiguration' }
            'Cleanup flush' { 'CleanupFlushFailed' }
            'Controller rejected' { 'ControllerRejected' }
            'Unverified stop' { 'UnverifiedStop' }
            'Not ready' { 'WorkerNotReady' }
            'Unverified cleanup' { 'UnverifiedCleanup' }
        }
    }
    # Stack frames locate Should failures at the call site, not inside Pester.
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).Replace('\', '/')
    $trace = [string]$Record.ScriptStackTrace
    foreach ($frame in $trace.Replace('\', '/') -split '\r?\n') {
        if ($frame -match ([regex]::Escape($repo) + '/(?<source>(?:eng|src)/[^:]+\.psm?1): line (?<line>\d+)\s*$')) {
            $value.Source = $Matches.source; $value.Line = [int]$Matches.line
            $safe = ConvertTo-CdcRunbookDiagnostic $value
            if ($safe.Contains('Source')) {
                $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo $safe.Source), [ref]$null, [ref]$null)
                $line = $safe.Line
                $hook = $ast.Find({ param($node)
                    $node -is [Management.Automation.Language.CommandAst] -and
                    $node.GetCommandName() -in @('BeforeAll', 'BeforeEach', 'AfterEach', 'AfterAll') -and
                    $node.Extent.StartLineNumber -le $line -and $node.Extent.EndLineNumber -ge $line
                }, $true)
                if ($null -ne $hook) { $safe.Phase = if ($hook.GetCommandName() -like 'After*') { 'Teardown' } else { 'Setup' } }
                return $safe
            }
        }
    }
    return ConvertTo-CdcRunbookDiagnostic $value
}

function Get-CdcRunbookBlockFailure {
    param($Block)
    foreach ($errorRecord in @($Block.ErrorRecord)) {
        if ($null -ne $errorRecord) { Get-CdcRunbookErrorDiagnostic $errorRecord -Category BlockFailed -Phase Setup }
    }
    foreach ($child in @($Block.Blocks)) { Get-CdcRunbookBlockFailure $child }
}

# Opt-in child-side collection. The caller rethrows its original error unchanged.
# Only metadata is written; diagnostic errors never replace the setup failure.
function Write-CdcRunbookChildFailure {
    param([object[]] $Records, [ValidateSet('Setup', 'Test', 'Teardown')][string] $Phase = 'Setup',
        [Collections.IDictionary] $Details = @{})
    if (-not $env:CDC_RUNBOOK_CHILD_FAILURE_PATH) { return }
    try {
        $path = [IO.Path]::GetFullPath($env:CDC_RUNBOOK_CHILD_FAILURE_PATH)
        $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
        if ($path.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { return }
        # Preserve an inner failure recorded before a lifecycle catch replaces its message.
        $failures = @()
        if (Test-Path -LiteralPath $path) {
            try {
                $existing = Get-Content -LiteralPath $path -Raw -ErrorAction Stop |
                    ConvertFrom-Json -AsHashtable -NoEnumerate -ErrorAction Stop
                $failures = @($existing | Where-Object { $_ -is [Collections.IDictionary] } |
                    ForEach-Object { ConvertTo-CdcRunbookDiagnostic $_ -Child })
            } catch {
                # A damaged prior file must not prevent the current failure from being recorded.
                $failures = @()
            }
        }
        $failures += @($Records | Select-Object -First 8 | ForEach-Object {
            $diagnostic = Get-CdcRunbookErrorDiagnostic $_ -Category ChildProcessFailure -Phase $Phase
            foreach ($field in @('ControllerOperation', 'ControllerExitCode', 'ControllerFailureCodes')) {
                if ($Details.Contains($field)) { $diagnostic[$field] = $Details[$field] }
            }
            ConvertTo-CdcRunbookDiagnostic $diagnostic -Child
        })
        $failures = @($failures | Select-Object -First 8)
        ConvertTo-Json -InputObject $failures -Depth 5 | Set-Content -LiteralPath $path -ErrorAction Stop
    } catch {
        # The existing parent process outcome remains authoritative.
        return
    }
}
