# Task 2 implementation report

## Status

DONE

## Commit

- `424a11a2` — `fix: preserve Windows paths in bootstrap workspace logs`

## Implemented

- Updated the prepared ApiSchema workspace log to use `Format-LogSafePath`, preserving printable Windows path separators while retaining control-character sanitization.
- Added a source-level regression assertion requiring the prepared-workspace call site to use `Format-LogSafePath` and rejecting `Format-LogSafeText` there.

## Verification

- `git diff --cached --check` completed successfully before commit.
- Command: `pwsh -NoProfile -Command "Invoke-Pester ./eng/docker-compose/tests/BootstrapSchemaDeploymentSafety.Tests.ps1 -Output Detailed"`
- Result: **356 passed, 0 failed, 8 skipped, 0 inconclusive, 0 not run** in 317.84 seconds.
- The regression test `uses the path-safe formatter for the prepared workspace log` passed.

## Concerns

- The first sandboxed attempt could not create Pester's temporary `HKCU\Software\Pester` registry key and executed no assertions (`SecurityException: Requested registry access is not allowed`). Re-running the identical command with permission completed successfully with the result above.
