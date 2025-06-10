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
    $Port = $envValues["POSTGRES_PORT"]
    $User = "postgres"
    $Password = $envValues["POSTGRES_PASSWORD"]
    $Database = $envValues["POSTGRES_DB_NAME"]
    $DbHost = "localhost"
    $PackageId = $envValues["DATABASE_TEMPLATE_PACKAGE"]

    if ([string]::IsNullOrWhiteSpace($PackageId)) {
        $PackageId = "EdFi.Dms.Minimal.Template.PostgreSql.5.2.0"
        Write-Host -ForegroundColor Yellow "Environment variable DATABASE_TEMPLATE_PACKAGE is not set. Using default package: $PackageId"
    } else {
        Write-Host -ForegroundColor Green "Using package from environment variable: $PackageId"
    }

    Write-Output "Setting up database template with package: $PackageId"

    Import-Module ../Package-Management.psm1

    $pkgPath = Get-NugetPackage -PackageName $PackageId -PackageVersion $Version -PreRelease

    $sqlPath = Join-Path $pkgPath "$PackageId.sql"

    if (-Not (Test-Path $sqlPath)) {
        throw "Database script file not found at: $sqlPath"
    }

    $env:PGPASSWORD = $Password

    psql -h $dbHost -p $Port -U $User -d $Database -c "SELECT 1;" 2>$null

    if ($LASTEXITCODE -eq 0) {
        Write-Host "PostgreSQL is running. Proceeding with bootstrap..."

        psql -h $dbHost -p $Port -U $User -d $Database -f $sqlPath

        if ($LASTEXITCODE -eq 0) {
            Write-Host "Template data loaded successfully."
        } else {
            Write-Error "Failed to execute the database script file."
        }
    } else {
        Write-Error "PostgreSQL server is not running or unreachable."
    }

    Remove-Item Env:PGPASSWORD
}
