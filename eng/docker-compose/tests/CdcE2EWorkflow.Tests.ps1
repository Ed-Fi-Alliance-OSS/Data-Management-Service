# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

BeforeAll {
    $script:composeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    Import-Module (Join-Path $script:composeRoot 'e2e-cdc.psm1') -Force
}

Describe 'Shared CDC E2E setup handoff' {
    BeforeEach {
        $script:arguments = @{
            EnvironmentFile = (Join-Path $TestDrive '.env.e2e')
            OriginalEnvironmentFile = (Join-Path $TestDrive '.env.original')
            DatabaseName = 'primary_e2e'; SnapshotDatabaseName = 'snapshot_e2e'
            CdcSettingsPath = (Join-Path $TestDrive 'settings.json')
            CdcBindingStatePath = (Join-Path $TestDrive 'custom-state')
            SkipDockerBuild = $true; UsePrebuiltTools = $true; Configuration = 'Release'
        }
        Mock ReadValuesFromEnvFile -ModuleName e2e-cdc { return @{ E2E_DATABASE_NAME = 'primary_e2e'; E2E_SNAPSHOT_DATABASE_NAME = 'snapshot_e2e' } }
        Mock Assert-E2EDatabaseIsDedicated -ModuleName e2e-cdc {}
        Mock Assert-E2ECdcWorkspaceAvailable -ModuleName e2e-cdc {}
        Mock Read-BootstrapCdcSettings -ModuleName e2e-cdc { return @{ ConfigurationServiceSettings = @{ BaseUrl = 'http://localhost:8081' } } }
        Mock Assert-BootstrapCdcOfflineOwnership -ModuleName e2e-cdc {}
        Mock Invoke-E2ECdcSnapshotPreparation -ModuleName e2e-cdc {}
        Mock Test-CdcDeployment -ModuleName e2e-cdc { return $false }
        Mock Invoke-CdcDeploymentLifecycle -ModuleName e2e-cdc {}
        Mock Set-Content -ModuleName e2e-cdc {}
        Mock Invoke-BootstrapWrapper -ModuleName e2e-cdc {
            & $BeforeCdcAdmission '/effective/.env'
        }
    }

    It 'forwards exact primary, state, settings and snapshot for <Provider>, published=<Published>' -ForEach @(
        @{ Provider = 'postgresql'; Published = $false }, @{ Provider = 'mssql'; Published = $false },
        @{ Provider = 'postgresql'; Published = $true }, @{ Provider = 'mssql'; Published = $true }
    ) {
        Invoke-E2ECdcSetup @script:arguments -DatabaseEngine $Provider -UsePublishedImage:$Published
        $expectedScript = if ($Published) { 'start-published-dms.ps1' } else { 'start-local-dms.ps1' }
        Should -Invoke Invoke-BootstrapWrapper -ModuleName e2e-cdc -Times 1 -Exactly -ParameterFilter {
            $StartScriptName -eq $expectedScript -and $DatabaseEngine -eq $Provider -and
            $EnableKafkaCdc -and $SeparateConfigDatabase -and $UseEnvironmentFileSchemaSettings -and
            -not $RebuildLocalImages -and $DataStoreDatabaseName -eq 'primary_e2e' -and
            $CdcSettingsPath -eq $script:arguments.CdcSettingsPath -and
            $CdcBindingStatePath -eq $script:arguments.CdcBindingStatePath -and
            $OriginalEnvironmentFile -eq $script:arguments.OriginalEnvironmentFile
        }
        Should -Invoke Invoke-E2ECdcSnapshotPreparation -ModuleName e2e-cdc -Times 1 -Exactly -ParameterFilter {
            $EnvironmentFile -eq '/effective/.env' -and $DatabaseName -eq 'snapshot_e2e' -and
            $DatabaseEngine -eq $Provider -and $Configuration -eq 'Release' -and $UsePrebuiltTools
        }
    }

    It 'rejects incorrect <Property> before bootstrap' -ForEach @(
        @{ Property = 'DatabaseName' }, @{ Property = 'SnapshotDatabaseName' }
    ) {
        $script:arguments[$Property] = 'different_e2e'
        { Invoke-E2ECdcSetup @script:arguments } | Should -Throw '*actual, distinct*'
        Should -Invoke Invoke-BootstrapWrapper -ModuleName e2e-cdc -Times 0
    }

    It 'rejects retained workspace before bootstrap or snapshot reset' {
        Mock Assert-E2ECdcWorkspaceAvailable -ModuleName e2e-cdc { throw 'Retained CDC workspace' }
        { Invoke-E2ECdcSetup @script:arguments } | Should -Throw '*Retained*'
        Should -Invoke Invoke-BootstrapWrapper -ModuleName e2e-cdc -Times 0
        Should -Invoke Invoke-E2ECdcSnapshotPreparation -ModuleName e2e-cdc -Times 0
    }

    It 'preserves cancellation and attempts governed stop on <Cancelled>' -ForEach @(
        @{ Cancelled = $true }, @{ Cancelled = $false }
    ) {
        if ($Cancelled) {
            Mock Invoke-BootstrapWrapper -ModuleName e2e-cdc { throw [OperationCanceledException]::new('private-secret') }
        }
        else { Mock Invoke-BootstrapWrapper -ModuleName e2e-cdc { throw 'private-secret' } }
        Mock Test-CdcDeployment -ModuleName e2e-cdc { return $true }
        $failure = $null
        try { Invoke-E2ECdcSetup @script:arguments } catch { $failure = $_ }
        $failure | Should -Not -BeNullOrEmpty
        $failure.Exception.Message | Should -Not -Match 'private-secret'
        ($failure.Exception -is [OperationCanceledException]) | Should -Be $Cancelled
        Should -Invoke Invoke-CdcDeploymentLifecycle -ModuleName e2e-cdc -Times 1 -Exactly -ParameterFilter {
            $Project -eq 'dms-local' -and $Parameters.d -and -not $Parameters.v -and
            $Parameters.EnvironmentFile -eq $script:arguments.OriginalEnvironmentFile
        }
        Should -Invoke Set-Content -ModuleName e2e-cdc -Times 1 -Exactly -ParameterFilter {
            ($Value -join '') -notmatch 'private-secret|ConnectionString|Password|settings.json' -and
            ($Value -join '') -match 'Stopped'
        }
    }

    It 'retains controller classifications without raw failure data' {
        Mock Invoke-BootstrapWrapper -ModuleName e2e-cdc {
            $setupError = [InvalidOperationException]::new('private-secret')
            $setupError.Data['CdcFailureCodes'] = @('WriterPublication/ValidationFailed', 'private-secret/Timeout')
            throw $setupError
        }
        { Invoke-E2ECdcSetup @script:arguments } | Should -Throw '*tests were not launched*'
        Should -Invoke Set-Content -ModuleName e2e-cdc -Times 1 -Exactly -ParameterFilter {
            ($Value -join '') -match 'WriterPublication/ValidationFailed' -and ($Value -join '') -notmatch 'private-secret'
        }
    }

    It 'records incomplete governed cleanup without hiding the setup rejection' {
        Mock Invoke-BootstrapWrapper -ModuleName e2e-cdc { throw 'private-secret' }
        Mock Test-CdcDeployment -ModuleName e2e-cdc { return $true }
        Mock Invoke-CdcDeploymentLifecycle -ModuleName e2e-cdc { throw 'cleanup-secret' }
        { Invoke-E2ECdcSetup @script:arguments } | Should -Throw '*tests were not launched*'
        Should -Invoke Set-Content -ModuleName e2e-cdc -Times 1 -Exactly -ParameterFilter {
            ($Value -join '') -match 'RetainedForReconciliation' -and ($Value -join '') -notmatch 'secret'
        }
    }
}

Describe 'Root CDC E2ETest launch ordering' {
    BeforeAll {
        $buildPath = Join-Path $script:composeRoot '../../build-dms.ps1'
        $ast = [Management.Automation.Language.Parser]::ParseFile($buildPath, [ref]$null, [ref]$null)
        foreach ($name in @('E2ETests', 'Invoke-TestExecution')) {
            $node = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
            . ([scriptblock]::Create($node.Extent.Text))
        }
        function Invoke-Step { param([scriptblock]$Action) & $Action }
        function Get-E2ETestEnvironmentContext { return $script:context }
        function RunE2E {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Production-compatible boundary stub; Pester verifies the arguments.')]
            param($TestFilter, $E2ETestSettings)
        }
    }
    BeforeEach {
        $script:EnvironmentFile = '/selected/.env'
        $script:Configuration = 'Release'
        $script:UsePrebuiltOutput = $true
        $script:context = [pscustomobject]@{
            EnvironmentFile = '/effective/.env'; OriginalEnvironmentFile = '/selected/.env'; DatabaseEngine = 'mssql'
            DataStoreDatabaseName = 'primary_e2e'; SnapshotDatabaseName = 'snapshot_e2e'
        }
        Mock Import-Module {}
        Mock Invoke-E2ECdcSetup {}
        Mock RunE2E {}
    }
    It 'passes explicit CDC settings through the command dispatcher before tests' {
        Invoke-TestExecution E2ETests -EnableKafkaCdc -CdcSettingsPath '/selected/settings.json' `
            -CdcBindingStatePath '/selected/state' -SkipDockerBuild -UsePublishedImage -TestFilter 'Category=smoke'
        Should -Invoke Invoke-E2ECdcSetup -Times 1 -Exactly -ParameterFilter {
            $EnvironmentFile -eq '/effective/.env' -and $OriginalEnvironmentFile -eq '/selected/.env' -and $DatabaseEngine -eq 'mssql' -and
            $DatabaseName -eq 'primary_e2e' -and $SnapshotDatabaseName -eq 'snapshot_e2e' -and
            $CdcSettingsPath -eq '/selected/settings.json' -and $CdcBindingStatePath -eq '/selected/state' -and
            $SkipDockerBuild -and $UsePublishedImage -and $UsePrebuiltTools -and $Configuration -eq 'Release'
        }
        Should -Invoke RunE2E -Times 1 -Exactly -ParameterFilter { $E2ETestSettings -eq $script:context -and $TestFilter -eq 'Category=smoke' }
    }
    It 'does not launch API tests when setup <Failure>' -ForEach @(@{ Failure = 'fails' }, @{ Failure = 'cancels' }) {
        if ($Failure -eq 'cancels') { Mock Invoke-E2ECdcSetup { throw [OperationCanceledException]::new() } }
        else { Mock Invoke-E2ECdcSetup { throw 'Admission failed' } }
        { Invoke-TestExecution E2ETests -EnableKafkaCdc -CdcSettingsPath '/selected/settings.json' } | Should -Throw
        Should -Invoke RunE2E -Times 0
    }
}

Describe 'Managed CDC provider infrastructure selection' {
    It 'selects Agent only for the SQL Server CDC lane in <Script>, <Engine>, CDC=<Enabled>' -ForEach @(
        @{ Script = 'start-local-dms.ps1'; Engine = 'mssql'; Enabled = $true },
        @{ Script = 'start-local-dms.ps1'; Engine = 'postgresql'; Enabled = $true },
        @{ Script = 'start-local-dms.ps1'; Engine = 'mssql'; Enabled = $false },
        @{ Script = 'start-published-dms.ps1'; Engine = 'mssql'; Enabled = $true },
        @{ Script = 'start-published-dms.ps1'; Engine = 'postgresql'; Enabled = $true },
        @{ Script = 'start-published-dms.ps1'; Engine = 'mssql'; Enabled = $false }
    ) {
        $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $script:composeRoot $Script), [ref]$null, [ref]$null)
        $selection = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.IfStatementAst] -and $node.Clauses[0].Item1.Extent.Text -ceq '$CdcKafkaInfrastructure'
        }, $true)
        Set-Variable CdcKafkaInfrastructure $Enabled
        Set-Variable DatabaseEngine $Engine
        Set-Variable InfraOnly $true
        Set-Variable enableKafkaInfrastructure $false
        $files = @()
        . ([scriptblock]::Create($selection.Extent.Text))
        ($files -contains 'mssql-cdc.yml') | Should -Be ($Enabled -and $Engine -eq 'mssql')
        ($files -contains 'kafka-cdc.yml') | Should -Be $Enabled
    }
}
