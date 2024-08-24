# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[CmdletBinding()]
param (
    # Stop services instead of starting them
    [Switch]
    $Down,

    # Delete volumes after stopping services
    [Switch]
    $Clean
)

if ($Down) {
    if ($Clean) {
        docker compose -f docker-compose.yml -f dms-published.yml  down -v
    }
    else {
        docker compose -f docker-compose.yml -f dms-published.yml  down
    }
}
else {
    docker compose -f docker-compose.yml -f dms-published.yml up --pull always -d
    ./setup.ps1
}
