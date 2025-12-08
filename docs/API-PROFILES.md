# API Profiles Design Document

## Problem Definition

The Data Management Service (DMS) needs to support **API Profiles** that define data policies for specific API Resources. These profiles enable scenarios where different consumers need different views of the same resource (e.g., Nutrition data, Special Education data) by allowing explicit inclusion or exclusion of properties, references, and collections.

### Key Requirements

1. **Dynamic Configuration**: Profiles must be configuration-driven, not hard-coded, allowing administrators to add/modify profiles without code changes
2. **XML Compatibility**: Use XML profile files compatible with AdminAPI-2.x format to avoid requiring end users to reformat existing profile documents
3. **Header-Based Selection**: Support profile selection via HTTP headers:
   - `Accept` header for GET requests (e.g., `application/vnd.ed-fi.student.test-profile.readable+json`)
   - `Content-Type` header for POST/PUT requests (e.g., `application/vnd.ed-fi.student.test-profile.writable+json`)
4. **Implicit Application**: When only one profile applies to a resource, apply it automatically without requiring explicit header specification
5. **Read/Write Policies**: Separate policies for read operations (GET) and write operations (POST/PUT)
6. **Filtering Semantics**: Support both `IncludeOnly` and `ExcludeOnly` member selection strategies

## Conceptual Model

### Profile Structure

A **Profile** is a named collection of resource-specific data policies. Each profile contains:

```
Profile
├── Name (e.g., "Student-Exclude-BirthDate")
└── Resources[]
    └── Resource
        ├── Name (e.g., "Student")
        ├── ReadContentType
        │   ├── MemberSelection ("IncludeOnly" | "ExcludeOnly")
        │   ├── Properties[]
        │   └── Collections[]
        └── WriteContentType
            ├── MemberSelection ("IncludeOnly" | "ExcludeOnly")
            ├── Properties[]
            └── Collections[]
```

### Content Type Rules

**ReadContentType** defines what data can be returned in GET responses:
- `IncludeOnly`: Only explicitly listed properties/collections are allowed in the response
- `ExcludeOnly`: All properties/collections except those listed are allowed in the response

**WriteContentType** defines what data can be submitted in POST/PUT requests:
- `IncludeOnly`: Only explicitly listed properties/collections are allowed in the request
- `ExcludeOnly`: All properties/collections except those listed are allowed in the request

### Member Selection Semantics

#### IncludeOnly Mode
- Start with an empty allowed set
- Add only the explicitly mentioned properties and collections
- Everything not mentioned is excluded

#### ExcludeOnly Mode
- Start with all resource members allowed
- Remove the explicitly mentioned properties and collections
- Everything not mentioned is included

### Collection Filtering

Collections can have their own member selection:
```xml
<Collection name="EducationOrganizationAddresses" memberSelection="IncludeAll"/>
```

Collections can also be filtered by type or descriptor values (future enhancement).

## Architecture

### Components

```
┌─────────────────────────────────────────────────────────────┐
│                    DMS Frontend Request                      │
│  (with Accept/Content-Type headers indicating profile)       │
└───────────────────┬─────────────────────────────────────────┘
                    │
                    ▼
┌─────────────────────────────────────────────────────────────┐
│              Profile Resolution Middleware                    │
│  - Parse headers to extract profile name                     │
│  - Resolve which profile applies to this resource            │
│  - Add profile info to RequestInfo                           │
└───────────────────┬─────────────────────────────────────────┘
                    │
                    ▼
┌─────────────────────────────────────────────────────────────┐
│         Standard DMS Pipeline Processing                      │
│  (validation, authorization, backend operations)              │
└───────────────────┬─────────────────────────────────────────┘
                    │
                    ▼
┌─────────────────────────────────────────────────────────────┐
│            Profile Application Middleware                     │
│  - For GET: Filter response body based on ReadContentType    │
│  - For POST/PUT: Filter request body based on WriteContentType│
│  - Apply IncludeOnly or ExcludeOnly semantics                │
└───────────────────┬─────────────────────────────────────────┘
                    │
                    ▼
┌─────────────────────────────────────────────────────────────┐
│                    Frontend Response                          │
└─────────────────────────────────────────────────────────────┘
```

### Core Services

#### 1. ProfileProvider (Singleton)
- Loads XML profile documents at startup from configured path
- Parses and validates XML against schema
- Builds internal profile models
- Provides profile lookup by name
- Supports hot-reload when profiles change (future enhancement)

#### 2. ProfileResolutionService (Scoped)
- Determines which profile applies to a given request
- Parses Accept and Content-Type headers
- Returns appropriate ContentType rules for filtering

#### 3. ProfileApplicationService (Scoped)
- Applies profile rules to JSON documents
- Implements IncludeOnly and ExcludeOnly filtering
- Handles nested properties and collections

### Middleware Integration

Two new middleware components integrate into the existing DMS pipeline:

**ProfileResolutionMiddleware** (Early in pipeline)
- Position: After ParsePathMiddleware, before body parsing
- Responsibility: Parse headers, identify applicable profile, add to RequestInfo

**ProfileApplicationMiddleware** (Late in pipeline)
- Position: For POST/PUT: After ParseBodyMiddleware, before validation
- Position: For GET: After backend response, before returning to client
- Responsibility: Apply filtering rules to request/response JSON

## XML Profile Format

### Schema
Profiles are defined in XML documents compatible with AdminAPI-2.x:

```xml
<Profile name="ProfileName">
  <Resource name="ResourceName">
    <ReadContentType memberSelection="IncludeOnly|ExcludeOnly">
      <Property name="PropertyName" />
      <Collection name="CollectionName" memberSelection="IncludeAll" />
    </ReadContentType>
    <WriteContentType memberSelection="IncludeOnly|ExcludeOnly">
      <Property name="PropertyName" />
      <Collection name="CollectionName" memberSelection="IncludeAll" />
    </WriteContentType>
  </Resource>
</Profile>
```

### Validation Rules
1. Profile name must be unique across all loaded profiles
2. Resource name must match Ed-Fi API resource names
3. MemberSelection must be either "IncludeOnly" or "ExcludeOnly"
4. Property names must reference actual properties in the resource schema
5. Collection names must reference actual collections in the resource schema

### Loading Mechanism
- Profiles are loaded from a configurable filesystem path
- All XML files matching the profile schema in the directory are loaded
- Invalid profiles are logged but don't prevent DMS startup
- Profile information is cached in memory for fast access

## Header-Based Profile Selection

### Media Type Format
Profiles use vendor-specific media types following Ed-Fi conventions:

**For GET requests (Accept header):**
```
application/vnd.ed-fi.{resource}.{profile-name}.readable+json
```
Example: `application/vnd.ed-fi.student.test-profile.readable+json`

**For POST/PUT requests (Content-Type header):**
```
application/vnd.ed-fi.{resource}.{profile-name}.writable+json
```
Example: `application/vnd.ed-fi.student.test-profile.writable+json`

### Selection Logic

1. **Explicit Profile Selection**: If request includes profile-specific media type, use that profile
2. **Single Profile Default**: If only one profile is defined for the resource, apply it automatically
3. **No Profile**: If no profile headers and multiple profiles exist, or no profiles defined, process without filtering
4. **Invalid Profile**: If specified profile doesn't exist or doesn't apply to the resource, return 406 Not Acceptable (GET) or 415 Unsupported Media Type (POST/PUT)

### Precedence Rules

For GET requests:
1. Check Accept header for profile media type
2. If found and valid, use specified profile
3. If not found and exactly one profile applies to resource, use that profile
4. Otherwise, no profile filtering

For POST/PUT requests:
1. Check Content-Type header for profile media type
2. If found and valid, use specified profile
3. If not found and exactly one profile applies to resource, use that profile
4. Otherwise, no profile filtering

## Integration with Existing DMS Features

### Overposting Protection
DMS already validates request bodies against JSON schemas to prevent overposting. Profile filtering happens **before** schema validation:

1. Parse request body
2. Apply profile WriteContentType filtering (remove disallowed properties)
3. Validate filtered body against JSON schema
4. Continue with standard processing

This ensures profiles work in harmony with existing security measures.

### Response Filtering
For GET requests, profile filtering happens **after** backend retrieval:

1. Backend returns full document
2. Apply profile ReadContentType filtering
3. Return filtered document to client

### Authorization
Profiles operate independently of authorization:
- Authorization determines **if** a client can access a resource
- Profiles determine **what data** the client sees within that resource

### JSON Schema Manipulation
Rather than filtering JSON documents after parsing, an alternative approach is to manipulate the JSON schema itself based on the active profile:

- For WriteContentType: Generate a modified schema with only allowed properties
- For ReadContentType: Generate a modified schema for response validation

This approach can be more efficient but requires deeper integration with the schema validation system. Initial implementation uses post-parsing filtering for simplicity.

## Configuration

### AppSettings Extensions
```json
{
  "AppSettings": {
    "EnableProfiles": true,
    "ProfilesPath": "/app/profiles"
  }
}
```

- `EnableProfiles`: Boolean flag to enable/disable profile feature globally
- `ProfilesPath`: Filesystem path where XML profile documents are stored

### Environment Variables
```bash
AppSettings__EnableProfiles=true
AppSettings__ProfilesPath=/app/profiles
```

## Error Handling

### Profile Loading Errors
- Invalid XML: Log error, skip profile, continue startup
- Invalid schema references: Log warning, skip profile, continue startup
- No profiles found: Log info message, continue without profiles

### Runtime Errors
- Profile not found: Return 406 Not Acceptable (GET) or 415 Unsupported Media Type (POST/PUT)
- Invalid header format: Ignore profile, process normally
- Profile filtering exception: Log error, return 500 Internal Server Error

## Security Considerations

1. **Path Traversal**: Validate ProfilesPath to prevent directory traversal attacks
2. **XML External Entity (XXE)**: Disable external entity processing in XML parser
3. **Resource Exhaustion**: Limit number of profiles and profile file sizes
4. **Privilege Escalation**: Profiles reduce data exposure, they don't grant additional permissions

## Future Enhancements (Out of Scope)

1. **Profile Management API**: REST endpoints to manage profiles dynamically
2. **Database Storage**: Store profiles in database instead of filesystem
3. **Type/Descriptor Filtering**: Filter collection items by type or descriptor values
4. **Profile Inheritance**: Support profile composition and inheritance
5. **Hot Reload**: Reload profiles without restarting DMS
6. **Performance Optimization**: Cache filtered schemas per profile
7. **Profile Testing Tools**: CLI utilities to test profile application

## Testing Strategy

### Unit Tests
1. **XML Parsing**: Validate correct parsing of all three sample XMLs
2. **Filtering Logic**: Test IncludeOnly and ExcludeOnly for properties and collections
3. **Header Parsing**: Test media type parsing and validation
4. **Profile Resolution**: Test single profile, multiple profiles, no profile scenarios

### Integration Tests
1. **End-to-End GET**: Request with profile, verify filtered response
2. **End-to-End POST**: Submit with profile, verify filtered request processing
3. **Header Validation**: Invalid profile names, malformed headers
4. **Configuration**: Enable/disable profiles, missing profile path

### Test Profiles
Use the three provided XML examples:
1. `Student-Exclude-BirthDate.xml`: Simple property exclusion
2. `Test-Profile-Resource-ExcludeOnly.xml`: ExcludeOnly with collections
3. `Test-Profile-Resource-IncludeOnly.xml`: IncludeOnly with collections

## Implementation Phases

### Phase 1: Foundation (This PR)
- Design document
- Core profile models
- XML loader and parser
- Basic configuration support

### Phase 2: Core Functionality (This PR)
- Profile resolution service
- Middleware components
- IncludeOnly/ExcludeOnly filtering
- Header parsing

### Phase 3: Testing & Validation (This PR)
- Unit tests for all components
- Integration tests for end-to-end scenarios
- Documentation updates

### Phase 4: Future Enhancements (Separate PRs)
- Type/descriptor filtering
- Management API
- Performance optimizations
- Hot reload support

## References

- AdminAPI-2.x Profile Implementation: https://github.com/Ed-Fi-Alliance-OSS/AdminAPI-2.x
- Ed-Fi API Profiles Documentation: https://docs.ed-fi.org/
- Project Tanager: https://github.com/Ed-Fi-Alliance-OSS/Project-Tanager
- DMS Architecture: See existing docs in `/docs` folder
