# DMS-1683 PR Build Failure and Merge Queue Kickout Findings

Ticket: [DMS-1683](https://edfi.atlassian.net/browse/DMS-1683), "Spike: Investigate PR build failures
and merge queue kickouts". Related: DMS-1696, DMS-1697, DMS-1568, DMS-1578, DMS-1607, DMS-1684,
DMS-1426, DMS-1463, DEVOPS-208, DEVOPS-211, DEVOPS-213.

This report classifies every failed-check merge queue removal and every failed pull request job
that the collection below returned for a 30-day window. It records the evidence and then a
disposition for each failure pattern: an existing ticket, a follow-up ticket created from this spike
([DMS-1704](https://edfi.atlassian.net/browse/DMS-1704), [DMS-1705](https://edfi.atlassian.net/browse/DMS-1705), [DMS-1706](https://edfi.atlassian.net/browse/DMS-1706)), or an
explanation of why no change is needed. Pattern IDs (`F1` to `F12`, `M1` to `M3`, `U1` to `U4`) are
used throughout. Implementing any fix is outside this spike.

## Summary

- **Window and cutoff.** Workflow runs created from 2026-10-20T00:00Z were listed between about
  18:03Z and 18:05Z on 2026-11-19; the newest run returned was created at 2026-11-19T17:30:54Z.
  That gave 2,637 `pull_request` runs and 267 `merge_group` runs. Merge queue timelines for the 100
  pull requests updated since 2026-10-20 were read at 2026-11-19T18:59:30Z.
- **Queue outcome.** There were 89 enqueues. 71 left the queue by merging (GitHub records these as
  removals with reason `merged`), and 18 were failed-check removals (`failed_checks`). The
  failed-check removals hit 11 pull requests and cost 837 minutes in the queue. PR #1454 took
  15.6 hours from first enqueue to merge, with five failed-check removals.
- **No failed-check removal traced to the pull request under test.** For 11 of the 18, a later
  re-enqueue built a group with an identical git tree and passed; for 1 more (#1406), a rerun of the
  same group SHA passed. The other 6 are attributed by failure signature. One of those patterns
  (`F2`) exposes a production defect on `main`, which DMS-1696 tracks.
- **Every failed-check removal followed a failed gate.** Each of the 18 came after a required gate
  completed with `failure`. None of the 100 timelines shows a manual dequeue or another removal
  reason in the window, and no collected run or job ended `timed_out`.
- **One active pattern drives the current kickouts.** `F1` is a CMS SQL Server provisioning test
  added on 2026-11-12. It caused 5 of the last 9 removals, including all three removals of PR #1337
  on 2026-11-19, and it has failed in seven pull requests. No earlier ticket covered it; it is now
  [DMS-1704](https://edfi.atlassian.net/browse/DMS-1704) (High).
- **Other queue causes.**
  - `F3` and `F4` (6 removals) were fixed on `main` on 2026-10-23 and 2026-11-10 and did not recur
    in the window after the fixes.
  - `F2` (3 removals) is open as DMS-1696.
  - `F5` (2 removals, CDC Qualification failures without test names) and `F6` (1 removal, a CMS
    hang guard) remain unexplained; diagnostic ticket [DMS-1706](https://edfi.atlassian.net/browse/DMS-1706) covers them.
  - `F7` (1 removal, a GitHub action download during job setup) needs no change.
- **Most failed pull request jobs show CI working as intended.** Of the 244 failed jobs collected
  (see the census for its boundaries):
  - 53 are compile, analyzer or format errors.
  - 55 are on Dependabot Compose branches, which DEVOPS-208 covers.
  - 65 are pull-request-specific test or verification failures with no signature shared across
    branches.
- **The avoidable PR noise is mostly external downloads.** 30 failed jobs came from external
  downloads (`F7`), 18 of them from installing Pester from the PowerShell Gallery. Another 5 came
  from the BulkLoadClient `Stack empty` race (`F8`, DMS-1568). The download fixes are
  [DMS-1705](https://edfi.atlassian.net/browse/DMS-1705).
- **Two evidence gaps stop root-cause analysis.** First, `CDC Qualification (Contract)` logs do not
  name the failing tests, and its artifacts expire after 14 days (`F5`). Second, test hang guards do
  not say what was stuck. Both are in [DMS-1706](https://edfi.atlassian.net/browse/DMS-1706).

## Method and sources

Evidence was collected with read-only queries through the work GitHub account (`gh-work`) and `jira`.
No CI was rerun and no queue state was changed during collection. The follow-up tickets were created
and linked after the evidence was reviewed. Raw logs and query output stayed outside the repository.

| Evidence | Query |
| --- | --- |
| Active rules and queue settings | `gh-work api repos/Ed-Fi-Alliance-OSS/Data-Management-Service/rules/branches/main` |
| Ruleset update times | `gh-work api repos/Ed-Fi-Alliance-OSS/Data-Management-Service/rulesets` |
| Workflow runs | `gh-work api "repos/<repo>/actions/runs?event=<pull_request\|merge_group>&created=<from>..<to>&per_page=100" --paginate`, with `pull_request` queried in date chunks to stay under the 1,000-result search cap |
| Jobs for each attempt | `gh-work api "repos/<repo>/actions/runs/<run>/attempts/<n>/jobs?per_page=100" --paginate`, for every attempt of each failed, cancelled or retried run (319 runs) |
| Job logs | `gh-work api repos/<repo>/actions/jobs/<job>/logs --allow-escape-sequences > <job>.log`, by job ID so each attempt is read on its own. Without the flag, `gh` refuses to write logs that contain terminal escape sequences |
| Queue timeline | `gh-work api graphql --paginate` over `search(query: "repo:<repo> is:pr updated:>=2026-10-20", type: ISSUE, first: 50, after: $endCursor)` with `timelineItems(itemTypes: [ADDED_TO_MERGE_QUEUE_EVENT, REMOVED_FROM_MERGE_QUEUE_EVENT, MERGED_EVENT])`, then `reason` and `beforeCommit` for each removal. The cursor variable must be named `$endCursor`, or `--paginate` repeats the first page without error |
| Gate timing | `gh-work api "repos/<repo>/commits/<group-sha>/check-runs?filter=all"` and `.../status` for each removed group |
| Same-tree comparison | `gh-work api repos/<repo>/git/commits/<sha> --jq .tree.sha` for each failed group and the pull request's next group or merge commit |
| Artifacts | `gh-work api "repos/<repo>/actions/runs/<run>/artifacts"` (`expired`, `expires_at`); the 2026-11-12 E2E setup logs were downloaded and read |
| Cancelled runs | Each cancelled `pull_request` run compared with newer runs of the same workflow on the same branch created before it ended |
| Workflow at each SHA | `git show <sha>:.github/workflows/on-dms-pullrequest.yml` (and the Config workflow) |
| Jira overlap | `jira issue list` text searches in DMS and DEVOPS for the failing test names, signatures and CI topics |

Two cautions apply to repeating this work:

- `gh run view <run> --job <id> --log` returns the **latest** attempt's log when a run was retried.
  Fetch logs by job ID from REST to read an earlier attempt.
- Some job logs contain bytes that make `grep` treat them as binary. Use `grep -a` so matches are not
  silently dropped.

Classification rules:

- **Removal cause.** A removal is assigned to the required gate that completed with `failure` before
  it. The queue removed groups 1 to 25 seconds after that gate finished. Failures that completed
  after the removal are recorded as "would also have failed" and are not counted.
- **Later pass.** For removals, "re-enqueue, identical tree" means a later group for the same pull
  request had the same git tree and passed; "same-SHA rerun" means another attempt of the same group
  SHA passed. For pull request jobs, "same SHA later passed" means another run or attempt of the same
  workflow at the same head SHA succeeded. A missing later pass is not treated as proof that the
  author caused the failure, because a new push can supersede a flaky run.
- **Patterns.** A pattern is a failure signature (failing test name, or error text from an external
  service) seen on more than one branch, plus the singletons listed under `U`.

## Queue configuration and mechanics

Active rules on `main` when read on 2026-11-19. Ruleset `main` was last updated 2026-09-22 and `cla`
2026-04-01, both before the window. That suggests, but does not prove, that the rules were unchanged
during the window:

- Required checks: `license/cla`, `DMS CI Gate` and `Config CI Gate`, all from GitHub Actions.
- Merge queue settings:

  | Setting | Value |
  | --- | --- |
  | Grouping strategy | `ALLGREEN` |
  | `max_entries_to_build` | 1 |
  | `max_entries_to_merge` | 3 |
  | `min_entries_to_merge_wait_minutes` | 5 |
  | Check response timeout | 240 minutes |
  | Merge method | squash |

- Ruleset history (`/rulesets/<id>/history`) returns 404 for this account, so the history inside
  the window cannot be confirmed.
- The `concurrency` expression was the same at 1bb57c6d98 (2026-10-21), 5043fa4b90 (2026-11-02) and
  bef28de1b (2026-11-18), in both gate workflows:
  `${{ github.workflow }}-${{ github.event.pull_request.number || github.ref }}` with
  `cancel-in-progress: true`.

Observed mechanics:

- **M1. Orphaned merge-group runs.** When one gate fails, the queue removes the group, but the other
  workflow keeps running. All 5 cancellations observed came seconds after the same pull request was
  re-enqueued on the same base, which reuses the `gh-readonly-queue/main/pr-<n>-<base>` ref and so
  the concurrency group. The timing is consistent with a concurrency cancel; the API does not record
  the cancellation cause. Example: #1378 was re-enqueued 24 s after removal and its DMS jobs were
  cancelled from 03:35:17. For #1337's third removal, the DMS run kept going until 17:07:59, 25 minutes after the
  removal. Thirteen runs continued after their group was removed, about 224 minutes of wall-clock
  workflow time in total. This adds runner load but does not hold the queue.
  - Disposition: no change now. The diagnostic ticket (DMS-1706) records the post-removal runtime so the
    team can decide later with data. The gate workflows trigger on `merge_group` only for
    `checks_requested`, so they receive nothing when a group is removed. Cancelling on removal would
    need new machinery.
- **M2. Rerunning a failed gate.** Disposition: DMS-1706 (diagnostic) records rerun request and dispatch
  times so the next case can be compared.
  - #1437: run 41867220273's `Config CI Gate` failed at 16:53:55 (the `F4` test) and attempt 2
    started at 16:54:07. The queue did not remove the pull request and merged it at 17:21:57 on
    that attempt.
  - #1406: run 40560385720's `DMS CI Gate` failed at 19:18:44, the removal followed at 19:19:04, and
    the rerun started at 19:19:51. The rerun passed, but the pull request had already left the
    queue and had to be re-enqueued.
  - This is an observation with an unknown mechanism. One success and one failure do not establish
    a rerun window, and the rerun request time is not recorded, only the attempt start.
- **M3. Scheduled enqueue.** After its first removal, #1454 was re-enqueued three times overnight by
  `scheduled-merge-queue-enqueue.yml` (02:15, 05:06 and 08:15 UTC on 2026-11-17), and by hand
  otherwise. Each scheduled attempt tests a fresh group, so the schedule works as designed (DMS-1561).
  It cannot help when the flake recurs.
  - Disposition: no change.

## Cancellations, timeouts and missing statuses

- **Timeouts.** No workflow run or job in the window ended `timed_out`. No removal was due to the
  240-minute check timeout.
- **Missing statuses.** None found among the removed groups. Each had all three required contexts
  reported, and every failed-check removal followed a gate that had completed with `failure`. One `merge_group` run was
  still in progress at the snapshot. That was #1337's final group, and it passed.
- **Cancelled merge-group runs.** All 5 belong to a group that was already removed, and each was
  cancelled seconds after the same pull request was re-enqueued on the same base (`M1`). None caused a
  removal.
- **Cancelled `pull_request` runs.** 212 of the 213 were superseded: a newer run of the same
  workflow on the same branch started before the cancelled run ended. That is the
  pattern a `cancel-in-progress` concurrency group produces. The remaining run (42160178919, `DMS-1634`) was
  cancelled 23 seconds after it started with no newer run, which looks like a manual cancel. None of
  these is a failure pattern.

## Merge queue removals

Times are UTC. "Gate" is the first required gate to complete with `failure`. "Later pass" uses the
definitions under Method.

| # | PR | Removed | Group | Gate (completed) | Run/attempt/job | Failing test or step | Pattern | Later pass |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | [#1377](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1377) | 10-20 02:36:31 | 60c484b993 | Config 02:36:10 | [39358556520/1/118419148890](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/39358556520/job/118419148890) | Integration (MSSQL): `It_suppresses_the_next_interval_behind_the_new_job` | F3 | no (base moved) |
| 2 | [#1374](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1374) | 10-20 21:17:45 | 14f2f364b4 | Config 21:17:44 | [39496492690/1/118882942781](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/39496492690/job/118882942781) | same test | F3 | no (base moved) |
| 3 | [#1381](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1381) | 10-20 22:29:04 | 5c7af15e5c | Config 22:29:00 | [39505609754/1/118913781613](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/39505609754/job/118913781613) | CMS unit: `It_creates_the_certificate_once` hit its 30 s `WaitAsync` guard. DMS would also have failed at 22:54 ([39505609536/1/118918383901](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/39505609536/job/118918383901), `It_preserves_observation_timeout_when_provider_returns_cancelled_after_endpoint_timeout`) | F6 | no (base moved) |
| 4 | [#1378](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1378) | 10-21 03:34:27 | d64036c85d | Config 03:34:05 | [39537441572/1/119017346011](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/39537441572/job/119017346011) | `It_suppresses_the_next_interval_behind_the_new_job` | F3 | re-enqueue, identical tree (f69a57998e) |
| 5 | [#1396](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1396) | 10-23 15:52:47 | 50bd423d97 | DMS 15:52:22 | [39975051264/1/120487601886](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/39975051264/job/120487601886) | CDC Qualification (Contract): `controller-unit` 2 failed, names not logged | F5 | re-enqueue, identical tree (5958419028) |
| 6 | [#1406](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1406) | 10-27 19:19:04 | 00f2991980 | DMS 19:18:44 | [40560385720/1/122387954329](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40560385720/job/122387954329) | CDC Qualification (Contract): `controller-unit` 1 failed, name not logged | F5 | same-SHA rerun (attempt 2, started 19:19:51, after removal) |
| 7 | [#1404](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1404) | 10-29 15:46:00 | b5084fe2ad | Config 15:45:37 | [40901520699/1/123545103902](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40901520699/job/123545103902) | Integration (MSSQL): `It_marks_the_client_of_an_Application_using_the_claim_set` | F4 | re-enqueue, identical tree (32a65a28f4) |
| 8 | [#1420](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1420) | 11-03 03:35:15 | 199ca79874 | Config 03:34:57 | [41571213368/1/125704440699](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/41571213368/job/125704440699) | same test | F4 | re-enqueue, identical tree (5b8049c4c6) |
| 9 | #1420 | 11-03 04:10:19 | e232a87792 | Config 04:10:13 | [41574680474/1/125715468056](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/41574680474/job/125715468056) | same test | F4 | re-enqueue, identical tree (5b8049c4c6) |
| 10 | [#1456](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1456) | 11-16 17:59:04 | ddb58e8eae | Config 17:58:39 | [43918933005/1/133480829592](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/43918933005/job/133480829592) | DMS Provisioning Integration: `It_ends_a_create_paused_before_the_rename_as_lock_lost` | F1 | no (base moved) |
| 11 | [#1454](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1454) | 11-16 22:58:16 | 943946d842 | DMS 22:57:53 | [43968007778/1/133658384790](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/43968007778/job/133658384790), [43968007778/1/133652750868](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/43968007778/job/133652750868) | Unit: `It_classifies_workflow_timeout_after_mutation_as_incomplete_retryable`, and CDC Qualification | F2 | no (base moved) |
| 12 | #1454 | 11-17 03:23:56 | 494646fa7f | Config 03:23:51 | [44006879542/1/133784366462](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44006879542/job/133784366462) | CMS unit: `It_propagates_the_cancellation_on_SQL_Server_instead_of_a_readiness_reason` ("Not listening") | F2 | no (base moved) |
| 13 | #1454 | 11-17 04:47:33 | 9855e2932c | DMS 04:47:24 | [44014910321/1/133813691164](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44014910321/job/133813691164) | Unit: `It_should_evict_faulted_entry_and_retry_compilation` | F2 | re-enqueue, identical tree (merge commit 596faa10ee) |
| 14 | #1454 | 11-17 05:36:02 | 17bb346f99 | Config 05:35:51 | [44021746460/1/133832717601](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44021746460/job/133832717601) | `It_ends_a_create_paused_before_the_rename_as_lock_lost` | F1 | re-enqueue, identical tree (596faa10ee) |
| 15 | #1454 | 11-17 09:01:32 | 3d38ebc060 | DMS 09:01:08 | [44045208887/1/133914369808](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44045208887/job/133914369808) | Build and Start DMS, `Set up job`: download of the `docker/login-action` tarball timed out after 3 attempts | F7 | re-enqueue, identical tree (596faa10ee) |
| 16 | [#1337](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1337) | 11-19 15:32:50 | ef8cc65980 | Config 15:32:39 | [44550341198/1/135652272240](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44550341198/job/135652272240) | `It_ends_a_create_paused_before_the_rename_as_lock_lost` | F1 | re-enqueue, identical tree (bbdad7f10c, merged 18:29:27) |
| 17 | #1337 | 11-19 16:12:23 | 50bb345113 | Config 16:12:01 | [44557099826/1/135676226713](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44557099826/job/135676226713) | same test | F1 | re-enqueue, identical tree (bbdad7f10c) |
| 18 | #1337 | 11-19 16:43:17 | 2301cb47d1 | Config 16:42:56 | [44563712973/1/135699855714](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44563712973/job/135699855714) | same test. The orphaned DMS run later failed on a NuGet 429 in Bootstrap Pester ([44563714264/1/135700559689](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44563714264/job/135700559689)) and `It_retains_initial_awaiting_offset_state_and_resumes_without_recreation` ([44563714264/1/135708251969](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44563714264/job/135708251969)) | F1 | re-enqueue, identical tree (bbdad7f10c) |

Removals by pattern:

| Pattern | Removals |
| --- | --- |
| F1 | 5 |
| F2 | 3 |
| F3 | 3 |
| F4 | 3 |
| F5 | 2 |
| F6 | 1 |
| F7 | 1 |

Minutes spent in the queue before removal (enqueue to removal):

| PR | Minutes |
| --- | --- |
| #1454 | 267 |
| #1374 | 132 |
| #1381 | 100 |
| #1337 | 94 |
| #1406 | 50 |
| #1396 | 49 |
| #1378 | 42 |
| #1420 | 38 |
| #1456 | 30 |
| #1404 | 20 |
| #1377 | 14 |

The queue builds at most one group at a time (`max_entries_to_build: 1`), so while a failing group
builds, no other group does.

## Failure patterns

### F1. CMS SQL Server provisioning takeover ends as `InstanceChanged` instead of `LockLost`

- **Symptom.** In `Run DMS Provisioning Integration Tests` (Config CI Gate),
  `Given_a_paused_worker_loses_its_holder_and_another_replaces_the_target.It_ends_a_create_paused_before_the_rename_as_lock_lost`
  fails with
  `Expected _beforeRename.Result to be DmsProvisioningFailureCode.LockLost {value: 21}, but found DmsProvisioningFailureCode.InstanceChanged {value: 39}`.
- **Source.**
  [`MssqlDeleteTests.cs`](../../../src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration/DataStoreProvisioning/MssqlDeleteTests.cs),
  introduced by cc5704e00 (DMS-1438, #1445, 2026-11-12). The PostgreSQL counterpart,
  `It_ends_a_create_paused_before_the_rename_as_lock_lost_and_drops_its_staging`, did not fail in
  the window.
- **Recurrence.** 12 failed jobs across seven pull requests (#1456, #1454, #1337, #1464, #1465,
  #1460 and #1467), or eight refs if #1337's branch and queue groups are counted separately. Three of
  the pull request failures later passed at the same SHA, for example:
  - #1337 run 44168111376 (head b4a6f56770b92bbdd8dda3c2dd6bd5b1c811c2c6): attempt 1 failed
    ([44168111376/1/134339717134](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44168111376/job/134339717134)); attempt 2 passed 20:59:43Z to 21:13:26Z on 11-17
    ([44168111376/2/134364201442](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44168111376/job/134364201442)).
  - #1467 run 44574532373 (head ca1bf997b8b463d0aa31211ac8b851340d559ee0): attempt 1 failed;
    attempt 2 passed 17:46:41Z to 17:59:22Z on 11-19 ([44574532373/2/135759764339](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44574532373/job/135759764339)).
  - #1337's fourth queue group bbdad7f10c9bad02f0120eb3ffe31c220ca4c609, with the same tree as the
    three failed groups, passed the job 17:29:49Z to 17:42:53Z ([44577708467/1/135749848589](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44577708467/job/135749848589))
    and `Config CI Gate` at 18:00:48Z ([44577708467/1/135768261081](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44577708467/job/135768261081)); the PR merged at 18:29:27Z.
- **Stored results.** The `DMS Provisioning Integration Test Results` artifact of run 44550341198
  (artifact 14544017881, group ef8cc659809e039f63d9b5a7086190cf84855be8, expires 2027-02-17) shows
  SQL Server 488 of 489 passed, with this test the only failure, and PostgreSQL 454 of 454.

  Occurrences (date, pull request, run/attempt/job):

  | Date | PR | Run/attempt/job | Same SHA later passed |
  | --- | --- | --- | --- |
  | 11-16 | #1456 (queue) | [43918933005/1/133480829592](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/43918933005/job/133480829592) | no |
  | 11-17 | #1464 | [44012430780/1/133802653399](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44012430780/job/133802653399) | no |
  | 11-17 | #1454 (queue) | [44021746460/1/133832717601](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44021746460/job/133832717601) | no |
  | 11-17 | #1464 | [44143865961/1/134253760118](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44143865961/job/134253760118) | no |
  | 11-17 | #1337 | [44168111376/1/134339717134](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44168111376/job/134339717134) | yes |
  | 11-17 | #1337 | [44179022484/1/134378233317](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44179022484/job/134378233317) | yes |
  | 11-18 | #1465 | [44382371027/1/135078184754](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44382371027/job/135078184754) | no |
  | 11-18 | #1460 | [44405763456/1/135162854186](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44405763456/job/135162854186) | no |
  | 11-19 | #1337 (queue) | [44550341198/1/135652272240](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44550341198/job/135652272240) | no |
  | 11-19 | #1337 (queue) | [44557099826/1/135676226713](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44557099826/job/135676226713) | no |
  | 11-19 | #1337 (queue) | [44563712973/1/135699855714](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44563712973/job/135699855714) | no |
  | 11-19 | #1467 | [44574532373/1/135738520055](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44574532373/job/135738520055) | yes |

  Outcomes for the job, counted by day:

  | Day | Jobs completed | Failed with F1 | Other failures |
  | --- | --- | --- | --- |
  | 11-12 to 11-14 | 6 | 0 | 0 |
  | 11-16 | 9 | 1 | 0 |
  | 11-17 | 33 | 5 | 0 |
  | 11-18 | 14 | 2 | 1 |
  | 11-19 (to 17:30) | 7 | 4 | 0 |

- **Impact.** 5 queue removals. PR #1337 needed four enqueues on 2026-11-19, and PR #1454's fourth
  removal was this test.
- **Cause.** Unconfirmed; this is a hypothesis. The test pauses worker A at `BeforeRename`, kills A's
  holder session, lets worker B create the target, and then resumes A. In
  `DmsDataStoreProvisioningService.Publication.cs`, after the hook, A dispatches
  `RenameIntoTargetAsync` on the holder session. Two outcomes are possible:
  - If the lost holder is noticed before dispatch, the rename is withdrawn and A ends as `LockLost`.
  - If the dead connection is noticed only when the rename is sent, the rename is in doubt.
    Settlement then finds B's instance under the name and reports `InstanceChanged`.

  The same fixture's `BeforeDrop` case documents exactly this second path ("SqlClient learns of the
  killed connection only when it sends") and expects `InstanceChanged` there. Which path is taken
  appears to depend on timing. The failure message does not include `Detail.Diagnostic`, so the
  failed runs do not show which step reported the outcome.
- **Validation still needed.** Record `Detail.Diagnostic` (step and operation) for the paused create.
  Then force each ordering deterministically, with the loss observed before dispatch and the loss
  observed only during the send.
- **Disposition.** Fix ticket DMS-1704, High priority. No Jira issue or pull request found mentions
  this test, `LockLost` or `InstanceChanged` (searched 2026-11-19T18:55Z).

### F2. #1454 races already analyzed in DMS-1696

- **Symptoms.** Four races, already analyzed in DMS-1696:
  - the administrative timeout ordering;
  - CDC terminal containment;
  - CMS stalled-login listener shutdown ("Not listening");
  - mapping-cache failure publication before eviction, which is a production ordering defect.
- **Recurrence.** 3 removals, all on #1454 (rows 11 to 13), and 3 unit-job failures in total. None
  appeared outside #1454's queue groups in the window.
- **Disposition.** Existing [DMS-1696](https://edfi.atlassian.net/browse/DMS-1696) (Open, Medium,
  unassigned). DMS-1696 covers rows 11 to 13 but not rows 14 and 15, which are F1 and F7.
  Recommendation: raise it to High. It accounts for 3 removals, one of its issues is a production
  defect, and its CMS fixture race (`StalledLoginServer`) was introduced by the same commit as F1
  (cc5704e00).
  [DMS-1697](https://edfi.atlassian.net/browse/DMS-1697) (Open) is the preventive analyzer
  follow-up; no change is needed to it.

### F3. CMS SQL Server job-schedule suppression (fixed)

- **Symptom.** `It_suppresses_the_next_interval_behind_the_new_job` expected `SkippedActiveJob` but
  found `AlreadyEnqueued`.
- **Recurrence.** 10 failures on 7 branches between 10-20 and 10-23, including 3 removals (rows 1,
  2 and 4). The pull request occurrences were on #1185, #1386, #1385 (three) and #1390 (two), for
  example [39635447764/1/119344787215](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/39635447764/job/119344787215) and [39883264013/1/120185525926](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/39883264013/job/120185525926).
- **Cause.** Confirmed in DMS-1578: two forced due times shared one SQL Server clock tick. The fix is
  595841902 (#1396, DMS-1578/DMS-1607), committed 2026-10-23T16:25:55Z. The last failure started at
  2026-10-23T13:20:38Z, before the fix, on a branch that did not yet contain it.
- **Disposition.** No change. The pattern stopped after the fix.

### F4. CMS SQL Server claim-set rename timestamp precision (fixed)

- **Symptom.** `It_marks_the_client_of_an_Application_using_the_claim_set` failed because the stored
  `_pendingSince["Renamed"]` was earlier than the rename start by under 1 ms, for example
  `16:33:35.010` against `16:33:35.010038`.
- **Recurrence.** 8 failures on 7 branches between 10-28 and 11-09. That includes 3 removals (rows
  7 to 9), plus #1437's first attempt, which a rerun recovered (`M2`,
  [41867220273/1/126704853440](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/41867220273/job/126704853440)). The pull request occurrences were on #1404, #1420, #1434
  and #1446, for example [40998024270/1/123875758736](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40998024270/job/123875758736).
- **Cause.** Inferred from the fix and the rounding in the failures. In
  `ClaimSetRepository` (SQL Server), the mark-clients update passed `PendingSince` as an anonymous
  Dapper parameter, which binds a .NET `DateTime` as SQL `datetime` and rounds it to 1/300 second
  before it is stored in a `DATETIME2` column. 38c758539 (#1446, DMS-1684), committed
  2026-11-10T21:46:24Z, binds it with `DbType.DateTime2` and gives the test a timestamp that
  `datetime` cannot represent. The last failure was at
  2026-11-09T02:00:32Z.
- **Disposition.** No change.

### F5. CDC Qualification (Contract) failures without test names

- **Symptom.** `CDC Qualification (Contract)` (a DMS CI Gate dependency) prints only a lane summary,
  for example `controller-unit: Failed, total=4756, passed=4755, failed=1`. The failing test names
  and assertions are not in the log.
- **Recurrence.** 13 failed jobs. Three were in the queue: #1396, #1406, and #1454's first group,
  which DMS-1696 analyzed from the still-live artifact. Ten were pull request jobs, on #1406, #1385,
  #1415, #1404, #1421, #1422, #1434, #1443, #1452 and #1440; two of them later passed at the same
  SHA. One more, on Dependabot branch #1462, is counted under F9. Examples: [40521383061/1/122249366352](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40521383061/job/122249366352) and [40787246566/1/123163618804](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40787246566/job/123163618804).
  Pester install failures that happened inside this job are counted under F7.
- **Evidence gaps.**
  - The `cdc-qualification-Contract-All-<attempt>` artifact is kept for 14 days
    (`retention-days: 14` in `on-dms-pullrequest.yml`). The artifacts for #1396 and #1406 no longer
    appear in their runs' artifact lists, so the failing tests for 2 removals cannot be recovered.
  - DMS-1696 also reports that the exported TRX removes assertion output.
- **Disposition.** Diagnostic ticket DMS-1706. The cause of the #1396 and #1406 failures cannot be
  determined from the available evidence.

### F6. Single-occurrence timing and hang failures in unit tests

These are single occurrences, each on one branch:

| Test | Where | What happened |
| --- | --- | --- |
| CMS `It_creates_the_certificate_once` | #1381 queue | The 30 s `WaitAsync` hang guard expired in `Given_a_development_certificate_first_start_with_concurrent_mint_validation_and_refresh` |
| DMS `It_preserves_observation_timeout_when_provider_returns_cancelled_after_endpoint_timeout` | #1381 queue, after the removal | Expected `StatusObservationTimeout`, found `StatusEndpointTimeout` |
| DMS `It_records_timed_out_direct_fill_duration_when_timeout_occurs_after_a_successful_candidate` | #1446, 11-10, [42887211460/1/130084913814](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/42887211460/job/130084913814) | `AssertDirectFillSkippedLog` found no log entry. Passed on rerun at the same SHA |
| DMS CDC `It_retains_initial_awaiting_offset_state_and_resumes_without_recreation` | #1337 orphaned run, 11-19 | `Observed` expected, `Unavailable` found |

The certificate test failed at its hang guard. The other three assert the outcome of a timeout or
state-observation path. Whether each depends on real-time scheduling has not been verified.

- **Disposition.** No individual fix ticket, because one occurrence each gives too little evidence.
  Prevention is in scope for DMS-1697 (real-time waits in unit tests). Next-occurrence capture is in
  DMS-1706: a hang guard should name the task that did not complete.

### F7. External downloads and registries

| Source and observed symptom | Failed jobs | Dates | Where, with examples |
| --- | --- | --- | --- |
| PowerShell Gallery: `Install-Module Pester -RequiredVersion 5.7.1` ("Package 'Pester' failed to download" or "No match was found") | 18 jobs in 11 runs | 10-22 to 10-27 | Bootstrap Pester, Verify DMS Packages, CDC Qualification, Config lock-file parity and package verification. All are gate dependencies. 12 later passed at the same SHA. On #1391, #1394, #1405, #1406, #1404, #1385 and #1409, for example [40402301081/2/121875653199](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40402301081/job/121875653199) and [40539275846/1/122313277859](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40539275846/job/122313277859) |
| Docker registry: buildx `failed to copy: httpReadSeeker` (Docker CDN) | 4 | 10-29 to 11-17 | E2E image builds: [40902146728/1/123547349028](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40902146728/job/123547349028), [42707353285/1/129467821907](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/42707353285/job/129467821907), [43296420888/1/131462764254](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/43296420888/job/131462764254), [44020639480/1/133829320320](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44020639480/job/133829320320) |
| Docker registry: buildx `blob sha256:… not found` | 1 | 11-09 | Build CI Docker Images, #1452, [42707353594/1/129467548058](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/42707353594/job/129467548058) |
| Docker Hub: `docker run postgres:18.6-alpine` returned `unauthorized: authentication required` | 1 | 10-22 | Backend PostgreSQL integration container start, #1390, [39799304299/1/119903280648](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/39799304299/job/119903280648) |
| Docker Scout: `Unexpected HTTP response: 403` | 1 | 10-26 | Dockerfile analysis, #1397, [40332100892/1/121603891210](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40332100892/job/121603891210) |
| Azure Artifacts: ApiSchema package download failed, or was still running when the 60 s DMS health check expired | 2 | 11-12 | SQL Server E2E setup (`build-dms-setup.log` artifacts read): #1441 [43276515636/1/131392917227](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/43276515636/job/131392917227), #1456 [43279028441/1/131401987878](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/43279028441/job/131401987878) |
| Azure DevOps: "Azure DevOps Services Unavailable" page | 1 | 10-23 | Scheduled Smoke Test, #1388, [40040116335/1/120715663277](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40040116335/job/120715663277) |
| Maven Central: openapi-generator jar, `429 Too Many Requests` | 1 | 11-17 | Scheduled Smoke Test (`build-sdk.ps1 DownloadCodeGen`), #1337, [44191446465/1/134431646831](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44191446465/job/134431646831) |
| Puppeteer Chrome download, `ECONNRESET` | 1 | 10-23 | Validate Resources Spec, #1396, [39965042106/1/120458971615](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/39965042106/job/120458971615) |
| NuGet: BulkLoadClient package resolution `429 Too Many Requests` inside a Pester test | 1 | 11-19 | Bootstrap Pester in #1337's orphaned queue run, [44563714264/1/135700559689](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44563714264/job/135700559689) |
| GitHub: action tarball download timed out (`docker/login-action`) | 1 | 11-17 | Set up job, #1454 removal 5, [44045208887/1/133914369808](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44045208887/job/133914369808) |

Total: 30 pull request jobs, 1 queue removal, and 1 failure in an orphaned queue run.

- **What is known.** The symptoms differ, and the logs do not establish a provider outage for any of
  them:
  - PowerShell Gallery: the install failed with "Package 'Pester' failed to download", "No match was
    found" or "Source Location … is not valid". The failures cluster in 10-22 to 10-27, and 12 passed
    at the same SHA, for example #1405 run 40402301081 (head
    08c88eb05013b43ab1c912bed02537289268ccba) attempt 3, 22:07:05Z to 22:19:58Z on 10-26
    ([40402301081/3/121877593618](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40402301081/job/121877593618)), and #1409 run 40539275846 (head
    831fd4a2e695730997551738ab18439a80fb17da) attempt 2, 18:57:29Z to 18:58:44Z on 10-27
    ([40539275846/2/122402826314](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40539275846/job/122402826314)). The cause is not recorded.
  - Docker CDN `httpReadSeeker: failed open`: a blob fetch was interrupted. Three passed at the same
    SHA, for example #1417 run 40902146728 (head e5bdbea158191e22eb2f7729de0f3e58f7faeff7) attempt 3,
    16:53:35Z to 16:58:39Z on 10-29 ([40902146728/3/123599415982](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40902146728/job/123599415982)).
  - `blob … not found` (#1452): no same-SHA pass; whether a cache reference or the registry was at
    fault is unknown.
  - Docker Hub `401 unauthorized` and Docker Scout `403`: authorization responses, not availability
    signals. Both passed on rerun: #1390 run 39799304299 (head
    432a43ce4bdecb6d469e2cd2f96b727836c063ed) attempt 2, 15:43:58Z to 15:58:38Z on 10-22
    ([39799304299/2/119924771082](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/39799304299/job/119924771082)), and #1397
    run 40332100892 (head 960ce4027e1f94358d90cdf2f566aaa29225f5ab) attempt 2, 13:39:59Z to
    13:41:54Z on 10-26 ([40332100892/2/121605502944](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40332100892/job/121605502944)).
  - ApiSchema packages: #1456's setup log reports "ApiSchema download failed" with no HTTP status;
    #1441's DMS container was still downloading packages when the 60 s health check expired. These
    are different symptoms, a download error and a slow download. Both passed on attempt 2: #1441
    run 43276515636 (head fd5fe1bf2f1a66efcf452ab9acaed5700f42f560), 20:14:47Z to 20:33:00Z on 11-12
    ([43276515636/2/131462559936](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/43276515636/job/131462559936)), and #1456 run 43279028441 (head
    1397705a1b5d89f01e8315cb83d3cc63124dd3e0), 19:10:20Z to 19:19:14Z
    ([43279028441/2/131424264294](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/43279028441/job/131424264294)).
  - Azure DevOps: an `Invoke-RestMethod` call in `Invoke-DestructiveSdkTests.ps1` received the
    "Azure DevOps Services Unavailable" page; the log does not name the URL. A later run at the same
    SHA passed (#1388 run 40040116335, head ceefe901ff1659956ba6d8f20adac20db1bca956, attempt 2,
    17:36:20Z to 17:55:11Z on 10-26, [40040116335/2/121738446351](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40040116335/job/121738446351)).
  - Maven Central and NuGet: explicit `429 Too Many Requests`. The Maven case passed on rerun (#1337
    run 44191446465, head d5076a4d61f569cdfb792801f0a139ba90c5e1e2, attempt 2, 23:32:47Z to 23:49:32Z
    on 11-17, [44191446465/2/134452335868](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44191446465/job/134452335868)).
  - Puppeteer: the third-party `swaggerexpert/swagger-editor-validate` action installs Puppeteer,
    whose Chrome download failed with `ECONNRESET`. The rerun passed (#1396 run 39965042106, head
    3a6af67fb198c6996972c4fa5a424199b6950305, attempt 2, 14:31:21Z to 14:33:24Z on 10-23,
    [39965042106/2/120470996565](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/39965042106/job/120470996565)).
  - GitHub action tarball: the download timed out while GitHub prepared the job. #1454's next group
    with the same tree passed and merged as 596faa10ee.
- **Retry coverage today.** None of these steps retries, except image pulls routed through
  `.github/actions/pull-ci-images` (jittered retries from DMS-1463), which none of these failures
  used. All eight `Install-Module Pester` sites on `main` pin `-RequiredVersion 5.7.1`: three in
  `on-dms-pullrequest.yml`, three in `on-config-pullrequest.yml`, and one each in
  `nightly-cdc-qualification.yml` and `powershell-analysis.yml`. `eng/ci/Invoke-CdcQualification.ps1`
  imports the installed module with `-MinimumVersion 5.7.1`.
- **Disposition.** Each subgroup is mapped in DMS-1705 (Medium).

### F8. BulkLoadClient `Stack empty` race

- **Symptom.** BulkLoadClient 7.3.20162 fails with `InvalidOperationException: Stack empty.` from
  `log4net.Util.ThreadContextStack.AutoPopStackFrame.Dispose()`, called from `ResourcePipeline`.
- **Recurrence.** 5 jobs, each confirmed by the `InvalidOperationException: Stack empty` text in its
  log:

  | Date | Workflow or job | PR | Run/attempt/job |
  | --- | --- | --- | --- |
  | 10-22 | Populated PostgreSQL 6.1.0 | #1391 | [39730078102/1/119662882499](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/39730078102/job/119662882499) |
  | 10-26 | Scheduled Smoke Test, Populated PostgreSQL 6.1.0 | #1402 | [40367413800/1/121729061285](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40367413800/job/121729061285) |
  | 10-30 | Minimal SQL Server 5.2.0 | #1423 | [41129239501/1/124318600114](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/41129239501/job/124318600114) |
  | 11-02 | Populated PostgreSQL 6.1.0 | #1433 | [41533444958/1/125579664573](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/41533444958/job/125579664573) |
  | 11-10 | Minimal PostgreSQL 6.1.0 | #1446 | [42887211546/1/130078592274](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/42887211546/job/130078592274) |

  All five later passed at the same SHA. The example in the Jira ticket's comment is confirmed:
  [#1446](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1446), run 42887211546, head 6134ad251e3da1df3ce6b5de48434376cffad5c6.
  Job 130078592274 (attempt 1, 19:38:05Z to 19:45:44Z on 11-10) threw at 19:45:34Z, and attempt 2 at
  the same SHA passed ([42887211546/2/130105386027](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/42887211546/job/130105386027), 20:24:10Z to 20:31:42Z;
  the run's attempt 2 ran 20:24:04Z to 20:33:35Z).
- **Impact.** None of these jobs is a required check, so each occurrence cost a rerun but no queue
  removal.
- **Cause.** DMS-1568 confirms the corruption mechanism: a file-name logging scope held across
  `yield return` is disposed on another thread and trims the worker thread's log4net stack, which it
  reproduced with the shipped log4net. Whether a run then throws `Stack empty` depends on thread
  scheduling, which is why most reruns pass. The repository already retries this race in one place:
  `eng/verification/PublisherSnapshotInterop/PublisherSnapshotInterop.psm1` (DMS-1370) recognizes the
  exception text and retries the seed load up to 3 times. The template builds have no such retry.
- **Disposition.** Existing [DMS-1568](https://edfi.atlassian.net/browse/DMS-1568) (Open, Medium).
  Recommendation: widen its title and scope beyond "Populated PostgreSQL 5.2". The race hit Minimal
  and Populated templates, PostgreSQL and SQL Server, and Data Standards 5.2 and 6.1.

### F9. Dependabot Compose pull requests

- **Symptom.** 55 failed jobs (38 E2E, setup or verification and 17 test) on four Dependabot Compose
  branches (#1448, #1461, #1462, #1428) and one Dependabot bundle branch (#1436). They are mostly E2E and Instance Management E2E jobs, and
  none passed later at the same SHA.
- **Disposition.** Existing
  [DEVOPS-208](https://edfi.atlassian.net/browse/DEVOPS-208) ("Dependabot: compose PRs that pass, PR
  limit and cadence"). No new ticket.

### F10. Compile, analyzer and format errors

- **Symptom.** 53 failed jobs with `error CS…`, `error S…`, `error IDE…` or `MSB` on pull request
  branches. Three of them are `Build CI Docker Images` jobs where the compile error happened inside
  the image build.
- **Disposition.** No change. Rejecting these errors is the gates' job.

### F11. Pull-request-specific test and verification failures

- **Symptom.** 65 failed jobs (40 test, 25 verification or E2E) with no failing test shared across
  branches and no same-SHA pass. Examples include #1404's CMS integration tests, #1440's Kafka E2E
  setup, and lock-file or package-version checks. F12's job is counted here.
- **Disposition.** No change. Nothing in the sample ties them to CI, but a single occurrence of a
  flaky test would look the same.

### F12. MSSQL Shard 4 CDC capture-instance test

- **Symptom.** `It_should_fail_closed_on_wrong_capture_instance_name_without_creating_replacement`
  ran for 28 s and then failed, on #1452 (11-11, [43064496597/1/130685442328](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/43064496597/job/130685442328)). It is the only MSSQL Shard 4 failure in the window,
  and it matches the rotating CDC-test family in DMS-1426.
- **Disposition.** Existing [DMS-1426](https://edfi.atlassian.net/browse/DMS-1426) (In QA, #1467
  open). No new ticket.

### U. Unexplained singletons and missing evidence

| ID | Job and occurrence | Evidence | Disposition |
| --- | --- | --- | --- |
| U1 | `Detect Fresh Build Changes` (#1424, 11-02, [41469858379/1/125360269026](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/41469858379/job/125360269026)) | `eng/ci/detect-config-changes.sh` exited non-zero with no output, and no step was marked failed. It passed at the same SHA | DMS-1706: scripts report the step ID that failed |
| U2 | `Verify DMS Packages` (#1337, 11-18, [44405603329/1/135161919779](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44405603329/job/135161919779)) | DocumentCacheAdmin smoke exited 11: "status timed out before a complete status document could be produced". It passed at the same SHA | DMS-1706: report the last status phase observed before the timeout |
| U3 | Scheduled Smoke Test, SQL Server 5.2.0 (#1337, 11-18, [44405603591/1/135188614562](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/44405603591/job/135188614562)) | The CLI printed "Invalid options were entered" and passed on attempt 2 at the same SHA | DMS-1706: report the rejected option names from an allowlist, never argument values |
| U4 | `Verify DMS Packages` on #1454 and #1440 (11-12, [43263993928/1/131342858110](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/43263993928/job/131342858110), [43281598219/1/131407345604](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/43281598219/job/131407345604)) | Pester reported `Failed: 0` and `Inconclusive: 46`, and the step still failed. The interop tests that simulate the `Stack empty` retry passed. It did not recur after 11-12, and neither run has a same-SHA pass | DMS-1706: the step states why it failed when Pester reports no failures |
| — | Job logs [40721939599/1/122942365540](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/40721939599/job/122942365540) (#1415, MSSQL Shard 1, 10-28) and [42752996739/1/129624438329](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/actions/runs/42752996739/job/129624438329) (#1452, PostgreSQL E2E Shard 2, 11-10) | The log endpoint returns `BlobNotFound`, so the cause cannot be determined | DMS-1706: a separately retained job summary keeps the evidence even when the hosted log disappears |

## PR event failure census

The census counts 244 failed `pull_request` jobs in 135 runs on 50 branches. Gate aggregates,
summaries and provenance `final` jobs are excluded, because they only repeat an upstream failure.

Boundaries: jobs were listed only for runs whose latest attempt failed, was cancelled, or was
retried (319 runs), for every attempt of those runs. A failed job inside a run whose only attempt
succeeded would not be listed. About 20 listing calls returned HTTP 502; all failed, cancelled and
earlier attempts were re-queried, but a gap of that kind cannot be ruled out. Runs created after
17:30:54Z on 2026-11-19 are not included.

| Pattern | Failed jobs | Later passed at same SHA |
| --- | --- | --- |
| F10 compile, analyzer or format | 53 | 0 |
| F9 Dependabot Compose | 55 | 0 |
| F11 PR-specific tests and verification | 65 | 0 |
| F7 external downloads | 30 | 22 |
| F5 CDC Qualification, names unknown | 10 | 2 |
| F8 BulkLoadClient `Stack empty` | 5 | 5 |
| F1 provisioning takeover | 7 | 3 |
| F3 job-schedule suppression | 7 | 3 |
| F4 claim-set timestamp | 4 | 2 |
| F6 timing (`It_records_timed_out_direct_fill_duration…`) | 1 | 1 |
| U1 to U3 | 3 | 3 |
| U4 `Verify DMS Packages` inconclusive | 2 | 0 |
| Missing log | 2 | 1 |

The 23 failed `merge_group` jobs are covered by the removal table and by the orphaned-run notes in
`M1`.

## Follow-up tickets

These follow-ups were created from this report and linked from DMS-1683. Jira holds the current
description; the criteria below record what the evidence called for.

### [DMS-1704](https://edfi.atlassian.net/browse/DMS-1704) (fix, High): CMS SQL Server paused-create takeover returns `InstanceChanged` instead of `LockLost`

Covers F1.

The provisioning integration test
`It_ends_a_create_paused_before_the_rename_as_lock_lost` (SQL Server, added in DMS-1438) fails
intermittently with `InstanceChanged`. The failures have cost 5 queue removals since 2026-11-16 and
show up as 12 failed jobs across seven pull requests. Determine which outcome the contract requires when a paused
create's holder is killed and another worker publishes the target before the rename. Then make the
test and, if needed, the publication path deterministic.

Acceptance criteria:

1. When the test fails, the failure message records `Detail.Code`, `Detail.Category` and
   `Detail.Diagnostic` (step and operation). Captured output contains no connection strings or
   credentials.
2. The ticket documents the required outcome in two cases, consistent with the design decisions the
   publication code cites (D-7b, D-12) and with the PostgreSQL counterpart:
   - the loss is observed before the rename is dispatched;
   - the loss is observed only when the rename is sent.
3. Deterministic tests cover both orderings, using explicit signals or hooks rather than added
   delays. Each test asserts the documented code, and the rename appears in doubt only on the
   dispatched path.
4. If product behavior changes, the existing `BeforeFence`, `BeforeDrop` and `AfterRestoreStarted`
   expectations in the same fixture still hold.
5. Regression evidence: each new test fails if the ordering it pins is reversed.

### [DMS-1705](https://edfi.atlassian.net/browse/DMS-1705) (fix, Medium): Make CI tolerant of transient failures in external downloads

Covers F7. Each subgroup is mapped below; retries apply only to transient signals.

| F7 subgroup | Disposition |
| --- | --- |
| PowerShell Gallery Pester install | Covered (criterion 1) |
| Docker CDN interrupted blob fetch | Covered (criterion 4) |
| buildx `blob … not found` | Not retried. One occurrence with no same-SHA pass, and the cause is unknown. DMS-1706 captures the next occurrence |
| Docker Hub 401 on `docker run` | Covered by routing the image through the existing pull action (criterion 4). The 401 is not retried as an availability failure unless that action's evidence shows it is transient |
| Docker Scout 403 | No change. It occurred once, in Dockerfile analysis, and the rerun passed 3 minutes later. A 403 is an authorization response, not evidence of a transient failure |
| ApiSchema download failure | Covered (criterion 3) |
| ApiSchema download still running at the 60 s health check | No retry and no longer wait. DMS-1706 records which startup phase was running at timeout, so the slow-download cause can be confirmed |
| Azure DevOps unavailable page | Covered only if the URL is identified. Criterion 3 requires final failures to name the host, and 5xx responses are retried |
| Maven Central 429 | Covered (criterion 3) |
| NuGet 429 inside a Pester test | Covered (criterion 2) |
| Puppeteer Chrome `ECONNRESET` | No change. The download happens inside the third-party `swaggerexpert/swagger-editor-validate` action, which needs the browser; it occurred once and the rerun passed |
| GitHub action tarball timeout | No change. It happens while GitHub prepares the job, before any workflow step, and the next group with the same tree passed |

Acceptance criteria:

1. All eight `Install-Module Pester -RequiredVersion 5.7.1` sites go through one helper:
   `on-dms-pullrequest.yml` (3), `on-config-pullrequest.yml` (3), `nightly-cdc-qualification.yml`
   (1) and `powershell-analysis.yml` (1). The helper retries with bounded backoff on download or
   repository errors, or the module is restored from a cache keyed on the version. The pinned
   version stays 5.7.1, and the existing `eng/ci/tests` Pester pattern guards the wiring.
2. Bootstrap Pester tests that resolve BulkLoadClient from NuGet use a pre-fetched package or retry
   429 and 5xx responses with bounded backoff that honors `Retry-After`.
3. ApiSchema package downloads in `build-dms.ps1` E2E setup, the openapi-generator download in
   `build-sdk.ps1`, and the Azure DevOps call in the smoke tests retry 429, 5xx and connection resets
   only. Each final failure names the host, the package and version, and the HTTP status, and nothing
   else from the request.
4. Images that jobs start directly (`docker run postgres:…`) are pre-pulled through
   `.github/actions/pull-ci-images`. Base-image fetches in buildx builds retry interrupted blob
   reads, or the ticket records why they cannot.
5. Retries are bounded and visible in the log (attempt number and wait). 401, 403 and 404 responses
   are not retried unless evidence shows they are transient for that endpoint.
6. Tests cover the retry decision for each signal class, including that a 404 or 401 fails
   immediately.

### [DMS-1706](https://edfi.atlassian.net/browse/DMS-1706) (diagnostic, Medium): Retain correlated, sanitized evidence for merge queue and flaky CI failures

Covers F5, F6, M1, M2, U1 to U4 and the missing logs.

Relationship to [DEVOPS-213](https://edfi.atlassian.net/browse/DEVOPS-213): DEVOPS-213 already
plans a gate summary, a failure digest that extracts the first compiler or Pester failure line, and a
weekly health report. DMS-1706 adds only what this spike needed and could not get:

- queue-event correlation;
- retained per-job evidence;
- CDC test names;
- hang-guard and script failure context;
- sanitization rules.

DMS-1706 is linked to DEVOPS-213 and to DMS-1705, and does not duplicate the digest.

Acceptance criteria:

1. **Queue events are correlated after the fact.** A job runs on a schedule or on `merge_group`
   completion and reads the queue timeline and check runs. For each enqueue, failed-check removal,
   merge and rerun it records:
   - the event times;
   - the removal reason;
   - the actor category (manual, scheduled or queue);
   - the PR number;
   - the group head, base and tree SHAs;
   - each required context's state and completion time;
   - run, attempt and job IDs;
   - the rerun request and dispatch times, when the API provides them.

   It records these even when a gate never starts or the other workflow is cancelled. It does not
   rely on a summary written by a gate before the removal happens. The correlated records are
   stored as a workflow artifact of the correlation job and kept for at least 30 days, the same as
   the per-job evidence in criterion 2.
2. **Each failed job keeps a structured summary that outlives the hosted log.** The summary is a
   separate artifact with job and step IDs, conclusions, timestamps, and the first failing test or
   error line, and it is kept for at least 30 days. That preserves the evidence when a hosted log
   later returns `BlobNotFound`.
3. **CDC Qualification names its failures.** `CDC Qualification (Contract)` reports each failing
   test's fully qualified name and a sanitized first assertion line. Its evidence artifact is kept
   for at least 30 days; it is 14 today.
4. **Hang guards and scripts state why they failed.**
   - An expiring test hang guard reports which awaited operations had not completed.
   - `detect-config-changes.sh` and similar `set -e` scripts report the step ID that failed.
   - The DocumentCacheAdmin smoke reports the last status phase observed before its timeout.
   - DMS startup reports which phase was running when the health check expired.
   - A Pester step that fails with no failed tests says why, for example inconclusive results.
   - A CLI that rejects its arguments (U3) reports only the rejected option names, checked against an
     allowlist of the tool's options, never their values.
   - Image pulls and downloads that DMS-1705 routes here (buildx `blob … not found`, the ApiSchema download
     still running at the health check) report the source host, image or package name and version,
     HTTP status or error class, attempt number, and start, end and timeout times. They report no
     URLs with query strings, headers or response bodies.
5. **Captured fields are sanitized and allowlisted.**
   - Only allowlisted fields, step IDs and CLI option names are captured.
   - Full command lines, argument values, HTTP response bodies and unsanitized assertion text are
     never written.
   - Sanitization happens before anything is written to a log, artifact or job summary.
6. **Redaction is tested.** Tests feed representative sensitive inputs into every capture path and
   assert that none appears in the output. The inputs include a connection string with a password, a
   bearer token, a client secret in a CLI argument, and a signed URL.

### Existing tickets: recommended updates

These are recommendations only. The existing tickets keep their current priority and scope, and all
of them are linked from DMS-1683.

| Ticket | Recommendation |
| --- | --- |
| DMS-1696 | Raise to High: F2 caused 3 removals, and one of its issues is a production defect |
| DMS-1568 | Widen the scope beyond Populated PostgreSQL 5.2 to every template build that runs BulkLoadClient |
| DMS-1697 | No change |
| DMS-1426 | No change |
| DEVOPS-208 | No change |
| DEVOPS-213 | No change; linked to DMS-1706 |

## Disposition map

| Pattern | Disposition | Ticket | Priority |
| --- | --- | --- | --- |
| F1 provisioning takeover | Fix | DMS-1704 | High |
| F2 #1454 races | Fix | DMS-1696 (existing) | Raise to High |
| F3 job-schedule suppression | No change: fixed by 595841902, no recurrence | DMS-1578, DMS-1607 (Done) | — |
| F4 claim-set timestamp | No change: fixed by 38c758539, no recurrence | DMS-1684 (Done) | — |
| F5 CDC failures unnamed | Diagnostic | DMS-1706 | Medium |
| F6 timing and hang singletons | Diagnostic and prevention | DMS-1706, DMS-1697 (existing) | Medium |
| F7 external downloads | Fix the covered subgroups; no change for Scout 403, Puppeteer and the action tarball (see DMS-1705) | DMS-1705 | Medium |
| F8 BulkLoadClient race | Fix | DMS-1568 (existing, widen scope) | Medium |
| F9 Dependabot Compose | Fix | DEVOPS-208 (existing) | As set |
| F10 compile, analyzer, format | No change: gates working as intended | — | — |
| F11 PR-specific failures | No change: no shared signature | — | — |
| F12 Shard 4 CDC test | Fix | DMS-1426 (existing, In QA) | As set |
| M1 orphaned runs | No change now; measure | DMS-1706 | — |
| M2 rerun within seconds | Diagnostic | DMS-1706 | Medium |
| M3 scheduled enqueue | No change: works as designed | — | — |
| U1 to U3 | Diagnostic | DMS-1706 | Medium |
| U4 inconclusive package verification | Diagnostic | DMS-1706 | Medium |
| Missing logs | Diagnostic: retained summaries | DMS-1706 | Medium |

## Limits and open gaps

- **Ruleset history.** The ruleset history API returns 404 for this account. The `updated_at` times
  suggest the rules were unchanged during the window but do not prove it.
- **Expired CDC artifacts.** The CDC Qualification artifacts for #1396 and #1406 have expired, so 2
  removals stay at "unit test in the CDC controller lane, name unknown".
- **Missing job logs.** Two job logs return `BlobNotFound`.
- **Possibly unseen jobs.** About 20 job-listing calls returned HTTP 502 during collection. Every
  failed or cancelled attempt was re-queried afterwards, and the remaining attempts without job rows
  are latest attempts that succeeded. Even so, a non-success job inside an otherwise successful
  attempt could have been missed.
- **External services.** The F7 symptoms were not checked against the services' public status
  history. The log text is the only evidence, and it does not establish an outage.
- **PR-event counts.** PR-event job counts include runs that later pushes superseded, so
  "no same-SHA pass" does not mean the author caused the failure.
