# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

BeforeAll {
    $script:composeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}

Describe 'CDC bootstrap wrapper phase contract' {
    BeforeAll {
        $script:sandbox = Join-Path $TestDrive 'wrapper'
        New-Item -ItemType Directory $script:sandbox | Out-Null
        foreach ($name in @('bootstrap-local-dms.ps1', 'bootstrap-published-dms.ps1', 'bootstrap-wrapper.psm1')) {
            Copy-Item (Join-Path $script:composeRoot $name) $script:sandbox
        }
        @'
function Invoke-CdcAdmittedHost { param($Project, $StartScript, $Parameters) & $StartScript @Parameters }
Export-ModuleMember -Function Invoke-CdcAdmittedHost
'@ | Set-Content (Join-Path $script:sandbox 'cdc-lifecycle.psm1')
        'local=test' | Set-Content (Join-Path $script:sandbox '.env')
        @'
function Resolve-DataStandardEnvironmentFile { param($DataStandardVersion, $BaseEnvironmentFile, $DockerComposeRoot, $OverlayPrefix) return $BaseEnvironmentFile }
function Resolve-DatabaseEngineEnvironmentFile { param($DatabaseEngine, $BaseEnvironmentFile, $DockerComposeRoot, $SkipMssqlCmsDatabaseValidation) return $BaseEnvironmentFile }
function Resolve-CmsDatabaseTopologyEnvironmentFile { param($BaseEnvironmentFile, $DatabaseEngine, $SeparateConfigDatabase, $DockerComposeRoot) return $BaseEnvironmentFile }
function Confirm-CmsDatabaseTopologyAgreement { param($EnvironmentFile, $DatabaseEngine) }
function ReadValuesFromEnvFile { param($EnvironmentFile) return @{} }
Export-ModuleMember -Function *
'@ | Set-Content (Join-Path $script:sandbox 'env-utility.psm1')

        # These stubs are phase boundaries. Actual controller receipt/readiness rules are exercised
        # below and in the CDC controller tests; this suite executes both real entry-point wrappers.
        @'
function Read-BootstrapCdcSettings { param($Path, $DatabaseEngine)
    return @{ Cdc = @{ DataStoreId = '42'; DeploymentKey = 'local'; InstanceKey = 'datastore-42'; Generation = 1 }; ConfigurationServiceSettings = @{ BaseUrl = 'http://localhost:8081' } }
}
function Assert-BootstrapCdcOfflineOwnership { param($Project, [switch]$InfrastructureReady, $DatabaseEngine, $CmsPort)
    if (Test-Path (Join-Path $PSScriptRoot 'writer')) { throw 'Running writer' }
}
function New-BootstrapCdcHandoff { param($Settings, $StatePath, $EnvironmentFile, $Project)
    return @{ Settings = $Settings; DmsComposePath = (Join-Path $StatePath 'dms.json'); SettingsPath = 'settings.json' }
}
function Invoke-BootstrapCdcEnable { param($Handoff, $Receipt, $SelectedDataStoreIds, $StatePath)
    Add-Content (Join-Path $PSScriptRoot 'calls') "cdc:$($SelectedDataStoreIds -join ','):$($Receipt.CreationReceipt.Outcome):$StatePath"
    if (Test-Path (Join-Path $PSScriptRoot 'cancel')) { throw [OperationCanceledException]::new() }
    if (Test-Path (Join-Path $PSScriptRoot 'fail')) { throw 'CDC unavailable' }
    if ($Receipt.CreationReceipt.Outcome -ne 'Created') { throw 'Reused database' }
}
Export-ModuleMember -Function *-BootstrapCdc*
'@ | Set-Content (Join-Path $script:sandbox 'bootstrap-cdc.psm1')
        $start = @'
param([switch]$InfraOnly, [switch]$DmsOnly, [switch]$EnableConfig, [string]$IdentityProvider,
    [string]$EnvironmentFile, [string]$DatabaseEngine, [switch]$SeparateConfigDatabase,
    [switch]$EnableKafkaUI, [switch]$CdcKafkaInfrastructure, [switch]$SuppressWriterGuidance,
    [switch]$SuppressWrapperContinuationGuidance, [string]$CdcDmsComposeFile)
$phase = if ($DmsOnly) { 'dms' } else { 'infra' }
Add-Content (Join-Path $PSScriptRoot 'calls') "$phase`:$DatabaseEngine`:$EnableKafkaUI`:$CdcKafkaInfrastructure`:$SuppressWriterGuidance`:$CdcDmsComposeFile"
if ($InfraOnly -and -not $SuppressWriterGuidance) { Write-Information 'early writer guidance' -InformationAction Continue }
'@
        foreach ($name in @('start-local-dms.ps1', 'start-published-dms.ps1')) { $start | Set-Content (Join-Path $script:sandbox $name) }
        @'
param($EnvironmentFile, $DatabaseEngine, [switch]$SeparateConfigDatabase, $DataStoreDatabaseName, [switch]$NoDataStore)
Add-Content (Join-Path $PSScriptRoot 'calls') "configure:$DatabaseEngine`:$DataStoreDatabaseName"
$id = if (Test-Path (Join-Path $PSScriptRoot 'mismatch')) { 43 } else { 42 }
return [pscustomobject]@{ SelectedDataStoreIds = @($id); HasRouteQualifiedDataStores = $false }
'@ | Set-Content (Join-Path $script:sandbox 'configure-local-data-store.ps1')
        @'
param($EnvironmentFile, $DataStoreId, $DatabaseEngine, [switch]$SeparateConfigDatabase, $CdcBindingStatePath,
    [switch]$PrepareCdcProjectionPrerequisites, $DeploymentKey, $InstanceKey, $Generation)
Add-Content (Join-Path $PSScriptRoot 'calls') "provision:$DatabaseEngine`:$PrepareCdcProjectionPrerequisites`:$CdcBindingStatePath`:$InstanceKey"
$outcome = if (Test-Path (Join-Path $PSScriptRoot 'reuse')) { 'Reused' } else { 'Created' }
if ($CdcBindingStatePath) { return @{ CreationReceipt = @{ Outcome = $outcome } } }
'@ | Set-Content (Join-Path $script:sandbox 'provision-dms-schema.ps1')
        @'
param($EnvironmentFile, $IdentityProvider, $DataStoreId)
Add-Content (Join-Path $PSScriptRoot 'calls') "seed:$($DataStoreId -join ',')"
'@ | Set-Content (Join-Path $script:sandbox 'load-dms-seed-data.ps1')
    }
    BeforeEach {
        foreach ($name in @('calls', 'fail', 'cancel', 'writer', 'reuse', 'mismatch')) {
            Remove-Item (Join-Path $script:sandbox $name) -ErrorAction SilentlyContinue
        }
        $script:arguments = @{
            EnableKafkaCdc = $true; CdcSettingsPath = 'explicit.json'; CdcBindingStatePath = (Join-Path $TestDrive 'custom-state')
            SeparateConfigDatabase = $true; DataStoreDatabaseName = 'dedicated_cdc'; LoadSeedData = $true; EnableKafkaUI = $true
        }
    }
    AfterAll {
        Get-Module -All | Where-Object { $_.Path -and $_.Path.StartsWith($script:sandbox + [IO.Path]::DirectorySeparatorChar) } | Remove-Module -Force
    }

    It 'orders <wrapper>/<provider> through CDC before DMS and seed, including UI' -ForEach @(
        @{ wrapper = 'local'; provider = 'postgresql' }, @{ wrapper = 'local'; provider = 'mssql' },
        @{ wrapper = 'published'; provider = 'postgresql' }, @{ wrapper = 'published'; provider = 'mssql' }
    ) {
        & (Join-Path $script:sandbox "bootstrap-$wrapper-dms.ps1") @script:arguments -DatabaseEngine $provider
        $calls = @(Get-Content (Join-Path $script:sandbox 'calls'))
        $calls.Count | Should -Be 6
        $calls[0] | Should -Be "infra:$provider`:True:True:True:"
        $calls[1] | Should -Be "configure:$provider`:dedicated_cdc"
        $calls[2] | Should -Be "provision:$provider`:True:$($script:arguments.CdcBindingStatePath):datastore-42"
        $calls[3] | Should -Be "cdc:42:Created:$($script:arguments.CdcBindingStatePath)"
        $calls[4] | Should -Be "dms:$provider`:False:False:False:$($script:arguments.CdcBindingStatePath)/dms.json"
        $calls[5] | Should -Be 'seed:42'
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
        { New-BootstrapCdcHandoff -Settings $script:settings -StatePath '/unused' -EnvironmentFile '/selected/env' -Project 'dms-local' -DatabaseName 'edfi_datamanagementservice' } | Should -Throw '*distinct from infrastructure-created*'
    }
    It 'carries identical projection and CMS settings, ordinary schemas and custom state for <provider>' -ForEach @(
        @{ provider = 'postgresql'; port = 15432 }, @{ provider = 'mssql'; port = 11433 }
    ) {
        $script:settings.AppSettings.Datastore = $provider
        $state = Join-Path $TestDrive 'custom-state'
        $handoff = New-BootstrapCdcHandoff -Settings $script:settings -StatePath $state -EnvironmentFile '/selected/env' -Project 'dms-published' -DatabaseName 'dedicated_cdc'
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
