# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Behavioral specs for the On DMS Pull Request change classifier. These import the real module and
# call it; nothing here reads workflow or script source text. The classifier decides which CI lanes
# run, and until it was extracted from an inline bash step there was no way to exercise it outside a
# CI run.

Describe "DMS pull request change classifier" {
    BeforeAll {
        $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        Import-Module (Join-Path $script:repoRoot "eng/ci/dms-change-categories.psm1") -Force
    }

    AfterAll {
        Remove-Module dms-change-categories -Force -ErrorAction SilentlyContinue
    }

    Context "Events that always validate the full tree" {
        It "runs everything for workflow_dispatch, whatever the file list says" {
            $result = Get-DmsChangeCategory -EventName "workflow_dispatch" -ChangedFile @("docs/README.md")

            $result.fresh_build_required | Should -BeTrue
            $result.dms_relevant | Should -BeTrue
        }

        It "runs everything for an event the classifier does not model" {
            # The bash this replaces had an explicit unreachable branch that ran the full suite
            # rather than infer a diff base. Adding a trigger must not silently start narrowing.
            $result = Get-DmsChangeCategory -EventName "schedule" -ChangedFile @("docs/README.md")

            $result.fresh_build_required | Should -BeTrue
            $result.dms_relevant | Should -BeTrue
        }

        It "runs everything for <EventName> when no trustworthy diff could be produced" -ForEach @(
            @{ EventName = "merge_group" }
            @{ EventName = "pull_request" }
        ) {
            # Narrowing needs a trustworthy file list. Without one the only safe answer is the full
            # suite - skipping a lane because git failed would hide the failure behind a green gate.
            $result = Get-DmsChangeCategory -EventName $EventName -ChangedFile @() -DiffUnavailable

            $result.fresh_build_required | Should -BeTrue
            $result.dms_relevant | Should -BeTrue
        }
    }

    Context "merge_group never narrows" {
        It "reports DMS-relevant even for a docs-only merge group" {
            $result = Get-DmsChangeCategory -EventName "merge_group" -ChangedFile @("docs/README.md")

            $result.dms_relevant | Should -BeTrue
            $result.fresh_build_required | Should -BeFalse
        }

        It "still detects a fresh-rebuild path in a merge group" {
            $result = Get-DmsChangeCategory -EventName "merge_group" -ChangedFile @("eng/docker-compose/postgresql.yml")

            $result.dms_relevant | Should -BeTrue
            $result.fresh_build_required | Should -BeTrue
        }
    }

    Context "pull_request narrows to the changed area" {
        It "reports nothing relevant for an empty file list" {
            $result = Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @()

            $result.fresh_build_required | Should -BeFalse
            $result.dms_relevant | Should -BeFalse
        }

        It "ignores blank entries left by a trailing newline" {
            $result = Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @("", "   ", $null)

            $result.fresh_build_required | Should -BeFalse
            $result.dms_relevant | Should -BeFalse
        }

        It "treats <Path> as DMS-relevant" -ForEach @(
            @{ Path = "build-dms.ps1" }
            @{ Path = ".github/actions/pull-ci-images/action.yml" }
            @{ Path = ".github/workflows/on-dms-pullrequest.yml" }
            @{ Path = "eng/build-helpers.psm1" }
            @{ Path = "eng/ci/tests/DmsChangeCategories.Tests.ps1" }
            @{ Path = "src/dms/core/EdFi.DataManagementService.Core/Something.cs" }
            @{ Path = "src/config/backend/Something.cs" }
            @{ Path = "src/Directory.Packages.props" }
            @{ Path = "src/nuget.config" }
        ) {
            (Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @($Path)).dms_relevant |
                Should -BeTrue
        }

        It "does not treat <Path> as DMS-relevant" -ForEach @(
            @{ Path = "docs/README.md" }
            # Only .github/actions and .github/workflows qualify, not all of .github.
            @{ Path = ".github/dependabot.yml" }
            @{ Path = ".github/CODEOWNERS" }
            # Prefix matching must not spill past the directory boundary.
            @{ Path = "engineering/notes.md" }
            @{ Path = "src/Directory.Build.props" }
            # Exact-path entries are whole paths, not prefixes.
            @{ Path = "build-dms.ps1.bak" }
            @{ Path = "README.md" }
        ) {
            (Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @($Path)).dms_relevant |
                Should -BeFalse
        }

        It "requires a fresh rebuild for <Path>" -ForEach @(
            @{ Path = "src/Directory.Packages.props" }
            @{ Path = "src/nuget.config" }
            @{ Path = "src/dms/Dockerfile" }
            @{ Path = "src/config/Dockerfile" }
            @{ Path = "eng/docker-compose/postgresql.yml" }
            # The bash glob this replaces matched across directory separators, so a nested
            # docker-compose file counts too.
            @{ Path = "eng/docker-compose/tests/BootstrapSeedDelivery.Tests.ps1" }
        ) {
            (Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @($Path)).fresh_build_required |
                Should -BeTrue
        }

        It "does not require a fresh rebuild for <Path>" -ForEach @(
            @{ Path = "src/dms/core/EdFi.DataManagementService.Core/Something.cs" }
            @{ Path = "eng/build-helpers.psm1" }
            @{ Path = "build-dms.ps1" }
            # Exact match only: a differently named Dockerfile is DMS-relevant but not a rebuild
            # trigger.
            @{ Path = "src/dms/Dockerfile.other" }
            @{ Path = "src/dms/Nuget.Dockerfile" }
        ) {
            (Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @($Path)).fresh_build_required |
                Should -BeFalse
        }

        It "matches paths case-sensitively, as Git does" {
            $result = Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @("SRC/DMS/Core/Something.cs")

            $result.dms_relevant | Should -BeFalse
            $result.fresh_build_required | Should -BeFalse
        }

        It "combines flags across a mixed file list" {
            $result = Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @(
                "docs/README.md",
                "src/dms/core/EdFi.DataManagementService.Core/Something.cs",
                "src/dms/Dockerfile"
            )

            $result.dms_relevant | Should -BeTrue
            $result.fresh_build_required | Should -BeTrue
        }

        It "reports DMS-relevant without a fresh rebuild when only source changed" {
            $result = Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @(
                "src/dms/core/EdFi.DataManagementService.Core/Something.cs",
                "src/dms/backend/EdFi.DataManagementService.Backend/Other.cs"
            )

            $result.dms_relevant | Should -BeTrue
            $result.fresh_build_required | Should -BeFalse
        }
    }
}

Describe "Write-DmsChangeCategories output contract" {
    BeforeAll {
        $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
        $script:writeScript = Join-Path $script:repoRoot "eng/ci/Write-DmsChangeCategories.ps1"
    }

    BeforeEach {
        $script:outputPath = Join-Path $TestDrive ([guid]::NewGuid().ToString() + ".txt")
        $script:changedFilePath = Join-Path $TestDrive ([guid]::NewGuid().ToString() + ".list")
    }

    It "writes lowercase flags the workflow if: expressions can compare against" {
        Set-Content -LiteralPath $script:changedFilePath -Value @(
            "docs/README.md"
            "src/dms/Dockerfile"
        )

        & $script:writeScript `
            -EventName "pull_request" `
            -ChangedFilePath $script:changedFilePath `
            -OutputPath $script:outputPath | Out-Null

        $written = @(Get-Content -LiteralPath $script:outputPath)

        $written | Should -Contain "fresh_build_required=true"
        $written | Should -Contain "dms_relevant=true"
    }

    It "writes false rather than omitting a flag when nothing is relevant" {
        Set-Content -LiteralPath $script:changedFilePath -Value "docs/README.md"

        & $script:writeScript `
            -EventName "pull_request" `
            -ChangedFilePath $script:changedFilePath `
            -OutputPath $script:outputPath | Out-Null

        $written = @(Get-Content -LiteralPath $script:outputPath)

        $written | Should -Contain "fresh_build_required=false"
        $written | Should -Contain "dms_relevant=false"
    }

    It "treats a missing changed-file list as an empty list" {
        # workflow_dispatch never computes a diff, so the file the workflow names does not exist.
        & $script:writeScript `
            -EventName "workflow_dispatch" `
            -ChangedFilePath (Join-Path $TestDrive "never-written.list") `
            -OutputPath $script:outputPath | Out-Null

        $written = @(Get-Content -LiteralPath $script:outputPath)

        $written | Should -Contain "fresh_build_required=true"
        $written | Should -Contain "dms_relevant=true"
    }

    It "appends rather than replacing, so it cannot clobber another step's outputs" {
        Set-Content -LiteralPath $script:outputPath -Value "existing_output=kept"
        Set-Content -LiteralPath $script:changedFilePath -Value "docs/README.md"

        & $script:writeScript `
            -EventName "pull_request" `
            -ChangedFilePath $script:changedFilePath `
            -OutputPath $script:outputPath | Out-Null

        @(Get-Content -LiteralPath $script:outputPath) | Should -Contain "existing_output=kept"
    }
}
