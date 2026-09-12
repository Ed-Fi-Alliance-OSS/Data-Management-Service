# DMS-1324 Porting Plan

## Objective and approach

Port the useful DMS-1324 message-contract implementation onto the replacement DMS-1323
branch, using the revised story as the acceptance specification. Start a separate branch
from the new base and transfer coherent groups of changes. Do not replay the obsolete
DMS-1323 implementation or mechanically resolve a rebase by restoring deleted code.

This document plans the port; writing it does not start the port, modify either branch's
history, or publish changes.

### Reference revisions

| Role | Revision |
| --- | --- |
| Original DMS-1324 branch point; lower bound of the source delta | `45046c1d3a26927d93e94394f901731e0aaa12fe` |
| Existing implementation tip assessed before the story update | `efb3f34571062a9e89c34318ca4bd58b0484d6e3` |
| Source implementation plus revised story | `bb3aecb98cd887f9b05a7cebccda1d978ce40c8b` |
| Replacement DMS-1323 base assessed | `c3ae3bfffbd0a049abf7b8768eb4f14744a6bbaf` |

Recheck these references when execution begins. If DMS-1323 has advanced, record the new
chosen base and review its intervening changes before transferring files.

The governing documents are:

- `reference/design/backend-redesign/epics/19-cdc-kafka/05-message-contract-tests.md`
  from the source revision above, including `bb3aecb98`'s update.
- `reference/design/backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md`
  and the normative CDC design documents from the replacement DMS-1323 base.

Carry forward the revised 05 story. Keep the target base's revised 04 story and design
documents; copying the source branch's older design directory would reverse the redesign.

## Assessment evidence and limits

- The original implementation contains 28 commits after the old branch point, changing
  68 files, with approximately 22,700 added lines. Most additions are tests and fixtures.
  The subsequent story update is a separate commit.
- A three-way application of the combined implementation delta reported seven conflicted
  paths. This was a merge-tree assessment, not a completed rebase or proof of compilation.
  Clean application of added files does not establish compatibility with current APIs.
- In a temporary copy of the replacement base, 265 message/consumer unit tests passed
  unchanged, with no failures or skips. The experiment imported the new unit files and
  fixture assets, excluded `MessageContractAdmission*.cs` from compilation, and excluded
  traceability tests from execution. It did not qualify the complete suite or Docker paths.
- The public message contract and representative materialized-document fixtures remain
  substantially reusable. The two production guards added to the obsolete observation
  mapper already exist in the replacement `CdcConnectOffsetEvidence` parser.
- Admission adapters, shared fixture composition, live image compatibility, and CI
  integration still require implementation and validation. Do not assign a completion
  estimate solely from conflict count or the passing subset.

## Transfer inventory

Paths in this table are under `src/dms/backend/` unless otherwise stated.

| Area | Treatment |
| --- | --- |
| `Fixtures/cdc/message-contract/` | Carry forward catalogs, provider inputs, vectors, variants, shared props, and documentation. Reconcile failure/traceability claims with adapted tests. |
| CDC unit `MessageContractFixtureCatalog`, `MessageContractJson`, fixture tests, consumer harness, bootstrap, ordering, and continuity tests | Carry forward with minimal changes; rerun against the actual port branch. |
| CDC integration `MessageContractRunner*`, its Java sources, and `CdcPinnedImageJavaRuntime` | Preserve the runner design; qualify against the target's current immutable image and plugin classpath. |
| Serialized upsert, routing, progress, failure, and runner prerequisite tests | Preserve behavioral assertions; adapt only current fixture/runtime interfaces as needed. |
| Provider row/assertion/observation helpers, PostgreSQL/SQL Server tests, consumer broker tests, retry and cleanup tests | Preserve scenarios and bounded evidence; integrate with the target fixture. |
| `MessageContractAdmissionFixture`, admission tests, and admission-offset tests | Rewrite the obsolete adapter composition while preserving valid contract scenarios. |
| Progress-acknowledgement and record-size tests plus fixture partials | Preserve fault injection and message evidence; adapt observations and readiness claims. |
| Shared `CdcConnectorTemplatePinnedImageFixture`, smoke/startup/cleanup tests, and new partials | Merge behavior deliberately into the target version; preserve all existing target modes. |
| Test project files and dependency locks | Add required fixture imports, runner assets, and shared helper links to target projects. Regenerate locks. |
| Obsolete `Backend.Cdc.Control` mapper/project/lock changes | Omit. Verify the equivalent current parser behaviors instead of restoring the old library. |
| Source PR-workflow changes | Reimplement the required coverage through the target's shared qualification runner and existing workflows. |
| Revised 05 story | Carry forward as the port's acceptance specification. |

## Execution stages

### 1. Establish an isolated target and baseline

1. Inspect working-tree status, worktree registrations, local branches, and remote refs.
   Preserve unrelated work and record exact source/base SHAs.
2. Create an available branch such as `DMS-1324-port` from the chosen replacement DMS-1323
   tip, in `/home/brad/work/dms-root/DMS-1324-port`. Keep the existing DMS-1324 checkout as
   the source. Follow the git-worktree-management skill for setup and tracking.
3. Bootstrap the new checkout with both solution restores, `dotnet tool restore`, and
   `dotnet husky install`, as required by that skill.
4. Copy this plan and the revised 05 story to the target. Generate a file inventory from
   the old branch point to the pinned source revision. Use file-level transfers and
   reviewed hunks; do not copy the source solution, design tree, or entire backend tree.
5. Run the target's existing Contract qualification lane and record baseline results.
   Classify environment failures separately from test failures. Note any existing failures
   before attributing later failures to the port.

**Gate:** Isolated target, known baseline, revised story present, and unchanged source
branch. Suggested first commit: port scope and plan.

### 2. Transfer independent fixtures and consumer contracts

1. Import the language-neutral fixture inputs and `MessageContractFixtures.props`.
   Continue loading representative cache rows, etags, and public document bodies from the
   existing materialized-document fixture tree; do not duplicate or overwrite those goldens.
2. Import the independent unit helpers and consumer suites listed in the inventory.
   Preserve exact numeric values, property absence, equal-version violation handling,
   tombstone behavior, durable partition barriers, and 24-hour bootstrap/renewal rules.
3. Merge the unit project import into the target project. Do not import a reference to
   `Backend.Cdc.Control`. Retain current target dependencies and regenerate affected locks.
4. Run the independent subset, then the existing CDC unit tests. Compare the transferred
   subset to the prior 265-test result and explain any count changes.
5. Inventory traceability mappings now, but enable the exhaustive mapping gate only when
   all referenced suites have been transferred in stage 7. Record this as an incomplete
   port stage; do not present partial discovery as full acceptance or weaken the final gate.

**Gate:** Independent scenarios pass on the actual target with no skipped cases and no
unexplained changes to consumer semantics. Suggested commit: fixtures and consumer contracts.

### 3. Integrate the shared fixture and serialized runner

1. Start with DMS-1323's `CdcConnectorTemplatePinnedImageFixture`. Preserve its controller
   callbacks, exposed broker/Connect/metrics endpoints, native Kafka and Compose modes,
   offline cleanup support, idempotent disposal, volume cleanup, and cancellation handling.
2. Add message-contract partials and utilities. Reconcile the source-producer isolation
   option, broker endpoint allocation/retry logic, and source observation with the existing
   modes. Avoid two competing broker-port fields or silently changing existing callers.
3. Preserve bounded cleanup after startup failure and every fault phase. Merge both
   branches' startup/cleanup regression coverage; do not replace the target cleanup path
   with the source branch's simpler implementation.
4. Import the Java runner, producer proxy, source observer, and C# invocation code. Use the
   shipped `CdcQualifiedWorkerImage.json` image selection and the shared prerequisite policy.
   Confirm the runner can load the transform, converter, partitioner, and Kafka APIs from
   that digest. Reuse production plugins rather than implementing a .NET transform.
5. Merge integration project assets and linked helpers without dropping target controller
   fixture/provisioning dependencies. Confirm required assets reach both build and packaged
   test output directories.
6. Run offline runner/prerequisite, broker-startup, cancellation, and cleanup tests. Run
   existing pinned-image smoke tests and serialized upsert/routing/progress/failure suites
   for PostgreSQL and SQL Server with the qualified image.

**Gate:** Both test projects compile, offline fixture regressions pass, existing target
fixture modes remain supported, and serialized suites pass on the current image.
Suggested commits: shared fixture integration; serialized contract runner and suites.

### 4. Adapt focused admission contracts to current adapters

1. Replace `AddDmsCdcControl`, `ICdcConnectorObservationMapper`, `CdcObservationContext`,
   and obsolete transport-result composition in `MessageContractAdmissionFixture`.
   Use the current `CdcConnectRestAdapter`/`ICdcConnectTransport` offset and status handoffs,
   shared Core evaluators, and current provider-position adapters. Exercise parsing through
   the production adapter, using controlled transport responses for deterministic cases.
2. Preserve tests for missing, multiple, mismatched, snapshot, null, and malformed offsets,
   including exact provider source-partition shapes and non-object offset rejection.
   Confirm the current parser already handles the source branch's two production fixes;
   add focused regression coverage only where current tests leave a meaningful gap.
3. Update observation construction to the current contracts. Require ordered first
   caught-up observation, captured provider barrier, committed offset crossing, and second
   caught-up observation, plus independent fresh current lag with valid identity.
4. Cover missing/stale/mismatched lag, expiry, and absent optional lag statistics. Reuse
   current telemetry adapter behavior; do not derive fake percentiles or add another parser.
5. Keep synthetic ownership, source-history, projection, and telemetry prerequisites
   explicit. An evaluator result with synthetic inputs establishes contract behavior only.
   Reuse DMS-1323's corrected PostgreSQL continuity semantics rather than copying old
   assumptions about `confirmed_flush_lsn` or implementing a new continuity classifier.
6. Run all CDC unit tests, including the adapted admission suites and existing target
   offset/telemetry/readiness regressions.

**Gate:** No obsolete control-library dependency remains, invalid observations fail closed,
and focused tests do not claim production writer admission. Suggested commit: current
admission adapter composition and contract tests.

### 5. Port provider, delivery, and consumer broker scenarios

1. Transfer PostgreSQL/SQL Server row builders and shared assertions/observations. Check
   source inserts against the target fixture's DDL and UUID constraints; reuse valid
   materialized rows rather than bypassing consistency requirements.
2. Qualify upserts, authoritative deletes, work-table exclusion, progress routing, exact
   keys, stable per-key partitioning, and suppression of automatic second tombstones.
3. Transfer broker retry and consumer-bootstrap tests. Preserve captured offset boundaries,
   bounded scans, controllable consumer state, and sanitized failure evidence.
4. Assert replay and eventual convergence after interruption. Do not reinterpret those
   results as managed lifecycle certification, source-history continuity, or absence of
   pre-validation publication during native recovery.
5. Identify the authorization-disabled local profile in results. Keep production-like ACL
   qualification in the target's existing policy suites.

**Gate:** Both provider suites and applicable broker-consumer/retry suites pass, required
cases do not skip, and fault cleanup leaves no unintended fixture resources.
Suggested commit: provider and broker message contracts.

### 6. Port acknowledgement and record-size readiness evidence

1. Preserve heartbeat fault injection: capture a barrier after a focused caught-up
   observation, block producer acknowledgement, observe committed source offsets remaining
   below the barrier, release the fault, and observe crossing. Consumed Kafka records and
   topic offsets are not substitutes for committed provider-position evidence.
2. Adapt `MessageContractProgressAcknowledgementTests` and `MessageContractRecordSizeTests`
   to the current transport/observation helpers from stage 4. Label live measurements and
   synthetic prerequisites separately in assertions and evidence. Live readiness claims
   must use corresponding current production observations; keep other results explicitly
   scoped to evaluator classification under stated prerequisites.
3. Preserve compression-disabled producer-size measurement, below/above boundary inputs,
   task failure without a partial public record, and resumed publication after correction.
   Renew all observations required for any subsequent readiness classification.
4. A direct test configuration change may establish resumed message publication. It must
   not claim to qualify the supported record-size increase procedure, consumer attestation,
   durable acknowledgement, or interrupted-rollout reconciliation owned by DMS-1323.
5. Run both provider variants and targeted existing admission, telemetry, native recovery,
   and record-size controller regressions affected by the shared fixture changes.

**Gate:** Fault causality is demonstrated with bounded live evidence, current observations
are used correctly, and the tests stay within revised 05 ownership.
Suggested commit: acknowledgement and sizing contract adaptation.

### 7. Reconcile traceability and integrate qualification

1. Update `traceability.json`, `failure-catalog.json`, fixture documentation, and scenario
   mappings to actual discovered tests and their revised claims. Preserve stable IDs for
   unchanged tests. Account for renamed/replaced tests explicitly; do not delete required
   scenarios simply to satisfy discovery.
2. Enforce the assigned invariant set: `CDC-INV-02`, `06`, `07`, `08`, `09`, `10`, `13`,
   and `14`. Restore complete traceability checks in both test assemblies. These checks
   validate executable coverage, not Markdown wording or controller documentation.
3. Extend `eng/ci/Invoke-CdcQualification.ps1` and its existing evidence infrastructure.
   Add an explicit `MessageContract` provider suite selection, including serialized
   and broker categories, and include it in provider `All` runs. Use the filter
   `(Category=CdcMessageContractSerialized|Category=CdcMessageContractKafka)&Category=<provider>Integration`,
   substituting `Postgresql` or `Mssql` for `<provider>` so the provider constraint applies
   to both categories. The current runner has
   no message-contract suite selection; merely adding test files will not route them into
   its live provider lanes.
4. Extend `eng/ci/Get-CdcQualificationMatrix.ps1` alongside the runner: add the new
   message-contract suite selection to its accepted values and generated jobs for both
   providers. Nightly jobs invoke specific suites, so extending only the runner's `All`
   selection does not schedule the new coverage. Update the matrix tests in
   `eng/ci/tests/CdcQualification.Tests.ps1` to verify default nightly selection, each
   provider's `All` selection, and targeted manual message-contract selection, with the
   exact expected suite names and no duplicate jobs: the default matrix must contain
   15 jobs (`Kafka/All` plus seven suites per provider), each provider's `All` selection
   must contain seven jobs, and each targeted `MessageContract` selection must contain
   exactly one job for the requested provider. Keep the runner and matrix suite lists
   consistent through a shared definition or an executable consistency check.
5. Preserve the Contract lane's Docker-free behavior. Route pinned-image serialized tests
   and live provider/broker tests to explicit/nightly qualification. Update
   `.github/workflows/nightly-cdc-qualification.yml` and the relevant PR/manual workflow
   wiring, including manual suite choices, without restoring the source branch's competing
   qualification path. Update the hard-coded "13 live suites" success message and its
   existing test assertion to "15 live suites" for the expanded nightly matrix.
6. Keep shipped-image validation, prerequisite failure, and evidence reporting shared.
   Ensure new named suite failures reach the aggregate exit status and expected reports.
   Extend existing runner/Pester checks for selection, nonempty discovery, missing
   prerequisites, failed/skipped required cases, and result aggregation.
7. Retain base/source/port revisions, qualified digest, provider, authorization profile,
   selected scenarios, TRX results, and sanitized contract evidence. Confirm the shared
   exporter carries the new attachments without publishing private logs or document bodies.
   Reconcile message-contract attachment names with the allowlist in
   `eng/ci/cdc-qualification.psm1`; add an exporter regression that verifies the sanitized
   JSON is retained and its exported TRX link resolves. Passing tests alone do not prove
   their attachments survive the publication boundary.
8. Run discovery/traceability against packaged test assemblies as well as local builds so
   missing JSON or Java assets cannot hide behind source-tree availability.

**Gate:** Every assigned scenario is mapped and selected by an intended lane; empty,
failed, or skipped required suites cannot produce successful qualification.
Suggested commit: traceability and shared qualification integration.

### 8. Complete validation and review the target delta

1. Format changed C# with `dotnet csharpier format <file-or-directory>`, regenerate affected
   locks, and run Release builds/analyzers for the affected projects and repository-required
   checks. Follow NUnit/FluentAssertions/FakeItEasy conventions and use `System.Text.Json`.
2. Run the complete Contract lane and PostgreSQL, MSSQL, and Kafka qualification lanes
   after runner integration. Include existing target controller suites because shared
   fixture startup, telemetry, producer, and cleanup behavior changed. Use fresh result
   directories for every invocation; retain final successful evidence from the final code.
3. Provision the actual required environments before testing. These CDC fixtures are not
   the `dms-local` HTTP E2E suite. Follow repository setup rules for any separately selected
   backend/API integration regressions; use SQL Server 2025 where required. Do not launch
   API E2E merely to qualify 05, and do not modify an unrelated running stack.
4. Review the complete diff against the chosen new DMS-1323 base. Verify no obsolete
   control project, old design text, source-replacement/adoption seam, or old workflow
   policy was accidentally restored. Explain any production-code change separately.
5. Reconcile the transfer inventory: each source file/scenario is retained, adapted,
   superseded by identified target coverage, or deliberately omitted with a reason. Check
   that the story and manifest claims match actual evidence rather than total test counts.
6. Record final results and remaining limitations. Keep the source branch intact. Branch
   renaming/replacement, pushing, and PR publication are subsequent delivery actions, not
   necessary to establish that the port is correct.

**Gate:** Full revised-story coverage passes with no skipped required cases, existing
affected DMS-1323 behavior remains qualified, and the final delta is reviewable.

## Validation commands

Run from the port checkout. The runner commands below already exist; provider `All` runs
will include the ported suites only after stage 7 extends their selection. Set required
environment values through the existing qualification setup, using the shipped qualified
image. Replace each example result directory with a fresh path when rerunning.

```powershell
pwsh -NoProfile -Command 'Import-Module Pester -MinimumVersion 5.7.1; Invoke-Pester -Path ./eng/ci/tests/CdcQualification.Tests.ps1 -CI'
pwsh ./eng/ci/Invoke-CdcQualification.ps1 -Lane Contract -ResultsDirectory TestResults/port-contract-01
pwsh ./eng/ci/Invoke-CdcQualification.ps1 -Lane Postgresql -PullImages -ResultsDirectory TestResults/port-postgresql-01
pwsh ./eng/ci/Invoke-CdcQualification.ps1 -Lane Mssql -PullImages -ResultsDirectory TestResults/port-mssql-01
pwsh ./eng/ci/Invoke-CdcQualification.ps1 -Lane Kafka -PullImages -ResultsDirectory TestResults/port-kafka-01
```

During development, use focused project/category filters for each stage. The prior
265-test experiment is useful as a portability reference, not a replacement for complete
admission, traceability, provider, or broker qualification.

After stage 7, verify targeted message-contract selection for both providers as well:

```powershell
pwsh ./eng/ci/Invoke-CdcQualification.ps1 -Lane Postgresql -Suite MessageContract -PullImages -ResultsDirectory TestResults/port-postgresql-message-contract-01
pwsh ./eng/ci/Invoke-CdcQualification.ps1 -Lane Mssql -Suite MessageContract -PullImages -ResultsDirectory TestResults/port-mssql-message-contract-01
```

## Completion checklist

- [x] Source and replacement-base revisions recorded; original branch preserved.
- [x] Revised 05 story retained alongside the replacement base's normative designs.
- [x] Independent fixture and consumer contracts transferred and passing.
- [ ] Shared fixture and serialized runner qualified on the current image.
- [x] Admission tests use current adapters without obsolete control-library references.
- [x] Both providers pass routing, deletion, progress, replay, sizing, and fault scenarios.
- [x] Synthetic evaluator evidence is distinguished from live production readiness.
- [x] Traceability is complete and packaged assets are verified.
- [x] Shared CI runner selects and reports every required suite correctly.
- [ ] Contract and all required live qualification lanes pass without required skips.
- [ ] Final base-relative diff and source-scenario disposition reviewed.
