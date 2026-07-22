# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Tears down the Instance Management E2E local Docker environment created by setup-local-dms.ps1.
.DESCRIPTION
    Thin, engine-aware wrapper over the shared, project-scoped teardown primitive
    (start-local-dms.ps1 -d -v -DatabaseEngine <postgresql|mssql>). The compose project
    (dms-local) is the sole authority for which containers, networks, and volumes are removed;
    this script performs no machine-wide cleanup (no dangling-volume prune, no container-name
    regex removal, no unprefixed volume removal, and no deletion of the shared external `dms`
    network). Only the two known locally-built images are additionally removed, by exact name.
.PARAMETER DatabaseEngine
    Database engine the environment was started with. "postgresql" (default) or "mssql".
.PARAMETER EnvironmentFile
    Environment file the setup used, resolved against eng/docker-compose. Defaults to the
    Instance Management route-context env file (.env.routeContext.e2e).
#>

[CmdletBinding()]
param(
    [ValidateSet("postgresql", "mssql")]
    [string] $DatabaseEngine = "postgresql",

    [string] $EnvironmentFile = ".env.routeContext.e2e"
)

$ErrorActionPreference = "Stop"

$dockerComposeDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../../eng/docker-compose"))
Import-Module (Join-Path $dockerComposeDir "e2e-teardown.psm1") -Force

$null = Invoke-E2EEngineAwareTeardown `
    -DatabaseEngine $DatabaseEngine `
    -EnvironmentFile $EnvironmentFile `
    -ComposeRoot $dockerComposeDir
