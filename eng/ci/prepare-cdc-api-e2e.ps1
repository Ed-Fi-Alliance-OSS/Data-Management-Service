# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param([string] $Repo, [string] $PrivateDirectory, [ValidateSet('Postgresql', 'Mssql')][string] $Provider,
    [string] $HandoffOutput)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $Repo 'eng/docker-compose/tests/cdc-fixture-inputs.psm1')
Set-Location $Repo
$sqlServer = $Provider -eq 'Mssql'
$engine = if ($sqlServer) { 'mssql' } else { 'postgresql' }
$image = if ($sqlServer) { $env:CDC_CONNECTOR_TEMPLATE_SQLSERVER_2025_IMAGE } else { $env:CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE }
$qualified = Get-Content (Join-Path $Repo 'src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcQualifiedWorkerImage.json') -Raw | ConvertFrom-Json
if ($env:CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE -cne $qualified.image) { throw 'Qualified worker required.' }
$inputs = New-CdcFixtureInput -Repo $Repo -FixtureRoot $PrivateDirectory -SqlServer $sqlServer -E2e $true `
    -ProviderImage $image -LargeWorker $true -AdditionalValues @{ CDC_CONNECT_IMAGE = $qualified.image }
# File settings own Compose interpolation, even when invoked from a developer shell.
foreach ($name in $inputs.Values.Keys) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
foreach ($item in @(Get-ChildItem Env: | Where-Object Name -like 'DMS_CDC__*')) { Remove-Item "Env:$($item.Name)" }
# Principal preparation needs only the provider. Starting CMS here would seed claims
# before the wrapper stages E2E fragments; CMS correctly preserves those initial rows.
& (Join-Path $Repo 'eng/docker-compose/start-local-dms.ps1') -DbOnly `
    -DatabaseEngine $engine -EnvironmentFile $inputs.EnvironmentFile
if ($LASTEXITCODE -ne 0) { throw 'Infrastructure failed.' }
Initialize-CdcFixturePrincipal -SqlServer $sqlServer -Inputs $inputs
# This small fixture input is consumed and completed by the shipped managed wrapper.
# Runbook settings/snippets deliberately remain in their own fixture.
$settings = Get-Content (Join-Path $inputs.Local 'dms-base.json') -Raw | ConvertFrom-Json -AsHashtable
$settings.AppSettings.Datastore = $engine
$settings.AppSettings.MultiTenancy = $false
$settings.AppSettings.RouteQualifierSegments = ''
$settings.DataManagement.DocumentCache.Targets = @(@{ DataStoreId = 1 })
$settings.Cdc = @{
    Provider = $(if ($sqlServer) { 'sqlserver' } else { 'postgresql' })
    DeploymentKey = 'local'; TenantKey = ''; DataStoreId = '1'; InstanceKey = 'datastore-1'; Generation = 1
    TopicPrefix = 'edfi'; PartitionCount = 3; Schemas = @()
    SetupPrincipal = $(if ($sqlServer) { 'sa' } else { 'postgres' }); DatabaseConnectorPrincipal = 'cdc_reader'
    SetupConnectionString = (Get-Content -LiteralPath $inputs.InputPath -Raw).Trim()
    ConnectEndpoint = 'http://127.0.0.1:8083'; WorkerMetricsEndpoint = 'http://127.0.0.1:9404/metrics'
    KafkaBootstrapServers = 'dms-kafka1:9092'; KafkaAdminBootstrapServers = '127.0.0.1:9092'
    MaxRecordBytes = 10000000; LagThresholdMilliseconds = 5000
    DurabilityProfile = 'LocalSingleBroker'; AuthorizationProfile = 'AuthorizationDisabledLocal'
    Worker = @{ Key = 'local-worker'; OffsetStorageTopic = 'dms-connect-offsets'; HeapBytes = 1073741824
        Principal = 'worker'; ConnectorPrincipal = 'connector'; AdministratorPrincipal = 'administrator' }
    Consumers = @(); KafkaClientSecurityProperties = @{}; KafkaAdminProperties = @{}
    ProviderConnectionProperties = @{
        'database.hostname' = $(if ($sqlServer) { 'dms-mssql' } else { 'dms-postgresql' })
        'database.port' = $(if ($sqlServer) { '1433' } else { '5432' })
        'database.user' = 'cdc_reader'; 'database.password' = '${env:CDC_DATABASE_PASSWORD}'
    }
    Compose = @{ File = Join-Path $Repo 'eng/docker-compose/kafka-cdc.yml'; EnvironmentFile = $inputs.EnvironmentFile
        Project = 'dms-local'; BrokerSizeOverrideFile = Join-Path $inputs.StatePath 'broker-size.json' }
    Timing = @{ CallMilliseconds = 120000; WaitMilliseconds = 600000; PollMilliseconds = 1000; MaximumObservationAgeMilliseconds = 60000 }
}
if ($sqlServer) {
    $settings.Cdc.ProviderConnectionProperties['database.names'] = $inputs.Database
    $settings.Cdc.ProviderConnectionProperties['driver.encrypt'] = 'true'
    $settings.Cdc.ProviderConnectionProperties['driver.trustServerCertificate'] = 'true'
} else { $settings.Cdc.ProviderConnectionProperties['database.dbname'] = $inputs.Database }
$null = New-Item -ItemType Directory -Path $inputs.StatePath
& chmod 700 $inputs.StatePath
$settings | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $inputs.SettingsPath
& chmod 600 $inputs.SettingsPath
if ($LASTEXITCODE -ne 0) { throw 'Cannot protect settings.' }
$handoff = & (Join-Path $Repo 'src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1') `
    -EnvironmentFile $inputs.EnvironmentFile -DatabaseEngine $engine -CdcSettingsPath $inputs.SettingsPath `
    -CdcBindingStatePath $inputs.StatePath -EnableKafkaCdc -CdcApiE2E
if ($LASTEXITCODE -ne 0 -or @($handoff).Count -ne 1 -or -not (Test-Path -LiteralPath $handoff -PathType Leaf)) { throw 'Admission/handoff failed.' }
$handoff | Set-Content -LiteralPath $HandoffOutput
