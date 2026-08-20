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

    function script:Get-ServiceBlock {
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

    function script:Get-ComposeLoggingViolation {
        param(
            [Parameter(Mandatory)]
            [string]
            $RelativePath,

            [Parameter(Mandatory)]
            [string]
            $Content
        )

        $violations = [System.Collections.Generic.List[string]]::new()
        $services = @(Get-ServiceBlock -Content $Content)

        if ($services.Count -eq 0) {
            $violations.Add("$RelativePath should contain concrete compose services")
        }

        if ($RelativePath -eq "eng/azure-vm/compose/docker-compose.yml") {
            $anchorMatch = [regex]::Match($Content, '(?ms)^x-app-defaults:\s*&app-defaults\s*\r?\n(?<block>.*?)(?=^[A-Za-z0-9_-]+:\s*$|\z)')
            if (-not $anchorMatch.Success -or $anchorMatch.Groups["block"].Value -notmatch $script:composeLogBlockPattern) {
                $violations.Add("$RelativePath anchor 'x-app-defaults' must keep the bounded json-file logging defaults")
            }
        }

        foreach ($service in $services) {
            $inheritsAzureDefaults = $RelativePath -eq "eng/azure-vm/compose/docker-compose.yml" -and $service.Block -match '<<:\s*\*app-defaults'
            if ($service.Block -notmatch $script:composeLogBlockPattern -and -not $inheritsAzureDefaults) {
                $violations.Add("$RelativePath service '$($service.Name)' must keep the bounded json-file logging defaults")
            }
        }

        return $violations
    }

    function script:Get-ComposeServiceFileUnderTest {
        $excludedOverrideFiles = @(
            "eng/docker-compose/bootstrap-dms.yml",
            "eng/docker-compose/local-dms-diagnostics.yml",
            "eng/docker-compose/postgresql-tmpfs.yml"
        )

        return @(
            Get-ChildItem -LiteralPath $script:dockerComposeRoot -Filter "*.yml" |
                ForEach-Object { [System.IO.Path]::GetRelativePath($script:repoRoot, $_.FullName).Replace("\", "/") } |
                Where-Object { $excludedOverrideFiles -notcontains $_ }
            Get-ChildItem -LiteralPath $script:azureComposeRoot -Filter "*.yml" |
                ForEach-Object { [System.IO.Path]::GetRelativePath($script:repoRoot, $_.FullName).Replace("\", "/") }
        ) | Sort-Object
    }

    function script:Get-TrackedComposeEnvFile {
        return @(
            Get-ChildItem -LiteralPath $script:dockerComposeRoot -File -Force -Filter ".env*" |
                ForEach-Object { [System.IO.Path]::GetRelativePath($script:repoRoot, $_.FullName).Replace("\", "/") }
            Get-ChildItem -LiteralPath $script:azureComposeRoot -File -Force -Filter ".env*" |
                ForEach-Object { [System.IO.Path]::GetRelativePath($script:repoRoot, $_.FullName).Replace("\", "/") }
        ) | Sort-Object
    }

    It "keeps tracked stack env files aligned to the Information default" {
        $envFiles = Get-TrackedComposeEnvFile

        foreach ($relativePath in $envFiles) {
            $content = Get-Content -LiteralPath (Join-Path $script:repoRoot $relativePath) -Raw

            if ($content -match "(?m)^LOG_LEVEL=") {
                $content | Should -Match "(?m)^LOG_LEVEL=Information$" -Because "$relativePath must not ship DMS at Debug by default"
            }
            if ($content -match "(?m)^DMS_CONFIG_LOG_LEVEL=") {
                $content | Should -Match "(?m)^DMS_CONFIG_LOG_LEVEL=Information$" -Because "$relativePath must keep CMS aligned to the shipped Information default"
            }
        }
    }

    It "caps json-file logs in every compose service file that ships a runnable stack" {
        $composeFiles = Get-ComposeServiceFileUnderTest

        foreach ($relativePath in $composeFiles) {
            $content = Get-Content -LiteralPath (Join-Path $script:repoRoot $relativePath) -Raw
            @(Get-ComposeLoggingViolation -RelativePath $relativePath -Content $content) | Should -BeNullOrEmpty
        }
    }

    It "fails inherited Azure services when the shared app-defaults anchor loses the logging cap" {
        $relativePath = "eng/azure-vm/compose/docker-compose.yml"
        $content = Get-Content -LiteralPath (Join-Path $script:repoRoot $relativePath) -Raw
        $loggingBlock = @"
  logging:
    driver: json-file
    options:
      max-size: "`${DOCKER_LOG_MAX_SIZE:-50m}"
      max-file: "`${DOCKER_LOG_MAX_FILE:-5}"
"@
        $mutatedContent = $content.Replace($loggingBlock, "")

        $mutatedContent | Should -Not -Be $content -Because "the fixture mutation must remove the shared logging cap"

        @(Get-ComposeLoggingViolation -RelativePath $relativePath -Content $mutatedContent) |
            Should -Contain "$relativePath anchor 'x-app-defaults' must keep the bounded json-file logging defaults"
    }

    It "does not document invalid Docker max-size values as the unbounded escape hatch" {
        $loggingDoc = Get-Content -LiteralPath (Join-Path $script:repoRoot "docs/LOGGING.md") -Raw

        $loggingDoc | Should -Not -Match 'DOCKER_LOG_MAX_SIZE=-1' -Because "Docker rejects -1 for json-file max-size; use a very large accepted cap instead"
        $loggingDoc | Should -Match 'DOCKER_LOG_MAX_SIZE=1000g' -Because "the docs should name an env-only unbounded-equivalent value Docker accepts"
    }

    It "documents Docker rotation as stdout-only and separate from in-container file sinks" {
        $loggingDoc = Get-Content -LiteralPath (Join-Path $script:repoRoot "docs/LOGGING.md") -Raw

        $loggingDoc | Should -Not -Match 'console/file logs on the host are still governed by DOCKER_LOG_MAX_SIZE and DOCKER_LOG_MAX_FILE'
        $loggingDoc | Should -Match 'Docker\s+settings\s+do\s+not\s+cap\s+the\s+in-container\s+Serilog\s+file\s+sink'
    }
}
