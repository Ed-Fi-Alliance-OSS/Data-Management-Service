# Kafka 3.9.0 to 4.3.2 compatibility

This assessment covers the single-node, combined broker/controller KRaft deployment
in [kafka-broker.compose.yml](../eng/docker-compose/kafka-broker.compose.yml) and its
[CDC overlay](../eng/docker-compose/kafka-cdc.compose.yml). It does not qualify
ZooKeeper migration, multi-node rolling upgrades, other architectures or arbitrary
Kafka distributions. The image security disposition is separate:
[Kafka image security assessment](KAFKA-IMAGE-SECURITY.md).

## Supported boundary

The storage compatibility test replaces the historical digest-pinned Kafka 3.9.0
broker with the shipped 4.3.2 broker while retaining the same named data volume.
It uses real PostgreSQL and SQL Server connectors, the shipped startup command,
and the controller's verified stop, retained startup and fresh validation.

**This is not an end-to-end managed deployment upgrade procedure.** The lifecycle
wrapper fingerprints the original Compose inputs, including
`kafka-broker.compose.yml`. Changing the pin changes that fingerprint. Existing
managed deployments therefore reject a checkout/image change at retained startup.
Do not edit `.cdc-deployments` hashes, delete controller state, invoke initial
bootstrap again, or bypass managed startup to make an existing deployment accept
the new pin. Keep that deployment's original inputs and image until a separately
qualified managed upgrade procedure is available. See
[managed shutdown/startup](../reference/cdc-documentation/operations-runbook.md#managed-shutdown-and-startup)
and [state preservation](../reference/cdc-documentation/operations-runbook.md#preserve-deployment-state).
Fresh managed deployments use 4.3.2 directly.

For a deployment independently managed outside that wrapper, its deployment owner
must provide the outage, state preservation and post-upgrade validation procedure.
The compatibility prerequisites below are inputs to that procedure, not permission
to bypass DMS continuity controls.

## Storage and startup prerequisites

- Both source and target use KRaft with `node.id=1`,
  `process.roles=broker,controller`, and the static voter
  `1@dms-kafka1:9093`. Keep the controller identity, listeners and original cluster
  ID. Do not introduce a dynamic-quorum migration as part of this image change.
- Retain the exact `kafka-data` volume at `/tmp/kraft-combined-logs`, including
  `meta.properties`, `__cluster_metadata-0`, all topic partitions and internal
  topics. Retain `kafka-logs` too. Keep the original Compose project/volume names.
  A new project or empty volume creates a different cluster, even if topic names
  look identical. `docker compose down -v` destroys the required history.
- Both inspected images use `appuser` UID/GID `1000:1000`. The shipped shell
  command starts as root, changes ownership of the mount directory only, then
  executes `/etc/kafka/docker/run` as `appuser`. Kafka itself runs unprivileged.
  The command does not recursively repair wrongly owned existing files. Confirm
  the entire retained data tree is readable/writable by UID 1000 before upgrading;
  investigate unexpected ownership while the broker is stopped.
- Both image startup scripts invoke `KafkaDockerWrapper setup`, then start
  `/opt/kafka/config/server.properties`. Already formatted storage is reused.
  Keep `KAFKA_LOG_DIRS=/tmp/kraft-combined-logs`; relying on the image's other
  default path can silently select empty storage. Never manually reformat a
  retained volume or replace its `meta.properties` to overcome startup errors.
- Preserve the provider's WAL/replication slot or capture history, Connect
  config/offset/status topics, controller receipts and state, and every peer on
  the shared worker. Establish verified connector shutdown before stopping the
  broker. Maintain writer exclusion until fresh readiness succeeds. A volume
  snapshot alone does not authorize restoring CDC continuity after rollback.

## Metadata version and rollback

Apache's [4.3 upgrade guidance](https://kafka.apache.org/43/getting-started/upgrade/)
permits KRaft upgrades from 3.3 or later and treats binary replacement and feature
finalization as separate steps. The tested 3.9.0 volume starts at
`metadata.version=3.9-IV0`; the binary upgrade must retain that version. A fresh
4.3.2 volume uses `4.3-IV0`. The regression checks both cases explicitly with
`kafka-features.sh --bootstrap-server <broker> describe`. The captured feature
reports also retain `kraft.version=0` (static quorum). Newly introduced features
remain at level 0 on the upgraded volume; a binary upgrade does not enable all
fresh-cluster 4.3 feature defaults.

This change does not finalize features with `upgrade --release-version 4.3`.
Feature finalization changes the storage compatibility boundary and requires its
own qualification and deployment decision. Apache documents metadata downgrade
restrictions across this transition. Returning to 3.9.0 after finalization, or
restoring old Kafka/controller backups as a CDC recovery method, is not qualified.
Even before finalization, these forward-upgrade tests do not establish safe binary
rollback. On failure, keep writers fenced, preserve evidence and use the
[CDC incident/recovery guidance](../reference/cdc-documentation/operations-runbook.md#native-recovery-and-incomplete-shutdown).

## Reproduce the compatibility checks

Use the [qualification prerequisites](CDC-QUALIFICATION.md), including the
qualified Connect image and provider images, Docker, .NET 10 and PowerShell 7.5+.
The historical source is deliberately pinned independently of the current image:

```text
apache/kafka:3.9.0@sha256:fbc7d7c428e3755cf36518d4976596002477e4c052d1f80b5b9eafd06d0fff2f
apache/kafka:4.3.2@sha256:852a0d49fc4e656b022e7f23785411802c9a088c78a7c5e39f33969de9c227a7
```

The normal provider `RecordSize` lanes include both fresh and cross-version
persistence. `-PullImages` resolves the old image from the fixture constant as well
as the current shipped pin; it records both image identities. Each provider's
required-case report rejects results missing either persistence case.

```powershell
./eng/ci/Invoke-CdcQualification.ps1 -Lane Postgresql -Suite RecordSize -PullImages
./eng/ci/Invoke-CdcQualification.ps1 -Lane Mssql -Suite RecordSize -PullImages
```

For just the four persistence cases, after supplying the same prerequisite
variables and setting `CDC_CONNECTOR_TEMPLATE_FAIL_FAST=true`:

```powershell
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/EdFi.DataManagementService.Backend.Cdc.Tests.Integration.csproj -c Release --filter 'FullyQualifiedName~Given_Cdc_Compose_Persistence'
```

`Category=CdcKafkaUpgrade` selects just the two cross-version cases. Tests create
isolated networks, ports, containers and named volumes and remove their owned
resources afterward. No existing user deployment is upgraded by these tests.
The live checks compare exact cluster/topic identity, storage metadata, stopped
source offsets and connector configuration, read a pre-upgrade sentinel at its
original partition/offset, resume both providers, and run an acknowledged broker
size recreation followed by fresh readiness. They also check volume ownership,
static KRaft configuration and unprivileged PID 1. The size recreation proves a
second 4.3.2 startup on the retained 3.9.0 data.

Results and evidence fingerprints are recorded in the
[DMS-1675 implementation record](../reference/design/DMS-1675-kafka-upgrade-and-smoke-tests.md).
