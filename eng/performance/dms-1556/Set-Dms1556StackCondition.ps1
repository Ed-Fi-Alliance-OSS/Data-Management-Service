# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
DMS-1556 step 0.5: recomposes the running dms-local CMS/PostgreSQL services into one
experiment condition and waits until CMS answers /health.

.DESCRIPTION
Every condition uses the same base compose set the E1/E2 stacks were built from
(postgresql.yml, local-config.yml, both diagnostics overlays, .env.e2e,
DMS_CONFIG_LOG_LEVEL=Debug) plus the resource-profile overlay, and differs from
'baseline' by exactly one overlay:

  baseline         - no control overlay
  e3-certificates  - local-control-e3-certificates.yml (config)
  e4-threads       - local-control-e4-threads.yml (config)
  e5-pool16        - local-control-e5-pool16.yml (config)
  headroom         - local-postgresql-headroom.yml (db; max_connections=200)

-RecreateDb force-recreates PostgreSQL as well (data volume kept), so a headroom block
and its matched baseline both start from a freshly started server, and then restarts
CMS so it holds no connection the recreated server terminated. The config service
is always brought to the condition's definition (compose recreates it when it differs).
Invoke-ControlBatch.ps1 verifies the resulting container state before it runs anything.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateSet('baseline', 'e3-certificates', 'e4-threads', 'e5-pool16', 'headroom')]
    [string] $Condition,

    [ValidateSet('p-dev', 'p-runner-approx')]
    [string] $ResourceProfile = 'p-runner-approx',

    [switch] $RecreateDb,

    [string] $CmsContainerName = 'ed-fi-api-config-service',

    # Claims directory bind-mounted into CMS (DMS_CONFIG_CLAIMS_MOUNT_SOURCE). A stack
    # provisioned by build-dms.ps1 E2ETest mounts eng/docker-compose/.e2e-claims (extension
    # security metadata the E2E suite needs); pass it so the recompose keeps that mount.
    # Empty keeps local-config.yml's default.
    [string] $ClaimsMountSource = '',

    [string] $HealthUrl = 'http://127.0.0.1:8081/config/health'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$composeDirectory = Join-Path $PSScriptRoot '../../docker-compose' | Resolve-Path
$files = @('postgresql.yml', 'local-config.yml', 'local-config-diagnostics.yml', 'local-postgresql-diagnostics.yml')
if ($ResourceProfile -eq 'p-runner-approx') { $files += 'local-resource-runner-approx.yml' }
switch ($Condition) {
    'e3-certificates' { $files += 'local-control-e3-certificates.yml' }
    'e4-threads' { $files += 'local-control-e4-threads.yml' }
    'e5-pool16' { $files += 'local-control-e5-pool16.yml' }
    'headroom' { $files += 'local-postgresql-headroom.yml' }
}
$fileArguments = @($files | ForEach-Object { '-f'; $_ })
$commonArguments = $fileArguments + @('--env-file', '.env.e2e', '-p', 'dms-local')

if ($PSCmdlet.ShouldProcess("dms-local config$(if ($RecreateDb -or $Condition -eq 'headroom') { ' and db' })", "Recompose as '$Condition' ($ResourceProfile)")) {
    Push-Location $composeDirectory
    $previousLogLevel = $env:DMS_CONFIG_LOG_LEVEL
    $previousClaimsMount = $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE
    try {
        $env:DMS_CONFIG_LOG_LEVEL = 'Debug'
        if ($ClaimsMountSource) { $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE = (Resolve-Path $ClaimsMountSource).Path }
        if ($RecreateDb -or $Condition -eq 'headroom') {
            & docker compose @commonArguments up -d --no-deps --force-recreate db
            if ($LASTEXITCODE -ne 0) { throw "docker compose up db failed ($LASTEXITCODE)." }
        }
        # The diagnostics overlay starts CMS in suspend mode, so CMS blocks until the
        # dotnet-monitor sidecar attaches; a stack torn down and rebuilt without the overlay
        # (e.g. by build-dms.ps1 E2ETest) has no sidecar. Idempotent when it is running.
        & docker compose @commonArguments up -d --no-deps cms-monitor
        if ($LASTEXITCODE -ne 0) { throw "docker compose up cms-monitor failed ($LASTEXITCODE)." }
        & docker compose @commonArguments up -d --no-deps config
        if ($LASTEXITCODE -ne 0) { throw "docker compose up config failed ($LASTEXITCODE)." }
        if ($RecreateDb -or $Condition -eq 'headroom') {
            # A recreated PostgreSQL terminates every pooled CMS connection (57P01); CMS
            # would otherwise serve the next request - the harness's token mint, which
            # precedes its own restart - on a dead connection.
            & docker restart $CmsContainerName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "docker restart $CmsContainerName failed ($LASTEXITCODE)." }
        }
    }
    finally {
        $env:DMS_CONFIG_LOG_LEVEL = $previousLogLevel
        $env:DMS_CONFIG_CLAIMS_MOUNT_SOURCE = $previousClaimsMount
        Pop-Location
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    while ($true) {
        try {
            if ((Invoke-WebRequest -Uri $HealthUrl -TimeoutSec 5).StatusCode -eq 200) { break }
        }
        catch {
            Write-Verbose "CMS not healthy yet: $($_.Exception.Message)"
        }
        if ([DateTime]::UtcNow -ge $deadline) { throw "CMS did not become healthy at $HealthUrl within 180 s." }
        Start-Sleep -Seconds 2
    }
    Write-Output "Stack recomposed as '$Condition' ($ResourceProfile); files: $($files -join ', ')"
}
