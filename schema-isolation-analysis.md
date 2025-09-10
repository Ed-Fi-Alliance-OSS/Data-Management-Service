# Schema-Based Test Isolation: Exhaustive Analysis

## Executive Summary

This document provides a comprehensive analysis of implementing schema-based database isolation to enable parallel E2E test execution for the Ed-Fi Data Management Service (DMS) and Configuration Management Service (CMS). The approach would allow tests to run 4-8x faster locally while maintaining compatibility with GitHub Actions CI/CD pipelines.

## Current Architecture

### Database Structure
- **DMS Database**: `edfi_datamanagementservice` using schema `dms`
- **CMS Database**: `edfi_configurationservice` using schema `dmscs`
- **Shared PostgreSQL Instance**: Both databases run on port 5432 (internal) / 5435 (external)

### Test Execution Flow
1. **BeforeTestRun**: Initialize containers, register system admin
2. **BeforeFeature**: Initialize API context, connect to services
3. **Test Execution**: Run scenarios serially
4. **AfterFeature**: Clean database (DELETE operations)
5. **AfterTestRun**: Dispose resources

### Database Reset Strategy
Current approach uses DELETE operations on tables:
- DMS: Clears Reference, Alias, Document (except SchoolYearType), EdOrg hierarchies
- CMS: Clears Application, Vendor, test-created ClaimSets

## Required Changes for Schema Isolation

### 1. Core Database Layer Changes

#### 1.1 DMS SqlAction.cs Modifications
**File**: `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs`

All 76+ hardcoded schema references must be parameterized:

```csharp
// Current (lines 76, 124, 265, 357, 412, 443, 517, 589, 758, 783, 826, 865, 903, 939, 969, 975, 1010, 1040, 1070, 1100, 1133, 1160, 1186, 1213, 1240, 1267)
$"SELECT * FROM dms.Document WHERE..."
$"INSERT INTO dms.Alias..."
$"DELETE FROM dms.Reference..."

// Proposed
private readonly string _schemaName;
public SqlAction(string schemaName = "dms") 
{
    _schemaName = schemaName;
}

$"SELECT * FROM {_schemaName}.Document WHERE..."
$"INSERT INTO {_schemaName}.Alias..."
$"DELETE FROM {_schemaName}.Reference..."
```

#### 1.2 SQL Script Modifications
**Files**: All 25+ SQL scripts in `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Deploy/Scripts/`

Scripts must support dynamic schema creation:

```sql
-- Current: 0000_Create_DMS_Schema.sql
CREATE SCHEMA IF NOT EXISTS dms;

-- Proposed: Support parameterized schema
CREATE SCHEMA IF NOT EXISTS ${SCHEMA_NAME:dms};
```

Table creation scripts need schema variable substitution:

```sql
-- Current: 0001_Create_Document_Table.sql
CREATE TABLE dms.Document (...)

-- Proposed
CREATE TABLE ${SCHEMA_NAME}.Document (...)
```

#### 1.3 Stored Procedures and Functions
**File**: `0010_Create_Insert_References_Procedure.sql`

```sql
-- Current
CREATE OR REPLACE FUNCTION dms.InsertReferences(...)

-- Proposed
CREATE OR REPLACE FUNCTION ${SCHEMA_NAME}.InsertReferences(...)
```

#### 1.4 Triggers and Cross-Table References
**Files**: All trigger scripts (11 files)

Complex triggers that reference multiple tables need careful schema handling:

```sql
-- Example: 0011_Create_EducationOrganizationHierarchy_Triggers.sql
CREATE TRIGGER after_insert_educationorganizationhierarchy
    AFTER INSERT ON ${SCHEMA_NAME}.EducationOrganizationHierarchy
    FOR EACH ROW
    EXECUTE FUNCTION ${SCHEMA_NAME}.update_hierarchy_on_insert();
```

### 2. CMS Database Layer Changes

#### 2.1 CMS Repository Classes
**Files**: 10+ repository classes in `src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Repositories/`

All repositories have hardcoded `dmscs` schema references:

```csharp
// ApplicationRepository.cs
"SELECT * FROM dmscs.Application WHERE..."

// Proposed
private readonly string _schemaName;
public ApplicationRepository(IConfiguration config)
{
    _schemaName = config["CMS_SCHEMA_NAME"] ?? "dmscs";
}
```

#### 2.2 CMS SQL Scripts
**Files**: 20 SQL scripts in `src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Deploy/Scripts/`

Similar schema parameterization needed:

```sql
-- 0000_Create_CMS_Schema.sql
CREATE SCHEMA IF NOT EXISTS ${CMS_SCHEMA_NAME:dmscs};
```

### 3. Test Infrastructure Changes

#### 3.1 New Schema Management Service
**New File**: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Infrastructure/SchemaIsolationManager.cs`

```csharp
public class SchemaIsolationManager
{
    private static readonly ConcurrentDictionary<string, SchemaContext> _activeSchemas = new();
    private static int _schemaCounter = 0;
    private readonly IConfiguration _configuration;
    private readonly string _connectionString;

    public async Task<SchemaContext> AllocateTestSchema(string featureName)
    {
        var schemaId = Interlocked.Increment(ref _schemaCounter);
        var dmsSchema = $"test_dms_{featureName.ToLowerInvariant()}_{schemaId:D3}";
        var cmsSchema = $"test_cms_{featureName.ToLowerInvariant()}_{schemaId:D3}";
        
        var context = new SchemaContext
        {
            DmsSchemaName = dmsSchema,
            CmsSchemaName = cmsSchema,
            FeatureName = featureName,
            AllocatedAt = DateTime.UtcNow
        };

        // Create schemas
        await CreateSchema(dmsSchema, "edfi_datamanagementservice");
        await CreateSchema(cmsSchema, "edfi_configurationservice");
        
        // Run migration scripts
        await RunMigrations(dmsSchema, GetDmsMigrationScripts());
        await RunMigrations(cmsSchema, GetCmsMigrationScripts());
        
        _activeSchemas[featureName] = context;
        return context;
    }

    public async Task ReleaseSchema(string featureName)
    {
        if (_activeSchemas.TryRemove(featureName, out var context))
        {
            await DropSchema(context.DmsSchemaName, "edfi_datamanagementservice");
            await DropSchema(context.CmsSchemaName, "edfi_configurationservice");
        }
    }

    private async Task RunMigrations(string schemaName, List<string> scripts)
    {
        foreach (var script in scripts)
        {
            var modifiedScript = script
                .Replace("dms.", $"{schemaName}.")
                .Replace("dmscs.", $"{schemaName}.")
                .Replace("CREATE SCHEMA IF NOT EXISTS dms", $"CREATE SCHEMA IF NOT EXISTS {schemaName}")
                .Replace("CREATE SCHEMA IF NOT EXISTS dmscs", $"CREATE SCHEMA IF NOT EXISTS {schemaName}");
            
            await ExecuteSql(modifiedScript);
        }
    }
}
```

#### 3.2 Modified SetupHooks for DMS
**File**: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Hooks/SetupHooks.cs`

```csharp
[Binding]
public static class SetupHooks
{
    private static SchemaIsolationManager _schemaManager;
    private static SchemaContext _currentContext;

    [BeforeTestRun]
    public static async Task BeforeTestRun(PlaywrightContext context, TestLogger logger)
    {
        _schemaManager = new SchemaIsolationManager(Configuration);
        // Keep original system admin registration
        await SystemAdministrator.Register("sys-admin " + Guid.NewGuid(), "SdfH)98&Jk");
    }

    [BeforeFeature(Order = 1)]
    public static async Task AllocateSchema(FeatureContext featureContext)
    {
        var featureName = featureContext.FeatureInfo.Title
            .Replace(" ", "_")
            .Replace("-", "_")
            .ToLowerInvariant();
        
        _currentContext = await _schemaManager.AllocateTestSchema(featureName);
        featureContext["SchemaContext"] = _currentContext;
        
        // Configure DMS to use isolated schema
        Environment.SetEnvironmentVariable("DMS_SCHEMA_NAME", _currentContext.DmsSchemaName);
        Environment.SetEnvironmentVariable("CMS_SCHEMA_NAME", _currentContext.CmsSchemaName);
    }

    [BeforeFeature(Order = 2)]
    public static async Task ConfigureServices(PlaywrightContext context, TestLogger logger)
    {
        // Modify connection strings to use isolated schemas
        var dmsConnStr = Configuration.GetConnectionString("DMS");
        var cmsConnStr = Configuration.GetConnectionString("CMS");
        
        // PostgreSQL SearchPath directive
        dmsConnStr = $"{dmsConnStr};SearchPath={_currentContext.DmsSchemaName},public";
        cmsConnStr = $"{cmsConnStr};SearchPath={_currentContext.CmsSchemaName},public";
        
        // Update container environment
        await UpdateContainerEnvironment(new Dictionary<string, string>
        {
            ["DATABASE_CONNECTION_STRING"] = dmsConnStr,
            ["DMS_CONFIG_DATABASE_CONNECTION_STRING"] = cmsConnStr,
            ["DMS_SCHEMA_NAME"] = _currentContext.DmsSchemaName,
            ["CMS_SCHEMA_NAME"] = _currentContext.CmsSchemaName
        });
        
        context.ApiUrl = _containerSetup!.ApiUrl();
        await context.InitializeApiContext();
    }

    [AfterFeature]
    public static async Task AfterFeature(FeatureContext featureContext)
    {
        if (featureContext.TryGetValue("SchemaContext", out SchemaContext context))
        {
            await _schemaManager.ReleaseSchema(context.FeatureName);
        }
    }
}
```

#### 3.3 Test Parallelization Configuration
**New File**: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/.runsettings`

```xml
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <RunConfiguration>
    <MaxCpuCount>4</MaxCpuCount>
  </RunConfiguration>
  <NUnit>
    <NumberOfTestWorkers>4</NumberOfTestWorkers>
    <PreFilter>cat != RequiresSerialExecution</PreFilter>
  </NUnit>
  <TestRunParameters>
    <Parameter name="EnableSchemaIsolation" value="true" />
    <Parameter name="MaxParallelSchemas" value="4" />
  </TestRunParameters>
</RunSettings>
```

### 4. Application Configuration Changes

#### 4.1 DMS Dependency Injection
**File**: `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/Program.cs`

```csharp
// Add schema configuration
services.Configure<DatabaseOptions>(options =>
{
    options.SchemaName = configuration["DMS_SCHEMA_NAME"] ?? "dms";
});

// Update SqlAction registration
services.AddScoped<ISqlAction>(provider =>
{
    var options = provider.GetRequiredService<IOptions<DatabaseOptions>>();
    return new SqlAction(options.Value.SchemaName);
});
```

#### 4.2 CMS Configuration
**File**: `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Program.cs`

Similar dependency injection updates for CMS repositories.

### 5. Cross-Schema Query Complications

#### 5.1 Education Organization Hierarchy Queries
**Issue**: Recursive CTEs that traverse hierarchy tables

```sql
-- Current query spans single schema
WITH RECURSIVE ParentHierarchy AS (
    SELECT * FROM dms.EducationOrganizationHierarchy WHERE...
    UNION ALL
    SELECT * FROM dms.EducationOrganizationHierarchy parent
    JOIN ParentHierarchy child ON...
)

-- With isolation, must ensure all references use same schema
WITH RECURSIVE ParentHierarchy AS (
    SELECT * FROM test_dms_001.EducationOrganizationHierarchy WHERE...
    UNION ALL
    SELECT * FROM test_dms_001.EducationOrganizationHierarchy parent
    JOIN ParentHierarchy child ON...
)
```

#### 5.2 Authorization Table Joins
**Files**: Multiple authorization-related queries

Complex joins between Document, StudentSchoolAssociationAuthorization, and hierarchy tables must maintain schema consistency.

### 6. Docker and Environment Configuration

#### 6.1 Docker Compose Updates
**File**: `eng/docker-compose/local-dms.yml`

```yaml
services:
  dms:
    environment:
      DMS_SCHEMA_NAME: ${DMS_SCHEMA_NAME:-dms}
      DATABASE_CONNECTION_STRING: ${DATABASE_CONNECTION_STRING}
      AppSettings__SchemaName: ${DMS_SCHEMA_NAME:-dms}
```

#### 6.2 Environment Files
**File**: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/.env.e2e`

```env
# Schema Isolation Configuration
ENABLE_SCHEMA_ISOLATION=true
MAX_PARALLEL_SCHEMAS=4
SCHEMA_CLEANUP_STRATEGY=immediate  # or 'deferred'
SCHEMA_NAME_PREFIX=test
```

### 7. Test Categories and Gradual Migration

#### 7.1 Test Categorization
Mark tests for parallel safety:

```csharp
[TestFixture]
[Category("ParallelSafe")]
[Category("DatabaseIsolated")]
public class ResourceValidationTests { }

[TestFixture]
[Category("RequiresSerialExecution")]
[Category("ModifiesSharedState")]
public class KafkaMessagingTests { }
```

#### 7.2 Migration Strategy
1. **Phase 1**: Implement schema isolation infrastructure
2. **Phase 2**: Update simple CRUD tests (Resources, Descriptors)
3. **Phase 3**: Update authorization tests
4. **Phase 4**: Complex integration tests
5. **Phase 5**: Performance optimization

### 8. Potential Issues and Solutions

#### 8.1 Connection Pool Exhaustion
**Problem**: Each schema requires separate connections
**Solution**: 
- Implement connection pooling per schema
- Configure PostgreSQL max_connections appropriately
- Use pgBouncer for connection pooling

#### 8.2 Schema Cleanup Failures
**Problem**: Tests crash leaving orphaned schemas
**Solution**:
```csharp
public class SchemaCleanupService : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Cleanup schemas older than 1 hour
        var orphanedSchemas = await FindOrphanedSchemas();
        foreach (var schema in orphanedSchemas)
        {
            await DropSchemasCascade(schema);
        }
    }
}
```

#### 8.3 Cross-Schema Foreign Keys
**Problem**: Foreign key constraints don't work across schemas
**Solution**: 
- Keep all related tables in same schema
- Use application-level validation instead of FK constraints for test scenarios

### 9. Performance Implications

#### 9.1 Expected Improvements
- **Serial Execution**: ~20-30 minutes
- **4x Parallelization**: ~5-8 minutes
- **8x Parallelization**: ~3-5 minutes (diminishing returns)

#### 9.2 Resource Requirements
- **Memory**: +200MB per schema (table structures)
- **Disk**: +50MB per schema (indexes, data)
- **CPU**: Linear scaling with parallelization
- **Connections**: 5-10 connections per schema

### 10. GitHub Actions Compatibility

No changes required for CI/CD:
- Schema isolation is transparent to GitHub Actions
- Same Docker images work for both serial and parallel
- Configuration via environment variables

### 11. Rollback Plan

If schema isolation causes issues:
1. Set `ENABLE_SCHEMA_ISOLATION=false`
2. Tests revert to serial execution using default schemas
3. No code changes required, just configuration

## Summary of Required Changes

### File Count Impact
- **SQL Scripts to Modify**: 45+ files
- **C# Classes to Update**: 25+ files
- **Configuration Files**: 10+ files
- **New Infrastructure Code**: 5-8 new classes
- **Test Attributes**: 50+ test classes to categorize

### Effort Estimate
- **Infrastructure Setup**: 3-5 days
- **DMS Schema Updates**: 2-3 days
- **CMS Schema Updates**: 1-2 days
- **Test Migration**: 3-5 days
- **Testing & Debugging**: 3-5 days
- **Total**: 12-20 days

### Risk Assessment
- **High Risk**: Cross-schema queries, authorization logic
- **Medium Risk**: Connection pool management, cleanup
- **Low Risk**: Simple CRUD operations, test categorization

## Recommendation

Implement schema isolation in phases:
1. Start with infrastructure and simple tests
2. Measure performance improvements
3. Gradually migrate complex tests
4. Keep serial execution as fallback

This approach provides significant performance gains while maintaining system stability and CI/CD compatibility.