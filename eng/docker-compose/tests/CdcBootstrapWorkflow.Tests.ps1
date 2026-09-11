# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

BeforeAll {
    $script:composeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}

AfterAll {
    # Nested imports must not survive into another file's sandboxed module mocks.
    Get-Module -All | Where-Object {
        $_.Path -and $_.Path.StartsWith($script:composeRoot + [IO.Path]::DirectorySeparatorChar)
    } | Remove-Module -Force
}

Describe 'CDC bootstrap wrapper phase contract' {
    BeforeAll {
        $script:sandbox = Join-Path $TestDrive 'wrapper'
        New-Item -ItemType Directory $script:sandbox | Out-Null
        Copy-Item (Join-Path $script:composeRoot '*.yml') $script:sandbox
        foreach ($name in @('bootstrap-local-dms.ps1', 'bootstrap-published-dms.ps1', 'bootstrap-wrapper.psm1')) {
            Copy-Item (Join-Path $script:composeRoot $name) $script:sandbox
        }
        @'
function Get-CdcBootstrapRetryHandoff { param($Project, $StatePath, $Settings, $DatabaseName)
    $path = Join-Path $PSScriptRoot 'retained.json'
    if (Test-Path $path) { return Get-Content $path -Raw | ConvertFrom-Json -AsHashtable }
}
function Invoke-CdcAdmittedHost { param($Project, $StartScript, $Parameters) & $StartScript @Parameters }
Export-ModuleMember -Function Invoke-CdcAdmittedHost, Get-CdcBootstrapRetryHandoff
'@ | Set-Content (Join-Path $script:sandbox 'cdc-lifecycle.psm1')
        Copy-Item (Join-Path $script:sandbox 'cdc-lifecycle.psm1') (Join-Path $script:sandbox 'cdc-lifecycle-stub.txt')
        'local=test' | Set-Content (Join-Path $script:sandbox '.env')
        @'
function Resolve-DataStandardEnvironmentFile { param($DataStandardVersion, $BaseEnvironmentFile, $DockerComposeRoot, $OverlayPrefix) return $BaseEnvironmentFile }
function Resolve-DatabaseEngineEnvironmentFile { param($DatabaseEngine, $BaseEnvironmentFile, $DockerComposeRoot, $SkipMssqlCmsDatabaseValidation) return $BaseEnvironmentFile }
function Resolve-CmsDatabaseTopologyEnvironmentFile { param($BaseEnvironmentFile, $DatabaseEngine, $SeparateConfigDatabase, $DockerComposeRoot) return $BaseEnvironmentFile }
function Confirm-CmsDatabaseTopologyAgreement { param($EnvironmentFile, $DatabaseEngine) }
function Get-EnvValue { param($EnvValues, $Name) return $EnvValues[$Name] }
function ReadValuesFromEnvFile { param($EnvironmentFile)
    $values = @{}
    foreach ($line in Get-Content $EnvironmentFile) {
        if ($line -match '^([^=]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] }
    }
    return $values
}
Export-ModuleMember -Function *
'@ | Set-Content (Join-Path $script:sandbox 'env-utility.psm1')

        # These stubs are phase boundaries. Actual controller receipt/readiness rules are exercised
        # below and in the CDC controller tests; this suite executes both real entry-point wrappers.
        @'
function Read-BootstrapCdcSettings { param($Path, $DatabaseEngine)
    return @{ Provider = $DatabaseEngine; Cdc = @{ DataStoreId = '42'; DeploymentKey = 'local'; InstanceKey = 'datastore-42'; Generation = 1 }; ConfigurationServiceSettings = @{ BaseUrl = 'http://localhost:8081' } }
}
function Assert-BootstrapCdcOfflineOwnership { param($Project, [switch]$InfrastructureReady, $DatabaseEngine, $CmsPort)
    if (Test-Path (Join-Path $PSScriptRoot 'writer')) { throw 'Running writer' }
}
function New-BootstrapCdcHandoff { param($Settings, $StatePath, $EnvironmentFile, $Project, $IdentityProvider)
    if (Test-Path (Join-Path $PSScriptRoot 'retained.json')) { throw 'Replacement handoff' }
    $Settings.AppSettings = @{ Datastore = $Settings.Provider }
    $Settings.DataManagement = @{ DocumentCache = @{ Targets = @(@{ DataStoreId = 42 }) } }
    $Settings.Cdc.Compose = @{ Project = $Project; EnvironmentFile = $EnvironmentFile; File = (Join-Path $PSScriptRoot 'kafka-cdc.yml'); BrokerSizeOverrideFile = (Join-Path $StatePath 'broker-size.json') }
    $Settings.Cdc.Worker = @{ Key = 'worker'; OffsetStorageTopic = 'shared-offsets' }
    $Settings.Cdc.ConnectEndpoint = 'http://localhost:8083/'
    $Settings.Cdc.WorkerMetricsEndpoint = 'http://localhost:9404/metrics'
    New-Item -ItemType Directory $StatePath -Force | Out-Null
    $settingsPath = Join-Path $StatePath 'settings.json'
    $dmsPath = Join-Path $StatePath 'dms.json'
    $Settings | ConvertTo-Json -Depth 64 | Set-Content $settingsPath
    @{ services = @{ dms = @{ environment = @{ AppSettings__Datastore = 'postgresql' } } } } | ConvertTo-Json -Depth 10 | Set-Content $dmsPath
    foreach ($path in @($settingsPath, $dmsPath)) { [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]384) }
    return [pscustomobject]@{ IdentityProvider = $IdentityProvider; Settings = $Settings; DmsComposePath = $dmsPath; SettingsPath = $settingsPath; EnvironmentFile = $EnvironmentFile; InputSettingsHash = 'fixture-input'; DatabaseNameHash = 'fixture-database' }
}
function Invoke-BootstrapCdcEnable { param($Handoff, $Receipt, $SelectedDataStoreIds, $StatePath)
    Add-Content (Join-Path $PSScriptRoot 'calls') "cdc:$($SelectedDataStoreIds -join ','):$($Receipt.CreationReceipt.Outcome):$StatePath"
    if (Test-Path (Join-Path $PSScriptRoot 'model-kafka')) {
        bootstrap-cdc-controller\Invoke-BootstrapCdcEnable -Handoff $Handoff -Receipt $Receipt -SelectedDataStoreIds $SelectedDataStoreIds -StatePath $StatePath
        return
    }
    $retained = $Handoff | ConvertTo-Json -Depth 64 | ConvertFrom-Json -AsHashtable
    $retained.Receipt = $Receipt
    if (Test-Path (Join-Path $PSScriptRoot 'real-lifecycle')) {
        Import-Module (Join-Path $PSScriptRoot 'cdc-lifecycle.psm1')
        Register-CdcDeploymentHandoff -Handoff $Handoff -StatePath $StatePath -Receipt $Receipt
        return
    }
    if (-not (Test-Path (Join-Path $PSScriptRoot 'retained.json'))) {
        $retained | ConvertTo-Json -Depth 64 | Set-Content (Join-Path $PSScriptRoot 'retained.json')
        New-Item -ItemType Directory (Join-Path $PSScriptRoot '.cdc-deployments') -Force | Out-Null
        foreach ($project in @('dms-local', 'dms-published')) { '{}' | Set-Content (Join-Path $PSScriptRoot ".cdc-deployments/$project.json") }
    }
    if (Test-Path (Join-Path $PSScriptRoot 'cancel')) { throw [OperationCanceledException]::new() }
    if (Test-Path (Join-Path $PSScriptRoot 'fail')) { throw 'CDC unavailable' }
    if ($Receipt.CreationReceipt.Outcome -ne 'Created') { throw 'Reused database' }
}
function Invoke-BootstrapCdcKafkaUI { param($Handoff)
    if (Test-Path (Join-Path $PSScriptRoot 'model-kafka')) {
        bootstrap-cdc-controller\Invoke-BootstrapCdcKafkaUI -Handoff $Handoff
    }
    else { Add-Content (Join-Path $PSScriptRoot 'ui') 'started' }
}
Export-ModuleMember -Function *-BootstrapCdc*
'@ | Set-Content (Join-Path $script:sandbox 'bootstrap-cdc.psm1')
        function docker { }
        function Initialize-KafkaFixture($wrapper) {
            '' | Set-Content (Join-Path $script:sandbox 'model-kafka')
            New-Item -ItemType Directory (Join-Path $script:sandbox 'resources') -Force | Out-Null
            Copy-Item (Join-Path $script:composeRoot 'cdc-lifecycle.psm1') $script:sandbox -Force
            Import-Module (Join-Path $script:sandbox 'cdc-lifecycle.psm1') -Force
            Copy-Item (Join-Path $script:composeRoot 'bootstrap-cdc.psm1') (Join-Path $script:sandbox 'bootstrap-cdc-controller.psm1')
            Import-Module (Join-Path $script:sandbox 'bootstrap-cdc-controller.psm1') -Force
            Mock -ModuleName cdc-lifecycle Invoke-CdcLifecycleDocker {
                if ($Arguments[0] -eq 'volume' -and (Test-Path (Join-Path (Get-Module cdc-lifecycle).ModuleBase 'resources/managed-volume'))) { return 'managed-volume' }
                if ($Arguments[0] -eq 'ps' -and (Test-Path (Join-Path (Get-Module cdc-lifecycle).ModuleBase 'resources/kafka-cdc-worker'))) { return 'kafka-cdc-worker' }
            }
            Mock -ModuleName bootstrap-cdc-controller docker {
                ($args -join ' ') | Should -Match 'up --detach --no-deps kafka-ui$'
                Test-Path (Join-Path (Get-Module cdc-lifecycle).ModuleBase 'resources/kafka-cdc-worker') | Should -BeTrue
                'present' | Set-Content (Join-Path (Get-Module cdc-lifecycle).ModuleBase 'resources/kafka-ui')
                Add-Content (Join-Path (Get-Module cdc-lifecycle).ModuleBase 'effects') 'ui'
                $global:LASTEXITCODE = 0
            }
            $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $script:composeRoot "start-$wrapper-dms.ps1"), [ref]$null, [ref]$null)
            foreach ($selection in @(
                @{ name = 'database-selection'; prefix = 'if ($CdcDatabaseInfrastructure)'; marker = 'CDC database preparation requires' },
                @{ name = 'kafka-selection'; prefix = 'if ($CdcKafkaInfrastructure)'; marker = 'CDC infrastructure startup requires' },
                @{ name = 'kafka-start'; prefix = 'if ($CdcKafkaInfrastructure)'; marker = 'Starting CDC broker' },
                @{ name = 'ui-start'; prefix = 'if ($EnableKafkaUI'; marker = 'Starting Kafka UI' },
                @{ name = "guard-$wrapper"; prefix = 'if (-not (Test-CdcInfrastructureInvocation))'; marker = 'Assert-CdcUnregisteredInfrastructure' }
            )) {
                $node = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.IfStatementAst] }, $true) |
                    Where-Object { $_.Extent.Text.StartsWith($selection.prefix) -and $_.Extent.Text.Contains($selection.marker) } | Select-Object -Last 1
                $node | Should -Not -BeNullOrEmpty
                $body = $node.Extent.Text
                if ($selection.name.StartsWith('guard-')) { $body = "param([switch]`$d, [switch]`$v)`n" + $body }
                $body | Set-Content (Join-Path $script:sandbox "$($selection.name).ps1")
            }
        }
        @'
function Resolve-DmsSchemaTool { return Join-Path $PSScriptRoot 'controller-tool.ps1' }
Export-ModuleMember -Function Resolve-DmsSchemaTool
'@ | Set-Content (Join-Path $script:sandbox 'bootstrap-schema-tool.psm1')
        @'
param()
# Simulated native controller transports. The actual CdcWorkerStartup sequence is covered in .NET.
$inventory = @(Get-ChildItem (Join-Path $PSScriptRoot '.cdc-deployments') -Filter '*.json')
if ($inventory.Count -ne 1) { exit 1 }
$deployment = Get-Content $inventory[0] -Raw | ConvertFrom-Json
if ($deployment.Entries.Count -ne 1 -or $deployment.Entries[0].Bootstrap.Receipt.CreationReceipt.Outcome -ne 'Created') { exit 2 }
Add-Content (Join-Path $PSScriptRoot 'effects') 'inventory'
'present' | Set-Content (Join-Path $PSScriptRoot 'resources/kafka')
'present' | Set-Content (Join-Path $PSScriptRoot 'resources/managed-volume')
Add-Content (Join-Path $PSScriptRoot 'effects') 'broker'
Add-Content (Join-Path $PSScriptRoot 'effects') 'offset-policy'
'present' | Set-Content (Join-Path $PSScriptRoot 'resources/kafka-cdc-worker')
Add-Content (Join-Path $PSScriptRoot 'effects') 'worker'
'{"operation":"enable","succeeded":true,"exitCode":0,"data":{"workflowId":"90c9769b-e70f-4c54-97ec-abbdc2d18879","authorizedAt":"2026-09-08T16:00:00Z"}}'
exit 0
'@ | Set-Content (Join-Path $script:sandbox 'controller-tool.ps1')
        $start = @'
param([switch]$InfraOnly, [switch]$DmsOnly, [switch]$EnableConfig, [string]$IdentityProvider,
    [string]$EnvironmentFile, [string]$DatabaseEngine, [switch]$SeparateConfigDatabase,
    [switch]$EnableKafkaUI, [switch]$CdcKafkaInfrastructure, [switch]$CdcDatabaseInfrastructure, [switch]$SuppressWriterGuidance,
    [switch]$SuppressWrapperContinuationGuidance, [string]$CdcDmsComposeFile,
    [switch]$d, [switch]$v, [switch]$RemoveBootstrap, [string]$CdcBrokerSizeOverrideFile)
if (Test-Path (Join-Path $PSScriptRoot 'model-kafka')) {
    $project = if ($PSCommandPath.Contains('published')) { 'dms-published' } else { 'dms-local' }
    if (-not (Test-CdcDeployment $project)) { Assert-CdcUnregisteredInfrastructure $project }
    if ($InfraOnly) {
        function docker {
            foreach ($service in @('kafka', 'kafka-postgresql-source', 'kafka-ui')) {
                if ($args -contains $service) {
                    'present' | Set-Content (Join-Path $PSScriptRoot "resources/$service")
                    if ($service -ne 'kafka-ui') { 'present' | Set-Content (Join-Path $PSScriptRoot 'resources/managed-volume') }
                }
            }
            $global:LASTEXITCODE = 0
        }
        $files = @()
        $enableKafkaInfrastructure = $EnableKafkaUI -or $CdcKafkaInfrastructure
        . (Join-Path $PSScriptRoot 'database-selection.ps1')
        . (Join-Path $PSScriptRoot 'kafka-selection.ps1')
        Set-Content (Join-Path $PSScriptRoot 'selected-files') -Value ($files -join "`n")
        # Execute the actual start script's service launch blocks against persistent Docker state.
        . (Join-Path $PSScriptRoot 'kafka-start.ps1')
        . (Join-Path $PSScriptRoot 'ui-start.ps1')
    }
    if ($DmsOnly) { Add-Content (Join-Path $PSScriptRoot 'effects') 'writer' }
}
$phase = if ($d) { if ($v) { 'retire' } else { 'stop' } } elseif ($DmsOnly) { 'dms' } else { 'infra' }
Add-Content (Join-Path $PSScriptRoot 'identities') "$phase`:$IdentityProvider"
Add-Content (Join-Path $PSScriptRoot 'calls') "$phase`:$DatabaseEngine`:$EnableKafkaUI`:$CdcKafkaInfrastructure`:$SuppressWriterGuidance`:$CdcDmsComposeFile"
if ($InfraOnly -and -not $SuppressWriterGuidance) { Write-Information 'early writer guidance' -InformationAction Continue }
'@
        foreach ($name in @('start-local-dms.ps1', 'start-published-dms.ps1')) { $start | Set-Content (Join-Path $script:sandbox $name) }
        @'
param($EnvironmentFile, $DatabaseEngine, [switch]$SeparateConfigDatabase, $DataStoreDatabaseName, [switch]$NoDataStore)
Add-Content (Join-Path $PSScriptRoot 'calls') "configure:$DatabaseEngine`:$DataStoreDatabaseName"
if (Test-Path (Join-Path $PSScriptRoot 'configure-failure')) { throw 'Configure failed' }
$id = if (Test-Path (Join-Path $PSScriptRoot 'mismatch')) { 43 } else { 42 }
return [pscustomobject]@{ SelectedDataStoreIds = @($id); HasRouteQualifiedDataStores = $false }
'@ | Set-Content (Join-Path $script:sandbox 'configure-local-data-store.ps1')
        @'
param($EnvironmentFile, $DataStoreId, $DatabaseEngine, [switch]$SeparateConfigDatabase, $CdcBindingStatePath,
    [switch]$PrepareCdcProjectionPrerequisites, [switch]$InitialCdcProvisioning, $DeploymentKey, $InstanceKey, $Generation)
Add-Content (Join-Path $PSScriptRoot 'calls') "provision:$DatabaseEngine`:$PrepareCdcProjectionPrerequisites`:$CdcBindingStatePath`:$InstanceKey`:$InitialCdcProvisioning"
if (Test-Path (Join-Path $PSScriptRoot 'provision-failure')) { throw 'Provision failed' }
$outcome = if (Test-Path (Join-Path $PSScriptRoot 'reuse')) { 'Reused' } else { 'Created' }
if ($CdcBindingStatePath) {
    return @{ WorkflowId = '90c9769b-e70f-4c54-97ec-abbdc2d18879';
        Target = @{ DataStoreId = '42'; DeploymentKey = $DeploymentKey; InstanceKey = $InstanceKey; Generation = $Generation };
        CreationReceipt = @{ Outcome = $outcome } }
}
'@ | Set-Content (Join-Path $script:sandbox 'provision-dms-schema.ps1')
        @'
param($EnvironmentFile, $IdentityProvider, $DataStoreId)
Add-Content (Join-Path $PSScriptRoot 'calls') "seed:$($DataStoreId -join ',')"
'@ | Set-Content (Join-Path $script:sandbox 'load-dms-seed-data.ps1')
    }
    BeforeEach {
        foreach ($name in @('calls', 'identities', 'model-kafka', 'resources', 'effects', 'ui', 'selected-files', 'configure-failure', 'provision-failure', 'real-lifecycle', 'fail', 'cancel', 'writer', 'reuse', 'mismatch', 'retained.json', '.cdc-deployments')) {
            Remove-Item (Join-Path $script:sandbox $name) -Recurse -Force -ErrorAction SilentlyContinue
        }
        Get-Module -All | Where-Object { $_.Path -and $_.Path.StartsWith($script:sandbox + [IO.Path]::DirectorySeparatorChar) } | Remove-Module -Force
        Copy-Item (Join-Path $script:sandbox 'cdc-lifecycle-stub.txt') (Join-Path $script:sandbox 'cdc-lifecycle.psm1') -Force
        'DMS_CONFIG_IDENTITY_PROVIDER=self-contained' | Set-Content (Join-Path $script:sandbox '.env')
        $script:arguments = @{
            EnableKafkaCdc = $true; CdcSettingsPath = 'explicit.json'; CdcBindingStatePath = (Join-Path $TestDrive 'custom-state')
            SeparateConfigDatabase = $true; DataStoreDatabaseName = 'dedicated_cdc'; LoadSeedData = $true; EnableKafkaUI = $true
        }
    }
    AfterAll {
        Get-Module -All | Where-Object { $_.Path -and $_.Path.StartsWith($script:sandbox + [IO.Path]::DirectorySeparatorChar) } | Remove-Module -Force
    }

    It 'leaves no unregistered Kafka after <failure> for <wrapper>/<provider>, UI=<ui>' -ForEach @(
        foreach ($wrapper in @('local', 'published')) {
            foreach ($provider in @('postgresql', 'mssql')) {
                foreach ($ui in @($false, $true)) {
                    foreach ($failure in @('configure', 'provision', 'snapshot', 'cancel')) {
                        @{ wrapper = $wrapper; provider = $provider; ui = $ui; failure = $failure }
                    }
                }
            }
        }
    ) {
        Initialize-KafkaFixture $wrapper
        $script:arguments.EnableKafkaUI = $ui
        if ($failure -in @('configure', 'provision')) {
            '' | Set-Content (Join-Path $script:sandbox "$failure-failure")
            { & (Join-Path $script:sandbox "bootstrap-$wrapper-dms.ps1") @script:arguments -DatabaseEngine $provider } | Should -Throw "*$failure failed*"
        }
        else {
            Import-Module (Join-Path $script:sandbox 'bootstrap-wrapper.psm1') -Force
            $callback = if ($failure -eq 'cancel') { { throw [OperationCanceledException]::new('Snapshot cancelled') } } else { { throw 'Snapshot failed' } }
            { Invoke-BootstrapWrapper -StartScriptName "start-$wrapper-dms.ps1" @script:arguments -DatabaseEngine $provider -BeforeCdcAdmission $callback } | Should -Throw '*Snapshot*'
        }
        @(Get-ChildItem (Join-Path $script:sandbox 'resources')).Count | Should -Be 0
        Test-CdcDeployment "dms-$wrapper" | Should -BeFalse
        @(Get-Content (Join-Path $script:sandbox 'selected-files')) | Should -Not -Contain 'kafka-cdc.yml'
        if ($provider -eq 'mssql') {
            @(Get-Content (Join-Path $script:sandbox 'selected-files')) | Should -Contain 'mssql-cdc.yml'
        }
        # Same surviving Docker state, real start-script entry guard, both startup and teardown.
        foreach ($parameters in @(@{}, @{ d = $true; v = $true })) {
            { & (Join-Path $script:sandbox "guard-$wrapper.ps1") @parameters } | Should -Not -Throw
        }
        @(Get-ChildItem (Join-Path $script:sandbox 'resources')).Count | Should -Be 0
        Test-Path (Join-Path $script:sandbox 'effects') | Should -BeFalse
        # This proves only absence of the Kafka state-loss rejection, not retry/adoption authority.
    }

    It 'registers before Kafka effects and still rejects later inventory loss for <wrapper>/<provider>, UI=<ui>' -ForEach @(
        foreach ($wrapper in @('local', 'published')) {
            foreach ($provider in @('postgresql', 'mssql')) {
                foreach ($ui in @($false, $true)) { @{ wrapper = $wrapper; provider = $provider; ui = $ui } }
            }
        }
    ) {
        Initialize-KafkaFixture $wrapper
        $script:arguments.EnableKafkaUI = $ui
        & (Join-Path $script:sandbox "bootstrap-$wrapper-dms.ps1") @script:arguments -DatabaseEngine $provider
        $expected = @('inventory', 'broker', 'offset-policy', 'worker')
        if ($ui) { $expected += 'ui' }
        $expected += 'writer'
        @(Get-Content (Join-Path $script:sandbox 'effects')) | Should -Be $expected
        Test-CdcDeployment "dms-$wrapper" | Should -BeTrue
        $before = @(Get-ChildItem (Join-Path $script:sandbox 'resources')).Name
        $before | Should -Contain 'managed-volume'
        $before | Should -Contain 'kafka-cdc-worker'
        Remove-Item (Join-Path $script:sandbox ".cdc-deployments/dms-$wrapper.json")
        foreach ($parameters in @(@{}, @{ d = $true; v = $true })) {
            { & (Join-Path $script:sandbox "guard-$wrapper.ps1") @parameters } | Should -Throw '*survives without*'
        }
        @(Get-ChildItem (Join-Path $script:sandbox 'resources')).Name | Should -Be $before
    }

    It 'orders <wrapper>/<provider> through CDC before DMS and seed, including UI' -ForEach @(
        @{ wrapper = 'local'; provider = 'postgresql' }, @{ wrapper = 'local'; provider = 'mssql' },
        @{ wrapper = 'published'; provider = 'postgresql' }, @{ wrapper = 'published'; provider = 'mssql' }
    ) {
        & (Join-Path $script:sandbox "bootstrap-$wrapper-dms.ps1") @script:arguments -DatabaseEngine $provider
        $calls = @(Get-Content (Join-Path $script:sandbox 'calls'))
        $calls.Count | Should -Be 6
        $calls[0] | Should -Be "infra:$provider`:False:False:True:"
        $calls[1] | Should -Be "configure:$provider`:dedicated_cdc"
        $calls[2] | Should -Be "provision:$provider`:True:$($script:arguments.CdcBindingStatePath):datastore-42:True"
        $calls[3] | Should -Be "cdc:42:Created:$($script:arguments.CdcBindingStatePath)"
        $calls[4] | Should -Be "dms:$provider`:False:False:False:$($script:arguments.CdcBindingStatePath)/dms.json"
        $calls[5] | Should -Be 'seed:42'
    }

    It 'resumes <wrapper>/<provider> using the retained handoff without provisioning again' -ForEach @(
        @{ wrapper = 'local'; provider = 'postgresql' }, @{ wrapper = 'local'; provider = 'mssql' },
        @{ wrapper = 'published'; provider = 'postgresql' }, @{ wrapper = 'published'; provider = 'mssql' }
    ) {
        '' | Set-Content (Join-Path $script:sandbox 'fail')
        { & (Join-Path $script:sandbox "bootstrap-$wrapper-dms.ps1") @script:arguments -DatabaseEngine $provider } | Should -Throw '*CDC unavailable*'
        $retained = Get-Content (Join-Path $script:sandbox 'retained.json') -Raw
        Remove-Item (Join-Path $script:sandbox 'fail')
        & (Join-Path $script:sandbox "bootstrap-$wrapper-dms.ps1") @script:arguments -DatabaseEngine $provider
        $calls = @(Get-Content (Join-Path $script:sandbox 'calls'))
        @($calls | Where-Object { $_ -match '^configure:' }).Count | Should -Be 1
        @($calls | Where-Object { $_ -match '^provision:' }).Count | Should -Be 1
        @($calls | Where-Object { $_ -match '^infra:' }).Count | Should -Be 1
        @($calls | Where-Object { $_ -match '^cdc:' }).Count | Should -Be 2
        @($calls | Where-Object { $_ -match '^dms:' }).Count | Should -Be 1
        $calls[-1] | Should -Be 'seed:42'
        (Get-Content (Join-Path $script:sandbox 'retained.json') -Raw) | Should -Be $retained
    }

    It 'retains the bootstrap override across <wrapper> stop, startup and teardown (<identity>)' -ForEach @(
        @{ wrapper = 'local'; identity = 'keycloak'; environment = 'self-contained' },
        @{ wrapper = 'published'; identity = 'keycloak'; environment = 'self-contained' },
        @{ wrapper = 'local'; identity = 'self-contained'; environment = 'keycloak' },
        @{ wrapper = 'published'; identity = 'self-contained'; environment = 'keycloak' }
    ) {
        Copy-Item (Join-Path $script:composeRoot 'cdc-lifecycle.psm1') (Join-Path $script:sandbox 'cdc-lifecycle.psm1') -Force
        '' | Set-Content (Join-Path $script:sandbox 'real-lifecycle')
        "DMS_CONFIG_IDENTITY_PROVIDER=$environment" | Set-Content (Join-Path $script:sandbox '.env')
        $entryPoint = Join-Path $script:sandbox "bootstrap-$wrapper-dms.ps1"
        & $entryPoint @script:arguments -IdentityProvider $identity
        $inventoryPath = Join-Path $script:sandbox ".cdc-deployments/dms-$wrapper.json"
        $deployment = Get-Content $inventoryPath -Raw | ConvertFrom-Json -AsHashtable
        $deployment.IdentityProvider | Should -Be $identity
        $deployment.Entries[0].IdentityProvider | Should -Be $identity
        Mock -ModuleName cdc-lifecycle Invoke-CdcLifecycleCommand {
            $Entry.ConnectorName = 'connector-42'
        }
        Mock -ModuleName cdc-lifecycle Invoke-CdcLifecycleRest {
            if ($Path -eq 'connectors') { return @('connector-42') }
            return @{ name = 'connector-42'; connector = @{ state = 'STOPPED' }; tasks = @() }
        }
        Mock -ModuleName cdc-lifecycle Invoke-CdcLifecycleDocker { return @() }
        & $entryPoint -d
        $before = Get-Content (Join-Path $script:sandbox 'identities') -Raw
        { & $entryPoint -IdentityProvider $environment } | Should -Throw '*IdentityProvider conflicts*'
        Should -Invoke -ModuleName cdc-lifecycle Invoke-CdcLifecycleCommand -Times 0 -Exactly -ParameterFilter { $Operation -ne 'stop' }
        (Get-Content (Join-Path $script:sandbox 'identities') -Raw) | Should -Be $before
        & $entryPoint
        # Matching explicit selection is also accepted for later lifecycle invocations.
        & $entryPoint -d -IdentityProvider $identity
        & $entryPoint -IdentityProvider $identity
        Mock -ModuleName cdc-lifecycle Invoke-CdcLifecycleRest { return @() }
        # Avoid deleting the isolated schema workspace after the actual inventory removal.
        Invoke-CdcDeploymentLifecycle -Project "dms-$wrapper" -StartScript (Join-Path $script:sandbox "start-$wrapper-dms.ps1") -Parameters @{ d = $true; v = $true }
        @(Get-Content (Join-Path $script:sandbox 'identities')) | Should -Be @(
            "infra:$identity", "dms:$identity", "stop:$identity", "infra:$identity", "dms:$identity",
            "stop:$identity", "infra:$identity", "dms:$identity", "retire:$identity"
        )
        Test-Path $inventoryPath | Should -BeFalse
    }

    It 'uses the retained override on initial <wrapper> retry and rejects a conflict' -ForEach @(
        @{ wrapper = 'local' }, @{ wrapper = 'published' }
    ) {
        $entryPoint = Join-Path $script:sandbox "bootstrap-$wrapper-dms.ps1"
        '' | Set-Content (Join-Path $script:sandbox 'fail')
        { & $entryPoint @script:arguments -IdentityProvider keycloak } | Should -Throw '*CDC unavailable*'
        (Get-Content (Join-Path $script:sandbox 'retained.json') -Raw | ConvertFrom-Json).IdentityProvider | Should -Be 'keycloak'
        $before = Get-Content (Join-Path $script:sandbox 'calls') -Raw
        { & $entryPoint @script:arguments -IdentityProvider self-contained } | Should -Throw '*IdentityProvider conflicts*'
        (Get-Content (Join-Path $script:sandbox 'calls') -Raw) | Should -Be $before
        Remove-Item (Join-Path $script:sandbox 'fail')
        & $entryPoint @script:arguments
        @(Get-Content (Join-Path $script:sandbox 'identities')) | Should -Be @('infra:keycloak', 'dms:keycloak')
    }

    It 'suppresses DMS, seed and guidance on <failure>' -ForEach @(@{ failure = 'fail' }, @{ failure = 'cancel' }, @{ failure = 'reuse' }) {
        '' | Set-Content (Join-Path $script:sandbox $failure)
        $information = @()
        { & (Join-Path $script:sandbox 'bootstrap-local-dms.ps1') @script:arguments -InformationVariable +information } | Should -Throw
        $calls = @(Get-Content (Join-Path $script:sandbox 'calls'))
        @($calls | Where-Object { $_ -match '^(dms|seed):' }).Count | Should -Be 0
        ($information -join ' ') | Should -Not -Match 'early writer|authorized writer|Launch DMS|Launch IDE'
    }

    It 'runs E2E snapshot preparation between managed provisioning and CDC admission for <provider>' -ForEach @(
        @{ provider = 'postgresql' }, @{ provider = 'mssql' }
    ) {
        Import-Module (Join-Path $script:sandbox 'bootstrap-wrapper.psm1') -Force
        $callsPath = Join-Path $script:sandbox 'calls'
        $callback = { param($effectiveEnvironment) Add-Content $callsPath "snapshot:$effectiveEnvironment" }.GetNewClosure()
        Invoke-BootstrapWrapper -StartScriptName 'start-local-dms.ps1' @script:arguments `
            -DatabaseEngine $provider -UseEnvironmentFileSchemaSettings -BeforeCdcAdmission $callback
        $calls = @(Get-Content $callsPath)
        $calls.Count | Should -Be 7
        $calls[2] | Should -Match '^provision:'
        $calls[3] | Should -Match '^snapshot:'
        $calls[4] | Should -Match '^cdc:'
        $calls[5] | Should -Match '^dms:'
        $calls[6] | Should -Match '^seed:'
    }

    It 'rejects snapshot failure before CDC registration, DMS, or seed' {
        Import-Module (Join-Path $script:sandbox 'bootstrap-wrapper.psm1') -Force
        { Invoke-BootstrapWrapper -StartScriptName 'start-local-dms.ps1' @script:arguments `
            -UseEnvironmentFileSchemaSettings -BeforeCdcAdmission { throw 'Snapshot failed' } } | Should -Throw '*Snapshot failed*'
        $calls = @(Get-Content (Join-Path $script:sandbox 'calls'))
        $calls.Count | Should -Be 3
        @($calls | Where-Object { $_ -match '^(cdc|dms|seed):' }).Count | Should -Be 0
    }

    It 'rejects selected target mismatch before provisioning' {
        '' | Set-Content (Join-Path $script:sandbox 'mismatch')
        { & (Join-Path $script:sandbox 'bootstrap-local-dms.ps1') @script:arguments } | Should -Throw '*different target*'
        @(Get-Content (Join-Path $script:sandbox 'calls')).Count | Should -Be 2
    }

    It 'leaves InfraOnly offline after authorization' {
        $script:arguments.Remove('LoadSeedData')
        & (Join-Path $script:sandbox 'bootstrap-local-dms.ps1') @script:arguments -InfraOnly
        @(Get-Content (Join-Path $script:sandbox 'calls')).Count | Should -Be 4
    }

    It 'rejects <flag> before infrastructure' -ForEach @(
        @{ flag = 'NoDataStore'; value = $true }, @{ flag = 'SchoolYearRange'; value = '2025-2026' },
        @{ flag = 'DmsBaseUrl'; value = 'http://localhost:8080' }, @{ flag = 'SeparateConfigDatabase'; value = $false },
        @{ flag = 'CdcSettingsPath'; value = '' }, @{ flag = 'DataStoreDatabaseName'; value = '' }
    ) {
        $script:arguments[$flag] = $value
        { & (Join-Path $script:sandbox 'bootstrap-local-dms.ps1') @script:arguments } | Should -Throw '*CDC bootstrap requires*'
        Test-Path (Join-Path $script:sandbox 'calls') | Should -BeFalse
    }

    It 'rejects running writers before infrastructure' {
        '' | Set-Content (Join-Path $script:sandbox 'writer')
        { & (Join-Path $script:sandbox 'bootstrap-published-dms.ps1') @script:arguments } | Should -Throw '*Running writer*'
        Test-Path (Join-Path $script:sandbox 'calls') | Should -BeFalse
    }

    It 'does not route explicit CDC teardown through ungoverned volume removal' {
        { & (Join-Path $script:sandbox 'bootstrap-local-dms.ps1') -d -v -EnableKafkaCdc } | Should -Throw '*original retained deployment inventory*'
        Test-Path (Join-Path $script:sandbox 'calls') | Should -BeFalse
    }

    It 'keeps non-CDC managed provisioning source-history-only before writer handoff for <wrapper>' -ForEach @(
        @{ wrapper = 'local' }, @{ wrapper = 'published' }
    ) {
        & (Join-Path $script:sandbox "bootstrap-$wrapper-dms.ps1") -CdcBindingStatePath $script:arguments.CdcBindingStatePath
        $calls = @(Get-Content (Join-Path $script:sandbox 'calls'))
        $calls.Count | Should -Be 4
        $calls[2] | Should -Be "provision:postgresql:False:$($script:arguments.CdcBindingStatePath)::False"
        $calls[3] | Should -Match '^dms:'
    }

    It 'retains ordinary UI-only phase behavior' {
        & (Join-Path $script:sandbox 'bootstrap-published-dms.ps1') -EnableKafkaUI -LoadSeedData
        $calls = @(Get-Content (Join-Path $script:sandbox 'calls'))
        $calls.Count | Should -Be 5
        $calls[0] | Should -Be 'infra:postgresql:True:False:False:'
        $calls[3] | Should -Be 'dms:postgresql:True:False:False:'
    }
}

Describe 'CDC local ownership inspection' {
    BeforeAll { Import-Module (Join-Path $script:composeRoot 'bootstrap-cdc.psm1') -Force }
    BeforeEach {
        Mock -ModuleName bootstrap-cdc Get-BootstrapCdcHostProcessCommand { @('dotnet unrelated.dll') }
        Mock -ModuleName bootstrap-cdc Invoke-BootstrapCdcDockerInspection { @() }
    }
    It 'accepts an offline local host' {
        { Assert-BootstrapCdcOfflineOwnership -Project 'dms-local' } | Should -Not -Throw
    }
    It 'rejects a running IDE process without probing arbitrary health URLs' {
        Mock -ModuleName bootstrap-cdc Get-BootstrapCdcHostProcessCommand { @('dotnet /app/EdFi.DataManagementService.Frontend.AspNetCore.dll') }
        { Assert-BootstrapCdcOfflineOwnership -Project 'dms-local' } | Should -Throw '*no running DMS or IDE*'
        Should -Invoke -ModuleName bootstrap-cdc Invoke-BootstrapCdcDockerInspection -Times 0
    }
    It 'rejects unavailable process inventory' {
        Mock -ModuleName bootstrap-cdc Get-BootstrapCdcHostProcessCommand { throw 'private-data' }
        { Assert-BootstrapCdcOfflineOwnership -Project 'dms-local' } | Should -Throw '*exclusively owned local*'
    }
    It 'rejects an application replica sharing the CMS network' {
        Mock -ModuleName bootstrap-cdc Invoke-BootstrapCdcDockerInspection {
            if ($Arguments[0] -eq 'ps') { return 'replica' }
            return '{"name":"/replica","project":"elsewhere","service":"app","directory":"/other","ports":{},"networks":{"dms":{}},"command":[],"entrypoint":[]}'
        }
        { Assert-BootstrapCdcOfflineOwnership -Project 'dms-local' } | Should -Throw '*exclusively owned local*'
    }
    It 'requires live CMS and database evidence after startup' {
        { Assert-BootstrapCdcOfflineOwnership -Project 'dms-local' -InfrastructureReady -CmsPort 8081 -DatabaseEngine postgresql } | Should -Throw '*exclusively owned local*'
    }
    It 'accepts scoped loopback infrastructure for <provider>' -ForEach @(
        @{ provider = 'postgresql'; database = '/dms-postgresql' }, @{ provider = 'mssql'; database = '/dms-mssql' }
    ) {
        $script:databaseContainerName = $database
        Mock -ModuleName bootstrap-cdc Get-BootstrapCdcHostProcessCommand { @('dotnet EdFi.DmsConfigurationService.Frontend.AspNetCore.dll') }
        Mock -ModuleName bootstrap-cdc Invoke-BootstrapCdcDockerInspection {
            if ($Arguments[0] -eq 'ps') { return @('cms', 'db') }
            foreach ($entry in @(@{ name = '/ed-fi-api-config-service'; service = 'config'; port = '8081' }, @{ name = $script:databaseContainerName; service = 'db'; port = '15432' })) {
                @{ name = $entry.name; service = $entry.service; project = 'dms-local'; directory = $script:composeRoot;
                    ports = @{ '1234/tcp' = @(@{ HostIp = '127.0.0.1'; HostPort = $entry.port }) }; networks = @{ dms = @{} }; command = @(); entrypoint = @()
                } | ConvertTo-Json -Depth 10 -Compress
            }
        }
        { Assert-BootstrapCdcOfflineOwnership -Project 'dms-local' -InfrastructureReady -CmsPort 8081 -DatabaseEngine $provider } | Should -Not -Throw
    }
    It 'rejects remote CMS access even for the selected project' {
        Mock -ModuleName bootstrap-cdc Invoke-BootstrapCdcDockerInspection {
            if ($Arguments[0] -eq 'ps') { return 'cms' }
            @{ name = '/ed-fi-api-config-service'; service = 'config'; project = 'dms-local'; directory = $script:composeRoot;
                ports = @{ '8081/tcp' = @(@{ HostIp = '0.0.0.0'; HostPort = '8081' }) }; networks = @{ dms = @{} }; command = @(); entrypoint = @()
            } | ConvertTo-Json -Depth 10 -Compress
        }
        { Assert-BootstrapCdcOfflineOwnership -Project 'dms-local' } | Should -Throw '*loopback CMS/database ports*'
    }
}

Describe 'CDC controller result handoff' {
    BeforeAll {
        Import-Module (Join-Path $script:composeRoot 'bootstrap-cdc.psm1') -Force
        Import-Module (Join-Path $script:composeRoot 'cdc-lifecycle.psm1') -Force
        $script:workflow = '90c9769b-e70f-4c54-97ec-abbdc2d18879'
    }
    BeforeEach {
        Mock -ModuleName bootstrap-cdc Register-CdcDeploymentHandoff {}
        $script:tool = Join-Path $TestDrive 'cdc-tool.ps1'
        @'
param()
'{"operation":"enable","succeeded":true,"exitCode":0,"data":{"workflowId":"90c9769b-e70f-4c54-97ec-abbdc2d18879","authorizedAt":"2026-09-08T16:00:00Z"}}'
exit 0
'@ | Set-Content $script:tool
        $script:enableArgs = @{
            Handoff = @{ Settings = @{ Cdc = @{ DataStoreId = '42'; DeploymentKey = 'local'; InstanceKey = 'datastore-42'; Generation = 1 } }; SettingsPath = 'settings.json' }
            Receipt = @{ WorkflowId = $script:workflow; Target = @{ DataStoreId = '42'; DeploymentKey = 'local'; InstanceKey = 'datastore-42'; Generation = 1 }; CreationReceipt = @{ Outcome = 'Created' } }
            SelectedDataStoreIds = @(42); StatePath = (Join-Path $TestDrive 'state'); ToolPath = $script:tool
        }
    }
    It 'accepts only the matching workflow publication response' {
        { Invoke-BootstrapCdcEnable @script:enableArgs } | Should -Not -Throw
    }
    It 'rejects reused receipt before invoking the command' {
        $script:enableArgs.Receipt.CreationReceipt.Outcome = 'Reused'
        { Invoke-BootstrapCdcEnable @script:enableArgs } | Should -Throw '*authoritative creation receipt*'
    }
    It 'rejects a successful response for a different workflow' {
        (Get-Content $script:tool -Raw).Replace($script:workflow, [guid]::NewGuid().ToString()) | Set-Content $script:tool
        { Invoke-BootstrapCdcEnable @script:enableArgs } | Should -Throw '*no valid writer publication*'
    }
    It 'rejects success without publication evidence' {
        "'{}'; exit 0" | Set-Content $script:tool
        { Invoke-BootstrapCdcEnable @script:enableArgs } | Should -Throw '*no valid writer publication*'
    }
    It 'sanitizes native failure and cancellation output (<code>)' -ForEach @(@{ code = 1 }, @{ code = 130 }) {
        "'private-secret'; exit $code" | Set-Content $script:tool
        { Invoke-BootstrapCdcEnable @script:enableArgs } | Should -Throw "*exit $code*"
    }
    It 'retains only allow-listed controller failure codes' {
        @'
'{"diagnostics":[{"component":"WriterPublication","failure":"ValidationFailed","message":"private-secret"},{"component":"private-secret","failure":"Timeout"}]}'
exit 1
'@ | Set-Content $script:tool
        try { Invoke-BootstrapCdcEnable @script:enableArgs; throw 'Expected failure' }
        catch {
            $_.Exception.Data['CdcFailureCodes'] | Should -Be @('WriterPublication/ValidationFailed')
            $_.Exception.Message | Should -Not -Match 'private-secret'
        }
    }

}

Describe 'Explicit CDC settings reach the controller and eventual HTTP host' {
    BeforeAll {
        $script:handoffRoot = Join-Path $TestDrive 'handoff'
        New-Item -ItemType Directory $script:handoffRoot | Out-Null
        Copy-Item (Join-Path $script:composeRoot 'bootstrap-cdc.psm1') (Join-Path $script:handoffRoot 'bootstrap-cdc-handoff.psm1')
        @'
function Resolve-BootstrapSchemaWorkspace { return @{ CoreSchemaPath = '/staged/core.json'; ExtensionSchemaPaths = @('/staged/extension.json') } }
Export-ModuleMember -Function Resolve-BootstrapSchemaWorkspace
'@ | Set-Content (Join-Path $script:handoffRoot 'bootstrap-schema-workspace.psm1')
        @'
function ReadValuesFromEnvFile { param($Path) return @{ POSTGRES_PORT = '15432'; MSSQL_PORT = '11433' } }
Export-ModuleMember -Function ReadValuesFromEnvFile
'@ | Set-Content (Join-Path $script:handoffRoot 'env-utility.psm1')
        @'
function Get-ComposeResolvedEnvValue { param($EnvironmentValues, $Name, $DefaultValue) if ($EnvironmentValues.ContainsKey($Name)) { return $EnvironmentValues[$Name] }; return $DefaultValue }
Export-ModuleMember -Function Get-ComposeResolvedEnvValue
'@ | Set-Content (Join-Path $script:handoffRoot 'database-safety.psm1')
        Import-Module (Join-Path $script:handoffRoot 'bootstrap-cdc-handoff.psm1') -Force
    }
    BeforeEach {
        $script:settings = @{
            AppSettings = @{ Datastore = 'postgresql' }
            DataManagement = @{ DocumentCache = @{ Targets = @(@{ DataStoreId = 42 }); Projector = @{ PageSize = 55 } } }
            ConfigurationServiceSettings = @{ BaseUrl = 'http://localhost:8081'; ClientSecret = 'private-$secret'; EncryptionKey = 'private-key' }
            Cdc = @{ TenantKey = ''; DataStoreId = '42'; DeploymentKey = 'local'; InstanceKey = 'datastore-42'; Generation = 1; SetupConnectionString = 'private-connection'; ConnectEndpoint = 'http://127.0.0.1:8083'; WorkerMetricsEndpoint = 'http://127.0.0.1:9404/metrics' }
        }
        $script:settingsFile = Join-Path $TestDrive 'explicit.json'
        $script:settings | ConvertTo-Json -Depth 20 | Set-Content $script:settingsFile
    }
    AfterAll {
        Get-Module -All | Where-Object { $_.Path -and $_.Path.StartsWith($script:handoffRoot + [IO.Path]::DirectorySeparatorChar) } | Remove-Module -Force
    }
    It 'accepts explicit runtime membership without manufacturing a target' {
        $loaded = Read-BootstrapCdcSettings -Path $script:settingsFile -DatabaseEngine postgresql
        $loaded.DataManagement.DocumentCache.Targets[0].DataStoreId | Should -Be 42
    }
    It 'rejects missing runtime membership and the obsolete top-level section' {
        $script:settings.DocumentCache = $script:settings.DataManagement.DocumentCache
        $script:settings.Remove('DataManagement')
        $script:settings | ConvertTo-Json -Depth 20 | Set-Content $script:settingsFile
        { Read-BootstrapCdcSettings -Path $script:settingsFile -DatabaseEngine postgresql } | Should -Throw '*explicit DMS settings*'
    }
    It 'rejects mismatched target and remote CMS before infrastructure' {
        $script:settings.Cdc.DataStoreId = '43'
        $script:settings.ConfigurationServiceSettings.BaseUrl = 'http://shared.example:8081'
        $script:settings | ConvertTo-Json -Depth 20 | Set-Content $script:settingsFile
        { Read-BootstrapCdcSettings -Path $script:settingsFile -DatabaseEngine postgresql } | Should -Throw '*explicit DMS settings*'
    }
    It 'rejects an unsupported qualified-worker endpoint before infrastructure (<field>, <value>)' -ForEach @(
        @{ field = 'ConnectEndpoint'; value = 'http://localhost:8083' },
        @{ field = 'WorkerMetricsEndpoint'; value = 'http://localhost:9404/metrics' },
        @{ field = 'WorkerMetricsEndpoint'; value = 'http://127.0.0.1:9404/wrong' },
        @{ field = 'ConnectEndpoint'; value = 'https://127.0.0.1:8083' }
    ) {
        $script:settings.Cdc[$field] = $value
        $script:settings | ConvertTo-Json -Depth 20 | Set-Content $script:settingsFile
        { Read-BootstrapCdcSettings -Path $script:settingsFile -DatabaseEngine postgresql } | Should -Throw '*worker/metrics endpoints*'
    }
    It 'rejects the infrastructure-created database before producing a handoff' {
        { New-BootstrapCdcHandoff -Settings $script:settings -InputSettingsPath $script:settingsFile -StatePath '/unused' -EnvironmentFile '/selected/env' -Project 'dms-local' -DatabaseName 'edfi_datamanagementservice' -IdentityProvider keycloak } | Should -Throw '*distinct from infrastructure-created*'
    }
    It 'carries identical projection and CMS settings, ordinary schemas and custom state for <provider>' -ForEach @(
        @{ provider = 'postgresql'; port = 15432 }, @{ provider = 'mssql'; port = 11433 }
    ) {
        $script:settings.AppSettings.Datastore = $provider
        $state = Join-Path $TestDrive 'custom-state'
        $handoff = New-BootstrapCdcHandoff -Settings $script:settings -InputSettingsPath $script:settingsFile -StatePath $state -EnvironmentFile '/selected/env' -Project 'dms-published' -DatabaseName 'dedicated_cdc' -IdentityProvider keycloak
        $handoff.IdentityProvider | Should -Be 'keycloak'
        $snapshot = Get-Content $handoff.SettingsPath -Raw | ConvertFrom-Json
        $compose = Get-Content $handoff.DmsComposePath -Raw | ConvertFrom-Json
        $snapshot.Cdc.Compose.Project | Should -Be 'dms-published'
        $snapshot.Cdc.Compose.EnvironmentFile | Should -Be '/selected/env'
        $snapshot.Cdc.Compose.BrokerSizeOverrideFile | Should -Be (Join-Path $state 'broker-size.json')
        $snapshot.Cdc.Compose.DatabaseHostPort | Should -Be $port
        $snapshot.Cdc.Schemas | Should -Be @('/staged/core.json', '/staged/extension.json')
        $compose.services.dms.environment.DataManagement__DocumentCache__Targets__0__DataStoreId | Should -Be '42'
        $compose.services.dms.environment.DataManagement__DocumentCache__Projector__PageSize | Should -Be '55'
        $compose.services.dms.environment.ConfigurationServiceSettings__BaseUrl | Should -Be 'http://ed-fi-api-config-service:8081'
        $compose.services.dms.environment.ConfigurationServiceSettings__ClientSecret | Should -Be 'private-$$secret'
        $compose.services.dms.environment.AppSettings__Datastore | Should -Be $provider
        $compose.services.dms.environment.AppSettings__ApiSchemaPath | Should -Be '/app/ApiSchema'
        (Get-Content $handoff.DmsComposePath -Raw) | Should -Not -Match 'private-connection|SetupConnectionString'
        [int][IO.File]::GetUnixFileMode($handoff.SettingsPath) | Should -Be 384
        [int][IO.File]::GetUnixFileMode($handoff.DmsComposePath) | Should -Be 384
        Test-Path (Join-Path $state 'bootstrap-manifest.json') | Should -BeFalse
        $script:settings.Cdc.Contains('Compose') | Should -BeFalse
    }
}
