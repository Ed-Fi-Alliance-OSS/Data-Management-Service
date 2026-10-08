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
    container name, the image reference it was created from, and its image ID (db adds repo digests).
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
        }
        if ($fields[2] -eq "db") {
            $global:LASTEXITCODE = 0
            $digests = @(docker image inspect $observation.ImageId --format '{{join .RepoDigests ","}}' 2>&1)
            if ($LASTEXITCODE -eq 0 -and $digests.Count -gt 0) {
                $observation.RepoDigests = @(([string]$digests[0]).Split(",") | Where-Object { $_ } | ForEach-Object { ConvertTo-RestoreSmokeLogSafeText $_ })
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
    foreach ($line in Get-Content -LiteralPath $DerivedEnvironmentFile) {
        if ($line -match '^\s*SCHEMA_PACKAGES\s*=\s*(.*)$') {
            return [pscustomobject]@{ Source = $DerivedEnvironmentFile; Value = $matches[1].Trim(); Reason = $null }
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

function Get-RestoreSmokeResultClassification {
    <#
    .SYNOPSIS
    Decides whether a run's evidence is final. Every unmet condition is a reason; any reason makes
    the result non-final. A missing observation is never treated as verified.
    #>
    param(
        [Parameter(Mandatory)]
        [System.Collections.IDictionary]$Provenance
    )

    $reasons = [System.Collections.Generic.List[string]]::new()

    if ($Provenance.ExploratoryPackage) {
        $reasons.Add("-ExploratoryPackage marks this run exploratory")
    }

    foreach ($point in @("SourceAtStart", "SourceAtEnd")) {
        $source = $Provenance[$point]
        if ($null -eq $source) {
            $reasons.Add("$point not captured")
        }
        elseif (-not $source.Observed) {
            $reasons.Add("$point not observed: $($source.Reason)")
        }
        elseif (-not $source.Clean) {
            $reasons.Add("$point worktree is dirty ($($source.Porcelain.Count) porcelain entries)")
        }
    }

    $schemaTools = $Provenance.SchemaTools
    if ($null -eq $schemaTools) {
        $reasons.Add("SchemaTools was not built in this run")
    }
    elseif (-not $schemaTools.Verified) {
        $reasons.Add("SchemaTools not verified: $($schemaTools.Reason)")
    }
    elseif ($schemaTools.Sha256AtEnd -cne $schemaTools.Sha256AtBuild) {
        $reasons.Add("the SchemaTools executable changed during the run (or was not re-hashed at the end)")
    }

    $images = $Provenance.Images
    if ($null -eq $images) {
        $reasons.Add("images were not built in this run")
    }
    else {
        foreach ($key in @("Dms", "Config")) {
            if ($null -eq $images.$key -or -not $images.$key.Verified -or [string]::IsNullOrWhiteSpace($images.$key.ImageId)) {
                $reason = if ($null -ne $images.$key) { $images.$key.Reason } else { "missing" }
                $reasons.Add("$key image not verified as built in this run: $reason")
            }
        }
    }

    $observations = @($Provenance.StackObservations)
    if ($observations.Count -eq 0) {
        $reasons.Add("no started stack was observed, so the images actually used are unknown")
    }
    elseif ($null -ne $images) {
        foreach ($observation in $observations) {
            foreach ($pair in @(@{ Service = "dms"; Key = "Dms" }, @{ Service = "config"; Key = "Config" })) {
                $observed = if ($observation.Services.Contains($pair.Service)) { $observation.Services[$pair.Service] } else { $null }
                $built = $images.($pair.Key)
                if ($null -eq $observed) {
                    $reasons.Add("$($observation.Label): no $($pair.Service) container observed")
                }
                elseif ($null -eq $built -or -not $built.Verified -or $observed.ImageId -cne $built.ImageId) {
                    $builtId = if ($null -ne $built -and $built.Verified) { $built.ImageId } else { "none verified" }
                    $reasons.Add("$($observation.Label): $($pair.Service) runs image $($observed.ImageId), not the in-run build ($builtId)")
                }
            }
            if (-not $observation.Services.Contains("db")) {
                $reasons.Add("$($observation.Label): no db container observed")
            }
        }
    }

    $packages = @($Provenance.Packages)
    if ($packages.Count -eq 0) {
        $reasons.Add("no package was built in this run")
    }
    foreach ($package in $packages) {
        if (-not $package.Verified) {
            $reasons.Add("package ($($package.TemplateKind)) not verified: $($package.Reason)")
        }
    }

    if ([string]::IsNullOrWhiteSpace([string]$Provenance.SourceIdentity)) {
        $reasons.Add("pre-backup SourceIdentity not captured: $($Provenance.SourceIdentityReason)")
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
    Get-RestoreSmokeResultClassification
