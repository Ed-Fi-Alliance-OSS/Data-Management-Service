# Technical Debt: Properly Scope Middleware Services

## Status
- **Created**: 2025-10-16
- **Priority**: Medium
- **Estimated Effort**: 2-3 days
- **Risk Level**: Medium-High

## Problem Statement

### Current Architecture Issue

The DMS middleware pipeline currently has a service lifetime mismatch that requires workarounds:

1. **Middleware classes are registered as Singletons** in the DI container
2. **They receive scoped services** (like `IDmsInstanceSelection`) through constructor injection
3. This violates DI best practices: **Singletons should not capture scoped dependencies**

### Why This Exists

The middleware pipeline pattern requires middleware steps to be registered as singletons because:
- They are resolved once at application startup
- They form a long-lived pipeline that processes all requests
- The `IPipelineStep` interface doesn't have built-in support for scoped service resolution

### Current Workaround

We implemented caching workarounds to make this pattern work:

**NpgsqlDataSourceProvider.cs** (lines 22-54):
- Caches data sources in a `Dictionary<long, NpgsqlDataSource>`
- Must call `GetSelectedDmsInstance()` on every property access to check which instance is active
- More complex than necessary because the service can't rely on true scoped behavior

**ResolveDmsInstanceMiddleware.cs**:
- Receives `IDmsInstanceSelection` via constructor (captured at startup)
- Must call `SetSelectedDmsInstance()` to modify state on the scoped service
- The scoped service isn't truly scoped - it's a singleton pretending to be scoped

### Problems This Causes

1. **Architectural confusion**: Services marked as "scoped" aren't actually scoped
2. **Performance overhead**: Repeated calls to `GetSelectedDmsInstance()` for validation
3. **Maintenance burden**: More complex code than necessary
4. **Risk of bugs**: Easy to accidentally cache state that should be per-request
5. **Testing complexity**: Tests must account for the caching behavior

## Proposed Solution: Resolve Scoped Services Per-Request

### High-Level Approach

Change middleware to resolve scoped dependencies during `Execute()` instead of the constructor, ensuring true per-request scoping.

### Implementation Plan

#### Phase 1: Add Service Resolution Support to Middleware

**File**: `src/dms/core/EdFi.DataManagementService.Core/Middleware/ResolveDmsInstanceMiddleware.cs`

Change from constructor injection to runtime resolution:

```csharp
// BEFORE (current)
internal class ResolveDmsInstanceMiddleware(
    IDmsInstanceProvider dmsInstanceProvider,
    IDmsInstanceSelection dmsInstanceSelection,  // ❌ Captured at startup
    ILogger<ResolveDmsInstanceMiddleware> logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        // Uses captured dmsInstanceSelection
        dmsInstanceSelection.SetSelectedDmsInstance(matchedInstance);
        await next();
    }
}

// AFTER (proposed)
internal class ResolveDmsInstanceMiddleware(
    IDmsInstanceProvider dmsInstanceProvider,
    IServiceProvider serviceProvider,  // ✅ Resolve per-request
    ILogger<ResolveDmsInstanceMiddleware> logger
) : IPipelineStep
{
    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        // Resolve scoped service for this request
        var dmsInstanceSelection = serviceProvider.GetRequiredService<IDmsInstanceSelection>();

        // Use the truly scoped service
        dmsInstanceSelection.SetSelectedDmsInstance(matchedInstance);
        await next();
    }
}
```

#### Phase 2: Update Other Affected Middleware

Apply the same pattern to any other middleware that uses scoped services:

1. **Audit all middleware** in `src/dms/core/EdFi.DataManagementService.Core/Middleware/`
2. Identify any that receive scoped services via constructor
3. Update them to use `IServiceProvider` for runtime resolution

#### Phase 3: Simplify NpgsqlDataSourceProvider

**File**: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/NpgsqlDataSourceProvider.cs`

Once `IDmsInstanceSelection` is truly scoped, simplify the provider:

```csharp
// BEFORE (current) - Complex caching needed
public sealed class NpgsqlDataSourceProvider(
    IDmsInstanceSelection dmsInstanceSelection,
    NpgsqlDataSourceCache dataSourceCache,
    ILogger<NpgsqlDataSourceProvider> logger
)
{
    private readonly Dictionary<long, NpgsqlDataSource> _cachedDataSources = new();

    public NpgsqlDataSource DataSource
    {
        get
        {
            var selectedInstance = dmsInstanceSelection.GetSelectedDmsInstance();

            if (_cachedDataSources.TryGetValue(selectedInstance.Id, out var cachedDataSource))
            {
                return cachedDataSource;
            }

            var dataSource = dataSourceCache.GetOrCreate(selectedInstance.ConnectionString!);
            _cachedDataSources[selectedInstance.Id] = dataSource;
            return dataSource;
        }
    }
}

// AFTER (proposed) - Simple per-request behavior
public sealed class NpgsqlDataSourceProvider(
    IDmsInstanceSelection dmsInstanceSelection,
    NpgsqlDataSourceCache dataSourceCache,
    ILogger<NpgsqlDataSourceProvider> logger
)
{
    private NpgsqlDataSource? _cachedDataSource;

    public NpgsqlDataSource DataSource
    {
        get
        {
            // Service is truly scoped, so selectedInstance won't change during request
            if (_cachedDataSource != null)
            {
                return _cachedDataSource;
            }

            var selectedInstance = dmsInstanceSelection.GetSelectedDmsInstance();
            _cachedDataSource = dataSourceCache.GetOrCreate(selectedInstance.ConnectionString!);
            return _cachedDataSource;
        }
    }
}
```

#### Phase 4: Update Service Registration

**File**: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Program.cs`

Verify that:
1. `IDmsInstanceSelection` remains registered as `Scoped`
2. Middleware steps remain registered as `Singleton`
3. No changes needed here - the fix is in how middleware resolves services

#### Phase 5: Update Tests

**Files to update**:
- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/NpgsqlDataSourceProviderTests.cs`

Changes needed:

```csharp
// Test: It_should_cache_data_source_for_same_dms_instance
// BEFORE: Expects GetSelectedDmsInstance() called twice (validation on each access)
A.CallTo(() => _dmsInstanceSelection.GetSelectedDmsInstance()).MustHaveHappenedTwiceExactly();

// AFTER: Expects GetSelectedDmsInstance() called once (true scoping)
A.CallTo(() => _dmsInstanceSelection.GetSelectedDmsInstance()).MustHaveHappenedOnceExactly();
```

**New tests to add**:
1. Test that middleware creates new scope for each request
2. Test that different concurrent requests get different scoped instances
3. Test that scoped instance state doesn't leak between requests

### Files to Modify

```
src/dms/core/EdFi.DataManagementService.Core/Middleware/
  ├── ResolveDmsInstanceMiddleware.cs              [Primary change]
  └── [Any other middleware using scoped services]

src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/
  └── NpgsqlDataSourceProvider.cs                  [Simplification]

src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/
  └── NpgsqlDataSourceProviderTests.cs             [Test updates]

src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/
  └── [Add new middleware scoping tests]
```

## Benefits

### Code Quality
- ✅ **Architecturally correct**: Services marked as scoped are truly scoped
- ✅ **Simpler code**: No need for complex caching workarounds
- ✅ **Fewer bugs**: No risk of accidentally capturing per-request state

### Performance
- ✅ **Fewer method calls**: `GetSelectedDmsInstance()` called once per request instead of repeatedly
- ✅ **Better memory usage**: No Dictionary overhead in NpgsqlDataSourceProvider

### Maintainability
- ✅ **Clearer intent**: Service lifetimes match their actual behavior
- ✅ **Easier testing**: Can test true scoping behavior
- ✅ **Standard patterns**: Follows ASP.NET Core best practices

## Risks and Mitigations

### Risk 1: Breaking Change to Middleware Pattern
**Likelihood**: Medium
**Impact**: High
**Mitigation**:
- Comprehensive testing before/after
- Review all middleware for similar patterns
- Document the new pattern for future middleware

### Risk 2: Performance Regression
**Likelihood**: Low
**Impact**: Medium
**Mitigation**:
- Service resolution is fast (microseconds)
- Actually reduces overhead by eliminating repeated validation calls
- Load test before/after to verify

### Risk 3: Subtle Concurrency Issues
**Likelihood**: Low
**Impact**: High
**Mitigation**:
- Add specific tests for concurrent request handling
- Review service lifetime of all dependencies
- Consider using AsyncLocal if needed for request context

### Risk 4: Missing Scoped Services in Pipeline
**Likelihood**: Medium
**Impact**: Medium
**Mitigation**:
- Audit all middleware classes for scoped service usage
- Create checklist of classes to update
- Update in single PR to avoid partial state

## Testing Strategy

### Unit Tests
1. **Middleware scoping tests**
   - Verify new IDmsInstanceSelection created per Execute() call
   - Verify no state leakage between calls

2. **NpgsqlDataSourceProvider tests**
   - Update existing cache tests for new behavior
   - Verify GetSelectedDmsInstance() called once per property access series
   - Verify data source correctly cached per instance

### Integration Tests
1. **Multi-request tests**
   - Send requests to different DMS instances concurrently
   - Verify correct instance used for each request
   - Verify no cross-request contamination

2. **E2E tests**
   - Run existing E2E test suite
   - All tests should pass with no behavior changes

### Performance Tests
1. **Baseline current performance**
   - Measure requests/second for single instance
   - Measure requests/second for multi-instance routing

2. **Compare after changes**
   - Should be equal or better (fewer validation calls)

## Implementation Checklist

- [ ] Create feature branch for this work
- [ ] Audit all middleware for scoped service usage
- [ ] Update ResolveDmsInstanceMiddleware to use IServiceProvider
- [ ] Update other affected middleware classes
- [ ] Simplify NpgsqlDataSourceProvider
- [ ] Update existing unit tests
- [ ] Add new scoping tests
- [ ] Run full unit test suite (all must pass)
- [ ] Run full E2E test suite (all must pass)
- [ ] Performance test and compare with baseline
- [ ] Code review focusing on service lifetimes
- [ ] Update documentation on middleware patterns
- [ ] Merge to main

## Alternative Approaches Considered

### Alternative 1: Keep Current Workaround
**Verdict**: Not recommended
**Reason**: Technical debt accumulates; confusing architecture

### Alternative 2: Make Middleware Scoped
**Verdict**: Not feasible
**Reason**: Pipeline pattern requires singleton middleware; would require major pipeline refactor

### Alternative 3: Use AsyncLocal for Request Context
**Verdict**: Possible but more complex
**Reason**: AsyncLocal adds magic; IServiceProvider approach is more explicit and standard

## References

- [ASP.NET Core Dependency Injection Best Practices](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection-guidelines)
- [Service Lifetimes](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection#service-lifetimes)
- [Middleware Activation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/extensibility)

## Related Issues

- [Link to issue about multi-instance support implementation]
- [Link to issue about NpgsqlDataSourceProvider caching]

## Notes

- This is a refactoring task with no external behavior changes
- All existing tests should continue to pass (with minor assertion updates)
- Consider this work when adding new middleware that needs scoped services
- Good candidate for a focused sprint story (not mixed with feature work)
