# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Thin convenience wrapper that sequences the bootstrap phase commands for the
    published-image developer workflow.

.DESCRIPTION
    Mirrors `bootstrap-local-dms.ps1` but targets `start-published-dms.ps1`. The wrapper is
    convenience packaging only; it is not the normative bootstrap contract. Developers may
    invoke the phase commands directly (`start-published-dms.ps1`, `load-dms-seed-data.ps1`,
    ...). The wrapper forwards developer-facing infrastructure and seed-source flags to the
    appropriate phase command without becoming the owner of those concerns.

    Seed loading is wrapper-level opt-in: when `-LoadSeedData` is absent the wrapper does
    not invoke `load-dms-seed-data.ps1`. Direct invocation of `load-dms-seed-data.ps1`
    always loads seed data and does not accept `-LoadSeedData`.

    The shared wrapper body lives in `bootstrap-wrapper.psm1`; this entry script only
    selects the target start script (`start-published-dms.ps1`).

.PARAMETER LoadSeedData
    When supplied, invokes `load-dms-seed-data.ps1` after `start-published-dms.ps1` completes.

.PARAMETER SeedTemplate
    Built-in seed template selector (`Minimal` or `Populated`). Forwarded to the seed phase.

.PARAMETER SeedDataPath
    Custom XML interchange directory. Forwarded to the seed phase. Mutually exclusive with
    `-SeedTemplate` (enforced by the seed phase).

.PARAMETER AdditionalNamespacePrefix
    Additional namespace prefixes for SeedLoader vendor authorization. Forwarded to the
    seed phase.

.PARAMETER EnvironmentFile
    Env file forwarded to both phase commands so they share local-settings resolution.

.PARAMETER IdentityProvider
    Forwarded to both phase commands for OAuth endpoint selection.

.PARAMETER EnableKafkaUI
    Forwarded to `start-published-dms.ps1`.

.PARAMETER EnableSwaggerUI
    Forwarded to `start-published-dms.ps1`.

.PARAMETER EnableConfig
    Forwarded to `start-published-dms.ps1`. Forced on when `-LoadSeedData` is supplied
    because the seed phase requires the Configuration Service to mint SeedLoader
    credentials.

.PARAMETER AddExtensionSecurityMetadata
    Forwarded to `start-published-dms.ps1`. Required by E2E pipelines that depend on
    extension claimset fragments (e.g. Sample, Homograph) being loaded from the
    AdditionalClaimsets directory.

.PARAMETER SchoolYearRange
    Multi-instance school-year range (e.g. "2024-2025"). Forwarded to
    `start-published-dms.ps1`; when seed loading is requested, every year in the range is
    also passed to the seed phase via `-SchoolYear`.

.EXAMPLE
    pwsh ./bootstrap-published-dms.ps1
    Default happy path: starts the published stack, no seed loading.

.EXAMPLE
    pwsh ./prepare-dms-schema.ps1 -ApiSchemaPath ../../src/dms/EdFi.DataStandard52.ApiSchema
    pwsh ./bootstrap-published-dms.ps1 -LoadSeedData -SeedDataPath ./my-seed-xml/
    Prepare an ApiSchemaPath-mode bootstrap manifest, then start the stack and load
    developer-supplied XML interchange files. Package-backed -SeedTemplate Minimal/Populated
    requires Story 06 schema selection and is not yet runnable from a fresh workspace.
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

    [string]$SchoolYearRange = ""
)

$ErrorActionPreference = "Stop"

Import-Module "$PSScriptRoot/bootstrap-wrapper.psm1" -Force

$wrapperArgs = @{} + $PSBoundParameters
$wrapperArgs["StartScriptName"] = "start-published-dms.ps1"

Invoke-BootstrapWrapper @wrapperArgs
