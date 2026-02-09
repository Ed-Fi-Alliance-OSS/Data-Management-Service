# Profile Validation Rules

## Overview

This document describes the validation rules and behavior for Ed-Fi API profiles in the Data Management Service (DMS).

## Validation Severity Levels

### Errors

Errors prevent profile loading entirely. Profiles with errors are not added to the profile cache and cannot be used for API filtering.

### Warnings

Warnings allow profile loading but log issues. Profiles with warnings are still usable for API filtering, but problems are noted in logs.

## Validation Rules by Profile Mode

### IncludeOnly Mode

IncludeOnly profiles specify exactly which data elements are allowed. All references must exist in the API schema.

#### Resource Validation

- **Rule**: Referenced resources must exist in the API schema
- **Severity**: Error
- **Example**: `<Resource name="School">` must match an existing resource

#### Property Validation

- **Rule**: Referenced properties must exist in the resource schema
- **Severity**: Error
- **Example**: `<Property name="schoolId">` must exist in the School resource

#### Extension Validation

- **Rule**: Referenced extensions must exist in the resource schema
- **Severity**: Error
- **Example**: `<Property name="extensionField">` in an extension must exist

#### Nested Object Validation

- **Rule**: Referenced nested object properties must exist
- **Severity**: Error
- **Example**: `<Property name="address.city">` must exist in the nested address object

#### Collection Validation

- **Rule**: Referenced collection properties must exist
- **Severity**: Error
- **Example**: `<Property name="students.studentId">` must exist in the students collection

### ExcludeOnly Mode

ExcludeOnly profiles specify which data elements are excluded. Non-existent references generate warnings rather than errors.

#### Resource Validation

- **Rule**: Referenced resources must exist in the API schema
- **Severity**: Error
- **Example**: `<Resource name="School">` must match an existing resource

#### Property Validation

- **Rule**: Referenced properties may not exist (logged as warning)
- **Severity**: Warning
- **Example**: `<Property name="nonExistentField">` generates a warning but allows profile loading

#### Extension Validation

- **Rule**: Referenced extensions may not exist (logged as warning)
- **Severity**: Warning
- **Example**: `<Property name="extensionField">` in a non-existent extension generates a warning

#### Nested Object Validation

- **Rule**: Referenced nested properties may not exist (logged as warning)
- **Severity**: Warning
- **Example**: `<Property name="address.nonExistentCity">` generates a warning

#### Collection Validation

- **Rule**: Referenced collection properties may not exist (logged as warning)
- **Severity**: Warning
- **Example**: `<Property name="students.nonExistentId">` generates a warning

#### Identity Member Exclusion

- **Rule**: Identity members cannot be excluded
- **Severity**: Warning
- **Example**: `<Property name="schoolId">` (identity) cannot be excluded - logs a warning but profile still loads

## General Validation Rules

### Empty Profiles

- **Rule**: Profiles with no resource definitions are valid
- **Severity**: Valid (no issues)
- **Example**: `<Profile name="EmptyProfile"></Profile>` is allowed

### Multiple Resources

- **Rule**: All resources in a profile are validated independently
- **Severity**: Varies by individual resource validation results
- **Example**: A profile with 3 resources validates each resource separately

### Content Types

- **Rule**: Both ReadContentType and WriteContentType are validated if present
- **Severity**: Varies by content type validation results
- **Example**: Read and write filtering rules are validated separately

### Case Sensitivity

- **Rule**: Profile and resource names are case-insensitive for lookup but preserved in definitions
- **Severity**: N/A (handled during profile resolution)
- **Example**: "school" matches "School" resource

## Implementation Details

### Validation Process

1. Profile definition is parsed from XML/JSON
2. Each resource in the profile is validated against the API schema
3. Validation failures are collected with appropriate severity levels
4. Profiles with errors are blocked from loading
5. Profiles with warnings are loaded but issues are logged

### Error Handling

- Validation errors prevent profile caching and usage
- Validation warnings allow profile usage but log issues
- All validation failures include detailed messages with profile, resource, and member context

### Logging

- Errors are logged at Error level with detailed failure information
- Warnings are logged at Warning level with detailed failure information
- Successful validations are logged at Debug level with summary statistics

## Examples

### Valid IncludeOnly Profile

```xml
<Profile name="SchoolBasic">
  <Resource name="School">
    <ReadContentType>
      <Property name="schoolId"/>
      <Property name="nameOfInstitution"/>
    </ReadContentType>
  </Resource>
</Profile>
```

### Invalid IncludeOnly Profile (Error)

```xml
<Profile name="SchoolInvalid">
  <Resource name="School">
    <ReadContentType>
      <Property name="nonExistentField"/>  <!-- Error: property doesn't exist -->
    </ReadContentType>
  </Resource>
</Profile>
```

### Valid ExcludeOnly Profile with Warning

```xml
<Profile name="SchoolExclude">
  <Resource name="School">
    <ReadContentType>
      <Property name="schoolId" memberSelection="ExcludeOnly"/>  <!-- Error: cannot exclude identity -->
      <Property name="nonExistentField" memberSelection="ExcludeOnly"/>  <!-- Warning: field doesn't exist -->
    </ReadContentType>
  </Resource>
</Profile>
```

## Testing

Profile validation is tested through:

- Unit tests for individual validation rules
- Integration tests for profile loading behavior
- E2E tests for API filtering with validated profiles

See the test files in `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Profile/` for detailed test coverage.
