# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../release-classification.psm1') -Force
    $script:entryScript = Join-Path $PSScriptRoot '../Write-PrereleaseClassification.ps1'

    # A real remote with the two tag shapes the repository carries: v8.0.0 is lightweight, as every
    # recent v tag is, and v0.2.0 is annotated, which ls-remote lists twice (the tag and its peeled
    # commit). Signing is switched off because a developer's global config may require it.
    $script:root = Join-Path ([System.IO.Path]::GetTempPath()) "release-classification-$([guid]::NewGuid().ToString('N'))"
    $script:remote = Join-Path $script:root 'remote.git'
    $work = Join-Path $script:root 'work'
    $git = @('-c', 'user.name=Test', '-c', 'user.email=test@example.com', '-c', 'commit.gpgsign=false', '-c', 'tag.gpgsign=false')
    git init -q --bare $script:remote
    git init -q $work
    git -C $work @git commit -q --allow-empty -m 'first'
    git -C $work @git tag v8.0.0
    git -C $work @git tag v8.1.0-beta.0.1
    git -C $work @git tag -a v0.2.0 -m 'annotated'
    git -C $work push -q $script:remote --tags
}

AfterAll {
    Remove-Item -LiteralPath $script:root -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'Get-PrereleaseVersion' {
    It 'returns the version a <ref> prerelease names' -ForEach @(
        @{ Ref = 'dms-pre-8.1.0-beta.0.1.3'; Version = '8.1.0-beta.0.1.3' }
        @{ Ref = 'cs-pre-8.0.1-alpha.0.204'; Version = '8.0.1-alpha.0.204' }
    ) {
        Get-PrereleaseVersion -ReleaseRef $Ref | Should -BeExactly $Version
    }

    It 'returns nothing for <ref>, which is not a DMS or Configuration Service prerelease' -ForEach @(
        @{ Ref = 'v8.1.0-beta.0.1' }
        @{ Ref = 'dms-v8.1.0' }
        @{ Ref = 'dms-pre-' }
        @{ Ref = 'DMS-pre-8.0.0' }
    ) {
        Get-PrereleaseVersion -ReleaseRef $Ref | Should -BeNullOrEmpty
    }
}

Describe 'Get-ReleaseTagVersion' {
    It 'lists every v tag once, peeling annotated tags' {
        @(Get-ReleaseTagVersion -Remote $script:remote | Sort-Object) | Should -Be @('0.2.0', '8.0.0', '8.1.0-beta.0.1')
    }

    It 'fails closed when the remote cannot be read' {
        # Answering "not tagged" here would move the pre image to a release build and skip its
        # release assets, so an unreadable remote must stop the run instead.
        { Get-ReleaseTagVersion -Remote (Join-Path $script:root 'missing.git') } | Should -Throw '*Could not list the v tags*'
    }
}

Describe 'Test-TaggedReleaseBuild' {
    BeforeAll {
        $script:tagged = @(Get-ReleaseTagVersion -Remote $script:remote)
    }

    It 'treats <ref> as a tagged release build' -ForEach @(
        @{ Ref = 'dms-pre-8.1.0-beta.0.1' }
        @{ Ref = 'cs-pre-8.1.0-beta.0.1' }
        @{ Ref = 'dms-pre-8.0.0' }
        @{ Ref = 'cs-pre-0.2.0' }
    ) {
        Test-TaggedReleaseBuild -ReleaseRef $Ref -TaggedVersion $script:tagged | Should -BeTrue
    }

    It 'treats <ref> as an ordinary main build' -ForEach @(
        # A merge after a beta tag: no "alpha" in it, and still not a release.
        @{ Ref = 'dms-pre-8.1.0-beta.0.1.3' }
        @{ Ref = 'cs-pre-8.1.0-beta.0.1.3' }
        @{ Ref = 'dms-pre-8.0.1-alpha.0.204' }
        # A prefix of a tagged version is a different version.
        @{ Ref = 'dms-pre-8.0' }
    ) {
        Test-TaggedReleaseBuild -ReleaseRef $Ref -TaggedVersion $script:tagged | Should -BeFalse
    }

    It 'refuses a ref that is not a DMS or Configuration Service prerelease' {
        { Test-TaggedReleaseBuild -ReleaseRef 'v8.1.0-beta.0.1' -TaggedVersion $script:tagged } | Should -Throw '*not a dms-pre- or cs-pre- prerelease*'
    }
}

Describe 'Test-DeletablePrerelease' {
    BeforeAll {
        $script:tagged = @(Get-ReleaseTagVersion -Remote $script:remote)
    }

    It 'deletes <ref>, an ordinary main build' -ForEach @(
        @{ Ref = 'dms-pre-8.1.0-beta.0.1.3' }
        @{ Ref = 'cs-pre-8.1.0-beta.0.1.2' }
        @{ Ref = 'dms-pre-8.0.1-alpha.0.204' }
    ) {
        Test-DeletablePrerelease -ReleaseRef $Ref -Prerelease $true -TaggedVersion $script:tagged | Should -BeTrue
    }

    It 'keeps <ref>, <case>' -ForEach @(
        @{ Ref = 'dms-pre-8.1.0-beta.0.1'; Prerelease = $true; Case = 'the build for a prerelease v tag' }
        @{ Ref = 'cs-pre-8.0.0'; Prerelease = $true; Case = 'the build for a final v tag' }
        @{ Ref = 'v8.1.0-beta.0.1'; Prerelease = $true; Case = 'the v release itself' }
        @{ Ref = '0.2.1-alpha.0.9'; Prerelease = $true; Case = 'a prerelease from before the dms-pre- and cs-pre- prefixes' }
        @{ Ref = 'dms-pre-8.0.1-alpha.0.7'; Prerelease = $false; Case = 'which is not marked as a prerelease' }
    ) {
        Test-DeletablePrerelease -ReleaseRef $Ref -Prerelease $Prerelease -TaggedVersion $script:tagged | Should -BeFalse
    }

    It 'refuses to decide when no v tags were read, before looking at the release' {
        # The cleanup always runs where v tags exist, so an empty list means the read went wrong.
        # Deleting on it would also remove every tagged build's prerelease and git tag, which the
        # cleanup never deletes, so the first call stops the run before anything is deleted.
        { Test-DeletablePrerelease -ReleaseRef 'v8.1.0' -Prerelease $false -TaggedVersion @() } | Should -Throw '*No v tags*'
    }
}

Describe 'Write-PrereleaseClassification' {
    It 'appends tagged-release=<expected> for <ref> to the output file' -ForEach @(
        @{ Ref = 'dms-pre-8.1.0-beta.0.1'; Expected = 'true' }
        @{ Ref = 'dms-pre-8.1.0-beta.0.1.3'; Expected = 'false' }
    ) {
        $outputPath = Join-Path $script:root "output-$([guid]::NewGuid().ToString('N')).txt"
        Set-Content -LiteralPath $outputPath -Value 'PRE_EXISTING=kept'

        & $script:entryScript -ReleaseRef $Ref -Remote $script:remote -OutputPath $outputPath | Out-Null

        Get-Content -LiteralPath $outputPath | Should -Be @('PRE_EXISTING=kept', "tagged-release=$Expected")
    }

    It 'echoes the decision so it can be read out of the job log' {
        $written = & $script:entryScript -ReleaseRef 'cs-pre-8.1.0-beta.0.1.3' -Remote $script:remote -OutputPath ''

        $written | Should -Contain 'tagged-release=false'
    }
}

Describe 'Prerelease classification workflow wiring' {
    BeforeAll {
        $workflows = Join-Path $PSScriptRoot '../../../.github/workflows'
        $script:prerelease = Get-Content (Join-Path $workflows 'on-prerelease.yml') -Raw
        $script:release = Get-Content (Join-Path $workflows 'on-release.yml') -Raw
        $script:dmsPullRequest = Get-Content (Join-Path $workflows 'on-dms-pullrequest.yml') -Raw
        function script:Get-Job([string]$Workflow, [string]$Name) {
            [regex]::Match($Workflow, "(?ms)^  ${Name}:.*?(?=^  \S|\z)").Value
        }
        $script:classify = script:Get-Job $script:prerelease 'classify-release'
    }

    It 'classifies a DMS or Configuration Service prerelease in a job of its own' {
        $script:classify | Should -Not -BeNullOrEmpty
        $script:classify | Should -Match "if: \`$\{\{ startsWith\(github\.event\.release\.tag_name, 'dms-'\) \|\| startsWith\(github\.event\.release\.tag_name, 'cs-'\) \}\}"
        $script:classify | Should -Match 'tagged-release: \$\{\{ steps\.classify\.outputs\.tagged-release \}\}'
        $script:classify | Should -Match 'uses: actions/checkout@[0-9a-f]{40} # v'
    }

    It 'runs the classifier in pwsh with no expression interpolated into the script body' {
        $step = [regex]::Match($script:classify, '(?ms)      - name: Classify the release.*?(?=\r?\n\r?\n|\z)').Value

        $step | Should -Match '(?m)^        id: classify$'
        $step | Should -Match 'shell: pwsh'
        $step | Should -Match 'eng/ci/Write-PrereleaseClassification\.ps1 -ReleaseRef \$env:REF'
        $step | Should -Not -Match '\$\{\{'
    }

    It 'gates <job> on the classification rather than on the word alpha' -ForEach @(
        @{ Job = 'attach-dms-artifacts-to-release'; Prefix = 'dms-' }
        @{ Job = 'attach-dms-host-assembly-manifest'; Prefix = 'dms-' }
        @{ Job = 'attach-cs-artifacts-to-release'; Prefix = 'cs-' }
    ) {
        $text = script:Get-Job $script:prerelease $Job

        $text | Should -Match '(?m)^      - classify-release$'
        $text | Should -Match "if: \`$\{\{ startsWith\(github\.event\.release\.tag_name, '$Prefix'\) && needs\.classify-release\.outputs\.tagged-release == 'true' \}\}"
        $text | Should -Not -Match 'alpha'
    }

    It 'hands the classification to <job>, which picks the image tags from it' -ForEach @(
        @{ Job = 'docker-publish-dms' }
        @{ Job = 'docker-publish-cs' }
    ) {
        $text = script:Get-Job $script:prerelease $Job

        $text | Should -Match '(?m)^      - classify-release$'
        $text | Should -Match 'TAGGED_RELEASE: \$\{\{ needs\.classify-release\.outputs\.tagged-release \}\}'
    }

    It 'leaves no decision in the prerelease workflow keyed on the word alpha' {
        $script:prerelease | Should -Not -Match "contains\(github\.event\.release\.tag_name, 'alpha'\)"
        $script:prerelease | Should -Not -Match '=~ "alpha"'
    }

    It 'deletes ordinary main-build prereleases at a final release by the same rule' {
        $cleanup = script:Get-Job $script:release 'delete-pre-releases'

        $cleanup | Should -Match "Import-Module \./eng/ci/release-classification\.psm1"
        $cleanup | Should -Match 'Get-ReleaseTagVersion -Remote origin'
        # The decision is the module's, which the cases above test; the workflow only asks.
        $cleanup | Should -Match 'if \(Test-DeletablePrerelease -ReleaseRef \$_\.tag_name -Prerelease \$_\.prerelease -TaggedVersion \$taggedVersion\) \{'
        $cleanup | Should -Not -Match '\$_\.prerelease -and'
        $cleanup | Should -Not -Match "-pre-\*-alpha\*"
    }

    It 'runs these tests in the DMS pull request Pester lane' {
        $script:dmsPullRequest | Should -Match '"eng/ci/tests/PrereleaseClassification\.Tests\.ps1"'
    }
}
