# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

$sourcePort="8083"
$sinkPort="8084"

Invoke-RestMethod -Method Delete -uri http://localhost:$sourcePort/connectors/fulfillment-connector

Start-Sleep 1

Invoke-RestMethod -Method Post -InFile .\postgresql_connector.json `
    -uri http://localhost:$sourcePort/connectors/ -ContentType "application/json"

Start-Sleep 1

Invoke-RestMethod -Method Get -uri http://localhost:$sourcePort/connectors/fulfillment-connector

##

Invoke-RestMethod -Method Delete -uri http://localhost:$sinkPort/connectors/search-engine

Start-Sleep 1

Invoke-RestMethod -Method Post -InFile .\opensearch_connector.json `
    -uri http://localhost:$sinkPort/connectors/ -ContentType "application/json"

Start-Sleep 1

Invoke-RestMethod -Method Get -uri http://localhost:$sinkPort/connectors/search-engine
