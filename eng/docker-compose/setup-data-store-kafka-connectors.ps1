# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Sets up Debezium connectors for topic-per-data-store architecture in E2E tests

.DESCRIPTION
    This script creates individual Debezium connectors for each data store,
    enabling topic-per-data-store message segregation. Each connector:
    - Watches a specific data store database
    - Publishes to data-store-specific topics (e.g., edfi.dms.{DataStoreId}.document)
    - Maintains separate replication slots and publications

.PARAMETER EnvironmentFile
    Path to the environment file containing configuration (default: ./.env)

.PARAMETER DataStores
    Array of data store configurations. Each data store should have:
    - DataStoreId: Numeric ID for the data store
    - DatabaseName: Name of the PostgreSQL database for this data store

.EXAMPLE
    $dataStores = @(
        @{ DataStoreId = 1; DatabaseName = "edfi_datamanagementservice_d255901_sy2024" },
        @{ DataStoreId = 2; DatabaseName = "edfi_datamanagementservice_d255901_sy2025" },
        @{ DataStoreId = 3; DatabaseName = "edfi_datamanagementservice_d255902_sy2024" }
    )
    .\setup-data-store-kafka-connectors.ps1 -DataStores $dataStores
#>

[CmdletBinding()]
param (
    [Parameter(Mandatory = $false)]
    [string]
    $EnvironmentFile = "./.env",

    [Parameter(Mandatory = $true)]
    [array]
    $DataStores
)

function IsReady([string] $Url) {
    $maxAttempts = 12
    $attempt = 0
    $waitTime = 5

    Write-Information -MessageData "Checking if Kafka Connect is ready at $Url..." -InformationAction Continue

    while ($attempt -lt $maxAttempts) {
        try {
            $response = Invoke-RestMethod -Uri $Url -Method Get -TimeoutSec 5
            Write-Information -MessageData "Kafka Connect is ready!" -InformationAction Continue
            return $true
        }
        catch {
            $attempt++
            Write-Information -MessageData "Attempt $attempt/$maxAttempts - Kafka Connect not ready yet: $($_.Exception.Message)" -InformationAction Continue
            if ($attempt -lt $maxAttempts) {
                Write-Information -MessageData "Waiting $waitTime seconds before retry..." -InformationAction Continue
                Start-Sleep -Seconds $waitTime
            }
        }
    }

    Write-Information -MessageData "Kafka Connect did not become ready within expected time" -InformationAction Continue
    return $false
}

function Initialize-DataStoreConnector {
    param (
        [int]$DataStoreId,
        [string]$DatabaseName,
        [string]$ConnectBaseUrl,
        [string]$PostgresPassword,
        [string]$TemplateContent
    )

    $connectorName = "postgresql-source-datastore-$DataStoreId"
    $connectorUrl = "$ConnectBaseUrl/$connectorName"

    Write-Information -MessageData "`n========================================"
    Write-Information -MessageData "Setting up connector for data store $DataStoreId"
    Write-Information -MessageData "  Database: $DatabaseName"
    Write-Information -MessageData "  Topic: edfi.dms.$DataStoreId.document"
    Write-Information -MessageData "========================================"

    # Check if connector already exists and delete it
    try {
        $existingConnector = Invoke-RestMethod -Uri $connectorUrl -Method Get -SkipHttpErrorCheck -ErrorAction SilentlyContinue

        if ($null -ne $existingConnector.name) {
            Write-Information -MessageData "Deleting existing connector: $connectorName" -InformationAction Continue
            Invoke-RestMethod -Method Delete -Uri $connectorUrl -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        }
    }
    catch {
        # Connector doesn't exist, which is fine
        Write-Information -MessageData "No existing connector found (expected for first run)" -InformationAction Continue
    }

    # Generate connector configuration from template
    $connectorConfig = $TemplateContent
    $connectorConfig = $connectorConfig.Replace("{{DATASTORE_ID}}", $DataStoreId)
    $connectorConfig = $connectorConfig.Replace("{{DATABASE_NAME}}", $DatabaseName)
    $connectorConfig = $connectorConfig.Replace("{{POSTGRES_PASSWORD}}", $PostgresPassword)

    # Install the connector
    try {
        Write-Information -MessageData "Installing connector configuration..."
        $response = Invoke-RestMethod -Method Post -Uri $ConnectBaseUrl -ContentType "application/json" -Body $connectorConfig

        Write-Information -MessageData "Connector installed successfully!"
        Write-Information -MessageData "  Connector Name: $($response.name)"
        Write-Information -MessageData "  Topic Prefix: edfi.dms.$DataStoreId"

        # Wait a moment for connector to initialize
        Start-Sleep -Seconds 2

        # Check connector status
        $status = Invoke-RestMethod -Method Get -Uri "$connectorUrl/status" -SkipHttpErrorCheck
        if ($status.connector.state -eq "RUNNING") {
            Write-Information -MessageData "  Status: RUNNING" -InformationAction Continue
        }
        else {
            Write-Information -MessageData "  Status: $($status.connector.state)" -InformationAction Continue
            if ($status.connector.trace) {
                Write-Information -MessageData "  Error: $($status.connector.trace)" -InformationAction Continue
            }
        }
    }
    catch {
        Write-Information -MessageData "Failed to install connector: $($_.Exception.Message)" -InformationAction Continue
        Write-Information -MessageData "Error details:" -InformationAction Continue
        Write-Information -MessageData $_.Exception -InformationAction Continue
        throw
    }
}

# Main execution
Write-Information -InformationAction Continue -MessageData @"

========================================
Data Store Kafka Connectors Setup
========================================
Environment File: $EnvironmentFile
Number of data stores: $($DataStores.Count)
========================================

"@

# Read .env file
Import-Module ./env-utility.psm1 -Force
$envFile = ReadValuesFromEnvFile $EnvironmentFile

$sourcePort = $envFile["CONNECT_SOURCE_PORT"]
if ([string]::IsNullOrEmpty($sourcePort)) {
    $sourcePort = "8083"
    Write-Information -MessageData "Using default Kafka Connect port: $sourcePort" -InformationAction Continue
}

$postgresPassword = $envFile["POSTGRES_PASSWORD"]
if ([string]::IsNullOrEmpty($postgresPassword)) {
    Write-Information -MessageData "ERROR: POSTGRES_PASSWORD not found in environment file" -InformationAction Continue
    exit 1
}

$connectBaseUrl = "http://localhost:$sourcePort/connectors"

# Check if Kafka Connect is ready
if (-not (IsReady $connectBaseUrl)) {
    Write-Information -MessageData "`nERROR: Kafka Connect is not available at $connectBaseUrl" -InformationAction Continue
    Write-Information -MessageData "Please ensure:" -InformationAction Continue
    Write-Information -MessageData "  1. Docker containers are running (docker ps)" -InformationAction Continue
    Write-Information -MessageData "  2. kafka-postgresql-source container is healthy" -InformationAction Continue
    Write-Information -MessageData "  3. Port $sourcePort is accessible" -InformationAction Continue
    exit 1
}

# Load connector template
$templatePath = "./data_store_connector_template.json"
if (-not (Test-Path $templatePath)) {
    Write-Information -MessageData "`nERROR: Connector template not found at: $templatePath" -InformationAction Continue
    exit 1
}

$templateContent = Get-Content $templatePath -Raw
Write-Information -MessageData "`nLoaded connector template from: $templatePath" -InformationAction Continue

# Setup connector for each data store
$successCount = 0
$failureCount = 0

foreach ($dataStore in $DataStores) {
    try {
        Initialize-DataStoreConnector `
            -DataStoreId $dataStore.DataStoreId `
            -DatabaseName $dataStore.DatabaseName `
            -ConnectBaseUrl $connectBaseUrl `
            -PostgresPassword $postgresPassword `
            -TemplateContent $templateContent

        $successCount++
    }
    catch {
        Write-Information -MessageData "`nFailed to setup connector for data store $($dataStore.DataStoreId)"
        Write-Information -MessageData $_.Exception.Message
        $failureCount++
    }
}

# Summary
Write-Information -MessageData "`n========================================" -InformationAction Continue
Write-Information -MessageData "Setup Complete" -InformationAction Continue
Write-Information -MessageData "========================================" -InformationAction Continue
Write-Information -MessageData "Successful: $successCount" -InformationAction Continue
Write-Information -MessageData "Failed: $failureCount" -InformationAction Continue

# List all connectors
Write-Information -MessageData "`nActive Connectors:" -InformationAction Continue
try {
    $allConnectors = Invoke-RestMethod -Uri $connectBaseUrl -Method Get
    foreach ($connector in $allConnectors) {
        $connectorStatus = Invoke-RestMethod -Uri "$connectBaseUrl/$connector/status" -Method Get
        Write-Information -MessageData "  - $connector : $($connectorStatus.connector.state)" -InformationAction Continue
    }
}
catch {
    Write-Information -MessageData "Could not retrieve connector list: $($_.Exception.Message)" -InformationAction Continue
}

Write-Information -MessageData "`nTo verify topics were created, run:" -InformationAction Continue
Write-Information -MessageData "  docker exec dms-kafka1 /opt/kafka/bin/kafka-topics.sh --list --bootstrap-server localhost:9092" -InformationAction Continue

if ($failureCount -gt 0) {
    Write-Information -MessageData "`nWARNING: Some connectors failed to setup. Check the errors above." -InformationAction Continue
    exit 1
}

Write-Information -MessageData "`nAll data store connectors configured successfully!" -InformationAction Continue
