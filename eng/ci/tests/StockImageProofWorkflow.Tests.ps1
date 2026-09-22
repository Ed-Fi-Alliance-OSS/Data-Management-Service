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
        $script:composeRoot = Join-Path $script:repositoryRoot 'eng/docker-compose'
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
            $script:readinessJob | Should -Match '-EventName \$env:GITHUB_EVENT_NAME'
        }

        It 'reads no more than it needs to' {
            $script:workflow | Should -Match '(?m)^permissions: read-all'
        }

        It 'never puts the pin value into a script it then runs' {
            # The pin path can come from a dispatch input. Interpolated into a run block, a value
            # carrying a quote closes the literal and the rest executes as PowerShell, in a job
            # holding the artifacts-feed credential. It is read from the environment instead.
            $run = @([regex]::Matches($script:workflow, '(?ms)^        run: .*?(?=^      - |^  \S|\z)') |
                    ForEach-Object { $_.Value })

            $run | Should -Not -BeNullOrEmpty

            foreach ($block in $run) {
                $block | Should -Not -Match 'inputs\.pin-file'
                $block | Should -Not -Match 'env\.PIN_FILE'
                $block | Should -Not -Match 'steps\.readiness\.outputs'
            }

            $script:workflow | Should -Match '-PinFile \$env:PIN_FILE'
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

        It 'tears down whatever happened, through the receipt-gated entry point' {
            $teardown = [regex]::Match($script:proofJob, '(?ms)^      - name: Tear down.*\z').Value

            $teardown | Should -Match '(?m)^        if: always\(\)'
            $teardown | Should -Match 'Invoke-StockImageProofFallbackCleanup\.ps1'
        }

        It 'brings nothing down on its own authority' {
            # A step that simply brought the compose project down would remove whatever is there,
            # including the stack the proof refused to touch at its preflight. Every removal has to
            # go through the entry point that requires a receipt this run wrote.
            $script:proofJob | Should -Not -Match 'bootstrap-published-dms\.ps1 -d'
            $script:proofJob | Should -Not -Match 'docker compose'
            $script:proofJob | Should -Not -Match 'docker system prune'
            $script:proofJob | Should -Not -Match 'docker volume prune'
            $script:proofJob | Should -Not -Match 'docker rm'
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

    Context 'the receipt that says an outside caller may tear something down' {
        BeforeAll {
            $script:fallbackScript = Join-Path $script:composeRoot 'tests/plugin-deployment/Invoke-StockImageProofFallbackCleanup.ps1'

            # A recording docker, so "ran no Docker command" is an observation rather than a claim.
            # It also fails closed: anything it is asked to do exits non-zero, so a fallback that
            # reached the daemon at all could not then report success.
            function script:Invoke-Fallback {
                param([string] $ReceiptPath)

                $scratch = Join-Path ([IO.Path]::GetTempPath()) "dms1502-fallback-$([guid]::NewGuid().ToString('N'))"
                New-Item -ItemType Directory -Path $scratch -Force | Out-Null

                try {
                    $log = Join-Path $scratch 'commands.log'
                    New-Item -ItemType File -Path $log -Force | Out-Null

                    $wrapper = Join-Path $scratch 'run.ps1'
                    Set-Content -LiteralPath $wrapper -Encoding utf8 -Value @'
param([string] $Script, [string] $ReceiptPath, [string] $Log, [string] $ResultFile)

$global:FallbackLog = $Log

function global:docker {
    Add-Content -LiteralPath $global:FallbackLog -Value "docker $($args -join ' ')"
    $global:LASTEXITCODE = 1
}

function global:pwsh {
    Add-Content -LiteralPath $global:FallbackLog -Value "pwsh $($args -join ' ')"
    $global:LASTEXITCODE = 0
}

$failure = ''
$action = ''
$environmentFile = ''

try {
    $outcome = & $Script -ReceiptPath $ReceiptPath -PassThru
    if ($null -ne $outcome) {
        $action = [string]$outcome.Action
        $environmentFile = [string]$outcome.EnvironmentFile
    }
}
catch {
    $failure = $_.Exception.Message
}

[pscustomobject]@{ Action = $action; EnvironmentFile = $environmentFile; Failure = $failure } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ResultFile -Encoding utf8
'@

                    $resultFile = Join-Path $scratch 'result.json'
                    $pwshPath = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
                    & $pwshPath -NoProfile -File $wrapper -Script $script:fallbackScript `
                        -ReceiptPath $ReceiptPath -Log $log -ResultFile $resultFile 2>&1 | Out-Null

                    $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json

                    return [pscustomobject]@{
                        Action          = $result.Action
                        EnvironmentFile = $result.EnvironmentFile
                        Failure         = $result.Failure
                        Command         = @(Get-Content -LiteralPath $log -ErrorAction SilentlyContinue |
                                Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
                    }
                }
                finally {
                    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
                }
            }

            function script:New-Receipt {
                param([hashtable] $Override = @{})

                $workspace = Join-Path ([IO.Path]::GetTempPath()) "dms1502-rw-$([guid]::NewGuid().ToString('N'))"
                New-Item -ItemType Directory -Path $workspace -Force | Out-Null
                $environmentFile = Join-Path $workspace 'recipe1.env'
                Set-Content -LiteralPath $environmentFile -Value 'DMS_HTTP_PORTS=8080' -Encoding utf8

                $receipt = [ordered]@{
                    composeProject  = 'dms-published'
                    composeRoot     = $script:composeRoot
                    environmentFile = $environmentFile
                    workspaceRoot   = $workspace
                    evidenceRoot    = (Join-Path $workspace '..' | Split-Path -Parent)
                    writtenUtc      = [DateTimeOffset]::UtcNow.ToString('o')
                }

                foreach ($key in $Override.Keys) { $receipt[$key] = $Override[$key] }

                $path = Join-Path $workspace 'receipt.json'
                Set-Content -LiteralPath $path -Encoding utf8 -Value ($receipt | ConvertTo-Json -Depth 4)

                return [pscustomobject]@{ Path = $path; Workspace = $workspace; EnvironmentFile = $environmentFile }
            }
        }

        It 'runs no Docker command at all when there is no receipt' {
            # The case that matters: the proof refused at its preflight because somebody else's
            # stack was up, so it claimed nothing and wrote nothing. A teardown here would remove
            # that stack.
            $absent = Join-Path ([IO.Path]::GetTempPath()) "dms1502-none-$([guid]::NewGuid().ToString('N')).json"

            $run = Invoke-Fallback -ReceiptPath $absent

            $run.Action | Should -BeExactly 'skipped'
            $run.Failure | Should -BeNullOrEmpty
            $run.Command | Should -HaveCount 0
        }

        It 'tears down with the environment file the receipt recorded' {
            $receipt = New-Receipt

            try {
                $run = Invoke-Fallback -ReceiptPath $receipt.Path

                $run.Failure | Should -BeNullOrEmpty
                $run.Action | Should -BeExactly 'cleaned'
                $run.EnvironmentFile | Should -BeExactly $receipt.EnvironmentFile

                # The recorded file, not a default: a different one composes a different set of
                # services and brings a different stack down.
                $teardown = @($run.Command | Where-Object { $_ -match 'bootstrap-published-dms\.ps1' })
                $teardown | Should -HaveCount 1
                $teardown[0] | Should -Match ([regex]::Escape($receipt.EnvironmentFile))
                $teardown[0] | Should -Match '-d -v'

                # And the permission is spent.
                Test-Path -LiteralPath $receipt.Path | Should -BeFalse
            }
            finally {
                Remove-Item -LiteralPath $receipt.Workspace -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'refuses a receipt that does not describe this proof: <Case>' -ForEach @(
            @{ Case = 'another compose project'; Override = @{ composeProject = 'somebody-elses' } }
            @{ Case = 'another compose directory'; Override = @{ composeRoot = 'C:/elsewhere/eng/docker-compose' } }
            @{ Case = 'an environment file outside its workspace'; Override = @{ environmentFile = 'C:/elsewhere/recipe1.env' } }
        ) {
            $receipt = New-Receipt -Override $Override

            try {
                $run = Invoke-Fallback -ReceiptPath $receipt.Path

                $run.Failure | Should -Match 'not permission to remove anything'
                $run.Command | Should -HaveCount 0
                Test-Path -LiteralPath $receipt.Path | Should -BeTrue
            }
            finally {
                Remove-Item -LiteralPath $receipt.Workspace -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'refuses rather than guessing when the recorded environment file is gone' {
            $receipt = New-Receipt
            Remove-Item -LiteralPath $receipt.EnvironmentFile -Force

            try {
                $run = Invoke-Fallback -ReceiptPath $receipt.Path

                $run.Failure | Should -Match 'no longer exists'
                $run.Command | Should -HaveCount 0
            }
            finally {
                Remove-Item -LiteralPath $receipt.Workspace -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'refuses a receipt that is not readable rather than acting on it' {
            $receipt = New-Receipt
            Set-Content -LiteralPath $receipt.Path -Value '{ not json'

            try {
                $run = Invoke-Fallback -ReceiptPath $receipt.Path

                $run.Failure | Should -Match 'not valid JSON'
                $run.Command | Should -HaveCount 0
            }
            finally {
                Remove-Item -LiteralPath $receipt.Workspace -Recurse -Force -ErrorAction SilentlyContinue
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
