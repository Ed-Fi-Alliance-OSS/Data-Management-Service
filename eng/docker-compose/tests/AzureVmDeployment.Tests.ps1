# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

param()

Describe "Azure VM lifecycle safety" {
    BeforeAll {
        $script:azureVmRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../azure-vm"))
        $script:downSource = Join-Path $script:azureVmRoot "compose/down.sh"
        $script:resetSource = Join-Path $script:azureVmRoot "compose/reset.sh"
        $script:upSource = Join-Path $script:azureVmRoot "compose/up.sh"
        $script:updateSource = Join-Path $script:azureVmRoot "compose/update.sh"
        $script:recordKeycloakImageSource = Join-Path $script:azureVmRoot "compose/record-keycloak-image.sh"
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-azure-vm-$([Guid]::NewGuid().ToString('N'))"
        $script:composeRoot = Join-Path $script:work "compose"
        $script:binRoot = Join-Path $script:work "bin"
        New-Item -ItemType Directory -Path (Join-Path $script:composeRoot ".bootstrap") -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $script:composeRoot ".bootstrap/ApiSchema") -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $script:composeRoot "ssl") -Force | Out-Null
        New-Item -ItemType Directory -Path $script:binRoot -Force | Out-Null
        Copy-Item -LiteralPath $script:downSource -Destination (Join-Path $script:composeRoot "down.sh")
        Copy-Item -LiteralPath $script:resetSource -Destination (Join-Path $script:composeRoot "reset.sh")
        Copy-Item -LiteralPath $script:upSource -Destination (Join-Path $script:composeRoot "up.sh")
        Copy-Item -LiteralPath $script:updateSource -Destination (Join-Path $script:composeRoot "update.sh")
        Copy-Item -LiteralPath $script:recordKeycloakImageSource -Destination (Join-Path $script:composeRoot "record-keycloak-image.sh")
        Set-Content -LiteralPath (Join-Path $script:composeRoot ".env") -Value "PUBLIC_HOST=test.example" -NoNewline
        Set-Content -LiteralPath (Join-Path $script:composeRoot ".bootstrap/ApiSchema/core.json") -Value "{}" -NoNewline
        Set-Content -LiteralPath (Join-Path $script:composeRoot "ssl/server.crt") -Value "test" -NoNewline

        & chmod +x (Join-Path $script:composeRoot "down.sh")
        & chmod +x (Join-Path $script:composeRoot "reset.sh")
        & chmod +x (Join-Path $script:composeRoot "up.sh")
        & chmod +x (Join-Path $script:composeRoot "update.sh")
        & chmod +x (Join-Path $script:composeRoot "record-keycloak-image.sh")

        $script:dockerLog = Join-Path $script:work "docker.log"
        $script:curlLog = Join-Path $script:work "curl.log"
        $script:dockerState = Join-Path $script:work "docker-state"
        New-Item -ItemType Directory -Path $script:dockerState -Force | Out-Null
        $dockerStub = Join-Path $script:binRoot "docker"
        Set-Content -LiteralPath $dockerStub -Value @'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$DOCKER_LOG"

state="${DOCKER_STATE_DIR:?}"
configured="${DOCKER_CONFIGURED_KEYCLOAK:-quay.io/keycloak/keycloak:26.7}"
deployed="${DOCKER_DEPLOYED_KEYCLOAK:-quay.io/keycloak/keycloak:26.7}"

if [ "${1:-}" = "compose" ] && [[ "$*" == *" config --images"* ]]; then
  printf '%s\n' "$configured"
  exit 0
fi
if [ "${1:-}" = "inspect" ] && [[ "$*" == *"dms-sec-keycloak"* ]]; then
  [ -f "$state/keycloak-present" ] || exit 1
  if [[ "$*" == *".Config.Image"* ]]; then printf '%s\n' "$deployed"; fi
  exit 0
fi
if [ "${1:-}" = "volume" ] && [ "${2:-}" = "inspect" ]; then
  [ -f "$state/keycloak-volume" ]
  exit $?
fi
if [ "${1:-}" = "compose" ] && [[ "$*" == *" up "* ]] && [[ "$*" == *" keycloak "* ]]; then
  : > "$state/keycloak-present"
  : > "$state/keycloak-volume"
fi
if [ "${1:-}" = "compose" ] && [[ "$*" == *" down"* ]]; then
  rm -f "$state/keycloak-present"
  if [[ "$*" == *" -v"* ]] || [[ "$*" == *"--volumes"* ]] || [[ "$*" == *"--volume"* ]]; then
    rm -f "$state/keycloak-volume"
  fi
fi
exit 0
'@ -NoNewline
        & chmod +x $dockerStub

        $curlStub = Join-Path $script:binRoot "curl"
        Set-Content -LiteralPath $curlStub -Value @'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$CURL_LOG"
if [ "${CURL_READY:-1}" != "1" ]; then
  if [[ "$*" == *"mt-config/health"* ]]; then printf '\n000'; else printf '000'; fi
  exit 7
fi
case "$*" in
  *mt-config/health*)
    printf '%s\n400' '{"message":"The '\''Tenant'\'' header is required when multi-tenancy is enabled"}'
    ;;
  *st-config/health* | *auth/realms/master*) printf '200' ;;
  *) printf '200' ;;
esac
'@ -NoNewline
        & chmod +x $curlStub

        $script:originalPath = $env:PATH
        $env:PATH = "$script:binRoot$([IO.Path]::PathSeparator)$env:PATH"
        $env:DOCKER_LOG = $script:dockerLog
        $env:CURL_LOG = $script:curlLog
        $env:DOCKER_STATE_DIR = $script:dockerState
        $env:DOCKER_CONFIGURED_KEYCLOAK = "quay.io/keycloak/keycloak:26.7"
        $env:DOCKER_DEPLOYED_KEYCLOAK = "quay.io/keycloak/keycloak:26.7"
        $env:DMS_STARTUP_TIMEOUT_SECONDS = "2"
        $env:DMS_STARTUP_POLL_SECONDS = "0"
    }

    AfterEach {
        $env:PATH = $script:originalPath
        foreach ($name in @(
                "DOCKER_LOG",
                "CURL_LOG",
                "DOCKER_STATE_DIR",
                "DOCKER_CONFIGURED_KEYCLOAK",
                "DOCKER_DEPLOYED_KEYCLOAK",
                "CURL_READY",
                "DMS_STARTUP_TIMEOUT_SECONDS",
                "DMS_STARTUP_POLL_SECONDS",
                "SKIP_GIT"
            )) {
            Remove-Item "Env:$name" -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "refuses the equals-form volumes flag without confirmation" {
        $output = & bash (Join-Path $script:composeRoot "down.sh") --volumes=true 2>&1

        $LASTEXITCODE | Should -Be 1
        $output | Out-String | Should -Match "refusing to drop all volumes"
        Test-Path -LiteralPath $script:dockerLog | Should -BeFalse
    }

    It "refuses every Docker-truthy equals-form volumes flag without confirmation" {
        $output = & bash (Join-Path $script:composeRoot "down.sh") -v=1 2>&1

        $LASTEXITCODE | Should -Be 1
        $output | Out-String | Should -Match "refusing to drop all volumes"
        Test-Path -LiteralPath $script:dockerLog | Should -BeFalse
    }

    It "refuses a bundled short-flag cluster containing -v without confirmation" {
        $output = & bash (Join-Path $script:composeRoot "down.sh") -vt 0 2>&1

        $LASTEXITCODE | Should -Be 1
        $output | Out-String | Should -Match "refusing to drop all volumes"
        Test-Path -LiteralPath $script:dockerLog | Should -BeFalse
    }

    It "refuses the deprecated --volume alias without confirmation" {
        $output = & bash (Join-Path $script:composeRoot "down.sh") --volume 2>&1

        $LASTEXITCODE | Should -Be 1
        $output | Out-String | Should -Match "refusing to drop all volumes"
        Test-Path -LiteralPath $script:dockerLog | Should -BeFalse
    }

    It "honors Compose last-value semantics for repeated volume flags" {
        $attempted = Join-Path $script:composeRoot ".bootstrap/bootstrap-attempted"
        $complete = Join-Path $script:composeRoot ".bootstrap/bootstrap-complete"
        New-Item -ItemType File -Path $attempted, $complete -Force | Out-Null

        # Compose resolves `-v --volumes=false` to FALSE (last value wins), so the wrapper must
        # neither prompt nor clear the markers -- the volumes are preserved.
        & bash (Join-Path $script:composeRoot "down.sh") -v --volumes=false

        $LASTEXITCODE | Should -Be 0
        Get-Content -LiteralPath $script:dockerLog -Raw | Should -Match "compose .* down -v --volumes=false"
        Test-Path -LiteralPath $attempted | Should -BeTrue
        Test-Path -LiteralPath $complete | Should -BeTrue
        Test-Path -LiteralPath (Join-Path $script:composeRoot ".bootstrap/reset-pending") | Should -BeFalse
    }

    It "clears bootstrap markers for a forced bundled volume drop" {
        $attempted = Join-Path $script:composeRoot ".bootstrap/bootstrap-attempted"
        $complete = Join-Path $script:composeRoot ".bootstrap/bootstrap-complete"
        New-Item -ItemType File -Path $attempted, $complete -Force | Out-Null

        & bash (Join-Path $script:composeRoot "down.sh") -vt 0 --force

        $LASTEXITCODE | Should -Be 0
        Get-Content -LiteralPath $script:dockerLog -Raw | Should -Match "compose .* down -vt 0"
        Test-Path -LiteralPath $attempted | Should -BeFalse
        Test-Path -LiteralPath $complete | Should -BeFalse
    }

    It "clears bootstrap markers for a forced equals-form volume drop" {
        $attempted = Join-Path $script:composeRoot ".bootstrap/bootstrap-attempted"
        $complete = Join-Path $script:composeRoot ".bootstrap/bootstrap-complete"
        $keycloakRef = Join-Path $script:composeRoot ".bootstrap/keycloak-image"
        New-Item -ItemType File -Path $attempted, $complete, $keycloakRef -Force | Out-Null

        & bash (Join-Path $script:composeRoot "down.sh") -v=true --force

        $LASTEXITCODE | Should -Be 0
        Get-Content -LiteralPath $script:dockerLog -Raw | Should -Match "compose .* down -v=true"
        Test-Path -LiteralPath $attempted | Should -BeFalse
        Test-Path -LiteralPath $complete | Should -BeFalse
        Test-Path -LiteralPath $keycloakRef | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $script:composeRoot ".bootstrap/reset-pending") | Should -BeFalse
    }

    It "clears bootstrap markers before a failing destructive volume attempt" {
        $attempted = Join-Path $script:composeRoot ".bootstrap/bootstrap-attempted"
        $complete = Join-Path $script:composeRoot ".bootstrap/bootstrap-complete"
        New-Item -ItemType File -Path $attempted, $complete -Force | Out-Null
        $dockerStub = Join-Path $script:binRoot "docker"
        Set-Content -LiteralPath $dockerStub -Value @'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$DOCKER_LOG"
exit 17
'@ -NoNewline
        & chmod +x $dockerStub

        & bash (Join-Path $script:composeRoot "down.sh") -v --force 2>&1 | Out-Null

        $LASTEXITCODE | Should -Be 17
        Test-Path -LiteralPath $attempted | Should -BeFalse
        Test-Path -LiteralPath $complete | Should -BeFalse
        # The sentinel must survive the failed attempt: the volumes may still hold live state, and
        # bootstrap.ps1 refuses to run (and duplicate identity/CMS objects) while it exists.
        Test-Path -LiteralPath (Join-Path $script:composeRoot ".bootstrap/reset-pending") | Should -BeTrue
    }

    It "supports an empty argument list under nounset" {
        & bash (Join-Path $script:composeRoot "down.sh")

        $LASTEXITCODE | Should -Be 0
        (Get-Content -LiteralPath $script:dockerLog -Raw).Trim() | Should -Match " down$"
    }

    It "starts infrastructure before DMS and records the deployed Keycloak image" {
        $output = & bash (Join-Path $script:composeRoot "up.sh") 2>&1

        $LASTEXITCODE | Should -Be 0
        $output | Out-String | Should -Match "Keycloak \+ config services ready"
        $stop = Select-String -LiteralPath $script:dockerLog -Pattern "stop st-dms mt-dms"
        $infra = Select-String -LiteralPath $script:dockerLog -Pattern "up -d --no-deps postgres keycloak st-config mt-config pgadmin swagger-ui gateway"
        $dms = Select-String -LiteralPath $script:dockerLog -Pattern "up -d st-dms mt-dms"
        $stop.LineNumber | Should -BeLessThan $infra.LineNumber
        $infra.LineNumber | Should -BeLessThan $dms.LineNumber

        $keycloakRef = Join-Path $script:composeRoot ".bootstrap/keycloak-image"
        Get-Content -LiteralPath $keycloakRef -Raw | Should -Be "quay.io/keycloak/keycloak:26.7`n"
        @(Get-ChildItem -LiteralPath (Join-Path $script:composeRoot ".bootstrap") -Filter "keycloak-image.tmp.*").Count | Should -Be 0
    }

    It "does not start DMS when infrastructure readiness times out" {
        $env:CURL_READY = "0"
        $env:DMS_STARTUP_TIMEOUT_SECONDS = "0"

        $output = & bash (Join-Path $script:composeRoot "up.sh") 2>&1

        $LASTEXITCODE | Should -Be 1
        $output | Out-String | Should -Match "were not ready within 0s"
        Get-Content -LiteralPath $script:dockerLog -Raw | Should -Not -Match "up -d st-dms mt-dms"
    }

    It "supports the first update after a plain down with the same Keycloak pin" {
        & bash (Join-Path $script:composeRoot "up.sh") 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
        & bash (Join-Path $script:composeRoot "down.sh") 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0

        $keycloakRef = Join-Path $script:composeRoot ".bootstrap/keycloak-image"
        Get-Content -LiteralPath $keycloakRef -Raw | Should -Be "quay.io/keycloak/keycloak:26.7`n"
        $env:SKIP_GIT = "1"

        $output = & bash (Join-Path $script:composeRoot "update.sh") 2>&1

        $LASTEXITCODE | Should -Be 0
        $output | Out-String | Should -Match "Update complete"
        Get-Content -LiteralPath $script:dockerLog -Raw | Should -Match "compose .* pull"
    }

    It "restarts the gateway and Swagger UI after an update so pulled template changes apply" {
        & bash (Join-Path $script:composeRoot "up.sh") 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
        $env:SKIP_GIT = "1"
        Set-Content -LiteralPath $script:dockerLog -Value "" -NoNewline

        $output = & bash (Join-Path $script:composeRoot "update.sh") 2>&1

        $LASTEXITCODE | Should -Be 0 -Because ($output | Out-String)
        # Both render bind-mounted files only at container start, and Compose does not recreate a
        # container whose definition is unchanged, so only a restart picks up a pulled change.
        $dms = Select-String -LiteralPath $script:dockerLog -Pattern "up -d st-dms mt-dms"
        $restart = Select-String -LiteralPath $script:dockerLog -Pattern "compose .* restart gateway swagger-ui"
        $restart | Should -Not -BeNullOrEmpty
        $dms.LineNumber | Should -BeLessThan $restart.LineNumber
    }

    It "rejects a changed Keycloak pin after a plain down before pulling images" {
        & bash (Join-Path $script:composeRoot "up.sh") 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
        & bash (Join-Path $script:composeRoot "down.sh") 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0

        $env:DOCKER_CONFIGURED_KEYCLOAK = "quay.io/keycloak/keycloak:27.0"
        $env:SKIP_GIT = "1"
        Set-Content -LiteralPath $script:dockerLog -Value "" -NoNewline

        $output = & bash (Join-Path $script:composeRoot "update.sh") 2>&1

        $LASTEXITCODE | Should -Be 1
        $output | Out-String | Should -Match "changes the Keycloak image"
        Get-Content -LiteralPath $script:dockerLog -Raw | Should -Not -Match "compose .* pull"
    }

    It "drops Keycloak with application state during reset" {
        $attempted = Join-Path $script:composeRoot ".bootstrap/bootstrap-attempted"
        $complete = Join-Path $script:composeRoot ".bootstrap/bootstrap-complete"
        $keycloakRef = Join-Path $script:composeRoot ".bootstrap/keycloak-image"
        New-Item -ItemType File -Path $attempted, $complete, $keycloakRef -Force | Out-Null

        & bash (Join-Path $script:composeRoot "reset.sh") --force

        $LASTEXITCODE | Should -Be 0
        $calls = Get-Content -LiteralPath $script:dockerLog -Raw
        $calls | Should -Match 'compose -f docker-compose\.yml -f keycloak\.yml --env-file \.env down -v'
        $calls | Should -Match 'compose -f docker-compose\.yml -f keycloak\.yml --env-file \.env up -d --no-deps'
        Test-Path -LiteralPath $attempted | Should -BeFalse
        Test-Path -LiteralPath $complete | Should -BeFalse
        Test-Path -LiteralPath (Join-Path $script:composeRoot ".bootstrap/reset-pending") | Should -BeFalse
        # The old volume/reference is gone, and the freshly recreated Keycloak container records
        # the actual image associated with its new H2 volume.
        Get-Content -LiteralPath $keycloakRef -Raw | Should -Be "quay.io/keycloak/keycloak:26.7`n"
    }

    It "retains the reset sentinel when the destructive reset fails" {
        $dockerStub = Join-Path $script:binRoot "docker"
        Set-Content -LiteralPath $dockerStub -Value @'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$DOCKER_LOG"
exit 17
'@ -NoNewline
        & chmod +x $dockerStub

        & bash (Join-Path $script:composeRoot "reset.sh") --force 2>&1 | Out-Null

        $LASTEXITCODE | Should -Be 17
        Test-Path -LiteralPath (Join-Path $script:composeRoot ".bootstrap/reset-pending") | Should -BeTrue
    }

    It "refuses a non-interactive reset without an explicit force flag" {
        $output = & bash (Join-Path $script:composeRoot "reset.sh") 2>&1

        $LASTEXITCODE | Should -Be 1
        $output | Out-String | Should -Match "refusing to drop"
        Test-Path -LiteralPath $script:dockerLog | Should -BeFalse
    }
}

Describe "Azure VM populated template seed" {
    BeforeAll {
        $script:grandbendSource = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../azure-vm/compose/seed/grandbend.sh"))
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-azure-vm-seed-$([Guid]::NewGuid().ToString('N'))"
        $script:composeRoot = Join-Path $script:work "compose"
        $script:binRoot = Join-Path $script:work "bin"
        New-Item -ItemType Directory -Path (Join-Path $script:composeRoot "seed") -Force | Out-Null
        New-Item -ItemType Directory -Path $script:binRoot -Force | Out-Null
        Copy-Item -LiteralPath $script:grandbendSource -Destination (Join-Path $script:composeRoot "seed/grandbend.sh")
        Set-Content -LiteralPath (Join-Path $script:composeRoot ".env") -Value "DATABASE_TEMPLATE_PACKAGE_VERSION=1.0.0" -NoNewline

        $script:dockerLog = Join-Path $script:work "docker.log"
        $script:dockerStdinLog = Join-Path $script:work "docker-stdin.log"
        $dockerStub = Join-Path $script:binRoot "docker"
        Set-Content -LiteralPath $dockerStub -Value @'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$DOCKER_LOG"
if [ "${1:-}" = "exec" ] && [ "${2:-}" = "-i" ]; then
  cat >> "$DOCKER_STDIN_LOG"
  [ "${ROLE_SETUP_FAILS:-0}" = "1" ] && exit 1
fi
exit 0
'@ -NoNewline
        & chmod +x $dockerStub

        $curlStub = Join-Path $script:binRoot "curl"
        Set-Content -LiteralPath $curlStub -Value @'
#!/usr/bin/env bash
out=""
while [ $# -gt 0 ]; do
  if [ "$1" = "-o" ]; then out="$2"; shift; fi
  shift
done
[ -n "$out" ] || exit 1
python3 - "$out" <<'PY'
import sys, zipfile
with zipfile.ZipFile(sys.argv[1], "w") as z:
    z.writestr("template.sql", 'CREATE SCHEMA edfi;\nCREATE TABLE dms."EffectiveSchema" ();\n')
PY
'@ -NoNewline
        & chmod +x $curlStub

        $script:originalPath = $env:PATH
        $env:PATH = "$script:binRoot$([IO.Path]::PathSeparator)$env:PATH"
        $env:DOCKER_LOG = $script:dockerLog
        $env:DOCKER_STDIN_LOG = $script:dockerStdinLog
    }

    AfterEach {
        $env:PATH = $script:originalPath
        foreach ($name in @("DOCKER_LOG", "DOCKER_STDIN_LOG", "ROLE_SETUP_FAILS")) {
            Remove-Item "Env:$name" -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "creates the locked-down enqueue owner role before restoring the template" {
        $output = & bash (Join-Path $script:composeRoot "seed/grandbend.sh") edfi_st edfi_mt 2>&1

        $LASTEXITCODE | Should -Be 0 -Because ($output | Out-String)
        $roleSetup = Select-String -LiteralPath $script:dockerLog -Pattern "exec -i dms-sec-postgres psql .*-d postgres"
        $restores = @(Select-String -LiteralPath $script:dockerLog -Pattern "-f /tmp/grandbend.sql")
        $roleSetup | Should -Not -BeNullOrEmpty
        $restores.Count | Should -Be 2
        $roleSetup.LineNumber | Should -BeLessThan $restores[0].LineNumber
        $roleSql = Get-Content -LiteralPath $script:dockerStdinLog -Raw
        $roleSql | Should -Match 'CREATE ROLE "edfi_dms_enqueue_owner" WITH NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;'
        $roleSql | Should -Match "exists but is not locked down"
    }

    It "reseeds the data store source identity inside each restore transaction" {
        $output = & bash (Join-Path $script:composeRoot "seed/grandbend.sh") edfi_st edfi_mt 2>&1

        $LASTEXITCODE | Should -Be 0 -Because ($output | Out-String)
        $log = Get-Content -LiteralPath $script:dockerLog -Raw
        $restores = [regex]::Matches($log, '(?s)psql -v ON_ERROR_STOP=1 --single-transaction -U postgres -d (edfi_st|edfi_mt) -f /tmp/grandbend\.sql -c (.*?)END\s*\$\$;')
        $restores.Count | Should -Be 2
        foreach ($restore in $restores) {
            $reseed = $restore.Groups[2].Value
            $reseed | Should -Match 'UPDATE "dms"\."DataStoreIdentity"'
            $reseed | Should -Match 'SET "SourceIdentity" = gen_random_uuid\(\)'
            $reseed | Should -Match 'GET DIAGNOSTICS _updated_count = ROW_COUNT'
        }
    }

    It "does not restore when the enqueue owner role cannot be ensured" {
        $env:ROLE_SETUP_FAILS = "1"

        $output = & bash (Join-Path $script:composeRoot "seed/grandbend.sh") edfi_st 2>&1

        $LASTEXITCODE | Should -Be 1
        $output | Out-String | Should -Match "edfi_dms_enqueue_owner"
        Get-Content -LiteralPath $script:dockerLog -Raw | Should -Not -Match "-f /tmp/grandbend.sql"
    }
}

Describe "Azure VM Data Standard selection" {
    BeforeAll {
        $script:composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../azure-vm/compose"))
    }

    It "passes DMS_CONFIG_DATA_STANDARD_VERSION to both config services, defaulting to 5.2" {
        $compose = Get-Content -LiteralPath (Join-Path $script:composeRoot "docker-compose.yml") -Raw

        $sharedEnvironment = [regex]::Match($compose, '(?ms)^x-cms-common-env: &cms-common-env\s*\n(.*?)(?=^\S)').Groups[1].Value
        $sharedEnvironment | Should -Match 'ClaimsOptions__DataStandardVersion:\s*"\$\{DMS_CONFIG_DATA_STANDARD_VERSION:-5\.2\}"'
        foreach ($service in @("st-config", "mt-config")) {
            $serviceBlock = [regex]::Match($compose, "(?ms)^  ${service}:\s*\n(.*?)(?=^  \S)").Groups[1].Value
            $serviceBlock | Should -Match '<<: \*cms-common-env' -Because "$service must inherit the shared claims settings"
        }
    }

    It "declares the Data Standard 5.2 default in .env.example" {
        Get-Content -LiteralPath (Join-Path $script:composeRoot ".env.example") -Raw |
            Should -Match '(?m)^DMS_CONFIG_DATA_STANDARD_VERSION=5\.2\r?$'
    }
}

Describe "Azure VM review variant applications" {
    BeforeAll {
        Import-Module ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../azure-vm/compose/bootstrap/review-variants.psm1"))) -Force
    }

    AfterAll {
        Remove-Module review-variants -ErrorAction SilentlyContinue
    }

    BeforeEach {
        $script:existingApplications = @()
        $script:existingVendors = @()
        $script:dataStores = @(
            [pscustomobject]@{ id = 3; name = "Some Other Store" },
            [pscustomobject]@{ id = 7; name = "Single-Tenant Data Store" },
            [pscustomobject]@{ id = 8; name = "MT Data Store (tenant1 2025)" },
            [pscustomobject]@{ id = 9; name = "MT Data Store (tenant2 2025)" }
        )
        Mock Get-CmsToken -ModuleName review-variants { "token" }
        # Invoke-RestMethod emits a JSON array as ONE object; the mocks reproduce that.
        Mock Invoke-RestMethod -ModuleName review-variants -ParameterFilter { $Uri -like "*v3/dataStores*" } {
            Write-Output -NoEnumerate -InputObject @($script:dataStores)
        }
        Mock Invoke-RestMethod -ModuleName review-variants -ParameterFilter { $Uri -like "*v3/applications*" } {
            Write-Output -NoEnumerate -InputObject @($script:existingApplications)
        }
        Mock Invoke-RestMethod -ModuleName review-variants -ParameterFilter { $Uri -like "*v3/vendors*" } {
            Write-Output -NoEnumerate -InputObject @($script:existingVendors)
        }
        Mock Add-Vendor -ModuleName review-variants { 100 }
        Mock Add-Application -ModuleName review-variants { @{ Id = 1; Key = "key"; Secret = "secret" } }
    }

    It "defines the four requested claim-set, EdOrg and namespace variants" {
        $variants = @(Get-ReviewVariant)

        @($variants | ForEach-Object { "$($_.ClaimSet)|$($_.EducationOrganizationIds -join ',')|$($_.NamespacePrefixes)" }) | Should -Be @(
            "SISVendor|255901|uri://ed-fi.org/",
            "SISVendor|255901107|uri://ed-fi.org/",
            "AssessmentVendor|255901|uri://one.example.com",
            "EdFiSandbox|255901|uri://ed-fi.org/"
        )
    }

    It "creates every variant in all three deployments, bound to each deployment's data store" {
        $created = @(Add-ReviewVariantSet -Deployment (Get-ReviewDeployment -BaseUrl "https://host/") -AdminClientId "admin" -AdminClientSecret "secret")

        $created.Count | Should -Be 12
        foreach ($expected in @(@{ Tenant = ""; DataStoreId = 7 }, @{ Tenant = "tenant1"; DataStoreId = 8 }, @{ Tenant = "tenant2"; DataStoreId = 9 })) {
            Should -Invoke Add-Application -ModuleName review-variants -Times 4 -Exactly -ParameterFilter {
                $Tenant -eq $expected.Tenant -and $DataStoreIds.Count -eq 1 -and $DataStoreIds[0] -eq $expected.DataStoreId
            }
        }
        Should -Invoke Add-Application -ModuleName review-variants -Times 1 -Exactly -ParameterFilter {
            $Tenant -eq "tenant2" -and $ClaimSetName -eq "SISVendor" -and $EducationOrganizationIds -contains 255901107
        }
        Should -Invoke Add-Vendor -ModuleName review-variants -Times 3 -Exactly -ParameterFilter { $NamespacePrefixes -eq "uri://one.example.com" }
    }

    It "keeps every application name within the 50-character API client name limit" {
        $created = @(Add-ReviewVariantSet -Deployment (Get-ReviewDeployment -BaseUrl "https://host") -AdminClientId "admin" -AdminClientSecret "secret")

        foreach ($name in $created.Application) {
            $name.Length | Should -BeLessOrEqual 50 -Because $name
        }
    }

    It "reuses an existing vendor, because vendor creation is create-only" {
        $script:existingVendors = @([pscustomobject]@{ id = 55; company = "Security Review Vendor (ST SISVendor School)" })

        Add-ReviewVariantSet -Deployment (Get-ReviewDeployment -BaseUrl "https://host") -AdminClientId "admin" -AdminClientSecret "secret" | Out-Null

        Should -Invoke Add-Vendor -ModuleName review-variants -Times 11 -Exactly
        Should -Invoke Add-Application -ModuleName review-variants -Times 1 -Exactly -ParameterFilter { $VendorId -eq 55 }
    }

    It "skips a variant whose application already exists" {
        $script:existingApplications = @([pscustomobject]@{ id = 4; applicationName = "Security Review ST EdFiSandbox District" })

        $created = @(Add-ReviewVariantSet -Deployment (Get-ReviewDeployment -BaseUrl "https://host") -AdminClientId "admin" -AdminClientSecret "secret" 3>$null)

        $created.Count | Should -Be 11
        Should -Invoke Add-Application -ModuleName review-variants -Times 0 -Exactly -ParameterFilter { $ApplicationName -eq "Security Review ST EdFiSandbox District" }
    }

    It "fails before creating anything when a deployment's data store is missing" {
        $script:dataStores = @([pscustomobject]@{ id = 7; name = "Single-Tenant Data Store" })

        { Add-ReviewVariantSet -Deployment (Get-ReviewDeployment -BaseUrl "https://host") -AdminClientId "admin" -AdminClientSecret "secret" } |
            Should -Throw "*MT Data Store (tenant1 2025)*"
        Should -Invoke Add-Application -ModuleName review-variants -Times 0 -Exactly
    }
}

Describe "Azure VM educator-prep load" {
    BeforeAll {
        Import-Module ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../azure-vm/compose/seed/educator-prep.psm1"))) -Force
    }

    AfterAll {
        Remove-Module educator-prep -ErrorAction SilentlyContinue
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-azure-vm-edprep-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:work -Force | Out-Null
    }

    AfterEach {
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "selects exactly the educator-prep files the populated template excludes" {
        foreach ($name in @(
                "Candidate.xml", "Path.xml", "PerformanceEvaluation.xml", "ProfessionalDevelopment.xml", "RecruitmentAndStaffing.xml",
                "EducationOrganization-EdPrep.xml", "Survey-EdPrep.xml", "AssessmentMetadata-EdPrep.xml", "Student.xml", "StudentAssessmentSample.xml")) {
            Set-Content -LiteralPath (Join-Path $script:work $name) -Value "<x/>"
        }

        @(Get-EducatorPrepLoadFile -SampleDataDirectory $script:work) | Should -Be @(
            "Candidate.xml", "EducationOrganization-EdPrep.xml", "Path.xml", "PerformanceEvaluation.xml",
            "ProfessionalDevelopment.xml", "RecruitmentAndStaffing.xml", "Survey-EdPrep.xml"
        )
    }

    It "re-posts only kept files that reference students defined in the educator-prep files" {
        Set-Content -LiteralPath (Join-Path $script:work "Candidate.xml") -Value "<Student><StudentUniqueId>C1</StudentUniqueId></Student>"
        Set-Content -LiteralPath (Join-Path $script:work "Student.xml") -Value "<Student><StudentUniqueId>S1</StudentUniqueId></Student>"
        Set-Content -LiteralPath (Join-Path $script:work "StudentAssessmentSample.xml") -Value "<StudentReference><StudentIdentity><StudentUniqueId>C1</StudentUniqueId></StudentIdentity></StudentReference>"
        Set-Content -LiteralPath (Join-Path $script:work "StudentAssessment-ACT.xml") -Value "<StudentReference><StudentIdentity><StudentUniqueId>S1</StudentUniqueId></StudentIdentity></StudentReference>"

        @(Get-EducatorPrepRepostFile -SampleDataDirectory $script:work) | Should -Be @("StudentAssessmentSample.xml")
    }

    It "removes the loader application by the id it created, never from a listing" {
        Mock Invoke-RestMethod -ModuleName educator-prep -ParameterFilter { $Method -ne "Delete" } {
            [pscustomobject]@{ id = 42; applicationName = "EdPrep Loader (tenant1)" }
        }
        Mock Invoke-RestMethod -ModuleName educator-prep -ParameterFilter { $Method -eq "Delete" } { }

        Remove-ReviewLoaderApplication -CmsUrl "https://host/mt-config/" -Headers @{} -ApplicationId 42 -ExpectedName "EdPrep Loader (tenant1)" -Confirm:$false

        Should -Invoke Invoke-RestMethod -ModuleName educator-prep -Times 1 -Exactly -ParameterFilter {
            $Method -eq "Delete" -and $Uri -eq "https://host/mt-config/v3/applications/42"
        }
        Should -Invoke Invoke-RestMethod -ModuleName educator-prep -Times 0 -Exactly -ParameterFilter { $Uri -match 'v3/applications\?' }
    }

    It "refuses to delete when the id no longer names the loader application" {
        Mock Invoke-RestMethod -ModuleName educator-prep -ParameterFilter { $Method -ne "Delete" } {
            [pscustomobject]@{ id = 42; applicationName = "Security Review (multi-tenant/tenant1)" }
        }
        Mock Invoke-RestMethod -ModuleName educator-prep -ParameterFilter { $Method -eq "Delete" } { }

        { Remove-ReviewLoaderApplication -CmsUrl "https://host/mt-config/" -Headers @{} -ApplicationId 42 -ExpectedName "EdPrep Loader (tenant1)" -Confirm:$false } |
            Should -Throw "*refusing to delete*"
        Should -Invoke Invoke-RestMethod -ModuleName educator-prep -Times 0 -Exactly -ParameterFilter { $Method -eq "Delete" }
    }

    It "skips certificate checks in every module instance its CMS calls resolve to under -Insecure" {
        # Template-Management force-reimports Dms-Management, so the instance educator-prep calls can be
        # one that Get-Module no longer lists; only resolving the commands themselves reaches it.
        Import-Module ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../azure-vm/compose/bootstrap/review-variants.psm1"))) -Force
        $educatorPrep = Get-Module educator-prep

        Disable-ReviewCertificateCheck -Module $educatorPrep

        $resolved = & $educatorPrep {
            foreach ($name in @("Get-CmsToken", "Add-Vendor", "Add-Application", "Get-BulkLoadClient")) {
                $module = (Get-Command $name).Module
                & $module { [bool]$PSDefaultParameterValues["Invoke-RestMethod:SkipCertificateCheck"] -and [bool]$PSDefaultParameterValues["Invoke-WebRequest:SkipCertificateCheck"] }
            }
            [bool]$PSDefaultParameterValues["Invoke-RestMethod:SkipCertificateCheck"]
        }
        $resolved | Should -Not -Contain $false
        Remove-Module review-variants -ErrorAction SilentlyContinue
    }

    It "resolves the pinned BulkLoadClient from the package it downloads" {
        Mock Get-BulkLoadClient -ModuleName educator-prep { ".packages/edfi.suite3.bulkloadclient.console.1.2.3" }
        $clientDirectory = Join-Path $script:work ".packages/edfi.suite3.bulkloadclient.console.1.2.3/tools/net10.0/any"
        New-Item -ItemType Directory -Path $clientDirectory -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $clientDirectory "EdFi.BulkLoadClient.Console.dll") -Value "stub"

        Get-ReviewBulkLoadClientDirectory -WorkDirectory $script:work | Should -Be (Get-Item -LiteralPath $clientDirectory).FullName
    }

    It "removes the loader application even when the bulk load fails" {
        Mock Get-CmsToken -ModuleName educator-prep { "token" }
        Mock Invoke-RestMethod -ModuleName educator-prep -ParameterFilter { $Uri -like "*v3/dataStores*" } {
            Write-Output -NoEnumerate -InputObject @([pscustomobject]@{ id = 8; name = "MT Data Store (tenant1 2025)" })
        }
        Mock Invoke-RestMethod -ModuleName educator-prep -ParameterFilter { $Uri -like "*v3/vendors*" } { Write-Output -NoEnumerate -InputObject @() }
        Mock Add-Vendor -ModuleName educator-prep { 100 }
        Mock Add-Application -ModuleName educator-prep { @{ Id = 42; Key = "key"; Secret = "secret" } }
        Mock Invoke-BulkLoadClientContainer -ModuleName educator-prep { 1 }
        Mock Remove-ReviewLoaderApplication -ModuleName educator-prep { }
        Set-Content -LiteralPath (Join-Path $script:work "Candidate.xml") -Value "<x/>"

        $deployment = @{ Label = "multi-tenant/tenant1"; Code = "T1"; CmsUrl = "https://host/mt-config/"; Tenant = "tenant1"; DataStoreName = "MT Data Store (tenant1 2025)"; DmsUrl = "http://mt-dms:8080/mt-dms/tenant1/2025" }
        $result = Invoke-EducatorPrepLoad -Deployment $deployment -DataDirectory $script:work -LogDirectory $script:work `
            -AdminClientId "admin" -AdminClientSecret "secret" -BulkLoadClientDirectory $script:work

        $result.ExitCode | Should -Be 1
        Should -Invoke Remove-ReviewLoaderApplication -ModuleName educator-prep -Times 1 -Exactly -ParameterFilter { $ApplicationId -eq 42 }
    }
}

Describe "Azure VM entry scripts" {
    BeforeAll {
        $script:seedRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../azure-vm/compose/seed"))
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-azure-vm-entry-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:work -Force | Out-Null
    }

    AfterEach {
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "load-educator-prep.ps1 resolves every helper it calls after its imports" {
        # Template-Management force-reimports Package-Management and Dms-Management as nested modules,
        # which can drop copies imported earlier; the script must still resolve every helper.
        $output = & pwsh -NoProfile -File (Join-Path $script:seedRoot "load-educator-prep.ps1") -SampleDataDirectory $script:work 2>&1

        $LASTEXITCODE | Should -Not -Be 0
        $output | Out-String | Should -Match "No educator-prep files found"
    }
}

Describe "Azure VM Swagger UI" {
    BeforeAll {
        $script:azureVmRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../azure-vm"))
        $script:initializer = Join-Path $script:azureVmRoot "compose/swagger-ui/swagger-initializer.js"
        # Loads the initializer into a bare `window`, exposes its pure helpers, and prints the JSON
        # result of the expression in $args[1]. The Pester DMS lane runs on ubuntu-latest, which ships Node.js.
        $script:runHelper = {
            param([string]$Expression)
            $program = "const fs = require('fs'); const window = {}; eval(fs.readFileSync(process.argv[1], 'utf8')); " +
                "const h = window.EdFiReviewSwagger; console.log(JSON.stringify($Expression));"
            $output = & node -e $program $script:initializer 2>&1
            $LASTEXITCODE | Should -Be 0 -Because ($output | Out-String)
            return ($output | Out-String | ConvertFrom-Json)
        }
    }

    It "serves Swagger UI behind the gateway at /swagger/" {
        $compose = Get-Content -LiteralPath (Join-Path $script:azureVmRoot "compose/docker-compose.yml") -Raw
        $service = [regex]::Match($compose, "(?ms)^  swagger-ui:\s*\n(.*?)(?=^  \S|^\S)").Groups[1].Value
        $service | Should -Match '<<: \*app-defaults' -Because "the service needs the shared logging cap and network"
        $service | Should -Match 'image: nginx:1\.30-alpine'
        $service | Should -Match '\.\./\.\./docker-compose/custom-swagger-ui:[^\s]*:ro'
        $service | Should -Match '\./swagger-ui:[^\s]*:ro'

        $gateway = Get-Content -LiteralPath (Join-Path $script:azureVmRoot "compose/nginx/default.conf.template") -Raw
        $gateway | Should -Match '(?ms)location /swagger/ \{.*?set \$u_swagger swagger-ui;.*?proxy_pass http://\$u_swagger:80'
        $gateway | Should -Match 'location = /swagger \{\s*return 301 /swagger/;'
        $gateway | Should -Match '<a href="/swagger/">'
    }

    It "starts Swagger UI with the infrastructure in every start path" {
        foreach ($file in @("compose/up.sh", "compose/reset.sh", "provision/setup-env.ps1", "provision/MANUAL.md")) {
            Get-Content -LiteralPath (Join-Path $script:azureVmRoot $file) -Raw |
                Should -Match 'postgres keycloak st-config mt-config pgadmin swagger-ui gateway' -Because $file
        }
    }

    It "lists every DMS and Configuration Service definition, without Discovery, as same-origin paths" {
        $stList = '[{"name":"Resources","endpointUri":"https://host.example/st-dms/metadata/specifications/resources-spec.json"},{"name":"Discovery","endpointUri":"https://host.example/st-dms/metadata/specifications/discovery-spec.json"},{"name":"Change-Queries","endpointUri":"https://host.example/st-dms/metadata/changequeries/v1/swagger.json"}]'
        $mtList = '[{"name":"Resources","endpointUri":"https://host.example/mt-dms/t1/2025/metadata/specifications/resources-spec.json"}]'

        $definitions = & $script:runHelper "h.buildDefinitions($stList, $mtList, ['t1', 't2'], '2025')"

        @($definitions | ForEach-Object { "$($_.name)|$($_.url)" }) | Should -Be @(
            "Single-tenant DMS: Resources|/st-dms/metadata/specifications/resources-spec.json",
            "Single-tenant DMS: Change-Queries|/st-dms/metadata/changequeries/v1/swagger.json",
            "Multi-tenant DMS: Resources|/mt-dms/t1/2025/metadata/specifications/resources-spec.json",
            "Single-tenant Configuration Service|/st-config/openapi/v1.json",
            "Multi-tenant Configuration Service (t1)|/mt-config/openapi/v1.json?tenant=t1",
            "Multi-tenant Configuration Service (t2)|/mt-config/openapi/v1.json?tenant=t2"
        )
    }

    It "falls back to the default DMS definitions when a stack's specification list is unavailable" {
        $definitions = & $script:runHelper "h.buildDefinitions([], null, ['t1', 't2'], '2025')"

        @($definitions | Select-Object -First 4 | ForEach-Object { "$($_.name)|$($_.url)" }) | Should -Be @(
            "Single-tenant DMS: Resources|/st-dms/metadata/specifications/resources-spec.json",
            "Single-tenant DMS: Descriptors|/st-dms/metadata/specifications/descriptors-spec.json",
            "Multi-tenant DMS: Resources|/mt-dms/t1/2025/metadata/specifications/resources-spec.json",
            "Multi-tenant DMS: Descriptors|/mt-dms/t1/2025/metadata/specifications/descriptors-spec.json"
        )
    }

    It "groups a lone untagged operation under its path in both Swagger UIs (shared plugin)" {
        $plugin = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../custom-swagger-ui/edfi-single-operation-group.js"))
        $program = "const fs = require('fs'); const window = {}; eval(fs.readFileSync(process.argv[1], 'utf8')); " +
            "const wrap = window.EdFiSingleOperationGroup().statePlugins.spec.wrapActions.updateSpec; let seen; " +
            "wrap(s => { seen = s; })(JSON.stringify({ paths: { '/availableChangeVersions': { get: { description: 'Versions' } } } })); const lone = JSON.parse(seen); " +
            "wrap(s => { seen = s; })({ paths: { '/a': { get: { tags: ['a'] } } } }); " +
            "console.log(JSON.stringify({ tags: lone.paths['/availableChangeVersions'].get.tags, groups: lone.tags, tagged: seen.paths['/a'].get.tags }));"
        $output = & node -e $program $plugin 2>&1
        $LASTEXITCODE | Should -Be 0 -Because ($output | Out-String)
        $result = $output | Out-String | ConvertFrom-Json

        @($result.tags) | Should -Be @("availableChangeVersions")
        $result.groups[0].name | Should -Be "availableChangeVersions"
        $result.groups[0].description | Should -Be "Versions"
        @($result.tagged) | Should -Be @("a")
        foreach ($index in @("../custom-swagger-ui/index.html", "../../azure-vm/compose/swagger-ui/index.html")) {
            Get-Content -LiteralPath ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $index))) -Raw |
                Should -Match '<script src="edfi-single-operation-group\.js"></script>' -Because $index
        }
    }

    It "adds the missing OAuth2 client-credentials scheme to the Configuration Service specs only" {
        $patched = & $script:runHelper "h.withConfigurationServiceSecurity({openapi:'3.1.1', paths:{}}, '/mt-config/openapi/v1.json?tenant=t1', 'https://host.example')"
        $scheme = $patched.components.securitySchemes.oauth2_client_credentials
        $scheme.flows.clientCredentials.tokenUrl | Should -Be "https://host.example/mt-config/connect/token"
        @($scheme.flows.clientCredentials.scopes.PSObject.Properties.Name) | Should -Be @("edfi_admin_api/full_access", "edfi_admin_api/readonly_access")
        $patched.security[0].PSObject.Properties.Name | Should -Be "oauth2_client_credentials"

        $dms = & $script:runHelper "h.withConfigurationServiceSecurity({openapi:'3.0.0', paths:{}}, '/st-dms/metadata/specifications/resources-spec.json', 'https://host.example')"
        $dms.PSObject.Properties.Name | Should -Not -Contain "components"
    }

    It "sends the Tenant header only to the multi-tenant Configuration Service, for the selected tenant" {
        $results = & $script:runHelper "[h.tenantForRequest('https://host.example/mt-config/openapi/v1.json?tenant=t2', null), h.tenantForRequest('https://host.example/mt-config/v3/vendors', '/mt-config/openapi/v1.json?tenant=t1'), h.tenantForRequest('https://host.example/mt-config/connect/token', '/mt-config/openapi/v1.json?tenant=t2'), h.tenantForRequest('https://host.example/st-config/v3/vendors', '/st-config/openapi/v1.json'), h.tenantForRequest('https://host.example/mt-dms/t1/2025/data/ed-fi/schools', '/mt-config/openapi/v1.json?tenant=t1')]"

        @($results) | Should -Be @("t2", "t1", "t2", $null, $null)
    }
}

Describe "Azure VM ODS parity check" {
    BeforeAll {
        $script:paritySource = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../azure-vm/compose/seed/check-ods-parity.py"))
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-azure-vm-parity-$([Guid]::NewGuid().ToString('N'))"
        $script:binRoot = Join-Path $script:work "bin"
        New-Item -ItemType Directory -Path $script:binRoot -Force | Out-Null
        $script:template = Join-Path $script:work "template.nupkg"
        @'
import sys, zipfile
sql = (
    "COPY edfi.school (schoolid) FROM stdin;\n1\n2\n\\.\n"
    "COPY edfi.student (studentusi) FROM stdin;\n1\n2\n3\n\\.\n"
    "COPY edfi.schoolyeartype (schoolyear) FROM stdin;\n2025\n2050\n\\.\n"
    "COPY edfi.descriptor (descriptorid) FROM stdin;\n1\n\\.\n"
)
with zipfile.ZipFile(sys.argv[1], "w") as z:
    z.writestr("EdFi.Ods.Populated.Template.sql", sql)
'@ | & python3 - $script:template
        $dockerStub = Join-Path $script:binRoot "docker"
        Set-Content -LiteralPath $dockerStub -Value @'
#!/usr/bin/env bash
printf 'school|2\nstudent|%s\nschoolyeartype|1\n__descriptors__|1\n' "${DMS_STUDENTS:-3}"
'@ -NoNewline
        & chmod +x $dockerStub
        $script:originalPath = $env:PATH
        $env:PATH = "$script:binRoot$([IO.Path]::PathSeparator)$env:PATH"
    }

    AfterEach {
        $env:PATH = $script:originalPath
        Remove-Item Env:DMS_STUDENTS -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "passes when only allowed tables differ" {
        $output = & python3 $script:paritySource --template $script:template --database edfi_st --allow-diff schoolyeartype 2>&1

        $LASTEXITCODE | Should -Be 0 -Because ($output | Out-String)
        $output | Out-String | Should -Match "schoolyeartype .*allowed"
    }

    It "fails when a table that must match differs" {
        $env:DMS_STUDENTS = "2"

        $output = & python3 $script:paritySource --template $script:template --database edfi_st --allow-diff schoolyeartype 2>&1

        $LASTEXITCODE | Should -Be 1
        $output | Out-String | Should -Match "student .*3.*2"
    }
}
