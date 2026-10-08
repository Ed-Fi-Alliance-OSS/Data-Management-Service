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

$script:LocalDmsImage = "local/ed-fi-api"
$script:LocalConfigImage = "local/ed-fi-api-configuration-service"
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

function Invoke-RestoreSmokeImageBuild {
    <#
    .SYNOPSIS
    Builds the DMS and CMS images from the worktree with the repository build scripts and records
    each command, exit code, image reference, and resulting image ID.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$RepoRoot
    )

    $images = [ordered]@{}
    foreach ($entry in @(
            @{ Key = "Dms"; Script = "build-dms.ps1"; Image = $script:LocalDmsImage },
            @{ Key = "Config"; Script = "build-config.ps1"; Image = $script:LocalConfigImage }
        )) {
        $scriptPath = Join-Path $RepoRoot $entry.Script
        $buildRecord = [ordered]@{
            Command  = "pwsh -NoProfile -File `"$scriptPath`" DockerBuild"
            ExitCode = $null
            Image    = $entry.Image
            ImageId  = $null
            Reason   = $null
        }

        Push-Location $RepoRoot
        try {
            $global:LASTEXITCODE = 0
            pwsh -NoProfile -File $scriptPath DockerBuild | Out-Host
            $buildRecord.ExitCode = $LASTEXITCODE
        }
        finally {
            Pop-Location
        }

        if ($buildRecord.ExitCode -ne 0) {
            $buildRecord.Reason = "$($entry.Script) DockerBuild exited $($buildRecord.ExitCode)"
        }
        else {
            $global:LASTEXITCODE = 0
            $imageId = @(docker image inspect $entry.Image --format '{{.Id}}' 2>&1)
            if ($LASTEXITCODE -eq 0 -and $imageId.Count -gt 0) {
                $buildRecord.ImageId = ConvertTo-RestoreSmokeLogSafeText ([string]$imageId[0])
            }
            else {
                $buildRecord.Reason = "docker image inspect $($entry.Image) failed after the build"
            }
        }
        $images[$entry.Key] = [pscustomobject]$buildRecord
    }

    return [pscustomobject]$images
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
            if ($null -eq $images.$key -or [string]::IsNullOrWhiteSpace($images.$key.ImageId)) {
                $reason = if ($null -ne $images.$key) { $images.$key.Reason } else { "missing" }
                $reasons.Add("$key image not built in this run: $reason")
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
                elseif ($null -eq $built -or $observed.ImageId -cne $built.ImageId) {
                    $reasons.Add("$($observation.Label): $($pair.Service) runs image $($observed.ImageId), not the in-run build")
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
    Invoke-RestoreSmokeImageBuild, `
    Write-RestoreSmokeImageEnvironmentFile, `
    Get-RestoreSmokeStackObservation, `
    Get-RestoreSmokeEffectiveSchemaPackageList, `
    Get-RestoreSmokePackageProvenance, `
    Get-RestoreSmokeResultClassification
