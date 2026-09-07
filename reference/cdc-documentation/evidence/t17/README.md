# T17 retirement operator evidence

Reviewed 2026-09-06 (America/Chicago; captures cross 2026-09-07 UTC) against
`933fbc4ad3f15c619025e29c636426bcf00489d6` plus T17 changes and the existing T13
working-tree follow-up. The T17 commit preserves that unrelated work. This review maps
[retirement](../../operations-runbook.md#retire-binding-generation) to the
[binding lifecycle owner](../../../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding),
CDC-INV-14/15 and supporting CDC-INV-11. Provider runbook replays remain T14/T15;
T16 owns final reconciliation.

## Results and boundaries

| Layer | Result | Captures / exact identities |
| --- | --- | --- |
| Real PostgreSQL, pinned Connect, authorizer-enabled Kafka, filesystem; registered connector | Passed | [Governed cleanup and retained state](retirement-broker-registered.json) |
| Same stack, provider artifacts and durable generation created before connector registration | Passed | [Refusal without absence assertion, then cleanup](retirement-broker-neverRegistered.json) |
| Same stack; injected provider-deletion wait canceled by the actual five-second controller budget | Passed | [Timeout, surviving binding/provider artifacts, refusal, successful retry](retirement-broker-providerTimeout.json) |
| Packaged CLI, fake CMS, two real source databases per provider; synthetic retained binding; unavailable broker/Connect | 10 passed / 0 failed / 0 skipped | [Exact source-selection cases](cli-source-results.txt); captures below |
| Real parser/executor, substituted dispatcher; refusal/partial cleanup/timeout followed by serialized success | 3 passed / 0 failed / 0 skipped | [Exact cases](cli-json-results.txt), [refusal](retirement-json-refused.json), [partial cleanup](retirement-json-partialCleanup.json), [timeout](retirement-json-providerTimeout.json) |
| Existing controller and provider/Kafka teardown unit behavior | 32 passed / 0 failed / 0 skipped | [Exact identities](controller-results.txt) |
| Existing CLI command-surface, request-building, dispatcher behavior | 151 passed / 0 failed / 0 skipped | [Exact identities](cli-unit-results.txt) |
| Existing real-filesystem lifecycle cases, including incident/binding deletion failures and retained-history guards | 4 passed / 0 failed / 0 skipped | [Exact identities](store-results.txt) |

The three real broker cases and their parameterized fixture names are in
[broker results](broker-retirement-results.txt). Each starts and disposes the existing
`CdcControlBrokerFixture`; no new Docker/bootstrap/provider implementation was added.
The registered case uses live committed offsets. The never-registered case seeds a
binding through the shipped lifecycle service; it does not fabricate adoption authority.
The timeout substitutes only `ICdcProviderArtifactTeardown.DeleteAsync`; provider
validation, source connection, Connect, Kafka and filesystem operations remain real.
The subsequent retry uses the real provider-deletion adapter.

All three successful proofs pass `CdcCleanupProofValidator`. Assertions read back absent
connector/public/progress topics and literal grants, zero governed publication/slot
artifacts, matching retained retirement identity/fingerprint, and no remaining binding.
Worker config, status and offset topics survive; offset-topic ACLs remain unchanged.
After timeout, connector and public/progress topics are absent, but both provider
artifacts and the byte-identical binding record remain. Retry without the absence
assertion refuses. Retry with the assertion completes the same target/generation.
The controller fixture deliberately uses a fixed request operation ID; it does not
claim the packaged CLI reuses IDs between invocations.

Packaged source-selection captures:

| Scenario | PostgreSQL | SQL Server | Observed output |
| --- | --- | --- | --- |
| Missing confirmation | [Capture](retirement-source-postgresql-missingConfirmation.json) | [Capture](retirement-source-mssql-missingConfirmation.json) | Exit 64, empty stdout, confirmation diagnostic |
| Literal record tenant `default` | [Capture](retirement-source-postgresql-literalDefault.json) | [Capture](retirement-source-mssql-literalDefault.json) | Exit 64, empty stdout, tenant diagnostic |
| Unset original-source variable | [Capture](retirement-source-postgresql-unsetSource.json) | [Capture](retirement-source-mssql-unsetSource.json) | Exit 10, empty stdout, `cdcSourceConnectionVariableUnresolved` |
| Variable points at current/replacement database | [Capture](retirement-source-postgresql-currentSource.json) | [Capture](retirement-source-mssql-currentSource.json) | Exit 10, empty stdout, `retireRefused` physical-source mismatch |
| Variable points at original database | [Capture](retirement-source-postgresql-originalSource.json) | [Capture](retirement-source-mssql-originalSource.json) | Source validation passes; unavailable Connect yields exit 12, empty stdout, `retireIncomplete` stop failure |

Every packaged case preserves both databases' lifecycle/fingerprint and the durable
binding files. Non-literal cases use `--tenant-key ''`, generation 1,
`--source-connection-variable` and `--connector-already-absent`; authorized cases use
`--confirm cdcBindingRetirement`. The original-source case proves the override reaches
the original database instead of CMS's current target. An unavailable worker remains
unavailable despite the absent-connector assertion. It is not a completed cleanup.

Existing dispatcher cases in `cli-unit-results.txt` independently cover source override
while CMS is unreachable and retirement refusal/incomplete/success mapping. Existing
controller cases supply cleanup ordering, SQL Server governed-artifact inventory, and
failure preservation; filesystem cases supply incident deletion ordering. The mocked
CLI captures reuse the existing serializer-only sample proof; the complete, validated
proofs are the broker captures. Internal diagnostic wrapper properties are not a CLI
JSON contract. Actual CLI stdout/stderr are preserved as strings.

## Reproduction

Start at the repository root. .NET SDK 10.0.102, VSTest 18.0.1, Docker Engine 28.3.2,
Linux/Bash. Build the integration projects before running the recorded DLL invocations:

```bash
dotnet build src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control.Tests.Integration
dotnet build src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration
```

[Broker command](broker-command.txt) records all three image digests, fail-fast input,
and exact fully qualified selections. [CLI source command](cli-command.txt),
[CLI serialization command](json-command.txt), and [filesystem command](store-command.txt)
record exact selections. DLL invocations avoid MSBuild consuming quotes in parameterized
fixture names; parentheses are escaped for VSTest. Logs/TRX are written to
`/tmp/dms-1326-t17`; create that directory first.

For the packaged CLI selection, supply `ConnectionStrings__DatabaseConnection` and
`ConnectionStrings__MssqlAdmin` through the test environment's secret mechanism. This
run read existing disposable-local-server credentials without printing them, using
PostgreSQL at localhost:5435 and SQL Server at localhost:14333. PostgreSQL was 16.8,
image `postgres:16.8-alpine@sha256:951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0`.
SQL Server used `mcr.microsoft.com/mssql/server:2025-latest`, local image ID
`sha256:86cc6144ef39bb0fbed2329e1ad79b13ee82e7b2e4739213a0db0800e668a74a`.
The harness leases disposable databases and supplies `postgres` or `sa` as the actual
setup principal. A cloned baseline can retain a source UUID: the fixture assigns its
original database a distinct test UUID before binding it. This is fixture data, not an
operator identity-rotation or replacement procedure.

Supporting unit invocations:

```bash
umask 077
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit --filter 'FullyQualifiedName~CdcSetupControllerTeardown|FullyQualifiedName~CdcProviderArtifactTeardown|FullyQualifiedName~CdcKafkaTeardown|Category=CdcSetupControllerTeardown' --logger 'trx;LogFileName=controller.trx' --results-directory /tmp/dms-1326-t17
dotnet test src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit --filter 'FullyQualifiedName~Given_DocumentCacheAdminCdcCommandDispatcher|FullyQualifiedName~Given_DocumentCacheAdminCdcCommandRequests|FullyQualifiedName~Given_DocumentCacheAdminCdcCommandSurface' --logger 'trx;LogFileName=cli-unit.trx' --results-directory /tmp/dms-1326-t17
```

The additional teardown category selects `Given_CdcSetupControllerRetirement`, whose
class name does not match the task's requested `CdcSetupControllerTeardown` FQN substring.
Initial exact-filter attempts selected zero cases or failed filter parsing; only the
recorded nonzero final runs count. The initial CLI run skipped ten cases with absent
admin variables. Its first configured run passed four and failed six due to fixture
UUID/setup-principal assumptions; corrected fixtures passed all ten. Initial compile
formatting/analyzer issues were corrected. Final builds and CSharpier checks passed;
no out-of-scope integration failure remains.

## Manual comparison and evidence handling

Compared the T06 procedure, generated retirement help retained in T06, command parsing,
dispatcher mapping, controller cleanup order, and these captures. Confirmed confirmation,
default-tenant translation, source-variable bypass/no fallback, absence assertion,
empty stdout on refusal/incomplete, stderr diagnostic code/message, and successful proof
shape. The CLI does not print the internal `observed=timedOut` field. The runbook's
same-selection retry and separate proof/history preservation match the results. Updated
the procedure and evidence-index handoff to T17; followed the local links manually.
No automated tests inspect documentation.

Raw TRX, logs and NUnit attachments remain in `/tmp/dms-1326-t17`. Committed JSON is
pretty-printed from those captures; command argument copies replace the disposable
state path and environment-variable name. Synthetic identities, fingerprints, diagnostic
fields, stdout/stderr and cleanup evidence remain intact. Reviewed for credentials and
payload disclosure. All fixture-owned stacks, databases and temporary state were disposed;
existing local servers were retained.

These are controller/CLI integration exercises, not complete bootstrap/API-consumer
runbook replays. The local broker uses an anonymous superuser with real ACL metadata;
it does not qualify production authentication or isolation. Neither cleanup proofs nor
retained retirement records certify broker remote-copy or independent consumer-store
purge. No production capacity, restored-source recovery, or re-enablement claim is made.
