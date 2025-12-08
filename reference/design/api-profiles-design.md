# Ed-Fi API Profiles Design for Data Management Service

## Overview

This document defines the architectural design for implementing Ed-Fi API Profiles support in the Data Management Service (DMS). API Profiles provide a mechanism to constrain the shape of API resources (properties, references, collections, and collection items) for specific usage scenarios, enabling data policies that limit what data can be read or written for particular API clients or contexts.

## Problem Statement and Goals

### Background

The existing Ed-Fi ODS/API and AdminAPI-2.x platforms support API Profiles, which are defined in XML format. These profiles allow organizations to:

- Limit the data exposed to or accepted from specific API clients based on data governance policies
- Create "views" of resources that include only relevant fields for specific use cases
- Enforce data minimization principles by constraining API payloads
- Support multi-tenant scenarios where different consumers have different data access patterns

### Goals for DMS

1. **Compatibility**: Support existing AdminAPI-2.x Profile XML files without requiring reformatting
2. **Integration**: Integrate cleanly with DMS architecture, reusing existing JSON schema validation and overposting removal mechanisms
3. **Performance**: Minimize runtime overhead for profile application
4. **Flexibility**: Support dynamic profile configuration without requiring application redeployment
5. **Security**: Enforce least privilege access patterns with appropriate safeguards

### Non-Goals

- Creating a new profile definition format (we will use existing XML)
- Implementing profile-specific database queries or projections (focus on request/response shaping)
- Building a profile management UI (this is for post-2026 Admin App work)

## Conceptual Model

### Profile Structure

An API Profile consists of:

1. **Profile Metadata**
   - Profile name (unique identifier)
   - Description
   - Applicable resources

2. **Resource-Level Rules**
   - Which resources are included in the profile
   - Read/write permissions per resource

3. **Member-Level Rules**
   - Properties (scalar fields) - include/exclude
   - References (navigational references to other resources) - include/exclude
   - Collections (arrays of objects) - include/exclude
   - Collection items (specific members within collections) - include/exclude

4. **Descriptor-Based Filters** (optional)
   - Constrain collection items based on descriptor values
   - Example: Only include addresses where `addressTypeDescriptor` is "Home"

### Profile Application Model

Profiles can be applied in two modes:

1. **Implicit Application**: When exactly one profile is assigned to an API client for a given resource, it is automatically applied
2. **Explicit Selection**: When multiple profiles are available, the client specifies which profile to use via HTTP headers

### Example Use Cases

1. **Read-Only Student Demographics Profile**
   - Includes: Student identity, name, demographics
   - Excludes: Addresses, phone numbers, email addresses
   - Use case: Public-facing student directory

2. **Assessment Scores Only Profile**
   - Includes: StudentAssessment with scores only
   - Excludes: Accommodations, performance levels, student objectives
   - Use case: District reporting dashboard

3. **SIS Vendor Full Access Profile**
   - Includes: All properties, references, and collections
   - Use case: Student Information System integration

## XML Schema and AdminAPI-2.x Compatibility

### XML Profile Format

DMS will support the existing AdminAPI-2.x Profile XML format. The XSD schema defines:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Profile name="Test-Profile-Student-Read-Resource-IncludeOnly">
  <Resource name="Student">
    <ReadContentType memberSelection="IncludeOnly">
      <Property name="StudentUniqueId" />
      <Property name="FirstName" />
      <Property name="LastSurname" />
      <Property name="BirthDate" />
      <Property name="BirthSexDescriptor" />
    </ReadContentType>
  </Resource>
</Profile>
```

### Key XML Elements

1. **Profile**: Root element with `name` attribute
2. **Resource**: Defines rules for a specific Ed-Fi resource (matches resource name)
3. **ReadContentType**: Specifies members included/excluded for GET requests
4. **WriteContentType**: Specifies members included/excluded for POST/PUT requests
5. **memberSelection**: Attribute indicating "IncludeOnly" or "ExcludeOnly" strategy
6. **Property**: Scalar field reference (by JSON path or property name)
7. **Collection**: Array field reference
8. **Reference**: Navigational reference to another resource

### Profile XML Examples

#### Example 1: Include-Only Student Profile
```xml
<Profile name="Student-Demographics-Only">
  <Resource name="Student">
    <ReadContentType memberSelection="IncludeOnly">
      <Property name="StudentUniqueId" />
      <Property name="FirstName" />
      <Property name="MiddleName" />
      <Property name="LastSurname" />
      <Property name="BirthDate" />
      <Property name="BirthSexDescriptor" />
    </ReadContentType>
    <WriteContentType memberSelection="ExcludeAll" />
  </Resource>
</Profile>
```

#### Example 2: Exclude Collections Profile
```xml
<Profile name="School-Without-Addresses">
  <Resource name="School">
    <ReadContentType memberSelection="IncludeAll">
      <Collection name="addresses" memberSelection="ExcludeOnly" />
      <Collection name="institutionTelephones" memberSelection="ExcludeOnly" />
    </ReadContentType>
    <WriteContentType memberSelection="IncludeAll">
      <Collection name="addresses" memberSelection="ExcludeOnly" />
      <Collection name="institutionTelephones" memberSelection="ExcludeOnly" />
    </WriteContentType>
  </Resource>
</Profile>
```

#### Example 3: Filtered Collection Items
```xml
<Profile name="Student-Home-Address-Only">
  <Resource name="Student">
    <ReadContentType memberSelection="IncludeAll">
      <Collection name="addresses" memberSelection="IncludeAll">
        <Filter propertyName="addressTypeDescriptor" value="Home" />
      </Collection>
    </ReadContentType>
  </Resource>
</Profile>
```

### XSD Schema Reuse

DMS will reuse the AdminAPI-2.x Profile XSD schema with minimal or no modifications. The schema file will be:
- Included in the DMS repository under `src/dms/core/EdFi.DataManagementService.Core/Profiles/Schema/`
- Used for XML validation at profile load time
- Referenced in documentation for profile authors

## Profile Discovery, Loading, and Validation

### Discovery Mechanism

Profiles will be discovered using one of two configurable approaches:

#### Option 1: File-Based Profiles (Primary)
```
/profiles/
  /core/
    Student-Demographics-Only.xml
    School-Basic-Info.xml
  /tenant-001/
    Custom-Profile.xml
```

Configuration in `appsettings.json`:
```json
{
  "AppSettings": {
    "Profiles": {
      "Source": "FileSystem",
      "ProfilesPath": "/app/profiles",
      "EnableProfileWatcher": true,
      "WatcherPollingIntervalSeconds": 60
    }
  }
}
```

#### Option 2: Database-Based Profiles (Future)
- Profiles stored in the Configuration Service database
- Retrieved via Configuration Service API
- Cached with TTL-based invalidation
- Allows for centralized profile management across multiple DMS instances

### Loading Strategy

1. **Startup Loading**
   - All profiles are loaded and parsed at application startup
   - Invalid profiles log errors but don't prevent application startup
   - Profile metadata is cached in memory

2. **Dynamic Reloading** (File-Based)
   - File system watcher detects changes to profile XML files
   - Modified/added profiles are reloaded and revalidated
   - Deleted profiles are removed from the cache
   - Profile reload uses versioned cache invalidation to avoid race conditions

3. **Cache Invalidation** (Database-Based)
   - Profiles cached with configurable TTL (default: 15 minutes)
   - Manual cache refresh endpoint: `POST /v2/profiles/refresh` (admin-only)
   - Configuration Service can push invalidation notifications

### Validation Process

Profiles are validated at load time:

1. **XML Schema Validation**: Validate against Profile XSD
2. **Resource Name Validation**: Ensure referenced resources exist in loaded ApiSchema
3. **Property/Collection Name Validation**: Verify that member names exist on the resource
4. **Descriptor Value Validation**: Check that descriptor URIs are valid (warning only)
5. **Logical Consistency**: Verify that include/exclude rules are consistent

Validation errors:
- **Critical errors** (malformed XML, missing resources): Profile is not loaded
- **Warnings** (unknown properties, deprecated descriptors): Profile is loaded with warnings logged

### Profile Versioning

Profiles are associated with ApiSchema versions:
- Profile definitions can specify compatible ApiSchema versions (optional)
- Profiles without version constraints are assumed compatible with all loaded schemas
- At runtime, profiles are matched to resources based on the current ApiSchema version

## Profile Selection and HTTP Behavior

### Profile-to-Client Assignment

Profiles are assigned to API clients (applications) through the Configuration Service:

```json
{
  "applicationId": 123,
  "clientId": "vendor-app-123",
  "profiles": [
    {
      "profileName": "Student-Demographics-Only",
      "resources": ["Student"]
    },
    {
      "profileName": "School-Basic-Info",
      "resources": ["School"]
    }
  ]
}
```

### HTTP Content Negotiation

Profiles are selected using standard HTTP content negotiation headers:

#### GET Requests (Read Operations)
Use the `Accept` header with a `profile` parameter:

```http
GET /data/v3/ed-fi/students/{id}
Accept: application/json;profile="Student-Demographics-Only"
```

#### POST/PUT Requests (Write Operations)
Use the `Content-Type` header with a `profile` parameter:

```http
POST /data/v3/ed-fi/students
Content-Type: application/json;profile="Student-Write-Basic"

{
  "studentUniqueId": "12345",
  ...
}
```

### Profile Selection Rules

1. **No Profile Specified**
   - If exactly one profile is assigned to the client for the resource: use it (implicit)
   - If zero profiles are assigned: no profile applied (full resource access)
   - If multiple profiles are assigned: return HTTP 406 (Not Acceptable) with error message

2. **Profile Specified in Header**
   - If the named profile is assigned to the client: use it
   - If the named profile is not assigned or doesn't exist: return HTTP 400 (Bad Request)

3. **Invalid Profile Name Format**
   - Return HTTP 400 (Bad Request) with descriptive error

### Error Responses

#### Multiple Profiles Available (No Selection)
```http
HTTP/1.1 406 Not Acceptable
Content-Type: application/json

{
  "error": "MultipleProfilesAvailable",
  "message": "Multiple profiles are available for this resource. Please specify a profile using the Accept or Content-Type header.",
  "availableProfiles": ["Student-Demographics-Only", "Student-Full-Access"]
}
```

#### Invalid Profile Name
```http
HTTP/1.1 400 Bad Request
Content-Type: application/json

{
  "error": "InvalidProfileName",
  "message": "The profile 'Unknown-Profile' is not assigned to this client or does not exist.",
  "availableProfiles": ["Student-Demographics-Only"]
}
```

#### Profile Parse Error (Header Malformed)
```http
HTTP/1.1 400 Bad Request
Content-Type: application/json

{
  "error": "InvalidHeaderFormat",
  "message": "The Accept/Content-Type header contains an invalid profile parameter format."
}
```

## Runtime Request Handling Strategy

### Overview

Profiles will be applied by manipulating the JSON schema used for validation and by adding a response transformation layer. This approach reuses existing DMS infrastructure rather than creating bespoke shaping logic.

### Architecture Integration Points

1. **Middleware Pipeline**: New `ProfileApplicationMiddleware` inserted after `ParsePathMiddleware` and before `ValidateDocumentMiddleware`
2. **Schema Manipulation**: Profile rules transform the JSON schema before validation
3. **Response Transformation**: Profile rules filter response payloads after backend retrieval

### Request Flow (POST/PUT with Profile)

```
1. ParsePathMiddleware
   ↓
2. JwtAuthenticationMiddleware
   ↓
3. ProfileSelectionMiddleware
   - Determine effective profile from headers and client configuration
   - Store profile in RequestInfo
   ↓
4. ProfileApplicationMiddleware (NEW)
   - Modify ResourceSchema JSON schema based on profile write rules
   - Remove excluded properties/collections from schema
   - This causes overposted excluded data to be rejected by existing validation
   ↓
5. ValidateDocumentMiddleware
   - Existing validation, but uses profile-modified schema
   ↓
6. Backend (Upsert)
   ↓
7. Response
```

### Response Flow (GET with Profile)

```
1. ParsePathMiddleware
   ↓
2. JwtAuthenticationMiddleware
   ↓
3. ProfileSelectionMiddleware
   - Determine effective profile from Accept header
   - Store profile in RequestInfo
   ↓
4. Backend (Query)
   ↓
5. ProfileResponseTransformationMiddleware (NEW)
   - Filter response payload based on profile read rules
   - Remove excluded properties/collections from response JSON
   ↓
6. Response
```

### Profile-Based Schema Transformation

The `ProfileApplicationMiddleware` transforms the JSON schema based on profile rules:

#### Include-Only Strategy
```csharp
// Original schema has all properties
// Profile specifies IncludeOnly with specific properties

var transformedSchema = originalSchema.DeepClone();
var properties = transformedSchema["properties"] as JsonObject;

// Remove all properties not in the profile's include list
foreach (var propertyName in properties.Keys.ToList())
{
    if (!profile.IncludedProperties.Contains(propertyName))
    {
        properties.Remove(propertyName);
    }
}

// Update required fields to only include those in the include list
var required = transformedSchema["required"] as JsonArray;
var newRequired = required.Where(r => profile.IncludedProperties.Contains(r)).ToArray();
transformedSchema["required"] = new JsonArray(newRequired);
```

#### Exclude-Only Strategy
```csharp
// Original schema has all properties
// Profile specifies ExcludeOnly with specific properties to remove

var transformedSchema = originalSchema.DeepClone();
var properties = transformedSchema["properties"] as JsonObject;

// Remove properties in the profile's exclude list
foreach (var propertyName in profile.ExcludedProperties)
{
    properties.Remove(propertyName);
}

// Update required fields to remove excluded properties
var required = transformedSchema["required"] as JsonArray;
var newRequired = required.Where(r => !profile.ExcludedProperties.Contains(r)).ToArray();
transformedSchema["required"] = new JsonArray(newRequired);
```

### Profile-Based Response Transformation

The `ProfileResponseTransformationMiddleware` filters the response JSON:

```csharp
public class ProfileResponseTransformationMiddleware : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        await next();

        // Only transform successful GET responses with an active profile
        if (requestInfo.Method == RequestMethod.GET &&
            requestInfo.FrontendResponse is SuccessResponse successResponse &&
            requestInfo.ActiveProfile != null)
        {
            var transformedBody = TransformResponseBody(
                successResponse.Body,
                requestInfo.ActiveProfile,
                requestInfo.ResourceSchema
            );

            requestInfo.FrontendResponse = new SuccessResponse(
                transformedBody,
                successResponse.StatusCode,
                successResponse.Headers,
                successResponse.LocationHeaderPath
            );
        }
    }

    private JsonNode TransformResponseBody(
        JsonNode body,
        ProfileDefinition profile,
        ResourceSchema resourceSchema)
    {
        // Handle both single item and array responses
        if (body is JsonArray array)
        {
            return new JsonArray(array.Select(item =>
                ApplyProfileRules(item, profile)).ToArray());
        }
        else
        {
            return ApplyProfileRules(body, profile);
        }
    }

    private JsonNode ApplyProfileRules(JsonNode item, ProfileDefinition profile)
    {
        var transformed = item.DeepClone();

        // Remove excluded properties
        foreach (var excludedProp in profile.ExcludedProperties)
        {
            transformed[excludedProp]?.Parent?.Remove();
        }

        // Remove excluded collections
        foreach (var excludedCollection in profile.ExcludedCollections)
        {
            transformed[excludedCollection]?.Parent?.Remove();
        }

        // Filter collection items based on descriptor filters
        foreach (var collectionFilter in profile.CollectionFilters)
        {
            ApplyCollectionFilter(transformed, collectionFilter);
        }

        // Only include specified properties if IncludeOnly mode
        if (profile.MemberSelection == MemberSelection.IncludeOnly)
        {
            var propsToKeep = profile.IncludedProperties.ToHashSet();
            var allProps = transformed.AsObject().ToList();
            foreach (var prop in allProps)
            {
                if (!propsToKeep.Contains(prop.Key))
                {
                    transformed[prop.Key]?.Parent?.Remove();
                }
            }
        }

        return transformed;
    }

    private void ApplyCollectionFilter(JsonNode node, CollectionFilterRule filter)
    {
        var collection = node[filter.CollectionName]?.AsArray();
        if (collection == null) return;

        // Filter items based on descriptor value
        var filteredItems = collection
            .Where(item => item[filter.PropertyName]?.GetValue<string>()
                .EndsWith($"#{filter.Value}", StringComparison.OrdinalIgnoreCase) ?? false)
            .ToList();

        collection.Clear();
        foreach (var item in filteredItems)
        {
            collection.Add(item);
        }
    }
}
```

### Schema Cache Considerations

Profile-modified schemas are cached separately from base schemas:

```csharp
public interface ICompiledSchemaCache
{
    JsonSchema GetOrAdd(
        string projectName,
        string resourceName,
        RequestMethod method,
        Guid apiSchemaReloadId,
        string? profileName, // NEW: profile-specific cache key
        Func<JsonSchema> schemaFactory
    );
}
```

Cache key format: `{projectName}:{resourceName}:{method}:{apiSchemaReloadId}:{profileName}`

## Dynamic Configuration

### File-Based Configuration (Recommended)

Profiles are stored as XML files in a mounted volume:

```yaml
# docker-compose.yml
services:
  dms:
    volumes:
      - ./profiles:/app/profiles:ro
```

Profiles are organized by tenant or environment:

```
/app/profiles/
  ├── global/
  │   ├── Student-Demographics.xml
  │   └── School-Basic.xml
  ├── tenant-district-001/
  │   ├── Custom-Assessment.xml
  │   └── Custom-Attendance.xml
  └── tenant-charter-network/
      └── Limited-Access.xml
```

### Profile Assignment via Configuration Service

Profile-to-client assignments are managed through the Configuration Service API:

```http
POST /v2/applications/{applicationId}/profiles
Content-Type: application/json

{
  "profileName": "Student-Demographics-Only",
  "resources": ["Student", "StudentSchoolAssociation"]
}
```

The Configuration Service stores these assignments and DMS retrieves them when resolving client authorizations.

### Multi-Tenancy Support

Profiles can be scoped by tenant:

1. **Global Profiles**: Available to all tenants (in `/profiles/global/`)
2. **Tenant-Specific Profiles**: Only available within a specific tenant context

Profile resolution logic:
```
1. Check for profile in tenant-specific directory
2. If not found, check global directory
3. If not found, return error
```

### Configuration Reload

Administrators can trigger a profile reload without restarting DMS:

```http
POST /v2/profiles/reload
Authorization: Bearer {admin-token}
```

This endpoint:
- Rescans the profile directories
- Reloads and revalidates all profile XML files
- Updates the in-memory profile cache
- Returns a summary of loaded/failed profiles

## Security Considerations

### Least Privilege Access

Profiles enforce least privilege by:
- Limiting readable data to only what is necessary for the client's function
- Preventing writes to sensitive fields
- Filtering collections to remove unnecessary detail

### Denial-of-Service Prevention

Mitigations for DoS risks:

1. **Profile Complexity Limits**
   - Maximum number of profiles per client: 100
   - Maximum profile size: 1MB XML
   - Maximum nesting depth for collection filters: 3 levels

2. **Schema Compilation Caching**
   - Profile-modified schemas are compiled once and cached
   - Reduces CPU overhead for repeated requests

3. **Response Transformation Optimization**
   - Transformation is performed in-memory on parsed JSON (not string manipulation)
   - Early exit if no profile rules apply to the resource

### Misconfiguration Safety

Safeguards against misconfiguration:

1. **Validation at Load Time**: Invalid profiles are rejected with detailed error messages
2. **Fail-Open for GET**: If profile transformation fails, the full resource is returned (logged as error)
3. **Fail-Closed for POST/PUT**: If profile transformation fails, the request is rejected (HTTP 500)
4. **Profile Isolation**: A bad profile for one resource doesn't affect other resources

### Logging and Auditing

Profile usage is logged for audit purposes:

```json
{
  "timestamp": "2025-12-08T23:19:26Z",
  "level": "INFO",
  "message": "Profile applied to request",
  "clientId": "vendor-app-123",
  "profileName": "Student-Demographics-Only",
  "resource": "Student",
  "method": "GET",
  "propertiesExcluded": 15,
  "collectionsFiltered": 2,
  "requestId": "abc-123-def"
}
```

Failed profile applications are logged as warnings or errors:
```json
{
  "timestamp": "2025-12-08T23:19:26Z",
  "level": "ERROR",
  "message": "Failed to apply profile to request",
  "clientId": "vendor-app-123",
  "profileName": "Invalid-Profile",
  "resource": "Student",
  "error": "Profile references unknown property 'invalidField'",
  "requestId": "abc-123-def"
}
```

### Authorization Integration

Profiles work in conjunction with existing DMS authorization:

1. **Authentication First**: JWT/OAuth token validation happens before profile selection
2. **Authorization Second**: Claim set permissions are evaluated before profile application
3. **Profile Third**: Profile rules further constrain the allowed operations

Example flow:
```
1. Client authenticates → JWT token with claim set
2. Claim set says: Client can read/write Student resource
3. Profile says: Client can only read specific Student properties
4. Result: Client can read those specific properties only
```

## Implementation Work Breakdown

The implementation is divided into the following tasks:

### Phase 1: Foundation (Core Infrastructure)
1. **Profile Model and XML Parsing**
   - Create internal model classes for profiles
   - Implement XML deserializer with XSD validation
   - Unit tests for profile parsing

2. **Profile Repository and Caching**
   - File-based profile discovery and loading
   - In-memory profile cache with versioning
   - File watcher for dynamic reload
   - Unit tests for profile repository

3. **Profile Selection Logic**
   - HTTP header parsing (Accept and Content-Type)
   - Profile-to-client resolution via Configuration Service
   - Profile selection middleware
   - Unit tests for selection logic

### Phase 2: Schema Transformation (Write Path)
4. **Profile-Based Schema Transformation**
   - JSON schema manipulation based on profile rules
   - Schema cache integration with profile keys
   - Unit tests for schema transformation

5. **Request Validation with Profiles**
   - Integration with existing DocumentValidator
   - Profile application middleware for POST/PUT
   - Integration tests with sample profiles

### Phase 3: Response Transformation (Read Path)
6. **Profile-Based Response Filtering**
   - JSON response transformation based on profile rules
   - Collection filtering by descriptor values
   - Response transformation middleware for GET
   - Unit tests for response transformation

7. **GET Query Integration**
   - Handle profile application for GET by ID
   - Handle profile application for GET by query (collections)
   - Integration tests for GET operations

### Phase 4: Configuration and Management
8. **Configuration Service Integration**
   - Profile-to-client assignment API
   - Profile metadata retrieval
   - Cache invalidation coordination

9. **Administrative Endpoints**
   - Profile reload endpoint
   - Profile listing/validation endpoints
   - Admin documentation

### Phase 5: Testing and Documentation
10. **End-to-End Testing**
    - Integration tests with full request/response cycle
    - Multi-profile scenarios
    - Error condition testing
    - Performance testing

11. **Documentation**
    - Developer guide: How profiles work internally
    - Operator guide: How to configure and manage profiles
    - API consumer guide: How to use profiles in API calls
    - Sample profile XML files
    - Migration guide from AdminAPI-2.x

## Testing Strategy

### Unit Tests
- Profile XML parsing and validation
- Schema transformation logic
- Response filtering logic
- Header parsing and profile selection

### Integration Tests
- End-to-end profile application for POST/PUT/GET
- Multiple profiles per client
- Profile reload scenarios
- Error conditions (invalid profiles, missing profiles, etc.)

### Performance Tests
- Profile application overhead measurement
- Schema compilation caching effectiveness
- Response transformation performance with large payloads
- Concurrent request handling with profiles

### Sample Profiles for Testing
1. **Student-Demographics-Only**: Read-only demographics, no PII
2. **School-Basic-Info**: School without addresses/contacts
3. **Assessment-Scores-Only**: StudentAssessment with filtered collections
4. **Full-Access**: No restrictions (baseline)

## Developer Documentation

### Adding a New Profile

1. Create XML file following the AdminAPI-2.x schema:
```xml
<Profile name="My-Custom-Profile">
  <Resource name="Student">
    <ReadContentType memberSelection="IncludeOnly">
      <Property name="StudentUniqueId" />
      <Property name="FirstName" />
    </ReadContentType>
  </Resource>
</Profile>
```

2. Place file in the profiles directory:
```bash
cp My-Custom-Profile.xml /app/profiles/global/
```

3. Trigger reload (or wait for auto-reload):
```bash
curl -X POST http://localhost:8080/v2/profiles/reload \
  -H "Authorization: Bearer {admin-token}"
```

4. Assign profile to client via Configuration Service:
```bash
curl -X POST http://localhost:8081/v2/applications/{appId}/profiles \
  -H "Content-Type: application/json" \
  -d '{"profileName": "My-Custom-Profile", "resources": ["Student"]}'
```

### Using Profiles in API Calls

#### Reading with a Profile
```bash
curl -X GET http://localhost:8080/data/v3/ed-fi/students/{id} \
  -H "Accept: application/json;profile=\"Student-Demographics-Only\"" \
  -H "Authorization: Bearer {token}"
```

#### Writing with a Profile
```bash
curl -X POST http://localhost:8080/data/v3/ed-fi/students \
  -H "Content-Type: application/json;profile=\"Student-Write-Basic\"" \
  -H "Authorization: Bearer {token}" \
  -d @student-payload.json
```

## Operator Documentation

### Profile Directory Structure

```
/app/profiles/
  ├── global/               # Available to all tenants
  │   ├── *.xml
  ├── {tenant-id}/          # Tenant-specific profiles
  │   ├── *.xml
```

### Configuration Options

```json
{
  "AppSettings": {
    "Profiles": {
      "Source": "FileSystem",
      "ProfilesPath": "/app/profiles",
      "EnableProfileWatcher": true,
      "WatcherPollingIntervalSeconds": 60,
      "MaxProfileSizeBytes": 1048576,
      "MaxProfilesPerClient": 100
    }
  }
}
```

### Monitoring Profile Health

Check profile load status:
```bash
curl http://localhost:8080/v2/profiles/status \
  -H "Authorization: Bearer {admin-token}"
```

Response:
```json
{
  "totalProfiles": 25,
  "loadedProfiles": 23,
  "failedProfiles": 2,
  "lastReloadTime": "2025-12-08T23:19:26Z",
  "errors": [
    {
      "profileName": "Invalid-Profile.xml",
      "error": "XML schema validation failed: Element 'Resoure' not recognized"
    }
  ]
}
```

### Troubleshooting

**Problem**: Profile not being applied to requests

**Solutions**:
1. Check profile is loaded: `GET /v2/profiles/status`
2. Verify profile is assigned to client in Configuration Service
3. Check header format: `Accept: application/json;profile="ProfileName"`
4. Review DMS logs for profile-related errors

**Problem**: Profile validation errors at startup

**Solutions**:
1. Validate XML against XSD schema offline
2. Check resource names match ApiSchema resources (case-sensitive)
3. Check property/collection names match resource schema
4. Review detailed error in startup logs

## Open Questions and Future Enhancements

### Open Questions
1. Should profile reload be automatic (file watcher) or manual only?
2. Should failed profiles prevent startup, or just log warnings?
3. Should profile-based query optimization be considered (e.g., database projections)?

### Future Enhancements
1. **Profile Composition**: Ability to combine multiple profiles
2. **Profile Inheritance**: Base profiles that can be extended
3. **Dynamic Profile Rules**: Profiles with conditional logic (if descriptor=X, exclude field Y)
4. **Profile Analytics**: Track which profiles are most used, which fields are most often excluded
5. **Profile UI in Admin App**: Graphical profile editor for post-2026
6. **Profile Validation Service**: Standalone tool to validate profile XML before deployment

## References

- AdminAPI-2.x Profiles: https://github.com/Ed-Fi-Alliance-OSS/AdminAPI-2.x
- Project Tanager: https://github.com/Ed-Fi-Alliance-OSS/Project-Tanager
- Ed-Fi API Guidelines: https://docs.ed-fi.org/
- Ed-Fi ODS/API Documentation: https://edfi.atlassian.net/wiki/spaces/ODSAPIS3V61/pages/24117479/Profiles

## Appendix A: Profile XML Schema (XSD)

The full XSD schema will be sourced from AdminAPI-2.x and included in:
`src/dms/core/EdFi.DataManagementService.Core/Profiles/Schema/ProfileSchema.xsd`

Key schema elements:
- Profile (root)
- Resource
- ReadContentType
- WriteContentType
- Property
- Collection
- Reference
- Filter

## Appendix B: Sample Profile Files

Sample profiles will be included in the repository under:
`reference/examples/profiles/`

1. `Student-Demographics-Only.xml`
2. `School-Basic-Info.xml`
3. `Assessment-Scores-Only.xml`
4. `Full-Access-Baseline.xml`

These samples will be used for integration testing and as templates for operators.
