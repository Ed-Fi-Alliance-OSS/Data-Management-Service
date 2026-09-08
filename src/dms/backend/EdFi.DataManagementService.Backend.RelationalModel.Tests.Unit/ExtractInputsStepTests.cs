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
/// Test fixture for an api schema with a descriptor mapping.
/// </summary>
[TestFixture]
public class Given_An_ApiSchema_With_A_Descriptor_Mapping
{
    private RelationalModelBuilderContext _context = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should capture the descriptor path and resource.
    /// </summary>
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

    /// <summary>
    /// It should mark the resource as non descriptor.
    /// </summary>
    [Test]
    public void It_should_mark_the_resource_as_non_descriptor()
    {
        _context.IsDescriptorResource.Should().BeFalse();
    }

    /// <summary>
    /// Create api schema root.
    /// </summary>
    private static JsonNode CreateApiSchemaRoot()
    {
        var resourceSchema = new JsonObject
        {
            ["resourceName"] = "School",
            ["isDescriptor"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["jsonSchemaForInsert"] = new JsonObject(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject
            {
                ["SchoolTypeDescriptor"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = true,
                    ["isPartOfIdentity"] = false,
                    ["isRequired"] = false,
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

/// <summary>
/// Test fixture for an api schema with a descriptor resource.
/// </summary>
[TestFixture]
public class Given_An_ApiSchema_With_A_Descriptor_Resource
{
    private RelationalModelBuilderContext _context = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
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

    /// <summary>
    /// It should mark the resource as descriptor.
    /// </summary>
    [Test]
    public void It_should_mark_the_resource_as_descriptor()
    {
        _context.IsDescriptorResource.Should().BeTrue();
    }

    /// <summary>
    /// Create api schema root.
    /// </summary>
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
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
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
