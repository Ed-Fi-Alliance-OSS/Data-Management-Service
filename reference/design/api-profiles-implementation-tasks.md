# API Profiles Implementation Tasks

This document outlines the GitHub issues to be created for implementing Ed-Fi API Profiles support in the Data Management Service.

## Epic Issue

**Title**: Implement Ed-Fi API Profiles Support in DMS

**Description**:
Implement support for Ed-Fi API Profiles to enable data policy enforcement through XML-defined resource constraints. Profiles constrain the shape of API resources (properties, references, collections, and collection items) for specific usage scenarios.

**Goals**:
- Support existing AdminAPI-2.x Profile XML format without requiring reformatting
- Integrate cleanly with DMS architecture (JSON schema validation, overposting removal)
- Enable dynamic profile configuration without application redeployment
- Provide secure, performant profile application

**Related Documentation**:
- Design Document: `reference/design/api-profiles-design.md`
- Sample Profiles: `reference/examples/profiles/`

**Implementation Tasks**: (Listed below)

---

## Phase 1: Foundation (Core Infrastructure)

### Task 1: Profile Model and XML Parsing

**Title**: Create Profile Model and XML Parser

**Labels**: enhancement, profiles, phase-1

**Description**:
Create the internal model classes for representing profiles and implement XML deserialization with XSD validation.

**Acceptance Criteria**:
- [ ] Internal model classes created:
  - `ProfileDefinition` (top-level profile)
  - `ResourceRule` (rules for a specific resource)
  - `ContentTypeRule` (read/write content type rules)
  - `MemberRule` (property/collection/reference rules)
  - `CollectionFilterRule` (descriptor-based filters)
- [ ] XML deserializer implemented using System.Xml.Serialization or similar
- [ ] XSD schema validation integrated
- [ ] Support for all member selection strategies (IncludeAll, IncludeOnly, ExcludeOnly, ExcludeAll)
- [ ] Support for collection filtering by descriptor values
- [ ] Unit tests covering:
  - Valid profile XML parsing
  - Invalid XML rejection
  - Schema validation errors
  - All profile patterns from sample files

**Files to Create**:
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/Model/ProfileDefinition.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/Model/ResourceRule.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/Model/ContentTypeRule.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/Model/MemberRule.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/Parsing/ProfileXmlParser.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/Parsing/IProfileXmlParser.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/Schema/ProfileSchema.xsd` (from AdminAPI-2.x)

**Dependencies**: None

---

### Task 2: Profile Repository and Caching

**Title**: Implement Profile Repository with File-Based Discovery and Caching

**Labels**: enhancement, profiles, phase-1

**Description**:
Implement the profile repository for loading, caching, and managing profiles from the file system.

**Acceptance Criteria**:
- [ ] `IProfileRepository` interface defined with methods:
  - `GetProfile(string profileName)` → returns ProfileDefinition or null
  - `GetProfilesForResource(string resourceName)` → returns list of profiles
  - `ReloadProfiles()` → reloads all profiles from disk
  - `GetLoadStatus()` → returns profile load statistics
- [ ] File-based profile discovery implementation:
  - Scans configured directory for *.xml files
  - Supports subdirectory organization (global, tenant-specific)
  - Handles missing directories gracefully
- [ ] In-memory caching with versioned invalidation
- [ ] File system watcher for dynamic reload (optional, configurable)
- [ ] Profile validation at load time:
  - XML schema validation
  - Resource name validation against ApiSchema
  - Property/collection name validation
  - Logical consistency checks
- [ ] Configuration support in appsettings.json:
  - `Profiles:Source` (FileSystem, Database - FileSystem only for now)
  - `Profiles:ProfilesPath` (default: /app/profiles)
  - `Profiles:EnableProfileWatcher` (default: true)
  - `Profiles:WatcherPollingIntervalSeconds` (default: 60)
- [ ] Unit tests covering:
  - Profile loading and caching
  - Validation logic
  - File watcher behavior
  - Error handling (malformed XML, missing files)

**Files to Create**:
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/IProfileRepository.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/FileBasedProfileRepository.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/ProfileLoadStatus.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Configuration/ProfilesSettings.cs`

**Dependencies**: Task 1 (Profile Model and XML Parsing)

---

### Task 3: Profile Selection Logic and HTTP Header Parsing

**Title**: Implement Profile Selection Logic and HTTP Content Negotiation

**Labels**: enhancement, profiles, phase-1

**Description**:
Implement the logic for selecting the appropriate profile based on HTTP headers and client configuration.

**Acceptance Criteria**:
- [ ] HTTP header parsing for profile parameter:
  - Parse `Accept: application/json;profile="ProfileName"` for GET
  - Parse `Content-Type: application/json;profile="ProfileName"` for POST/PUT
  - Handle malformed headers gracefully
- [ ] Profile selection logic:
  - If no profile specified and exactly one assigned: use it (implicit)
  - If no profile specified and zero assigned: no profile applied
  - If no profile specified and multiple assigned: return HTTP 406
  - If profile specified: validate it's assigned to client
- [ ] `ProfileSelectionMiddleware` implementation:
  - Runs after `JwtAuthenticationMiddleware`
  - Determines effective profile and stores in `RequestInfo`
  - Returns appropriate error responses for invalid scenarios
- [ ] Add `ActiveProfile` property to `RequestInfo`
- [ ] Integration with Configuration Service for profile-to-client assignments
- [ ] Unit tests covering:
  - Header parsing (valid and invalid formats)
  - Profile selection rules
  - Error responses (406, 400)
  - Integration with RequestInfo

**Files to Create**:
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/ProfileHeaderParser.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/IProfileHeaderParser.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ProfileSelectionMiddleware.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/ProfileSelectionResult.cs`

**Files to Modify**:
- `src/dms/core/EdFi.DataManagementService.Core/Pipeline/RequestInfo.cs` (add ActiveProfile property)

**Dependencies**: Task 2 (Profile Repository and Caching)

---

## Phase 2: Schema Transformation (Write Path)

### Task 4: Profile-Based Schema Transformation

**Title**: Implement JSON Schema Transformation Based on Profile Rules

**Labels**: enhancement, profiles, phase-2

**Description**:
Implement the logic for transforming JSON schemas based on profile write rules, enabling profile-aware validation.

**Acceptance Criteria**:
- [ ] `IProfileSchemaTransformer` interface defined
- [ ] Schema transformation implementation:
  - Transform schema based on `IncludeOnly` strategy (remove non-included properties)
  - Transform schema based on `ExcludeOnly` strategy (remove excluded properties)
  - Handle collection exclusion (remove collection from schema)
  - Update `required` array to match included properties
  - Preserve schema structure and metadata
- [ ] Integration with `ICompiledSchemaCache`:
  - Update cache key to include profile name
  - Cache profile-transformed schemas separately
- [ ] Unit tests covering:
  - IncludeOnly transformation
  - ExcludeOnly transformation
  - Collection exclusion
  - Required field updates
  - Schema structure preservation

**Files to Create**:
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/IProfileSchemaTransformer.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/ProfileSchemaTransformer.cs`

**Files to Modify**:
- `src/dms/core/EdFi.DataManagementService.Core/Validation/ICompiledSchemaCache.cs` (add profileName parameter)
- `src/dms/core/EdFi.DataManagementService.Core/Validation/CompiledSchemaCache.cs` (update cache key)

**Dependencies**: Task 3 (Profile Selection Logic)

---

### Task 5: Request Validation with Profiles

**Title**: Integrate Profile Schema Transformation with Request Validation

**Labels**: enhancement, profiles, phase-2

**Description**:
Integrate profile-based schema transformation into the request validation pipeline for POST/PUT operations.

**Acceptance Criteria**:
- [ ] `ProfileApplicationMiddleware` implementation:
  - Runs after `ProfileSelectionMiddleware` and before `ValidateDocumentMiddleware`
  - Applies profile write rules by transforming the JSON schema
  - Stores transformed schema for use by validation
- [ ] Integration with `DocumentValidator`:
  - Use profile-transformed schema if profile is active
  - Fall back to base schema if no profile
- [ ] Error handling:
  - Log profile application errors
  - Return HTTP 500 if profile transformation fails critically
- [ ] Integration tests with sample profiles:
  - POST with IncludeOnly profile
  - POST with ExcludeOnly profile
  - PUT with profile
  - Verify overposted data is rejected per profile rules
  - Verify required fields are enforced per profile

**Files to Create**:
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ProfileApplicationMiddleware.cs`

**Files to Modify**:
- `src/dms/core/EdFi.DataManagementService.Core/Validation/DocumentValidator.cs` (use profile-transformed schema)
- Pipeline registration in frontend (add ProfileApplicationMiddleware)

**Dependencies**: Task 4 (Profile-Based Schema Transformation)

---

## Phase 3: Response Transformation (Read Path)

### Task 6: Profile-Based Response Filtering

**Title**: Implement JSON Response Transformation Based on Profile Rules

**Labels**: enhancement, profiles, phase-3

**Description**:
Implement the logic for filtering JSON response payloads based on profile read rules.

**Acceptance Criteria**:
- [ ] `IProfileResponseTransformer` interface defined
- [ ] Response transformation implementation:
  - Remove excluded properties based on profile rules
  - Remove excluded collections
  - Filter collection items by descriptor values
  - Handle IncludeOnly mode (only include specified members)
  - Support both single-item and array responses
  - Preserve JSON structure
- [ ] Collection filtering logic:
  - Parse descriptor URIs (namespace#codeValue)
  - Match filter values (case-insensitive)
  - Remove non-matching collection items
- [ ] Performance optimizations:
  - In-memory JSON manipulation (not string operations)
  - Early exit if no rules apply
  - Reuse parsed JSON structures
- [ ] Unit tests covering:
  - Property exclusion
  - Collection exclusion
  - Collection filtering by descriptor
  - IncludeOnly mode
  - Array response handling
  - Edge cases (null/empty collections)

**Files to Create**:
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/IProfileResponseTransformer.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/ProfileResponseTransformer.cs`

**Dependencies**: Task 3 (Profile Selection Logic)

---

### Task 7: GET Operation Integration with Profiles

**Title**: Integrate Profile Response Transformation with GET Operations

**Labels**: enhancement, profiles, phase-3

**Description**:
Integrate profile-based response transformation into the GET operation pipeline.

**Acceptance Criteria**:
- [ ] `ProfileResponseTransformationMiddleware` implementation:
  - Runs after backend query returns data
  - Applies profile read rules to response body
  - Handles both GET by ID and GET by query (collections)
  - Only transforms successful responses (2xx status codes)
- [ ] Error handling:
  - Log transformation errors
  - Fail-open: return full resource if transformation fails (with error log)
  - Include profile name in error logs for debugging
- [ ] Integration tests:
  - GET by ID with IncludeOnly profile
  - GET by ID with ExcludeOnly profile
  - GET by query with profile (array response)
  - Collection filtering by descriptor
  - Verify excluded properties are removed
  - Verify included properties are present
  - Verify pagination still works with profiles

**Files to Create**:
- `src/dms/core/EdFi.DataManagementService.Core/Middleware/ProfileResponseTransformationMiddleware.cs`

**Files to Modify**:
- Pipeline registration in frontend (add ProfileResponseTransformationMiddleware)

**Dependencies**: Task 6 (Profile-Based Response Filtering)

---

## Phase 4: Configuration and Management

### Task 8: Configuration Service Integration

**Title**: Integrate Profile Assignment with Configuration Service

**Labels**: enhancement, profiles, configuration-service, phase-4

**Description**:
Implement the Configuration Service integration for managing profile-to-client assignments.

**Acceptance Criteria**:
- [ ] Configuration Service database schema for profile assignments:
  - `ApplicationProfile` table with columns: ApplicationId, ProfileName, Resources (JSON array)
- [ ] Configuration Service API endpoints:
  - `POST /v2/applications/{applicationId}/profiles` - Assign profile to application
  - `GET /v2/applications/{applicationId}/profiles` - List assigned profiles
  - `DELETE /v2/applications/{applicationId}/profiles/{profileName}` - Remove profile assignment
- [ ] DMS integration:
  - Fetch profile assignments when loading client authorizations
  - Cache profile assignments with TTL
  - Include profile metadata in `ClientAuthorizations` model
- [ ] Validation:
  - Verify profile exists before assignment
  - Verify resources exist in ApiSchema
  - Prevent duplicate assignments
- [ ] Integration tests:
  - Profile assignment CRUD operations
  - DMS profile resolution using Configuration Service data
  - Cache invalidation scenarios

**Files to Create** (Configuration Service):
- Migration for ApplicationProfile table
- `ApplicationProfilesController.cs`
- `ApplicationProfileService.cs`

**Files to Modify** (DMS):
- `src/dms/core/EdFi.DataManagementService.Core/Model/ClientAuthorizations.cs` (add Profiles property)
- Configuration Service client code in DMS

**Dependencies**: Task 3 (Profile Selection Logic)

---

### Task 9: Administrative Endpoints and Management

**Title**: Implement Profile Management Administrative Endpoints

**Labels**: enhancement, profiles, admin, phase-4

**Description**:
Implement administrative endpoints for profile management and monitoring.

**Acceptance Criteria**:
- [ ] DMS API endpoints (admin-only):
  - `GET /v2/profiles/status` - Profile load status and statistics
  - `POST /v2/profiles/reload` - Trigger profile reload from disk
  - `GET /v2/profiles` - List all loaded profiles
  - `GET /v2/profiles/{profileName}` - Get profile details
- [ ] Response models:
  - Profile load status (loaded, failed, error details)
  - Profile metadata (name, resources, rules summary)
- [ ] Authorization:
  - Endpoints require admin role/claim
  - Return 403 for non-admin users
- [ ] Logging:
  - Log all admin endpoint invocations
  - Include user identity in audit logs
- [ ] Integration tests:
  - Profile status endpoint
  - Profile reload endpoint
  - Authorization enforcement
  - Error scenarios

**Files to Create**:
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Controllers/ProfilesController.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/ProfileManagementService.cs`
- `src/dms/core/EdFi.DataManagementService.Core/Profiles/Model/ProfileLoadStatusResponse.cs`

**Dependencies**: Task 2 (Profile Repository and Caching)

---

## Phase 5: Testing and Documentation

### Task 10: End-to-End Testing

**Title**: Comprehensive End-to-End Profile Testing

**Labels**: testing, profiles, phase-5

**Description**:
Create comprehensive end-to-end tests covering all profile scenarios and edge cases.

**Acceptance Criteria**:
- [ ] Integration test suite:
  - Full request/response cycle with profiles
  - Multiple profiles assigned to single client
  - Profile selection (implicit and explicit)
  - All sample profiles tested (Student-Demographics-Only, School-Basic-Info, etc.)
  - Error conditions (invalid profiles, missing profiles, malformed headers)
  - Profile reload scenarios
  - Multi-tenant profile scenarios
- [ ] Performance testing:
  - Measure profile application overhead
  - Schema compilation caching effectiveness
  - Response transformation performance with large payloads
  - Concurrent request handling with profiles
  - Target: <10ms overhead for profile application
- [ ] Load testing:
  - Profile behavior under high load
  - Cache efficiency
  - File watcher stability
- [ ] Test documentation:
  - Test scenario descriptions
  - Expected outcomes
  - Performance baselines

**Files to Create**:
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Profiles/ProfileIntegrationTests.cs`
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Profiles/ProfilePerformanceTests.cs`
- Performance test results documentation

**Dependencies**: All previous tasks

---

### Task 11: Documentation

**Title**: Complete Developer and Operator Documentation for Profiles

**Labels**: documentation, profiles, phase-5

**Description**:
Create comprehensive documentation for developers, operators, and API consumers covering profile usage and management.

**Acceptance Criteria**:
- [ ] Developer documentation:
  - How profiles work internally
  - Architecture and design decisions
  - How to extend profile functionality
  - Troubleshooting guide
- [ ] Operator documentation:
  - How to configure profiles (file layout, environment settings)
  - Profile deployment and management
  - Monitoring profile health
  - Troubleshooting common issues
  - Performance tuning
- [ ] API consumer documentation:
  - How to use profiles in API calls (header examples)
  - Common error scenarios and resolutions
  - Sample profile use cases
  - Best practices
- [ ] Migration guide:
  - Migrating profiles from AdminAPI-2.x to DMS
  - Compatibility notes
  - Deprecated features (if any)
- [ ] README updates:
  - Add profiles to main README feature list
  - Link to profile documentation

**Files to Create**:
- `docs/API-PROFILES.md` (main profiles documentation)
- `docs/API-PROFILES-OPERATOR-GUIDE.md` (operator-focused)
- `docs/API-PROFILES-DEVELOPER-GUIDE.md` (developer-focused)
- `docs/API-PROFILES-MIGRATION.md` (migration from AdminAPI-2.x)

**Files to Modify**:
- `README.md` (add profiles to feature list)
- `docs/CONFIGURATION.md` (add profile configuration options)

**Dependencies**: All previous tasks

---

## Issue Creation Checklist

When creating these issues in GitHub:

1. Create the Epic issue first
2. Create each task issue and link to the Epic
3. Apply appropriate labels:
   - `enhancement` for all new feature work
   - `profiles` for profile-specific work
   - `documentation` for docs
   - `testing` for test tasks
   - `phase-1`, `phase-2`, etc. for phase tracking
4. Set up GitHub Projects board with columns:
   - Backlog
   - Phase 1: Foundation
   - Phase 2: Write Path
   - Phase 3: Read Path
   - Phase 4: Configuration
   - Phase 5: Testing & Docs
   - In Progress
   - Done
5. Assign issues to appropriate phases/columns
6. Link dependencies between issues

## Estimated Timeline

- **Phase 1**: 2-3 weeks (Foundation)
- **Phase 2**: 1-2 weeks (Write Path)
- **Phase 3**: 1-2 weeks (Read Path)
- **Phase 4**: 2-3 weeks (Configuration & Management)
- **Phase 5**: 2-3 weeks (Testing & Documentation)

**Total**: 8-13 weeks for full implementation

## Success Criteria

- [ ] All sample profiles load successfully from file system
- [ ] Profiles can be assigned to API clients via Configuration Service
- [ ] Profile selection works via HTTP headers (implicit and explicit)
- [ ] POST/PUT requests are validated against profile-transformed schemas
- [ ] GET requests return filtered responses per profile rules
- [ ] Collection filtering by descriptor values works correctly
- [ ] Profile reload works without service restart
- [ ] Administrative endpoints provide visibility into profile status
- [ ] Performance overhead is <10ms per request
- [ ] Documentation is complete and accurate
- [ ] All tests pass (unit, integration, e2e, performance)
