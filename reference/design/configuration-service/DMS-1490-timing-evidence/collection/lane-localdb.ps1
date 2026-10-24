# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    One SQL Server endpoint timing run on LocalDB, with the Configuration Service as a host process.
.DESCRIPTION
    For hosts that cannot run the SQL Server container (an ARM64 Windows host cannot). Steps:
    1. Refuses to start unless the collector worktree is clean, port 8081 is free and no cs-local
       container exists, and unless the prepared target's Release assemblies still match the
       fingerprints prepare-target.ps1 recorded.
    2. Starts the LocalDB instance, enables the sa login over its named pipe, and recreates the
       database with its files in -WorkRoot (the LocalDB default data folder can hold same-named
       files of other instances, which CREATE DATABASE refuses to reuse).
    3. Starts the target's Configuration Service as a host process with an explicit environment
       block configured as local-config.yml configures the container from
       .env.config.mssql.e2e, inserts the OpenIddict signing key and runs the target's
       setup-openiddict.ps1 -InsertData with `docker exec ... sqlcmd` routed to the host's sqlcmd.
    4. Records nonsecret provenance before and after collection: process id and start time, an
       exact allowlist of settings from the launch environment block, and the SHA-256 of each
       production assembly the process loads from its output folder.
    5. Runs the explicit endpoint timing collector once, from the collector worktree (Release,
       --no-build), then stops the process.

    Written to -OutputDir (evidence): the samples CSV and header JSON, provenance-*.json and
    lane-localdb-*.log. Written to -WorkRoot (never evidence): database files, the service's own
    output, setup logs and the full text of any exception. The committed log names an exception's
    type only. Secrets come from the target's eng/docker-compose/.env.config.mssql.e2e and are
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
    [string] $Instance = 'DMS1490',
    [ValidateSet('current', 'pre-fix')] [string] $Contract = 'current'
)

$ErrorActionPreference = 'Stop'

$environmentFile = './.env.config.mssql.e2e'
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
# The secret inputs, by the env-file key each comes from. Values are never written anywhere.
$secretSources = @(
    'MSSQL_SA_PASSWORD',
    'CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY',
    'CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY',
    'CONFIGURATION_SERVICE_IDENTITY_CLIENT_SECRET'
)

New-Item -ItemType Directory -Force -Path $OutputDir, $WorkRoot | Out-Null
$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")
$log = Join-Path $OutputDir "lane-localdb-$RunLabel-$stamp.log"
$provenancePath = Join-Path $OutputDir "provenance-endpoint-mssql-$RunLabel-$stamp.json"
$diagnostics = Join-Path $WorkRoot "lane-localdb-$RunLabel-$stamp.diagnostics.txt"
$composeDirectory = Join-Path $TargetRoot 'eng\docker-compose'
$cmsBin = Join-Path $TargetRoot 'src\config\frontend\EdFi.DmsConfigurationService.Frontend.AspNetCore\bin\Release\net10.0'
$cmsDll = Join-Path $cmsBin 'EdFi.DmsConfigurationService.Frontend.AspNetCore.dll'
$e2eProject = Join-Path $CollectorRoot 'src\config\tests\EdFi.DmsConfigurationService.Tests.E2E\EdFi.DmsConfigurationService.Tests.E2E.csproj'

function Write-LaneLog([string] $Message) {
    "[$((Get-Date).ToUniversalTime().ToString('o'))] $Message" | Out-File -LiteralPath $log -Append -Encoding utf8NoBOM
}

function Get-AssemblyFingerprint {
    $fingerprints = [ordered]@{}
    foreach ($assembly in $fingerprintedAssemblies) {
        $fingerprints[$assembly] = (Get-FileHash -LiteralPath (Join-Path $cmsBin $assembly) -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $fingerprints
}

function Get-ProcessSnapshot([System.Diagnostics.Process] $Process) {
    $live = Get-Process -Id $Process.Id -ErrorAction SilentlyContinue
    [ordered]@{
        utc        = (Get-Date).ToUniversalTime().ToString('o')
        pid        = $Process.Id
        has_exited = $Process.HasExited
        alive      = $null -ne $live
        start_time = if ($live) { $live.StartTime.ToUniversalTime().ToString('o') } else { $null }
        assemblies = Get-AssemblyFingerprint
    }
}

"" | Out-File -LiteralPath $log -Encoding utf8NoBOM
$provenance = [ordered]@{
    schema                   = 'timing-provenance/2'
    lane                     = 'endpoint-mssql (LocalDB, host-process Configuration Service)'
    lane_script              = Split-Path -Leaf $PSCommandPath
    lane_script_sha256       = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
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
    $collectorCommit = (git -C $CollectorRoot rev-parse HEAD).Trim()
    $collectorClean = [string]::IsNullOrEmpty((git -C $CollectorRoot status --porcelain))
    $provenance.collector = [ordered]@{ commit = $collectorCommit; worktree_clean = $collectorClean }
    if (-not $collectorClean) { throw 'The collector worktree is not clean.' }

    $target = Get-Content -LiteralPath (Join-Path $TargetRoot 'target.json') -Raw | ConvertFrom-Json
    $provenance.target = $target
    if ($null -eq $target.host_release) { throw 'The target was prepared without -BuildHostRelease.' }
    $current = Get-AssemblyFingerprint
    foreach ($assembly in $fingerprintedAssemblies) {
        if ($current[$assembly] -ne $target.host_release.fingerprints.$assembly) { throw "$assembly no longer matches the prepared target." }
    }
    Write-LaneLog "LANE label=$RunLabel rounds=$Rounds warmup=$WarmupRounds seed=$Seed contract=$Contract collector=$collectorCommit target=$($target.commit)"

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

    # LocalDB: create if missing, start, and read the pipe in this same process (it changes on restart).
    if (-not ((sqllocaldb info) -contains $Instance)) { sqllocaldb create $Instance | Out-Null }
    sqllocaldb start $Instance | Out-Null
    $pipe = ((sqllocaldb info $Instance | Select-String 'Instance pipe name:').ToString() -split ':', 2)[1].Trim()
    $version = ((sqllocaldb info $Instance | Select-String '^Version:').ToString() -split ':', 2)[1].Trim()
    $provenance.localdb = [ordered]@{ instance = $Instance; version = $version; database = $database }
    Write-LaneLog "LOCALDB $Instance version=$version"

    $saFile = Join-Path $WorkRoot ('sa-' + [guid]::NewGuid().ToString('N') + '.sql')
    try {
        Set-Content -LiteralPath $saFile -Value "ALTER LOGIN sa WITH PASSWORD = N'$($saPassword.Replace("'", "''"))', CHECK_POLICY = OFF; ALTER LOGIN sa ENABLE;" -Encoding utf8NoBOM
        & sqlcmd -S $pipe -E -b -i $saFile | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Enabling the sa login failed.' }
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
            if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed ($LASTEXITCODE)." }
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
    # what is recorded is what the process was started with.
    $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.ArgumentList.Add($cmsDll)
    $start.WorkingDirectory = $cmsBin
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($name in $launch.Keys) { $start.Environment[$name] = [string]$launch[$name] }
    $configurationPattern = '^(AppSettings|IdentitySettings|DatabaseSettings|ClaimsOptions|ConnectionStrings)__'
    $provenance.inherited_configuration_variable_names = @($start.Environment.Keys | Where-Object { $_ -match $configurationPattern -and -not $launch.Contains($_) } | Sort-Object)
    $launchSettings = [ordered]@{}
    foreach ($name in $settingsAllowlist) {
        $launchSettings[$name] = [ordered]@{ value = $start.Environment[$name]; source = 'explicit launch environment of the service process (ProcessStartInfo.Environment)' }
    }
    $provenance.launch_settings = $launchSettings

    $cms = [System.Diagnostics.Process]::Start($start)
    $outputStreams = @(
        [System.IO.File]::Create((Join-Path $WorkRoot "cms-$RunLabel-$stamp.out.log")),
        [System.IO.File]::Create((Join-Path $WorkRoot "cms-$RunLabel-$stamp.err.log"))
    )
    $copies = @(
        $cms.StandardOutput.BaseStream.CopyToAsync($outputStreams[0]),
        $cms.StandardError.BaseStream.CopyToAsync($outputStreams[1])
    )
    Write-LaneLog "CMS STARTED pid=$($cms.Id) inheritedConfigurationVariables=$($provenance.inherited_configuration_variable_names.Count)"
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
        Write-LaneLog "KEY INSERT rows=$($command.ExecuteNonQuery())"
    }
    finally {
        $connection.Dispose()
    }

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
    $applications = & $runSql 'SET NOCOUNT ON; SELECT ClientId FROM dmscs.OpenIddictApplication ORDER BY ClientId;' $database
    Write-LaneLog "OPENIDDICT APPS: $(($applications | Where-Object { $_.Trim() }) -join ',')"
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
    $env:TIMING_EVIDENCE_DECLARED_ITERATIONS = $launch['IdentitySettings__ClientSecretHashingIterations']
    Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue
    & dotnet build-server shutdown *> $null
    $before = Get-ProcessSnapshot $cms
    $provenance.before = $before
    Write-LaneLog "BEFORE pid=$($before.pid) start=$($before.start_time) alive=$($before.alive)"
    Write-LaneLog 'RUN collector (Release, --no-build)'
    & dotnet test $e2eProject -c Release --no-build --no-restore --nologo --filter 'FullyQualifiedName~Given_token_endpoint_requests_measured_for_timing_evidence' --logger 'console;verbosity=normal' *>> $log
    $provenance.collector_exit = $LASTEXITCODE
    Write-LaneLog "RUN EXIT $LASTEXITCODE"
    $after = Get-ProcessSnapshot $cms
    $provenance.after = $after
    $same = $after.alive -and -not $after.has_exited -and $after.pid -eq $before.pid -and $after.start_time -eq $before.start_time
    foreach ($assembly in $fingerprintedAssemblies) { $same = $same -and ($after.assemblies[$assembly] -eq $before.assemblies[$assembly]) }
    $provenance.same_process_throughout = $same
    Write-LaneLog "AFTER pid=$($after.pid) alive=$($after.alive) sameThroughout=$same"
}
catch {
    $provenance.lane_error_type = $_.Exception.GetType().FullName
    $_ | Out-String | Out-File -LiteralPath $diagnostics -Encoding utf8NoBOM
    Write-LaneLog "LANE ERROR $($provenance.lane_error_type) (full text in the work folder)"
}
finally {
    if ($shimInstalled) { Remove-Item -LiteralPath Function:\global:docker -ErrorAction SilentlyContinue }
    if ($locationPushed) { Pop-Location }
    if ($cms -and -not $cms.HasExited) { $cms.Kill($true); $cms.WaitForExit(); Write-LaneLog "CMS STOPPED pid=$($cms.Id)" }
    if ($copies) { try { [System.Threading.Tasks.Task]::WaitAll($copies, 10000) | Out-Null } catch { Write-LaneLog 'CMS output copy did not finish' } }
    foreach ($stream in $outputStreams) { $stream.Dispose() }
    Remove-Item Env:SQLCMDPASSWORD -ErrorAction SilentlyContinue
    $provenance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $provenancePath -Encoding utf8NoBOM
    Write-LaneLog "PROVENANCE $(Split-Path -Leaf $provenancePath)"
    Write-LaneLog 'DONE'
}
