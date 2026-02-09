// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using EdFi.DataManagementService.Backend.RelationalModel.DescriptorPaths;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for a multi project effective schema set with cross project descriptor propagation.
/// </summary>
[TestFixture]
public class Given_A_MultiProject_EffectiveSchemaSet_With_CrossProject_Descriptor_Propagation
{
    private RelationalModelSetBuilderContext _context = default!;
    private QualifiedResourceName _schoolResource = default!;
    private QualifiedResourceName _sectionResource = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateHandAuthoredEffectiveSchemaSet();
        _context = new RelationalModelSetBuilderContext(
            effectiveSchemaSet,
            SqlDialect.Pgsql,
            new PgsqlDialectRules()
        );
        _schoolResource = new QualifiedResourceName("Ed-Fi", "School");
        _sectionResource = new QualifiedResourceName("Sample", "Section");
    }

    /// <summary>
    /// It should propagate descriptor paths across projects.
    /// </summary>
    [Test]
    public void It_should_propagate_descriptor_paths_across_projects()
    {
        var descriptorPaths = _context.GetDescriptorPathsForResource(_sectionResource);

        descriptorPaths.Should().ContainKey("$.schoolReference.schoolTypeDescriptor");
        descriptorPaths["$.schoolReference.schoolTypeDescriptor"]
            .DescriptorResource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"));
    }

    /// <summary>
    /// It should partition extension descriptor paths.
    /// </summary>
    [Test]
    public void It_should_partition_extension_descriptor_paths()
    {
        var basePaths = _context.GetDescriptorPathsForResource(_schoolResource);
        var extensionPaths = _context.GetExtensionDescriptorPathsForResource(_schoolResource);

        basePaths.Should().NotContainKey("$._ext.sample.extensionDescriptor");
        extensionPaths.Should().ContainKey("$._ext.sample.extensionDescriptor");
    }
}

/// <summary>
/// Test fixture for descriptor path inference with reordered resource schemas and document paths mapping.
/// </summary>
[TestFixture]
public class Given_DescriptorPathInference_With_Reordered_ResourceSchemas_And_DocumentPathsMapping
{
    private IReadOnlyDictionary<string, DescriptorPathInfo> _ordered = default!;
    private IReadOnlyDictionary<string, DescriptorPathInfo> _reordered = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var orderedSchemaSet = CreateEffectiveSchemaSet(
            reverseResourceOrder: false,
            reverseMappingOrder: false
        );
        var reorderedSchemaSet = CreateEffectiveSchemaSet(
            reverseResourceOrder: true,
            reverseMappingOrder: true
        );

        var orderedContext = new RelationalModelSetBuilderContext(
            orderedSchemaSet,
            SqlDialect.Pgsql,
            new PgsqlDialectRules()
        );
        var reorderedContext = new RelationalModelSetBuilderContext(
            reorderedSchemaSet,
            SqlDialect.Pgsql,
            new PgsqlDialectRules()
        );

        var sectionResource = new QualifiedResourceName("Ed-Fi", "Section");
        _ordered = orderedContext.GetDescriptorPathsForResource(sectionResource);
        _reordered = reorderedContext.GetDescriptorPathsForResource(sectionResource);
    }

    /// <summary>
    /// It should build the same descriptor paths.
    /// </summary>
    [Test]
    public void It_should_build_the_same_descriptor_paths()
    {
        _ordered.Should().BeEquivalentTo(_reordered);
    }

    /// <summary>
    /// Create effective schema set.
    /// </summary>
    private static EffectiveSchemaSet CreateEffectiveSchemaSet(
        bool reverseResourceOrder,
        bool reverseMappingOrder
    )
    {
        var projectSchema = CreateProjectSchema(reverseResourceOrder, reverseMappingOrder);

        var resourceKeys = new[]
        {
            EffectiveSchemaFixture.CreateResourceKey(1, "Ed-Fi", "School"),
            EffectiveSchemaFixture.CreateResourceKey(2, "Ed-Fi", "GradingPeriod"),
            EffectiveSchemaFixture.CreateResourceKey(3, "Ed-Fi", "Section"),
            EffectiveSchemaFixture.CreateResourceKey(4, "Ed-Fi", "SchoolTypeDescriptor"),
            EffectiveSchemaFixture.CreateResourceKey(5, "Ed-Fi", "PeriodDescriptor"),
        };

        return EffectiveSchemaFixture.CreateEffectiveSchemaSet(projectSchema, resourceKeys);
    }

    /// <summary>
    /// Create project schema.
    /// </summary>
    private static JsonObject CreateProjectSchema(bool reverseResourceOrder, bool reverseMappingOrder)
    {
        var schoolSchema = new JsonObject
        {
            ["resourceName"] = "School",
            ["identityJsonPaths"] = new JsonArray { "$.schoolTypeDescriptor" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["SchoolTypeDescriptor"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "SchoolTypeDescriptor",
                    ["path"] = "$.schoolTypeDescriptor",
                },
            },
        };
        var gradingPeriodSchema = new JsonObject
        {
            ["resourceName"] = "GradingPeriod",
            ["identityJsonPaths"] = new JsonArray { "$.periodDescriptor" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["PeriodDescriptor"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "PeriodDescriptor",
                    ["path"] = "$.periodDescriptor",
                },
            },
        };
        var sectionSchema = new JsonObject
        {
            ["resourceName"] = "Section",
            ["documentPathsMapping"] = CreateSectionDocumentPathsMapping(reverseMappingOrder),
        };
        var schoolTypeDescriptorSchema = new JsonObject
        {
            ["resourceName"] = "SchoolTypeDescriptor",
            ["identityJsonPaths"] = new JsonArray { "$.schoolTypeDescriptorId" },
            ["documentPathsMapping"] = new JsonObject(),
        };
        var periodDescriptorSchema = new JsonObject
        {
            ["resourceName"] = "PeriodDescriptor",
            ["identityJsonPaths"] = new JsonArray { "$.periodDescriptorId" },
            ["documentPathsMapping"] = new JsonObject(),
        };

        JsonObject resourceSchemas = new();

        if (reverseResourceOrder)
        {
            resourceSchemas["periodDescriptors"] = periodDescriptorSchema;
            resourceSchemas["schoolTypeDescriptors"] = schoolTypeDescriptorSchema;
            resourceSchemas["sections"] = sectionSchema;
            resourceSchemas["gradingPeriods"] = gradingPeriodSchema;
            resourceSchemas["schools"] = schoolSchema;
        }
        else
        {
            resourceSchemas["schools"] = schoolSchema;
            resourceSchemas["gradingPeriods"] = gradingPeriodSchema;
            resourceSchemas["sections"] = sectionSchema;
            resourceSchemas["schoolTypeDescriptors"] = schoolTypeDescriptorSchema;
            resourceSchemas["periodDescriptors"] = periodDescriptorSchema;
        }

        return new JsonObject { ["resourceSchemas"] = resourceSchemas };
    }

    /// <summary>
    /// Create section document paths mapping.
    /// </summary>
    private static JsonObject CreateSectionDocumentPathsMapping(bool reverseMappingOrder)
    {
        var schoolReference = new JsonObject
        {
            ["isReference"] = true,
            ["isDescriptor"] = false,
            ["projectName"] = "Ed-Fi",
            ["resourceName"] = "School",
            ["referenceJsonPaths"] = new JsonArray
            {
                new JsonObject
                {
                    ["identityJsonPath"] = "$.schoolTypeDescriptor",
                    ["referenceJsonPath"] = "$.schoolReference.schoolTypeDescriptor",
                },
            },
        };

        var gradingPeriodReference = new JsonObject
        {
            ["isReference"] = true,
            ["isDescriptor"] = false,
            ["projectName"] = "Ed-Fi",
            ["resourceName"] = "GradingPeriod",
            ["referenceJsonPaths"] = new JsonArray
            {
                new JsonObject
                {
                    ["identityJsonPath"] = "$.periodDescriptor",
                    ["referenceJsonPath"] = "$.gradingPeriodReference.periodDescriptor",
                },
            },
        };

        JsonObject documentPathsMapping = new();

        if (reverseMappingOrder)
        {
            documentPathsMapping["School"] = schoolReference;
            documentPathsMapping["GradingPeriod"] = gradingPeriodReference;
        }
        else
        {
            documentPathsMapping["GradingPeriod"] = gradingPeriodReference;
            documentPathsMapping["School"] = schoolReference;
        }

        return documentPathsMapping;
    }
}

/// <summary>
/// Test fixture for descriptor path inference with null project entries.
/// </summary>
[TestFixture]
public class Given_DescriptorPathInference_With_Null_Project_Entries
{
    private InvalidOperationException _exception = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = new JsonObject { ["resourceSchemas"] = new JsonObject() };
        IReadOnlyList<DescriptorPathInference.ProjectDescriptorSchema> projects =
            new DescriptorPathInference.ProjectDescriptorSchema[] { new("Ed-Fi", projectSchema), null! };

        Action act = () => DescriptorPathInference.BuildDescriptorPathsByResource(projects);
        _exception = act.Should().Throw<InvalidOperationException>().Which;
    }

    /// <summary>
    /// It should include null project entry index in diagnostics.
    /// </summary>
    [Test]
    public void It_should_include_null_project_entry_index_in_diagnostics()
    {
        _exception.Message.Should().Contain("Null entry at index 1 (0-based)");
    }
}
