# Profile XML Schema (XSD)

This directory will contain the XSD schema file for validating Ed-Fi API Profile XML files.

## Schema Source

The Profile XSD schema is sourced from [AdminAPI-2.x](https://github.com/Ed-Fi-Alliance-OSS/AdminAPI-2.x) to ensure compatibility with existing profile definitions.

## Implementation Note

During implementation (Task 1 in the implementation tasks), the XSD schema file will be:

1. Extracted from AdminAPI-2.x repository
2. Placed in this directory as `ProfileSchema.xsd`
3. Integrated into DMS for XML validation at profile load time
4. Referenced in documentation for profile authors

## Schema Location in DMS Codebase

The operational schema file will be located at:
```
src/dms/core/EdFi.DataManagementService.Core/Profiles/Schema/ProfileSchema.xsd
```

This reference copy is for documentation and testing purposes only.

## Validating Profile XML

Once the schema is in place, profiles can be validated using:

### Using xmllint (Linux/Mac)
```bash
xmllint --noout --schema ProfileSchema.xsd YourProfile.xml
```

### Using Visual Studio / VS Code with XML Extensions
The XSD schema can be referenced in profile XML files for automatic validation:

```xml
<?xml version="1.0" encoding="utf-8"?>
<Profile 
  xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
  xsi:noNamespaceSchemaLocation="ProfileSchema.xsd"
  name="YourProfile">
  ...
</Profile>
```

## Related Documentation

- [API Profiles Design](../../design/api-profiles-design.md)
- [Profile Examples README](../README.md)
- [AdminAPI-2.x Profiles](https://github.com/Ed-Fi-Alliance-OSS/AdminAPI-2.x)
