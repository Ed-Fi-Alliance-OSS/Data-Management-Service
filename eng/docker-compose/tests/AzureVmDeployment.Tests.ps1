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
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-azure-vm-$([Guid]::NewGuid().ToString('N'))"
        $script:composeRoot = Join-Path $script:work "compose"
        $script:binRoot = Join-Path $script:work "bin"
        New-Item -ItemType Directory -Path (Join-Path $script:composeRoot ".bootstrap") -Force | Out-Null
        New-Item -ItemType Directory -Path $script:binRoot -Force | Out-Null
        Copy-Item -LiteralPath $script:downSource -Destination (Join-Path $script:composeRoot "down.sh")
        Copy-Item -LiteralPath $script:resetSource -Destination (Join-Path $script:composeRoot "reset.sh")
        Set-Content -LiteralPath (Join-Path $script:composeRoot ".env") -Value "PUBLIC_HOST=test.example" -NoNewline

        $script:dockerLog = Join-Path $script:work "docker.log"
        $dockerStub = Join-Path $script:binRoot "docker"
        Set-Content -LiteralPath $dockerStub -Value @'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$DOCKER_LOG"
exit 0
'@ -NoNewline
        & chmod +x $dockerStub
        $script:originalPath = $env:PATH
        $env:PATH = "$script:binRoot$([IO.Path]::PathSeparator)$env:PATH"
        $env:DOCKER_LOG = $script:dockerLog
    }

    AfterEach {
        $env:PATH = $script:originalPath
        Remove-Item Env:DOCKER_LOG -ErrorAction SilentlyContinue
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
        New-Item -ItemType File -Path $attempted, $complete -Force | Out-Null

        & bash (Join-Path $script:composeRoot "down.sh") -v=true --force

        $LASTEXITCODE | Should -Be 0
        Get-Content -LiteralPath $script:dockerLog -Raw | Should -Match "compose .* down -v=true"
        Test-Path -LiteralPath $attempted | Should -BeFalse
        Test-Path -LiteralPath $complete | Should -BeFalse
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
        # The volume the recorded Keycloak image reference described is gone with the reset.
        Test-Path -LiteralPath $keycloakRef | Should -BeFalse
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
