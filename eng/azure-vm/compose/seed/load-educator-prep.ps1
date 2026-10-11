#Requires -Version 7
# SPDX-License-Identifier: Apache-2.0
#
# Loads the Data Standard 6.1 educator-preparation sample data into the security-review
# deployments, after seed/grandbend.sh restored the DMS DS 6.1 populated template. The template
# deliberately leaves these files out; adding them back gives each deployment the same Grand Bend
# data as the ODS/API DS 6.1 populated template (1959 students in 9 schools instead of 960 in 3).
#
# Two passes per deployment, both through the DMS API with a temporary EdFiSandbox application
# that is removed afterwards:
#   1. the excluded educator-prep files (Candidate.xml, *-EdPrep.xml, ...);
#   2. the template files that reference educator-prep students (their records were skipped by
#      the template build because those students did not exist yet).
#
# Prerequisites: the deployment runs Data Standard 6.1 (provision/UPDATE.md or REDEPLOY.md), the
# DMS services are healthy, and SampleDataDirectory is "Samples/Sample XML" from
# Ed-Fi-Data-Standard v6.1.0, e.g.:
#   git clone --depth 1 --branch v6.1.0 --filter=blob:none --sparse https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-Data-Standard.git ~/ds61
#   git -C ~/ds61 sparse-checkout set "Samples/Sample XML"
#
# Usage (on the VM, from eng/azure-vm/compose):
#   pwsh ./seed/load-educator-prep.ps1 -SampleDataDirectory "$HOME/ds61/Samples/Sample XML" -BaseUrl https://localhost -Insecure

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SampleDataDirectory,
    [string]$EnvFile = "$PSScriptRoot/../.env",
    # Gateway base URL for the Configuration Service calls (default: PUBLIC_BASE_URL from .env).
    [string]$BaseUrl = "",
    # Which deployments to load: ST, T1, T2 (default: all three).
    [ValidateSet("ST", "T1", "T2")][string[]]$Deployment = @("ST", "T1", "T2"),
    [string]$WorkDirectory = "$PSScriptRoot/../.bootstrap/educator-prep",
    [string]$Network = "dms-sec",
    [switch]$Insecure
)

$ErrorActionPreference = "Stop"
$engRoot = Join-Path $PSScriptRoot "../../.."
Import-Module (Join-Path $engRoot "Dms-Management.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "../bootstrap/review-variants.psm1") -Force
Import-Module (Join-Path $PSScriptRoot "educator-prep.psm1") -Force
# Fail fast if an import change ever hides a helper this script calls directly.
foreach ($command in @("Disable-ReviewCertificateCheck", "Read-ReviewEnvFile", "Get-ReviewDeployment", "Get-EducatorPrepLoadFile", "Get-EducatorPrepRepostFile",
        "Get-ReviewBulkLoadClientDirectory", "Get-ReviewEducationOrganizationId", "Invoke-EducatorPrepLoad")) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) { throw "Helper '$command' is not available after the module imports." }
}

if ($Insecure) { Disable-ReviewCertificateCheck -Module (Get-Module educator-prep), (Get-Module review-variants) }

$sampleDirectory = (Resolve-Path -LiteralPath $SampleDataDirectory).Path
$loadFiles = @(Get-EducatorPrepLoadFile -SampleDataDirectory $sampleDirectory)
if ($loadFiles.Count -eq 0) {
    throw "No educator-prep files found in '$sampleDirectory'. Point -SampleDataDirectory at Ed-Fi-Data-Standard v6.1.0 'Samples/Sample XML'."
}
$repostFiles = @(Get-EducatorPrepRepostFile -SampleDataDirectory $sampleDirectory)
Write-Output "Educator-prep files ($($loadFiles.Count)): $($loadFiles -join ', ')"
Write-Output "Re-posted template files ($($repostFiles.Count)): $($repostFiles -join ', ')"

# Stage each pass in its own directory: the BulkLoadClient loads every file in its data directory.
$work = (New-Item -ItemType Directory -Force -Path $WorkDirectory).FullName
$stages = [ordered]@{ "educator-prep" = $loadFiles; "repost" = $repostFiles }
foreach ($stage in $stages.Keys) {
    $stageDirectory = Join-Path $work $stage
    Remove-Item -LiteralPath $stageDirectory -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null
    foreach ($name in $stages[$stage]) { Copy-Item -LiteralPath (Join-Path $sampleDirectory $name) -Destination $stageDirectory }
}

$clientDirectory = Get-ReviewBulkLoadClientDirectory -WorkDirectory $work

# Scope the loader to every education organization the full sample set defines, as Build-Template
# does, so records that reference core Grand Bend EdOrgs authorize too.
$edOrgIds = Get-ReviewEducationOrganizationId -SampleDataDirectory $sampleDirectory

$envValues = Read-ReviewEnvFile -Path $EnvFile
function EnvVal([string]$key, [string]$default = "") {
    if ($envValues.ContainsKey($key) -and $envValues[$key]) { return $envValues[$key] }
    return $default
}
$publicBaseUrl = if ($BaseUrl) { $BaseUrl } else { EnvVal "PUBLIC_BASE_URL" "https://localhost" }
$deployments = Get-ReviewDeployment -BaseUrl $publicBaseUrl -SchoolYear (EnvVal "MT_SCHOOL_YEAR" "2025") `
    -Tenant1 (EnvVal "MT_TENANT_1" "tenant1") -Tenant2 (EnvVal "MT_TENANT_2" "tenant2") |
    Where-Object { $Deployment -contains $_.Code }

$results = [System.Collections.Generic.List[object]]::new()
foreach ($d in $deployments) {
    foreach ($stage in $stages.Keys) {
        if ($stages[$stage].Count -eq 0) { continue }
        $result = Invoke-EducatorPrepLoad -Deployment $d -DataDirectory (Join-Path $work $stage) -LogDirectory $work `
            -AdminClientId (EnvVal "BOOTSTRAP_ADMIN_CLIENT_ID" "dms-bootstrap-admin") `
            -AdminClientSecret (EnvVal "BOOTSTRAP_ADMIN_CLIENT_SECRET") `
            -BulkLoadClientDirectory $clientDirectory -EducationOrganizationIds $edOrgIds -Network $Network
        $results.Add($result)
        Write-Output ("[{0}] {1}: exit {2} in {3:mm\:ss} (log: {4})" -f $result.Deployment, $result.Data, $result.ExitCode, $result.Elapsed, $result.LogPath)
    }
}

$failed = @($results | Where-Object { $_.ExitCode -ne 0 })
if ($failed.Count -gt 0) {
    throw "$($failed.Count) load(s) failed; check the logs above. Re-running is safe: existing records are upserted."
}
Write-Output "Educator-prep data loaded. Verify with seed/check-ods-parity.py."
