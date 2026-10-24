# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    One PostgreSQL endpoint timing run against a freshly built cs-local Docker stack.
.DESCRIPTION
    1. Refuses to start unless the staging, work and target folders neither equal, contain nor lie
       inside the collector checkout (and the staging folder none of the others), the collector
       checkout is clean, power condition P holds, port 8081 is free, and no cs-local container or
       ed-fi-api-config-local image exists.
    2. Builds and starts the stack from the prepared target (prepare-target.ps1) with that target's
       own setup-local-cms.ps1, which builds the image without cache.
    3. Before collection, requires the running container to carry the pre-registered values of the
       exact settings allowlist and exactly five valid assembly fingerprints, and power condition P
       again immediately before the collector launches.
    4. Runs the explicit endpoint timing collector once, from the collector checkout (Release,
       --no-build), with its output and results file in the work folder, checks power condition P
       as soon as it exits, whatever its outcome, and requires that it ran the collector fixture's
       two tests and wrote this run's samples and header.
    5. Requires the same container, image, settings and assemblies after collection, then tears the
       stack down.

    Any failed stage, including teardown, makes the script exit 1 after cleanup; success exits 0.
    A power check that fails before the collector launch is a refusal, not an attempt: it is
    appended to the staging folder's refusals.log, which is never rewritten, and the run may be
    started again once the environment is corrected. From the launch on, the run is its one attempt.
    Only the power snapshots of protocol E1 are implemented; its sampler coverage is not, so no run
    of this script is a complete E1 run.

    Written to -OutputDir (the evidence staging folder): the samples CSV and header JSON,
    provenance-*.json, refusals.log and lane-postgresql-*.log, which holds only status lines,
    checked counts, unmet power condition codes and exception type names. Written to -WorkRoot
    (never evidence): the collector's own output and results file, setup and teardown logs, and the
    full text of any exception.

    The lane reads no secret itself; the stack takes its settings from the target's
    eng/docker-compose/.env.config.e2e.
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
Import-Module (Join-Path $PSScriptRoot 'CollectionLane.psm1') -Force

$container = 'ed-fi-api-config-service'
$settingsAllowlist = Get-SettingAllowlist
$fingerprintedAssemblies = Get-FingerprintedAssemblyName
$failures = [System.Collections.Generic.List[object]]::new()
$powerChecks = [System.Collections.Generic.List[object]]::new()

# Every later file operation uses these normalized forms, so it acts on exactly the folders checked.
$CollectorRoot = Resolve-LanePath $CollectorRoot
$OutputDir = Resolve-LanePath $OutputDir
$WorkRoot = Resolve-LanePath $WorkRoot
$TargetRoot = Resolve-LanePath $TargetRoot
Assert-LaneRoot -CollectorRoot $CollectorRoot -OutputDir $OutputDir -WorkRoot $WorkRoot -TargetRoot $TargetRoot
New-Item -ItemType Directory -Force -Path $OutputDir, $WorkRoot | Out-Null
$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")
$log = Join-Path $OutputDir "lane-postgresql-$RunLabel-$stamp.log"
$provenancePath = Join-Path $OutputDir "provenance-endpoint-postgresql-$RunLabel-$stamp.json"
$refusalLog = Join-Path $OutputDir 'refusals.log'
$diagnostics = Join-Path $WorkRoot "lane-postgresql-$RunLabel-$stamp.diagnostics.txt"
$runnerLog = Join-Path $WorkRoot "collector-postgresql-$RunLabel-$stamp.log"
$trxName = "collector-postgresql-$RunLabel-$stamp.trx"
$e2eProject = Join-Path $CollectorRoot 'src\config\tests\EdFi.DmsConfigurationService.Tests.E2E\EdFi.DmsConfigurationService.Tests.E2E.csproj'
$targetE2eDirectory = Join-Path $TargetRoot 'src\config\tests\EdFi.DmsConfigurationService.Tests.E2E'

function Write-LaneLog([string] $Message) {
    "[$((Get-Date).ToUniversalTime().ToString('o'))] $Message" | Out-File -LiteralPath $log -Append -Encoding utf8NoBOM
}

function Add-LaneFailure([string] $Stage, [System.Management.Automation.ErrorRecord] $ErrorRecord) {
    $type = $ErrorRecord.Exception.GetType().FullName
    $failures.Add([ordered]@{ stage = $Stage; error_type = $type })
    "[$Stage]`n$($ErrorRecord | Out-String)" | Out-File -LiteralPath $diagnostics -Append -Encoding utf8NoBOM
    Write-LaneLog "FAILED $Stage ($type; full text in the work folder)"
}

function Invoke-Native([string] $What, [scriptblock] $Command) {
    $output = & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What exited $LASTEXITCODE." }
    $output
}

# Observes the power source at one check, records it and throws unless condition P holds. A check
# before the collector launch that fails is also a refusal, appended to the refusal log. The text
# of a failed observation goes to the work folder only.
function Invoke-PowerCheck([string] $Check) {
    $reading = $null
    try { $reading = Get-PowerSource }
    catch { "[power $Check]`n$($_ | Out-String)" | Out-File -LiteralPath $diagnostics -Append -Encoding utf8NoBOM }
    $record = ConvertTo-PowerCheckRecord -Check $Check -Reading $reading
    $powerChecks.Add($record)
    if ($record.condition_met) {
        Write-LaneLog "POWER $Check condition P met"
        return
    }
    if ($Check -ne 'after_exit') {
        Add-PowerRefusal -Path $refusalLog -Lane 'endpoint-postgresql' -RunLabel $RunLabel -Record $record
        $provenance.refused_at = $Check
        Write-LaneLog "REFUSED $Check, condition P not met: $($record.unmet -join ', ')"
    }
    else {
        Write-LaneLog "POWER $Check condition P not met: $($record.unmet -join ', ')"
    }
    throw "Power condition P is not met at $Check."
}

# Reads the allowlisted settings (from the container environment, otherwise from the image's
# appsettings.json, recording the source either way) and fingerprints the production assemblies
# the container actually runs. Every native call must succeed and every fingerprint must be present.
function Get-ContainerSnapshot {
    $inspected = (Invoke-Native 'docker inspect' { docker inspect $container } | Out-String | ConvertFrom-Json)[0]
    $image = (Invoke-Native 'docker image inspect' { docker image inspect $inspected.Image } | Out-String | ConvertFrom-Json)[0]
    $environment = @{}
    foreach ($line in $inspected.Config.Env) {
        $name, $value = $line -split '=', 2
        if ($settingsAllowlist -contains $name) { $environment[$name] = $value }
    }
    $appsettings = $null
    $settings = [ordered]@{}
    $sources = [ordered]@{}
    foreach ($name in $settingsAllowlist) {
        if ($environment.ContainsKey($name)) {
            $settings[$name] = $environment[$name]
            $sources[$name] = 'container environment (docker inspect .Config.Env)'
            continue
        }
        if ($null -eq $appsettings) {
            $appsettings = Invoke-Native 'docker exec cat appsettings.json' { docker exec $container cat /app/appsettings.json } | Out-String | ConvertFrom-Json
        }
        $node = $appsettings
        foreach ($segment in $name -split '__') { $node = if ($null -ne $node) { $node.$segment } else { $null } }
        $settings[$name] = if ($null -ne $node) { [string]$node } else { $null }
        $sources[$name] = if ($null -ne $node) { '/app/appsettings.json in the image (not set in the container environment)' } else { 'not set' }
    }
    $listing = Invoke-Native 'docker exec sha256sum' { docker exec $container sh -c "cd /app && sha256sum $($fingerprintedAssemblies -join ' ')" }
    [ordered]@{
        utc             = (Get-Date).ToUniversalTime().ToString('o')
        container_id    = $inspected.Id
        container_name  = $inspected.Name.TrimStart('/')
        running         = [string]$inspected.State.Running
        started_at      = $inspected.State.StartedAt
        restart_count   = [string]$inspected.RestartCount
        image_id        = $inspected.Image
        image_created   = $image.Created
        settings        = $settings
        setting_sources = $sources
        assemblies      = ConvertFrom-Sha256Listing -Lines @($listing)
    }
}

"" | Out-File -LiteralPath $log -Encoding utf8NoBOM
$provenance = [ordered]@{
    schema                   = 'timing-provenance/4'
    environment_protocol     = [ordered]@{
        version         = 'E1'
        complete        = $false
        implemented     = 'power condition P before setup, before the collector launch and after it exits'
        not_implemented = 'sampler readiness and continuous coverage'
    }
    attempt_started          = $false
    lane                     = 'endpoint-postgresql (cs-local Docker stack)'
    lane_script              = Split-Path -Leaf $PSCommandPath
    lane_script_sha256       = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    lane_module_sha256       = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'CollectionLane.psm1') -Algorithm SHA256).Hash.ToLowerInvariant()
    parameters               = [ordered]@{ run_label = $RunLabel; rounds = $Rounds; warmup_rounds = $WarmupRounds; seed = $Seed; contract = $Contract }
    settings_allowlist       = $settingsAllowlist
    assemblies_fingerprinted = $fingerprintedAssemblies
}
$stackStarted = $false
try {
    $collectorCommit = (Invoke-Native 'git rev-parse' { git -C $CollectorRoot rev-parse HEAD } | Out-String).Trim()
    $collectorStatus = Invoke-Native 'git status' { git -C $CollectorRoot status --porcelain }
    $provenance.collector = [ordered]@{ commit = $collectorCommit; worktree_clean = [string]::IsNullOrWhiteSpace(($collectorStatus | Out-String)) }
    if (-not $provenance.collector.worktree_clean) { throw 'The collector checkout is not clean.' }

    $target = Get-Content -LiteralPath (Join-Path $TargetRoot 'target.json') -Raw | ConvertFrom-Json
    $provenance.target = $target
    Write-LaneLog "LANE label=$RunLabel rounds=$Rounds warmup=$WarmupRounds seed=$Seed contract=$Contract collector=$collectorCommit target=$($target.commit)"
    Invoke-PowerCheck 'before_setup'

    if (Get-NetTCPConnection -LocalPort 8081 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 8081 is already in use.' }
    $stackContainers = Invoke-Native 'docker ps' { docker ps -a --filter 'label=com.docker.compose.project=cs-local' --format '{{.Names}}' }
    if ($stackContainers) { throw 'cs-local containers are present.' }
    $existingImages = @(Invoke-Native 'docker images' { docker images ed-fi-api-config-local --format '{{.ID}}' })
    $provenance.images_before_setup = $existingImages
    if ($existingImages.Count -gt 0) { throw 'An ed-fi-api-config-local image already exists.' }

    Write-LaneLog "SETUP target's setup-local-cms.ps1 (log in the work folder)"
    $stackStarted = $true
    & pwsh -NoProfile -File (Join-Path $targetE2eDirectory 'setup-local-cms.ps1') *> (Join-Path $WorkRoot "setup-local-cms-$RunLabel-$stamp.log")
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
    if ($before.running -ne 'True') { throw 'The container is not running.' }
    Assert-RunSetting -Value $before.settings -Datastore postgresql
    Write-LaneLog "BEFORE container=$($before.container_id) image=$($before.image_id) settings and five fingerprints verified"

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
    $env:TIMING_EVIDENCE_DECLARED_ITERATIONS = $before.settings['IdentitySettings__ClientSecretHashingIterations']
    Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue
    & dotnet build-server shutdown *> $null
    Invoke-PowerCheck 'before_launch'
    Write-LaneLog 'RUN collector (Release, --no-build; output and results file in the work folder); the launch starts the attempt'
    $provenance.attempt_started = $true
    $collectorStarted = (Get-Date).ToUniversalTime()
    try {
        & dotnet test $e2eProject -c Release --no-build --no-restore --nologo --filter "FullyQualifiedName~$(Get-CollectorFixtureName)" --logger "trx;LogFileName=$trxName" --results-directory $WorkRoot *> $runnerLog
        $collectorExit = $LASTEXITCODE
    }
    finally {
        # Whatever the collector did; a failure here is recorded beside any failure of the run.
        try { Invoke-PowerCheck 'after_exit' } catch { Add-LaneFailure 'power-after-exit' $_ }
    }
    $provenance.collector_exit = $collectorExit
    $summary = Assert-CollectorRun -ExitCode $collectorExit -TrxPath (Join-Path $WorkRoot $trxName) -OutputDir $OutputDir -RunLabel $RunLabel -Backend postgresql -Rounds $Rounds -Seed $Seed -StartedUtc $collectorStarted
    $provenance.collector_run = $summary
    Write-LaneLog "RUN tests_passed=$($summary.tests_passed) samples=$($summary.samples_file) rows=$($summary.sample_rows)"

    $after = Get-ContainerSnapshot
    $provenance.after = $after
    Assert-RunSetting -Value $after.settings -Datastore postgresql
    $differences = Compare-LaneSnapshot -Before $before -After $after -IdentityField container_id, started_at, restart_count, running, image_id
    $provenance.deployment_differences = $differences
    if ($differences.Count -gt 0) { throw "The deployment changed during collection: $($differences -join '; ')." }
    Write-LaneLog 'AFTER the same container, image, settings and assemblies served the whole collection'
}
catch {
    Add-LaneFailure 'run' $_
}
finally {
    if ($stackStarted) {
        try {
            & pwsh -NoProfile -File (Join-Path $targetE2eDirectory 'teardown-local-cms.ps1') *> (Join-Path $WorkRoot "teardown-local-cms-$RunLabel-$stamp.log")
            if ($LASTEXITCODE -ne 0) { throw "teardown-local-cms.ps1 exited $LASTEXITCODE." }
            Write-LaneLog 'TEARDOWN done'
        }
        catch {
            Add-LaneFailure 'teardown' $_
        }
    }
    $provenance.power_checks = $powerChecks.ToArray()
    $provenance.failures = $failures.ToArray()
    $provenance.succeeded = $failures.Count -eq 0
    $provenance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $provenancePath -Encoding utf8NoBOM
    Write-LaneLog "DONE succeeded=$($provenance.succeeded) provenance=$(Split-Path -Leaf $provenancePath)"
}
exit ([int]($failures.Count -gt 0))
