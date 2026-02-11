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
/// Test fixture for abstract union view derivation.
/// </summary>
[TestFixture]
public class Given_Abstract_Union_View_Derivation
{
    private AbstractUnionViewInfo _abstractUnionView = default!;

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
                new AbstractIdentityTableAndUnionViewDerivationPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _abstractUnionView = result.AbstractUnionViewsInNameOrder.Single(view =>
            view.AbstractResourceKey.Resource.ResourceName == "EducationOrganization"
        );
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
}

/// <summary>
/// Test fixture for abstract union view derivation with widenable string max-length mismatches.
/// </summary>
[TestFixture]
public class Given_Abstract_Union_View_With_Widenable_String_Length_Mismatch
{
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
                new AbstractIdentityTableAndUnionViewDerivationPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _abstractUnionView = result.AbstractUnionViewsInNameOrder.Single();
    }

    /// <summary>
    /// It should choose the widest string max length for the canonical abstract union view output column type.
    /// </summary>
    [Test]
    public void It_should_choose_widest_string_max_length_for_canonical_output_column_type()
    {
        var outputColumn = _abstractUnionView.OutputColumnsInSelectOrder.Single(column =>
            column.ColumnName.Value == "OrganizationName"
        );

        outputColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.String, 255));
    }
}

/// <summary>
/// Test fixture for abstract union view derivation with widenable integer-width mismatches.
/// </summary>
[TestFixture]
public class Given_Abstract_Union_View_With_Widenable_Integer_Widths
{
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
                new AbstractIdentityTableAndUnionViewDerivationPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _abstractUnionView = result.AbstractUnionViewsInNameOrder.Single();
    }

    /// <summary>
    /// It should widen mixed int32/int64 identities to int64 for canonical abstract union view output columns.
    /// </summary>
    [Test]
    public void It_should_widen_mixed_int32_and_int64_identities_to_int64()
    {
        var outputColumn = _abstractUnionView.OutputColumnsInSelectOrder.Single(column =>
            column.ColumnName.Value == "EducationOrganizationId"
        );

        outputColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Int64));
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
                new AbstractIdentityTableAndUnionViewDerivationPass(),
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
                new AbstractIdentityTableAndUnionViewDerivationPass(),
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
/// Test fixture for concrete resources that omit the optional isSubclass property.
/// </summary>
[TestFixture]
public class Given_Concrete_Resource_Without_IsSubclass_Property
{
    private Exception? _exception;
    private string[] _memberResourceNames = [];

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
        var schoolSchema = (JsonObject)resourceSchemas["schools"]!;
        schoolSchema.Remove("isSubclass");
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
            var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
            _memberResourceNames = result
                .AbstractUnionViewsInNameOrder.Single(view =>
                    view.AbstractResourceKey.Resource.ResourceName == "EducationOrganization"
                )
                .UnionArmsInOrder.Select(arm => arm.ConcreteMemberResourceKey.Resource.ResourceName)
                .ToArray();
        }
        catch (Exception exception)
        {
            _exception = exception;
        }
    }

    /// <summary>
    /// It should default missing isSubclass to false and not include the resource as an abstract member.
    /// </summary>
    [Test]
    public void It_should_not_treat_resource_without_is_subclass_property_as_subclass_member()
    {
        _exception.Should().BeNull();
        _memberResourceNames.Should().Equal("LocalEducationAgency");
    }
}
