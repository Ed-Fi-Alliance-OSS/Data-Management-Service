# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param(
    [ValidateSet('self-contained', 'keycloak')]
    [string] $IdentityProvider = 'self-contained',
    [ValidateSet('postgresql', 'mssql')]
    [string] $DatabaseEngine = 'postgresql',
    [string] $EnvironmentFile = './.env.e2e',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $SkipDockerBuild,
    [switch] $UsePrebuiltOutput
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
$composeRoot = Join-Path $repoRoot 'eng/docker-compose'
$composeFile = Join-Path $PSScriptRoot 'docker-compose.yml'
$composeProject = 'dms-token-contract'
$derivedRoot = Join-Path $composeRoot '.derived/oauth-token-contract'
$lockPath = Join-Path $derivedRoot 'active.lock'
$envModule = Join-Path $composeRoot 'env-utility.psm1'
$schemaModule = Join-Path $composeRoot 'dms-schema-environment.psm1'
$primaryFailure = $null
$lockCreated = $false
$originalEnvironmentPath = $null
$resolvedEnvironmentPath = $null
$proxyLaunchAttempted = $false
$e2eAttempted = $false
$usableDmsWasStarted = $false

function Get-TokenContractOverlayLines {
    param(
        [ValidateSet('self-contained', 'keycloak')] [string] $Provider,
        [switch] $Qualified
    )

    $endpointKey = if ($Provider -eq 'keycloak') { 'KEYCLOAK_OAUTH_TOKEN_ENDPOINT' } else { 'SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT' }
    $lines = @("$endpointKey=http://dms-token-proxy:8080/token", 'CORRELATION_ID_HEADER=X-DMS-1606-Correlation')
    if ($Qualified) { $lines += 'ROUTE_QUALIFIER_SEGMENTS=schoolYear' }
    return $lines
}

function Get-TokenContractUpstreamUri {
    param(
        [ValidateSet('self-contained', 'keycloak')] [string] $Provider,
        [Parameter(Mandatory)] [hashtable] $EnvironmentValues
    )

    $endpointKey = if ($Provider -eq 'keycloak') { 'KEYCLOAK_OAUTH_TOKEN_ENDPOINT' } else { 'SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT' }
    $uriText = [string] $EnvironmentValues[$endpointKey]
    $parsedUri = $null
    if (-not [Uri]::TryCreate($uriText, [UriKind]::Absolute, [ref]$parsedUri) -or $parsedUri.Scheme -notin @('http', 'https')) {
        throw "The selected provider endpoint '$endpointKey' is missing or not an HTTP URI in the resolved environment."
    }
    return $uriText
}

function Get-TokenContractTestFilter {
    param(
        [ValidateSet('postgresql', 'mssql')] [string] $Engine,
        [switch] $Qualified
    )

    $engineExclusion = if ($Engine -eq 'mssql') { '(Category!=@PostgresqlOnly)' } else { '(Category!=@MssqlOnly)' }
    if ($Qualified) { return "(Category=@TokenContractQualified)&$engineExclusion" }
    return "(Category=@DMS-1606)&(Category!=@TokenContractQualified)&$engineExclusion"
}

function Get-TokenContractBuildArguments {
    param(
        [Parameter(Mandatory)] [string] $Provider,
        [Parameter(Mandatory)] [string] $Engine,
        [Parameter(Mandatory)] [string] $EnvironmentPath,
        [Parameter(Mandatory)] [string] $OverlayPath,
        [Parameter(Mandatory)] [string] $TestFilter,
        [Parameter(Mandatory)] [string] $BuildConfiguration,
        [switch] $SkipBuild,
        [switch] $PrebuiltOutput
    )

    return @{
        Command = 'E2ETest'
        Configuration = $BuildConfiguration
        IdentityProvider = $Provider
        DatabaseEngine = $Engine
        EnvironmentFile = $EnvironmentPath
        EnvironmentOverlayFile = $OverlayPath
        TestFilter = $TestFilter
        SkipDockerBuild = [bool]$SkipBuild
        UsePrebuiltOutput = [bool]$PrebuiltOutput
    }
}

function Invoke-WithTokenContractProxyEnvironment {
    param(
        [Parameter(Mandatory)] [scriptblock] $Action,
        [switch] $SkipBuild
    )

    $hadPreviousMarker = Test-Path Env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE
    $previousMarker = $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE
    $hadPreviousDmsImage = Test-Path Env:DMS_DOCKER_IMAGE
    $previousDmsImage = $env:DMS_DOCKER_IMAGE
    $hadPreviousAuthenticationService = Test-Path Env:AppSettings__AuthenticationService
    $previousAuthenticationService = $env:AppSettings__AuthenticationService
    try {
        $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE = '1'
        # Host-run setup/authentication uses the loopback port; DMS uses the Docker-network alias
        # from the endpoint overlay. Environment configuration overrides compiled appsettings.
        $env:AppSettings__AuthenticationService = 'http://127.0.0.1:18987/token'
        # A source build produces local/ed-fi-api. When reusing images, preserve the CI-selected
        # reference or Compose's ed-fi-api-local default instead of selecting an unbuilt tag.
        if (-not $SkipBuild) { $env:DMS_DOCKER_IMAGE = 'local/ed-fi-api' }
        & $Action
    }
    finally {
        if ($hadPreviousMarker) { $env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE = $previousMarker }
        else { Remove-Item Env:DMS_TOKEN_CONTRACT_PROXY_ACTIVE -ErrorAction SilentlyContinue }
        if ($hadPreviousDmsImage) { $env:DMS_DOCKER_IMAGE = $previousDmsImage }
        else { Remove-Item Env:DMS_DOCKER_IMAGE -ErrorAction SilentlyContinue }
        if ($hadPreviousAuthenticationService) { $env:AppSettings__AuthenticationService = $previousAuthenticationService }
        else { Remove-Item Env:AppSettings__AuthenticationService -ErrorAction SilentlyContinue }
    }
}

function Assert-TokenContractTrx {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string[]] $RequiredScenarioPrefixes
    )

    if (-not (Test-Path -LiteralPath $Path)) { throw "The $Name token contract run produced no TRX results at '$Path'." }
    [xml] $trx = Get-Content -LiteralPath $Path -Raw
    $total = [int] $trx.TestRun.ResultSummary.Counters.total
    $failed = [int] $trx.TestRun.ResultSummary.Counters.failed
    $notExecuted = [int] $trx.TestRun.ResultSummary.Counters.notExecuted
    if ($total -le 0) { throw "The $Name token contract run selected zero test cases." }
    if ($failed -gt 0 -or $notExecuted -gt 0) {
        throw "The $Name token contract run has failed ($failed) or not executed ($notExecuted) cases out of $total."
    }

    $testNames = @($trx.TestRun.Results.UnitTestResult | ForEach-Object { [regex]::Replace([string]$_.testName, '[^A-Za-z0-9]', '') })
    foreach ($prefix in $RequiredScenarioPrefixes) {
        $normalizedPrefix = [regex]::Replace($prefix, '[^A-Za-z0-9]', '')
        if (-not ($testNames | Where-Object { $_.StartsWith($normalizedPrefix, [StringComparison]::OrdinalIgnoreCase) })) {
            throw "The $Name token contract TRX did not contain required scenario '$prefix'."
        }
    }
    return [pscustomobject]@{ Total = $total; Failed = $failed; NotExecuted = $notExecuted }
}

function Assert-NativeCommandSucceeded {
    param(
        [Parameter(Mandatory)] [string] $Operation,
        [Parameter(Mandatory)] [int] $ExitCode
    )
    if ($ExitCode -ne 0) {
        throw "$Operation failed with exit code $ExitCode."
    }
}

function Build-TokenContractE2ETestAssembly {
    param(
        [Parameter(Mandatory)] [string] $ProjectPath,
        [Parameter(Mandatory)] [string] $BuildConfiguration,
        [switch] $PrebuiltOutput
    )

    if ($PrebuiltOutput) { return }

    dotnet build $ProjectPath --configuration $BuildConfiguration -v minimal
    Assert-NativeCommandSucceeded -Operation 'Building the DMS token contract E2E assembly' -ExitCode $LASTEXITCODE
}

function Stop-TokenContractDmsIfPresent {
    docker inspect ed-fi-api *> $null
    if ($LASTEXITCODE -ne 0) { return 'NotPresent' }
    docker stop ed-fi-api *> $null
    if ($LASTEXITCODE -ne 0) { return 'Failed' }
    return 'Stopped'
}

function Invoke-TokenContractPass {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Filter,
        [Parameter(Mandatory)] [string] $OverlayPath,
        [Parameter(Mandatory)] [string[]] $RequiredScenarioPrefixes
    )

    $resultsDirectory = Join-Path $repoRoot 'TestResults'
    New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
    $trxPath = Join-Path $resultsDirectory 'EdFi.DataManagementService.Tests.E2E.filtered.trx'
    Remove-Item -LiteralPath $trxPath -Force -ErrorAction SilentlyContinue
    $buildParameters = Get-TokenContractBuildArguments `
        -Provider $IdentityProvider `
        -Engine $DatabaseEngine `
        -EnvironmentPath $originalEnvironmentPath `
        -OverlayPath $OverlayPath `
        -TestFilter $Filter `
        -BuildConfiguration $Configuration `
        -SkipBuild:$SkipDockerBuild `
        -PrebuiltOutput:$UsePrebuiltOutput

    $passFailure = $null
    try {
        $script:e2eAttempted = $true
        $buildScriptPath = Join-Path $repoRoot 'build-dms.ps1'
        & $buildScriptPath @buildParameters
        Assert-NativeCommandSucceeded -Operation "The $Name token contract run" -ExitCode $LASTEXITCODE
    }
    catch {
        $passFailure = $_
    }

    if (Test-Path -LiteralPath $trxPath) {
        $script:usableDmsWasStarted = $true
        Copy-Item -LiteralPath $trxPath -Destination (Join-Path $repoRoot "TestResults/token-contract-$Name.trx") -Force
    }
    if ($null -ne $passFailure) { throw $passFailure }

    $retainedTrx = Join-Path $repoRoot "TestResults/token-contract-$Name.trx"
    $summary = Assert-TokenContractTrx -Path $retainedTrx -Name $Name -RequiredScenarioPrefixes $RequiredScenarioPrefixes
    Write-Host "Retained $Name token contract TRX with $($summary.Total) passed test case(s)."
}

try {
    New-Item -ItemType Directory -Path $derivedRoot -Force | Out-Null
    try {
        $lock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $lock.Dispose()
        $lockCreated = $true
    }
    catch [IO.IOException] {
        throw 'Another token contract launcher holds the shared DMS E2E stack lock. Wait for it to finish before retrying.'
    }

    Build-TokenContractE2ETestAssembly `
        -ProjectPath (Join-Path $repoRoot 'src/dms/tests/EdFi.DataManagementService.Tests.E2E/EdFi.DataManagementService.Tests.E2E.csproj') `
        -BuildConfiguration $Configuration `
        -PrebuiltOutput:$UsePrebuiltOutput

    Import-Module $envModule -Force
    $environmentCandidates = @(
        $EnvironmentFile,
        (Join-Path $repoRoot $EnvironmentFile),
        (Join-Path $composeRoot $EnvironmentFile),
        (Join-Path $composeRoot ([IO.Path]::GetFileName($EnvironmentFile)))
    ) | Select-Object -Unique
    $environmentCandidate = $environmentCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($null -eq $environmentCandidate) { throw "Environment file '$EnvironmentFile' could not be resolved." }
    $originalEnvironmentPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $environmentCandidate).Path)
    $dataStandardEnvironmentPath = Resolve-DataStandardEnvironmentFile -BaseEnvironmentFile $originalEnvironmentPath -DockerComposeRoot $composeRoot
    $resolvedEnvironmentPath = Resolve-DatabaseEngineEnvironmentFile -DatabaseEngine $DatabaseEngine -BaseEnvironmentFile $dataStandardEnvironmentPath -DockerComposeRoot $composeRoot
    $environmentValues = ReadValuesFromEnvFile $resolvedEnvironmentPath
    $realUpstreamUri = Get-TokenContractUpstreamUri -Provider $IdentityProvider -EnvironmentValues $environmentValues

    $ownedRoot = Join-Path $derivedRoot ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $ownedRoot -Force | Out-Null
    $proxyComposeEnvironment = Join-Path $ownedRoot 'proxy.env'
    [IO.File]::WriteAllText($proxyComposeEnvironment, "TOKEN_PROXY_UPSTREAM_URI=$realUpstreamUri`n", [Text.UTF8Encoding]::new($false))

    docker network inspect dms *> $null
    if ($LASTEXITCODE -ne 0) {
        docker network create dms | Out-Null
        Assert-NativeCommandSucceeded -Operation 'Creating the external DMS network' -ExitCode $LASTEXITCODE
    }

    docker inspect dms-token-contract-proxy *> $null
    if ($LASTEXITCODE -eq 0) {
        throw 'A dms-token-contract-proxy container already exists; inspect and remove that container before retrying.'
    }

    $proxyLaunchAttempted = $true
    docker compose --project-name $composeProject --file $composeFile --env-file $proxyComposeEnvironment up --detach --build token-proxy
    Assert-NativeCommandSucceeded -Operation 'Building and starting the closed token proxy' -ExitCode $LASTEXITCODE
    $ready = $false
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    while ([DateTimeOffset]::UtcNow -lt $deadline -and -not $ready) {
        try {
            $health = Invoke-RestMethod -Uri 'http://127.0.0.1:18987/health' -TimeoutSec 2
            $ready = $health.status -eq 'ready'
        }
        catch { Start-Sleep -Seconds 1 }
    }
    if (-not $ready) { throw 'The token proxy did not become ready at http://127.0.0.1:18987/health within 90 seconds.' }

    $rootOverlay = Join-Path $ownedRoot 'root.overlay.env'
    $qualifiedOverlay = Join-Path $ownedRoot 'qualified.overlay.env'
    [IO.File]::WriteAllLines($rootOverlay, (Get-TokenContractOverlayLines -Provider $IdentityProvider), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllLines($qualifiedOverlay, (Get-TokenContractOverlayLines -Provider $IdentityProvider -Qualified), [Text.UTF8Encoding]::new($false))

    Invoke-WithTokenContractProxyEnvironment -SkipBuild:$SkipDockerBuild -Action {
        $passFailures = [Collections.Generic.List[string]]::new()
        $passes = @(
            @{
                Name = 'root'
                Filter = Get-TokenContractTestFilter -Engine $DatabaseEngine
                OverlayPath = $rootOverlay
                RequiredScenarioPrefixes = @(
                    '33 A present unsupported grant type',
                    '18 A JSON provider 401', '19 An HTML provider 401', '20 A provider invalid_scope',
                    '21 A provider invalid_client response after Basic', '22 A provider invalid_client response after body',
                    '23 An unknown provider 400', '24 A canonical token limit', '25 A generic provider 429',
                    '26 A provider conflict', '27 A provider server error', '28 A provider connection abort'
                )
            },
            @{
                Name = 'qualified'
                Filter = Get-TokenContractTestFilter -Engine $DatabaseEngine -Qualified
                OverlayPath = $qualifiedOverlay
                RequiredScenarioPrefixes = @(
                    '29 Root token route', '30 Qualified token route returns', '31 Qualified token route issues',
                    '32 Root and qualified error routes'
                )
            }
        )
        foreach ($pass in $passes) {
            try {
                Invoke-TokenContractPass -Name $pass.Name -Filter $pass.Filter -OverlayPath $pass.OverlayPath -RequiredScenarioPrefixes $pass.RequiredScenarioPrefixes
            }
            catch {
                if (-not $script:usableDmsWasStarted) { throw }
                $passFailures.Add("$($pass.Name) pass: $($_.Exception.Message)")
            }
        }
        if ($passFailures.Count -gt 0) {
            throw "Token contract E2E failures: $($passFailures -join ' | ')"
        }
    }
}
catch {
    $primaryFailure = $_
}
finally {
    $cleanupFailures = [Collections.Generic.List[string]]::new()
    if ($proxyLaunchAttempted) {
        $proxyStatus = docker inspect --format '{{.State.Status}} {{.State.ExitCode}}' dms-token-contract-proxy 2>$null
        if ($LASTEXITCODE -eq 0) { Write-Host "Sanitized token proxy diagnostic: $proxyStatus" }
        docker compose --project-name $composeProject --file $composeFile --env-file (Join-Path $ownedRoot 'proxy.env') down --remove-orphans --rmi local
        if ($LASTEXITCODE -ne 0) { $cleanupFailures.Add("Proxy cleanup failed with exit code $LASTEXITCODE.") }
    }
    # Invoke-WithTokenContractProxyEnvironment restores the marker before this cleanup path runs.

    if ($e2eAttempted -and $usableDmsWasStarted -and $null -ne $originalEnvironmentPath -and (Test-Path -LiteralPath $originalEnvironmentPath)) {
        try {
            Import-Module $schemaModule -Force
            Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
                & (Join-Path $composeRoot 'start-local-dms.ps1') -DmsOnly -EnableConfig -EnvironmentFile $originalEnvironmentPath -IdentityProvider $IdentityProvider -DatabaseEngine $DatabaseEngine -AddExtensionSecurityMetadata
            }
            Assert-NativeCommandSucceeded -Operation 'DMS restoration' -ExitCode $LASTEXITCODE
            $dmsPort = if ([string]::IsNullOrWhiteSpace([string]$environmentValues.DMS_HTTP_PORTS)) { '8080' } else { [string]$environmentValues.DMS_HTTP_PORTS }
            $dmsReady = $false
            $restoreDeadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
            while ([DateTimeOffset]::UtcNow -lt $restoreDeadline -and -not $dmsReady) {
                try {
                    $null = Invoke-WebRequest -Uri "http://127.0.0.1:$dmsPort/health" -TimeoutSec 2
                    $dmsReady = $true
                }
                catch { Start-Sleep -Seconds 1 }
            }
            if (-not $dmsReady) { throw 'DMS did not become healthy on the original environment after restoration.' }
            Write-Host 'DMS restoration completed and its health endpoint is ready.'
        }
        catch {
            $stopResult = Stop-TokenContractDmsIfPresent
            $cleanupFailures.Add("DMS restoration failed: $($_.Exception.GetType().Name); fixture-dependent container stop result: $stopResult.")
        }
    }
    elseif ($e2eAttempted) {
        $stopResult = Stop-TokenContractDmsIfPresent
        $cleanupFailures.Add("No usable provisioned DMS test run was observed; fixture-dependent container stop result: $stopResult.")
    }

    if ($null -ne $ownedRoot -and (Test-Path -LiteralPath $ownedRoot)) {
        Remove-Item -LiteralPath $ownedRoot -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $ownedRoot) { $cleanupFailures.Add('Owned token contract derived files could not be removed.') }
    }
    if ($lockCreated) {
        Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $lockPath) { $cleanupFailures.Add('The token contract launcher lock could not be removed.') }
    }
    if ($cleanupFailures.Count -gt 0) {
        $cleanupFailureText = $cleanupFailures -join ' '
        if ($null -eq $primaryFailure) { $primaryFailure = [InvalidOperationException]::new($cleanupFailureText) }
        else { Write-Error "Primary token contract failure: $($primaryFailure.Exception.Message) Cleanup issue: $cleanupFailureText" -ErrorAction Continue }
    }
}

if ($null -ne $primaryFailure) {
    Write-Error $primaryFailure -ErrorAction Continue
    exit 1
}
