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
    schema provisioning will NOT happen here.

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

    # Enable Kafka and Kafka Connect infrastructure
    [Switch]
    $EnableKafka,

    # Enable Kafka UI. This also enables Kafka infrastructure.
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
    # gate in bootstrap-design.md section 6.4 line 1250: removal is gated on the full bootstrap
    # verification gate closing (XSD-staging verification and BulkLoadClient XML mode validation).
    # The new API-based seed path, via load-dms-seed-data.ps1 + bootstrap-*-dms.ps1,
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

    # PostgreSQL database name to use when creating CMS data stores. Defaults to POSTGRES_DB_NAME from the environment file.
    [string]
    $DataStoreDatabaseName = "",

    # Start only infrastructure required before schema provisioning
    [Switch]
    $InfraOnly,

    # Start only the DMS service after external schema provisioning
    [Switch]
    $DmsOnly,

    # Remove the .bootstrap workspace during teardown (-d -v). Off by default so a prepared
    # workspace is preserved when the caller (e.g. build-dms.ps1) does not intend to wipe it.
    # A failed compose teardown throws before removal, so a still-running stack keeps its
    # bind-mounted schema and claims workspace.
    [Switch]
    $RemoveBootstrap,

    # Transitional non-bootstrap helper: when no bootstrap manifest is present,
    # passing this switch sets DMS_CONFIG_CLAIMS_SOURCE=Hybrid and DMS_CONFIG_CLAIMS_DIRECTORY=/app/additional-claims
    # so that extension claimset fragments (e.g. Sample, Homograph) are loaded from the AdditionalClaimsets
    # directory that is already mounted at /app/additional-claims by published-config.yml.
    # This flag is intentionally kept as a transitional helper for non-bootstrap extension E2E setups.
    [Switch]
    $AddExtensionSecurityMetadata,

    # Optional Ed-Fi Data Standard version (e.g. "5.2", "6.1"). When supplied, the matching
    # .env.ds<NN> overlay is composed onto -EnvironmentFile so the stack runs that data standard.
    # Omit for the default (DS 5.2) behavior driven entirely by the base environment file.
    [string]
    $DataStandardVersion
)

Import-Module (Join-Path $PSScriptRoot "bootstrap-manifest.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "bootstrap-claims-gate.psm1") -Force
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
$bootstrapMode = Invoke-BootstrapStartupConfiguration -IsTeardown:$d -AddExtensionSecurityMetadata:$AddExtensionSecurityMetadata
$bootstrapManifestPresent = Test-Path -LiteralPath (Join-Path (Get-BootstrapRoot) "bootstrap-manifest.json") -PathType Leaf

# Identity provider configuration
Import-Module ./env-utility.psm1 -Force
# Compose the data-standard overlay onto the base env file when a version is requested; with no
# -DataStandardVersion this returns the base file unchanged (DS 5.2 default).
$EnvironmentFile = Resolve-DataStandardEnvironmentFile -DataStandardVersion $DataStandardVersion -BaseEnvironmentFile $EnvironmentFile -DockerComposeRoot $PSScriptRoot
$envValues = ReadValuesFromEnvFile $EnvironmentFile
$identityClientSecrets = Resolve-IdentityClientSecretConfiguration -EnvValues $envValues
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
$usePostgresqlTmpfs = [string]::Equals(
    $env:POSTGRES_USE_TMPFS,
    "true",
    [System.StringComparison]::OrdinalIgnoreCase
)
$postgresqlTmpfsComposeFile = "postgresql-tmpfs.yml"
if ($usePostgresqlTmpfs) {
    $postgresqlTmpfsSize =
        if ([string]::IsNullOrWhiteSpace($env:POSTGRES_TMPFS_SIZE)) {
            "4g"
        }
        else {
            $env:POSTGRES_TMPFS_SIZE
        }
    $postgresqlContainerMemory =
        if ([string]::IsNullOrWhiteSpace($env:POSTGRES_CONTAINER_MEMORY)) {
            "10g"
        }
        else {
            $env:POSTGRES_CONTAINER_MEMORY
        }
    Write-Output "Using PostgreSQL tmpfs data directory (POSTGRES_TMPFS_SIZE=$postgresqlTmpfsSize, POSTGRES_CONTAINER_MEMORY=$postgresqlContainerMemory)."
}

$files = @(
    "-f",
    "postgresql.yml"
)

if ($usePostgresqlTmpfs) {
    $files += @("-f", $postgresqlTmpfsComposeFile)
}

$files += @("-f", "published-dms.yml")

$enableKafkaInfrastructure = $EnableKafka -or $EnableKafkaUI
if ($enableKafkaInfrastructure) {
    $files += @("-f", "kafka.yml")
}

if ($IdentityProvider -eq "keycloak") {
    # Keep Keycloak in the managed compose set so follow-up up/down calls operate on the full environment.
    $files += @("-f", "keycloak.yml")
}

if ($EnableKafkaUI) {
    $files += @("-f", "kafka-ui.yml")
}

# Include Configuration Service when requested, when needed for self-contained identity,
# or when bootstrap mode activates the staged claims workspace mount.
if ($EnableConfig -or $InfraOnly -or $IdentityProvider -eq "self-contained" -or $bootstrapMode) {
    $files += @("-f", "published-config.yml")
}

if ($bootstrapMode) {
    # Include bootstrap-dms.yml in the managed compose set so follow-up up/down calls operate
    # on the full environment (same pattern as keycloak.yml above). This mounts the staged
    # .bootstrap/ApiSchema workspace into the DMS container at /app/ApiSchema:ro.
    $files += @("-f", "bootstrap-dms.yml")
}

if ($EnableSwaggerUI) {
    $files += @("-f", "swagger-ui.yml")
}

if ($d) {
    $downArgs = @("--remove-orphans")
    if ($v) {
        $downArgs += "-v"
        Write-Output "Shutting down with volume delete"
    }
    else {
        Write-Output "Shutting down"
    }
    docker compose $files --env-file $EnvironmentFile -p dms-published down $downArgs
    # Fail before workspace removal: a failed down can leave services running against the
    # bind-mounted .bootstrap schema and claims, so removing the workspace would pull it
    # out from under a live stack.
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to shut down Docker environment. Exit code $LASTEXITCODE"
    }
    if ($v) {
        Remove-BootstrapWorkspaceIfRequested -RemoveBootstrap:$RemoveBootstrap
    }
}
else {
    $existingNetwork = docker network ls --filter name="dms" -q
    if (! $existingNetwork) {
        docker network create dms
    }

    $upArgs = @(
        "--detach",
        "--remove-orphans"
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
        Write-Output "Starting published DMS service only..."
        $dmsServices = @("dms")
        if ($EnableSwaggerUI) {
            $dmsServices += "swagger-ui"
        }
        docker compose $files --env-file $EnvironmentFile -p dms-published up $upArgs $dmsServices

        if ($LASTEXITCODE -ne 0) {
            throw "Unable to start published DMS service, with exit code $LASTEXITCODE."
        }

        Wait-HttpEndpointHealthy -Url "$($dmsUrl.TrimEnd('/'))/health" -Name "DMS"
        Write-Output "DMS service is healthy."

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
        ./setup-keycloak.ps1 -NewClientSecret $identityClientSecrets.DmsConfigurationServiceClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength

        # Create client with edfi_admin_api/readonly_access scope
        ./setup-keycloak.ps1 -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -NewClientSecret $identityClientSecrets.CmsReadOnlyAccessClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength

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
            ./setup-openiddict.ps1 -InsertData -NewClientSecret $identityClientSecrets.DmsConfigurationServiceClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile
            ./setup-openiddict.ps1 -InsertData -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -NewClientSecret $identityClientSecrets.CmsReadOnlyAccessClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile
            ./setup-openiddict.ps1 -InsertData -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access" -EnvironmentFile $EnvironmentFile
        }

        if ($enableKafkaInfrastructure) {
            Write-Output "Starting Kafka infrastructure..."
            docker compose $files --env-file $EnvironmentFile -p dms-published up $upArgs kafka kafka-postgresql-source
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to start Kafka infrastructure. Exit code $LASTEXITCODE"
            }
        }

        if ($EnableKafkaUI) {
            Write-Output "Starting Kafka UI..."
            docker compose $files --env-file $EnvironmentFile -p dms-published up $upArgs kafka-ui
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to start Kafka UI. Exit code $LASTEXITCODE"
            }
        }

        # Claims-ready gate: prove CMS has applied the expected claims content before
        # instance configuration begins. Runs only on bootstrap-manifest runs; skipped
        # with an informational message on no-bootstrap invocations.
        if ($bootstrapManifestPresent) {
            Write-Output "Running claims-ready gate..."
            Test-CmsClaimsReady `
                -EnvironmentFile $EnvironmentFile `
                -IdentityProvider $IdentityProvider
        }
        else {
            Write-Information "Claims gate: no bootstrap manifest present; skipping claims-ready check on no-bootstrap run." -InformationAction Continue
        }

        Write-Output "Infrastructure phase complete. DMS service was not started."
        return
    }


    Write-Output "Starting published DMS"
    if ($bootstrapManifestPresent) {
        Write-Output "Bootstrap manifest detected; starting published DMS."
        docker compose $files --env-file $EnvironmentFile -p dms-published up $upArgs
    }
    else {
        Write-Output "No bootstrap manifest detected; starting published DMS."
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
        ./setup-openiddict.ps1 -InsertData -NewClientSecret $identityClientSecrets.DmsConfigurationServiceClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile

        # Create client with edfi_admin_api/readonly_access scope
        ./setup-openiddict.ps1 -InsertData -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -NewClientSecret $identityClientSecrets.CmsReadOnlyAccessClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile

        # Create client with edfi_admin_api/authMetadata_readonly_access scope
        ./setup-openiddict.ps1 -InsertData -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access" -EnvironmentFile $EnvironmentFile
    }

    if($AddSmokeTestCredentials)
    {
        Import-Module ../smoke_test/modules/SmokeTest.psm1 -Force
        Write-Output "Creating smoke test credentials..."
        $null = Get-SmokeTestCredential -ConfigServiceUrl $cmsUrl

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
            $postgresDbName =
                if ([string]::IsNullOrWhiteSpace($DataStoreDatabaseName)) {
                    $envValues.POSTGRES_DB_NAME
                }
                else {
                    $DataStoreDatabaseName
                }
            $postgresUser =
                if ([string]::IsNullOrWhiteSpace([string]$envValues.POSTGRES_USER)) {
                    "postgres"
                }
                else {
                    [string]$envValues.POSTGRES_USER
                }
            $postgresCredential = ConvertTo-PostgresCredential -UserName $postgresUser -Secret $envValues.POSTGRES_PASSWORD

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
                        -PostgresCredential $postgresCredential `
                        -PostgresDbName $postgresDbName `
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
                $dataStoreId = Add-DataStore -CmsUrl $cmsUrl -AccessToken $configToken -PostgresCredential $postgresCredential -PostgresDbName $postgresDbName -Name "Local Development Data Store" -DataStoreType "Development" -Tenant $tenant

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
