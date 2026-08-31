# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Runs the repeatable DMS-1317 DocumentCache qualification entrypoint.

.DESCRIPTION
    Runs the bounded query-plan/statistics guards that are safe for ordinary CI-style
    databases and writes a run summary scaffold under the requested results directory.
    For release validation, pass -RunExplicitWriterEvidence after PostgreSQL and SQL Server
    integration connection strings are configured; that adds the explicit writer
    performance evidence fixtures and attaches their output to the test result.

.EXAMPLE
    ./eng/performance/invoke-documentcache-qualification.ps1 -Provider postgresql,mssql `
        -ResultsDirectory C:\perf\document-cache

.EXAMPLE
    ./eng/performance/invoke-documentcache-qualification.ps1 -Provider postgresql `
        -ResultsDirectory C:\perf\document-cache -RunExplicitWriterEvidence
#>
[CmdletBinding()]
param(
    [ValidateSet('postgresql', 'mssql')]
    [string[]] $Provider = @('postgresql', 'mssql'),

    [Parameter(Mandatory = $true)]
    [string] $ResultsDirectory,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $RunExplicitWriterEvidence
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path -Path $PSScriptRoot -ChildPath '..' -AdditionalChildPath '..')).Path
$runId = 'document-cache-qualification-{0:yyyyMMdd-HHmmss}' -f (Get-Date)
$runDirectory = Join-Path -Path $ResultsDirectory -ChildPath $runId
New-Item -Path $runDirectory -ItemType Directory -Force | Out-Null

$commandsRun = [System.Collections.Generic.List[string]]::new()

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
        [switch] $Explicit
    )

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
        $runDirectory
    )

    if ($Explicit) {
        $arguments += @('--', 'NUnit.Explicit=true')
    }

    Invoke-CheckedCommand -Label $Label -FilePath 'dotnet' -ArgumentList $arguments
}

Invoke-DotnetTest `
    -Label 'document-cache-qualification-unit' `
    -Project 'src/dms/tests/EdFi.DataManagementService.Performance.Harness.Tests.Unit/EdFi.DataManagementService.Performance.Harness.Tests.Unit.csproj' `
    -Filter 'FullyQualifiedName~DocumentCacheQualification'

foreach ($providerName in $Provider) {
    switch ($providerName) {
        'postgresql' {
            Invoke-DotnetTest `
                -Label 'postgresql-document-cache-query-plan' `
                -Project 'src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration.csproj' `
                -Filter 'FullyQualifiedName~DocumentCacheQueryPlan'

            if ($RunExplicitWriterEvidence) {
                Invoke-DotnetTest `
                    -Label 'postgresql-document-cache-writer-performance-evidence' `
                    -Project 'src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration.csproj' `
                    -Filter 'FullyQualifiedName~DocumentCacheWriterPerformanceEvidence' `
                    -Explicit
            }
        }
        'mssql' {
            Invoke-DotnetTest `
                -Label 'mssql-document-cache-query-plan' `
                -Project 'src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/EdFi.DataManagementService.Backend.Mssql.Tests.Integration.csproj' `
                -Filter 'FullyQualifiedName~DocumentCacheQueryPlan'

            if ($RunExplicitWriterEvidence) {
                Invoke-DotnetTest `
                    -Label 'mssql-document-cache-writer-performance-evidence' `
                    -Project 'src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/EdFi.DataManagementService.Backend.Mssql.Tests.Integration.csproj' `
                    -Filter 'FullyQualifiedName~DocumentCacheWriterPerformanceEvidence' `
                    -Explicit
            }
        }
    }
}

$summaryPath = Join-Path -Path $runDirectory -ChildPath 'qualification-summary.md'
@(
    '# DocumentCache Qualification Run',
    '',
    "Run ID: $runId",
    "Configuration: $Configuration",
    "Providers: $($Provider -join ', ')",
    "Explicit writer evidence: $RunExplicitWriterEvidence",
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
    '',
    'Record representative-scale measured values against reference/document-cache/performance-qualification.md before release qualification.'
) | Set-Content -Path $summaryPath -Encoding UTF8

Write-Information "DocumentCache qualification summary: $summaryPath" -InformationAction Continue
