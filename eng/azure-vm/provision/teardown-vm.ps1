#Requires -Version 7
# SPDX-License-Identifier: Apache-2.0
#
# Permanently delete the entire resource group (VM, disk, IP, NSG, everything).
# Use when the engagement is over. Run from your workstation.
[CmdletBinding()]
param(
    [string]$ResourceGroup = "rg-dms-security-review",
    [switch]$Force
)
$ErrorActionPreference = "Stop"

if (-not $Force) {
    $confirm = Read-Host "Delete resource group '$ResourceGroup' and ALL its resources? Type the group name to confirm"
    if ($confirm -ne $ResourceGroup) { Write-Host "Aborted."; return }
}

Write-Host "Deleting resource group '$ResourceGroup'..." -ForegroundColor Yellow
az group delete -n $ResourceGroup --yes --no-wait -o none
if ($LASTEXITCODE -ne 0) { throw "az group delete failed ($LASTEXITCODE)" }
Write-Host "Deletion started (running in the background)." -ForegroundColor Green
