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
    $EnableOpenSearchUI,

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
    "postgresql.yml",
    "-f",
    "published-dms.yml",
    "-f",
    "keycloak.yml"
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

if ($d) {
    if ($v) {
        Write-Output "Shutting down with volume delete"
        docker compose $files down -v
    }
    else {
        Write-Output "Shutting down"
        docker compose $files down
    }
}
else {
    docker network create dms

    Write-Output "Starting published DMS"
    docker compose $files --env-file $EnvironmentFile up -d

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to start Published Docker environment, with exit code $LASTEXITCODE."
    }

    Start-Sleep 20
    ./setup-connectors.ps1 $EnvironmentFile $SearchEngine

    Start-Sleep 5
    # Create client with default edfi_admin_api/full_access scope
    ./setup-keycloak.ps1

    # Create client with edfi_admin_api/readonly_access scope
    ./setup-keycloak.ps1 -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access"

    # Create client with edfi_admin_api/authMetadata_readonly_access scope
    ./setup-keycloak.ps1 -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access"
}
