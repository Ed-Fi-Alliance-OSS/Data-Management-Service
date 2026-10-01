# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Runs build-config.ps1 E2ETest against an isolated Configuration Service stack.

.DESCRIPTION
    The stock E2E stack uses the cs-local compose project, fixed container names, the dms network
    and host ports 8081/5435/8045, so it cannot run while another local stack holds them. This derives
    an environment file from the given one that moves the stack onto its own project, network,
    container names, host ports and image tag (cms-e2e-isolation.yml carries the renames), runs the
    unchanged build-config.ps1 E2ETest against it, and removes the isolated stack afterwards unless
    -KeepStack is given. Nothing about the stock stack, or any stack another launcher owns, is touched.

    Only the self-contained identity provider is supported: the isolation moves the self-contained
    token endpoint and authority onto the isolated Configuration Service, but not Keycloak's.

.EXAMPLE
    pwsh eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [string]
    $EnvironmentFile = "./.env.config.e2e",

    [ValidateSet("Debug", "Release")]
    [string]
    $Configuration = "Debug",

    # Self-contained only: the isolation moves the self-contained identity URLs onto the isolated
    # Configuration Service, and does not move Keycloak's.
    [ValidateSet("self-contained")]
    [string]
    $IdentityProvider = "self-contained",

    [string]
    $E2ETestFilter = "",

    [switch]
    $SkipDockerBuild,

    [switch]
    $KeepStack,

    # Builds the test assembly and runs the target preflight, then stops before any stack or suite run.
    [switch]
    $PreflightOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$composeDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $composeDirectory "../.."))

$sourceEnvironmentFile = if ([System.IO.Path]::IsPathRooted($EnvironmentFile)) {
    $EnvironmentFile
}
else {
    Join-Path $composeDirectory $EnvironmentFile
}
$sourceEnvironmentFile = [System.IO.Path]::GetFullPath($sourceEnvironmentFile)

# The isolated identity: none of it may equal a name or port the stock stack uses.
$isolation = [ordered]@{
    CMS_COMPOSE_PROJECT = "cms-e2e-isolated"
    CMS_COMPOSE_NETWORK = "cms-e2e-isolated"
    CMS_COMPOSE_OVERLAY_FILES = "tests/cms-isolation/cms-e2e-isolation.yml"
    CMS_POSTGRES_CONTAINER = "cms-e2e-isolated-db"
    CMS_MSSQL_CONTAINER = "cms-e2e-isolated-db"
    CMS_KEYCLOAK_CONTAINER = "cms-e2e-isolated-keycloak"
    CMS_CONFIG_CONTAINER = "cms-e2e-isolated-config"
    # The Configuration Service listens on the same port inside and outside its container, as the
    # stock stack does: it fetches its own OpenAPI document from the request's host and port.
    DMS_CONFIG_ASPNETCORE_HTTP_PORTS = "8191"
    SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT = "http://localhost:8191/connect/token"
    SELF_CONTAINED_DMS_JWT_AUTHORITY = "http://ed-fi-api-config:8191"
    SELF_CONTAINED_DMS_JWT_METADATA_ADDRESS = "http://ed-fi-api-config:8191/.well-known/openid-configuration"
    DMS_CONFIG_IDENTITY_AUTHORITY = "http://ed-fi-api-config:8191"
    CMS_E2E_API_URL = "http://localhost:8191"
    POSTGRES_PORT = "5536"
    MSSQL_PORT = "1536"
    KEYCLOAK_PORT = "8145"
    DMS_CONFIG_DOCKER_IMAGE = "ed-fi-api-config-cms-e2e-isolated"
}

$derivedDirectory = Join-Path $composeDirectory ".derived"
$null = New-Item -ItemType Directory -Path $derivedDirectory -Force
$derivedEnvironmentFile = Join-Path $derivedDirectory "$([System.IO.Path]::GetFileName($sourceEnvironmentFile)).isolated"

# Keys are replaced line by line, so every other line, multi-line values included, passes through.
$lines = [System.Collections.Generic.List[string]]::new([string[]](Get-Content -LiteralPath $sourceEnvironmentFile))
$remaining = [ordered]@{} + $isolation
for ($index = 0; $index -lt $lines.Count; $index++) {
    if ($lines[$index] -match '^(?<Key>[A-Za-z_][A-Za-z0-9_]*)=' -and $remaining.Contains($Matches.Key)) {
        $lines[$index] = "$($Matches.Key)=$($remaining[$Matches.Key])"
        $remaining.Remove($Matches.Key)
    }
}
foreach ($key in $remaining.Keys) {
    $lines.Add("$key=$($remaining[$key])")
}
Set-Content -LiteralPath $derivedEnvironmentFile -Value $lines
Write-Output "Isolated environment file: $derivedEnvironmentFile"

# Compose gives the process environment precedence over --env-file, so every isolation value is also
# set in this process: a value inherited from the caller's shell cannot override any of them.
foreach ($key in $isolation.Keys) {
    Set-Item -Path "Env:$key" -Value $isolation[$key]
}

function Remove-IsolatedStack {
    Push-Location $composeDirectory
    try {
        ./start-local-config.ps1 -d -v -EnvironmentFile $derivedEnvironmentFile
        & docker network rm $isolation.CMS_COMPOSE_NETWORK *> $null
    }
    finally {
        Pop-Location
    }
}

<#
    Guards that must hold before anything can send a request. The E2ETest path runs the test assembly
    already built, without building it, so a stale build can carry defaults that point at the stock
    stack. The assembly is therefore built here first, and a read-only preflight test inside that very
    assembly must report that it targets this deployment's Configuration Service and database.
#>
Import-Module (Join-Path $repoRoot "eng/build-helpers.psm1") -Force

$e2eProject = Join-Path $repoRoot "src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/EdFi.DmsConfigurationService.Tests.E2E.csproj"
Write-Output "Building the E2E test project ($Configuration) so the suite run uses this tree's code..."
& dotnet build $e2eProject --configuration $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Building the E2E test project failed with exit code $LASTEXITCODE; refusing to run the suite."
}

$derivedValues = @{}
foreach ($line in $lines) {
    if ($line -match '^(?<Key>[A-Za-z_][A-Za-z0-9_]*)=(?<Value>.*)$') {
        $derivedValues[$Matches.Key] = $Matches.Value
    }
}
$datastore = if ($derivedValues["DMS_CONFIG_DATASTORE"]) { $derivedValues["DMS_CONFIG_DATASTORE"] } else { "postgresql" }
$expectedDbTarget = if ($datastore -eq "mssql") {
    "mssql localhost:$($isolation.MSSQL_PORT)/$($derivedValues["POSTGRES_DB_NAME"])"
}
else {
    "postgresql localhost:$($isolation.POSTGRES_PORT)/$($derivedValues["POSTGRES_DB_NAME"])"
}

# The values the suite's hooks read, as build-config.ps1 will export them for the run itself.
$env:POSTGRES_DB_NAME = $derivedValues["POSTGRES_DB_NAME"]
$env:DMS_CONFIG_DATASTORE = $datastore
$env:CMS_E2E_PREFLIGHT_API_URL = $isolation.CMS_E2E_API_URL
$env:CMS_E2E_PREFLIGHT_DB_TARGET = $expectedDbTarget

if ($derivedValues["CMS_CONFIG_CONTAINER"] -ne $isolation.CMS_CONFIG_CONTAINER) {
    throw "The derived environment file does not name the isolated Configuration Service container; refusing to run."
}

$preflightTests = @(
    "It_sends_requests_to_the_selected_configuration_service",
    "It_cleans_the_selected_database"
)
foreach ($assembly in @(Get-RequiredTestAssembly -SolutionRoot (Join-Path $repoRoot "src/config") -Filter "*.Tests.E2E" -Configuration $Configuration)) {
    $preflightResults = Join-Path $derivedDirectory "e2e-target-preflight.trx"
    Remove-Item -LiteralPath $preflightResults -ErrorAction SilentlyContinue
    & dotnet test $assembly.FullName --filter "TestCategory=E2ETargetPreflight" --logger "trx;LogFileName=$preflightResults"
    if ($LASTEXITCODE -ne 0) {
        throw "The target preflight failed for $($assembly.FullName); the suite would not target this deployment. Refusing to run."
    }

    # Zero discovered or skipped tests is not a pass: every preflight test must have executed and passed.
    [xml]$trx = Get-Content -LiteralPath $preflightResults -Raw
    $results = @($trx.TestRun.Results.UnitTestResult)
    foreach ($name in $preflightTests) {
        $result = $results | Where-Object { $_.testName -eq $name }
        if (@($result).Count -ne 1 -or $result.outcome -ne "Passed") {
            throw "The target preflight test $name did not execute and pass for $($assembly.FullName). Refusing to run."
        }
    }
    Write-Output "Target preflight passed for $($assembly.FullName): requests go to $($isolation.CMS_E2E_API_URL), cleanup targets $expectedDbTarget."
}

if ($PreflightOnly) {
    return
}

Remove-IsolatedStack

$exitCode = 1
Push-Location $repoRoot
try {
    $arguments = @{
        Command = "E2ETest"
        Configuration = $Configuration
        IdentityProvider = $IdentityProvider
        EnvironmentFile = $derivedEnvironmentFile
    }
    if ($E2ETestFilter) {
        $arguments.E2ETestFilter = $E2ETestFilter
    }
    if ($SkipDockerBuild) {
        $arguments.SkipDockerBuild = $true
    }

    ./build-config.ps1 @arguments
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
    if ($KeepStack) {
        Write-Output "Isolated stack left running (project $($isolation.CMS_COMPOSE_PROJECT))."
    }
    else {
        Remove-IsolatedStack
    }
}

exit $exitCode
