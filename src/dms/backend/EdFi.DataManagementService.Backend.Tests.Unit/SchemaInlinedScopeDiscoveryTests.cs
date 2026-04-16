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
public class Given_SchemaInlinedScopeDiscovery_with_absolute_paths_under_non_root_tables
{
    [Test]
    public void It_does_not_duplicate_collection_scope_when_source_path_is_already_absolute()
    {
        var rootPlan = PlanBuilder.CreateTablePlan("School", "$", [("Name", "$.name")]);
        var collectionPlan = PlanBuilder.CreateTablePlan(
            tableName: "StudentSchoolAssociationAlternativeGraduationPlan",
            jsonScope: "$.alternativeGraduationPlans[*]",
            columns:
            [
                (
                    "EducationOrganizationId",
                    "$.alternativeGraduationPlans[*].alternativeGraduationPlanReference.educationOrganizationId"
                ),
            ]
        );

        var plan = PlanBuilder.BuildPlan([rootPlan, collectionPlan]);

        var result = SchemaInlinedScopeDiscovery.Discover(plan);

        result
            .Should()
            .ContainSingle(r =>
                r.JsonScope == "$.alternativeGraduationPlans[*].alternativeGraduationPlanReference"
            );
        result
            .Should()
            .NotContain(r =>
                r.JsonScope
                == "$.alternativeGraduationPlans[*].alternativeGraduationPlans[*].alternativeGraduationPlanReference"
            );
    }

    [Test]
    public void It_does_not_duplicate_root_extension_scope_when_source_path_is_already_absolute()
    {
        var rootPlan = PlanBuilder.CreateTablePlan("Student", "$", [("Id", "$.id")]);
        var extensionPlan = PlanBuilder.CreateTablePlan(
            tableName: "StudentExtension",
            jsonScope: "$._ext.sample",
            tableKind: DbTableKind.RootExtension,
            columns: [("ProgramName", "$._ext.sample.favoriteProgramReference.programName")]
        );

        var plan = PlanBuilder.BuildPlan([rootPlan, extensionPlan]);

        var result = SchemaInlinedScopeDiscovery.Discover(plan);

        result.Should().ContainSingle(r => r.JsonScope == "$._ext.sample.favoriteProgramReference");
        result.Should().NotContain(r => r.JsonScope == "$._ext.sample._ext.sample.favoriteProgramReference");
    }

    [Test]
    public void It_does_not_duplicate_extension_collection_scope_when_source_path_is_already_absolute()
    {
        var rootPlan = PlanBuilder.CreateTablePlan("StudentSectionAssociation", "$", [("Id", "$.id")]);
        var collectionPlan = PlanBuilder.CreateTablePlan(
            tableName: "StudentSectionAssociationExtensionRelatedGeneralStudentProgramAssociation",
            jsonScope: "$._ext.sample.relatedGeneralStudentProgramAssociations[*]",
            columns:
            [
                (
                    "BeginDate",
                    "$._ext.sample.relatedGeneralStudentProgramAssociations[*].relatedGeneralStudentProgramAssociationReference.beginDate"
                ),
            ]
        );

        var plan = PlanBuilder.BuildPlan([rootPlan, collectionPlan]);

        var result = SchemaInlinedScopeDiscovery.Discover(plan);

        result
            .Should()
            .ContainSingle(r =>
                r.JsonScope
                == "$._ext.sample.relatedGeneralStudentProgramAssociations[*].relatedGeneralStudentProgramAssociationReference"
            );
        result
            .Should()
            .NotContain(r =>
                r.JsonScope
                == "$._ext.sample.relatedGeneralStudentProgramAssociations[*].relatedGeneralStudentProgramAssociations[*].relatedGeneralStudentProgramAssociationReference"
            );
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
/// Pins the equivalence between backend-side scope discovery (walks column
/// <c>SourceJsonPath</c>s) and Core-side scope discovery (walks a profile
/// content-type tree). Even though the inputs differ, both must agree on the
/// set of <see cref="ScopeKind.NonCollection"/> inlined scopes for a given
/// resource when the profile is full-visibility. Drift here is exactly the
/// kind of contract mismatch that invited the hidden-member-path vocabulary
/// bug in DMS-1124.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_SchemaInlinedScopeDiscovery_and_ContentTypeScopeDiscovery_for_the_same_resource
{
    /// <summary>
    /// Verifies that <see cref="SchemaInlinedScopeDiscovery.Discover"/> and
    /// <see cref="ContentTypeScopeDiscovery.DiscoverInlinedScopes"/> agree on the
    /// set of <see cref="ScopeKind.NonCollection"/> inlined scopes for a fixture
    /// resource expressed under full-visibility in both representations.
    /// </summary>
    /// <remarks>
    /// The fixture covers three canonical inlined-scope patterns:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///     A root-level inlined reference (<c>$.calendarReference</c>): root table
    ///     holds columns <c>$.calendarReference.schoolId</c> and
    ///     <c>$.calendarReference.calendarCode</c>; no table carries
    ///     <c>$.calendarReference</c> as its scope.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     An inlined reference nested inside a collection
    ///     (<c>$.classPeriods[*].somethingReference</c>): the collection table at
    ///     <c>$.classPeriods[*]</c> holds a column
    ///     <c>$.somethingReference.schoolId</c>; no table carries
    ///     <c>$.classPeriods[*].somethingReference</c>.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     An inlined object nested within a table-backed extension scope
    ///     (<c>$._ext.sample.nestedRef</c>): the extension table at
    ///     <c>$._ext.sample</c> holds a column <c>$.nestedRef.someId</c>; the
    ///     extension table scope itself is table-backed and therefore appears in
    ///     <c>knownScopes</c>, but its nested object is inlined.
    ///     </description>
    ///   </item>
    /// </list>
    /// <para>
    /// <see cref="ContentTypeScopeDiscovery.DiscoverInlinedScopes"/> may also emit
    /// <see cref="ScopeKind.Collection"/> entries for collections that are absent
    /// from <c>knownScopes</c>. Because well-formed schemas always table-back
    /// collections, any such collection scopes would correspond to a table in the
    /// backend plan. Filtering to <see cref="ScopeKind.NonCollection"/> before
    /// comparing is therefore the correct equivalence boundary between the two
    /// inputs.
    /// </para>
    /// </remarks>
    [Test]
    public void It_agrees_on_NonCollection_inlined_scopes_for_a_full_visibility_resource()
    {
        // ── Backend: ResourceWritePlan ────────────────────────────────────────────

        // Root table: holds inlined calendarReference columns
        var rootPlan = PlanBuilder.CreateTablePlan(
            tableName: "School",
            jsonScope: "$",
            columns:
            [
                ("CalRef_SchoolId", "$.calendarReference.schoolId"),
                ("CalRef_CalendarCode", "$.calendarReference.calendarCode"),
                ("Name", "$.name"),
            ]
        );

        // Collection table: holds an inlined somethingReference inside classPeriods
        var classPeriodsPlan = PlanBuilder.CreateTablePlan(
            tableName: "SchoolClassPeriod",
            jsonScope: "$.classPeriods[*]",
            columns: [("SomethingRef_SchoolId", "$.somethingReference.schoolId")]
        );

        // Extension table (table-backed at $._ext.sample): holds inlined nestedRef
        var extensionPlan = PlanBuilder.CreateTablePlan(
            tableName: "SchoolExt",
            jsonScope: "$._ext.sample",
            columns: [("NestedRef_SomeId", "$.nestedRef.someId")]
        );

        var plan = PlanBuilder.BuildPlan([rootPlan, classPeriodsPlan, extensionPlan]);

        // ── knownScopes: table-backed scope paths from the write plan ─────────────
        var knownScopes = plan
            .TablePlansInDependencyOrder.Select(tp => tp.TableModel.JsonScope.Canonical)
            .ToHashSet(StringComparer.Ordinal);

        // ── Core: ContentTypeDefinition (full-visibility, same structure) ─────────
        var contentType = new ContentTypeDefinition(
            MemberSelection: MemberSelection.IncludeAll,
            Properties: [new PropertyRule("name")],
            Objects:
            [
                new ObjectRule(
                    Name: "calendarReference",
                    MemberSelection: MemberSelection.IncludeAll,
                    LogicalSchema: null,
                    Properties: [new PropertyRule("schoolId"), new PropertyRule("calendarCode")],
                    NestedObjects: null,
                    Collections: null,
                    Extensions: null
                ),
            ],
            Collections:
            [
                new CollectionRule(
                    Name: "classPeriods",
                    MemberSelection: MemberSelection.IncludeAll,
                    LogicalSchema: null,
                    Properties: null,
                    NestedObjects:
                    [
                        new ObjectRule(
                            Name: "somethingReference",
                            MemberSelection: MemberSelection.IncludeAll,
                            LogicalSchema: null,
                            Properties: [new PropertyRule("schoolId")],
                            NestedObjects: null,
                            Collections: null,
                            Extensions: null
                        ),
                    ],
                    NestedCollections: null,
                    Extensions: null,
                    ItemFilter: null
                ),
            ],
            Extensions:
            [
                new ExtensionRule(
                    Name: "sample",
                    MemberSelection: MemberSelection.IncludeAll,
                    LogicalSchema: null,
                    Properties: null,
                    Objects:
                    [
                        new ObjectRule(
                            Name: "nestedRef",
                            MemberSelection: MemberSelection.IncludeAll,
                            LogicalSchema: null,
                            Properties: [new PropertyRule("someId")],
                            NestedObjects: null,
                            Collections: null,
                            Extensions: null
                        ),
                    ],
                    Collections: null
                ),
            ]
        );

        // ── Act ───────────────────────────────────────────────────────────────────
        var backend = SchemaInlinedScopeDiscovery.Discover(plan);
        var core = ContentTypeScopeDiscovery.DiscoverInlinedScopes(contentType, knownScopes);

        // ── Assert: NonCollection scopes must be identical ────────────────────────
        // Filter Core output to NonCollection only (see <remarks> above).
        var backendScopes = backend
            .Where(r => r.Kind == ScopeKind.NonCollection)
            .Select(r => r.JsonScope)
            .ToHashSet(StringComparer.Ordinal);

        var coreScopes = core.Where(r => r.Kind == ScopeKind.NonCollection)
            .Select(r => r.JsonScope)
            .ToHashSet(StringComparer.Ordinal);

        backendScopes.Should().BeEquivalentTo(coreScopes);
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

    /// <summary>
    /// Creates a minimal TableWritePlan for a nested collection table (parent is another collection,
    /// not the root). Adds a parent-scope-locator column referencing the parent collection's stable
    /// row identity and wires <see cref="DbTableIdentityMetadata.ImmediateParentScopeLocatorColumns"/>
    /// so the adapter can resolve the parent row in the ancestor chain.
    /// </summary>
    public static TableWritePlan CreateNestedCollectionTablePlan(
        string tableName,
        string jsonScope,
        string parentTableName,
        IEnumerable<(string ColumnName, string? SourcePath)> columns,
        IEnumerable<string> semanticIdentityRelativePaths
    )
    {
        var scopeSegments = ParsePathSegments(jsonScope);
        var identityPaths = semanticIdentityRelativePaths.ToList();

        // Parent scope locator column: references parent collection's CollectionItemId
        var parentLocatorColumnName = $"{parentTableName}CollectionItemId";

        var columnModels = new List<DbColumnModel>
        {
            // ParentScopeLocator (references parent collection's stable row identity)
            new(
                new DbColumnName(parentLocatorColumnName),
                ColumnKind.ParentKeyPart,
                null,
                false,
                null,
                null,
                new ColumnStorage.Stored()
            ),
            // CollectionItemId (this table's own stable row identity)
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
                [new DbKeyColumn(new DbColumnName(parentLocatorColumnName), ColumnKind.ParentKeyPart)]
            ),
            columnModels,
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName(parentLocatorColumnName)],
                [new DbColumnName(parentLocatorColumnName)],
                [new DbColumnName(parentLocatorColumnName)],
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
            InsertSql: $"insert into edfi.\"{tableName}\" values (@ParentId)",
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
