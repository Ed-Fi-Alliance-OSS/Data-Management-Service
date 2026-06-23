#Requires -Version 7
# SPDX-License-Identifier: Apache-2.0
#
# Sets up the DMS security-review environment ON the VM:
#   1. (optional) clone the private repo
#   2. create .env with strong generated secrets + the public host
#   3. obtain a TLS cert (Let's Encrypt if -LetsEncryptEmail given, else self-signed)
#   4. start the stack (up.sh)
#   5. wait for health and run bootstrap (clients, tenants, data stores)
#
# ⚠️ GAP: this does NOT provision the relational schema (dms-schema), stage
#    .bootstrap/ApiSchema, or seed multi-tenant, and it starts the DMS before bootstrap.
#    The deployed env was provisioned by hand — see provision/README.md "Known gaps"
#    (#1-#4; Jira refs DMS-1159 / DMS-1093 / DMS-1109 / DMS-1230 / DMS-1231).
#
# Run after cloud-init has finished and the repo is on the VM. Example:
#   pwsh ~/dms-src/eng/azure-vm/provision/setup-env.ps1 -PublicHost mylabel.eastus.cloudapp.azure.com -LetsEncryptEmail you@org.tld
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublicHost,                 # the VM FQDN
    [string]$RepoDir = "$HOME/dms-src/eng/azure-vm",
    [string]$RepoUrl = "",                                      # if set and RepoDir missing, git clone this
    [string]$LetsEncryptEmail = "",                            # if set, obtain a Let's Encrypt cert
    [switch]$LoadGrandbend,                                     # load the Grand Bend populated template
    [switch]$SkipBootstrap
)
$ErrorActionPreference = "Stop"

# --- secret helpers ---------------------------------------------------------
function New-ComplexSecret([int]$Length = 40) {
    # Meets CMS/Keycloak complexity: lower, upper, digit, special; 32-128 chars.
    # Special set avoids characters significant in .env / connection strings / URLs.
    $lower = 'abcdefghijkmnopqrstuvwxyz'; $upper = 'ABCDEFGHJKLMNPQRSTUVWXYZ'
    $digit = '23456789'; $special = '!@#%^*-_+.'
    $all = ($lower + $upper + $digit + $special).ToCharArray()
    $chars = @(
        (Get-Random -InputObject $lower.ToCharArray()),
        (Get-Random -InputObject $upper.ToCharArray()),
        (Get-Random -InputObject $digit.ToCharArray()),
        (Get-Random -InputObject $special.ToCharArray())
    )
    while ($chars.Count -lt $Length) { $chars += (Get-Random -InputObject $all) }
    -join ($chars | Sort-Object { Get-Random })
}
function New-Key32 {
    $a = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'.ToCharArray()
    -join (1..32 | ForEach-Object { Get-Random -InputObject $a })
}
function New-Base64Key {
    $b = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($b)
    [Convert]::ToBase64String($b)
}
function Set-EnvValue([string]$File, [string]$Key, [string]$Value) {
    $found = $false
    $out = foreach ($line in (Get-Content $File)) {
        if ($line -match "^\s*$([regex]::Escape($Key))=") { $found = $true; "$Key=$Value" } else { $line }
    }
    if (-not $found) { $out += "$Key=$Value" }
    Set-Content -Path $File -Value $out
}

# --- 1. repo ----------------------------------------------------------------
if (-not (Test-Path $RepoDir)) {
    if (-not $RepoUrl) { throw "RepoDir '$RepoDir' not found and no -RepoUrl provided. Clone the repo first (see provision/README.md)." }
    Write-Host "Cloning $RepoUrl -> $RepoDir" -ForegroundColor Cyan
    git clone $RepoUrl $RepoDir
}
$composeDir = Join-Path $RepoDir "compose"
if (-not (Test-Path $composeDir)) { throw "compose dir not found at $composeDir" }

# docker group sanity check (cloud-init adds the user; needs a fresh login to take effect).
docker info *> $null
if ($LASTEXITCODE -ne 0) { throw "Cannot talk to Docker. Log out and back in (so the 'docker' group applies), then re-run." }

Push-Location $composeDir
try {
    # --- 2. .env with generated secrets -------------------------------------
    if (-not (Test-Path ".env")) { Copy-Item ".env.example" ".env" }
    Write-Host "Writing .env (host + generated secrets)..." -ForegroundColor Cyan
    Set-EnvValue ".env" "PUBLIC_BASE_URL" "https://$PublicHost"
    Set-EnvValue ".env" "PUBLIC_HOST"     $PublicHost
    Set-EnvValue ".env" "POSTGRES_PASSWORD"                  (New-ComplexSecret)
    Set-EnvValue ".env" "KEYCLOAK_ADMIN_PASSWORD"            (New-ComplexSecret)
    Set-EnvValue ".env" "DMS_CONFIG_IDENTITY_CLIENT_SECRET"  (New-ComplexSecret)
    Set-EnvValue ".env" "CONFIG_SERVICE_CLIENT_SECRET"       (New-ComplexSecret)
    Set-EnvValue ".env" "BOOTSTRAP_ADMIN_CLIENT_SECRET"      (New-ComplexSecret)
    Set-EnvValue ".env" "PGADMIN_DEFAULT_PASSWORD"           (New-ComplexSecret)
    Set-EnvValue ".env" "DMS_CONFIG_DATABASE_ENCRYPTION_KEY" (New-Key32)
    Set-EnvValue ".env" "DMS_CONFIG_IDENTITY_ENCRYPTION_KEY" (New-Base64Key)

    # --- 3. TLS certificate -------------------------------------------------
    $insecureBootstrap = $true
    if ($LetsEncryptEmail) {
        Write-Host "Obtaining Let's Encrypt certificate for $PublicHost (port 80 must be reachable)..." -ForegroundColor Cyan
        sudo certbot certonly --standalone --non-interactive --agree-tos -m $LetsEncryptEmail -d $PublicHost
        if ($LASTEXITCODE -ne 0) { throw "certbot failed. Check that DNS resolves to this VM and port 80 is open." }
        $live = "/etc/letsencrypt/live/$PublicHost"
        sudo cp "$live/fullchain.pem" ssl/server.crt
        sudo cp "$live/privkey.pem"  ssl/server.key
        sudo chown "$(whoami)" ssl/server.crt ssl/server.key
        $insecureBootstrap = $false   # cert is valid (bootstrap still uses loopback below)
    }
    else {
        Write-Host "No -LetsEncryptEmail: generating a self-signed certificate." -ForegroundColor Yellow
        ./ssl/generate-certificate.sh $PublicHost
    }

    # --- 4. (optional) Grand Bend sample data -------------------------------
    if ($LoadGrandbend) {
        Write-Host "Loading Grand Bend sample data (populated template)..." -ForegroundColor Cyan
        Set-EnvValue ".env" "DMS_DEPLOY_DATABASE_ON_STARTUP" "false"   # template SQL creates the schema
        docker network inspect dms-sec *> $null
        if ($LASTEXITCODE -ne 0) { docker network create dms-sec | Out-Null }
        docker compose -f docker-compose.yml --env-file .env up -d postgres
        $pgDeadline = (Get-Date).AddMinutes(3)
        do {
            Start-Sleep -Seconds 5
            $pg = (docker inspect -f '{{.State.Health.Status}}' dms-sec-postgres 2>$null)
        } while ($pg -ne "healthy" -and (Get-Date) -lt $pgDeadline)
        bash ./seed/grandbend.sh
    }

    # --- 5. start -----------------------------------------------------------
    Write-Host "Starting the stack..." -ForegroundColor Cyan
    ./up.sh

    # --- 5. wait for health + bootstrap -------------------------------------
    Write-Host "Waiting for services to report healthy..." -ForegroundColor Cyan
    $deadline = (Get-Date).AddMinutes(8)
    $paths = @("st-dms", "st-config", "mt-dms", "mt-config")
    do {
        Start-Sleep -Seconds 10
        $healthy = $true
        foreach ($p in $paths) {
            $code = (curl -s -k -o /dev/null -w "%{http_code}" "https://localhost/$p/health")
            if ($code -ne "200") { $healthy = $false }
        }
    } while (-not $healthy -and (Get-Date) -lt $deadline)
    if ($healthy) { Write-Host "All services healthy." -ForegroundColor Green }
    else { Write-Warning "Not all services healthy yet. Check './logs.sh' before bootstrapping." }

    if (-not $SkipBootstrap) {
        Write-Host "Running bootstrap (over loopback)..." -ForegroundColor Cyan
        & "$composeDir/bootstrap/bootstrap.ps1" -BaseUrl "https://localhost" -Insecure
    }

    Write-Host "`n== Setup complete ==" -ForegroundColor Green
    Write-Host "Public URL: https://$PublicHost"
    Write-Host "Secrets were written to compose/.env. Record the generated values and the API"
    Write-Host "key/secret pairs above into docs/infrastructure.md (keep this repo private)."
}
finally {
    Pop-Location
}
