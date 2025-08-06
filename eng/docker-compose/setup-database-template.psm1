# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

function LoadSeedData {
    param (
        [string]$EnvironmentFile
    )

    Import-Module ./env-utility.psm1

    $envValues = ReadValuesFromEnvFile $EnvironmentFile
    $User = "postgres"
    $Database = $envValues["POSTGRES_DB_NAME"]
    $PackageId = $envValues["DATABASE_TEMPLATE_PACKAGE"]

    Write-Host -ForegroundColor Cyan "Loading database template for database '$Database' using package '$PackageId'"

    if ([string]::IsNullOrWhiteSpace($PackageId)) {
        $PackageId = "EdFi.Dms.Minimal.Template.PostgreSql.5.2.0"
        Write-Host -ForegroundColor Yellow "Environment variable DATABASE_TEMPLATE_PACKAGE is not set. Using default package: $PackageId"
    } else {
        Write-Host -ForegroundColor Green "Using package from environment variable: $PackageId"
    }

    Write-Host -ForegroundColor Cyan "Setting up database template with package: $PackageId"

    Import-Module ../Package-Management.psm1

    $pkgPath = Get-NugetPackage -PackageName $PackageId -PackageVersion $Version -PreRelease

    $sqlPath = Join-Path $pkgPath "$PackageId.sql"

    if (-Not (Test-Path $sqlPath)) {
        throw "Database script file not found at: $sqlPath"
    }

    # Wait for PostgreSQL to be ready with retries and detailed logging
    Write-Host -ForegroundColor Cyan "Waiting for PostgreSQL to be ready..."
    $maxAttempts = 30
    $attempt = 0
    $isReady = $false

    do {
        Write-Host "Attempt $($attempt + 1)/$maxAttempts`: Checking PostgreSQL connection..."
        
        # Check if container is running first
        $containerStatus = docker ps --filter "name=dms-postgresql" --format "{{.Status}}"
        if ([string]::IsNullOrWhiteSpace($containerStatus)) {
            Write-Host -ForegroundColor Red "PostgreSQL container 'dms-postgresql' is not running"
            docker ps -a --filter "name=postgres" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
        } else {
            Write-Host -ForegroundColor Yellow "Container status: $containerStatus"
        }

        # Test database connection
        docker exec dms-postgresql psql -U $User -d $Database -c "SELECT 1;" 2>$null
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host -ForegroundColor Green "✅ PostgreSQL is ready!"
            $isReady = $true
            break
        } else {
            Write-Host -ForegroundColor Yellow "PostgreSQL not ready yet (exit code: $LASTEXITCODE)"
            
            # Show container logs for debugging on first attempt and every 10th attempt
            if ($attempt -eq 0 -or $attempt % 10 -eq 0) {
                Write-Host -ForegroundColor Cyan "Recent PostgreSQL logs:"
                docker logs --tail 5 dms-postgresql 2>$null
            }
        }
        
        if ($attempt -lt ($maxAttempts - 1)) {
            Write-Host "Waiting 10 seconds before next attempt..."
            Start-Sleep -Seconds 10
        }
        $attempt++
    } while ($attempt -lt $maxAttempts)

    if ($isReady) {
        Write-Host "PostgreSQL is running. Proceeding with bootstrap..."
        $schemaName = "dms"
        $schemaExists = docker exec dms-postgresql psql -U $User -d $Database -tAc "SELECT 1 FROM pg_namespace WHERE nspname = '$schemaName';"
        if (-not $schemaExists) {
            docker cp $sqlPath "dms-postgresql:/tmp/restore.sql"
            docker exec dms-postgresql psql -U postgres -d $database -f /tmp/restore.sql

            if ($LASTEXITCODE -eq 0) {
                Write-Host "Template data loaded successfully."
            } else {
                Write-Error "Failed to execute the database script file."
            }
        }
        else {
           Write-Warning "PostgreSQL Data Seed Load failed: existing volumes were detected with data loaded. Please remove them before proceeding."
        }
    } else {
        Write-Error "PostgreSQL server failed to become ready after $maxAttempts attempts (5 minutes total)"
        Write-Host -ForegroundColor Red "Expected container: dms-postgresql, Database: $Database, User: $User"
        
        Write-Host -ForegroundColor Cyan "Current PostgreSQL containers:"
        docker ps -a --filter "name=postgresql" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
        
        Write-Host -ForegroundColor Cyan "Recent container logs:"
        docker logs --tail 15 dms-postgresql 2>$null
        
        throw "PostgreSQL connection failed - check container status and logs above"
    }
}
