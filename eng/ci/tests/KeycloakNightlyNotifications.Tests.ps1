# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Describe 'Keycloak nightly Slack notifications' {
    BeforeAll {
        $workflow = Get-Content (Join-Path $PSScriptRoot '../../../.github/workflows/nightly-keycloak-e2e.yml') -Raw
        $script:notification = [regex]::Match($workflow, '(?ms)^  notify-results:.*\z').Value
    }

    It 'uses single-line success and failure summaries without rich message blocks' {
        $payloads = @([regex]::Matches($script:notification, '(?ms)          payload: \|\r?\n(.*?)          webhook:') | ForEach-Object {
            $_.Groups[1].Value | ConvertFrom-Json
        })
        $payloads.Count | Should -Be 2
        $payloads[0].text | Should -Be ':heavy_check_mark: DMS CI nightly Keycloak DMS E2E passed, all 4 shards verified'
        $payloads[1].text | Should -Be ':x: DMS CI nightly Keycloak DMS E2E failed (result: ${{ needs.keycloak-e2e.result }})'
        foreach ($payload in $payloads) {
            @($payload.PSObject.Properties.Name) | Should -Be @('text')
            $payload.text | Should -Not -Match '[\r\n]'
        }
    }

    It 'retains aggregate success and failure reporting and suppresses manual notifications' {
        $script:notification | Should -Match '(?m)^    needs: keycloak-e2e$'
        $script:notification | Should -Match '(?m)^    if: always\(\)$'
        $script:notification | Should -Match "if: github.event_name != 'workflow_dispatch' && needs.keycloak-e2e.result == 'success'"
        $script:notification | Should -Match "if: github.event_name != 'workflow_dispatch' && needs.keycloak-e2e.result != 'success'"
        [regex]::Matches($script:notification, 'webhook: \$\{\{ secrets.SLACK_WEBHOOK_URL \}\}').Count | Should -Be 2
    }
}
