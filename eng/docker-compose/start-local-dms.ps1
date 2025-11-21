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
        Write-Output "Creating 3 test DMS instances for Kafka topic-per-instance testing..."

        try {
            # Create system administrator credentials
            Add-CmsClient -CmsUrl "http://localhost:8081" -ClientId "dms-instance-admin" -ClientSecret "DmsSetup1!" -DisplayName "DMS Instance Setup Administrator"

            # Get configuration service token
            $configToken = Get-CmsToken -CmsUrl "http://localhost:8081" -ClientId "dms-instance-admin" -ClientSecret "DmsSetup1!"

            # Create Instance 1: District 255901 - School Year 2024
            Write-Output "Creating Instance 1..."
            $instance1Id = Add-DmsInstance -CmsUrl "http://localhost:8081" -AccessToken $configToken -PostgresPassword $envValues.POSTGRES_PASSWORD -PostgresDbName "edfi_datamanagementservice_d255901_sy2024" -InstanceName "District 255901 - School Year 2024" -InstanceType "District"
            Add-DmsInstanceRouteContext -CmsUrl "http://localhost:8081" -AccessToken $configToken -InstanceId $instance1Id -ContextKey "districtId" -ContextValue "255901"
            Add-DmsInstanceRouteContext -CmsUrl "http://localhost:8081" -AccessToken $configToken -InstanceId $instance1Id -ContextKey "schoolYear" -ContextValue "2024"
            Write-Output "Instance 1 created with ID: $instance1Id"

            # Create Instance 2: District 255901 - School Year 2025
            Write-Output "Creating Instance 2..."
            $instance2Id = Add-DmsInstance -CmsUrl "http://localhost:8081" -AccessToken $configToken -PostgresPassword $envValues.POSTGRES_PASSWORD -PostgresDbName "edfi_datamanagementservice_d255901_sy2025" -InstanceName "District 255901 - School Year 2025" -InstanceType "District"
            Add-DmsInstanceRouteContext -CmsUrl "http://localhost:8081" -AccessToken $configToken -InstanceId $instance2Id -ContextKey "districtId" -ContextValue "255901"
            Add-DmsInstanceRouteContext -CmsUrl "http://localhost:8081" -AccessToken $configToken -InstanceId $instance2Id -ContextKey "schoolYear" -ContextValue "2025"
            Write-Output "Instance 2 created with ID: $instance2Id"

            # Create Instance 3: District 255902 - School Year 2024
            Write-Output "Creating Instance 3..."
            $instance3Id = Add-DmsInstance -CmsUrl "http://localhost:8081" -AccessToken $configToken -PostgresPassword $envValues.POSTGRES_PASSWORD -PostgresDbName "edfi_datamanagementservice_d255902_sy2024" -InstanceName "District 255902 - School Year 2024" -InstanceType "District"
            Add-DmsInstanceRouteContext -CmsUrl "http://localhost:8081" -AccessToken $configToken -InstanceId $instance3Id -ContextKey "districtId" -ContextValue "255902"
            Add-DmsInstanceRouteContext -CmsUrl "http://localhost:8081" -AccessToken $configToken -InstanceId $instance3Id -ContextKey "schoolYear" -ContextValue "2024"
            Write-Output "Instance 3 created with ID: $instance3Id"

            Write-Output "All 3 test instances created successfully with IDs: $instance1Id, $instance2Id, $instance3Id"

            # Create the physical PostgreSQL databases for each instance
            Write-Output "Creating PostgreSQL databases for instances..."
            docker exec dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255901_sy2024;" 2>$null
            docker exec dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255901_sy2025;" 2>$null
            docker exec dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255902_sy2024;" 2>$null
            Write-Output "Instance databases created"

            # Run migrations to create DMS schema in each instance database
            Write-Output "Running DMS migrations on instance databases..."
            docker exec dms-local-dms-1 dotnet /app/Installer/EdFi.DataManagementService.Backend.Installer.dll -e postgresql -c "host=dms-postgresql;port=5432;username=postgres;password=$($envValues.POSTGRES_PASSWORD);database=edfi_datamanagementservice_d255901_sy2024;" 2>$null
            docker exec dms-local-dms-1 dotnet /app/Installer/EdFi.DataManagementService.Backend.Installer.dll -e postgresql -c "host=dms-postgresql;port=5432;username=postgres;password=$($envValues.POSTGRES_PASSWORD);database=edfi_datamanagementservice_d255901_sy2025;" 2>$null
            docker exec dms-local-dms-1 dotnet /app/Installer/EdFi.DataManagementService.Backend.Installer.dll -e postgresql -c "host=dms-postgresql;port=5432;username=postgres;password=$($envValues.POSTGRES_PASSWORD);database=edfi_datamanagementservice_d255902_sy2024;" 2>$null
            Write-Output "Migrations completed"

            # Create Kafka connectors using the separate setup script
            Write-Output "Setting up Kafka connectors for instance databases..."
            $instances = @(
                @{ InstanceId = $instance1Id; DatabaseName = "edfi_datamanagementservice_d255901_sy2024" },
                @{ InstanceId = $instance2Id; DatabaseName = "edfi_datamanagementservice_d255901_sy2025" },
                @{ InstanceId = $instance3Id; DatabaseName = "edfi_datamanagementservice_d255902_sy2024" }
            )
            ./setup-instance-kafka-connectors.ps1 -Instances $instances -EnvironmentFile $EnvironmentFile
            Write-Output "Kafka connectors setup completed"

            # Explicitly create Kafka topics for each instance
            # This is required because Debezium only creates topics when publishing messages,
            # and empty databases won't trigger topic creation during initial snapshot
            Write-Output "Waiting for Kafka broker to be ready for topic creation..."

            # Wait for Kafka to be ready to accept topic creation commands
            $maxWaitAttempts = 12
            $kafkaReady = $false
            for ($i = 1; $i -le $maxWaitAttempts; $i++) {
                try {
                    # Use dms-kafka1:9092 because that's how Kafka advertises itself inside the container network
                    $testResult = docker exec dms-kafka1 /opt/kafka/bin/kafka-broker-api-versions.sh --bootstrap-server dms-kafka1:9092 2>&1
                    if ($LASTEXITCODE -eq 0) {
                        Write-Output "Kafka broker is ready!"
                        $kafkaReady = $true
                        break
                    }
                }
                catch {
                    # Ignore errors, will retry
                }
                Write-Output "  Waiting for Kafka... ($i/$maxWaitAttempts)"
                Start-Sleep -Seconds 5
            }

            if (-not $kafkaReady) {
                Write-Warning "Kafka broker readiness check timed out. Topics may be auto-created by Debezium when first message is published."
            }

            Write-Output "Creating Kafka topics for instances..."
            $topicsToCreate = @("edfi.dms.1.document", "edfi.dms.2.document", "edfi.dms.3.document")
            $maxRetries = 3
            $retryDelay = 5

            foreach ($topic in $topicsToCreate) {
                $created = $false
                for ($attempt = 1; $attempt -le $maxRetries; $attempt++) {
                    try {
                        # Use dms-kafka1:9092 because that's how Kafka advertises itself inside the container network
                        $result = docker exec dms-kafka1 /opt/kafka/bin/kafka-topics.sh --create --if-not-exists --topic $topic --bootstrap-server dms-kafka1:9092 --partitions 1 --replication-factor 1 2>&1
                        if ($LASTEXITCODE -eq 0 -or $result -match "already exists") {
                            Write-Output "  ✓ Topic created: $topic"
                            $created = $true
                            break
                        }
                    }
                    catch {
                        Write-Warning "  Attempt $attempt/$maxRetries failed for topic $topic"
                    }

                    if ($attempt -lt $maxRetries) {
                        Write-Output "  Retrying in $retryDelay seconds..."
                        Start-Sleep -Seconds $retryDelay
                    }
                }

                if (-not $created) {
                    Write-Warning "  Could not create topic $topic after $maxRetries attempts. Topic will be auto-created when first message is published."
                }
            }
            Write-Output "Kafka topic creation completed"
        }
        catch {
            Write-Warning "Failed to create DMS test instances: $($_.Exception.Message)"
        }
    }

    Start-Sleep 20

}
