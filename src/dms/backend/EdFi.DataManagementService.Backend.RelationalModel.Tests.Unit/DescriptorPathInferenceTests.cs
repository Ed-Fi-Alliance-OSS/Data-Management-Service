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
    private QualifiedResourceName _studentResource = default!;

    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = CreateEffectiveSchemaSet();
        _context = new RelationalModelSetBuilderContext(
            effectiveSchemaSet,
            SqlDialect.Pgsql,
            new PgsqlDialectRules()
        );
        _studentResource = new QualifiedResourceName("Sample", "Student");
    }

    [Test]
    public void It_should_propagate_descriptor_paths_across_projects()
    {
        var descriptorPaths = _context.GetDescriptorPathsForResource(_studentResource);

        descriptorPaths.Should().ContainKey("$.schoolReference.schoolTypeDescriptor");
        descriptorPaths["$.schoolReference.schoolTypeDescriptor"]
            .DescriptorResource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"));
    }

    [Test]
    public void It_should_partition_extension_descriptor_paths()
    {
        var basePaths = _context.GetDescriptorPathsForResource(_studentResource);
        var extensionPaths = _context.GetExtensionDescriptorPathsForResource(_studentResource);

        basePaths.Should().NotContainKey("$._ext.sample.customDescriptor");
        extensionPaths.Should().ContainKey("$._ext.sample.customDescriptor");
    }

    private static EffectiveSchemaSet CreateEffectiveSchemaSet()
    {
        var edFiProjectSchema = new JsonObject
        {
            ["resourceSchemas"] = new JsonObject
            {
                ["schools"] = new JsonObject
                {
                    ["resourceName"] = "School",
                    ["isDescriptor"] = false,
                    ["jsonSchemaForInsert"] = new JsonObject(),
                    ["identityJsonPaths"] = new JsonArray("$.schoolTypeDescriptor"),
                },
                ["schoolTypeDescriptors"] = new JsonObject
                {
                    ["resourceName"] = "SchoolTypeDescriptor",
                    ["isDescriptor"] = true,
                    ["jsonSchemaForInsert"] = new JsonObject(),
                    ["identityJsonPaths"] = new JsonArray(),
                },
            },
        };

        var sampleProjectSchema = new JsonObject
        {
            ["resourceSchemas"] = new JsonObject
            {
                ["students"] = new JsonObject
                {
                    ["resourceName"] = "Student",
                    ["isDescriptor"] = false,
                    ["jsonSchemaForInsert"] = new JsonObject(),
                    ["identityJsonPaths"] = new JsonArray(),
                    ["documentPathsMapping"] = new JsonObject
                    {
                        ["schoolReference"] = new JsonObject
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
                        },
                        ["customDescriptor"] = new JsonObject
                        {
                            ["isReference"] = true,
                            ["isDescriptor"] = true,
                            ["projectName"] = "Sample",
                            ["resourceName"] = "CustomDescriptor",
                            ["path"] = "$._ext.sample.customDescriptor",
                        },
                    },
                },
                ["customDescriptors"] = new JsonObject
                {
                    ["resourceName"] = "CustomDescriptor",
                    ["isDescriptor"] = true,
                    ["jsonSchemaForInsert"] = new JsonObject(),
                    ["identityJsonPaths"] = new JsonArray(),
                },
            },
        };

        var schemaComponents = new[]
        {
            new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false),
            new SchemaComponentInfo("sample", "Sample", "1.0.0", true),
        };

        var resourceKeys = new[]
        {
            new ResourceKeyEntry(1, new QualifiedResourceName("Ed-Fi", "School"), "1.0.0", false),
            new ResourceKeyEntry(
                2,
                new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"),
                "1.0.0",
                false
            ),
            new ResourceKeyEntry(3, new QualifiedResourceName("Sample", "Student"), "1.0.0", false),
            new ResourceKeyEntry(4, new QualifiedResourceName("Sample", "CustomDescriptor"), "1.0.0", false),
        };

        var effectiveSchemaInfo = new EffectiveSchemaInfo(
            "1.0.0",
            "1.0.0",
            "deadbeef",
            resourceKeys.Length,
            new byte[] { 0x01 },
            schemaComponents,
            resourceKeys
        );

        var projects = new[]
        {
            new EffectiveProjectSchema("ed-fi", "Ed-Fi", "5.0.0", false, edFiProjectSchema),
            new EffectiveProjectSchema("sample", "Sample", "1.0.0", true, sampleProjectSchema),
        };

        return new EffectiveSchemaSet(effectiveSchemaInfo, projects);
    }
}
