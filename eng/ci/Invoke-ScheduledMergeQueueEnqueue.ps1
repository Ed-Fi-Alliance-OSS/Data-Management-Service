# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Adds approved, ready pull requests against main to the merge queue.

.DESCRIPTION
    A pull request is eligible when it is not a draft, not already queued, approved, mergeable, and
    every required check for main has passed. Each eligible pull request is enqueued and gets a
    comment linking the run, and a summary of enqueued and skipped pull requests is written.
    Called by .github/workflows/scheduled-merge-queue-enqueue.yml; gh must be authenticated with a
    token whose events start other workflows, so the enqueue starts merge_group CI.

.EXAMPLE
    ./eng/ci/Invoke-ScheduledMergeQueueEnqueue.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [string] $Repository = $env:GITHUB_REPOSITORY,

    [string] $RunUrl = $env:RUN_URL,

    # Defaults to the job summary; a local run prints the summary instead.
    [string] $SummaryPath = $env:GITHUB_STEP_SUMMARY,

    [switch] $DryRun,

    [int] $MergeableRetrySeconds = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Gh {
    param([string[]] $Arguments)
    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments[0]) $($Arguments[1]) failed with exit code $LASTEXITCODE"
    }
    $output -join "`n"
}

function Get-RequiredCheck {
    # Every ruleset's required checks for main; the merge queue enforces the same set.
    $rules = Invoke-Gh @('api', "repos/$Repository/rules/branches/main?per_page=100") | ConvertFrom-Json
    @($rules |
        Where-Object type -EQ 'required_status_checks' |
        ForEach-Object { $_.parameters.required_status_checks.context } |
        Sort-Object -Unique)
}

function Get-OpenPullRequest {
    $query = @'
query($owner: String!, $name: String!, $endCursor: String) {
  repository(owner: $owner, name: $name) {
    pullRequests(states: OPEN, baseRefName: "main", first: 50, after: $endCursor) {
      pageInfo { hasNextPage endCursor }
      nodes {
        id number title url isDraft isInMergeQueue reviewDecision mergeable
        commits(last: 1) { nodes { commit { statusCheckRollup { contexts(first: 100) { nodes {
          __typename
          ... on CheckRun { name status conclusion }
          ... on StatusContext { context state }
        } } } } } }
      }
    }
  }
}
'@
    $owner, $name = $Repository -split '/', 2
    $pages = Invoke-Gh @('api', 'graphql', '--paginate', '--slurp', '-f', "query=$query", '-f', "owner=$owner", '-f', "name=$name") |
        ConvertFrom-Json
    @($pages | ForEach-Object { $_.data.repository.pullRequests.nodes })
}

function Get-CheckState {
    # Latest state of each check on the head commit, by name: a check run reports its status until
    # it completes and its conclusion after, a commit status reports its state.
    param($PullRequest)
    $states = @{}
    $rollup = $PullRequest.commits.nodes[0].commit.statusCheckRollup
    if ($null -ne $rollup) {
        foreach ($context in $rollup.contexts.nodes) {
            if ($context.__typename -eq 'CheckRun') {
                $states[$context.name] = if ($null -ne $context.conclusion) { $context.conclusion } else { $context.status }
            }
            else {
                $states[$context.context] = $context.state
            }
        }
    }
    $states
}

function Get-SkipReason {
    # Returns why a pull request is not eligible, or $null when it is. mergeStateStatus is not used:
    # the license/cla ruleset requires up-to-date branches, so nearly every pull request reports
    # BEHIND, which also hides required checks that have not reported yet.
    param($PullRequest, [string[]] $RequiredChecks)
    if ($PullRequest.isInMergeQueue) { return 'already in the merge queue' }
    if ($PullRequest.isDraft) { return 'draft' }
    if ($PullRequest.reviewDecision -ne 'APPROVED') {
        return "review decision is $(if ($PullRequest.reviewDecision) { $PullRequest.reviewDecision } else { 'none' })"
    }
    if ($PullRequest.mergeable -ne 'MERGEABLE') { return "mergeable is $($PullRequest.mergeable)" }

    $states = Get-CheckState -PullRequest $PullRequest
    $unmet = @(foreach ($check in $RequiredChecks) {
            $state = if ($states.ContainsKey($check)) { $states[$check] } else { 'missing' }
            if ($state -notin 'SUCCESS', 'SKIPPED', 'NEUTRAL') { "$check is $state" }
        })
    if ($unmet.Count -gt 0) { return "required check $($unmet -join ', ')" }
    $null
}

function Format-PullRequest {
    param($PullRequest)
    "- [#$($PullRequest.number)]($($PullRequest.url)) $($PullRequest.title)"
}

function Write-Section {
    param([System.Collections.Generic.List[string]] $Lines, [string] $Heading, [string[]] $Items)
    $Lines.Add('')
    $Lines.Add("### $Heading ($($Items.Count))")
    $Lines.Add('')
    foreach ($item in $Items) { $Lines.Add($item) }
}

$requiredChecks = Get-RequiredCheck

# GitHub computes mergeability lazily, so the first read after main moves often returns UNKNOWN;
# reading again a few seconds later returns the real value.
for ($attempt = 1; $attempt -le 3; $attempt++) {
    $classified = @(Get-OpenPullRequest | ForEach-Object {
            [pscustomobject]@{ PullRequest = $_; Skip = Get-SkipReason -PullRequest $_ -RequiredChecks $requiredChecks }
        })
    if ($attempt -eq 3 -or -not ($classified | Where-Object Skip -EQ 'mergeable is UNKNOWN')) { break }
    Start-Sleep -Seconds $MergeableRetrySeconds
}

$eligible = @($classified | Where-Object { $null -eq $_.Skip } | ForEach-Object PullRequest)
$skipped = @($classified | Where-Object { $null -ne $_.Skip } | ForEach-Object { "$(Format-PullRequest -PullRequest $_.PullRequest): $($_.Skip)" })
$enqueued = [System.Collections.Generic.List[string]]::new()
$failed = [System.Collections.Generic.List[string]]::new()

if (-not $DryRun) {
    foreach ($pullRequest in $eligible) {
        try {
            $mutation = 'mutation($id: ID!) { enqueuePullRequest(input: {pullRequestId: $id}) { clientMutationId } }'
            Invoke-Gh @('api', 'graphql', '-f', "query=$mutation", '-f', "id=$($pullRequest.id)") | Out-Null
        }
        catch {
            Write-Output "::error::Failed to enqueue #$($pullRequest.number): $_"
            $failed.Add((Format-PullRequest -PullRequest $pullRequest))
            continue
        }
        $enqueued.Add((Format-PullRequest -PullRequest $pullRequest))
        & gh pr comment $pullRequest.number --repo $Repository --body "Added to the merge queue by the scheduled merge queue run: $RunUrl" | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Output "::warning::Enqueued #$($pullRequest.number) but could not comment on it" }
    }
}

$summary = [System.Collections.Generic.List[string]]::new()
$summary.Add('## Scheduled merge queue run')
$summary.Add('')
if ($eligible.Count -eq 0) {
    $summary.Add('No eligible pull requests.')
}
elseif ($DryRun) {
    $summary.Add('Dry run: nothing was enqueued.')
    Write-Section -Lines $summary -Heading 'Would enqueue' -Items @($eligible | ForEach-Object { Format-PullRequest -PullRequest $_ })
}
else {
    Write-Section -Lines $summary -Heading 'Enqueued' -Items $enqueued
    if ($failed.Count -gt 0) { Write-Section -Lines $summary -Heading 'Failed to enqueue' -Items $failed }
}
if ($skipped.Count -gt 0) { Write-Section -Lines $summary -Heading 'Skipped' -Items $skipped }

if ($SummaryPath) {
    Add-Content -Path $SummaryPath -Value $summary
}
else {
    $summary | Write-Output
}

if ($failed.Count -gt 0) { exit 1 }
