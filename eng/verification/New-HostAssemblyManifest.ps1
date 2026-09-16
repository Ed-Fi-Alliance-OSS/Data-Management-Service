# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Emits the host assembly manifest for a released Ed-Fi API image.

.DESCRIPTION
    A plugin is compiled against the contract packages, but at run time it is served every assembly
    the host carries, because assembly resolution is host-first. The host's whole assembly closure is
    therefore part of what a plugin has to be compatible with, and no contract package announces it.
    This emits that closure as a release asset so an implementer can read it before they deploy.

    The image inspected is the released one, the one src/dms/Nuget.Dockerfile produces, and never the
    one src/dms/Dockerfile builds for development. The two pin different runtime base images, so a
    manifest generated from the local image would state a shared-framework section no released image
    carries.

    Nothing is read from a .deps.json. A framework-dependent publish records no shared-framework
    assembly in that file at all, so a manifest built from it would omit Microsoft.Extensions.
    Configuration.Abstractions and Microsoft.Extensions.DependencyInjection.Abstractions, the two an
    implementer most needs. Everything here comes from the files in the image.

    The sweep is the top level of /app and nothing below it. /app/ApiSchemaDownloader/ is a second
    application the entrypoint script invokes as its own process, so its assemblies are never in the
    DMS process and a plugin can never be served one; listing them would tell an implementer they
    were part of the compatibility surface. /app/runtimes/ is excluded on a different ground: those
    RID-specific managed assets repeat the simple name and version of the library's own top-level
    entry, which the sweep already lists, so including them would repeat one identity several times.

    SUPPORTED INPUT, AND WHAT IS REFUSED

    This is a release tool for one shipped artifact, not a reimplementation of the runtime's
    framework resolution. It supports exactly the shape the released image has and refuses anything
    else by name rather than guessing:

      - the application must be framework-dependent: runtimeOptions carries "framework" or
        "frameworks" and no "includedFrameworks";
      - neither runtimeOptions nor any framework reference may carry rollForward, applyPatches, or
        the legacy rollForwardOnNoCandidateFx;
      - the image environment must carry no DOTNET_ROLL_FORWARD, DOTNET_ROLL_FORWARD_ON_NO_CANDIDATE_FX
        or DOTNET_ROLL_FORWARD_TO_PRERELEASE;
      - for each named framework, the installed directories must include at least one whose major and
        minor equal the requested major and minor and whose version is not below the requested
        version. The highest such patch is selected.

    That is latestPatch confined to one major.minor, which is a strict subset of the runtime's own
    default policy. It can never select an 11.x directory for a 10.0 application and never crosses a
    minor boundary. A named framework with no directory, with no directory satisfying the floor, or
    with a directory name that does not parse as a version is a refusal, not a guess.

    Each selected framework's own runtimeconfig.json is then read, because a shared framework can
    declare a dependency on a lower one. Its rollForward, when present, must be LatestPatch, and any
    framework it names must resolve, under the same policy, to the version already selected. That is
    what stops a framework dependency changing the answer silently.

.EXAMPLE
    ./New-HostAssemblyManifest.ps1 -Image edfialliance/ed-fi-api:dms-pre-1.2.3

.EXAMPLE
    ./New-HostAssemblyManifest.ps1 -ExtractedRoot ./staged -OutputPath ./host-assembly-manifest.md
#>
[CmdletBinding(DefaultParameterSetName = "FromImage")]
[OutputType([string])]
param(
    # The released image to inspect. It is pulled, resolved to one immutable digest, and every
    # subsequent inspection uses that digest, so a tag republished mid-run cannot make one manifest
    # describe two images.
    [Parameter(Mandatory, ParameterSetName = "FromImage")]
    [string]
    $Image,

    # A directory already laid out as this script's extraction stage: <root>/app holding the
    # application's publish output, and <root>/shared/<Framework>/<version>/ holding each shared
    # framework. Every scanning and selection rule is driven through here by the tests, so none of
    # them needs Docker to be exercised.
    [Parameter(Mandatory, ParameterSetName = "FromDirectory")]
    [string]
    $ExtractedRoot,

    # Environment entries as the image declares them, in NAME=value form. Supplied by the caller only
    # in the directory parameter set; the image parameter set reads them from the image itself.
    [Parameter(ParameterSetName = "FromDirectory")]
    [AllowEmptyCollection()]
    [string[]]
    $ImageEnvironment = @(),

    # The application whose runtimeconfig.json names the shared frameworks.
    [string]
    $EntryAssemblyName = "EdFi.DataManagementService.Frontend.AspNetCore",

    # The Dockerfile whose runtime base pin is recorded in the manifest header. It is recorded as a
    # fact about this checkout and cross-checked against the image, never presented as provenance for
    # the image itself.
    [string]
    $DockerfilePath,

    # The build stage whose FROM carries the runtime base. Named rather than taken from whichever
    # FROM appears first, because a Dockerfile's later stages build on the earlier ones and reading
    # the wrong line would put a stage reference where a base image belongs.
    [string]
    $RuntimeBaseStageName = "runtimebase",

    [string]
    $OutputPath = "host-assembly-manifest.md"
)

$ErrorActionPreference = "Stop"

# Exit codes from docker are checked explicitly below, per call, including the probes that are
# expected to fail. Leaving the automatic translation on would turn an expected non-zero probe into a
# terminating error and lose the fallback it exists to drive.
$PSNativeCommandUseErrorActionPreference = $false

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../.."))

if ([string]::IsNullOrWhiteSpace($DockerfilePath)) {
    $DockerfilePath = Join-Path $repositoryRoot "src/dms/Nuget.Dockerfile"
}

# Roll-forward is configurable from the environment as well as from runtimeconfig, so an image that
# set one of these would resolve differently from what the rules below compute.
$rollForwardEnvironmentNames = @(
    "DOTNET_ROLL_FORWARD",
    "DOTNET_ROLL_FORWARD_ON_NO_CANDIDATE_FX",
    "DOTNET_ROLL_FORWARD_TO_PRERELEASE"
)

# The shared framework root is not advertised by the released image, which declares no DOTNET_ROOT,
# so these are probed in order. Failing to find one is a refusal naming every root tried.
$sharedRootCandidates = @("/usr/share/dotnet", "/usr/lib/dotnet", "/usr/local/share/dotnet")

function Invoke-Docker {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string[]] $Arguments,
        [switch] $AllowFailure
    )

    # Every call site is checked. A docker step that failed and was not noticed would produce a
    # manifest missing a whole section rather than an error. Stderr is merged into the captured
    # output so a failure message reaches the thrown exception, which needs the preference relaxed
    # for the length of the call: an error record arriving on the output stream under Stop would
    # otherwise terminate before the exit code can be read and classified.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & docker @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "docker $($Arguments -join ' ') failed with exit code ${exitCode}: $($output -join [Environment]::NewLine)"
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = ($output | Out-String).Trim()
    }
}

function Read-JsonDocument {
    [CmdletBinding()]
    [OutputType([System.Text.Json.JsonDocument])]
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Expected JSON file not found: $Path"
    }

    return [System.Text.Json.JsonDocument]::Parse([System.IO.File]::ReadAllText($Path))
}

function Get-JsonProperty {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement] $Element,
        [Parameter(Mandatory)][string] $Name
    )

    $value = [System.Text.Json.JsonElement]::new()
    if ($Element.TryGetProperty($Name, [ref] $value)) {
        return $value
    }

    return $null
}

# Every roll-forward knob, wherever it can appear. Refused rather than honoured: honouring one would
# mean implementing the runtime's resolution, and ignoring one would mean reporting a selection the
# runtime would not make.
function Assert-NoRollForwardOverride {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement] $Element,
        [Parameter(Mandatory)][string] $Where
    )

    foreach ($name in @("rollForward", "applyPatches", "rollForwardOnNoCandidateFx")) {
        if ($null -ne (Get-JsonProperty -Element $Element -Name $name)) {
            throw "$Where declares '$name'. This tool supports only the released image's default framework resolution and refuses to report a selection it does not implement."
        }
    }
}

function Get-FrameworkReference {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.Text.Json.JsonElement] $RuntimeOptions, [Parameter(Mandatory)][string] $Where)

    if ($null -ne (Get-JsonProperty -Element $RuntimeOptions -Name "includedFrameworks")) {
        throw "$Where declares 'includedFrameworks', which is a self-contained publish. This tool inspects a framework-dependent application."
    }

    $references = @()

    $plural = Get-JsonProperty -Element $RuntimeOptions -Name "frameworks"
    if ($null -ne $plural) {
        foreach ($entry in $plural.EnumerateArray()) {
            $references += $entry
        }
    }

    $singular = Get-JsonProperty -Element $RuntimeOptions -Name "framework"
    if ($null -ne $singular) {
        $references += $singular
    }

    if ($references.Count -eq 0) {
        throw "$Where names no shared framework. This tool inspects a framework-dependent application."
    }

    $resolved = @()
    foreach ($reference in $references) {
        Assert-NoRollForwardOverride -Element $reference -Where "$Where framework reference"

        $name = Get-JsonProperty -Element $reference -Name "name"
        $version = Get-JsonProperty -Element $reference -Name "version"

        if ($null -eq $name -or $null -eq $version) {
            throw "$Where carries a framework reference without both a name and a version."
        }

        $parsed = [version] "0.0.0"
        if (-not [version]::TryParse($version.GetString(), [ref] $parsed)) {
            throw "$Where requests framework '$($name.GetString())' at version '$($version.GetString())', which does not parse as a version."
        }

        $resolved += [pscustomobject]@{ Name = $name.GetString(); Requested = $parsed }
    }

    return $resolved
}

# latestPatch, confined to the requested major.minor. Anything that would need a minor or major roll
# forward is a refusal.
function Select-FrameworkVersion {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string] $SharedRoot,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][version] $Requested
    )

    $frameworkRoot = Join-Path $SharedRoot $Name

    if (-not (Test-Path -LiteralPath $frameworkRoot -PathType Container)) {
        throw "The image carries no directory for shared framework '$Name', which the application requests at $Requested. Looked under $SharedRoot."
    }

    $installed = @()
    foreach ($directory in Get-ChildItem -LiteralPath $frameworkRoot -Directory) {
        $parsed = [version] "0.0.0"
        if (-not [version]::TryParse($directory.Name, [ref] $parsed)) {
            throw "Shared framework '$Name' carries a directory named '$($directory.Name)', which does not parse as a version. This tool refuses an ambiguous layout rather than guessing which directory the runtime would choose."
        }

        $installed += [pscustomobject]@{ Directory = $directory.Name; Version = $parsed }
    }

    if ($installed.Count -eq 0) {
        throw "Shared framework '$Name' has no version directory under $frameworkRoot."
    }

    $candidates = @(
        $installed |
            Where-Object {
                $_.Version.Major -eq $Requested.Major -and
                $_.Version.Minor -eq $Requested.Minor -and
                $_.Version -ge $Requested
            }
    )

    if ($candidates.Count -eq 0) {
        $found = ($installed | ForEach-Object { $_.Directory }) -join ", "
        throw "Shared framework '$Name' is requested at $Requested and the image carries only $found. Resolving that would need a minor or major roll forward, which this tool does not implement."
    }

    return (($candidates | Sort-Object -Property Version -Descending) | Select-Object -First 1).Directory
}

# A shared framework can itself declare a dependency on a lower one, so the selection is not settled
# until those agree with it.
function Assert-FrameworkDependenciesAgree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $SharedRoot,
        [Parameter(Mandatory)][hashtable] $Selected
    )

    foreach ($name in @($Selected.Keys)) {
        $configPath = Join-Path (Join-Path (Join-Path $SharedRoot $name) $Selected[$name]) "$name.runtimeconfig.json"

        if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
            continue
        }

        $document = Read-JsonDocument -Path $configPath
        try {
            $runtimeOptions = Get-JsonProperty -Element $document.RootElement -Name "runtimeOptions"
            if ($null -eq $runtimeOptions) {
                continue
            }

            # A shared framework legitimately pins itself to latestPatch, which is what the released
            # image's Microsoft.AspNetCore.App does and is exactly the policy selected above. Any
            # other value is refused rather than ignored.
            $rollForward = Get-JsonProperty -Element $runtimeOptions -Name "rollForward"
            if ($null -ne $rollForward -and $rollForward.GetString() -ne "LatestPatch") {
                throw "Shared framework '$name' declares rollForward '$($rollForward.GetString())', which this tool does not implement."
            }
            foreach ($unsupported in @("applyPatches", "rollForwardOnNoCandidateFx")) {
                if ($null -ne (Get-JsonProperty -Element $runtimeOptions -Name $unsupported)) {
                    throw "Shared framework '$name' declares '$unsupported', which this tool does not implement."
                }
            }

            foreach ($propertyName in @("frameworks", "framework")) {
                $property = Get-JsonProperty -Element $runtimeOptions -Name $propertyName
                if ($null -eq $property) {
                    continue
                }

                $entries = if ($propertyName -eq "frameworks") { @($property.EnumerateArray()) } else { @($property) }

                foreach ($entry in $entries) {
                    # The same refusal the application's own framework references get. A policy set
                    # on a nested entry changes which version the runtime would load just as surely
                    # as one set at the top of the file, and validating only the parent would let a
                    # nested Disable through while the manifest reported a version chosen under a
                    # policy the runtime was not using.
                    Assert-NoRollForwardOverride -Element $entry -Where "Shared framework '$name' framework reference"

                    $dependencyName = (Get-JsonProperty -Element $entry -Name "name").GetString()
                    $dependencyVersionText = (Get-JsonProperty -Element $entry -Name "version").GetString()

                    $dependencyVersion = [version] "0.0.0"
                    if (-not [version]::TryParse($dependencyVersionText, [ref] $dependencyVersion)) {
                        throw "Shared framework '$name' depends on '$dependencyName' at '$dependencyVersionText', which does not parse as a version."
                    }

                    if (-not $Selected.ContainsKey($dependencyName)) {
                        throw "Shared framework '$name' depends on '$dependencyName', which the application does not name. This tool inspects only the frameworks the application itself requests."
                    }

                    # Put through the same selection rules rather than taken on trust, which is what
                    # catches a dependency the image cannot satisfy or one naming a framework the
                    # application never asked for. The equality below is defensive: selection is a
                    # function of the framework and the installed set, so two satisfiable requests
                    # for the same framework agree by construction, and a request that does not
                    # agree has already thrown above.
                    $wouldSelect = Select-FrameworkVersion -SharedRoot $SharedRoot -Name $dependencyName -Requested $dependencyVersion

                    if ($wouldSelect -ne $Selected[$dependencyName]) {
                        throw "Shared framework '$name' depends on '$dependencyName' at $dependencyVersion, which selects $wouldSelect, but the application's own request selected $($Selected[$dependencyName]). The manifest would state a version the runtime would not load."
                    }
                }
            }
        }
        finally {
            $document.Dispose()
        }
    }
}

# Metadata only. GetAssemblyName reads the file's headers and does not load the assembly into this
# process, which matters because these are a third party's bytes as far as this tool is concerned.
function Get-ManagedAssemblyRow {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.IO.FileInfo] $File)

    try {
        $name = [System.Reflection.AssemblyName]::GetAssemblyName($File.FullName)
        return [pscustomobject]@{ Assembly = $name.Name; Version = $name.Version.ToString() }
    }
    catch {
        $exception = $_.Exception
        while ($null -ne $exception) {
            if ($exception -is [System.BadImageFormatException]) {
                # Not a managed assembly. The host's default context could not serve it as one
                # either, so it is not part of the compatibility surface.
                return $null
            }
            $exception = $exception.InnerException
        }

        throw
    }
}

function Get-AssemblyRow {
    [CmdletBinding()]
    [OutputType([object[]])]
    param([Parameter(Mandatory)][string] $Directory)

    $rows = @()
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter "*.dll" -File) {
        $row = Get-ManagedAssemblyRow -File $file
        if ($null -ne $row) {
            $rows += $row
        }
    }

    return @($rows | Sort-Object -Property Assembly -CaseSensitive)
}

function Get-RuntimeBasePin {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $StageName
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Dockerfile not found: $Path"
    }

    foreach ($line in [System.IO.File]::ReadAllLines($Path)) {
        # The named stage, not the first FROM. A Dockerfile's later stages build on its earlier ones,
        # so the first FROM is the runtime base only by coincidence of ordering.
        if ($line -match '^\s*FROM\s+(\S+)\s+AS\s+(\S+)\s*$' -and $Matches[2] -ieq $StageName) {
            return $Matches[1]
        }
    }

    throw "No 'FROM <image> AS $StageName' instruction found in $Path. This tool records the runtime base of a named stage and refuses a Dockerfile shape it does not recognise rather than reading whichever FROM comes first."
}

# The version the base image's tag carries, compared as a version rather than matched as text. An
# unanchored substring comparison treats 10.0.3 as present in 10.0.30, which is the wrong answer in
# the direction that matters: it accepts a checkout and an image that are not the same pair.
function Get-BaseImageVersion {
    [CmdletBinding()]
    [OutputType([version])]
    param([Parameter(Mandatory)][string] $Reference)

    $withoutDigest = ($Reference -split "@", 2)[0]

    $lastColon = $withoutDigest.LastIndexOf(":")
    $lastSlash = $withoutDigest.LastIndexOf("/")

    # A colon before the last slash is a registry port, not a tag separator.
    if ($lastColon -lt 0 -or $lastColon -lt $lastSlash) {
        throw "The runtime base reference '$Reference' carries no tag, so there is no version to cross-check against the inspected image."
    }

    $tag = $withoutDigest.Substring($lastColon + 1)

    if ($tag -notmatch '^(\d+\.\d+\.\d+)(?:[-.].*)?$') {
        throw "The runtime base tag '$tag' does not begin with a three-part version, so there is no version to cross-check against the inspected image."
    }

    return [version] $Matches[1]
}

function Format-AssemblyTable {
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Rows)

    $lines = @("| Assembly | AssemblyVersion |", "| --- | --- |")
    foreach ($row in $Rows) {
        $lines += "| $($row.Assembly) | $($row.Version) |"
    }

    return $lines
}

# ---------------------------------------------------------------------------------------------
# Stage the image, or take a staged directory as given.
# ---------------------------------------------------------------------------------------------

$stageRoot = $null
$stageIsOurs = $false
$containerId = $null
$inspectedReference = $null
$inspectedDigest = $null
$environmentEntries = @($ImageEnvironment)

try {
    if ($PSCmdlet.ParameterSetName -eq "FromImage") {
        Write-Verbose "Pulling $Image"
        Invoke-Docker -Arguments @("pull", $Image) | Out-Null

        # One immutable digest from here on. A tag republished between two inspections would
        # otherwise let one manifest describe two images.
        $digestResult = Invoke-Docker -Arguments @("image", "inspect", $Image, "--format", "{{index .RepoDigests 0}}")
        $inspectedDigest = $digestResult.Output

        if ([string]::IsNullOrWhiteSpace($inspectedDigest) -or $inspectedDigest -notmatch "@sha256:") {
            throw "Could not resolve an immutable digest for '$Image'. A manifest is generated from a released image pulled from a registry, not from a locally built one."
        }

        $inspectedReference = $inspectedDigest

        $environmentResult = Invoke-Docker -Arguments @("image", "inspect", $inspectedReference, "--format", "{{json .Config.Env}}")
        $environmentDocument = [System.Text.Json.JsonDocument]::Parse($environmentResult.Output)
        try {
            $environmentEntries = @($environmentDocument.RootElement.EnumerateArray() | ForEach-Object { $_.GetString() })
        }
        finally {
            $environmentDocument.Dispose()
        }

        $stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) "host-assembly-manifest-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
        $stageIsOurs = $true

        # create, never run. The entrypoint is not executed: this reads files, and starting a
        # database-backed API to list its own assemblies would be both slower and wrong.
        $containerId = (Invoke-Docker -Arguments @("create", $inspectedReference)).Output

        if ([string]::IsNullOrWhiteSpace($containerId)) {
            throw "docker create returned no container id for $inspectedReference."
        }

        Invoke-Docker -Arguments @("cp", "${containerId}:/app", (Join-Path $stageRoot "app")) | Out-Null

        $sharedCopied = $false
        $declaredRoot = ($environmentEntries | Where-Object { $_ -like "DOTNET_ROOT=*" } | Select-Object -First 1)
        $rootsToTry = if ($null -ne $declaredRoot) {
            @($declaredRoot.Substring("DOTNET_ROOT=".Length))
        }
        else {
            $sharedRootCandidates
        }

        foreach ($candidate in $rootsToTry) {
            $attempt = Invoke-Docker -Arguments @("cp", "${containerId}:$candidate/shared", (Join-Path $stageRoot "shared")) -AllowFailure
            if ($attempt.ExitCode -eq 0) {
                $sharedCopied = $true
                break
            }
        }

        if (-not $sharedCopied) {
            throw "Could not find a shared framework root in $inspectedReference. Tried: $($rootsToTry -join ', ')."
        }
    }
    else {
        $stageRoot = [System.IO.Path]::GetFullPath($ExtractedRoot)
        $inspectedReference = $ExtractedRoot
    }

    foreach ($name in $rollForwardEnvironmentNames) {
        if ($environmentEntries | Where-Object { $_ -like "$name=*" }) {
            throw "The image declares $name. This tool supports only the released image's default framework resolution and refuses to report a selection it does not implement."
        }
    }

    $appDirectory = Join-Path $stageRoot "app"
    $sharedDirectory = Join-Path $stageRoot "shared"

    foreach ($required in @($appDirectory, $sharedDirectory)) {
        if (-not (Test-Path -LiteralPath $required -PathType Container)) {
            throw "Expected staged directory not found: $required"
        }
    }

    # -----------------------------------------------------------------------------------------
    # Frameworks.
    # -----------------------------------------------------------------------------------------

    $runtimeConfigPath = Join-Path $appDirectory "$EntryAssemblyName.runtimeconfig.json"
    $runtimeConfig = Read-JsonDocument -Path $runtimeConfigPath

    $selected = @{}
    try {
        $runtimeOptions = Get-JsonProperty -Element $runtimeConfig.RootElement -Name "runtimeOptions"
        if ($null -eq $runtimeOptions) {
            throw "$runtimeConfigPath carries no runtimeOptions."
        }

        Assert-NoRollForwardOverride -Element $runtimeOptions -Where $runtimeConfigPath

        foreach ($reference in (Get-FrameworkReference -RuntimeOptions $runtimeOptions -Where $runtimeConfigPath)) {
            $selected[$reference.Name] = Select-FrameworkVersion `
                -SharedRoot $sharedDirectory -Name $reference.Name -Requested $reference.Requested
        }
    }
    finally {
        $runtimeConfig.Dispose()
    }

    Assert-FrameworkDependenciesAgree -SharedRoot $sharedDirectory -Selected $selected

    # -----------------------------------------------------------------------------------------
    # Assemblies.
    # -----------------------------------------------------------------------------------------

    $applicationRows = Get-AssemblyRow -Directory $appDirectory

    if ($applicationRows.Count -eq 0) {
        throw "No managed assembly was found at the top level of $appDirectory."
    }

    $frameworkSections = @()
    foreach ($name in ($selected.Keys | Sort-Object)) {
        $directory = Join-Path (Join-Path $sharedDirectory $name) $selected[$name]
        $rows = Get-AssemblyRow -Directory $directory

        if ($rows.Count -eq 0) {
            throw "Shared framework '$name' version $($selected[$name]) carries no managed assembly. A manifest with an empty framework section would understate the compatibility surface."
        }

        $frameworkSections += [pscustomobject]@{ Name = $name; Version = $selected[$name]; Rows = $rows }
    }

    # -----------------------------------------------------------------------------------------
    # Header facts, each labelled with where it came from.
    # -----------------------------------------------------------------------------------------

    $basePin = Get-RuntimeBasePin -Path $DockerfilePath -StageName $RuntimeBaseStageName
    $dockerfileRelative = [System.IO.Path]::GetRelativePath($repositoryRoot, [System.IO.Path]::GetFullPath($DockerfilePath)).Replace("\", "/")

    $declaredVersions = @{}
    foreach ($entry in $environmentEntries) {
        foreach ($name in @("DOTNET_VERSION", "ASPNET_VERSION")) {
            if ($entry -like "$name=*") {
                $declaredVersions[$name] = $entry.Substring($name.Length + 1)
            }
        }
    }

    # The base pin is a fact about this checkout, and the manifest says so rather than presenting it
    # as provenance for the image. Cross-checking it is what makes the label meaningful: a checkout
    # whose pin disagrees with the image it was pointed at is not the pair this manifest claims.
    $observedFrameworkVersions = @($frameworkSections | ForEach-Object { $_.Version } | Sort-Object -Unique)

    foreach ($pair in @(
            @{ Name = "DOTNET_VERSION"; Framework = "Microsoft.NETCore.App" },
            @{ Name = "ASPNET_VERSION"; Framework = "Microsoft.AspNetCore.App" }
        )) {
        if (-not $declaredVersions.ContainsKey($pair.Name)) {
            continue
        }
        if (-not $selected.ContainsKey($pair.Framework)) {
            continue
        }
        if ($declaredVersions[$pair.Name] -ne $selected[$pair.Framework]) {
            throw "The image declares $($pair.Name)=$($declaredVersions[$pair.Name]) but carries $($pair.Framework) $($selected[$pair.Framework]). The extraction and the image disagree, so the manifest would be wrong."
        }
    }

    # The released base image is an aspnet image, so its tag carries the ASP.NET Core shared
    # framework's version. That is the version compared, falling back to the observed version when
    # every framework selected the same one.
    $pinComparisonVersion = if ($selected.ContainsKey("Microsoft.AspNetCore.App")) {
        $selected["Microsoft.AspNetCore.App"]
    }
    elseif ($observedFrameworkVersions.Count -eq 1) {
        $observedFrameworkVersions[0]
    }
    else {
        $null
    }

    if ($null -ne $pinComparisonVersion) {
        $basePinVersion = Get-BaseImageVersion -Reference $basePin

        if ($basePinVersion -ne [version] $pinComparisonVersion) {
            throw "The runtime base pinned by $dockerfileRelative is '$basePin', carrying $basePinVersion, while the inspected image carries $pinComparisonVersion. This checkout and $inspectedReference are not the same pair, so the header's base pin would be misleading."
        }
    }

    # -----------------------------------------------------------------------------------------
    # Emit.
    # -----------------------------------------------------------------------------------------

    $contractRows = @(
        $applicationRows |
            Where-Object { $_.Assembly -eq "EdFi.Api.Plugins" -or $_.Assembly -eq "EdFi.DataManagementService.CustomValidation" }
    )

    $lines = @(
        "# Host assembly manifest",
        "",
        "Every managed assembly the Ed-Fi API host can serve a plugin, with its ``AssemblyVersion``.",
        "Assembly resolution is host-first, so an assembly listed here is served from the host and a",
        "plugin's own copy of it is not used. A plugin that declares a **higher** version of anything",
        "listed here is refused.",
        "",
        "Generated by ``eng/verification/New-HostAssemblyManifest.ps1``.",
        "",
        "## Release",
        "",
        "| Fact | Value |",
        "| --- | --- |",
        "| Inspected image | $inspectedReference |",
        "| Runtime base pinned by ``$dockerfileRelative`` in this checkout | $basePin |"
    )

    foreach ($name in @("DOTNET_VERSION", "ASPNET_VERSION")) {
        if ($declaredVersions.ContainsKey($name)) {
            $lines += "| ``$name`` declared by the inspected image | $($declaredVersions[$name]) |"
        }
    }

    foreach ($section in $frameworkSections) {
        $lines += "| $($section.Name) version observed in the inspected image | $($section.Version) |"
    }

    $lines += @(
        "| Framework selection policy | Framework-dependent application with no roll-forward override; the highest installed patch within the requested major.minor, and never below the requested version. |",
        "",
        "The runtime base row is a fact about the checkout this manifest was generated from, not",
        "provenance for the image. It is cross-checked against the versions the image declares and",
        "carries, and a disagreement fails generation rather than being reported.",
        "",
        "### Contract assemblies",
        ""
    )

    $lines += Format-AssemblyTable -Rows $contractRows

    $lines += @(
        "",
        "Every value above is the ``AssemblyVersion`` observed in the image. Read each against the",
        "versioning policy of the package it comes from: a contract that declares its own version,",
        "as ``EdFi.Api.Plugins`` does, states that version here and moves it only when its public",
        "surface moves, while a contract that inherits the Data Management Service release version",
        "states that instead. An observed assembly version is not necessarily a contract package",
        "version.",
        "",
        "## Application assemblies",
        "",
        "One row per managed assembly at the top level of ``/app``. Assemblies under ``/app`` in a",
        "subdirectory are excluded: ``/app/ApiSchemaDownloader/`` is a separate application that runs",
        "in its own process, so a plugin can never be served one of its assemblies, and",
        "``/app/runtimes/`` holds RID-specific copies of libraries this table already lists.",
        ""
    )

    $lines += Format-AssemblyTable -Rows $applicationRows

    foreach ($section in $frameworkSections) {
        $lines += @(
            "",
            "## Shared framework: $($section.Name) $($section.Version)",
            "",
            "Supplied by the runtime rather than by the application, which is why an image scan",
            "rather than a ``.deps.json`` is what finds them.",
            "",
            "This section says where the *host* gets an assembly. It does not say when a plugin's",
            "version skew against one is caught, which depends on how the plugin obtained it. An",
            "assembly a plugin reaches only through a framework reference is declared nowhere in its",
            "framework-dependent ``.deps.json``, so skew on that one surfaces at first use. One the",
            "plugin takes as a ``PackageReference``, which is how the Microsoft.Extensions",
            "abstractions normally arrive, carries a runtime entry in that file and is checked at",
            "load, before the plugin is constructed.",
            ""
        )
        $lines += Format-AssemblyTable -Rows $section.Rows
    }

    $manifest = ($lines -join "`n") + "`n"
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutputPath), $manifest)

    $frameworkSummary = ($frameworkSections | ForEach-Object { "$($_.Name) $($_.Version): $($_.Rows.Count)" }) -join "; "
    Write-Output "Wrote $OutputPath from ${inspectedReference}: $($applicationRows.Count) application assemblies; $frameworkSummary."
}
finally {
    if ($null -ne $containerId) {
        Invoke-Docker -Arguments @("rm", "-f", $containerId) -AllowFailure | Out-Null
    }

    # Only the directory this run created, and only after its resolved path is confirmed to sit
    # under the temp directory. A recursive delete guarded by nothing but a variable is how a tool
    # removes a caller's tree when an earlier assignment did not happen.
    if ($stageIsOurs -and $null -ne $stageRoot -and (Test-Path -LiteralPath $stageRoot)) {
        $resolvedStage = [System.IO.Path]::GetFullPath($stageRoot)
        $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())

        if ($resolvedStage.StartsWith($temporaryRoot, [StringComparison]::Ordinal) -and $resolvedStage.Length -gt $temporaryRoot.Length) {
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
        else {
            Write-Warning "Leaving $resolvedStage in place: it is not under $temporaryRoot, and this script removes only directories it created there."
        }
    }
}
