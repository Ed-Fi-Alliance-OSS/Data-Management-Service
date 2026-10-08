# CDC API E2E scenario execution and report (version 1)

`Given_CdcApiE2E` is the single explicit, nonparallel NUnit entry point in category
`CdcApiE2E`. It owns one `CdcApiScenarios` and one attached context for the entire
sequence. Scenario methods are separate from the NUnit entry point. There are no
scenario selectors, resets between phases, retries, or report-resume modes.

| ID | Method / scenario |
| --- | --- |
| CDC-E2E-01 | Student CRUD |
| CDC-E2E-02 | SchoolTypeDescriptor CRUD |
| CDC-E2E-03 | Materialized N overlapping API N+1 |
| CDC-E2E-04 | Delete before first projection |
| CDC-E2E-05 | Online cache rebuild |
| CDC-E2E-06 | Independent executor restart |
| CDC-E2E-07 | Temporarily unavailable offset evidence |
| CDC-E2E-08 | Terminal source-history loss (always last) |

Full qualification requires all eight scenarios to pass through the `ApiE2E`
setup/test/teardown/export path on both PostgreSQL and SQL Server. Focused helper
tests provide development coverage. The [scenario index](../../../../../reference/cdc-documentation/cdc-inv-evidence.md#api-to-kafka-scenario-traceability-dms-1325)
maps each flow to its invariants. Store exported reports outside the repository and
record their tested revisions and artifact references in the relevant PR or release record.

The runner supplies these environment variables:

- `CDC_API_E2E_HANDOFF_PATH`: private admitted-stack handoff produced by the wrapper.
- `CDC_API_E2E_REPORT_PATH`: absolute path to a **nonexistent** report in an existing,
  writable results directory. The fixture rejects an existing file rather than
  reusing evidence from an earlier invocation.
- `CDC_API_E2E_INVOCATION_ID`: nonempty UUID in `D` format (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`).

The fixture alone writes the UTF-8 JSON report. It initializes the invocation and
all eight IDs before attachment, including when execution is already cancelled.
Each complete snapshot is written to an adjacent temporary file and atomically
renamed. The initial rename refuses to overwrite; subsequent renames replace only
this invocation's report. No runner-owned fields are read, merged, or preserved.

The case-sensitive version 1 schema is:

```json
{
  "Version": 1,
  "InvocationId": "11111111-1111-1111-1111-111111111111",
  "Identity": { "Provider": "", "BindingId": "", "Generation": 0 },
  "AttachmentBoundary": "None",
  "Attachment": { "Id": "Attachment", "Outcome": "NotRun", "Failure": "None" },
  "Disposal": { "Id": "Disposal", "Outcome": "NotRun", "Failure": "None" },
  "Scenarios": [
    { "Id": "CDC-E2E-01", "Outcome": "NotRun", "Failure": "None" },
    { "Id": "CDC-E2E-02", "Outcome": "NotRun", "Failure": "None" },
    { "Id": "CDC-E2E-03", "Outcome": "NotRun", "Failure": "None" },
    { "Id": "CDC-E2E-04", "Outcome": "NotRun", "Failure": "None" },
    { "Id": "CDC-E2E-05", "Outcome": "NotRun", "Failure": "None" },
    { "Id": "CDC-E2E-06", "Outcome": "NotRun", "Failure": "None" },
    { "Id": "CDC-E2E-07", "Outcome": "NotRun", "Failure": "None" },
    { "Id": "CDC-E2E-08", "Outcome": "NotRun", "Failure": "None" }
  ]
}
```

`Outcome` is exactly `NotRun`, `Running`, `Passed`, or `Failed`. `Failure` is
exactly `None`, `Error`, `Cancelled`, `TimedOut`, or `Unimplemented`. Updates mark
attachment and each entered phase `Running` before calling it, then `Passed` on
completion. The first failure marks that stage `Failed`; subsequent scenarios
remain `NotRun`. Unimplemented methods throw and are never counted as passed.
Process termination may leave `Running` or `NotRun`, which is incomplete evidence.

Successful attachment fills `Identity` from the retained admitted binding:
`Provider` is `Postgresql` or `Mssql`; `BindingId` is lowercase hex SHA-256 of
`System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(binding.ToCompleteBindingIdentity())`
using default options; `Generation` is the retained generation. Empty strings and
zero mean unavailable identity before or after failed attachment. The opaque hash
avoids publishing deployment, tenant, datastore, connector, topic, or source names.
No settings, document bodies, paths, raw exception messages, or inner exceptions
are included. NUnit receives only the original failing stage and a fixed failure
category, even if subsequent cleanup also fails. Known attachment failures also carry
`AttachmentBoundary`: `Handoff`, `ProvenanceOrHttpConfiguration`, `RetainedBinding`,
`HttpEndpoints`, `Provider`, `KafkaAdvertisedEndpoints`, `Connect`, `Metrics`,
`RuntimeIdentitySchema`, or `ApiAuthentication`. `None` means no known boundary
(including cancellation before attachment begins or before the bounded drain finishes). NUnit
appends the known boundary to `CDC_API_Attachment_<category>`. Export validates the
exact allowlist and retains `AttachmentFailure` and `AttachmentBoundary` even when
binding identity is unavailable; this never qualifies an incomplete run. Earlier
version-1 reports without the field default to `None`.

Execution has a 45-minute deadline and observes NUnit cancellation. Scenarios must
honor cancellation and await their operations. Cancellation callbacks, draining
outstanding work, and local disposal share one independent 10-minute shutdown
budget, covering the five-minute gate and two-minute pager drains with room for
runtime/transport cleanup. Each of two final report writes has its own 30-second
bound. Budget up to 11 minutes for finalization,
plus initialization and the runner's governed infrastructure teardown reserve.
Disposal is attempted after attachment failure as well as scenario failure.
Outstanding work that does not drain makes disposal fail even if resource disposal
returns; disposal timeout/failure always fails qualification. Final report-write
failure also fails the test. A cleanup/report-write error does not replace the
original scenario failure. Hard process termination cannot guarantee finalization;
the runner must reject the resulting missing/incomplete evidence.

The runner validator and evidence exporter consume this same schema. The same export
path adds bounded checkpoint diagnostics without changing outcome ownership.
Qualification requires matching invocation/provider/binding identity, successful
attachment and disposal, and exactly the eight distinct ordered IDs all `Passed`
with `Failure: None`. Wrapper setup/teardown, process exit/TRX, and export outcomes
belong in the existing runner qualification summary and must independently pass;
they are never fields in this fixture-owned report.

## CRUD, overlap and delete-before-projection checkpoints

CDC-E2E-01 (Student) and CDC-E2E-02 (SchoolTypeDescriptor) share the same private CRUD
sequence and hold the designated projector during POST and PUT. The create checkpoint
compares the admitted target/source and configured schema, reads the resource over
HTTP, and requires matching durable work with no cache row. The update checkpoint
requires newer source/work versions while the complete old cache row remains.
After each release, cache publication and work acknowledgement precede consumption;
created state is consumed before PUT and updated state before DELETE. Deletion
requires absent source/cache/work rows and a keyed, record-level null tombstone.

One consumer retains state and all partition scan positions across these steps.
Every public record must match a known independent envelope or the allowed delete;
duplicates and older replay are reduced by the shared consumer. Held-work scans
reject premature publication. Each scan completes a provider fence before capturing
Kafka ends, classifies progress records as heartbeats, and separately checks broker
metadata for forbidden raw topics. Capture inventory and connector include lists
exclude projection work. Payload-free checkpoint output includes versions and
partition boundaries; the existing scenario report records the phase result.

CDC-E2E-02 changes `shortDescription` through the descriptor API while preserving
namespace/code identity. Its independent envelope uses the descriptor ResourceKey
metadata and no-link stream ETag; complete body comparisons reject injected links.
The consumer checks the same UUID key and binding-derived partition through create,
update, and the record-level null tombstone. Each CRUD phase starts from current
Kafka ends on the existing binding; no database or capture state is reset between them.

CDC-E2E-03 holds the actual Student N candidate at the production writer boundary,
commits N+1 through HTTP, then releases only N's write. The gate records the real
provider result and blocks the next processor call before its fast-path writer.
While blocked, source/work must require N+1 and cache must remain absent or older.
Only after those assertions does the scenario release processing, await cache/work
convergence, and consume the independently expected N+1 envelope through provider
fences and every Kafka partition boundary. N need not appear in Kafka. An API delete
and fenced consumption clean up the scenario resource. Payload-free checkpoints
appear in materialized-N, API-N+1, N-completed, next-blocked, converged order. The
fixed runner accounts for this phase using the existing CDC-E2E-03 report entry.

CDC-E2E-04 creates a Student with its first work item held before the production
processor. It observes matching durable work, no cache row and the current HTTP
body, then deletes through HTTP and verifies canonical/cache/work absence. A
provider-fenced scan must consume the keyed record-level null tombstone before
releasing the gate. Its partition/offset checkpoint precedes the executor release
and drain checkpoint. After the held production call drains, a second provider
fence and bounded scan verify no resurrection, followed by fresh database absence
checks. Both scans retain one consumer and all partition positions; every upsert
is rejected, including transient upsert-then-delete pairs. Replayed tombstones and
compacted gaps are allowed. An empty scan cannot satisfy initial deletion evidence.
The existing CDC-E2E-04 report entry records the result.

## Online rebuild checkpoints

CDC-E2E-05 publishes and consumes three Students before invoking the supported online
rebuild through the current fixture-owned runtime. The internal forwarding method
uses that runtime's command provider; the public runtime interface is unchanged.
Production preflight, mutex, baseline seeding and administrative drain own the rebuild.
A local observation-sink decorator retains completed phases, including committed
Resetting/Rebuilding transitions, verified cache clearing and the explicit baseline
scan. It delegates ordinary observations to the production store and retains no payloads.

After completion, read-back requires Tracking, a clear cache-ahead latch, empty work,
unchanged canonical metadata and ChangeVersionSequence, and independently expected
cache contents. The same multi-key consumer and partition positions span the operation:
equal-version replay is permitted, every tombstone is rejected, and all live keys remain.
Provider fences precede Kafka bounds; source continuity, binding/generation, capture
inventory and advancing offsets are checked. Canonical API deletion and fenced
consumption clean up only after the measured rebuild.

## Executor restart checkpoints

CDC-E2E-06 publishes/consumes a Student and completes API reads/writes before fully
stopping the designated runtime. With disposal complete and HTTP DMS still running,
it creates five distinct Students, updates one, and verifies successful GETs leave
matching durable work and absent cache rows. A provider-fenced scan excludes public
publication during this outage. No HTTP restart, capture reset or re-admission occurs.

The replacement uses the same production factory with page size two. Observations
are installed before initialization and belong only to this runtime, excluding the
earlier explicit rebuild. A provider-pager decorator records actual returned work
pages and holds the second nonempty page. The first page must already be published
and acknowledged. While this second page is held, API PUT/POST/GET succeed and leave
stale/absent cache plus new durable work. The hold must still be active when those
requests complete. Release delegates the unchanged page to production processing.

All seven keys must converge in cache and the retained Kafka consumer, including the
outage update and drain mutations. At least four actual pages must cover every key.
The bounded recorder counts baseline-boundary, baseline-seeding and inventory-scrub
primitive invocations, including failed calls; all must remain zero through recovery.
Bounded status existence probes are not canonical/cache inventory scans. Earlier
rebuild observations cannot satisfy or contaminate this evidence. Controller status
resolves the replacement, checks healthy same-binding continuity, and API deletes
clean up after convergence. Page holds honor finite deadlines/cancellation; disposal
cancels and awaits in-flight provider calls. UTC interval checkpoints, page counts,
operation counts and Kafka boundaries contain no document payloads.

## Unavailable continuity evidence checkpoints

CDC-E2E-07 publishes and consumes a Student and requires healthy readiness before
one bounded fault interval. A test-only forwarding transport throws only from
`ReadOffsetEvidenceAsync`; all other calls still reach the real attached transport.
Production status must report Unknown continuity and NotReady, and both managed
Restart and Resume must reject with Connect/Unavailable and the missing ConnectOffset
fact. Each observation must actually hit the injected read. Other pre-start
prerequisites remain satisfied. Missing source-partition/streaming proof also prevents
configuration validation and snapshot-status resolution; independent real read-backs
require unchanged connector configuration and RUNNING tasks so unrelated failures
cannot satisfy these assertions. Connector configuration is never printed.

During the same fault interval, API PUT/GET succeed while the designated runtime's
gate holds matching durable work and the complete old cache envelope. Fresh reads
through the production binding-state service require the original complete binding
and no terminal incident. The transport restores delegation in finally, including on
failure, deadline or cancellation; the gate also releases in finally. Managed Resume
then uses that same runtime and must return fresh healthy readiness. Both the in-fault
write and a subsequent API mutation must converge to independently expected cache and
Kafka state on the same binding/generation/topic. Provider fences, retained consumer
positions, full-record assertions and final API deletion reuse existing helpers.
No absence-of-publication or strict native-recovery fencing guarantee is inferred.
Focused helper tests cover the fault and evidence assertions.

CDC-E2E-08 is the final phase on that same binding. It publishes and consumes a
Student, requires fresh healthy continuity, then stops the real connector, verifies
zero tasks, deletes committed offsets exactly once and reads authoritative `Missing`
evidence. Production status must detect `ConnectOffsetMissing`, durably latch
`SourceHistoryContinuityLost`, report lost/NotReady and verify connector containment.
Fresh controllers and independent state-store reads must retain the exact incident.
The bounded offset decorator offers previously captured healthy streaming evidence
while managed restart/resume still reject before any connector start effect; reads
may short-circuit on retained loss. Real task status and containment are never faked.
The decorator is removed in `finally`, and real offsets must still be missing.

A final HTTP PUT/GET succeeds after containment while the designated executor is
held: canonical/work versions advance and the complete older cache remains. The
executor is then cancelled/disposed with work retained. No offset restoration,
capture recreation, incident clearing, connector resume or post-loss Kafka fence is
attempted. The terminal binding remains for the runner's governed teardown.

## Development checkpoints

Use the `ApiE2E` runner for development checkpoints and final provider qualifications,
with a fresh results directory on each invocation:

```powershell
pwsh ./eng/ci/Invoke-CdcQualification.ps1 -Lane Postgresql -Suite ApiE2E -ResultsDirectory TestResults/cdc-api-e2e-postgresql-checkpoint-01
pwsh ./eng/ci/Invoke-CdcQualification.ps1 -Lane Mssql -Suite ApiE2E -ResultsDirectory TestResults/cdc-api-e2e-mssql-checkpoint-01
```

Run providers serially in the shared Compose workspace and complete governed
cleanup before the next setup. A checkpoint is useful when resolving an integration
risk; it is not required after every scenario. Every run starts at CDC-E2E-01.
The first unfinished scenario fails normally, later phases stay `NotRun`, and the
runner still performs governed teardown. Earlier passed phases are useful progress
evidence, but every incomplete run fails qualification. There is no separate
direct-development setup or reporting path. Bounded diagnostics use the same export path.

## Runner preparation and failure handling

Use Linux, .NET 10, PowerShell 7.5 or later, Docker/Compose, and
`CDC_RUNBOOK_OWNED_STACK=1` on an exclusively owned disposable local workspace.
Supply `CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE` matching the checked-in qualified
image and the selected `CDC_CONNECTOR_TEMPLATE_POSTGRES_IMAGE` or
`CDC_CONNECTOR_TEMPLATE_SQLSERVER_2025_IMAGE`. The wrapper uses the shipped pinned
Apache Kafka broker. `-PullImages` pulls prerequisites before preparation.

The runner verifies both Compose projects have no containers, volumes, networks,
retained deployment inventories or bootstrap workspace. It refreshes selected and
Debug SchemaTools builds and requires both implicit and explicit wrapper resolution
to select the refreshed in-repo Debug executable. Private environment/credentials,
base configuration and restricted role/login preparation use the shared runbook
fixture helper. The shipped setup wrapper creates and admits the fresh target;
the runner never provisions it separately. HTTP and runtime attachment use the
returned handoff and retained settings. Host database name and complete binding
identity are resolved with the production configuration loader and state store.

`qualification.json` is the runner-owned atomic stage summary. Its invocation UUID
is persisted before prerequisites and passed to the fixture. Setup has a shared
30-minute budget, tests have 60 minutes (including the fixture's 45-minute deadline
and finalization), governed teardown gets an independent 15 minutes, and export
gets an independent 2 minutes. Reserve at least 110 minutes after workflow
prerequisites. An OS kill can prevent finalization; incremental reports and the
private ownership marker remain useful, but incomplete runs fail qualification.

After partial infrastructure startup, cleanup requires the pre-start ownership
marker and uses only the local project teardown primitive. Once deployment inventory
exists, the standard governed teardown wrapper owns retirement. Cleanup verifies
resources are gone before archiving retained bootstrap inputs and removing the
fixture's binding state last. Incomplete cleanup preserves provenance for diagnosis.
The original execution failure and cleanup/export failures have separate fields.

Only allowlisted identity/stage/phase data and sanitized test results are exported.
Private settings, credentials, handoff, ownership inventory and raw logs stay under
the runner's private temporary directory; never upload that directory. The runner
does not write the fixture's scenario report. Every phase, attachment, disposal,
setup, teardown, process/TRX result and export must pass to return zero.

### Bounded diagnostic export

`scenario.json.checkpoints` is a private, fixture-owned journal whose first line is
its invocation UUID. Completed checkpoints are flushed individually; process death
may leave a partial final line, which the exporter rejects. The journal is limited
to 2,048 lines of 512 characters; exceeding either limit fails the emitting scenario.
It contains no outcomes and does not replace `scenario.json`. The exporter selects
fixed checkpoint names and strictly typed values, preserves their order, and records
missing, oversized, rejected or wrong-invocation evidence explicitly.

Before retiring owned containers, cleanup captures only Compose service roles,
actual image IDs and configured manifest digests. Collection uses ten-second calls,
at most eight containers, and cannot prevent governed teardown. Private resource
names, environment variables and complete image configuration are never exported.
Resource counts and verified absence supplement the runner's teardown outcome.

ApiE2E publishes only `cdc-api-e2e.json`; it does not recursively export arbitrary
attachments or TRX output from the private directory. This includes bounded copies
of existing stage/process/scenario results, separate failure categories, traceability,
runtime inputs and checkpoint data. See the [evidence index](../../../../../reference/cdc-documentation/cdc-inv-evidence.md#api-to-kafka-scenario-traceability-dms-1325)
for precise invariant scope. Diagnostic entries never establish qualification alone.
