# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Claims-ready gate for the bootstrap workflow (Story 03 / DMS-1153).

.DESCRIPTION
    Exposes Test-CmsClaimsReady, which reads claims.expectedVerificationChecks from the
    bootstrap manifest and proves that CMS has applied the expected claim-set composition
    before the instance-configuration phase begins.

    Authentication uses the CMSAuthMetadataReadOnlyAccess client
    (scope edfi_admin_api/authMetadata_readonly_access) when available, falling back to
    the bootstrap admin client already used by configure-local-data-store.ps1.

    The check is authoritative against /authorizationMetadata (not just /health or
    /v2/claimSets presence): for each manifest entry the gate asserts the expected
    claim set exists, the expected resource claim URI is present in that claim set's
    claims, and the expected action name appears in the linked authorization strategies.
    Finding only EdFiSandbox, a claim-set name, or /health green is not sufficient.
#>

Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------
# Module-level path constants (mirrors bootstrap-manifest.psm1 pattern)
# ---------------------------------------------------------------------------

$script:DockerComposeRoot = $PSScriptRoot
$script:DefaultBootstrapRoot = Join-Path $script:DockerComposeRoot ".bootstrap"
$script:DefaultManifestPath = Join-Path $script:DefaultBootstrapRoot "bootstrap-manifest.json"

# Default client credentials for CMSAuthMetadataReadOnlyAccess.
# These match the values written by setup-keycloak.ps1 / setup-openiddict.ps1 when
# start-local-dms.ps1 provisions the dedicated auth-metadata read-only client.
$script:AuthMetaClientId = "CMSAuthMetadataReadOnlyAccess"
$script:AuthMetaClientSecret = "ValidClientSecret1234567890!Abcd"
$script:AuthMetaScope = "edfi_admin_api/authMetadata_readonly_access"

# ---------------------------------------------------------------------------
# Private helpers
# ---------------------------------------------------------------------------

function Format-ClaimsGateLogSafeText {
    <#
    .SYNOPSIS
    Sanitizes a value for safe inclusion in log output (whitelist of letters, digits, and safe
    punctuation). Kept module-private so the gate is importable without bootstrap-manifest.psm1.
    #>
    param(
        $Value
    )

    if ($null -eq $Value) {
        return ""
    }

    $text = [string]$Value
    if ([string]::IsNullOrEmpty($text)) {
        return ""
    }

    $builder = [System.Text.StringBuilder]::new()
    foreach ($character in $text.ToCharArray()) {
        if ([char]::IsLetterOrDigit($character) -or
            $character -eq " " -or
            $character -eq "_" -or
            $character -eq "-" -or
            $character -eq "." -or
            $character -eq ":" -or
            $character -eq "/") {
            $null = $builder.Append($character)
        }
    }

    return $builder.ToString()
}

function Read-ClaimsGateManifest {
    <#
    .SYNOPSIS
    Reads the bootstrap manifest from disk and returns the parsed hashtable.
    Returns $null when no manifest file exists (non-bootstrap invocation path).
    Throws an actionable error when the manifest is present but malformed.
    #>
    param(
        [string]
        $ManifestPath = $script:DefaultManifestPath
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        return $null
    }

    try {
        $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json -AsHashtable
    }
    catch {
        throw "Bootstrap manifest '$(Format-ClaimsGateLogSafeText $ManifestPath)' contains malformed JSON. $(Format-ClaimsGateLogSafeText ($_.Exception.Message))"
    }

    if ($manifest -isnot [System.Collections.IDictionary]) {
        throw "Bootstrap manifest '$(Format-ClaimsGateLogSafeText $ManifestPath)' must contain a JSON object."
    }

    return $manifest
}

function Read-ExpectedVerificationCheckList {
    <#
    .SYNOPSIS
    Extracts claims.expectedVerificationChecks from the manifest hashtable. Throws with an
    actionable message when the manifest is present but the list is missing or empty.
    #>
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]
        $Manifest,

        [string]
        $ManifestPath = $script:DefaultManifestPath
    )

    if (-not $Manifest.ContainsKey("claims")) {
        throw "Bootstrap manifest '$(Format-ClaimsGateLogSafeText $ManifestPath)' is missing the 'claims' section. Run prepare-dms-claims.ps1 before invoking the claims-ready gate."
    }

    $claimsSection = $Manifest["claims"]
    if ($claimsSection -isnot [System.Collections.IDictionary]) {
        throw "Bootstrap manifest 'claims' section must be a JSON object."
    }

    if (-not $claimsSection.ContainsKey("expectedVerificationChecks")) {
        throw "Bootstrap manifest 'claims.expectedVerificationChecks' is missing. Run prepare-dms-claims.ps1 to regenerate the manifest with verification checks before invoking the claims-ready gate."
    }

    $checks = $claimsSection["expectedVerificationChecks"]

    # ConvertFrom-Json -AsHashtable delivers arrays as System.Object[]; normalize defensively.
    if ($null -eq $checks) {
        throw "Bootstrap manifest 'claims.expectedVerificationChecks' is null. Run prepare-dms-claims.ps1 to regenerate the manifest with at least one verification check."
    }

    $checkList = @($checks)
    if ($checkList.Count -eq 0) {
        throw "Bootstrap manifest 'claims.expectedVerificationChecks' is empty. At minimum the EdFiSandbox baseline probe must be present. Run prepare-dms-claims.ps1 to regenerate the manifest."
    }

    return $checkList
}

function Resolve-ClaimsGateTokenUrl {
    <#
    .SYNOPSIS
    Returns the host-side OAuth token endpoint for the resolved identity provider.
    Reads KEYCLOAK_OAUTH_TOKEN_ENDPOINT / SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT from the
    env-file values and falls back to the documented local-dev defaults. Delegates to
    env-utility's Resolve-OAuthTokenUrl when the sibling module is available so the two
    remain in lock-step on endpoint shape; degrades gracefully in isolated unit tests.
    #>
    param(
        [hashtable]
        $EnvValues,

        [string]
        $IdentityProvider = "self-contained"
    )

    $envUtilityPath = Join-Path $script:DockerComposeRoot "env-utility.psm1"
    if (Test-Path -LiteralPath $envUtilityPath) {
        Import-Module $envUtilityPath -Force
        return Resolve-OAuthTokenUrl -EnvValues $EnvValues -IdentityProvider $IdentityProvider
    }

    # Fallback when env-utility.psm1 is not co-located (isolated Pester sandbox).
    switch ($IdentityProvider) {
        "keycloak" {
            $endpoint = if ($null -ne $EnvValues -and $EnvValues.ContainsKey("KEYCLOAK_OAUTH_TOKEN_ENDPOINT") -and
                           -not [string]::IsNullOrWhiteSpace([string]$EnvValues["KEYCLOAK_OAUTH_TOKEN_ENDPOINT"])) {
                [string]$EnvValues["KEYCLOAK_OAUTH_TOKEN_ENDPOINT"]
            }
            else {
                $port = if ($null -ne $EnvValues -and $EnvValues.ContainsKey("KEYCLOAK_PORT") -and
                            -not [string]::IsNullOrWhiteSpace([string]$EnvValues["KEYCLOAK_PORT"])) {
                    [string]$EnvValues["KEYCLOAK_PORT"]
                }
                else { "8045" }
                "http://localhost:$port/realms/edfi/protocol/openid-connect/token"
            }
            return $endpoint
        }
        default {
            # self-contained
            $port = if ($null -ne $EnvValues -and $EnvValues.ContainsKey("DMS_CONFIG_ASPNETCORE_HTTP_PORTS") -and
                        -not [string]::IsNullOrWhiteSpace([string]$EnvValues["DMS_CONFIG_ASPNETCORE_HTTP_PORTS"])) {
                [string]$EnvValues["DMS_CONFIG_ASPNETCORE_HTTP_PORTS"]
            }
            else { "8081" }
            return "http://localhost:$port/connect/token"
        }
    }
}

function Invoke-ClaimsGateTokenRequest {
    <#
    .SYNOPSIS
    Posts a client_credentials grant to the token endpoint and returns the access_token string.
    Throws when the response is missing an access_token.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $TokenUrl,

        [Parameter(Mandatory)]
        [string]
        $ClientId,

        [Parameter(Mandatory)]
        [string]
        $ClientSecret,

        [string]
        $Scope = ""
    )

    $body = "client_id=$([System.Uri]::EscapeDataString($ClientId))" +
            "&client_secret=$([System.Uri]::EscapeDataString($ClientSecret))" +
            "&grant_type=client_credentials"

    if (-not [string]::IsNullOrWhiteSpace($Scope)) {
        $body += "&scope=$([System.Uri]::EscapeDataString($Scope))"
    }

    $response = Invoke-RestMethod `
        -Uri $TokenUrl `
        -Method Post `
        -ContentType "application/x-www-form-urlencoded" `
        -Body $body

    if ($null -eq $response -or [string]::IsNullOrWhiteSpace([string]$response.access_token)) {
        throw "Token endpoint '$(Format-ClaimsGateLogSafeText $TokenUrl)' did not return an access_token for client '$(Format-ClaimsGateLogSafeText $ClientId)'."
    }

    return [string]$response.access_token
}

function Get-ClaimsGateToken {
    <#
    .SYNOPSIS
    Acquires a CMS bearer token for authorization-metadata queries. Tries the dedicated
    CMSAuthMetadataReadOnlyAccess client first; falls back to the bootstrap admin client
    when the dedicated client is unavailable (e.g. during early startup before identity
    setup has completed). Uses Resolve-BootstrapAdminClient from env-utility.psm1 when
    available so the admin client id/secret stay single-sourced.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $TokenUrl,

        [hashtable]
        $EnvValues
    )

    # --- Attempt 1: dedicated CMSAuthMetadataReadOnlyAccess client ---
    try {
        $token = Invoke-ClaimsGateTokenRequest `
            -TokenUrl $TokenUrl `
            -ClientId $script:AuthMetaClientId `
            -ClientSecret $script:AuthMetaClientSecret `
            -Scope $script:AuthMetaScope

        Write-Information "Claims gate: authenticated with CMSAuthMetadataReadOnlyAccess client." -InformationAction Continue
        return $token
    }
    catch {
        Write-Warning "Claims gate: CMSAuthMetadataReadOnlyAccess token request failed ($(Format-ClaimsGateLogSafeText ($_.Exception.Message))). Falling back to bootstrap admin client."
    }

    # --- Attempt 2: bootstrap admin client (same as configure-local-data-store.ps1) ---
    $adminClientId = "dms-data-store-admin"
    $adminClientSecret = "ValidClientSecret1234567890!Abcd"

    $envUtilityPath = Join-Path $script:DockerComposeRoot "env-utility.psm1"
    if (Test-Path -LiteralPath $envUtilityPath) {
        Import-Module $envUtilityPath -Force
        if (Get-Command Resolve-BootstrapAdminClient -ErrorAction SilentlyContinue) {
            $adminClient = Resolve-BootstrapAdminClient -EnvValues $EnvValues
            $adminClientId = $adminClient.ClientId
            $adminClientSecret = $adminClient.ClientSecret
        }
    }

    $token = Invoke-ClaimsGateTokenRequest `
        -TokenUrl $TokenUrl `
        -ClientId $adminClientId `
        -ClientSecret $adminClientSecret `
        -Scope "edfi_admin_api/full_access"

    Write-Information "Claims gate: authenticated with bootstrap admin client." -InformationAction Continue
    return $token
}

function Get-AuthorizationMetadataResponse {
    <#
    .SYNOPSIS
    Calls CMS GET /v3/authorizationMetadata?claimSetName=<name> and returns the parsed response.
    Returns $null when the claim set is not found (404). Throws on unexpected HTTP errors.
    Sends the Tenant header when a tenant is supplied, matching every other CMS Admin API
    call in Dms-Management.psm1 so multi-tenant CMS routing resolves the request.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $CmsBaseUrl,

        [Parameter(Mandatory)]
        [string]
        $AccessToken,

        [string]
        $ClaimSetName = "",

        [string]
        $Tenant = ""
    )

    $normalizedBase = $CmsBaseUrl.TrimEnd('/')
    $url = "$normalizedBase/v3/authorizationMetadata"
    if (-not [string]::IsNullOrWhiteSpace($ClaimSetName)) {
        $encodedName = [System.Uri]::EscapeDataString($ClaimSetName)
        $url = $url + "?claimSetName=" + $encodedName
    }

    $headers = @{ Authorization = "Bearer $AccessToken" }
    if (-not [string]::IsNullOrWhiteSpace($Tenant)) {
        $headers["Tenant"] = $Tenant
    }

    try {
        $response = Invoke-RestMethod `
            -Uri $url `
            -Method Get `
            -ContentType "application/json" `
            -Headers $headers
        return $response
    }
    catch {
        $httpResponse = $_.Exception.Response
        if ($null -ne $httpResponse -and $httpResponse.StatusCode -eq 404) {
            return $null
        }

        throw "GET $(Format-ClaimsGateLogSafeText $url) failed: $(Format-ClaimsGateLogSafeText ($_.Exception.Message))"
    }
}

function Assert-SingleVerificationCheck {
    <#
    .SYNOPSIS
    Performs the claims-ready assertion for a single expectedVerificationChecks entry.
    Throws with a descriptive failure message when any of the three required conditions
    are not satisfied: the expected claim set exists, the expected resource claim URI is
    in that claim set's claims ('name' property), and the expected action name is present
    in the authorizations entry the claim's 'authorizationId' joins to.
    #>
    param(
        [Parameter(Mandatory)]
        $Check,

        # The /authorizationMetadata response for $Check.claimSetName, already fetched by the caller.
        # Test-CmsClaimsReady memoizes this per claim set so repeated claim sets are fetched once.
        # $null means the claim set was not found (404).
        $Metadata
    )

    $claimSetName = [string]$Check.claimSetName
    $resourceClaim = [string]$Check.resourceClaim
    $actionName = [string]$Check.action

    if ([string]::IsNullOrWhiteSpace($claimSetName)) {
        throw "Claims-ready gate: a verification check has an empty claimSetName. Manifest may be malformed."
    }

    if ([string]::IsNullOrWhiteSpace($resourceClaim)) {
        throw "Claims-ready gate: a verification check has an empty resourceClaim. Manifest may be malformed."
    }

    if ([string]::IsNullOrWhiteSpace($actionName)) {
        throw "Claims-ready gate: a verification check has an empty action. Manifest may be malformed."
    }

    if ($null -eq $Metadata) {
        throw "Claims-ready gate FAILED: claim set '$(Format-ClaimsGateLogSafeText $claimSetName)' was not found in CMS /authorizationMetadata. CMS may not yet have applied the expected claims. /health green and /v2/claimSets name presence alone do not satisfy this gate."
    }

    # /authorizationMetadata returns an array of claim-set objects when no filter is applied;
    # with ?claimSetName= it returns the filtered subset. Normalize to array either way.
    $claimSetArray = @($Metadata)

    # Locate the matching claim set entry.
    $matchingClaimSet = $claimSetArray | Where-Object {
        $null -ne $_ -and
        $null -ne $_.PSObject.Properties["claimSetName"] -and
        [string]$_.claimSetName -eq $claimSetName
    } | Select-Object -First 1

    if ($null -eq $matchingClaimSet) {
        throw "Claims-ready gate FAILED: claim set '$(Format-ClaimsGateLogSafeText $claimSetName)' was not present in the /authorizationMetadata response body. Finding only EdFiSandbox or only a claim-set name is not sufficient."
    }

    # Locate the matching resource claim in that claim set's claims array.
    # CMS serializes ClaimSetMetadata.Claim as { name, authorizationId } (camelCase of
    # Claim(string Name, int AuthorizationId) in AuthorizationMetadataResponse.cs): the
    # resource claim URI is 'name', and 'authorizationId' joins to the separate
    # authorizations[] collection that carries the action names.
    $claimsCollection = if ($null -ne $matchingClaimSet.PSObject.Properties["claims"]) {
        @($matchingClaimSet.claims)
    }
    else {
        @()
    }
    $matchingClaim = $claimsCollection | Where-Object {
        $null -ne $_ -and
        $null -ne $_.PSObject.Properties["name"] -and
        [string]$_.name -eq $resourceClaim
    } | Select-Object -First 1

    if ($null -eq $matchingClaim) {
        throw "Claims-ready gate FAILED: resource claim '$(Format-ClaimsGateLogSafeText $resourceClaim)' was not found in claim set '$(Format-ClaimsGateLogSafeText $claimSetName)'. The claim set exists but the expected resource claim URI is absent. Check CMS claims sources."
    }

    # Resolve the claim's actions through the authorizations join:
    # claims[].authorizationId -> authorizations[].id -> actions[].name.
    if ($null -eq $matchingClaim.PSObject.Properties["authorizationId"]) {
        throw "Claims-ready gate FAILED: resource claim '$(Format-ClaimsGateLogSafeText $resourceClaim)' in claim set '$(Format-ClaimsGateLogSafeText $claimSetName)' has no authorizationId, so its actions cannot be resolved from the authorizations collection."
    }
    $authorizationId = [int]$matchingClaim.authorizationId

    $authorizationsCollection = if ($null -ne $matchingClaimSet.PSObject.Properties["authorizations"]) {
        @($matchingClaimSet.authorizations)
    }
    else {
        @()
    }
    $matchingAuthorization = $authorizationsCollection | Where-Object {
        $null -ne $_ -and
        $null -ne $_.PSObject.Properties["id"] -and
        [int]$_.id -eq $authorizationId
    } | Select-Object -First 1

    if ($null -eq $matchingAuthorization) {
        throw "Claims-ready gate FAILED: no authorizations entry with id '$authorizationId' exists for resource claim '$(Format-ClaimsGateLogSafeText $resourceClaim)' in claim set '$(Format-ClaimsGateLogSafeText $claimSetName)'. Expected action '$(Format-ClaimsGateLogSafeText $actionName)'."
    }

    $actionsCollection = if ($null -ne $matchingAuthorization.PSObject.Properties["actions"]) {
        @($matchingAuthorization.actions)
    }
    else {
        @()
    }
    if ($actionsCollection.Count -eq 0) {
        throw "Claims-ready gate FAILED: resource claim '$(Format-ClaimsGateLogSafeText $resourceClaim)' in claim set '$(Format-ClaimsGateLogSafeText $claimSetName)' has no actions in its authorizations entry. Expected action '$(Format-ClaimsGateLogSafeText $actionName)'."
    }

    $matchingAction = $actionsCollection | Where-Object {
        $null -ne $_ -and
        $null -ne $_.PSObject.Properties["name"] -and
        [string]$_.name -eq $actionName
    } | Select-Object -First 1

    if ($null -eq $matchingAction) {
        throw "Claims-ready gate FAILED: action '$(Format-ClaimsGateLogSafeText $actionName)' was not found for resource claim '$(Format-ClaimsGateLogSafeText $resourceClaim)' in claim set '$(Format-ClaimsGateLogSafeText $claimSetName)'. Finding only the claim set name or resource claim is not sufficient."
    }
}

# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

function Test-CmsClaimsReady {
    <#
    .SYNOPSIS
    Proves that CMS has applied the expected claims content before instance configuration
    begins.

    .DESCRIPTION
    Reads claims.expectedVerificationChecks from the bootstrap manifest at ManifestPath.
    When no manifest is present returns immediately (non-bootstrap invocation). When the
    manifest is present the list must be non-empty; a missing or empty list throws with
    an actionable message.

    For each check, queries CMS GET /v3/authorizationMetadata?claimSetName=<name> and asserts:
      1. The expected claim set exists in the response.
      2. The expected resource claim URI is in that claim set's claims array ('name').
      3. The expected action name is present in the actions of the authorizations entry
         the claim's 'authorizationId' joins to (authorizations[].id).

    Authentication tries the CMSAuthMetadataReadOnlyAccess client
    (scope edfi_admin_api/authMetadata_readonly_access) first, then falls back to the
    bootstrap admin token path used by configure-local-data-store.ps1. Both paths use
    Invoke-RestMethod so Pester tests can mock the HTTP calls without a Docker dependency.

    Returns nothing on success. Throws on failure with which check failed (caller exits non-zero).

    .PARAMETER CmsBaseUrl
    Base URL of the running CMS (e.g. http://localhost:8081). When omitted the value is
    derived from the environment file via Resolve-CmsBaseUrl (env-utility.psm1).

    .PARAMETER EnvironmentFile
    Path to the .env file used to resolve CMS base URL, identity provider settings, and the
    CONFIG_SERVICE_TENANT tenant scope sent as the Tenant header on multi-tenant CMS calls.
    Resolved via Resolve-LocalSettingsEnvironmentFile (env-utility.psm1) when the sibling
    module is present. Ignored when -CmsBaseUrl and -AccessToken are both supplied.

    .PARAMETER IdentityProvider
    Identity provider used to acquire the auth-metadata token. Accepted values: 'keycloak',
    'self-contained'. Defaults to 'self-contained'.

    .PARAMETER ManifestPath
    Absolute path to the bootstrap manifest JSON file. Defaults to
    eng/docker-compose/.bootstrap/bootstrap-manifest.json relative to this module's location.

    .PARAMETER AccessToken
    Pre-obtained bearer token. When supplied the token-acquisition step is skipped. Useful
    for Pester tests that stub the token externally.

    .EXAMPLE
    # Standard bootstrap invocation (reads manifest, resolves env file, acquires token):
    Test-CmsClaimsReady

    .EXAMPLE
    # Explicit CMS URL and pre-obtained token for unit testing:
    Test-CmsClaimsReady -CmsBaseUrl "http://localhost:8081" -AccessToken "fake-token"
    #>
    [CmdletBinding()]
    param(
        [string]
        $CmsBaseUrl = "",

        [string]
        $EnvironmentFile = "",

        [ValidateSet("keycloak", "self-contained")]
        [string]
        $IdentityProvider = "self-contained",

        [string]
        $ManifestPath = "",

        [string]
        $AccessToken = ""
    )

    $ErrorActionPreference = "Stop"

    # Resolve manifest path.
    $resolvedManifestPath = if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
        $script:DefaultManifestPath
    }
    else {
        $ManifestPath
    }

    # Read and validate the manifest; return silently when no manifest is present.
    $manifest = Read-ClaimsGateManifest -ManifestPath $resolvedManifestPath
    if ($null -eq $manifest) {
        Write-Information "Claims gate: no bootstrap manifest found at '$(Format-ClaimsGateLogSafeText $resolvedManifestPath)'; skipping claims-ready check." -InformationAction Continue
        return
    }

    # Extract expectedVerificationChecks - throws with actionable message when missing/empty.
    # Re-wrap: PowerShell unwraps a single-element array on return, and .Count on the bare
    # hashtable would report its key count (3) instead of the check count (1).
    $checks = @(Read-ExpectedVerificationCheckList -Manifest $manifest -ManifestPath $resolvedManifestPath)

    # Resolve env-file values (needed for CMS base URL and token endpoint).
    $envValues = $null
    if ([string]::IsNullOrWhiteSpace($CmsBaseUrl) -or [string]::IsNullOrWhiteSpace($AccessToken)) {
        $envUtilityPath = Join-Path $script:DockerComposeRoot "env-utility.psm1"
        if (Test-Path -LiteralPath $envUtilityPath) {
            Import-Module $envUtilityPath -Force

            $resolvedEnvFile = Resolve-LocalSettingsEnvironmentFile `
                -Path $EnvironmentFile `
                -DockerComposeRoot $script:DockerComposeRoot

            $envValues = ReadValuesFromEnvFile -EnvironmentFile $resolvedEnvFile
        }
    }

    # Resolve CMS base URL.
    $resolvedCmsBaseUrl = if (-not [string]::IsNullOrWhiteSpace($CmsBaseUrl)) {
        $CmsBaseUrl
    }
    elseif ($null -ne $envValues) {
        Resolve-CmsBaseUrl -EnvValues $envValues
    }
    else {
        "http://localhost:8081"
    }

    # Resolve tenant scope for multi-tenant CMS routing. Mirrors configure-local-data-store.ps1:
    # CONFIG_SERVICE_TENANT from the env file, forwarded as the Tenant header only when non-empty
    # (the same pattern every CMS Admin API call in Dms-Management.psm1 uses). Without it the
    # gate's /v3/authorizationMetadata request fails tenant resolution in multi-tenant CMS mode.
    $resolvedTenant = ""
    if ($null -ne $envValues -and $envValues.ContainsKey("CONFIG_SERVICE_TENANT")) {
        $resolvedTenant = ([string]$envValues["CONFIG_SERVICE_TENANT"]).Trim()
    }

    # Acquire bearer token (skip when pre-obtained token was supplied by the caller).
    $resolvedToken = if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
        $AccessToken
    }
    else {
        $tokenUrl = Resolve-ClaimsGateTokenUrl -EnvValues $envValues -IdentityProvider $IdentityProvider
        Get-ClaimsGateToken -TokenUrl $tokenUrl -EnvValues $envValues
    }

    # Run each expected-verification-check against /authorizationMetadata.
    #
    # Checks marked isParent are DEFERRED, not asserted: parent (isParent=true) fragment
    # resource claims attach grants that materialize on leaf descendants via hierarchy
    # lineage, and /authorizationMetadata serializes leaf resource claims only - the parent
    # name itself never appears in claims[]. Composed-hierarchy verification of parent
    # grants requires a hierarchy-aware verification path.
    $checkIndex = 0
    $verifiedCount = 0
    $deferredCount = 0
    # Memoize the /authorizationMetadata response per claim set: multiple checks commonly target the
    # same claim set (e.g. EdFiSandbox), and the response is identical for each, so fetch it once.
    $metadataByClaimSet = @{}
    foreach ($check in $checks) {
        $checkIndex++

        # Guarded lookup: manifest checks deserialize as OrderedHashtable, whose missing-key
        # dot access throws under Set-StrictMode -Version Latest (unlike plain Hashtable).
        $isParentCheck = if ($check -is [System.Collections.IDictionary]) {
            $check.Contains("isParent") -and $true -eq $check["isParent"]
        }
        else {
            $null -ne $check.PSObject.Properties["isParent"] -and $true -eq $check.isParent
        }

        # Bracket indexing (not dot access) for log lines: missing keys on a malformed or
        # hand-edited manifest return $null under bracket access (Format-ClaimsGateLogSafeText
        # handles $null), whereas OrderedHashtable dot access throws under StrictMode before
        # Assert-SingleVerificationCheck can report the clean validation message.
        $logClaimSet = if ($check -is [System.Collections.IDictionary]) { $check["claimSetName"] } else { $check.claimSetName }
        $logResourceClaim = if ($check -is [System.Collections.IDictionary]) { $check["resourceClaim"] } else { $check.resourceClaim }
        $logAction = if ($check -is [System.Collections.IDictionary]) { $check["action"] } else { $check.action }

        if ($isParentCheck) {
            $deferredCount++
            Write-Warning "Claims gate: check $checkIndex/$($checks.Count) targets parent resource claim '$(Format-ClaimsGateLogSafeText $logResourceClaim)' (claimSet=$(Format-ClaimsGateLogSafeText $logClaimSet)); parent grants are not directly observable in /authorizationMetadata leaf claims - deferring to composed-hierarchy verification."
            continue
        }

        Write-Information "Claims gate: verifying check $checkIndex/$($checks.Count) - claimSet=$(Format-ClaimsGateLogSafeText $logClaimSet) resourceClaim=$(Format-ClaimsGateLogSafeText $logResourceClaim) action=$(Format-ClaimsGateLogSafeText $logAction)." -InformationAction Continue

        # Fetch /authorizationMetadata once per claim set and reuse it for every check on that set.
        $checkClaimSetName = [string]$logClaimSet
        if (-not $metadataByClaimSet.ContainsKey($checkClaimSetName)) {
            $metadataByClaimSet[$checkClaimSetName] = Get-AuthorizationMetadataResponse `
                -CmsBaseUrl $resolvedCmsBaseUrl `
                -AccessToken $resolvedToken `
                -ClaimSetName $checkClaimSetName `
                -Tenant $resolvedTenant
        }

        Assert-SingleVerificationCheck -Check $check -Metadata $metadataByClaimSet[$checkClaimSetName]

        $verifiedCount++
    }

    if ($verifiedCount -eq 0) {
        throw "Claims-ready gate FAILED: all $deferredCount verification check(s) were parent-claim deferrals; nothing was verifiable against /authorizationMetadata. At minimum the leaf baseline probe must be present - run prepare-dms-claims.ps1 to regenerate the manifest."
    }

    $deferredSuffix = if ($deferredCount -gt 0) { " ($deferredCount parent-claim check(s) deferred)" } else { "" }
    Write-Information "Claims gate PASSED: all $verifiedCount verifiable check(s) confirmed in CMS authorization metadata$deferredSuffix." -InformationAction Continue
}

Export-ModuleMember -Function Test-CmsClaimsReady
