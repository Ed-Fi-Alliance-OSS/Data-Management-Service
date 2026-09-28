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

T21's runner validation and T19's evidence export consume this same schema.
Qualification requires matching invocation/provider/binding identity, successful
attachment and disposal, and exactly the eight distinct ordered IDs all `Passed`
with `Failure: None`. Wrapper setup/teardown, process exit/TRX, and export outcomes
belong in the existing runner qualification summary and must independently pass;
they are never fields in this fixture-owned report.

## Development checkpoints

T21 adds the `ApiE2E` runner dispatch. Once available, use the same commands as the
final provider qualifications, with a fresh results directory on each invocation:

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
