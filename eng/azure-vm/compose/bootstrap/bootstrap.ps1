#Requires -Version 7
# SPDX-License-Identifier: Apache-2.0
#
# Post-start bootstrap for the DMS security-review environment.
#
# Sequence (mirrors eng/docker-compose/start-published-dms.ps1, adapted for two
# stacks reached through the gateway; this environment is Keycloak-only):
#   1. Create realm + the three CMS service clients (unless -SkipKeycloak).
#   2. Single-tenant: register admin client, create a data store, vendor, and
#      application -> emit API key/secret.
#   3. Multi-tenant: create two tenants; per tenant create a data store (+ schoolYear
#      route context), vendor, and application -> emit API key/secret.
#
# All traffic goes through the gateway (PUBLIC_BASE_URL + path). For local runs
# with a self-signed cert, pass -Insecure.
#
# Usage:
#   pwsh ./bootstrap.ps1                 # uses ../.env
#   pwsh ./bootstrap.ps1 -Insecure       # local self-signed cert
#   pwsh ./bootstrap.ps1 -SkipKeycloak   # realm/clients already created
[CmdletBinding()]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'ClaimSetName', Justification = 'Consumed as the default value of the New-ReviewApplication -ClaimSet parameter; the analyzer does not track usage inside nested function parameter defaults.')]
param(
    [string]$EnvFile = "$PSScriptRoot/../.env",
    [string]$ClaimSetName = "E2E-NoFurtherAuthRequiredClaimSet",
    # Override the gateway base URL (default: PUBLIC_BASE_URL from .env). Set to
    # https://localhost when running on the VM to use the loopback and avoid
    # public-IP hairpin issues.
    [string]$BaseUrl = "",
    [switch]$SkipKeycloak,
    [switch]$Insecure
)

$ErrorActionPreference = "Stop"
# Reuse the repo's canonical management module (no vendored copy).
Import-Module "$PSScriptRoot/../../../Dms-Management.psm1" -Force

# Self-signed / loopback cert support for local runs (e.g. -BaseUrl https://localhost behind a
# cert issued for the public FQDN). -SkipCertificateCheck must be applied in TWO session states:
#   1. Global - for setup-keycloak.ps1, which is &-invoked in this script's scope.
#   2. The Dms-Management module - its Invoke-RestMethod/Invoke-WebRequest calls run in the
#      module's OWN session state and do NOT inherit $global:PSDefaultParameterValues. Without
#      this, every CMS call after Keycloak fails with RemoteCertificateNameMismatch.
if ($Insecure) {
    $global:PSDefaultParameterValues['Invoke-RestMethod:SkipCertificateCheck'] = $true
    $global:PSDefaultParameterValues['Invoke-WebRequest:SkipCertificateCheck'] = $true

    $dmsModule = Get-Module Dms-Management
    if ($dmsModule) {
        & $dmsModule {
            $PSDefaultParameterValues['Invoke-RestMethod:SkipCertificateCheck'] = $true
            $PSDefaultParameterValues['Invoke-WebRequest:SkipCertificateCheck'] = $true
        }
    }
}

# --- Parse .env -------------------------------------------------------------
if (-not (Test-Path $EnvFile)) { throw "Env file not found: $EnvFile (copy .env.example to .env)." }
$envValues = @{}
foreach ($line in Get-Content $EnvFile) {
    $trimmed = $line.Trim()
    if ($trimmed -eq "" -or $trimmed.StartsWith("#")) { continue }
    $idx = $trimmed.IndexOf("=")
    if ($idx -lt 1) { continue }
    $envValues[$trimmed.Substring(0, $idx).Trim()] = $trimmed.Substring($idx + 1).Trim()
}
function EnvVal([string]$key, [string]$default = "") {
    if ($envValues.ContainsKey($key) -and $envValues[$key]) { return $envValues[$key] }
    return $default
}

$publicBaseUrl = if ($BaseUrl) { $BaseUrl.TrimEnd("/") } else { (EnvVal "PUBLIC_BASE_URL" "https://localhost").TrimEnd("/") }
$realm         = EnvVal "KEYCLOAK_REALM" "edfi"
$pgUser        = EnvVal "POSTGRES_USER" "postgres"
$pgPassword    = EnvVal "POSTGRES_PASSWORD"
# Add-DataStore takes a PSCredential (the module's plaintext -PostgresPassword parameter was
# replaced); bridge the .env values through the module's own converter.
$pgCredential  = ConvertTo-PostgresCredential -UserName $pgUser -Secret $pgPassword
$schoolYear    = EnvVal "MT_SCHOOL_YEAR" "2025"
$tenant1       = EnvVal "MT_TENANT_1" "tenant1"
$tenant2       = EnvVal "MT_TENANT_2" "tenant2"
$adminClientId     = EnvVal "BOOTSTRAP_ADMIN_CLIENT_ID" "dms-bootstrap-admin"
$adminClientSecret = EnvVal "BOOTSTRAP_ADMIN_CLIENT_SECRET"
# The service client ids/scope MUST be the ones the compose services request: read them from
# the same .env keys (with the same defaults) the compose file consumes, so a customized value
# yields the same client on both sides instead of an authentication mismatch.
$cmsClientId   = EnvVal "DMS_CONFIG_IDENTITY_CLIENT_ID" "DmsConfigurationService"
$roClientId    = EnvVal "CONFIG_SERVICE_CLIENT_ID" "CMSReadOnlyAccess"
$roClientScope = EnvVal "CONFIG_SERVICE_CLIENT_SCOPE" "edfi_admin_api/readonly_access"

# Grand Bend sample-data education organizations (district LEA 255901 + sample schools).
$grandBendEdOrgIds = @([long]255901, [long]255901001, [long]255901107)

$stConfig = "$publicBaseUrl/st-config/"   # trailing slash required for URI joining
$mtConfig = "$publicBaseUrl/mt-config/"

$created = [System.Collections.Generic.List[object]]::new()

# --- 1. Keycloak realm + service clients ------------------------------------
if (-not $SkipKeycloak) {
    Write-Output "== Configuring Keycloak realm '$realm' and service clients =="
    $kc = "$publicBaseUrl/auth"
    $kcAdmin = EnvVal "KEYCLOAK_ADMIN" "admin"
    $kcAdminPw = EnvVal "KEYCLOAK_ADMIN_PASSWORD"

    & "$PSScriptRoot/../../../docker-compose/setup-keycloak.ps1" -KeycloakServer $kc -Realm $realm -AdminUsername $kcAdmin -AdminPassword $kcAdminPw `
        -NewClientId $cmsClientId -NewClientName "DMS Configuration Service" `
        -ClientScopeName "edfi_admin_api/full_access" -NewClientSecret (EnvVal "DMS_CONFIG_IDENTITY_CLIENT_SECRET")

    & "$PSScriptRoot/../../../docker-compose/setup-keycloak.ps1" -KeycloakServer $kc -Realm $realm -AdminUsername $kcAdmin -AdminPassword $kcAdminPw `
        -NewClientId $roClientId -NewClientName "CMS ReadOnly Access" `
        -ClientScopeName $roClientScope -NewClientSecret (EnvVal "CONFIG_SERVICE_CLIENT_SECRET")

    # Bootstrap admin client -- created directly in Keycloak (full_access scope + cms-client role)
    # so we never rely on the CMS public self-registration endpoint (/connect/register), which is
    # disabled. The shared realm backs both stacks, so this one client authenticates against
    # st-config and mt-config alike. -SkipRealmAdmin: this client only calls the CMS Admin API
    # (CMS uses its own DmsConfigurationService identity for Keycloak), so it must NOT also be a
    # Keycloak realm administrator -- that would expand the credential's blast radius well beyond
    # its documented CMS-bootstrap role.
    & "$PSScriptRoot/../../../docker-compose/setup-keycloak.ps1" -KeycloakServer $kc -Realm $realm -AdminUsername $kcAdmin -AdminPassword $kcAdminPw `
        -NewClientId $adminClientId -NewClientName "DMS Bootstrap Admin" `
        -ClientScopeName "edfi_admin_api/full_access" -NewClientSecret $adminClientSecret -SkipRealmAdmin
}

# --- Helper: provision one application and capture credentials --------------
function New-ReviewApplication {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'One-shot provisioning helper that creates CMS entities via API calls; no -WhatIf surface in this bootstrap script.')]
    param(
        [Parameter(Mandatory)][string]$CmsUrl,
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$Token,
        [Parameter(Mandatory)][long[]]$DataStoreIds,
        [string]$ClaimSet = $ClaimSetName,
        [long[]]$EducationOrganizationIds = $grandBendEdOrgIds,
        [string]$Tenant = ""
    )
    $vendorId = Add-Vendor -CmsUrl $CmsUrl -Company "Security Review Vendor ($Label)" `
        -NamespacePrefixes "uri://ed-fi.org" -AccessToken $Token -Tenant $Tenant
    # Keep this name <= 50 chars: it is copied into dmscs.ApiClient.Name (VARCHAR(50)).
    # Longest label "multi-tenant/tenant1" -> "Security Review (multi-tenant/tenant1)" = 38 chars.
    $app = Add-Application -CmsUrl $CmsUrl -ApplicationName "Security Review ($Label)" `
        -ClaimSetName $ClaimSet -VendorId $vendorId -AccessToken $Token `
        -EducationOrganizationIds $EducationOrganizationIds `
        -DataStoreIds $DataStoreIds -Tenant $Tenant
    $created.Add([pscustomobject]@{
        Environment = $Label
        ClaimSet    = $ClaimSet
        Key         = $app.Key
        Secret      = $app.Secret
        EdOrgIds    = ($EducationOrganizationIds -join ",")
    })
}

# --- 2. Single-tenant -------------------------------------------------------
Write-Output "== Bootstrapping single-tenant stack ($stConfig) =="
# The bootstrap admin client already exists in Keycloak (created above, or pre-existing under
# -SkipKeycloak); authenticate with it directly -- no CMS self-registration.
$stToken = Get-CmsToken -CmsUrl $stConfig -ClientId $adminClientId -ClientSecret $adminClientSecret
$stDataStoreId = Add-DataStore -CmsUrl $stConfig -AccessToken $stToken -Name "Single-Tenant Data Store" `
    -DataStoreType "Review" -PostgresHost "postgres" -PostgresDbName "edfi_st" -PostgresCredential $pgCredential
# Full-access single-tenant client. (Scope: single-tenant + two isolated tenants.)
New-ReviewApplication -CmsUrl $stConfig -Label "single-tenant/full" -Token $stToken `
    -DataStoreIds @([long]$stDataStoreId) -ClaimSet "E2E-NoFurtherAuthRequiredClaimSet"
# To demo school/district-level authorization, add an EdOrg-scoped client via the CMS, e.g.
# New-ReviewApplication ... -ClaimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" -EducationOrganizationIds @([long]255901)

# --- 3. Multi-tenant --------------------------------------------------------
Write-Output "== Bootstrapping multi-tenant stack ($mtConfig) =="
# Same Keycloak bootstrap admin client (shared realm) -- authenticate against mt-config directly.
$mtToken = Get-CmsToken -CmsUrl $mtConfig -ClientId $adminClientId -ClientSecret $adminClientSecret

foreach ($t in @($tenant1, $tenant2)) {
    Write-Output "  - tenant '$t'"
    try { Add-Tenant -CmsUrl $mtConfig -AccessToken $mtToken -TenantName $t | Out-Null }
    catch { Write-Warning "    tenant '$t' may already exist: $($_.Exception.Message)" }

    # Physical per-tenant isolation: each tenant gets its OWN data database, provisioned
    # and seeded separately. tenant1 -> edfi_mt, tenant2 -> edfi_mt_t2. The schoolYear
    # route context still qualifies the DMS data path. (Isolation verified -- see
    # docs/infrastructure.md.)
    $mtDb = @{ $tenant1 = "edfi_mt"; $tenant2 = "edfi_mt_t2" }[$t]
    if (-not $mtDb) { $mtDb = "edfi_mt" }
    $dsId = Add-DataStore -CmsUrl $mtConfig -AccessToken $mtToken -Name "MT Data Store ($t $schoolYear)" `
        -DataStoreType "SchoolYear" -PostgresHost "postgres" -PostgresDbName $mtDb `
        -PostgresCredential $pgCredential -Tenant $t
    Add-DataStoreContext -CmsUrl $mtConfig -AccessToken $mtToken -DataStoreId $dsId `
        -ContextKey "schoolYear" -ContextValue $schoolYear -Tenant $t | Out-Null
    New-ReviewApplication -CmsUrl $mtConfig -Label "multi-tenant/$t" -Token $mtToken `
        -DataStoreIds @([long]$dsId) -Tenant $t
}

# --- Summary ----------------------------------------------------------------
Write-Output "`n== API credentials created (store in your private vault / credentials doc -- NEVER commit to this repo) =="
# Format-List, not Format-Table: a table truncates the long key/secret columns at normal
# terminal widths, and the application secret cannot be retrieved after creation.
$created | Format-List
Write-Output "DMS endpoints:"
Write-Output "  single-tenant: $publicBaseUrl/st-dms/data/ed-fi/..."
Write-Output "  multi-tenant : $publicBaseUrl/mt-dms/{tenant}/$schoolYear/data/ed-fi/...   (tenant in PATH: $tenant1 or $tenant2)"
Write-Output "Token endpoint (advertised in Discovery): $publicBaseUrl/auth/realms/$realm/protocol/openid-connect/token  (Basic key:secret, grant_type=client_credentials). The <dms-base>/oauth/token proxy forwards here but needs a publicly-trusted cert."
