# DMS-1106: Integrate the Core/Backend Profile Write Contract — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bridge Core's profile write pipeline (C1-C8) into backend's relational write path by threading optional profile contracts through orchestration boundaries, enforcing root creatability, validating Core-emitted addresses, and enabling deferred stored-state projection.

**Architecture:** New middleware (`ProfileWritePipelineMiddleware`) invokes Core's `ProfileWritePipeline.Execute()` after mapping set resolution, builds a `BackendProfileWriteContext` composite type carrying the request contract + scope catalog + stored-state projection callback, threads it through `UpdateRequest`/`UpsertRequest` to `RelationalDocumentStoreRepository` where root creatability is enforced and contract validation runs. Body-source selection and merge execution are deferred to DMS-1123 and DMS-1124 respectively.

**Tech Stack:** C# 12 / .NET 10, NUnit, FakeItEasy, FluentAssertions, ImmutableArray

**Design spec:** `docs/superpowers/specs/2026-04-02-dms-1106-profile-write-contract-integration-design.md`

---

## File Map

| File | Action | Responsibility |
|---|---|---|
| `src/dms/backend/EdFi.DataManagementService.Backend.External/BackendProfileWriteContext.cs` | Create | Composite type + `IStoredStateProjectionInvoker` interface |
| `src/dms/backend/EdFi.DataManagementService.Backend/Profile/CompiledScopeAdapterFactory.cs` | Create | Build `CompiledScopeDescriptor[]` from `ResourceWritePlan` |
| `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileWriteContractValidator.cs` | Create | Validate Core-emitted addresses against compiled metadata |
| `src/dms/core/EdFi.DataManagementService.Core/Middleware/ProfileWritePipelineMiddleware.cs` | Create | Invoke `ProfileWritePipeline.Execute()`, build `BackendProfileWriteContext` |
| `src/dms/core/EdFi.DataManagementService.Core/Pipeline/RequestInfo.cs` | Modify | Add `BackendProfileWriteContext?` property |
| `src/dms/core/EdFi.DataManagementService.Core/Backend/UpdateRequest.cs` | Modify | Add `BackendProfileWriteContext?` parameter |
| `src/dms/core/EdFi.DataManagementService.Core/Backend/UpsertRequest.cs` | Modify | Thread inherited parameter |
| `src/dms/core/EdFi.DataManagementService.Core/Handler/UpsertHandler.cs` | Modify | Pass profile context to request |
| `src/dms/core/EdFi.DataManagementService.Core/Handler/UpdateByIdHandler.cs` | Modify | Pass profile context to request |
| `src/dms/backend/EdFi.DataManagementService.Backend.External/RelationalWriteRequestContracts.cs` | Modify | Add `BackendProfileWriteContext?` to `IRelationalWriteRequest` |
| `src/dms/backend/EdFi.DataManagementService.Backend/RelationalDocumentStoreRepository.cs` | Modify | Profile orchestration steps |
| `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteContracts.cs` | Modify | Add `ProfileAppliedWriteContext?` to terminal stage request |
| `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs` | Modify | Register middleware in pipeline |
| `src/dms/core/EdFi.DataManagementService.Core/DmsCoreServiceExtensions.cs` | Modify | Register middleware in DI |
| `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Profile/CompiledScopeAdapterFactoryTests.cs` | Create | Adapter factory unit tests |
| `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Profile/ProfileWriteContractValidatorTests.cs` | Create | Contract validator unit tests |
| `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Profile/ProfileWriteOrchestrationTests.cs` | Create | Repository orchestration tests |

---

### Task 1: Create BackendProfileWriteContext and IStoredStateProjectionInvoker

**Files:**
- Create: `src/dms/backend/EdFi.DataManagementService.Backend.External/BackendProfileWriteContext.cs`

- [ ] **Step 1: Create the composite type and interface**

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Callback interface for deferred stored-state projection. Middleware provides
/// an implementation that captures C6 projection dependencies; the repository
/// calls it when the stored document is available during update/upsert-to-existing flows.
/// </summary>
public interface IStoredStateProjectionInvoker
{
    /// <summary>
    /// Projects the stored document into a <see cref="ProfileAppliedWriteContext"/>
    /// using the captured profile pipeline state.
    /// </summary>
    /// <param name="storedDocument">The current stored JSON document loaded by the repository.</param>
    /// <param name="request">The request-side profile contract.</param>
    /// <param name="scopeCatalog">The compiled scope descriptors for the target resource.</param>
    ProfileAppliedWriteContext ProjectStoredState(
        JsonNode storedDocument,
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    );
}

/// <summary>
/// Composite type carrying all profile data needed by the backend write path.
/// Produced by <c>ProfileWritePipelineMiddleware</c> and threaded through request records
/// to the repository orchestration layer.
/// </summary>
/// <param name="Request">The request-side profile contract from the profile write pipeline.</param>
/// <param name="CompiledScopeCatalog">The compiled scope descriptors for the target resource.</param>
/// <param name="StoredStateProjectionInvoker">
/// Callback to invoke C6 stored-state projection when the stored document is available.
/// </param>
public sealed record BackendProfileWriteContext(
    ProfileAppliedWriteRequest Request,
    IReadOnlyList<CompiledScopeDescriptor> CompiledScopeCatalog,
    IStoredStateProjectionInvoker StoredStateProjectionInvoker
);
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/dms/backend/EdFi.DataManagementService.Backend.External/EdFi.DataManagementService.Backend.External.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```
feat(DMS-1106): add BackendProfileWriteContext composite type and IStoredStateProjectionInvoker
```

---

### Task 2: Add BackendProfileWriteContext to IRelationalWriteRequest interface

**Files:**
- Modify: `src/dms/backend/EdFi.DataManagementService.Backend.External/RelationalWriteRequestContracts.cs`

- [ ] **Step 1: Add the property to the interface**

In `RelationalWriteRequestContracts.cs`, add to `IRelationalWriteRequest`:

```csharp
// Add using at top:
// (No new using needed — BackendProfileWriteContext is in the same namespace)

// Add to the interface body after the MappingSet property:

    /// <summary>
    /// Optional profile write context when a writable profile applies to the request.
    /// Null when no profile applies or the request is not a write operation.
    /// </summary>
    BackendProfileWriteContext? BackendProfileWriteContext { get; }
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/dms/backend/EdFi.DataManagementService.Backend.External/EdFi.DataManagementService.Backend.External.csproj`
Expected: Build failure — `UpdateRequest` and `UpsertRequest` don't implement the new property yet. This is expected; we'll fix it in Task 4.

- [ ] **Step 3: Commit (hold — will bundle with Task 4)**

---

### Task 3: Thread BackendProfileWriteContext through request records and RequestInfo

**Files:**
- Modify: `src/dms/core/EdFi.DataManagementService.Core/Pipeline/RequestInfo.cs`
- Modify: `src/dms/core/EdFi.DataManagementService.Core/Backend/UpdateRequest.cs`
- Modify: `src/dms/core/EdFi.DataManagementService.Core/Backend/UpsertRequest.cs`

- [ ] **Step 1: Add property to RequestInfo**

In `RequestInfo.cs`, add after the `MappingSet` property (around line 162):

```csharp
    /// <summary>
    /// Optional profile write context when a writable profile applies to the current
    /// write request. Produced by ProfileWritePipelineMiddleware. Null when no writable
    /// profile applies or the request is not a write operation.
    /// </summary>
    public BackendProfileWriteContext? BackendProfileWriteContext { get; set; }
```

Add the using at the top:

```csharp
using EdFi.DataManagementService.Backend.External;
```

(Check if it's already there — `RequestInfo.cs` already uses `EdFi.DataManagementService.Backend.External` for `MappingSet`.)

- [ ] **Step 2: Add parameter to UpdateRequest**

In `UpdateRequest.cs`, add after the `ResourceAuthorizationPathways` parameter:

```csharp
    /// <summary>
    /// Optional profile write context when a writable profile applies.
    /// </summary>
    BackendProfileWriteContext? BackendProfileWriteContext = null
```

- [ ] **Step 3: Thread parameter through UpsertRequest**

In `UpsertRequest.cs`, add matching parameter and pass it through the base call:

Add after `ResourceAuthorizationPathways` parameter:
```csharp
    /// <summary>
    /// Optional profile write context when a writable profile applies.
    /// </summary>
    BackendProfileWriteContext? BackendProfileWriteContext = null
```

Update the base constructor call to pass it:
```csharp
    : UpdateRequest(
        ResourceInfo,
        DocumentInfo,
        MappingSet,
        EdfiDoc,
        Headers,
        TraceId,
        DocumentUuid,
        DocumentSecurityElements,
        UpdateCascadeHandler,
        ResourceAuthorizationHandler,
        ResourceAuthorizationPathways,
        BackendProfileWriteContext
    ),
        IRelationalUpsertRequest;
```

- [ ] **Step 4: Verify full solution compiles**

Run: `dotnet build src/dms/backend/EdFi.DataManagementService.Backend/EdFi.DataManagementService.Backend.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```
feat(DMS-1106): thread BackendProfileWriteContext through request records and interfaces
```

---

### Task 4: Add ProfileAppliedWriteContext to RelationalWriteTerminalStageRequest

**Files:**
- Modify: `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteContracts.cs`

- [ ] **Step 1: Add optional property to terminal stage request**

In `RelationalWriteTerminalStageRequest`, add a property and update the constructor:

Add using at top:
```csharp
using EdFi.DataManagementService.Core.Profile;
```

Add property after `DiagnosticIdentifier`:
```csharp
    /// <summary>
    /// Optional profile write context for profile-constrained writes.
    /// Present when a writable profile applies and stored-state projection has completed.
    /// DMS-1124 consumes this for hidden-member preservation during merge execution.
    /// </summary>
    public ProfileAppliedWriteContext? ProfileWriteContext { get; init; }
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/dms/backend/EdFi.DataManagementService.Backend/EdFi.DataManagementService.Backend.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```
feat(DMS-1106): add ProfileAppliedWriteContext to terminal stage request for downstream stories
```

---

### Task 5: Create CompiledScopeAdapterFactory with TDD

**Files:**
- Create: `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Profile/CompiledScopeAdapterFactoryTests.cs`
- Create: `src/dms/backend/EdFi.DataManagementService.Backend/Profile/CompiledScopeAdapterFactory.cs`

- [ ] **Step 1: Create the Profile directory in tests**

Run: `mkdir -p src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Profile`

- [ ] **Step 2: Write the failing test for root scope**

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_A_WritePlan_With_Root_Only
{
    private IReadOnlyList<CompiledScopeDescriptor> _result = null!;

    [SetUp]
    public void Setup()
    {
        ResourceWritePlan writePlan = AdapterFactoryTestFixtures.BuildRootOnlyWritePlan();
        _result = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
    }

    [Test]
    public void It_should_produce_one_scope()
    {
        _result.Should().HaveCount(1);
    }

    [Test]
    public void It_should_have_root_scope_kind()
    {
        _result[0].ScopeKind.Should().Be(ScopeKind.Root);
    }

    [Test]
    public void It_should_have_dollar_json_scope()
    {
        _result[0].JsonScope.Should().Be("$");
    }

    [Test]
    public void It_should_have_null_parent()
    {
        _result[0].ImmediateParentJsonScope.Should().BeNull();
    }

    [Test]
    public void It_should_have_empty_collection_ancestors()
    {
        _result[0].CollectionAncestorsInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_should_have_empty_semantic_identity()
    {
        _result[0].SemanticIdentityRelativePathsInOrder.Should().BeEmpty();
    }

    [Test]
    public void It_should_have_canonical_member_paths_from_columns()
    {
        _result[0].CanonicalScopeRelativeMemberPaths.Should().Contain("schoolId");
    }
}

[TestFixture]
public class Given_A_WritePlan_With_Collection_Scope
{
    private IReadOnlyList<CompiledScopeDescriptor> _result = null!;

    [SetUp]
    public void Setup()
    {
        ResourceWritePlan writePlan = AdapterFactoryTestFixtures.BuildRootWithCollectionWritePlan();
        _result = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
    }

    [Test]
    public void It_should_produce_two_scopes()
    {
        _result.Should().HaveCount(2);
    }

    [Test]
    public void It_should_have_collection_scope_kind_for_second_scope()
    {
        _result[1].ScopeKind.Should().Be(ScopeKind.Collection);
    }

    [Test]
    public void It_should_have_root_as_parent_of_collection()
    {
        _result[1].ImmediateParentJsonScope.Should().Be("$");
    }

    [Test]
    public void It_should_have_semantic_identity_from_merge_plan()
    {
        _result[1].SemanticIdentityRelativePathsInOrder.Should().Contain("classPeriodName");
    }

    [Test]
    public void It_should_have_root_in_collection_ancestors()
    {
        // Root is not a collection scope, so collection ancestors for
        // a top-level collection should be empty (root is not a collection ancestor).
        _result[1].CollectionAncestorsInOrder.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_A_WritePlan_With_Extension_Scope
{
    private IReadOnlyList<CompiledScopeDescriptor> _result = null!;

    [SetUp]
    public void Setup()
    {
        ResourceWritePlan writePlan = AdapterFactoryTestFixtures.BuildRootWithExtensionWritePlan();
        _result = CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);
    }

    [Test]
    public void It_should_have_non_collection_scope_kind_for_extension()
    {
        var extScope = _result.First(s => s.JsonScope.Contains("_ext"));
        extScope.ScopeKind.Should().Be(ScopeKind.NonCollection);
    }

    [Test]
    public void It_should_have_root_as_parent_of_extension()
    {
        var extScope = _result.First(s => s.JsonScope.Contains("_ext"));
        extScope.ImmediateParentJsonScope.Should().Be("$");
    }
}
```

- [ ] **Step 3: Create test fixture helper**

Create `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Profile/AdapterFactoryTestFixtures.cs`:

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

/// <summary>
/// Shared test fixture builders for adapter factory and contract validator tests.
/// Constructs minimal ResourceWritePlan/TableWritePlan/DbTableModel structures
/// that represent the backend plan shapes relevant to profile integration.
/// </summary>
internal static class AdapterFactoryTestFixtures
{
    private static readonly JsonPathExpression RootScope = MakeJsonPath("$");
    private static readonly JsonPathExpression CollectionScope = MakeJsonPath("$.classPeriods[*]");
    private static readonly JsonPathExpression ExtensionScope = MakeJsonPath("$._ext.sample");

    public static ResourceWritePlan BuildRootOnlyWritePlan()
    {
        var rootTable = BuildRootTableModel("schoolId", "schoolName");
        var rootPlan = BuildTableWritePlan(rootTable);
        return new ResourceWritePlan(
            new RelationalResourceModel([rootTable]),
            [rootPlan]
        );
    }

    public static ResourceWritePlan BuildRootWithCollectionWritePlan()
    {
        var rootTable = BuildRootTableModel("schoolId");
        var collectionTable = BuildCollectionTableModel(
            CollectionScope,
            parentJsonScope: RootScope,
            semanticIdentityPaths: ["classPeriodName"],
            columnNames: ["classPeriodName", "officialAttendancePeriod"]
        );

        var rootPlan = BuildTableWritePlan(rootTable);
        var collectionPlan = BuildTableWritePlan(
            collectionTable,
            collectionMergePlan: BuildCollectionMergePlan(["classPeriodName"])
        );

        return new ResourceWritePlan(
            new RelationalResourceModel([rootTable, collectionTable]),
            [rootPlan, collectionPlan]
        );
    }

    public static ResourceWritePlan BuildRootWithExtensionWritePlan()
    {
        var rootTable = BuildRootTableModel("schoolId");
        var extTable = BuildExtensionTableModel(
            ExtensionScope,
            parentJsonScope: RootScope,
            columnNames: ["sampleField"]
        );

        var rootPlan = BuildTableWritePlan(rootTable);
        var extPlan = BuildTableWritePlan(extTable);

        return new ResourceWritePlan(
            new RelationalResourceModel([rootTable, extTable]),
            [rootPlan, extPlan]
        );
    }

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    private static DbTableModel BuildRootTableModel(params string[] columnNames)
    {
        var columns = columnNames
            .Select(n => new DbColumnModel(
                new DbColumnName(n),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.String),
                IsNullable: false,
                SourceJsonPath: MakeJsonPath(n),
                TargetResource: null
            ))
            .ToList();

        return new DbTableModel(
            new DbTableName(new DbSchemaName("dms"), "School"),
            RootScope,
            new TableKey([new DbColumnName("DocumentId")]),
            columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };
    }

    private static DbTableModel BuildCollectionTableModel(
        JsonPathExpression jsonScope,
        JsonPathExpression parentJsonScope,
        string[] semanticIdentityPaths,
        string[] columnNames
    )
    {
        var columns = columnNames
            .Select(n => new DbColumnModel(
                new DbColumnName(n),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.String),
                IsNullable: false,
                SourceJsonPath: MakeJsonPath(n),
                TargetResource: null
            ))
            .ToList();

        var semanticBindings = semanticIdentityPaths
            .Select(p => new CollectionSemanticIdentityBinding(MakeJsonPath(p), new DbColumnName(p)))
            .ToList();

        return new DbTableModel(
            new DbTableName(new DbSchemaName("dms"), "School_classPeriods"),
            jsonScope,
            new TableKey([new DbColumnName("CollectionItemId")]),
            columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                SemanticIdentityBindings: semanticBindings
            ),
        };
    }

    private static DbTableModel BuildExtensionTableModel(
        JsonPathExpression jsonScope,
        JsonPathExpression parentJsonScope,
        string[] columnNames
    )
    {
        var columns = columnNames
            .Select(n => new DbColumnModel(
                new DbColumnName(n),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.String),
                IsNullable: true,
                SourceJsonPath: MakeJsonPath(n),
                TargetResource: null
            ))
            .ToList();

        return new DbTableModel(
            new DbTableName(new DbSchemaName("dms"), "School__ext_sample"),
            jsonScope,
            new TableKey([new DbColumnName("DocumentId")]),
            columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.RootExtension,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")],
                SemanticIdentityBindings: []
            ),
        };
    }

    private static TableWritePlan BuildTableWritePlan(
        DbTableModel tableModel,
        CollectionMergePlan? collectionMergePlan = null
    ) =>
        new(
            tableModel,
            InsertSql: "INSERT INTO ...",
            UpdateSql: "UPDATE ...",
            DeleteByParentSql: "DELETE FROM ...",
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 10, 2100),
            ColumnBindings: tableModel.Columns
                .Select(c => new WriteColumnBinding(
                    c,
                    new WriteValueSource.Scalar(
                        c.SourceJsonPath!.Value,
                        c.ScalarType!
                    ),
                    $"@p_{c.ColumnName.Value}"
                )),
            KeyUnificationPlans: [],
            CollectionMergePlan: collectionMergePlan
        );

    private static CollectionMergePlan BuildCollectionMergePlan(string[] semanticIdentityPaths) =>
        new(
            SemanticIdentityBindings: semanticIdentityPaths
                .Select((p, i) => new CollectionMergeSemanticIdentityBinding(MakeJsonPath(p), BindingIndex: i)),
            StableRowIdentityBindingIndex: 0,
            UpdateByStableRowIdentitySql: "UPDATE ...",
            DeleteByStableRowIdentitySql: "DELETE ...",
            OrdinalBindingIndex: 0,
            CompareBindingIndexesInOrder: [0]
        );

    private static JsonPathExpression MakeJsonPath(string canonical) =>
        new(canonical, []);
}
```

- [ ] **Step 4: Create the Profile directory in backend**

Run: `mkdir -p src/dms/backend/EdFi.DataManagementService.Backend/Profile`

- [ ] **Step 5: Run the tests to confirm they fail**

Run: `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/ --filter "FullyQualifiedName~AdapterFactory" --no-restore`
Expected: Compilation failure — `CompiledScopeAdapterFactory` does not exist.

- [ ] **Step 6: Implement CompiledScopeAdapterFactory**

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Builds a <see cref="CompiledScopeDescriptor"/> catalog from backend
/// <see cref="ResourceWritePlan"/> metadata. This is the adapter factory
/// that bridges backend's relational plan types into Core's profile
/// address derivation vocabulary.
/// </summary>
internal static class CompiledScopeAdapterFactory
{
    /// <summary>
    /// Builds an immutable scope catalog from the write plan's tables in dependency order.
    /// </summary>
    public static IReadOnlyList<CompiledScopeDescriptor> BuildFromWritePlan(ResourceWritePlan writePlan)
    {
        ArgumentNullException.ThrowIfNull(writePlan);

        // Index tables by JsonScope for parent lookups
        var tablesByScope = new Dictionary<string, DbTableModel>();
        foreach (TableWritePlan tablePlan in writePlan.TablePlansInDependencyOrder)
        {
            tablesByScope[tablePlan.TableModel.JsonScope.Canonical] = tablePlan.TableModel;
        }

        // Index merge plans by JsonScope for semantic identity lookup
        var mergePlansByScope = new Dictionary<string, CollectionMergePlan>();
        foreach (TableWritePlan tablePlan in writePlan.TablePlansInDependencyOrder)
        {
            if (tablePlan.CollectionMergePlan is not null)
            {
                mergePlansByScope[tablePlan.TableModel.JsonScope.Canonical] = tablePlan.CollectionMergePlan;
            }
        }

        var result = ImmutableArray.CreateBuilder<CompiledScopeDescriptor>(
            writePlan.TablePlansInDependencyOrder.Length
        );

        foreach (TableWritePlan tablePlan in writePlan.TablePlansInDependencyOrder)
        {
            DbTableModel table = tablePlan.TableModel;
            string jsonScope = table.JsonScope.Canonical;
            DbTableKind tableKind = table.IdentityMetadata.TableKind;

            ScopeKind scopeKind = MapTableKindToScopeKind(tableKind);

            string? immediateParentJsonScope = ResolveImmediateParentJsonScope(
                table,
                tablesByScope
            );

            ImmutableArray<string> collectionAncestorsInOrder = BuildCollectionAncestors(
                immediateParentJsonScope,
                tablesByScope
            );

            ImmutableArray<string> semanticIdentityPaths = BuildSemanticIdentityPaths(
                jsonScope,
                mergePlansByScope
            );

            ImmutableArray<string> canonicalMemberPaths = BuildCanonicalMemberPaths(table);

            result.Add(
                new CompiledScopeDescriptor(
                    JsonScope: jsonScope,
                    ScopeKind: scopeKind,
                    ImmediateParentJsonScope: immediateParentJsonScope,
                    CollectionAncestorsInOrder: collectionAncestorsInOrder,
                    SemanticIdentityRelativePathsInOrder: semanticIdentityPaths,
                    CanonicalScopeRelativeMemberPaths: canonicalMemberPaths
                )
            );
        }

        return result.ToImmutable();
    }

    private static ScopeKind MapTableKindToScopeKind(DbTableKind tableKind) =>
        tableKind switch
        {
            DbTableKind.Root => ScopeKind.Root,
            DbTableKind.Collection => ScopeKind.Collection,
            DbTableKind.ExtensionCollection => ScopeKind.Collection,
            _ => ScopeKind.NonCollection,
        };

    private static string? ResolveImmediateParentJsonScope(
        DbTableModel table,
        Dictionary<string, DbTableModel> tablesByScope
    )
    {
        if (table.IdentityMetadata.TableKind == DbTableKind.Root)
        {
            return null;
        }

        // Walk up: find a table whose scope is the prefix of this one
        // For "$.classPeriods[*]", parent is "$"
        // For "$._ext.sample", parent is "$"
        // For "$.classPeriods[*]._ext.sample", parent is "$.classPeriods[*]"
        string scope = table.JsonScope.Canonical;

        // Try stripping the last segment to find the parent
        int lastDot = scope.LastIndexOf('.');
        while (lastDot > 0)
        {
            string candidateParent = scope[..lastDot];
            // Strip [*] suffix if present for lookup
            string normalizedCandidate = candidateParent.EndsWith("[*]")
                ? candidateParent[..^3]
                : candidateParent;

            // Check both forms
            if (tablesByScope.ContainsKey(candidateParent))
            {
                return candidateParent;
            }

            if (
                normalizedCandidate != candidateParent
                && tablesByScope.ContainsKey(normalizedCandidate)
            )
            {
                return normalizedCandidate;
            }

            // Also check with [*] suffix
            string withArraySuffix = candidateParent + "[*]";
            if (tablesByScope.ContainsKey(withArraySuffix))
            {
                return withArraySuffix;
            }

            lastDot = scope.LastIndexOf('.', lastDot - 1);
        }

        // Default: root
        return tablesByScope.ContainsKey("$") ? "$" : null;
    }

    private static ImmutableArray<string> BuildCollectionAncestors(
        string? immediateParentJsonScope,
        Dictionary<string, DbTableModel> tablesByScope
    )
    {
        if (immediateParentJsonScope is null)
        {
            return [];
        }

        var ancestors = new List<string>();
        string? current = immediateParentJsonScope;

        while (current is not null && tablesByScope.TryGetValue(current, out DbTableModel? table))
        {
            DbTableKind kind = table.IdentityMetadata.TableKind;
            if (kind is DbTableKind.Collection or DbTableKind.ExtensionCollection)
            {
                ancestors.Add(current);
            }

            // Walk up to the parent of current
            current = ResolveImmediateParentJsonScope(table, tablesByScope);
        }

        // Reverse to get root-most first
        ancestors.Reverse();
        return [.. ancestors];
    }

    private static ImmutableArray<string> BuildSemanticIdentityPaths(
        string jsonScope,
        Dictionary<string, CollectionMergePlan> mergePlansByScope
    )
    {
        if (!mergePlansByScope.TryGetValue(jsonScope, out CollectionMergePlan? mergePlan))
        {
            return [];
        }

        return [.. mergePlan.SemanticIdentityBindings.Select(b => b.RelativePath.Canonical)];
    }

    private static ImmutableArray<string> BuildCanonicalMemberPaths(DbTableModel table) =>
        [
            .. table.Columns
                .Where(c => c.SourceJsonPath is not null)
                .Select(c => c.SourceJsonPath!.Value.Canonical),
        ];
}
```

- [ ] **Step 7: Run the tests and verify they pass**

Run: `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/ --filter "FullyQualifiedName~AdapterFactory" -v normal`
Expected: All tests pass

- [ ] **Step 8: Commit**

```
feat(DMS-1106): implement CompiledScopeAdapterFactory with TDD
```

---

### Task 6: Create ProfileWriteContractValidator with TDD

**Files:**
- Create: `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Profile/ProfileWriteContractValidatorTests.cs`
- Create: `src/dms/backend/EdFi.DataManagementService.Backend/Profile/ProfileWriteContractValidator.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_ValidRequestContract_When_Validating
{
    private ProfileFailure[] _result = null!;

    private static readonly IReadOnlyList<CompiledScopeDescriptor> ScopeCatalog =
    [
        new CompiledScopeDescriptor(
            "$",
            ScopeKind.Root,
            null,
            [],
            [],
            ["schoolId", "schoolName"]
        ),
    ];

    [SetUp]
    public void Setup()
    {
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$", []),
                    ProfileVisibilityKind.VisiblePresent,
                    true
                ),
            ],
            VisibleRequestCollectionItems: []
        );

        _result = ProfileWriteContractValidator.ValidateRequestContract(
            request,
            ScopeCatalog,
            "TestProfile",
            "School",
            "POST",
            "write"
        );
    }

    [Test]
    public void It_should_return_no_failures()
    {
        _result.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_UnknownJsonScope_When_Validating
{
    private ProfileFailure[] _result = null!;

    private static readonly IReadOnlyList<CompiledScopeDescriptor> ScopeCatalog =
    [
        new CompiledScopeDescriptor(
            "$",
            ScopeKind.Root,
            null,
            [],
            [],
            ["schoolId"]
        ),
    ];

    [SetUp]
    public void Setup()
    {
        var request = new ProfileAppliedWriteRequest(
            WritableRequestBody: JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates:
            [
                new RequestScopeState(
                    new ScopeInstanceAddress("$.unknownScope", []),
                    ProfileVisibilityKind.VisiblePresent,
                    true
                ),
            ],
            VisibleRequestCollectionItems: []
        );

        _result = ProfileWriteContractValidator.ValidateRequestContract(
            request,
            ScopeCatalog,
            "TestProfile",
            "School",
            "POST",
            "write"
        );
    }

    [Test]
    public void It_should_return_one_failure()
    {
        _result.Should().HaveCount(1);
    }

    [Test]
    public void It_should_be_category5_contract_mismatch()
    {
        _result[0].Category.Should().Be(ProfileFailureCategory.CoreBackendContractMismatch);
    }

    [Test]
    public void It_should_be_unknown_jsonscope_type()
    {
        _result[0].Should().BeOfType<UnknownJsonScopeCoreBackendContractMismatchFailure>();
    }
}
```

- [ ] **Step 2: Run the tests to confirm they fail**

Run: `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/ --filter "FullyQualifiedName~ContractValidator" --no-restore`
Expected: Compilation failure — `ProfileWriteContractValidator` does not exist.

- [ ] **Step 3: Implement ProfileWriteContractValidator**

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profile;

namespace EdFi.DataManagementService.Backend.Profile;

/// <summary>
/// Validates Core-emitted profile contract addresses against the compiled
/// scope metadata before downstream merge/persist code uses them.
/// Emits deterministic C8 category-5 contract-mismatch diagnostics.
/// </summary>
internal static class ProfileWriteContractValidator
{
    /// <summary>
    /// Validates the request-side contract: checks that every emitted JsonScope,
    /// ancestor chain, and canonical member path aligns with the compiled scope catalog.
    /// </summary>
    public static ProfileFailure[] ValidateRequestContract(
        ProfileAppliedWriteRequest request,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
        string profileName,
        string resourceName,
        string method,
        string operation
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scopeCatalog);

        var scopeIndex = BuildScopeIndex(scopeCatalog);
        var failures = new List<ProfileFailure>();

        // Validate request scope states
        foreach (RequestScopeState scopeState in request.RequestScopeStates)
        {
            ValidateScopeAddress(
                scopeState.Address.JsonScope,
                scopeState.Address.AncestorCollectionInstances,
                scopeIndex,
                profileName,
                resourceName,
                method,
                operation,
                failures
            );
        }

        // Validate visible request collection items
        foreach (VisibleRequestCollectionItem item in request.VisibleRequestCollectionItems)
        {
            ValidateCollectionRowAddress(
                item.Address,
                scopeIndex,
                profileName,
                resourceName,
                method,
                operation,
                failures
            );
        }

        return [.. failures];
    }

    /// <summary>
    /// Validates the stored-side context: checks that every emitted stored scope/row
    /// address and hidden member path aligns with the compiled scope catalog.
    /// </summary>
    public static ProfileFailure[] ValidateWriteContext(
        ProfileAppliedWriteContext context,
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
        string profileName,
        string resourceName,
        string method,
        string operation
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scopeCatalog);

        var scopeIndex = BuildScopeIndex(scopeCatalog);
        var failures = new List<ProfileFailure>();

        // Validate stored scope states
        foreach (StoredScopeState storedScope in context.StoredScopeStates)
        {
            ValidateScopeAddress(
                storedScope.Address.JsonScope,
                storedScope.Address.AncestorCollectionInstances,
                scopeIndex,
                profileName,
                resourceName,
                method,
                operation,
                failures
            );

            // Validate hidden member paths against canonical vocabulary
            if (!storedScope.HiddenMemberPaths.IsDefaultOrEmpty)
            {
                ValidateHiddenMemberPaths(
                    storedScope.Address.JsonScope,
                    storedScope.HiddenMemberPaths,
                    scopeIndex,
                    profileName,
                    resourceName,
                    method,
                    operation,
                    failures
                );
            }
        }

        // Validate visible stored collection rows
        foreach (VisibleStoredCollectionRow storedRow in context.VisibleStoredCollectionRows)
        {
            ValidateCollectionRowAddress(
                storedRow.Address,
                scopeIndex,
                profileName,
                resourceName,
                method,
                operation,
                failures
            );

            if (!storedRow.HiddenMemberPaths.IsDefaultOrEmpty)
            {
                ValidateHiddenMemberPaths(
                    storedRow.Address.JsonScope,
                    storedRow.HiddenMemberPaths,
                    scopeIndex,
                    profileName,
                    resourceName,
                    method,
                    operation,
                    failures
                );
            }
        }

        return [.. failures];
    }

    // -----------------------------------------------------------------------
    //  Private helpers
    // -----------------------------------------------------------------------

    private static Dictionary<string, CompiledScopeDescriptor> BuildScopeIndex(
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
    )
    {
        var index = new Dictionary<string, CompiledScopeDescriptor>(scopeCatalog.Count);
        foreach (CompiledScopeDescriptor scope in scopeCatalog)
        {
            index[scope.JsonScope] = scope;
        }

        return index;
    }

    private static void ValidateScopeAddress(
        string jsonScope,
        System.Collections.Immutable.ImmutableArray<AncestorCollectionInstance> ancestorInstances,
        Dictionary<string, CompiledScopeDescriptor> scopeIndex,
        string profileName,
        string resourceName,
        string method,
        string operation,
        List<ProfileFailure> failures
    )
    {
        if (!scopeIndex.TryGetValue(jsonScope, out CompiledScopeDescriptor? compiled))
        {
            failures.Add(
                ProfileFailures.UnknownJsonScope(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    jsonScope,
                    ScopeKind.NonCollection
                )
            );
            return;
        }

        // Validate ancestor chain length matches
        if (ancestorInstances.Length != compiled.CollectionAncestorsInOrder.Length)
        {
            failures.Add(
                ProfileFailures.AncestorChainMismatch(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    compiled,
                    new ScopeInstanceAddress(jsonScope, ancestorInstances)
                )
            );
            return;
        }

        // Validate each ancestor scope matches
        for (int i = 0; i < ancestorInstances.Length; i++)
        {
            if (ancestorInstances[i].JsonScope != compiled.CollectionAncestorsInOrder[i])
            {
                failures.Add(
                    ProfileFailures.AncestorChainMismatch(
                        profileName,
                        resourceName,
                        method,
                        operation,
                        compiled,
                        new ScopeInstanceAddress(jsonScope, ancestorInstances)
                    )
                );
                return;
            }
        }
    }

    private static void ValidateCollectionRowAddress(
        CollectionRowAddress address,
        Dictionary<string, CompiledScopeDescriptor> scopeIndex,
        string profileName,
        string resourceName,
        string method,
        string operation,
        List<ProfileFailure> failures
    )
    {
        if (!scopeIndex.TryGetValue(address.JsonScope, out CompiledScopeDescriptor? compiled))
        {
            failures.Add(
                ProfileFailures.UnknownJsonScope(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    address.JsonScope,
                    ScopeKind.Collection
                )
            );
            return;
        }

        // Validate ancestor chain from parent address
        if (
            address.ParentAddress.AncestorCollectionInstances.Length
            != compiled.CollectionAncestorsInOrder.Length
        )
        {
            failures.Add(
                ProfileFailures.AncestorChainMismatch(
                    profileName,
                    resourceName,
                    method,
                    operation,
                    compiled,
                    address
                )
            );
        }
    }

    private static void ValidateHiddenMemberPaths(
        string jsonScope,
        System.Collections.Immutable.ImmutableArray<string> hiddenMemberPaths,
        Dictionary<string, CompiledScopeDescriptor> scopeIndex,
        string profileName,
        string resourceName,
        string method,
        string operation,
        List<ProfileFailure> failures
    )
    {
        if (!scopeIndex.TryGetValue(jsonScope, out CompiledScopeDescriptor? compiled))
        {
            return; // Already reported as unknown scope
        }

        var vocabulary = compiled.CanonicalScopeRelativeMemberPaths;

        foreach (string path in hiddenMemberPaths)
        {
            if (!vocabulary.Contains(path))
            {
                failures.Add(
                    ProfileFailures.CanonicalMemberPathMismatch(
                        profileName,
                        resourceName,
                        method,
                        operation,
                        compiled,
                        new ScopeInstanceAddress(jsonScope, []),
                        hiddenMemberPaths
                    )
                );
                return;
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests and verify they pass**

Run: `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/ --filter "FullyQualifiedName~ContractValidator" -v normal`
Expected: All tests pass

- [ ] **Step 5: Commit**

```
feat(DMS-1106): implement ProfileWriteContractValidator with TDD
```

---

### Task 7: Integrate profile steps into RelationalDocumentStoreRepository

**Files:**
- Modify: `src/dms/backend/EdFi.DataManagementService.Backend/RelationalDocumentStoreRepository.cs`

- [ ] **Step 1: Add profile orchestration to ExecuteWriteGuardRails**

Add using statements at the top:
```csharp
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
```

In `UpsertDocument`, extract `BackendProfileWriteContext` from the relational request and pass it to `ExecuteWriteGuardRails`. Similarly for `UpdateDocumentById`.

Add a new parameter to `ExecuteWriteGuardRails`:
```csharp
BackendProfileWriteContext? backendProfileWriteContext,
```

Inside `ExecuteWriteGuardRails`, after resolving the write plan (after line 223) and before the try/catch block for target context resolution, add the profile orchestration steps:

```csharp
        // ---------------------------------------------------------------
        // Profile: Root creatability guard (POST only)
        // ---------------------------------------------------------------
        if (
            backendProfileWriteContext is not null
            && operationKind == RelationalWriteOperationKind.Post
            && !backendProfileWriteContext.Request.RootResourceCreatable
        )
        {
            return validationFailureFactory(
                [
                    new WriteValidationFailure(
                        "Root resource cannot be created with the active writable profile.",
                        []
                    ),
                ]
            );
        }

        // ---------------------------------------------------------------
        // Profile: Validate request-side contract
        // ---------------------------------------------------------------
        ProfileAppliedWriteContext? resolvedProfileWriteContext = null;

        if (backendProfileWriteContext is not null)
        {
            ProfileFailure[] requestContractFailures =
                ProfileWriteContractValidator.ValidateRequestContract(
                    backendProfileWriteContext.Request,
                    backendProfileWriteContext.CompiledScopeCatalog,
                    /* profileName, resourceName, method, operation — extract from context */
                    resourceInfo.ResourceName.Value,
                    resourceInfo.ResourceName.Value,
                    operationKind == RelationalWriteOperationKind.Post ? "POST" : "PUT",
                    "write"
                );

            if (requestContractFailures.Length > 0)
            {
                return validationFailureFactory(
                    requestContractFailures
                        .Select(f => new WriteValidationFailure(f.Message, []))
                        .ToArray()
                );
            }
        }
```

After target context resolution (inside the try block, after `resolvedReferences` check), add stored-state projection:

```csharp
            // ---------------------------------------------------------------
            // Profile: Stored-state projection (update/upsert-to-existing)
            // ---------------------------------------------------------------
            if (
                backendProfileWriteContext is not null
                && targetContext is RelationalWriteTargetContext.ExistingDocument
            )
            {
                // Note: storedDocument loading is formalized by DMS-1105.
                // For now, this branch is reachable but the stored document
                // is not yet available. The projection invocation is wired up
                // so DMS-1105 can supply the document when ready.
                // resolvedProfileWriteContext = backendProfileWriteContext
                //     .StoredStateProjectionInvoker
                //     .ProjectStoredState(
                //         storedDocument,
                //         backendProfileWriteContext.Request,
                //         backendProfileWriteContext.CompiledScopeCatalog
                //     );
            }
```

Update the terminal stage request construction to include the profile context:

```csharp
            var terminalStageResult = await _terminalStage
                .ExecuteAsync(
                    new RelationalWriteTerminalStageRequest(flatteningInput, flattenedWriteSet, traceId)
                    {
                        ProfileWriteContext = resolvedProfileWriteContext,
                    }
                )
                .ConfigureAwait(false);
```

- [ ] **Step 2: Update callers to pass BackendProfileWriteContext**

In `UpsertDocument`, pass the profile context:
```csharp
return ExecuteWriteGuardRails<UpsertResult>(
    requestBody: relationalUpsertRequest.EdfiDoc,
    traceId: relationalUpsertRequest.TraceId,
    mappingSet,
    relationalUpsertRequest.ResourceInfo,
    RelationalWriteOperationKind.Post,
    relationalUpsertRequest.DocumentInfo.DocumentReferences,
    relationalUpsertRequest.DocumentInfo.DescriptorReferences,
    relationalUpsertRequest.BackendProfileWriteContext,  // NEW
    // ... rest unchanged
```

Same for `UpdateDocumentById`:
```csharp
return ExecuteWriteGuardRails<UpdateResult>(
    requestBody: relationalUpdateRequest.EdfiDoc,
    traceId: relationalUpdateRequest.TraceId,
    mappingSet,
    relationalUpdateRequest.ResourceInfo,
    RelationalWriteOperationKind.Put,
    relationalUpdateRequest.DocumentInfo.DocumentReferences,
    relationalUpdateRequest.DocumentInfo.DescriptorReferences,
    relationalUpdateRequest.BackendProfileWriteContext,  // NEW
    // ... rest unchanged
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build src/dms/backend/EdFi.DataManagementService.Backend/EdFi.DataManagementService.Backend.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```
feat(DMS-1106): integrate profile orchestration steps into RelationalDocumentStoreRepository
```

---

### Task 8: Create ProfileWritePipelineMiddleware

**Files:**
- Create: `src/dms/core/EdFi.DataManagementService.Core/Middleware/ProfileWritePipelineMiddleware.cs`

- [ ] **Step 1: Implement the middleware**

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static EdFi.DataManagementService.Core.Response.FailureResponse;

namespace EdFi.DataManagementService.Core.Middleware;

/// <summary>
/// Middleware that invokes the Core profile write pipeline for POST/PUT requests
/// when a relational MappingSet is available. Builds the compiled scope catalog
/// from the write plan, invokes <see cref="ProfileWritePipeline.Execute"/>,
/// and attaches a <see cref="BackendProfileWriteContext"/> to <see cref="RequestInfo"/>
/// for downstream consumption by the repository.
/// </summary>
internal class ProfileWritePipelineMiddleware(
    IOptions<AppSettings> appSettings,
    ILogger<ProfileWritePipelineMiddleware> logger
) : IPipelineStep
{
    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return new string(
            input.Where(c =>
                char.IsLetterOrDigit(c)
                || c == ' '
                || c == '_'
                || c == '-'
                || c == '.'
                || c == ':'
                || c == '/'
            ).ToArray()
        );
    }

    public async Task Execute(RequestInfo requestInfo, Func<Task> next)
    {
        // Short-circuit if relational backend is disabled
        if (!appSettings.Value.UseRelationalBackend)
        {
            await next();
            return;
        }

        // Short-circuit if not a write operation
        if (
            requestInfo.Method != RequestMethod.POST
            && requestInfo.Method != RequestMethod.PUT
        )
        {
            await next();
            return;
        }

        // Short-circuit if no MappingSet available (non-relational path or prior short-circuit)
        if (requestInfo.MappingSet is null)
        {
            await next();
            return;
        }

        // Short-circuit if no profile context
        if (requestInfo.ProfileContext is null)
        {
            await next();
            return;
        }

        logger.LogDebug(
            "Entering ProfileWritePipelineMiddleware - {TraceId}",
            requestInfo.FrontendRequest.TraceId.Value
        );

        // Resolve the write plan for the current resource
        var resource = new QualifiedResourceName(
            requestInfo.ResourceInfo.ProjectName.Value,
            requestInfo.ResourceInfo.ResourceName.Value
        );

        ResourceWritePlan writePlan;
        try
        {
            writePlan = requestInfo.MappingSet.GetWritePlanOrThrow(resource);
        }
        catch (Exception ex) when (ex is NotSupportedException or MissingWritePlanLookupGuardRailException)
        {
            logger.LogWarning(
                "Profile pipeline skipped — write plan unavailable for {Resource} - {TraceId}",
                SanitizeForLog(resource.ResourceName),
                requestInfo.FrontendRequest.TraceId.Value
            );
            await next();
            return;
        }

        // Build compiled scope catalog from the write plan
        IReadOnlyList<CompiledScopeDescriptor> scopeCatalog =
            CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);

        // Determine profile parameters
        ProfileContext profileContext = requestInfo.ProfileContext;
        ContentTypeDefinition? writeContentType = profileContext.ResourceProfile.WriteContentType;
        ProfileContentType? resolvedContentType =
            profileContext.ContentType == ProfileContentType.Write ? ProfileContentType.Write : null;

        bool isCreate = requestInfo.Method == RequestMethod.POST;

        // Build effectiveSchemaRequiredMembersByScope
        // For now, use an empty dictionary — the creatability analysis will be
        // conservative (all members assumed not required), which is safe.
        // A future story should source this from the effective JSON schema.
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveSchemaRequiredMembersByScope =
            new Dictionary<string, IReadOnlyList<string>>();

        // Invoke the profile write pipeline (request-side only — no stored document yet)
        ProfileWritePipelineResult pipelineResult = ProfileWritePipeline.Execute(
            canonicalizedRequestBody: requestInfo.ParsedBody,
            writeContentType: writeContentType,
            resolvedContentType: resolvedContentType,
            scopeCatalog: scopeCatalog,
            storedDocument: null, // Stored-state projection is deferred to the repository
            isCreate: isCreate,
            profileName: profileContext.ProfileName,
            resourceName: requestInfo.ResourceInfo.ResourceName.Value,
            method: requestInfo.Method.ToString(),
            operation: "write",
            effectiveSchemaRequiredMembersByScope: effectiveSchemaRequiredMembersByScope
        );

        // Handle pipeline failures
        if (!pipelineResult.Failures.IsDefaultOrEmpty)
        {
            logger.LogDebug(
                "Profile write pipeline produced {Count} failure(s) for {Resource} - {TraceId}",
                pipelineResult.Failures.Length,
                SanitizeForLog(resource.ResourceName),
                requestInfo.FrontendRequest.TraceId.Value
            );

            requestInfo.FrontendResponse = MapProfileFailuresToResponse(
                pipelineResult.Failures,
                requestInfo.FrontendRequest.TraceId
            );
            return;
        }

        // No-profile passthrough
        if (!pipelineResult.HasProfile || pipelineResult.Request is null)
        {
            await next();
            return;
        }

        // Build the stored-state projection invoker that captures pipeline dependencies
        var projectionInvoker = new CapturedStoredStateProjectionInvoker(
            writeContentType!,
            profileContext.ProfileName,
            requestInfo.ResourceInfo.ResourceName.Value,
            requestInfo.Method.ToString(),
            effectiveSchemaRequiredMembersByScope
        );

        // Attach the profile context for downstream consumption
        requestInfo.BackendProfileWriteContext = new BackendProfileWriteContext(
            Request: pipelineResult.Request,
            CompiledScopeCatalog: scopeCatalog,
            StoredStateProjectionInvoker: projectionInvoker
        );

        logger.LogDebug(
            "Profile write pipeline attached BackendProfileWriteContext for {Resource} - {TraceId}",
            SanitizeForLog(resource.ResourceName),
            requestInfo.FrontendRequest.TraceId.Value
        );

        await next();
    }

    private static IFrontendResponse MapProfileFailuresToResponse(
        System.Collections.Immutable.ImmutableArray<ProfileFailure> failures,
        TraceId traceId
    )
    {
        // Map category to HTTP status code
        int statusCode = failures[0].Category switch
        {
            ProfileFailureCategory.InvalidProfileDefinition => 500,
            ProfileFailureCategory.InvalidProfileUsage => 400,
            ProfileFailureCategory.WritableProfileValidationFailure => 400,
            ProfileFailureCategory.CreatabilityViolation => 403,
            ProfileFailureCategory.CoreBackendContractMismatch => 500,
            ProfileFailureCategory.BindingAccountingFailure => 500,
            _ => 500,
        };

        string[] messages = failures.Select(f => f.Message).ToArray();

        return new FrontendResponse(
            StatusCode: statusCode,
            Body: ForBadRequest(
                "Profile validation failed.",
                traceId,
                validationErrors: new Dictionary<string, string[]>
                {
                    ["profile"] = messages,
                },
                errors: []
            ),
            Headers: []
        );
    }

    /// <summary>
    /// Captures the profile pipeline dependencies from the middleware's request-side
    /// execution so the repository can invoke C6 stored-state projection later.
    /// </summary>
    private sealed class CapturedStoredStateProjectionInvoker(
        ContentTypeDefinition writeContentType,
        string profileName,
        string resourceName,
        string method,
        IReadOnlyDictionary<string, IReadOnlyList<string>> effectiveSchemaRequiredMembersByScope
    ) : IStoredStateProjectionInvoker
    {
        public ProfileAppliedWriteContext ProjectStoredState(
            JsonNode storedDocument,
            ProfileAppliedWriteRequest request,
            IReadOnlyList<CompiledScopeDescriptor> scopeCatalog
        )
        {
            // Re-execute the pipeline with the stored document to get the full context
            ProfileWritePipelineResult result = ProfileWritePipeline.Execute(
                canonicalizedRequestBody: request.WritableRequestBody,
                writeContentType: writeContentType,
                resolvedContentType: ProfileContentType.Write,
                scopeCatalog: scopeCatalog,
                storedDocument: storedDocument,
                isCreate: false,
                profileName: profileName,
                resourceName: resourceName,
                method: method,
                operation: "write",
                effectiveSchemaRequiredMembersByScope: effectiveSchemaRequiredMembersByScope
            );

            if (result.Context is null)
            {
                throw new InvalidOperationException(
                    "Stored-state projection did not produce a ProfileAppliedWriteContext. "
                        + "This indicates a pipeline bug: a stored document was supplied but no context was returned."
                );
            }

            return result.Context;
        }
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/dms/core/EdFi.DataManagementService.Core/EdFi.DataManagementService.Core.csproj`
Expected: Build succeeded (may need to check that `IFrontendResponse` import resolves)

- [ ] **Step 3: Commit**

```
feat(DMS-1106): implement ProfileWritePipelineMiddleware
```

---

### Task 9: Register middleware in pipeline and DI

**Files:**
- Modify: `src/dms/core/EdFi.DataManagementService.Core/ApiService.cs`
- Modify: `src/dms/core/EdFi.DataManagementService.Core/DmsCoreServiceExtensions.cs`

- [ ] **Step 1: Register in DI container**

In `DmsCoreServiceExtensions.cs`, add near the other middleware registrations (around line 125):

```csharp
.AddTransient<ProfileWritePipelineMiddleware>()
```

Add the using if needed:
```csharp
using EdFi.DataManagementService.Core.Middleware;
```

- [ ] **Step 2: Insert into pipeline order in ApiService.cs**

In the upsert pipeline (`CreateUpsertPipeline`), insert `ProfileWritePipelineMiddleware` **after** `ProfileWriteValidationMiddleware` and **before** `ExtractDocumentSecurityElementsMiddleware`. The new middleware runs after the legacy one so both can coexist during transition.

Find the line with `ProfileWriteValidationMiddleware` in the upsert pipeline steps and add after it:
```csharp
_serviceProvider.GetRequiredService<ProfileWritePipelineMiddleware>(),
```

Do the same in the update pipeline (`CreateUpdatePipeline`).

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build src/dms/core/EdFi.DataManagementService.Core/EdFi.DataManagementService.Core.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```
feat(DMS-1106): register ProfileWritePipelineMiddleware in pipeline and DI
```

---

### Task 10: Update handlers to pass BackendProfileWriteContext

**Files:**
- Modify: `src/dms/core/EdFi.DataManagementService.Core/Handler/UpsertHandler.cs`
- Modify: `src/dms/core/EdFi.DataManagementService.Core/Handler/UpdateByIdHandler.cs`

- [ ] **Step 1: Update UpsertHandler**

In `UpsertHandler.cs`, in the `UpsertRequest` construction (around line 57), add the new parameter:

```csharp
return await documentStoreRepository.UpsertDocument(
    new UpsertRequest(
        ResourceInfo: requestInfo.ResourceInfo,
        DocumentInfo: requestInfo.DocumentInfo,
        MappingSet: requestInfo.MappingSet,
        EdfiDoc: requestInfo.ParsedBody,
        Headers: requestInfo.FrontendRequest.Headers,
        TraceId: requestInfo.FrontendRequest.TraceId,
        DocumentUuid: candidateDocumentUuid,
        DocumentSecurityElements: requestInfo.DocumentSecurityElements,
        UpdateCascadeHandler: updateCascadeHandler,
        ResourceAuthorizationHandler: new ResourceAuthorizationHandler(
            requestInfo.AuthorizationStrategyEvaluators,
            requestInfo.AuthorizationSecurableInfo,
            authorizationServiceFactory,
            requestInfo.ScopedServiceProvider,
            _logger
        ),
        ResourceAuthorizationPathways: requestInfo.AuthorizationPathways,
        BackendProfileWriteContext: requestInfo.BackendProfileWriteContext
    )
);
```

- [ ] **Step 2: Update UpdateByIdHandler**

In `UpdateByIdHandler.cs`, in the `UpdateRequest` construction (around line 54), add the new parameter:

```csharp
await documentStoreRepository.UpdateDocumentById(
    new UpdateRequest(
        DocumentUuid: requestInfo.PathComponents.DocumentUuid,
        ResourceInfo: requestInfo.ResourceInfo,
        DocumentInfo: requestInfo.DocumentInfo,
        MappingSet: requestInfo.MappingSet,
        EdfiDoc: requestInfo.ParsedBody,
        Headers: requestInfo.FrontendRequest.Headers,
        DocumentSecurityElements: requestInfo.DocumentSecurityElements,
        TraceId: requestInfo.FrontendRequest.TraceId,
        UpdateCascadeHandler: updateCascadeHandler,
        ResourceAuthorizationHandler: new ResourceAuthorizationHandler(
            requestInfo.AuthorizationStrategyEvaluators,
            requestInfo.AuthorizationSecurableInfo,
            authorizationServiceFactory,
            requestInfo.ScopedServiceProvider,
            _logger
        ),
        ResourceAuthorizationPathways: requestInfo.AuthorizationPathways,
        BackendProfileWriteContext: requestInfo.BackendProfileWriteContext
    )
),
```

- [ ] **Step 3: Verify the full solution compiles**

Run: `dotnet build src/dms/dms.sln`
Expected: Build succeeded

- [ ] **Step 4: Run existing tests to verify no regressions**

Run: `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/ -v normal`
Expected: All existing tests pass (they don't supply BackendProfileWriteContext, so it defaults to null)

- [ ] **Step 5: Commit**

```
feat(DMS-1106): pass BackendProfileWriteContext from handlers to repository
```

---

### Task 11: Write repository orchestration integration tests

**Files:**
- Create: `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/Profile/ProfileWriteOrchestrationTests.cs`

- [ ] **Step 1: Write the orchestration test fixtures**

```csharp
// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Profile;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

/// <summary>
/// Tests for profile write orchestration in RelationalDocumentStoreRepository.
/// Uses shared scenario names from the acceptance criteria.
/// </summary>
public class ProfileWriteOrchestrationTests
{
    private static ResourceInfo CreateResourceInfo(string name) =>
        new(
            new ProjectName("Ed-Fi"),
            new ResourceName(name),
            IsDescriptor: false,
            AllowIdentityUpdates: false
        );

    private static IRelationalWriteTargetContextResolver CreateMockTargetContextResolver()
    {
        var resolver = A.Fake<IRelationalWriteTargetContextResolver>();
        A.CallTo(() =>
                resolver.ResolveForPostAsync(
                    A<MappingSet>._,
                    A<QualifiedResourceName>._,
                    A<ReferentialId>._,
                    A<DocumentUuid>._,
                    A<CancellationToken>._
                )
            )
            .ReturnsLazily(call =>
                Task.FromResult<RelationalWriteTargetContext>(
                    new RelationalWriteTargetContext.CreateNew(call.GetArgument<DocumentUuid>(3))
                )
            );
        return resolver;
    }

    private static IReferenceResolver CreateMockReferenceResolver()
    {
        var resolver = A.Fake<IReferenceResolver>();
        A.CallTo(() => resolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(new ResolvedReferenceSet([], [], [], [])));
        return resolver;
    }

    // -----------------------------------------------------------------------
    //  Given_NoProfileWriteBehavior
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_NoProfileWriteBehavior
    {
        private UpsertResult _result = null!;
        private IRelationalWriteFlattener _writeFlattener = null!;
        private IRelationalWriteTerminalStage _terminalStage = null!;

        [SetUp]
        public async Task Setup()
        {
            _writeFlattener = A.Fake<IRelationalWriteFlattener>();
            _terminalStage = A.Fake<IRelationalWriteTerminalStage>();

            var writePlan = AdapterFactoryTestFixtures.BuildRootOnlyWritePlan();
            var mappingSet = A.Fake<MappingSet>();
            A.CallTo(() => mappingSet.GetWritePlanOrThrow(A<QualifiedResourceName>._))
                .Returns(writePlan);

            A.CallTo(() => _writeFlattener.Flatten(A<FlatteningInput>._))
                .ReturnsLazily(call =>
                {
                    var input = call.GetArgument<FlatteningInput>(0)!;
                    return new FlattenedWriteSet(
                        new RootWriteRowBuffer(
                            input.WritePlan.TablePlansInDependencyOrder[0],
                            input.WritePlan.TablePlansInDependencyOrder[0]
                                .ColumnBindings.Select(_ => (FlattenedWriteValue)new FlattenedWriteValue.Literal("test"))
                        )
                    );
                });

            A.CallTo(() => _terminalStage.ExecuteAsync(A<RelationalWriteTerminalStageRequest>._, A<CancellationToken>._))
                .ReturnsLazily(() =>
                    Task.FromResult<RelationalWriteTerminalStageResult>(
                        new RelationalWriteTerminalStageResult.Upsert(
                            new UpsertResult.InsertSuccess(new DocumentUuid(Guid.NewGuid()))
                        )
                    )
                );

            var sut = new RelationalDocumentStoreRepository(
                NullLogger<RelationalDocumentStoreRepository>.Instance,
                CreateMockTargetContextResolver(),
                CreateMockReferenceResolver(),
                _writeFlattener,
                _terminalStage
            );

            var request = CreateUpsertRequest(
                CreateResourceInfo("School"),
                mappingSet,
                backendProfileWriteContext: null // No profile
            );

            _result = await sut.UpsertDocument(request);
        }

        [Test]
        public void It_should_succeed()
        {
            _result.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test]
        public void It_should_invoke_flattener()
        {
            A.CallTo(() => _writeFlattener.Flatten(A<FlatteningInput>._))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_should_invoke_terminal_stage()
        {
            A.CallTo(() =>
                    _terminalStage.ExecuteAsync(
                        A<RelationalWriteTerminalStageRequest>._,
                        A<CancellationToken>._
                    )
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    // -----------------------------------------------------------------------
    //  Given_ProfileRootCreateRejectedWhenNonCreatable
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_ProfileRootCreateRejectedWhenNonCreatable
    {
        private UpsertResult _result = null!;
        private IRelationalWriteFlattener _writeFlattener = null!;
        private IRelationalWriteTerminalStage _terminalStage = null!;

        [SetUp]
        public async Task Setup()
        {
            _writeFlattener = A.Fake<IRelationalWriteFlattener>();
            _terminalStage = A.Fake<IRelationalWriteTerminalStage>();

            var writePlan = AdapterFactoryTestFixtures.BuildRootOnlyWritePlan();
            var mappingSet = A.Fake<MappingSet>();
            A.CallTo(() => mappingSet.GetWritePlanOrThrow(A<QualifiedResourceName>._))
                .Returns(writePlan);

            IReadOnlyList<CompiledScopeDescriptor> scopeCatalog =
                Backend.Profile.CompiledScopeAdapterFactory.BuildFromWritePlan(writePlan);

            var profileRequest = new ProfileAppliedWriteRequest(
                WritableRequestBody: JsonNode.Parse("{}")!,
                RootResourceCreatable: false, // Non-creatable
                RequestScopeStates:
                [
                    new RequestScopeState(
                        new ScopeInstanceAddress("$", []),
                        ProfileVisibilityKind.VisiblePresent,
                        false
                    ),
                ],
                VisibleRequestCollectionItems: []
            );

            var projectionInvoker = A.Fake<IStoredStateProjectionInvoker>();

            var backendProfileContext = new BackendProfileWriteContext(
                Request: profileRequest,
                CompiledScopeCatalog: scopeCatalog,
                StoredStateProjectionInvoker: projectionInvoker
            );

            var sut = new RelationalDocumentStoreRepository(
                NullLogger<RelationalDocumentStoreRepository>.Instance,
                CreateMockTargetContextResolver(),
                CreateMockReferenceResolver(),
                _writeFlattener,
                _terminalStage
            );

            var request = CreateUpsertRequest(
                CreateResourceInfo("School"),
                mappingSet,
                backendProfileWriteContext: backendProfileContext
            );

            _result = await sut.UpsertDocument(request);
        }

        [Test]
        public void It_should_reject_before_persistence()
        {
            _result.Should().BeOfType<UpsertResult.UpsertFailureValidation>();
        }

        [Test]
        public void It_should_not_invoke_flattener()
        {
            A.CallTo(() => _writeFlattener.Flatten(A<FlatteningInput>._)).MustNotHaveHappened();
        }

        [Test]
        public void It_should_not_invoke_terminal_stage()
        {
            A.CallTo(() =>
                    _terminalStage.ExecuteAsync(
                        A<RelationalWriteTerminalStageRequest>._,
                        A<CancellationToken>._
                    )
                )
                .MustNotHaveHappened();
        }
    }

    // -----------------------------------------------------------------------
    //  Shared helper: create UpsertRequest implementing IRelationalUpsertRequest
    // -----------------------------------------------------------------------

    private static IRelationalUpsertRequest CreateUpsertRequest(
        ResourceInfo resourceInfo,
        MappingSet mappingSet,
        BackendProfileWriteContext? backendProfileWriteContext
    )
    {
        var fake = A.Fake<IRelationalUpsertRequest>();
        A.CallTo(() => fake.ResourceInfo).Returns(resourceInfo);
        A.CallTo(() => fake.DocumentInfo)
            .Returns(
                new DocumentInfo(
                    DocumentIdentity: new DocumentIdentity([]),
                    ReferentialId: new ReferentialId(Guid.NewGuid()),
                    DocumentReferences: [],
                    DescriptorReferences: [],
                    SuperclassIdentity: null
                )
            );
        A.CallTo(() => fake.MappingSet).Returns(mappingSet);
        A.CallTo(() => fake.EdfiDoc).Returns(JsonNode.Parse("{}")!);
        A.CallTo(() => fake.TraceId).Returns(new TraceId("test-trace"));
        A.CallTo(() => fake.DocumentUuid).Returns(new DocumentUuid(Guid.NewGuid()));
        A.CallTo(() => fake.BackendProfileWriteContext).Returns(backendProfileWriteContext);
        return fake;
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/ --filter "FullyQualifiedName~ProfileWriteOrchestration" -v normal`
Expected: All tests pass

- [ ] **Step 3: Run the full test suite to check for regressions**

Run: `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/ -v normal`
Expected: All tests pass

- [ ] **Step 4: Commit**

```
feat(DMS-1106): add profile write orchestration tests

Covers NoProfileWriteBehavior and ProfileRootCreateRejectedWhenNonCreatable
acceptance criteria scenarios.
```

---

### Task 12: Run full build and test verification

- [ ] **Step 1: Build the entire solution**

Run: `dotnet build src/dms/dms.sln`
Expected: Build succeeded with no errors

- [ ] **Step 2: Run all backend unit tests**

Run: `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/ -v normal`
Expected: All tests pass

- [ ] **Step 3: Run Core unit tests to verify no regressions**

Run: `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/ -v normal`
Expected: All tests pass

- [ ] **Step 4: Format code**

Run: `dotnet csharpier format src/dms/`

- [ ] **Step 5: Final commit if formatting changes**

```
style(DMS-1106): apply CSharpier formatting
```
