# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Tears down the infrastructure-only Docker environment
.DESCRIPTION
    This script stops and removes only the infrastructure services (PostgreSQL, Kafka)
    started by setup-infrastructure-only.ps1. It does NOT affect locally-running
    DMS or Configuration Service instances.
#>

[CmdletBinding()]
param()

Write-Host @"
Ed-Fi DMS Infrastructure Teardown
==================================
"@ -ForegroundColor Cyan

# Check if Docker is running
Write-Host "Checking Docker status..." -ForegroundColor Yellow
$dockerCheck = $null
try {
    $dockerCheck = docker version 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Docker command failed"
    }
}
catch {
    Write-Host ""
    Write-Error "Docker is not running or not installed. Please start Docker and try again."
    Write-Host ""
    Write-Host "Error details:" -ForegroundColor Red
    if ($dockerCheck) {
        Write-Host $dockerCheck -ForegroundColor Red
    } else {
        Write-Host $_.Exception.Message -ForegroundColor Red
    }
    exit 1
}
Write-Host "Docker is running ✓" -ForegroundColor Green
Write-Host ""

# Store current location and navigate to docker-compose directory
$originalLocation = Get-Location
$dockerComposeDir = Join-Path $PSScriptRoot "../../../../eng/docker-compose"

try {
    Set-Location $dockerComposeDir

    # Set SCHEMA_PACKAGES to empty to prevent warning
    [System.Environment]::SetEnvironmentVariable("SCHEMA_PACKAGES", "", "Process")

    # Define compose files for infrastructure services only
    $files = @(
        "-f", "postgresql.yml",
        "-f", "kafka.yml",
        "-f", "kafka-ui.yml",
        "-f", "keycloak.yml"
    )

    # Use environment file
    $envFile = "./.env.e2e"
    if (-not (Test-Path $envFile)) {
        $envFile = "./.env"
    }

    Write-Host "Stopping infrastructure containers..." -ForegroundColor Yellow
    try {
        $output = docker compose $files --env-file $envFile -p dms-local down -v 2>&1

        # Filter out the SCHEMA_PACKAGES warning from output
        $filteredOutput = $output | Where-Object {
            $_ -notmatch 'SCHEMA_PACKAGES.*variable is not set'
        }

        if ($filteredOutput) {
            $filteredOutput | ForEach-Object { Write-Host $_ }
        }

        if ($LASTEXITCODE -eq 0) {
            Write-Host "Infrastructure containers stopped successfully." -ForegroundColor Green
        }
    }
    catch {
        Write-Warning "Error stopping containers: $_"
    }

    # Force stop any remaining infrastructure containers
    Write-Host "`nForce stopping any remaining infrastructure containers..." -ForegroundColor Yellow
    $infraContainers = docker ps -a --format "{{.Names}}" | Where-Object {
        $_ -match "(dms-postgresql|dms-kafka|kafka-.*|dms-keycloak)"
    }
    if ($infraContainers) {
        foreach ($container in $infraContainers) {
            Write-Host "- Force removing container: $container" -ForegroundColor Gray
            docker rm -f $container 2>&1 | Out-Null
        }
    } else {
        Write-Host "No remaining infrastructure containers found" -ForegroundColor Gray
    }

    # Remove volumes
    Write-Host "`nRemoving volumes..." -ForegroundColor Yellow
    $volumePatterns = @(
        "dms-local_dms-postgresql",
        "dms-local_kafka-data",
        "dms-local_kafka-logs",
        "dms-local_kafka-postgresql-source-logs",
        "dms-local_kafka-postgresql-source-config",
        "dms-local_kafka-elasticsearch-sink-logs",
        "dms-local_kafka-elasticsearch-sink-config",
        "dms-local_keycloak-data"
    )

    foreach ($volumePattern in $volumePatterns) {
        $matchingVolumes = docker volume ls -q | Where-Object { $_ -eq $volumePattern }
        if ($matchingVolumes) {
            foreach ($volume in $matchingVolumes) {
                Write-Host "- Removing $volume..." -NoNewline
                try {
                    docker volume rm $volume 2>&1 | Out-Null
                    if ($LASTEXITCODE -eq 0) {
                        Write-Host " done" -ForegroundColor Green
                    }
                }
                catch {
                    Write-Host " error" -ForegroundColor Red
                }
            }
        }
    }

    # Remove orphaned volumes
    Write-Host "`nRemoving orphaned volumes..." -ForegroundColor Yellow
    try {
        $orphanedVolumes = docker volume ls -qf dangling=true
        if ($orphanedVolumes) {
            $orphanedCount = ($orphanedVolumes | Measure-Object).Count
            Write-Host "Found $orphanedCount orphaned volume(s)" -ForegroundColor Gray

            foreach ($volume in $orphanedVolumes) {
                Write-Host "- Removing orphaned volume $volume..." -NoNewline
                try {
                    docker volume rm $volume 2>&1 | Out-Null
                    if ($LASTEXITCODE -eq 0) {
                        Write-Host " done" -ForegroundColor Green
                    }
                }
                catch {
                    Write-Host " error" -ForegroundColor Red
                }
            }
        }
        else {
            Write-Host "No orphaned volumes found" -ForegroundColor Gray
        }
    }
    catch {
        Write-Warning "Error checking for orphaned volumes: $_"
    }

    # Remove the dms network
    Write-Host "`nRemoving dms network..." -NoNewline
    try {
        docker network rm dms 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host " done" -ForegroundColor Green
        }
        else {
            Write-Host " not found or has connected containers" -ForegroundColor Gray
        }
    }
    catch {
        Write-Host " error" -ForegroundColor Red
        Write-Warning "  $_"
    }

    Write-Host "`nInfrastructure teardown complete!" -ForegroundColor Green
    Write-Host "To setup infrastructure again, run: ./setup-infrastructure-only.ps1" -ForegroundColor Cyan
}
finally {
    # Return to original location
    Set-Location $originalLocation
}
