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

    Context "Promoted-suite categories" {
        BeforeAll {
            $script:allCategory = @(
                'backend_mssql_relevant'
                'dms_api_relevant'
                'schematools_relevant'
                'cdc_relevant'
            )

            function Get-SetCategory {
                # The promoted categories a single changed path turns on, for a ready pull request.
                param([Parameter(Mandatory)] [string] $Path)

                $result = Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @($Path)

                return @(
                    $script:allCategory | Where-Object { $result.$_ }
                ) | Sort-Object
            }
        }

        Context "Events that never narrow" {
            It "reports every category for <EventName>" -ForEach @(
                @{ EventName = "merge_group" }
                @{ EventName = "workflow_dispatch" }
            ) {
                # The merge queue is the recovery path for everything a pull request skipped, so it
                # must never inherit a category decision.
                $result = Get-DmsChangeCategory -EventName $EventName -ChangedFile @("docs/README.md")

                foreach ($name in $script:allCategory) {
                    $result.$name | Should -BeTrue -Because "$name must be true for $EventName"
                }
            }

            It "reports every category when the diff could not be produced" {
                $result = Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @() -DiffUnavailable

                foreach ($name in $script:allCategory) {
                    $result.$name | Should -BeTrue -Because "$name must be true when nothing can be classified"
                }
            }
        }

        Context "Shared paths reach every promoted lane" {
            It "<Path> sets every category" -ForEach @(
                @{ Path = "src/dms/core/EdFi.DataManagementService.Core/Something.cs" }
                @{ Path = "src/dms/backend/EdFi.DataManagementService.Backend/Something.cs" }
                @{ Path = "src/dms/backend/EdFi.DataManagementService.Backend.External/Something.cs" }
                @{ Path = "src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/Something.cs" }
                @{ Path = "src/dms/backend/EdFi.DataManagementService.Backend.Plans/Something.cs" }
                @{ Path = "src/dms/backend/EdFi.DataManagementService.Backend.Ddl/Something.cs" }
                @{ Path = "src/dms/backend/Fixtures/authoritative/ds-5.2/inputs/x.json" }
                @{ Path = "src/dms/backend/EdFi.DataManagementService.Backend.Tests.Integration.Common/Something.cs" }
                # The SQL Server provider backs every MSSQL lane, and CDC is SQL-Server-only.
                @{ Path = "src/dms/backend/EdFi.DataManagementService.Backend.Mssql/Something.cs" }
                @{ Path = "build-dms.ps1" }
                # Imported by build-dms.ps1, so it is classified exactly like build-dms.ps1.
                @{ Path = "package-helpers.psm1" }
                # Pins the dotnet local tools build-dms.ps1 runs, the coverage merge's
                # reportgenerator among them.
                @{ Path = ".config/dotnet-tools.json" }
                @{ Path = "eng/build-helpers.psm1" }
                @{ Path = ".github/workflows/on-dms-pullrequest.yml" }
                @{ Path = "src/Directory.Packages.props" }
                # Copied into both Docker build contexts beside Directory.Packages.props, and its
                # severity settings are build-affecting under TreatWarningsAsErrors, so it is
                # classified exactly like that file.
                @{ Path = "src/.editorconfig" }
                @{ Path = "src/dms/Directory.Build.props" }
                @{ Path = "src/dms/EdFi.DataManagementService.sln" }
            ) {
                Get-SetCategory -Path $Path |
                    Should -Be @('backend_mssql_relevant', 'cdc_relevant', 'dms_api_relevant', 'schematools_relevant')
            }
        }

        Context "Narrow paths reach only their own lanes" {
            It "<Path> sets exactly <Expected>" -ForEach @(
                @{
                    Path     = "src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/Something.cs"
                    Expected = @('backend_mssql_relevant')
                }
                @{
                    # CDC source reaches the backend MSSQL lane as well; see the dedicated spec below.
                    Path     = "src/dms/backend/EdFi.DataManagementService.Backend.Cdc/Something.cs"
                    Expected = @('backend_mssql_relevant', 'cdc_relevant')
                }
                @{
                    Path     = "src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/Something.cs"
                    Expected = @('cdc_relevant')
                }
                @{
                    Path     = "src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/Something.cs"
                    Expected = @('cdc_relevant')
                }
                @{
                    Path     = "src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Something.cs"
                    Expected = @('dms_api_relevant')
                }
                @{
                    Path     = "src/dms/tests/EdFi.DataManagementService.Tests.Integration/Something.cs"
                    Expected = @('dms_api_relevant')
                }
                @{
                    Path     = "src/dms/clis/EdFi.DataManagementService.SchemaTools/Something.cs"
                    Expected = @('schematools_relevant')
                }
                @{
                    Path     = "src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration/Something.cs"
                    Expected = @('schematools_relevant')
                }
                @{
                    Path     = "src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Something.cs"
                    Expected = @('dms_api_relevant', 'schematools_relevant')
                }
            ) {
                Get-SetCategory -Path $Path | Should -Be (@($Expected) | Sort-Object)
            }
        }

        Context "Known paths that no promoted lane exercises" {
            It "<Path> sets no category" -ForEach @(
                # Backend PostgreSQL Integration is deliberately not promoted, so its own test
                # project must not promote anything either - but it is a known path, so it must not
                # fail open.
                @{ Path = "src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/Something.cs" }
                # Only the always-on unit-test job runs Backend.Tests.Unit, so it must not fail
                # open into every promoted lane.
                @{ Path = "src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Something.cs" }
                @{ Path = "src/dms/clis/EdFi.DataManagementService.OpenApiGenerator/Something.cs" }
                @{ Path = "src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/Something.cs" }
                @{ Path = "src/dms/tests/EdFi.DataManagementService.Tests.E2E/Something.cs" }
                @{ Path = "src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Something.cs" }
                @{ Path = "src/dms/tests/EdFi.DataManagementService.Tests.Unit/Something.cs" }
                @{ Path = "src/config/backend/Something.cs" }
                @{ Path = "docs/README.md" }
            ) {
                Get-SetCategory -Path $Path | Should -BeNullOrEmpty
            }

            It "still reports those DMS paths as DMS-relevant" {
                # Not promoting a lane must not stop the file's own lanes from running.
                (Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @("src/config/backend/Something.cs")).dms_relevant |
                    Should -BeTrue
            }
        }

        Context "Unclassified DMS paths fail open" {
            It "<Path> sets every category" -ForEach @(
                @{ Path = "src/dms/backend/EdFi.DataManagementService.Backend.SomethingNew/File.cs" }
                @{ Path = "src/dms/somethingnew/File.cs" }
                @{ Path = "src/dms/Dockerfile" }
                @{ Path = "src/dms/run.sh" }
            ) {
                # A directory nobody has classified yet is validated by everything. The opposite
                # failure - a new directory silently skipping every promoted lane - is the one this
                # design cannot afford.
                Get-SetCategory -Path $Path |
                    Should -Be @('backend_mssql_relevant', 'cdc_relevant', 'dms_api_relevant', 'schematools_relevant')
            }

            It "does not fail open for a path that is not DMS-relevant at all" {
                Get-SetCategory -Path "docs/architecture/notes.md" | Should -BeNullOrEmpty
            }
        }

        Context "Category matching respects directory boundaries" {
            It "treats Backend and Backend.Mssql.Tests.Integration as different projects" {
                # A prefix that ignored the boundary would fold the narrow lane into the shared one
                # and silently promote everything.
                Get-SetCategory -Path "src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/x.cs" |
                    Should -Be @('backend_mssql_relevant')
            }

            It "treats Postgresql and Postgresql.Tests.Integration as different projects" {
                Get-SetCategory -Path "src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/x.cs" |
                    Should -Be @('dms_api_relevant', 'schematools_relevant')

                Get-SetCategory -Path "src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/x.cs" |
                    Should -BeNullOrEmpty
            }

            It "promotes the backend MSSQL lane for a change to CDC source" {
                # EdFi.DataManagementService.Backend.Mssql.Tests.Integration references
                # EdFi.DataManagementService.Backend.Cdc and holds the only DB-backed CDC tests in
                # the suite - MssqlCdcSourcePositionAdapterTests is [Category("DatabaseIntegration")]
                # and [Category("MssqlIntegration")]. The promoted CDC lane cannot cover them: it
                # runs with --filter "Category!=DatabaseIntegration". Without this category a CDC
                # source change ran no DB-backed CDC test at all.
                Get-SetCategory -Path "src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcSourcePositionAdapter.cs" |
                    Should -Contain 'backend_mssql_relevant'
            }

            It "leaves the CDC test projects reaching only the CDC lane" {
                # The reference runs from the MSSQL integration project to CDC *source*, not to the
                # CDC test projects, so those stay narrow.
                foreach ($path in @(
                        "src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/x.cs"
                        "src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/x.cs"
                    )) {
                    Get-SetCategory -Path $path | Should -Be @('cdc_relevant')
                }
            }

            It "matches category paths case-sensitively, as Git does" {
                Get-SetCategory -Path "SRC/DMS/backend/EdFi.DataManagementService.Backend.Cdc/x.cs" |
                    Should -BeNullOrEmpty
            }
        }

        Context "Categories combine across a file list" {
            It "unions the categories of every changed file" {
                # Deliberately the CDC integration test project rather than CDC source: that path
                # reaches one lane, so this stays a test about unioning rather than about the
                # backend MSSQL promotion, which has its own spec.
                $result = Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @(
                    "src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/x.cs",
                    "src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/y.cs"
                )

                $result.cdc_relevant | Should -BeTrue
                $result.dms_api_relevant | Should -BeTrue
                $result.backend_mssql_relevant | Should -BeFalse
                $result.schematools_relevant | Should -BeFalse
            }

            It "lets one shared file promote everything regardless of its neighbours" {
                $result = Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @(
                    "src/dms/tests/EdFi.DataManagementService.Tests.E2E/x.cs",
                    "src/dms/core/EdFi.DataManagementService.Core/y.cs"
                )

                foreach ($name in $script:allCategory) {
                    $result.$name | Should -BeTrue -Because "$name must follow a core change"
                }
            }

            It "reports no category for an empty file list on a pull request" {
                $result = Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @()

                foreach ($name in $script:allCategory) {
                    $result.$name | Should -BeFalse
                }
            }
        }
    }

    Context "Draft reporting" {
        It "reports draft for a draft pull request" {
            (Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @("src/dms/x.cs") -IsDraft).draft |
                Should -BeTrue
        }

        It "does not report draft for a ready pull request" {
            (Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @("src/dms/x.cs")).draft |
                Should -BeFalse
        }

        It "never reports draft for <EventName>, whatever the payload carried" -ForEach @(
            @{ EventName = "merge_group" }
            @{ EventName = "workflow_dispatch" }
        ) {
            # A draft is a statement about a pull request. If a stale payload value ever reached
            # another event, gating the merge queue on it would skip the full suite it exists to run.
            (Get-DmsChangeCategory -EventName $EventName -ChangedFile @("src/dms/x.cs") -IsDraft).draft |
                Should -BeFalse
        }

        It "still reports draft when the diff could not be produced" {
            # Draft state does not depend on the file list, so the fail-open path must not lose it.
            (Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @() -DiffUnavailable -IsDraft).draft |
                Should -BeTrue
        }

        It "reports draft alongside, not instead of, the file classification" {
            $result = Get-DmsChangeCategory -EventName "pull_request" -ChangedFile @("src/dms/Dockerfile") -IsDraft

            $result.draft | Should -BeTrue
            $result.dms_relevant | Should -BeTrue
            $result.fresh_build_required | Should -BeTrue
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
            @{ Path = "package-helpers.psm1" }
            @{ Path = ".config/dotnet-tools.json" }
            @{ Path = ".github/actions/pull-ci-images/action.yml" }
            @{ Path = ".github/workflows/on-dms-pullrequest.yml" }
            @{ Path = "eng/build-helpers.psm1" }
            @{ Path = "eng/ci/tests/DmsChangeCategories.Tests.ps1" }
            @{ Path = "src/dms/core/EdFi.DataManagementService.Core/Something.cs" }
            @{ Path = "src/config/backend/Something.cs" }
            @{ Path = "src/Directory.Packages.props" }
            @{ Path = "src/nuget.config" }
            @{ Path = "src/.editorconfig" }
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
            @{ Path = "src/.editorconfig" }
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

    It "emits the draft flag the workflow gates on" {
        Set-Content -LiteralPath $script:changedFilePath -Value "src/dms/x.cs"

        & $script:writeScript `
            -EventName "pull_request" `
            -ChangedFilePath $script:changedFilePath `
            -IsDraft `
            -OutputPath $script:outputPath | Out-Null

        @(Get-Content -LiteralPath $script:outputPath) | Should -Contain "draft=true"
    }

    It "emits draft=false rather than omitting it on a ready pull request" {
        # An omitted output evaluates to the empty string, which would silently satisfy
        # `draft != 'true'` today and hide a wiring mistake later.
        Set-Content -LiteralPath $script:changedFilePath -Value "src/dms/x.cs"

        & $script:writeScript `
            -EventName "pull_request" `
            -ChangedFilePath $script:changedFilePath `
            -OutputPath $script:outputPath | Out-Null

        @(Get-Content -LiteralPath $script:outputPath) | Should -Contain "draft=false"
    }

    It "emits every promoted category flag the workflow gates on" {
        # The CDC integration test project, not CDC source: this spec needs categories that stay
        # false to prove a false flag is still written.
        Set-Content -LiteralPath $script:changedFilePath -Value "src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/x.cs"

        & $script:writeScript `
            -EventName "pull_request" `
            -ChangedFilePath $script:changedFilePath `
            -OutputPath $script:outputPath | Out-Null

        $written = @(Get-Content -LiteralPath $script:outputPath)

        # Written whether true or false: an omitted output evaluates to the empty string, which
        # never equals 'true', so a missing flag would look exactly like a deliberate skip.
        $written | Should -Contain "cdc_relevant=true"
        $written | Should -Contain "backend_mssql_relevant=false"
        $written | Should -Contain "dms_api_relevant=false"
        $written | Should -Contain "schematools_relevant=false"
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
