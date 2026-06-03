# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Post-bootstrap startup phase for the published DMS Docker stack.
.DESCRIPTION
    This script is the post-bootstrap startup phase. The wrapper
    bootstrap-published-dms.ps1 orchestrates prepare -> infra -> configure -> provision ->
    this script, so by the time the wrapper calls into here a .bootstrap/ workspace and
    a provisioned database already exist.

    Direct invocation is supported for diagnostics and partial-phase orchestration
    (-InfraOnly, -DmsOnly). When invoked directly without a .bootstrap/ manifest the
    script proceeds but Invoke-BootstrapStartupConfiguration emits a warning: bootstrap
    schema provisioning will NOT happen here. Legacy direct-start database provisioning
    remains controlled by the supplied environment file's NEED_DATABASE_SETUP value.

    See command-boundaries.md Section 3 for the phase contract and
    01-schema-deployment-safety.md for the DMS-1151 story.
#>

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

    # Enable KafkaUI
    [Switch]
    $EnableKafkaUI,

    # Enable the DMS Configuration Service
    [Switch]
    $EnableConfig,

    # Enable Swagger UI for the DMS API
    [Switch]$EnableSwaggerUI,

    # Add smoke test credentials
    [Switch]
    $AddSmokeTestCredentials,

    # Load seed data via the direct-SQL database-template path. Retained pending the implementation
    # gate in bootstrap-design.md section 6.4 line 1250: removal is gated on Story 04 XSD-staging
    # verification. The new API-based seed path, via load-dms-seed-data.ps1 + bootstrap-*-dms.ps1,
    # is the forward contract; the slice that closes the gate owns this switch's removal.
    [Switch]
    $LoadSeedData,

    # Identity provider type
    [string]
    [ValidateSet("keycloak", "self-contained")]
    $IdentityProvider="self-contained",

    # Skip creating initial data store in Configuration Service
    [Switch]
    $NoDataStore,

    # School year range for multi-data-store setup (format: StartYear-EndYear, e.g., "2022-2026")
    [string]
    $SchoolYearRange = "",

    # Start only infrastructure required before schema provisioning
    [Switch]
    $InfraOnly,

    # Start only the DMS service after external schema provisioning
    [Switch]
    $DmsOnly,

    # Skip registering the default Debezium source connector. Used by harnesses that create
    # their own connectors after provisioning data-store-specific databases.
    [Switch]
    $SkipConnectorSetup,

    # Remove the .bootstrap workspace during teardown (-d -v). Off by default so a prepared
    # workspace is preserved when the caller (e.g. build-dms.ps1) does not intend to wipe it.
    [Switch]
    $RemoveBootstrap,

    # Transitional non-bootstrap helper: when no bootstrap manifest is present (DLL-backed startup),
    # passing this switch sets DMS_CONFIG_CLAIMS_SOURCE=Hybrid and DMS_CONFIG_CLAIMS_DIRECTORY=/app/additional-claims
    # so that extension claimset fragments (e.g. Sample, Homograph) are loaded from the AdditionalClaimsets
    # directory that is already mounted at /app/additional-claims by published-config.yml.
    # This flag is intentionally kept until Story 04 moves E2E runtime loading onto the staged bootstrap workspace.
    [Switch]
    $AddExtensionSecurityMetadata
)

Import-Module (Join-Path $PSScriptRoot "bootstrap-manifest.psm1") -Force
$originalLocation = Get-Location
if (-not [System.IO.Path]::IsPathRooted($EnvironmentFile)) {
    if ($PSBoundParameters.ContainsKey('EnvironmentFile')) {
        # Caller supplied an explicit relative path - resolve against the caller's CWD.
        $EnvironmentFile = [System.IO.Path]::GetFullPath((Join-Path $originalLocation.Path $EnvironmentFile))
    }
    else {
        # Default value - resolve against the script directory so that invoking the
        # script from any CWD (e.g. the repo root) still finds eng/docker-compose/.env.
        $EnvironmentFile = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $EnvironmentFile))
    }
}
$bootstrapEnvSnapshot = Get-BootstrapEnvSnapshot
Push-Location $PSScriptRoot
try {
Invoke-BootstrapStartupConfiguration -IsTeardown:$d -AddExtensionSecurityMetadata:$AddExtensionSecurityMetadata
$bootstrapManifestPresent = Test-Path -LiteralPath (Join-Path (Get-BootstrapRoot) "bootstrap-manifest.json") -PathType Leaf

# Identity provider configuration
Import-Module ./env-utility.psm1 -Force
$envValues = ReadValuesFromEnvFile $EnvironmentFile
$cmsUrl = Resolve-CmsBaseUrl -EnvValues $envValues
$dmsUrl = Resolve-DockerLocalDmsBaseUrl -EnvValues $envValues
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

if (-not $d) {
    if ($InfraOnly -and $DmsOnly) {
        throw "Parameters -InfraOnly and -DmsOnly are mutually exclusive."
    }

    if (($InfraOnly -or $DmsOnly) -and $LoadSeedData) {
        throw "Parameter -LoadSeedData cannot be used with -InfraOnly or -DmsOnly."
    }

    if ($DmsOnly -and ($NoDataStore -or -not [string]::IsNullOrWhiteSpace($SchoolYearRange) -or $AddSmokeTestCredentials)) {
        throw "Parameters -NoDataStore, -SchoolYearRange, and -AddSmokeTestCredentials cannot be used with -DmsOnly."
    }

    if ($NoDataStore -and -not [string]::IsNullOrWhiteSpace($SchoolYearRange)) {
        throw "Parameters -NoDataStore and -SchoolYearRange are mutually exclusive. Use -NoDataStore for manual data store creation, or use -SchoolYearRange to auto-create data stores."
    }

    if (-not [string]::IsNullOrWhiteSpace($SchoolYearRange) -and $envValues.DMS_CONFIG_MULTI_TENANCY -eq "true" -and -not $envValues.CONFIG_SERVICE_TENANT) {
        throw "Parameter -SchoolYearRange requires CONFIG_SERVICE_TENANT to be set in the environment file when DMS_CONFIG_MULTI_TENANCY=true (the Configuration Service requires the Tenant header)."
    }
}
$files = @(
    "-f",
    "postgresql.yml",
    "-f",
    "published-dms.yml",
    "-f",
    "kafka.yml"
)

if ($IdentityProvider -eq "keycloak") {
    # Keep Keycloak in the managed compose set so follow-up up/down calls operate on the full environment.
    $files += @("-f", "keycloak.yml")
}

if ($EnableKafkaUI) {
    $files += @("-f", "kafka-ui.yml")
}

# Include configuration service if enabled or if using self-contained identity provider
if ($EnableConfig -or $InfraOnly -or $IdentityProvider -eq "self-contained") {
    $files += @("-f", "published-config.yml")
}

if ($EnableSwaggerUI) {
    $files += @("-f", "swagger-ui.yml")
}

if ($d) {
    if ($v) {
        Write-Output "Shutting down with volume delete"
        docker compose $files --env-file $EnvironmentFile -p dms-published down -v

        Remove-BootstrapWorkspaceIfRequested -RemoveBootstrap:$RemoveBootstrap
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

    $upArgs = @(
        "--detach"
    )

    function Wait-HttpEndpointHealthy {
        param(
            [Parameter(Mandatory)]
            [string]
            $Url,

            [Parameter(Mandatory)]
            [string]
            $Name,

            [int]
            $TimeoutSeconds = 60
        )

        $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
        while ($true) {
            try {
                $response = Invoke-WebRequest -Uri $Url -Method Get -TimeoutSec 5 -ErrorAction Stop
                if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                    return
                }
            }
            catch {
                $null = $_
            }

            if ([datetime]::UtcNow -ge $deadline) {
                throw "$Name health check timed out after $TimeoutSeconds seconds. Endpoint: $(Format-LogSafeText $Url)"
            }

            Start-Sleep -Seconds 2
        }
    }

    if ($DmsOnly) {
        Write-Output "Starting published DMS service only with startup database provisioning disabled..."
        $dmsServices = @("dms")
        if ($EnableSwaggerUI) {
            $dmsServices += "swagger-ui"
        }
        $previousNeedDatabaseSetup = [System.Environment]::GetEnvironmentVariable("NEED_DATABASE_SETUP")
        $previousDeployDatabaseOnStartup = [System.Environment]::GetEnvironmentVariable("DMS_DEPLOY_DATABASE_ON_STARTUP")
        $previousAppSettingsDeployDatabaseOnStartup = [System.Environment]::GetEnvironmentVariable("AppSettings__DeployDatabaseOnStartup")
        try {
            $env:NEED_DATABASE_SETUP = "false"
            $env:DMS_DEPLOY_DATABASE_ON_STARTUP = "false"
            $env:AppSettings__DeployDatabaseOnStartup = "false"
            docker compose $files --env-file $EnvironmentFile -p dms-published up $upArgs $dmsServices
        }
        finally {
            [System.Environment]::SetEnvironmentVariable("NEED_DATABASE_SETUP", $previousNeedDatabaseSetup)
            [System.Environment]::SetEnvironmentVariable("DMS_DEPLOY_DATABASE_ON_STARTUP", $previousDeployDatabaseOnStartup)
            [System.Environment]::SetEnvironmentVariable("AppSettings__DeployDatabaseOnStartup", $previousAppSettingsDeployDatabaseOnStartup)
        }

        if ($LASTEXITCODE -ne 0) {
            throw "Unable to start published DMS service, with exit code $LASTEXITCODE."
        }

        Wait-HttpEndpointHealthy -Url "$($dmsUrl.TrimEnd('/'))/health" -Name "DMS"
        Write-Output "DMS service is healthy."

        if (-not $SkipConnectorSetup) {
            # Register the Debezium source connector now that the schema has been provisioned and
            # the DMS service is up. The connector infrastructure (kafka-postgresql-source) was
            # started during the -InfraOnly phase; setup-connectors.ps1 verifies it reaches the
            # RUNNING state and throws on failure.
            Write-Output "Running connector setup..."
            ./setup-connectors.ps1 $EnvironmentFile
        }
        else {
            Write-Output "Skipping default connector setup."
        }

        return
    }

    if($IdentityProvider -eq "keycloak")
    {
        Write-Output "Starting Keycloak first..."
        docker compose $files --env-file $EnvironmentFile -p dms-published up -d keycloak
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
    docker compose $files --env-file $EnvironmentFile -p dms-published up $upArgs db
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to start Postgresql. Exit code $LASTEXITCODE"
    }
    Start-Sleep 20

    if ($InfraOnly) {
        if($IdentityProvider -eq "self-contained")
        {
            Write-Output "Init db public and private keys for OpenIddict..."
            ./setup-openiddict.ps1 -InitDb -EnvironmentFile $EnvironmentFile
        }

        Write-Output "Starting Configuration Service..."
        docker compose $files --env-file $EnvironmentFile -p dms-published up $upArgs config
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to start Configuration Service. Exit code $LASTEXITCODE"
        }

        Wait-HttpEndpointHealthy -Url "$($cmsUrl.TrimEnd('/'))/health" -Name "Configuration Service"
        Write-Output "Configuration Service is healthy."

        if($IdentityProvider -eq "self-contained")
        {
            Write-Output "Starting self-contained initialization script..."
            ./setup-openiddict.ps1 -InsertData -EnvironmentFile $EnvironmentFile
            ./setup-openiddict.ps1 -InsertData -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -EnvironmentFile $EnvironmentFile
            ./setup-openiddict.ps1 -InsertData -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access" -EnvironmentFile $EnvironmentFile
        }

        Write-Output "Starting Kafka connector infrastructure..."
        docker compose $files --env-file $EnvironmentFile -p dms-published up $upArgs kafka-postgresql-source
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to start Kafka connector infrastructure. Exit code $LASTEXITCODE"
        }

        # Connector registration is intentionally deferred to the -DmsOnly phase. The Debezium
        # source connector snapshots dms.document on registration; those tables do not exist
        # until provision-dms-schema.ps1 runs, so registering here would start the connector
        # task in a failed state. setup-connectors.ps1 runs after schema provisioning completes.

        if ($EnableKafkaUI) {
            Write-Output "Starting Kafka UI..."
            docker compose $files --env-file $EnvironmentFile -p dms-published up $upArgs kafka-ui
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to start Kafka UI. Exit code $LASTEXITCODE"
            }
        }

        Write-Output "Infrastructure phase complete. DMS service was not started."
        return
    }


    Write-Output "Starting published DMS"
    if ($bootstrapManifestPresent) {
        # DMS-1151: schema provisioning is owned by provision-dms-schema.ps1 in the
        # story-aligned bootstrap flow. Force the legacy Backend.Installer entrypoint off
        # when a bootstrap manifest is present so a stale NEED_DATABASE_SETUP=true value
        # in the env file or process environment cannot reactivate it. Direct, non-bootstrap
        # startup remains backward compatible with the env-file NEED_DATABASE_SETUP value.
        Write-Output "Bootstrap manifest detected; starting published DMS with startup database provisioning disabled."
        $previousNeedDatabaseSetup = [System.Environment]::GetEnvironmentVariable("NEED_DATABASE_SETUP")
        $previousDeployDatabaseOnStartup = [System.Environment]::GetEnvironmentVariable("DMS_DEPLOY_DATABASE_ON_STARTUP")
        $previousAppSettingsDeployDatabaseOnStartup = [System.Environment]::GetEnvironmentVariable("AppSettings__DeployDatabaseOnStartup")
        try {
            $env:NEED_DATABASE_SETUP = "false"
            $env:DMS_DEPLOY_DATABASE_ON_STARTUP = "false"
            $env:AppSettings__DeployDatabaseOnStartup = "false"
            docker compose $files --env-file $EnvironmentFile -p dms-published up $upArgs
        }
        finally {
            [System.Environment]::SetEnvironmentVariable("NEED_DATABASE_SETUP", $previousNeedDatabaseSetup)
            [System.Environment]::SetEnvironmentVariable("DMS_DEPLOY_DATABASE_ON_STARTUP", $previousDeployDatabaseOnStartup)
            [System.Environment]::SetEnvironmentVariable("AppSettings__DeployDatabaseOnStartup", $previousAppSettingsDeployDatabaseOnStartup)
        }
    }
    else {
        Write-Output "No bootstrap manifest detected; starting published DMS with database startup provisioning controlled by the environment file."
        docker compose $files --env-file $EnvironmentFile -p dms-published up $upArgs
    }

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

    if($IdentityProvider -eq "self-contained")
    {
        Write-Output "Init db public and private keys for OpenIddict..."
        ./setup-openiddict.ps1 -InitDb -EnvironmentFile $EnvironmentFile
    }

    Start-Sleep 10

    if($IdentityProvider -eq "self-contained")
    {
        Write-Output "Starting self-contained initialization script..."
        # Create client with default edfi_admin_api/full_access scope
        ./setup-openiddict.ps1 -InsertData -EnvironmentFile $EnvironmentFile

        # Create client with edfi_admin_api/readonly_access scope
        ./setup-openiddict.ps1 -InsertData -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -EnvironmentFile $EnvironmentFile

        # Create client with edfi_admin_api/authMetadata_readonly_access scope
        ./setup-openiddict.ps1 -InsertData -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access" -EnvironmentFile $EnvironmentFile
    }

    if (-not $SkipConnectorSetup) {
        Write-Output "Running connector setup..."
        ./setup-connectors.ps1 $EnvironmentFile
    }
    else {
        Write-Output "Skipping default connector setup."
    }

    if($AddSmokeTestCredentials)
    {
        Import-Module ../smoke_test/modules/SmokeTest.psm1 -Force
        Write-Output "Creating smoke test credentials..."
        $null = Get-SmokeTestCredentials -ConfigServiceUrl $cmsUrl

        Write-Output "Smoke test credentials created successfully!"
        Write-Output "Credential values were returned to the caller and were not written to logs."
    }

    if(-not $NoDataStore -or $SchoolYearRange)
    {
        Import-Module ../Dms-Management.psm1 -Force

        try {
            # Create system administrator credentials
            Add-CmsClient -CmsUrl $cmsUrl -ClientId "dms-data-store-admin" -ClientSecret "ValidClientSecret1234567890!Abcd" -DisplayName "Data Store Setup Administrator"

            # Get configuration service token
            $configToken = Get-CmsToken -CmsUrl $cmsUrl -ClientId "dms-data-store-admin" -ClientSecret "ValidClientSecret1234567890!Abcd"

            # Create tenant if multi-tenancy is enabled
            if ($envValues.DMS_CONFIG_MULTI_TENANCY -eq "true" -and $envValues.CONFIG_SERVICE_TENANT) {
                Write-Output "Multi-tenancy is enabled. Creating tenant: $($envValues.CONFIG_SERVICE_TENANT)"
                try {
                    $tenantId = Add-Tenant -CmsUrl $cmsUrl -AccessToken $configToken -TenantName $envValues.CONFIG_SERVICE_TENANT
                    Write-Output "Tenant created successfully with ID: $tenantId"
                }
                catch {
                    Write-Warning "Failed to create tenant (may already exist): $($_.Exception.Message)"
                }
            }

            # Get tenant from environment (for multi-tenant support)
            $tenant = $envValues.CONFIG_SERVICE_TENANT

            # Handle school year range data stores
            if ($SchoolYearRange) {
                Write-Output "Creating data stores for school year range: $SchoolYearRange"

                # Parse the range (format: StartYear-EndYear, e.g., "2022-2026")
                if ($SchoolYearRange -match '^(\d{4})-(\d{4})$') {
                    $startYear = [int]$matches[1]
                    $endYear = [int]$matches[2]

                    # Create data stores for each year in the range
                    $dataStores = Add-DmsSchoolYearInstances `
                        -CmsUrl $cmsUrl `
                        -AccessToken $configToken `
                        -StartYear $startYear `
                        -EndYear $endYear `
                        -PostgresPassword $envValues.POSTGRES_PASSWORD `
                        -PostgresDbName $envValues.POSTGRES_DB_NAME `
                        -Tenant $tenant

                    Write-Output "Created $($dataStores.Count) school year data stores successfully"
                }
                else {
                    Write-Warning "Invalid SchoolYearRange format. Expected format: StartYear-EndYear (e.g., 2022-2026)"
                }
            }
            # Handle single default data store
            elseif(-not $NoDataStore) {
                Write-Output "Creating initial data store..."

                # Create data store using environment variables
                $dataStoreId = Add-DataStore -CmsUrl $cmsUrl -AccessToken $configToken -PostgresPassword $envValues.POSTGRES_PASSWORD -PostgresDbName $envValues.POSTGRES_DB_NAME -Name "Local Development Data Store" -DataStoreType "Development" -Tenant $tenant

                Write-Output "Data store created successfully with ID: $dataStoreId"
            }
        }
        catch {
            throw "Failed to create data store(s): $($_.Exception.Message)"
        }
    }

    Start-Sleep 20
}
} finally {
    Restore-BootstrapEnvSnapshot -Snapshot $bootstrapEnvSnapshot
    Pop-Location
}

