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

    # Enable KafkaUI
    [Switch]
    $EnableKafkaUI,

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

    # Identity provider type
    [string]
    [ValidateSet("keycloak", "self-contained")]
    $IdentityProvider="self-contained",

    # Add initial DMS Instance to Configuration Service
    [Switch]
    $AddDmsInstance = $true
)


# Configure environment variables for new claimset loading approach
if($AddExtensionSecurityMetadata)
{
    # Set environment variables for hybrid claimset loading
    $env:DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING = "true"
    $env:DMS_CONFIG_CLAIMS_SOURCE = "Hybrid"
    $env:DMS_CONFIG_CLAIMS_DIRECTORY = "/app/additional-claims"
    Write-Output "Configured environment variables for file-based extension claimset loading"
}
    # Identity provider configuration
    Import-Module ./env-utility.psm1 -Force
    $envValues = ReadValuesFromEnvFile $EnvironmentFile
    $env:DMS_CONFIG_IDENTITY_PROVIDER=$IdentityProvider
    Write-Output "Identity Provider $IdentityProvider"
    if($IdentityProvider -eq "keycloak")
    {
        $env:OAUTH_TOKEN_ENDPOINT = $envValues.KEYCLOAK_OAUTH_TOKEN_ENDPOINT
        $env:DMS_JWT_AUTHORITY = $envValues.KEYCLOAK_DMS_JWT_AUTHORITY
        $env:DMS_JWT_METADATA_ADDRESS = $envValues.KEYCLOAK_DMS_JWT_METADATA_ADDRESS
        $env:DMS_CONFIG_IDENTITY_AUTHORITY = $envValues.KEYCLOAK_DMS_JWT_AUTHORITY
    }
    elseif ($IdentityProvider -eq "self-contained") {
        $env:OAUTH_TOKEN_ENDPOINT = $envValues.SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT
        $env:DMS_JWT_AUTHORITY = $envValues.SELF_CONTAINED_DMS_JWT_AUTHORITY
        $env:DMS_JWT_METADATA_ADDRESS = $envValues.SELF_CONTAINED_DMS_JWT_METADATA_ADDRESS
        $env:DMS_CONFIG_IDENTITY_AUTHORITY = $envValues.SELF_CONTAINED_DMS_JWT_AUTHORITY
    }
$files = @(
    "-f",
    "postgresql.yml",
    "-f",
    "local-dms.yml",
    "-f",
    "kafka.yml"
)

if ($EnableKafkaUI) {
    $files += @("-f", "kafka-ui.yml")
}

# Include configuration service if enabled or if using self-contained identity provider
if ($EnableConfig -or $IdentityProvider -eq "self-contained") {
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
    if ($r) {
        Write-Output "Building images with no cache (this may take a few minutes)..."
        docker compose $files --env-file $EnvironmentFile -p dms-local build --no-cache
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to build images. Exit code $LASTEXITCODE"
        }
    }

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

    if($IdentityProvider -eq "self-contained")
    {
        Write-Output "Init db public and private keys for OpenIddict..."
        ./setup-openiddict.ps1 -InitDb -InsertData:$false -EnvironmentFile $EnvironmentFile
    }
    docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to start local Docker environment, with exit code $LASTEXITCODE."
    }

    Start-Sleep 20
    if($IdentityProvider -eq "self-contained")
    {
        Write-Output "Starting self-contained initialization script..."
        # Create client with default edfi_admin_api/full_access scope
        ./setup-openiddict.ps1 -EnvironmentFile $EnvironmentFile

        # Create client with edfi_admin_api/readonly_access scope
        ./setup-openiddict.ps1 -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -EnvironmentFile $EnvironmentFile

        # Create client with edfi_admin_api/authMetadata_readonly_access scope
        ./setup-openiddict.ps1 -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access" -EnvironmentFile $EnvironmentFile
    }
    Write-Output "Running connector setup..."
    ./setup-connectors.ps1 $EnvironmentFile

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

    if($AddDmsInstance)
    {
        Import-Module ../Dms-Management.psm1 -Force
        Write-Output "Creating initial DMS Instance..."

        try {
            # Create system administrator credentials
            Add-CmsClient -CmsUrl "http://localhost:8081" -ClientId "dms-instance-admin" -ClientSecret "DmsSetup1!" -DisplayName "DMS Instance Setup Administrator"

            # Get configuration service token
            $configToken = Get-CmsToken -CmsUrl "http://localhost:8081" -ClientId "dms-instance-admin" -ClientSecret "DmsSetup1!"

            # Create DMS Instance using environment variables
            $instanceId = Add-DmsInstance -CmsUrl "http://localhost:8081" -AccessToken $configToken -PostgresPassword $envValues.POSTGRES_PASSWORD -PostgresDbName $envValues.POSTGRES_DB_NAME -InstanceName "Local Development Instance" -InstanceType "Development"

            Write-Output "DMS Instance created successfully with ID: $instanceId"
        }
        catch {
            Write-Warning "Failed to create DMS Instance: $($_.Exception.Message)"
        }
    }

    Start-Sleep 20

}
