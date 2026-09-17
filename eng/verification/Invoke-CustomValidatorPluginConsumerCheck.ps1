# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Compiles and runs the guide's samples against both packed contract nupkgs.

.DESCRIPTION
    The two sibling checks each prove one package resolves and compiles on its own. This proves the
    pair COMPOSE, and it is the only check that exercises the code CUSTOM-VALIDATION.md actually
    publishes: the samples in that readme are mirrored verbatim from this project's sources, and
    Assert-DocumentEmbeds.ps1 holds the two to each other, so a sample an implementer copies is one
    that has been compiled and run.

    Running it, not only building it, is the difference that matters. Compiling proves the packed
    contracts carry the dependency closure their signatures need. Running proves what the readme
    claims about the samples: that ContributeServices registers the validator in the shape DMS's
    startup guard accepts, that both documented options forms configure the options type, and that
    the rule behaves as described.

    Neither package is published yet, so only the per-PR lane calls this today. Whoever wires a
    publishing lane calls it there too, so that no version reaches a feed without the documented
    samples having been compiled and run against it.
#>
[CmdletBinding()]
param(
    # The EdFi.Api.CustomValidation version to restore, which must be the version just packed.
    [Parameter(Mandatory)]
    [string]
    $CustomValidationPackageVersion,

    # The EdFi.Api.Plugins version to restore, which must be the version just packed. It is the
    # contract's own surface version rather than the DMS release version, so callers read it with
    # Get-PluginsContractVersion rather than passing a literal.
    [Parameter(Mandatory)]
    [string]
    $PluginsPackageVersion,

    # A throwaway NuGet global-packages folder, which must not exist or must be empty.
    #
    # Redirecting it at all is what stops the build resolving an already-extracted package of the
    # same version from the real ~/.nuget/packages cache: the global-packages folder is consulted
    # before any source, so a cache hit would silently validate old bits. Requiring the folder to
    # be empty is what makes the redirection mean something, and it matters twice over here, since
    # the plugin contract's version is fixed at its own surface version and therefore extracts into
    # the same path on every run.
    [Parameter(Mandatory)]
    [string]
    $NuGetPackagesDirectory,

    [string]
    $ConsumerProject = (Join-Path $PSScriptRoot "CustomValidatorPluginConsumer")
)

$ErrorActionPreference = "Stop"

if (Test-Path -LiteralPath $NuGetPackagesDirectory) {
    $existingEntries = @(Get-ChildItem -LiteralPath $NuGetPackagesDirectory -Force)

    if ($existingEntries.Count -gt 0) {
        throw "Refusing to use $NuGetPackagesDirectory as a throwaway package cache: it is not empty, so a previously extracted EdFi.Api.CustomValidation $CustomValidationPackageVersion or EdFi.Api.Plugins $PluginsPackageVersion could satisfy this restore. Pass a fresh path per invocation."
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
        -p:CustomValidationPackageVersion=$CustomValidationPackageVersion `
        -p:PluginsPackageVersion=$PluginsPackageVersion

    if ($LASTEXITCODE -ne 0) {
        throw "The documented samples failed to compile against EdFi.Api.CustomValidation $CustomValidationPackageVersion and EdFi.Api.Plugins $PluginsPackageVersion"
    }

    # --no-build so this runs what was just compiled rather than recompiling with different
    # properties. The same two properties are still required: without them the default 0.0.0-local
    # values change the project's evaluated state and the run reports the build as out of date.
    dotnet run --project $ConsumerProject --no-build `
        -p:CustomValidationPackageVersion=$CustomValidationPackageVersion `
        -p:PluginsPackageVersion=$PluginsPackageVersion

    if ($LASTEXITCODE -ne 0) {
        throw "The documented samples compiled but their assertions failed against EdFi.Api.CustomValidation $CustomValidationPackageVersion and EdFi.Api.Plugins $PluginsPackageVersion"
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

Write-Output "Verified the documented samples compile and their assertions pass against EdFi.Api.CustomValidation $CustomValidationPackageVersion and EdFi.Api.Plugins $PluginsPackageVersion."
