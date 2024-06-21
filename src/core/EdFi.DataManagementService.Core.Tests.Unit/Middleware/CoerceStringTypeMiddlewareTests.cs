// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Extensions;
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
    public class CoerceStringTypeMiddlewareTests
    {
        public static Func<Task> Next()
        {
            return () => Task.CompletedTask;
        }

        internal static ApiSchemaDocument SchemaDocument()
        {
            var builder = new JsonSchemaBuilder();
            builder.Title("Ed-Fi.School");
            builder.Description("This entity represents an educational organization");
            builder.Schema("https://json-schema.org/draft/2020-12/schema");
            builder.AdditionalProperties(false);
            builder
                .Properties(
                    ("schoolId", new JsonSchemaBuilder().Type(SchemaValueType.Integer)),
                    (
                        "gradeLevels",
                        new JsonSchemaBuilder()
                            .Type(SchemaValueType.Array)
                            .Properties(
                                ("gradeLevelDescriptor", new JsonSchemaBuilder().Type(SchemaValueType.String)),
                                ("isSecondary", new JsonSchemaBuilder().Type(SchemaValueType.Boolean))
                            )
                            .Required("gradeLevelDescriptor")
                            .AdditionalProperties(false)
                    ),
                    ("nameOfInstitution", new JsonSchemaBuilder().Type(SchemaValueType.String)),
                    ("webSite", new JsonSchemaBuilder().Type(SchemaValueType.String).MinLength(5).MaxLength(10))
                )
                .Required("schoolId", "gradeLevels", "nameOfInstitution");

            return new ApiSchemaBuilder()
                .WithStartProject("Ed-Fi", "5.0.0")
                .WithStartResource("School")
                .WithJsonSchemaForInsert(builder.Build()!)
                .WithBooleanJsonPaths(new []
                {
                    "$.gradeLevels[*].isSecondary"
                })
                .WithNumericJsonPaths(new []
                {
                    "$.schoolId"
                })
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocument();
        }

        internal static IPipelineStep Middleware()
        {
            return new CoerceStringTypeMiddleware(NullLogger.Instance);
        }

        internal PipelineContext Context(FrontendRequest frontendRequest, RequestMethod method)
        {
            PipelineContext _context =
                new(frontendRequest, method)
                {
                    ApiSchemaDocument = SchemaDocument(),
                    PathComponents = new(
                        ProjectNamespace: new("ed-fi"),
                        EndpointName: new("schools"),
                        DocumentUuid: No.DocumentUuid
                    )
                };
            _context.ProjectSchema = new ProjectSchema(
                _context.ApiSchemaDocument.FindProjectSchemaNode(new("ed-fi")) ?? new JsonObject(),
                NullLogger.Instance
            );
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNode(new("schools")) ?? new JsonObject()
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
        public class Given_A_Request_With_Boolean_And_Numeric_Property_As_String : CoerceStringTypeMiddlewareTests
        {
            private PipelineContext _context = No.PipelineContext();

            [SetUp]
            public async Task Setup()
            {
                string jsonData =
                    """{"schoolId": "1","gradeLevels":[{"gradeLevelDescriptor": "grade1", "isSecondary": "false"}],"nameOfInstitution":"school12"}""";

                var frontEndRequest = new FrontendRequest(
                    "ed-fi/schools",
                    Body: jsonData,
                    QueryParameters: [],
                    new TraceId("traceId")
                );
                _context = Context(frontEndRequest, RequestMethod.POST);
                await Middleware().Execute(_context, Next());
            }
            
            [Test]
            public void It_coerces_numbers()
            {
                foreach (string path in _context.ResourceSchema.NumericJsonPaths.Select(s => s.Value))
                {
                    _context.ParsedBody.SelectNodeFromPath(path, NullLogger.Instance)!.AsValue().GetValueKind().Should()
                        .Be(JsonValueKind.Number);
                }
            }

            [Test]
            public void It_coerces_booleans()
            {
                foreach (string path in _context.ResourceSchema.BooleanJsonPaths.Select(s => s.Value))
                {
                    _context.ParsedBody.SelectNodeFromPath(path, NullLogger.Instance)!.AsValue().GetValueKind().Should()
                        .BeOneOf([JsonValueKind.True, JsonValueKind.False]);
                }
            }
        }
    }
}
