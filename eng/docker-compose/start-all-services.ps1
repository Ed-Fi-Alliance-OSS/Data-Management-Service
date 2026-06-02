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
    $EnvironmentFile = "./.env"
)

$files = @(
    "-f",
    "postgresql.yml",
    "-f",
    "kafka.yml"
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
    Write-Output "Starting all services, without the DMS"
    docker compose $files --env-file $EnvironmentFile up -d
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to start PostgreSQL and Kafka services. Exit code $LASTEXITCODE"
    }

    # Connector registration is intentionally deferred. The Debezium PostgreSQL source connector
    # snapshots dms.document the moment it is registered, so registering it here - before the
    # IDE-hosted DMS has started and deployed the schema - snapshots an empty (or missing) table and
    # leaves the connector silently not streaming changes. This script starts only PostgreSQL and
    # Kafka for the local-debugger workflow, so the schema does not exist yet.
    Write-Output ""
    Write-Output "PostgreSQL and Kafka are starting. Kafka connector registration is deferred."
    Write-Output "After DMS is running and schema deployment is complete, register the source connector with:"
    Write-Output "    ./setup-connectors.ps1 $EnvironmentFile"
}
