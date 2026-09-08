---
jira: DMS-1135
jira_url: https://edfi.atlassian.net/browse/DMS-1135
---

# Story: Parallelize Relational E2E CI Shards and Harden Script-Output Assertions

## Description

Reduce pull request E2E wall-clock time by running the relational backend E2E lane as several independent CI shard jobs.

Legacy E2E coverage is not part of the pull request CI build and should not be reintroduced there. Legacy E2E tests continue to run on their existing schedule; this story does not optimize the scheduled legacy lane.

The pull request workflow should run only relational backend E2E coverage:

- environment file: `./.env.e2e.relational`
- base filter: `Category=@relational-backend`
- identity providers: `keycloak` and `self-contained`
- additional shard filter: one shard-specific category such as `Category=@relational-ci-shard-1`

The intended matrix is `identityprovider x shard`. With four shards, the pull request workflow runs eight independent relational E2E jobs. Each job starts its own Docker stack, provisions its own relational E2E database, and invokes `build-dms.ps1 E2ETest` exactly once for its assigned identity provider and shard.

The shard split should be explicit and stable. Add shard tags to relational scenarios/scenario outlines, for example:

- `@relational-ci-shard-1`
- `@relational-ci-shard-2`
- `@relational-ci-shard-3`
- `@relational-ci-shard-4`

Each scenario/scenario outline tagged `@relational-backend` should have exactly one shard tag. The workflow filter for each shard should preserve relational isolation by using `&`, for example:

```powershell
-TestFilter 'Category=@relational-backend&Category=@relational-ci-shard-1'
```

The initial shard assignment should balance measured runtime first and scenario count second. Profiles and other high-count/high-duration feature groups should be split across multiple shards rather than assigned as one large folder. If runtime data is not yet available, start with four roughly even shard buckets and rebalance after CI produces timing data.

This story also hardens the PowerShell-backed test harness so unit tests do not assert on brittle host-formatted exception output. Assertions that currently depend on raw `pwsh` host formatting should normalize ANSI escape sequences and whitespace, disable formatting in the child process, or assert on smaller stable fragments / structured signals instead of contiguous formatted error text.

## Acceptance Criteria

- The pull request workflow does not run legacy E2E tests.
- The pull request workflow runs relational E2E tests as matrix jobs over:
  - identity provider, and
  - relational CI shard.
- Each relational shard job invokes `build-dms.ps1 E2ETest` exactly once using:
  - `-EnvironmentFile './.env.e2e.relational'`,
  - `-TestFilter 'Category=@relational-backend&Category=@relational-ci-shard-N'`, and
  - the assigned identity provider.
- The workflow uses `&` to narrow the relational lane. It does not use `|` or any filter that can mix relational and legacy tests.
- Per-shard logs, test results, and uploaded artifacts are uniquely named by identity provider and shard so parallel jobs do not collide.
- A failed shard is surfaced directly by GitHub as a failed matrix job.
- If a single required status check is still needed, it is implemented as a lightweight summary job that depends on the matrix jobs rather than by forcing heavy E2E work back into one sequential job.
- Every scenario/scenario outline tagged `@relational-backend` has exactly one `@relational-ci-shard-N` tag.
- No scenario/scenario outline without `@relational-backend` has a relational CI shard tag.
- Add or update guardrail tests that enforce the relational shard tagging rules.
- Scheduled legacy E2E workflows are left in their current scheduled path unless a separate story changes them.
- PowerShell-backed unit tests that validate provisioning-helper failures no longer depend on raw ANSI-colored / line-wrapped host output formatting and pass reliably across supported environments.

## Implementation Notes

- Start with four relational shards unless CI capacity or measured runtime suggests a different number.
- Keep the shard count configurable in the workflow matrix so it can be adjusted without redesigning the workflow.
- Prefer shard tags over generated-test ordering, method-name ranges, or file-name globbing. Tags make the CI assignment explicit in the feature files and keep the `dotnet test` filter simple.
- Use stable artifact names such as `postgresql-self-contained-relational-shard-1-test-logs` and `EdFi.DataManagementService.Tests.E2E.relational-shard-1.trx`.
- If the existing build script cannot produce unique TRX names per shard, extend it with a small shard/result-name parameter rather than relying on jobs to overwrite the same file path.
- Capture Docker image reuse as follow-up work if duplicated image build/setup time becomes the dominant cost after sharding.

## Tasks

1. Refactor `.github/workflows/on-dms-pullrequest.yml` so the relational E2E job has a matrix dimension for `identityprovider` and `shard`.
2. Remove legacy E2E execution from the pull request CI workflow if any remains.
3. Add relational shard tags to every current `@relational-backend` scenario/scenario outline.
4. Balance the initial shard assignment across four shards using current scenario counts and any available CI timing data.
5. Update the E2E workflow run step to call `build-dms.ps1 E2ETest` once per matrix job with the relational environment file and the shard-specific filter.
6. Make relational shard logs, TRX files, test-reporter names, and uploaded artifacts unique by identity provider and shard.
7. Add or update unit/guardrail tests that verify:
   - each relational scenario has exactly one shard tag,
   - non-relational scenarios do not have shard tags, and
   - relational E2E workflow filters cannot include legacy tests.
8. Replace the same-job E2E fail wrapper with direct matrix job failure, or add a lightweight summary job only if branch protection requires one roll-up status.
9. Harden PowerShell-output assertions in the provisioning-helper unit tests by normalizing ANSI escape sequences and whitespace before asserting, or by asserting on smaller stable fragments / structured signals instead of host-formatted exception text.
10. Document follow-up optimization work separately if duplicated image-build/setup time remains the main bottleneck after relational sharding.
