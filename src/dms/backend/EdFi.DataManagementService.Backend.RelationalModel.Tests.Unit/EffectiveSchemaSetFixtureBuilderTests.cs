// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_EffectiveSchemaSetFixtureBuilder_With_Resource_Extensions
{
    private EffectiveSchemaSet _effectiveSchemaSet = null!;

    [SetUp]
    public void Setup()
    {
        var projectSchema = new JsonObject
        {
            ["projectName"] = "Sample",
            ["projectEndpointName"] = "sample",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["busRoutes"] = new JsonObject
                {
                    ["resourceName"] = "BusRoute",
                    ["isResourceExtension"] = false,
                },
                ["schools"] = new JsonObject { ["resourceName"] = "School", ["isResourceExtension"] = true },
            },
            ["abstractResources"] = new JsonObject(),
        };

        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: true
        );

        _effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
    }

    [Test]
    public void It_excludes_resource_extensions_from_resource_keys()
    {
        _effectiveSchemaSet.EffectiveSchema.ResourceKeysInIdOrder.Should().HaveCount(1);
        _effectiveSchemaSet
            .EffectiveSchema.ResourceKeysInIdOrder.Should()
            .NotContain(entry => entry.Resource == new QualifiedResourceName("Sample", "School"));
    }

    [Test]
    public void It_keeps_non_extension_resources_in_resource_keys()
    {
        _effectiveSchemaSet
            .EffectiveSchema.ResourceKeysInIdOrder.Should()
            .ContainSingle(entry => entry.Resource == new QualifiedResourceName("Sample", "BusRoute"));
    }
}
