# Task 2 brief — Preserve Windows paths in prepared-workspace output

## Scope

- Modify only `eng/docker-compose/prepare-dms-schema.ps1` at the prepared-workspace log call and `eng/docker-compose/tests/BootstrapSchemaDeploymentSafety.Tests.ps1` near existing formatter tests.
- Replace only `Format-LogSafeText $finalWorkspace` with the existing `Format-LogSafePath $finalWorkspace` for the `Prepared ApiSchema workspace at ...` output.
- Add a source-level regression assertion proving the call site uses `Format-LogSafePath` and not `Format-LogSafeText`.
- Preserve existing formatter behavior tests: printable Windows path characters remain readable, control characters are removed.

## Constraints

- Do not replace unrelated `Format-LogSafeText` validation/error sites.
- Do not create another sanitizer or change exported formatter behavior.
- Preserve DMS-1151/DMS-1153 and all other approved design constraints; no migration, compatibility, health, or unrelated cleanup.

## Verification

Run focused Pester:
`pwsh -NoProfile -Command "Invoke-Pester ./eng/docker-compose/tests/BootstrapSchemaDeploymentSafety.Tests.ps1 -Output Detailed"`

Commit with a focused message and write `.superpowers/sdd/2026-09-24-dms-1427-feedback-remediation/task-2-report.md` containing status, commit, exact test result, and concerns.

Do not dispatch subagents or reviewers. Work only in the current workspace, and do not modify unrelated files.
