# Token Info Endpoint - Quick Reference Guide

## Overview

The `/oauth/token_info` endpoint provides comprehensive information about an OAuth bearer token, including client details, authorized resources, profiles, and permissions. This document shows how JWT token claims are mapped to the token info response.

**Endpoint**: `POST /oauth/token_info`  
**Authentication**: Requires valid Bearer token in Authorization header  
**Content-Type**: `application/x-www-form-urlencoded`

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
| `services[]` | Database (ResourceClaims) | - | Claims filtered by service prefix pattern (e.g., `http://ed-fi.org/identity/claims/services/*`) |

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
  operations. **Note: The proposed design retrieves services from the
  ResourceClaims authorization metadata, filtered by a service-specific claim
  name prefix pattern (e.g., `http://ed-fi.org/identity/claims/services/*`).
  This filtering logic is not yet implemented in the DMS codebase.** This
  approach would align with the Ed-Fi ODS/API authorization
  model where services are stored as ResourceClaims in the security database alongside
  regular resource claims. See the [Ed-Fi ODS Implementation repository](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-Implementation)
  for reference implementations.

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
| `services[]` | Array of services | ResourceClaims query | Filtered by service claim name prefix |

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
| `services[]` | Array of services | ResourceClaims query | Filtered by service claim name prefix |

## Implementation Notes

### ✅ Currently Implemented

- JWT token validation and claims extraction
- Basic response fields: `active`, `client_id`, `exp`, `namespace_prefixes`,
  `claim_set`, `resources`
- Education organization IDs from token (without enrichment)

### ❌ Pending Implementation

- **Services Support** - PROPOSED FEATURE
  - Populate `services[]` array from ResourceClaims table
  - Implement filtering by service-specific claim name prefix pattern
    (e.g., `http://ed-fi.org/identity/claims/services/*`)
  - Distinguish services from standard CRUD resources

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

## References

- [Ed-Fi ODS API Authorization Guide](https://docs.ed-fi.org/reference/ods-api/client-developers-guide/authorization/#token-info)

---

**Related Ticket**: [DMS-902](https://edfi.atlassian.net/browse/DMS-902)
