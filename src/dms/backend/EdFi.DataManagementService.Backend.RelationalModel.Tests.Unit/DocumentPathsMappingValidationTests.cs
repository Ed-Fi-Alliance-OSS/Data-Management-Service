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
public class Given_An_EffectiveSchemaSet_With_A_Missing_DocumentPathsMapping_Descriptor_Target
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false));
        var resourceSchemas = (JsonObject)projectSchema["resourceSchemas"]!;
        var schoolSchema = (JsonObject)resourceSchemas["schools"]!;

        schoolSchema["documentPathsMapping"] = new JsonObject
        {
            ["SchoolTypeDescriptor"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = true,
                ["projectName"] = "Ed-Fi",
                ["resourceName"] = "SchoolTypeDescriptor",
                ["path"] = "$.schoolTypeDescriptor",
            },
        };

        var resourceKeys = new[] { EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "School") };
        var effectiveSchemaSet = EffectiveSchemaFixture.CreateEffectiveSchemaSet(projectSchema, resourceKeys);

        var builder = new DerivedRelationalModelSetBuilder(Array.Empty<IRelationalModelSetPass>());

        try
        {
            builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_fail_with_a_missing_descriptor_target()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("documentPathsMapping");
        _exception.Message.Should().Contain("SchoolTypeDescriptor");
        _exception.Message.Should().Contain("$.schoolTypeDescriptor");
        _exception.Message.Should().Contain("Ed-Fi:School");
        _exception.Message.Should().Contain("Ed-Fi:SchoolTypeDescriptor");
    }
}

[TestFixture]
public class Given_An_EffectiveSchemaSet_With_A_Missing_DocumentPathsMapping_Reference_Target
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema(("sections", "Section", false));
        var resourceSchemas = (JsonObject)projectSchema["resourceSchemas"]!;
        var sectionSchema = (JsonObject)resourceSchemas["sections"]!;

        sectionSchema["documentPathsMapping"] = new JsonObject
        {
            ["School"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = false,
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
        };

        var resourceKeys = new[] { EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "Section") };
        var effectiveSchemaSet = EffectiveSchemaFixture.CreateEffectiveSchemaSet(projectSchema, resourceKeys);

        var builder = new DerivedRelationalModelSetBuilder(Array.Empty<IRelationalModelSetPass>());

        try
        {
            builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_fail_with_a_missing_reference_target()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("documentPathsMapping");
        _exception.Message.Should().Contain("School");
        _exception.Message.Should().Contain("Ed-Fi:Section");
        _exception.Message.Should().Contain("Ed-Fi:School");
    }
}

[TestFixture]
public class Given_An_EffectiveSchemaSet_With_An_AbstractResource_Missing_DocumentPathsMapping_Target
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var projectSchema = EffectiveSchemaFixture.CreateProjectSchema();
        EffectiveSchemaFixture.AddAbstractResources(projectSchema, ("abstractSchools", "AbstractSchool"));

        var abstractResources = (JsonObject)projectSchema["abstractResources"]!;
        var abstractSchoolSchema = (JsonObject)abstractResources["abstractSchools"]!;

        abstractSchoolSchema["documentPathsMapping"] = new JsonObject
        {
            ["School"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = false,
                ["projectName"] = "Ed-Fi",
                ["resourceName"] = "School",
                ["path"] = "$.schoolReference",
            },
        };

        var resourceKeys = new[] { EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "AbstractSchool") };
        var effectiveSchemaSet = EffectiveSchemaFixture.CreateEffectiveSchemaSet(projectSchema, resourceKeys);

        var builder = new DerivedRelationalModelSetBuilder(Array.Empty<IRelationalModelSetPass>());

        try
        {
            builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_fail_with_a_missing_reference_target_in_abstract_resource()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("documentPathsMapping");
        _exception.Message.Should().Contain("School");
        _exception.Message.Should().Contain("$.schoolReference");
        _exception.Message.Should().Contain("Ed-Fi:AbstractSchool");
        _exception.Message.Should().Contain("Ed-Fi:School");
    }
}
