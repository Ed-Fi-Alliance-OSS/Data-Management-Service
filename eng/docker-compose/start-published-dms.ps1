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

    # Force re-pull of all Docker hub images
    [Switch]
    $p,

    # Environment file
    [string]
    $EnvironmentFile = "./.env"
)

if ($d) {
    if ($v) {
        docker compose -f docker-compose.yml -f dms-published.yml  down -v
    }
    else {
        docker compose -f docker-compose.yml -f dms-published.yml  down
    }
}
else {
    $pull = "never"
    if ($p) {
        $pull = "always"
    }
    docker compose -f docker-compose.yml -f dms-local.yml --env-file $EnvironmentFile up --pull $pull -d

    ./setup-connectors.ps1 $EnvironmentFile
}
