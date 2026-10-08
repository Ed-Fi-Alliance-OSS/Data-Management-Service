# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Decision and evidence helpers for the manual restore smoke (Invoke-BootstrapRestoreSmoke.ps1).

.DESCRIPTION
    Holds the smoke logic that can be proven without a live stack: which wrapper, teardown script,
    and compose project a run uses; which wrapper arguments carry -DataStandardVersion; whether
    Docker already holds a stack the smoke did not create; and the provenance record that decides
    whether a run's result is final evidence. Every provenance value is either observed from its
    source (git, MSBuild, docker inspect, the built package) or recorded as null with a reason; a
    missing observation is never reported as verified.
#>

$script:WrapperProfiles = @{
    local     = [pscustomobject]@{
        Wrapper             = "local"
        BootstrapScriptName = "bootstrap-local-dms.ps1"
        TeardownScriptName  = "start-local-dms.ps1"
        ComposeProject      = "dms-local"
    }
    published = [pscustomobject]@{
        Wrapper             = "published"
        BootstrapScriptName = "bootstrap-published-dms.ps1"
        TeardownScriptName  = "start-published-dms.ps1"
        ComposeProject      = "dms-published"
    }
}

# Both projects are inventoried whichever wrapper runs: their compose files share fixed container
# names, and a teardown with -v removes the selected project's named volumes.
$script:GuardedComposeProjects = @("dms-local", "dms-published")

# The engine compose file each start script swaps in ($databaseComposeFile in start-local-dms.ps1
# and start-published-dms.ps1). A volume a confirmed removal leaves behind is identified only
# against the top-level volumes these files declare.
$script:EngineComposeFiles = [ordered]@{
    postgresql = "postgresql.yml"
    mssql      = "mssql.yml"
}

# The smoke's own image builds (Get-RestoreSmokeImageBuildPlan). Repository, source directory, and
# VERSION mirror build-dms.ps1 / build-config.ps1 DockerBuild, whose version parameters default to
# 8.0.0; BootstrapRestoreSmoke.Tests.ps1 pins both. Tags are run-unique, never the shared local/ tags.
$script:ImageBuildVersion = "8.0.0"
$script:ImageRunTagPrefix = "dms-restore-smoke-"
$script:ImageBuildDefinitions = @(
    [pscustomobject]@{ Key = "Dms"; SourceDirectory = "src/dms"; Repository = "local/ed-fi-api" }
    [pscustomobject]@{ Key = "Config"; SourceDirectory = "src/config"; Repository = "local/ed-fi-api-configuration-service" }
)
$script:PublishedDmsRepository = "edfialliance/ed-fi-api"

# The served-data probe (Test-RestoreSmokeApiRead) uses the repository's own helpers: env-utility
# resolves the ports and the bootstrap admin client exactly as the configure phase does, and the
# CMS/DMS calls are the Dms-Management and SmokeTest functions the bootstrap flows use.
Import-Module (Join-Path $PSScriptRoot "../env-utility.psm1")
Import-Module (Join-Path $PSScriptRoot "../../smoke_test/modules/SmokeTest.psm1")
Import-Module (Join-Path $PSScriptRoot "../../Dms-Management.psm1")

# Relative to the DMS base URL. Descriptors exist in every seeded template; schools only in Populated.
$script:ApiProbeDescriptorPath = "data/ed-fi/academicSubjectDescriptors?limit=5"
$script:ApiProbeSchoolPath = "data/ed-fi/schools?limit=5"

function ConvertTo-RestoreSmokeLogSafeText {
    <#
    .SYNOPSIS
    Normalizes an externally sourced value (docker or git output) to one printable line before it
    reaches a log, an exception message, or the results JSON.
    #>
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Value
    )

    if ($null -eq $Value) {
        return ""
    }
    return ($Value -replace '[\p{Cc}\p{Cf}]', '?').Trim()
}

function Get-RestoreSmokeWrapperProfile {
    <#
    .SYNOPSIS
    The bootstrap wrapper, matching teardown script, and compose project for -Wrapper.
    #>
    param(
        [Parameter(Mandatory)]
        [ValidateSet("local", "published")]
        [string]$Wrapper
    )

    return $script:WrapperProfiles[$Wrapper.ToLowerInvariant()]
}

function Get-RestoreSmokeWrapperArgumentSet {
    <#
    .SYNOPSIS
    Copies a wrapper argument set and applies the Data Standard selection rule.

    .DESCRIPTION
    bootstrap-local-dms.ps1 always composes the .env.bootstrap.<ds> overlay, so the local wrapper
    always receives -DataStandardVersion (the smoke's default is 5.2). bootstrap-published-dms.ps1
    composes the overlay ONLY when -DataStandardVersion is bound, so the published wrapper receives
    it only when the smoke's caller supplied it explicitly - even when the explicit value equals
    the default. The input hashtable is not modified.
    #>
    param(
        [Parameter(Mandatory)]
        [hashtable]$Arguments,

        [Parameter(Mandatory)]
        [ValidateSet("local", "published")]
        [string]$Wrapper,

        [Parameter(Mandatory)]
        [ValidateSet("5.2", "6.1")]
        [string]$DataStandardVersion,

        [Parameter(Mandatory)]
        [bool]$DataStandardVersionSupplied
    )

    if ($Arguments.ContainsKey("DataStandardVersion")) {
        throw "Wrapper arguments must not carry DataStandardVersion directly; it is applied from the smoke's -DataStandardVersion selection."
    }

    $result = @{} + $Arguments
    if ($Wrapper -eq "local" -or $DataStandardVersionSupplied) {
        $result.DataStandardVersion = $DataStandardVersion
    }
    return $result
}

function Resolve-RestoreSmokeStandardVersion {
    <#
    .SYNOPSIS
    The package-id Data Standard segment: an explicit -StandardVersion, else <DataStandardVersion>.0.
    #>
    param(
        [AllowEmptyString()]
        [string]$StandardVersion,

        [Parameter(Mandatory)]
        [ValidateSet("5.2", "6.1")]
        [string]$DataStandardVersion
    )

    if (-not [string]::IsNullOrWhiteSpace($StandardVersion)) {
        return $StandardVersion
    }
    return "$DataStandardVersion.0"
}

function Get-RestoreSmokeForeignStackInventory {
    <#
    .SYNOPSIS
    Read-only inventory of every container (any state) and volume labelled with a guarded compose
    project. Fails closed: when docker cannot list them, the state is unknown and the smoke refuses.
    #>
    $projects = foreach ($project in $script:GuardedComposeProjects) {
        $filter = "label=com.docker.compose.project=$project"

        $global:LASTEXITCODE = 0
        $containerLines = @(docker ps -a --filter $filter --format '{{.Names}}|{{.State}}|{{.Label "com.docker.compose.service"}}' 2>&1)
        if ($LASTEXITCODE -ne 0) {
            $detail = ConvertTo-RestoreSmokeLogSafeText ($containerLines | Out-String)
            throw "Refusing to run: could not list containers for compose project '$project' (docker exit ${LASTEXITCODE}: $detail). Foreign Docker state is unknown, so nothing was stopped or removed."
        }

        $global:LASTEXITCODE = 0
        $volumeLines = @(docker volume ls --filter $filter --format '{{.Name}}' 2>&1)
        if ($LASTEXITCODE -ne 0) {
            $detail = ConvertTo-RestoreSmokeLogSafeText ($volumeLines | Out-String)
            throw "Refusing to run: could not list volumes for compose project '$project' (docker exit ${LASTEXITCODE}: $detail). Foreign Docker state is unknown, so nothing was stopped or removed."
        }

        $containers = @(
            $containerLines |
                ForEach-Object { [string]$_ } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                ForEach-Object {
                    $fields = $_.Split("|")
                    [pscustomobject]@{
                        Name    = ConvertTo-RestoreSmokeLogSafeText $fields[0]
                        State   = if ($fields.Count -gt 1) { ConvertTo-RestoreSmokeLogSafeText $fields[1] } else { "" }
                        Service = if ($fields.Count -gt 2) { ConvertTo-RestoreSmokeLogSafeText $fields[2] } else { "" }
                    }
                }
        )
        $volumes = @(
            $volumeLines |
                ForEach-Object { [string]$_ } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                ForEach-Object { ConvertTo-RestoreSmokeLogSafeText $_ }
        )

        [pscustomobject]@{
            Project    = $project
            Containers = $containers
            Volumes    = $volumes
        }
    }

    $projects = @($projects)
    return [pscustomobject]@{
        Projects        = $projects
        HasForeignState = @($projects | Where-Object { $_.Containers.Count -gt 0 -or $_.Volumes.Count -gt 0 }).Count -gt 0
        ForeignProjects = @($projects | Where-Object { $_.Containers.Count -gt 0 -or $_.Volumes.Count -gt 0 } | ForEach-Object { $_.Project })
    }
}

function Format-RestoreSmokeForeignStackInventory {
    <#
    .SYNOPSIS
    One line per project that holds containers or volumes, for refusal messages and logs.
    #>
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Inventory
    )

    $lines = foreach ($project in $Inventory.Projects) {
        if ($project.Containers.Count -eq 0 -and $project.Volumes.Count -eq 0) {
            continue
        }
        $containerText = if ($project.Containers.Count -gt 0) {
            ($project.Containers | ForEach-Object { "$($_.Name) ($($_.State))" }) -join ", "
        }
        else {
            "none"
        }
        $volumeText = if ($project.Volumes.Count -gt 0) { $project.Volumes -join ", " } else { "none" }
        "$($project.Project): containers $containerText; volumes $volumeText"
    }
    return (@($lines) -join "; ")
}

function Assert-RestoreSmokeNoForeignStack {
    <#
    .SYNOPSIS
    Refuses a run when Docker holds containers (running or stopped) or volumes of a guarded compose
    project, unless the caller confirmed their removal.
    #>
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Inventory,

        [switch]$ConfirmForeignStackRemoval
    )

    if (-not $Inventory.HasForeignState -or $ConfirmForeignStackRemoval) {
        return
    }

    throw ("Refusing to run: Docker already holds a stack this smoke did not create - " +
        (Format-RestoreSmokeForeignStackInventory -Inventory $Inventory) +
        ". The smoke tears down with -d -v, which would remove them. Nothing was stopped or removed. " +
        "Run on an isolated Docker host, or pass -ConfirmForeignStackRemoval only with the stack owner's approval.")
}

function Get-RestoreSmokeEngineVolumeDefinition {
    <#
    .SYNOPSIS
    The named volumes each database engine's compose file declares, read from the repository.

    .DESCRIPTION
    Reads the top-level volumes: keys (two-space-indented under a column-0 volumes:) of
    postgresql.yml and mssql.yml. Service-level volume lists are indented and ignored. Fails closed:
    a missing file, a file that declares no volume, or a key both engines declare means leftovers
    cannot be identified, so the caller refuses before removing anything.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$DockerComposeRoot
    )

    $definitions = foreach ($engine in $script:EngineComposeFiles.Keys) {
        $composeFile = $script:EngineComposeFiles[$engine]
        $path = Join-Path $DockerComposeRoot $composeFile
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Cannot identify leftover volumes: the $engine compose file '$composeFile' was not found in '$DockerComposeRoot'."
        }

        $keys = [System.Collections.Generic.List[string]]::new()
        $inVolumes = $false
        foreach ($line in [System.IO.File]::ReadAllLines($path)) {
            if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith("#")) {
                continue
            }
            if ($line -match '^\S') {
                $inVolumes = $line -match '^volumes:\s*(#.*)?$'
                continue
            }
            if ($inVolumes -and $line -match '^ {2}([A-Za-z0-9][A-Za-z0-9._-]*):') {
                $keys.Add($matches[1])
            }
        }
        if ($keys.Count -eq 0) {
            throw "Cannot identify leftover volumes: the $engine compose file '$composeFile' declares no top-level volume."
        }

        [pscustomobject]@{
            Engine      = $engine
            ComposeFile = $composeFile
            Volumes     = @($keys)
        }
    }

    $definitions = @($definitions)
    $declaredBy = @{}
    foreach ($definition in $definitions) {
        foreach ($key in $definition.Volumes) {
            if ($declaredBy.ContainsKey($key)) {
                throw "Cannot identify leftover volumes: volume '$key' is declared by both $($declaredBy[$key]) and $($definition.ComposeFile)."
            }
            $declaredBy[$key] = $definition.ComposeFile
        }
    }
    return $definitions
}

function ConvertTo-RestoreSmokeVolumeTimestamp {
    <#
    .SYNOPSIS
    A volume's CreatedAt as invariant round-trip UTC text. ConvertFrom-Json turns Docker's RFC 3339
    value into a DateTime, whose default string form depends on the culture.
    #>
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($Value -is [System.DateTime]) {
        return $Value.ToUniversalTime().ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Value -is [System.DateTimeOffset]) {
        return $Value.UtcDateTime.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
    }
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }
    return ConvertTo-RestoreSmokeLogSafeText $text
}

function Read-RestoreSmokeVolumeInspection {
    <#
    .SYNOPSIS
    One read-only docker volume inspect. Returns the parsed object, or a reason it could not be read.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $global:LASTEXITCODE = 0
    $output = @(docker volume inspect --format '{{json .}}' $Name 2>&1)
    $exitCode = $LASTEXITCODE
    $text = (@($output | ForEach-Object { [string]$_ }) -join "`n").Trim()
    if ($exitCode -ne 0) {
        return [pscustomobject]@{ Volume = $null; ExitCode = $exitCode; Reason = "inspection failed (docker exit ${exitCode}: $(ConvertTo-RestoreSmokeLogSafeText $text))" }
    }

    $volume = $null
    try {
        $volume = $text | ConvertFrom-Json -AsHashtable -NoEnumerate -ErrorAction Stop
    }
    catch {
        $volume = $null
    }
    if ($volume -isnot [System.Collections.IDictionary]) {
        return [pscustomobject]@{ Volume = $null; ExitCode = $exitCode; Reason = "the inspection output is not one JSON object" }
    }
    if ([string]$volume["Name"] -cne $Name) {
        return [pscustomobject]@{ Volume = $null; ExitCode = $exitCode; Reason = "the inspection returned volume '$(ConvertTo-RestoreSmokeLogSafeText ([string]$volume["Name"]))' instead" }
    }
    return [pscustomobject]@{ Volume = $volume; ExitCode = $exitCode; Reason = $null }
}

function Get-RestoreSmokeLeftoverVolumeClassification {
    <#
    .SYNOPSIS
    Decides, for every volume a confirmed removal left in a guarded compose project, whether the
    run may continue beside it.

    .DESCRIPTION
    Each volume is inspected (read-only) and allowed ONLY when its observed compose labels identify
    it as a volume the OTHER engine's compose file declares: the selected engine's compose files do
    not declare it, so this run's down -v teardowns leave it in place. Every other volume is
    blocked with a reason: an inspection failure or unreadable output, a missing or conflicting
    com.docker.compose.project label, a missing com.docker.compose.volume label, a name that is not
    <project>_<volume label>, the selected engine's own storage, or a volume no engine compose file
    declares. Never throws for a volume; the caller refuses when anything is blocked.
    #>
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Inventory,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [Parameter(Mandatory)]
        [object[]]$Definition
    )

    $selected = @($Definition | Where-Object { $_.Engine -eq $DatabaseEngine })
    if ($selected.Count -ne 1) {
        throw "No engine volume definition for '$DatabaseEngine'."
    }
    $selected = $selected[0]
    $declaredFiles = @($Definition | ForEach-Object { $_.ComposeFile }) -join ", "

    $records = [System.Collections.Generic.List[object]]::new()
    foreach ($project in @($Inventory.Projects)) {
        foreach ($name in @($project.Volumes)) {
            $record = [ordered]@{
                Project      = $project.Project
                Name         = $name
                ProjectLabel = $null
                VolumeLabel  = $null
                CreatedAt    = $null
                Engine       = $null
                ComposeFile  = $null
                Decision     = "blocked"
                Reason       = $null
            }
            $inspection = Read-RestoreSmokeVolumeInspection -Name $name
            if ($null -eq $inspection.Volume) {
                $record.Reason = $inspection.Reason
                $records.Add([pscustomobject]$record)
                continue
            }

            $labels = $inspection.Volume["Labels"]
            if ($labels -isnot [System.Collections.IDictionary]) {
                $labels = @{}
            }
            $projectLabel = [string]$labels["com.docker.compose.project"]
            $volumeLabel = [string]$labels["com.docker.compose.volume"]
            $record.ProjectLabel = if ([string]::IsNullOrWhiteSpace($projectLabel)) { $null } else { ConvertTo-RestoreSmokeLogSafeText $projectLabel }
            $record.VolumeLabel = if ([string]::IsNullOrWhiteSpace($volumeLabel)) { $null } else { ConvertTo-RestoreSmokeLogSafeText $volumeLabel }
            $record.CreatedAt = ConvertTo-RestoreSmokeVolumeTimestamp $inspection.Volume["CreatedAt"]

            $owner = @($Definition | Where-Object { @($_.Volumes) -ccontains $volumeLabel })
            if ([string]::IsNullOrWhiteSpace($projectLabel)) {
                $record.Reason = "it has no com.docker.compose.project label"
            }
            elseif ($projectLabel -cne $project.Project) {
                $record.Reason = "its com.docker.compose.project label is '$($record.ProjectLabel)', but it was listed under '$($project.Project)'"
            }
            elseif ([string]::IsNullOrWhiteSpace($volumeLabel)) {
                $record.Reason = "it has no com.docker.compose.volume label"
            }
            elseif ($name -cne "$($project.Project)_$volumeLabel") {
                $record.Reason = "its name does not match its compose labels (expected '$($project.Project)_$($record.VolumeLabel)')"
            }
            elseif ($owner.Count -eq 0) {
                $record.Reason = "volume '$($record.VolumeLabel)' is not declared by any engine compose file ($declaredFiles), so it cannot be identified"
            }
            else {
                $record.Engine = $owner[0].Engine
                $record.ComposeFile = $owner[0].ComposeFile
                if ($owner[0].Engine -eq $DatabaseEngine) {
                    $record.Reason = "it is the selected engine's ($DatabaseEngine) storage, declared in $($owner[0].ComposeFile); the run needs fresh storage"
                }
                else {
                    $record.Decision = "allowed"
                    $record.Reason = "declared in $($owner[0].ComposeFile) as $($owner[0].Engine) storage; this run uses $DatabaseEngine, whose $($selected.ComposeFile) does not declare it, so this run's down -v teardowns leave it in place"
                }
            }
            $records.Add([pscustomobject]$record)
        }
    }

    $volumes = @($records)
    return [pscustomobject]@{
        DatabaseEngine = $DatabaseEngine
        Volumes        = $volumes
        Allowed        = @($volumes | Where-Object { $_.Decision -eq "allowed" })
        Blocked        = @($volumes | Where-Object { $_.Decision -ne "allowed" })
    }
}

function Format-RestoreSmokeLeftoverVolumeClassification {
    <#
    .SYNOPSIS
    "<name> (<reason>)" for each volume with the given decision, for refusal messages and logs.
    #>
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Classification,

        [Parameter(Mandatory)]
        [ValidateSet("allowed", "blocked")]
        [string]$Decision
    )

    $volumes = @($Classification.Blocked)
    if ($Decision -eq "allowed") {
        $volumes = @($Classification.Allowed)
    }
    return (($volumes | ForEach-Object { "$($_.Name) ($($_.Reason))" }) -join "; ")
}

function Get-RestoreSmokeAllowedVolumeState {
    <#
    .SYNOPSIS
    Re-inspects each leftover volume the preflight allowed, at the end of the run: it is preserved
    only when it still exists with the CreatedAt the preflight observed.
    #>
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Classification
    )

    $states = foreach ($volume in @($Classification.Allowed)) {
        $state = [ordered]@{
            Name      = $volume.Name
            Present   = $false
            CreatedAt = $null
            Preserved = $false
            Reason    = $null
        }
        $inspection = Read-RestoreSmokeVolumeInspection -Name $volume.Name
        if ($null -eq $inspection.Volume) {
            $state.Reason = $inspection.Reason
        }
        else {
            $state.Present = $true
            $state.CreatedAt = ConvertTo-RestoreSmokeVolumeTimestamp $inspection.Volume["CreatedAt"]
            if ([string]::IsNullOrWhiteSpace($volume.CreatedAt)) {
                $state.Reason = "the preflight observed no CreatedAt, so preservation cannot be shown"
            }
            elseif ($state.CreatedAt -cne $volume.CreatedAt) {
                $state.Reason = "CreatedAt changed from '$($volume.CreatedAt)' to '$($state.CreatedAt)'; the volume was recreated"
            }
            else {
                $state.Preserved = $true
            }
        }
        [pscustomobject]$state
    }
    return @($states)
}

function Get-RestoreSmokeSourceRevision {
    <#
    .SYNOPSIS
    The observed worktree revision and porcelain status. Unobservable values stay null with a reason.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$RepoRoot
    )

    $global:LASTEXITCODE = 0
    $revisionOutput = @(git -C $RepoRoot rev-parse HEAD 2>&1)
    $revisionExit = $LASTEXITCODE

    $global:LASTEXITCODE = 0
    $statusOutput = @(git -C $RepoRoot status --porcelain 2>&1)
    $statusExit = $LASTEXITCODE

    $observed = ($revisionExit -eq 0 -and $statusExit -eq 0)
    # Assigned directly, never through an if expression: an if expression unrolls an empty array
    # to $null and a one-element array to a scalar, and .Count then throws under strict mode.
    $porcelain = @()
    if ($statusExit -eq 0) {
        $porcelain = @($statusOutput | ForEach-Object { ConvertTo-RestoreSmokeLogSafeText ([string]$_) } | Where-Object { $_ -ne "" })
    }

    return [pscustomobject]@{
        CapturedUtc = [System.DateTime]::UtcNow.ToString("o", [System.Globalization.CultureInfo]::InvariantCulture)
        Revision    = if ($revisionExit -eq 0) { ConvertTo-RestoreSmokeLogSafeText ([string]@($revisionOutput)[0]) } else { $null }
        Porcelain   = $porcelain
        Observed    = $observed
        Clean       = ($observed -and $porcelain.Count -eq 0)
        Reason      = if ($observed) { $null } else { "git rev-parse exit $revisionExit, git status exit $statusExit" }
    }
}

function Get-RestoreSmokeFileSha256 {
    <#
    .SYNOPSIS
    Lower-case hex SHA-256 of a file, the form the attestation payload records.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-RestoreSmokeSchemaToolBuild {
    <#
    .SYNOPSIS
    Builds api-schema-tools from the worktree in the configuration the bootstrap resolver prefers
    (Debug), then proves the resolver returns exactly the built executable.

    .DESCRIPTION
    The restore consumer resolves the tool by directory order and ignores DMS_SCHEMA_TOOL_PATH, so
    setting the variable alone would not make the run use the built tool. The record is Verified only
    when the build exited 0 and both the default resolution and the -RequestedPath resolution return
    the built path. -Resolver is invoked as `& $Resolver <requestedPath>` ("" for the default lookup).
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,

        [Parameter(Mandatory)]
        [scriptblock]$Resolver
    )

    $record = [ordered]@{
        BuildCommand          = "dotnet build `"$ProjectPath`" -c Debug"
        BuildExitCode         = $null
        ExecutablePath        = $null
        Sha256AtBuild         = $null
        Sha256AtEnd           = $null
        InformationalVersion  = $null
        ResolvedDefaultPath   = $null
        ResolvedRequestedPath = $null
        Verified              = $false
        Reason                = $null
    }

    $global:LASTEXITCODE = 0
    dotnet build $ProjectPath -c Debug | Out-Host
    $record.BuildExitCode = $LASTEXITCODE
    if ($record.BuildExitCode -ne 0) {
        $record.Reason = "dotnet build exited $($record.BuildExitCode)"
        return [pscustomobject]$record
    }

    $global:LASTEXITCODE = 0
    $targetDirectoryOutput = @(dotnet msbuild $ProjectPath -getProperty:TargetDir -p:Configuration=Debug 2>&1)
    if ($LASTEXITCODE -ne 0 -or $targetDirectoryOutput.Count -eq 0) {
        $record.Reason = "could not read the Debug TargetDir from MSBuild (exit $LASTEXITCODE)"
        return [pscustomobject]$record
    }
    $targetDirectory = ([string]@($targetDirectoryOutput | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })[-1]).Trim()
    $executableName = if ($IsWindows) { "api-schema-tools.exe" } else { "api-schema-tools" }
    $executablePath = [System.IO.Path]::GetFullPath((Join-Path $targetDirectory $executableName))
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        $record.Reason = "the build reported success but '$executablePath' does not exist"
        return [pscustomobject]$record
    }

    $record.ExecutablePath = $executablePath
    $record.Sha256AtBuild = Get-RestoreSmokeFileSha256 -Path $executablePath
    $assemblyPath = Join-Path $targetDirectory "api-schema-tools.dll"
    if (Test-Path -LiteralPath $assemblyPath -PathType Leaf) {
        $record.InformationalVersion = (Get-Item -LiteralPath $assemblyPath).VersionInfo.ProductVersion
    }

    $record.ResolvedDefaultPath = [string](& $Resolver "")
    $record.ResolvedRequestedPath = [string](& $Resolver $executablePath)
    if ($record.ResolvedDefaultPath -cne $executablePath) {
        $record.Reason = "the bootstrap resolver selects '$($record.ResolvedDefaultPath)', not the in-run build '$executablePath'"
        return [pscustomobject]$record
    }
    if ($record.ResolvedRequestedPath -cne $executablePath) {
        $record.Reason = "the requested-path resolution returned '$($record.ResolvedRequestedPath)', not '$executablePath'"
        return [pscustomobject]$record
    }
    if ([string]::IsNullOrWhiteSpace($record.InformationalVersion)) {
        $record.Reason = "the built api-schema-tools.dll has no informational version"
        return [pscustomobject]$record
    }

    $record.Verified = $true
    return [pscustomobject]$record
}

function Get-RestoreSmokeImageBuildPlan {
    <#
    .SYNOPSIS
    The smoke's own image builds: one docker buildx build per image, with the Dockerfile, context,
    parentdir build context, and VERSION build argument that build-dms.ps1 / build-config.ps1
    DockerBuild use, plus --load, a fresh --iidfile in the work directory, and run-unique tags.

    .DESCRIPTION
    The smoke does not call the build scripts: their fixed local/ tags are reusable, so an image
    found under them proves nothing about this run's build, and build-dms.ps1 DockerBuild neither
    loads the result nor propagates a docker failure. BootstrapRestoreSmoke.Tests.ps1 pins these
    arguments against both scripts' DockerBuild so they cannot drift apart silently.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$RepoRoot,

        [Parameter(Mandatory)]
        [string]$WorkDirectory,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9a-f]{12}$')]
        [string]$RunId,

        [Parameter(Mandatory)]
        [ValidateSet("local", "published")]
        [string]$Wrapper
    )

    $runTag = "$script:ImageRunTagPrefix$RunId"
    $imageDirectory = Join-Path $WorkDirectory "images"
    $plan = foreach ($definition in $script:ImageBuildDefinitions) {
        $tags = @("$($definition.Repository):$runTag")
        if ($definition.Key -eq "Dms" -and $Wrapper -eq "published") {
            # published-dms.yml pins the edfialliance/ed-fi-api repository and takes only the tag.
            $tags += "$($script:PublishedDmsRepository):$runTag"
        }
        $iidFile = Join-Path $imageDirectory "$($definition.Key.ToLowerInvariant()).iid"
        $arguments = @("buildx", "build", "--load", "--iidfile", $iidFile)
        foreach ($tag in $tags) {
            $arguments += @("-t", $tag)
        }
        $arguments += @("-f", "Dockerfile", ".", "--build-context", "parentdir=../", "--build-arg", "VERSION=$script:ImageBuildVersion")

        [pscustomobject]@{
            Key              = $definition.Key
            SourceDirectory  = $definition.SourceDirectory
            WorkingDirectory = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $definition.SourceDirectory))
            Tags             = $tags
            IidFile          = $iidFile
            Arguments        = $arguments
        }
    }
    return @($plan)
}

function New-RestoreSmokeImageLedger {
    <#
    .SYNOPSIS
    The provenance record for the in-run image builds. It exists before the first build so that tag
    ownership and every build record survive a later build's failure or exception, for cleanup and
    for the results JSON.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Creates an in-memory record only.')]
    param()

    return [ordered]@{
        Version   = $script:ImageBuildVersion
        OwnedTags = [System.Collections.Generic.List[string]]::new()
        Dms       = $null
        Config    = $null
        Cleanup   = [System.Collections.Generic.List[object]]::new()
    }
}

function Get-RestoreSmokeImageTagState {
    <#
    .SYNOPSIS
    Whether an image reference resolves: Present (with its image ID), Absent, or Unknown.

    .DESCRIPTION
    A failed docker image inspect is Absent only when every line of its output is a "No such image"
    message for exactly this reference. Any other failure (daemon unreachable, permission, transport)
    is Unknown, and callers must not treat it as absence.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Reference
    )

    $global:LASTEXITCODE = 0
    $output = @(docker image inspect $Reference --format '{{.Id}}' 2>&1 | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        if ($output.Count -eq 1 -and $output[0].Trim() -cmatch '^sha256:[0-9a-f]{64}$') {
            return [pscustomobject]@{ State = "Present"; ImageId = $output[0].Trim(); Detail = $null }
        }
        return [pscustomobject]@{ State = "Unknown"; ImageId = $null; Detail = "docker image inspect $Reference returned an unexpected ID: $(ConvertTo-RestoreSmokeLogSafeText ($output -join ' '))" }
    }

    $absentPattern = '^(?:Error response from daemon: |Error: )?No such image: (?:docker\.io/)?' + [regex]::Escape($Reference) + '$'
    if ($output.Count -gt 0 -and @($output | Where-Object { $_.Trim() -notmatch $absentPattern }).Count -eq 0) {
        return [pscustomobject]@{ State = "Absent"; ImageId = $null; Detail = $null }
    }
    return [pscustomobject]@{ State = "Unknown"; ImageId = $null; Detail = "docker image inspect $Reference exited ${exitCode}: $(ConvertTo-RestoreSmokeLogSafeText ($output -join ' '))" }
}

function Register-RestoreSmokeImageTagOwnership {
    <#
    .SYNOPSIS
    Claims every planned tag for this run, all together, only after proving each one absent and
    each iidfile path unused. A present tag or an inspection failure claims nothing.
    #>
    param(
        [Parameter(Mandatory)]
        [object[]]$Plan,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Ledger
    )

    foreach ($entry in $Plan) {
        if (Test-Path -LiteralPath $entry.IidFile) {
            throw "The iidfile path '$($entry.IidFile)' already exists, so it cannot identify this run's build; no image tag was claimed."
        }
        foreach ($tag in $entry.Tags) {
            $tagState = Get-RestoreSmokeImageTagState -Reference $tag
            if ($tagState.State -eq "Present") {
                throw "The run tag '$tag' already exists ($($tagState.ImageId)), so this run does not own it; no image tag was claimed."
            }
            if ($tagState.State -ne "Absent") {
                throw "Cannot establish that the run tag '$tag' is absent ($($tagState.Detail)); no image tag was claimed."
            }
        }
    }

    foreach ($entry in $Plan) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $entry.IidFile) -Force | Out-Null
        foreach ($tag in $entry.Tags) {
            $Ledger.OwnedTags.Add($tag)
        }
    }
}

function Invoke-RestoreSmokeImageBuild {
    <#
    .SYNOPSIS
    Runs each planned image build in order and records it in the ledger. An image is verified only
    when docker exited 0, the build wrote an image ID to its fresh iidfile, and every run tag
    resolves to that ID. Throws at the first build that is not verified.

    .DESCRIPTION
    Each record is stored in the ledger before docker runs, so a failure or exception in a later
    build leaves the earlier records (and the claimed tags) in place. No comparison is made with any
    image that existed before the run: a cached build may legitimately reproduce an existing ID, or
    produce a new one.
    #>
    param(
        [Parameter(Mandatory)]
        [object[]]$Plan,

        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Ledger
    )

    foreach ($entry in $Plan) {
        foreach ($tag in $entry.Tags) {
            if (-not $Ledger.OwnedTags.Contains($tag)) {
                throw "The run tag '$tag' was not claimed for this run; refusing to build under it."
            }
        }

        $record = [ordered]@{
            Command         = @("docker") + $entry.Arguments
            SourceDirectory = $entry.SourceDirectory
            Tags            = $entry.Tags
            ExitCode        = $null
            IidFileContent  = $null
            ImageId         = $null
            TagImageIds     = [ordered]@{}
            Verified        = $false
            Reason          = "the build did not complete"
        }
        $Ledger[$entry.Key] = $record

        if (Test-Path -LiteralPath $entry.IidFile) {
            $record.Reason = "the iidfile already existed before the build"
            throw "Image provenance could not be established for $($entry.Key): $($record.Reason)"
        }

        Push-Location $entry.WorkingDirectory
        try {
            $buildArguments = [string[]]$entry.Arguments
            $global:LASTEXITCODE = 0
            docker @buildArguments | Out-Host
            $record.ExitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }

        if ($record.ExitCode -ne 0) {
            $record.Reason = "docker buildx build ($($entry.Key)) exited $($record.ExitCode)"
        }
        elseif (-not (Test-Path -LiteralPath $entry.IidFile -PathType Leaf)) {
            $record.Reason = "the build produced no image ID (no iidfile)"
        }
        else {
            $record.IidFileContent = ConvertTo-RestoreSmokeLogSafeText (Get-Content -LiteralPath $entry.IidFile -Raw)
            if ($record.IidFileContent -cnotmatch '^sha256:[0-9a-f]{64}$') {
                $record.Reason = "the build produced no image ID (iidfile content '$($record.IidFileContent)')"
            }
            else {
                $mismatch = $null
                foreach ($tag in $entry.Tags) {
                    $tagState = Get-RestoreSmokeImageTagState -Reference $tag
                    $record.TagImageIds[$tag] = $tagState.ImageId
                    if ($null -ne $mismatch) {
                        continue
                    }
                    if ($tagState.State -eq "Absent") {
                        $mismatch = "the run tag '$tag' is missing after the build"
                    }
                    elseif ($tagState.State -ne "Present") {
                        $mismatch = "the run tag '$tag' could not be inspected after the build ($($tagState.Detail))"
                    }
                    elseif ($tagState.ImageId -cne $record.IidFileContent) {
                        $mismatch = "the run tag '$tag' resolves to $($tagState.ImageId), but the build reported $($record.IidFileContent)"
                    }
                }
                if ($null -eq $mismatch) {
                    $record.ImageId = $record.IidFileContent
                    $record.Verified = $true
                    $record.Reason = $null
                }
                else {
                    $record.Reason = $mismatch
                }
            }
        }

        if (-not $record.Verified) {
            throw "Image provenance could not be established for $($entry.Key): $($record.Reason)"
        }
    }
}

function Remove-RestoreSmokeOwnedImageTag {
    <#
    .SYNOPSIS
    Removes the tags this run claimed, and nothing else: no -f, no removal by image ID, no prune.
    Docker itself keeps an image a container still uses. Each outcome is recorded in the ledger;
    a failure warns and never changes the run's result.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal smoke cleanup; the smoke does not expose -WhatIf.')]
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Ledger,

        [switch]$Keep
    )

    foreach ($tag in @($Ledger.OwnedTags)) {
        if ($Keep) {
            $Ledger.Cleanup.Add([pscustomobject]@{ Tag = $tag; Action = "kept"; Detail = "-SkipTeardown" })
            continue
        }

        $tagState = Get-RestoreSmokeImageTagState -Reference $tag
        if ($tagState.State -eq "Absent") {
            $Ledger.Cleanup.Add([pscustomobject]@{ Tag = $tag; Action = "absent"; Detail = $null })
            continue
        }
        if ($tagState.State -ne "Present") {
            Write-Warning "Could not inspect the run tag '$tag' for cleanup: $($tagState.Detail)"
            $Ledger.Cleanup.Add([pscustomobject]@{ Tag = $tag; Action = "failed"; Detail = $tagState.Detail })
            continue
        }

        $global:LASTEXITCODE = 0
        $output = @(docker image rm $tag 2>&1 | ForEach-Object { [string]$_ })
        if ($LASTEXITCODE -eq 0) {
            $Ledger.Cleanup.Add([pscustomobject]@{ Tag = $tag; Action = "removed"; Detail = $null })
        }
        else {
            $detail = "docker image rm exited ${LASTEXITCODE}: $(ConvertTo-RestoreSmokeLogSafeText ($output -join ' '))"
            Write-Warning "Could not remove the run tag '$tag': $detail"
            $Ledger.Cleanup.Add([pscustomobject]@{ Tag = $tag; Action = "failed"; Detail = $detail })
        }
    }
}

function Write-RestoreSmokeImageEnvironmentFile {
    <#
    .SYNOPSIS
    Writes the smoke-owned env file: the base env with the image keys replaced so the selected
    wrapper's compose files resolve to the in-run built images.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$BaseEnvironmentFile,

        [Parameter(Mandatory)]
        [string]$TargetPath,

        [Parameter(Mandatory)]
        [ValidateSet("local", "published")]
        [string]$Wrapper,

        [Parameter(Mandatory)]
        [string]$ConfigImage,

        [string]$DmsImage,

        [string]$PublishedDmsTag
    )

    $imageKeys = if ($Wrapper -eq "local") {
        if ([string]::IsNullOrWhiteSpace($DmsImage)) {
            throw "The local wrapper needs -DmsImage."
        }
        [ordered]@{ DMS_DOCKER_IMAGE = $DmsImage; DMS_CONFIG_DOCKER_IMAGE = $ConfigImage }
    }
    else {
        if ([string]::IsNullOrWhiteSpace($PublishedDmsTag)) {
            throw "The published wrapper needs -PublishedDmsTag."
        }
        [ordered]@{ DMS_IMAGE_TAG = $PublishedDmsTag; DMS_CONFIG_DOCKER_IMAGE = $ConfigImage }
    }

    $keyPattern = '^\s*(?:' + (($imageKeys.Keys | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\s*='
    $lines = @(Get-Content -LiteralPath $BaseEnvironmentFile | Where-Object { $_ -notmatch $keyPattern })
    $lines += @($imageKeys.Keys | ForEach-Object { "$_=$($imageKeys[$_])" })
    Set-Content -LiteralPath $TargetPath -Value $lines -Encoding utf8
    return [pscustomobject]$imageKeys
}

function Get-RestoreSmokeStackObservation {
    <#
    .SYNOPSIS
    What the started stack actually runs: for the dms, config, and db services of the project, the
    container name, the image reference it was created from, and its image ID. The db service adds
    its image's repository digests, or the reason none were observed (a locally built or untagged
    image has none; the image ID alone does not identify the published engine image).
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ComposeProject,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $global:LASTEXITCODE = 0
    $containerLines = @(docker ps --filter "label=com.docker.compose.project=$ComposeProject" --format '{{.ID}}|{{.Names}}|{{.Label "com.docker.compose.service"}}' 2>&1)
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{ Label = $Label; Services = [ordered]@{}; Reason = "docker ps failed (exit $LASTEXITCODE)" }
    }

    $services = [ordered]@{}
    foreach ($line in @($containerLines | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
        $fields = $line.Split("|")
        if ($fields.Count -lt 3 -or $fields[2] -notin @("dms", "config", "db")) {
            continue
        }

        $global:LASTEXITCODE = 0
        $inspect = @(docker inspect $fields[0] --format '{{.Image}}|{{.Config.Image}}' 2>&1)
        if ($LASTEXITCODE -ne 0 -or $inspect.Count -eq 0) {
            continue
        }
        $imageFields = ([string]$inspect[0]).Split("|")
        $observation = [ordered]@{
            Container  = ConvertTo-RestoreSmokeLogSafeText $fields[1]
            ImageId    = ConvertTo-RestoreSmokeLogSafeText $imageFields[0]
            ImageRef   = if ($imageFields.Count -gt 1) { ConvertTo-RestoreSmokeLogSafeText $imageFields[1] } else { $null }
            RepoDigests = @()
            RepoDigestsReason = $null
        }
        if ($fields[2] -eq "db") {
            $global:LASTEXITCODE = 0
            $digests = @(docker image inspect $observation.ImageId --format '{{join .RepoDigests ","}}' 2>&1)
            if ($LASTEXITCODE -ne 0) {
                $observation.RepoDigestsReason = "docker image inspect $($observation.ImageId) exited ${LASTEXITCODE}: $(ConvertTo-RestoreSmokeLogSafeText (($digests | ForEach-Object { [string]$_ }) -join ' '))"
            }
            else {
                $observation.RepoDigests = @(([string]@($digests)[0]).Split(",") | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { ConvertTo-RestoreSmokeLogSafeText $_ })
                if ($observation.RepoDigests.Count -eq 0) {
                    $observation.RepoDigestsReason = "the image has no repository digest (locally built or untagged)"
                }
            }
        }
        $services[$fields[2]] = [pscustomobject]$observation
    }

    return [pscustomobject]@{ Label = $Label; Services = $services; Reason = $null }
}

function Get-RestoreSmokeEffectiveSchemaPackageList {
    <#
    .SYNOPSIS
    SCHEMA_PACKAGES from the derived env file the wrapper handed to the start phase, or null with a reason.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$DerivedEnvironmentFile
    )

    if (-not (Test-Path -LiteralPath $DerivedEnvironmentFile -PathType Leaf)) {
        return [pscustomobject]@{ Source = $DerivedEnvironmentFile; Value = $null; Reason = "derived env file not present" }
    }
    try {
        $lines = @(Get-Content -LiteralPath $DerivedEnvironmentFile -ErrorAction Stop)
    }
    catch {
        return [pscustomobject]@{ Source = $DerivedEnvironmentFile; Value = $null; Reason = "derived env file could not be read: $(ConvertTo-RestoreSmokeLogSafeText $_.Exception.Message)" }
    }
    foreach ($line in $lines) {
        if ($line -match '^\s*SCHEMA_PACKAGES\s*=\s*(.*)$') {
            $value = $matches[1].Trim()
            if ([string]::IsNullOrWhiteSpace($value)) {
                return [pscustomobject]@{ Source = $DerivedEnvironmentFile; Value = $null; Reason = "SCHEMA_PACKAGES is blank in the derived env file" }
            }
            return [pscustomobject]@{ Source = $DerivedEnvironmentFile; Value = $value; Reason = $null }
        }
    }
    return [pscustomobject]@{ Source = $DerivedEnvironmentFile; Value = $null; Reason = "SCHEMA_PACKAGES not set in the derived env file" }
}

function Get-RestoreSmokePackageProvenance {
    <#
    .SYNOPSIS
    The built template package as observed on disk: file SHA-256, the attestation payload's package
    id, version, recorded SHA-256 and producer, the signing key id, and the in-package restore
    manifest's projects. Verified only when every value was read and the payload SHA equals the file SHA.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$PackageDirectory,

        [Parameter(Mandatory)]
        [string]$RestoreManifestFileName,

        [Parameter(Mandatory)]
        [string]$TemplateKind
    )

    $record = [ordered]@{
        TemplateKind            = $TemplateKind
        PackageFile             = $null
        Sha256                  = $null
        PackageId               = $null
        PackageVersion          = $null
        AttestationPackageSha256 = $null
        AttestationProducer     = $null
        AttestationKeyId        = $null
        RestoreManifestProjects = $null
        Verified                = $false
        Reason                  = $null
    }

    $packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter "*.nupkg" -File | Where-Object { $_.Name -notlike "*.Attestation.*" })
    if ($packages.Count -ne 1) {
        $record.Reason = "expected one template .nupkg, found $($packages.Count)"
        return [pscustomobject]$record
    }
    $record.PackageFile = $packages[0].Name
    $record.Sha256 = Get-RestoreSmokeFileSha256 -Path $packages[0].FullName

    $attestationPath = "$($packages[0].FullName).attestation.json"
    if (-not (Test-Path -LiteralPath $attestationPath -PathType Leaf)) {
        $record.Reason = "attestation document '$([System.IO.Path]::GetFileName($attestationPath))' not found"
        return [pscustomobject]$record
    }
    $attestation = Get-Content -LiteralPath $attestationPath -Raw | ConvertFrom-Json
    $payload = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String([string]$attestation.payloadB64)) | ConvertFrom-Json
    $record.PackageId = [string]$payload.packageId
    $record.PackageVersion = [string]$payload.packageVersion
    $record.AttestationPackageSha256 = ([string]$payload.packageSha256).ToLowerInvariant()
    $record.AttestationProducer = [string]$payload.producer
    $record.AttestationKeyId = [string]$attestation.signature.keyId

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
    try {
        $manifestEntry = @($archive.Entries | Where-Object { $_.Name -eq $RestoreManifestFileName })
        if ($manifestEntry.Count -ne 1) {
            $record.Reason = "expected one '$RestoreManifestFileName' in the package, found $($manifestEntry.Count)"
            return [pscustomobject]$record
        }
        $reader = [System.IO.StreamReader]::new($manifestEntry[0].Open())
        try {
            $manifest = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
    $record.RestoreManifestProjects = @($manifest.projects | ForEach-Object { [string]$_ })

    if ($record.AttestationPackageSha256 -cne $record.Sha256) {
        $record.Reason = "the attestation payload records packageSha256 $($record.AttestationPackageSha256), but the file hashes to $($record.Sha256)"
        return [pscustomobject]$record
    }
    if ([string]::IsNullOrWhiteSpace($record.AttestationProducer) -or [string]::IsNullOrWhiteSpace($record.AttestationKeyId)) {
        $record.Reason = "the attestation lacks a producer or key id"
        return [pscustomobject]$record
    }

    $record.Verified = $true
    return [pscustomobject]$record
}

function ConvertTo-RestoreSmokeRedactedText {
    <#
    .SYNOPSIS
    Log-safe text in which every supplied secret (a client secret or a bearer token) is replaced by
    ***. Used for messages built from a helper's exception, whose text the probe does not control.
    #>
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Value,

        [AllowNull()]
        [AllowEmptyCollection()]
        [string[]]$Secret = @()
    )

    $text = [string]$Value
    foreach ($item in @($Secret | Where-Object { -not [string]::IsNullOrEmpty($_) })) {
        $text = $text.Replace($item, "***")
    }
    return (ConvertTo-RestoreSmokeLogSafeText $text)
}

function Resolve-RestoreSmokeApiEndpoint {
    <#
    .SYNOPSIS
    The CMS and DMS base URLs and the bootstrap admin client of the stack an env file starts, read
    with the helpers the configure phase uses (Resolve-CmsBaseUrl, Resolve-DockerLocalDmsBaseUrl,
    Resolve-BootstrapAdminClient). The engine, Data Standard, and image overlays the wrappers compose
    set none of these keys. A multi-tenant stack is refused: its data stores are tenant-scoped and
    its DMS routes carry a tenant segment, which the probe does not model.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$EnvironmentFile
    )

    $values = ReadValuesFromEnvFile -EnvironmentFile $EnvironmentFile
    $multiTenancy = [string](Get-EnvValue -EnvValues $values -Name "DMS_CONFIG_MULTI_TENANCY" -DefaultValue "false")
    if ($multiTenancy.Trim().Trim('"').Trim("'") -ine "false") {
        throw "The served-data probe supports only a single-tenant stack, but DMS_CONFIG_MULTI_TENANCY is '$(ConvertTo-RestoreSmokeLogSafeText $multiTenancy)' in '$EnvironmentFile'."
    }

    return [pscustomobject]@{
        CmsUrl      = ([string](Resolve-CmsBaseUrl -EnvValues $values)).TrimEnd("/")
        DmsUrl      = ([string](Resolve-DockerLocalDmsBaseUrl -EnvValues $values)).TrimEnd("/")
        AdminClient = Resolve-BootstrapAdminClient -EnvValues $values
    }
}

function New-RestoreSmokeApiSession {
    <#
    .SYNOPSIS
    The served-data probe's per-stack credential cache. The first Test-RestoreSmokeApiRead against a
    started stack fills it (one smoke application, one DMS token); every later probe request against
    that stack reuses the token. Reset-RestoreSmokeApiSession empties it whenever the stack is torn
    down or started again. The token is held only here and is never logged or recorded.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Creates an in-memory record only.')]
    param()

    return [pscustomobject]@{
        StackGeneration   = 0
        Token             = $null
        DataStore         = $null
        TokenRequestCount = 0
    }
}

function Reset-RestoreSmokeApiSession {
    <#
    .SYNOPSIS
    Forgets the cached token and data-store selection, so the next probe obtains new ones. The smoke
    calls it before every teardown and every wrapper run, because each recreates the stack.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Clears an in-memory record only.')]
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Session
    )

    $Session.Token = $null
    $Session.DataStore = $null
    $Session.StackGeneration++
}

function Select-RestoreSmokeDataStore {
    <#
    .SYNOPSIS
    The one route-unqualified CMS data store whose stored connection string has the selected engine's
    form and names the restored target database. No match, or more than one, throws, listing every
    data store by id and name with the reason it was or was not selected. Connection strings carry
    credentials, so only the parsed database name is ever reported.
    #>
    param(
        [AllowNull()]
        [AllowEmptyCollection()]
        [object[]]$DataStores,

        [Parameter(Mandatory)]
        [string]$TargetDatabaseName,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine
    )

    # The keys configure-local-data-store.ps1 writes: host/database for PostgreSQL, Server/Database
    # for SQL Server (Data Source / Initial Catalog are the SqlClient synonyms).
    $engineKeys = @("Host")
    $databaseKeys = @("Database")
    if ($DatabaseEngine -eq "mssql") {
        $engineKeys = @("Server", "Data Source")
        $databaseKeys = @("Database", "Initial Catalog")
    }

    $records = [System.Collections.Generic.List[object]]::new()
    foreach ($dataStore in @($DataStores | Where-Object { $null -ne $_ })) {
        $record = [ordered]@{ Id = $null; Name = $null; DatabaseName = $null; Selected = $false; Reason = $null }
        $records.Add($record)

        $record.Name = ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $dataStore "name"))
        $parsedId = [long]0
        if (-not [long]::TryParse([string](Get-RestoreSmokeEvidenceValue $dataStore "id"), [System.Globalization.NumberStyles]::None, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsedId) -or $parsedId -lt 1) {
            $record.Reason = "it has no positive integer id"
            continue
        }
        $record.Id = $parsedId

        $contextList = Get-RestoreSmokeEvidenceValue $dataStore "dataStoreContexts"
        if ($contextList -is [string]) {
            $record.Reason = "its route contexts are not a list"
            continue
        }
        $contexts = @()
        if ($null -ne $contextList) {
            $contexts = @(@($contextList) | Where-Object { $null -ne $_ })
        }
        if ($contexts.Count -gt 0) {
            $pairs = @($contexts | ForEach-Object {
                    "$(ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $_ 'contextKey')))=$(ConvertTo-RestoreSmokeLogSafeText ([string](Get-RestoreSmokeEvidenceValue $_ 'contextValue')))"
                })
            $record.Reason = "it is route-qualified ($($pairs -join ', '))"
            continue
        }

        $connectionString = [string](Get-RestoreSmokeEvidenceValue $dataStore "connectionString")
        if ([string]::IsNullOrWhiteSpace($connectionString)) {
            $record.Reason = "CMS returned no connection string for it"
            continue
        }
        $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
        try {
            # The setter, not property syntax: PowerShell adapts the builder as a dictionary, so
            # "$builder.ConnectionString = ..." would only add a key named ConnectionString.
            $builder.set_ConnectionString($connectionString)
        }
        catch {
            $record.Reason = "its connection string could not be parsed"
            continue
        }
        if (@($engineKeys | Where-Object { $builder.ContainsKey($_) }).Count -eq 0) {
            $record.Reason = "its connection string is not the $DatabaseEngine form (no $($engineKeys -join ' or ') key)"
            continue
        }
        $databaseNames = @($databaseKeys | Where-Object { $builder.ContainsKey($_) } | ForEach-Object { [string]$builder[$_] })
        if ($databaseNames.Count -ne 1) {
            $record.Reason = "its connection string names $($databaseNames.Count) databases"
            continue
        }
        $record.DatabaseName = ConvertTo-RestoreSmokeLogSafeText $databaseNames[0]
        if ($databaseNames[0] -cne $TargetDatabaseName) {
            $record.Reason = "it targets database '$($record.DatabaseName)', not '$TargetDatabaseName'"
            continue
        }
        $record.Selected = $true
        $record.Reason = "route-unqualified $DatabaseEngine data store for '$TargetDatabaseName'"
    }

    $candidates = @($records | ForEach-Object { [pscustomobject]$_ })
    $selected = @($candidates | Where-Object { $_.Selected })
    $listing = ($candidates | ForEach-Object { "id=$($_.Id) name='$($_.Name)': $($_.Reason)" }) -join "; "
    if ($selected.Count -eq 0) {
        $detail = ""
        if ($candidates.Count -gt 0) {
            $detail = ": $listing"
        }
        throw "No route-unqualified data store targets the restored database '$TargetDatabaseName' ($($candidates.Count) listed$detail)."
    }
    if ($selected.Count -gt 1) {
        throw "$($selected.Count) route-unqualified data stores target the restored database '$TargetDatabaseName' (ids $(($selected | ForEach-Object { $_.Id }) -join ', ')); the probe needs exactly one. Listed: $listing."
    }

    return [pscustomobject]@{
        DataStoreId = $selected[0].Id
        Name        = $selected[0].Name
        Candidates  = $candidates
    }
}

function Get-RestoreSmokeJsonArrayLength {
    <#
    .SYNOPSIS
    Classifies a response body with System.Text.Json rather than ConvertFrom-Json, whose collection
    conversion hides the shape: PowerShell unrolls an empty array to nothing and a one-element array
    to its element, which then looks like an object. Returns the length only for a JSON array whose
    elements are all objects; anything else returns the reason it is not one.
    #>
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Content
    )

    if ([string]::IsNullOrWhiteSpace($Content)) {
        return [pscustomobject]@{ IsArray = $false; Length = $null; Reason = "the body is empty" }
    }
    try {
        $document = [System.Text.Json.JsonDocument]::Parse($Content)
    }
    catch {
        return [pscustomobject]@{ IsArray = $false; Length = $null; Reason = "the body is not a single JSON value" }
    }
    try {
        $root = $document.RootElement
        if ($root.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
            return [pscustomobject]@{ IsArray = $false; Length = $null; Reason = "the body's JSON root is $($root.ValueKind.ToString().ToLowerInvariant()), not an array" }
        }
        $index = 0
        foreach ($element in $root.EnumerateArray()) {
            if ($element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                return [pscustomobject]@{ IsArray = $false; Length = $null; Reason = "array element $index is $($element.ValueKind.ToString().ToLowerInvariant()), not a resource object" }
            }
            $index++
        }
        return [pscustomobject]@{ IsArray = $true; Length = $root.GetArrayLength(); Reason = $null }
    }
    finally {
        $document.Dispose()
    }
}

function Invoke-RestoreSmokeApiGet {
    <#
    .SYNOPSIS
    One authenticated GET, returning the status code and the raw body whatever the status.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Uri,

        [Parameter(Mandatory)]
        [string]$Token
    )

    $response = Invoke-WebRequest -Uri $Uri -Method Get -Headers @{ Authorization = "Bearer $Token" } -SkipHttpErrorCheck -TimeoutSec 30 -ErrorAction Stop
    $content = $response.Content
    if ($content -is [byte[]]) {
        $content = [System.Text.Encoding]::UTF8.GetString($content)
    }
    return [pscustomobject]@{ StatusCode = [int]$response.StatusCode; Content = [string]$content }
}

function Test-RestoreSmokeApiRead {
    <#
    .SYNOPSIS
    The authenticated served-data probe. Reads the restored data through the DMS API and throws
    unless every read returns HTTP 200 with a JSON array of resource objects. Returns the evidence
    record; no token or secret appears in it or in any message.

    .DESCRIPTION
    academicSubjectDescriptors must be a non-empty array. -SchemaOnly (the source was not seeded)
    accepts an empty array and reports the read as schema-only. -RequirePopulatedData also requires
    a non-empty schools array.

    Credentials and token budget: the first call against a stack lists the CMS data stores with the
    bootstrap admin client, selects the one route-unqualified data store for the restored target,
    creates the smoke application bound to it (Get-SmokeTestCredential), waits until CMS accepts the
    new client (Wait-CmsClientAvailable; OpenIddict surfaces new applications asynchronously), and
    calls Get-DmsToken once. Every later read against the same stack reuses that token, until
    Reset-RestoreSmokeApiSession. The CMS per-client token limit keeps its default.
    #>
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Session,

        [Parameter(Mandatory)]
        [pscustomobject]$Endpoint,

        [Parameter(Mandatory)]
        [string]$TargetDatabaseName,

        [Parameter(Mandatory)]
        [ValidateSet("postgresql", "mssql")]
        [string]$DatabaseEngine,

        [switch]$RequirePopulatedData,

        [switch]$SchemaOnly
    )

    if ($RequirePopulatedData -and $SchemaOnly) {
        throw "A Populated served-data read needs a seeded source; -SchemaOnly cannot be combined with -RequirePopulatedData."
    }

    $adminSecret = [string]$Endpoint.AdminClient.ClientSecret
    $tokenReused = $null -ne $Session.Token
    if (-not $tokenReused) {
        $cmsToken = $null
        try {
            $cmsToken = Get-CmsToken -CmsUrl $Endpoint.CmsUrl -ClientId ([string]$Endpoint.AdminClient.ClientId) -ClientSecret $adminSecret
            $dataStores = @(Get-DataStore -CmsUrl $Endpoint.CmsUrl -AccessToken $cmsToken)
        }
        catch {
            throw "The served-data probe could not list the CMS data stores: $(ConvertTo-RestoreSmokeRedactedText $_.Exception.Message -Secret @($adminSecret, $cmsToken))"
        }
        $selection = Select-RestoreSmokeDataStore -DataStores $dataStores -TargetDatabaseName $TargetDatabaseName -DatabaseEngine $DatabaseEngine

        $applicationSecret = $null
        $token = $null
        try {
            $credential = Get-SmokeTestCredential -ConfigServiceUrl $Endpoint.CmsUrl -DataStoreIds @($selection.DataStoreId)
            $applicationSecret = [string]$credential.Secret
            Wait-CmsClientAvailable -CmsUrl $Endpoint.CmsUrl -ClientId ([string]$credential.Key) -ClientSecret $applicationSecret
            $Session.TokenRequestCount++
            $token = Get-DmsToken -DmsUrl $Endpoint.DmsUrl -Key ([string]$credential.Key) -Secret $applicationSecret
        }
        catch {
            throw "The served-data probe could not obtain a DMS token for data store $($selection.DataStoreId): $(ConvertTo-RestoreSmokeRedactedText $_.Exception.Message -Secret @($adminSecret, $cmsToken, $applicationSecret, $token))"
        }
        if ([string]::IsNullOrWhiteSpace([string]$token)) {
            throw "The served-data probe received no DMS token for data store $($selection.DataStoreId)."
        }
        $Session.Token = [string]$token
        $Session.DataStore = $selection
    }

    $resources = [System.Collections.Generic.List[object]]::new()
    $resources.Add([pscustomobject]@{ Name = "academicSubjectDescriptors"; Path = $script:ApiProbeDescriptorPath; AllowEmpty = [bool]$SchemaOnly })
    if ($RequirePopulatedData) {
        $resources.Add([pscustomobject]@{ Name = "schools"; Path = $script:ApiProbeSchoolPath; AllowEmpty = $false })
    }

    $reads = [System.Collections.Generic.List[object]]::new()
    foreach ($resource in $resources) {
        $uri = "$($Endpoint.DmsUrl.TrimEnd('/'))/$($resource.Path)"
        try {
            $response = Invoke-RestoreSmokeApiGet -Uri $uri -Token $Session.Token
        }
        catch {
            throw "GET $uri failed: $(ConvertTo-RestoreSmokeRedactedText $_.Exception.Message -Secret @($Session.Token))"
        }
        if ($response.StatusCode -ne 200) {
            $body = ConvertTo-RestoreSmokeRedactedText $response.Content -Secret @($Session.Token)
            if ($body.Length -gt 200) {
                $body = $body.Substring(0, 200) + "..."
            }
            throw "GET $uri returned HTTP $($response.StatusCode); the served-data probe requires 200. Body: $body"
        }
        $shape = Get-RestoreSmokeJsonArrayLength -Content $response.Content
        if (-not $shape.IsArray) {
            throw "GET $uri returned HTTP 200, but $($shape.Reason); the served-data probe requires a JSON array of $($resource.Name)."
        }
        if ($shape.Length -lt 1 -and -not $resource.AllowEmpty) {
            throw "GET $uri returned an empty array; the restored $($resource.Name) were not served."
        }
        $reads.Add([pscustomobject]@{ Resource = $resource.Name; Uri = $uri; StatusCode = $response.StatusCode; Count = $shape.Length })
    }

    $mode = "seeded"
    if ($RequirePopulatedData) {
        $mode = "populated"
    }
    elseif ($SchemaOnly) {
        $mode = "schema-only"
    }
    return [pscustomobject]@{
        Mode                = $mode
        CmsUrl              = $Endpoint.CmsUrl
        DmsUrl              = $Endpoint.DmsUrl
        DataStoreId         = $Session.DataStore.DataStoreId
        DataStoreName       = $Session.DataStore.Name
        DataStoreCandidates = $Session.DataStore.Candidates
        StackGeneration     = $Session.StackGeneration
        TokenReused         = $tokenReused
        TokenRequestCount   = $Session.TokenRequestCount
        Reads               = @($reads)
    }
}

function Get-RestoreSmokeEvidenceValue {
    <#
    .SYNOPSIS
    Reads one field of an evidence record (a dictionary or an object) and returns null when the
    record or the field is missing, so an incomplete run is classified rather than thrown on.
    Collections are returned as one object (not enumerated); callers normalize them with @().
    #>
    param(
        [AllowNull()]
        [object]$Source,

        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($null -eq $Source) {
        return $null
    }
    if ($Source -is [System.Collections.IDictionary]) {
        if ($Source.Contains($Name)) {
            return , $Source[$Name]
        }
        return $null
    }
    $property = $Source.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return , $property.Value
}

function Get-RestoreSmokeResultClassification {
    <#
    .SYNOPSIS
    Decides whether a run's evidence is final. Every unmet condition is a reason; any reason makes
    the result non-final. A missing observation is never treated as verified, and missing or
    malformed evidence produces a reason instead of an exception.

    .DESCRIPTION
    Final requires, besides the build, package, and SourceIdentity evidence: observed, non-empty,
    equal start and end revisions with a clean tree at both points; and for EVERY stack observation
    the dms/config containers running the verified in-run image IDs, a db container whose image has
    an observed repository digest (an image ID alone is not a digest), and an observed, non-blank
    effective SCHEMA_PACKAGES value. Every leftover volume the preflight allowed must be shown
    preserved at run end.
    #>
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Provenance
    )

    $reasons = [System.Collections.Generic.List[string]]::new()

    if (Get-RestoreSmokeEvidenceValue $Provenance "ExploratoryPackage") {
        $reasons.Add("-ExploratoryPackage marks this run exploratory")
    }

    $revisions = [ordered]@{}
    foreach ($point in @("SourceAtStart", "SourceAtEnd")) {
        $source = Get-RestoreSmokeEvidenceValue $Provenance $point
        if ($null -eq $source) {
            $reasons.Add("$point not captured")
            continue
        }
        if ((Get-RestoreSmokeEvidenceValue $source "Observed") -ne $true) {
            $reasons.Add("$point not observed: $(Get-RestoreSmokeEvidenceValue $source 'Reason')")
            continue
        }
        $revision = [string](Get-RestoreSmokeEvidenceValue $source "Revision")
        if ([string]::IsNullOrWhiteSpace($revision)) {
            $reasons.Add("$point revision is empty")
        }
        else {
            $revisions[$point] = $revision
        }
        $porcelain = Get-RestoreSmokeEvidenceValue $source "Porcelain"
        # Assigned directly, never through an if expression: an if expression unrolls arrays.
        $porcelainEntries = @()
        if ($null -ne $porcelain) {
            $porcelainEntries = @(@($porcelain) | Where-Object { $null -ne $_ })
        }
        $porcelainCount = $porcelainEntries.Count
        if ((Get-RestoreSmokeEvidenceValue $source "Clean") -ne $true -or $porcelainCount -gt 0) {
            $reasons.Add("$point worktree is dirty ($porcelainCount porcelain entries)")
        }
    }
    if ($revisions.Count -eq 2 -and $revisions["SourceAtStart"] -cne $revisions["SourceAtEnd"]) {
        $reasons.Add("the revision changed during the run (start $($revisions['SourceAtStart']), end $($revisions['SourceAtEnd']))")
    }

    $schemaTools = Get-RestoreSmokeEvidenceValue $Provenance "SchemaTools"
    if ($null -eq $schemaTools) {
        $reasons.Add("SchemaTools was not built in this run")
    }
    elseif ((Get-RestoreSmokeEvidenceValue $schemaTools "Verified") -ne $true) {
        $reasons.Add("SchemaTools not verified: $(Get-RestoreSmokeEvidenceValue $schemaTools 'Reason')")
    }
    elseif ([string]::IsNullOrWhiteSpace([string](Get-RestoreSmokeEvidenceValue $schemaTools "Sha256AtBuild")) -or
        (Get-RestoreSmokeEvidenceValue $schemaTools "Sha256AtEnd") -cne (Get-RestoreSmokeEvidenceValue $schemaTools "Sha256AtBuild")) {
        $reasons.Add("the SchemaTools executable changed during the run (or was not re-hashed at the end)")
    }

    $images = Get-RestoreSmokeEvidenceValue $Provenance "Images"
    $verifiedImageIds = @{}
    if ($null -eq $images) {
        $reasons.Add("images were not built in this run")
    }
    else {
        foreach ($key in @("Dms", "Config")) {
            $built = Get-RestoreSmokeEvidenceValue $images $key
            $builtId = [string](Get-RestoreSmokeEvidenceValue $built "ImageId")
            if ($null -eq $built -or (Get-RestoreSmokeEvidenceValue $built "Verified") -ne $true -or [string]::IsNullOrWhiteSpace($builtId)) {
                $reason = if ($null -ne $built) { Get-RestoreSmokeEvidenceValue $built "Reason" } else { "missing" }
                $reasons.Add("$key image not verified as built in this run: $reason")
            }
            else {
                $verifiedImageIds[$key] = $builtId
            }
        }
    }

    $observationList = Get-RestoreSmokeEvidenceValue $Provenance "StackObservations"
    $observations = @()
    if ($null -ne $observationList) {
        $observations = @($observationList)
    }
    if ($observations.Count -eq 0) {
        $reasons.Add("no started stack was observed, so the images actually used are unknown")
    }
    for ($index = 0; $index -lt $observations.Count; $index++) {
        $observation = $observations[$index]
        $label = [string](Get-RestoreSmokeEvidenceValue $observation "Label")
        if ([string]::IsNullOrWhiteSpace($label)) {
            $label = "stack observation $($index + 1)"
        }
        if ($null -eq $observation) {
            $reasons.Add("${label}: the observation record is missing")
            continue
        }

        $observationReason = [string](Get-RestoreSmokeEvidenceValue $observation "Reason")
        if (-not [string]::IsNullOrWhiteSpace($observationReason)) {
            $reasons.Add("${label}: the stack could not be observed: $observationReason")
        }

        $services = Get-RestoreSmokeEvidenceValue $observation "Services"
        if ($null -ne $images) {
            foreach ($pair in @(@{ Service = "dms"; Key = "Dms" }, @{ Service = "config"; Key = "Config" })) {
                $observed = Get-RestoreSmokeEvidenceValue $services $pair.Service
                $observedId = [string](Get-RestoreSmokeEvidenceValue $observed "ImageId")
                $builtId = if ($verifiedImageIds.ContainsKey($pair.Key)) { $verifiedImageIds[$pair.Key] } else { "none verified" }
                if ($null -eq $observed) {
                    $reasons.Add("${label}: no $($pair.Service) container observed")
                }
                elseif (-not $verifiedImageIds.ContainsKey($pair.Key) -or $observedId -cne $builtId) {
                    $reasons.Add("${label}: $($pair.Service) runs image $observedId, not the in-run build ($builtId)")
                }
            }
        }

        $database = Get-RestoreSmokeEvidenceValue $services "db"
        if ($null -eq $database) {
            $reasons.Add("${label}: no db container observed")
        }
        else {
            $digestList = Get-RestoreSmokeEvidenceValue $database "RepoDigests"
            $digests = @()
            if ($null -ne $digestList) {
                $digests = @(@($digestList) | Where-Object { [string]$_ -cmatch '^[^@\s]+@sha256:[0-9a-f]{64}$' })
            }
            if ($digests.Count -eq 0) {
                $digestReason = [string](Get-RestoreSmokeEvidenceValue $database "RepoDigestsReason")
                $suffix = if ([string]::IsNullOrWhiteSpace($digestReason)) { "" } else { ": $digestReason" }
                $reasons.Add("${label}: the db image $(Get-RestoreSmokeEvidenceValue $database 'ImageId') has no observed repository digest$suffix")
            }
        }

        $schemaPackages = Get-RestoreSmokeEvidenceValue $observation "EffectiveSchemaPackages"
        if ($null -eq $schemaPackages) {
            $reasons.Add("${label}: effective SCHEMA_PACKAGES was not observed")
        }
        elseif ([string]::IsNullOrWhiteSpace([string](Get-RestoreSmokeEvidenceValue $schemaPackages "Value"))) {
            $packageReason = [string](Get-RestoreSmokeEvidenceValue $schemaPackages "Reason")
            if ([string]::IsNullOrWhiteSpace($packageReason)) {
                $packageReason = "the value is blank"
            }
            $reasons.Add("${label}: effective SCHEMA_PACKAGES not observed: $packageReason")
        }
    }

    $packageList = Get-RestoreSmokeEvidenceValue $Provenance "Packages"
    $packages = @()
    if ($null -ne $packageList) {
        $packages = @($packageList)
    }
    if ($packages.Count -eq 0) {
        $reasons.Add("no package was built in this run")
    }
    foreach ($package in $packages) {
        if ((Get-RestoreSmokeEvidenceValue $package "Verified") -ne $true) {
            $reasons.Add("package ($(Get-RestoreSmokeEvidenceValue $package 'TemplateKind')) not verified: $(Get-RestoreSmokeEvidenceValue $package 'Reason')")
        }
    }

    # A leftover volume the preflight allowed must still be there, unchanged, when the run ends.
    $foreignStack = Get-RestoreSmokeEvidenceValue $Provenance "ForeignStack"
    $allowedList = Get-RestoreSmokeEvidenceValue (Get-RestoreSmokeEvidenceValue $foreignStack "LeftoverVolumes") "Allowed"
    $allowedVolumes = @()
    if ($null -ne $allowedList) {
        $allowedVolumes = @($allowedList | Where-Object { $null -ne $_ })
    }
    $stateList = Get-RestoreSmokeEvidenceValue $foreignStack "AllowedVolumesAtEnd"
    $endStates = @()
    if ($null -ne $stateList) {
        $endStates = @($stateList | Where-Object { $null -ne $_ })
    }
    foreach ($volume in $allowedVolumes) {
        $volumeName = [string](Get-RestoreSmokeEvidenceValue $volume "Name")
        $endState = @($endStates | Where-Object { [string](Get-RestoreSmokeEvidenceValue $_ "Name") -ceq $volumeName })
        if ($endState.Count -eq 0) {
            $reasons.Add("leftover volume '$volumeName' was allowed by the preflight, but no run-end check was recorded")
        }
        elseif ((Get-RestoreSmokeEvidenceValue $endState[0] "Preserved") -ne $true) {
            $reasons.Add("leftover volume '$volumeName' was allowed by the preflight, but was not shown preserved at run end: $(Get-RestoreSmokeEvidenceValue $endState[0] 'Reason')")
        }
    }

    if ([string]::IsNullOrWhiteSpace([string](Get-RestoreSmokeEvidenceValue $Provenance "SourceIdentity"))) {
        $reasons.Add("pre-backup SourceIdentity not captured: $(Get-RestoreSmokeEvidenceValue $Provenance 'SourceIdentityReason')")
    }

    return [pscustomobject]@{
        Final   = ($reasons.Count -eq 0)
        Reasons = @($reasons)
    }
}

Export-ModuleMember -Function `
    ConvertTo-RestoreSmokeLogSafeText, `
    Get-RestoreSmokeWrapperProfile, `
    Get-RestoreSmokeWrapperArgumentSet, `
    Resolve-RestoreSmokeStandardVersion, `
    Get-RestoreSmokeForeignStackInventory, `
    Format-RestoreSmokeForeignStackInventory, `
    Assert-RestoreSmokeNoForeignStack, `
    Get-RestoreSmokeEngineVolumeDefinition, `
    Get-RestoreSmokeLeftoverVolumeClassification, `
    Format-RestoreSmokeLeftoverVolumeClassification, `
    Get-RestoreSmokeAllowedVolumeState, `
    Get-RestoreSmokeSourceRevision, `
    Get-RestoreSmokeFileSha256, `
    Invoke-RestoreSmokeSchemaToolBuild, `
    Get-RestoreSmokeImageBuildPlan, `
    New-RestoreSmokeImageLedger, `
    Get-RestoreSmokeImageTagState, `
    Register-RestoreSmokeImageTagOwnership, `
    Invoke-RestoreSmokeImageBuild, `
    Remove-RestoreSmokeOwnedImageTag, `
    Write-RestoreSmokeImageEnvironmentFile, `
    Get-RestoreSmokeStackObservation, `
    Get-RestoreSmokeEffectiveSchemaPackageList, `
    Get-RestoreSmokePackageProvenance, `
    Resolve-RestoreSmokeApiEndpoint, `
    New-RestoreSmokeApiSession, `
    Reset-RestoreSmokeApiSession, `
    Select-RestoreSmokeDataStore, `
    Get-RestoreSmokeJsonArrayLength, `
    Test-RestoreSmokeApiRead, `
    Get-RestoreSmokeResultClassification
