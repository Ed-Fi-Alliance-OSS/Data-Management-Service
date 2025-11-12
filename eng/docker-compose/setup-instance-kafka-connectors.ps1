# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Sets up Debezium connectors for topic-per-instance architecture in E2E tests

.DESCRIPTION
    This script creates individual Debezium connectors for each DMS instance,
    enabling topic-per-instance message segregation. Each connector:
    - Watches a specific instance database
    - Publishes to instance-specific topics (e.g., edfi.dms.{instanceId}.document)
    - Maintains separate replication slots and publications

.PARAMETER EnvironmentFile
    Path to the environment file containing configuration (default: ./.env)

.PARAMETER Instances
    Array of instance configurations. Each instance should have:
    - InstanceId: Numeric ID for the instance
    - DatabaseName: Name of the PostgreSQL database for this instance

.EXAMPLE
    $instances = @(
        @{ InstanceId = 1; DatabaseName = "edfi_datamanagementservice_d255901_sy2024" },
        @{ InstanceId = 2; DatabaseName = "edfi_datamanagementservice_d255901_sy2025" },
        @{ InstanceId = 3; DatabaseName = "edfi_datamanagementservice_d255902_sy2024" }
    )
    .\setup-instance-kafka-connectors.ps1 -Instances $instances
#>

[CmdletBinding()]
param (
    [Parameter(Mandatory = $false)]
    [string]
    $EnvironmentFile = "./.env",

    [Parameter(Mandatory = $true)]
    [array]
    $Instances
)

function IsReady([string] $Url) {
    $maxAttempts = 12
    $attempt = 0
    $waitTime = 5

    Write-Host "Checking if Kafka Connect is ready at $Url..." -ForegroundColor Cyan

    while ($attempt -lt $maxAttempts) {
        try {
            $response = Invoke-RestMethod -Uri $Url -Method Get -TimeoutSec 5
            Write-Host "Kafka Connect is ready!" -ForegroundColor Green
            return $true
        }
        catch {
            $attempt++
            Write-Host "Attempt $attempt/$maxAttempts - Kafka Connect not ready yet: $($_.Exception.Message)" -ForegroundColor Yellow
            if ($attempt -lt $maxAttempts) {
                Write-Host "Waiting $waitTime seconds before retry..." -ForegroundColor Yellow
                Start-Sleep -Seconds $waitTime
            }
        }
    }

    Write-Host "Kafka Connect did not become ready within expected time" -ForegroundColor Red
    return $false
}

function Setup-InstanceConnector {
    param (
        [int]$InstanceId,
        [string]$DatabaseName,
        [string]$ConnectBaseUrl,
        [string]$PostgresPassword,
        [string]$TemplateContent
    )

    $connectorName = "postgresql-source-instance-$InstanceId"
    $connectorUrl = "$ConnectBaseUrl/$connectorName"

    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host "Setting up connector for Instance $InstanceId" -ForegroundColor Cyan
    Write-Host "  Database: $DatabaseName" -ForegroundColor Gray
    Write-Host "  Topic: edfi.dms.$InstanceId.document" -ForegroundColor Gray
    Write-Host "========================================" -ForegroundColor Cyan

    # Check if connector already exists and delete it
    try {
        $existingConnector = Invoke-RestMethod -Uri $connectorUrl -Method Get -SkipHttpErrorCheck -ErrorAction SilentlyContinue

        if ($null -ne $existingConnector.name) {
            Write-Host "Deleting existing connector: $connectorName" -ForegroundColor Yellow
            Invoke-RestMethod -Method Delete -Uri $connectorUrl -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        }
    }
    catch {
        # Connector doesn't exist, which is fine
        Write-Host "No existing connector found (expected for first run)" -ForegroundColor Gray
    }

    # Generate connector configuration from template
    $connectorConfig = $TemplateContent
    $connectorConfig = $connectorConfig.Replace("{{INSTANCE_ID}}", $InstanceId)
    $connectorConfig = $connectorConfig.Replace("{{DATABASE_NAME}}", $DatabaseName)
    $connectorConfig = $connectorConfig.Replace("{{POSTGRES_PASSWORD}}", $PostgresPassword)

    # Install the connector
    try {
        Write-Host "Installing connector configuration..." -ForegroundColor Cyan
        $response = Invoke-RestMethod -Method Post -Uri $ConnectBaseUrl -ContentType "application/json" -Body $connectorConfig

        Write-Host "Connector installed successfully!" -ForegroundColor Green
        Write-Host "  Connector Name: $($response.name)" -ForegroundColor Gray
        Write-Host "  Topic Prefix: edfi.dms.$InstanceId" -ForegroundColor Gray

        # Wait a moment for connector to initialize
        Start-Sleep -Seconds 2

        # Check connector status
        $status = Invoke-RestMethod -Method Get -Uri "$connectorUrl/status" -SkipHttpErrorCheck
        if ($status.connector.state -eq "RUNNING") {
            Write-Host "  Status: RUNNING ✓" -ForegroundColor Green
        }
        else {
            Write-Host "  Status: $($status.connector.state)" -ForegroundColor Yellow
            if ($status.connector.trace) {
                Write-Host "  Error: $($status.connector.trace)" -ForegroundColor Red
            }
        }
    }
    catch {
        Write-Host "Failed to install connector: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "Error details:" -ForegroundColor Red
        Write-Host $_.Exception -ForegroundColor Red
        throw
    }
}

# Main execution
Write-Host @"

========================================
Instance Kafka Connectors Setup
========================================
Environment File: $EnvironmentFile
Number of Instances: $($Instances.Count)
========================================

"@ -ForegroundColor Cyan

# Read .env file
Import-Module ./env-utility.psm1 -Force
$envFile = ReadValuesFromEnvFile $EnvironmentFile

$sourcePort = $envFile["CONNECT_SOURCE_PORT"]
if ([string]::IsNullOrEmpty($sourcePort)) {
    $sourcePort = "8083"
    Write-Host "Using default Kafka Connect port: $sourcePort" -ForegroundColor Yellow
}

$postgresPassword = $envFile["POSTGRES_PASSWORD"]
if ([string]::IsNullOrEmpty($postgresPassword)) {
    Write-Host "ERROR: POSTGRES_PASSWORD not found in environment file" -ForegroundColor Red
    exit 1
}

$connectBaseUrl = "http://localhost:$sourcePort/connectors"

# Check if Kafka Connect is ready
if (-not (IsReady $connectBaseUrl)) {
    Write-Host "`nERROR: Kafka Connect is not available at $connectBaseUrl" -ForegroundColor Red
    Write-Host "Please ensure:" -ForegroundColor Yellow
    Write-Host "  1. Docker containers are running (docker ps)" -ForegroundColor Yellow
    Write-Host "  2. kafka-postgresql-source container is healthy" -ForegroundColor Yellow
    Write-Host "  3. Port $sourcePort is accessible" -ForegroundColor Yellow
    exit 1
}

# Load connector template
$templatePath = "./instance_connector_template.json"
if (-not (Test-Path $templatePath)) {
    Write-Host "`nERROR: Connector template not found at: $templatePath" -ForegroundColor Red
    exit 1
}

$templateContent = Get-Content $templatePath -Raw
Write-Host "`nLoaded connector template from: $templatePath" -ForegroundColor Green

# Setup connector for each instance
$successCount = 0
$failureCount = 0

foreach ($instance in $Instances) {
    try {
        Setup-InstanceConnector `
            -InstanceId $instance.InstanceId `
            -DatabaseName $instance.DatabaseName `
            -ConnectBaseUrl $connectBaseUrl `
            -PostgresPassword $postgresPassword `
            -TemplateContent $templateContent

        $successCount++
    }
    catch {
        Write-Host "`nFailed to setup connector for instance $($instance.InstanceId)" -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor Red
        $failureCount++
    }
}

# Summary
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Setup Complete" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Successful: $successCount" -ForegroundColor Green
Write-Host "Failed: $failureCount" -ForegroundColor $(if ($failureCount -gt 0) { "Red" } else { "Gray" })

# List all connectors
Write-Host "`nActive Connectors:" -ForegroundColor Cyan
try {
    $allConnectors = Invoke-RestMethod -Uri $connectBaseUrl -Method Get
    foreach ($connector in $allConnectors) {
        $connectorStatus = Invoke-RestMethod -Uri "$connectBaseUrl/$connector/status" -Method Get
        $statusColor = if ($connectorStatus.connector.state -eq "RUNNING") { "Green" } else { "Yellow" }
        Write-Host "  - $connector : $($connectorStatus.connector.state)" -ForegroundColor $statusColor
    }
}
catch {
    Write-Host "Could not retrieve connector list: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host "`nTo verify topics were created, run:" -ForegroundColor Cyan
Write-Host "  docker exec dms-kafka1 /opt/kafka/bin/kafka-topics.sh --list --bootstrap-server localhost:9092" -ForegroundColor Gray

if ($failureCount -gt 0) {
    Write-Host "`nWARNING: Some connectors failed to setup. Check the errors above." -ForegroundColor Yellow
    exit 1
}

Write-Host "`nAll instance connectors configured successfully! ✓" -ForegroundColor Green
