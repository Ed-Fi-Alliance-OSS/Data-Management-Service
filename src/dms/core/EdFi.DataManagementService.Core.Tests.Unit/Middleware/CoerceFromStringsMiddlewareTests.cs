// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Json.Schema;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware
{
    public class CoerceFromStringsMiddlewareTests
    {
        public static Func<Task> Next()
        {
            return () => Task.CompletedTask;
        }

        internal static ApiSchemaDocuments SchemaDocuments()
        {
            var builder = new JsonSchemaBuilder();
            builder.Title("Ed-Fi.School");
            builder.Description("This entity represents an educational organization");
            builder.Schema("https://json-schema.org/draft/2020-12/schema");
            builder.AdditionalProperties(false);
            builder
                .Properties(
                    ("schoolId", new JsonSchemaBuilder().Type(SchemaValueType.Integer)),
                    ("yearsOld", new JsonSchemaBuilder().Type(SchemaValueType.Number)),
                    (
                        "gradeLevels",
                        new JsonSchemaBuilder()
                            .Type(SchemaValueType.Array)
                            .Properties(
                                (
                                    "gradeLevelDescriptor",
                                    new JsonSchemaBuilder().Type(SchemaValueType.String)
                                ),
                                ("isSecondary", new JsonSchemaBuilder().Type(SchemaValueType.Boolean))
                            )
                            .Required("gradeLevelDescriptor")
                            .AdditionalProperties(false)
                    ),
                    ("nameOfInstitution", new JsonSchemaBuilder().Type(SchemaValueType.String)),
                    (
                        "webSite",
                        new JsonSchemaBuilder().Type(SchemaValueType.String).MinLength(5).MaxLength(10)
                    )
                )
                .Required("schoolId", "gradeLevels", "nameOfInstitution");

            return new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithStartResource("School")
                .WithJsonSchemaForInsert(builder.Build()!)
                .WithDecimalPropertyValidationInfos(
                    new[] { new DecimalValidationInfo(new JsonPath("$.yearsOld"), 5, 3) }
                )
                .WithBooleanJsonPaths(new[] { "$.gradeLevels[*].isSecondary" })
                .WithNumericJsonPaths(new[] { "$.schoolId" })
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();
        }

        internal static IPipelineStep Middleware()
        {
            return new CoerceFromStringsMiddleware(NullLogger.Instance);
        }

        internal RequestData Context(FrontendRequest frontendRequest, RequestMethod method)
        {
            RequestData _context = new(frontendRequest, method)
            {
                ApiSchemaDocuments = SchemaDocuments(),
                PathComponents = new(
                    ProjectNamespace: new("ed-fi"),
                    EndpointName: new("schools"),
                    DocumentUuid: No.DocumentUuid
                ),
            };
            _context.ProjectSchema = _context.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
                new("ed-fi")
            )!;
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("schools"))
                    ?? new JsonObject()
            );

            if (_context.FrontendRequest.Body != null)
            {
                var body = JsonNode.Parse(_context.FrontendRequest.Body);
                if (body != null)
                {
                    _context.ParsedBody = body;
                }
            }

            return _context;
        }

        [TestFixture]
        [Parallelizable]
        public class Given_A_Request_With_Boolean_And_Numeric_Property_As_String
            : CoerceFromStringsMiddlewareTests
        {
            private RequestData _context = No.RequestData();

            [SetUp]
            public async Task Setup()
            {
                string jsonData =
                    """{"schoolId": "1","yearsOld": "1","gradeLevels":[{"gradeLevelDescriptor": "grade1", "isSecondary": "false"}],"nameOfInstitution":"school12"}""";

                var frontEndRequest = new FrontendRequest(
                    "ed-fi/schools",
                    Body: jsonData,
                    Headers: [],
                    QueryParameters: [],
                    new TraceId("traceId"),
                    new ClientAuthorizations(
                        TokenId: "",
                        ClaimSetName: "",
                        EducationOrganizationIds: [],
                        NamespacePrefixes: []
                    )
                );
                _context = Context(frontEndRequest, RequestMethod.POST);
                await Middleware().Execute(_context, Next());
            }

            [Test]
            public void It_coerces_numbers()
            {
                var paths = _context.ResourceSchema.NumericJsonPaths.Select(s => s.Value).ToList();
                paths.Count.Should().BeGreaterThan(0);
                foreach (string path in paths)
                {
                    _context
                        .ParsedBody.SelectNodeFromPath(path, NullLogger.Instance)!
                        .AsValue()
                        .GetValueKind()
                        .Should()
                        .Be(JsonValueKind.Number);
                }
            }

            [Test]
            public void It_coerces_decimals()
            {
                var paths = _context
                    .ResourceSchema.DecimalPropertyValidationInfos.Select(s => s.Path.Value)
                    .ToList();
                paths.Count.Should().BeGreaterThan(0);
                foreach (string path in paths)
                {
                    _context
                        .ParsedBody.SelectNodeFromPath(path, NullLogger.Instance)!
                        .AsValue()
                        .GetValueKind()
                        .Should()
                        .Be(JsonValueKind.Number);
                }
            }

            [Test]
            public void It_coerces_booleans()
            {
                var paths = _context.ResourceSchema.BooleanJsonPaths.Select(s => s.Value).ToList();
                paths.Count.Should().BeGreaterThan(0);
                foreach (string path in paths)
                {
                    _context
                        .ParsedBody.SelectNodeFromPath(path, NullLogger.Instance)!
                        .AsValue()
                        .GetValueKind()
                        .Should()
                        .BeOneOf(JsonValueKind.True, JsonValueKind.False);
                }
            }
        }
    }
}
