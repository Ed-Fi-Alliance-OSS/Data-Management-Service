# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'bootstrap-wrapper.psm1')
Import-Module (Join-Path $PSScriptRoot 'bootstrap-cdc.psm1')
Import-Module (Join-Path $PSScriptRoot 'cdc-lifecycle.psm1')
Import-Module (Join-Path $PSScriptRoot 'database-safety.psm1')
Import-Module (Join-Path $PSScriptRoot 'env-utility.psm1')
Import-Module (Join-Path $PSScriptRoot 'dms-schema-environment.psm1')

function Assert-E2ECdcWorkspaceAvailable {
    <#
    .SYNOPSIS
    Rejects destructive E2E setup when retained CDC configuration still owns the workspace.
    #>
    if (Test-CdcBootstrapWorkspaceProtected) {
        throw 'E2E setup cannot reset retained CDC state or configuration. Use the original deployment for governed retirement and retain its source history before preparing a new workspace.'
    }
}

function Invoke-E2ECdcSnapshotPreparation {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Parameters are captured by the schema-authority action scriptblock.')]
    param([string]$EnvironmentFile, [string]$DatabaseEngine, [string]$DatabaseName,
        [string]$Configuration, [switch]$UsePrebuiltTools)
    Import-Module (Join-Path $PSScriptRoot 'effective-schema-hash.psm1')
    Import-Module (Join-Path $PSScriptRoot 'bootstrap-schema-workspace.psm1')
    $global:LASTEXITCODE = 0
    $output = @(Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
        & (Join-Path $PSScriptRoot 'provision-e2e-database.ps1') -EnvironmentFile $EnvironmentFile `
            -DatabaseEngine $DatabaseEngine -DatabaseName $DatabaseName -Configuration $Configuration `
            -UsePrebuiltTools:$UsePrebuiltTools 6>&1
    })
    if ($LASTEXITCODE -ne 0) { throw 'E2E CDC snapshot preparation failed.' }
    $hash = Get-EffectiveSchemaHashFromOutput -Output $output
    if (-not $hash -or $hash -cne (Resolve-BootstrapSchemaWorkspace).EffectiveSchemaHash) {
        throw 'E2E snapshot schema does not match the staged primary schema.'
    }
}

function New-E2ECdcHttpOverride {
    <#
    .SYNOPSIS
    Creates a private HTTP-only replacement for the admitted DMS Compose override.
    .DESCRIPTION
    Pass the admitted (merged, for peers) override, then REPLACE that file in the HTTP
    Compose invocation. Layering this file over the original retains indexed targets.
    The caller must resolve the complete Compose configuration and check it with
    Assert-E2ECdcHttpConfiguration before rollout and before publishing the handoff.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Creates a unique private test override; never modifies retained admission inputs.')]
    param([Parameter(Mandatory)][string]$AdmittedComposePath)
    $ErrorActionPreference = 'Stop'
    # Admission reloads this module in bootstrap-wrapper's scope. Reattach it here
    # because that -Force import removes our earlier scoped module reference.
    Import-Module (Join-Path $PSScriptRoot 'bootstrap-cdc.psm1')
    $AdmittedComposePath = [IO.Path]::GetFullPath($AdmittedComposePath)
    & (Get-Module cdc-lifecycle) { param($path) Assert-CdcPrivatePath $path } $AdmittedComposePath
    $document = Get-Content -LiteralPath $AdmittedComposePath -Raw | ConvertFrom-Json -AsHashtable
    $environment = $document.services.dms.environment
    if ($environment -isnot [Collections.IDictionary]) { throw 'CDC HTTP override requires an admitted environment mapping.' }
    foreach ($key in @($environment.Keys)) {
        if ($key -match '^DataManagement(?:__|:)DocumentCache(?:__|:)Targets(?:(?:__|:)|$)' -or
            $key -match '^DataManagement(?:__|:)DocumentCache(?:__|:)ReadAcceleration(?:__|:)Enabled$') {
            $environment.Remove($key)
        }
    }
    $environment['DataManagement__DocumentCache__ReadAcceleration__Enabled'] = 'false'
    $path = Join-Path (Split-Path $AdmittedComposePath -Parent) "api-e2e-$([guid]::NewGuid().ToString('N')).http.json"
    & (Get-Module bootstrap-cdc) { param($path, $value) Write-BootstrapCdcPrivateJson $path $value } $path $document
    return $path
}

function Assert-E2ECdcHttpConfiguration {
    <#
    .SYNOPSIS
    Checks docker compose config --format json output privately, without logging it.
    #>
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Configuration)
    $environment = $Configuration.services.dms.environment
    if ($environment -isnot [Collections.IDictionary] -or
        @($environment.Keys | Where-Object { $_ -match '^DataManagement(?:__|:)DocumentCache(?:__|:)Targets(?:(?:__|:)|$)' }).Count -ne 0 -or
        $environment['DataManagement__DocumentCache__ReadAcceleration__Enabled'] -ine 'false' -or
        @($environment.Keys | Where-Object {
            $_ -match '^DataManagement(?:__|:)DocumentCache(?:__|:)ReadAcceleration(?:__|:)Enabled$' -and $environment[$_] -ine 'false'
        }).Count -ne 0) {
        throw 'CDC HTTP configuration must have no projection targets and disabled read acceleration.'
    }
}

function Resolve-E2ECdcHttpBaseUrl {
    <#
    .SYNOPSIS
    Resolves both health and fixture HTTP access from the effective Compose configuration.
    #>
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Configuration)
    $dms = $Configuration.services.dms
    $ports = @($dms.ports | Where-Object { $_.protocol -eq 'tcp' -and $_.host_ip -in @('127.0.0.1', '0.0.0.0') })
    if ($ports.Count -ne 1 -or [int]$ports[0].published -lt 1 -or [int]$ports[0].published -gt 65535) {
        throw 'CDC API E2E requires one resolved host-reachable HTTP port.'
    }
    $pathBase = [string]$dms.environment['AppSettings__PathBase']
    if ($pathBase -and ($pathBase -match '[:?#\\]' -or $pathBase.StartsWith('//'))) {
        throw 'CDC API E2E HTTP path base is invalid.'
    }
    # Program.cs passes /{pathBase.Trim('/')} to UsePathBase. The shipped environment
    # uses "api", so normalize both unprefixed and prefixed values as the host does.
    $pathBase = $pathBase.Trim('/')
    $suffix = if ($pathBase) { "/$pathBase" } else { '' }
    return "http://127.0.0.1:$($ports[0].published)$suffix"
}

function Write-E2ECdcApiHandoff {
    <#
    .SYNOPSIS
    Publishes the private fixture attachment only after the caller completes HTTP rollout.
    .DESCRIPTION
    Private v1 JSON contract, supplied to the fixture through CDC_API_E2E_HANDOFF_PATH:
      version: 1
      settingsPath: retained selected entry; load with CdcCommandConfiguration.Load.
      statePath: original selected controller state root (never a copy or a new journal).
      deploymentPath: original inventory; authority for Compose inputs and teardown.
      httpComposePath: separate target-free override, retained for bounded cleanup.
      dmsBaseUrl: host HTTP URL resolved from the effective Compose port and path base.

    No provider, binding, generation, schema, credential, or worker inventory is copied.
    The fixture loads runtime/controller configuration through CdcCommandConfiguration.Load
    and resolves the retained binding through the production state store. That settings file
    owns admitted targets, host CMS URL, provider setup connection, Kafka advertised endpoint,
    Connect/metrics endpoints, Compose/worker identity and schema assets. Preserve connector
    ProviderConnectionProperties (container endpoints); any fixture host adaptations belong
    only in memory, using Cdc:Compose:DatabaseHostPort. Never fall back to static E2E settings.

    Neither this file, the override nor raw settings belongs in public evidence. The caller
    owns rollout/health ordering and removes these two private test files only after successful
    governed teardown. A returned path is the only publication point; failures retain originals.
    #>
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$StatePath,
        [Parameter(Mandatory)][string]$HttpComposePath,
        [Parameter(Mandatory)][System.Collections.IDictionary]$EffectiveConfiguration
    )
    $ErrorActionPreference = 'Stop'
    Assert-E2ECdcHttpConfiguration $EffectiveConfiguration
    $deployment = & (Get-Module cdc-lifecycle) { param($project) Read-CdcDeployment $project } $Project
    if ($deployment.Phase -cne 'Active') { throw 'CDC API E2E handoff requires an active admitted deployment.' }
    $state = [IO.Path]::GetFullPath($StatePath)
    $entries = @($deployment.Entries | Where-Object { $_.StatePath -ceq $state })
    if ($entries.Count -ne 1) { throw 'CDC API E2E handoff requires one retained entry at the selected state root.' }
    $entry = $entries[0]
    & (Get-Module cdc-lifecycle) { param($path) Assert-CdcPrivatePath $path -Directory } $state
    $http = [IO.Path]::GetFullPath($HttpComposePath)
    $deploymentPath = & (Get-Module cdc-lifecycle) { param($project) Get-CdcDeploymentPath $project } $Project
    if ($http -cin @($deployment.Entries | ForEach-Object { $_.DmsComposePath }) -or
        (Split-Path $http -Parent) -cnotin @((Split-Path $entry.DmsComposePath -Parent), (Split-Path $deploymentPath -Parent)) -or
        [IO.Path]::GetFileName($http) -cnotmatch '^api-e2e-[a-f0-9]{32}\.http\.json$') {
        throw 'CDC API E2E requires a separate private HTTP override beside the retained configuration.'
    }
    & (Get-Module cdc-lifecycle) { param($path) Assert-CdcPrivatePath $path } $http
    Assert-E2ECdcHttpConfiguration (Get-Content -LiteralPath $http -Raw | ConvertFrom-Json -AsHashtable)
    $settings = Get-Content -LiteralPath $entry.SettingsPath -Raw | ConvertFrom-Json -AsHashtable
    if (@($settings.Cdc.Schemas).Count -eq 0) { throw 'CDC API E2E requires retained schema references.' }
    foreach ($path in $settings.Cdc.Schemas) {
        if (-not [IO.Path]::IsPathFullyQualified($path)) { throw 'CDC API E2E requires absolute schema references.' }
        # Open each reference to reject missing/unreadable files, without emitting schema content.
        $stream = [IO.File]::OpenRead($path)
        $stream.Dispose()
    }
    $dmsBaseUrl = Resolve-E2ECdcHttpBaseUrl -Configuration $EffectiveConfiguration
    $handoff = @{
        version = 1; settingsPath = $entry.SettingsPath; statePath = $state
        deploymentPath = $deploymentPath; httpComposePath = $http
        dmsBaseUrl = $dmsBaseUrl
    }
    $path = $http.Replace('.http.json', '.handoff.json')
    $temporary = "$path.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        & (Get-Module bootstrap-cdc) { param($path, $value) Write-BootstrapCdcPrivateJson $path $value } $temporary $handoff
        [IO.File]::Move($temporary, $path, $false)
    }
    finally { if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) } }
    return $path
}

function Invoke-E2ECdcHttpPreparation {
    <#
    .SYNOPSIS
    Uses the launcher's exact file selection and retained process environment. No raw config output.
    #>
    param([string[]]$ComposeFiles, [string]$EnvironmentFile, [string]$Project)
    $output = @(docker compose @ComposeFiles --env-file $EnvironmentFile -p $Project config --format json 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'CDC API E2E HTTP configuration resolution failed.' }
    try { $configuration = ($output -join "`n") | ConvertFrom-Json -AsHashtable }
    catch { throw 'CDC API E2E HTTP configuration is invalid.' }
    Assert-E2ECdcHttpConfiguration $configuration
    $null = Resolve-E2ECdcHttpBaseUrl -Configuration $configuration
    # Compose waits for the old process to exit (including hosted executor shutdown) before
    # returning. A timeout kills that process; no old executor can survive into attachment.
    $null = docker compose @ComposeFiles --env-file $EnvironmentFile -p $Project stop --timeout 60 dms 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'CDC API E2E previous HTTP host shutdown failed.' }
    $running = @(docker compose @ComposeFiles --env-file $EnvironmentFile -p $Project ps --status running -q dms 2>$null)
    if ($LASTEXITCODE -ne 0 -or @($running | Where-Object { $_ }).Count -ne 0) {
        throw 'CDC API E2E previous HTTP host is still running.'
    }
    return $configuration
}

function Assert-E2ECdcApiAttachment {
    <#
    .SYNOPSIS
    Checks fixture attachment read-only, retaining the wrapper as inventory/hash authority.
    #>
    param([Parameter(Mandatory)][string]$HandoffPath)
    $ErrorActionPreference = 'Stop'
    $handoff = Get-Content -LiteralPath $HandoffPath -Raw | ConvertFrom-Json -AsHashtable
    & (Get-Module cdc-lifecycle) { param($p) Assert-CdcPrivatePath $p } $HandoffPath
    if ($handoff.version -ne 1) { throw 'Version' }
    $settings = Get-Content -LiteralPath $handoff.settingsPath -Raw | ConvertFrom-Json -AsHashtable
    $deployment = & (Get-Module cdc-lifecycle) { param($p) Read-CdcDeployment $p } $settings.Cdc.Compose.Project
    $path = & (Get-Module cdc-lifecycle) { param($p) Get-CdcDeploymentPath $p } $deployment.Project
    $entries = @($deployment.Entries | Where-Object { $_.SettingsPath -ceq $handoff.settingsPath -and $_.StatePath -ceq $handoff.statePath })
    if ($deployment.Phase -cne 'Active' -or $path -cne $handoff.deploymentPath -or $entries.Count -ne 1 -or
        $handoff.httpComposePath -cin @($deployment.Entries.DmsComposePath)) { throw 'Scope' }
    & (Get-Module cdc-lifecycle) { param($p) Assert-CdcPrivatePath $p; } $handoff.httpComposePath
    Assert-E2ECdcHttpConfiguration (Get-Content -LiteralPath $handoff.httpComposePath -Raw | ConvertFrom-Json -AsHashtable)
    # Check the running host, not just the override on disk. No container or worker mutation.
    $ids = @(& docker ps --filter "label=com.docker.compose.project=$($deployment.Project)" --filter 'label=com.docker.compose.service=dms' --format '{{.ID}}' 2>$null)
    if ($LASTEXITCODE -ne 0 -or $ids.Count -ne 1) { throw 'HTTP host' }
    $hostConfig = @(& docker inspect $ids[0] 2>$null | ConvertFrom-Json -AsHashtable)[0]
    if ($LASTEXITCODE -ne 0 -or -not $hostConfig.State.Running) { throw 'HTTP host' }
    $environment = @{}
    foreach ($value in $hostConfig.Config.Env) {
        $parts = $value.Split('=', 2)
        $environment[$parts[0]] = $parts[1]
    }
    $ports = @($hostConfig.NetworkSettings.Ports.Values | ForEach-Object { $_ } | Where-Object { $_.HostIp -in @('127.0.0.1', '0.0.0.0') })
    if ($ports.Count -ne 1) { throw 'HTTP port' }
    $expectedUrl = Resolve-E2ECdcHttpBaseUrl -Configuration @{ services = @{ dms = @{
        environment = $environment
        ports = @(@{ host_ip = $ports[0].HostIp; published = $ports[0].HostPort; protocol = 'tcp' })
    } } }
    if ($handoff.dmsBaseUrl -cne $expectedUrl) { throw 'HTTP endpoint' }
    Assert-E2ECdcHttpConfiguration @{ services = @{ dms = @{ environment = $environment } } }
}

function Invoke-E2ECdcApiRollout {
    param([string]$Project, [string]$StartScript, [string]$StatePath)
    $deployment = & (Get-Module cdc-lifecycle) { param($project) Read-CdcDeployment $project } $Project
    $admitted = & (Get-Module cdc-lifecycle) { param($deployment) Get-CdcDmsComposeHandoff $deployment } $deployment
    $http = New-E2ECdcHttpOverride -AdmittedComposePath $admitted
    Invoke-CdcAdmittedHost -Project $Project -StartScript $StartScript -Parameters @{
        EnvironmentFile = $deployment.EnvironmentFile; DatabaseEngine = $deployment.DatabaseEngine
        IdentityProvider = $deployment.IdentityProvider; SeparateConfigDatabase = $true
        DmsOnly = $true; CdcDmsComposeFile = $http; CdcApiE2E = $true; CdcBindingStatePath = $StatePath
    } | Out-Host
    $handoff = $http.Replace('.http.json', '.handoff.json')
    if (-not (Test-Path -LiteralPath $handoff -PathType Leaf)) { throw 'CDC API E2E HTTP rollout did not publish a handoff.' }
    return $handoff
}

function Remove-E2ECdcApiFile {
    <#
    .SYNOPSIS
    Removes private API E2E files after both projects complete governed destructive teardown.
    .DESCRIPTION
    Failed rollout overrides have no handoff, so enumerate only our exact private filename pattern.
    Files live beside the deployment inventories, never under a recursive cleanup root.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Bounded private-file cleanup after explicitly requested governed E2E teardown.')]
    param([string]$ComposeRoot = $PSScriptRoot)
    $root = Join-Path $ComposeRoot '.cdc-deployments'
    if (-not (Test-Path -LiteralPath $root)) { return }
    & (Get-Module cdc-lifecycle) { param($path) Assert-CdcPrivatePath $path -Directory } $root
    foreach ($project in @('dms-local', 'dms-published')) {
        $path = Join-Path $root "$project.json"
        if (Test-Path -LiteralPath $path) {
            # Nested source-state roots can retain an inventory after runtime cleanup.
            $deployment = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
            if ($deployment.Phase -cne 'RuntimeCleanup') { throw 'CDC API E2E cleanup requires completed governed teardown.' }
            foreach ($entry in $deployment.Entries) {
                $state = [IO.Path]::GetFullPath($entry.StatePath).TrimEnd('/')
                if ($root -ceq $state -or $root.StartsWith("$state/", [StringComparison]::Ordinal)) {
                    throw 'CDC API E2E cleanup cannot remove protected source state.'
                }
            }
        }
    }
    foreach ($file in @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Name -cmatch '^api-e2e-[a-f0-9]{32}\.(http|handoff)\.json$' })) {
        & (Get-Module cdc-lifecycle) { param($path) Assert-CdcPrivatePath $path } $file.FullName
        [IO.File]::Delete($file.FullName)
    }
}

function Invoke-E2ECdcSetup {
    <#
    .SYNOPSIS
    Runs the shared managed bootstrap admission before E2E DMS startup for either provider.
    .DESCRIPTION
    The primary is created through managed provisioning; it is never reset or replaced by the
    legacy E2E provisioner. Only the separate snapshot uses the E2E reset path. Failure retains
    configuration/provenance and attempts governed stop while infrastructure remains reachable.
    With -CdcApiE2E, admission is followed by target-free HTTP rollout; the sole success output
    is the private handoff path for CDC_API_E2E_HANDOFF_PATH. HTTP rollout failures retain the
    worker untouched for explicit governed teardown.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Parameters are captured by the bootstrap and snapshot action scriptblocks.')]
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$EnvironmentFile,
        [string]$OriginalEnvironmentFile,
        [ValidateSet('postgresql', 'mssql')][string]$DatabaseEngine = 'postgresql',
        [Parameter(Mandatory)][string]$DatabaseName,
        [Parameter(Mandatory)][string]$SnapshotDatabaseName,
        [Parameter(Mandatory)][string]$CdcSettingsPath,
        [string]$CdcBindingStatePath,
        [switch]$UsePublishedImage,
        [switch]$CdcApiE2E,
        [switch]$SkipDockerBuild,
        [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
        [switch]$UsePrebuiltTools,
        [ValidateSet('self-contained', 'keycloak')][string]$IdentityProvider = 'self-contained'
    )
    $ErrorActionPreference = 'Stop'
    $EnvironmentFile = [IO.Path]::GetFullPath($EnvironmentFile)
    if (-not $OriginalEnvironmentFile) { $OriginalEnvironmentFile = $EnvironmentFile }
    $OriginalEnvironmentFile = [IO.Path]::GetFullPath($OriginalEnvironmentFile)
    $CdcSettingsPath = [IO.Path]::GetFullPath($CdcSettingsPath)
    if (-not $CdcBindingStatePath) { $CdcBindingStatePath = Join-Path $PSScriptRoot '.cdc-state' }
    $CdcBindingStatePath = [IO.Path]::GetFullPath($CdcBindingStatePath)
    $project = if ($UsePublishedImage) { 'dms-published' } else { 'dms-local' }
    $startScript = if ($UsePublishedImage) { 'start-published-dms.ps1' } else { 'start-local-dms.ps1' }
    $values = ReadValuesFromEnvFile $EnvironmentFile
    if ($DatabaseName -cne (Get-ComposeResolvedEnvValue -EnvironmentValues $values -Name 'E2E_DATABASE_NAME') -or
        $SnapshotDatabaseName -cne (Get-ComposeResolvedEnvValue -EnvironmentValues $values -Name 'E2E_SNAPSHOT_DATABASE_NAME') -or
        $DatabaseName -eq $SnapshotDatabaseName) { throw 'CDC E2E requires the actual, distinct primary and snapshot database names from the selected environment.' }
    foreach ($name in @($DatabaseName, $SnapshotDatabaseName)) {
        Assert-E2EDatabaseIsDedicated -EnvironmentValues $values -EnvironmentFilePath $EnvironmentFile -E2EDatabaseName $name
    }
    Assert-E2ECdcWorkspaceAvailable
    $settings = Read-BootstrapCdcSettings -Path $CdcSettingsPath -DatabaseEngine $DatabaseEngine
    Assert-BootstrapCdcOfflineOwnership -Project $project -DatabaseEngine $DatabaseEngine `
        -CmsPort ([uri]$settings.ConfigurationServiceSettings.BaseUrl).Port

    $snapshotModule = $ExecutionContext.SessionState.Module
    $snapshot = {
        param($effectiveEnvironmentFile)
        & $snapshotModule {
            param($environment, $engine, $database, $buildConfiguration, $prebuilt)
            Invoke-E2ECdcSnapshotPreparation -EnvironmentFile $environment -DatabaseEngine $engine `
                -DatabaseName $database -Configuration $buildConfiguration -UsePrebuiltTools:$prebuilt
        } $effectiveEnvironmentFile $DatabaseEngine $SnapshotDatabaseName $Configuration $UsePrebuiltTools
    }.GetNewClosure()
    $httpRolloutStarted = $false
    try {
        # Bootstrap stages exactly the selected E2E packages and carries the same settings to DMS.
        Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
            Invoke-BootstrapWrapper -StartScriptName $startScript -EnvironmentFile $EnvironmentFile `
                -OriginalEnvironmentFile $OriginalEnvironmentFile -DatabaseEngine $DatabaseEngine `
                -EnableKafkaCdc -CdcSettingsPath $CdcSettingsPath -CdcBindingStatePath $CdcBindingStatePath `
                -DataStoreDatabaseName $DatabaseName -SeparateConfigDatabase -EnableConfig `
                -IdentityProvider $IdentityProvider -UseEnvironmentFileSchemaSettings -IncludeE2EClaimSets `
                -RebuildLocalImages:(!$SkipDockerBuild -and !$UsePublishedImage) -BeforeCdcAdmission $snapshot
        } | Out-Host
        if ($CdcApiE2E) {
            $httpRolloutStarted = $true
            return Invoke-E2ECdcApiRollout -Project $project -StartScript (Join-Path $PSScriptRoot $startScript) -StatePath $CdcBindingStatePath
        }
    }
    catch {
        $cancelled = $_.Exception -is [OperationCanceledException]
        $failureCodes = @($_.Exception.Data['CdcFailureCodes'] | Where-Object {
            $_ -cmatch '^(Request|WorkflowState|Projection|ProviderSetup|Kafka|Connect|Worker|Metrics|WriterPublication)/(InvalidInput|Unavailable|Timeout|AuthenticationFailed|Conflict|ValidationFailed)$'
        })
        # An HTTP-only rollout failure must not change the connector, offsets or worker.
        # The caller retains the original inventory for explicit governed teardown.
        $cleanup = if ($httpRolloutStarted) { 'RetainedForGovernedTeardown' } else { 'NotStarted' }
        if (-not $httpRolloutStarted -and (Test-CdcDeployment $project)) {
            try {
                $null = Invoke-CdcDeploymentLifecycle -Project $project -StartScript (Join-Path $PSScriptRoot $startScript) `
                    -Parameters @{ d = $true; EnvironmentFile = $OriginalEnvironmentFile; DatabaseEngine = $DatabaseEngine }
                $cleanup = 'Stopped'
            }
            catch { $cleanup = 'RetainedForReconciliation' }
        }
        # Never persist raw exceptions, CLI output, connection strings, or settings. Detailed typed
        # controller evidence remains at its original state root for cdc status/retirement.
        $diagnostic = @{ operation = 'e2e-setup'; succeeded = $false; cancelled = $cancelled; cleanup = $cleanup; provider = $DatabaseEngine; failureCodes = $failureCodes }
        $diagnosticRoot = if ($env:CDC_RUNBOOK_EVIDENCE_DIRECTORY) {
            Join-Path $env:CDC_RUNBOOK_EVIDENCE_DIRECTORY 'e2e-setup'
        } else { Join-Path ([IO.Path]::GetTempPath()) ('dms-cdc-diagnostics-' + [guid]::NewGuid().ToString('N')) }
        try {
            $diagnosticRoot = [IO.Path]::GetFullPath($diagnosticRoot)
            $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
            if ($diagnosticRoot -eq $repo -or $diagnosticRoot.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'CDC diagnostics must be outside the repository checkout.'
            }
            $null = [IO.Directory]::CreateDirectory($diagnosticRoot)
            $diagnostic | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $diagnosticRoot "$([guid]::NewGuid().ToString('N')).json") -ErrorAction Stop
        } catch { Write-Warning 'CDC E2E failure diagnostics could not be saved; the original setup failure is retained.' }
        if ($cancelled) { throw [OperationCanceledException]::new("CDC E2E setup cancelled; tests were not launched. Retain original state and temporary diagnostics at '$diagnosticRoot' for governed cleanup.") }
        throw "CDC E2E setup failed; tests were not launched. Retain original state and inspect temporary diagnostics at '$diagnosticRoot' plus SchemaTools cdc status before governed cleanup."
    }
}

Export-ModuleMember -Function Assert-E2ECdcApiAttachment, Assert-E2ECdcWorkspaceAvailable, Invoke-E2ECdcSetup, New-E2ECdcHttpOverride, Assert-E2ECdcHttpConfiguration, Write-E2ECdcApiHandoff, Invoke-E2ECdcHttpPreparation, Resolve-E2ECdcHttpBaseUrl, Remove-E2ECdcApiFile
