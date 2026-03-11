param(
    [string]$DmsBaseUrl = "http://localhost:8080",
    [string]$CmsBaseUrl = "http://localhost:8081",
    [string]$OutputDir = (Join-Path (Get-Location) "zap-reports"),
    [string]$SysAdminId = "DmsConfigurationService",
    [string]$SysAdminSecret = "ValidClientSecret1234567890!Abcd",
    [string]$DmsInstanceConnectionString = "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;",
    [string]$ZapImage = "ghcr.io/zaproxy/zaproxy:stable",
    [string]$CmsOpenApiPath = "/metadata/specifications",
    [string]$HostAlias = "host.docker.internal",
    [switch]$IgnoreZapExitCode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-DockerAvailable
{
    if (-not (Get-Command docker -ErrorAction SilentlyContinue))
    {
        throw "Docker is required but was not found on PATH."
    }
}

function New-ConfigToken
{
    $tokenUrl = "$CmsBaseUrl/connect/token"
    $body = "client_id=$SysAdminId&client_secret=$SysAdminSecret&grant_type=client_credentials&scope=edfi_admin_api/full_access"

    $response = Invoke-RestMethod -Method Post -Uri $tokenUrl -ContentType "application/x-www-form-urlencoded" -Body $body
    return $response.access_token
}

function New-DmsClient
{
    param(
        [string]$ConfigToken
    )

    $vendorBody = @{
        company = "ZAP Vendor $(Get-Random -Minimum 1000 -Maximum 9999999)"
        contactName = "ZAP User"
        contactEmailAddress = "zap.user@example.com"
        namespacePrefixes = "uri://ed-fi.org"
    } | ConvertTo-Json

    $vendor = Invoke-RestMethod -Method Post -Uri "$CmsBaseUrl/v2/vendors" -Headers @{ Authorization = "Bearer $ConfigToken" } -ContentType "application/json" -Body $vendorBody

    $instanceBody = @{
        instanceType = "Test"
        instanceName = "ZAP Instance $(Get-Random -Minimum 1000 -Maximum 9999999)"
        connectionString = $DmsInstanceConnectionString
    } | ConvertTo-Json

    $instance = Invoke-RestMethod -Method Post -Uri "$CmsBaseUrl/v2/dmsInstances" -Headers @{ Authorization = "Bearer $ConfigToken" } -ContentType "application/json" -Body $instanceBody

    $applicationBody = @{
        vendorId = $vendor.id
        applicationName = "ZAP App $(Get-Random -Minimum 1000 -Maximum 9999999)"
        claimSetName = "E2E-RelationshipsWithEdOrgsOnlyClaimSet"
        educationOrganizationIds = @(255, 255901)
        dmsInstanceIds = @($instance.id)
    } | ConvertTo-Json

    $application = Invoke-RestMethod -Method Post -Uri "$CmsBaseUrl/v2/applications" -Headers @{ Authorization = "Bearer $ConfigToken" } -ContentType "application/json" -Body $applicationBody

    return [pscustomobject]@{
        ClientId = $application.key
        ClientSecret = $application.secret
    }
}

function New-DmsToken
{
    param(
        [string]$ClientId,
        [string]$ClientSecret
    )

    $discovery = Invoke-RestMethod -Method Get -Uri $DmsBaseUrl
    $tokenUrl = $discovery.urls.oauth

    if (-not $tokenUrl)
    {
        throw "Discovery did not return an OAuth token URL."
    }

    $basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("$ClientId`:$ClientSecret"))
    $headers = @{ Authorization = "Basic $basic" }

    $response = Invoke-RestMethod -Method Post -Uri $tokenUrl -Headers $headers -ContentType "application/x-www-form-urlencoded" -Body "grant_type=client_credentials"
    return [pscustomobject]@{
        Token = $response.access_token
        DataApi = $discovery.urls.dataManagementApi
    }
}

function Update-SpecForDockerHost
{
    param(
        [string]$SpecPath
    )

    $content = Get-Content -Raw -Path $SpecPath
    $content = $content.Replace("http://localhost", "http://$HostAlias").Replace("https://localhost", "https://$HostAlias")
    Set-Content -Path $SpecPath -Value $content -NoNewline -Encoding utf8
}

function Get-ZapReplacerConfig
{
    param(
        [string]$Token
    )

    $escapedToken = $Token.Replace(" ", "%20")

    return "-config replacer.full_list(0).description=auth `
        -config replacer.full_list(0).enabled=true `
        -config replacer.full_list(0).matchtype=REQ_HEADER `
        -config replacer.full_list(0).matchstr=Authorization `
        -config replacer.full_list(0).regex=false `
        -config replacer.full_list(0).replacement=Bearer%20$escapedToken"
}

function Invoke-ZapApiScan
{
    param(
        [string]$SpecFile,
        [string]$ReportPrefix,
        [string]$Token
    )

    $authConfig = Get-ZapReplacerConfig -Token $Token
    $zapArgs = @(
        "zap-api-scan.py",
        "-t", "/zap/wrk/$SpecFile",
        "-f", "openapi",
        "-r", "$ReportPrefix.html",
        "-J", "$ReportPrefix.json",
        "-x", "$ReportPrefix.xml",
        "-z", "$authConfig -config api.disablekey=true"
    )

    if ($IgnoreZapExitCode)
    {
        $zapArgs += "-I"
    }

    $resolved = (Resolve-Path $OutputDir).Path
    $volume = "${resolved}:/zap/wrk" -replace "\\", "/"

    Write-Host "Running ZAP scan: $ReportPrefix"
    docker run --rm -v $volume $ZapImage @zapArgs
}

Assert-DockerAvailable

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Write-Host "Requesting CMS admin token..."
$configToken = New-ConfigToken

Write-Host "Provisioning DMS client..."
$client = New-DmsClient -ConfigToken $configToken

Write-Host "Requesting DMS token..."
$dmsAuth = New-DmsToken -ClientId $client.ClientId -ClientSecret $client.ClientSecret

Write-Host "Validating CMS access..."
Invoke-RestMethod -Method Get -Uri "$CmsBaseUrl/authorizationMetadata?claimSetName=E2E-RelationshipsWithEdOrgsOnlyClaimSet" -Headers @{ Authorization = "Bearer $configToken" } | Out-Null

Write-Host "Validating DMS access..."
Invoke-RestMethod -Method Get -Uri "$($dmsAuth.DataApi)/ed-fi/gradeLevelDescriptors" -Headers @{ Authorization = "Bearer $($dmsAuth.Token)" } | Out-Null

$dmsResourcesSpec = Join-Path $OutputDir "dms-resources-spec.json"
$dmsDescriptorsSpec = Join-Path $OutputDir "dms-descriptors-spec.json"
$cmsSpec = Join-Path $OutputDir "cms-openapi.json"

Invoke-WebRequest -Uri "$DmsBaseUrl/metadata/specifications/resources-spec.json" -OutFile $dmsResourcesSpec
Invoke-WebRequest -Uri "$DmsBaseUrl/metadata/specifications/descriptors-spec.json" -OutFile $dmsDescriptorsSpec
Invoke-WebRequest -Uri "$CmsBaseUrl$CmsOpenApiPath" -OutFile $cmsSpec

Update-SpecForDockerHost -SpecPath $dmsResourcesSpec
Update-SpecForDockerHost -SpecPath $dmsDescriptorsSpec
Update-SpecForDockerHost -SpecPath $cmsSpec

Invoke-ZapApiScan -SpecFile "dms-resources-spec.json" -ReportPrefix "dms-resources" -Token $dmsAuth.Token
Invoke-ZapApiScan -SpecFile "dms-descriptors-spec.json" -ReportPrefix "dms-descriptors" -Token $dmsAuth.Token
Invoke-ZapApiScan -SpecFile "cms-openapi.json" -ReportPrefix "cms" -Token $configToken

Write-Host "ZAP reports saved to $OutputDir"
