# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Dot-sourced by cdc-qualification.psm1; this suite alone owns these accounting rules.
function New-CdcApiRunnerReport {
    <# .SYNOPSIS
    Creates initial suite accounting without performing setup or writing a fixture report.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure in-memory report construction.')]
    [CmdletBinding()]
    [OutputType([Collections.Specialized.OrderedDictionary])]
    param([string] $Provider)
    [ordered]@{ Name = "$Provider-ApiE2E"; InvocationId = [guid]::NewGuid().ToString('D'); Provider = $Provider
        Status = 'Running'; Reason = 'None'; CleanupFailure = 'None'; ExportFailure = 'None'; ReportFailure = 'None'
        Total = 8; Passed = 0; Failed = 0; Skipped = 0
        Stages = [ordered]@{ Setup = 'NotRun'; Test = 'NotRun'; Teardown = 'NotRun'; Export = 'NotRun' }
        ProcessResult = @{ Status = 'ReportUnavailable'; Total = 0; Passed = 0; Failed = 0; Skipped = 0 }
        ProcessExit = -1; BindingId = ''; Generation = 0; Cases = @() }
}

function Get-CdcApiScenarioReport {
    param([string] $Path, [Collections.IDictionary] $Runner, [Collections.IDictionary] $Process)
    $result = @{ Status = 'Failed'; Reason = 'ScenarioReportMissing'; Cases = @(); Attachment = 'NotRun'; Disposal = 'NotRun' }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $result }
    try {
        if ((Get-Item -LiteralPath $Path).Length -gt 32768) { throw 'Size' }
        $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -Depth 16
        $result.Reason = 'ScenarioReportInvalid'
        if ($report.Version -ne 1 -or $report.Scenarios -isnot [array] -or $report.Scenarios.Count -ne 8) { return $result }
        $outcomes = @('NotRun', 'Running', 'Passed', 'Failed')
        $failures = @('None', 'Error', 'Cancelled', 'TimedOut', 'Unimplemented')
        $expected = @(1..8 | ForEach-Object { 'CDC-E2E-{0:00}' -f $_ })
        foreach ($stage in @($report.Attachment, $report.Disposal) + $report.Scenarios) {
            if ($stage.Outcome -cnotin $outcomes -or $stage.Failure -cnotin $failures) { return $result }
        }
        if ($report.Attachment.Id -cne 'Attachment' -or $report.Disposal.Id -cne 'Disposal') { return $result }
        for ($i = 0; $i -lt 8; $i++) { if ($report.Scenarios[$i].Id -cne $expected[$i]) { return $result } }
        $result.Cases = @($report.Scenarios | ForEach-Object { @{ Id = $_.Id; Outcome = $_.Outcome; Failure = $_.Failure } })
        $result.Attachment = $report.Attachment.Outcome; $result.Disposal = $report.Disposal.Outcome
        $result.Reason = 'ScenarioIdentityMismatch'
        if ($report.InvocationId -cne $Runner.InvocationId -or $report.Identity.Provider -cne $Runner.Provider -or
            $Runner.BindingId -cnotmatch '^[a-f0-9]{64}$' -or $Runner.Generation -le 0 -or
            $report.Identity.BindingId -cne $Runner.BindingId -or $report.Identity.Generation -ne $Runner.Generation) { return $result }
        $result.Reason = 'ScenarioIncomplete'
        foreach ($stage in @($report.Attachment, $report.Disposal) + $report.Scenarios) {
            if ($stage.Outcome -cne 'Passed' -or $stage.Failure -cne 'None') { return $result }
        }
        $result.Reason = 'RunnerStageFailed'
        foreach ($name in @('Setup', 'Test', 'Teardown', 'Export')) {
            if ($Runner.Stages[$name] -cne 'Passed') { return $result }
        }
        $result.Reason = 'TestProcessFailed'
        if ($Runner.ProcessExit -ne 0 -or $Process.Status -cne 'Passed' -or $Process.Total -le 0 -or
            $Process.Passed -ne $Process.Total -or $Process.Failed -ne 0 -or $Process.Skipped -ne 0) { return $result }
        $result.Status = 'Passed'; $result.Reason = 'None'
    } catch { $result.Reason = 'ScenarioReportInvalid' }
    return $result
}

function Export-CdcApiEvidence {
    param([string] $Path, [string] $Destination, [Collections.IDictionary] $Runner)
    # Construct every published field, including partial reports, from bounded enums/identities.
    $validated = Get-CdcApiScenarioReport -Path $Path -Runner $Runner -Process $Runner.ProcessResult
    $safe = [ordered]@{ Version = 1; InvocationId = $Runner.InvocationId; Provider = $Runner.Provider
        BindingId = $Runner.BindingId; Generation = $Runner.Generation
        ReportValidation = $validated.Reason; Stages = $Runner.Stages; Attachment = $validated.Attachment; Disposal = $validated.Disposal; Scenarios = $validated.Cases }
    $safe | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Destination 'cdc-api-e2e.json')
}

function Invoke-CdcApiProcess {
    param([string] $FilePath, [string[]] $Arguments, [string] $LogPath, [int] $TimeoutSeconds)
    Import-Module (Join-Path $PSScriptRoot '../docker-compose/env-utility.psm1') -DisableNameChecking
    $result = Invoke-NativeCommandWithInput -FilePath $FilePath -ArgumentList $Arguments -InputText '' -TimeoutSeconds $TimeoutSeconds
    $result.StandardOutput | Set-Content -LiteralPath "$LogPath.stdout"
    $result.StandardError | Set-Content -LiteralPath "$LogPath.stderr"
    return $result
}

function Assert-CdcApiProcess {
    param($Result)
    if ($Result.FailureKind -ne 'None' -or $Result.ExitCode -ne 0) { throw 'CDC_API_PROCESS_FAILED' }
}

function Assert-CdcApiWorkspaceAbsent {
    param([string] $Repo, [string] $PrivateDirectory)
    if ($env:CDC_RUNBOOK_OWNED_STACK -ne '1' -or -not $IsLinux) { throw 'CDC_API_OWNERSHIP_REQUIRED' }
    foreach ($path in @('.bootstrap', '.cdc-deployments/dms-local.json', '.cdc-deployments/dms-published.json')) {
        if (Test-Path -LiteralPath (Join-Path $Repo "eng/docker-compose/$path")) { throw 'CDC_API_WORKSPACE_OCCUPIED' }
    }
    foreach ($project in @('dms-local', 'dms-published')) {
        foreach ($prefix in @(@('ps', '-aq'), @('volume', 'ls', '-q'), @('network', 'ls', '-q'))) {
            $probe = Invoke-CdcApiProcess -FilePath docker -Arguments ($prefix + @('--filter', "label=com.docker.compose.project=$project")) `
                -LogPath (Join-Path $PrivateDirectory "absence-$project-$($prefix[0])") -TimeoutSeconds 10
            Assert-CdcApiProcess $probe
            if (-not [string]::IsNullOrWhiteSpace($probe.StandardOutput)) { throw 'CDC_API_WORKSPACE_OCCUPIED' }
        }
    }
}

function Invoke-CdcApiQualification {
    param([string] $Repo, [string] $RawDirectory, [string] $Destination, [string] $Configuration,
        [Collections.IDictionary] $Report, [scriptblock] $Persist)
    $private = Join-Path $RawDirectory $Report.Name
    $null = New-Item -ItemType Directory -Path $private
    $scenarioPath = Join-Path $private 'scenario.json'
    $inputPath = Join-Path $private 'admitted.json'
    $handoffPath = Join-Path $private 'handoff-path.txt'
    $ownershipPath = Join-Path $private 'ownership.json'
    $owned = $false; $lock = $null
    $process = @{ Status = 'ReportUnavailable'; Total = 0; Passed = 0; Failed = 0; Skipped = 0 }
    $saved = @{}
    foreach ($name in @('DMS_SCHEMA_TOOL_PATH', 'DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK', 'CDC_API_E2E_HANDOFF_PATH',
        'CDC_API_E2E_INVOCATION_ID', 'CDC_API_E2E_REPORT_PATH', 'AppSettings__DataStoreDatabaseName', 'NODE_OPTIONS')) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    foreach ($item in @(Get-ChildItem Env: | Where-Object Name -like 'DMS_CDC__*')) {
        $saved[$item.Name] = $item.Value
        Remove-Item "Env:$($item.Name)"
    }
    try {
        $Report.Stages.Setup = 'Running'; & $Persist
        $deadline = [DateTimeOffset]::UtcNow.AddMinutes(30)
        # Serialize cooperating runner invocations before establishing absence/cleanup authority.
        $lock = [IO.File]::Open((Join-Path $Repo 'eng/docker-compose/.cdc-api-e2e.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
        Assert-CdcApiWorkspaceAbsent -Repo $Repo -PrivateDirectory $private
        # 30 minutes shared by builds/preparation/admission, 50 for test+fixture finalization,
        # then a fresh 15-minute cleanup budget. Workflow must reserve at least 100 minutes.
        foreach ($build in @($Configuration, 'Debug') | Select-Object -Unique) {
            $result = Invoke-CdcApiProcess -FilePath dotnet -Arguments @('build',
                (Join-Path $Repo 'src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj'),
                '-c', $build, '--nologo') -LogPath (Join-Path $private "build-$build") -TimeoutSeconds ([Math]::Max(1, [int]($deadline - [DateTimeOffset]::UtcNow).TotalSeconds))
            Assert-CdcApiProcess $result
        }
        $env:DMS_SCHEMA_TOOL_PATH = Join-Path $Repo 'src/dms/clis/EdFi.DataManagementService.SchemaTools/bin/Debug/net10.0/api-schema-tools'
        $env:DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK = 'false'
        Import-Module (Join-Path $Repo 'eng/docker-compose/bootstrap-schema-tool.psm1')
        if ((Resolve-DmsSchemaTool -RequestedPath $env:DMS_SCHEMA_TOOL_PATH) -cne $env:DMS_SCHEMA_TOOL_PATH -or
            (Resolve-DmsSchemaTool) -cne $env:DMS_SCHEMA_TOOL_PATH) { throw 'CDC_API_TOOL_MISMATCH' }
        # The marker is written before infrastructure can partially start; ownership is not
        # inferred from a missing inventory. Private marker survives process termination.
        @{ InvocationId = $Report.InvocationId; Project = 'dms-local'; AbsentBeforeStart = $true } |
            ConvertTo-Json | Set-Content -LiteralPath $ownershipPath
        $owned = $true
        $result = Invoke-CdcApiProcess -FilePath pwsh -Arguments @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'prepare-cdc-api-e2e.ps1'),
            '-Repo', $Repo, '-PrivateDirectory', $private, '-Provider', $Report.Provider, '-HandoffOutput', $handoffPath) `
            -LogPath (Join-Path $private 'setup') -TimeoutSeconds ([Math]::Max(1, [int]($deadline - [DateTimeOffset]::UtcNow).TotalSeconds))
        Assert-CdcApiProcess $result
        $env:CDC_API_E2E_HANDOFF_PATH = (Get-Content -LiteralPath $handoffPath -Raw).Trim()
        $result = Invoke-CdcApiProcess -FilePath dotnet -Arguments @('run', '--file', (Join-Path $PSScriptRoot 'CdcApiInputs.cs'),
            '--', $env:CDC_API_E2E_HANDOFF_PATH, $inputPath) -LogPath (Join-Path $private 'inputs') `
            -TimeoutSeconds ([Math]::Max(1, [int]($deadline - [DateTimeOffset]::UtcNow).TotalSeconds))
        Assert-CdcApiProcess $result
        $inputs = Get-Content -LiteralPath $inputPath -Raw | ConvertFrom-Json -AsHashtable
        if ($inputs.Provider -cne $Report.Provider -or $inputs.BindingId -cnotmatch '^[a-f0-9]{64}$' -or $inputs.Generation -le 0 -or -not $inputs.Database) { throw 'CDC_API_INPUTS_INVALID' }
        $Report.BindingId = $inputs.BindingId; $Report.Generation = $inputs.Generation
        $Report.Stages.Setup = 'Passed'; $Report.Stages.Test = 'Running'; & $Persist
        $env:CDC_API_E2E_INVOCATION_ID = $Report.InvocationId
        $env:CDC_API_E2E_REPORT_PATH = $scenarioPath
        $env:AppSettings__DataStoreDatabaseName = $inputs.Database
        Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue
        $result = Invoke-CdcApiProcess -FilePath dotnet -Arguments @('test',
            (Join-Path $Repo 'src/dms/tests/EdFi.DataManagementService.Tests.E2E/EdFi.DataManagementService.Tests.E2E.csproj'),
            '-c', $Configuration, '--nologo', '--filter', 'FullyQualifiedName~Given_CdcApiE2E',
            '--results-directory', $private, '--logger', 'trx;LogFileName=api.trx', '--logger', 'console;verbosity=quiet') `
            -LogPath (Join-Path $private 'test') -TimeoutSeconds 3000
        $Report.ProcessExit = $result.ExitCode
        $process = Get-CdcQualificationReport -Path (Join-Path $private 'api.trx') -ExitCode $result.ExitCode
        foreach ($key in @('Status', 'Total', 'Passed', 'Failed', 'Skipped')) { $Report.ProcessResult[$key] = $process[$key] }
        Assert-CdcApiProcess $result
        if ($process.Status -ne 'Passed') { throw 'CDC_API_TEST_FAILED' }
        $Report.Stages.Test = 'Passed'
    } catch {
        $_ | Out-String | Set-Content -LiteralPath (Join-Path $private 'failure.log')
        if ($Report.Stages.Setup -ne 'Passed') { $Report.Stages.Setup = 'Failed'; $Report.Reason = 'PrerequisiteFailed' }
        else { $Report.Stages.Test = 'Failed'; $Report.Reason = 'ScenarioExecutionFailed' }
    } finally {
        try {
            $Report.Stages.Teardown = 'Running'
            try { & $Persist } catch { $Report.ReportFailure = 'ReportPersistenceFailed' }
            if ($owned) {
                $result = Invoke-CdcApiProcess -FilePath pwsh -Arguments @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'cleanup-cdc-api-e2e.ps1'),
                    '-Repo', $Repo, '-PrivateDirectory', $private, '-Provider', $Report.Provider, '-InvocationId', $Report.InvocationId) `
                    -LogPath (Join-Path $private 'cleanup') -TimeoutSeconds 900
                Assert-CdcApiProcess $result
            }
            $Report.Stages.Teardown = 'Passed'
        } catch { $Report.Stages.Teardown = 'Failed'; $Report.CleanupFailure = 'CleanupFailed' }
        try {
            $Report.Stages.Export = 'Running'
            try { & $Persist } catch { $Report.ReportFailure = 'ReportPersistenceFailed' }
            $exportInput = Join-Path $private 'runner-export.json'
            $Report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $exportInput
            $result = Invoke-CdcApiProcess -FilePath pwsh -Arguments @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'export-cdc-api-e2e.ps1'),
                '-RawDirectory', $private, '-Destination', (Join-Path $Destination $Report.Name), '-RunnerPath', $exportInput) `
                -LogPath (Join-Path $private 'export') -TimeoutSeconds 120
            Assert-CdcApiProcess $result
            $Report.Stages.Export = 'Passed'
        } catch { $Report.Stages.Export = 'Failed'; $Report.ExportFailure = 'ExportFailed' }
        $validation = Get-CdcApiScenarioReport -Path $scenarioPath -Runner $Report -Process $process
        $Report.Cases = $validation.Cases
        $Report.Passed = @($Report.Cases | Where-Object Outcome -ceq 'Passed').Count
        $Report.Failed = 8 - $Report.Passed
        $Report.Status = if ($Report.Stages.Setup -ne 'Passed') { 'EnvironmentUnavailable' } else { $validation.Status }
        if ($validation.Attachment -eq 'Failed') {
            $Report.Status = 'EnvironmentUnavailable'
            if ($Report.Reason -in @('None', 'ScenarioExecutionFailed')) { $Report.Reason = 'AttachmentFailed' }
        }
        if ($Report.ReportFailure -ne 'None') { $Report.Status = 'Failed' }
        if ($Report.Reason -eq 'None') { $Report.Reason = $validation.Reason }
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
        if ($null -ne $lock) { $lock.Dispose() }
        & $Persist
    }
}
