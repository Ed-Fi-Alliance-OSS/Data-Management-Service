#Requires -RunAsAdministrator
# SPDX-License-Identifier: Apache-2.0
#
# Configures a Windows Server VM to host the Linux-container DMS stack via WSL2.
# Run in an ELEVATED PowerShell ON THE VM, AFTER WSL2 + Ubuntu are installed and
# initialized (see README.md step 5).
#
# Does: WSL networking (mirrored, or portproxy with -UsePortProxy), Windows firewall
# for 80/443, installs Docker + PowerShell + git + certbot inside WSL, enables systemd
# in WSL, and registers a startup task so WSL/Docker resume after a reboot.
#
#   .\setup-windows-host.ps1                 # Windows Server 2025 (mirrored networking)
#   .\setup-windows-host.ps1 -UsePortProxy   # Windows Server 2022 (portproxy)
[CmdletBinding()]
param(
    [string]$Distro = "Ubuntu",
    [int[]]$Ports = @(80, 443),
    [switch]$UsePortProxy,
    [string]$StateDir = "C:\dms-sec"
)
$ErrorActionPreference = "Stop"

# --- sanity: WSL + distro present -------------------------------------------
if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) { throw "WSL not found. Run 'wsl --install -d Ubuntu' and reboot first (README step 5)." }
$distros = (wsl --list --quiet) -replace "`0", "" | ForEach-Object { $_.Trim() } | Where-Object { $_ }
if ($distros -notcontains $Distro) { throw "WSL distro '$Distro' not found/initialized. Launch it once to create a user, then re-run." }

$wslUser = (wsl -d $Distro -- whoami).Trim()
Write-Output "WSL distro '$Distro' present (default user: $wslUser)."

# --- networking -------------------------------------------------------------
if (-not $UsePortProxy) {
    Write-Output "Configuring WSL mirrored networking (.wslconfig)..."
    $wslconfig = Join-Path $env:USERPROFILE ".wslconfig"
    @"
[wsl2]
networkingMode=mirrored
"@ | Set-Content -Path $wslconfig -Encoding ASCII
}
foreach ($p in $Ports) {
    New-NetFirewallRule -DisplayName "DMS-sec-$p" -Direction Inbound -LocalPort $p -Protocol TCP -Action Allow -ErrorAction SilentlyContinue | Out-Null
}

# Apply config (restarts WSL); next invocation boots with systemd once wsl.conf is set below.
wsl --shutdown

# --- provision inside WSL ---------------------------------------------------
Write-Output "Enabling systemd in WSL..."
wsl -d $Distro -u root -- bash -c "printf '[boot]\nsystemd=true\n' > /etc/wsl.conf"
wsl --shutdown

Write-Output "Installing Docker, PowerShell, git, certbot inside WSL (a few minutes)..."
$bash = @'
set -e
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y ca-certificates curl wget git apt-transport-https certbot
# Docker Engine + compose plugin (no Docker Desktop license needed)
curl -fsSL https://get.docker.com | sh
usermod -aG docker __WSLUSER__
# PowerShell 7 from the Microsoft apt repo
. /etc/os-release
wget -q "https://packages.microsoft.com/config/ubuntu/${VERSION_ID}/packages-microsoft-prod.deb" -O /tmp/pmc.deb
dpkg -i /tmp/pmc.deb
apt-get update
apt-get install -y powershell
systemctl enable --now docker
docker --version && pwsh --version
'@ -replace "__WSLUSER__", $wslUser
wsl -d $Distro -u root -- bash -lc $bash

# --- autostart on boot ------------------------------------------------------
New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType S4U -RunLevel Highest
if ($UsePortProxy) {
    Copy-Item -Path (Join-Path $PSScriptRoot "portproxy.ps1") -Destination (Join-Path $StateDir "portproxy.ps1") -Force
    $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$StateDir\portproxy.ps1`" -Distro $Distro"
}
else {
    # Mirrored mode: just need WSL (and thus Docker) booted.
    $action = New-ScheduledTaskAction -Execute "wsl.exe" -Argument "-d $Distro -u root -e true"
}
Register-ScheduledTask -TaskName "DMS-Start-WSL" -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null

# Apply now for this session.
if ($UsePortProxy) { & (Join-Path $PSScriptRoot "portproxy.ps1") -Distro $Distro -Ports $Ports }
else { wsl -d $Distro -u root -e true | Out-Null }

Write-Output "`nWindows host configured."
Write-Output "Next (inside WSL): clone the repo and run provision/setup-env.ps1 - see README step 7."
Write-Output "NOTE: validate that 'DMS-Start-WSL' brings containers back after a reboot;"
Write-Output "      headless WSL autostart can need a tweak on some builds."
