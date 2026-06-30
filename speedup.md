# GitHub Actions Speedup Implementation Plan

This plan targets the PR build queue pressure caused by high job fan-out, repeated setup work, and duplicated validation. It covers these changes:

- Build CI Docker images once, then reuse them.
- Prebuild .NET test assemblies once for integration shards.
- Rebalance shards using timing artifacts.
- Gate expensive jobs behind cheap jobs.
- Remove duplicate Dockerfile analysis.
- Add `max-parallel` to expensive matrices.

## Current Bottlenecks

The current DMS PR workflow fans out many heavyweight jobs in parallel:

- Four Backend MSSQL integration shards.
- DMS API MSSQL integration.
- Backend PostgreSQL integration.
- DMS API PostgreSQL integration.
- Three CLI integration legs plus SchemaTools database legs.
- Four DMS E2E shards.
- DS 6.1 DMS E2E.
- Two Instance Management E2E shards.
- OpenAPI validation stack.
- Fresh Docker rebuild checks.

The latest observed run showed:

- DMS PR workflow elapsed time: about 32 minutes.
- Slowest DMS E2E shard: about 21 minutes.
- Slowest Backend MSSQL shard: about 20 minutes.
- DMS E2E shards each rebuilt DMS and Config Docker images and ran a build before tests.
- Backend MSSQL shard 3 spent about 19 minutes in `dotnet test`, but only about 2 minutes were attributed to individual test durations in the TRX summary.
- Adding more shards now risks increasing queue wait instead of reducing total time.

## Phase 1: Remove Duplicate Dockerfile Analysis

Goal: stop analyzing the DMS Dockerfile twice on PRs.

Files:

- `.github/workflows/on-dms-pullrequest-dockerfile.yml`
- `.github/workflows/on-pullrequest-dockerfile.yml`

Implementation:

1. Keep the generic `On Pull Request - Dockerfile` workflow as the single Dockerfile PR analysis path because it already covers both DMS and Config Dockerfiles.
2. Delete `.github/workflows/on-dms-pullrequest-dockerfile.yml`, or change its trigger to `workflow_dispatch` only if a short-term rollback path is desired.
3. Confirm branch protection does not require checks from the deleted workflow. If it does, update required checks before merging.
4. Verify that a DMS Dockerfile-only PR still runs one Dockerfile analysis job for `src/dms/Dockerfile`.

Expected impact:

- Removes one redundant workflow run and one Docker build/scout pass when `src/dms/Dockerfile` changes.

## Phase 2: Gate Expensive Jobs Behind Cheap Jobs

Goal: fail fast before starting Docker-heavy and database-heavy jobs.

Files:

- `.github/workflows/on-dms-pullrequest.yml`
- `.github/workflows/on-config-pullrequest.yml`

Cheap gate jobs:

- Detect Fresh Build Changes.
- BIDI/action scan.
- Lock-file verification.
- Bootstrap/lock-file Pester tests.
- Unit tests.

Expensive jobs to gate:

- Backend MSSQL integration shards.
- DMS API MSSQL integration.
- Backend PostgreSQL integration.
- DMS API PostgreSQL integration.
- SchemaTools database integration jobs.
- DMS E2E shards.
- DS 6.1 DMS E2E.
- Instance Management E2E shards.
- OpenAPI stack build/download job.
- Fresh Docker rebuild jobs.

Implementation:

1. Add a lightweight aggregate gate job, for example `cheap-checks-summary`, that depends on the cheap jobs and fails if any cheap check fails.
2. Add `needs: cheap-checks-summary` to heavyweight jobs.
3. Preserve `if: always()` only on summary/reporting jobs that must run after failures.
4. Keep timing artifact upload steps inside heavyweight jobs unchanged.
5. Apply the same pattern in Config PR workflow for integration/E2E/fresh Docker rebuild jobs.

Expected impact:

- Prevents wasting expensive runner time when a cheap validation fails.
- Reduces concurrent job bursts at the start of every PR run.

## Phase 3: Build CI Docker Images Once And Reuse Them

Goal: remove repeated DMS and Config image builds from E2E, instance E2E, OpenAPI, and smoke jobs.

Files:

- `.github/workflows/on-dms-pullrequest.yml`
- `.github/workflows/on-config-pullrequest.yml`
- `.github/workflows/scheduled-smoke-test.yml`
- Potentially `eng/docker-compose/start-local-dms.ps1` and related compose files if image tag injection is needed.

Preferred design:

1. Add a `build-ci-docker-images` job after cheap gates.
2. Build `ed-fi-api-local:${{ github.sha }}` and `ed-fi-api-config-local:${{ github.sha }}` once.
3. Push the images to GHCR with PR-scoped tags, or upload compressed `docker save` artifacts if registry use is not acceptable.
4. Update downstream jobs to pull/load those images.
5. Make compose/start scripts accept an image tag override, or set environment variables consumed by compose files.
6. Remove repeated `Build DMS Docker image` and `Build Config Docker image` steps from:
   - DMS E2E matrix.
   - DS 6.1 DMS E2E.
   - Instance Management E2E.
   - Build and Start DMS, Download OpenAPI Specs.
   - Smoke-test legs.

Registry option:

- Pros: faster startup for downstream jobs, avoids large artifact transfer, works across workflows.
- Cons: requires package permissions and cleanup policy.

Artifact option:

- Pros: avoids registry setup and permissions.
- Cons: Docker image artifacts may be large and slow to upload/download.

Recommended first implementation:

1. Use GHCR for PR images.
2. Tag images with `pr-${{ github.event.pull_request.number }}-${{ github.sha }}` and `sha-${{ github.sha }}`.
3. Add retention cleanup using GHCR retention policy or a scheduled cleanup workflow.
4. Keep existing per-job builds behind a temporary fallback input/environment variable during rollout.

Expected impact:

- Removes several minutes of repeated setup from each E2E-style job.
- Reduces Docker layer cache contention from many jobs writing the same cache key.

## Phase 4: Prebuild .NET Test Assemblies For Integration Shards

Goal: avoid rebuilding test projects inside every integration shard.

Files:

- `.github/workflows/on-dms-pullrequest.yml`
- `build-dms.ps1` if helper commands are needed.

Implementation:

1. Add a `build-dms-test-assemblies` job after cheap gates.
2. Restore and build these projects once in Release:
   - `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/EdFi.DataManagementService.Backend.Mssql.Tests.Integration.csproj`
   - `src/dms/tests/EdFi.DataManagementService.Tests.Integration/EdFi.DataManagementService.Tests.Integration.csproj`
   - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration.csproj`
   - `src/dms/clis/EdFi.DataManagementService.SchemaTools.Tests.Integration/EdFi.DataManagementService.SchemaTools.Tests.Integration.csproj`
3. Upload the built `bin/Release/net10.0` and needed `obj` outputs as artifacts, or use `dotnet publish` for test projects if it produces a cleaner runnable directory.
4. In shard jobs, download the artifact and run `dotnet test <test-assembly.dll> --no-build --no-restore` with the existing filters and loggers.
5. Keep database container startup inside each shard job.
6. Verify NUnit adapter discovery works from the artifact directory.

Important follow-up:

- Backend MSSQL shard 3 currently reports much more VSTest time than summed test-method duration. After prebuilding, add fixture-level timing around database setup/teardown to find the hidden cost.

Expected impact:

- Reduces redundant restore/build work.
- Makes integration shard timings easier to interpret.

## Phase 5: Rebalance Expensive Shards Using Timing Artifacts

Goal: reduce the slowest shard without increasing total runner demand.

Files:

- Test category attributes in DMS E2E and Backend MSSQL integration tests.
- `on-dms-pullrequest.yml` shard category filters.
- Existing timing artifacts generated by `eng/ci/summarize-test-timings.ps1`.

Implementation:

1. Download timing artifacts from at least 5 recent successful DMS PR runs.
2. Aggregate by fixture and category.
3. For DMS E2E, move the slowest shard 3 fixtures across shards:
   - Change query authorization fixtures.
   - Relationship-based authorization fixtures.
   - Profile-heavy fixtures.
4. For Backend MSSQL integration, inspect fixture setup time because TRX method duration does not explain the shard runtime.
5. Reassign categories so each shard has comparable observed wall time, not merely comparable test count.
6. Add a short shard-balancing note to the timing summary output so future regressions are visible in PR summaries.

Expected impact:

- Reduces critical-path shard time without adding more jobs.
- Avoids worsening queue pressure.

## Phase 6: Refine PR Trigger Scope

Goal: avoid lighting up DMS, Config, and smoke workflows for changes that do not require all of them.

Files:

- `.github/workflows/on-dms-pullrequest.yml`
- `.github/workflows/on-config-pullrequest.yml`
- `.github/workflows/scheduled-smoke-test.yml`

Implementation:

1. Split broad `eng/**` triggers into narrower areas:
   - Docker compose changes.
   - Database template changes.
   - Smoke tooling changes.
   - CI helper changes.
2. Keep full DMS validation for config changes that affect DMS runtime behavior, but avoid triggering it for config-only tests/docs when possible.
3. Move full `Scheduled Smoke Test` PR trigger to one of:
   - Label-triggered workflow.
   - Manual `workflow_dispatch`.
   - Merge queue or post-merge validation.
   - Narrower paths only for SDK/template/smoke source changes.
4. Add a smaller PR smoke canary if full smoke moves out of the standard PR path.

Expected impact:

- Reduces the number of workflows started for mixed but low-risk changes.
- Keeps full coverage available without making every PR pay for it.

## Rollout Order

1. Remove duplicate Dockerfile analysis.
2. Add cheap-check summary gates and make heavyweight jobs depend on them.
3. Build and reuse CI Docker images.
4. Prebuild .NET test assemblies for integration shards.
5. Rebalance DMS E2E and Backend MSSQL shards.
6. Refine broad PR path triggers and move full smoke to a narrower path.

This order starts with low-risk workflow cleanup, then controls queue pressure, then removes repeated work.

Each phase must be implemented, pushed, and verified with a real GitHub Actions run before starting the next phase. Do not batch multiple phases into one push unless a phase is blocked by an unavoidable dependency on the next phase. For each phase, capture the workflow run URL, note whether all expected checks ran, and compare the relevant timing/queue metrics against the previous baseline before proceeding.

## Verification

For each phase-specific validation run, record:

- Total workflow elapsed time.
- Queued time per heavyweight job.
- Number of jobs created per workflow.
- Slowest DMS E2E shard duration.
- Slowest Backend MSSQL shard duration.
- Total runner-minutes consumed.
- Failure detection time when cheap checks fail.

Success criteria:

- Fewer jobs start immediately at PR creation.
- Heavyweight jobs spend less time queued when multiple PRs are active.
- DMS E2E shard durations are closer together.
- Repeated Docker build time is removed from downstream E2E-style jobs.
- Required PR checks still provide equivalent coverage.
