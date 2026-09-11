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

function Get-CdcComposeInput {
    param([hashtable]$Handoff)
    $compose = $Handoff.Settings.Cdc.Compose
    $provider = $Handoff.Settings.AppSettings.Datastore
    $flavor = if ($compose.Project -ceq 'dms-local') { 'local' } else { 'published' }
    if ($provider -cnotin @('postgresql', 'mssql')) { throw 'CDC deployment provider is unsupported.' }
    Import-Module (Join-Path $PSScriptRoot 'env-utility.psm1')
    $environment = ReadValuesFromEnvFile $compose.EnvironmentFile
    # The shipped managed lifecycle's selected provider/host files, including Keycloak for
    # down -v (both start scripts include its volume then) and the worker's broker extension.
    # Never discover inputs by enumerating unrelated Compose files in the checkout.
    $files = @($compose.File, (Join-Path (Split-Path $compose.File -Parent) 'kafka-broker.yml'))
    $files += @("$provider.yml", "$flavor-dms.yml", "$flavor-config.yml", 'keycloak.yml', 'bootstrap-dms.yml' |
        ForEach-Object { Join-Path $PSScriptRoot $_ })
    if ($provider -ceq 'mssql') { $files += Join-Path $PSScriptRoot 'mssql-cdc.yml' }
    if ($provider -ceq 'postgresql' -and $env:POSTGRES_USE_TMPFS -ieq 'true') { $files += Join-Path $PSScriptRoot 'postgresql-tmpfs.yml' }
    if ($flavor -ceq 'local' -and (Get-EnvValue -EnvValues $environment -Name 'DMS_ENABLE_DOTNET_DIAGNOSTICS') -ieq 'true') {
        $files += Join-Path $PSScriptRoot 'local-dms-diagnostics.yml'
    }
    foreach ($option in @(@('EnableKafkaUI', 'kafka-ui.yml'), @('EnableSwaggerUI', 'swagger-ui.yml'))) {
        if ($Handoff[$option[0]]) { $files += Join-Path $PSScriptRoot $option[1] }
    }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $inputs = @(foreach ($file in $files) {
        @{ Path = $file; Hash = (Get-FileHash -LiteralPath $file -ErrorAction Stop).Hash }
    })
    # Retain process overrides and absence separately; Compose still reads the original
    # effective env file. Include its keys/references for nested dotenv interpolation and
    # the wrapper's settings derived from that same file. Escaped dollars are literals.
    foreach ($file in @($files) + @($compose.EnvironmentFile)) {
        $content = [IO.File]::ReadAllText($file).Replace('$$', '')
        foreach ($match in [regex]::Matches($content, '\$\{?([a-zA-Z_][a-zA-Z0-9_]*)')) {
            $names.Add($match.Groups[1].Value) | Out-Null
        }
    }
    foreach ($name in $environment.Keys) { $names.Add($name) | Out-Null }
    foreach ($name in @('COMPOSE_PROFILES', 'COMPOSE_FILE', 'COMPOSE_PROJECT_NAME', 'COMPOSE_ENV_FILES', 'COMPOSE_DISABLE_ENV_FILE',
        'DMS_DOCUMENTCACHE_COMPOSE_FILE', 'POSTGRES_USE_TMPFS')) { $names.Add($name) | Out-Null }
    foreach ($variable in @(Get-ChildItem Env:COMPOSE_*)) { $names.Add($variable.Name) | Out-Null }
    $values = [ordered]@{}
    foreach ($name in @($names | Sort-Object -CaseSensitive)) { $values[$name] = [Environment]::GetEnvironmentVariable($name) }
    return @{
        Version = 1; Project = $compose.Project; DatabaseEngine = $provider; IdentityProvider = $Handoff.IdentityProvider
        EnvironmentFile = $compose.EnvironmentFile; ComposeFile = $compose.File
        Files = $inputs; ProcessEnvironment = $values
    }
}

function Get-CdcComposeInputHash {
    param($Inputs)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($Inputs | ConvertTo-Json -Depth 64 -Compress))))
}

function Set-CdcRetainedEnvironment {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Temporarily applies the already authorized private deployment inputs; callers restore them in finally.')]
    param([hashtable]$Deployment, [hashtable]$Snapshot)
    $values = $Deployment.ComposeInputs.ProcessEnvironment
    # An unrelated shell must not inject new Compose control options either. Ordinary
    # unrelated environment variables remain untouched and are not retained authority.
    $names = @($values.Keys) + @(Get-ChildItem Env:COMPOSE_* | ForEach-Object { $_.Name })
    foreach ($name in @($names | Select-Object -Unique)) {
        $Snapshot[$name] = [Environment]::GetEnvironmentVariable($name)
        if ($null -eq $values[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $values[$name]) }
    }
}

function Restore-CdcRetainedEnvironment {
    param([hashtable]$Snapshot)
    foreach ($name in $Snapshot.Keys) {
        if ($null -eq $Snapshot[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $Snapshot[$name]) }
    }
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
            $value.Phase -notin @('Active', 'Stopped', 'Transition', 'Retiring', 'Retired', 'RuntimeCleanup')) { throw 'Inventory' }
        foreach ($entry in $value.Entries) {
            if (-not [IO.Path]::IsPathFullyQualified($entry.StatePath) -or $entry.Generation -le 0) { throw 'Scope' }
            # This checkpoint is written only after governed retirement AND destructive Compose
            # teardown. Original generated files may already be gone; only cleanup can resume.
            if ($value.Phase -eq 'RuntimeCleanup') { continue }
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
        }
        if ($value.Phase -eq 'RuntimeCleanup') {
            if (-not $value.ContainsKey('RuntimeCleanupFiles') -or $value.RuntimeCleanupFiles -isnot [array]) { throw 'Cleanup inventory' }
            $expected = @(Get-CdcGeneratedRuntimePath $value)
            if ($value.RuntimeCleanupFiles.Count -ne $expected.Count) { throw 'Cleanup inventory' }
            foreach ($file in $value.RuntimeCleanupFiles) {
                if ($file.Path -cnotin $expected -or $file.Hash -cnotmatch '^[A-F0-9]{64}$') { throw 'Cleanup inventory' }
            }
            if (@($value.RuntimeCleanupFiles | ForEach-Object { $_.Path } | Select-Object -Unique).Count -ne $expected.Count) { throw 'Cleanup inventory' }
        }
        else {
            if ((Get-FileHash -LiteralPath $value.EnvironmentFile).Hash -cne $value.EnvironmentHash) { throw 'Changed environment' }
            if (-not $value.Contains('ComposeInputs') -or -not $value.Contains('ComposeInputsHash') -or
                $value.ComposeInputsHash -cne (Get-CdcComposeInputHash $value.ComposeInputs)) { throw 'Missing or changed Compose inputs' }
            $inputs = $value.ComposeInputs
            if ($inputs.Version -ne 1 -or $inputs.Project -cne $Project -or $inputs.DatabaseEngine -cne $value.DatabaseEngine -or
                ($value['IdentityProvider'] -cin @('keycloak', 'self-contained') -and $inputs.IdentityProvider -cne $value.IdentityProvider) -or $inputs.EnvironmentFile -cne $value.EnvironmentFile -or
                $inputs.ComposeFile -cne $value.ComposeFile -or $inputs.ProcessEnvironment -isnot [Collections.IDictionary] -or
                $inputs.Files -isnot [array] -or $inputs.Files.Count -eq 0 -or
                -not $inputs.ProcessEnvironment.Contains('COMPOSE_PROFILES')) { throw 'Contradictory Compose inputs' }
            foreach ($name in $inputs.ProcessEnvironment.Keys) {
                if ($name -cnotmatch '^[a-zA-Z_][a-zA-Z0-9_]*$' -or
                    ($null -ne $inputs.ProcessEnvironment[$name] -and $inputs.ProcessEnvironment[$name] -isnot [string])) { throw 'Invalid Compose input' }
            }
            foreach ($file in $inputs.Files) {
                if (-not [IO.Path]::IsPathFullyQualified($file.Path) -or
                    (Get-FileHash -LiteralPath $file.Path -ErrorAction Stop).Hash -cne $file.Hash) { throw 'Changed selected Compose file' }
            }
            if (@(Get-ChildItem Env:DMS_CDC__*).Count -gt 0) { throw 'Overrides' }
        }
    }
    catch { throw 'CDC deployment inventory or original configuration is missing, changed, or unreadable. Retain infrastructure and reconcile the original deployment; configuration removal is not cleanup authority.' }
    if ($value['IdentityProvider'] -cnotin @('keycloak', 'self-contained') -or
        @($value.Entries | Where-Object { $_['IdentityProvider'] -cne $value.IdentityProvider }).Count -gt 0) {
        throw 'CDC deployment IdentityProvider is missing, unsupported, or contradictory. Restore the original complete deployment inventory with its retained identity-provider selection; an environment default or new override cannot recover it.'
    }
    return $value
}

function Get-CdcGeneratedRuntimePath {
    param([hashtable]$Deployment)
    $root = Join-Path $PSScriptRoot '.bootstrap/cdc-runtime'
    foreach ($entry in $Deployment.Entries) {
        foreach ($path in @($entry.SettingsPath, $entry.DmsComposePath)) {
            if ([IO.Path]::GetDirectoryName($path) -ceq $root -and
                [IO.Path]::GetFileName($path) -cmatch '^bootstrap-[a-f0-9]{32}\.(settings|dms)\.json$') { $path }
        }
    }
}

function Test-CdcNestedStateRoot {
    param([hashtable]$Deployment, [string]$BootstrapRoot)
    $root = [IO.Path]::GetFullPath($BootstrapRoot).TrimEnd('/')
    foreach ($entry in $Deployment.Entries) {
        $state = [IO.Path]::GetFullPath($entry.StatePath).TrimEnd('/')
        if (($state -ceq $root -or $state.StartsWith("$root/", [StringComparison]::Ordinal) -or
            $root.StartsWith("$state/", [StringComparison]::Ordinal)) -and
            (Test-Path -LiteralPath $state)) { return $true }
    }
    return $false
}

function Test-CdcBootstrapWorkspaceProtected {
    <#
    .SYNOPSIS
    Keeps retained deployment configuration and source state out of recursive workspace removal.
    #>
    param([string]$BootstrapRoot = (Join-Path $PSScriptRoot '.bootstrap'))
    $retained = $false
    foreach ($project in @('dms-local', 'dms-published')) {
        # Resolve against the supplied workspace for the shared E2E cleanup helper.
        $path = Join-Path (Split-Path $BootstrapRoot -Parent) ".cdc-deployments/$project.json"
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $retained = $true
        try {
            Assert-CdcPrivatePath (Split-Path $path -Parent) -Directory
            Assert-CdcPrivatePath $path
            $deployment = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
            if ($deployment.Version -ne 1 -or $deployment.Project -cne $project -or @($deployment.Entries).Count -eq 0) { throw 'Inventory' }
            $nested = Test-CdcNestedStateRoot $deployment $BootstrapRoot
        }
        catch { throw 'CDC workspace protection inventory is unreadable; retain the workspace and original deployment state.' }
        if ($nested) { throw 'CDC bootstrap workspace removal is blocked by a surviving protected source-state root. Retain its journals and source history; governed retirement does not authorize their deletion.' }
    }
    return $retained -or (Test-Path -LiteralPath (Join-Path $BootstrapRoot 'cdc-runtime'))
}

function Complete-CdcRuntimeCleanup {
    param([hashtable]$Deployment, [switch]$RemoveBootstrap)
    $root = Join-Path $PSScriptRoot '.bootstrap/cdc-runtime'
    try {
        if (Test-Path -LiteralPath $root) { Assert-CdcPrivatePath $root -Directory }
        $peers = @(foreach ($project in @('dms-local', 'dms-published')) {
            if ($project -cne $Deployment.Project -and (Test-CdcDeployment $project)) { Read-CdcDeployment $project }
        })
        foreach ($file in $Deployment.RuntimeCleanupFiles) {
            # Never delete through a source-state root, even if it contains a generated file.
            foreach ($owner in @($Deployment) + $peers) {
                foreach ($entry in $owner.Entries) {
                    $state = [IO.Path]::GetFullPath($entry.StatePath).TrimEnd('/')
                    if ($file.Path.StartsWith("$state/", [StringComparison]::Ordinal) -or $file.Path -ceq $state) { throw 'Protected state' }
                }
            }
            if (@($peers | ForEach-Object { $_.Entries } | Where-Object { $_.SettingsPath -ceq $file.Path -or $_.DmsComposePath -ceq $file.Path }).Count -gt 0) { throw 'Peer configuration' }
            if (-not (Test-Path -LiteralPath $file.Path)) { continue }
            Assert-CdcPrivatePath $file.Path
            if ((Get-FileHash -LiteralPath $file.Path -ErrorAction Stop).Hash -cne $file.Hash) { throw 'Changed generated configuration' }
            Remove-Item -LiteralPath $file.Path -Force -ErrorAction Stop
        }
        if ((Test-Path -LiteralPath $root) -and
            @(@($Deployment) + $peers | Where-Object { Test-CdcNestedStateRoot $_ $root }).Count -eq 0 -and @(Get-ChildItem -LiteralPath $root -Force -ErrorAction Stop).Count -eq 0) {
            # Nonrecursive removal also refuses a directory populated during cleanup.
            [IO.Directory]::Delete($root)
        }
        & sync -f $PSScriptRoot 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Cleanup flush' }
    }
    catch {
        if ($_.Exception.Message -ceq 'Protected state') { throw 'CDC bootstrap workspace removal is blocked by a surviving protected source-state root. Retain its journals and source history; governed retirement does not authorize their deletion.' }
        throw 'CDC generated runtime cleanup is incomplete; retain the deployment inventory and retry destructive teardown. Protected state, peer configuration, and changed files cannot be removed.'
    }
    # A nested state root still needs its retained inventory to protect later recursive cleanup,
    # even when cdc-runtime is empty. External source journals/history are never removed.
    if (-not (Test-CdcNestedStateRoot $Deployment (Join-Path $PSScriptRoot '.bootstrap'))) {
        [IO.File]::Delete((Get-CdcDeploymentPath $Deployment.Project))
        & sync -f (Split-Path (Get-CdcDeploymentPath $Deployment.Project) -Parent) 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'CDC inventory removal flush failed.' }
    }
    if ($RemoveBootstrap) {
        Import-Module (Join-Path $PSScriptRoot 'bootstrap-manifest.psm1')
        Remove-BootstrapWorkspaceIfRequested -RemoveBootstrap
    }
}

function Register-CdcDeploymentHandoff {
    <#
    .SYNOPSIS
    Retains each target handoff before connector effects so later stop and cleanup include it.
    #>
    param($Handoff, [string]$StatePath, $Receipt)
    $Handoff = $Handoff | ConvertTo-Json -Depth 64 | ConvertFrom-Json -AsHashtable
    if ($Handoff['IdentityProvider'] -cnotin @('keycloak', 'self-contained')) {
        throw 'CDC handoff requires the original resolved IdentityProvider selection.'
    }
    $settings = $Handoff.Settings
    $compose = $settings.Cdc.Compose
    $project = $compose.Project
    if (-not $Handoff.Contains('OriginalEnvironmentFile')) { $Handoff.OriginalEnvironmentFile = $compose.EnvironmentFile }
    $lock = Enter-CdcDeploymentLock $project
    try {
        $deployment = if (Test-CdcDeployment $project) { Read-CdcDeployment $project } else {
            @{
                Version = 1; Project = $project; DatabaseEngine = $settings.AppSettings.Datastore
                IdentityProvider = $Handoff.IdentityProvider
                OriginalEnvironmentFile = $Handoff.OriginalEnvironmentFile
                EnvironmentFile = $compose.EnvironmentFile; EnvironmentHash = (Get-FileHash -LiteralPath $compose.EnvironmentFile).Hash
                ComposeFile = $compose.File; BrokerSizeOverrideFile = $compose.BrokerSizeOverrideFile
                WorkerKey = $settings.Cdc.Worker.Key; ConnectEndpoint = $settings.Cdc.ConnectEndpoint
                WorkerMetricsEndpoint = $settings.Cdc.WorkerMetricsEndpoint
                OffsetStorageTopic = $settings.Cdc.Worker.OffsetStorageTopic
                Phase = 'Active'; Entries = @()
            }
        }
        if ($deployment.Entries.Count -eq 0) {
            try {
                $deployment.ComposeInputs = Get-CdcComposeInput $Handoff
                $deployment.ComposeInputsHash = Get-CdcComposeInputHash $deployment.ComposeInputs
            }
            catch { throw 'CDC selected Compose inputs could not be retained. No deployment effects are authorized.' }
        }
        if ($deployment.IdentityProvider -cne $Handoff.IdentityProvider -or
            $deployment.Phase -ne 'Active' -or $deployment.DatabaseEngine -cne $settings.AppSettings.Datastore -or
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
            IdentityProvider = $Handoff.IdentityProvider
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
            IdentityProvider = $deployment.IdentityProvider
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
    $environmentSnapshot = @{}
    try {
        $deployment = Read-CdcDeployment $Project
        if ($deployment.Phase -ne 'Active') { throw 'CDC deployment was stopped during admission; DMS handoff is no longer authorized.' }
        if ($Parameters.ContainsKey('IdentityProvider') -and $Parameters.IdentityProvider -ine $deployment.IdentityProvider) {
            throw 'CDC lifecycle IdentityProvider conflicts with the retained deployment.'
        }
        $Parameters = $Parameters + @{}
        $Parameters.IdentityProvider = $deployment.IdentityProvider
        Set-CdcRetainedEnvironment $deployment $environmentSnapshot
        Invoke-CdcInfrastructure $StartScript $Parameters
    }
    finally { Restore-CdcRetainedEnvironment $environmentSnapshot; $lock.Dispose() }
}

function Invoke-CdcDeploymentLifecycle {
    <#
    .SYNOPSIS
    Governs all retained connectors before worker stop, retained startup, or destructive teardown.
    #>
    param([string]$Project, [string]$StartScript, [hashtable]$Parameters)
    $lock = Enter-CdcDeploymentLock $Project
    $environmentSnapshot = @{}
    try {
        $deployment = Read-CdcDeployment $Project
        # Explicit selection must agree; omitted values inherit the original selected environment.
        if ($Parameters.ContainsKey('IdentityProvider') -and $Parameters.IdentityProvider -ine $deployment.IdentityProvider) {
            throw 'CDC lifecycle IdentityProvider conflicts with the retained deployment.'
        }
        if (($Parameters.ContainsKey('DatabaseEngine') -and $Parameters.DatabaseEngine -cne $deployment.DatabaseEngine) -or
            ($Parameters['CdcBindingStatePath'] -and @($deployment.Entries | Where-Object { $_.StatePath -ceq [IO.Path]::GetFullPath($Parameters.CdcBindingStatePath) }).Count -eq 0) -or
            ($Parameters['CdcSettingsPath'] -and @($deployment.Entries | Where-Object { $_.SettingsPath -ceq [IO.Path]::GetFullPath($Parameters.CdcSettingsPath) }).Count -eq 0)) { throw 'CDC lifecycle selection conflicts with the retained deployment.' }
        # EnvironmentFile can be a base file composed by bootstrap. Always use the retained effective
        # snapshot, whose content hash was checked above; do not compose it again from caller defaults.
        if ($Parameters['EnvironmentFile'] -and [IO.Path]::GetFullPath($Parameters.EnvironmentFile) -cnotin @($deployment.EnvironmentFile, $deployment.OriginalEnvironmentFile)) {
            throw 'CDC lifecycle requires the original effective environment file; omit -EnvironmentFile to inherit it.'
        }
        $down = $Parameters['d'] -eq $true
        $destructive = $down -and $Parameters['v'] -eq $true
        if ($deployment.Phase -in @('Retired', 'RuntimeCleanup') -and -not $destructive) {
            throw 'CDC governed cleanup is complete; repeat destructive teardown with -d -v to finish infrastructure and inventory removal.'
        }
        if ($deployment.Phase -eq 'RuntimeCleanup') {
            Complete-CdcRuntimeCleanup $deployment -RemoveBootstrap:($Parameters['RemoveBootstrap'] -eq $true)
            return
        }
        foreach ($option in @(@('EnableKafkaUI', 'kafka-ui.yml'), @('EnableSwaggerUI', 'swagger-ui.yml'))) {
            if ($Parameters[$option[0]] -and (Join-Path $PSScriptRoot $option[1]) -cnotin @($deployment.ComposeInputs.Files | ForEach-Object { $_.Path })) {
                throw 'CDC lifecycle requires the original optional service inputs; new services are not part of this retained deployment.'
            }
        }
        Set-CdcRetainedEnvironment $deployment $environmentSnapshot
        if (-not $down -and -not $Parameters['InfraOnly']) { Get-CdcDmsComposeHandoff $deployment | Out-Null }
        if (-not $down -and ($Parameters['DbOnly'] -or $Parameters['DmsOnly'] -or $Parameters['DmsBaseUrl'] -or $Parameters['LoadSeedData'])) {
            throw 'Retained CDC startup uses the managed infrastructure/controller/DMS sequence; partial startup and seed flags are unsupported.'
        }
        $infrastructure = @{
            EnvironmentFile = $deployment.EnvironmentFile; DatabaseEngine = $deployment.DatabaseEngine
            IdentityProvider = $deployment.IdentityProvider
            SeparateConfigDatabase = $true; CdcKafkaInfrastructure = $true
            CdcBrokerSizeOverrideFile = $deployment.BrokerSizeOverrideFile
        }
        foreach ($flag in @('EnableKafkaUI', 'EnableSwaggerUI')) { if ($Parameters[$flag]) { $infrastructure[$flag] = $true } }
        # Retired is durable only after every governed artifact and the empty worker inventory
        # were verified. Compose may already have removed those services on a failed down -v;
        # resume infrastructure cleanup without requiring their live evidence again.
        if ($down -and $deployment.Phase -ne 'Retired' -and ($deployment.Phase -ne 'Stopped' -or (Test-CdcWorkerRunning $Project))) {
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
            $preparation = $infrastructure + @{ InfraOnly = $true; CdcDatabaseInfrastructure = $true; SuppressWriterGuidance = $true }
            $preparation.Remove('CdcKafkaInfrastructure')
            $preparation.Remove('EnableKafkaUI')
            $preparation.Remove('CdcBrokerSizeOverrideFile')
            Invoke-CdcInfrastructure $StartScript $preparation
            Invoke-CdcLifecycleCommand $deployment.Entries[0] 'start-worker'
            if ($Parameters['EnableKafkaUI'] -and -not $destructive) {
                Import-Module (Join-Path $PSScriptRoot 'bootstrap-cdc.psm1')
                $settings = Get-Content -LiteralPath $deployment.Entries[0].SettingsPath -Raw | ConvertFrom-Json -AsHashtable
                Invoke-BootstrapCdcKafkaUI -Handoff @{ Settings = $settings }
            }
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
            if ($destructive) {
                # Persist exact current hashes before deleting the first generated file. The
                # separate checkpoint permits retries without re-reading partially deleted inputs.
                $deployment.RuntimeCleanupFiles = @(foreach ($path in @(Get-CdcGeneratedRuntimePath $deployment)) {
                    @{ Path = $path; Hash = (Get-FileHash -LiteralPath $path -ErrorAction Stop).Hash }
                })
                $deployment.Phase = 'RuntimeCleanup'
                Write-CdcDeployment $Project $deployment
                Complete-CdcRuntimeCleanup $deployment -RemoveBootstrap:($Parameters['RemoveBootstrap'] -eq $true)
            }
        }
        elseif (-not $Parameters['InfraOnly']) {
            $merged = Get-CdcDmsComposeHandoff $deployment
            Invoke-CdcInfrastructure $StartScript @{
                EnvironmentFile = $deployment.EnvironmentFile; DatabaseEngine = $deployment.DatabaseEngine
                IdentityProvider = $deployment.IdentityProvider
                SeparateConfigDatabase = $true; DmsOnly = $true; CdcDmsComposeFile = $merged
            }
        }
    }
    finally { Restore-CdcRetainedEnvironment $environmentSnapshot; $lock.Dispose() }
}

Export-ModuleMember -Function Test-CdcBootstrapWorkspaceProtected, Get-CdcBootstrapRetryHandoff, Test-CdcDeployment, Test-CdcInfrastructureInvocation, Register-CdcDeploymentHandoff, Invoke-CdcDeploymentLifecycle, Invoke-CdcInfrastructure, Assert-CdcUnregisteredInfrastructure, Invoke-CdcAdmittedHost
