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

    docker exec dms-postgresql psql -U $User -d $Database -c "SELECT 1;" 2>$null


    if ($LASTEXITCODE -eq 0) {
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
           Write-Host "PostgreSQL Data Seed Load failed: existing volumes were detected. Please remove them before proceeding."
        }
    }
}
