# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

function AddExtensionSecurityMetadata {
    param (
        [string]$EnvironmentFile
    )

    Import-Module ./env-utility.psm1
    $envValues = ReadValuesFromEnvFile $EnvironmentFile

    try {

    $schemaPackagesJson = $envValues["SCHEMA_PACKAGES"]

    if(!$schemaPackagesJson)
    {
        Write-Error "SCHEMA_PACKAGES environment variable is not set. Please set it to the path of the schema packages JSON file."
        exit 1
    }
    # Parse JSON list
    $schemaPackages = $schemaPackagesJson | ConvertFrom-Json

    # Initial file list
    $inputFileList = @(
        "E2E-NameSpaceBasedClaimSet.json",
        "E2E-NoFurtherAuthRequiredClaimSet.json",
        "E2E-RelationshipsWithEdOrgsOnlyClaimSet.json"
    )
    # Add extension claim files if corresponding packages exist
    $schemaPackages | ForEach-Object {
        switch -Wildcard ($_.name) {
            "*Sample*"    { $inputFileList += "SampleExtensionResourceClaims.json" }
            "*Homograph*" { $inputFileList += "HomographExtensionResourceClaims.json" }
        }
    }

    # Join into a single string with semicolons
    $inputFileListString = $inputFileList -join ";"

    Push-Location ../CmsHierarchy/
    Write-Output "Loading extension resource claims..."
    dotnet build CmsHierarchy.csproj -c Release

    dotnet run --configuration Release --project "CmsHierarchy.csproj" --no-launch-profile -- --command Transform --input $inputFileListString --outputFormat ToFile --output ../docker-compose/SecurityMetadata.json --skipAuths "RelationshipsWithEdOrgsAndPeopleInverted;RelationshipsWithStudentsOnlyThroughResponsibility;RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes"
    Pop-Location

    $updatedAuthorizationHierarchyFilePath = "SecurityMetadata.json"

    # Read JSON content
    $jsonContent = Get-Content -Raw -Path $updatedAuthorizationHierarchyFilePath
    $escapedJson = $jsonContent -replace "'", "''"

    $existingScriptFilePath = Join-Path -Path $PSScriptRoot -ChildPath "..\..\src\config\backend\EdFi.DmsConfigurationService.Backend.Postgresql\Deploy\Scripts\0011_Insert_ClaimsHierarchy.sql"
    $resolvedPath = Resolve-Path $existingScriptFilePath

    # Read original .sql file content
    $originalLines = Get-Content -Raw -Path $resolvedPath

    # Retain only the commented lines (lines starting with "--")
    $commentLines = Get-Content -Path $resolvedPath | Where-Object {
        $_.TrimStart().StartsWith("--")
    }

# Define the new INSERT SQL
$insertStatement = @"
INSERT INTO dmscs.claimshierarchy(
     hierarchy)
VALUES ('$escapedJson'::jsonb);
"@.Trim()

    # Combine comments and new insert
    $finalContent = $commentLines + "" + $insertStatement

    # Write the updated file
    Set-Content -Path $resolvedPath -Value $finalContent -Encoding UTF8
    }
    catch {
        Write-Error "Failed to load extension resource claims: $_"
        exit 1
    }
    finally {
        Pop-Location
    }
}
