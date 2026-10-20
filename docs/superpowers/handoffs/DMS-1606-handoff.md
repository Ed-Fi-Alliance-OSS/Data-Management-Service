# DMS-1606 Implementation Handoff

## Story

- Process id: `2026-10-20-DMS-1606-oauth-token-errors`
- Story id: DMS-1606
- Story source: [DMS-1606](https://edfi.atlassian.net/browse/DMS-1606)
- Notes source: `.plans/DMS-1606-implementation-pre-spec.md`, `.plans/DMS-1606-implementation-pre-spec.nano.md`, `.plans/DMS-1606-implementation-pre-spec-review.md`, `.plans/DMS-1606-ods-parity-validation.md` (supporting analysis; approved spec governs behavior)

## Approved artifacts

- Spec: `reference/design/DMS-1606-oauth-token-errors.md` (approved 2026-10-20)
- Plan: `docs/superpowers/plans/2026-10-20-DMS-1606-oauth-token-errors.md` (approved 2026-10-20; Native `superpowers:executing-plans` selected)

## Current state

- Branch: `DMS-1606`
- Baseline commit: `334ce4443a172d8b6ef145c5367d68068f52c351`
- Current repository revision at handoff creation: `5803e378f608abeff662bf78f1db2d5322073589` plus the approval-status and handoff documentation changes in this handoff commit.
- Working tree state: no product implementation has started; all seven plan task checkboxes remain unchecked. This handoff records approval and does not start implementation.
- Last verified command: `git diff --check` (documentation-only approval and handoff changes)
- Last verified result: documentation diff check passed with exit code 0; this is not test or build evidence.
- Review findings report: `.plans/DMS-1606-implementation-pre-spec-review.md`; ODS source parity findings: `.plans/DMS-1606-ods-parity-validation.md`.

## Execution contract

- Target host: local repository checkout, branch `DMS-1606`.
- Entry command: open this handoff, then read the approved spec and plan. Start Task 1 using `superpowers:executing-plans` after checking branch, HEAD, and working tree.
- Adapter layer notes: preserve the existing Core `IOAuthManager` / `HttpResponseMessage` boundary and frontend endpoint mappings. Keep token formatting token-local and use endpoint metadata for limiter and exception selection. No CMS/shared formatter or schema mapping change.
- Required skills: `superpowers:executing-plans`, `superpowers:test-driven-development`, `superpowers:verification-before-completion`; follow repository `AGENTS.md` and the plan's per-task commands.
- Files to read first: `AGENTS.md`, approved spec, approved plan, and this handoff. Then inspect the plan's Task 1 file map and current code before editing.

## Next task

- First pending task: Task 1, token wire factory and local request classification.
- Why this is next: it establishes the shared response contract and local OAuth behavior required by the provider and frontend tasks.
- Acceptance signal: Task 1 tests demonstrate the approved wire shape and rows 1-6, 8-10; implementation passes the plan's focused Core test command and is committed as an isolated task. No task is complete yet.

## Assumptions

- The approved spec is normative. The human/nano pre-specs, review report, and parity report provide evidence only.
- The required dual token-limit URNs are a verified upstream compatibility dependency recorded in the approved plan; retain both accepted forms and the existing bounded guidance behavior.
- Existing intentional DMS/ODS differences and the explicit 429/502/503 extension mapping remain as approved in the spec.

## Risks

- Keep caller Authorization context distinct from DMS-generated upstream Basic credentials when classifying provider `invalid_client`.
- Validate provider descriptions after JSON decoding and preserve existing sanitized summary logging limits.
- Proxy fixture work is bounded to tests; its launcher must restore DMS configuration and owned resources on success and failure. Follow Task 6 cleanup and CI constraints.
- Real-stack runtime verification remains pending. Do not mark fixture or E2E tasks complete without the plan's specified evidence.

## Verification evidence

- Command: `git diff --check`
- Result: passed with exit code 0 for the documentation diff. No build, local test, Docker run, or implementation verification has been performed for DMS-1606.
