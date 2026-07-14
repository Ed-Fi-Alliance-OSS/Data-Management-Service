# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Describe "Assert-TemplateWorkflowInputs" {
    BeforeAll {
        $script:templatesRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $script:templatesRoot "../docker-compose"))
        $script:workflowRoot = [System.IO.Path]::GetFullPath((Join-Path $script:templatesRoot "../../.github/workflows"))
        $script:validator = Join-Path $script:templatesRoot "Assert-TemplateWorkflowInputs.ps1"
        $script:templateCallerCases = @(
            [pscustomobject]@{
                FileName        = "EdFi.Api.Minimal.Template.PostgreSQL.yml"
                Reusable        = ".github/workflows/build-minimal-template.yml"
                BackendPath     = "src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/**"
                ComposePath     = "eng/docker-compose/postgresql.yml"
                ExtraPaths      = @("eng/docker-compose/postgresql-init.sh")
                ForkGuardCount  = 1
                PackageNames    = @(
                    "EdFi.Api.Minimal.Template.PostgreSql.5.2.0",
                    "EdFi.Api.Minimal.Template.PostgreSql.6.1.0"
                )
            }
            [pscustomobject]@{
                FileName        = "EdFi.Api.Minimal.Template.MsSql.yml"
                Reusable        = ".github/workflows/build-minimal-template.yml"
                BackendPath     = "src/dms/backend/EdFi.DataManagementService.Backend.Mssql/**"
                ComposePath     = "eng/docker-compose/mssql.yml"
                ExtraPaths      = @("eng/docker-compose/.env.mssql")
                ForkGuardCount  = 1
                PackageNames    = @(
                    "EdFi.Api.Minimal.Template.MsSql.5.2.0",
                    "EdFi.Api.Minimal.Template.MsSql.6.1.0"
                )
            }
            [pscustomobject]@{
                FileName        = "EdFi.Api.Populated.Template.PostgreSQL.yml"
                Reusable        = ".github/workflows/build-populated-template.yml"
                BackendPath     = "src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/**"
                ComposePath     = "eng/docker-compose/postgresql.yml"
                ExtraPaths      = @("eng/docker-compose/postgresql-init.sh")
                ForkGuardCount  = 2
                PackageNames    = @(
                    "EdFi.Api.Populated.Template.PostgreSql.5.2.0",
                    "EdFi.Api.Populated.Template.PostgreSql.6.1.0"
                )
            }
            [pscustomobject]@{
                FileName        = "EdFi.Api.Populated.Template.MsSql.yml"
                Reusable        = ".github/workflows/build-populated-template.yml"
                BackendPath     = "src/dms/backend/EdFi.DataManagementService.Backend.Mssql/**"
                ComposePath     = "eng/docker-compose/mssql.yml"
                ExtraPaths      = @("eng/docker-compose/.env.mssql")
                ForkGuardCount  = 2
                PackageNames    = @(
                    "EdFi.Api.Populated.Template.MsSql.5.2.0",
                    "EdFi.Api.Populated.Template.MsSql.6.1.0"
                )
            }
        )
    }

    BeforeEach {
        $script:work = Join-Path ([System.IO.Path]::GetTempPath()) "dms-template-inputs-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $script:work -Force | Out-Null
        foreach ($fileName in @(".env.template", ".env.template.ds61", ".env.smoke", ".env.smoke.ds61")) {
            Copy-Item -LiteralPath (Join-Path $script:dockerComposeRoot $fileName) -Destination $script:work
        }
    }

    AfterEach {
        if (Test-Path -LiteralPath $script:work) {
            Remove-Item -LiteralPath $script:work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It "accepts every supported product tuple: <WorkflowKind> / <DatabaseEngine> / <StandardVersion>" -ForEach @(
        @{ WorkflowKind = "Minimal"; DatabaseEngine = "postgresql"; StandardVersion = "5.2.0"; EnvironmentFile = "./.env.template"; PackageName = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0" }
        @{ WorkflowKind = "Minimal"; DatabaseEngine = "postgresql"; StandardVersion = "6.1.0"; EnvironmentFile = "./.env.template.ds61"; PackageName = "EdFi.Api.Minimal.Template.PostgreSql.6.1.0" }
        @{ WorkflowKind = "Minimal"; DatabaseEngine = "mssql"; StandardVersion = "5.2.0"; EnvironmentFile = "./.env.template"; PackageName = "EdFi.Api.Minimal.Template.MsSql.5.2.0" }
        @{ WorkflowKind = "Minimal"; DatabaseEngine = "mssql"; StandardVersion = "6.1.0"; EnvironmentFile = "./.env.template.ds61"; PackageName = "EdFi.Api.Minimal.Template.MsSql.6.1.0" }
        @{ WorkflowKind = "Populated"; DatabaseEngine = "postgresql"; StandardVersion = "5.2.0"; EnvironmentFile = "./.env.template"; PackageName = "EdFi.Api.Populated.Template.PostgreSql.5.2.0" }
        @{ WorkflowKind = "Populated"; DatabaseEngine = "postgresql"; StandardVersion = "6.1.0"; EnvironmentFile = "./.env.template.ds61"; PackageName = "EdFi.Api.Populated.Template.PostgreSql.6.1.0" }
        @{ WorkflowKind = "Populated"; DatabaseEngine = "mssql"; StandardVersion = "5.2.0"; EnvironmentFile = "./.env.template"; PackageName = "EdFi.Api.Populated.Template.MsSql.5.2.0" }
        @{ WorkflowKind = "Populated"; DatabaseEngine = "mssql"; StandardVersion = "6.1.0"; EnvironmentFile = "./.env.template.ds61"; PackageName = "EdFi.Api.Populated.Template.MsSql.6.1.0" }
    ) {
        $parameters = @{
            WorkflowKind      = $WorkflowKind
            StandardVersion   = $StandardVersion
            PackageName       = $PackageName
            EnvironmentFile   = $EnvironmentFile
            DatabaseEngine    = $DatabaseEngine
            PublishPackage    = $true
            DockerComposeRoot = $script:work
        }

        { & $script:validator @parameters } | Should -Not -Throw
    }

    It "accepts the two non-publishing PostgreSQL smoke tuples" -ForEach @(
        @{ StandardVersion = "5.2.0"; EnvironmentFile = "./.env.smoke"; PackageName = "EdFi.Api.Smoke.Template.PostgreSql.5.2.0" }
        @{ StandardVersion = "6.1.0"; EnvironmentFile = "./.env.smoke.ds61"; PackageName = "EdFi.Api.Smoke.Template.PostgreSql.6.1.0" }
    ) {
        {
            & $script:validator `
                -WorkflowKind "Populated" `
                -StandardVersion $StandardVersion `
                -PackageName $PackageName `
                -EnvironmentFile $EnvironmentFile `
                -DatabaseEngine "postgresql" `
                -PublishPackage $false `
                -VerifyRestore $true `
                -RequirePopulatedData $true `
                -DockerComposeRoot $script:work
        } | Should -Not -Throw
    }

    It "rejects a standard_version and environment_file from different Data Standards" {
        {
            & $script:validator `
                -WorkflowKind "Minimal" `
                -StandardVersion "6.1.0" `
                -PackageName "EdFi.Api.Minimal.Template.MsSql.6.1.0" `
                -EnvironmentFile "./.env.template" `
                -DatabaseEngine "mssql" `
                -DockerComposeRoot $script:work
        } | Should -Throw "*selects Data Standard 5.2.0*"
    }

    It "rejects a package name whose exact version suffix differs from standard_version" {
        {
            & $script:validator `
                -WorkflowKind "Minimal" `
                -StandardVersion "6.1.0" `
                -PackageName "EdFi.Api.Minimal.Template.MsSql.5.2.0" `
                -EnvironmentFile "./.env.template.ds61" `
                -DatabaseEngine "mssql" `
                -DockerComposeRoot $script:work
        } | Should -Throw "*require package_name*6.1.0*"
    }

    It "rejects an environment whose core SCHEMA_PACKAGES identity differs from standard_version" {
        $environmentPath = Join-Path $script:work ".env.template.ds61"
        $content = (Get-Content -LiteralPath $environmentPath -Raw).Replace(
            "EdFi.DataStandard61.ApiSchema",
            "EdFi.DataStandard52.ApiSchema"
        )
        Set-Content -LiteralPath $environmentPath -Value $content -NoNewline

        {
            & $script:validator `
                -WorkflowKind "Populated" `
                -StandardVersion "6.1.0" `
                -PackageName "EdFi.Api.Populated.Template.PostgreSql.6.1.0" `
                -EnvironmentFile "./.env.template.ds61" `
                -DatabaseEngine "postgresql" `
                -DockerComposeRoot $script:work
        } | Should -Throw "*SCHEMA_PACKAGES core*does not match*"
    }

    It "rejects a mixed Data Standard extension package set" {
        $environmentPath = Join-Path $script:work ".env.template"
        $content = (Get-Content -LiteralPath $environmentPath -Raw).Replace(
            "EdFi.DataStandard52.TPDM.ApiSchema",
            "EdFi.DataStandard61.TPDM.ApiSchema"
        )
        Set-Content -LiteralPath $environmentPath -Value $content -NoNewline

        {
            & $script:validator `
                -WorkflowKind "Populated" `
                -StandardVersion "5.2.0" `
                -PackageName "EdFi.Api.Populated.Template.PostgreSql.5.2.0" `
                -EnvironmentFile "./.env.template" `
                -DatabaseEngine "postgresql" `
                -DockerComposeRoot $script:work
        } | Should -Throw "*SCHEMA_PACKAGES package*DataStandard61.TPDM*does not match*"
    }

    It "rejects an environment whose configured template package version differs" {
        $environmentPath = Join-Path $script:work ".env.template.ds61"
        $content = (Get-Content -LiteralPath $environmentPath -Raw).Replace(
            "DATABASE_TEMPLATE_PACKAGE=EdFi.Api.Populated.Template.PostgreSql.6.1.0",
            "DATABASE_TEMPLATE_PACKAGE=EdFi.Api.Populated.Template.PostgreSql.5.2.0"
        )
        Set-Content -LiteralPath $environmentPath -Value $content -NoNewline

        {
            & $script:validator `
                -WorkflowKind "Populated" `
                -StandardVersion "6.1.0" `
                -PackageName "EdFi.Api.Populated.Template.PostgreSql.6.1.0" `
                -EnvironmentFile "./.env.template.ds61" `
                -DatabaseEngine "postgresql" `
                -DockerComposeRoot $script:work
        } | Should -Throw "*DATABASE_TEMPLATE_PACKAGE*must be*6.1.0*"
    }

    It "rejects MSSQL smoke, publishing smoke, and Minimal smoke combinations" -ForEach @(
        @{ WorkflowKind = "Populated"; DatabaseEngine = "mssql"; PublishPackage = $false; Expected = "*PostgreSQL-only*" }
        @{ WorkflowKind = "Populated"; DatabaseEngine = "postgresql"; PublishPackage = $true; Expected = "*must not be published*" }
        @{ WorkflowKind = "Minimal"; DatabaseEngine = "postgresql"; PublishPackage = $false; Expected = "*Minimal template workflow cannot use smoke*" }
    ) {
        {
            & $script:validator `
                -WorkflowKind $WorkflowKind `
                -StandardVersion "5.2.0" `
                -PackageName "EdFi.Api.Smoke.Template.PostgreSql.5.2.0" `
                -EnvironmentFile "./.env.smoke" `
                -DatabaseEngine $DatabaseEngine `
                -PublishPackage $PublishPackage `
                -DockerComposeRoot $script:work
        } | Should -Throw $Expected
    }

    It "rejects require_populated_data without restore verification" {
        {
            & $script:validator `
                -WorkflowKind "Populated" `
                -StandardVersion "5.2.0" `
                -PackageName "EdFi.Api.Populated.Template.PostgreSql.5.2.0" `
                -EnvironmentFile "./.env.template" `
                -DatabaseEngine "postgresql" `
                -RequirePopulatedData $true `
                -DockerComposeRoot $script:work
        } | Should -Throw "*require_populated_data requires verify_restore*"
    }

    It "keeps reusable release workflows publish-by-default" {
        foreach ($workflowName in @("build-minimal-template.yml", "build-populated-template.yml")) {
            $workflowPath = Join-Path $script:templatesRoot "../../.github/workflows/$workflowName"
            $workflowContent = Get-Content -LiteralPath $workflowPath -Raw
            $workflowContent | Should -Match '(?ms)^      publish_package:\r?\n.*?^        default: true$'
        }
    }

    It "keeps pull-request validation with each artifact-owning workflow" {
        Test-Path -LiteralPath (Join-Path $script:workflowRoot "on-mssql-template-pullrequest.yml") |
            Should -BeFalse -Because "the package callers own their PR and release lifecycle without a duplicated MSSQL-only orchestrator"

        $sharedPaths = @(
            "eng/DatabaseTemplates/**",
            "eng/Dms-Management.psm1",
            "eng/Package-Management.psm1",
            "eng/SchoolYear-Loader.psm1",
            "eng/schema-package-utility.psm1",
            "eng/docker-compose/.env.template*",
            "eng/docker-compose/bootstrap-claims-gate.psm1",
            "eng/docker-compose/bootstrap-manifest.psm1",
            "eng/docker-compose/env-utility.psm1",
            "eng/docker-compose/local-config.yml",
            "eng/docker-compose/local-dms.yml",
            "eng/docker-compose/OpenIddict-Crypto.psm1",
            "eng/docker-compose/provision-dms-schema.ps1",
            "eng/docker-compose/provision-e2e-database.ps1",
            "eng/docker-compose/setup-openiddict.ps1",
            "eng/docker-compose/start-local-dms.ps1",
            "package-helpers.psm1",
            "src/.editorconfig",
            "src/Directory.Packages.props",
            "src/nuget.config",
            "src/dms/Directory.Build.props",
            "src/dms/Directory.Build.targets",
            "src/dms/Dockerfile",
            "src/dms/backend/EdFi.DataManagementService.Backend.Ddl/**",
            "src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel/**",
            "src/dms/clis/EdFi.DataManagementService.SchemaTools/**",
            "src/dms/run.sh"
        )
        $publishGate = "publish_package: `${{ github.event_name != 'pull_request' && (github.event_name != 'workflow_dispatch' || inputs.publish_package == true) }}"
        $forkGate = "if: `${{ github.event_name != 'pull_request' || github.event.pull_request.head.repo.fork == false }}"
        $concurrencyGroup = "group: `${{ github.workflow }}-`${{ github.event.pull_request.number || github.run_id }}"
        $cancelInProgress = "cancel-in-progress: `${{ github.event_name == 'pull_request' }}"

        foreach ($case in $script:templateCallerCases) {
            $workflowPath = Join-Path $script:workflowRoot $case.FileName
            $workflowContent = Get-Content -LiteralPath $workflowPath -Raw
            $pullRequestBlock = [regex]::Match(
                $workflowContent,
                '(?ms)^  pull_request:\r?\n(?<body>.*?)(?=^  release:)'
            ).Groups["body"].Value
            $actualPaths = @(
                [regex]::Matches($pullRequestBlock, '(?m)^      - "(?<path>[^"]+)"$') |
                    ForEach-Object { $_.Groups["path"].Value }
            )
            $expectedPaths = @(
                ".github/workflows/$($case.FileName)"
                $case.Reusable
                $sharedPaths
                $case.ComposePath
                $case.BackendPath
                $case.ExtraPaths
            )

            $pullRequestBlock | Should -Not -BeNullOrEmpty
            $actualPaths.Count | Should -Be $expectedPaths.Count
            @(Compare-Object -ReferenceObject $expectedPaths -DifferenceObject $actualPaths) |
                Should -BeNullOrEmpty -Because "$($case.FileName) must run only for its own kind/engine plus shared template dependencies"
            @([regex]::Matches($workflowContent, [regex]::Escape($publishGate))).Count |
                Should -Be 1 -Because "$($case.FileName) must never publish from a PR"
            @([regex]::Matches($workflowContent, [regex]::Escape($forkGate))).Count |
                Should -Be $case.ForkGuardCount -Because "secret-consuming PR jobs must skip fork pull requests"
            @([regex]::Matches($workflowContent, [regex]::Escape($concurrencyGroup))).Count | Should -Be 1
            @([regex]::Matches($workflowContent, [regex]::Escape($cancelInProgress))).Count |
                Should -Be 1 -Because "new commits should cancel obsolete template builds only for the same PR"

            foreach ($packageName in $case.PackageNames) {
                @([regex]::Matches($workflowContent, [regex]::Escape($packageName))).Count |
                    Should -Be 1 -Because "$($case.FileName) must own each of its two Data Standard package legs exactly once"
            }
        }
    }

    It "keeps template prereleases excluded and stable-release promotion explicit" {
        foreach ($case in $script:templateCallerCases) {
            $workflowContent = Get-Content -LiteralPath (Join-Path $script:workflowRoot $case.FileName) -Raw
            $workflowContent | Should -Match '(?ms)^  release:\r?\n(?:.*\r?\n)*?    types:\r?\n      - released$'
            $workflowContent | Should -Not -Match '(?m)^\s+- (?:prereleased|published)\s*$'
        }

        foreach ($reusableWorkflow in @("build-minimal-template.yml", "build-populated-template.yml")) {
            $workflowContent = Get-Content -LiteralPath (Join-Path $script:workflowRoot $reusableWorkflow) -Raw
            $workflowContent | Should -Match "if: \`$\{\{ github\.event_name == 'release' && github\.event\.action == 'released' \}\}"
            $workflowContent | Should -Not -Match "(?m)^\s*if: \`$\{\{ github\.event_name == 'release'\s*\}\}\s*$"
        }
    }

    It "publishes template packages from main and tags while keeping manual publication opt-out" {
        foreach ($case in $script:templateCallerCases) {
            $workflowContent = Get-Content -LiteralPath (Join-Path $script:workflowRoot $case.FileName) -Raw

            $workflowContent | Should -Match '(?ms)^  push:\r?\n    branches:\r?\n      - main\r?\n    tags:\r?\n      - "v\*\.\*\.\*"$'
            $workflowContent | Should -Match '(?ms)^  workflow_dispatch:\r?\n    inputs:\r?\n      publish_package:.*?^        default: true$'
            $workflowContent | Should -Match ([regex]::Escape(
                "publish_package: `${{ github.event_name != 'pull_request' && (github.event_name != 'workflow_dispatch' || inputs.publish_package == true) }}"
            ))
        }
    }
}
