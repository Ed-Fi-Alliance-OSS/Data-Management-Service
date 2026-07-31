---
jira: DMS-1320
source_spike: DMS-1245
epic: DMS-1309
---

# Story: Emit/Provision Provider CDC Key and Database Support

## Design References

- **Connector topology and provider setup**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup
- **Schema and query integration**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#schema-and-query-integration
- **Physical CDC heartbeat object**: reference/design/backend-redesign/design-docs/data-model.md#8-dmscdcheartbeat-opt-in-cdc-integration-object
- **Projector and source decision**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md

The referenced design sections define the opt-in provider objects and access requirements.
This story is only the work package for implementing them.

## Outcome

Deliver the PostgreSQL and SQL Server database setup consumed by relational CDC.

## Dependencies

- Depends on the ordinary source/cache schema from 18-00.

## Implementation Scope

- Add provider DDL/provisioning for the CDC source, key, capture, and heartbeat objects.
- Explicitly exclude `dms.DocumentProjectionWork` from PostgreSQL publications, SQL
  Server CDC capture instances, and CDC-reader grants.
- Integrate those objects with generated manifests, binding-aware validation, and
  diagnostics.
- Add least-privilege connector access setup.
- Add provider metadata queries consumed by 19-00 continuity checks.

## Resolved CDC DDL and Provider Scope

This section records the DMS-1320 implementation resolution for the provider-DDL and
provider-metadata portions of the referenced CDC design. The owning design documents remain
normative for behavior, readiness, source continuity, and public stream contracts; if this
implementation partition needs to change one of those contracts, update the owning design
document first and keep this story as the work package and evidence owner.

### Opt-In Boundary and Create-Only Behavior

- CDC provider setup is a separate opt-in provisioning phase. Ordinary relational
  `ddl emit` / `ddl provision` keeps the E18 schema behavior: it does not create
  `dms.CdcHeartbeat`, PostgreSQL publications or slots, SQL Server CDC capture instances,
  CDC reader principals, CDC grants, or connector metadata.
- The opt-in phase runs only after the complete E18 ordinary schema has been provisioned and
  before 19-04 registers the connector. It must accept the provider, connector principal,
  and binding-derived artifact names as inputs from deployment/bootstrap orchestration rather
  than deriving them from tenant display names, connection strings, or physical server names.
- This story does not make an existing admitted database CDC-eligible. It provides the
  provider setup and validation used by the new-database initial workflow, plus validation
  surfaces used by retries and continuity checks. 19-04 owns the proof that write admission
  is still closed and that the database belongs to the initial provisioning workflow.
- CDC opt-in objects are not part of `EffectiveSchemaHash`, `ResourceKeySeedHash`,
  `RelationalMappingVersion`, or mapping-pack payloads. The DDL/provider manifests should
  report a separate CDC-provider section for the selected target instead of changing the
  effective-schema fingerprint.
- Same-binding reruns are create-or-exact-match only. Missing provider artifacts may be
  created only while the caller is still in the supported initial setup/retry workflow.
  Once a connector has been admitted, validation-only paths must not recreate a missing
  PostgreSQL slot, SQL Server capture instance, or CDC history artifact around retained
  offsets; 19-00 classifies that as source-history continuity evidence.
- Expose provider setup as an explicit implementation contract with caller-selected mode,
  not as a hidden branch inside ordinary DDL. The required modes are:
  `InitialCreateOrExactMatch`, which may create missing provider artifacts only while
  19-04 still proves the new-database initial workflow, and `ValidateOnly`, which performs
  exact-match inspection without creating, dropping, renaming, or repairing provider
  history artifacts.
- The setup command/service input is provider, bound physical-source fingerprint,
  caller-selected setup mode, deployment-supplied setup principal context,
  deployment-supplied connector principal, and binding-derived artifact names. It also
  receives the already-emitted source table identifiers from the ordinary DDL layer.
  Inputs must not be inferred from tenant display names, raw connection strings, server
  names, database names, or connector JSON.
- The setup output is typed provider metadata plus diagnostics: created/matched artifact
  inventory, grant inventory, heartbeat action query, expected message-key columns,
  provider history observations, source fingerprint observed through `dms.DataStoreIdentity`,
  and the CDC-provider manifest section. The output is consumed by 19-00, 19-02, and
  19-04; it is not a connector template and not mutable binding state.
- Provider setup is staged for retry rather than treated as a blanket transactional promise
  across server/provider artifacts. Each step is idempotent by exact match, records enough
  metadata for retry diagnostics, and fails closed on mismatches. A partial failure is
  retried by re-entering the same mode and exact-matching completed work; it is not repaired
  by destructive cleanup unless 19-04/19-07 invokes an explicit governed teardown path.

### Fixed Captured Source Inventory

- The only CDC source tables are `dms.DocumentCache`, `dms.Document`, and the opt-in
  `dms.CdcHeartbeat` singleton. No per-resource, descriptor, authorization, tracked-change,
  or projection-work table is a CDC source.
- `dms.DocumentProjectionWork` is excluded at every database layer: it is not in the
  PostgreSQL publication, has no SQL Server CDC capture instance, receives no connector
  principal grant, and is absent from provider metadata inventories returned for connector
  template generation.
- `dms.CdcHeartbeat` is inserted only when absent, starts with `HeartbeatId = 1` and
  `HeartbeatSequence = 0`, and is captured only as an internal source-position progress
  table. Its rows are not public document-state records and it carries no tenant or document
  data.
- The provider action query is generated from emitted identifiers and returned as provider
  setup metadata for 19-02. It increments `HeartbeatSequence` and sets `HeartbeatAt` with
  the provider UTC clock for `HeartbeatId = 1`. It is not free-form operator SQL.
- Capture all physical columns from the three source tables for v1. The transform and public
  contract decide which fields are retained publicly. This avoids provider-specific column
  subset drift and keeps SQL Server LOB placeholder behavior visible to the transform tests.

### PostgreSQL Provider Setup

- Use one binding-derived logical replication slot and one binding-derived publication per
  connector. The publication includes exactly the three fixed source tables above and uses
  their generated quoted names. Existing publications must exact-match that table inventory.
- Create or validate the logical replication slot with the `pgoutput` plug-in during the
  initial opt-in workflow. An existing slot with a different plug-in, database, or temporary
  status is a validation failure; provider history observations are returned for 19-00 to
  classify retained-offset continuity. The setup does not drop and recreate the slot as
  repair.
- Set `dms.Document` to `REPLICA IDENTITY FULL` so Debezium can key delete records by
  `DocumentUuid`. Do not add a `DocumentCache.DocumentUuid` index or change
  `DocumentCache`'s `DocumentId` primary-key shape.
- The setup principal and connector principal are separate. The setup principal must have
  the provider authority needed to create or inspect the publication, replication slot,
  heartbeat object, replica identity, and grants. Its elevated privileges are not conferred
  on the connector principal and are not recorded in connector templates or binding state.
- The connector principal is deployment-supplied. Production setup validates required
  CDC/replication privileges and known disallowed elevated privileges; it does not make the
  principal a superuser, database owner, table owner, or publication owner. Local bootstrap
  may create a disposable local principal, but the production DDL path grants only to a named
  existing role.
- Database-local grants for the connector principal are limited to schema usage, snapshot
  reads of `dms.Document`, `dms.DocumentCache`, and `dms.CdcHeartbeat`, and update of the
  heartbeat singleton columns needed by the action query. It receives no
  `dms.DocumentProjectionWork` privilege and no write privilege on `dms.Document` or
  `dms.DocumentCache`.
- Connector templates in 19-02 consume the provider metadata from this story, including the
  slot/publication names, quoted table identifiers, generated heartbeat action query, and
  expected `DocumentUuid` message-key columns. The database setup does not render connector
  JSON itself.

### SQL Server Provider Setup

- Enable database CDC when it is absent and the caller is in the initial opt-in setup path.
  SQL Server Agent, database CDC state, capture and cleanup jobs, and retained LSN ranges are
  reported through provider metadata queries. Job tuning and retention policy are operational
  configuration, not hidden DDL defaults.
- The setup principal and connector principal are separate. The setup principal must have
  the provider authority needed to enable database/table CDC, create or inspect capture
  instances and the gating role, inspect capture/cleanup job metadata, and grant connector
  access. Its elevated privileges are not conferred on the connector principal and are not
  recorded in connector templates or binding state.
- Create or exact-match one capture instance each for `dms.DocumentCache`, `dms.Document`,
  and `dms.CdcHeartbeat`. Do not create a capture instance for
  `dms.DocumentProjectionWork`. Capture instances use deterministic binding/provider names
  supplied by the shared artifact-name helper and are validated by name, source object, and
  captured column inventory.
- Use one CDC gating role for the connector principal and pass it as `@role_name` when
  enabling the three capture instances. Use `@supports_net_changes = 0`; v1 consumes the
  all-changes stream, not SQL Server net-change functions.
- The connector login/user is deployment-supplied. Production setup validates required CDC
  read/metadata access and known disallowed elevated privileges. It may create database
  membership for the named principal but does not create or rotate credentials. It grants CDC
  read/metadata access required by Debezium and update of the heartbeat singleton only; it
  grants no `dms.Document` or `dms.DocumentCache` DML and no
  `dms.DocumentProjectionWork` access.
- SQL Server setup does not change projection prerequisites owned by E18, such as RCSI or
  server-level `nested triggers`. It may report those values in diagnostics, but activation
  and target-initialization validation remain owned by the E18/19-00 workflow.
- Provider metadata returned to 19-02 includes capture instance names, table identifiers,
  generated heartbeat action query, expected `DocumentUuid` message-key columns, and
  provider-observed column metadata needed for connector-template validation. This story does
  not render the connector JSON or create Kafka topics.

### Manifests, Validation, and Diagnostics

- Add a CDC-provider manifest or manifest section that records provider, opt-in status,
  source table inventory, provider artifact names, heartbeat table/action-query hash or
  literal, grant inventory, and validation observations. Do not include connection strings,
  credentials, tenant display names, or unsanitized server/catalog identifiers.
- Binding-aware validation exact-matches the caller-supplied provider artifact names,
  current `dms.DataStoreIdentity` source fingerprint, captured source inventory, key
  inventory, heartbeat object, grants, and provider metadata. A mismatch is a fail-closed
  diagnostic, not an automatic rename, offset reset, or source-history repair.
- Provider metadata queries must support 19-00 source-position and continuity checks:
  PostgreSQL slot/publication existence, plug-in, database, retained WAL/flush positions, and
  publication table inventory; SQL Server database/table CDC state, capture instance
  inventory, captured columns, capture/cleanup job state, retained min/max LSN range, and
  heartbeat capture visibility.
- Diagnostics distinguish setup-principal failure, connector-principal privilege failure,
  missing required source objects, work-table capture/grant violations, provider history
  unavailability, and provider history loss evidence. They should identify safe provider and
  binding artifact names but never log credentials or document payloads.

## Acceptance Evidence

- Story-owned test names and PR evidence trace provider setup coverage to `CDC-INV-06`.
  Provider-barrier and source-history contract evidence remains owned by the stories listed
  in the central traceability table; this story supplies the provider metadata surfaces they
  consume.
- PostgreSQL and SQL Server DB-apply tests cover the provider object inventory and source
  records defined by the design references.
- Provisioning tests cover opt-in, setup modes, eligibility, rerun, partial retry, and
  binding-aware validation.
- Principal-access and provider-metadata tests cover the design-owned security and
  continuity observations.
- Capture inventories and raw-record fixtures prove work-table DML produces no captured
  source record.
- Manifest/snapshot tests prove CDC opt-in metadata is reported separately from
  `EffectiveSchemaHash`, `ResourceKeySeedHash`, `RelationalMappingVersion`, and ordinary E18
  DDL output.
- PostgreSQL provider tests cover exact publication membership, `pgoutput` slot validation,
  `REPLICA IDENTITY FULL` on `dms.Document`, heartbeat action-query capture, and absence of
  work-table grants.
- SQL Server provider tests cover database/table CDC enablement, three and only three
  capture instances, full captured-column inventories, CDC gating-role access, heartbeat
  action-query capture, job/retention metadata, and absence of work-table grants.

## Not Assigned to This Story

- Connector JSON generation and registration are assigned to 19-02 and 19-04.
- Kafka topics, Kafka ACLs, Connect offset-store provisioning, connector status polling, and
  destructive binding teardown orchestration are assigned to 19-04 and 19-07.
- Projector implementation is assigned to E18.
