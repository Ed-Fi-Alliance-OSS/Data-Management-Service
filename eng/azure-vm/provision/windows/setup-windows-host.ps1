#Requires -RunAsAdministrator
# SPDX-License-Identifier: Apache-2.0
#
# Configures a Windows Server VM to host the Linux-container DMS stack via WSL2.
# Run in an ELEVATED PowerShell ON THE VM, AFTER WSL2 + Ubuntu are installed and
# initialized (see README.md step 5).
#
# Does: WSL networking (mirrored, or portproxy with -UsePortProxy), Windows + Hyper-V
# firewall for 80/443, installs Docker + PowerShell + git + certbot inside WSL, enables
# systemd in WSL, and registers a startup task so WSL/Docker resume after a reboot.
# Mirrored mode is VERIFIED after WSL restarts; when it did not apply (older build), the
# script falls back to portproxy automatically.
#
#   .\setup-windows-host.ps1                 # attempt mirrored networking, auto-fallback to portproxy
#   .\setup-windows-host.ps1 -UsePortProxy   # skip the attempt (the expected path on Windows Server)
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
$useProxy = [bool]$UsePortProxy
if (-not $useProxy) {
    Write-Output "Configuring WSL mirrored networking (.wslconfig)..."
    $wslconfig = Join-Path $env:USERPROFILE ".wslconfig"
    @"
[wsl2]
networkingMode=mirrored
"@ | Set-Content -Path $wslconfig -Encoding ASCII

    # Mirrored networking is gated by the HYPER-V firewall, not (only) the classic Windows
    # firewall below: without an explicit Hyper-V allowance, inbound 80/443 never reaches WSL
    # (the Let's Encrypt HTTP-01 challenge and the gateway both time out). Cmdlet presence does
    # NOT guarantee mirrored mode works (Windows Server 2025 ships the cmdlets while mirrored
    # still fails to apply -- open microsoft/WSL#11154); the wslinfo verification below is
    # authoritative. Missing cmdlets DO mean an old build -- fall back immediately.
    if (Get-Command New-NetFirewallHyperVRule -ErrorAction SilentlyContinue) {
        $wslVmCreatorId = '{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}'   # WSL's Hyper-V VM creator
        foreach ($p in $Ports) {
            if (-not (Get-NetFirewallHyperVRule -Name "DMS-sec-hv-$p" -ErrorAction SilentlyContinue)) {
                New-NetFirewallHyperVRule -Name "DMS-sec-hv-$p" -DisplayName "DMS-sec WSL inbound $p" `
                    -Direction Inbound -VMCreatorId $wslVmCreatorId -Protocol TCP -LocalPorts $p -Action Allow | Out-Null
            }
        }
    }
    else {
        Write-Warning "Hyper-V firewall cmdlets not found; mirrored networking cannot receive inbound traffic here. Falling back to portproxy."
        $useProxy = $true
    }
}
foreach ($p in $Ports) {
    if (-not (Get-NetFirewallRule -DisplayName "DMS-sec-$p" -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName "DMS-sec-$p" -Direction Inbound -LocalPort $p -Protocol TCP -Action Allow | Out-Null
    }
}

# Apply config (restarts WSL); next invocation boots with systemd once wsl.conf is set below.
wsl --shutdown

# --- provision inside WSL ---------------------------------------------------
Write-Output "Enabling systemd in WSL..."
wsl -d $Distro -u root -- bash -c "printf '[boot]\nsystemd=true\n' > /etc/wsl.conf"
if ($LASTEXITCODE -ne 0) { throw "Enabling systemd in WSL failed ($LASTEXITCODE)." }
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
if ($LASTEXITCODE -ne 0) { throw "WSL provisioning (Docker/pwsh/git/certbot install) failed ($LASTEXITCODE)." }

# Verify mirrored networking actually applied. It is currently a Windows 11 (22H2+) feature:
# Windows Server builds -- including 2025 -- commonly fail to enable it and WSL silently stays
# NAT'd (open microsoft/WSL#11154), so inbound 80/443 would never arrive. Detect that here and
# fall back to portproxy instead of handing over a broken host.
if (-not $useProxy) {
    $netMode = (wsl -d $Distro -- wslinfo --networking-mode 2>$null | Out-String).Trim()
    if ($netMode -ne "mirrored") {
        Write-Warning "WSL networking mode is '$netMode', not 'mirrored' (unsupported build?). Falling back to portproxy."
        $useProxy = $true
    }
}

# --- autostart on boot ------------------------------------------------------
New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType S4U -RunLevel Highest
if ($useProxy) {
    Copy-Item -Path (Join-Path $PSScriptRoot "portproxy.ps1") -Destination (Join-Path $StateDir "portproxy.ps1") -Force
    # Pass -Ports too, so custom ports survive a reboot (portproxy.ps1 otherwise defaults to 80,443).
    $portList = $Ports -join ','
    $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$StateDir\portproxy.ps1`" -Distro $Distro -Ports $portList"
}
else {
    # Mirrored mode: just need WSL (and thus Docker) booted.
    $action = New-ScheduledTaskAction -Execute "wsl.exe" -Argument "-d $Distro -u root -e true"
}
Register-ScheduledTask -TaskName "DMS-Start-WSL" -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null

# Apply now for this session.
if ($useProxy) { & (Join-Path $PSScriptRoot "portproxy.ps1") -Distro $Distro -Ports $Ports }
else { wsl -d $Distro -u root -e true | Out-Null }

Write-Output "`nWindows host configured."
Write-Output "Next (inside WSL): clone the repo and run provision/setup-env.ps1 - see README step 7."
Write-Output "NOTE: validate that 'DMS-Start-WSL' brings containers back after a reboot;"
Write-Output "      headless WSL autostart can need a tweak on some builds."
