# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# This phase carries configuration and receipts. SchemaTools owns all binding, provider,
# Kafka, readiness and durable publication decisions.
Set-StrictMode -Version Latest

function Read-BootstrapCdcSettings {
    <#
    .SYNOPSIS
    Reads the explicit single-target DMS settings accepted by local CDC bootstrap.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Settings is the existing DMS configuration contract name.')]
    param([string]$Path, [string]$DatabaseEngine)
    try {
        $settings = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable
        # A snapshot must be the configuration both processes receive. Reject hidden overrides
        # rather than letting the CLI select a different target after the wrapper's checks.
        if (@(Get-ChildItem Env:DMS_CDC__*).Count -gt 0) { throw 'Overrides' }
        if ($settings.AppSettings.Datastore -cne $DatabaseEngine -or
            @($settings.DataManagement.DocumentCache.Targets).Count -ne 1 -or
            $settings.Cdc.DataStoreId -cne [string]$settings.DataManagement.DocumentCache.Targets[0].DataStoreId -or
            [long]$settings.Cdc.DataStoreId -le 0 -or [long]$settings.Cdc.Generation -le 0 -or
            $settings.Cdc.TenantKey -ne '' -or $settings.DataManagement.DocumentCache.Targets[0]['TenantKey'] -notin @($null, '') -or
            [string]::IsNullOrWhiteSpace($settings.Cdc.DeploymentKey) -or
            [string]::IsNullOrWhiteSpace($settings.Cdc.InstanceKey) -or
            $settings.AppSettings['MultiTenancy'] -eq $true) { throw 'Target' }
        # Limit the first local adapter to the route-unqualified default tenant. Provider token
        # conversion and the complete policy contract remain with the CLI.
        $cms = [uri]$settings.ConfigurationServiceSettings.BaseUrl
        if (-not $cms.IsLoopback -or $cms.Scheme -ne 'http' -or $cms.AbsolutePath -ne '/') { throw 'CMS' }
        # The qualified worker inspector proves an exact published IPv4 loopback endpoint.
        # Reject unsupported aliases before creating a database or retaining an initial workflow.
        foreach ($endpoint in @(@('ConnectEndpoint', '/'), @('WorkerMetricsEndpoint', '/metrics'))) {
            $uri = [uri]$settings.Cdc[$endpoint[0]]
            if ($uri.Host -cne '127.0.0.1' -or $uri.Scheme -cne 'http' -or
                $uri.AbsolutePath -cne $endpoint[1] -or $uri.Query -or $uri.UserInfo -or $uri.Fragment) { throw 'Worker endpoint' }
        }
        return $settings
    }
    catch { throw 'CDC requires valid explicit DMS settings with one default-tenant target, local CMS, and explicit http://127.0.0.1 worker/metrics endpoints; environment overrides are not accepted by bootstrap.' }
}

function Invoke-BootstrapCdcDockerInspection {
    param([string[]]$Arguments)
    $output = @(& docker @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'CDC local deployment inspection is unavailable.' }
    return $output
}

function Get-BootstrapCdcHostProcessCommand {
    if ($IsLinux -or $IsMacOS) {
        $commands = @(& ps -eo args= 2>$null)
        if ($LASTEXITCODE -ne 0) { throw 'CDC host process inspection is unavailable.' }
        return $commands
    }
    return @(Get-CimInstance Win32_Process -ErrorAction Stop | ForEach-Object { $_.CommandLine })
}

function Assert-BootstrapCdcOfflineOwnership {
    <#
    .SYNOPSIS
    Inspects local processes and Compose topology before allowing the offline CDC phase.
    #>
    param([string]$Project, [switch]$InfrastructureReady, [string]$DatabaseEngine, [int]$CmsPort)
    try {
        foreach ($command in @(Get-BootstrapCdcHostProcessCommand)) {
            if ($command -match '(?i)EdFi\.DataManagementService\.Frontend\.AspNetCore(\.dll|\.exe|\s|$)') {
                throw 'Host writer'
            }
        }
        $ids = @(Invoke-BootstrapCdcDockerInspection -Arguments @('ps', '-q'))
        $containers = @()
        if ($ids.Count -gt 0) {
            # Deliberately select metadata only; never return Docker environment/credentials.
            $format = '{"name":{{json .Name}},"project":{{json (index .Config.Labels "com.docker.compose.project")}},"service":{{json (index .Config.Labels "com.docker.compose.service")}},"directory":{{json (index .Config.Labels "com.docker.compose.project.working_dir")}},"ports":{{json .NetworkSettings.Ports}},"networks":{{json .NetworkSettings.Networks}},"command":{{json .Config.Cmd}},"entrypoint":{{json .Config.Entrypoint}}}'
            $containers = @(Invoke-BootstrapCdcDockerInspection -Arguments (@('inspect', '--format', $format) + $ids) | ForEach-Object { $_ | ConvertFrom-Json -AsHashtable })
        }
        foreach ($container in $containers) {
            $isOwned = $container.project -ceq $Project -and
                $container.directory -and [IO.Path]::GetFullPath($container.directory) -ceq [IO.Path]::GetFullPath($PSScriptRoot)
            if ($container.name -eq '/ed-fi-api' -or $container.service -eq 'dms' -or
                (($container.command + $container.entrypoint) -join ' ') -match 'EdFi\.DataManagementService\.Frontend\.AspNetCore') { throw 'Writer' }
            if ($container.networks.Contains('dms') -and (-not $isOwned -or
                $container.service -notin @('db', 'config', 'kafka', 'kafka-cdc-worker', 'kafka-ui', 'keycloak', 'swagger-ui'))) { throw 'Shared network' }
            if ($container.project -ceq $Project -and $container.service -in @('db', 'config') -and
                (-not $isOwned -or -not $container.networks.Contains('dms'))) { throw 'Infrastructure scope' }
            if ($container.service -in @('db', 'config') -and $isOwned) {
                foreach ($bindings in $container.ports.Values) {
                    foreach ($binding in @($bindings)) {
                        if ($null -ne $binding -and $binding.HostIp -notin @('127.0.0.1', '::1')) { throw 'Remote access' }
                    }
                }
            }
        }
        if ($InfrastructureReady) {
            $cms = @($containers | Where-Object { $_.project -ceq $Project -and $_.service -eq 'config' })
            $db = @($containers | Where-Object { $_.project -ceq $Project -and $_.service -eq 'db' })
            if ($cms.Count -ne 1 -or $db.Count -ne 1 -or
                $cms[0].name -cne '/ed-fi-api-config-service' -or
                @($cms[0].ports.Values | ForEach-Object { $_ } | Where-Object { $_.HostPort -eq [string]$CmsPort }).Count -eq 0 -or
                $db[0].name -cne $(if ($DatabaseEngine -eq 'mssql') { '/dms-mssql' } else { '/dms-postgresql' })) { throw 'Ownership' }
        }
    }
    catch { throw 'CDC requires an exclusively owned local deployment, loopback CMS/database ports, and no running DMS or IDE writers. Stop other applications before retrying.' }
}

function Write-BootstrapCdcPrivateJson {
    param([string]$Path, $Value)
    if ($IsWindows) { throw 'CDC bootstrap private-file creation currently requires Unix owner permissions.' }
    $directory = Split-Path $Path -Parent
    if (-not (Test-Path -LiteralPath $directory)) {
        [IO.Directory]::CreateDirectory($directory, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute) | Out-Null
    }
    if ((Get-Item -LiteralPath $directory -Force).LinkType) { throw 'CDC state directory must not be a link.' }
    if (([int][IO.File]::GetUnixFileMode($directory) -band 511) -ne 448) { throw 'CDC private configuration directory requires owner-only permissions.' }
    $options = [IO.FileStreamOptions]::new()
    $options.Mode = [IO.FileMode]::CreateNew
    $options.Access = [IO.FileAccess]::Write
    $options.Share = [IO.FileShare]::None
    if (-not $IsWindows) { $options.UnixCreateMode = [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite }
    $file = [IO.FileStream]::new($Path, $options)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 64 -Compress))
        $file.Write($bytes)
        $file.Flush($true)
    }
    finally { $file.Dispose() }
}

function ConvertTo-BootstrapCdcEnvironment {
    param($Value, [string]$Prefix, [hashtable]$Result)
    if ($Value -is [System.Collections.IDictionary]) {
        foreach ($key in $Value.Keys) {
            ConvertTo-BootstrapCdcEnvironment -Value $Value[$key] -Prefix "$Prefix`__$key" -Result $Result
        }
    }
    elseif ($Value -is [array]) {
        for ($i = 0; $i -lt $Value.Count; $i++) {
            ConvertTo-BootstrapCdcEnvironment -Value $Value[$i] -Prefix "$Prefix`__$i" -Result $Result
        }
    }
    elseif ($null -ne $Value) {
        # Compose also interpolates JSON strings. Escape literal '$', especially in secrets.
        $Result[$Prefix] = ([string]$Value).Replace('$', '$$')
    }
}

function New-BootstrapCdcHandoff {
    <#
    .SYNOPSIS
    Snapshots supplied settings with staged schemas and the selected local deployment context.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Creates unique private configuration snapshots within the already authorized bootstrap workflow; no interactive approval surface.')]
    param([hashtable]$Settings, [string]$InputSettingsPath, [string]$StatePath, [string]$EnvironmentFile, [string]$Project, [string]$DatabaseName)
    Import-Module (Join-Path $PSScriptRoot 'bootstrap-schema-workspace.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot 'env-utility.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot 'database-safety.psm1') -Force
    $envValues = ReadValuesFromEnvFile $EnvironmentFile
    if (Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name 'DMS_DOCUMENTCACHE_COMPOSE_FILE' -DefaultValue '') {
        throw 'CDC bootstrap supplies its explicit DocumentCache settings handoff; an additional DocumentCache Compose override is unsupported.'
    }
    $portName = if ($Settings.AppSettings.Datastore -eq 'mssql') { 'MSSQL_PORT' } else { 'POSTGRES_PORT' }
    $defaultPort = if ($Settings.AppSettings.Datastore -eq 'mssql') { '1433' } else { '5432' }
    $databasePort = [int](Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name $portName -DefaultValue $defaultPort)
    if ($databasePort -lt 1 -or $databasePort -gt 65535) { throw 'CDC database host port is invalid.' }
    $initialDatabaseKey = if ($Settings.AppSettings.Datastore -eq 'mssql') { 'MSSQL_DB_NAME' } else { 'POSTGRES_DB_NAME' }
    $initialDatabase = Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name $initialDatabaseKey -DefaultValue 'edfi_datamanagementservice'
    if (-not $DatabaseName -or $DatabaseName -eq $initialDatabase -or
        $DatabaseName -in @('edfi_configurationservice', 'postgres', 'master', 'model', 'msdb', 'tempdb')) {
        throw 'CDC requires a dedicated database distinct from infrastructure-created databases.'
    }
    $cmsPort = [int](Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name 'DMS_CONFIG_ASPNETCORE_HTTP_PORTS' -DefaultValue '8081')
    if (([uri]$Settings.ConfigurationServiceSettings.BaseUrl).Port -ne $cmsPort) {
        throw 'CDC settings must address the CMS port of the selected local environment.'
    }
    foreach ($name in @('DMS_CONFIG_MULTI_TENANCY', 'DMS_MULTI_TENANCY')) {
        if ((Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name $name -DefaultValue 'false') -ne 'false') {
            throw 'CDC bootstrap requires the default tenant topology.'
        }
    }
    foreach ($name in @('CONFIG_SERVICE_TENANT', 'ROUTE_QUALIFIER_SEGMENTS')) {
        if (Get-ComposeResolvedEnvValue -EnvironmentValues $envValues -Name $name -DefaultValue '') {
            throw 'CDC bootstrap requires the route-unqualified default tenant topology.'
        }
    }
    $workspace = Resolve-BootstrapSchemaWorkspace
    $settingsCopy = $Settings | ConvertTo-Json -Depth 64 | ConvertFrom-Json -AsHashtable
    $settingsCopy.Cdc.Schemas = @($workspace.CoreSchemaPath) + @($workspace.ExtensionSchemaPaths)
    $settingsCopy.Cdc.Compose = @{
        File = Join-Path $PSScriptRoot 'kafka-cdc.yml'
        EnvironmentFile = $EnvironmentFile
        Project = $Project
        BrokerSizeOverrideFile = Join-Path $StatePath 'broker-size.json'
        DatabaseHostPort = $databasePort
    }
    $settingsCopy.AppSettings.UseApiSchemaPath = $true
    $settingsCopy.AppSettings.ApiSchemaPath = Join-Path $PSScriptRoot '.bootstrap/ApiSchema'
    # Private deployment configuration is separate from the secret-free controller journal.
    $configurationRoot = Join-Path $PSScriptRoot '.bootstrap/cdc-runtime'
    $id = [guid]::NewGuid().ToString('N')
    $settingsPath = Join-Path $configurationRoot "bootstrap-$id.settings.json"
    Write-BootstrapCdcPrivateJson -Path $settingsPath -Value $settingsCopy
    $environment = @{}
    ConvertTo-BootstrapCdcEnvironment -Value $settingsCopy.DataManagement.DocumentCache -Prefix 'DataManagement__DocumentCache' -Result $environment
    # CMS credentials and target resolution must be identical; only the network address changes
    # between the host-side controller and Compose DMS. No CDC credentials enter the HTTP host.
    ConvertTo-BootstrapCdcEnvironment -Value $settingsCopy.ConfigurationServiceSettings -Prefix 'ConfigurationServiceSettings' -Result $environment
    $cmsPort = ([uri]$settingsCopy.ConfigurationServiceSettings.BaseUrl).Port
    $environment.ConfigurationServiceSettings__BaseUrl = "http://ed-fi-api-config-service:$cmsPort"
    $environment.AppSettings__Datastore = $settingsCopy.AppSettings.Datastore
    $environment.AppSettings__MultiTenancy = 'false'
    $environment.AppSettings__RouteQualifierSegments = ''
    $environment.AppSettings__UseApiSchemaPath = 'true'
    $environment.AppSettings__ApiSchemaPath = '/app/ApiSchema'
    $composePath = Join-Path $configurationRoot "bootstrap-$id.dms.json"
    Write-BootstrapCdcPrivateJson -Path $composePath -Value @{ services = @{ dms = @{ environment = $environment } } }
    return [pscustomobject]@{
        Settings = $settingsCopy; SettingsPath = $settingsPath; DmsComposePath = $composePath
        InputSettingsHash = (Get-FileHash -LiteralPath $InputSettingsPath).Hash
        DatabaseNameHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($DatabaseName)))
    }
}

function Invoke-BootstrapCdcEnable {
    <#
    .SYNOPSIS
    Runs SchemaTools CDC enable and requires matching controller publication authorization.
    #>
    param($Handoff, $Receipt, [long[]]$SelectedDataStoreIds, [string]$StatePath, [string]$ToolPath = '')
    try {
        if ($SelectedDataStoreIds.Count -ne 1 -or $Handoff.Settings.Cdc.DataStoreId -cne [string]$SelectedDataStoreIds[0] -or
            $Receipt.Target.DataStoreId -cne [string]$SelectedDataStoreIds[0] -or
            $Receipt.CreationReceipt.Outcome -cne 'Created' -or
            $Receipt.Target.DeploymentKey -cne $Handoff.Settings.Cdc.DeploymentKey -or
            $Receipt.Target.InstanceKey -cne $Handoff.Settings.Cdc.InstanceKey -or
            $Receipt.Target.Generation -ne $Handoff.Settings.Cdc.Generation) { throw 'Receipt' }
    }
    catch { throw 'CDC selected target and authoritative creation receipt must match the supplied settings; reused databases require cleanup/reprovisioning.' }
    Import-Module (Join-Path $PSScriptRoot 'cdc-lifecycle.psm1')
    Register-CdcDeploymentHandoff -Handoff $Handoff -StatePath $StatePath -Receipt $Receipt
    Import-Module (Join-Path $PSScriptRoot 'bootstrap-schema-tool.psm1') -Force
    $tool = Resolve-DmsSchemaTool -RequestedPath $ToolPath
    $arguments = @('cdc', 'enable', '--settings', $Handoff.SettingsPath, '--state-path', $StatePath, '--json')
    # Discard arbitrary native output on failure; only the known successful publication shape
    # can release the wrapper's writer/seed continuation. Durable authority stays in the controller.
    if ($tool.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase)) {
        $output = @(& pwsh -NoProfile -File $tool @arguments 2>$null)
    }
    else { $output = @(& $tool @arguments 2>$null) }
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $codes = @()
        try {
            $failure = ($output -join "`n") | ConvertFrom-Json
            $codes = @($failure.diagnostics | ForEach-Object {
                if ($_.component -cin @('Request', 'WorkflowState', 'Projection', 'ProviderSetup', 'Kafka', 'Connect', 'Worker', 'Metrics', 'WriterPublication') -and
                    $_.failure -cin @('InvalidInput', 'Unavailable', 'Timeout', 'AuthenticationFailed', 'Conflict', 'ValidationFailed')) {
                    "$($_.component)/$($_.failure)"
                }
            } | Select-Object -Unique)
        }
        catch { $codes = @() }
        $enableError = [InvalidOperationException]::new("CDC enable did not authorize writer publication (exit $exitCode). Retain the original state and inspect with SchemaTools cdc status.")
        $enableError.Data['CdcFailureCodes'] = $codes
        throw $enableError
    }
    try {
        $result = ($output -join "`n") | ConvertFrom-Json
        if ($result.operation -cne 'enable' -or $result.succeeded -ne $true -or $result.exitCode -ne 0 -or
            [guid]$result.data.workflowId -ne [guid]$Receipt.WorkflowId -or
            [DateTimeOffset]$result.data.authorizedAt -eq [DateTimeOffset]::MinValue) { throw 'Publication' }
    }
    catch { throw 'CDC enable returned no valid writer publication authorization.' }
}

Export-ModuleMember -Function Read-BootstrapCdcSettings, Assert-BootstrapCdcOfflineOwnership, New-BootstrapCdcHandoff, Invoke-BootstrapCdcEnable
