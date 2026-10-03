# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Isolated process: keep the launchers' real nested/forced imports and Compose resolver.
# Only host effects are replaced. Unexpected Docker operations fail closed.
param([string]$ComposeRoot, [string]$Project, [string]$Provider, [string]$IdentityProvider,
    [string]$StatePath, [string]$ResultPath, [switch]$RejectConfiguration)
$ErrorActionPreference = 'Stop'
$script:dockerExecutable = @(Get-Command docker -CommandType Application)[0].Source
$script:events = [Collections.Generic.List[string]]::new()
$script:configuration = @{}
$script:rollout = $false
$script:reject = $RejectConfiguration.IsPresent
function global:docker {
    $arguments = @($args | ForEach-Object { $_ })
    $global:LASTEXITCODE = 0
    if ($arguments[0] -eq 'network' -and $arguments[1] -eq 'ls') { return @('bridge', 'dms') }
    if ($arguments[0] -eq 'ps') { return 'selected-http-host' }
    if ($arguments[0] -eq 'inspect') {
        return ConvertTo-Json -Depth 20 -InputObject @(@{
            State = @{ Running = $true }
            Config = @{ Env = @($script:configuration.services.dms.environment.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) }
            NetworkSettings = @{ Ports = @{ '18080/tcp' = @(@{ HostIp = '127.0.0.1'; HostPort = '18080' }) } }
        })
    }
    if ($arguments[0] -ne 'compose') { throw 'Unexpected Docker command' }
    if ($arguments -contains 'config') {
        $script:events.Add('config')
        if ($arguments -contains (Join-Path $ComposeRoot 'retained.dms.json') -or
            @($arguments | Where-Object { $_ -match 'api-e2e-[a-f0-9]{32}\.http\.json$' }).Count -ne 1 -or
            $arguments -notcontains 'bootstrap-dms.yml' -or $arguments -contains 'kafka-cdc.yml') {
            throw 'Incorrect HTTP Compose file selection'
        }
        $output = @(& $script:dockerExecutable @arguments 2>$null)
        if ($LASTEXITCODE -ne 0) { throw 'Real Compose configuration failed' }
        $script:configuration = ($output -join "`n") | ConvertFrom-Json -AsHashtable
        if ($script:configuration.services.dms.environment.AppSettings__Datastore -cne $Provider -or
            $script:configuration.services.dms.environment.AppSettings__PathBase -cne 'api' -or
            $script:configuration.services.dms.environment.AppSettings__ApiSchemaPath -cne '/app/ApiSchema') {
            throw 'Incorrect resolved HTTP environment'
        }
        if ($script:reject) {
            $script:configuration.services.dms.environment.DataManagement__DocumentCache__Targets__0__DataStoreId = '42'
        }
        return $script:configuration | ConvertTo-Json -Depth 40
    }
    if ($arguments[-1] -ne 'dms') { throw 'Unexpected service operation' }
    if ($arguments -contains 'stop') { $script:events.Add('stop-dms'); return }
    if ($arguments -contains 'ps') { $script:events.Add('verify-stopped'); return }
    if ($arguments -contains 'up' -and $arguments -contains '--no-deps' -and $arguments -notcontains '--remove-orphans') {
        $script:events.Add('up-dms'); return
    }
    throw 'Unexpected Compose operation'
}
function global:Invoke-WebRequest {
    param($Uri, $Method, $TimeoutSec)
    if ($Method -ne 'Get' -or $TimeoutSec -ne 5) { throw 'Unexpected HTTP operation' }
    if ($script:rollout -and $Uri -cne 'http://127.0.0.1:18080/api/health') { throw 'Incorrect health endpoint' }
    if (@(Get-ChildItem (Join-Path $ComposeRoot '.cdc-deployments') -Filter '*.handoff.json').Count -ne 0) {
        throw 'Handoff published before healthy replacement'
    }
    $script:events.Add('health')
    return @{ StatusCode = 200 }
}

Import-Module (Join-Path $ComposeRoot 'e2e-cdc.psm1')
Import-Module (Join-Path $ComposeRoot 'cdc-lifecycle.psm1')
$start = Join-Path $ComposeRoot "start-$($Project.Replace('dms-', ''))-dms.ps1"
# Admission reload followed by the initial admitted launcher mirrors wrapper ordering.
& (Get-Module e2e-cdc) {
    param($dependency)
    & (Get-Module bootstrap-wrapper) { param($path) Import-Module $path -Force } $dependency
} (Join-Path $ComposeRoot 'bootstrap-cdc.psm1')
Invoke-CdcAdmittedHost -Project $Project -StartScript $start -Parameters @{
    EnvironmentFile = Join-Path $ComposeRoot '.env.selected'; DatabaseEngine = $Provider
    IdentityProvider = $IdentityProvider; SeparateConfigDatabase = $true; DmsOnly = $true
    CdcDmsComposeFile = Join-Path $ComposeRoot 'retained.dms.json'
} | Out-Null
$script:events.Clear()
$script:rollout = $true
$result = @{ Succeeded = $false; Events = @(); Handoff = ''; Failure = '' }
try {
    $result.Handoff = & (Get-Module e2e-cdc) {
        param($project, $start, $state)
        Invoke-E2ECdcApiRollout -Project $project -StartScript $start -StatePath $state
    } $Project $start $StatePath
    Assert-E2ECdcApiAttachment $result.Handoff
    $result.Succeeded = $true
}
catch {
    # This probe consumes synthetic test inputs only; never invoke it with private live settings.
    $result.Failure = $_.Exception.Message
}
$result.Events = $script:events.ToArray()
$result | ConvertTo-Json -Depth 10 | Set-Content $ResultPath
