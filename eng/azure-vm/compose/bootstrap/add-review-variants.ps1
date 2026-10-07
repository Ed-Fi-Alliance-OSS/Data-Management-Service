#Requires -Version 7
# SPDX-License-Identifier: Apache-2.0
#
# Adds the review-variant applications (see review-variants.psm1) to the single-tenant and both
# multi-tenant deployments: SISVendor at district 255901, SISVendor at school 255901107,
# AssessmentVendor at district 255901 with namespace uri://one.example.com, and EdFiSandbox at
# district 255901 -- 4 per deployment, 12 key/secret pairs.
#
# bootstrap.ps1 already does this on a fresh deploy. Run this script to add the variants to an
# environment bootstrapped before they existed. It is re-runnable: existing variants are skipped.
#
# Usage (on the VM, from eng/azure-vm/compose):
#   pwsh ./bootstrap/add-review-variants.ps1 -BaseUrl https://localhost -Insecure
#   pwsh ./bootstrap/add-review-variants.ps1 -BaseUrl https://localhost -Insecure -OutFile ~/review-variants.json

[CmdletBinding()]
param(
    [string]$EnvFile = "$PSScriptRoot/../.env",
    # Gateway base URL (default: PUBLIC_BASE_URL from .env). Use https://localhost on the VM.
    [string]$BaseUrl = "",
    # Also write the created credentials as JSON (mode 600), e.g. for http/sample-variants.py.
    [string]$OutFile = "",
    [switch]$Insecure
)

$ErrorActionPreference = "Stop"
Import-Module "$PSScriptRoot/../../../Dms-Management.psm1" -Force
Import-Module "$PSScriptRoot/review-variants.psm1" -Force

if ($Insecure) {
    # Invoke-RestMethod/Invoke-WebRequest run in each module's own session state, which does not
    # inherit $global:PSDefaultParameterValues, so set the defaults in every module that calls them.
    $global:PSDefaultParameterValues['Invoke-RestMethod:SkipCertificateCheck'] = $true
    $global:PSDefaultParameterValues['Invoke-WebRequest:SkipCertificateCheck'] = $true
    foreach ($module in @(Get-Module -All Dms-Management) + @(Get-Module review-variants)) {
        & $module {
            $PSDefaultParameterValues['Invoke-RestMethod:SkipCertificateCheck'] = $true
            $PSDefaultParameterValues['Invoke-WebRequest:SkipCertificateCheck'] = $true
        }
    }
}

$envValues = Read-ReviewEnvFile -Path $EnvFile
function EnvVal([string]$key, [string]$default = "") {
    if ($envValues.ContainsKey($key) -and $envValues[$key]) { return $envValues[$key] }
    return $default
}

$publicBaseUrl = if ($BaseUrl) { $BaseUrl } else { EnvVal "PUBLIC_BASE_URL" "https://localhost" }
$deployments = Get-ReviewDeployment -BaseUrl $publicBaseUrl -SchoolYear (EnvVal "MT_SCHOOL_YEAR" "2025") `
    -Tenant1 (EnvVal "MT_TENANT_1" "tenant1") -Tenant2 (EnvVal "MT_TENANT_2" "tenant2")
$created = @(Add-ReviewVariantSet -Deployment $deployments `
        -AdminClientId (EnvVal "BOOTSTRAP_ADMIN_CLIENT_ID" "dms-bootstrap-admin") `
        -AdminClientSecret (EnvVal "BOOTSTRAP_ADMIN_CLIENT_SECRET"))

Write-Output "`n== Review-variant API credentials created: $($created.Count) (store in your private vault / credentials doc -- NEVER commit to this repo) =="
# Format-List, not Format-Table: a table truncates the key/secret columns, and a secret cannot be
# retrieved after creation.
$created | Format-List
if ($OutFile) {
    if (-not $IsWindows) {
        # Create the file 600 before any secret is written to it.
        New-Item -ItemType File -Path $OutFile -Force | Out-Null
        chmod 600 $OutFile
    }
    ConvertTo-Json -InputObject $created | Set-Content -Path $OutFile -NoNewline
    Write-Output "Also written to $OutFile."
}
