# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Sets up the Ed-Fi DMS local Docker environment for Instance Management E2E testing
.DESCRIPTION
    This script is a convenience wrapper that runs start-local-dms.ps1 with the standard
    E2E testing configuration for instance management tests. It is the companion to teardown-local-dms.ps1.

    The script runs:
    ./start-local-dms.ps1 -EnableKafkaUI -EnableConfig -EnvironmentFile ./.env.e2e -r -AddExtensionSecurityMetadata
#>

[CmdletBinding()]
param()

Write-Host @"
Ed-Fi DMS Local Environment Setup for Instance Management E2E Testing
======================================================================
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

    Write-Host "Starting DMS environment with Instance Management E2E configuration..." -ForegroundColor Green
    Write-Host "Configuration:" -ForegroundColor Yellow
    Write-Host "  - Search Engine UI: Enabled" -ForegroundColor Gray
    Write-Host "  - Configuration Service: Enabled" -ForegroundColor Gray
    Write-Host "  - Environment File: ./.env.routeContext.e2e" -ForegroundColor Gray
    Write-Host "  - Force Rebuild: Yes" -ForegroundColor Gray
    Write-Host "  - Extension Security Metadata: Yes" -ForegroundColor Gray
    Write-Host "  - Route Qualifiers: districtId, schoolYear" -ForegroundColor Cyan
    Write-Host "  - Identity Provider: self-contained" -ForegroundColor Gray
    Write-Host "  - Add Test DMS Instances: Yes (3 instances with IDs 1, 2, 3)" -ForegroundColor Cyan
    Write-Host ""

    # Run the start script with Route Context E2E configuration
    ./start-local-dms.ps1 -EnableKafkaUI -EnableConfig -EnvironmentFile ./.env.routeContext.e2e -r -AddExtensionSecurityMetadata -IdentityProvider self-contained -AddDmsInstance:$false

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to start DMS environment. Exit code: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    # Create 3 test DMS instances with route contexts for Kafka topic-per-instance testing
    # This logic was moved from start-local-dms.ps1 to keep E2E-specific setup out of shared engineering scripts
    $environmentFile = "./.env.routeContext.e2e"

    Import-Module ./env-utility.psm1 -Force
    Import-Module ../Dms-Management.psm1 -Force
    $envValues = ReadValuesFromEnvFile $environmentFile

    Write-Output "Creating 3 test DMS instances for Kafka topic-per-instance testing..."

    try {
        # Create system administrator credentials
        Add-CmsClient -CmsUrl "http://localhost:8081" -ClientId "dms-instance-admin" -ClientSecret "DmsSetup1!" -DisplayName "DMS Instance Setup Administrator"

        # Get configuration service token
        $configToken = Get-CmsToken -CmsUrl "http://localhost:8081" -ClientId "dms-instance-admin" -ClientSecret "DmsSetup1!"

        # Create tenant if multi-tenancy is enabled
        $tenant = $envValues.CONFIG_SERVICE_TENANT
        if ($envValues.DMS_CONFIG_MULTI_TENANCY -eq "true" -and $tenant) {
            Write-Output "Multi-tenancy is enabled. Creating tenant: $tenant"
            try {
                $tenantId = Add-Tenant -CmsUrl "http://localhost:8081" -AccessToken $configToken -TenantName $tenant
                Write-Output "Tenant created successfully with ID: $tenantId"
            }
            catch {
                Write-Warning "Failed to create tenant (may already exist): $($_.Exception.Message)"
            }
        }

        # Create Instance 1: District 255901 - School Year 2024
        Write-Output "Creating Instance 1..."
        $instance1Id = Add-DmsInstance -CmsUrl "http://localhost:8081" -AccessToken $configToken -PostgresPassword $envValues.POSTGRES_PASSWORD -PostgresDbName "edfi_datamanagementservice_d255901_sy2024" -InstanceName "District 255901 - School Year 2024" -InstanceType "District" -Tenant $tenant
        Add-DmsInstanceRouteContext -CmsUrl "http://localhost:8081" -AccessToken $configToken -InstanceId $instance1Id -ContextKey "districtId" -ContextValue "255901" -Tenant $tenant
        Add-DmsInstanceRouteContext -CmsUrl "http://localhost:8081" -AccessToken $configToken -InstanceId $instance1Id -ContextKey "schoolYear" -ContextValue "2024" -Tenant $tenant
        Write-Output "Instance 1 created with ID: $instance1Id"

        # Create Instance 2: District 255901 - School Year 2025
        Write-Output "Creating Instance 2..."
        $instance2Id = Add-DmsInstance -CmsUrl "http://localhost:8081" -AccessToken $configToken -PostgresPassword $envValues.POSTGRES_PASSWORD -PostgresDbName "edfi_datamanagementservice_d255901_sy2025" -InstanceName "District 255901 - School Year 2025" -InstanceType "District" -Tenant $tenant
        Add-DmsInstanceRouteContext -CmsUrl "http://localhost:8081" -AccessToken $configToken -InstanceId $instance2Id -ContextKey "districtId" -ContextValue "255901" -Tenant $tenant
        Add-DmsInstanceRouteContext -CmsUrl "http://localhost:8081" -AccessToken $configToken -InstanceId $instance2Id -ContextKey "schoolYear" -ContextValue "2025" -Tenant $tenant
        Write-Output "Instance 2 created with ID: $instance2Id"

        # Create Instance 3: District 255902 - School Year 2024
        Write-Output "Creating Instance 3..."
        $instance3Id = Add-DmsInstance -CmsUrl "http://localhost:8081" -AccessToken $configToken -PostgresPassword $envValues.POSTGRES_PASSWORD -PostgresDbName "edfi_datamanagementservice_d255902_sy2024" -InstanceName "District 255902 - School Year 2024" -InstanceType "District" -Tenant $tenant
        Add-DmsInstanceRouteContext -CmsUrl "http://localhost:8081" -AccessToken $configToken -InstanceId $instance3Id -ContextKey "districtId" -ContextValue "255902" -Tenant $tenant
        Add-DmsInstanceRouteContext -CmsUrl "http://localhost:8081" -AccessToken $configToken -InstanceId $instance3Id -ContextKey "schoolYear" -ContextValue "2024" -Tenant $tenant
        Write-Output "Instance 3 created with ID: $instance3Id"

        Write-Output "All 3 test instances created successfully with IDs: $instance1Id, $instance2Id, $instance3Id"

        # Create the three test databases
        docker exec dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255901_sy2024;" 2>&1
        Write-Host "Created database: edfi_datamanagementservice_d255901_sy2024" -ForegroundColor Green

        docker exec dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255901_sy2025;" 2>&1
        Write-Host "Created database: edfi_datamanagementservice_d255901_sy2025" -ForegroundColor Green

        docker exec dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255902_sy2024;" 2>&1
        Write-Host "Created database: edfi_datamanagementservice_d255902_sy2024" -ForegroundColor Green

        Write-Host "Exporting schema from main database..." -ForegroundColor Cyan

        # Export schema from main database to a temporary location
        $tempSchemaFile = [System.IO.Path]::GetTempFileName()
        docker exec dms-postgresql pg_dump -U postgres -d edfi_datamanagementservice --schema-only > $tempSchemaFile
        Write-Host "Schema exported successfully" -ForegroundColor Green

        Write-Host "Applying schema to test databases..." -ForegroundColor Cyan

        # Apply schema to each test database
        Get-Content $tempSchemaFile | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024
        Write-Host "Schema applied to: edfi_datamanagementservice_d255901_sy2024" -ForegroundColor Green

        Get-Content $tempSchemaFile | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2025
        Write-Host "Schema applied to: edfi_datamanagementservice_d255901_sy2025" -ForegroundColor Green

        Get-Content $tempSchemaFile | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255902_sy2024
        Write-Host "Schema applied to: edfi_datamanagementservice_d255902_sy2024" -ForegroundColor Green

        # Clean up temp file
        Remove-Item $tempSchemaFile -ErrorAction SilentlyContinue

        # Create Kafka connectors using the separate setup script
        Write-Output "Setting up Kafka connectors for instance databases..."
        $instances = @(
            @{ InstanceId = $instance1Id; DatabaseName = "edfi_datamanagementservice_d255901_sy2024" },
            @{ InstanceId = $instance2Id; DatabaseName = "edfi_datamanagementservice_d255901_sy2025" },
            @{ InstanceId = $instance3Id; DatabaseName = "edfi_datamanagementservice_d255902_sy2024" }
        )
        ./setup-instance-kafka-connectors.ps1 -Instances $instances -EnvironmentFile $environmentFile
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

    Start-Sleep 20

    Write-Host "`nDMS Instance Management E2E environment setup complete!" -ForegroundColor Green
    Write-Host "To tear down this environment, run: ./teardown-local-dms.ps1" -ForegroundColor Cyan
}
finally {
    # Return to original location
    Set-Location $originalLocation
}
