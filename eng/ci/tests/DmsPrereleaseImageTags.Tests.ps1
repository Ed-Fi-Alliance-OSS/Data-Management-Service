# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Describe 'DMS prerelease image tags' {
    BeforeAll {
        $script:script = Join-Path $PSScriptRoot '../Get-DmsPrereleaseImageTag.ps1'

        # Runs the real script and returns what the workflow would consume, read back out of the
        # output file rather than out of a return value, because the file is the mechanism the step
        # depends on. The temporary path is fresh per call so nothing accumulates across cases.
        function script:Invoke-TagScript {
            param(
                [Parameter(Mandatory)] [string] $ReleaseRef,
                [Parameter(Mandatory)] [string] $TaggedRelease,
                [string] $ImageName = 'edfialliance/ed-fi-api'
            )

            $outputPath = Join-Path ([System.IO.Path]::GetTempPath()) "dms-tags-$([guid]::NewGuid().ToString('N')).txt"

            try {
                & $script:script -ReleaseRef $ReleaseRef -TaggedRelease $TaggedRelease -ImageName $ImageName -OutputPath $outputPath | Out-Null

                $written = [ordered]@{}
                foreach ($line in Get-Content -LiteralPath $outputPath) {
                    $split = $line.Split('=', 2)
                    $written[$split[0]] = $split[1]
                }

                return [pscustomobject]$written
            }
            finally {
                if (Test-Path -LiteralPath $outputPath) {
                    Remove-Item -LiteralPath $outputPath -Force
                }
            }
        }
    }

    Context 'an ordinary main build, which no v tag names' {
        It 'publishes the moving tag and a version-specific tag for <ref>' -ForEach @(
            @{ Ref = 'dms-pre-8.0.1-alpha.0.7'; Version = '8.0.1-alpha.0.7' }
            @{ Ref = 'dms-pre-8.1.0-alpha.0.152'; Version = '8.1.0-alpha.0.152' }
            # A merge after a beta tag carries no "alpha" and is still an ordinary build.
            @{ Ref = 'dms-pre-8.1.0-beta.0.1.3'; Version = '8.1.0-beta.0.1.3' }
        ) {
            $result = script:Invoke-TagScript -ReleaseRef $Ref -TaggedRelease 'false'

            $result.DMSTAGS | Should -BeExactly "edfialliance/ed-fi-api:pre,edfialliance/ed-fi-api:$Version"
            $result.VERSION | Should -BeExactly $Version
        }

        It 'keeps the moving tag first, because that is the position it has always been pushed in' {
            $result = script:Invoke-TagScript -ReleaseRef 'dms-pre-8.0.1-alpha.0.7' -TaggedRelease 'false'

            $result.DMSTAGS.Split(',')[0] | Should -BeExactly 'edfialliance/ed-fi-api:pre'
        }

        It 'tags the image name it was given rather than a literal' {
            $result = script:Invoke-TagScript -ReleaseRef 'dms-pre-8.0.1-alpha.0.7' -TaggedRelease 'false' -ImageName 'someone/else'

            $result.DMSTAGS | Should -BeExactly 'someone/else:pre,someone/else:8.0.1-alpha.0.7'
        }

        It 'refuses an ordinary build ref without the dms-pre- prefix' {
            # The classify-release job already fails for such a ref, so the workflow never publishes
            # one, and no release in the repository has ever carried one. Refusing here keeps a hand
            # run from publishing what the workflow would not.
            { & $script:script -ReleaseRef 'dms-8.0.1-alpha.0.7' -TaggedRelease 'false' -ImageName 'edfialliance/ed-fi-api' -OutputPath '' } |
                Should -Throw "*does not start with 'dms-pre-'*"
        }
    }

    Context 'a tagged release build' {
        It 'publishes the version and minor tags the v8.1.0-beta.0.1 build published, and no moving tag' {
            $result = script:Invoke-TagScript -ReleaseRef 'dms-pre-8.1.0-beta.0.1' -TaggedRelease 'true'

            $result.DMSTAGS | Should -BeExactly 'edfialliance/ed-fi-api:dms-pre-8.1.0-beta.0.1,edfialliance/ed-fi-api:dms-pre-8.1'
            $result.VERSION | Should -BeExactly '8.1.0-beta.0.1'
        }

        It 'follows the classification rather than the name, so a v-tagged alpha is a release' {
            $result = script:Invoke-TagScript -ReleaseRef 'dms-pre-8.2.0-alpha.1' -TaggedRelease 'true'

            $result.DMSTAGS | Should -BeExactly 'edfialliance/ed-fi-api:dms-pre-8.2.0-alpha.1,edfialliance/ed-fi-api:dms-pre-8.2'
        }

        It 'emits the full ref and the two-field minor form, and no moving tag' {
            $result = script:Invoke-TagScript -ReleaseRef 'dms-v8.1.0' -TaggedRelease 'true'

            $result.DMSTAGS | Should -BeExactly 'edfialliance/ed-fi-api:dms-v8.1.0,edfialliance/ed-fi-api:dms-v8.1'
            $result.DMSTAGS | Should -Not -Match ':pre'
        }

        It 'reproduces the fixed-offset strip the shell performed, wrong result and all' {
            # ${REF:8} over "dms-v8.1.0" is ".0", not a version. Ported rather than corrected: the
            # merge-or-tag workflows only create dms-pre- refs, where the strip removes exactly the
            # prefix, so a dms-v ref never reaches this branch from a real release.
            (script:Invoke-TagScript -ReleaseRef 'dms-v8.1.0' -TaggedRelease 'true').VERSION | Should -BeExactly '.0'
        }

        It 'yields an empty version rather than throwing on a ref shorter than the strip' {
            (script:Invoke-TagScript -ReleaseRef 'dms-v8' -TaggedRelease 'true').VERSION | Should -BeExactly ''
        }

        It 'reproduces the trailing dot awk prints for a ref with one field' {
            (script:Invoke-TagScript -ReleaseRef 'dms-v8' -TaggedRelease 'true').DMSTAGS |
                Should -BeExactly 'edfialliance/ed-fi-api:dms-v8,edfialliance/ed-fi-api:dms-v8.'
        }
    }

    Context 'the output mechanism' {
        It 'appends to an existing output file rather than replacing it' {
            $outputPath = Join-Path ([System.IO.Path]::GetTempPath()) "dms-tags-$([guid]::NewGuid().ToString('N')).txt"
            Set-Content -LiteralPath $outputPath -Value 'PRE_EXISTING=kept'

            try {
                & $script:script -ReleaseRef 'dms-pre-8.0.1-alpha.0.7' -TaggedRelease 'false' -ImageName 'edfialliance/ed-fi-api' -OutputPath $outputPath | Out-Null

                Get-Content -LiteralPath $outputPath | Should -Be @(
                    'PRE_EXISTING=kept'
                    'DMSTAGS=edfialliance/ed-fi-api:pre,edfialliance/ed-fi-api:8.0.1-alpha.0.7'
                    'VERSION=8.0.1-alpha.0.7'
                )
            }
            finally {
                Remove-Item -LiteralPath $outputPath -Force
            }
        }

        It 'echoes both lines so an unexpected tag can be read out of the job log' {
            $written = & $script:script -ReleaseRef 'dms-pre-8.0.1-alpha.0.7' -TaggedRelease 'false' -ImageName 'edfialliance/ed-fi-api' -OutputPath ''

            $written | Should -Be @(
                'DMSTAGS=edfialliance/ed-fi-api:pre,edfialliance/ed-fi-api:8.0.1-alpha.0.7'
                'VERSION=8.0.1-alpha.0.7'
            )
        }

        It 'rejects an empty image name, which an unset IMAGE_NAME variable would supply' {
            { & $script:script -ReleaseRef 'dms-pre-8.0.1-alpha.0.7' -TaggedRelease 'false' -ImageName '' -OutputPath '' } | Should -Throw
        }

        It 'refuses an ordinary build ref that names no version after the prefix' {
            { & $script:script -ReleaseRef 'dms-pre-' -TaggedRelease 'false' -ImageName 'edfialliance/ed-fi-api' -OutputPath '' } |
                Should -Throw '*names no version*'
        }

        It 'rejects a classification of <value>, which a skipped or failed classify job would supply' -ForEach @(
            @{ Value = '' }
            @{ Value = 'yes' }
        ) {
            { & $script:script -ReleaseRef 'dms-pre-8.0.1-alpha.0.7' -TaggedRelease $Value -ImageName 'edfialliance/ed-fi-api' -OutputPath '' } | Should -Throw
        }
    }
}

Describe 'DMS prerelease image tag workflow wiring' {
    BeforeAll {
        $workflow = Get-Content (Join-Path $PSScriptRoot '../../../.github/workflows/on-prerelease.yml') -Raw
        $script:dmsPublish = [regex]::Match($workflow, '(?ms)^  docker-publish-dms:.*?(?=^  \S)').Value
        $script:cmsPublish = [regex]::Match($workflow, '(?ms)^  docker-publish-cs:.*\z').Value
        $script:packDms = [regex]::Match($workflow, '(?ms)^  pack-dms:.*?(?=^  \S)').Value
    }

    It 'checks the repository out before the step that runs a repository script' {
        $checkout = $script:dmsPublish.IndexOf('uses: actions/checkout@')
        $prepare = $script:dmsPublish.IndexOf('name: Prepare DMS Tags')

        $checkout | Should -BeGreaterThan -1
        $prepare | Should -BeGreaterThan $checkout
    }

    It 'pins the checkout to a full commit SHA' {
        $script:dmsPublish | Should -Match 'uses: actions/checkout@[0-9a-f]{40} # v'
    }

    It 'runs the tag helper in pwsh with no expression interpolated into the script body' {
        $step = [regex]::Match($script:dmsPublish, '(?ms)      - name: Prepare DMS Tags.*?(?=\r?\n\r?\n)').Value

        $step | Should -Match 'shell: pwsh'
        $step | Should -Match 'eng/ci/Get-DmsPrereleaseImageTag\.ps1'
        $step | Should -Match '\$env:REF'
        $step | Should -Match '\$env:IMAGE_NAME'
        $step | Should -Match '-TaggedRelease \$env:TAGGED_RELEASE'
        $step | Should -Not -Match '\$\{\{'
    }

    It 'keeps the step id and the output names the build step already consumes' {
        $script:dmsPublish | Should -Match '(?m)^        id: prepare-tags$'
        $script:dmsPublish | Should -Match 'tags: \$\{\{ steps\.prepare-tags\.outputs\.DMSTAGS \}\}'
        $script:dmsPublish | Should -Match 'build-args: VERSION=\$\{\{ steps\.prepare-tags\.outputs\.VERSION \}\}'
    }

    It 'still builds the image from the remote context rather than the new working tree' {
        $script:dmsPublish | Should -Match 'context: "\{\{defaultContext\}\}:src/dms"'
    }

    It 'leaves the Configuration Service publishing only the moving tag for an ordinary build' {
        # docker-publish-cs keeps its own inline shell and its single tag; only its decision moved
        # from the word alpha to the classification.
        $script:cmsPublish | Should -Match '\$\{\{ env\.CONFIG_IMAGE_NAME \}\}:pre"'
        $script:cmsPublish | Should -Match 'if \[ "\$TAGGED_RELEASE" = "false" \]'
        $script:cmsPublish | Should -Not -Match 'Get-DmsPrereleaseImageTag'
    }

    It 'keeps a dispatch from reaching the publish chain' {
        # docker-publish-dms depends on publish-package-dms, which depends on pack-dms, which a
        # dispatch skips because it carries no release tag name.
        $script:packDms | Should -Match "if: startsWith\(github\.event\.release\.tag_name, 'dms-'\)"
        $script:dmsPublish | Should -Match '(?m)^      - publish-package-dms$'
    }
}

Describe 'Prerelease images build for every platform' {
    BeforeAll {
        # No -Force, for the reason package-helpers.psm1 gives.
        Import-Module (Join-Path $PSScriptRoot 'WorkflowText.psm1') -ErrorAction Stop
        $workflow = Get-Content (Join-Path $PSScriptRoot '../../../.github/workflows/on-prerelease.yml') -Raw

        # The published image must run natively on both amd64 and arm64 hosts, and an arm64 image
        # cannot be built on the amd64 runner without QEMU emulation registered before Buildx.
        $script:jobSteps = @{
            'docker-publish-dms' = @(Get-WorkflowJobStep (Get-WorkflowJobText -Workflow $workflow -Id 'docker-publish-dms'))
            'docker-publish-cs'  = @(Get-WorkflowJobStep (Get-WorkflowJobText -Workflow $workflow -Id 'docker-publish-cs'))
        }

        function script:Get-BuildStep([string] $JobId, [string] $Name) {
            return @($script:jobSteps[$JobId] | Where-Object { $_ -match "^      - name: $([regex]::Escape($Name))\r?\n" })
        }
    }

    Context 'the <Name> build step' -ForEach @(
        @{
            Name     = 'DMS'
            JobId    = 'docker-publish-dms'
            StepName = 'Build and push DMS image'
        }
        @{
            Name     = 'Configuration Service'
            JobId    = 'docker-publish-cs'
            StepName = 'Build and push Config image'
        }
    ) {
        It 'declares exactly one platforms input, for amd64 and arm64' {
            $build = @(script:Get-BuildStep $JobId $StepName)
            $build.Count | Should -Be 1 -Because "the $Name job has one step named '$StepName'"

            $platforms = @($build[0] -split '\r?\n' | Where-Object { $_ -match '^\s+platforms:' })
            $platforms.Count | Should -Be 1 -Because "the $Name build step must declare platforms: once"
            $platforms[0].Trim() | Should -BeExactly 'platforms: linux/amd64,linux/arm64'
        }
    }

    Context 'the <Name> job QEMU setup' -ForEach @(
        @{ Name = 'DMS'; JobId = 'docker-publish-dms' }
        @{ Name = 'Configuration Service'; JobId = 'docker-publish-cs' }
    ) {
        It 'registers QEMU, pinned to a full commit SHA, before Buildx is set up' {
            $steps = $script:jobSteps[$JobId]
            $qemu = @(0..($steps.Count - 1) | Where-Object { $steps[$_] -match 'uses: docker/setup-qemu-action@[0-9a-f]{40} # v\d+\.\d+\.\d+' })
            $buildx = @(0..($steps.Count - 1) | Where-Object { $steps[$_] -match 'uses: docker/setup-buildx-action@' })

            $qemu.Count | Should -Be 1 -Because "the $Name job is missing its docker/setup-qemu-action step pinned to a SHA with a version comment"
            $buildx.Count | Should -Be 1
            $qemu[0] | Should -BeLessThan $buildx[0] -Because 'QEMU must be registered before Buildx creates its builder'
        }

        It 'registers only arm64, from a binfmt image pinned to a digest' {
            $steps = $script:jobSteps[$JobId]
            $qemu = @($steps | Where-Object { $_ -match 'uses: docker/setup-qemu-action@' })
            $qemu.Count | Should -Be 1

            # The action defaults to tonistiigi/binfmt:latest and to every platform it knows.
            $lines = @($qemu[0] -split '\r?\n')
            $lines | Should -Contain '          image: docker.io/tonistiigi/binfmt:qemu-v10.2.3-68@sha256:400a4873b838d1b89194d982c45e5fb3cda4593fbfd7e08a02e76b03b21166f0'
            $lines | Should -Contain '          platforms: arm64'
        }
    }
}

Describe 'Release image promotion keeps every platform' {
    BeforeAll {
        # No -Force, for the reason package-helpers.psm1 gives.
        Import-Module (Join-Path $PSScriptRoot 'WorkflowText.psm1') -ErrorAction Stop
        $workflow = Get-Content (Join-Path $PSScriptRoot '../../../.github/workflows/on-release.yml') -Raw

        # docker pull fetches only the runner's own platform, so promoting by pull, tag and push would
        # republish a multi-platform prerelease image as a single platform. The registry copies the
        # whole image index when the release tags are created from the prerelease tag directly.
        $script:jobSteps = @{
            'tag-dms-image' = @(Get-WorkflowJobStep (Get-WorkflowJobText -Workflow $workflow -Id 'tag-dms-image'))
            'tag-cs-image'  = @(Get-WorkflowJobStep (Get-WorkflowJobText -Workflow $workflow -Id 'tag-cs-image'))
        }

        $script:promoteCommand = './eng/ci/Invoke-ReleaseImagePromotion.ps1 -Source $env:PRE_IMAGE -Tag $env:RELEASE_MAJOR, $env:RELEASE_MINOR'

        function script:Get-PromoteStep([string] $JobId) {
            return @($script:jobSteps[$JobId] | Where-Object { (Get-WorkflowStepRun $_).Trim() -ceq $script:promoteCommand })
        }

        function script:Get-PromoteStepIndex([string] $JobId) {
            $steps = $script:jobSteps[$JobId]
            return @(0..($steps.Count - 1) | Where-Object { (Get-WorkflowStepRun $steps[$_]).Trim() -ceq $script:promoteCommand })
        }

        function script:Get-StepIndexByName([string] $JobId, [string] $Name) {
            $steps = $script:jobSteps[$JobId]
            return @(0..($steps.Count - 1) | Where-Object { $steps[$_] -cmatch "^      - name: $([regex]::Escape($Name))\r?\n" })
        }

        # Reads the raw step text rather than Get-WorkflowStepRun, which returns only a block scalar
        # and so would miss a one-line run: command. Comments are dropped first.
        function script:Test-StepRunsDockerCopy([string] $Step) {
            $code = @($Step -split '\r?\n' | ForEach-Object { $_ -replace '(^|\s)#.*$', '' }) -join "`n"
            return $code -match '\bdocker\s+(tag|push|pull)\b'
        }
    }

    Context 'the <Name> job' -ForEach @(
        @{ Name = 'DMS'; JobId = 'tag-dms-image' }
        @{ Name = 'Configuration Service'; JobId = 'tag-cs-image' }
    ) {
        It 'promotes with exactly one registry-side copy that reads its values from the environment' {
            $promote = @(script:Get-PromoteStep $JobId)
            $promote.Count | Should -Be 1 -Because "the $Name job must have exactly one step that runs: $script:promoteCommand"

            (Get-WorkflowStepRun $promote[0]) | Should -Not -Match '\$\{\{' -Because 'the promote script must read the tags from its environment, not from expressions expanded into the script'
            $promote[0] | Should -Match '(?m)^        shell: pwsh\s*$' -Because 'the promote command reads $env: variables, which only PowerShell expands'
        }

        It 'maps the image names from the prepared tags into the promote step environment' {
            $promote = @(script:Get-PromoteStep $JobId)
            $promote.Count | Should -Be 1 -Because "the $Name job must have exactly one step that runs: $script:promoteCommand"

            $lines = @($promote[0] -split '\r?\n')
            foreach ($name in 'PRE_IMAGE', 'RELEASE_MAJOR', 'RELEASE_MINOR') {
                $lines | Should -Contain ('          {0}: ${{{{ steps.prepare-tags.outputs.{0} }}}}' -f $name) -Because "the promote step must map $name from the prepare-tags step"
            }
        }

        It 'does not pull, tag or push the image on the runner' {
            $steps = $script:jobSteps[$JobId]

            @($steps | Where-Object { $_ -cmatch '(?m)^        uses: \./\.github/actions/pull-ci-images\s*$' }).Count |
                Should -Be 0 -Because "the $Name job must not pull the prerelease image through the local action"
            @($steps | Where-Object { script:Test-StepRunsDockerCopy $_ }).Count |
                Should -Be 0 -Because "the $Name job must not run docker pull, tag or push, which keeps one platform"
        }

        It 'logs in to Docker Hub before it promotes' {
            $login = @(script:Get-StepIndexByName $JobId 'Login to Docker hub')
            $login.Count | Should -Be 1 -Because "the $Name job must have exactly one step named 'Login to Docker hub'"

            $promote = @(script:Get-PromoteStepIndex $JobId)
            $promote.Count | Should -Be 1 -Because "the $Name job must have exactly one step that runs: $script:promoteCommand"
            $login[0] | Should -BeLessThan $promote[0] -Because 'the registry rejects the copy unless the job is logged in first'
        }
    }

    It 'flags a docker push in a <Form> as a runner command: <Expected>' -ForEach @(
        @{ Form = 'one-line run: command'; Expected = $true; Step = "      - name: Push`n        run: docker push example/image:1.0`n" }
        @{ Form = 'block run: command'; Expected = $true; Step = "      - name: Push`n        run: |`n          docker push example/image:1.0`n" }
        @{ Form = 'comment only'; Expected = $false; Step = "      # docker push is not used here`n      - name: Promote`n        run: ./promote.ps1 # not docker push`n" }
    ) {
        script:Test-StepRunsDockerCopy $Step | Should -Be $Expected
    }
}

Describe 'Invoke-ReleaseImagePromotion retry behavior' {
    BeforeAll {
        $script:promotionScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Invoke-ReleaseImagePromotion.ps1'))

        # The script runs in a child pwsh with docker and Start-Sleep replaced by functions that
        # record each call, so no registry and no real wait is involved.
        $script:harnessPath = Join-Path $TestDrive 'invoke-promotion-harness.ps1'
        Set-Content -LiteralPath $script:harnessPath -Encoding utf8 -Value @'
$global:dockerCallIndex = 0

function docker {
    $global:dockerCallIndex++
    Add-Content -LiteralPath $env:PROMOTE_LOG -Value "docker $($args -join ' ')"

    # One exit code per call; the last one repeats.
    $codes = @($env:PROMOTE_EXIT_CODES -split ',' | ForEach-Object { [int] $_ })
    $global:LASTEXITCODE = $codes[[Math]::Min($global:dockerCallIndex, $codes.Count) - 1]
}

function Start-Sleep {
    param([int] $Seconds)
    Add-Content -LiteralPath $env:PROMOTE_LOG -Value "sleep $Seconds"
}

# The message is written here, unwrapped: pwsh's own error view wraps it at the console width.
try {
    & $env:PROMOTE_SCRIPT -Source 'example/api:dms-pre-8.2.0' -Tag 'example/api:8.2.0', 'example/api:8.2'
}
catch {
    Write-Output "PROMOTION ERROR: $($_.Exception.Message)"
    exit 1
}
'@

        function script:Invoke-Promotion([string] $ExitCodes) {
            $log = Join-Path $TestDrive "promotion-$([guid]::NewGuid().ToString('N')).log"
            $env:PROMOTE_LOG = $log
            $env:PROMOTE_EXIT_CODES = $ExitCodes
            $env:PROMOTE_SCRIPT = $script:promotionScript
            try {
                $output = & pwsh -NoProfile -NonInteractive -File $script:harnessPath 2>&1
                $exitCode = $LASTEXITCODE
            }
            finally {
                Remove-Item Env:\PROMOTE_LOG, Env:\PROMOTE_EXIT_CODES, Env:\PROMOTE_SCRIPT -ErrorAction SilentlyContinue
            }

            return [pscustomobject]@{
                ExitCode = $exitCode
                Output   = ($output | Out-String)
                Calls    = @(Get-Content -LiteralPath $log)
            }
        }

        $script:promoteCall = 'docker buildx imagetools create --tag example/api:8.2.0 --tag example/api:8.2 example/api:dms-pre-8.2.0'
    }

    It 'promotes both tags in one registry-side copy when the first attempt succeeds' {
        $result = script:Invoke-Promotion '0'

        $result.ExitCode | Should -Be 0
        $result.Calls | Should -Be @($script:promoteCall)
    }

    It 'retries a transient failure with a doubling wait and then succeeds' {
        $result = script:Invoke-Promotion '1,1,0'

        $result.ExitCode | Should -Be 0
        $result.Calls | Should -Be @($script:promoteCall, 'sleep 10', $script:promoteCall, 'sleep 20', $script:promoteCall)
    }

    It 'fails the step after the last attempt fails' {
        $result = script:Invoke-Promotion '1'

        $result.ExitCode | Should -Be 1
        $result.Calls | Should -Be @($script:promoteCall, 'sleep 10', $script:promoteCall, 'sleep 20', $script:promoteCall, 'sleep 40', $script:promoteCall)
        $result.Output | Should -BeLike '*PROMOTION ERROR: Failed to promote example/api:dms-pre-8.2.0 to example/api:8.2.0, example/api:8.2 after 4 attempt(s)*'
    }
}
