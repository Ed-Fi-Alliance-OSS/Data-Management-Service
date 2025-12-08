# Ed-Fi API Profiles Guide

## Table of Contents

- [Overview](#overview)
- [What Are API Profiles?](#what-are-api-profiles)
- [Configuration](#configuration)
- [Profile Directory Structure](#profile-directory-structure)
- [Creating Profiles](#creating-profiles)
- [Deploying Profiles](#deploying-profiles)
- [Assigning Profiles to Clients](#assigning-profiles-to-clients)
- [Using Profiles in API Calls](#using-profiles-in-api-calls)
- [Monitoring and Management](#monitoring-and-management)
- [Troubleshooting](#troubleshooting)
- [Best Practices](#best-practices)
- [Examples](#examples)

## Overview

Ed-Fi API Profiles provide a mechanism to constrain the shape of API resources for specific usage scenarios. Profiles enable organizations to:

- **Enforce data minimization** by limiting what data is exposed or accepted
- **Implement role-based access** with different profiles for different user types
- **Support compliance requirements** by constraining data access at the API level
- **Optimize integrations** by tailoring API responses for specific use cases

Profiles are defined in XML format and are compatible with existing AdminAPI-2.x profile definitions.

## What Are API Profiles?

An API Profile defines rules that constrain:

1. **Properties**: Which scalar fields are included/excluded
2. **References**: Which navigational references are included/excluded
3. **Collections**: Which arrays are included/excluded
4. **Collection Items**: Which items within collections are included (filtered by descriptor values)

### Profile Application Modes

**Read Operations (GET)**:
- Profiles filter response payloads
- Excluded members are removed from the response
- Collections can be filtered by descriptor values

**Write Operations (POST/PUT)**:
- Profiles constrain what data can be submitted
- Excluded members are rejected if present (overposting prevention)
- Only included members are accepted

### Profile Selection

Profiles can be applied in two ways:

1. **Implicit**: When exactly one profile is assigned to a client for a resource, it's automatically applied
2. **Explicit**: When multiple profiles are available, the client specifies which to use via HTTP headers

## Configuration

### Application Settings

Add the following configuration to your `appsettings.json`:

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

### Configuration Options

| Setting | Description | Default |
|---------|-------------|---------|
| `Source` | Profile source (currently only "FileSystem" supported) | FileSystem |
| `ProfilesPath` | Directory path containing profile XML files | /app/profiles |
| `EnableProfileWatcher` | Enable automatic profile reload on file changes | true |
| `WatcherPollingIntervalSeconds` | Interval for checking file changes (seconds) | 60 |
| `MaxProfileSizeBytes` | Maximum size of a profile XML file | 1048576 (1MB) |
| `MaxProfilesPerClient` | Maximum profiles that can be assigned to one client | 100 |

### Environment Variables

Configuration can be overridden via environment variables using double underscore notation:

```bash
AppSettings__Profiles__ProfilesPath=/custom/profiles/path
AppSettings__Profiles__EnableProfileWatcher=false
```

## Profile Directory Structure

Profiles are organized in a directory structure:

```
/app/profiles/
  ├── global/                           # Available to all tenants
  │   ├── Student-Demographics-Only.xml
  │   ├── School-Basic-Info.xml
  │   └── Assessment-Scores-Only.xml
  ├── tenant-district-001/              # Tenant-specific profiles
  │   ├── Custom-Assessment.xml
  │   └── Custom-Attendance.xml
  └── tenant-charter-network/
      └── Limited-Access.xml
```

### Directory Types

**Global Profiles** (`/app/profiles/global/`):
- Available to all tenants and clients
- Use for common, reusable profiles
- Loaded first during initialization

**Tenant-Specific Profiles** (`/app/profiles/{tenant-id}/`):
- Only available within the specific tenant context
- Override global profiles with the same name
- Use for custom, organization-specific profiles

## Creating Profiles

### Profile XML Structure

```xml
<?xml version="1.0" encoding="utf-8"?>
<Profile name="ProfileName">
  <Resource name="ResourceName">
    <ReadContentType memberSelection="IncludeAll|IncludeOnly|ExcludeOnly">
      <Property name="propertyName" />
      <Collection name="collectionName" memberSelection="IncludeAll|ExcludeOnly">
        <Filter propertyName="descriptorField" value="DescriptorValue" />
      </Collection>
    </ReadContentType>
    <WriteContentType memberSelection="IncludeAll|IncludeOnly|ExcludeOnly|ExcludeAll">
      <!-- Same structure as ReadContentType -->
    </WriteContentType>
  </Resource>
</Profile>
```

### Member Selection Strategies

| Strategy | Behavior |
|----------|----------|
| `IncludeAll` | Include all members (default, no restrictions) |
| `IncludeOnly` | Include only explicitly listed members (allowlist) |
| `ExcludeOnly` | Include all except explicitly listed members (denylist) |
| `ExcludeAll` | Exclude all members (useful for read-only or write-only profiles) |

### Example: Read-Only Demographics Profile

```xml
<?xml version="1.0" encoding="utf-8"?>
<Profile name="Student-Demographics-Only">
  <Resource name="Student">
    <ReadContentType memberSelection="IncludeOnly">
      <Property name="StudentUniqueId" />
      <Property name="FirstName" />
      <Property name="LastSurname" />
      <Property name="BirthDate" />
    </ReadContentType>
    <WriteContentType memberSelection="ExcludeAll" />
  </Resource>
</Profile>
```

### Example: Exclude Sensitive Collections

```xml
<?xml version="1.0" encoding="utf-8"?>
<Profile name="School-Without-Contact-Info">
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

### Example: Filter Collection by Descriptor

```xml
<?xml version="1.0" encoding="utf-8"?>
<Profile name="Student-Home-Address-Only">
  <Resource name="Student">
    <ReadContentType memberSelection="IncludeAll">
      <Collection name="addresses" memberSelection="IncludeAll">
        <Filter propertyName="addressTypeDescriptor" value="Home" />
      </Collection>
    </ReadContentType>
    <WriteContentType memberSelection="IncludeAll">
      <Collection name="addresses" memberSelection="IncludeAll">
        <Filter propertyName="addressTypeDescriptor" value="Home" />
      </Collection>
    </WriteContentType>
  </Resource>
</Profile>
```

### Profile Naming Conventions

- Use descriptive, kebab-case names: `Student-Demographics-Only`
- Include the resource name when profile is resource-specific
- Indicate the use case: `Assessment-Scores-Only`, `School-Basic-Info`
- Avoid generic names: "Profile1", "Test"

## Deploying Profiles

### Docker Deployment

Mount the profiles directory as a volume:

```yaml
# docker-compose.yml
services:
  dms:
    image: edfialliance/data-management-service:latest
    volumes:
      - ./profiles:/app/profiles:ro
    environment:
      - AppSettings__Profiles__ProfilesPath=/app/profiles
```

### Kubernetes Deployment

Use a ConfigMap or PersistentVolume:

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: dms-profiles
data:
  Student-Demographics-Only.xml: |
    <?xml version="1.0" encoding="utf-8"?>
    <Profile name="Student-Demographics-Only">
      ...
    </Profile>
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: dms
spec:
  template:
    spec:
      containers:
      - name: dms
        volumeMounts:
        - name: profiles
          mountPath: /app/profiles
      volumes:
      - name: profiles
        configMap:
          name: dms-profiles
```

### Profile Validation

Before deploying, validate your profile XML:

1. **Check XML syntax**: Use an XML validator or IDE
2. **Validate against XSD schema**: Use the Profile XSD schema from the repository
3. **Test resource names**: Ensure resource names match your ApiSchema exactly (case-sensitive)
4. **Test property names**: Verify property and collection names exist on the resource

## Assigning Profiles to Clients

Profiles must be assigned to API clients (applications) through the Configuration Service.

### Assign a Profile

```bash
curl -X POST http://localhost:8081/v2/applications/{applicationId}/profiles \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer {admin-token}" \
  -d '{
    "profileName": "Student-Demographics-Only",
    "resources": ["Student", "StudentSchoolAssociation"]
  }'
```

### List Assigned Profiles

```bash
curl -X GET http://localhost:8081/v2/applications/{applicationId}/profiles \
  -H "Authorization: Bearer {admin-token}"
```

### Remove a Profile Assignment

```bash
curl -X DELETE http://localhost:8081/v2/applications/{applicationId}/profiles/Student-Demographics-Only \
  -H "Authorization: Bearer {admin-token}"
```

## Using Profiles in API Calls

### GET Requests (Read Operations)

Use the `Accept` header with a `profile` parameter:

```bash
# Get a single student with demographics-only profile
curl -X GET http://localhost:8080/data/v3/ed-fi/students/{id} \
  -H "Accept: application/json;profile=\"Student-Demographics-Only\"" \
  -H "Authorization: Bearer {token}"

# Query students with profile
curl -X GET "http://localhost:8080/data/v3/ed-fi/students?limit=100" \
  -H "Accept: application/json;profile=\"Student-Demographics-Only\"" \
  -H "Authorization: Bearer {token}"
```

### POST/PUT Requests (Write Operations)

Use the `Content-Type` header with a `profile` parameter:

```bash
# Create a student with limited profile
curl -X POST http://localhost:8080/data/v3/ed-fi/students \
  -H "Content-Type: application/json;profile=\"Student-Basic-Write\"" \
  -H "Authorization: Bearer {token}" \
  -d @student-payload.json

# Update a student with profile
curl -X PUT http://localhost:8080/data/v3/ed-fi/students/{id} \
  -H "Content-Type: application/json;profile=\"Student-Basic-Write\"" \
  -H "Authorization: Bearer {token}" \
  -d @student-payload.json
```

### Implicit Profile Application

If only one profile is assigned to a client for a resource, it is applied automatically without needing to specify it in headers:

```bash
# Profile is automatically applied if only one is assigned
curl -X GET http://localhost:8080/data/v3/ed-fi/students/{id} \
  -H "Accept: application/json" \
  -H "Authorization: Bearer {token}"
```

## Monitoring and Management

### Check Profile Load Status

```bash
curl -X GET http://localhost:8080/v2/profiles/status \
  -H "Authorization: Bearer {admin-token}"
```

Response:
```json
{
  "totalProfiles": 25,
  "loadedProfiles": 23,
  "failedProfiles": 2,
  "lastReloadTime": "2025-12-08T23:19:26Z",
  "profiles": [
    {
      "name": "Student-Demographics-Only",
      "status": "Loaded",
      "resources": ["Student"]
    },
    {
      "name": "Invalid-Profile",
      "status": "Failed",
      "error": "XML schema validation failed"
    }
  ]
}
```

### Reload Profiles

Trigger a manual profile reload without restarting the service:

```bash
curl -X POST http://localhost:8080/v2/profiles/reload \
  -H "Authorization: Bearer {admin-token}"
```

### List Available Profiles

```bash
curl -X GET http://localhost:8080/v2/profiles \
  -H "Authorization: Bearer {admin-token}"
```

### Get Profile Details

```bash
curl -X GET http://localhost:8080/v2/profiles/Student-Demographics-Only \
  -H "Authorization: Bearer {admin-token}"
```

## Troubleshooting

### Profile Not Applied to Requests

**Symptoms**: API returns full resource data despite profile being specified

**Troubleshooting Steps**:

1. **Check profile is loaded**:
   ```bash
   curl http://localhost:8080/v2/profiles/status
   ```
   Look for your profile in the loaded list.

2. **Verify profile assignment**:
   ```bash
   curl http://localhost:8081/v2/applications/{appId}/profiles
   ```
   Ensure the profile is assigned to your application.

3. **Check header format**:
   - GET: `Accept: application/json;profile="ProfileName"`
   - POST/PUT: `Content-Type: application/json;profile="ProfileName"`
   - Profile name must match exactly (case-sensitive)
   - Use double quotes around profile name

4. **Review DMS logs**:
   ```bash
   docker logs dms | grep -i profile
   ```
   Look for profile selection or application errors.

### Profile Validation Errors at Startup

**Symptoms**: Profiles fail to load with validation errors in startup logs

**Troubleshooting Steps**:

1. **Check XML syntax**:
   - Ensure XML is well-formed
   - Verify closing tags match opening tags
   - Check for special characters that need escaping

2. **Validate against XSD**:
   - Use the Profile XSD schema from the repository
   - Validate with an XML validator tool

3. **Verify resource names**:
   - Resource names must match ApiSchema exactly
   - Names are case-sensitive: use "Student", not "student"
   - Check the ApiSchema for the exact resource name

4. **Verify property/collection names**:
   - Property names must exist on the resource
   - Use JSON property names, not display names
   - Check the resource's JSON schema for valid property names

### Multiple Profiles Error (HTTP 406)

**Symptoms**: API returns 406 Not Acceptable when no profile is specified in the header

**Cause**: Multiple profiles are assigned to the client for the requested resource

**Solution**:
1. Explicitly specify which profile to use in the request header
2. Or, remove extra profile assignments so only one remains (implicit mode)

Example response:
```json
{
  "error": "MultipleProfilesAvailable",
  "message": "Multiple profiles are available for this resource. Please specify a profile.",
  "availableProfiles": ["Student-Demographics-Only", "Student-Full-Access"]
}
```

### Invalid Profile Error (HTTP 400)

**Symptoms**: API returns 400 Bad Request when profile is specified

**Cause**: The specified profile is not assigned to the client or doesn't exist

**Solution**:
1. Check available profiles: `GET /v2/applications/{appId}/profiles`
2. Assign the profile if it exists but isn't assigned
3. Verify the profile name spelling and capitalization

### Profile Transformation Failures

**Symptoms**: Errors in logs about profile transformation, or HTTP 500 responses

**Troubleshooting Steps**:

1. **Check profile rules**:
   - Ensure required fields aren't excluded
   - Verify collection filters reference valid descriptor fields

2. **Review error logs**:
   ```bash
   docker logs dms | grep -i "profile transformation"
   ```

3. **Test with a simpler profile**:
   - Create a minimal profile to isolate the issue
   - Gradually add rules to identify the problematic configuration

## Best Practices

### Profile Design

1. **Start with IncludeOnly**: For sensitive resources, use IncludeOnly to explicitly control exposed data
2. **Use meaningful names**: Profile names should clearly indicate their purpose
3. **Document use cases**: Add XML comments explaining the profile's intended use
4. **Keep profiles focused**: Each profile should serve a single, clear purpose
5. **Test thoroughly**: Verify both read and write operations with the profile

### Profile Management

1. **Version control**: Store profile XML files in version control (Git)
2. **Test before deploying**: Validate profiles in a test environment first
3. **Monitor profile health**: Regularly check `/v2/profiles/status` endpoint
4. **Review logs**: Monitor DMS logs for profile-related warnings or errors
5. **Document assignments**: Keep a record of which profiles are assigned to which clients

### Performance

1. **Limit profile complexity**: Excessive filtering can impact performance
2. **Cache effectively**: Profile-transformed schemas are cached; don't disable caching
3. **Monitor overhead**: Use performance monitoring to track profile application impact
4. **Optimize filters**: Descriptor-based filters are more expensive than simple inclusion/exclusion

### Security

1. **Principle of least privilege**: Grant minimum necessary data access via profiles
2. **Separate read/write**: Use ExcludeAll for read-only or write-only profiles
3. **Audit profile usage**: Review logs to ensure profiles are used as intended
4. **Protect profile files**: Limit write access to profile directories
5. **Regular review**: Periodically review profile assignments and update as needed

## Examples

### Example 1: Public Student Directory

**Use Case**: A public-facing student directory that shows only basic demographics

**Profile**: `Student-Demographics-Only.xml`

```xml
<?xml version="1.0" encoding="utf-8"?>
<Profile name="Student-Demographics-Only">
  <Resource name="Student">
    <ReadContentType memberSelection="IncludeOnly">
      <Property name="StudentUniqueId" />
      <Property name="FirstName" />
      <Property name="LastSurname" />
      <Property name="BirthDate" />
    </ReadContentType>
    <WriteContentType memberSelection="ExcludeAll" />
  </Resource>
</Profile>
```

**Usage**:
```bash
curl -X GET http://localhost:8080/data/v3/ed-fi/students \
  -H "Accept: application/json;profile=\"Student-Demographics-Only\"" \
  -H "Authorization: Bearer {token}"
```

### Example 2: District Reporting Dashboard

**Use Case**: A reporting dashboard that needs assessment scores but not detailed items

**Profile**: `Assessment-Scores-Only.xml`

```xml
<?xml version="1.0" encoding="utf-8"?>
<Profile name="Assessment-Scores-Only">
  <Resource name="StudentAssessment">
    <ReadContentType memberSelection="IncludeAll">
      <Collection name="accommodations" memberSelection="ExcludeOnly" />
      <Collection name="items" memberSelection="ExcludeOnly" />
      <Collection name="performanceLevels" memberSelection="ExcludeOnly" />
    </ReadContentType>
    <WriteContentType memberSelection="ExcludeAll" />
  </Resource>
</Profile>
```

### Example 3: Transportation System Integration

**Use Case**: A transportation system needs student home addresses only

**Profile**: `Student-Home-Address-Only.xml`

```xml
<?xml version="1.0" encoding="utf-8"?>
<Profile name="Student-Home-Address-Only">
  <Resource name="Student">
    <ReadContentType memberSelection="IncludeAll">
      <Collection name="addresses" memberSelection="IncludeAll">
        <Filter propertyName="addressTypeDescriptor" value="Home" />
      </Collection>
      <Collection name="electronicMails" memberSelection="ExcludeOnly" />
      <Collection name="telephones" memberSelection="ExcludeOnly" />
    </ReadContentType>
    <WriteContentType memberSelection="ExcludeAll" />
  </Resource>
</Profile>
```

## Additional Resources

- [API Profiles Design Document](../reference/design/api-profiles-design.md)
- [Profile XML Examples](../reference/examples/profiles/)
- [Implementation Tasks](../reference/design/api-profiles-implementation-tasks.md)
- [Ed-Fi API Guidelines](https://docs.ed-fi.org/)
- [AdminAPI-2.x Documentation](https://github.com/Ed-Fi-Alliance-OSS/AdminAPI-2.x)

## Support

For issues, questions, or feature requests related to API Profiles:

1. Check the [Troubleshooting](#troubleshooting) section
2. Review the [Examples](#examples) for similar use cases
3. Search existing GitHub issues
4. Create a new issue with:
   - Profile XML (sanitized)
   - Error logs
   - Steps to reproduce
   - Expected vs. actual behavior

---

Copyright (c) 2025 Ed-Fi Alliance, LLC and contributors.

Licensed under the Apache License, Version 2.0 (the "License"). See LICENSE and NOTICES files in the project root for more information.
