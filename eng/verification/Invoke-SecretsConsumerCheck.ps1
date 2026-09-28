# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Packs EdFi.Api.Secrets into a local folder feed and compiles the scratch consumer against it.

.DESCRIPTION
    Asserting on a package's contents proves what is in the box; this proves an outside project can
    restore the packed artifact and implement both contracts against it, which is the only check
    that exercises the package the way an implementer will.

    Three things, in order:

    1. Packs the contract, at the Version its csproj declares, into -FeedDirectory. No version is
       passed on the command line: the contract is versioned on its own public surface, and its
       csproj ignores a global version override anyway.
    2. Asserts the AssemblyVersion of the DLL inside that nupkg equals the package version, because
       an assembly reference carries the assembly version, not the package version, and the two
       disagreeing would leave any version check on the host comparing the wrong thing.
    3. Restores the consumer from -FeedDirectory alone, into a fresh -NuGetPackagesDirectory, and
       builds it. The consumer's nuget.config declares no source, so the explicit feed is the only
       place EdFi.Api.Secrets can come from; the redirected global-packages folder is consulted
       before any source, so without it an already-extracted package of the same version would be
       validated in place of the one just packed.

    The on-config-pullrequest.yml lane calls this on a pull request that is not a draft and changed
    a config-relevant path, and unconditionally in the merge queue.
#>
[CmdletBinding()]
param(
    # Folder feed to pack into. Must not exist or be empty, so a stale nupkg cannot be restored
    # in place of the one packed here.
    [Parameter(Mandatory)]
    [string]
    $FeedDirectory,

    # Throwaway NuGet global-packages folder for the consumer restore. Must not exist or be empty.
    [Parameter(Mandatory)]
    [string]
    $NuGetPackagesDirectory,

    [ValidateSet("Debug", "Release")]
    [string]
    $Configuration = "Release",

    [string]
    $RepositoryRoot = (Join-Path $PSScriptRoot "../.."),

    [string]
    $ConsumerProject = (Join-Path $PSScriptRoot "SecretsConsumer")
)

$ErrorActionPreference = "Stop"

$packageId = "EdFi.Api.Secrets"
$assemblyName = "EdFi.DmsConfigurationService.Secrets"
$contractProject = Join-Path $RepositoryRoot "src/config/contracts/$assemblyName/$assemblyName.csproj"

function Initialize-EmptyDirectory {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $Description)

    if ((Test-Path -LiteralPath $Path) -and @(Get-ChildItem -LiteralPath $Path -Force).Count -gt 0) {
        throw "The $Description $Path is not empty. Pass a fresh path per run."
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    return (Resolve-Path -LiteralPath $Path).ProviderPath
}

$feed = Initialize-EmptyDirectory -Path $FeedDirectory -Description "feed directory"
$packages = Initialize-EmptyDirectory -Path $NuGetPackagesDirectory -Description "NuGet packages directory"

# The csproj declares Version exactly once; read it rather than repeat a literal that could drift.
$versionNodes = @(([xml](Get-Content -LiteralPath $contractProject -Raw)).SelectNodes("/Project/PropertyGroup/Version"))
if ($versionNodes.Count -ne 1 -or [string]::IsNullOrWhiteSpace($versionNodes[0].InnerText)) {
    throw "$contractProject must declare Version exactly once; found $($versionNodes.Count)."
}
$packageVersion = $versionNodes[0].InnerText.Trim()

# 1. Pack. The contract's own restore uses the repository's feeds and cache as any build does;
#    only the consumer restore below is isolated.
dotnet pack $contractProject -c $Configuration --nologo -o $feed
if ($LASTEXITCODE -ne 0) {
    throw "Packing $packageId failed."
}

$packageFile = Join-Path $feed "$packageId.$packageVersion.nupkg"
$packed = @(Get-ChildItem -LiteralPath $feed -Filter "*.nupkg")
if ($packed.Count -ne 1 -or -not (Test-Path -LiteralPath $packageFile)) {
    throw "Expected exactly $packageId.$packageVersion.nupkg in $feed; found: $($packed.Name -join ', ')."
}

# 2. The packed assembly's version must equal the package version.
$extracted = Join-Path $packages "_packed-$packageId"
[System.IO.Compression.ZipFile]::ExtractToDirectory($packageFile, $extracted)
$packedAssembly = Join-Path $extracted "lib/net10.0/$assemblyName.dll"
if (-not (Test-Path -LiteralPath $packedAssembly)) {
    throw "$packageFile does not carry lib/net10.0/$assemblyName.dll."
}

$assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($packedAssembly).Version
$declared = [version]$packageVersion
$expectedAssemblyVersion = [version]::new(
    $declared.Major,
    $declared.Minor,
    [Math]::Max($declared.Build, 0),
    [Math]::Max($declared.Revision, 0)
)
if ($assemblyVersion -ne $expectedAssemblyVersion) {
    throw "$assemblyName.dll inside $packageId.$packageVersion.nupkg carries AssemblyVersion $assemblyVersion; expected $expectedAssemblyVersion to match the package version."
}
Remove-Item -LiteralPath $extracted -Recurse -Force

# 3. Restore the consumer from the local feed only, into the fresh cache, and compile it.
$env:NUGET_PACKAGES = $packages

dotnet restore $ConsumerProject --source $feed -p:SecretsPackageVersion=$packageVersion
if ($LASTEXITCODE -ne 0) {
    throw "The scratch consumer failed to restore $packageId $packageVersion from $feed."
}

dotnet build $ConsumerProject -c $Configuration --no-restore --nologo -p:SecretsPackageVersion=$packageVersion
if ($LASTEXITCODE -ne 0) {
    throw "The scratch consumer failed to compile against $packageId $packageVersion."
}

Write-Output "Verified $packageId $packageVersion packs with AssemblyVersion $assemblyVersion and that the scratch consumer implements ISecretResolver and IClientSecretHasher against it from a local feed."
