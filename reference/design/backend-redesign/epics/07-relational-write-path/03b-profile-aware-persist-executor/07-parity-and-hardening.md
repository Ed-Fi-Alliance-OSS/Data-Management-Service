---
jira: DMS-1124
---

# Slice 7: Parity And Hardening

## Purpose

Close the planning sequence by:

- verifying pgsql and mssql parity for the supported profiled runtime shapes,
- tightening remaining contract/runtime hardening that is still required for safe merge, and
- extracting any remaining non-blocking fragility into explicit named follow-ups instead of leaving it as silent debt.

This slice exists to keep the earlier slices focused on semantic support rather than letting every review thread expand into "and also parity/hardening/cleanup."

Earlier slices still own parity expectations for any SQL-sensitive behavior they introduce. This final slice is the branch-wide parity audit and gap-closure pass, not the first point where parity becomes required.

## In Scope

- PostgreSQL and SQL Server parity review for supported profiled shapes
- Dialect-sensitive batching, parameterization, and locking checks where behavior could diverge
- Remaining reverse-coverage or contract-hardening work required for safe merge
- Explicit follow-up extraction for unresolved but non-blocking risks
- Explicit handoff to `DMS-1132` / `../07-semantic-identity-presence-fidelity.md` for presence-sensitive semantic identity fidelity unless that work is intentionally absorbed here
- Decision on profile vs no-profile collection ordinal-base alignment. Slice 6 (`06-profile-guarded-no-op.md`) ships profile-aware guarded no-op while the no-profile flatten path stamps 0-based ordinals (`RelationalWriteFlattener` `RequestOrder`) and the profile collection walker stamps 1-based `finalOrdinal = i + 1`. Documents created via the no-profile path therefore cannot reach the guarded no-op short-circuit on a later identical profiled PUT — the merged ordinals differ from the stored ordinals and the executor falls through to real collection DML. Slice 6's top-level-collection integration fixture acknowledges this by seeding through the profiled POST path (see `PostgresqlProfileGuardedNoOpTests.cs` lines 666-674). This slice owns the decision to either (a) align ordinal bases on one side, (b) document and accept the first-write DML cost on pre-profile data, or (c) plan a backfill/normalization migration.

## Explicitly Out Of Scope

- Broad refactor/cleanup unrelated to correctness
- New feature scope beyond the supported profiled slices

## Supported After This Slice

- Supported profiled runtime scenarios have explicit pgsql/mssql parity expectations.
- Dialect-sensitive code paths have the minimum required coverage to support merge confidence.
- Residual non-blocking risks are documented as explicit follow-ups rather than carried invisibly by `DMS-1124`.

## Design Constraints

- Parity is not just "tests exist"; the branch must either demonstrate equivalent behavior or document why a difference is expected.
- Hardening work that is not a merge blocker must become an explicit follow-up rather than expanding the slice indefinitely.
- Presence-sensitive semantic identity fragility should stay tied to `DMS-1132` unless this slice explicitly changes the merge guarantee.

## Acceptance Criteria

- The supported profiled runtime baseline passes on both PostgreSQL and SQL Server.
- Dialect-sensitive batching/locking/parameterization behavior has explicit coverage or explicit review rationale.
- Remaining unresolved correctness assumptions are documented as explicit follow-ups, not hidden in comments or oral history.
- `DMS-1132` / `../07-semantic-identity-presence-fidelity.md` remains the named follow-on for presence-sensitive semantic identity fidelity unless the implementation truly closes that gap here.

## Tests Required

### Integration tests

- Supported-slice parity pass for pgsql
- Supported-slice parity pass for mssql
- Any dialect-sensitive batch/parameter-limit cases added by the supported slices
- Any dialect-sensitive lock/freshness behavior relevant to profiled guarded no-op
- Literal three-level update-allowed/create-denied chain at the provider level (parents → children → grandchildren). Slice 5 delivers this at the synthesizer level (`Given_three_level_chain_with_update_allowed_at_levels_1_and_2_create_denied_at_level_3`) and at the HTTP layer with a two-level matched-update / create-denied-child chain. As part of this slice's parity audit, decide whether the literal three-level provider fixture is merge-blocking; if yes, add a new `IntegrationFixtures/profile-nested-three-level-chain` fixture plus pgsql/mssql provider plumbing and a three-level rejection scenario. Otherwise explicitly document why synthesizer coverage plus two-level provider rejection coverage is sufficient.

### Documentation / review outputs

- Explicit list of non-blocking follow-ups extracted from the branch review
- Explicit statement of whether `DMS-1132` remains open and why

## Reviewer Focus

Reviewers for this slice should focus only on:

- pgsql/mssql parity,
- dialect-sensitive risk,
- whether remaining hardening items are true merge blockers or safe follow-ups, and
- whether follow-up extraction is explicit enough.

Reviewers should explicitly ignore:

- re-review of already accepted semantic slices unless a parity issue reveals a real correctness gap.

## Leaves Behind

After this slice, `DMS-1124` should have:

- a complete serial design plan,
- an explicit merge-blocker vs follow-up boundary, and
- a named handoff for any unresolved hardening such as `DMS-1132`.
