#Requires -Version 7
# SPDX-License-Identifier: Apache-2.0
#
# Renew the Let's Encrypt certificate (run ON the VM). certbot --standalone needs
# port 80, so the gateway is briefly stopped, then the renewed cert is copied into
# the gateway and it is restarted. Let's Encrypt certs last 90 days; for short
# engagements you may never need this.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublicHost,
    [string]$RepoDir = "$HOME/dms-src/eng/azure-vm"
)
$ErrorActionPreference = "Stop"
$composeDir = Join-Path $RepoDir "compose"
if (-not (Test-Path $composeDir)) { throw "compose dir not found at $composeDir" }

Push-Location $composeDir
try {
    Write-Host "Stopping gateway to free port 80..." -ForegroundColor Cyan
    docker compose -f docker-compose.yml --env-file .env stop gateway

    Write-Host "Renewing certificate..." -ForegroundColor Cyan
    sudo certbot renew --standalone --non-interactive

    $live = "/etc/letsencrypt/live/$PublicHost"
    # install (not cp) keeps the private key owner-only (cp would inherit the shell umask).
    sudo install -m 644 -o "$(whoami)" "$live/fullchain.pem" ssl/server.crt
    sudo install -m 600 -o "$(whoami)" "$live/privkey.pem"  ssl/server.key

    Write-Host "Restarting gateway..." -ForegroundColor Cyan
    docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d gateway
    Write-Host "Certificate renewed." -ForegroundColor Green
}
finally {
    Pop-Location
}
