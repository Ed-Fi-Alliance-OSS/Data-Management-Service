---
jira: DMS-1135
jira_url: https://edfi.atlassian.net/browse/DMS-1135
---

# Story: Parallelize Legacy/Relational E2E Lanes and Harden Script-Output Assertions

## Description

Reduce E2E wall-clock time by running the legacy and relational backend lanes as independent matrix jobs instead of sequential steps inside the same workflow job.

This story makes `lane` a first-class workflow dimension across:

- `.github/workflows/on-dms-pullrequest.yml`
- `.github/workflows/scheduled-build.yml`
- `.github/workflows/scheduled-pre-image-test.yml`

Each matrix lane should invoke `build-dms.ps1 E2ETest` exactly once using the lane-specific environment file and test filter, while preserving the current identity-provider split and published-image behavior where applicable.

This story also hardens the PowerShell-backed test harness so unit tests do not assert on brittle host-formatted exception output. Assertions that currently depend on raw `pwsh` host formatting should normalize ANSI escape sequences and whitespace, disable formatting in the child process, or assert on smaller stable fragments / structured signals instead of contiguous formatted error text.

This is a workflow and test-harness refactor. It does not change the logical coverage owned by the existing legacy vs relational lanes.

## Acceptance Criteria

- The PR, scheduled-build, and scheduled pre-image workflows run legacy and relational backend E2E coverage as separate matrix jobs instead of sequential steps in one job.
- Each matrix job invokes `build-dms.ps1 E2ETest` once for its assigned lane, using lane-specific values for:
  - identity provider,
  - environment file,
  - test filter, and
  - published-image mode where required.
- Per-lane logs, test results, and uploaded artifacts are uniquely named by lane and identity provider so parallel jobs do not collide.
- A failed lane is surfaced directly by GitHub as a failed matrix job; the workflow no longer relies on a same-job fail-fast wrapper to summarize multiple hidden lane results.
- If a single required status check is still needed, it is implemented as a lightweight summary job that depends on the matrix jobs rather than by forcing heavy E2E work back into one sequential job.
- PowerShell-backed unit tests that validate provisioning-helper failures no longer depend on raw ANSI-colored / line-wrapped host output formatting and pass reliably across supported environments.
- The workflows preserve the current logical lane split:
  - legacy lane uses `./.env.e2e` with `Category!=@relational-backend`
  - relational lane uses `./.env.e2e.relational` with `Category=@relational-backend`

## Tasks

1. Refactor `.github/workflows/on-dms-pullrequest.yml` so `lane` is a matrix dimension alongside identity provider and each job runs one E2E lane.
2. Apply the same lane-matrix pattern to `.github/workflows/scheduled-build.yml`.
3. Apply the same lane-matrix pattern to `.github/workflows/scheduled-pre-image-test.yml`, preserving `-UsePublishedImage`.
4. Consolidate duplicated E2E run, log-export, result-upload, and artifact-upload steps into single parameterized lane-aware steps.
5. Remove the same-job lane aggregation/fail wrapper, or replace it with a lightweight summary job only if repository branch protection still requires a single roll-up check.
6. Harden PowerShell-output assertions in the provisioning-helper unit tests by normalizing ANSI escape sequences and whitespace before asserting, or by asserting on smaller stable fragments / structured signals instead of host-formatted exception text.
7. Capture any follow-up optimization work separately if duplicated image-build/setup time becomes the dominant cost after lane parallelization (for example, a producer job that uploads prebuilt Docker images for later `docker load` reuse).
