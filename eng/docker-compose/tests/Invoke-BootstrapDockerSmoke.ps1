# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    DMS-1151/DMS-1154 Docker smoke: real bootstrap sequence against a live Docker stack.

.DESCRIPTION
    Manual end-to-end smoke for the DMS-1151 bootstrap contract. Exercises the real
    phase commands (prepare -> infra -> configure -> provision -> re-run -> DMS-only)
    against an actual Docker stack, not the synthetic Pester fixtures.

    The script is intentionally MANUAL: it is not wired into CI and is not run by the
    normal Pester suite. The reviewer for DMS-1151 explicitly asked for at least one
    real Docker pass before sign-off; this script formalizes that pass so it is
    repeatable and observable.

    Default input mode: when -ApiSchemaPath is not provided (and DMS_SMOKE_API_SCHEMA_PATH
    is not set), the smoke downloads the published asset-only core ApiSchema NuGet package
    from the Ed-Fi feed, extracts it to a temp directory, and uses
    contentFiles/any/any/ApiSchema inside the archive as the effective ApiSchemaPath.
    This exercises a real published package payload rather than pre-staged repo files.
    Note: this download is test-harness input acquisition only — bootstrap package
    resolution at runtime remains the scope of Story 06.

    -ApiSchemaPath (or DMS_SMOKE_API_SCHEMA_PATH) overrides the default and points the
    smoke at caller-supplied loose files instead of the downloaded package.

    Prerequisites:
      - Docker daemon running.
      - PowerShell 7+ (pwsh).
      - Network access to the Ed-Fi NuGet feed (for the default package-download mode)
        OR a directory containing the ApiSchema content passed via -ApiSchemaPath or
        DMS_SMOKE_API_SCHEMA_PATH.
      - An env file (defaults to eng/docker-compose/.env.example).

    Sequence:
      0. Preflight checks and pre-teardown of any leftover dms-local stack.
         If -ApiSchemaPath is not set, downloads and extracts the core ApiSchema package
         (step: download-core-apischema-package) to acquire the effective ApiSchemaPath.
      1. prepare-dms-schema.ps1 + prepare-dms-claims.ps1 materialize .bootstrap/.
      2. start-local-dms.ps1 -InfraOnly -EnableConfig -IdentityProvider self-contained.
      3. configure-local-data-store.ps1 registers the data store(s).
      4. provision-dms-schema.ps1 provisions the selected target databases.
      5. Re-run step 4 to confirm provision is safely re-runnable (idempotent re-invocation;
         the re-run is asserted to complete without error - it does not diff schema state).
      6. Corrupt the staged ApiSchema manifest and confirm provision rejects it by throwing,
         then restore the manifest. This negative test runs before DMS is started.
      7. start-local-dms.ps1 -DmsOnly to start DMS against the provisioned database.
      8. Assertions:
           - DMS /health returns 200 within timeout.
           - psql lists at least one table in the provisioned database.
      9. Teardown (unless -SkipTeardown). Temp download/extract directory is always
         removed in the finally block regardless of -SkipTeardown (which applies only
         to the Docker stack and .bootstrap/ workspace).

    Exit code: 0 on full success, non-zero on the first failing assertion or step.

.PARAMETER EnvironmentFile
    Env file to forward to every phase. Defaults to eng/docker-compose/.env.example.

.PARAMETER ApiSchemaPath
    Directory containing the ApiSchema content for prepare-dms-schema.ps1. Falls back
    to $env:DMS_SMOKE_API_SCHEMA_PATH. When neither is set, the smoke downloads the
    published core ApiSchema package identified by -SchemaPackageId/-SchemaPackageVersion
    from -SchemaPackageFeedUrl and uses its contentFiles/any/any/ApiSchema directory.

.PARAMETER SchemaPackageId
    NuGet package ID to download when -ApiSchemaPath is not supplied.
    Defaults to EdFi.DataStandard52.ApiSchema.

.PARAMETER SchemaPackageVersion
    NuGet package version to download when -ApiSchemaPath is not supplied.
    Defaults to 1.0.333.

.PARAMETER SchemaPackageFeedUrl
    NuGet v3 feed index URL used to resolve the package download URL.
    Defaults to the Ed-Fi Alliance OSS feed.

.PARAMETER ResultsPath
    Optional path; if supplied, writes a JSON summary of the run (step status + timings).

.PARAMETER SkipTeardown
    When set, leaves the dms-local stack and .bootstrap/ workspace in place after the
    run. Useful when debugging an assertion failure interactively. The temp package
    download directory is always cleaned up regardless of this flag.

.EXAMPLE
    # Default: downloads the published core ApiSchema package automatically
    pwsh ./Invoke-BootstrapDockerSmoke.ps1

.EXAMPLE
    # Override with caller-supplied loose files
    pwsh ./Invoke-BootstrapDockerSmoke.ps1 `
        -ApiSchemaPath "$HOME/edfi/ApiSchema"
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Manual smoke script intentionally writes operator progress and step banners to the console.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'False positive: parameters are consumed inside nested script blocks and helper functions.')]
[CmdletBinding()]
param(
    [string]$EnvironmentFile,

    [string]$ApiSchemaPath = $env:DMS_SMOKE_API_SCHEMA_PATH,

    [string]$SchemaPackageId = "EdFi.DataStandard52.ApiSchema",

    [string]$SchemaPackageVersion = "1.0.333",

    [string]$SchemaPackageFeedUrl = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json",

    [string]$ResultsPath,

    [switch]$SkipTeardown
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:DockerComposeRoot = Split-Path -Parent $PSScriptRoot
$script:RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $script:DockerComposeRoot "../.."))
$script:BootstrapRoot = Join-Path $script:DockerComposeRoot ".bootstrap"
$script:StepResults = [System.Collections.Generic.List[pscustomobject]]::new()

# Temp directory created by the package-download step; cleaned up in the finally block.
$script:PackageDownloadTempDir = $null

function Format-LogSafeText {
    param($Value)

    if ($null -eq $Value) { return "" }
    $text = [string]$Value
    $builder = [System.Text.StringBuilder]::new()
    foreach ($character in $text.ToCharArray()) {
        if ([char]::IsLetterOrDigit($character) -or
            $character -eq " " -or
            $character -eq "_" -or
            $character -eq "-" -or
            $character -eq "." -or
            $character -eq ":" -or
            $character -eq "/") {
            $null = $builder.Append($character)
        }
    }

    return $builder.ToString()
}

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
        throw "Environment file not found: $(Format-LogSafeText $resolvedEnvFile)"
    }

    Write-Host "[smoke] EnvironmentFile : $(Format-LogSafeText $resolvedEnvFile)"
    if ([string]::IsNullOrWhiteSpace($ApiSchemaPath)) {
        Write-Host "[smoke] ApiSchemaPath   : (not set — will download package $(Format-LogSafeText $SchemaPackageId) $(Format-LogSafeText $SchemaPackageVersion))"
    }
    else {
        Write-Host "[smoke] ApiSchemaPath   : $(Format-LogSafeText $ApiSchemaPath)"
    }
}

$smokeFailed = $false
try {
    # Download the published core ApiSchema package when no ApiSchemaPath was supplied.
    if ([string]::IsNullOrWhiteSpace($ApiSchemaPath)) {
        Invoke-SmokeStep -Name "download-core-apischema-package" -Body {
            $packageIdLower = $SchemaPackageId.ToLowerInvariant()

            # Resolve PackageBaseAddress from the v3 feed index.
            $packageBaseAddress = $null
            try {
                $indexResponse = Invoke-RestMethod -Uri $SchemaPackageFeedUrl -TimeoutSec 30 -ErrorAction Stop
                $packageBaseResource = $indexResponse.resources |
                    Where-Object { $_.'@type' -like 'PackageBaseAddress/3.0.0' } |
                    Select-Object -First 1
                if ($null -ne $packageBaseResource) {
                    $packageBaseAddress = $packageBaseResource.'@id'
                }
            }
            catch {
                Write-Host "[smoke] Feed index lookup failed ($(Format-LogSafeText $SchemaPackageFeedUrl)); will derive flat2 URL from index URL."
            }

            if ([string]::IsNullOrWhiteSpace($packageBaseAddress)) {
                # Fallback: replace /v3/index.json with /v3/flat2/
                $packageBaseAddress = $SchemaPackageFeedUrl -replace '/v3/index\.json$', '/v3/flat2/'
            }

            $baseAddr = $packageBaseAddress.TrimEnd('/')
            $downloadUrl = "$baseAddr/$packageIdLower/$SchemaPackageVersion/$packageIdLower.$SchemaPackageVersion.nupkg"
            Write-Host "[smoke] Downloading $(Format-LogSafeText $SchemaPackageId) $(Format-LogSafeText $SchemaPackageVersion) from $(Format-LogSafeText $downloadUrl)"

            $tempDir = [System.IO.Path]::Combine(
                [System.IO.Path]::GetTempPath(),
                "dms-smoke-apischema-$([System.Guid]::NewGuid().ToString('N'))"
            )
            New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
            $script:PackageDownloadTempDir = $tempDir

            $zipPath = Join-Path $tempDir "package.zip"
            try {
                Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -TimeoutSec 300 -ErrorAction Stop
            }
            catch {
                throw "Failed to download ApiSchema package from $(Format-LogSafeText $downloadUrl): $(Format-LogSafeText $_.Exception.Message)"
            }

            $extractDir = Join-Path $tempDir "extracted"
            # Expand-Archive requires a .zip extension; we renamed to .zip above.
            Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force

            $apiSchemaDir = Join-Path $extractDir "contentFiles/any/any/ApiSchema"
            if (-not (Test-Path -LiteralPath $apiSchemaDir)) {
                throw "contentFiles/any/any/ApiSchema not found in downloaded package at $(Format-LogSafeText $apiSchemaDir). Package layout may have changed."
            }

            $apiSchemaJson = Join-Path $apiSchemaDir "ApiSchema.json"
            if (-not (Test-Path -LiteralPath $apiSchemaJson)) {
                throw "ApiSchema.json not found inside $(Format-LogSafeText $apiSchemaDir). Package may be incomplete."
            }

            $xsdFiles = @(Get-ChildItem -LiteralPath $apiSchemaDir -Recurse -Filter "*.xsd" -ErrorAction SilentlyContinue)
            Write-Host "[smoke] Package extracted to $(Format-LogSafeText $apiSchemaDir) — ApiSchema.json present, $($xsdFiles.Count) xsd file(s) found."

            # Use the extracted directory as the effective ApiSchemaPath for subsequent steps.
            $script:EffectiveApiSchemaPath = $apiSchemaDir
        }
    }
    else {
        $script:EffectiveApiSchemaPath = $ApiSchemaPath
    }

    # Initial teardown to guarantee a clean starting state
    Invoke-SmokeStep -Name "pre-teardown" -Body {
        Invoke-ComposeTeardown -EnvFile $resolvedEnvFile
    }

    # Step 1: prepare workspace
    Invoke-SmokeStep -Name "prepare-dms-schema" -Body {
        $prepareArgs = @{ ApiSchemaPath = $script:EffectiveApiSchemaPath }
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
    Invoke-SmokeStep -Name "configure-local-data-store" -Body {
        Invoke-PhaseScript `
            -ScriptPath "$script:DockerComposeRoot/configure-local-data-store.ps1" `
            -Arguments @{ EnvironmentFile = $resolvedEnvFile } `
            -Description "configure-local-data-store.ps1"
    }

    # Step 4: provision
    Invoke-SmokeStep -Name "provision-dms-schema-first-run" -Body {
        Invoke-PhaseScript `
            -ScriptPath "$script:DockerComposeRoot/provision-dms-schema.ps1" `
            -Arguments @{ EnvironmentFile = $resolvedEnvFile } `
            -Description "provision-dms-schema.ps1 (first run)"
    }

    # Step 5: idempotence - re-run provision
    Invoke-SmokeStep -Name "provision-dms-schema-second-run-idempotence" -Body {
        Invoke-PhaseScript `
            -ScriptPath "$script:DockerComposeRoot/provision-dms-schema.ps1" `
            -Arguments @{ EnvironmentFile = $resolvedEnvFile } `
            -Description "provision-dms-schema.ps1 (second run / idempotence)"
    }

    # Step 6: negative test - a corrupted ApiSchema manifest must be rejected before DMS starts
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

    # Step 7: DMS-only startup
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

    # Assertion: metadata endpoints and staged XSD content
    Invoke-SmokeStep -Name "assert-metadata-endpoints" -Body {
        $dmsPort = Get-EnvFileValue -Path $resolvedEnvFile -Key "DMS_HTTP_PORTS"
        if ([string]::IsNullOrWhiteSpace($dmsPort)) { $dmsPort = "8080" }
        $pathBase = Get-EnvFileValue -Path $resolvedEnvFile -Key "PATH_BASE"
        if ([string]::IsNullOrWhiteSpace($pathBase)) { $pathBase = "api" }
        $dmsBase = "http://localhost:$dmsPort/$pathBase"

        # Assert discovery root, specifications listing, and XSD listing return HTTP 200
        foreach ($endpoint in @("$dmsBase/", "$dmsBase/metadata/specifications", "$dmsBase/metadata/xsd")) {
            $response = $null
            try {
                $response = Invoke-WebRequest -Uri $endpoint -Method Get -TimeoutSec 10 -ErrorAction Stop
            }
            catch {
                throw "GET $endpoint failed: $($_.Exception.Message)"
            }
            if ($response.StatusCode -ne 200) {
                throw "GET $endpoint returned HTTP $($response.StatusCode); expected 200."
            }
            Write-Host "[smoke] GET $endpoint -> $($response.StatusCode) OK"
        }

        # Assert zero *.ApiSchema.dll files in the staged workspace (acceptance criterion 9)
        $apiSchemaWorkspace = Join-Path $script:BootstrapRoot "ApiSchema"
        if (Test-Path -LiteralPath $apiSchemaWorkspace) {
            $dllFiles = @(Get-ChildItem -LiteralPath $apiSchemaWorkspace -Recurse -Filter "*.ApiSchema.dll" -ErrorAction SilentlyContinue)
            if ($dllFiles.Count -ne 0) {
                throw "Staged workspace contains $($dllFiles.Count) *.ApiSchema.dll file(s); bootstrap mode must use the file-based workspace with no DLL assemblies. Files found: $($dllFiles.FullName -join ', ')"
            }
            Write-Host "[smoke] Staged workspace contains zero *.ApiSchema.dll files (AC9 verified)."
        }
        else {
            Write-Host "[smoke] Staged workspace not found at $apiSchemaWorkspace; skipping *.ApiSchema.dll check."
        }

        # Read the staged API schema manifest to assert per-project XSD content endpoints
        $apiSchemaManifestPath = Join-Path $script:BootstrapRoot "ApiSchema/bootstrap-api-schema-manifest.json"
        if (-not (Test-Path -LiteralPath $apiSchemaManifestPath)) {
            throw "ApiSchema manifest not found at $apiSchemaManifestPath; cannot assert per-project XSD endpoints."
        }

        $apiSchemaManifest = Get-Content -LiteralPath $apiSchemaManifestPath -Raw | ConvertFrom-Json
        $projectsWithXsd = @($apiSchemaManifest.projects | Where-Object { $_.PSObject.Properties.Name -contains "xsdDirectory" -and -not [string]::IsNullOrWhiteSpace($_.xsdDirectory) })

        if ($projectsWithXsd.Count -eq 0) {
            Write-Host "[smoke] No project in the staged manifest has an xsdDirectory; XSD file assertions skipped (JSON-only input)."
        }
        else {
            foreach ($project in $projectsWithXsd) {
                $projectNameLower = $project.projectName.ToLowerInvariant()
                $filesUrl = "$dmsBase/metadata/xsd/$projectNameLower/files"

                # GET /metadata/xsd/<project>/files -> 200 and non-empty JSON array
                $filesResponse = $null
                try {
                    $filesResponse = Invoke-WebRequest -Uri $filesUrl -Method Get -TimeoutSec 10 -ErrorAction Stop
                }
                catch {
                    throw "GET $filesUrl failed: $($_.Exception.Message)"
                }
                if ($filesResponse.StatusCode -ne 200) {
                    throw "GET $filesUrl returned HTTP $($filesResponse.StatusCode); expected 200."
                }
                $fileList = $filesResponse.Content | ConvertFrom-Json
                if ($null -eq $fileList -or $fileList.Count -eq 0) {
                    throw "GET $filesUrl returned an empty JSON array; expected at least one XSD file URL."
                }
                Write-Host "[smoke] GET $filesUrl -> $($filesResponse.StatusCode) OK ($($fileList.Count) file(s))"

                # Take the first file URL and GET it -> 200 with non-empty body
                $firstFileUrl = $fileList[0]
                $firstFileResponse = $null
                try {
                    $firstFileResponse = Invoke-WebRequest -Uri $firstFileUrl -Method Get -TimeoutSec 10 -ErrorAction Stop
                }
                catch {
                    throw "GET $firstFileUrl failed: $($_.Exception.Message)"
                }
                if ($firstFileResponse.StatusCode -ne 200) {
                    throw "GET $firstFileUrl returned HTTP $($firstFileResponse.StatusCode); expected 200."
                }
                if ([string]::IsNullOrWhiteSpace($firstFileResponse.Content)) {
                    throw "GET $firstFileUrl returned an empty body; expected XSD file content."
                }
                Write-Host "[smoke] GET $firstFileUrl -> $($firstFileResponse.StatusCode) OK ($(($firstFileResponse.Content).Length) bytes)"
            }
        }
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

    # Always clean up the package download temp directory (independent of -SkipTeardown,
    # which applies only to the Docker stack and .bootstrap/ workspace).
    if ($null -ne $script:PackageDownloadTempDir -and (Test-Path -LiteralPath $script:PackageDownloadTempDir)) {
        try {
            Remove-Item -LiteralPath $script:PackageDownloadTempDir -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "[smoke] Package download temp directory removed."
        }
        catch {
            Write-Host "[smoke] Warning: could not remove temp directory $(Format-LogSafeText $script:PackageDownloadTempDir): $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($ResultsPath)) {
        $script:StepResults |
            ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath $ResultsPath -Encoding utf8
        Write-Host "[smoke] Results written to $(Format-LogSafeText $ResultsPath)"
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
