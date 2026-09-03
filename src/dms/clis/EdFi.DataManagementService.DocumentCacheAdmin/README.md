# Ed-Fi DocumentCache Administration CLI

`EdFi.Api.DocumentCacheAdmin` packages the `dms-document-cache` .NET tool for Ed-Fi DMS
DocumentCache status and administration workflows. It reuses the DMS target registry,
provider adapters, status pipeline, administrative command runner, mutex, telemetry, and
shared JSON contracts without starting the DMS web host.

For configuration keys, see
[`docs/CONFIGURATION.md`](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/docs/CONFIGURATION.md#datamanagementdocumentcache).
For the relational backend runbook context, see
[`docs/RELATIONAL-BACKEND.md`](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/docs/RELATIONAL-BACKEND.md#always-provisioned-documentcache-inventory).

## Installation

Install the published .NET tool package from the Ed-Fi NuGet feed:

```bash
feed="https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json"
version="<published-version>"
dotnet tool install --global EdFi.Api.DocumentCacheAdmin --source "$feed" --version "$version"
```

Use a published package version from the feed. To install the latest stable package, omit
`--version "$version"`.

The installed command is `dms-document-cache`.

Run help:

```bash
dms-document-cache --help
```

## Configuration

The CLI loads DMS configuration from the normal settings and environment providers. Use
`--settings <path>` and `--environment <name>` for non-secret configuration selection, and
use `--datastore postgresql|sqlserver` only to override the provider value for the current
run. Connection strings, CMS credentials, client secrets, and other secrets must come from
settings, environment variables, user secrets, or the deployment secret provider. The CLI
does not expose secret-bearing command-line options.

The tool package includes the default Ed-Fi ApiSchema workspace and uses it when
`AppSettings:UseApiSchemaPath` is absent or `false`. Set `AppSettings:UseApiSchemaPath=true`
and `AppSettings:ApiSchemaPath=<workspace>` only when the run must use an external
bootstrap workspace with `bootstrap-api-schema-manifest.json`.

The `cdc` verbs read the deployment's CDC control-plane settings from
`DataManagement:DocumentCache:Cdc`. Each `cdc` option below overrides one key in that
section for the current run only. Keys that have no command-line option — topic prefix,
partition count, the database setup and connector principals, and the Kafka Connect worker
settings — come only from settings, environment variables, user secrets, or the deployment
secret provider.

Every invocation targets exactly one DocumentCache target:

```bash
dms-document-cache status --data-store-id 1 --tenant-key "district-a" --settings ./appsettings.Production.json --environment Production --datastore postgresql --json
```

Omit `--tenant-key` for the normalized default tenant:

```bash
dms-document-cache status --data-store-id 1 --settings ./appsettings.Production.json --environment Production --json
```

Automation may supply the target or mutating request through `--request-json <path|->`.
When `--request-json` is present, do not also pass target, confirmation, offline writer
admission, or expected-fingerprint options.

Status request JSON is target-only:

```json
{
  "targetKey": {
    "tenantKey": "",
    "dataStoreId": 1
  }
}
```

```bash
dms-document-cache status --request-json status-target.json --settings ./appsettings.Production.json --environment Production --json
```

## Commands

| Command | Purpose | Required confirmation | Offline writer admission |
| --- | --- | --- | --- |
| `status` | Inspect one target and emit the shared 18-06 one-target status DTO. | None | None |
| `activate-new-empty` | Activate DocumentCache for a new empty target. | `newEmptyActivation` | None |
| `activate-offline` | Activate DocumentCache while external writers are closed and drained. | `offlineActivation` | `closedAndDrained` |
| `deactivate-offline` | Disable DocumentCache while external writers are closed and drained. | `offlineDeactivation` | `closedAndDrained` |
| `rebuild-online` | Rebuild DocumentCache while canonical writes remain online. | `onlineCacheRebuild` | None |
| `scrub` | Run the explicit integrity scrub over source/cache/work relationships. | `integrityScrub` | None |
| `recover-cache-ahead` | Run the proven-internal-only cache-ahead recovery workflow. | `internalCacheAheadRecovery` | `closedAndDrained` |
| `cdc` | Verb group for deployment-owned CDC binding operations on one target. | Per verb | None |

The `cdc` verb group carries the deployment-owned CDC binding operations:

| Verb | Purpose | Required confirmation | Other required options |
| --- | --- | --- | --- |
| `cdc enable` | Enable CDC on a target created for this provisioning. | None | `--database-creation-mode`, `--write-admission` |
| `cdc status` | Report deployment-owned CDC readiness for one binding. | None | None |
| `cdc restart` | Restart the binding's connector after affirmative continuity evidence. | None | None |
| `cdc adopt` | Adopt an operator-supplied binding around a complete governed artifact set. | None | `--binding-json` |
| `cdc replace-source` | Replace the physical source behind an enabled target with a new binding generation. | `cdcSourceReplacement` | `--database-creation-mode`, `--write-admission`, `--previous-generation` |
| `cdc retire` | Retire a binding and its governed artifacts. | `cdcBindingRetirement` | None |

`cdc enable` requires the target to already be an operator-configured
`DataManagement:DocumentCache:Targets` entry in the running DMS's own configuration. It is
the only initial-enable path: the physical database must have been created for this CDC
provisioning and must never have admitted a write. An already-provisioned database is not
eligible, and neither the CLI nor the control plane infers eligibility from the schema it
finds.

`cdc adopt` repairs deployment state around an already complete governed-artifact set from
the complete binding record `--binding-json` supplies. It is not a first-time enablement
path, and adoption never infers a binding from the topics or connector configuration that
happen to exist.

`cdc replace-source` points a logical target at a different physical database as a new
binding generation. It fences the connector of the generation named by
`--previous-generation`, then runs the replacing generation through the same initial
readiness sequence `cdc enable` runs, which is why it requires the same
`--database-creation-mode` and `--write-admission` evidence: in-place source reset and topic
reuse are deferred, so the replacing database must be one created for this CDC provisioning
that has never admitted a write, and no governed artifact of the generation it supersedes is
reused.

The replacing source's identity must already have been rotated away from the generation it
replaces. A restore, rollback, or copy carries the replaced database's own
`dms.DataStoreIdentity` row, so its fingerprint is the replaced source's until it is rotated,
and binding a new generation to an unrotated identity would publish one physical source under
two generations. The verb refuses that rather than proceeding.

The generation it supersedes is retained, not retired: its connector configuration and
committed offsets are left for a `cdc retire` that removes them in order.

Each `cdc` verb writes one shared CDC contract document, selected by the verb rather than
by the outcome:

| Verb | Shared contract | Reported outcome values |
| --- | --- | --- |
| `cdc enable` | `CdcAdmission` | `admitted`, `notAdmitted`, `unknown` |
| `cdc status`, `cdc restart` | `CdcStatus` | `ready`, `notReady`, `unknown` |
| `cdc adopt` | `CdcAdoptionProof` | `completed`, `rejectedNoMutation` |
| `cdc retire` | `CdcCleanupProof` | `completed`, `incompleteRetryable` |

Without `--json`, a `cdc` verb prints its outcome followed by the governed names it
operated on — connector name, provider, data store identifier, the opaque instance key,
and the public, progress, and SQL Server schema-history topics. No connection string,
credential, or tenant display name appears in either output mode.

Current packaged production behavior intentionally rejects `activate-offline`,
`deactivate-offline`, and `recover-cache-ahead` unless a trusted downstream
publication-history provider reports `internalOnly` for the same target and
physical-source fingerprint. This tool registers the CDC control plane, so that provider
reads the durable binding state store: it reports `internalOnly` only when a complete,
readable listing for the deployment holds at least one binding, none of them binds this
target, and no retirement record says one once did. A listing that fails, a record that
cannot be read, an empty store, and a store with no deployment key configured all report
`unknown`. Treat `downstreamHistoryPresentOrUnknown` as expected whenever the evidence
does not positively prove the target was never published.

All commands support `--json`. In JSON mode, stdout contains exactly one shared contract
document and no prose. Logs, warnings, progress, and sanitized diagnostics go to stderr or
configured log sinks. Status effective settings and administrative command durations use
numeric `*Seconds` JSON fields; administrative command results expose elapsed workflow time
as `elapsedCommandTimeSeconds`.

## Options

Global options:

| Option | Description |
| --- | --- |
| `--json` | Write the shared JSON contract document to stdout. |
| `-v`, `--verbose` | Enable verbose debug-level logging. |
| `--settings <path>` | Path to a DMS appsettings JSON file. |
| `--environment <name>` | DMS environment name used for configuration loading. |
| `--datastore postgresql|sqlserver` | Target datastore provider override for this run. |

Target and request options:

| Option | Description |
| --- | --- |
| `--data-store-id <id>` | Positive CMS data store identifier. |
| `--tenant-key <value>` | Target tenant key; omitted means the default tenant. |
| `--request-json <path|->` | Path to a shared JSON request document, or `-` for stdin. |

Status timeout options:

| Option | Default | Mapped configuration key |
| --- | ---: | --- |
| `--status-observation-timeout-seconds <seconds>` | `5` | `DataManagement:DocumentCache:Status:StatusObservationTimeout` |
| `--status-timeout-seconds <seconds>` | `30` | `DataManagement:DocumentCache:Status:EndpointTimeout` |

Mutating command options:

| Option | Default | Description |
| --- | ---: | --- |
| `--confirm <token>` | None | Exact command-specific confirmation token. |
| `--expected-physical-source-fingerprint <value>` | None | Optional `sha256:<lowercase-hex>` guard checked before mutation. |
| `--command-timeout-seconds <seconds>` | `86400` | Total workflow budget mapped to `DataManagement:DocumentCache:Administration:WorkflowTimeout`. |
| `--offline-writer-admission closedAndDrained` | None | Required only for `activate-offline`, `deactivate-offline`, and `recover-cache-ahead`. |

`cdc` verb options. Every `cdc` verb accepts the target options above and the options in
this table; the configuration key column names the `DataManagement:DocumentCache:Cdc` key
each one overrides for the current run:

| Option | Description | Mapped configuration key |
| --- | --- | --- |
| `--cdc-binding-state-path <path>` | Root path of the durable CDC binding state store. | `BindingStateStore:RootPath` |
| `--deployment-key <value>` | Opaque deployment key contributing to governed artifact names. | `DeploymentKey` |
| `--instance-key <value>` | Opaque instance key contributing to governed artifact names. | `InstanceKey` |
| `--generation <n>` | Positive binding generation. | `Generation` |
| `--previous-generation <n>` | Positive generation being replaced; required by `cdc replace-source` and never inferred from the generations that exist. | None; request-only. |
| `--kafka-bootstrap-servers <value>` | Kafka bootstrap servers the governed topics are provisioned through. | `KafkaBootstrapServers` |
| `--connect-base-url <url>` | Base URL of the Kafka Connect REST interface. | `ConnectBaseUri` |
| `--max-record-bytes <n>` | Largest record the pipeline must carry end to end. | `MaxRecordBytes` |
| `--durability-profile local\|production` | Durability profile governed topics are created with and validated against. | `DurabilityProfile` |
| `--binding-json <path\|->` | Complete binding record to adopt; required by `cdc adopt` and never inferred. | None; request-only. |
| `--connector-already-absent` | Assert the connector the record names is already gone, so `cdc retire` may proceed without observing its committed offsets. | None; request-only. |

`--generation` and `--previous-generation` are positive integers, and
`--max-record-bytes` is a positive byte count. Zero, negative, malformed, and overflow
values are argument errors, as is a `--durability-profile` outside `local|production`. An
unreadable or malformed `--binding-json` document is an argument error too, and no control
plane operation is attempted.

`cdc enable` additionally requires two exact-token evidence flags,
because the control plane never infers either fact for itself:

| Option | Required value |
| --- | --- |
| `--database-creation-mode` | `created-for-initial-cdc-provisioning` |
| `--write-admission` | `closed-never-opened` |

Timeout values are positive numeric seconds. Zero, negative, malformed, overflow, and
unsupported aliases such as `--timeout`, `--provider-command-timeout`, and
`--mutex-timeout` are argument errors.

## Examples

Inspect status as JSON:

```bash
dms-document-cache status --data-store-id 1 --settings ./appsettings.Production.json --environment Production --datastore postgresql --json
```

Activate a new empty target:

```bash
dms-document-cache activate-new-empty --data-store-id 1 --confirm newEmptyActivation --settings ./appsettings.Production.json --environment Production --json
```

Activate while writers are closed and drained. In the default packaged production state,
this command rejects with `downstreamHistoryPresentOrUnknown` because internal-only proof
is unavailable:

```bash
dms-document-cache activate-offline --data-store-id 1 --confirm offlineActivation --offline-writer-admission closedAndDrained --settings ./appsettings.Production.json --environment Production --json
```

Run an online rebuild with a source-fingerprint guard:

```bash
dms-document-cache rebuild-online --data-store-id 1 --confirm onlineCacheRebuild --expected-physical-source-fingerprint sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef --command-timeout-seconds 86400 --settings ./appsettings.Production.json --environment Production --json
```

Run an explicit integrity scrub:

```bash
dms-document-cache scrub --data-store-id 1 --confirm integrityScrub --settings ./appsettings.Production.json --environment Production --json
```

Deactivate while writers are closed and drained. In the default packaged production
state, this command rejects with `downstreamHistoryPresentOrUnknown` because
internal-only proof is unavailable:

```bash
dms-document-cache deactivate-offline --data-store-id 1 --confirm offlineDeactivation --offline-writer-admission closedAndDrained --settings ./appsettings.Production.json --environment Production --json
```

Recover cache-ahead state only after trusted internal-only evidence exists. In the
default packaged production state, this command rejects with
`downstreamHistoryPresentOrUnknown` because internal-only proof is unavailable:

```bash
dms-document-cache recover-cache-ahead --data-store-id 1 --confirm internalCacheAheadRecovery --offline-writer-admission closedAndDrained --settings ./appsettings.Production.json --environment Production --json
```

Enable CDC on a target whose database was created for this provisioning:

```bash
dms-document-cache cdc enable --data-store-id 1 --database-creation-mode created-for-initial-cdc-provisioning --write-admission closed-never-opened --durability-profile local --settings ./appsettings.Production.json --environment Production --json
```

Report deployment-owned CDC readiness for that binding:

```bash
dms-document-cache cdc status --data-store-id 1 --settings ./appsettings.Production.json --environment Production --json
```

Restart the binding's connector and report the readiness that follows:

```bash
dms-document-cache cdc restart --data-store-id 1 --settings ./appsettings.Production.json --environment Production --json
```

Adopt an existing governed-artifact set under a binding record you supply:

```bash
dms-document-cache cdc adopt --data-store-id 1 --binding-json ./binding.json --settings ./appsettings.Production.json --environment Production --json
```

Retire a binding and its governed artifacts:

```bash
dms-document-cache cdc retire --data-store-id 1 --confirm cdcBindingRetirement --settings ./appsettings.Production.json --environment Production --json
```

Mutating request JSON uses the shared administrative DTO shape:

```json
{
  "targetKey": {
    "tenantKey": "",
    "dataStoreId": 1
  },
  "confirmation": "onlineCacheRebuild",
  "expectedPhysicalSourceFingerprint": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
}
```

Writer-fenced JSON requests carry the same offline writer admission token used by
`--offline-writer-admission`:

```json
{
  "targetKey": {
    "tenantKey": "",
    "dataStoreId": 1
  },
  "confirmation": "offlineActivation",
  "expectedPhysicalSourceFingerprint": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "offlineWriterAdmission": "closedAndDrained"
}
```

## Exit Codes

Exit-code selection is derived from typed result classifications, not message text.

| Code | Meaning |
| ---: | --- |
| 0 | Status or administrative command completed according to the shared result DTO. |
| 1 | Unexpected or unclassified CLI/runtime failure. |
| 10 | Administrative command rejected before mutation by a known guard or preflight rule. |
| 11 | Administrative command failed before mutation, or status failed before a complete DTO. |
| 12 | Administrative command is incomplete and retryable after possible mutation. |
| 64 | Command-line argument, confirmation, or JSON request validation error. |
| 78 | Process-wide configuration error before the target registry or shared command contract could be built. |

For Exit code `12`, retry the same command with the same target and guard values. The
runner reacquires the provider mutex and revalidates durable state before resuming; it does
not reconnect under presumed mutex ownership after cancellation or session loss.

The `cdc` verbs use the same codes, classified from the shared CDC contract they produce:

| `cdc` outcome | Code |
| --- | ---: |
| Write admission opened, adoption completed, or retirement completed. | 0 |
| A readiness answer of any kind, including `notReady` and `unknown`. | 0 |
| The binding is missing, does not match, or the operation is invalid for it. | 10 |
| The binding state store could not be read or written. | 11 |
| Write admission did not open, or a retirement removed only part of the artifact set. | 12 |

Write admission that did not open is retryable rather than a rejection: the binding record
is made durable before any external artifact is created, so such a run may already have
mutated deployment state, and every `cdc` verb is built to be reissued unchanged. A partial
retirement leaves the binding record in place for the same reason. A readiness report is a
success whatever it reports, because the command answered; only a status that could not be
produced at all fails.

## Runbook Links

- DocumentCache operator workflows are in the
  [DocumentCache operations runbook](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/document-cache-documentation/operations-runbook.md).
- Safe new-empty activation is owned by
  [Guarded New-Empty Activation](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#guarded-new-empty-activation).
- Safe offline activation and deactivation are owned by
  [Offline Read-Acceleration Activation](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#offline-read-acceleration-activation)
  and
  [Offline Deactivation](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#offline-deactivation).
- Online rebuild, `Resetting`/`Rebuilding` crash retry, and set-latch rebuild rejection
  are owned by
  [Online Cache Rebuild](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#online-cache-rebuild)
  and the broader
  [Baseline, Rebuild, Deactivation, and Scrub](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#baseline-rebuild-deactivation-and-scrub)
  runbook material.
- Explicit scrub and persistent poison/work-anomaly remediation are owned by
  [Explicit Integrity Scrub](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#explicit-integrity-scrub),
  [Freshness and Reconciliation](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#freshness-and-reconciliation),
  and
  [Projection Health and Deployment-Owned CDC Readiness](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness).
- Cache-ahead recovery routing is owned by
  [Cache-Ahead Invariant Recovery](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cache-ahead-invariant-recovery)
  and
  [Contract Change and Repair Operations](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#contract-change-and-repair-operations).
- `cdc enable` eligibility, the evidence the two exact-token flags assert, and the order the
  initial readiness sequence runs in are owned by
  [Enablement and Initial Readiness Sequence](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence).
- The binding record `cdc adopt`, `cdc replace-source`, and `cdc retire` operate on — its
  identity, its fail-closed creation and cleanup order, and the physical-source fingerprint
  it is bound to — is owned by
  [Deployment-Owned CDC Target and Physical Source Binding](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding).
- The governed artifact names the `cdc` verbs report, and the provider capture artifacts and
  topics they create, are owned by
  [Connector Topology and Provider Setup](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup).
- The continuity evidence `cdc restart` depends on, and the terminal source-history loss it
  cannot recover from, are owned by
  [Source-History Continuity](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity).
- Kafka connector setup, connector teardown, source replacement, binding retirement, topic
  management, CDC bootstrap orchestration, and downstream publication containment are
  governed through the `cdc` verb group and the E19 runbooks. The work package is
  [Add Explicit Local/Bootstrap Connector Registration](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md);
  operational procedure starts with
  [Add CDC Setup, Monitoring, Recovery, and Security Runbooks](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/epics/19-cdc-kafka/07-ops-docs-runbooks.md).
- The CLI story boundary and package verification evidence are in
  [Add a DocumentCache Administration CLI](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/epics/18-document-cache/09-documentcache-administration-cli.md);
  cross-feature DocumentCache runbook evidence is tracked by
  [Add DocumentCache Integration Coverage and Runbooks](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/epics/18-document-cache/07-documentcache-integration-tests-and-runbooks.md).

## Out of Scope

This CLI does not run representation restamp, publish release artifacts, own release
pipeline work, expose HTTP administration endpoints, or provide an interactive wizard.
Kafka connector, topic, and provider capture artifacts are governed only through the `cdc`
verb group above, and only for a target the running DMS already has configured; the
surrounding bootstrap and E2E orchestration that invokes it is owned by the E18/E19 stories
linked above.
