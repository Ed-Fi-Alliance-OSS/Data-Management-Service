# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    The deployment-owned CDC enablement phase command.

.DESCRIPTION
    One phase command beside the other bootstrap phases - configure-local-data-store.ps1,
    provision-dms-schema.ps1, load-dms-seed-data.ps1 - invoked the same way and returning a
    structured result the caller reads rather than parses.

    It exists because command-boundaries.md gives bootstrap-wrapper.psm1 orchestration only. The
    wrapper sequences phase commands and forwards parameters; it does not resolve credentials,
    gate on endpoint authorization, provision database principals, or compose a tool invocation.
    All of that is this phase's, and it lives in cdc-enable.psm1 behind this entry point.

    Callers are the wrapper's -EnableKafkaCdc opt-in and the E2E harness. Both pass the same
    parameters, so neither can drift into its own idea of what the phase does.

.OUTPUTS
    A [pscustomobject] naming the target that was bound, the source database it was bound to, and
    whether this run created that database.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]
    $ComposeProjectName,

    [Parameter(Mandatory)]
    [string]
    $EnvironmentFile,

    [Parameter(Mandatory)]
    [AllowEmptyString()]
    [string]
    $TenantKey,

    [Parameter(Mandatory)]
    [long]
    $DataStoreId,

    [Parameter(Mandatory)]
    [ValidateSet("postgresql", "mssql")]
    [string]
    $DatabaseEngine,

    # Evidence, not a preference: initial CDC enablement is admitted only for a database this run
    # created and never opened to writes. The caller observes it; this phase forwards it verbatim
    # and never infers it.
    [Parameter(Mandatory)]
    [bool]
    $DatabaseCreatedByThisRun,

    # The operator's assertion that the live binding record for this target belongs to an enablement
    # that never finished. Also evidence rather than a preference, and asserted rather than observed:
    # the record survives a completed enablement, so its presence cannot establish that the database
    # was never opened to writes. cdc-enable.psm1 owns the rule.
    [switch]
    $ResumeInterruptedEnable,

    [string]
    $SourceDatabaseName = "",

    [int]
    $HealthTimeoutSeconds = 300
)

$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot "cdc-enable.psm1") -Force

Invoke-CdcEnablePhase `
    -ComposeProjectName $ComposeProjectName `
    -EnvironmentFile $EnvironmentFile `
    -TenantKey $TenantKey `
    -DataStoreId $DataStoreId `
    -DatabaseEngine $DatabaseEngine `
    -DatabaseCreatedByThisRun $DatabaseCreatedByThisRun `
    -ResumeInterruptedEnable:$ResumeInterruptedEnable `
    -SourceDatabaseName $SourceDatabaseName `
    -HealthTimeoutSeconds $HealthTimeoutSeconds
