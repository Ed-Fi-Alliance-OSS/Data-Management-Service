# E2E Speedup Plan

## Goal

Reduce pull request queue pressure by running the full relational E2E suite with only the `self-contained` identity provider on every DMS PR, while running the full `keycloak` identity provider relational E2E suite on scheduled `main` workflows.

## Current State

- `.github/workflows/on-dms-pullrequest.yml` runs `run-e2e-tests` as an 8-job matrix:
  - `identityprovider: [keycloak, self-contained]`
  - `shard: [1, 2, 3, 4]`
- Each matrix job builds Docker images, runs `./build-dms.ps1 Build`, and then runs one relational E2E shard.
- The `keycloak` and `self-contained` lanes run the same relational shard filters:
  - `Category=@relational-backend&Category=@relational-ci-shard-${{ matrix.shard }}`
- `.github/workflows/scheduled-build.yml` already has scheduled DMS E2E coverage for both identity providers, but it currently runs weekly and is broader than this focused change.

## Target Behavior

- Every DMS PR runs the full relational E2E suite once, using `self-contained` only:
  - 4 PR jobs instead of 8 for `run-e2e-tests`.
  - Same shard filters and artifacts as today.
- `keycloak` full relational E2E coverage runs Sunday-Friday on `main`:
  - 4 nightly jobs, one per relational shard.
  - Same test filters as the current PR `keycloak` matrix.
  - Manual `workflow_dispatch` remains available for reruns.
- The existing weekly scheduled build continues to run full `keycloak` E2E coverage.
- A broken `keycloak` lane is reported in Slack and triaged from the nightly workflow, not from every PR.

## Implementation Plan

1. Add a dedicated nightly Keycloak relational E2E workflow.

   Create `.github/workflows/nightly-keycloak-e2e.yml` with:

   ```yaml
   name: Nightly Keycloak Relational E2E

   on:
     workflow_dispatch:
     schedule:
       - cron: "0 8 * * 0-5"

   permissions: read-all

   env:
     CONFIGURATION: "Release"
   ```

   Keep the workflow dedicated to `keycloak`. Do not include `self-contained` in this workflow.

   Use a matrix over relational shards only:

   ```yaml
   jobs:
     keycloak-relational-e2e:
       name: Run PostgreSQL Relational E2E (keycloak, Shard ${{ matrix.shard }})
       runs-on: ubuntu-latest
       defaults:
         run:
           shell: pwsh
       strategy:
         fail-fast: false
         matrix:
           shard: [1, 2, 3, 4]
   ```

   The job reuses the current PR workflow setup from `run-e2e-tests`:

   - checkout
   - Docker Buildx setup
   - Docker layer cache
   - build DMS Docker image as `dms-local-dms:latest`
   - build Config Docker image as `cs-local-config:latest`
   - NuGet cache
   - `actions/setup-dotnet` with `10.0.x`
   - `./build-dms.ps1 Build -Configuration Release -IdentityProvider keycloak`
   - add `dms-kafka1` to `/etc/hosts`
   - run the full `keycloak` relational shard:

   ```powershell
   ./build-dms.ps1 E2ETest `
     -Configuration Release `
     -SkipDockerBuild `
     -IdentityProvider keycloak `
     -EnvironmentFile './.env.e2e.relational' `
     -TestFilter 'Category=@relational-backend&Category=@relational-ci-shard-${{ matrix.shard }}'
   ```

2. Preserve diagnostics in the nightly workflow.

   Copy the PR workflow's failure handling so nightly failures are actionable. The nightly workflow must always publish the same classes of artifacts as the PR E2E workflow:

   - export relational Docker logs when a shard fails
   - upload `.trx` results
   - generate and upload timing artifacts
   - use these artifact names:
     - `nightly-keycloak-relational-shard-${{ matrix.shard }}-test-logs`
     - `nightly-keycloak-relational-shard-${{ matrix.shard }}-test-results`
     - `test-timings-nightly-keycloak-relational-shard-${{ matrix.shard }}`

3. Add failure notification for the nightly Keycloak workflow.

   Use the same Slack action and secret as `.github/workflows/scheduled-build.yml`:

   - action: `slackapi/slack-github-action@485a9d42d3a73031f12ec201c457e2162c45d02d`
   - secret: `secrets.SLACK_WEBHOOK_URL`

   Add a `notify-results` job that needs `keycloak-relational-e2e`, runs with `if: always()`, skips notification for `workflow_dispatch`, and sends:

   - success notification when all shards pass
   - failure notification when any shard fails

   The Slack message must include:

   - workflow name
   - branch/ref
   - commit SHA
   - Actions run URL

4. Change the DMS PR workflow to self-contained only.

   In `.github/workflows/on-dms-pullrequest.yml`, update the `run-e2e-tests` matrix from:

   ```yaml
   identityprovider: [keycloak, self-contained]
   shard: [1, 2, 3, 4]
   ```

   to:

   ```yaml
   identityprovider: [self-contained]
   shard: [1, 2, 3, 4]
   ```

   This keeps job names, artifact naming, filters, and timing upload logic stable while removing the four Keycloak PR jobs.

5. Review required checks before merging.

   Update `main` branch protection in the same change window as the workflow change.

   Remove these PR-required Keycloak checks:

   - `Run PostgreSQL Relational E2E (keycloak, Shard 1)`
   - `Run PostgreSQL Relational E2E (keycloak, Shard 2)`
   - `Run PostgreSQL Relational E2E (keycloak, Shard 3)`
   - `Run PostgreSQL Relational E2E (keycloak, Shard 4)`

   Keep these PR-required self-contained checks:

   - `Run PostgreSQL Relational E2E (self-contained, Shard 1)`
   - `Run PostgreSQL Relational E2E (self-contained, Shard 2)`
   - `Run PostgreSQL Relational E2E (self-contained, Shard 3)`
   - `Run PostgreSQL Relational E2E (self-contained, Shard 4)`

   Do not make the nightly Keycloak workflow a PR-required check. It runs on `main` and reports failures through Slack.

6. Keep weekly scheduled Keycloak coverage.

   Leave the `.github/workflows/scheduled-build.yml` `build-and-test` matrix unchanged:

   ```yaml
   identityprovider: [keycloak, self-contained]
   ```

   The weekly scheduled build remains the broader scheduled validation workflow and continues running full Keycloak relational E2E coverage on Saturday. The dedicated nightly workflow adds Sunday-Friday Keycloak feedback without removing the weekly Keycloak lane.

7. Validate in stages.

   - Open the workflow-change PR.
   - Run the new nightly workflow manually with `workflow_dispatch` on the PR branch.
   - Confirm all four Keycloak shards run and publish logs/results.
   - Confirm the PR workflow now schedules only four relational E2E jobs, all `self-contained`.
   - Confirm `.github/workflows/scheduled-build.yml` still includes `identityprovider: [keycloak, self-contained]`.
   - Merge after branch protection is updated.
   - Confirm the first nightly run on `main` completes and publishes results; triage uploaded failure artifacts immediately for any failed shard.

## Expected Impact

- Removes four long-running relational E2E jobs from every DMS PR.
- Estimated savings from recent workflow timings: 80-90 GitHub-hosted runner minutes per DMS PR, plus reduced queue contention for the remaining jobs.
- Maintains full `self-contained` relational coverage before merge.
- Maintains full `keycloak` relational coverage Sunday-Friday on `main` and during the weekly scheduled build on Saturday, where failures can be triaged without blocking every PR.

## Follow-Up Optimizations

- Build the DMS and Config Docker images once per workflow and reuse them across shard jobs.
- Replace the full `./build-dms.ps1 Build` in E2E shard jobs with a narrower test-project build path if the Docker image already contains the app under test.
- Add setup/fixture timing around E2E runs so hidden one-time setup costs are visible in timing artifacts.
