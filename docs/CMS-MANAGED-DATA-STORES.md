# CMS managed data stores

CMS owns the managed lifecycle record and its tenant-scoped state machine. The ordinary
`dmscs.DataStore` catalog remains the DMS routing source. A managed create provisions a physical
database and publishes an encrypted ordinary registration; a managed delete drops that database,
removes its registration and retains a managed tombstone. It does not create application/client
assignments, grant runtime permissions or refresh education organizations.

The selected API, query and catalog-compatibility decisions, with their primary sources, are
recorded in the [contract provenance](../reference/design/managed-data-stores-DMS-1439/contract-provenance.md).
Configurations that have not been qualified are listed under
[DMS visibility and qualification limits](#dms-visibility-and-qualification-limits).

## Configure before enabling

`AppSettings:EnableDataStoreManagement` defaults to **true**, including when it is omitted from
the normal shipped configuration stack. An unconfigured upgrade therefore fails startup.
Deployment examples that set it to false are explicit opt-outs, not the service default.
Use consistent settings on every replica; the management policy and readiness are captured at startup.

| Setting or prerequisite | Operator responsibility |
| --- | --- |
| `AppSettings:Datastore` | Select the CMS persistence provider independently of the physical target provider. |
| `DmsDataStoreProvisioning:Enabled` | Set true when management is enabled. The standalone provisioner defaults to false. |
| `DmsDataStoreProvisioning:Provider` | Select `postgresql` or `mssql` for the target. SQL ordinary registrations use the DMS routing token `sqlserver`. |
| Provisioner administrative connection, artifact catalog, trust and compatibility settings | Supply the administrative endpoint/permissions and genuine compatible Minimal/Sample packages. Follow the [provisioner configuration](CONFIGURATION.md#dmsdatastoreprovisioning) and [platform setup](OPERATIONS.md#configuration-service-dms-data-store-provisioning), including PostgreSQL tools or SQL Server shared staging. |
| `DataStoreManagement:RuntimeConnectionString` | Supply a separate operator-managed runtime credential for the same canonical target endpoint; startup rejects endpoints whose literal parts differ (the host, and the port or named instance when both settings state it), and parts that depend on a secret are checked when each operation runs. CMS never substitutes the administrative connection. |
| Runtime principal and access | Arrange authentication and permissions for newly provisioned databases before enabling submissions. Qualify the intended DMS workload using the published runtime connection. CMS creates no production principal or grant. |
| CMS connection encryption and DMS decryption | Preserve the existing ordinary catalog encryption configuration and DMS access to its corresponding key. Publication uses that boundary once. |

Use the existing secret resolver and `${secret:<name>}` syntax where supported; do not put real
credentials in examples, logs or tickets. Static startup validation does not fetch secrets. It
validates determinable literal fields and rejects unsupported topology; operation-time resolution
must still produce a valid endpoint. The runtime builder resolves one snapshot, validates its
endpoint and changes only its database selector to the managed target. `Created` means successful
physical provisioning and CMS publication, **not** that CMS has probed runtime permissions.

With management enabled, missing/invalid required static configuration fails before database
deployment and worker startup. With valid static configuration but a failed environmental readiness
probe, the host runs with a latched `NotReady` verdict. New POST and eligible DELETE requests return
503 without an aggregate/job change or physical operation; GET remains available. Readiness is not
an extra `/health` status. Remediate the cause and restart to probe again; restoring a file or rotating
a credential does not reopen a closed gate in the existing process. See the
[startup policy](CONFIGURATION.md#managed-data-store-lifecycle) and [readiness diagnostics](OPERATIONS.md#readiness).

## Upgrade and ordinary catalog compatibility

These catalog changes apply **even when management is false**. Ordinary name equality trims the
fixed 25-character boundary whitespace set and folds ASCII A–Z only. It does not fold accents,
non-ASCII case or Unicode composition. `Store`, `store` and edge-padded variants collide within
the same tenant; `café` and `cafe` are distinct, including on an accent-insensitive SQL Server
display column. Stored display names are preserved. NULL-tenant names obey the same uniqueness rule.

Ordinary `name` filters use that same canonical equality. Ordinary `type` filters are exact,
case-sensitive and untrimmed. Explicit sorting uses unsigned UTF-8 lexical order of the canonical
name or raw type, with directional ID ties. Ordinary default ID-ascending ordering, no implicit limit
and offset-only paging are unchanged. Callers relying on accent-insensitive filtering, padded type values or old text-sort
page positions must adjust. The DMS catalog consumer uses unfiltered ID-based reads; bootstrap name
selection uses the shared boundary trim while retaining its target-identity checks. Arbitrary external
callers have not been certified. Use IDs to identify existing registrations.

Follow the [migration rollout and read-only residual-index inventory](../reference/design/managed-data-stores-DMS-1439/catalog-name-uniqueness-rollout.md)
before upgrading:

1. Back up CMS and schedule maintenance. Drain all CMS replicas, old readers/writers, direct SQL
   writers and setup jobs. Deploy the coordinated read paths and migrations together; do not serve
   the new catalog with old binaries.
2. Check PostgreSQL UTF8 encoding and database TEMP privilege, or SQL Server 2019+ and the required
   UTF-8 collation/capability. Preserve the documented SQL Server computed-index SET options for
   direct writers. Compatibility-level tests of selected job paths do not certify every CMS feature.
3. Inventory **all** custom unique constraints/indexes, expressions, filters and collations on the
   ordinary and managed tables. An additional display-Name unique index can still reject `café`/`cafe`.
   Resolve that conflict explicitly before claiming provider parity; the standard migrations replace
   only the named ordinary constraint, not arbitrary custom indexes.
4. Apply the normal [CMS database upgrade procedure](OPERATIONS.md#configuration-service-database-upgrades).
   Migration 0039 adds the key; 0040 independently revalidates it, checks canonical collisions and
   replaces uniqueness transactionally; 0041 creates the lifecycle aggregate and reservations. Replay
   guards reject incompatible definitions, including conservatively refusing equivalent custom ones.
   On refusal, investigate the bounded row-ID diagnostics/schema difference and agree on remediation.
   Do not bypass guards, erase journal entries or silently discard colliding rows.
5. Verify the resulting definitions and residual indexes, then start consistently configured replicas.
   Confirm readiness and perform a controlled create-to-DMS-access check with operator credentials.
   Keep application/client assignment and intended runtime permission checks explicit.

The migration's transaction protects its own failure boundary. Deployment rollback is separate:
the old constraint can reject names newly allowed by the common key. Validate legacy eligibility
before restoring it or use an approved backup/restore plan. Setting management false or reverting a
binary does not undo catalog semantics. Restoring CMS alone does not reverse physical create/drop
effects; reconcile those effects using recorded ownership evidence before resuming work.

## Submit and observe

Use `/v3/dataStores/manage` and `/v3/dataStores/manage/{id}` with the normal CMS service-role and
scope authorization. Reads require the supported read/full scope; mutations require full management
scope. Anonymous and insufficient-scope requests are refused before the feature response.
The `Tenant` header selects a data partition, **not** an authorization boundary: an appropriately
scoped caller can select another valid tenant. Separate deployments, databases and credentials are
needed for tenant-level security.

POST accepts only `name` and `databaseTemplate`. After boundary trim, names contain 1–46 ASCII
letters, digits, spaces or underscores; templates are exactly `Minimal` or `Sample`. The physical
name is derived deterministically with the `EdFi_Ods_` prefix and template suffix; Sample selects
the populated artifact. Do not infer physical ownership from a name. Different display names can
derive the same target: CMS reserves both the tenant name and the canonical endpoint/target before
accepting, with target reservation spanning tenants.

| Request/outcome | Contract |
| --- | --- |
| Accepted POST | **202** with a `jobQueuedResult` body, `{"jobId": "…", "message": "The Data Store has been queued to be created."}`, and an absolute `Location` for the managed record, after aggregate/job linkage commits. `jobId` is the durable create job, readable through `/v3/jobs/{jobId}` in the same tenant. |
| Duplicate name / reserved target | **400** with `A data store with this name already exists.` / `The requested target database is already reserved.` No accepted work. |
| GET collection/by ID | Nine public fields: `id`, `name`, `dataStoreId`, `dataStoreName`, `status`, `databaseTemplate`, `databaseName`, `lastRefreshed`, `lastModifiedDate`. Internal identity, pins, credentials and job linkage are not returned. |
| Pending/unlinked create | All three linked fields (`dataStoreId`, `dataStoreName`, `databaseName`) are explicitly null until publication, including failed/error creates. |
| DELETE of Created | **202** with a `jobQueuedResult` body, `{"jobId": "…", "message": "The Data Store has been queued to be deleted."}`, no Location. This accepts asynchronous deletion; it does not mean the physical database has already disappeared. |
| DELETE of absent/Deleted | **404**; GET still returns a Deleted tombstone. |
| DELETE in another lifecycle state | State-specific **400** naming the state, with no state/job mutation. `CreateFailed`/`DeleteFailed` remain eligible for an automatic retry; `CreateError`/`DeleteError` will not be retried and need operator investigation. State/not-found checks take precedence over readiness. |
| Closed readiness | **503**, `Managed data store provisioning is unavailable.` for POST or otherwise eligible DELETE, including a DELETE whose record targets a different provider than the one the provisioner is ready for; no Retry-After or readiness detail. |
| Management disabled | Authorized managed requests, including reads, return **400**, `Data store management is disabled by AppSettings:EnableDataStoreManagement.` |
| Ordinary PUT/DELETE of a currently linked managed registration | Atomic **409** with an absolute management URL, including while management is disabled. Use the managed lifecycle; do not bypass the guard. |

Managed collections default to `offset=0`, `limit=25`, `orderBy=id`, `direction=Descending`.
ID and canonical-name filters apply before paging. All nine public fields are valid `orderBy`
values; direction is exactly `Ascending` or `Descending`. Nulls sort last in either direction, with
ID ties in the same direction. Paging values are validated int32 values (offset nonnegative, limit
positive). Both timestamps are UTC lifecycle observations; `lastRefreshed` does not report an
education-organization refresh. Deleted records retain historical linked fields.

## Retry, recovery and operator investigation

| Lifecycle | Expected progression |
| --- | --- |
| Create | `PendingCreate` → `CreateInProgress` → `Created`. Retryable failures become `CreateFailed`; terminal/exhausted work becomes `CreateError`. |
| Delete | `Created` → `PendingDelete` → `DeleteInProgress` → `Deleted`. Retryable failures become `DeleteFailed`; terminal/exhausted work becomes `DeleteError`. |

The durable job and matching lifecycle retry/terminal outcome commit together, including failures
before handler execution. When `Created` or `Deleted` committed but the worker stopped before
acknowledging the job, recovery completes that job without repeating physical work, including on its
last attempt (whether the exhaustion sweep reaches it or that attempt itself fails, for example before
its handler runs), so the job and the record report the same success; a later accepted DELETE does not
change this. If attempts are exhausted before the CMS success transaction commits,
physical success alone does not establish completion: the job ends in `Error` and the managed record
in `CreateError`/`DeleteError`. Earlier attempts remain eligible for reconciliation. A transient
physical outage after acceptance uses the existing job backoff
and maximum attempts; it does not close the startup readiness latch. That includes an accepted
create whose allowlisted package or attestation is temporarily missing (`ArtifactMissing`), before or
after the physical create: the record is `CreateFailed` while retrying, every retry repeats the trust,
hash, manifest and pin checks, and restoring the same verified package lets the original job finish.
A package that stays missing exhausts into `CreateError`; an untrusted, tampered or changed package
stays terminal. While a host's startup readiness
is `NotReady`, its worker neither claims nor exhausts `ManagedDataStore.Create` and
`ManagedDataStore.Delete` jobs; other jobs keep running. Previously accepted, nonterminal managed work
stays queued with its attempts, identity and pin unchanged, and resumes after remediation and a
restart that reports Ready. This does not repair records that already ended in `CreateError` or
`DeleteError`; those need operator investigation. There is no managed reset/retry endpoint;
failed/error records retain their name and target reservation until successful managed deletion, and
DELETE admits only Created.

Physical work and CMS persistence cannot share a transaction. After physical create succeeds but
publication fails, a later attempt reuses the recorded identity and artifact pin, reconciles the target
and publishes once. After physical drop succeeds but CMS finalization fails, a later attempt reconciles
absence before committing catalog removal and the tombstone. An ordinary row may remain temporarily
after the target is gone. A worker whose lease is stale cannot finalize CMS writes.

Guarded physical admission occurs under the physical target lock, after checking the actual holder
endpoint, and before target inspection/mutation or accepting absence. It verifies the persisted
operation and current job fence. Target names and endpoint digests alone are not ownership proof;
the physical source identity and stored operation must agree. Recovery must not adopt or drop a
replacement database merely because its name matches.

The first create pins artifact identity/hash/provider/contract metadata before physical work. Keep
the pinned package and required trust/compatibility material available for recovery; replacing an
allowlist/catalog entry does not repin accepted work. Endpoint/provider changes cannot redirect an
existing operation. Credential-only rotation on the same canonical endpoint can be used on a later
resolved operation; each operation binds its resolved snapshot through cleanup. Rotating a secret
does not rewrite an already-published encrypted runtime snapshot, and neither does changing
`DataStoreManagement:RuntimeConnectionString`, cache expiry or a restart: the
[SecretsSettings rotation timing](CONFIGURATION.md#secretssettings) applies only to stored connection
strings that keep their secret references. No API updates the credential of an existing managed data
store, so revoking the credential it was published with leaves DMS unable to connect to it. Aliases
and server moves are not automatically equivalent endpoints. Live two-server rollover has **not**
been tested.

For an incident, collect managed ID, tenant partition, lifecycle state, UTC observation times and safe
correlation/job identifiers through authorized diagnostics. If a job ID is known, its secured
`/v3/jobs/{jobId}` resource reports the public job status; it is not the managed Location ID. Job
failures intentionally use fixed safe messages. The accepted POST/DELETE response's `jobId` is that identifier. Restricted CMS failure fields and the provisioner's
[failure-code guide](OPERATIONS.md#failure-codes) distinguish transient, NotReady, invalid request,
permanent and operator-inspection failures; do not infer the root cause from `HandlerFailed` alone.

Fix configuration/readiness issues before restarting; allow transient work to retry; investigate
ownership, trust, pin or endpoint conflicts before further action. Do not reset counters, rewrite
identities/pins, remove reservations, delete journal records or drop targets by name to force success.
Use the [provisioner recovery guidance](OPERATIONS.md#recovery) for verified staging/physical leftovers.
Never copy passwords, connection strings, credential-bearing artifact URLs or source identities into
public job data, logs or support reports. Redaction qualification covers the recorded paths, not every
operator-installed logging sink.

## Pause, re-enable and retain history

To pause, stop new submissions, allow admitted operations to settle, then consistently restart CMS
replicas with `AppSettings:EnableDataStoreManagement=false`. This is not immediate cancellation of
physical work already admitted. It suppresses acceptance, enqueue, schedule materialization, claim and exhaustion for exactly
`ManagedDataStore.Create` and `ManagedDataStore.Delete`. Other jobs, scheduling and retention continue;
do not disable the entire worker merely to pause managed work. Ordinary managed-link guards remain active.

Re-enable consistently after restoring configuration and readiness. Eligible persisted jobs resume
with the same identities, pins and attempt history; counters are not reset, exhausted work may become
terminal and terminal work is not revived. See [job enablement and retention](CONFIGURATION-SERVICE-BACKGROUND-JOBS.md).
Finished-job retention defaults to seven days and is configurable. Job purging does not remove the
managed aggregate, pin or tombstone, and pending managed work is not finished-job retention material.
No managed tombstone purge API is provided.

Successful delete atomically unlinks the aggregate, removes the ordinary row and its existing
API-client assignments, contexts and derivative registrations, then records Deleted history. Clients
and applications survive; derivative physical databases are not dropped by this cascade. Under the
[explicit AC9 clarification](../reference/design/managed-data-stores-DMS-1439/contract-provenance.md#q4--authorized-ac9-clarification),
**DMS-1441 owns snapshot persistence and cleanup**. DMS-1439 adds no snapshot schema, cleanup or
education-organization refresh. Original Jira AC9 wording has not been edited; this clarification
governs its acceptance here, with no dependency on DMS-1441 delivery.

## DMS visibility and qualification limits

Publication changes the ordinary catalog, not an in-process DMS routing cache. With
`CacheSettings:DataStoreCacheRefreshEnabled=true` and positive `DataStoreCacheExpirationSeconds`
(default 600), an expired cached tenant catalog is refreshed on the next triggering access and
successful CMS fetch. Expiry alone is not a background timer, push notification or visibility SLA.
If refresh is disabled or TTL is nonpositive, plan an explicit cache reload/restart through existing
operations. A create may remain undiscoverable until refresh; a delete may leave stale routing to a
database already gone. Coordinate client traffic with this window. Discovery also does not create
client assignments or grant resource access.

Reviewed qualification includes both target engines, genuine Minimal/Sample recovery, both mixed
CMS/target directions, real process termination, transient retries, external self-contained Minimal
lifecycle/readiness/suppression, and DMS discovery/decryption. Separate focused tests prove a
representative production DMS descriptor insert through the published non-admin runtime connection
on each provider, with operator test grants and independently observed committed data. This is not
resource HTTP, full CRUD, CDC or a production least-privilege recipe.

The [historical qualification record](../reference/design/managed-data-stores-DMS-1439/step-6.1-closure-review.md#remaining-limitations-and-their-closure-impact)
retains untested deployment permutations (including external Sample, Keycloak/TLS and abstract-socket
connectivity), overlapping exhaustion sweeps, unresolved fake-psql/SQL error 3980 and broader suite
failures/skips. Earlier retained database residue remains untouched and requires independently
established ownership and cleanup authority. These are not passing results. Custom-catalog inventory,
runtime permissions and coordinated rollout remain operator/release obligations.
