# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Set-StrictMode -Version Latest

function Get-CdcDeploymentPath {
    param([string]$Project)
    if ($Project -notin @('dms-local', 'dms-published')) { throw 'Unsupported CDC Compose project.' }
    return Join-Path $PSScriptRoot ".cdc-deployments/$Project.json"
}

function Test-CdcDeployment {
    <#
    .SYNOPSIS
    Detects retained CDC deployment configuration, including custom controller state roots.
    #>
    param([string]$Project)
    return Test-Path -LiteralPath (Get-CdcDeploymentPath $Project)
}

function Invoke-CdcLifecycleDocker {
    param([string[]]$Arguments)
    $output = @(& docker @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'CDC deployment Docker inventory is unavailable.' }
    return $output
}

function Assert-CdcUnregisteredInfrastructure {
    <#
    .SYNOPSIS
    Rejects state loss when retained managed worker containers or broker volumes still exist.
    #>
    param([string]$Project)
    $containers = @(Invoke-CdcLifecycleDocker @('ps', '-aq', '--filter', "label=com.docker.compose.project=$Project", '--filter', 'label=com.docker.compose.service=kafka-cdc-worker'))
    $volumes = @(Invoke-CdcLifecycleDocker @('volume', 'ls', '-q', '--filter', "label=com.docker.compose.project=$Project", '--filter', 'label=org.edfi.cdc.managed=true'))
    if ($containers.Count -gt 0 -or $volumes.Count -gt 0) {
        throw 'CDC infrastructure survives without its original deployment inventory. State loss is not stop, adoption, or destructive cleanup authority.'
    }
}

function Test-CdcWorkerRunning {
    param([string]$Project)
    return @(Invoke-CdcLifecycleDocker @('ps', '-q', '--filter', "label=com.docker.compose.project=$Project", '--filter', 'label=com.docker.compose.service=kafka-cdc-worker')).Count -gt 0
}

function Test-CdcInfrastructureInvocation {
    <#
    .SYNOPSIS
    Identifies the infrastructure phase currently owned by this module in this process.
    #>
    # Child .ps1 files invoked from a module acquire their own script scope. A script-scoped
    # flag is therefore unavailable to commands reached through that child. The owning call
    # frame proves this invocation is inside the lifecycle infrastructure boundary.
    return @(Get-PSCallStack | Where-Object {
        $_.FunctionName -ceq 'Invoke-CdcInfrastructure' -and
        $_.ScriptName -ceq (Join-Path $PSScriptRoot 'cdc-lifecycle.psm1')
    }).Count -gt 0
}

function Assert-CdcPrivatePath {
    param([string]$Path, [switch]$Directory)
    if (-not $IsLinux) { throw 'Managed CDC lifecycle currently requires Linux durable private files.' }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    $expected = if ($Directory) { 448 } else { 384 }
    if ($item.LinkType -or (([int][IO.File]::GetUnixFileMode($Path) -band 511) -ne $expected)) {
        throw 'CDC deployment files require owner-only permissions and must not be links.'
    }
}

function Enter-CdcDeploymentLock {
    param([string]$Project)
    $directory = Split-Path (Get-CdcDeploymentPath $Project) -Parent
    if (-not (Test-Path -LiteralPath $directory)) {
        [IO.Directory]::CreateDirectory($directory, [IO.UnixFileMode]448) | Out-Null
    }
    Assert-CdcPrivatePath $directory -Directory
    $path = Join-Path $directory "$Project.lock"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        if (Test-Path -LiteralPath $path) { Assert-CdcPrivatePath $path }
        $options = [IO.FileStreamOptions]::new()
        $options.Mode = [IO.FileMode]::OpenOrCreate
        $options.Access = [IO.FileAccess]::ReadWrite
        $options.Share = [IO.FileShare]::None
        $options.UnixCreateMode = [IO.UnixFileMode]384
        try { return [IO.FileStream]::new($path, $options) }
        catch [IO.IOException] {
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'CDC deployment controller lock timed out.' }
            Start-Sleep -Milliseconds 100
        }
    } while ($true)
}

function Write-CdcDeployment {
    param([string]$Project, [hashtable]$Deployment, [string]$Path = (Get-CdcDeploymentPath $Project))
    $path = $Path
    $temporary = "$path.$([guid]::NewGuid().ToString('N')).tmp"
    $options = [IO.FileStreamOptions]::new()
    $options.Mode = [IO.FileMode]::CreateNew
    $options.Access = [IO.FileAccess]::Write
    $options.Share = [IO.FileShare]::None
    $options.UnixCreateMode = [IO.UnixFileMode]384
    try {
        $stream = [IO.FileStream]::new($temporary, $options)
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes(($Deployment | ConvertTo-Json -Depth 64 -Compress))
            $stream.Write($bytes)
            $stream.Flush($true)
        }
        finally { $stream.Dispose() }
        [IO.File]::Move($temporary, $path, $true)
        # Flush the renamed directory entry before any external effect. Linux sync -f uses
        # syncfs; this is stronger than flushing only the file and includes the containing directory.
        & sync -f (Split-Path $path -Parent) 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'CDC deployment directory flush failed.' }
    }
    finally { if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) } }
}

function Get-CdcComposeEnvironmentHash {
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.yml')) {
        foreach ($match in [regex]::Matches([IO.File]::ReadAllText($file.FullName), '\$\{([A-Z][A-Z0-9_]*)')) {
            $names.Add($match.Groups[1].Value) | Out-Null
        }
    }
    foreach ($name in @('COMPOSE_PROFILES', 'COMPOSE_FILE', 'COMPOSE_PROJECT_NAME', 'DMS_DOCUMENTCACHE_COMPOSE_FILE')) { $names.Add($name) | Out-Null }
    $values = [ordered]@{}
    foreach ($name in @($names | Sort-Object -CaseSensitive)) { $values[$name] = [Environment]::GetEnvironmentVariable($name) }
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($values | ConvertTo-Json -Compress))))
}

function Get-CdcSettingsHash {
    param([string]$Path)
    $settings = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
    # These two operational ceilings change through the acknowledged controller rollout. Their
    # current values are checked against live policy by start/validate; all identity, credential,
    # endpoint, schema and worker settings remain part of the retained configuration hash.
    $settings.Cdc.Remove('MaxRecordBytes') | Out-Null
    $settings.Cdc.Remove('ProducerBufferBytes') | Out-Null
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($settings | ConvertTo-Json -Depth 64 -Compress))))
}

function Read-CdcDeployment {
    param([string]$Project)
    try {
        $path = Get-CdcDeploymentPath $Project
        Assert-CdcPrivatePath (Split-Path $path -Parent) -Directory
        Assert-CdcPrivatePath $path
        $value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
        if ($value.Version -ne 1 -or $value.Project -cne $Project -or @($value.Entries).Count -eq 0 -or
            $value.Phase -notin @('Active', 'Stopped', 'Transition', 'Retiring', 'Retired')) { throw 'Inventory' }
        foreach ($entry in $value.Entries) {
            foreach ($pair in @(@('SettingsPath', 'SettingsHash'), @('DmsComposePath', 'DmsComposeHash'))) {
                Assert-CdcPrivatePath $entry[$pair[0]]
                $hash = if ($pair[0] -eq 'SettingsPath') { Get-CdcSettingsHash $entry.SettingsPath } else { (Get-FileHash -LiteralPath $entry[$pair[0]]).Hash }
                if ($hash -cne $entry[$pair[1]]) { throw 'Changed configuration' }
            }
            $settings = Get-Content -LiteralPath $entry.SettingsPath -Raw | ConvertFrom-Json -AsHashtable
            if ($settings.Cdc.Compose.Project -cne $Project -or $settings.AppSettings.Datastore -cne $value.DatabaseEngine -or
                $settings.Cdc.Compose.EnvironmentFile -cne $value.EnvironmentFile -or $settings.Cdc.Compose.File -cne $value.ComposeFile -or
                $settings.Cdc.Compose.BrokerSizeOverrideFile -cne $value.BrokerSizeOverrideFile -or
                $settings.Cdc.Worker.Key -cne $value.WorkerKey -or $settings.Cdc.Worker.OffsetStorageTopic -cne $value.OffsetStorageTopic -or
                $settings.Cdc.ConnectEndpoint -cne $value.ConnectEndpoint -or $settings.Cdc.WorkerMetricsEndpoint -cne $value.WorkerMetricsEndpoint -or
                $settings.Cdc.DataStoreId -cne $entry.DataStoreId -or $settings.Cdc.InstanceKey -cne $entry.InstanceKey -or
                $settings.Cdc.DeploymentKey -cne $entry.DeploymentKey -or $settings.Cdc.Generation -ne $entry.Generation) { throw 'Contradictory scope' }
            if (-not [IO.Path]::IsPathFullyQualified($entry.StatePath) -or $entry.Generation -le 0) { throw 'Scope' }
        }
        if ((Get-FileHash -LiteralPath $value.EnvironmentFile).Hash -cne $value.EnvironmentHash) { throw 'Changed environment' }
        if ($value.ComposeEnvironmentHash -cne (Get-CdcComposeEnvironmentHash)) { throw 'Changed Compose environment' }
        if (@(Get-ChildItem Env:DMS_CDC__*).Count -gt 0) { throw 'Overrides' }
        return $value
    }
    catch { throw 'CDC deployment inventory or original configuration is missing, changed, or unreadable. Retain infrastructure and reconcile the original deployment; configuration removal is not cleanup authority.' }
}

function Register-CdcDeploymentHandoff {
    <#
    .SYNOPSIS
    Retains each target handoff before connector effects so later stop and cleanup include it.
    #>
    param($Handoff, [string]$StatePath, $Receipt)
    $Handoff = $Handoff | ConvertTo-Json -Depth 64 | ConvertFrom-Json -AsHashtable
    $settings = $Handoff.Settings
    $compose = $settings.Cdc.Compose
    $project = $compose.Project
    if (-not $Handoff.Contains('OriginalEnvironmentFile')) { $Handoff.OriginalEnvironmentFile = $compose.EnvironmentFile }
    $lock = Enter-CdcDeploymentLock $project
    try {
        $deployment = if (Test-CdcDeployment $project) { Read-CdcDeployment $project } else {
            @{
                ComposeEnvironmentHash = Get-CdcComposeEnvironmentHash
                Version = 1; Project = $project; DatabaseEngine = $settings.AppSettings.Datastore
                OriginalEnvironmentFile = $Handoff.OriginalEnvironmentFile
                EnvironmentFile = $compose.EnvironmentFile; EnvironmentHash = (Get-FileHash -LiteralPath $compose.EnvironmentFile).Hash
                ComposeFile = $compose.File; BrokerSizeOverrideFile = $compose.BrokerSizeOverrideFile
                WorkerKey = $settings.Cdc.Worker.Key; ConnectEndpoint = $settings.Cdc.ConnectEndpoint
                WorkerMetricsEndpoint = $settings.Cdc.WorkerMetricsEndpoint
                OffsetStorageTopic = $settings.Cdc.Worker.OffsetStorageTopic
                Phase = 'Active'; Entries = @()
            }
        }
        if ($deployment.Phase -ne 'Active' -or $deployment.DatabaseEngine -cne $settings.AppSettings.Datastore -or
            $deployment.EnvironmentFile -cne $compose.EnvironmentFile -or $deployment.ComposeFile -cne $compose.File -or
            $deployment.BrokerSizeOverrideFile -cne $compose.BrokerSizeOverrideFile -or
            $deployment.WorkerKey -cne $settings.Cdc.Worker.Key -or $deployment.ConnectEndpoint -cne $settings.Cdc.ConnectEndpoint -or
            $deployment.WorkerMetricsEndpoint -cne $settings.Cdc.WorkerMetricsEndpoint -or
            $deployment.OffsetStorageTopic -cne $settings.Cdc.Worker.OffsetStorageTopic) { throw 'CDC peer handoffs must share the exact retained worker and deployment context.' }
        foreach ($entry in $deployment.Entries) {
            if ($entry.SettingsPath -ceq $Handoff.SettingsPath -and $entry.StatePath -ceq $StatePath) { return }
            if ($entry.InstanceKey -ceq $settings.Cdc.InstanceKey -or $entry.DataStoreId -ceq $settings.Cdc.DataStoreId) {
                throw 'CDC target already has a retained handoff; use its original deployment configuration.'
            }
        }
        $deployment.Entries += @(@{
            SettingsPath = $Handoff.SettingsPath; SettingsHash = Get-CdcSettingsHash $Handoff.SettingsPath
            DmsComposePath = $Handoff.DmsComposePath; DmsComposeHash = (Get-FileHash -LiteralPath $Handoff.DmsComposePath).Hash
            StatePath = [IO.Path]::GetFullPath($StatePath); Generation = $settings.Cdc.Generation
            DeploymentKey = $settings.Cdc.DeploymentKey; InstanceKey = $settings.Cdc.InstanceKey
            DataStoreId = $settings.Cdc.DataStoreId; ConnectorName = ''
            Bootstrap = if ($null -ne $Receipt) { @{
                Receipt = $Receipt
                InputSettingsHash = $Handoff.InputSettingsHash
                DatabaseNameHash = $Handoff.DatabaseNameHash
            } } else { $null }
        })
        Write-CdcDeployment $project $deployment
    }
    finally { $lock.Dispose() }
}

function Get-CdcBootstrapRetryHandoff {
    <#
    .SYNOPSIS
    Recovers original bootstrap configuration; only the controller can authorize initial retry.
    #>
    param([string]$Project, [string]$StatePath, [string]$InputSettingsPath, [string]$DatabaseName)
    if (-not (Test-CdcDeployment $Project)) { return $null }
    $lock = Enter-CdcDeploymentLock $Project
    try {
        $deployment = Read-CdcDeployment $Project
        if ($deployment.Phase -cne 'Active' -or $deployment.Entries.Count -ne 1) {
            throw 'Initial CDC retry requires the original single-target offline deployment.'
        }
        $entry = $deployment.Entries[0]
        $hash = (Get-FileHash -LiteralPath $InputSettingsPath -ErrorAction Stop).Hash
        $databaseHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($DatabaseName)))
        if (-not $entry.Contains('Bootstrap') -or $null -eq $entry.Bootstrap -or
            $entry.StatePath -cne $StatePath -or $entry.Bootstrap.InputSettingsHash -cne $hash -or
            $entry.Bootstrap.DatabaseNameHash -cne $databaseHash) {
            throw 'Initial CDC retry requires the original settings, database, state root and provisioning handoff.'
        }
        return @{
            Settings = Get-Content -LiteralPath $entry.SettingsPath -Raw | ConvertFrom-Json -AsHashtable
            SettingsPath = $entry.SettingsPath; DmsComposePath = $entry.DmsComposePath
            OriginalEnvironmentFile = $deployment.OriginalEnvironmentFile
            EnvironmentFile = $deployment.EnvironmentFile
            InputSettingsHash = $entry.Bootstrap.InputSettingsHash; DatabaseNameHash = $entry.Bootstrap.DatabaseNameHash
            Receipt = $entry.Bootstrap.Receipt
        }
    }
    finally { $lock.Dispose() }
}

function Invoke-CdcLifecycleCommand {
    param([hashtable]$Entry, [string]$Operation)
    Import-Module (Join-Path $PSScriptRoot 'bootstrap-schema-tool.psm1')
    $tool = Resolve-DmsSchemaTool
    $arguments = @('cdc', $Operation, '--settings', $Entry.SettingsPath, '--state-path', $Entry.StatePath, '--json')
    if ($Operation -eq 'retire') { $arguments += @('--generation', [string]$Entry.Generation, '--destructive-cleanup') }
    if ($tool.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase)) { $output = @(& pwsh -NoProfile -File $tool @arguments 2>$null) }
    else { $output = @(& $tool @arguments 2>$null) }
    $code = $LASTEXITCODE
    try {
        if ($code -ne 0) { throw 'Controller rejected' }
        $result = ($output -join "`n") | ConvertFrom-Json -AsHashtable
        if ($result.operation -isnot [string] -or $result.succeeded -isnot [bool] -or
            $result.operation -cne $Operation -or $result.succeeded -ne $true -or $result.exitCode -ne 0 -or
            $result.binding.generation -ne $Entry.Generation -or $result.binding.instanceKey -cne $Entry.InstanceKey -or
            $result.binding.deploymentKey -cne $Entry.DeploymentKey -or $result.binding.dataStoreId -cne $Entry.DataStoreId -or
            [string]::IsNullOrWhiteSpace($result.binding.connectorName) -or
            ($Entry.ConnectorName -and $Entry.ConnectorName -cne $result.binding.connectorName)) { throw 'Scope' }
        if ($Operation -eq 'stop' -and ($result.data.targetShutdownVerified -isnot [bool] -or $result.data.succeeded -isnot [bool] -or $result.data.targetShutdownVerified -ne $true -or
            $result.data.boundary -cne 'VerifiedManagedStop' -or $result.data.succeeded -ne $true)) { throw 'Unverified stop' }
        if ($Operation -eq 'start' -and ($result.data.ready -isnot [bool] -or $result.data.succeeded -isnot [bool] -or $result.data.ready -ne $true -or $result.data.succeeded -ne $true)) { throw 'Not ready' }
        if ($Operation -eq 'retire' -and ($result.data.succeeded -isnot [bool] -or $result.data.succeeded -ne $true -or [guid]$result.data.operationId -eq [guid]::Empty)) { throw 'Unverified cleanup' }
        $Entry.ConnectorName = $result.binding.connectorName
    }
    catch { throw "CDC $Operation failed or returned unverified evidence (exit $code). Infrastructure and original state must remain available for reconciliation." }
}

function Invoke-CdcLifecycleRest {
    param([hashtable]$Deployment, [string]$Path)
    try {
        $uri = [uri]::new([uri]$Deployment.ConnectEndpoint, $Path)
        if (-not $uri.IsLoopback -or $uri.Scheme -ne 'http') { throw 'Unsupported endpoint' }
        $response = Invoke-WebRequest -Uri $uri -Method Get -TimeoutSec 10 -ErrorAction Stop
        $value = $response.Content | ConvertFrom-Json -AsHashtable -NoEnumerate -ErrorAction Stop
        if ($Path -eq 'connectors') {
            if ($value -isnot [array] -or @($value | Where-Object { $_ -isnot [string] -or -not $_ }).Count -gt 0) { throw 'Unknown inventory' }
        }
        elseif ($value -isnot [Collections.IDictionary] -or $value.name -isnot [string] -or
            $value.connector -isnot [Collections.IDictionary] -or $value.connector.state -isnot [string] -or $value.tasks -isnot [array]) {
            throw 'Unknown task state'
        }
        return $value
    }
    catch { throw 'CDC worker inventory is unavailable; managed shutdown or resume is not authorized.' }
}

function Assert-CdcWorkerInventory {
    param([hashtable]$Deployment, [switch]$Stopped, [switch]$Empty)
    $actual = @(Invoke-CdcLifecycleRest $Deployment 'connectors')
    $expected = @(if (-not $Empty) { $Deployment.Entries | ForEach-Object { $_.ConnectorName } })
    if (@($expected | Where-Object { -not $_ }).Count -gt 0 -or
        @($expected | Select-Object -Unique).Count -ne $expected.Count -or
        $actual.Count -ne $expected.Count -or @($actual | Where-Object { $_ -cnotin $expected }).Count -gt 0) {
        throw 'CDC live worker inventory does not match every retained managed binding; retain infrastructure and reconcile missing or unmanaged connectors.'
    }
    if ($Stopped) {
        foreach ($name in $expected) {
            $status = Invoke-CdcLifecycleRest $Deployment "connectors/$([uri]::EscapeDataString($name))/status"
            if ($status.name -cne $name -or $status.connector.state -cne 'STOPPED' -or @($status.tasks).Count -ne 0) {
                throw 'CDC connector shutdown is not currently verified for every worker assignment.'
            }
        }
    }
}

function Invoke-CdcInfrastructure {
    <#
    .SYNOPSIS
    Runs an infrastructure primitive within its owning lifecycle phase without recursive dispatch.
    #>
    param([string]$StartScript, [hashtable]$Parameters)
    & $StartScript @Parameters
    if ($LASTEXITCODE -ne 0) { throw 'CDC infrastructure phase failed; retain deployment configuration.' }
}

function Get-CdcDmsComposeHandoff {
    param([hashtable]$Deployment)
    $environment = @{}
    $targets = @()
    foreach ($entry in $Deployment.Entries) {
        $document = Get-Content -LiteralPath $entry.DmsComposePath -Raw | ConvertFrom-Json -AsHashtable
        $current = $document.services.dms.environment
        $common = @{}
        foreach ($key in $current.Keys) {
            if ($key -notlike 'DataManagement__DocumentCache__Targets__*') { $common[$key] = $current[$key] }
        }
        if ($environment.Count -eq 0) { $environment = $common }
        elseif ($environment.Count -ne $common.Count -or @($common.Keys | Where-Object { -not $environment.ContainsKey($_) -or $environment[$_] -cne $common[$_] }).Count -gt 0) {
            throw 'CDC peers require matching DMS host settings for automatic shared startup.'
        }
        $settings = Get-Content -LiteralPath $entry.SettingsPath -Raw | ConvertFrom-Json -AsHashtable
        $targets += @($settings.DataManagement.DocumentCache.Targets)
    }
    for ($index = 0; $index -lt $targets.Count; $index++) {
        foreach ($key in $targets[$index].Keys) {
            $environment["DataManagement__DocumentCache__Targets__$index`__$key"] = ([string]$targets[$index][$key]).Replace('$', '$$')
        }
    }
    $path = Join-Path (Split-Path (Get-CdcDeploymentPath $Deployment.Project) -Parent) "$($Deployment.Project).dms.json"
    Write-CdcDeployment $Deployment.Project @{ services = @{ dms = @{ environment = $environment } } } -Path $path
    return $path
}

function Invoke-CdcAdmittedHost {
    <#
    .SYNOPSIS
    Serializes initial DMS handoff with managed stop and rejects a concurrently stopped deployment.
    #>
    param([string]$Project, [string]$StartScript, [hashtable]$Parameters)
    $lock = Enter-CdcDeploymentLock $Project
    try {
        $deployment = Read-CdcDeployment $Project
        if ($deployment.Phase -ne 'Active') { throw 'CDC deployment was stopped during admission; DMS handoff is no longer authorized.' }
        Invoke-CdcInfrastructure $StartScript $Parameters
    }
    finally { $lock.Dispose() }
}

function Invoke-CdcDeploymentLifecycle {
    <#
    .SYNOPSIS
    Governs all retained connectors before worker stop, retained startup, or destructive teardown.
    #>
    param([string]$Project, [string]$StartScript, [hashtable]$Parameters)
    $lock = Enter-CdcDeploymentLock $Project
    try {
        $deployment = Read-CdcDeployment $Project
        # Explicit selection must agree; omitted values inherit the original selected environment.
        if (($Parameters.ContainsKey('DatabaseEngine') -and $Parameters.DatabaseEngine -cne $deployment.DatabaseEngine) -or
            ($Parameters['CdcBindingStatePath'] -and @($deployment.Entries | Where-Object { $_.StatePath -ceq [IO.Path]::GetFullPath($Parameters.CdcBindingStatePath) }).Count -eq 0) -or
            ($Parameters['CdcSettingsPath'] -and @($deployment.Entries | Where-Object { $_.SettingsPath -ceq [IO.Path]::GetFullPath($Parameters.CdcSettingsPath) }).Count -eq 0)) { throw 'CDC lifecycle selection conflicts with the retained deployment.' }
        # EnvironmentFile can be a base file composed by bootstrap. Always use the retained effective
        # snapshot, whose content hash was checked above; do not compose it again from caller defaults.
        if ($Parameters['EnvironmentFile'] -and [IO.Path]::GetFullPath($Parameters.EnvironmentFile) -cnotin @($deployment.EnvironmentFile, $deployment.OriginalEnvironmentFile)) {
            throw 'CDC lifecycle requires the original effective environment file; omit -EnvironmentFile to inherit it.'
        }
        $down = $Parameters['d'] -eq $true
        if (-not $down -and -not $Parameters['InfraOnly']) { Get-CdcDmsComposeHandoff $deployment | Out-Null }
        $destructive = $down -and $Parameters['v'] -eq $true
        if (-not $down -and ($Parameters['DbOnly'] -or $Parameters['DmsOnly'] -or $Parameters['DmsBaseUrl'] -or $Parameters['LoadSeedData'])) {
            throw 'Retained CDC startup uses the managed infrastructure/controller/DMS sequence; partial startup and seed flags are unsupported.'
        }
        $infrastructure = @{
            EnvironmentFile = $deployment.EnvironmentFile; DatabaseEngine = $deployment.DatabaseEngine
            SeparateConfigDatabase = $true; CdcKafkaInfrastructure = $true
            CdcBrokerSizeOverrideFile = $deployment.BrokerSizeOverrideFile
        }
        foreach ($flag in @('EnableKafkaUI', 'EnableSwaggerUI')) { if ($Parameters[$flag]) { $infrastructure[$flag] = $true } }
        if ($down -and ($deployment.Phase -ne 'Stopped' -or (Test-CdcWorkerRunning $Project))) {
            $deployment.Phase = if ($destructive) { 'Retiring' } else { 'Transition' }
            Write-CdcDeployment $Project $deployment
            if ($destructive) {
                foreach ($entry in $deployment.Entries) { Invoke-CdcLifecycleCommand $entry 'retire' }
                Assert-CdcWorkerInventory $deployment -Empty
                $deployment.Phase = 'Retired'
            }
            else {
                foreach ($entry in $deployment.Entries) { Invoke-CdcLifecycleCommand $entry 'stop' }
                Assert-CdcWorkerInventory $deployment -Stopped
                $deployment.Phase = 'Stopped'
            }
            Write-CdcDeployment $Project $deployment
        }
        elseif (-not $down -or ($destructive -and $deployment.Phase -eq 'Stopped')) {
            if ($deployment.Phase -ne 'Stopped') { throw 'CDC managed worker startup requires complete verified shutdown. Reconcile through controller stop; native recovery cannot be certified by startup.' }
            if (Test-CdcWorkerRunning $Project) { throw 'CDC worker recovered outside managed startup; reconcile through controller stop before retrying.' }
            # Consume the wrapper shutdown checkpoint before launching a retained worker. A crash
            # cannot turn an earlier successful stop into authority for a second worker launch.
            $deployment.Phase = 'Transition'
            Write-CdcDeployment $Project $deployment
            Invoke-CdcInfrastructure $StartScript ($infrastructure + @{ InfraOnly = $true; SuppressWriterGuidance = $true })
            Invoke-CdcLifecycleCommand $deployment.Entries[0] 'start-worker'
            Assert-CdcWorkerInventory $deployment -Stopped
            if ($destructive) {
                $deployment.Phase = 'Retiring'
                Write-CdcDeployment $Project $deployment
                foreach ($entry in $deployment.Entries) { Invoke-CdcLifecycleCommand $entry 'retire' }
                Assert-CdcWorkerInventory $deployment -Empty
                $deployment.Phase = 'Retired'
            }
            else {
                foreach ($entry in $deployment.Entries) { Invoke-CdcLifecycleCommand $entry 'start' }
                $deployment.Phase = 'Active'
            }
            Write-CdcDeployment $Project $deployment
        }
        if ($down) {
            if ($destructive -and $deployment.Phase -ne 'Retired') { throw 'CDC destructive cleanup has not completed.' }
            Invoke-CdcInfrastructure $StartScript ($infrastructure + @{
                d = $true; v = $destructive; RemoveBootstrap = $false
            })
            # Source journals/history, peers, and broker-size configuration stay in their original
            # roots even after volume deletion. Only this project's wrapper inventory is removed.
            if ($destructive) {
                [IO.File]::Delete((Get-CdcDeploymentPath $Project))
                & sync -f (Split-Path (Get-CdcDeploymentPath $Project) -Parent) 2>$null
                if ($LASTEXITCODE -ne 0) { throw 'CDC inventory removal flush failed.' }
                if ($Parameters['RemoveBootstrap']) {
                    Import-Module (Join-Path $PSScriptRoot 'bootstrap-manifest.psm1')
                    Remove-BootstrapWorkspaceIfRequested -RemoveBootstrap
                }
            }
        }
        elseif (-not $Parameters['InfraOnly']) {
            $merged = Get-CdcDmsComposeHandoff $deployment
            Invoke-CdcInfrastructure $StartScript @{
                EnvironmentFile = $deployment.EnvironmentFile; DatabaseEngine = $deployment.DatabaseEngine
                SeparateConfigDatabase = $true; DmsOnly = $true; CdcDmsComposeFile = $merged
            }
        }
    }
    finally { $lock.Dispose() }
}

Export-ModuleMember -Function Get-CdcBootstrapRetryHandoff, Test-CdcDeployment, Test-CdcInfrastructureInvocation, Register-CdcDeploymentHandoff, Invoke-CdcDeploymentLifecycle, Invoke-CdcInfrastructure, Assert-CdcUnregisteredInfrastructure, Invoke-CdcAdmittedHost
