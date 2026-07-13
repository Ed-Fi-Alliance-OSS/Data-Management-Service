# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Infrastructure-lifecycle phase for the local DMS Docker stack.
.DESCRIPTION
    This script is the infrastructure-lifecycle phase. It starts or stops the Docker
    services that underpin local DMS development (PostgreSQL or SQL Server, Config Service,
    optional Keycloak/SwaggerUI/Kafka/KafkaUI). The wrapper bootstrap-local-dms.ps1
    orchestrates prepare -> infra -> configure -> provision -> DMS-only, so by the
    time the wrapper calls into here a .bootstrap/ workspace and a provisioned database
    already exist.

    Direct invocation is supported for diagnostics and partial-phase orchestration
    (-InfraOnly, -DmsOnly). When invoked directly without a .bootstrap/ manifest the
    script proceeds but Invoke-BootstrapStartupConfiguration emits a warning: bootstrap
    schema provisioning will NOT happen here.

    BREAKING CHANGE (DMS-1153): The following flags have been removed from this script
    and relocated to phase-specific commands:
      -NoDataStore         -> configure-local-data-store.ps1 (instance selection)
      -SchoolYearRange     -> configure-local-data-store.ps1 (school-year data stores)
      -AddSmokeTestCredentials -> configure-local-data-store.ps1 (CMS-only smoke credentials)
      -LoadSeedData        -> load-dms-seed-data.ps1 (API-based seed delivery)

    Migration guidance:
      - For infrastructure + configure + provision + seed in one step:
          bootstrap-local-dms.ps1 [-LoadSeedData] [-AddSmokeTestCredentials] [other flags]
      - For manual phase-by-phase invocation:
          1. start-local-dms.ps1 -InfraOnly    (infrastructure only)
          2. configure-local-data-store.ps1    (instance creation / selection)
          3. provision-dms-schema.ps1          (schema provisioning)
          4. Launch DMS in IDE / debugger
          5. start-local-dms.ps1 -InfraOnly -DmsBaseUrl http://localhost:8080  (post-provision health wait)
          6. load-dms-seed-data.ps1            (optional seed delivery)
      - Scripts that previously passed -NoDataStore to this script should call
        configure-local-data-store.ps1 -NoDataStore after the -InfraOnly phase.

    IDE workflow shapes (requires -InfraOnly):
      -InfraOnly (terminal):
          Starts infrastructure and Config Service, runs the claims-ready gate, then
          stops. Use this as the pre-DMS preparation phase. After this returns, run
          configure-local-data-store.ps1 and provision-dms-schema.ps1, then launch
          DMS in your IDE. This shape does not perform data-store, provisioning,
          smoke-credential, or seed work.

      -InfraOnly -DmsBaseUrl <url> (health-wait continuation):
          Starts or verifies infrastructure (docker compose up is idempotent), runs
          the Config Service readiness check and the claims-ready gate, then polls
          <url>/health until HTTP 200 is returned. Use this after configure and
          provision phases have already been run and the IDE-hosted DMS process is
          launching. Times out after 300 seconds with a clear error if the DMS
          endpoint never becomes healthy.

    Example invocations:
      # Infrastructure pre-DMS stop (terminal):
      start-local-dms.ps1 -InfraOnly

      # Post-provision IDE health-wait continuation:
      start-local-dms.ps1 -InfraOnly -DmsBaseUrl http://localhost:8080

    See command-boundaries.md Section 3 for the phase contract and
    01-schema-deployment-safety.md for the DMS-1151 story.
.PARAMETER DmsBaseUrl
    The base URL of an IDE-hosted (externally launched) DMS process to health-wait.
    Valid only with -InfraOnly; not valid with -DmsOnly. When set the script starts
    or verifies infrastructure (docker compose up is idempotent), waits for Config
    Service readiness and the claims-ready gate, then polls <DmsBaseUrl>/health until
    HTTP 200 is returned or the 300-second timeout elapses. No data-store, schema
    provisioning, smoke-credential, or seed work is performed on this path — those
    are preconditions the caller must have already completed.
#>

[CmdletBinding()]
param (
    # Stop services instead of starting them
    [Switch]
    $d,

    # Delete volumes after stopping services
    [Switch]
    $v,

    # Environment file. When omitted, resolves eng/docker-compose/.env, seeding it once from
    # the tracked .env.example (the shared local-settings contract used by the phase commands), so
    # direct invocations - including teardown - work on a clean checkout with no hand-created .env.
    [string]
    $EnvironmentFile = "",

    # Force a rebuild
    [Switch]
    $r,

    # Enable Kafka and Kafka Connect infrastructure
    [Switch]
    $EnableKafka,

    # Enable Kafka UI. This also enables Kafka infrastructure.
    [Switch]
    $EnableKafkaUI,

    # Enable the DMS Configuration Service.
    # Retained for backward compatibility; Config Service is now always included in the compose set.
    # Per the bootstrap entry-point spec (DMS-1153), every non-teardown run starts Config Service,
    # including keycloak-backed runs. Passing this switch has no additional effect.
    [Switch]
    $EnableConfig,

    # Enable Swagger UI for the DMS API
    [switch]$EnableSwaggerUI,

    # Identity provider type. When omitted, resolved from the environment file's
    # DMS_CONFIG_IDENTITY_PROVIDER via Resolve-IdentityProvider (defaulting to self-contained),
    # matching the shared local-settings contract used by the other phase commands.
    [string]
    [ValidateSet("keycloak", "self-contained")]
    $IdentityProvider,

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
    # directory that is already mounted at /app/additional-claims by local-config.yml.
    # This flag is intentionally kept as a transitional helper for non-bootstrap extension E2E setups.
    [Switch]
    $AddExtensionSecurityMetadata,

    # Base URL of an IDE-hosted DMS process to health-wait after infrastructure and Config Service
    # are ready. Valid only with -InfraOnly; not valid with -DmsOnly. When set, the script polls
    # <DmsBaseUrl>/health until HTTP 200 is returned (300-second timeout). No data-store,
    # provisioning, smoke-credential, or seed work is performed — those are caller preconditions.
    # See .DESCRIPTION for the two -InfraOnly workflow shapes.
    [string]
    $DmsBaseUrl,

    # Optional Ed-Fi Data Standard version (e.g. "5.2", "6.1"). When supplied, the matching
    # .env.ds<NN> overlay is composed onto -EnvironmentFile so the stack runs that data standard.
    # Omit for the default (DS 5.2) behavior driven entirely by the base environment file.
    [string]
    $DataStandardVersion,

    # Database engine for the whole stack. "postgresql" (default) uses postgresql.yml.
    # "mssql" swaps in mssql.yml: SQL Server hosts the DMS datastore, the Configuration
    # Service (CMS SQL Server backend), and the self-contained OpenIddict identity stores —
    # no PostgreSQL container runs. The relational backend has no Debezium CDC (Kafka is
    # PostgreSQL-only and omitted). The .env.mssql overlay (DMS_DATASTORE=mssql,
    # DMS_CONFIG_DATASTORE=mssql, the MSSQL_* keys, and the SQL Server connection strings)
    # is composed automatically onto -EnvironmentFile. See mssql.yml and
    # Resolve-DatabaseEngineEnvironmentFile.
    [ValidateSet("postgresql", "mssql")]
    [string]
    $DatabaseEngine = "postgresql"
)

# Early fail-fast parameter validation — runs before any module import or Docker activity.
if ($PSBoundParameters.ContainsKey('DmsBaseUrl') -and -not [string]::IsNullOrWhiteSpace($DmsBaseUrl)) {
    if ($DmsOnly) {
        throw "-DmsBaseUrl is not valid with -DmsOnly. Use -InfraOnly -DmsBaseUrl <url> for the IDE health-wait continuation shape."
    }
    if (-not $InfraOnly) {
        throw "-DmsBaseUrl requires -InfraOnly. Use: start-local-dms.ps1 -InfraOnly -DmsBaseUrl <url>"
    }
}

Import-Module (Join-Path $PSScriptRoot "bootstrap-manifest.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "bootstrap-claims-gate.psm1") -Force
$originalLocation = Get-Location
Import-Module (Join-Path $PSScriptRoot "env-utility.psm1") -Force
if (-not [string]::IsNullOrWhiteSpace($EnvironmentFile)) {
    if (-not [System.IO.Path]::IsPathRooted($EnvironmentFile)) {
        # Caller supplied an explicit relative path - resolve against the caller's CWD.
        $EnvironmentFile = [System.IO.Path]::GetFullPath((Join-Path $originalLocation.Path $EnvironmentFile))
    }
}
else {
    # No explicit -EnvironmentFile: shared local-settings resolution (.env, seeded once from
    # the tracked .env.example when absent) so direct invocations - including the documented
    # teardown - work on a clean checkout with no hand-created .env, matching the phase commands.
    $EnvironmentFile = Resolve-LocalSettingsEnvironmentFile -Path "" -DockerComposeRoot $PSScriptRoot
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
# Shared local-settings contract: explicit -IdentityProvider wins, then the env file's
# DMS_CONFIG_IDENTITY_PROVIDER, then self-contained (Resolve-IdentityProvider treats an
# empty override as "not supplied").
$IdentityProvider = Resolve-IdentityProvider -EnvValues $envValues -OverrideProvider $IdentityProvider
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
# "db" service that local-config.yml gates on (depends_on: db: service_healthy), so exactly
# one of them joins the compose set. On the mssql path SQL Server hosts everything —
# the DMS datastore, the Configuration Service (CMS SQL Server backend), and the
# self-contained OpenIddict identity stores — and no PostgreSQL container runs at all.
$databaseComposeFile = if ($DatabaseEngine -eq "mssql") { "mssql.yml" } else { "postgresql.yml" }
$files = @(
    "-f",
    $databaseComposeFile
)

if ($usePostgresqlTmpfs -and $DatabaseEngine -eq "postgresql") {
    $files += @("-f", $postgresqlTmpfsComposeFile)
}

$files += @(
    "-f",
    "local-dms.yml"
)

$enableDotnetDiagnostics = [string]::Equals(
    (Get-EnvValue -EnvValues $envValues -Name "DMS_ENABLE_DOTNET_DIAGNOSTICS" -DefaultValue "false"),
    "true",
    [System.StringComparison]::OrdinalIgnoreCase
)
if ($enableDotnetDiagnostics) {
    Write-Output "Using .NET diagnostics Docker Compose override."
    $files += @("-f", "local-dms-diagnostics.yml")
}

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

# Config Service is always included in the managed compose set.
# Every non-teardown bootstrap run starts Config Service, including keycloak-backed runs.
# -EnableConfig is retained for backward compatibility but is no longer a meaningful opt-out
# (per the bootstrap entry-point spec, DMS-1153). Teardown uses the same $files list so that
# follow-up up/down calls operate on the full environment (same pattern as keycloak.yml above).
$files += @("-f", "local-config.yml")

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
    docker compose $files --env-file $EnvironmentFile -p dms-local down $downArgs
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
    if ($r) {
        Write-Output "Building images with no cache (this may take a few minutes)..."
        docker compose $files --env-file $EnvironmentFile -p dms-local build --no-cache
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to build images. Exit code $LASTEXITCODE"
        }
    }

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
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The SA password is read as plaintext from the environment file and handed to sqlcmd -P, which only accepts plaintext; SecureString adds no protection across that boundary.')]
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
            docker exec $ContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $Password -Q "SELECT 1" -C -b *> $null
            if ($LASTEXITCODE -eq 0) {
                Write-Output "SQL Server is ready."
                return
            }

            Start-Sleep -Seconds 3
        }

        throw "SQL Server ($(Format-LogSafeText $ContainerName)) did not become ready within the timeout period."
    }

    if ($DmsOnly) {
        Write-Output "Starting DMS service only..."
        $dmsServices = @("dms")
        if ($EnableSwaggerUI) {
            $dmsServices += "swagger-ui"
        }
        docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs $dmsServices

        if ($LASTEXITCODE -ne 0) {
            throw "Unable to start local DMS service, with exit code $LASTEXITCODE."
        }

        Wait-HttpEndpointHealthy -Url "$($dmsUrl.TrimEnd('/'))/health" -Name "DMS"
        Write-Output "DMS service is healthy."

        return
    }

    if($IdentityProvider -eq "keycloak")
    {
        Write-Output "Starting Keycloak..."
        docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs keycloak
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
    docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs db
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
    # start-local-config.ps1). On SQL Server the OpenIddict stores live in the CMS database —
    # -InitDb creates it (and the dmscs schema) when missing, ahead of the CMS startup deploy.
    # On PostgreSQL the script defaults apply unchanged (shared POSTGRES_DB_NAME database).
    $identityDbParams =
        if ($DatabaseEngine -eq "mssql") {
            @{ DbType = "MSSQL"; DbUser = "sa"; DbPort = "ENV:MSSQL_PORT"; DbName = "edfi_configurationservice" }
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
        docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs config
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
            docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs kafka kafka-postgresql-source
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to start Kafka infrastructure. Exit code $LASTEXITCODE"
            }
        }
        elseif ($enableKafkaInfrastructure -and $DatabaseEngine -eq "mssql") {
            Write-Output "Skipping Kafka infrastructure: the MSSQL relational path does not use Debezium CDC (PostgreSQL-only)."
        }

        if ($EnableKafkaUI -and $DatabaseEngine -eq "postgresql") {
            Write-Output "Starting Kafka UI..."
            docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs kafka-ui
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

        if ($PSBoundParameters.ContainsKey('DmsBaseUrl') -and -not [string]::IsNullOrWhiteSpace($DmsBaseUrl)) {
            # IDE health-wait continuation: the caller has already run configure and provision
            # phases externally and has launched a DMS process in the IDE. Poll the health
            # endpoint until it responds HTTP 200. The 300-second default is intentionally
            # generous — the developer may need time to attach and start the process.
            Write-Output "Waiting for IDE-hosted DMS at $(Format-LogSafeText $DmsBaseUrl) to become healthy (timeout: 300 seconds)..."
            Wait-HttpEndpointHealthy -Url "$($DmsBaseUrl.TrimEnd('/'))/health" -Name "DMS (IDE-hosted)" -TimeoutSeconds 300
            Write-Output "DMS (IDE-hosted) is healthy. Infrastructure and DMS health-wait complete."
        }
        else {
            # Terminal guidance contract (DMS-1153 AC): print actionable phase next-steps but do
            # NOT present a second start-local-dms.ps1 run as a resume mechanism. The wrapper
            # continuation shape is the supported health-wait path after a terminal stop.
            Write-Output "Infrastructure phase complete. DMS service was not started."
            Write-Output ""
            Write-Output "Next steps for the manual IDE / debugger phase flow:"
            Write-Output "  1. configure-local-data-store.ps1    (instance creation / selection)"
            Write-Output "  2. provision-dms-schema.ps1          (schema provisioning; prints IDE configuration guidance)"
            Write-Output "  3. Launch DMS in your IDE / debugger"
            Write-Output "  4. load-dms-seed-data.ps1 -DmsBaseUrl <url>   (optional seed delivery to the IDE-hosted DMS)"
            Write-Output "For a wrapper-managed health-wait and optional seed, run a fresh:"
            Write-Output "  bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl <url> [-LoadSeedData ...]"
        }
        return
    }

    if($IdentityProvider -eq "self-contained")
    {
        Write-Output "Init db public and private keys for OpenIddict..."
        ./setup-openiddict.ps1 -InitDb -EnvironmentFile $EnvironmentFile @identityDbParams
    }

    if ($bootstrapManifestPresent) {
        Write-Output "Bootstrap manifest detected; starting DMS."
        docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs
    }
    else {
        Write-Output "No bootstrap manifest detected; starting DMS."
        docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to start local Docker environment, with exit code $LASTEXITCODE."
    }

    Start-Sleep 20
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
    Start-Sleep 20
}
} finally {
    Restore-BootstrapEnvSnapshot -Snapshot $bootstrapEnvSnapshot
    Pop-Location
}
