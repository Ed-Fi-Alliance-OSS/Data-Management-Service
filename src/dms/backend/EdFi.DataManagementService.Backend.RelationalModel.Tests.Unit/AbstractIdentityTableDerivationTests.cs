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
    private DerivedRelationalModelSet _modelSet = default!;

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
                new AbstractIdentityTableAndUnionViewDerivationPass(),
            }
        );

        _modelSet = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _abstractIdentityTable = _modelSet.AbstractIdentityTablesInNameOrder.Single(table =>
            table.AbstractResourceKey.Resource.ResourceName == "EducationOrganization"
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
    /// It should include natural and reference-key unique constraints.
    /// </summary>
    [Test]
    public void It_should_include_natural_and_reference_key_unique_constraints()
    {
        var uniqueByName = _abstractIdentityTable
            .TableModel.Constraints.OfType<TableConstraint.Unique>()
            .ToDictionary(constraint => constraint.Name, StringComparer.Ordinal);

        uniqueByName
            .Keys.Should()
            .Equal("UX_EducationOrganizationIdentity_NK", "UX_EducationOrganizationIdentity_RefKey");
        uniqueByName["UX_EducationOrganizationIdentity_NK"]
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("EducationOrganizationId", "OrganizationName");
        uniqueByName["UX_EducationOrganizationIdentity_RefKey"]
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("EducationOrganizationId", "OrganizationName", "DocumentId");
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
    /// It should carry ApiSchema superclass metadata on subclass concrete resources.
    /// </summary>
    [Test]
    public void It_should_carry_superclass_metadata_on_subclass_concrete_resources()
    {
        var subclassResources = _modelSet
            .ConcreteResourcesInNameOrder.Where(resource => resource.SuperclassResource is not null)
            .ToArray();

        subclassResources.Should().NotBeEmpty();
        subclassResources
            .Select(resource => resource.SuperclassResource!.Value)
            .Should()
            .AllBeEquivalentTo(new QualifiedResourceName("Ed-Fi", "EducationOrganization"));
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
                new AbstractIdentityTableAndUnionViewDerivationPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _abstractIdentityTable = result.AbstractIdentityTablesInNameOrder.Single();
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

        identityColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.String, 255));
    }
}

/// <summary>
/// Test fixture for abstract identity table derivation with widenable integer-width mismatches.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Widenable_Integer_Widths
{
    private AbstractIdentityTableInfo _abstractIdentityTable = default!;

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
                new AbstractIdentityTableAndUnionViewDerivationPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _abstractIdentityTable = result.AbstractIdentityTablesInNameOrder.Single();
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

        identityColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Int64));
    }
}

/// <summary>
/// Test fixture for abstract identity table derivation with widenable decimal precision/scale mismatches.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Widenable_Decimal_Precision
{
    private AbstractIdentityTableInfo _abstractIdentityTable = default!;

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
                new AbstractIdentityTableAndUnionViewDerivationPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _abstractIdentityTable = result.AbstractIdentityTablesInNameOrder.Single();
    }

    /// <summary>
    /// It should widen decimal precision from both integer digits and scale.
    /// </summary>
    [Test]
    public void It_should_widen_decimal_precision_from_integer_digits_and_scale()
    {
        var identityColumn = _abstractIdentityTable.TableModel.Columns.Single(column =>
            column.ColumnName.Value == "Amount"
        );

        identityColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Decimal, Decimal: (12, 4)));
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
                    ["identityJsonPaths"] = new JsonArray { "$.amount" },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["schools"] = BuildConcreteResourceSchema("School", totalDigits: 10, decimalPlaces: 2),
                ["localEducationAgencies"] = BuildConcreteResourceSchema(
                    "LocalEducationAgency",
                    totalDigits: 5,
                    decimalPlaces: 4
                ),
            },
        };
    }

    /// <summary>
    /// Build concrete resource schema.
    /// </summary>
    private static JsonObject BuildConcreteResourceSchema(
        string resourceName,
        short totalDigits,
        short decimalPlaces
    )
    {
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
                ["Amount"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["path"] = "$.amount",
                },
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray
            {
                new JsonObject
                {
                    ["path"] = "$.amount",
                    ["totalDigits"] = totalDigits,
                    ["decimalPlaces"] = decimalPlaces,
                },
            },
            ["identityJsonPaths"] = new JsonArray { "$.amount" },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["amount"] = new JsonObject { ["type"] = "number" } },
                ["required"] = new JsonArray { "amount" },
            },
        };
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
                new AbstractIdentityTableAndUnionViewDerivationPass(),
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
                new AbstractIdentityTableAndUnionViewDerivationPass(),
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
                new AbstractIdentityTableAndUnionViewDerivationPass(),
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
/// Test fixture for abstract resources with zero concrete members.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_No_Concrete_Members
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
        var resourceSchemas = (JsonObject)projectSchema["resourceSchemas"]!;

        foreach (var entry in resourceSchemas)
        {
            if (entry.Value is not JsonObject resourceSchema)
            {
                continue;
            }

            resourceSchema["isSubclass"] = false;
        }

        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new AbstractIdentityTableAndUnionViewDerivationPass(),
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
    /// It should fail fast when an abstract resource has no concrete members.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_abstract_resource_has_zero_concrete_members()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("has no concrete members");
        _exception.Message.Should().Contain("Ed-Fi:EducationOrganization");
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
                new AbstractIdentityTableAndUnionViewDerivationPass(),
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
/// Test fixture for subclass resources missing required superclass metadata.
/// </summary>
[TestFixture]
public class Given_Subclass_With_Missing_Superclass_Project_Name
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
                new AbstractIdentityTableAndUnionViewDerivationPass(),
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
    /// It should include subclass resource identity in the error for missing superclass metadata.
    /// </summary>
    [Test]
    public void It_should_include_subclass_resource_identity_when_superclass_project_name_is_missing()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("superclassProjectName");
        _exception.Message.Should().Contain("Ed-Fi:Campus");
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
                    ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["campuses"] = new JsonObject
                {
                    ["resourceName"] = "Campus",
                    ["isDescriptor"] = false,
                    ["isResourceExtension"] = false,
                    ["isSubclass"] = true,
                    ["subclassType"] = "association",
                    ["superclassResourceName"] = "EducationOrganization",
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
                new AbstractIdentityTableAndUnionViewDerivationPass(),
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
/// Test fixture for duplicate root-column SourceJsonPath mappings on a concrete member.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Duplicate_Root_Source_JsonPath_Mappings
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
                new ForceRootColumnSourcePathPass(
                    new QualifiedResourceName("Ed-Fi", "School"),
                    columnName: "OrganizationName",
                    sourceJsonPath: "$.educationOrganizationId"
                ),
                new AbstractIdentityTableAndUnionViewDerivationPass(),
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
    /// It should fail fast when two root columns map to the same SourceJsonPath.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_root_columns_share_source_json_path_mapping()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("duplicate root-column SourceJsonPath mapping");
        _exception.Message.Should().Contain("$.educationOrganizationId");
        _exception.Message.Should().Contain("EducationOrganizationId");
        _exception.Message.Should().Contain("OrganizationName");
    }
}

/// <summary>
/// Test fixture for key-unified duplicate root-column SourceJsonPath mappings on a concrete member.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Key_Unified_Duplicate_Root_Source_JsonPath_Mappings
{
    private DerivedRelationalModelSet _result = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _result = BuildDerivedSet(reverseDuplicateReferenceBindings: false);
    }

    private static DerivedRelationalModelSet BuildDerivedSet(bool reverseDuplicateReferenceBindings)
    {
        var projectSchema = AbstractIdentityTableTestSchemaBuilder.BuildGroupedReferenceIdentityProjectSchema(
            reverseDuplicateReferenceBindings
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
                new DescriptorResourceMappingPass(),
                new ExtensionTableDerivationPass(),
                new ReferenceBindingPass(),
                new KeyUnificationPass(),
                new AbstractIdentityTableAndUnionViewDerivationPass(),
            }
        );

        return builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    /// <summary>
    /// It should resolve grouped duplicate root-column mappings through their canonical stored column.
    /// </summary>
    [Test]
    public void It_should_resolve_key_unified_duplicate_root_columns_to_one_stored_column()
    {
        var abstractTable = _result.AbstractIdentityTablesInNameOrder.Single(table =>
            table.AbstractResourceKey.Resource.ResourceName == "EnrollmentCarrier"
        );
        var unionView = _result.AbstractUnionViewsInNameOrder.Single(view =>
            view.AbstractResourceKey.Resource.ResourceName == "EnrollmentCarrier"
        );

        abstractTable
            .TableModel.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Contain("School_SchoolId");

        var identityProjection = unionView
            .UnionArmsInOrder.Single()
            .ProjectionExpressionsInSelectOrder[1]
            .Should()
            .BeOfType<AbstractUnionViewProjectionExpression.SourceColumn>()
            .Subject;

        identityProjection.ColumnName.Value.Should().Be("SchoolId_Unified");
    }

    /// <summary>
    /// It should not let duplicate reference identity binding order change the abstract column contract.
    /// </summary>
    [Test]
    public void It_should_resolve_duplicate_reference_bindings_independent_of_binding_order()
    {
        var reversedResult = BuildDerivedSet(reverseDuplicateReferenceBindings: true);

        ResolveAbstractIdentityColumnNames(reversedResult)
            .Should()
            .Equal(ResolveAbstractIdentityColumnNames(_result))
            .And.Contain("School_SchoolId")
            .And.NotContain("School_LocalEducationAgencyId");

        ResolveUnionViewOutputColumnNames(reversedResult)
            .Should()
            .Equal(ResolveUnionViewOutputColumnNames(_result))
            .And.Contain("School_SchoolId")
            .And.NotContain("School_LocalEducationAgencyId");
    }

    private static string[] ResolveAbstractIdentityColumnNames(DerivedRelationalModelSet modelSet)
    {
        return modelSet
            .AbstractIdentityTablesInNameOrder.Single(table =>
                table.AbstractResourceKey.Resource.ResourceName == "EnrollmentCarrier"
            )
            .TableModel.Columns.Select(column => column.ColumnName.Value)
            .ToArray();
    }

    private static string[] ResolveUnionViewOutputColumnNames(DerivedRelationalModelSet modelSet)
    {
        return modelSet
            .AbstractUnionViewsInNameOrder.Single(view =>
                view.AbstractResourceKey.Resource.ResourceName == "EnrollmentCarrier"
            )
            .OutputColumnsInSelectOrder.Select(column => column.ColumnName.Value)
            .ToArray();
    }
}

/// <summary>
/// Test fixture for conflicting root-column SourceJsonPath kinds on a concrete member.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Conflicting_Root_Source_JsonPath_Kinds
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
                new ForceRootColumnSourcePathPass(
                    new QualifiedResourceName("Ed-Fi", "School"),
                    columnName: "OrganizationName",
                    sourceJsonPath: "$.educationOrganizationId"
                ),
                new ForceRootColumnKindPass(
                    new QualifiedResourceName("Ed-Fi", "School"),
                    columnName: "OrganizationName",
                    kind: ColumnKind.DocumentFk,
                    scalarType: new RelationalScalarType(ScalarKind.Int64),
                    targetResource: new QualifiedResourceName("Ed-Fi", "School")
                ),
                new AbstractIdentityTableAndUnionViewDerivationPass(),
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
    /// It should fail fast when one duplicate root SourceJsonPath mixes column kinds.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_root_columns_share_a_source_json_path_with_conflicting_kinds()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("multiple root-column kinds");
        _exception.Message.Should().Contain("$.educationOrganizationId");
        _exception.Message.Should().Contain("EducationOrganizationId (Scalar)");
        _exception.Message.Should().Contain("OrganizationName (DocumentFk)");
    }
}

/// <summary>
/// Test fixture for missing root-column SourceJsonPath mappings on a concrete member.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Missing_Root_Source_JsonPath_Mapping
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
                new ForceRootColumnSourcePathNullPass(
                    new QualifiedResourceName("Ed-Fi", "School"),
                    "$.organizationName"
                ),
                new AbstractIdentityTableAndUnionViewDerivationPass(),
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
    /// It should fail fast when identity paths do not resolve to root-column SourceJsonPath mappings.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_identity_path_mapping_is_missing_from_root_columns()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("did not resolve to a root-column SourceJsonPath mapping");
        _exception.Message.Should().Contain("$.organizationName");
        _exception.Message.Should().Contain("Ed-Fi:School");
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
/// Rewrites a root column SourceJsonPath for targeted duplicate-mapping fail-fast tests.
/// </summary>
internal sealed class ForceRootColumnSourcePathPass(
    QualifiedResourceName resource,
    string columnName,
    string sourceJsonPath
) : IRelationalModelSetPass
{
    /// <inheritdoc />
    public void Execute(RelationalModelSetBuilderContext context)
    {
        var index = context.ConcreteResourcesInNameOrder.FindIndex(model =>
            model.ResourceKey.Resource == resource
        );

        if (index < 0)
        {
            throw new InvalidOperationException(
                $"Concrete resource '{resource.ProjectName}:{resource.ResourceName}' was not found."
            );
        }

        var canonicalPath = JsonPathExpressionCompiler.Compile(sourceJsonPath);
        var concrete = context.ConcreteResourcesInNameOrder[index];
        var root = concrete.RelationalModel.Root;
        var found = false;
        DbColumnModel[] updatedColumns = new DbColumnModel[root.Columns.Count];

        for (var i = 0; i < root.Columns.Count; i++)
        {
            var column = root.Columns[i];

            if (!string.Equals(column.ColumnName.Value, columnName, StringComparison.Ordinal))
            {
                updatedColumns[i] = column;
                continue;
            }

            found = true;
            updatedColumns[i] = column with { SourceJsonPath = canonicalPath };
        }

        if (!found)
        {
            throw new InvalidOperationException(
                $"Root column '{columnName}' was not found on resource "
                    + $"'{resource.ProjectName}:{resource.ResourceName}'."
            );
        }

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
/// Rewrites a root column kind for targeted conflicting-kind validation tests.
/// </summary>
internal sealed class ForceRootColumnKindPass(
    QualifiedResourceName resource,
    string columnName,
    ColumnKind kind,
    RelationalScalarType scalarType,
    QualifiedResourceName? targetResource
) : IRelationalModelSetPass
{
    /// <inheritdoc />
    public void Execute(RelationalModelSetBuilderContext context)
    {
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
        var found = false;
        DbColumnModel[] updatedColumns = new DbColumnModel[root.Columns.Count];

        for (var i = 0; i < root.Columns.Count; i++)
        {
            var column = root.Columns[i];

            if (!string.Equals(column.ColumnName.Value, columnName, StringComparison.Ordinal))
            {
                updatedColumns[i] = column;
                continue;
            }

            found = true;
            updatedColumns[i] = column with
            {
                Kind = kind,
                ScalarType = scalarType,
                TargetResource = targetResource,
            };
        }

        if (!found)
        {
            throw new InvalidOperationException(
                $"Root column '{columnName}' was not found on resource "
                    + $"'{resource.ProjectName}:{resource.ResourceName}'."
            );
        }

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
/// Removes SourceJsonPath from a root column for targeted missing-resolution fail-fast tests.
/// </summary>
internal sealed class ForceRootColumnSourcePathNullPass(QualifiedResourceName resource, string sourceJsonPath)
    : IRelationalModelSetPass
{
    /// <inheritdoc />
    public void Execute(RelationalModelSetBuilderContext context)
    {
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
        var matched = false;
        DbColumnModel[] updatedColumns = new DbColumnModel[root.Columns.Count];

        for (var i = 0; i < root.Columns.Count; i++)
        {
            var column = root.Columns[i];

            if (
                column.SourceJsonPath is null
                || !string.Equals(
                    column.SourceJsonPath.Value.Canonical,
                    sourceJsonPath,
                    StringComparison.Ordinal
                )
            )
            {
                updatedColumns[i] = column;
                continue;
            }

            matched = true;
            updatedColumns[i] = column with { SourceJsonPath = null };
        }

        if (!matched)
        {
            throw new InvalidOperationException(
                $"Root SourceJsonPath '{sourceJsonPath}' was not found on resource "
                    + $"'{resource.ProjectName}:{resource.ResourceName}'."
            );
        }

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
/// Test fixture for subclass members with superclassIdentityJsonPath set but multiple identity paths.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Superclass_Identity_Path_And_Multiple_Member_Identities
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        // Build a schema where a concrete member has superclassIdentityJsonPath set (non-null)
        // but also has multiple identity paths — violating the single-identity invariant.
        var projectSchema = new JsonObject
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
                    // Non-null superclassIdentityJsonPath triggers the guard
                    ["superclassIdentityJsonPath"] = "$.schoolId",
                    ["allowIdentityUpdates"] = false,
                    ["documentPathsMapping"] = new JsonObject
                    {
                        ["SchoolId"] = new JsonObject
                        {
                            ["isReference"] = false,
                            ["isDescriptor"] = false,
                            ["path"] = "$.schoolId",
                        },
                        ["SchoolName"] = new JsonObject
                        {
                            ["isReference"] = false,
                            ["isDescriptor"] = false,
                            ["path"] = "$.schoolName",
                        },
                    },
                    ["arrayUniquenessConstraints"] = new JsonArray(),
                    ["decimalPropertyValidationInfos"] = new JsonArray(),
                    // Multiple identity paths with superclassIdentityJsonPath set
                    ["identityJsonPaths"] = new JsonArray { "$.schoolId", "$.schoolName" },
                    ["jsonSchemaForInsert"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["schoolId"] = new JsonObject { ["type"] = "integer", ["format"] = "int64" },
                            ["schoolName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 75 },
                        },
                        ["required"] = new JsonArray { "schoolId", "schoolName" },
                    },
                },
            },
        };

        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new AbstractIdentityTableAndUnionViewDerivationPass(),
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
    /// It should fail fast when a member with superclassIdentityJsonPath has multiple identity paths.
    /// The validation layer rejects this before the derivation pass runs.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_superclass_identity_path_member_has_multiple_identities()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!
            .Message.Should()
            .Contain("superclassIdentityJsonPath but must declare exactly one identityJsonPaths entry");
        _exception.Message.Should().Contain("Ed-Fi:School");
    }
}

/// <summary>
/// Test fixture for abstract identity column naming: composite reference scalars and reference-backed
/// descriptor.
/// The abstract resource ProgramCarrier has three identity paths under $.programReference:
///   - $.programReference.educationOrganizationId → Program_EducationOrganizationId
///   - $.programReference.programName             → Program_ProgramName
///   - $.programReference.programTypeDescriptor   → Program_ProgramTypeDescriptor_DescriptorId
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Column_Naming_With_Composite_Reference
{
    private AbstractIdentityTableInfo _abstractTable = default!;

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
                new DescriptorResourceMappingPass(),
                new ExtensionTableDerivationPass(),
                new ReferenceBindingPass(),
                new KeyUnificationPass(),
                new AbstractIdentityTableAndUnionViewDerivationPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _abstractTable = result.AbstractIdentityTablesInNameOrder.Single(table =>
            table.AbstractResourceKey.Resource.ResourceName == "ProgramCarrier"
        );
    }

    /// <summary>
    /// Composite reference scalar: $.programReference.educationOrganizationId → Program_EducationOrganizationId.
    /// </summary>
    [Test]
    public void It_should_name_composite_reference_scalar_identity_column_as_Ref_Field()
    {
        _abstractTable
            .TableModel.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Contain("Program_EducationOrganizationId")
            .And.NotContain("ProgramReferenceEducationOrganizationId");
    }

    /// <summary>
    /// Second composite reference scalar: $.programReference.programName → Program_ProgramName. Covers the
    /// ticket's Program_ProgramName example — a reference-backed scalar field other than the first.
    /// </summary>
    [Test]
    public void It_should_name_additional_composite_reference_scalar_identity_column_as_Ref_Field()
    {
        _abstractTable
            .TableModel.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Contain("Program_ProgramName")
            .And.NotContain("ProgramReferenceProgramName");
    }

    /// <summary>
    /// Reference-backed descriptor: $.programReference.programTypeDescriptor → Program_ProgramTypeDescriptor_DescriptorId.
    /// </summary>
    [Test]
    public void It_should_name_reference_backed_descriptor_identity_column_as_Ref_Field_DescriptorId()
    {
        _abstractTable
            .TableModel.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Contain("Program_ProgramTypeDescriptor_DescriptorId")
            .And.NotContain("ProgramReferenceProgramTypeDescriptor");
    }

    /// <summary>
    /// Pins the table-shape contract that
    /// <c>RelationalWriteConstraintResolver.ResolveAbstractIdentityUniqueConstraint</c> depends on, exercised
    /// here for a reference-backed abstract resource: the primary key is exactly the surrogate
    /// <c>DocumentId</c>, the <c>_NK</c> natural-key unique excludes <c>DocumentId</c>, and the <c>_RefKey</c>
    /// unique is the natural key plus <c>DocumentId</c>. The resolver classifies a unique violation as a 409
    /// identity conflict precisely when the violated constraint does not contain a primary-key column; if the
    /// derivation ever changes this shape (PK structure, constraint membership, or an extra unique), that
    /// heuristic would silently misclassify and this test fails first.
    /// </summary>
    [Test]
    public void It_keys_the_identity_table_on_document_id_with_natural_and_reference_key_uniques()
    {
        var table = _abstractTable.TableModel;

        table.Key.Columns.Select(column => column.ColumnName.Value).Should().Equal("DocumentId");

        var uniques = table.Constraints.OfType<TableConstraint.Unique>().ToArray();
        var naturalKey = uniques.Single(constraint =>
            constraint.Name.EndsWith("_NK", StringComparison.Ordinal)
        );
        var referenceKey = uniques.Single(constraint =>
            constraint.Name.EndsWith("_RefKey", StringComparison.Ordinal)
        );

        naturalKey.Columns.Select(column => column.Value).Should().NotContain("DocumentId");
        referenceKey
            .Columns.Select(column => column.Value)
            .Should()
            .Equal(naturalKey.Columns.Select(column => column.Value).Append("DocumentId"));
    }

    private static JsonObject BuildProjectSchema() => ProgramCarrierTestSchema.BuildProjectSchema();
}

/// <summary>
/// Test fixture for abstract identity column naming: relational.nameOverrides is not propagated, and the
/// reference base name comes from the schema MappingKey ("SchoolBase"), not the JSON path segment
/// ("schoolReference" -> "School"). The concrete member carries a nameOverrides entry renaming its
/// physical reference identity column; the abstract identity column must use the MappingKey-derived
/// convention name (SchoolBase_SchoolId), matching concrete naming exactly.
/// This override-free behavior is intentional product behavior for DMS-1223: the abstract identity column
/// is named by the shared override-free convention so all concrete members agree on one name, rather than
/// inheriting any single member's relational.nameOverrides.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Column_Naming_With_Name_Override
{
    private AbstractIdentityTableInfo _abstractTable = default!;

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
                new DescriptorResourceMappingPass(),
                new ExtensionTableDerivationPass(),
                new ReferenceBindingPass(),
                new KeyUnificationPass(),
                new AbstractIdentityTableAndUnionViewDerivationPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _abstractTable = result.AbstractIdentityTablesInNameOrder.Single(table =>
            table.AbstractResourceKey.Resource.ResourceName == "SchoolCarrier"
        );
    }

    /// <summary>
    /// The abstract identity column uses the MappingKey-derived convention name (SchoolBase_SchoolId),
    /// matching concrete naming — not the "schoolReference" path-segment form (School_SchoolId) and not
    /// the concrete relational.nameOverrides physical name.
    /// </summary>
    [Test]
    public void It_should_name_abstract_identity_column_by_convention_ignoring_concrete_name_override()
    {
        var columnNames = _abstractTable
            .TableModel.Columns.Select(column => column.ColumnName.Value)
            .ToArray();

        // MappingKey ("SchoolBase") derived, matching concrete — not the path segment ("School").
        columnNames.Should().Contain("SchoolBase_SchoolId");
        columnNames.Should().NotContain("School_SchoolId");
        // The concrete relational.nameOverrides ("campusId") is not propagated to the abstract column.
        columnNames.Should().NotContain(name => name.Contains("ampus", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds a project schema where the concrete member has a nameOverride on its reference identity column
    /// but the abstract table must still use the convention name.
    /// </summary>
    private static JsonObject BuildProjectSchema() => SchoolCarrierOverrideTestSchema.BuildProjectSchema();
}

/// <summary>
/// Test fixture for abstract identity column naming: subclass rename via superclassIdentityJsonPath.
/// When a concrete member declares superclassIdentityJsonPath, its own identity path is different from the
/// abstract identity path. The abstract identity column must be named from the abstract path, not the member path.
/// Abstract: $.educationOrganizationId → EducationOrganizationId (plain scalar, unchanged).
/// Member: identity is $.schoolId, but superclassIdentityJsonPath maps it to $.educationOrganizationId.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Table_With_Subclass_Identity_Rename
{
    private AbstractIdentityTableInfo _abstractTable = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = new JsonObject
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
                    // superclassIdentityJsonPath declares that $.schoolId maps to $.educationOrganizationId
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

        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingPass(),
                new AbstractIdentityTableAndUnionViewDerivationPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _abstractTable = result.AbstractIdentityTablesInNameOrder.Single();
    }

    /// <summary>
    /// Subclass rename: abstract identity column EducationOrganizationId is derived from the abstract path
    /// $.educationOrganizationId even though the concrete member's own identity is $.schoolId.
    /// The member's concrete column SchoolId is used as the union-view projection source, but the abstract
    /// identity table column is named EducationOrganizationId (plain scalar, unchanged from abstract path).
    /// </summary>
    [Test]
    public void It_should_name_subclass_rename_abstract_identity_column_from_abstract_path()
    {
        _abstractTable
            .TableModel.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Contain("EducationOrganizationId");

        // The member's own concrete column name must NOT appear on the abstract identity table.
        _abstractTable
            .TableModel.Columns.Select(column => column.ColumnName.Value)
            .Should()
            .NotContain("SchoolId");
    }
}

/// <summary>
/// Test fixture for an abstract identity path that is reference-backed on one concrete member but supplied
/// as a plain (non-reference) scalar on another. Real Ed-Fi schemas never mix these for a single abstract
/// identity path; this fixture forces the shape to prove the derivation fails fast rather than silently
/// naming the abstract column from the reference-backed member while the scalar member projects a
/// differently-named column.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Path_Mixing_Reference_And_Scalar_Members
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
                new DescriptorResourceMappingPass(),
                new ExtensionTableDerivationPass(),
                new ReferenceBindingPass(),
                new KeyUnificationPass(),
                new AbstractIdentityTableAndUnionViewDerivationPass(),
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
    /// It should fail fast when members resolve one abstract identity path through different binding kinds.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_members_mix_reference_and_non_reference_resolution()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!
            .Message.Should()
            .Contain("reference-backed on some members but reaches the path via a non-reference column");
        _exception.Message.Should().Contain("$.schoolReference.schoolId");
        // The scalar-backed member is named in the diagnostic so the offending shape is identifiable.
        _exception.Message.Should().Contain("Ed-Fi:ScalarMember");
    }

    /// <summary>
    /// Builds a project schema whose abstract identity path is reference-backed on ReferenceMember but
    /// remapped from a plain top-level scalar on ScalarMember (via superclassIdentityJsonPath).
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
                ["MixedCarrier"] = new JsonObject
                {
                    ["resourceName"] = "MixedCarrier",
                    ["identityJsonPaths"] = new JsonArray { "$.schoolReference.schoolId" },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["referenceMembers"] = BuildReferenceMemberSchema(),
                ["scalarMembers"] = BuildScalarMemberSchema(),
                ["schools"] = BuildSchoolTargetSchema(),
            },
        };
    }

    /// <summary>
    /// ReferenceMember: subclass of MixedCarrier whose identity reaches $.schoolReference.schoolId through a
    /// document reference to School.
    /// </summary>
    private static JsonObject BuildReferenceMemberSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "ReferenceMember",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = true,
            ["subclassType"] = "association",
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "MixedCarrier",
            ["superclassIdentityJsonPath"] = null,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject
            {
                ["School"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "School",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.schoolId",
                            ["referenceJsonPath"] = "$.schoolReference.schoolId",
                        },
                    },
                },
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["equalityConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolReference.schoolId" },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["schoolReference"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["schoolId"] = new JsonObject { ["type"] = "integer", ["format"] = "int64" },
                        },
                        ["required"] = new JsonArray { "schoolId" },
                    },
                },
                ["required"] = new JsonArray { "schoolReference" },
            },
        };
    }

    /// <summary>
    /// ScalarMember: subclass of MixedCarrier whose own identity is the plain top-level scalar $.schoolId,
    /// remapped onto the abstract path $.schoolReference.schoolId via superclassIdentityJsonPath.
    /// </summary>
    private static JsonObject BuildScalarMemberSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "ScalarMember",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = true,
            ["subclassType"] = "association",
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "MixedCarrier",
            ["superclassIdentityJsonPath"] = "$.schoolReference.schoolId",
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
            ["equalityConstraints"] = new JsonArray(),
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
        };
    }

    /// <summary>
    /// School: standalone target resource referenced by ReferenceMember.
    /// </summary>
    private static JsonObject BuildSchoolTargetSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["SchoolId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["path"] = "$.schoolId",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["schoolId"] = new JsonObject { ["type"] = "integer", ["format"] = "int64" },
                },
                ["required"] = new JsonArray { "schoolId" },
            },
        };
    }
}

/// <summary>
/// Test fixture for an abstract identity path supplied through grouped duplicate reference bindings where
/// NO binding is field-name-matched (the reference field name matches neither key-unified target identity
/// field), so the candidate convention columns diverge with no field-matched representative. Abstract
/// identity naming must fail fast rather than silently pick one. This is the pathological shape real Ed-Fi
/// schemas never produce; it directly exercises the ambiguous-convention-column guard.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Path_With_Ambiguous_Grouped_Reference_Bindings
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
                new DescriptorResourceMappingPass(),
                new ExtensionTableDerivationPass(),
                new ReferenceBindingPass(),
                new KeyUnificationPass(),
                new AbstractIdentityTableAndUnionViewDerivationPass(),
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
    /// It should fail fast when grouped reference bindings yield divergent convention columns with no
    /// field-name-matched representative.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_grouped_reference_bindings_have_ambiguous_convention_columns()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("ambiguous convention columns");
        _exception.Message.Should().Contain("$.schoolReference.schoolId");
    }

    private static JsonObject BuildProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "5.0.0",
            ["abstractResources"] = new JsonObject
            {
                ["AmbiguousCarrier"] = new JsonObject
                {
                    ["resourceName"] = "AmbiguousCarrier",
                    ["identityJsonPaths"] = new JsonArray { "$.schoolReference.schoolId" },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["ambiguousMembers"] = BuildMemberSchema(),
                ["schools"] = BuildTargetSchema(),
            },
        };
    }

    /// <summary>
    /// AmbiguousMember: subclass of AmbiguousCarrier whose single reference field $.schoolReference.schoolId
    /// feeds two key-unified target identity fields, neither named schoolId — so no candidate is
    /// field-name-matched.
    /// </summary>
    private static JsonObject BuildMemberSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "AmbiguousMember",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = true,
            ["subclassType"] = "association",
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "AmbiguousCarrier",
            ["superclassIdentityJsonPath"] = null,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject
            {
                ["School"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "School",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.localEducationAgencyId",
                            ["referenceJsonPath"] = "$.schoolReference.schoolId",
                        },
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.stateEducationAgencyId",
                            ["referenceJsonPath"] = "$.schoolReference.schoolId",
                        },
                    },
                },
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["equalityConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolReference.schoolId" },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["schoolReference"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["schoolId"] = new JsonObject { ["type"] = "integer" },
                        },
                        ["required"] = new JsonArray { "schoolId" },
                    },
                },
                ["required"] = new JsonArray { "schoolReference" },
            },
        };
    }

    /// <summary>
    /// School: target whose two identity fields are key-unified by an equality constraint, so one reference
    /// field can feed both.
    /// </summary>
    private static JsonObject BuildTargetSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.localEducationAgencyId", "$.stateEducationAgencyId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["LocalEducationAgencyId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["path"] = "$.localEducationAgencyId",
                },
                ["StateEducationAgencyId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["path"] = "$.stateEducationAgencyId",
                },
            },
            ["equalityConstraints"] = new JsonArray
            {
                new JsonObject
                {
                    ["sourceJsonPath"] = "$.localEducationAgencyId",
                    ["targetJsonPath"] = "$.stateEducationAgencyId",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["localEducationAgencyId"] = new JsonObject { ["type"] = "integer" },
                    ["stateEducationAgencyId"] = new JsonObject { ["type"] = "integer" },
                },
                ["required"] = new JsonArray("localEducationAgencyId", "stateEducationAgencyId"),
            },
        };
    }
}

/// <summary>
/// Test fixture for an abstract identity path that two concrete members both reach through a document
/// reference, but via differently-named references: the same reference object path is mapped under different
/// documentPathsMapping MappingKeys (<c>Program</c> vs <c>ProgramAlias</c>). The convention column is
/// MappingKey-derived, so the two members resolve the single abstract identity path to divergent column names
/// (<c>Program_EducationOrganizationId</c> vs <c>ProgramAlias_EducationOrganizationId</c>). Real Ed-Fi schemas
/// never name the same shared reference differently across an abstract resource's members; this fixture forces
/// the shape to prove the derivation fails fast rather than silently letting one member's name win while the
/// other projects a differently-named column under the single abstract contract.
/// </summary>
[TestFixture]
public class Given_Abstract_Identity_Path_With_Divergent_Member_Convention_Columns
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
                new DescriptorResourceMappingPass(),
                new ExtensionTableDerivationPass(),
                new ReferenceBindingPass(),
                new KeyUnificationPass(),
                new AbstractIdentityTableAndUnionViewDerivationPass(),
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
    /// It should fail fast when members resolve one abstract identity path to different convention columns.
    /// </summary>
    [Test]
    public void It_should_fail_fast_when_members_resolve_one_abstract_path_to_divergent_convention_columns()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("ambiguous abstract identity naming");
        // Both divergent MappingKey-derived column names appear in the diagnostic.
        _exception.Message.Should().Contain("Program_EducationOrganizationId");
        _exception.Message.Should().Contain("ProgramAlias_EducationOrganizationId");
    }

    private static JsonObject BuildProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "5.0.0",
            ["abstractResources"] = new JsonObject
            {
                ["DivergentCarrier"] = new JsonObject
                {
                    ["resourceName"] = "DivergentCarrier",
                    ["identityJsonPaths"] = new JsonArray { "$.programReference.educationOrganizationId" },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["firstDivergentMembers"] = BuildMemberSchema("FirstDivergentMember", "Program"),
                ["secondDivergentMembers"] = BuildMemberSchema("SecondDivergentMember", "ProgramAlias"),
                ["programs"] = BuildProgramTargetSchema(),
            },
        };
    }

    /// <summary>
    /// Member subclass of DivergentCarrier whose identity reaches $.programReference.educationOrganizationId
    /// through a document reference to Program under the supplied MappingKey (the reference role name). The
    /// MappingKey drives the convention column name, so two members using different keys for the same shared
    /// reference object path diverge.
    /// </summary>
    private static JsonObject BuildMemberSchema(string resourceName, string mappingKey)
    {
        return new JsonObject
        {
            ["resourceName"] = resourceName,
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = true,
            ["subclassType"] = "association",
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "DivergentCarrier",
            ["superclassIdentityJsonPath"] = null,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject
            {
                [mappingKey] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Program",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] = "$.programReference.educationOrganizationId",
                        },
                    },
                },
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["equalityConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.programReference.educationOrganizationId" },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["programReference"] = new JsonObject
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
                },
                ["required"] = new JsonArray { "programReference" },
            },
        };
    }

    /// <summary>
    /// Program: standalone target referenced by both members.
    /// </summary>
    private static JsonObject BuildProgramTargetSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "Program",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["path"] = "$.educationOrganizationId",
                },
            },
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
    /// Build project schema with grouped reference-field duplicates that key-unify before abstract identity
    /// derivation.
    /// </summary>
    internal static JsonObject BuildGroupedReferenceIdentityProjectSchema(
        bool reverseDuplicateReferenceBindings = false
    )
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "5.0.0",
            ["abstractResources"] = new JsonObject
            {
                ["EnrollmentCarrier"] = new JsonObject
                {
                    ["resourceName"] = "EnrollmentCarrier",
                    ["identityJsonPaths"] = new JsonArray { "$.schoolReference.schoolId" },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollmentSchoolCarriers"] = BuildGroupedReferenceIdentityMemberSchema(
                    reverseDuplicateReferenceBindings
                ),
                ["schools"] = BuildGroupedReferenceIdentityTargetSchema(),
            },
        };
    }

    /// <summary>
    /// Build project schema whose abstract identity is a descriptor-valued member.
    /// </summary>
    internal static JsonObject BuildDescriptorIdentityProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "5.0.0",
            ["abstractResources"] = new JsonObject
            {
                ["ProgramCarrier"] = new JsonObject
                {
                    ["resourceName"] = "ProgramCarrier",
                    ["identityJsonPaths"] = new JsonArray { "$.programTypeDescriptor" },
                },
            },
            ["resourceSchemas"] = new JsonObject
            {
                ["programOfferings"] = BuildDescriptorIdentityMemberSchema(),
                ["programTypeDescriptors"] = BuildProgramTypeDescriptorSchema(),
            },
        };
    }

    /// <summary>
    /// Build subclass resource schema whose identity comes from one grouped duplicate reference field.
    /// </summary>
    private static JsonObject BuildGroupedReferenceIdentityMemberSchema(
        bool reverseDuplicateReferenceBindings
    )
    {
        var schoolIdBinding = new JsonObject
        {
            ["identityJsonPath"] = "$.schoolId",
            ["referenceJsonPath"] = "$.schoolReference.schoolId",
        };
        var localEducationAgencyIdBinding = new JsonObject
        {
            ["identityJsonPath"] = "$.localEducationAgencyId",
            ["referenceJsonPath"] = "$.schoolReference.schoolId",
        };
        var referenceJsonPaths = reverseDuplicateReferenceBindings
            ? new JsonArray { localEducationAgencyIdBinding, schoolIdBinding }
            : new JsonArray { schoolIdBinding, localEducationAgencyIdBinding };

        return new JsonObject
        {
            ["resourceName"] = "EnrollmentSchoolCarrier",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = true,
            ["subclassType"] = "association",
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "EnrollmentCarrier",
            ["superclassIdentityJsonPath"] = null,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject
            {
                ["School"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "School",
                    ["referenceJsonPaths"] = referenceJsonPaths,
                },
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolReference.schoolId" },
            ["equalityConstraints"] = new JsonArray(),
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["schoolReference"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["schoolId"] = new JsonObject { ["type"] = "integer" },
                        },
                        ["required"] = new JsonArray { "schoolId" },
                    },
                },
                ["required"] = new JsonArray { "schoolReference" },
            },
        };
    }

    /// <summary>
    /// Build subclass resource schema with one descriptor-valued identity member.
    /// </summary>
    private static JsonObject BuildDescriptorIdentityMemberSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "ProgramOffering",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = true,
            ["subclassType"] = "association",
            ["superclassProjectName"] = "Ed-Fi",
            ["superclassResourceName"] = "ProgramCarrier",
            ["superclassIdentityJsonPath"] = null,
            ["allowIdentityUpdates"] = false,
            ["documentPathsMapping"] = new JsonObject
            {
                ["ProgramTypeDescriptor"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = true,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "ProgramTypeDescriptor",
                    ["path"] = "$.programTypeDescriptor",
                },
            },
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.programTypeDescriptor" },
            ["equalityConstraints"] = new JsonArray(),
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["programTypeDescriptor"] = new JsonObject { ["type"] = "string", ["maxLength"] = 306 },
                },
                ["required"] = new JsonArray("programTypeDescriptor"),
            },
        };
    }

    /// <summary>
    /// Build minimal program type descriptor schema.
    /// </summary>
    private static JsonObject BuildProgramTypeDescriptorSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "ProgramTypeDescriptor",
            ["isDescriptor"] = true,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["codeValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                    ["namespace"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 },
                },
                ["required"] = new JsonArray("codeValue", "namespace"),
            },
        };
    }

    /// <summary>
    /// Build target school schema whose key unification makes the grouped reference duplicates converge.
    /// </summary>
    private static JsonObject BuildGroupedReferenceIdentityTargetSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = true,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolId", "$.localEducationAgencyId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["SchoolId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["path"] = "$.schoolId",
                },
                ["LocalEducationAgencyId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["isDescriptor"] = false,
                    ["path"] = "$.localEducationAgencyId",
                },
            },
            ["equalityConstraints"] = new JsonArray
            {
                new JsonObject
                {
                    ["sourceJsonPath"] = "$.schoolId",
                    ["targetJsonPath"] = "$.localEducationAgencyId",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["schoolId"] = new JsonObject { ["type"] = "integer" },
                    ["localEducationAgencyId"] = new JsonObject { ["type"] = "integer" },
                },
                ["required"] = new JsonArray("schoolId", "localEducationAgencyId"),
            },
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
