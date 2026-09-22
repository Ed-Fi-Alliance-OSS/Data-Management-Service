# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# DMS-1502: the lane that runs the stock-image plugin proof.
#
# Two things here cannot be proved by running the harness, because they are decisions the lane makes
# before and after it: that a proof which did not run cannot leave anything a reader would take for
# a result, and that this lane is isolated from the per-PR checks. Both are one edit away from being
# untrue, and neither would fail any other test in the repository.
#
# The readiness script itself is exercised for real in StockImagePinReadiness.Tests.ps1; what is
# asserted here is the wiring around it.

# Read at discovery, not in a BeforeAll. Pester evaluates -Skip: while it is discovering tests, when
# nothing a BeforeAll set exists yet, so a condition read from there is always $null and the test is
# always skipped. A silently skipped assertion is worse than no assertion.
$script:pinPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../docker-compose/tests/plugin-deployment/stock-image-pin.json'))
$script:pinIsPending = ((Get-Content -LiteralPath $script:pinPath -Raw | ConvertFrom-Json).status -ceq 'pending')

Describe 'Stock image plugin proof lane' {
    BeforeAll {
        $script:repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
        $script:workflowPath = Join-Path $script:repositoryRoot '.github/workflows/scheduled-stock-plugin-proof.yml'
        $script:workflow = Get-Content -LiteralPath $script:workflowPath -Raw

        # The two jobs, split so an assertion about one cannot be satisfied by the other.
        $script:readinessJob = [regex]::Match($script:workflow, '(?ms)^  publication-readiness:.*?(?=^  \S)').Value
        $script:proofJob = [regex]::Match($script:workflow, '(?ms)^  stock-image-plugin-proof:.*\z').Value
    }

    Context 'when it runs, and what it can be asked to run against' {
        It 'runs on a schedule and on request' {
            $script:workflow | Should -Match '(?m)^  schedule:'
            $script:workflow | Should -Match '(?m)^  workflow_dispatch:'
            $script:workflow | Should -Match "(?m)^  - cron: '"
        }

        It 'lets a dispatched run name an alternate pin, and defaults to the committed one' {
            $committed = 'eng/docker-compose/tests/plugin-deployment/stock-image-pin.json'

            $script:workflow | Should -Match '(?m)^      pin-file:'
            $script:workflow | Should -Match ([regex]::Escape("inputs.pin-file || '$committed'"))
        }

        It 'passes the event through, because a pending pin means different things on each' {
            # The readiness script fails a dispatched run on a pending pin and reports not-ready on
            # a scheduled one. Hardcoding either value here would collapse that distinction.
            $script:readinessJob | Should -Match "-EventName '\`$\{\{ github\.event_name \}\}'"
        }

        It 'reads no more than it needs to' {
            $script:workflow | Should -Match '(?m)^permissions: read-all'
        }

        It 'runs on Linux, in PowerShell' {
            @([regex]::Matches($script:workflow, '(?m)^    runs-on: ubuntu-latest')).Count | Should -Be 2
            @([regex]::Matches($script:workflow, '(?m)^        shell: pwsh')).Count | Should -Be 2
        }
    }

    Context 'the gate between readiness and the proof' {
        It 'decides readiness with the same script the harness validates the pin with' {
            $script:readinessJob | Should -Match 'eng/ci/Test-StockImagePinReadiness\.ps1'
        }

        It 'exposes readiness for another job to gate on' {
            $script:readinessJob | Should -Match '(?m)^    outputs:'
            $script:readinessJob | Should -Match 'ready: \$\{\{ steps\.readiness\.outputs\.ready \}\}'
        }

        It 'runs the proof only when the pin is ready' {
            $script:proofJob | Should -Match '(?m)^    needs: publication-readiness'
            $script:proofJob | Should -Match "(?m)^    if: needs\.publication-readiness\.outputs\.ready == 'true'"
        }

        It 'says in the summary that a skip is not evidence' {
            # The failure mode this guards: a green run with no proof job and no explanation, read
            # months later as a proof that passed.
            $script:readinessJob | Should -Match "if: steps\.readiness\.outputs\.ready != 'true'"
            $script:readinessJob | Should -Match 'GITHUB_STEP_SUMMARY'
            $script:readinessJob | Should -Match 'is not evidence'
        }
    }

    Context 'what a run may leave behind' {
        It 'uploads proof evidence only from the job that produced it' {
            # A skipped proof has nothing to upload, and an artifact from a job that never ran the
            # harness would be a success claim nobody made.
            $script:readinessJob | Should -Not -Match 'upload-artifact'
            $script:proofJob | Should -Match 'actions/upload-artifact@'
            $script:proofJob | Should -Match 'stock-image-plugin-proof\.json'
        }

        It 'uploads that evidence only when the proof failed' {
            $script:proofJob | Should -Match "if: always\(\) && steps\.proof\.outcome == 'failure'"
        }

        It 'tears down what it owns whatever happened' {
            $teardown = [regex]::Match($script:proofJob, '(?ms)^      - name: Tear down.*\z').Value

            $teardown | Should -Match '(?m)^        if: always\(\)'
            $teardown | Should -Match 'bootstrap-published-dms\.ps1 -d -v'
        }

        It 'removes only its own compose project, never the host at large' {
            # The teardown is a backstop for a harness that died before its own cleanup. A prune or
            # a name filter here would reach resources this lane did not create.
            $script:proofJob | Should -Not -Match 'docker system prune'
            $script:proofJob | Should -Not -Match 'docker volume prune'
            $script:proofJob | Should -Not -Match 'docker rm -f'
            $script:proofJob | Should -Not -Match 'network rm'
        }
    }

    Context 'what the proof job needs to run at all' {
        It 'sets up buildx, which is how the registry is asked what the tag resolves to' {
            $script:proofJob | Should -Match 'docker/setup-buildx-action@'
        }

        It 'restores from the Ed-Fi feed with the credential the other lanes use' {
            $script:proofJob | Should -Match 'ARTIFACTS_FEED_URL: \$\{\{ vars\.AZURE_ARTIFACTS_FEED_URL \}\}'
            $script:proofJob | Should -Match 'secrets\.AZURE_ARTIFACTS_PERSONAL_ACCESS_TOKEN'
        }

        It 'runs the harness that already exists rather than a second copy of it' {
            $script:proofJob | Should -Match 'eng/docker-compose/tests/plugin-deployment/Invoke-StockImagePluginProof\.ps1'
        }

        It 'pins every action to a commit, at a version this repository already uses' {
            # A tag can move. A version this repository does not use elsewhere is a second thing to
            # keep up to date, so each pin here is checked against a lane that already has one.
            $reference = @(Get-ChildItem -LiteralPath (Join-Path $script:repositoryRoot '.github/workflows') -Filter '*.yml' |
                    Where-Object { $_.Name -ne 'scheduled-stock-plugin-proof.yml' } |
                    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"

            $used = @([regex]::Matches($script:workflow, '(?m)uses: (\S+)@([0-9a-f]{40})') |
                    ForEach-Object { $_.Groups[0].Value -replace '^uses: ', '' })

            $used | Should -Not -BeNullOrEmpty

            foreach ($pin in $used) {
                $reference | Should -Match ([regex]::Escape($pin)) -Because "$pin is used elsewhere in this repository"
            }
        }
    }

    Context 'isolation from the per-PR checks' {
        It 'is not run by the pull request workflow' {
            # This lane pulls an image and brings four stacks up. Per-PR validation of the same work
            # stays at the Pester level, against a shim.
            $pullRequest = Get-Content -LiteralPath (Join-Path $script:repositoryRoot '.github/workflows/on-dms-pullrequest.yml') -Raw

            $pullRequest | Should -Not -Match 'Invoke-StockImagePluginProof'
            $pullRequest | Should -Not -Match 'scheduled-stock-plugin-proof'
        }

        It 'is the only workflow that runs the proof harness' {
            $running = @(Get-ChildItem -LiteralPath (Join-Path $script:repositoryRoot '.github/workflows') -Filter '*.yml' |
                    Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match 'Invoke-StockImagePluginProof' } |
                    ForEach-Object { $_.Name })

            $running | Should -Be @('scheduled-stock-plugin-proof.yml')
        }

        It 'keeps the harness suites in the pull request lane, where they belong' {
            $pullRequest = Get-Content -LiteralPath (Join-Path $script:repositoryRoot '.github/workflows/on-dms-pullrequest.yml') -Raw

            foreach ($suite in @(
                    'eng/docker-compose/tests/StockImageProofEntryScript.Tests.ps1'
                    'eng/ci/tests/StockImagePinReadiness.Tests.ps1'
                    'eng/ci/tests/StockImageProofWorkflow.Tests.ps1'
                )) {
                $pullRequest | Should -Match ([regex]::Escape($suite))
            }
        }
    }

    Context 'the two answers a pending pin gets, run for real' {
        BeforeAll {
            $script:readinessScript = Join-Path $script:repositoryRoot 'eng/ci/Test-StockImagePinReadiness.ps1'
            # Resolved again here: what discovery put in $script:pinPath does not survive into the
            # run phase, which has its own script scope. Only the -Skip: condition needs the
            # discovery-time value.
            $script:committedPin = Join-Path $script:repositoryRoot 'eng/docker-compose/tests/plugin-deployment/stock-image-pin.json'
        }

        It 'reports not-ready on a schedule while the pin is pending, without failing' -Skip:(-not $script:pinIsPending) {
            $output = & $script:readinessScript -PinFile $script:committedPin -EventName 'schedule' -OutputPath ''

            $output | Should -Contain 'ready=false'
        }

        It 'fails a dispatched run while the pin is pending' -Skip:(-not $script:pinIsPending) {
            # The distinction the lane depends on: somebody asked for the proof, so a skip would
            # read as a pass.
            { & $script:readinessScript -PinFile $script:committedPin -EventName 'workflow_dispatch' -OutputPath '' } |
                Should -Throw
        }

        It 'fails a dispatched run against a malformed pin' {
            $malformed = Join-Path ([IO.Path]::GetTempPath()) "dms1502-bad-$([guid]::NewGuid().ToString('N')).json"
            Set-Content -LiteralPath $malformed -Value '{ not json'

            try {
                { & $script:readinessScript -PinFile $malformed -EventName 'workflow_dispatch' -OutputPath '' } |
                    Should -Throw
            }
            finally {
                Remove-Item -LiteralPath $malformed -Force -ErrorAction SilentlyContinue
            }
        }

        It 'fails a dispatched run against an incomplete published pin' {
            $incomplete = Join-Path ([IO.Path]::GetTempPath()) "dms1502-partial-$([guid]::NewGuid().ToString('N')).json"
            Set-Content -LiteralPath $incomplete -Encoding utf8 -Value (
                @{ status = 'published'; edFiApi = @{ repository = 'edfialliance/ed-fi-api' } } | ConvertTo-Json -Depth 4)

            try {
                { & $script:readinessScript -PinFile $incomplete -EventName 'workflow_dispatch' -OutputPath '' } |
                    Should -Throw
            }
            finally {
                Remove-Item -LiteralPath $incomplete -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
