# API Profiles Quick Start Guide

## Introduction

This guide will help you quickly get started with API Profiles in the Ed-Fi Data Management Service (DMS). Profiles allow you to control which data fields are visible or modifiable for different API consumers.

## What are API Profiles?

API Profiles are XML-based policy documents that define:

- Which properties of a resource can be read or written
- Which collections (arrays) are accessible
- Which references (links to other resources) are included
- Different access levels for different API consumers

## Use Cases

Common scenarios where profiles are valuable:

1. **Vendor Integration**: Limit third-party vendors to specific data fields
2. **Reporting Systems**: Provide read-only access to aggregated data
3. **Parent Portals**: Show only relevant student information to parents
4. **Data Privacy**: Hide PII fields from unauthorized consumers
5. **Compliance**: Enforce FERPA/GDPR requirements at the API level

## Prerequisites

- DMS Platform installed and running
- Admin access to DMS Configuration Service
- Basic understanding of Ed-Fi resources and data model
- Familiarity with XML (helpful but not required)

## Architecture Overview

Profiles are managed across two services:

- **Config Service**: Manages profile storage and lifecycle (create, update, delete, export)
  - Endpoints: `/v2/profiles/*`
  - Used for: Importing, exporting, and managing profiles

- **DMS (Data Management Service)**: Enforces profile rules during data operations
  - Endpoints: `/data/v3/ed-fi/*`
  - Used for: Reading/writing data with profile filtering applied

### Config Service Endpoints

**Main Endpoints (JSON format)**:

- `POST /v2/profiles` - Create profile with JSON
- `GET /v2/profiles` - List all profiles
- `GET /v2/profiles/{id}` - Get profile details as JSON
- `PUT /v2/profiles/{id}` - Update profile with JSON
- `DELETE /v2/profiles/{id}` - Delete profile

**Compatibility Endpoints (XML format)**:

- `POST /v2/profiles/import` - Import XML file (AdminAPI-2.x compatible)
- `POST /v2/profiles/xml` - Create profile with XML body
- `GET /v2/profiles/{id}/export` - Export profile as XML file

In this guide:

- Profile management operations use the **Config Service** API
- Data operations use the **DMS** API with profile headers

## Quick Start: 5 Minute Setup

### Step 1: Download Example Profile

Download one of the example profiles from the repository:

```bash
curl -O https://raw.githubusercontent.com/Ed-Fi-Alliance-OSS/Data-Management-Service/main/docs/profiles/examples/student-read-only.xml
```

Or create your own `student-read-only.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Profile name="Student-Read-Only">
  <Resource name="Student">
    <ReadContentType memberSelection="IncludeOnly">
      <Property name="StudentUniqueId" />
      <Property name="FirstName" />
      <Property name="LastSurname" />
      <Property name="BirthDate" />
    </ReadContentType>
  </Resource>
</Profile>
```

### Step 2: Import Profile

**Option A: Import XML Profile (Compatibility Endpoint)**

Import the profile using the XML compatibility endpoint:

```bash
curl -X POST \
  https://your-config-service/v2/profiles/import \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -F "file=@student-read-only.xml"
```

**Option B: Create Profile with JSON (Main Endpoint)**

Create a profile directly with JSON:

```bash
curl -X POST \
  https://your-config-service/v2/profiles \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Student-Read-Only",
    "definition": {
      "profileName": "Student-Read-Only",
      "resources": [{
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
      }]
    }
  }'
```

Response (both options):

```http
201 Created
Location: /v2/profiles/12345
```

The profile has been created successfully. The Location header contains the URL to access the profile.

> **Note**: Use `/v2/profiles/import` for XML files (backward compatibility with AdminAPI-2.x), or use `/v2/profiles` for JSON-based profile creation (recommended for new integrations).

### Step 3: Use Profile in API Request

Make a request with the profile specified in the `Accept` header:

```bash
curl -X GET \
  https://dms-api/data/v3/ed-fi/students/{id} \
  -H "Accept: application/json;profile=student-read-only" \
  -H "Authorization: Bearer YOUR_TOKEN"
```

Response will only include the fields specified in the profile:

```json
{
  "id": "abc123",
  "studentUniqueId": "604822",
  "firstName": "John",
  "lastSurname": "Doe",
  "birthDate": "2010-05-15"
}
```

### Step 4: Verify Profile

List all profiles to confirm import:

```bash
curl -X GET \
  https://your-config-service/v2/profiles \
  -H "Authorization: Bearer YOUR_TOKEN"
```

## Understanding Profile Syntax

### Basic Structure

```xml
<Profile name="ProfileName">
  <Resource name="ResourceName">
    <ReadContentType memberSelection="IncludeOnly|ExcludeOnly|IncludeAll|ExcludeAll">
      <!-- Elements for read operations -->
    </ReadContentType>
    <WriteContentType memberSelection="IncludeOnly|ExcludeOnly|IncludeAll|ExcludeAll">
      <!-- Elements for write operations -->
    </WriteContentType>
  </Resource>
</Profile>
```

### Member Selection Options

- **IncludeOnly**: Only listed elements are accessible (whitelist)
- **ExcludeOnly**: All elements except listed are accessible (blacklist)
- **IncludeAll**: All elements are accessible (default, no filtering)
- **ExcludeAll**: No elements are accessible (use with caution)

### JSON Structure (Internal Format)

When profiles are stored in the Config Service or retrieved via the API, they use a JSON format. Here's the equivalent JSON structure:

```json
{
  "profileName": "ProfileName",
  "resources": [
    {
      "resourceName": "ResourceName",
      "readContentType": {
        "memberSelection": "IncludeOnly",
        "properties": [
          {"name": "propertyName"}
        ],
        "collections": [
          {
            "name": "collectionName",
            "memberSelection": "IncludeOnly",
            "properties": [
              {"name": "childPropertyName"}
            ]
          }
        ],
        "references": [
          {
            "name": "referenceName",
            "properties": [
              {"name": "referencePropertyName"}
            ]
          }
        ]
      },
      "writeContentType": {
        "memberSelection": "IncludeOnly",
        "properties": [
          {"name": "propertyName"}
        ]
      }
    }
  ]
}
```

**Example: Student-Read-Only Profile in JSON**

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
        ],
        "references": [
          {
            "name": "schoolReference",
            "properties": [
              {"name": "schoolId"}
            ]
          }
        ]
      }
    }
  ]
}
```

> **Note**: The main API endpoints (`POST /v2/profiles`, `GET /v2/profiles/{id}`, `PUT /v2/profiles/{id}`) use **JSON format** for creating, retrieving, and updating profiles. XML format is supported through compatibility endpoints (`POST /v2/profiles/import`, `POST /v2/profiles/xml`, `GET /v2/profiles/{id}/export`) for backward compatibility with AdminAPI-2.x.

### Element Types

1. **Property**: Simple scalar field

   ```xml
   <Property name="FirstName" />
   ```

2. **Collection**: Array of child objects

   ```xml
   <Collection name="Addresses" memberSelection="IncludeOnly">
     <Property name="StreetNumberName" />
     <Property name="City" />
   </Collection>
   ```

3. **Reference**: Link to another resource

   ```xml
   <Reference name="SchoolReference">
     <Property name="SchoolId" />
   </Reference>
   ```

## Common Patterns

### Pattern 1: Read-Only Access

Allow reading specific fields, prevent all writes:

```xml
<Profile name="Student-ReadOnly">
  <Resource name="Student">
    <ReadContentType memberSelection="IncludeOnly">
      <Property name="StudentUniqueId" />
      <Property name="FirstName" />
      <Property name="LastSurname" />
    </ReadContentType>
    <!-- No WriteContentType = no writes allowed -->
  </Resource>
</Profile>
```

### Pattern 2: Limited Write Access

Allow updates to specific fields only:

```xml
<Profile name="Student-ContactInfo">
  <Resource name="Student">
    <WriteContentType memberSelection="IncludeOnly">
      <Property name="StudentUniqueId" /> <!-- Required for identity -->
      <Collection name="Addresses" memberSelection="IncludeOnly">
        <Property name="StreetNumberName" />
        <Property name="City" />
        <Property name="StateAbbreviation" />
        <Property name="PostalCode" />
      </Collection>
      <Collection name="ElectronicMails" memberSelection="IncludeAll" />
    </WriteContentType>
  </Resource>
</Profile>
```

### Pattern 3: Hide Sensitive Data

Exclude sensitive fields from read operations:

```xml
<Profile name="Student-NoSSN">
  <Resource name="Student">
    <ReadContentType memberSelection="ExcludeOnly">
      <Property name="StudentIdentificationCodes" />
      <Collection name="StudentIdentificationCodes" memberSelection="ExcludeAll" />
    </ReadContentType>
  </Resource>
</Profile>
```

### Pattern 4: Multi-Resource Profile

Control access to multiple resources:

```xml
<Profile name="BasicAccess">
  <Resource name="Student">
    <ReadContentType memberSelection="IncludeOnly">
      <Property name="StudentUniqueId" />
      <Property name="FirstName" />
      <Property name="LastSurname" />
    </ReadContentType>
  </Resource>
  <Resource name="School">
    <ReadContentType memberSelection="IncludeOnly">
      <Property name="SchoolId" />
      <Property name="NameOfInstitution" />
    </ReadContentType>
  </Resource>
</Profile>
```

## Testing Your Profile

### 1. Test with Postman/Insomnia

Import this collection to test profiles:

**GET Request with Profile**:

- URL: `{{base_url}}/data/v3/ed-fi/students/{{student_id}}`
- Headers:
  - `Accept: application/json;profile={{profile_name}}`
  - `Authorization: Bearer {{token}}`

**POST Request with Profile**:

- URL: `{{base_url}}/data/v3/ed-fi/students`
- Headers:
  - `Content-Type: application/json;profile={{profile_name}}`
  - `Authorization: Bearer {{token}}`
- Body: Student JSON

### 2. Compare Responses

Test the same request with and without profile:

```bash
# Without profile (full response)
curl -X GET \
  https://dms-api/data/v3/ed-fi/students/{id} \
  -H "Accept: application/json" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  | jq . > full-response.json

# With profile (filtered response)
curl -X GET \
  https://dms-api/data/v3/ed-fi/students/{id} \
  -H "Accept: application/json;profile=student-read-only" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  | jq . > filtered-response.json

# Compare
diff full-response.json filtered-response.json
```

### 3. Validate Write Restrictions

Test that excluded fields are rejected:

```bash
# This should fail if Addresses are excluded
curl -X POST \
  https://dms-api/data/v3/ed-fi/students \
  -H "Content-Type: application/json;profile=student-read-only" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -d '{
    "studentUniqueId": "604822",
    "firstName": "John",
    "lastSurname": "Doe",
    "addresses": [{"city": "Austin"}]  # Excluded field
  }'
```

Expected response: `400 Bad Request` with error indicating profile violation.

## Troubleshooting

### Profile Not Found

**Error**: `404 Profile 'xyz' not found`

**Solution**:

1. Verify profile name matches exactly (case-sensitive)
2. Check profile is active: `GET /v2/profiles` (Config Service)
3. Ensure profile was imported successfully

### Profile Not Applied

**Symptoms**: Response includes all fields despite profile specification

**Causes**:

1. Profile header not formatted correctly
2. Profile enforcement not enabled in configuration
3. Profile cache needs refresh

**Solutions**:

```bash
# Check profile status (Config Service)
curl -X GET \
  https://your-config-service/v2/profiles/{id} \
  -H "Authorization: Bearer YOUR_TOKEN"

# Verify header format
Accept: application/json;profile=profile-name
# NOT: Accept: application/json; profile=profile-name (no space before profile)
```

### Write Validation Fails

**Error**: `400 Bad Request: Property 'xyz' is not allowed by profile`

**Cause**: Attempting to write a field excluded by profile

**Solution**: Remove the excluded field from request body or update profile to include it

### Performance Issues

**Symptoms**: Slow API responses with profiles enabled

**Solutions**:

1. Check profile cache hit rate in monitoring
2. Simplify complex profiles (reduce nested collections)
3. Increase cache duration in configuration
4. Pre-load frequently used profiles on startup

## Best Practices

### 1. Naming Conventions

Use descriptive names that indicate purpose:

- ✅ `Student-ReadOnly-Demographics`
- ✅ `Assessment-Vendor-Limited`
- ✅ `Parent-Portal-Access`
- ❌ `Profile1`
- ❌ `Temp`

### 2. Start with IncludeOnly

For security-critical scenarios, use `IncludeOnly` to whitelist allowed fields:

```xml
<ReadContentType memberSelection="IncludeOnly">
  <!-- Explicitly list allowed fields -->
</ReadContentType>
```

This is safer than `ExcludeOnly` which can accidentally expose new fields added in future versions.

### 3. Test Before Production

Always test profiles in non-production environment:

1. Import profile to test environment
2. Validate read/write operations
3. Verify error handling
4. Check performance impact
5. Review audit logs

### 4. Document Profiles

Add descriptions to profiles explaining their purpose:

```xml
<!-- Profile: Student-ReadOnly-Demographics
     Purpose: Provides read-only access to basic student demographics
     Used By: External reporting systems
     Excludes: Assessment data, program associations, sensitive IDs
     Version: 1.0
     Updated: 2025-01-15
-->
<Profile name="Student-ReadOnly-Demographics">
  <!-- ... -->
</Profile>
```

### 5. Version Control

Store profile XMLs in version control:

```
profiles/
├── README.md
├── student-read-only-v1.xml
├── student-read-only-v2.xml
└── changelog.md
```

### 6. Monitor Usage

Track which profiles are used most frequently:

- Profile selection counts
- Error rates per profile
- Performance metrics
- Cache hit rates

## Next Steps

### Get Help

- Review example profiles in `docs/profiles/examples/`
- Check troubleshooting section above
- Consult Ed-Fi community forums
- Contact Ed-Fi support team

## Example Profiles Repository

Find complete, tested example profiles at:

```
docs/profiles/examples/
├── student-read-only.xml
├── student-write-limited.xml
├── assessment-limited.xml
├── school-minimal.xml
└── descriptor-full-access.xml
```

Each example includes:

- Use case description
- Complete XML profile
- Test data
- Expected behavior

---

**Need Help?** Join the Ed-Fi community at <https://www.ed-fi.org/community/>
