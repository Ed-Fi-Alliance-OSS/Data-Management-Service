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
    private TableReconstitutionPlan _rootTablePlan = null!;
    private TableReconstitutionPlan _childTablePlan = null!;

    [SetUp]
    public void SetUp()
    {
        var readPlan = HydrationTestHelper.BuildSchoolReadPlan("edfi", SqlDialect.Pgsql);
        _compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(readPlan);
        _rootTablePlan = _compiledPlan.TablePlansInDependencyOrder[0];
        _childTablePlan = _compiledPlan.TablePlansInDependencyOrder[1];
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

    [Test]
    public void It_should_resolve_table_plans_by_table_name()
    {
        _compiledPlan.GetTablePlanOrThrow(_rootTablePlan.Table).Should().BeSameAs(_rootTablePlan);
    }

    [Test]
    public void It_should_fail_when_the_requested_table_is_missing()
    {
        var missingTable = new DbTableName(new DbSchemaName("edfi"), "MissingSchoolTable");

        var exception = Assert.Throws<KeyNotFoundException>(() =>
            _compiledPlan.GetTablePlanOrThrow(missingTable)
        )!;

        exception.Message.Should().Contain("Ed-Fi.School");
        exception.Message.Should().Contain("does not contain table");
        exception.Message.Should().Contain(missingTable.ToString());
    }

    [Test]
    public void It_should_fail_when_duplicate_table_plans_are_supplied()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new CompiledReconstitutionPlan(
                _compiledPlan.ReadPlan,
                [_rootTablePlan, _rootTablePlan],
                _compiledPlan.PropertyOrder
            )
        )!;

        exception.Message.Should().Contain("Ed-Fi.School");
        exception.Message.Should().Contain("contains duplicate table");
        exception.Message.Should().Contain(_rootTablePlan.Table.ToString());
    }

    [Test]
    public void It_should_resolve_single_locator_ordinals()
    {
        _rootTablePlan.ResolveSingleRootScopeLocatorOrdinalOrThrow().Should().Be(0);
        _childTablePlan.ResolveSingleImmediateParentScopeLocatorOrdinalOrThrow().Should().Be(1);
    }

    [Test]
    public void It_should_fail_when_root_scope_locator_count_is_not_one()
    {
        var invalidTablePlan = _rootTablePlan with { RootScopeLocatorOrdinals = [] };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            invalidTablePlan.ResolveSingleRootScopeLocatorOrdinalOrThrow()
        )!;

        exception.Message.Should().Contain(_rootTablePlan.Table.ToString());
        exception.Message.Should().Contain("requires exactly one root-scope locator ordinal");
        exception.Message.Should().Contain("but found 0");
    }

    [Test]
    public void It_should_fail_when_immediate_parent_locator_count_is_not_one()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            _rootTablePlan.ResolveSingleImmediateParentScopeLocatorOrdinalOrThrow()
        )!;

        exception.Message.Should().Contain(_rootTablePlan.Table.ToString());
        exception.Message.Should().Contain("requires exactly one immediate-parent locator ordinal");
        exception.Message.Should().Contain("but found 0");
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
public class Given_CompiledReconstitutionPlanTests_With_Property_Order
{
    private CompiledReconstitutionPlan _compiledPlan = null!;

    [SetUp]
    public void SetUp()
    {
        _compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(
            CompiledReconstitutionPlanTestData.CreatePropertyOrderReadPlan()
        );
    }

    [Test]
    public void It_should_include_table_scope_scalar_source_and_reference_object_paths()
    {
        _compiledPlan
            .PropertyOrder.ChildrenInOrder.Select(static child => child.Key)
            .Should()
            .Equal(
                "calendarReference",
                "educationOrganizationReferences",
                "schoolReference",
                "studentUniqueId"
            );
    }

    [Test]
    public void It_should_include_reference_identity_field_paths()
    {
        var schoolReferenceOrder = _compiledPlan.PropertyOrder.ChildrenInOrder.Single(static child =>
            child.Key == "schoolReference"
        );

        schoolReferenceOrder
            .Value.ChildrenInOrder.Select(static child => child.Key)
            .Should()
            .Equal("schoolId");
    }
}

[TestFixture]
public class Given_CompiledReconstitutionPlanTests_With_Sibling_Collections
{
    private CompiledReconstitutionPlan _compiledPlan = null!;

    [SetUp]
    public void SetUp()
    {
        _compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(
            CompiledReconstitutionPlanTestData.CreateSiblingCollectionReadPlan()
        );
    }

    [Test]
    public void It_should_keep_sibling_tables_in_dependency_order()
    {
        _compiledPlan
            .TablePlansInDependencyOrder.Select(static tablePlan => tablePlan.TableModel.JsonScope.Canonical)
            .Should()
            .Equal("$", "$.addresses[*]", "$.contacts[*]");
    }
}

[TestFixture]
public class Given_CompiledReconstitutionPlanTests_With_Invalid_Topology
{
    [Test]
    public void It_should_report_multiple_root_tables_after_parent_resolution()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompiledReconstitutionPlanCache.GetOrBuild(
                CompiledReconstitutionPlanTestData.CreateMultipleRootTablesReadPlan()
            )
        )!;

        exception.Message.Should().Contain("Ed-Fi.StudentSchoolAssociation");
        exception.Message.Should().Contain("expected exactly one root table in page topology, but found 2.");
    }

    [Test]
    public void It_should_report_missing_root_parent_for_root_extension_tables()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompiledReconstitutionPlanCache.GetOrBuild(
                CompiledReconstitutionPlanTestData.CreateRootExtensionWithoutRootReadPlan()
            )
        )!;

        exception.Message.Should().Contain("Ed-Fi.StudentSchoolAssociation");
        exception.Message.Should().Contain("table 'edfi.SchoolExtension'");
        exception.Message.Should().Contain("at scope '$._ext.Sample'");
        exception.Message.Should().Contain("expected exactly one root table at scope '$', but found none.");
    }

    [Test]
    public void It_should_ignore_non_root_tables_at_root_scope_when_resolving_root_extension_parent()
    {
        var compiledPlan = CompiledReconstitutionPlanCache.GetOrBuild(
            CompiledReconstitutionPlanTestData.CreateRootExtensionWithNonRootCandidateAtRootScopeReadPlan()
        );

        var rootTable = new DbTableName(new DbSchemaName("edfi"), "School");
        var extensionTable = new DbTableName(new DbSchemaName("edfi"), "SchoolExtension");

        compiledPlan.GetTablePlanOrThrow(extensionTable).ImmediateParentTable.Should().Be(rootTable);
    }

    [Test]
    public void It_should_report_missing_collection_parent_for_collection_extension_scope_tables()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompiledReconstitutionPlanCache.GetOrBuild(
                CompiledReconstitutionPlanTestData.CreateCollectionExtensionScopeWithoutCollectionReadPlan()
            )
        )!;

        exception.Message.Should().Contain("Ed-Fi.StudentSchoolAssociation");
        exception.Message.Should().Contain("table 'edfi.SchoolAddressExtension'");
        exception.Message.Should().Contain("at scope '$.addresses[*]._ext.Sample'");
        exception
            .Message.Should()
            .Contain(
                "expected exactly one collection table aligned to the extended base scope at scope '$.addresses[*]', but found none."
            );
    }

    [Test]
    public void It_should_report_ambiguous_parent_table_matches()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompiledReconstitutionPlanCache.GetOrBuild(
                CompiledReconstitutionPlanTestData.CreateAmbiguousCollectionParentReadPlan()
            )
        )!;

        exception.Message.Should().Contain("Ed-Fi.StudentSchoolAssociation");
        exception.Message.Should().Contain("table 'edfi.SchoolAddress'");
        exception.Message.Should().Contain("at scope '$.addresses[*]'");
        exception
            .Message.Should()
            .Contain(
                "expected exactly one root or collection table at scope '$', but found 2: 'edfi.School', 'edfi.AlternateSchool'."
            );
    }

    [Test]
    public void It_should_report_unsupported_table_kinds()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompiledReconstitutionPlanCache.GetOrBuild(
                CompiledReconstitutionPlanTestData.CreateUnsupportedTableKindReadPlan()
            )
        )!;

        exception.Message.Should().Contain("Ed-Fi.StudentSchoolAssociation");
        exception
            .Message.Should()
            .Contain(
                "table 'edfi.UnsupportedTable' uses unsupported table kind 'Unspecified' for page topology."
            );
    }
}

[TestFixture]
public class Given_CompiledReconstitutionPlanTests_With_Invalid_Hydration_Metadata
{
    [Test]
    public void It_should_report_missing_root_scope_locator_columns()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompiledReconstitutionPlanCache.GetOrBuild(
                CompiledReconstitutionPlanTestData.CreateMissingRootScopeLocatorColumnReadPlan()
            )
        )!;

        exception
            .Message.Should()
            .Contain(
                "Cannot build compiled reconstitution plan for 'edfi.School': root-scope locator column 'MissingDocumentId' does not exist in hydration select-list columns."
            );
    }

    [Test]
    public void It_should_report_missing_immediate_parent_locator_columns()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompiledReconstitutionPlanCache.GetOrBuild(
                CompiledReconstitutionPlanTestData.CreateMissingImmediateParentLocatorColumnReadPlan()
            )
        )!;

        exception
            .Message.Should()
            .Contain(
                "Cannot build compiled reconstitution plan for 'edfi.SchoolAddress': immediate-parent locator column 'MissingDocumentId' does not exist in hydration select-list columns."
            );
    }

    [Test]
    public void It_should_report_missing_physical_row_identity_columns()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompiledReconstitutionPlanCache.GetOrBuild(
                CompiledReconstitutionPlanTestData.CreateMissingPhysicalRowIdentityColumnReadPlan()
            )
        )!;

        exception
            .Message.Should()
            .Contain(
                "Cannot build compiled reconstitution plan for 'edfi.School': physical-row identity column 'MissingRowId' does not exist in hydration select-list columns."
            );
    }

    [Test]
    public void It_should_report_empty_explicit_root_scope_locator_metadata()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompiledReconstitutionPlanCache.GetOrBuild(
                CompiledReconstitutionPlanTestData.CreateEmptyRootScopeLocatorMetadataReadPlan()
            )
        )!;

        exception
            .Message.Should()
            .Contain(
                "Cannot compile compiled reconstitution plan for 'edfi.School': expected exactly one explicit root-scope locator column, but found 0."
            );
    }

    [Test]
    public void It_should_report_empty_explicit_immediate_parent_locator_metadata()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompiledReconstitutionPlanCache.GetOrBuild(
                CompiledReconstitutionPlanTestData.CreateEmptyImmediateParentLocatorMetadataReadPlan()
            )
        )!;

        exception
            .Message.Should()
            .Contain(
                "Cannot compile compiled reconstitution plan for 'edfi.SchoolAddress': explicit immediate-parent scope locator metadata is empty for non-root table kind 'Collection'."
            );
    }

    [Test]
    public void It_should_report_multiple_ordinal_columns()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompiledReconstitutionPlanCache.GetOrBuild(
                CompiledReconstitutionPlanTestData.CreateMultipleOrdinalColumnsReadPlan()
            )
        )!;

        exception
            .Message.Should()
            .Contain(
                "Cannot build compiled reconstitution plan for 'edfi.School': multiple ordinal columns were found."
            );
    }

    [Test]
    public void It_should_report_duplicate_hydration_columns()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompiledReconstitutionPlanCache.GetOrBuild(
                CompiledReconstitutionPlanTestData.CreateDuplicateHydrationColumnReadPlan()
            )
        )!;

        exception
            .Message.Should()
            .Contain(
                "Cannot build compiled reconstitution plan for 'edfi.School': duplicate hydration column 'DocumentId' was encountered."
            );
    }

    [Test]
    public void It_should_report_duplicate_tables_when_building_column_ordinals()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            CompiledReconstitutionPlanCache.GetOrBuild(
                CompiledReconstitutionPlanTestData.CreateDuplicateTableReadPlan()
            )
        )!;

        exception
            .Message.Should()
            .Contain(
                "Cannot build compiled reconstitution plan for resource 'Ed-Fi.StudentSchoolAssociation': duplicate table 'edfi.School' was encountered."
            );
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
    public void It_should_accept_lazy_enumerables()
    {
        var parts = new object?[] { 1, (short)2, "A" }.Select(static part => part);

        new ScopeKey(parts).Should().Be(_first);
    }

    [Test]
    public void It_should_hash_equal_scope_parts_after_numeric_canonicalization()
    {
        HashSet<ScopeKey> keys = [_first];

        keys.Contains(_second).Should().BeTrue();
        _first.GetHashCode().Should().Be(_second.GetHashCode());
    }

    [Test]
    public void It_should_match_itself()
    {
        _first.Equals(_first).Should().BeTrue();
    }

    [Test]
    public void It_should_not_match_keys_with_different_part_counts()
    {
        _first.Equals(new ScopeKey([1L, 2L])).Should().BeFalse();
    }

    [Test]
    public void It_should_not_match_keys_with_different_part_values()
    {
        _first.Equals(new ScopeKey([1L, 2L, "B"])).Should().BeFalse();
    }
}

file static class CompiledReconstitutionPlanTestData
{
    private static readonly DbSchemaName _schema = new("edfi");

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

    public static ResourceReadPlan CreatePropertyOrderReadPlan()
    {
        var rootTable = CreateRootTable(
            "StudentSchoolAssociation",
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn(
                    "StudentUSI",
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64),
                    CreatePath("$.studentUniqueId", new JsonPathSegment.Property("studentUniqueId"))
                ),
                CreateColumn("School_DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("School_SchoolId", ColumnKind.Scalar),
                CreateColumn("Calendar_DocumentId", ColumnKind.ParentKeyPart),
            ]
        );
        var educationOrganizationReferenceTable = CreateCollectionTable(
            "StudentSchoolAssociationEducationOrganizationReference",
            "$.educationOrganizationReferences[*]",
            "educationOrganizationReferences"
        );

        return CreateReadPlan(
            [rootTable, educationOrganizationReferenceTable],
            [
                new ReferenceIdentityProjectionTablePlan(
                    rootTable.Table,
                    [
                        new ReferenceIdentityProjectionBinding(
                            IsIdentityComponent: false,
                            ReferenceObjectPath: CreatePath(
                                "$.calendarReference",
                                new JsonPathSegment.Property("calendarReference")
                            ),
                            TargetResource: new QualifiedResourceName("Ed-Fi", "Calendar"),
                            FkColumnOrdinal: 4,
                            IdentityFieldOrdinalsInOrder: []
                        ),
                        new ReferenceIdentityProjectionBinding(
                            IsIdentityComponent: false,
                            ReferenceObjectPath: CreatePath(
                                "$.schoolReference",
                                new JsonPathSegment.Property("schoolReference")
                            ),
                            TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
                            FkColumnOrdinal: 2,
                            IdentityFieldOrdinalsInOrder:
                            [
                                new ReferenceIdentityProjectionFieldOrdinal(
                                    ReferenceJsonPath: CreatePath(
                                        "$.schoolReference.schoolId",
                                        new JsonPathSegment.Property("schoolReference"),
                                        new JsonPathSegment.Property("schoolId")
                                    ),
                                    ColumnOrdinal: 3,
                                    ScalarType: new RelationalScalarType(ScalarKind.Int64)
                                ),
                            ]
                        ),
                    ]
                ),
            ]
        );
    }

    public static ResourceReadPlan CreateSiblingCollectionReadPlan()
    {
        var rootTable = CreateRootTable("School", [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)]);
        var addressTable = CreateCollectionTable("SchoolAddress", "$.addresses[*]", "addresses");
        var contactTable = CreateCollectionTable("SchoolContact", "$.contacts[*]", "contacts");

        return CreateReadPlan([rootTable, addressTable, contactTable], []);
    }

    public static ResourceReadPlan CreateMultipleRootTablesReadPlan()
    {
        var rootTable = CreateRootTable("School", [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)]);
        var alternateRootTable = CreateRootTable(
            "AlternateSchool",
            [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)]
        );

        return CreateReadPlan([rootTable, alternateRootTable], []);
    }

    public static ResourceReadPlan CreateRootExtensionWithoutRootReadPlan()
    {
        var rootExtensionTable = CreateRootExtensionTable("SchoolExtension");

        return CreateReadPlan([rootExtensionTable], []);
    }

    public static ResourceReadPlan CreateRootExtensionWithNonRootCandidateAtRootScopeReadPlan()
    {
        var rootTable = CreateRootTable("School", [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)]);
        var nonRootCandidateAtRootScope = CreateTable(
            tableName: "SchoolRootScopeExtension",
            jsonScope: CreatePath("$"),
            keyColumns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)],
            columns: [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)],
            tableKind: DbTableKind.RootExtension,
            physicalRowIdentityColumns: [new DbColumnName("DocumentId")],
            rootScopeLocatorColumns: [new DbColumnName("DocumentId")],
            immediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")]
        );
        var rootExtensionTable = CreateRootExtensionTable("SchoolExtension");

        return CreateReadPlan([rootTable, nonRootCandidateAtRootScope, rootExtensionTable], []);
    }

    public static ResourceReadPlan CreateCollectionExtensionScopeWithoutCollectionReadPlan()
    {
        var rootTable = CreateRootTable("School", [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)]);
        var extensionScopeTable = CreateCollectionExtensionScopeTable("SchoolAddressExtension");

        return CreateReadPlan([rootTable, extensionScopeTable], []);
    }

    public static ResourceReadPlan CreateAmbiguousCollectionParentReadPlan()
    {
        var rootTable = CreateRootTable("School", [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)]);
        var alternateRootTable = CreateRootTable(
            "AlternateSchool",
            [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)]
        );
        var addressTable = CreateCollectionTable("SchoolAddress", "$.addresses[*]", "addresses");

        return CreateReadPlan([rootTable, alternateRootTable, addressTable], []);
    }

    public static ResourceReadPlan CreateUnsupportedTableKindReadPlan()
    {
        var rootTable = CreateRootTable("School", [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)]);
        var unsupportedTable = CreateTable(
            tableName: "UnsupportedTable",
            jsonScope: CreatePath(
                "$.unsupported[*]",
                new JsonPathSegment.Property("unsupported"),
                new JsonPathSegment.AnyArrayElement()
            ),
            keyColumns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)],
            columns: [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)],
            tableKind: DbTableKind.Unspecified,
            physicalRowIdentityColumns: [new DbColumnName("DocumentId")],
            rootScopeLocatorColumns: [new DbColumnName("DocumentId")],
            immediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")]
        );

        return CreateReadPlan([rootTable, unsupportedTable], []);
    }

    public static ResourceReadPlan CreateMissingRootScopeLocatorColumnReadPlan()
    {
        var rootTable = CreateTable(
            tableName: "School",
            jsonScope: CreatePath("$"),
            keyColumns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)],
            columns: [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)],
            tableKind: DbTableKind.Root,
            physicalRowIdentityColumns: [new DbColumnName("DocumentId")],
            rootScopeLocatorColumns: [new DbColumnName("MissingDocumentId")],
            immediateParentScopeLocatorColumns: []
        );

        return CreateReadPlan([rootTable], []);
    }

    public static ResourceReadPlan CreateMissingImmediateParentLocatorColumnReadPlan()
    {
        var rootTable = CreateRootTable("School", [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)]);
        var addressTable = CreateTable(
            tableName: "SchoolAddress",
            jsonScope: CreatePath(
                "$.addresses[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement()
            ),
            keyColumns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)],
            columns:
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey),
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
            ],
            tableKind: DbTableKind.Collection,
            physicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
            rootScopeLocatorColumns: [new DbColumnName("DocumentId")],
            immediateParentScopeLocatorColumns: [new DbColumnName("MissingDocumentId")]
        );

        return CreateReadPlan([rootTable, addressTable], []);
    }

    public static ResourceReadPlan CreateMissingPhysicalRowIdentityColumnReadPlan()
    {
        var rootTable = CreateTable(
            tableName: "School",
            jsonScope: CreatePath("$"),
            keyColumns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)],
            columns: [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)],
            tableKind: DbTableKind.Root,
            physicalRowIdentityColumns: [new DbColumnName("MissingRowId")],
            rootScopeLocatorColumns: [new DbColumnName("DocumentId")],
            immediateParentScopeLocatorColumns: []
        );

        return CreateReadPlan([rootTable], []);
    }

    public static ResourceReadPlan CreateEmptyRootScopeLocatorMetadataReadPlan()
    {
        var rootTable = CreateTable(
            tableName: "School",
            jsonScope: CreatePath("$"),
            keyColumns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)],
            columns: [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)],
            tableKind: DbTableKind.Root,
            physicalRowIdentityColumns: [new DbColumnName("DocumentId")],
            rootScopeLocatorColumns: [],
            immediateParentScopeLocatorColumns: []
        );

        return CreateReadPlan([rootTable], []);
    }

    public static ResourceReadPlan CreateEmptyImmediateParentLocatorMetadataReadPlan()
    {
        var rootTable = CreateRootTable("School", [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)]);
        var addressTable = CreateTable(
            tableName: "SchoolAddress",
            jsonScope: CreatePath(
                "$.addresses[*]",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement()
            ),
            keyColumns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)],
            columns:
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey),
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
            ],
            tableKind: DbTableKind.Collection,
            physicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
            rootScopeLocatorColumns: [new DbColumnName("DocumentId")],
            immediateParentScopeLocatorColumns: []
        );

        return CreateReadPlan([rootTable, addressTable], []);
    }

    public static ResourceReadPlan CreateMultipleOrdinalColumnsReadPlan()
    {
        var rootTable = CreateRootTable(
            "School",
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("FirstOrdinal", ColumnKind.Ordinal),
                CreateColumn("SecondOrdinal", ColumnKind.Ordinal),
            ]
        );

        return CreateReadPlan([rootTable], []);
    }

    public static ResourceReadPlan CreateDuplicateHydrationColumnReadPlan()
    {
        var rootTable = CreateRootTable(
            "School",
            [
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
            ]
        );

        return CreateReadPlan([rootTable], []);
    }

    public static ResourceReadPlan CreateDuplicateTableReadPlan()
    {
        var rootTable = CreateRootTable("School", [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)]);
        var duplicateRootTable = CreateRootTable(
            "School",
            [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)]
        );

        return CreateReadPlan([rootTable, duplicateRootTable], []);
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

    private static ResourceReadPlan CreateReadPlan(
        IReadOnlyList<DbTableModel> tablesInDependencyOrder,
        IReadOnlyList<ReferenceIdentityProjectionTablePlan> referencePlans
    )
    {
        var model = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation"),
            PhysicalSchema: _schema,
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: tablesInDependencyOrder[0],
            TablesInDependencyOrder: tablesInDependencyOrder,
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ResourceReadPlan(
            Model: model,
            KeysetTable: KeysetTableConventions.GetKeysetTableContract(SqlDialect.Pgsql),
            TablePlansInDependencyOrder: tablesInDependencyOrder.Select(static table => new TableReadPlan(
                table,
                "SELECT 1;\n"
            )),
            ReferenceIdentityProjectionPlansInDependencyOrder: referencePlans,
            DescriptorProjectionPlansInOrder: []
        );
    }

    private static DbTableModel CreateRootTable(string tableName, IReadOnlyList<DbColumnModel> columns)
    {
        return CreateTable(
            tableName: tableName,
            jsonScope: CreatePath("$"),
            keyColumns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)],
            columns: columns,
            tableKind: DbTableKind.Root,
            physicalRowIdentityColumns: [new DbColumnName("DocumentId")],
            rootScopeLocatorColumns: [new DbColumnName("DocumentId")],
            immediateParentScopeLocatorColumns: []
        );
    }

    private static DbTableModel CreateCollectionTable(
        string tableName,
        string canonicalScope,
        string scopeProperty
    )
    {
        return CreateTable(
            tableName: tableName,
            jsonScope: CreatePath(
                canonicalScope,
                new JsonPathSegment.Property(scopeProperty),
                new JsonPathSegment.AnyArrayElement()
            ),
            keyColumns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)],
            columns:
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey),
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
            ],
            tableKind: DbTableKind.Collection,
            physicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
            rootScopeLocatorColumns: [new DbColumnName("DocumentId")],
            immediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")]
        );
    }

    private static DbTableModel CreateRootExtensionTable(string tableName)
    {
        return CreateTable(
            tableName: tableName,
            jsonScope: CreatePath(
                "$._ext.Sample",
                new JsonPathSegment.Property("_ext"),
                new JsonPathSegment.Property("Sample")
            ),
            keyColumns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)],
            columns: [CreateColumn("DocumentId", ColumnKind.ParentKeyPart)],
            tableKind: DbTableKind.RootExtension,
            physicalRowIdentityColumns: [new DbColumnName("DocumentId")],
            rootScopeLocatorColumns: [new DbColumnName("DocumentId")],
            immediateParentScopeLocatorColumns: [new DbColumnName("DocumentId")]
        );
    }

    private static DbTableModel CreateCollectionExtensionScopeTable(string tableName)
    {
        return CreateTable(
            tableName: tableName,
            jsonScope: CreatePath(
                "$.addresses[*]._ext.Sample",
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("_ext"),
                new JsonPathSegment.Property("Sample")
            ),
            keyColumns: [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)],
            columns:
            [
                CreateColumn("CollectionItemId", ColumnKind.CollectionKey),
                CreateColumn("DocumentId", ColumnKind.ParentKeyPart),
            ],
            tableKind: DbTableKind.CollectionExtensionScope,
            physicalRowIdentityColumns: [new DbColumnName("CollectionItemId")],
            rootScopeLocatorColumns: [new DbColumnName("DocumentId")],
            immediateParentScopeLocatorColumns: [new DbColumnName("CollectionItemId")]
        );
    }

    private static DbTableModel CreateTable(
        string tableName,
        JsonPathExpression jsonScope,
        IReadOnlyList<DbKeyColumn> keyColumns,
        IReadOnlyList<DbColumnModel> columns,
        DbTableKind tableKind,
        IReadOnlyList<DbColumnName> physicalRowIdentityColumns,
        IReadOnlyList<DbColumnName> rootScopeLocatorColumns,
        IReadOnlyList<DbColumnName> immediateParentScopeLocatorColumns
    )
    {
        return new DbTableModel(
            Table: new DbTableName(_schema, tableName),
            JsonScope: jsonScope,
            Key: new TableKey(ConstraintName: $"PK_{tableName}", Columns: keyColumns),
            Columns: columns,
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: tableKind,
                PhysicalRowIdentityColumns: physicalRowIdentityColumns,
                RootScopeLocatorColumns: rootScopeLocatorColumns,
                ImmediateParentScopeLocatorColumns: immediateParentScopeLocatorColumns,
                SemanticIdentityBindings: []
            ),
        };
    }

    private static DbColumnModel CreateColumn(
        string name,
        ColumnKind kind,
        RelationalScalarType? scalarType = null,
        JsonPathExpression? sourceJsonPath = null
    )
    {
        return new DbColumnModel(
            ColumnName: new DbColumnName(name),
            Kind: kind,
            ScalarType: scalarType ?? new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: sourceJsonPath,
            TargetResource: null
        );
    }

    private static JsonPathExpression CreatePath(string canonical, params JsonPathSegment[] segments) =>
        new(canonical, segments);
}
