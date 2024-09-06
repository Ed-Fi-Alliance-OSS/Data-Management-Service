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
    $EnableOpenSearchUI
)

$files = @(
    "-f",
    "postgresql.yml",
    "-f",
    "kafka-opensearch.yml",
    "-f",
    "local-dms.yml"
)

if($EnableOpenSearchUI)
{
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
    $upArgs = @()
    if ($r) { $upArgs.Add("--build") }

    Write-Output "Starting locally-built DMS"
    docker compose $files --env-file $EnvironmentFile up -d $upArgs

    Start-Sleep 20
    ./setup-connectors.ps1 $EnvironmentFile
}
