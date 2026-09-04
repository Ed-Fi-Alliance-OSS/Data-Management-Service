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
| `restamp-preview` | Create an auditable representation-restamp preview. | None | `closedAndDrained` |
| `restamp-execute` | Execute or resume a representation restamp operation. | `representationRestamp` | `closedAndDrained` |

Current packaged production behavior intentionally rejects `activate-offline`,
`deactivate-offline`, and `recover-cache-ahead` unless a trusted downstream
publication-history provider reports `internalOnly` for the same target and
physical-source fingerprint. The default provider reports `unknown` because durable CDC
binding/history evidence is not available in this product scope. Treat
`downstreamHistoryPresentOrUnknown` as expected in that default state.

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

Representation restamp options are `--mode`, `--reason`, `--project-name`,
`--resource-name`, `--document-uuid`, and `--operation-id`.

```bash
dms-document-cache restamp-preview --data-store-id 1 --mode tracking --reason "repair" --project-name Ed-Fi --resource-name Student --offline-writer-admission closedAndDrained
dms-document-cache restamp-execute --data-store-id 1 --operation-id 00000000-0000-0000-0000-000000000001 --confirm representationRestamp --offline-writer-admission closedAndDrained
```

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
| `--offline-writer-admission closedAndDrained` | None | Required for writer-fenced commands, including representation restamp preview and execute. |

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

## Representation Restamp

Use representation restamp only for an offline correction that changes composed API or
stream representation bytes without changing domain fields, keys, or deletion history.
The operation advances the existing canonical `ContentVersion` and
`ContentLastModifiedAt` values and their root or descriptor mirrors. It does not add an
ETag algorithm, Change Query event type, projection epoch, or Kafka ordering field.

### Offline prerequisites

Before preview, stop and verify all access to the target data store: DMS replicas, API
readers and writers, projector loops, direct-fill or bulk/seed loaders, administrative
peers, and external writers. Keep them stopped through execute or every resume attempt.
Deploy the corrected materializer/composer while the target remains offline. The exact
`closedAndDrained` token acknowledges this external fence; the CLI does not establish or
certify it.

Choose the mode from durable DocumentCache state, with
`CacheAheadRecoveryRequired=false`:

| Mode | Required lifecycle | Completion claim |
| --- | --- | --- |
| `tracking` | `Tracking` | Canonical restamp complete and projection work queued (`projectionWorkQueued`). |
| `disabled` | `Disabled` | Canonical-only restamp complete (`canonicalOnlyComplete`). |

The CLI rejects `Resetting`, `Rebuilding`, a set cache-ahead latch, unavailable lifecycle,
or a mode/lifecycle mismatch before the next page is stamped. Do not clear a latch or
change lifecycle merely to bypass rejection; diagnose and recover through the E18
DocumentCache procedures linked below.

### Preview and inspect

Preview creates the durable operation manifest and returns its opaque operation ID,
immutable pre-restamp boundary, selected mode, physical-source fingerprint, and selected
document count. It does not restamp documents or change mirrors, projection work, cache
rows, or Kafka state.

Preview one resource:

```bash
dms-document-cache restamp-preview --data-store-id 1 --mode tracking --reason "Recompose Student representations after corrected materializer deployment" --project-name Ed-Fi --resource-name Student --offline-writer-admission closedAndDrained --expected-physical-source-fingerprint sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef --settings ./appsettings.Production.json --environment Production --json
```

The shared request JSON for the same preview is:

```json
{
  "targetKey": {
    "tenantKey": "",
    "dataStoreId": 1
  },
  "offlineWriterAdmission": "closedAndDrained",
  "mode": "tracking",
  "reason": "Recompose Student representations after corrected materializer deployment",
  "scope": {
    "scopeType": "resource",
    "projectName": "Ed-Fi",
    "resourceName": "Student"
  },
  "expectedPhysicalSourceFingerprint": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
}
```

Preview a bounded UUID scope by repeating `--document-uuid`, or use this request shape:

```json
{
  "targetKey": {
    "tenantKey": "",
    "dataStoreId": 1
  },
  "offlineWriterAdmission": "closedAndDrained",
  "mode": "disabled",
  "reason": "Recompose the identified representations after corrected materializer deployment",
  "scope": {
    "scopeType": "documentUuids",
    "documentUuids": [
      "11111111-1111-1111-1111-111111111111",
      "22222222-2222-2222-2222-222222222222"
    ]
  },
  "expectedPhysicalSourceFingerprint": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
}
```

Supply request JSON as the only command DTO input:

```bash
dms-document-cache restamp-preview --request-json restamp-preview.json --settings ./appsettings.Production.json --environment Production --json
```

Do not combine `--request-json` with target, scope, mode, reason, confirmation, offline
admission, or expected-fingerprint options. Inspect the JSON result and retain
`result.operationId`. Confirm the target, fingerprint, mode, scope summary,
`preRestampBoundary`, `previewDocumentCount`, and `state` before execute.

### Execute or resume

Execute uses only the previewed target and operation ID. Scope, mode, reason, and boundary
are immutable manifest values and are not execute inputs:

```bash
dms-document-cache restamp-execute --data-store-id 1 --operation-id 11111111-1111-1111-1111-111111111111 --confirm representationRestamp --offline-writer-admission closedAndDrained --settings ./appsettings.Production.json --environment Production --json
```

The shared execute request JSON is:

```json
{
  "targetKey": {
    "tenantKey": "",
    "dataStoreId": 1
  },
  "operationId": "11111111-1111-1111-1111-111111111111",
  "offlineWriterAdmission": "closedAndDrained",
  "confirmation": "representationRestamp"
}
```

```bash
dms-document-cache restamp-execute --request-json restamp-execute.json --settings ./appsettings.Production.json --environment Production --json
```

A completed Tracking result has `result.state` equal to `completed` and
`result.claimLevel` equal to `projectionWorkQueued`. A completed Disabled result has
`result.claimLevel` equal to `canonicalOnlyComplete`. These claims are intentionally
bounded: the utility does not drain projection work, publish records, verify Kafka
delivery, purge prior Kafka values, or certify a replacement CDC baseline.

If execution is interrupted, times out, exhausts a retry, or loses its mutex session after
a committed page, preserve the offline fence and manifest. Exit code `12`,
`status: incompleteRetryable`, or `result.state: incomplete` means to rerun
`restamp-execute` with the same target and operation ID. A new invocation reacquires the
mutex and revalidates the target, fingerprint, lifecycle, latch, and immutable manifest
mode. Never start a new preview to substitute for an incomplete operation, and never retry
a completed operation ID.

### Verify and restore service

For Tracking, start only corrected DMS/projector instances, allow ordinary queued work to
catch up, and then verify affected public resources have unchanged domain fields, a higher
`contentVersion`, a different strong ETag, and a later `_lastModifiedDate`. Verify the
current resource appears in a later live-resource Change Query window and that no synthetic
`/deletes` or `/keyChanges` record was created. Observe Kafka separately if configured;
projection catch-up and the `projectionWorkQueued` claim do not prove Kafka delivery.

For Disabled, start only corrected DMS API instances and verify relational API ETags,
`_lastModifiedDate`, and Change Query visibility. There is no projection, cache, or Kafka
publication expectation. A later ordinary activation or rebuild establishes projection
state through its normal baseline procedure.

If corrected bytes remove or mask sensitive information previously published to Kafka,
do not treat a higher-version replacement, tombstone, compaction, or successful restamp as
purge evidence. Follow the E19 sensitive-data containment and destructive binding-
generation retirement procedure linked below before restoring CDC access.

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
- Kafka connector setup, connector teardown, source replacement, binding retirement, topic
  management, CDC bootstrap orchestration, and downstream publication containment are E19
  concerns. Start with
  [Add CDC Setup, Monitoring, Recovery, and Security Runbooks](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/epics/19-cdc-kafka/07-ops-docs-runbooks.md).
- The CLI story boundary and package verification evidence are in
  [Add a DocumentCache Administration CLI](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/epics/18-document-cache/09-documentcache-administration-cli.md);
  cross-feature DocumentCache runbook evidence is tracked by
  [Add DocumentCache Integration Coverage and Runbooks](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/reference/design/backend-redesign/epics/18-document-cache/07-documentcache-integration-tests-and-runbooks.md).

## Out of Scope

This CLI does not configure Kafka connectors, create or delete topics, retire source
bindings, replace a physical source, orchestrate CDC bootstrap, drain restamp projection
work, publish or verify Kafka records, purge prior Kafka values, certify a replacement CDC
baseline, publish release artifacts, own release pipeline work, expose HTTP administration
endpoints, or provide an interactive wizard. Those workflows are owned by the E18/E19
stories linked above.
