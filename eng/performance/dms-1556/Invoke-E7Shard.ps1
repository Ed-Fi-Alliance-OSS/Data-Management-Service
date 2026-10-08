# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
DMS-1556 step 0.5 E7 (overlay variant): runs one DMS E2E shard directly against the
ALREADY PROVISIONED dms-local stack while the diagnostics samplers record the whole run,
then summarizes the CMS side of the real workload.

.DESCRIPTION
build-dms.ps1 E2ETest tears the stack down and recomposes it from its own compose set,
so the diagnostics and resource-profile overlays cannot be applied through it. This
script is the overlay variant: after an E2ETest run has provisioned the stack, the CMS
and PostgreSQL services are recomposed with the overlays (Set-Dms1556StackCondition.ps1)
and the shard is re-run here with the same test-process context build-dms.ps1 builds
(Get-E2ETestEnvironmentContext / Invoke-WithE2EIdentityEnvironment /
Invoke-WithE2ETestProcessContext, reproduced from the same module functions), the same
already-built assembly (--no-build), and the same filter normalization.

Afterwards it summarizes, for the test window: CMS 'Request finished' lines for
/v3/profiles/{id} per second (bursts, statuses, server-side elapsed), CMS
warning/error lines by scope, PostgreSQL connection handshakes from the CMS container
(received -> authorized), PostgreSQL errors, and livemetrics thread-pool/pool maxima.
Writes e7b-<label>-summary.json. Observational only: the real workload's burst shape is
whatever the shard produces.
#>
[CmdletBinding()]
param(
    [string] $TestFilter = 'Category=e2e-ci-shard-2',

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateRange(60, 3600)]
    [int] $SamplerDurationSeconds = 3600,

    [string] $Label = 'e7b-shard2',

    [string] $CmsContainerName = 'ed-fi-api-config-service',

    [string] $PgContainerName = 'dms-postgresql',

    [string] $CmsDatabase = 'edfi_datamanagementservice',

    [string] $MonitorBaseUrl = 'http://localhost:52323',

    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$composeRoot = Join-Path $repositoryRoot 'eng/docker-compose'
Import-Module (Join-Path $PSScriptRoot 'dms-1556-analysis.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'dms-1556-samplers.psm1') -Force
Import-Module (Join-Path $composeRoot 'env-utility.psm1') -Force
Import-Module (Join-Path $repositoryRoot 'eng/Dms-Management.psm1') -Force
Import-Module (Join-Path $composeRoot 'database-safety.psm1') -Force
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory

$testAssembly = Join-Path $repositoryRoot "src/dms/tests/EdFi.DataManagementService.Tests.E2E/bin/$Configuration/net10.0/EdFi.DataManagementService.Tests.E2E.dll"
if (-not (Test-Path -LiteralPath $testAssembly)) {
    throw "E2E test assembly not found at $testAssembly; build $Configuration first (an absent assembly would run zero tests and exit 0)."
}

# Same resolution as build-dms.ps1 Get-E2ETestEnvironmentContext for PostgreSQL with no
# feature, data-standard, or engine overlay (those steps return the input unchanged).
$environmentFile = (Resolve-Path (Join-Path $composeRoot '.env.e2e')).Path
$environmentValues = ReadValuesFromEnvFile $environmentFile
$databaseName = Get-ComposeResolvedEnvValue -EnvironmentValues $environmentValues -Name 'E2E_DATABASE_NAME'
$snapshotDatabaseName = Get-ComposeResolvedEnvValue -EnvironmentValues $environmentValues -Name 'E2E_SNAPSHOT_DATABASE_NAME'
$connectionStrings = New-E2EDataStoreConnectionStrings -DatabaseEngine 'postgresql' -EnvironmentValues $environmentValues -DatabaseName $databaseName
$snapshotConnectionStrings = New-E2EDataStoreConnectionStrings -DatabaseEngine 'postgresql' -EnvironmentValues $environmentValues -DatabaseName $snapshotDatabaseName
$dmsContainerName = (Get-E2EStartupPhasePlan -DatabaseEngine 'postgresql').DmsContainerName
$normalizedFilter = $TestFilter -replace '(Test)?Category\s*(!?[=~])\s*@', '$1Category$2'

$processEnvironment = [ordered]@{
    # Invoke-WithE2EIdentityEnvironment (self-contained)
    DMS_CONFIG_IDENTITY_PROVIDER                  = 'self-contained'
    OAUTH_TOKEN_ENDPOINT                          = $environmentValues['SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT']
    DMS_JWT_AUTHORITY                             = $environmentValues['SELF_CONTAINED_DMS_JWT_AUTHORITY']
    DMS_JWT_METADATA_ADDRESS                      = $environmentValues['SELF_CONTAINED_DMS_JWT_METADATA_ADDRESS']
    DMS_CONFIG_IDENTITY_AUTHORITY                 = $environmentValues['SELF_CONTAINED_DMS_JWT_AUTHORITY']
    # Invoke-WithE2ETestProcessContext
    AppSettings__DataStoreDatabaseName            = $databaseName
    AppSettings__DmsContainerName                 = $dmsContainerName
    AppSettings__DatabaseEngine                   = 'postgresql'
    AppSettings__DataStoreAdminConnectionString   = $connectionStrings.AdminConnectionString
    AppSettings__DataStoreConnectionString        = $connectionStrings.RegistrationConnectionString
    AppSettings__DataStoreSnapshotConnectionString = $snapshotConnectionStrings.RegistrationConnectionString
    DMS_E2E_ENVIRONMENT_FILE                      = $environmentFile
}
$previous = @{}
foreach ($name in $processEnvironment.Keys) {
    $previous[$name] = @{ Exists = Test-Path -LiteralPath "Env:$name"; Value = [Environment]::GetEnvironmentVariable($name) }
}
$previousNodeOptions = $env:NODE_OPTIONS

$runId = '{0}-{1}' -f $Label, [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$trxPath = Join-Path $OutputDirectory "$runId.trx"
$testLogPath = Join-Path $OutputDirectory "$runId-dotnet-test.log"
$samplerState = $null
$samplerReport = $null
$testExitCode = $null
$logStartUtc = [DateTime]::UtcNow.AddSeconds(-5)
try {
    $samplerState = Start-DmsSamplerSet -Label $runId -OutputDirectory $OutputDirectory -DurationSeconds $SamplerDurationSeconds `
        -MonitorBaseUrl $MonitorBaseUrl -RequireMonitor -StatsContainers @($CmsContainerName, $PgContainerName, $dmsContainerName)
    Wait-DmsSamplerSetReady -State $samplerState
    Write-Output "Samplers ready; running shard ($normalizedFilter)."

    foreach ($name in $processEnvironment.Keys) { Set-Item -LiteralPath "Env:$name" -Value $processEnvironment[$name] }
    Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue
    $testStartUtc = [DateTime]::UtcNow
    & dotnet test $testAssembly --no-build --no-restore -v normal --logger "trx;LogFileName=$trxPath" --logger console --nologo --filter $normalizedFilter *>&1 |
        Tee-Object -FilePath $testLogPath | Select-String -Pattern '^\s*(Passed!|Failed!|Skipped!|Total tests|Passed|Failed|Skipped)\s*[:!]' | ForEach-Object { $_.Line }
    $testExitCode = $LASTEXITCODE
    $testEndUtc = [DateTime]::UtcNow
    Start-Sleep -Seconds 5
    $samplerReport = Stop-DmsSamplerSet -State $samplerState -SkipWindowWait -BurstStartUtc $testStartUtc -BurstEndUtc $testEndUtc
}
finally {
    foreach ($name in $processEnvironment.Keys) {
        if ($previous[$name].Exists) { Set-Item -LiteralPath "Env:$name" -Value $previous[$name].Value }
        else { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
    }
    if ($null -ne $previousNodeOptions) { $env:NODE_OPTIONS = $previousNodeOptions }
    if ($null -ne $samplerState) { Remove-DmsSamplerSet -State $samplerState }
}

$sinceIso = $logStartUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
$cmsLogPath = Join-Path $OutputDirectory "$runId-cms.log"
$pgLogPath = Join-Path $OutputDirectory "$runId-pg.log"
docker logs --since $sinceIso $CmsContainerName 2>&1 | Set-Content -LiteralPath $cmsLogPath -Encoding utf8
docker logs --since $sinceIso $PgContainerName 2>&1 | Set-Content -LiteralPath $pgLogPath -Encoding utf8
$cmsHost = Get-DmsContainerIpAddress -ContainerName $CmsContainerName

function Get-StatusText {
    [CmdletBinding()]
    [OutputType([string])]
    param([object[]] $Values)

    $counts = [ordered]@{}
    foreach ($group in (@($Values) | Group-Object -Property status | Sort-Object Name)) { $counts[$group.Name] = $group.Count }
    return ConvertTo-DmsFlatText $counts
}

# Profile requests per second from CMS 'Request finished' lines (server-side view).
$profileSeconds = @{}
foreach ($line in [System.IO.File]::ReadLines($cmsLogPath)) {
    if (-not $line.Contains('"MessageTemplate":"Request finished') -or $line -notmatch '/v3/profiles/\d+') { continue }
    if ($line -notmatch '^\{"Timestamp":"(?<ts>[^"]+)"') { continue }
    $utc = ConvertTo-DmsUtc -Text $Matches.ts
    if ($null -eq $utc) { continue }
    $status = if ($line -match '"StatusCode":(?<s>\d+)') { $Matches.s } else { '?' }
    $elapsed = if ($line -match '"ElapsedMilliseconds":(?<e>[0-9.]+)') { [double]::Parse($Matches.e, [System.Globalization.CultureInfo]::InvariantCulture) } else { 0 }
    $second = [DateTime]::new($utc.Ticks - ($utc.Ticks % [TimeSpan]::TicksPerSecond), [DateTimeKind]::Utc)
    if (-not $profileSeconds.ContainsKey($second)) { $profileSeconds[$second] = [System.Collections.Generic.List[object]]::new() }
    $profileSeconds[$second].Add([pscustomobject]@{ status = $status; elapsedMs = $elapsed })
}
$bursts = @($profileSeconds.GetEnumerator() | Where-Object { $_.Value.Count -ge 10 } | Sort-Object Key | ForEach-Object {
        $values = $_.Value
        [pscustomobject]@{
            secondUtc    = $_.Key.ToString('o')
            completions  = $values.Count
            maxElapsedMs = [Math]::Round(($values | Measure-Object -Property elapsedMs -Maximum).Maximum, 1)
            statuses     = Get-StatusText -Values $values
        }
    })
$allProfile = @($profileSeconds.Values | ForEach-Object { $_ })
$window = if ($samplerReport) { @{ Start = $testStartUtc; End = $testEndUtc } } else { @{ Start = $logStartUtc; End = [DateTime]::UtcNow } }
$pgEvents = Get-DmsPgLogEvent -Path $pgLogPath
$handshake = Measure-DmsPgHandshakeWindow -Events $pgEvents -StartUtc $window.Start -EndUtc $window.End -ClientHost $cmsHost
$slowHandshakes = @($pgEvents | Where-Object { $_.kind -eq 'received' -and $_.host -eq $cmsHost -and $_.tsUtc -ge $window.Start -and $_.tsUtc -le $window.End })
$authorizedByPid = @{}
foreach ($pgEvent in $pgEvents) { if ($pgEvent.kind -eq 'authorized' -and -not $authorizedByPid.ContainsKey($pgEvent.pid)) { $authorizedByPid[$pgEvent.pid] = $pgEvent.tsUtc } }
$gaps = @($slowHandshakes | Where-Object { $authorizedByPid.ContainsKey($_.pid) } | ForEach-Object { ($authorizedByPid[$_.pid] - $_.tsUtc).TotalMilliseconds })
$connections = Measure-DmsPgConnectionWindow -Events $pgEvents -StartUtc $window.Start -EndUtc $window.End -Database $CmsDatabase -ClientHost $cmsHost
$livemetrics = if ($samplerReport) {
    Get-DmsLivemetricsWindowSummary -Path ([string]$samplerReport.files.livemetrics) -StartUtc $window.Start -EndUtc $window.End -PoolDatabase $CmsDatabase
}
else { $null }
$cmsLog = Get-DmsCmsLogWindowSummary -Path $cmsLogPath -StartUtc $window.Start -EndUtc $window.End

$trxCounters = $null
if (Test-Path -LiteralPath $trxPath) {
    $trx = [xml](Get-Content -LiteralPath $trxPath -Raw)
    $counters = $trx.TestRun.ResultSummary.Counters
    $trxCounters = [pscustomobject]@{ total = [int]$counters.total; executed = [int]$counters.executed; passed = [int]$counters.passed; failed = [int]$counters.failed; notExecuted = [int]$counters.notExecuted }
}

$summary = [pscustomobject]@{
    runId                    = $runId
    testFilter               = $normalizedFilter
    testAssembly             = $testAssembly
    testExitCode             = $testExitCode
    trxCounters              = $trxCounters
    testWindow               = [pscustomobject]@{ startUtc = $window.Start.ToString('o'); endUtc = $window.End.ToString('o') }
    stack                    = [pscustomobject]@{
        cms      = Get-DmsContainerResourceRecord -ContainerName $CmsContainerName
        postgres = Get-DmsContainerResourceRecord -ContainerName $PgContainerName
    }
    samplerRequiredFailures  = @(if ($samplerReport) { $samplerReport.requiredFailures } else { 'no sampler report' })
    profileRequests          = $allProfile.Count
    profileStatuses          = Get-StatusText -Values $allProfile
    profileMaxElapsedMs      = if ($allProfile.Count -gt 0) { [Math]::Round(($allProfile | Measure-Object -Property elapsedMs -Maximum).Maximum, 1) } else { $null }
    profileBurstSeconds      = $bursts
    cmsConnectionsCreated    = $connections.cmsConnectionsCreated
    pgErrors                 = $connections.pgErrors
    handshake                = $handshake
    handshakesOver1s         = @($gaps | Where-Object { $_ -gt 1000 }).Count
    handshakesOver5s         = @($gaps | Where-Object { $_ -gt 5000 }).Count
    livemetrics              = $livemetrics
    cmsLog                   = $cmsLog
}
$summaryPath = Join-Path $OutputDirectory "$runId-summary.json"
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8
Write-Output "E7 summary written to $summaryPath (test exit code $testExitCode)"
