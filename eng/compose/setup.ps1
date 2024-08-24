
# Read .env file
$envFile = @{}

Get-Content .env | ForEach-Object {
    $split = $_.split('=')
    $key = $split[0]
    $value = $split[1]
    $envFile[$key] = $value
}

$sourcePort=$envFile["CONNECT_SOURCE_PORT"]
$sinkPort=$envFile["CONNECT_SINK_PORT"]

$postgreSQLConnector = Get-Content ./postgresql_connector.json
$postgreSQLConnector = $postgreSQLConnector.Replace("abcdefgh1!", $envFile["POSTGRES_PASSWORD"])

Invoke-RestMethod -Method Delete -uri http://localhost:$sourcePort/connectors/postgresql-source

Start-Sleep 1


Invoke-RestMethod -Method Post -Body $postgreSQLConnector `
    -uri http://localhost:$sourcePort/connectors/ -ContentType "application/json"

Start-Sleep 1

Invoke-RestMethod -Method Get -uri http://localhost:$sourcePort/connectors/postgresql-source

##

Invoke-RestMethod -Method Delete -uri http://localhost:$sinkPort/connectors/opensearch-sink

Start-Sleep 1

$opensearchConnector = Get-Content ./opensearch_connector.json
$opensearchConnector = $opensearchConnector.Replace("abcdefgh1!", $envFile["OPENSEARCH_ADMIN_PASSWORD"])

Invoke-RestMethod -Method Post -Body $opensearchConnector `
    -uri http://localhost:$sinkPort/connectors/ -ContentType "application/json"

Start-Sleep 1

Invoke-RestMethod -Method Get -uri http://localhost:$sinkPort/connectors/opensearch-sink
