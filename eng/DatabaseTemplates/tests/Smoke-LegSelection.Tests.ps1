# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Describe "Select-SmokeTestLegs" {
    BeforeAll {
        $script:templatesRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $script:selector = Join-Path $script:templatesRoot "Select-SmokeTestLegs.ps1"

        $script:allLegs = @(
            [pscustomobject]@{
                standard_version  = "5.2.0"
                database_engine   = "postgresql"
                package_name      = "EdFi.Api.Smoke.Template.PostgreSql.5.2.0"
                provenance_file   = "populated.template.intoto.jsonl"
                environment_file  = "./.env.smoke"
                run_ods_sdk_tests = $true
            }
            [pscustomobject]@{
                standard_version  = "6.1.0"
                database_engine   = "postgresql"
                package_name      = "EdFi.Api.Smoke.Template.PostgreSql.6.1.0"
                provenance_file   = "populated.template.6.1.0.intoto.jsonl"
                environment_file  = "./.env.smoke.ds61"
                run_ods_sdk_tests = $false
            }
            [pscustomobject]@{
                standard_version  = "5.2.0"
                database_engine   = "mssql"
                package_name      = "EdFi.Api.Smoke.Template.MsSql.5.2.0"
                provenance_file   = "populated.template.mssql.intoto.jsonl"
                environment_file  = "./.env.smoke"
                run_ods_sdk_tests = $true
            }
            [pscustomobject]@{
                standard_version  = "6.1.0"
                database_engine   = "mssql"
                package_name      = "EdFi.Api.Smoke.Template.MsSql.6.1.0"
                provenance_file   = "populated.template.mssql.6.1.0.intoto.jsonl"
                environment_file  = "./.env.smoke.ds61"
                run_ods_sdk_tests = $false
            }
        )

        function Invoke-Selector {
            param(
                [Parameter(Mandatory)][string]$EventName,
                [string[]]$ChangedFiles = @()
            )
            $json = & $script:selector -EventName $EventName -ChangedFiles $ChangedFiles
            return @(ConvertFrom-Json -InputObject $json)
        }

        function Assert-LegSet {
            param(
                [Parameter(Mandatory)]$Actual,
                [Parameter(Mandatory)][int[]]$ExpectedIndices
            )
            $expected = @($ExpectedIndices | ForEach-Object { $script:allLegs[$_].package_name })
            $actualNames = @($Actual | ForEach-Object { $_.package_name })
            $actualNames.Count | Should -Be $expected.Count
            @(Compare-Object -ReferenceObject $expected -DifferenceObject $actualNames -SyncWindow 0) |
                Should -BeNullOrEmpty
        }
    }

    It "returns the full matrix for a <EventName> event regardless of -ChangedFiles" -ForEach @(
        @{ EventName = "schedule" }
        @{ EventName = "workflow_dispatch" }
    ) {
        $legs = Invoke-Selector -EventName $EventName -ChangedFiles @("eng/docker-compose/mssql.yml")
        Assert-LegSet -Actual $legs -ExpectedIndices @(0, 1, 2, 3)
    }

    It "selects only the two mssql legs for eng/docker-compose/mssql.yml" {
        $legs = Invoke-Selector -EventName "pull_request" -ChangedFiles @("eng/docker-compose/mssql.yml")
        Assert-LegSet -Actual $legs -ExpectedIndices @(2, 3)
    }

    It "selects only the two postgresql legs for eng/docker-compose/postgresql.yml" {
        $legs = Invoke-Selector -EventName "pull_request" -ChangedFiles @("eng/docker-compose/postgresql.yml")
        Assert-LegSet -Actual $legs -ExpectedIndices @(0, 1)
    }

    It "selects the two 6.1 legs across engines for eng/docker-compose/.env.smoke.ds61" {
        $legs = Invoke-Selector -EventName "pull_request" -ChangedFiles @("eng/docker-compose/.env.smoke.ds61")
        Assert-LegSet -Actual $legs -ExpectedIndices @(1, 3)
    }

    It "selects all four legs for a shared path" {
        $legs = Invoke-Selector -EventName "pull_request" -ChangedFiles @("eng/smoke_test/modules/SmokeTest.psm1")
        Assert-LegSet -Actual $legs -ExpectedIndices @(0, 1, 2, 3)
    }

    It "selects all four legs for an unmapped path" {
        $legs = Invoke-Selector -EventName "pull_request" -ChangedFiles @("README.md")
        Assert-LegSet -Actual $legs -ExpectedIndices @(0, 1, 2, 3)
    }

    It "selects all four legs for an empty changed-file list" {
        $legs = Invoke-Selector -EventName "pull_request" -ChangedFiles @()
        Assert-LegSet -Actual $legs -ExpectedIndices @(0, 1, 2, 3)
    }

    It "selects the union of legs for mixed changed files" {
        $legs = Invoke-Selector -EventName "pull_request" -ChangedFiles @(
            "eng/docker-compose/mssql.yml",
            "eng/docker-compose/.env.smoke"
        )
        Assert-LegSet -Actual $legs -ExpectedIndices @(0, 2, 3)
    }

    It "emits every leg object with all six keys and correct values" {
        $legs = Invoke-Selector -EventName "schedule"
        $legs.Count | Should -Be 4

        foreach ($leg in $legs) {
            @($leg.PSObject.Properties.Name | Sort-Object) | Should -Be @(
                "database_engine",
                "environment_file",
                "package_name",
                "provenance_file",
                "run_ods_sdk_tests",
                "standard_version"
            )
        }

        $postgres52 = $legs | Where-Object { $_.database_engine -eq "postgresql" -and $_.standard_version -eq "5.2.0" }
        $postgres61 = $legs | Where-Object { $_.database_engine -eq "postgresql" -and $_.standard_version -eq "6.1.0" }
        $mssql52 = $legs | Where-Object { $_.database_engine -eq "mssql" -and $_.standard_version -eq "5.2.0" }
        $mssql61 = $legs | Where-Object { $_.database_engine -eq "mssql" -and $_.standard_version -eq "6.1.0" }

        $postgres52.package_name | Should -Be "EdFi.Api.Smoke.Template.PostgreSql.5.2.0"
        $postgres52.provenance_file | Should -Be "populated.template.intoto.jsonl"
        $postgres52.environment_file | Should -Be "./.env.smoke"
        $postgres52.run_ods_sdk_tests | Should -BeTrue

        $postgres61.package_name | Should -Be "EdFi.Api.Smoke.Template.PostgreSql.6.1.0"
        $postgres61.provenance_file | Should -Be "populated.template.6.1.0.intoto.jsonl"
        $postgres61.environment_file | Should -Be "./.env.smoke.ds61"
        $postgres61.run_ods_sdk_tests | Should -BeFalse

        $mssql52.package_name | Should -Be "EdFi.Api.Smoke.Template.MsSql.5.2.0"
        $mssql52.provenance_file | Should -Be "populated.template.mssql.intoto.jsonl"
        $mssql52.environment_file | Should -Be "./.env.smoke"
        $mssql52.run_ods_sdk_tests | Should -BeTrue

        $mssql61.package_name | Should -Be "EdFi.Api.Smoke.Template.MsSql.6.1.0"
        $mssql61.provenance_file | Should -Be "populated.template.mssql.6.1.0.intoto.jsonl"
        $mssql61.environment_file | Should -Be "./.env.smoke.ds61"
        $mssql61.run_ods_sdk_tests | Should -BeFalse
    }

    It "keeps the workflow trigger paths and the leg-selection path map identical" {
        $workflowPath = [System.IO.Path]::GetFullPath(
            (Join-Path $script:templatesRoot "../../.github/workflows/scheduled-smoke-test.yml")
        )
        $workflowContent = Get-Content -LiteralPath $workflowPath -Raw

        # The pull_request block is the last child key under the top-level "on:" mapping in this
        # workflow, so its body runs up to the next line that starts at column 0 - i.e. the next
        # key that is a sibling of "on:" itself (here, "permissions:").
        $pullRequestBlock = [regex]::Match(
            $workflowContent,
            '(?ms)^  pull_request:\r?\n(?<body>.*?)(?=^\S)'
        ).Groups["body"].Value
        $pullRequestBlock | Should -Not -BeNullOrEmpty -Because "the pull_request: block must be found before its paths can be compared"

        # Each "paths:" list entry is a single- or double-quoted scalar; match either quote style
        # so a future re-quoting of the list does not silently zero out this guard.
        $workflowPaths = @(
            [regex]::Matches($pullRequestBlock, '(?m)^\s*-\s+([''"])(?<path>[^''"]+)\1\s*$') |
                ForEach-Object { $_.Groups["path"].Value }
        )
        $workflowPaths | Should -Not -BeNullOrEmpty -Because "an empty capture means the extraction regex has silently rotted rather than the paths block being genuinely empty"

        $scriptPatterns = @(& $script:selector -ListPathPatterns)

        @(Compare-Object -ReferenceObject $workflowPaths -DifferenceObject $scriptPatterns) |
            Should -BeNullOrEmpty -Because "a path that can trigger scheduled-smoke-test.yml on a pull request must be classifiable by Select-SmokeTestLegs.ps1's path map, and every pattern in that map must correspond to a real workflow trigger path"
    }
}
