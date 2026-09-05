# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Thin convenience wrapper that sequences the bootstrap phase commands for the common
    happy-path developer workflow, including the local IDE debugging workflow shapes.

.DESCRIPTION
    The wrapper is convenience packaging only; it is not the normative bootstrap contract.
    Developers may invoke the phase commands directly (`start-local-dms.ps1`,
    `configure-local-data-store.ps1`, `provision-dms-schema.ps1`,
    `load-dms-seed-data.ps1`, ...). The wrapper forwards developer-facing infrastructure
    and seed-source flags to the appropriate phase command without becoming the owner of
    those concerns.

    TEARDOWN: this entry point stops the local stack as well as starting it. `-d` stops the
    services and keeps volumes (delegating to `start-local-dms.ps1 -d`); `-d -v` also deletes
    data volumes and removes the `.bootstrap/` workspace (delegating to
    `start-local-dms.ps1 -d -v -RemoveBootstrap`). When the compose teardown fails,
    `start-local-dms.ps1` throws before removing the workspace (services may still be running
    against it) and the failure propagates. Teardown short-circuits to that delegation and
    returns before any staging, configure, provision, DMS-startup, or seed orchestration. Only the
    options that shape the Docker compose set (`-EnvironmentFile`, `-IdentityProvider`,
    `-EnableKafkaUI`, `-EnableKafkaCdc`, `-CdcBindingStatePath`, `-EnableSwaggerUI`,
    `-DatabaseEngine`) plus `-AbandonCdcBindingState` are forwarded to teardown; pass the same
    infrastructure flags used at start so teardown targets the same compose shape.

    Seed loading is wrapper-level opt-in: when `-LoadSeedData` is absent the wrapper does
    not invoke `load-dms-seed-data.ps1`. Direct invocation of `load-dms-seed-data.ps1`
    always loads seed data and does not accept `-LoadSeedData`.

    IDE WORKFLOW SHAPES (local only; not available on bootstrap-published-dms.ps1):

    Primary (pre-DMS stop) — `-InfraOnly` alone:
        Runs infrastructure startup, instance creation or reuse
        (`configure-local-data-store.ps1`), optional CMS-only smoke-test credentials,
        schema provisioning (`provision-dms-schema.ps1`), then prints IDE next-step
        guidance and stops. No DMS startup (`-DmsOnly`) runs. Terminal for that invocation.
        Use this to prepare the local stack for IDE-hosted (debugger) DMS launch.

    Convenience (health-wait continuation) — `-InfraOnly -DmsBaseUrl <url>`:
        Completes the same pre-DMS phase, then waits for the IDE-hosted DMS process at
        `<url>/health` to become healthy (300-second timeout). `-DmsBaseUrl` is held
        locally and NOT forwarded to the initial infrastructure invocation; it is only
        used for the post-provision health-wait. When `-LoadSeedData` is also requested,
        the wrapper forwards `-DmsBaseUrl`, `-IdentityProvider`,
        `-AdditionalNamespacePrefix` (when provided), and the in-memory selected
        data-store IDs to `load-dms-seed-data.ps1`.

    Without `-InfraOnly`, the existing Docker-hosted behavior (configure → provision →
    `-DmsOnly` → optional seed) runs unchanged.

    The shared wrapper body lives in `bootstrap-wrapper.psm1`; this entry script only
    selects the target start script (`start-local-dms.ps1`).

    Staging: no manual prepare step is required for the standard happy path. When no workspace is
    staged the wrapper stages standard mode from the effective env's SCHEMA_PACKAGES value (core
    plus any listed extensions; the catalog-pinned core-only default applies only when the env
    carries no SCHEMA_PACKAGES). An already-staged standard-mode workspace is reused only while
    its recorded package identity still matches the effective SCHEMA_PACKAGES value. A mismatch
    stops before package downloads or Docker/CMS side effects with guidance to stop the stack and
    remove `eng/docker-compose/.bootstrap`; guarded automatic replacement belongs to DMS-1271.
    Expert `-ApiSchemaPath` workspaces are reused as-is. There is no
    `-Extensions` parameter; custom or unpublished schema sets are staged via expert
    `-ApiSchemaPath` before invoking the wrapper. All staging is delegated to
    `prepare-dms-schema.ps1` / `prepare-dms-claims.ps1`.

.PARAMETER d
    Teardown switch. Stops the local DMS Docker stack instead of starting it, delegating to
    `start-local-dms.ps1 -d`. Returns before any staging/configure/provision/DMS/seed orchestration.
    Combine with `-v` to also delete data volumes and remove the `.bootstrap/` workspace.

.PARAMETER v
    Teardown volume/workspace deletion modifier. Valid only with `-d`. Delegates to
    `start-local-dms.ps1 -d -v -RemoveBootstrap`, deleting data volumes and removing the
    `eng/docker-compose/.bootstrap` workspace (preserved when the compose teardown fails).
    Rejected when supplied without `-d`.

.PARAMETER LoadSeedData
    When supplied, invokes `load-dms-seed-data.ps1` after DMS startup completes. When
    combined with `-InfraOnly -DmsBaseUrl`, the seed phase runs after the IDE-hosted DMS
    health-wait passes. Requires `-DmsBaseUrl` when `-InfraOnly` is also set.

.PARAMETER SeedTemplate
    Built-in seed template selector (`Minimal` or `Populated`). Forwarded to the seed phase.

.PARAMETER SeedDataPath
    Custom XML interchange directory. Forwarded to the seed phase. Mutually exclusive with
    `-SeedTemplate` (enforced by the seed phase).

.PARAMETER AdditionalNamespacePrefix
    Additional namespace prefixes for SeedLoader vendor authorization. Forwarded to the
    seed phase and to `load-dms-seed-data.ps1` in the IDE continuation shape.

.PARAMETER EnvironmentFile
    Env file forwarded to all phase commands so they share local-settings resolution.

.PARAMETER IdentityProvider
    Forwarded to all phase commands for OAuth endpoint selection.

.PARAMETER EnableKafkaUI
    Forwarded to `start-local-dms.ps1`.

.PARAMETER EnableKafkaCdc
    Deployment-owned CDC opt-in. Forwarded to `start-local-dms.ps1`, which starts Kafka and Kafka
    Connect, creates the binding state root, and registers the DocumentCache operator identity
    client. The wrapper then configures one `DataManagement:DocumentCache:Targets` entry and the
    status endpoint role BEFORE the DMS start, and runs the guarded CDC enable AFTER it and before
    any seed or API write: the enable reads the running projector for its caught-up evidence, and
    "write admission closed" means no write has been issued, not that DMS is down.

    Supported on the self-contained identity provider only, and rejected with `-InfraOnly`,
    `-NoDataStore`, or `-SchoolYearRange`: initial CDC enablement is admitted only for a database
    this run created, and a binding covers exactly one instance database.

.PARAMETER CdcBindingStatePath
    Root path of the durable CDC binding state store, defaulting to
    `eng/docker-compose/.cdc-state`. Requires `-EnableKafkaCdc`.

.PARAMETER ResumeInterruptedCdcEnable
    Assert that the live binding record this deployment holds belongs to an enablement that never
    finished, and that this run is completing it. Requires `-EnableKafkaCdc`, and is refused when the
    binding state store holds no live record to resume.

    An explicit operator decision rather than something a rerun infers. The binding record is written
    before the artifacts it governs exist and is removed only by retirement, so it survives a
    completed enablement and every write admitted afterwards; its presence cannot establish that the
    database was never opened to writes, which is what the initial-provisioning evidence asserts.
    Without this switch a rerun over an already-enabled target asserts nothing and is refused.

.PARAMETER AbandonCdcBindingState
    Remove the data volumes even when a CDC binding did not retire. A `-d -v` teardown otherwise
    fails on an unretired binding, because removing the volumes around a surviving binding record
    destroys the artifacts a retirement retry needs. Requires `-d -v`, and is an explicit operator
    decision rather than something a failed retirement infers.

.PARAMETER EnableSwaggerUI
    Forwarded to `start-local-dms.ps1`.

.PARAMETER EnableConfig
    Forwarded to `start-local-dms.ps1`. Forced on when `-LoadSeedData` is supplied because
    the seed phase requires the Configuration Service to mint SeedLoader credentials.

.PARAMETER AddExtensionSecurityMetadata
    Forwarded to `start-local-dms.ps1`. Required by E2E pipelines that depend on extension
    claimset fragments (e.g. Sample, Homograph) being loaded from the AdditionalClaimsets
    directory.

.PARAMETER SchoolYearRange
    Multi-instance school-year range (e.g. "2024-2025"). Consumed by the configure phase
    (`configure-local-data-store.ps1 -SchoolYearRange`) and, when seed loading is
    requested, every year in the range is passed to the seed phase via `-SchoolYear`.
    This is a wrapper/configure-phase input; it is not forwarded to `start-local-dms.ps1`.

.PARAMETER InfraOnly
    IDE workflow switch. When set, the wrapper runs infrastructure startup, configure, and
    provision, then stops before any DMS startup. Combine with `-DmsBaseUrl` for the
    health-wait continuation shape. See .DESCRIPTION for details.

.PARAMETER DmsBaseUrl
    IDE workflow URL. Base URL of an IDE-hosted DMS process to health-wait after
    infrastructure startup, configure, and provision. Valid only with `-InfraOnly`; rejected
    without it. The value is not forwarded to the initial `start-local-dms.ps1` infra
    invocation; it is used only for the post-provision health-wait and, when `-LoadSeedData`
    is also requested, forwarded to `load-dms-seed-data.ps1` so seeds hit the IDE-hosted DMS.

.EXAMPLE
    pwsh ./bootstrap-local-dms.ps1
    Standard mode. Stages the effective SCHEMA_PACKAGES set and matching claims in-line (when no
    workspace is staged), then starts the stack. The default DS 5.2 profile is core + TPDM.
    No manual prepare step and no seed loading.

.EXAMPLE
    pwsh ./bootstrap-local-dms.ps1 -d
    Stop the local DMS stack, keeping data volumes and the .bootstrap workspace.

.EXAMPLE
    pwsh ./bootstrap-local-dms.ps1 -d -v
    Stop the local DMS stack, delete data volumes, and remove the .bootstrap workspace.

.EXAMPLE
    pwsh ./prepare-dms-schema.ps1 -EnvironmentFile ./.env.bootstrap.ds52 -SchemaToolPath $schemaToolExe
    pwsh ./prepare-dms-claims.ps1
    pwsh ./bootstrap-local-dms.ps1
    Standard-mode manual prepare flow. Stage the same default DS 5.2 core + TPDM package set the
    wrapper will use, then stage claims and start the local stack. Use this flow when you want to
    inspect or validate the staged workspace before starting infrastructure.

.EXAMPLE
    pwsh ./prepare-dms-schema.ps1 -ApiSchemaPath ../../src/dms/EdFi.DataStandard52.ApiSchema -SchemaToolPath $schemaToolExe
    pwsh ./prepare-dms-claims.ps1
    pwsh ./bootstrap-local-dms.ps1
    Expert mode (filesystem). Stage a local ApiSchema directory (which may include TPDM
    and other extensions) and claims workspaces manually, then start the local stack.
    -ClaimsDirectoryPath is needed only for a custom extension outside the bootstrap map;
    core, Sample, Homograph, and TPDM are all handled without it.

.EXAMPLE
    pwsh ./prepare-dms-schema.ps1 -ApiSchemaPath ../../src/dms/EdFi.DataStandard52.ApiSchema -SchemaToolPath $schemaToolExe
    pwsh ./prepare-dms-claims.ps1
    pwsh ./bootstrap-local-dms.ps1 -LoadSeedData -SeedDataPath ./my-seed-xml/
    Expert mode with seed loading. Prepare the bootstrap manifest, then start the stack and
    load developer-supplied XML interchange files.

.EXAMPLE
    pwsh ./prepare-dms-schema.ps1 -ApiSchemaPath ../../src/dms/EdFi.DataStandard52.ApiSchema
    pwsh ./prepare-dms-claims.ps1
    pwsh ./bootstrap-local-dms.ps1 -InfraOnly
    IDE pre-DMS stop: start infrastructure, configure the data store, provision the schema,
    then stop. Launch DMS in your IDE debugger. Use the IDE guidance printed by
    provision-dms-schema.ps1 to configure appsettings.Development.json.

.EXAMPLE
    pwsh ./prepare-dms-schema.ps1 -ApiSchemaPath ../../src/dms/EdFi.DataStandard52.ApiSchema
    pwsh ./prepare-dms-claims.ps1
    pwsh ./bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl http://localhost:8080
    IDE health-wait continuation: same pre-DMS phase, then waits for the IDE-hosted DMS at
    http://localhost:8080/health to return HTTP 200 (300-second timeout).

.EXAMPLE
    pwsh ./prepare-dms-schema.ps1 -ApiSchemaPath ../../src/dms/EdFi.DataStandard52.ApiSchema
    pwsh ./prepare-dms-claims.ps1
    pwsh ./bootstrap-local-dms.ps1 -InfraOnly -DmsBaseUrl http://localhost:8080 -LoadSeedData -SeedDataPath ./my-seed-xml/
    IDE full workflow: pre-DMS phase, health-wait for IDE DMS, then load seed data against
    the IDE-hosted DMS endpoint.
#>
[CmdletBinding()]
param(
    # Teardown switches (see .PARAMETER d / .PARAMETER v). Stop the stack; -v also removes volumes.
    [Switch]$d,
    [Switch]$v,

    [Switch]$LoadSeedData,

    [ValidateSet("Minimal", "Populated")]
    [string]$SeedTemplate,

    [string]$SeedDataPath,

    [string[]]$AdditionalNamespacePrefix = @(),

    [string]$EnvironmentFile,

    # Default is left unset so the phase commands fall back to the env file's
    # DMS_CONFIG_IDENTITY_PROVIDER value via Resolve-IdentityProvider. Pass explicitly
    # only to override the env-file resolution.
    [ValidateSet("keycloak", "self-contained")]
    [string]$IdentityProvider,

    [Switch]$EnableKafkaUI,

    # Deployment-owned CDC opt-in: starts Kafka and Kafka Connect, configures one DocumentCache
    # projection target, and runs the guarded CDC enable after the DMS start and before any seed.
    # Supported on the self-contained identity provider, and only for a data store this run
    # creates. See .PARAMETER EnableKafkaCdc.
    [Switch]$EnableKafkaCdc,

    # Root path of the durable CDC binding state store. Requires -EnableKafkaCdc.
    [string]$CdcBindingStatePath = "",

    # Complete an enablement that never finished, rather than running this as a first enablement.
    # Requires -EnableKafkaCdc. See .PARAMETER ResumeInterruptedCdcEnable.
    [Switch]$ResumeInterruptedCdcEnable,

    # Abandon an unretired CDC binding rather than failing the destructive teardown. Requires -d -v.
    # See .PARAMETER AbandonCdcBindingState.
    [Switch]$AbandonCdcBindingState,

    [Switch]$EnableSwaggerUI,

    [Switch]$EnableConfig,

    [Switch]$AddExtensionSecurityMetadata,

    [Switch]$NoDataStore,

    [Switch]$AddSmokeTestCredentials,

    [string]$SchoolYearRange = "",

    # IDE workflow: stop before DMS startup so the developer can launch DMS in an IDE debugger.
    # When combined with -DmsBaseUrl, waits for the IDE-hosted DMS to become healthy after
    # configure + provision. See .DESCRIPTION for the two IDE shapes.
    [Switch]$InfraOnly,

    # IDE workflow: base URL of an IDE-hosted DMS process to health-wait. Valid only with
    # -InfraOnly; rejected without it. Not forwarded to the initial start-local-dms.ps1 infra
    # invocation. When -LoadSeedData is also set, forwarded to load-dms-seed-data.ps1.
    [string]$DmsBaseUrl,

    # Database engine for the whole stack. "mssql" swaps mssql.yml in for postgresql.yml:
    # SQL Server hosts the DMS datastore (relational backend), the Configuration Service
    # (CMS SQL Server backend), and the self-contained OpenIddict identity stores — no
    # PostgreSQL container runs. Forwarded to start-local-dms.ps1 and
    # configure-local-data-store.ps1. The .env.mssql overlay (DMS_DATASTORE=mssql,
    # DMS_CONFIG_DATASTORE=mssql, the MSSQL_* keys, and the SQL Server connection strings) is
    # composed automatically onto -EnvironmentFile, so no -EnvironmentFile is needed for a
    # turnkey MSSQL deploy.
    [ValidateSet("postgresql", "mssql")]
    [string]$DatabaseEngine = "postgresql",

    # Redirects the CMS (Configuration Service) database to a dedicated edfi_configurationservice
    # database instead of sharing the DMS datastore database. Forwarded unchanged to
    # start-local-dms.ps1 and to both datastore phases, each of which enforces one half of the rule
    # that the DMS datastore may not land in the dedicated Configuration Service database: the
    # configure phase judges a name it is about to register, and the provision phase judges the
    # database each selected target resolves to - the only place a REUSED data store's stored
    # connection string is known. Supported on both database engines.
    [Switch]$SeparateConfigDatabase,

    # Data standard version for the local-bootstrap package surface. The .env.bootstrap.<token>
    # overlay is always composed onto -EnvironmentFile: DS 5.2 (default) stages core + TPDM,
    # DS 6.1 stages core only (TPDM is folded into core in 6.1). Distinct from
    # start-local-dms.ps1 -DataStandardVersion, whose shared .env.ds<NN> overlays carry the
    # E2E/SDK surfaces (Sample/Homograph test extensions).
    [ValidateSet("5.2", "6.1")]
    [string]$DataStandardVersion = "5.2"
)

$ErrorActionPreference = "Stop"

# Teardown short-circuit (see .DESCRIPTION): delegate stop / volume + workspace removal to
# start-local-dms.ps1, which owns -d/-v/-RemoveBootstrap, and return before importing the wrapper or
# running any phase. -v maps to -v -RemoveBootstrap; -v without -d is meaningless, so reject it.
if ($v -and -not $d) {
    throw "-v requires -d. Use bootstrap-local-dms.ps1 -d -v to stop services, delete volumes, and remove the .bootstrap workspace."
}
# Abandoning CDC binding state is only meaningful for the workflow that removes it, so a start run
# that named it is refused here rather than starting a stack under a permission nothing will read.
if ($AbandonCdcBindingState -and -not ($d -and $v)) {
    throw "-AbandonCdcBindingState requires -d -v. It permits the destructive volume removal to proceed when a CDC binding did not retire, which is the only workflow that removes a binding record."
}
if ($d) {
    $teardownArgs = @{ d = $true }
    if ($v) {
        $teardownArgs.v = $true
        $teardownArgs.RemoveBootstrap = $true
    }
    # Forward only the flags that shape the compose-file set start-local-dms.ps1 rebuilds for
    # `docker compose ... down`, so teardown targets the same containers/volumes and env the stack
    # started with: -DatabaseEngine (postgresql.yml vs mssql.yml), -IdentityProvider (keycloak.yml),
    # -EnableKafkaUI (kafka.yml + kafka-ui.yml), -EnableSwaggerUI (swagger-ui.yml), and the env file.
    # Seed/configure/IDE options and -DataStandardVersion do not change the compose-file set (the DS
    # overlay only rewrites env values such as SCHEMA_PACKAGES), so they are omitted. Each is forwarded
    # only when the caller bound it; the unbound defaults (postgresql, no switches) match
    # start-local-dms.ps1's own, so an omitted flag and its default forward identically.
    #
    # -SeparateConfigDatabase is deliberately omitted too: local-config.yml is unconditional in
    # start-local-dms.ps1's compose set, so the switch changes which database CMS targets but never
    # which compose files a teardown must cover. (It does shape the set in start-published-dms.ps1,
    # but that script owns its own teardown; this wrapper offers none for the published path.)
    # -EnableKafkaCdc joins that list because it also selects kafka.yml, and -CdcBindingStatePath
    # travels with it so a teardown names the same binding state store the start run used.
    foreach ($name in 'EnvironmentFile', 'IdentityProvider', 'EnableKafkaUI', 'EnableKafkaCdc', 'CdcBindingStatePath', 'AbandonCdcBindingState', 'EnableSwaggerUI', 'DatabaseEngine') {
        if ($PSBoundParameters.ContainsKey($name)) {
            $teardownArgs[$name] = $PSBoundParameters[$name]
        }
    }

    # start-local-dms.ps1 throws when the compose teardown fails; the error propagates here.
    & "$PSScriptRoot/start-local-dms.ps1" @teardownArgs
    return
}

Import-Module "$PSScriptRoot/bootstrap-wrapper.psm1" -Force

# Copy the bound parameters for the start path, then strip -d/-v. An explicit -d:$false / -v:$false
# binds the switch into $PSBoundParameters without tripping the short-circuit above, so this point is
# reachable with either bound; the wrapper declares neither parameter, so leaving them in the splat
# would crash Invoke-BootstrapWrapper.
# -AbandonCdcBindingState is stripped for the same reason: the guard above leaves only an explicit
# -AbandonCdcBindingState:$false reachable here, and the wrapper does not declare it either.
$wrapperArgs = @{} + $PSBoundParameters
$wrapperArgs.Remove("d")
$wrapperArgs.Remove("v")
$wrapperArgs.Remove("AbandonCdcBindingState")
$wrapperArgs["StartScriptName"] = "start-local-dms.ps1"

Invoke-BootstrapWrapper @wrapperArgs
