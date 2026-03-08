// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Utilities;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

[TestFixture]
public class EffectiveSchemaSetBuilderTests
{
    private sealed record ProjectSummary(
        string ProjectEndpointName,
        string ProjectName,
        string ProjectVersion,
        bool IsExtensionProject,
        string CanonicalProjectSchemaJson
    );

    private sealed record EffectiveSchemaSetSummary(
        string ApiSchemaFormatVersion,
        string RelationalMappingVersion,
        string EffectiveSchemaHash,
        int ResourceKeyCount,
        string ResourceKeySeedHashHex,
        IReadOnlyList<SchemaComponentInfo> SchemaComponentsInEndpointOrder,
        IReadOnlyList<ResourceKeyEntry> ResourceKeysInIdOrder,
        IReadOnlyList<ProjectSummary> ProjectsInEndpointOrder
    );

    private const string MinimalCoreSchemaJson = """
        {
          "apiSchemaVersion": "1.0.0",
          "projectSchema": {
            "projectName": "Ed-Fi",
            "projectEndpointName": "ed-fi",
            "projectVersion": "5.0.0",
            "isExtensionProject": false,
            "abstractResources": {},
            "resourceSchemas": {
              "schools": {
                "resourceName": "School",
                "isDescriptor": false,
                "isResourceExtension": false,
                "isSubclass": false,
                "allowIdentityUpdates": false,
                "arrayUniquenessConstraints": [],
                "identityJsonPaths": ["$.schoolId"],
                "documentPathsMapping": {
                  "SchoolId": {
                    "isReference": false,
                    "isPartOfIdentity": true,
                    "isRequired": true,
                    "path": "$.schoolId"
                  }
                },
                "jsonSchemaForInsert": { "type": "object" }
              }
            }
          }
        }
        """;

    private const string MinimalExtensionSchemaJson = """
        {
          "apiSchemaVersion": "1.0.0",
          "projectSchema": {
            "projectName": "Sample",
            "projectEndpointName": "sample",
            "projectVersion": "1.0.0",
            "isExtensionProject": true,
            "abstractResources": {},
            "resourceSchemas": {
              "busRoutes": {
                "resourceName": "BusRoute",
                "isDescriptor": false,
                "isResourceExtension": false,
                "isSubclass": false,
                "allowIdentityUpdates": false,
                "arrayUniquenessConstraints": [],
                "identityJsonPaths": ["$.busRouteNumber"],
                "documentPathsMapping": {
                  "BusRouteNumber": {
                    "isReference": false,
                    "isPartOfIdentity": true,
                    "isRequired": true,
                    "path": "$.busRouteNumber"
                  }
                },
                "jsonSchemaForInsert": { "type": "object" }
              }
            }
          }
        }
        """;

    private static EffectiveSchemaSetBuilder CreateBuilder() =>
        new(
            new EffectiveSchemaHashProvider(A.Fake<ILogger<EffectiveSchemaHashProvider>>()),
            new ResourceKeySeedProvider(A.Fake<ILogger<ResourceKeySeedProvider>>())
        );

    [TestFixture]
    public class Given_Core_Schema_Only : EffectiveSchemaSetBuilderTests
    {
        private EffectiveSchemaSet _result = null!;

        [SetUp]
        public void SetUp()
        {
            var coreNode = JsonNode.Parse(MinimalCoreSchemaJson)!;
            var nodes = new ApiSchemaDocumentNodes(coreNode, []);
            _result = CreateBuilder().Build(nodes);
        }

        [Test]
        public void It_produces_a_non_null_result()
        {
            _result.Should().NotBeNull();
        }

        [Test]
        public void It_has_one_project()
        {
            _result.ProjectsInEndpointOrder.Should().HaveCount(1);
        }

        [Test]
        public void It_has_correct_project_endpoint_name()
        {
            _result.ProjectsInEndpointOrder[0].ProjectEndpointName.Should().Be("ed-fi");
        }

        [Test]
        public void It_has_correct_project_name()
        {
            _result.ProjectsInEndpointOrder[0].ProjectName.Should().Be("Ed-Fi");
        }

        [Test]
        public void It_has_correct_project_version()
        {
            _result.ProjectsInEndpointOrder[0].ProjectVersion.Should().Be("5.0.0");
        }

        [Test]
        public void It_marks_project_as_not_extension()
        {
            _result.ProjectsInEndpointOrder[0].IsExtensionProject.Should().BeFalse();
        }

        [Test]
        public void It_has_non_empty_effective_schema_hash()
        {
            _result.EffectiveSchema.EffectiveSchemaHash.Should().NotBeNullOrEmpty();
        }

        [Test]
        public void It_has_correct_api_schema_format_version()
        {
            _result.EffectiveSchema.ApiSchemaFormatVersion.Should().Be("1.0.0");
        }

        [Test]
        public void It_has_one_resource_key()
        {
            _result.EffectiveSchema.ResourceKeyCount.Should().Be(1);
        }

        [Test]
        public void It_has_resource_key_for_school()
        {
            _result
                .EffectiveSchema.ResourceKeysInIdOrder.Should()
                .ContainSingle(k => k.Resource.ProjectName == "Ed-Fi" && k.Resource.ResourceName == "School");
        }

        [Test]
        public void It_assigns_resource_key_id_1()
        {
            _result.EffectiveSchema.ResourceKeysInIdOrder[0].ResourceKeyId.Should().Be(1);
        }

        [Test]
        public void It_has_one_schema_component()
        {
            _result.EffectiveSchema.SchemaComponentsInEndpointOrder.Should().HaveCount(1);
        }

        [Test]
        public void It_has_non_empty_project_hash()
        {
            _result
                .EffectiveSchema.SchemaComponentsInEndpointOrder[0]
                .ProjectHash.Should()
                .NotBeNullOrEmpty();
        }

        [Test]
        public void It_has_non_empty_resource_key_seed_hash()
        {
            _result.EffectiveSchema.ResourceKeySeedHash.Should().NotBeEmpty();
        }
    }

    [TestFixture]
    public class Given_Core_And_Extension_Schemas : EffectiveSchemaSetBuilderTests
    {
        private EffectiveSchemaSet _result = null!;

        [SetUp]
        public void SetUp()
        {
            var coreNode = JsonNode.Parse(MinimalCoreSchemaJson)!;
            var extensionNode = JsonNode.Parse(MinimalExtensionSchemaJson)!;
            var nodes = new ApiSchemaDocumentNodes(coreNode, [extensionNode]);
            _result = CreateBuilder().Build(nodes);
        }

        [Test]
        public void It_has_two_projects()
        {
            _result.ProjectsInEndpointOrder.Should().HaveCount(2);
        }

        [Test]
        public void It_orders_projects_by_endpoint_name()
        {
            _result.ProjectsInEndpointOrder.Select(p => p.ProjectEndpointName).Should().BeInAscendingOrder();
        }

        [Test]
        public void It_has_two_resource_keys()
        {
            _result.EffectiveSchema.ResourceKeyCount.Should().Be(2);
        }

        [Test]
        public void It_orders_resource_keys_by_project_then_resource_name()
        {
            var keys = _result.EffectiveSchema.ResourceKeysInIdOrder;
            keys[0].Resource.ProjectName.Should().Be("Ed-Fi");
            keys[0].Resource.ResourceName.Should().Be("School");
            keys[1].Resource.ProjectName.Should().Be("Sample");
            keys[1].Resource.ResourceName.Should().Be("BusRoute");
        }

        [Test]
        public void It_assigns_sequential_resource_key_ids()
        {
            var keys = _result.EffectiveSchema.ResourceKeysInIdOrder;
            keys[0].ResourceKeyId.Should().Be(1);
            keys[1].ResourceKeyId.Should().Be(2);
        }

        [Test]
        public void It_has_two_schema_components()
        {
            _result.EffectiveSchema.SchemaComponentsInEndpointOrder.Should().HaveCount(2);
        }

        [Test]
        public void It_orders_schema_components_by_endpoint_name()
        {
            _result
                .EffectiveSchema.SchemaComponentsInEndpointOrder.Select(c => c.ProjectEndpointName)
                .Should()
                .BeInAscendingOrder();
        }

        [Test]
        public void It_identifies_extension_project_correctly()
        {
            var sampleComponent = _result.EffectiveSchema.SchemaComponentsInEndpointOrder.Single(c =>
                c.ProjectEndpointName == "sample"
            );
            sampleComponent.IsExtensionProject.Should().BeTrue();
        }
    }

    [TestFixture]
    public class Given_Same_Input_Twice : EffectiveSchemaSetBuilderTests
    {
        private EffectiveSchemaSet _result1 = null!;
        private EffectiveSchemaSet _result2 = null!;

        [SetUp]
        public void SetUp()
        {
            var builder = CreateBuilder();

            var nodes1 = new ApiSchemaDocumentNodes(JsonNode.Parse(MinimalCoreSchemaJson)!, []);
            _result1 = builder.Build(nodes1);

            var nodes2 = new ApiSchemaDocumentNodes(JsonNode.Parse(MinimalCoreSchemaJson)!, []);
            _result2 = builder.Build(nodes2);
        }

        [Test]
        public void It_produces_identical_effective_schema_hash()
        {
            _result1
                .EffectiveSchema.EffectiveSchemaHash.Should()
                .Be(_result2.EffectiveSchema.EffectiveSchemaHash);
        }

        [Test]
        public void It_produces_identical_project_hashes()
        {
            _result1
                .EffectiveSchema.SchemaComponentsInEndpointOrder[0]
                .ProjectHash.Should()
                .Be(_result2.EffectiveSchema.SchemaComponentsInEndpointOrder[0].ProjectHash);
        }

        [Test]
        public void It_produces_identical_resource_key_seed_hashes()
        {
            _result1
                .EffectiveSchema.ResourceKeySeedHash.Should()
                .BeEquivalentTo(_result2.EffectiveSchema.ResourceKeySeedHash);
        }
    }

    [TestFixture]
    public class Given_Cli_And_Runtime_Builder_Paths : EffectiveSchemaSetBuilderTests
    {
        private EffectiveSchemaSet _cliResult = null!;
        private EffectiveSchemaSet _runtimeResult = null!;

        private static EffectiveSchemaSet BuildUsingCliRegistration(ApiSchemaDocumentNodes nodes)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IEffectiveSchemaHashProvider>(
                new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance)
            );
            services.AddSingleton<IResourceKeySeedProvider>(
                new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
            );
            services.AddSingleton<EffectiveSchemaSetBuilder>();

            using var serviceProvider = services.BuildServiceProvider();
            return serviceProvider.GetRequiredService<EffectiveSchemaSetBuilder>().Build(nodes);
        }

        private static EffectiveSchemaSetSummary Summarize(EffectiveSchemaSet schemaSet)
        {
            var effectiveSchema = schemaSet.EffectiveSchema;

            return new EffectiveSchemaSetSummary(
                effectiveSchema.ApiSchemaFormatVersion,
                effectiveSchema.RelationalMappingVersion,
                effectiveSchema.EffectiveSchemaHash,
                effectiveSchema.ResourceKeyCount,
                Convert.ToHexStringLower(effectiveSchema.ResourceKeySeedHash),
                effectiveSchema.SchemaComponentsInEndpointOrder,
                effectiveSchema.ResourceKeysInIdOrder,
                schemaSet
                    .ProjectsInEndpointOrder.Select(project => new ProjectSummary(
                        project.ProjectEndpointName,
                        project.ProjectName,
                        project.ProjectVersion,
                        project.IsExtensionProject,
                        CanonicalJsonSerializer.SerializeToString(project.ProjectSchema)
                    ))
                    .ToArray()
            );
        }

        [SetUp]
        public void SetUp()
        {
            _cliResult = BuildUsingCliRegistration(
                new ApiSchemaDocumentNodes(
                    JsonNode.Parse(MinimalCoreSchemaJson)!,
                    [JsonNode.Parse(MinimalExtensionSchemaJson)!]
                )
            );

            _runtimeResult = CreateBuilder()
                .Build(
                    new ApiSchemaDocumentNodes(
                        JsonNode.Parse(MinimalCoreSchemaJson)!,
                        [JsonNode.Parse(MinimalExtensionSchemaJson)!]
                    )
                );
        }

        [Test]
        public void It_matches_effective_schema_metadata_byte_for_byte()
        {
            Summarize(_cliResult)
                .Should()
                .BeEquivalentTo(Summarize(_runtimeResult), options => options.WithStrictOrdering());
        }
    }
}
