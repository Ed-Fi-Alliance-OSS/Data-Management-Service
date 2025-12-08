# Ed-Fi API Profile Examples

This directory contains sample Ed-Fi API Profile XML files that demonstrate various profile patterns and use cases.

## Overview

API Profiles allow organizations to constrain the shape of API resources (properties, references, collections, and collection items) for specific usage scenarios. Profiles enable:

- **Data minimization**: Limit exposed data to only what is necessary
- **Role-based access**: Different profiles for different user roles
- **Use case optimization**: Tailor API responses for specific integrations
- **Compliance**: Enforce data governance policies at the API level

## Sample Profiles

### 1. Student-Demographics-Only.xml

**Use Case**: Public student directory, limited access reporting

**Access Pattern**: Read-only

**What's Included**:
- Student identity (StudentUniqueId, Id)
- Name fields (FirstName, MiddleName, LastSurname)
- Basic demographics (BirthDate, BirthSexDescriptor, etc.)

**What's Excluded**:
- All collections (addresses, phones, emails, etc.)
- All write operations

### 2. School-Basic-Info.xml

**Use Case**: Public school directory, basic school lookup

**Access Pattern**: Read and write with restrictions

**What's Included**:
- All school properties (SchoolId, NameOfInstitution, etc.)

**What's Excluded**:
- addresses collection
- institutionTelephones collection
- schoolCategories collection
- gradeLevels collection (write operations only)

### 3. Student-Home-Address-Only.xml

**Use Case**: Transportation routing, emergency contact information

**Access Pattern**: Read and write with collection filtering

**What's Included**:
- All student properties
- addresses collection filtered to only "Home" address type

**What's Excluded**:
- electronicMails collection
- telephones collection
- studentIdentificationCodes collection
- Non-home addresses

**Key Feature**: Demonstrates descriptor-based collection filtering

### 4. Assessment-Scores-Only.xml

**Use Case**: District reporting dashboard, aggregate score analysis

**Access Pattern**: Read-only

**What's Included**:
- StudentAssessment identity and references
- scoreResults collection (for aggregate analysis)

**What's Excluded**:
- accommodations collection
- items collection
- performanceLevels collection
- studentObjectiveAssessments collection
- All write operations

## Profile XML Structure

### Basic Structure

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

- **IncludeAll**: Include all members of the resource (default behavior)
- **IncludeOnly**: Include only explicitly listed members (allowlist approach)
- **ExcludeOnly**: Include all members except explicitly listed ones (denylist approach)
- **ExcludeAll**: Exclude all members (useful for read-only or write-only profiles)

### Collection Filtering

Collections can be filtered based on descriptor values:

```xml
<Collection name="addresses" memberSelection="IncludeAll">
  <Filter propertyName="addressTypeDescriptor" value="Home" />
</Collection>
```

This includes the addresses collection but only includes items where the addressTypeDescriptor ends with "#Home" (following Ed-Fi URI conventions).

## Using Profiles in API Calls

### Reading with a Profile (GET)

Use the `Accept` header with a `profile` parameter:

```bash
curl -X GET http://localhost:8080/data/v3/ed-fi/students/{id} \
  -H "Accept: application/json;profile=\"Student-Demographics-Only\"" \
  -H "Authorization: Bearer {token}"
```

### Writing with a Profile (POST/PUT)

Use the `Content-Type` header with a `profile` parameter:

```bash
curl -X POST http://localhost:8080/data/v3/ed-fi/students \
  -H "Content-Type: application/json;profile=\"Student-Basic-Write\"" \
  -H "Authorization: Bearer {token}" \
  -d @student-payload.json
```

## Profile Configuration

### File-Based Deployment

Place profile XML files in the configured profiles directory:

```
/app/profiles/
  ├── global/
  │   ├── Student-Demographics-Only.xml
  │   ├── School-Basic-Info.xml
  │   └── Assessment-Scores-Only.xml
  └── tenant-specific/
      └── Custom-Profile.xml
```

### Profile Assignment

Profiles must be assigned to API clients through the Configuration Service:

```bash
curl -X POST http://localhost:8081/v2/applications/{applicationId}/profiles \
  -H "Content-Type: application/json" \
  -d '{
    "profileName": "Student-Demographics-Only",
    "resources": ["Student", "StudentSchoolAssociation"]
  }'
```

## Creating Custom Profiles

1. **Copy a sample profile** as a starting point
2. **Modify the profile name** to be unique
3. **Adjust the resource rules** based on your requirements
4. **Validate the XML** against the Profile XSD schema
5. **Deploy the profile** to the profiles directory
6. **Assign the profile** to API clients via Configuration Service
7. **Test the profile** with sample API requests

## Profile Best Practices

1. **Start with IncludeOnly**: Use the IncludeOnly strategy for sensitive resources to ensure you don't accidentally expose data
2. **Use descriptive names**: Profile names should clearly indicate their purpose
3. **Document use cases**: Add XML comments explaining the profile's intended use
4. **Test thoroughly**: Verify both read and write operations with the profile
5. **Consider performance**: Profiles with many filters may impact response times
6. **Version your profiles**: Keep profile XML files under version control
7. **Monitor usage**: Use DMS logs to track profile application and errors

## Troubleshooting

### Profile Not Applied

**Check**:
- Profile is loaded (check `/v2/profiles/status` endpoint)
- Profile is assigned to the API client in Configuration Service
- Header format is correct: `Accept: application/json;profile="ProfileName"`
- Profile name matches exactly (case-sensitive)

### Validation Errors

**Check**:
- XML is well-formed
- Resource names match Ed-Fi resources exactly
- Property/collection names match the resource schema
- memberSelection values are valid

### Unexpected Data Excluded

**Check**:
- memberSelection strategy (IncludeOnly vs ExcludeOnly)
- Collection filters are correctly configured
- Profile is applied to the correct resource

## Additional Resources

- [API Profiles Design Document](../../design/api-profiles-design.md)
- [Ed-Fi API Guidelines](https://docs.ed-fi.org/)
- [AdminAPI-2.x Profiles Documentation](https://github.com/Ed-Fi-Alliance-OSS/AdminAPI-2.x)

## License

Copyright (c) 2025 Ed-Fi Alliance, LLC and contributors.

Licensed under the Apache License, Version 2.0 (the "License"). See LICENSE and NOTICES files in the project root for more information.
