# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Verifies that a generated database template package restores into a
    database that DMS can serve requests from.

.DESCRIPTION
    Extracts the .sql dump from the template .nupkg and restores it into a
    fresh verification database inside the running PostgreSQL container. The
    restored schema set must match the source database's user schemas, and
    core data (dms.EffectiveSchema, dms.Document, dms.Descriptor) must be
    non-empty; extension schemas may be DDL-only. Serveability is then proven
    end to end: a CMS DmsInstance and application bound only to the restored
    database are registered, DMS is restarted to clear its instance cache, and
    an authenticated descriptor read must return HTTP 200 with a non-empty
    array.

.PARAMETER SourceDatabaseName
    The relational database the template was dumped from; its user-schema set
    is the expected schema set after restore.

.PARAMETER PackageDirectory
    Directory containing the generated .nupkg (default: current directory).
#>

[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [string]$SourceDatabaseName,

    [string]$PackageDirectory = ".",

    [string]$VerificationDatabaseName = "template_restore_verification",

    [string]$ContainerName = "dms-postgresql",

    [string]$DmsContainerName = "dms-local-dms-1",

    [string]$DmsUrl = "http://localhost:8080",

    [string]$CmsUrl = "http://localhost:8081",

    [string]$PostgresPassword = $env:POSTGRES_PASSWORD ?? "abcdefgh1!"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-SafeDatabaseName {
    param([string]$DatabaseName)

    if ($DatabaseName -notmatch "^[A-Za-z0-9_]+$") {
        throw "Database name '$DatabaseName' contains unsupported characters."
    }
}

function Invoke-PsqlScalar {
    param(
        [string]$DatabaseName,
        [string]$Query
    )

    $value = & docker exec $ContainerName psql -U postgres -d $DatabaseName -tA -c $Query

    if ($LASTEXITCODE -ne 0) {
        throw "Query failed against database '$DatabaseName': $Query"
    }

    return [long](@($value) | Select-Object -First 1)
}

Assert-SafeDatabaseName -DatabaseName $SourceDatabaseName
Assert-SafeDatabaseName -DatabaseName $VerificationDatabaseName

$extractDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "template-restore-$([Guid]::NewGuid().ToString('N'))"

Push-Location $PSScriptRoot
try {
    Import-Module ./Template-Management.psm1 -Force
    Import-Module ../Dms-Management.psm1 -Force

    # --- Locate and extract the package ---
    $package = Get-ChildItem -Path $PackageDirectory -Filter *.nupkg | Select-Object -First 1

    if ($null -eq $package) {
        throw "No .nupkg found in '$PackageDirectory'."
    }

    Write-Host "Verifying template package: $($package.Name)" -ForegroundColor Cyan

    New-Item -ItemType Directory -Path $extractDirectory -Force | Out-Null
    $zipPath = Join-Path $extractDirectory "package.zip"
    Copy-Item $package.FullName $zipPath
    Expand-Archive -Path $zipPath -DestinationPath (Join-Path $extractDirectory "contents")

    $sqlFile = Get-ChildItem -Path (Join-Path $extractDirectory "contents") -Filter *.sql -Recurse | Select-Object -First 1

    if ($null -eq $sqlFile) {
        throw "No .sql dump found inside package '$($package.Name)'."
    }

    # --- Restore into a fresh verification database ---
    $expectedSchemas = @(Get-UserSchemaNames -ContainerName $ContainerName -DatabaseName $SourceDatabaseName)
    Write-Host "Expected schemas (from '$SourceDatabaseName'): $($expectedSchemas -join ', ')"

    & docker exec $ContainerName psql -U postgres -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS $VerificationDatabaseName;"
    if ($LASTEXITCODE -ne 0) { throw "Failed to drop existing verification database '$VerificationDatabaseName'." }

    & docker exec $ContainerName psql -U postgres -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $VerificationDatabaseName;"
    if ($LASTEXITCODE -ne 0) { throw "Failed to create verification database '$VerificationDatabaseName'." }

    & docker cp $sqlFile.FullName "$($ContainerName):/tmp/template-restore.sql"
    if ($LASTEXITCODE -ne 0) { throw "Failed to copy the dump into container '$ContainerName'." }

    & docker exec $ContainerName psql -U postgres -d $VerificationDatabaseName -v ON_ERROR_STOP=1 -f /tmp/template-restore.sql | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Restore of '$($sqlFile.Name)' into '$VerificationDatabaseName' failed." }

    # --- Structural and data assertions ---
    $restoredSchemas = @(Get-UserSchemaNames -ContainerName $ContainerName -DatabaseName $VerificationDatabaseName)
    $missingSchemas = @($expectedSchemas | Where-Object { $_ -cnotin $restoredSchemas })
    $unexpectedSchemas = @($restoredSchemas | Where-Object { $_ -cnotin $expectedSchemas })

    if ($missingSchemas.Count -gt 0 -or $unexpectedSchemas.Count -gt 0) {
        throw "Restored database schema set differs from source. Missing: $($missingSchemas -join ', '). Unexpected: $($unexpectedSchemas -join ', '). Expected: $($expectedSchemas -join ', '). Restored: $($restoredSchemas -join ', ')."
    }

    Write-Host "Restored schemas match: $($restoredSchemas -join ', ')" -ForegroundColor Green

    $effectiveSchemaCount = Invoke-PsqlScalar -DatabaseName $VerificationDatabaseName -Query 'SELECT COUNT(*) FROM dms."EffectiveSchema";'
    if ($effectiveSchemaCount -lt 1) { throw 'Restored dms."EffectiveSchema" is empty.' }

    $documentCount = Invoke-PsqlScalar -DatabaseName $VerificationDatabaseName -Query 'SELECT COUNT(*) FROM dms."Document";'
    if ($documentCount -lt 1) { throw 'Restored dms."Document" is empty.' }

    $descriptorCount = Invoke-PsqlScalar -DatabaseName $VerificationDatabaseName -Query 'SELECT COUNT(*) FROM dms."Descriptor";'
    if ($descriptorCount -lt 1) { throw 'Restored dms."Descriptor" is empty.' }

    Write-Host "Data assertions passed: EffectiveSchema=$effectiveSchemaCount, Document=$documentCount, Descriptor=$descriptorCount" -ForegroundColor Green

    # --- Serveability probe ---
    $cmsToken = Get-CmsToken -CmsUrl $CmsUrl

    $verificationInstanceId = Add-DmsInstance `
        -CmsUrl $CmsUrl `
        -AccessToken $cmsToken `
        -PostgresPassword $PostgresPassword `
        -PostgresDbName $VerificationDatabaseName `
        -InstanceName "Template Restore Verification" `
        -InstanceType "Verification"

    $vendorId = Add-Vendor -CmsUrl $CmsUrl -AccessToken $cmsToken -Company "Template Restore Verification Vendor"

    $application = Add-Application `
        -CmsUrl $CmsUrl `
        -AccessToken $cmsToken `
        -VendorId $vendorId `
        -ClaimSetName "EdFiSandbox" `
        -ApplicationName "Template Restore Verification" `
        -DmsInstanceIds @($verificationInstanceId)

    # Close the CMS/OpenIddict application-visibility race before requesting a token.
    Wait-CmsClientAvailable -CmsUrl $CmsUrl -ClientId $application.Key -ClientSecret $application.Secret

    # Restart DMS so the cached instance list includes the verification instance.
    & docker restart $DmsContainerName
    if ($LASTEXITCODE -ne 0) { throw "Failed to restart DMS container '$DmsContainerName'." }

    $deadline = [datetime]::UtcNow.AddSeconds(300)
    while ($true) {
        try {
            $healthResponse = Invoke-WebRequest -Uri "$($DmsUrl.TrimEnd('/'))/health" -Method Get -TimeoutSec 5 -ErrorAction Stop
            if ($healthResponse.StatusCode -eq 200) { break }
        }
        catch {
            $null = $_
        }

        if ([datetime]::UtcNow -ge $deadline) {
            throw "DMS did not become healthy within 300 seconds after restart."
        }

        Start-Sleep -Seconds 5
    }

    $dmsToken = Get-DmsToken -DmsUrl $DmsUrl -Key $application.Key -Secret $application.Secret
    $resourceUri = "$($DmsUrl.TrimEnd('/'))/data/ed-fi/academicSubjectDescriptors"
    $resourceResponse = Invoke-WebRequest -Uri $resourceUri -Headers @{ Authorization = "Bearer $dmsToken" } -Method Get -TimeoutSec 30

    if ($resourceResponse.StatusCode -ne 200) {
        throw "Serveability probe returned HTTP $($resourceResponse.StatusCode) from $resourceUri."
    }

    $descriptors = @($resourceResponse.Content | ConvertFrom-Json)

    if ($descriptors.Count -lt 1) {
        throw "Serveability probe returned an empty descriptor array from $resourceUri."
    }

    Write-Host "Serveability probe passed: $($descriptors.Count) academicSubjectDescriptors served from the restored database." -ForegroundColor Green
    Write-Host "Template restore verification succeeded." -ForegroundColor Green
}
finally {
    if (Test-Path $extractDirectory) {
        Remove-Item $extractDirectory -Recurse -Force
    }
    Pop-Location
}
