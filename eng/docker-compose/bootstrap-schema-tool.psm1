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
    #>
    param(
        [string]
        $RequestedPath
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
