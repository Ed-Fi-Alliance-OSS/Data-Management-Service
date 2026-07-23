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
        The relative endpoint path (e.g., /v3/vendors).

    .PARAMETER Method
        The HTTP method to use: Get or Post.

    .PARAMETER ContentType
        The content type of the request body (e.g., application/json).

    .PARAMETER Body
        Optional request body for POST requests.

    .PARAMETER Headers
        Optional hashtable of HTTP headers (e.g., @{ Authorization = "Bearer ..." }).

    .EXAMPLE
        Invoke-Api -BaseUrl "https://dms.ed-fi.com" -RelativeUrl "/v3/vendors" -Method Get -ContentType "application/json"

    .EXAMPLE
        $body = @{ name = "Vendor" } | ConvertTo-Json
        $headers = @{ Authorization = "Bearer token" }
        Invoke-Api -BaseUrl "https://dms.ed-fi.com" -RelativeUrl "/v3/vendors" -Method Post -ContentType "application/json" -Body $body -Headers $headers
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

    try {
        $response = Invoke-RestMethod @invokeParams
        return $response
    }
    catch {
        # PowerShell auto-disposes the underlying HttpResponseMessage Content stream once the
        # exception propagates, so any later call to ReadAsStringAsync() throws "Cannot access
        # a disposed object" and masks the actual API error. Read the body now while the
        # response is still alive, and stash it on ErrorDetails so Get-HttpErrorResponse and
        # other callers can surface it without touching the disposed Content.
        $httpResponse = $_.Exception.Response
        if ($httpResponse -is [System.Net.Http.HttpResponseMessage] -and $httpResponse.Content) {
            try {
                $body = $httpResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                if (-not [string]::IsNullOrEmpty($body)) {
                    $_.ErrorDetails = [System.Management.Automation.ErrorDetails]::new($body)
                }
            }
            catch {
                # If the body can't be read for any reason, fall through and re-throw the
                # original error untouched. We never want this helper to mask the real failure.
                $null = $_
            }
        }
        throw
    }
}

<#
    .SYNOPSIS
        Extracts HTTP status and body details from an error record.

    .PARAMETER ErrorRecord
        The error record produced by a failed HTTP request.

    .OUTPUTS
        [hashtable] Returns StatusCode and Body entries for the failed response.
#>
function Get-HttpErrorResponse {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param (
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.ErrorRecord]$ErrorRecord
    )

    $response = $ErrorRecord.Exception.Response
    $statusCode = if ($response -is [System.Net.Http.HttpResponseMessage]) {
        [int]$response.StatusCode
    } else {
        $null
    }

    # Prefer the body captured by Invoke-Api before the response was disposed. Fall back to
    # reading the live response only when ErrorDetails is empty (e.g. callers that bypass
    # Invoke-Api). The fallback path can still hit "Cannot access a disposed object" for
    # responses that have already propagated, so wrap it defensively.
    $responseBody = ""
    if ($ErrorRecord.ErrorDetails -and -not [string]::IsNullOrEmpty($ErrorRecord.ErrorDetails.Message)) {
        $responseBody = $ErrorRecord.ErrorDetails.Message
    }
    elseif ($response -is [System.Net.Http.HttpResponseMessage] -and $response.Content) {
        try {
            $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        }
        catch {
            $responseBody = ""
        }
    }

    return @{
        StatusCode = $statusCode
        Body = $responseBody
    }
}

function Test-IsDuplicateCmsClientRegistrationError {
    [CmdletBinding()]
    [OutputType([bool])]
    param (
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.ErrorRecord]$ErrorRecord
    )

    $errorResponse = Get-HttpErrorResponse -ErrorRecord $ErrorRecord

    return $errorResponse.StatusCode -eq 400 -and $errorResponse.Body -match "Client with the same Client Id already exists"
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
        [System.Collections.IDictionary]$Data
    )

    return ($Data.GetEnumerator() | ForEach-Object {
        "$($_.Key)=$([uri]::EscapeDataString($_.Value))"
    }) -join "&"
}

<#
    .SYNOPSIS
        Creates a PostgreSQL credential from a username and secret.

    .DESCRIPTION
        Builds a PSCredential for helpers that need to pass PostgreSQL connection details
        through to the Configuration Service.

    .PARAMETER UserName
        The PostgreSQL username.

    .PARAMETER Secret
        The PostgreSQL secret.

    .OUTPUTS
        [System.Management.Automation.PSCredential]

    .EXAMPLE
        $credential = ConvertTo-PostgresCredential -UserName "postgres" -Secret $env:POSTGRES_PASSWORD
#>
function ConvertTo-PostgresCredential {
    [CmdletBinding()]
    [OutputType([System.Management.Automation.PSCredential])]
    param (
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$UserName,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Secret
    )

    $secureSecret = [System.Security.SecureString]::new()
    foreach ($character in $Secret.ToCharArray()) {
        $secureSecret.AppendChar($character)
    }
    $secureSecret.MakeReadOnly()

    return [System.Management.Automation.PSCredential]::new($UserName, $secureSecret)
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
        [string]$ClientSecret = "SdfH)98&JkSdfH)98&JkSdfH)98&JkSdfH)9",

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

    try {
        $response = Invoke-Api @invokeParams
    }
    catch {
        if (Test-IsDuplicateCmsClientRegistrationError -ErrorRecord $_) {
            Write-Warning "Client '$ClientId' already exists. Continuing with the existing registration."
            return
        }

        throw
    }

    if ($response.validationErrors) {
        Write-Warning "Client registration failed: $($response.validationErrors.clientId)"
    }
    else {
        Write-Information "Client '$ClientId' registered successfully." -InformationAction Continue
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
        [string]$ClientSecret = "SdfH)98&JkSdfH)98&JkSdfH)98&JkSdfH)9",

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
        Polls the CMS OAuth token endpoint with a freshly-created client_credentials grant until
        OpenIddict has surfaced the new application and returns a 200, or until the budget is
        exhausted.

    .DESCRIPTION
        Application creation through `Add-Application` writes to `dmscs.application` /
        `dmscs.openiddictapplication`, but OpenIddict's runtime view of the application store
        catches up asynchronously. The first OAuth grant immediately after `Add-Application`
        typically returns 401 ("Invalid client or Invalid client credentials") on a cold stack
        before settling. This helper closes that race by polling `/connect/token` until the
        grant succeeds, so callers of `New-SeedLoaderCredentials` and similar receive credentials
        that are usable on first call.

    .PARAMETER CmsUrl
        The base URL of the Configuration Service (e.g., http://localhost:8081).

    .PARAMETER ClientId
        The just-issued OAuth client_id (Key) to validate.

    .PARAMETER ClientSecret
        The just-issued OAuth client_secret (Secret) to validate.

    .PARAMETER MaxAttempts
        Maximum number of poll attempts. Default: 60 (paired with default DelayMs gives a 30-second ceiling).

    .PARAMETER DelayMs
        Milliseconds to wait between attempts. Default: 500.

    .PARAMETER Invoker
        Test seam: when supplied, the scriptblock is invoked instead of `Invoke-WebRequest`.
        Receives ($uri, $body, $attemptIndex) and must return an integer HTTP status code.

    .OUTPUTS
        None. Throws when the client doesn't become available before the budget is exhausted.

    .EXAMPLE
        Wait-CmsClientAvailable -CmsUrl "http://localhost:8081" -ClientId $newCreds.Key -ClientSecret $newCreds.Secret
#>
function Wait-CmsClientAvailable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$CmsUrl,
        [Parameter(Mandatory)] [string]$ClientId,
        [Parameter(Mandatory)] [string]$ClientSecret,
        [int]$MaxAttempts = 60,
        [int]$DelayMs = 500,
        [scriptblock]$Invoker = $null
    )

    if ($MaxAttempts -lt 1) { throw "Wait-CmsClientAvailable: MaxAttempts must be >= 1." }
    if ($DelayMs -lt 0)     { throw "Wait-CmsClientAvailable: DelayMs must be >= 0." }

    $uri = "$($CmsUrl.TrimEnd('/'))/connect/token"
    $body = ConvertTo-FormBody -Data ([ordered]@{
        client_id     = $ClientId
        client_secret = $ClientSecret
        grant_type    = "client_credentials"
    })
    $lastStatus = $null

    for ($i = 1; $i -le $MaxAttempts; $i++) {
        if ($null -ne $Invoker) {
            $status = [int](& $Invoker $uri $body $i)
        }
        else {
            try {
                $resp = Invoke-WebRequest `
                    -Uri $uri `
                    -Method Post `
                    -Body $body `
                    -ContentType "application/x-www-form-urlencoded" `
                    -SkipHttpErrorCheck
                $status = [int]$resp.StatusCode
            }
            catch {
                $status = -1
            }
        }

        $lastStatus = $status
        if ($status -eq 200) {
            return
        }

        if ($i -lt $MaxAttempts) {
            Start-Sleep -Milliseconds $DelayMs
        }
    }

    $waitSeconds = [math]::Round(($MaxAttempts * $DelayMs) / 1000.0, 1)
    throw "CMS client '$ClientId' did not become available at $uri within ${waitSeconds}s ($MaxAttempts attempts). Last status: $lastStatus."
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
        [long] Returns the vendor ID of the newly created or updated vendor.

    .EXAMPLE
        # Create or update a vendor
        $vendorId = Add-Vendor -AccessToken $token -NamespacePrefixes "uri://ed-fi.org,uri://another.org"
#>
function Add-Vendor {
    [CmdletBinding()]
    [OutputType([long])]
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
        [string]$AccessToken,

        [string]$Tenant = ""
    )

    $vendorData = @{
        company             = $Company
        contactName         = $ContactName
        contactEmailAddress = $ContactEmailAddress
        namespacePrefixes   = $NamespacePrefixes
    }

    $headers = @{ Authorization = "Bearer $AccessToken" }
    if ($Tenant) {
        $headers["Tenant"] = $Tenant
    }

    $fullUri = "$($CmsUrl.TrimEnd('/'))/v3/vendors"

    $webRequestParams = @{
        Uri         = $fullUri
        Method      = "Post"
        ContentType = "application/json"
        Body        = ConvertTo-Json -InputObject $vendorData -Depth 10
        Headers     = $headers
    }

    $webResponse = Invoke-WebRequest @webRequestParams
    $location = $webResponse.Headers['Location']
    if ($location -is [array]) { $location = $location[0] }
    if (-not $location) { throw "CMS Add-Vendor response missing Location header" }
    return [long]($location -split '/')[-1]
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

    .PARAMETER DataStoreIds
        Array of DMS instance IDs to associate with this application. Optional.

    .OUTPUTS
        Hashtable containing:
            - Id: The application's ID.
            - Key: The application's API key.
            - Secret: The application's secret.

    .EXAMPLE
        $creds = Add-Application -VendorId 12345 -AccessToken $token -ApplicationName "MyApp" -DataStoreIds @(1,2)
        Write-Output "App ID: $($creds.Id)"
        Write-Output "App Key: $($creds.Key)"
        Write-Output "App Secret: $($creds.Secret)"
#>
function Add-Application {
    [CmdletBinding()]
    [OutputType([hashtable])]
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

        [long[]]
        $EducationOrganizationIds = @(255901, 19255901),

        [long[]]
        $DataStoreIds = @(),

        [string]$Tenant = ""
    )

    $applicationData = @{
        vendorId        = $VendorId
        applicationName = $ApplicationName
        claimSetName    = $ClaimSetName
        educationOrganizationIds = $EducationOrganizationIds
        dataStoreIds = $DataStoreIds
    }

    $headers = @{ Authorization = "Bearer $AccessToken" }
    if ($Tenant) {
        $headers["Tenant"] = $Tenant
    }

    $invokeParams = @{
        BaseUrl      = $CmsUrl
        RelativeUrl  = "v3/applications"
        Method       = "Post"
        ContentType  = "application/json"
        Body         = ConvertTo-Json -InputObject $applicationData -Depth 10
        Headers      = $headers
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
    Builds a Configuration Service data-store connection string for the given database engine.

.DESCRIPTION
    Produces the Docker-internal connection string the Configuration Service stores for a data
    store. PostgreSQL uses the libpq key=value form; MSSQL uses the SqlClient form
    (Server=host,port) with TrustServerCertificate for the container's self-signed certificate.
    The provision phase (provision-dms-schema.ps1) reads this string back and translates the
    Docker host to the host-side mapped port before invoking SchemaTools.

.PARAMETER DatabaseEngine
    "postgresql" or "mssql".

.PARAMETER DbHost
    The Docker-internal database host (e.g. dms-postgresql or dms-mssql).

.PARAMETER Port
    The Docker-internal database port (5432 for PostgreSQL, 1433 for SQL Server).

.PARAMETER Username
    The database user.

.PARAMETER Password
    The database password.

.PARAMETER DatabaseName
    The target database name.

.OUTPUTS
    [string] The engine-specific connection string.

.EXAMPLE
    New-DataStoreConnectionString -DatabaseEngine mssql -DbHost dms-mssql -Port 1433 -Username sa -Password $pwd -DatabaseName edfi_datamanagementservice
#>
function New-DataStoreConnectionString {
    [CmdletBinding()]
    [OutputType([string])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingUsernameAndPasswordParams', '', Justification = 'The credentials are read as plaintext from the environment file and must be embedded as plaintext in the engine connection string; SecureString/PSCredential adds no protection across that boundary.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'The password is read as plaintext from the environment file and must be embedded as plaintext in the engine connection string; SecureString adds no protection across that boundary.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Pure string-factory helper despite the New- verb; it creates no system state, so -WhatIf/-Confirm semantics add no value.')]
    param(
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine = "postgresql",

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$DbHost,

        [Parameter(Mandatory)]
        [int]$Port,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Username,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Password,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$DatabaseName
    )

    if ($DatabaseEngine -eq "mssql") {
        return "Server=$DbHost,$Port;Database=$DatabaseName;User Id=$Username;Password=$Password;TrustServerCertificate=true;"
    }

    return "host=$DbHost;port=$Port;username=$Username;password=$Password;database=$DatabaseName;"
}

<#
.SYNOPSIS
    Creates a new DMS Instance by sending a POST request to the Configuration Service.

.DESCRIPTION
    Adds a new DMS Instance with the specified instance type, name, and connection string built from individual database parameters.

.PARAMETER CmsUrl
    The base URL of the Config server (e.g., http://localhost:8081).

.PARAMETER DataStoreType
    The type of data store (e.g., "Production", "Development", "Staging"). Defaults to "Local".

.PARAMETER Name
    The name of the data store. Defaults to "Local Data Store".

.PARAMETER PostgresCredential
    The PostgreSQL credential (mandatory).

.PARAMETER PostgresDbName
    The PostgreSQL database name. Defaults to "edfi_datamanagementservice".

.PARAMETER PostgresHost
    The PostgreSQL host. Defaults to "dms-postgresql" for Docker environment.

.PARAMETER PostgresPort
    The PostgreSQL port. Defaults to 5432 for Docker internal port.

.PARAMETER AccessToken
    The bearer token for authorization (mandatory).

.PARAMETER Tenant
    Optional tenant identifier. When specified, this value is passed as a "Tenant" header
    to enable multi-tenant routing.

.OUTPUTS
    [long] Returns the data store ID of the newly created data store.

.EXAMPLE
    # Create data store
    $postgresCredential = ConvertTo-PostgresCredential -UserName "postgres" -Secret "secret123"
    $dataStoreId = Add-DataStore -AccessToken $token -PostgresCredential $postgresCredential

.EXAMPLE
    # Create data store with tenant
    $postgresCredential = ConvertTo-PostgresCredential -UserName "postgres" -Secret "secret123"
    $dataStoreId = Add-DataStore -AccessToken $token -PostgresCredential $postgresCredential -Tenant "Tenant1"
#>
function Add-DataStore {
    [CmdletBinding()]
    param (
        [ValidateNotNullOrEmpty()]
        [string]$CmsUrl = "http://localhost:8081",

        [ValidateNotNullOrEmpty()]
        [string]$DataStoreType = "Local",

        [ValidateNotNullOrEmpty()]
        [string]$Name = "Local Data Store",

        [Parameter(Mandatory = $true)]
        [ValidateNotNull()]
        [System.Management.Automation.PSCredential]$PostgresCredential,

        [ValidateNotNullOrEmpty()]
        [string]$PostgresDbName = "edfi_datamanagementservice",

        [ValidateNotNullOrEmpty()]
        [string]$PostgresHost = "dms-postgresql",

        [int]$PostgresPort = 5432,

        # Pre-built connection string. When provided it is used verbatim (any engine, e.g. an
        # MSSQL connection string from New-DataStoreConnectionString); otherwise a PostgreSQL
        # connection string is built from the credential and Postgres* parameters.
        [string]$ConnectionString = "",

        [Parameter(Mandatory = $true)]
        [string]$AccessToken,

        [string]$Tenant = ""
    )

    # Build the PostgreSQL connection string from the credential and individual parameters
    # unless a pre-built connection string (e.g. MSSQL) was supplied.
    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        $postgresUser = $PostgresCredential.UserName
        $postgresPassword = $PostgresCredential.GetNetworkCredential().Password
        $ConnectionString = "host=$PostgresHost;port=$PostgresPort;username=$postgresUser;password=$postgresPassword;database=$PostgresDbName;"
    }

    $dataStoreData = @{
        dataStoreType = $DataStoreType
        name          = $Name
        connectionString = $ConnectionString
    }

    $headers = @{ Authorization = "Bearer $AccessToken" }
    if ($Tenant) {
        $headers["Tenant"] = $Tenant
    }

    $invokeParams = @{
        BaseUrl      = $CmsUrl
        RelativeUrl  = "v3/dataStores"
        Method       = "Post"
        ContentType  = "application/json"
        Body         = ConvertTo-Json -InputObject $dataStoreData -Depth 10
        Headers      = $headers
    }

    $response = Invoke-Api @invokeParams

    return $response.id
}

<#
.SYNOPSIS
    Retrieves all data stores from the Configuration Service.

.DESCRIPTION
    Gets a list of all data stores with optional paging support.

.PARAMETER CmsUrl
    The base URL of the Config server (e.g., http://localhost:8081).

.PARAMETER AccessToken
    The bearer token for authorization (mandatory).

.PARAMETER Offset
    The offset for paging. Defaults to 0.

.PARAMETER Limit
    The limit for paging. Defaults to 100.

.PARAMETER Tenant
    Optional tenant identifier. When specified, this value is passed as a "Tenant" header
    to enable multi-tenant routing.

.OUTPUTS
    Array of data store objects.

.EXAMPLE
    $instances = Get-DataStore -AccessToken $token
#>
function Get-DataStore {
    [CmdletBinding()]
    param (
        [ValidateNotNullOrEmpty()]
        [string]$CmsUrl = "http://localhost:8081",

        [Parameter(Mandatory = $true)]
        [string]$AccessToken,

        [int]$Offset = 0,

        [int]$Limit = 100,

        [string]$Tenant = ""
    )

    $headers = @{ Authorization = "Bearer $AccessToken" }
    if ($Tenant) {
        $headers["Tenant"] = $Tenant
    }

    $invokeParams = @{
        BaseUrl      = $CmsUrl
        RelativeUrl  = "v3/dataStores?offset=$Offset&limit=$Limit"
        Method       = "Get"
        ContentType  = "application/json"
        Headers      = $headers
    }

    $response = Invoke-Api @invokeParams

    return $response
}

<#
.SYNOPSIS
    Creates a Data Store Context by sending a POST request to the Configuration Service.

.DESCRIPTION
    Adds a context (key-value pair) to an existing Data Store.

.PARAMETER CmsUrl
    The base URL of the Config server (e.g., http://localhost:8081).

.PARAMETER DataStoreId
    The ID of the Data Store to which this context will be added (mandatory).

.PARAMETER ContextKey
    The context key (e.g., "schoolYear", "districtId"). Mandatory.

.PARAMETER ContextValue
    The context value (e.g., "2024", "255901"). Mandatory.

.PARAMETER AccessToken
    The bearer token for authorization (mandatory).

.PARAMETER Tenant
    Optional tenant identifier. When specified, this value is passed as a "Tenant" header
    to enable multi-tenant routing.

.OUTPUTS
    [long] Returns the Context ID of the newly created context.

.EXAMPLE
    # Add schoolYear context to a data store
    $contextId = Add-DataStoreContext -AccessToken $token -DataStoreId 1 -ContextKey "schoolYear" -ContextValue "2024"
#>
function Add-DataStoreContext {
    [CmdletBinding()]
    param (
        [ValidateNotNullOrEmpty()]
        [string]$CmsUrl = "http://localhost:8081",

        [Parameter(Mandatory = $true)]
        [long]$DataStoreId,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$ContextKey,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$ContextValue,

        [Parameter(Mandatory = $true)]
        [string]$AccessToken,

        [string]$Tenant = ""
    )

    $routeContextData = @{
        dataStoreId  = $DataStoreId
        contextKey   = $ContextKey
        contextValue = $ContextValue
    }

    $headers = @{ Authorization = "Bearer $AccessToken" }
    if ($Tenant) {
        $headers["Tenant"] = $Tenant
    }

    $invokeParams = @{
        BaseUrl      = $CmsUrl
        RelativeUrl  = "v3/dataStoreContexts"
        Method       = "Post"
        ContentType  = "application/json"
        Body         = ConvertTo-Json -InputObject $routeContextData -Depth 10
        Headers      = $headers
    }

    $response = Invoke-Api @invokeParams

    return $response.id
}

<#
.SYNOPSIS
    Creates multiple data stores with school year route contexts.

.DESCRIPTION
    Creates a data store for each school year in the specified range.
    Each data store will have a route context with key "schoolYear" and the year as the value.

.PARAMETER CmsUrl
    The base URL of the Config server (e.g., http://localhost:8081).

.PARAMETER StartYear
    The first school year in the range (mandatory).

.PARAMETER EndYear
    The last school year in the range (mandatory).

.PARAMETER PostgresCredential
    The PostgreSQL credential (mandatory).

.PARAMETER PostgresDbName
    The PostgreSQL database name. Defaults to "edfi_datamanagementservice".

.PARAMETER PostgresHost
    The PostgreSQL host. Defaults to "dms-postgresql".

.PARAMETER PostgresPort
    The PostgreSQL port. Defaults to 5432.

.PARAMETER AccessToken
    The bearer token for authorization (mandatory).

.PARAMETER Tenant
    Optional tenant identifier. When specified, this value is passed as a "Tenant" header
    to enable multi-tenant routing.

.OUTPUTS
    Array of hashtables containing DataStoreId and Year for each created data store.

.EXAMPLE
    # Create data stores for years 2022-2026
    $postgresCredential = ConvertTo-PostgresCredential -UserName "postgres" -Secret "secret123"
    $dataStores = Add-DmsSchoolYearInstances -AccessToken $token -StartYear 2022 -EndYear 2026 -PostgresCredential $postgresCredential
#>
function Add-DmsSchoolYearInstances {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'The exported helper name is retained for existing bootstrap scripts.')]
    [CmdletBinding()]
    param (
        [ValidateNotNullOrEmpty()]
        [string]$CmsUrl = "http://localhost:8081",

        [Parameter(Mandatory = $true)]
        [int]$StartYear,

        [Parameter(Mandatory = $true)]
        [int]$EndYear,

        [Parameter(Mandatory = $true)]
        [ValidateNotNull()]
        [System.Management.Automation.PSCredential]$PostgresCredential,

        [ValidateNotNullOrEmpty()]
        [string]$PostgresDbName = "edfi_datamanagementservice",

        [ValidateNotNullOrEmpty()]
        [string]$PostgresHost = "dms-postgresql",

        [int]$PostgresPort = 5432,

        # Pre-built connection string forwarded verbatim to each per-year Add-DataStore call
        # (e.g. an MSSQL connection string). When empty, Add-DataStore builds the PostgreSQL form.
        [string]$ConnectionString = "",

        [Parameter(Mandatory = $true)]
        [string]$AccessToken,

        [string]$Tenant = ""
    )

    # Validate year range
    if ($StartYear -gt $EndYear) {
        throw "StartYear ($StartYear) cannot be greater than EndYear ($EndYear)"
    }

    $createdDataStores = @()

    Write-Information "Creating data stores for school years $StartYear to $EndYear..." -InformationAction Continue

    for ($year = $StartYear; $year -le $EndYear; $year++) {
        Write-Verbose "  Creating data store for School Year $year..."

        # Create data store
        $dataStoreId = Add-DataStore `
            -CmsUrl $CmsUrl `
            -DataStoreType "SchoolYear" `
            -Name "School Year $year" `
            -PostgresCredential $PostgresCredential `
            -PostgresDbName $PostgresDbName `
            -PostgresHost $PostgresHost `
            -PostgresPort $PostgresPort `
            -ConnectionString $ConnectionString `
            -AccessToken $AccessToken `
            -Tenant $Tenant

        Write-Verbose "    Data store created with ID: $dataStoreId"

        # Add route context for school year
        $routeContextId = Add-DataStoreContext `
            -CmsUrl $CmsUrl `
            -DataStoreId $dataStoreId `
            -ContextKey "schoolYear" `
            -ContextValue $year.ToString() `
            -AccessToken $AccessToken `
            -Tenant $Tenant

        Write-Information "    Route context created with ID: $routeContextId (schoolYear=$year)" -InformationAction Continue

        $createdDataStores += @{
            DataStoreId = $dataStoreId
            Year = $year
            RouteContextId = $routeContextId
        }
    }

    Write-Verbose "Successfully created $($createdDataStores.Count) data stores with school year contexts"

    return $createdDataStores
}

<#
.SYNOPSIS
    Creates a new Tenant by sending a POST request to the Configuration Service.

.DESCRIPTION
    Adds a new Tenant with the specified name. This is required before creating
    data stores when multi-tenancy is enabled.

.PARAMETER CmsUrl
    The base URL of the Config server (e.g., http://localhost:8081).

.PARAMETER TenantName
    The name of the tenant to create (mandatory).

.PARAMETER AccessToken
    The bearer token for authorization (mandatory).

.OUTPUTS
    [long] Returns the Tenant ID of the newly created tenant.

.EXAMPLE
    # Create a tenant
    $tenantId = Add-Tenant -AccessToken $token -TenantName "Tenant1"
#>
function Add-Tenant {
    [CmdletBinding()]
    param (
        [ValidateNotNullOrEmpty()]
        [string]$CmsUrl = "http://localhost:8081",

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$TenantName,

        [Parameter(Mandatory = $true)]
        [string]$AccessToken
    )

    $tenantData = @{
        name = $TenantName
    }

    $invokeParams = @{
        BaseUrl      = $CmsUrl
        RelativeUrl  = "v3/tenants"
        Method       = "Post"
        ContentType  = "application/json"
        Body         = ConvertTo-Json -InputObject $tenantData -Depth 10
        Headers      = @{ Authorization = "Bearer $AccessToken" }
    }

    $response = Invoke-Api @invokeParams

    return $response.id
}

<#
.SYNOPSIS
    Returns the de-duplicated, ordered namespace prefix list a SeedLoader vendor needs.

.DESCRIPTION
    Combines the two baseline seed prefixes (uri://ed-fi.org and uri://gbisd.edu) with any
    extension and additional prefixes supplied by the caller. Validates that every non-baseline
    prefix is non-null, non-whitespace, and starts with "uri://". De-duplicates using
    first-occurrence order (case-sensitive).

.PARAMETER ExtensionPrefixes
    Namespace prefixes from the bootstrap manifest seed.extensionNamespacePrefixes.

.PARAMETER AdditionalPrefixes
    Namespace prefixes supplied via -AdditionalNamespacePrefix by the caller.

.OUTPUTS
    [string[]] De-duplicated ordered prefix array.

.EXAMPLE
    $prefixes = Get-SeedLoaderNamespacePrefixes -AdditionalPrefixes @("uri://example.org")
#>
function Get-SeedLoaderNamespacePrefixes {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Function name describes a list-returning helper and matches the exported bootstrap API.')]
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [string[]]$ExtensionPrefixes = @(),
        [string[]]$AdditionalPrefixes = @()
    )

    $baseline = @("uri://ed-fi.org", "uri://gbisd.edu")

    # Validate each non-baseline prefix before building the list
    foreach ($prefix in ($ExtensionPrefixes + $AdditionalPrefixes)) {
        if ([string]::IsNullOrWhiteSpace($prefix)) {
            throw "Namespace prefix must not be null or whitespace."
        }
        if (-not $prefix.StartsWith("uri://")) {
            throw "Namespace prefix '$prefix' is invalid. Every prefix must start with 'uri://'."
        }
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $result = [System.Collections.Generic.List[string]]::new()

    foreach ($prefix in ($baseline + $ExtensionPrefixes + $AdditionalPrefixes)) {
        if ($seen.Add($prefix)) {
            $result.Add($prefix)
        }
    }

    return [string[]]$result.ToArray()
}

<#
.SYNOPSIS
    Looks up an existing CMS application by name and vendor ID.

.DESCRIPTION
    GETs v3/vendors/{vendorId}/applications and filters by applicationName client-side.
    Returns the matching application ID or $null. When multiple matches are found the first
    is returned and a warning is written.

.PARAMETER CmsUrl
    The base URL of the Config server. Defaults to http://localhost:8081.

.PARAMETER VendorId
    The vendor ID to match (mandatory).

.PARAMETER ApplicationName
    The exact application name to match (mandatory).

.PARAMETER AccessToken
    Bearer token for authorization (mandatory).

.PARAMETER Tenant
    Optional tenant header value.

.OUTPUTS
    [long] or $null - the application ID if found, $null if not found.

.EXAMPLE
    $appIds = Find-CmsApplicationIdsByNameAndVendor -VendorId 42 -ApplicationName "Seed Loader" -AccessToken $token
#>
function Find-CmsApplicationIdsByNameAndVendor {
    [CmdletBinding()]
    [OutputType([long[]])]
    param(
        [ValidateNotNullOrEmpty()]
        [string]$CmsUrl = "http://localhost:8081",

        [Parameter(Mandatory)]
        [long]$VendorId,

        [Parameter(Mandatory)]
        [string]$ApplicationName,

        [Parameter(Mandatory)]
        [string]$AccessToken,

        [string]$Tenant = ""
    )

    $headers = @{ Authorization = "Bearer $AccessToken" }
    if ($Tenant) {
        $headers["Tenant"] = $Tenant
    }

    $invokeParams = @{
        BaseUrl     = $CmsUrl
        RelativeUrl = "v3/vendors/$VendorId/applications"
        Method      = "Get"
        ContentType = "application/json"
        Headers     = $headers
    }

    $response = Invoke-Api @invokeParams

    $matchingApplications = @($response | Where-Object { $_.applicationName -eq $ApplicationName })
    return [long[]]@($matchingApplications | ForEach-Object { [long]$_.id })
}

<#
.SYNOPSIS
    Deletes a CMS application by ID.

.DESCRIPTION
    Sends DELETE v3/applications/{id}. Treats 404 as already-deleted (warns instead of throwing).

.PARAMETER CmsUrl
    The base URL of the Config server. Defaults to http://localhost:8081.

.PARAMETER ApplicationId
    The ID of the application to delete (mandatory).

.PARAMETER AccessToken
    Bearer token for authorization (mandatory).

.PARAMETER Tenant
    Optional tenant header value.

.EXAMPLE
    Remove-CmsApplication -ApplicationId 99 -AccessToken $token
#>
function Remove-CmsApplication {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap cleanup helper; callers do not expose -WhatIf.')]
    [CmdletBinding()]
    param(
        [ValidateNotNull()]
        [uri]$CmsUrl = "http://localhost:8081",

        [Parameter(Mandatory)]
        [long]$ApplicationId,

        [Parameter(Mandatory)]
        [string]$AccessToken,

        [string]$Tenant = ""
    )

    $headers = @{ Authorization = "Bearer $AccessToken" }
    if ($Tenant) {
        $headers["Tenant"] = $Tenant
    }

    $fullUri = [uri]::new($CmsUrl, "v3/applications/$ApplicationId").AbsoluteUri

    try {
        Invoke-RestMethod -Uri $fullUri -Method Delete -Headers $headers | Out-Null
    }
    catch {
        $errorResponse = Get-HttpErrorResponse -ErrorRecord $_
        if ($errorResponse.StatusCode -eq 404) {
            Write-Warning "Application $ApplicationId not found during delete; treating as already deleted."
            return
        }
        throw
    }
}

<#
.SYNOPSIS
    Creates a fresh SeedLoader vendor/application and returns in-memory credentials.

.DESCRIPTION
    Orchestrates the CMS bootstrap steps required for seed delivery:
      1. Registers the admin client (idempotent).
      2. Obtains a CMS bearer token.
      3. Creates (or reuses) the SeedLoader vendor.
      4. Deletes any existing Seed Loader application for that vendor so fresh credentials are returned.
      5. Creates a new application with ClaimSetName="SeedLoader" bound to the supplied DMS instance IDs.
    Returns Key, Secret, VendorId, and ApplicationId in a hashtable; credentials are not logged.

    This helper is for the bootstrap seed flow only. It does not call smoke-test credential helpers.

.PARAMETER CmsUrl
    The base URL of the Config server. Defaults to http://localhost:8081.

.PARAMETER AdminClientId
    The admin client ID used to obtain a CMS token.

.PARAMETER AdminClientSecret
    The admin client secret.

.PARAMETER VendorCompany
    The vendor company name for the SeedLoader vendor.

.PARAMETER ApplicationName
    The application name for the SeedLoader application. Defaults to "Seed Loader".

.PARAMETER NamespacePrefixes
    The ordered, de-duplicated namespace prefix array (mandatory). Build with Get-SeedLoaderNamespacePrefixes.

.PARAMETER DataStoreIds
    The DMS instance IDs to bind the application to (mandatory).

.PARAMETER Tenant
    Optional tenant header value.

.PARAMETER AdminToken
    Optional pre-obtained sys-admin access token. When supplied, the helper skips its internal
    Add-CmsClient + Get-CmsToken calls and uses the provided token for downstream vendor/application
    operations. Callers that already hold a fresh admin token (e.g. load-dms-seed-data.ps1's
    Step 4) should pass it here to avoid the duplicate Add-CmsClient warning and the redundant
    OAuth round-trip. Omitting the parameter preserves the original behavior for direct callers.

.OUTPUTS
    Hashtable with Key, Secret, VendorId, ApplicationId.

.EXAMPLE
    $prefixes = Get-SeedLoaderNamespacePrefixes
    $creds = New-SeedLoaderCredentials -NamespacePrefixes $prefixes -DataStoreIds @(1)
#>
function New-SeedLoaderCredentials {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Function returns a key/secret credential pair and matches the exported bootstrap API.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal bootstrap credential helper; callers do not expose -WhatIf end-to-end.')]
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [ValidateNotNullOrEmpty()]
        [string]$CmsUrl = "http://localhost:8081",

        [string]$AdminClientId = "sys-admin",

        [string]$AdminClientSecret = "SdfH)98&JkSdfH)98&JkSdfH)98&JkSdfH)9",

        [string]$VendorCompany = "DMS Bootstrap Seed Loader",

        [string]$ApplicationName = "Seed Loader",

        [Parameter(Mandatory)]
        [string[]]$NamespacePrefixes,

        [Parameter(Mandatory)]
        [long[]]$DataStoreIds,

        # Education organization IDs to associate with the SeedLoader vendor. Resources whose
        # claims use the RelationshipsWithEdOrgsAndPeople authorization strategy (Section,
        # CourseOffering, StudentSchoolAssociation, etc.) require the vendor to have explicit
        # EdOrg associations; an empty list 403s those resources. The default covers every
        # top-level EdOrg present in the v5.x Sample Data inventory, plus the TPDM 1.1.0
        # sample EdOrgs (5, 6, 7) so TPDM sample loads can create educatorPreparationProgram
        # records, whose claim defaults to RelationshipsWithEdOrgsOnly.
        [long[]]$EducationOrganizationIds = @([long]5, [long]6, [long]7, [long]255950, [long]255901, [long]255901001, [long]255901044, [long]255901107, [long]19, [long]19255901, [long]6000203),

        [string]$Tenant = "",

        [string]$AdminToken = ""
    )

    if (-not [string]::IsNullOrWhiteSpace($AdminToken)) {
        $token = $AdminToken
    }
    else {
        Add-CmsClient `
            -CmsUrl $CmsUrl `
            -ClientId $AdminClientId `
            -ClientSecret $AdminClientSecret `
            -DisplayName "System Administrator"

        $token = Get-CmsToken `
            -CmsUrl $CmsUrl `
            -ClientId $AdminClientId `
            -ClientSecret $AdminClientSecret
    }

    $vendorId = Add-Vendor `
        -CmsUrl $CmsUrl `
        -Company $VendorCompany `
        -NamespacePrefixes ($NamespacePrefixes -join ", ") `
        -AccessToken $token `
        -Tenant $Tenant

    # Discard ALL existing applications matching the SeedLoader name so fresh credentials
    # are returned each invocation. Previous runs that leaked duplicates would otherwise leave
    # phantom apps behind when only the first match was deleted. The story disallows plaintext
    # caching of credentials across bootstrap runs.
    $existingAppIds = Find-CmsApplicationIdsByNameAndVendor `
        -CmsUrl $CmsUrl `
        -VendorId $vendorId `
        -ApplicationName $ApplicationName `
        -AccessToken $token `
        -Tenant $Tenant

    foreach ($appId in $existingAppIds) {
        Remove-CmsApplication `
            -CmsUrl $CmsUrl `
            -ApplicationId $appId `
            -AccessToken $token `
            -Tenant $Tenant
    }

    $result = Add-Application `
        -CmsUrl $CmsUrl `
        -ApplicationName $ApplicationName `
        -ClaimSetName "SeedLoader" `
        -VendorId $vendorId `
        -AccessToken $token `
        -EducationOrganizationIds $EducationOrganizationIds `
        -DataStoreIds $DataStoreIds `
        -Tenant $Tenant

    # Close the OpenIddict CDC race: on a cold stack the just-created application is in the DB
    # but not yet surfaced into OpenIddict's runtime application store, so the first OAuth grant
    # returns 401. Polling /connect/token confirms the client is usable before we hand
    # credentials back to the caller.
    Wait-CmsClientAvailable -CmsUrl $CmsUrl -ClientId $result.Key -ClientSecret $result.Secret

    return @{
        Key           = $result.Key
        Secret        = $result.Secret
        VendorId      = $vendorId
        ApplicationId = $result.Id
    }
}

<#
.SYNOPSIS
    Confirms CMS has the 'SeedLoader' claim set loaded before SeedLoader credentials are minted.

.DESCRIPTION
    Add-Application stores the ClaimSetName as a string. A stale Config image that predates
    DMS-1152's embedded Claims.json would accept the application creation but then BulkLoadClient
    would surface confusing 401/403 noise on the first call because the runtime grant resolution
    can't find the SeedLoader claim set. This fail-fast preflight queries /v3/claimSets and throws
    with a clear remediation message when the claim set is absent.

.PARAMETER CmsUrl
    The base URL of the Config server.

.PARAMETER AccessToken
    Admin bearer token (mandatory).

.PARAMETER Tenant
    Optional tenant header value.
#>
function Assert-CmsSeedLoaderClaimSetLoaded {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$CmsUrl,

        [Parameter(Mandatory)]
        [string]$AccessToken,

        [string]$Tenant = "",

        # Test-only seam: Pester tests pass a scriptblock that returns a fake response so the
        # preflight can be exercised without a live CMS. Module functions calling Invoke-Api
        # bind to the module's own scope, so a script-level function override in tests does
        # not propagate - hence this explicit invoker.
        [scriptblock]$ApiInvoker = $null
    )

    $headers = @{ Authorization = "Bearer $AccessToken" }
    if ($Tenant) {
        $headers["Tenant"] = $Tenant
    }

    $invokeParams = @{
        BaseUrl     = $CmsUrl
        RelativeUrl = "v3/claimSets?offset=0&limit=500"
        Method      = "Get"
        ContentType = "application/json"
        Headers     = $headers
    }

    $response = if ($null -ne $ApiInvoker) {
        & $ApiInvoker @invokeParams
    }
    else {
        Invoke-Api @invokeParams
    }

    if (
        -not (
            $response
            | Where-Object { $_.name -eq "SeedLoader" -or $_.claimSetName -eq "SeedLoader" }
        )
    ) {
        throw "CMS does not have the 'SeedLoader' claim set loaded at $CmsUrl. The Configuration Service image likely predates DMS-1152's embedded Claims.json. Rebuild or pull a current Config image and re-run."
    }
}

Export-ModuleMember -Function Add-CmsClient, Get-CmsToken, Wait-CmsClientAvailable, Add-Vendor, Add-Application, Get-DmsToken, Get-CurrentSchoolYear, New-DataStoreConnectionString, Add-DataStore, Get-DataStore, Add-DataStoreContext, Add-DmsSchoolYearInstances, Add-Tenant, Invoke-Api, Get-HttpErrorResponse, Get-SeedLoaderNamespacePrefixes, Find-CmsApplicationIdsByNameAndVendor, Remove-CmsApplication, New-SeedLoaderCredentials, Assert-CmsSeedLoaderClaimSetLoaded, ConvertTo-FormBody, ConvertTo-PostgresCredential
