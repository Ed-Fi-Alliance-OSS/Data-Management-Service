# Data-Management-Service CI/CD: short-term ticket drafts

**Status:** Part A created in Jira on 2026-11-16 (DEVOPS-207 to DEVOPS-213, parent DEVOPS-154). Part B (T3, T4, T7, T11) still DRAFT, waiting on Stephen or an external person. Nothing in the repository was changed.
**Source:** the 20 short-term items of the DEVOPS-157 review (epic DEVOPS-154) and the answers recorded on the DMS CI Review Board during the session with the DMS developer on 2026-11-13 (verdict, priority and comment per item). Facts were re-checked against `main` on 2026-11-16 where noted.
**Companions:** [ci-executive-brief.md](./ci-executive-brief.md), [ci-workflow-review.md](./ci-workflow-review.md), [dockerfile-review.md](./dockerfile-review.md)
**Format:** the same as the Ed-Fi-AdminApp tickets DEVOPS-191 to DEVOPS-199 (Context, Rules, How to gather evidence, How to fix, Acceptance criteria). Each draft also records the session verdict so the ticket carries the team's decision. Ticket bodies do not reference local working files; they point at the Confluence page, the review board and the repository itself.

## 0. Session result and proposed grouping

Session 1 (2026-11-13) covered the 20 short-term items: 17 agreed, 1 already done (S13, with follow-ups), 2 to discuss (S19 with Stephen, S25 with Vinaya or Stephen). Priorities: Must 13 (S1, S2, S3, S8, S9, S10, S16, S17, S18, S20, S21, S22, S24), Should 6 (S6, S7, S11, S13, S19, S23), Could 1 (S25).

Duplicate check in Jira (projects DMS and DEVOPS, 2026-11-16): no open ticket covers CodeQL `build-mode`, the Actions cache, the build-artifact packaging, Dependabot limits, non-root containers, `global.json` or workflow linters. Three open tickets overlap and are linked instead of duplicated: DMS-1661 (contract check at PR time and prerelease alerting), DMS-1686 (release publish re-run recovery), DMS-1653 (publishing token rotation). DMS-1474 (done) is the history of the shared build and the CI budget.

### A. Ready to create: no decision pending from outside the DMS team

Created in Jira on 2026-11-16 as DEVOPS-207 to DEVOPS-213 (parent DEVOPS-154), each with a comment tagging Jesus Flores.

Choices inside these tickets (cadence, license list, Slack channel, which Dockerfiles) belong to the DMS team and have a proposed default, so the ticket can be created and started now.

| # | Proposed ticket | Items | Highest priority | Blocked on |
|---|---|---|---|---|
| T1 ([DEVOPS-207](https://edfi.atlassian.net/browse/DEVOPS-207)) | Release chain: a contract check must not silently skip the DMS publish; poll the feed and verify the package download | S1, S3 | Must | Coordinate with DMS-1661 and DMS-1686 |
| T2 ([DEVOPS-208](https://edfi.atlassian.net/browse/DEVOPS-208)) | Dependabot: compose PRs that pass, PR limit and cadence, `docker` and `github-actions` ecosystems | S2, S9, S10, S7 (ecosystem part) | Must | Team decision on cadence and limits |
| T5 ([DEVOPS-209](https://edfi.atlassian.net/browse/DEVOPS-209)) | CodeQL `build-mode: none` and dependency review in its own job with an explicit policy | S21, S13 | Must | Trial on one PR |
| T6 ([DEVOPS-210](https://edfi.atlassian.net/browse/DEVOPS-210)) | Actions cache under control and faster build-artifact packaging | S22, S23 | Must | Trial on one PR |
| T8 ([DEVOPS-211](https://edfi.atlassian.net/browse/DEVOPS-211)) | Workflow safety nets and linters: retention, concurrency, timeout investigation, `global.json`, `actionlint` and `zizmor` | S6, S8, S24 | Must | Team decision on timeouts (deferred) |
| T9 ([DEVOPS-212](https://edfi.atlassian.net/browse/DEVOPS-212)) | Run the published containers as a non-root user | S11 | Should | Confirm which Dockerfiles are published |
| T10 ([DEVOPS-213](https://edfi.atlassian.net/browse/DEVOPS-213)) | CI visibility: gate summary, failure digest kept current, weekly health report | S16, S17, S18 | Must | Team: Slack channel and the existing scheduled workflow to reuse |

### B. Waiting on Stephen or an external person

These tickets can be created, but at least one step needs an answer from Stephen, Vinaya, the Director or a repository administrator before it can close. The question is written into the acceptance criteria.

| # | Proposed ticket | Items | Highest priority | Blocked on |
|---|---|---|---|---|
| T3 | Pin the shared Ed-Fi-Actions workflow references and `minver-cli` | S7 | Should | Stephen: pinning convention for shared workflows; tags on Ed-Fi-Actions |
| T4 | Finish the publishing environment protection and remove the duplicate secrets (admin) | S20 | Must | Administrator; reviewer list; DMS-1653 |
| T7 | Clean up unused workflows and ghost run histories | S19 | Should | Stephen: whether the patterns are shared with other repos |
| T11 | Tighten the `main` ruleset and confirm the repository Actions policy (admin) | S25 | Could | Vinaya or Stephen |

Suggested Jira summary prefix, as for the other repositories: `Data-Management-Service: <title> (<items>)`, type Task, parent DEVOPS-154. `.github/**` is owned by `@ed-fi-alliance-oss/blackbird-automation-owners` (CODEOWNERS), so every workflow PR needs their review.

Common rules, repeated in every ticket:

* Evidence commands are read-only. Run them first and record the numbers in the ticket before changing anything.
* One pull request per step where practical. Never push to `main`.
* New `uses:` lines are pinned to a commit SHA with a version comment.
* Do not widen permissions beyond what the ticket names. Do not add secrets without approval from the security owner.
* The Pester wiring tests under `eng/ci/tests` are updated in the same PR as the workflow change.

### 0.1 Decisions needed from Stephen or another person

Each row is a question that a ticket cannot close without. The "default" column is what the ticket will do if the answer is "no preference", so the work is not blocked by silence. Steps that do not depend on the answer start right away.

**Decisions from Stephen or an external person**

| Decision | Who | Ticket | What it blocks | Proposed default |
|---|---|---|---|---|
| Pinning convention for shared `Ed-Fi-Actions` reusable workflows: commit SHA with a version comment, or a release tag. Will `Ed-Fi-Actions` publish version tags (today only `latest`)? | Stephen | T3 (S7) | T3 step 1; the same answer closes the question in DEVOPS-194 and DEVOPS-167 | SHA with a version comment, refreshed by the `github-actions` Dependabot ecosystem from T2 |
| Are `after-pullrequest.yml` and `on-issue-opened.yml` an organization pattern used in other repositories? If so, does the fix go in the shared source or here? | Stephen | T7 (S19) | T7 steps 1 and 2 (deleting the two workflows and the `event_file` jobs); steps 3 and 4 (ghost histories, Copilot trigger) can start now | Delete them here; both have 0 runs ever and the `workflow_run` name does not exist |
| `main` ruleset: enable `required_review_thread_resolution`? Enable `dismiss_stale_reviews_on_push` although `require_last_push_approval` is already on? | Vinaya or Stephen | T11 (S25) | T11 entirely | Enable thread resolution; leave stale-review dismissal off |
| Reviewer list for the `publishing` environment, and whether `release-automation` needs reviewers or only a branch policy | Director or team lead, then a repository administrator | T4 (S20) | T4 entirely, and the sequencing of the token rotation in DMS-1653 | Two named developers as reviewers on `publishing`; branch policy only on `release-automation` |
| Apply the environment rules and tag rulesets, remove the repository and organization copies of the four secrets, read the repository Actions policy | Repository administrator (name to confirm) | T4 (S20), T11 (S25) | T4 steps 2 and 3; T11 step 2 | None; only an administrator can do it |

**Decisions inside the DMS team (defaults apply if nobody objects)**

| Decision | Who | Ticket | What it blocks | Proposed default |
|---|---|---|---|---|
| Dependabot cadence and limits: keep weekly or move `nuget` to monthly; `open-pull-requests-limit` values; which option (a, b or c) fixes the compose PRs; who reviews bot PRs weekly | DMS team | T2 (S2, S9, S10) | T2 steps 1 and 2; steps 3 and 4 (docker and github-actions ecosystems) can start now | Weekly, limit 3 for `nuget` and 2 for `docker-compose`, option (a) advisory parity test for bot PRs |
| Split of the release-chain work between this ticket, DMS-1661 (PR-time contract check and alerting) and DMS-1686 (re-run-safe publish) | Owners of DMS-1661 and DMS-1686 | T1 (S1, S3) | Nothing technical; it avoids three PRs touching `Invoke-ContractPublishCheck.ps1` and the publish jobs at once | T1 owns the job graph, the notice and the Docker side; the other two keep their scope |
| `allow-licenses` list for dependency review | DMS team | T5 (S13) | T5 step 3 only; the CodeQL trial and the job split start now | The set of licenses already present in the dependency tree |
| Add `timeout-minutes` after the investigation table is reviewed? | DMS team | T8 (S6) | A follow-up ticket, not T8 itself | None; the session said to leave timeouts out for now |
| Which Dockerfiles are published, and are the source-built CI images distributed anywhere? | DMS team | T9 (S11) | T9 step 2 | The two `Nuget.Dockerfile` files only |
| Slack channel for the weekly health report, and which existing scheduled workflow hosts the job | DMS team | T10 (S18) | T10 step 3; steps 1 and 2 (gate summary, failure digest) start now | The channel the seven scheduled workflows already post to; `scheduled-smoke-test.yml` as host unless the team names another |

Every workflow PR also needs a review from `@ed-fi-alliance-oss/blackbird-automation-owners` (CODEOWNERS for `.github/**`); that is a dependency, not a decision.

Part A below holds the tickets with no external decision (T1, T2, T5, T6, T8, T9, T10); Part B holds the four that wait on Stephen or an external person (T3, T4, T7, T11). Within Part B, T7 steps 3 and 4 and T3 step 2 do not wait.

---

## Part A. Tickets ready to create (no external decision pending)

### T1 (DEVOPS-207). Release chain: a contract check must not silently skip the DMS publish; poll the feed and verify the package download (S1, S3)

#### Context

Part of the Data-Management-Service CI/CD review (DEVOPS-157, epic DEVOPS-154). Repo `Ed-Fi-Alliance-OSS/Data-Management-Service`, default branch `main`. Items S1 and S3; verdict Agree, priority Must for both (session of 2026-11-13).

* Between 2026-10-20 and 2026-11-03, 18 of 19 failed prerelease runs stopped in the contract check: `EdFi.Api.CustomValidation 1.0.0 is already published and documentation differs from what was published`. Because `publish-package-dms` `needs` the three contract publish jobs, the DMS package and the Docker publish were **skipped**, not failed, so the merge produced no prerelease and nobody was told. Docker Hub has no tag for any of the 18 affected `dms-pre` versions. PR #1418 (2026-10-29) restored the published doc comment and the prerelease has been green since; the wiring that turns one contract refusal into a silent missing prerelease is still there.
* DMS-1661 (open) proposes to run the contract check at PR time and to alert on prerelease failures. DMS-1686 (open) proposes a re-run-safe publish for the four release publish jobs. This ticket is the third piece: the job graph and the notice. It must not duplicate either.
* The two Docker publish jobs wait a fixed `sleep 20` for the package to appear on the feed, then build `Nuget.Dockerfile`, which downloads the package with `wget` and no checksum. The publish job already computes the package SHA-256 for the SLSA subject; nothing verifies it on the consuming side.
* Session note on S1: "Work on this item." Session note on S3: none; agreed, Must.

#### How to gather evidence

```bash
export R=Ed-Fi-Alliance-OSS/Data-Management-Service

# S1: Reliability
gh run list -R $R --workflow on-prerelease.yml --limit 100 --json conclusion -q 'group_by(.conclusion)|map("\(.[0].conclusion) \(length)")|.[]'
id=$(gh run list -R $R --workflow on-prerelease.yml --status failure --limit 1 --json databaseId -q '.[0].databaseId'); gh run view $id -R $R --log-failed 2>/dev/null | grep -m2 "already published"
grep -n "already published" eng/verification/Invoke-ContractPublishCheck.ps1
grep -n -A6 "^  publish-package-dms:" .github/workflows/on-prerelease.yml | grep -A5 needs
gh pr view 1418 -R $R --json title,mergedAt -q '"\(.mergedAt[:10]) \(.title)"'
curl -s "https://hub.docker.com/v2/repositories/edfialliance/ed-fi-api/tags?page_size=5&ordering=last_updated" | jq -r '.results[]|"\(.last_updated[:10]) \(.name)"'

# S3: Reliability
grep -n "sleep 20" .github/workflows/on-prerelease.yml
grep -n "wget\|sha256\|curl" src/dms/Nuget.Dockerfile src/config/Nuget.Dockerfile
grep -n -B3 -A3 "sha256\|hash" .github/workflows/on-prerelease.yml | grep -n "Get-FileHash\|sha256" | head -5
```

Values found at review time: 19 failures of 90 prerelease runs in 30 days, 18 of them the contract refusal; `sleep 20` at `on-prerelease.yml` lines 1608 and 2415; `wget` with no checksum in both `Nuget.Dockerfile` files; all prerelease runs since 2026-11-05 green.

#### How to fix

1. **S1, job graph:** make `publish-package-dms` and `docker-publish-dms` depend on the contract **build** jobs but not on the contract **publish** outcome, or give the contract publish jobs `continue-on-error` with an explicit output that the DMS publish reads. A contract refusal then fails its own job visibly and the DMS prerelease still ships.
2. **S1, notice:** in `Invoke-ContractPublishCheck.ps1`, distinguish "already published, same surface" (emit `::notice::` and skip) from "already published, surface differs" (fail that package's job with the current message). Add a Pester case for each branch. Agree with DMS-1661 on where the PR-time check lives so the script has one behavior in both places.
3. **S3, poll:** replace each `sleep 20` with a bounded loop that queries the feed for the exact version (for example 12 tries of 10 s) and fails with a clear message when the version never appears.
4. **S3, verify:** pass the expected SHA-256 as a build argument to `Nuget.Dockerfile` and check it with `sha256sum -c` before extracting. The hash already exists in the publish job. Coordinate with DMS-1686, which compares the feed copy with the build artifact for the re-run case.

#### Acceptance criteria

* A prerelease where a contract package is refused by the feed still publishes the DMS package and image, and the run shows the refusal as a failed or annotated step, never as a skipped DMS publish.
* `Invoke-ContractPublishCheck.ps1` emits a `::notice::` for an already-published, unchanged package and fails only for a changed surface; Pester covers both branches and `eng/ci/tests/ReleasePublishingCredentials.Tests.ps1` still passes.
* No `sleep` in the Docker publish jobs; the poll's timeout and message are visible in the workflow file.
* `Nuget.Dockerfile` refuses a package whose SHA-256 does not match the build argument; a deliberate wrong hash fails the build in a test run.
* The split with DMS-1661 and DMS-1686 is recorded in all three tickets so none of the three implements the others' scope.
* Baseline and after numbers recorded: prerelease conclusions over the last 100 runs, and the list of `dms-pre` versions without a Docker Hub tag.

---

### T2 (DEVOPS-208). Dependabot: compose PRs that pass, PR limit and cadence, `docker` and `github-actions` ecosystems (S2, S9, S10, S7 ecosystem part)

#### Context

Items S2 (Must), S9 (Must), S10 (Must) and the Dependabot part of S7 (Should), all agreed on 2026-11-13.

* `dependabot.yml` watches `nuget` (weekly, cooldown) and `docker-compose` (two directories, grouped by dependency name since PR #1422 on 2026-10-30). The grouping was meant to bump both directories in one PR, but Dependabot still opens one PR per directory ("Bump nginx in /eng/docker-compose", "Bump keycloak/keycloak in /eng/azure-vm/compose"), and those PRs fail the Pester parity test that requires the same pin in both compose directories. Checked 2026-11-16: five compose bot PRs open, #1461 with 17 failed checks and #1448 with 8; #1449 closed by hand as "Already a PR". Before that, 7 of 8 compose PRs were closed unmerged. Each bot push starts up to nine workflows first.
* There is no `open-pull-requests-limit`, so the default of five applies per ecosystem. Session note on S9: "Review periodicity of dependabot. Review behavior from open-pull-requests-limit."
* There is no `docker` ecosystem, so the four Dockerfiles' digest pins are refreshed by hand; the last two bumps were incident-driven. Session note on S10: "Currently CVE fixes are made manually. Re-check Dependabot costs related to the docker group."
* There is no `github-actions` ecosystem, so the 400+ action SHA pins are refreshed by hand (S7).
* Session note on S2: "Work on this item and validate before ticket creation." Validation done on 2026-11-16, above: the grouping from #1422 did not solve it.

#### How to gather evidence

```bash
export R=Ed-Fi-Alliance-OSS/Data-Management-Service

# S2: Reliability
cat .github/dependabot.yml
git log --format='%h %cs %s' -- .github/dependabot.yml | head -3
gh pr list -R $R --author app/dependabot --state all --limit 20 --json number,title,state,mergedAt,createdAt -q '.[]|"\(.createdAt[:10]) #\(.number) \(.state) merged=\(.mergedAt!=null) \(.title[:70])"'
for n in $(gh pr list -R $R --author app/dependabot --state open --json number -q '.[].number'); do echo "#$n: $(gh pr view $n -R $R --json statusCheckRollup -q '[.statusCheckRollup[]|.conclusion // .state]|group_by(.)|map("\(.[0]) \(length)")|join(", ")')"; done
grep -rn "both compose\|Keycloak image" eng/ci/tests/*.ps1 | head -3

# S9: Hygiene
grep -n "open-pull-requests-limit\|cooldown\|interval" -A3 .github/dependabot.yml
gh run list -R $R --limit 300 --json event,headBranch -q '[.[]|select(.headBranch|startswith("dependabot/"))]|length'

# S10: Security
grep -n "package-ecosystem" .github/dependabot.yml
grep -n "^FROM" src/dms/Dockerfile src/config/Dockerfile src/dms/Nuget.Dockerfile src/config/Nuget.Dockerfile
git log --format='%h %cs %s' -S 'sdk:10.0' -- src/dms/Dockerfile | head -3

# S7 (ecosystem part): Security
grep -ho "uses: [^ ]*@[0-9a-f]\{40\}" .github/workflows/*.yml .github/actions/*/action.yml | wc -l
```

#### How to fix

1. **S2:** reproduce why the grouped `docker-compose` entry still yields per-directory PRs (Dependabot groups apply within one `directory`; with `directories` each directory is a separate update job). Options: (a) one `docker-compose` entry per directory with a `group` each and a Pester parity test that is advisory for `dependabot[bot]` PRs, with a follow-up PR that aligns the other directory; (b) keep both pins in a single source (one compose file that both stacks include) so one bump moves both; (c) a small workflow step on bot PRs that copies the bumped pin to the sibling directory and pushes to the bot branch. Pick with the team; record the choice in the ticket. Name who reviews bot PRs weekly so superseded ones are closed on purpose.
2. **S9:** set `open-pull-requests-limit` (3 proposed for `nuget`, 2 for `docker-compose`) and decide the cadence (weekly kept, or move to `monthly` for `nuget` if the lock-file workflow keeps up); keep the cooldown. Observe one cycle and note how many bot runs it started.
3. **S10:** add a `docker` ecosystem entry for `/src/dms` and `/src/config`, weekly, one group, so digest bumps arrive as reviewed PRs that run the existing Hadolint and Scout scans. Record the runner-minutes of the first two bot PRs so the "cost of the docker group" question is answered with numbers, and keep the manual CVE path documented as the fallback.
4. **S7 ecosystem:** add a `github-actions` ecosystem, weekly, one group, so SHA pins move by PR.

#### Acceptance criteria

* The first weekly Dependabot cycle after the change produces compose PRs that pass the DMS CI Gate and the Config CI Gate without manual edits, or the parity test is advisory for bot PRs with the follow-up documented in the ticket.
* `dependabot.yml` declares `open-pull-requests-limit` on every ecosystem and the chosen cadence; the cooldown stays.
* `docker` and `github-actions` ecosystems exist, grouped and weekly; their first PRs ran the Dockerfile scans and the workflow Pester tests, and their runner-minutes are recorded in the ticket.
* A named reviewer for bot PRs is written in the ticket and in the repository's CI document.
* Baseline and after numbers recorded: bot PRs open, closed unmerged and merged over 60 days; workflow runs started by `dependabot/` branches over 300 runs.

---

### T5 (DEVOPS-209). CodeQL `build-mode: none` and dependency review in its own job with an explicit policy (S21, S13)

#### Context

Items S21 (Agree, Must) and S13 (marked Already done, Should, with follow-ups).

* `codeql-analysis.yml` runs `dotnet build` on both solutions under the CodeQL tracer on every PR and push, although the PR workflows compile the same commit. The `Build` step is 38 of the job's 41 minutes (median 2,283 s build, 342 s analysis), 250 PR runs a month, cancelled 26 times. No commit in the workflow's history mentions `build-mode`. CodeQL supports `build-mode: none` for C# since 2.17; the trial must confirm the alert set does not change.
* `dependency-review-action@v5` runs with defaults inside that job, so it waits for the 38-minute build and its result is buried in the CodeQL log. Session note on S13: "Review allow-licenses; we should keep `fail-on-severity = low`. Review to add a comment on PR for visibility." So the severity stays `low`; the follow-ups are the license list and the PR comment.
* Session note on S21: "It seems applicable. Include a research when creating this ticket. If there is a duplicated ticket, do not create a duplicate." Research done 2026-11-16: no ticket in DMS or DEVOPS mentions CodeQL `build-mode` or the CodeQL build time; DMS-1474 (done) covered the PR workflow's shared build and did not touch CodeQL.

#### How to gather evidence

```bash
export R=Ed-Fi-Alliance-OSS/Data-Management-Service

# S21: Speed
grep -n "build-mode\|languages\|dependency-review" .github/workflows/codeql-analysis.yml
git log --oneline -S 'build-mode' -- .github/workflows/codeql-analysis.yml
gh run list -R $R --workflow codeql-analysis.yml --limit 100 --json conclusion,event -q 'group_by(.event+" "+.conclusion)|map("\(.[0].event) \(.[0].conclusion) \(length)")|.[]'
id=$(gh run list -R $R --workflow codeql-analysis.yml --status success --limit 1 --json databaseId -q '.[0].databaseId'); gh run view $id -R $R --json jobs -q '.jobs[].steps[]|select(.name|test("Build|Analyze"))|"\(.name) \(.startedAt) -> \(.completedAt)"'
gh api "repos/$R/code-scanning/alerts?state=open&per_page=100" -q 'length' 2>&1 | head -1   # 403 without security read

# S13: Security
grep -n -B2 -A8 "dependency-review-action" .github/workflows/codeql-analysis.yml
```

#### How to fix

1. **S21 trial:** on one PR, set `build-mode: none` for `csharp` in the `init` step and compare the CodeQL alert set (open alerts before and after, same commit) and the job time. If a rule needs compiled output (generated code, source generators), keep the full build on the weekly schedule only.
2. **S21 split:** move `dependency-review-action` to its own small job in the same workflow (checkout only), so it reports in a minute and can be re-run alone.
3. **S13:** keep `fail-on-severity: low` as the team decided; add `comment-summary-in-pr: always` and `pull-requests: write` on that job only; propose an `allow-licenses` list from the licenses already present in `src/Directory.Packages.props` dependencies and let the team approve it.

#### Acceptance criteria

* The CodeQL job on a PR finishes in under 10 minutes with `build-mode: none`, and the open alert count on the trial commit is the same before and after (numbers in the ticket), or the ticket records which rules differ and the decision taken.
* Dependency review runs in its own job, posts a summary comment on PRs, keeps `fail-on-severity: low`, and the configured license policy is visible in the workflow file.
* No new write permission beyond `pull-requests: write` on the dependency-review job.
* Baseline and after numbers recorded: CodeQL `Build` and `Analyze` step medians over 20 runs, PR runs cancelled per month.

---

### T6 (DEVOPS-210). Actions cache under control and faster build-artifact packaging (S22, S23)

#### Context

Items S22 (Agree, Must) and S23 (Agree, Should).

* The repository cache limit is 10 GB. At review time it held 8.96 GB in 110 entries, 7.12 GB belonging to one PR ref (`refs/pull/1452/merge`: three 2 GB `Linux-buildx` entries from the per-job image-build fallback plus 99 BuildKit blobs). On 2026-11-16 it holds 9.02 GB in 215 entries. When the limit is hit GitHub evicts older entries, including `main`'s 1.1 GB NuGet entry that every job restores, so later jobs start cold.
* Each E2E job carries a fallback that builds the images locally when the GHCR CI image is missing (fork PRs) and saves a buildx layer cache with `actions/cache` keyed on the PR ref. `cache-to` is only written on non-PR refs in `build-ci-docker-images` (lines 1185 and 1201), but the fallback has its own `actions/cache` steps.
* `build-dms-solution` packs its output (about 1.1 GB) with `tar -czf` before `upload-artifact`, which already runs with `compression-level: 0`. `Archive DMS Build Output` takes 127 s on the critical path and each of the 11 consumers spends about 14 s extracting. Session note on S23: "Check and compare PR on this job, look for alternative to pack artifacts faster."
* Session note on S22: "Include a research when creating this ticket. If there is a duplicated ticket, do not create a duplicate." Research done 2026-11-16: no DMS or DEVOPS ticket mentions the Actions cache, buildx caches or the artifact packaging. DEVOPS-197 is the Ed-Fi-AdminApp equivalent and is not a duplicate.

#### How to gather evidence

```bash
export R=Ed-Fi-Alliance-OSS/Data-Management-Service

# S22: Cache
gh api repos/$R/actions/cache/usage -q '"cache \(.active_caches_size_in_bytes/1e9*100|round/100) GB in \(.active_caches_count) entries"'
gh api "repos/$R/actions/caches?per_page=100&sort=size_in_bytes&direction=desc" -q '.actions_caches[:8][]|"\(.size_in_bytes/1e6|round) MB \(.ref) \(.key[:50])"'
grep -n "actions/cache@" .github/workflows/on-dms-pullrequest.yml | wc -l
grep -n "Linux-buildx\|cache-to" .github/workflows/on-dms-pullrequest.yml | head

# S23: Speed
grep -n -B2 -A12 "Archive DMS Build Output" .github/workflows/on-dms-pullrequest.yml | grep -n "tar\|compression-level\|run:"
grep -c "dms-build-output" .github/workflows/on-dms-pullrequest.yml
id=$(gh run list -R $R --workflow on-dms-pullrequest.yml --status success --event pull_request --limit 1 --json databaseId -q '.[0].databaseId'); gh run view $id -R $R --json jobs -q '.jobs[].steps[]|select(.name|test("Archive DMS Build Output|Extract"))|"\(.name) \(.startedAt) -> \(.completedAt)"'
gh api "repos/$R/actions/runs/$id/artifacts" -q '.artifacts[]|select(.name=="dms-build-output")|"\(.name) \(.size_in_bytes/1e6|round) MB"'
```

#### How to fix

1. **S22:** make the fallback restore-only on pull requests (`actions/cache/restore`) and save only on `main`, or remove the `actions/cache` steps from the fallback and rely on the GHCR registry cache that `build-ci-docker-images` already writes. Add a Pester test that fails when a job saves a buildx cache on a PR ref. Delete the current PR-ref entries once (`gh cache delete`) and record the usage before and after.
2. **S23 trial:** on one PR, replace `tar -czf` with `tar -cf` (no compression) and, as the alternative, `tar -I 'zstd -1 -T0' -cf`; keep `compression-level: 0` on the upload. Compare the archive step, the artifact size, the upload time and one consumer's extract time against a baseline run of the same commit. Pick the variant with the lowest critical-path time; if upload bandwidth becomes the limit, `zstd` is the middle ground.

#### Acceptance criteria

* No PR-ref `Linux-buildx` entry appears in the cache list after one week; cache usage stays under 6 GB with `main`'s NuGet entry present; the numbers are in the ticket.
* A Pester test guards the rule that PR runs do not write buildx caches.
* The trial table (archive s, artifact MB, upload s, extract s) for gzip, none and zstd on the same commit is in the ticket, and the chosen variant is merged with its reason.
* The DMS PR critical path is measured before and after on the same commit and recorded.

---

### T8 (DEVOPS-211). Workflow safety nets and linters: retention, concurrency, timeout investigation, `global.json`, `actionlint` and `zizmor` (S6, S8, S24)

#### Context

Items S6 (Agree, Should), S8 (Agree, Must), S24 (Agree, Must).

* Only three jobs in the whole repository declare `timeout-minutes`, so 177 jobs inherit the six-hour default while the merge queue gives up after four hours. Session note on S6: "Leave time-out out for now. And better keep retention. Investigate about timeout behavior." So this ticket keeps `retention-days` and `concurrency`, and turns the timeouts into an investigation, not a change.
* 33 artifact uploads have no `retention-days` (90-day default). The two release workflows have no `concurrency` group, so two merges minutes apart run two prereleases that both write the `:pre` Docker tag.
* There is no `global.json`; `setup-dotnet` with `10.0.x` installs whatever the newest 10.0 SDK is on the day, in 71 places. Three jobs pin `ubuntu-24.04` and the weekend build uses the `windows-2025-vs2026` label without a comment. Session note on S8: "Review if we have windows images."
* The repository guards its workflows with 25 Pester files and `Test-WorkflowRefInjection.ps1`, but no generic linter runs. `actionlint` and `zizmor` would have flagged the `@main` references and the missing timeouts. Session note on S24: "Innovation", Must.

#### How to gather evidence

```bash
export R=Ed-Fi-Alliance-OSS/Data-Management-Service

# S6: Hygiene
grep -c "timeout-minutes" .github/workflows/*.yml | grep -v ":0$"
grep -c "^  [a-z0-9_-]*:$" .github/workflows/on-dms-pullrequest.yml .github/workflows/on-prerelease.yml
grep -n "retention-days" .github/workflows/*.yml | wc -l; grep -n "upload-artifact@" .github/workflows/*.yml | wc -l
grep -ln "^concurrency:" .github/workflows/*.yml
gh api repos/$R/rules/branches/main -q '.[]|select(.type=="merge_queue")|.parameters.check_response_timeout_minutes'
gh run list -R $R --limit 300 --json conclusion,createdAt,updatedAt -q '[.[]|select(((.updatedAt|fromdate)-(.createdAt|fromdate))>7200)]|length'   # runs longer than 2 h

# S8: Runners
grep -n "runs-on" .github/workflows/*.yml | grep -v ubuntu-latest
ls global.json 2>&1
grep -c 'dotnet-version: "10.0.x"' .github/workflows/*.yml .github/actions/*/action.yml | grep -v ":0$" | wc -l
gh run list -R $R --workflow dms-weekend-build.yml --limit 6 --json createdAt,conclusion -q '.[]|"\(.createdAt[:10]) \(.conclusion)"'

# S24: Hygiene
grep -rn "actionlint\|zizmor" .github eng 2>/dev/null || echo "no workflow linter"
ls eng/ci/tests/*.Tests.ps1 | wc -l
grep -n "uses:" .github/workflows/powershell-analysis.yml
```

#### How to fix

1. **S6 retention and concurrency:** add `retention-days` (7 proposed; 14 for diagnostics) to the 33 uploads without one, and `concurrency: { group: prerelease, cancel-in-progress: false }` to `on-prerelease.yml` and `on-release.yml`.
2. **S6 timeout investigation (no change):** list the jobs by p90 duration from the last 30 days, check how many runs exceeded two hours and why (hung step, queue wait, real work), and document what a `timeout-minutes` would have done in each case (fail early versus kill real work). Present the table to the team; the decision to add timeouts is a separate ticket if the team agrees.
3. **S8:** add `global.json` with the SDK version and `rollForward: latestPatch`; add a one-line comment to each non-default `runs-on` saying what depends on it. Review whether any job needs Windows images (the weekend build is the only Windows job) and record the answer.
4. **S24:** add `actionlint` and `zizmor` as steps in `powershell-analysis.yml` (pinned binaries or their official actions pinned by SHA), non-blocking first; fix the backlog; then make them blocking. Keep the Pester guards for the repository-specific rules.

#### Acceptance criteria

* Every `upload-artifact` step declares `retention-days`; `on-prerelease.yml` and `on-release.yml` declare a `concurrency` group without cancel; a Pester test guards both rules.
* The timeout investigation table (job, p90, runs over 2 h, cause, what a timeout would have done) is in the ticket and was reviewed by the team; no `timeout-minutes` was added by this ticket.
* `global.json` exists with `rollForward: latestPatch`, local `dotnet --version` matches CI, and every non-default `runs-on` has a comment; the Windows image question is answered in the ticket.
* `actionlint` and `zizmor` run on every PR in blocking mode with zero findings, or the remaining findings are listed in the ticket with an owner.

---

### T9 (DEVOPS-212). Run the published containers as a non-root user (S11)

#### Context

Item S11, verdict Agree, priority Should. Session note: "Review which containers are. As not all containers are published. Let's focus on published docker containers. E2E is not applicable."

* None of the four Dockerfiles has a `USER` instruction, so the services run as root inside the container. Clusters that enforce `runAsNonRoot` or restricted Pod Security Standards reject the image.
* Published images: `on-prerelease.yml` publishes to Docker Hub from `src/dms/Nuget.Dockerfile` and `src/config/Nuget.Dockerfile` (the `file: Nuget.Dockerfile` jobs). `src/dms/Dockerfile` and `src/config/Dockerfile` build the CI images pushed to GHCR for the E2E lanes and the pre-image tests; per the session they are out of scope unless the team confirms they are also distributed.
* DMS-1291 (in QA) adds multi-arch publishing from the same Dockerfiles; the non-root change should land after it or in coordination.

#### How to gather evidence

```bash
export R=Ed-Fi-Alliance-OSS/Data-Management-Service

# S11: Security
grep -n "USER\|adduser\|chown" src/dms/Dockerfile src/config/Dockerfile src/dms/Nuget.Dockerfile src/config/Nuget.Dockerfile || echo "no USER instruction"
grep -n "file:.*Dockerfile" .github/workflows/on-prerelease.yml
grep -n "run.sh\|ENTRYPOINT\|CMD" src/dms/Nuget.Dockerfile src/config/Nuget.Dockerfile | head -6
docker run --rm edfialliance/ed-fi-api:pre id 2>/dev/null || echo "(pull the image to check the runtime uid)"
docker run --rm edfialliance/ed-fi-config-service:pre id 2>/dev/null || echo "(pull the image to check the runtime uid)"
```

#### How to fix

1. Confirm with the team the list of published Dockerfiles (the two `Nuget.Dockerfile` files) and whether the source Dockerfiles are distributed anywhere; record it in the ticket.
2. In each published Dockerfile's final stage: `adduser -D -u 10001 app`, `COPY --chown=app:app` for the publish output, make the entrypoint script's writable paths owned by that user, and `USER app`. Keep the listening port above 1024 or grant the capability explicitly.
3. Run the pre-image test workflow (`scheduled-pre-image-test.yml`) on the candidate image to catch file-permission assumptions in `run.sh`; coordinate with DMS-1291 for the multi-arch build.

#### Acceptance criteria

* `docker run --rm <image> id` on both published images returns a non-zero uid, and the Dockerfiles contain `USER`.
* The pre-image test workflow passes on the non-root images; the E2E lanes are unchanged.
* The ticket records which Dockerfiles are published and the decision for the CI-only ones.
* The images pass a `runAsNonRoot: true` pod spec in a local Kubernetes smoke test (kind or minikube), noted in the ticket.

---

### T10 (DEVOPS-213). CI visibility: gate summary, failure digest kept current, weekly health report (S16, S17, S18)

#### Context

Items S16, S17 and S18, all Agree, all Must.

* Each test lane writes fixture and test timing files, but `dms-ci-gate` only aggregates pass/fail. A reviewer who wants to know which lane was slow or what the run cost has to open 49 jobs. Session note on S16: "Work on a CI gate with a file as an artifact and group runtimes." So the summary is also written to a file uploaded as an artifact, with runtimes grouped by lane family.
* When a DMS PR fails, 84 of 142 failed runs showed only "Process completed with exit code 1" at the `##[error]` level; the cause was deeper in one of 49 logs. Session note on S17: "Do not keep errored comments. Update comment with the latest state and the reason." So there is one digest comment per PR, edited in place, and it is updated (or marked resolved) when the gate goes green.
* The numbers in the review were computed once with `gh`. Session note on S18: "There is an existing workflow and the job there... Send status for visibility on Slack or discuss with team." So the weekly report reuses an existing scheduled workflow (the team names it) and posts to Slack as the seven scheduled workflows already do, rather than adding a new workflow.

#### How to gather evidence

```bash
export R=Ed-Fi-Alliance-OSS/Data-Management-Service

# S16: Innovation
grep -n "GITHUB_STEP_SUMMARY" .github/workflows/on-dms-pullrequest.yml | wc -l
grep -n "test-timings\|timing" .github/workflows/on-dms-pullrequest.yml | head -5
grep -n -A12 "^  dms-ci-gate:" .github/workflows/on-dms-pullrequest.yml | head -20

# S17: Innovation
ids=$(gh run list -R $R --workflow on-dms-pullrequest.yml --status failure --limit 5 --json databaseId -q '.[].databaseId'); for id in $(echo $ids); do echo "== $id"; gh run view $id -R $R --log-failed 2>/dev/null | grep -m3 "##\[error\]"; done

# S18: Innovation
gh run list -R $R --workflow on-dms-pullrequest.yml --event pull_request --status success --limit 100 --json createdAt,updatedAt -q '[.[]|((.updatedAt|fromdate)-(.createdAt|fromdate))/60]|sort|"median \(.[length/2|floor]|.*10|round/10) min, p90 \(.[length*0.9|floor]|.*10|round/10) min over \(length) runs"'
gh api repos/$R/actions/cache/usage -q '"cache \(.active_caches_size_in_bytes/1e9*100|round/100) GB"'
gh api "repos/$R/actions/runs?per_page=100&status=queued" -q '"queued now: \(.total_count)"'
grep -ln "schedule:" .github/workflows/*.yml; grep -ln "slackapi" .github/workflows/*.yml
```

#### How to fix

1. **S16:** in `dms-ci-gate`, call the Actions API for the run's jobs (`GITHUB_TOKEN` can read them), compute per-job duration and the runner-minute total, group by lane family (build, unit, integration, E2E, CDC, packages), and write a short table to `$GITHUB_STEP_SUMMARY` and to a `ci-run-summary.json` uploaded as an artifact. Do the same in `config-ci-gate`.
2. **S17:** add a non-blocking job on gate failure that pulls the failed job logs, extracts the first assertion, compiler error or Pester failure line with a small script (a model with a fixed prompt is optional and advisory), and creates or updates **one** PR comment identified by a marker. On a green gate the same job edits the comment to "resolved at <sha>" instead of leaving a stale error. Measure whether reviewers use it before expanding.
3. **S18:** add a job to the existing scheduled workflow the team names, running the queries above weekly (median and p90 per workflow, runner-minutes by event, failure causes by class, queue p99, cache usage), writing the table to the job summary and posting a short Slack message with the link, using the same Slack action and secret the other scheduled workflows use. The baseline is the DEVOPS-157 review on Confluence.

#### Acceptance criteria

* Every DMS and Config PR run shows, on the gate job, a summary with the five slowest lanes, the failed jobs, the runner-minutes of the run and per-family runtimes, and the same data is downloadable as an artifact.
* A failed PR carries exactly one digest comment that names the first real failure line and its job; a later green run updates that comment; no stale error comment remains.
* The weekly report runs from the existing scheduled workflow, posts to the agreed Slack channel, and its first three editions are linked in the ticket.
* No new secrets; the digest and report jobs use `GITHUB_TOKEN` with `pull-requests: write` only where the comment is written.

---

## Part B. Tickets waiting on Stephen or an external person

### T3. Pin the shared Ed-Fi-Actions workflow references and `minver-cli` (S7)

#### Context

Item S7, verdict Agree, priority Should. Session note: "Following practices use SHA. Let's discuss with Stephen."

* Three `uses:` lines point at `@main` of the shared `Ed-Fi-Actions` repository: `repository-scanner.yml` in both PR workflows (the required BIDI check) and `powershell-analyzer.yml`. A push to that repository changes a required check here without a review in this repository. Every other third-party action is pinned to a SHA; the OpenSSF Scorecard reports 8/10 on pinning because of these.
* `minver-cli` is installed in the release path without a version (`dotnet minver` in `analyze-docker-images.yml` and the version steps of `on-prerelease.yml`), so a new MinVer release can change the computed version under an unchanged commit.
* `Ed-Fi-Actions` has the tag `latest` only, so there is no release tag to pin to today. The same question is open in DEVOPS-194 (Ed-Fi-AdminApp) and DEVOPS-167 (ODS-Admin-API).

#### How to gather evidence

```bash
export R=Ed-Fi-Alliance-OSS/Data-Management-Service

# S7: Security
grep -n "Ed-Fi-Actions/.*@main" .github/workflows/*.yml
grep -n "minver" .github/workflows/on-prerelease.yml .github/workflows/analyze-docker-images.yml | head -5
grep -ho "uses: [^ ]*@[0-9a-f]\{40\}" .github/workflows/*.yml .github/actions/*/action.yml | wc -l
gh api repos/Ed-Fi-Alliance-OSS/Ed-Fi-Actions/tags -q '.[].name'
gh api repos/Ed-Fi-Alliance-OSS/Ed-Fi-Actions/commits/main -q '.sha'
```

#### How to fix

1. Agree the convention with Stephen (see acceptance criteria), then pin the three references to a commit SHA with a version comment, or to a release tag if `Ed-Fi-Actions` publishes one. The `github-actions` Dependabot ecosystem from T2 keeps the pin fresh.
2. Pin `minver-cli` with `dotnet tool install --global minver-cli --version x.y.z` (read the version from a recent green run) in every place it is installed.
3. Confirm the Scorecard pinning score rises on the next weekly run.

#### Acceptance criteria

* Confirmed with Stephen: the pinning convention for shared Ed-Fi-Actions workflows (commit SHA with a version comment, or release tag), and whether the `Ed-Fi-Actions` owners will publish version tags. The answer is recorded in this ticket and reused by DEVOPS-194 and DEVOPS-167.
* No `@main` reference remains in `.github/workflows`; the Pester test that guards action pinning covers reusable workflow references too.
* `minver-cli` is installed with an explicit version everywhere; the version is the same in all places.
* Scorecard `Pinned-Dependencies` reaches 10/10 on the next run, or the remaining findings are listed in the ticket.

---

### T4. Finish the publishing environment protection and remove the duplicate secrets (S20, admin)

#### Context

Item S20, verdict Agree, priority Must. Session note: "Let's keep this work."

* DMS-1647, DMS-1648 and DMS-1649 (done) moved the publishing tokens into the `publishing` and `release-automation` environments and wrote `docs/CI-PUBLISHING-CREDENTIALS.md` with the remaining setup steps. On 2026-11-16 the three environments (`copilot`, `publishing`, `release-automation`) still have 0 protection rules and a `null` deployment branch policy, and the rulesets `cla`, `main`, `patch` target branches only, so any workflow in any branch that names the environment can read the tokens. The repository and organization copies of the four secrets were not confirmed removed (403 for non-admins).
* DMS-1653 (in progress) rotates the Azure Artifacts and Docker Hub tokens after the CI fixes; the rotation should land after the environments are protected, so the new tokens never exist outside them.
* This is an administrator task: settings, not code.

#### How to gather evidence

```bash
export R=Ed-Fi-Alliance-OSS/Data-Management-Service

# S20: Security
gh api repos/$R/environments -q '.environments[]|"\(.name) rules=\(.protection_rules|length) policy=\(.deployment_branch_policy)"'
gh api repos/$R/rulesets -q '.[]|"\(.name) \(.target) \(.enforcement)"'
grep -n "^## \|^### \|DMS-164" docs/CI-PUBLISHING-CREDENTIALS.md | head -30
grep -n "environment:" .github/workflows/on-prerelease.yml .github/workflows/on-release.yml .github/workflows/on-*-merge-or-tag.yml | head
gh secret list -R $R 2>&1 | head -2                      # 403 without admin
gh api repos/$R/actions/organization-secrets 2>&1 | head -c 120   # 403 without admin
```

#### How to fix

1. Decide the reviewer list for `publishing` (names in the ticket) and whether `release-automation` needs reviewers or only a branch policy.
2. An administrator completes the steps of `docs/CI-PUBLISHING-CREDENTIALS.md`: required reviewers and branch/tag policy on `publishing`, branch policy on `release-automation`, the two tag rulesets.
3. Remove the repository and organization copies of the four secrets; then run the document's verification checks 1, 5, 6 and 7 and paste the results in the ticket.
4. Sequence DMS-1653 after step 3 so the rotated tokens are created only inside the environments.

#### Acceptance criteria

* `gh api repos/$R/environments` shows `publishing` with required reviewers and a branch/tag policy, and `release-automation` with a branch policy; the two tag rulesets exist with `active` enforcement.
* The named reviewer approved at least one prerelease publish after the change, and the run shows the approval.
* An administrator confirms in the ticket that no repository-level or organization-level copy of the four publishing secrets remains.
* Verification checks 1, 5, 6 and 7 of the credentials document pass and their output is in the ticket.
* DMS-1653 references this ticket and its rotation happened after the environments were protected.

---

### T7. Clean up unused workflows and ghost run histories (S19)

#### Context

Item S19, verdict Needs discussion, priority Should. Session note: "Validate with Stephen as it seems those workflows exist on other repos as well." The question is whether `after-pullrequest.yml` and `on-issue-opened.yml` follow an organization-wide pattern, in which case the fix may belong in the shared template rather than here.

* `after-pullrequest.yml` listens with `workflow_run` for a workflow named "On Pull Request", but the two PR workflows are named "On DMS Pull Request" and "On Config Pull Request", so it has never run (0 runs ever). The two `event_file` jobs in the PR workflows exist only to feed it (24 and 10 `needs` entries).
* `on-issue-opened.yml` ("Issue Management") has 0 runs ever.
* Four workflows still appear in the Actions list although their files were deleted: `EdFi.Dms.Populated.Template.PostgreSQL.yml`, `backfill-document-cache-admin.yml`, `config-database-topology-matrix.yml`, `on-mssql-template-pullrequest.yml` (confirmed absent on 2026-11-16). GitHub keeps a workflow registered until its run history is deleted.
* `copilot-setup-steps.yml` filters by `paths`, and GitHub ignores path filters on tag pushes; every merge pushes a prerelease tag, so it ran 602 times in 30 days.

#### How to gather evidence

```bash
export R=Ed-Fi-Alliance-OSS/Data-Management-Service

# S19: Hygiene
gh api repos/$R/actions/workflows --paginate -q '.workflows[]|"\(.state) \(.path)"' | sort
for f in EdFi.Dms.Populated.Template.PostgreSQL.yml backfill-document-cache-admin.yml config-database-topology-matrix.yml on-mssql-template-pullrequest.yml; do test -f .github/workflows/$f && echo "present $f" || echo "absent  $f"; done
gh api "repos/$R/actions/workflows/after-pullrequest.yml/runs?per_page=1" -q '"after-pullrequest runs ever: \(.total_count)"'
gh api "repos/$R/actions/workflows/on-issue-opened.yml/runs?per_page=1" -q '"on-issue-opened runs ever: \(.total_count)"'
grep -n -A2 "workflows:" .github/workflows/after-pullrequest.yml; grep -n "^name:" .github/workflows/on-dms-pullrequest.yml .github/workflows/on-config-pullrequest.yml
grep -c "event_file" .github/workflows/on-dms-pullrequest.yml .github/workflows/on-config-pullrequest.yml
gh run list -R $R --workflow copilot-setup-steps.yml --limit 100 --json event -q 'group_by(.event)|map("\(.[0].event) \(length)")|.[]'
# Are the same files in the sibling repositories?
for r in Ed-Fi-Alliance-OSS/Ed-Fi-ODS-AdminApi Ed-Fi-Alliance-OSS/Ed-Fi-AdminApp Ed-Fi-Alliance-OSS/Ed-Fi-Actions; do echo "$r: $(gh api repos/$r/contents/.github/workflows -q '[.[].name|select(test("after-pullrequest|on-issue-opened"))]|join(" ")' 2>/dev/null)"; done
```

#### How to fix

1. Ask Stephen the question in the acceptance criteria. If the two workflows are an organization convention, open the fix (correct `workflow_run` name, or removal) in the shared source and apply it here as a consumer.
2. Otherwise delete `after-pullrequest.yml`, `on-issue-opened.yml` and the two `event_file` jobs with their `needs` references, and update the Pester wiring tests in the same PR.
3. Delete the run history of the four ghost workflows (Actions UI, "Delete all workflow runs"; the entry disappears when no run is left).
4. Add `branches: [main]` under `push` in `copilot-setup-steps.yml` so tag pushes do not trigger it.

#### Acceptance criteria

* Confirmed with Stephen: whether `after-pullrequest.yml` and `on-issue-opened.yml` are an organization pattern used in other repositories, and where the fix belongs. The answer is recorded in this ticket.
* `after-pullrequest.yml`, `on-issue-opened.yml` and the `event_file` jobs are either removed or wired to an existing workflow name, with the Pester tests green.
* The Actions workflow list contains only workflows whose files exist.
* `copilot-setup-steps.yml` runs on pushes to `main` only; the next prerelease tag does not start it.

---

### T11. Tighten the `main` ruleset and confirm the repository Actions policy (S25, admin)

#### Context

Item S25, verdict Needs discussion, priority Could. Session note: "Let's review with Vinaya or Stephen."

* The `main` ruleset requires one approving review, code-owner review, the DMS CI Gate and Config CI Gate checks, and already has `require_last_push_approval: true`, which covers most of the "stale approval" risk. `dismiss_stale_reviews_on_push` and `required_review_thread_resolution` are both `false`, so a PR with open review threads can merge.
* The repository Actions policy (default `GITHUB_TOKEN` permissions, allowed actions, whether Actions may create or approve PRs) returns 403 to non-admins, so it is unverified. The same question is open in DEVOPS-199 (Ed-Fi-AdminApp).

#### How to gather evidence

```bash
export R=Ed-Fi-Alliance-OSS/Data-Management-Service

# S25: Governance
gh api repos/$R/rulesets -q '.[]|"\(.id) \(.name) \(.target)"'
gh api repos/$R/rulesets/571410 -q '.rules[]|select(.type=="pull_request")|.parameters|{dismiss_stale_reviews_on_push,required_review_thread_resolution,require_last_push_approval,required_approving_review_count}'
gh api repos/$R/rulesets/571410 -q '[.rules[]|select(.type=="required_status_checks")|.parameters.required_status_checks[].context]'
gh api repos/$R/actions/permissions 2>&1 | head -c 120; echo          # 403 without admin
gh api repos/$R/actions/permissions/workflow 2>&1 | head -c 120; echo # 403 without admin
```

#### How to fix

1. Review with Vinaya or Stephen whether `required_review_thread_resolution` should be on (recommended) and whether `dismiss_stale_reviews_on_push` adds anything given `require_last_push_approval`.
2. An administrator applies the agreed flags on the `main` ruleset and reads the Actions policy: default token permissions, allowed actions, "Allow GitHub Actions to create and approve pull requests".
3. Record the policy in `SECURITY.md` so the next review does not need admin access to answer the question.

#### Acceptance criteria

* Confirmed with Vinaya or Stephen: the decision on `required_review_thread_resolution` and `dismiss_stale_reviews_on_push`, recorded in the ticket, and the flags on the `main` ruleset match it.
* An administrator has pasted the repository Actions policy (default token permissions, allowed actions, PR approval setting) in the ticket, and `SECURITY.md` states it.
* The same answers are shared with DEVOPS-199 so the organization applies one convention.
