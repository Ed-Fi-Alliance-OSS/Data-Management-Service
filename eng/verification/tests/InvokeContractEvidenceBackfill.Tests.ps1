# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Backfilling a contract's release evidence from the run that published it.
#
# Every fixture mirrors the shapes the GitHub API and the artifacts actually have: the run object
# with repository.id and path, the artifact object with workflow_run.id and repository_id, a DSSE
# envelope whose statement records the run id and attempt, and an SPDX manifest whose root is
# resolved through documentDescribes because two package entries carry the package's name. Every
# request is a seam, so nothing here needs a token, a feed or a network, and nothing is uploaded.

BeforeAll {
    $script:backfill = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../Invoke-ContractEvidenceBackfill.ps1"))
    $script:publisher = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../Publish-ReleaseAsset.ps1"))

    Import-Module (Join-Path $PSScriptRoot "../ContractEvidence.psm1") -Force

    $script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms1501-backfill-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:fixtureRoot -Force | Out-Null

    $script:repository = "Ed-Fi-Alliance-OSS/Data-Management-Service"
    $script:repositoryId = 744612924
    $script:runId = "35352662171"
    $script:headSha = "c5f0241305b201f3296c392e1683028375f26ae7"
    $script:packageId = "EdFi.Api.TestContract"
    $script:packageVersion = "1.0.0"
    $script:packageFileName = "EdFi.Api.TestContract.1.0.0.nupkg"
    $script:provenanceName = "edfiApiTestContract.intoto.jsonl"
    $script:builderId = "https://github.com/Ed-Fi-Alliance-OSS/slsa-github-generator/.github/workflows/generator_generic_slsa3.yml@refs/tags/v2.0.0"

    function New-FixtureDirectory {
        [CmdletBinding(SupportsShouldProcess)]
        [OutputType([string])]
        param([string] $Prefix = "scenario")

        $path = Join-Path $script:fixtureRoot "$Prefix-$([guid]::NewGuid().ToString('N'))"

        if ($PSCmdlet.ShouldProcess($path, "Create fixture directory")) {
            New-Item -ItemType Directory -Path $path -Force | Out-Null
        }

        return $path
    }

    function Write-Fixture {
        [CmdletBinding(SupportsShouldProcess)]
        [OutputType([string])]
        param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][string] $Content)

        if ($PSCmdlet.ShouldProcess($Path, "Write fixture")) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
            [System.IO.File]::WriteAllText($Path, $Content)
        }

        return $Path
    }

    function Get-Statement {
        [CmdletBinding()]
        [OutputType([string])]
        param(
            [string] $SubjectName = $script:packageFileName,
            [Parameter(Mandatory)][string] $Sha256,
            [string] $RunId = $script:runId,
            [string] $RunAttempt = "1",
            [string] $RepositoryUri = "git+https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service@refs/heads/DMS-1501",
            [string] $SourceSha = $script:headSha,
            [string] $EntryPoint = ".github/workflows/on-prerelease.yml",
            [string] $BuilderId = $script:builderId,
            [string] $PredicateType = "https://slsa.dev/provenance/v0.2"
        )

        return [ordered]@{
            _type         = "https://in-toto.io/Statement/v0.1"
            predicateType = $PredicateType
            subject       = @(@{ name = $SubjectName; digest = @{ sha256 = $Sha256 } })
            predicate     = [ordered]@{
                builder    = @{ id = $BuilderId }
                buildType  = "https://github.com/slsa-framework/slsa-github-generator/generic@v1"
                invocation = [ordered]@{
                    configSource = [ordered]@{
                        uri        = $RepositoryUri
                        digest     = @{ sha1 = $SourceSha }
                        entryPoint = $EntryPoint
                    }
                    environment  = [ordered]@{
                        github_run_id      = $RunId
                        github_run_attempt = $RunAttempt
                        github_event_name  = "release"
                    }
                }
            }
        } | ConvertTo-Json -Depth 10 -Compress
    }

    function Get-Envelope {
        [CmdletBinding()]
        [OutputType([string])]
        param([Parameter(Mandatory)][string] $Statement, [string] $Signature = "c2lnbmF0dXJl")

        return [ordered]@{
            payloadType = "application/vnd.in-toto+json"
            payload     = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Statement))
            signatures  = @(@{ keyid = ""; sig = $Signature; cert = "-----BEGIN CERTIFICATE-----" })
        } | ConvertTo-Json -Depth 10 -Compress
    }

    function Get-Manifest {
        [CmdletBinding()]
        [OutputType([string])]
        param(
            [Parameter(Mandatory)][string] $Sha256,
            [string] $RootName = $script:packageId,
            [string] $RootVersion = $script:packageVersion,
            [string] $FileName = "./$($script:packageFileName)",
            [string[]] $DocumentDescribes = @("SPDXRef-RootPackage")
        )

        return [ordered]@{
            files                = @(
                [ordered]@{
                    fileName  = $FileName
                    SPDXID    = "SPDXRef-File--EdFi.Api.TestContract.1.0.0.nupkg-9D40763F661D66C12AB1ADA8D937D6FC851F76D1"
                    checksums = @(
                        @{ algorithm = "SHA256"; checksumValue = $Sha256 },
                        @{ algorithm = "SHA1"; checksumValue = "9d40763f661d66c12ab1ada8d937d6fc851f76d1" }
                    )
                }
            )
            packages             = @(
                [ordered]@{ name = $script:packageId; SPDXID = "SPDXRef-Package-82AC287A9D7ACC154EA89622429B13043D4FB394DF5A3D0D9C43F3D3C4C468FC"; versionInfo = $script:packageVersion },
                [ordered]@{ name = $RootName; SPDXID = "SPDXRef-RootPackage"; versionInfo = $RootVersion }
            )
            externalDocumentRefs = @()
            relationships        = @()
            spdxVersion          = "SPDX-2.2"
            dataLicense          = "CC0-1.0"
            SPDXID               = "SPDXRef-DOCUMENT"
            name                 = "$($script:packageId) $($script:packageVersion)"
            documentNamespace    = "https://ed-fi.org/EdFi.Api.TestContract/1.0.0/abc"
            creationInfo         = @{ created = "2026-09-18T13:58:00Z"; creators = @("Organization: Ed-Fi Alliance") }
            documentDescribes    = $DocumentDescribes
        } | ConvertTo-Json -Depth 10
    }

    function Get-FakeArtifact {
        [CmdletBinding()]
        [OutputType([pscustomobject])]
        param(
            [Parameter(Mandatory)][int] $Id,
            [Parameter(Mandatory)][string] $Name,
            [string] $RunId = $script:runId,
            [int] $RepositoryId = $script:repositoryId,
            [bool] $Expired = $false
        )

        return [pscustomobject]@{
            id                   = $Id
            name                 = $Name
            size_in_bytes        = 1
            url                  = "https://api.invalid/artifacts/$Id"
            archive_download_url = "https://api.invalid/artifacts/$Id/zip"
            expired              = $Expired
            created_at           = "2026-09-18T13:58:09Z"
            expires_at           = "2026-09-23T13:58:09Z"
            workflow_run         = [pscustomobject]@{
                id                 = [long] $RunId
                repository_id      = $RepositoryId
                head_repository_id = $RepositoryId
                head_branch        = "DMS-1501"
                head_sha           = $script:headSha
            }
        }
    }

    # One complete scenario: the run, its three artifacts and their files, the feed's archive, the
    # release, and a recording Publish seam. Each parameter introduces exactly one fault.
    function Get-Scenario {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Read inside GetNewClosure bodies, and the closure parameters are the injected seam signature.')]
        [CmdletBinding()]
        [OutputType([hashtable])]
        param(
            [string] $StatementSha = "",
            [string] $StatementSubjectName = $script:packageFileName,
            [string] $StatementRunId = $script:runId,
            [string] $StatementRunAttempt = "1",
            [string] $StatementRepositoryUri = "git+https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service@refs/heads/DMS-1501",
            [string] $StatementSourceSha = $script:headSha,
            [string] $StatementEntryPoint = ".github/workflows/on-prerelease.yml",
            [string] $StatementBuilderId = $script:builderId,
            [string] $ManifestSha = "",
            [string] $ManifestRootName = $script:packageId,
            [string[]] $ManifestDocumentDescribes = @("SPDXRef-RootPackage"),
            [string] $RunPath = ".github/workflows/on-prerelease.yml",
            [switch] $FeedServesOtherBytes,
            [switch] $FeedAbsent,
            [switch] $DuplicateSbomArtifact,
            [switch] $SbomArtifactFromOtherRun,
            [switch] $ProvenanceExpired,
            [scriptblock] $Publish
        )

        $root = New-FixtureDirectory

        # The package: arbitrary bytes, since nothing here parses it. What matters is its digest.
        $packagePath = Write-Fixture -Path (Join-Path $root "artifacts/1/content/$($script:packageFileName)") -Content "package bytes $([guid]::NewGuid())"
        $packageSha = Get-FileSha256 -Path $packagePath

        $feedPath = Join-Path $root "feed.nupkg"

        if ($FeedServesOtherBytes) {
            Write-Fixture -Path $feedPath -Content "different package bytes" | Out-Null
        }
        else {
            Copy-Item -LiteralPath $packagePath -Destination $feedPath
        }

        $statementSha = if ($StatementSha.Length -gt 0) { $StatementSha } else { $packageSha }
        $manifestSha = if ($ManifestSha.Length -gt 0) { $ManifestSha } else { $packageSha }

        $statement = Get-Statement -SubjectName $StatementSubjectName -Sha256 $statementSha -RunId $StatementRunId -RunAttempt $StatementRunAttempt `
            -RepositoryUri $StatementRepositoryUri -SourceSha $StatementSourceSha -EntryPoint $StatementEntryPoint -BuilderId $StatementBuilderId

        Write-Fixture -Path (Join-Path $root "artifacts/2/content/manifest.spdx.json") -Content (Get-Manifest -Sha256 $manifestSha -RootName $ManifestRootName -DocumentDescribes $ManifestDocumentDescribes) | Out-Null
        Write-Fixture -Path (Join-Path $root "artifacts/3/content/$($script:provenanceName)") -Content (Get-Envelope -Statement $statement) | Out-Null

        $artifacts = [System.Collections.Generic.List[object]]::new()
        $artifacts.Add((Get-FakeArtifact -Id 1 -Name "$($script:packageId)-NuGet"))
        $artifacts.Add((Get-FakeArtifact -Id 2 -Name "$($script:packageId)-SBOM" -RunId $(if ($SbomArtifactFromOtherRun) { "111" } else { $script:runId })))
        $artifacts.Add((Get-FakeArtifact -Id 3 -Name $script:provenanceName -Expired $ProvenanceExpired.IsPresent))

        if ($DuplicateSbomArtifact) {
            # The duplicate serves the same manifest, so a case that resolves the ambiguity by id
            # can still succeed.
            Copy-Item -LiteralPath (Join-Path $root "artifacts/2") -Destination (Join-Path $root "artifacts/4") -Recurse
            $artifacts.Add((Get-FakeArtifact -Id 4 -Name "$($script:packageId)-SBOM"))
        }

        $run = [pscustomobject]@{
            id              = [long] $script:runId
            name            = "On Pre-Release for DMS and Config Service"
            path            = $RunPath
            event           = "release"
            head_sha        = $script:headSha
            head_branch     = "DMS-1501"
            run_attempt     = 1
            repository      = [pscustomobject]@{ id = $script:repositoryId; full_name = $script:repository }
            head_repository = [pscustomobject]@{ id = $script:repositoryId; full_name = $script:repository }
        }

        $calls = [System.Collections.Generic.List[string]]::new()
        $publications = [System.Collections.Generic.List[hashtable]]::new()

        if ($null -eq $Publish) {
            $Publish = {
                param([hashtable] $Arguments)

                $publications.Add($Arguments)

                return [pscustomobject]@{ Action = "uploaded"; AssetName = $Arguments.AssetName; Digest = "recorded" }
            }.GetNewClosure()
        }

        return @{
            Root         = $root
            PackageSha   = $packageSha
            Calls        = $calls
            Publications = $publications
            Seams        = @{
                GetRun                    = {
                    param([string] $ApiBaseUrl, [string] $Repository, [string] $RunId, [string] $RunAttempt, [string] $Token)

                    $calls.Add("run:$RunId")

                    return $run
                }.GetNewClosure()
                ListRunArtifacts          = {
                    param([string] $ApiBaseUrl, [string] $Repository, [string] $RunId, [string] $Token)

                    $calls.Add("artifacts:$RunId")

                    return [object[]] $artifacts.ToArray()
                }.GetNewClosure()
                GetArtifact               = {
                    param([string] $ApiBaseUrl, [string] $Repository, [string] $ArtifactId, [string] $Token)

                    $calls.Add("artifact:$ArtifactId")

                    return @($artifacts | Where-Object { [string] $_.id -ceq $ArtifactId })[0]
                }.GetNewClosure()
                DownloadArtifact          = {
                    param($Artifact, [string] $DestinationDirectory, [string] $Token)

                    $calls.Add("download:$($Artifact.id)")
                    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
                    Copy-Item -LiteralPath (Join-Path $root "artifacts/$($Artifact.id)/content") -Destination (Join-Path $DestinationDirectory "content") -Recurse
                }.GetNewClosure()
                ResolvePackageBaseAddress = {
                    param([string] $IndexUrl, [string] $ApiKey)

                    return "https://feed.invalid/flat2"
                }.GetNewClosure()
                SavePublishedPackage      = {
                    param([string] $BaseAddress, [string] $NormalizedId, [string] $NormalizedVersion, [string] $Destination, [string] $ApiKey)

                    $calls.Add("feed:$NormalizedId/$NormalizedVersion")

                    if ($FeedAbsent) {
                        throw "The published package at $BaseAddress/$NormalizedId/$NormalizedVersion could not be downloaded: 404"
                    }

                    Copy-Item -LiteralPath $feedPath -Destination $Destination
                }.GetNewClosure()
                ResolveRelease            = {
                    param([string] $ApiBaseUrl, [string] $Repository, [string] $ReleaseTag, [string] $Token)

                    $calls.Add("release:$ReleaseTag")

                    return [pscustomobject]@{ id = 777; tag_name = $ReleaseTag }
                }.GetNewClosure()
                Publish                   = $Publish
            }
        }
    }

    # A fake release behind the production Publish-ReleaseAsset.ps1: one complete SBOM asset already
    # uploaded, served from the archive at the path the recorder names, and nothing else. The
    # publisher is the real script, so the retry behaviour under test is production code.
    function Get-FakeReleasePublisher {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Read inside GetNewClosure bodies, and the closure parameters are the injected seam signature.')]
        [CmdletBinding()]
        [OutputType([hashtable])]
        param()

        $uploads = [System.Collections.Generic.List[string]]::new()
        $deletes = [System.Collections.Generic.List[string]]::new()
        $existing = @{ ArchivePath = "" }

        # Captured into the closure by value: a closure's $script: scope is its own, not this file's.
        $publisher = $script:publisher

        $publish = {
            param([hashtable] $Arguments)

            # GetNewClosure captures locals only, so the variables this closure itself captured are
            # re-bound as locals before the inner seams are created.
            $existingArchive = $existing.ArchivePath
            $uploadLog = $uploads
            $deleteLog = $deletes

            $Arguments.GetAssets = {
                param([string] $ApiBaseUrl, [string] $Repository, [string] $ReleaseId, [string] $Token)

                return [object[]] @([pscustomobject]@{ id = 41; name = "EdFi.Api.TestContract-SBOM.zip"; state = "uploaded"; url = "https://api.invalid/assets/41" })
            }
            $Arguments.DownloadAsset = {
                param($Asset, [string] $Destination, [string] $Token)

                Copy-Item -LiteralPath $existingArchive -Destination $Destination
            }.GetNewClosure()
            $Arguments.DeleteAsset = {
                param([string] $ApiBaseUrl, [string] $Repository, $Asset, [string] $Token)

                $deleteLog.Add([string] $Asset.id)
            }.GetNewClosure()
            $Arguments.UploadAsset = {
                param([string] $UploadBaseUrl, [string] $Repository, [string] $ReleaseId, [string] $AssetName, [string] $FilePath, [string] $Token)

                $uploadLog.Add($AssetName)

                return [pscustomobject]@{ id = 42; name = $AssetName; state = "uploaded" }
            }.GetNewClosure()

            return & $publisher @Arguments
        }.GetNewClosure()

        return @{
            Uploads  = $uploads
            Deletes  = $deletes
            Existing = $existing
            Publish  = $publish
        }
    }

    function Invoke-Backfill {
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)][hashtable] $Scenario,
            [string] $RunAttempt = "",
            [string] $ReleaseTag = "v8.1.0",
            [hashtable] $Extra = @{}
        )

        $arguments = @{
            Repository             = $script:repository
            RunId                  = $script:runId
            PackageId              = $script:packageId
            PackageVersion         = $script:packageVersion
            ReleaseTag             = $ReleaseTag
            ProvenanceArtifactName = $script:provenanceName
            WorkingDirectory       = (Join-Path $Scenario.Root "work")
            Token                  = "not-a-real-token"
        }

        if ($RunAttempt.Length -gt 0) {
            $arguments.RunAttempt = $RunAttempt
        }

        foreach ($key in $Scenario.Seams.Keys) {
            $arguments[$key] = $Scenario.Seams[$key]
        }

        foreach ($key in $Extra.Keys) {
            $arguments[$key] = $Extra[$key]
        }

        return & $script:backfill @arguments
    }
}

AfterAll {
    if ($script:fixtureRoot -and (Test-Path -LiteralPath $script:fixtureRoot)) {
        Remove-Item -LiteralPath $script:fixtureRoot -Recurse -Force
    }
}

Describe "Invoke-ContractEvidenceBackfill attaches the original evidence" {
    It "uploads the SBOM archive and the provenance envelope after every check passes" {
        $scenario = Get-Scenario

        $result = Invoke-Backfill -Scenario $scenario

        $result.PackageSha256 | Should -BeExactly $scenario.PackageSha
        $result.Sbom.Action | Should -BeExactly "uploaded"
        $result.Provenance.Action | Should -BeExactly "uploaded"
        $scenario.Publications.Count | Should -Be 2
        $scenario.Publications[0].AssetName | Should -BeExactly "EdFi.Api.TestContract-SBOM.zip"
        $scenario.Publications[0].ReleaseId | Should -BeExactly "777"
        $scenario.Publications[1].AssetName | Should -BeExactly $script:provenanceName
    }

    It "hands the SBOM upload the manifest's digest as the expected content digest" {
        $scenario = Get-Scenario

        Invoke-Backfill -Scenario $scenario | Out-Null

        $manifest = Join-Path $scenario.Root "artifacts/2/content/manifest.spdx.json"
        $scenario.Publications[0].ExpectedDigest | Should -BeExactly (Get-FileSha256 -Path $manifest)
        (& $scenario.Publications[0].ContentDigest $scenario.Publications[0].FilePath) | Should -BeExactly (Get-FileSha256 -Path $manifest)
    }

    It "uploads the provenance bytes it retrieved, unchanged" {
        $scenario = Get-Scenario

        Invoke-Backfill -Scenario $scenario | Out-Null

        $original = Join-Path $scenario.Root "artifacts/3/content/$($script:provenanceName)"
        Get-FileSha256 -Path $scenario.Publications[1].FilePath | Should -BeExactly (Get-FileSha256 -Path $original)
    }

    # The alpha-first shape: the run that pushed the version was an alpha prerelease, and the
    # evidence belongs on the later final release, which is a different tag.
    It "attaches an alpha run's evidence to the release named by tag" {
        $scenario = Get-Scenario

        Invoke-Backfill -Scenario $scenario -ReleaseTag "v8.1.0" | Out-Null

        $scenario.Calls | Should -Contain "release:v8.1.0"
        $scenario.Calls | Should -Contain "run:$($script:runId)"
    }

    It "checks the run attempt against the provenance when one is given" {
        $scenario = Get-Scenario -StatementRunAttempt "1"

        { Invoke-Backfill -Scenario $scenario -RunAttempt "1" } | Should -Not -Throw
    }

    It "selects an explicitly identified artifact among duplicates of one name" {
        $scenario = Get-Scenario -DuplicateSbomArtifact

        $result = Invoke-Backfill -Scenario $scenario -Extra @{ SbomArtifactId = "4" }

        $result.Sbom.Action | Should -BeExactly "uploaded"
        $scenario.Calls | Should -Contain "artifact:4"
    }
}

Describe "Invoke-ContractEvidenceBackfill uploads nothing unless everything is proven" {
    It "fails when the feed serves different bytes from the run's package" {
        $scenario = Get-Scenario -FeedServesOtherBytes

        { Invoke-Backfill -Scenario $scenario } | Should -Throw -ExpectedMessage "*did not publish the bytes on the feed*"

        $scenario.Publications.Count | Should -Be 0
    }

    It "fails when the version is not on the feed" {
        $scenario = Get-Scenario -FeedAbsent

        { Invoke-Backfill -Scenario $scenario } | Should -Throw -ExpectedMessage "*404*"

        $scenario.Publications.Count | Should -Be 0
    }

    It "fails when the provenance subject digest is not the package's" {
        $scenario = Get-Scenario -StatementSha ("00" * 32)

        { Invoke-Backfill -Scenario $scenario } | Should -Throw -ExpectedMessage "*does not describe this package*"

        $scenario.Publications.Count | Should -Be 0
    }

    It "fails when the provenance subject is another file" {
        $scenario = Get-Scenario -StatementSubjectName "EdFi.Api.Other.1.0.0.nupkg"

        { Invoke-Backfill -Scenario $scenario } | Should -Throw -ExpectedMessage "*names subject*"
    }

    It "fails when the provenance records another run" {
        $scenario = Get-Scenario -StatementRunId "999"

        { Invoke-Backfill -Scenario $scenario } | Should -Throw -ExpectedMessage "*records run '999'*"
    }

    It "fails when the provenance records another attempt than the one given" {
        $scenario = Get-Scenario -StatementRunAttempt "2"

        { Invoke-Backfill -Scenario $scenario -RunAttempt "1" } | Should -Throw -ExpectedMessage "*run attempt '2'*"
    }

    It "fails when the provenance names another workflow file" {
        $scenario = Get-Scenario -StatementEntryPoint ".github/workflows/on-release.yml"

        { Invoke-Backfill -Scenario $scenario } | Should -Throw -ExpectedMessage "*entry point*"
    }

    It "fails when the provenance names another source commit" {
        $scenario = Get-Scenario -StatementSourceSha ("0" * 40)

        { Invoke-Backfill -Scenario $scenario } | Should -Throw -ExpectedMessage "*source commit*"
    }

    It "fails when the provenance names another repository" {
        $scenario = Get-Scenario -StatementRepositoryUri "git+https://github.com/someone-else/Data-Management-Service@refs/heads/main"

        { Invoke-Backfill -Scenario $scenario } | Should -Throw -ExpectedMessage "*not this repository*"
    }

    It "fails when the provenance names another builder" {
        $scenario = Get-Scenario -StatementBuilderId "https://github.com/someone-else/generator/.github/workflows/x.yml@refs/tags/v1"

        { Invoke-Backfill -Scenario $scenario } | Should -Throw -ExpectedMessage "*not the generator*"
    }

    It "fails when the SBOM records another digest for the package file" {
        $scenario = Get-Scenario -ManifestSha ("11" * 32)

        { Invoke-Backfill -Scenario $scenario } | Should -Throw -ExpectedMessage "*SBOM does not describe this package*"

        $scenario.Publications.Count | Should -Be 0
    }

    It "fails when the SBOM's root package is another package" {
        $scenario = Get-Scenario -ManifestRootName "EdFi.Api.Other"

        { Invoke-Backfill -Scenario $scenario } | Should -Throw -ExpectedMessage "*describes package 'EdFi.Api.Other'*"
    }

    It "fails when the SBOM describes more than one root" {
        $scenario = Get-Scenario -ManifestDocumentDescribes @("SPDXRef-RootPackage", "SPDXRef-Package-82AC287A9D7ACC154EA89622429B13043D4FB394DF5A3D0D9C43F3D3C4C468FC")

        { Invoke-Backfill -Scenario $scenario } | Should -Throw -ExpectedMessage "*2 root packages*"
    }

    It "fails when two artifacts carry one name, naming the id parameter" {
        $scenario = Get-Scenario -DuplicateSbomArtifact

        { Invoke-Backfill -Scenario $scenario } | Should -Throw -ExpectedMessage "*Pass -SbomArtifactId*"

        $scenario.Publications.Count | Should -Be 0
    }

    It "refuses an explicit id that names another artifact" {
        $scenario = Get-Scenario

        { Invoke-Backfill -Scenario $scenario -Extra @{ SbomArtifactId = "1" } } |
            Should -Throw -ExpectedMessage "*does not substitute another artifact*"
    }

    It "refuses an artifact that belongs to another run" {
        $scenario = Get-Scenario -SbomArtifactFromOtherRun

        { Invoke-Backfill -Scenario $scenario } | Should -Throw -ExpectedMessage "*belongs to run '111'*"
    }

    It "fails on an expired artifact, naming its expiry and the retention limits" {
        $scenario = Get-Scenario -ProvenanceExpired

        { Invoke-Backfill -Scenario $scenario } | Should -Throw -ExpectedMessage "*expired at 2026-09-23T13:58:09Z*five days*"

        $scenario.Publications.Count | Should -Be 0
    }

    It "fails when the run ran another workflow" {
        $scenario = Get-Scenario -RunPath ".github/workflows/on-release.yml"

        { Invoke-Backfill -Scenario $scenario } | Should -Throw -ExpectedMessage "*ran '.github/workflows/on-release.yml'*"

        $scenario.Calls | Should -Not -Contain "artifacts:$($script:runId)"
    }

    It "requires a token when the GitHub seams are the defaults" {
        $previousGh = $env:GH_TOKEN
        $previousGitHub = $env:GITHUB_TOKEN

        try {
            $env:GH_TOKEN = ""
            $env:GITHUB_TOKEN = ""

            {
                & $script:backfill `
                    -Repository $script:repository `
                    -RunId $script:runId `
                    -PackageId $script:packageId `
                    -PackageVersion $script:packageVersion `
                    -ReleaseTag "v8.1.0" `
                    -ProvenanceArtifactName $script:provenanceName `
                    -WorkingDirectory (Join-Path (New-FixtureDirectory) "work")
            } | Should -Throw -ExpectedMessage "*token is required*"
        }
        finally {
            $env:GH_TOKEN = $previousGh
            $env:GITHUB_TOKEN = $previousGitHub
        }
    }
}

Describe "Invoke-ContractEvidenceBackfill retries a partial upload through the real publisher" {
    # The SBOM reached the release on the first attempt, the provenance did not. The retry runs the
    # production Publish-ReleaseAsset.ps1 against a fake release: the existing SBOM asset is a
    # different zip of the same manifest, so it is recognized by content and skipped; the provenance
    # is uploaded.
    It "skips the SBOM already on the release and uploads the missing provenance" {
        $release = Get-FakeReleasePublisher
        $scenario = Get-Scenario -Publish $release.Publish

        # The asset the first attempt uploaded: the same manifest, zipped separately.
        $manifest = Join-Path $scenario.Root "artifacts/2/content/manifest.spdx.json"
        $release.Existing.ArchivePath = New-SbomArchive -ManifestPath $manifest -DestinationPath (Join-Path $scenario.Root "first-attempt-sbom.zip")

        $result = Invoke-Backfill -Scenario $scenario

        $result.Sbom.Action | Should -BeExactly "skipped"
        $result.Provenance.Action | Should -BeExactly "uploaded"
        ($release.Uploads -join ",") | Should -BeExactly $script:provenanceName
        $release.Deletes.Count | Should -Be 0
    }
}
