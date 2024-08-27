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
    $p
)

if ($d) {
    if ($v) {
        docker compose down -v
    }
    else {
        docker compose down
    }
}
else {
    $pull = "never"
    if ($p) {
        $pull = "--pull"
    }
    docker compose -f docker-compose.yml up --pull $pull -d

    Start-Sleep -Seconds 15
    ./setup-connectors.ps1
}
