# Example API Profiles

This directory contains example API Profile XML documents that demonstrate common use cases for the Ed-Fi Data Management Service (DMS).

## Available Profiles

### 1. student-read-only.xml
**Purpose**: Provides read-only access to basic student demographics  
**Use Case**: External reporting systems that need basic student information  
**Includes**: StudentUniqueId, names, birth date, school reference  
**Excludes**: All collections, write access  

### 2. student-write-limited.xml
**Purpose**: Allows limited write access for demographic updates  
**Use Case**: Student information system integration for basic data updates  
**Includes**: Demographics, addresses, electronic mails  
**Excludes**: Assessment data, program associations, identification codes  

### 3. assessment-limited.xml
**Purpose**: Restricts assessment data access to core fields only  
**Use Case**: Assessment vendor with need-to-know access  
**Includes**: Basic assessment results, student reference  
**Excludes**: Accommodations, detailed objectives, performance levels  

### 4. school-minimal.xml
**Purpose**: Public-facing school directory information  
**Use Case**: School finder applications, public directories  
**Includes**: School ID, name, type, operational status, LEA reference  
**Excludes**: Internal administrative data, staff assignments  

### 5. descriptor-full-access.xml
**Purpose**: Full access to descriptor resources  
**Use Case**: Administrative users managing system descriptors  
**Includes**: All descriptor fields (read and write)  
**Excludes**: None  

## Testing Profiles

Each profile can be tested using the provided test scripts and data:

```bash
# Import a profile
curl -X POST \
  https://dms-api/management/v1/profiles/import \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -F "file=@student-read-only.xml"

# Test the profile
curl -X GET \
  https://dms-api/data/v5/ed-fi/students/{id} \
  -H "Accept: application/json;profile=student-read-only" \
  -H "Authorization: Bearer YOUR_TOKEN"
```

## Customizing Profiles

To create your own profile:

1. Copy one of the example files
2. Rename the profile in the `name` attribute
3. Modify the `Resource` name if targeting a different resource
4. Adjust `memberSelection` strategy (IncludeOnly, ExcludeOnly, etc.)
5. Add or remove `Property`, `Collection`, and `Reference` elements
6. Test in non-production environment
7. Import to production when validated

## Profile Naming Guidelines

- Use descriptive names that indicate purpose
- Include resource name if profile is resource-specific
- Add version suffix for breaking changes (e.g., `-v2`)
- Use kebab-case or PascalCase consistently

Examples:
- ✅ `Student-ReadOnly-Demographics`
- ✅ `Assessment-Vendor-Limited-v2`
- ✅ `School-Public-Directory`
- ❌ `profile1`
- ❌ `temp`

## Validation

Before importing to production:

1. **XML Validation**: Ensure XML is well-formed
   ```bash
   xmllint --noout student-read-only.xml
   ```

2. **Schema Validation**: Verify against profile schema
   ```bash
   xmllint --schema profile-schema.xsd student-read-only.xml
   ```

3. **Functional Testing**: Test actual API behavior
   - Import to test environment
   - Execute read/write operations
   - Verify filtering works as expected
   - Test error cases

4. **Performance Testing**: Measure impact
   - Compare response times with/without profile
   - Check cache effectiveness
   - Monitor resource usage

## Documentation

For complete documentation on API Profiles, see:

- [API Profiles Design](../../API-PROFILES-DESIGN.md) - Comprehensive architecture and design
- [API Profiles Quick Start](../../API-PROFILES-QUICKSTART.md) - Getting started guide
- [Ed-Fi API Profiles Specification](https://edfi.atlassian.net/wiki/spaces/EDFICERT/pages/20874540/Profiles) - Official specification

## Contributing

To contribute new example profiles:

1. Create profile XML following the guidelines above
2. Add comprehensive comments explaining purpose and use case
3. Provide test scenarios and expected behavior
4. Update this README with profile description
5. Submit pull request with changes

## Support

For questions or issues with these profiles:
- Review the Quick Start guide
- Check the troubleshooting section in the main documentation
- Post in Ed-Fi community forums
- Contact Ed-Fi support

---

**Last Updated**: 2025-12-09  
**Compatible With**: DMS 1.0+
