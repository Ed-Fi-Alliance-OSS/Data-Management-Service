# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Credential guardrails for the Docker analysis workflows. Pull requests and manual runs on any
# branch control these workflow files, so the Docker Hub image publishing token must never reach
# them; they log in with the separate read-only Scout identity instead. Following
# DmsPullRequestMssqlWorkflow.Tests.ps1, no YAML parser is assumed: assertions run over the raw
# workflow text.

Describe 'Docker analysis workflow credentials: <Workflow>' -ForEach @(
    @{ Workflow = 'on-pullrequest-dockerfile.yml' }
    @{ Workflow = 'analyze-docker-images.yml' }
) {
    BeforeAll {
        $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        $script:content = Get-Content (Join-Path $repoRoot ".github/workflows/$Workflow") -Raw
    }

    It 'never references the Docker Hub publishing token' {
        $script:content | Should -Not -Match 'DOCKER_HUB_TOKEN'
    }

    It 'declares no workflow-wide env' {
        $script:content | Should -Not -Match '(?m)^env:'
    }

    It 'reads no secret other than the Scout token' {
        $secrets = [regex]::Matches($script:content, 'secrets\.([A-Za-z0-9_]+)') |
            ForEach-Object { $_.Groups[1].Value } |
            Sort-Object -Unique
        $secrets | Should -Be @('DOCKER_SCOUT_TOKEN')
    }

    It 'logs in to Docker Hub with the Scout identity' {
        $login = [regex]::Match(
            $script:content,
            '(?ms)^      - name: Log in to Docker Hub\r?\n.*?(?=^      - name:)'
        ).Value
        $login | Should -Match 'username: \$\{\{ vars\.DOCKER_SCOUT_USERNAME \}\}'
        $login | Should -Match 'password: \$\{\{ secrets\.DOCKER_SCOUT_TOKEN \}\}'
    }
}
