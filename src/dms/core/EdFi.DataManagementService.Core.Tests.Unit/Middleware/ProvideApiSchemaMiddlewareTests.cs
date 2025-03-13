// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Json.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
public class ProvideApiSchemaMiddlewareTests
{
    internal static IPipelineStep ProvideMiddleware(IApiSchemaProvider provider)
    {
        return new ProvideApiSchemaMiddleware(provider, NullLogger.Instance);
    }

    [TestFixture]
    public class Given_An_Api_Schema_Provider_Is_Injected : ParsePathMiddlewareTests
    {
        private readonly PipelineContext _context = No.PipelineContext();
        private ProjectSchema? coreProjectSchemas;

        internal class Provider : IApiSchemaProvider
        {
            public ApiSchemaNodes GetApiSchemaNodes()
            {
                var coreJsonSchemaForInsert = new JsonSchemaBuilder();
                coreJsonSchemaForInsert.Title("Ed-Fi.School");
                coreJsonSchemaForInsert.Description("This entity represents an educational organization");
                coreJsonSchemaForInsert.Schema("https://json-schema.org/draft/2020-12/schema");
                coreJsonSchemaForInsert.AdditionalProperties(false);
                coreJsonSchemaForInsert
                    .Properties(
                        ("schoolId", new JsonSchemaBuilder().Type(SchemaValueType.Integer)),
                        ("nameOfInstitution", new JsonSchemaBuilder().Type(SchemaValueType.String))
                    )
                    .Required("schoolId", "nameOfInstitution");

                var extensionJsonSchemaForInsert = new JsonSchemaBuilder()
                    .Schema("https://json-schema.org/draft/2020-12/schema")
                    .AdditionalProperties(false)
                    .Description("")
                    .Title("TPDM.School")
                    .Type(SchemaValueType.Object)
                    .Properties(
                        (
                            "_ext",
                            new JsonSchemaBuilder()
                                .AdditionalProperties(true)
                                .Description("optional extension collection")
                                .Type(SchemaValueType.Object)
                                .Properties(
                                    (
                                        "postSecondaryInstitutionId",
                                        new JsonSchemaBuilder()
                                            .Description(
                                                "The ID of the post secondary institution. It must be distinct from any other identifier assigned to educational organizations, such as a LocalEducationAgencyId, to prevent duplication."
                                            )
                                            .Type(SchemaValueType.Integer)
                                    )
                                )
                        )
                    );

                JsonNode _apiSchemaRootNode = new ApiSchemaBuilder()
                    .WithStartProject("Ed-Fi", "5.0.0")
                    .WithStartResource("School")
                    .WithJsonSchemaForInsert(coreJsonSchemaForInsert.Build())
                    .WithBooleanJsonPaths(new[] { "$.gradeLevels[*].isSecondary" })
                    .WithNumericJsonPaths(new[] { "$.schoolId" })
                    .WithDateTimeJsonPaths(new[] { "$.beginDate" })
                    .WithEndResource()
                    .WithEndProject()
                    .AsSingleApiSchemaRootNode();

                JsonNode _extensionApiSchemaRootNode = new ApiSchemaBuilder()
                    .WithStartProject("tpdm", "5.0.0")
                    .WithStartResource("School", isResourceExtension: true)
                    .WithJsonSchemaForInsert(extensionJsonSchemaForInsert.Build())
                    .WithBooleanJsonPaths(new[] { "$.SurveyLevels[*].isSecondary" })
                    .WithNumericJsonPaths(new[] { "$.studentId" })
                    .WithDateTimeJsonPaths(new[] { "$.endDate" })
                    .WithEndResource()
                    .WithEndProject()
                    .AsSingleApiSchemaRootNode();

                return new ApiSchemaNodes(_apiSchemaRootNode, new[] { _extensionApiSchemaRootNode });
            }
        }

        [SetUp]
        public async Task Setup()
        {
            var provider = new Provider();
            var apiSchemaNodes = provider.GetApiSchemaNodes();

            _context.ApiSchemaDocuments = new ApiSchemaDocuments(apiSchemaNodes, NullLogger.Instance);

            var middleware = ProvideMiddleware(provider);
            await middleware.Execute(_context, NullNext);
            coreProjectSchemas = _context.ApiSchemaDocuments.GetCoreProjectSchema();
        }

        [Test]
        public void It_has_the_root_node_from_the_provider()
        {
            _context.Should().NotBeNull();
            _context.ApiSchemaDocuments.Should().NotBeNull();

            _context
                .ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(new("ed-fi"))
                .Should()
                .NotBeNull();
        }

        [Test]
        public void It_updates_core_resource_schemas_with_extension_type_coercion_json_paths()
        {
            coreProjectSchemas.Should().NotBeNull();

            var coreSchoolResourceSchema = coreProjectSchemas?.FindResourceSchemaNodeByResourceName(
                new("School")
            );

            var dateTimeJsonPaths = coreSchoolResourceSchema?["dateTimeJsonPaths"] as JsonArray;
            var booleanJsonPaths = coreSchoolResourceSchema?["booleanJsonPaths"] as JsonArray;
            var numericJsonPaths = coreSchoolResourceSchema?["numericJsonPaths"] as JsonArray;

            dateTimeJsonPaths.Should().NotBeNull();
            booleanJsonPaths.Should().NotBeNull();
            numericJsonPaths.Should().NotBeNull();

            var dateTimePaths = dateTimeJsonPaths?.Select(p => p?.ToString()).ToList();
            var booleanPaths = booleanJsonPaths?.Select(p => p?.ToString()).ToList();
            var numericPaths = numericJsonPaths?.Select(p => p?.ToString()).ToList();

            dateTimePaths.Should().Contain("$.endDate");
            booleanPaths.Should().Contain("$.SurveyLevels[*].isSecondary");
            numericPaths.Should().Contain("$.studentId");
        }

        [Test]
        public void It_updates_core_resource_schemas_with_extension_jsonschemaforinsert_properties()
        {
            coreProjectSchemas.Should().NotBeNull();

            var coreSchoolResourceSchema = coreProjectSchemas?.FindResourceSchemaNodeByResourceName(
                new("School")
            );

            JsonNode? jsonSchemaForInsertProperties = coreSchoolResourceSchema
                ?["jsonSchemaForInsert"]
                ?["properties"];
            bool containsPostSecondaryInstitutionId = false;
            if (jsonSchemaForInsertProperties is JsonObject coreProperties)
            {
                JsonObject clonedCoreProperties = coreProperties.DeepClone().AsObject();

                foreach (var property in clonedCoreProperties)
                {
                    if (property.Key.Equals("postSecondaryInstitutionId"))
                    {
                        containsPostSecondaryInstitutionId = true;
                    }
                }
            }
            containsPostSecondaryInstitutionId.Should().BeTrue();
        }
    }
}
