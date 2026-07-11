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
    (-InfraOnly, -DmsOnly, -DbOnly). When invoked directly without a .bootstrap/ manifest
    the script proceeds but Invoke-BootstrapStartupConfiguration emits a warning: bootstrap
    schema provisioning will NOT happen here.

    -DbOnly: database container + readiness only; exists for diagnostics and for
    other tooling to sequence a database-only startup around.

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

    # Start only the database container and wait for readiness, then stop. Exists for
    # diagnostics and for other tooling to sequence a database-only startup around.
    # Mutually exclusive with -InfraOnly and -DmsOnly, and with -NoDataStore,
    # -SchoolYearRange, and -AddSmokeTestCredentials.
    [Switch]
    $DbOnly,

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
    $DataStandardVersion,

    # Database engine for the whole stack. "postgresql" (default) uses postgresql.yml.
    # "mssql" swaps in mssql.yml: SQL Server hosts the DMS datastore, the Configuration
    # Service (CMS SQL Server backend), and the self-contained OpenIddict identity stores -
    # no PostgreSQL container runs. The relational backend has no Debezium CDC (Kafka is
    # PostgreSQL-only and omitted). The .env.mssql overlay (DMS_DATASTORE=mssql,
    # DMS_CONFIG_DATASTORE=mssql, the MSSQL_* keys, and the SQL Server connection strings)
    # is composed automatically onto -EnvironmentFile. See mssql.yml and
    # Resolve-DatabaseEngineEnvironmentFile.
    [ValidateSet("postgresql", "mssql")]
    [string]
    $DatabaseEngine = "postgresql"
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
# Compose the MSSQL engine overlay for -DatabaseEngine mssql; this covers both direct invocation
# (a custom -EnvironmentFile still gets the overlay layered on top) and the bootstrap wrapper
# path (Resolve-DatabaseEngineEnvironmentFile detects the overlay is already composed via
# DMS_DATASTORE=mssql and returns the file unchanged, avoiding a derived-of-derived file).
$EnvironmentFile = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine $DatabaseEngine -BaseEnvironmentFile $EnvironmentFile -DockerComposeRoot $PSScriptRoot
$envValues = ReadValuesFromEnvFile $EnvironmentFile
$identityClientSecrets = Resolve-IdentityClientSecretConfiguration -EnvValues $envValues
$cmsUrl = Resolve-CmsBaseUrl -EnvValues $envValues
$dmsUrl = Resolve-DockerLocalDmsBaseUrl -EnvValues $envValues
$env:DMS_CONFIG_IDENTITY_PROVIDER=$IdentityProvider
Write-Output "Identity Provider $IdentityProvider"
Write-Output "Database Engine $DatabaseEngine"
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

    if ($DbOnly -and ($InfraOnly -or $DmsOnly)) {
        throw "Parameter -DbOnly is mutually exclusive with -InfraOnly and -DmsOnly."
    }

    if ($DmsOnly -and ($NoDataStore -or -not [string]::IsNullOrWhiteSpace($SchoolYearRange) -or $AddSmokeTestCredentials)) {
        throw "Parameters -NoDataStore, -SchoolYearRange, and -AddSmokeTestCredentials cannot be used with -DmsOnly."
    }

    if ($DbOnly -and ($NoDataStore -or -not [string]::IsNullOrWhiteSpace($SchoolYearRange) -or $AddSmokeTestCredentials)) {
        throw "Parameters -NoDataStore, -SchoolYearRange, and -AddSmokeTestCredentials cannot be used with -DbOnly."
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

# The database compose file is a swap: both postgresql.yml and mssql.yml define the same
# "db" service, so exactly one of them joins the compose set. On the mssql path SQL Server
# hosts everything - the DMS datastore, the Configuration Service (CMS SQL Server backend),
# and the self-contained OpenIddict identity stores - and no PostgreSQL container runs at all.
$databaseComposeFile = if ($DatabaseEngine -eq "mssql") { "mssql.yml" } else { "postgresql.yml" }
$files = @(
    "-f",
    $databaseComposeFile
)

if ($usePostgresqlTmpfs -and $DatabaseEngine -eq "postgresql") {
    $files += @("-f", $postgresqlTmpfsComposeFile)
}

$files += @("-f", "published-dms.yml")

# Kafka (and KafkaUI) back the PostgreSQL Debezium CDC path only and are opt-in via
# -EnableKafka / -EnableKafkaUI. The relational MSSQL path serves writes and queries directly
# from SQL and registers no connector, so Kafka is omitted.
$enableKafkaInfrastructure = $EnableKafka -or $EnableKafkaUI
if ($enableKafkaInfrastructure -and $DatabaseEngine -eq "postgresql") {
    $files += @("-f", "kafka.yml")
}

if ($IdentityProvider -eq "keycloak") {
    # Keep Keycloak in the managed compose set so follow-up up/down calls operate on the full environment.
    $files += @("-f", "keycloak.yml")
}

if ($EnableKafkaUI -and $DatabaseEngine -eq "postgresql") {
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

    function Wait-MssqlReady {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The SA password is read as plaintext from the environment file and handed to sqlcmd via the SQLCMDPASSWORD environment variable on docker exec (still visible in host-side docker argv); SecureString adds no protection across that boundary.')]
        param(
            [Parameter(Mandatory)]
            [string]
            $ContainerName,

            [Parameter(Mandatory)]
            [string]
            $Password,

            [int]
            $MaxAttempts = 40
        )

        # SQL Server can take 30+ seconds to accept connections on a cold start. Poll sqlcmd
        # the same way the CI start-mssql-test-container action does, so the schema provision
        # phase that follows always finds a reachable server.
        for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
            docker exec -e "SQLCMDPASSWORD=$Password" $ContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -Q "SELECT 1" -C -b *> $null
            if ($LASTEXITCODE -eq 0) {
                Write-Output "SQL Server is ready."
                return
            }

            if ($attempt -lt $MaxAttempts) {
                Start-Sleep -Seconds 3
            }
        }

        throw "SQL Server ($(Format-LogSafeText $ContainerName)) did not become ready within the timeout period."
    }

    function Wait-PostgresqlReady {
        param(
            [Parameter(Mandatory)]
            [string]
            $ContainerName,

            [int]
            $MaxAttempts = 40
        )

        # PostgreSQL can take a few seconds to accept connections on a cold start. Poll
        # pg_isready inside the container so the schema provision phase that follows
        # always finds a reachable server.
        for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
            docker exec $ContainerName pg_isready -U postgres *> $null
            if ($LASTEXITCODE -eq 0) {
                Write-Output "PostgreSQL is ready."
                return
            }

            if ($attempt -lt $MaxAttempts) {
                Start-Sleep -Seconds 3
            }
        }

        throw "PostgreSQL ($(Format-LogSafeText $ContainerName)) did not become ready within the timeout period."
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

    if ($DbOnly) {
        $databaseDisplayName = if ($DatabaseEngine -eq "mssql") { "SQL Server" } else { "Postgresql" }
        Write-Output "Starting $databaseDisplayName only..."
        docker compose $files --env-file $EnvironmentFile -p dms-published up $upArgs db
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to start $databaseDisplayName. Exit code $LASTEXITCODE"
        }

        if ($DatabaseEngine -eq "mssql") {
            $mssqlSaPassword =
                if ([string]::IsNullOrWhiteSpace($envValues.MSSQL_SA_PASSWORD)) {
                    "abcdefgh1!"
                }
                else {
                    $envValues.MSSQL_SA_PASSWORD
                }
            Wait-MssqlReady -ContainerName "dms-mssql" -Password $mssqlSaPassword
        }
        else {
            Wait-PostgresqlReady -ContainerName "dms-postgresql"
        }

        Write-Output "Database phase complete. Only the database container was started."
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

    $databaseDisplayName = if ($DatabaseEngine -eq "mssql") { "SQL Server" } else { "Postgresql" }
    Write-Output "Starting $databaseDisplayName..."
    docker compose $files --env-file $EnvironmentFile -p dms-published up $upArgs db
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to start $databaseDisplayName. Exit code $LASTEXITCODE"
    }

    if ($DatabaseEngine -eq "mssql") {
        # SQL Server accepts connections noticeably later than its container reports running;
        # poll before the phase commands need it. Default matches mssql.yml's compose default.
        $mssqlSaPassword =
            if ([string]::IsNullOrWhiteSpace($envValues.MSSQL_SA_PASSWORD)) {
                "abcdefgh1!"
            }
            else {
                $envValues.MSSQL_SA_PASSWORD
            }
        Wait-MssqlReady -ContainerName "dms-mssql" -Password $mssqlSaPassword
    }

    # Engine-aware database parameters for the setup-openiddict.ps1 calls below (mirrors
    # start-local-config.ps1). On SQL Server the OpenIddict stores live in the shared DMS
    # datastore database (MSSQL_DB_NAME), which CMS also uses now that the two share one
    # database; -InitDb creates it (and the dmscs schema) when missing, ahead of the CMS
    # startup deploy. On PostgreSQL the script defaults apply unchanged (shared
    # POSTGRES_DB_NAME database).
    $identityDbParams =
        if ($DatabaseEngine -eq "mssql") {
            @{ DbType = "MSSQL"; DbUser = "sa"; DbPort = "ENV:MSSQL_PORT"; DbName = "ENV:MSSQL_DB_NAME" }
        }
        else {
            @{}
        }

    Start-Sleep 20

    if ($InfraOnly) {
        if($IdentityProvider -eq "self-contained")
        {
            Write-Output "Init db public and private keys for OpenIddict..."
            ./setup-openiddict.ps1 -InitDb -EnvironmentFile $EnvironmentFile @identityDbParams
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
            ./setup-openiddict.ps1 -InsertData -NewClientSecret $identityClientSecrets.DmsConfigurationServiceClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile @identityDbParams
            ./setup-openiddict.ps1 -InsertData -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -NewClientSecret $identityClientSecrets.CmsReadOnlyAccessClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile @identityDbParams
            ./setup-openiddict.ps1 -InsertData -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access" -EnvironmentFile $EnvironmentFile @identityDbParams
        }

        if ($enableKafkaInfrastructure -and $DatabaseEngine -eq "postgresql") {
            Write-Output "Starting Kafka infrastructure..."
            docker compose $files --env-file $EnvironmentFile -p dms-published up $upArgs kafka kafka-postgresql-source
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to start Kafka infrastructure. Exit code $LASTEXITCODE"
            }
        }
        elseif ($enableKafkaInfrastructure -and $DatabaseEngine -eq "mssql") {
            Write-Output "Skipping Kafka infrastructure: the MSSQL relational path does not use Debezium CDC (PostgreSQL-only)."
        }

        if ($EnableKafkaUI -and $DatabaseEngine -eq "postgresql") {
            Write-Output "Starting Kafka UI..."
            docker compose $files --env-file $EnvironmentFile -p dms-published up $upArgs kafka-ui
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to start Kafka UI. Exit code $LASTEXITCODE"
            }
        }
        elseif ($EnableKafkaUI -and $DatabaseEngine -eq "mssql") {
            Write-Output "Skipping Kafka UI: the MSSQL relational path does not use Debezium CDC (PostgreSQL-only)."
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

    if($IdentityProvider -eq "self-contained")
    {
        Write-Output "Init db public and private keys for OpenIddict..."
        ./setup-openiddict.ps1 -InitDb -EnvironmentFile $EnvironmentFile @identityDbParams
    }

    Start-Sleep 10

    if($IdentityProvider -eq "self-contained")
    {
        Write-Output "Starting self-contained initialization script..."
        # Create client with default edfi_admin_api/full_access scope
        ./setup-openiddict.ps1 -InsertData -NewClientSecret $identityClientSecrets.DmsConfigurationServiceClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile @identityDbParams

        # Create client with edfi_admin_api/readonly_access scope
        ./setup-openiddict.ps1 -InsertData -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -NewClientSecret $identityClientSecrets.CmsReadOnlyAccessClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile @identityDbParams

        # Create client with edfi_admin_api/authMetadata_readonly_access scope
        ./setup-openiddict.ps1 -InsertData -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access" -EnvironmentFile $EnvironmentFile @identityDbParams
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

            # Resolve the data-store connection string stored in CMS for the DMS datastore. For
            # MSSQL this is the SQL Server form pointing at the dms-mssql container; for PostgreSQL
            # it is left empty so Add-DataStore builds its PostgreSQL connection string from the
            # Postgres* values.
            $dataStoreConnectionString = ""
            if ($DatabaseEngine -eq "mssql") {
                $mssqlPassword =
                    if ([string]::IsNullOrWhiteSpace($envValues.MSSQL_SA_PASSWORD)) {
                        "abcdefgh1!"
                    }
                    else {
                        $envValues.MSSQL_SA_PASSWORD
                    }
                $mssqlDbName =
                    if (-not [string]::IsNullOrWhiteSpace($DataStoreDatabaseName)) {
                        $DataStoreDatabaseName
                    }
                    elseif (-not [string]::IsNullOrWhiteSpace([string]$envValues.MSSQL_DB_NAME)) {
                        [string]$envValues.MSSQL_DB_NAME
                    }
                    else {
                        "edfi_datamanagementservice"
                    }
                $dataStoreConnectionString = New-DataStoreConnectionString `
                    -DatabaseEngine "mssql" `
                    -DbHost "dms-mssql" `
                    -Port 1433 `
                    -Username "sa" `
                    -Password $mssqlPassword `
                    -DatabaseName $mssqlDbName
            }

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
                        -ConnectionString $dataStoreConnectionString `
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
                $dataStoreId = Add-DataStore -CmsUrl $cmsUrl -AccessToken $configToken -PostgresCredential $postgresCredential -PostgresDbName $postgresDbName -ConnectionString $dataStoreConnectionString -Name "Local Development Data Store" -DataStoreType "Development" -Tenant $tenant

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
