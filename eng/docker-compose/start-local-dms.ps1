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
    (-InfraOnly, -DmsOnly, -DbOnly). When invoked directly without a .bootstrap/ manifest
    the script proceeds but Invoke-BootstrapStartupConfiguration emits a warning: bootstrap
    schema provisioning will NOT happen here.

    -DbOnly: database container + readiness only; exists for diagnostics and for
    other tooling to sequence a database-only startup around.

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
    [Alias("Rebuild")]
    [Switch]
    $r,

    # Enable Kafka and Kafka Connect infrastructure
    [Switch]
    $EnableKafka,

    # Enable Kafka UI. This also enables Kafka infrastructure.
    [Switch]
    $EnableKafkaUI,

    # Enable the Kafka and Kafka Connect infrastructure the deployment-owned CDC workflow runs on,
    # for either database engine. This switch is an infrastructure opt-in only: it configures no
    # DocumentCache projection target and enables tracking on no data store. Starting DMS is never
    # authority to enable tracking - the CDC workflow's own explicit steps do both, and the
    # bootstrap wrapper owns them.
    [Switch]
    $EnableKafkaCdc,

    # Root path of the durable CDC binding state store, which holds the deployment-owned immutable
    # binding records. Defaults to eng/docker-compose/.cdc-state (Git-ignored). The store is
    # deliberately separate from the .bootstrap workspace: a binding record outlives any one
    # bootstrap run and is never part of the bootstrap manifest. A relative path resolves against
    # the caller's working directory, the same way -EnvironmentFile does.
    [string]
    $CdcBindingStatePath = "",

    # Remove the compose volumes even when a CDC binding did not retire, abandoning its record and
    # the governed artifacts it names. A destructive teardown otherwise fails on an unretired
    # binding, because deleting the volumes around a surviving binding record destroys the very
    # connector, offsets, topics, and capture artifacts an idempotent retirement retry needs. Requires
    # -d -v. This is an operator decision and is never inferred from a retirement that could not run.
    [Switch]
    $AbandonCdcBindingState,

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

    # Start only the database container and wait for readiness, then stop. Exists for
    # diagnostics and for other tooling to sequence a database-only startup around.
    # Mutually exclusive with -InfraOnly, -DmsOnly, and -r/-Rebuild. Database-only mode
    # never builds application images.
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
    # no PostgreSQL container runs. Kafka and Kafka Connect are engine-neutral and opt-in on
    # either engine; the engine selects only the database. The .env.mssql overlay (DMS_DATASTORE=mssql,
    # DMS_CONFIG_DATASTORE=mssql, the MSSQL_* keys, and the SQL Server connection strings)
    # is composed automatically onto -EnvironmentFile. See mssql.yml and
    # Resolve-DatabaseEngineEnvironmentFile.
    [ValidateSet("postgresql", "mssql")]
    [string]
    $DatabaseEngine = "postgresql",

    # Redirects the CMS (Configuration Service) database to a dedicated edfi_configurationservice
    # database instead of sharing the DMS datastore database. Applies only when CMS actually
    # participates (the default/-InfraOnly shape); has no effect with -DmsOnly/-DbOnly/-d, where
    # CMS does not start. Supported on both database engines.
    [Switch]
    $SeparateConfigDatabase,

    # Set by bootstrap-wrapper.psm1 on its own infrastructure invocation, where THIS script is a step
    # inside a wrapper-owned workflow rather than the operator's entry point. It suppresses only the
    # "run a fresh bootstrap-local-dms.ps1" hint in the -InfraOnly guidance below.
    #
    # That hint has to reconstruct the input a FRESH wrapper run would compose from, and inside a
    # wrapper-owned run this script cannot: the wrapper hands it an already-derived -EnvironmentFile
    # and deliberately does not forward its own -DataStandardVersion (forwarding it would recompose the
    # shared data-standard overlay over the wrapper's bootstrap-scoped one). Emitting the hint anyway
    # would advertise a derived path as a base file, and omit a data standard the operator selected.
    # The wrapper owns that hint instead and prints it from its own caller-supplied state. Direct
    # invocation never passes this and its guidance is unchanged.
    [Switch]
    $SuppressWrapperContinuationGuidance
)

# Early fail-fast parameter validation — runs before any module import or Docker activity.
if ($PSBoundParameters.ContainsKey('DmsBaseUrl') -and -not [string]::IsNullOrWhiteSpace($DmsBaseUrl)) {
    if ($DmsOnly) {
        throw "-DmsBaseUrl is not valid with -DmsOnly. Use -InfraOnly -DmsBaseUrl <url> for the IDE health-wait continuation shape."
    }
    if ($DbOnly) {
        throw "-DmsBaseUrl is not valid with -DbOnly."
    }
    if (-not $InfraOnly) {
        throw "-DmsBaseUrl requires -InfraOnly. Use: start-local-dms.ps1 -InfraOnly -DmsBaseUrl <url>"
    }
}

if ($DbOnly -and $r) {
    throw "Parameter -r/-Rebuild is not valid with -DbOnly. Database-only mode starts and waits for the database without building application images."
}

# A binding state root named on a run that neither opts into CDC nor tears one down would be
# silently ignored, which is the failure mode most likely to leave an operator believing a
# different store was in use than the one that was written.
if (-not [string]::IsNullOrWhiteSpace($CdcBindingStatePath) -and -not ($EnableKafkaCdc -or $d)) {
    throw "-CdcBindingStatePath requires -EnableKafkaCdc (or a teardown run). Use: start-local-dms.ps1 -EnableKafkaCdc -CdcBindingStatePath <path>"
}

# Abandoning the binding state is only meaningful for the one workflow that removes it, so a run that
# cannot reach the retirement is refused rather than silently carrying an unused permission.
if ($AbandonCdcBindingState -and -not ($d -and $v)) {
    throw "-AbandonCdcBindingState requires -d -v. It permits the destructive volume removal to proceed when a CDC binding did not retire, which is the only workflow that removes a binding record."
}

function Resolve-CdcBindingStateRoot {
    <#
    .SYNOPSIS
    Resolves the durable CDC binding state store root for this run.

    .DESCRIPTION
    An omitted -CdcBindingStatePath resolves to eng/docker-compose/.cdc-state, which is Git-ignored;
    a relative path resolves against the caller's working directory rather than this script's
    directory, matching how -EnvironmentFile is resolved. The result is always absolute, so every
    later phase and any teardown name the same store.
    #>
    param(
        [string]
        $Path,

        [Parameter(Mandatory)]
        [string]
        $DockerComposeRoot,

        [Parameter(Mandatory)]
        [string]
        $WorkingDirectory
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return [System.IO.Path]::GetFullPath((Join-Path $DockerComposeRoot ".cdc-state"))
    }

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $WorkingDirectory $Path))
}

function Assert-CdcConnectImagePinnedByDigest {
    <#
    .SYNOPSIS
    Fails closed unless the Kafka Connect image the CDC workflow runs is named by immutable digest.

    .DESCRIPTION
    The image is operator-supplied through DMS_CDC_CONNECT_IMAGE and must be the qualified Ed-Fi
    Kafka Connect build, named by digest, exactly as the connector-template integration fixture
    requires of CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE. A tag is rejected rather than used: a moving
    tag makes a registered connector's runtime unreproducible and leaves the live read-back
    validation comparing against an unknown image. There is deliberately no fallback - kafka.yml's
    :pre default belongs to the non-CDC Kafka/Kafka-UI path, which this opt-in does not relax.

    Digest-qualification is all this gate enforces, and the messages say so rather than claiming an
    identity check. The repository name cannot separate the qualified build from the unqualified one
    - both are published as ed-fi-kafka-connect and only the digest distinguishes them - so a digest
    naming an image without the Ed-Fi partitioner passes here and fails later inside connector
    validation, which is where the plugin is actually observed.
    #>
    param(
        [AllowEmptyString()]
        [string]
        $Image
    )

    if ([string]::IsNullOrWhiteSpace($Image)) {
        throw "-EnableKafkaCdc requires DMS_CDC_CONNECT_IMAGE to name a Kafka Connect image by immutable digest (for example edfialliance/ed-fi-kafka-connect@sha256:<digest>). It is unset, and the CDC workflow never falls back to a tag. Supply the qualified Ed-Fi build: this check enforces the digest form only, so a digest for any other image fails later, inside connector validation."
    }

    if (-not $Image.Contains("@sha256:")) {
        throw "DMS_CDC_CONNECT_IMAGE must name the Kafka Connect image by immutable digest. '$Image' names a tag, which the CDC workflow rejects. This check enforces the digest form only; supplying a digest for an image other than the qualified Ed-Fi build fails later, inside connector validation."
    }

    return $Image
}

$databaseOnlyStartup = $DbOnly -and -not $d
if (-not $databaseOnlyStartup) {
    # Database-only startup must not depend on bootstrap module loading or workspace state.
    # Teardown keeps the normal full-stack behavior, including bootstrap cleanup support.
    Import-Module (Join-Path $PSScriptRoot "bootstrap-manifest.psm1") -Force
    Import-Module (Join-Path $PSScriptRoot "bootstrap-claims-gate.psm1") -Force
}
$originalLocation = Get-Location
Import-Module (Join-Path $PSScriptRoot "env-utility.psm1") -Force
# Shared Compose-equivalent resolver so the readiness probes use the same port/password the container
# received (ambient process/shell value wins over the env file), not a stale file value.
Import-Module (Join-Path $PSScriptRoot "database-safety.psm1") -Force
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
$cdcBindingStateRoot = Resolve-CdcBindingStateRoot `
    -Path $CdcBindingStatePath `
    -DockerComposeRoot $PSScriptRoot `
    -WorkingDirectory $originalLocation.Path
# The base env file, before any overlay composition below reassigns $EnvironmentFile to a derived path.
# A continuation that recomposes the environment from its own switches - the fresh wrapper run the
# -InfraOnly guidance prints - must start from THIS file, not from a derived one it would then compose
# a second set of overlays over.
$baseEnvironmentFile = $EnvironmentFile
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
# CMS participates only in the default/-InfraOnly forward-starting shape - not -DmsOnly (CMS
# doesn't start), -DbOnly, or teardown (-d). Non-participating shapes get structural validation
# only; every participating MSSQL shape is verified physically on the running server after
# readiness (Assert-MssqlTopologyPhysicalConsistency), in shared and separate mode alike.
$cmsParticipates = -not ($databaseOnlyStartup -or $d -or $DmsOnly)

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
    # Shared local-settings contract: explicit -IdentityProvider wins, then the env file's
    # DMS_CONFIG_IDENTITY_PROVIDER, then self-contained (Resolve-IdentityProvider treats an
    # empty override as "not supplied").
    $IdentityProvider = Resolve-IdentityProvider -EnvValues $envValues -OverrideProvider $IdentityProvider
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

if (-not $databaseOnlyStartup) {
    $files += @(
        "-f",
        "local-dms.yml"
    )

    $documentCacheComposeFile = Get-EnvValue `
        -EnvValues $envValues `
        -Name "DMS_DOCUMENTCACHE_COMPOSE_FILE" `
        -DefaultValue ""
    if (-not [string]::IsNullOrWhiteSpace($documentCacheComposeFile)) {
        $documentCacheComposeFilePath =
            if ([System.IO.Path]::IsPathRooted($documentCacheComposeFile)) {
                $documentCacheComposeFile
            }
            else {
                Join-Path $PSScriptRoot $documentCacheComposeFile
            }

        if (-not (Test-Path -LiteralPath $documentCacheComposeFilePath -PathType Leaf)) {
            throw "DMS_DOCUMENTCACHE_COMPOSE_FILE does not identify a compose file: $documentCacheComposeFilePath"
        }

        Write-Output "Using DocumentCache Docker Compose file '$documentCacheComposeFilePath'."
        $files += @("-f", $documentCacheComposeFilePath)
    }

    $enableDotnetDiagnostics = [string]::Equals(
        (Get-EnvValue -EnvValues $envValues -Name "DMS_ENABLE_DOTNET_DIAGNOSTICS" -DefaultValue "false"),
        "true",
        [System.StringComparison]::OrdinalIgnoreCase
    )
    if ($enableDotnetDiagnostics) {
        Write-Output "Using .NET diagnostics Docker Compose override."
        $files += @("-f", "local-dms-diagnostics.yml")
    }

    # Kafka and Kafka Connect are opt-in via -EnableKafka, -EnableKafkaUI, or -EnableKafkaCdc, and
    # are engine-neutral: the deployment-owned CDC workflow captures from SQL Server as well as
    # PostgreSQL, so neither the compose set nor the start sequence branches on -DatabaseEngine.
    $enableKafkaInfrastructure = $EnableKafka -or $EnableKafkaUI -or $EnableKafkaCdc
    if ($enableKafkaInfrastructure) {
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

    # -EnableKafkaUI adds the UI on top of that infrastructure and nothing else; it is not a CDC
    # opt-in and never implies one.
    if ($EnableKafkaUI) {
        $files += @("-f", "kafka-ui.yml")
    }

    # Config Service is always included in the managed compose set outside the dedicated
    # database-only diagnostic phase. Every non-teardown bootstrap run starts Config Service,
    # including keycloak-backed runs. -EnableConfig is retained for backward compatibility.
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
    if ($v) {
        # Destructive volume removal is the only local workflow allowed to remove a CDC binding
        # record, and only in the same pass that removes every artifact the record governs. The
        # retirement therefore runs BEFORE the compose down, while the connector, broker, and
        # instance database it must reach are still running. A normal stop (-d without -v) retains
        # the binding, connector, offsets, topics, ACLs, and provider capture artifacts.
        Import-Module (Join-Path $PSScriptRoot "cdc-teardown.psm1") -Force
        if ([string]::IsNullOrWhiteSpace($CdcBindingStatePath)) {
            # The path is optional on a teardown run, so an omitted one is drift rather than an
            # error: a stack started with a custom root would be retired from the empty default,
            # which reports nothing to retire and then removes every volume anyway. The root this
            # run resolved is named here so that mismatch is visible before the down, since the
            # script cannot know what the start run passed.
            Write-Output "CDC teardown will retire from the default binding state store at '$cdcBindingStateRoot' (no -CdcBindingStatePath was supplied). A stack started with -CdcBindingStatePath must be torn down with the same path."
        }
        # Throws when a discovered binding did not retire, which is what keeps the compose down below
        # from removing the volumes holding the artifacts that binding's surviving record still names.
        # -AbandonCdcBindingState is the operator's explicit decision to accept that instead.
        Invoke-CdcDestructiveTeardown `
            -BindingStateRoot $cdcBindingStateRoot `
            -ComposeProjectName "dms-local" `
            -EnvironmentFile $EnvironmentFile `
            -DatabaseEngine $DatabaseEngine `
            -AbandonBindingState:$AbandonCdcBindingState
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

    if ($EnableKafkaCdc) {
        # Read the way Compose reads it, so an ambient shell value - which wins over the env file
        # during interpolation - is the value that is validated, not the file's own text.
        Assert-CdcConnectImagePinnedByDigest -Image (
            Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "DMS_CDC_CONNECT_IMAGE"
        ) | Out-Null

        if ($IdentityProvider -eq "keycloak") {
            # Only the self-contained identity setup registers the DocumentCache operator client, so
            # under Keycloak the infrastructure still starts but nothing can authenticate to the
            # status endpoint. Said here rather than discovered later as a 403.
            Write-Warning "Kafka CDC infrastructure is starting under the keycloak identity provider, which does not register the DocumentCache CDC operator client. The CDC enable workflow is supported on the self-contained identity provider."
        }

        # The shared Connect offset store's topic name, resolved the way Compose resolves it so this
        # script, the worker (kafka.yml OFFSET_STORAGE_TOPIC), and the control plane's own
        # ConnectOffsetStorageTopic setting all name one topic.
        $cdcConnectOffsetStorageTopic = Get-ComposeResolvedEnvValue `
            -EnvironmentValues $envValues `
            -Name "DMS_CDC_CONNECT_OFFSET_STORAGE_TOPIC" `
            -DefaultValue (Get-LocalCdcDeploymentPolicy).OffsetStoreTopicDefault

        # The store root is created here, before any CDC work, so every later phase and any teardown
        # name one existing absolute path. Creating it is not an enablement decision: this run
        # configures no DocumentCache projection target and enables tracking on no data store, and
        # an empty store is exactly what a deployment that has bound nothing yet holds.
        if (-not (Test-Path -LiteralPath $cdcBindingStateRoot -PathType Container)) {
            New-Item -ItemType Directory -Path $cdcBindingStateRoot -Force | Out-Null
        }
        Write-Output "CDC binding state store root: $cdcBindingStateRoot"
        Write-Output "Kafka CDC infrastructure is opt-in infrastructure only: no projection target is configured and no data store has CDC enabled by this step."
    }

    $upArgs = @("--detach")
    if (-not $databaseOnlyStartup) {
        # The DbOnly compose set intentionally contains only the database definition. Passing
        # --remove-orphans there would remove already-running DMS/CMS containers from this project.
        $upArgs += "--remove-orphans"
    }
    if ($r) {
        Write-Output "Building images with no cache (this may take a few minutes)..."
        docker compose $files --env-file $EnvironmentFile -p dms-local build --no-cache
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to build images. Exit code $LASTEXITCODE"
        }
    }

    function Get-ContinuationCommandArgument {
        <#
        .SYNOPSIS
        Formats the argument text a printed continuation command needs to reconstruct THIS run's
        execution state: the database engine, the environment file the continuation should read, and
        the topology declaration when this run declared it.

        .DESCRIPTION
        One helper for every command the -InfraOnly guidance emits, so the separate-mode and
        shared-mode branches cannot drift apart in which state they carry. Carrying only
        -SeparateConfigDatabase was the defect: following the hint after an MSSQL run, or after a run
        against a custom environment file, silently continued on the PostgreSQL default and the default
        environment.

        -EnvironmentFile is emitted as a PowerShell single-quoted literal so a path containing spaces
        stays one argument and an embedded apostrophe is doubled rather than terminating the quote.
        Single quotes also suppress interpolation, so a '$' in a path is not expanded by the shell the
        operator pastes into.

        -DataStandardVersion is emitted only for a command that recomposes a data-standard overlay of
        its own AND only when this run selected a version explicitly. With no selection this script
        composed no overlay, so there is no version to carry and naming one would compose an overlay the
        run never had.

        -IdentityProvider is emitted only when a caller supplies one, because only the fresh-wrapper
        command accepts it: an explicit provider outranks the environment file's own
        DMS_CONFIG_IDENTITY_PROVIDER, so a keycloak run over a self-contained environment would otherwise
        advertise a continuation that silently switches providers. The two phase commands do not declare
        the parameter and are never given it.
        #>
        param(
            [Parameter(Mandatory)]
            [string]
            $DatabaseEngine,

            [Parameter(Mandatory)]
            [string]
            $EnvironmentFile,

            [switch]
            $SeparateConfigDatabase,

            [string]
            $DataStandardVersion = "",

            [string]
            $IdentityProvider = ""
        )

        $quotedEnvironmentFile = "'" + $EnvironmentFile.Replace("'", "''") + "'"
        $argumentText = "-DatabaseEngine $DatabaseEngine -EnvironmentFile $quotedEnvironmentFile"
        if (-not [string]::IsNullOrWhiteSpace($DataStandardVersion)) {
            $argumentText += " -DataStandardVersion $DataStandardVersion"
        }
        if (-not [string]::IsNullOrWhiteSpace($IdentityProvider)) {
            $argumentText += " -IdentityProvider $IdentityProvider"
        }
        if ($SeparateConfigDatabase) {
            $argumentText += " -SeparateConfigDatabase"
        }

        return $argumentText
    }

    function Get-InfraOnlyTerminalGuidance {
        <#
        .SYNOPSIS
        Builds the -InfraOnly terminal guidance block: the manual phase next-steps and, when this script
        is the operator's entry point, the fresh-wrapper continuation command. Returns the lines rather
        than writing them, so every command it would print can be collected and bound in a test without
        starting infrastructure.

        .DESCRIPTION
        Same shape as provision-dms-schema.ps1's Get-ProvisionIdeGuidance, and for the same reason: the
        commands are the contract, and a block that can only be observed by running the whole start-up
        cannot be asserted on. Pure and side-effect-free.

        The two phase commands and the fresh-wrapper command deliberately receive DIFFERENT environment
        files:

          - The phase commands continue from the environment this run already composed, so they get
            -EffectiveEnvironmentFile, the derived file carrying the engine overlay and the topology
            marker. Both recompose idempotently from it - the engine overlay detects it is already
            applied, and the topology derivation recomputes the same artifact from the same switch.
          - The fresh wrapper run recomposes its own overlays from its input, so it gets
            -BaseEnvironmentFile, captured before this script's overlays ran. Handing it a derived file
            would layer a second generation of overlays on top, and an explicitly selected data standard
            has to travel with it because the wrapper always recomposes a data-standard overlay for a
            local bootstrap.

        Both datastore phases enforce the separate topology and neither can infer it from the environment
        file the operator hands it - the marker lives in the derived file this start wrote - so a
        separate-topology start declares it on each. Configure judges the name it registers; provision
        judges the database each selected target resolves to, which is the only place a REUSED data
        store's stored connection string is known. Without the switch the continuation could register,
        or deploy the DMS schema into, the dedicated Configuration Service database.

        Terminal guidance contract (DMS-1153 AC): print actionable phase next-steps but do NOT present a
        second start-local-dms.ps1 run as a resume mechanism.
        #>
        param(
            [Parameter(Mandatory)]
            [string]
            $DatabaseEngine,

            [Parameter(Mandatory)]
            [string]
            $EffectiveEnvironmentFile,

            [Parameter(Mandatory)]
            [string]
            $BaseEnvironmentFile,

            [string]
            $DataStandardVersion = "",

            # The provider this run RESOLVED (explicit parameter, then the environment file, then
            # self-contained). Travels on the fresh-wrapper command only; configure and provision do not
            # declare it.
            [string]
            $IdentityProvider = "",

            [switch]
            $SeparateConfigDatabase,

            [switch]
            $SuppressWrapperContinuationGuidance
        )

        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add("Infrastructure phase complete. DMS service was not started.")
        $lines.Add("")
        $lines.Add("Next steps for the manual IDE / debugger phase flow:")

        $phaseContinuationArgument = Get-ContinuationCommandArgument `
            -DatabaseEngine $DatabaseEngine `
            -EnvironmentFile $EffectiveEnvironmentFile `
            -SeparateConfigDatabase:$SeparateConfigDatabase
        $lines.Add("  1. configure-local-data-store.ps1 $phaseContinuationArgument    (instance creation / selection)")
        $lines.Add("  2. provision-dms-schema.ps1 $phaseContinuationArgument          (schema provisioning; prints IDE configuration guidance)")
        $lines.Add("  3. Launch DMS in your IDE / debugger")
        $lines.Add("  4. load-dms-seed-data.ps1 -DmsBaseUrl <url>   (optional seed delivery to the IDE-hosted DMS)")

        # Emitted only when THIS script is the operator's entry point. Inside a wrapper-owned run the
        # wrapper prints this hint itself, from the caller-supplied base environment file and data
        # standard it still holds - state this script is not given (see
        # -SuppressWrapperContinuationGuidance). Printing both would put two fresh-wrapper commands in
        # one transcript, disagreeing about the environment to compose from.
        if (-not $SuppressWrapperContinuationGuidance) {
            $lines.Add("For a wrapper-managed health-wait and optional seed, run a fresh:")
            $wrapperContinuationArgument = Get-ContinuationCommandArgument `
                -DatabaseEngine $DatabaseEngine `
                -EnvironmentFile $BaseEnvironmentFile `
                -DataStandardVersion $DataStandardVersion `
                -IdentityProvider $IdentityProvider `
                -SeparateConfigDatabase:$SeparateConfigDatabase
            $lines.Add("  bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl <url> $wrapperContinuationArgument [-LoadSeedData ...]")
        }

        return $lines.ToArray()
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

    function Initialize-CdcConnectOffsetStore {
        <#
        .SYNOPSIS
        Provisions the shared Kafka Connect offset store before the Connect worker can create it.

        .DESCRIPTION
        cdc-streaming.md requires bootstrap to pre-create and validate the configured shared offset
        topic before it starts local Kafka Connect, and never to rely on Connect topic auto-creation
        or broker defaults. A worker that reaches the broker first creates the topic itself and sets
        only cleanup.policy on it, leaving min.insync.replicas to the broker default. The control
        plane validates an existing store rather than repairing it, and a broker-default value is not
        a topic-level override, so a worker-created store is permanently nonconforming and every cdc
        verb refuses against it - which is exactly what the checked-in broker-backed test proves.

        Kafka is therefore started on its own first, the store is provisioned, and only then does the
        caller start the worker.

        The add-config runs whether or not the create found the topic present. A stack stopped
        without -v keeps the broker volume, so a run that opted into Kafka before it opted into CDC
        has already left a worker-created store behind, and setting the explicit topic-level values
        on it is the deployment obligation that makes the opt-in usable on that broker. The values
        are the local profile's own, read from Get-LocalCdcDeploymentPolicy so this script and the
        cdc verbs cannot name different ones. The control plane still validates the store for itself
        on every verb; this provisions it and confirms what the broker reports back.
        #>
        param(
            [Parameter(Mandatory)]
            [string[]]
            $ComposeFiles,

            [Parameter(Mandatory)]
            [string]
            $EnvironmentFile,

            [Parameter(Mandatory)]
            [string]
            $TopicName,

            [int]
            $TimeoutSeconds = 120
        )

        $policy = Get-LocalCdcDeploymentPolicy
        $partitionCount = [string]$policy.OffsetStorePartitionCount
        $replicationFactor = [string]$policy.OffsetStoreReplicationFactor
        $minInSyncReplicas = [string]$policy.OffsetStoreMinInSyncReplicas

        Write-Output "Starting Kafka before Kafka Connect, so the shared Connect offset store is provisioned before the worker can create it..."
        docker compose $ComposeFiles --env-file $EnvironmentFile -p dms-local up --detach kafka
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to start Kafka. Exit code $LASTEXITCODE"
        }

        $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
        $brokerReady = $false
        while ([datetime]::UtcNow -lt $deadline) {
            $remainingSeconds = [math]::Max(1, [math]::Ceiling(($deadline - [datetime]::UtcNow).TotalSeconds))
            $probeArguments = @(
                "exec", "dms-kafka1",
                "/opt/kafka/bin/kafka-cluster.sh", "cluster-id", "--bootstrap-server", "dms-kafka1:9092"
            )
            if (Test-NativeCommandWithTimeout -FilePath "docker" -ArgumentList $probeArguments -TimeoutSeconds ([math]::Min(10, $remainingSeconds))) {
                $brokerReady = $true
                break
            }

            if ([datetime]::UtcNow -lt $deadline) {
                Start-Sleep -Seconds 2
            }
        }

        if (-not $brokerReady) {
            throw "Kafka (dms-kafka1) did not become reachable within $TimeoutSeconds seconds, so the shared Connect offset store could not be provisioned before the Connect worker starts."
        }

        Write-Output "Provisioning the shared Connect offset store '$(Format-LogSafeText $TopicName)'..."
        docker exec dms-kafka1 /opt/kafka/bin/kafka-topics.sh `
            --bootstrap-server dms-kafka1:9092 `
            --create --if-not-exists `
            --topic $TopicName `
            --partitions $partitionCount `
            --replication-factor $replicationFactor `
            --config cleanup.policy=compact `
            --config "min.insync.replicas=$minInSyncReplicas"
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to create the shared Connect offset store. Exit code $LASTEXITCODE"
        }

        docker exec dms-kafka1 /opt/kafka/bin/kafka-configs.sh `
            --bootstrap-server dms-kafka1:9092 `
            --alter --entity-type topics --entity-name $TopicName `
            --add-config "cleanup.policy=compact,min.insync.replicas=$minInSyncReplicas"
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to set the shared Connect offset store's explicit topic-level policy. Exit code $LASTEXITCODE"
        }

        $described = (docker exec dms-kafka1 /opt/kafka/bin/kafka-configs.sh `
            --bootstrap-server dms-kafka1:9092 `
            --describe --entity-type topics --entity-name $TopicName 2>&1) | Out-String
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to read the shared Connect offset store's policy back. Exit code $LASTEXITCODE"
        }

        if ($described -notmatch [regex]::Escape("min.insync.replicas=$minInSyncReplicas") -or
            $described -notmatch [regex]::Escape("cleanup.policy=compact")) {
            throw "The shared Connect offset store does not report the explicit topic-level policy the CDC control plane requires (cleanup.policy=compact and min.insync.replicas=$minInSyncReplicas)."
        }

        Write-Output "Shared Connect offset store is compacted with an explicit topic-level min.insync.replicas=$minInSyncReplicas."
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

    if ($DbOnly) {
        $databaseDisplayName = if ($DatabaseEngine -eq "mssql") { "SQL Server" } else { "Postgresql" }
        Write-Output "Starting $databaseDisplayName only..."
        docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs db
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

    # The two role names CMS enforces are supported overrides: local-config.yml maps
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
        Write-Output "Starting Keycloak..."
        docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs keycloak
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
    docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs db
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
            # stays physically distinct from the dedicated Configuration Service database. The
            # authority reads the effective file's own topology marker (raw) to select the mode;
            # on this participating path the topology resolver above just recomputed the topology
            # from the switch, so the marker in the file it RETURNED is the current declaration.
            # Placed hard against readiness so a violated relation, or any inability to verify
            # (it fails closed), stops the start after the database container exists but before
            # OpenIddict, CMS, DMS, or any datastore work touches it.
            Assert-MssqlTopologyPhysicalConsistency `
                -EnvironmentFile $EnvironmentFile `
                -ContainerName "dms-mssql" `
                -SaPassword $mssqlSaPassword
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
        docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs config
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

            if ($EnableKafkaCdc) {
                # The DocumentCache status endpoint authorizes on an exact role claim, and a client
                # CMS creates carries the configured client role instead, so the operator client is
                # registered here - in the identity phase that already owns local client
                # registration - rather than by the CDC phase that consumes its token.
                #
                # The resolved role set is not splatted: this is the one client whose DMS role is
                # deliberately not the configured client role, and splatting it alongside the
                # override would bind -DmsClientRole twice. ConfigServiceRole still comes from that
                # same resolved set, so the operator client is registered against the CMS role the
                # deployment configured rather than the setup script's default.
                Write-Output "Registering the DocumentCache CDC operator client..."
                ./setup-openiddict.ps1 -InsertData -ConfigServiceRole $identityRoleParams.ConfigServiceRole -NewClientId $identityClientSecrets.DocumentCacheOperatorClientId -NewClientName "DocumentCache CDC Operator" -DmsClientRole (Get-DocumentCacheStatusOperatorRole) -NewClientSecret $identityClientSecrets.DocumentCacheOperatorClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile @identityDbParams
            }
        }

        if ($enableKafkaInfrastructure) {
            # The Connect worker starts in the same `up` as the broker, so on the CDC opt-in the
            # shared offset store is provisioned first rather than left to the worker to create.
            if ($EnableKafkaCdc) {
                Initialize-CdcConnectOffsetStore `
                    -ComposeFiles $files `
                    -EnvironmentFile $EnvironmentFile `
                    -TopicName $cdcConnectOffsetStorageTopic
            }

            Write-Output "Starting Kafka infrastructure..."
            # kafka-postgresql-source is the Kafka Connect service. The name predates the
            # engine-neutral CDC workflow and is kept: renaming it would break existing local
            # workflows and any external reference to the container name.
            docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs kafka kafka-postgresql-source
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to start Kafka infrastructure. Exit code $LASTEXITCODE"
            }
        }

        if ($EnableKafkaUI) {
            Write-Output "Starting Kafka UI..."
            docker compose $files --env-file $EnvironmentFile -p dms-local up $upArgs kafka-ui
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
            foreach ($line in (Get-InfraOnlyTerminalGuidance `
                -DatabaseEngine $DatabaseEngine `
                -EffectiveEnvironmentFile $EnvironmentFile `
                -BaseEnvironmentFile $baseEnvironmentFile `
                -DataStandardVersion $DataStandardVersion `
                -IdentityProvider $IdentityProvider `
                -SeparateConfigDatabase:$SeparateConfigDatabase `
                -SuppressWrapperContinuationGuidance:$SuppressWrapperContinuationGuidance)) {
                Write-Output $line
            }
        }
        return
    }

    if($IdentityProvider -eq "self-contained")
    {
        Write-Output "Init db public and private keys for OpenIddict..."
        ./setup-openiddict.ps1 -InitDb -EnvironmentFile $EnvironmentFile @identityDbParams
    }

    # The full-stack `up` starts the Connect worker alongside everything else, so the shared offset
    # store is provisioned before it rather than left to the worker's own auto-creation.
    if ($EnableKafkaCdc) {
        Initialize-CdcConnectOffsetStore `
            -ComposeFiles $files `
            -EnvironmentFile $EnvironmentFile `
            -TopicName $cdcConnectOffsetStorageTopic
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
        ./setup-openiddict.ps1 -InsertData @identityRoleParams -NewClientSecret $identityClientSecrets.DmsConfigurationServiceClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile @identityDbParams

        # Create client with edfi_admin_api/readonly_access scope
        ./setup-openiddict.ps1 -InsertData @identityRoleParams -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access" -NewClientSecret $identityClientSecrets.CmsReadOnlyAccessClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile @identityDbParams

        # Create client with edfi_admin_api/authMetadata_readonly_access scope
        ./setup-openiddict.ps1 -InsertData @identityRoleParams -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access" -EnvironmentFile $EnvironmentFile @identityDbParams

        if ($EnableKafkaCdc) {
            # Same operator client the -InfraOnly shape registers, for a direct full start, and for
            # the same reason it does not splat the resolved role set.
            Write-Output "Registering the DocumentCache CDC operator client..."
            ./setup-openiddict.ps1 -InsertData -ConfigServiceRole $identityRoleParams.ConfigServiceRole -NewClientId $identityClientSecrets.DocumentCacheOperatorClientId -NewClientName "DocumentCache CDC Operator" -DmsClientRole (Get-DocumentCacheStatusOperatorRole) -NewClientSecret $identityClientSecrets.DocumentCacheOperatorClientSecret -ClientSecretMinimumLength $identityClientSecrets.ClientSecretMinimumLength -ClientSecretMaximumLength $identityClientSecrets.ClientSecretMaximumLength -EnvironmentFile $EnvironmentFile @identityDbParams
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
