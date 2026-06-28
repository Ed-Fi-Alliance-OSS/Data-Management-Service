# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Sets up the Ed-Fi DMS local Docker environment for Instance Management E2E testing
.DESCRIPTION
    This script starts the Docker stack and provisions the 3 route-context test databases.
    Tenant and instance creation is handled by the tests themselves.

    Extension schema packages (Sample, Homograph) are loaded through the file-based SCHEMA_PACKAGES path.
    The -AddExtensionSecurityMetadata switch activates Hybrid claims mode so extension
    claimset fragments are loaded from the AdditionalClaimsets directory mounted at
    /app/additional-claims. This is the non-bootstrap compatibility path; bootstrap mode
    activates staged schema and claims automatically when a manifest is present.

    The script runs:
    ./start-local-dms.ps1 -EnableKafkaUI -EnableConfig -EnvironmentFile ./.env.routeContext.e2e -r -IdentityProvider self-contained -AddExtensionSecurityMetadata
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Setup script is intentionally host-oriented and uses console progress output.')]
[CmdletBinding()]
param(
    [switch]
    $SkipDockerBuild,

    # Optional Ed-Fi Data Standard version (e.g. "5.2", "6.1") forwarded to start-local-dms.ps1.
    [string]
    $DataStandardVersion
)

function Get-RequiredRelationRegclass {
    <#
    .SYNOPSIS
    Returns the to_regclass value for a required relation. Throws when the query itself fails, so a
    failed psql invocation is never read as "relation absent".
    #>
    param(
        [string]$Database,
        [string]$QualifiedRelationName
    )

    $regclass = (
        docker exec dms-postgresql psql -U postgres -d $Database -tAc "SELECT to_regclass('$QualifiedRelationName');" `
            | Out-String
    ).Trim()

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to query database '$Database' for relation '$QualifiedRelationName' (psql exit code $LASTEXITCODE)."
    }

    return $regclass
}

function Assert-RelationalSchemaProvisioned {
    param(
        [string]$Database
    )

    $effectiveSchemaRowCount = (
        docker exec dms-postgresql psql -U postgres -d $Database -tAc 'SELECT COUNT(*) FROM dms."EffectiveSchema" WHERE "EffectiveSchemaSingletonId" = 1;' `
            | Out-String
    ).Trim()

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to query database '$Database' for the dms.EffectiveSchema singleton row (psql exit code $LASTEXITCODE)."
    }

    if ($effectiveSchemaRowCount -ne "1") {
        throw "Schema verification failed: expected one dms.EffectiveSchema singleton row in test database '$Database' but found '$effectiveSchemaRowCount'."
    }

    $requiredRelations = @(
        @{
            QualifiedName = '"dms"."EffectiveSchema"'
            Description = 'dms.EffectiveSchema'
        },
        @{
            QualifiedName = '"dms"."Document"'
            Description = 'dms.Document'
        },
        @{
            QualifiedName = '"edfi"."School"'
            Description = 'edfi.School'
        },
        @{
            QualifiedName = '"edfi"."Student"'
            Description = 'edfi.Student'
        }
    )

    foreach ($relation in $requiredRelations) {
        $regclass = Get-RequiredRelationRegclass `
            -Database $Database `
            -QualifiedRelationName $relation.QualifiedName

        if ([string]::IsNullOrWhiteSpace($regclass)) {
            throw "Schema verification failed: expected relational table '$($relation.Description)' in test database '$Database'."
        }
    }
}

Write-Host @"
Ed-Fi DMS Local Environment Setup for Instance Management E2E Testing
======================================================================
"@ -ForegroundColor Cyan

# Check if Docker is running
Write-Host "Checking Docker status..." -ForegroundColor Yellow
$dockerCheck = $null
try {
    $dockerCheck = docker version 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Docker command failed"
    }
}
catch {
    Write-Host ""
    Write-Error "Docker is not running or not installed. Please start Docker and try again."
    Write-Host ""
    Write-Host "Error details:" -ForegroundColor Red
    if ($dockerCheck) {
        Write-Host $dockerCheck -ForegroundColor Red
    } else {
        Write-Host $_.Exception.Message -ForegroundColor Red
    }
    exit 1
}
Write-Host "Docker is running" -ForegroundColor Green
Write-Host ""

# Store current location and navigate to docker-compose directory
$originalLocation = Get-Location
$dockerComposeDir = Join-Path $PSScriptRoot "../../../../eng/docker-compose"

try {
    Set-Location $dockerComposeDir

    $bootstrapDir = Join-Path $dockerComposeDir ".bootstrap"
    if (Test-Path -LiteralPath $bootstrapDir) {
        Write-Output "Removing stale .bootstrap workspace before file-based schema package E2E startup..."
        # Fail fast on cleanup errors: a stale manifest left here would trigger bootstrap mode
        # on the next start-local-dms.ps1 invocation and silently divert the E2E run.
        Remove-Item -LiteralPath $bootstrapDir -Recurse -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $bootstrapDir) {
            throw "Failed to remove stale .bootstrap workspace at '$bootstrapDir'. Resolve any file locks or permissions before re-running setup."
        }
    }

    Write-Host "Starting DMS environment with Instance Management E2E configuration..." -ForegroundColor Green
    Write-Host "Configuration:" -ForegroundColor Yellow
    Write-Host "  - Kafka UI: Enabled" -ForegroundColor Gray
    Write-Host "  - Configuration Service: Enabled" -ForegroundColor Gray
    Write-Host "  - Environment File: ./.env.routeContext.e2e" -ForegroundColor Gray
    Write-Host "  - Force Rebuild: $(if ($SkipDockerBuild) { "No" } else { "Yes" })" -ForegroundColor Gray
    Write-Host "  - Route Qualifiers: districtId, schoolYear" -ForegroundColor Cyan
    Write-Host "  - Identity Provider: self-contained" -ForegroundColor Gray
    Write-Output "  - Extension Security Metadata: Yes"
    Write-Host ""
    Write-Host "NOTE: Tenant and instance records will be created by tests" -ForegroundColor Yellow
    Write-Host ""

    Write-Output "Using file-based schema packages from .env.routeContext.e2e for E2E (non-bootstrap compatibility path)."

    $previousUseApiSchemaPath = [System.Environment]::GetEnvironmentVariable("USE_API_SCHEMA_PATH")
    $previousApiSchemaPath = [System.Environment]::GetEnvironmentVariable("API_SCHEMA_PATH")
    $previousSchemaPackages = [System.Environment]::GetEnvironmentVariable("SCHEMA_PACKAGES")
    try {
        # .env.routeContext.e2e carries the file-based ApiSchema package settings. Process
        # env values win over docker compose --env-file entries, so clear stale overrides
        # left by teardown or earlier bootstrap runs and let the env file provide
        # USE_API_SCHEMA_PATH, API_SCHEMA_PATH, and SCHEMA_PACKAGES.
        $env:USE_API_SCHEMA_PATH = $null
        $env:API_SCHEMA_PATH = $null
        $env:SCHEMA_PACKAGES = $null

        # Run the start script - NO instance creation
        if ($SkipDockerBuild) {
            ./start-local-dms.ps1 -EnableKafkaUI -EnableConfig -EnvironmentFile ./.env.routeContext.e2e -IdentityProvider self-contained -AddExtensionSecurityMetadata -DataStandardVersion $DataStandardVersion
        }
        else {
            ./start-local-dms.ps1 -EnableKafkaUI -EnableConfig -EnvironmentFile ./.env.routeContext.e2e -r -IdentityProvider self-contained -AddExtensionSecurityMetadata -DataStandardVersion $DataStandardVersion
        }
    }
    finally {
        if ($null -eq $previousUseApiSchemaPath) {
            $env:USE_API_SCHEMA_PATH = $null
        } else {
            $env:USE_API_SCHEMA_PATH = $previousUseApiSchemaPath
        }

        if ($null -eq $previousApiSchemaPath) {
            $env:API_SCHEMA_PATH = $null
        } else {
            $env:API_SCHEMA_PATH = $previousApiSchemaPath
        }

        if ($null -eq $previousSchemaPackages) {
            $env:SCHEMA_PACKAGES = $null
        } else {
            $env:SCHEMA_PACKAGES = $previousSchemaPackages
        }

    }

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to start DMS environment. Exit code: $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    # Provision the three test databases.
    Write-Host "`nProvisioning route-context test databases..." -ForegroundColor Cyan

    $databases = @(
        "edfi_datamanagementservice_d255901_sy2024",
        "edfi_datamanagementservice_d255901_sy2025",
        "edfi_datamanagementservice_d255902_sy2024"
    )

    $provisionE2EDatabaseScript = Join-Path $dockerComposeDir "provision-e2e-database.ps1"

    foreach ($db in $databases) {
        & $provisionE2EDatabaseScript `
            -EnvironmentFile ./.env.routeContext.e2e `
            -DatabaseName $db `
            -Configuration Release

        if ($LASTEXITCODE -ne 0) {
            throw "Failed to provision route-context database '$db' (exit code $LASTEXITCODE)."
        }

        Assert-RelationalSchemaProvisioned -Database $db
        Write-Host "  Provisioned and verified relational schema: $db" -ForegroundColor Green
    }

    Write-Host "`n========================================" -ForegroundColor Green
    Write-Host "Setup Complete!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "The following databases are ready:" -ForegroundColor Cyan
    foreach ($db in $databases) {
        Write-Host "  - $db" -ForegroundColor Gray
    }
    Write-Host ""
    Write-Host "To tear down this environment, run: ./teardown-local-dms.ps1" -ForegroundColor Cyan
}
finally {
    Set-Location $originalLocation
}
