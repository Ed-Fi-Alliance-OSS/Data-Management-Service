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
public class Given_Set_Level_Canonicalization_When_Reference_Mappings_Are_Out_Of_Order
{
    private IReadOnlyList<string> _referencePaths = default!;
    private IReadOnlyList<string> _descriptorPaths = default!;

    [SetUp]
    public void Setup()
    {
        var projectSchema = CanonicalizeOrderingSetPassSchemaBuilder.BuildProjectSchema();
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });
        IRelationalModelSetPass[] passes =
        [
            new BaseTraversalAndDescriptorBindingRelationalModelSetPass(),
            new ReferenceBindingRelationalModelSetPass(),
            new CanonicalizeOrderingRelationalModelSetPass(),
        ];

        var builder = new DerivedRelationalModelSetBuilder(passes);
        var result = builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        var studentModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == "Student"
            )
            .RelationalModel;

        _referencePaths = studentModel
            .DocumentReferenceBindings.Select(binding => binding.ReferenceObjectPath.Canonical)
            .ToArray();
        _descriptorPaths = studentModel
            .DescriptorEdgeSources.Select(edge => edge.DescriptorValuePath.Canonical)
            .ToArray();
    }

    [Test]
    public void It_should_canonicalize_document_reference_bindings_by_reference_object_path()
    {
        _referencePaths.Should().Equal("$.aReference", "$.zReference");
    }

    [Test]
    public void It_should_canonicalize_descriptor_edges_by_descriptor_value_path()
    {
        _descriptorPaths.Should().Equal("$.aReference.betaDescriptor", "$.zReference.alphaDescriptor");
    }
}

internal static class CanonicalizeOrderingSetPassSchemaBuilder
{
    internal static JsonObject BuildProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["students"] = BuildStudentSchema(),
                ["alphas"] = BuildAlphaSchema(),
                ["betas"] = BuildBetaSchema(),
                ["alphaDescriptors"] = BuildAlphaDescriptorSchema(),
                ["betaDescriptors"] = BuildBetaDescriptorSchema(),
            },
        };
    }

    private static JsonObject BuildStudentSchema()
    {
        var jsonSchemaForInsert = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["aReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["betaDescriptor"] = new JsonObject { ["type"] = "string", ["maxLength"] = 30 },
                    },
                },
                ["zReference"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["alphaDescriptor"] = new JsonObject { ["type"] = "string", ["maxLength"] = 30 },
                    },
                },
            },
        };

        return new JsonObject
        {
            ["resourceName"] = "Student",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject
            {
                ["AlphaMapping"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = false,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Alpha",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.alphaDescriptor",
                            ["referenceJsonPath"] = "$.zReference.alphaDescriptor",
                        },
                    },
                },
                ["ZuluMapping"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = false,
                    ["isRequired"] = false,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "Beta",
                    ["referenceJsonPaths"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["identityJsonPath"] = "$.betaDescriptor",
                            ["referenceJsonPath"] = "$.aReference.betaDescriptor",
                        },
                    },
                },
            },
            ["jsonSchemaForInsert"] = jsonSchemaForInsert,
        };
    }

    private static JsonObject BuildAlphaSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "Alpha",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.alphaDescriptor" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["AlphaDescriptor"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = true,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "AlphaDescriptor",
                    ["path"] = "$.alphaDescriptor",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["alphaDescriptor"] = new JsonObject { ["type"] = "string", ["maxLength"] = 30 },
                },
                ["required"] = new JsonArray("alphaDescriptor"),
            },
        };
    }

    private static JsonObject BuildBetaSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "Beta",
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray { "$.betaDescriptor" },
            ["documentPathsMapping"] = new JsonObject
            {
                ["BetaDescriptor"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = true,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "BetaDescriptor",
                    ["path"] = "$.betaDescriptor",
                },
            },
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["betaDescriptor"] = new JsonObject { ["type"] = "string", ["maxLength"] = 30 },
                },
                ["required"] = new JsonArray("betaDescriptor"),
            },
        };
    }

    private static JsonObject BuildAlphaDescriptorSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "AlphaDescriptor",
            ["isDescriptor"] = true,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = BuildDescriptorInsertSchema(),
        };
    }

    private static JsonObject BuildBetaDescriptorSchema()
    {
        return new JsonObject
        {
            ["resourceName"] = "BetaDescriptor",
            ["isDescriptor"] = true,
            ["isResourceExtension"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(),
            ["documentPathsMapping"] = new JsonObject(),
            ["jsonSchemaForInsert"] = BuildDescriptorInsertSchema(),
        };
    }

    private static JsonObject BuildDescriptorInsertSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["codeValue"] = new JsonObject { ["type"] = "string", ["maxLength"] = 20 },
            },
        };
    }
}
