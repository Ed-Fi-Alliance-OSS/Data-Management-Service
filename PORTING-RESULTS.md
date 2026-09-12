# DMS-1324 port execution

## Scope and revisions

- Target: `/home/brad/work/dms-root/DMS-1324-port`, branch `DMS-1324-port`, no upstream.
- Replacement DMS-1323 base: `c3ae3bfffbd0a049abf7b8768eb4f14744a6bbaf`;
  local and remote refs agreed when the port began.
- Source delta: `45046c1d3a26927d93e94394f901731e0aaa12fe..bb3aecb98cd887f9b05a7cebccda1d978ce40c8b`.
  Source branch history remains at `1c1e3a1fbedfa5d4f572a67b9eaf8750c4b9eedd`, which adds the plan.
  The user's later plan clarification is the only source-checkout working change.
- Current implementation revision: `4af6fe5f576da8ab4bbbacb98095a91941b55329`.
  Both provider telemetry qualifications and all focused lifecycle repairs pass.
  Release publication with locked dependency restore and packaged discovery pass.
- Both solution restores, tool restore, and Husky installation passed. Release builds,
  packaged test publication, CSharpier, and staged PowerShell analysis passed.
- The revised 05 story is retained. The target 04 story and normative design tree have
  no diff. No production C# source or obsolete Control library was imported or changed.
- All 68 source-delta paths are accounted for in `PORTING-INVENTORY.md`, including the
  superseded workflow and Control-library changes and supplemental target test repairs.

## Qualification status

Full qualification is **complete**. All four required lanes pass: Contract 5840,
Kafka 49, PostgreSQL 604, and MSSQL 604, with no failed or skipped required cases.
Paths below are relative to this checkout. Focused runs supplement the complete lanes;
their counts overlap and should not be added to the lane totals.

| Check | Result | Evidence |
| --- | --- | --- |
| Independent consumer/fixture unit subset | 265 passed | `TestResults/port-independent-01` |
| Adapted focused admission subset | 672 passed | `TestResults/port-admission-03` |
| Complete message unit suite, including traceability | 957 passed | `TestResults/port-message-unit-01` |
| Full Contract lane with corrected evidence directory | 5840 passed: 4465 CDC unit, 715 CLI unit, 283 offline, 377 PowerShell | `TestResults/port-contract-02` |
| Pinned-image smoke, including both generated-schema restamps | 6 passed | `TestResults/port-smoke-final-01` |
| Corrected CLI fixture cases | 16 passed | `TestResults/port-cli-fixed-01` |
| Packaged unit and integration traceability, run outside the checkout | 20 passed per assembly | `/tmp/dms1324-port-packaged/results-unit`, `results-integration` |
| Final CI selection/export checks | 31 passed | `/tmp/dms1324-port-ci-evidence.log` |
| Full Kafka lane with corrected evidence directory | 48 secured + 1 local passed | `TestResults/port-kafka-03` |
| Full PostgreSQL lane | 604 passed across all eight reports; no failures, skips, or environment failures; process exited 0 | `TestResults/port-postgresql-02` |
| Full MSSQL lane | 604 passed across all eight reports; no failures, skips, or environment failures; process exited 0 | `TestResults/port-mssql-02` |
| PostgreSQL message contracts with corrected evidence export | 502 passed | `TestResults/port-postgresql-message-contract-02` |
| MSSQL message contracts with corrected evidence export | 496 passed | `TestResults/port-mssql-message-contract-02` |
| Fresh PostgreSQL full-lane admission phase | 30 passed; 60 exported evidence links resolve | `TestResults/port-postgresql-02/Postgresql-CdcControllerAdmission` |
| Fresh PostgreSQL full-lane managed lifecycle | 15 passed | `TestResults/port-postgresql-02/Postgresql-CdcControllerManagedLifecycle` |
| Corrected PostgreSQL telemetry fixture | 1 passed | `TestResults/port-postgresql-telemetry-fixed-02` |
| Corrected MSSQL telemetry fixture | 1 passed | `TestResults/port-mssql-telemetry-fixed-01` |
| Focused MSSQL lifecycle failures after timing correction | 3 passed | `TestResults/port-mssql-lifecycle-focused-01` |
| Corrected PostgreSQL persistent-backlog timing | 1 passed | `TestResults/port-postgresql-lifecycle-freshness-01` |
| PostgreSQL history and cleanup with corrected admin environment | 25 history + 5 cleanup passed | `TestResults/port-postgresql-history-fixed-02` |
| MSSQL history and provider cleanup | 25 history + 5 cleanup passed | `TestResults/port-mssql-history-01` |
| Fresh PostgreSQL full-lane native recovery phase | 9 passed | `TestResults/port-postgresql-02/Postgresql-CdcControllerNativeRecovery` |
| Fresh PostgreSQL full-lane record-size phase | 17 passed; 51 exported evidence links resolve | `TestResults/port-postgresql-02/Postgresql-CdcControllerRecordSize` |
| Fresh MSSQL full-lane admission phase | 36 passed | `TestResults/port-mssql-02/Mssql-CdcControllerAdmission` |
| Fresh MSSQL full-lane managed lifecycle | 15 passed | `TestResults/port-mssql-02/Mssql-CdcControllerManagedLifecycle` |
| Fresh MSSQL full-lane native recovery | 9 passed; 27 exported evidence links resolve | `TestResults/port-mssql-02/Mssql-CdcControllerNativeRecovery` |
| Fresh MSSQL full-lane record-size phase | 17 passed; 51 exported evidence links resolve | `TestResults/port-mssql-02/Mssql-CdcControllerRecordSize` |
| Fresh MSSQL full-lane telemetry | 1 passed | `TestResults/port-mssql-02/Mssql-telemetry` |
| Fresh MSSQL full-lane history and provider cleanup | 25 history + 5 cleanup passed; all 25 history evidence links resolve | `TestResults/port-mssql-02/Mssql-history`, `Mssql-provider-cleanup` |
| Final integration package traceability outside checkout | 20 passed | `/tmp/dms1324-port-packaged-verified/results-integration` |

Both complete provider lanes include the final message-contract suites: PostgreSQL 502
and MSSQL 496. The aggregate reports and every exported TRX attachment link were inspected.

The earlier complete MSSQL invocation (`TestResults/port-mssql-01`) finished with 601
passed and three lifecycle failures from before the repairs, with no skips. Its other
seven reports pass, including 496 message-contract tests. That invocation also predates
the evidence-directory fix and exports only one message-contract JSON file. It is retained
as diagnostic history; the current full invocation remains the acceptance run.

## Evidence boundary

- Shipped Connect image:
  `edfialliance/ed-fi-kafka-connect@sha256:13d9afb3ee322bae4f9cc1939b32079e99543ca27d48cb908b1cebfcdb60546d`.
- Message broker profile: `AuthorizationDisabledLocal`. Production-like ACL qualification
  is supplied separately by the existing secured Kafka lane.
- Focused admission uses `CdcConnectRestAdapter`, `ICdcConnectTransport`,
  `CdcControllerObservations.Offset`, and current provider adapters. Ownership, projection,
  source-history, and lag prerequisites remain explicitly synthetic. Optional lag
  percentiles are absent; these tests do not claim production writer admission.
- VSTest omits fixture-level attachments. The runner now sets `NUnit.WorkDirectory` to
  each suite's private results directory so the existing allowlisted exporter retains
  their JSON independently of TRX links. Raw assertion output and private logs are excluded.
- The first PostgreSQL message run passed 502 cases but exported only one attachment.
  Its replacement run exports all six expected evidence files: serialized failures,
  provider observations, consumer bootstrap, acknowledgement, record-size recovery, and
  replay. The corrected MSSQL run exports its five expected files (consumer bootstrap is
  provider-neutral and exercised by the PostgreSQL fixture). Both TRX attachment links
  resolve, and both `qualified-image.json` files match the shipped image.
- The complete PostgreSQL lane also retains all six message-contract evidence files.
  All 205 attachment links across its eight exported TRX reports resolve; its qualified
  image metadata exactly matches the shipped image definition.
- The complete MSSQL lane retains all five expected message-contract evidence files.
  All 217 attachment links across its eight exported TRX reports resolve; its qualified
  image metadata also exactly matches the shipped image definition.
- Candidate-tree checks before transfer passed 263 offline, 964 serialized, and 34 broker
  tests. These establish transfer compatibility; they are not substituted for final
  worktree qualification.

## Baseline findings and repairs

The unchanged target baseline (`TestResults/port-baseline-contract-01`) produced:
3502 passed / 4 failed CDC unit tests; 685 passed / 30 failed CLI tests; 92 passed
offline tests; and 346 passed / 25 failed PowerShell tests, with no skips.

The port repairs the following fixture problems, described in the transfer inventory:

- PostgreSQL admission already samples offsets after the slot. The old fixture expected
  another read after that fresh read had proved a gap. It now asserts ordering, resumability,
  and immediate containment; its mock reports a verified stop instead of waiting five minutes.
- Generated-schema restamps require identity-column metadata, SQL Server snapshot settings
  after database reset, and a descriptor-only slug resolver that rejects unexpected links.
- The secured Kafka startup fixture reacquired its held controller session. Its independent
  live policy check now uses the shared adapter inspection path.
- Unreserved retirement preserves possible exposure history. The CLI fixture now verifies
  that supported behavior rather than expecting rejection.
- Isolated PowerShell fixtures need actual Compose paths and the schema-package module,
  and optional hashtable keys must be read safely under strict mode.
- PowerShell 7.4.10 cannot preserve the absent-versus-empty environment distinction tested
  by the existing wrappers. Validation uses an isolated PowerShell 7.5.3 tool with runtime
  roll-forward to installed .NET 10. The user's global tool was not changed.

## Scenario reconciliation

- Unit manifest: 891 source scenarios become 937. There are 829 unchanged stable IDs,
  62 replacements for explicit fixture/test naming or argument changes, and 46 additional
  cases (40 lag-identity assertions and 6 numeric-string offset rejection assertions).
- Integration manifest: 1030 source scenarios become 1031. There are 1022 unchanged IDs,
  8 renamed cases matching qualified-image or focused-evaluator claims, and 1 additional
  unqualified-digest prerequisite case. No required scenario was dropped.
- Infrastructure traceability adds 20 tests per assembly and is explicitly excluded from
  scenario self-mapping. Both complete manifests pass executable packaged discovery.
- Every mapped scenario was matched by its full parameterized fixture and test name to
  a passing result in the complete Contract and provider lanes: 937 unit and 1031 integration
  scenarios, with none missing. This independently checks executed coverage beyond discovery
  and aggregate test counts.
- The shared CI runner and matrix select seven suites per provider plus `Kafka/All`, for
  15 nightly jobs. Targeted message selection includes both serialized and broker categories
  with the selected provider constraint applied to both.

## Acceptance audit

The stage gates below preserve the full plan scope. Passing message-contract evidence
is not substituted for affected controller qualification.

| Plan stage | Inspected evidence | Status |
| --- | --- | --- |
| 1. Isolated target and baseline | Recorded source/base revisions, both restores, tool/bootstrap setup, original baseline results, separate target branch | Complete |
| 2. Independent fixtures and consumers | 265 independent tests, full 957 message unit tests, unchanged materializer goldens | Complete |
| 3. Shared fixture and serialized runner | Both projects build and publish; offline lane passes; six pinned-image smoke/restamp tests and complete provider lanes pass, including wrapper and native recovery cases | Complete |
| 4. Current admission adapters | 672 focused admission tests and complete 4465 CDC unit tests; no obsolete Control project or production parser replacement | Complete |
| 5. Provider, delivery, and consumer scenarios | PostgreSQL 502 and MSSQL 496 message tests, with local authorization profile and sanitized evidence | Complete |
| 6. Acknowledgement and sizing evidence | Both provider message suites and affected admission, lifecycle, recovery, sizing, and telemetry controller suites pass; live offset/task evidence remains distinct from synthetic evaluator prerequisites | Complete |
| 7. Traceability and shared CI | Packaged discovery passes; manifest assigns exactly 02/06/07/08/09/10/13/14; matrix emits 15 jobs; targeted suite and exporter checks pass; expected provider JSON exports and TRX links verified | Complete |
| 8. Final validation and review | All four complete lanes and Release/analyzer/format checks pass; all 68 source paths accounted for; retained paths match source; only revised 05 changes in design tree; no production C# changes; dedicated resources removed | Complete |

The revised 05 file exactly matches `bb3aecb98`; target materializer goldens have no
base-relative changes. The current target delta contains 82 paths, including the
supplemental fixture repairs documented in the inventory. The source checkout retains
only the requested plan clarification and its original branch history.

## Completion and delivery

- All plan gates and the completion checklist are satisfied. Qualification used the shipped
  immutable image, PostgreSQL 18.4, SQL Server 2025, and the explicit fixture authorization profiles.
- Removed the dedicated `dms1324-port-history-pg` and `dms1324-port-history-mssql` containers
  and the preserved PostgreSQL volume after qualification completed. No connector fixture
  containers or networks remained. The four unrelated container identities were preserved,
  and their existing application health checks remained healthy.
- The port remains on the separate local `DMS-1324-port` branch. Source history is intact;
  branch replacement, pushing, and PR publication remain separate delivery actions.
- Sanitized reports remain under `TestResults`. Private diagnostic logs are local only and
  must not be published. Historical failed runs are retained for diagnosis and are not
  counted as successful acceptance evidence.

### Managed lifecycle follow-up

The first full PostgreSQL lane passed 12 of 15 managed-lifecycle cases. Two wrapper
handoff cases failed because the Compose fixture lacked the neighboring broker file
required by retained-input inventory. The fixture now retains local copies of both shipped
Compose files, preserving their relative inheritance and CDC listener overrides. Both wrapper variants pass
(`TestResults/port-postgresql-lifecycle-fixed-04`, 2 passed, no skips). An intermediate broker-only inheritance attempt dropped those
overrides and was corrected before acceptance.
A persistent-backlog test assumed four polls fit a fixed deadline. Its corrected
assertion checks repeated polling plus the unchanged deadline/timeout/retained-work
requirements; the release-on-fourth-poll case still requires all four polls. Both backlog
variants passed the focused rerun. Wrapper process failures now expose their captured
exit diagnostics before asserting that a handoff occurred.

### Telemetry and provider cleanup follow-up

The original PostgreSQL telemetry case used generic unit-test request data, including an
unrelated Kafka bootstrap address, for live worker inspection. It now reuses the existing
fixture observation request built from the actual rendered connector. Both providers pass
worker inspection, task replacement, worker replacement, and live telemetry correlation.

The original PostgreSQL history phase passed all 25 cases, but all five provider-cleanup
cases failed because the dedicated admin container used `wal_level=replica`. Logical
decoding is now enabled. A restart changed its ephemeral published port and invalidated
one focused rerun; the container was recreated with its preserved database volume and
fixed original host port `32796`. Fresh qualification passes all 25 history and five
provider-cleanup cases with no skips.

The original MSSQL lifecycle phase passed 12 of 15 cases. One failed during offset-store
setup, one rejected start before resume with workflow evidence validation, and one rejected
restart with metrics/Connect validation diagnostics. All three pass the focused rerun,
and the complete fresh MSSQL lane passes all 15 lifecycle cases and every remaining phase.

The persistent-backlog timing override was also found to reset `MaximumObservationAge`
to ten seconds, despite the shared fixture explicitly allowing one minute. The override
now preserves that fixture setting while retaining its existing wait deadline and all
resume, polling, and retained-work assertions. A focused rerun of all three MSSQL failures
passes (`TestResults/port-mssql-lifecycle-focused-01`, three passed, no skips). The other
two failures did not reproduce in this rerun. The fresh full MSSQL lane has passed
admission and all 15 managed-lifecycle cases with this correction.

The PostgreSQL persistent-backlog case passes with the corrected freshness override
(`TestResults/port-postgresql-lifecycle-freshness-01`, one passed, no skips). This supplements
the full PostgreSQL managed-lifecycle result after the test-only timing change.
