# T23 blocked: HTTP-only rollout after successful admission

## Latest follow-up: 2026-09-28

The registration-read repair is implemented in the working tree. The controller
retries only transient Connect configuration-read unavailability and timeouts,
using the existing poll interval, per-call timeout, and original registration
deadline. It preserves configuration validation and durable registration intent
and never repeats connector creation. Focused registration tests: **244 passed**,
zero skipped, including transient reads after a lost creation response, call
timeout, persistent failure, cancellation, and configuration drift.

One subsequent PostgreSQL qualification used the same pinned inputs below and
`-ResultsDirectory TestResults/cdc-api-e2e-postgresql-registration-retry`.
Invocation `feee8b1f-4d31-4721-8ad9-e678a35d8b1c` passed admission, reached a healthy
DMS host, then failed in `Invoke-E2ECdcApiRollout` before handoff publication.
The setup diagnostic reports `RetainedForGovernedTeardown` with no typed failure
codes; that value is set only after the admitted bootstrap returns successfully.
The precise rollout failure is not preserved by the wrapper's generic exception.
This is a separate blocker requiring investigation; no rollout changes were made.

The rebalance race did not recur in the inspected live worker logs, so this run
does not independently exercise the new retry. The regression tests do.
Setup **Failed**, tests **NotRun**, teardown **Passed**, export **Passed**; all
five owned containers and three volumes were removed. T23 remains incomplete and
T24 remains unattempted. Sanitized reports are in the results directory above.
Private diagnostics are at
`/tmp/cdc-qualification-3be5a9ebb2d943a8b8532058e321a1c6`; do not upload them.

## Previous blocker: transient Connect rebalance aborts initial admission

The following records the earlier failure, before the registration-read repair.

Story: `reference/design/backend-redesign/epics/19-cdc-kafka/06-e2e-kafka-scenarios.md`
(DMS-1325). T23 remains **incomplete**; T24 was not attempted.

The implementation loop requires stopping when an E2E blocker is outside this story.
The latest PostgreSQL qualification failed in the existing managed CDC admission
path, before HTTP handoff, fixture attachment, or any API scenario. Fixing initial
connector-registration reconciliation/retry belongs to the existing CDC bootstrap
controller, not this story's API scenario coverage. No admission bypass or automatic
retry was introduced, and no required result is counted as passing.

## Reproduction and inputs

Tested revision: `850ebb883` (full revision in the accompanying run details).
From the repository root on an exclusively owned, initially absent disposable stack:

```bash
CDC_RUNBOOK_OWNED_STACK=1 \
CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE='edfialliance/ed-fi-kafka-connect@sha256:13d9afb3ee322bae4f9cc1939b32079e99543ca27d48cb908b1cebfcdb60546d' \
CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE='postgres:16.8-alpine@sha256:951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0' \
pwsh ./eng/ci/Invoke-CdcQualification.ps1 -Lane Postgresql -Suite ApiE2E \
  -ResultsDirectory TestResults/cdc-api-e2e-postgresql-retry3
```

Images were already present at the exact pinned digests; no `-PullImages` was needed.
Host SDK: .NET 10.0.102; PowerShell 7.6.6. Broker: the runner's pinned Apache Kafka
3.9.0 image, digest `sha256:fbc7d7c428e3755cf36518d4976596002477e4c052d1f80b5b9eafd06d0fff2f`.
Actual running image IDs and governed cleanup counts are in the sanitized evidence.

## Observed failure

The claims gate passed all nine verifiable checks, CMS loaded 20 claim sets, and
schema provisioning completed. The retained workflow reached `RegisterConnector`;
its registration intent was persisted but had no completion receipt. Worker logs
were inspected before governed teardown removed the container:

| UTC on 2026-09-28 | Observation |
| --- | --- |
| 08:42:39.746 | `POST /connectors` returned **201**. |
| 08:42:39.760 | Connector configuration GET returned **200**. |
| 08:42:39.795 | `RebalanceNeededException`: request cannot complete because a rebalance is expected. |
| 08:42:39.796 | Next connector configuration GET returned **500**. |
| 08:42:39.799 | Worker completed the next assignment, including the connector task. |
| 08:42:41.401 | Governed connector stop returned **204**. |

The wrapper reported `Connect/Unavailable`, stopped the admitted infrastructure,
and refused to launch tests. This is a transient admission-time read-back failure,
not one of CDC-E2E-07/08's intentional faults. The scenario report is absent because
the test process never launched. This race may not reproduce on every fresh run.

Investigate the production registration and configuration-observation path in
`CdcConnectRestAdapter` and its controller caller. `ReconcileConfigurationAsync`
currently transfers an unavailable configuration read to the caller; a repair
must preserve bounded deadlines, durable registration intent, exact configuration
validation, and ambiguous-write reconciliation. Do not blindly resubmit connector
creation, reset offsets, relax readiness, or retry until a green qualification hides
the failure. Add focused admission regression coverage before resuming T23.

## Completed prerequisite corrections

- `61e67f530`: native process helper now accepts the runner's 30/50/15-minute
  budgets. Qualification and native transport tests: **272 passed**, zero skipped.
- `c37a7a444`: restore the E2E module's `bootstrap-cdc` reference after admission's
  forced module reload. Workflow tests: **95 passed**, zero skipped, including
  scoped reload before all provider/project/identity rollout combinations.
- `850ebb883`: prepare connector credentials with database-only startup. Starting
  CMS before claims staging seeded incomplete claims that CMS correctly preserved
  on restart. Qualification tests: **250 passed**, zero skipped. The latest live
  attempt passed the previously failing claims gate.

All three commits passed PowerShell analysis and diff checks. The handoff correction
has offline regression evidence; the latest attempt did not reach live handoff.
No authoritative schema inputs, mappings, or mapping version were modified.

## Evidence and cleanup

[Sanitized evidence](reference/cdc-documentation/evidence/dms1325-t23-postgresql-blocked/qualification.json)
records invocation `badb5f80-7515-4fe8-ae4c-cfefb2d9c1ff`:
setup **Failed**, test **NotRun**, teardown **Passed**, export **Passed**.
[Runtime evidence](reference/cdc-documentation/evidence/dms1325-t23-postgresql-blocked/cdc-api-e2e.json)
confirms governed cleanup of four containers and three volumes, with owned resources
absent. Unrelated containers were untouched. Zero scenarios passed; the runner's
eight failed required outcomes are missing qualification evidence, not eight
executed test failures.

Private diagnostics remain only at
`/tmp/cdc-qualification-09267ab8f42045f1bef28f41e8326fc8`.
Earlier attempts remain under their separate `TestResults/cdc-api-e2e-postgresql*`
directories and are described in `progress.txt`. Do not commit/upload private
directories: they contain credentials and raw settings. Resolve this blocker and
remove this stop file before another implementation-loop iteration.
