
[CmdletBinding()]
param (
    # Environment file
    [string]
    $EnvironmentFile = "./.env"
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

$sourcePort = $envFile["CONNECT_SOURCE_PORT"]

$sourceBase = "http://localhost:$sourcePort/connectors"
$sourceUrl = "$sourceBase/postgresql-source"

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
        Invoke-RestMethod -Method Post -uri $sourceBase -ContentType "application/json" -Body $sourceBody
    }
    catch {
        Write-Output $_.Exception.Message
    }
    Start-Sleep 2
    Invoke-RestMethod -Method Get -uri $sourceUrl -SkipHttpErrorCheck
}
else {
    Write-Output "Service at $sourceBase not available."
}

