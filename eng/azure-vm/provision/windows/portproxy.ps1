# SPDX-License-Identifier: Apache-2.0
#
# Windows Server 2022 fallback (no WSL mirrored networking): forward Windows
# inbound 80/443 to the WSL2 distro's current NAT IP. The WSL IP changes across
# reboots, so this runs at startup (registered by setup-windows-host.ps1) to
# (re)apply the mapping. Boots WSL first so Docker/containers come up too.
[CmdletBinding()]
param(
    [string]$Distro = "Ubuntu",
    [int[]]$Ports = @(80, 443)
)
$ErrorActionPreference = "Stop"

# Boot WSL (starts systemd -> docker -> containers) and read its IP.
wsl -d $Distro -u root -e true | Out-Null
$wslIp = ((wsl -d $Distro -- hostname -I) -split '\s+' | Where-Object { $_ })[0]
if (-not $wslIp) { throw "Could not determine the WSL IP for '$Distro'." }

netsh interface portproxy reset | Out-Null
foreach ($p in $Ports) {
    netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=$p connectaddress=$wslIp connectport=$p | Out-Null
    New-NetFirewallRule -DisplayName "DMS-sec-$p" -Direction Inbound -LocalPort $p -Protocol TCP -Action Allow -ErrorAction SilentlyContinue | Out-Null
}
Write-Host "portproxy active: Windows :$($Ports -join ', ') -> $wslIp"
