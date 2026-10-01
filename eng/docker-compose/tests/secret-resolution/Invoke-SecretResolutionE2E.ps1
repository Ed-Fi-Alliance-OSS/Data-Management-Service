# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    End-to-end proof that the Configuration Service resolves secret references in stored connection
    strings through a plugin, and that DMS serves data through the resolved credential.

.DESCRIPTION
    Builds and runs an isolated deployment: its own compose project, network, container names, host
    ports and image tags, so it runs beside any other local stack and never touches cs-local or
    dms-local. The stock postgresql.yml, local-config.yml and local-dms.yml services are composed with
    plugins-config.yml, which mounts a plugin root holding the Acme.FileSecretResolver fixture
    published framework-dependent, and with secret-resolution-isolation.yml, which renames the stack,
    allowlists the plugin and mounts the secrets file the fixture reads on every call.

    The run:
      1. publishes the fixture plugin and writes the secrets file;
      2. starts PostgreSQL, initializes the OpenIddict stores, starts the Configuration Service and
         registers its clients;
      3. provisions the DMS database with SchemaTools ddl provision (generated DDL and EffectiveSchema);
      4. starts DMS against it;
      5. runs the SecretResolutionPlugin category of the Configuration Service E2E project, which
         creates data stores through the real endpoints with ${secret:...} passwords, asserts DMS
         serves a resource out of one, and measures a rotation against the Configuration Service's
         own endpoint.
    The stack is removed afterwards, volumes included, unless -KeepStack is given.

.PARAMETER KeepStack
    Leaves the deployment running for inspection. Remove it later with -Down.

.PARAMETER Down
    Removes a deployment left by -KeepStack, then exits.

.PARAMETER SkipImageBuild
    Reuses the harness's image tags from an earlier run instead of rebuilding them.

.PARAMETER CacheExpirationSeconds
    SecretsSettings:CacheExpirationSeconds for the Configuration Service, the window the rotation
    proof measures.

.PARAMETER ResultsDirectory
    Where the TRX result of the test run is written.

.EXAMPLE
    pwsh eng/docker-compose/tests/secret-resolution/Invoke-SecretResolutionE2E.ps1
#>
[CmdletBinding()]
param(
    [switch]
    $KeepStack,

    [switch]
    $Down,

    [switch]
    $SkipImageBuild,

    [ValidateRange(5, 120)]
    [int]
    $CacheExpirationSeconds = 20,

    [string]
    $Configuration = "Release",

    [string]
    $ResultsDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$composeDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $composeDirectory "../.."))

Import-Module (Join-Path $composeDirectory "env-utility.psm1") -Force
Import-Module (Join-Path $composeDirectory "bootstrap-manifest.psm1") -Force
Import-Module (Join-Path $composeDirectory "database-safety.psm1") -Force

# The deployment's identity. Everything that names a container, network, port or image is here, so
# none of it can collide with a stack another launcher owns.
$project = "dms-secrets-e2e"
$names = @{
    SECRETS_E2E_NETWORK = "dms-secrets-e2e"
    SECRETS_E2E_POSTGRES_CONTAINER = "dms-secrets-e2e-postgresql"
    SECRETS_E2E_CONFIG_CONTAINER = "dms-secrets-e2e-config"
    SECRETS_E2E_DMS_CONTAINER = "dms-secrets-e2e-api"
}
$postgresPort = 5446
$dmsPort = 8090
$configPort = 8091
$pluginName = "Acme.FileSecretResolver"
$datastoreSecretName = "dms/datastore-password"

$workDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "dms-secret-resolution-e2e"
$environmentFile = Join-Path $workDirectory ".env.secret-resolution.e2e"
$pluginRoot = Join-Path $workDirectory "plugins"
$secretsDirectory = Join-Path $workDirectory "secrets"
$secretsFile = Join-Path $secretsDirectory "secrets.json"

$composeFiles = @(
    "-f", "postgresql.yml",
    "-f", "local-config.yml",
    "-f", "local-dms.yml",
    "-f", "plugins-config.yml",
    "-f", "tests/secret-resolution/secret-resolution-isolation.yml"
)

function Invoke-Compose {
    param([Parameter(Mandatory)] [string[]] $Arguments)

    & docker compose @composeFiles --env-file $environmentFile -p $project @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

# Every process variable the harness sets, with the value it replaced, so a run inside an interactive
# session leaves that session as it found it. A later start-local-config.ps1 in the same session would
# otherwise inherit the harness's project, ports and images.
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

function Restore-HarnessEnvironment {
    foreach ($name in $priorEnvironment.Keys) {
        [System.Environment]::SetEnvironmentVariable($name, $priorEnvironment[$name])
    }
    $priorEnvironment.Clear()
}

<#
    Every proof the run must have executed and passed. A filter that matches nothing, or a proof that
    ignores itself because a setting did not reach it, leaves dotnet test exiting zero, so the exit
    code alone is not evidence. Renaming or adding a proof means updating this list.
#>
$expectedProofs = @(
    "It_returns_the_resolved_password_from_the_configuration_service",
    "It_lets_dms_write_and_read_a_resource_through_the_resolved_credential",
    "It_resolves_the_value_the_store_held_first",
    "It_observed_the_inside_read_inside_the_window",
    "It_keeps_the_previous_value_inside_the_window",
    "It_does_not_return_the_rotated_value_before_the_window_can_have_closed",
    "It_returns_the_rotated_value_once_the_window_has_closed",
    "It_keeps_returning_the_rotated_value",
    "It_returns_only_the_previous_or_the_rotated_value"
)

function Assert-ProofsExecuted {
    param([Parameter(Mandatory)] [string] $TrxPath)

    if (-not (Test-Path -LiteralPath $TrxPath)) {
        throw "The SecretResolutionPlugin proofs wrote no results to $TrxPath."
    }

    [xml]$trx = Get-Content -LiteralPath $TrxPath -Raw
    $results = @($trx.TestRun.Results.UnitTestResult | Where-Object { $null -ne $_ })
    if ($results.Count -eq 0) {
        throw "No SecretResolutionPlugin proofs ran; the filter matched nothing."
    }

    $notPassed = @($results | Where-Object { $_.outcome -ne "Passed" })
    if ($notPassed.Count -gt 0) {
        throw "SecretResolutionPlugin proofs did not pass: $(($notPassed | ForEach-Object { "$($_.testName) ($($_.outcome))" }) -join ', ')."
    }

    foreach ($name in $expectedProofs) {
        if (@($results | Where-Object { $_.testName -eq $name }).Count -ne 1) {
            throw "The SecretResolutionPlugin proof $name did not execute exactly once."
        }
    }

    Write-Output "All $($expectedProofs.Count) SecretResolutionPlugin proofs executed and passed."
}

function Wait-HttpHealthy {
    param([string] $Url, [string] $Name, [int] $TimeoutSeconds = 300)

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5 -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200) {
                Write-Output "$Name is healthy."
                return
            }
        }
        catch {
            Write-Verbose "Waiting for $Name at $Url"
        }
        Start-Sleep -Seconds 3
    }
    throw "$Name did not become healthy at $Url within $TimeoutSeconds seconds."
}

function Wait-PostgresqlReady {
    param([int] $TimeoutSeconds = 120)

    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        & docker exec $names.SECRETS_E2E_POSTGRES_CONTAINER pg_isready -U postgres *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Output "PostgreSQL is ready."
            return
        }
        Start-Sleep -Seconds 2
    }
    throw "PostgreSQL did not become ready within $TimeoutSeconds seconds."
}

<#
    The harness's environment file: .env.e2e with the deployment's own ports, URLs, image tags and
    mount sources. Keys are replaced line by line so multi-line values such as SCHEMA_PACKAGES pass
    through untouched.
#>
function New-HarnessEnvironmentFile {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal helper of the non-interactive E2E harness; it writes only the harness-owned environment file.')]
    [CmdletBinding()]
    param([hashtable] $Overrides)

    $lines = [System.Collections.Generic.List[string]]::new(
        [string[]](Get-Content -LiteralPath (Join-Path $composeDirectory ".env.e2e"))
    )
    $remaining = [System.Collections.Generic.Dictionary[string, string]]::new()
    foreach ($key in $Overrides.Keys) {
        $remaining[$key] = [string]$Overrides[$key]
    }

    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match '^(?<Key>[A-Za-z_][A-Za-z0-9_]*)=' -and $remaining.ContainsKey($Matches.Key)) {
            $key = $Matches.Key
            $lines[$index] = "$key=$($remaining[$key])"
            $null = $remaining.Remove($key)
        }
    }

    foreach ($key in $remaining.Keys) {
        $lines.Add("$key=$($remaining[$key])")
    }

    Set-Content -LiteralPath $environmentFile -Value $lines

    # Compose gives the process environment precedence over --env-file, so every override is also
    # set in this process: a port, URL, image or cache setting inherited from the caller's shell
    # cannot replace the harness's own.
    foreach ($key in $Overrides.Keys) {
        Set-HarnessVariable -Name $key -Value ([string]$Overrides[$key])
    }
}

function Remove-Deployment {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal teardown of the non-interactive E2E harness; it removes only the harness-owned deployment and work directory.')]
    [CmdletBinding()]
    param()

    Push-Location $composeDirectory
    try {
        if (Test-Path -LiteralPath $environmentFile) {
            & docker compose @composeFiles --env-file $environmentFile -p $project down -v --remove-orphans
        }
        else {
            # Without the environment file the compose files cannot be interpolated, but the project's
            # containers, networks and volumes carry its label, so they can still be found and removed.
            & docker compose -p $project down -v --remove-orphans
        }
        $downExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    # The work directory holds the environment file a retried -Down needs, so it is kept until the
    # deployment is known to be gone.
    if ($downExitCode -ne 0) {
        throw "docker compose down for project $project failed with exit code $downExitCode; the deployment and $workDirectory were left in place. Retry with -Down."
    }

    & docker network rm $names.SECRETS_E2E_NETWORK *> $null
    if (Test-Path -LiteralPath $workDirectory) {
        Remove-Item -LiteralPath $workDirectory -Recurse -Force
    }
}

try {
    # Every variable the compose files read, set for this process too, because Compose gives the process
    # environment precedence over --env-file and a value inherited from the caller's shell must not win.
    foreach ($name in $names.Keys) {
        Set-HarnessVariable -Name $name -Value $names[$name]
    }
    Set-HarnessVariable -Name SECRETS_E2E_SECRETS_DIRECTORY -Value $secretsDirectory
    Set-HarnessVariable -Name CMS_PLUGINS_MOUNT_SOURCE -Value $pluginRoot

    if ($Down) {
        Remove-Deployment
        return
    }

    if (-not $ResultsDirectory) {
        $ResultsDirectory = Join-Path $workDirectory "results"
    }
    $ResultsDirectory = [System.IO.Path]::GetFullPath($ResultsDirectory)

    Remove-Deployment
    $null = New-Item -ItemType Directory -Path $workDirectory, $pluginRoot, $secretsDirectory, $ResultsDirectory -Force

    # Read as Compose resolves them, so a value set in the caller's shell, which Compose, the
    # Configuration Service and provisioning all use, is the one the harness uses too.
    $baseValues = ReadValuesFromEnvFile (Join-Path $composeDirectory ".env.e2e")
    function Get-BaseValue([string] $Name) {
        Get-RequiredComposeResolvedEnvValue -EnvironmentValues $baseValues -Name $Name
    }
    $postgresPassword = Get-BaseValue "POSTGRES_PASSWORD"
    $databaseName = Get-BaseValue "E2E_DATABASE_NAME"
    $configUrlInNetwork = "http://ed-fi-api-config:$configPort"
    $claimsMountSource = Initialize-E2EClaimsWorkspace

    New-HarnessEnvironmentFile -Overrides @{
        POSTGRES_PORT = $postgresPort
        DMS_HTTP_PORTS = $dmsPort
        DMS_CONFIG_ASPNETCORE_HTTP_PORTS = $configPort
        CONFIG_SERVICE_URL = $configUrlInNetwork
        OAUTH_TOKEN_ENDPOINT = "$configUrlInNetwork/connect/token"
        SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT = "$configUrlInNetwork/connect/token"
        SELF_CONTAINED_DMS_JWT_AUTHORITY = $configUrlInNetwork
        SELF_CONTAINED_DMS_JWT_METADATA_ADDRESS = "$configUrlInNetwork/.well-known/openid-configuration"
        DMS_JWT_AUTHORITY = $configUrlInNetwork
        DMS_JWT_METADATA_ADDRESS = "$configUrlInNetwork/.well-known/openid-configuration"
        DMS_CONFIG_IDENTITY_AUTHORITY = $configUrlInNetwork
        DMS_CONFIG_IDENTITY_PROVIDER = "self-contained"
        DMS_CONFIG_SECRETS_CACHE_EXPIRATION_SECONDS = $CacheExpirationSeconds
        DMS_CONFIG_DOCKER_IMAGE = "ed-fi-api-config-secrets-e2e"
        DMS_DOCKER_IMAGE = "ed-fi-api-secrets-e2e"
        DMS_CONFIG_CLAIMS_MOUNT_SOURCE = $claimsMountSource
        CMS_PLUGINS_MOUNT_SOURCE = $pluginRoot
    }

    Write-Output "Publishing $pluginName framework-dependent into the plugin root..."
    & dotnet publish (Join-Path $repoRoot "src/plugins/EdFi.Api.Plugins.Hosting.Tests.Unit/Fixtures/Plugins/$pluginName/$pluginName.csproj") `
        --configuration $Configuration --no-self-contained --output (Join-Path $pluginRoot $pluginName) --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing $pluginName failed with exit code $LASTEXITCODE."
    }

    # The store the fixture reads. The data store password is the real one, held only here; the test
    # owns every other entry, including the ones it rotates.
    @{ $datastoreSecretName = $postgresPassword } | ConvertTo-Json | Set-Content -LiteralPath $secretsFile

    $succeeded = $false
    Push-Location $composeDirectory
    try {
        & docker network inspect $names.SECRETS_E2E_NETWORK *> $null
        if ($LASTEXITCODE -ne 0) {
            & docker network create $names.SECRETS_E2E_NETWORK | Out-Null
        }

        if (-not $SkipImageBuild) {
            Write-Output "Building the Configuration Service and DMS images under the harness's own tags..."
            Invoke-Compose -Arguments @("build", "config", "dms")
        }

        Invoke-Compose -Arguments @("up", "--detach", "db")
        Wait-PostgresqlReady

        $identityDbParams = @{
            EnvironmentFile = $environmentFile
            DbUser = "postgres"
            DbPort = "ENV:POSTGRES_PORT"
            DbName = "ENV:DMS_CONFIG_DATABASE_NAME"
            PostgresContainerName = $names.SECRETS_E2E_POSTGRES_CONTAINER
        }
        ./setup-openiddict.ps1 -InitDb @identityDbParams

        Invoke-Compose -Arguments @("up", "--detach", "config")
        Wait-HttpHealthy -Url "http://localhost:$configPort/health" -Name "Configuration Service"

        $clientSecrets = Resolve-IdentityClientSecretConfiguration -EnvValues (ReadValuesFromEnvFile $environmentFile)
        $bounds = @{
            ClientSecretMinimumLength = $clientSecrets.ClientSecretMinimumLength
            ClientSecretMaximumLength = $clientSecrets.ClientSecretMaximumLength
        }
        ./setup-openiddict.ps1 -InsertData -NewClientSecret $clientSecrets.DmsConfigurationServiceClientSecret @bounds @identityDbParams
        ./setup-openiddict.ps1 -InsertData -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" `
            -ClientScopeName "edfi_admin_api/readonly_access" -NewClientSecret $clientSecrets.CmsReadOnlyAccessClientSecret @bounds @identityDbParams

        Write-Output "Provisioning the DMS database with SchemaTools ddl provision..."
        ./provision-e2e-database.ps1 -EnvironmentFile $environmentFile -DatabaseName $databaseName `
            -PostgresContainerName $names.SECRETS_E2E_POSTGRES_CONTAINER -Configuration $Configuration

        # DMS will not start without a data store to load, so the one the capability proof uses is
        # created first, through the Configuration Service's own endpoint and validator, carrying only a
        # reference to its password. DMS's startup load is then already a read through the resolver.
        $cmsToken = (Invoke-RestMethod -Method Post -Uri "http://localhost:$configPort/connect/token" -Body @{
                client_id = "DmsConfigurationService"
                client_secret = $clientSecrets.DmsConfigurationServiceClientSecret
                grant_type = "client_credentials"
                scope = "edfi_admin_api/full_access"
            }).access_token
        $dataStore = Invoke-RestMethod -Method Post -Uri "http://localhost:$configPort/v3/dataStores" `
            -Headers @{ Authorization = "Bearer $cmsToken" } -ContentType "application/json" -Body (@{
                dataStoreType = "Test"
                name = "Secret resolution capability"
                connectionString = "host=dms-postgresql;port=5432;username=postgres;password=`${secret:$datastoreSecretName};database=$($databaseName)"
            } | ConvertTo-Json)

        Invoke-Compose -Arguments @("up", "--detach", "dms")
        Wait-HttpHealthy -Url "http://localhost:$dmsPort/health" -Name "DMS"

        Set-HarnessVariable -Name SECRETS_E2E_CMS_URL -Value "http://localhost:$configPort"
        Set-HarnessVariable -Name SECRETS_E2E_DMS_URL -Value ("http://localhost:$dmsPort/$(Get-ComposeResolvedEnvValue -EnvironmentValues $baseValues -Name "PATH_BASE")")
        Set-HarnessVariable -Name SECRETS_E2E_CMS_CLIENT_ID -Value "DmsConfigurationService"
        Set-HarnessVariable -Name SECRETS_E2E_CMS_CLIENT_SECRET -Value $clientSecrets.DmsConfigurationServiceClientSecret
        Set-HarnessVariable -Name SECRETS_E2E_ENCRYPTION_KEY -Value (Get-BaseValue "DMS_CONFIG_DATABASE_ENCRYPTION_KEY")
        Set-HarnessVariable -Name SECRETS_E2E_SECRETS_FILE -Value $secretsFile
        Set-HarnessVariable -Name SECRETS_E2E_DATASTORE_SECRET -Value $datastoreSecretName
        Set-HarnessVariable -Name SECRETS_E2E_DATASTORE_ID -Value $dataStore.id
        Set-HarnessVariable -Name SECRETS_E2E_DATASTORE_DATABASE -Value $databaseName
        Set-HarnessVariable -Name SECRETS_E2E_CACHE_EXPIRATION_SECONDS -Value $CacheExpirationSeconds

        Write-Output "Running the SecretResolutionPlugin proofs..."
        $trxPath = Join-Path $ResultsDirectory "secret-resolution-e2e.trx"
        Remove-Item -LiteralPath $trxPath -ErrorAction SilentlyContinue
        & dotnet test (Join-Path $repoRoot "src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/EdFi.DmsConfigurationService.Tests.E2E.csproj") `
            --configuration $Configuration --filter "TestCategory=SecretResolutionPlugin" `
            --logger "trx;LogFileName=$trxPath" `
            --logger "console;verbosity=normal"
        if ($LASTEXITCODE -ne 0) {
            throw "The SecretResolutionPlugin proofs failed with exit code $LASTEXITCODE."
        }
        Assert-ProofsExecuted -TrxPath $trxPath
        $succeeded = $true
    }
    finally {
        if (-not $succeeded) {
            foreach ($container in $names.SECRETS_E2E_CONFIG_CONTAINER, $names.SECRETS_E2E_DMS_CONTAINER) {
                Write-Output "---- last log lines of $container ----"
                & docker logs --tail 60 $container 2>&1 | Write-Output
            }
        }
        Pop-Location

        if ($KeepStack) {
            Write-Output "Deployment left running (project $project). Remove it with -Down."
        }
        else {
            # The results directory sits under the work directory by default; keep a copy outside it.
            if ($ResultsDirectory.StartsWith($workDirectory, [System.StringComparison]::Ordinal)) {
                $kept = Join-Path ([System.IO.Path]::GetTempPath()) "dms-secret-resolution-e2e-results"
                $null = New-Item -ItemType Directory -Path $kept -Force
                Copy-Item -Path (Join-Path $ResultsDirectory "*") -Destination $kept -Force -ErrorAction SilentlyContinue
                Write-Output "Results copied to $kept"
            }

            if ($succeeded) {
                Remove-Deployment
            }
            else {
                # The run's own failure is the one to report; a teardown failure on top of it is a warning.
                try {
                    Remove-Deployment
                }
                catch {
                    Write-Warning $_.Exception.Message
                }
            }
        }
    }
}
finally {
    Restore-HarnessEnvironment
}
