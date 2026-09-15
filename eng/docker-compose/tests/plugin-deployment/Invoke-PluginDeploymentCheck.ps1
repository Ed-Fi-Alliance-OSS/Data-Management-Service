# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

<#
.SYNOPSIS
    Proves plugin loading end to end against a locally built DMS image.

.DESCRIPTION
    DESTRUCTIVE TO THE LOCAL STACK. This is not a sandboxed harness. It deploys and tears down under
    the ordinary local Compose project name, dms-local, and every teardown is a `-d -v`, so it
    removes that project's containers and deletes its named volumes. Any data in the local
    PostgreSQL, the Configuration Service database and the identity store is destroyed. There are
    four deployments, and each one tears down before it starts as well as after it ends, so an
    existing local stack is destroyed by the first teardown, before any assertion has run. Its ports
    are the ordinary local stack's too, so that stack must not be up when this starts. The
    scratch-workspace containment check below guards file deletions only; it has nothing to say
    about any of this. Do not run it against a local stack whose data you want.

    Everything except the word "stock". A later story runs the same mechanism against a pulled
    published image; this one builds the image from this worktree and proves the rest.

    In order:

    0. The launcher hook itself, driven through the real script: a compose file that does not exist
       and an empty list entry each fail before anything touches Docker.

    1. The image build no longer stamps AssemblyVersion or FileVersion, and nothing replaced it. The
       plugin contract inside the image carries the version src/plugins/Directory.Build.props
       declares rather than the version the build was given, which is the value the loader's
       newer-plugin-on-older-host preflight compares. Both expectations are read from the committed
       props at run time rather than written here, so neither rots. The assemblies are copied out and
       read as metadata; nothing in the image is executed.

    2. A plugin nobody in this repository has a project reference to is published against the two
       contracts as packed nupkgs, exactly as an outside implementer consumes them, and packed
       asset-only into a .nupkg of its own.

    3. Recipe 1: the committed eng/docker-compose/plugins-dms.yml, run unedited by the real setup
       path, with the plugin bind-mounted and allowlisted. Then a second, fresh deployment with the
       allowlist empty, which must still reach Ready and emit no inventory event. Absence is only
       evidence against a boot that reached the same state.

    4. Recipe 2: the committed eng/docker-compose/plugins-fetch-dms.yml, run unedited, fetching the
       package over HTTP from a digest-pinned static-file container in a test-owned overlay. Then a
       third, fresh deployment with a deliberately wrong digest, where the fetch must fail on the
       checksum comparison specifically and the DMS container must never start.

    Each deployment is torn down before the next begins and again on the way out, whether its
    assertions held or not, and the teardown is asserted rather than hoped for: they share a compose
    project name, so a survivor from the previous run would answer for the one under test. That
    project name and those ports are the ordinary local stack's, so a failure that left containers
    behind would leave them in the developer's way as well as the next deployment's. A failing
    deployment has its container states and logs written to the evidence directory first, because
    teardown removes what the evidence is about.

    Evidence is written as JSON under .ai-work/verification, which is excluded from the repository.

.PARAMETER DmsVersion
    The version to build the image with. Deliberately unlike the committed assembly versions, so
    that "the frontend did not take the version the build was given" is a distinguishable claim.

.PARAMETER WorkspaceRoot
    Where the scratch feed, the published plugin and the packed package are written. Defaults to a
    directory under .ai-work, which is excluded from the repository. Everything this script deletes
    is verified to sit under it first.
#>
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Read inside the functions below, which the rule does not follow.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'A verification harness with no interactive surface; every state change is inside its own scratch workspace or its own Compose project.')]
[CmdletBinding()]
param(
    [string]
    $DmsVersion = "9.9.9-pre.0.7",

    [string]
    $WorkspaceRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$composeRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $composeRoot)

if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $WorkspaceRoot = Join-Path $repositoryRoot ".ai-work/plugin-deployment"
}

New-Item -ItemType Directory -Path $WorkspaceRoot -Force | Out-Null

# Resolved once, and the only workspace path anything below reads. $WorkspaceRoot is not referenced
# past this line on purpose: a relative value would be written here but read after a Push-Location
# into eng/docker-compose, where it names a different directory or none at all.
#
# Every deletion below is checked against this path as well. Nothing here should be able to remove a
# path a caller supplied by accident, and "it is under a directory this script owns" is the only
# check that holds when the caller controls the parameter.
$workspaceFull = (Resolve-Path -LiteralPath $WorkspaceRoot).Path
$evidenceRoot = Join-Path $repositoryRoot ".ai-work/verification"
$composeProject = "dms-local"
$dmsContainer = "ed-fi-api"
$pluginName = "Acme.SampleValidator"
$fixtureProject = Join-Path $PSScriptRoot "$pluginName/$pluginName.csproj"

New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null

$script:results = [ordered]@{}
$script:fixture = $null
$script:package = $null
$script:probedImageId = $null

# Diagnostics go to the information stream rather than to the success stream, so that a function
# returning a value cannot hand its caller a mixture of the value and everything it printed.
function Write-Phase([string]$text) {
    Write-Information "" -InformationAction Continue
    Write-Information "=== $text ===" -InformationAction Continue
}

function Write-Detail([string]$text) {
    Write-Information "  $text" -InformationAction Continue
}

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) {
        throw "ASSERTION FAILED: $message"
    }

    Write-Detail "ok: $message"
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [scriptblock] $Command,
        [Parameter(Mandatory)] [string] $What
    )

    & $Command 2>&1 | ForEach-Object { Write-Information $_ -InformationAction Continue }

    if ($LASTEXITCODE -ne 0) {
        throw "$What failed with exit code $LASTEXITCODE"
    }
}

function Get-DeclaredAssemblyVersion([string]$propsPath) {
    $node = ([xml](Get-Content -Raw -LiteralPath $propsPath)).SelectSingleNode("//AssemblyVersion")

    if ($null -eq $node) {
        throw "No AssemblyVersion element in $propsPath"
    }

    return $node.InnerText
}

# Metadata only. GetAssemblyName reads the manifest without loading the assembly into this process
# and FileVersionInfo reads the version resource; neither executes anything from the image.
function Get-AssemblyIdentity([string]$path) {
    $name = [System.Reflection.AssemblyName]::GetAssemblyName($path)
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path)

    return [ordered]@{
        File            = [System.IO.Path]::GetFileName($path)
        AssemblyVersion = $name.Version.ToString()
        FileVersion     = $info.FileVersion
        ProductVersion  = $info.ProductVersion
    }
}

function Get-Sha256([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

# Recursive removal, confined to the scratch workspace by a containment check rather than by
# convention. The path is resolved first, so a link or a "..\" segment cannot walk out of it.
function Remove-ScratchTree([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        return
    }

    $full = (Resolve-Path -LiteralPath $path).Path
    $prefix = $workspaceFull.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    if (-not $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove '$full': it is not inside the scratch workspace '$workspaceFull'."
    }

    Remove-Item -LiteralPath $full -Recurse -Force
}

# ---------------------------------------------------------------------------------------------
# Deployment plumbing
# ---------------------------------------------------------------------------------------------

function New-DeploymentEnvironmentFile {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [hashtable] $Values
    )

    # The end-to-end lane's own environment file, read rather than copied into a plugin variant of
    # its own. A second committed copy would be a file every future change to this one has to be
    # remembered into, and forgetting would deploy this lane without the setting.
    $source = Join-Path $composeRoot ".env.e2e"
    $destination = Join-Path $workspaceFull "$Name.env"

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# Generated by Invoke-PluginDeploymentCheck.ps1 from .env.e2e. Do not edit; regenerate.")

    foreach ($line in Get-Content -LiteralPath $source) {
        $lines.Add($line)
    }

    $lines.Add("")
    $lines.Add("# Per-run values for this deployment.")

    # The image Phase 1 built from the current source and read the assembly versions out of.
    # local-dms.yml already reads this setting and falls back to ed-fi-api-local, which Compose builds
    # only when absent and otherwise reuses from whenever it was last built. Pinning here is what makes
    # the stamping proof and the deployment proof statements about one artifact rather than two.
    $lines.Add("DMS_DOCKER_IMAGE=local/ed-fi-api")

    foreach ($key in $Values.Keys) {
        $lines.Add("$key=$($Values[$key])")
    }

    Set-Content -LiteralPath $destination -Value $lines -Encoding utf8

    return $destination
}

# By compose labels rather than by a global name filter, so nothing outside this project's own
# services can be mistaken for one of them.
function Get-ComposeContainerName([string]$service) {
    $names = @(
        & docker ps -a `
            --filter "label=com.docker.compose.project=$composeProject" `
            --filter "label=com.docker.compose.service=$service" `
            --format "{{.Names}}"
    )

    return ($names | Select-Object -First 1)
}

# The raw start timestamp, read as text. ConvertFrom-Json turns an ISO-8601 string into a DateTime,
# and the zero value a never-started container carries then stops comparing equal to the string form,
# which is a difference that silently inverts this check.
function Get-ContainerStartedAtRaw([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) {
        return $null
    }

    $raw = & docker inspect $name --format '{{.State.StartedAt}}' 2>$null

    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    return ($raw -join "").Trim()
}

function Get-ContainerState([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) {
        return $null
    }

    $raw = & docker inspect $name --format '{{json .State}}' 2>$null

    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) {
        return $null
    }

    return $raw | ConvertFrom-Json
}

# Everything a failed deployment leaves behind, written out before anything is torn down, because
# teardown removes the containers the evidence lives in.
#
# Best effort throughout. A harness that could not collect its evidence still has to report the
# failure that sent it here rather than replace it with one of its own.
function Save-FailureEvidence([string]$Name) {
    try {
        $path = Join-Path $evidenceRoot "plugin-deployment-failure-$Name.log"
        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add("Deployment '$Name' failed at $([DateTimeOffset]::UtcNow.ToString('o')).")

        foreach ($service in @("dms", "fetch-plugins", "plugin-feed")) {
            $lines.Add("")
            $lines.Add("=== $service ===")

            $container = Get-ComposeContainerName $service

            if ([string]::IsNullOrWhiteSpace($container)) {
                $lines.Add("no container in compose project '$composeProject'")
                continue
            }

            $state = Get-ContainerState $container
            $lines.Add("container: $container")
            $lines.Add("status:    $(if ($null -ne $state) { $state.Status } else { 'unknown' })")
            $lines.Add("exit code: $(if ($null -ne $state) { $state.ExitCode } else { 'unknown' })")
            $lines.Add("--- docker logs ---")

            foreach ($line in @(& docker logs $container 2>&1)) {
                $lines.Add("$line")
            }
        }

        Set-Content -LiteralPath $path -Value $lines -Encoding utf8
        Write-Detail "wrote failure evidence to $path"
    }
    catch {
        Write-Detail "could not collect failure evidence: $($_.Exception.Message)"
    }
}

# Teardown is asserted, not hoped for. These deployments share one compose project name, so a
# container that survived the previous one would answer every question the next one asks.
function Remove-Deployment([string]$environmentFile) {
    Push-Location $composeRoot
    try {
        Invoke-Checked -What "bootstrap-local-dms.ps1 -d -v" -Command {
            & "$composeRoot/bootstrap-local-dms.ps1" -d -v -EnvironmentFile $environmentFile
        }
    }
    finally {
        Pop-Location
    }

    foreach ($service in @("dms", "fetch-plugins", "plugin-feed")) {
        $survivor = Get-ComposeContainerName $service

        if (-not [string]::IsNullOrWhiteSpace($survivor)) {
            throw "Teardown left the '$service' container '$survivor' in place; the next deployment would not be fresh."
        }
    }
}

function Start-Deployment([string]$environmentFile) {
    Push-Location $composeRoot
    try {
        Invoke-Checked -What "bootstrap-local-dms.ps1" -Command {
            & "$composeRoot/bootstrap-local-dms.ps1" -EnvironmentFile $environmentFile
        }
    }
    finally {
        Pop-Location
    }
}

# The startup status document DMS writes, read out of the running container. State reaches Ready only
# after every startup task has run, which is the difference between "the process is up" and "the host
# finished starting".
function Get-StartupStatus {
    $raw = & docker exec $dmsContainer cat /tmp/dms-startup-status.json 2>$null

    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) {
        return $null
    }

    return (($raw -join "`n") | ConvertFrom-Json)
}

function Wait-ForDmsReady([int]$timeoutSeconds = 300) {
    $deadline = [datetime]::UtcNow.AddSeconds($timeoutSeconds)

    while ([datetime]::UtcNow -lt $deadline) {
        $status = Get-StartupStatus

        if ($null -ne $status -and $status.State -eq "Ready") {
            return $status
        }

        Start-Sleep -Seconds 5
    }

    throw "DMS did not reach Ready within $timeoutSeconds seconds."
}

function Get-DmsLog {
    return ((& docker logs $dmsContainer 2>&1) -join "`n")
}

# The image an image reference currently resolves to, and the image a container actually ran. These
# are separate facts and the whole point of recording them: build-dms.ps1 DockerBuild tags
# local/ed-fi-api, while Compose builds ed-fi-api-local from the same Dockerfile through
# local-dms.yml. Without the ids, the assembly versions asserted in Phase 1 and the deployments below
# are claims about two artifacts nothing ties together.
function Get-ImageId([string]$reference) {
    $id = & docker image inspect $reference --format '{{.Id}}' 2>$null

    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($id)) {
        return $null
    }

    return ($id -join "").Trim()
}

function Get-ContainerImage([string]$container) {
    if ([string]::IsNullOrWhiteSpace($container)) {
        return $null
    }

    $raw = & docker inspect $container --format '{{.Config.Image}}|{{.Image}}' 2>$null

    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) {
        return $null
    }

    $parts = (($raw -join "").Trim() -split '\|', 2)

    return [ordered]@{ Reference = $parts[0]; ImageId = $parts[1] }
}

function Get-PluginMountIsReadOnly([string]$container) {
    $raw = & docker inspect $container --format '{{json .Mounts}}' 2>$null

    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) {
        return $null
    }

    $mounts = @(@($raw | ConvertFrom-Json) | Where-Object { $_.Destination -eq "/app/plugins" })

    if ($mounts.Count -eq 0) {
        return $null
    }

    return (-not $mounts[0].RW)
}

# The inventory event as the container wrote it. Serilog renders the structured properties into the
# console line; the structured shape itself is asserted in the integration tier, against Serilog's
# own LogEvent rather than against text.
function Get-InventoryLine([string]$log) {
    return @($log -split "`n" | Where-Object { $_ -match "Plugin inventory for " })
}

# ---------------------------------------------------------------------------------------------
# Phase 0: the launcher hook
# ---------------------------------------------------------------------------------------------

function Invoke-Launcher([string]$environmentFile) {
    Push-Location $composeRoot
    try {
        $output = & pwsh -NoProfile -File "$composeRoot/start-local-dms.ps1" -EnvironmentFile $environmentFile 2>&1
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
    }
    finally {
        Pop-Location
    }
}

function Invoke-LauncherHookProof {
    Write-Phase "Phase 0: DMS_PLUGINS_COMPOSE_FILES"

    # The default shape rather than -DbOnly. The compose file list, and therefore this hook, is built
    # only when the DMS service participates, so a database-only run would exercise nothing and would
    # pass for the wrong reason.
    $containersBefore = @(& docker ps -a --format "{{.Names}}")

    $envBadPath = New-DeploymentEnvironmentFile -Name "hook-missing-file" -Values @{
        DMS_PLUGINS_COMPOSE_FILES = "tests/plugin-deployment/does-not-exist.yml"
        DMS_PLUGINS_MOUNT_SOURCE  = $workspaceFull
    }
    $badPathRun = Invoke-Launcher $envBadPath
    Assert-True ($badPathRun.ExitCode -ne 0) "a plugin compose file that does not exist fails the launcher"
    Assert-True `
        ($badPathRun.Output -match "does not identify a compose file") `
        "the failure names the rule rather than surfacing a raw Docker error"
    Assert-True `
        (-not ($badPathRun.Output -match "Using plugin Docker Compose file")) `
        "no file was accepted before the missing one was rejected"

    $envEmptySegment = New-DeploymentEnvironmentFile -Name "hook-empty-segment" -Values @{
        DMS_PLUGINS_COMPOSE_FILES = "plugins-dms.yml;"
        DMS_PLUGINS_MOUNT_SOURCE  = $workspaceFull
    }
    $emptySegmentRun = Invoke-Launcher $envEmptySegment
    Assert-True ($emptySegmentRun.ExitCode -ne 0) "an empty entry in a non-empty list is refused"
    Assert-True `
        ($emptySegmentRun.Output -match "contains an empty entry") `
        "the refusal says what was wrong, so a stray separator is not silently dropped"

    $containersAfter = @(& docker ps -a --format "{{.Names}}")
    Assert-True `
        ($null -eq (Compare-Object $containersBefore $containersAfter)) `
        "neither rejection created or removed a container, so validation ran before any Docker mutation"

    $script:results.launcherHook = [ordered]@{
        missingFileExitCode   = $badPathRun.ExitCode
        emptySegmentExitCode  = $emptySegmentRun.ExitCode
        containerSetUnchanged = $true
    }
}

# ---------------------------------------------------------------------------------------------
# Phase 1: the image, and what it did not stamp
# ---------------------------------------------------------------------------------------------

function Invoke-ImageVersionProof {
    Write-Phase "Phase 1: image build and version stamping"

    $dmsDeclared = Get-DeclaredAssemblyVersion (Join-Path $repositoryRoot "src/dms/Directory.Build.props")
    $pluginsDeclared = Get-DeclaredAssemblyVersion (Join-Path $repositoryRoot "src/plugins/Directory.Build.props")

    Assert-True `
        ($DmsVersion -notlike "$dmsDeclared*") `
        "the build version '$DmsVersion' differs from the committed DMS assembly version '$dmsDeclared', so the two are distinguishable"

    # Always, with no way to skip it. The image this probes is also the image the deployments run,
    # so a switch that reused an earlier build would put a stale artifact under every assertion
    # below. The build is cached, so it costs little when nothing changed.
    Push-Location $repositoryRoot
    try {
        Invoke-Checked -What "build-dms.ps1 DockerBuild" -Command {
            & "$repositoryRoot/build-dms.ps1" DockerBuild -DMSVersion $DmsVersion
        }
    }
    finally {
        Pop-Location
    }

    # SetDMSAssemblyInfo is deliberately not called from DockerBuild, and this is the assertion that
    # proves it: that helper regenerates the tracked props into a different shape on every run.
    $propsStatus = & git -C $repositoryRoot status --porcelain -- "src/dms/Directory.Build.props"
    Assert-True `
        ([string]::IsNullOrWhiteSpace($propsStatus)) `
        "src/dms/Directory.Build.props is byte-identical after DockerBuild"

    $extractRoot = Join-Path $workspaceFull "image-assemblies"
    Remove-ScratchTree $extractRoot
    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null

    $container = "dms-plugin-deployment-version-probe"
    & docker rm -f $container 2>$null | Out-Null
    Invoke-Checked -What "docker create" -Command { & docker create --name $container local/ed-fi-api | Out-Null }

    try {
        foreach ($assembly in @("EdFi.Api.Plugins.dll", "EdFi.DataManagementService.Frontend.AspNetCore.dll")) {
            Invoke-Checked -What "docker cp $assembly" -Command {
                & docker cp "${container}:/app/$assembly" $extractRoot | Out-Null
            }
        }
    }
    finally {
        & docker rm -f $container 2>$null | Out-Null
    }

    $contract = Get-AssemblyIdentity (Join-Path $extractRoot "EdFi.Api.Plugins.dll")
    $frontend = Get-AssemblyIdentity (Join-Path $extractRoot "EdFi.DataManagementService.Frontend.AspNetCore.dll")

    # Scoped to the plugin contract deliberately. EdFi.DataManagementService.CustomValidation declares
    # no version of its own today and inherits src/dms/Directory.Build.props; asserting that it
    # "carries the version its own project declares" would assert against a value no project writes.
    # The story that gives it its own declaration extends this assertion in the same pass.
    Assert-True `
        ($contract.AssemblyVersion -eq "$pluginsDeclared.0") `
        "EdFi.Api.Plugins carries AssemblyVersion $($contract.AssemblyVersion), the version src/plugins/Directory.Build.props declares"
    Assert-True `
        ($contract.FileVersion -eq $pluginsDeclared) `
        "EdFi.Api.Plugins carries FileVersion $($contract.FileVersion)"
    Assert-True `
        ($frontend.AssemblyVersion -eq "$dmsDeclared.0") `
        "the frontend carries AssemblyVersion $($frontend.AssemblyVersion), the committed value"
    Assert-True `
        ($frontend.FileVersion -eq $dmsDeclared) `
        "the frontend carries FileVersion $($frontend.FileVersion), the committed value"
    Assert-True `
        ($frontend.ProductVersion -eq $DmsVersion) `
        "Version and InformationalVersion still record what the image was built for: $($frontend.ProductVersion)"

    $probedImageId = Get-ImageId "local/ed-fi-api"
    Assert-True `
        (-not [string]::IsNullOrWhiteSpace($probedImageId)) `
        "the assemblies above were read out of image local/ed-fi-api $probedImageId"

    # The tag local-dms.yml would select on its own, recorded rather than used. Compose builds it
    # only when it is absent and silently reuses it when it is not, so a machine that has run this
    # before carries one built from whatever the source said then. The deployments below are pinned
    # away from it through DMS_DOCKER_IMAGE, and this is the value that makes the pin checkable: if
    # it is present and differs from the probe, a deployment matching the probe cannot have taken it.
    $defaultTagImageId = Get-ImageId "ed-fi-api-local"

    if ([string]::IsNullOrWhiteSpace($defaultTagImageId)) {
        Write-Detail "note: ed-fi-api-local is absent on this host, so there is no stale tag to avoid"
    }
    else {
        Write-Detail "note: ed-fi-api-local is present at $defaultTagImageId and is not what the deployments will run"
    }

    $script:results.imageVersionProof = [ordered]@{
        dmsVersionArgument       = $DmsVersion
        committedDmsVersion      = $dmsDeclared
        committedContractVersion = $pluginsDeclared
        probedImageReference     = "local/ed-fi-api"
        probedImageId            = $probedImageId
        defaultTagImageId        = $defaultTagImageId
        contract                 = $contract
        frontend                 = $frontend
        propsUnchanged           = $true
    }
    $script:probedImageId = $probedImageId
}

# ---------------------------------------------------------------------------------------------
# Phase 2: the plugin, consumed as packages
# ---------------------------------------------------------------------------------------------

function Invoke-FixtureBuild {
    Write-Phase "Phase 2: pack the contracts and publish the fixture plugin"

    Import-Module (Join-Path $repositoryRoot "package-helpers.psm1") -Force
    $contractVersion = Get-PluginsContractVersion
    $customValidationVersion = "0.0.0-plugin-deployment"

    Push-Location $repositoryRoot
    try {
        Invoke-Checked -What "build-dms.ps1 Build" -Command {
            & "$repositoryRoot/build-dms.ps1" Build -Configuration Release
        }
        Invoke-Checked -What "build-dms.ps1 Package -PackageTarget Plugins" -Command {
            & "$repositoryRoot/build-dms.ps1" Package -PackageTarget Plugins -Configuration Release
        }
        Invoke-Checked -What "build-dms.ps1 Package -PackageTarget CustomValidation" -Command {
            & "$repositoryRoot/build-dms.ps1" Package -PackageTarget CustomValidation -DMSVersion $customValidationVersion -Configuration Release
        }
    }
    finally {
        Pop-Location
    }

    $publishRoot = Join-Path $workspaceFull "plugins"
    $publishTarget = Join-Path $publishRoot $pluginName
    Remove-ScratchTree $publishRoot
    New-Item -ItemType Directory -Path $publishTarget -Force | Out-Null

    # A throwaway global-packages folder, for the reason the contract's own scratch consumer records:
    # that folder is consulted before any source, so an already-extracted package of the same version
    # would silently satisfy this restore and validate old bits.
    $nugetCache = Join-Path $workspaceFull "nuget-cache"
    Remove-ScratchTree $nugetCache
    New-Item -ItemType Directory -Path $nugetCache -Force | Out-Null

    $nuGetPackagesWasSet = Test-Path -LiteralPath "Env:NUGET_PACKAGES"
    $previousNuGetPackages = if ($nuGetPackagesWasSet) { $env:NUGET_PACKAGES } else { $null }

    try {
        $env:NUGET_PACKAGES = $nugetCache

        Invoke-Checked -What "dotnet publish $pluginName" -Command {
            & dotnet publish $fixtureProject `
                --configuration Release `
                --no-self-contained `
                --output $publishTarget `
                -p:PluginsPackageVersion=$contractVersion `
                -p:CustomValidationPackageVersion=$customValidationVersion `
                --nologo
        }
    }
    finally {
        if ($nuGetPackagesWasSet) {
            $env:NUGET_PACKAGES = $previousNuGetPackages
        }
        elseif (Test-Path -LiteralPath "Env:NUGET_PACKAGES") {
            Remove-Item -LiteralPath "Env:NUGET_PACKAGES"
        }
    }

    Assert-True `
        (Test-Path -LiteralPath (Join-Path $publishTarget "$pluginName.dll")) `
        "the fixture published its entry assembly"
    Assert-True `
        (Test-Path -LiteralPath (Join-Path $publishTarget "$pluginName.deps.json")) `
        "the fixture published a dependency manifest"
    Assert-True `
        (Test-Path -LiteralPath (Join-Path $publishTarget "EdFi.DataManagementService.CustomValidation.dll")) `
        "the fixture published the custom validation contract it restored from the packed nupkg"
    Assert-True `
        (-not (Test-Path -LiteralPath (Join-Path $publishTarget "System.Private.CoreLib.dll"))) `
        "the publish is framework-dependent, so it carries no runtime pack"

    # Digests over the published files themselves. These are what the inventory's per-file rows have
    # to agree with, and they are computed here rather than read back out of the container.
    $digests = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $publishTarget -File) {
        $digests[$file.Name] = Get-Sha256 $file.FullName
    }

    $script:fixture = [ordered]@{
        pluginName              = $pluginName
        contractVersion         = $contractVersion
        customValidationVersion = $customValidationVersion
        publishedDirectory      = $publishTarget
        publishRoot             = $publishRoot
        publishedFileDigests    = $digests
        entryAssemblyVersion    = (Get-AssemblyIdentity (Join-Path $publishTarget "$pluginName.dll")).AssemblyVersion
    }
    $script:results.fixture = $script:fixture
}

# The asset-only package Recipe 2 fetches: the published directory under
# contentFiles/any/any/<PluginName>/, with a nuspec and the content-types part that make it a real
# package rather than a renamed zip. No lib/ or ref/ entries; nothing on the deploy path restores it.
function New-PluginPackage {
    param(
        [Parameter(Mandatory)] [string] $Destination,
        [Parameter(Mandatory)] [string] $Version
    )

    Write-Phase "Phase 2b: pack the plugin asset-only"

    $stage = Join-Path $workspaceFull "package-stage"
    Remove-ScratchTree $stage
    $contentRoot = Join-Path $stage "contentFiles/any/any/$pluginName"
    New-Item -ItemType Directory -Path $contentRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $script:fixture.publishedDirectory "*") -Destination $contentRoot -Recurse -Force

    $lowerId = $pluginName.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $stage "$pluginName.nuspec") -Encoding utf8 -Value @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>$pluginName</id>
    <version>$Version</version>
    <authors>Ed-Fi Alliance, LLC and contributors</authors>
    <description>Test fixture plugin for the DMS plugin deployment check. Asset-only.</description>
  </metadata>
</package>
"@
    Set-Content -LiteralPath (Join-Path $stage "[Content_Types].xml") -Encoding utf8 -Value @"
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="dll" ContentType="application/octet-stream" />
  <Default Extension="json" ContentType="application/json" />
  <Default Extension="pdb" ContentType="application/octet-stream" />
  <Default Extension="xml" ContentType="application/xml" />
  <Default Extension="nuspec" ContentType="application/octet-stream" />
</Types>
"@

    Remove-ScratchTree $Destination
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $packagePath = Join-Path $Destination "$lowerId.$Version.nupkg"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $packagePath)

    Assert-True (Test-Path -LiteralPath $packagePath) "the plugin packed to $packagePath"

    # Computed over the package just written, which is what makes the run repeatable even though a
    # package is not byte-reproducible across packs. This is the deployment's side of the recipe: an
    # operator pins the digest of the package they downloaded.
    $script:package = [ordered]@{
        path     = $packagePath
        sha256   = (Get-Sha256 $packagePath)
        version  = $Version
        fileName = [System.IO.Path]::GetFileName($packagePath)
    }
    $script:results.package = $script:package
}

# ---------------------------------------------------------------------------------------------
# The deployments
# ---------------------------------------------------------------------------------------------

# The two files whose digests the inventory must agree with: the plugin's own entry assembly, and a
# contract assembly it declared and shipped. The dependency manifest is deliberately not among them,
# because the inventory lists the assets that manifest declares and the manifest is not one of them.
function Assert-InventoryDigest([string]$inventoryLine) {
    foreach ($file in @("$pluginName.dll", "EdFi.DataManagementService.CustomValidation.dll")) {
        Assert-True `
            ($inventoryLine -match [regex]::Escape($script:fixture.publishedFileDigests[$file])) `
            "the inventory event carries the SHA-256 the harness computed over the published $file"
    }
}

# Every deployment in this harness, including the one that expects Compose to refuse. It owns the
# environment file, the pre-run teardown, and start-to-teardown as one shape; what varies is the
# body, which is Invoke-DeployedDmsCheck unless the caller supplies its own through -Scenario. The
# scenario receives the environment file and returns the record the caller keeps as evidence.
function Invoke-PluginDeployment {
    param(
        [Parameter(Mandatory)] [string] $Title,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [hashtable] $EnvironmentValues,
        [int] $ExpectedInventoryEvents = 0,
        [scriptblock] $Scenario
    )

    Write-Phase $Title

    $environmentFile = New-DeploymentEnvironmentFile -Name $Name -Values $EnvironmentValues
    Remove-Deployment $environmentFile

    $record = $null
    $failure = $null

    # Started inside a try so that a startup failure, a readiness timeout or a failed assertion
    # tears the stack down on the way out. This harness uses the ordinary local project name and
    # ports, so containers left behind are not just untidy: they are the local stack.
    try {
        $record = if ($null -eq $Scenario) {
            Invoke-DeployedDmsCheck -ExpectedInventoryEvents $ExpectedInventoryEvents `
                -EnvironmentFile $environmentFile
        }
        else {
            & $Scenario $environmentFile
        }
    }
    catch {
        $failure = $_
        Save-FailureEvidence -Name $Name
    }
    finally {
        try {
            Remove-Deployment $environmentFile
        }
        catch {
            # A teardown failure is its own fatal when the deployment succeeded, and never a reason
            # to replace the failure that got here with this one.
            if ($null -eq $failure) {
                throw
            }

            Write-Detail "teardown after the failure above also failed: $($_.Exception.Message)"
        }
    }

    if ($null -ne $failure) {
        throw $failure
    }

    return $record
}

# The body of one deployment: start it, and assert everything that has to be true of the running
# stack. Separated from its caller only so that the caller owns start-to-teardown as one shape.
function Invoke-DeployedDmsCheck {
    param(
        [Parameter(Mandatory)] [int] $ExpectedInventoryEvents,
        [Parameter(Mandatory)] [string] $EnvironmentFile
    )

    Start-Deployment $EnvironmentFile

    $status = Wait-ForDmsReady
    Assert-True ($status.State -eq "Ready") "the startup status document reports State=Ready"

    # Wrapped at the call site: PowerShell unwraps a one-element array on return, so a single
    # inventory event would arrive here as a bare string and .Count would not exist on it.
    $inventory = @(Get-InventoryLine (Get-DmsLog))
    Assert-True `
        ($inventory.Count -eq $ExpectedInventoryEvents) `
        "$($inventory.Count) plugin inventory event(s) on a host that reached Ready, expected $ExpectedInventoryEvents"

    Assert-True ((Get-PluginMountIsReadOnly $dmsContainer) -eq $true) "/app/plugins is mounted read-only"

    $state = Get-ContainerState $dmsContainer

    # Which image actually served this deployment. Compose builds ed-fi-api-local from the same
    # Dockerfile that DockerBuild tags local/ed-fi-api, so the two ids are recorded side by side and
    # the reader can see for themselves whether they are the same artifact rather than take it on
    # trust. They are routinely different, because the two builds happen at different moments.
    $image = Get-ContainerImage $dmsContainer
    Assert-True `
        ($null -ne $image -and -not [string]::IsNullOrWhiteSpace($image.ImageId)) `
        "the DMS container ran image $($image.Reference) $($image.ImageId)"

    # The gate, not an observation. The probed id moves whenever the source does, so an image built
    # at some earlier candidate cannot satisfy this, and neither can the ed-fi-api-local tag Compose
    # would otherwise have selected.
    Assert-True `
        ($image.ImageId -eq $script:probedImageId) `
        ("the deployment ran the image Phase 1 built and verified: expected " +
            "$($script:probedImageId), actual $($image.ImageId) from $($image.Reference)")

    $record = [ordered]@{
        readyPhase           = $status.Phase
        readyState           = $status.State
        dmsStartedAt         = $state.StartedAt
        dmsImageReference    = $image.Reference
        dmsImageId           = $image.ImageId
        dmsImageMatchesProbe = ($image.ImageId -eq $script:probedImageId)
        inventoryEvents      = $inventory.Count
        pluginMountReadOnly  = $true
        inventoryLine        = ""
    }

    if ($ExpectedInventoryEvents -gt 0) {
        Assert-True ($inventory[0] -match [regex]::Escape($pluginName)) "the inventory event names $pluginName"
        Assert-True `
            ($inventory[0] -match [regex]::Escape($script:fixture.entryAssemblyVersion)) `
            "the inventory event carries the fixture's assembly version $($script:fixture.entryAssemblyVersion)"
        Assert-InventoryDigest $inventory[0]
        $record.inventoryLine = $inventory[0]
    }

    return $record
}

# The wrong-digest deployment's body, in the shape Invoke-PluginDeployment's -Scenario expects: start
# it, expect Compose to refuse, assert the refusal was the checksum comparison and nothing
# incidental, and return the record. Defined here because a script defines a function when the
# definition executes, which has to be before the call.
function Invoke-WrongDigestCheck([string]$environmentFile) {
    $deploymentCFailed = $false

    try {
        Start-Deployment $environmentFile
    }
    catch {
        $deploymentCFailed = $true
        Write-Detail "the deployment refused to come up, as intended: $($_.Exception.Message)"
    }

    $fetchContainer = Get-ComposeContainerName "fetch-plugins"
    $fetchState = Get-ContainerState $fetchContainer
    $fetchLog = if ([string]::IsNullOrWhiteSpace($fetchContainer)) { "" } else { ((& docker logs $fetchContainer 2>&1) -join "`n") }
    $dmsContainerC = Get-ComposeContainerName "dms"
    $dmsState = Get-ContainerState $dmsContainerC
    $dmsStartedAtRaw = Get-ContainerStartedAtRaw $dmsContainerC

    Assert-True $deploymentCFailed "the deployment failed rather than coming up"
    Assert-True ($null -ne $fetchState) "the fetch service ran in this compose project"
    Assert-True ($fetchState.ExitCode -ne 0) "the fetch step exited non-zero (exit code $($fetchState.ExitCode))"

    # The failure has to be the checksum and nothing incidental: an unreachable feed, or a script
    # that fell over before it downloaded anything, would also exit non-zero and would prove nothing
    # at all about digest verification.
    Assert-True `
        ($fetchLog -match "did NOT match" -or $fetchLog -match "FAILED") `
        "the fetch step failed on the checksum comparison specifically"

    # StartedAt rather than the absence of a log line. A container that started and then died would
    # carry a start time; this one was never started at all, which is what
    # service_completed_successfully buys.
    $dmsNeverStarted = $null -eq $dmsState -or $dmsStartedAtRaw -eq "0001-01-01T00:00:00Z"
    Assert-True `
        $dmsNeverStarted `
        "the DMS container never started (status '$(if ($null -ne $dmsState) { $dmsState.Status } else { 'absent' })', StartedAt '$dmsStartedAtRaw')"

    $dmsImageC = Get-ContainerImage $dmsContainerC

    return [ordered]@{
        recipe            = "2"
        dmsImageReference = if ($null -ne $dmsImageC) { $dmsImageC.Reference } else { $null }
        packageSha256     = ("0" * 64)
        fetchExitCode     = $fetchState.ExitCode
        fetchLogExcerpt   = (($fetchLog -split "`n" | Select-Object -Last 5) -join " | ")
        dmsState          = if ($null -ne $dmsState) { $dmsState.Status } else { "absent" }
        dmsStartedAt      = $dmsStartedAtRaw
        dmsNeverStarted   = $dmsNeverStarted
    }
}

# ---------------------------------------------------------------------------------------------

Write-Phase "Prerequisites"
Invoke-Checked -What "docker version" -Command { & docker version --format '{{.Server.Version}}' }
Write-Detail "workspace: $workspaceFull"
Write-Detail "evidence:  $evidenceRoot"

Invoke-LauncherHookProof
Invoke-ImageVersionProof
Invoke-FixtureBuild

$feedRoot = Join-Path $workspaceFull "feed"
Remove-ScratchTree $feedRoot
$packageVersion = "1.0.0"
$packageDirectory = Join-Path $feedRoot "$($pluginName.ToLowerInvariant())/$packageVersion"
New-PluginPackage -Destination $packageDirectory -Version $packageVersion

$acquisitionOverlay = "plugins-dms.yml"
$fetchOverlay = "plugins-fetch-dms.yml"
$allowedOverlay = "tests/plugin-deployment/plugins-allowed-dms.yml"
$feedOverlay = "tests/plugin-deployment/plugins-feed-dms.yml"
$packageUrl = "http://plugin-feed:8080/$($pluginName.ToLowerInvariant())/$packageVersion/$($script:package.fileName)"

$script:results.deploymentA = Invoke-PluginDeployment `
    -Title "Deployment A: Recipe 1, plugin bind-mounted and allowlisted" `
    -Name "recipe1-allowed" `
    -ExpectedInventoryEvents 1 `
    -EnvironmentValues @{
    DMS_PLUGINS_COMPOSE_FILES = "$acquisitionOverlay;$allowedOverlay"
    DMS_PLUGINS_MOUNT_SOURCE  = $script:fixture.publishRoot
    DMS_PLUGINS_ALLOWED       = $pluginName
}

$script:results.deploymentANegative = Invoke-PluginDeployment `
    -Title "Deployment A-negative: same mount, empty allowlist, fresh deployment" `
    -Name "recipe1-disabled" `
    -ExpectedInventoryEvents 0 `
    -EnvironmentValues @{
    DMS_PLUGINS_COMPOSE_FILES = "$acquisitionOverlay;$allowedOverlay"
    DMS_PLUGINS_MOUNT_SOURCE  = $script:fixture.publishRoot
    DMS_PLUGINS_ALLOWED       = ""
}

$script:results.deploymentB = Invoke-PluginDeployment `
    -Title "Deployment B: Recipe 2, package fetched, digest verified, extracted" `
    -Name "recipe2-good" `
    -ExpectedInventoryEvents 1 `
    -EnvironmentValues @{
    DMS_PLUGINS_COMPOSE_FILES = "$fetchOverlay;$feedOverlay;$allowedOverlay"
    PLUGIN_FEED_SOURCE        = $feedRoot
    PLUGIN_PACKAGE_URL        = $packageUrl
    PLUGIN_PACKAGE_SHA256     = $script:package.sha256
    PLUGIN_NAME               = $pluginName
    DMS_PLUGINS_ALLOWED       = $pluginName
}

# -------------------------------------------------------------------------------------------------
# Deployment C: Recipe 2 with a digest that does not match
# -------------------------------------------------------------------------------------------------

$script:results.deploymentC = Invoke-PluginDeployment `
    -Title "Deployment C: Recipe 2, deliberately wrong digest, fresh deployment" `
    -Name "recipe2-baddigest" `
    -Scenario { param($environmentFile) Invoke-WrongDigestCheck $environmentFile } `
    -EnvironmentValues @{
    DMS_PLUGINS_COMPOSE_FILES = "$fetchOverlay;$feedOverlay;$allowedOverlay"
    PLUGIN_FEED_SOURCE        = $feedRoot
    PLUGIN_PACKAGE_URL        = $packageUrl
    PLUGIN_PACKAGE_SHA256     = ("0" * 64)
    PLUGIN_NAME               = $pluginName
    DMS_PLUGINS_ALLOWED       = $pluginName
}

# -------------------------------------------------------------------------------------------------

Write-Phase "Evidence"
$script:results.head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
$script:results.completedUtc = [DateTimeOffset]::UtcNow.ToString("o")
$evidencePath = Join-Path $evidenceRoot "plugin-deployment.json"
$script:results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidencePath -Encoding utf8
Write-Detail "wrote $evidencePath"
Write-Information "" -InformationAction Continue
Write-Information "Plugin deployment check passed." -InformationAction Continue
