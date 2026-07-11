# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

param()

Describe "Azure VM lifecycle safety" {
    BeforeAll {
        $script:azureVmRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../azure-vm"))
        $script:downSource = Join-Path $script:azureVmRoot "compose/down.sh"
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-azure-vm-$([Guid]::NewGuid().ToString('N'))"
        $script:composeRoot = Join-Path $script:work "compose"
        $script:binRoot = Join-Path $script:work "bin"
        New-Item -ItemType Directory -Path (Join-Path $script:composeRoot ".bootstrap") -Force | Out-Null
        New-Item -ItemType Directory -Path $script:binRoot -Force | Out-Null
        Copy-Item -LiteralPath $script:downSource -Destination (Join-Path $script:composeRoot "down.sh")
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

    It "clears bootstrap markers for a forced equals-form volume drop" {
        $attempted = Join-Path $script:composeRoot ".bootstrap/bootstrap-attempted"
        $complete = Join-Path $script:composeRoot ".bootstrap/bootstrap-complete"
        New-Item -ItemType File -Path $attempted, $complete -Force | Out-Null

        & bash (Join-Path $script:composeRoot "down.sh") -v=true --force

        $LASTEXITCODE | Should -Be 0
        Get-Content -LiteralPath $script:dockerLog -Raw | Should -Match "compose .* down -v=true"
        Test-Path -LiteralPath $attempted | Should -BeFalse
        Test-Path -LiteralPath $complete | Should -BeFalse
    }

    It "supports an empty argument list under nounset" {
        & bash (Join-Path $script:composeRoot "down.sh")

        $LASTEXITCODE | Should -Be 0
        (Get-Content -LiteralPath $script:dockerLog -Raw).Trim() | Should -Match " down$"
    }
}

Describe "Azure VM deployment invariants" {
    BeforeAll {
        $script:azureVmRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../azure-vm"))
        $script:renewCert = Get-Content (Join-Path $script:azureVmRoot "provision/renew-cert.ps1") -Raw
        $script:bootstrap = Get-Content (Join-Path $script:azureVmRoot "compose/bootstrap/bootstrap.ps1") -Raw
        $script:setupEnv = Get-Content (Join-Path $script:azureVmRoot "provision/setup-env.ps1") -Raw
        $script:compose = Get-Content (Join-Path $script:azureVmRoot "compose/docker-compose.yml") -Raw
        $script:exampleEnv = Get-Content (Join-Path $script:azureVmRoot "compose/.env.example") -Raw
    }

    It "restarts only the gateway during certificate renewal" {
        $script:renewCert | Should -Match 'up -d --no-deps gateway'
    }

    It "centralizes CMS bootstrap markers and exposes a Keycloak-only recovery path" {
        $script:bootstrap | Should -Match 'bootstrap-attempted'
        $script:bootstrap | Should -Match 'bootstrap-complete'
        $script:bootstrap | Should -Match '\[switch\]\$KeycloakOnly'
        $script:bootstrap | Should -Match 'existing CMS state was not modified'
        $script:setupEnv | Should -Not -Match '\[switch\]\$Bootstrap'
    }

    It "uses the real Hybrid claim fragment glob" {
        $script:compose | Should -Match '\*-claimset\.json'
        $script:exampleEnv | Should -Match '\*-claimset\.json'
        $script:exampleEnv | Should -Not -Match 'claim set \*\.json'
    }

    It "does not require the OpenIddict-only identity encryption key" {
        $script:compose | Should -Not -Match 'DMS_CONFIG_IDENTITY_ENCRYPTION_KEY'
        $script:exampleEnv | Should -Not -Match 'DMS_CONFIG_IDENTITY_ENCRYPTION_KEY'
        $script:setupEnv | Should -Match 'Remove-EnvValue -File "\.env" -Key "DMS_CONFIG_IDENTITY_ENCRYPTION_KEY"'
        $script:setupEnv | Should -Not -Match 'Set-EnvValue -File "\.env" -Key "DMS_CONFIG_IDENTITY_ENCRYPTION_KEY"'
    }

    It "guards secret rotation against both independently persistent state volumes" {
        $script:setupEnv | Should -Match 'dms-security-review_dms-sec-postgres'
        $script:setupEnv | Should -Match 'dms-security-review_dms-sec-keycloak'
    }

    It "uses fail-fast Docker installer downloads in both provisioning paths" {
        $windowsSetup = Get-Content (Join-Path $script:azureVmRoot "provision/windows/setup-windows-host.ps1") -Raw
        $portal = Get-Content (Join-Path $script:azureVmRoot "provision/PORTAL.md") -Raw

        $windowsSetup | Should -Match 'set -euo pipefail'
        $windowsSetup | Should -Match 'curl -fsSL https://get\.docker\.com -o /tmp/get-docker\.sh'
        $portal | Should -Not -Match 'curl -fsSL https://get\.docker\.com \| sh'
    }

    It "resolves the VM NSG through its attached NIC" {
        $provisionVm = Get-Content (Join-Path $script:azureVmRoot "provision/provision-vm.ps1") -Raw

        $provisionVm | Should -Match 'networkProfile\.networkInterfaces\[0\]\.id'
        $provisionVm | Should -Match 'networkSecurityGroup\.id'
        $provisionVm | Should -Not -Match '--query", "\[0\]\.name"'
    }

    It "documents the guarded update wrapper instead of raw full-stack recreation" {
        foreach ($relativePath in @("provision/README.md", "provision/MANUAL.md", "provision/windows/README.md")) {
            $document = Get-Content (Join-Path $script:azureVmRoot $relativePath) -Raw
            $document | Should -Match '\./update\.sh'
            $document | Should -Not -Match '(?s)git pull.*docker compose.*pull.*docker compose.*up -d'
        }
    }

    It "documents the resolved Keycloak volume and Keycloak-only recovery" {
        $readme = Get-Content (Join-Path $script:azureVmRoot "provision/README.md") -Raw
        $keycloakCompose = Get-Content (Join-Path $script:azureVmRoot "compose/keycloak.yml") -Raw

        $readme | Should -Match 'dms-security-review_dms-sec-keycloak'
        $readme | Should -Match '-KeycloakOnly'
        $keycloakCompose | Should -Match 'dms-security-review_dms-sec-keycloak'
    }
}
