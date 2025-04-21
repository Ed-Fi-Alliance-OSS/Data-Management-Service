# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param (
    # Stop services instead of starting them
    [Switch]
    $d,

    # Delete volumes after stopping services
    [Switch]
    $v,

    # Environment file
    [string]
    $EnvironmentFile = "./.env",

    # Force a rebuild
    [Switch]
    $r,

    # Enable KafkaUI and OpenSearch or ElasticSearch Dashboard
    [Switch]
    $EnableSearchEngineUI,

    # Search engine type ("OpenSearch" or "ElasticSearch")
    [string]
    [ValidateSet("OpenSearch", "ElasticSearch")]
    $SearchEngine = "OpenSearch",

    # Enable the DMS Configuration Service
    [Switch]
    $EnableConfig
)

$files = @(
    "-f",
    "postgresql.yml"
)

if ($SearchEngine -eq "ElasticSearch") {
    $files += @("-f", "kafka-elasticsearch.yml")
    if ($EnableSearchEngineUI) {
        $files += @("-f", "kafka-elasticsearch-ui.yml")
    }
}
else {
    $files += @("-f", "kafka-opensearch.yml")
    if ($EnableSearchEngineUI) {
        $files += @("-f", "kafka-opensearch-ui.yml")
    }
}

if ($EnableConfig) {
    $files += @("-f", "local-config.yml")
}

if ($d) {
    if ($v) {
        Write-Output "Shutting down with volume delete"
        docker compose $files --env-file $EnvironmentFile -p dms-local down -v
    }
    else {
        Write-Output "Shutting down"
        docker compose $files --env-file $EnvironmentFile -p dms-local down
    }
}
else {
    $existingNetwork = docker network ls --filter name="dms" -q
    if (! $existingNetwork) {
        docker network create dms
    }

    $upArgs = @(
        "--detach"
    )
    if ($r) { $upArgs += @("--build") }

    Write-Output "Starting Keycloak first..."
    docker compose -f keycloak.yml --env-file $EnvironmentFile -p dms-local up $upArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to start Keycloak. Exit code $LASTEXITCODE"
    }

    Write-Output "Waiting for Keycloak to initialize..."
    Start-Sleep 20

    Write-Output "Running setup-keycloak.ps1 scripts..."
    Start-Sleep 5
    # Create client with default edfi_admin_api/full_access scope
    ./setup-keycloak.ps1

    # Create client with edfi_admin_api/readonly_access scope
    ./setup-keycloak.ps1 -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access"

    # Create client with edfi_admin_api/authMetadata_readonly_access scope
    ./setup-keycloak.ps1 -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access"

    Write-Output "Starting locally-built DMS"

    docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to start local Docker environment, with exit code $LASTEXITCODE."
    }

    Start-Sleep 20

    Write-Output "Running connector setup..."
    ./setup-connectors.ps1 $EnvironmentFile $SearchEngine

    Write-Host "Waiting for SearchEngine to initialize..."

    function Wait-ForHealthy($containerName) {
        while ((docker inspect -f '{{.State.Health.Status}}' $containerName) -ne "healthy") {
            Write-Host "Waiting on $containerName..."
            Start-Sleep -Seconds 5
        }
    }

    Wait-ForHealthy "dms-search"
    Write-Host "dms-search container is healthy ..."
    Wait-ForHealthy "dms-kafka1"
    Write-Host "dms-kafka1 container is healthy ..."

    Write-Host "Starting DMS..."
    docker-compose -f local-dms.yml --env-file $EnvironmentFile -p dms-local up $upArgs

    Write-Host "Waiting for DMS to initialize..."
    Wait-ForHealthy "dms-local-dms-1"
    Write-Host "dms-local-dms-1 container is healthy ..."
}
