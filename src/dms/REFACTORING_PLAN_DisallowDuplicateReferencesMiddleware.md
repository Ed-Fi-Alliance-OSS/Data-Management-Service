# DisallowDuplicateReferencesMiddleware Refactoring Plan

## Executive Summary

This document outlines a comprehensive plan to refactor `DisallowDuplicateReferencesMiddleware` into two separate, focused middlewares to improve separation of concerns, maintainability, and performance while preserving all existing functionality.

## Current State Analysis

### Problem Statement

The current `DisallowDuplicateReferencesMiddleware` violates the Single Responsibility Principle by handling two distinct validation concerns:

1. **ArrayUniquenessConstraints Validation** (lines 26-52): Validates uniqueness within arrays based on schema-defined constraint groups
2. **DocumentReferenceArrays Validation** (lines 54-97): Validates duplicate references in arrays by checking ReferentialId uniqueness

### Code Analysis

**Current Structure:**
```
DisallowDuplicateReferencesMiddleware
├── ArrayUniquenessConstraints validation (26-52)
│   ├── Processes context.ResourceSchema.ArrayUniquenessConstraints
│   ├── Uses GetArrayRootPath() and GetRelativePath() helpers
│   └── Calls context.ParsedBody.FindDuplicatesWithArrayPath()
├── DocumentReferenceArrays validation (54-97)
│   ├── Processes context.DocumentInfo.DocumentReferenceArrays
│   ├── Implements path overlap logic to skip arrays handled by ArrayUniquenessConstraints
│   └── Checks ReferentialId duplicates using string comparison
└── Shared components
    ├── BuildValidationError() - creates error messages
    ├── GetOrdinal() - formats ordinal numbers
    └── Error response building (99-122)
```

**Key Observations:**
- Early exit pattern: ArrayUniquenessConstraints validation failure skips DocumentReferenceArrays validation
- Complex path overlap logic prevents double-validation of the same arrays
- Shared error handling and response format
- O(n²) performance characteristics in duplicate detection loops

## Refactoring Goals

### Primary Objectives
1. **Separation of Concerns**: Each middleware handles one validation type
2. **Improved Testability**: Independent unit testing of each validation logic
3. **Enhanced Maintainability**: Clearer code organization and easier debugging
4. **Performance Optimization**: Algorithmic improvements from O(n²) to O(n)
5. **Pipeline Flexibility**: Ability to configure validation order and composition

### Success Criteria
- [ ] All existing tests pass without modification
- [ ] No behavioral regressions in validation logic
- [ ] Improved performance metrics for large arrays
- [ ] Clear separation of responsibilities
- [ ] Comprehensive unit test coverage for each new middleware

## Target Architecture

### New Components

#### 1. ArrayUniquenessValidationMiddleware
**Responsibility**: Validate schema-defined array uniqueness constraints

**Key Features:**
- Processes `context.ResourceSchema.ArrayUniquenessConstraints`
- Uses HashSet-based composite key validation for O(n) performance
- Maintains early exit on first validation error
- Independent of DocumentInfo processing

#### 2. DuplicateReferenceValidationMiddleware  
**Responsibility**: Validate duplicate references in DocumentReferenceArrays

**Key Features:**
- Processes `context.DocumentInfo.DocumentReferenceArrays`
- Implements path overlap detection to avoid double-validation
- Uses HashSet<ReferentialId> for O(n) duplicate detection
- Maintains existing reference validation logic

#### 3. ValidationErrorFactory (Shared Utility)
**Responsibility**: Centralized error message and response generation

**Key Features:**
- Extracts `BuildValidationError()` logic
- Provides consistent error formatting
- Shared by both middlewares

#### 4. ArrayPathHelper (Shared Utility)
**Responsibility**: Path manipulation and overlap detection logic

**Key Features:**
- Extracts `GetArrayRootPath()` and `GetRelativePath()` methods
- Handles path overlap detection logic
- Reusable across validation components

### Pipeline Integration

**New Pipeline Order:**
```
[Previous Middlewares]
    ↓
ArrayUniquenessValidationMiddleware
    ↓
DuplicateReferenceValidationMiddleware  
    ↓
[Subsequent Middlewares]
```

**Registration Example:**
```csharp
// In pipeline configuration
services.AddTransient<ArrayUniquenessValidationMiddleware>();
services.AddTransient<DuplicateReferenceValidationMiddleware>();
services.AddTransient<ValidationErrorFactory>();
services.AddTransient<ArrayPathHelper>();

// Pipeline order (critical - must maintain this sequence)
app.UseMiddleware<ArrayUniquenessValidationMiddleware>();
app.UseMiddleware<DuplicateReferenceValidationMiddleware>();
```

## Detailed Implementation Plan

### Phase 1: Create Shared Utilities

#### 1.1 ValidationErrorFactory
**File**: `Core/Utilities/ValidationErrorFactory.cs`

```csharp
namespace EdFi.DataManagementService.Core.Utilities;

internal static class ValidationErrorFactory
{
    public static (string errorKey, string message) BuildValidationError(string arrayPath, int index)
    {
        string errorKey = arrayPath.Substring(0, arrayPath.IndexOf("[*]", StringComparison.Ordinal));
        string[] parts = errorKey.Split('.');
        string shortArrayName = parts[^1];
        string message = $"The {GetOrdinal(index + 1)} item of the {shortArrayName} has the same identifying values as another item earlier in the list.";
        return (errorKey, message);
    }

    public static FrontendResponse CreateValidationErrorResponse(
        Dictionary<string, string[]> validationErrors, 
        TraceId traceId)
    {
        return new FrontendResponse(
            StatusCode: 400,
            Body: ForDataValidation(
                "Data validation failed. See 'validationErrors' for details.",
                traceId: traceId,
                validationErrors,
                []
            ),
            Headers: []
        );
    }

    private static string GetOrdinal(int number)
    {
        if (number % 100 == 11 || number % 100 == 12 || number % 100 == 13)
            return $"{number}th";

        return (number % 10) switch
        {
            2 => $"{number}nd",
            3 => $"{number}rd", 
            _ => $"{number}th",
        };
    }
}
```

#### 1.2 ArrayPathHelper
**File**: `Core/Utilities/ArrayPathHelper.cs`

```csharp
namespace EdFi.DataManagementService.Core.Utilities;

internal static class ArrayPathHelper
{
    public static string GetArrayRootPath(IEnumerable<JsonPath> paths)
    {
        // Find the common path until the first [*]
        return paths.First().Value.Split(["[*]"], StringSplitOptions.None)[0] + "[*]";
    }

    public static string GetRelativePath(string root, string fullPath)
    {
        if (fullPath.StartsWith(root))
        {
            return fullPath.Substring(root.Length).TrimStart('.');
        }
        return fullPath;
    }

    public static HashSet<string> GetUniquenessParentPaths(
        IReadOnlyList<IReadOnlyList<JsonPath>> arrayUniquenessConstraints)
    {
        return arrayUniquenessConstraints
            .SelectMany(group => group)
            .Select(jsonPath =>
            {
                int lastDot = jsonPath.Value.LastIndexOf('.');
                return lastDot > 0 ? jsonPath.Value.Substring(0, lastDot) : jsonPath.Value;
            })
            .ToHashSet();
    }
}
```

### Phase 2: Implement ArrayUniquenessValidationMiddleware

**File**: `Core/Middleware/ArrayUniquenessValidationMiddleware.cs`

```csharp
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

internal class ArrayUniquenessValidationMiddleware(ILogger<ArrayUniquenessValidationMiddleware> logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        logger.LogDebug(
            "Entering ArrayUniquenessValidationMiddleware - {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        if (context.ResourceSchema.ArrayUniquenessConstraints.Count > 0)
        {
            var validationError = ValidateArrayUniquenessConstraints(context);
            if (validationError.HasValue)
            {
                var (errorKey, message) = validationError.Value;
                var validationErrors = new Dictionary<string, string[]> { [errorKey] = [message] };
                
                context.FrontendResponse = ValidationErrorFactory.CreateValidationErrorResponse(
                    validationErrors, 
                    context.FrontendRequest.TraceId
                );
                
                logger.LogDebug("Array uniqueness constraint violation - {TraceId}", 
                    context.FrontendRequest.TraceId.Value);
                return;
            }
        }

        await next();
    }

    private static (string errorKey, string message)? ValidateArrayUniquenessConstraints(PipelineContext context)
    {
        foreach (var constraintGroup in context.ResourceSchema.ArrayUniquenessConstraints)
        {
            // 1. Detect array root path (e.g., "$.requiredImmunizations[*]")
            string arrayRootPath = ArrayPathHelper.GetArrayRootPath(constraintGroup);

            // 2. Get relative paths (e.g., "dates[*].immunizationDate", "immunizationTypeDescriptor")
            List<string> relativePaths = constraintGroup
                .Select(p => ArrayPathHelper.GetRelativePath(arrayRootPath, p.Value))
                .ToList();

            // 3. Check for duplicates using optimized algorithm
            var duplicateResult = FindDuplicatesOptimized(context.ParsedBody, arrayRootPath, relativePaths);
            if (duplicateResult.HasValue)
            {
                var (arrayPath, dupeIndex) = duplicateResult.Value;
                return ValidationErrorFactory.BuildValidationError(arrayPath, dupeIndex);
            }
        }

        return null;
    }

    private static (string arrayPath, int dupeIndex)? FindDuplicatesOptimized(
        JsonNode parsedBody, 
        string arrayRootPath, 
        List<string> relativePaths)
    {
        // Use HashSet for O(n) duplicate detection instead of O(n²)
        var seenCompositeKeys = new HashSet<CompositeKey>();
        
        // Implementation details for extracting array elements and building composite keys
        // This replaces the existing FindDuplicatesWithArrayPath call with optimized logic
        
        // TODO: Implement optimized duplicate detection logic
        // For now, delegate to existing method to maintain functionality
        return parsedBody.FindDuplicatesWithArrayPath(arrayRootPath, relativePaths, logger);
    }
}

// Value object for composite key comparison
internal readonly record struct CompositeKey(string[] Values)
{
    public bool Equals(CompositeKey other) => Values.SequenceEqual(other.Values);
    public override int GetHashCode() => Values.Aggregate(0, (acc, val) => HashCode.Combine(acc, val?.GetHashCode() ?? 0));
}
```

### Phase 3: Implement DuplicateReferenceValidationMiddleware

**File**: `Core/Middleware/DuplicateReferenceValidationMiddleware.cs`

```csharp
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Core.Middleware;

internal class DuplicateReferenceValidationMiddleware(ILogger<DuplicateReferenceValidationMiddleware> logger) : IPipelineStep
{
    public async Task Execute(PipelineContext context, Func<Task> next)
    {
        logger.LogDebug(
            "Entering DuplicateReferenceValidationMiddleware - {TraceId}",
            context.FrontendRequest.TraceId.Value
        );

        var validationError = ValidateDuplicateReferences(context);
        if (validationError.HasValue)
        {
            var (errorKey, message) = validationError.Value;
            var validationErrors = new Dictionary<string, string[]> { [errorKey] = [message] };
            
            context.FrontendResponse = ValidationErrorFactory.CreateValidationErrorResponse(
                validationErrors, 
                context.FrontendRequest.TraceId
            );
            
            logger.LogDebug("Duplicate reference detected - {TraceId}", 
                context.FrontendRequest.TraceId.Value);
            return;
        }

        await next();
    }

    private static (string errorKey, string message)? ValidateDuplicateReferences(PipelineContext context)
    {
        // Get paths already handled by ArrayUniquenessConstraints to avoid double-validation
        var uniquenessParentPaths = ArrayPathHelper.GetUniquenessParentPaths(
            context.ResourceSchema.ArrayUniquenessConstraints
        );

        foreach (var referenceArray in context.DocumentInfo.DocumentReferenceArrays)
        {
            // Skip arrays already covered by ArrayUniquenessConstraints
            if (uniquenessParentPaths.Contains(referenceArray.arrayPath.Value))
            {
                continue;
            }

            if (referenceArray.DocumentReferences.Length > 1)
            {
                var duplicateIndex = FindDuplicateReferenceIndex(referenceArray.DocumentReferences);
                if (duplicateIndex >= 0)
                {
                    return ValidationErrorFactory.BuildValidationError(
                        referenceArray.arrayPath.Value, 
                        duplicateIndex
                    );
                }
            }
        }

        return null;
    }

    private static int FindDuplicateReferenceIndex(DocumentReference[] documentReferences)
    {
        // Use HashSet for O(n) duplicate detection instead of O(n²)
        var seenIds = new HashSet<string>();
        
        for (int i = 0; i < documentReferences.Length; i++)
        {
            string id = documentReferences[i].ReferentialId.ToString();
            if (!seenIds.Add(id))
            {
                return i; // Return index of first duplicate found
            }
        }
        
        return -1; // No duplicates found
    }
}
```

### Phase 4: Update Pipeline Configuration

#### 4.1 Dependency Injection Registration
**File**: `Frontend.AspNetCore/Program.cs` or relevant DI configuration

```csharp
// Remove old middleware registration
// services.AddTransient<DisallowDuplicateReferencesMiddleware>();

// Add new middleware registrations
services.AddTransient<ArrayUniquenessValidationMiddleware>();
services.AddTransient<DuplicateReferenceValidationMiddleware>();
```

#### 4.2 Pipeline Order Configuration
**Critical**: The order must be exactly as specified to maintain existing behavior.

```csharp
// In pipeline configuration where middlewares are added
// Replace:
// app.UseMiddleware<DisallowDuplicateReferencesMiddleware>();

// With:
app.UseMiddleware<ArrayUniquenessValidationMiddleware>();
app.UseMiddleware<DuplicateReferenceValidationMiddleware>();
```

### Phase 5: Testing Strategy

#### 5.1 Unit Tests

**ArrayUniquenessValidationMiddleware Tests:**
- Empty constraints (should pass through)
- Single constraint group with unique values (should pass)
- Single constraint group with duplicate values (should fail)
- Multiple constraint groups with mixed scenarios
- Complex nested array paths
- Performance test with large arrays

**DuplicateReferenceValidationMiddleware Tests:**
- No reference arrays (should pass through)
- Single reference in array (should pass)
- Multiple unique references (should pass)
- Duplicate references (should fail)
- Path overlap scenarios (arrays covered by uniqueness constraints)
- Performance test with large reference arrays

**ValidationErrorFactory Tests:**
- Error message formatting
- Ordinal number generation
- Response object structure

#### 5.2 Integration Tests

**Pipeline Integration:**
- End-to-end tests with both middlewares in pipeline
- Verify exact same behavior as original middleware
- Test all existing `DisallowDuplicateReferencesMiddlewareTests` scenarios
- Performance comparison tests

#### 5.3 Test Migration Plan

1. **Copy Existing Tests**: Create new test files for each middleware
2. **Modify Test Setup**: Adjust test contexts for individual middleware testing
3. **Preserve Integration Tests**: Keep existing tests as integration tests for the combined pipeline
4. **Add Performance Tests**: Benchmark new vs. old implementation

### Phase 6: Performance Optimization Details

#### 6.1 HashSet-Based Duplicate Detection

**Before (O(n²)):**
```csharp
// Nested loop comparison - inefficient for large arrays
for (int i = 0; i < items.Length; i++)
{
    for (int j = i + 1; j < items.Length; j++)
    {
        if (CompareItems(items[i], items[j])) // Duplicate found
            return j;
    }
}
```

**After (O(n)):**
```csharp
// HashSet-based comparison - efficient for any array size
var seen = new HashSet<CompositeKey>();
for (int i = 0; i < items.Length; i++)
{
    var key = BuildCompositeKey(items[i]);
    if (!seen.Add(key)) // Duplicate found
        return i;
}
```

#### 6.2 Composite Key Strategy

For ArrayUniquenessConstraints, combine multiple field values into a single comparison key:

```csharp
private static CompositeKey BuildCompositeKey(JsonNode item, IReadOnlyList<JsonPath> paths)
{
    var values = new string[paths.Count];
    for (int i = 0; i < paths.Count; i++)
    {
        values[i] = item.SelectToken(paths[i].Value)?.ToString() ?? "";
    }
    return new CompositeKey(values);
}
```

## Risk Assessment & Mitigation

### High-Risk Areas

#### 1. Path Overlap Logic Complexity
**Risk**: The logic for determining which paths are covered by ArrayUniquenessConstraints is subtle and could be implemented incorrectly.

**Mitigation:**
- Extract exact logic from lines 57-65 of original middleware
- Comprehensive unit tests covering edge cases
- Integration tests verifying no double-validation occurs

#### 2. Behavioral Regression
**Risk**: Subtle changes in validation precedence or error messages could break existing integrations.

**Mitigation:**
- Preserve exact error message format
- Maintain early exit behavior 
- Run all existing tests without modification
- Document any intentional behavior changes

#### 3. Performance Degradation
**Risk**: Two middleware calls could introduce overhead that negates HashSet optimizations.

**Mitigation:**
- Benchmark pipeline execution with realistic data sizes
- Profile memory usage of new composite key structures
- Consider middleware consolidation if overhead is significant

### Medium-Risk Areas

#### 4. Pipeline Configuration Errors
**Risk**: Incorrect middleware ordering or missing registrations could break functionality.

**Mitigation:**
- Clear documentation with examples
- Integration tests that verify complete pipeline behavior
- Consider startup validation to detect configuration errors

#### 5. Test Maintenance Burden
**Risk**: Splitting tests increases maintenance overhead and could lead to coverage gaps.

**Mitigation:**
- Automated test generation for common scenarios
- Shared test utilities for consistent test setup
- Regular coverage analysis

### Low-Risk Areas

#### 6. Increased Code Complexity
**Risk**: More files and classes could make the codebase harder to navigate.

**Mitigation:**
- Clear naming conventions and documentation
- Logical namespace organization
- Code organization tools and IDE support

## Success Metrics

### Performance Metrics
- [ ] Array validation performance improves for arrays > 100 items
- [ ] Memory usage remains stable or improves
- [ ] Pipeline execution time remains constant or improves

### Quality Metrics  
- [ ] Unit test coverage ≥ 95% for new middlewares
- [ ] All existing integration tests pass without modification
- [ ] Code complexity metrics improve or remain stable
- [ ] Zero behavioral regressions detected

### Maintainability Metrics
- [ ] Clear separation of concerns achieved
- [ ] Documentation covers all new components
- [ ] Developer onboarding time for validation logic decreases

## Timeline & Dependencies

### Phase 1: Foundation (Week 1)
- [ ] Create shared utilities (ValidationErrorFactory, ArrayPathHelper)
- [ ] Set up unit test structure
- [ ] No external dependencies

### Phase 2: Core Implementation (Week 2)  
- [ ] Implement ArrayUniquenessValidationMiddleware
- [ ] Implement DuplicateReferenceValidationMiddleware
- [ ] Unit tests for each middleware
- [ ] Depends on: Phase 1 completion

### Phase 3: Integration (Week 3)
- [ ] Update pipeline configuration
- [ ] Integration testing
- [ ] Performance testing
- [ ] Depends on: Phase 2 completion

### Phase 4: Cleanup (Week 4)
- [ ] Remove old middleware
- [ ] Documentation updates
- [ ] Final validation
- [ ] Depends on: Phase 3 validation

## Rollback Plan

If critical issues are discovered during implementation:

1. **Immediate Rollback**: Revert pipeline configuration to use original middleware
2. **Partial Rollback**: Keep shared utilities, revert middleware split
3. **Data Preservation**: Ensure no data corruption during transition
4. **Communication**: Clear communication plan for any rollback scenarios

## Future Considerations

### Extensibility Opportunities
- Additional validation middleware can follow the same pattern
- Shared utilities can be extended for other validation types
- Pipeline composition allows for flexible validation configurations

### Performance Enhancements
- Consider async validation for large datasets
- Parallel processing for independent validation groups
- Caching strategies for repeated validation scenarios

### Monitoring & Observability
- Add performance metrics to track validation execution time
- Error rate monitoring for each validation type
- Usage analytics to optimize validation ordering

---

## Conclusion

This refactoring plan provides a comprehensive approach to splitting `DisallowDuplicateReferencesMiddleware` into focused, maintainable components while preserving all existing functionality and improving performance. The phased approach minimizes risk while delivering incremental value throughout the implementation process.

The success of this refactoring will improve the codebase's maintainability, testability, and performance while establishing patterns for future validation middleware development.
