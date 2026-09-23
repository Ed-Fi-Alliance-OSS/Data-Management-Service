# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Runs Invoke-ScheduledMergeQueueEnqueue.ps1 in a child pwsh, as the workflow step does, against a
# fake gh on PATH that serves canned branch rules and pull request pages and records every write.

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
  query=""; id=""
  for arg in "$@"; do
    case "$arg" in
      query=*) query="${arg#query=}" ;;
      id=*) id="${arg#id=}" ;;
    esac
  done
  if [[ "$query" == *enqueuePullRequest* ]]; then
    echo "enqueue $id" >> "$FAKE_GH_LOG"
    if grep -qx "$id" "$FAKE_GH_DIR/fail-ids" 2>/dev/null; then
      echo "enqueue refused" >&2
      exit 1
    fi
    echo '{"data":{"enqueuePullRequest":{"clientMutationId":null}}}'
    exit 0
  fi
  n=$(( $(cat "$FAKE_GH_DIR/fetch-count" 2>/dev/null || echo 0) + 1 ))
  echo "$n" > "$FAKE_GH_DIR/fetch-count"
  echo "fetch $n" >> "$FAKE_GH_LOG"
  page="$FAKE_GH_DIR/prs-$n.json"
  [ -f "$page" ] || page="$FAKE_GH_DIR/prs-1.json"
  # --paginate --slurp returns every page wrapped in one array.
  printf '['; cat "$page"; printf ']'
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

        function Get-FakePullRequest {
            param(
                [int] $Number,
                [string] $Review = 'APPROVED',
                [string] $Mergeable = 'MERGEABLE',
                [bool] $Draft = $false,
                [bool] $Queued = $false,
                [hashtable] $Checks = @{ 'DMS CI Gate' = 'SUCCESS'; 'Config CI Gate' = 'SUCCESS'; 'license/cla' = 'SUCCESS' }
            )
            $contexts = @(foreach ($key in $Checks.Keys) {
                    if ($key -eq 'license/cla') {
                        [ordered]@{ __typename = 'StatusContext'; context = $key; state = $Checks[$key] }
                    }
                    elseif ($Checks[$key] -in 'QUEUED', 'IN_PROGRESS') {
                        [ordered]@{ __typename = 'CheckRun'; name = $key; status = $Checks[$key]; conclusion = $null }
                    }
                    else {
                        [ordered]@{ __typename = 'CheckRun'; name = $key; status = 'COMPLETED'; conclusion = $Checks[$key] }
                    }
                })
            [ordered]@{
                id = "PR_$Number"
                number = $Number
                title = "Pull request $Number"
                url = "https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/$Number"
                isDraft = $Draft
                isInMergeQueue = $Queued
                reviewDecision = $Review
                mergeable = $Mergeable
                commits = @{ nodes = @(@{ commit = @{ statusCheckRollup = @{ contexts = @{ nodes = $contexts } } } }) }
            }
        }

        function Get-FakePullRequestPage {
            param([object[]] $Prs)
            @{ data = @{ repository = @{ pullRequests = @{
                            pageInfo = @{ hasNextPage = $false; endCursor = $null }
                            nodes = @($Prs)
                        } } } } | ConvertTo-Json -Depth 20
        }

        function Invoke-Enqueue {
            param(
                [object[]] $Pages,
                [switch] $DryRun,
                [string[]] $FailIds = @()
            )
            $dir = Join-Path $TestDrive ([guid]::NewGuid().ToString())
            New-Item -ItemType Directory -Path $dir | Out-Null
            $gh = Join-Path $dir 'gh'
            Set-Content -Path $gh -Value $script:fakeGh -NoNewline
            & chmod +x $gh
            Set-Content -Path (Join-Path $dir 'rules.json') -Value $script:rules
            for ($i = 0; $i -lt $Pages.Count; $i++) {
                Set-Content -Path (Join-Path $dir "prs-$($i + 1).json") -Value $Pages[$i]
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
            $page = Get-FakePullRequestPage @(
                (Get-FakePullRequest 1)
                (Get-FakePullRequest 2 -Draft $true)
                (Get-FakePullRequest 3 -Review 'REVIEW_REQUIRED')
                (Get-FakePullRequest 4 -Review 'CHANGES_REQUESTED')
                (Get-FakePullRequest 5 -Mergeable 'CONFLICTING')
                (Get-FakePullRequest 6 -Queued $true)
                (Get-FakePullRequest 7 -Checks @{ 'Config CI Gate' = 'SUCCESS'; 'license/cla' = 'SUCCESS' })
                (Get-FakePullRequest 8 -Checks @{ 'DMS CI Gate' = 'FAILURE'; 'Config CI Gate' = 'SUCCESS'; 'license/cla' = 'SUCCESS' })
                (Get-FakePullRequest 9 -Checks @{ 'DMS CI Gate' = 'SUCCESS'; 'Config CI Gate' = 'SUCCESS'; 'license/cla' = 'PENDING' })
                (Get-FakePullRequest 10 -Checks @{ 'DMS CI Gate' = 'SUCCESS'; 'Config CI Gate' = 'SKIPPED'; 'license/cla' = 'SUCCESS'; 'submit-nuget' = 'FAILURE' })
                (Get-FakePullRequest 11 -Checks @{ 'DMS CI Gate' = 'IN_PROGRESS'; 'Config CI Gate' = 'SUCCESS'; 'license/cla' = 'SUCCESS' })
            )

            $result = Invoke-Enqueue -Pages @($page)

            $result.ExitCode | Should -Be 0 -Because $result.Output
            @($result.Log | Where-Object { $_ -like 'enqueue *' }) | Should -Be @('enqueue PR_1', 'enqueue PR_10')
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

        It 're-reads pull requests while GitHub has not computed mergeability yet' {
            $first = Get-FakePullRequestPage @((Get-FakePullRequest 1 -Mergeable 'UNKNOWN'), (Get-FakePullRequest 2 -Review 'REVIEW_REQUIRED'))
            $second = Get-FakePullRequestPage @((Get-FakePullRequest 1), (Get-FakePullRequest 2 -Review 'REVIEW_REQUIRED'))

            $result = Invoke-Enqueue -Pages @($first, $second)

            $result.ExitCode | Should -Be 0 -Because $result.Output
            @($result.Log | Where-Object { $_ -like 'fetch *' }) | Should -Be @('fetch 1', 'fetch 2')
            @($result.Log | Where-Object { $_ -like 'enqueue *' }) | Should -Be @('enqueue PR_1')
        }

        It 'gives up after three reads and reports a pull request that stays UNKNOWN' {
            $page = Get-FakePullRequestPage @((Get-FakePullRequest 1 -Mergeable 'UNKNOWN'))

            $result = Invoke-Enqueue -Pages @($page)

            $result.ExitCode | Should -Be 0 -Because $result.Output
            @($result.Log | Where-Object { $_ -like 'fetch *' }) | Should -Be @('fetch 1', 'fetch 2', 'fetch 3')
            @($result.Log | Where-Object { $_ -like 'enqueue *' }).Count | Should -Be 0
            $result.Summary | Should -Match '(?m)/pull/1\) Pull request 1: mergeable is UNKNOWN$'
        }
    }

    Context 'outcomes' {
        It 'exits early with a summary when no pull request is eligible' {
            $page = Get-FakePullRequestPage @((Get-FakePullRequest 3 -Review 'REVIEW_REQUIRED'))

            $result = Invoke-Enqueue -Pages @($page)

            $result.ExitCode | Should -Be 0 -Because $result.Output
            @($result.Log | Where-Object { $_ -like 'enqueue *' -or $_ -like 'comment *' }).Count | Should -Be 0
            $result.Summary | Should -Match '(?m)^No eligible pull requests\.$'
            $result.Summary | Should -Match '(?m)^### Skipped \(1\)$'
        }

        It 'makes no writes on a dry run and lists what it would enqueue' {
            $page = Get-FakePullRequestPage @((Get-FakePullRequest 1), (Get-FakePullRequest 2))

            $result = Invoke-Enqueue -Pages @($page) -DryRun

            $result.ExitCode | Should -Be 0 -Because $result.Output
            @($result.Log | Where-Object { $_ -like 'enqueue *' -or $_ -like 'comment *' }).Count | Should -Be 0
            $result.Summary | Should -Match '(?m)^Dry run: nothing was enqueued\.$'
            $result.Summary | Should -Match '(?m)^### Would enqueue \(2\)$'
        }

        It 'keeps going after a refused enqueue, skips its comment, and fails the run' {
            $page = Get-FakePullRequestPage @((Get-FakePullRequest 1), (Get-FakePullRequest 2))

            $result = Invoke-Enqueue -Pages @($page) -FailIds @('PR_1')

            $result.ExitCode | Should -Not -Be 0
            @($result.Log | Where-Object { $_ -like 'enqueue *' }) | Should -Be @('enqueue PR_1', 'enqueue PR_2')
            @($result.Log | Where-Object { $_ -like 'comment *' }) | Should -HaveCount 1
            @($result.Log | Where-Object { $_ -like 'comment *' })[0] | Should -BeLike 'comment 2 *'
            $result.Summary | Should -Match '(?m)^### Enqueued \(1\)$'
            $result.Summary | Should -Match '(?ms)^### Failed to enqueue \(1\)\r?\n\r?\n- \[#1\]\([^)]+/pull/1\) Pull request 1$'
        }
    }
}
