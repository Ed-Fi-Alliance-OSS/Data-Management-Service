# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Registers the Debezium PostgreSQL source connector and verifies it reaches RUNNING.
.DESCRIPTION
    This runs after schema provisioning and DMS startup. The source connector snapshots
    dms.document on registration, so it must not be registered before those tables exist.
    Registration failures throw, and the connector plus all of its tasks must report the
    RUNNING state before the script returns, so a broken connector is no longer reported
    as success.
#>

[CmdletBinding()]
param (
    # Environment file
    [string]
    $EnvironmentFile = "./.env"
)

$ErrorActionPreference = "Stop"

function Format-LogSafeText {
    <#
    .SYNOPSIS
    Sanitizes a value for safe inclusion in log output (whitelist of letters, digits, and safe punctuation).
    #>
    param($Value)

    if ($null -eq $Value) {
        return ""
    }

    $text = [string]$Value
    $builder = [System.Text.StringBuilder]::new()
    foreach ($character in $text.ToCharArray()) {
        if ([char]::IsLetterOrDigit($character) -or
            $character -eq " " -or
            $character -eq "_" -or
            $character -eq "-" -or
            $character -eq "." -or
            $character -eq ":" -or
            $character -eq "/" -or
            $character -eq ",") {
            $null = $builder.Append($character)
        }
    }

    return $builder.ToString()
}

function Wait-ConnectEndpointReady {
    param(
        [string]$Url,
        [int]$MaxAttempts = 12,
        [int]$DelaySeconds = 5
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Invoke-RestMethod -Uri $Url -Method Get -TimeoutSec 5 | Out-Null
            return $true
        }
        catch {
            Write-Output "Kafka Connect REST API not ready (attempt $attempt/$MaxAttempts): $(Format-LogSafeText $_.Exception.Message)"
            Start-Sleep -Seconds $DelaySeconds
        }
    }

    return $false
}

function Wait-ConnectorRunning {
    param(
        [string]$StatusUrl,
        [string]$ConnectorName,
        [int]$MaxAttempts = 24,
        [int]$DelaySeconds = 5
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $status = $null
        try {
            $status = Invoke-RestMethod -Uri $StatusUrl -Method Get -TimeoutSec 5 -SkipHttpErrorCheck
        }
        catch {
            Write-Output "Unable to read connector status (attempt $attempt/$MaxAttempts): $(Format-LogSafeText $_.Exception.Message)"
            Start-Sleep -Seconds $DelaySeconds
            continue
        }

        $connectorState = [string]$status.connector.state
        $taskStates = @($status.tasks | ForEach-Object { [string]$_.state })

        # Fail fast on a FAILED connector or task: the trace is only available here and would
        # otherwise be lost, leaving the stack up but silently not streaming changes.
        if ($connectorState -eq "FAILED") {
            throw "Connector '$(Format-LogSafeText $ConnectorName)' entered the FAILED state. $(Format-LogSafeText $status.connector.trace)"
        }

        $failedTask = $status.tasks | Where-Object { $_.state -eq "FAILED" } | Select-Object -First 1
        if ($null -ne $failedTask) {
            throw "Connector '$(Format-LogSafeText $ConnectorName)' has a FAILED task. $(Format-LogSafeText $failedTask.trace)"
        }

        $allTasksRunning = $taskStates.Count -gt 0 -and -not ($taskStates | Where-Object { $_ -ne "RUNNING" })
        if ($connectorState -eq "RUNNING" -and $allTasksRunning) {
            return
        }

        Write-Output "Waiting for connector '$(Format-LogSafeText $ConnectorName)' to reach RUNNING (connector=$(Format-LogSafeText $connectorState); tasks=$(Format-LogSafeText ($taskStates -join ',')))..."
        Start-Sleep -Seconds $DelaySeconds
    }

    throw "Connector '$(Format-LogSafeText $ConnectorName)' did not reach the RUNNING state within $($MaxAttempts * $DelaySeconds) seconds."
}

function Wait-ConnectorAbsent {
    param(
        [string]$ConnectorUrl,
        [string]$ConnectorName,
        [int]$MaxAttempts = 15,
        [int]$DelaySeconds = 2
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $ConnectorUrl -Method Get -TimeoutSec 5 -SkipHttpErrorCheck
        }
        catch {
            # A transport-level failure cannot confirm removal; retry until the deadline.
            Write-Output "Unable to confirm connector removal (attempt $attempt/$MaxAttempts): $(Format-LogSafeText $_.Exception.Message)"
            Start-Sleep -Seconds $DelaySeconds
            continue
        }

        if ($response.StatusCode -eq 404) {
            return
        }

        Write-Output "Waiting for connector '$(Format-LogSafeText $ConnectorName)' deletion to settle (status $(Format-LogSafeText $response.StatusCode))..."
        Start-Sleep -Seconds $DelaySeconds
    }

    throw "Connector '$(Format-LogSafeText $ConnectorName)' was not removed within $($MaxAttempts * $DelaySeconds) seconds; cannot register a clean replacement."
}

function New-ConnectorRequestBody {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Builds an in-memory request body string; no system state changes and no -WhatIf surface.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The database password is read as plaintext from the environment file and must be serialized as plaintext JSON for the Kafka Connect REST API; SecureString adds no protection across that boundary.')]
    param(
        [string]$TemplatePath,
        [string]$Password
    )

    $connector = Get-Content -LiteralPath $TemplatePath -Raw | ConvertFrom-Json
    # Set the password structurally so a value containing quotes, backslashes, or other
    # JSON-significant characters is escaped correctly. The prior string .Replace approach
    # produced invalid JSON for such passwords.
    $connector.config."database.password" = $Password
    return ($connector | ConvertTo-Json -Depth 20)
}

# Allow dot-sourcing for unit tests: only the functions above are needed in that case.
if ($MyInvocation.InvocationName -eq '.') { return }

# Read .env file
Import-Module ./env-utility.psm1
$envFile = ReadValuesFromEnvFile $EnvironmentFile

$sourcePort = $envFile["CONNECT_SOURCE_PORT"]

$sourceBase = "http://localhost:$sourcePort/connectors"
$sourceUrl = "$sourceBase/postgresql-source"
$statusUrl = "$sourceUrl/status"

if (-not (Wait-ConnectEndpointReady -Url $sourceBase)) {
    throw "Kafka Connect REST API at $(Format-LogSafeText $sourceBase) did not become available. Connector registration cannot proceed."
}

# Replace any existing connector so the configuration below is applied cleanly.
$existing = Invoke-RestMethod -Uri $sourceUrl -Method Get -SkipHttpErrorCheck
if ($null -ne $existing.name) {
    Write-Output "Deleting existing source connector configuration."
    Invoke-RestMethod -Method Delete -Uri $sourceUrl | Out-Null
    # DELETE is asynchronous; wait until the connector is actually gone so the POST below does
    # not race the deletion and fail with 409 Conflict.
    Wait-ConnectorAbsent -ConnectorUrl $sourceUrl -ConnectorName "postgresql-source"
}

$sourceBody = New-ConnectorRequestBody -TemplatePath "./postgresql_connector.json" -Password $envFile["POSTGRES_PASSWORD"]

Write-Output "Installing source connector configuration"
try {
    Invoke-RestMethod -Method Post -Uri $sourceBase -ContentType "application/json" -Body $sourceBody | Out-Null
}
catch {
    throw "Failed to register the PostgreSQL source connector: $(Format-LogSafeText $_.Exception.Message)"
}

Wait-ConnectorRunning -StatusUrl $statusUrl -ConnectorName "postgresql-source"
Write-Output "Source connector 'postgresql-source' is RUNNING."
