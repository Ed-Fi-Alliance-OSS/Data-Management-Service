# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Compiles the identity scratch consumer against the packed EdFi.Api.Identity and EdFi.Api.Plugins nupkgs.

.DESCRIPTION
    Asserting on a package's contents proves what is in the box; this proves an outside project can
    actually restore it and compile against it, which is the only check that exercises the package
    the way an implementer will. The consumer implements every member of IIdentityService and
    subclasses EdFiApiPlugin to register the implementation, so a contract that packed a signature
    without the dependency closure that signature needs, or an interface member the provider sample
    does not implement, fails here (CS0535) rather than at an implementer's desk. It then runs the
    consumer, whose Program.cs asserts that the provider sample returns the payload shapes the guide
    documents, so the sample an implementer copies is one that behaves, not only one that compiles.

    Both contracts are restored because the samples the identity guide publishes are a provider and
    the plugin that registers it. The consumer's two embed regions are what the guide mirrors, so the
    sample an implementer copies is one that has been compiled.

    With -PublishedServiceIndexUrl it then also runs Invoke-PublishedContractConsumerCheck.ps1 for
    EdFi.Api.Identity, which compiles the same consumer against the package published on that feed
    once the declared version exists there, and says plainly when it does not yet. Before the first
    publication that is the expected state: the status reads "not yet on the feed" and the check
    passes. EdFi.Api.Plugins for that restore is the one this checkout packed, never the feed's: a
    pull request that moves the Plugins version declares a version no prerelease has published yet,
    and the published half verifies the published Identity, not Plugins.

    The per-PR lane calls this after packing both contracts, on a pull request that is not a draft
    and changed a DMS-relevant path, and unconditionally in the merge queue; the prerelease lane does
    not. What this proves about a contract version is proven before the pull request that produced
    that version merges, not in the job that publishes it.
#>
[CmdletBinding()]
param(
    # The EdFi.Api.Identity version to restore, which must be the version just packed. Callers read
    # it from the contract's own csproj via Get-IdentityContractVersion rather than passing a literal.
    [Parameter(Mandatory)]
    [string]
    $PackageVersion,

    # The EdFi.Api.Plugins version to restore, which must be the version just packed. Callers read
    # it from the contract's own props via Get-PluginsContractVersion rather than passing a literal.
    [Parameter(Mandatory)]
    [string]
    $PluginsPackageVersion,

    # A throwaway NuGet global-packages folder, which must not exist or must be empty.
    #
    # Redirecting it at all is what stops the build resolving an already-extracted package of the
    # same version from the real ~/.nuget/packages cache: the global-packages folder is consulted
    # before any source, so a cache hit would silently validate old bits. Requiring the folder to be
    # empty is what makes the redirection mean something. Both contracts' versions are fixed at their
    # own surface versions rather than moving with every build, so the same version strings are
    # extracted into the same paths on every run, and a reused scratch folder would be a cache hit
    # just like the one the redirection exists to avoid.
    [Parameter(Mandatory)]
    [string]
    $NuGetPackagesDirectory,

    [string]
    $ConsumerProject = (Join-Path $PSScriptRoot "IdentityConsumer"),

    # The feed whose published EdFi.Api.Identity the scratch consumer is also compiled against, once
    # the declared version exists there. Omitted, the published half does not run.
    [string]
    $PublishedServiceIndexUrl = "",

    # Throwaway NuGet global-packages folder for the published half. Required with
    # -PublishedServiceIndexUrl, and separate from -NuGetPackagesDirectory, which by then holds the
    # locally packed package under the same id and version.
    [string]
    $PublishedNuGetPackagesDirectory = "",

    # The packed EdFi.Api.Identity nupkg the published one is compared with. Defaults to the one
    # build-dms.ps1 Package leaves at the repository root, where the consumer's nuget.config also
    # reads it.
    [string]
    $PackageFile = "",

    # The folder holding the EdFi.Api.Plugins nupkg this checkout packed, which the published half
    # restores Plugins from. Defaults to the repository root, where build-dms.ps1 Package leaves it
    # and the consumer's nuget.config reads it.
    [string]
    $LocalPackageSource = (Join-Path $PSScriptRoot "../.."),

    # Where every package that is not an Ed-Fi contract comes from in the published half. Tests
    # point it at a local folder.
    [string]
    $PublicSource = "https://api.nuget.org/v3/index.json",

    # Passed through to the published check unchanged, so a test can decide what the feed answers
    # without a network and restore from a local folder standing in for it.
    [string]
    $PublishedRestoreSource = "",

    [scriptblock]
    $ResolvePackageBaseAddress,

    [scriptblock]
    $GetPublishedVersions,

    [scriptblock]
    $SavePublishedPackage
)

$ErrorActionPreference = "Stop"

if ($PublishedServiceIndexUrl -and -not $PublishedNuGetPackagesDirectory) {
    throw "-PublishedServiceIndexUrl needs -PublishedNuGetPackagesDirectory, a fresh folder of its own."
}

if (Test-Path -LiteralPath $NuGetPackagesDirectory) {
    $existingEntries = @(Get-ChildItem -LiteralPath $NuGetPackagesDirectory -Force)

    if ($existingEntries.Count -gt 0) {
        throw "Refusing to use $NuGetPackagesDirectory as a throwaway package cache: it is not empty, so a previously extracted EdFi.Api.Identity $PackageVersion or EdFi.Api.Plugins $PluginsPackageVersion could satisfy this restore. Pass a fresh path per invocation."
    }
}
else {
    New-Item -ItemType Directory -Path $NuGetPackagesDirectory -Force | Out-Null
}

# Captured so the caller's environment survives this script. Assigning $null to restore an
# originally-absent variable is not equivalent to removing it: on PowerShell 7.5 that leaves the
# name defined and empty, and an empty NUGET_PACKAGES is not the same as no NUGET_PACKAGES.
$nugetPackagesWasSet = Test-Path -LiteralPath "Env:NUGET_PACKAGES"
$previousNuGetPackages = if ($nugetPackagesWasSet) { $env:NUGET_PACKAGES } else { $null }

try {
    $env:NUGET_PACKAGES = $NuGetPackagesDirectory

    # Both versions are passed as properties. Neither of the csproj's 0.0.0-local defaults is
    # produced by anything, so pointing them at what the caller packed beats rewriting a tracked
    # file mid-job.
    dotnet build $ConsumerProject `
        -p:IdentityPackageVersion=$PackageVersion `
        -p:PluginsPackageVersion=$PluginsPackageVersion

    if ($LASTEXITCODE -ne 0) {
        throw "Scratch consumer failed to compile against EdFi.Api.Identity $PackageVersion and EdFi.Api.Plugins $PluginsPackageVersion"
    }

    # --no-build so this runs what was just compiled rather than recompiling with different
    # properties. The same two properties are still required: without them the default 0.0.0-local
    # values change the project's evaluated state and the run reports the build as out of date.
    dotnet run --project $ConsumerProject --no-build `
        -p:IdentityPackageVersion=$PackageVersion `
        -p:PluginsPackageVersion=$PluginsPackageVersion

    if ($LASTEXITCODE -ne 0) {
        throw "The scratch consumer compiled but the sample provider's assertions failed against EdFi.Api.Identity $PackageVersion and EdFi.Api.Plugins $PluginsPackageVersion"
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

$publishedStatus = "The published package was not checked: no -PublishedServiceIndexUrl was given."
if ($PublishedServiceIndexUrl) {
    if (-not $PackageFile) {
        $PackageFile = Join-Path $PSScriptRoot "../../EdFi.Api.Identity.$PackageVersion.nupkg"
    }

    $publishedArguments = @{
        PackageFile              = $PackageFile
        PackageVersion           = $PackageVersion
        ServiceIndexUrl          = $PublishedServiceIndexUrl
        NuGetPackagesDirectory   = $PublishedNuGetPackagesDirectory
        ConsumerProject          = $ConsumerProject
        PackageId                = "EdFi.Api.Identity"
        AssemblyName             = "EdFi.DataManagementService.Identity"
        VersionPropertyName      = "IdentityPackageVersion"
        AdditionalProperties     = @{ PluginsPackageVersion = $PluginsPackageVersion }
        LocalPackageIds          = @("EdFi.Api.Plugins")
        LocalPackageSource       = $LocalPackageSource
        PublicSource             = $PublicSource
    }
    if ($PublishedRestoreSource) {
        $publishedArguments.RestoreSource = $PublishedRestoreSource
    }
    if ($null -ne $ResolvePackageBaseAddress) {
        $publishedArguments.ResolvePackageBaseAddress = $ResolvePackageBaseAddress
    }
    if ($null -ne $GetPublishedVersions) {
        $publishedArguments.GetPublishedVersions = $GetPublishedVersions
    }
    if ($null -ne $SavePublishedPackage) {
        $publishedArguments.SavePublishedPackage = $SavePublishedPackage
    }

    $publishedStatus = & (Join-Path $PSScriptRoot "Invoke-PublishedContractConsumerCheck.ps1") @publishedArguments
}

Write-Output "Verified the scratch consumer compiles against EdFi.Api.Identity $PackageVersion and EdFi.Api.Plugins $PluginsPackageVersion."
Write-Output $publishedStatus
