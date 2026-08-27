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

    # Database name to use when creating CMS data stores. Defaults to POSTGRES_DB_NAME for
    # PostgreSQL or MSSQL_DB_NAME for SQL Server from the effective environment file.
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
    $DatabaseEngine = "postgresql",

    # Redirects the CMS (Configuration Service) database to a dedicated edfi_configurationservice
    # database instead of sharing the DMS datastore database. Applies only when CMS actually
    # participates (the default/-InfraOnly shape); has no effect with -DmsOnly/-DbOnly/-d, where
    # CMS does not start. Also adds published-config.yml to the managed compose set even when no
    # other condition would (e.g. -IdentityProvider keycloak without -EnableConfig/-InfraOnly),
    # since CMS must actually run to create the separate database. Supported on both database
    # engines.
    [Switch]
    $SeparateConfigDatabase
)

$databaseOnlyStartup = $DbOnly -and -not $d
if (-not $databaseOnlyStartup) {
    # Database-only startup must not depend on bootstrap module loading or workspace state.
    # Teardown keeps the normal full-stack behavior, including bootstrap cleanup support.
    Import-Module (Join-Path $PSScriptRoot "bootstrap-manifest.psm1") -Force
    Import-Module (Join-Path $PSScriptRoot "bootstrap-claims-gate.psm1") -Force
}
Import-Module (Join-Path $PSScriptRoot "env-utility.psm1") -Force
# Shared Compose-equivalent resolver so the readiness probes and the CMS data-store creation below
# use the same port/password/database values the containers received (an ambient process/shell
# value wins over the env file), matching start-local-dms.ps1 and the configure/provision phases.
Import-Module (Join-Path $PSScriptRoot "database-safety.psm1") -Force
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
if (-not $databaseOnlyStartup) {
    $bootstrapEnvSnapshot = Get-BootstrapEnvSnapshot
}
Push-Location $PSScriptRoot
try {
$bootstrapMode = $false
$bootstrapManifestPresent = $false
if (-not $databaseOnlyStartup) {
    # Database-only startup is deliberately independent of bootstrap state. A stale or
    # incomplete workspace must not block the diagnostic database + readiness phase, and
    # no bootstrap environment values or compose mounts may leak into that phase.
    $bootstrapMode = Invoke-BootstrapStartupConfiguration -IsTeardown:$d -AddExtensionSecurityMetadata:$AddExtensionSecurityMetadata
    $bootstrapManifestPresent = Test-Path -LiteralPath (Join-Path (Get-BootstrapRoot) "bootstrap-manifest.json") -PathType Leaf
}

# Compose the data-standard overlay onto the base env file when a version is requested; with no
# -DataStandardVersion this returns the base file unchanged (DS 5.2 default).
$EnvironmentFile = Resolve-DataStandardEnvironmentFile -DataStandardVersion $DataStandardVersion -BaseEnvironmentFile $EnvironmentFile -DockerComposeRoot $PSScriptRoot
# Compose the MSSQL engine overlay for -DatabaseEngine mssql; this covers both direct invocation
# (a custom -EnvironmentFile still gets the overlay layered on top) and the bootstrap wrapper
# path (Resolve-DatabaseEngineEnvironmentFile detects the overlay is already composed via
# DMS_DATASTORE=mssql and returns the file unchanged, avoiding a derived-of-derived file).
# DbOnly and teardown skip the CMS/OpenIddict invariant because neither initializes identity data.
#
# Whether published-config.yml joins the managed compose set, i.e. whether the Configuration
# Service actually runs. Unlike local-config.yml (unconditional in start-local-dms.ps1), the
# published stack includes CMS only on an explicit request, for self-contained identity, for the
# bootstrap claims mount, or - this story's own addition - for -SeparateConfigDatabase, since CMS
# must run to create the dedicated database. Computed once here and consumed both by the
# CMS-participation gate below and by the compose-file-set construction later, so the two can
# never drift apart.
$cmsIncludedInComposeSet = $EnableConfig -or $InfraOnly -or $IdentityProvider -eq "self-contained" -or $bootstrapMode -or $SeparateConfigDatabase

# CMS participates only when it is both a forward-starting non-DMS-only shape AND actually present
# in the compose set - not -DmsOnly (CMS doesn't start), -DbOnly, or teardown (-d), and not a bare
# published Keycloak start that omits published-config.yml entirely. Non-participating shapes get
# structural validation only; every participating MSSQL shape is verified physically on the
# running server after readiness (Assert-MssqlTopologyPhysicalConsistency), in shared and
# separate mode alike. Validating a CMS endpoint for a CMS that never starts would reject an
# irrelevant customized value, so the compose-set conjunct is load-bearing, not cosmetic.
$cmsParticipates = (-not ($databaseOnlyStartup -or $d -or $DmsOnly)) -and $cmsIncludedInComposeSet

if ($cmsParticipates) {
    $EnvironmentFile = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine $DatabaseEngine -BaseEnvironmentFile $EnvironmentFile -DockerComposeRoot $PSScriptRoot -SkipMssqlCmsDatabaseValidation:$true

    # Both engines run the same topology-write sequence, so shared and separate mode are symmetric
    # across PostgreSQL and SQL Server. The profile files and both .yml inline fallbacks carry the
    # topology seam in their database segment; the fallbacks' host, port, and credentials stay
    # PostgreSQL-shaped because Compose interpolation cannot branch on the engine, which is why an
    # MSSQL run must supply DMS_CONFIG_DATABASE_CONNECTION_STRING explicitly (the .env.mssql overlay
    # always does).
    $EnvironmentFile = Resolve-CmsDatabaseTopologyEnvironmentFile -BaseEnvironmentFile $EnvironmentFile -DatabaseEngine $DatabaseEngine -SeparateConfigDatabase:$SeparateConfigDatabase -DockerComposeRoot $PSScriptRoot
    Confirm-CmsDatabaseTopologyAgreement -EnvironmentFile $EnvironmentFile -DatabaseEngine $DatabaseEngine
}
else {
    # CMS does not participate in this shape, so the topology sequence above does not run and this
    # path composes the engine overlay only - structural validation, no database-NAME verdict. A
    # SQL Server name relationship is the running instance's collation's call, and this shape never
    # starts a server to ask, so a documented "accepted, gated no-op" continuation like
    # `-DmsOnly -SeparateConfigDatabase` against the original -EnvironmentFile proceeds regardless
    # of which database its CMS connection string names.
    # -SkipMssqlCmsDatabaseValidation is retained at this call site for compatibility and is a
    # documented no-op; the switch value is preserved so the argument still records the intent.
    $EnvironmentFile = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine $DatabaseEngine -BaseEnvironmentFile $EnvironmentFile -DockerComposeRoot $PSScriptRoot -SkipMssqlCmsDatabaseValidation:($databaseOnlyStartup -or $d -or $SeparateConfigDatabase)
}
$envValues = ReadValuesFromEnvFile $EnvironmentFile
if (-not $databaseOnlyStartup) {
    # Identity/CMS/DMS settings are application concerns. Keeping them outside DbOnly means an
    # unrelated malformed identity value cannot block the database + readiness diagnostic slice.
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
}
Write-Output "Database Engine $DatabaseEngine"

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

    # The DMS datastore database registered in the CMS data-store record is chosen AFTER topology
    # validation has already run - so it could silently reintroduce the very sharing
    # -SeparateConfigDatabase exists to remove. On PostgreSQL distinctness is enforced here, at
    # the same fail-fast boundary as the other parameter rules, but deliberately AFTER them: this check
    # is new, and an invalid parameter shape must keep reporting the established diagnostic that
    # describes it. Placed first, it masked "-NoDataStore and -SchoolYearRange are mutually exclusive"
    # for a caller who had made that mistake and happened to also pass the reserved database name.
    #
    # The guard judges the EFFECTIVE name that will actually be registered, not just the parameter:
    # -DataStoreDatabaseName when supplied, and otherwise the Compose-resolved POSTGRES_DB_NAME
    # fallback the registration itself computes. That is anti-drift hardening, NOT a hole this closes:
    # when the parameter is omitted the effective name IS POSTGRES_DB_NAME, and every colliding value
    # reachable that way is already refused earlier by Confirm-CmsDatabaseTopologyAgreement's
    # initialized-path authority (Compose value semantics trim a declaration's trailing whitespace, so
    # an env-file name arrives as the exact reserved literal, which that authority rejects). The one
    # input where the two authorities disagree - a bare trailing line feed, which this registered
    # transport collapses and createdb preserves - can only arrive through -DataStoreDatabaseName,
    # which bypasses Compose resolution, and that remains the load-bearing case for this guard.
    # Resolving the effective name into one variable that the registration below reuses means the
    # guarded value and the registered value cannot drift apart if either ordering or the set of value
    # sources changes later.
    #
    # It applies only to a shape that actually reaches the data-store registration below: -InfraOnly,
    # -DmsOnly, and -DbOnly all return before it, and -NoDataStore skips it. Because -NoDataStore and
    # -SchoolYearRange are mutually exclusive - rejected immediately above - -NoDataStore always means the
    # registration is skipped, so the parameter is inert in that shape too and the switch combination
    # stays a no-op, matching continuation behavior.
    #
    # WHETHER a name collides is decided by Test-RegisteredDatastoreNameCollidesWithReservedCmsDatabase,
    # and that is deliberately NOT the predicate Confirm-CmsDatabaseTopologyAgreement uses. The two ask
    # about different physical creation mechanisms, and this call site asks only about the REGISTERED
    # transport: whichever name is effective, it is
    # serialized into the datastore connection string registered in CMS below, parsed back by the
    # provider, and SchemaTools creates the database with a QUOTED identifier - so on PostgreSQL nothing
    # folds on either path: only a name the provider parses back AS the reserved name collides (the exact
    # name, or - the measured exception, and now the one input where the two mechanisms still differ - one
    # whose bare trailing line feed this transport collapses while createdb preserves it), and a
    # mixed-case name is a genuinely distinct database. Sharing one engine-neutral
    # predicate across both call sites is what made this script reject that distinct PostgreSQL database
    # while the validator accepted a colliding one. The INITIALIZED path is not re-judged here: when the
    # effective name comes from POSTGRES_DB_NAME that value also reaches postgresql-init.sh's createdb,
    # and Confirm-CmsDatabaseTopologyAgreement owns that mechanism - which is why the omitted-parameter
    # shape is already refused there rather than here.
    #
    # PostgreSQL only: that offline verdict is sound because it models the exact provider transport.
    # SQL Server renders NO offline verdict - database names inherit the INSTANCE collation, so MSSQL
    # distinctness (for this parameter too, as the PARSED value the provider receives) is decided by
    # the server-backed authority against the running instance, after the database container starts
    # and before the registration below runs.
    $dataStoreRegistrationRuns = -not ($InfraOnly -or $DmsOnly -or $DbOnly -or $NoDataStore)
    # The single source of truth for the PostgreSQL datastore database this run will register. The
    # registration below reuses this exact variable, so the guarded value is the registered value.
    # Every path that reaches the registration passes through here first: the registration lives in the
    # else branch of `if ($d)`, which this block precedes.
    $effectivePostgresDataStoreName =
        if ([string]::IsNullOrWhiteSpace($DataStoreDatabaseName)) {
            Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "POSTGRES_DB_NAME" -DefaultValue "edfi_datamanagementservice"
        }
        else {
            $DataStoreDatabaseName
        }
    if ($SeparateConfigDatabase -and $dataStoreRegistrationRuns -and $DatabaseEngine -eq "postgresql" -and
        (Test-RegisteredDatastoreNameCollidesWithReservedCmsDatabase -DatabaseEngine $DatabaseEngine -DatastoreDatabaseName $effectivePostgresDataStoreName)) {
        # Names the parameter and the reserved literal only - never the caller's own value.
        throw "-DataStoreDatabaseName must be provably distinct from 'edfi_configurationservice' with -SeparateConfigDatabase: that is the dedicated Configuration Service database, and pointing the DMS datastore at it would reintroduce the shared topology the switch opts out of. On PostgreSQL the name is compared as the provider parses it - SchemaTools creates it with a quoted identifier, so nothing folds; only a name that parses back to that reserved name collides, and the measured non-exact case is a trailing line feed, which connection-string parsing removes."
    }

    # Resolved once with Compose precedence and reused by the data-store creation below, so the
    # tenant/multi-tenancy decision matches what the CMS container received.
    $multiTenancyEnabled = (Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "DMS_CONFIG_MULTI_TENANCY") -eq "true"
    $configServiceTenant = Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "CONFIG_SERVICE_TENANT"
    if (-not [string]::IsNullOrWhiteSpace($SchoolYearRange) -and $multiTenancyEnabled -and [string]::IsNullOrWhiteSpace($configServiceTenant)) {
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

if (-not $databaseOnlyStartup) {
    $files += @("-f", "published-dms.yml")

    # Kafka (and KafkaUI) back the PostgreSQL Debezium CDC path only and are opt-in via
    # -EnableKafka / -EnableKafkaUI. The relational MSSQL path serves writes and queries directly
    # from SQL and registers no connector, so Kafka is omitted.
    $enableKafkaInfrastructure = $EnableKafka -or $EnableKafkaUI
    if ($enableKafkaInfrastructure -and $DatabaseEngine -eq "postgresql") {
        $files += @("-f", "kafka.yml")
    }

    # Keep Keycloak in the managed compose set so follow-up up/down calls operate on the full
    # environment. Teardown (-d) always includes it: the identity provider is resolved from the
    # environment file, which need not name the provider the running stack was started with, and a
    # compose file left out of the down set takes its named volume (dms-keycloak) with it, leaking
    # <project>_dms-keycloak past `down -v`.
    if ($d -or $IdentityProvider -eq "keycloak") {
        $files += @("-f", "keycloak.yml")
    }

    if ($EnableKafkaUI -and $DatabaseEngine -eq "postgresql") {
        $files += @("-f", "kafka-ui.yml")
    }

    # Include Configuration Service when requested, when needed for self-contained identity, when
    # bootstrap mode activates the staged claims workspace mount, or when -SeparateConfigDatabase is
    # requested: CMS must actually run to create the dedicated database, regardless of identity
    # provider (e.g. keycloak without -EnableConfig/-InfraOnly would otherwise omit it). The
    # condition itself is computed once, well above, and shared with the CMS-participation gate.
    if ($cmsIncludedInComposeSet) {
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

    $upArgs = @("--detach")
    if (-not $databaseOnlyStartup) {
        # The DbOnly compose set intentionally contains only the database definition. Passing
        # --remove-orphans there would remove already-running DMS/CMS containers from this project.
        $upArgs += "--remove-orphans"
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
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The SA password is read as plaintext from the environment file and handed to sqlcmd via the SQLCMDPASSWORD environment variable on docker exec (still visible in host-side docker argv); SecureString adds no protection across that boundary.')]
        param(
            [Parameter(Mandatory)]
            [string]
            $ContainerName,

            [Parameter(Mandatory)]
            [string]
            $Password,

            [int]
            $TimeoutSeconds = 120
        )

        # SQL Server can take 30+ seconds to accept connections on a cold start. Poll sqlcmd
        # the same way the CI start-mssql-test-container action does, so the schema provision
        # phase that follows always finds a reachable server.
        $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
        while ([datetime]::UtcNow -lt $deadline) {
            $remainingSeconds = [math]::Max(1, [math]::Ceiling(($deadline - [datetime]::UtcNow).TotalSeconds))
            $probeTimeoutSeconds = [math]::Min(10, $remainingSeconds)
            $probeArguments = @(
                "exec", "-e", "SQLCMDPASSWORD=$Password", $ContainerName,
                "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa",
                "-Q", "SELECT 1", "-C", "-b"
            )
            if (Test-NativeCommandWithTimeout -FilePath "docker" -ArgumentList $probeArguments -TimeoutSeconds $probeTimeoutSeconds) {
                Write-Output "SQL Server is ready."
                return
            }

            if ([datetime]::UtcNow -lt $deadline) {
                Start-Sleep -Seconds ([math]::Min(3, [math]::Max(1, [math]::Floor(($deadline - [datetime]::UtcNow).TotalSeconds))))
            }
        }

        throw "SQL Server ($(Format-LogSafeText $ContainerName)) did not become ready within $TimeoutSeconds seconds."
    }

    function Wait-PostgresqlReady {
        param(
            [Parameter(Mandatory)]
            [string]
            $ContainerName,

            [int]
            $TimeoutSeconds = 120
        )

        # PostgreSQL can take a few seconds to accept connections on a cold start. Poll
        # pg_isready inside the container so the schema provision phase that follows
        # always finds a reachable server.
        $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
        while ([datetime]::UtcNow -lt $deadline) {
            $remainingSeconds = [math]::Max(1, [math]::Ceiling(($deadline - [datetime]::UtcNow).TotalSeconds))
            $probeArguments = @("exec", $ContainerName, "pg_isready", "-U", "postgres")
            if (Test-NativeCommandWithTimeout -FilePath "docker" -ArgumentList $probeArguments -TimeoutSeconds ([math]::Min(10, $remainingSeconds))) {
                Write-Output "PostgreSQL is ready."
                return
            }

            if ([datetime]::UtcNow -lt $deadline) {
                Start-Sleep -Seconds ([math]::Min(3, [math]::Max(1, [math]::Floor(($deadline - [datetime]::UtcNow).TotalSeconds))))
            }
        }

        throw "PostgreSQL ($(Format-LogSafeText $ContainerName)) did not become ready within $TimeoutSeconds seconds."
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
            $mssqlSaPassword = Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "MSSQL_SA_PASSWORD" -DefaultValue "abcdefgh1!"
            Wait-MssqlReady -ContainerName "dms-mssql" -Password $mssqlSaPassword
        }
        else {
            Wait-PostgresqlReady -ContainerName "dms-postgresql"
        }

        Write-Output "Database phase complete. Only the database container was started."
        return
    }

    # The two role names CMS enforces are supported overrides: published-config.yml maps
    # IdentitySettings:ConfigServiceRole / :ClientRole from DMS_CONFIG_IDENTITY_SERVICE_ROLE /
    # DMS_CONFIG_IDENTITY_CLIENT_ROLE, and BOTH identity setup scripts -- setup-keycloak.ps1 and
    # setup-openiddict.ps1 -InsertData -- fall back to their own cms-client / dms-client defaults when
    # the parameters are omitted. Resolved once, above the provider branch, so neither provider
    # registers clients against a role the configured CMS does not require: that registration
    # succeeds and tokens mint, and the failure surfaces later as DMS unable to read claim sets. The
    # defaults are the compose file's own fallbacks, so an unset override resolves exactly as before.
    $identityRoleParams = @{
        ConfigServiceRole = Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "DMS_CONFIG_IDENTITY_SERVICE_ROLE" -DefaultValue "cms-client"
        DmsClientRole     = Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "DMS_CONFIG_IDENTITY_CLIENT_ROLE" -DefaultValue "dms-client"
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
        ./setup-keycloak.ps1 @identityRoleParams -NewClientSecret $identityClientSecrets.DmsConfigurationServiceClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength

        # Create client with edfi_admin_api/readonly_access scope
        ./setup-keycloak.ps1 @identityRoleParams -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -NewClientSecret $identityClientSecrets.CmsReadOnlyAccessClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength

        # Create client with edfi_admin_api/authMetadata_readonly_access scope
        ./setup-keycloak.ps1 @identityRoleParams -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access"
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
        $mssqlSaPassword = Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "MSSQL_SA_PASSWORD" -DefaultValue "abcdefgh1!"
        Wait-MssqlReady -ContainerName "dms-mssql" -Password $mssqlSaPassword

        if ($cmsParticipates) {
            # The live topology check for EVERY CMS-participating MSSQL start: the RUNNING SQL
            # Server - the only authority on its own database-name semantics - verifies that the
            # CMS targets (the seam and every connection-string segment) are physically the
            # database the effective topology expects, and in separate mode that the datastore
            # candidates stay physically distinct from the dedicated Configuration Service
            # database. The authority reads the effective file's own topology marker (raw) to
            # select the mode; on this participating path the topology resolver above just
            # recomputed the topology from the switch, so the marker in the file it RETURNED is
            # the current declaration.
            # Placed hard against readiness so a violated relation, or any inability to verify
            # (it fails closed), stops the start after the database container exists but before
            # OpenIddict, CMS, DMS, or the data-store registration below touches it. The
            # registered candidate joins
            # only when that registration will actually run, and as the value a provider
            # RECEIVES - never the raw parameter text, which the connection-string transport
            # can differ from (a bare trailing line feed is removed by it); the authority
            # applies it only where it participates (the separate mode's distinctness rule).
            $registeredDatastoreDatabaseValue = ""
            if ($dataStoreRegistrationRuns -and -not [string]::IsNullOrWhiteSpace($DataStoreDatabaseName)) {
                $registeredDatastoreDatabaseValue = Get-RegisteredDatastoreDatabaseValue -DatastoreDatabaseName $DataStoreDatabaseName
            }
            Assert-MssqlTopologyPhysicalConsistency `
                -EnvironmentFile $EnvironmentFile `
                -ContainerName "dms-mssql" `
                -SaPassword $mssqlSaPassword `
                -RegisteredDatastoreDatabaseName $registeredDatastoreDatabaseValue
        }
    }

    # Engine-aware database parameters for the setup-openiddict.ps1 calls below. On both engines the
    # OpenIddict stores live in whichever database CMS itself targets - DMS_CONFIG_DATABASE_NAME,
    # the CMS database topology seam (DMS-1270): the shared DMS datastore database by default, or
    # the dedicated edfi_configurationservice database when -SeparateConfigDatabase redirects CMS
    # there. -InitDb creates that database (and the dmscs schema) when missing, ahead of the CMS
    # startup deploy. Only the connection details differ per engine; the database name is resolved
    # from the same seam either way, so the two modes stay symmetric across engines.
    $identityDbParams =
        if ($DatabaseEngine -eq "mssql") {
            @{ DbType = "MSSQL"; DbUser = "sa"; DbPort = "ENV:MSSQL_PORT"; DbName = "ENV:DMS_CONFIG_DATABASE_NAME" }
        }
        else {
            # POSTGRES_USER is a supported override - postgresql.yml passes ${POSTGRES_USER:-postgres}
            # to the container - and setup-openiddict.ps1 defaults DbUser to postgres. Because these
            # calls pass -EnvironmentFile, Get-EffectiveConnectionString always builds the connection
            # string from this parameter group, so that default would reach psql as Username=postgres
            # and a stack using the override would fail to connect before the OpenIddict stores exist.
            # Resolved with the same Compose precedence the container itself saw, and with the same
            # default the compose file falls back to, so an unset override resolves exactly as before.
            @{
                DbUser = Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "POSTGRES_USER" -DefaultValue "postgres"
                DbName = "ENV:DMS_CONFIG_DATABASE_NAME"
            }
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
            ./setup-openiddict.ps1 -InsertData @identityRoleParams -NewClientSecret $identityClientSecrets.DmsConfigurationServiceClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile @identityDbParams
            ./setup-openiddict.ps1 -InsertData @identityRoleParams -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -NewClientSecret $identityClientSecrets.CmsReadOnlyAccessClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile @identityDbParams
            ./setup-openiddict.ps1 -InsertData @identityRoleParams -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access" -EnvironmentFile $EnvironmentFile @identityDbParams
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

    # Published-ordering fix (DMS-1270): -InitDb must run before the compose set that includes CMS
    # is started, closing a genuine, pre-existing latent race in the shared-mode published
    # self-contained flow - CMS deploying its dmscs schema before OpenIddict's database/keys exist.
    # The "db" service was already started and confirmed ready earlier in this script, so -InitDb
    # (which needs only the database) can safely run here, ahead of "up $upArgs" starting CMS/DMS.
    # Not gated by -DatabaseEngine: this structural fix applies to both engines equally.
    if($IdentityProvider -eq "self-contained")
    {
        Write-Output "Init db public and private keys for OpenIddict..."
        ./setup-openiddict.ps1 -InitDb -EnvironmentFile $EnvironmentFile @identityDbParams
    }

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
    Start-Sleep 10

    if($IdentityProvider -eq "self-contained")
    {
        Write-Output "Starting self-contained initialization script..."
        # Create client with default edfi_admin_api/full_access scope
        ./setup-openiddict.ps1 -InsertData @identityRoleParams -NewClientSecret $identityClientSecrets.DmsConfigurationServiceClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile @identityDbParams

        # Create client with edfi_admin_api/readonly_access scope
        ./setup-openiddict.ps1 -InsertData @identityRoleParams -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -NewClientSecret $identityClientSecrets.CmsReadOnlyAccessClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile @identityDbParams

        # Create client with edfi_admin_api/authMetadata_readonly_access scope
        ./setup-openiddict.ps1 -InsertData @identityRoleParams -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access" -EnvironmentFile $EnvironmentFile @identityDbParams
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

            # Create tenant if multi-tenancy is enabled. Both values were resolved above with
            # Compose precedence so the tenant registered here matches the CMS container's view.
            if ($multiTenancyEnabled -and -not [string]::IsNullOrWhiteSpace($configServiceTenant)) {
                Write-Output "Multi-tenancy is enabled. Creating tenant: $configServiceTenant"
                try {
                    $tenantId = Add-Tenant -CmsUrl $cmsUrl -AccessToken $configToken -TenantName $configServiceTenant
                    Write-Output "Tenant created successfully with ID: $tenantId"
                }
                catch {
                    Write-Warning "Failed to create tenant (may already exist): $($_.Exception.Message)"
                }
            }

            # Get tenant from the effective environment (for multi-tenant support). Database
            # values below resolve with the same Compose precedence, and the defaults match the
            # compose-file ${VAR:-default} fallbacks, so the registered data store targets exactly
            # the database/credentials the containers received.
            $tenant = $configServiceTenant
            # The same value the separate-mode collision guard above judged, not a second resolution of
            # it: re-deriving it here is what let the guard and the registration disagree.
            $postgresDbName = $effectivePostgresDataStoreName
            $postgresUser = Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "POSTGRES_USER" -DefaultValue "postgres"
            $postgresCredential = ConvertTo-PostgresCredential -UserName $postgresUser -Secret (Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "POSTGRES_PASSWORD")

            # Resolve the data-store connection string stored in CMS for the DMS datastore. For
            # MSSQL this is the SQL Server form pointing at the dms-mssql container; for PostgreSQL
            # it is left empty so Add-DataStore builds its PostgreSQL connection string from the
            # Postgres* values.
            $dataStoreConnectionString = ""
            if ($DatabaseEngine -eq "mssql") {
                $mssqlPassword = Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "MSSQL_SA_PASSWORD" -DefaultValue "abcdefgh1!"
                $mssqlDbName =
                    if (-not [string]::IsNullOrWhiteSpace($DataStoreDatabaseName)) {
                        $DataStoreDatabaseName
                    }
                    else {
                        Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "MSSQL_DB_NAME" -DefaultValue "edfi_datamanagementservice"
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
    if (-not $databaseOnlyStartup) {
        Restore-BootstrapEnvSnapshot -Snapshot $bootstrapEnvSnapshot
    }
    Pop-Location
}
