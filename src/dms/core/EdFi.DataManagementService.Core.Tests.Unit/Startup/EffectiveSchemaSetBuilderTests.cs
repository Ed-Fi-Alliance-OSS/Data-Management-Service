// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
public class EffectiveSchemaSetBuilderTests
{
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

    [TestFixture]
    public class Given_A_Runtime_EffectiveSchemaSetBuilder : EffectiveSchemaSetBuilderTests
    {
        private ServiceProvider _serviceProvider = null!;
        private EffectiveSchemaSetBuilder _builder = null!;
        private EffectiveSchemaSet _result = null!;

        private static ServiceProvider CreateRuntimeServiceProvider()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IEffectiveSchemaHashProvider>(
                new EffectiveSchemaHashProvider(A.Fake<ILogger<EffectiveSchemaHashProvider>>())
            );
            services.AddSingleton<IResourceKeySeedProvider>(
                new ResourceKeySeedProvider(A.Fake<ILogger<ResourceKeySeedProvider>>())
            );
            services.AddSingleton<EffectiveSchemaSetBuilder>();

            return services.BuildServiceProvider();
        }

        [SetUp]
        public void SetUp()
        {
            _serviceProvider = CreateRuntimeServiceProvider();
            _builder = _serviceProvider.GetRequiredService<EffectiveSchemaSetBuilder>();
            _result = _builder.Build(new ApiSchemaDocumentNodes(JsonNode.Parse(MinimalCoreSchemaJson)!, []));
        }

        [TearDown]
        public void TearDown()
        {
            _serviceProvider.Dispose();
        }

        [Test]
        public void It_builds_an_effective_schema_set_from_runtime_services()
        {
            _result.EffectiveSchema.EffectiveSchemaHash.Should().NotBeNullOrEmpty();
            _result.EffectiveSchema.ResourceKeyCount.Should().Be(1);
            _result
                .EffectiveSchema.ResourceKeysInIdOrder.Should()
                .ContainSingle(entry =>
                    entry.Resource == new QualifiedResourceName("Ed-Fi", "School") && entry.ResourceKeyId == 1
                );
        }

        [Test]
        public void It_does_not_reference_the_cli_assembly()
        {
            typeof(EffectiveSchemaSetBuilder)
                .Assembly.GetReferencedAssemblies()
                .Select(assemblyName => assemblyName.Name)
                .Should()
                .NotContain("EdFi.DataManagementService.SchemaTools");
        }
    }
}
