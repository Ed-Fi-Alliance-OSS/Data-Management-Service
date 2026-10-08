# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# The published-package consumer check, driven through every outcome without a network.
#
# The policy itself is Invoke-ContractPublishCheck.ps1's, and its generic cases live in that script's
# own suites. What is particular here is the wiring: the real EdFi.Api.Secrets package, whose id is
# not its assembly name, reaching each of the publish check's outcomes, and what this script then
# does or deliberately does not do. The feed is three injected script blocks and a local folder
# standing in for it as the restore source.

BeforeAll {
    $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
    $script:checker = Join-Path $script:repoRoot "eng/verification/Invoke-PublishedContractConsumerCheck.ps1"
    $script:contractProject = Join-Path $script:repoRoot `
        "src/config/contracts/EdFi.DmsConfigurationService.Secrets/EdFi.DmsConfigurationService.Secrets.csproj"

    Import-Module (Join-Path $script:repoRoot "package-helpers.psm1") -Force
    $script:version = Get-SecretsContractVersion
    $script:packageName = "EdFi.Api.Secrets.$($script:version).nupkg"

    $script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms1555-published-secrets-$([guid]::NewGuid().ToString('N'))"

    # Two packs of the same source: one is what this checkout just packed, the other the copy the
    # feed serves, so the restore reads a different file from the one the comparison was given.
    $script:packedDirectory = Join-Path $script:fixtureRoot "packed"
    $script:publishedDirectory = Join-Path $script:fixtureRoot "published"
    $script:changedDirectory = Join-Path $script:fixtureRoot "changed"

    foreach ($output in @($script:packedDirectory, $script:publishedDirectory)) {
        dotnet pack $script:contractProject -c Release --nologo -o $output | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Packing EdFi.Api.Secrets into $output failed."
        }
    }

    $script:packedFile = Join-Path $script:packedDirectory $script:packageName
    $script:publishedFile = Join-Path $script:publishedDirectory $script:packageName

    # A published copy whose XML documentation says something the packed one does not.
    New-Item -ItemType Directory -Path $script:changedDirectory -Force | Out-Null
    $script:changedFile = Join-Path $script:changedDirectory $script:packageName
    Copy-Item -LiteralPath $script:publishedFile -Destination $script:changedFile

    Add-Type -AssemblyName System.IO.Compression
    $archive = [System.IO.Compression.ZipFile]::Open($script:changedFile, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $entryName = "lib/net10.0/EdFi.DmsConfigurationService.Secrets.xml"
        $entry = $archive.GetEntry($entryName)
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { $documentation = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $entry.Delete()

        $changedDocumentation = $documentation.Replace(
            "Resolves a named secret for a tenant",
            "Resolves a named secret for a tenant, reworded after publication"
        )
        if ($changedDocumentation -eq $documentation) {
            throw "The fixture's rewording did not apply; the contract's ISecretResolver summary no longer carries the phrase it replaces."
        }
        $writer = [System.IO.StreamWriter]::new($archive.CreateEntry($entryName).Open())
        try { $writer.Write($changedDocumentation) } finally { $writer.Dispose() }
    }
    finally {
        $archive.Dispose()
    }

    $script:baseAddress = "https://feed.invalid/flat2"

    # Each run gets a fresh packages folder, and the feed answers from what the case says.
    function Invoke-Check {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Read inside GetNewClosure bodies, and the closure parameters are the injected seam signature.')]
        param(
            [Parameter(Mandatory)][hashtable] $Feed,
            [string] $RestoreSource = $script:publishedDirectory
        )

        $packages = Join-Path $script:fixtureRoot "packages-$([guid]::NewGuid().ToString('N'))"
        $baseAddress = $script:baseAddress
        $found = $Feed.Found
        $versions = $Feed.Versions
        $source = $Feed.Source
        $unreachable = $Feed.Unreachable

        $arguments = @{
            PackageFile               = $script:packedFile
            PackageVersion            = $script:version
            ServiceIndexUrl           = "https://feed.invalid/index.json"
            NuGetPackagesDirectory    = $packages
            RestoreSource             = $RestoreSource
            ResolvePackageBaseAddress = {
                param([string] $IndexUrl, [string] $ApiKey)
                if ($unreachable) {
                    throw "The feed's service index at $IndexUrl could not be read: 401 Unauthorized."
                }
                return $baseAddress
            }.GetNewClosure()
            GetPublishedVersions      = {
                param([string] $BaseAddress, [string] $NormalizedId, [string] $ApiKey)
                return @{ Found = $found; Versions = $versions }
            }.GetNewClosure()
            SavePublishedPackage      = {
                param([string] $BaseAddress, [string] $NormalizedId, [string] $NormalizedVersion, [string] $Destination, [string] $ApiKey)
                Copy-Item -LiteralPath $source -Destination $Destination
            }.GetNewClosure()
        }

        return @{
            Packages = $packages
            Output   = @(& $script:checker @arguments)
        }
    }
}

AfterAll {
    if (Test-Path -LiteralPath $script:fixtureRoot) {
        Remove-Item -LiteralPath $script:fixtureRoot -Recurse -Force
    }
}

Describe "Invoke-PublishedContractConsumerCheck" {
    Context "The package id has never been published" {
        BeforeAll {
            $script:absent = Invoke-Check -Feed @{ Found = $false; Versions = @() }
        }

        It "says the version is not yet on the feed and that nothing was compiled against it" {
            $script:absent.Output | Should -HaveCount 1
            $script:absent.Output[0] | Should -BeLike "Not verified against the feed: EdFi.Api.Secrets $($script:version) is not yet on *nothing was compiled against a published package*"
        }

        It "restores nothing" {
            @(Get-ChildItem -LiteralPath $script:absent.Packages -Force) | Should -HaveCount 0
        }
    }

    Context "Other versions are published but not the declared one" {
        It "reports the declared version as not yet on the feed" {
            $result = Invoke-Check -Feed @{ Found = $true; Versions = @("0.9.0") }

            $result.Output[0] | Should -BeLike "Not verified against the feed: EdFi.Api.Secrets $($script:version) is not yet on *"
        }
    }

    Context "The feed cannot be read" {
        It "fails rather than reporting the version as not yet published" {
            { Invoke-Check -Feed @{ Unreachable = $true } } |
                Should -Throw -ExpectedMessage "*could not be read: 401 Unauthorized*"
        }
    }

    Context "The declared version is published, unchanged" {
        BeforeAll {
            $script:unchanged = Invoke-Check -Feed @{
                Found    = $true
                Versions = @($script:version)
                Source   = $script:publishedFile
            }
        }

        It "reports the published package verified" {
            $script:unchanged.Output | Should -HaveCount 1
            $script:unchanged.Output[0] | Should -BeLike "Verified the published EdFi.Api.Secrets $($script:version) from *"
        }

        It "compiled the consumer against the package restored from the feed, at the package version" {
            $assembly = Join-Path $script:unchanged.Packages `
                "edfi.api.secrets/$($script:version)/lib/net10.0/EdFi.DmsConfigurationService.Secrets.dll"
            $declared = [version]$script:version

            [System.Reflection.AssemblyName]::GetAssemblyName($assembly).Version |
                Should -Be ([version]::new($declared.Major, $declared.Minor, [Math]::Max($declared.Build, 0), 0))
        }
    }

    Context "The declared version is published, and its XML documentation differs" {
        It "fails naming the id, the version, and the comparison that differed" {
            { Invoke-Check -Feed @{ Found = $true; Versions = @($script:version); Source = $script:changedFile } } |
                Should -Throw -ExpectedMessage "EdFi.Api.Secrets $($script:version) is already published and its XML documentation differ(s) from what was just packed.*"
        }
    }

    Context "The restore source does not hold the published package" {
        It "fails at restore rather than compiling against anything else" {
            $empty = Join-Path $script:fixtureRoot "empty-source"
            New-Item -ItemType Directory -Path $empty -Force | Out-Null

            {
                Invoke-Check -RestoreSource $empty -Feed @{
                    Found    = $true
                    Versions = @($script:version)
                    Source   = $script:publishedFile
                }
            } | Should -Throw -ExpectedMessage "The scratch consumer failed to restore the published EdFi.Api.Secrets*"
        }
    }
}

# The same script, parameterized for EdFi.Api.Identity. That consumer restores two Ed-Fi contracts,
# and carries its own package source mapping, so what is particular here is that Identity comes from
# the feed folder only, Plugins from the locally packed folder only, and everything else from
# elsewhere, and that Secrets' plain --source restore is untouched. Plugins is the local pack because
# a pull request that moves the Plugins version declares one no prerelease has published yet. The
# packs are the ones build-dms.ps1 Package leaves at the repository root, which the pull request
# lane has made by the time this suite runs.
Describe "Invoke-PublishedContractConsumerCheck for EdFi.Api.Identity" {
    BeforeAll {
        $script:identityVersion = Get-IdentityContractVersion
        $script:pluginsVersion = Get-PluginsContractVersion
        $script:identityName = "EdFi.Api.Identity.$($script:identityVersion).nupkg"
        $script:pluginsName = "EdFi.Api.Plugins.$($script:pluginsVersion).nupkg"
        $script:identityPacked = Join-Path $script:repoRoot $script:identityName
        $script:pluginsPacked = Join-Path $script:repoRoot $script:pluginsName

        foreach ($required in @($script:identityPacked, $script:pluginsPacked)) {
            if (-not (Test-Path -LiteralPath $required)) {
                throw "$required is missing. Run ./build-dms.ps1 Package -PackageTarget Identity and -PackageTarget Plugins first."
            }
        }

        # What the feed serves: the identity pack and nothing else, so a Plugins version the feed
        # does not have is the case every restore below exercises. The local folder holds what this
        # checkout packed: Plugins, and an Identity of the same version that the restore must not
        # take in place of the published one.
        $script:identityFeed = Join-Path $script:fixtureRoot "identity-feed"
        $script:identityChangedFeed = Join-Path $script:fixtureRoot "identity-changed-feed"
        $script:pluginsOnlyFeed = Join-Path $script:fixtureRoot "plugins-only-feed"
        $script:fullFeed = Join-Path $script:fixtureRoot "identity-and-plugins-feed"
        $script:localPacks = Join-Path $script:fixtureRoot "local-packs"
        $script:emptyLocalPacks = Join-Path $script:fixtureRoot "empty-local-packs"
        foreach ($folder in @($script:identityFeed, $script:identityChangedFeed, $script:pluginsOnlyFeed, $script:fullFeed, $script:localPacks, $script:emptyLocalPacks)) {
            New-Item -ItemType Directory -Path $folder -Force | Out-Null
        }
        Copy-Item -LiteralPath $script:identityPacked -Destination $script:identityFeed
        Copy-Item -LiteralPath $script:identityPacked -Destination $script:fullFeed
        Copy-Item -LiteralPath $script:pluginsPacked -Destination $script:fullFeed
        Copy-Item -LiteralPath $script:pluginsPacked -Destination $script:pluginsOnlyFeed
        Copy-Item -LiteralPath $script:pluginsPacked -Destination $script:localPacks
        Copy-Item -LiteralPath $script:identityPacked -Destination $script:localPacks
        $script:identityChangedFile = Join-Path $script:identityChangedFeed $script:identityName
        Copy-Item -LiteralPath $script:identityPacked -Destination $script:identityChangedFile

        Add-Type -AssemblyName System.IO.Compression
        $archive = [System.IO.Compression.ZipFile]::Open($script:identityChangedFile, [System.IO.Compression.ZipArchiveMode]::Update)
        try {
            $entryName = "lib/net10.0/EdFi.DataManagementService.Identity.xml"
            $entry = $archive.GetEntry($entryName)
            $reader = [System.IO.StreamReader]::new($entry.Open())
            try { $documentation = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $entry.Delete()

            $changedDocumentation = $documentation.Replace(
                "The token a client polls through",
                "The reworded token a client polls through"
            )
            if ($changedDocumentation -eq $documentation) {
                throw "The fixture's rewording did not apply; the identity contract's IdentityAsyncResult summary no longer carries the phrase it replaces."
            }
            $writer = [System.IO.StreamWriter]::new($archive.CreateEntry($entryName).Open())
            try { $writer.Write($changedDocumentation) } finally { $writer.Dispose() }
        }
        finally {
            $archive.Dispose()
        }

        function Invoke-IdentityCheck {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Read inside GetNewClosure bodies, and the closure parameters are the injected seam signature.')]
            param(
                [Parameter(Mandatory)][hashtable] $Feed,
                [string] $RestoreSource = $script:identityFeed,
                [string] $LocalPackageSource = $script:localPacks,
                [string] $PackageVersion = $script:identityVersion,
                [string] $AssemblyName = "EdFi.DataManagementService.Identity"
            )

            $packages = Join-Path $script:fixtureRoot "identity-packages-$([guid]::NewGuid().ToString('N'))"
            $found = $Feed.Found
            $versions = $Feed.Versions
            $source = $Feed.Source
            $unreachable = $Feed.Unreachable

            $arguments = @{
                PackageFile               = $script:identityPacked
                PackageVersion            = $PackageVersion
                ServiceIndexUrl           = "https://feed.invalid/index.json"
                NuGetPackagesDirectory    = $packages
                RestoreSource             = $RestoreSource
                ConsumerProject           = Join-Path $script:repoRoot "eng/verification/IdentityConsumer"
                PackageId                 = "EdFi.Api.Identity"
                AssemblyName              = $AssemblyName
                VersionPropertyName       = "IdentityPackageVersion"
                AdditionalProperties      = @{ PluginsPackageVersion = $script:pluginsVersion }
                LocalPackageIds           = @("EdFi.Api.Plugins")
                LocalPackageSource        = $LocalPackageSource
                PublicSource              = "https://api.nuget.org/v3/index.json"
                ResolvePackageBaseAddress = {
                    param([string] $IndexUrl, [string] $ApiKey)
                    if ($unreachable) {
                        throw "The feed's service index at $IndexUrl could not be read: 401 Unauthorized."
                    }
                    return "https://feed.invalid/flat2"
                }.GetNewClosure()
                GetPublishedVersions      = {
                    param([string] $BaseAddress, [string] $NormalizedId, [string] $ApiKey)
                    return @{ Found = $found; Versions = $versions }
                }.GetNewClosure()
                SavePublishedPackage      = {
                    param([string] $BaseAddress, [string] $NormalizedId, [string] $NormalizedVersion, [string] $Destination, [string] $ApiKey)
                    Copy-Item -LiteralPath $source -Destination $Destination
                }.GetNewClosure()
            }

            return @{
                Packages = $packages
                Output   = @(& $script:checker @arguments)
            }
        }
    }

    Context "The package id has never been published" {
        BeforeAll {
            $script:identityAbsent = Invoke-IdentityCheck -Feed @{ Found = $false; Versions = @() }
        }

        It "says the version is not yet on the feed and that nothing was compiled against it" {
            $script:identityAbsent.Output | Should -HaveCount 1
            $script:identityAbsent.Output[0] | Should -BeLike "Not verified against the feed: EdFi.Api.Identity $($script:identityVersion) is not yet on *nothing was compiled against a published package*"
        }

        It "restores nothing" {
            @(Get-ChildItem -LiteralPath $script:identityAbsent.Packages -Force) | Should -HaveCount 0
        }
    }

    Context "The feed cannot be read" {
        It "fails rather than reporting the version as not yet published" {
            { Invoke-IdentityCheck -Feed @{ Unreachable = $true } } |
                Should -Throw -ExpectedMessage "*could not be read: 401 Unauthorized*"
        }
    }

    Context "The declared version is published, unchanged" {
        BeforeAll {
            $script:identityUnchanged = Invoke-IdentityCheck -Feed @{
                Found    = $true
                Versions = @($script:identityVersion)
                Source   = $script:identityPacked
            }
        }

        It "reports the published package verified" {
            $script:identityUnchanged.Output | Should -HaveCount 1
            $script:identityUnchanged.Output[0] | Should -BeLike "Verified the published EdFi.Api.Identity $($script:identityVersion) from *"
        }

        It "compiled the consumer against Identity from the feed folder and Plugins from the local packs" {
            $assembly = Join-Path $script:identityUnchanged.Packages `
                "edfi.api.identity/$($script:identityVersion)/lib/net10.0/EdFi.DataManagementService.Identity.dll"
            $declared = [version]$script:identityVersion

            [System.Reflection.AssemblyName]::GetAssemblyName($assembly).Version |
                Should -Be ([version]::new($declared.Major, $declared.Minor, [Math]::Max($declared.Build, 0), 0))
            Join-Path $script:identityUnchanged.Packages "edfi.api.plugins/$($script:pluginsVersion)" |
                Should -Exist
        }
    }

    Context "The declared version is published, and its XML documentation differs" {
        It "fails naming the id, the version, and the comparison that differed" {
            { Invoke-IdentityCheck -Feed @{ Found = $true; Versions = @($script:identityVersion); Source = $script:identityChangedFile } } |
                Should -Throw -ExpectedMessage "EdFi.Api.Identity $($script:identityVersion) is already published and its XML documentation differ(s) from what was just packed.*"
        }
    }

    Context "The restore source does not hold the published Identity package" {
        It "fails at restore rather than taking the locally packed Identity" {
            {
                Invoke-IdentityCheck -RestoreSource $script:pluginsOnlyFeed -Feed @{
                    Found    = $true
                    Versions = @($script:identityVersion)
                    Source   = $script:identityPacked
                }
            } | Should -Throw -ExpectedMessage "The scratch consumer failed to restore the published EdFi.Api.Identity*"
        }
    }

    Context "The local packs do not hold Plugins" {
        It "fails at restore rather than taking Plugins from the feed, which holds it" {
            {
                Invoke-IdentityCheck -RestoreSource $script:fullFeed -LocalPackageSource $script:emptyLocalPacks -Feed @{
                    Found    = $true
                    Versions = @($script:identityVersion)
                    Source   = $script:identityPacked
                }
            } | Should -Throw -ExpectedMessage "The scratch consumer failed to restore the published EdFi.Api.Identity*"
        }
    }

    Context "The assembly name does not match the package" {
        It "fails to find the extracted assembly rather than passing" {
            {
                Invoke-IdentityCheck -AssemblyName "EdFi.Not.The.Assembly" -Feed @{
                    Found    = $true
                    Versions = @($script:identityVersion)
                    Source   = $script:identityPacked
                }
            } | Should -Throw -ExpectedMessage "The restore did not extract EdFi.Not.The.Assembly.dll*"
        }
    }

    Context "Local package ids are given without both sources" {
        It "fails before reading the feed when <Missing> is missing" -ForEach @(
            @{ Missing = "-LocalPackageSource"; Arguments = @{ PublicSource = "https://api.nuget.org/v3/index.json" } }
            @{ Missing = "-PublicSource"; Arguments = @{ LocalPackageSource = "local-packs" } }
        ) {
            $arguments = @{
                PackageFile            = $script:identityPacked
                PackageVersion         = $script:identityVersion
                ServiceIndexUrl        = "https://feed.invalid/index.json"
                NuGetPackagesDirectory = Join-Path $script:fixtureRoot "never-used-$([guid]::NewGuid().ToString('N'))"
                LocalPackageIds        = @("EdFi.Api.Plugins")
            } + $Arguments

            { & $script:checker @arguments } | Should -Throw -ExpectedMessage "*needs $Missing*"
        }
    }
}

Describe "Invoke-IdentityConsumerCheck published half" {
    It "refuses a published feed without its own fresh packages folder" {
        {
            & (Join-Path $script:repoRoot "eng/verification/Invoke-IdentityConsumerCheck.ps1") `
                -PackageVersion "1.0.0" `
                -PluginsPackageVersion "1.1.0" `
                -NuGetPackagesDirectory (Join-Path $script:fixtureRoot "unused-$([guid]::NewGuid().ToString('N'))") `
                -PublishedServiceIndexUrl "https://feed.invalid/index.json"
        } | Should -Throw -ExpectedMessage "*needs -PublishedNuGetPackagesDirectory*"
    }
}
