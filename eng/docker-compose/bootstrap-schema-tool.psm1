# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Set-StrictMode -Version Latest

# Import WITHOUT -Force. A -Force reload here removes and re-imports bootstrap-manifest, which re-homes it
# out of a caller's session scope (e.g. a start script that imported bootstrap-manifest for its own env
# snapshot / startup config) and breaks that caller's bootstrap-manifest functions. Without -Force the
# module is loaded if absent and reused if already present, so importing bootstrap-schema-tool never
# disturbs a caller's bootstrap-manifest.
Import-Module (Join-Path $PSScriptRoot "bootstrap-manifest.psm1")

function Get-DotNetFrameworkVersion {
    param(
        [Parameter(Mandatory)]
        [string]
        $Name
    )

    if ($Name -match "^net(?<Major>\d+)(?:\.(?<Minor>\d+))?") {
        $minor = if ([string]::IsNullOrWhiteSpace($matches["Minor"])) { 0 } else { [int]$matches["Minor"] }
        return [version]::new([int]$matches["Major"], $minor)
    }

    return [version]::new(0, 0)
}

function Get-DmsSchemaToolCandidateDirectory {
    $repoRoot = Get-BootstrapRepoRoot
    $candidateDirectories = [System.Collections.ArrayList]::new()

    # Documented publish location (README "Standard mode" / prepare-dms-schema.ps1 help):
    # `dotnet publish ... -o .bootstrap/tools/api-schema-tools` drops the apphost executable directly in this
    # flat directory. Probing it lets the documented wrapper shorthand
    # (`bootstrap-local-dms.ps1` package-backed standard staging) resolve the tool after the documented one-time
    # publish step, without requiring -SchemaToolPath (which the wrapper does not forward) or DMS_SCHEMA_TOOL_PATH.
    $publishedToolDirectory = Join-Path (Get-BootstrapRoot) "tools/api-schema-tools"
    if (Test-Path -LiteralPath $publishedToolDirectory -PathType Container) {
        $null = $candidateDirectories.Add($publishedToolDirectory)
    }

    $toolBinRoot = Join-Path $repoRoot "src/dms/clis/EdFi.DataManagementService.SchemaTools/bin"
    $frameworkDirectories = [System.Collections.ArrayList]::new()

    foreach ($configuration in @("Debug", "Release")) {
        $configurationRoot = Join-Path $toolBinRoot $configuration
        if (-not (Test-Path -LiteralPath $configurationRoot -PathType Container)) {
            continue
        }

        foreach ($frameworkDirectory in Get-ChildItem -LiteralPath $configurationRoot -Directory -Filter "net*") {
            if ($frameworkDirectory.Name -notmatch "^net\d") {
                continue
            }

            $null = $frameworkDirectories.Add(
                [pscustomobject]@{
                    Directory = $frameworkDirectory.FullName
                    FrameworkVersion = Get-DotNetFrameworkVersion -Name $frameworkDirectory.Name
                    ConfigurationPriority = if ($configuration -eq "Debug") { 0 } else { 1 }
                }
            )
        }
    }

    $sortedBinDirectories = @(
        $frameworkDirectories |
            Sort-Object `
                -Property @{ Expression = { $_.FrameworkVersion }; Descending = $true },
                    @{ Expression = { $_.ConfigurationPriority }; Ascending = $true } |
            ForEach-Object { $_.Directory }
    )
    foreach ($binDirectory in $sortedBinDirectories) {
        $null = $candidateDirectories.Add($binDirectory)
    }

    return @($candidateDirectories)
}

function Invoke-DmsSchemaToolPublish {
    <#
    .SYNOPSIS
    Publishes the api-schema-tools CLI from source to a drop directory and returns the publish outcome.
    .DESCRIPTION
    Isolated as a seam so the -BuildIfMissing recovery branch in Resolve-DmsSchemaTool is directly testable
    without a live .NET SDK: tests mock this function to simulate publish success or failure. The dotnet
    output is captured (not returned) so it can never pollute the resolver's path assignment; the caller
    re-probes for the executable on success or surfaces the captured output as guidance on failure.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $ProjectPath,

        [Parameter(Mandatory)]
        [string]
        $PublishDirectory
    )

    $output = & dotnet publish $ProjectPath -c Release -p:UseAppHost=true -o $PublishDirectory --nologo 2>&1
    return [pscustomobject]@{
        Succeeded = ($LASTEXITCODE -eq 0)
        Output    = ($output | Out-String).Trim()
    }
}

function Resolve-DmsSchemaTool {
    <#
    .SYNOPSIS
    Resolves the absolute path to the api-schema-tools executable used by the bootstrap schema phase.
    .DESCRIPTION
    Honors an explicit -RequestedPath when supplied, otherwise probes the in-repo build output
    directories. PATH-resolved fallback is opt-in via DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK=true;
    when nothing resolves, throws with build/configuration guidance.
    .PARAMETER RequestedPath
    Optional explicit path to the api-schema-tools executable. When set, it must exist or resolution throws.
    .PARAMETER BuildIfMissing
    When no prebuilt tool is found and the .NET SDK is available, publish the CLI from source once to the
    documented drop directory (.bootstrap/tools/api-schema-tools) and re-probe, instead of throwing. Lets
    lanes/operators that run the start scripts without a prior host build (template builds, config-PR E2E,
    direct diagnostic use) self-heal. When the SDK is absent, resolution still throws with guidance.
    #>
    param(
        [string]
        $RequestedPath,

        [switch]
        $BuildIfMissing
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $fullPath = [System.IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "The configured api-schema-tools executable was not found: $(Format-LogSafeText $fullPath)"
        }

        return $fullPath
    }

    $candidateNames = if ($IsWindows) {
        @("api-schema-tools.exe", "api-schema-tools")
    } else {
        @("api-schema-tools")
    }

    foreach ($candidateDirectory in Get-DmsSchemaToolCandidateDirectory) {
        foreach ($candidateName in $candidateNames) {
            $candidate = Join-Path $candidateDirectory $candidateName
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return $candidate
            }
        }
    }

    if ($BuildIfMissing) {
        # Nothing prebuilt. When the .NET SDK is available, publish the CLI from source once to the
        # documented drop directory and re-probe, so a lane/operator running the start scripts without a
        # prior host build self-heals rather than failing. dotnet output is captured (not returned) so it
        # cannot pollute the caller's assignment; the SDK-absent case falls through to the guidance throw.
        $dotnetCommand = Get-Command "dotnet" -ErrorAction SilentlyContinue
        $schemaToolsProject = Join-Path (Get-BootstrapRepoRoot) "src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj"
        if ($null -ne $dotnetCommand -and (Test-Path -LiteralPath $schemaToolsProject -PathType Leaf)) {
            $publishDirectory = Join-Path (Get-BootstrapRoot) "tools/api-schema-tools"
            Write-Information "api-schema-tools was not found; publishing it from source to '$(Format-LogSafeText $publishDirectory)' (one-time build)..." -InformationAction Continue
            $publishResult = Invoke-DmsSchemaToolPublish -ProjectPath $schemaToolsProject -PublishDirectory $publishDirectory
            if ($publishResult.Succeeded) {
                foreach ($candidateName in $candidateNames) {
                    $candidate = Join-Path $publishDirectory $candidateName
                    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                        return $candidate
                    }
                }
            }
            else {
                Write-Warning "Failed to publish api-schema-tools; falling back to resolution guidance. $(Format-LogSafeText $publishResult.Output)"
            }
        }
    }

    $pathCommand = Get-Command "api-schema-tools" -ErrorAction SilentlyContinue
    if ($null -ne $pathCommand) {
        if ($env:DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK -ne "true") {
            throw "In-repo api-schema-tools tool not found. Build src/dms/clis/EdFi.DataManagementService.SchemaTools, set DMS_SCHEMA_TOOL_PATH, or set DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK=true to opt in to PATH fallback."
        }

        Write-Warning "Falling back to PATH-resolved api-schema-tools executable: $(Format-LogSafeText ($pathCommand.Source))"
        return $pathCommand.Source
    }

    throw "Unable to resolve the api-schema-tools executable. Build src/dms/clis/EdFi.DataManagementService.SchemaTools or set DMS_SCHEMA_TOOL_PATH."
}

function Resolve-DmsConnectionValidator {
    <#
    .SYNOPSIS
    Resolves HOW to run the exact-provider connection-string validator on this host, returning a
    descriptor the runtime contract passes through to the connection-validation seam.
    .DESCRIPTION
    Returns one of:
      [pscustomobject]@{ Kind = 'HostExe';     Path  = <path> }
      [pscustomobject]@{ Kind = 'DockerImage'; Image = <image>; ToolPath = <in-image dll path> }

    A host executable is preferred: an explicit -RequestedPath, a prebuilt in-repo copy, or a
    -BuildIfMissing publish when the .NET SDK is present (Resolve-DmsSchemaTool). When no host executable
    can be resolved AND a -DmsImage is supplied, it falls back to running the SAME 'connection validate'
    verb inside that image, which bundles the api-schema-tools CLI. This is the published-image path on a
    clean Docker/PowerShell-only host: exact-provider parsing (Npgsql / Microsoft.Data.SqlClient) stays
    available with no .NET SDK and no source build. The parser is never weakened or replaced.

    Throws only when neither a host executable nor a container image is available (so a source checkout on
    an SDK-less host with no image still fails with the original build/configuration guidance).
    .PARAMETER RequestedPath
    Optional explicit host path to the api-schema-tools executable (DMS_SCHEMA_TOOL_PATH). When set it must
    exist or host resolution fails - and the container fallback then applies only if -DmsImage is supplied.
    .PARAMETER DmsImage
    The DMS container image (resolved from Docker Compose) that bundles the tool. When host resolution
    fails and this is supplied, the validator runs inside this image.
    .PARAMETER ContainerToolPath
    Path to the bundled tool assembly inside -DmsImage. Defaults to the image's documented drop location.
    #>
    param(
        [string]
        $RequestedPath,

        [string]
        $DmsImage,

        [string]
        $ContainerToolPath = "/app/ApiSchemaTools/api-schema-tools.dll"
    )

    try {
        $hostTool = Resolve-DmsSchemaTool -RequestedPath $RequestedPath -BuildIfMissing
        return [pscustomobject]@{ Kind = "HostExe"; Path = $hostTool }
    }
    catch {
        if ([string]::IsNullOrWhiteSpace($DmsImage)) {
            throw
        }

        Write-Information "api-schema-tools is not available on the host; the connection-string validator will run inside the '$(Format-LogSafeText $DmsImage)' image (no host .NET SDK or source build required)." -InformationAction Continue
        return [pscustomobject]@{
            Kind     = "DockerImage"
            Image    = $DmsImage
            ToolPath = $ContainerToolPath
        }
    }
}

function Invoke-DmsSchemaHash {
    <#
    .SYNOPSIS
    Runs 'api-schema-tools hash' and returns the lowercased effective schema hash.
    .DESCRIPTION
    Invokes the resolved api-schema-tools tool against the supplied core and extension schema paths,
    throwing on a non-zero exit code or when the tool does not report an effective schema hash.
    .PARAMETER ToolPath
    Path to the api-schema-tools executable (or .ps1 wrapper) returned by Resolve-DmsSchemaTool.
    .PARAMETER CoreSchemaPath
    Path to the core ApiSchema content to hash.
    .PARAMETER ExtensionSchemaPath
    Optional extension ApiSchema paths to include in the hash.
    #>
    param(
        [Parameter(Mandatory)]
        [string]
        $ToolPath,

        [Parameter(Mandatory)]
        [string]
        $CoreSchemaPath,

        [string[]]
        $ExtensionSchemaPath = @()
    )

    $arguments = @("hash", $CoreSchemaPath) + $ExtensionSchemaPath
    $output = if ($ToolPath.EndsWith(".ps1", [System.StringComparison]::OrdinalIgnoreCase)) {
        & pwsh -NoLogo -NoProfile -File $ToolPath @arguments 2>&1
    } else {
        & $ToolPath @arguments 2>&1
    }

    $exitCode = $LASTEXITCODE
    $outputText = ($output | Out-String).Trim()

    if ($exitCode -ne 0) {
        throw "api-schema-tools hash failed with exit code $exitCode. $(Format-LogSafeText $outputText)"
    }

    if ($outputText -notmatch "(?m)^Effective schema hash:\s*([a-fA-F0-9]{64})\s*$") {
        throw "api-schema-tools hash completed but did not report an Effective schema hash. $(Format-LogSafeText $outputText)"
    }

    return $matches[1].ToLowerInvariant()
}

Export-ModuleMember -Function Resolve-DmsSchemaTool, Resolve-DmsConnectionValidator, Invoke-DmsSchemaHash
