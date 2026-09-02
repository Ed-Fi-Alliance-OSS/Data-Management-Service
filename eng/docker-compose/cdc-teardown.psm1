# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Destructive CDC teardown for the local stack: retires every binding the durable state store
    holds before the compose project's volumes are removed.
.DESCRIPTION
    A normal stop retains the binding record, the connector, its committed offsets, the governed
    topics and ACLs, and the provider capture artifacts. Destructive volume removal (`-d -v`) is the
    only local workflow permitted to remove a binding record, and only in the same pass that removes
    every artifact the record governs - so this module runs while the connector, broker, and instance
    database are still up, before `docker compose down -v`.

    The ordered sequence itself belongs to the control plane, not to this module: `cdc retire` stops
    the connector, deletes its committed offsets while it is stopped and still exists, deletes the
    connector, then the binding's public, progress, and SQL Server schema-history topics and ACLs,
    then the provider capture artifacts, and only then the terminal incident state and the binding
    record - which it deletes last and only against a validated cleanup proof. Reimplementing any of
    that here would put a second, unverified ordering next to the verified one.

    Bindings are discovered from the state store rather than from a switch: teardown must remove what
    an earlier run actually bound, which the run being torn down cannot be asked about. A retirement
    that cannot run or that fails leaves the record in place and reports it; the compose down still
    proceeds, because a surviving record with no surviving artifact is the recoverable state the
    design already allows for a crash, while a blocked teardown leaves the operator with neither.
#>

Set-StrictMode -Version Latest

# The local deployment policy the bootstrap CDC phase enables under. Retirement recovers the
# governed artifact names from the binding record, but the control plane still resolves its
# endpoints and record-size policy from options, so the two invocations name them identically.
$script:LocalKafkaBootstrapServers = 'dms-kafka1:9092'
$script:LocalConnectBaseUrl = 'http://kafka-postgresql-source:8083'
$script:LocalMaxRecordBytes = '1048576'
$script:LocalDurabilityProfile = 'local'
$script:ContainerBindingStatePath = '/state'

# The exact token `cdc retire` requires; a retirement is never inferred from the absence of one.
$script:BindingRetirementConfirmation = 'cdcBindingRetirement'

function Get-CdcRetirableBinding {
    <#
    .SYNOPSIS
        The binding records the durable state store holds, newest generation first.

    .DESCRIPTION
        The store lays a record out at <root>/bindings/<deploymentKey>/<instanceKey>/<generation>.json.
        The target is read from the record's own fields rather than parsed out of the path, because
        the record is the authority for what was bound and the path segments are only its index.

        A file that is not a readable binding record is reported and skipped rather than guessed at:
        an unreadable record is exactly the case where inferring a target could retire artifacts that
        belong to a different binding. Within one instance key the newest generation is retired first,
        so a generation is never removed before the one that superseded it.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns the set of retirable bindings; the singular noun names the item, matching the sibling Get- helpers.')]
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)]
        [string]
        $BindingStateRoot
    )

    $bindingsRoot = Join-Path $BindingStateRoot "bindings"
    if (-not (Test-Path -LiteralPath $bindingsRoot -PathType Container)) {
        return @()
    }

    $records = @()
    foreach ($file in (Get-ChildItem -LiteralPath $bindingsRoot -Recurse -File -Filter "*.json" | Sort-Object -Property FullName)) {
        $record = $null
        try {
            $record = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        }
        catch {
            Write-Warning "CDC teardown: '$($file.FullName)' is not a readable binding record and was left in place."
            continue
        }

        $identityFields = @('deploymentKey', 'instanceKey', 'dataStoreId', 'generation')
        $missingField = $identityFields | Where-Object {
            $null -eq $record.PSObject.Properties[$_] -or
            [string]::IsNullOrWhiteSpace([string]$record.$_)
        }
        if (@($missingField).Count -gt 0) {
            Write-Warning "CDC teardown: '$($file.FullName)' does not name a complete binding target ($(@($missingField) -join ', ')) and was left in place."
            continue
        }

        $records += [pscustomobject]@{
            DeploymentKey = [string]$record.deploymentKey
            # A blank tenant key is the default tenant, which the record carries as an empty string.
            TenantKey     = if ($null -eq $record.PSObject.Properties['tenantKey']) { "" } else { [string]$record.tenantKey }
            DataStoreId   = [string]$record.dataStoreId
            InstanceKey   = [string]$record.instanceKey
            Generation    = [long]$record.generation
            RecordPath    = $file.FullName
        }
    }

    return @(
        $records | Sort-Object -Property DeploymentKey, InstanceKey, @{ Expression = 'Generation'; Descending = $true }
    )
}

function Get-CdcRetireArgument {
    <#
    .SYNOPSIS
        The docker compose argument list that runs `dms-document-cache cdc retire` for one binding.

    .DESCRIPTION
        Runs as the same one-shot container on the dms network the enable phase uses: the instance
        database is registered in CMS under its container alias and the broker advertises
        PLAINTEXT://dms-kafka1:9092, so a host-side process is redirected to names it cannot resolve.

        Carries no provisioning evidence. The evidence flags attest that a database was created for
        an initial CDC provisioning and has admitted no write, which is a claim about enablement;
        retirement neither needs nor may assert it.

        It does carry the connector principal, which every cdc verb requires: the provider-setup
        input factory refuses without it, because the validate-only pass retirement runs reports the
        grants that principal holds. It carries no connector source-connection properties - a
        retirement registers no connector and reads none, and the connector's database name is a
        per-run value this module has no authority over, so supplying a guess would put a wrong
        value where nothing reads a right one.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns the argument list for one invocation; the plural noun reflects the return shape.')]
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)]
        [string]
        $ComposeProjectName,

        [Parameter(Mandatory)]
        [string]
        $EnvironmentFile,

        [Parameter(Mandatory)]
        $BindingRecord,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]
        $DatabaseEngine,

        [Parameter(Mandatory)]
        [string]
        $DmsBearerToken,

        [hashtable]
        $ConnectorPrincipal
    )

    Import-Module (Join-Path $PSScriptRoot "env-utility.psm1") -Force
    Import-Module (Join-Path $PSScriptRoot "bootstrap-wrapper.psm1") -Force

    # The database principal the provider teardown runs as - the server's own administrative login,
    # which is the account the compose file creates, matching the enable phase's setup principal.
    $setupPrincipal = if ($DatabaseEngine -eq "mssql") { "sa" } else { "postgres" }

    if ($null -eq $ConnectorPrincipal) {
        $ConnectorPrincipal = Get-CdcConnectorPrincipalConfiguration -EnvValues @{}
    }

    $composeArguments = @(
        "compose",
        "-f", "cdc-setup.yml",
        "--env-file", $EnvironmentFile,
        "-p", $ComposeProjectName,
        "run", "--rm", "--build",
        "-e", "DataManagement__DocumentCache__Cdc__SetupPrincipal=$setupPrincipal",
        # Retirement reads no projection status, but the control plane's options are validated as a
        # whole, so the operator credential travels with it - by environment, never on the command
        # line - exactly as it does for the enable invocation.
        "-e", "DataManagement__DocumentCache__Cdc__DmsBearerToken=$DmsBearerToken"
    )
    $composeArguments += Get-WrapperCdcConnectorPrincipalEnvArgument -ConnectorPrincipal $ConnectorPrincipal
    $composeArguments += @(
        "cdc-setup",
        "cdc", "retire",
        "--confirm", $script:BindingRetirementConfirmation,
        "--data-store-id", "$($BindingRecord.DataStoreId)"
    )

    if (-not [string]::IsNullOrWhiteSpace($BindingRecord.TenantKey)) {
        $composeArguments += @("--tenant-key", [string]$BindingRecord.TenantKey)
    }

    # The generation and the artifact-name keys are the record's own. A retirement that guessed any
    # of them would be naming a binding other than the one it read.
    $composeArguments += @(
        "--deployment-key", [string]$BindingRecord.DeploymentKey,
        "--instance-key", [string]$BindingRecord.InstanceKey,
        "--generation", "$($BindingRecord.Generation)",
        "--kafka-bootstrap-servers", $script:LocalKafkaBootstrapServers,
        "--connect-base-url", $script:LocalConnectBaseUrl,
        "--max-record-bytes", $script:LocalMaxRecordBytes,
        "--durability-profile", $script:LocalDurabilityProfile,
        "--cdc-binding-state-path", $script:ContainerBindingStatePath,
        "--json"
    )

    return $composeArguments
}

function Invoke-CdcDestructiveTeardown {
    <#
    .SYNOPSIS
        Retires every binding in the state store, before the caller removes the compose volumes.

    .DESCRIPTION
        Returns one result object per binding it attempted, so the caller and the tests can see what
        was retired and what was left. Reports and continues on a failure rather than throwing: the
        destructive down the caller is about to run is the operator's explicit request, and the
        control plane guarantees that an incomplete teardown keeps its binding record.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Teardown phase helper, consistent with the sibling teardown invocations; no -WhatIf surface.')]
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory)]
        [string]
        $BindingStateRoot,

        [Parameter(Mandatory)]
        [string]
        $ComposeProjectName,

        [Parameter(Mandatory)]
        [string]
        $EnvironmentFile,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]
        $DatabaseEngine
    )

    $bindings = @(Get-CdcRetirableBinding -BindingStateRoot $BindingStateRoot)
    if ($bindings.Count -eq 0) {
        Write-Information "CDC teardown: the binding state store at '$BindingStateRoot' holds no binding record; nothing to retire." -InformationAction Continue
        return @()
    }

    # Imported here rather than at the top of the function: a teardown with nothing to retire - which
    # is every teardown of a stack that never enabled CDC - depends on neither module.
    Import-Module (Join-Path $PSScriptRoot "env-utility.psm1") -Force
    Import-Module (Join-Path $PSScriptRoot "../Dms-Management.psm1") -Force

    $envValues = ReadValuesFromEnvFile $EnvironmentFile
    $dmsUrl = (Resolve-DockerLocalDmsBaseUrl -EnvValues $envValues).TrimEnd('/')
    $identityClientSecrets = Resolve-IdentityClientSecretConfiguration -EnvValues $envValues
    $connectorPrincipal = Get-CdcConnectorPrincipalConfiguration -EnvValues $envValues

    # Minted through the DMS token proxy, as the enable phase does, so the issuer matches the
    # authority DMS validates against. An unreachable DMS is not a reason to skip the retirement's
    # ordering, but it is a reason not to attempt it at all: the control plane validates its options
    # before running any step, so a run without this credential would refuse the whole retirement.
    $operatorToken = ""
    try {
        $operatorToken = Get-DmsToken `
            -DmsUrl $dmsUrl `
            -Key $identityClientSecrets.DocumentCacheOperatorClientId `
            -Secret $identityClientSecrets.DocumentCacheOperatorClientSecret
    }
    catch {
        $operatorToken = ""
    }

    if ([string]::IsNullOrWhiteSpace($operatorToken)) {
        Write-Warning "CDC teardown: no DocumentCache operator token could be obtained from $dmsUrl, so $($bindings.Count) binding record(s) under '$BindingStateRoot' were left in place. Retire them against a running stack (dms-document-cache cdc retire) before reusing the state store."
        return @()
    }

    # The setup container mounts the state store from DMS_CDC_BINDING_STATE_PATH, and Compose gives an
    # ambient value precedence over the env file - so the resolved root this teardown was given is the
    # one that is mounted, whichever env file the caller passed.
    $previousStatePath = [System.Environment]::GetEnvironmentVariable('DMS_CDC_BINDING_STATE_PATH')
    $results = @()
    try {
        $env:DMS_CDC_BINDING_STATE_PATH = $BindingStateRoot

        foreach ($binding in $bindings) {
            Write-Information "CDC teardown: retiring generation $($binding.Generation) of data store $($binding.DataStoreId) and its governed artifacts." -InformationAction Continue

            $composeArguments = Get-CdcRetireArgument `
                -ComposeProjectName $ComposeProjectName `
                -EnvironmentFile $EnvironmentFile `
                -BindingRecord $binding `
                -DatabaseEngine $DatabaseEngine `
                -DmsBearerToken $operatorToken `
                -ConnectorPrincipal $connectorPrincipal

            $global:LASTEXITCODE = 0
            & docker @composeArguments
            $retired = -not ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0)

            if (-not $retired) {
                Write-Warning "CDC teardown: dms-document-cache cdc retire failed for generation $($binding.Generation) of data store $($binding.DataStoreId) (exit code $LASTEXITCODE). Its binding record at '$($binding.RecordPath)' was retained."
            }

            $results += [pscustomobject]@{
                DataStoreId = $binding.DataStoreId
                Generation  = $binding.Generation
                RecordPath  = $binding.RecordPath
                Retired     = $retired
            }
        }
    }
    finally {
        if ($null -eq $previousStatePath) {
            Remove-Item -LiteralPath "Env:DMS_CDC_BINDING_STATE_PATH" -ErrorAction SilentlyContinue
        }
        else {
            $env:DMS_CDC_BINDING_STATE_PATH = $previousStatePath
        }
    }

    return @($results)
}

Export-ModuleMember -Function `
    Get-CdcRetirableBinding, `
    Get-CdcRetireArgument, `
    Invoke-CdcDestructiveTeardown
