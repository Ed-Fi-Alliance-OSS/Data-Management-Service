# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseDeclaredVarsMoreThanAssignments', '', Justification = 'Fixture variables supply the dynamic scope of the extracted production E2E setup branch.')]
param()

BeforeAll {
    . (Join-Path $PSScriptRoot 'cdc-runbook-snippets.ps1')
    $script:composeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    Import-Module (Join-Path $script:composeRoot 'e2e-cdc.psm1') -Force
}

AfterAll {
    # Nested imports must not survive into another file's sandboxed module mocks.
    Get-Module -All | Where-Object {
        $_.Path -and $_.Path.StartsWith($script:composeRoot + [IO.Path]::DirectorySeparatorChar)
    } | Remove-Module -Force
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

    It 'CDC-DOC <Id>' -ForEach @(
        @{ Id = 'cdc-pg-e2e-setup'; Provider = 'postgresql'; Stem = 'postgresql'; State = 'state-pg-e2e' },
        @{ Id = 'cdc-sqlserver-e2e-setup'; Provider = 'mssql'; Stem = 'sqlserver'; State = 'state-sqlserver-e2e' }
    ) {
        $invocation = Get-CdcRunbookInvocation $Id $TestDrive
        $parameters = $invocation.Parameters
        $parameters.DatabaseEngine | Should -Be $Provider
        $parameters.EnvironmentFile | Should -Be (Join-Path $TestDrive '.env.e2e')
        $source = [Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $script:composeRoot "../../$($invocation.Path)"), [ref]$null, [ref]$null)
        $branch = $source.Find({ param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
            $n.Extent.Text.StartsWith('if ($EnableKafkaCdc)') }, $true)
        $branch | Should -Not -BeNullOrEmpty
        $resolvedEnvironmentFile = '/effective/.env'
        $baseEnvironmentFile = $parameters.EnvironmentFile
        $e2eDatabaseName = 'primary_e2e'
        $e2eSnapshotDatabaseName = 'snapshot_e2e'
        function Get-DirectSetupTeardownCommand { 'fixture teardown' }
        $dispatch = [scriptblock]::Create($source.ParamBlock.Extent.Text + "`n" + $branch.Extent.Text)
        & $dispatch @parameters
        Should -Invoke Invoke-BootstrapWrapper -ModuleName e2e-cdc -Times 1 -Exactly -ParameterFilter {
            $DatabaseEngine -eq $Provider -and $DataStoreDatabaseName -eq 'primary_e2e' -and
            $EnvironmentFile -eq '/effective/.env' -and
            $CdcSettingsPath -eq (Join-Path $TestDrive ".local/cdc/$Stem-e2e.json") -and
            $CdcBindingStatePath -eq (Join-Path $TestDrive ".local/cdc/$State") -and
            $EnableKafkaCdc -and $SeparateConfigDatabase
        }
        Should -Invoke Invoke-E2ECdcSnapshotPreparation -ModuleName e2e-cdc -Times 1 -Exactly -ParameterFilter {
            $DatabaseEngine -eq $Provider -and $DatabaseName -eq 'snapshot_e2e'
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
            $EnableKafkaCdc -and $SeparateConfigDatabase -and $UseEnvironmentFileSchemaSettings -and $IncludeE2EClaimSets -and
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

    It 'retains provider admission classifications without raw failure data for <Published>' -ForEach @(
        @{ Published = $false }, @{ Published = $true }
    ) {
        Mock Invoke-BootstrapWrapper -ModuleName e2e-cdc {
            $setupError = [InvalidOperationException]::new('private-secret')
            $setupError.Data['CdcFailureCodes'] = @('ProviderSetup/ValidationFailed', 'private-secret/Timeout')
            throw $setupError
        }
        { Invoke-E2ECdcSetup @script:arguments -DatabaseEngine mssql -UsePublishedImage:$Published } | Should -Throw '*tests were not launched*'
        Should -Invoke Set-Content -ModuleName e2e-cdc -Times 1 -Exactly -ParameterFilter {
            ($Value -join '') -match 'ProviderSetup/ValidationFailed' -and ($Value -join '') -notmatch 'private-secret'
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
        # Run the real script-level parameter binding and dispatch, retaining guards and assignments.
        # Imports and function definitions are replaced by the controlled boundaries below so a
        # rejected invocation cannot install tools, build images, provision, start DMS or seed data.
        $statements = $ast.EndBlock.Statements | Where-Object {
            $_ -isnot [Management.Automation.Language.FunctionDefinitionAst] -and
            -not ($_ -is [Management.Automation.Language.PipelineAst] -and
                $_.PipelineElements[0] -is [Management.Automation.Language.CommandAst] -and
                $_.PipelineElements[0].GetCommandName() -eq 'Import-Module')
        }
        $script:buildDispatch = [scriptblock]::Create(
            $ast.ParamBlock.Extent.Text + "`n" + (($statements | ForEach-Object { $_.Extent.Text }) -join "`n")
        )
        function Invoke-Main { param([scriptblock]$MainBlock) & $MainBlock }
        function Invoke-Step { param([scriptblock]$Action) & $Action }
        function Install-NugetCli { return 'nuget' }
        function Invoke-Build {}
        function Start-BootstrapDockerEnvironment {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Side-effect-free boundary stub; Pester verifies dispatch without starting infrastructure.')]
            param()
        }
        function Get-E2ETestEnvironmentContext { return $script:context }
        function RunE2E {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Production-compatible boundary stub; Pester verifies the arguments.')]
            param($TestFilter, $E2ETestSettings, $IdentityProvider)
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
        Mock Install-NugetCli { return 'nuget' }
        Mock Set-Alias {}
        Mock Invoke-Build {}
        Mock Start-BootstrapDockerEnvironment {}
        Mock Invoke-E2ECdcSetup {}
        Mock RunE2E {}
    }
    It 'CDC-DOC <Id>' -ForEach @(
        @{ Id = 'cdc-pg-e2e-build'; Provider = 'postgresql'; Stem = 'postgresql'; State = 'state-pg-e2e' },
        @{ Id = 'cdc-sqlserver-e2e-build'; Provider = 'mssql'; Stem = 'sqlserver'; State = 'state-sqlserver-e2e' }
    ) {
        $invocation = Get-CdcRunbookInvocation $Id $TestDrive
        $parameters = $invocation.Parameters
        $parameters.DatabaseEngine | Should -Be $Provider
        $parameters.EnvironmentFile | Should -Be (Join-Path $TestDrive '.env.e2e')
        $script:context.DatabaseEngine = $Provider
        & $script:buildDispatch @parameters
        Should -Invoke Invoke-E2ECdcSetup -Times 1 -Exactly -ParameterFilter {
            $DatabaseEngine -eq $Provider -and $DatabaseName -eq 'primary_e2e' -and
            $SnapshotDatabaseName -eq 'snapshot_e2e' -and $EnvironmentFile -eq '/effective/.env' -and
            $CdcSettingsPath -eq (Join-Path $TestDrive ".local/cdc/$Stem-e2e.json") -and
            $CdcBindingStatePath -eq (Join-Path $TestDrive ".local/cdc/$State")
        }
        Should -Invoke RunE2E -Times 1 -Exactly -ParameterFilter {
            $TestFilter -eq 'FullyQualifiedName~Given_CdcE2ESetup' -and $E2ETestSettings -eq $script:context
        }
        Mock Invoke-E2ECdcSetup { throw 'Admission failed' }
        Mock RunE2E {}
        { & $script:buildDispatch @parameters } | Should -Throw
        Should -Invoke RunE2E -Times 1 -Exactly # Only the earlier successful invocation.
    }

    It 'rejects <BuildCommand> with explicitly bound <CdcParameter>=<Value> before build or startup effects' -ForEach @(
        @{ BuildCommand = 'StartEnvironment'; CdcParameter = 'EnableKafkaCdc'; Value = $true },
        @{ BuildCommand = 'StartEnvironment'; CdcParameter = 'EnableKafkaCdc'; Value = $false },
        @{ BuildCommand = 'StartEnvironment'; CdcParameter = 'CdcSettingsPath'; Value = '/selected/settings.json' },
        @{ BuildCommand = 'StartEnvironment'; CdcParameter = 'CdcBindingStatePath'; Value = '/selected/state' },
        @{ BuildCommand = 'Build'; CdcParameter = 'CdcSettingsPath'; Value = '' },
        @{ BuildCommand = 'Build'; CdcParameter = 'CdcBindingStatePath'; Value = '/selected/state' }
    ) {
        $arguments = @{ Command = $BuildCommand; IsLocalBuild = $true; LoadSeedData = $true }
        $arguments[$CdcParameter] = $Value
        $failure = $null
        try { & $script:buildDispatch @arguments } catch { $failure = $_ }

        Should -Invoke Install-NugetCli -Times 0
        Should -Invoke Invoke-Build -Times 0
        Should -Invoke Start-BootstrapDockerEnvironment -Times 0
        Should -Invoke Invoke-E2ECdcSetup -Times 0
        Should -Invoke RunE2E -Times 0
        $failure | Should -Not -BeNullOrEmpty
        $failure.Exception.Message | Should -Match $BuildCommand
        $failure.Exception.Message | Should -Match $CdcParameter
        $failure.Exception.Message | Should -Match 'E2ETest'
        $failure.Exception.Message | Should -Match 'bootstrap-local-dms.ps1|bootstrap-published-dms.ps1'
    }
    It 'preserves ordinary <BuildCommand> dispatch without CDC inputs' -ForEach @(
        @{ BuildCommand = 'StartEnvironment'; Boundary = 'Start-BootstrapDockerEnvironment' },
        @{ BuildCommand = 'Build'; Boundary = 'Invoke-Build' }
    ) {
        & $script:buildDispatch -Command $BuildCommand
        Should -Invoke $Boundary -Times 1 -Exactly
    }
    It 'retains E2ETest rejection of <CdcParameter> without opt-in' -ForEach @(
        @{ CdcParameter = 'CdcSettingsPath' }, @{ CdcParameter = 'CdcBindingStatePath' }
    ) {
        $arguments = @{ Command = 'E2ETest' }
        $arguments[$CdcParameter] = '/selected/input'
        { & $script:buildDispatch @arguments } | Should -Throw '*require -EnableKafkaCdc*'
        Should -Invoke Invoke-E2ECdcSetup -Times 0
        Should -Invoke RunE2E -Times 0
        Should -Invoke Start-BootstrapDockerEnvironment -Times 0
    }
    It 'passes explicit CDC settings through the command dispatcher before tests' {
        & $script:buildDispatch -Command E2ETest -EnableKafkaCdc -CdcSettingsPath '/selected/settings.json' `
            -CdcBindingStatePath '/selected/state' -SkipDockerBuild -UsePublishedImage -TestFilter 'Category=smoke' `
            -Configuration Release -UsePrebuiltOutput -IdentityProvider keycloak
        Should -Invoke Invoke-E2ECdcSetup -Times 1 -Exactly -ParameterFilter {
            $EnvironmentFile -eq '/effective/.env' -and $OriginalEnvironmentFile -eq '/selected/.env' -and $DatabaseEngine -eq 'mssql' -and
            $DatabaseName -eq 'primary_e2e' -and $SnapshotDatabaseName -eq 'snapshot_e2e' -and
            $CdcSettingsPath -eq '/selected/settings.json' -and $CdcBindingStatePath -eq '/selected/state' -and
            $SkipDockerBuild -and $UsePublishedImage -and $UsePrebuiltTools -and $Configuration -eq 'Release'
        }
        Should -Invoke RunE2E -Times 1 -Exactly -ParameterFilter { $E2ETestSettings -eq $script:context -and $TestFilter -eq 'Category=smoke' -and $IdentityProvider -eq 'keycloak' }
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

Describe 'Ordinary E2E workspace guard after CDC runtime cleanup' {
    BeforeAll {
        $script:guardRoot = Join-Path $TestDrive 'guard-compose'
        New-Item -ItemType Directory $script:guardRoot | Out-Null
        Copy-Item (Join-Path $script:composeRoot '*.psm1') $script:guardRoot
        Copy-Item (Join-Path $script:composeRoot '../schema-package-utility.psm1') $TestDrive
        Import-Module (Join-Path $script:guardRoot 'e2e-cdc.psm1') -Force
    }
    AfterAll {
        Get-Module -All | Where-Object { $_.Path -and $_.Path.StartsWith($script:guardRoot + '/') } | Remove-Module -Force
    }
    It 'accepts the released workspace but rejects unowned surviving runtime files' {
        { Assert-E2ECdcWorkspaceAvailable } | Should -Not -Throw
        $runtime = Join-Path $script:guardRoot '.bootstrap/cdc-runtime'
        New-Item -ItemType Directory $runtime -Force | Out-Null
        'unrelated' | Set-Content (Join-Path $runtime 'notes.txt')
        { Assert-E2ECdcWorkspaceAvailable } | Should -Throw '*E2E setup cannot reset*'
        Get-Content (Join-Path $runtime 'notes.txt') | Should -Be 'unrelated'
    }
    It 'rejects surviving nested state even after the generated runtime directory is gone' {
        Remove-Item (Join-Path $script:guardRoot '.bootstrap/cdc-runtime') -Recurse -Force
        $state = Join-Path $script:guardRoot '.bootstrap/private-state'
        New-Item -ItemType Directory $state -Force | Out-Null
        'historical' | Set-Content (Join-Path $state 'history.json')
        $inventory = Join-Path $script:guardRoot '.cdc-deployments'
        [IO.Directory]::CreateDirectory($inventory, [IO.UnixFileMode]448) | Out-Null
        $path = Join-Path $inventory 'dms-local.json'
        @{ Version = 1; Project = 'dms-local'; Phase = 'RuntimeCleanup'; Entries = @(@{ StatePath = $state }) } | ConvertTo-Json -Depth 5 | Set-Content $path
        [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]384)
        { Assert-E2ECdcWorkspaceAvailable } | Should -Throw '*surviving protected source-state root*'
        Get-Content (Join-Path $state 'history.json') | Should -Be 'historical'
    }
}

Describe 'Private API CDC attachment contract' {
    BeforeAll {
        # Prior sandbox fixtures import private copies under the same module names.
        Get-Module -All | Where-Object { $_.Name -in @('e2e-cdc', 'bootstrap-cdc', 'cdc-lifecycle') } | Remove-Module -Force
        $script:attachmentRoot = Join-Path $TestDrive 'attachment-compose'
        $null = [IO.Directory]::CreateDirectory($script:attachmentRoot, [IO.UnixFileMode]448)
        Copy-Item (Join-Path $script:composeRoot '*.psm1') $script:attachmentRoot
        Copy-Item (Join-Path $script:composeRoot '*.yml') $script:attachmentRoot
        Copy-Item (Join-Path $script:composeRoot '../schema-package-utility.psm1') $TestDrive
        Import-Module (Join-Path $script:attachmentRoot 'e2e-cdc.psm1') -Force
        Import-Module (Join-Path $script:attachmentRoot 'cdc-lifecycle.psm1') -Force
        function Initialize-AttachmentFixture {
            param([string]$Provider)
            $script:state = Join-Path $script:attachmentRoot 'state'
            $null = [IO.Directory]::CreateDirectory($script:state, [IO.UnixFileMode]448)
            $script:settingsPath = Join-Path $script:attachmentRoot 'retained.settings.json'
            $script:admittedPath = Join-Path $script:attachmentRoot 'retained.dms.json'
            $schema = Join-Path $script:attachmentRoot 'schema.json'
            if (Test-Path $schema) { Remove-Item $schema -Recurse -Force }
            '{}' | Set-Content $schema
            $environmentFile = Join-Path $script:attachmentRoot '.env.selected'
            'DMS_HTTP_PORTS=18080' | Set-Content $environmentFile
            $script:settings = @{
                AppSettings = @{ Datastore = $Provider; ApiSchemaPath = $script:attachmentRoot }
                ConfigurationServiceSettings = @{ BaseUrl = 'http://localhost:18081'; ClientSecret = 'private-sentinel' }
                DataManagement = @{ DocumentCache = @{
                    Targets = @(@{ Tenant = ''; DataStoreId = 42 }, @{ Tenant = ''; DataStoreId = 43 })
                    ReadAcceleration = @{ Enabled = $true }
                } }
                Cdc = @{
                    Provider = $Provider; DeploymentKey = 'deployment'; DataStoreId = '42'; InstanceKey = 'custom'; Generation = 9
                    Schemas = @($schema); SetupConnectionString = 'host-side-private-connection'
                    KafkaBootstrapServers = '127.0.0.1:19092'; ConnectEndpoint = 'http://localhost:18083'
                    WorkerMetricsEndpoint = 'http://localhost:19404/metrics'
                    ProviderConnectionProperties = @{ 'database.hostname' = 'container-database'; 'database.port' = '5432' }
                    Worker = @{ Key = 'custom-worker'; OffsetStorageTopic = 'custom-offsets' }
                    Compose = @{
                        Project = 'dms-local'; EnvironmentFile = $environmentFile
                        File = Join-Path $script:attachmentRoot 'kafka-cdc.yml'
                        BrokerSizeOverrideFile = Join-Path $script:state 'broker-size.json'; DatabaseHostPort = 15432
                    }
                }
            }
            if ($Provider -eq 'mssql') {
                $script:settings.Cdc.ProviderConnectionProperties['database.port'] = '1433'
                $script:settings.Cdc.Compose.DatabaseHostPort = 11433
            }
            if (Test-Path $script:settingsPath) { Remove-Item $script:settingsPath -Force }
            $script:settings | ConvertTo-Json -Depth 30 | Set-Content $script:settingsPath
            $script:admitted = @{ services = @{ dms = @{ environment = @{
                DataManagement__DocumentCache__Targets__0__DataStoreId = '42'
                DataManagement__DocumentCache__Targets__0__Tenant = ''
                DataManagement__DocumentCache__Targets__12__DataStoreId = '43'
                DataManagement__DocumentCache__ReadAcceleration__Enabled = 'true'
                DataManagement__DocumentCache__Projector__PageSize = '7'
                ConfigurationServiceSettings__BaseUrl = 'http://ed-fi-api-config-service:18081'
                ConfigurationServiceSettings__ClientSecret = 'private-$$sentinel'
                AppSettings__Datastore = $Provider
            } } } }
            $script:admitted | ConvertTo-Json -Depth 20 | Set-Content $script:admittedPath
            foreach ($path in @($script:settingsPath, $script:admittedPath)) { [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]384) }
            $inventory = Join-Path $script:attachmentRoot '.cdc-deployments/dms-local.json'
            if (Test-Path $inventory) { Remove-Item $inventory }
            Register-CdcDeploymentHandoff -StatePath $script:state -Handoff @{
                IdentityProvider = 'self-contained'; Settings = $script:settings
                SettingsPath = $script:settingsPath; DmsComposePath = $script:admittedPath
            }
            $script:originalHashes = @{}
            foreach ($path in @($script:settingsPath, $script:admittedPath, $environmentFile, $inventory)) {
                $script:originalHashes[$path] = (Get-FileHash $path).Hash
            }
            $script:httpPath = New-E2ECdcHttpOverride -AdmittedComposePath $script:admittedPath
            $script:effective = Get-Content $script:httpPath -Raw | ConvertFrom-Json -AsHashtable
            $script:effective.services.dms.ports = @(@{ host_ip = '127.0.0.1'; published = '18080'; target = 8080; protocol = 'tcp' })
            $script:effective.services.dms.environment.AppSettings__PathBase = '/api'
        }
        function Assert-AttachmentOriginal {
            foreach ($path in $script:originalHashes.Keys) { (Get-FileHash $path).Hash | Should -BeExactly $script:originalHashes[$path] }
            $deployment = & (Get-Module cdc-lifecycle) { Read-CdcDeployment 'dms-local' }
            $deployment.Entries[0].SettingsPath | Should -BeExactly $script:settingsPath
            $deployment.Phase | Should -BeExactly 'Active'
        }
    }
    AfterAll {
        Get-Module -All | Where-Object { $_.Path -and $_.Path.StartsWith($script:attachmentRoot) } | Remove-Module -Force
    }

    It 'preserves authoritative references and host/container separation for <Provider>' -ForEach @(
        @{ Provider = 'postgresql' }, @{ Provider = 'mssql' }
    ) {
        Initialize-AttachmentFixture $Provider
        $handoffPath = Write-E2ECdcApiHandoff -Project dms-local -StatePath $script:state -HttpComposePath $script:httpPath -EffectiveConfiguration $script:effective
        $handoff = Get-Content $handoffPath -Raw | ConvertFrom-Json -AsHashtable
        @($handoff.Keys | Sort-Object) | Should -Be @('deploymentPath', 'dmsBaseUrl', 'httpComposePath', 'settingsPath', 'statePath', 'version')
        $handoff.version | Should -Be 1
        $handoff.settingsPath | Should -BeExactly $script:settingsPath
        $handoff.statePath | Should -BeExactly $script:state
        $handoff.httpComposePath | Should -BeExactly $script:httpPath
        $handoff.deploymentPath | Should -BeExactly (Join-Path $script:attachmentRoot '.cdc-deployments/dms-local.json')
        $handoff.dmsBaseUrl | Should -BeExactly 'http://127.0.0.1:18080/api'
        $retained = Get-Content $handoff.settingsPath -Raw | ConvertFrom-Json -AsHashtable
        $retained.AppSettings.Datastore | Should -BeExactly $Provider
        $retained.Cdc.Generation | Should -Be 9
        $retained.Cdc.InstanceKey | Should -BeExactly 'custom'
        $retained.Cdc.Schemas | Should -Be $script:settings.Cdc.Schemas
        $retained.Cdc.Worker.Key | Should -BeExactly 'custom-worker'
        $retained.Cdc.KafkaBootstrapServers | Should -BeExactly '127.0.0.1:19092'
        $retained.Cdc.ConnectEndpoint | Should -BeExactly 'http://localhost:18083'
        $retained.Cdc.WorkerMetricsEndpoint | Should -BeExactly 'http://localhost:19404/metrics'
        $retained.Cdc.SetupConnectionString | Should -BeExactly 'host-side-private-connection'
        $retained.Cdc.Compose.DatabaseHostPort | Should -Be $script:settings.Cdc.Compose.DatabaseHostPort
        $retained.Cdc.ProviderConnectionProperties['database.hostname'] | Should -BeExactly 'container-database'
        $retained.ConfigurationServiceSettings.BaseUrl | Should -BeExactly 'http://localhost:18081'
        $retained.DataManagement.DocumentCache.Targets.DataStoreId | Should -Be @(42, 43)
        $environment = $script:effective.services.dms.environment
        @($environment.Keys | Where-Object { $_ -like '*Targets*' }).Count | Should -Be 0
        $environment.DataManagement__DocumentCache__ReadAcceleration__Enabled | Should -BeExactly 'false'
        $environment.DataManagement__DocumentCache__Projector__PageSize | Should -BeExactly '7'
        $environment.ConfigurationServiceSettings__BaseUrl | Should -BeExactly 'http://ed-fi-api-config-service:18081'
        $environment.ConfigurationServiceSettings__ClientSecret | Should -BeExactly 'private-$$sentinel'
        foreach ($path in @($handoffPath, $script:httpPath)) {
            ([int][IO.File]::GetUnixFileMode($path) -band 511) | Should -Be 384
        }
        (Get-Content $handoffPath -Raw) | Should -Not -Match 'private-sentinel|container-database|custom-worker|19092'
        Assert-AttachmentOriginal
    }

    It 'rejects an incomplete handoff for <Fault> without publishing it' -ForEach @(
        @{ Fault = 'missing-settings' }, @{ Fault = 'unreadable-settings' }, @{ Fault = 'changed-settings' }, @{ Fault = 'missing-schema' },
        @{ Fault = 'unreadable-schema' }, @{ Fault = 'missing-state' }, @{ Fault = 'unknown-state' },
        @{ Fault = 'missing-override' }, @{ Fault = 'public-override' }, @{ Fault = 'missing-inventory' },
        @{ Fault = 'inherited-target' }, @{ Fault = 'read-acceleration' }, @{ Fault = 'missing-port' }
    ) {
        Initialize-AttachmentFixture postgresql
        $selectedState = $script:state
        switch ($Fault) {
            'missing-settings' { Remove-Item $script:settingsPath }
            'unreadable-settings' { [IO.File]::SetUnixFileMode($script:settingsPath, [IO.UnixFileMode]0) }
            'changed-settings' {
                $script:settings.Cdc.Generation = 10
                $script:settings | ConvertTo-Json -Depth 30 | Set-Content $script:settingsPath
            }
            'missing-schema' { Remove-Item $script:settings.Cdc.Schemas[0] }
            'unreadable-schema' { Remove-Item $script:settings.Cdc.Schemas[0]; $null = New-Item -ItemType Directory $script:settings.Cdc.Schemas[0] }
            'missing-state' { Remove-Item $script:state -Recurse }
            'unknown-state' { $selectedState = Join-Path $script:attachmentRoot 'other-state' }
            'missing-override' { Remove-Item $script:httpPath }
            'public-override' { [IO.File]::SetUnixFileMode($script:httpPath, [IO.UnixFileMode]420) }
            'missing-inventory' { Remove-Item (Join-Path $script:attachmentRoot '.cdc-deployments/dms-local.json') }
            'inherited-target' { $script:effective.services.dms.environment.DataManagement__DocumentCache__Targets__5__DataStoreId = '99' }
            'read-acceleration' { $script:effective.services.dms.environment.DataManagement__DocumentCache__ReadAcceleration__Enabled = 'true' }
            'missing-port' { $script:effective.services.dms.ports = @() }
        }
        { Write-E2ECdcApiHandoff -Project dms-local -StatePath $selectedState -HttpComposePath $script:httpPath -EffectiveConfiguration $script:effective } | Should -Throw
        Test-Path ($script:httpPath.Replace('.http.json', '.handoff.json')) | Should -BeFalse
        @(Get-ChildItem $script:attachmentRoot -Filter '*.tmp').Count | Should -Be 0
        if ($Fault -in @('inherited-target', 'read-acceleration', 'missing-port', 'missing-override', 'public-override', 'unknown-state')) {
            Assert-AttachmentOriginal
        }
    }

    It 'removes alternate configuration key spellings and rejects surviving aliases' {
        Initialize-AttachmentFixture postgresql
        $script:admitted.services.dms.environment['DataManagement:DocumentCache:Targets:23:DataStoreId'] = '44'
        $script:admitted.services.dms.environment['DataManagement:DocumentCache:ReadAcceleration:Enabled'] = 'true'
        $script:admitted | ConvertTo-Json -Depth 20 | Set-Content $script:admittedPath
        $path = New-E2ECdcHttpOverride -AdmittedComposePath $script:admittedPath
        $configuration = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
        { Assert-E2ECdcHttpConfiguration $configuration } | Should -Not -Throw
        $configuration.services.dms.environment['DataManagement:DocumentCache:ReadAcceleration:Enabled'] = 'true'
        { Assert-E2ECdcHttpConfiguration $configuration } | Should -Throw '*disabled read acceleration*'
    }

    It 'removes a partial temporary write without publishing or changing retained inputs' {
        Initialize-AttachmentFixture postgresql
        Mock Write-BootstrapCdcPrivateJson -ModuleName bootstrap-cdc {
            [IO.File]::WriteAllText($Path, '{')
            throw 'Simulated interrupted private write'
        }
        { Write-E2ECdcApiHandoff -Project dms-local -StatePath $script:state -HttpComposePath $script:httpPath -EffectiveConfiguration $script:effective } | Should -Throw '*interrupted*'
        Test-Path ($script:httpPath.Replace('.http.json', '.handoff.json')) | Should -BeFalse
        @(Get-ChildItem $script:attachmentRoot -Filter '*.tmp').Count | Should -Be 0
        Assert-AttachmentOriginal
    }

    It 'rejects layering the replacement over target-bearing inputs with real Compose merging for <Provider>' -ForEach @(
        @{ Provider = 'postgresql' }, @{ Provider = 'mssql' }
    ) {
        Initialize-AttachmentFixture $Provider
        $base = Join-Path $script:attachmentRoot 'base.json'
        @{ services = @{ dms = @{ image = 'fixture'; ports = @('127.0.0.1:18080:8080') } } } | ConvertTo-Json -Depth 8 | Set-Content $base
        $merged = & docker compose -p cdc-attachment-test -f $base -f $script:httpPath config --format json 2>$null
        $LASTEXITCODE | Should -Be 0
        $configuration = ($merged -join "`n") | ConvertFrom-Json -AsHashtable
        { Assert-E2ECdcHttpConfiguration $configuration } | Should -Not -Throw
        $merged = & docker compose -p cdc-attachment-test -f $base -f $script:admittedPath -f $script:httpPath config --format json 2>$null
        $LASTEXITCODE | Should -Be 0
        $configuration = ($merged -join "`n") | ConvertFrom-Json -AsHashtable
        { Assert-E2ECdcHttpConfiguration $configuration } | Should -Throw '*no projection targets*'
        Assert-AttachmentOriginal
    }
}
