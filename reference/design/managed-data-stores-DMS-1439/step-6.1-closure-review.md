# DMS-1439 step 6.1 closure review

**Step 6.1 is approved complete at the reviewed qualification boundaries.** The architectural review of closure assessment `8a3cf892d`, relayed by the user, explicitly accepted the G5/G6 results, representative runtime write on both providers and bounded endpoint evidence. The reviewer inspected documents and prior evidence, did not rerun tests, and authorized step 6.2 only. This is not ticket completion, full validation, release or push approval.

Closure submission source baseline: `77fcfbf3d1e01cb9b384b44e0a841e587c94fb4a`. G5 (`9b817eaf7`), G6 (`b283e37b0`) and the focused runtime-write qualification (`77fcfbf3d`) were approved separately. Original assessment `5cb95acef` remains historical context. The [operations guide](../../../docs/CMS-MANAGED-DATA-STORES.md) is now supplied for step 6.2 review; the limitations below remain in force.

## Governing closure decisions

The previously identified HTTP-state and explicit identity-canary gaps are addressed at their reviewed scopes. The [17-criterion audit](step-6.1-acceptance-audit.md) and [36-boundary matrix](step-6.1-qualification.md) retain the evidence and limitations. No new production defect was established by this focused review. The entire ticket is not complete: AC16 operator documentation and the rollout/operations work are submitted separately in step 6.2.

The architectural review of `5cb95acef` selected these dispositions:

1. **Runtime access — representative write qualification approved.** Reads alone were rejected for Q3. The architectural review of `77fcfbf3d` accepts one representative backend descriptor write on each target provider using the discovered runtime connection and a test-provisioned operator principal. The requested write gap is addressed. Resource HTTP, full CRUD, CDC and a production least-privilege grant recipe remain outside the claim. CMS acquires no production principal/grant responsibilities.
2. **Endpoint rollover — bounded evidence accepted.** Controlled endpoint rotation/refusal and snapshot-binding tests, composed with separate live physical-lock/recovery tests, are sufficient for this step. The reviewer clarified that the spec does not explicitly require live two-server rollover. Live rollover itself was **not tested**; retain that limit here and in the future operations documentation. It is no longer an unresolved closure decision.

**Approved closure boundary:** the specifically requested G5, G6 and runtime-write gaps are addressed, and the endpoint evidence has an explicit accepted disposition. The approval does not mark remaining failures/skips as passing or accept every deployment permutation. The limitation table below remains part of the approved assessment. Step 6.2 review and push remain separate gates.

## Runtime-access sufficiency assessment

The governing revision-9 spec Q3 says operators arrange the runtime principal and access before enabling management, excludes principal/grant creation from this ticket, and says live create-to-DMS-access qualification verifies permissions. Section 7 requires real SQL registration/discovery, both target providers and mixed-provider selection; AC6 requires encrypted ordinary publication and AC16 requires eventual cache visibility. None of those statements explicitly enumerates a resource CRUD or CDC workload. Nevertheless, the phrase "verifies permissions" must not be reported as all runtime permissions having been established.

The inspected [PostgreSQL live fixture](../../../src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration/DataStoreManageLiveRecoveryTests.cs) and [SQL Server live fixture](../../../src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration/DataStoreManageLiveRecoveryTests.cs) do the following:

- `VerifyLiveRecovery` constructs the real DMS `ConfigurationServiceDataStoreProvider`, CMS API client and connection decryptor, initially sees no registration, then observes the published row only after an expiry-triggered refresh. It also observes deletion on a subsequent refresh. Authentication and time are controlled; a full DMS web host/resource pipeline is not launched.
- The discovered provider token is checked, including `mssql` artifact/aggregate to `sqlserver` ordinary routing. `VerifyLiveRuntime` opens the **discovered, decrypted** connection, verifies the configured principal is not superuser/sysadmin, and reads the expected `dms.EffectiveSchema` hash and `dms.DataStoreIdentity` value.
- Genuine Minimal and Sample packages and both mixed CMS/target pairings have retained passing evidence. The [external lifecycle fixture](../../../src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/ManagedDataStores/ManagedDataStoreExternalLifecycleTests.cs) independently checks runtime principal/schema/identity with genuine Minimal, real CMS authentication/startup and worker execution. That external case decrypts the CMS response directly; it is not another DMS cache test.
- In the original read-only selections the principal receives PostgreSQL `pg_read_all_data` or SQL Server `CONNECT ANY DATABASE` and `SELECT ALL USER SECURABLES`. Those are broad test-server read grants, not a production least-privilege recipe. Those selections contain no successful write, update, delete, resource authorization/validation path or CDC assertion; the approved write follow-up adds a separate descriptor insert with additional test-only grants.

**Assessment after review:** the original evidence establishes credential handoff, endpoint/database selection, single encryption/decryption, discovery and reads, but was insufficient for Q3's write-access boundary. The earlier recommendation to accept reads alone was rejected. The now-approved follow-up supplies a production DMS descriptor insert through the discovered runtime credential on both targets and addresses that requested gap. It does not establish a full DMS resource workload or CDC.

The production [runtime builder](../../../src/config/backend/EdFi.DmsConfigurationService.Backend/ManagedDataStores/ManagedDataStoreRuntimeConnectionBuilder.cs) validates a snapshot and constructs the connection; it does not connect or grant access. A Created state therefore cannot be advertised as a runtime permission probe. The operations guide explains the operator's access prerequisite; a passing qualification test does not add a production permission probe.

## Focused runtime-write follow-up — approved in 77fcfbf3d

Mirrored `DataStoreManageRuntimeWriteTests.It_writes_a_descriptor_with_the_discovered_managed_runtime_principal` cases extend the genuine Minimal live recovery scenario on PostgreSQL and SQL Server. The existing read-only and Sample/mixed cases keep their default behavior. After real managed acceptance, physical provisioning, injected publication rollback and successful replay, the actual DMS catalog consumer discovers/decrypts the connection. The existing runtime assertions establish the intended non-superuser/non-sysadmin identity and correct target hash/source identity.

Test-only operator setup then grants DML on the owned target's `dms` schema and PostgreSQL sequence usage or SQL Server schema EXECUTE; SQL also creates the database user for the already-created login. Administrative access is used only for these grants. The production DMS `IDescriptorWriteHandler`, provider connection acquisition and write transaction receive the discovered runtime `DataStore`. A SchoolTypeDescriptor POST must return InsertSuccess with the supplied document UUID. A separate connection using that same runtime credential must observe exactly one joined Document/Descriptor row with the actual resource key, namespace, code value, short description and positive content version. No hand-written INSERT substitutes for the DMS handler. The existing managed-delete recovery and target-absence assertions then complete.

This is a representative descriptor resource write at the DMS backend boundary, not external DMS resource HTTP or full API-schema compilation. It reuses the established DMS descriptor test model, binds its resource key to the restored catalog and its mapping key to the trusted package hash/version, selects a controlled NoFurtherAuthorizationRequired policy, and substitutes ancillary cache supervision/profile services. The writer, provider sessions, transaction, SQL and target are real. No update/delete resource or CDC permission claim is made. Test grants are not a production least-privilege recipe; production CMS creates no principal or grant.

Final focused execution: **one PostgreSQL case and one SQL Server 2025 TCP case passed, zero failures/skips**. Case durations were **9.00s / 52.41s** (TRX start-to-finish **9.84s / 59.74s**). SQL server facts were **17.0.5065.9 / TCP**. Both cases include the existing live create/delete recovery sequence; no broad suite was rerun. SQL database inventories were unchanged. Fixture-owned target/principal cleanup completed; the independent PostgreSQL cleanup probe failed, so no separate server-wide PostgreSQL inventory assertion is claimed. Previously retained unexplained targets were not touched.

The first build caught test reference/name issues. Two initial PostgreSQL runs stopped on fixture setup: an invalid query for RelationalMappingVersion (now taken from the manifest), then missing production connection-source registration (now registered through AddPostgresqlDatastore). Neither reached the resource write. The final Rider build `72202142-1da4-4dc2-a031-d4aac30fbafa` and four-file CSharpier check passed. Two integration project files add DMS provider/backend/test-support references; their lockfiles add six project entries each, with no existing package/version changes. No production code or deadline changed.

Local evidence under `evidence/step-6.1-runtime-write/` in the spec directory:

| Final run | TRX SHA-256 |
| --- | --- |
| `Postgresql-runtime-write-f8a1ab3570f8420985c239f0cc5e3b59` | `1dc89439fdaa8b7e8ed2492caaad5dc69879bd3a52ea541450d5c2cb004ac011` |
| `Mssql-runtime-write-06ad20b187a64accbc743ca11c33a930` | `36bb7f392044a6b97e42eed18221fedc3bd2e0a16ca87e50c433418be513f335` |

The architectural review approved these results and source in `77fcfbf3d`, while explicitly stating that the reviewer did not rerun the tests. That approval closes the requested write-qualification gap, not step 6.1 itself. All remaining limitations below retain their recorded status.

## Remaining limitations and their closure impact

| Item | Evidence and disposition required |
| --- | --- |
| Live endpoint/provider rollover | Matrix 31–34: controlled resolver/holder mismatch, immutable binding and runtime snapshot tests plus separate live locks/recovery. The architectural review explicitly accepts this composed evidence for the step. Live two-server rollover was not tested and must remain documented |
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

The [existing audit](step-6.1-acceptance-audit.md#acceptance-criteria-disposition) still maps every AC. AC1–5, 7–11 and 13–15 have their recorded implementation/provider/API evidence; G5 supplies the full HTTP DELETE composition for AC8. AC6 has real publication/recovery evidence and the approved representative runtime write. AC12 includes the approved G6 sentinel assertions at its stated scope. AC16 has real cache-refresh evidence but its required operator documentation is pending 6.2. AC17 retains strict parity; mirrored results apply to the tested configurations, the endpoint composition is accepted with its stated limit, and customized-catalog qualification remains a deployment obligation.

## Checks for this closure resubmission

This documentation-only resubmission records the architectural approval and reconciles the audit/matrix. The two final runtime-write TRX hashes and summary counts were rechecked against the evidence above: one pass per provider, zero failures/skips. The revision-9 spec and OpenAPI hashes still match the original assessment. All 17 criteria and 36 matrix rows remain, and local documentation links and Git whitespace are checked. No test, build, database query, inventory probe or application host ran for this resubmission. Earlier failed executions, unsuccessful independent PostgreSQL cleanup probing and retained residue are not recast as passing or clean.

AC9 continues under the explicit user clarification recorded in [provenance](contract-provenance.md#q4--authorized-ac9-clarification): DMS-1439 owns physical deletion, atomic ordinary/dependent removal and the tombstone; DMS-1441 owns snapshot persistence/cleanup. Live Jira still contains the original wording. No Jira edit or reverse snapshot dependency is introduced.

## Checks performed for the original assessment

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

No tests, builds, application hosts or database queries ran for the original documentation-only assessment; its results above were retained executions. The subsequent focused write execution is recorded separately in the follow-up section. Documentation links, 17/36 table-row counts and Git whitespace were checked for the closure submission. No existing container, Rider process, unexplained database or private test configuration was changed. The subsequent approval of `8a3cf892d` permits step 6.2; it does not authorize a push.
