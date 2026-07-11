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
        # Stage BOTH files, then move them into place together -- installing directly could
        # leave a mismatched cert/key pair if the second install fails, and the finally block
        # below would restart the gateway onto that mismatch (nginx refuses to start).
        sudo install -m 644 -o "$(whoami)" "$live/fullchain.pem" ssl/server.crt.tmp
        if ($LASTEXITCODE -ne 0) { throw "staging fullchain.pem failed ($LASTEXITCODE)." }
        sudo install -m 600 -o "$(whoami)" "$live/privkey.pem"  ssl/server.key.tmp
        if ($LASTEXITCODE -ne 0) { throw "staging privkey.pem failed ($LASTEXITCODE)." }
        Move-Item -Force ssl/server.crt.tmp ssl/server.crt
        Move-Item -Force ssl/server.key.tmp ssl/server.key
    }
    finally {
        # Bring the gateway back even when renewal failed: serving the previous certificate
        # beats leaving the environment down. (Warning, not throw: it must not mask the
        # original renewal error.)
        Write-Output "Restarting gateway..."
        docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d gateway
        $script:gatewayUp = ($LASTEXITCODE -eq 0)
        if (-not $script:gatewayUp) { Write-Warning "gateway restart failed ($LASTEXITCODE); run ./up.sh gateway." }
    }
    # Only claim success if the renewed cert is actually being served -- otherwise the message
    # would contradict the gateway-restart warning above.
    if ($script:gatewayUp) { Write-Output "Certificate renewed and gateway restarted." }
    else { Write-Warning "Certificate renewed, but the gateway is NOT running -- start it with ./up.sh gateway." }
}
finally {
    Pop-Location
}
