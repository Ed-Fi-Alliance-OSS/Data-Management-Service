# Product Requirements Document: Ed-Fi Configuration Management Service (CMS) v8.2

> **Status**: Draft \
> **Owner**: Stephen Fuqua \
> **Product**: Ed-Fi Configuration Management Service (CMS) \
> **Repository**: `Ed-Fi-Alliance-OSS/Data-Management-Service` (`src/config`) \
> **Companion document**: [CMS v8.1 Product Requirements](./PRD-CMS-v8.1.md)

## 1. Purpose

Early draft of potential 8.2 requirements.

## 2.  Functional Requirements

### 2.1 Application Management

- **FR-APP-8**: Creating or updating an application SHOULD validate that the
  referenced claim set name exists, so applications cannot be configured
  against a claim set that will never resolve.

### 2.2 Error Handling and Responses

- **FR-ERROR-9**: Decide whether unresolved caller-supplied references should
  remain `400 Bad Request` validation failures or move to `409 Conflict` for
  closer ODS Admin API parity, and apply that decision consistently across
  CMS endpoints.
- **FR-ERROR-10**: Accept a client-provided correlation ID, with the same
  limitations found in [DMS 8.1](./PRD-DMS-v8.1.md).

## 3. Non-Functional Requirements

### 3.1 Security and Privacy

- **NFR-SEC-4**: Swagger/OpenAPI generation SHOULD be independently
  controllable so it can be disabled in production deployments that do not
  want to expose API metadata.

### 3.2 Reliability and Operations

- **NFR-REL-7**: CMS SHOULD expose a health endpoint reflecting the status of
  its own dependencies, at minimum the configuration database, extending the
  basic anonymous liveness endpoint v8.0.0 already provides (NFR-REL-1).

## 4. Open Questions and Decision Log

- **Error conformance scope**: Decide whether unresolved-reference errors
  should use `400` or `409`, and whether OAuth-standard error shapes and
  dynamic claims-management responses should be converted to the Ed-Fi error
  contract or explicitly documented as exceptions.
- **OpenAPI disablement**: Decide whether API metadata exposure should be
  controlled by configuration, environment, or deployment topology.
- **Dependency-aware health reporting**: Decide which dependencies (database,
  identity provider, others) the CMS health endpoint should check, and how
  CMS should report partial dependency failures without breaking existing
  health-probe contracts.
- **Secret expiration/rotation**: Decide whether to implement
  `secret_expires_on` tracking and rotation tooling, or leave rotation as a
  fully manual, host-driven operation. (Carried over from v8.1; still
  unresolved as of this writing.)
