# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Runs the repeatable DMS-1317 DocumentCache qualification entrypoint.

.DESCRIPTION
    Runs the bounded query-plan/statistics guards that are safe for ordinary CI-style
    databases and writes a run summary under the requested results directory.
    For release validation, pass -RunExplicitWriterEvidence after PostgreSQL and SQL Server
    integration connection strings are configured; that adds the explicit writer
    performance evidence fixtures and places their output under writer-contention-evidence/.
    Pass -RunRepresentative only on qualified performance targets to execute the long-running
    DocumentCacheRepresentativeQualification harness and validate the produced result
    artifacts. Pass -ValidateResults to validate an existing representative result directory
    without running bounded guards, explicit writer evidence, or representative benchmarks.

.EXAMPLE
    ./eng/performance/invoke-documentcache-qualification.ps1 -Provider postgresql,mssql `
        -ResultsDirectory C:\perf\document-cache

.EXAMPLE
    ./eng/performance/invoke-documentcache-qualification.ps1 -Provider postgresql `
        -ResultsDirectory C:\perf\document-cache -RunExplicitWriterEvidence

.EXAMPLE
    ./eng/performance/invoke-documentcache-qualification.ps1 -Provider postgresql,mssql `
        -ResultsDirectory C:\perf\document-cache -RunRepresentative -RunExplicitWriterEvidence `
        -OperatorMetricsFile C:\perf\document-cache\operator-cpu-io.json

.EXAMPLE
    ./eng/performance/invoke-documentcache-qualification.ps1 `
        -ValidateResults C:\perf\document-cache\document-cache-qualification-20260901-140000
#>
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'Run')]
    [ValidateSet('postgresql', 'mssql')]
    [string[]] $Provider = @('postgresql', 'mssql'),

    [Parameter(Mandatory = $true, ParameterSetName = 'Run')]
    [string] $ResultsDirectory,

    [Parameter(Mandatory = $true, ParameterSetName = 'Validate')]
    [string] $ValidateResults,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [Parameter(ParameterSetName = 'Run')]
    [switch] $RunRepresentative,

    [Parameter(ParameterSetName = 'Run')]
    [switch] $RunExplicitWriterEvidence,

    [Parameter(ParameterSetName = 'Run')]
    [string] $OperatorMetricsFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$commandsRun = [System.Collections.Generic.List[string]]::new()

$repoRoot = (Resolve-Path (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath '..')).Path
$validationToolProject = Join-Path -Path $repoRoot `
    -ChildPath 'src/dms/tests/EdFi.DataManagementService.Performance.Harness.Tools/EdFi.DataManagementService.Performance.Harness.Tools.csproj'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string] $FilePath,
        [Parameter(Mandatory = $true)][string[]] $ArgumentList
    )

    $renderedCommand = "$FilePath $($ArgumentList -join ' ')"
    $commandsRun.Add($renderedCommand)
    Write-Information "[$Label] $renderedCommand" -InformationAction Continue

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
}

function Invoke-DotnetTest {
    param(
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string] $Project,
        [Parameter(Mandatory = $true)][string] $Filter,
        [Parameter(Mandatory = $true)][string] $OutputDirectory,
        [switch] $Explicit
    )

    New-Item -Path $OutputDirectory -ItemType Directory -Force | Out-Null

    $arguments = @(
        'test',
        (Join-Path -Path $repoRoot -ChildPath $Project),
        '-c',
        $Configuration,
        '--filter',
        $Filter,
        '--logger',
        "trx;LogFileName=$($Label -replace '[^A-Za-z0-9_.-]', '_').trx",
        '--results-directory',
        $OutputDirectory
    )

    if ($Explicit) {
        $arguments += @('--', 'NUnit.Explicit=true')
    }

    Invoke-CheckedCommand -Label $Label -FilePath 'dotnet' -ArgumentList $arguments
}

function Invoke-DocumentCacheQualificationResultValidation {
    param([Parameter(Mandatory = $true)][string] $ResultDirectory)

    Invoke-CheckedCommand `
        -Label 'document-cache-qualification-result-validation' `
        -FilePath 'dotnet' `
        -ArgumentList @(
            'run',
            '--project',
            $validationToolProject,
            '-c',
            $Configuration,
            '--',
            'validate-results',
            $ResultDirectory
        )
}

if ($PSCmdlet.ParameterSetName -eq 'Validate') {
    $resolvedResults = (Resolve-Path -Path $ValidateResults).Path
    Invoke-DocumentCacheQualificationResultValidation -ResultDirectory $resolvedResults
    return
}

if ($RunRepresentative) {
    $effectiveOperatorMetricsFile = $OperatorMetricsFile
    if ([string]::IsNullOrWhiteSpace($effectiveOperatorMetricsFile)) {
        $effectiveOperatorMetricsFile = [Environment]::GetEnvironmentVariable('PERF_DOCUMENTCACHE_OPERATOR_METRICS_FILE')
    }

    if ([string]::IsNullOrWhiteSpace($effectiveOperatorMetricsFile)) {
        throw 'Representative DocumentCache qualification requires -OperatorMetricsFile or PERF_DOCUMENTCACHE_OPERATOR_METRICS_FILE with strict CPU/IO evidence.'
    }

    $OperatorMetricsFile = (Resolve-Path -Path $effectiveOperatorMetricsFile).Path
}

$runId = 'document-cache-qualification-{0:yyyyMMdd-HHmmss}' -f (Get-Date)
$runDirectory = Join-Path -Path $ResultsDirectory -ChildPath $runId
$queryPlanGuardsDirectory = Join-Path -Path $runDirectory -ChildPath 'query-plan-guards'
$writerContentionEvidenceDirectory = Join-Path -Path $runDirectory -ChildPath 'writer-contention-evidence'

New-Item -Path $runDirectory -ItemType Directory -Force | Out-Null
New-Item -Path $queryPlanGuardsDirectory -ItemType Directory -Force | Out-Null
New-Item -Path $writerContentionEvidenceDirectory -ItemType Directory -Force | Out-Null

Invoke-DotnetTest `
    -Label 'document-cache-qualification-unit' `
    -Project 'src/dms/tests/EdFi.DataManagementService.Performance.Harness.Tests.Unit/EdFi.DataManagementService.Performance.Harness.Tests.Unit.csproj' `
    -Filter 'FullyQualifiedName~DocumentCacheQualification' `
    -OutputDirectory $queryPlanGuardsDirectory

foreach ($providerName in $Provider) {
    switch ($providerName) {
        'postgresql' {
            Invoke-DotnetTest `
                -Label 'postgresql-document-cache-query-plan' `
                -Project 'src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration.csproj' `
                -Filter 'FullyQualifiedName~DocumentCacheQueryPlan' `
                -OutputDirectory $queryPlanGuardsDirectory

            if ($RunExplicitWriterEvidence) {
                Invoke-DotnetTest `
                    -Label 'postgresql-document-cache-writer-performance-evidence' `
                    -Project 'src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration.csproj' `
                    -Filter 'FullyQualifiedName~DocumentCacheWriterPerformanceEvidence' `
                    -OutputDirectory $writerContentionEvidenceDirectory `
                    -Explicit
            }
        }
        'mssql' {
            Invoke-DotnetTest `
                -Label 'mssql-document-cache-query-plan' `
                -Project 'src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/EdFi.DataManagementService.Backend.Mssql.Tests.Integration.csproj' `
                -Filter 'FullyQualifiedName~DocumentCacheQueryPlan' `
                -OutputDirectory $queryPlanGuardsDirectory

            if ($RunExplicitWriterEvidence) {
                Invoke-DotnetTest `
                    -Label 'mssql-document-cache-writer-performance-evidence' `
                    -Project 'src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/EdFi.DataManagementService.Backend.Mssql.Tests.Integration.csproj' `
                    -Filter 'FullyQualifiedName~DocumentCacheWriterPerformanceEvidence' `
                    -OutputDirectory $writerContentionEvidenceDirectory `
                    -Explicit
            }
        }
    }
}

if ($RunRepresentative) {
    $previousPerfResultsDirectory = [Environment]::GetEnvironmentVariable('PERF_RESULTS_DIR')
    $previousDocumentCacheProvider = [Environment]::GetEnvironmentVariable('PERF_DOCUMENTCACHE_PROVIDER')
    $previousOperatorMetricsFile = [Environment]::GetEnvironmentVariable('PERF_DOCUMENTCACHE_OPERATOR_METRICS_FILE')
    try {
        [Environment]::SetEnvironmentVariable('PERF_RESULTS_DIR', $runDirectory)
        [Environment]::SetEnvironmentVariable('PERF_DOCUMENTCACHE_OPERATOR_METRICS_FILE', $OperatorMetricsFile)
        foreach ($providerName in $Provider) {
            [Environment]::SetEnvironmentVariable('PERF_DOCUMENTCACHE_PROVIDER', $providerName)

            $providerCategory = switch ($providerName) {
                'postgresql' { 'PostgresqlIntegration' }
                'mssql' { 'MssqlIntegration' }
            }

            Invoke-DotnetTest `
                -Label "$providerName-document-cache-representative-qualification" `
                -Project 'src/dms/tests/EdFi.DataManagementService.Performance.Harness/EdFi.DataManagementService.Performance.Harness.csproj' `
                -Filter "Category=DocumentCacheRepresentativeQualification&Category=$providerCategory" `
                -OutputDirectory $runDirectory `
                -Explicit
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable('PERF_RESULTS_DIR', $previousPerfResultsDirectory)
        [Environment]::SetEnvironmentVariable('PERF_DOCUMENTCACHE_PROVIDER', $previousDocumentCacheProvider)
        [Environment]::SetEnvironmentVariable('PERF_DOCUMENTCACHE_OPERATOR_METRICS_FILE', $previousOperatorMetricsFile)
    }
}

$summaryPath = Join-Path -Path $runDirectory -ChildPath 'qualification-summary.md'
@(
    '# DocumentCache Qualification Run',
    '',
    "Run ID: $runId",
    "Configuration: $Configuration",
    "Providers: $($Provider -join ', ')",
    "Representative benchmark: $RunRepresentative",
    "Explicit writer evidence: $RunExplicitWriterEvidence",
    "Operator metrics file: $OperatorMetricsFile",
    '',
    '## Evidence Status',
    '',
    'Bounded guard output proves CI-sized query-plan/statistics checks only.',
    'Explicit writer evidence, when requested, is small targeted contention evidence only.',
    'Representative-scale qualification is measured evidence only when this summary is produced by -RunRepresentative and threshold-results.json validates.',
    '',
    '## Artifact Directories',
    '',
    "- Query-plan guards: ``$([IO.Path]::GetRelativePath($runDirectory, $queryPlanGuardsDirectory) -replace '\\', '/')/``",
    "- Writer contention evidence: ``$([IO.Path]::GetRelativePath($runDirectory, $writerContentionEvidenceDirectory) -replace '\\', '/')/``",
    '',
    '## Commands',
    '',
    ($commandsRun | ForEach-Object { "- ``$_``" }),
    '',
    '## Required Follow-Up Artifacts',
    '',
    '- threshold-results.json',
    '- query-plan-guards/',
    '- writer-contention-evidence/',
    '- outage-drain-evidence/',
    '- provider-metrics/postgresql-wal-vacuum-bloat.md',
    '- provider-metrics/mssql-log-ghost-index.md',
    '- provider-metrics/operator-cpu-io.json',
    '',
    'Record representative-scale measured values against reference/document-cache/performance-qualification.md before release qualification.'
) | Set-Content -Path $summaryPath -Encoding UTF8

if ($RunRepresentative) {
    Invoke-DocumentCacheQualificationResultValidation -ResultDirectory $runDirectory
}

Write-Information "DocumentCache qualification summary: $summaryPath" -InformationAction Continue
