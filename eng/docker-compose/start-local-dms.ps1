# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param (
    # Stop services instead of starting them
    [Switch]
    $d,

    # Delete volumes after stopping services
    [Switch]
    $v,

    # Force rebuild of the local image
    [Switch]
    $b,

    # Force re-pull of all Docker hub images
    [Switch]
    $p,

    # Environment file
    [string]
    $EnvironmentFile = "./.env"

)

if ($d) {
    if ($v) {
        Write-Output "Shutting down services and deleting volumes"
        docker compose -f docker-compose.yml -f dms-local.yml down -v
    }
    else {
        Write-Output "Shutting down services"
        docker compose -f docker-compose.yml -f dms-local.yml down
    }
}
else {
    if ($b) {
        Write-Output "Rebuilding the local image"
        docker compose -f dms-local.yml build
    }
    $pull = "never"
    if ($p) {
        $pull = "always"
    }

    Write-Output "Starting services"
    docker compose -f docker-compose.yml -f dms-local.yml --env-file $EnvironmentFile up --pull $pull -d

    Start-Sleep 25

    ./setup-connectors.ps1 $EnvironmentFile
}
