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
    $r,

    # Enable KafkaUI and OpenSearch Dashboard
    [Switch]
    $EnableOpenSearchUI,

    # Enforce Authorization
    [Switch]
    $EnforceAuthorization
)

$files = @(
    "-f",
    "postgresql.yml",
    "-f",
    "kafka-elasticsearch.yml",
    "-f",
    "local-dms.yml"
)

if ($EnforceAuthorization) {
    $files += @("-f", "keycloak.yml")
}

if ($EnableOpenSearchUI) {
    $files += @("-f", "kafka-opensearch-ui.yml")
}

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
    $upArgs = @(
        "--detach"
    )
    if ($r) { $upArgs += @("--build") }

    Write-Output "Starting locally-built DMS"
    if ($EnforceAuthorization) {
        $env:IDENTITY_ENFORCE_AUTHORIZATION = $true
    }
    else {
        $env:IDENTITY_ENFORCE_AUTHORIZATION = $false
    }

    docker compose $files --env-file $EnvironmentFile up $upArgs

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to start local Docker environment, with exit code $LASTEXITCODE."
    }

    Start-Sleep 20
    ./setup-connectors.ps1 $EnvironmentFile
}
