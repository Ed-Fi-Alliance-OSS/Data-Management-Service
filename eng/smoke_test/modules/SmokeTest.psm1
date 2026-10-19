# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7
$ErrorActionPreference = "Stop"

# Import the Dms-Management module for vendor/application management
Import-Module "$PSScriptRoot/../../Dms-Management.psm1" -Force

function Invoke-SmokeTestUtility {

    [CmdletBinding()]
    param (
        [string]
        [Parameter(Mandatory = $True)]
        $BaseUrl,

        [string]
        [Parameter(Mandatory = $True)]
        $Key,

        [string]
        [Parameter(Mandatory = $True)]
        $Secret,

        [string]
        [Parameter(Mandatory = $True)]
        $ToolPath,

        [ValidateSet("NonDestructiveApi", "NonDestructiveSdk", "DestructiveSdk")]
        [Parameter(Mandatory = $True)]
        $TestSet,

        [string]
        $SdkPath,

        [ValidateSet("EdFi.Api.TestSdk", "EdFi.Api.Sdk", "EdFi.OdsApi.Sdk")]
        [string]
        $SdkNamespace = "EdFi.Api.TestSdk",

        # Explicit OAuth token URL for the smoke test console. Without it the console
        # resolves the token endpoint from the DMS discovery document, which only works
        # when the advertised endpoint is reachable from the console's host. Stacks that
        # advertise a Docker-internal endpoint must pass the host-reachable DMS token
        # proxy here (e.g. http://localhost:8080/oauth/token); explicit values win over
        # discovery in the console.
        [string]
        $OAuthUrl
    )

    $options = @(
        "-b", $BaseUrl,
        "-k", $Key,
        "-s", $Secret,
        "-t", $TestSet,
        "-c", $SdkNamespace
    )

    if ($OAuthUrl) {
        $options += @("-o", $OAuthUrl)
    }

    if($TestSet -ne "NonDestructiveApi")
    {
        if(!$SdkPath)
        {
            Write-Error "Please provide valid SDK path"
            return
        }
        $options += @("-l", $SdkPath)
    }

    # Build a brand-new array for the echo rather than assigning $options to another
    # variable: PowerShell array assignment is a reference copy, so redacting elements
    # of a variable that still points at $options would redact the real array that
    # gets passed to dotnet below and break authentication for every smoke-test leg.
    $display = @()
    for ($i = 0; $i -lt $options.Length; $i++) {
        if ($i -gt 0 -and ($options[$i - 1] -eq "-k" -or $options[$i - 1] -eq "-s")) {
            $display += "***"
        }
        else {
            $display += $options[$i]
        }
    }

    $previousForegroundColor = $host.UI.RawUI.ForegroundColor
    $host.UI.RawUI.ForegroundColor = "Cyan"
    Write-Output $ToolPath $display
    $host.UI.RawUI.ForegroundColor = $previousForegroundColor

    $path = (Join-Path -Path ($ToolPath).Trim() -ChildPath "tools/net*/any/EdFi.SmokeTest.Console.dll")
    $path = Resolve-Path $path
    &dotnet $path $options
}

function Get-SmokeTestCredential {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator-facing smoke-test helper intentionally writes progress to the console.')]
    [OutputType([hashtable])]
    [CmdletBinding()]
    param (
        [string]
        [Parameter(Mandatory = $True)]
        $ConfigServiceUrl,

        [string]
        $SysAdminId = "smoke-test-admin",

        [string]
        $SysAdminSecret = "SdfH)98&JkSdfH)98&JkSdfH)98&JkSdfH)9",

        [string]
        $VendorName = "Smoke Test Vendor",

        [string]
        $ApplicationName = "Smoke Test Application",

        [string]
        $ClaimSetName = "EdFiSandbox",

        # Includes the TPDM 1.1.0 sample EdOrgs (5, 6, 7) so smoke clients can reach TPDM
        # resources such as educatorPreparationProgram, whose claim defaults to
        # RelationshipsWithEdOrgsOnly.
        [long[]]
        $EducationOrganizationIds = @(5, 6, 7, 255901, 19255901, 100000, 200000, 300000),

        [long[]]
        $DataStoreIds = @(),

        [string]
        $Tenant = ""
    )

    Write-Host "Creating smoke test credentials via Configuration Service..."

    try {
        # Step 1: Create system administrator credentials using Dms-Management module
        Write-Host "Creating system administrator credentials..."
        Add-CmsClient -CmsUrl $ConfigServiceUrl -ClientId $SysAdminId -ClientSecret $SysAdminSecret -DisplayName "Smoke Test Administrator"

        # Step 2: Get configuration service token using Dms-Management module
        Write-Host "Obtaining configuration service token..."
        $configToken = Get-CmsToken -CmsUrl $ConfigServiceUrl -ClientId $SysAdminId -ClientSecret $SysAdminSecret

        # Step 3: Get available data stores. When this helper selects the store, all of its route
        # contexts are returned too, whatever their key, since the caller does not know them.
        $routeContexts = @()
        if ($DataStoreIds.Count -gt 0) {
            $dataStoreIds = [long[]]@($DataStoreIds)
            Write-Host "Using supplied data store IDs: $($dataStoreIds -join ', ')"
        }
        else {
            Write-Host "Retrieving available data stores..."
            $dataStores = Get-DataStore -CmsUrl $ConfigServiceUrl -AccessToken $configToken -Tenant $Tenant

            if ($dataStores -and $dataStores.Count -gt 0) {
                $dataStoreIds = @($dataStores[0].id)
                Write-Host "Found data store with ID: $($dataStoreIds[0])"
                $routeContexts = @(
                    $dataStores[0].dataStoreContexts |
                        Where-Object { $null -ne $_ } |
                        ForEach-Object { [pscustomobject]@{ DataStoreId = [long]$dataStoreIds[0]; ContextKey = [string]$_.contextKey; ContextValue = [string]$_.contextValue } }
                )
            }
            else {
                Write-Warning "No data stores found. Application will be created without data store association."
                $dataStoreIds = @()
            }
        }

        # Step 4: Create the vendor, or reuse it. POST /v3/vendors is create-only, so a repeat call
        # (another -ApplicationName, or a retry after Add-Application failed) reuses the vendor.
        Write-Host "Resolving vendor..."
        $existingVendor = Find-CmsVendorByCompany -CmsUrl $ConfigServiceUrl -Company $VendorName -AccessToken $configToken -Tenant $Tenant
        if ($existingVendor) {
            $vendorId = [long]$existingVendor.id
            Write-Host "Reusing existing vendor with ID: $vendorId"
        }
        else {
            $vendorId = Add-Vendor -CmsUrl $ConfigServiceUrl -Company $VendorName -ContactName "Smoke Test Contact" -ContactEmailAddress "smoketest@example.com" -NamespacePrefixes "uri://ed-fi.org,uri://gbisd.edu,uri://tpdm.ed-fi.org,uri://sample.ed-fi.org" -AccessToken $configToken -Tenant $Tenant
            Write-Host "Vendor created with ID: $vendorId"
        }

        # Step 5: Create application using Dms-Management module
        Write-Host "Creating application..."
        $credentials = Add-Application -CmsUrl $ConfigServiceUrl -ApplicationName $ApplicationName -ClaimSetName $ClaimSetName -VendorId $vendorId -AccessToken $configToken -EducationOrganizationIds $EducationOrganizationIds -DataStoreIds $dataStoreIds -Tenant $Tenant

        # The Configuration Service returns the secret only in this response, so a missing or
        # unprintable value can never be recovered later. Fail here instead of returning it, and
        # name only the field: the response content is never echoed.
        foreach ($field in @("Key", "Secret")) {
            $value = $credentials.$field
            if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value) -or $value -match '\p{Cc}') {
                throw "The Configuration Service application response did not contain a usable $field; the response content is withheld."
            }
        }

        $key = $credentials.Key
        $secret = $credentials.Secret

        Write-Host "Application created successfully. Credentials were returned to the caller and were not written to logs."

        return @{
            Key = $key
            Secret = $secret
            VendorId = $vendorId
            ApplicationName = $ApplicationName
            RouteContexts = $routeContexts
        }
    }
    catch {
        Write-Error "Failed to create smoke test credentials: $($_.Exception.Message)"
        throw
    }
}

function Get-SmokeTestCredentialSummary {
    <#
    .SYNOPSIS
    Builds the operator-facing summary lines for a smoke-test credential: the key and secret the
    Configuration Service returned once, and how to run Invoke-NonDestructiveApiTests.ps1 with them.
    Returns strings only; it writes nothing to the host or the diagnostic streams, so the caller
    decides where the summary goes.

    Every value in the copyable command is a single-quoted literal (generated secrets can contain
    '$'), escaped for every quote character PowerShell accepts. Tenant- or route-qualified targets
    get base and token URLs instead of a command, because those smoke runs are not validated; route
    contexts other than one school year per data store are listed without URLs.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param (
        # A null credential reaches the field check below, so it fails with the same value-free
        # message as any other unusable credential.
        [Parameter(Mandatory = $True)]
        [AllowNull()]
        $Credential,

        # Base URL of the DMS the smoke tests target; omitted when it is not known yet (an
        # IDE-hosted DMS that has not started).
        [string]
        $DmsBaseUrl = "",

        [string]
        $Tenant = "",

        # Route contexts from the configure result (DataStoreId, ContextKey, ContextValue).
        [object[]]
        $RouteContexts = @(),

        [switch]
        $SelfContainedIdentity,

        # The run failed after the credential was created; the stack is not ready for smoke tests.
        [switch]
        $Incomplete
    )

    $values = @{}
    foreach ($field in @("Key", "Secret", "ApplicationName")) {
        $value = if ($Credential -is [System.Collections.IDictionary]) {
            $Credential[$field]
        }
        elseif ($null -ne $Credential -and $null -ne $Credential.PSObject.Properties[$field]) {
            $Credential.PSObject.Properties[$field].Value
        }

        if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value) -or $value -match '\p{Cc}') {
            throw "The smoke-test credential summary requires a usable $field; the value is withheld."
        }
        $values[$field] = $value
    }

    if ($DmsBaseUrl -match '\p{Cc}') {
        throw "The smoke-test credential summary requires a DMS base URL without control characters."
    }

    $baseUrl = if ([string]::IsNullOrWhiteSpace($DmsBaseUrl)) { "<dms-base-url>" } else { $DmsBaseUrl.TrimEnd('/') }

    $lines = [System.Collections.Generic.List[string]]::new()
    if ($Incomplete) {
        $lines.Add("--- Smoke test credentials (created before the failure; the stack is NOT ready) ---")
    }
    else {
        $lines.Add("--- Smoke test credentials ---")
    }
    $lines.Add("Application: $($values.ApplicationName)")
    $lines.Add("Key:         $($values.Key)")
    $lines.Add("Secret:      $($values.Secret)")
    $lines.Add("The Configuration Service returns this secret only once. These bootstrap scripts do not write it to logs or files; your terminal, transcripts, or host logging may still retain it. Store it now.")

    if ($Incomplete) {
        $lines.Add("Fix the failure; CMS will reject a second '$($values.ApplicationName)' for this vendor (see README).")
        return $lines.ToArray()
    }

    # The tenant and route contexts come from configuration and Configuration Service records, which
    # may hold line breaks or terminal control sequences. They are printed with those characters
    # escaped (\u001B), and never composed into a URL, so they cannot rewrite or hide the lines above.
    $unsafeDisplayCharacters = '[\p{Cc}\p{Zl}\p{Zp}]'
    $display = { param([string]$Text) [regex]::Replace($Text, $unsafeDisplayCharacters, { param($match) '\u{0:X4}' -f [int][char]$match.Value }) }

    $contexts = @($RouteContexts | Where-Object { $null -ne $_ })
    $unsafeRouting = (-not [string]::IsNullOrWhiteSpace($Tenant) -and $Tenant -match $unsafeDisplayCharacters) -or
        @($contexts | Where-Object { "$($_.DataStoreId)$($_.ContextKey)$($_.ContextValue)" -match $unsafeDisplayCharacters }).Count -gt 0
    if (-not [string]::IsNullOrWhiteSpace($Tenant) -or $contexts.Count -gt 0) {
        $tenantBaseUrl = $baseUrl
        if (-not [string]::IsNullOrWhiteSpace($Tenant)) {
            $lines.Add("Tenant:      $(& $display $Tenant)")
            $tenantBaseUrl = "$baseUrl/$Tenant"
        }

        # A URL is composed only when every data store has exactly one schoolYear context. Any other
        # key, or several contexts on one store, is routed in ROUTE_QUALIFIER_SEGMENTS order, which
        # this formatter does not know, so those contexts are listed instead.
        $schoolYearOnly = @($contexts | Where-Object { $_.ContextKey -cne "schoolYear" }).Count -eq 0 -and
            @($contexts | Group-Object -Property DataStoreId | Where-Object { $_.Count -gt 1 }).Count -eq 0

        if ($unsafeRouting) {
            foreach ($store in @($contexts | Group-Object -Property DataStoreId)) {
                $pairs = @($store.Group | ForEach-Object { "$(& $display $_.ContextKey)=$(& $display $_.ContextValue)" }) -join ", "
                $lines.Add("Data store $(& $display $store.Name) route contexts: $pairs")
            }
            $lines.Add("No DMS base or token URL is printed: the tenant or a route context contains control or line-break characters, shown escaped above. Correct the Configuration Service tenant or data-store contexts.")
        }
        elseif ($contexts.Count -eq 0) {
            $lines.Add("DMS base:    $tenantBaseUrl")
            $lines.Add("Token URL:   $tenantBaseUrl/oauth/token")
        }
        elseif ($schoolYearOnly) {
            foreach ($context in $contexts) {
                $contextBaseUrl = "$tenantBaseUrl/$($context.ContextValue)"
                $lines.Add("Data store $($context.DataStoreId) (school year $($context.ContextValue)): DMS base $contextBaseUrl  token $contextBaseUrl/oauth/token")
            }
        }
        else {
            foreach ($store in @($contexts | Group-Object -Property DataStoreId)) {
                $pairs = @($store.Group | ForEach-Object { "$($_.ContextKey)=$($_.ContextValue)" }) -join ", "
                $lines.Add("Data store $($store.Name) route contexts: $pairs")
            }
            $lines.Add("No DMS base or token URL is printed for these contexts: the DMS base is $tenantBaseUrl followed by the context values in the order ROUTE_QUALIFIER_SEGMENTS lists their keys, and the token URL is that base plus /oauth/token.")
        }
        $lines.Add("Tenant- or route-qualified smoke runs have not been validated with the smoke console; pass the matching DMS base as -BaseUrl and the token URL as -OAuthUrl.")
    }
    else {
        # Invoke-NonDestructiveApiTests.ps1 imports its modules relative to the current directory,
        # so the command must run from eng/smoke_test, the parent of this module's folder.
        $smokeTestDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $quote = { param([string]$Text) "'" + [System.Management.Automation.Language.CodeGeneration]::EscapeSingleQuotedStringContent($Text) + "'" }

        $lines.Add("Run the non-destructive API smoke tests:")
        $lines.Add("  Set-Location -LiteralPath $(& $quote $smokeTestDirectory)")
        $lines.Add("  ./Invoke-NonDestructiveApiTests.ps1 -BaseUrl $(& $quote $baseUrl) -OAuthUrl $(& $quote "$baseUrl/oauth/token") -Key $(& $quote $values.Key) -Secret $(& $quote $values.Secret)")
    }

    # Shared by the command and the qualified URL lines: both carry the placeholder when the DMS URL
    # is not known yet.
    if ($baseUrl -eq "<dms-base-url>" -and -not $unsafeRouting) {
        $lines.Add("Replace <dms-base-url> with the IDE-hosted DMS URL once it is healthy.")
    }

    if ($SelfContainedIdentity) {
        $lines.Add("Self-contained identity: set CONFIGURATION_SERVICE_IDENTITY_BEARER_TOKEN_PER_CLIENT_LIMIT=-1 before starting the stack (see eng/smoke_test/SMOKETEST.md); the default limit stops the smoke run.")
    }

    return $lines.ToArray()
}

Set-Alias -Name Get-SmokeTestCredentials -Value Get-SmokeTestCredential

Export-ModuleMember -Function * -Alias Get-SmokeTestCredentials
