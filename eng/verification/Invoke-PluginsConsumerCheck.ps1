# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Compiles the scratch consumer against a packed EdFi.Api.Plugins nupkg.

.DESCRIPTION
    Asserting on a package's contents proves what is in the box; this proves an outside project can
    actually restore it and compile against it, which is the only check that exercises the package
    the way an implementer will. The consumer subclasses EdFiApiPlugin and overrides both members,
    so a contract that packed a signature without the dependency closure that signature needs would
    fail here rather than at an implementer's desk.

    Only the per-PR lane calls this today, because the package is deliberately built and never
    published. Whoever wires the publishing lane calls it there too, so that no version can reach a
    feed without an outside project having compiled against it first.
#>
[CmdletBinding()]
param(
    # The package version to restore, which must be the version just packed. Callers read it from
    # the contract's own props via Get-PluginsContractVersion rather than passing a literal.
    [Parameter(Mandatory)]
    [string]
    $PackageVersion,

    # A throwaway NuGet global-packages folder, which must not exist or must be empty.
    #
    # Redirecting it at all is what stops the build resolving an already-extracted package of the
    # same version from the real ~/.nuget/packages cache: the global-packages folder is consulted
    # before any source, so a cache hit would silently validate old bits. Requiring the folder to be
    # empty is what makes the redirection mean something. This contract's version is fixed at its
    # own surface version rather than moving with every build, so the same version string is
    # extracted into the same path on every run, and a reused scratch folder would be a cache hit
    # just like the one the redirection exists to avoid.
    [Parameter(Mandatory)]
    [string]
    $NuGetPackagesDirectory,

    [string]
    $ConsumerProject = (Join-Path $PSScriptRoot "PluginsConsumer")
)

$ErrorActionPreference = "Stop"

if (Test-Path -LiteralPath $NuGetPackagesDirectory) {
    $existingEntries = @(Get-ChildItem -LiteralPath $NuGetPackagesDirectory -Force)

    if ($existingEntries.Count -gt 0) {
        throw "Refusing to use $NuGetPackagesDirectory as a throwaway package cache: it is not empty, so a previously extracted EdFi.Api.Plugins $PackageVersion could satisfy this restore. Pass a fresh path per invocation."
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

    # The consumer csproj declares its package version through the PluginsPackageVersion property.
    # Nothing produces its 0.0.0-local default on its own, so point it at the version packed by the
    # caller rather than rewriting a tracked file mid-job.
    dotnet build $ConsumerProject -p:PluginsPackageVersion=$PackageVersion

    if ($LASTEXITCODE -ne 0) {
        throw "Scratch consumer failed to compile against EdFi.Api.Plugins $PackageVersion"
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

Write-Output "Verified the scratch consumer compiles against EdFi.Api.Plugins $PackageVersion."
