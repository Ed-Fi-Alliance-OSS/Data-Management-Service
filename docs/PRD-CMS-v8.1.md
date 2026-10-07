# Product Requirements Document: Ed-Fi Configuration Management Service (CMS) v8.1

> **Status**: Planned \
> **Owner**: Stephen Fuqua \
> **Product**: Ed-Fi Configuration Management Service (CMS) \
> **Repository**: `Ed-Fi-Alliance-OSS/Data-Management-Service` (`src/config`) \
> **Companion document**: [CMS v8.0 Product Requirements](./PRD-CMS-v8.0.md)

## 1. Product Overview

CMS v8.1 implements the core Ed-Fi Management API v3 administrative control
plane, in parity with ODS Admin API v2.4.

### 1.1 Jobs to Be Done

#### JTBD 11: Database Instance Provisioning

**Personas**: Platform Host System Administrator, Operator

**When** onboarding a new district or standing up a new environment, \
**I want** to request creation of a new data store from an approved database
template through the API, monitor its provisioning status, and later request
its removal, \
**so that** I don't have to run manual database-provisioning scripts or track
progress out of band.

**How CMS Could Help**: The ODS Admin API 2.4 line introduced an analogous
`/v2/odsInstances/manage` / `/v3/dataStores/manage` resource with
asynchronous, job-tracked provisioning. No equivalent exists in CMS v8.0:
current CMS data-store creation is synchronous CRUD over an already-provisioned
connection string, and schema provisioning is a separate host-run tooling step
(`api-schema-tools`, `provision-dms-schema.ps1`), not an API-driven workflow.

#### JTBD 12: Education Organization Synchronization

**Personas**: Platform Host System Administrator, Operator

**When** a data store's education organization structure changes, \
**I want** CMS to keep an up-to-date, queryable copy of each data store's
education organizations, \
**so that** I can view and audit education organization hierarchies without
querying operational databases directly.

**How CMS Could Help**: The ODS Admin API 2.4 line introduced this as a
periodically synchronized, on-demand-refreshable cache. No equivalent exists
in CMS v8.0.

## 2. Functional Requirements

### 2.1 API Versioning and Discovery

- **FR-VERSION-4**: CMS discovery/metadata endpoints SHOULD report whether the
  deployment is running in multi-tenant mode. In v8.0, the discovery model
  contains version, application name, build, OpenAPI metadata URL, and
  specification version only.
- **FR-VERSION-5**: CMS SHOULD expose an anonymous, unauthenticated endpoint
  that lists the names of configured tenants (an empty list when
  multi-tenancy is disabled), so a client can discover valid tenant names
  before it is able to supply tenant-scoped requests.
- **FR-VERSION-6**: Discovery and API-metadata endpoints SHOULD remain
  reachable without a tenant header in multi-tenant deployments, since they
  expose no tenant-scoped data, so deployment discovery does not depend on a
  client already knowing a tenant name.

### 2.2 Identity Provider and Authentication

- **FR-AUTH-11**: In self-contained mode, CMS SHOULD periodically remove
  expired access-token records from its own storage on a configurable interval,
  defaulting to enabled with a 30-minute sweep interval. In Keycloak mode, CMS
  SHOULD NOT implement its own token-cleanup mechanism, since Keycloak owns its
  own token and session lifecycle.
- **FR-AUTH-12**: CMS SHOULD require client authentication for token
  revocation, SHOULD allow a client to revoke only its own tokens, and SHOULD
  avoid responses that reveal whether a submitted token was valid, so
  revocation cannot be used to enumerate or deny service to other clients.
- **FR-AUTH-13**: When CMS provisions API credentials through an external
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

- **FR-DATASTORE-10**: Updating a data store or data-store derivative without
  supplying a new connection value SHOULD preserve the previously stored
  value rather than requiring the caller to resupply it.
- **FR-DATASTORE-11**: CMS SHOULD validate that a submitted connection value is
  a genuine, correctly formatted connection string for the target database
  engine before storing it, and SHOULD reject a previously issued (already
  protected) connection value if a caller resubmits it as new input, so that
  retrieving and then round-tripping a stored value can never silently
  corrupt the underlying connection information.

### 2.5 Profiles

- **FR-PROFILE-5**: CMS SHOULD expose an endpoint for DMS to retrieve all
  Profiles assigned to a given application (`GET /v3/applications/{id}/profiles`),
  so DMS can resolve and cache which Profiles apply to a given API client. In
  v8.0, Profile CRUD exists and Applications expose `profileIds`, but no
  application-profile endpoint is mapped.

### 2.6 Error Handling and Responses

- **FR-ERROR-6**: Every non-success CMS response — including framework-level
  errors, authentication/authorization failures, tenant-resolution middleware
  failures, dynamic claims-management endpoints, and OAuth/OIDC error
  responses — SHOULD return a JSON body conforming to the Ed-Fi Error
  Response Knowledge Base contract: `detail`, `type`, `title`, `status`,
  `correlationId`, `validationErrors`, and `errors`.
- **FR-ERROR-7**: CMS SHOULD NOT return bare framework results (for example,
  `NotFound()` or `Forbid()`) or ad hoc `{ error, message }` shapes from any
  endpoint or middleware.
- **FR-ERROR-8**: All unstructured or untrusted provider, transport, JSON, and
  operational error text SHOULD be replaced with safe, fixed fallback
  messages in client-facing responses.

### 2.7 Database Instance Provisioning

- **FR-DBINST-1**: CMS SHOULD allow an administrator to request creation of a
  new data store from a named, approved database template, validating the
  instance name and template before accepting the request.
- **FR-DBINST-2**: CMS SHOULD provision and de-provision such data stores
  asynchronously via a background job after a create or delete request is
  accepted, including tenant-aware scheduling in multi-tenant deployments.
- **FR-DBINST-3**: CMS SHOULD expose granular lifecycle status values for a
  data store's creation and deletion (for example, `PendingCreate`,
  `CreateInProgress`, `CreateFailed`, `PendingDelete`, `DeleteInProgress`,
  `Deleted`, `DeleteFailed`), so administrators can track provisioning
  progress.
- **FR-DBINST-4**: Deleting a provisioned data store SHOULD mark it
  `PendingDelete` (soft delete) rather than removing it immediately, and SHOULD
  reject the request with a descriptive error if the instance is in a status
  that blocks deletion.

### 2.8 Education Organization Synchronization

- **FR-EDORG-1**: CMS SHOULD periodically refresh a cached copy of each data
  store's education organization structure, on an administrator-configurable
  interval.
- **FR-EDORG-2**: CMS SHOULD allow an administrator to retrieve education
  organizations grouped by their owning data store, and to trigger an
  on-demand refresh for all data stores or a specific one.

### 2.9 Asynchronous Job Tracking

- **FR-JOB-1**: CMS SHOULD allow an administrator to query the status of an
  asynchronous background job (such as data-store provisioning or
  education-organization refresh) by job ID, including when it was created and,
  if applicable, when it finished.
- **FR-JOB-2**: CMS background jobs SHOULD be durably recoverable across
  process restarts and across replicas, with safe retry behavior, so accepted
  administrative work is not lost when execution is interrupted.
- **FR-JOB-3**: CMS SHOULD support recurring schedules for platform-managed
  background operations, so periodic work continues predictably across
  restarts and multi-replica deployments without duplicate or missed
  occurrences.

### 2.10 Rate Limiting

- **FR-RATE-1**: CMS SHOULD enforce a configurable rate limit on client
  requests and return `429 Too Many Requests` when a client exceeds it within a
  configured time window.

### 2.11 Multi-Tenancy and Tenant Partitioning

- **FR-TENANT-7**: In multi-tenant deployments, vendor, claim set, and
  profile configuration SHOULD be partitioned by tenant, including
  tenant-scoped uniqueness and rejection of cross-tenant associations, so one
  tenant's configuration can neither block nor leak into another's.

### 2.12 Extensibility and Secret Management

- **FR-PLUGIN-1**: CMS SHOULD support explicitly enabled, host-provided
  extensions at approved extension points, so deployments can add supported
  behavior without modifying CMS itself.
- **FR-PLUGIN-2**: CMS SHOULD allow a data-store connection definition to
  reference an externally managed secret that CMS resolves for authorized
  reads, so operators can keep backend credentials outside CMS-managed
  configuration data.

### 2.13 Ownership-Based Authorization Administration

- **FR-OWNERSHIP-1**: CMS SHOULD allow administrators to create and maintain
  ownership-token definitions and assign them to API clients, so downstream
  services can enforce ownership-based access rules from centrally managed
  configuration.
- **FR-OWNERSHIP-2**: CMS SHOULD expose each API client's effective ownership
  configuration in API-client read models, so downstream services can
  retrieve the ownership rules they must enforce.

### 2.14 API Contract Consistency

- **FR-CONTRACT-1**: CMS update operations SHOULD reject a request whose
  route identifier and request-body identifier do not refer to the same
  resource, so a malformed request cannot partially update one record while
  intending to affect another.

### 2.15 Vendor Configuration

- **FR-VENDOR-5**: CMS SHOULD allow a vendor to be created or updated without
  a namespace prefix, so deployments that do not rely on namespace-based
  authorization are not forced to supply an irrelevant value.

## 3. Non-Functional Requirements

### 3.1 Security and Privacy

- **NFR-SEC-8**: CMS SHOULD refuse to start unless the deployment supplies a
  non-default encryption key of sufficient strength for protecting stored
  connection information, so stock or weak configuration cannot expose
  administrative secrets.

### 3.2 Reliability and Operations

- **NFR-REL-4**: In self-contained identity-provider mode, CMS SHOULD run an
  in-process, config-gated background sweep that deletes expired access
  token rows on a configurable interval.
- **NFR-REL-5**: CMS token-expiration storage and cleanup SHOULD use
  time-zone-independent semantics across supported database engines, so
  token lifecycle behavior does not vary with the database's or session's
  configured time zone.
- **NFR-REL-6**: CMS SHOULD load only explicitly enabled host-provided
  extensions and SHOULD fail startup when an extension is structurally
  invalid or conflicts with core runtime behavior, so broken or unreviewed
  extensions cannot silently alter production behavior.

### 3.3 Performance and Scalability

- **NFR-PERF-3**: Running the expired-token cleanup sweep concurrently
  across multiple CMS replicas SHOULD be safe: the underlying delete should be
  idempotent, so replicas racing to delete the same expired rows cause no harm.

## 4. Out of Scope and Known Limitations

- CMS v8.1 confirmed that database-instance provisioning and
  education-organization synchronization are CMS responsibilities (see
  FR-DBINST-1..4 and FR-EDORG-1..2) rather than remaining host-run
  operational tooling.
- Requiring one or more data-store IDs on API client creation (v8.0
  FR-CLIENT-2) and supporting direct application-level enabled/disabled
  writes were both evaluated and explicitly not carried forward: CMS instead
  allows zero data-store assignments on API clients (FR-APP-7, extending the
  zero-assignment support v8.0 FR-APP-2 already allowed at the application
  level) and continues to manage enabled/approved state at the API-client
  level.
- A published Identity API surface and host-pluggable identity
  implementation are being delivered on the Data Management Service (DMS),
  not the Configuration Management Service, and are out of scope for this
  document.
- Secret expiration and scheduled rotation; today, secret rotation is a manual
  reset with no expiration date tracked or enforced.
- Client-supplied correlation IDs, supported on the DMS resource API, are not
  supported on CMS v8.1.
- Several CMS v8.0 requirements originally drafted for v8.1 have no
  confirmed v8.1 delivery and have been moved to the draft
  [CMS v8.2 PRD](./PRD-CMS-v8.2.md): claim-set-existence
  validation on application writes, the `400` vs. `409` decision for
  unresolved caller-supplied references, independent control of Swagger/OpenAPI
  generation, and a dependency-aware health endpoint.

## 5. Open Questions and Decision Log

- **Secret expiration/rotation**: Decide whether to implement
  `secret_expires_on` tracking and rotation tooling, or leave rotation as a
  fully manual, host-driven operation.
