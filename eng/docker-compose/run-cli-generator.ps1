# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param (
    # Arguments to pass to the CLI
    [string[]]
    $CliArguments = @("--help"),

    # Path to the input file
    [string]
    $InputFile,

    # Path to the output folder
    [string]
    $OutputFolder,

    # URL to fetch the schema JSON
    [string]
    $SchemaUrl
)


# Validate OutputFolder
if (-not [string]::IsNullOrWhiteSpace($OutputFolder)) {
    if (-not (Test-Path $OutputFolder)) {
        try {
            New-Item -ItemType Directory -Path $OutputFolder -Force | Out-Null
        } catch {
            Write-Error "The specified output folder '$OutputFolder' could not be created."
            return
        }
    }
}

# Validate InputFile and SchemaUrl
if (-not [string]::IsNullOrWhiteSpace($InputFile) -and -not [string]::IsNullOrWhiteSpace($SchemaUrl)) {
    Write-Error "You cannot specify both InputFile and SchemaUrl. Please provide only one."
    return
}

if ([string]::IsNullOrWhiteSpace($InputFile) -and [string]::IsNullOrWhiteSpace($SchemaUrl) -and (($CliArguments.Count -ne 1) -or ($CliArguments[0] -ne "--help"))) {
    Write-Error "You must specify either InputFile or SchemaUrl."
    return
}

# Determine if we need to use Docker (when we have input file or output folder)
$UseDocker = (-not [string]::IsNullOrWhiteSpace($InputFile)) -or (-not [string]::IsNullOrWhiteSpace($OutputFolder)) -or (-not [string]::IsNullOrWhiteSpace($SchemaUrl))

# Clear default CLI arguments when we have real work to do
if (($CliArguments.Count -eq 1) -and ($CliArguments[0] -eq "--help") -and ($UseDocker -or (-not [string]::IsNullOrWhiteSpace($SchemaUrl)))) {
    $CliArguments = @()
}

# Add SchemaUrl to CLI arguments if provided
if (-not [string]::IsNullOrWhiteSpace($SchemaUrl)) {
    $CliArguments += "--url"
    $CliArguments += $SchemaUrl
}

# Run the CLI generator directly from the host or container
if ($UseDocker) {
    # Build docker run command with volume mounts
    $dockerArgs = @("run", "--rm")
    
    if (-not [string]::IsNullOrWhiteSpace($InputFile)) {
        $dockerArgs += "-v"
        $dockerArgs += "$($InputFile):/app/input/input-file.txt"
        $CliArguments += "-i"
        $CliArguments += "/app/input/input-file.txt"
    }
    
    if (-not [string]::IsNullOrWhiteSpace($OutputFolder)) {
        $dockerArgs += "-v"
        $dockerArgs += "$($OutputFolder):/app/output"
        $CliArguments += "-o"
        $CliArguments += "/app/output"
    }

    # Get the image name from docker-compose
    $imageName = "docker-compose-cli-generator"
    
    # Add the image name and CLI arguments
    $dockerArgs += $imageName
    $dockerArgs += $CliArguments

    Write-Host "Running: docker $($dockerArgs -join ' ')" -ForegroundColor Cyan
    & docker @dockerArgs
} else {
    dotnet run --project ./src/dms/clis/EdFi.DataManagementService.CliGenerator.csproj -- @CliArguments
}