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

    # Enable KafkaUI and OpenSearch Dashboard
    [Switch]
    $EnableSearchEngineUI,

    # Search engine type ("OpenSearch" or "ElasticSearch")
    [string]
    [ValidateSet("OpenSearch", "ElasticSearch")]
    $SearchEngine = "OpenSearch",

    # Enable the DMS Configuration Service
    [Switch]
    $EnableConfig,

    # Enable Swagger UI for the DMS API
    [Switch]$EnableSwaggerUI,

    # Load seed data using database template package
    [Switch]
    $LoadSeedData,

    # Add extension security metadata
    [Switch]
    $AddExtensionSecurityMetadata
)

$files = @(
    "-f",
    "postgresql.yml",
    "-f",
    "published-dms.yml"
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
    $files += @("-f", "published-config.yml")
}

if ($EnableSwaggerUI) {
    $files += @("-f", "swagger-ui.yml")
}

if ($d) {
    if ($v) {
        Write-Output "Shutting down with volume delete"
        docker compose $files --env-file $EnvironmentFile -p dms-published down -v
    }
    else {
        Write-Output "Shutting down"
        docker compose $files --env-file $EnvironmentFile -p dms-published down
    }
}
else {
    $existingNetwork = docker network ls --filter name="dms" -q
    if (! $existingNetwork) {
        docker network create dms
    }

    Write-Output "Starting Keycloak first..."
    docker compose -f keycloak.yml --env-file $EnvironmentFile -p dms-published up -d
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

    Import-Module ./env-utility.psm1
    $envValues = ReadValuesFromEnvFile $EnvironmentFile

    Write-Output "Starting published DMS"
    $env:NEED_DATABASE_SETUP = if ($LoadSeedData) { "false" } else { $env:NEED_DATABASE_SETUP }
    docker compose $files --env-file $EnvironmentFile -p dms-published up -d
    $env:NEED_DATABASE_SETUP = $envValues["NEED_DATABASE_SETUP"]

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to start Published Docker environment, with exit code $LASTEXITCODE."
    }

    Start-Sleep 20

    if($LoadSeedData)
    {
        Import-Module ./setup-database-template.psm1 -Force
        Write-Output "Loading initial data from the database template..."
        LoadSeedData -EnvironmentFile $EnvironmentFile
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to load initial data, with exit code $LASTEXITCODE."
        }
    }

    if($AddExtensionSecurityMetadata)
    {
        Write-Output "Updating Claim Hierarchy..."
        Import-Module ./setup-extension-security-metadata.psm1 -Force
        UpdateExtensionSecurityMetadata -EnvironmentFile $EnvironmentFile
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to set up extension security metadata, with exit code $LASTEXITCODE."
        }
        docker restart dms-published-dms-1
    }

    Start-Sleep 10

    Write-Output "Running connector setup..."
    ./setup-connectors.ps1 $EnvironmentFile $SearchEngine
}
