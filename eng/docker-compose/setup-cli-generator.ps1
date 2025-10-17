# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param (
    # Stop and remove containers instead of starting them
    [Switch]
    $Down,

    # Force rebuild of the container
    [Switch]
    $Rebuild
)

$ComposeFile = "cli-generator.yml"

if ($Down) {
    Write-Host "Stopping and removing CLI generator containers..." -ForegroundColor Yellow
    docker-compose -f $ComposeFile down --remove-orphans
    return
}

# Build arguments
$BuildArgs = @()
if ($Rebuild) {
    $BuildArgs += "--build"
}

Write-Host "Building CLI generator container..." -ForegroundColor Green
docker-compose -f $ComposeFile build

Write-Host "CLI generator container setup completed." -ForegroundColor Green
Write-Host "Use 'run-cli-generator.ps1' to execute the CLI with your input files and output folders." -ForegroundColor Cyan
