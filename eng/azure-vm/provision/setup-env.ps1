#Requires -Version 7
# SPDX-License-Identifier: Apache-2.0
#
# Sets up the DMS security-review environment ON the VM:
#   1. (optional) clone the repo
#   2. create .env with strong generated secrets + the public host
#   3. obtain a TLS cert (Let's Encrypt if -LetsEncryptEmail given, else self-signed)
#   4. (with -LoadGrandbend) restore the populated template into edfi_st
#   5. start identity + CMS (everything EXCEPT the DMS services), wait for health, then
#      run bootstrap (clients, tenants, data stores)
#   6. (with -StartDms, after the relational schema is provisioned) start the DMS services
#
# Idempotent re-runs: secrets are written only when .env is first created (or with -RotateSecrets);
# the documented second pass (-StartDms, after schema provisioning) preserves every secret and
# skips bootstrap. Regenerating secrets on a re-run would break the existing Postgres volume,
# Keycloak realm/clients, and CMS-encrypted rows, which are all keyed to the first-run values.
# An interrupted bootstrap must be recovered with reset.sh, which removes both CMS and Keycloak
# state. The bootstrap script owns attempted/complete markers and refuses a blind retry because its
# identity/CMS creates are not idempotent.
#
# !! GAP: by default this does NOT provision the relational schema (api-schema-tools), stage
#    .bootstrap/ApiSchema, or seed data (-LoadGrandbend is the explicit single-tenant exception).
#    Because of that it bootstraps but does NOT start the
#    DMS services by default (a DMS booted against an unprovisioned data DB won't pass /health):
#    stage the ApiSchema workspace, provision the schema, then start them with -StartDms (or
#    `./up.sh st-dms mt-dms`) -- both refuse to start the DMS services while
#    compose/.bootstrap/ApiSchema is unstaged, since Docker would silently mount it empty.
#    See README.md "What setup-env.ps1 does NOT do" and ../docs/infrastructure.md
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
    [switch]$RotateSecrets,                                     # regenerate .env secrets even if .env exists (you MUST also reset dependent volumes/registrations)
    [switch]$StartDms,                                          # start the DMS services (only after the relational schema is provisioned)
    [switch]$Force                                              # allow -RotateSecrets against existing volumes (only if you will recreate them)
)
$ErrorActionPreference = "Stop"

# --- secret helpers ---------------------------------------------------------
# All generators draw from the cryptographically secure RandomNumberGenerator, NOT Get-Random
# (System.Random is a predictable, non-cryptographic PRNG). GetInt32(n) yields an unbiased index.
function Get-SecureChar([string]$Set) {
    return $Set[[System.Security.Cryptography.RandomNumberGenerator]::GetInt32($Set.Length)]
}
function New-ComplexSecret {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Generates an in-memory secret string; no system state changes and no -WhatIf surface.')]
    param([int]$Length = 40)
    # Meets CMS/Keycloak complexity: lower, upper, digit, special; 32-128 chars.
    # Special set avoids characters significant in .env / connection strings / URLs AND in
    # form-urlencoded bodies ('+' decodes to a space, '%' starts an escape sequence), so the
    # secrets can be pasted into token requests without percent-encoding.
    $lower = 'abcdefghijkmnopqrstuvwxyz'; $upper = 'ABCDEFGHJKLMNPQRSTUVWXYZ'
    $digit = '23456789'; $special = '!@#^*-_.'
    $all = $lower + $upper + $digit + $special
    $chars = [System.Collections.Generic.List[char]]::new()
    # Guarantee at least one character from each required class.
    $chars.Add((Get-SecureChar $lower)); $chars.Add((Get-SecureChar $upper))
    $chars.Add((Get-SecureChar $digit)); $chars.Add((Get-SecureChar $special))
    while ($chars.Count -lt $Length) { $chars.Add((Get-SecureChar $all)) }
    # Fisher-Yates shuffle (CSPRNG) so the guaranteed-class characters are not always in front.
    for ($i = $chars.Count - 1; $i -gt 0; $i--) {
        $j = [System.Security.Cryptography.RandomNumberGenerator]::GetInt32($i + 1)
        $tmp = $chars[$i]; $chars[$i] = $chars[$j]; $chars[$j] = $tmp
    }
    -join $chars
}
function New-Key32 {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Generates an in-memory key string; no system state changes and no -WhatIf surface.')]
    param()
    # Exactly 32 characters (the CMS database encryption key requires length 32).
    $a = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    -join (1..32 | ForEach-Object { Get-SecureChar $a })
}
function Set-EnvValue {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Rewrites one key in the local .env working file during setup; a -WhatIf surface adds no value in this one-shot deployment script.')]
    param([string]$File, [string]$Key, [string]$Value)
    $found = $false
    $out = foreach ($line in (Get-Content $File)) {
        if ($line -match "^\s*$([regex]::Escape($Key))=") { $found = $true; "$Key=$Value" } else { $line }
    }
    if (-not $found) { $out += "$Key=$Value" }
    Set-Content -Path $File -Value $out
}
function Remove-EnvValue {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Removes one obsolete key from the local .env working file during setup; a -WhatIf surface adds no value in this one-shot deployment script.')]
    param([string]$File, [string]$Key)
    $out = Get-Content $File | Where-Object { $_ -notmatch "^\s*$([regex]::Escape($Key))=" }
    Set-Content -Path $File -Value $out
}

# --- 1. repo ----------------------------------------------------------------
if (-not (Test-Path $RepoDir)) {
    if (-not $RepoUrl) { throw "RepoDir '$RepoDir' not found and no -RepoUrl provided. Clone the repo first (see '$RepoDir/provision/README.md')." }
    # $RepoDir is the eng/azure-vm folder INSIDE the repo, so the clone target is the repo root
    # above it. Cloning straight into $RepoDir would nest this folder at $RepoDir/eng/azure-vm
    # and the compose-dir check below would fail.
    $cloneRoot = ($RepoDir -replace '\\', '/').TrimEnd('/') -replace '/eng/azure-vm$', ''
    Write-Output "Cloning $RepoUrl -> $cloneRoot"
    git clone $RepoUrl $cloneRoot
    if ($LASTEXITCODE -ne 0) { throw "git clone of '$RepoUrl' failed ($LASTEXITCODE)." }
    if (-not (Test-Path (Join-Path $RepoDir "compose"))) {
        # A custom -RepoDir that doesn't follow the .../eng/azure-vm shape was cloned into
        # directly; point $RepoDir at the deployment folder inside the fresh clone.
        $RepoDir = Join-Path $cloneRoot "eng/azure-vm"
    }
}
$composeDir = Join-Path $RepoDir "compose"
if (-not (Test-Path $composeDir)) { throw "compose dir not found at $composeDir" }

# docker group sanity check (cloud-init adds the user; needs a fresh login to take effect).
docker info *> $null
if ($LASTEXITCODE -ne 0) { throw "Cannot talk to Docker. Log out and back in (so the 'docker' group applies), then re-run." }

Push-Location $composeDir
try {
    # --- 2. .env: host (always) + generated secrets (first run / -RotateSecrets only) -------
    $freshEnv = -not (Test-Path ".env")
    if ($freshEnv) { Copy-Item ".env.example" ".env" }

    # Lock .env to owner-only (0600): it holds the DB password, client secrets, and encryption
    # keys. Copy-Item / Set-Content inherit the shell umask (typically 0644 on Linux/WSL), which
    # would leave secrets world-readable to other local users/processes. Run every invocation
    # (idempotent); Set-Content below preserves the mode of the existing file.
    if ($IsLinux -or $IsMacOS) {
        chmod 600 .env
        if ($LASTEXITCODE -ne 0) { throw "chmod 600 .env failed ($LASTEXITCODE); refusing to continue with potentially world-readable secrets." }
    }

    # Host / base URL are deterministic from -PublicHost, so they are safe to (re)write every run.
    Write-Output "Writing .env host values..."
    Set-EnvValue -File ".env" -Key "PUBLIC_BASE_URL" -Value "https://$PublicHost"
    Set-EnvValue -File ".env" -Key "PUBLIC_HOST" -Value $PublicHost
    # Removed from this Keycloak-only stack: it was consumed only by self-contained OpenIddict.
    # Clean legacy .env files too, particularly ones that still carry a CHANGEME placeholder.
    Remove-EnvValue -File ".env" -Key "DMS_CONFIG_IDENTITY_ENCRYPTION_KEY"

    # Secrets are persisted state: the Postgres volume, the Keycloak realm/clients, and the
    # CMS-encrypted rows are all keyed to the FIRST-run values. Regenerating them on a re-run (e.g.
    # the documented '-StartDms' second pass) would break DB auth / token auth / decryption against
    # the existing volumes. Generate only when .env is first created, or on an explicit
    # -RotateSecrets (which implies you will also reset the dependent volumes/registrations).
    # An interrupted first run can create .env (line above) and die before this block, leaving
    # placeholder CHANGEME secrets. $freshEnv would then be false on the retry, so also
    # (re)generate whenever any placeholder remains -- otherwise the retry starts Compose below
    # with the .env.example placeholders (that path never passes through up.sh's CHANGEME guard).
    $hasPlaceholders = [bool](Select-String -Path ".env" -Pattern '=.*CHANGEME' -Quiet)
    if ($hasPlaceholders -and -not $freshEnv) {
        Write-Warning ".env still holds placeholder (CHANGEME) secrets from an incomplete earlier run; regenerating all secrets."
    }
    # Guard -RotateSecrets against EXISTING volumes: the Postgres data, Keycloak realm, and CMS-
    # encrypted rows are keyed to the current secrets, so rotating them and then starting the same
    # volumes (below) breaks DB auth / token auth / decryption. Require a full reset first.
    if ($RotateSecrets -and -not $Force) {
        $existingStateVolumes = [System.Collections.Generic.List[string]]::new()
        foreach ($volumeName in @(
                "dms-security-review_dms-sec-postgres",
                "dms-security-review_dms-sec-keycloak"
            )) {
            docker volume inspect $volumeName *> $null
            if ($LASTEXITCODE -eq 0) { $existingStateVolumes.Add($volumeName) }
        }
        if ($existingStateVolumes.Count -gt 0) {
            throw ("-RotateSecrets would regenerate secrets that the EXISTING volumes (Postgres/" +
                "Keycloak/CMS-encrypted state) are keyed to, breaking auth and decryption when they " +
                "restart. Existing state: $($existingStateVolumes -join ', '). Run ./down.sh -v first " +
                "(drops ALL data, including Keycloak), then re-run -- " +
                "or pass -Force to rotate anyway (only if the volumes will be recreated).")
        }
    }
    if ($freshEnv -or $RotateSecrets -or $hasPlaceholders) {
        Write-Output "Writing generated secrets to .env..."
        Set-EnvValue -File ".env" -Key "POSTGRES_PASSWORD" -Value (New-ComplexSecret)
        Set-EnvValue -File ".env" -Key "KEYCLOAK_ADMIN_PASSWORD" -Value (New-ComplexSecret)
        Set-EnvValue -File ".env" -Key "DMS_CONFIG_IDENTITY_CLIENT_SECRET" -Value (New-ComplexSecret)
        Set-EnvValue -File ".env" -Key "CONFIG_SERVICE_CLIENT_SECRET" -Value (New-ComplexSecret)
        Set-EnvValue -File ".env" -Key "BOOTSTRAP_ADMIN_CLIENT_SECRET" -Value (New-ComplexSecret)
        Set-EnvValue -File ".env" -Key "PGADMIN_DEFAULT_PASSWORD" -Value (New-ComplexSecret)
        Set-EnvValue -File ".env" -Key "DMS_CONFIG_DATABASE_ENCRYPTION_KEY" -Value (New-Key32)
    }
    else {
        Write-Output "Preserving existing secrets in .env (pass -RotateSecrets to regenerate)."
    }

    # --- 3. TLS certificate -------------------------------------------------
    if ($LetsEncryptEmail) {
        Write-Output "Obtaining Let's Encrypt certificate for $PublicHost (port 80 must be reachable)..."
        sudo certbot certonly --standalone --non-interactive --agree-tos -m $LetsEncryptEmail -d $PublicHost
        if ($LASTEXITCODE -ne 0) { throw "certbot failed. Check that DNS resolves to this VM and port 80 is open." }
        $live = "/etc/letsencrypt/live/$PublicHost"
        # install (not cp) sets the destination mode atomically: the private key stays owner-only
        # (a bare cp inherits the shell umask, often leaving the key group/other-readable).
        # Stage BOTH files, then move them into place together -- installing directly could
        # leave a mismatched cert/key pair if the second install fails, and nginx refuses a
        # mismatched pair on its next start.
        sudo install -m 644 -o "$(whoami)" "$live/fullchain.pem" ssl/server.crt.tmp
        if ($LASTEXITCODE -ne 0) { throw "staging fullchain.pem failed ($LASTEXITCODE)." }
        sudo install -m 600 -o "$(whoami)" "$live/privkey.pem"  ssl/server.key.tmp
        if ($LASTEXITCODE -ne 0) { throw "staging privkey.pem failed ($LASTEXITCODE)." }
        Move-Item -Force ssl/server.crt.tmp ssl/server.crt
        Move-Item -Force ssl/server.key.tmp ssl/server.key
    }
    elseif (Test-Path "ssl/server.crt") {
        Write-Output "Reusing existing certificate at ssl/server.crt (delete it to regenerate)."
    }
    else {
        Write-Output "No -LetsEncryptEmail: generating a self-signed certificate."
        ./ssl/generate-certificate.sh $PublicHost
        if ($LASTEXITCODE -ne 0) { throw "generate-certificate.sh failed ($LASTEXITCODE)." }
    }

    # Belt-and-suspenders: never start Compose with placeholder secrets. This script starts
    # Compose directly below (not via up.sh, which has its own guard), so re-assert here after
    # the secret/cert steps and before the first `docker compose up`.
    if (Select-String -Path ".env" -Pattern '=.*CHANGEME' -Quiet) {
        throw ".env still contains CHANGEME placeholder secrets after secret generation. Refusing to start Compose. Re-run with -RotateSecrets, or replace every CHANGEME value manually."
    }

    # --- 4. (optional) Grand Bend sample data -------------------------------
    if ($LoadGrandbend) {
        Write-Output "Loading Grand Bend sample data (populated template)..."
        docker network inspect dms-sec *> $null
        if ($LASTEXITCODE -ne 0) { docker network create dms-sec | Out-Null }
        docker compose -f docker-compose.yml --env-file .env up -d postgres
        if ($LASTEXITCODE -ne 0) { throw "docker compose up postgres failed ($LASTEXITCODE)." }
        $pgDeadline = (Get-Date).AddMinutes(3)
        do {
            Start-Sleep -Seconds 5
            $pg = (docker inspect -f '{{.State.Health.Status}}' dms-sec-postgres 2>$null)
        } while ($pg -ne "healthy" -and (Get-Date) -lt $pgDeadline)
        if ($pg -ne "healthy") { throw "PostgreSQL did not report healthy within 3 minutes; cannot restore the template. Check './logs.sh postgres'." }
        bash ./seed/grandbend.sh
        if ($LASTEXITCODE -ne 0) { throw "seed/grandbend.sh failed ($LASTEXITCODE); see its output above." }
        Write-Output "Grand Bend template loaded into edfi_st (schema + data). Single-tenant is ready to start; the multi-tenant DBs still need schema + seed."
    }

    # --- 5. start identity + CMS (NOT the DMS services yet) -----------------
    # The DMS eagerly loads its data stores from the CMS on boot and fail-fasts if the Keycloak
    # realm / clients / data stores don't yet exist (../docs/infrastructure.md issue 3). So bring up
    # everything EXCEPT st-dms/mt-dms, bootstrap, and only then start the DMS services.
    docker network inspect dms-sec *> $null
    if ($LASTEXITCODE -ne 0) { docker network create dms-sec | Out-Null }

    Write-Output "Starting infrastructure (postgres, keycloak, config services, gateway)..."
    # --no-deps so the gateway (which depends_on the DMS services) does not pull them up early;
    # the gateway resolves upstreams at request time, so it starts fine without the DMS backends.
    docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d --no-deps `
        postgres keycloak st-config mt-config pgadmin gateway
    if ($LASTEXITCODE -ne 0) { throw "docker compose up (infrastructure) failed ($LASTEXITCODE)." }

    Write-Output "Waiting for Keycloak + config services to report healthy..."
    $deadline = (Get-Date).AddMinutes(8)
    do {
        Start-Sleep -Seconds 10
        $healthy = $true
        foreach ($p in @("st-config", "mt-config")) {
            $code = (curl -s -k --connect-timeout 5 --max-time 10 -o /dev/null -w "%{http_code}" "https://localhost/$p/health")
            if ($code -ne "200") { $healthy = $false }
        }
        $kcCode = (curl -s -k --connect-timeout 5 --max-time 10 -o /dev/null -w "%{http_code}" "https://localhost/auth/realms/master")
        if ($kcCode -ne "200") { $healthy = $false }
    } while (-not $healthy -and (Get-Date) -lt $deadline)
    if ($healthy) { Write-Output "Identity + config services healthy." }
    else { throw "Identity/config services did not report healthy within 8 minutes. Check './logs.sh', then re-run (re-runs preserve secrets; bootstrap has not been attempted yet, so the retry runs it cleanly)." }

    # Bootstrap state is tracked with TWO markers, not .env existence: bootstrap.ps1's
    # data-store/vendor/application creates are NOT idempotent, so a bootstrap that dies partway
    # leaves partial CMS objects that a blind retry would DUPLICATE. So distinguish three states:
    #   - "complete" marker present            -> already bootstrapped; skip.
    #   - "attempted" present, "complete" not   -> a prior bootstrap died mid-way; the config DB
    #                                              may hold partial objects. Refuse to auto-retry
    #                                              (would duplicate); tell the user to reset first.
    #   - neither                               -> safe to run (fresh, or a prior run died BEFORE
    #                                              bootstrap was reached, e.g. cert/health failure).
    # bootstrap.ps1 writes "attempted" immediately before its first mutation and "complete" only
    # after every create succeeds. reset.sh (which empties the config DB) removes both.
    $bootstrapAttempted = Join-Path $composeDir ".bootstrap/bootstrap-attempted"
    $bootstrapComplete  = Join-Path $composeDir ".bootstrap/bootstrap-complete"
    if (-not $SkipBootstrap) {
        if (Test-Path $bootstrapComplete) {
            Write-Output "Skipping bootstrap: already completed (reset.sh clears this after it empties the config DB)."
        }
        elseif (Test-Path $bootstrapAttempted) {
            throw ("A previous bootstrap started but did not complete; the CMS config DB may hold " +
                "partial vendors/applications/data stores that a retry would DUPLICATE. Run " +
                "./reset.sh (clears partial CMS and Keycloak state), then re-run.")
        }
        else {
            Write-Output "Running bootstrap (over loopback)..."
            & "$composeDir/bootstrap/bootstrap.ps1" -BaseUrl "https://localhost" -Insecure
        }
    }

    # --- 6. relational schema (MANUAL) + start the DMS services -------------
    # The DMS data DBs use the relational backend and are provisioned OUT OF BAND with the
    # api-schema-tools tool (../docs/infrastructure.md "Provisioning method", step 3). This script does
    # NOT provision schema, so by default it does not start the DMS services -- a DMS booted
    # against an unprovisioned data DB will not pass /health. Provision the schema, then re-run
    # with -StartDms (or `./up.sh st-dms mt-dms`).
    if ($StartDms) {
        # The DMS services bind-mount ./.bootstrap/ApiSchema at /app/ApiSchema and read the API
        # schema exclusively from there (AppSettings__UseApiSchemaPath=true). Docker auto-creates
        # the directory empty if it was never staged, and a DMS booted against an empty schema
        # directory fails startup/health even with the databases provisioned -- so refuse early.
        $apiSchemaDir = Join-Path $composeDir ".bootstrap/ApiSchema"
        $apiSchemaStaged = (Test-Path $apiSchemaDir) -and
            @(Get-ChildItem $apiSchemaDir -Recurse -File -Filter "*.json" -ErrorAction SilentlyContinue).Count -gt 0
        if (-not $apiSchemaStaged) {
            throw ("ApiSchema workspace not staged: '$apiSchemaDir' is missing or has no *.json files. " +
                "Stage it first (eng/docker-compose/prepare-dms-schema.ps1 writes eng/docker-compose/.bootstrap/ApiSchema; " +
                "copy that folder here), then re-run with -StartDms. See ../docs/infrastructure.md 'Provisioning method'.")
        }
        Write-Output "Starting DMS services..."
        docker compose -f docker-compose.yml -f keycloak.yml --env-file .env up -d st-dms mt-dms
        if ($LASTEXITCODE -ne 0) { throw "docker compose up st-dms/mt-dms failed ($LASTEXITCODE)." }
        Write-Output "Waiting for DMS services to report healthy (requires provisioned schema)..."
        $deadline = (Get-Date).AddMinutes(8)
        do {
            Start-Sleep -Seconds 10
            $healthy = $true
            foreach ($p in @("st-dms", "mt-dms")) {
                $code = (curl -s -k --connect-timeout 5 --max-time 10 -o /dev/null -w "%{http_code}" "https://localhost/$p/health")
                if ($code -ne "200") { $healthy = $false }
            }
        } while (-not $healthy -and (Get-Date) -lt $deadline)
        if ($healthy) { Write-Output "DMS services healthy." }
        else { throw "DMS services did not report healthy within 8 minutes. Is the relational schema provisioned? See ../docs/infrastructure.md, then re-run with -StartDms." }
    }
    else {
        Write-Output "`nNext steps (manual):"
        Write-Output "  1. Stage the ApiSchema workspace into compose/.bootstrap/ApiSchema (eng/docker-compose/prepare-dms-schema.ps1"
        Write-Output "     writes eng/docker-compose/.bootstrap/ApiSchema -- copy that folder here; the DMS services mount it read-only)."
        Write-Output "  2. Provision the relational schema into edfi_st / edfi_mt / edfi_mt_t2 (api-schema-tools, against the same staged"
        Write-Output "     workspace; see ../docs/infrastructure.md)."
        Write-Output "  3. Start the DMS services:  ./up.sh st-dms mt-dms   (or re-run this script with -StartDms)."
    }

    Write-Output "`n== Setup complete =="
    Write-Output "Public URL: https://$PublicHost"
    Write-Output "Secrets were written to compose/.env (gitignored). Record the generated values and"
    Write-Output "the API key/secret pairs above in your PRIVATE vault / credentials doc -- never commit"
    Write-Output "them to this repo (../docs/infrastructure.md is tracked and must stay secret-free)."
}
finally {
    Pop-Location
}
