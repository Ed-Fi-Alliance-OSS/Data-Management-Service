# Task 1 brief — local rebuild and normal-wrapper guidance

Read this brief first; it is the exact task requirement.

Implement Task 1 of `docs/superpowers/plans/2026-09-24-dms-1427-feedback-remediation.md`.

Files:
- Modify `eng/docker-compose/bootstrap-local-dms.ps1`: add public `[Alias("r")][Switch]$Rebuild` with help text.
- Modify `eng/docker-compose/bootstrap-wrapper.psm1`: accept `[switch]$Rebuild`, map it to the existing initial local `start-local-dms.ps1 -r`, and set existing `SuppressWriterGuidance` only for wrapper-owned normal (not terminal `-InfraOnly`) local startup. Do not pass `r` to later `-DmsOnly`.
- Modify `eng/docker-compose/tests/BootstrapEntryPointWorkflow.Tests.ps1`: extend recording start stub to capture `r` and `SuppressWriterGuidance`; test explicit rebuild only on initial start, default no rebuild, normal wrapper omits terminal guidance and reaches DMS-only, direct InfraOnly guidance remains.

Global constraints:
- Preserve DMS-1153 direct/manual InfraOnly guidance.
- Preserve existing schema/workspace safety and all phase ownership.
- No migration, health, form-credential, automatic-rebuild, or published-wrapper changes.
- Do not implement unrelated cleanup.

Required test behavior:
- `-Rebuild` wrapper start record has `rebuild=True` only for `start-infra`; `start-dms` has `rebuild=False`.
- Default wrapper start has `rebuild=False`.
- Normal wrapper has full writer guidance suppression on initial infra call and still records DMS-only startup.
- Terminal/direct InfraOnly path still emits manual guidance.

Run the focused Pester file after implementation:
`pwsh -NoProfile -Command "Invoke-Pester ./eng/docker-compose/tests/BootstrapEntryPointWorkflow.Tests.ps1 -Output Detailed"`

Commit implementation and tests. Write your full report to:
`.superpowers/sdd/2026-09-24-dms-1427-feedback-remediation/task-1-report.md`

Return only status (DONE, DONE_WITH_CONCERNS, NEEDS_CONTEXT, or BLOCKED), commit hash(es), one-line test summary, and concerns.
