// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_CompiledReconstitutionPlanTests_Cache
{
    private CompiledReconstitutionPlan _first = null!;
    private CompiledReconstitutionPlan _second = null!;

    [SetUp]
    public void SetUp()
    {
        var readPlan = CompiledReconstitutionPlanTestData.CreateDescriptorProjectionReadPlan();
        _first = CompiledReconstitutionPlanCache.GetOrBuild(readPlan);
        _second = CompiledReconstitutionPlanCache.GetOrBuild(readPlan);
    }

    [Test]
    public void It_should_return_the_same_compiled_plan_for_the_same_read_plan()
    {
        _second.Should().BeSameAs(_first);
    }

    [Test]
    public void It_should_cache_property_ordering_once()
    {
        _second.PropertyOrder.Should().BeSameAs(_first.PropertyOrder);
        _first
            .PropertyOrder.ChildrenInOrder.Select(static child => child.Key)
            .Should()
            .Equal("academicSubjectDescriptor", "gradeLevelDescriptor");
    }
}

[TestFixture]
public class Given_CompiledReconstitutionPlanTests_With_Table_Metadata
{
    private CompiledReconstitutionPlan _compiledPlan = null!;

    [SetUp]
    public void SetUp()
    {
        var readPlan = HydrationTestHelper.BuildSchoolReadPlan("edfi", SqlDialect.Pgsql);
        _compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(readPlan);
    }

    [Test]
    public void It_should_capture_root_and_child_locator_identity_and_ordinal_metadata()
    {
        _compiledPlan.TablePlansInDependencyOrder.Should().HaveCount(3);

        var rootTablePlan = _compiledPlan.TablePlansInDependencyOrder[0];
        rootTablePlan.RootScopeLocatorOrdinals.Should().Equal(0);
        rootTablePlan.ImmediateParentScopeLocatorOrdinals.Should().BeEmpty();
        rootTablePlan.PhysicalRowIdentityOrdinals.Should().Equal(0);
        rootTablePlan.OrdinalColumnOrdinal.Should().BeNull();

        var childTablePlan = _compiledPlan.TablePlansInDependencyOrder[1];
        childTablePlan.RootScopeLocatorOrdinals.Should().Equal(1);
        childTablePlan.ImmediateParentScopeLocatorOrdinals.Should().Equal(1);
        childTablePlan.PhysicalRowIdentityOrdinals.Should().Equal(0);
        childTablePlan.OrdinalColumnOrdinal.Should().Be(2);

        var nestedChildTablePlan = _compiledPlan.TablePlansInDependencyOrder[2];
        nestedChildTablePlan.RootScopeLocatorOrdinals.Should().Equal(1);
        nestedChildTablePlan.ImmediateParentScopeLocatorOrdinals.Should().Equal(2);
        nestedChildTablePlan.PhysicalRowIdentityOrdinals.Should().Equal(0);
        nestedChildTablePlan.OrdinalColumnOrdinal.Should().Be(3);
    }
}

[TestFixture]
public class Given_CompiledReconstitutionPlanTests_With_Table_Local_Projection_Bindings
{
    private TableReconstitutionPlan _referenceTablePlan = null!;
    private TableReconstitutionPlan _descriptorTablePlan = null!;

    [SetUp]
    public void SetUp()
    {
        var referenceReadPlan = HydrationTestHelper.BuildStudentSchoolAssociationReadPlan(
            "edfi",
            SqlDialect.Pgsql
        );
        var descriptorReadPlan = CompiledReconstitutionPlanTestData.CreateDescriptorProjectionReadPlan();

        _referenceTablePlan = CompiledReconstitutionPlanCache
            .GetOrBuild(referenceReadPlan)
            .TablePlansInDependencyOrder[0];
        _descriptorTablePlan = CompiledReconstitutionPlanCache
            .GetOrBuild(descriptorReadPlan)
            .TablePlansInDependencyOrder[0];
    }

    [Test]
    public void It_should_group_reference_identity_bindings_by_table_once()
    {
        _referenceTablePlan.ReferenceBindingsInOrder.Should().HaveCount(2);
        _referenceTablePlan
            .ReferenceBindingsInOrder.Select(static binding => binding.ReferenceObjectPath.Canonical)
            .Should()
            .Equal("$.schoolReference", "$.calendarReference");
        _referenceTablePlan
            .ReferenceBindingsInOrder.Select(static binding => binding.FkColumnOrdinal)
            .Should()
            .Equal(1, 3);
    }

    [Test]
    public void It_should_flatten_descriptor_bindings_from_compiled_projection_plans()
    {
        _descriptorTablePlan.DescriptorBindingsInOrder.Should().HaveCount(2);
        _descriptorTablePlan
            .DescriptorBindingsInOrder.Select(static binding => binding.DescriptorValuePath.Canonical)
            .Should()
            .Equal("$.gradeLevelDescriptor", "$.academicSubjectDescriptor");
        _descriptorTablePlan
            .DescriptorBindingsInOrder.Select(static binding => binding.DescriptorIdColumnOrdinal)
            .Should()
            .Equal(9, 7);
    }
}

[TestFixture]
public class Given_CompiledReconstitutionPlanTests_With_ScopeKey
{
    private ScopeKey _first = null!;
    private ScopeKey _second = null!;

    [SetUp]
    public void SetUp()
    {
        _first = new ScopeKey([1, (short)2, "A"]);
        _second = new ScopeKey([1L, 2L, "A"]);
    }

    [Test]
    public void It_should_use_structural_equality_over_canonicalized_parts()
    {
        _first.Should().Be(_second);
    }

    [Test]
    public void It_should_hash_equal_scope_parts_after_numeric_canonicalization()
    {
        HashSet<ScopeKey> keys = [_first];

        keys.Contains(_second).Should().BeTrue();
        _first.GetHashCode().Should().Be(_second.GetHashCode());
    }
}

file static class CompiledReconstitutionPlanTestData
{
    public static ResourceReadPlan CreateDescriptorProjectionReadPlan()
    {
        var readPlan = new ReadPlanCompiler(SqlDialect.Pgsql).Compile(CreateDescriptorProjectionModel());
        var descriptorSources = readPlan
            .DescriptorProjectionPlansInOrder.SelectMany(static plan => plan.SourcesInOrder)
            .ToArray();

        return readPlan with
        {
            DescriptorProjectionPlansInOrder =
            [
                new DescriptorProjectionPlan(
                    SelectByKeysetSql: "SELECT descriptor_plan_1;\n",
                    ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                    SourcesInOrder: [descriptorSources[1] with { DescriptorIdColumnOrdinal = 9 }]
                ),
                new DescriptorProjectionPlan(
                    SelectByKeysetSql: "SELECT descriptor_plan_0;\n",
                    ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                    SourcesInOrder: [descriptorSources[0] with { DescriptorIdColumnOrdinal = 7 }]
                ),
            ],
        };
    }

    private static RelationalResourceModel CreateDescriptorProjectionModel()
    {
        var resource = new QualifiedResourceName("Ed-Fi", "Assessment");
        var descriptorSchema = new DbSchemaName("edfi");
        var academicSubjectDescriptor = new QualifiedResourceName("Ed-Fi", "AcademicSubjectDescriptor");
        var gradeLevelDescriptor = new QualifiedResourceName("Ed-Fi", "GradeLevelDescriptor");

        var rootTable = new DbTableModel(
            Table: new DbTableName(descriptorSchema, "Assessment"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_Assessment",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("AcademicSubjectDescriptorId"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression(
                        "$.academicSubjectDescriptor",
                        [new JsonPathSegment.Property("academicSubjectDescriptor")]
                    ),
                    TargetResource: academicSubjectDescriptor
                ),
                new DbColumnModel(
                    ColumnName: new DbColumnName("GradeLevelDescriptorId"),
                    Kind: ColumnKind.DescriptorFk,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: true,
                    SourceJsonPath: new JsonPathExpression(
                        "$.gradeLevelDescriptor",
                        [new JsonPathSegment.Property("gradeLevelDescriptor")]
                    ),
                    TargetResource: gradeLevelDescriptor
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

        return new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: descriptorSchema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources:
            [
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: new JsonPathExpression(
                        "$.academicSubjectDescriptor",
                        [new JsonPathSegment.Property("academicSubjectDescriptor")]
                    ),
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("AcademicSubjectDescriptorId"),
                    DescriptorResource: academicSubjectDescriptor
                ),
                new DescriptorEdgeSource(
                    IsIdentityComponent: false,
                    DescriptorValuePath: new JsonPathExpression(
                        "$.gradeLevelDescriptor",
                        [new JsonPathSegment.Property("gradeLevelDescriptor")]
                    ),
                    Table: rootTable.Table,
                    FkColumn: new DbColumnName("GradeLevelDescriptorId"),
                    DescriptorResource: gradeLevelDescriptor
                ),
            ]
        );
    }
}
