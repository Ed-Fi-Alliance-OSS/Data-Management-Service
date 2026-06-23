#Requires -Version 7
# SPDX-License-Identifier: Apache-2.0
#
# Stop (DEALLOCATE) the security-review VM to halt compute billing when idle.
# Deallocate releases the compute reservation; you keep paying only for the disk
# and the static public IP. Data volumes and the Keycloak realm persist.
# A plain power-off from inside the OS does NOT stop billing - use this instead.
[CmdletBinding()]
param(
    [string]$ResourceGroup = "rg-dms-security-review",
    [string]$VmName = "dms-sec-vm"
)
$ErrorActionPreference = "Stop"

Write-Host "Deallocating VM '$VmName' (stops compute billing)..." -ForegroundColor Cyan
az vm deallocate -g $ResourceGroup -n $VmName -o none
if ($LASTEXITCODE -ne 0) { throw "az vm deallocate failed ($LASTEXITCODE)" }

Write-Host "VM deallocated. Compute billing stopped; disk + public IP still incur (small) cost." -ForegroundColor Green
Write-Host "Start again before the next session with ./start-vm.ps1"
