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
