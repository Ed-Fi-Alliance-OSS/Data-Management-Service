# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Compiles the scratch consumer against a packed EdFi.Api.CustomValidation nupkg.

.DESCRIPTION
    Asserting on a package's contents proves what is in the box; this proves an outside project can
    actually restore it and compile against it, which is the only check that exercises the package
    the way an implementer will.

    Only the per-PR lane calls this today, because the package is deliberately built and never
    published. Whoever wires the publishing lane calls it there too, so that no version can reach a
    feed without an outside project having compiled against it first.
#>
[CmdletBinding()]
param(
    # The package version to restore, which must be the version just packed.
    [Parameter(Mandatory)]
    [string]
    $PackageVersion,

    # Throwaway NuGet global-packages folder. Redirected so the build cannot resolve a stale,
    # already-extracted package of the same (MinVer height-based, collision-prone) version from the
    # restored ~/.nuget/packages cache: the global-packages folder is consulted before any source,
    # so a cache hit would otherwise silently validate old bits.
    [Parameter(Mandatory)]
    [string]
    $NuGetPackagesDirectory,

    [string]
    $ConsumerProject = (Join-Path $PSScriptRoot "CustomValidationConsumer")
)

$ErrorActionPreference = "Stop"

$env:NUGET_PACKAGES = $NuGetPackagesDirectory

# The consumer csproj declares its package version through the CustomValidationPackageVersion
# property. Nothing produces its 0.0.0-local default on its own, so point it at the version packed
# by the caller rather than rewriting a tracked file mid-job.
dotnet build $ConsumerProject -p:CustomValidationPackageVersion=$PackageVersion

if ($LASTEXITCODE -ne 0) {
    throw "Scratch consumer failed to compile against EdFi.Api.CustomValidation $PackageVersion"
}

Write-Output "Verified the scratch consumer compiles against EdFi.Api.CustomValidation $PackageVersion."
