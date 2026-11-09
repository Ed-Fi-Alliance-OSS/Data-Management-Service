# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Pins the one-time 8.1.0 backfill of EdFi.Api.DocumentCacheAdmin: the dispatch-only workflow that builds
# the v8.1.0 tag's commit as exactly 8.1.0 and publishes it, and the module that guards it. This file is
# deleted together with the workflow and the module once the backfill has run.

BeforeAll {
    $script:repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
}

Describe 'DocumentCacheAdminBackfill module' {
    BeforeAll {
        Import-Module (Join-Path $script:repoRoot 'package-helpers.psm1') -Force -ErrorAction Stop
        Import-Module (Join-Path $script:repoRoot 'eng/ci/DocumentCacheAdminBackfill.psm1') -Force -ErrorAction Stop
        $script:feed = 'https://feed.invalid/_packaging/EdFi/nuget/v3/index.json'
        $script:packageName = 'EdFi.Api.DocumentCacheAdmin'

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

    Context 'Assert-DocumentCacheAdminBackfillVersion' {
        It 'accepts the version that equals the tag without its v' {
            { Assert-DocumentCacheAdminBackfillVersion -ReleaseTag 'v8.1.0' -Version '8.1.0' } | Should -Not -Throw
        }

        It 'rejects the version <Version> for tag v8.1.0' -ForEach @(
            @{ Version = '8.1.1-alpha.0.1' }
            @{ Version = '8.1.0-beta2' }
            @{ Version = '8.1.00' }
        ) {
            { Assert-DocumentCacheAdminBackfillVersion -ReleaseTag 'v8.1.0' -Version $Version } | Should -Throw
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
            # Mock scopes: the push runs inside DocumentCacheAdminBackfill, so dotnet is mocked there.
            # Test-PackageInView and its feed lookups run inside package-helpers, so Invoke-RestMethod
            # is mocked there, exactly as the SecurityMetadataTools publication tests do.
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
        }

        It 'pushes exactly once through dotnet nuget push when the version is absent from the Local view' {
            Invoke-TestPublication
            $script:pushes.Count | Should -Be 1
            $script:pushes[0] | Should -Be @('nuget', 'push', $script:packageFile, '--api-key', 'test-only', '--source', $script:feed)
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

        It 'refuses a version already in the Local view and does not push' {
            $script:published.Add('8.1.0')
            { Invoke-TestPublication } | Should -Throw '*already published*'
            $script:pushes.Count | Should -Be 0
            # The mock answers every view alike, so pin the view the lookup was scoped to.
            Should -Invoke -ModuleName package-helpers Invoke-RestMethod -Times 1 -ParameterFilter { $Uri -like '*/_packaging/*@Local/*' }
        }

        It 'throws when the push exits nonzero' {
            $script:pushExit = 1
            { Invoke-TestPublication } | Should -Throw
            $script:pushes.Count | Should -Be 1
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

        $script:trustedGate = "if: `${{ inputs.publish_package && github.ref_type == 'branch' && github.ref == 'refs/heads/main' }}"
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

    It 'validates the tag, then checks out that tag, and builds the exact version from it' {
        $build = Get-Job 'build'
        $build | Should -Not -BeNullOrEmpty
        $build | Should -Not -Match 'publish_package'
        $build | Should -Match 'ref: \$\{\{ github\.sha \}\}'
        $build | Should -Match '(?m)^\s+RELEASE_TAG: \$\{\{ inputs\.release_tag \}\}\r?$'

        $order = @(
            'ref: ${{ github.sha }}'
            'Assert-DocumentCacheAdminBackfillTag'
            'ref: refs/tags/${{ inputs.release_tag }}'
            'Get-VersionNumber -projectPrefix "dms"'
            'Assert-DocumentCacheAdminBackfillVersion'
            './build-dms.ps1 -Command BuildAndPublish'
            './build-dms.ps1 -Command Package'
            'Invoke-DocumentCacheAdminPackageSmoke.ps1'
            'actions/upload-artifact'
        ) | ForEach-Object { $build.IndexOf($_, [StringComparison]::Ordinal) }
        $order | ForEach-Object { $_ | Should -BeGreaterThan -1 }
        for ($i = 1; $i -lt $order.Count; $i++) {
            $order[$i] | Should -BeGreaterThan $order[$i - 1] -Because "step $i must follow step $($i - 1)"
        }

        $tagCheckout = [regex]::Match($build, '(?m)- uses: actions/checkout@[^\r\n]*\r?\n(?:(?!      - )[^\r\n]*\r?\n)*?\s+ref: refs/tags/\$\{\{ inputs\.release_tag \}\}\r?\n(?:(?!      - )[^\r\n]*\r?\n?)*').Value
        $tagCheckout | Should -Not -BeNullOrEmpty
        $tagCheckout | Should -Match '(?m)^\s+path: source\r?$'
        $tagCheckout | Should -Match '(?m)^\s+fetch-depth: 0\r?$'

        # A command may continue onto later lines with a trailing backtick, as the release jobs write them.
        $build | Should -Match 'build-dms\.ps1 -Command BuildAndPublish(?:[^\r\n]|`\r?\n)*?-LockedMode'
        $build | Should -Match 'build-dms\.ps1 -Command Package(?:[^\r\n]|`\r?\n)*?-PackageTarget DocumentCacheAdmin(?:[^\r\n]|`\r?\n)*?-LockedMode|build-dms\.ps1 -Command Package(?:[^\r\n]|`\r?\n)*?-LockedMode(?:[^\r\n]|`\r?\n)*?-PackageTarget DocumentCacheAdmin'
        $build | Should -Match 'Invoke-DocumentCacheAdminPackageSmoke\.ps1(?:[^\r\n]|`\r?\n)*?-SkipPackageBuild'
        $build | Should -Match 'EdFi\.Api\.DocumentCacheAdmin-NuGet'
        $build | Should -Match '(?m)^\s+working-directory: source\r?$|cd source'
        foreach ($output in 'version', 'sha256', 'hash-code') {
            $build | Should -Match "(?m)^      ${output}: " -Because "build exposes $output"
        }
    }

    It 'always attests the exact artifact, with no publish switch' {
        $sbom = Get-Job 'sbom-create-document-cache-admin'
        $sbom | Should -Not -BeNullOrEmpty
        Get-JobNeed $sbom | Should -Be @('build')
        $sbom | Should -Not -Match 'publish_package'

        $provenance = Get-Job 'provenance-create-document-cache-admin'
        $provenance | Should -Not -BeNullOrEmpty
        $provenance | Should -Match ([regex]::Escape('uses: Ed-Fi-Alliance-OSS/slsa-github-generator/.github/workflows/generator_generic_slsa3.yml@v2.0.0'))
        $provenance | Should -Match ([regex]::Escape('base64-subjects: ${{ needs.build.outputs.hash-code }}'))
        $provenance | Should -Match 'provenance-name: edfiApiDocumentCacheAdmin\.intoto\.jsonl'
        $provenance | Should -Not -Match 'publish_package'
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
        Get-JobNeed $attach | Should -Be @('build', 'provenance-create-document-cache-admin', 'publish', 'sbom-create-document-cache-admin')
        $attach | Should -Match '(?m)^      contents: write\r?$'
        $attach | Should -Match 'EdFi\.Api\.DocumentCacheAdmin-SBOM\.zip'
        $attach | Should -Match 'edfiApiDocumentCacheAdmin\.intoto\.jsonl'
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
