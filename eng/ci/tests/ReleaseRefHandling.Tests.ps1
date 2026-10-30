# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# GitHub substitutes ${{ }} expressions into a run script before the shell reads it, and git allows
# shell metacharacters in tag names, so a tag or ref name written into a script is code. The release
# workflows pass the name through env instead and check it is a version before using it. These
# tests run the workflows' own steps with hostile names, and pin which jobs carry the check.

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../release-classification.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot '../workflow-ref-guard.psm1') -Force

    $script:workflows = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../.github/workflows'))
    $script:repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    $script:root = Join-Path ([System.IO.Path]::GetTempPath()) "release-ref-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:root | Out-Null

    function script:Read-Workflow([string]$Name) {
        (Get-Content -LiteralPath (Join-Path $script:workflows $Name) -Raw) -replace "`r`n", "`n"
    }

    function script:Get-Job([string]$Workflow, [string]$Name) {
        [regex]::Match($Workflow, "(?ms)^  ${Name}:\n.*?(?=^  [A-Za-z0-9_-]+:\n|\z)").Value
    }

    function script:Get-JobName([string]$Workflow) {
        $jobs = $Workflow.Substring($Workflow.IndexOf("`njobs:`n"))
        [regex]::Matches($jobs, '(?m)^  ([A-Za-z0-9_-]+):$') | ForEach-Object { $_.Groups[1].Value }
    }

    function script:Get-JobNeed([string]$Job) {
        $inline = [regex]::Match($Job, '(?m)^    needs: *(\S.*)$')
        if ($inline.Success) {
            return @($inline.Groups[1].Value.Trim('[', ']', ' ') -split ', *' | Where-Object { $_ })
        }
        $list = [regex]::Match($Job, '(?ms)^    needs:\n((?:      - \S+\n)+)')
        if ($list.Success) {
            return @([regex]::Matches($list.Groups[1].Value, '- (\S+)') | ForEach-Object { $_.Groups[1].Value })
        }
        return @()
    }

    # Each run script in a workflow, or in one of its jobs, by the same reading the workflow ref
    # guard uses.
    function script:Get-RunScript([string]$Workflow, [string]$Job) {
        Get-WorkflowRunScript -Content $Workflow | Where-Object { -not $Job -or $_.Job -eq $Job } | ForEach-Object Text
    }

    # The run script of the step named $StepName in one job.
    function script:Get-StepScript([string]$Workflow, [string]$Job, [string]$StepName) {
        $script = @(Get-WorkflowRunScript -Content $Workflow | Where-Object { $_.Job -eq $Job -and $_.Step -eq $StepName })
        $script.Count | Should -Be 1 -Because "the job $Job has one '$StepName' step"
        $script[0].Text
    }

    # Replaces any expression GitHub would substitute, as the runner would, so a script that still
    # interpolated the ref would run the payload. A script that reads env has nothing to replace.
    function script:Expand-Expression([string]$Script, [hashtable]$Value) {
        foreach ($name in $Value.Keys) {
            $Script = $Script.Replace("`${{ $name }}", $Value[$name])
        }
        $Script | Should -Not -Match '\$\{\{'
        return $Script
    }

    function script:Invoke-Bash([string]$Script, [hashtable]$Environment, [string]$WorkDir) {
        $outputPath = Join-Path $WorkDir 'github-output'
        Set-Content -LiteralPath $outputPath -Value $null
        $envArgs = @("GITHUB_OUTPUT=$outputPath", "PATH=$env:PATH") + @($Environment.Keys | ForEach-Object { "$_=$($Environment[$_])" })
        Push-Location -LiteralPath $WorkDir
        try {
            $printed = $Script | & env -i @envArgs bash -e -o pipefail 2>&1
            $outputs = @{}
            foreach ($line in Get-Content -LiteralPath $outputPath) {
                $key, $val = $line -split '=', 2
                $outputs[$key] = $val
            }
            [pscustomobject]@{ ExitCode = $LASTEXITCODE; Printed = ($printed -join "`n"); Outputs = $outputs }
        }
        finally {
            Pop-Location
        }
    }

    function script:Invoke-Pwsh([string]$Script, [hashtable]$Environment, [string]$WorkDir) {
        $stepPath = Join-Path $WorkDir "step-$([guid]::NewGuid().ToString('N')).ps1"
        Set-Content -LiteralPath $stepPath -Value "`$ErrorActionPreference = 'Stop'`n$Script"
        $saved = @{}
        foreach ($name in $Environment.Keys) {
            $saved[$name] = [System.Environment]::GetEnvironmentVariable($name)
            [System.Environment]::SetEnvironmentVariable($name, $Environment[$name])
        }
        Push-Location -LiteralPath $WorkDir
        try {
            # pwsh, not this process's own executable: on a host where pwsh is a dotnet global tool
            # the current process is dotnet.
            $printed = & pwsh -NoProfile -NonInteractive -File $stepPath 2>&1 | ForEach-Object { "$_" }
            [pscustomobject]@{ ExitCode = $LASTEXITCODE; Printed = ($printed -join "`n") }
        }
        finally {
            Pop-Location
            foreach ($name in $saved.Keys) {
                [System.Environment]::SetEnvironmentVariable($name, $saved[$name])
            }
        }
    }

    function script:New-WorkDir {
        $dir = Join-Path $script:root ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path (Join-Path $dir 'eng/ci') | Out-Null
        Copy-Item -LiteralPath (Join-Path $script:repoRoot 'eng/ci/release-classification.psm1') -Destination (Join-Path $dir 'eng/ci')
        return $dir
    }

    # Names an attacker could give a tag: each runs a command in bash or pwsh if it reaches the
    # script text. Each writes a file named pwned into the step's working directory.
    $script:hostileRef = @(
        'v1.2.3$(touch pwned)'
        'v1.2.3`touch pwned`'
        'v1.2.3";touch pwned;"'
        'v1.2.3$(New-Item pwned)'
        'dms-pre-1.2.3$(touch pwned)'
        'cs-pre-1.2.3$(New-Item pwned)'
    )
}

AfterAll {
    Remove-Item -LiteralPath $script:root -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'Assert-ReleaseRef' {
    It 'accepts <ref> for prefix <prefix>' -ForEach @(
        @{ Ref = 'v8.0.0'; Prefix = @('v') }
        @{ Ref = 'v8.1.0-beta.0.1'; Prefix = @('v') }
        @{ Ref = 'v8.1.0-beta2'; Prefix = @('v') }
        @{ Ref = 'dms-pre-8.1.0-beta.0.1.3'; Prefix = @('dms-pre-') }
        @{ Ref = 'dms-pre-8.1.0-beta.0.1'; Prefix = @('dms-pre-', 'cs-pre-') }
        @{ Ref = 'cs-pre-8.0.1-alpha.0.204'; Prefix = @('dms-pre-', 'cs-pre-') }
    ) {
        { Assert-ReleaseRef -ReleaseRef $Ref -Prefix $Prefix } | Should -Not -Throw
    }

    It 'refuses <case>' -ForEach @(
        @{ Ref = "v8.0.0`n"; Prefix = @('v'); Case = 'a trailing newline, which $ alone would allow' }
        @{ Ref = 'V8.0.0'; Prefix = @('v'); Case = 'an upper-case prefix' }
        @{ Ref = 'v8.0'; Prefix = @('v'); Case = 'a version without a patch number' }
        @{ Ref = "v$([char]0x0663).0.0"; Prefix = @('v'); Case = 'non-ASCII digits' }
        @{ Ref = ' v8.0.0'; Prefix = @('v'); Case = 'a leading space' }
        @{ Ref = 'v8.0.0+build.1'; Prefix = @('v'); Case = 'build metadata' }
        @{ Ref = 'main'; Prefix = @('v'); Case = 'a branch name' }
        @{ Ref = ''; Prefix = @('v'); Case = 'an empty name' }
        @{ Ref = 'dms-pre-8.0.0'; Prefix = @('v'); Case = 'a prerelease tag where a v tag is expected' }
        @{ Ref = 'v8.0.0'; Prefix = @('dms-pre-', 'cs-pre-'); Case = 'a v tag where a prerelease tag is expected' }
        @{ Ref = 'cs-pre-8.0.0'; Prefix = @('dms-pre-'); Case = 'the other product''s prerelease' }
        @{ Ref = 'v1.2.3$(touch pwned)'; Prefix = @('v'); Case = 'a bash command substitution' }
        @{ Ref = 'v1.2.3-a`id`'; Prefix = @('v'); Case = 'backticks' }
        @{ Ref = 'v1.2.3-a;id'; Prefix = @('v'); Case = 'a command separator' }
    ) {
        { Assert-ReleaseRef -ReleaseRef $Ref -Prefix $Prefix } | Should -Throw '*is not a*version such as*'
    }

    It 'writes no control character from a refused name to the log' {
        $message = try { Assert-ReleaseRef -ReleaseRef "v1.2.3`n::error::forged" -Prefix v } catch { $_.Exception.Message }

        $message | Should -BeLike "*'v1.2.3?::error::forged'*"
        $message | Should -Not -Match '[\r\n]'
    }
}

Describe 'Release workflows read ref and tag names as data' {
    It 'interpolates no ref, tag or ref-derived value into a run script in <file>' -ForEach @(
        @{ File = 'on-prerelease.yml' }
        @{ File = 'on-release.yml' }
        @{ File = 'build-minimal-template.yml' }
        @{ File = 'build-populated-template.yml' }
        @{ File = 'build-sdk-package.yml' }
        @{ File = 'on-dms-merge-or-tag.yml' }
        @{ File = 'on-config-merge-or-tag.yml' }
    ) {
        $offending = foreach ($script in script:Get-RunScript (script:Read-Workflow $File)) {
            [regex]::Matches($script, '\$\{\{(?:(?!\}\}).)*?(?:github\.ref|github\.head_ref|github\.base_ref|github\[|\.tag_name|env\.REF\b|env\.RELEASE_REF|steps\.prepare-(?:version|tags)\.)(?:(?!\}\}).)*\}\}', 'Singleline') |
                ForEach-Object { $_.Value }
        }

        $offending | Should -BeNullOrEmpty
    }
}

Describe 'on-prerelease.yml' {
    BeforeAll {
        $script:prerelease = script:Read-Workflow 'on-prerelease.yml'
    }

    It 'derives <semver> from <tag> in <job>, as before' -ForEach @(
        @{ Job = 'attach-dms-artifacts-to-release'; Tag = 'dms-pre-8.1.0-beta.0.1'; Semver = 'v8.1.0-beta.0.1' }
        @{ Job = 'attach-dms-host-assembly-manifest'; Tag = 'dms-pre-8.0.0'; Semver = 'v8.0.0' }
        @{ Job = 'attach-cs-artifacts-to-release'; Tag = 'cs-pre-8.1.0-beta.0.1'; Semver = 'v8.1.0-beta.0.1' }
        @{ Job = 'attach-security-metadata-tools-artifacts-to-release'; Tag = 'cs-pre-8.0.0'; Semver = 'v8.0.0' }
    ) {
        # Expanding the expression as well lets this run against the old, interpolating text too,
        # which is how the derived values were shown to be unchanged.
        $script = script:Expand-Expression (script:Get-StepScript $script:prerelease $Job 'Prepare release version') @{ 'github.event.release.tag_name' = $Tag }

        $result = script:Invoke-Bash $script @{ TAG_NAME = $Tag } (script:New-WorkDir)

        $result.ExitCode | Should -Be 0
        $result.Outputs['semver'] | Should -BeExactly $Semver
    }

    It 'runs nothing from a hostile tag name in <job>' -ForEach @(
        @{ Job = 'attach-dms-artifacts-to-release' }
        @{ Job = 'attach-dms-host-assembly-manifest' }
        @{ Job = 'attach-cs-artifacts-to-release' }
        @{ Job = 'attach-security-metadata-tools-artifacts-to-release' }
    ) {
        $jobText = script:Get-Job $script:prerelease $Job
        foreach ($tag in $script:hostileRef) {
            $workDir = script:New-WorkDir
            $script = script:Expand-Expression (script:Get-StepScript $script:prerelease $Job 'Prepare release version') @{ 'github.event.release.tag_name' = $tag }

            $null = script:Invoke-Bash $script @{ TAG_NAME = $tag } $workDir

            Join-Path $workDir 'pwned' | Should -Not -Exist -Because "the tag '$tag' is data"
        }
        $jobText | Should -Match "(?m)^          TAG_NAME: \`$\{\{ github\.event\.release\.tag_name \}\}$"
    }

    It 'composes the Configuration Service image tags from REF unchanged' {
        $step = script:Get-StepScript $script:prerelease 'docker-publish-cs' 'Prepare Config Tags'
        $name = @{ 'env.CONFIG_IMAGE_NAME' = 'edfialliance/ed-fi-api-configuration-service' }

        $tagged = script:Invoke-Bash (script:Expand-Expression $step ($name + @{ 'env.REF' = 'cs-pre-8.1.0-beta.0.1' })) @{ REF = 'cs-pre-8.1.0-beta.0.1'; TAGGED_RELEASE = 'true' } (script:New-WorkDir)
        $main = script:Invoke-Bash (script:Expand-Expression $step ($name + @{ 'env.REF' = 'cs-pre-8.1.0-beta.0.1.3' })) @{ REF = 'cs-pre-8.1.0-beta.0.1.3'; TAGGED_RELEASE = 'false' } (script:New-WorkDir)

        $tagged.Outputs['DMSTAGS'] | Should -BeExactly 'edfialliance/ed-fi-api-configuration-service:cs-pre-8.1.0-beta.0.1,edfialliance/ed-fi-api-configuration-service:cs-pre-8.1'
        $tagged.Outputs['VERSION'] | Should -BeExactly '8.1.0-beta.0.1'
        $main.Outputs['DMSTAGS'] | Should -BeExactly 'edfialliance/ed-fi-api-configuration-service:pre'
        $main.Outputs['VERSION'] | Should -BeExactly '8.1.0-beta.0.1.3'
    }

    It 'runs nothing from a hostile tag name while composing the Configuration Service image tags' {
        $step = script:Get-StepScript $script:prerelease 'docker-publish-cs' 'Prepare Config Tags'
        foreach ($tag in $script:hostileRef) {
            $workDir = script:New-WorkDir
            $script = script:Expand-Expression $step @{ 'env.CONFIG_IMAGE_NAME' = 'image'; 'env.REF' = $tag }

            $null = script:Invoke-Bash $script @{ REF = $tag; TAGGED_RELEASE = 'true' } $workDir

            Join-Path $workDir 'pwned' | Should -Not -Exist -Because "the tag '$tag' is data"
        }
    }

    It 'checks the tag in <job> before anything else runs, on a release only: <release>' -ForEach @(
        @{ Job = 'classify-release'; Prefix = 'dms-pre-, cs-pre-'; Release = $false }
        @{ Job = 'pack-plugins'; Prefix = 'dms-pre-'; Release = $true }
        @{ Job = 'pack-custom-validation'; Prefix = 'dms-pre-'; Release = $true }
        @{ Job = 'pack-identity'; Prefix = 'dms-pre-'; Release = $true }
        @{ Job = 'pack-secrets'; Prefix = 'cs-pre-'; Release = $true }
    ) {
        $jobText = script:Get-Job $script:prerelease $Job
        $firstRun = @(script:Get-RunScript $script:prerelease $Job)[0]

        $firstRun | Should -Match "Assert-ReleaseRef -ReleaseRef \`$env:REF -Prefix $Prefix\s*$"
        $step = [regex]::Match($jobText, '(?ms)^      - name: Validate the release tag\n.*?(?=^      - |\z)').Value
        if ($Release) {
            # The dispatch dry run carries no tag and must still run these jobs.
            $step | Should -Match "if: \`$\{\{ github\.event_name == 'release' \}\}"
        }
        else {
            $step | Should -Not -Match 'if:'
        }
    }

    It 'reaches every job through a job that checks the tag' {
        $checked = @{}
        $names = @(script:Get-JobName $script:prerelease)
        $pending = [System.Collections.Generic.List[string]]$names
        # Resolve in passes: a job is covered when it checks the tag or needs a covered job.
        do {
            $progress = $false
            foreach ($name in @($pending)) {
                $job = script:Get-Job $script:prerelease $name
                $needs = @(script:Get-JobNeed $job)
                if ($job -match '- name: Validate the release tag' -or @($needs | Where-Object { $checked[$_] }).Count -gt 0) {
                    $checked[$name] = $true
                    $pending.Remove($name) | Out-Null
                    $progress = $true
                }
            }
        } while ($progress -and $pending.Count -gt 0)

        $pending | Should -BeNullOrEmpty
    }

    It 'checks a tag with the workflow''s own step: <tag> passes: <passes>' -ForEach @(
        @{ Tag = 'dms-pre-8.1.0-beta.0.1.3'; Passes = $true }
        @{ Tag = 'cs-pre-8.0.1-alpha.0.204'; Passes = $true }
        @{ Tag = 'dms-pre-1.2.3$(New-Item pwned)'; Passes = $false }
        @{ Tag = 'dms-pre-1.2.3$(touch pwned)'; Passes = $false }
        @{ Tag = 'v8.1.0'; Passes = $false }
    ) {
        $workDir = script:New-WorkDir
        $script = script:Get-StepScript $script:prerelease 'classify-release' 'Validate the release tag'

        $result = script:Invoke-Pwsh $script @{ REF = $Tag } $workDir

        ($result.ExitCode -eq 0) | Should -Be $Passes -Because $result.Printed
        Join-Path $workDir 'pwned' | Should -Not -Exist
    }
}

Describe 'on-release.yml' {
    BeforeAll {
        $script:release = script:Read-Workflow 'on-release.yml'
    }

    It 'reads the release ref from env' {
        $script:release | Should -Match '(?m)^  RELEASE_REF: \$\{\{ github\.ref_name \}\}$'
    }

    It 'checks the tag first in <job>, which needs no other job' -ForEach @(
        @{ Job = 'delete-pre-releases' }
        @{ Job = 'promote-azure-artifact' }
        @{ Job = 'tag-dms-image' }
        @{ Job = 'tag-cs-image' }
    ) {
        $jobText = script:Get-Job $script:release $Job

        @(script:Get-JobNeed $jobText) | Should -BeNullOrEmpty
        @(script:Get-RunScript $script:release $Job)[0] | Should -Match 'Assert-ReleaseRef -ReleaseRef \$env:RELEASE_REF -Prefix v\s*$'
    }

    It 'derives the <image> image tags from <tag> as before' -ForEach @(
        @{ Job = 'tag-dms-image'; Image = 'DMS'; Tag = 'v8.0.0'; Name = 'edfialliance/data-management-service'; Pre = 'dms-pre-8.0.0'; Major = '8.0.0'; Minor = '8.0' }
        @{ Job = 'tag-dms-image'; Image = 'DMS'; Tag = 'v8.1.0-beta.0.1'; Name = 'edfialliance/data-management-service'; Pre = 'dms-pre-8.1.0-beta.0.1'; Major = '8.1.0-beta.0.1'; Minor = '8.1' }
        @{ Job = 'tag-cs-image'; Image = 'Configuration'; Tag = 'v8.0.0'; Name = 'edfialliance/ed-fi-api-configuration-service'; Pre = 'cs-pre-8.0.0'; Major = '8.0.0'; Minor = '8.0' }
    ) {
        $script = script:Expand-Expression (script:Get-StepScript $script:release $Job 'Prepare Release Tags') @{ 'vars.IMAGE_NAME' = $Name; 'github.ref_name' = $Tag }

        $result = script:Invoke-Bash $script @{ RELEASE_REF = $Tag } (script:New-WorkDir)

        $result.ExitCode | Should -Be 0
        $result.Outputs['PRE_IMAGE'] | Should -BeExactly "${Name}:$Pre"
        $result.Outputs['RELEASE_MAJOR'] | Should -BeExactly "${Name}:$Major"
        $result.Outputs['RELEASE_MINOR'] | Should -BeExactly "${Name}:$Minor"
    }

    It 'runs nothing from a hostile tag name in <job>' -ForEach @(
        @{ Job = 'tag-dms-image' }
        @{ Job = 'tag-cs-image' }
    ) {
        foreach ($tag in $script:hostileRef) {
            $workDir = script:New-WorkDir
            $script = script:Expand-Expression (script:Get-StepScript $script:release $Job 'Prepare Release Tags') @{ 'vars.IMAGE_NAME' = 'image'; 'github.ref_name' = $tag }

            $null = script:Invoke-Bash $script @{ RELEASE_REF = $tag } $workDir

            Join-Path $workDir 'pwned' | Should -Not -Exist -Because "the tag '$tag' is data"
        }
    }

    It 'refuses <case> before anything is deleted, promoted or pushed' -ForEach @(
        @{ Ref = 'v1.2.3$(New-Item pwned)'; RefType = 'tag'; Case = 'a hostile tag'; Message = '*is not a v version*' }
        @{ Ref = 'dms-pre-8.0.0'; RefType = 'tag'; Case = 'a prerelease tag'; Message = '*is not a v version*' }
        @{ Ref = 'v8.0.0'; RefType = 'branch'; Case = 'a dispatch from a branch named like a release'; Message = '*runs for a v tag*branch*' }
    ) {
        $workDir = script:New-WorkDir
        $script = script:Get-StepScript $script:release 'delete-pre-releases' 'Validate the release tag'

        $result = script:Invoke-Pwsh $script @{ RELEASE_REF = $Ref; GITHUB_REF_TYPE = $RefType } $workDir

        $result.ExitCode | Should -Not -Be 0
        $result.Printed | Should -BeLike $Message
        Join-Path $workDir 'pwned' | Should -Not -Exist
    }

    It 'accepts the release tag <ref>' -ForEach @(
        @{ Ref = 'v8.0.0' }
        @{ Ref = 'v8.1.0-beta.0.1' }
    ) {
        $script = script:Get-StepScript $script:release 'tag-dms-image' 'Validate the release tag'

        $result = script:Invoke-Pwsh $script @{ RELEASE_REF = $Ref; GITHUB_REF_TYPE = 'tag' } (script:New-WorkDir)

        $result.ExitCode | Should -Be 0 -Because $result.Printed
    }
}

Describe 'Reusable package workflows and merge-or-tag workflows' {
    It 'checks a tag in <file> <job> before its first script, <when>' -ForEach @(
        @{ File = 'build-minimal-template.yml'; Job = 'build-template'; TagOnly = $true; When = 'on a tag only' }
        @{ File = 'build-minimal-template.yml'; Job = 'promote-azure-package'; TagOnly = $false; When = 'always' }
        @{ File = 'build-populated-template.yml'; Job = 'build-template'; TagOnly = $true; When = 'on a tag only' }
        @{ File = 'build-populated-template.yml'; Job = 'promote-azure-package'; TagOnly = $false; When = 'always' }
        @{ File = 'build-sdk-package.yml'; Job = 'build-sdk'; TagOnly = $true; When = 'on a tag only' }
        @{ File = 'build-sdk-package.yml'; Job = 'promote-azure-package'; TagOnly = $false; When = 'always' }
        @{ File = 'on-dms-merge-or-tag.yml'; Job = 'create-pre-releases'; TagOnly = $true; When = 'on a tag only' }
        @{ File = 'on-config-merge-or-tag.yml'; Job = 'create-pre-releases'; TagOnly = $true; When = 'on a tag only' }
    ) {
        $workflow = script:Read-Workflow $File
        $jobText = script:Get-Job $workflow $Job
        $step = [regex]::Match($jobText, '(?ms)^      - name: Validate the release tag\n.*?(?=^      - |\z)').Value

        @(script:Get-RunScript $workflow $Job)[0] | Should -Match 'Assert-ReleaseRef -ReleaseRef \$env:RELEASE_REF -Prefix v\s*$'
        if ($TagOnly) {
            # Branch, pull request and schedule runs carry no release tag and are unchanged.
            $step | Should -Match "if: \`$\{\{ github\.ref_type == 'tag' \}\}"
        }
        else {
            $step | Should -Not -Match 'if:'
        }
    }

    It 'attaches release assets in <file> only after the build job checked the tag' -ForEach @(
        @{ File = 'build-minimal-template.yml'; Root = 'build-template' }
        @{ File = 'build-populated-template.yml'; Root = 'build-template' }
        @{ File = 'build-sdk-package.yml'; Root = 'build-sdk' }
    ) {
        $workflow = script:Read-Workflow $File
        foreach ($need in script:Get-JobNeed (script:Get-Job $workflow 'attach-sbom-and-provenance-to-release')) {
            @(script:Get-JobNeed (script:Get-Job $workflow $need)) | Should -Contain $Root
        }
    }

    It 'promotes <file> with the version derived from the tag as before' -ForEach @(
        @{ File = 'build-minimal-template.yml'; Step = 'Promote Minimal Template Azure Package' }
        @{ File = 'build-populated-template.yml'; Step = 'Promote Populated Template Azure Package' }
    ) {
        $script = script:Get-StepScript (script:Read-Workflow $File) 'promote-azure-package' $Step

        $script | Should -Match '(?m)^\$releaseVersion = \$env:RELEASE_REF -replace "v", ""$'
        $script | Should -Match '(?m)^  ReleaseRef  = \$env:RELEASE_REF$'
    }

    It 'refuses a hostile tag with the workflow''s own step in <file>' -ForEach @(
        @{ File = 'build-sdk-package.yml'; Job = 'build-sdk' }
        @{ File = 'on-dms-merge-or-tag.yml'; Job = 'create-pre-releases' }
    ) {
        $script = script:Get-StepScript (script:Read-Workflow $File) $Job 'Validate the release tag'
        foreach ($tag in $script:hostileRef) {
            $workDir = script:New-WorkDir

            $result = script:Invoke-Pwsh $script @{ RELEASE_REF = $tag } $workDir

            $result.ExitCode | Should -Not -Be 0
            Join-Path $workDir 'pwned' | Should -Not -Exist
        }
        (script:Invoke-Pwsh $script @{ RELEASE_REF = 'v8.0.0' } (script:New-WorkDir)).ExitCode | Should -Be 0
    }
}
