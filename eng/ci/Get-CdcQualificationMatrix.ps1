# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

[CmdletBinding()]
param(
    [ValidateSet('All', 'Kafka', 'Postgresql', 'Mssql')]
    [string] $Lane = 'All',
    [ValidateSet('All', 'Admission', 'Lifecycle', 'Recovery', 'RecordSize', 'Telemetry', 'History')]
    [string] $Suite = 'All'
)

$ErrorActionPreference = 'Stop'
if ($Suite -ne 'All' -and $Lane -notin @('Postgresql', 'Mssql')) {
    throw 'A suite selection requires one provider lane.'
}
$jobs = @(
    if ($Lane -in @('All', 'Kafka')) { @{ lane = 'Kafka'; suite = 'All' } }
    foreach ($provider in @('Postgresql', 'Mssql')) {
        if ($Lane -notin @('All', $provider)) { continue }
        foreach ($phase in @('Admission', 'Lifecycle', 'Recovery', 'RecordSize', 'Telemetry', 'History')) {
            if ($Suite -eq 'All' -or $Suite -eq $phase) { @{ lane = $provider; suite = $phase } }
        }
    }
)
@{ include = $jobs } | ConvertTo-Json -Depth 3 -Compress
