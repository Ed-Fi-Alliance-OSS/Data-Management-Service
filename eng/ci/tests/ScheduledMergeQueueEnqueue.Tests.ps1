# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Runs Invoke-ScheduledMergeQueueEnqueue.ps1 in a child pwsh, as the workflow step does, against a
# fake gh on PATH that serves canned branch rules, pull request lists and paged head-commit checks,
# and records every read of checks and every write.

Describe 'Scheduled merge queue enqueue' -Skip:$IsWindows {
    BeforeAll {
        $script:workflow = Get-Content (Join-Path $PSScriptRoot '../../../.github/workflows/scheduled-merge-queue-enqueue.yml') -Raw
        $script:enqueueScript = Join-Path $PSScriptRoot '../Invoke-ScheduledMergeQueueEnqueue.ps1'

        $script:fakeGh = @'
#!/usr/bin/env bash
set -euo pipefail
if [ "$1" = "api" ] && [[ "$2" == repos/Ed-Fi-Alliance-OSS/Data-Management-Service/rules/branches/main* ]]; then
  cat "$FAKE_GH_DIR/rules.json"
  exit 0
fi
if [ "$1" = "api" ] && [ "$2" = "graphql" ]; then
  query=""; id=""; oid=""; number=""
  for arg in "$@"; do
    case "$arg" in
      query=*) query="${arg#query=}" ;;
      id=*) id="${arg#id=}" ;;
      oid=*) oid="${arg#oid=}" ;;
      number=*) number="${arg#number=}" ;;
    esac
  done
  if [[ "$query" == *enqueuePullRequest* ]]; then
    echo "enqueue $id $oid" >> "$FAKE_GH_LOG"
    if grep -qx "$id" "$FAKE_GH_DIR/fail-ids" 2>/dev/null; then
      echo "enqueue refused" >&2
      exit 1
    fi
    echo '{"data":{"enqueuePullRequest":{"clientMutationId":null}}}'
    exit 0
  fi
  if [[ "$query" == *statusCheckRollup* ]]; then
    echo "checks $number" >> "$FAKE_GH_LOG"
    # Already the array --paginate --slurp returns.
    cat "$FAKE_GH_DIR/checks-$number.json"
    exit 0
  fi
  n=$(( $(cat "$FAKE_GH_DIR/list-count" 2>/dev/null || echo 0) + 1 ))
  echo "$n" > "$FAKE_GH_DIR/list-count"
  echo "list $n" >> "$FAKE_GH_LOG"
  page="$FAKE_GH_DIR/list-$n.json"
  [ -f "$page" ] || page="$FAKE_GH_DIR/list-1.json"
  cat "$page"
  exit 0
fi
if [ "$1" = "pr" ] && [ "$2" = "comment" ]; then
  body=""; prev=""
  for arg in "$@"; do
    if [ "$prev" = "--body" ]; then body="$arg"; fi
    prev="$arg"
  done
  echo "comment $3 $body" >> "$FAKE_GH_LOG"
  exit 0
fi
echo "fake gh: unexpected call: $*" >&2
exit 99
'@

        # Mirrors the shape of GET /repos/{owner}/{repo}/rules/branches/main: two rulesets contribute
        # required checks, and unrelated rule types must be ignored.
        $script:rules = @'
[
  {"type": "pull_request", "parameters": {"required_approving_review_count": 1}},
  {"type": "required_status_checks", "parameters": {"required_status_checks": [{"context": "license/cla"}]}},
  {"type": "merge_queue", "parameters": {"merge_method": "SQUASH"}},
  {"type": "required_status_checks", "parameters": {"required_status_checks": [{"context": "DMS CI Gate"}, {"context": "Config CI Gate"}]}}
]
'@

        $script:passing = @(@('DMS CI Gate', 'SUCCESS'), @('Config CI Gate', 'SUCCESS'), @('license/cla', 'SUCCESS'))

        # A pull request plus the checks on its head commit. Checks are name/state pairs so a name can
        # repeat, and Filler adds that many passing unrelated check runs ahead of them, the way a full
        # DMS run lists its matrix jobs before the gates.
        function Get-FakePullRequest {
            param(
                [int] $Number,
                [string] $Review = 'APPROVED',
                [string] $Mergeable = 'MERGEABLE',
                [bool] $Draft = $false,
                [bool] $Queued = $false,
                [object[]] $Checks = $script:passing,
                [int] $Filler = 0
            )
            $contexts = [System.Collections.Generic.List[object]]::new()
            for ($i = 1; $i -le $Filler; $i++) {
                $contexts.Add([ordered]@{ __typename = 'CheckRun'; name = "Matrix job $i"; status = 'COMPLETED'; conclusion = 'SUCCESS' })
            }
            foreach ($check in $Checks) {
                $name, $state = $check
                if ($name -eq 'license/cla') {
                    $contexts.Add([ordered]@{ __typename = 'StatusContext'; context = $name; state = $state })
                }
                elseif ($state -in 'QUEUED', 'IN_PROGRESS') {
                    $contexts.Add([ordered]@{ __typename = 'CheckRun'; name = $name; status = $state; conclusion = $null })
                }
                else {
                    $contexts.Add([ordered]@{ __typename = 'CheckRun'; name = $name; status = 'COMPLETED'; conclusion = $state })
                }
            }
            [pscustomobject]@{
                Node = [ordered]@{
                    id = "PR_$Number"
                    number = $Number
                    title = "Pull request $Number"
                    url = "https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/$Number"
                    isDraft = $Draft
                    isInMergeQueue = $Queued
                    reviewDecision = $Review
                    mergeable = $Mergeable
                }
                Contexts = $contexts
            }
        }

        function ConvertTo-FakeCheckPage {
            # The head-commit checks as gh api graphql --paginate --slurp returns them: 100 per page.
            param([int] $Number, [System.Collections.Generic.List[object]] $Contexts)
            $pages = [System.Collections.Generic.List[object]]::new()
            $offset = 0
            do {
                $slice = @($Contexts | Select-Object -Skip $offset -First 100)
                $offset += 100
                $pages.Add(@{ data = @{ repository = @{ pullRequest = @{ commits = @{ nodes = @(@{ commit = @{
                                                oid = "sha-$Number"
                                                statusCheckRollup = @{ contexts = @{
                                                        pageInfo = @{ hasNextPage = ($offset -lt $Contexts.Count); endCursor = "c$offset" }
                                                        nodes = $slice
                                                    } }
                                            } }) } } } } })
            } while ($offset -lt $Contexts.Count)
            ConvertTo-Json -InputObject @($pages) -Depth 20
        }

        function Invoke-Enqueue {
            # Each entry of Reads is the list of pull requests one read of the open pull requests returns.
            param(
                [object[][]] $Reads,
                [switch] $DryRun,
                [string[]] $FailIds = @()
            )
            $dir = Join-Path $TestDrive ([guid]::NewGuid().ToString())
            New-Item -ItemType Directory -Path $dir | Out-Null
            $gh = Join-Path $dir 'gh'
            Set-Content -Path $gh -Value $script:fakeGh -NoNewline
            & chmod +x $gh
            Set-Content -Path (Join-Path $dir 'rules.json') -Value $script:rules
            for ($i = 0; $i -lt $Reads.Count; $i++) {
                $page = @{ data = @{ repository = @{ pullRequests = @{
                                pageInfo = @{ hasNextPage = $false; endCursor = $null }
                                nodes = @($Reads[$i] | ForEach-Object Node)
                            } } } }
                Set-Content -Path (Join-Path $dir "list-$($i + 1).json") -Value (ConvertTo-Json -InputObject @($page) -Depth 20)
                foreach ($pr in $Reads[$i]) {
                    Set-Content -Path (Join-Path $dir "checks-$($pr.Node.number).json") -Value (ConvertTo-FakeCheckPage -Number $pr.Node.number -Contexts $pr.Contexts)
                }
            }
            if ($FailIds.Count -gt 0) { Set-Content -Path (Join-Path $dir 'fail-ids') -Value ($FailIds -join "`n") }
            $log = Join-Path $dir 'gh.log'
            $summary = Join-Path $dir 'summary.md'
            New-Item -ItemType File -Path $log, $summary | Out-Null

            $arguments = @(
                '-NoProfile', '-File', $script:enqueueScript
                '-Repository', 'Ed-Fi-Alliance-OSS/Data-Management-Service'
                '-RunUrl', 'https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/42'
                '-SummaryPath', $summary
                '-MergeableRetrySeconds', '0'
            )
            if ($DryRun) { $arguments += '-DryRun' }

            $saved = @{}
            $vars = @{ PATH = "$dir$([IO.Path]::PathSeparator)$env:PATH"; FAKE_GH_DIR = $dir; FAKE_GH_LOG = $log }
            foreach ($name in $vars.Keys) {
                $saved[$name] = [Environment]::GetEnvironmentVariable($name)
                [Environment]::SetEnvironmentVariable($name, $vars[$name])
            }
            try {
                $output = & pwsh @arguments 2>&1
                $exitCode = $LASTEXITCODE
            }
            finally {
                foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
            }
            [pscustomobject]@{
                ExitCode = $exitCode
                Output = ($output -join "`n")
                Log = @(Get-Content $log)
                Summary = (Get-Content $summary -Raw)
            }
        }
    }

    Context 'workflow contract' {
        It 'runs at 8pm, 11pm and 2am CST and on manual dispatch' {
            $crons = @([regex]::Matches($script:workflow, "(?m)^\s+- cron: '([^']+)'") | ForEach-Object { $_.Groups[1].Value })
            $crons | Should -Be @('0 2 * * *', '0 5 * * *', '0 8 * * *')
            $script:workflow | Should -Match '(?m)^  workflow_dispatch:$'
        }

        It 'defaults manual runs to a dry run and never dry-runs a scheduled run' {
            $script:workflow | Should -Match '(?ms)dry-run:\s+description: .+?type: boolean\s+default: true'
            $script:workflow | Should -Match '(?m)^\s+DRY_RUN: \$\{\{ inputs\.dry-run == true \}\}$'
            $script:workflow | Should -Match "(?m)^\s+run: \./eng/ci/Invoke-ScheduledMergeQueueEnqueue\.ps1 -DryRun:\(\`$env:DRY_RUN -eq 'true'\)$"
        }

        It 'authenticates the script with the build agent PAT so enqueues start merge_group CI' {
            $script:workflow | Should -Match '(?m)^\s+GH_TOKEN: \$\{\{ secrets\.EDFI_BUILD_AGENT_PAT \}\}$'
            $script:workflow | Should -Not -Match 'secrets\.GITHUB_TOKEN|github\.token'
            $script:workflow | Should -Match '(?m)^permissions:\r?\n  contents: read$'
            $script:workflow | Should -Match '(?m)^\s+persist-credentials: false$'
        }
    }

    Context 'selection' {
        It 'enqueues and comments on only the approved, mergeable pull requests whose required checks passed' {
            $prs = @(
                (Get-FakePullRequest 1)
                (Get-FakePullRequest 2 -Draft $true)
                (Get-FakePullRequest 3 -Review 'REVIEW_REQUIRED')
                (Get-FakePullRequest 4 -Review 'CHANGES_REQUESTED')
                (Get-FakePullRequest 5 -Mergeable 'CONFLICTING')
                (Get-FakePullRequest 6 -Queued $true)
                (Get-FakePullRequest 7 -Checks @(@('Config CI Gate', 'SUCCESS'), @('license/cla', 'SUCCESS')))
                (Get-FakePullRequest 8 -Checks @(@('DMS CI Gate', 'FAILURE'), @('Config CI Gate', 'SUCCESS'), @('license/cla', 'SUCCESS')))
                (Get-FakePullRequest 9 -Checks @(@('DMS CI Gate', 'SUCCESS'), @('Config CI Gate', 'SUCCESS'), @('license/cla', 'PENDING')))
                (Get-FakePullRequest 10 -Checks @(@('DMS CI Gate', 'SUCCESS'), @('Config CI Gate', 'SKIPPED'), @('license/cla', 'SUCCESS'), @('submit-nuget', 'FAILURE')))
                (Get-FakePullRequest 11 -Checks @(@('DMS CI Gate', 'IN_PROGRESS'), @('Config CI Gate', 'SUCCESS'), @('license/cla', 'SUCCESS')))
            )

            $result = Invoke-Enqueue -Reads @(, $prs)

            $result.ExitCode | Should -Be 0 -Because $result.Output
            @($result.Log | Where-Object { $_ -like 'enqueue *' }) | Should -Be @('enqueue PR_1 sha-1', 'enqueue PR_10 sha-10')
            @($result.Log | Where-Object { $_ -like 'comment *' }) | Should -Be @(
                'comment 1 Added to the merge queue by the scheduled merge queue run: https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/42'
                'comment 10 Added to the merge queue by the scheduled merge queue run: https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/42'
            )
            $result.Summary | Should -Match '(?m)^### Enqueued \(2\)$'
            $result.Summary | Should -Match '(?m)^### Skipped \(9\)$'
            $result.Summary | Should -Match '(?m)/pull/2\) Pull request 2: draft$'
            $result.Summary | Should -Match '(?m)/pull/3\) Pull request 3: review decision is REVIEW_REQUIRED$'
            $result.Summary | Should -Match '(?m)/pull/4\) Pull request 4: review decision is CHANGES_REQUESTED$'
            $result.Summary | Should -Match '(?m)/pull/5\) Pull request 5: mergeable is CONFLICTING$'
            $result.Summary | Should -Match '(?m)/pull/6\) Pull request 6: already in the merge queue$'
            $result.Summary | Should -Match '(?m)/pull/7\) Pull request 7: required check DMS CI Gate is missing$'
            $result.Summary | Should -Match '(?m)/pull/8\) Pull request 8: required check DMS CI Gate is FAILURE$'
            $result.Summary | Should -Match '(?m)/pull/9\) Pull request 9: required check license/cla is PENDING$'
            $result.Summary | Should -Match '(?m)/pull/11\) Pull request 11: required check DMS CI Gate is IN_PROGRESS$'
        }

        It 'reads every page of head-commit checks and only for pull requests that pass the other rules' {
            $prs = @(
                (Get-FakePullRequest 1 -Filler 150)
                (Get-FakePullRequest 2 -Filler 250 -Checks @(@('DMS CI Gate', 'FAILURE'), @('Config CI Gate', 'SUCCESS'), @('license/cla', 'SUCCESS')))
                (Get-FakePullRequest 3 -Review 'REVIEW_REQUIRED')
            )

            $result = Invoke-Enqueue -Reads @(, $prs)

            $result.ExitCode | Should -Be 0 -Because $result.Output
            @($result.Log | Where-Object { $_ -like 'checks *' }) | Should -Be @('checks 1', 'checks 2')
            @($result.Log | Where-Object { $_ -like 'enqueue *' }) | Should -Be @('enqueue PR_1 sha-1')
            $result.Summary | Should -Match '(?m)/pull/2\) Pull request 2: required check DMS CI Gate is FAILURE$'
        }

        It 'requires every check sharing a required name to pass' {
            $prs = @(
                (Get-FakePullRequest 1 -Checks ($script:passing + , @('DMS CI Gate', 'FAILURE')))
                (Get-FakePullRequest 2 -Checks (, @('DMS CI Gate', 'FAILURE') + $script:passing))
            )

            $result = Invoke-Enqueue -Reads @(, $prs)

            $result.ExitCode | Should -Be 0 -Because $result.Output
            @($result.Log | Where-Object { $_ -like 'enqueue *' }).Count | Should -Be 0
            $result.Summary | Should -Match '(?m)/pull/1\) Pull request 1: required check DMS CI Gate is FAILURE$'
            $result.Summary | Should -Match '(?m)/pull/2\) Pull request 2: required check DMS CI Gate is FAILURE$'
        }

        It 're-reads pull requests while GitHub has not computed mergeability yet' {
            $first = @((Get-FakePullRequest 1 -Mergeable 'UNKNOWN'), (Get-FakePullRequest 2 -Review 'REVIEW_REQUIRED'))
            $second = @((Get-FakePullRequest 1), (Get-FakePullRequest 2 -Review 'REVIEW_REQUIRED'))

            $result = Invoke-Enqueue -Reads @($first, $second)

            $result.ExitCode | Should -Be 0 -Because $result.Output
            @($result.Log | Where-Object { $_ -like 'list *' }) | Should -Be @('list 1', 'list 2')
            @($result.Log | Where-Object { $_ -like 'enqueue *' }) | Should -Be @('enqueue PR_1 sha-1')
        }

        It 'gives up after three reads and reports a pull request that stays UNKNOWN' {
            $prs = @((Get-FakePullRequest 1 -Mergeable 'UNKNOWN'))

            $result = Invoke-Enqueue -Reads @(, $prs)

            $result.ExitCode | Should -Be 0 -Because $result.Output
            @($result.Log | Where-Object { $_ -like 'list *' }) | Should -Be @('list 1', 'list 2', 'list 3')
            @($result.Log | Where-Object { $_ -like 'enqueue *' }).Count | Should -Be 0
            $result.Summary | Should -Match '(?m)/pull/1\) Pull request 1: mergeable is UNKNOWN$'
        }
    }

    Context 'outcomes' {
        It 'exits early with a summary when no pull request is eligible' {
            $prs = @((Get-FakePullRequest 3 -Review 'REVIEW_REQUIRED'))

            $result = Invoke-Enqueue -Reads @(, $prs)

            $result.ExitCode | Should -Be 0 -Because $result.Output
            @($result.Log | Where-Object { $_ -like 'checks *' -or $_ -like 'enqueue *' -or $_ -like 'comment *' }).Count | Should -Be 0
            $result.Summary | Should -Match '(?m)^No eligible pull requests\.$'
            $result.Summary | Should -Match '(?m)^### Skipped \(1\)$'
        }

        It 'makes no writes on a dry run and lists what it would enqueue' {
            $prs = @((Get-FakePullRequest 1), (Get-FakePullRequest 2))

            $result = Invoke-Enqueue -Reads @(, $prs) -DryRun

            $result.ExitCode | Should -Be 0 -Because $result.Output
            @($result.Log | Where-Object { $_ -like 'enqueue *' -or $_ -like 'comment *' }).Count | Should -Be 0
            $result.Summary | Should -Match '(?m)^Dry run: nothing was enqueued\.$'
            $result.Summary | Should -Match '(?m)^### Would enqueue \(2\)$'
        }

        It 'keeps going after a refused enqueue, skips its comment, and fails the run' {
            # A refusal is what GitHub returns when the head moved past the commit whose checks were read.
            $prs = @((Get-FakePullRequest 1), (Get-FakePullRequest 2))

            $result = Invoke-Enqueue -Reads @(, $prs) -FailIds @('PR_1')

            $result.ExitCode | Should -Not -Be 0
            @($result.Log | Where-Object { $_ -like 'enqueue *' }) | Should -Be @('enqueue PR_1 sha-1', 'enqueue PR_2 sha-2')
            @($result.Log | Where-Object { $_ -like 'comment *' }) | Should -HaveCount 1
            @($result.Log | Where-Object { $_ -like 'comment *' })[0] | Should -BeLike 'comment 2 *'
            $result.Summary | Should -Match '(?m)^### Enqueued \(1\)$'
            $result.Summary | Should -Match '(?ms)^### Failed to enqueue \(1\)\r?\n\r?\n- \[#1\]\([^)]+/pull/1\) Pull request 1$'
        }
    }
}
