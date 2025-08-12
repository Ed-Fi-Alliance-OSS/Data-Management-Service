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
    $EnableConfig,

    # Enable Swagger UI for the DMS API
    [switch]$EnableSwaggerUI,

    # Load seed data using database template package
    [Switch]
    $LoadSeedData,

    # Add extension security metadata
    [Switch]
    $AddExtensionSecurityMetadata,

    # Add smoke test credentials
    [Switch]
    $AddSmokeTestCredentials,

    # Add smoke test credentials
    [string]
    [ValidateSet("keycloak", "self-contained")]
    $IdentityProvider="keycloak"
)


if($AddExtensionSecurityMetadata)
{
    Import-Module ./setup-extension-security-metadata.psm1 -Force
    AddExtensionSecurityMetadata -EnvironmentFile $EnvironmentFile
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to set up extension security metadata, with exit code $LASTEXITCODE."
    }
}

$files = @(
    "-f",
    "postgresql.yml",
    "-f",
    "local-dms.yml"
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

if ($EnableSwaggerUI) {
    $files += @("-f", "swagger-ui.yml")
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

    if($IdentityProvider -eq "keycloak")
    {
        Write-Output "Starting Keycloak..."
        docker compose -f keycloak.yml --env-file $EnvironmentFile -p dms-local up $upArgs
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to start Keycloak. Exit code $LASTEXITCODE"
        }

        Write-Output "Running setup-keycloak.ps1 scripts..."
        # Create client with default edfi_admin_api/full_access scope
        ./setup-keycloak.ps1

        # Create client with edfi_admin_api/readonly_access scope
        ./setup-keycloak.ps1 -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access"

        # Create client with edfi_admin_api/authMetadata_readonly_access scope
        ./setup-keycloak.ps1 -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access"
    }
    Write-Output "Starting Postgresql..."
    docker compose -f postgresql.yml --env-file $EnvironmentFile -p dms-local up $upArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to start Postgresql. Exit code $LASTEXITCODE"
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


    Write-Output "Starting locally-built DMS"
    docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to start local Docker environment, with exit code $LASTEXITCODE."
    }

    Start-Sleep 20
    if($IdentityProvider -eq "self-contained")
    {
        Write-Output "Starting self-contained initialization script..."
        # Create client with default edfi_admin_api/full_access scope
        ./setup-openiddict.ps1

        # Create client with edfi_admin_api/readonly_access scope
        ./setup-openiddict.ps1 -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access"

        # Create client with edfi_admin_api/authMetadata_readonly_access scope
        ./setup-openiddict.ps1 -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access"
    }
    Write-Output "Running connector setup..."
    ./setup-connectors.ps1 $EnvironmentFile $SearchEngine

    if($AddSmokeTestCredentials)
    {
        Import-Module ../smoke_test/modules/SmokeTest.psm1 -Force
        Write-Output "Creating smoke test credentials..."
        $credentials = Get-SmokeTestCredentials -ConfigServiceUrl "http://localhost:8081"

        Write-Output "Smoke test credentials created successfully!"
        Write-Output "Key: $($credentials.Key)"
        Write-Output "Secret: $($credentials.Secret)"
        Write-Output "These credentials can be used for smoke testing the DMS API."
    }
    Start-Sleep 20
    if($IdentityProvider -eq "self-contained")
    {
        Write-Output "Restart config and dms apis to refresh openIddict keys"
        Write-Output "Restarting dms-config-service"
        docker restart dms-config-service
        Start-Sleep 10
        Write-Output "Restarting dms-local"
        docker restart dms-local-dms-1
    }

}
