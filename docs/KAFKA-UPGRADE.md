# Kafka startup and persistence

The shipped digest-pinned Kafka 4.3.2 broker uses a single-node, combined
broker/controller KRaft deployment in
[kafka-broker.compose.yml](../eng/docker-compose/kafka-broker.compose.yml) and its
[CDC overlay](../eng/docker-compose/kafka-cdc.compose.yml). Qualification covers
fresh startup and current-image recreation with retained storage for PostgreSQL
and SQL Server connectors. The image security disposition is separate:
[Kafka image security assessment](KAFKA-IMAGE-SECURITY.md).

## Retained managed deployments

The lifecycle wrapper fingerprints the original Compose inputs, including
`kafka-broker.compose.yml`. Changing the pin changes that fingerprint, so retained
startup rejects a checkout/image change. Keep the deployment's original inputs
and image. Do not edit `.cdc-deployments` hashes, delete controller state, invoke
initial bootstrap again, or bypass managed startup to accept a different image.
See [managed shutdown/startup](../reference/cdc-documentation/operations-runbook.md#managed-shutdown-and-startup)
and [state preservation](../reference/cdc-documentation/operations-runbook.md#preserve-deployment-state).

## Storage and startup

- The broker uses `node.id=1`, `process.roles=broker,controller`, and the static
  voter `1@dms-kafka1:9093`. Retain the controller identity, listeners and cluster ID.
- Retain the exact `kafka-data` volume at `/tmp/kraft-combined-logs`, including
  `meta.properties`, `__cluster_metadata-0`, all topic partitions and internal
  topics. Retain `kafka-logs` and the original Compose project/volume names.
  A new project or empty volume creates a different cluster. `docker compose
  down -v` destroys the required history.
- The image uses `appuser` UID/GID `1000:1000`. The shipped shell command starts
  as root, changes ownership of the mount directory only, then executes
  `/etc/kafka/docker/run` as `appuser`. Kafka itself runs unprivileged. The command
  does not recursively repair wrongly owned existing files.
- The startup script invokes `KafkaDockerWrapper setup`, then starts
  `/opt/kafka/config/server.properties`. Already formatted storage is reused.
  Keep `KAFKA_LOG_DIRS=/tmp/kraft-combined-logs`. Never manually reformat a retained
  volume or replace its `meta.properties` to overcome startup errors.
- Preserve the provider's WAL/replication slot or capture history, Connect
  config/offset/status topics, controller receipts and state, and every peer on
  the shared worker. Establish verified connector shutdown before stopping the
  broker. Maintain writer exclusion until fresh readiness succeeds.

Fresh storage uses `metadata.version=4.3-IV0`. Persistence tests check that it
survives broker recreation using `kafka-features.sh --bootstrap-server <broker>
describe`. These tests do not change feature levels or qualify binary upgrades,
downgrades, multi-node deployments or restoring CDC state from backups.

## Reproduce the checks

Use the [qualification prerequisites](CDC-QUALIFICATION.md), including the
qualified Connect image and provider images, Docker, .NET 10 and PowerShell 7.5+.
The runner resolves the current broker from Compose and checks it against the
application's image authority. Each provider's `RecordSize` lane includes one
Compose persistence case, which its required-case report enforces.

```powershell
./eng/ci/Invoke-CdcQualification.ps1 -Lane Postgresql -Suite RecordSize -PullImages
./eng/ci/Invoke-CdcQualification.ps1 -Lane Mssql -Suite RecordSize -PullImages
```

For just the two persistence cases, supply the same prerequisite variables and
set `CDC_CONNECTOR_TEMPLATE_FAIL_FAST=true`:

```powershell
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/EdFi.DataManagementService.Backend.Cdc.Tests.Integration.csproj -c Release --filter 'FullyQualifiedName~Given_Cdc_Compose_Persistence'
```

Tests create isolated networks, ports, containers and named volumes and remove
their owned resources afterward. They compare exact cluster/topic identity,
storage metadata, stopped source offsets and connector configuration, read a
sentinel at its original partition/offset, resume both providers, and run an
acknowledged broker-size recreation followed by fresh readiness. They also check
volume ownership, static KRaft configuration and unprivileged PID 1.

Dated results and evidence fingerprints are recorded in the
[DMS-1675 implementation record](../reference/design/DMS-1675-kafka-upgrade-and-smoke-tests.md).
