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
/// Test fixture for an effective schema set with a missing document paths mapping descriptor target.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaSet_With_A_Missing_DocumentPathsMapping_Descriptor_Target
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with a missing descriptor target.
    /// </summary>
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

/// <summary>
/// Test fixture for an effective schema set with a missing document paths mapping reference target.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaSet_With_A_Missing_DocumentPathsMapping_Reference_Target
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should fail with a missing reference target.
    /// </summary>
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

/// <summary>
/// Test fixture for an effective schema set with an abstract resource missing document paths mapping target.
/// </summary>
[TestFixture]
public class Given_An_EffectiveSchemaSet_With_An_AbstractResource_Missing_DocumentPathsMapping_Target
{
    private Exception? _exception;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

        var resourceKeys = new[]
        {
            EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "AbstractSchool", isAbstractResource: true),
        };
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

    /// <summary>
    /// It should fail with a missing reference target in abstract resource.
    /// </summary>
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

/// <summary>
/// Test fixture for reordered resources and document paths mapping.
/// </summary>
[TestFixture]
public class Given_Reordered_Resources_And_DocumentPathsMapping
{
    private Exception? _orderedException;
    private Exception? _reorderedException;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _orderedException = CaptureBuildException(reverseResourceOrder: false, reverseMappingOrder: false);
        _reorderedException = CaptureBuildException(reverseResourceOrder: true, reverseMappingOrder: true);
    }

    /// <summary>
    /// It should report missing targets in canonical order.
    /// </summary>
    [Test]
    public void It_should_report_missing_targets_in_canonical_order()
    {
        _orderedException.Should().BeOfType<InvalidOperationException>();
        _reorderedException.Should().BeOfType<InvalidOperationException>();
        _orderedException!.Message.Should().Be(_reorderedException!.Message);
        _orderedException.Message.Should().Contain("Alpha");
        _orderedException.Message.Should().Contain("AlphaMissing");
    }

    /// <summary>
    /// Capture build exception.
    /// </summary>
    private static Exception CaptureBuildException(bool reverseResourceOrder, bool reverseMappingOrder)
    {
        var projectSchema = CreateProjectSchema(reverseResourceOrder, reverseMappingOrder);
        var resourceKeys = new[]
        {
            EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "Alpha"),
            EffectiveSchemaFixture.CreateResourceKey(2, "Ed-Fi", "Beta"),
        };
        var effectiveSchemaSet = EffectiveSchemaFixture.CreateEffectiveSchemaSet(projectSchema, resourceKeys);

        var builder = new DerivedRelationalModelSetBuilder(Array.Empty<IRelationalModelSetPass>());

        try
        {
            builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("Expected builder to fail due to missing targets.");
    }

    /// <summary>
    /// Create project schema.
    /// </summary>
    private static JsonObject CreateProjectSchema(bool reverseResourceOrder, bool reverseMappingOrder)
    {
        var resourceEntries = reverseResourceOrder
            ? new[] { ("betas", "Beta"), ("alphas", "Alpha") }
            : new[] { ("alphas", "Alpha"), ("betas", "Beta") };

        JsonObject resourceSchemas = new();

        foreach (var entry in resourceEntries)
        {
            resourceSchemas[entry.Item1] = new JsonObject
            {
                ["resourceName"] = entry.Item2,
                ["isResourceExtension"] = false,
            };
        }

        var alphaSchema = (JsonObject)resourceSchemas["alphas"]!;
        alphaSchema["documentPathsMapping"] = CreateAlphaMappings(reverseMappingOrder);

        var betaSchema = (JsonObject)resourceSchemas["betas"]!;
        betaSchema["documentPathsMapping"] = new JsonObject
        {
            ["Beta"] = new JsonObject
            {
                ["isReference"] = true,
                ["isDescriptor"] = false,
                ["projectName"] = "Ed-Fi",
                ["resourceName"] = "BetaMissing",
                ["path"] = "$.betaReference",
            },
        };

        return new JsonObject { ["resourceSchemas"] = resourceSchemas };
    }

    /// <summary>
    /// Create alpha mappings.
    /// </summary>
    private static JsonObject CreateAlphaMappings(bool reverseMappingOrder)
    {
        var alphaMapping = new JsonObject
        {
            ["isReference"] = true,
            ["isDescriptor"] = false,
            ["projectName"] = "Ed-Fi",
            ["resourceName"] = "AlphaMissing",
            ["path"] = "$.alphaReference",
        };

        var zebraMapping = new JsonObject
        {
            ["isReference"] = true,
            ["isDescriptor"] = false,
            ["projectName"] = "Ed-Fi",
            ["resourceName"] = "ZebraMissing",
            ["path"] = "$.zebraReference",
        };

        JsonObject documentPathsMapping = new();

        if (reverseMappingOrder)
        {
            documentPathsMapping["Zebra"] = zebraMapping;
            documentPathsMapping["Alpha"] = alphaMapping;
        }
        else
        {
            documentPathsMapping["Alpha"] = alphaMapping;
            documentPathsMapping["Zebra"] = zebraMapping;
        }

        return documentPathsMapping;
    }
}
