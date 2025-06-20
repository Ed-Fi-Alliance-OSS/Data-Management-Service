# ArrayUniquenessConstraints Schema Changes: MetaEd-js to DMS

## Overview

The `arrayUniquenessConstraints` property in the API schema has undergone significant structural changes in MetaEd-js that are incompatible with the current Data Management Service (DMS) implementation. This document details the changes and provides examples from both the old and new formats.

## Executive Summary

**Current DMS Format (Old):**
- Simple 2D array structure: `string[][]`
- Each constraint group is an array of JSON path strings
- No support for nested array validation
- Flat structure suitable only for simple array uniqueness

**New MetaEd-js Format:**
- Object-based structure with optional properties
- Support for simple arrays via `paths` property
- Support for nested arrays via `nestedConstraints` property  
- Hierarchical structure allowing complex validation scenarios

## Detailed Format Comparison

### Current DMS ResourceSchema Implementation

**C# Property Definition:**
```csharp
// EdFi.DataManagementService.Core/ApiSchema/ResourceSchema.cs:485
public IReadOnlyList<IReadOnlyList<JsonPath>> ArrayUniquenessConstraints =>
    _arrayUniquenessConstraints.Value;
```

**Expected JSON Structure:**
```json
{
  "arrayUniquenessConstraints": [
    [
      "$.performanceLevels[*].assessmentReportingMethodDescriptor",
      "$.performanceLevels[*].performanceLevelDescriptor"
    ],
    [
      "$.items[*].assessmentItemReference.assessmentIdentifier",
      "$.items[*].assessmentItemReference.identificationCode", 
      "$.items[*].assessmentItemReference.namespace"
    ]
  ]
}
```

### New MetaEd-js ArrayUniquenessConstraint Format

**TypeScript Type Definition:**
```typescript
// ArrayUniquenessConstraint.ts
export type ArrayUniquenessConstraint = {
  // Parent array path (only present for nested constraints)
  basePath?: JsonPath;

  // Scalar paths on an array ($.XYZ[*].something)
  paths?: JsonPath[];

  // Nested ArrayUniquenessConstraints for nested arrays
  nestedConstraints?: ArrayUniquenessConstraint[];
};
```

**New JSON Structure:**
```json
{
  "arrayUniquenessConstraints": [
    {
      "paths": [
        "$.requiredStringProperties[*].requiredStringProperty"
      ]
    }
  ]
}
```

## Examples from MetaEd-js Tests

### 1. Simple Array Constraints
**Source:** Simple domain entity with collections
```json
[
  {
    "paths": [
      "$.requiredStringProperties[*].requiredStringProperty"
    ]
  }
]
```

### 2. Multiple Simple Arrays (Separate Constraints)
**Source:** Entity with two different array collections
```json
[
  {
    "paths": [
      "$.firstTypes[*].firstTypeDescriptor"
    ]
  },
  {
    "paths": [
      "$.secondTypes[*].secondTypeDescriptor"
    ]
  }
]
```

### 3. Multiple Paths in Single Constraint
**Source:** Staff entity with scalar collection and common collection
```json
[
  {
    "paths": [
      "$.identificationDocuments[*].identificationDocumentUseDescriptor",
      "$.identificationDocuments[*].personalInformationVerificationDescriptor",
      "$.visas[*].visaDescriptor"
    ]
  }
]
```

### 4. Nested Array Constraints (NEW FEATURE)
**Source:** Association with common collection in common collection
```json
[
  {
    "nestedConstraints": [
      {
        "basePath": "$.addresses[*]",
        "paths": [
          "$.periods[*].beginDate"
        ]
      }
    ]
  }
]
```

### 5. Multiple Nested Constraints
**Source:** School entity with multiple nested collections
```json
[
  {
    "nestedConstraints": [
      {
        "basePath": "$.addresses[*]",
        "paths": [
          "$.contacts[*].contactTypeDescriptor"
        ]
      },
      {
        "basePath": "$.addresses[*]",
        "paths": [
          "$.periods[*].beginDate"
        ]
      }
    ]
  }
]
```

### 6. Scalar Collections on Scalar Common
**Source:** StudentTransportation entity
```json
[
  {
    "paths": [
      "$.studentBusDetails.travelDayOfWeeks[*].travelDayOfWeekDescriptor",
      "$.studentBusDetails.travelDirections[*].travelDirectionDescriptor"
    ]
  }
]
```

## Key Structural Differences

| Aspect | Old Format (DMS) | New Format (MetaEd-js) |
|--------|------------------|------------------------|
| **Root Structure** | `string[][]` | `ArrayUniquenessConstraint[]` |
| **Constraint Definition** | Array of strings | Object with optional properties |
| **Simple Arrays** | `["$.path[*].field"]` | `{"paths": ["$.path[*].field"]}` |
| **Multiple Paths** | `["$.path1[*].field1", "$.path2[*].field2"]` | `{"paths": ["$.path1[*].field1", "$.path2[*].field2"]}` |
| **Nested Arrays** | Not supported | `{"nestedConstraints": [...]}` |
| **Base Path** | Implicit in each path | Explicit `basePath` property |

## Impact on DMS Components

### 1. ResourceSchema.cs
**Current Implementation:**
```csharp
private readonly Lazy<IReadOnlyList<IReadOnlyList<JsonPath>>> _arrayUniquenessConstraints = new(() =>
{
    var outerArray = _resourceSchemaNode["arrayUniquenessConstraints"]!.AsArray();
    return outerArray
        .Select(innerJsonElement =>
            innerJsonElement!
                .AsArray()
                .Select(pathElement => new JsonPath(pathElement!.GetValue<string>()!))
                .ToList()
                .AsReadOnly()
        )
        .ToList()
        .AsReadOnly();
});
```

**Required Changes:** Complete redesign to handle the new object structure with optional properties.

### 2. ArrayUniquenessValidationMiddleware.cs
**Current Implementation:**
```csharp
foreach (var constraintGroup in context.ResourceSchema.ArrayUniquenessConstraints)
{
    string arrayRootPath = ArrayPathHelper.GetArrayRootPath(constraintGroup);
    List<string> relativePaths = constraintGroup
        .Select(p => ArrayPathHelper.GetRelativePath(arrayRootPath, p.Value))
        .ToList();
    // ...
}
```

**Required Changes:** Must handle both `paths` and `nestedConstraints` properties, with proper recursion for nested validation.

### 3. ApiSchemaBuilder.cs (Test Helper)
**Current Implementation:**
```csharp
public ApiSchemaBuilder WithArrayUniquenessConstraints(List<string> constraints)
{
    var jsonArray = new JsonArray(constraints.Select(s => JsonValue.Create(s)!).ToArray());
    // ...
}
```

**Required Changes:** New methods to build the object-based structure with support for nested constraints.

## Migration Strategy

### Phase 1: Add New Model Classes
1. Create `ArrayUniquenessConstraint` class
2. Add support for `basePath`, `paths`, and `nestedConstraints` properties
3. The Simple 2D array structure is replaced

### Phase 2: Update ResourceSchema Parser
1. Modify parsing logic to handle both old and new formats
2. Convert old format to new internal representation
3. Add validation for new structure integrity

### Phase 3: Update Validation Logic
1. Enhance `ArrayUniquenessValidationMiddleware` to process new structure
2. Add recursive validation for `nestedConstraints`
3. Update path resolution logic for `basePath` handling

### Phase 4: Update Test Infrastructure
1. Enhance `ApiSchemaBuilder` with new constraint building methods
2. Update all existing tests to use new format
3. Add tests for nested constraint scenarios

## Example Migration for Current Tests

### Before (Current DMS Test):
```csharp
var schemaDocuments = new ApiSchemaBuilder()
    .WithStartProject()
    .WithStartResource("Assessment") 
    .WithStartArrayUniquenessConstraints()
    .WithArrayUniquenessConstraints([
        "$.performanceLevels[*].assessmentReportingMethodDescriptor",
        "$.performanceLevels[*].performanceLevelDescriptor"
    ])
    .WithEndArrayUniquenessConstraints()
    .WithEndResource()
    .WithEndProject()
    .ToApiSchemaDocuments();
```

### After (New Format):
```csharp
var schemaDocuments = new ApiSchemaBuilder()
    .WithStartProject()
    .WithStartResource("Assessment")
    .WithArrayUniquenessConstraint(new ArrayUniquenessConstraint
    {
        Paths = [
            "$.performanceLevels[*].assessmentReportingMethodDescriptor",
            "$.performanceLevels[*].performanceLevelDescriptor"
        ]
    })
    .WithEndResource() 
    .WithEndProject()
    .ToApiSchemaDocuments();
```

## Benefits of New Format

1. **Nested Array Support**: Enables validation of arrays within arrays
2. **Clearer Structure**: Explicit separation of base paths and validation paths
3. **Extensibility**: Object structure allows for future enhancements
4. **Type Safety**: Better TypeScript/C# type definitions
5. **Hierarchical Validation**: Supports complex Ed-Fi data structures

## Breaking Changes Summary

⚠️ **Critical Breaking Changes:**
- JSON structure completely changed from `string[][]` to `ArrayUniquenessConstraint[]`
- All parsing code must be rewritten
- Test infrastructure requires comprehensive updates
- Validation logic needs enhancement for nested scenarios

The migration requires coordinated changes across multiple components and cannot be done incrementally without supporting both formats during a transition period.
