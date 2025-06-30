// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Pipeline;
using FakeItEasy;
using FluentAssertions;
using Json.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;
using No = EdFi.DataManagementService.Core.Model.No;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ProvideApiSchemaMiddlewareTests
{
    // SUT
    private ProvideApiSchemaMiddleware? _provideApiSchemaMiddleware;

    [TestFixture]
    [Parallelizable]
    public class Given_An_Api_Schema_With_Resource_Extensions : ProvideApiSchemaMiddlewareTests
    {
        private readonly ApiSchemaDocumentNodes _apiSchemaNodes = new ApiSchemaBuilder()
            .WithStartProject("Ed-Fi", "5.0.0")
            .WithStartResource("School")
            .WithEqualityConstraints(
                [new(new JsonPath("$.schoolReference.schoolId"), new JsonPath("$.sessionReference.schoolId"))]
            )
            .WithJsonSchemaForInsert(
                new JsonSchemaBuilder()
                    .Properties(
                        (
                            "credentialIdentifier",
                            new JsonSchemaBuilder()
                                .Description("Identifier or serial number assigned to the credential.")
                                .Type(SchemaValueType.String)
                        )
                    )
                    .Build()
            )
            .WithEndResource()
            .WithEndProject()
            .WithStartProject("tpdm", "5.0.0")
            .WithStartResource("School", isResourceExtension: true)
            .WithEqualityConstraints(
                [
                    new(
                        new JsonPath("$.evaluationObjectiveRatingReference.evaluationTitle"),
                        new JsonPath("$.evaluationElementReference.evaluationTitle")
                    ),
                ]
            )
            .WithJsonSchemaForInsert(
                new JsonSchemaBuilder()
                    .Properties(
                        new Dictionary<string, JsonSchema>
                        {
                            {
                                "_ext",
                                new JsonSchemaBuilder().Properties(
                                    new Dictionary<string, JsonSchema>
                                    {
                                        {
                                            "tpdm",
                                            new JsonSchemaBuilder().Properties(
                                                new Dictionary<string, JsonSchema>
                                                {
                                                    {
                                                        "boardCertificationIndicator",
                                                        new JsonSchemaBuilder()
                                                            .Description("Indicator that the credential")
                                                            .Type(SchemaValueType.Boolean)
                                                    },
                                                }
                                            )
                                        },
                                    }
                                )
                            },
                        }
                    )
                    .Build()
            )
            .WithBooleanJsonPaths(["$._ext.tpdm.gradeLevels[*].isSecondary"])
            .WithNumericJsonPaths(["$._ext.tpdm.schoolId"])
            .WithDateTimeJsonPaths(["$._ext.tpdm.beginDate"])
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference(
                "Person",
                [new("$._ext.tpdm.personId", "$._ext.tpdm.personReference.personId")]
            )
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .WithStartProject("sample", "1.0.0")
            .WithStartResource("School", isResourceExtension: true)
            .WithJsonSchemaForInsert(
                new JsonSchemaBuilder()
                    .Properties(
                        new Dictionary<string, JsonSchema>
                        {
                            {
                                "_ext",
                                new JsonSchemaBuilder().Properties(
                                    new Dictionary<string, JsonSchema>
                                    {
                                        {
                                            "sample",
                                            new JsonSchemaBuilder().Properties(
                                                new Dictionary<string, JsonSchema>
                                                {
                                                    {
                                                        "directlyOwnedBuses",
                                                        new JsonSchemaBuilder().Items(
                                                            new JsonSchemaBuilder().Properties(
                                                                new Dictionary<string, JsonSchema>
                                                                {
                                                                    {
                                                                        "directlyOwnedBusReference",
                                                                        new JsonSchemaBuilder().Properties(
                                                                            new Dictionary<string, JsonSchema>
                                                                            {
                                                                                {
                                                                                    "busId",
                                                                                    new JsonSchemaBuilder()
                                                                                        .Description(
                                                                                            "The unique identifier for the bus"
                                                                                        )
                                                                                        .Type(
                                                                                            SchemaValueType.Boolean
                                                                                        )
                                                                                },
                                                                            }
                                                                        )
                                                                    },
                                                                }
                                                            )
                                                        )
                                                    },
                                                }
                                            )
                                        },
                                    }
                                )
                            },
                        }
                    )
                    .Build()
            )
            .WithBooleanJsonPaths(
                ["$._ext.sample.cteProgramService.primaryIndicator", "$._ext.sample.isExemplary"]
            )
            .WithStartDocumentPathsMapping()
            .WithDocumentPathReference(
                "DirectlyOwnedBus",
                [new("$.busId", "$._ext.sample.directlyOwnedBuses[*].directlyOwnedBusReference.busId")]
            )
            .WithEndDocumentPathsMapping()
            .WithEndResource()
            .WithEndProject()
            .AsApiSchemaNodes();

        [SetUp]
        public void Setup()
        {
            var fakeApiSchemaProvider = A.Fake<IApiSchemaProvider>();
            A.CallTo(() => fakeApiSchemaProvider.GetApiSchemaNodes()).Returns(_apiSchemaNodes);

            _provideApiSchemaMiddleware = new ProvideApiSchemaMiddleware(
                fakeApiSchemaProvider,
                NullLogger<ProvideApiSchemaMiddleware>.Instance
            );
        }

        [Test]
        public async Task Copies_paths_to_core()
        {
            // Act
            var fakeRequestData = A.Fake<RequestData>();
            await _provideApiSchemaMiddleware!.Execute(fakeRequestData, NullNext);

            // Assert
            fakeRequestData.ApiSchemaDocuments.Should().NotBeNull();

            var coreSchoolResource = fakeRequestData
                .ApiSchemaDocuments.GetCoreProjectSchema()
                .FindResourceSchemaNodeByResourceName(new ResourceName("School"));

            var booleanJsonPaths = coreSchoolResource!
                .GetRequiredNode("booleanJsonPaths")
                .AsArray()
                .Select(node => node!.GetValue<string>());
            booleanJsonPaths.Should().NotBeNull();
            booleanJsonPaths.Should().Contain("$._ext.tpdm.gradeLevels[*].isSecondary");
            booleanJsonPaths.Should().Contain("$._ext.sample.cteProgramService.primaryIndicator");
            booleanJsonPaths.Should().Contain("$._ext.sample.isExemplary");

            coreSchoolResource!
                .GetRequiredNode("numericJsonPaths")
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .ContainSingle("$._ext.tpdm.schoolId");

            coreSchoolResource!
                .GetRequiredNode("dateTimeJsonPaths")
                .AsArray()
                .Select(node => node!.GetValue<string>())
                .Should()
                .ContainSingle("$._ext.tpdm.beginDate");

            coreSchoolResource!
                .GetRequiredNode("documentPathsMapping")
                .AsObject()
                .GetRequiredNode("Person")
                .GetRequiredNode("referenceJsonPaths")[0]!
                .GetRequiredNode("referenceJsonPath")
                .GetValue<string>()
                .Should()
                .Be("$._ext.tpdm.personReference.personId");

            coreSchoolResource!
                .GetRequiredNode("documentPathsMapping")
                .AsObject()
                .GetRequiredNode("DirectlyOwnedBus")
                .GetRequiredNode("referenceJsonPaths")[0]!
                .GetRequiredNode("referenceJsonPath")
                .GetValue<string>()
                .Should()
                .Be("$._ext.sample.directlyOwnedBuses[*].directlyOwnedBusReference.busId");

            // check tpdm extension
            coreSchoolResource!
                .GetRequiredNode("jsonSchemaForInsert")
                .GetRequiredNode("properties")
                .GetRequiredNode("_ext")
                .GetRequiredNode("properties")
                .GetRequiredNode("tpdm")
                .GetRequiredNode("properties")
                .GetRequiredNode("boardCertificationIndicator")
                .GetRequiredNode("description")
                .GetValue<string>()
                .Should()
                .Be("Indicator that the credential");

            // check sample extension
            coreSchoolResource!
                .GetRequiredNode("jsonSchemaForInsert")
                .GetRequiredNode("properties")
                .GetRequiredNode("_ext")
                .GetRequiredNode("properties")
                .GetRequiredNode("sample")
                .GetRequiredNode("properties")
                .GetRequiredNode("directlyOwnedBuses")
                .GetRequiredNode("items")
                .GetRequiredNode("properties")
                .GetRequiredNode("directlyOwnedBusReference")
                .GetRequiredNode("properties")
                .GetRequiredNode("busId")
                .GetRequiredNode("description")
                .GetValue<string>()
                .Should()
                .Be("The unique identifier for the bus");

            coreSchoolResource!
                .GetRequiredNode("equalityConstraints")
                .AsArray()
                .Select(node => node!.GetRequiredNode("sourceJsonPath").GetValue<string>())
                .Should()
                .Contain("$.evaluationObjectiveRatingReference.evaluationTitle");

            coreSchoolResource!
                .GetRequiredNode("equalityConstraints")
                .AsArray()
                .Select(node => node!.GetRequiredNode("targetJsonPath").GetValue<string>())
                .Should()
                .Contain("$.evaluationElementReference.evaluationTitle");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class HotReloadScenarios : ProvideApiSchemaMiddlewareTests
    {
        private IApiSchemaProvider _mockProvider = null!;
        private ProvideApiSchemaMiddleware _middleware = null!;

        [SetUp]
        public void Setup()
        {
            _mockProvider = A.Fake<IApiSchemaProvider>();
            _middleware = new ProvideApiSchemaMiddleware(
                _mockProvider,
                NullLogger<ProvideApiSchemaMiddleware>.Instance
            );
        }

        [Test]
        public async Task Process_AfterSchemaReload_ProvidesNewSchema()
        {
            // Arrange
            var requestData = No.RequestData();
            var initialVersion = Guid.NewGuid();
            var newVersion = Guid.NewGuid();

            var initialSchema = new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithStartResource("InitialResource")
                .WithIdentityJsonPaths(["$.id"])
                .WithJsonSchemaForInsert(new JsonSchemaBuilder().Build())
                .WithEndResource()
                .WithEndProject()
                .AsApiSchemaNodes();

            var updatedSchema = new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.1.0")
                .WithStartResource("UpdatedResource")
                .WithIdentityJsonPaths(["$.id"])
                .WithJsonSchemaForInsert(new JsonSchemaBuilder().Build())
                .WithEndResource()
                .WithEndProject()
                .AsApiSchemaNodes();

            // Setup version changes
            A.CallTo(() => _mockProvider.ReloadId).ReturnsNextFromSequence(initialVersion, newVersion);

            A.CallTo(() => _mockProvider.GetApiSchemaNodes())
                .ReturnsNextFromSequence(initialSchema, updatedSchema);

            // Act
            await _middleware.Execute(requestData, NullNext);
            var firstSchemaVersion = requestData
                .ApiSchemaDocuments.GetCoreProjectSchema()
                .ResourceVersion.Value;

            // Reset requestData for second execution
            requestData = No.RequestData();
            await _middleware.Execute(requestData, NullNext);
            var secondSchemaVersion = requestData
                .ApiSchemaDocuments.GetCoreProjectSchema()
                .ResourceVersion.Value;

            // Assert
            firstSchemaVersion.Should().Be("5.0.0");
            secondSchemaVersion.Should().Be("5.1.0");

            A.CallTo(() => _mockProvider.GetApiSchemaNodes()).MustHaveHappenedTwiceExactly();
        }

        [Test]
        public async Task Process_MultipleRequestsAfterReload_ConsistentSchema()
        {
            // Arrange
            var contexts = Enumerable.Range(0, 10).Select(_ => No.RequestData()).ToList();
            var version = Guid.NewGuid();

            var schema = new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithStartResource("TestResource")
                .WithIdentityJsonPaths(["$.id"])
                .WithJsonSchemaForInsert(new JsonSchemaBuilder().Build())
                .WithEndResource()
                .WithEndProject()
                .AsApiSchemaNodes();

            A.CallTo(() => _mockProvider.ReloadId).Returns(version);
            A.CallTo(() => _mockProvider.GetApiSchemaNodes()).Returns(schema);

            // Act
            var tasks = contexts.Select(requestData => _middleware.Execute(requestData, NullNext)).ToArray();
            await Task.WhenAll(tasks);

            // Assert
            var projectVersions = contexts
                .Select(c => c.ApiSchemaDocuments.GetCoreProjectSchema().ResourceVersion.Value)
                .ToList();

            projectVersions
                .Should()
                .OnlyContain(v => v == "5.0.0", "all requests should get the same schema");

            // Should use cache - only called once despite multiple requests
            A.CallTo(() => _mockProvider.GetApiSchemaNodes()).MustHaveHappenedOnceExactly();
        }
    }
}
