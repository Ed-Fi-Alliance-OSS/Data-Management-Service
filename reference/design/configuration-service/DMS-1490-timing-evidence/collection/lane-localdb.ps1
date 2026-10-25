# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    One SQL Server endpoint timing run on LocalDB, with the Configuration Service as a host process.
.DESCRIPTION
    For hosts that cannot run the SQL Server container (an ARM64 Windows host cannot). Steps:
    1. Refuses to start unless the staging, work and target folders neither equal, contain nor lie
       inside the collector checkout (and the staging folder none of the others), the collector
       checkout is clean and at -ExpectedCollectorCommit with exactly the Release output recorded in
       -CollectorBuildManifest, the prepared target's Release assemblies still match the five
       fingerprints prepare-target.ps1 recorded, power condition P holds, port 8081 is free, and
       no cs-local container exists.
    2. Starts the LocalDB instance, enables the sa login over its named pipe, and recreates the
       database with its files in -WorkRoot.
    3. Builds the service's launch environment as local-config.yml configures the container from
       the target's .env.config.mssql.e2e, requires the pre-registered values of the exact settings
       allowlist in it, and refuses any inherited configuration variable it does not set itself.
       Starts the target's Configuration Service as a host process, inserts the OpenIddict signing
       key and runs the target's setup-openiddict.ps1 -InsertData with `docker exec ... sqlcmd`
       routed to the host's sqlcmd, requiring each step to succeed.
    4. Starts the power sampler (power-sampler.ps1, roles cms and localdb), requires its ready
       record within 30 s with a first row that meets power condition P, and checks P again
       immediately before the collector launches. Then runs the explicit endpoint timing collector
       once, from the collector checkout (Release, --no-build), with its output and results file in
       the work folder. As soon as it exits, whatever its outcome, checks power condition P, stops
       the sampler and requires its coverage of the collection; then requires that the collector
       ran the fixture's two tests and wrote this run's samples and header.
    5. Requires the same live process, start time and assemblies after collection, then stops it.

    Any failed stage, including stopping the process, makes the script exit 1 after cleanup;
    success exits 0. A power check that fails, or a sampler that is not ready, before the collector
    launch is a refusal, not an attempt: it is appended to the staging folder's refusals.log, which
    is never rewritten, and the run may be started again once the environment is corrected. From
    the launch on, the run is its one attempt. Protocol E1's power checks and sampler coverage are
    implemented; its diagnostics and collector timestamps are not, so no run of this script is a
    complete E1 run.

    Written to -OutputDir (the evidence staging folder): the samples CSV and header JSON,
    provenance-*.json, refusals.log, the sampler's host-power-*.csv, host-optional-*.csv and
    host-ready-*.json, and lane-localdb-*.log, which holds only status lines, checked counts, unmet
    power condition and coverage codes, and exception type names. Written to -WorkRoot (never
    evidence): database files, the service's own output, the collector's output and results file,
    setup logs, the sampler's console output and stop signal, and the full text of any exception. Secrets come from the target's eng/docker-compose/.env.config.mssql.e2e and are
    referenced in provenance by key name only.
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
    [Parameter(Mandatory)] [string] $CollectorBuildManifest,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9a-f]{40}$')] [string] $ExpectedCollectorCommit,
    [string] $Instance = 'DMS1490',
    [ValidateSet('current', 'pre-fix')] [string] $Contract = 'current'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CollectionLane.psm1') -Force

$environmentFile = './.env.config.mssql.e2e'
$settingsAllowlist = Get-SettingAllowlist
$fingerprintedAssemblies = Get-FingerprintedAssemblyName
$expectedApplications = @('CMSAuthMetadataReadOnlyAccess', 'CMSReadOnlyAccess', 'DmsConfigurationService')
# The secret inputs, by the env-file key each comes from. Values are never written anywhere.
$secretSources = @(
    'MSSQL_SA_PASSWORD',
    'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY',
    'CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY',
    'CONFIGURATION_SERVICE_IDENTITY_CLIENT_SECRET'
)
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
$log = Join-Path $OutputDir "lane-localdb-$RunLabel-$stamp.log"
$provenancePath = Join-Path $OutputDir "provenance-endpoint-mssql-$RunLabel-$stamp.json"
$refusalLog = Join-Path $OutputDir 'refusals.log'
$diagnostics = Join-Path $WorkRoot "lane-localdb-$RunLabel-$stamp.diagnostics.txt"
$runnerLog = Join-Path $WorkRoot "collector-mssql-$RunLabel-$stamp.log"
$trxName = "collector-mssql-$RunLabel-$stamp.trx"
$composeDirectory = Join-Path $TargetRoot 'eng\docker-compose'
$cmsBin = Join-Path $TargetRoot 'src\config\frontend\EdFi.DmsConfigurationService.Frontend.AspNetCore\bin\Release\net10.0'
$cmsDll = Join-Path $cmsBin 'EdFi.DmsConfigurationService.Frontend.AspNetCore.dll'
$e2eProject = Join-Path $CollectorRoot 'src\config\tests\EdFi.DmsConfigurationService.Tests.E2E\EdFi.DmsConfigurationService.Tests.E2E.csproj'
$samplerScript = Join-Path $PSScriptRoot 'power-sampler.ps1'
$samplerRunId = [guid]::NewGuid().ToString('N')
$samplerFiles = [ordered]@{
    power       = Join-Path $OutputDir "host-power-mssql-$RunLabel-$stamp.csv"
    optional    = Join-Path $OutputDir "host-optional-mssql-$RunLabel-$stamp.csv"
    ready       = Join-Path $OutputDir "host-ready-mssql-$RunLabel-$stamp.json"
    stop        = Join-Path $WorkRoot "sampler-stop-mssql-$RunLabel-$stamp"
    stdout      = Join-Path $WorkRoot "sampler-mssql-$RunLabel-$stamp.out.log"
    stderr      = Join-Path $WorkRoot "sampler-mssql-$RunLabel-$stamp.err.log"
    diagnostics = Join-Path $WorkRoot "sampler-mssql-$RunLabel-$stamp.diagnostics.txt"
}
$sampler = $null
$samplerStopped = $false

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
        Add-PowerRefusal -Path $refusalLog -Lane 'endpoint-mssql' -RunLabel $RunLabel -Record $record
        $provenance.refused_at = $Check
        Write-LaneLog "REFUSED $Check, condition P not met: $($record.unmet -join ', ')"
    }
    else {
        Write-LaneLog "POWER $Check condition P not met: $($record.unmet -join ', ')"
    }
    throw "Power condition P is not met at $Check."
}

# Starts the power sampler and requires its ready record before the collector launch. A sampler
# that is not ready is a refusal, recorded like a failed power check.
function Open-LaneSampler([System.Collections.IDictionary] $Role) {
    $arguments = [ordered]@{
        RunId           = $samplerRunId
        RunLabel        = $RunLabel
        LaneStamp       = $stamp
        PowerPath       = $samplerFiles.power
        OptionalPath    = $samplerFiles.optional
        ReadyPath       = $samplerFiles.ready
        StopPath        = $samplerFiles.stop
        DiagnosticsPath = $samplerFiles.diagnostics
    }
    foreach ($name in $Role.Keys) { $arguments[$name] = $Role[$name] }
    $script:sampler = Open-PowerSampler -ScriptPath $samplerScript -Argument $arguments -StandardOutputPath $samplerFiles.stdout -StandardErrorPath $samplerFiles.stderr
    Write-LaneLog "SAMPLER started pid=$($script:sampler.Id)"
    $ready = Wait-PowerSamplerReady -Process $script:sampler -ReadyPath $samplerFiles.ready -RunId $samplerRunId -RunLabel $RunLabel -LaneStamp $stamp
    $provenance.sampler.ready_failure = $ready.failure
    if ($null -ne $ready.ready) {
        $provenance.sampler.identity = $ready.ready.identity
        $provenance.sampler.roles = $ready.ready.roles
    }
    if ($null -eq $ready.failure) {
        Write-LaneLog 'SAMPLER ready; its first row meets condition P'
        return
    }
    $record = ConvertTo-SamplerRefusalRecord -Ready $ready
    Add-PowerRefusal -Path $refusalLog -Lane 'endpoint-mssql' -RunLabel $RunLabel -Record $record
    $provenance.refused_at = 'before_launch'
    Write-LaneLog "REFUSED before_launch, the sampler is not ready: $($record.unmet -join ', ')"
    throw 'The power sampler is not ready.'
}

# Stops the sampler once the collector has exited and requires its coverage of the collection.
function Complete-LaneSampler([long] $LaunchQpc, [long] $ExitQpc) {
    $stop = Close-PowerSampler -Process $script:sampler -StopPath $samplerFiles.stop
    # Only a confirmed termination ends the sampler; otherwise cleanup tries once more and reports it.
    $script:samplerStopped = [bool]$stop.terminated
    $lines = @(if (Test-Path -LiteralPath $samplerFiles.power) { Get-Content -LiteralPath $samplerFiles.power })
    # Assigned first: @() around a call that returns its list as one object would nest it.
    $lifecycle = Get-SamplerLifecycleFailure $stop
    $covered = Test-SamplerCoverage -Line $lines -RunId $samplerRunId -QpcFrequency ([System.Diagnostics.Stopwatch]::Frequency) -LaunchQpc $LaunchQpc -ExitQpc $ExitQpc
    $coverage = @($lifecycle) + @($covered)
    $provenance.sampler.launch_qpc = $LaunchQpc
    $provenance.sampler.exit_qpc = $ExitQpc
    $provenance.sampler.qpc_frequency = [System.Diagnostics.Stopwatch]::Frequency
    $provenance.sampler.stop = $stop
    $provenance.sampler.power_rows = [Math]::Max(0, $lines.Count - 1)
    $provenance.sampler.coverage_failures = $coverage
    if ($coverage.Count -gt 0) {
        Write-LaneLog "SAMPLER coverage failed: $($coverage -join ', ')"
        throw 'The power sampler did not cover the collection.'
    }
    Write-LaneLog "SAMPLER coverage held rows=$($provenance.sampler.power_rows)"
}

function Get-AssemblyFingerprint {
    $fingerprints = [ordered]@{}
    foreach ($assembly in $fingerprintedAssemblies) {
        $path = Join-Path $cmsBin $assembly
        if (-not (Test-Path -LiteralPath $path)) { throw "$assembly is missing from the target build." }
        $fingerprints[$assembly] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    Assert-AssemblyFingerprint -Fingerprint $fingerprints
    $fingerprints
}

function Get-ProcessSnapshot([System.Diagnostics.Process] $Process, [System.Collections.IDictionary] $Settings) {
    $live = Get-Process -Id $Process.Id -ErrorAction SilentlyContinue
    [ordered]@{
        utc        = (Get-Date).ToUniversalTime().ToString('o')
        pid        = [string]$Process.Id
        alive      = [string]($null -ne $live -and -not $Process.HasExited)
        start_time = if ($live) { $live.StartTime.ToUniversalTime().ToString('o') } else { $null }
        settings   = $Settings
        assemblies = Get-AssemblyFingerprint
    }
}

"" | Out-File -LiteralPath $log -Encoding utf8NoBOM
$provenance = [ordered]@{
    schema                   = 'timing-provenance/4'
    environment_protocol     = [ordered]@{
        version               = 'E1'
        # Set by review of a run's evidence, never by this script.
        complete              = $false
        implemented           = 'power condition P before setup, before the collector launch and after it exits; sampler readiness and coverage; timing-evidence/2 round timestamps and warm-up file; offline diagnostics; collector build, target and analysis identity checks'
        acceptance            = 'pending: independent review of this run''s evidence'
        scheme_query_provider = Get-SchemeQueryProvider
    }
    attempt_started          = $false
    sampler                  = [ordered]@{
        script_sha256 = (Get-FileHash -LiteralPath $samplerScript -Algorithm SHA256).Hash.ToLowerInvariant()
        run_id        = $samplerRunId
        power_file    = Split-Path -Leaf $samplerFiles.power
        optional_file = Split-Path -Leaf $samplerFiles.optional
        ready_file    = Split-Path -Leaf $samplerFiles.ready
    }
    lane                     = 'endpoint-mssql (LocalDB, host-process Configuration Service)'
    lane_script              = Split-Path -Leaf $PSCommandPath
    lane_script_sha256       = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    lane_module_sha256       = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'CollectionLane.psm1') -Algorithm SHA256).Hash.ToLowerInvariant()
    parameters               = [ordered]@{ run_label = $RunLabel; rounds = $Rounds; warmup_rounds = $WarmupRounds; seed = $Seed; contract = $Contract; instance = $Instance }
    environment_file         = 'eng/docker-compose/.env.config.mssql.e2e (of the target)'
    secret_sources           = $secretSources
    settings_allowlist       = $settingsAllowlist
    assemblies_fingerprinted = $fingerprintedAssemblies
}
$cms = $null
$outputStreams = @()
$copies = @()
$shimInstalled = $false
$locationPushed = $false
try {
    $collectorCommit = (Invoke-Native 'git rev-parse' { git -C $CollectorRoot rev-parse HEAD } | Out-String).Trim()
    $collectorStatus = Invoke-Native 'git status' { git -C $CollectorRoot status --porcelain }
    $provenance.collector = [ordered]@{ commit = $collectorCommit; worktree_clean = [string]::IsNullOrWhiteSpace(($collectorStatus | Out-String)) }
    if (-not $provenance.collector.worktree_clean) { throw 'The collector checkout is not clean.' }
    # The collector build this lane runs (--no-build) must be the recorded Release output of the
    # approved commit, file for file, before anything is set up.
    $collectorTree = (Invoke-Native 'git rev-parse tree' { git -C $CollectorRoot rev-parse 'HEAD^{tree}' } | Out-String).Trim()
    $provenance.collector_build = Assert-CollectorBuild -CollectorRoot $CollectorRoot -ManifestPath $CollectorBuildManifest -ExpectedCommit $ExpectedCollectorCommit -HeadCommit $collectorCommit -HeadTree $collectorTree
    Write-LaneLog "COLLECTOR BUILD verified commit=$collectorCommit files=$($provenance.collector_build.files) manifest=$($provenance.collector_build.manifest_sha256)"

    $target = Get-Content -LiteralPath (Join-Path $TargetRoot 'target.json') -Raw | ConvertFrom-Json
    $provenance.target = $target
    if ($null -eq $target.host_release) { throw 'The target was prepared without -BuildHostRelease.' }
    $current = Get-AssemblyFingerprint
    foreach ($assembly in $fingerprintedAssemblies) {
        if ($current[$assembly] -ne $target.host_release.fingerprints.$assembly) { throw "$assembly no longer matches the prepared target." }
    }
    Write-LaneLog "LANE label=$RunLabel rounds=$Rounds warmup=$WarmupRounds seed=$Seed contract=$Contract collector=$collectorCommit target=$($target.commit)"
    Invoke-PowerCheck 'before_setup'

    if (Get-NetTCPConnection -LocalPort 8081 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 8081 is already in use.' }
    # Docker may not be running for this lane; then there is no cs-local container to collide with.
    $stackContainers = docker ps -a --filter 'label=com.docker.compose.project=cs-local' --format '{{.Names}}' 2>$null
    if ($stackContainers) { throw 'cs-local containers are present.' }

    Push-Location -LiteralPath $composeDirectory
    $locationPushed = $true
    Import-Module ./env-utility.psm1 -Force
    Import-Module ./database-safety.psm1 -Force
    Import-Module ./bootstrap-manifest.psm1 -Force
    Import-Module ./OpenIddict-Crypto.psm1 -Force
    $values = ReadValuesFromEnvFile $environmentFile
    $database = $values['POSTGRES_DB_NAME']
    $saPassword = $values['MSSQL_SA_PASSWORD']
    if ([string]::IsNullOrWhiteSpace($database) -or [string]::IsNullOrWhiteSpace($saPassword)) { throw 'The environment file lacks the database name or the sa password.' }

    # LocalDB: create if missing, start, and read the pipe in this same process (it changes on restart).
    $instances = Invoke-Native 'sqllocaldb info' { sqllocaldb info }
    if (-not ($instances -contains $Instance)) { Invoke-Native 'sqllocaldb create' { sqllocaldb create $Instance } | Out-Null }
    Invoke-Native 'sqllocaldb start' { sqllocaldb start $Instance } | Out-Null
    $information = Invoke-Native 'sqllocaldb info instance' { sqllocaldb info $Instance }
    $pipe = (($information | Select-String 'Instance pipe name:').ToString() -split ':', 2)[1].Trim()
    $version = (($information | Select-String '^Version:').ToString() -split ':', 2)[1].Trim()
    $provenance.localdb = [ordered]@{ instance = $Instance; version = $version; database = $database }
    Write-LaneLog "LOCALDB $Instance version=$version"

    $saFile = Join-Path $WorkRoot ('sa-' + [guid]::NewGuid().ToString('N') + '.sql')
    try {
        Set-Content -LiteralPath $saFile -Value "ALTER LOGIN sa WITH PASSWORD = N'$($saPassword.Replace("'", "''"))', CHECK_POLICY = OFF; ALTER LOGIN sa ENABLE;" -Encoding utf8NoBOM
        Invoke-Native 'enabling the sa login' { sqlcmd -S $pipe -E -b -i $saFile } | Out-Null
    }
    finally {
        Remove-Item -LiteralPath $saFile -ErrorAction SilentlyContinue
    }

    $env:SQLCMDPASSWORD = $saPassword
    $runSql = {
        param([string] $Sql, [string] $Database = 'master')
        $file = Join-Path $WorkRoot ('lane-sql-' + [guid]::NewGuid().ToString('N') + '.sql')
        try {
            Set-Content -LiteralPath $file -Value $Sql -Encoding utf8NoBOM
            $result = & sqlcmd -S $pipe -U sa -d $Database -C -b -I -h -1 -W -f 65001 -i $file 2>&1
            if ($LASTEXITCODE -ne 0) { throw "sqlcmd exited $LASTEXITCODE." }
            $result
        }
        finally {
            Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue
        }
    }

    $databaseDirectory = Join-Path $WorkRoot 'db'
    New-Item -ItemType Directory -Force -Path $databaseDirectory | Out-Null
    $mdf = Join-Path $databaseDirectory "$database-$Instance.mdf"
    $ldf = Join-Path $databaseDirectory "$database-$Instance-log.ldf"
    & $runSql "IF DB_ID(N'$database') IS NOT NULL BEGIN ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database]; END;" | Out-Null
    Remove-Item -LiteralPath $mdf, $ldf -ErrorAction SilentlyContinue
    & $runSql "CREATE DATABASE [$database] ON (NAME = N'$database', FILENAME = N'$mdf') LOG ON (NAME = N'${database}_log', FILENAME = N'$ldf');" | Out-Null
    Write-LaneLog "DATABASE $database recreated in the work folder"

    $claimsDirectory = Initialize-E2EClaimsWorkspace
    $launch = [ordered]@{
        ASPNETCORE_ENVIRONMENT                                  = 'Production'
        ASPNETCORE_URLS                                         = 'http://localhost:8081'
        AppSettings__Datastore                                  = 'mssql'
        DatabaseSettings__DatabaseConnection                    = "Server=$pipe;Database=$database;User Id=sa;Password=$saPassword;TrustServerCertificate=true;"
        DatabaseSettings__EncryptionKey                         = $values['CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY']
        IdentitySettings__AllowRegistration                     = $values['CONFIGURATION_SERVICE_IDENTITY_ALLOW_REGISTRATION']
        IdentitySettings__ConfigServiceRole                     = $values['CONFIGURATION_SERVICE_IDENTITY_SERVICE_ROLE']
        IdentitySettings__ClientRole                            = $values['CONFIGURATION_SERVICE_IDENTITY_CLIENT_ROLE']
        IdentitySettings__Authority                             = 'http://localhost:8081'
        IdentitySettings__Audience                              = $values['CONFIGURATION_SERVICE_IDENTITY_AUDIENCE']
        IdentitySettings__ClientId                              = $values['CONFIGURATION_SERVICE_IDENTITY_CLIENT_ID']
        IdentitySettings__ClientSecret                          = $values['CONFIGURATION_SERVICE_IDENTITY_CLIENT_SECRET']
        IdentitySettings__ClientSecretValidation__MinimumLength = $values['CONFIGURATION_SERVICE_IDENTITY_CLIENT_SECRET_MINIMUM_LENGTH']
        IdentitySettings__ClientSecretValidation__MaximumLength = $values['CONFIGURATION_SERVICE_IDENTITY_CLIENT_SECRET_MAXIMUM_LENGTH']
        IdentitySettings__RequireHttpsMetadata                  = $values['CONFIGURATION_SERVICE_IDENTITY_REQUIRE_HTTPS']
        IdentitySettings__RoleClaimType                         = $values['CONFIGURATION_SERVICE_IDENTITY_ROLE_CLAIM_TYPE']
        Serilog__MinimumLevel__Default                          = $values['CONFIGURATION_SERVICE_LOG_LEVEL']
        OtlpLogging__Enabled                                    = 'false'
        AppSettings__DeployDatabaseOnStartup                    = $values['CONFIGURATION_SERVICE_DEPLOY_DATABASE']
        AppSettings__TokenRequestTimeoutSeconds                 = $values['CONFIGURATION_SERVICE_TOKEN_TIMEOUT_SECONDS']
        AppSettings__IdentityProvider                           = 'self-contained'
        AppSettings__MultiTenancy                               = 'false'
        AppSettings__PathBase                                   = $values['CONFIGURATION_SERVICE_PATH_BASE']
        AppSettings__ReverseProxy__UseForwardedHeaders          = $values['CONFIGURATION_SERVICE_USE_REVERSE_PROXY_HEADERS']
        ClaimsOptions__ClaimsSource                             = $values['CONFIGURATION_SERVICE_CLAIMS_SOURCE']
        ClaimsOptions__DangerouslyEnableUnrestrictedClaimsLoading = $values['CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING']
        ClaimsOptions__ClaimsDirectory                          = $claimsDirectory
        ClaimsOptions__DataStandardVersion                      = '5.2'
        IdentitySettings__EncryptionKey                         = $values['CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY']
        IdentitySettings__ClientSecretHashingIterations         = $values['CONFIGURATION_SERVICE_IDENTITY_HASHING_ITERATIONS']
        IdentitySettings__BearerTokenPerClientLimit             = $values['CONFIGURATION_SERVICE_IDENTITY_BEARER_TOKEN_PER_CLIENT_LIMIT']
        Cors__SwaggerUIOrigin                                   = 'http://localhost:8082'
    }

    # The launch environment is this explicit block over a copy of the lane's own environment, so
    # what is recorded is what the process was started with. The service binds unprefixed
    # environment variables, so any inherited variable in a section it configures, or that sets its
    # hosting environment, is removed from the copy before the block is applied: the launch is then
    # fixed by the block alone. The names removed are recorded; their values are not.
    $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.ArgumentList.Add($cmsDll)
    $start.WorkingDirectory = $cmsBin
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $configurationPattern = '^((AppSettings|IdentitySettings|DatabaseSettings|ClaimsOptions|ConnectionStrings|Serilog|OtlpLogging|Cors|JobSettings|Plugins|RateLimit|Logging)__|ASPNETCORE_|DOTNET_ENVIRONMENT$|AllowedHosts$)'
    $removed = @($start.Environment.Keys | Where-Object { $_ -match $configurationPattern } | Sort-Object)
    foreach ($name in $removed) { [void]$start.Environment.Remove($name) }
    $provenance.removed_inherited_configuration_variable_names = $removed
    foreach ($name in $launch.Keys) { $start.Environment[$name] = [string]$launch[$name] }
    $uncontrolled = @($start.Environment.Keys | Where-Object { $_ -match $configurationPattern -and -not $launch.Contains($_) })
    if ($uncontrolled.Count -gt 0) { throw "Configuration variables remain outside the launch block: $($uncontrolled -join ', ')." }
    $launchSettings = [ordered]@{}
    foreach ($name in $settingsAllowlist) { $launchSettings[$name] = $start.Environment[$name] }
    $provenance.launch_settings = $launchSettings
    $provenance.launch_settings_source = 'explicit launch environment of the service process (ProcessStartInfo.Environment)'
    Assert-RunSetting -Value $launchSettings -Datastore mssql

    $cms = [System.Diagnostics.Process]::Start($start)
    $outputStreams = @(
        [System.IO.File]::Create((Join-Path $WorkRoot "cms-$RunLabel-$stamp.out.log")),
        [System.IO.File]::Create((Join-Path $WorkRoot "cms-$RunLabel-$stamp.err.log"))
    )
    $copies = @(
        $cms.StandardOutput.BaseStream.CopyToAsync($outputStreams[0]),
        $cms.StandardError.BaseStream.CopyToAsync($outputStreams[1])
    )
    Write-LaneLog "CMS STARTED pid=$($cms.Id)"
    $deadline = (Get-Date).AddSeconds(240)
    $healthy = $false
    while ((Get-Date) -lt $deadline -and -not $cms.HasExited -and -not $healthy) {
        try { $healthy = (Invoke-WebRequest http://localhost:8081/health -TimeoutSec 5 -SkipHttpErrorCheck).StatusCode -eq 200 } catch { $healthy = $false }
        if (-not $healthy) { Start-Sleep -Seconds 3 }
    }
    if (-not $healthy) { throw 'The Configuration Service did not become healthy.' }
    Write-LaneLog 'CMS HEALTHY'

    # The OpenIddict signing key, as InitDb's key step inserts it.
    $keyTable = (& $runSql "SET NOCOUNT ON; SELECT CASE WHEN OBJECT_ID('dmscs.OpenIddictKey','U') IS NULL THEN 0 ELSE 1 END;" $database | Where-Object { $_.Trim() } | Select-Object -First 1).Trim()
    if ($keyTable -ne '1') { throw 'dmscs.OpenIddictKey does not exist after deployment.' }
    $keyCommand = New-OpenIddictKeyInsertCommand -EncryptionKey $values['CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY'] -DbType MSSQL
    $connection = [System.Data.SqlClient.SqlConnection]::new("Server=$pipe;Database=$database;User Id=sa;Password=$saPassword;TrustServerCertificate=true")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $keyCommand.Sql
        $command.Parameters.Add('@KeyId', [System.Data.SqlDbType]::NVarChar, 64).Value = $keyCommand.Parameters.KeyId
        $command.Parameters.Add('@PublicKey', [System.Data.SqlDbType]::VarBinary, -1).Value = $keyCommand.Parameters.PublicKey
        $command.Parameters.Add('@PrivateKey', [System.Data.SqlDbType]::VarChar, -1).Value = $keyCommand.Parameters.PrivateKey
        $command.Parameters.Add('@EncryptionKey', [System.Data.SqlDbType]::NVarChar, -1).Value = $keyCommand.Parameters.EncryptionKey
        $keyRows = $command.ExecuteNonQuery()
    }
    finally {
        $connection.Dispose()
    }
    if ($keyRows -ne 1) { throw "The signing key insert affected $keyRows rows." }
    Write-LaneLog 'KEY INSERT rows=1'

    # setup-openiddict.ps1 -InsertData, unchanged, with `docker exec ... sqlcmd` routed to the host's sqlcmd.
    $docker = {
        $arguments = @($args)
        if ($arguments[0] -ne 'exec' -or [array]::IndexOf($arguments, '-Q') -lt 0) { throw "docker shim: unexpected call: docker $($arguments[0])" }
        $targetDatabase = $arguments[[array]::IndexOf($arguments, '-d') + 1]
        $query = $arguments[[array]::IndexOf($arguments, '-Q') + 1]
        $file = Join-Path $WorkRoot ('shim-' + [guid]::NewGuid().ToString('N') + '.sql')
        try {
            Set-Content -LiteralPath $file -Value $query -Encoding utf8NoBOM
            & sqlcmd -S $pipe -U sa -d $targetDatabase -C -b -I -h -1 -W -f 65001 -i $file
        }
        finally {
            Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue
        }
    }.GetNewClosure()
    Set-Item -LiteralPath Function:\global:docker -Value $docker
    $shimInstalled = $true
    $secrets = Resolve-IdentityClientSecretConfiguration -EnvValues $values
    $roles = @{
        ConfigServiceRole = Get-ComposeResolvedEnvValue -EnvironmentValues $values -Name 'CONFIGURATION_SERVICE_IDENTITY_SERVICE_ROLE' -DefaultValue 'cms-client'
        DmsClientRole     = Get-ComposeResolvedEnvValue -EnvironmentValues $values -Name 'CONFIGURATION_SERVICE_IDENTITY_CLIENT_ROLE' -DefaultValue 'dms-client'
    }
    $common = @{ EnvironmentFile = $environmentFile; SkipLegacyEnvironmentVariableWarning = $true; DbType = 'MSSQL'; DbUser = 'sa'; DbPort = 'ENV:MSSQL_PORT'; PostgresContainerName = 'dms-postgresql'; MssqlContainerName = 'dms-mssql' }
    $setupLog = Join-Path $WorkRoot "setup-openiddict-$RunLabel-$stamp.log"
    ./setup-openiddict.ps1 -InsertData @roles @common -NewClientSecret $secrets.DmsConfigurationServiceClientSecret -ClientSecretMinimumLength $secrets.ClientSecretMinimumLength -ClientSecretMaximumLength $secrets.ClientSecretMaximumLength *>> $setupLog
    ./setup-openiddict.ps1 -InsertData @roles @common -NewClientId 'CMSReadOnlyAccess' -NewClientName 'CMS ReadOnly Access' -ClientScopeName 'edfi_admin_api/readonly_access' -NewClientSecret $secrets.CmsReadOnlyAccessClientSecret -ClientSecretMinimumLength $secrets.ClientSecretMinimumLength -ClientSecretMaximumLength $secrets.ClientSecretMaximumLength *>> $setupLog
    ./setup-openiddict.ps1 -InsertData @roles @common -NewClientId 'CMSAuthMetadataReadOnlyAccess' -NewClientName 'CMS Auth Endpoints Only Access' -ClientScopeName 'edfi_admin_api/authMetadata_readonly_access' *>> $setupLog
    Remove-Item -LiteralPath Function:\global:docker
    $shimInstalled = $false
    $applications = @(& $runSql 'SET NOCOUNT ON; SELECT ClientId FROM dmscs.OpenIddictApplication ORDER BY ClientId;' $database | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() })
    if (@(Compare-Object -ReferenceObject $expectedApplications -DifferenceObject $applications).Count -gt 0) { throw 'The OpenIddict applications are not the three the setup creates.' }
    Write-LaneLog "OPENIDDICT APPS: $($applications -join ',')"
    Remove-Item Env:SQLCMDPASSWORD -ErrorAction SilentlyContinue
    Pop-Location
    $locationPushed = $false

    $env:CONFIGURATION_SERVICE_E2E_API_URL = 'http://localhost:8081'
    $env:TIMING_EVIDENCE_OUTPUT_DIR = $OutputDir
    $env:TIMING_EVIDENCE_RUN_LABEL = $RunLabel
    $env:TIMING_EVIDENCE_ROUNDS = "$Rounds"
    $env:TIMING_EVIDENCE_WARMUP_ROUNDS = "$WarmupRounds"
    $env:TIMING_EVIDENCE_SEED = "$Seed"
    $env:TIMING_EVIDENCE_COMMIT = $collectorCommit
    $env:TIMING_EVIDENCE_BACKEND = 'mssql'
    $env:TIMING_EVIDENCE_CONTRACT = $Contract
    $env:TIMING_EVIDENCE_TARGET_BUILD = "host-process Release build of $($target.commit) (git archive); frontend dll sha256 $($current['EdFi.DmsConfigurationService.Frontend.AspNetCore.dll'])"
    $env:TIMING_EVIDENCE_DECLARED_ITERATIONS = $launchSettings['IdentitySettings__ClientSecretHashingIterations']
    Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue
    & dotnet build-server shutdown *> $null
    $before = Get-ProcessSnapshot $cms $launchSettings
    $provenance.before = $before
    if ($before.alive -ne 'True') { throw 'The service process is not running.' }
    Write-LaneLog "BEFORE pid=$($before.pid) start=$($before.start_time) settings and five fingerprints verified"
    Open-LaneSampler ([ordered]@{ CmsProcessId = $cms.Id; CmsStartTicks = $cms.StartTime.ToUniversalTime().Ticks; LocalDbInstance = $Instance })
    Invoke-PowerCheck 'before_launch'
    Write-LaneLog 'RUN collector (Release, --no-build; output and results file in the work folder); the launch starts the attempt'
    $provenance.attempt_started = $true
    $collectorStarted = (Get-Date).ToUniversalTime()
    $launchQpc = [System.Diagnostics.Stopwatch]::GetTimestamp()
    try {
        & dotnet test $e2eProject -c Release --no-build --no-restore --nologo --filter "FullyQualifiedName~$(Get-CollectorFixtureName)" --logger "trx;LogFileName=$trxName" --results-directory $WorkRoot *> $runnerLog
        $collectorExit = $LASTEXITCODE
    }
    finally {
        # Whatever the collector did; each failure here is recorded beside any failure of the run.
        $exitQpc = [System.Diagnostics.Stopwatch]::GetTimestamp()
        try { Invoke-PowerCheck 'after_exit' } catch { Add-LaneFailure 'power-after-exit' $_ }
        try { Complete-LaneSampler $launchQpc $exitQpc } catch { Add-LaneFailure 'sampler-coverage' $_ }
    }
    $provenance.collector_exit = $collectorExit
    $summary = Assert-CollectorRun -ExitCode $collectorExit -TrxPath (Join-Path $WorkRoot $trxName) -OutputDir $OutputDir -RunLabel $RunLabel -Backend mssql -Rounds $Rounds -WarmupRounds $WarmupRounds -Seed $Seed -StartedUtc $collectorStarted
    $provenance.collector_run = $summary
    Write-LaneLog "RUN tests_passed=$($summary.tests_passed) samples=$($summary.samples_file) rows=$($summary.sample_rows)"
    $after = Get-ProcessSnapshot $cms $launchSettings
    $provenance.after = $after
    $differences = Compare-LaneSnapshot -Before $before -After $after -IdentityField pid, start_time, alive
    if ($after.alive -ne 'True') { $differences = @($differences) + 'the process is no longer running' }
    $provenance.deployment_differences = $differences
    if (@($differences).Count -gt 0) { throw "The service process changed during collection: $($differences -join '; ')." }
    Write-LaneLog 'AFTER the same live process and assemblies served the whole collection'
}
catch {
    Add-LaneFailure 'run' $_
}
finally {
    if ($shimInstalled) { Remove-Item -LiteralPath Function:\global:docker -ErrorAction SilentlyContinue }
    if ($locationPushed) { Pop-Location }
    if ($null -ne $sampler -and -not $samplerStopped) {
        try {
            $cleanup = Close-PowerSampler -Process $sampler -StopPath $samplerFiles.stop
            $provenance.sampler.cleanup_stop = $cleanup
            if (-not $cleanup.terminated) { throw "The power sampler (pid $($sampler.Id)) is still running after cleanup." }
            Write-LaneLog 'SAMPLER stopped during cleanup'
        }
        catch {
            Add-LaneFailure 'sampler-stop' $_
        }
    }
    if ($cms -and -not $cms.HasExited) {
        try {
            $cms.Kill($true)
            if (-not $cms.WaitForExit(30000)) { throw 'The service process did not stop within 30 seconds.' }
            Write-LaneLog "CMS STOPPED pid=$($cms.Id)"
        }
        catch {
            Add-LaneFailure 'stop' $_
        }
    }
    if ($copies) {
        try { [System.Threading.Tasks.Task]::WaitAll($copies, 10000) | Out-Null } catch { Write-LaneLog 'CMS output copy did not finish' }
    }
    foreach ($stream in $outputStreams) { $stream.Dispose() }
    Remove-Item Env:SQLCMDPASSWORD -ErrorAction SilentlyContinue
    $provenance.power_checks = $powerChecks.ToArray()
    $provenance.failures = $failures.ToArray()
    $provenance.succeeded = $failures.Count -eq 0
    $provenance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $provenancePath -Encoding utf8NoBOM
    Write-LaneLog "DONE succeeded=$($provenance.succeeded) provenance=$(Split-Path -Leaf $provenancePath)"
}
exit ([int]($failures.Count -gt 0))
