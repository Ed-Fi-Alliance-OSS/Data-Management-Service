# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# The caller owns a fresh scratch directory and removes it in finally, including on install failure.
Set-StrictMode -Version Latest

function Install-SecurityMetadataToolsPackage {
    <#
    .SYNOPSIS
        Installs the caller's exact local package into its disposable feed/cache/tool directory.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)] [string] $PackageFile,
        [Parameter(Mandatory)] [string] $PackageVersion,
        [Parameter(Mandatory)] [string] $ScratchDirectory
    )

    $feed = New-Item -ItemType Directory -Path (Join-Path $ScratchDirectory "feed")
    Copy-Item -LiteralPath $PackageFile -Destination $feed.FullName
    $toolPath = Join-Path $ScratchDirectory "tool"
    $nugetConfig = Join-Path $ScratchDirectory "NuGet.Config"
    # Clear inherited sources, fallback folders and source mappings as well as the global cache.
    $escapedFeed = [Security.SecurityElement]::Escape($feed.FullName)
    Set-Content -LiteralPath $nugetConfig -Value "<configuration><packageSources><clear /><add key=""local"" value=""$escapedFeed"" /></packageSources><fallbackPackageFolders><clear /></fallbackPackageFolders><packageSourceMapping><clear /></packageSourceMapping></configuration>"
    $originalPackages = [Environment]::GetEnvironmentVariable('NUGET_PACKAGES')
    try {
        $env:NUGET_PACKAGES = Join-Path $ScratchDirectory "nuget-packages"
        dotnet tool install EdFi.Api.SecurityMetadataTools --tool-path $toolPath --source $feed.FullName `
            --configfile $nugetConfig --version $PackageVersion --no-cache | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Local-only tool installation failed ($LASTEXITCODE)." }
        $executable = Join-Path $toolPath $(if ($IsWindows) { "security-metadata-tools.exe" } else { "security-metadata-tools" })
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw "Local-only tool installation produced no executable."
        }
        return $executable
    }
    finally {
        if ($null -eq $originalPackages) { Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue }
        else { $env:NUGET_PACKAGES = $originalPackages }
    }
}

function Remove-SecurityMetadataToolsInstallation {
    <#
    .SYNOPSIS
        Removes the caller-owned installation directory, including a partially failed installation.
    #>
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Removes only the caller-owned disposable installation directory, from its finally block.')]
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $ScratchDirectory)

    if (Test-Path -LiteralPath $ScratchDirectory) {
        Remove-Item -LiteralPath $ScratchDirectory -Recurse -Force
    }
}

Export-ModuleMember -Function Install-SecurityMetadataToolsPackage, Remove-SecurityMetadataToolsInstallation
