# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Describe "Education-organization projection toggle wiring" {
    # -ForEach arrays are evaluated at discovery time, before any BeforeAll runs, so the paths
    # are computed inline from $PSScriptRoot rather than from a script-scoped variable.
    It "wires AppSettings__EnableEducationOrganizationProjection to DMS_ENABLE_EDUCATION_ORGANIZATION_PROJECTION, defaulting to true, in <_>" -ForEach @(
        (Join-Path $PSScriptRoot "../local-dms.yml"),
        (Join-Path $PSScriptRoot "../published-dms.yml"),
        (Join-Path $PSScriptRoot "../../azure-vm/compose/docker-compose.yml")
    ) {
        $path = [System.IO.Path]::GetFullPath($_)
        Test-Path -LiteralPath $path | Should -BeTrue -Because "the compose file must exist at $path"
        $content = Get-Content -LiteralPath $path -Raw

        $content | Should -Match 'AppSettings__EnableEducationOrganizationProjection:\s*"?\$\{DMS_ENABLE_EDUCATION_ORGANIZATION_PROJECTION:-true\}"?'
    }

    It "defaults DMS_ENABLE_EDUCATION_ORGANIZATION_PROJECTION to true in <_>" -ForEach @(
        (Join-Path $PSScriptRoot "../.env.example"),
        (Join-Path $PSScriptRoot "../../azure-vm/compose/.env.example")
    ) {
        $path = [System.IO.Path]::GetFullPath($_)
        Test-Path -LiteralPath $path | Should -BeTrue -Because "the env example file must exist at $path"
        $content = Get-Content -LiteralPath $path -Raw

        $content | Should -Match '(?m)^DMS_ENABLE_EDUCATION_ORGANIZATION_PROJECTION=true\r?$'
    }
}
