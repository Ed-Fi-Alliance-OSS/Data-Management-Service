#Requires -Version 7
# SPDX-License-Identifier: Apache-2.0
#
# Post-start bootstrap for the DMS security-review environment.
#
# Sequence (mirrors eng/docker-compose/start-published-dms.ps1, adapted for two
# stacks reached through the gateway):
#   1. (keycloak mode) Create realm + the three CMS service clients.
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
param(
    [string]$EnvFile = "$PSScriptRoot/../.env",
    [ValidateSet("", "keycloak", "self-contained")]
    [string]$IdentityProvider = "",
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

if (-not $IdentityProvider) { $IdentityProvider = EnvVal "IDENTITY_PROVIDER" "keycloak" }
$publicBaseUrl = if ($BaseUrl) { $BaseUrl.TrimEnd("/") } else { (EnvVal "PUBLIC_BASE_URL" "https://localhost").TrimEnd("/") }
$realm         = EnvVal "KEYCLOAK_REALM" "edfi"
$pgPassword    = EnvVal "POSTGRES_PASSWORD"
$schoolYear    = EnvVal "MT_SCHOOL_YEAR" "2025"
$tenant1       = EnvVal "MT_TENANT_1" "tenant1"
$tenant2       = EnvVal "MT_TENANT_2" "tenant2"
$adminClientId     = EnvVal "BOOTSTRAP_ADMIN_CLIENT_ID" "dms-bootstrap-admin"
$adminClientSecret = EnvVal "BOOTSTRAP_ADMIN_CLIENT_SECRET"

# Grand Bend sample-data education organizations (district LEA 255901 + sample schools).
$grandBendEdOrgIds = @([long]255901, [long]255901001, [long]255901107)

$stConfig = "$publicBaseUrl/st-config/"   # trailing slash required for URI joining
$mtConfig = "$publicBaseUrl/mt-config/"

$created = [System.Collections.Generic.List[object]]::new()

# --- 1. Keycloak realm + service clients ------------------------------------
if ($IdentityProvider -eq "keycloak" -and -not $SkipKeycloak) {
    Write-Host "== Configuring Keycloak realm '$realm' and service clients ==" -ForegroundColor Cyan
    $kc = "$publicBaseUrl/auth"
    $kcAdmin = EnvVal "KEYCLOAK_ADMIN" "admin"
    $kcAdminPw = EnvVal "KEYCLOAK_ADMIN_PASSWORD"

    & "$PSScriptRoot/../../../docker-compose/setup-keycloak.ps1" -KeycloakServer $kc -Realm $realm -AdminUsername $kcAdmin -AdminPassword $kcAdminPw `
        -NewClientId "DmsConfigurationService" -NewClientName "DMS Configuration Service" `
        -ClientScopeName "edfi_admin_api/full_access" -NewClientSecret (EnvVal "DMS_CONFIG_IDENTITY_CLIENT_SECRET")

    & "$PSScriptRoot/../../../docker-compose/setup-keycloak.ps1" -KeycloakServer $kc -Realm $realm -AdminUsername $kcAdmin -AdminPassword $kcAdminPw `
        -NewClientId "CMSReadOnlyAccess" -NewClientName "CMS ReadOnly Access" `
        -ClientScopeName "edfi_admin_api/readonly_access" -NewClientSecret (EnvVal "CONFIG_SERVICE_CLIENT_SECRET")
}
elseif ($IdentityProvider -eq "self-contained") {
    Write-Warning "Identity provider 'self-contained' selected. This scaffold provisions Keycloak only."
    Write-Warning "For self-contained, vendor eng/docker-compose/setup-openiddict.ps1 and provision OpenIddict keys/clients here."
}

# --- Helper: provision one application and capture credentials --------------
function New-ReviewApplication {
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
Write-Host "== Bootstrapping single-tenant stack ($stConfig) ==" -ForegroundColor Cyan
Add-CmsClient -CmsUrl $stConfig -ClientId $adminClientId -ClientSecret $adminClientSecret -DisplayName "Bootstrap Admin"
$stToken = Get-CmsToken -CmsUrl $stConfig -ClientId $adminClientId -ClientSecret $adminClientSecret
$stDataStoreId = Add-DataStore -CmsUrl $stConfig -AccessToken $stToken -Name "Single-Tenant Data Store" `
    -DataStoreType "Review" -PostgresHost "postgres" -PostgresDbName "edfi_st" -PostgresPassword $pgPassword
# Full-access single-tenant client. (Scope: single-tenant + two isolated tenants.)
New-ReviewApplication -CmsUrl $stConfig -Label "single-tenant/full" -Token $stToken `
    -DataStoreIds @([long]$stDataStoreId) -ClaimSet "E2E-NoFurtherAuthRequiredClaimSet"
# To demo school/district-level authorization, add an EdOrg-scoped client via the CMS, e.g.
# New-ReviewApplication ... -ClaimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" -EducationOrganizationIds @([long]255901)

# --- 3. Multi-tenant --------------------------------------------------------
Write-Host "== Bootstrapping multi-tenant stack ($mtConfig) ==" -ForegroundColor Cyan
Add-CmsClient -CmsUrl $mtConfig -ClientId $adminClientId -ClientSecret $adminClientSecret -DisplayName "Bootstrap Admin"
$mtToken = Get-CmsToken -CmsUrl $mtConfig -ClientId $adminClientId -ClientSecret $adminClientSecret

foreach ($t in @($tenant1, $tenant2)) {
    Write-Host "  - tenant '$t'" -ForegroundColor DarkCyan
    try { Add-Tenant -CmsUrl $mtConfig -AccessToken $mtToken -TenantName $t | Out-Null }
    catch { Write-Warning "    tenant '$t' may already exist: $($_.Exception.Message)" }

    # Physical per-tenant isolation: each tenant gets its OWN data database, provisioned
    # and seeded separately. tenant1 -> edfi_mt, tenant2 -> edfi_mt_t2. The schoolYear
    # route context still qualifies the DMS data path. (Isolation verified — see
    # docs/infrastructure.md.)
    $mtDb = @{ $tenant1 = "edfi_mt"; $tenant2 = "edfi_mt_t2" }[$t]
    if (-not $mtDb) { $mtDb = "edfi_mt" }
    $dsId = Add-DataStore -CmsUrl $mtConfig -AccessToken $mtToken -Name "MT Data Store ($t $schoolYear)" `
        -DataStoreType "SchoolYear" -PostgresHost "postgres" -PostgresDbName $mtDb `
        -PostgresPassword $pgPassword -Tenant $t
    Add-DataStoreContext -CmsUrl $mtConfig -AccessToken $mtToken -DataStoreId $dsId `
        -ContextKey "schoolYear" -ContextValue $schoolYear -Tenant $t | Out-Null
    New-ReviewApplication -CmsUrl $mtConfig -Label "multi-tenant/$t" -Token $mtToken `
        -DataStoreIds @([long]$dsId) -Tenant $t
}

# --- Summary ----------------------------------------------------------------
Write-Host "`n== API credentials created (store in your private vault / credentials doc -- NEVER commit to this repo) ==" -ForegroundColor Green
$created | Format-Table -AutoSize
Write-Host "DMS endpoints:"
Write-Host "  single-tenant: $publicBaseUrl/st-dms/data/ed-fi/..."
Write-Host "  multi-tenant : $publicBaseUrl/mt-dms/{tenant}/$schoolYear/data/ed-fi/...   (tenant in PATH: $tenant1 or $tenant2)"
Write-Host "Token endpoint (advertised in Discovery): $publicBaseUrl/auth/realms/$realm/protocol/openid-connect/token  (Basic key:secret, grant_type=client_credentials). The <dms-base>/oauth/token proxy forwards here but needs a publicly-trusted cert."
