# Data-Management-Service CI/CD Review: Findings and Proposal

**For:** the DMS development team and the Director of Software Engineering
**Status:** DRAFT for discussion. Proposals only. Nothing in the repository was changed.
**Context:** Jira DEVOPS-157, part of epic DEVOPS-154 (improve GitHub Actions workflows across Ed-Fi repositories). The ODS-Admin-API and Ed-Fi-AdminApp reviews (DEVOPS-155, DEVOPS-156) used this repository as the example to follow. This review looks at it as the subject.
**Technical companions:** [ci-workflow-review.md](./ci-workflow-review.md), [ci-workflow-review-detailed.md](./ci-workflow-review-detailed.md), [dockerfile-review.md](./dockerfile-review.md), [workflow-structure-review.md](./workflow-structure-review.md)

---

## 1. Summary

The pipeline is complete, well engineered and well tested; it is also the most expensive one in the group to run and to change. The problems are not missing tools. They are **duplicated work, size, and a few settings that were designed but not finished**:

1. **Every pull request runs a very large set of checks, and every merge runs the same set again** in the merge queue. The wait for a green pull request is close to an hour, and most of the machine time the repository uses goes to those two events.
2. **The same code is compiled several times per change** by different workflows: once for the tests, once again for the security scanner, several times in the Configuration Service workflow, and again at release time. The scanner alone spends almost all of its time rebuilding.
3. **The release chain sometimes refuses to publish, silently.** When a shared "contract" package changes without a version bump, a safety check stops the run, and the main product package and image are not published for that merge. This was the most common release failure in the period.
4. **Security controls exist on paper but one key setting is pending.** The tokens that publish packages and images were moved to protected "environments", but the protection itself (reviewers, allowed branches, tag rules) has not been configured yet, so the move protects nothing until it is finished. Containers also run as the root user, and base images are refreshed by hand.
5. **Housekeeping debt accumulates:** workflows that can never run, registered workflows whose files were deleted, a workflow that starts on every prerelease tag by accident, almost no job timeouts, and a shared cache that is nearly full because of one pull request.
6. **The workflow code itself is large** (about 15,000 lines, changed in 62 commits over 90 days) because the same job is copied many times. Review and maintenance effort, not minutes, is the real cost.

Most of the highest-value fixes are small, and many are deletions. We propose to start with those, measure the effect with the numbers already collected, and only then invest in the larger refactoring.

## 2. How to read the proposal

Each item below follows the same pattern: **what we saw -> why it matters -> what we propose -> expected result**. Results are described qualitatively; real numbers are in the technical companions and will be measured again after each step (section 7).

Effort is relative: **S** = small, **M** = medium, **L** = large. IDs refer to the roadmap in `ci-workflow-review.md`. Every proposal names what it costs in review and maintenance, not only what it saves, because the repository is mature and adding is rarely the right move.

---

## 3. Short term (quick wins, low risk)

### 3.1 Remove what no longer works or never ran (S19)

| | |
|---|---|
| **What we saw** | One workflow waits for another workflow by a name that does not exist, so it has never run; two jobs in the main workflows exist only to feed it. Another workflow has never run either. Four workflows still appear in the Actions list although their files were deleted months ago. A setup workflow starts on every prerelease tag because of how GitHub treats path filters on tags (hundreds of pointless runs). |
| **Why it matters** | Dead code in the two largest files makes every review slower and misleads newcomers; ghost entries mix old history into the Actions list. |
| **Proposal** | Delete the dead workflows and jobs, remove the ghost run histories, and limit the setup workflow to the main branch. |
| **Expected result** | A cleaner Actions page and shorter workflow files, with nothing to maintain afterwards. This is the quick win to clean up unused workflows. |

### 3.2 Stop the duplicate builds (S21, M1, S23)

| | |
|---|---|
| **What we saw** | The security scanner rebuilds both products under its own instrumentation on every code pull request and takes most of its time doing so. The Configuration Service workflow compiles its product in five separate jobs instead of once. The main workflow compresses a large build artifact in a way that costs minutes on the critical path. |
| **Why it matters** | Machine time is spent on work that was already done a minute earlier in the same pull request, and the longest paths get longer. |
| **Proposal** | Run the scanner in "no build" mode for C# (a supported option), build the Configuration Service once and share the output as the main product already does, and change the artifact packaging. Each is a small change, trialed on one pull request. |
| **Expected result** | Shorter scanner runs, a shorter Configuration Service pull request, and a few minutes off the main critical path, with no loss of coverage if the trial confirms the scanner's results are unchanged. |

### 3.3 Make the release chain fail loudly, not silently (S1, S3)

| | |
|---|---|
| **What we saw** | A safety check correctly refuses to republish a shared package whose contents changed but whose version did not. The way the workflow is wired, that refusal also skips publishing the main product package and image for that merge, and nobody is told. Separately, the image publish waits a fixed 20 seconds for the package to appear and downloads it without verifying it. |
| **Why it matters** | A merge that looks finished has produced no prerelease. People discover it later, or not at all. A fixed wait is a guess; an unverified download weakens the signed-provenance story. |
| **Proposal** | Let the check skip the shared package (with a visible notice) instead of failing the run, bump the package version that currently needs it, replace the fixed wait with a bounded poll, and verify the download against the hash the build already computes. |
| **Expected result** | Every merge produces its prerelease, failures that remain are real, and the published image provably contains the attested package. |

### 3.4 Finish the credential protection and the hardening basics (S20, S10, S11, S7)

| | |
|---|---|
| **What we saw** | The publishing tokens were moved to GitHub "environments", with a written setup plan, but the environments have no reviewers, no allowed-branch rules and no tag rules yet. Containers run as root. Base images are refreshed by hand, and the last two refreshes were triggered by incidents. The required code-scanning check and the PowerShell analyzer follow a moving branch of a shared repository. |
| **Why it matters** | Until the environments are protected, any branch that names them can read the tokens. Root containers fail common deployment policies. Hand-refreshed images drift. A moving reference changes a required check without a review in this repository. |
| **Proposal** | Complete the four setup steps in the existing document (an administrator task), add a non-root user to the four Dockerfiles, add automatic base-image updates, and pin the shared references. |
| **Expected result** | Publishing needs an approval from a named reviewer; images pass non-root policies; base images are refreshed by pull request; required checks change only through a reviewed update. |

### 3.5 Safety nets and bot hygiene (S6, S22, S2, S8, S9)

| | |
|---|---|
| **What we saw** | Almost no job has a timeout (the default is six hours; the merge queue waits four). The shared cache is nearly full because one pull request wrote several gigabytes. Dependency-bot pull requests for container images fail a consistency test and are closed by hand. The .NET SDK version is not pinned in a file. |
| **Why it matters** | A hung job blocks a merge-queue entry for hours; a full cache slows every job; bot noise hides real failures; an unpinned SDK can change under a green build. |
| **Proposal** | Add timeouts based on the measured durations, stop writing image caches from pull requests, group the two compose directories so one bot pull request updates both, pin the SDK, and limit concurrent bot pull requests. |
| **Expected result** | Bounded worst cases, a cache that serves the main branch, bot pull requests that pass on their own. |

### 3.6 Visibility (S16, S17, S18, S24)

| | |
|---|---|
| **What we saw** | The repository already produces excellent per-lane test timing summaries, but there is no per-run view of cost or of why a run failed; failures are spread over nearly fifty jobs and long logs. |
| **Why it matters** | Without a trend, nobody can tell whether the pipeline is getting faster or slower, and reading a failure takes longer than it should. |
| **Proposal** | Add a one-screen run summary on the gate job, an advisory failure digest posted to the pull request, a weekly CI health report built from the same queries used in this review, and standard workflow linters. |
| **Expected result** | Cost and reliability become visible week to week, which is also how we will know the later items worked. |

---

## 4. Mid term (weeks to a quarter)

### 4.1 Shorter pull requests (M2, M3, M4, M22)

| | |
|---|---|
| **What we saw** | The longest single job is a SQL Server integration lane that runs unsharded while its sibling lane is split in four; unit tests take over twenty minutes with coverage on every pull request; two script-based suites run for ten to twenty minutes on most pull requests; static analyzers roughly double compile time in lanes that only need binaries. |
| **Why it matters** | These four items set the critical path. Everything else finishes earlier and waits. |
| **Proposal** | Shard the long lane like its sibling, profile and split the unit-test job (coverage on the main branch only, if the team agrees), move most of the script suites to the nightly with a short subset on pull requests, and switch analyzers off in the lanes that only produce binaries after a measured trial. |
| **Expected result** | A noticeably shorter time to a green pull request, and the same reduction in the merge queue since it runs the same workflow. |

### 4.2 Stop paying for work nobody consumes (M5, M23)

| | |
|---|---|
| **What we saw** | Database template packages are built, signed and published on every merge to the main branch, in eight variants. A nightly qualification suite runs for almost two hours; a weekend build runs on a Windows machine and failed three of the last four times on a Windows-only path issue; results go to a chat channel only. |
| **Why it matters** | Publishing has a cost in machine time and in signing, and a published artifact nobody uses is still something to support. Scheduled failures that only reach a chat channel are easy to miss. |
| **Proposal** | Publish templates on release tags and on demand unless someone consumes the intermediate versions; give every scheduled lane an owner, a purpose line and a one-line result summary; decide the frequency of the nightly qualification and the future of the Windows build. |
| **Expected result** | Fewer published artifacts to support, scheduled lanes that someone reads, and a clear decision on each cron. |

### 4.3 Make the workflow files smaller than the products they test (M6, M18, M19, M21)

| | |
|---|---|
| **What we saw** | The same end-to-end job is copied eight times inside the main workflow and three times in the Configuration Service one; the release workflow repeats a five-job chain eight times; five template workflows differ by ten lines. There is no document that explains which workflow runs when. |
| **Why it matters** | A change that is conceptually one line ("add a package family", "add a test variant") is a multi-hundred-line review, and copies drift. This is the main reason the workflows are expensive to maintain. |
| **Proposal** | Extract one reusable end-to-end lane and call it with a matrix, turn the release chain into a matrix over package families, merge the template wrappers, and write a one-page CI map. Do it lane by lane, using the existing timing summaries to prove nothing regressed. |
| **Expected result** | Thousands of lines fewer, one place to fix each behavior, and faster reviews. This is the largest piece of work in the proposal and the one with the most lasting effect. |

### 4.4 Security as a continuous process (M8 to M13)

| | |
|---|---|
| **What we saw** | Scans run when files change; nothing scans the published images or the dependency lock files on a schedule; provenance covers packages but not images; package signatures are not verified; a long-lived personal token still creates releases; there is no written response time for vulnerabilities. |
| **Why it matters** | New advisories appear without any code change. The published image is what customers run. |
| **Proposal** | A nightly dependency and image scan, image attestation, package signature verification, a GitHub App instead of the personal token, and a vulnerability policy with an owner and deadlines. |
| **Expected result** | Advisories are seen within a day, images are verifiable, fewer long-lived secrets. |

### 4.5 Innovation candidates (M14 to M17)

| | |
|---|---|
| **What we saw** | The merge queue builds one entry at a time; end-to-end lanes run even when no feature file changed; the list of lanes is hand-written in dozens of conditions; runner capacity is only tight when several large runs overlap. |
| **Proposal** | Trial a larger queue batch for a week, skip end-to-end shards whose inputs did not change, generate the lane matrix from the change detection, and test larger runners on the two CPU-bound lanes once they are sharded. Each is a measured trial, not a commitment. |
| **Expected result** | Data to decide whether throughput, cost or both improve. |

---

## 5. Long term (quarter and beyond)

- **Release the artifact that was tested (L2, L5).** Today the release compiles the code a third time and builds the published image from a downloaded package. Retagging the image that passed the pull request, and deriving the packages from the same build, is the standard "build once, promote" model. It needs a decision on the release model (a prerelease on every merge, or a promotion step with approval).
- **Decide the Windows build (L1)** and whether arm64 images are wanted.
- **Export the DMS patterns to the shared actions repository (L4, L11).** The sibling repositories were told to copy the gate job, the lock-file automation, the image reuse and the credential model from here. Making them shared pieces, versioned with tags, gives each fix one owner across three repositories and is the start of a paved-road pipeline for Ed-Fi .NET services.
- **Replace the forked provenance generator with GitHub's native attestation (L7)**, which a proof of concept in the sibling repository already explores, and move to short-lived credentials everywhere (L8).
- **Simplify the .NET build definition (L9)**: one shared properties file instead of three copies regenerated by scripts.
- **Preview environments and agent-assisted maintenance (L10, L12)**: deploy a pull request's images to a temporary stack with a link in the pull request; let a scheduled agent triage failures, propose pin and timeout updates and keep the CI document current, since workflow churn is the real recurring cost here.

---

## 6. Why this order

1. **Deletions and settings first.** They cost almost nothing, cannot break a build, and one of them (environment protection) closes the most important open security item.
2. **Then the duplicate builds**, because they are small changes with a visible effect on wait time, and they produce the before/after numbers that justify the rest.
3. **Then the critical-path lanes**, measured one by one, since each is a few days and the merge queue doubles every minute saved.
4. **Structural refactoring after that**, once the numbers show where the remaining time goes and once the team has decided on templates, schedules and the release model.
5. **The release model and the shared repository last**, because they need decisions beyond this repository.

## 7. How we will measure success

Baselines were recorded in this review (30 days to 2026-11-12) and will be compared after each step. Directions only; no target numbers are promised.

| Indicator | Direction we expect |
|---|---|
| Time to a green pull request (median and p90) | Down |
| Machine minutes per pull request and per merge | Down |
| Merges per day through the queue | Up |
| Pull-request failures caused by infrastructure, bots or timing (not code) | Down |
| Merges that produced no prerelease | None |
| Scheduled lanes with an owner and a readable result | All |
| Jobs without a timeout; references to moving branches | None |
| Publishing runs approved by a named reviewer | All |
| Lines of workflow YAML, and lines changed per workflow change | Down |
| Open high and critical security findings, and their age | Down, with an owner for each |

## 8. Questions for the development team

We want their input before any change. Each question has a verification command and the value found in section 9 of `ci-workflow-review.md`.

1. What hurts most day to day: the hour-long pull request, the second hour in the merge queue, reruns, or the release failures?
2. Is it intended that a shared-package version clash stops the whole prerelease? Who bumps a contract package's version?
3. Was the "no build" mode of the security scanner tried? Why does it build both products?
4. Why do unit tests take over twenty minutes, and is coverage needed on every pull request?
5. Can the long SQL Server integration lane be sharded like its sibling? What blocked it?
6. Who consumes the template packages published on every merge?
7. Who owns the environment protection, the tag rules and the removal of the old organization secrets?
8. Is running containers as root a known decision?
9. Prior experiments: larger runners, analyzers off, test sharding, queue batch size, reusable lanes. What happened?
10. Who reads the scheduled lanes' results, and what happened after the recent weekend and nightly failures?
11. Is the Windows build a product requirement?
12. Are arm64 images wanted, and does anyone outside the team consume the `pre` tag?
13. Which Compose overlays are still used?
14. Would the shared actions repository's owners accept the DMS pieces and version them with tags?

## 9. Decisions requested from the Director

1. **Approve** the short-term package (section 3) as the first phase, including the deletions.
2. **Assign an administrator** to finish the environment protection and tag rules (3.4), and **nominate an owner** for security findings with agreed response deadlines (4.4).
3. **Decide the template publish cadence** (every merge, or tags and on demand) and **the frequency and ownership of each scheduled lane** (4.2).
4. **Approve the structural refactoring** (4.3) as a planned effort with a measured, lane-by-lane rollout, since it is the largest item and the one that lowers maintenance cost.
5. **Decide the release model** (prerelease on every merge versus a gated promotion of the tested artifact) and the future of the Windows build and arm64 images (section 5).
6. **Sponsor the shared-templates effort** across Ed-Fi repositories, since DMS is the source of the patterns the other repositories were told to adopt.
7. **Agree on the principle** that self-hosted runners are not used on public repositories.

## 10. Risks and limitations

- Findings are based on read-only access to public data over 30 days. Security alerts, repository secrets and the Actions policy could not be read; those points are questions, not conclusions. Rulesets, environments, caches and releases were readable, so statements about them are facts.
- Time savings are expectations from measured step durations. That is why the scanner change, the artifact packaging, sharding and analyzers start as trials.
- The structural refactoring touches the files that gate every merge. It must go lane by lane with the existing timing summaries and Pester wiring tests as the safety net, and with a documented rollback.
- Some fixes depend on other teams: the shared actions repository, organization-level settings, and consumers of the template packages and the `pre` image tag.
- No change was made to any repository.

---

## Appendix A. Full list of improvement items

IDs, areas and effort match the roadmap in `ci-workflow-review.md`.

### A.1 Short term

| ID | Area | Improvement | Reason | Effort |
|---|---|---|---|---|
| S19 | Hygiene | Delete the never-run workflows and their feeder jobs, remove ghost workflow histories, limit the setup workflow to the main branch | Dead code and ghost entries | S |
| S6 | Hygiene | Job timeouts from measured durations, artifact retention, concurrency on the release workflows | Almost no timeouts; parallel prereleases | S |
| S7 | Security | Pin the shared references and the version tool; add automatic updates for actions | Moving references in required checks | S |
| S20 | Security | Finish the environment protection and tag rules; remove old secret copies | Protection designed but not configured | S (admin) |
| S21 | Speed | Security scanner in "no build" mode for C#; dependency review in its own job | Scanner spends almost all its time rebuilding | S (trial) |
| S22 | Cache | Stop writing image caches from pull requests | Cache nearly full from one pull request | S |
| S23 | Speed | Package the build artifact without gzip | Minutes on the critical path | S (trial) |
| S1 | Reliability | Contract check skips instead of failing the run; bump the package that needs it | Most common release failure; no prerelease for those merges | S |
| S2 | Reliability | Group the two compose directories for the dependency bot; decide who closes superseded bot PRs | Bot pull requests fail a parity test | S |
| S3 | Reliability | Replace the fixed wait with a poll; verify the downloaded package | Timing race; unverified download | S |
| S8 | Runners | Document runner pins; pin the .NET SDK in a file | Runner updates change the SDK silently | S |
| S9 | Hygiene | Limit concurrent dependency-bot pull requests | Each bot push starts up to nine workflows | S |
| S10 | Security | Automatic base-image updates for the Dockerfiles | Images refreshed by hand after incidents | S |
| S11 | Security | Run containers as a non-root user | Root inside the container | S |
| S13 | Security | Configure the dependency review action | Default settings | S |
| S16 | Innovation | Run-level summary on the gate job | Cost invisible per run | S |
| S17 | Innovation | Advisory failure digest on the pull request | Failures spread over many jobs | S-M |
| S18 | Innovation | Weekly CI health report | Baseline for later gains | S |
| S24 | Hygiene | Standard workflow linters | Catch unpinned refs and injection patterns | S |
| S25 | Governance | Tighten branch rules; confirm the Actions policy | Readable ruleset shows gaps | S (admin) |

### A.2 Mid term

| ID | Area | Improvement | Reason | Effort |
|---|---|---|---|---|
| M1 | Speed | Build the Configuration Service once and share the output | Five builds per pull request | M |
| M2 | Speed | Profile and split the unit-test job; coverage on main only | Over twenty minutes | M |
| M3 | Speed | Shard the SQL Server API integration lane | The critical path | M |
| M4 | Speed | Move script suites to the nightly with a short subset on pull requests | Ten to twenty minutes on most pull requests | M |
| M5 | Cost | Templates on demand and on tags | Eight published packages per merge | S-M |
| M6 | Reuse | Reusable end-to-end lane called with a matrix | Eight copies in one file | L |
| M7 | Reliability | Flaky-test quarantine and rerun | Manual reruns every week | M |
| M8 | Security | Nightly dependency and published-image scan | Scans only on file changes | M |
| M9 | Security | Wider code-scanning rules, including workflow files | Broader detection | S |
| M10 | Security | Attest the Docker images | Provenance stops at packages | M |
| M11 | Security | Verify package signatures | Authenticity not verified | M |
| M12 | Security | Vulnerability policy with owner and deadlines | None written | M |
| M13 | Security | GitHub App instead of the personal token | Long-lived token in the release path | M |
| M14 | Innovation | Merge queue batch trial | One entry at a time | S (trial) |
| M15 | Innovation | Skip end-to-end shards whose inputs did not change | Many pull requests touch one area | M |
| M16 | Innovation | Generate the lane matrix from change detection | Dozens of hand-written conditions | M |
| M17 | Runners | Trial larger runners on the two CPU-bound lanes | No claim without a trial | S (trial) |
| M18 | Reuse | Matrix or reusable chain for the release workflow | Eight copies of a five-job chain | M-L |
| M19 | Reuse | One template workflow with a matrix | Five near-identical files | M |
| M20 | Docker | Remove the per-job image-build fallback | Duplicated in eight jobs | M |
| M21 | DX | A one-page CI map document | Nothing explains the 36 workflows | S-M |
| M22 | .NET build | Analyzers off in binary-only lanes after a trial | Analyzers double compile time | S (trial) |
| M23 | Scheduled | Owner, purpose and readable result for every cron | Failures reach a chat channel only | S-M |

### A.3 Long term

| ID | Area | Improvement | Reason | Effort |
|---|---|---|---|---|
| L1 | Scope | Decide the Windows weekend build | Windows-only failures | S |
| L2 | Release | Release the tested artifact; rethink prerelease on every merge | Third build of each commit | L |
| L3 | Governance | Publishing approvals as the release gate; promotion runbook | After S20 | S |
| L4 | Reuse | Export the DMS pieces to the shared actions repository | One owner per fix across repositories | L |
| L5 | Docker | Publish the CI-built image; arm64 only if wanted | amd64 only; third compile | M-L |
| L6 | Runners | No self-hosted runners on a public repository | Fork pull requests would run code on your infrastructure | n/a |
| L7 | Security | Native attestation instead of the forked generator | Twelve calls, a fork pinned by tag | L |
| L8 | Security | Short-lived credentials everywhere | Fewer long-lived secrets | L |
| L9 | .NET build | One shared build properties file | Three copies regenerated by scripts | M |
| L10 | Innovation | Preview environments from pull-request images | Review without a local stack | L |
| L11 | Innovation | Paved-road pipeline for Ed-Fi .NET services | DMS is the first | L |
| L12 | Innovation | Agent-assisted CI maintenance | Workflow churn is the recurring cost | L |
