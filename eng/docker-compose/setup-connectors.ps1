
[CmdletBinding()]
param (
    # Environment file
    [string]
    $EnvironmentFile = "./.env"
)

function IsReady([string] $Url)
{
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
$envFile = @{}

try {

    Get-Content $EnvironmentFile -ErrorAction Stop | ForEach-Object {
        $split = $_.split('=')
        $key = $split[0]
        $value = $split[1]
        $envFile[$key] = $value
    }
}
catch {
    Write-Error "Please provide valid .env file."
}

$sourcePort=$envFile["CONNECT_SOURCE_PORT"]
$sinkPort=$envFile["CONNECT_SINK_PORT"]

$sourceBase = "http://localhost:$sourcePort/connectors"
$sinkBase = "http://localhost:$sinkPort/connectors"
$sourceUrl = "$sourceBase/postgresql-source"
$sinkUrl = "$sinkBase/opensearch-sink"

 # Source connector
 if(IsReady($sourceBase))
 {
     try {
         $sourceResponse = Invoke-RestMethod -Uri $sourceUrl -Method Get
         if($sourceResponse)
         {
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
         Invoke-RestMethod -Method Post -uri $sourceBase -ContentType "application/json" -Body $sourceBody
     }
     catch {
         Write-Output $_.Exception.Message
     }
     Start-Sleep 2
     Invoke-RestMethod -Method Get -uri $sourceUrl
 }
 else {
     Write-Output "Service at $sourceBase not available."
 }

 # Sink connector
 if(IsReady($sinkBase))
 {
     try {
         $sinkResponse = Invoke-RestMethod -Uri $sinkUrl -Method Get
         if($sinkResponse)
         {
             Write-Output "Deleting existing sink connector configuration."
             Invoke-RestMethod -Method Delete -uri $sinkUrl
         }
     }
     catch {
        Write-Output $_.Exception.Message
     }

     try {
         $sinkBody = Get-Content "./opensearch_connector.json"
         $sinkBody = $sinkBody.Replace("abcdefgh1!", $envFile["OPENSEARCH_ADMIN_PASSWORD"])
         Invoke-RestMethod -Method Post -uri $sinkBase -ContentType "application/json" -Body $sinkBody
     }
     catch {
         Write-Output $_.Exception.Message
     }
     Start-Sleep 2
     Invoke-RestMethod -Method Get -uri $sinkUrl
 }
 else {
     Write-Output "Service at $sinkBase not available."
 }
