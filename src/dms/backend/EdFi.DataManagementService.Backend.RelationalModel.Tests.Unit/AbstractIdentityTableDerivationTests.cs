// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for abstract identity table derivation.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_Derivation
{
    private AbstractIdentityTableInfo _abstractIdentityTable = default!;
    private AbstractUnionViewInfo _abstractUnionView = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = BuildProjectSchema(mismatchMemberType: false);
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new AbstractIdentityTableDerivationPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _abstractIdentityTable = result.AbstractIdentityTablesInNameOrder.Single(table =>
            table.AbstractResourceKey.Resource.ResourceName == "EducationOrganization"
        );
        _abstractUnionView = result.AbstractUnionViewsInNameOrder.Single(view =>
            view.AbstractResourceKey.Resource.ResourceName == "EducationOrganization"
        );
    }

    /// <summary>
    /// It should order columns with document id identity and discriminator.
    /// </summary>
    [Test]
    public void It_should_order_columns_with_document_id_identity_and_discriminator()
    {
        var columns = _abstractIdentityTable
            .TableModel.Columns.Select(column => column.ColumnName.Value)
            .ToArray();

        columns.Should().Equal("DocumentId", "EducationOrganizationId", "OrganizationName", "Discriminator");
    }

    /// <summary>
    /// It should define document id key and column.
    /// </summary>
    [Test]
    public void It_should_define_document_id_key_and_column()
    {
        var table = _abstractIdentityTable.TableModel;
        var keyColumns = table.Key.Columns.Select(column => column.ColumnName.Value).ToArray();

        keyColumns.Should().Equal("DocumentId");
        table.Key.Columns.Single().Kind.Should().Be(ColumnKind.ParentKeyPart);

        var documentIdColumn = table.Columns.Single(column => column.ColumnName.Value == "DocumentId");

        documentIdColumn.Kind.Should().Be(ColumnKind.ParentKeyPart);
        documentIdColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Int64));
        documentIdColumn.IsNullable.Should().BeFalse();
        documentIdColumn.SourceJsonPath.Should().BeNull();
        documentIdColumn.TargetResource.Should().BeNull();
    }

    /// <summary>
    /// It should include discriminator column.
    /// </summary>
    [Test]
    public void It_should_include_discriminator_column()
    {
        var discriminator = _abstractIdentityTable.TableModel.Columns.Single(column =>
            column.ColumnName.Value == "Discriminator"
        );

        discriminator.Kind.Should().Be(ColumnKind.Scalar);
        discriminator.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.String, 256));
        discriminator.IsNullable.Should().BeFalse();
    }

    /// <summary>
    /// It should include composite unique constraint.
    /// </summary>
    [Test]
    public void It_should_include_composite_unique_constraint()
    {
        var unique = _abstractIdentityTable.TableModel.Constraints.OfType<TableConstraint.Unique>().Single();

        unique
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("DocumentId", "EducationOrganizationId", "OrganizationName");
        unique.Name.Should().Be("UX_EducationOrganizationIdentity_NK");
    }

    /// <summary>
    /// It should use no action on update for document fk.
    /// </summary>
    [Test]
    public void It_should_use_no_action_on_update_for_document_fk()
    {
        var foreignKey = _abstractIdentityTable
            .TableModel.Constraints.OfType<TableConstraint.ForeignKey>()
            .Single();

        foreignKey.TargetTable.Schema.Value.Should().Be("dms");
        foreignKey.TargetTable.Name.Should().Be("Document");
        foreignKey.OnDelete.Should().Be(ReferentialAction.Cascade);
        foreignKey.OnUpdate.Should().Be(ReferentialAction.NoAction);
    }

    /// <summary>
    /// It should derive a union view with matching output column contract.
    /// </summary>
    [Test]
    public void It_should_derive_union_view_with_matching_output_column_contract()
    {
        _abstractUnionView
            .OutputColumnsInSelectOrder.Select(column => column.ColumnName.Value)
            .Should()
            .Equal("DocumentId", "EducationOrganizationId", "OrganizationName", "Discriminator");

        _abstractUnionView
            .OutputColumnsInSelectOrder.Select(column => column.ScalarType)
            .Should()
            .Equal(
                new RelationalScalarType(ScalarKind.Int64),
                new RelationalScalarType(ScalarKind.Int64),
                new RelationalScalarType(ScalarKind.String, 60),
                new RelationalScalarType(ScalarKind.String, 256)
            );
    }

    /// <summary>
    /// It should order union arms by member resource name and project discriminator literals.
    /// </summary>
    [Test]
    public void It_should_order_union_arms_by_member_resource_name_and_emit_discriminator_literals()
    {
        _abstractUnionView
            .UnionArmsInOrder.Select(arm => arm.ConcreteMemberResourceKey.Resource.ResourceName)
            .Should()
            .Equal("LocalEducationAgency", "School");

        var firstArmDiscriminator = _abstractUnionView
            .UnionArmsInOrder[0]
            .ProjectionExpressionsInSelectOrder[^1]
            .Should()
            .BeOfType<AbstractUnionViewProjectionExpression.StringLiteral>()
            .Which.Value;
        var secondArmDiscriminator = _abstractUnionView
            .UnionArmsInOrder[1]
            .ProjectionExpressionsInSelectOrder[^1]
            .Should()
            .BeOfType<AbstractUnionViewProjectionExpression.StringLiteral>()
            .Which.Value;

        firstArmDiscriminator.Should().Be("Ed-Fi:LocalEducationAgency");
        secondArmDiscriminator.Should().Be("Ed-Fi:School");
    }

    /// <summary>
    /// Build project schema.
    /// </summary>
    private static JsonObject BuildProjectSchema(bool mismatchMemberType)
    {
        return AbstractIdentityTableTestSchemaBuilder.BuildProjectSchema(mismatchMemberType);
    }
}

/// <summary>
/// Test fixture for abstract identity table derivation with widenable string max-length mismatches.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Widenable_String_Length_Mismatch
{
    private AbstractIdentityTableInfo _abstractIdentityTable = default!;
    private AbstractUnionViewInfo _abstractUnionView = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = AbstractIdentityTableTestSchemaBuilder.BuildProjectSchema(
            mismatchMemberType: false,
            localEducationAgencyOrganizationNameMaxLength: 255
        );
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new AbstractIdentityTableDerivationPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _abstractIdentityTable = result.AbstractIdentityTablesInNameOrder.Single();
        _abstractUnionView = result.AbstractUnionViewsInNameOrder.Single();
    }

    /// <summary>
    /// It should choose the widest string max length for the canonical abstract identity column type.
    /// </summary>
    [Test]
    public void It_should_choose_widest_string_max_length_for_canonical_identity_column_type()
    {
        var identityColumn = _abstractIdentityTable.TableModel.Columns.Single(column =>
            column.ColumnName.Value == "OrganizationName"
        );
        var outputColumn = _abstractUnionView.OutputColumnsInSelectOrder.Single(column =>
            column.ColumnName.Value == "OrganizationName"
        );

        identityColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.String, 255));
        outputColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.String, 255));
    }
}

/// <summary>
/// Test fixture for abstract identity table derivation with widenable integer-width mismatches.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Widenable_Integer_Widths
{
    private AbstractIdentityTableInfo _abstractIdentityTable = default!;
    private AbstractUnionViewInfo _abstractUnionView = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = AbstractIdentityTableTestSchemaBuilder.BuildProjectSchema(
            mismatchMemberType: false,
            schoolEducationOrganizationIdIsInt64: false,
            localEducationAgencyEducationOrganizationIdIsInt64: true
        );
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new AbstractIdentityTableDerivationPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _abstractIdentityTable = result.AbstractIdentityTablesInNameOrder.Single();
        _abstractUnionView = result.AbstractUnionViewsInNameOrder.Single();
    }

    /// <summary>
    /// It should widen mixed int32/int64 identities to int64 for canonical abstract identity columns.
    /// </summary>
    [Test]
    public void It_should_widen_mixed_int32_and_int64_identities_to_int64()
    {
        var identityColumn = _abstractIdentityTable.TableModel.Columns.Single(column =>
            column.ColumnName.Value == "EducationOrganizationId"
        );
        var outputColumn = _abstractUnionView.OutputColumnsInSelectOrder.Single(column =>
            column.ColumnName.Value == "EducationOrganizationId"
        );

        identityColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Int64));
        outputColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Int64));
    }
}

/// <summary>
/// Test fixture for abstract identity table with mismatched types.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Mismatched_Types
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = AbstractIdentityTableTestSchemaBuilder.BuildProjectSchema(
            mismatchMemberType: true
        );
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new AbstractIdentityTableDerivationPass(),
            }
        );

        try
        {
            builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    /// <summary>
    /// It should fail fast on mismatched member types.
    /// </summary>
    [Test]
    public void It_should_fail_fast_on_mismatched_member_types()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("inconsistent column types");
    }
}

/// <summary>
/// Test fixture for abstract identity table with duplicate identity paths.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Duplicate_Identity_Paths
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var abstractIdentityJsonPaths = new JsonArray
        {
            "$.educationOrganizationId",
            "$.educationOrganizationId",
        };
        var projectSchema = AbstractIdentityTableTestSchemaBuilder.BuildProjectSchema(
            mismatchMemberType: false,
            abstractIdentityJsonPaths: abstractIdentityJsonPaths
        );
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new AbstractIdentityTableDerivationPass(),
            }
        );

        try
        {
            builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    /// <summary>
    /// It should fail fast on duplicate identity paths.
    /// </summary>
    [Test]
    public void It_should_fail_fast_on_duplicate_identity_paths()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("duplicate");
        _exception.Message.Should().Contain("$.educationOrganizationId");
        _exception.Message.Should().Contain("Ed-Fi:EducationOrganization");
    }
}

/// <summary>
/// Test fixture for abstract identity table with a missing member identity field.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Missing_Member_Identity_Field
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = AbstractIdentityTableTestSchemaBuilder.BuildProjectSchema(
            mismatchMemberType: false,
            localEducationAgencyIdentityJsonPaths: new JsonArray { "$.educationOrganizationId" }
        );
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new AbstractIdentityTableDerivationPass(),
            }
        );

        try
        {
            builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    /// <summary>
    /// It should fail fast when a member cannot supply all abstract identity fields.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_member_cannot_supply_all_identity_fields()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("was not found in identityJsonPaths");
        _exception.Message.Should().Contain("$.organizationName");
        _exception.Message.Should().Contain("Ed-Fi:EducationOrganization");
    }
}

/// <summary>
/// Test fixture for superclass identity rename mapping on association subclasses.
/// </summary>
[TestFixture]
public class Given_Association_Subclass_With_Superclass_Identity_Rename_Mapping
{
    private AbstractUnionViewInfo _abstractUnionView = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = BuildProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new AbstractIdentityTableDerivationPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _abstractUnionView = result.AbstractUnionViewsInNameOrder.Single();
    }

    /// <summary>
    /// It should honor superclassIdentityJsonPath mapping even for association subclass types.
    /// </summary>
    [Test]
    public void It_should_honor_superclass_identity_mapping_for_association_subclasses()
    {
        _abstractUnionView
            .OutputColumnsInSelectOrder.Select(column => column.ColumnName.Value)
            .Should()
            .Equal("DocumentId", "EducationOrganizationId", "Discriminator");

        _abstractUnionView.UnionArmsInOrder.Should().HaveCount(1);

        var sourceColumn = _abstractUnionView
            .UnionArmsInOrder[0]
            .ProjectionExpressionsInSelectOrder[1]
            .Should()
            .BeOfType<AbstractUnionViewProjectionExpression.SourceColumn>()
            .Which.ColumnName.Value;

        sourceColumn.Should().Be("SchoolId");
    }

    /// <summary>
    /// Build project schema.
    /// </summary>
    private static JsonObject BuildProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "5.0.0",
            ["abstractResources"] = new JsonObject
            {
                ["EducationOrganization"] = new JsonObject
                {
                    ["resourceName"] = "EducationOrganization",
                    ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["schools"] = new JsonObject
                {
                    ["resourceName"] = "School",
                    ["isDescriptor"] = false,
                    ["isResourceExtension"] = false,
                    ["isSubclass"] = true,
                    ["subclassType"] = "association",
                    ["superclassProjectName"] = "Ed-Fi",
                    ["superclassResourceName"] = "EducationOrganization",
                    ["superclassIdentityJsonPath"] = "$.educationOrganizationId",
                    ["allowIdentityUpdates"] = false,
                    ["documentPathsMapping"] = new JsonObject
                    {
                        ["SchoolId"] = new JsonObject
                        {
                            ["isReference"] = false,
                            ["isDescriptor"] = false,
                            ["path"] = "$.schoolId",
                        },
                    },
                    ["arrayUniquenessConstraints"] = new JsonArray(),
                    ["decimalPropertyValidationInfos"] = new JsonArray(),
                    ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
                    ["jsonSchemaForInsert"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["schoolId"] = new JsonObject { ["type"] = "integer", ["format"] = "int64" },
                        },
                        ["required"] = new JsonArray { "schoolId" },
                    },
                },
            },
        };
    }
}

/// <summary>
/// Test fixture for abstract identity table derivation with extension project members.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Extension_Project_Member
{
    private AbstractUnionViewInfo _abstractUnionView = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            BuildCoreProjectSchema(),
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            BuildExtensionProjectSchema(),
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(
            new[] { coreProject, extensionProject }
        );
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new AbstractIdentityTableDerivationPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _abstractUnionView = result.AbstractUnionViewsInNameOrder.Single();
    }

    /// <summary>
    /// It should include extension project members as union arms with project-qualified discriminator values.
    /// </summary>
    [Test]
    public void It_should_include_extension_project_members_as_union_arms_with_project_qualified_discriminator()
    {
        _abstractUnionView
            .UnionArmsInOrder.Select(arm =>
                (
                    arm.ConcreteMemberResourceKey.Resource.ProjectName,
                    arm.ConcreteMemberResourceKey.Resource.ResourceName
                )
            )
            .Should()
            .Equal(("Sample", "CharterSchool"), ("Ed-Fi", "School"));

        var discriminatorLiterals = _abstractUnionView
            .UnionArmsInOrder.Select(arm =>
                arm.ProjectionExpressionsInSelectOrder[^1]
                    .Should()
                    .BeOfType<AbstractUnionViewProjectionExpression.StringLiteral>()
                    .Which.Value
            )
            .ToArray();

        discriminatorLiterals.Should().Equal("Sample:CharterSchool", "Ed-Fi:School");
    }

    /// <summary>
    /// Build core project schema.
    /// </summary>
    private static JsonObject BuildCoreProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "5.0.0",
            ["abstractResources"] = new JsonObject
            {
                ["EducationOrganization"] = new JsonObject
                {
                    ["resourceName"] = "EducationOrganization",
                    ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["schools"] = BuildConcreteSubclassResourceSchema(
                    resourceName: "School",
                    superclassProjectName: "Ed-Fi"
                ),
            },
        };
    }

    /// <summary>
    /// Build extension project schema.
    /// </summary>
    private static JsonObject BuildExtensionProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Sample",
            ["projectEndpointName"] = "sample",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["charterSchools"] = BuildConcreteSubclassResourceSchema(
                    resourceName: "CharterSchool",
                    superclassProjectName: "Ed-Fi"
                ),
            },
        };
    }

    /// <summary>
    /// Build concrete subclass resource schema.
    /// </summary>
    private static JsonObject BuildConcreteSubclassResourceSchema(
        string resourceName,
        string superclassProjectName
    )
    {
        return new JsonObject
        {
            ["resourceName"] = resourceName,
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = true,
            ["subclassType"] = "association",
            ["superclassProjectName"] = superclassProjectName,
            ["superclassResourceName"] = "EducationOrganization",
            ["superclassIdentityJsonPath"] = null,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["educationOrganizationId"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["format"] = "int64",
                    },
                },
                ["required"] = new JsonArray { "educationOrganizationId" },
            },
        };
    }
}

/// <summary>
/// Test fixture for duplicate concrete member names across projects.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Duplicate_Member_Resource_Names_Across_Projects
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            BuildCoreProjectSchema(),
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            BuildExtensionProjectSchema(),
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(
            new[] { coreProject, extensionProject }
        );
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new AbstractIdentityTableDerivationPass(),
            }
        );

        try
        {
            builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    /// <summary>
    /// It should fail fast when two projects contribute members with the same resource name.
    /// </summary>
    [Test]
    public void It_should_fail_fast_on_duplicate_member_resource_names_across_projects()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("duplicate member ResourceName");
        _exception.Message.Should().Contain("'School'");
    }

    /// <summary>
    /// Build core project schema.
    /// </summary>
    private static JsonObject BuildCoreProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "5.0.0",
            ["abstractResources"] = new JsonObject
            {
                ["EducationOrganization"] = new JsonObject
                {
                    ["resourceName"] = "EducationOrganization",
                    ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
                },
            },
            ["resourceSchemas"] = new JsonObject { ["schools"] = BuildConcreteSchoolSchema("Ed-Fi") },
        };
    }

    /// <summary>
    /// Build extension project schema.
    /// </summary>
    private static JsonObject BuildExtensionProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Sample",
            ["projectEndpointName"] = "sample",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["schools"] = BuildConcreteSchoolSchema("Ed-Fi") },
        };
    }

    /// <summary>
    /// Build concrete school schema.
    /// </summary>
    private static JsonObject BuildConcreteSchoolSchema(string superclassProjectName)
    {
        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = true,
            ["subclassType"] = "association",
            ["superclassProjectName"] = superclassProjectName,
            ["superclassResourceName"] = "EducationOrganization",
            ["superclassIdentityJsonPath"] = null,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["educationOrganizationId"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["format"] = "int64",
                    },
                },
                ["required"] = new JsonArray { "educationOrganizationId" },
            },
        };
    }
}

/// <summary>
/// Test fixture for overly long discriminator values.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Overlength_Discriminator
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var longProjectName = new string('P', 200);
        var longMemberResourceName = new string('R', 70);
        var projectSchema = BuildProjectSchema(longProjectName, longMemberResourceName);
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new AbstractIdentityTableDerivationPass(),
            }
        );

        try
        {
            builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    /// <summary>
    /// It should fail fast when discriminator value length exceeds 256.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_discriminator_value_exceeds_max_length()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("exceeds max length 256");
    }

    /// <summary>
    /// Build project schema.
    /// </summary>
    private static JsonObject BuildProjectSchema(string projectName, string memberResourceName)
    {
        return new JsonObject
        {
            ["projectName"] = projectName,
            ["projectEndpointName"] = "long-project",
            ["projectVersion"] = "1.0.0",
            ["abstractResources"] = new JsonObject
            {
                ["AbstractBase"] = new JsonObject
                {
                    ["resourceName"] = "AbstractBase",
                    ["identityJsonPaths"] = new JsonArray { "$.id" },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["members"] = new JsonObject
                {
                    ["resourceName"] = memberResourceName,
                    ["isDescriptor"] = false,
                    ["isResourceExtension"] = false,
                    ["isSubclass"] = true,
                    ["subclassType"] = "association",
                    ["superclassProjectName"] = projectName,
                    ["superclassResourceName"] = "AbstractBase",
                    ["superclassIdentityJsonPath"] = null,
                    ["allowIdentityUpdates"] = false,
                    ["documentPathsMapping"] = new JsonObject
                    {
                        ["Id"] = new JsonObject
                        {
                            ["isReference"] = false,
                            ["isDescriptor"] = false,
                            ["path"] = "$.id",
                        },
                    },
                    ["arrayUniquenessConstraints"] = new JsonArray(),
                    ["decimalPropertyValidationInfos"] = new JsonArray(),
                    ["identityJsonPaths"] = new JsonArray { "$.id" },
                    ["jsonSchemaForInsert"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["id"] = new JsonObject { ["type"] = "integer", ["format"] = "int64" },
                        },
                        ["required"] = new JsonArray { "id" },
                    },
                },
            },
        };
    }
}

/// <summary>
/// Test fixture for subclass resources that reference a non-abstract superclass.
/// </summary>
[TestFixture]
public class Given_Subclass_With_Non_Abstract_Superclass
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = BuildProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new AbstractIdentityTableDerivationPass(),
            }
        );

        try
        {
            builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    /// <summary>
    /// It should fail fast when a subclass declares a non-abstract superclass.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_subclass_superclass_is_not_abstract()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Subclass-of-subclass is not permitted");
    }

    /// <summary>
    /// Build project schema.
    /// </summary>
    private static JsonObject BuildProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "5.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["schools"] = new JsonObject
                {
                    ["resourceName"] = "School",
                    ["isDescriptor"] = false,
                    ["isResourceExtension"] = false,
                    ["isSubclass"] = false,
                    ["allowIdentityUpdates"] = false,
                    ["documentPathsMapping"] = new JsonObject
                    {
                        ["SchoolId"] = new JsonObject
                        {
                            ["isReference"] = false,
                            ["isDescriptor"] = false,
                            ["path"] = "$.schoolId",
                        },
                    },
                    ["arrayUniquenessConstraints"] = new JsonArray(),
                    ["decimalPropertyValidationInfos"] = new JsonArray(),
                    ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
                    ["jsonSchemaForInsert"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["schoolId"] = new JsonObject { ["type"] = "integer", ["format"] = "int64" },
                        },
                        ["required"] = new JsonArray { "schoolId" },
                    },
                },
                ["campuses"] = new JsonObject
                {
                    ["resourceName"] = "Campus",
                    ["isDescriptor"] = false,
                    ["isResourceExtension"] = false,
                    ["isSubclass"] = true,
                    ["subclassType"] = "association",
                    ["superclassProjectName"] = "Ed-Fi",
                    ["superclassResourceName"] = "School",
                    ["superclassIdentityJsonPath"] = null,
                    ["allowIdentityUpdates"] = false,
                    ["documentPathsMapping"] = new JsonObject
                    {
                        ["SchoolId"] = new JsonObject
                        {
                            ["isReference"] = false,
                            ["isDescriptor"] = false,
                            ["path"] = "$.schoolId",
                        },
                    },
                    ["arrayUniquenessConstraints"] = new JsonArray(),
                    ["decimalPropertyValidationInfos"] = new JsonArray(),
                    ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
                    ["jsonSchemaForInsert"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["schoolId"] = new JsonObject { ["type"] = "integer", ["format"] = "int64" },
                        },
                        ["required"] = new JsonArray { "schoolId" },
                    },
                },
            },
        };
    }
}

/// <summary>
/// Test fixture for nullable member source columns used for abstract identity paths.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Nullable_Member_Identity_Source_Column
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = AbstractIdentityTableTestSchemaBuilder.BuildProjectSchema(
            mismatchMemberType: false
        );
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new ForceRootColumnNullablePass(
                    new QualifiedResourceName("Ed-Fi", "School"),
                    "$.organizationName"
                ),
                new AbstractIdentityTableDerivationPass(),
            }
        );

        try
        {
            builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    /// <summary>
    /// It should fail fast when a mapped member source column is nullable.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_mapped_member_source_column_is_nullable()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("resolved to nullable source column");
        _exception.Message.Should().Contain("$.organizationName");
    }
}

/// <summary>
/// Mutates a single root column to nullable for targeted fail-fast validation tests.
/// </summary>
internal sealed class ForceRootColumnNullablePass(QualifiedResourceName resource, string sourceJsonPath)
    : IRelationalModelSetPass
{
    /// <inheritdoc />
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var index = context.ConcreteResourcesInNameOrder.FindIndex(model =>
            model.ResourceKey.Resource == resource
        );

        if (index < 0)
        {
            throw new InvalidOperationException(
                $"Concrete resource '{resource.ProjectName}:{resource.ResourceName}' was not found."
            );
        }

        var concrete = context.ConcreteResourcesInNameOrder[index];
        var root = concrete.RelationalModel.Root;
        var updatedColumns = root
            .Columns.Select(column =>
                column.SourceJsonPath is { } path
                && string.Equals(path.Canonical, sourceJsonPath, StringComparison.Ordinal)
                    ? column with
                    {
                        IsNullable = true,
                    }
                    : column
            )
            .ToArray();

        var updatedRoot = root with { Columns = updatedColumns };
        var updatedTables = concrete
            .RelationalModel.TablesInDependencyOrder.Select(table =>
                table.Table == root.Table ? updatedRoot : table
            )
            .ToArray();
        var updatedModel = concrete.RelationalModel with
        {
            Root = updatedRoot,
            TablesInDependencyOrder = updatedTables,
        };

        context.ConcreteResourcesInNameOrder[index] = concrete with { RelationalModel = updatedModel };
    }
}

/// <summary>
/// Test type abstract identity table test schema builder.
/// </summary>
internal static class AbstractIdentityTableTestSchemaBuilder
{
    /// <summary>
    /// Build project schema.
    /// </summary>
    internal static JsonObject BuildProjectSchema(
        bool mismatchMemberType,
        JsonArray? abstractIdentityJsonPaths = null,
        JsonArray? localEducationAgencyIdentityJsonPaths = null,
        int schoolOrganizationNameMaxLength = 60,
        int localEducationAgencyOrganizationNameMaxLength = 60,
        bool schoolEducationOrganizationIdIsInt64 = true,
        bool localEducationAgencyEducationOrganizationIdIsInt64 = true
    )
    {
        var identityJsonPaths =
            abstractIdentityJsonPaths ?? new JsonArray { "$.educationOrganizationId", "$.organizationName" };
        var abstractResources = new JsonObject
        {
            ["EducationOrganization"] = new JsonObject
            {
                ["resourceName"] = "EducationOrganization",
                ["identityJsonPaths"] = identityJsonPaths,
            },
        };

        var resourceSchemas = new JsonObject
        {
            ["schools"] = BuildConcreteResourceSchema(
                "School",
                organizationNameIsString: true,
                organizationNameMaxLength: schoolOrganizationNameMaxLength,
                educationOrganizationIdIsInt64: schoolEducationOrganizationIdIsInt64
            ),
            ["localEducationAgencies"] = BuildConcreteResourceSchema(
                "LocalEducationAgency",
                organizationNameIsString: !mismatchMemberType,
                organizationNameMaxLength: mismatchMemberType
                    ? 40
                    : localEducationAgencyOrganizationNameMaxLength,
                identityJsonPathsOverride: localEducationAgencyIdentityJsonPaths,
                educationOrganizationIdIsInt64: localEducationAgencyEducationOrganizationIdIsInt64
            ),
        };

        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "5.0.0",
            ["resourceSchemas"] = resourceSchemas,
            ["abstractResources"] = abstractResources,
        };
    }

    /// <summary>
    /// Build concrete resource schema.
    /// </summary>
    private static JsonObject BuildConcreteResourceSchema(
        string resourceName,
        bool organizationNameIsString,
        int organizationNameMaxLength,
        JsonArray? identityJsonPathsOverride = null,
        bool educationOrganizationIdIsInt64 = true
    )
    {
        var concreteIdentityJsonPaths = identityJsonPathsOverride is null
            ? new JsonArray { "$.educationOrganizationId", "$.organizationName" }
            : (JsonArray)identityJsonPathsOverride.DeepClone();

        var properties = new JsonObject
        {
            ["educationOrganizationId"] = educationOrganizationIdIsInt64
                ? new JsonObject { ["type"] = "integer", ["format"] = "int64" }
                : new JsonObject { ["type"] = "integer" },
            ["organizationName"] = organizationNameIsString
                ? new JsonObject { ["type"] = "string", ["maxLength"] = organizationNameMaxLength }
                : new JsonObject { ["type"] = "integer", ["format"] = "int64" },
        };

        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray { "educationOrganizationId", "organizationName" },
        };

        return new JsonObject
        {
            ["resourceName"] = resourceName,
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = true,
            ["subclassType"] = "association",
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "EducationOrganization",
            ["superclassIdentityJsonPath"] = null,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
                ["OrganizationName"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["path"] = "$.organizationName",
                },
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = concreteIdentityJsonPaths,
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }
}
