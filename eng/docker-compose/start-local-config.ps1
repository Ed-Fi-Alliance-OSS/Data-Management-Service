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

    # Environment file
    [string]
    $EnvironmentFile = "./.env",

    # Force a rebuild
    [Switch]
    $r
)

$files = @(
    "-f",
    "postgresql.yml",
    "-f",
    "local-config.yml",
    "-f",
    "keycloak.yml"
)

if ($d) {
    if ($v) {
        Write-Output "Shutting down with volume delete"
        docker compose $files down -v
    }
    else {
        Write-Output "Shutting down"
        docker compose $files down
    }
}
else {

    $existingNetwork = docker network ls --filter name="dms" -q
    if (! $existingNetwork) {
        docker network create dms
    }

    $upArgs = @(
        "--detach"
    )
    if ($r) { $upArgs += @("--build") }

    Write-Output "Starting locally-built DMS config service"

    docker compose $files --env-file $EnvironmentFile up $upArgs

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to start local Docker environment, with exit code $LASTEXITCODE."
    }

    Start-Sleep 20
    # Create client with default edfi_admin_api/full_access scope
    ./setup-keycloak.ps1

    # Create client with edfi_admin_api/readonly_access scope
    ./setup-keycloak.ps1 -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" -ClientScopeName "edfi_admin_api/readonly_access"

    # Create client with edfi_admin_api/authMetadata_readonly_access scope
    ./setup-keycloak.ps1 -NewClientId "CMSAuthMetadataReadOnlyAccess" -NewClientName "CMS Auth Endpoints Only Access" -ClientScopeName "edfi_admin_api/authMetadata_readonly_access"
}
