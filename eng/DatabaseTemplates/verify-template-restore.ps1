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
    Extracts the database dump from the template .nupkg and restores it into a
    fresh verification database inside the running database container. The
    restored schema set must match the source database's user schemas, and
    core data (dms.EffectiveSchema, dms.Document, dms.Descriptor) must be
    non-empty; extension schemas may be DDL-only. Serveability is then proven
    end to end: a CMS data store and application bound only to the restored
    database are registered, DMS is restarted to clear its data store cache, and
    an authenticated descriptor read must return HTTP 200 with a non-empty
    array.

    With -RequirePopulatedData, the script additionally proves the package
    contains populated sample data: the source database must hold at least one
    non-descriptor, non-school-year document, populated counts must restore
    exactly (source-to-restored comparison, never hard-coded counts), and an
    authenticated read of a populated resource (schools) must return HTTP 200
    with a non-empty array.

.PARAMETER SourceDatabaseName
    The relational database the template was dumped from; its user-schema set
    is the expected schema set after restore.

.PARAMETER PackageDirectory
    Directory containing the generated .nupkg (default: current directory).

.PARAMETER DatabaseEngine
    "postgresql" or "mssql". Defaults to "postgresql". Selects the engine used
    to restore and inspect the verification database.

.PARAMETER MssqlPassword
    The MSSQL "sa" password. Defaults to environment variable MSSQL_SA_PASSWORD or "abcdefgh1!".

.PARAMETER RequirePopulatedData
    Additionally require populated (non-descriptor) sample data to survive the
    restore and be serveable by DMS.
#>

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Verification entry script intentionally writes operator progress to the console.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '', Justification = 'The PostgreSQL and MSSQL passwords are handed to a database connection string / sqlcmd where they must be plaintext; there is no companion username credential and a PSCredential adds no protection across that boundary.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The PostgreSQL and MSSQL passwords are read as plaintext from the environment and handed to psql/sqlcmd or a database connection string, where they must be plaintext; SecureString adds no protection across that boundary.')]
[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [string]$SourceDatabaseName,

    [string]$PackageDirectory = ".",

    [string]$VerificationDatabaseName = "template_restore_verification",

    [ValidateSet("postgresql", "mssql")]
    [string]$DatabaseEngine = "postgresql",

    [string]$ContainerName = $(if ($DatabaseEngine -eq "mssql") { "dms-mssql" } else { "dms-postgresql" }),

    [string]$DmsContainerName = "ed-fi-api",

    [string]$DmsUrl = "http://localhost:8080",

    [string]$CmsUrl = "http://localhost:8081",

    [string]$PostgresPassword = $env:POSTGRES_PASSWORD ?? "abcdefgh1!",

    [string]$MssqlPassword = $env:MSSQL_SA_PASSWORD ?? "abcdefgh1!",

    [switch]$RequirePopulatedData
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

function Invoke-SqlcmdScalar {
    param(
        [string]$DatabaseName,
        [string]$Query
    )

    $value = & docker exec $ContainerName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $MssqlPassword -d $DatabaseName -C -b -h -1 -W -Q $Query

    if ($LASTEXITCODE -ne 0) {
        throw "Query failed against database '$DatabaseName': $Query"
    }

    return [long](@($value) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)
}

Assert-SafeDatabaseName -DatabaseName $SourceDatabaseName
Assert-SafeDatabaseName -DatabaseName $VerificationDatabaseName

Push-Location $PSScriptRoot
try {
    Import-Module ./Template-Management.psm1 -Force
    Import-Module ../Dms-Management.psm1 -Force

    # --- Restore the package into a fresh verification database ---
    $expectedSchemas = @(Get-UserSchemaNames -DatabaseEngine $DatabaseEngine -ContainerName $ContainerName -DatabaseName $SourceDatabaseName -MssqlPassword $MssqlPassword)
    Write-Host "Expected schemas (from '$SourceDatabaseName'): $($expectedSchemas -join ', ')"

    $packageName = Restore-TemplatePackage -PackageDirectory $PackageDirectory -DatabaseName $VerificationDatabaseName -DatabaseEngine $DatabaseEngine -ContainerName $ContainerName -MssqlPassword $MssqlPassword
    Write-Host "Verifying template package: $packageName" -ForegroundColor Cyan

    # --- Structural and data assertions ---
    $restoredSchemas = @(Get-UserSchemaNames -DatabaseEngine $DatabaseEngine -ContainerName $ContainerName -DatabaseName $VerificationDatabaseName -MssqlPassword $MssqlPassword)
    $missingSchemas = @($expectedSchemas | Where-Object { $_ -cnotin $restoredSchemas })
    $unexpectedSchemas = @($restoredSchemas | Where-Object { $_ -cnotin $expectedSchemas })

    if ($missingSchemas.Count -gt 0 -or $unexpectedSchemas.Count -gt 0) {
        throw "Restored database schema set differs from source. Missing: $($missingSchemas -join ', '). Unexpected: $($unexpectedSchemas -join ', '). Expected: $($expectedSchemas -join ', '). Restored: $($restoredSchemas -join ', ')."
    }

    Write-Host "Restored schemas match: $($restoredSchemas -join ', ')" -ForegroundColor Green

    if ($DatabaseEngine -eq "mssql") {
        $effectiveSchemaCount = Invoke-SqlcmdScalar -DatabaseName $VerificationDatabaseName -Query 'SELECT COUNT(*) FROM [dms].[EffectiveSchema];'
        if ($effectiveSchemaCount -lt 1) { throw 'Restored [dms].[EffectiveSchema] is empty.' }

        $documentCount = Invoke-SqlcmdScalar -DatabaseName $VerificationDatabaseName -Query 'SELECT COUNT(*) FROM [dms].[Document];'
        if ($documentCount -lt 1) { throw 'Restored [dms].[Document] is empty.' }

        $descriptorCount = Invoke-SqlcmdScalar -DatabaseName $VerificationDatabaseName -Query 'SELECT COUNT(*) FROM [dms].[Descriptor];'
        if ($descriptorCount -lt 1) { throw 'Restored [dms].[Descriptor] is empty.' }
    }
    else {
        $effectiveSchemaCount = Invoke-PsqlScalar -DatabaseName $VerificationDatabaseName -Query 'SELECT COUNT(*) FROM dms."EffectiveSchema";'
        if ($effectiveSchemaCount -lt 1) { throw 'Restored dms."EffectiveSchema" is empty.' }

        $documentCount = Invoke-PsqlScalar -DatabaseName $VerificationDatabaseName -Query 'SELECT COUNT(*) FROM dms."Document";'
        if ($documentCount -lt 1) { throw 'Restored dms."Document" is empty.' }

        $descriptorCount = Invoke-PsqlScalar -DatabaseName $VerificationDatabaseName -Query 'SELECT COUNT(*) FROM dms."Descriptor";'
        if ($descriptorCount -lt 1) { throw 'Restored dms."Descriptor" is empty.' }
    }

    Write-Host "Data assertions passed: EffectiveSchema=$effectiveSchemaCount, Document=$documentCount, Descriptor=$descriptorCount" -ForegroundColor Green

    # --- Populated-data assertions (opt-in) ---
    # Counts are compared source-to-restored rather than hard-coded so the check is
    # robust to data-standard sample changes. The %SchoolYear% exclusion is broader
    # than the exact school-year resource name so school-year seed rows can never
    # satisfy the "more than minimal data" assertion.
    $probeSchoolId = $null

    if ($RequirePopulatedData) {
        if ($DatabaseEngine -eq "mssql") {
            $populatedDocumentCountQuery = @'
SELECT COUNT(*)
FROM [dms].[Document] d
JOIN [dms].[ResourceKey] rk ON rk.[ResourceKeyId] = d.[ResourceKeyId]
WHERE rk.[ResourceName] NOT LIKE '%Descriptor'
  AND rk.[ResourceName] NOT LIKE '%SchoolYear%';
'@

            $sourcePopulatedDocumentCount = Invoke-SqlcmdScalar -DatabaseName $SourceDatabaseName -Query $populatedDocumentCountQuery
            if ($sourcePopulatedDocumentCount -lt 1) {
                throw "Source database '$SourceDatabaseName' contains no populated (non-descriptor) documents; the populated bulk load did not land there."
            }

            $restoredPopulatedDocumentCount = Invoke-SqlcmdScalar -DatabaseName $VerificationDatabaseName -Query $populatedDocumentCountQuery
            if ($restoredPopulatedDocumentCount -ne $sourcePopulatedDocumentCount) {
                throw "Restored populated document count ($restoredPopulatedDocumentCount) does not match the source ($sourcePopulatedDocumentCount); the dump dropped populated rows."
            }

            $schoolCountQuery = 'SELECT COUNT(*) FROM [edfi].[School];'

            $sourceSchoolCount = Invoke-SqlcmdScalar -DatabaseName $SourceDatabaseName -Query $schoolCountQuery
            if ($sourceSchoolCount -lt 1) {
                throw "Source database '$SourceDatabaseName' contains no schools; populated sample data is incomplete."
            }

            $restoredSchoolCount = Invoke-SqlcmdScalar -DatabaseName $VerificationDatabaseName -Query $schoolCountQuery
            if ($restoredSchoolCount -ne $sourceSchoolCount) {
                throw "Restored school count ($restoredSchoolCount) does not match the source ($sourceSchoolCount); the dump dropped school rows."
            }

            $probeSchoolId = Invoke-SqlcmdScalar -DatabaseName $VerificationDatabaseName -Query 'SELECT TOP 1 [SchoolId] FROM [edfi].[School] ORDER BY [SchoolId];'
        }
        else {
            $populatedDocumentCountQuery = @'
SELECT COUNT(*)
FROM dms."Document" d
JOIN dms."ResourceKey" rk ON rk."ResourceKeyId" = d."ResourceKeyId"
WHERE rk."ResourceName" NOT LIKE '%Descriptor'
  AND rk."ResourceName" NOT ILIKE '%SchoolYear%';
'@

            $sourcePopulatedDocumentCount = Invoke-PsqlScalar -DatabaseName $SourceDatabaseName -Query $populatedDocumentCountQuery
            if ($sourcePopulatedDocumentCount -lt 1) {
                throw "Source database '$SourceDatabaseName' contains no populated (non-descriptor) documents; the populated bulk load did not land there."
            }

            $restoredPopulatedDocumentCount = Invoke-PsqlScalar -DatabaseName $VerificationDatabaseName -Query $populatedDocumentCountQuery
            if ($restoredPopulatedDocumentCount -ne $sourcePopulatedDocumentCount) {
                throw "Restored populated document count ($restoredPopulatedDocumentCount) does not match the source ($sourcePopulatedDocumentCount); the dump dropped populated rows."
            }

            $schoolCountQuery = 'SELECT COUNT(*) FROM edfi."School";'

            $sourceSchoolCount = Invoke-PsqlScalar -DatabaseName $SourceDatabaseName -Query $schoolCountQuery
            if ($sourceSchoolCount -lt 1) {
                throw "Source database '$SourceDatabaseName' contains no schools; populated sample data is incomplete."
            }

            $restoredSchoolCount = Invoke-PsqlScalar -DatabaseName $VerificationDatabaseName -Query $schoolCountQuery
            if ($restoredSchoolCount -ne $sourceSchoolCount) {
                throw "Restored school count ($restoredSchoolCount) does not match the source ($sourceSchoolCount); the dump dropped school rows."
            }

            $probeSchoolId = Invoke-PsqlScalar -DatabaseName $VerificationDatabaseName -Query 'SELECT "SchoolId" FROM edfi."School" ORDER BY "SchoolId" LIMIT 1;'
        }

        Write-Host "Populated data assertions passed: PopulatedDocuments=$restoredPopulatedDocumentCount, Schools=$restoredSchoolCount" -ForegroundColor Green
    }

    # --- Serveability probe ---
    $cmsToken = Get-CmsToken -CmsUrl $CmsUrl

    # Add-DataStore requires a PostgresCredential regardless of target engine; for MSSQL it goes
    # unused because -ConnectionString is supplied verbatim from New-DataStoreConnectionString.
    $postgresCredential = ConvertTo-PostgresCredential -UserName "postgres" -Secret $PostgresPassword

    $dataStoreConnectionString = ""
    if ($DatabaseEngine -eq "mssql") {
        $dataStoreConnectionString = New-DataStoreConnectionString `
            -DatabaseEngine "mssql" `
            -DbHost $ContainerName `
            -Port 1433 `
            -Username "sa" `
            -Password $MssqlPassword `
            -DatabaseName $VerificationDatabaseName
    }

    $verificationDataStoreId = Add-DataStore `
        -CmsUrl $CmsUrl `
        -AccessToken $cmsToken `
        -PostgresCredential $postgresCredential `
        -PostgresDbName $VerificationDatabaseName `
        -ConnectionString $dataStoreConnectionString `
        -Name "Template Restore Verification" `
        -DataStoreType "Verification"

    $vendorId = Add-Vendor -CmsUrl $CmsUrl -AccessToken $cmsToken -Company "Template Restore Verification Vendor"

    $applicationParams = @{
        CmsUrl          = $CmsUrl
        AccessToken     = $cmsToken
        VendorId        = $vendorId
        ClaimSetName    = "EdFiSandbox"
        ApplicationName = "Template Restore Verification"
        DataStoreIds    = @($verificationDataStoreId)
    }

    if ($RequirePopulatedData) {
        $applicationParams.EducationOrganizationIds = @($probeSchoolId)
    }

    $application = Add-Application @applicationParams

    # Close the CMS/OpenIddict application-visibility race before requesting a token.
    Wait-CmsClientAvailable -CmsUrl $CmsUrl -ClientId $application.Key -ClientSecret $application.Secret

    # Restart DMS so the cached data store list includes the verification data store.
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

    if ($RequirePopulatedData) {
        $schoolsUri = "$($DmsUrl.TrimEnd('/'))/data/ed-fi/schools"
        $schoolsResponse = Invoke-WebRequest -Uri $schoolsUri -Headers @{ Authorization = "Bearer $dmsToken" } -Method Get -TimeoutSec 30

        if ($schoolsResponse.StatusCode -ne 200) {
            throw "Populated serveability probe returned HTTP $($schoolsResponse.StatusCode) from $schoolsUri."
        }

        $schools = @($schoolsResponse.Content | ConvertFrom-Json)

        if ($schools.Count -lt 1) {
            throw "Populated serveability probe returned an empty schools array from $schoolsUri."
        }

        Write-Host "Populated serveability probe passed: $($schools.Count) schools served from the restored database." -ForegroundColor Green
    }

    Write-Host "Template restore verification succeeded." -ForegroundColor Green
}
finally {
    Pop-Location
}
