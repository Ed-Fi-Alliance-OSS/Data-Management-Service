# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

param()

Describe "DMS-1154 Dockerfile ApiSchema packaging contract" {
    BeforeAll {
        $script:dockerfilePath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "../../../src/dms/Dockerfile")
        )
        $script:dockerfileContent = Get-Content -LiteralPath $script:dockerfilePath -Raw
    }

    It "copies Directory.Build.targets into the build stage before restore" {
        $targetsCopyIndex = $script:dockerfileContent.IndexOf("COPY Directory.Build.targets ./")
        $restoreIndex = $script:dockerfileContent.IndexOf("RUN dotnet restore")

        $targetsCopyIndex | Should -BeGreaterOrEqual 0
        $restoreIndex | Should -BeGreaterThan $targetsCopyIndex
    }

    It "copies published ApiSchema content recursively into the final image" {
        $script:dockerfileContent |
            Should -Match 'COPY --from=build /app/Frontend/ApiSchema/ ./ApiSchema/'
        $script:dockerfileContent |
            Should -Not -Match 'COPY --from=build /app/Frontend/ApiSchema/\*\.json ./ApiSchema/'
    }
}
