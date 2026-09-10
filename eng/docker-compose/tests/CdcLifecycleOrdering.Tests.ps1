# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseDeclaredVarsMoreThanAssignments', '', Justification = 'Variables supply the dynamic scope of executable primitive blocks extracted from the scripts under test.')]
param()

Describe 'Managed CDC deployment lifecycle ordering' {
    BeforeAll {
        $script:root = Join-Path $TestDrive 'compose'
        New-Item -ItemType Directory $script:root | Out-Null
        Copy-Item (Join-Path $PSScriptRoot '../cdc-lifecycle.psm1') $script:root
        Import-Module (Join-Path $script:root 'cdc-lifecycle.psm1') -Force
        function New-TestHandoff {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Creates isolated test files only.')]
            param($id, $project = 'dms-local', $provider = 'postgresql')
            $settings = @{
                AppSettings = @{ Datastore = $provider }
                DataManagement = @{ DocumentCache = @{ Targets = @(@{ DataStoreId = $id }) } }
                Cdc = @{
                    Compose = @{ Project = $project; EnvironmentFile = (Join-Path $script:root '.env.custom'); File = '/compose/kafka-cdc.yml'; BrokerSizeOverrideFile = (Join-Path $script:root 'shared/broker-size.json') }
                    DeploymentKey = 'deployment'; DataStoreId = [string]$id; InstanceKey = "instance-$id"; Generation = 7
                    Worker = @{ Key = 'worker'; OffsetStorageTopic = 'shared-offsets' }
                    ConnectEndpoint = 'http://localhost:8083/'; WorkerMetricsEndpoint = 'http://localhost:9404/metrics'
                }
            }
            $settingsPath = Join-Path $script:root "$id.settings.json"
            $dmsPath = Join-Path $script:root "$id.dms.json"
            $settings | ConvertTo-Json -Depth 20 | Set-Content $settingsPath
            @{ services = @{ dms = @{ environment = @{ DataManagement__DocumentCache__Targets__0__DataStoreId = [string]$id; AppSettings__Datastore = $provider } } } } | ConvertTo-Json -Depth 10 | Set-Content $dmsPath
            foreach ($path in @($settingsPath, $dmsPath)) { [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]384) }
            return @{ Settings = $settings; SettingsPath = $settingsPath; DmsComposePath = $dmsPath }
        }
        function Read-TestDeployment($project = 'dms-local') {
            Get-Content (Join-Path $script:root ".cdc-deployments/$project.json") -Raw | ConvertFrom-Json -AsHashtable
        }
        function Invoke-TestLifecycle($parameters, $project = 'dms-local') {
            Invoke-CdcDeploymentLifecycle -Project $project -StartScript "/compose/start-$project.ps1" -Parameters $parameters
        }
    }
    BeforeEach {
        Remove-Item (Join-Path $script:root '.cdc-deployments') -Recurse -Force -ErrorAction SilentlyContinue
        'CUSTOM=selected' | Set-Content (Join-Path $script:root '.env.custom')
        $script:trace = [Collections.Generic.List[string]]::new()
        $script:live = @('connector-42', 'connector-43')
        $script:workerRunning = $true
        $script:badStatus = $false
        $script:failure = ''
        $script:removedDuringFailure = @()
        $script:resources = Join-Path $script:root 'resources'
        New-Item -ItemType Directory $script:resources -Force | Out-Null
        foreach ($resource in @('worker', 'database', 'worker-volume', 'database-volume', 'network')) {
            'present' | Set-Content (Join-Path $script:resources $resource)
        }
        foreach ($id in @(42, 43)) {
            Register-CdcDeploymentHandoff -Handoff (New-TestHandoff $id) -StatePath (Join-Path $script:root "custom-state-$id")
        }
        Mock -ModuleName cdc-lifecycle Invoke-CdcLifecycleCommand {
            $script:trace.Add("$Operation`:$($Entry.DataStoreId)")
            if (-not (Test-Path (Join-Path $script:resources 'worker')) -or
                -not (Test-Path (Join-Path $script:resources 'database'))) { throw 'Controller services removed' }
            if ($script:failure -eq "$Operation`:$($Entry.DataStoreId)") { throw 'Controller rejected or cancelled' }
            $Entry.ConnectorName = "connector-$($Entry.DataStoreId)"
            if ($Operation -eq 'retire') { $script:live = @($script:live | Where-Object { $_ -ne $Entry.ConnectorName }) }
            if ($Operation -eq 'start-worker') { $script:workerRunning = $true }
        }
        Mock -ModuleName cdc-lifecycle Invoke-CdcLifecycleRest {
            $script:trace.Add("rest:$Path")
            if (-not (Test-Path (Join-Path $script:resources 'worker'))) { throw 'Connect removed' }
            if ($Path -eq 'connectors') { return $script:live }
            $name = $Path.Split('/')[1]
            return @{ name = $name; connector = @{ state = $(if ($script:badStatus) { 'RUNNING' } else { 'STOPPED' }) }; tasks = @() }
        }
        Mock -ModuleName cdc-lifecycle Invoke-CdcLifecycleDocker {
            if ($script:workerRunning) { return 'worker-container' }
        }
        Mock -ModuleName cdc-lifecycle Invoke-CdcInfrastructure {
            $phase = if ($Parameters['d']) { if ($Parameters.v) { 'down-volumes' } else { 'stop-worker' } } elseif ($Parameters['DmsOnly']) { 'dms' } else { 'infra' }
            $script:trace.Add($phase)
            if ($script:failure -eq $phase) {
                foreach ($resource in $script:removedDuringFailure) {
                    Remove-Item (Join-Path $script:resources $resource) -Force -ErrorAction SilentlyContinue
                }
                if (-not (Test-Path (Join-Path $script:resources 'worker'))) { $script:workerRunning = $false }
                throw 'Infrastructure failed'
            }
            if ($phase -eq 'down-volumes') { Get-ChildItem $script:resources | Remove-Item -Force }
            if ($phase -in @('down-volumes', 'stop-worker')) { $script:workerRunning = $false }
        }
    }
    AfterAll { Remove-Module cdc-lifecycle -Force }

    It 'recovers only the original initial handoff (<change>)' -ForEach @(
        @{ change = 'none' }, @{ change = 'settings' }, @{ change = 'state' },
        @{ change = 'database' }, @{ change = 'phase' }, @{ change = 'receipt' },
        @{ change = 'snapshot' }, @{ change = 'environment' }
    ) {
        Remove-Item (Join-Path $script:root '.cdc-deployments') -Recurse -Force
        $handoff = New-TestHandoff 42
        $inputPath = Join-Path $script:root "original-input.json"
        Copy-Item $handoff.SettingsPath $inputPath -Force
        $handoff.InputSettingsHash = (Get-FileHash $inputPath).Hash
        $handoff.DatabaseNameHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('dedicated')))
        $state = Join-Path $script:root 'original-state'
        $receipt = @{ WorkflowId = [guid]::NewGuid().ToString(); CreationReceipt = @{ Outcome = 'Created' } }
        if ($change -eq 'receipt') { $receipt = $null }
        Register-CdcDeploymentHandoff -Handoff $handoff -StatePath $state -Receipt $receipt
        $original = Get-Content (Join-Path $script:root '.cdc-deployments/dms-local.json') -Raw
        $database = 'dedicated'
        switch ($change) {
            'settings' { Add-Content $inputPath 'changed' }
            'state' { $state = Join-Path $script:root 'other-state' }
            'database' { $database = 'other' }
            'phase' {
                $deployment = Read-TestDeployment
                $deployment.Phase = 'Stopped'
                $deployment | ConvertTo-Json -Depth 64 | Set-Content (Join-Path $script:root '.cdc-deployments/dms-local.json')
            }
            'snapshot' { Add-Content $handoff.DmsComposePath 'changed' }
            'environment' { Add-Content (Join-Path $script:root '.env.custom') 'CHANGED=yes' }
        }
        $arguments = @{ Project = 'dms-local'; StatePath = $state; InputSettingsPath = $inputPath; DatabaseName = $database }
        if ($change -ne 'none') { { Get-CdcBootstrapRetryHandoff @arguments } | Should -Throw }
        else {
            $retained = Get-CdcBootstrapRetryHandoff @arguments
            $retained.SettingsPath | Should -Be $handoff.SettingsPath
            $retained.DmsComposePath | Should -Be $handoff.DmsComposePath
            $retained.Receipt.WorkflowId | Should -Be $receipt.WorkflowId
            $retained.EnvironmentFile | Should -Be $handoff.Settings.Cdc.Compose.EnvironmentFile
            (Get-Content (Join-Path $script:root '.cdc-deployments/dms-local.json') -Raw) | Should -Be $original
        }
    }

    It 'stops and verifies both connectors before worker shutdown while retaining custom roots' {
        Invoke-TestLifecycle @{ d = $true }
        $script:trace | Should -Be @('stop:42', 'stop:43', 'rest:connectors', 'rest:connectors/connector-42/status', 'rest:connectors/connector-43/status', 'stop-worker')
        $deployment = Read-TestDeployment
        $deployment.Phase | Should -Be 'Stopped'
        $deployment.Entries.StatePath | Should -Be @((Join-Path $script:root 'custom-state-42'), (Join-Path $script:root 'custom-state-43'))
    }
    It 'does not shutdown the worker after delayed/unverified final task status' {
        $script:badStatus = $true
        { Invoke-TestLifecycle @{ d = $true } } | Should -Throw '*not currently verified*'
        $script:trace | Should -Not -Contain 'stop-worker'
        (Read-TestDeployment).Phase | Should -Be 'Transition'
    }
    It 'retains infrastructure and inventory on <failure>' -ForEach @(@{ failure = 'stop:42' }, @{ failure = 'stop:43' }) {
        $script:failure = $failure
        { Invoke-TestLifecycle @{ d = $true } } | Should -Throw
        $script:trace | Should -Not -Contain 'stop-worker'
        (Read-TestDeployment).Phase | Should -Be 'Transition'
    }
    It 'rejects missing or additional live connectors (<names>)' -ForEach @(@{ names = @('connector-42') }, @{ names = @('connector-42', 'connector-43', 'unmanaged') }) {
        $script:live = $names
        { Invoke-TestLifecycle @{ d = $true } } | Should -Throw '*inventory*'
        $script:trace | Should -Not -Contain 'stop-worker'
    }
    It 'starts REST with all retained connectors stopped before individual guarded resume and DMS' {
        Invoke-TestLifecycle @{ d = $true }
        $script:trace.Clear()
        Invoke-TestLifecycle @{}
        $script:trace | Should -Be @('infra', 'start-worker:42', 'rest:connectors', 'rest:connectors/connector-42/status', 'rest:connectors/connector-43/status', 'start:42', 'start:43', 'dms')
        (Read-TestDeployment).Phase | Should -Be 'Active'
        $merged = Get-Content (Join-Path $script:root '.cdc-deployments/dms-local.dms.json') -Raw | ConvertFrom-Json -AsHashtable
        $merged.services.dms.environment.DataManagement__DocumentCache__Targets__0__DataStoreId | Should -Be '42'
        $merged.services.dms.environment.DataManagement__DocumentCache__Targets__1__DataStoreId | Should -Be '43'
        Should -Invoke -ModuleName cdc-lifecycle Invoke-CdcInfrastructure -Times 1 -ParameterFilter {
            $Parameters['InfraOnly'] -and $Parameters.EnvironmentFile -eq (Join-Path $script:root '.env.custom') -and
            $Parameters.CdcBrokerSizeOverrideFile -eq (Join-Path $script:root 'shared/broker-size.json') -and $Parameters.SuppressWriterGuidance
        }
    }
    It 'never resumes or starts DMS if worker restart lost STOPPED state' {
        Invoke-TestLifecycle @{ d = $true }
        $script:trace.Clear()
        $script:badStatus = $true
        { Invoke-TestLifecycle @{} } | Should -Throw '*not currently verified*'
        $script:trace | Should -Not -Contain 'start:42'
        $script:trace | Should -Not -Contain 'dms'
        (Read-TestDeployment).Phase | Should -Be 'Transition'
    }
    It 'blocks DMS and a later unmanaged startup retry after one resume fails' {
        Invoke-TestLifecycle @{ d = $true }
        $script:trace.Clear()
        $script:failure = 'start:43'
        { Invoke-TestLifecycle @{} } | Should -Throw
        $script:trace | Should -Not -Contain 'dms'
        $script:trace.Clear()
        { Invoke-TestLifecycle @{} } | Should -Throw '*verified shutdown*'
        $script:trace.Count | Should -Be 0
    }
    It 'rejects native worker recovery before managed worker launch' {
        Invoke-TestLifecycle @{ d = $true }
        $script:workerRunning = $true
        $script:trace.Clear()
        { Invoke-TestLifecycle @{} } | Should -Throw '*recovered outside*'
        $script:trace.Count | Should -Be 0
    }
    It 'rechecks fresh connector shutdown on a stop retry when infrastructure shutdown failed' {
        $script:failure = 'stop-worker'
        { Invoke-TestLifecycle @{ d = $true } } | Should -Throw
        $script:failure = ''
        $script:trace.Clear()
        Invoke-TestLifecycle @{ d = $true }
        $script:trace[0] | Should -Be 'stop:42'
        $script:trace[-1] | Should -Be 'stop-worker'
    }
    It 'performs governed generation cleanup for every peer before removing volumes' {
        Invoke-TestLifecycle @{ d = $true; v = $true }
        $script:trace | Should -Be @('retire:42', 'retire:43', 'rest:connectors', 'down-volumes')
        Test-CdcDeployment 'dms-local' | Should -BeFalse
    }
    It 'retires retained bindings after pre-registration interruption before deleting volumes' {
        $script:live = @()
        Invoke-TestLifecycle @{ d = $true; v = $true }
        $script:trace | Should -Be @('retire:42', 'retire:43', 'rest:connectors', 'down-volumes')
        Test-CdcDeployment 'dms-local' | Should -BeFalse
    }
    It 'blocks volume deletion when an absent connector has unverifiable offsets' {
        $script:live = @()
        $script:failure = 'retire:42'
        { Invoke-TestLifecycle @{ d = $true; v = $true } } | Should -Throw
        $script:trace | Should -Not -Contain 'down-volumes'
        (Read-TestDeployment).Phase | Should -Be 'Retiring'
    }
    It 'retains partial cleanup and retries controller retirement before volume deletion' {
        $script:failure = 'retire:43'
        { Invoke-TestLifecycle @{ d = $true; v = $true } } | Should -Throw
        $script:trace | Should -Not -Contain 'down-volumes'
        (Read-TestDeployment).Phase | Should -Be 'Retiring'
        $script:failure = ''
        $script:trace.Clear()
        Invoke-TestLifecycle @{ d = $true; v = $true }
        $script:trace | Should -Be @('retire:42', 'retire:43', 'rest:connectors', 'down-volumes')
    }
    It 'restores stopped infrastructure without resuming before destructive cleanup' {
        Invoke-TestLifecycle @{ d = $true }
        $script:trace.Clear()
        Invoke-TestLifecycle @{ d = $true; v = $true }
        $script:trace | Should -Be @('infra', 'start-worker:42', 'rest:connectors', 'rest:connectors/connector-42/status', 'rest:connectors/connector-43/status', 'retire:42', 'retire:43', 'rest:connectors', 'down-volumes')
    }
    It 'resumes retired <project>/<provider> teardown after removal of <removed>' -ForEach @(
        foreach ($project in @('dms-local', 'dms-published')) {
            foreach ($provider in @('postgresql', 'mssql')) {
                foreach ($removed in @('worker', 'database', 'all')) {
                    @{ project = $project; provider = $provider; removed = $removed }
                }
            }
        }
    ) {
        Remove-Item (Join-Path $script:root '.cdc-deployments') -Recurse -Force
        foreach ($id in @(42, 43)) {
            Register-CdcDeploymentHandoff (New-TestHandoff -id $id -project $project -provider $provider) (Join-Path $script:root "custom-state-$id")
        }
        $parameters = @{ d = $true; v = $true; DatabaseEngine = $provider }
        $script:failure = 'down-volumes'
        $script:removedDuringFailure = if ($removed -eq 'all') {
            @('worker', 'database', 'worker-volume', 'database-volume', 'network')
        } else { @($removed, "$removed-volume") }
        { Invoke-TestLifecycle $parameters $project } | Should -Throw '*Infrastructure failed*'
        (Read-TestDeployment $project).Phase | Should -Be 'Retired'
        foreach ($resource in $script:removedDuringFailure) {
            Test-Path (Join-Path $script:resources $resource) | Should -BeFalse
        }
        $script:failure = ''
        $script:trace.Clear()
        Invoke-TestLifecycle $parameters $project
        $script:trace | Should -Be @('down-volumes')
        @(Get-ChildItem $script:resources).Count | Should -Be 0
        Test-CdcDeployment $project | Should -BeFalse
        Should -Invoke -ModuleName cdc-lifecycle Invoke-CdcInfrastructure -Times 2 -Exactly -ParameterFilter {
            $Parameters.d -and $Parameters.v -and -not $Parameters.RemoveBootstrap -and
            $Parameters.DatabaseEngine -eq $provider -and
            $Parameters.EnvironmentFile -eq (Join-Path $script:root '.env.custom')
        }
    }
    It 'resumes after a process exits following successful volume deletion before inventory removal' {
        # Leave a real, validated Retired checkpoint, then let a separate process complete
        # the infrastructure primitive and exit before returning to inventory deletion.
        $script:failure = 'down-volumes'
        { Invoke-TestLifecycle @{ d = $true; v = $true } } | Should -Throw '*Infrastructure failed*'
        $childPath = Join-Path $script:root 'interrupt-teardown.ps1'
        @'
param($ModulePath, $Resources)
$ErrorActionPreference = 'Stop'
Import-Module $ModulePath
& (Get-Module cdc-lifecycle) {
    param($Resources)
    $script:resources = $Resources
    function script:Invoke-CdcLifecycleCommand { throw 'Unexpected controller call' }
    function script:Invoke-CdcLifecycleRest { throw 'Unexpected Connect call' }
    function script:Invoke-CdcLifecycleDocker { throw 'Unexpected worker inspection' }
    function script:Invoke-CdcInfrastructure {
        param($StartScript, $Parameters)
        if (-not $Parameters.d -or -not $Parameters.v) { throw 'Unexpected startup' }
        Get-ChildItem $script:resources | Remove-Item -Force
        exit 73
    }
} $Resources
Invoke-CdcDeploymentLifecycle 'dms-local' '/unused/start.ps1' @{ d = $true; v = $true }
'@ | Set-Content $childPath
        & pwsh -NoProfile -File $childPath (Join-Path $script:root 'cdc-lifecycle.psm1') $script:resources
        $LASTEXITCODE | Should -Be 73
        @(Get-ChildItem $script:resources).Count | Should -Be 0
        (Read-TestDeployment).Phase | Should -Be 'Retired'
        $script:failure = ''
        $script:workerRunning = $false
        $script:trace.Clear()
        Invoke-TestLifecycle @{ d = $true; v = $true }
        $script:trace | Should -Be @('down-volumes')
        Test-CdcDeployment 'dms-local' | Should -BeFalse
    }
    It 'requires destructive teardown to finish a Retired deployment (<operation>)' -ForEach @(
        @{ operation = 'start'; parameters = @{} }, @{ operation = 'stop'; parameters = @{ d = $true } }
    ) {
        $script:failure = 'down-volumes'
        { Invoke-TestLifecycle @{ d = $true; v = $true } } | Should -Throw
        $script:trace.Clear()
        { Invoke-TestLifecycle $parameters } | Should -Throw '*destructive teardown*'
        $script:trace.Count | Should -Be 0
        (Read-TestDeployment).Phase | Should -Be 'Retired'
    }
    It 'does not infer retirement from absent services or binding files in <phase>' -ForEach @(
        @{ phase = 'Active' }, @{ phase = 'Transition' }, @{ phase = 'Retiring' }
    ) {
        $path = Join-Path $script:root '.cdc-deployments/dms-local.json'
        $deployment = Read-TestDeployment
        $deployment.Phase = $phase
        $deployment | ConvertTo-Json -Depth 64 | Set-Content $path
        Get-ChildItem $script:resources | Remove-Item -Force
        $script:workerRunning = $false
        { Invoke-TestLifecycle @{ d = $true; v = $true } } | Should -Throw '*Controller services removed*'
        $script:trace | Should -Be @('retire:42')
        (Read-TestDeployment).Phase | Should -Be 'Retiring'
    }
    It 'rejects <fault> configuration in <phase> before controller or infrastructure effects' -ForEach @(
        foreach ($phase in @('Active', 'Retired')) {
            foreach ($fault in @('settings', 'environment', 'missing', 'version', 'permissions')) {
                @{ fault = $fault; phase = $phase }
            }
        }
    ) {
        if ($phase -eq 'Retired') {
            $script:failure = 'down-volumes'
            { Invoke-TestLifecycle @{ d = $true; v = $true } } | Should -Throw
            $script:trace.Clear()
        }
        $path = Join-Path $script:root '.cdc-deployments/dms-local.json'
        switch ($fault) {
            settings {
                $settingsPath = Join-Path $script:root '42.settings.json'
                $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
                $settings.Cdc.Worker.Key = 'changed-worker'
                $settings | ConvertTo-Json -Depth 20 | Set-Content $settingsPath
            }
            environment { Add-Content (Join-Path $script:root '.env.custom') 'CHANGED=true' }
            missing { Remove-Item (Join-Path $script:root '42.settings.json') }
            version { (Get-Content $path -Raw).Replace('"Version":1', '"Version":99') | Set-Content $path }
            permissions { [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]420) }
        }
        { Invoke-TestLifecycle @{ d = $true; v = $true } } | Should -Throw '*inventory*'
        $script:trace.Count | Should -Be 0
    }
    It 'rejects <selection> conflicts without using caller defaults' -ForEach @(
        @{ selection = @{ DatabaseEngine = 'mssql' } }, @{ selection = @{ EnvironmentFile = '/wrong/env' } },
        @{ selection = @{ CdcBindingStatePath = '/empty/replacement' } }, @{ selection = @{ CdcSettingsPath = '/wrong/settings' } }
    ) {
        { Invoke-TestLifecycle ($selection + @{ d = $true }) } | Should -Throw
        $script:trace.Count | Should -Be 0
    }
    It 'uses the same governed ordering for <project>/<provider>' -ForEach @(
        @{ project = 'dms-local'; provider = 'mssql' }, @{ project = 'dms-published'; provider = 'postgresql' }, @{ project = 'dms-published'; provider = 'mssql' }
    ) {
        Remove-Item (Join-Path $script:root '.cdc-deployments') -Recurse -Force
        Register-CdcDeploymentHandoff (New-TestHandoff -id 42 -project $project -provider $provider) (Join-Path $script:root 'custom')
        $script:live = @('connector-42')
        Invoke-TestLifecycle @{ d = $true; DatabaseEngine = $provider } $project
        $script:trace[-1] | Should -Be 'stop-worker'
        (Read-TestDeployment $project).Phase | Should -Be 'Stopped'
    }
    It 'keeps peer inventory and source history when retiring only one deployment' {
        $peer = New-TestHandoff 99 'dms-published'
        Register-CdcDeploymentHandoff $peer (Join-Path $script:root 'peer-state')
        $state = Join-Path $script:root 'custom-state-42'
        New-Item -ItemType Directory $state -Force | Out-Null
        'historical-exposure' | Set-Content (Join-Path $state 'history')
        Invoke-TestLifecycle @{ d = $true; v = $true }
        Test-CdcDeployment 'dms-published' | Should -BeTrue
        Get-Content (Join-Path $state 'history') | Should -Be 'historical-exposure'
    }
    It 'permits operational ceiling updates while retaining identity and leaving validation to controllers' {
        $settingsPath = Join-Path $script:root '42.settings.json'
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
        $settings.Cdc.MaxRecordBytes = 134217728
        $settings.Cdc.ProducerBufferBytes = 268435456
        $settings | ConvertTo-Json -Depth 20 | Set-Content $settingsPath
        Invoke-TestLifecycle @{ d = $true }
        (Read-TestDeployment).Phase | Should -Be 'Stopped'
    }
    It 'rejects initial writer handoff after a concurrent managed stop' {
        Invoke-TestLifecycle @{ d = $true }
        $script:trace.Clear()
        { Invoke-CdcAdmittedHost 'dms-local' '/compose/start.ps1' @{ DmsOnly = $true } } | Should -Throw '*no longer authorized*'
        $script:trace.Count | Should -Be 0
    }
    It 'does not turn unreadable REST evidence into empty worker inventory' {
        Mock -ModuleName cdc-lifecycle Invoke-CdcLifecycleRest { throw 'REST unavailable' }
        { Invoke-TestLifecycle @{ d = $true; v = $true } } | Should -Throw
        $script:trace | Should -Not -Contain 'down-volumes'
        (Read-TestDeployment).Phase | Should -Be 'Retiring'
    }
    It 'keeps external effects behind durable deployment intent' {
        Mock -ModuleName cdc-lifecycle sync { $global:LASTEXITCODE = 1 }
        { Invoke-TestLifecycle @{ d = $true } } | Should -Throw '*flush failed*'
        $script:trace.Count | Should -Be 0
        (Read-TestDeployment).Phase | Should -Be 'Transition'
    }
    It 'serializes independent wrapper processes with the same deployment lock' {
        $modulePath = Join-Path $script:root 'cdc-lifecycle.psm1'
        $childPath = Join-Path $script:root 'contend.ps1'
        $ready = Join-Path $script:root 'lock-ready'
        $acquired = Join-Path $script:root 'lock-acquired'
        @'
param($ModulePath, $ReadyPath, $AcquiredPath)
Import-Module $ModulePath
'waiting' | Set-Content $ReadyPath
$lock = & (Get-Module cdc-lifecycle) { Enter-CdcDeploymentLock 'dms-local' }
try { 'acquired' | Set-Content $AcquiredPath } finally { $lock.Dispose() }
'@ | Set-Content $childPath
        $lock = & (Get-Module cdc-lifecycle) { Enter-CdcDeploymentLock 'dms-local' }
        # A dotnet-tool installation runs inside dotnet, whose ProcessPath is not a pwsh launcher.
        $launcher = Get-Command pwsh -CommandType Application | Select-Object -First 1
        $info = [Diagnostics.ProcessStartInfo]::new($launcher.Source)
        $info.UseShellExecute = $false
        foreach ($arg in @('-NoProfile', '-File', $childPath, $modulePath, $ready, $acquired)) { $info.ArgumentList.Add($arg) }
        $child = [Diagnostics.Process]::Start($info)
        try {
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
            while (-not (Test-Path $ready) -and [DateTimeOffset]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
            Test-Path $ready | Should -BeTrue
            Start-Sleep -Milliseconds 200
            Test-Path $acquired | Should -BeFalse
            $lock.Dispose()
            $child.WaitForExit(5000) | Should -BeTrue
            $child.ExitCode | Should -Be 0
            Test-Path $acquired | Should -BeTrue
        }
        finally {
            $lock.Dispose()
            if (-not $child.HasExited) { $child.Kill($true); $child.WaitForExit() }
            $child.Dispose()
        }
    }
    It 'detects lost deployment inventory from retained worker or volume evidence' {
        { Assert-CdcUnregisteredInfrastructure 'dms-local' } | Should -Throw '*survives without*'
    }
}

Describe 'SchemaTools lifecycle result boundary' {
    BeforeAll {
        $script:root = Join-Path $TestDrive 'native'
        New-Item -ItemType Directory $script:root | Out-Null
        Copy-Item (Join-Path $PSScriptRoot '../cdc-lifecycle.psm1') $script:root
        @'
function Resolve-DmsSchemaTool { return Join-Path $PSScriptRoot 'tool.ps1' }
Export-ModuleMember -Function Resolve-DmsSchemaTool
'@ | Set-Content (Join-Path $script:root 'bootstrap-schema-tool.psm1')
        @'
param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
$Arguments | ConvertTo-Json | Set-Content (Join-Path $PSScriptRoot 'arguments.json')
Get-Content (Join-Path $PSScriptRoot 'result.json') -Raw
exit ([int](Get-Content (Join-Path $PSScriptRoot 'exit-code')))
'@ | Set-Content (Join-Path $script:root 'tool.ps1')
        Import-Module (Join-Path $script:root 'cdc-lifecycle.psm1') -Force
        function Invoke-TestCommand($operation = 'stop') {
            & (Get-Module cdc-lifecycle) { param($entry, $operation) Invoke-CdcLifecycleCommand $entry $operation } $script:entry $operation
        }
        function Save-TestResult {
            $script:result | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $script:root 'result.json')
        }
    }
    BeforeEach {
        $script:entry = @{ SettingsPath = '/original/settings'; StatePath = '/custom/state'; Generation = 7; InstanceKey = 'instance'; DeploymentKey = 'deployment'; DataStoreId = '42'; ConnectorName = '' }
        $script:result = @{
            operation = 'stop'; succeeded = $true; exitCode = 0
            binding = @{ generation = 7; instanceKey = 'instance'; deploymentKey = 'deployment'; dataStoreId = '42'; connectorName = 'bound-connector' }
            data = @{ targetShutdownVerified = $true; succeeded = $true; boundary = 'VerifiedManagedStop' }
        }
        '0' | Set-Content (Join-Path $script:root 'exit-code')
        Save-TestResult
    }
    AfterAll { Remove-Module cdc-lifecycle -Force }
    It 'requires the structured verified target shutdown and retains original command inputs' {
        Invoke-TestCommand
        $script:entry.ConnectorName | Should -Be 'bound-connector'
        Get-Content (Join-Path $script:root 'arguments.json') -Raw | ConvertFrom-Json | Should -Be @('cdc', 'stop', '--settings', '/original/settings', '--state-path', '/custom/state', '--json')
    }
    It 'rejects acknowledgement-only, wrong-target, and incomplete replies (<fault>)' -ForEach @(
        @{ fault = 'ack' }, @{ fault = 'boundary' }, @{ fault = 'generation' }, @{ fault = 'instance' }, @{ fault = 'name' }, @{ fault = 'data' }, @{ fault = 'operation' }, @{ fault = 'succeeded' }, @{ fault = 'string-boolean' }
    ) {
        switch ($fault) {
            ack { $script:result.data.targetShutdownVerified = $false }
            boundary { $script:result.data.boundary = 'NativeRecovery' }
            generation { $script:result.binding.generation = 8 }
            instance { $script:result.binding.instanceKey = 'peer' }
            name { $script:entry.ConnectorName = 'other' }
            data { $script:result.Remove('data') }
            operation { $script:result.operation = 'status' }
            succeeded { $script:result.succeeded = $false }
            string-boolean { $script:result.data.targetShutdownVerified = 'true' }
        }
        Save-TestResult
        { Invoke-TestCommand } | Should -Throw '*unverified evidence*'
    }
    It 'sanitizes native failure/cancellation output (<code>)' -ForEach @(@{ code = 1 }, @{ code = 130 }) {
        $code | Set-Content (Join-Path $script:root 'exit-code')
        'secret-connection-password' | Set-Content (Join-Path $script:root 'result.json')
        $failure = { Invoke-TestCommand } | Should -Throw -PassThru
        $failure.Exception.Message | Should -Not -Match 'secret-connection-password'
        $failure.Exception.Message | Should -Match "exit $code"
    }
    It 'passes the retained generation and destructive intent to governed retirement' {
        $script:result.operation = 'retire'
        $script:result.data = @{ succeeded = $true; operationId = [guid]::NewGuid().ToString() }
        Save-TestResult
        Invoke-TestCommand 'retire'
        $arguments = Get-Content (Join-Path $script:root 'arguments.json') -Raw | ConvertFrom-Json
        $arguments[-3..-1] | Should -Be @('--generation', '7', '--destructive-cleanup')
    }
}

Describe 'Local and published lifecycle entry points' {
    BeforeAll {
        $script:root = Join-Path $TestDrive 'entry'
        New-Item -ItemType Directory (Join-Path $script:root '.cdc-deployments') -Force | Out-Null
        foreach ($name in @('bootstrap-local-dms.ps1', 'bootstrap-published-dms.ps1', 'start-local-dms.ps1', 'start-published-dms.ps1')) {
            Copy-Item (Join-Path $PSScriptRoot "../$name") $script:root
        }
        @'
function Test-CdcInfrastructureInvocation { return $false }
function Test-CdcDeployment { param($Project) return Test-Path (Join-Path $PSScriptRoot ".cdc-deployments/$Project.json") }
function Invoke-CdcDeploymentLifecycle { param($Project, $StartScript, $Parameters)
    @{ Project = $Project; StartScript = $StartScript; Parameters = $Parameters } | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $PSScriptRoot 'calls.json')
}
Export-ModuleMember -Function *
'@ | Set-Content (Join-Path $script:root 'cdc-lifecycle.psm1')
        foreach ($project in @('dms-local', 'dms-published')) { '{}' | Set-Content (Join-Path $script:root ".cdc-deployments/$project.json") }
    }
    AfterAll {
        Get-Module -All | Where-Object { $_.Path -and $_.Path.StartsWith($script:root + '/') } | Remove-Module -Force
    }
    It 'routes retained <entry> directly with the selected provider, environment and custom state' -ForEach @(
        @{ entry = 'bootstrap-local'; project = 'dms-local' }, @{ entry = 'bootstrap-published'; project = 'dms-published' },
        @{ entry = 'start-local'; project = 'dms-local' }, @{ entry = 'start-published'; project = 'dms-published' }
    ) {
        & (Join-Path $script:root "$entry-dms.ps1") -d -v -DatabaseEngine mssql -EnvironmentFile '/original/.env' -CdcBindingStatePath '/custom/state'
        $call = Get-Content (Join-Path $script:root 'calls.json') -Raw | ConvertFrom-Json -AsHashtable
        $call.Project | Should -Be $project
        $call.Parameters.d | Should -BeTrue
        $call.Parameters.v | Should -BeTrue
        $call.Parameters.DatabaseEngine | Should -Be 'mssql'
        $call.Parameters.EnvironmentFile | Should -Be '/original/.env'
        $call.Parameters.CdcBindingStatePath | Should -Be '/custom/state'
    }
    It 'routes bare <entry> restart without manufacturing fresh bootstrap inputs' -ForEach @(
        @{ entry = 'bootstrap-local' }, @{ entry = 'bootstrap-published' }, @{ entry = 'start-local' }, @{ entry = 'start-published' }
    ) {
        & (Join-Path $script:root "$entry-dms.ps1")
        $call = Get-Content (Join-Path $script:root 'calls.json') -Raw | ConvertFrom-Json -AsHashtable
        $call.Parameters.Count | Should -Be 0
    }
}

Describe 'Managed primitive Docker shutdown selection' {
    BeforeAll {
        function docker { }
    }
    BeforeEach {
        $script:commands = [Collections.Generic.List[string]]::new()
        Mock docker { $script:commands.Add((@($args | ForEach-Object { $_ }) -join ' ')); $global:LASTEXITCODE = 0 }
    }
    It 'preserves containers on ordinary <flavor> stop and selects the worker profile for volume cleanup' -ForEach @(@{ flavor = 'local' }, @{ flavor = 'published' }) {
        $path = Join-Path $PSScriptRoot "../start-$flavor-dms.ps1"
        $ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)
        $node = $ast.Find({ param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Extent.Text.StartsWith('if ($CdcKafkaInfrastructure -and -not $v)') }, $true)
        $block = [scriptblock]::Create($node.Extent.Text)
        # Variables are inputs to the actual executable primitive block, not copied commands.
        & {
            param($block)
            $CdcKafkaInfrastructure = $true
            $files = @('-f', 'kafka-cdc.yml', '-f', '/custom/broker-size.json')
            $EnvironmentFile = '/selected/.env'
            $downArgs = @('--remove-orphans', '-v')
            $v = $false
            & $block
            $v = $true
            & $block
        } $block
        $script:commands[0] | Should -Match "--env-file /selected/.env -p dms-$flavor --profile cdc-managed-worker stop$"
        $script:commands[1] | Should -Match "--env-file /selected/.env -p dms-$flavor --profile cdc-managed-worker down --remove-orphans -v$"
        $script:commands[0] | Should -Match '/custom/broker-size.json'
    }
}

Describe 'Live worker REST evidence shape' {
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot '../cdc-lifecycle.psm1') -Force
    }
    AfterAll { Remove-Module cdc-lifecycle -Force }
    It 'rejects unknown inventory instead of proving empty (<body>)' -ForEach @(
        @{ body = 'null' }, @{ body = '{}' }, @{ body = '""' }, @{ body = '[null]' }, @{ body = '[{}]' }
    ) {
        $script:body = $body
        Mock -ModuleName cdc-lifecycle Invoke-WebRequest { @{ Content = $script:body } }
        { & (Get-Module cdc-lifecycle) { Invoke-CdcLifecycleRest @{ ConnectEndpoint = 'http://localhost:8083/' } 'connectors' } } | Should -Throw '*inventory is unavailable*'
    }
    It 'accepts an authoritative empty connector array' {
        Mock -ModuleName cdc-lifecycle Invoke-WebRequest { @{ Content = '[]' } }
        $names = @(& (Get-Module cdc-lifecycle) { Invoke-CdcLifecycleRest @{ ConnectEndpoint = 'http://localhost:8083/' } 'connectors' })
        $names.Count | Should -Be 0
    }
}

Describe 'Lifecycle ownership across a real child script' {
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot '../cdc-lifecycle.psm1') -Force
        $script:child = Join-Path $TestDrive 'child.ps1'
        @'
param([switch]$Fail)
Import-Module "$PSScriptRoot/cdc-lifecycle.psm1"
if (-not (Test-CdcInfrastructureInvocation)) { throw 'Lost lifecycle ownership' }
if ($Fail) { throw 'Child failure' }
$global:LASTEXITCODE = 0
'@ | Set-Content $script:child
        Copy-Item (Join-Path $PSScriptRoot '../cdc-lifecycle.psm1') (Join-Path $TestDrive 'cdc-lifecycle.psm1')
        # Use the same module path in the child as the owning invocation, just like real primitives.
        Import-Module (Join-Path $TestDrive 'cdc-lifecycle.psm1') -Force
    }
    AfterAll { Remove-Module cdc-lifecycle -Force }

    It 'preserves lifecycle ownership through the child and ends it after return' {
        Test-CdcInfrastructureInvocation | Should -BeFalse
        Invoke-CdcInfrastructure -StartScript $script:child -Parameters @{}
        Test-CdcInfrastructureInvocation | Should -BeFalse
    }

    It 'ends ownership after a child failure' {
        { Invoke-CdcInfrastructure -StartScript $script:child -Parameters @{ Fail = $true } } | Should -Throw '*Child failure*'
        Test-CdcInfrastructureInvocation | Should -BeFalse
    }
}
