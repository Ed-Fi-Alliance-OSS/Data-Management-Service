# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    One explicit step of the E1 pilot stage (spec section 18.6, M3): Stage0, Preflight,
    PilotPostgreSql, PilotSqlServer or Planning. Each step needs its own approval.
.DESCRIPTION
    The collector checkout is the one that holds this script; every step first requires it to be
    clean, at its own top level and at -ExpectedCommit, so the driver, module, lanes, sampler and
    analysis scripts it runs are all the approved commit's. Every native command runs with literal
    arguments; its line and exit code are appended to the public <Stage>\<step>-commands.log, and
    its standard output and error go to a private folder under -Work. Any unexpected exit, missing
    prerequisite or failed check stops the step with exit 1 (diagnostics that raise a flag stop it
    with exit 3); nothing is retried and no step runs another. No step overwrites an earlier step's
    output.

    Stage0        clean checkout at -ExpectedCommit; toolchain versions; the collector Release build
                  (--no-incremental) and collector-build.json; both prepared targets verified
                  against their pinned trees (target-verification.json); analysis-manifest.json,
                  then both analysis selftests; and last, stage0.json, which records the commit, the
                  targets folder and the hash of each of those records. No deployment, no collector.
    Preflight     the declared preflight (Invoke-SamplerPreflight): one power reading and the sampler
                  alone for 10 s, written to preflight.json with the Stage0 record it follows. No
                  deployment, no request, no collector.
    PilotPostgreSql / PilotSqlServer
                  one lane run on target A5 (200 measured rounds, 1,200 warm-up rounds, seed 401 or
                  402), then diagnostics.py on its measured file. Before the lane: a completed
                  Stage0 for this commit and targets folder, a passed preflight after it, no launch
                  record for the seed (a seed is used once; a refusal before the launch does not use
                  it), and target A5 verified again against its pins (target-check-*.json). The SQL
                  Server pilot also needs the PostgreSQL pilot's diagnostics with no flag raised.
    Planning      for each backend: analyze_timing.py plan, fit, and simulate at every candidate N,
                  with the pinned parameters. It chooses nothing.

    The analysis identity is checked against -ExpectedCommit immediately before every Python
    analysis call, and the script run is the one just verified. A step's prerequisites passing is a
    machine check only; it never stands for the review and approval each step needs.

    Collection still needs the host on AC power with battery saver off and the Balanced scheme; the
    lanes enforce it. This script closes no process and prepares nothing on the host.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Stage0', 'Preflight', 'PilotPostgreSql', 'PilotSqlServer', 'Planning')] [string] $Step,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $ExpectedCommit,
    [string] $Stage = 'C:\dev\simpat\dms1490-timing\stage-e1\pilot',
    [string] $Work = 'C:\dev\simpat\dms1490-timing\work\runs-e1',
    [string] $Targets = 'C:\dev\simpat\dms1490-timing\work\targets',
    [string] $LocalDbInstance = 'DMS1490',
    [double] $PreflightSeconds = 10
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CollectionLane.psm1') -Force

# The pinned targets and the reserved pilot runs (spec section 18.5).
$targetPins = [ordered]@{
    a5       = @{ Commit = 'b2b8d5df47e5e48c46f9ca26fe0f2a58b787c399'; Tree = 'd420aa2c27a94a9d3736af7c82fd7ce4298e121e' }
    baseline = @{ Commit = '9a19802ddae9b23b103cd92dd6c27a8a48814cb7'; Tree = 'd5d810184f6f34e8732e1ce2ad06d79509c0cf4f' }
}
$runLabel = 'e1-pilot-a5'
$pilots = @{
    PilotPostgreSql = @{ Backend = 'postgresql'; Lane = 'lane-postgresql.ps1'; Seed = 401 }
    PilotSqlServer  = @{ Backend = 'mssql'; Lane = 'lane-localdb.ps1'; Seed = 402 }
}

# This script lives in <checkout>\reference\design\configuration-service\DMS-1490-timing-evidence\collection.
$Repository = Resolve-LanePath (Join-Path $PSScriptRoot '..\..\..\..\..')
$Stage = Resolve-LanePath $Stage
$Work = Resolve-LanePath $Work
$Targets = Resolve-LanePath $Targets
Assert-LaneRoot -CollectorRoot $Repository -OutputDir $Stage -WorkRoot $Work -TargetRoot (Join-Path $Targets 'a5')
$analysis = Join-Path $Stage 'analysis'
$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")
$private = Join-Path $Work "$($Step.ToLowerInvariant())-$stamp"
New-Item -ItemType Directory -Force -Path $Stage, $analysis, $private | Out-Null
$commandsLog = Join-Path $Stage "$($Step.ToLowerInvariant())-commands.log"
$files = [ordered]@{
    collectorBuild     = Join-Path $Stage 'collector-build.json'
    targetVerification = Join-Path $Stage 'target-verification.json'
    analysisManifest   = Join-Path $Stage 'analysis-manifest.json'
    toolchain          = Join-Path $Stage 'toolchain.json'
    stage0             = Join-Path $Stage 'stage0.json'
    preflight          = Join-Path $Stage 'preflight.json'
}
$stage0Records = @('collectorBuild', 'targetVerification', 'analysisManifest', 'toolchain')

function Write-StepLog([string] $Text) {
    "[$((Get-Date).ToUniversalTime().ToString('o'))] $Text" | Out-File -LiteralPath $commandsLog -Append -Encoding utf8NoBOM
}

function Format-Argument([string] $Value) {
    if ($Value -ne '' -and $Value -notmatch '[\s"]') { return $Value }
    '"' + (($Value -replace '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
}

function Get-Sha256([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

# Runs one native command with literal arguments, logs the line and its exit code publicly, keeps
# its output privately (or in -OutputPath), and throws on an exit outside -Accept.
function Invoke-Logged {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $FilePath,
        [string[]] $Arguments = @(),
        [int[]] $Accept = @(0),
        [string] $OutputPath = ''
    )
    $out = if ($OutputPath) { $OutputPath } else { Join-Path $private "$Name.out.txt" }
    $err = Join-Path $private "$Name.err.txt"
    $line = (@($FilePath) + $Arguments | ForEach-Object { Format-Argument $_ }) -join ' '
    $global:LASTEXITCODE = 0
    & $FilePath @Arguments 1> $out 2> $err
    $code = $LASTEXITCODE
    Write-StepLog "$line -> exit $code"
    if ($Accept -notcontains $code) { throw "$Name exited $code." }
    [pscustomobject]@{ ExitCode = $code; Output = (Get-Content -LiteralPath $out -Raw -ErrorAction SilentlyContinue) }
}

# One Python analysis call, made only after the analysis identity holds for the approved commit,
# with the script path that check verified.
function Invoke-Analysis {
    param(
        [Parameter(Mandatory)] [ValidateSet('analyze_timing', 'diagnostics')] [string] $Script,
        [Parameter(Mandatory)] [string] $Name,
        [string[]] $Arguments = @(),
        [int[]] $Accept = @(0),
        [string] $OutputPath = ''
    )
    $identity = Assert-AnalysisIdentity -RepositoryRoot $Repository -ManifestPath $files.analysisManifest -ExpectedCommit $ExpectedCommit
    Write-StepLog "ANALYSIS identity checked before $Name manifest=$($identity.manifest_sha256)"
    Invoke-Logged -Name $Name -FilePath 'python' -Arguments (@('-I', '-B', $identity.paths[$Script]) + $Arguments) -Accept $Accept -OutputPath $OutputPath
}

function Save-Json([string] $Path, $Value) {
    if (Test-Path -LiteralPath $Path) { throw "$(Split-Path -Leaf $Path) already exists; a step never overwrites earlier output." }
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Assert-Absent([string[]] $Path) {
    foreach ($item in $Path) { if (Test-Path -LiteralPath $item) { throw "$(Split-Path -Leaf $item) already exists; a step never overwrites earlier output." } }
}

function Assert-Present([string[]] $Path) {
    foreach ($item in $Path) { if (-not (Test-Path -LiteralPath $item)) { throw "$(Split-Path -Leaf $item) is missing; run and review the earlier step first." } }
}

function Get-CheckoutState {
    $status = (Invoke-Logged -Name 'git-status' -FilePath 'git' -Arguments @('-C', $Repository, 'status', '--porcelain')).Output
    if (-not [string]::IsNullOrWhiteSpace($status)) { throw 'The collector checkout is not clean.' }
    $top = ([string](Invoke-Logged -Name 'git-top-level' -FilePath 'git' -Arguments @('-C', $Repository, 'rev-parse', '--show-toplevel')).Output).Trim()
    if ((Resolve-LanePath $top) -ine $Repository) { throw 'This script is not in its own checkout''s collection folder.' }
    $head = ([string](Invoke-Logged -Name 'git-head' -FilePath 'git' -Arguments @('-C', $Repository, 'rev-parse', 'HEAD')).Output).Trim()
    if ($head -cne $ExpectedCommit) { throw "The collector checkout is at $head, not the approved $ExpectedCommit." }
    $tree = ([string](Invoke-Logged -Name 'git-tree' -FilePath 'git' -Arguments @('-C', $Repository, 'rev-parse', 'HEAD^{tree}')).Output).Trim()
    [ordered]@{ commit = $head; tree = $tree }
}

# The SHA-256 of a completed Stage0's record, which must be for this commit and targets folder and
# whose listed records must be unchanged.
function Get-Stage0Completion {
    Assert-Present $files.stage0
    $record = Get-Content -LiteralPath $files.stage0 -Raw | ConvertFrom-Json -AsHashtable
    if ($record.schema -cne 'stage0-complete/1' -or $record.expected_commit -cne $ExpectedCommit) { throw 'stage0.json is not a completed Stage0 of the approved commit.' }
    if ($record.targets_root -ine $Targets) { throw 'The targets folder is not the one Stage0 verified.' }
    foreach ($name in $stage0Records) {
        $leaf = Split-Path -Leaf $files[$name]
        if (-not (Test-Path -LiteralPath $files[$name]) -or (Get-Sha256 $files[$name]) -cne $record.records[$leaf]) { throw "$leaf is not the record Stage0 completed with." }
    }
    Get-Sha256 $files.stage0
}

# Requires the preflight to have passed after this Stage0.
function Assert-PassedPreflight([string] $Stage0Sha256) {
    Assert-Present $files.preflight
    $preflight = Get-Content -LiteralPath $files.preflight -Raw | ConvertFrom-Json -AsHashtable
    if ($preflight.outcome -cne 'passed') { throw 'The preflight did not pass; the pilots wait for its review.' }
    if ($preflight.stage0_sha256 -cne $Stage0Sha256 -or $preflight.expected_commit -cne $ExpectedCommit) { throw 'The preflight does not follow this Stage0.' }
}

# The diagnostics of a finished pilot; refuses a missing file or one that raised a flag.
function Get-CleanDiagnostic([string] $Backend) {
    $path = Join-Path $analysis "diagnostics-$Backend.json"
    Assert-Present $path
    $result = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($result.flags.stop_for_review -ne $false) { throw "The $Backend pilot's diagnostics raised a flag; the stage stopped for review." }
    $result
}

function Invoke-Stage0 {
    Assert-Absent (@($stage0Records | ForEach-Object { $files[$_] }) + $files.stage0)
    $checkout = Get-CheckoutState
    $python = ([string](Invoke-Logged -Name 'python-version' -FilePath 'python' -Arguments @('-I', '-c', 'import sys; print(sys.version.split()[0])')).Output).Trim()
    $numpy = ([string](Invoke-Logged -Name 'numpy-version' -FilePath 'python' -Arguments @('-I', '-c', 'import numpy; print(numpy.__version__)')).Output).Trim()
    $sdk = ([string](Invoke-Logged -Name 'dotnet-version' -FilePath 'dotnet' -Arguments @('--version')).Output).Trim()
    Save-Json $files.toolchain ([ordered]@{
            schema      = 'timing-toolchain/1'
            dotnet_sdk  = $sdk
            dotnet_sdks = @(([string](Invoke-Logged -Name 'dotnet-sdks' -FilePath 'dotnet' -Arguments @('--list-sdks')).Output).Trim() -split "`r?`n")
            pwsh        = $PSVersionTable.PSVersion.ToString()
            docker      = ([string](Invoke-Logged -Name 'docker-version' -FilePath 'docker' -Arguments @('version', '--format', '{{.Server.Version}}')).Output).Trim()
            sqllocaldb  = @(([string](Invoke-Logged -Name 'sqllocaldb-versions' -FilePath 'sqllocaldb' -Arguments @('versions')).Output).Trim() -split "`r?`n")
            python      = $python
            numpy       = $numpy
        })
    Invoke-Logged -Name 'build-server-shutdown' -FilePath 'dotnet' -Arguments @('build-server', 'shutdown') | Out-Null
    $project = Join-Path $Repository 'src\config\tests\EdFi.DmsConfigurationService.Tests.E2E\EdFi.DmsConfigurationService.Tests.E2E.csproj'
    $buildArguments = @('build', $project, '-c', 'Release', '--no-incremental', '--nologo')
    Invoke-Logged -Name 'collector-build' -FilePath 'dotnet' -Arguments $buildArguments | Out-Null
    $buildLine = (@('dotnet') + $buildArguments | ForEach-Object { Format-Argument $_ }) -join ' '
    Save-Json $files.collectorBuild (Get-CollectorBuildManifest -CollectorRoot $Repository -Commit $checkout.commit -Tree $checkout.tree -BuildCommand $buildLine -SdkVersion $sdk)
    $verified = Assert-CollectorBuild -CollectorRoot $Repository -ManifestPath $files.collectorBuild -ExpectedCommit $ExpectedCommit -HeadCommit $checkout.commit -HeadTree $checkout.tree
    Write-StepLog "COLLECTOR BUILD files=$($verified.files) manifest=$($verified.manifest_sha256)"
    $targetResults = [ordered]@{}
    foreach ($name in $targetPins.Keys) {
        $targetResults[$name] = Assert-TargetSource -RepositoryRoot $Repository -TargetRoot (Join-Path $Targets $name) -Commit $targetPins[$name].Commit -Tree $targetPins[$name].Tree
        Write-StepLog "TARGET $name verified tracked=$($targetResults[$name].tracked_files) allowed_extra=$($targetResults[$name].allowed_extra)"
    }
    Save-Json $files.targetVerification ([ordered]@{ schema = 'timing-target-verification/1'; targets_root = $Targets; targets = $targetResults })
    Save-Json $files.analysisManifest (Get-AnalysisManifest -RepositoryRoot $Repository -Commit $ExpectedCommit -EvidenceManifestPath (Join-Path $Repository 'reference\design\configuration-service\DMS-1490-timing-evidence\SHA256SUMS.txt') -PythonVersion $python -NumpyVersion $numpy)
    Invoke-Analysis -Script analyze_timing -Name 'analyze-timing-selftest' -Arguments @('selftest') | Out-Null
    Invoke-Analysis -Script diagnostics -Name 'diagnostics-selftest' -Arguments @('selftest') | Out-Null
    $final = Get-CheckoutState
    $records = [ordered]@{}
    foreach ($name in $stage0Records) { $records[(Split-Path -Leaf $files[$name])] = Get-Sha256 $files[$name] }
    Save-Json $files.stage0 ([ordered]@{
            schema          = 'stage0-complete/1'
            statement       = 'Machine prerequisites only; every later step still needs its own review and approval.'
            expected_commit = $ExpectedCommit
            tree            = $final.tree
            targets_root    = $Targets
            records         = $records
        })
    Write-StepLog "STAGE0 complete record=$(Get-Sha256 $files.stage0)"
}

function Invoke-Preflight([string] $Instance, [double] $Seconds) {
    $stage0 = Get-Stage0Completion
    Assert-Absent $files.preflight
    Get-CheckoutState | Out-Null
    $runId = [guid]::NewGuid().ToString('N')
    $paths = @{
        PowerPath       = Join-Path $private 'preflight-power.csv'
        OptionalPath    = Join-Path $private 'preflight-optional.csv'
        ReadyPath       = Join-Path $private 'preflight-ready.json'
        StopPath        = Join-Path $private 'preflight-stop'
        DiagnosticsPath = Join-Path $private 'preflight-sampler.diagnostics.txt'
    }
    # Locals only: a closure captures the variables of the scope that creates it.
    $laneStamp = $stamp
    $privateFolder = $private
    $samplerScript = Join-Path $PSScriptRoot 'power-sampler.ps1'
    $readyPath = $paths.ReadyPath
    $stopPath = $paths.StopPath
    $arguments = [ordered]@{
        RunId = $runId; RunLabel = 'e1-preflight'; LaneStamp = $laneStamp
        PowerPath = $paths.PowerPath; OptionalPath = $paths.OptionalPath; ReadyPath = $paths.ReadyPath; StopPath = $paths.StopPath; DiagnosticsPath = $paths.DiagnosticsPath
        LocalDbInstance = $Instance; DockerVm = $true
    }
    Write-StepLog "PREFLIGHT sampler alone for $Seconds s; roles requested: localdb ($Instance), docker_vm; no deployment, no collector"
    $result = Invoke-SamplerPreflight -RunId $runId -PowerPath $paths.PowerPath -WindowSeconds $Seconds `
        -ReadPower { Get-PowerSource } `
        -Open { Open-PowerSampler -ScriptPath $samplerScript -Argument $arguments -StandardOutputPath (Join-Path $privateFolder 'preflight-sampler.out.txt') -StandardErrorPath (Join-Path $privateFolder 'preflight-sampler.err.txt') }.GetNewClosure() `
        -WaitReady { param($Process) Wait-PowerSamplerReady -Process $Process -ReadyPath $readyPath -RunId $runId -RunLabel 'e1-preflight' -LaneStamp $laneStamp }.GetNewClosure() `
        -Close { param($Process) Close-PowerSampler -Process $Process -StopPath $stopPath }.GetNewClosure() `
        -ReadQpc { [System.Diagnostics.Stopwatch]::GetTimestamp() } `
        -Clock { [System.Diagnostics.Stopwatch]::GetTimestamp() / [double][System.Diagnostics.Stopwatch]::Frequency } `
        -Pause { param([int] $Milliseconds) Start-Sleep -Milliseconds $Milliseconds }
    $result.expected_commit = $ExpectedCommit
    $result.stage0_sha256 = $stage0
    Save-Json $files.preflight $result
    Write-StepLog "PREFLIGHT outcome=$($result.outcome)"
    if ($result.outcome -ne 'passed') { throw 'The preflight failed; it is kept for review and is not retried.' }
}

function Invoke-Pilot([hashtable] $Pilot) {
    $stage0 = Get-Stage0Completion
    Assert-PassedPreflight $stage0
    $diagnosticsPath = Join-Path $analysis "diagnostics-$($Pilot.Backend).json"
    Assert-Absent $diagnosticsPath
    if ($Pilot.Backend -eq 'mssql') { Get-CleanDiagnostic 'postgresql' | Out-Null }
    $launchRecord = Get-LaunchRecordPath -OutputDir $Stage -Backend $Pilot.Backend -RunLabel $runLabel -Seed $Pilot.Seed
    if (Test-Path -LiteralPath $launchRecord) { throw "Seed $($Pilot.Seed) was already launched ($(Split-Path -Leaf $launchRecord)); a seed is used once, and that attempt needs review." }
    $pattern = "provenance-endpoint-$($Pilot.Backend)-$runLabel-*.json"
    foreach ($earlier in @(Get-ChildItem -LiteralPath $Stage -Filter $pattern)) {
        if ((Get-Content -LiteralPath $earlier.FullName -Raw | ConvertFrom-Json).attempt_started -eq $true) {
            throw "Seed $($Pilot.Seed) was already attempted ($($earlier.Name)); a seed is used once."
        }
    }
    Get-CheckoutState | Out-Null
    $target = Assert-TargetSource -RepositoryRoot $Repository -TargetRoot (Join-Path $Targets 'a5') -Commit $targetPins.a5.Commit -Tree $targetPins.a5.Tree
    Save-Json (Join-Path $Stage "target-check-$($Pilot.Backend)-$stamp.json") ([ordered]@{ schema = 'timing-target-verification/1'; step = $Step; targets_root = $Targets; targets = [ordered]@{ a5 = $target } })
    Write-StepLog "TARGET a5 verified before the lane tracked=$($target.tracked_files) allowed_extra=$($target.allowed_extra)"
    $earlierRecords = @(Get-ChildItem -LiteralPath $Stage -Filter $pattern | ForEach-Object { $_.Name })
    $laneArguments = @(
        '-NoProfile', '-File', (Join-Path $PSScriptRoot $Pilot.Lane),
        '-RunLabel', $runLabel, '-Rounds', '200', '-WarmupRounds', '1200', '-Seed', [string]$Pilot.Seed,
        '-OutputDir', $Stage, '-CollectorRoot', $Repository, '-TargetRoot', (Join-Path $Targets 'a5'), '-WorkRoot', $Work,
        '-CollectorBuildManifest', $files.collectorBuild, '-ExpectedCollectorCommit', $ExpectedCommit
    )
    Invoke-Logged -Name "lane-$($Pilot.Backend)" -FilePath 'pwsh' -Arguments $laneArguments | Out-Null
    $provenance = @(Get-ChildItem -LiteralPath $Stage -Filter $pattern | Where-Object { $earlierRecords -notcontains $_.Name })
    if ($provenance.Count -ne 1) { throw "Expected one provenance record for this pilot, found $($provenance.Count)." }
    $record = Get-Content -LiteralPath $provenance[0].FullName -Raw | ConvertFrom-Json
    if ($record.succeeded -ne $true -or $record.attempt_started -ne $true -or [string]::IsNullOrEmpty($record.collector_run.samples_file)) { throw 'The pilot''s provenance does not record a successful attempt.' }
    if (-not (Test-Path -LiteralPath $launchRecord)) { throw 'The pilot''s provenance records a launch, but its launch record is missing.' }
    $diagnose = Invoke-Analysis -Script diagnostics -Name "diagnostics-$($Pilot.Backend)" -Arguments @(
        'diagnose', '--csv', (Join-Path $Stage $record.collector_run.samples_file),
        '--delta', '250', '--resamples', '5000', '--seed', '1'
    ) -Accept @(0, 3) -OutputPath $diagnosticsPath
    if ($diagnose.ExitCode -eq 3) {
        Write-StepLog "DIAGNOSTICS $($Pilot.Backend): a flag was raised; the stage stops for review"
        exit 3
    }
    Write-StepLog "DIAGNOSTICS $($Pilot.Backend): no flag raised (not a validity finding)"
}

function Invoke-Planning {
    Get-Stage0Completion | Out-Null
    $inputs = [ordered]@{}
    foreach ($backend in 'postgresql', 'mssql') {
        $inputs[$backend] = Join-Path $Stage (Get-CleanDiagnostic $backend).input
        Assert-Absent @(Join-Path $analysis "plan-$backend.json")
    }
    Get-CheckoutState | Out-Null
    foreach ($backend in $inputs.Keys) {
        Invoke-Analysis -Script analyze_timing -Name "plan-$backend" -Arguments @('plan', '--csv', $inputs[$backend], '--delta', '250', '--candidates', '500', '1000', '2000', '4000', '--resamples', '5000', '--seed', '1') -OutputPath (Join-Path $analysis "plan-$backend.json") | Out-Null
        Invoke-Analysis -Script analyze_timing -Name "fit-$backend" -Arguments @('fit', '--csv', $inputs[$backend]) -OutputPath (Join-Path $analysis "fit-$backend.json") | Out-Null
        $fit = Get-Content -LiteralPath (Join-Path $analysis "fit-$backend.json") -Raw | ConvertFrom-Json
        foreach ($rounds in 500, 1000, 2000, 4000) {
            $parameters = @('--mu', $fit.mu, '--sigma', $fit.sigma, '--rho', $fit.rho, '--drift-sd', $fit.drift_sd) | ForEach-Object { [string]::Format([cultureinfo]::InvariantCulture, '{0}', $_) }
            Invoke-Analysis -Script analyze_timing -Name "simulate-$backend-$rounds" -Arguments (@('simulate', '--rounds', [string]$rounds, '--delta', '250') + $parameters + @('--replicates', '500', '--resamples', '5000', '--seed', '1')) -OutputPath (Join-Path $analysis "simulate-$backend-$rounds.json") | Out-Null
        }
    }
    Write-StepLog 'PLANNING outputs written for every candidate; the choice of N is for review'
}

Write-StepLog "STEP $Step expected_commit=$ExpectedCommit"
try {
    switch ($Step) {
        'Stage0' { Invoke-Stage0 }
        'Preflight' { Invoke-Preflight -Instance $LocalDbInstance -Seconds $PreflightSeconds }
        'Planning' { Invoke-Planning }
        default { Invoke-Pilot $pilots[$Step] }
    }
}
catch {
    "$($_ | Out-String)" | Out-File -LiteralPath (Join-Path $private 'failure.txt') -Append -Encoding utf8NoBOM
    # A stop this script or the module raised carries a fixed, safe message; any other exception's
    # text stays in the work folder.
    $reason = if ($_.Exception.GetType() -eq [System.Management.Automation.RuntimeException]) { ": $($_.Exception.Message)" } else { '' }
    Write-StepLog "STOPPED $Step ($($_.Exception.GetType().FullName); full text in the work folder)$reason"
    exit 1
}
Write-StepLog "DONE $Step; stop for review"
exit 0
