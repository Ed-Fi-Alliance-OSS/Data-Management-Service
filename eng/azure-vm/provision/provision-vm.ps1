#Requires -Version 7
# SPDX-License-Identifier: Apache-2.0
#
# Provisions the Azure VM + network for the DMS security-review environment.
# Wraps the `az` CLI. Run from your workstation (logged in: `az login`).
#
# Creates: resource group, Ubuntu VM (Standard SSD), Standard static public IP
# with a DNS label, and NSG rules (SSH restricted to your IP; 80/443 for the app).
# A cloud-init payload installs Docker, PowerShell, git, and certbot on first boot.
#
# Example:
#   ./provision-vm.ps1 -DnsLabel your-label -AllowedSshCidr 203.0.113.7/32
[CmdletBinding()]
param(
    [string]$ResourceGroup = "rg-dms-security-review",
    [string]$Location = "eastus",
    [string]$VmName = "dms-sec-vm",
    [string]$VmSize = "Standard_B2ms",                 # 2 vCPU / 8 GB
    [Parameter(Mandatory)][string]$DnsLabel,           # -> <label>.<region>.cloudapp.azure.com (unique per region)
    [string]$AdminUsername = "edfi",
    [string]$SshPublicKeyPath = "$HOME/.ssh/id_rsa.pub",
    [Parameter(Mandatory)][string]$AllowedSshCidr,     # your admin IP, e.g. 203.0.113.7/32
    [string]$AppSourceCidr = "Internet",               # who can reach 80/443 (Internet for a pen test)
    [int]$OsDiskSizeGb = 64,
    [string]$Image = "Ubuntu2404"                       # fallback URN: Canonical:ubuntu-24_04-lts:server:latest
)

$ErrorActionPreference = "Stop"
# Parameter must NOT be named $Args: in a simple function the automatic $args variable
# (unbound arguments -- empty here) overwrites a bound parameter of that name after binding,
# so `az @Args` would splat nothing and every call would run bare `az`.
function Invoke-Az { param([string[]]$AzArgs) az @AzArgs; if ($LASTEXITCODE -ne 0) { throw "az $($AzArgs -join ' ') failed ($LASTEXITCODE)" } }

if (-not (Get-Command az -ErrorAction SilentlyContinue)) { throw "Azure CLI (az) not found. Install it and run 'az login'." }
az account show -o none 2>$null; if ($LASTEXITCODE -ne 0) { throw "Not logged in. Run 'az login' first." }
if (-not (Test-Path $SshPublicKeyPath)) { throw "SSH public key not found at $SshPublicKeyPath (generate with: ssh-keygen -t ed25519)." }

# Render cloud-init with the admin username substituted.
$cloudInit = Get-Content "$PSScriptRoot/cloud-init.yaml" -Raw
$tmpCloudInit = Join-Path ([System.IO.Path]::GetTempPath()) "dms-sec-cloud-init.yaml"
($cloudInit -replace "__ADMIN_USER__", $AdminUsername) | Set-Content -Path $tmpCloudInit -NoNewline

Write-Host "Creating resource group '$ResourceGroup' in $Location..." -ForegroundColor Cyan
Invoke-Az @("group", "create", "-n", $ResourceGroup, "-l", $Location, "-o", "none")

Write-Host "Creating VM '$VmName' ($VmSize)..." -ForegroundColor Cyan
Invoke-Az @(
    "vm", "create",
    "-g", $ResourceGroup, "-n", $VmName,
    "--image", $Image, "--size", $VmSize,
    "--admin-username", $AdminUsername,
    "--ssh-key-values", $SshPublicKeyPath,
    "--public-ip-sku", "Standard",
    "--public-ip-address-dns-name", $DnsLabel,
    "--storage-sku", "StandardSSD_LRS",
    "--os-disk-size-gb", "$OsDiskSizeGb",
    "--custom-data", $tmpCloudInit,
    "--nsg-rule", "NONE",
    "-o", "none"
)
Remove-Item $tmpCloudInit -ErrorAction SilentlyContinue

# Discover the NSG created for the VM and add inbound rules.
$nsgName = az network nsg list -g $ResourceGroup --query "[0].name" -o tsv
if (-not $nsgName) { throw "Could not find the VM's network security group." }
Write-Host "Adding NSG rules to '$nsgName'..." -ForegroundColor Cyan

Invoke-Az @("network","nsg","rule","create","-g",$ResourceGroup,"--nsg-name",$nsgName,"-n","allow-ssh",
    "--priority","1000","--access","Allow","--protocol","Tcp","--direction","Inbound",
    "--destination-port-ranges","22","--source-address-prefixes",$AllowedSshCidr,"-o","none")
Invoke-Az @("network","nsg","rule","create","-g",$ResourceGroup,"--nsg-name",$nsgName,"-n","allow-http",
    "--priority","1010","--access","Allow","--protocol","Tcp","--direction","Inbound",
    "--destination-port-ranges","80","--source-address-prefixes","Internet","-o","none")
Invoke-Az @("network","nsg","rule","create","-g",$ResourceGroup,"--nsg-name",$nsgName,"-n","allow-https",
    "--priority","1020","--access","Allow","--protocol","Tcp","--direction","Inbound",
    "--destination-port-ranges","443","--source-address-prefixes",$AppSourceCidr,"-o","none")

$fqdn = az vm show -d -g $ResourceGroup -n $VmName --query fqdns -o tsv
$ip   = az vm show -d -g $ResourceGroup -n $VmName --query publicIps -o tsv

Write-Host "`n== VM provisioned ==" -ForegroundColor Green
Write-Host "  FQDN : $fqdn"
Write-Host "  IP   : $ip"
Write-Host "  SSH  : ssh $AdminUsername@$fqdn"
Write-Host "`nNext steps:"
Write-Host "  1. Wait ~2-3 min for cloud-init to finish (Docker/pwsh/git/certbot install)."
Write-Host "     Verify:  ssh $AdminUsername@$fqdn 'cloud-init status --wait && docker --version && pwsh --version'"
Write-Host "  2. Get the repo onto the VM (deploy key clone or scp - see provision/README.md)."
Write-Host "  3. On the VM:  pwsh ~/dms-src/eng/azure-vm/provision/setup-env.ps1 -PublicHost $fqdn -LetsEncryptEmail you@org.tld"
Write-Host "`nCost control: ./stop-vm.ps1 to deallocate when idle; ./start-vm.ps1 before a session."
