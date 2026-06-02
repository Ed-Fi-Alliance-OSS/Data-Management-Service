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

    return @(
        $frameworkDirectories |
            Sort-Object `
                -Property @{ Expression = { $_.FrameworkVersion }; Descending = $true },
                    @{ Expression = { $_.ConfigurationPriority }; Ascending = $true } |
            ForEach-Object { $_.Directory }
    )
}

function Resolve-DmsSchemaTool {
    <#
    .SYNOPSIS
    Resolves the absolute path to the dms-schema executable used by the bootstrap schema phase.
    .DESCRIPTION
    Honors an explicit -RequestedPath when supplied, otherwise probes the in-repo build output
    directories. PATH-resolved fallback is opt-in via DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK=true;
    when nothing resolves, throws with build/configuration guidance.
    .PARAMETER RequestedPath
    Optional explicit path to the dms-schema executable. When set, it must exist or resolution throws.
    #>
    param(
        [string]
        $RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $fullPath = [System.IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "The configured dms-schema executable was not found: $(Format-LogSafeText $fullPath)"
        }

        return $fullPath
    }

    $candidateNames = if ($IsWindows) {
        @("dms-schema.exe", "dms-schema")
    } else {
        @("dms-schema")
    }

    foreach ($candidateDirectory in Get-DmsSchemaToolCandidateDirectory) {
        foreach ($candidateName in $candidateNames) {
            $candidate = Join-Path $candidateDirectory $candidateName
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return $candidate
            }
        }
    }

    $pathCommand = Get-Command "dms-schema" -ErrorAction SilentlyContinue
    if ($null -ne $pathCommand) {
        if ($env:DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK -ne "true") {
            throw "In-repo dms-schema tool not found. Build src/dms/clis/EdFi.DataManagementService.SchemaTools, set DMS_SCHEMA_TOOL_PATH, or set DMS_SCHEMA_TOOL_ALLOW_PATH_FALLBACK=true to opt in to PATH fallback."
        }

        Write-Warning "Falling back to PATH-resolved dms-schema executable: $(Format-LogSafeText ($pathCommand.Source))"
        return $pathCommand.Source
    }

    throw "Unable to resolve the dms-schema executable. Build src/dms/clis/EdFi.DataManagementService.SchemaTools or set DMS_SCHEMA_TOOL_PATH."
}

function Invoke-DmsSchemaHash {
    <#
    .SYNOPSIS
    Runs 'dms-schema hash' and returns the lowercased effective schema hash.
    .DESCRIPTION
    Invokes the resolved dms-schema tool against the supplied core and extension schema paths,
    throwing on a non-zero exit code or when the tool does not report an effective schema hash.
    .PARAMETER ToolPath
    Path to the dms-schema executable (or .ps1 wrapper) returned by Resolve-DmsSchemaTool.
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
        throw "dms-schema hash failed with exit code $exitCode. $(Format-LogSafeText $outputText)"
    }

    if ($outputText -notmatch "(?m)^Effective schema hash:\s*([a-fA-F0-9]{64})\s*$") {
        throw "dms-schema hash completed but did not report an Effective schema hash. $(Format-LogSafeText $outputText)"
    }

    return $matches[1].ToLowerInvariant()
}

Export-ModuleMember -Function Resolve-DmsSchemaTool, Invoke-DmsSchemaHash
