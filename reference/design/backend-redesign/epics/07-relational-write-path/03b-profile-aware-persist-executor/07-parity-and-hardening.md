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

## In Scope

- PostgreSQL and SQL Server parity review for supported profiled shapes
- Dialect-sensitive batching, parameterization, and locking checks where behavior could diverge
- Remaining reverse-coverage or contract-hardening work required for safe merge
- Explicit follow-up extraction for unresolved but non-blocking risks
- Explicit handoff to `DMS-1132` for presence-sensitive semantic identity fidelity unless that work is intentionally absorbed here

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
- `DMS-1132` remains the named follow-on for presence-sensitive semantic identity fidelity unless the implementation truly closes that gap here.

## Tests Required

### Integration tests

- Supported-slice parity pass for pgsql
- Supported-slice parity pass for mssql
- Any dialect-sensitive batch/parameter-limit cases added by the supported slices
- Any dialect-sensitive lock/freshness behavior relevant to profiled guarded no-op

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
