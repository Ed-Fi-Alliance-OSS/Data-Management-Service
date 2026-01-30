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
public class Given_An_ApiSchema_With_A_Descriptor_Mapping
{
    private RelationalModelBuilderContext _context = default!;

    [SetUp]
    public void Setup()
    {
        var apiSchemaRoot = CreateApiSchemaRoot();
        _context = new RelationalModelBuilderContext
        {
            ApiSchemaRoot = apiSchemaRoot,
            ResourceEndpointName = "schools",
        };

        var step = new ExtractInputsStep();

        step.Execute(_context);
    }

    [Test]
    public void It_should_capture_the_descriptor_path_and_resource()
    {
        _context.DescriptorPathsByJsonPath.Should().HaveCount(1);
        _context
            .DescriptorPathsByJsonPath.Should()
            .ContainKey("$.schoolTypeDescriptor")
            .WhoseValue.DescriptorResource.Should()
            .Be(new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor"));
    }

    [Test]
    public void It_should_mark_the_resource_as_non_descriptor()
    {
        _context.IsDescriptorResource.Should().BeFalse();
    }

    private static JsonNode CreateApiSchemaRoot()
    {
        var resourceSchema = new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["jsonSchemaForInsert"] = new JsonObject(),
            ["identityJsonPaths"] = new JsonArray(),
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

        var projectSchema = new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectVersion"] = "5.0.0",
            ["projectEndpointName"] = "ed-fi",
            ["resourceSchemas"] = new JsonObject { ["schools"] = resourceSchema },
        };

        return new JsonObject { ["apiSchemaVersion"] = "1.0.0", ["projectSchema"] = projectSchema };
    }
}

[TestFixture]
public class Given_An_ApiSchema_With_A_Descriptor_Resource
{
    private RelationalModelBuilderContext _context = default!;

    [SetUp]
    public void Setup()
    {
        var apiSchemaRoot = CreateApiSchemaRoot(
            "academicSubjectDescriptors",
            "AcademicSubjectDescriptor",
            true
        );
        _context = new RelationalModelBuilderContext
        {
            ApiSchemaRoot = apiSchemaRoot,
            ResourceEndpointName = "academicSubjectDescriptors",
        };

        var step = new ExtractInputsStep();

        step.Execute(_context);
    }

    [Test]
    public void It_should_mark_the_resource_as_descriptor()
    {
        _context.IsDescriptorResource.Should().BeTrue();
    }

    private static JsonNode CreateApiSchemaRoot(
        string resourceEndpointName,
        string resourceName,
        bool isDescriptor
    )
    {
        var resourceSchema = new JsonObject
        {
            ["resourceName"] = resourceName,
            ["isDescriptor"] = isDescriptor,
            ["jsonSchemaForInsert"] = new JsonObject(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
        };

        var projectSchema = new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectVersion"] = "5.0.0",
            ["projectEndpointName"] = "ed-fi",
            ["resourceSchemas"] = new JsonObject { [resourceEndpointName] = resourceSchema },
        };

        return new JsonObject { ["apiSchemaVersion"] = "1.0.0", ["projectSchema"] = projectSchema };
    }
}
