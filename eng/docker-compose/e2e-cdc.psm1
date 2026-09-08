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
    if ((Test-CdcDeployment 'dms-local') -or (Test-CdcDeployment 'dms-published') -or
        (Test-Path -LiteralPath (Join-Path $PSScriptRoot '.bootstrap/cdc-runtime'))) {
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

function Invoke-E2ECdcSetup {
    <#
    .SYNOPSIS
    Runs the shared managed bootstrap admission before E2E DMS startup for either provider.
    .DESCRIPTION
    The primary is created through managed provisioning; it is never reset or replaced by the
    legacy E2E provisioner. Only the separate snapshot uses the E2E reset path. Failure retains
    configuration/provenance and attempts governed stop while infrastructure remains reachable.
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
    try {
        # Bootstrap stages exactly the selected E2E packages and carries the same settings to DMS.
        Invoke-WithDmsEnvironmentFileSchemaAuthority -Action {
            Invoke-BootstrapWrapper -StartScriptName $startScript -EnvironmentFile $EnvironmentFile `
                -OriginalEnvironmentFile $OriginalEnvironmentFile -DatabaseEngine $DatabaseEngine `
                -EnableKafkaCdc -CdcSettingsPath $CdcSettingsPath -CdcBindingStatePath $CdcBindingStatePath `
                -DataStoreDatabaseName $DatabaseName -SeparateConfigDatabase -EnableConfig `
                -IdentityProvider $IdentityProvider -UseEnvironmentFileSchemaSettings `
                -RebuildLocalImages:(!$SkipDockerBuild -and !$UsePublishedImage) -BeforeCdcAdmission $snapshot
        } | Out-Host
    }
    catch {
        $cancelled = $_.Exception -is [OperationCanceledException]
        $failureCodes = @($_.Exception.Data['CdcFailureCodes'] | Where-Object {
            $_ -cmatch '^(Request|WorkflowState|Projection|ProviderSetup|Kafka|Connect|Worker|Metrics|WriterPublication)/(InvalidInput|Unavailable|Timeout|AuthenticationFailed|Conflict|ValidationFailed)$'
        })
        $cleanup = 'NotStarted'
        if (Test-CdcDeployment $project) {
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
        $diagnosticRoot = Join-Path $PSScriptRoot '.cdc-diagnostics'
        $null = [IO.Directory]::CreateDirectory($diagnosticRoot)
        $diagnostic | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $diagnosticRoot "$([guid]::NewGuid().ToString('N')).json")
        if ($cancelled) { throw [OperationCanceledException]::new('CDC E2E setup cancelled; tests were not launched. Retain original state for governed cleanup.') }
        throw 'CDC E2E setup failed; tests were not launched. Retain original state and inspect .cdc-diagnostics plus SchemaTools cdc status before governed cleanup.'
    }
}

Export-ModuleMember -Function Assert-E2ECdcWorkspaceAvailable, Invoke-E2ECdcSetup
