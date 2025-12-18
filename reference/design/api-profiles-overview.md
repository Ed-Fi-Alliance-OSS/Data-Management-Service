# API Profiles Overview

## 1. Scope

**Profiles** in Ed-Fi DMS are a mechanism for creating data policies for specific API resources, generally supporting particular usage scenarios such as nutrition or special education specialty applications (Ed-Fi Alliance, n.d.-a). An API Profile enables:

- **Data Shape Transformation**: Controlling which resource fields, collections, and references are visible or modifiable for each API client through explicit inclusion or exclusion rules.

- **Security/Privacy Filtering**: Enforcing privacy, regulatory, and business rules by restricting data exposure and write access at the API layer.

As noted in the Ed-Fi documentation, "the policy is expressed as a set of rules for explicit inclusion or exclusion of properties, references, collections, and/or collection items (based on Type or Ed-Fi Descriptor values) at all levels of a Resource" (Ed-Fi Alliance, n.d.-a, para. 1). Profiles are storage-agnostic and operate at the middleware pipeline, supporting both document-store and cloud-native architectures.

---

## 2. Key Concepts

Profiles define two distinct content types that control data access: `ReadContentType` and `WriteContentType`. According to the Ed-Fi ODS/API documentation, "resource properties may be read-write, read-only, or unavailable" depending on the profile configuration (Ed-Fi Alliance OSS, 2024, line 29).

### 2.1. Income (Write/Update)

The Ed-Fi documentation explicitly states that when a profile with `WriteContentType` restrictions is applied to filtering operations on child collections, "the caller will receive an error response if they attempt to write anything other than" the allowed values (Ed-Fi Alliance, n.d.-a, Profile Definition section, para. 13). This establishes the validation behavior for write operations.

- **Purpose**: Restrict which fields/collections a client can submit when creating or updating resources.

- **Mechanism**: On POST/PUT requests, the DMS pipeline validates the incoming JSON payload against the active profile's `WriteContentType` rules. As documented in the Ed-Fi ODS/API, profiles can specify "writable only" resources using `WriteContentType` definitions (Ed-Fi Alliance, n.d.-a, Profile Definition section).

- **Validation Logic**:
  - Rejects any fields not allowed by the profile.
  - Ensures required fields (per profile) are present.
  - Validates collections and references per profile definition.
  - Returns an error response if validation fails (following Ed-Fi's documented behavior).

### 2.2. Outcome (Read/Query)

The Ed-Fi documentation provides clear guidance on read filtering behavior, stating that "`GET` requests will only return" the data elements specified in the profile's `ReadContentType` definition (Ed-Fi Alliance, n.d.-a, Profile Definition section, para. 13). Furthermore, profiles can specify "readable only" resources that control which fields are returned to API consumers (Ed-Fi Alliance, n.d.-a, Profile Definition section).

- **Purpose**: Filter the JSON payload returned to the client based on profile-defined access rules.

- **Mechanism**: On GET requests, the DMS pipeline applies the profile's `ReadContentType` rules as a projection engine. The Ed-Fi documentation demonstrates this with examples showing how profiles use `memberSelection` modes (`IncludeOnly` or `ExcludeOnly`) to control which properties, collections, and references appear in responses (Ed-Fi Alliance, n.d.-a, Profile Definition section).

- **Filtering Logic**:
  - Removes excluded fields, collections, and references from the response.
  - Applies collection item filters based on Type or Descriptor values (e.g., only Physical and Shipping addresses).
  - Maintains data structure integrity while preserving identity fields, as "resource members that are part of the identity are automatically included in the `GET` responses" even when not explicitly defined in the profile (Ed-Fi Alliance, n.d.-a, Profile Definition section, para. 14).

---

## 3. DMS Alignment

- **Storage-Agnostic**: Profiles are enforced in the middleware pipeline, not in the storage layer. No SQL or document-store specifics are required.

- **Cloud-Native**: Profile definitions are managed via a Config Service API, fetched by DMS at runtime, and can be cached for performance. The Ed-Fi ODS/API documentation notes that "the refresh period for dynamically defined profiles is controlled through the API configuration settings" (Ed-Fi Alliance, n.d.-a, para. 4).

- **Extensible**: Supports profile definitions in JSON format while maintaining semantic compatibility with XML-based Ed-Fi profile schemas.

---

## 4. Technical Specifications

### 4.1. `/applications` Endpoint Changes

- **Enhancement**: Applications can be assigned one or more profile IDs via the Config Service API (`/v2/applications`).

- **Behavior**: The Ed-Fi documentation explains that "the API evaluates each request to determine if the resource requested by the API consumer is covered by a single assigned Profile, and if so, it will implicitly process the request using that Profile" (Ed-Fi Alliance, n.d.-a, para. 6). However, when multiple profiles are assigned, the API consumer must explicitly specify which profile to use via HTTP headers.
  - If a client omits a profile header, DMS uses the application's assigned profile (if only one).
  - If multiple profiles are assigned, the client must specify which profile to use via HTTP Accept or Content-Type headers.
  - Profile assignment is managed via the `profileIds` array in the application object.

### 4.2. Internal Metadata Structure

- **Profile Storage**: Profiles are stored as JSONB (PostgreSQL) or JSON (SQL Server) in the Config Service database, analogous to how the Ed-Fi ODS/API stores profiles in the `EdFi_Admin` database (Ed-Fi Alliance, n.d.-b).

- **Profile Definition Example**:

  ```json
  {
    "profileName": "Student-Read-Only",
    "resources": [
      {
        "resourceName": "Student",
        "readContentType": {
          "memberSelection": "IncludeOnly",
          "properties": [
            {"name": "studentUniqueId"},
            {"name": "firstName"},
            {"name": "lastSurname"},
            {"name": "birthDate"}
          ]
        }
      }
    ]
  }
  ```

- **Application-Profile Assignment**: Managed via a junction table (`ApplicationProfile`) linking applications to profiles, similar to the `ProfileApplications` table in Ed-Fi ODS/API (Ed-Fi Alliance OSS, 2024, Profile.cs).

### 4.3. Profile Resolution

The Ed-Fi documentation specifies that API consumers must add appropriate HTTP headers to requests when multiple profiles are assigned or when explicitly choosing a particular profile: "the API consumer must specify which Profile is to be used by adding the appropriate HTTP header to the request (i.e. `Accept` for `GET` requests, and `Content-Type` for `PUT`/`POST` requests)" (Ed-Fi Alliance, n.d.-a, para. 7).

- **Priority**:
  1. Profile specified in HTTP header (Accept/Content-Type, using Ed-Fi vendor-specific media type).
     - **Example**: `Accept: application/vnd.ed-fi.student.Student-Read-Only.readable+json`
     - **Format**: Following the Ed-Fi standard, profile media types use the structure `application/vnd.{schema}.{resource}.{profile-name}.{readable|writable}+json` where clients specify `readable` for GET operations via the `Accept` header, or `writable` for POST/PUT operations via the `Content-Type` header (Ed-Fi Alliance, n.d.-c).
  2. Profile assigned to application (if only one).
  3. No profile (default: no filtering).

- **DMS fetches profile definitions from Config Service API** at request time (with optional caching). Caching behavior follows the pattern established in Ed-Fi ODS/API, where profile cache expiration can be configured (Ed-Fi Alliance, n.d.-b).

---

## 5. Input/Output Behavior Definitions

The Ed-Fi documentation emphasizes that "resource members that are part of the identity are automatically included in the `GET` responses and must be included in the `PUT` and `POST` request bodies" (Ed-Fi Alliance, n.d.-a, para. 15). Additionally, "if required fields are excluded, the profile cannot be used to create the resource (though updates would still be possible)" (Ed-Fi Alliance, n.d.-a, para. 16).

### 5.1. Write (Income)

- **Input**: JSON payload from client.
- **Process**: Validate against profile's `WriteContentType` rules.
- **Output**: Accept (store) or reject (HTTP 400 Bad Request) based on compliance.
- **Note**: Identity fields must always be included in POST and PUT requests, regardless of profile definition.

### 5.2. Read (Outcome)

- **Input**: Unfiltered data from document store.
- **Process**: Apply profile's `ReadContentType` rules to filter/projection engine.
- **Output**: Filtered JSON response to client.
- **Note**: Identity fields are automatically included in GET responses even if not explicitly defined in the profile.

### 5.3. HTTP Status Codes for Profile Operations

Following RESTful API conventions and Ed-Fi standards, DMS returns appropriate HTTP status codes for profile-related operations. The Ed-Fi ODS/API documentation specifies that when API clients with multiple assigned profiles fail to provide a valid profile header, "failing to provide a valid profile header will result in an error response" (Ed-Fi Alliance, n.d.-c, Write Operations section). Additionally, the Ed-Fi Error Response Knowledge Base documents specific profile-related error codes, including 400 Bad Request for invalid profile usage and 403 Forbidden for data policy failures (Ed-Fi Alliance, n.d.-d).

| Status Code | Scenario | Description | Ed-Fi Reference |
|-------------|----------|-------------|-----------------|
| **200 OK** | Successful GET | Profile filtering applied successfully; response contains data constrained by `ReadContentType` | Standard REST |
| **201 Created** | Successful POST | Resource created successfully; payload validated against profile's `WriteContentType` | Standard REST |
| **204 No Content** | Successful PUT/DELETE | Resource updated or deleted successfully | Standard REST |
| **400 Bad Request** | Invalid profile usage | PUT/POST request when read-only profile is in use, or profile method not supported (Ed-Fi Alliance, n.d.-d, urn:ed-fi:api:profile:invalid-profile-usage) | Ed-Fi Alliance, n.d.-d |
| **400 Bad Request** | Validation failure | Request payload violates profile's `WriteContentType` rules (e.g., excluded fields present, required fields missing, invalid collection items per filter rules) | Ed-Fi Alliance, n.d.-a |
| **403 Forbidden** | Data policy failure | Client attempts to use a profile-specific content type not assigned to it, or does not specify a required writable profile (Ed-Fi Alliance, n.d.-d, urn:ed-fi:api:security:data-policy:incorrect-usage) | Ed-Fi Alliance, n.d.-d |
| **406 Not Acceptable** | Invalid Accept header | Client specifies profile in `Accept` header that is not assigned to the application or does not cover the requested resource; applies to GET requests when multiple profiles are assigned and no valid header is provided | RESTful convention |
| **415 Unsupported Media Type** | Invalid Content-Type header | Client specifies profile in `Content-Type` header that is not assigned to the application or does not cover the requested resource; applies to POST/PUT requests (Ed-Fi Alliance, n.d.-d) | Ed-Fi Alliance, n.d.-d |

**Notes**:

- The Ed-Fi ODS/API implements RFC 9457 (Problem Details) for standardized error responses with machine-readable error details (Ed-Fi Alliance, n.d.-d).
- Standard Ed-Fi authorization errors (401 Unauthorized, 403 Forbidden) apply independently of profile enforcement.
- When a client is assigned multiple profiles covering the same resource, explicit profile headers are required; omitting valid headers will result in an error response (Ed-Fi Alliance, n.d.-c, Write Operations section). DMS returns 406 for GET requests with invalid Accept headers and 415 for POST/PUT requests with invalid Content-Type headers, following RESTful conventions.
- When a client is assigned a single profile, DMS auto-applies it if the client uses standard `application/json` headers, following Ed-Fi ODS/API behavior (Ed-Fi Alliance, n.d.-c, Write Operations section).

---

## 6. API Endpoints

| Endpoint | Type | Description |
|----------|------|-------------|
| `GET /v2/profiles` | New | Retrieve all profile definitions (JSON format) |
| `GET /v2/profiles/{profileId}` | New | Retrieve a specific profile definition by ID (JSON format) |
| `GET /v2/profiles/xml/{profileId}` | New | Retrieve a specific profile definition in Admin API 2.x XML format for backward compatibility |
| `POST /v2/profiles` | New | Create a new profile definition (JSON format) |
| `PUT /v2/profiles/{profileId}` | New | Update an existing profile definition |
| `DELETE /v2/profiles/{profileId}` | New | Delete a profile definition |
| `POST /v2/profiles/import/xml` | New | Import XML-based profile definition (Ed-Fi ODS/API format) for backward compatibility |
| `GET /v2/applications` | Updated | Retrieve applications; now includes `profileIds` array for profile assignments |
| `GET /v2/applications/{applicationId}` | Updated | Retrieve specific application; now includes `profileIds` array |
| `POST /v2/applications` | Updated | Create a new application with optional `profileIds` array for initial profile assignments |
| `PUT /v2/applications/{applicationId}` | Updated | Update application configuration; now supports `profileIds` array for assigning profiles to applications |

> **Note**: The `/v2/applications` endpoints are updated to include a `profileIds` array property, enabling profile assignment to applications. This supports the profile resolution logic where the API implicitly applies a profile when only one is assigned to an application, or requires explicit HTTP header specification when multiple profiles are assigned.

---

## 7. Task Breakdown

1. **Profile Enforcement Middleware**

- Implement middleware to resolve and enforce profiles in the DMS pipeline.

2. **Config Service API Enhancements**

- Add `profileIds` support to `/v2/applications` endpoints.
- Implement profile CRUD endpoints (`/v2/profiles/*`).
- Implement XML import/export endpoints for backward compatibility.
- Add validation endpoint for profile definitions.

3. **Profile Storage**

- Store profiles as JSONB/JSON in the Config Service database.
- Support XML import/export for compatibility.

4. **Profile Resolution Logic**

- Implement logic to resolve profile by header or application assignment.
- Handle ambiguous/missing profile scenarios.

5. **Validation Engine (Income)**

- Validate incoming payloads against profile writable rules.

6. **Projection Engine (Outcome)**

- Filter outgoing payloads per profile readable rules.

7. **Error Handling**

- Return appropriate HTTP status codes for profile errors (400, 406, 415, etc.).

8. **Caching (Phase 2)**

- Implement in-memory caching for profile definitions and assignments.

9. **Documentation & Examples**

- Provide admin/developer documentation and example profiles.

---

## References

Ed-Fi Alliance. (n.d.-a). *API profiles*. Ed-Fi Tech Docs. Retrieved December 17, 2024, from <https://docs.ed-fi.org/reference/ods-api/platform-dev-guide/security/api-profiles/>

Ed-Fi Alliance. (n.d.-b). *How to: Add profiles to the Ed-Fi ODS / API*. Ed-Fi Tech Docs. Retrieved December 17, 2024, from <https://docs.ed-fi.org/reference/ods-api/how-to-guides/how-to-add-profiles-to-the-ed-fi-ods-api>

Ed-Fi Alliance. (n.d.-c). *Authorization*. Ed-Fi Tech Docs. Retrieved December 17, 2024, from <https://docs.ed-fi.org/reference/ods-api/client-developers-guide/authorization#profile-media-type-format>

Ed-Fi Alliance. (n.d.-d). *Error response knowledge base*. Ed-Fi Tech Docs. Retrieved December 17, 2024, from <https://docs.ed-fi.org/reference/ods-api/client-developers-guide/error-response-knowledge-base>

Ed-Fi Alliance OSS. (2024). *Ed-Fi-ODS-API-Profiles.xsd* [XML Schema Definition]. GitHub. <https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Application/EdFi.Ods.Common/Metadata/Schemas/Ed-Fi-ODS-API-Profiles.xsd>

Ed-Fi Alliance OSS. (2024). *Profile.cs* [C# class file]. GitHub. <https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Application/EdFi.Admin.DataAccess/Models/Profile.cs>

Ed-Fi Alliance OSS. (2024). *Profiles sample template* [Source code]. GitHub. <https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/tree/v7.3/Samples/Project-Profiles-Template>
