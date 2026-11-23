# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

[CmdletBinding()]
param(
    [ValidateSet('All', 'Kafka', 'Postgresql', 'Mssql')]
    [string] $Lane = 'All',
    [ValidateSet('All', 'Admission', 'Lifecycle', 'Recovery', 'RecordSize', 'Telemetry', 'History', 'MessageContract', 'ApiE2E')]
    [string] $Suite = 'All'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'cdc-qualification.psm1') -Force
if ($Suite -ne 'All' -and $Lane -notin @('Postgresql', 'Mssql')) {
    throw 'A suite selection requires one provider lane.'
}
$mssqlNativeDocumentCache = [object]::Equals((Get-CdcQualificationWorkerImage)['mssqlNativeDocumentCache'], $true)
$jobs = @(
    if ($Lane -in @('All', 'Kafka')) { @{ lane = 'Kafka'; suite = 'All' } }
    foreach ($provider in @('Postgresql', 'Mssql')) {
        if ($Lane -notin @('All', $provider)) { continue }
        $suites = Get-CdcQualificationProviderSuite -Provider $provider -MssqlNativeDocumentCache $mssqlNativeDocumentCache
        if ($provider -eq 'Mssql' -and -not $mssqlNativeDocumentCache) {
            if ($Suite -ne 'All' -and -not $suites.Contains($Suite)) {
                throw "Mssql/$Suite requires a shipped worker qualified for native DocumentJson."
            }
            if ($Suite -in @('All', 'Admission', 'History', 'MessageContract')) {
                $notice = 'SQL Server native CDC admission success, managed lifecycle, recovery, record size, telemetry, live message contracts, API E2E and managed retirement are deferred pending a qualified worker. Admission verifies refusal and MessageContract runs serialized cases.'
                Write-Warning $notice
                if ($env:GITHUB_STEP_SUMMARY) { $notice >> $env:GITHUB_STEP_SUMMARY }
            }
        }
        foreach ($phase in $suites.Keys) {
            if ($Suite -eq 'All' -or $Suite -eq $phase) { @{ lane = $provider; suite = $phase } }
        }
    }
)
@{ include = $jobs } | ConvertTo-Json -Depth 3 -Compress
