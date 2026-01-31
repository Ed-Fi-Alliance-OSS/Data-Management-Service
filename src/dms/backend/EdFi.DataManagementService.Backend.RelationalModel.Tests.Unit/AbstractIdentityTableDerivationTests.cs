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
public class Given_Abstract_Identity_Table_Derivation
{
    private AbstractIdentityTableInfo _abstractIdentityTable = default!;

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
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new AbstractIdentityTableDerivationRelationalModelSetPass(),
            }
        );

        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        _abstractIdentityTable = result.AbstractIdentityTablesInNameOrder.Single(table =>
            table.AbstractResourceKey.Resource.ResourceName == "EducationOrganization"
        );
    }

    [Test]
    public void It_should_order_identity_columns_by_identityJsonPaths()
    {
        var identityColumns = _abstractIdentityTable
            .ColumnsInIdentityOrder.Where(column => column.ColumnName.Value != "Discriminator")
            .Select(column => column.ColumnName.Value)
            .ToArray();

        identityColumns.Should().Equal("EducationOrganizationId", "OrganizationName");
    }

    [Test]
    public void It_should_include_discriminator_column()
    {
        var discriminator = _abstractIdentityTable.ColumnsInIdentityOrder.Single(column =>
            column.ColumnName.Value == "Discriminator"
        );

        discriminator.Kind.Should().Be(ColumnKind.Scalar);
        discriminator.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.String, 256));
        discriminator.IsNullable.Should().BeFalse();
    }

    [Test]
    public void It_should_include_composite_unique_constraint()
    {
        var unique = _abstractIdentityTable.Constraints.OfType<TableConstraint.Unique>().Single();

        unique
            .Columns.Select(column => column.Value)
            .Should()
            .Equal("DocumentId", "EducationOrganizationId", "OrganizationName");
        unique
            .Name.Should()
            .Be("UX_EducationOrganizationIdentity_DocumentId_EducationOrganizationId_OrganizationName");
    }

    [Test]
    public void It_should_use_no_action_on_update_for_document_fk()
    {
        var foreignKey = _abstractIdentityTable.Constraints.OfType<TableConstraint.ForeignKey>().Single();

        foreignKey.TargetTable.Schema.Value.Should().Be("dms");
        foreignKey.TargetTable.Name.Should().Be("Document");
        foreignKey.OnDelete.Should().Be(ReferentialAction.Cascade);
        foreignKey.OnUpdate.Should().Be(ReferentialAction.NoAction);
    }

    private static JsonObject BuildProjectSchema(bool mismatchMemberType)
    {
        return AbstractIdentityTableTestSchemaBuilder.BuildProjectSchema(mismatchMemberType);
    }
}

[TestFixture]
public class Given_Abstract_Identity_Table_With_Mismatched_Types
{
    private Exception? _exception;

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
                new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
                new AbstractIdentityTableDerivationRelationalModelSetPass(),
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

    [Test]
    public void It_should_fail_fast_on_mismatched_member_types()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("inconsistent column types");
    }
}

internal static class AbstractIdentityTableTestSchemaBuilder
{
    internal static JsonObject BuildProjectSchema(bool mismatchMemberType)
    {
        var abstractResources = new JsonObject
        {
            ["EducationOrganization"] = new JsonObject
            {
                ["resourceName"] = "EducationOrganization",
                ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId", "$.organizationName" },
            },
        };

        var resourceSchemas = new JsonObject
        {
            ["schools"] = BuildConcreteResourceSchema(
                "School",
                organizationNameIsString: true,
                organizationNameMaxLength: 60
            ),
            ["localEducationAgencies"] = BuildConcreteResourceSchema(
                "LocalEducationAgency",
                organizationNameIsString: !mismatchMemberType,
                organizationNameMaxLength: mismatchMemberType ? 40 : 60
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

    private static JsonObject BuildConcreteResourceSchema(
        string resourceName,
        bool organizationNameIsString,
        int organizationNameMaxLength
    )
    {
        var properties = new JsonObject
        {
            ["educationOrganizationId"] = new JsonObject { ["type"] = "integer", ["format"] = "int64" },
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
            ["identityJsonPaths"] = new JsonArray { "$.educationOrganizationId", "$.organizationName" },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }
}
