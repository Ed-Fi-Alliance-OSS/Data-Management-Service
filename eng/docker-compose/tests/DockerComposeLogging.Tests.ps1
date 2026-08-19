# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Describe "Docker Compose logging defaults (DMS-1407)" {
    BeforeAll {
        $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:dockerComposeRoot = Join-Path $script:repoRoot "eng/docker-compose"
        $script:azureComposeRoot = Join-Path $script:repoRoot "eng/azure-vm/compose"
        $script:composeLogBlockPattern = '(?ms)logging:\s*driver:\s*json-file\s*options:\s*max-size:\s*"\$\{DOCKER_LOG_MAX_SIZE:-50m\}"\s*max-file:\s*"\$\{DOCKER_LOG_MAX_FILE:-5\}"'
    }

    function script:Get-ServiceBlocks {
        param(
            [Parameter(Mandatory)]
            [string]
            $Content
        )

        $servicesMatch = [regex]::Match($Content, '(?ms)^services:\s*\r?\n(?<body>.*?)(?=^[A-Za-z0-9_-]+:\s*$|\z)')
        if (-not $servicesMatch.Success) {
            return @()
        }

        $body = $servicesMatch.Groups["body"].Value
        $serviceMatches = [regex]::Matches($body, '(?ms)^  (?<name>[A-Za-z0-9_-]+):\s*\r?\n(?<block>.*?)(?=^  [A-Za-z0-9_-]+:\s*$|\z)')

        foreach ($match in $serviceMatches) {
            [pscustomobject]@{
                Name = $match.Groups["name"].Value
                Block = $match.Groups["block"].Value
            }
        }
    }

    It "keeps documented operator env files aligned to the Information default" {
        foreach ($fileName in @(".env.example", ".env.template", ".env.template.ds61", ".env.multitenancy")) {
            $content = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot $fileName) -Raw

            $content | Should -Match "(?m)^LOG_LEVEL=Information$" -Because "$fileName is a documented operator path and must not ship DMS at Debug by default"
        }
    }

    It "caps json-file logs in every compose service file that ships a runnable stack" {
        $composeFiles = @(
            "eng/docker-compose/kafka-ui.yml",
            "eng/docker-compose/kafka.yml",
            "eng/docker-compose/keycloak.yml",
            "eng/docker-compose/local-config.yml",
            "eng/docker-compose/local-dms.yml",
            "eng/docker-compose/mssql.yml",
            "eng/docker-compose/postgresql.yml",
            "eng/docker-compose/published-config.yml",
            "eng/docker-compose/published-dms.yml",
            "eng/docker-compose/swagger-ui.yml",
            "eng/azure-vm/compose/docker-compose.yml",
            "eng/azure-vm/compose/keycloak.yml"
        )

        foreach ($relativePath in $composeFiles) {
            $content = Get-Content -LiteralPath (Join-Path $script:repoRoot $relativePath) -Raw
            $services = @(Get-ServiceBlocks -Content $content)

            $services.Count | Should -BeGreaterThan 0 -Because "$relativePath should contain concrete compose services"
            foreach ($service in $services) {
                $inheritsAzureDefaults = $relativePath -eq "eng/azure-vm/compose/docker-compose.yml" -and $service.Block -match '<<:\s*\*app-defaults'
                ($service.Block -match $script:composeLogBlockPattern -or $inheritsAzureDefaults) |
                    Should -BeTrue -Because "$relativePath service '$($service.Name)' must keep the bounded json-file logging defaults"
            }
        }
    }

    It "does not document invalid Docker max-size values as the unbounded escape hatch" {
        $loggingDoc = Get-Content -LiteralPath (Join-Path $script:repoRoot "docs/LOGGING.md") -Raw

        $loggingDoc | Should -Not -Match 'DOCKER_LOG_MAX_SIZE=-1' -Because "Docker rejects -1 for json-file max-size; use a very large accepted cap instead"
        $loggingDoc | Should -Match 'DOCKER_LOG_MAX_SIZE=1000g' -Because "the docs should name an env-only unbounded-equivalent value Docker accepts"
    }

    It "does not claim Docker json-file rotation governs in-container file sinks" {
        $loggingDoc = Get-Content -LiteralPath (Join-Path $script:repoRoot "docs/LOGGING.md") -Raw

        $loggingDoc | Should -Not -Match 'console/file logs on the host are still governed by DOCKER_LOG_MAX_SIZE and DOCKER_LOG_MAX_FILE'
    }
}
