#Requires -Version 7
# SPDX-License-Identifier: Apache-2.0
#
# Sets up the DMS security-review environment ON the VM:
#   1. (optional) clone the private repo
#   2. create .env with strong generated secrets + the public host
#   3. obtain a TLS cert (Let's Encrypt if -LetsEncryptEmail given, else self-signed)
#   4. start identity + CMS (everything EXCEPT the DMS services)
#   5. wait for identity/CMS health, then run bootstrap (clients, tenants, data stores)
#   6. (with -StartDms, after the relational schema is provisioned) start the DMS services
#
# ⚠️ GAP: this does NOT provision the relational schema (dms-schema), stage
#    .bootstrap/ApiSchema, or seed data. Because of that it bootstraps but does NOT start the
#    DMS services by default (a DMS booted against an unprovisioned data DB won't pass /health):
#    provision the schema, then start them with -StartDms (or `./up.sh st-dms mt-dms`).
#    See provision/README.md "What setup-env.ps1 does NOT do" and docs/infrastructure.md
#    "Provisioning method".
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
    [switch]$SkipBootstrap,
    [switch]$StartDms                                           # start the DMS services (only after the relational schema is provisioned)
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

    # --- 5. start identity + CMS (NOT the DMS services yet) -----------------
    # The DMS eagerly loads its data stores from the CMS on boot and fail-fasts if the Keycloak
    # realm / clients / data stores don't yet exist (docs/infrastructure.md issue 3). So bring up
    # everything EXCEPT st-dms/mt-dms, bootstrap, and only then start the DMS services.
    docker network inspect dms-sec *> $null
    if ($LASTEXITCODE -ne 0) { docker network create dms-sec | Out-Null }

    Write-Host "Starting infrastructure (postgres, keycloak, config services, gateway)..." -ForegroundColor Cyan
    # --no-deps so the gateway (which depends_on the DMS services) does not pull them up early;
    # the gateway resolves upstreams at request time, so it starts fine without the DMS backends.
    docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d --no-deps `
        postgres keycloak st-config mt-config pgadmin gateway

    Write-Host "Waiting for Keycloak + config services to report healthy..." -ForegroundColor Cyan
    $deadline = (Get-Date).AddMinutes(8)
    do {
        Start-Sleep -Seconds 10
        $healthy = $true
        foreach ($p in @("st-config", "mt-config")) {
            $code = (curl -s -k -o /dev/null -w "%{http_code}" "https://localhost/$p/health")
            if ($code -ne "200") { $healthy = $false }
        }
        $kcCode = (curl -s -k -o /dev/null -w "%{http_code}" "https://localhost/auth/realms/master")
        if ($kcCode -ne "200") { $healthy = $false }
    } while (-not $healthy -and (Get-Date) -lt $deadline)
    if ($healthy) { Write-Host "Identity + config services healthy." -ForegroundColor Green }
    else { Write-Warning "Identity/config not all healthy yet. Check './logs.sh' before continuing." }

    if (-not $SkipBootstrap) {
        Write-Host "Running bootstrap (over loopback)..." -ForegroundColor Cyan
        & "$composeDir/bootstrap/bootstrap.ps1" -BaseUrl "https://localhost" -Insecure
    }

    # --- 6. relational schema (MANUAL) + start the DMS services -------------
    # The DMS data DBs use the relational backend and are provisioned OUT OF BAND with the
    # dms-schema tool (docs/infrastructure.md "Provisioning method", step 2). This script does
    # NOT provision schema, so by default it does not start the DMS services -- a DMS booted
    # against an unprovisioned data DB will not pass /health. Provision the schema, then re-run
    # with -StartDms (or `./up.sh st-dms mt-dms`).
    if ($StartDms) {
        Write-Host "Starting DMS services..." -ForegroundColor Cyan
        docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d st-dms mt-dms
        Write-Host "Waiting for DMS services to report healthy (requires provisioned schema)..." -ForegroundColor Cyan
        $deadline = (Get-Date).AddMinutes(8)
        do {
            Start-Sleep -Seconds 10
            $healthy = $true
            foreach ($p in @("st-dms", "mt-dms")) {
                $code = (curl -s -k -o /dev/null -w "%{http_code}" "https://localhost/$p/health")
                if ($code -ne "200") { $healthy = $false }
            }
        } while (-not $healthy -and (Get-Date) -lt $deadline)
        if ($healthy) { Write-Host "DMS services healthy." -ForegroundColor Green }
        else { Write-Warning "DMS not healthy. Is the relational schema provisioned? See docs/infrastructure.md." }
    }
    else {
        Write-Host "`nNext steps (manual):" -ForegroundColor Yellow
        Write-Host "  1. Provision the relational schema into edfi_st / edfi_mt / edfi_mt_t2 (dms-schema; see docs/infrastructure.md)."
        Write-Host "  2. Start the DMS services:  ./up.sh st-dms mt-dms   (or re-run this script with -StartDms)."
    }

    Write-Host "`n== Setup complete ==" -ForegroundColor Green
    Write-Host "Public URL: https://$PublicHost"
    Write-Host "Secrets were written to compose/.env (gitignored). Record the generated values and"
    Write-Host "the API key/secret pairs above in your PRIVATE vault / credentials doc -- never commit"
    Write-Host "them to this repo (docs/infrastructure.md is tracked and must stay secret-free)."
}
finally {
    Pop-Location
}
