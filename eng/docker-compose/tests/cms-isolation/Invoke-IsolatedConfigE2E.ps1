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

    The run fails closed before it changes anything in Docker: it refuses to start when the chosen
    project has any container, volume or network, when a chosen container name or the network already
    exists, or when anything listens on one of its host ports. It only ever removes a stack it started
    itself in the same run. A stack an earlier -KeepStack run left is not taken as this run's: remove it
    first with start-local-config.ps1 -d -v -EnvironmentFile <the derived file> and docker network rm.

    Both identity providers are supported. The isolation moves the self-contained token endpoint and
    authority onto the isolated Configuration Service, and Keycloak's published port, external token
    endpoint and issuer onto the isolated Keycloak; inside the isolated network Keycloak keeps its
    hostname, so the internal authority and metadata addresses are unchanged. -IsolationName names the
    project, network, containers and image, so a launcher run can be given names no other stack uses.

.EXAMPLE
    pwsh eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.ps1 -Configuration Release

.EXAMPLE
    pwsh eng/docker-compose/tests/cms-isolation/Invoke-IsolatedConfigE2E.ps1 -IdentityProvider keycloak -IsolationName cms-e2e-ticket
#>
[CmdletBinding()]
param(
    [string]
    $EnvironmentFile = "./.env.config.e2e",

    [ValidateSet("Debug", "Release")]
    [string]
    $Configuration = "Debug",

    [ValidateSet("self-contained", "keycloak")]
    [string]
    $IdentityProvider = "self-contained",

    # The base of the project, network, container and image names the isolated stack uses.
    [ValidatePattern('^[a-z0-9][a-z0-9-]*$')]
    [string]
    $IsolationName = "cms-e2e-isolated",

    [string]
    $E2ETestFilter = "",

    [ValidateSet("Hybrid", "Filesystem", "Upload")]
    [string]
    $SecurityMetadataToolsScenario = "",

    # Custom fixture plumbing remains available independently of named scenarios.
    [string]
    $ClaimsFixtureDirectory = "",

    [ValidateSet("Hybrid", "Filesystem")]
    [string]
    $ClaimsSource = "Hybrid",

    [ValidateSet("5.2", "6.1")]
    [string]
    $DataStandardVersion = "5.2",

    [switch]
    $EnableUnrestrictedClaimsLoading,

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
    CONFIGURATION_SERVICE_COMPOSE_PROJECT = $IsolationName
    CONFIGURATION_SERVICE_COMPOSE_NETWORK = $IsolationName
    CONFIGURATION_SERVICE_COMPOSE_OVERLAY_FILES = "tests/cms-isolation/cms-e2e-isolation.yml"
    CONFIGURATION_SERVICE_POSTGRES_CONTAINER = "$IsolationName-db"
    CONFIGURATION_SERVICE_MSSQL_CONTAINER = "$IsolationName-db"
    CONFIGURATION_SERVICE_KEYCLOAK_CONTAINER = "$IsolationName-keycloak"
    CONFIGURATION_SERVICE_CONTAINER = "$IsolationName-config"
    # The Configuration Service listens on the same port inside and outside its container, as the
    # stock stack does: it fetches its own OpenAPI document from the request's host and port.
    CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS = "8191"
    SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT = "http://localhost:8191/connect/token"
    SELF_CONTAINED_DMS_JWT_AUTHORITY = "http://ed-fi-api-config:8191"
    SELF_CONTAINED_DMS_JWT_METADATA_ADDRESS = "http://ed-fi-api-config:8191/.well-known/openid-configuration"
    CONFIGURATION_SERVICE_IDENTITY_AUTHORITY = "http://ed-fi-api-config:8191"
    CONFIGURATION_SERVICE_E2E_API_URL = "http://localhost:8191"
    POSTGRES_PORT = "5536"
    MSSQL_PORT = "1536"
    KEYCLOAK_PORT = "8145"
    # Keycloak issues tokens for the host name it publishes (KC_HOSTNAME follows KEYCLOAK_PORT), so the
    # external token endpoint and issuer move with the port. The internal authority and metadata
    # addresses name the Keycloak hostname, which the isolated network also resolves, and stay as they are.
    KEYCLOAK_OAUTH_TOKEN_ENDPOINT = "http://localhost:8145/realms/edfi/protocol/openid-connect/token"
    KEYCLOAK_DMS_JWT_AUTHORITY = "http://localhost:8145/realms/edfi"
    CONFIGURATION_SERVICE_DOCKER_IMAGE = "ed-fi-api-config-$IsolationName"
}

$expectedTestNames = @()
if ($SecurityMetadataToolsScenario) {
    foreach ($conflictingParameter in @('ClaimsFixtureDirectory', 'ClaimsSource', 'EnableUnrestrictedClaimsLoading', 'E2ETestFilter')) {
        if ($PSBoundParameters.ContainsKey($conflictingParameter)) {
            throw "Named policy scenarios own $conflictingParameter; omit that override."
        }
    }
    $ClaimsFixtureDirectory = Join-Path $repoRoot "src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/TestData/Claims/SecurityMetadataTools/Hybrid/fragments"
    if ($SecurityMetadataToolsScenario -in @('Filesystem', 'Upload') -and $DataStandardVersion -ne '5.2') {
        throw "The $SecurityMetadataToolsScenario policy scenario requires DataStandardVersion 5.2."
    }
    $ClaimsSource = if ($SecurityMetadataToolsScenario -eq 'Upload') { 'Hybrid' } else { $SecurityMetadataToolsScenario }
    $EnableUnrestrictedClaimsLoading = $SecurityMetadataToolsScenario -eq 'Upload'
    $testName = if ($SecurityMetadataToolsScenario -eq 'Filesystem') {
        'It_loads_unmodified_merge_output_through_Filesystem_startup'
    } elseif ($SecurityMetadataToolsScenario -eq 'Upload') {
        'It_uploads_unmodified_merge_output_and_verifies_stored_permissions'
    } else {
        "It_matches_reviewed_Hybrid_permissions_for_DS_$($DataStandardVersion.Replace('.', '_'))"
    }
    $expectedTestNames = @($testName)
    $E2ETestFilter = "(TestCategory=SecurityMetadataToolsService)&(TestCategory=SecurityMetadataTools$SecurityMetadataToolsScenario)&(Name=$testName)"
}

Import-Module (Join-Path $PSScriptRoot "IsolatedConfigE2E.psm1") -Force
$preparedFixtureDirectory = ""
$runId = [guid]::NewGuid().ToString("N")
if ($ClaimsFixtureDirectory) {
    if ($KeepStack) {
        throw "Custom fixture runs require teardown; KeepStack is supported only for ordinary runs."
    }
    $ClaimsFixtureDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ClaimsFixtureDirectory)
    $preparedFixtureDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "cms-policy-$runId"
    $isolation.CONFIGURATION_SERVICE_CLAIMS_SOURCE = $ClaimsSource
    $isolation.CONFIGURATION_SERVICE_DATA_STANDARD_VERSION = $DataStandardVersion
    $isolation.CONFIGURATION_SERVICE_CLAIMS_DIRECTORY = "/app/additional-claims"
    $isolation.CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING = $EnableUnrestrictedClaimsLoading.ToString().ToLowerInvariant()
    $isolation.CONFIGURATION_SERVICE_CLAIMS_MOUNT_SOURCE = $preparedFixtureDirectory
}
elseif ($PSBoundParameters.ContainsKey("DataStandardVersion")) {
    $isolation.CONFIGURATION_SERVICE_DATA_STANDARD_VERSION = $DataStandardVersion
}

$derivedDirectory = Join-Path $composeDirectory ".derived"
$null = New-Item -ItemType Directory -Path $derivedDirectory -Force
# Named for the isolation as well as the source, so a run under another -IsolationName never overwrites the
# file a kept stack's cleanup needs.
$derivedEnvironmentFile = Join-Path $derivedDirectory "$([System.IO.Path]::GetFileName($sourceEnvironmentFile)).$IsolationName.isolated"

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

# Every process variable the harness sets, with the value it replaced, so a run inside an interactive
# session restores those variables afterwards. A later start-local-config.ps1 in the same session would
# otherwise inherit the isolated project, ports and image. Variables build-config.ps1 and the scripts
# it calls set themselves are not tracked here.
$priorEnvironment = @{}

function Set-HarnessVariable {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal helper of the non-interactive E2E harness; it sets only process variables it restores.')]
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Name, [string] $Value)

    if (-not $priorEnvironment.ContainsKey($Name)) {
        $priorEnvironment[$Name] = [System.Environment]::GetEnvironmentVariable($Name)
    }
    [System.Environment]::SetEnvironmentVariable($Name, $Value)
}

# Child setup/build scripts also change process settings. Snapshot them before invoking any child.
foreach ($name in @(
    'CONFIGURATION_SERVICE_MULTI_TENANCY', 'POSTGRES_PASSWORD', 'POSTGRES_PORT', 'POSTGRES_DB_NAME',
    'MSSQL_SA_PASSWORD', 'MSSQL_PORT', 'CONFIGURATION_SERVICE_IDENTITY_PROVIDER',
    'OAUTH_TOKEN_ENDPOINT', 'DMS_JWT_AUTHORITY', 'DMS_JWT_METADATA_ADDRESS',
    'CONFIGURATION_SERVICE_E2E_EXPECTED_IDENTITY_PROVIDER', 'CONFIGURATION_SERVICE_E2E_EXPECTED_ROLE_CLAIM_TYPE',
    'CONFIGURATION_SERVICE_E2E_EXPECTED_CLIENT_ROLE', 'CONFIGURATION_SERVICE_E2E_EXPECTED_SELF_CONTAINED_ROLE_CLAIM_TYPE',
    'NODE_OPTIONS'
)) {
    $priorEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$exitCode = 1
try {
    Remove-Item Env:NODE_OPTIONS -ErrorAction SilentlyContinue
    if ($preparedFixtureDirectory) {
        if ($SecurityMetadataToolsScenario -in @('Filesystem', 'Upload')) {
            # Upload starts base-only; its test merges fragments outside this empty mount.
            $null = New-Item -ItemType Directory -Path $preparedFixtureDirectory
        } else {
            $null = Copy-IsolatedClaimsFixture -Source $ClaimsFixtureDirectory -Destination $preparedFixtureDirectory
        }
    }
    # Compose gives the process environment precedence over --env-file, so every isolation value is also
    # set in this process: a value inherited from the caller's shell cannot override any of them.
    foreach ($key in $isolation.Keys) {
        Set-HarnessVariable -Name $key -Value $isolation[$key]
    }

    # Fail closed before any Docker change: everything this run would create must not exist yet, so a
    # stack, network or volume another workspace or an earlier -KeepStack run holds is never removed.
    $projectLabel = "label=com.docker.compose.project=$($isolation.CONFIGURATION_SERVICE_COMPOSE_PROJECT)"
    $inUse = @(
        & docker ps --all --filter $projectLabel --format 'container {{.Names}}'
        if ($LASTEXITCODE -ne 0) { throw "Listing containers failed; refusing to run." }
        & docker volume ls --filter $projectLabel --format 'volume {{.Name}}'
        if ($LASTEXITCODE -ne 0) { throw "Listing volumes failed; refusing to run." }
        & docker network ls --filter $projectLabel --format 'network {{.Name}}'
        if ($LASTEXITCODE -ne 0) { throw "Listing networks failed; refusing to run." }
    )
    $existingContainers = @(& docker ps --all --format '{{.Names}}')
    if ($LASTEXITCODE -ne 0) { throw "Listing all containers failed; refusing to run." }
    $existingNetworks = @(& docker network ls --format '{{.Name}}')
    if ($LASTEXITCODE -ne 0) { throw "Listing all networks failed; refusing to run." }
    foreach ($container in @(
            $isolation.CONFIGURATION_SERVICE_POSTGRES_CONTAINER
            $isolation.CONFIGURATION_SERVICE_KEYCLOAK_CONTAINER
            $isolation.CONFIGURATION_SERVICE_CONTAINER
        ) | Select-Object -Unique) {
        if ($existingContainers -ccontains $container) { $inUse += "container $container" }
    }
    if ($existingNetworks -ccontains $isolation.CONFIGURATION_SERVICE_COMPOSE_NETWORK) {
        $inUse += "network $($isolation.CONFIGURATION_SERVICE_COMPOSE_NETWORK)"
    }
    foreach ($port in @($isolation.CONFIGURATION_SERVICE_ASPNETCORE_HTTP_PORTS, $isolation.POSTGRES_PORT, $isolation.MSSQL_PORT, $isolation.KEYCLOAK_PORT)) {
        $probe = [System.Net.Sockets.TcpClient]::new()
        try {
            # A refused connection throws, which means the port is free.
            $listening = try { $probe.ConnectAsync("127.0.0.1", [int]$port).Wait(1000) } catch { $false }
        }
        finally {
            $probe.Dispose()
        }
        if ($listening) { $inUse += "host port $port" }
    }
    if ($inUse.Count -gt 0) {
        throw "The isolation identity '$IsolationName' is in use ($(@($inUse | Select-Object -Unique) -join ', ')); refusing to run. Choose another -IsolationName, or remove what is listed if this workspace left it."
    }

    function Remove-IsolatedStack {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal teardown of the non-interactive E2E harness; it removes only the isolated stack it started.')]
        [CmdletBinding()]
        param()

        Push-Location $composeDirectory
        try {
            # start-local-config.ps1 does not check its own docker compose down, so the exit code it
            # leaves behind is read here: a stack that is still running must not read as removed.
            $global:LASTEXITCODE = 0
            ./start-local-config.ps1 -d -v -EnvironmentFile $derivedEnvironmentFile
            $downExitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }

        if ($downExitCode -ne 0) {
            throw "Removing the isolated stack (project $($isolation.CONFIGURATION_SERVICE_COMPOSE_PROJECT)) failed with exit code $downExitCode; it may still be running. Remove it with start-local-config.ps1 -d -v -EnvironmentFile $derivedEnvironmentFile."
        }

        # Every compose file declares this network external, so compose down leaves it and this is the
        # only removal. Its failure does not fail the run, since the network holds no data, but the next
        # run refuses to start while it exists, so the failure is reported with the command that clears it.
        $network = $isolation.CONFIGURATION_SERVICE_COMPOSE_NETWORK
        $removeOutput = & docker network rm $network 2>&1
        if ($LASTEXITCODE -ne 0) {
            $leftNetworks = @(& docker network ls --filter "name=^$network$" --format '{{.Name}}')
            if ($LASTEXITCODE -ne 0 -or $leftNetworks -ccontains $network) {
                Write-Warning "Removing the isolated network $network failed ($($removeOutput -join ' ')). The next run with -IsolationName $IsolationName refuses to start until it is removed: docker network rm $network"
            }
        }
        $global:LASTEXITCODE = 0
    }

    <#
        Guards that must hold before anything can send a request. The E2ETest path runs the test assembly
        already built, without building it, so a stale build can carry defaults that point at the stock
        stack. The assembly is therefore built here first, and a read-only preflight test inside that very
        assembly must report that it targets this deployment's Configuration Service, and the engine and
        host port of its database. The database name in the expectation comes from POSTGRES_DB_NAME,
        which the cleanup hooks also read, so it confirms nothing on its own.
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
    $datastore = if ($derivedValues["CONFIGURATION_SERVICE_DATASTORE"]) { $derivedValues["CONFIGURATION_SERVICE_DATASTORE"] } else { "postgresql" }
    $expectedDbTarget = if ($datastore -eq "mssql") {
        "mssql localhost:$($isolation.MSSQL_PORT)/$($derivedValues["POSTGRES_DB_NAME"])"
    }
    else {
        "postgresql localhost:$($isolation.POSTGRES_PORT)/$($derivedValues["POSTGRES_DB_NAME"])"
    }

    # The values the suite's hooks read, as build-config.ps1 will export them for the run itself.
    foreach ($name in @('POSTGRES_PASSWORD', 'MSSQL_SA_PASSWORD', 'CONFIGURATION_SERVICE_MULTI_TENANCY')) {
        Set-HarnessVariable -Name $name -Value $derivedValues[$name]
    }
    Set-HarnessVariable -Name POSTGRES_DB_NAME -Value $derivedValues["POSTGRES_DB_NAME"]
    Set-HarnessVariable -Name CONFIGURATION_SERVICE_DATASTORE -Value $datastore
    Set-HarnessVariable -Name CONFIGURATION_SERVICE_E2E_PREFLIGHT_API_URL -Value $isolation.CONFIGURATION_SERVICE_E2E_API_URL
    Set-HarnessVariable -Name CONFIGURATION_SERVICE_E2E_PREFLIGHT_DB_TARGET -Value $expectedDbTarget

    if ($derivedValues["CONFIGURATION_SERVICE_CONTAINER"] -ne $isolation.CONFIGURATION_SERVICE_CONTAINER) {
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
        # Results are selected by local name, so a results file with no Results element reaches the
        # message below instead of failing strict mode's property access.
        $content = if (Test-Path -LiteralPath $preflightResults) { Get-Content -LiteralPath $preflightResults -Raw } else { $null }
        if ([string]::IsNullOrWhiteSpace($content)) {
            throw "The target preflight wrote no results to $preflightResults for $($assembly.FullName). Refusing to run."
        }
        [xml]$trx = $content
        $results = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
        foreach ($name in $preflightTests) {
            $result = $results | Where-Object { $_.testName -eq $name }
            if (@($result).Count -ne 1 -or $result.outcome -ne "Passed") {
                throw "The target preflight test $name did not execute and pass for $($assembly.FullName). Refusing to run."
            }
        }
        Write-Output "Target preflight passed for $($assembly.FullName): requests go to $($isolation.CONFIGURATION_SERVICE_E2E_API_URL), cleanup targets $expectedDbTarget."
    }

    if ($PreflightOnly) {
        $exitCode = 0
        return
    }

    $scenarioName = if ($SecurityMetadataToolsScenario) { $SecurityMetadataToolsScenario } elseif ($preparedFixtureDirectory) { "CustomFixture" } else { "OrdinarySuite" }
    $selectedVersion = if ($derivedValues["CONFIGURATION_SERVICE_DATA_STANDARD_VERSION"]) { $derivedValues["CONFIGURATION_SERVICE_DATA_STANDARD_VERSION"] } else { "5.2" }
    $artifactDirectory = Join-Path $repoRoot "TestResults/cms-isolation/$datastore/$scenarioName/$selectedVersion/$runId"
    if ($preparedFixtureDirectory) {
        Set-HarnessVariable -Name SECURITY_METADATA_TOOLS_SERVICE_RUNNER -Value "true"
        Set-HarnessVariable -Name SECURITY_METADATA_TOOLS_FIXTURE_DIRECTORY -Value $preparedFixtureDirectory
        Set-HarnessVariable -Name SECURITY_METADATA_TOOLS_EVIDENCE_DIRECTORY -Value $artifactDirectory
    }
    else {
        Set-HarnessVariable -Name SECURITY_METADATA_TOOLS_SERVICE_RUNNER -Value "false"
    }
    $workingTrx = Join-Path $repoRoot "TestResults/EdFi.DmsConfigurationService.Tests.E2E.trx"
    Invoke-IsolatedCmsTestRun -WorkingTrx $workingTrx -ArtifactDirectory $artifactDirectory -ExpectedTestNames $expectedTestNames -Execute {
        if ($SecurityMetadataToolsScenario -eq 'Filesystem') {
            # Generate once before startup; the mount contains only the unchanged complete document.
            $sourceHashes = @(Get-ChildItem -LiteralPath $ClaimsFixtureDirectory -File -Recurse | Sort-Object FullName | Get-FileHash -Algorithm SHA256)
            $merged = Join-Path $artifactDirectory 'merged.json'
            $toolPath = Join-Path $assembly.DirectoryName 'SecurityMetadataTools/security-metadata-tools.dll'
            & dotnet $toolPath merge --fragments $ClaimsFixtureDirectory --data-standard-version $DataStandardVersion --output $merged 1> (Join-Path $artifactDirectory 'merge-stdout.txt') 2> (Join-Path $artifactDirectory 'merge-stderr.txt')
            if ($LASTEXITCODE -ne 0) {
                throw "Preparing Filesystem claims with merge failed (exit $LASTEXITCODE)."
            }
            $mergedHash = (Get-FileHash -LiteralPath $merged -Algorithm SHA256).Hash
            Set-Content -LiteralPath (Join-Path $artifactDirectory 'merged-sha256-before.txt') -Value $mergedHash
            Copy-Item -LiteralPath $merged -Destination (Join-Path $preparedFixtureDirectory 'Claims.json')
            $sourceHashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactDirectory 'source-sha256-before.json')
        }
        Push-Location $repoRoot
        try {
            $arguments = @{
                Command = "E2ETest"
                Configuration = $Configuration
                IdentityProvider = $IdentityProvider
                EnvironmentFile = $derivedEnvironmentFile
            }
            if ($E2ETestFilter) { $arguments.E2ETestFilter = $E2ETestFilter }
            if ($SkipDockerBuild) { $arguments.SkipDockerBuild = $true }
            if ($preparedFixtureDirectory) { $arguments.ClaimsFixtureDirectory = $preparedFixtureDirectory }

            # A child PowerShell process contains build-config's exit calls; finally must still archive
            # results and tear down when startup or the test command fails.
            $commandArguments = @('-NoProfile', '-File', (Join-Path $repoRoot 'build-config.ps1'))
            foreach ($key in $arguments.Keys) {
                $commandArguments += "-$key"
                if ($arguments[$key] -isnot [bool]) { $commandArguments += [string]$arguments[$key] }
            }
            & pwsh @commandArguments
            $exitCode = $LASTEXITCODE
            if ($exitCode -ne 0) {
                throw "Isolated CMS execution failed (exit $exitCode); inspect retained results. Startup/database failures are environment failures, not policy mismatches."
            }
        }
        finally {
            Pop-Location
            if ($SecurityMetadataToolsScenario -eq 'Filesystem') {
                $afterHashes = @(Get-ChildItem -LiteralPath $ClaimsFixtureDirectory -File -Recurse | Sort-Object FullName | Get-FileHash -Algorithm SHA256)
                $afterHashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactDirectory 'source-sha256-after.json')
                if (($sourceHashes | ConvertTo-Json -Compress) -cne ($afterHashes | ConvertTo-Json -Compress)) {
                    throw 'Filesystem scenario changed the source fragments.'
                }
                foreach ($path in @($merged, (Join-Path $preparedFixtureDirectory 'Claims.json'))) {
                    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $mergedHash) {
                        throw 'Filesystem scenario changed the merged document.'
                    }
                }
            }
        }
    } -Teardown {
        if ($KeepStack) {
            Write-Output "Isolated stack left running (project $($isolation.CONFIGURATION_SERVICE_COMPOSE_PROJECT))."
        }
        else {
            Remove-IsolatedStack
        }
    }
    $exitCode = 0
}
finally {
    if ($preparedFixtureDirectory -and (Test-Path -LiteralPath $preparedFixtureDirectory)) {
        Remove-Item -LiteralPath $preparedFixtureDirectory -Recurse -Force
    }
    # A $null prior value means the variable was absent and must be removed. Remove-Item is required for
    # that: SetEnvironmentVariable with $null from PowerShell coerces the value to "", which pwsh on Unix
    # stores as a present-but-blank variable instead of removing it (see bootstrap-manifest.psm1).
    foreach ($name in $priorEnvironment.Keys) {
        if ($null -eq $priorEnvironment[$name]) {
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }
        else {
            [System.Environment]::SetEnvironmentVariable($name, $priorEnvironment[$name])
        }
    }
}

exit $exitCode
