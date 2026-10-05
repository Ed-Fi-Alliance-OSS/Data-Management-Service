# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Compiles the scratch consumer against EdFi.Api.Secrets as published on a feed, once the version
    the contract declares exists there.

.DESCRIPTION
    The local check proves the package just packed restores and compiles. This proves the same of
    the package an implementer actually downloads, which is a different artifact: it was packed by an
    earlier prerelease, and only the policy in Invoke-ContractPublishCheck.ps1 says it still matches.

    Whether the declared version is on the feed is decided by that same check, run against the
    package just packed, so the rules about what a feed response means are stated once:

      - absent: the version has not been published yet, which is the expected state until the first
        prerelease pushes it. Nothing is restored, and the output says so in as many words. It is
        not reported as a pass of this check, because nothing was compiled against a published
        package.
      - unchanged: the published package matches what was just packed in public surface, XML
        documentation and declared dependencies. The readme is deliberately not compared, so a
        documentation correction to the guide since publication does not fail this. The consumer is
        then restored from the feed alone, into a fresh global-packages folder, and built, and the
        AssemblyVersion of the DLL NuGet extracted from the published nupkg is asserted to equal the
        package version.
      - changed: the check throws, naming the id, the version and which comparison differed.

    A feed that cannot be read, or answers with something malformed, throws as well. It is never
    read as an absent version, so an outage cannot turn this check into a silent skip.

.OUTPUTS
    One status line on the success stream: either that the published package was verified, or that
    the version is not yet on the feed and nothing was compiled against it.
#>
[CmdletBinding()]
param(
    # The EdFi.Api.Secrets nupkg just packed from this checkout, which the published one is compared
    # with before anything is restored.
    [Parameter(Mandatory)]
    [string]
    $PackageFile,

    # The version the contract declares, read by the caller with Get-SecretsContractVersion.
    [Parameter(Mandatory)]
    [string]
    $PackageVersion,

    # The feed's NuGet v3 service index.
    [Parameter(Mandatory)]
    [string]
    $ServiceIndexUrl,

    # Throwaway NuGet global-packages folder for the restore from the feed. Must not exist or be
    # empty, and must not be the folder the local check restored into: that one already holds a
    # package of the same id and version, which NuGet would use instead of downloading.
    [Parameter(Mandatory)]
    [string]
    $NuGetPackagesDirectory,

    # The source the consumer restores from. Defaults to the service index, which is the only value
    # a lane passes; a test passes a local folder standing in for the feed.
    [string]
    $RestoreSource = $ServiceIndexUrl,

    [ValidateSet("Debug", "Release")]
    [string]
    $Configuration = "Release",

    [string]
    $ConsumerProject = (Join-Path $PSScriptRoot "SecretsConsumer"),

    # Passed through to Invoke-ContractPublishCheck.ps1 unchanged, so a test can decide what the
    # feed answers without a network. A lane passes none and the real requests are made.
    [scriptblock]
    $ResolvePackageBaseAddress,

    [scriptblock]
    $GetPublishedVersions,

    [scriptblock]
    $SavePublishedPackage
)

$ErrorActionPreference = "Stop"

$packageId = "EdFi.Api.Secrets"
$assemblyName = "EdFi.DmsConfigurationService.Secrets"

if ((Test-Path -LiteralPath $NuGetPackagesDirectory) -and @(Get-ChildItem -LiteralPath $NuGetPackagesDirectory -Force).Count -gt 0) {
    throw "The NuGet packages directory $NuGetPackagesDirectory is not empty. Pass a fresh path per run."
}

New-Item -ItemType Directory -Path $NuGetPackagesDirectory -Force | Out-Null
$packages = (Resolve-Path -LiteralPath $NuGetPackagesDirectory).ProviderPath

$checkArguments = @{
    PackageFile      = $PackageFile
    PackageId        = $packageId
    PackageVersion   = $PackageVersion
    WorkingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "secrets-published-check-$([guid]::NewGuid().ToString('N'))"
    ServiceIndexUrl  = $ServiceIndexUrl
}

if ($null -ne $ResolvePackageBaseAddress) {
    $checkArguments.ResolvePackageBaseAddress = $ResolvePackageBaseAddress
}
if ($null -ne $GetPublishedVersions) {
    $checkArguments.GetPublishedVersions = $GetPublishedVersions
}
if ($null -ne $SavePublishedPackage) {
    $checkArguments.SavePublishedPackage = $SavePublishedPackage
}

$decision = & (Join-Path $PSScriptRoot "Invoke-ContractPublishCheck.ps1") @checkArguments

if ($decision.Reason -eq "absent") {
    return "Not verified against the feed: $packageId $PackageVersion is not yet on $ServiceIndexUrl, so nothing was compiled against a published package. This is the expected state until the first prerelease publishes it."
}

if ($decision.Reason -ne "unchanged") {
    throw "Invoke-ContractPublishCheck.ps1 returned reason '$($decision.Reason)' for $packageId $PackageVersion; expected absent or unchanged."
}

# The caller's NUGET_PACKAGES is put back afterwards, and an originally-absent variable is removed
# rather than assigned $null, which PowerShell 7.5 leaves defined and empty.
$nugetPackagesWasSet = Test-Path -LiteralPath "Env:NUGET_PACKAGES"
$previousNuGetPackages = if ($nugetPackagesWasSet) { $env:NUGET_PACKAGES } else { $null }

try {
    $env:NUGET_PACKAGES = $packages

    # The consumer's nuget.config clears every source, so the feed named here is the only place the
    # package can come from, and the fresh global-packages folder means it is downloaded. Build
    # output goes to the host so the success stream carries only the status line.
    dotnet restore $ConsumerProject --source $RestoreSource -p:SecretsPackageVersion=$PackageVersion | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "The scratch consumer failed to restore the published $packageId $PackageVersion from $RestoreSource."
    }

    dotnet build $ConsumerProject -c $Configuration --no-restore --nologo -p:SecretsPackageVersion=$PackageVersion | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "The scratch consumer failed to compile against the published $packageId $PackageVersion."
    }
}
finally {
    if ($nugetPackagesWasSet) {
        $env:NUGET_PACKAGES = $previousNuGetPackages
    }
    elseif (Test-Path -LiteralPath "Env:NUGET_PACKAGES") {
        Remove-Item -LiteralPath "Env:NUGET_PACKAGES"
    }
}

# What NuGet extracted from the downloaded nupkg, which is the assembly an implementer compiles
# against. Its folder is named for the normalized, lower-cased id and version.
$publishedAssembly = Join-Path $packages "$($packageId.ToLowerInvariant())/$($PackageVersion.ToLowerInvariant())/lib/net10.0/$assemblyName.dll"
if (-not (Test-Path -LiteralPath $publishedAssembly)) {
    throw "The restore did not extract $assemblyName.dll for $packageId $PackageVersion at $publishedAssembly."
}

$assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($publishedAssembly).Version
$declared = [version]$PackageVersion
$expectedAssemblyVersion = [version]::new(
    $declared.Major,
    $declared.Minor,
    [Math]::Max($declared.Build, 0),
    [Math]::Max($declared.Revision, 0)
)
if ($assemblyVersion -ne $expectedAssemblyVersion) {
    throw "$assemblyName.dll in the published $packageId $PackageVersion carries AssemblyVersion $assemblyVersion; expected $expectedAssemblyVersion to match the package version."
}

return "Verified the published $packageId $PackageVersion from ${ServiceIndexUrl}: unchanged from what was just packed, the scratch consumer restores it from the feed alone and compiles, and its assembly carries AssemblyVersion $assemblyVersion."
