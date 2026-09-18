# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Attaching one release asset idempotently, and the digest rules the attachment rests on.
#
# The failure that matters: a retry after a partial upload must never replace evidence the release
# already carries for other bytes, and an envelope that merely repeats the right subject hash must
# never pass as the generator's provenance. Every GitHub call is an injected seam, so nothing here
# needs a token or a network.

BeforeAll {
    $script:publisher = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../Publish-ReleaseAsset.ps1"))

    Import-Module (Join-Path $PSScriptRoot "../ContractEvidence.psm1") -Force

    $script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms1501-release-asset-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:fixtureRoot -Force | Out-Null

    function New-FixtureFile {
        [CmdletBinding(SupportsShouldProcess)]
        [OutputType([string])]
        param([Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][AllowEmptyString()][string] $Content)

        $path = Join-Path $script:fixtureRoot "$([guid]::NewGuid().ToString('N'))-$Name"

        if ($PSCmdlet.ShouldProcess($path, "Write fixture")) {
            [System.IO.File]::WriteAllText($path, $Content)
        }

        return $path
    }

    function Get-ScratchPath {
        [CmdletBinding()]
        [OutputType([string])]
        param()

        return Join-Path $script:fixtureRoot "work-$([guid]::NewGuid().ToString('N'))"
    }

    function Get-FakeAsset {
        [CmdletBinding()]
        [OutputType([pscustomobject])]
        param([Parameter(Mandatory)][int] $Id, [Parameter(Mandatory)][string] $Name, [string] $State = "uploaded")

        return [pscustomobject]@{ id = $Id; name = $Name; state = $State; url = "https://api.invalid/assets/$Id" }
    }

    # A release whose assets and their bytes come from a table, recording every call in order.
    function Get-FakeGitHub {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Read inside GetNewClosure bodies, and the closure parameters are the injected seam signature.')]
        [CmdletBinding()]
        [OutputType([hashtable])]
        param(
            [object[]] $Assets = @(),
            [string] $RemoteFile = "",
            [string] $UploadError = ""
        )

        $calls = [System.Collections.Generic.List[string]]::new()

        return @{
            Calls         = $calls
            GetAssets     = {
                param([string] $ApiBaseUrl, [string] $Repository, [string] $ReleaseId, [string] $Token)

                $calls.Add("list")

                return [object[]] $Assets
            }.GetNewClosure()
            DownloadAsset = {
                param($Asset, [string] $Destination, [string] $Token)

                $calls.Add("download:$($Asset.id)")
                Copy-Item -LiteralPath $RemoteFile -Destination $Destination
            }.GetNewClosure()
            DeleteAsset   = {
                param([string] $ApiBaseUrl, [string] $Repository, $Asset, [string] $Token)

                $calls.Add("delete:$($Asset.id)")
            }.GetNewClosure()
            UploadAsset   = {
                param([string] $UploadBaseUrl, [string] $Repository, [string] $ReleaseId, [string] $AssetName, [string] $FilePath, [string] $Token)

                $calls.Add("upload:$AssetName")

                if ($UploadError.Length -gt 0) {
                    throw $UploadError
                }

                return [pscustomobject]@{ id = 9001; name = $AssetName; state = "uploaded" }
            }.GetNewClosure()
        }
    }

    function Invoke-Publish {
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)][string] $FilePath,
            [Parameter(Mandatory)][hashtable] $Fake,
            [string] $AssetName = "EdFi.Api.TestContract-SBOM.zip",
            [scriptblock] $ContentDigest,
            [string] $ExpectedDigest = ""
        )

        $arguments = @{
            Repository       = "Ed-Fi-Alliance-OSS/Data-Management-Service"
            ReleaseId        = "123"
            AssetName        = $AssetName
            FilePath         = $FilePath
            WorkingDirectory = (Get-ScratchPath)
            GetAssets        = $Fake.GetAssets
            DownloadAsset    = $Fake.DownloadAsset
            DeleteAsset      = $Fake.DeleteAsset
            UploadAsset      = $Fake.UploadAsset
        }

        if ($null -ne $ContentDigest) {
            $arguments.ContentDigest = $ContentDigest
        }

        if ($ExpectedDigest.Length -gt 0) {
            $arguments.ExpectedDigest = $ExpectedDigest
        }

        return & $script:publisher @arguments
    }

    # A DSSE envelope of the shape the SLSA generator writes, with the signature under the caller's
    # control so a second envelope can repeat the subject and differ only there.
    function New-ProvenanceEnvelope {
        [CmdletBinding(SupportsShouldProcess)]
        [OutputType([string])]
        param(
            [string] $SubjectName = "EdFi.Api.TestContract.1.0.0.nupkg",
            [string] $Sha256 = ("ab" * 32),
            [string] $Signature = "c2lnbmF0dXJl"
        )

        $statement = [ordered]@{
            _type         = "https://in-toto.io/Statement/v0.1"
            predicateType = "https://slsa.dev/provenance/v0.2"
            subject       = @(@{ name = $SubjectName; digest = @{ sha256 = $Sha256 } })
            predicate     = @{ builder = @{ id = "https://github.com/Ed-Fi-Alliance-OSS/slsa-github-generator/.github/workflows/generator_generic_slsa3.yml@refs/tags/v2.0.0" } }
        } | ConvertTo-Json -Depth 8 -Compress

        $envelope = [ordered]@{
            payloadType = "application/vnd.in-toto+json"
            payload     = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($statement))
            signatures  = @(@{ keyid = ""; sig = $Signature; cert = "-----BEGIN CERTIFICATE-----" })
        } | ConvertTo-Json -Depth 8 -Compress

        return New-FixtureFile -Name "provenance.intoto.jsonl" -Content $envelope
    }

    function Write-ManifestFixture {
        [CmdletBinding()]
        [OutputType([string])]
        param([string] $Content = '{"spdxVersion":"SPDX-2.2","name":"EdFi.Api.TestContract 1.0.0"}')

        return New-FixtureFile -Name "manifest.spdx.json" -Content $Content
    }

    function Get-ArchivePath {
        [CmdletBinding()]
        [OutputType([string])]
        param()

        return Join-Path $script:fixtureRoot "archive-$([guid]::NewGuid().ToString('N')).zip"
    }

    # The bytes the fake release serves for an existing asset, at one path a mock body can find
    # through a function: a mock runs in the scope of the script under test, where this file's
    # variables are not visible.
    function Get-RemoteFixturePath {
        [CmdletBinding()]
        [OutputType([string])]
        param()

        return Join-Path ([System.IO.Path]::GetTempPath()) "dms1501-release-asset-remote-$PID.bin"
    }

    function Get-HttpAsset {
        [CmdletBinding()]
        [OutputType([pscustomobject])]
        param([Parameter(Mandatory)][int] $Id, [Parameter(Mandatory)][string] $Name, [string] $State = "uploaded")

        return [pscustomobject]@{
            id    = $Id
            name  = $Name
            state = $State
            url   = "https://api.github.com/repos/Ed-Fi-Alliance-OSS/Data-Management-Service/releases/assets/$Id"
        }
    }

    # Runs the publisher with its real GitHub defaults; only the HTTP cmdlets are mocked by the case.
    function Invoke-DefaultPublish {
        [CmdletBinding()]
        param([Parameter(Mandatory)][string] $FilePath, [string] $AssetName = "EdFi.Api.TestContract-SBOM.zip")

        return & $script:publisher `
            -Repository "Ed-Fi-Alliance-OSS/Data-Management-Service" `
            -ReleaseId "123" `
            -AssetName $AssetName `
            -FilePath $FilePath `
            -WorkingDirectory (Get-ScratchPath) `
            -Token "not-a-real-token"
    }
}

AfterAll {
    if ($script:fixtureRoot -and (Test-Path -LiteralPath $script:fixtureRoot)) {
        Remove-Item -LiteralPath $script:fixtureRoot -Recurse -Force
    }

    if (Test-Path -LiteralPath (Get-RemoteFixturePath)) {
        Remove-Item -LiteralPath (Get-RemoteFixturePath) -Force
    }
}

Describe "Publish-ReleaseAsset's GitHub defaults" {
    # The injected seams above hand the script an array they built; these cases hand it what
    # Invoke-RestMethod really returns, so the listing's shape and its pagination are exercised.
    It "finds the asset among several on one page and compares it" {
        $file = New-FixtureFile -Name "a.txt" -Content "evidence"
        Copy-Item -LiteralPath $file -Destination (Get-RemoteFixturePath) -Force

        Mock Invoke-RestMethod {
            if ($Uri -like "*/releases/123/assets?per_page=100&page=1") {
                return @(
                    (Get-HttpAsset -Id 1 -Name "EdFi.Api-SBOM.zip"),
                    (Get-HttpAsset -Id 2 -Name "EdFi.Api.TestContract-SBOM.zip"),
                    (Get-HttpAsset -Id 3 -Name "edfiApi.intoto.jsonl")
                )
            }

            throw "unexpected request: $Uri"
        }
        Mock Invoke-WebRequest {
            Copy-Item -LiteralPath (Get-RemoteFixturePath) -Destination $OutFile
        }

        $result = Invoke-DefaultPublish -FilePath $file

        $result.Action | Should -BeExactly "skipped"
        $result.AssetId | Should -Be 2
        Should -Invoke Invoke-WebRequest -Times 1 -Exactly -ParameterFilter { $Uri -like "*/releases/assets/2" }
    }

    It "reads every page of assets before deciding" {
        $file = New-FixtureFile -Name "a.txt" -Content "evidence"
        Copy-Item -LiteralPath $file -Destination (Get-RemoteFixturePath) -Force

        Mock Invoke-RestMethod {
            if ($Uri -like "*/releases/123/assets?per_page=100&page=1") {
                return @(1..100 | ForEach-Object { Get-HttpAsset -Id $_ -Name "other-$_.txt" })
            }

            if ($Uri -like "*/releases/123/assets?per_page=100&page=2") {
                return @((Get-HttpAsset -Id 555 -Name "EdFi.Api.TestContract-SBOM.zip"))
            }

            throw "unexpected request: $Uri"
        }
        Mock Invoke-WebRequest {
            Copy-Item -LiteralPath (Get-RemoteFixturePath) -Destination $OutFile
        }

        $result = Invoke-DefaultPublish -FilePath $file

        $result.Action | Should -BeExactly "skipped"
        $result.AssetId | Should -Be 555
        Should -Invoke Invoke-RestMethod -Times 1 -Exactly -ParameterFilter { $Uri -like "*page=2" }
    }

    It "uploads through the uploads endpoint when no page lists the asset" {
        $file = New-FixtureFile -Name "a.txt" -Content "evidence"

        Mock Invoke-RestMethod {
            if ($Uri -like "*/releases/123/assets?per_page=100&page=1") {
                return @((Get-HttpAsset -Id 1 -Name "EdFi.Api-SBOM.zip"))
            }

            if ($Method -eq "Post" -and $Uri -like "https://uploads.github.com/repos/Ed-Fi-Alliance-OSS/Data-Management-Service/releases/123/assets?name=EdFi.Api.TestContract-SBOM.zip") {
                return [pscustomobject]@{ id = 777; name = "EdFi.Api.TestContract-SBOM.zip"; state = "uploaded" }
            }

            throw "unexpected request: $Uri"
        }

        $result = Invoke-DefaultPublish -FilePath $file

        $result.Action | Should -BeExactly "uploaded"
        $result.AssetId | Should -Be 777
        Should -Invoke Invoke-RestMethod -Times 1 -Exactly -ParameterFilter { $Method -eq "Post" -and $InFile -eq $file }
    }

    It "deletes a starter through the assets endpoint before uploading" {
        $file = New-FixtureFile -Name "a.txt" -Content "evidence"

        Mock Invoke-RestMethod {
            if ($Uri -like "*/releases/123/assets?per_page=100&page=1") {
                return @((Get-HttpAsset -Id 9 -Name "EdFi.Api.TestContract-SBOM.zip" -State "starter"))
            }

            if ($Method -eq "Delete" -and $Uri -like "*/releases/assets/9") {
                return $null
            }

            if ($Method -eq "Post") {
                return [pscustomobject]@{ id = 10; name = "EdFi.Api.TestContract-SBOM.zip"; state = "uploaded" }
            }

            throw "unexpected request: $Uri"
        }

        $result = Invoke-DefaultPublish -FilePath $file

        $result.Action | Should -BeExactly "replaced-starter"
        Should -Invoke Invoke-RestMethod -Times 1 -Exactly -ParameterFilter { $Method -eq "Delete" -and $Uri -like "*/releases/assets/9" }
    }
}

Describe "Publish-ReleaseAsset decides from what the release already carries" {
    It "uploads when the release carries no asset of that name" {
        $file = New-FixtureFile -Name "a.txt" -Content "evidence"
        $fake = Get-FakeGitHub

        $result = Invoke-Publish -FilePath $file -Fake $fake

        $result.Action | Should -BeExactly "uploaded"
        $result.AssetId | Should -Be 9001
        $result.Digest | Should -BeExactly (Get-FileSha256 -Path $file)
        ($fake.Calls -join ",") | Should -BeExactly "list,upload:EdFi.Api.TestContract-SBOM.zip"
    }

    It "replaces an interrupted starter upload, deleting it before uploading" {
        $file = New-FixtureFile -Name "a.txt" -Content "evidence"
        $fake = Get-FakeGitHub -Assets @((Get-FakeAsset -Id 1 -Name "EdFi.Api.TestContract-SBOM.zip" -State "starter"))

        $result = Invoke-Publish -FilePath $file -Fake $fake

        $result.Action | Should -BeExactly "replaced-starter"
        ($fake.Calls -join ",") | Should -BeExactly "list,delete:1,upload:EdFi.Api.TestContract-SBOM.zip"
    }

    It "skips a complete asset whose content matches, touching nothing" {
        $file = New-FixtureFile -Name "a.txt" -Content "evidence"
        $fake = Get-FakeGitHub -Assets @((Get-FakeAsset -Id 2 -Name "EdFi.Api.TestContract-SBOM.zip")) -RemoteFile $file

        $result = Invoke-Publish -FilePath $file -Fake $fake

        $result.Action | Should -BeExactly "skipped"
        $result.AssetId | Should -Be 2
        ($fake.Calls -join ",") | Should -BeExactly "list,download:2"
    }

    # The release already holds evidence for other bytes. Replacing it would make the release page
    # describe whatever was uploaded last rather than what was published.
    It "refuses to replace a complete asset whose content differs, deleting nothing" {
        $file = New-FixtureFile -Name "a.txt" -Content "evidence"
        $other = New-FixtureFile -Name "b.txt" -Content "other evidence"
        $fake = Get-FakeGitHub -Assets @((Get-FakeAsset -Id 3 -Name "EdFi.Api.TestContract-SBOM.zip")) -RemoteFile $other

        { Invoke-Publish -FilePath $file -Fake $fake } |
            Should -Throw -ExpectedMessage "*evidence for different bytes*"

        ($fake.Calls -join ",") | Should -BeExactly "list,download:3"
    }

    It "refuses two assets of one name rather than choosing" {
        $file = New-FixtureFile -Name "a.txt" -Content "evidence"
        $fake = Get-FakeGitHub -Assets @(
            (Get-FakeAsset -Id 4 -Name "EdFi.Api.TestContract-SBOM.zip"),
            (Get-FakeAsset -Id 5 -Name "EdFi.Api.TestContract-SBOM.zip" -State "starter")
        )

        { Invoke-Publish -FilePath $file -Fake $fake } |
            Should -Throw -ExpectedMessage "*2 assets named*"

        ($fake.Calls -join ",") | Should -BeExactly "list"
    }

    It "refuses an asset in a state it does not know" {
        $file = New-FixtureFile -Name "a.txt" -Content "evidence"
        $fake = Get-FakeGitHub -Assets @((Get-FakeAsset -Id 6 -Name "EdFi.Api.TestContract-SBOM.zip" -State "open"))

        { Invoke-Publish -FilePath $file -Fake $fake } |
            Should -Throw -ExpectedMessage "*state 'open'*"

        ($fake.Calls -join ",") | Should -BeExactly "list"
    }

    It "matches the asset name ordinally, so a case difference is another asset" {
        $file = New-FixtureFile -Name "a.txt" -Content "evidence"
        $fake = Get-FakeGitHub -Assets @((Get-FakeAsset -Id 7 -Name "edfi.api.testcontract-sbom.zip"))

        $result = Invoke-Publish -FilePath $file -Fake $fake

        $result.Action | Should -BeExactly "uploaded"
    }

    # A culture comparison ignores U+00AD, so a starter whose name differs only by a soft hyphen
    # would have been selected as this asset and deleted. It is another asset, and it stays.
    It "does not select or delete a starter whose name differs by an ignorable character" {
        $file = New-FixtureFile -Name "a.txt" -Content "evidence"
        $lookalike = "EdFi.Api.TestContract-SBOM$([char]0x00AD).zip"
        $fake = Get-FakeGitHub -Assets @((Get-FakeAsset -Id 10 -Name $lookalike -State "starter"))

        $result = Invoke-Publish -FilePath $file -Fake $fake

        $result.Action | Should -BeExactly "uploaded"
        ($fake.Calls -join ",") | Should -BeExactly "list,upload:EdFi.Api.TestContract-SBOM.zip"
    }

    It "propagates an upload failure and deletes nothing" {
        $file = New-FixtureFile -Name "a.txt" -Content "evidence"
        $fake = Get-FakeGitHub -UploadError "uploads.github.com answered 502"

        { Invoke-Publish -FilePath $file -Fake $fake } |
            Should -Throw -ExpectedMessage "*502*"

        ($fake.Calls | Where-Object { $_ -like "delete:*" }).Count | Should -Be 0
    }

    It "checks the local content against the expected digest before any request" {
        $file = New-FixtureFile -Name "a.txt" -Content "evidence"
        $fake = Get-FakeGitHub

        { Invoke-Publish -FilePath $file -Fake $fake -ExpectedDigest ("00" * 32) } |
            Should -Throw -ExpectedMessage "*was expected*"

        $fake.Calls.Count | Should -Be 0
    }

    It "checks a malformed local SBOM archive before any request" {
        $archive = Get-ArchivePath
        $zip = [System.IO.Compression.ZipFile]::Open($archive, [System.IO.Compression.ZipArchiveMode]::Create)
        [void] $zip.CreateEntry("manifest.spdx.json")
        [void] $zip.CreateEntry("extra.txt")
        $zip.Dispose()
        $fake = Get-FakeGitHub

        { Invoke-Publish -FilePath $archive -Fake $fake -ContentDigest { param([string] $Path) Get-SbomArchiveContentDigest -Path $Path } } |
            Should -Throw -ExpectedMessage "*2 entries*"

        $fake.Calls.Count | Should -Be 0
    }

    It "refuses an empty file" {
        $file = New-FixtureFile -Name "empty.txt" -Content ""
        $fake = Get-FakeGitHub

        { Invoke-Publish -FilePath $file -Fake $fake } | Should -Throw -ExpectedMessage "*is empty*"

        $fake.Calls.Count | Should -Be 0
    }

    It "requires a token when any GitHub seam is the default" {
        $file = New-FixtureFile -Name "a.txt" -Content "evidence"

        {
            & $script:publisher `
                -Repository "Ed-Fi-Alliance-OSS/Data-Management-Service" `
                -ReleaseId "123" `
                -AssetName "a.txt" `
                -FilePath $file `
                -WorkingDirectory (Get-ScratchPath)
        } | Should -Throw -ExpectedMessage "*token is required*"
    }
}

Describe "Provenance is compared as whole bytes" {
    It "skips only for the byte-identical envelope" {
        $envelope = New-ProvenanceEnvelope
        $fake = Get-FakeGitHub -Assets @((Get-FakeAsset -Id 8 -Name "edfiApiTest.intoto.jsonl")) -RemoteFile $envelope

        $result = Invoke-Publish -FilePath $envelope -Fake $fake -AssetName "edfiApiTest.intoto.jsonl" `
            -ContentDigest { param([string] $Path) Get-ProvenanceContentDigest -Path $Path }

        $result.Action | Should -BeExactly "skipped"
    }

    # The subject inside is identical; only the signature differs. A comparison that read the subject
    # would call these the same evidence, and a fabricated envelope quoting the package hash would
    # pass for the generator's.
    It "refuses an envelope that repeats the subject with another signature" {
        $original = New-ProvenanceEnvelope -Signature "c2lnbmF0dXJl"
        $lookalike = New-ProvenanceEnvelope -Signature "Zm9yZ2Vk"
        $fake = Get-FakeGitHub -Assets @((Get-FakeAsset -Id 9 -Name "edfiApiTest.intoto.jsonl")) -RemoteFile $original

        { Invoke-Publish -FilePath $lookalike -Fake $fake -AssetName "edfiApiTest.intoto.jsonl" `
                -ContentDigest { param([string] $Path) Get-ProvenanceContentDigest -Path $Path } } |
            Should -Throw -ExpectedMessage "*evidence for different bytes*"

        ($fake.Calls | Where-Object { $_ -like "delete:*" -or $_ -like "upload:*" }).Count | Should -Be 0
    }
}

Describe "The SBOM archive digest" {
    It "equals the manifest's own digest for an archive holding exactly the manifest" {
        $manifest = Write-ManifestFixture
        $archive = New-SbomArchive -ManifestPath $manifest -DestinationPath (Get-ArchivePath)

        Get-SbomArchiveContentDigest -Path $archive | Should -BeExactly (Get-FileSha256 -Path $manifest)
    }

    # Two archives of one manifest differ as bytes, which is the whole reason the digest reads the
    # entry rather than the file.
    It "is the same for two archives of one manifest whose bytes differ" {
        $manifest = Write-ManifestFixture
        $first = New-SbomArchive -ManifestPath $manifest -DestinationPath (Get-ArchivePath)
        Start-Sleep -Milliseconds 1100
        $second = New-SbomArchive -ManifestPath $manifest -DestinationPath (Get-ArchivePath)

        Get-SbomArchiveContentDigest -Path $first | Should -BeExactly (Get-SbomArchiveContentDigest -Path $second)
    }

    It "refuses an archive with an extra entry" {
        $archive = Get-ArchivePath
        $zip = [System.IO.Compression.ZipFile]::Open($archive, [System.IO.Compression.ZipArchiveMode]::Create)
        [void] $zip.CreateEntry("manifest.spdx.json")
        [void] $zip.CreateEntry("manifest.spdx.json.sha256")
        $zip.Dispose()

        { Get-SbomArchiveContentDigest -Path $archive } | Should -Throw -ExpectedMessage "*2 entries*"
    }

    It "refuses an archive whose one entry has another name" {
        $archive = Get-ArchivePath
        $zip = [System.IO.Compression.ZipFile]::Open($archive, [System.IO.Compression.ZipArchiveMode]::Create)
        [void] $zip.CreateEntry("_manifest/spdx_2.2/manifest.spdx.json")
        $zip.Dispose()

        { Get-SbomArchiveContentDigest -Path $archive } | Should -Throw -ExpectedMessage "*rather than manifest.spdx.json*"
    }

    It "refuses a file that is not an archive" {
        $file = New-FixtureFile -Name "not-a-zip.zip" -Content "plain text"

        { Get-SbomArchiveContentDigest -Path $file } | Should -Throw -ExpectedMessage "*not a zip archive*"
    }

    It "New-SbomArchive refuses to overwrite" {
        $manifest = Write-ManifestFixture
        $archive = New-SbomArchive -ManifestPath $manifest -DestinationPath (Get-ArchivePath)

        { New-SbomArchive -ManifestPath $manifest -DestinationPath $archive } |
            Should -Throw -ExpectedMessage "*already exists*"
    }
}

Describe "ConvertFrom-Sha256SumBase64" {
    It "reads the digest out of a pack job's hash-code output" {
        $digest = "de6085e140789bf2bd4583a03eec3597944360ef01aeeb69d07999afb0f517a9"
        $line = "$digest  EdFi.Api.Plugins.1.0.0.nupkg`n"
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($line))

        ConvertFrom-Sha256SumBase64 -Value $encoded | Should -BeExactly $digest
    }

    It "lowercases an uppercase digest" {
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(("AB" * 32) + "  x.nupkg"))

        ConvertFrom-Sha256SumBase64 -Value $encoded | Should -BeExactly ("ab" * 32)
    }

    It "refuses text that is not base64" {
        { ConvertFrom-Sha256SumBase64 -Value "not base64!" } | Should -Throw -ExpectedMessage "*not base64*"
    }

    It "refuses a line that does not begin with a digest" {
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("hello  x.nupkg"))

        { ConvertFrom-Sha256SumBase64 -Value $encoded } | Should -Throw -ExpectedMessage "*does not begin with a SHA-256*"
    }
}
