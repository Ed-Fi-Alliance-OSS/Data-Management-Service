# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

function ReadValuesFromEnvFile {
    param (
        [string]$EnvironmentFile
    )

    if (-Not (Test-Path $EnvironmentFile)) {
        throw "Environment file not found: $EnvironmentFile"
    }
    $envFile = @{}
    Get-Content $EnvironmentFile | ForEach-Object {
        if ($_ -match "^\s*#") { return }
        $split = $_.Split('=')
        if ($split.Length -eq 2) {
            $key = $split[0].Trim()
            $value = $split[1].Trim()
            $envFile[$key] = $value
        }
    }
    return $envFile
}

function SetupMinimalTemplate {
    param (
        [string]$EnvironmentFile,
        [string]$PackageId = "EdFi.Dms.Minimal.Template.PostgreSql.5.2.0",
        [string]$SqlRelativePath = "EdFi.Dms.Minimal.Template.PostgreSql.5.2.0.sql",
        [string]$NuGetPath = $(Join-Path $PSScriptRoot "tools\nuget.exe"),
        [string]$Version = ""
    )

    $envValues = ReadValuesFromEnvFile $EnvironmentFile
    $Port = $envValues["POSTGRES_PORT"]
    $User = "postgres"
    $Password = $envValues["POSTGRES_PASSWORD"]
    $Database = $envValues["POSTGRES_DB_NAME"]
    $DbHost = "localhost"

    Import-Module ../Package-Management.psm1

    $pkgPath = Get-NugetPackage -PackageName $PackageId -PackageVersion $Version -PreRelease
    write-host "Package path: $pkgPath"

    $sqlPath = Join-Path $pkgPath $SqlRelativePath

    if (-Not (Test-Path $sqlPath)) {
        throw "Database script file not found at: $sqlPath"
    }

    $env:PGPASSWORD = $Password

    psql -h $dbHost -p $Port -U $User -d $Database -c "SELECT 1;" 2>$null

    if ($LASTEXITCODE -eq 0) {
        Write-Host "PostgreSQL is running. Proceeding with bootstrap..."

        psql -h $dbHost -p $Port -U $User -d $Database -f $sqlPath

        if ($LASTEXITCODE -eq 0) {
            Write-Host "Minimal template data loaded successfully."
        } else {
            Write-Error "Failed to execute the database script file."
        }
    } else {
        Write-Error "PostgreSQL server is not running or unreachable."
    }

    Remove-Item Env:PGPASSWORD
}
