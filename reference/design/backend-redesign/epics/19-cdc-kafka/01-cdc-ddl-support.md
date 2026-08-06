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
  receives the already-emitted source table and column inventory from the ordinary DDL
  layer.
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
- DMS-1320 consumes the ordinary DDL layer's emitted table/column inventory for
  `dms.Document`, `dms.DocumentCache`, and `dms.CdcHeartbeat`. It must not maintain a
  separate hard-coded column model. Live inspection is used only to exact-match the
  provisioned database against that expected emitted inventory.
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

## Clarifying Questions and Answers

### Questions 1

1. Should DMS-1320 deliver a user-facing CDC provider setup command/CLI, or only an internal service/API consumed by 19-04/bootstrap orchestration and tests?
2. Which story owns the shared deterministic artifact-name helper for PostgreSQL slots/publications and SQL Server capture instances/gating roles: DMS-1320, 19-00, or 19-04?
3. What is the exact CDC-provider manifest boundary: a new standalone artifact, a new section in an existing DDL/provisioning manifest, setup-result JSON only, or more than one of those surfaces?
4. Should the CDC-provider manifest be emitted from planned setup inputs before database execution, from live database inspection after setup/validation, or from both?
5. What exact per-provider setup-principal and connector-principal privilege matrix is acceptance criteria, including disallowed elevated-role checks and whether heartbeat UPDATE grants must be column-level?
6. What stable diagnostic/result contract should provider setup expose for setup-principal failure, connector-principal privilege failure, work-table capture/grant violations, provider-history unavailability, and provider-history loss evidence?
7. For an existing SQL Server CDC gating role, what exact-match criteria apply beyond role name and connector-principal membership, especially if the role has extra members or permissions?

### Answers 1

1. DMS-1320 should deliver an internal provider setup/validation service API, typed input/output models, and test helpers only. It should not add a supported user-facing CDC setup CLI. The local/bootstrap command surface and operator workflow belong to 19-04, which calls this service with the selected mode, principals, source inventory, physical-source fingerprint, and binding-derived artifact names.
2. 19-00 owns the shared deterministic artifact-name helper because these names are binding-derived deployment state. Treat that helper as an explicit early/shared 19-00 deliverable when DMS-1320 implementation starts before the rest of 19-00. The helper should live with the CDC binding model and return the provider artifact names consumed by DMS-1320, plus the connector/topic names consumed by 19-02 and 19-04. DMS-1320 should accept those names as inputs and exact-match them; it must not derive names from tenant display names, connection strings, server names, database names, or connector JSON. The 19-00 story should be updated during tasking if this helper ownership is not already explicit there.
3. Use two surfaces backed by one model: DMS-1320 returns typed setup-result metadata to callers, and serializes the CDC-provider portion as a standalone `cdc-provider.{dialect}.manifest.json` artifact when artifact output is requested. Do not add CDC provider data to `ddl.manifest.json`, `effective-schema.manifest.json`, `relational-model.{dialect}.manifest.json`, `.mpack` payloads, or `.bootstrap/bootstrap-manifest.json`.
4. Emit the CDC-provider manifest only from live database inspection after setup or validate-only execution. Planned inputs may appear as expected names or expected source inventory inside the result, but every manifest artifact, grant, heartbeat, source-fingerprint, and provider-history field must be based on the observed database state. A pre-execution plan is useful for logging/tests but is not the acceptance manifest.
5. The privilege acceptance matrix is:
   - PostgreSQL setup principal: must be able to connect to the database, create/inspect the heartbeat table, insert the singleton when absent, set `dms.Document` to `REPLICA IDENTITY FULL`, create/inspect the binding publication, create/inspect the `pgoutput` logical replication slot, and grant the required schema/table/column privileges. It may be an elevated setup identity; DMS-1320 reports missing authority as setup-principal failure but does not reject the setup principal merely for owning objects or having elevated setup rights.
   - PostgreSQL connector principal: must be an existing login role with `REPLICATION`, database `CONNECT`, `USAGE` on schema `dms`, `SELECT` on exactly `dms.Document`, `dms.DocumentCache`, and `dms.CdcHeartbeat`, and column-level `UPDATE` only on `dms.CdcHeartbeat.HeartbeatSequence` and `dms.CdcHeartbeat.HeartbeatAt`. It must have no privilege on `dms.DocumentProjectionWork`, no write privilege on `dms.Document` or `dms.DocumentCache`, and no table/schema/database/publication ownership, `SUPERUSER`, `CREATEDB`, `CREATEROLE`, `BYPASSRLS`, `pg_read_all_data`, or `pg_write_all_data`. The `exactly` checks apply to DMS/provider-managed DMS table grants, ownership, and role attributes; they do not prohibit PostgreSQL system catalog visibility or provider metadata visibility required for logical replication and validation.
   - SQL Server setup principal: must be able to connect to the database, enable database CDC when needed, enable/inspect table CDC for the three captured tables, create/inspect the CDC gating role, inspect capture/cleanup job metadata, and grant the required connector permissions. It may use the elevated SQL Server authority needed for setup; DMS-1320 reports missing authority as setup-principal failure rather than treating setup elevation as connector privilege.
   - SQL Server connector principal: must be an existing login/user with database `CONNECT`, membership in the exact binding-derived CDC gating role, snapshot `SELECT` on exactly `[dms].[Document]`, `[dms].[DocumentCache]`, and `[dms].[CdcHeartbeat]`, Debezium-required CDC metadata/read access for the configured capture instances, and column-level `UPDATE` only on `[dms].[CdcHeartbeat].[HeartbeatSequence]` and `[HeartbeatAt]`. It must have no privilege on `[dms].[DocumentProjectionWork]`, no write privilege on `[dms].[Document]` or `[dms].[DocumentCache]`, and no ownership, `db_owner`, `db_ddladmin`, `db_datareader`, `db_datawriter`, `sysadmin`, `securityadmin`, `serveradmin`, or `dbcreator` membership. The `exactly` checks apply to DMS/provider-managed DMS table grants, explicit database-role memberships, ownership, and server-role memberships; they do not prohibit SQL Server system catalog or CDC metadata visibility required by Debezium and provider validation. The heartbeat update grant is column-level for both providers; `HeartbeatId` is never updatable by the connector principal.
6. Expose one stable `CdcProviderSetupResult` contract from both setup modes. It should include provider, mode, outcome, physical-source fingerprint, artifact inventory, source-table inventory, grant inventory, heartbeat action query/hash, expected message-key columns, provider-history observations, manifest payload, and `diagnostics[]`. Each diagnostic should carry stable fields: `code`, `category`, `severity`, `principalKind`, `artifactKind`, safe artifact/object name, expected value, observed value, provider error class when available, and retry/continuity classification. Required categories are `SetupPrincipalFailure`, `ConnectorPrincipalPrivilegeFailure`, `MissingRequiredSourceObject`, `WorkTableCaptureViolation`, `WorkTableGrantViolation`, `ProviderHistoryUnavailable`, and `ProviderHistoryLossEvidence`. `ProviderHistoryUnavailable` maps to source-history `unknown`; `ProviderHistoryLossEvidence` maps to source-history `lost`. Setup stops after the first error diagnostic from a provider setup step, so one validate-only run is not an exhaustive full-database remediation list. Callers remediate by retrying the same mode: completed work is exact-matched, and the next blocking fault is reported. Callers must not parse provider exception text, and diagnostics must not include credentials, connection strings, document payloads, tenant display names, or unsanitized physical identifiers.
7. An existing SQL Server CDC gating role exact-matches only when it is a normal database role with the binding-derived name, is referenced by all three expected capture instances and no unexpected capture instance, has `@supports_net_changes = 0` on those capture instances, and those instances expose the exact expected source objects and captured columns. Its direct membership set must be exactly the connector database principal and no other users, groups, or roles. The role must own no schemas or objects, be a member of no other role, and carry no explicit permissions beyond the CDC gating semantics supplied by SQL Server capture configuration; base table snapshot grants and heartbeat column-update grants are checked separately on the connector principal. Extra members or permissions are fail-closed validation mismatches and are never removed automatically by DMS-1320.

### Questions 2

1. What is the source of truth for the expected physical column inventory and ordering used by DMS-1320 when creating or exact-matching SQL Server capture instances and provider metadata: the fixed DMS table model, live inspection after ordinary E18 provisioning, or caller-supplied emitted-column inventory?
2. What exact PostgreSQL publication properties must be created and exact-matched beyond the three-table membership, including publish operation set, absence of row filters or column lists, `publish_via_partition_root`, and any provider-version-specific options?
3. For an existing PostgreSQL logical replication slot, what exact-match and diagnostic criteria apply beyond name, `pgoutput` plug-in, database, and temporary status, especially for active slots, invalidated/lost slots, retained WAL positions, and slot state that has advanced before connector registration?
4. For SQL Server capture instances, which `sp_cdc_enable_table` options are fixed acceptance criteria beyond capture instance name, role name, captured columns, and `@supports_net_changes = 0`, such as `@index_name`, `@filegroup_name`, and `@allow_partition_switch`?
5. If SQL Server database CDC is already enabled but capture or cleanup jobs are missing, disabled, stopped, or have retention values outside deployment policy, should DMS-1320 create/repair them, fail exact-match validation, or only report observations for 19-00/19-04 status handling?
6. Should connector-principal privilege validation be based only on setup-principal catalog/effective-permission inspection, or must DMS-1320 also support optional live probe operations using connector-principal credentials when callers can supply them?

### Answers 2

1. Use the caller-supplied emitted-column inventory from the ordinary E18 DDL layer as the expected inventory for DMS-1320. That inventory is derived from the fixed DMS table model, but DMS-1320 should not maintain a second hard-coded copy and should not treat live inspection as the source of truth. The inventory passed to DMS-1320 is limited to the three CDC source tables and should use the same quoted physical identifiers and ordinal column metadata that connector-template generation will consume. Provider setup first validates the live source tables against the emitted inventory, then creates or exact-matches capture using every emitted physical column in emitted table-ordinal order. A live column mismatch is a fail-closed `MissingRequiredSourceObject` or provider-metadata diagnostic, not a reason to adapt the capture instance.
2. Create and exact-match each PostgreSQL publication as a binding-derived publication over exactly the three emitted base tables: `dms.DocumentCache`, `dms.Document`, and `dms.CdcHeartbeat`. The publication must not be `FOR ALL TABLES`, schema-wide, partition-root based, or include `dms.DocumentProjectionWork`. Use an explicit operation set of `insert`, `update`, and `delete`; do not publish `truncate`. Exact-match no row filters, no column lists, and `publish_via_partition_root = false` when that option exists for the provider version. DMS-1320 should not opt into provider-version-specific publication options; unsupported options are recorded as not applicable, while any supported option that changes the captured row set is a validation mismatch.
3. An existing PostgreSQL slot exact-matches only when it is a logical, permanent, database-local `pgoutput` slot with the binding-derived name, `two_phase = false` when exposed, readable retained-position metadata, and no invalidation or lost-WAL state. In `InitialCreateOrExactMatch`, before 19-04 registers the connector, the slot must be inactive and must not show consumption advancement beyond the creation position observed for the same initial workflow; an active slot, an unprovable prior creation position, or pre-registration advancement is a fail-closed provider-history diagnostic and DMS-1320 must not drop or recreate it. In `ValidateOnly`, an active slot is allowed as an observation because the registered connector may own it. `wal_status = lost`, an invalidation reason, a missing `restart_lsn`/`confirmed_flush_lsn` needed for continuity, or a retained-WAL gap proved against a supplied committed offset is `ProviderHistoryLossEvidence`; a timeout or permission failure while reading slot history is `ProviderHistoryUnavailable`.
4. SQL Server capture setup should call `sys.sp_cdc_enable_table` with the binding-derived `@capture_instance`, the binding-derived gating `@role_name`, `@supports_net_changes = 0`, an explicit `@captured_column_list` containing all emitted columns in emitted order, `@index_name = NULL`, `@filegroup_name = NULL`, and `@allow_partition_switch = 0`. Existing capture instances exact-match those choices through SQL Server's provider-normal observable metadata: source schema/object, capture-instance name, gating role, all captured columns in expected order, no net-changes support, no CDC filegroup override, source index either blank or the source primary key SQL Server selected for `@index_name = NULL`, and partition switching allowed only when it does not prove partition switching was enabled for a partitioned source. A manually named or wrong non-PK source index, a CDC filegroup override, or `partition_switch = true` on a partitioned source remains a fail-closed mismatch.
5. DMS-1320 should not create, repair, start, stop, or retune SQL Server CDC jobs when database CDC is already enabled. If DMS-1320 enables database CDC during `InitialCreateOrExactMatch`, it may rely on SQL Server's normal job creation and then inspect the result. For an already CDC-enabled database, missing required capture or cleanup jobs fail provider setup/validation with a provider-history diagnostic because continuity cannot be proved. Disabled, stopped, failed, or retention-outside-policy jobs are reported in provider metadata and diagnostics for 19-00/19-04 readiness/status handling; DMS-1320 does not change job state or retention policy.
6. Catalog and effective-permission inspection by the setup principal is the required validation path, and DMS-1320 must also support optional live probes when the caller supplies connector-principal credentials or a connector-principal connection factory. The optional probe should prove the connector can connect, read only the three captured source tables, cannot read or write `dms.DocumentProjectionWork`, cannot write `dms.Document` or `dms.DocumentCache`, and can update only the heartbeat sequence/time columns inside a rolled-back transaction. Probe credentials are never serialized to manifests, diagnostics, or logs. Absence of probe credentials is not a validation failure; a failed probe is a `ConnectorPrincipalPrivilegeFailure`.
