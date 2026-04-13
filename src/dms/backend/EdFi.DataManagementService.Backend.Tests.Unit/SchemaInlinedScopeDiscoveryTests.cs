// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

// TODO(DMS-1124 Task 4d): Add a consistency test comparing SchemaInlinedScopeDiscovery.Discover output
// to ContentTypeScopeDiscovery.DiscoverInlinedScopes for a fixture resource once the NoProfileSyntheticProfileAdapter
// adapter surface stabilizes.

[TestFixture]
[Parallelizable]
public class Given_SchemaInlinedScopeDiscovery_with_no_inlined_scopes
{
    [Test]
    public void It_returns_empty_when_all_columns_are_direct_leaves()
    {
        // Root table columns: $.schoolId, $.nameOfInstitution — no intermediate scopes
        var plan = PlanBuilder.BuildPlanWithRootOnly([
            ("SchoolId", "$.schoolId"),
            ("NameOfInstitution", "$.nameOfInstitution"),
        ]);

        var result = SchemaInlinedScopeDiscovery.Discover(plan);

        result.Should().BeEmpty();
    }
}

[TestFixture]
[Parallelizable]
public class Given_SchemaInlinedScopeDiscovery_with_root_level_inlined_reference
{
    [Test]
    public void It_discovers_inlined_reference_scope_exactly_once()
    {
        // Root table has columns: $.calendarReference.schoolId, $.calendarReference.calendarCode
        // No table with JsonScope "$.calendarReference" exists.
        var plan = PlanBuilder.BuildPlanWithRootOnly([
            ("CalRef_SchoolId", "$.calendarReference.schoolId"),
            ("CalRef_CalendarCode", "$.calendarReference.calendarCode"),
            ("Name", "$.name"),
        ]);

        var result = SchemaInlinedScopeDiscovery.Discover(plan);

        result.Should().ContainSingle();
        result[0].JsonScope.Should().Be("$.calendarReference");
        result[0].Kind.Should().Be(ScopeKind.NonCollection);
    }

    [Test]
    public void It_deduplicates_inlined_scope_appearing_multiple_times()
    {
        // Both columns belong to the same inlined scope — expect it only once
        var plan = PlanBuilder.BuildPlanWithRootOnly([
            ("CalRef_SchoolId", "$.calendarReference.schoolId"),
            ("CalRef_CalendarCode", "$.calendarReference.calendarCode"),
        ]);

        var result = SchemaInlinedScopeDiscovery.Discover(plan);

        result.Should().ContainSingle(r => r.JsonScope == "$.calendarReference");
    }
}

[TestFixture]
[Parallelizable]
public class Given_SchemaInlinedScopeDiscovery_with_collection_level_inlined_reference
{
    [Test]
    public void It_discovers_inlined_reference_under_collection_scope()
    {
        // Collection table at "$.classPeriods" has a column whose relative path is
        // "$.classPeriodReference.schoolId" — making the absolute path
        // "$.classPeriods.classPeriodReference.schoolId".
        // No table at "$.classPeriods.classPeriodReference".
        var plan = PlanBuilder.BuildPlanWithRootAndCollection(
            rootColumns: [("Name", "$.name")],
            collectionScope: "$.classPeriods",
            collectionColumns: [("Ref_SchoolId", "$.classPeriodReference.schoolId")]
        );

        var result = SchemaInlinedScopeDiscovery.Discover(plan);

        result.Should().ContainSingle();
        result[0].JsonScope.Should().Be("$.classPeriods.classPeriodReference");
        result[0].Kind.Should().Be(ScopeKind.NonCollection);
    }
}

[TestFixture]
[Parallelizable]
public class Given_SchemaInlinedScopeDiscovery_with_nested_inlined_scopes
{
    [Test]
    public void It_discovers_intermediate_scope_under_collection()
    {
        // Collection table at "$.addresses" has column "$.address.city"
        // making absolute "$.addresses.address.city" — expect "$.addresses.address"
        var plan = PlanBuilder.BuildPlanWithRootAndCollection(
            rootColumns: [("Name", "$.name")],
            collectionScope: "$.addresses",
            collectionColumns: [("City", "$.address.city")]
        );

        var result = SchemaInlinedScopeDiscovery.Discover(plan);

        result.Should().ContainSingle(r => r.JsonScope == "$.addresses.address");
    }

    [Test]
    public void It_discovers_all_intermediate_levels_for_deep_column_path()
    {
        // Root table column "$.a.b.c.d" — no tables for $.a, $.a.b, $.a.b.c — all three inlined
        var plan = PlanBuilder.BuildPlanWithRootOnly([("Deep", "$.a.b.c.d")]);

        var result = SchemaInlinedScopeDiscovery.Discover(plan);

        var scopes = result.Select(r => r.JsonScope).ToList();
        scopes.Should().BeEquivalentTo("$.a", "$.a.b", "$.a.b.c");
        result.Should().OnlyContain(r => r.Kind == ScopeKind.NonCollection);
    }

    [Test]
    public void It_returns_sorted_results()
    {
        // Two inlined scopes — result should be sorted
        var plan = PlanBuilder.BuildPlanWithRootOnly([
            ("ZField", "$.z.field"),
            ("AField", "$.a.field"),
            ("BField", "$.b.c.field"),
        ]);

        var result = SchemaInlinedScopeDiscovery.Discover(plan);

        var scopes = result.Select(r => r.JsonScope).ToList();
        scopes.Should().BeInAscendingOrder();
    }
}

[TestFixture]
[Parallelizable]
public class Given_SchemaInlinedScopeDiscovery_with_table_backed_scope
{
    [Test]
    public void It_excludes_table_backed_scope_from_inlined_results()
    {
        // Root table has column "$.sharedThing.innerField"
        // but "$.sharedThing" IS the JsonScope of another table
        var sharedThingRootPlan = PlanBuilder.CreateTablePlan(
            tableName: "SharedThing",
            jsonScope: "$.sharedThing",
            columns: [("InnerField", "$.innerField")]
        );

        var rootPlan = PlanBuilder.CreateTablePlan(
            tableName: "School",
            jsonScope: "$",
            columns: [("SharedThingField", "$.sharedThing.innerField")]
        );

        var plan = PlanBuilder.BuildPlan([rootPlan, sharedThingRootPlan]);

        var result = SchemaInlinedScopeDiscovery.Discover(plan);

        result.Should().NotContain(r => r.JsonScope == "$.sharedThing");
    }

    [Test]
    public void It_excludes_all_table_backed_scopes_and_includes_only_inlined()
    {
        // Root table has columns: $.calendarReference.schoolId (inlined), $.sharedThing.innerField (table-backed)
        // A second table covers "$.sharedThing"
        var sharedThingPlan = PlanBuilder.CreateTablePlan(
            tableName: "SharedThing",
            jsonScope: "$.sharedThing",
            columns: [("InnerField", "$.innerField")]
        );

        var rootPlan = PlanBuilder.CreateTablePlan(
            tableName: "School",
            jsonScope: "$",
            columns:
            [
                ("CalRef_SchoolId", "$.calendarReference.schoolId"),
                ("SharedThingField", "$.sharedThing.innerField"),
            ]
        );

        var plan = PlanBuilder.BuildPlan([rootPlan, sharedThingPlan]);

        var result = SchemaInlinedScopeDiscovery.Discover(plan);

        result.Should().ContainSingle(r => r.JsonScope == "$.calendarReference");
        result.Should().NotContain(r => r.JsonScope == "$.sharedThing");
    }
}

[TestFixture]
[Parallelizable]
public class Given_SchemaInlinedScopeDiscovery_null_plan
{
    [Test]
    public void It_throws_ArgumentNullException_for_null_plan()
    {
        var action = () => SchemaInlinedScopeDiscovery.Discover(null!);
        action.Should().Throw<ArgumentNullException>();
    }
}

/// <summary>
/// Minimal fixture helpers for SchemaInlinedScopeDiscovery tests only.
/// These produce the smallest valid ResourceWritePlan / TableWritePlan possible.
/// </summary>
internal static class PlanBuilder
{
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");

    /// <summary>
    /// Builds a plan with a single root table whose columns carry the specified SourceJsonPaths.
    /// Columns with a non-null path are Scalar; columns listed with null path are ParentKeyPart.
    /// </summary>
    public static ResourceWritePlan BuildPlanWithRootOnly(
        IEnumerable<(string ColumnName, string? SourcePath)> columns
    )
    {
        var rootPlan = CreateTablePlan("School", "$", columns);
        return BuildPlan([rootPlan]);
    }

    /// <summary>
    /// Builds a plan with a root table plus one collection table.
    /// </summary>
    public static ResourceWritePlan BuildPlanWithRootAndCollection(
        IEnumerable<(string ColumnName, string? SourcePath)> rootColumns,
        string collectionScope,
        IEnumerable<(string ColumnName, string? SourcePath)> collectionColumns
    )
    {
        var rootPlan = CreateTablePlan("School", "$", rootColumns);
        var collectionPlan = CreateTablePlan("SchoolCollection", collectionScope, collectionColumns);
        return BuildPlan([rootPlan, collectionPlan]);
    }

    /// <summary>
    /// Builds a ResourceWritePlan from a list of already-created TableWritePlans.
    /// </summary>
    public static ResourceWritePlan BuildPlan(IReadOnlyList<TableWritePlan> tablePlans)
    {
        var resourceModel = new RelationalResourceModel(
            Resource: _schoolResource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: tablePlans[0].TableModel,
            TablesInDependencyOrder: tablePlans.Select(tp => tp.TableModel).ToList(),
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        return new ResourceWritePlan(resourceModel, tablePlans);
    }

    /// <summary>
    /// Creates a minimal TableWritePlan with specified JsonScope and column source paths.
    /// Columns with non-null SourcePath are modeled as Scalar; null paths produce ParentKeyPart columns.
    /// The plan always prepends a synthetic DocumentId (ParentKeyPart, no SourcePath) as column[0].
    /// </summary>
    public static TableWritePlan CreateTablePlan(
        string tableName,
        string jsonScope,
        IEnumerable<(string ColumnName, string? SourcePath)> columns
    )
    {
        var scopeSegments = ParsePathSegments(jsonScope);

        var columnModels = new List<DbColumnModel>
        {
            // Synthetic key column so the plan has at least one column
            new(
                new DbColumnName("DocumentId"),
                ColumnKind.ParentKeyPart,
                null,
                false,
                null,
                null,
                new ColumnStorage.Stored()
            ),
        };

        foreach (var (colName, sourcePath) in columns)
        {
            var pathExpr = sourcePath is not null
                ? (JsonPathExpression?)new JsonPathExpression(sourcePath, ParsePathSegments(sourcePath))
                : null;

            columnModels.Add(
                new DbColumnModel(
                    new DbColumnName(colName),
                    sourcePath is not null ? ColumnKind.Scalar : ColumnKind.ParentKeyPart,
                    sourcePath is not null
                        ? new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                        : null,
                    true,
                    pathExpr,
                    null,
                    new ColumnStorage.Stored()
                )
            );
        }

        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), tableName),
            new JsonPathExpression(jsonScope, scopeSegments),
            new TableKey(
                $"PK_{tableName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            columnModels,
            []
        )
        {
            // Use Unspecified for non-root to avoid TableWritePlan collection-contract validation.
            // SchemaInlinedScopeDiscovery reads JsonScope.Canonical only; TableKind is irrelevant.
            IdentityMetadata = new DbTableIdentityMetadata(
                jsonScope == "$" ? DbTableKind.Root : DbTableKind.Unspecified,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        // Minimal bindings: one per column
        var bindings = columnModels
            .Select(
                (col, i) =>
                    new WriteColumnBinding(col, new WriteValueSource.DocumentId(), col.ColumnName.Value)
            )
            .ToList();

        return new TableWritePlan(
            tableModel,
            InsertSql: $"insert into edfi.\"{tableName}\" values (@DocumentId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, bindings.Count, 1000),
            ColumnBindings: bindings,
            KeyUnificationPlans: []
        );
    }

    /// <summary>
    /// Creates a minimal TableWritePlan with specified JsonScope, column source paths, and explicit table kind.
    /// For adapter tests that need proper Root/RootExtension table kinds.
    /// </summary>
    public static TableWritePlan CreateTablePlan(
        string tableName,
        string jsonScope,
        DbTableKind tableKind,
        IEnumerable<(string ColumnName, string? SourcePath)> columns
    )
    {
        var scopeSegments = ParsePathSegments(jsonScope);

        var columnModels = new List<DbColumnModel>
        {
            new(
                new DbColumnName("DocumentId"),
                ColumnKind.ParentKeyPart,
                null,
                false,
                null,
                null,
                new ColumnStorage.Stored()
            ),
        };

        foreach (var (colName, sourcePath) in columns)
        {
            var pathExpr = sourcePath is not null
                ? (JsonPathExpression?)new JsonPathExpression(sourcePath, ParsePathSegments(sourcePath))
                : null;

            columnModels.Add(
                new DbColumnModel(
                    new DbColumnName(colName),
                    sourcePath is not null ? ColumnKind.Scalar : ColumnKind.ParentKeyPart,
                    sourcePath is not null
                        ? new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                        : null,
                    true,
                    pathExpr,
                    null,
                    new ColumnStorage.Stored()
                )
            );
        }

        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), tableName),
            new JsonPathExpression(jsonScope, scopeSegments),
            new TableKey(
                $"PK_{tableName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            columnModels,
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                tableKind,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        var bindings = columnModels
            .Select(
                (col, i) =>
                    new WriteColumnBinding(col, new WriteValueSource.DocumentId(), col.ColumnName.Value)
            )
            .ToList();

        return new TableWritePlan(
            tableModel,
            InsertSql: $"insert into edfi.\"{tableName}\" values (@DocumentId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, bindings.Count, 1000),
            ColumnBindings: bindings,
            KeyUnificationPlans: []
        );
    }

    /// <summary>
    /// Creates a minimal TableWritePlan for a collection table with a CollectionMergePlan.
    /// Needed for tests that create CollectionWriteCandidate instances (which validate TableKind).
    /// </summary>
    public static TableWritePlan CreateCollectionTablePlan(
        string tableName,
        string jsonScope,
        IEnumerable<(string ColumnName, string? SourcePath)> columns,
        IEnumerable<string> semanticIdentityRelativePaths
    )
    {
        var scopeSegments = ParsePathSegments(jsonScope);
        var identityPaths = semanticIdentityRelativePaths.ToList();

        var columnModels = new List<DbColumnModel>
        {
            // DocumentId (parent key part)
            new(
                new DbColumnName("DocumentId"),
                ColumnKind.ParentKeyPart,
                null,
                false,
                null,
                null,
                new ColumnStorage.Stored()
            ),
            // CollectionItemId (stable row identity)
            new(
                new DbColumnName("CollectionItemId"),
                ColumnKind.ParentKeyPart,
                null,
                false,
                null,
                null,
                new ColumnStorage.Stored()
            ),
            // Ordinal
            new(
                new DbColumnName("Ordinal"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.Int32),
                false,
                null,
                null,
                new ColumnStorage.Stored()
            ),
        };

        var userColumnStartIndex = columnModels.Count;

        foreach (var (colName, sourcePath) in columns)
        {
            var pathExpr = sourcePath is not null
                ? (JsonPathExpression?)new JsonPathExpression(sourcePath, ParsePathSegments(sourcePath))
                : null;

            columnModels.Add(
                new DbColumnModel(
                    new DbColumnName(colName),
                    sourcePath is not null ? ColumnKind.Scalar : ColumnKind.ParentKeyPart,
                    sourcePath is not null
                        ? new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                        : null,
                    true,
                    pathExpr,
                    null,
                    new ColumnStorage.Stored()
                )
            );
        }

        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), tableName),
            new JsonPathExpression(jsonScope, scopeSegments),
            new TableKey(
                $"PK_{tableName}",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            columnModels,
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        var bindings = columnModels
            .Select(
                (col, i) =>
                    new WriteColumnBinding(col, new WriteValueSource.DocumentId(), col.ColumnName.Value)
            )
            .ToList();

        // Build semantic identity bindings pointing into the binding list
        var semanticIdentityBindings = identityPaths
            .Select(relPath =>
            {
                // Find the column whose source path matches
                var bindingIndex = columnModels.FindIndex(c =>
                    c.SourceJsonPath.HasValue && c.SourceJsonPath.Value.Canonical == relPath
                );
                if (bindingIndex < 0)
                {
                    bindingIndex = userColumnStartIndex; // fallback
                }

                return new CollectionMergeSemanticIdentityBinding(
                    new JsonPathExpression(relPath, ParsePathSegments(relPath)),
                    bindingIndex
                );
            })
            .ToList();

        var collectionMergePlan = new CollectionMergePlan(
            SemanticIdentityBindings: semanticIdentityBindings,
            StableRowIdentityBindingIndex: 1, // CollectionItemId
            UpdateByStableRowIdentitySql: $"update edfi.\"{tableName}\" set Ordinal=@Ordinal where CollectionItemId=@CollectionItemId",
            DeleteByStableRowIdentitySql: $"delete from edfi.\"{tableName}\" where CollectionItemId=@CollectionItemId",
            OrdinalBindingIndex: 2, // Ordinal column
            CompareBindingIndexesInOrder: Enumerable.Range(0, bindings.Count)
        );

        var collectionKeyPreallocationPlan = new CollectionKeyPreallocationPlan(
            new DbColumnName("CollectionItemId"),
            BindingIndex: 1
        );

        return new TableWritePlan(
            tableModel,
            InsertSql: $"insert into edfi.\"{tableName}\" values (@DocumentId)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, bindings.Count, 1000),
            ColumnBindings: bindings,
            KeyUnificationPlans: [],
            CollectionMergePlan: collectionMergePlan,
            CollectionKeyPreallocationPlan: collectionKeyPreallocationPlan
        );
    }

    private static IReadOnlyList<JsonPathSegment> ParsePathSegments(string path)
    {
        // Minimal: split on "." skipping "$", convert "[*]" suffix to AnyArrayElement
        var segments = new List<JsonPathSegment>();
        var parts = path.Split('.');

        foreach (var part in parts)
        {
            if (part == "$")
            {
                continue;
            }

            if (part.EndsWith("[*]", StringComparison.Ordinal))
            {
                segments.Add(new JsonPathSegment.Property(part[..^3]));
                segments.Add(new JsonPathSegment.AnyArrayElement());
            }
            else
            {
                segments.Add(new JsonPathSegment.Property(part));
            }
        }

        return segments;
    }
}
