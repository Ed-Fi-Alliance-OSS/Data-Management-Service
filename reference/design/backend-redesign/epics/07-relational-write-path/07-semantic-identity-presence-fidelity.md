---
jira: DMS-1132
jira_url: https://edfi.atlassian.net/browse/DMS-1132
---

> **Status:** Identity-validation work closed. Implemented as deterministic pre-merge ambiguity detection; the validation rule is documented in `../../design-docs/profiles.md` § "Storage-Collapsed Semantic Identity Uniqueness". Profile path emits `AmbiguousStorageCollapsedIdentityCoreBackendContractMismatchFailure` from `ProfileWriteContractValidator`; no-profile path emits `RelationalWriteRequestValidationException` from the flattener.
>
> This story also carries the CI E2E-scope requirement documented below.

# Story: Preserve Presence-Sensitive Semantic Identity Fidelity

## Description

Close the remaining presence-sensitive semantic identity gap exposed by `DMS-1124`.

The profile-aware shared write path must distinguish:

- identity members omitted because they are absent,
- identity members explicitly present with JSON `null`, and
- identity members present with a concrete value.

The current redesign still carries a documented fragility: once request and stored data are flattened into executor-facing buffers, some identity surfaces can collapse "absent" and "explicit null" into the same storage-space shape unless an upstream invariant has already preserved that distinction.

`DMS-1132` exists so that `DMS-1124` does not need to silently rely on null-pruning or profile-shaping side effects to keep collection matching correct.

This follow-on owns making the distinction explicit, validated, and durable for semantic-identity matching in the shared executor path.

## Scope

- Presence-sensitive semantic identity fidelity for profile-aware collection matching
- Contract or plan changes required to preserve absent-vs-explicit-null information through executor-facing metadata
- Deterministic validation and failure behavior when required presence fidelity is missing or inconsistent
- Test coverage for null-identity and presence-sensitive matching cases on both PostgreSQL and SQL Server
- CI E2E scope adjustment for this branch: normal DMS PR/push CI should run only relational-backend DMS E2E coverage from the main DMS E2E suite, while scheduled workflows retain the legacy-backend DMS E2E lane.

## Out Of Scope

- General profile-aware merge semantics already owned by `DMS-1124`
- Non-identity hidden-member preservation already covered by `DMS-1124`
- Unrelated write-path refactors
- Removing CMS E2E coverage from CI
- Removing the DMS multi-tenant / instance-management E2E suite from CI
- Removing legacy-backend DMS E2E coverage from scheduled workflows

## Acceptance Criteria

- Semantic-identity matching no longer depends on upstream null-pruning invariants to distinguish absent from explicit-null identity members.
- Executor-facing metadata or plan shape preserves the presence information required for deterministic matching.
- Profile-aware and no-profile collection matching behave consistently for presence-sensitive identity cases.
- Missing or inconsistent presence-fidelity metadata fails deterministically rather than silently matching the wrong row.
- PostgreSQL and SQL Server coverage exists for representative presence-sensitive identity cases.
- Normal DMS PR/push CI no longer runs the legacy-backend lane from `EdFi.DataManagementService.Tests.E2E` (`Category!=@relational-backend`).
- Normal DMS PR/push CI still runs `EdFi.DataManagementService.Tests.E2E` scenarios tagged `@relational-backend` with the relational E2E environment.
- Normal DMS PR/push CI still runs the DMS instance-management / multi-tenant E2E suite.
- CMS PR CI still runs CMS E2E coverage unchanged.
- Scheduled DMS workflows still run legacy-backend DMS E2E coverage.

## CI E2E Scope Requirement

The DMS PR/push workflow currently runs both DMS E2E lanes:

- legacy lane: `build-dms.ps1 E2ETest -EnvironmentFile './.env.e2e' -TestFilter 'Category!=@relational-backend'`
- relational lane: `build-dms.ps1 E2ETest -EnvironmentFile './.env.e2e.relational' -TestFilter 'Category=@relational-backend'`

The branch requirement is to remove the legacy lane from normal DMS CI only. In `.github/workflows/on-dms-pullrequest.yml`, the `run-e2e-tests` job should keep the relational lane and its result/log handling, but should no longer invoke or fail-gate on the legacy lane. The relational lane must continue to use `Category=@relational-backend`; do not broaden the filter to all E2E tests.

The workflow must keep running the DMS instance-management E2E job (`build-dms.ps1 InstanceE2ETest`) because that is the multi-tenant suite and is not part of the legacy DMS backend lane being removed from normal CI.

CMS E2E coverage is owned by the config workflow and must remain unchanged.

Legacy-backend DMS E2E coverage remains scheduled:

- `.github/workflows/scheduled-build.yml` runs weekly on Saturday at 08:00 UTC and includes `Category!=@relational-backend`.
- `.github/workflows/scheduled-pre-image-test.yml` runs weekly on Saturday at 08:30 UTC and includes `Category!=@relational-backend`.

Do not weaken `build-dms.ps1` E2E lane guardrails. The script should continue requiring relational environments to use `Category=@relational-backend` and legacy environments to use `Category!=@relational-backend`; the CI change is a workflow-selection change, not a script contract change.

## Tests Required

### Unit tests

- Identity matching distinguishes absent vs explicit-null for representative semantic-identity shapes
- Missing required presence-fidelity metadata fails deterministically
- Duplicate or ambiguous matches caused by collapsed presence information fail deterministically

### Integration tests

- Representative no-profile null-identity case
- Representative profile-aware null-identity case
- PostgreSQL and SQL Server parity coverage for the above cases

### CI/workflow validation

- The diff to `.github/workflows/on-dms-pullrequest.yml` is reviewed to confirm the normal DMS PR/push workflow no longer invokes `build-dms.ps1 E2ETest` with `Category!=@relational-backend`, still invokes `build-dms.ps1 E2ETest` with `Category=@relational-backend`, and still invokes `build-dms.ps1 InstanceE2ETest`.
- Existing `build-dms.ps1` runtime lane guardrail tests (`Given_Build_Dms_E2E_Guardrails`) remain valid and are not relaxed.

## Tasks

1. Define the executor-facing contract or plan changes needed to preserve presence-sensitive identity fidelity.
2. Update matching logic to consume the preserved presence information without introducing backend-owned profile inference.
3. Add deterministic validation/failure behavior when presence-sensitive identity metadata is missing or inconsistent.
4. Add unit and integration coverage for representative absent-vs-explicit-null identity cases on both dialects.
5. Update normal DMS PR/push CI to run only relational-tagged DMS E2E coverage from the main DMS E2E suite, while preserving CMS E2E, DMS instance-management E2E, and scheduled legacy DMS E2E coverage.
