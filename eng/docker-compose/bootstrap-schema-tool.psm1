# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot "bootstrap-manifest.psm1") -Force

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

    # Publish destination for automatic acquisition and optional manual refresh. The flat directory contains the
    # apphost executable plus its dependency/runtime assets.
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

function Resolve-DmsSchemaTool {
    <#
    .SYNOPSIS
    Resolves the absolute path to the api-schema-tools executable used by the bootstrap schema phase.
    .DESCRIPTION
    Honors an explicit -RequestedPath when supplied, then probes the published and in-repo build output
    directories. PATH-resolved fallback is opt-in via DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK=true. When no
    eligible executable resolves, publishes the SchemaTools project to the bootstrap tool directory.
    .PARAMETER RequestedPath
    Optional explicit path to the api-schema-tools executable. When set, it must exist or resolution throws.
    #>
    param(
        [string]
        $RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $fullPath = [System.IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "The configured api-schema-tools executable was not found: $(Format-LogSafePath $fullPath)"
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

    $pathCommand = Get-Command "api-schema-tools" -ErrorAction SilentlyContinue
    if ($null -ne $pathCommand -and $env:DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK -eq "true") {
        Write-Warning "Falling back to PATH-resolved api-schema-tools executable: $(Format-LogSafePath ($pathCommand.Source))"
        return $pathCommand.Source
    }

    $repoRoot = Get-BootstrapRepoRoot
    $bootstrapRoot = Get-BootstrapRoot
    $projectPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "src/dms/clis/EdFi.DataManagementService.SchemaTools/EdFi.DataManagementService.SchemaTools.csproj"))
    $outputDirectory = [System.IO.Path]::GetFullPath((Join-Path $bootstrapRoot "tools/api-schema-tools"))
    $safeProjectPath = Format-LogSafePath $projectPath
    $safeOutputDirectory = Format-LogSafePath $outputDirectory

    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Cannot publish api-schema-tools because the source project is missing: $safeProjectPath. Restore the repository checkout or set DMS_SCHEMA_TOOL_PATH to an existing executable."
    }

    $dotnetCommand = Get-Command "dotnet" -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw "Cannot publish api-schema-tools because the .NET SDK command 'dotnet' is unavailable. Install the .NET 10 SDK and ensure dotnet is on PATH, or set DMS_SCHEMA_TOOL_PATH to an existing executable. Source project: $safeProjectPath"
    }

    try {
        New-Item -ItemType Directory -Path $outputDirectory -Force -ErrorAction Stop | Out-Null
    }
    catch {
        throw "Cannot prepare api-schema-tools publish output directory '$safeOutputDirectory'. Check filesystem permissions and available disk space, or set DMS_SCHEMA_TOOL_PATH to an existing executable. $(Format-LogSafeText $_.Exception.Message)"
    }

    $existingExecutables = @{}
    foreach ($candidateName in $candidateNames) {
        $candidatePath = [System.IO.Path]::GetFullPath((Join-Path $outputDirectory $candidateName))
        if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
            $existingExecutables[$candidatePath] = $true
        }
    }

    $publishArguments = @("publish", $projectPath, "-c", "Release", "-p:UseAppHost=true", "-o", $outputDirectory)
    Write-Information "No eligible api-schema-tools executable was found. Publishing the source project to '$safeOutputDirectory'." -InformationAction Continue

    $publishOutput = @()
    $publishExitCode = $null
    $invocationFailure = $null
    $originalErrorActionPreference = $ErrorActionPreference
    $originalNativeErrorPreference = $PSNativeCommandUseErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $PSNativeCommandUseErrorActionPreference = $false
        $publishOutput = @(& dotnet @publishArguments 2>&1)
        $publishExitCode = $LASTEXITCODE
    }
    catch {
        $invocationFailure = Format-LogSafeText $_.Exception.Message
        $publishExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $originalErrorActionPreference
        $PSNativeCommandUseErrorActionPreference = $originalNativeErrorPreference
    }

    $outputText = Format-LogSafeText (($publishOutput | ForEach-Object { [string]$_ }) -join " | ")
    $expectedName = if ($IsWindows) { "api-schema-tools.exe" } else { "api-schema-tools" }
    $expectedPath = [System.IO.Path]::GetFullPath((Join-Path $outputDirectory $expectedName))
    $publishFailure = $null
    if ($null -ne $invocationFailure) {
        $publishFailure = "dotnet publish invocation failed: $invocationFailure"
    }
    elseif ($publishExitCode -ne 0) {
        $publishFailure = "dotnet publish exited with code $publishExitCode."
    }
    elseif (-not (Test-Path -LiteralPath $expectedPath -PathType Leaf)) {
        $publishFailure = "dotnet publish completed without producing the expected executable '$expectedName'."
    }

    if ($null -ne $publishFailure) {
        $cleanupFailures = [System.Collections.Generic.List[string]]::new()
        foreach ($candidateName in $candidateNames) {
            $candidatePath = [System.IO.Path]::GetFullPath((Join-Path $outputDirectory $candidateName))
            $parentPath = [System.IO.Path]::GetFullPath((Split-Path -Parent $candidatePath))
            if ($existingExecutables.ContainsKey($candidatePath) -or
                -not [string]::Equals($parentPath, $outputDirectory, [System.StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
                continue
            }

            try {
                Remove-Item -LiteralPath $candidatePath -Force -ErrorAction Stop
            }
            catch {
                $cleanupFailures.Add((Format-LogSafeText $_.Exception.Message))
            }
        }

        $details = if ([string]::IsNullOrWhiteSpace($outputText)) { "" } else { " Publish output: $outputText" }
        $cleanupDetails = if ($cleanupFailures.Count -eq 0) { "" } else { " Cleanup also failed: $($cleanupFailures -join ' | ')" }
        throw "Unable to acquire api-schema-tools from '$safeProjectPath' into '$safeOutputDirectory'. $publishFailure$details$cleanupDetails Check the .NET 10 SDK, package restore/build output, and output directory access, or set DMS_SCHEMA_TOOL_PATH to an existing executable."
    }

    return $expectedPath
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

Export-ModuleMember -Function Resolve-DmsSchemaTool, Invoke-DmsSchemaHash
