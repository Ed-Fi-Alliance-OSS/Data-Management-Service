# Product Requirements Document: Ed-Fi API Configuration Service (CS) v8.1

> **Status**: Published \
> **Owner**: Vinaya Mayya \
> **Product**: Ed-Fi API Configuration Service (CS) \
> **Repository**: `Ed-Fi-Alliance-OSS/Data-Management-Service` (`src/config`) \
> **Companion document**: [CS v8.0 Product Requirements](./PRD-CS-v8.0.md)

## 1. Product Overview

CS v8.1 implements the core Ed-Fi Management API v3 administrative control
plane, in parity with ODS Admin API v2.4.

> [!NOTE]
> Any requirement listed as "SHOULD" instead of "MUST" or "SHALL" may
> end up being deferred to the next release. This PRD should be edited
> once the 8.1 release reaches code complete, with deferred "SHOULD"
> items becoming "MUST" items in the 8.2 release PRD.

### 1.1 Jobs to Be Done

#### JTBD 11: Database Instance Provisioning

**Personas**: Platform Host System Administrator

**When** onboarding a new district or standing up a new environment, \
**I want** to request creation of a new data store from an approved database
template through the API, monitor its provisioning status, and later request
its removal, \
**so that** I don't have to run manual database-provisioning scripts or track
progress out of band.

**How CS Could Help**: The ODS Admin API 2.4 line introduced an analogous
`/v2/odsInstances/manage` / `/v3/dataStores/manage` resource with
asynchronous, job-tracked provisioning. No equivalent exists in CS v8.0:
current CS data-store creation is synchronous CRUD over an already-provisioned
connection string, and schema provisioning is a separate host-run tooling step
(`api-schema-tools`, `provision-dms-schema.ps1`), not an API-driven workflow.

#### JTBD 12: Education Organization Synchronization

**Personas**: Platform Host System Administrator

**When** a data store's education organization structure changes, \
**I want** CS to keep an up-to-date, queryable copy of each data store's
education organizations, \
**so that** I can view and audit education organization hierarchies without
querying operational databases directly.

**How CS Could Help**: The ODS Admin API 2.4 line introduced this as a
periodically synchronized, on-demand-refreshable cache. No equivalent exists
in CS v8.0.

## 2. Functional Requirements

### 2.1 API Versioning and Discovery

- **FR-VERSION-4**: The CS discovery endpoint SHOULD advertise the tenant
  discovery URL (`urls.tenancy`) so clients can determine available tenants.
  In v8.0, the discovery model contains version, application name,
  informational version, build, OpenAPI metadata URL, and specification
  version only.
- **FR-VERSION-5**: CS SHOULD expose an anonymous, unauthenticated endpoint
  that lists the names of configured tenants (an empty list when
  multi-tenancy is disabled), so a client can discover valid tenant names
  before it is able to supply tenant-scoped requests.
- **FR-VERSION-6**: Discovery and API-metadata endpoints SHOULD remain
  reachable without a tenant header in multi-tenant deployments, since they
  expose no tenant-scoped data, so deployment discovery does not depend on a
  client already knowing a tenant name.

### 2.2 Identity Provider and Authentication

- **FR-AUTH-14**: In self-contained mode, CS SHOULD periodically remove
  expired access-token records from its own storage on a configurable interval,
  defaulting to enabled with a 30-minute sweep interval. In Keycloak mode, CS
  SHOULD NOT implement its own token-cleanup mechanism, since Keycloak owns its
  own token and session lifecycle.
- **FR-AUTH-15**: CS SHOULD require client authentication for token
  revocation, SHOULD allow a client to revoke only its own tokens, and SHOULD
  avoid responses that reveal whether a submitted token was valid, so
  revocation cannot be used to enumerate or deny service to other clients.
- **FR-AUTH-16**: When CS provisions API credentials through an external
  identity provider, it SHOULD report success only once the credential is
  fully usable for authorized API access, and SHOULD return a recoverable
  failure when downstream provisioning cannot be completed, so operators are
  never handed unusable credentials disguised as a successful result.

### 2.3 Application Management

- **FR-APP-7**: Creating or updating an API client SHOULD allow zero
  data-store assignments, extending the zero-assignment support that v8.0
  already allows at application creation, so operators can provision and
  administer identity-only or other non-resource-access clients through the
  normal lifecycle without being forced to grant data access they don't need.

### 2.4 Data Store Management

- **FR-DATASTORE-11**: Updating a data store or data-store derivative without
  supplying a new connection value SHOULD preserve the previously stored
  value rather than requiring the caller to resupply it.
- **FR-DATASTORE-12**: CS SHOULD validate that a submitted connection value is
  a genuine, correctly formatted connection string for the target database
  engine before storing it, and SHOULD reject a previously issued (already
  protected) connection value if a caller resubmits it as new input, so that
  retrieving and then round-tripping a stored value can never silently
  corrupt the underlying connection information.

### 2.5 Profiles

- **FR-PROFILE-5**: CS SHOULD expose an endpoint for DMS to retrieve all
  Profiles assigned to a given application (`GET /v3/applications/{id}/profiles`),
  so DMS can resolve and cache which Profiles apply to a given API client. In
  v8.0, Profile CRUD exists and Applications expose `profileIds`, but no
  application-profile endpoint is mapped.

### 2.6 Error Handling and Responses

- **FR-ERROR-6**: Every non-success CS response — including framework-level
  errors, authentication/authorization failures, tenant-resolution middleware
  failures, dynamic claims-management endpoints, and `/connect/token`
  errors — SHOULD return a JSON body conforming to the Ed-Fi Error
  Response Knowledge Base contract: `detail`, `type`, `title`, `status`,
  `correlationId`, `validationErrors`, and `errors`. `/connect/revoke`
  returns the RFC 6749/7009 `{error, error_description}` shape as a deliberate
  exception.
- **FR-ERROR-7**: CS SHOULD NOT return bare framework results (for example,
  `NotFound()` or `Forbid()`) or ad hoc `{ error, message }` shapes from any
  endpoint or middleware.
- **FR-ERROR-8**: All unstructured or untrusted provider, transport, JSON, and
  operational error text SHOULD be replaced with safe, fixed fallback
  messages in client-facing responses.
- **FR-ERROR-9**: CS SHALL distinguish unresolved references from duplicate
  natural keys. A caller-supplied reference to an entity that does not exist
  (for example a vendor, data store, profile, application, ownership token,
  or claim set copy source) SHALL return `409 Conflict` with type
  `urn:ed-fi:api:conflict:unresolved-reference`. A create, update, copy, or
  import that duplicates an existing natural key (vendor company name per
  tenant, profile name, claim set name, data store name per tenant, API client
  name per application, data store derivative type per data store,
  application name per vendor, tenant name, data store context key) SHALL
  return `400 Bad Request` with type `urn:ed-fi:api:bad-request:data`, with
  the message under the duplicated field in `validationErrors`.
  `POST /v3/vendors` SHALL only create; an existing company name is rejected
  rather than updated. Supersedes v8.0 FR-ERROR-5 for claim sets and vendors.

### 2.7 Database Instance Provisioning

- **FR-DBINST-1**: CS SHOULD allow an administrator to request creation of a
  new data store from a named, approved database template, validating the
  instance name and template before accepting the request.
- **FR-DBINST-2**: CS SHOULD provision and de-provision such data stores
  asynchronously via a background job after a create or delete request is
  accepted, including tenant-aware scheduling in multi-tenant deployments.
- **FR-DBINST-3**: CS SHOULD expose granular lifecycle status values for a
  data store's creation and deletion (`PendingCreate`, `CreateInProgress`,
  `Created`, `CreateFailed`, `CreateError`, `PendingDelete`,
  `DeleteInProgress`, `Deleted`, `DeleteFailed`, `DeleteError`), so
  administrators can track provisioning progress.
- **FR-DBINST-4**: Deleting a provisioned data store SHOULD mark it
  `PendingDelete` (soft delete) rather than removing it immediately, and SHOULD
  reject the request with a descriptive error if the instance is in a status
  that blocks deletion.

### 2.8 Education Organization Synchronization

- **FR-EDORG-1**: CS SHOULD periodically refresh a cached copy of each data
  store's education organization structure, on an administrator-configurable
  interval.
- **FR-EDORG-2**: CS SHOULD allow an administrator to retrieve education
  organizations grouped by their owning data store, and to trigger an
  on-demand refresh for all data stores or a specific one.

### 2.9 Asynchronous Job Tracking

- **FR-JOB-1**: CS SHOULD allow an administrator to query the status of an
  asynchronous background job (such as data-store provisioning or
  education-organization refresh) by job ID, including when it was created and,
  if applicable, when it finished.
- **FR-JOB-2**: CS background jobs SHOULD be durably recoverable across
  process restarts and across replicas, with safe retry behavior, so accepted
  administrative work is not lost when execution is interrupted.
- **FR-JOB-3**: CS SHOULD support recurring schedules for platform-managed
  background operations, so periodic work continues predictably across
  restarts and multi-replica deployments without duplicate or missed
  occurrences.

### 2.10 Rate Limiting

- **FR-RATE-1**: CS SHOULD enforce a configurable rate limit on client
  requests and return `429 Too Many Requests` when a client exceeds it within a
  configured time window.

### 2.11 Multi-Tenancy and Tenant Partitioning

- **FR-TENANT-7**: In multi-tenant deployments, vendor and profile
  configuration SHOULD be partitioned by tenant, including tenant-scoped name
  uniqueness and rejection of cross-tenant associations, so one tenant's
  configuration can neither block nor leak into another's. Claim set names
  remain unique across all tenants, and system-reserved claim sets are shared.

### 2.12 Extensibility and Secret Management

- **FR-PLUGIN-1**: CS SHOULD support explicitly enabled, host-provided
  extensions at approved extension points, so deployments can add supported
  behavior without modifying CS itself.
- **FR-PLUGIN-2**: CS SHOULD allow a data-store connection definition to
  reference an externally managed secret that CS resolves for authorized
  reads, so operators can keep backend credentials outside CS-managed
  configuration data.

### 2.13 Ownership-Based Authorization Administration

- **FR-OWNERSHIP-1**: CS SHOULD allow administrators to create and maintain
  ownership-token definitions and assign them to API clients, so downstream
  services can enforce ownership-based access rules from centrally managed
  configuration.
- **FR-OWNERSHIP-2**: CS SHOULD expose each API client's effective ownership
  configuration in API-client read models, so downstream services can
  retrieve the ownership rules they must enforce.

### 2.14 API Contract Consistency

- **FR-CONTRACT-1**: CS update operations SHOULD reject a request whose
  route identifier and request-body identifier do not refer to the same
  resource, so a malformed request cannot partially update one record while
  intending to affect another.

### 2.15 Vendor Configuration

- **FR-VENDOR-6**: CS SHOULD allow a vendor to be created or updated without
  a namespace prefix, so deployments that do not rely on namespace-based
  authorization are not forced to supply an irrelevant value.

## 3. Non-Functional Requirements

### 3.1 Security and Privacy

- **NFR-SEC-8**: CS SHOULD refuse to start unless the deployment supplies a
  non-default encryption key of sufficient strength for protecting stored
  connection information, so stock or weak configuration cannot expose
  administrative secrets.

### 3.2 Reliability and Operations

- **NFR-REL-4**: In self-contained identity-provider mode, CS SHOULD run an
  in-process, config-gated background sweep that deletes expired access
  token rows on a configurable interval.
- **NFR-REL-5**: CS token-expiration storage and cleanup SHOULD use
  time-zone-independent semantics across supported database engines, so
  token lifecycle behavior does not vary with the database's or session's
  configured time zone.
- **NFR-REL-6**: CS SHOULD load only explicitly enabled host-provided
  extensions and SHOULD fail startup when an extension is structurally
  invalid or conflicts with core runtime behavior, so broken or unreviewed
  extensions cannot silently alter production behavior.

### 3.3 Performance and Scalability

- **NFR-PERF-3**: Running the expired-token cleanup sweep concurrently
  across multiple CS replicas SHOULD be safe: the underlying delete should be
  idempotent, so replicas racing to delete the same expired rows cause no harm.

## 4. Out of Scope and Known Limitations

- CS v8.1 confirmed that database-instance provisioning and
  education-organization synchronization are CS responsibilities (see
  FR-DBINST-1..4 and FR-EDORG-1..2) rather than remaining host-run
  operational tooling.
- v8.0 FR-CLIENT-2 (one or more data-store IDs required on API client
  creation) is superseded by FR-APP-7, which allows zero data-store
  assignments. Direct application-level enabled/disabled writes were evaluated
  and not adopted; enabled/approved state remains at the API-client level.
- A published Identity API surface and host-pluggable identity implementation
  are being delivered on the Ed-Fi API service, not the Configuration Service,
  and are out of scope for this document.
- Secret expiration and scheduled rotation; today, secret rotation is a manual
  reset with no expiration date tracked or enforced.
- Client-supplied correlation IDs, supported on the DMS resource API, are not
  supported on CS v8.1.
- Several CS v8.0 requirements originally drafted for v8.1 have no
  confirmed v8.1 delivery and have been moved to the draft
  [CS v8.2 PRD](./PRD-CS-v8.2.md): claim-set-existence
  validation on application writes, independent control of Swagger/OpenAPI
  generation, and a dependency-aware health endpoint.

## 5. Open Questions and Decision Log

- **Secret expiration/rotation**: Decide whether to implement
  `secret_expires_on` tracking and rotation tooling, or leave rotation as a
  fully manual, host-driven operation.
