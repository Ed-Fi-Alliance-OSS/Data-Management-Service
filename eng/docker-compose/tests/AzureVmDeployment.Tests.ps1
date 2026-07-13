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
        $infra = Select-String -LiteralPath $script:dockerLog -Pattern "up -d --no-deps postgres keycloak st-config mt-config pgadmin gateway"
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
