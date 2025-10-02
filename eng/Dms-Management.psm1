# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
    .SYNOPSIS
        Sends an HTTP GET or POST request to a specified API endpoint.

    .DESCRIPTION
        Builds a full URI from base and relative paths, optionally adds headers and request body,
        and executes the API call using Invoke-RestMethod.

    .PARAMETER BaseUrl
        The base URL of the API (e.g., https://dms.ed-fi.com).

    .PARAMETER RelativeUrl
        The relative endpoint path (e.g., /v2/vendors).

    .PARAMETER Method
        The HTTP method to use: Get or Post.

    .PARAMETER ContentType
        The content type of the request body (e.g., application/json).

    .PARAMETER Body
        Optional request body for POST requests.

    .PARAMETER Headers
        Optional hashtable of HTTP headers (e.g., @{ Authorization = "Bearer ..." }).

    .EXAMPLE
        Invoke-Api -BaseUrl "https://dms.ed-fi.com" -RelativeUrl "/v2/vendors" -Method Get -ContentType "application/json"

    .EXAMPLE
        $body = @{ name = "Vendor" } | ConvertTo-Json
        $headers = @{ Authorization = "Bearer token" }
        Invoke-Api -BaseUrl "https://dms.ed-fi.com" -RelativeUrl "/v2/vendors" -Method Post -ContentType "application/json" -Body $body -Headers $headers
#>
function Invoke-Api {
    [CmdletBinding()]
    param (
        [Parameter(Mandatory = $true)]
        [uri]$BaseUrl,

        [Parameter(Mandatory = $true)]
        [string]$RelativeUrl,

        [ValidateSet("Get", "Post")]
        [Parameter(Mandatory = $true)]
        [string]$Method,

        [Parameter(Mandatory = $true)]
        [string]$ContentType,

        [object]$Body = $null,

        [hashtable]$Headers = @{}
    )

    $fullUri = [uri]::new($BaseUrl, $RelativeUrl).AbsoluteUri

    $invokeParams = @{
        Uri         = $fullUri
        Method      = $Method
        ContentType = $ContentType
        Headers     = $Headers
    }

    if ($Method -eq 'Post' -and $Body) {
        $invokeParams.Body = $Body
    }

    $response = Invoke-RestMethod @invokeParams
    return $response
}

<#
    .SYNOPSIS
        Converts a hashtable into a x-www-form-urlencoded string.

    .PARAMETER Data
        A hashtable containing key-value pairs to encode.

    .EXAMPLE
        ConvertTo-FormBody -Data @{ client_id = "abc"; scope = "read" }
        # Returns: client_id=abc&scope=read
#>
function ConvertTo-FormBody {
    param (
        [Parameter(Mandatory)]
        [hashtable]$Data
    )

    return ($Data.GetEnumerator() | ForEach-Object {
        "$($_.Key)=$([uri]::EscapeDataString($_.Value))"
    }) -join "&"
}

<#
    .SYNOPSIS
        Registers a new Client by sending a POST request to the Config server.

    .DESCRIPTION
        Sends client credentials to a local or remote Config server's registration endpoint.
        Uses x-www-form-urlencoded format and handles validation feedback.

    .PARAMETER CmsUrl
        The base URL of the Config server configuration API. Defaults to http://localhost:8081.

    .PARAMETER ClientId
        The unique identifier of the client being registered.

    .PARAMETER ClientSecret
        The client secret used for authentication.

    .PARAMETER DisplayName
        A human-readable display name for the client.

    .EXAMPLE
        Add-CmsClient -ClientId "sys-admin" -ClientSecret "SuperSecret123!" -DisplayName "System Administrator"

    .NOTES
        Requires the helper function Invoke-Api to be defined in scope.
#>
function Add-CmsClient {
    [CmdletBinding()]
    param (
        [ValidateNotNullOrEmpty()]
        [string]$CmsUrl = "http://localhost:8081",

        [ValidateNotNullOrEmpty()]
        [string]$ClientId = "sys-admin",

        [ValidateNotNullOrEmpty()]
        [string]$ClientSecret = "SdfH)98&Jk",

        [ValidateNotNullOrEmpty()]
        [string]$DisplayName = "System Administrator"
    )

    $clientData = @{
        ClientId     = $ClientId
        ClientSecret = $ClientSecret
        DisplayName  = $DisplayName
    }

    $invokeParams = @{
        BaseUrl      = $CmsUrl
        RelativeUrl  = "connect/register"
        Method       = "Post"
        ContentType  = "application/x-www-form-urlencoded"
        Body         = ConvertTo-FormBody -Data $clientData
    }

    $response = Invoke-Api @invokeParams

    if ($response.validationErrors) {
        Write-Warning "Client registration failed: $($response.validationErrors.clientId)"
    }
    else {
        Write-Host "Client '$ClientId' registered successfully."
    }
}

<#
    .SYNOPSIS
        Authenticates a client using client credentials and retrieves an access token.

    .DESCRIPTION
        Sends a POST request to the Config server's token endpoint using the client credentials grant type.
        Returns the access token if the authentication is successful.

    .PARAMETER CmsUrl
        The base URL of the Config server (e.g., http://localhost:8081).

    .PARAMETER ClientId
        The client ID to authenticate with.

    .PARAMETER ClientSecret
        The client secret associated with the client ID.

    .PARAMETER GrantType
        Keycloak grant type. Defaults to "client_credentials".

    .PARAMETER Scope
        Scope of the access token request. Defaults to "edfi_admin_api/full_access".

    .OUTPUTS
        [string] The Keycloak access token.

    .EXAMPLE
        $token = Initialize-Client -ClientId "my-client" -ClientSecret "SuperSecret123!"
#>
function Get-CmsToken {
    [CmdletBinding()]
    param (
        [ValidateNotNullOrEmpty()]
        [string]$CmsUrl = "http://localhost:8081",

        [ValidateNotNullOrEmpty()]
        [string]$ClientId = "sys-admin",

        [ValidateNotNullOrEmpty()]
        [string]$ClientSecret = "SdfH)98&Jk",

        [ValidateNotNullOrEmpty()]
        [string]$GrantType = "client_credentials",

        [ValidateNotNullOrEmpty()]
        [string]$Scope = "edfi_admin_api/full_access"
    )

    $clientData = @{
        client_id     = $ClientId
        client_secret = $ClientSecret
        grant_type    = $GrantType
        scope         = $Scope
    }

    $invokeParams = @{
        BaseUrl      = $CmsUrl
        RelativeUrl  = "connect/token"
        Method       = "Post"
        ContentType  = "application/x-www-form-urlencoded"
        Body         = ConvertTo-FormBody -Data $clientData
    }

    $response = Invoke-Api @invokeParams

    return $response.access_token
}

<#
    .SYNOPSIS
        Creates or updates a vendor by sending a POST request to the specified API.

    .DESCRIPTION
        Adds a new vendor if it doesn't exist, or updates the existing vendor if it does.
        The NamespacePrefixes parameter accepts one or multiple comma-separated values as a string.
        This string is sent as-is in the JSON payload (no splitting or conversion to array).

    .PARAMETER CmsUrl
        The base URL of the Config server (e.g., http://localhost:8081).

    .PARAMETER Company
        The name of the vendor company.

    .PARAMETER ContactName
        The contact person's name at the vendor company.

    .PARAMETER ContactEmailAddress
        The contact email address for the vendor.

    .PARAMETER NamespacePrefixes
        A string representing one or multiple namespace prefixes separated by commas.
        For example: "uri://ed-fi.org" or "uri://ed-fi.org,uri://another.org"
        This string will be sent as-is in the JSON payload.

    .PARAMETER AccessToken
        The Keycloak bearer token for authorization (mandatory).

    .OUTPUTS
        [string] Returns the vendor ID of the newly created or updated vendor.

    .EXAMPLE
        # Create or update a vendor
        $vendorId = Add-Vendor -AccessToken $token -NamespacePrefixes "uri://ed-fi.org,uri://another.org"
#>
function Add-Vendor {
    [CmdletBinding()]
    param (
        [ValidateNotNullOrEmpty()]
        [string]$CmsUrl = "http://localhost:8081",

        [ValidateNotNullOrEmpty()]
        [string]$Company = "Demo Vendor",

        [ValidateNotNullOrEmpty()]
        [string]$ContactName = "George Washington",

        [ValidateNotNullOrEmpty()]
        [string]$ContactEmailAddress = "george@example.com",

        [ValidateNotNullOrEmpty()]
        [string]$NamespacePrefixes = "uri://ed-fi.org, uri://gbisd.edu",

        [Parameter(Mandatory = $true)]
        [string]$AccessToken
    )

    $vendorData = @{
        company             = $Company
        contactName         = $ContactName
        contactEmailAddress = $ContactEmailAddress
        namespacePrefixes   = $NamespacePrefixes
    }

    $invokeParams = @{
        BaseUrl      = $CmsUrl
        RelativeUrl  = "v2/vendors"
        Method       = "Post"
        ContentType  = "application/json"
        Body         = ConvertTo-Json -InputObject $vendorData -Depth 10
        Headers      = @{ Authorization = "Bearer $AccessToken" }
    }

    $response = Invoke-Api @invokeParams

    return $response.id
}

<#
    .SYNOPSIS
        Creates a new application associated with a vendor.

    .DESCRIPTION
        Sends a POST request to create an application record linked to the specified vendor.
        Requires a valid Keycloak access token for authorization.
        Returns the application key and secret from the API response.

    .PARAMETER CmsUrl
        The base URL of the Config server (e.g., http://localhost:8081).

    .PARAMETER ApplicationName
        The display name of the application to be created. Defaults to "Demo application".

    .PARAMETER ClaimSetName
        The claim set associated with the application. Defaults to "EdfiSandbox".

    .PARAMETER VendorId
        The numeric ID of the vendor to which the application belongs. Mandatory.

    .PARAMETER AccessToken
        The Keycloak bearer token for API authorization. Mandatory.

    .PARAMETER DmsInstanceIds
        Array of DMS instance IDs to associate with this application. Optional.

    .OUTPUTS
        Hashtable containing:
            - Id: The application's ID.
            - Key: The application's API key.
            - Secret: The application's secret.

    .EXAMPLE
        $creds = Add-Application -VendorId 12345 -AccessToken $token -ApplicationName "MyApp" -DmsInstanceIds @(1,2)
        Write-Host "App ID: $($creds.Id)"
        Write-Host "App Key: $($creds.Key)"
        Write-Host "App Secret: $($creds.Secret)"
#>
function Add-Application {
    [CmdletBinding()]
    param (
        [ValidateNotNullOrEmpty()]
        [string]$CmsUrl = "http://localhost:8081",

        [ValidateNotNullOrEmpty()]
        [string]$ApplicationName = "Demo application",

        [ValidateNotNullOrEmpty()]
        [string]$ClaimSetName = "EdfiSandbox",

        [Parameter(Mandatory = $true)]
        [long]$VendorId,

        [Parameter(Mandatory = $true)]
        [string]$AccessToken,

        [int[]]
        $EducationOrganizationIds = @(255901, 19255901),

        [long[]]
        $DmsInstanceIds = @()
    )

    $applicationData = @{
        vendorId        = $VendorId
        applicationName = $ApplicationName
        claimSetName    = $ClaimSetName
        educationOrganizationIds = $EducationOrganizationIds
        dmsInstanceIds = $DmsInstanceIds
    }

    $invokeParams = @{
        BaseUrl      = $CmsUrl
        RelativeUrl  = "v2/applications"
        Method       = "Post"
        ContentType  = "application/json"
        Body         = ConvertTo-Json -InputObject $applicationData -Depth 10
        Headers      = @{ Authorization = "Bearer $AccessToken" }
    }

    $response = Invoke-Api @invokeParams

    return @{
        Id     = $response.id
        Key    = $response.key
        Secret = $response.secret
    }
}

<#
.SYNOPSIS
    Retrieves a bearer token using client credentials from a DMS OAuth endpoint.

.DESCRIPTION
    Encodes the provided client Key and Secret using Basic Authentication,
    then sends a POST request to the DMS OAuth token endpoint using
    grant_type=client_credentials. Returns the full token response.

.PARAMETER DmsUrl
    The base URL of the Data Management System (default: http://localhost:8080).

.PARAMETER Key
    The client ID used for authentication.

.PARAMETER Secret
    The client secret used for authentication.

.EXAMPLE
    Get-DmsToken -DmsUrl "http://localhost:8080" -Key "myKey" -Secret "mySecret"
#>
function Get-DmsToken {
    [CmdletBinding()]
    param (
        [ValidateNotNullOrEmpty()]
        [string]$DmsUrl = "http://localhost:8080",

        [Parameter(Mandatory = $true)]
        [string]$Key,

        [Parameter(Mandatory = $true)]
        [string]$Secret
    )

    $encodedAuth = [Convert]::ToBase64String(
        [System.Text.Encoding]::UTF8.GetBytes("$Key`:$Secret")
    )

    $invokeParams = @{
        BaseUrl     = $DmsUrl
        RelativeUrl = "oauth/token"
        Method      = "Post"
        ContentType = "application/x-www-form-urlencoded"
        Body        = ConvertTo-FormBody -Data @{ grant_type = 'client_credentials' }
        Headers     = @{ Authorization = "Basic $encodedAuth" }
    }

    $response = Invoke-Api @invokeParams

    return $response.access_token
}

<#
.SYNOPSIS
    Calculates the current school year based on the current date.

.DESCRIPTION
    Determines the school year using the logic: if current month > June,
    use next calendar year; otherwise use current calendar year.
    This follows the typical academic year pattern where the school year
    begins in late summer/early fall and ends the following spring/summer.

.OUTPUTS
    [int] The calculated school year

.EXAMPLE
    $currentSchoolYear = Get-CurrentSchoolYear
    # Returns 2026 if current date is August 2025 (month > 6)
    # Returns 2025 if current date is March 2025 (month <= 6)

.NOTES
    The cutoff is June (month 6). Any date after June is considered to be
    in the next school year cycle.
#>
function Get-CurrentSchoolYear {
    param()

    $currentDate = Get-Date
    $currentYear = $currentDate.Year
    $currentMonth = $currentDate.Month

    if ($currentMonth -gt 6) {
        return $currentYear + 1
    } else {
        return $currentYear
    }
}

<#
.SYNOPSIS
    Creates a new DMS Instance by sending a POST request to the Configuration Service.

.DESCRIPTION
    Adds a new DMS Instance with the specified instance type, name, and connection string built from individual database parameters.

.PARAMETER CmsUrl
    The base URL of the Config server (e.g., http://localhost:8081).

.PARAMETER InstanceType
    The type of instance (e.g., "Production", "Development", "Staging"). Defaults to "Local".

.PARAMETER InstanceName
    The name of the DMS instance. Defaults to "Local DMS Instance".

.PARAMETER PostgresPassword
    The PostgreSQL password (mandatory).

.PARAMETER PostgresDbName
    The PostgreSQL database name. Defaults to "edfi_datamanagementservice".

.PARAMETER PostgresHost
    The PostgreSQL host. Defaults to "dms-postgresql" for Docker environment.

.PARAMETER PostgresPort
    The PostgreSQL port. Defaults to 5432 for Docker internal port.

.PARAMETER PostgresUser
    The PostgreSQL username. Defaults to "postgres".

.PARAMETER AccessToken
    The bearer token for authorization (mandatory).

.OUTPUTS
    [long] Returns the DMS Instance ID of the newly created instance.

.EXAMPLE
    # Create DMS Instance
    $instanceId = Add-DmsInstance -AccessToken $token -PostgresPassword "secret123"
#>
function Add-DmsInstance {
    [CmdletBinding()]
    param (
        [ValidateNotNullOrEmpty()]
        [string]$CmsUrl = "http://localhost:8081",

        [ValidateNotNullOrEmpty()]
        [string]$InstanceType = "Local",

        [ValidateNotNullOrEmpty()]
        [string]$InstanceName = "Local DMS Instance",

        [Parameter(Mandatory = $true)]
        [string]$PostgresPassword,

        [ValidateNotNullOrEmpty()]
        [string]$PostgresDbName = "edfi_datamanagementservice",

        [ValidateNotNullOrEmpty()]
        [string]$PostgresHost = "dms-postgresql",

        [int]$PostgresPort = 5432,

        [ValidateNotNullOrEmpty()]
        [string]$PostgresUser = "postgres",

        [Parameter(Mandatory = $true)]
        [string]$AccessToken
    )

    # Build connection string from individual parameters
    $ConnectionString = "host=$PostgresHost;port=$PostgresPort;username=$PostgresUser;password=$PostgresPassword;database=$PostgresDbName;"

    $dmsInstanceData = @{
        instanceType = $InstanceType
        instanceName = $InstanceName
        connectionString = $ConnectionString
    }

    $invokeParams = @{
        BaseUrl      = $CmsUrl
        RelativeUrl  = "v2/dmsInstances"
        Method       = "Post"
        ContentType  = "application/json"
        Body         = ConvertTo-Json -InputObject $dmsInstanceData -Depth 10
        Headers      = @{ Authorization = "Bearer $AccessToken" }
    }

    $response = Invoke-Api @invokeParams

    return $response.id
}

<#
.SYNOPSIS
    Retrieves all DMS Instances from the Configuration Service.

.DESCRIPTION
    Gets a list of all DMS Instances with optional paging support.

.PARAMETER CmsUrl
    The base URL of the Config server (e.g., http://localhost:8081).

.PARAMETER AccessToken
    The bearer token for authorization (mandatory).

.PARAMETER Offset
    The offset for paging. Defaults to 0.

.PARAMETER Limit
    The limit for paging. Defaults to 100.

.OUTPUTS
    Array of DMS Instance objects.

.EXAMPLE
    $instances = Get-DmsInstances -AccessToken $token
#>
function Get-DmsInstances {
    [CmdletBinding()]
    param (
        [ValidateNotNullOrEmpty()]
        [string]$CmsUrl = "http://localhost:8081",

        [Parameter(Mandatory = $true)]
        [string]$AccessToken,

        [int]$Offset = 0,

        [int]$Limit = 100
    )

    $invokeParams = @{
        BaseUrl      = $CmsUrl
        RelativeUrl  = "v2/dmsInstances?offset=$Offset&limit=$Limit"
        Method       = "Get"
        ContentType  = "application/json"
        Headers      = @{ Authorization = "Bearer $AccessToken" }
    }

    $response = Invoke-Api @invokeParams

    return $response
}

Export-ModuleMember -Function Add-CmsClient, Get-CmsToken, Add-Vendor, Add-Application, Get-DmsToken, Get-CurrentSchoolYear, Add-DmsInstance, Get-DmsInstances, Invoke-Api
