# Product Requirements Document: Ed-Fi API Configuration Service (CS) v8.2

> **Status**: Draft \
> **Owner**: Vinaya Mayya \
> **Product**: Ed-Fi API Configuration Service (CS) \
> **Repository**: `Ed-Fi-Alliance-OSS/Data-Management-Service` (`src/config`) \
> **Companion document**: [CS v8.1 Product Requirements](./PRD-CS-v8.1.md)

## 1. Purpose

Early draft of potential 8.2 requirements.

## 2. Functional Requirements

### 2.1 Application Management

- **FR-APP-8**: Creating or updating an application SHOULD validate that the
  referenced claim set name exists, so applications cannot be configured
  against a claim set that will never resolve.

### 2.2 Error Handling and Responses

- **FR-ERROR-10**: CS SHOULD accept a client-supplied correlation ID through a
  configurable request header, applying the same character allowlist, maximum
  length, and invalid-value handling as the DMS resource API (DMS v8.1
  FR-LOG-3 through FR-LOG-5), and SHOULD use it as the `correlationId` in
  error responses and log entries. Supersedes v8.0 NFR-OBS-4.

## 3. Non-Functional Requirements

### 3.1 Security and Privacy

- **NFR-SEC-9**: Swagger/OpenAPI generation SHOULD be independently
  controllable so it can be disabled in production deployments that do not
  want to expose API metadata. (Supersedes v8.0 NFR-SEC-4)

### 3.2 Reliability and Operations

- **NFR-REL-7**: CS SHOULD expose a health endpoint reflecting the status of
  its own dependencies, at minimum the configuration database, extending the
  basic anonymous liveness endpoint v8.0 already provides (NFR-REL-1) without
  breaking the existing `/health` liveness contract.

## 4. Open Questions and Decision Log

- **Error conformance scope**: Decide whether `/connect/token` errors should
  stay in the Ed-Fi error contract or move to the RFC 6749 OAuth error shape
  used by `/connect/revoke`.
- **OpenAPI disablement**: Decide whether API metadata exposure should be
  controlled by configuration, environment, or deployment topology.
- **Dependency-aware health reporting**: Decide which dependencies (database,
  identity provider, others) the CS health endpoint should check, and how
  CS should report partial dependency failures without breaking existing
  health-probe contracts.
- **Secret expiration/rotation**: Decide whether to implement
  `secret_expires_on` tracking and rotation tooling, or leave rotation as a
  fully manual, host-driven operation. (Carried over from v8.1; still
  unresolved as of this writing.)
