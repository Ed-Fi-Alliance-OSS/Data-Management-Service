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
    $script:checker = Join-Path $script:repoRoot "eng/verification/Invoke-PublishedSecretsConsumerCheck.ps1"
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

Describe "Invoke-PublishedSecretsConsumerCheck" {
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
