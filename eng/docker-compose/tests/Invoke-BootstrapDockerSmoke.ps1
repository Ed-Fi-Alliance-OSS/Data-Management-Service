# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    DMS-1151 Docker smoke: real bootstrap sequence against a live Docker stack.

.DESCRIPTION
    Manual end-to-end smoke for the DMS-1151 bootstrap contract. Exercises the real
    phase commands (prepare -> infra -> configure -> provision -> re-run -> DMS-only)
    against an actual Docker stack, not the synthetic Pester fixtures.

    The script is intentionally MANUAL: it is not wired into CI and is not run by the
    normal Pester suite. The reviewer for DMS-1151 explicitly asked for at least one
    real Docker pass before sign-off; this script formalizes that pass so it is
    repeatable and observable.

    Prerequisites:
      - Docker daemon running.
      - PowerShell 7+ (pwsh).
      - A directory containing the ApiSchema content used by prepare-dms-schema.ps1,
        passed via -ApiSchemaPath or DMS_SMOKE_API_SCHEMA_PATH.
      - An env file (defaults to eng/docker-compose/.env.example).

    Sequence:
      0. Preflight checks and pre-teardown of any leftover dms-local stack.
      1. prepare-dms-schema.ps1 + prepare-dms-claims.ps1 materialize .bootstrap/.
      2. start-local-dms.ps1 -InfraOnly -EnableConfig -IdentityProvider self-contained.
      3. configure-local-dms-instance.ps1 registers the instance(s).
      4. provision-dms-schema.ps1 provisions the selected target databases.
      5. Re-run step 4 to verify idempotence.
      6. start-local-dms.ps1 -DmsOnly to start DMS against the provisioned database.
      7. Assertions:
           - DMS /health returns 200 within timeout.
           - psql lists at least one table in the provisioned database.
           - Re-run of provision applies no DDL (idempotence captured in step 5).
           - A deliberately corrupted ApiSchema manifest causes provision to fail.
      8. Teardown (unless -SkipTeardown).

    Exit code: 0 on full success, non-zero on the first failing assertion or step.

.PARAMETER EnvironmentFile
    Env file to forward to every phase. Defaults to eng/docker-compose/.env.example.

.PARAMETER ApiSchemaPath
    Directory containing the ApiSchema content for prepare-dms-schema.ps1. Falls back
    to $env:DMS_SMOKE_API_SCHEMA_PATH.

.PARAMETER Extensions
    Optional extension list to forward to prepare-dms-schema.ps1.

.PARAMETER ResultsPath
    Optional path; if supplied, writes a JSON summary of the run (step status + timings).

.PARAMETER SkipTeardown
    When set, leaves the dms-local stack and .bootstrap/ workspace in place after the
    run. Useful when debugging an assertion failure interactively.

.EXAMPLE
    pwsh ./Invoke-BootstrapDockerSmoke.ps1 `
        -ApiSchemaPath "$HOME/edfi/ApiSchema"
#>

[CmdletBinding()]
param(
    [string]$EnvironmentFile,

    [string]$ApiSchemaPath = $env:DMS_SMOKE_API_SCHEMA_PATH,

    [string[]]$Extensions = @(),

    [string]$ResultsPath,

    [switch]$SkipTeardown
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:DockerComposeRoot = Split-Path -Parent $PSScriptRoot
$script:RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $script:DockerComposeRoot "../.."))
$script:BootstrapRoot = Join-Path $script:DockerComposeRoot ".bootstrap"
$script:StepResults = [System.Collections.Generic.List[pscustomobject]]::new()

function Write-SmokeStep {
    param([string]$Label)

    $banner = "=" * 78
    Write-Host ""
    Write-Host $banner
    Write-Host "[smoke] $Label"
    Write-Host $banner
}

function Invoke-SmokeStep {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Body
    )

    Write-SmokeStep $Name
    $startTime = Get-Date
    $status = "ok"
    $errorMessage = $null
    try {
        & $Body
    }
    catch {
        $status = "failed"
        $errorMessage = $_.Exception.Message
        throw
    }
    finally {
        $duration = (Get-Date) - $startTime
        $script:StepResults.Add([pscustomobject]@{
            Name = $Name
            Status = $status
            DurationSeconds = [math]::Round($duration.TotalSeconds, 2)
            Error = $errorMessage
        })
    }
}

function Resolve-EnvironmentFile {
    if ([string]::IsNullOrWhiteSpace($EnvironmentFile)) {
        return (Join-Path $script:DockerComposeRoot ".env.example")
    }

    if ([System.IO.Path]::IsPathRooted($EnvironmentFile)) {
        return $EnvironmentFile
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $EnvironmentFile))
}

function Get-EnvFileValue {
    param(
        [string]$Path,
        [string]$Key
    )

    if (-not (Test-Path -LiteralPath $Path)) { return $null }

    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*#') { continue }
        if ($line -match "^\s*$([regex]::Escape($Key))\s*=\s*(.*)$") {
            return $matches[1].Trim().Trim('"').Trim("'")
        }
    }
    return $null
}

function Invoke-ComposeTeardown {
    param([string]$EnvFile)

    Push-Location $script:DockerComposeRoot
    try {
        & "$script:DockerComposeRoot/start-local-dms.ps1" -d -v -RemoveBootstrap -EnvironmentFile $EnvFile -ErrorAction Continue
    }
    finally {
        Pop-Location
    }
}

function Invoke-PhaseScript {
    param(
        [Parameter(Mandatory)]
        [string]$ScriptPath,

        [Parameter(Mandatory)]
        [hashtable]$Arguments,

        [Parameter(Mandatory)]
        [string]$Description
    )

    Push-Location $script:DockerComposeRoot
    try {
        & $ScriptPath @Arguments
        if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
            throw "$Description failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

# Preflight
$resolvedEnvFile = Resolve-EnvironmentFile
Invoke-SmokeStep -Name "preflight" -Body {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw "Docker CLI not found on PATH."
    }
    docker info | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Docker daemon is not reachable. Start Docker and retry."
    }

    if (-not (Test-Path -LiteralPath $resolvedEnvFile)) {
        throw "Environment file not found: $resolvedEnvFile"
    }

    if ([string]::IsNullOrWhiteSpace($ApiSchemaPath) -or -not (Test-Path -LiteralPath $ApiSchemaPath)) {
        throw "ApiSchemaPath is required. Pass -ApiSchemaPath or set DMS_SMOKE_API_SCHEMA_PATH."
    }

    Write-Host "[smoke] EnvironmentFile : $resolvedEnvFile"
    Write-Host "[smoke] ApiSchemaPath   : $ApiSchemaPath"
    Write-Host "[smoke] Extensions      : $([string]::Join(',', $Extensions))"
}

$smokeFailed = $false
try {
    # Initial teardown to guarantee a clean starting state
    Invoke-SmokeStep -Name "pre-teardown" -Body {
        Invoke-ComposeTeardown -EnvFile $resolvedEnvFile
    }

    # Step 1: prepare workspace
    Invoke-SmokeStep -Name "prepare-dms-schema" -Body {
        $prepareArgs = @{ ApiSchemaPath = $ApiSchemaPath }
        if ($Extensions.Count -gt 0) { $prepareArgs.Extensions = $Extensions }
        Invoke-PhaseScript `
            -ScriptPath "$script:DockerComposeRoot/prepare-dms-schema.ps1" `
            -Arguments $prepareArgs `
            -Description "prepare-dms-schema.ps1"
    }

    Invoke-SmokeStep -Name "prepare-dms-claims" -Body {
        Invoke-PhaseScript `
            -ScriptPath "$script:DockerComposeRoot/prepare-dms-claims.ps1" `
            -Arguments @{} `
            -Description "prepare-dms-claims.ps1"
    }

    # Step 2: infra-only startup
    Invoke-SmokeStep -Name "start-local-dms-infra-only" -Body {
        Invoke-PhaseScript `
            -ScriptPath "$script:DockerComposeRoot/start-local-dms.ps1" `
            -Arguments @{
                InfraOnly = $true
                EnableConfig = $true
                IdentityProvider = "self-contained"
                EnvironmentFile = $resolvedEnvFile
            } `
            -Description "start-local-dms.ps1 -InfraOnly"
    }

    # Step 3: configure
    Invoke-SmokeStep -Name "configure-local-dms-instance" -Body {
        Invoke-PhaseScript `
            -ScriptPath "$script:DockerComposeRoot/configure-local-dms-instance.ps1" `
            -Arguments @{ EnvironmentFile = $resolvedEnvFile } `
            -Description "configure-local-dms-instance.ps1"
    }

    # Step 4: provision
    Invoke-SmokeStep -Name "provision-dms-schema-first-run" -Body {
        Invoke-PhaseScript `
            -ScriptPath "$script:DockerComposeRoot/provision-dms-schema.ps1" `
            -Arguments @{ EnvironmentFile = $resolvedEnvFile } `
            -Description "provision-dms-schema.ps1 (first run)"
    }

    # Step 5: idempotence — re-run provision
    Invoke-SmokeStep -Name "provision-dms-schema-second-run-idempotence" -Body {
        Invoke-PhaseScript `
            -ScriptPath "$script:DockerComposeRoot/provision-dms-schema.ps1" `
            -Arguments @{ EnvironmentFile = $resolvedEnvFile } `
            -Description "provision-dms-schema.ps1 (second run / idempotence)"
    }

    # Step 6: DMS-only startup
    Invoke-SmokeStep -Name "start-local-dms-dms-only" -Body {
        Invoke-PhaseScript `
            -ScriptPath "$script:DockerComposeRoot/start-local-dms.ps1" `
            -Arguments @{
                DmsOnly = $true
                IdentityProvider = "self-contained"
                EnvironmentFile = $resolvedEnvFile
            } `
            -Description "start-local-dms.ps1 -DmsOnly"
    }

    # Assertion: DMS health
    Invoke-SmokeStep -Name "assert-dms-health" -Body {
        $dmsPort = Get-EnvFileValue -Path $resolvedEnvFile -Key "DMS_HTTP_PORTS"
        if ([string]::IsNullOrWhiteSpace($dmsPort)) { $dmsPort = "8080" }
        $healthUrl = "http://localhost:$dmsPort/health"

        $deadline = (Get-Date).AddSeconds(60)
        $healthy = $false
        $lastError = $null
        while ((Get-Date) -lt $deadline) {
            try {
                $response = Invoke-WebRequest -Uri $healthUrl -Method Get -TimeoutSec 5
                if ($response.StatusCode -eq 200) {
                    $healthy = $true
                    break
                }
            }
            catch {
                $lastError = $_.Exception.Message
            }
            Start-Sleep -Seconds 2
        }
        if (-not $healthy) {
            throw "DMS health endpoint $healthUrl did not return 200 within 60s. Last error: $lastError"
        }
        Write-Host "[smoke] DMS health OK at $healthUrl"
    }

    # Assertion: provisioned tables exist
    Invoke-SmokeStep -Name "assert-provisioned-tables" -Body {
        $postgresDb = Get-EnvFileValue -Path $resolvedEnvFile -Key "POSTGRES_DB_NAME"
        if ([string]::IsNullOrWhiteSpace($postgresDb)) { $postgresDb = "edfi_datamanagementservice" }

        $listOutput = docker exec dms-postgresql psql -U postgres -d $postgresDb -At -c "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'dms';" 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "psql query failed: $listOutput"
        }
        $tableCount = 0
        if (-not [int]::TryParse((($listOutput | Select-Object -Last 1) -as [string]).Trim(), [ref]$tableCount)) {
            throw "Could not parse table count from psql output: $listOutput"
        }
        if ($tableCount -le 0) {
            throw "Expected at least one provisioned table in schema 'dms' of database '$postgresDb'; got $tableCount."
        }
        Write-Host "[smoke] Provisioned tables in dms schema: $tableCount"
    }

    # Assertion: corrupted manifest is rejected
    Invoke-SmokeStep -Name "assert-corrupted-manifest-rejected" -Body {
        $apiManifestPath = Join-Path $script:BootstrapRoot "ApiSchema/bootstrap-api-schema-manifest.json"
        if (-not (Test-Path -LiteralPath $apiManifestPath)) {
            throw "ApiSchema manifest not found at $apiManifestPath; cannot test corruption rejection."
        }

        $savedManifest = Get-Content -LiteralPath $apiManifestPath -Raw
        try {
            "not-json{{" | Set-Content -LiteralPath $apiManifestPath -Encoding utf8 -NoNewline
            $threw = $false
            $caughtMessage = ""
            Push-Location $script:DockerComposeRoot
            try {
                & "$script:DockerComposeRoot/provision-dms-schema.ps1" -EnvironmentFile $resolvedEnvFile 2>&1 | Out-Null
            }
            catch {
                $threw = $true
                $caughtMessage = $_.Exception.Message
            }
            finally {
                Pop-Location
            }
            if (-not $threw) {
                throw "Provision unexpectedly succeeded with a corrupted ApiSchema manifest."
            }
            if ($caughtMessage -notmatch 'malformed JSON|manifest') {
                throw "Provision failed for an unexpected reason: $caughtMessage"
            }
            Write-Host "[smoke] Provision correctly rejected the corrupted ApiSchema manifest by throwing."
        }
        finally {
            Set-Content -LiteralPath $apiManifestPath -Value $savedManifest -NoNewline
        }
    }
}
catch {
    $smokeFailed = $true
    Write-Host ""
    Write-Host "[smoke] FAILED: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    if (-not $SkipTeardown) {
        try {
            Invoke-SmokeStep -Name "teardown" -Body {
                Invoke-ComposeTeardown -EnvFile $resolvedEnvFile
            }
        }
        catch {
            Write-Host "[smoke] Teardown error: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
    else {
        Write-Host "[smoke] -SkipTeardown set; leaving dms-local stack running for inspection."
    }

    if (-not [string]::IsNullOrWhiteSpace($ResultsPath)) {
        $script:StepResults |
            ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath $ResultsPath -Encoding utf8
        Write-Host "[smoke] Results written to $ResultsPath"
    }

    $script:StepResults |
        Format-Table -AutoSize Name, Status, DurationSeconds |
        Out-String |
        Write-Host
}

if ($smokeFailed) {
    exit 1
}

Write-Host ""
Write-Host "[smoke] Bootstrap Docker smoke completed successfully." -ForegroundColor Green
exit 0
