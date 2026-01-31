// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_Root_Unique_Constraint_Derivation
{
    private DbTableModel _rootTable = default!;

    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildReferenceIdentityProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ReferenceBindingRelationalModelSetPass(),
                new ConstraintDerivationRelationalModelSetPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var enrollmentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "Enrollment"
            )
            .RelationalModel;

        _rootTable = enrollmentModel.Root;
    }

    [Test]
    public void It_should_create_root_unique_using_reference_document_ids()
    {
        var uniqueConstraint = _rootTable.Constraints.OfType<TableConstraint.Unique>().Single();

        uniqueConstraint
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("School_DocumentId", "Student_DocumentId");
        uniqueConstraint.Name.Should().Be("UX_Enrollment_School_DocumentId_Student_DocumentId");
    }
}

[TestFixture]
public class Given_Descriptor_Unique_Constraint_Derivation
{
    private DbTableModel _descriptorTable = default!;

    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildDescriptorProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ConstraintDerivationRelationalModelSetPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var descriptorModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "GradeLevelDescriptor"
            )
            .RelationalModel;

        _descriptorTable = descriptorModel.Root;
    }

    [Test]
    public void It_should_add_uri_and_discriminator_columns()
    {
        var uriColumn = _descriptorTable.Columns.Single(column => column.ColumnName.Value == "Uri");
        var discriminatorColumn = _descriptorTable.Columns.Single(column =>
            column.ColumnName.Value == "Discriminator"
        );

        uriColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.String, 306));
        uriColumn.IsNullable.Should().BeFalse();
        uriColumn.SourceJsonPath.Should().BeNull();

        discriminatorColumn.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.String, 128));
        discriminatorColumn.IsNullable.Should().BeFalse();
        discriminatorColumn.SourceJsonPath.Should().BeNull();
    }

    [Test]
    public void It_should_define_unique_on_uri_and_discriminator()
    {
        var uniqueConstraint = _descriptorTable.Constraints.OfType<TableConstraint.Unique>().Single();

        uniqueConstraint.Columns.Select(column => column.Value).Should().Equal("Uri", "Discriminator");
        uniqueConstraint.Name.Should().Be("UX_Descriptor_Uri_Discriminator");
    }
}

[TestFixture]
public class Given_Unmappable_Identity_Paths
{
    [Test]
    public void It_should_fail_fast_when_identity_path_cannot_be_mapped()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildArrayIdentityProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { coreProject });
        var builder = new DerivedRelationalModelSetBuilder(
            new IRelationalModelSetPass[]
            {
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new ConstraintDerivationRelationalModelSetPass(),
            }
        );

        Action action = () => builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        action.Should().Throw<InvalidOperationException>().WithMessage("*addresses[*].streetNumberName*");
    }
}

internal static class ConstraintDerivationTestSchemaBuilder
{
    internal static JsonObject BuildReferenceIdentityProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildEnrollmentSchema(),
                ["schools"] = BuildSchoolSchema(),
                ["students"] = BuildStudentSchema(),
            },
        };
    }

    internal static JsonObject BuildDescriptorProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["gradeLevelDescriptors"] = BuildDescriptorSchema() },
        };
    }

    internal static JsonObject BuildArrayIdentityProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["contacts"] = BuildArrayIdentitySchema() },
        };
    }

    private static JsonObject BuildEnrollmentSchema()
    {
        var jsonSchemaForInsert = new JsonObject
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
                        ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
                    },
                },
                ["studentReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Enrollment",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray
            {
                "$.schoolReference.schoolId",
                "$.schoolReference.educationOrganizationId",
                "$.studentReference.studentUniqueId",
            },
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
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.educationOrganizationId",
                            ["referenceJsonPath"] = "$.schoolReference.educationOrganizationId",
                        },
                    },
                },
                ["Student"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Student",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.studentUniqueId",
                            ["referenceJsonPath"] = "$.studentReference.studentUniqueId",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildSchoolSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schoolId"] = new JsonObject { ["type"] = "integer" },
                ["educationOrganizationId"] = new JsonObject { ["type"] = "integer" },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.schoolId", "$.educationOrganizationId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["SchoolId"] = new JsonObject { ["isReference"] = false, ["path"] = "$.schoolId" },
                ["EducationOrganizationId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.educationOrganizationId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildStudentSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["studentUniqueId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32 },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Student",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.studentUniqueId" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["StudentUniqueId"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.studentUniqueId",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildDescriptorSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["namespace"] = new JsonObject { ["type"] = "string", ["maxLength"] = 255 },
                ["codeValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 50 },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "GradeLevelDescriptor",
            ["isDescriptor"] = true,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildArrayIdentitySchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["addresses"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["streetNumberName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
                        },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Contact",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.addresses[*].streetNumberName" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["StreetNumberName"] = new JsonObject
                {
                    ["isReference"] = false,
                    ["path"] = "$.addresses[*].streetNumberName",
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }
}
