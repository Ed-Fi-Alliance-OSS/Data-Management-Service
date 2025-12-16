# Token Info Endpoint - Quick Reference Guide

## Overview

The `/oauth/token_info` endpoint provides comprehensive information about an OAuth bearer token, including client details, authorized resources, profiles, and permissions. This document shows how JWT token claims are mapped to the token info response.

**Endpoint**: `POST /oauth/token_info`  
**Authentication**: Requires valid Bearer token in Authorization header  
**Content-Type**: `application/x-www-form-urlencoded`

## Request/Response Flow

```mermaid
flowchart LR
    A[Client Request<br/>JWT Token Bearer<br/>+ token parameter] --> B[Token Info Processing]
    B --> B1[1. Validate Token]
    B1 --> B2[2. Extract Claims]
    B2 --> B3[3. Query Database]
    B3 --> B4[4. Enrich Data]
    B4 --> B5[5. Build Response]
    B5 --> C[Token Info Response<br/>JSON]
    
    style A fill:#e1f5ff
    style C fill:#e1ffe1
    style B fill:#fff4e1
```

## Token Info Response Structure

```json
{
  "active": true,
  "client_id": "8cc91775-aaae-47c8-abdc-f4671bbb6cdb",
  "exp": 1765572094,
  "namespace_prefixes": [
    "uri://ed-fi.org"
  ],
  "education_organizations": [
    {
      "education_organization_id": 255,
      "name_of_institution": "Grand Bend Elementary School",
      "type": "edfi.School",
      "local_education_agency_id": 255901,
      "education_service_center_id": null
    },
    {
      "education_organization_id": 255901,
      "name_of_institution": "Grand Bend ISD",
      "type": "edfi.LocalEducationAgency",
      "local_education_agency_id": null,
      "education_service_center_id": 255950
    }
  ],
  "assigned_profiles": [
    "Test-Profile-Student-Read-Only"
  ],
  "claim_set": {
    "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet"
  },
  "resources": [
    {
      "resource": "/ed-fi/students",
      "operations": ["Create", "Read", "Update", "Delete", "ReadChanges"]
    },
    {
      "resource": "/ed-fi/schools",
      "operations": ["Read"]
    }
  ],
  "services": [
    {
      "service": "identity",
      "operations": ["Create", "Read", "Update"]
    }
  ]
}
```

## Field Mapping Summary

| Response Field | Data Source | Extracted From | Enriched From Database |
|---------------|-------------|----------------|----------------------|
| `active` | Token Validation | JWT signature + expiration | Token revocation status |
| `client_id` | JWT Token | `client_id` or `sub` claim | - |
| `exp` | JWT Token | `exp` claim | - |
| `namespace_prefixes[]` | JWT Token | `namespacePrefixes` claim | - |
| `education_organizations[]` | JWT Token + Database | `educationOrganizationIds` claim | DMS Document table (name, type, hierarchy) |
| `assigned_profiles[]`* | Database | - | Profile + ApplicationProfile tables |
| `claim_set.name` | JWT Token | `scope` claim | - |
| `resources[]` | Database + Metadata API | - | ClaimsHierarchy → AuthorizationMetadata → ClaimSet claims |
| `services[]` | Hardcoded | - | Ed-Fi special services (e.g., Identity API) - separate from CRUD resources |

**Notes**:

- *Profile support is not yet implemented. The `assigned_profiles` field will
  return an empty array until the Profile and ApplicationProfile tables are
  created and populated.
- **OpenIddict Introspection Endpoint**: The DMS Config Service also exposes the
  standard OAuth 2.0 introspection endpoint at `/connect/introspect` (OpenIddict
  implementation). This endpoint returns a subset of token information including
  `active`, `client_id`, `exp`, `scope`, and other standard OAuth claims. The
  `/oauth/token_info` endpoint extends this with DMS-specific enrichments like
  education organizations, profiles, resources, and services.
- **Services** represent special Ed-Fi APIs (like the Identity API for person
  matching) that are separate from standard CRUD resources. According to
  [Ed-Fi documentation](https://docs.ed-fi.org/reference/ods-api/client-developers-guide/authorization/#token-info),
  services are distinct from resources and typically include specialized
  operations. Currently hardcoded to return the Identity service, but could be
  extended to include other special services in the future.

## Example 1: OpenIddict Token (DMS Config Service)

### Decoded JWT Token

```json
{
  "jti": "8a59695b-9c53-4f1c-a36b-9f0d88ea5f2a",
  "sub": "8cc91775-aaae-47c8-abdc-f4671bbb6cdb",
  "iat": "1765570294",
  "exp": "1765572094",
  "client_id": "8cc91775-aaae-47c8-abdc-f4671bbb6cdb",
  "typ": "Bearer",
  "azp": "8cc91775-aaae-47c8-abdc-f4671bbb6cdb",
  "scope": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
  "aud": "account",
  "iss": "http://dms-config-service:8081",
  "namespacePrefixes": "uri://ed-fi.org",
  "educationOrganizationIds": "255,255901",
  "client_name": "For ed orgs",
  "permission": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
  "http://schemas.microsoft.com/ws/2008/06/identity/claims/role": ["dms-client"],
  "dmsInstanceIds": "2"
}
```

### Token Info Response (Example 1)

```json
{
  "active": true,
  "client_id": "8cc91775-aaae-47c8-abdc-f4671bbb6cdb",
  "exp": 1765572094,
  "namespace_prefixes": [
    "uri://ed-fi.org"
  ],
  "education_organizations": [
    {
      "education_organization_id": 255,
      "name_of_institution": "Grand Bend Elementary School",
      "type": "edfi.School",
      "local_education_agency_id": 255901,
      "education_service_center_id": null
    },
    {
      "education_organization_id": 255901,
      "name_of_institution": "Grand Bend ISD",
      "type": "edfi.LocalEducationAgency",
      "local_education_agency_id": null,
      "education_service_center_id": 255950
    }
  ],
  "assigned_profiles": [
    "Test-Profile-Student-Read-Only"
  ],
  "claim_set": {
    "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet"
  },
  "resources": [
    {
      "resource": "/ed-fi/students",
      "operations": ["Create", "Read", "Update", "Delete", "ReadChanges"]
    },
    {
      "resource": "/ed-fi/schools",
      "operations": ["Read"]
    }
  ],
  "services": [
    {
      "service": "identity",
      "operations": ["Create", "Read", "Update"]
    }
  ]
}
```

### Field Mapping for Example 1

| Response Field | Value | Source | Notes |
|---------------|-------|--------|-------|
| `active` | `true` | Token validation | Token is valid and not expired |
| `client_id` | `"8cc91775-aaae-47c8-abdc-f4671bbb6cdb"` | JWT `client_id` claim | Direct extraction |
| `exp` | `1765572094` | JWT `exp` claim | Converted from string to number |
| `namespace_prefixes[0]` | `"uri://ed-fi.org"` | JWT `namespacePrefixes` claim | Split comma-separated string |
| `education_organizations[0].education_organization_id` | `255` | JWT `educationOrganizationIds` claim | Parsed from "255,255901" |
| `education_organizations[0].name_of_institution` | `"Grand Bend Elementary School"` | Ed-Fi ODS query | Enriched from database |
| `education_organizations[0].type` | `"edfi.School"` | Ed-Fi ODS query | Enriched from database |
| `education_organizations[0].local_education_agency_id` | `255901` | Ed-Fi ODS query | Parent LEA from hierarchy |
| `education_organizations[1].education_organization_id` | `255901` | JWT `educationOrganizationIds` claim | Parsed from "255,255901" |
| `education_organizations[1].name_of_institution` | `"Grand Bend ISD"` | Ed-Fi ODS query | Enriched from database |
| `education_organizations[1].type` | `"edfi.LocalEducationAgency"` | Ed-Fi ODS query | Enriched from database |
| `education_organizations[1].education_service_center_id` | `255950` | Ed-Fi ODS query | Parent ESC from hierarchy |
| `assigned_profiles[0]` | `"Assessment-Profile-Read-Only"` | ApplicationProfile query | Queried from Profile tables |
| `claim_set.name` | `"E2E-RelationshipsWithEdOrgsOnlyClaimSet"` | JWT `scope` claim | Direct extraction |
| `resources[]` | Array of resources | ClaimSet hierarchy query | Built from claim set configuration |
| `services[]` | Array of services | Configuration | Hardcoded or configured |

## Example 2: Keycloak Token

### Decoded JWT Token

```json
{
  "exp": 1765572891,
  "iat": 1765571091,
  "jti": "266638ef-44ad-4ada-a17b-da2d284dd27e",
  "iss": "http://localhost:8045/realms/edfi",
  "aud": "account",
  "sub": "5adb3971-bcbe-4aaa-8129-c70f50fce021",
  "typ": "Bearer",
  "azp": "5296a732-b986-44b6-b48c-2d5e1fcbb5c0",
  "scope": "EdFiSandbox",
  "dmsInstanceIds": "2",
  "clientHost": "172.19.0.1",
  "namespacePrefixes": "uri://ed-fi.org",
  "educationOrganizationIds": "255,255901",
  "http://schemas.microsoft.com/ws/2008/06/identity/claims/role": [
    "offline_access",
    "default-roles-edfi",
    "uma_authorization",
    "dms-client"
  ],
  "clientAddress": "172.19.0.1",
  "client_id": "5296a732-b986-44b6-b48c-2d5e1fcbb5c0"
}
```

### Token Info Response (Example 2)

```json
{
  "active": true,
  "client_id": "5296a732-b986-44b6-b48c-2d5e1fcbb5c0",
  "exp": 1765572891,
  "namespace_prefixes": [
    "uri://ed-fi.org"
  ],
  "education_organizations": [
    {
      "education_organization_id": 255,
      "name_of_institution": "Grand Bend Elementary School",
      "type": "edfi.School",
      "local_education_agency_id": 255901,
      "education_service_center_id": null
    },
    {
      "education_organization_id": 255901,
      "name_of_institution": "Grand Bend ISD",
      "type": "edfi.LocalEducationAgency",
      "local_education_agency_id": null,
      "education_service_center_id": 255950
    }
  ],
  "assigned_profiles": [
    "Student-Profile-Full-Access"
  ],
  "claim_set": {
    "name": "EdFiSandbox"
  },
  "resources": [
    {
      "resource": "/ed-fi/students",
      "operations": ["Create", "Read", "Update", "Delete", "ReadChanges"]
    },
    {
      "resource": "/ed-fi/assessments",
      "operations": ["Create", "Read", "Update", "Delete", "ReadChanges"]
    },
    {
      "resource": "/ed-fi/schools",
      "operations": ["Create", "Read", "Update", "Delete", "ReadChanges"]
    },
    {
      "resource": "/ed-fi/studentSchoolAssociations",
      "operations": ["Create", "Read", "Update", "Delete", "ReadChanges"]
    }
  ],
  "services": [
    {
      "service": "identity",
      "operations": ["Create", "Read", "Update"]
    }
  ]
}
```

### Field Mapping for Example 2

| Response Field | Value | Source | Notes |
|---------------|-------|--------|-------|
| `active` | `true` | Token validation | Token is valid and not expired |
| `client_id` | `"5296a732-b986-44b6-b48c-2d5e1fcbb5c0"` | JWT `client_id` claim | Direct extraction |
| `exp` | `1765572891` | JWT `exp` claim | Already numeric in Keycloak |
| `namespace_prefixes[0]` | `"uri://ed-fi.org"` | JWT `namespacePrefixes` claim | Split comma-separated string |
| `education_organizations[0].education_organization_id` | `255` | JWT `educationOrganizationIds` claim | Parsed from "255,255901" |
| `education_organizations[0].name_of_institution` | `"Grand Bend Elementary School"` | DMS Document.EdfiDoc | Extracted from JSON |
| `education_organizations[0].type` | `"edfi.School"` | DMS Document.ResourceName | From document metadata |
| `education_organizations[0].local_education_agency_id` | `255901` | DMS EducationOrganizationHierarchy | Parent LEA from hierarchy |
| `education_organizations[1].education_organization_id` | `255901` | JWT `educationOrganizationIds` claim | Parsed from "255,255901" |
| `education_organizations[1].name_of_institution` | `"Grand Bend ISD"` | DMS Document.EdfiDoc | Extracted from JSON |
| `education_organizations[1].type` | `"edfi.LocalEducationAgency"` | DMS Document.ResourceName | From document metadata |
| `education_organizations[1].education_service_center_id` | `255950` | DMS EducationOrganizationHierarchy | Parent ESC from hierarchy |
| `assigned_profiles[0]` | `"Student-Profile-Full-Access"` | ApplicationProfile query | Queried from Profile tables |
| `claim_set.name` | `"EdFiSandbox"` | JWT `scope` claim | Direct extraction |
| `resources[]` | Array of resources | ClaimSet hierarchy query | Built from EdFiSandbox claim set |
| `services[]` | Array of services | Configuration | Hardcoded or configured |

## Key Differences Between Providers

| Aspect | OpenIddict (DMS Config) | Keycloak |
|--------|------------------------|----------|
| **Token Format** | Custom DMS claims | Standard OAuth + custom |
| **Exp Claim** | String format (`"1765572094"`) | Numeric format (`1765572891`) |
| **Client Identification** | Uses `client_id` claim | Uses `client_id` claim |
| **Scope Claim** | Single claim set name | Claim set name or standard scopes |
| **Custom Claims** | `client_name`, `permission` | `clientHost`, `clientAddress` |
| **Role Claims** | Simple array | Multiple standard roles included |
| **Issuer** | DMS Config Service URL | Keycloak realm URL |

## Data Enrichment Process

```mermaid
flowchart LR
    subgraph JWT["JWT Token Claims"]
        A1[client_id]
        A2[exp]
        A3[scope]
        A4[namespacePrefixes]
        A5[educationOrganizationIds]
        A6[not in token]
        A7[configuration]
    end
    
    subgraph DB["Database Queries"]
        B1[ApiClient → Application → Vendor]
        B2[ClaimSet → ResourceClaim → Actions]
        B4[DMS Document<br/>EdfiDoc JSON, ResourceName]
        B5[ApplicationProfile → Profile]
        B6[Service Config]
    end
    
    subgraph RESP["Final Response"]
        C1[client_id<br/>exp<br/>namespace_prefixes]
        C2[claim_set<br/>resources]
        C3[education_organizations]
        C4[assigned_profiles]
        C5[services]
    end
    
    A1 --> B1 --> C1
    A2 --> B1
    A3 --> B2 --> C2
    A4 --> C1
    A5 --> B4 --> C3
    A6 --> B5 --> C4
    A7 --> B6 --> C5
    
    style JWT fill:#e1f5ff
    style DB fill:#fff4e1
    style RESP fill:#e1ffe1
```

## Implementation Notes

### ✅ Currently Implemented

- JWT token validation and claims extraction
- Basic response fields: `active`, `client_id`, `exp`, `namespace_prefixes`, `claim_set`, `resources`, `services`
- Education organization IDs from token (without enrichment)
- Claim set resource mapping from database or `/metadata/dependencies` endpoint

### ❌ Pending Implementation (DMS-885)

- **Profile Support** - NEW FEATURE
  - Database tables: `Profile`, `ApplicationProfile`
  - Query profiles by application
  - Populate `assigned_profiles[]` array
  
- **Education Organization Enrichment** - FUTURE
  - Query DMS Document table for org details
  - Extract `name_of_institution` from EdfiDoc JSON
  - Get `type` from ResourceName field
  - Query EducationOrganizationHierarchy for parent relationships

## Usage Example

### Request

```http
POST /oauth/token_info HTTP/1.1
Host: dms-config-service:8081
Authorization: Bearer eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9...
Content-Type: application/x-www-form-urlencoded

token=eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9...
```

### Response

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "active": true,
  "client_id": "8cc91775-aaae-47c8-abdc-f4671bbb6cdb",
  "exp": 1765572094,
  "namespace_prefixes": ["uri://ed-fi.org"],
  "education_organizations": [...],
  "assigned_profiles": [...],
  "claim_set": { "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet" },
  "resources": [...],
  "services": [...]
}
```

## SQL Query Examples for Education Organizations

### Get Education Organization Document from DMS Database

```sql
-- Get education organization document and hierarchy information
SELECT 
    d.Id as DocumentId,
    d.DocumentPartitionKey,
    d.ResourceName,
    d.EdfiDoc,
    eoh.EducationOrganizationId,
    eoh.ParentId,
    parent_eoh.EducationOrganizationId as ParentEducationOrganizationId
FROM dms.Document d
INNER JOIN dms.EducationOrganizationHierarchy eoh 
    ON d.Id = eoh.DocumentId 
    AND d.DocumentPartitionKey = eoh.DocumentPartitionKey
LEFT JOIN dms.EducationOrganizationHierarchy parent_eoh
    ON eoh.ParentId = parent_eoh.Id
WHERE eoh.EducationOrganizationId = @EducationOrganizationId;
```

### Extract Name from Education Organization JSON

```sql
-- Extract nameOfInstitution from EdfiDoc JSON for multiple education organizations
SELECT 
    eoh.EducationOrganizationId,
    d.ResourceName as OrganizationType,
    d.EdfiDoc->>'nameOfInstitution' as NameOfInstitution,
    d.EdfiDoc
FROM dms.Document d
INNER JOIN dms.EducationOrganizationHierarchy eoh 
    ON d.Id = eoh.DocumentId 
    AND d.DocumentPartitionKey = eoh.DocumentPartitionKey
WHERE eoh.EducationOrganizationId IN (255, 255901);
```

### Get Education Organization Ancestors (LEA, ESC)

```sql
-- Use the built-in function to get all ancestor organizations
SELECT 
    eoh.EducationOrganizationId,
    d.ResourceName as OrganizationType,
    d.EdfiDoc->>'nameOfInstitution' as NameOfInstitution
FROM dms.GetEducationOrganizationAncestors(255) ancestors
INNER JOIN dms.EducationOrganizationHierarchy eoh
    ON ancestors.EducationOrganizationId = eoh.EducationOrganizationId
INNER JOIN dms.Document d
    ON eoh.DocumentId = d.Id
    AND eoh.DocumentPartitionKey = d.DocumentPartitionKey
ORDER BY 
    CASE d.ResourceName
        WHEN 'EducationServiceCenter' THEN 1
        WHEN 'LocalEducationAgency' THEN 2
        WHEN 'School' THEN 3
        ELSE 4
    END;
```

### Complete Education Organization Enrichment Query

```sql
-- Get enriched education organization data with parent relationships
WITH EdOrgBase AS (
    SELECT 
        eoh.EducationOrganizationId,
        d.ResourceName as OrganizationType,
        d.EdfiDoc->>'nameOfInstitution' as NameOfInstitution,
        eoh.ParentId
    FROM dms.EducationOrganizationHierarchy eoh
    INNER JOIN dms.Document d
        ON eoh.DocumentId = d.Id
        AND eoh.DocumentPartitionKey = d.DocumentPartitionKey
    WHERE eoh.EducationOrganizationId IN (255, 255901)
),
ParentData AS (
    SELECT 
        child.EducationOrganizationId as ChildEdOrgId,
        parent_eoh.EducationOrganizationId as ParentEdOrgId,
        parent_doc.ResourceName as ParentType
    FROM EdOrgBase child
    INNER JOIN dms.EducationOrganizationHierarchy parent_eoh
        ON child.ParentId = parent_eoh.Id
    LEFT JOIN dms.Document parent_doc
        ON parent_eoh.DocumentId = parent_doc.Id
        AND parent_eoh.DocumentPartitionKey = parent_doc.DocumentPartitionKey
)
SELECT 
    eb.EducationOrganizationId,
    eb.NameOfInstitution,
    'edfi.' || eb.OrganizationType as Type,
    CASE 
        WHEN eb.OrganizationType = 'School' THEN 
            (SELECT ParentEdOrgId FROM ParentData WHERE ChildEdOrgId = eb.EducationOrganizationId AND ParentType = 'LocalEducationAgency')
        ELSE NULL 
    END as LocalEducationAgencyId,
    CASE 
        WHEN eb.OrganizationType = 'LocalEducationAgency' THEN 
            (SELECT ParentEdOrgId FROM ParentData WHERE ChildEdOrgId = eb.EducationOrganizationId AND ParentType = 'EducationServiceCenter')
        ELSE NULL 
    END as EducationServiceCenterId
FROM EdOrgBase eb
ORDER BY eb.EducationOrganizationId;
```

**Example Result:**

| EducationOrganizationId | NameOfInstitution | Type | LocalEducationAgencyId | EducationServiceCenterId |
|------------------------|-------------------|------|------------------------|--------------------------|
| 255 | Grand Bend Elementary School | edfi.School | 255901 | NULL |
| 255901 | Grand Bend ISD | edfi.LocalEducationAgency | NULL | 255950 |

## References

- [Ed-Fi ODS API Authorization Guide](https://docs.ed-fi.org/reference/ods-api/client-developers-guide/authorization/#token-info)

---

**Related Ticket**: [DMS-902](https://edfi.atlassian.net/browse/DMS-902)
