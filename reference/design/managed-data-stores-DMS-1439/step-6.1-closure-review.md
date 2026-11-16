# DMS-1439 step 6.1 closure review

Source baseline: `b283e37b0888af046c7139b0113f01dd740808d3`. **Submitted for architectural review; step 6.1 remains open.** G5 (`9b817eaf7`) and G6 (`b283e37b0`) are approved. Their approval does not approve this closure assessment, step 6.2, a release or a push. This is a documentation-only review of retained evidence, not a new test execution or an independent review of every implementation line.

## Proposed disposition

The previously identified HTTP-state and explicit identity-canary gaps are addressed at their reviewed scopes. The [17-criterion audit](step-6.1-acceptance-audit.md) and [36-boundary matrix](step-6.1-qualification.md) retain the evidence and limitations. No new production defect was established by this focused review. The entire ticket is not complete: AC16 operator documentation and the rollout/operations work belong to unstarted step 6.2.

Two explicit decisions are needed before calling 6.1 complete:

1. **Runtime access:** accept the bounded credential-publication, DMS discovery and real read-access qualification described below as sufficient for DMS-1439's Q3 boundary, or require a focused resource-write qualification. The recommendation is to accept the bounded evidence for this lifecycle ticket, without claiming general DMS CRUD/CDC qualification. This is a proposed interpretation of the approved spec, not an already-authorized narrowing.
2. **Live endpoint rollover:** the spec calls for endpoint-rotation qualification, while the existing evidence composes controlled endpoint/secret-rotation tests with separate live physical holder/fence tests. It does not exercise a live A-to-B rollover during one operation. Do not silently promote that composition to live rollover coverage. Either explicitly accept that qualification limitation at closure or require a focused follow-up before closure. The conservative default remains open until the reviewer selects a disposition.

No additional test implementation is authorized by this document. If a decision requires more evidence, define the smallest provider-specific slice and retain the next commit/review boundary.

## Runtime-access sufficiency assessment

The governing revision-9 spec Q3 says operators arrange the runtime principal and access before enabling management, excludes principal/grant creation from this ticket, and says live create-to-DMS-access qualification verifies permissions. Section 7 requires real SQL registration/discovery, both target providers and mixed-provider selection; AC6 requires encrypted ordinary publication and AC16 requires eventual cache visibility. None of those statements explicitly enumerates a resource CRUD or CDC workload. Nevertheless, the phrase "verifies permissions" must not be reported as all runtime permissions having been established.

The inspected [PostgreSQL live fixture](../../../src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration/DataStoreManageLiveRecoveryTests.cs) and [SQL Server live fixture](../../../src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration/DataStoreManageLiveRecoveryTests.cs) do the following:

- `VerifyLiveRecovery` constructs the real DMS `ConfigurationServiceDataStoreProvider`, CMS API client and connection decryptor, initially sees no registration, then observes the published row only after an expiry-triggered refresh. It also observes deletion on a subsequent refresh. Authentication and time are controlled; a full DMS web host/resource pipeline is not launched.
- The discovered provider token is checked, including `mssql` artifact/aggregate to `sqlserver` ordinary routing. `VerifyLiveRuntime` opens the **discovered, decrypted** connection, verifies the configured principal is not superuser/sysadmin, and reads the expected `dms.EffectiveSchema` hash and `dms.DataStoreIdentity` value.
- Genuine Minimal and Sample packages and both mixed CMS/target pairings have retained passing evidence. The [external lifecycle fixture](../../../src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/ManagedDataStores/ManagedDataStoreExternalLifecycleTests.cs) independently checks runtime principal/schema/identity with genuine Minimal, real CMS authentication/startup and worker execution. That external case decrypts the CMS response directly; it is not another DMS cache test.
- The test principal is intentionally read-capable: PostgreSQL grants `pg_read_all_data`; SQL Server grants `CONNECT ANY DATABASE` and `SELECT ALL USER SECURABLES`. Those are broad test-server read grants, not a production least-privilege recipe. No successful write, update, delete, resource authorization/validation path, CDC capture or CDC permission assertion is present.

**Assessment:** this is sufficient evidence of the lifecycle component's actual credential handoff, endpoint/database selection, single encryption/decryption, DMS discovery and demonstrated read access. It is **insufficient** to claim the resulting principal can serve a full DMS resource workload. Recommend accepting it as the bounded Q3 qualification because DMS-1439 does not install grants or alter resource execution; operators still own workload-specific grants. Reviewer acceptance must record this exact boundary. If broader Q3 access is intended, these read-only fixtures do not satisfy it; add focused assertions using the published principal rather than relabeling reads as CRUD. CDC remains a separate deployment capability, not an inferred ticket requirement.

The production [runtime builder](../../../src/config/backend/EdFi.DmsConfigurationService.Backend/ManagedDataStores/ManagedDataStoreRuntimeConnectionBuilder.cs) validates a snapshot and constructs the connection; it does not connect or grant access. A Created state therefore cannot be advertised as a runtime permission probe. Step 6.2 must explain this operator prerequisite if the bounded interpretation is accepted.

## Remaining limitations and their closure impact

| Item | Evidence and disposition required |
| --- | --- |
| Live endpoint/provider rollover | Matrix 31–34: controlled resolver/holder mismatch, immutable binding and runtime snapshot tests plus separate live locks/recovery. Wrong-server absence refusal is asserted in controlled tests. No live two-server rollover; explicit closure disposition required above. No existing approval is a waiver |
| Physical diagnostics and logging sinks | G6 qualifies real handler/executor/CMS responses and captured application records with simulated physical outcomes and hostile diagnostic input. Physical diagnostic unit tests and external output scans are complementary; no all-sink or all-physical-error certification. Retain this limit; no observed leak is established here |
| External deployment permutations | Real self-contained HTTP and Minimal lifecycle/readiness/restart/mixed-job behavior passed on both engines. Genuine Sample and mixed CMS/target evidence use controlled hosts. External Sample, Keycloak/TLS and abstract-socket connectivity remain unverified permutations, not evidence that the tested paths fail |
| Process and job boundaries | Both final eight-case process selections passed; termination is real, dead-lease expiry is accelerated and authentication/readiness controlled. Separate real lock-timeout tests verify the unchanged backoff and same-worker retry. Concurrent overlapping exhaustion sweeps remain unqualified; existing batch/latency/retention tests do not assert that composition |
| Unrelated job progress | External suppression proves dispatch/settlement of unsupported types, ordinary exhaustion, scheduling and retention while managed work stays paused, then original-job resumption. It does not prove successful unrelated business-handler execution; no such handler is registered in the native host |
| Customized catalogs and rollout | Standard/upgrade migration, definition drift, collision, rollback and direct-write evidence is retained. Additional custom display-Name unique indexes can still veto canonical-distinct names. Operator inventory/preflight, coordinated rollout, rollback constraints and PostgreSQL TEMP privilege are step 6.2/release obligations; do not claim arbitrary custom-catalog parity |
| Other failures and skips | Fake-psql failure, original SQL error 3980, bootstrap real-SqlClient skips and broader DMS/SchemaTools/CDC qualification remain unresolved/unverified. Comparable SqlClient baselines distinguish observed failures from the dependency update; they do not make those suites green. Closure cannot be described as full-suite success or waive the separate push validation gate |
| Pre-existing concerns | Ordinary dependent-read failures yielding empty collections, ordinary exception logging and DMS cache debug logging remain separate concerns. This slice neither introduces nor resolves them; managed G6 checks are not a whole-service redaction assurance |
| Residue | The earlier PostgreSQL `EdFi_Ods_Httpf5f2f193fcc6Retained_Minimal` target and other unverified residue remain untouched. No fresh inventory/ownership check was performed. Historical cleanup evidence applies only to its recorded run |

These limits are not newly invented defects or automatic scope additions. Review must distinguish acceptance of bounded qualification from a requirement to add a specific missing check. In particular, an explicit live-coverage requirement cannot be dismissed merely by labeling it an environment limitation.

## Acceptance-criteria boundary

The [existing audit](step-6.1-acceptance-audit.md#acceptance-criteria-disposition) still maps every AC. AC1–5, 7–11 and 13–15 have their recorded implementation/provider/API evidence; G5 now supplies the full HTTP DELETE composition for AC8. AC6 has real publication/recovery evidence with the runtime-access interpretation above. AC12 now includes the approved G6 sentinel assertions at its stated scope. AC16 has real cache-refresh evidence but its required operator documentation is pending 6.2. AC17 retains strict parity; all mirrored results apply to the tested configurations and do not waive custom-catalog qualification or the endpoint decision.

AC9 continues under the explicit user clarification recorded in [provenance](contract-provenance.md#q4--authorized-ac9-clarification): DMS-1439 owns physical deletion, atomic ordinary/dependent removal and the tombstone; DMS-1441 owns snapshot persistence/cleanup. Live Jira still contains the original wording. No Jira edit or reverse snapshot dependency is introduced.

## Checks performed for this submission

- Re-read live Jira using `acli`; all 17 criteria still match the repository story after code-markup/card normalization. The original AC9 difference is governed by the recorded clarification.
- Rechecked revision-9 spec SHA-256 `0deda56ca66aecffb4e184cb7acc1c4d770ee7dfc3248e07ab47bfb2792098ac` and pinned OpenAPI SHA-256 `f0735ae1c7517b3534f31635fdc387b0665ff28679a98b2008103c7557b9466b`; both match provenance.
- Inspected runtime grant setup, actual DMS discovery/decryption and runtime queries in both live fixtures, external runtime queries, production builder boundary and controlled endpoint refusal/binding assertions. Rider symbol search returned no result for the live fixture; direct source inspection supplied the evidence. No IDE inspection snapshot is represented as a fresh build.
- Rehashed and parsed original evidence E01/E02/E09/E10/E12: 2/2/3/3/4 passing cases respectively, matching the [frozen index](step-6.1-evidence-index.json), with zero failures/skips. This is a focused recheck of runtime evidence, not a second audit of every one of the original 25 runs.
- Parsed all six G5/G6 summaries and TRX: G5 frontend/PostgreSQL/SQL 4/27/27 passes; G6 28/4/4 passes; zero failures/skips. Hashes below identify these later artifacts separately from the frozen index. Overlapping run counts are not summed into unique coverage.
- Compared the CMS frontend/shared/PostgreSQL/SQL backend production directories from approved startup commit `4d92928f7` to this source baseline: no changes. Tests/harnesses evolved; their own reviewed boundaries still apply.

| Local artifact under the spec directory's `evidence/` | TRX SHA-256 |
| --- | --- |
| `step-6.1-g5/Unit/results.trx` | `e1a24db8b45df4822dedb6ff5f36e0d5bd227ab1bfe9c6546f803159a8e4c942` |
| `step-6.1-g5/Postgresql/results.trx` | `40daa778e54243ac16a810252305882306457b99d3623929f9a93e6c505d3b5b` |
| `step-6.1-g5/Mssql/results.trx` | `0526b6b89fc857eaf5e41f5fd1f7a841c8aae7115407a2c5916517508e446721` |
| `step-6.1-g6/Unit/results.trx` | `3bda9b168956c9f4e24e3a370244c38f5206682ff20d5fdeccd0fa115fcb08d4` |
| `step-6.1-g6/Postgresql/results.trx` | `932eec766cd9f1885638fc91f15fb06bbbdd1f8e20950a433a0d27f87993aaf4` |
| `step-6.1-g6/Mssql/results.trx` | `fba7b1a97dce96dc94398022aa586ecfd3fa11ca8f0bcf58bb051aeb932729ed` |

No tests, builds, application hosts or database queries were run for this review. The passing results above are retained executions. Documentation links, 17/36 table-row counts and Git whitespace are checked for this documentation commit. No container, Rider process, database or private test configuration was changed. Await explicit closure review; do not start 6.2 or push.
