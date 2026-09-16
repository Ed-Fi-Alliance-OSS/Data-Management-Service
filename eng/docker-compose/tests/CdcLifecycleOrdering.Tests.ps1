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
        Copy-Item (Join-Path $PSScriptRoot '../*.psm1') $script:root
        Copy-Item (Join-Path $PSScriptRoot '../*.yml') $script:root
        Copy-Item (Join-Path $PSScriptRoot '../../schema-package-utility.psm1') $TestDrive
        @'
function Resolve-BootstrapSchemaWorkspace { @{ CoreSchemaPath = '/staged/core.json'; ExtensionSchemaPaths = @() } }
Export-ModuleMember -Function Resolve-BootstrapSchemaWorkspace
'@ | Set-Content (Join-Path $script:root 'bootstrap-schema-workspace.psm1')
        Import-Module (Join-Path $script:root 'bootstrap-cdc.psm1') -Force
        Import-Module (Join-Path $script:root 'bootstrap-manifest.psm1') -Force
        Import-Module (Join-Path $script:root 'e2e-cdc.psm1') -Force
        Import-Module (Join-Path $script:root 'e2e-teardown.psm1') -Force
        Import-Module (Join-Path $script:root 'cdc-lifecycle.psm1') -Force
        $script:realCommand = & (Get-Module cdc-lifecycle) { (Get-Command Invoke-CdcLifecycleCommand).ScriptBlock }
        $script:realRest = & (Get-Module cdc-lifecycle) { (Get-Command Invoke-CdcLifecycleRest).ScriptBlock }
        function New-TestHandoff {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Creates isolated test files only.')]
            param($id, $project = 'dms-local', $provider = 'postgresql', $identityProvider = 'self-contained')
            $settings = @{
                AppSettings = @{ Datastore = $provider }
                DataManagement = @{ DocumentCache = @{ Targets = @(@{ DataStoreId = $id }) } }
                Cdc = @{
                    Compose = @{ Project = $project; EnvironmentFile = (Join-Path $script:root '.env.custom'); File = (Join-Path $script:root 'kafka-cdc.yml'); BrokerSizeOverrideFile = (Join-Path $script:root 'shared/broker-size.json') }
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
            return @{ IdentityProvider = $identityProvider; Settings = $settings; SettingsPath = $settingsPath; DmsComposePath = $dmsPath; EnableKafkaUI = $true; EnableSwaggerUI = $true }
        }
        function New-TestBootstrapHandoff {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Creates isolated test bootstrap files only.')]
            param($id = 42, $project = 'dms-local', $state = (Join-Path $script:root '.cdc-state'))
            $sourceHandoff = New-TestHandoff -id $id -project $project
            $sourceHandoff.Settings.ConfigurationServiceSettings = @{ BaseUrl = 'http://localhost:8081' }
            New-BootstrapCdcHandoff -Settings $sourceHandoff.Settings -InputSettingsPath $sourceHandoff.SettingsPath -StatePath $state `
                -EnvironmentFile (Join-Path $script:root '.env.custom') -Project $project -DatabaseName 'dedicated-cdc' -IdentityProvider self-contained
        }
        function Read-TestDeployment($project = 'dms-local') {
            Get-Content (Join-Path $script:root ".cdc-deployments/$project.json") -Raw | ConvertFrom-Json -AsHashtable
        }
        function Invoke-TestLifecycle($parameters, $project = 'dms-local') {
            Invoke-CdcDeploymentLifecycle -Project $project -StartScript "/compose/start-$project.ps1" -Parameters $parameters
        }
        function Invoke-TestPreparationStartup($flavor, $parameters) {
            $path = Join-Path $PSScriptRoot "../start-$flavor-dms.ps1"
            $ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)
            $nodes = $ast.FindAll({
                param($n)
                return ($n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Extent.Text -eq '$upArgs = @("--detach")') -or
                    ($n -is [Management.Automation.Language.IfStatementAst] -and $n.Extent.Text.StartsWith('if (-not $databaseOnlyStartup -and -not $DmsOnly'))
            }, $true)
            $nodes.Count | Should -Be 2
            $databaseOnlyStartup = [bool]$parameters['DbOnly']
            $DmsOnly = $false
            $CdcDatabaseInfrastructure = [bool]$parameters['CdcDatabaseInfrastructure']
            $files = @('-f', "$flavor-dms.yml")
            $EnvironmentFile = $parameters.EnvironmentFile
            $commands = [Collections.Generic.List[string]]::new()
            function docker { $commands.Add((@($args | ForEach-Object { $_ }) -join ' ')); $global:LASTEXITCODE = 0 }
            . ([scriptblock]::Create(($nodes.Extent.Text -join "`n")))
            # Execute the actual database/CMS commands with the production arguments, using
            # the database-only command for DbOnly and the full-start command for its control.
            $services = if ($databaseOnlyStartup) { @('db') } elseif ($parameters['InfraOnly']) { @('db', 'config') } else { @('db', '') }
            foreach ($service in $services) {
                $text = 'docker compose $files --env-file $EnvironmentFile -p dms-' + $flavor + ' up $upArgs'
                if ($service) { $text += " $service" }
                $calls = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.Extent.Text -eq $text }, $true))
                $calls.Count | Should -BeGreaterThan 0
                $call = if ($databaseOnlyStartup) { $calls[0] } else { $calls[-1] }
                . ([scriptblock]::Create($call.Extent.Text))
            }
            return $commands.ToArray()
        }
        function Get-TestComposeModel($flavor, $parameters) {
            $composeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
            Import-Module (Join-Path $composeRoot 'env-utility.psm1') -Scope Local
            $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $composeRoot "start-$flavor-dms.ps1"), [ref]$null, [ref]$null)
            # Execute the complete production file-selection span, including the retained override.
            $first = $ast.Find({ param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Extent.Text.StartsWith('$databaseComposeFile =') }, $true)
            $last = $ast.Find({ param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Extent.Text.StartsWith('if ($CdcBrokerSizeOverrideFile') }, $true)
            $first | Should -Not -BeNullOrEmpty
            $last | Should -Not -BeNullOrEmpty
            $selection = $ast.Extent.Text.Substring($first.Extent.StartOffset, $last.Extent.EndOffset - $first.Extent.StartOffset)
            $CdcDatabaseInfrastructure = $CdcKafkaInfrastructure = $EnableKafkaUI = $EnableKafka = $d = $false
            $InfraOnly = $EnableSwaggerUI = $usePostgresqlTmpfs = $databaseOnlyStartup = $bootstrapMode = $false
            $CdcBrokerSizeOverrideFile = ''
            $cmsIncludedInComposeSet = $true
            $envValues = @{}
            foreach ($key in $parameters.Keys) { Set-Variable $key $parameters[$key] }
            . ([scriptblock]::Create($selection)) | Out-Null
            $arguments = @('compose', '--project-directory', $composeRoot)
            for ($i = 0; $i -lt $files.Count; $i += 2) {
                $path = $files[$i + 1]
                if (-not [IO.Path]::IsPathRooted($path)) { $path = Join-Path $composeRoot $path }
                $arguments += @('-f', $path)
            }
            $arguments += @('--env-file', $parameters.EnvironmentFile, 'config', '--format', 'json')
            $docker = (Get-Command docker -CommandType Application | Select-Object -First 1).Source
            $json = & $docker @arguments
            $LASTEXITCODE | Should -Be 0
            return $json | ConvertFrom-Json -AsHashtable
        }
    }
    BeforeEach {
        Remove-Item (Join-Path $script:root '.cdc-deployments') -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $script:root '.bootstrap') -Recurse -Force -ErrorAction SilentlyContinue
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
    AfterAll {
        Get-Module -All | Where-Object { $_.Path -and $_.Path.StartsWith($script:root + '/') } | Remove-Module -Force
    }

    Context 'Retained Compose interpolation inputs' {
        BeforeEach {
            $script:savedInputs = @{}
            foreach ($name in @('KAFKA_PORT', 'CDC_DATABASE_PASSWORD', 'DOCKER_LOG_MAX_FILE', 'DOCKER_LOG_MAX_SIZE',
                'T74_NESTED', 'T74_UNRELATED', 'COMPOSE_PROFILES', 'COMPOSE_PARALLEL_LIMIT')) {
                $script:savedInputs[$name] = [Environment]::GetEnvironmentVariable($name)
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }
            Remove-Item (Join-Path $script:root '.cdc-deployments') -Recurse -Force
            $env:KAFKA_PORT = '19092'
            $env:CDC_DATABASE_PASSWORD = 'retained-$-sentinel'
            $env:DOCKER_LOG_MAX_FILE = ''
            $env:T74_NESTED = 'nested-original'
            @'
KAFKA_PORT=29092
CDC_DATABASE_PASSWORD=env-file-sentinel
DOCKER_LOG_MAX_SIZE=${T74_NESTED}
'@ | Set-Content (Join-Path $script:root '.env.custom')
            $script:inputChecks = 0
        }
        AfterEach {
            foreach ($name in $script:savedInputs.Keys) {
                if ($null -eq $script:savedInputs[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
                else { [Environment]::SetEnvironmentVariable($name, $script:savedInputs[$name]) }
            }
            Copy-Item (Join-Path $PSScriptRoot '../*.yml') $script:root -Force
            Remove-Item (Join-Path $script:root 'unrelated-t74.yml') -ErrorAction SilentlyContinue
        }
        BeforeAll {
            function Assert-TestRetainedInput {
                # Boolean assertions keep even sentinel credential values out of failure output.
                ($env:KAFKA_PORT -ceq '19092') | Should -BeTrue
                ($env:CDC_DATABASE_PASSWORD -ceq 'retained-$-sentinel') | Should -BeTrue
                (Test-Path Env:DOCKER_LOG_MAX_FILE) | Should -BeTrue
                ($env:DOCKER_LOG_MAX_FILE -ceq '') | Should -BeTrue
                (Test-Path Env:DOCKER_LOG_MAX_SIZE) | Should -BeFalse
                ($env:T74_NESTED -ceq 'nested-original') | Should -BeTrue
                (Test-Path Env:COMPOSE_PROFILES) | Should -BeFalse
                (Test-Path Env:COMPOSE_PARALLEL_LIMIT) | Should -BeFalse
                $script:inputChecks++
            }
            function Set-TestChangedShell {
                [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Changes only isolated test process inputs, restored by AfterEach.')]
                param()
                $env:KAFKA_PORT = '39092'
                $env:CDC_DATABASE_PASSWORD = 'ambient-sentinel'
                Remove-Item Env:DOCKER_LOG_MAX_FILE
                $env:DOCKER_LOG_MAX_SIZE = ''
                $env:T74_NESTED = 'nested-changed'
                $env:COMPOSE_PROFILES = 'unrelated-profile'
                $env:COMPOSE_PARALLEL_LIMIT = '1'
                $env:T74_UNRELATED = 'unrelated'
            }
            function Assert-TestRestoredShell {
                ($env:KAFKA_PORT -ceq '39092') | Should -BeTrue
                ($env:CDC_DATABASE_PASSWORD -ceq 'ambient-sentinel') | Should -BeTrue
                (Test-Path Env:DOCKER_LOG_MAX_FILE) | Should -BeFalse
                (Test-Path Env:DOCKER_LOG_MAX_SIZE) | Should -BeTrue
                ($env:DOCKER_LOG_MAX_SIZE -ceq '') | Should -BeTrue
                ($env:T74_NESTED -ceq 'nested-changed') | Should -BeTrue
                ($env:COMPOSE_PROFILES -ceq 'unrelated-profile') | Should -BeTrue
                ($env:COMPOSE_PARALLEL_LIMIT -ceq '1') | Should -BeTrue
                ($env:T74_UNRELATED -ceq 'unrelated') | Should -BeTrue
            }
        }

        It 'uses original inputs for <project>/<provider> despite unrelated checkout and shell changes' -ForEach @(
            @{ project = 'dms-local'; provider = 'postgresql' }, @{ project = 'dms-local'; provider = 'mssql' },
            @{ project = 'dms-published'; provider = 'postgresql' }, @{ project = 'dms-published'; provider = 'mssql' }
        ) {
            Register-CdcDeploymentHandoff (New-TestHandoff -id 42 -project $project -provider $provider) (Join-Path $script:root 'state')
            $script:live = @('connector-42')
            'services: { unrelated: { image: "${T74_UNRELATED}" } }' | Set-Content (Join-Path $script:root 'unrelated-t74.yml')
            $otherProvider = if ($provider -eq 'mssql') { 'postgresql' } else { 'mssql' }
            $otherFlavor = if ($project -eq 'dms-local') { 'published' } else { 'local' }
            foreach ($file in @("$otherProvider.yml", "$otherFlavor-dms.yml")) {
                Add-Content (Join-Path $script:root $file) '# ${T74_UNRELATED}'
            }
            Set-TestChangedShell
            Mock -ModuleName cdc-lifecycle Invoke-CdcLifecycleCommand {
                Assert-TestRetainedInput
                $Entry.ConnectorName = 'connector-42'
                if ($Operation -eq 'retire') { $script:live = @() }
            }
            Mock -ModuleName cdc-lifecycle Invoke-CdcInfrastructure {
                Assert-TestRetainedInput
                ($Parameters.EnvironmentFile -ceq (Join-Path $script:root '.env.custom')) | Should -BeTrue
            }
            Invoke-TestLifecycle @{ d = $true } $project
            Assert-TestRestoredShell
            Invoke-TestLifecycle @{ d = $true; v = $true } $project
            Assert-TestRestoredShell
            $script:inputChecks | Should -Be 4
            Test-CdcDeployment $project | Should -BeFalse
        }

        It 'reproduces original Compose interpolation through the actual retained infrastructure boundary' {
            Register-CdcDeploymentHandoff (New-TestHandoff 42) (Join-Path $script:root 'state')
            $script:live = @('connector-42')
            Set-TestChangedShell
            Mock -ModuleName cdc-lifecycle Invoke-CdcInfrastructure {
                Assert-TestRetainedInput
                $docker = (Get-Command docker -CommandType Application | Select-Object -First 1).Source
                $model = & $docker compose -f (Join-Path $script:root 'kafka-cdc.yml') --env-file $Parameters.EnvironmentFile -p dms-local --profile cdc-managed-worker config --format json 2>$null |
                    ConvertFrom-Json -AsHashtable
                $LASTEXITCODE | Should -Be 0
                ($model.services.kafka.environment.KAFKA_ADVERTISED_LISTENERS -clike '*:19092') | Should -BeTrue
                # Compose escapes literal dollars in its round-trippable config output.
                ($model.services.'kafka-cdc-worker'.environment.CDC_DATABASE_PASSWORD -ceq 'retained-$$-sentinel') | Should -BeTrue
                ($model.services.kafka.logging.options.'max-size' -ceq 'nested-original') | Should -BeTrue
                $script:workerRunning = $false
            }
            Invoke-TestLifecycle @{ d = $true }
            Assert-TestRestoredShell
            $script:inputChecks | Should -Be 1
        }

        It 'restores absent, empty and populated inputs after <boundary> failure' -ForEach @(
            @{ boundary = 'controller' }, @{ boundary = 'infrastructure' }, @{ boundary = 'admitted-host' }
        ) {
            Register-CdcDeploymentHandoff (New-TestHandoff 42) (Join-Path $script:root 'state')
            $script:live = @('connector-42')
            Set-TestChangedShell
            $script:failedBoundary = $boundary
            Mock -ModuleName cdc-lifecycle Invoke-CdcLifecycleCommand {
                Assert-TestRetainedInput
                if ($script:failedBoundary -eq 'controller') { throw 'Controlled failure' }
                $Entry.ConnectorName = 'connector-42'
            }
            Mock -ModuleName cdc-lifecycle Invoke-CdcInfrastructure {
                Assert-TestRetainedInput
                throw 'Controlled failure'
            }
            if ($boundary -eq 'admitted-host') {
                { Invoke-CdcAdmittedHost -Project dms-local -StartScript '/unused' -Parameters @{} } | Should -Throw '*Controlled failure*'
            }
            else { { Invoke-TestLifecycle @{ d = $true } } | Should -Throw '*Controlled failure*' }
            Assert-TestRestoredShell
            $script:inputChecks | Should -BeGreaterThan 0
            # Lock is also released on the failure path.
            $lock = & (Get-Module cdc-lifecycle) { Enter-CdcDeploymentLock 'dms-local' }
            $lock.Dispose()
        }

        It 'applies retained inputs to worker startup, guarded resume and admitted DMS' {
            Register-CdcDeploymentHandoff (New-TestHandoff 42) (Join-Path $script:root 'state')
            $script:live = @('connector-42')
            Invoke-TestLifecycle @{ d = $true }
            Set-TestChangedShell
            Mock -ModuleName cdc-lifecycle Invoke-CdcLifecycleCommand {
                Assert-TestRetainedInput
                $Entry.ConnectorName = 'connector-42'
            }
            Mock -ModuleName cdc-lifecycle Invoke-CdcInfrastructure { Assert-TestRetainedInput }
            Invoke-TestLifecycle @{}
            Assert-TestRestoredShell
            Invoke-CdcAdmittedHost -Project dms-local -StartScript '/unused' -Parameters @{}
            Assert-TestRestoredShell
            $script:inputChecks | Should -Be 5
        }

        It 'rejects <fault> before controller or infrastructure effects' -ForEach @(
            @{ fault = 'changed-input' }, @{ fault = 'missing-inputs' }, @{ fault = 'missing-hash' },
            @{ fault = 'legacy-hash-only' }, @{ fault = 'scope' }, @{ fault = 'selected-file' }
        ) {
            Register-CdcDeploymentHandoff (New-TestHandoff 42) (Join-Path $script:root 'state')
            $path = Join-Path $script:root '.cdc-deployments/dms-local.json'
            $deployment = Read-TestDeployment
            switch ($fault) {
                'changed-input' { $deployment.ComposeInputs.ProcessEnvironment.CDC_DATABASE_PASSWORD = 'tampered-sentinel' }
                'missing-inputs' { $deployment.Remove('ComposeInputs') }
                'missing-hash' { $deployment.Remove('ComposeInputsHash') }
                'legacy-hash-only' {
                    $deployment.Remove('ComposeInputs')
                    $deployment.Remove('ComposeInputsHash')
                    $deployment.ComposeEnvironmentHash = 'legacy-cannot-reconstruct-original-values'
                }
                'scope' {
                    $deployment.ComposeInputs.Project = 'dms-published'
                    $deployment.ComposeInputsHash = & (Get-Module cdc-lifecycle) { param($inputs) Get-CdcComposeInputHash $inputs } $deployment.ComposeInputs
                }
                'selected-file' { Add-Content (Join-Path $script:root 'kafka-cdc.yml') '# changed selected configuration' }
            }
            $deployment | ConvertTo-Json -Depth 64 -Compress | Set-Content $path
            Set-TestChangedShell
            { Invoke-TestLifecycle @{ d = $true; v = $true } } | Should -Throw '*inventory*'
            Assert-TestRestoredShell
            $script:trace.Count | Should -Be 0
            (Get-Content $path -Raw).Contains('ambient-sentinel') | Should -BeFalse
        }

        It 'retains only selected optional service inputs and rejects uncaptured additions' {
            $handoff = New-TestHandoff 42
            $handoff.EnableKafkaUI = $handoff.EnableSwaggerUI = $false
            Register-CdcDeploymentHandoff $handoff (Join-Path $script:root 'state')
            $deployment = Read-TestDeployment
            $paths = @($deployment.ComposeInputs.Files | ForEach-Object { [IO.Path]::GetFileName($_.Path) })
            $paths | Should -Not -Contain 'kafka-ui.yml'
            $paths | Should -Not -Contain 'swagger-ui.yml'
            Add-Content (Join-Path $script:root 'kafka-ui.yml') '# unrelated optional service'
            { Invoke-TestLifecycle @{ d = $true; EnableKafkaUI = $true } } | Should -Throw '*original optional service inputs*'
            $script:trace.Count | Should -Be 0
            $script:live = @('connector-42')
            Invoke-TestLifecycle @{ d = $true; v = $true }
            Test-CdcDeployment dms-local | Should -BeFalse
        }
    }

    if ($env:DMS_T57_BRIDGE) {
        It 'uses the production controller session bridge for retained wrapper startup' {
            # The existing command fixture answers the native tool boundary through temporary files.
            # Its real CdcCommandRunner/CdcWorkerStartup own all authorization and Kafka decisions.
            $bridge = Get-Content $env:DMS_T57_BRIDGE -Raw | ConvertFrom-Json -AsHashtable
            $project = if ($bridge.Live) { $bridge.Project } else { "dms-$($bridge.Flavor)" }
            Remove-Item (Join-Path $script:root '.cdc-deployments') -Recurse -Force
            $handoff = New-TestHandoff -id ([int]$bridge.Binding.DataStoreId) -project $project -provider $bridge.Provider -identityProvider $bridge.IdentityProvider
            $flat = Get-Content $bridge.SettingsPath -Raw | ConvertFrom-Json -AsHashtable
            foreach ($key in $flat.Keys) {
                $parts = $key.Split(':')
                $current = $handoff.Settings
                for ($i = 0; $i -lt $parts.Count - 1; $i++) {
                    if (-not $current.ContainsKey($parts[$i])) { $current[$parts[$i]] = @{} }
                    $current = $current[$parts[$i]]
                }
                $current[$parts[-1]] = $flat[$key]
            }
            $handoff.Settings.Cdc.Compose.Project = $project
            if (-not $bridge.Live) {
                # Retain the fixture's copied Compose inputs instead of the command fixture's unused paths.
                $handoff.Settings.Cdc.Compose.EnvironmentFile = Join-Path $script:root '.env.custom'
                $handoff.Settings.Cdc.Compose.File = Join-Path $script:root 'kafka-cdc.yml'
            }
            $handoff.Settings.Cdc.Worker.Key = $bridge.WorkerKey
            $handoff.Settings.Cdc.Worker.OffsetStorageTopic = $bridge.OffsetTopic
            $handoff.Settings | ConvertTo-Json -Depth 64 | Set-Content $handoff.SettingsPath
            if ($bridge.Live) {
                # Isolate the qualification project's inventory, retaining the production handoff/lifecycle logic.
                Mock -ModuleName cdc-lifecycle Get-CdcDeploymentPath { Join-Path $script:root ".cdc-deployments/$Project.json" }
            }
            if ($bridge.Live) {
                $handoff.InputSettingsHash = (Get-FileHash $bridge.SettingsPath).Hash
                $databaseKey = if ($bridge.Provider -eq 'postgresql') { 'database.dbname' } else { 'database.names' }
                $databaseName = $handoff.Settings.Cdc.ProviderConnectionProperties[$databaseKey]
                $handoff.DatabaseNameHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($databaseName)))
                Register-CdcDeploymentHandoff -Handoff $handoff -StatePath $bridge.StateRoot -Receipt $bridge.Receipt
                [IO.File]::WriteAllText("$env:DMS_T57_BRIDGE.request", '["prepare"]')
                $deadline = [DateTime]::UtcNow.AddSeconds(90)
                while (-not (Test-Path "$env:DMS_T57_BRIDGE.response")) {
                    if ([DateTime]::UtcNow -ge $deadline) { throw 'Initial provider failure was not injected after handoff' }
                    Start-Sleep -Milliseconds 10
                }
                Remove-Item "$env:DMS_T57_BRIDGE.response"
            }
            else { Register-CdcDeploymentHandoff -Handoff $handoff -StatePath $bridge.StateRoot }
            & (Get-Module cdc-lifecycle) {
                param($project, $name, $cleanup)
                $deployment = Read-CdcDeployment $project
                $deployment.Phase = if ($cleanup) { 'Transition' } else { 'Stopped' }
                $deployment.Entries[0].ConnectorName = $name
                Write-CdcDeployment $project $deployment
            } $project $bridge.Binding.ConnectorName $bridge.Cleanup
            $script:workerRunning = $false
            $script:live = if ($bridge.Cleanup) { @() } else { @($bridge.Binding.ConnectorName) }
            $script:trace.Clear()
            $script:liveBridge = $bridge
            $script:bridgeProject = $project
            $script:bridgeFlavor = $bridge.Flavor
            $script:bridgeEngine = $bridge.Provider
            $script:bridgeIdentity = $bridge.IdentityProvider
            $script:bridgeEntryPath = $handoff.SettingsPath
            $script:bridgeTool = Join-Path $script:root 'tool.ps1'
            @'
param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
$request = "$env:DMS_T57_BRIDGE.request"
$response = "$env:DMS_T57_BRIDGE.response"
[IO.File]::WriteAllText("$request.tmp", ($Arguments | ConvertTo-Json -Compress))
[IO.File]::Move("$request.tmp", $request)
$deadline = [DateTime]::UtcNow.AddSeconds(30)
while (-not (Test-Path $response)) {
    if ([DateTime]::UtcNow -ge $deadline) { exit 1 }
    Start-Sleep -Milliseconds 10
}
$result = [IO.File]::ReadAllText($response)
[IO.File]::Delete($response)
$result
exit ([int]($result | ConvertFrom-Json).exitCode)
'@ | Set-Content $script:bridgeTool
            if ($bridge.Live) {
                @'
param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
$bridge = Get-Content $env:DMS_T57_BRIDGE -Raw | ConvertFrom-Json -AsHashtable
$result = @(& $bridge.ToolPath @Arguments 2>&1)
$code = $LASTEXITCODE
[IO.File]::WriteAllText("$env:DMS_T57_BRIDGE.command", ($result -join "`n"))
$result
exit $code
'@ | Set-Content $script:bridgeTool
                Mock -ModuleName cdc-lifecycle Invoke-CdcLifecycleRest {
                    $script:trace.Add("rest:$Path")
                    & (Get-Module cdc-lifecycle) $script:realRest $Deployment $Path
                }
            }
            @'
function Resolve-DmsSchemaTool { Join-Path $PSScriptRoot 'tool.ps1' }
Export-ModuleMember -Function Resolve-DmsSchemaTool
'@ | Set-Content (Join-Path $script:root 'bootstrap-schema-tool.psm1')
            Mock -ModuleName cdc-lifecycle Invoke-CdcLifecycleCommand {
                try { & (Get-Module cdc-lifecycle) $script:realCommand $Entry $Operation }
                catch {
                    if ($script:liveBridge.Live) { Write-Information (Get-Content "$env:DMS_T57_BRIDGE.command" -Raw) -InformationAction Continue }
                    throw
                }
                $script:trace.Add("$Operation`:$($Entry.DataStoreId)")
                if ($Operation -in @('start-worker', 'retire')) { $script:workerRunning = $true; 'present' | Set-Content (Join-Path $script:resources 'worker') }
            }
            Mock -ModuleName cdc-lifecycle Invoke-CdcInfrastructure {
                $Parameters.IdentityProvider | Should -Be $script:bridgeIdentity
                if ($Parameters['d']) {
                    (Read-TestDeployment $script:bridgeProject).Phase | Should -Be 'Retired'
                    $script:trace | Should -Contain 'rest:connectors'
                    $script:trace.Add('down-volumes')
                    if ($script:liveBridge.Live) {
                        $docker = (Get-Command docker -CommandType Application | Select-Object -First 1).Source
                        & $docker compose -f $script:liveBridge.ComposeFile --env-file $script:liveBridge.EnvironmentFile -p $script:bridgeProject --profile cdc-managed-worker down -v | Out-Null
                        $LASTEXITCODE | Should -Be 0
                        & $docker rm -f -v $script:liveBridge.ProviderContainer | Out-Null
                        $LASTEXITCODE | Should -Be 0
                        $remaining = & $docker volume ls --filter "label=com.docker.compose.project=$script:bridgeProject" -q
                        $remaining | Should -BeNullOrEmpty
                    }
                    Get-ChildItem $script:resources | Remove-Item -Force
                    return
                }
                if ($Parameters.DmsOnly) {
                    (Read-TestDeployment "dms-$script:bridgeFlavor").Phase | Should -Be 'Active'
                    $script:trace.Add('dms')
                    return
                }
                $Parameters.SeparateConfigDatabase | Should -BeTrue
                $script:trace.Add('database-infra')
                # Execute both actual provider/Compose selection and the old first-broker effect.
                # No test pre-starts either Kafka or Connect.
                $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot "../start-$script:bridgeFlavor-dms.ps1"), [ref]$null, [ref]$null)
                $blocks = @('CDC database preparation requires', 'CDC infrastructure startup requires', 'Starting CDC broker') | ForEach-Object {
                    $marker = $_
                    $node = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.IfStatementAst] -and ($n.Extent.Text.StartsWith('if ($CdcDatabaseInfrastructure)') -or $n.Extent.Text.StartsWith('if ($CdcKafkaInfrastructure)')) }, $true) |
                        Where-Object { $_.Extent.Text.Contains($marker) } | Select-Object -First 1
                    [scriptblock]::Create($node.Extent.Text)
                }
                & {
                    param($parameters, $blocks)
                    $CdcDatabaseInfrastructure = $CdcKafkaInfrastructure = $EnableKafkaUI = $EnableKafka = $d = $false
                    $upArgs = @('-d')
                    foreach ($key in $parameters.Keys) { Set-Variable $key $parameters[$key] }
                    $files = @()
                    $enableKafkaInfrastructure = $false
                    foreach ($block in $blocks) { . $block | Out-Null }
                    $files | Should -Not -Contain 'kafka-cdc.yml'
                    ($files -contains 'mssql-cdc.yml') | Should -Be ($DatabaseEngine -eq 'mssql')
                } $Parameters $blocks
                $Parameters.CdcDatabaseInfrastructure | Should -BeTrue
                $Parameters.CdcKafkaInfrastructure | Should -Not -BeTrue
                $Parameters.EnableKafkaUI | Should -Not -BeTrue
            }
            function docker { throw 'Wrapper launched Kafka outside the controller startup session' }
            Import-Module (Join-Path $script:root 'bootstrap-cdc.psm1') -Force
            Mock -ModuleName bootstrap-cdc docker {
                (@($args) -join ' ') | Should -Match 'up --detach --no-deps kafka-ui$'
                $script:workerRunning | Should -BeTrue
                $script:trace.Add('ui')
                $global:LASTEXITCODE = 0
            }
            if ($bridge.Cleanup) {
                Remove-Item (Join-Path $script:resources 'worker') -Force
                if ($bridge.RetryCleanup) {
                    { Invoke-TestLifecycle @{ d = $true; v = $true } $project } | Should -Throw '*unverified evidence*'
                    (Read-TestDeployment $project).Phase | Should -Be 'Retiring'
                    $script:trace | Should -Not -Contain 'down-volumes'
                }
                Invoke-TestLifecycle @{ d = $true; v = $true } $project
                $script:trace[-1] | Should -Be 'down-volumes'
                $script:trace | Should -Not -Contain 'database-infra'
                $script:trace | Should -Not -Contain 'start-worker'
                Test-Path (Join-Path $script:root ".cdc-deployments/$project.json") | Should -BeFalse
                Get-ChildItem $script:resources | Should -BeNullOrEmpty
            }
            elseif ($bridge.Reject) {
                { Invoke-TestLifecycle @{ EnableKafkaUI = $true } $project } | Should -Throw '*unverified evidence*'
                $script:trace | Should -Be @('database-infra')
                (Read-TestDeployment $project).Phase | Should -Be 'Transition'
            }
            elseif ($bridge.CatchUpTimeout) {
                { Invoke-TestLifecycle @{ EnableKafkaUI = $true } $project } | Should -Throw '*unverified evidence*'
                $script:trace | Should -Not -Contain 'dms'
                (Read-TestDeployment $project).Phase | Should -Be 'Transition'
            }
            else {
                Invoke-TestLifecycle @{ EnableKafkaUI = $true } $project
                $script:trace | Should -Contain 'ui'
                $script:trace[-1] | Should -Be 'dms'
                (Read-TestDeployment $project).Phase | Should -Be 'Active'
            }
        }
    }

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
            $retained.IdentityProvider | Should -Be $handoff.IdentityProvider
            $retained.SettingsPath | Should -Be $handoff.SettingsPath
            $retained.DmsComposePath | Should -Be $handoff.DmsComposePath
            $retained.Receipt.WorkflowId | Should -Be $receipt.WorkflowId
            $retained.EnvironmentFile | Should -Be $handoff.Settings.Cdc.Compose.EnvironmentFile
            (Get-Content (Join-Path $script:root '.cdc-deployments/dms-local.json') -Raw) | Should -Be $original
        }
    }

    It 'rejects a <change> identity selection before any lifecycle effects' -ForEach @(
        @{ change = 'missing' }, @{ change = 'unsupported' }, @{ change = 'peer-missing' }, @{ change = 'peer-conflict' }
    ) {
        $deployment = Read-TestDeployment
        switch ($change) {
            'missing' { $deployment.Remove('IdentityProvider') }
            'unsupported' { $deployment.IdentityProvider = 'other' }
            'peer-missing' { $deployment.Entries[0].Remove('IdentityProvider') }
            'peer-conflict' { $deployment.Entries[0].IdentityProvider = 'keycloak' }
        }
        $deployment | ConvertTo-Json -Depth 64 | Set-Content (Join-Path $script:root '.cdc-deployments/dms-local.json')
        { Invoke-TestLifecycle @{ d = $true } } | Should -Throw '*IdentityProvider*Restore the original complete deployment inventory*'
        $script:trace.Count | Should -Be 0
    }

    It 'rejects missing or conflicting provider in an incoming peer (<selection>)' -ForEach @(
        @{ selection = '' }, @{ selection = 'keycloak' }
    ) {
        $original = Get-Content (Join-Path $script:root '.cdc-deployments/dms-local.json') -Raw
        $peer = New-TestHandoff -id 44 -identityProvider $selection
        { Register-CdcDeploymentHandoff -Handoff $peer -StatePath (Join-Path $script:root 'peer-state') } | Should -Throw
        (Get-Content (Join-Path $script:root '.cdc-deployments/dms-local.json') -Raw) | Should -Be $original
        $script:trace.Count | Should -Be 0
    }

    It 'rejects a conflicting explicit provider for <operation> before effects' -ForEach @(
        @{ operation = 'start' }, @{ operation = 'stop' }, @{ operation = 'retire' }, @{ operation = 'admit' }
    ) {
        $parameters = @{ IdentityProvider = 'keycloak' }
        if ($operation -in @('stop', 'retire')) { $parameters.d = $true }
        if ($operation -eq 'retire') { $parameters.v = $true }
        if ($operation -eq 'admit') {
            { Invoke-CdcAdmittedHost -Project 'dms-local' -StartScript '/unused' -Parameters $parameters } | Should -Throw '*IdentityProvider conflicts*'
        }
        else { { Invoke-TestLifecycle $parameters } | Should -Throw '*IdentityProvider conflicts*' }
        $script:trace.Count | Should -Be 0
        (Read-TestDeployment).Phase | Should -Be 'Active'
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
            -not $Parameters.ContainsKey('CdcBrokerSizeOverrideFile') -and $Parameters.SuppressWriterGuidance
        }
    }
    It 'waits for managed <project> <operation> startup evidence: <scenario>' -ForEach @(
        foreach ($project in @('dms-local', 'dms-published')) {
            foreach ($operation in @('start', 'retire')) {
                foreach ($scenario in @('incomplete', 'status-missing', 'connection', 'persistent')) {
                    @{ project = $project; operation = $operation; scenario = $scenario }
                }
            }
        }
    ) {
        Remove-Item (Join-Path $script:root '.cdc-deployments') -Recurse -Force
        foreach ($id in @(42, 43)) {
            $handoff = New-TestHandoff -id $id -project $project
            # Entry 43 deliberately cannot issue a representable HTTP timeout. Startup must
            # read the first entry, which also owns the start-worker invocation.
            $handoff.Settings.Cdc.Timing = if ($id -eq 42) {
                @{ CallMilliseconds = 1000; WaitMilliseconds = 1400; PollMilliseconds = 20 }
            } else { @{ CallMilliseconds = 1; WaitMilliseconds = 1; PollMilliseconds = 1 } }
            $handoff.Settings | ConvertTo-Json -Depth 20 | Set-Content $handoff.SettingsPath
            Register-CdcDeploymentHandoff -Handoff $handoff -StatePath (Join-Path $script:root "custom-state-$id")
        }
        Invoke-TestLifecycle @{ d = $true } $project
        (Read-TestDeployment $project).Phase | Should -Be 'Stopped'
        $script:trace.Clear()
        $script:startupScenario = $scenario
        $script:inventoryPass = 0
        $script:statusMissing = $false
        Mock -ModuleName cdc-lifecycle Invoke-CdcLifecycleRest {
            $script:trace.Add("rest:$Path")
            & (Get-Module cdc-lifecycle) $script:realRest $Deployment $Path $StartupWait
        }
        Mock -ModuleName cdc-lifecycle Invoke-WebRequest {
            $path = $Uri.AbsolutePath.TrimStart('/')
            if ($path -eq 'connectors') {
                $script:inventoryPass++
                if ($script:startupScenario -eq 'connection' -and $script:inventoryPass -eq 1) {
                    throw [Net.Http.HttpRequestException]::new([Net.Http.HttpRequestError]::ConnectionError, 'sentinel-secret', $null, $null)
                }
                if ($script:startupScenario -eq 'persistent' -or ($script:startupScenario -eq 'incomplete' -and $script:inventoryPass -eq 1)) {
                    return @{ Content = '["connector-42"]' }
                }
                return @{ Content = ConvertTo-Json -InputObject @($script:live) -Compress }
            }
            if ($script:startupScenario -eq 'status-missing' -and -not $script:statusMissing -and $path -like '*connector-43/status') {
                $script:statusMissing = $true
                throw [Microsoft.PowerShell.Commands.HttpResponseException]::new('sentinel-secret', [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::NotFound))
            }
            return @{ Content = (@{ name = $path.Split('/')[1]; connector = @{ state = 'STOPPED' }; tasks = @() } | ConvertTo-Json -Depth 5) }
        }
        Mock -ModuleName cdc-lifecycle Invoke-BootstrapCdcKafkaUI { $script:trace.Add('ui') }
        $parameters = if ($operation -eq 'retire') { @{ d = $true; v = $true } } else { @{ EnableKafkaUI = $true } }
        if ($scenario -eq 'persistent') {
            { Invoke-TestLifecycle $parameters $project } | Should -Throw '*startup-readiness timed out*'
            (Read-TestDeployment $project).Phase | Should -Be 'Transition'
            $script:trace | Should -Not -Contain 'start:42'
            $script:trace | Should -Not -Contain 'retire:42'
            $script:trace | Should -Not -Contain 'dms'
            $script:trace | Should -Not -Contain 'down-volumes'
            # The consumed checkpoint cannot authorize an unmanaged second launch.
            if ($operation -eq 'start') { { Invoke-TestLifecycle $parameters $project } | Should -Throw '*complete verified shutdown*' }
        }
        else {
            Invoke-TestLifecycle $parameters $project
            if ($operation -eq 'start') {
                (Read-TestDeployment $project).Phase | Should -Be 'Active'
                $script:trace[-3..-1] | Should -Be @('start:42', 'start:43', 'dms')
            }
            else {
                Test-CdcDeployment $project | Should -BeFalse
                $script:trace[-4..-1] | Should -Be @('retire:42', 'retire:43', 'rest:connectors', 'down-volumes')
            }
            $lastStatus = $script:trace.LastIndexOf('rest:connectors/connector-43/status')
            $lastStatus | Should -BeGreaterThan 1
            $script:trace.IndexOf("$operation`:42") | Should -BeGreaterThan $lastStatus
        }
        $script:inventoryPass | Should -BeGreaterThan 1
        $script:trace[0..1] | Should -Be @('infra', 'start-worker:42')
        @($script:trace | Where-Object { $_ -eq 'start-worker:42' }).Count | Should -Be 1
        if ($operation -eq 'start') { $script:trace[2] | Should -Be 'ui' }
        Should -Invoke -ModuleName cdc-lifecycle Invoke-WebRequest -Times 0 -ParameterFilter { $OperationTimeoutSeconds -and ($TimeoutSec -le 0 -or $TimeoutSec -gt 1) }
    }
    It 'preserves the managed <project> Swagger handoff for <scenario>' -ForEach @(
        foreach ($project in @('dms-local', 'dms-published')) {
            foreach ($scenario in @('enabled', 'omitted', 'infra-only', 'controller-failure', 'unsupported')) {
                @{ project = $project; scenario = $scenario }
            }
        }
    ) {
        Remove-Item (Join-Path $script:root '.cdc-deployments') -Recurse -Force
        foreach ($id in @(42, 43)) {
            $handoff = New-TestHandoff -id $id -project $project
            $handoff.EnableSwaggerUI = $scenario -ne 'unsupported'
            Register-CdcDeploymentHandoff -Handoff $handoff -StatePath (Join-Path $script:root "custom-state-$id")
        }
        Invoke-TestLifecycle -parameters @{ d = $true } -project $project
        (Read-TestDeployment $project).Phase | Should -Be 'Stopped'
        $script:trace.Clear()
        $parameters = @{}
        if ($scenario -ne 'omitted') { $parameters.EnableSwaggerUI = $true }
        if ($scenario -eq 'infra-only') { $parameters.InfraOnly = $true }
        if ($scenario -eq 'controller-failure') { $script:failure = 'start:43' }

        if ($scenario -eq 'unsupported') {
            { Invoke-TestLifecycle -parameters $parameters -project $project } | Should -Throw '*original optional service inputs*'
            $script:trace.Count | Should -Be 0
            (Read-TestDeployment $project).Phase | Should -Be 'Stopped'
        }
        else {
            if ($scenario -eq 'controller-failure') {
                { Invoke-TestLifecycle -parameters $parameters -project $project } | Should -Throw '*Controller rejected*'
                (Read-TestDeployment $project).Phase | Should -Be 'Transition'
            }
            else {
                Invoke-TestLifecycle -parameters $parameters -project $project
                (Read-TestDeployment $project).Phase | Should -Be 'Active'
            }
            $expected = @('infra', 'start-worker:42', 'rest:connectors', 'rest:connectors/connector-42/status', 'rest:connectors/connector-43/status', 'start:42', 'start:43')
            if ($scenario -in @('enabled', 'omitted')) { $expected += 'dms' }
            $script:trace | Should -Be $expected
            Should -Invoke -ModuleName cdc-lifecycle Invoke-CdcInfrastructure -Times 1 -Exactly -ParameterFilter {
                $Parameters.InfraOnly -and -not $Parameters.DmsOnly
            }
        }

        if ($scenario -in @('enabled', 'omitted')) {
            Should -Invoke -ModuleName cdc-lifecycle Invoke-CdcInfrastructure -Times 1 -Exactly -ParameterFilter {
                $Parameters.DmsOnly -and -not $Parameters.InfraOnly -and $Parameters.CdcDmsComposeFile -and
                $StartScript -eq "/compose/start-$project.ps1" -and
                [bool]$Parameters.EnableSwaggerUI -eq ($scenario -eq 'enabled')
            }
        }
        else {
            Should -Invoke -ModuleName cdc-lifecycle Invoke-CdcInfrastructure -Times 0 -Exactly -ParameterFilter { $Parameters.DmsOnly }
        }
    }
    It 'keeps the raised broker override out of stopped <flavor>/<provider> <operation> preparation' -ForEach @(
        foreach ($flavor in @('local', 'published')) {
            foreach ($provider in @('postgresql', 'mssql')) {
                foreach ($operation in @('start', 'retire')) {
                    @{ flavor = $flavor; provider = $provider; operation = $operation }
                }
            }
        }
    ) {
        $project = "dms-$flavor"
        Remove-Item (Join-Path $script:root '.cdc-deployments') -Recurse -Force
        @('DMS_HTTP_PORTS=8080', 'POSTGRES_PASSWORD=fixture-password', 'POSTGRES_DB_NAME=fixture',
            'CONFIG_SERVICE_CLIENT_SECRET=fixture-secret', 'DMS_CONFIG_IDENTITY_CLIENT_SECRET=fixture-secret') |
            Set-Content (Join-Path $script:root '.env.custom')
        Register-CdcDeploymentHandoff (New-TestHandoff -id 42 -project $project -provider $provider) (Join-Path $script:root 'custom-state-42')
        $script:live = @('connector-42')
        $override = Join-Path $script:root 'shared/broker-size.json'
        New-Item -ItemType Directory (Split-Path $override) -Force | Out-Null
        # Same fragment emitted by CdcComposeBrokerSizeDeployment after a size increase.
        @{ services = @{ kafka = @{ environment = @{
            KAFKA_SOCKET_REQUEST_MAX_BYTES = '268435456'
            KAFKA_REPLICA_FETCH_MAX_BYTES = '134217728'
            KAFKA_REPLICA_FETCH_RESPONSE_MAX_BYTES = '134217728'
        } } } } | ConvertTo-Json -Depth 10 | Set-Content $override
        $originalHash = (Get-FileHash $override).Hash
        Invoke-TestLifecycle @{ d = $true } $project
        (Read-TestDeployment $project).Phase | Should -Be 'Stopped'
        $script:trace.Clear()
        $script:composeFlavor = $flavor
        $script:brokerOverride = $override
        Mock -ModuleName cdc-lifecycle Invoke-CdcInfrastructure {
            $model = Get-TestComposeModel $script:composeFlavor $Parameters
            if ($Parameters['d']) {
                $Parameters.CdcKafkaInfrastructure | Should -BeTrue
                $Parameters.CdcBrokerSizeOverrideFile | Should -Be $script:brokerOverride
                $model.services.kafka.image | Should -Not -BeNullOrEmpty
                $model.services.kafka.environment.KAFKA_REPLICA_FETCH_MAX_BYTES | Should -Be '134217728'
                $script:trace.Add('down-volumes')
            }
            else {
                $Parameters.InfraOnly | Should -BeTrue
                $Parameters.CdcDatabaseInfrastructure | Should -BeTrue
                $Parameters.ContainsKey('CdcBrokerSizeOverrideFile') | Should -BeFalse
                $Parameters.ContainsKey('CdcKafkaInfrastructure') | Should -BeFalse
                $Parameters.ContainsKey('EnableKafkaUI') | Should -BeFalse
                $commands = @(Invoke-TestPreparationStartup $script:composeFlavor $Parameters)
                $commands.Count | Should -Be 2
                $commands[0] | Should -Match "-p dms-$script:composeFlavor up --detach db$"
                $commands[1] | Should -Match "-p dms-$script:composeFlavor up --detach config$"
                $commands | Should -Not -Match '--remove-orphans|--no-deps|kafka'
                $model.services.Keys | Should -Contain 'db'
                $model.services.Keys | Should -Contain 'config'
                $model.services.Keys | Should -Not -Contain 'kafka'
                $model.services.Keys | Should -Not -Contain 'kafka-cdc-worker'
                $script:trace.Add('infra')
            }
        } -ParameterFilter { $Parameters['InfraOnly'] -or $Parameters['d'] }
        Mock -ModuleName cdc-lifecycle Invoke-CdcLifecycleCommand {
            $settings = Get-Content $Entry.SettingsPath -Raw | ConvertFrom-Json -AsHashtable
            $settings.Cdc.Compose.BrokerSizeOverrideFile | Should -Be $script:brokerOverride
            $script:trace.Add("start-worker:$($Entry.DataStoreId)")
            $script:workerRunning = $true
        } -ParameterFilter { $Operation -eq 'start-worker' }
        $parameters = if ($operation -eq 'retire') { @{ d = $true; v = $true } } else { @{} }
        Invoke-TestLifecycle $parameters $project
        $expected = @('infra', 'start-worker:42', 'rest:connectors', 'rest:connectors/connector-42/status')
        if ($operation -eq 'retire') {
            $expected += @('retire:42', 'rest:connectors', 'down-volumes')
            Test-CdcDeployment $project | Should -BeFalse
        }
        else {
            $expected += @('start:42', 'dms')
            (Read-TestDeployment $project).BrokerSizeOverrideFile | Should -Be $override
            (Read-TestDeployment $project).Phase | Should -Be 'Active'
        }
        $script:trace | Should -Be $expected
        (Get-FileHash $override).Hash | Should -Be $originalHash
    }
    It 'preserves ordinary <flavor> <mode> startup arguments' -ForEach @(
        foreach ($flavor in @('local', 'published')) {
            foreach ($mode in @('full', 'DbOnly')) { @{ flavor = $flavor; mode = $mode } }
        }
    ) {
        $commands = @(Invoke-TestPreparationStartup $flavor @{ DbOnly = ($mode -eq 'DbOnly'); EnvironmentFile = '/selected/.env' })
        if ($mode -eq 'DbOnly') {
            $commands.Count | Should -Be 1
            $commands[0] | Should -Match "-p dms-$flavor up --detach db$"
            $commands[0] | Should -Not -Match '--remove-orphans|--no-deps'
        }
        else {
            $commands.Count | Should -Be 2
            $commands[0] | Should -Match "-p dms-$flavor up --detach --remove-orphans db$"
            $commands[1] | Should -Match "-p dms-$flavor up --detach --remove-orphans$"
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
    Context 'Generated bootstrap runtime cleanup' {
        BeforeEach {
            Remove-Item (Join-Path $script:root '.cdc-deployments') -Recurse -Force
            $script:handoff = New-TestBootstrapHandoff
            Register-CdcDeploymentHandoff $script:handoff (Join-Path $script:root '.cdc-state')
            $script:live = @('connector-42')
            $script:runtimeRoot = Join-Path $script:root '.bootstrap/cdc-runtime'
        }

        It 'releases the existing bootstrap layout for ordinary E2E preparation (<cleanup>)' -ForEach @(
            @{ cleanup = 'bootstrap' }, @{ cleanup = 'e2e' }
        ) {
            [IO.Path]::GetDirectoryName($script:handoff.SettingsPath) | Should -Be $script:runtimeRoot
            ([int][IO.File]::GetUnixFileMode($script:runtimeRoot) -band 511) | Should -Be 448
            foreach ($path in @($script:handoff.SettingsPath, $script:handoff.DmsComposePath)) {
                ([int][IO.File]::GetUnixFileMode($path) -band 511) | Should -Be 384
            }
            $state = Join-Path $script:root '.cdc-state'
            New-Item -ItemType Directory $state -Force | Out-Null
            'irreversible-exposure' | Set-Content (Join-Path $state 'source-history.json')
            { Assert-E2ECdcWorkspaceAvailable } | Should -Throw '*E2E setup cannot reset*'
            Invoke-TestLifecycle @{ d = $true; v = $true; RemoveBootstrap = ($cleanup -eq 'bootstrap') }
            if ($cleanup -eq 'e2e') { Remove-E2EBootstrapWorkspace -BootstrapWorkspacePath (Join-Path $script:root '.bootstrap') }
            Test-Path (Join-Path $script:root '.bootstrap') | Should -BeFalse
            Get-Content (Join-Path $state 'source-history.json') | Should -Be 'irreversible-exposure'
            Test-CdcDeployment 'dms-local' | Should -BeFalse
            { Assert-E2ECdcWorkspaceAvailable } | Should -Not -Throw
        }

        It 'resumes cleanup after one inventoried file was removed and preserves cleanup authority' {
            $script:cleanupRemoved = 0
            Mock Remove-Item -ModuleName cdc-lifecycle {
                if ($script:cleanupRemoved -eq 1) { throw 'private-path injected interruption' }
                [IO.File]::Delete($LiteralPath)
                $script:cleanupRemoved++
            } -ParameterFilter { $LiteralPath -like '*.bootstrap/cdc-runtime/*' }
            { Invoke-TestLifecycle @{ d = $true; v = $true } } | Should -Throw '*runtime cleanup is incomplete*'
            (Read-TestDeployment).Phase | Should -Be 'RuntimeCleanup'
            (Read-TestDeployment).RuntimeCleanupFiles.Count | Should -Be 2
            @(Get-ChildItem $script:runtimeRoot).Count | Should -Be 1
            $script:trace.Clear()
            # No settings re-read, controller call or Compose teardown is needed after this checkpoint.
            Mock Remove-Item -ModuleName cdc-lifecycle { [IO.File]::Delete($LiteralPath) } -ParameterFilter { $LiteralPath -like '*.bootstrap/cdc-runtime/*' }
            Invoke-TestLifecycle @{ d = $true; v = $true; RemoveBootstrap = $true }
            $script:trace.Count | Should -Be 0
            Test-CdcDeployment 'dms-local' | Should -BeFalse
            { Assert-E2ECdcWorkspaceAvailable } | Should -Not -Throw
        }

        It 'rejects changed surviving configuration on an interrupted cleanup retry' {
            $script:cleanupRemoved = 0
            Mock Remove-Item -ModuleName cdc-lifecycle {
                if ($script:cleanupRemoved -eq 1) { throw 'Interrupted' }
                [IO.File]::Delete($LiteralPath)
                $script:cleanupRemoved++
            } -ParameterFilter { $LiteralPath -like '*.bootstrap/cdc-runtime/*' }
            { Invoke-TestLifecycle @{ d = $true; v = $true } } | Should -Throw '*runtime cleanup is incomplete*'
            $remaining = @(Get-ChildItem $script:runtimeRoot)[0].FullName
            'private-changed-content' | Set-Content $remaining
            $script:trace.Clear()
            { Invoke-TestLifecycle @{ d = $true; v = $true } } | Should -Throw '*runtime cleanup is incomplete*'
            Get-Content $remaining | Should -Be 'private-changed-content'
            Test-CdcDeployment 'dms-local' | Should -BeTrue
            $script:trace.Count | Should -Be 0
        }

        It 'does not delete generated files before the cleanup checkpoint is durable' {
            $script:realWrite = & (Get-Module cdc-lifecycle) { (Get-Command Write-CdcDeployment).ScriptBlock }
            Mock Write-CdcDeployment -ModuleName cdc-lifecycle {
                if ($Deployment.Phase -eq 'RuntimeCleanup') { throw 'Checkpoint failed' }
                & (Get-Module cdc-lifecycle) $script:realWrite $Project $Deployment
            }
            { Invoke-TestLifecycle @{ d = $true; v = $true } } | Should -Throw '*Checkpoint failed*'
            (Read-TestDeployment).Phase | Should -Be 'Retired'
            @(Get-ChildItem $script:runtimeRoot).Count | Should -Be 2
            $script:trace[-1] | Should -Be 'down-volumes'
        }

        It 'keeps unrelated files protected from both recursive cleanup helpers' {
            'unrelated' | Set-Content (Join-Path $script:runtimeRoot 'notes.txt')
            Invoke-TestLifecycle @{ d = $true; v = $true; RemoveBootstrap = $true }
            Remove-E2EBootstrapWorkspace -BootstrapWorkspacePath (Join-Path $script:root '.bootstrap')
            Get-Content (Join-Path $script:runtimeRoot 'notes.txt') | Should -Be 'unrelated'
            Test-Path $script:handoff.SettingsPath | Should -BeFalse
            { Assert-E2ECdcWorkspaceAvailable } | Should -Throw '*E2E setup cannot reset*'
        }

        It 'keeps generated peer files and their deployment inventory' {
            $peer = New-TestBootstrapHandoff -id 99 -project 'dms-published' -state (Join-Path $script:root 'peer-state')
            Register-CdcDeploymentHandoff $peer (Join-Path $script:root 'peer-state')
            $hash = (Get-FileHash $peer.SettingsPath).Hash
            Invoke-TestLifecycle @{ d = $true; v = $true; RemoveBootstrap = $true }
            Remove-E2EBootstrapWorkspace -BootstrapWorkspacePath (Join-Path $script:root '.bootstrap')
            (Get-FileHash $peer.SettingsPath).Hash | Should -Be $hash
            Test-Path $peer.DmsComposePath | Should -BeTrue
            Test-CdcDeployment 'dms-published' | Should -BeTrue
            { Assert-E2ECdcWorkspaceAvailable } | Should -Throw '*E2E setup cannot reset*'
        }

        It 'retains nested state at <location> and gives a sanitized protection diagnostic' -ForEach @(
            @{ location = 'custom-source-secret' }, @{ location = 'cdc-runtime/custom-source-secret' }, @{ location = 'cdc-runtime' }
        ) {
            $state = Join-Path $script:root ".bootstrap/$location"
            New-Item -ItemType Directory $state -Force | Out-Null
            'irreversible-exposure' | Set-Content (Join-Path $state 'source-history.json')
            & (Get-Module cdc-lifecycle) {
                param($state)
                $value = Read-CdcDeployment 'dms-local'
                $value.Entries[0].StatePath = $state
                Write-CdcDeployment 'dms-local' $value
            } $state
            { Invoke-TestLifecycle @{ d = $true; v = $true; RemoveBootstrap = $true } } | Should -Throw '*surviving protected source-state root*'
            Test-CdcDeployment 'dms-local' | Should -BeTrue
            (Read-TestDeployment).Phase | Should -Be 'RuntimeCleanup'
            foreach ($action in @(
                { Remove-BootstrapWorkspaceIfRequested -RemoveBootstrap },
                { Remove-E2EBootstrapWorkspace -BootstrapWorkspacePath (Join-Path $script:root '.bootstrap') },
                { Assert-E2ECdcWorkspaceAvailable }
            )) {
                $failure = $null
                try { & $action } catch { $failure = $_ }
                $failure.Exception.Message | Should -Match 'surviving protected source-state root'
                $failure.Exception.Message | Should -Not -Match 'custom-source-secret|irreversible-exposure'
            }
            Get-Content (Join-Path $state 'source-history.json') | Should -Be 'irreversible-exposure'
        }

        It 'keeps runtime configuration during <operation>' -ForEach @(
            @{ operation = 'stop' }, @{ operation = 'failed retirement' }
        ) {
            if ($operation -eq 'stop') { Invoke-TestLifecycle @{ d = $true } }
            else {
                $script:failure = 'retire:42'
                { Invoke-TestLifecycle @{ d = $true; v = $true } } | Should -Throw
            }
            Remove-BootstrapWorkspaceIfRequested -RemoveBootstrap
            Test-Path $script:handoff.SettingsPath | Should -BeTrue
            Test-Path $script:handoff.DmsComposePath | Should -BeTrue
            { Assert-E2ECdcWorkspaceAvailable } | Should -Throw
        }
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

Describe 'Managed primitive DMS startup selection' {
    BeforeAll {
        function docker { }
        function Wait-HttpEndpointHealthy { }
        function Invoke-TestDmsStartup($flavor, $DmsOnly, $CdcDmsComposeFile, $EnableSwaggerUI) {
            $path = Join-Path $PSScriptRoot "../start-$flavor-dms.ps1"
            $ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)
            # Execute production validation, argument construction and the DMS branch in order.
            $nodes = $ast.FindAll({
                param($n)
                if ($n -is [Management.Automation.Language.AssignmentStatementAst]) {
                    return $n.Extent.Text -eq '$upArgs = @("--detach")'
                }
                if ($n -isnot [Management.Automation.Language.IfStatementAst]) { return $false }
                return $n.Extent.Text.StartsWith('if ($CdcDmsComposeFile)') -or
                    $n.Extent.Text.StartsWith('if (-not $databaseOnlyStartup -and -not $DmsOnly -and -not $CdcDatabaseInfrastructure)') -or
                    ($n.Clauses[0].Item2.Statements.Extent.Text -contains '$upArgs += "--no-deps"') -or
                    ($n.Extent.Text.StartsWith('if ($DmsOnly)') -and $n.Extent.Text.Contains('$dmsServices ='))
            }, $true)
            $nodes.Count | Should -Be 5
            $files = @('-f', "$flavor-dms.yml")
            $EnvironmentFile = '/selected/.env'
            $databaseOnlyStartup = $false
            $CdcDatabaseInfrastructure = $false
            $dmsUrl = 'http://localhost:8080'
            & ([scriptblock]::Create(($nodes.Extent.Text -join "`n"))) | Out-Null
        }
    }
    BeforeEach {
        $script:commands = [Collections.Generic.List[string]]::new()
        Mock docker { $script:commands.Add((@($args | ForEach-Object { $_ }) -join ' ')); $global:LASTEXITCODE = 0 }
        Mock Wait-HttpEndpointHealthy { }
        $script:handoff = Join-Path $TestDrive 'dms-override.json'
        '{"services":{"dms":{}}}' | Set-Content $script:handoff
    }
    It 'isolates the validated <flavor> CDC handoff (Swagger=<swagger>)' -ForEach @(
        @{ flavor = 'local'; swagger = $false }, @{ flavor = 'published'; swagger = $false },
        @{ flavor = 'local'; swagger = $true }, @{ flavor = 'published'; swagger = $true }
    ) {
        Invoke-TestDmsStartup -flavor $flavor -DmsOnly $true -CdcDmsComposeFile $script:handoff -EnableSwaggerUI $swagger
        $script:commands.Count | Should -Be 1
        $services = if ($swagger) { 'dms swagger-ui' } else { 'dms' }
        $script:commands[0] | Should -Match "-p dms-$flavor up --detach --no-deps $services$"
        $script:commands[0] | Should -Match ([regex]::Escape("-f $script:handoff --env-file /selected/.env"))
        $script:commands[0] | Should -Not -Match '--remove-orphans'
        Should -Invoke Wait-HttpEndpointHealthy -Times 1 -Exactly
    }
    It 'rejects <fault> CDC handoff before <flavor> Docker startup' -ForEach @(
        @{ flavor = 'local'; fault = 'missing-file' }, @{ flavor = 'published'; fault = 'missing-file' },
        @{ flavor = 'local'; fault = 'without-DmsOnly' }, @{ flavor = 'published'; fault = 'without-DmsOnly' }
    ) {
        if ($fault -eq 'missing-file') { Remove-Item $script:handoff }
        { Invoke-TestDmsStartup -flavor $flavor -DmsOnly ($fault -ne 'without-DmsOnly') -CdcDmsComposeFile $script:handoff -EnableSwaggerUI $false } |
            Should -Throw '*CDC DMS settings handoff requires -DmsOnly and an existing Compose override*'
        $script:commands.Count | Should -Be 0
        Should -Invoke Wait-HttpEndpointHealthy -Times 0 -Exactly
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
    BeforeEach {
        $script:deployment = @{
            ConnectEndpoint = 'http://localhost:8083/'
            Entries = @(@{ ConnectorName = 'connector-42' }, @{ ConnectorName = 'connector-43' })
        }
        $script:budget = @{
            Clock = [Diagnostics.Stopwatch]::StartNew(); CallMilliseconds = 30000; WaitMilliseconds = 300000
            TimeoutMessage = 'CDC startup-readiness timed out; retain infrastructure.'
        }
    }
    It 'classifies real HTTP <code> at <path> as startup retryable <retryable>' -ForEach @(
        foreach ($path in @('connectors', 'connectors/connector-42/status')) {
            foreach ($code in @(301, 400, 401, 403, 404, 408, 409, 422, 429, 500, 502, 503, 504, 599)) {
                @{ path = $path; code = $code; retryable = ($code -in @(408, 429) -or $code -ge 500 -or ($code -eq 404 -and $path -ne 'connectors')) }
            }
        }
    ) {
        $script:httpCode = $code
        Mock -ModuleName cdc-lifecycle Invoke-WebRequest {
            throw [Microsoft.PowerShell.Commands.HttpResponseException]::new('sentinel-secret http://physical-source', [Net.Http.HttpResponseMessage]::new([Enum]::ToObject([Net.HttpStatusCode], $script:httpCode)))
        }
        $failure = $null
        try { & (Get-Module cdc-lifecycle) { param($d, $p, $w) Invoke-CdcLifecycleRest $d $p -StartupWait $w } $script:deployment $path $script:budget }
        catch { $failure = $_.Exception }
        $failure | Should -Not -BeNullOrEmpty
        ($failure -is [Net.Http.HttpRequestException]) | Should -Be $retryable
        $failure.Message | Should -Be 'CDC worker inventory is unavailable; managed shutdown or resume is not authorized.'
        $failure.InnerException | Should -BeNullOrEmpty
        Should -Invoke -ModuleName cdc-lifecycle Invoke-WebRequest -Times 1 -Exactly -ParameterFilter {
            $TimeoutSec -eq 10 -and $OperationTimeoutSeconds -eq 10 -and $MaximumRedirection -eq 0
        }
    }
    It 'classifies real transport <kind> as startup retryable <retryable>' -ForEach @(
        @{ kind = 'ConnectionError'; retryable = $true }, @{ kind = 'NameResolutionError'; retryable = $true },
        @{ kind = 'ResponseEnded'; retryable = $true }, @{ kind = 'SecureConnectionError'; retryable = $false },
        @{ kind = 'InvalidResponse'; retryable = $false }, @{ kind = 'Unknown'; retryable = $false },
        @{ kind = 'task-timeout'; retryable = $true }, @{ kind = 'timeout'; retryable = $true },
        @{ kind = 'web-timeout'; retryable = $true }, @{ kind = 'web-auth'; retryable = $false },
        @{ kind = 'unexpected'; retryable = $false }
    ) {
        $script:failureKind = $kind
        Mock -ModuleName cdc-lifecycle Invoke-WebRequest {
            switch ($script:failureKind) {
                'task-timeout' { throw [Threading.Tasks.TaskCanceledException]::new('sentinel-secret') }
                'timeout' { throw [TimeoutException]::new('sentinel-secret') }
                'web-timeout' { throw [Net.WebException]::new('sentinel-secret', [Net.WebExceptionStatus]::Timeout) }
                'web-auth' { throw [Net.WebException]::new('sentinel-secret', [Net.WebExceptionStatus]::TrustFailure) }
                'unexpected' { throw [InvalidOperationException]::new('sentinel-secret') }
                default { throw [Net.Http.HttpRequestException]::new([Net.Http.HttpRequestError]$script:failureKind, 'sentinel-secret', $null, $null) }
            }
        }
        $failure = $null
        try { & (Get-Module cdc-lifecycle) { param($d, $w) Invoke-CdcLifecycleRest $d 'connectors' -StartupWait $w } $script:deployment $script:budget }
        catch { $failure = $_.Exception }
        $failure | Should -Not -BeNullOrEmpty
        ($failure -is [Net.Http.HttpRequestException]) | Should -Be $retryable
        $failure.Message | Should -Be 'CDC worker inventory is unavailable; managed shutdown or resume is not authorized.'
        $failure.InnerException | Should -BeNullOrEmpty
    }
    It 'rejects unsafe startup evidence immediately: <scenario>' -ForEach @(
        @{ scenario = 'invalid-json'; inventory = '{sentinel-secret' },
        @{ scenario = 'null-inventory'; inventory = 'null' },
        @{ scenario = 'object-inventory'; inventory = '{}' },
        @{ scenario = 'invalid-live-name'; inventory = '["connector-42",null]' },
        @{ scenario = 'blank-live-name'; inventory = '["connector-42"," "]' },
        @{ scenario = 'duplicate-live-name'; inventory = '["connector-42","connector-42"]' },
        @{ scenario = 'unexpected'; inventory = '["connector-42","unmanaged"]' },
        @{ scenario = 'duplicate-retained'; retained = @('connector-42', 'connector-42') },
        @{ scenario = 'blank-retained'; retained = @('connector-42', ' ') },
        @{ scenario = 'invalid-retained'; retained = @('connector-42', '../sentinel-secret') },
        @{ scenario = 'nonstring-retained'; retained = @('connector-42', 43) },
        @{ scenario = 'null-status'; status = 'null' },
        @{ scenario = 'missing-tasks'; status = '{"name":"connector-42","connector":{"state":"STOPPED"}}' },
        @{ scenario = 'malformed-tasks'; status = '{"name":"connector-42","connector":{"state":"STOPPED"},"tasks":{}}' },
        @{ scenario = 'mismatched-status'; status = '{"name":"connector-43","connector":{"state":"STOPPED"},"tasks":[]}' },
        @{ scenario = 'running'; status = '{"name":"connector-42","connector":{"state":"RUNNING"},"tasks":[]}' },
        @{ scenario = 'paused'; status = '{"name":"connector-42","connector":{"state":"PAUSED"},"tasks":[]}' },
        @{ scenario = 'failed'; status = '{"name":"connector-42","connector":{"state":"FAILED"},"tasks":[]}' },
        @{ scenario = 'tasks'; status = '{"name":"connector-42","connector":{"state":"STOPPED"},"tasks":[{}]}' },
        @{ scenario = 'incomplete-but-running'; inventory = '["connector-42"]'; status = '{"name":"connector-42","connector":{"state":"RUNNING"},"tasks":[]}' }
    ) {
        param([string]$inventory = '', [string]$status = '', [object[]]$retained = @())

        $script:inventoryBody = if ($inventory) { $inventory } else { '["connector-42","connector-43"]' }
        $script:statusBody = if ($status) { $status } else { '{"name":"connector-42","connector":{"state":"STOPPED"},"tasks":[]}' }
        if ($retained) { $script:deployment.Entries = @($retained | ForEach-Object { @{ ConnectorName = $_ } }) }
        Mock -ModuleName cdc-lifecycle Invoke-WebRequest {
            @{ Content = $(if ($Uri.AbsolutePath -eq '/connectors') { $script:inventoryBody } else { $script:statusBody }) }
        }
        $failure = $null
        try { & (Get-Module cdc-lifecycle) { param($d, $w) Assert-CdcWorkerInventory $d -Stopped -StartupWait $w } $script:deployment $script:budget }
        catch { $failure = $_.Exception }
        $failure | Should -Not -BeNullOrEmpty
        ($failure -is [Net.Http.HttpRequestException]) | Should -BeFalse
        $failure.Message | Should -Not -Match 'sentinel-secret'
        if ($retained) { Should -Invoke -ModuleName cdc-lifecycle Invoke-WebRequest -Times 0 -Exactly }
        else { Should -Invoke -ModuleName cdc-lifecycle Invoke-WebRequest -Times 1 -Exactly -ParameterFilter { $Uri.AbsolutePath -eq '/connectors' } }
    }
    It 'caps startup requests without rounding up: <call>/<remaining> milliseconds' -ForEach @(
        @{ call = 30000; remaining = 20000; seconds = 10 },
        @{ call = 2900; remaining = 20000; seconds = 2 },
        @{ call = 30000; remaining = 1900; seconds = 1 },
        @{ call = 30000; remaining = 999; seconds = 0 },
        @{ call = 999; remaining = 20000; seconds = 0 },
        @{ call = 30000; remaining = 0; seconds = 0 }
    ) {
        $script:budget.CallMilliseconds = $call
        $script:budget.WaitMilliseconds = 30000
        # A fixed elapsed sample probes the exact whole-second representability boundary.
        $script:budget.Clock = @{ Elapsed = @{ TotalMilliseconds = 30000 - $remaining } }
        Mock -ModuleName cdc-lifecycle Invoke-WebRequest { @{ Content = '[]' } }
        if ($seconds -eq 0) {
            { & (Get-Module cdc-lifecycle) { param($d, $w) Invoke-CdcLifecycleRest $d 'connectors' -StartupWait $w } $script:deployment $script:budget } | Should -Throw '*startup-readiness timed out*'
            Should -Invoke -ModuleName cdc-lifecycle Invoke-WebRequest -Times 0 -Exactly
        }
        else {
            & (Get-Module cdc-lifecycle) { param($d, $w) Invoke-CdcLifecycleRest $d 'connectors' -StartupWait $w } $script:deployment $script:budget
            Should -Invoke -ModuleName cdc-lifecycle Invoke-WebRequest -Times 1 -Exactly -ParameterFilter { $TimeoutSec -eq $seconds -and $OperationTimeoutSeconds -eq $seconds }
        }
    }
    It 'keeps shutdown and retirement inventory checks single-pass (<operation>)' -ForEach @(
        @{ operation = 'stop' }, @{ operation = 'retire' }
    ) {
        Mock -ModuleName cdc-lifecycle Invoke-WebRequest { @{ Content = '["connector-42"]' } }
        { & (Get-Module cdc-lifecycle) { param($d, $op) Assert-CdcWorkerInventory $d -Stopped:($op -eq 'stop') -Empty:($op -eq 'retire') } $script:deployment $operation } | Should -Throw '*inventory*'
        Should -Invoke -ModuleName cdc-lifecycle Invoke-WebRequest -Times 1 -Exactly
    }
    It 'does not make ordinary transport failures retryable' {
        Mock -ModuleName cdc-lifecycle Invoke-WebRequest { throw [TimeoutException]::new('sentinel-secret') }
        $failure = $null
        try { & (Get-Module cdc-lifecycle) { param($d) Invoke-CdcLifecycleRest $d 'connectors' } $script:deployment }
        catch { $failure = $_.Exception }
        ($failure -is [Net.Http.HttpRequestException]) | Should -BeFalse
        $failure.Message | Should -Be 'CDC worker inventory is unavailable; managed shutdown or resume is not authorized.'
    }
    It 'shares the original wait budget across slow status and polling: <scenario>' -ForEach @(
        @{ scenario = 'slow-status' }, @{ scenario = 'poll' }, @{ scenario = 'expired-success' }, @{ scenario = 'capped-poll' }
    ) {
        $script:budgetScenario = $scenario
        $settingsPath = Join-Path $TestDrive 'timing.json'
        $poll = if ($scenario -eq 'capped-poll') { 2300 } else { 500 }
        @{ Cdc = @{ Timing = @{ CallMilliseconds = 2000; WaitMilliseconds = 2300; PollMilliseconds = $poll } } } |
            ConvertTo-Json -Depth 5 | Set-Content $settingsPath
        $script:deployment.Entries[0].SettingsPath = $settingsPath
        $script:requests = [Collections.Generic.List[object]]::new()
        $script:delays = [Collections.Generic.List[int]]::new()
        $script:clock = [Diagnostics.Stopwatch]::StartNew()
        Mock -ModuleName cdc-lifecycle Invoke-WebRequest {
            $script:requests.Add(@{ Path = $Uri.AbsolutePath; Seconds = $ConnectionTimeoutSeconds; Elapsed = $script:clock.Elapsed.TotalMilliseconds })
            if ($Uri.AbsolutePath -eq '/connectors') {
                if ($script:budgetScenario -in @('poll', 'capped-poll')) { return @{ Content = '[]' } }
                return @{ Content = '["connector-42","connector-43"]' }
            }
            if ($script:budgetScenario -eq 'slow-status' -and $Uri.AbsolutePath -like '*connector-42/status') { [Threading.Thread]::Sleep(1400) }
            if ($script:budgetScenario -eq 'expired-success' -and $Uri.AbsolutePath -like '*connector-43/status') { [Threading.Thread]::Sleep(2400) }
            return @{ Content = (@{ name = $Uri.AbsolutePath.Split('/')[2]; connector = @{ state = 'STOPPED' }; tasks = @() } | ConvertTo-Json -Depth 5) }
        }
        Mock -ModuleName cdc-lifecycle Start-Sleep {
            $script:delays.Add($Milliseconds)
            [Threading.Thread]::Sleep($Milliseconds)
        }
        { & (Get-Module cdc-lifecycle) { param($d) Wait-CdcWorkerStartup $d } $script:deployment } | Should -Throw '*startup-readiness timed out*'
        $script:clock.Stop()
        $script:clock.Elapsed.TotalSeconds | Should -BeLessThan 4
        @($script:requests | Where-Object { $_.Seconds -le 0 -or $_.Seconds -gt 2 }).Count | Should -Be 0
        if ($scenario -eq 'poll') {
            $script:requests.Count | Should -BeGreaterThan 1
            $script:requests[-1].Seconds | Should -Be 1
            $script:delays.Count | Should -BeGreaterThan 0
            @($script:delays | Where-Object { $_ -le 0 -or $_ -gt 500 }).Count | Should -Be 0
        }
        elseif ($scenario -eq 'capped-poll') {
            $script:requests.Count | Should -Be 1
            $script:delays.Count | Should -Be 1
            $script:delays[0] | Should -BeGreaterThan 0
            $script:delays[0] | Should -BeLessThan 2300
        }
        else {
            $expected = @('/connectors', '/connectors/connector-42/status')
            if ($scenario -eq 'expired-success') { $expected += '/connectors/connector-43/status' }
            $script:requests.Path | Should -Be $expected
            $script:delays.Count | Should -Be 0
        }
    }
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
