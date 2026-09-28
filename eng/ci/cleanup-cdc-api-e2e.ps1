# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param([string] $Repo, [string] $PrivateDirectory, [ValidateSet('Postgresql', 'Mssql')][string] $Provider,
    [string] $InvocationId)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$marker = Get-Content -LiteralPath (Join-Path $PrivateDirectory 'ownership.json') -Raw | ConvertFrom-Json
if ($marker.InvocationId -cne $InvocationId -or $marker.Project -cne 'dms-local' -or $marker.AbsentBeforeStart -ne $true) {
    throw 'No cleanup authority.'
}
$root = Join-Path $Repo 'eng/docker-compose'
$environment = Join-Path $PrivateDirectory '.env.e2e'
# Preparation may fail before the environment is created; no infrastructure can then have started.
if (-not (Test-Path -LiteralPath $environment)) { return }
$engine = if ($Provider -eq 'Mssql') { 'mssql' } else { 'postgresql' }
Set-Location $root
Import-Module (Join-Path $root 'env-utility.psm1') -DisableNameChecking
foreach ($name in (ReadValuesFromEnvFile $environment).Keys) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
$resources = [ordered]@{}
foreach ($kind in @('container', 'volume', 'network')) {
    $prefix = if ($kind -eq 'container') { @('ps', '-aq') } else { @($kind, 'ls', '-q') }
    $resources[$kind] = @(& docker @prefix --filter label=com.docker.compose.project=dms-local)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inventory owned resources.' }
}
# Retain exact project resource inventory even when preparation timed out midway.
@{ InvocationId = $InvocationId; Project = 'dms-local'; Resources = $resources } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $PrivateDirectory 'owned-resources.json')
$inventory = Join-Path $root '.cdc-deployments/dms-local.json'
$runtimePath = Join-Path $PrivateDirectory 'runtime-inputs.json'
. (Join-Path $PSScriptRoot 'cdc-api-diagnostics.ps1')
try {
    Write-CdcApiRuntimeInput -Path $runtimePath -InvocationId $InvocationId -Resources $resources -Governed (Test-Path -LiteralPath $inventory)
} catch { # Evidence collection must not prevent retirement.
    Write-Warning 'CDC_API_RUNTIME_DIAGNOSTICS_UNAVAILABLE'
}
if (Test-Path -LiteralPath $inventory) {
    # Infrastructure remains reachable for controller retirement; never delete inventory as cleanup.
    & (Join-Path $Repo 'src/dms/tests/EdFi.DataManagementService.Tests.E2E/teardown-local-dms.ps1') `
        -EnvironmentFile $environment -DatabaseEngine $engine
} else {
    # Explicit pre-start absence and exclusive runner ownership, including partial startup.
    # The primitive also rejects surviving managed CDC resources without inventory.
    Import-Module (Join-Path $root 'e2e-teardown.psm1')
    $plan = Get-E2ETeardownPlan -EnvironmentFile $environment -DatabaseEngine $engine -ComposeRoot $root
    Invoke-ComposeProjectTeardown -StartScript $plan.TeardownSteps[0].StartScript -StartParameters $plan.TeardownSteps[0].StartParameters
}
if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $inventory)) { throw 'Governed cleanup incomplete.' }
foreach ($prefix in @(@('ps', '-aq'), @('volume', 'ls', '-q'), @('network', 'ls', '-q'))) {
    $remaining = @(& docker @prefix --filter label=com.docker.compose.project=dms-local)
    if ($LASTEXITCODE -ne 0 -or $remaining.Count) { throw 'Owned resources remain.' }
}
try {
    $runtime = Get-Content -LiteralPath $runtimePath -Raw | ConvertFrom-Json -AsHashtable
    $runtime.ResourcesAbsent = $true
    $runtime | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $runtimePath
} catch { # Continue final state cleanup even if evidence storage failed.
    Write-Warning 'CDC_API_CLEANUP_DIAGNOSTICS_UNAVAILABLE'
}
$workspace = Join-Path $root '.bootstrap'
if (Test-Path -LiteralPath $workspace) { Move-Item -LiteralPath $workspace -Destination (Join-Path $PrivateDirectory 'retired-bootstrap') }
# State is removed last, only after verified infrastructure retirement. Failed cleanup retains all provenance.
$stateName = if ($Provider -eq 'Mssql') { 'state-sqlserver-e2e' } else { 'state-pg-e2e' }
$state = Join-Path $PrivateDirectory ".local/cdc/$stateName"
if (Test-Path -LiteralPath $state) { Remove-Item -LiteralPath $state -Recurse -Force }
