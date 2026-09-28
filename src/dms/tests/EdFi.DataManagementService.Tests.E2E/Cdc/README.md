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
category, even if subsequent cleanup also fails.

Execution has a 45-minute deadline and observes NUnit cancellation. Scenarios must
honor cancellation and await their operations. Finalization uses independent
30-second bounds for cancellation callbacks, draining outstanding work, local
disposal, and each of two report writes. Budget up to 150 seconds for finalization,
plus initialization and the runner's governed infrastructure teardown reserve.
Disposal is attempted after attachment failure as well as scenario failure.
Outstanding work that does not drain makes disposal fail even if resource disposal
returns; disposal timeout/failure always fails qualification. Final report-write
failure also fails the test. A cleanup/report-write error does not replace the
original scenario failure. Hard process termination cannot guarantee finalization;
the runner must reject the resulting missing/incomplete evidence.

The runner validator and minimal evidence exporter consume this same schema. T19 extends
the same export path with detailed diagnostics.
Qualification requires matching invocation/provider/binding identity, successful
attachment and disposal, and exactly the eight distinct ordered IDs all `Passed`
with `Failure: None`. Wrapper setup/teardown, process exit/TRX, and export outcomes
belong in the existing runner qualification summary and must independently pass;
they are never fields in this fixture-owned report.

## Student and descriptor CRUD checkpoints

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
direct-development setup or reporting path. Detailed diagnostics arrive in T19;
they do not block implementing Student CRUD in T11.

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
30-minute budget, tests have 50 minutes (including the fixture's 45-minute deadline
and finalization), governed teardown gets an independent 15 minutes, and export
gets an independent 2 minutes. Reserve at least 100 minutes after workflow
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
