# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

<#
.SYNOPSIS
    Adds approved, ready pull requests against main to the merge queue.

.DESCRIPTION
    A pull request is eligible when it is not a draft, not already queued, approved, mergeable, and
    every required check for main succeeded. Each eligible pull request is enqueued and gets a
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

function Get-HeadCheck {
    # The head commit and the current state of each check on it, by name. A full DMS run reports well
    # over 100 checks with the gates near the end, so every page is read. The rollup also keeps older
    # runs of a check on the same commit (re-runs, close/reopen, draft-to-ready), so the run with the
    # highest databaseId decides, as it does for GitHub. A check run reports its status until it
    # completes and its conclusion after; a commit status reports its state.
    param($PullRequest)
    $query = @'
query($owner: String!, $name: String!, $number: Int!, $endCursor: String) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $number) {
      commits(last: 1) { nodes { commit {
        oid
        statusCheckRollup { contexts(first: 100, after: $endCursor) {
          pageInfo { hasNextPage endCursor }
          nodes {
            __typename
            ... on CheckRun { name databaseId status conclusion }
            ... on StatusContext { context state }
          }
        } }
      } } }
    }
  }
}
'@
    $owner, $name = $Repository -split '/', 2
    $pages = @(Invoke-Gh @('api', 'graphql', '--paginate', '--slurp', '-f', "query=$query", '-f', "owner=$owner", '-f', "name=$name", '-F', "number=$($PullRequest.number)") |
            ConvertFrom-Json)
    $states = @{}
    $newest = @{}
    foreach ($page in $pages) {
        $commit = $page.data.repository.pullRequest.commits.nodes[0].commit
        if ($null -eq $commit.statusCheckRollup) { continue }
        foreach ($context in $commit.statusCheckRollup.contexts.nodes) {
            if ($context.__typename -eq 'CheckRun') {
                if ($newest.ContainsKey($context.name) -and $newest[$context.name] -gt $context.databaseId) { continue }
                $newest[$context.name] = $context.databaseId
                $states[$context.name] = if ($null -ne $context.conclusion) { $context.conclusion } else { $context.status }
            }
            else {
                $states[$context.context] = $context.state
            }
        }
    }
    [pscustomobject]@{ Oid = $pages[0].data.repository.pullRequest.commits.nodes[0].commit.oid; States = $states }
}

function Get-SkipReason {
    # Returns why a pull request is not eligible, or $null when it is, from the fields the list
    # query returns. mergeStateStatus is not used: the license/cla ruleset requires up-to-date
    # branches, so nearly every pull request reports BEHIND.
    param($PullRequest)
    if ($PullRequest.isInMergeQueue) { return 'already in the merge queue' }
    if ($PullRequest.isDraft) { return 'draft' }
    if ($PullRequest.reviewDecision -ne 'APPROVED') {
        return "review decision is $(if ($PullRequest.reviewDecision) { $PullRequest.reviewDecision } else { 'none' })"
    }
    if ($PullRequest.mergeable -ne 'MERGEABLE') { return "mergeable is $($PullRequest.mergeable)" }
    $null
}

function Get-UnmetCheck {
    # Returns why the required checks block the head commit, or $null when they all succeeded.
    # SKIPPED does not count: the gates skip on drafts, and after a draft is marked ready that skip
    # stays the newest result until the new run reaches the gate, which can take hours.
    param($HeadCheck, [string[]] $RequiredChecks)
    $unmet = @(foreach ($check in $RequiredChecks) {
            $state = if ($HeadCheck.States.ContainsKey($check)) { $HeadCheck.States[$check] } else { 'missing' }
            if ($state -ne 'SUCCESS') { "$check is $state" }
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
            [pscustomobject]@{ PullRequest = $_; Skip = Get-SkipReason -PullRequest $_; Oid = $null }
        })
    if ($attempt -eq 3 -or -not ($classified | Where-Object Skip -EQ 'mergeable is UNKNOWN')) { break }
    Start-Sleep -Seconds $MergeableRetrySeconds
}

# Checks are read only for pull requests that pass every other rule, and the enqueue pins the
# commit they were read from, so a push after this read makes GitHub refuse the enqueue.
# A failed read skips only that pull request; the run still fails so the error is seen.
$readFailed = $false
foreach ($entry in $classified | Where-Object { $null -eq $_.Skip }) {
    try {
        $headCheck = Get-HeadCheck -PullRequest $entry.PullRequest
    }
    catch {
        Write-Output "::error::Failed to read the checks of #$($entry.PullRequest.number): $_"
        $entry.Skip = 'check read failed'
        $readFailed = $true
        continue
    }
    $entry.Skip = Get-UnmetCheck -HeadCheck $headCheck -RequiredChecks $requiredChecks
    $entry.Oid = $headCheck.Oid
}

$eligible = @($classified | Where-Object { $null -eq $_.Skip })
$skipped = @($classified | Where-Object { $null -ne $_.Skip } | ForEach-Object { "$(Format-PullRequest -PullRequest $_.PullRequest): $($_.Skip)" })
$enqueued = [System.Collections.Generic.List[string]]::new()
$failed = [System.Collections.Generic.List[string]]::new()

if (-not $DryRun) {
    foreach ($entry in $eligible) {
        $pullRequest = $entry.PullRequest
        try {
            $mutation = 'mutation($id: ID!, $oid: GitObjectID!) { enqueuePullRequest(input: {pullRequestId: $id, expectedHeadOid: $oid}) { clientMutationId } }'
            Invoke-Gh @('api', 'graphql', '-f', "query=$mutation", '-f', "id=$($pullRequest.id)", '-f', "oid=$($entry.Oid)") | Out-Null
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
    Write-Section -Lines $summary -Heading 'Would enqueue' -Items @($eligible | ForEach-Object { Format-PullRequest -PullRequest $_.PullRequest })
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

if ($failed.Count -gt 0 -or $readFailed) { exit 1 }
