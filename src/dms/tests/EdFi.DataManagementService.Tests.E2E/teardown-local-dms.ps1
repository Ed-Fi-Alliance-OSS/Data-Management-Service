# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Tears down the standard DMS E2E local Docker environment created by setup-local-dms.ps1.
.DESCRIPTION
    Thin, engine-aware wrapper over the shared, project-scoped teardown primitives
    (start-local-dms.ps1 and start-published-dms.ps1, each -d -v -DatabaseEngine
    <postgresql|mssql>). Both compose projects are torn down - dms-local for a locally-built run
    and dms-published for `build-dms.ps1 E2ETest -UsePublishedImage` - so this wrapper cleans up
    either image mode without being told which one ran; a down for an absent project is a
    successful no-op. The compose projects are the sole authority for which containers, networks,
    and volumes are removed; this script performs no machine-wide cleanup (no dangling-volume
    prune, no container-name regex removal, no unprefixed volume removal, and no deletion of the
    shared external `dms` network). Only the two known locally-built images are additionally
    removed, by exact name.

    Because the primitives are invoked destructively (`-d -v`), a run started with
    `setup-local-dms.ps1 -EnableKafkaCdc` also has its CDC binding retired before its volumes are
    removed: the connector, its committed offsets, the governed topics and ACLs, and the provider
    capture artifacts go first, and the binding record last. Retirement runs against the still-running
    stack, so tear down before stopping the containers by any other means - a binding that cannot be
    retired fails this teardown and leaves the volumes in place, because removing them would destroy
    the artifacts its surviving record still names.
.PARAMETER DatabaseEngine
    Database engine the environment was started with. "postgresql" (default) or "mssql".
.PARAMETER EnvironmentFile
    Environment file the setup used, resolved against eng/docker-compose. Defaults to the
    standard E2E env file (.env.e2e).
#>

[CmdletBinding()]
param(
    [ValidateSet("postgresql", "mssql")]
    [string] $DatabaseEngine = "postgresql",

    [string] $EnvironmentFile = ".env.e2e"
)

$ErrorActionPreference = "Stop"

$dockerComposeDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../../eng/docker-compose"))
Import-Module (Join-Path $dockerComposeDir "e2e-teardown.psm1") -Force

$null = Invoke-E2EEngineAwareTeardown `
    -DatabaseEngine $DatabaseEngine `
    -EnvironmentFile $EnvironmentFile `
    -ComposeRoot $dockerComposeDir
