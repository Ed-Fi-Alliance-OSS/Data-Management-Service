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

Activate while writers are closed and drained:

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

Deactivate while writers are closed and drained:

```bash
dms-document-cache deactivate-offline --data-store-id 1 --confirm offlineDeactivation --offline-writer-admission closedAndDrained --settings ./appsettings.Production.json --environment Production --json
```

Recover cache-ahead state only after trusted internal-only evidence exists:

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
bindings, replace a physical source, orchestrate CDC bootstrap, run representation restamp,
publish release artifacts, own release pipeline work, expose HTTP administration
endpoints, or provide an interactive wizard. Those workflows are owned by the E18/E19
stories linked above.
