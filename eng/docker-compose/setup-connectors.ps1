
[CmdletBinding()]
param (
    # Environment file
    [string]
    $EnvironmentFile = "./.env",

    # Search engine type ("OpenSearch" or "ElasticSearch")
    [string]
    [ValidateSet("OpenSearch", "ElasticSearch")]
    $SearchEngine = "OpenSearch"
)

function IsReady([string] $Url) {
    $maxAttempts = 6
    $attempt = 0
    $waitTime = 5
    while ($attempt -lt $maxAttempts) {
        try {
            Invoke-RestMethod -Uri $Url -Method Get -TimeoutSec 5
            return $true;
        }
        catch {
            Write-Output $_.Exception.Message
            Start-Sleep -Seconds $waitTime
            $attempt++
        }
    }
    return $false;
}

# Read .env file
Import-Module ./env-utility.psm1
$envFile = ReadValuesFromEnvFile $EnvironmentFile

$connectPort = $envFile["CONNECT_PORT"]
$connectBaseUrl = "http://localhost:$connectPort/connectors"

$sourceUrl = "$connectBaseUrl/postgresql-source"
$sinkUrl = "$connectBaseUrl/opensearch-sink"

$connectorConfig = "opensearch_connector.json"
$AdminPassword = $envFile["OPENSEARCH_ADMIN_PASSWORD"]

# Search engine check
if($SearchEngine -eq "ElasticSearch")
{
    $sinkUrl = "$connectBaseUrl/elasticsearch-sink"
    $connectorConfig = "elasticsearch_connector.json"
    $AdminPassword = $envFile["ELASTICSEARCH_ADMIN_PASSWORD"]
}

# Source connector
if (IsReady($sourceBase)) {
    try {
        $sourceResponse = Invoke-RestMethod -Uri $sourceUrl -Method Get -SkipHttpErrorCheck

        # only true if the response was 200
        if ($null -ne $sourceResponse.name) {
            Write-Output "Deleting existing source connector configuration."
            Invoke-RestMethod -Method Delete -uri $sourceUrl
        }
    }
    catch {
        Write-Output $_.Exception.Message
    }

    try {
        $sourceBody = Get-Content "./postgresql_connector.json"
        $sourceBody = $sourceBody.Replace("abcdefgh1!", $envFile["POSTGRES_PASSWORD"])

        Write-Output "Installing source connector configuration"
        Invoke-RestMethod -Method Post -uri $connectBaseUrl -ContentType "application/json" -Body $sourceBody
    }
    catch {
        Write-Output $_.Exception.Message
    }
    Start-Sleep 2
    Invoke-RestMethod -Method Get -uri $sourceUrl -SkipHttpErrorCheck
}
else {
    Write-Output "Service at $connectBaseUrl not available."
}

# Sink connector
if (IsReady($connectBaseUrl)) {
    try {
        $sinkResponse = Invoke-RestMethod -Uri $sinkUrl -Method Get -SkipHttpErrorCheck

        if ($null -ne $sinkResponse.name) {
            Write-Output "Deleting existing sink connector configuration."
            Invoke-RestMethod -Method Delete -uri $sinkUrl
        }
    }
    catch {
        Write-Output $_.Exception.Message
    }

    try {
        $sinkBody = Get-Content "./$connectorConfig"
        $sinkBody = $sinkBody.Replace("abcdefgh1!", $AdminPassword)

        Write-Output "Installing sink connector configuration"
        Invoke-RestMethod -Method Post -uri $connectBaseUrl -ContentType "application/json" -Body $sinkBody
    }
    catch {
        Write-Output $_.Exception.Message
    }
    Start-Sleep 2
    Invoke-RestMethod -Method Get -uri $sinkUrl -SkipHttpErrorCheck
}
else {
    Write-Output "Service at $connectBaseUrl not available."
}
