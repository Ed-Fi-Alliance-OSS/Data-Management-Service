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
public class Given_A_MultiProject_EffectiveSchemaSet_With_CrossProject_Descriptor_Propagation
{
    private RelationalModelSetBuilderContext _context = default!;
    private QualifiedResourceName _schoolResource = default!;
    private QualifiedResourceName _sectionResource = default!;

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

    [Test]
    public void It_should_propagate_descriptor_paths_across_projects()
    {
        var descriptorPaths = _context.GetDescriptorPathsForResource(_sectionResource);

        descriptorPaths.Should().ContainKey("$.schoolReference.schoolTypeDescriptor");
        descriptorPaths["$.schoolReference.schoolTypeDescriptor"]
            .DescriptorResource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"));
    }

    [Test]
    public void It_should_partition_extension_descriptor_paths()
    {
        var basePaths = _context.GetDescriptorPathsForResource(_schoolResource);
        var extensionPaths = _context.GetExtensionDescriptorPathsForResource(_schoolResource);

        basePaths.Should().NotContainKey("$._ext.sample.extensionDescriptor");
        extensionPaths.Should().ContainKey("$._ext.sample.extensionDescriptor");
    }
}

[TestFixture]
public class Given_DescriptorPathInference_With_Reordered_ResourceSchemas_And_DocumentPathsMapping
{
    private IReadOnlyDictionary<string, DescriptorPathInfo> _ordered = default!;
    private IReadOnlyDictionary<string, DescriptorPathInfo> _reordered = default!;

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

    [Test]
    public void It_should_build_the_same_descriptor_paths()
    {
        _ordered.Should().BeEquivalentTo(_reordered);
    }

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
        };

        return EffectiveSchemaFixture.CreateEffectiveSchemaSet(projectSchema, resourceKeys);
    }

    private static JsonObject CreateProjectSchema(bool reverseResourceOrder, bool reverseMappingOrder)
    {
        var schoolSchema = new JsonObject
        {
            ["resourceName"] = "School",
            ["identityJsonPaths"] = new JsonArray { "$.schoolTypeDescriptor" },
        };
        var gradingPeriodSchema = new JsonObject
        {
            ["resourceName"] = "GradingPeriod",
            ["identityJsonPaths"] = new JsonArray { "$.periodDescriptor" },
        };
        var sectionSchema = new JsonObject
        {
            ["resourceName"] = "Section",
            ["documentPathsMapping"] = CreateSectionDocumentPathsMapping(reverseMappingOrder),
        };

        JsonObject resourceSchemas = new();

        if (reverseResourceOrder)
        {
            resourceSchemas["sections"] = sectionSchema;
            resourceSchemas["gradingPeriods"] = gradingPeriodSchema;
            resourceSchemas["schools"] = schoolSchema;
        }
        else
        {
            resourceSchemas["schools"] = schoolSchema;
            resourceSchemas["gradingPeriods"] = gradingPeriodSchema;
            resourceSchemas["sections"] = sectionSchema;
        }

        return new JsonObject { ["resourceSchemas"] = resourceSchemas };
    }

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
