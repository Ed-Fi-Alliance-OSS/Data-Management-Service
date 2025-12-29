# CMS Profiles Design Document

## DMS-877: Profile Storage and Management

### Overview

This document describes the design for storing and managing API Profiles in
the DMS Configuration Service (CMS). Profiles are managed through a new
`/v2/profiles` endpoint and associated with Applications via a many-to-many
relationship.

---

## 1. Problem Statement

API Profiles enable platform hosts to define data policies that constrain the
surface area of API Resources for specific usage scenarios (e.g., Nutrition,
Special Education). The Configuration Service needs to:

1. Store profile definitions (XML format)
2. Provide CRUD operations for profile management
3. Associate profiles with Applications (many-to-many)
4. Expose profile data for DMS to fetch and cache

---

## 2. Data Model

### 2.1 Profile Entity

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| `Id` | BIGINT | PK, Auto-generated | Unique identifier |
| `ProfileName` | VARCHAR(256) | NOT NULL, UNIQUE | Profile name |
| `Definition` | TEXT | NOT NULL | XML profile definition |
| `CreatedAt` | TIMESTAMP | NOT NULL, DEFAULT NOW() | Creation timestamp |
| `CreatedBy` | VARCHAR(256) | | Creator identifier |
| `LastModifiedAt` | TIMESTAMP | | Modification timestamp |
| `ModifiedBy` | VARCHAR(256) | | Modifier identifier |

### 2.2 Application-Profile Junction Table

| Field | Type | Constraints | Description |
|-------|------|-------------|-------------|
| `ApplicationId` | BIGINT | FK, ON DELETE CASCADE | Application reference |
| `ProfileId` | BIGINT | FK, ON DELETE RESTRICT | Profile reference |
| `CreatedAt` | TIMESTAMP | NOT NULL, DEFAULT NOW() | Creation timestamp |
| `CreatedBy` | VARCHAR(256) | | Creator identifier |

**Composite Primary Key:** (`ApplicationId`, `ProfileId`)

**Note:** `ON DELETE RESTRICT` on ProfileId prevents deleting a Profile that
is still assigned to Applications.

### 2.3 Entity Relationship Diagram

```text
┌─────────────┐       ┌─────────────────────────┐       ┌─────────────┐
│  Vendor     │       │  Application            │       │  Profile    │
├─────────────┤       ├─────────────────────────┤       ├─────────────┤
│ Id (PK)     │──────<│ Id (PK)                 │>──────│ Id (PK)     │
│ VendorName  │   1:N │ ApplicationName         │  M:N  │ ProfileName │
│ ...         │       │ VendorId (FK)           │       │ Definition  │
└─────────────┘       │ ClaimSetName            │       │ ...         │
                      │ ...                     │       └─────────────┘
                      └─────────────────────────┘
                                 │
                                 │ 1:N
                                 ▼
                      ┌─────────────────────────┐
                      │  ApiClient              │
                      ├─────────────────────────┤
                      │ Id (PK)                 │
                      │ ApplicationId (FK)      │
                      │ ClientId                │
                      │ ...                     │
                      └─────────────────────────┘
```

---

## 3. API Endpoints

### 3.1 Profile Management (`/v2/profiles`)

| Method | Endpoint | Description | Auth Scope |
|--------|----------|-------------|------------|
| POST | `/v2/profiles` | Create a new profile | Admin |
| GET | `/v2/profiles` | List all profiles (paginated) | ReadOnly or Admin |
| GET | `/v2/profiles/{id}` | Get profile by ID | ReadOnly or Admin |
| PUT | `/v2/profiles/{id}` | Update a profile | Admin |
| DELETE | `/v2/profiles/{id}` | Delete a profile | Admin |

### 3.2 Request/Response Models

#### ProfileInsertCommand (POST body)

```json
{
  "profileName": "Student-Exclude-BirthDate",
  "definition": "<Profile name=\"Student-Exclude-BirthDate\">...</Profile>"
}
```

#### ProfileUpdateCommand (PUT body)

```json
{
  "id": 123,
  "profileName": "Student-Exclude-BirthDate",
  "definition": "<Profile name=\"Student-Exclude-BirthDate\">...</Profile>"
}
```

#### ProfileResponse (GET response)

```json
{
  "id": 123,
  "profileName": "Student-Exclude-BirthDate",
  "definition": "<Profile name=\"Student-Exclude-BirthDate\">...</Profile>",
  "createdAt": "2025-01-15T10:30:00Z",
  "createdBy": "admin@example.com",
  "lastModifiedAt": "2025-01-16T14:22:00Z",
  "modifiedBy": "admin@example.com"
}
```

### 3.3 Validation Rules

| Field | Rules |
|-------|-------|
| `profileName` | Required, max 256 chars, unique, alphanumeric/hyphens |
| `definition` | Required, valid XML, `<Profile>` root, name must match |

### 3.4 Error Responses

| Scenario | HTTP Status | Error Type |
|----------|-------------|------------|
| Validation failure | 400 | `urn:ed-fi:api:bad-request` |
| Profile not found | 404 | `urn:ed-fi:api:not-found` |
| Duplicate name | 409 | `urn:ed-fi:api:conflict:duplicate` |
| Profile in use | 409 | `urn:ed-fi:api:conflict:dependent-item-exists` |

---

## 4. Application-Profile Association

### 4.1 Modified Application Endpoints

The existing Application endpoints are extended to support profile assignment:

#### ApplicationInsertCommand (modified)

```json
{
  "applicationName": "Nutrition App",
  "vendorId": 1,
  "claimSetName": "SIS Vendor",
  "educationOrganizationIds": [255901],
  "dmsInstanceIds": [1],
  "profileIds": [123, 456]
}
```

#### ApplicationUpdateCommand (modified)

```json
{
  "id": 1,
  "applicationName": "Nutrition App",
  "vendorId": 1,
  "claimSetName": "SIS Vendor",
  "educationOrganizationIds": [255901],
  "dmsInstanceIds": [1],
  "profileIds": [123, 456]
}
```

#### ApplicationResponse (modified)

```json
{
  "id": 1,
  "applicationName": "Nutrition App",
  "vendorId": 1,
  "claimSetName": "SIS Vendor",
  "educationOrganizationIds": [255901],
  "dmsInstanceIds": [1],
  "profileIds": [123, 456],
  "createdAt": "2025-01-15T10:30:00Z",
  "createdBy": "admin@example.com",
  "lastModifiedAt": null,
  "modifiedBy": null
}
```

### 4.2 Validation Rules for Profile Assignment

| Rule | Description |
|------|-------------|
| Valid ProfileIds | All provided ProfileIds must exist |
| No duplicates | ProfileIds array must not contain duplicates |

---

## 5. DMS Integration Endpoint

### 5.1 Application Profiles Endpoint

DMS needs to fetch all profiles assigned to an application. A single endpoint
provides this:

| Method | Endpoint | Description | Auth |
|--------|----------|-------------|------|
| GET | `/v2/applications/{id}/profiles` | Get app profiles | Service |

DMS caches all profiles for an application at once, then resolves profile
requests from the cache.

#### Response

```json
[
  {
    "id": 123,
    "profileName": "Student-Exclude-BirthDate",
    "definition": "<Profile name=\"Student-Exclude-BirthDate\">...</Profile>"
  },
  {
    "id": 456,
    "profileName": "Nutrition-Profile",
    "definition": "<Profile name=\"Nutrition-Profile\">...</Profile>"
  }
]
```

#### Empty Response (no profiles assigned)

```json
[]
```

### 5.2 DMS Caching Strategy

1. On first request from a client, DMS calls `GET /v2/applications/{applicationId}/profiles`
2. DMS caches the full profile array keyed by `applicationId`
3. Subsequent requests resolve profiles from cache by `profileName`
4. Cache entries expire based on configured TTL (default: 30 minutes, matching ODS/API)

---

## 6. Database Schema (PostgreSQL)

### 6.1 Profile Table

```sql
CREATE TABLE dmscs.Profile (
    Id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ProfileName VARCHAR(256) NOT NULL,
    Definition TEXT NOT NULL,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    LastModifiedAt TIMESTAMP,
    ModifiedBy VARCHAR(256),
    CONSTRAINT uq_profile_name UNIQUE (ProfileName)
);

CREATE INDEX ix_profile_name ON dmscs.Profile (ProfileName);
```

### 6.2 ApplicationProfile Junction Table

```sql
CREATE TABLE dmscs.ApplicationProfile (
    ApplicationId BIGINT NOT NULL,
    ProfileId BIGINT NOT NULL,
    CreatedAt TIMESTAMP NOT NULL DEFAULT NOW(),
    CreatedBy VARCHAR(256),
    PRIMARY KEY (ApplicationId, ProfileId),
    CONSTRAINT fk_applicationprofile_application
        FOREIGN KEY (ApplicationId) REFERENCES dmscs.Application(Id) ON DELETE CASCADE,
    CONSTRAINT fk_applicationprofile_profile
        FOREIGN KEY (ProfileId) REFERENCES dmscs.Profile(Id) ON DELETE RESTRICT
);
```

---

## 7. Implementation Components

### 7.1 New Files

| Layer | File | Description |
|-------|------|-------------|
| DataModel | `Model/Profile/ProfileResponse.cs` | Response DTO |
| DataModel | `Model/Profile/ProfileInsertCommand.cs` | Insert command |
| DataModel | `Model/Profile/ProfileUpdateCommand.cs` | Update command |
| Backend | `Repositories/IProfileRepository.cs` | Interface + results |
| Backend.Postgresql | `Repositories/ProfileRepository.cs` | Implementation |
| Backend.Postgresql | `Deploy/Scripts/00XX_Create_Profile.sql` | Migration |
| Frontend | `Modules/ProfileModule.cs` | Endpoint module |

### 7.2 Modified Files

| Layer | File | Change |
|-------|------|--------|
| DataModel | `ApplicationInsertCommand.cs` | Add `ProfileIds` |
| DataModel | `ApplicationUpdateCommand.cs` | Add `ProfileIds` |
| DataModel | `ApplicationResponse.cs` | Add `ProfileIds` |
| Backend | `IApplicationRepository.cs` | Update results |
| Backend.Postgresql | `ApplicationRepository.cs` | Junction table |
| Backend.Postgresql | `Deploy/Scripts/` | Add migration |
