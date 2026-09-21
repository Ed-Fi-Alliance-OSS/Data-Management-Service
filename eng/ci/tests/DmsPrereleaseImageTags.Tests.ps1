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
                [string] $ImageName = 'edfialliance/ed-fi-api'
            )

            $outputPath = Join-Path ([System.IO.Path]::GetTempPath()) "dms-tags-$([guid]::NewGuid().ToString('N')).txt"

            try {
                & $script:script -ReleaseRef $ReleaseRef -ImageName $ImageName -OutputPath $outputPath | Out-Null

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

    Context 'a prerelease ref' {
        It 'publishes the moving tag and a version-specific tag for <ref>' -ForEach @(
            @{ Ref = 'dms-pre-8.0.1-alpha.0.7'; Version = '8.0.1-alpha.0.7' }
            @{ Ref = 'dms-pre-8.1.0-alpha.0.152'; Version = '8.1.0-alpha.0.152' }
        ) {
            $result = script:Invoke-TagScript -ReleaseRef $Ref

            $result.DMSTAGS | Should -BeExactly "edfialliance/ed-fi-api:pre,edfialliance/ed-fi-api:$Version"
            $result.VERSION | Should -BeExactly $Version
        }

        It 'keeps the moving tag first, because that is the position it has always been pushed in' {
            $result = script:Invoke-TagScript -ReleaseRef 'dms-pre-8.0.1-alpha.0.7'

            $result.DMSTAGS.Split(',')[0] | Should -BeExactly 'edfialliance/ed-fi-api:pre'
        }

        It 'tags the image name it was given rather than a literal' {
            $result = script:Invoke-TagScript -ReleaseRef 'dms-pre-8.0.1-alpha.0.7' -ImageName 'someone/else'

            $result.DMSTAGS | Should -BeExactly 'someone/else:pre,someone/else:8.0.1-alpha.0.7'
        }

        It 'refuses a prerelease ref that does not carry the dms-pre- prefix' {
            # A ref like this used to produce only a mislabelled build argument. It would now
            # produce a pushed tag, which is published state nobody can take back.
            { script:Invoke-TagScript -ReleaseRef 'cs-pre-8.0.1-alpha.0.7' } |
                Should -Throw "*does not start with 'dms-pre-'*"
        }

        It 'treats Alpha as a release, because the shell it replaces matched case-sensitively' {
            # Verified against the shell being replaced: [[ $REF =~ "alpha" ]] does not match
            # "Alpha", so this ref takes the release branch and gets no moving tag.
            $result = script:Invoke-TagScript -ReleaseRef 'dms-v8.1.0-Alpha'

            $result.DMSTAGS | Should -BeExactly 'edfialliance/ed-fi-api:dms-v8.1.0-Alpha,edfialliance/ed-fi-api:dms-v8.1'
            $result.VERSION | Should -BeExactly '.0-Alpha'
        }
    }

    Context 'a non-alpha ref, whose handling this change does not alter' {
        It 'emits the full ref and the two-field minor form, and no moving tag' {
            $result = script:Invoke-TagScript -ReleaseRef 'dms-v8.1.0'

            $result.DMSTAGS | Should -BeExactly 'edfialliance/ed-fi-api:dms-v8.1.0,edfialliance/ed-fi-api:dms-v8.1'
            $result.DMSTAGS | Should -Not -Match ':pre'
        }

        It 'reproduces the fixed-offset strip the shell performed, wrong result and all' {
            # ${REF:8} over "dms-v8.1.0" is ".0", not a version. Ported rather than corrected: this
            # branch publishes nothing on a workflow that fires on prereleased.
            (script:Invoke-TagScript -ReleaseRef 'dms-v8.1.0').VERSION | Should -BeExactly '.0'
        }

        It 'yields an empty version rather than throwing on a ref shorter than the strip' {
            (script:Invoke-TagScript -ReleaseRef 'dms-v8').VERSION | Should -BeExactly ''
        }

        It 'reproduces the trailing dot awk prints for a ref with one field' {
            (script:Invoke-TagScript -ReleaseRef 'dms-v8').DMSTAGS |
                Should -BeExactly 'edfialliance/ed-fi-api:dms-v8,edfialliance/ed-fi-api:dms-v8.'
        }
    }

    Context 'the output mechanism' {
        It 'appends to an existing output file rather than replacing it' {
            $outputPath = Join-Path ([System.IO.Path]::GetTempPath()) "dms-tags-$([guid]::NewGuid().ToString('N')).txt"
            Set-Content -LiteralPath $outputPath -Value 'PRE_EXISTING=kept'

            try {
                & $script:script -ReleaseRef 'dms-pre-8.0.1-alpha.0.7' -ImageName 'edfialliance/ed-fi-api' -OutputPath $outputPath | Out-Null

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
            $written = & $script:script -ReleaseRef 'dms-pre-8.0.1-alpha.0.7' -ImageName 'edfialliance/ed-fi-api' -OutputPath ''

            $written | Should -Be @(
                'DMSTAGS=edfialliance/ed-fi-api:pre,edfialliance/ed-fi-api:8.0.1-alpha.0.7'
                'VERSION=8.0.1-alpha.0.7'
            )
        }

        It 'rejects an empty image name, which an unset IMAGE_NAME variable would supply' {
            { & $script:script -ReleaseRef 'dms-pre-8.0.1-alpha.0.7' -ImageName '' -OutputPath '' } | Should -Throw
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

    It 'leaves the Configuration Service publishing only the moving tag on a prerelease' {
        # Unchanged by this work. docker-publish-cs keeps its own inline shell and its single tag.
        $script:cmsPublish | Should -Match '\$\{\{ env\.CONFIG_IMAGE_NAME \}\}:pre"'
        $script:cmsPublish | Should -Not -Match 'Get-DmsPrereleaseImageTag'
    }

    It 'keeps a dispatch from reaching the publish chain' {
        # docker-publish-dms depends on publish-package-dms, which depends on pack-dms, which a
        # dispatch skips because it carries no release tag name.
        $script:packDms | Should -Match "if: startsWith\(github\.event\.release\.tag_name, 'dms-'\)"
        $script:dmsPublish | Should -Match '(?m)^      - publish-package-dms$'
    }
}
