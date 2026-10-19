# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Describe "Identity management toggle wiring" {
    # -ForEach arrays are evaluated at discovery time, before any BeforeAll runs, so the paths
    # are computed inline from $PSScriptRoot rather than from a script-scoped variable.
    It "wires DataManagement__IdentityManagement__Enabled to DMS_ENABLE_IDENTITY_MANAGEMENT, defaulting to false like AppSettings__ManagementEndpoints__Enabled, in <_>" -ForEach @(
        (Join-Path $PSScriptRoot "../local-dms.yml"),
        (Join-Path $PSScriptRoot "../published-dms.yml"),
        (Join-Path $PSScriptRoot "../../azure-vm/compose/docker-compose.yml")
    ) {
        $path = [System.IO.Path]::GetFullPath($_)
        Test-Path -LiteralPath $path | Should -BeTrue -Because "the compose file must exist at $path"
        $content = Get-Content -LiteralPath $path -Raw

        $content | Should -Match 'DataManagement__IdentityManagement__Enabled:\s*"?\$\{DMS_ENABLE_IDENTITY_MANAGEMENT:-false\}"?'
        $content | Should -Match 'AppSettings__ManagementEndpoints__Enabled:\s*"?\$\{DMS_ENABLE_MANAGEMENT_ENDPOINTS:-false\}"?'
        $content | Should -Not -Match 'AppSettings__Enable(IdentityManagement|ManagementEndpoints|ClaimsetReload)'
        $content | Should -Not -Match 'DMS_ENABLE_CLAIMSET_RELOAD'
    }

    It "defaults DMS_ENABLE_IDENTITY_MANAGEMENT to false, like DMS_ENABLE_MANAGEMENT_ENDPOINTS, in <_>" -ForEach @(
        (Join-Path $PSScriptRoot "../.env.example"),
        (Join-Path $PSScriptRoot "../../azure-vm/compose/.env.example")
    ) {
        $path = [System.IO.Path]::GetFullPath($_)
        Test-Path -LiteralPath $path | Should -BeTrue -Because "the env example file must exist at $path"
        $content = Get-Content -LiteralPath $path -Raw

        $content | Should -Match 'DMS_ENABLE_IDENTITY_MANAGEMENT=false'
        $content | Should -Match 'DMS_ENABLE_MANAGEMENT_ENDPOINTS=false'
    }
}
