# T37 qualification complete — environment blockers resolved

## Current status — 2026-09-09

The Docker Desktop environment blocker below is resolved. Docker Desktop was
removed and replaced with native Docker Engine 29.8.0 on Ubuntu. The host test
processes and provider containers now share the host kernel clock. At the user's
request, the old Desktop containers, images, volumes, and recovery archive were
deleted; no old Docker resources remain to clean up. Portainer is unrelated to
the qualification fixtures and must be preserved.

The partial implementation is committed in `433dc7b92`. T37 is now complete.
All three previously outstanding full suites passed against Engine in new
evidence directories:

- PostgreSQL RecordSize: `/tmp/dms-t37-engine-pg-recordsize`, passed 16/16,
  zero skips. The historical future-observation failure did not recur.
- SQL Server Admission: `/tmp/dms-t37-engine-sql-admission`, passed 36/36,
  zero skips.
- SQL Server RecordSize: `/tmp/dms-t37-engine-sql-recordsize`, passed 16/16,
  zero skips.

The evidence audit exposed a reporting defect: generic source-identifier
redaction also redacted generated history attachment filenames in TRX files.
The exporter now preserves only the generated numeric NUnit ID/GUID history
basename format in `ResultFile` paths, after stripping directories. A regression
test proves correlation is preserved while sensitive paths remain redacted.
Retained passing history/cleanup raw reports were re-exported, not rerun, into
`/tmp/dms-t37-engine-pg-history-reexport` and
`/tmp/dms-t37-engine-sql-history-reexport`; original reports remain untouched.

Fresh Contract validation passed 3,295 cases with zero skips at
`/tmp/dms-t37-engine-contract`; CI guard tests passed 443/443. The missing-image
probe at `/tmp/dms-t37-engine-missing-prerequisite` failed closed with
`EnvironmentUnavailable`, not skipped or passing evidence. CSharpier, actionlint,
and the repository's race-safe PSScriptAnalyzer wrapper passed.

The final selected qualification matrix contains 3,535 passing cases, zero
failures, and zero skips across 20 suite reports (the 14 CI jobs). Audit:
`/tmp/dms-t37-qualification.json`. All 435 audited JSON/TRX files passed the
sanitization and attachment-correlation audit; 3,353 TRX cases plus 182 wrapper
cases reconcile to the selected total. The local audit selects only complete
passing suites and retains superseded invocation outcomes separately.

All task-owned containers, networks, and volumes were cleaned up by the
fixtures. The final Docker inventory contains only Portainer and its resources,
plus Docker's built-in networks. No remaining task blocker was found.

Unaffected earlier passing provider and Kafka reports remain selected. The
sections below retain historical results; the audit identifies the final
selections. Failed and interrupted reports are retained, not promoted to passing
evidence. No production timing or future-observation contracts were weakened
for this rerun.

## Historical blocker — resolved

Story: `reference/design/backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md`.
Task: T37, repository CDC qualification and test-to-design evidence index.
Recorded: 2026-09-09, approximately 14:30 UTC.

At the time of this historical report, implementation was staged and no task
commit had been made.
The implementation-loop prompt requires stopping when an integration/E2E blocker
is determined to be outside this story's scope. Repairing Docker Desktop's VM
filesystem service and host/VM clock synchronization is outside that scope.
Authoritative input fixtures were not modified.

## Blocking evidence

1. The final host-driven PostgreSQL record-size suite completed 15/16 cases,
   with zero skips. The failed case was
   `It_requires_renewed_confirmation_at_every_interrupted_boundary(5,True)`.
   Final projection validation rejected a database observation timestamp later
   than the host test process's read-return timestamp:
   - projection returned: `2026-09-09T14:14:55.0007891Z`;
   - database durable observation: `2026-09-09T14:14:55.001028Z`;
   - difference: approximately 0.239 ms in the future.
   PostgreSQL supplies `statement_timestamp()` from Docker Desktop's VM clock;
   the .NET test process ran on the host clock. Other samples also showed small
   positive differences. The retained projection evidence was tracking,
   operational, caught up, source-matched, and did not require recovery. The
   production future-evidence rejection was preserved.
2. A same-clock runner was attempted inside Docker Desktop's VM, using the local
   `mcr.microsoft.com/dotnet/sdk:10.0` image (SDK 10.0.302), host networking, and
   the VM Docker socket. Preliminary probes verified PowerShell, Docker API
   access, and the PostgreSQL published port. The container was named
   `dms-t37-clock-qualification`. Its qualification invocation produced no report
   before Docker Desktop lost its engine connection.
3. At `2026-09-09T14:27:47Z`, Docker Desktop logged:
   `FATAL: running services: running fs: injecting event blocked for 60s`.
   The log also records that dockerd was explicitly stopped and exited cleanly,
   followed by `all processes have shutdown`. Subsequent proxy calls waited for
   `/run/guest-services/docker.proxy.sock`, which no longer existed. A bounded
   `docker info` probe timed out with exit 124. This is an engine/VM failure,
   not an assertion failure in the CDC implementation. Subsequent host-kernel
   evidence identified `virtiofsd` being killed by seccomp with SIGSYS on the
   disallowed `tkill` syscall, before the filesystem event timeout. It was not
   attributed to an out-of-memory event.
4. This shutdown also interrupted the final SQL Server admission and record-size
   suites. Neither had a completed qualification manifest at interruption. Their
   partial fixture output is not accepted as passing qualification.

## Historical implementation and completed validation

Staged changes include the 14-job CI matrix and aggregate gate, fail-closed
qualification runner and sanitized reports, provider cleanup coverage, wrapper
and category guards, and `docs/CDC-QUALIFICATION.md`. Live testing also exposed
and corrected initial Kafka metadata propagation, established PostgreSQL
observation isolation, and the bounded zero-task UNASSIGNED transition after
record-size configuration updates. Regression cases cover those production fixes.
Fixture budgets and safe bounded diagnostics were adjusted based on retained
failures; production freshness and future-observation checks were not weakened.

Completed selected suites passed 3,466 cases in total, with zero skips:

| Suite | Passed | Evidence directory |
| --- | ---: | --- |
| Contract: controller unit, CLI, offline, wrappers | 3,294 | `/tmp/dms-t37-contract-qualified` |
| Kafka secured and explicit local | 48 | `/tmp/dms-t37-kafka-1g` |
| PostgreSQL admission | 30 | `/tmp/dms-t37-pg-admission-1g` |
| PostgreSQL lifecycle | 9 | `/tmp/dms-t37-pg-lifecycle-1g` |
| PostgreSQL recovery | 9 | `/tmp/dms-t37-pg-recovery-fixed` |
| PostgreSQL telemetry (only selected report in this directory) | 1 | `/tmp/dms-t37-pg-final` |
| PostgreSQL history and provider cleanup | 28 | `/tmp/dms-t37-pg-history-cleanup` |
| SQL Server lifecycle | 9 | `/tmp/dms-t37-sql-lifecycle-bounded` |
| SQL Server recovery | 9 | `/tmp/dms-t37-sql-recovery-bounded` |
| SQL Server telemetry | 1 | `/tmp/dms-t37-sql-telemetry-1g` |
| SQL Server history and provider cleanup | 28 | `/tmp/dms-t37-sql-history-cleanup` |

The full intended matrix still lacks passing complete PostgreSQL RecordSize
(16), SQL Server Admission (36), and SQL Server RecordSize (16) reports.
CI guard tests passed 246 cases; actionlint, PSScriptAnalyzer, CSharpier, and
staged diff checks passed. Retained failed/interrupted runs remain separate
from selected evidence. No documentation/help/evidence-index tests were added.

## Historical retained diagnostics and continuation

- Environment excerpt: `/tmp/dms-t37-environment-blocker/docker-desktop-excerpt.log`.
- Failed PostgreSQL run: `/tmp/dms-t37-pg-recordsize-transition`;
  private raw root: `/tmp/cdc-qualification-3bd738113b694d748487aa51781b5320`.
  Relevant admission attachment: `admission-evidence-679dba186cf94c8d9a9bef4ed3a5fc6b.json`.
- Attempted same-clock runner: `/home/brad/.cache/dms-t37-clock-runner`;
  `private-runner.log` records the Docker wait failure. No successful runner
  qualification manifest exists.
- Interrupted SQL admission: `/tmp/dms-t37-sql-admission-bounded`;
  raw root `/tmp/cdc-qualification-2ebb1d7233d744e8b578fc2017192f80`.
- Interrupted SQL record-size: `/tmp/dms-t37-sql-recordsize-bounded`;
  raw root `/tmp/cdc-qualification-1faf3dff1611409b8d4738bf72e61ca6`.
  Both result directories contain an explicit `interrupted-invocation.json`.
- Local continuation notes: `/tmp/dms-t37-current.md` and
  `/tmp/dms-t37-handoff.md`; this document supersedes their earlier active-run
  status. `/tmp/dms-t37-audit.py` still points to the failed PostgreSQL run and
  must not be interpreted as a completed qualification audit.

The owned host qualification processes were interrupted and diagnostic sampling
was stopped. Docker cleanup could not be verified with the engine unavailable.
After engine recovery, inspect and remove only task-owned resources: admin
containers `dms-t37-history-pg` and `dms-t37-history-sql`, the clock runner, and
remaining test-owned `dms-cdc-template-*` resources identified from private run
logs. Preserve unrelated resources and pre-existing images. Private logs may
contain sensitive fixture details and must not be published.

Resume with a healthy Docker engine and a test/database environment sharing a
clock, preferably the normal Linux CI arrangement. Run the three outstanding
full suites into new evidence directories; do not overwrite failed evidence or
substitute partial passing cases. Then update the local audit selections,
verify zero skips and artifact sanitization, finish cleanup, mark T37 complete,
append `progress.txt`, and commit. Do not weaken time-validation contracts to
work around the local clock mismatch.
