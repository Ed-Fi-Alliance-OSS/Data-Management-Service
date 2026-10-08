# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# The identity wire-contract gate: served document versus the baseline of the last published
# identity contract version, anchored by the commit the published nupkg records.
#
# Every case builds a throwaway git repository in $TestDrive whose commits carry a baseline file,
# synthesizes a minimal nupkg whose nuspec records a chosen commit, and hands the gate a fake feed.
# The documents are derived from the real committed golden so cases resemble production.

BeforeAll {
    # Loaded here so the default-seam case can mock HTTP inside the module the defaults run in; the
    # gate module imports it without -Force and so keeps this instance.
    Import-Module (Join-Path $PSScriptRoot "../ContractFeed.psm1") -Force

    $script:gate = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../Invoke-IdentityWireContractGate.ps1"))

    $realGolden = [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot "../../../src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/OpenApi/Fixtures/identity-v2-wire-baseline.json")
    )

    $script:golden = [System.IO.File]::ReadAllText($realGolden).Replace("`r`n", "`n")
    $script:mutated = $script:golden.Replace('"minLength": 1,', '"minLength": 2,')
    $script:baselinePath = "baseline/identity-v2-wire-baseline.json"

    $script:utf8 = [System.Text.UTF8Encoding]::new($false)

    function Invoke-TestGit {
        [CmdletBinding()]
        param([string] $Repository, [string[]] $Arguments)

        $output = & git -C $Repository -c user.name=test -c user.email=test@example.invalid -c commit.gpgsign=false -c core.autocrlf=false @Arguments 2>&1
        $exitCode = $LASTEXITCODE

        if ($exitCode -ne 0) {
            throw "git $($Arguments -join ' ') failed with $exitCode : $output"
        }

        return $output
    }

    # A repository with one commit per entry in $Documents, each writing the baseline file (or
    # deleting it when the entry is $null). Returns the repository path and the commit ids in order.
    function New-TestRepository {
        [CmdletBinding(SupportsShouldProcess)]
        [OutputType([pscustomobject])]
        param([Parameter(Mandatory)][AllowNull()][string[]] $Documents)

        $root = Join-Path $TestDrive "repo-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path (Join-Path $root "baseline") -Force | Out-Null

        if (-not $PSCmdlet.ShouldProcess($root, "Create test repository")) {
            return $null
        }

        Invoke-TestGit -Repository $root -Arguments @("init", "--quiet") | Out-Null

        $commits = @()
        $file = Join-Path $root $script:baselinePath

        # A commit that does not touch the baseline, so "absent at commit" can be tested.
        [System.IO.File]::WriteAllText((Join-Path $root "README.md"), "x")
        Invoke-TestGit -Repository $root -Arguments @("add", "README.md") | Out-Null
        Invoke-TestGit -Repository $root -Arguments @("commit", "--quiet", "-m", "no baseline yet") | Out-Null
        $commits += (Invoke-TestGit -Repository $root -Arguments @("rev-parse", "HEAD")).Trim()

        foreach ($document in $Documents) {
            if ($null -eq $document) {
                continue
            }

            [System.IO.File]::WriteAllText($file, $document, $script:utf8)
            Invoke-TestGit -Repository $root -Arguments @("add", $script:baselinePath) | Out-Null
            Invoke-TestGit -Repository $root -Arguments @("commit", "--quiet", "-m", "baseline $($commits.Count)") | Out-Null
            $commits += (Invoke-TestGit -Repository $root -Arguments @("rev-parse", "HEAD")).Trim()
        }

        return [pscustomobject]@{ Root = $root; Commits = $commits }
    }

    # A minimal nupkg: a zip holding one nuspec. $RepositoryXml is written verbatim inside metadata.
    function New-IdentityPackage {
        [CmdletBinding(SupportsShouldProcess)]
        [OutputType([string])]
        param(
            [string] $Version = "1.0.0",
            [string] $RepositoryXml = ""
        )

        $stage = Join-Path $TestDrive "stage-$([guid]::NewGuid().ToString('N'))"
        $package = Join-Path $TestDrive "EdFi.Api.Identity.$Version-$([guid]::NewGuid().ToString('N')).nupkg"

        if (-not $PSCmdlet.ShouldProcess($package, "Build package")) {
            return $null
        }

        New-Item -ItemType Directory -Path $stage -Force | Out-Null

        [System.IO.File]::WriteAllText(
            (Join-Path $stage "EdFi.Api.Identity.nuspec"),
            @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
    <metadata>
        <id>EdFi.Api.Identity</id>
        <version>$Version</version>
        <authors>Ed-Fi Alliance, LLC and contributors</authors>
        <description>Synthetic.</description>
$RepositoryXml
    </metadata>
</package>
"@
        )

        Compress-Archive -LiteralPath (Join-Path $stage "EdFi.Api.Identity.nuspec") -DestinationPath $package

        return $package
    }

    function Get-RepositoryXml {
        [CmdletBinding()]
        [OutputType([string])]
        param([string] $Commit)

        return "        <repository type=`"git`" url=`"https://example.invalid/repo.git`" commit=`"$Commit`" />"
    }

    function Get-FakeFeed {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Read inside GetNewClosure bodies, and the closure parameters are the injected seam signature.')]
        [CmdletBinding()]
        [OutputType([hashtable])]
        param(
            # The versions the package index reports, or $null for an id that was never published.
            [string[]] $Versions,
            [string] $ServiceIndexError = "",
            [string] $VersionIndexError = "",

            # Package served for any download.
            [string] $PublishedPackage = ""
        )

        return @{
            ResolvePackageBaseAddress = {
                param([string] $IndexUrl, [string] $ApiKey)

                if ($ServiceIndexError.Length -gt 0) {
                    throw $ServiceIndexError
                }

                return "https://feed.invalid/flat2"
            }.GetNewClosure()

            GetPublishedVersions      = {
                param([string] $BaseAddress, [string] $NormalizedId, [string] $ApiKey)

                if ($VersionIndexError.Length -gt 0) {
                    throw $VersionIndexError
                }

                if ($null -eq $Versions) {
                    return @{ Found = $false; Versions = @() }
                }

                return @{ Found = $true; Versions = $Versions }
            }.GetNewClosure()

            SavePublishedPackage      = {
                param([string] $BaseAddress, [string] $NormalizedId, [string] $NormalizedVersion, [string] $Destination, [string] $ApiKey)

                Copy-Item -LiteralPath $PublishedPackage -Destination $Destination
            }.GetNewClosure()
        }
    }

    function Invoke-Gate {
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)][string] $Repository,
            [Parameter(Mandatory)][string] $ServedDocument,
            [Parameter(Mandatory)][hashtable] $Feed,
            [string] $ContractVersion = "1.0.0"
        )

        $served = Join-Path $TestDrive "served-$([guid]::NewGuid().ToString('N')).json"
        [System.IO.File]::WriteAllText($served, $ServedDocument, $script:utf8)

        return & $script:gate `
            -ServedDocumentPath $served `
            -ContractVersion $ContractVersion `
            -RepositoryRoot $Repository `
            -BaselinePath $script:baselinePath `
            -ResolvePackageBaseAddress $Feed.ResolvePackageBaseAddress `
            -GetPublishedVersions $Feed.GetPublishedVersions `
            -SavePublishedPackage $Feed.SavePublishedPackage
    }
}

Describe "Invoke-IdentityWireContractGate with no published identity contract" {
    It "reports initial when the served document equals the baseline at HEAD" {
        $repo = New-TestRepository -Documents @($script:golden, $script:mutated)

        $decision = Invoke-Gate -Repository $repo.Root -ServedDocument $script:mutated -Feed (Get-FakeFeed) 6>$null

        $decision.Outcome | Should -BeExactly "initial"
        $decision.PublishedVersion | Should -BeNullOrEmpty
        $decision.AnchorCommit | Should -BeNullOrEmpty
    }

    It "reads the shared default feed seams, and a 404 package index is an absent package" {
        $repo = New-TestRepository -Documents @($script:golden, $script:mutated)
        $served = Join-Path $TestDrive "served-defaults.json"
        [System.IO.File]::WriteAllText($served, $script:mutated, $script:utf8)

        Mock -ModuleName ContractFeed Invoke-RestMethod {
            if ($Uri -eq "https://feed.invalid/index.json") {
                return [pscustomobject]@{
                    resources = @([pscustomobject]@{ '@type' = "PackageBaseAddress/3.0.0"; '@id' = "https://feed.invalid/flat2" })
                }
            }

            $response = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]::NotFound)
            throw [Microsoft.PowerShell.Commands.HttpResponseException]::new("Response status code does not indicate success: 404 (Not Found).", $response)
        }

        $decision = & $script:gate `
            -ServedDocumentPath $served `
            -ContractVersion "1.0.0" `
            -RepositoryRoot $repo.Root `
            -BaselinePath $script:baselinePath `
            -ServiceIndexUrl "https://feed.invalid/index.json" 6>$null

        $decision.Outcome | Should -BeExactly "initial"
        Should -Invoke -ModuleName ContractFeed Invoke-RestMethod -Times 2 -Exactly
    }

    It "fails when the served document differs from the baseline at HEAD" {
        $repo = New-TestRepository -Documents @($script:golden, $script:mutated)

        { Invoke-Gate -Repository $repo.Root -ServedDocument $script:golden -Feed (Get-FakeFeed) } |
            Should -Throw -ExpectedMessage "*reviewed initial baseline*"
    }
}

Describe "Invoke-IdentityWireContractGate with the contract version already published" {
    It "reports unchanged, with the version and anchor commit, when the served document equals the anchored baseline" {
        $repo = New-TestRepository -Documents @($script:golden, $script:mutated)
        $package = New-IdentityPackage -RepositoryXml (Get-RepositoryXml -Commit $repo.Commits[1])

        # HEAD holds a different baseline than the anchor, so passing proves the anchor is used.
        $decision = Invoke-Gate -Repository $repo.Root -ServedDocument $script:golden -Feed (Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $package) 6>$null

        $decision.Outcome | Should -BeExactly "unchanged"
        $decision.PublishedVersion | Should -BeExactly "1.0.0"
        $decision.AnchorCommit | Should -BeExactly $repo.Commits[1]
    }

    It "fails with a unified diff when one schema constraint changed at the same version" {
        $repo = New-TestRepository -Documents @($script:golden)
        $package = New-IdentityPackage -RepositoryXml (Get-RepositoryXml -Commit $repo.Commits[1])

        $message = ""

        try {
            Invoke-Gate -Repository $repo.Root -ServedDocument $script:mutated -Feed (Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $package) | Out-Null
        }
        catch {
            $message = $_.Exception.Message
        }

        $message | Should -BeLike "*differs from the baseline published with EdFi.Api.Identity 1.0.0*"
        $message | Should -BeLike '*-            "minLength": 1,*'
        $message | Should -BeLike '*+            "minLength": 2,*'
    }

    # The document carries the placeholders, never a concrete host, so two serves from
    # different hosts are one document. The C# normalizer's own test covers the substitution.
    It "compares equal when the placeholders are the only server and token url content" {
        $repo = New-TestRepository -Documents @($script:golden)
        $package = New-IdentityPackage -RepositoryXml (Get-RepositoryXml -Commit $repo.Commits[1])

        $script:golden | Should -BeLike '*"url": "{server}"*'
        $script:golden | Should -BeLike '*"tokenUrl": "{tokenUrl}"*'

        $decision = Invoke-Gate -Repository $repo.Root -ServedDocument $script:golden -Feed (Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $package) 6>$null

        $decision.Outcome | Should -BeExactly "unchanged"
    }

    It "compares equal across line-ending conventions" {
        $repo = New-TestRepository -Documents @($script:golden)
        $package = New-IdentityPackage -RepositoryXml (Get-RepositoryXml -Commit $repo.Commits[1])

        $decision = Invoke-Gate -Repository $repo.Root -ServedDocument $script:golden.Replace("`n", "`r`n") -Feed (Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $package) 6>$null

        $decision.Outcome | Should -BeExactly "unchanged"
    }

    It "anchors to the highest published version not greater than the contract version" {
        $repo = New-TestRepository -Documents @($script:golden)
        $package = New-IdentityPackage -RepositoryXml (Get-RepositoryXml -Commit $repo.Commits[1])

        $decision = Invoke-Gate -Repository $repo.Root -ServedDocument $script:golden -ContractVersion "1.0.0" -Feed (Get-FakeFeed -Versions @("0.9.0", "1.0.0", "2.0.0") -PublishedPackage $package) 6>$null

        $decision.PublishedVersion | Should -BeExactly "1.0.0"
    }

    It "requires the packed package to review a contract version greater than the published one" {
        $repo = New-TestRepository -Documents @($script:golden)
        $package = New-IdentityPackage -RepositoryXml (Get-RepositoryXml -Commit $repo.Commits[1])

        { Invoke-Gate -Repository $repo.Root -ServedDocument $script:golden -ContractVersion "1.1.0" -Feed (Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $package) } |
            Should -Throw -ExpectedMessage "*requires -PackedPackageFile*"
    }
}

Describe "Invoke-IdentityWireContractGate fails closed on an unusable anchor" {
    # Every unusable anchor fails with a message naming the unusable part, and none falls back to the
    # current golden.
    It "fails when the nuspec has no repository element" {
        $repo = New-TestRepository -Documents @($script:golden)
        $package = New-IdentityPackage

        { Invoke-Gate -Repository $repo.Root -ServedDocument $script:golden -Feed (Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $package) } |
            Should -Throw -ExpectedMessage "*no repository element*"
    }

    It "fails when the repository element has no commit" {
        $repo = New-TestRepository -Documents @($script:golden)
        $package = New-IdentityPackage -RepositoryXml '        <repository type="git" url="https://example.invalid/repo.git" />'

        { Invoke-Gate -Repository $repo.Root -ServedDocument $script:golden -Feed (Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $package) } |
            Should -Throw -ExpectedMessage "*no commit attribute*"
    }

    It "fails when the commit is not 40 hexadecimal characters" {
        $repo = New-TestRepository -Documents @($script:golden)
        $package = New-IdentityPackage -RepositoryXml (Get-RepositoryXml -Commit "2afedf538")

        { Invoke-Gate -Repository $repo.Root -ServedDocument $script:golden -Feed (Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $package) } |
            Should -Throw -ExpectedMessage "*not a 40 character hexadecimal commit id*"
    }

    It "fails when the commit is absent from the repository" {
        $repo = New-TestRepository -Documents @($script:golden)
        $package = New-IdentityPackage -RepositoryXml (Get-RepositoryXml -Commit ("ab" * 20))

        { Invoke-Gate -Repository $repo.Root -ServedDocument $script:golden -Feed (Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $package) } |
            Should -Throw -ExpectedMessage "*is not reachable in this repository*"
    }

    It "fails when the baseline file is absent at the anchor commit" {
        $repo = New-TestRepository -Documents @($script:golden)
        $package = New-IdentityPackage -RepositoryXml (Get-RepositoryXml -Commit $repo.Commits[0])

        { Invoke-Gate -Repository $repo.Root -ServedDocument $script:golden -Feed (Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $package) } |
            Should -Throw -ExpectedMessage "*is absent at commit*"
    }

    It "fails when the baseline at the anchor commit is not parseable JSON" {
        $repo = New-TestRepository -Documents @("{ not json")
        $package = New-IdentityPackage -RepositoryXml (Get-RepositoryXml -Commit $repo.Commits[1])

        { Invoke-Gate -Repository $repo.Root -ServedDocument $script:golden -Feed (Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $package) } |
            Should -Throw -ExpectedMessage "*is not parseable JSON*"
    }

    It "fails when the feed's service index cannot be read" {
        $repo = New-TestRepository -Documents @($script:golden)

        { Invoke-Gate -Repository $repo.Root -ServedDocument $script:golden -Feed (Get-FakeFeed -ServiceIndexError "the feed answered 401") } |
            Should -Throw -ExpectedMessage "*401*"
    }

    It "fails rather than treating an erroring package index as absent" {
        $repo = New-TestRepository -Documents @($script:golden)

        { Invoke-Gate -Repository $repo.Root -ServedDocument $script:golden -Feed (Get-FakeFeed -VersionIndexError "the feed answered 503") } |
            Should -Throw -ExpectedMessage "*503*"
    }

    It "fails when every published version is greater than the contract version" {
        $repo = New-TestRepository -Documents @($script:golden)
        $package = New-IdentityPackage -RepositoryXml (Get-RepositoryXml -Commit $repo.Commits[1])

        { Invoke-Gate -Repository $repo.Root -ServedDocument $script:golden -ContractVersion "1.0.0" -Feed (Get-FakeFeed -Versions @("1.1.0", "2.0.0") -PublishedPackage $package) } |
            Should -Throw -ExpectedMessage "*went backwards*"
    }

    It "never falls back to the current golden when the anchor is unusable" {
        # HEAD equals the served document, so a fallback would pass.
        $repo = New-TestRepository -Documents @($script:golden)
        $package = New-IdentityPackage

        { Invoke-Gate -Repository $repo.Root -ServedDocument $script:golden -Feed (Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $package) } |
            Should -Throw -ExpectedMessage "*no repository element*"
    }
}
