# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Describe "Assert-TemplateWorkflowInputs" {
    BeforeAll {
        $script:templatesRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $script:templatesRoot "../docker-compose"))
        $script:validator = Join-Path $script:templatesRoot "Assert-TemplateWorkflowInputs.ps1"
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

    It "makes every MSSQL pull-request validation leg explicitly non-publishing" {
        $workflowPath = Join-Path $script:templatesRoot "../../.github/workflows/on-mssql-template-pullrequest.yml"
        $workflowContent = Get-Content -LiteralPath $workflowPath -Raw
        @([regex]::Matches($workflowContent, '(?m)^      publish_package: false$')).Count |
            Should -Be 2 -Because "both Minimal and Populated reusable-workflow calls must opt out explicitly"
    }
}
