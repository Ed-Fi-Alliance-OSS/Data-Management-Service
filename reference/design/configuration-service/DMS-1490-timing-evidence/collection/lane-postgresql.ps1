# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    One PostgreSQL endpoint timing run against a freshly built cs-local Docker stack.
.DESCRIPTION
    1. Refuses to start unless the collector worktree is clean, port 8081 is free, no cs-local
       container exists and no ed-fi-api-config-local image exists.
    2. Builds and starts the stack from the prepared target (prepare-target.ps1) with that target's
       own setup-local-cms.ps1, which builds the image without cache.
    3. Records nonsecret provenance before and after collection: container and image identity,
       start time, restart count, an exact allowlist of settings read from the running container
       (never a whole settings group) and the SHA-256 of each Configuration Service production
       assembly inside the container.
    4. Runs the explicit endpoint timing collector once, from the collector worktree (Release,
       --no-build).
    5. Verifies the same container, image, settings and assemblies served the whole collection,
       then tears the stack down.

    Written to -OutputDir (evidence): the samples CSV and header JSON, provenance-*.json and
    lane-postgresql-*.log. Written to -WorkRoot (never evidence): setup and teardown logs, and the
    full text of any exception. The committed log names an exception's type only.

    The lane reads no secret itself; the stack takes its settings from the target's
    eng/docker-compose/.env.config.e2e, and the collector its system client from its own test
    source.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[a-z0-9-]+$')] [string] $RunLabel,
    [Parameter(Mandatory)] [ValidateRange(20, 100000)] [int] $Rounds,
    [Parameter(Mandatory)] [ValidateRange(0, 100000)] [int] $WarmupRounds,
    [Parameter(Mandatory)] [int] $Seed,
    [Parameter(Mandatory)] [string] $OutputDir,
    [Parameter(Mandatory)] [string] $CollectorRoot,
    [Parameter(Mandatory)] [string] $TargetRoot,
    [Parameter(Mandatory)] [string] $WorkRoot,
    [ValidateSet('current', 'pre-fix')] [string] $Contract = 'current'
)

$ErrorActionPreference = 'Stop'

$container = 'ed-fi-api-config-service'
$settingsAllowlist = @(
    'AppSettings__Datastore',
    'AppSettings__IdentityProvider',
    'AppSettings__MultiTenancy',
    'IdentitySettings__ClientSecretHashingIterations',
    'IdentitySettings__AllowRegistration'
)
$fingerprintedAssemblies = @(
    'EdFi.DmsConfigurationService.Frontend.AspNetCore.dll',
    'EdFi.DmsConfigurationService.Backend.dll',
    'EdFi.DmsConfigurationService.Backend.OpenIddict.dll',
    'EdFi.DmsConfigurationService.Backend.Postgresql.dll',
    'EdFi.DmsConfigurationService.Backend.Mssql.dll'
)

New-Item -ItemType Directory -Force -Path $OutputDir, $WorkRoot | Out-Null
$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")
$log = Join-Path $OutputDir "lane-postgresql-$RunLabel-$stamp.log"
$provenancePath = Join-Path $OutputDir "provenance-endpoint-postgresql-$RunLabel-$stamp.json"
$diagnostics = Join-Path $WorkRoot "lane-postgresql-$RunLabel-$stamp.diagnostics.txt"
$e2eProject = Join-Path $CollectorRoot 'src\config\tests\EdFi.DmsConfigurationService.Tests.E2E\EdFi.DmsConfigurationService.Tests.E2E.csproj'
$targetE2eDirectory = Join-Path $TargetRoot 'src\config\tests\EdFi.DmsConfigurationService.Tests.E2E'

function Write-LaneLog([string] $Message) {
    "[$((Get-Date).ToUniversalTime().ToString('o'))] $Message" | Out-File -LiteralPath $log -Append -Encoding utf8NoBOM
}

# Reads only the allowlisted keys: from the container environment when set there, otherwise from
# the image's appsettings.json, with the source recorded either way. Also fingerprints the
# production assemblies the container actually runs.
function Get-ContainerSnapshot {
    $inspected = (docker inspect $container | ConvertFrom-Json)[0]
    $image = (docker image inspect $inspected.Image | ConvertFrom-Json)[0]
    $environment = @{}
    foreach ($line in $inspected.Config.Env) {
        $name, $value = $line -split '=', 2
        if ($settingsAllowlist -contains $name) { $environment[$name] = $value }
    }
    $appsettings = $null
    $settings = [ordered]@{}
    foreach ($name in $settingsAllowlist) {
        if ($environment.ContainsKey($name)) {
            $settings[$name] = [ordered]@{ value = $environment[$name]; source = 'container environment (docker inspect .Config.Env)' }
            continue
        }
        if ($null -eq $appsettings) { $appsettings = (docker exec $container cat /app/appsettings.json) -join "`n" | ConvertFrom-Json }
        $node = $appsettings
        foreach ($segment in $name -split '__') { $node = if ($null -ne $node) { $node.$segment } else { $null } }
        $settings[$name] = [ordered]@{
            value  = if ($null -ne $node) { [string]$node } else { $null }
            source = if ($null -ne $node) { '/app/appsettings.json in the image (not set in the container environment)' } else { 'not set; the options class default applies' }
        }
    }
    $fingerprints = [ordered]@{}
    $hashed = docker exec $container sh -c "cd /app && sha256sum $($fingerprintedAssemblies -join ' ')"
    foreach ($line in $hashed) {
        $hash, $file = ($line.Trim() -split '\s+', 2)
        $fingerprints[$file.TrimStart('*')] = $hash
    }
    [ordered]@{
        utc            = (Get-Date).ToUniversalTime().ToString('o')
        container_id   = $inspected.Id
        container_name = $inspected.Name.TrimStart('/')
        running        = $inspected.State.Running
        started_at     = $inspected.State.StartedAt
        restart_count  = $inspected.RestartCount
        image_id       = $inspected.Image
        image_created  = $image.Created
        settings       = $settings
        assemblies     = $fingerprints
    }
}

"" | Out-File -LiteralPath $log -Encoding utf8NoBOM
$provenance = [ordered]@{
    schema                = 'timing-provenance/2'
    lane                  = 'endpoint-postgresql (cs-local Docker stack)'
    lane_script           = Split-Path -Leaf $PSCommandPath
    lane_script_sha256    = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    parameters            = [ordered]@{ run_label = $RunLabel; rounds = $Rounds; warmup_rounds = $WarmupRounds; seed = $Seed; contract = $Contract }
    settings_allowlist    = $settingsAllowlist
    assemblies_fingerprinted = $fingerprintedAssemblies
}
$stackStarted = $false
try {
    $collectorCommit = (git -C $CollectorRoot rev-parse HEAD).Trim()
    $collectorClean = [string]::IsNullOrEmpty((git -C $CollectorRoot status --porcelain))
    $provenance.collector = [ordered]@{ commit = $collectorCommit; worktree_clean = $collectorClean }
    if (-not $collectorClean) { throw 'The collector worktree is not clean.' }

    $target = Get-Content -LiteralPath (Join-Path $TargetRoot 'target.json') -Raw | ConvertFrom-Json
    $provenance.target = $target
    Write-LaneLog "LANE label=$RunLabel rounds=$Rounds warmup=$WarmupRounds seed=$Seed contract=$Contract collector=$collectorCommit target=$($target.commit)"

    if (Get-NetTCPConnection -LocalPort 8081 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 8081 is already in use.' }
    if (docker ps -a --filter 'label=com.docker.compose.project=cs-local' --format '{{.Names}}') { throw 'cs-local containers are present.' }
    $existingImages = @(docker images ed-fi-api-config-local --format '{{.ID}}')
    $provenance.images_before_setup = $existingImages
    if ($existingImages.Count -gt 0) { throw 'An ed-fi-api-config-local image already exists.' }

    $setupLog = Join-Path $WorkRoot "setup-local-cms-$RunLabel-$stamp.log"
    Write-LaneLog "SETUP target's setup-local-cms.ps1 (log in the work folder)"
    $stackStarted = $true
    & pwsh -NoProfile -File (Join-Path $targetE2eDirectory 'setup-local-cms.ps1') *> $setupLog
    if ($LASTEXITCODE -ne 0) { throw "setup-local-cms.ps1 exited $LASTEXITCODE." }
    $deadline = (Get-Date).AddSeconds(240)
    $healthy = $false
    while ((Get-Date) -lt $deadline -and -not $healthy) {
        try { $healthy = (Invoke-WebRequest http://localhost:8081/health -TimeoutSec 5 -SkipHttpErrorCheck).StatusCode -eq 200 } catch { $healthy = $false }
        if (-not $healthy) { Start-Sleep -Seconds 3 }
    }
    if (-not $healthy) { throw 'The Configuration Service did not become healthy.' }
    Write-LaneLog 'CMS HEALTHY'

    $before = Get-ContainerSnapshot
    $provenance.before = $before
    $settings = $before.settings
    Write-LaneLog "BEFORE container=$($before.container_id) image=$($before.image_id) $(($settingsAllowlist | ForEach-Object { "$_=$($settings[$_].value)" }) -join ' ')"
    if ($settings['AppSettings__Datastore'].value -ne 'postgresql') { throw 'The running service is not configured for postgresql.' }
    if ($settings['AppSettings__IdentityProvider'].value -ne 'self-contained') { throw 'The running service is not self-contained.' }

    $env:CONFIGURATION_SERVICE_E2E_API_URL = 'http://localhost:8081'
    $env:TIMING_EVIDENCE_OUTPUT_DIR = $OutputDir
    $env:TIMING_EVIDENCE_RUN_LABEL = $RunLabel
    $env:TIMING_EVIDENCE_ROUNDS = "$Rounds"
    $env:TIMING_EVIDENCE_WARMUP_ROUNDS = "$WarmupRounds"
    $env:TIMING_EVIDENCE_SEED = "$Seed"
    $env:TIMING_EVIDENCE_COMMIT = $collectorCommit
    $env:TIMING_EVIDENCE_BACKEND = 'postgresql'
    $env:TIMING_EVIDENCE_CONTRACT = $Contract
    $env:TIMING_EVIDENCE_TARGET_BUILD = "docker image $($before.image_id), built by this lane from $($target.commit) (git archive)"
    $env:TIMING_EVIDENCE_DECLARED_ITERATIONS = $settings['IdentitySettings__ClientSecretHashingIterations'].value
    Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue
    & dotnet build-server shutdown *> $null
    Write-LaneLog 'RUN collector (Release, --no-build)'
    & dotnet test $e2eProject -c Release --no-build --no-restore --nologo --filter 'FullyQualifiedName~Given_token_endpoint_requests_measured_for_timing_evidence' --logger 'console;verbosity=normal' *>> $log
    $provenance.collector_exit = $LASTEXITCODE
    Write-LaneLog "RUN EXIT $LASTEXITCODE"

    $after = Get-ContainerSnapshot
    $provenance.after = $after
    $same = $after.container_id -eq $before.container_id -and $after.started_at -eq $before.started_at -and
        $after.restart_count -eq $before.restart_count -and $after.running -and $after.image_id -eq $before.image_id
    foreach ($name in $settingsAllowlist) { $same = $same -and ($after.settings[$name].value -eq $before.settings[$name].value) }
    foreach ($assembly in $fingerprintedAssemblies) { $same = $same -and ($after.assemblies[$assembly] -eq $before.assemblies[$assembly]) }
    $provenance.same_deployment_throughout = $same
    Write-LaneLog "AFTER container=$($after.container_id) running=$($after.running) sameThroughout=$same"
}
catch {
    $provenance.lane_error_type = $_.Exception.GetType().FullName
    $_ | Out-String | Out-File -LiteralPath $diagnostics -Encoding utf8NoBOM
    Write-LaneLog "LANE ERROR $($provenance.lane_error_type) (full text in the work folder)"
}
finally {
    $provenance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $provenancePath -Encoding utf8NoBOM
    Write-LaneLog "PROVENANCE $(Split-Path -Leaf $provenancePath)"
    if ($stackStarted) {
        $teardownLog = Join-Path $WorkRoot "teardown-local-cms-$RunLabel-$stamp.log"
        & pwsh -NoProfile -File (Join-Path $targetE2eDirectory 'teardown-local-cms.ps1') *> $teardownLog
        Write-LaneLog "TEARDOWN exit=$LASTEXITCODE"
    }
    Write-LaneLog 'DONE'
}
