// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

/// <summary>
/// What normalization does with a schema whose resource declares a query field spelled like a query
/// parameter DMS consumes as a control parameter.
/// </summary>
/// <remarks>
/// Held separately from the general normalizer suite because these cases need schemas carrying real
/// resources and query field mappings, where that suite's fixtures deliberately carry none.
/// </remarks>
[TestFixture]
[Parallelizable]
public class ApiSchemaInputNormalizerReservedQueryParameterTests
{
    private static ApiSchemaInputNormalizer Normalizer() =>
        new(NullLogger<ApiSchemaInputNormalizer>.Instance);

    /// <summary>A resource declaring the given query fields, in the shape the loader receives one.</summary>
    private static JsonObject ResourceDeclaring(string resourceName, params string[] queryFieldNames)
    {
        JsonObject queryFieldMapping = [];

        foreach (string queryFieldName in queryFieldNames)
        {
            queryFieldMapping[queryFieldName] = new JsonArray(
                new JsonObject { ["path"] = $"$.{queryFieldName}", ["type"] = "string" }
            );
        }

        return new JsonObject { ["resourceName"] = resourceName, ["queryFieldMapping"] = queryFieldMapping };
    }

    private static JsonNode Schema(
        string projectEndpointName,
        bool isExtensionProject,
        params (string EndpointName, JsonObject Resource)[] resources
    )
    {
        JsonObject resourceSchemas = [];

        foreach ((string endpointName, JsonObject resource) in resources)
        {
            resourceSchemas[endpointName] = resource;
        }

        return new JsonObject
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = new JsonObject
            {
                ["projectName"] = projectEndpointName,
                ["projectVersion"] = "1.0.0",
                ["projectEndpointName"] = projectEndpointName,
                ["isExtensionProject"] = isExtensionProject,
                ["resourceSchemas"] = resourceSchemas,
                ["abstractResources"] = new JsonObject(),
            },
        };
    }

    private static JsonNode CoreSchema(params (string EndpointName, JsonObject Resource)[] resources) =>
        Schema("ed-fi", isExtensionProject: false, resources);

    private static JsonNode ExtensionSchema(
        string projectEndpointName,
        params (string EndpointName, JsonObject Resource)[] resources
    ) => Schema(projectEndpointName, isExtensionProject: true, resources);

    private static ApiSchemaNormalizationResult.ReservedQueryParameterCollisionResult AsCollisionResult(
        ApiSchemaNormalizationResult result
    ) =>
        result
            .Should()
            .BeOfType<ApiSchemaNormalizationResult.ReservedQueryParameterCollisionResult>()
            .Subject;

    [TestFixture]
    [Parallelizable]
    public class Given_Extension_With_Reserved_Query_Field
        : ApiSchemaInputNormalizerReservedQueryParameterTests
    {
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = Normalizer()
                .Normalize(
                    new ApiSchemaDocumentNodes(
                        CoreSchema(),
                        [
                            ExtensionSchema(
                                "tpdm",
                                (
                                    "customResources",
                                    ResourceDeclaring("CustomResource", "schoolId", "pageSize")
                                )
                            ),
                        ]
                    )
                );
        }

        [Test]
        public void It_returns_a_reserved_query_parameter_collision_result()
        {
            AsCollisionResult(_result).Collisions.Should().ContainSingle();
        }

        [Test]
        public void It_names_the_schema_the_project_the_resource_and_the_field()
        {
            var collision = AsCollisionResult(_result).Collisions[0];

            collision.SchemaSource.Should().Be("extension[0]");
            collision.ProjectEndpointName.Should().Be("tpdm");
            collision.ResourceEndpointName.Should().Be("customResources");
            collision.QueryFieldName.Should().Be("pageSize");
        }

        [Test]
        public void It_leaves_the_ordinary_query_field_alone()
        {
            AsCollisionResult(_result)
                .Collisions.Select(collision => collision.QueryFieldName)
                .Should()
                .NotContain("schoolId");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Core_With_Reserved_Query_Field : ApiSchemaInputNormalizerReservedQueryParameterTests
    {
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = Normalizer()
                .Normalize(
                    new ApiSchemaDocumentNodes(
                        CoreSchema(("students", ResourceDeclaring("Student", "minChangeVersion"))),
                        []
                    )
                );
        }

        [Test]
        public void It_refuses_the_core_schema_too()
        {
            AsCollisionResult(_result)
                .Collisions.Should()
                .ContainSingle("what DMS can serve is 'no schema may', not 'extensions may not'");
        }

        [Test]
        public void It_names_core_as_the_schema_source()
        {
            AsCollisionResult(_result).Collisions[0].SchemaSource.Should().Be("core");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Collisions_In_Core_And_Several_Extensions
        : ApiSchemaInputNormalizerReservedQueryParameterTests
    {
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = Normalizer()
                .Normalize(
                    new ApiSchemaDocumentNodes(
                        CoreSchema(("students", ResourceDeclaring("Student", "limit"))),
                        [
                            ExtensionSchema("tpdm", ("candidates", ResourceDeclaring("Candidate", "number"))),
                            ExtensionSchema(
                                "sample",
                                ("buses", ResourceDeclaring("Bus", "maxChangeVersion"))
                            ),
                        ]
                    )
                );
        }

        [Test]
        public void It_reports_every_collision_rather_than_the_first()
        {
            AsCollisionResult(_result).Collisions.Should().HaveCount(3);
        }

        /// <summary>
        /// The report reads in the order the operator listed the schemas, not the order normalization
        /// later sorts them into. The sort below this check exists for hashing determinism.
        /// </summary>
        [Test]
        public void It_reports_them_in_load_order_with_core_first()
        {
            string[] expected = ["core", "extension[0]", "extension[1]"];

            AsCollisionResult(_result)
                .Collisions.Select(collision => collision.SchemaSource)
                .Should()
                .Equal(expected);
        }

        [Test]
        public void It_describes_every_collision()
        {
            string description = AsCollisionResult(_result).Describe();

            description.Should().Contain("'limit'").And.Contain("'number'").And.Contain("'maxChangeVersion'");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Reserved_Query_Field_And_Endpoint_Collision
        : ApiSchemaInputNormalizerReservedQueryParameterTests
    {
        [Test]
        public void It_reports_the_endpoint_name_collision_first()
        {
            ApiSchemaNormalizationResult result = Normalizer()
                .Normalize(
                    new ApiSchemaDocumentNodes(
                        CoreSchema(),
                        [
                            ExtensionSchema(
                                "tpdm",
                                ("candidates", ResourceDeclaring("Candidate", "pageToken"))
                            ),
                            ExtensionSchema("tpdm"),
                        ]
                    )
                );

            result
                .Should()
                .BeOfType<ApiSchemaNormalizationResult.ProjectEndpointNameCollisionResult>(
                    "two projects answering on one endpoint name make 'which project declares this "
                        + "field' ambiguous, so naming a project in a collision message would mislead"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Schema_With_Ordinary_Query_Fields
        : ApiSchemaInputNormalizerReservedQueryParameterTests
    {
        private ApiSchemaDocumentNodes _inputNodes = null!;
        private ApiSchemaNormalizationResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _inputNodes = new ApiSchemaDocumentNodes(
                CoreSchema(
                    (
                        "students",
                        ResourceDeclaring("Student", "schoolId", "numberOfPartitions", "id", "limits")
                    )
                ),
                [
                    ExtensionSchema(
                        "tpdm",
                        ("candidates", ResourceDeclaring("Candidate", "candidateIdentifier", "minChange"))
                    ),
                ]
            );

            _result = Normalizer().Normalize(_inputNodes);
        }

        [Test]
        public void It_returns_success()
        {
            _result.Should().BeOfType<ApiSchemaNormalizationResult.SuccessResult>();
        }

        [Test]
        public void It_normalizes_the_core_schema_to_exactly_its_input()
        {
            var success = (ApiSchemaNormalizationResult.SuccessResult)_result;

            success
                .NormalizedNodes.CoreApiSchemaRootNode.ToJsonString()
                .Should()
                .Be(
                    _inputNodes.CoreApiSchemaRootNode.ToJsonString(),
                    "detection reads the schema and must not rewrite a byte of it, or every "
                        + "EffectiveSchemaHash would move and force a reprovision of every database"
                );
        }

        [Test]
        public void It_normalizes_the_extension_schema_to_exactly_its_input()
        {
            var success = (ApiSchemaNormalizationResult.SuccessResult)_result;

            success
                .NormalizedNodes.ExtensionApiSchemaRootNodes[0]
                .ToJsonString()
                .Should()
                .Be(_inputNodes.ExtensionApiSchemaRootNodes[0].ToJsonString());
        }
    }
}
