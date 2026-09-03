# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    The deployment-owned CDC enablement phase: everything the phase decides, and nothing about
    when it runs.

.DESCRIPTION
    This module owns the CDC phase's own behavior - resolving the source database and the
    credentials the invocation needs, gating on DMS health and on the DocumentCache status
    endpoint's authorization, provisioning the connector's database principal, and composing and
    running the `dms-document-cache cdc enable` invocation.

    It lives here rather than in bootstrap-wrapper.psm1 because command-boundaries.md gives the
    wrapper orchestration only: the wrapper sequences phase commands, forwards developer-facing
    parameters, and prints next-step guidance, and must not implement phase-specific behavior,
    synthesize credentials, or absorb a concern a phase owns. Every other phase is a standalone
    script the wrapper invokes and whose structured result it reads; enable-kafka-cdc.ps1 is this
    phase's, and it is the entry point callers use. The E2E harness calls that script too, rather
    than importing an orchestration module to reach phase logic.

    The functions are exported because the phase script and the destructive teardown both compose
    invocations of the same one-shot container, and a second copy of the argument shape is how the
    enable and retire paths come to disagree about the principal, the state root, or the user the
    container runs as.
#>

function Get-CdcRuntimeEnvOverride {
    <#
    .SYNOPSIS
    The DMS runtime settings the CDC phase depends on, as env-file key overrides.

    .DESCRIPTION
    Both settings are read at DMS startup, so they must be written into the effective env file
    BEFORE the DMS start and therefore before the CDC phase: the projection target because the
    enable workflow's first proof is that the target is configured, and the status role because
    /health/document-cache is not even mapped without it, which the caught-up step would see as a
    404. The binding state root travels with them so the CDC setup container mounts the same store
    the start script created.
    #>
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $TenantKey,

        [Parameter(Mandatory)]
        [long]
        $DataStoreId,

        [Parameter(Mandatory)]
        [string]
        $BindingStateRootPath
    )

    # The role token is owned by env-utility so the identity setup that registers the operator
    # client and this write cannot name two different roles.
    Import-Module (Join-Path $PSScriptRoot "env-utility.psm1") -Force

    return @{
        DMS_CDC_TARGET_TENANT_KEY = $TenantKey
        DMS_CDC_TARGET_DATA_STORE_ID = [string]$DataStoreId
        DMS_DOCUMENTCACHE_STATUS_REQUIRED_ROLE = (Get-DocumentCacheStatusOperatorRole)
        DMS_CDC_BINDING_STATE_PATH = $BindingStateRootPath
    }
}

function Resolve-CdcSourceDatabaseName {
    <#
    .SYNOPSIS
    The instance database the binding captures from.

    .DESCRIPTION
    An explicit -SourceDatabaseName wins: a caller that provisioned its own database - the DMS E2E
    setup wrapper does - knows the name and must not have it re-derived. Otherwise it resolves the
    same way configure-local-data-store.ps1 resolves the datastore database name for the engine,
    because that is the database a plain bootstrap run registered in CMS. Deriving it any other way
    would point the connector at a database the run never configured.
    #>
    param(
        [hashtable]
        $EnvValues,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]
        $DatabaseEngine,

        [AllowEmptyString()]
        [string]
        $SourceDatabaseName = ""
    )

    if (-not [string]::IsNullOrWhiteSpace($SourceDatabaseName)) {
        return $SourceDatabaseName
    }

    $settingName = if ($DatabaseEngine -eq "mssql") { "MSSQL_DB_NAME" } else { "POSTGRES_DB_NAME" }

    return Get-EnvValue -EnvValues $EnvValues -Name $settingName -DefaultValue "edfi_datamanagementservice"
}

function Get-CdcContainerUserArgument {
    <#
    .SYNOPSIS
    The `docker compose run` fragment that makes the one-shot container write the binding state store
    as the host user, on the hosts where that matters.

    .DESCRIPTION
    The store is a bind mount, and the store itself creates its directories 0700 and its files 0600 -
    owner-only, deliberately. The image declares no non-root USER, so without this the container
    writes those as root.

    On Docker Desktop that is invisible: the file-sharing layer synthesizes ownership, and the host
    user reads the tree whatever uid wrote it. On a native Linux host the bind mount preserves real
    ownership, so a root-owned 0700 `bindings/` directory is one the invoking user cannot descend
    into - and the host-side retirement that has to enumerate those records before a destructive
    teardown is exactly the reader that then cannot see them.

    Running the container as the invoking user's uid/gid makes the records it writes readable by the
    account that has to read them back. It is applied only on Linux, because that is the only host
    where the ownership is real and where `id` reports a uid the daemon shares.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns an argument list fragment; the plural noun reflects the return shape.')]
    param()

    if (-not $IsLinux) {
        return @()
    }

    try {
        $userId = (& id -u 2>$null | Select-Object -First 1)
        $groupId = (& id -g 2>$null | Select-Object -First 1)
    }
    catch {
        return @()
    }

    if ($userId -notmatch '^\d+$' -or $groupId -notmatch '^\d+$') {
        # Without a real uid the safe move is to leave the container as it is: the store stays
        # root-owned, and the teardown's enumeration now refuses loudly rather than reading an
        # unreadable tree as an empty one.
        return @()
    }

    return @("--user", "${userId}:${groupId}")
}

function Get-CdcConnectorPrincipalEnvArgument {
    <#
    .SYNOPSIS
    The `-e` pair naming the database principal the Debezium connector authenticates as.

    .DESCRIPTION
    Every cdc verb needs it, not only the ones that register a connector: the provider-setup input
    factory refuses any verb without it, because both the create pass and the validate-only pass
    report the grants this principal holds. Emitted from one place so the enable phase and the
    destructive teardown cannot name two different principals.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns an argument list fragment; the plural noun reflects the return shape.')]
    param(
        [Parameter(Mandatory)]
        [hashtable]
        $ConnectorPrincipal
    )

    return @(
        "-e",
        "DataManagement__DocumentCache__Cdc__ConnectorPrincipal=$($ConnectorPrincipal.PrincipalName)"
    )
}

function Get-CdcConnectorEnvArgument {
    <#
    .SYNOPSIS
    The `-e` pairs carrying the connector principal and the connector's own source-connection
    properties.

    .DESCRIPTION
    The connector connects to the instance database itself rather than through the DMS connection
    string the tool resolves from CMS, so its host and port are the container-internal names Kafka
    Connect resolves - the connector runs inside the dms network, where PostgreSQL answers on 5432
    and SQL Server on 1433 regardless of the host ports the compose files publish.

    The password is emitted as the worker's config-provider reference, never as the secret: the
    registered connector configuration is read back and compared during validation, and a rendered
    password would then be a secret sitting in Kafka Connect's own config topic.

    SQL Server names the captured catalog `database.names` and PostgreSQL names it
    `database.dbname`; the control plane's template requires whichever belongs to the provider.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns an argument list fragment; the plural noun reflects the return shape.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'No password is emitted: only the ${env:...} reference the Kafka Connect worker resolves.')]
    param(
        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]
        $DatabaseEngine,

        [Parameter(Mandatory)]
        [string]
        $SourceDatabaseName,

        [Parameter(Mandatory)]
        [hashtable]
        $ConnectorPrincipal
    )

    $sourceHost = if ($DatabaseEngine -eq "mssql") { "dms-mssql" } else { "dms-postgresql" }
    $sourcePort = if ($DatabaseEngine -eq "mssql") { "1433" } else { "5432" }
    $catalogPropertyName = if ($DatabaseEngine -eq "mssql") { "database.names" } else { "database.dbname" }
    $propertyPrefix = "DataManagement__DocumentCache__Cdc__ProviderConnectionProperties__"

    $arguments = @(Get-CdcConnectorPrincipalEnvArgument -ConnectorPrincipal $ConnectorPrincipal)
    $arguments += @(
        "-e", "$($propertyPrefix)database.hostname=$sourceHost",
        "-e", "$($propertyPrefix)database.port=$sourcePort",
        "-e", "$($propertyPrefix)database.user=$($ConnectorPrincipal.PrincipalName)",
        "-e", "$($propertyPrefix)database.password=$($ConnectorPrincipal.PasswordReference)",
        "-e", "$propertyPrefix$catalogPropertyName=$SourceDatabaseName"
    )

    return $arguments
}

function Resolve-CdcHostBindingStateRoot {
    <#
    .SYNOPSIS
        The host path of the durable binding state store the setup container will be given.

    .DESCRIPTION
        Read the way Compose reads it, because Compose is what turns it into the /state bind mount:
        an ambient DMS_CDC_BINDING_STATE_PATH wins over the env file's own text, and an absent or
        blank value falls back to the same ./.cdc-state default cdc-setup.yml declares. A relative
        value resolves against this directory, which is the compose project directory the mount
        source is relative to.

        Resolved here rather than taken as a parameter from the phase's callers: the generation this
        phase allocates has to be read from the very store the container will write to, and the env
        file plus the ambient environment are the only authorities on which one that is.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [hashtable]
        $EnvValues
    )

    Import-Module (Join-Path $PSScriptRoot "env-utility.psm1") -Force

    $configured = Get-ComposeResolvedEnvValue `
        -EnvironmentValues $EnvValues `
        -Name "DMS_CDC_BINDING_STATE_PATH" `
        -DefaultValue "./.cdc-state"

    if ([System.IO.Path]::IsPathRooted($configured)) {
        return [System.IO.Path]::GetFullPath($configured)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $configured))
}

function Get-CdcNextGeneration {
    <#
    .SYNOPSIS
        The generation a new binding for this instance key may be created under.

    .DESCRIPTION
        One past the highest generation the store has ever held for the instance key, counting the
        retirement records as well as the live bindings - so the first binding of a target is 1, and
        a target whose only generation was retired gets 2.

        Both trees are counted because retirement removes the binding record it retires and leaves
        the retirement record as the only trace. Reading the bindings alone makes a retired
        generation look unallocated, and this store's root is a deployment path rather than a
        container volume: it survives the destructive volume removal that destroys the database the
        generation was bound to, so the next stack would ask for that same generation against a new
        physical source. That reassigns an existing connector name, topic namespace, and consumer
        state to a different database, which v1 never does - the control plane refuses it, and
        without this the refusal would land on every enable after the first teardown.

        A store that cannot be enumerated is fatal rather than empty, for the same reason it is in
        the teardown module: an unreadable tree read as "no generations" would allocate 1 over
        records it could not see.
    #>
    [CmdletBinding()]
    [OutputType([long])]
    param(
        [Parameter(Mandatory)]
        [string]
        $BindingStateRoot,

        [Parameter(Mandatory)]
        [string]
        $DeploymentKey,

        [Parameter(Mandatory)]
        [string]
        $InstanceKey
    )

    $highest = 0L
    foreach ($stateKind in @("bindings", "retirements")) {
        $instanceDirectory = Join-Path (Join-Path (Join-Path $BindingStateRoot $stateKind) $DeploymentKey) $InstanceKey
        if (-not (Test-Path -LiteralPath $instanceDirectory -PathType Container)) {
            continue
        }

        $stateFiles = @()
        try {
            $stateFiles = @(Get-ChildItem -LiteralPath $instanceDirectory -File -Filter "*.json" -ErrorAction Stop)
        }
        catch {
            throw "CDC phase: the binding state store at '$instanceDirectory' could not be enumerated ($($_.Exception.Message)). It may hold the generations this target has already published, so the phase stops rather than allocating one over them. Run the enable from a host account that can read the store."
        }

        foreach ($stateFile in $stateFiles) {
            $generationName = [System.IO.Path]::GetFileNameWithoutExtension($stateFile.Name)
            $generation = 0L
            if (-not [long]::TryParse($generationName, [ref]$generation) -or $generation -lt 1) {
                throw "CDC phase: '$($stateFile.FullName)' is not named for a binding generation. The store's own layout names each record for the generation it holds, so a generation cannot be allocated without reading it. Repair or remove that file before enabling CDC."
            }

            if ($generation -gt $highest) {
                $highest = $generation
            }
        }
    }

    return $highest + 1
}

function Get-CdcSetupComposeArgument {
    <#
    .SYNOPSIS
    The docker compose argument list that runs one `dms-document-cache cdc` verb in the setup
    container.

    .DESCRIPTION
    Every cdc verb a deployment runs reaches the control plane the same way: as a one-shot container
    on the dms network, from the same compose service, under the same host-user override, as the same
    database setup principal, and against the same local deployment policy. Only the environment a
    verb needs and the verb's own options differ, so those are what the caller supplies and
    everything else is built here - the enable phase and the destructive teardown cannot drift on the
    connector base URL, the record-size budget, the binding-state path, the durability profile, the
    user override, the setup principal, or the build flags, and a retirement that named a Connect port
    the enable phase had moved would reach nothing.

    The setup principal is the database server's own administrative login, which is the account the
    compose file creates for each engine.

    .PARAMETER EnvironmentArgument
    Additional `-e` pairs the verb needs, emitted after the setup principal. Deployment facts the
    control plane requires but has no command-line surface for, including its secrets.

    .PARAMETER VerbArgument
    The verb's own options, emitted after `cdc <VerbName>` and before the deployment-policy flags.
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
        [ValidateSet("postgresql", "mssql")]
        [string]
        $DatabaseEngine,

        [Parameter(Mandatory)]
        [string]
        $VerbName,

        [object[]]
        $EnvironmentArgument = @(),

        [object[]]
        $VerbArgument = @()
    )

    Import-Module (Join-Path $PSScriptRoot "env-utility.psm1") -Force

    $setupPrincipal = if ($DatabaseEngine -eq "mssql") { "sa" } else { "postgres" }
    $localPolicy = Get-LocalCdcDeploymentPolicy

    $composeArguments = @(
        "compose",
        "-f", "cdc-setup.yml",
        "--env-file", $EnvironmentFile,
        "-p", $ComposeProjectName,
        "run", "--rm", "--build"
    )
    $composeArguments += Get-CdcContainerUserArgument
    $composeArguments += @("-e", "DataManagement__DocumentCache__Cdc__SetupPrincipal=$setupPrincipal")
    $composeArguments += $EnvironmentArgument
    $composeArguments += @("cdc-setup", "cdc", $VerbName)
    $composeArguments += $VerbArgument
    $composeArguments += @(
        "--kafka-bootstrap-servers", $localPolicy.KafkaBootstrapServers,
        "--connect-base-url", $localPolicy.ConnectBaseUrl,
        "--max-record-bytes", $localPolicy.MaxRecordBytes,
        "--durability-profile", $localPolicy.DurabilityProfile,
        "--cdc-binding-state-path", $localPolicy.BindingStatePath,
        "--json"
    )

    return $composeArguments
}

function Get-CdcEnableArgument {
    <#
    .SYNOPSIS
    The docker compose argument list that runs `dms-document-cache cdc enable` for one target.

    .DESCRIPTION
    The tool runs as a one-shot container on the dms network rather than on the host: the instance
    database is registered in CMS under its container alias and the broker advertises
    PLAINTEXT://dms-kafka1:9092, so a host-side process is redirected to names it cannot resolve.

    The two exact-token evidence flags are emitted ONLY when this run created the physical
    database. Bootstrap has no standing to assert either fact about a data store it merely found,
    and the command surface refuses an enable that omits them - which is the correct outcome,
    reached here without an assertion this caller cannot support. That refusal is a command-line
    parse failure, not a control-plane result: the executor is never entered, so the exit code is
    the parser's and no admission contract reaches stdout.

    The connector principal and the connector's own database connection properties travel by
    environment rather than on the command line, alongside the setup principal: they are deployment
    facts the control plane requires but has no command-line surface for, and the password among
    them is a secret. The connector reaches the source DIRECTLY - not through the DMS connection
    string the tool resolves from CMS - so its host, port and database are named here in the
    container-internal terms Kafka Connect resolves them in.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseSingularNouns', '', Justification = 'Returns the argument list for one invocation; the plural noun reflects the return shape.')]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingPlainTextForPassword', '', Justification = 'No password is passed: the connector password reaches the worker only as the ${env:...} reference this function emits.')]
    param(
        [Parameter(Mandatory)]
        [string]
        $ComposeProjectName,

        [Parameter(Mandatory)]
        [string]
        $EnvironmentFile,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $TenantKey,

        [Parameter(Mandatory)]
        [long]
        $DataStoreId,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]
        $DatabaseEngine,

        [Parameter(Mandatory)]
        [bool]
        $DatabaseCreatedByThisRun,

        [Parameter(Mandatory)]
        [string]
        $DmsBearerToken,

        # The instance database the binding captures from, as Kafka Connect must name it. Required
        # because it is a per-run value: the connector's own database property cannot be a compose
        # default without silently capturing the wrong database on a run that named another.
        [Parameter(Mandatory)]
        [string]
        $SourceDatabaseName,

        # Host path of the durable binding state store this run will mount. Required because the
        # generation is allocated from it: a guess would either collide with a live binding or
        # reassign one this deployment already retired.
        [Parameter(Mandatory)]
        [string]
        $BindingStateRoot,

        [hashtable]
        $ConnectorPrincipal
    )

    Import-Module (Join-Path $PSScriptRoot "env-utility.psm1") -Force

    if ($null -eq $ConnectorPrincipal) {
        $ConnectorPrincipal = Get-CdcConnectorPrincipalConfiguration -EnvValues @{}
    }

    # The operator token and the connector's own source-connection properties. Both are deployment
    # facts with no command-line surface, and the password among them is a secret.
    $environmentArguments = @(
        "-e", "DataManagement__DocumentCache__Cdc__DmsBearerToken=$DmsBearerToken"
    )
    $environmentArguments += Get-CdcConnectorEnvArgument `
        -DatabaseEngine $DatabaseEngine `
        -SourceDatabaseName $SourceDatabaseName `
        -ConnectorPrincipal $ConnectorPrincipal

    $verbArguments = @("--data-store-id", "$DataStoreId")

    if (-not [string]::IsNullOrWhiteSpace($TenantKey)) {
        $verbArguments += @("--tenant-key", $TenantKey)
    }

    if ($DatabaseCreatedByThisRun) {
        $verbArguments += @(
            "--database-creation-mode", "created-for-initial-cdc-provisioning",
            "--write-admission", "closed-never-opened"
        )
    }

    # The keys are opaque and only have to be stable for the binding they name. The generation is
    # allocated from the store rather than fixed at 1: a target's first binding gets 1, and one whose
    # earlier generation was retired gets the next, because the retirement record outlives the
    # binding it removed and the control plane never rebinds a retired generation.
    $deploymentKey = "local"
    $instanceKey = "ds$DataStoreId"
    $generation = Get-CdcNextGeneration `
        -BindingStateRoot $BindingStateRoot `
        -DeploymentKey $deploymentKey `
        -InstanceKey $instanceKey

    $verbArguments += @(
        "--deployment-key", $deploymentKey,
        "--instance-key", $instanceKey,
        "--generation", "$generation"
    )

    return Get-CdcSetupComposeArgument `
        -ComposeProjectName $ComposeProjectName `
        -EnvironmentFile $EnvironmentFile `
        -DatabaseEngine $DatabaseEngine `
        -VerbName "enable" `
        -EnvironmentArgument $environmentArguments `
        -VerbArgument $verbArguments
}

function Invoke-CdcEnablePhase {
    <#
    .SYNOPSIS
    Runs the CDC enable workflow against the DMS this wrapper just started.

    .DESCRIPTION
    Ordered deliberately: DMS health, then the operator token, then a status-endpoint preflight,
    then the enable itself. The preflight is what separates a configuration mistake from a CDC
    failure - a 404 means the status role never reached the container and the endpoint was never
    mapped, and a 403 means the token does not carry the role - so it is answered here, in the
    phase that owns those settings, rather than surfacing from inside the enable workflow.

    Write admission is still closed when this runs: no seed or API write has been issued yet. DMS
    being up is not write admission, and DMS never enables tracking itself - this external
    administrative command does.

    The connector's database principal is created immediately before the enable. Provider setup
    grants that principal its capture access but never creates it - the SQL Server pass refuses
    outright when it is absent - so its creation is a deployment step, and it belongs to this phase
    because this is the phase that knows which instance database the binding will capture.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Bootstrap phase helper, consistent with the other phase invocations; no -WhatIf surface.')]
    param(
        [Parameter(Mandatory)]
        [string]
        $ComposeProjectName,

        [Parameter(Mandatory)]
        [string]
        $EnvironmentFile,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]
        $TenantKey,

        [Parameter(Mandatory)]
        [long]
        $DataStoreId,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]
        $DatabaseEngine,

        [Parameter(Mandatory)]
        [bool]
        $DatabaseCreatedByThisRun,

        # The instance database the binding captures from. A caller that provisioned its own
        # database names it; an omitted value resolves the same way the configure phase resolves
        # the datastore database name, which is what a plain bootstrap run registered.
        [string]
        $SourceDatabaseName = "",

        [int]
        $HealthTimeoutSeconds = 300
    )

    Import-Module (Join-Path $PSScriptRoot "env-utility.psm1") -Force
    Import-Module (Join-Path $PSScriptRoot "../Dms-Management.psm1") -Force

    $envValues = ReadValuesFromEnvFile $EnvironmentFile
    $dmsUrl = (Resolve-DockerLocalDmsBaseUrl -EnvValues $envValues).TrimEnd('/')
    $identityClientSecrets = Resolve-IdentityClientSecretConfiguration -EnvValues $envValues
    $connectorPrincipal = Get-CdcConnectorPrincipalConfiguration -EnvValues $envValues
    $resolvedSourceDatabaseName = Resolve-CdcSourceDatabaseName `
        -EnvValues $envValues `
        -DatabaseEngine $DatabaseEngine `
        -SourceDatabaseName $SourceDatabaseName

    Write-Information "CDC phase: waiting for DMS at $dmsUrl to become healthy." -InformationAction Continue
    Wait-CdcHttpEndpoint -Url "$dmsUrl/health" -Name "DMS" -TimeoutSeconds $HealthTimeoutSeconds

    # The token is minted through the DMS token proxy rather than straight from the identity
    # provider: the proxy calls it from inside the Docker network, so the issuer matches the
    # authority DMS validates against. A host-side call to the same provider would not.
    $operatorToken = Get-DmsToken `
        -DmsUrl $dmsUrl `
        -Key $identityClientSecrets.DocumentCacheOperatorClientId `
        -Secret $identityClientSecrets.DocumentCacheOperatorClientSecret

    if ([string]::IsNullOrWhiteSpace($operatorToken)) {
        throw "CDC phase: the DocumentCache operator client did not return an access token. The client is registered by the self-contained identity setup during the infrastructure phase."
    }

    Assert-CdcDocumentCacheStatusEndpoint `
        -DmsBaseUrl $dmsUrl `
        -AccessToken $operatorToken `
        -TimeoutSeconds $HealthTimeoutSeconds

    # Before the enable, and after the database exists: provider setup grants this principal its
    # capture access and refuses when it is missing.
    & "$PSScriptRoot/provision-cdc-principal.ps1" `
        -EnvironmentFile $EnvironmentFile `
        -DatabaseName $resolvedSourceDatabaseName `
        -DatabaseEngine $DatabaseEngine

    $composeArguments = Get-CdcEnableArgument `
        -ComposeProjectName $ComposeProjectName `
        -EnvironmentFile $EnvironmentFile `
        -TenantKey $TenantKey `
        -DataStoreId $DataStoreId `
        -DatabaseEngine $DatabaseEngine `
        -DatabaseCreatedByThisRun $DatabaseCreatedByThisRun `
        -DmsBearerToken $operatorToken `
        -SourceDatabaseName $resolvedSourceDatabaseName `
        -BindingStateRoot (Resolve-CdcHostBindingStateRoot -EnvValues $envValues) `
        -ConnectorPrincipal $connectorPrincipal

    Write-Information "CDC phase: enabling CDC for data store $DataStoreId." -InformationAction Continue
    $global:LASTEXITCODE = 0
    & docker @composeArguments
    if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
        throw "dms-document-cache cdc enable failed with exit code $LASTEXITCODE."
    }

    # The phase result, in the shape command-boundaries.md requires of a phase command: a
    # JSON-compatible object the caller reads structurally rather than by parsing prose. It names
    # what this run bound and the source it bound it to, which is what a later phase - or an
    # operator reading the transcript - needs to identify the binding.
    return [pscustomobject]@{
        TenantKey                = $TenantKey
        DataStoreId              = $DataStoreId
        DatabaseEngine           = $DatabaseEngine
        SourceDatabaseName       = $resolvedSourceDatabaseName
        DatabaseCreatedByThisRun = $DatabaseCreatedByThisRun
        Status                   = "Enabled"
    }
}

function Wait-CdcHttpEndpoint {
    <#
    .SYNOPSIS
    Polls an endpoint until it answers HTTP 200 or the timeout elapses.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $Url,

        [Parameter(Mandatory)]
        [string]
        $Name,

        [int]
        $TimeoutSeconds = 300
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -Method Get -TimeoutSec 10 -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200) {
                return
            }
        }
        catch {
            # Connection refused while the container is still starting. Recorded rather than
            # rethrown: the deadline below is the verdict, and the first transient failure is not.
            Write-Debug "Waiting for $Name at $Url : $($_.Exception.Message)"
        }

        Start-Sleep -Seconds 2
    }

    throw "$Name did not become healthy at $Url within $TimeoutSeconds seconds."
}

function Assert-CdcDocumentCacheStatusEndpoint {
    <#
    .SYNOPSIS
    Proves the DocumentCache status endpoint answers 200 for the operator credential before any
    CDC work begins.

    .DESCRIPTION
    Both failure shapes are configuration faults with distinct causes, and both would otherwise
    reach the operator as an opaque CDC failure much later in the enable workflow: a 404 means
    DataManagement:DocumentCache:Status:RequiredRole never reached the container, so the route was
    never mapped, and a 403 means the token carries no matching role claim.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $DmsBaseUrl,

        [Parameter(Mandatory)]
        [string]
        $AccessToken,

        [int]
        $TimeoutSeconds = 300
    )

    $statusUrl = "$($DmsBaseUrl.TrimEnd('/'))/health/document-cache"
    $headers = @{ Authorization = "Bearer $AccessToken" }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastStatusCode = 0

    while ((Get-Date) -lt $deadline) {
        # -SkipHttpErrorCheck suppresses an error STATUS, not a transport failure: a refused
        # connection or a reset socket still throws. The DMS answered /health and minted a token a
        # moment ago, so this is the narrow window where it restarts or drops a connection while its
        # authority metadata warms - the same window the 401/5xx retry below exists for, and it is
        # retried the same way rather than ending the phase with a raw PowerShell error.
        $response = $null
        try {
            $response = Invoke-WebRequest -Uri $statusUrl -Method Get -Headers $headers -TimeoutSec 30 -SkipHttpErrorCheck
        }
        catch {
            Write-Debug "Waiting for the DocumentCache status endpoint at $statusUrl : $($_.Exception.Message)"
        }

        if ($null -eq $response) {
            Start-Sleep -Seconds 2
            continue
        }

        $lastStatusCode = [int]$response.StatusCode

        if ($lastStatusCode -eq 200) {
            Write-Information "CDC phase: DocumentCache status endpoint answered 200 for the operator credential." -InformationAction Continue
            return
        }

        if ($lastStatusCode -eq 404) {
            throw "CDC phase: $statusUrl returned 404, so the DocumentCache status endpoint was never mapped. DataManagement__DocumentCache__Status__RequiredRole did not reach the DMS container; it must be set before the DMS start."
        }

        if ($lastStatusCode -eq 403) {
            throw "CDC phase: $statusUrl returned 403, so the operator token carries no $(Get-DocumentCacheStatusOperatorRole) role claim under the configured role claim type."
        }

        # 401 and 5xx are the shapes a just-started DMS answers with while its authority metadata
        # or projection runtime is still warming, so they are retried rather than judged.
        Start-Sleep -Seconds 2
    }

    throw "CDC phase: $statusUrl did not answer 200 within $TimeoutSeconds seconds; the last status was $lastStatusCode."
}

Export-ModuleMember -Function `
    Get-CdcRuntimeEnvOverride, `
    Resolve-CdcSourceDatabaseName, `
    Get-CdcContainerUserArgument, `
    Get-CdcConnectorPrincipalEnvArgument, `
    Get-CdcConnectorEnvArgument, `
    Get-CdcSetupComposeArgument, `
    Resolve-CdcHostBindingStateRoot, `
    Get-CdcNextGeneration, `
    Get-CdcEnableArgument, `
    Invoke-CdcEnablePhase, `
    Wait-CdcHttpEndpoint, `
    Assert-CdcDocumentCacheStatusEndpoint
