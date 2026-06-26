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
    staged the wrapper stages core-only standard mode; an already-staged workspace (e.g. a manual
    expert `-ApiSchemaPath` flow) is used as-is. There is no `-Extensions` parameter; extension or
    custom schema sets are staged via expert `-ApiSchemaPath` before invoking the wrapper. All
    staging is delegated to `prepare-dms-schema.ps1` / `prepare-dms-claims.ps1`.

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
    Standard mode, core only. Stages the core ApiSchema package and claims in-line (when no
    workspace is staged), then starts the stack. No manual prepare step and no seed loading.

.EXAMPLE
    pwsh ./prepare-dms-schema.ps1 -SchemaToolPath $schemaToolExe
    pwsh ./prepare-dms-claims.ps1
    pwsh ./bootstrap-local-dms.ps1
    Standard mode, core only - manual prepare flow. Stage the core schema and claims
    workspaces first, then start the local stack. Use this flow when you want to inspect
    or validate the staged workspace before starting infrastructure.

.EXAMPLE
    pwsh ./prepare-dms-schema.ps1 -ApiSchemaPath ../../src/dms/EdFi.DataStandard52.ApiSchema -SchemaToolPath $schemaToolExe
    pwsh ./prepare-dms-claims.ps1
    pwsh ./bootstrap-local-dms.ps1
    Expert mode (filesystem). Stage the in-repo ApiSchema directory (which includes TPDM
    and other extensions) and claims workspaces manually, then start the local stack. The
    in-repo directory requires -ClaimsDirectoryPath with a TPDM claim fragment unless
    only core, Sample, and Homograph extensions are staged.

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

    # Database engine for the DMS datastore. "mssql" composes mssql.yml alongside postgresql.yml
    # and runs the relational backend; the Configuration Service and self-contained identity stay
    # on PostgreSQL (no MSSQL backend). Forwarded to start-local-dms.ps1 and
    # configure-local-data-store.ps1. Use with -EnvironmentFile ./.env.mssql.relational.
    [ValidateSet("postgresql", "mssql")]
    [string]$DatabaseEngine = "postgresql"
)

$ErrorActionPreference = "Stop"

Import-Module "$PSScriptRoot/bootstrap-wrapper.psm1" -Force

$wrapperArgs = @{} + $PSBoundParameters
$wrapperArgs["StartScriptName"] = "start-local-dms.ps1"

Invoke-BootstrapWrapper @wrapperArgs
