# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

function Get-SchemaPackagesFromEnv {
    param (
        [Parameter(Mandatory = $true)]
        [string]$EnvFilePath
    )

    $schemaJson = ""
    $isReadingSchema = $false

    # Read and process file
    Get-Content $EnvFilePath | ForEach-Object {
        $line = $_.Trim()

        if ($line -match "^\s*#") { return }  # Skip comments

        if (-not $isReadingSchema) {
            if ($line -match "^SCHEMA_PACKAGES\s*=\s*'(.*)$") {
                $schemaJson = $matches[1]
                $isReadingSchema = $true

                if ($schemaJson.Trim().EndsWith("'")) {
                    $schemaJson = $schemaJson.TrimEnd("'")
                    $isReadingSchema = $false
                }
            }
        }
        elseif ($isReadingSchema) {
            $schemaJson += "`n$line"
            if ($line.Trim().EndsWith("'")) {
                $schemaJson = $schemaJson.TrimEnd("'")
                $isReadingSchema = $false
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($schemaJson)) {
        Write-Error "SCHEMA_PACKAGES not found or empty in $EnvFilePath"
        return $null
    }

    try {
        return $schemaJson | ConvertFrom-Json
    }
    catch {
        Write-Error "Failed to parse SCHEMA_PACKAGES: $_"
        return $null
    }
}

function Get-InputFileList {
    param (
        [Parameter(Mandatory = $true)]
        [string]$EnvironmentFile
    )

    # Initial file list
    $inputFileList = @(
        "E2E-NameSpaceBasedClaimSet.json",
        "E2E-NoFurtherAuthRequiredClaimSet.json",
        "E2E-RelationshipsWithEdOrgsOnlyClaimSet.json"
    )

    $SchemaPackages = Get-SchemaPackagesFromEnv -EnvFilePath $EnvironmentFile
    if ($null -eq $SchemaPackages) {
        throw "Failed to get schema packages from environment file"
    }

    $SchemaPackages |
        Where-Object { $_.extensionName -and $_.extensionName.Trim() -ne "" } |
        ForEach-Object { $inputFileList += "$($_.extensionName)ExtensionResourceClaims.json" }

    return $inputFileList
}

function Invoke-CmsHierarchyTransform {
    param (
        [Parameter(Mandatory = $true)]
        [string]$InputFileListString
    )

    try {
        Push-Location ../CmsHierarchy/
        Write-Output "Loading extension resource claims..."
        dotnet build CmsHierarchy.csproj -c Release

        dotnet run --configuration Release --project "CmsHierarchy.csproj" --no-launch-profile -- --command Transform --input $InputFileListString --outputFormat ToFile --output ../docker-compose/SecurityMetadata.json --skipAuths "RelationshipsWithEdOrgsAndPeopleInverted;RelationshipsWithStudentsOnlyThroughResponsibility;RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes"
        Pop-Location
    }
    catch {
        Pop-Location
        throw
    }
}

function Get-EscapedJsonContent {
    param (
        [Parameter(Mandatory = $true)]
        [string]$JsonFilePath
    )

    # Read JSON content
    $jsonContent = Get-Content -Raw -Path $JsonFilePath
    return $jsonContent -replace "'", "''"
}

function AddExtensionSecurityMetadata {
    param (
        [string]$EnvironmentFile
    )

    try {
        $inputFileList = Get-InputFileList -EnvironmentFile $EnvironmentFile
        $inputFileListString = $inputFileList -join ";"
        Write-Host "Input file list: $inputFileListString"

        Invoke-CmsHierarchyTransform -InputFileListString $inputFileListString

        $updatedAuthorizationHierarchyFilePath = "SecurityMetadata.json"
        $escapedJson = Get-EscapedJsonContent -JsonFilePath $updatedAuthorizationHierarchyFilePath
        Remove-Item -Path $updatedAuthorizationHierarchyFilePath -ErrorAction SilentlyContinue

        $existingScriptFilePath = Join-Path -Path $PSScriptRoot -ChildPath "..\..\src\config\backend\EdFi.DmsConfigurationService.Backend.Postgresql\Deploy\Scripts\0011_Insert_ClaimsHierarchy.sql"
        $resolvedPath = Resolve-Path $existingScriptFilePath

        # Retain only the commented lines (lines starting with "--")
        $commentLines = Get-Content -Path $resolvedPath | Where-Object {
            $_.TrimStart().StartsWith("--")
        }

        # Define the new INSERT SQL
        $insertStatement = @(
            "`nINSERT INTO dmscs.claimshierarchy(",
            "`t hierarchy)",
            "`tVALUES ('$escapedJson'::jsonb);"
        ) -join "`n"

        # Combine comments and new insert
        $finalContent = ($commentLines -join "`n") + "`n" + $insertStatement

        # Write the updated file
        Set-Content -Path $resolvedPath -Value $finalContent -Encoding UTF8
    }
    catch {
        Write-Error "Failed to load extension resource claims: $_"
        exit 1
    }
}

function UpdateExtensionSecurityMetadata {
    param (
        [string]$EnvironmentFile
    )

    try {
        $inputFileList = Get-InputFileList -EnvironmentFile $EnvironmentFile
        $inputFileListString = $inputFileList -join ";"
        Write-Host "Input file list: $inputFileListString"

        Invoke-CmsHierarchyTransform -InputFileListString $inputFileListString

        $updatedAuthorizationHierarchyFilePath = "SecurityMetadata.json"
        $escapedJson = Get-EscapedJsonContent -JsonFilePath $updatedAuthorizationHierarchyFilePath
        Remove-Item -Path $updatedAuthorizationHierarchyFilePath -ErrorAction SilentlyContinue

        # Define the UPDATE SQL
        $updateStatement = "UPDATE dmscs.claimshierarchy SET hierarchy = '$escapedJson'::jsonb;"

        $envValues = ReadValuesFromEnvFile $EnvironmentFile
        $database = $envValues["POSTGRES_DB_NAME"]
        Write-Output $updateStatement | docker exec -i dms-postgresql psql -U postgres -d $database
    }
    catch {
        Write-Error "Failed to load extension resource claims: $_"
        exit 1
    }
}

Export-ModuleMember -Function AddExtensionSecurityMetadata, UpdateExtensionSecurityMetadata
