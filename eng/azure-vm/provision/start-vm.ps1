#Requires -Version 7
# SPDX-License-Identifier: Apache-2.0
#
# Start the security-review VM before an audit session.
# Docker starts on boot and containers (restart: unless-stopped) resume from their
# persisted volumes - no re-bootstrap needed. Run from your workstation.
[CmdletBinding()]
param(
    [string]$ResourceGroup = "rg-dms-security-review",
    [string]$VmName = "dms-sec-vm"
)
$ErrorActionPreference = "Stop"

Write-Output "Starting VM '$VmName'..."
az vm start -g $ResourceGroup -n $VmName -o none
if ($LASTEXITCODE -ne 0) { throw "az vm start failed ($LASTEXITCODE)" }

$fqdn = az vm show -d -g $ResourceGroup -n $VmName --query fqdns -o tsv
if ($LASTEXITCODE -ne 0 -or -not $fqdn) { Write-Warning "Could not read the VM FQDN (az vm show failed); the VM was started."; $fqdn = "<vm-fqdn>" }
Write-Output "VM running. Containers resume automatically."
Write-Output "  https://$fqdn"
Write-Output "  Allow ~30-60s for services to report healthy."
