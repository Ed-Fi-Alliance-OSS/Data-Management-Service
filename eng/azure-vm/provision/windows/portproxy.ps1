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
# Prefer eth0's address explicitly. `hostname -I` returns ALL non-loopback IPs and, once the
# Docker bridge exists, includes 172.17.0.1 -- taking [0] could forward to docker0 instead of
# eth0. Fall back to the first hostname -I entry only if eth0 can't be read.
$wslIp = (wsl -d $Distro -- sh -c "ip -4 -o addr show eth0 2>/dev/null | awk '{print `$4}' | cut -d/ -f1" | Select-Object -First 1).Trim()
if (-not $wslIp) {
    $wslIp = ((wsl -d $Distro -- hostname -I) -split '\s+' | Where-Object { $_ })[0]
}
if (-not $wslIp) { throw "Could not determine the WSL IP for '$Distro'." }

foreach ($p in $Ports) {
    # Delete only THIS port's mapping before re-adding (the WSL IP changes across reboots), rather
    # than `portproxy reset`, which would wipe every unrelated portproxy mapping on the host.
    netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=$p 2>$null | Out-Null
    netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=$p connectaddress=$wslIp connectport=$p | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "netsh portproxy add for port $p -> $wslIp failed ($LASTEXITCODE)." }
    New-NetFirewallRule -DisplayName "DMS-sec-$p" -Direction Inbound -LocalPort $p -Protocol TCP -Action Allow -ErrorAction SilentlyContinue | Out-Null
}
Write-Output "portproxy active: Windows :$($Ports -join ', ') -> $wslIp"
