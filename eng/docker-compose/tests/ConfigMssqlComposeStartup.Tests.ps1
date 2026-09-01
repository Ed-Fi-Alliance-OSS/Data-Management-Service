# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

BeforeAll {
    $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
    $script:dockerComposeRoot = Join-Path $script:repoRoot "eng/docker-compose"
    Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
}

Describe "Config MSSQL Compose startup hardening" {
    It "opts the config MSSQL E2E env files into the same resource shape as the hardened MSSQL action" {
        foreach ($envFileName in @(".env.config.mssql.e2e", ".env.config.mssql.multitenant.e2e")) {
            $envValues = ReadValuesFromEnvFile (Join-Path $script:dockerComposeRoot $envFileName)

            $envValues["MSSQL_MEMORY_LIMIT_MB"] | Should -Be "4096" -Because "$envFileName should match .github/actions/start-mssql-test-container/action.yml"
            $envValues["MSSQL_AGENT_ENABLED"] | Should -Be "true" -Because "$envFileName should match .github/actions/start-mssql-test-container/action.yml"
            $envValues["MSSQL_CONTAINER_MEMORY"] | Should -Be "10g" -Because "$envFileName should match .github/actions/start-mssql-test-container/action.yml"
            $envValues["MSSQL_TMPFS_SIZE"] | Should -Be "4g" -Because "$envFileName should match .github/actions/start-mssql-test-container/action.yml"
            $envValues["MSSQL_USE_TMPFS"] | Should -Be "true" -Because "$envFileName should use tmpfs-backed SQL Server storage in E2E"
        }
    }

    It "keeps the MSSQL tmpfs override aligned to the hardened MSSQL action" {
        $composeContent = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "mssql-tmpfs.yml") -Raw

        $composeContent | Should -Match 'mem_limit:\s*\$\{MSSQL_CONTAINER_MEMORY:-10g\}'
        $composeContent | Should -Match 'memswap_limit:\s*\$\{MSSQL_CONTAINER_MEMORY:-10g\}'
        $composeContent | Should -Match 'MSSQL_AGENT_ENABLED:\s*\$\{MSSQL_AGENT_ENABLED:-true\}'
        $composeContent | Should -Match '/var/opt/mssql:rw,size=\$\{MSSQL_TMPFS_SIZE:-4g\},mode=1777'
        $composeContent | Should -Match 'volumes:\s*!override\s*\[\]'
    }

    It "adds the MSSQL tmpfs override only for MSSQL starts that explicitly request it" {
        $scriptContent = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "start-local-config.ps1") -Raw

        $scriptContent | Should -Match 'Get-ComposeResolvedEnvValue\s+-EnvironmentValues\s+\$envValues\s+-Name\s+"MSSQL_USE_TMPFS"\s+-DefaultValue\s+"false"'
        $scriptContent | Should -Match '\$mssqlTmpfsComposeFile\s*=\s*"mssql-tmpfs\.yml"'
        $scriptContent | Should -Match 'if\s*\(\$useMssqlTmpfs\s+-and\s+\$datastore\s+-eq\s+"mssql"\)\s*\{\s*\$files\s*\+=\s*@\("-f",\s*\$mssqlTmpfsComposeFile\)\s*\}'
    }

    It "starts SQL Server and waits for sqlcmd readiness before starting the config services" {
        $scriptContent = Get-Content -LiteralPath (Join-Path $script:dockerComposeRoot "start-local-config.ps1") -Raw

        $dbStartIndex = $scriptContent.IndexOf('docker compose $files --env-file $EnvironmentFile -p cs-local up $upArgs db')
        $waitIndex = $scriptContent.IndexOf('Wait-MssqlReady -ContainerName "dms-mssql" -Password $mssqlSaPassword')
        $serviceSelectionIndex = $scriptContent.IndexOf('$configServices = if ($datastore -eq "mssql") { @("keycloak", "config") } else { @() }')
        $configStartIndex = $scriptContent.IndexOf('docker compose $files --env-file $EnvironmentFile -p cs-local up $upArgs $configServices')

        $dbStartIndex | Should -BeGreaterOrEqual 0
        $waitIndex | Should -BeGreaterThan $dbStartIndex
        $serviceSelectionIndex | Should -BeGreaterThan $waitIndex
        $configStartIndex | Should -BeGreaterThan $serviceSelectionIndex
    }
}
