# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Sets up the Ed-Fi DMS local Docker environment for E2E testing
.DESCRIPTION
    This script is a convenience wrapper that runs start-local-dms.ps1 with the standard
    E2E testing configuration. It is the companion to teardown-local-dms.ps1.

    Extension schema packages (Sample, Homograph) are loaded through the file-based SCHEMA_PACKAGES path.
    The -AddExtensionSecurityMetadata switch activates Hybrid claims mode so extension
    claimset fragments are loaded from the AdditionalClaimsets directory mounted at
    /app/additional-claims. This is the non-bootstrap compatibility path; bootstrap mode
    activates staged schema and claims automatically when a manifest is present.

    The script runs (with -DatabaseEngine forwarded to every engine-aware phase):
    ./start-local-dms.ps1 -InfraOnly -EnableConfig -EnvironmentFile <selected env file> -DatabaseEngine <engine> -r -AddExtensionSecurityMetadata
    ./configure-local-data-store.ps1 -EnvironmentFile <selected env file> -DatabaseEngine <engine> -DataStoreDatabaseName <E2E_DATABASE_NAME>
    ./provision-e2e-database.ps1 -EnvironmentFile <selected env file> -DatabaseEngine <engine> -DatabaseName <E2E_DATABASE_NAME>
    ./provision-e2e-database.ps1 -EnvironmentFile <selected env file> -DatabaseEngine <engine> -DatabaseName <E2E_SNAPSHOT_DATABASE_NAME>
    ./start-local-dms.ps1 -DmsOnly -EnableConfig -EnvironmentFile <selected env file> -DatabaseEngine <engine> -AddExtensionSecurityMetadata

    Each Docker phase above runs inside the shared schema-settings guard from
    eng/docker-compose/dms-schema-environment.psm1, which removes USE_API_SCHEMA_PATH,
    API_SCHEMA_PATH, and SCHEMA_PACKAGES from this process for the duration of that phase and then
    restores the caller's exact prior state, including the absent, empty, whitespace, and valued
    distinctions, and on failure paths. The selected environment file is therefore the sole authority
    for the schema package surface of every phase. Each phase is guarded on its own rather than the
    sequence as a whole, so a phase that re-creates one of the three names in this process cannot
    leave it set for a later phase. That module explains why an ambient value would otherwise win
    over the environment file.

    After DMS starts, the script compares the started container's schema SETTINGS against the
    selected environment file and fails the setup when they diverge, so that divergence is reported
    here rather than as an HTTP 503 EffectiveSchemaHash failure in every scenario of the suite. This
    is a settings-level check, not a schema-hash comparison: build-dms.ps1 E2ETest is the path that
    compares the provisioned and runtime schema hashes, so a hash divergence whose settings agree is
    caught there and not here. Both sides of this comparison are read from the environment file,
    never with Docker Compose precedence.

    -EnableKafkaCdc adds the deployment-owned CDC workflow to the same phase sequence: Kafka and
    Kafka Connect join the infrastructure phase, the DocumentCache projection target and status role
    are configured before DMS starts, and capture is registered against the E2E database this setup
    just provisioned - before the suite issues any write, which is the only state initial CDC
    enablement is admitted in. It is off by default; a standard E2E run starts no Kafka. The opt-in
    also requires DMS_CDC_CONNECT_IMAGE to name the Ed-Fi Kafka Connect image by immutable digest;
    start-local-dms.ps1 fails closed on a tag or an unset value before anything starts.

    On completion the script prints a copyable teardown command carrying the same -DatabaseEngine and
    the resolved -EnvironmentFile, so a custom or MSSQL run is torn down against its own compose
    definition/environment rather than the teardown wrapper's postgresql/.env.e2e defaults:
    ./teardown-local-dms.ps1 -DatabaseEngine <engine> -EnvironmentFile '<resolved env file>'.
    That teardown is destructive (-d -v), so it also retires any CDC binding this run created.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Setup script is intentionally host-oriented and uses console progress output.')]
[CmdletBinding()]
param(
    [string] $EnvironmentFile = "./.env.e2e",

    # Optional Ed-Fi Data Standard version (e.g. "5.2", "6.1") composed into the effective environment file.
    [string] $DataStandardVersion,

    # Database engine backing the stack. "postgresql" (default) or "mssql".
    [ValidateSet("postgresql", "mssql")]
    [string] $DatabaseEngine = "postgresql",

    # Start Kafka and Kafka Connect and register CDC capture against the E2E database this setup
    # provisions. Off by default - a standard E2E run needs neither. See .DESCRIPTION.
    [switch] $EnableKafkaCdc
)

function Get-DirectSetupTeardownCommand {
    # Builds a copyable teardown command that carries the same engine and environment file this setup
    # used, so a custom or MSSQL run is torn down against its own compose definition/environment rather
    # than the teardown wrapper's postgresql/.env.e2e defaults. The path is single-quoted (with embedded
    # single quotes doubled) so a path containing spaces or quotes is safe to paste back.
    param(
        [Parameter(Mandatory)]
        [string] $DatabaseEngine,

        [Parameter(Mandatory)]
        [string] $EnvironmentFile
    )

    $quotedEnvironmentFile = "'" + ($EnvironmentFile -replace "'", "''") + "'"
    return "./teardown-local-dms.ps1 -DatabaseEngine $DatabaseEngine -EnvironmentFile $quotedEnvironmentFile"
}

Write-Host @"
Ed-Fi DMS Local Environment Setup for E2E Testing
=================================================
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
Write-Host "Docker is running" -ForegroundColor Green
Write-Host ""

# Store current location and navigate to docker-compose directory
$originalLocation = Get-Location
$dockerComposeDir = Join-Path $PSScriptRoot "../../../../eng/docker-compose"

# Prior process values of the DMS runtime settings the CDC opt-in delivers ambiently, so the finally
# block can put this shell back the way it found it. $null distinguishes absent from present-and-empty,
# which is the distinction the restore has to reproduce.
$cdcRuntimeEnvironmentSnapshot = @{}

try {
    Set-Location $dockerComposeDir
    Import-Module ./env-utility.psm1 -Force
    # Shared Compose-equivalent resolver so this wrapper selects the same E2E target database the
    # provision phase resets (an ambient E2E_DATABASE_NAME override wins over the env file).
    Import-Module ./database-safety.psm1 -Force
    # The schema-settings guard and the post-start container schema verification, both shared with the
    # Instance Management E2E wrapper so the two flows guard and verify the same thing the same way
    # rather than carrying their own copies. The verification reads the environment file through the
    # same file-only package reader the provision phase uses, so the container is compared against
    # exactly the package surface the database was provisioned for.
    #
    # Without -Force, the same rule this module applies to its own nested imports: -Force removes a
    # module session-wide before re-importing it, while a plain import reuses an already-loaded
    # instance. build-dms.ps1 loads this same module for its own guarded call sites before invoking a
    # setup wrapper in-process, so reusing that instance keeps one module serving both.
    Import-Module ./dms-schema-environment.psm1
    if ($EnableKafkaCdc) {
        # The CDC enable phase, the data-store-id reader, and the runtime-settings key set are the
        # bootstrap wrapper's own rather than copies: this wrapper builds a different phase sequence,
        # but the enable itself is one workflow (DMS health -> operator token -> status-endpoint
        # preflight -> enable) and a second copy would be the copy that misses the next fix.
        Import-Module ./bootstrap-wrapper.psm1 -Force
    }

    $baseEnvironmentFile = Resolve-LocalSettingsEnvironmentFile -Path $EnvironmentFile -DockerComposeRoot $dockerComposeDir
    # Compose the data-standard overlay first, then the database-engine overlay (same order as
    # start-local-dms.ps1), so every phase below reads the one resolved file. For postgresql the
    # engine step is a no-op.
    $resolvedEnvironmentFile = Resolve-DataStandardEnvironmentFile `
        -DataStandardVersion $DataStandardVersion `
        -BaseEnvironmentFile $baseEnvironmentFile `
        -DockerComposeRoot $dockerComposeDir
    $resolvedEnvironmentFile = Resolve-DatabaseEngineEnvironmentFile `
        -DatabaseEngine $DatabaseEngine `
        -BaseEnvironmentFile $resolvedEnvironmentFile `
        -DockerComposeRoot $dockerComposeDir
    $envValues = ReadValuesFromEnvFile $resolvedEnvironmentFile
    $e2eDatabaseName = Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "E2E_DATABASE_NAME"

    # The Snapshot derivative's database. Resolved with the same Compose precedence as the primary so
    # an ambient override wins here exactly as it does there.
    $e2eSnapshotDatabaseName = Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name "E2E_SNAPSHOT_DATABASE_NAME"

    if ([string]::IsNullOrWhiteSpace($e2eSnapshotDatabaseName)) {
        throw "E2E_SNAPSHOT_DATABASE_NAME must be set in '$resolvedEnvironmentFile' or the process environment so the DMS E2E snapshot derivative has a provisioned database."
    }

    if ($e2eSnapshotDatabaseName -eq $e2eDatabaseName) {
        throw "E2E_SNAPSHOT_DATABASE_NAME must differ from E2E_DATABASE_NAME; a snapshot pointing at the primary database cannot be told apart from it."
    }

    if ([string]::IsNullOrWhiteSpace($e2eDatabaseName)) {
        throw "E2E_DATABASE_NAME must be set in '$resolvedEnvironmentFile' or the process environment so direct DMS E2E setup creates a CMS data store against the provisioned E2E database."
    }

    if ($EnableKafkaCdc -and (Resolve-IdentityProvider -EnvValues $envValues) -eq "keycloak") {
        # Said here rather than discovered as a 403 from the status endpoint after the whole stack is
        # up: only the self-contained identity setup registers the DocumentCache operator client, and
        # the enable workflow authenticates as that client.
        throw "-EnableKafkaCdc is supported on the self-contained identity provider only. The keycloak setup in '$resolvedEnvironmentFile' does not register a client whose token carries the DocumentCache status role."
    }

    # The CDC opt-in is the only thing that adds Kafka to an E2E run. With the switch absent this
    # splat is empty and both start phases below receive exactly the arguments they always have.
    $cdcStartArgs = @{}
    if ($EnableKafkaCdc) {
        $cdcStartArgs.EnableKafkaCdc = $true
    }

    $bootstrapDir = Join-Path $dockerComposeDir ".bootstrap"
    if (Test-Path -LiteralPath $bootstrapDir) {
        Write-Output "Removing stale .bootstrap workspace before file-based schema package E2E startup..."
        # Fail fast on cleanup errors: a stale manifest left here would trigger bootstrap mode
        # on the next start-local-dms.ps1 invocation and silently divert the E2E run.
        Remove-Item -LiteralPath $bootstrapDir -Recurse -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $bootstrapDir) {
            throw "Failed to remove stale .bootstrap workspace at '$bootstrapDir'. Resolve any file locks or permissions before re-running setup."
        }
    }

    Write-Host "Starting DMS environment with E2E configuration..." -ForegroundColor Green
    Write-Host "Configuration:" -ForegroundColor Yellow
    Write-Host "  - Search Engine UI: Enabled" -ForegroundColor Gray
    Write-Host "  - Configuration Service: Enabled" -ForegroundColor Gray
    Write-Host "  - Environment File: $resolvedEnvironmentFile" -ForegroundColor Gray
    Write-Host "  - Database Engine: $DatabaseEngine" -ForegroundColor Gray
    Write-Host "  - E2E Database: $e2eDatabaseName" -ForegroundColor Gray
    Write-Host "  - E2E Snapshot Database: $e2eSnapshotDatabaseName" -ForegroundColor Gray
    Write-Host "  - Kafka CDC: $(if ($EnableKafkaCdc) { 'Enabled' } else { 'Disabled' })" -ForegroundColor Gray
    Write-Host "  - Force Rebuild: Yes" -ForegroundColor Gray
    Write-Output "  - Extension Security Metadata: Yes"
    Write-Host ""

    Write-Output "Using file-based schema packages from $resolvedEnvironmentFile for E2E (non-bootstrap compatibility path)."

    # Each phase is guarded INDIVIDUALLY rather than the sequence as a whole. The guard removes the
    # three schema names for the phase it wraps and restores the caller's prior state when that phase
    # returns, so wrapping the whole sequence would remove them exactly once, before phase 1: a phase
    # script runs in this same process, and one that re-creates any of the three - start-local-dms.ps1
    # does exactly that for bootstrap mode - would then still be setting it for every later phase.
    # Guarding per phase re-applies the removal immediately before each Compose call.

    # Start only the infrastructure and Configuration Service first. DMS starts after the
    # E2E data store exists and the relational schema has been provisioned.
    # Under the CDC opt-in this phase also starts Kafka and Kafka Connect and registers the
    # DocumentCache operator client the enable workflow authenticates as.
    Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
        ./start-local-dms.ps1 -InfraOnly -EnableConfig -EnvironmentFile $resolvedEnvironmentFile -DatabaseEngine $DatabaseEngine -r -AddExtensionSecurityMetadata @cdcStartArgs
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to start DMS infrastructure. Exit code: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    # Create the default data store via the configuration phase. start-local-dms.ps1 no longer
    # creates a data store automatically; instance creation is owned by configure-local-data-store.ps1.
    # Config Service is already healthy at this point because the -InfraOnly phase waits for
    # CMS readiness before returning.
    # The phase's single structured result is captured rather than emitted, because the CDC target
    # below is the data store this phase selected and must never be inferred from anything else. It
    # is written back to the console so a run's visible output is unchanged.
    $dataStoreConfiguration = Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
        ./configure-local-data-store.ps1 -EnvironmentFile $resolvedEnvironmentFile -DatabaseEngine $DatabaseEngine -DataStoreDatabaseName $e2eDatabaseName
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to configure local data store. Exit code: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    $dataStoreConfiguration | Out-Host

    Write-Host "`nProvisioning E2E database '$e2eDatabaseName'..." -ForegroundColor Cyan
    Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
        ./provision-e2e-database.ps1 -EnvironmentFile $resolvedEnvironmentFile -DatabaseEngine $DatabaseEngine -DatabaseName $e2eDatabaseName
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to provision E2E database '$e2eDatabaseName'. Exit code: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    # The snapshot database gets the same generated DDL as the primary and is then left empty. It is
    # provisioned after the primary so a failure here cannot leave a half-configured primary behind.
    Write-Host "`nProvisioning E2E snapshot database '$e2eSnapshotDatabaseName'..." -ForegroundColor Cyan
    Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
        ./provision-e2e-database.ps1 -EnvironmentFile $resolvedEnvironmentFile -DatabaseEngine $DatabaseEngine -DatabaseName $e2eSnapshotDatabaseName
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to provision E2E snapshot database '$e2eSnapshotDatabaseName'. Exit code: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    if ($EnableKafkaCdc) {
        # The projection target and the status role are read at DMS startup, so they are configured
        # here - after provisioning, before the DMS start. Without the target the enable workflow's
        # first proof fails closed and the projector has nothing to drain; without the role the
        # status endpoint is never mapped and the caught-up read would see a 404.
        #
        # They are delivered through the process environment rather than by editing the selected env
        # file: Compose gives an ambient value precedence over --env-file, and .env.e2e is a tracked
        # file this wrapper must leave exactly as it found it. The finally block restores them.
        # Same structured-result contract the bootstrap wrapper enforces (command-boundaries.md
        # Section 3.4): the CDC target is read from one result object, never from a stream that also
        # carried progress text.
        $dataStoreConfigurationResults = @($dataStoreConfiguration)
        if ($dataStoreConfigurationResults.Count -ne 1) {
            throw "configure-local-data-store.ps1 must return exactly one structured result object for -EnableKafkaCdc to name the CDC target. Returned $($dataStoreConfigurationResults.Count)."
        }
        $configuredDataStore = $dataStoreConfigurationResults[0]

        $configuredDataStoreIds = [long[]]@(
            Resolve-WrapperSelectedDataStoreIds -ConfigureResult $configuredDataStore
        )
        if ($configuredDataStoreIds.Count -ne 1) {
            throw "-EnableKafkaCdc requires exactly one configured data store; the configure phase selected $($configuredDataStoreIds.Count). A CDC binding covers exactly one instance database."
        }
        if ($configuredDataStore.HasRouteQualifiedDataStores) {
            throw "-EnableKafkaCdc does not support route-qualified data stores. A CDC binding covers exactly one instance database."
        }

        $cdcTargetDataStoreId = [long]$configuredDataStoreIds[0]
        $cdcTargetTenantKey = [string]$configuredDataStore.Tenant
        # The default durable binding state store, which is also the root the destructive teardown
        # (-d -v, through teardown-local-dms.ps1) resolves and retires from.
        $cdcBindingStateRoot = [System.IO.Path]::GetFullPath((Join-Path $dockerComposeDir ".cdc-state"))

        $cdcRuntimeSettings = Get-WrapperCdcRuntimeEnvOverride `
            -TenantKey $cdcTargetTenantKey `
            -DataStoreId $cdcTargetDataStoreId `
            -BindingStateRootPath $cdcBindingStateRoot
        foreach ($settingName in $cdcRuntimeSettings.Keys) {
            $cdcRuntimeEnvironmentSnapshot[$settingName] = [System.Environment]::GetEnvironmentVariable($settingName)
            [System.Environment]::SetEnvironmentVariable($settingName, [string]$cdcRuntimeSettings[$settingName])
        }

        Write-Host "`nConfigured the DocumentCache projection target (data store $cdcTargetDataStoreId) and status endpoint role for the DMS start." -ForegroundColor Cyan
    }

    Write-Host "`nStarting DMS after E2E database provisioning..." -ForegroundColor Cyan
    Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
        ./start-local-dms.ps1 -DmsOnly -EnableConfig -EnvironmentFile $resolvedEnvironmentFile -DatabaseEngine $DatabaseEngine -AddExtensionSecurityMetadata @cdcStartArgs
    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to start DMS service after E2E database provisioning. Exit code: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    # Prove DMS actually came up on the environment file's schema package surface before any scenario
    # runs. In its own guard, after the DMS-only start: the check reads a RUNNING container, and the
    # guard keeps the file-only expectation from being contaminated by an ambient override even if a
    # future edit reaches for a Compose-precedence reader.
    Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
        Assert-DmsContainerSchemaEnvironment `
            -EnvironmentFilePath $resolvedEnvironmentFile `
            -ContainerName "ed-fi-api"
    }

    if ($EnableKafkaCdc) {
        # Registers capture against the database the provision phase just created, after DMS is up
        # and before the suite issues any write: write admission is still closed, which is the only
        # state initial CDC enablement is admitted in. dms-local is the compose project both start
        # phases above used.
        # -SourceDatabaseName is the database the provision phase created, named explicitly rather
        # than re-derived: this run provisioned E2E_DATABASE_NAME, not the datastore database name a
        # plain bootstrap run registers, and the connector connects to the source directly.
        Invoke-WrapperCdcEnablePhase `
            -ComposeProjectName "dms-local" `
            -EnvironmentFile $resolvedEnvironmentFile `
            -TenantKey $cdcTargetTenantKey `
            -DataStoreId $cdcTargetDataStoreId `
            -DatabaseEngine $DatabaseEngine `
            -DatabaseCreatedByThisRun $true `
            -SourceDatabaseName $e2eDatabaseName
    }

    # Pass the fully resolved environment file (data-standard then engine overlay) so teardown uses the
    # same effective environment the setup composed, not the pre-overlay base.
    $teardownCommand = Get-DirectSetupTeardownCommand -DatabaseEngine $DatabaseEngine -EnvironmentFile $resolvedEnvironmentFile
    Write-Host "`nDMS E2E environment setup complete!" -ForegroundColor Green
    Write-Host "To tear down this environment, run: $teardownCommand" -ForegroundColor Cyan
}
finally {
    # Restore each CDC runtime setting to its exact prior state - re-created with the verbatim prior
    # value when it existed, removed otherwise - on the success path, on a throw, and on exit. The
    # containers already have the values they were created with, so restoring changes nothing about
    # the running stack; it only keeps this shell from carrying a projection target into the next
    # command a developer runs in it.
    foreach ($settingName in @($cdcRuntimeEnvironmentSnapshot.Keys)) {
        if ($null -eq $cdcRuntimeEnvironmentSnapshot[$settingName]) {
            Remove-Item -LiteralPath "Env:$settingName" -ErrorAction SilentlyContinue
        }
        else {
            [System.Environment]::SetEnvironmentVariable($settingName, $cdcRuntimeEnvironmentSnapshot[$settingName])
        }
    }

    # Return to original location
    Set-Location $originalLocation
}
