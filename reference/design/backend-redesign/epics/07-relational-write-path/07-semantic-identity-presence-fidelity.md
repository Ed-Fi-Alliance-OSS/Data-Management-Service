---
jira: DMS-1132
jira_url: https://edfi.atlassian.net/browse/DMS-1132
---

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

## Out Of Scope

- General profile-aware merge semantics already owned by `DMS-1124`
- Non-identity hidden-member preservation already covered by `DMS-1124`
- Unrelated write-path refactors

## Acceptance Criteria

- Semantic-identity matching no longer depends on upstream null-pruning invariants to distinguish absent from explicit-null identity members.
- Executor-facing metadata or plan shape preserves the presence information required for deterministic matching.
- Profile-aware and no-profile collection matching behave consistently for presence-sensitive identity cases.
- Missing or inconsistent presence-fidelity metadata fails deterministically rather than silently matching the wrong row.
- PostgreSQL and SQL Server coverage exists for representative presence-sensitive identity cases.

## Tests Required

### Unit tests

- Identity matching distinguishes absent vs explicit-null for representative semantic-identity shapes
- Missing required presence-fidelity metadata fails deterministically
- Duplicate or ambiguous matches caused by collapsed presence information fail deterministically

### Integration tests

- Representative no-profile null-identity case
- Representative profile-aware null-identity case
- PostgreSQL and SQL Server parity coverage for the above cases

## Tasks

1. Define the executor-facing contract or plan changes needed to preserve presence-sensitive identity fidelity.
2. Update matching logic to consume the preserved presence information without introducing backend-owned profile inference.
3. Add deterministic validation/failure behavior when presence-sensitive identity metadata is missing or inconsistent.
4. Add unit and integration coverage for representative absent-vs-explicit-null identity cases on both dialects.
