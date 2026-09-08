# SPDX-License-Identifier: Apache-2.0
#
# Windows Server 2022 fallback (no WSL mirrored networking): forward Windows
# inbound 80/443 to the WSL2 distro's current NAT IP. The WSL IP changes across
# reboots, so this runs at startup (registered by setup-windows-host.ps1) to
# (re)apply the mapping. Boots WSL first so Docker/containers come up too.
[CmdletBinding()]
param(
    [string]$Distro = "Ubuntu",
    # string[] rather than int[]: the startup task invokes this via `powershell.exe -File`, which
    # passes "80,443" as ONE string argument -- an [int[]] parameter fails to bind it and the boot
    # task dies before restoring connectivity. Accept both spellings (`-Ports 80,443` interactively
    # binds two elements; the -File form binds one comma-joined element) and flatten below.
    [string[]]$Ports = @("80", "443")
)
$ErrorActionPreference = "Stop"

$portNumbers = [System.Collections.Generic.List[int]]::new()
foreach ($portToken in ($Ports | ForEach-Object { "$_" -split ',' })) {
    $trimmedToken = $portToken.Trim()
    if (-not $trimmedToken) { continue }
    $portNumber = 0
    if (-not [int]::TryParse($trimmedToken, [ref]$portNumber) -or $portNumber -lt 1 -or $portNumber -gt 65535) {
        throw "Invalid port '$trimmedToken'. Ports must be integers from 1 through 65535."
    }
    if (-not $portNumbers.Contains($portNumber)) { $portNumbers.Add($portNumber) }
}
if ($portNumbers.Count -eq 0) { throw "At least one TCP port is required." }

# Boot WSL (starts systemd -> docker -> containers) and read its IP.
wsl -d $Distro -u root -e true | Out-Null
# Prefer eth0's address explicitly. `hostname -I` returns ALL non-loopback IPs and, once the
# Docker bridge exists, includes 172.17.0.1 -- taking [0] could forward to docker0 instead of
# eth0. Fall back to the first hostname -I entry only if eth0 can't be read.
$primaryIp = wsl -d $Distro -- sh -c "ip -4 -o addr show eth0 2>/dev/null | awk '{print `$4}' | cut -d/ -f1" | Select-Object -First 1
$wslIp = if ($primaryIp) { $primaryIp.Trim() } else { "" }
if (-not $wslIp) {
    $fallbackAddresses = (wsl -d $Distro -- hostname -I) -split '\s+' | Where-Object { $_ }
    $wslIp = if ($fallbackAddresses) { $fallbackAddresses[0] } else { "" }
}
if (-not $wslIp) { throw "Could not determine the WSL IP for '$Distro'." }

foreach ($p in $portNumbers) {
    # Delete only THIS port's mapping before re-adding (the WSL IP changes across reboots), rather
    # than `portproxy reset`, which would wipe every unrelated portproxy mapping on the host.
    netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=$p 2>$null | Out-Null
    netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=$p connectaddress=$wslIp connectport=$p | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "netsh portproxy add for port $p -> $wslIp failed ($LASTEXITCODE)." }
    # Create the inbound rule only if absent (idempotent across reboots) but let a real failure
    # surface -- a blanket -ErrorAction SilentlyContinue would mask it before the success line below.
    if (-not (Get-NetFirewallRule -DisplayName "DMS-sec-$p" -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName "DMS-sec-$p" -Direction Inbound -LocalPort $p -Protocol TCP -Action Allow | Out-Null
    }
}
Write-Output "portproxy active: Windows :$($portNumbers -join ', ') -> $wslIp"
