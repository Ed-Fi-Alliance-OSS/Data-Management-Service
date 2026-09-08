// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_PageReconstitutionTopology_With_Focused_Stable_Key_Extension_Fixture
{
    private const string FixturePath =
        "Fixtures/runtime-plan-compilation/focused-stable-key/positive/extension-child-collections/fixture.manifest.json";

    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");

    private ResourceReadPlan _readPlan = null!;
    private IReadOnlyDictionary<string, TableReconstitutionPlan> _tablePlansByScope = null!;
    private IReadOnlyDictionary<DbTableName, string> _scopeByTable = null!;

    [SetUp]
    public void SetUp()
    {
        var modelSet = RuntimePlanFixtureModelSetBuilder.Build(FixturePath, SqlDialect.Pgsql);
        var schoolModel = modelSet.ConcreteResourcesInNameOrder.Single(resource =>
            resource.ResourceKey.Resource == _schoolResource
        );

        _readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(schoolModel.RelationalModel);

        var compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(_readPlan);

        _tablePlansByScope = compiledPlan.TablePlansInDependencyOrder.ToDictionary(tablePlan =>
            tablePlan.TableModel.JsonScope.Canonical
        );
        _scopeByTable = compiledPlan.TablePlansInDependencyOrder.ToDictionary(
            tablePlan => tablePlan.Table,
            tablePlan => tablePlan.TableModel.JsonScope.Canonical
        );
    }

    [Test]
    public void It_should_resolve_immediate_parent_tables_for_base_and_extension_scopes()
    {
        _tablePlansByScope["$.addresses[*]"].ImmediateParentTable.Should().Be(_tablePlansByScope["$"].Table);
        _tablePlansByScope["$.addresses[*].periods[*]"]
            .ImmediateParentTable.Should()
            .Be(_tablePlansByScope["$.addresses[*]"].Table);
        _tablePlansByScope["$._ext.sample"].ImmediateParentTable.Should().Be(_tablePlansByScope["$"].Table);
        _tablePlansByScope["$._ext.sample.addresses[*]._ext.sample"]
            .ImmediateParentTable.Should()
            .Be(_tablePlansByScope["$.addresses[*]"].Table);
        _tablePlansByScope["$._ext.sample.interventions[*]"]
            .ImmediateParentTable.Should()
            .Be(_tablePlansByScope["$._ext.sample"].Table);
        _tablePlansByScope["$._ext.sample.interventions[*].visits[*]"]
            .ImmediateParentTable.Should()
            .Be(_tablePlansByScope["$._ext.sample.interventions[*]"].Table);
        _tablePlansByScope["$._ext.sample.addresses[*]._ext.sample.sponsorReferences[*]"]
            .ImmediateParentTable.Should()
            .Be(_tablePlansByScope["$._ext.sample.addresses[*]._ext.sample"].Table);
    }

    [Test]
    public void It_should_emit_immediate_children_in_dependency_order()
    {
        AssertImmediateChildren("$", "$.addresses[*]", "$._ext.sample");
        AssertImmediateChildren(
            "$.addresses[*]",
            "$.addresses[*].periods[*]",
            "$._ext.sample.addresses[*]._ext.sample"
        );
        AssertImmediateChildren("$._ext.sample", "$._ext.sample.interventions[*]");
        AssertImmediateChildren("$._ext.sample.interventions[*]", "$._ext.sample.interventions[*].visits[*]");
        AssertImmediateChildren(
            "$._ext.sample.addresses[*]._ext.sample",
            "$._ext.sample.addresses[*]._ext.sample.sponsorReferences[*]"
        );
    }

    private void AssertImmediateChildren(string parentScope, params string[] expectedChildScopes)
    {
        var expectedChildScopeSet = expectedChildScopes.ToHashSet(StringComparer.Ordinal);

        _tablePlansByScope[parentScope]
            .ImmediateChildrenInDependencyOrder.Select(table => _scopeByTable[table])
            .Should()
            .Equal(
                _readPlan
                    .TablePlansInDependencyOrder.Select(tablePlan => tablePlan.TableModel.JsonScope.Canonical)
                    .Where(expectedChildScopeSet.Contains)
            );
    }
}

[TestFixture]
public class Given_PageReconstitutionTopology_With_A_Missing_Aligned_Extension_Scope
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var readPlan = PageReconstitutionTopologyTestData.CreateMissingAlignedExtensionScopeReadPlan();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            CompiledReconstitutionPlanCache.GetOrBuild(readPlan)
        )!;
    }

    [Test]
    public void It_should_fail_instead_of_attaching_an_extension_child_collection_to_the_base_collection()
    {
        _exception.Message.Should().Contain("$.addresses[*]._ext.sample");
        _exception.Message.Should().Contain("SchoolExtensionAddressService");
        _exception.Message.Should().Contain("collection extension scope");
    }
}

[TestFixture]
public class Given_PageReconstitutionTopology_With_Ambiguous_Parent_Scope
{
    private Exception _exception = null!;

    [SetUp]
    public void SetUp()
    {
        var readPlan = PageReconstitutionTopologyTestData.CreateAmbiguousNestedCollectionReadPlan();

        _exception = Assert.Throws<InvalidOperationException>(() =>
            CompiledReconstitutionPlanCache.GetOrBuild(readPlan)
        )!;
    }

    [Test]
    public void It_should_fail_when_multiple_earlier_tables_match_the_expected_parent_scope()
    {
        _exception.Message.Should().Contain("$.addresses[*]");
        _exception.Message.Should().Contain("SchoolAddressPrimary");
        _exception.Message.Should().Contain("SchoolAddressReplica");
        _exception.Message.Should().Contain("exactly one root or collection table");
    }
}

file static class PageReconstitutionTopologyTestData
{
    private static readonly DbSchemaName _coreSchema = new("edfi");
    private static readonly DbSchemaName _sampleSchema = new("sample");

    public static ResourceReadPlan CreateMissingAlignedExtensionScopeReadPlan()
    {
        var rootTable = CreateRootTable();
        var addressTable = CreateAddressTable("SchoolAddress");
        var extensionChildCollectionTable = CreateExtensionChildCollectionTable();

        return CreateReadPlan(rootTable, addressTable, extensionChildCollectionTable);
    }

    public static ResourceReadPlan CreateAmbiguousNestedCollectionReadPlan()
    {
        var rootTable = CreateRootTable();
        var primaryAddressTable = CreateAddressTable("SchoolAddressPrimary");
        var replicaAddressTable = CreateAddressTable("SchoolAddressReplica");
        var nestedCollectionTable = CreateAddressPeriodTable();

        return CreateReadPlan(rootTable, primaryAddressTable, replicaAddressTable, nestedCollectionTable);
    }

    private static ResourceReadPlan CreateReadPlan(params DbTableModel[] tablesInDependencyOrder)
    {
        var model = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: _coreSchema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: tablesInDependencyOrder[0],
            TablesInDependencyOrder: tablesInDependencyOrder,
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ResourceReadPlan(
            Model: model,
            KeysetTable: KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            TablePlansInDependencyOrder: tablesInDependencyOrder.Select(table => new TableReadPlan(
                table,
                "SELECT 1;\n"
            )),
            ReferenceIdentityProjectionPlansInDependencyOrder: [],
            DescriptorProjectionPlansInOrder: []
        );
    }

    private static DbTableModel CreateRootTable()
    {
        return new DbTableModel(
            Table: new DbTableName(_coreSchema, "School"),
            JsonScope: CreatePath("$"),
            Key: new TableKey(
                ConstraintName: "PK_School",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart, isNullable: false),
                CreateColumn(
                    "SchoolId",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32),
                    isNullable: false,
                    sourceJsonPath: CreatePath("$.schoolId", new JsonPathSegment.Property("schoolId"))
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };
    }

    private static DbTableModel CreateAddressTable(string tableName)
    {
        return new DbTableModel(
            Table: new DbTableName(_coreSchema, tableName),
            JsonScope: CreatePath(
                "$.addresses[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: $"PK_{tableName}",
                Columns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey, isNullable: false),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart, isNullable: false),
                CreateColumn("Ordinal", ColumnKind.Ordinal, isNullable: false),
                CreateColumn(
                    "City",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    isNullable: false,
                    sourceJsonPath: CreatePath(
                        "$.addresses[*].city",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("city")
                    )
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                SemanticIdentityBindings: []
            ),
        };
    }

    private static DbTableModel CreateAddressPeriodTable()
    {
        return new DbTableModel(
            Table: new DbTableName(_coreSchema, "SchoolAddressPeriod"),
            JsonScope: CreatePath(
                "$.addresses[*].periods[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("periods"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_SchoolAddressPeriod",
                Columns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey, isNullable: false),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart, isNullable: false),
                CreateColumn("Address_CollectionItemId", ColumnKind.ParentKeyPart, isNullable: false),
                CreateColumn("Ordinal", ColumnKind.Ordinal, isNullable: false),
                CreateColumn(
                    "BeginDate",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Date),
                    isNullable: false,
                    sourceJsonPath: CreatePath(
                        "$.addresses[*].periods[*].beginDate",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("periods"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("beginDate")
                    )
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Collection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("Address_CollectionItemId")],
                SemanticIdentityBindings: []
            ),
        };
    }

    private static DbTableModel CreateExtensionChildCollectionTable()
    {
        return new DbTableModel(
            Table: new DbTableName(_sampleSchema, "SchoolExtensionAddressService"),
            JsonScope: CreatePath(
                "$.addresses[*]._ext.sample.services[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("_ext"),
                new JsonPathSegment.Property("sample"),
                new JsonPathSegment.Property("services"),
                new JsonPathSegment.AnyArrayElement()
            ),
            Key: new TableKey(
                ConstraintName: "PK_SchoolExtensionAddressService",
                Columns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            Columns:
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey, isNullable: false),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart, isNullable: false),
                CreateColumn("BaseCollectionItemId", ColumnKind.ParentKeyPart, isNullable: false),
                CreateColumn("Ordinal", ColumnKind.Ordinal, isNullable: false),
                CreateColumn(
                    "ServiceName",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 30),
                    isNullable: false,
                    sourceJsonPath: CreatePath(
                        "$.addresses[*]._ext.sample.services[*].serviceName",
                        new JsonPathSegment.Property("addresses"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("_ext"),
                        new JsonPathSegment.Property("sample"),
                        new JsonPathSegment.Property("services"),
                        new JsonPathSegment.AnyArrayElement(),
                        new JsonPathSegment.Property("serviceName")
                    )
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.ExtensionCollection,
                PhysicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
                RootScopeLocatorColumns: [new DbColumnName("School_DocumentId")],
                ImmediateParentScopeLocatorColumns: [new DbColumnName("BaseCollectionItemId")],
                SemanticIdentityBindings: []
            ),
        };
    }

    private static DbColumnModel CreateColumn(
        string name,
        ColumnKind kind,
        RelationalScalarType? scalarType = null,
        bool isNullable = false,
        JsonPathExpression? sourceJsonPath = null
    )
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(name),
            Kind: kind,
            ScalarType: scalarType ?? new RelationalScalarType(ScalarKind.Int64),
            IsNullable: isNullable,
            SourceJsonPath: sourceJsonPath,
            TargetResource: null
        );
    }

    private static JsonPathExpression CreatePath(string canonical, params JsonPathSegment[] segments) =>
        new(canonical, segments);
}
