# CDC Operator References

Start with the [operations runbook](operations-runbook.md#prerequisites) for deployment
prerequisites and procedure conventions. This reference set is being delivered in
DMS-1326: configuration, [monitoring/incident routing](operations-runbook.md#monitoring),
[local provider setup and stop/restart](operations-runbook.md#local-setup),
and the [projection/repair handoff](operations-runbook.md#projection-repair-handoff)
are available; live provider replay and recovery authoring remain pending in the
[evidence index](cdc-inv-evidence.md).
Pending work is not evidence that an operator procedure has passed.

- [CDC evidence index](cdc-inv-evidence.md): manual reviews, exact behavior-test identities,
  provider results, sanitized captures, and outstanding verification owners.
- [DocumentCache runbook](../document-cache-documentation/operations-runbook.md) and
  [E18 evidence](../document-cache-documentation/cdc-inv-evidence.md): projection queue,
  lifecycle, rebuild, scrub, and provider-prerequisite guidance.
- [Restamp boundary](../document-cache-documentation/operations-runbook.md#restamp-scope-boundary)
  and its [offline representation-correction owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#offline-byte-changing-representation-correction):
  separately owned correction work; cache rebuild does not restamp changed public bytes.
- [DocumentCacheAdmin CLI reference](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md):
  installation, syntax, confirmations, and exit codes.
- [CDC configuration catalog](../../docs/CONFIGURATION.md#datamanagementdocumentcachecdc):
  required values, defaults, credentials, timeouts, and precedence.

## Design owners

The [backend summary](../design/backend-redesign/design-docs/summary.md) places CDC in the
relational design. Follow these owners for authoritative contracts; this runbook does not
define another CDC protocol:

- [Configuration and targets](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#configuration-and-projection-target-selection),
  [initial admission](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence),
  and [local bootstrap](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci).
- [Readiness scope](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-readiness-scope),
  [continuity](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity),
  and [binding and physical-source identity](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding).
- [Projector and provider sources](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md),
  [topic/message contract](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md),
  and [consumer bootstrap](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#public-consumer-bootstrap).
- [Repair and deferred workflows](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-change-and-repair-operations)
  and [security, telemetry, and operations](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations).

## Verification handoff

During each authoring task, manually compare the procedure with generated CLI help,
implementation, configuration validators, renderer, wrappers, and available serialized
fixtures. Follow each new local file link and anchor from the containing document and
read command paths from the stated starting directory. Record findings and corrections
in the evidence index. There are no automated documentation tests, Markdown execution
tools, executable documentation catalogs, or link-checking tests in this workflow.

[T02 setup review](cdc-inv-evidence.md#t02-setup-review) records the selected bootstrap
behavior tests, generated help, and the unmet E19-06 API-consumer dependency.

T01's exact commands and captured results are in the
[foundation review](cdc-inv-evidence.md#t01-foundation-review). T02–T09 add their command
selections while authoring; [T03 monitoring results](cdc-inv-evidence.md#t03-monitoring-review)
include status/lag contracts and containment fixtures. Test selections must exercise product behavior independently
of prose; exclude documentation assertions from mixed Pester suites. Existing fixture
output can support authoring without a new live deployment. Capture output before
sanitizing, preserve contract fields, and identify the evidence layer.

[T12 operator-path results](cdc-inv-evidence.md#t12-operator-path-review) cover packaged
history rejection on both providers and the added CLI status contracts. T13 owns
adoption/replacement assertions;
T17 retirement assertions; T14 and T15 the PostgreSQL and SQL Server live exercises,
respectively. Each requires actual selected test identities and nonzero execution counts.
Unmet prerequisites and skips stay visible. Provider exercises use isolated disposable
targets and run sequentially when they share the local Compose stack.

T16 performs one final manual review after those results exist:

1. Reconcile each runnable anchor's syntax, settings, outcome/exit code, retry disposition,
   and sanitized JSON with its captured evidence and design owner.
2. Follow all local links and anchors, including evidence artifacts and E18 handoffs.
3. Confirm both provider exercises have actual results, with mocks, provider access,
   broker/API behavior, skips, and missing prerequisites distinguished.
4. Check secret/payload redaction and unsupported promises: production capacity, platform
   purge, remote state storage, runtime writer gates, and deferred baseline/re-enablement
   workflows cannot be claimed from small fixtures or component cleanup.
5. Record verified revision, tool/image versions, corrections, results, and cleanup
   disposition in the evidence index. Run `git diff --check` for the final changes.
