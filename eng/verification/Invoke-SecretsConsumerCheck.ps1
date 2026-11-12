# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Packs EdFi.Api.Secrets into a local folder feed and compiles the scratch consumer and the
    implementer guide's worked examples against it, and, when given a feed, the scratch consumer
    against the package published there.

.DESCRIPTION
    Asserting on a package's contents proves what is in the box; this proves an outside project can
    restore the packed artifact and implement both contracts against it, which is the only check
    that exercises the package the way an implementer will.

    Up to six things, in order; the sixth only when a feed is given:

    1. Packs the contract, at the Version its csproj declares, into -FeedDirectory. No version is
       passed on the command line: the contract is versioned on its own public surface, and its
       csproj ignores a global version override anyway. With -NoBuild it packs what an earlier build
       left in the output folder instead, which is how the prerelease lane proves that a release
       build with explicit versions still packs the contract at its own.
    2. Asserts the AssemblyVersion of the DLL inside that nupkg equals the package version, because
       an assembly reference carries the assembly version, not the package version, and the two
       disagreeing would leave any version check on the host comparing the wrong thing.
    3. Asserts the nupkg carries the implementer guide as its readme, byte for byte. The readme is
       all an implementer resolving the package from a feed sees of it, so a package that dropped
       it, or packed a stale copy, is one an implementer cannot work from.
    4. Restores the consumer from -FeedDirectory alone, into a fresh -NuGetPackagesDirectory, and
       builds it. The consumer's nuget.config declares no source, so the explicit feed is the only
       place EdFi.Api.Secrets can come from; the redirected global-packages folder is consulted
       before any source, so without it an already-extracted package of the same version would be
       validated in place of the one just packed.
    5. Packs EdFi.Api.Plugins into the same feed and builds -ExamplesProject, the guide's worked
       examples, against both packed contracts and the pinned vendor SDKs it declares. Its
       nuget.config maps both Ed-Fi ids to the feed this script names through
       SECRETS_VERIFICATION_FEED and everything else to nuget.org. Assert-DocumentEmbeds.ps1 holds
       the guide's blocks to that project's sources, so the examples an implementer copies are ones
       that compile. They are compiled only; nothing here runs them against a vault.
    6. With -PublishedServiceIndexUrl, runs Invoke-PublishedContractConsumerCheck.ps1, which compiles
       the scratch consumer against the package published on that feed once the declared version
       exists there, and says plainly when it does not yet.

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
    $ConsumerProject = (Join-Path $PSScriptRoot "SecretsConsumer"),

    [string]
    $ExamplesProject = (Join-Path $PSScriptRoot "SecretsPluginExamples"),

    # Pack the contract without building it, from the output an earlier build in the same
    # -Configuration left behind.
    [switch]
    $NoBuild,

    # The feed whose published EdFi.Api.Secrets the scratch consumer is also compiled against, once
    # the declared version exists there. Omitted, step 6 does not run.
    [string]
    $PublishedServiceIndexUrl = "",

    # Throwaway NuGet global-packages folder for step 6. Required with -PublishedServiceIndexUrl, and
    # separate from -NuGetPackagesDirectory, which by then holds the locally packed package under the
    # same id and version.
    [string]
    $PublishedNuGetPackagesDirectory = ""
)

$ErrorActionPreference = "Stop"

$packageId = "EdFi.Api.Secrets"
$assemblyName = "EdFi.DmsConfigurationService.Secrets"
$contractProject = Join-Path $RepositoryRoot "src/config/contracts/$assemblyName/$assemblyName.csproj"
$contractReadme = Join-Path $RepositoryRoot "src/config/contracts/$assemblyName/README.md"
$pluginsProject = Join-Path $RepositoryRoot "src/plugins/EdFi.Api.Plugins/EdFi.Api.Plugins.csproj"

Import-Module (Join-Path $RepositoryRoot "package-helpers.psm1") -Force

function Initialize-EmptyDirectory {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $Description)

    if ((Test-Path -LiteralPath $Path) -and @(Get-ChildItem -LiteralPath $Path -Force).Count -gt 0) {
        throw "The $Description $Path is not empty. Pass a fresh path per run."
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    return (Resolve-Path -LiteralPath $Path).ProviderPath
}

if ($PublishedServiceIndexUrl -and -not $PublishedNuGetPackagesDirectory) {
    throw "-PublishedServiceIndexUrl needs -PublishedNuGetPackagesDirectory, a fresh folder of its own."
}

$feed = Initialize-EmptyDirectory -Path $FeedDirectory -Description "feed directory"
$packages = Initialize-EmptyDirectory -Path $NuGetPackagesDirectory -Description "NuGet packages directory"

# Read where every lane reads it, rather than repeat a literal that could drift.
$packageVersion = Get-SecretsContractVersion -ProjectPath $contractProject

# 1. Pack. The contract's own restore uses the repository's feeds and cache as any build does;
#    only the consumer restore below is isolated.
$packArguments = @($contractProject, "-c", $Configuration, "--nologo", "-o", $feed)
if ($NoBuild) {
    $packArguments += "--no-build"
}
dotnet pack @packArguments
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

# 3. The packed readme must be the implementer guide as committed. Compared byte for byte, because a
#    check for some expected phrase would pass on a stale copy that still happened to contain it.
$nuspecFile = @(Get-ChildItem -LiteralPath $extracted -Filter "*.nuspec")
if ($nuspecFile.Count -ne 1) {
    throw "$packageFile must carry exactly one .nuspec; found $($nuspecFile.Count)."
}
$nuspec = [xml](Get-Content -LiteralPath $nuspecFile[0].FullName -Raw)
$declaredReadme = $nuspec.package.metadata.readme
if ($declaredReadme -ne "README.md") {
    throw "$packageId.$packageVersion.nupkg declares readme '$declaredReadme'; expected README.md, the implementer guide."
}
$packedReadme = Join-Path $extracted "README.md"
if (-not (Test-Path -LiteralPath $packedReadme)) {
    throw "$packageId.$packageVersion.nupkg declares README.md as its readme but does not carry the file."
}
$packedReadmeHash = (Get-FileHash -LiteralPath $packedReadme -Algorithm SHA256).Hash
$sourceReadmeHash = (Get-FileHash -LiteralPath $contractReadme -Algorithm SHA256).Hash
if ($packedReadmeHash -ne $sourceReadmeHash) {
    throw "The README.md inside $packageId.$packageVersion.nupkg differs from $contractReadme."
}
Remove-Item -LiteralPath $extracted -Recurse -Force

# 5's pack happens here, before the throwaway cache is installed, so the contract's own restore uses
# the repository's feeds and cache as any build does. It goes into the same feed after the
# exactly-one-nupkg check above, which is about the package under verification. Its version is the
# plugin contract's own, read where every lane reads it.
$pluginsVersion = Get-PluginsContractVersion
dotnet pack $pluginsProject -c $Configuration --nologo -o $feed
if ($LASTEXITCODE -ne 0) {
    throw "Packing EdFi.Api.Plugins failed."
}
if (-not (Test-Path -LiteralPath (Join-Path $feed "EdFi.Api.Plugins.$pluginsVersion.nupkg"))) {
    throw "Expected EdFi.Api.Plugins.$pluginsVersion.nupkg in $feed after packing it."
}

# 4. Restore the consumer from the local feed only, into the fresh cache, and compile it. The
#    caller's NUGET_PACKAGES is restored afterwards so an interactive session is not left pointing
#    at the throwaway folder. An originally-absent variable is removed rather than assigned $null:
#    on PowerShell 7.5 that leaves the name defined and empty, which NuGet does not treat as unset.
$nugetPackagesWasSet = Test-Path -LiteralPath "Env:NUGET_PACKAGES"
$previousNuGetPackages = if ($nugetPackagesWasSet) { $env:NUGET_PACKAGES } else { $null }

try {
    $env:NUGET_PACKAGES = $packages

    dotnet restore $ConsumerProject --source $feed -p:SecretsPackageVersion=$packageVersion
    if ($LASTEXITCODE -ne 0) {
        throw "The scratch consumer failed to restore $packageId $packageVersion from $feed."
    }

    dotnet build $ConsumerProject -c $Configuration --no-restore --nologo -p:SecretsPackageVersion=$packageVersion
    if ($LASTEXITCODE -ne 0) {
        throw "The scratch consumer failed to compile against $packageId $packageVersion."
    }

    # 5. The worked examples. The feed variable is set only for this restore and put back as it was
    #    afterwards, for the same reason NUGET_PACKAGES is.
    $feedVariableWasSet = Test-Path -LiteralPath "Env:SECRETS_VERIFICATION_FEED"
    $previousFeedVariable = if ($feedVariableWasSet) { $env:SECRETS_VERIFICATION_FEED } else { $null }
    $env:SECRETS_VERIFICATION_FEED = $feed
    try {
        dotnet restore $ExamplesProject -p:SecretsPackageVersion=$packageVersion -p:PluginsPackageVersion=$pluginsVersion
        if ($LASTEXITCODE -ne 0) {
            throw "The worked examples failed to restore $packageId $packageVersion and EdFi.Api.Plugins $pluginsVersion from $feed."
        }
    }
    finally {
        if ($feedVariableWasSet) {
            $env:SECRETS_VERIFICATION_FEED = $previousFeedVariable
        }
        elseif (Test-Path -LiteralPath "Env:SECRETS_VERIFICATION_FEED") {
            Remove-Item -LiteralPath "Env:SECRETS_VERIFICATION_FEED"
        }
    }

    dotnet build $ExamplesProject -c $Configuration --no-restore --nologo -p:SecretsPackageVersion=$packageVersion -p:PluginsPackageVersion=$pluginsVersion
    if ($LASTEXITCODE -ne 0) {
        throw "The worked examples failed to compile against $packageId $packageVersion and EdFi.Api.Plugins $pluginsVersion."
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
    # 6. Against the package published on the feed, if the declared version is there yet.
    $publishedStatus = & (Join-Path $PSScriptRoot "Invoke-PublishedContractConsumerCheck.ps1") `
        -PackageFile $packageFile `
        -PackageVersion $packageVersion `
        -ServiceIndexUrl $PublishedServiceIndexUrl `
        -NuGetPackagesDirectory $PublishedNuGetPackagesDirectory `
        -Configuration $Configuration `
        -ConsumerProject $ConsumerProject
}

Write-Output "Verified $packageId $packageVersion packs with AssemblyVersion $assemblyVersion and its implementer guide as readme, that the scratch consumer implements ISecretResolver and IClientSecretHasher against it from a local feed, and that the guide's worked examples compile against it and EdFi.Api.Plugins $pluginsVersion."
Write-Output $publishedStatus
