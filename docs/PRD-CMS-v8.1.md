# PRD: CMS v8.1.0 Deferred Requirements

> **Status**: Planned / deferred from v8.0 \
> **Owner**: Stephen Fuqua \
> **Product**: Ed-Fi Configuration Management Service (CMS) \
> **Repository**: `Ed-Fi-Alliance-OSS/Data-Management-Service` (`src/config`) \
> **Companion document**: [CMS v8.0 Product Requirements](./PRD-CMS-v8.0.md)

This document captures requirements and feature slices that appeared in the
CMS v8.0 PRD but are not implemented in the CMS code shipped at git tag
`v8.0.0`. It mirrors the v8.0 PRD structure where useful so the deferred work
can be evaluated for a v8.1.0 release without overstating v8.0.0 behavior.

## 1. Product Overview

CMS v8.0.0 implements the core Ed-Fi Management API v3 administrative control
plane. The following jobs and requirements are deferred because the v8.0.0 code
has no matching route, model, service registration, repository behavior, or
configuration switch, or because only part of the originally stated behavior is
implemented.

### 1.1 Deferred Jobs to Be Done

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
asynchronous, job-tracked provisioning. No equivalent exists in CMS v8.0.0:
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
in CMS v8.0.0.

## 2. Functional Requirements

### 2.1 API Versioning and Discovery

- **FR-VERSION-4**: CMS discovery/metadata endpoints SHOULD report whether the
  deployment is running in multi-tenant mode. In v8.0.0, the discovery model
  contains version, application name, build, OpenAPI metadata URL, and
  specification version only.

### 2.2 Identity Provider and Authentication

- **FR-AUTH-10**: In self-contained mode, CMS SHOULD periodically remove
  expired access-token records from its own storage on a configurable interval,
  defaulting to enabled with a 30-minute sweep interval. In Keycloak mode, CMS
  SHOULD NOT implement its own token-cleanup mechanism, since Keycloak owns its
  own token and session lifecycle.

### 2.3 Application Management

- **FR-APP-2-DEFERRED**: Creating an application SHOULD require one or more
  data store IDs if application-created credentials are expected to be usable
  immediately against DMS. In v8.0.0, `ApplicationInsertCommand.DataStoreIds`
  defaults to an empty array and is not marked `NotEmpty`.
- **FR-APP-3-DEFERRED**: Creating or updating an application SHOULD validate
  that the referenced claim set name exists. In v8.0.0, `ClaimSetName` is
  stored as a string and validated for format, but is not enforced by a
  foreign key or repository lookup.
- **FR-APP-5-DEFERRED**: Updating an application SHOULD support direct changes
  to an application-level enabled state if that state remains part of the
  public application contract. In v8.0.0, enabled/approved state is managed on
  ApiClient records, and `ApplicationUpdateCommand` has no enabled property.

### 2.4 Data Store Management

- **FR-DATASTORE-3-DEFERRED**: A `PUT /v3/dataStores/{id}` request that omits
  the connection-string field SHOULD preserve the stored value rather than
  requiring the caller to resupply it. In v8.0.0, the update path writes the
  request body's `ConnectionString` value.

### 2.5 Profiles

- **FR-PROFILE-5**: CMS SHOULD expose an endpoint for DMS to retrieve all
  Profiles assigned to a given application (`GET /v3/applications/{id}/profiles`),
  so DMS can resolve and cache which Profiles apply to a given API client. In
  v8.0.0, Profile CRUD exists and Applications expose `profileIds`, but no
  application-profile endpoint is mapped.

### 2.6 Error Handling and Responses

- **FR-ERROR-1-DEFERRED**: Every non-success CMS response — including
  framework-level errors, authentication/authorization failures,
  tenant-resolution middleware failures, dynamic claims-management endpoints,
  and OAuth/OIDC error responses — SHOULD return a JSON body conforming to the
  Ed-Fi Error Response Knowledge Base contract: `detail`, `type`, `title`,
  `status`, `correlationId`, `validationErrors`, and `errors`.
- **FR-ERROR-4-DEFERRED**: CMS SHOULD NOT return bare framework results (for
  example, `NotFound()` or `Forbid()`) or ad hoc `{ error, message }` shapes
  from any endpoint or middleware.
- **FR-ERROR-5-DEFERRED**: All unstructured or untrusted provider, transport,
  JSON, and operational error text SHOULD be replaced with safe, fixed
  fallback messages in client-facing responses.
- **FR-ERROR-6-DEFERRED**: Decide whether unresolved caller-supplied
  references should remain `400 Bad Request` validation failures, as in
  v8.0.0, or move to `409 Conflict` for closer ODS Admin API parity.

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

### 2.10 Rate Limiting

- **FR-RATE-1**: CMS SHOULD enforce a configurable rate limit on client
  requests and return `429 Too Many Requests` when a client exceeds it within a
  configured time window.

## 3. Non-Functional Requirements

### 3.1 Security and Privacy

- **NFR-SEC-4-DEFERRED**: Swagger/OpenAPI generation SHOULD be independently
  controllable so it can be disabled in production deployments that do not
  want to expose API metadata. In v8.0.0, `Program.cs` calls `AddOpenApi()` and
  `MapOpenApi()` unconditionally, and `/metadata/specifications` is always
  mapped.

### 3.2 Reliability and Operations

- **NFR-REL-1-DEFERRED**: CMS SHOULD expose a health endpoint reflecting the
  status of its own dependencies, at minimum the configuration database. In
  v8.0.0, `/health` returns the current server timestamp and does not check
  dependencies.
- **NFR-REL-2-DEFERRED**: In self-contained identity-provider mode, CMS SHOULD
  run an in-process, config-gated background sweep that deletes expired access
  token rows on a configurable interval.

### 3.3 Performance and Scalability

- **NFR-PERF-2-DEFERRED**: Running the expired-token cleanup sweep concurrently
  across multiple CMS replicas SHOULD be safe: the underlying delete should be
  idempotent, so replicas racing to delete the same expired rows cause no harm.

## 4. Out of Scope and Known Limitations

- CMS v8.1.0 planning should decide whether database-instance provisioning and
  education-organization synchronization remain CMS responsibilities or stay as
  host-run operational tooling.
- Secret expiration and scheduled rotation were researched in a design spike
  but are not implemented in v8.0.0; today, secret rotation is a manual reset
  with no expiration date tracked or enforced.
- Client-supplied correlation IDs, supported on the DMS resource API, are not
  supported on CMS v8.0.0.

## 5. Open Questions and Decision Log

- **Database-instance provisioning scope**: Should CMS build the async,
  template-based data-store provisioning workflow described above, or should
  schema provisioning remain a host-run tooling step (`api-schema-tools`)?
- **Education-organization synchronization scope**: Is a read-side
  education-organization cache still a priority for CMS, given DMS's own
  authorization model resolves relationships from live operational data rather
  than an administrative cache?
- **Rate limiting**: Confirm whether CMS should implement request rate
  limiting or intentionally leave rate limiting to upstream infrastructure.
- **Secret expiration/rotation**: Decide whether to implement
  `secret_expires_on` tracking and rotation tooling, or leave rotation as a
  fully manual, host-driven operation.
- **Error conformance scope**: Decide whether OAuth-standard error shapes and
  dynamic claims-management responses should be converted to the Ed-Fi error
  contract or explicitly documented as exceptions.
- **OpenAPI disablement**: Decide whether API metadata exposure should be
  controlled by configuration, environment, or deployment topology.
