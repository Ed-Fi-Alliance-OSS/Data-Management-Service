# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

[CmdletBinding()]
param(
    [string] $ComposeFile = (Join-Path $PSScriptRoot '../docker-compose/kafka-broker.compose.yml'),
    [string] $BrokerSourceFile = (Join-Path $PSScriptRoot '../../src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcComposeBrokerSizeDeployment.cs')
)

$ErrorActionPreference = 'Stop'
# Qualification uses the branch's shipped broker, independently of environment overrides.
$compose = Get-Content -LiteralPath $ComposeFile -Raw
$source = Get-Content -LiteralPath $BrokerSourceFile -Raw
$image = [regex]::Match($compose, '(?m)^\s+image:\s+(apache/kafka:[^\s]+@sha256:[a-f0-9]{64})\s*$').Groups[1].Value
$authority = [regex]::Match($source, 'internal const string BrokerImage\s*=\s*"([^"]+)";').Groups[1].Value
if (-not $image -or $image -cne $authority) {
    throw 'EnvironmentUnavailable: CDC qualification requires matching digest-pinned Apache Kafka images in Compose and CdcComposeBrokerSizeDeployment.BrokerImage.'
}
$image
