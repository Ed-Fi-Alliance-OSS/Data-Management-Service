# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Pins the one-time backfill of EdFi.Api.DocumentCacheAdmin: the dispatch-only workflow that builds the
# commit it was dispatched from, at the version of a stable release tag whose build inputs that commit
# shares, and publishes it, and the module that guards it. This file is deleted together with the
# workflow and the module once 8.1.0 has been backfilled.

BeforeAll {
    $script:repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
}

Describe 'DocumentCacheAdminBackfill module' {
    BeforeAll {
        # The two feed modules first, so the module under test shares the instances the mocks reach.
        # No -Force, for the reason package-helpers.psm1 gives: re-importing a loaded module replaces the
        # instance other modules already hold, and a later file's mocks would then miss their calls.
        Import-Module (Join-Path $script:repoRoot 'package-helpers.psm1') -ErrorAction Stop
        Import-Module (Join-Path $script:repoRoot 'eng/verification/ContractFeed.psm1') -ErrorAction Stop
        Import-Module (Join-Path $script:repoRoot 'eng/ci/DocumentCacheAdminBackfill.psm1') -ErrorAction Stop
        $script:feed = 'https://feed.invalid/_packaging/EdFi/nuget/v3/index.json'
        $script:packageName = 'EdFi.Api.DocumentCacheAdmin'
        $script:readme = 'src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md'

        function New-BackfillPackage {
            [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes only a disposable package in Pester TestDrive.')]
            param([string] $Version, [string] $Id = 'EdFi.Api.DocumentCacheAdmin')
            $directory = New-Item -ItemType Directory (Join-Path $TestDrive ([guid]::NewGuid()))
            Set-Content (Join-Path $directory "$script:packageName.nuspec") "<package><metadata><id>$Id</id><version>$Version</version></metadata></package>"
            $path = "$directory.nupkg"
            [IO.Compression.ZipFile]::CreateFromDirectory($directory, $path)
            return $path
        }

        function Invoke-TestPublication {
            Publish-DocumentCacheAdminBackfill -Version $script:version -PackageFile $script:packageFile `
                -Sha256 $script:hash -ServiceIndexUrl $script:feed -ApiKey 'test-only'
        }
    }

    Context 'Assert-DocumentCacheAdminBackfillTag' {
        BeforeEach {
            $script:lookups = [Collections.Generic.List[string]]::new()
            $script:release = [pscustomobject]@{ tag_name = 'v8.1.0'; draft = $false; prerelease = $false }
            $script:getRelease = {
                param($Tag)
                $script:lookups.Add($Tag)
                return $script:release
            }
        }

        It 'accepts v8.1.0 when its GitHub release is neither a draft nor a prerelease' {
            { Assert-DocumentCacheAdminBackfillTag -ReleaseTag 'v8.1.0' -GetRelease $script:getRelease } | Should -Not -Throw
            $script:lookups | Should -Be @('v8.1.0')
        }

        It 'rejects the malformed tag <Tag> without looking up a release' -ForEach @(
            @{ Tag = 'v8.1.0-beta2' }
            @{ Tag = '8.1.0' }
            @{ Tag = 'main' }
            @{ Tag = "v8.1.0`n" }
        ) {
            { Assert-DocumentCacheAdminBackfillTag -ReleaseTag $Tag -GetRelease $script:getRelease } | Should -Throw
            $script:lookups.Count | Should -Be 0
        }

        It 'rejects a tag with no GitHub release' {
            $script:release = $null
            { Assert-DocumentCacheAdminBackfillTag -ReleaseTag 'v8.1.0' -GetRelease $script:getRelease } | Should -Throw
        }

        It 'rejects a draft release' {
            $script:release = [pscustomobject]@{ tag_name = 'v8.1.0'; draft = $true; prerelease = $false }
            { Assert-DocumentCacheAdminBackfillTag -ReleaseTag 'v8.1.0' -GetRelease $script:getRelease } | Should -Throw
        }

        It 'rejects a prerelease release' {
            $script:release = [pscustomobject]@{ tag_name = 'v8.1.0'; draft = $false; prerelease = $true }
            { Assert-DocumentCacheAdminBackfillTag -ReleaseTag 'v8.1.0' -GetRelease $script:getRelease } | Should -Throw
        }
    }

    Context 'Get-DocumentCacheAdminBackfillVersion' {
        It 'returns the release tag without its v' {
            Get-DocumentCacheAdminBackfillVersion -ReleaseTag 'v8.1.0' | Should -BeExactly '8.1.0'
        }

        It 'refuses the tag <Tag>, which is not a stable vMAJOR.MINOR.PATCH tag' -ForEach @(
            @{ Tag = 'v8.1.0-beta2' }
            @{ Tag = '8.1.0' }
            @{ Tag = 'main' }
            @{ Tag = "v8.1.0`n" }
            @{ Tag = '' }
        ) {
            { Get-DocumentCacheAdminBackfillVersion -ReleaseTag $Tag } | Should -Throw
        }
    }

    Context 'Get-DocumentCacheAdminBackfillSourceInput' {
        It 'lists every build input and excludes only the tool README' {
            Get-DocumentCacheAdminBackfillSourceInput | Should -Be @(
                'src'
                'build-dms.ps1'
                'eng/build-helpers.psm1'
                'eng/docker-compose/effective-schema-hash.psm1'
                'package-helpers.psm1'
                'eng/verification/ContractPackageComparison.psm1'
                'eng/docker-compose/dms-schema-environment.psm1'
                'eng/docker-compose/database-safety.psm1'
                'eng/schema-package-utility.psm1'
                '.config'
                '.editorconfig'
                '.gitattributes'
                'global.json'
                'Directory.Build.rsp'
                ":(exclude)$script:readme"
            )
        }

        It 'names only paths that exist in this repository, so a misspelled input cannot leave the gate blind to it' {
            # global.json and Directory.Build.rsp are read only when present, and are absent today.
            foreach ($entry in Get-DocumentCacheAdminBackfillSourceInput | Where-Object { $_ -notin 'global.json', 'Directory.Build.rsp' }) {
                $path = $entry -replace '^:\(exclude\)', ''
                Test-Path -LiteralPath (Join-Path $script:repoRoot $path) | Should -BeTrue -Because "$path is a gate input"
            }
        }
    }

    Context 'Assert-DocumentCacheAdminBackfillSource' {
        BeforeAll {
            function Invoke-FixtureGit {
                # Signing is switched off because a developer's global config may require it.
                & git -c user.name=Test -c user.email=test@example.invalid -c commit.gpgsign=false -c tag.gpgsign=false @args | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "git $args failed ($LASTEXITCODE)." }
            }

            function Set-FixtureFile {
                [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes only a disposable repository in Pester TestDrive.')]
                param([string] $Path, [string] $Content)
                $full = Join-Path (Get-Location) $Path
                New-Item -ItemType Directory -Force (Split-Path $full -Parent) | Out-Null
                Set-Content -LiteralPath $full -Value $Content -NoNewline
            }

            function Save-FixtureChange {
                param([string[]] $Path)
                foreach ($file in $Path) { Set-FixtureFile $file "changed after the tag: $file" }
                Invoke-FixtureGit add -A
                Invoke-FixtureGit commit -q -m 'after the tag'
            }
        }

        BeforeEach {
            # A real repository: the tag commit holds source under src/, the build script, a script
            # outside the inputs and the tool README, as the release tag does.
            $script:fixture = New-Item -ItemType Directory (Join-Path $TestDrive ([guid]::NewGuid()))
            Push-Location -LiteralPath $script:fixture
            Invoke-FixtureGit init -q
            Set-FixtureFile 'src/dms/core/Lib.cs' 'class Lib { }'
            Set-FixtureFile 'build-dms.ps1' 'param()'
            Set-FixtureFile 'eng/ci/x.ps1' 'param()'
            Set-FixtureFile $script:readme '# DocumentCacheAdmin'
            Invoke-FixtureGit add -A
            Invoke-FixtureGit commit -q -m 'release'
            Invoke-FixtureGit tag v8.1.0
        }

        AfterEach {
            Pop-Location
        }

        It 'passes when the dispatch commit is the tag commit' {
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0' } | Should -Not -Throw
        }

        It 'passes when only the tool README changed after the tag' {
            Save-FixtureChange $script:readme
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0' } | Should -Not -Throw
        }

        It 'passes when only a file outside the build inputs changed after the tag' {
            Save-FixtureChange 'eng/ci/x.ps1'
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0' } | Should -Not -Throw
        }

        It 'passes for an annotated tag, peeled to its commit' {
            Invoke-FixtureGit tag -a v8.1.1 -m 'annotated'
            Save-FixtureChange 'eng/ci/x.ps1'
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.1' } | Should -Not -Throw
        }

        It 'refuses a change under src and names the tag and the path' {
            Save-FixtureChange 'src/dms/core/Lib.cs'
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0' } | Should -Throw "*v8.1.0*src/dms/core/Lib.cs*"
        }

        It 'refuses a file added under src after the tag' {
            Save-FixtureChange 'src/dms/core/Added.cs'
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0' } | Should -Throw '*src/dms/core/Added.cs*'
        }

        It 'refuses a change to build-dms.ps1 and names it' {
            Save-FixtureChange 'build-dms.ps1'
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0' } | Should -Throw '*build-dms.ps1*'
        }

        It 'lists only the differing inputs, never the README or a file outside the inputs' {
            Save-FixtureChange 'src/dms/core/Lib.cs', $script:readme, 'eng/ci/x.ps1'
            $message = try { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0'; '' } catch { $_.Exception.Message }
            $message | Should -BeLike '*src/dms/core/Lib.cs*'
            $message | Should -Not -BeLike '*README.md*'
            $message | Should -Not -BeLike '*eng/ci/x.ps1*'
        }

        It 'compares from the repository root when run in a subdirectory' {
            Save-FixtureChange 'src/dms/core/Lib.cs'
            Push-Location -LiteralPath (Join-Path $script:fixture 'eng/ci')
            try {
                { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0' } | Should -Throw '*src/dms/core/Lib.cs*'
            }
            finally {
                Pop-Location
            }
        }

        It 'refuses a release tag that does not exist' {
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.2' } | Should -Throw '*v8.1.2*'
        }

        It 'refuses an existing tag that is not a stable vMAJOR.MINOR.PATCH tag, though its inputs are equal' {
            Invoke-FixtureGit tag v8.2.0-beta.1
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.2.0-beta.1' } | Should -Throw
        }

        It 'throws when git diff exits with neither 0 nor 1' {
            Mock -ModuleName DocumentCacheAdminBackfill git {
                if ($args -contains 'diff') {
                    $global:LASTEXITCODE = 128
                    return
                }
                $global:LASTEXITCODE = 0
                return $(if ($args -contains '--show-toplevel') { $script:fixture.FullName } else { '0123456789abcdef0123456789abcdef01234567' })
            }
            { Assert-DocumentCacheAdminBackfillSource -ReleaseTag 'v8.1.0' } | Should -Throw '*128*'
        }
    }

    Context 'Publish-DocumentCacheAdminBackfill' {
        BeforeEach {
            $script:version = '8.1.0'
            $script:packageFile = New-BackfillPackage $script:version
            $script:hash = (Get-FileHash $script:packageFile).Hash
            $script:published = [Collections.Generic.List[string]]::new()
            $script:pushExit = 0
            $script:pushes = [Collections.Generic.List[object]]::new()
            $script:feedBytes = [IO.File]::ReadAllBytes($script:packageFile)
            $script:downloadFails = $false
            # Mock scopes: the push runs inside DocumentCacheAdminBackfill, so dotnet is mocked there.
            # Test-PackageInView and its feed lookups run inside package-helpers, so Invoke-RestMethod
            # is mocked there, exactly as the SecurityMetadataTools publication tests do. The download
            # of a version already on the feed runs through the ContractFeed seams, so its service
            # index read and its package download are mocked in ContractFeed.
            Mock -ModuleName DocumentCacheAdminBackfill dotnet {
                $script:pushes.Add(@($args))
                $global:LASTEXITCODE = $script:pushExit
            }
            Mock -ModuleName package-helpers Invoke-RestMethod {
                if ($Uri -like '*_packaging/*') {
                    return @{ resources = @(@{ '@id' = 'https://feed.invalid/Local/flat2/'; '@type' = 'PackageBaseAddress/3.0.0' }) }
                }
                if ($Uri -like '*/Local/flat2/*') { return @{ versions = @($script:published) } }
                throw "Unexpected feed request: $Uri"
            }
            Mock -ModuleName ContractFeed Invoke-RestMethod {
                if ($Uri -like '*_packaging/*') {
                    return @{ resources = @(@{ '@id' = 'https://feed.invalid/Local/flat2/'; '@type' = 'PackageBaseAddress/3.0.0' }) }
                }
                throw "Unexpected feed request: $Uri"
            }
            Mock -ModuleName ContractFeed Invoke-WebRequest {
                if ($script:downloadFails) { throw 'Response status code does not indicate success: 500 (Internal Server Error).' }
                [IO.File]::WriteAllBytes($OutFile, $script:feedBytes)
            }
        }

        It 'pushes exactly once through dotnet nuget push when the version is absent from the Local view' {
            Invoke-TestPublication
            $script:pushes.Count | Should -Be 1
            $script:pushes[0] | Should -Be @('nuget', 'push', $script:packageFile, '--api-key', 'test-only', '--source', $script:feed)
            Should -Invoke -ModuleName ContractFeed Invoke-WebRequest -Times 0 -Exactly
        }

        It 'refuses altered bytes before any feed call' {
            $script:hash = ('0' * 64)
            { Invoke-TestPublication } | Should -Throw
            Should -Invoke -ModuleName package-helpers Invoke-RestMethod -Times 0 -Exactly
            $script:pushes.Count | Should -Be 0
        }

        It 'refuses a package whose nuspec id is not EdFi.Api.DocumentCacheAdmin' {
            $script:packageFile = New-BackfillPackage $script:version -Id 'EdFi.Api.Other'
            $script:hash = (Get-FileHash $script:packageFile).Hash
            { Invoke-TestPublication } | Should -Throw
            $script:pushes.Count | Should -Be 0
        }

        It 'refuses a package whose nuspec version differs from the requested version' {
            $script:packageFile = New-BackfillPackage '8.1.1'
            $script:hash = (Get-FileHash $script:packageFile).Hash
            { Invoke-TestPublication } | Should -Throw
            $script:pushes.Count | Should -Be 0
        }

        It 'continues without pushing when the Local view already holds these exact bytes' {
            $script:published.Add('8.1.0')
            { Invoke-TestPublication } | Should -Not -Throw
            $script:pushes.Count | Should -Be 0
            # The mock answers every view alike, so pin the view the lookup was scoped to.
            Should -Invoke -ModuleName package-helpers Invoke-RestMethod -Times 1 -ParameterFilter { $Uri -like '*/_packaging/*@Local/*' }
            # The copy is read anonymously from the Local view's flat container, at this exact version.
            Should -Invoke -ModuleName ContractFeed Invoke-RestMethod -Times 1 -Exactly -ParameterFilter {
                $Uri -ceq 'https://feed.invalid/_packaging/EdFi@Local/nuget/v3/index.json' -and -not $Headers.ContainsKey('Authorization')
            }
            Should -Invoke -ModuleName ContractFeed Invoke-WebRequest -Times 1 -Exactly -ParameterFilter {
                $Uri -ceq 'https://feed.invalid/Local/flat2/edfi.api.documentcacheadmin/8.1.0/edfi.api.documentcacheadmin.8.1.0.nupkg' -and
                -not $Headers.ContainsKey('Authorization')
            }
        }

        It 'refuses, without pushing, a version already in the Local view with different bytes' {
            $script:published.Add('8.1.0')
            $script:feedBytes = [Text.Encoding]::UTF8.GetBytes('another build of 8.1.0')
            { Invoke-TestPublication } | Should -Throw '*already published with different bytes*'
            $script:pushes.Count | Should -Be 0
        }

        It 'refuses, without pushing, when the copy already on the feed cannot be downloaded' {
            $script:published.Add('8.1.0')
            $script:downloadFails = $true
            { Invoke-TestPublication } | Should -Throw '*could not be downloaded*'
            $script:pushes.Count | Should -Be 0
        }

        It 'throws when the push exits nonzero' {
            $script:pushExit = 1
            { Invoke-TestPublication } | Should -Throw
            $script:pushes.Count | Should -Be 1
        }
    }

    Context 'Publish-DocumentCacheAdminBackfillReleaseAsset' {
        BeforeAll {
            $script:sbomName = 'EdFi.Api.DocumentCacheAdmin-SBOM.zip'
            $script:provenanceName = 'edfiApiDocumentCacheAdmin.intoto.jsonl'

            function New-SbomZip {
                # Built with ZipFile rather than Compress-Archive, uncompressed, so its bytes differ from
                # what the function would build from the same manifest.
                [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes only a disposable zip in Pester TestDrive.')]
                param([string] $Content, [string] $EntryName = 'manifest.spdx.json')
                $path = Join-Path $TestDrive "$([guid]::NewGuid()).zip"
                $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
                try {
                    $entry = $zip.CreateEntry($EntryName, [IO.Compression.CompressionLevel]::NoCompression)
                    $writer = [IO.StreamWriter]::new($entry.Open())
                    try { $writer.Write($Content) }
                    finally { $writer.Dispose() }
                }
                finally { $zip.Dispose() }
                return $path
            }

            function New-AssetFile {
                [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Writes only a disposable file in Pester TestDrive.')]
                param([string] $Content)
                $path = Join-Path $TestDrive ([guid]::NewGuid())
                [IO.File]::WriteAllText($path, $Content)
                return $path
            }

            function Get-UploadedManifest([byte[]] $Zip) {
                $archive = [IO.Compression.ZipArchive]::new([IO.MemoryStream]::new($Zip))
                try {
                    @($archive.Entries).Count | Should -Be 1
                    $reader = [IO.StreamReader]::new($archive.GetEntry('manifest.spdx.json').Open())
                    try { return $reader.ReadToEnd() }
                    finally { $reader.Dispose() }
                }
                finally { $archive.Dispose() }
            }

            function Invoke-TestAttach {
                Publish-DocumentCacheAdminBackfillReleaseAsset -ReleaseTag 'v8.1.0' `
                    -ManifestFile $script:manifest -ProvenanceFile $script:provenance `
                    -GetRelease $script:getRelease -DownloadAsset $script:downloadAsset -UploadAsset $script:uploadAsset
            }
        }

        BeforeEach {
            $script:manifestText = '{"spdxVersion":"SPDX-2.2","name":"EdFi.Api.DocumentCacheAdmin 8.1.0"}'
            $script:provenanceText = '{"payloadType":"application/vnd.in-toto+json"}'
            $directory = New-Item -ItemType Directory (Join-Path $TestDrive ([guid]::NewGuid()))
            $script:manifest = Join-Path $directory 'manifest.spdx.json'
            [IO.File]::WriteAllText($script:manifest, $script:manifestText)
            $script:provenance = Join-Path $directory $script:provenanceName
            [IO.File]::WriteAllText($script:provenance, $script:provenanceText)

            # Asset name to the file holding its bytes on the release.
            $script:existing = [ordered]@{}
            $script:lookups = [Collections.Generic.List[string]]::new()
            $script:downloads = [Collections.Generic.List[string]]::new()
            $script:uploads = [Collections.Generic.List[object]]::new()
            $script:release = 'present'
            $script:getRelease = {
                param($Tag)
                $script:lookups.Add($Tag)
                if ($script:release -ne 'present') { return $null }
                return [pscustomobject]@{
                    id     = 42
                    assets = @($script:existing.Keys | ForEach-Object { [pscustomobject]@{ name = $_; url = "https://api.invalid/assets/$_" } })
                }
            }
            $script:downloadAsset = {
                param($Asset, $Destination)
                $script:downloads.Add($Asset.name)
                Copy-Item -LiteralPath $script:existing[$Asset.name] -Destination $Destination
            }
            $script:uploadAsset = {
                param($Release, $Name, $Path)
                $script:uploads.Add([pscustomobject]@{ ReleaseId = $Release.id; Name = $Name; Bytes = [IO.File]::ReadAllBytes($Path) })
            }
        }

        It 'uploads both assets when the release has neither' {
            Invoke-TestAttach
            $script:lookups | Should -Be @('v8.1.0')
            @($script:uploads.Name) | Should -Be @($script:sbomName, $script:provenanceName)
            @($script:uploads.ReleaseId) | Should -Be @(42, 42)
            Get-UploadedManifest $script:uploads[0].Bytes | Should -BeExactly $script:manifestText
            [Text.Encoding]::UTF8.GetString($script:uploads[1].Bytes) | Should -BeExactly $script:provenanceText
        }

        It 'uploads nothing when both assets already hold this content, though the SBOM zip bytes differ' {
            $script:existing[$script:sbomName] = New-SbomZip $script:manifestText
            $script:existing[$script:provenanceName] = New-AssetFile $script:provenanceText
            { Invoke-TestAttach } | Should -Not -Throw
            @($script:downloads) | Should -Be @($script:sbomName, $script:provenanceName)
            $script:uploads.Count | Should -Be 0
        }

        It 'uploads only the provenance when the SBOM is already present with this content' {
            $script:existing[$script:sbomName] = New-SbomZip $script:manifestText
            Invoke-TestAttach
            @($script:uploads.Name) | Should -Be @($script:provenanceName)
        }

        It 'refuses, uploading nothing, an SBOM whose manifest differs' {
            $script:existing[$script:sbomName] = New-SbomZip '{"spdxVersion":"SPDX-2.2","name":"another build"}'
            { Invoke-TestAttach } | Should -Throw "*$script:sbomName*different*"
            $script:uploads.Count | Should -Be 0
        }

        It 'refuses, uploading nothing, an SBOM zip that does not hold exactly the manifest' {
            $script:existing[$script:sbomName] = New-SbomZip $script:manifestText -EntryName 'other.json'
            { Invoke-TestAttach } | Should -Throw "*$script:sbomName*different*"
            $script:uploads.Count | Should -Be 0
        }

        It 'refuses, uploading nothing, a provenance whose bytes differ' {
            $script:existing[$script:provenanceName] = New-AssetFile '{"payloadType":"another"}'
            { Invoke-TestAttach } | Should -Throw "*$script:provenanceName*different*"
            $script:uploads.Count | Should -Be 0
        }

        It 'refuses a tag with no GitHub release' {
            $script:release = 'absent'
            { Invoke-TestAttach } | Should -Throw '*v8.1.0*'
            $script:uploads.Count | Should -Be 0
        }
    }
}

Describe 'DocumentCacheAdmin backfill workflow wiring' {
    BeforeAll {
        $script:workflow = Get-Content (Join-Path $script:repoRoot '.github/workflows/backfill-document-cache-admin.yml') -Raw

        function script:Get-Job([string] $Id) {
            return [regex]::Match($script:workflow, "(?ms)^  $([regex]::Escape($Id)):.*?(?=^  [A-Za-z_][\w-]*:[ \t]*\r?`$|\z)").Value
        }

        function script:Get-JobNeed([string] $Job) {
            $needs = [regex]::Match($Job, '(?m)^    needs: \[(?<ids>[^\]]*)\]\s*$').Groups['ids'].Value
            return @($needs -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Sort-Object)
        }

        function script:Get-JobStep([string] $Job) {
            # Each step of a job, from its '      - ' line up to the next one.
            $steps = [regex]::Match($Job, '(?m)^    steps:\r?$')
            if (-not $steps.Success) { return @() }
            return @([regex]::Split($Job.Substring($steps.Index + $steps.Length), '(?m)^(?=      - )') | Where-Object { $_ -match '\A      - ' })
        }

        function script:Find-Step([string] $Job, [string] $Text) {
            return @(Get-JobStep $Job | Where-Object { $_.Contains($Text) })
        }

        $script:trustedGate = "if: `${{ inputs.publish_package && github.ref_type == 'branch' && github.ref == 'refs/heads/main' }}"
        $script:feedUrl = 'https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json'
    }

    It 'is dispatch-only with exactly the release_tag and publish_package inputs' {
        $events = [regex]::Match($script:workflow, '(?ms)^on:\r?\n(.*?)^\S').Groups[1].Value
        $events | Should -Match '(?m)^  workflow_dispatch:'
        $events | Should -Not -Match 'release:|push:|workflow_call:|version:'
        $inputs = [regex]::Match($events, '(?ms)^    inputs:\r?\n(.*)').Groups[1].Value
        @([regex]::Matches($inputs, '(?m)^      (?<name>[A-Za-z_]\w*):') | ForEach-Object { $_.Groups['name'].Value }) |
            Should -Be @('release_tag', 'publish_package')
        $tag = [regex]::Match($inputs, '(?ms)^      release_tag:\r?\n(?<body>(?:        .*\r?\n)+)').Groups['body'].Value
        $tag | Should -Match '(?m)^        type: string\r?$'
        $tag | Should -Match '(?m)^        required: true\r?$'
        $publish = [regex]::Match($inputs, '(?ms)^      publish_package:\r?\n(?<body>(?:        .*\r?\n?)+)').Groups['body'].Value
        $publish | Should -Match '(?m)^        type: boolean\r?$'
        $publish | Should -Match '(?m)^        default: false\r?$'
    }

    It 'checks out only the dispatch commit, with every tag, and builds nowhere but the repository root' {
        $build = Get-Job 'build'
        $build | Should -Not -BeNullOrEmpty
        $build | Should -Not -Match 'publish_package'
        $checkouts = @(Find-Step $build 'uses: actions/checkout@')
        $checkouts.Count | Should -Be 1
        $checkouts[0] | Should -Match '(?m)^          ref: \$\{\{ github\.sha \}\}\r?$'
        $checkouts[0] | Should -Match '(?m)^          fetch-depth: 0\r?$'
        $checkouts[0] | Should -Not -Match '(?m)^          path:'
        $script:workflow | Should -Not -Match 'working-directory:'
        $script:workflow | Should -Not -Match 'refs/tags/\$\{\{|\bsource/|cd source'
    }

    It 'validates the tag, then requires the tag''s build inputs, before any build step' {
        $build = Get-Job 'build'
        $order = @(
            'uses: actions/checkout@'
            'Assert-DocumentCacheAdminBackfillTag -ReleaseTag $env:RELEASE_TAG'
            'Get-DocumentCacheAdminBackfillSourceInput'
            'Assert-DocumentCacheAdminBackfillSource -ReleaseTag $env:RELEASE_TAG'
            'Get-DocumentCacheAdminBackfillVersion -ReleaseTag $env:RELEASE_TAG'
            './build-dms.ps1 -Command BuildAndPublish'
            './build-dms.ps1 -Command Package'
            './eng/ci/Invoke-DocumentCacheAdminPackageSmoke.ps1'
            'actions/upload-artifact'
        ) | ForEach-Object { $build.IndexOf($_, [StringComparison]::Ordinal) }
        $order | ForEach-Object { $_ | Should -BeGreaterThan -1 }
        for ($i = 1; $i -lt $order.Count; $i++) {
            $order[$i] | Should -BeGreaterThan $order[$i - 1] -Because "step $i must follow step $($i - 1)"
        }

        foreach ($call in 'Assert-DocumentCacheAdminBackfillTag', 'Assert-DocumentCacheAdminBackfillSource', 'Get-DocumentCacheAdminBackfillVersion') {
            $step = @(Find-Step $build $call)
            $step.Count | Should -Be 1 -Because "one step calls $call"
            $step[0] | Should -Match '(?m)^          RELEASE_TAG: \$\{\{ inputs\.release_tag \}\}\r?$' -Because "$call reads the tag through env"
        }
    }

    It 'stamps the version from the release tag, never from MinVer' {
        $build = Get-Job 'build'
        $build | Should -Not -Match 'Get-VersionNumber|minver|steps\.versions\.'
        $version = @(Find-Step $build 'Get-DocumentCacheAdminBackfillVersion')
        $version[0] | Should -Match '(?m)^        id: version\r?$'
        $version[0] | Should -Match '"version=\$version" >> \$env:GITHUB_OUTPUT'
        foreach ($call in './build-dms.ps1 -Command BuildAndPublish', './build-dms.ps1 -Command Package', 'Invoke-DocumentCacheAdminPackageSmoke.ps1', 'id: package') {
            $step = @(Find-Step $build $call)
            $step.Count | Should -Be 1 -Because "one step runs $call"
            $step[0] | Should -Match '(?m)^          APP_VERSION: \$\{\{ steps\.version\.outputs\.version \}\}\r?$' -Because "$call uses the tag's version"
        }
    }

    It 'builds, packs and smoke-tests with the release flags, and uploads the package from the repository root' {
        $build = Get-Job 'build'
        $buildStep = @(Find-Step $build './build-dms.ps1 -Command BuildAndPublish')[0]
        foreach ($flag in '-Configuration Release', '-DMSVersion $env:APP_VERSION', '-LockedMode') {
            $buildStep.Contains($flag) | Should -BeTrue -Because "BuildAndPublish passes $flag"
        }
        $packageStep = @(Find-Step $build './build-dms.ps1 -Command Package')[0]
        foreach ($flag in '-DMSVersion $env:APP_VERSION', '-Configuration Release', '-PackageTarget DocumentCacheAdmin', '-LockedMode') {
            $packageStep.Contains($flag) | Should -BeTrue -Because "Package passes $flag"
        }
        $smokeStep = @(Find-Step $build 'Invoke-DocumentCacheAdminPackageSmoke.ps1')[0]
        foreach ($flag in '-DMSVersion $env:APP_VERSION', '-Configuration Release', '-SkipPackageBuild') {
            $smokeStep.Contains($flag) | Should -BeTrue -Because "the smoke passes $flag"
        }
        $upload = @(Find-Step $build 'actions/upload-artifact')
        $upload.Count | Should -Be 1
        $upload[0] | Should -Match '(?m)^          name: EdFi\.Api\.DocumentCacheAdmin-NuGet\r?$'
        $upload[0] | Should -Match '(?m)^          path: EdFi\.Api\.DocumentCacheAdmin\.\$\{\{ steps\.package\.outputs\.version \}\}\.nupkg\r?$'
        foreach ($output in 'version', 'sha256', 'hash-code') {
            $build | Should -Match "(?m)^      ${output}: \`$\{\{ steps\.package\.outputs\.${output} \}\}\r?`$" -Because "build exposes $output"
        }
    }

    It 'scans the dispatch commit for the SBOM, reads contents only, and stops when a tool fails' {
        $sbom = Get-Job 'sbom-create-document-cache-admin'
        $sbom | Should -Not -BeNullOrEmpty
        Get-JobNeed $sbom | Should -Be @('build')
        $sbom | Should -Not -Match 'publish_package'
        $sbom | Should -Match '(?m)^      contents: read\r?$'
        $sbom | Should -Not -Match '(?m)^      contents: write'

        $steps = @(Get-JobStep $sbom)
        $steps[0] | Should -Match 'uses: actions/checkout@'
        $steps[0] | Should -Match '(?m)^          ref: \$\{\{ github\.sha \}\}\r?$'

        $generate = @($steps | Where-Object { $_.Contains('sbom-tool generate') })
        $generate.Count | Should -Be 1
        $text = $generate[0]
        $text | Should -Match '-bc "\./"'
        $install = $text.IndexOf('dotnet tool install', [StringComparison]::Ordinal)
        $tool = $text.IndexOf('sbom-tool generate', [StringComparison]::Ordinal)
        $copy = $text.IndexOf('Copy-Item', [StringComparison]::Ordinal)
        $install | Should -BeGreaterThan -1
        $tool | Should -BeGreaterThan $install
        $copy | Should -BeGreaterThan $tool
        $checks = @([regex]::Matches($text, 'if \(\$LASTEXITCODE -ne 0\)') | ForEach-Object Index)
        @($checks | Where-Object { $_ -gt $install -and $_ -lt $tool }).Count | Should -Be 1 -Because 'the tool install is checked'
        @($checks | Where-Object { $_ -gt $tool -and $_ -lt $copy }).Count | Should -Be 1 -Because 'sbom-tool generate is checked'
    }

    It 'always attests the exact artifact, with no publish switch' {
        $provenance = Get-Job 'provenance-create-document-cache-admin'
        $provenance | Should -Not -BeNullOrEmpty
        $provenance | Should -Match ([regex]::Escape('uses: Ed-Fi-Alliance-OSS/slsa-github-generator/.github/workflows/generator_generic_slsa3.yml@v2.0.0'))
        $provenance | Should -Match ([regex]::Escape('base64-subjects: ${{ needs.build.outputs.hash-code }}'))
        $provenance | Should -Match 'provenance-name: edfiApiDocumentCacheAdmin\.intoto\.jsonl'
        $provenance | Should -Not -Match 'publish_package'
    }

    It 'pushes and promotes at one feed, and binds the credential provider to it for the push' {
        $workflowEnv = [regex]::Match($script:workflow, '(?m)^env:\r?\n(?<body>(?:  [^\r\n]*\r?\n)+)').Groups['body'].Value
        [regex]::Match($workflowEnv, '(?m)^  ARTIFACTS_FEED_URL: "(?<url>[^"]+)"\r?$').Groups['url'].Value | Should -BeExactly $script:feedUrl
        # No job or step sets its own feed, so every -ServiceIndexUrl below reads that one value.
        @([regex]::Matches($script:workflow, '(?m)^[ \t]+ARTIFACTS_FEED_URL:')).Count | Should -Be 1
        $script:workflow | Should -Not -Match 'vars\.AZURE_ARTIFACTS_FEED_URL'

        $push = @(Find-Step (Get-Job 'publish') 'Publish-DocumentCacheAdminBackfill')
        $push.Count | Should -Be 1
        $push[0] | Should -Match 'InstallCredentialHandler'
        $push[0] | Should -Match '-ServiceIndexUrl \$env:ARTIFACTS_FEED_URL\b'
        $endpoints = [regex]::Match($push[0], '(?m)^          VSS_NUGET_EXTERNAL_FEED_ENDPOINTS: ''(?<json>[^''\r\n]+)''\r?$').Groups['json'].Value
        $endpoints | Should -Not -BeNullOrEmpty -Because 'the credential provider authenticates the push only through this variable'
        $credentials = @(($endpoints.Replace('${{ secrets.AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN }}', 'publishing-token') | ConvertFrom-Json).endpointCredentials)
        $credentials.Count | Should -Be 1
        $credentials[0].endpoint | Should -BeExactly $script:feedUrl
        $credentials[0].password | Should -BeExactly 'publishing-token'

        $promote = @(Find-Step (Get-Job 'promote') 'Invoke-ContractPromotion')
        $promote.Count | Should -Be 1
        $promote[0] | Should -Match '-ServiceIndexUrl \$env:ARTIFACTS_FEED_URL\b'
    }

    It 'publishes, promotes and attaches with the right dependencies' {
        $publish = Get-Job 'publish'
        $publish | Should -Not -BeNullOrEmpty
        Get-JobNeed $publish | Should -Be @('build', 'provenance-create-document-cache-admin', 'sbom-create-document-cache-admin')
        $publish | Should -Match 'Publish-DocumentCacheAdminBackfill'

        $promote = Get-Job 'promote'
        $promote | Should -Not -BeNullOrEmpty
        Get-JobNeed $promote | Should -Be @('build', 'publish')
        $promote | Should -Match 'Invoke-ContractPromotion'
        $promote | Should -Match "-PackageName 'EdFi\.Api\.DocumentCacheAdmin'"
        $promote | Should -Match 'needs\.build\.outputs\.version'

        $attach = Get-Job 'attach'
        $attach | Should -Not -BeNullOrEmpty
        # promote too, so the assets reach the release only once the version is in the Release view.
        Get-JobNeed $attach | Should -Be @('build', 'promote', 'provenance-create-document-cache-admin', 'publish', 'sbom-create-document-cache-admin')
        $attach | Should -Match '(?m)^      contents: write\r?$'
    }

    It 'attaches through the module, after checking out the dispatch commit and downloading both assets' {
        $attach = Get-Job 'attach'
        $steps = @(Get-JobStep $attach)
        $steps[0] | Should -Match 'uses: actions/checkout@'
        $steps[0] | Should -Match '(?m)^          ref: \$\{\{ github\.sha \}\}\r?$'
        $order = @(
            'uses: actions/checkout@'
            'secure-download-artifact@'
            'name: edfiApiDocumentCacheAdmin.intoto.jsonl'
            'Publish-DocumentCacheAdminBackfillReleaseAsset'
        ) | ForEach-Object { $attach.IndexOf($_, [StringComparison]::Ordinal) }
        $order | ForEach-Object { $_ | Should -BeGreaterThan -1 }
        for ($i = 1; $i -lt $order.Count; $i++) {
            $order[$i] | Should -BeGreaterThan $order[$i - 1] -Because "step $i must follow step $($i - 1)"
        }
        $step = @(Find-Step $attach 'Publish-DocumentCacheAdminBackfillReleaseAsset')
        $step.Count | Should -Be 1
        $step[0] | Should -Match '(?m)^          RELEASE_TAG: \$\{\{ inputs\.release_tag \}\}\r?$'
        $step[0] | Should -Match '-ReleaseTag \$env:RELEASE_TAG\b'
        $step[0] | Should -Match '-ManifestFile \$env:MANIFEST_FILE\b'
        $step[0] | Should -Match '-ProvenanceFile edfiApiDocumentCacheAdmin\.intoto\.jsonl\b'
    }

    It 'gates <Id> on the publish switch and the main branch only' -ForEach @(
        @{ Id = 'publish' }, @{ Id = 'promote' }, @{ Id = 'attach' }
    ) {
        $job = Get-Job $Id
        $job | Should -Not -BeNullOrEmpty
        @([regex]::Matches($job, '(?m)^    if: .*$') | ForEach-Object { $_.Value.Trim() }) | Should -Be @($script:trustedGate)
    }

    It 'binds only publish and promote to the publishing environment' {
        $bound = foreach ($id in 'build', 'sbom-create-document-cache-admin', 'provenance-create-document-cache-admin', 'publish', 'promote', 'attach') {
            if ((Get-Job $id) -match '(?m)^    environment: publishing\r?$') { $id }
        }
        @($bound) | Sort-Object | Should -Be @('promote', 'publish')
    }
}

Describe 'DocumentCacheAdmin backfill test file registration' {
    BeforeAll {
        $script:pullRequestWorkflow = Get-Content (Join-Path $script:repoRoot '.github/workflows/on-dms-pullrequest.yml') -Raw
    }

    It 'is listed in the Pester allowlist, because a file under eng/ci/tests runs in no lane otherwise' {
        $script:pullRequestWorkflow | Should -Match '(?m)^\s+"eng/ci/tests/DocumentCacheAdminBackfill\.Tests\.ps1",?$'
    }
}
