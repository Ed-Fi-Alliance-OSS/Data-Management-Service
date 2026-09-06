# CDC Operations Runbook

This runbook covers the shipped deployment-owned CDC operator surface for PostgreSQL and
SQL Server. Begin with the prerequisites below. The T01 foundation is available;
provider setup exercises and operational procedures are still pending in
[delivery and evidence](cdc-inv-evidence.md#pending-delivery). Do not infer a runnable
recovery procedure from a planned section.

Use the [CLI reference](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md)
for installation, command syntax, confirmations, and exit codes, and the
[configuration catalog](../../docs/CONFIGURATION.md#datamanagementdocumentcachecdc) for
settings and defaults. [Design owners](README.md#design-owners) remain authoritative.

<a id="prerequisites"></a>
## Deployment prerequisites

Before initial enablement, the provisioning owner must hold writer admission closed on
a new physical database created for that CDC provisioning. Admission must never have
opened. Existing/admitted databases are ineligible for retrofit; neither an empty table
nor current-schema validation proves provisioning authority. A retry of interrupted
initial enablement retains the same target and generation and requires the original
evidence. The deployment owns external write fencing; DMS status polling supplies no
runtime writer gate. See [initial admission](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence).

Supply these deployment facts before issuing a CDC mutation:

- A generated/provisioned schema matching the DMS ApiSchema workspace, CMS credentials
  and target metadata, and matching datastore provider across CMS and DMS. Select an
  explicit projection target on the running projector and in the control plane's
  configuration. A CLI target argument alone does not configure the running DMS.
- Provider setup authority through the selected source connection, a named setup principal,
  and a distinct least-privilege connector database account. PostgreSQL requires logical
  replication and suitable publication/slot/grant authority; SQL Server requires CDC
  setup authority and running capture infrastructure. Setup remains owned by the shipped
  provider workflow; do not independently create slots, publications, capture instances,
  or connector JSON. See [PostgreSQL](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#postgresql)
  and [SQL Server](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#sql-server).
- SQL Server projection prerequisites (`READ_COMMITTED_SNAPSHOT` and server `nested triggers`)
  plus connector snapshot-isolation support. Follow the
  [E18 correction scope](../document-cache-documentation/operations-runbook.md#sql-server-prerequisite-failure-correction);
  changing settings after successful validation on an active target is outside supported
  v1 recovery. A restart is not a general renewed-readiness guarantee.
- A qualified Ed-Fi Connect image selected by digest, including the Ed-Fi transforms.
  Local opt-in takes `DMS_CDC_CONNECT_IMAGE`; obtain its actual qualified digest, never
  substitute a floating tag or unmodified Debezium image. Qualification is owned by the
  [pinned runtime](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#pinned-connector-runtime).
- Network access from the control plane to CMS, the source database, Kafka, Connect REST,
  the metrics bridge, and DMS; and from the worker to the database and broker. The local
  one-shot `cdc-setup` container joins the `dms` network. Container service names and
  advertised broker addresses need not resolve from a host-side CLI. Match all addresses
  to the actual invocation context.
- A running DMS mapping `GET /health/document-cache` with
  `DataManagement:DocumentCache:Status:RequiredRole` and an operator token whose role claim
  satisfies it. Supply the token through protected configuration. Projection-evidence
  workflows need it; adoption/retirement do not depend on that endpoint.
- Named worker secret references, separate Java connector and librdkafka admin security
  settings, declared consumers/groups, record budget, and durable deployment state.
  Resolve required values using the catalog, not another operator's binding record.

The local `-EnableKafkaCdc` bootstrap wrapper orchestrates initial enablement; Kafka UI
alone does not enable it. The published deployment start script does not accept that
local switch. Production-like deployments supply their own admission authority,
configuration, credentials, connectivity, qualified image, and persistent state. See
[local/bootstrap ownership](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci).

<a id="deployment-state"></a>
## Durable deployment-state handoff

Record the resolved host state root, container mount, target, and generation in the
deployment record. Setup, status, stop, and retirement must all use the same store.
Follow the [catalog's exact path precedence](../../docs/CONFIGURATION.md#cdc-timeouts-and-durable-state)
and the shipped [resolver](../../eng/docker-compose/env-utility.psm1) and
[Compose mount](../../eng/docker-compose/cdc-setup.yml). The container path `/state` is
not the host mount source. Direct CLI invocations need their own matching root setting.

The shipped filesystem backend supports a single controller, not distributed coordination
or a delivered remote adapter. Restrict the root to the operator service account; the
store rejects group/other-writable Unix directories and non-owner-only files. Keep state
on durable storage outside ephemeral container/bootstrap workspaces. Protect backups of
all binding, incident, and retirement records, and preserve retirement history through
destructive local teardown. Store backup access and retention with the deployment's
operational records.

A backup or redacted connector manifest is not authority to edit binding identity or
recreate offsets. Missing state requires complete-record `cdc adopt` with live validation;
that procedure is pending T05. Binding, incident, and retirement behavior remains owned
by the [binding design](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding).

<a id="procedure-format"></a>
## Procedure conventions

Every delivered executable procedure uses an explicit stable anchor and the following
sequence, shared across providers unless a provider-specific step differs:

1. **Scope and effect:** diagnostic or mutating operation, affected components, and
   destructive scope before any commands. CDC `status` is an observation with containment
   effects: proven history loss latches an incident and fences the connector; later polls
   retry an unapplied fence. Loss evidence alone does not prove the stop succeeded.
2. **Starting directory and prerequisites:** name the shell, repository or installed-tool
   working directory, configuration file, network context, credentials by reference,
   provider authority, and any external admission/fencing requirement.
3. **Target/generation selection:** inspect the intended logical target, physical-source
   evidence, generation, and resolved state root. Use synthetic identifiers such as
   tenant `district-lab`, data store `42`, deployment `cdc-lab`, instance `school-year-lab`,
   and generation `1`. For the default tenant, translate record `default` to CLI empty.
4. **Commands:** use packaged `dms-document-cache cdc` verbs or shipped local wrappers.
   Keep shared setup in one place; do not hand-author connector JSON or recovery SQL.
   Refer to secrets by name, such as `CDC_SOURCE_PASSWORD` and `CDC_OPERATOR_TOKEN`.
5. **Expected result:** name the JSON contract/outcome and exit code, linking a captured
   fixture artifact. Help commands have text output and no JSON contract. Do not fabricate
   operational JSON: capture raw output first, sanitize it, and preserve relevant fields.
6. **Verification:** inspect outcome and component evidence separately. Successfully
   produced CDC `notReady` and `unknown` answers exit zero; stop/restart success is not
   end-to-end readiness. Distinguish DMS projection `status` from deployment CDC `status`.
7. **Interruption/retry:** state what may already have changed, preserved evidence,
   same-target/generation retry rules, and conditions requiring containment or escalation.
   A timeout does not prove rollback. Never prescribe offset resets, same-topic
   resnapshots, or unimplemented baseline recovery.
8. **Evidence:** link the design owner and evidence-index row with exact test identities,
   provider/layer, result, artifacts, and any pending downstream verification owner.

CDC polling and initial-readiness scope are owned by
[continuity](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#source-history-continuity)
and [readiness](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-readiness-scope).
Routine observation uses existing status and indexed queue facts. Explicit O(N) scrub
is separately admitted expensive work, not a health probe. Reuse
[E18 projection procedures](../document-cache-documentation/operations-runbook.md) for
queue/poison, enqueue failure, lifecycle, rebuild, and scrub; CDC handoff reconciliation
is pending T04. Current/historical CDC state disqualifies simple internal-only toggles;
stopping a connector does not clear downstream publication history.
