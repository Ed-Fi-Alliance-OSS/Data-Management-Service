# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

[CmdletBinding()]
param(
    [ValidateSet('All', 'Contract', 'Postgresql', 'Mssql', 'Kafka')]
    [string] $Lane = 'All',
    [ValidateSet('All', 'Admission', 'Lifecycle', 'Recovery', 'RecordSize', 'Telemetry', 'History')]
    [string] $Suite = 'All',
    [string] $ResultsDirectory = 'TestResults/cdc-qualification',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $PullImages
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Import-Module (Join-Path $PSScriptRoot 'cdc-qualification.psm1') -Force
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$destination = [IO.Path]::GetFullPath($ResultsDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Use a new results directory; previous evidence must not be overwritten.' }
New-Item -ItemType Directory -Path $destination | Out-Null
$raw = Join-Path ([IO.Path]::GetTempPath()) ('cdc-qualification-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $raw | Out-Null
if (-not $IsWindows) { & chmod 700 $raw }
$reports = [System.Collections.Generic.List[object]]::new()
$script:qualificationConfiguration = $Configuration
$oldLocation = Get-Location
$savedEnvironment = @{}
foreach ($name in @('CDC_CONNECTOR_TEMPLATE_FAIL_FAST', 'CDC_CONNECTOR_TEMPLATE_KEEP_CONTAINERS', 'CDC_ARTIFACT_CLEANUP_FAIL_FAST', 'CDC_CLEANUP_POSTGRESQL_ADMIN', 'CDC_CLEANUP_MSSQL_ADMIN', 'MSBUILDDISABLENODEREUSE', 'NODE_OPTIONS', 'TMPDIR')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$env:CDC_CONNECTOR_TEMPLATE_FAIL_FAST = 'true'
$env:CDC_CONNECTOR_TEMPLATE_KEEP_CONTAINERS = 'false'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:TMPDIR = Join-Path $raw 'temp'
New-Item -ItemType Directory -Path $env:TMPDIR | Out-Null
if (-not $IsWindows) { & chmod 700 $env:TMPDIR }
Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue

function Invoke-QualificationSuite {
    param([string] $Name, [string] $Project, [string] $Filter = '')
    Write-Output "CDC qualification: $Name"
    $suiteDirectory = Join-Path $raw $Name
    New-Item -ItemType Directory -Path $suiteDirectory | Out-Null
    $arguments = @('test', $Project, '-c', $script:qualificationConfiguration, '--nologo', '--results-directory', $suiteDirectory,
        '--logger', "trx;LogFileName=$Name.trx", '--logger', 'console;verbosity=quiet')
    if ($Filter) { $arguments += @('--filter', $Filter) }
    & dotnet @arguments *> (Join-Path $suiteDirectory 'private.log')
    $report = Get-CdcQualificationReport -Path (Join-Path $suiteDirectory "$Name.trx") -ExitCode $LASTEXITCODE
    $report.Name = $Name
    $reports.Add($report)
    Export-CdcQualificationEvidence -RawDirectory $suiteDirectory -Destination (Join-Path $destination $Name)
    Write-Output "$Name`: $($report.Status), total=$($report.Total), passed=$($report.Passed), failed=$($report.Failed), skipped=$($report.Skipped)"
}

try {
    Set-Location $repo
    if ($Suite -ne 'All' -and $Lane -notin @('Postgresql', 'Mssql')) { throw 'A suite selection requires one provider lane.' }
    $lanes = if ($Lane -eq 'All') { @('Contract', 'Postgresql', 'Mssql', 'Kafka') } else { @($Lane) }
    if (@($lanes | Where-Object { $_ -ne 'Contract' }).Count -gt 0) {
        $required = @('CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE')
        if ('Postgresql' -in $lanes -or 'Mssql' -in $lanes) { $required += 'CDC_CONNECTOR_TEMPLATE_REDPANDA_IMAGE' }
        if ('Postgresql' -in $lanes) {
            $required += 'CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE'
            if ($Suite -in @('All', 'History')) { $required += 'ConnectionStrings__DatabaseConnection' }
        }
        if ('Mssql' -in $lanes) {
            $required += 'CDC_CONNECTOR_TEMPLATE_SQLSERVER_2025_IMAGE'
            if ($Suite -in @('All', 'History')) { $required += 'ConnectionStrings__MssqlAdmin' }
        }
        foreach ($name in $required) {
            if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
                throw "EnvironmentUnavailable: required $name is missing."
            }
        }
        $qualified = Get-Content 'src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcQualifiedWorkerImage.json' -Raw | ConvertFrom-Json
        if ($env:CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE -cne $qualified.image) {
            throw 'EnvironmentUnavailable: Connect must use the shipped qualified exporter image digest.'
        }
        $brokerSource = Get-Content 'src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcComposeBrokerSizeDeployment.cs' -Raw
        $broker = [regex]::Match($brokerSource, '"(apache/kafka:[^"]+@sha256:[a-f0-9]{64})"').Groups[1].Value
        if (-not $broker) { throw 'EnvironmentUnavailable: pinned Kafka image unavailable.' }
        & docker info --format '{{.ServerVersion}}' *> (Join-Path $raw 'docker.log')
        if ($LASTEXITCODE -ne 0) { throw 'EnvironmentUnavailable: Docker is unavailable.' }
        $images = @($broker) + @($required | Where-Object { $_ -like '*_IMAGE' } | ForEach-Object { [Environment]::GetEnvironmentVariable($_) })
        foreach ($image in $images | Select-Object -Unique) {
            if ($PullImages) {
                & docker pull $image *> (Join-Path $raw 'pull.log')
                if ($LASTEXITCODE -ne 0) { throw 'EnvironmentUnavailable: required image pull failed.' }
            }
            & docker image inspect $image --format '{{.Id}}' *> (Join-Path $raw 'inspect.log')
            if ($LASTEXITCODE -ne 0) { throw 'EnvironmentUnavailable: required image is unavailable locally.' }
        }
        $qualified | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $destination 'qualified-image.json')
    }
    $backend = 'src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/EdFi.DataManagementService.Backend.Cdc.Tests.Integration.csproj'
    foreach ($selected in $lanes) {
        if ($selected -eq 'Contract') {
            Invoke-QualificationSuite -Name 'controller-unit' -Project 'src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/EdFi.DataManagementService.Backend.Cdc.Tests.Unit.csproj'
            Invoke-QualificationSuite -Name 'cli-unit' -Project 'src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Unit/EdFi.DataManagementService.SchemaTools.Tests.Unit.csproj' -Filter 'FullyQualifiedName~Cdc'
            Invoke-QualificationSuite -Name 'controller-offline' -Project $backend -Filter 'Category!=DatabaseIntegration'
            Import-Module Pester -MinimumVersion 5.7.1
            $config = New-PesterConfiguration
            $config.Run.Path = @('eng/docker-compose/tests/Cdc*.Tests.ps1', 'eng/ci/tests/CdcQualification.Tests.ps1')
            $config.Run.PassThru = $true
            $config.Output.Verbosity = 'Detailed'
            & { $script:qualificationPesterResult = Invoke-Pester -Configuration $config } *> (Join-Path $raw 'pester-private.log')
            $result = $script:qualificationPesterResult
            $success = $result.TotalCount -gt 0 -and $result.PassedCount -eq $result.TotalCount -and
                $result.FailedContainersCount -eq 0 -and $result.FailedBlocksCount -eq 0
            $reports.Add([ordered]@{ Name = 'wrappers'; Status = $(if ($success) { 'Passed' } else { 'Failed' });
                Total = $result.TotalCount; Passed = $result.PassedCount; Failed = $result.FailedCount; Skipped = $result.SkippedCount })
            Write-Output "wrappers: total=$($result.TotalCount), passed=$($result.PassedCount), failed=$($result.FailedCount), skipped=$($result.SkippedCount)"
        }
        elseif ($selected -eq 'Kafka') {
            Invoke-QualificationSuite -Name 'kafka-secured' -Project $backend -Filter 'Category=CdcControllerKafkaPolicy&Category=CdcAuthorizationEnabled'
            Invoke-QualificationSuite -Name 'kafka-local' -Project $backend -Filter 'Category=CdcControllerKafkaPolicy&Category=CdcAuthorizationDisabledLocal'
        }
        else {
            $categories = [ordered]@{
                Admission = 'CdcControllerAdmission'; Lifecycle = 'CdcControllerManagedLifecycle'
                Recovery = 'CdcControllerNativeRecovery'; RecordSize = 'CdcControllerRecordSize'
                Telemetry = 'CdcConnectorTelemetryQualification'; History = 'CdcPublicationHistory'
            }
            foreach ($phase in $categories.Keys) {
                if ($Suite -ne 'All' -and $Suite -ne $phase) { continue }
                $project = $backend
                $name = "$selected-$($categories[$phase])"
                if ($phase -eq 'Telemetry') { $name = "$selected-telemetry" }
                if ($phase -eq 'History') {
                    $name = "$selected-history"
                    $project = 'src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration.csproj'
                }
                Invoke-QualificationSuite -Name $name -Project $project -Filter "Category=$($categories[$phase])&Category=$($selected)Integration"
                if ($phase -eq 'History') {
                    $env:CDC_ARTIFACT_CLEANUP_FAIL_FAST = 'true'
                    if ($selected -eq 'Postgresql') { $env:CDC_CLEANUP_POSTGRESQL_ADMIN = $env:ConnectionStrings__DatabaseConnection }
                    else { $env:CDC_CLEANUP_MSSQL_ADMIN = $env:ConnectionStrings__MssqlAdmin }
                    Invoke-QualificationSuite -Name "$selected-provider-cleanup" -Project $backend -Filter "Category=CdcArtifactCleanup&Category=$($selected)Integration"
                }
            }
        }
    }
}
catch {
    $_ | Out-String | Set-Content (Join-Path $raw 'runner-private.log')
    $status = if ($_.Exception.Message -like 'EnvironmentUnavailable:*') { 'EnvironmentUnavailable' } else { 'RunnerFailed' }
    # Do not serialize exceptions; dependency tools may put credentials or document values in them.
    $reason = if ($status -eq 'EnvironmentUnavailable') { $_.Exception.Message } else { 'See the private runner log.' }
    $reports.Add([ordered]@{ Name = 'prerequisites-or-runner'; Status = $status; Reason = $reason; Total = 0; Passed = 0; Failed = 0; Skipped = 0 })
    Write-Output "CDC qualification: $status. $reason"
}
finally {
    $reports | ConvertTo-Json -Depth 10 -AsArray | Set-Content (Join-Path $destination 'qualification.json')
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
    Set-Location $oldLocation
    Write-Output "Private local diagnostic logs: $raw (never upload this directory)."
}
if ($reports.Count -eq 0 -or @($reports | Where-Object Status -ne 'Passed').Count -gt 0) { exit 1 }
