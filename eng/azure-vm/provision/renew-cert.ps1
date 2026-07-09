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
    Write-Output "Stopping gateway to free port 80..."
    docker compose -f docker-compose.yml --env-file .env stop gateway
    if ($LASTEXITCODE -ne 0) { throw "docker compose stop gateway failed ($LASTEXITCODE)." }

    try {
        Write-Output "Renewing certificate..."
        sudo certbot renew --standalone --non-interactive
        if ($LASTEXITCODE -ne 0) { throw "certbot renew failed ($LASTEXITCODE); the current certificate was kept." }

        $live = "/etc/letsencrypt/live/$PublicHost"
        # install (not cp) keeps the private key owner-only (cp would inherit the shell umask).
        sudo install -m 644 -o "$(whoami)" "$live/fullchain.pem" ssl/server.crt
        if ($LASTEXITCODE -ne 0) { throw "installing fullchain.pem -> ssl/server.crt failed ($LASTEXITCODE)." }
        sudo install -m 600 -o "$(whoami)" "$live/privkey.pem"  ssl/server.key
        if ($LASTEXITCODE -ne 0) { throw "installing privkey.pem -> ssl/server.key failed ($LASTEXITCODE)." }
    }
    finally {
        # Bring the gateway back even when renewal failed: serving the previous certificate
        # beats leaving the environment down. (Warning, not throw: it must not mask the
        # original renewal error.)
        Write-Output "Restarting gateway..."
        docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d gateway
        if ($LASTEXITCODE -ne 0) { Write-Warning "gateway restart failed ($LASTEXITCODE); run ./up.sh gateway." }
    }
    Write-Output "Certificate renewed."
}
finally {
    Pop-Location
}
