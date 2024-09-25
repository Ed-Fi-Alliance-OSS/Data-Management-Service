// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Validation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware
{
    public class ValidateMatchingDocumentUuidsMiddlewareTests
    {
        public static Func<Task> Next()
        {
            return () => Task.CompletedTask;
        }

        internal static ApiSchemaDocument SchemaDocument()
        {
            return new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocument();
        }

        internal static IPipelineStep Middleware()
        {
            var immutableIdentityValidator = new MatchingDocumentUuidsValidator();
            return new ValidateMatchingDocumentUuidsMiddleware(
                NullLogger.Instance,
                immutableIdentityValidator
            );
        }

        internal PipelineContext Context(FrontendRequest frontendRequest, RequestMethod method)
        {
            PipelineContext _context =
                new(frontendRequest, method)
                {
                    ApiSchemaDocument = SchemaDocument(),
                    PathComponents = new(
                        ProjectNamespace: new("ed-fi"),
                        EndpointName: new("academicweeks"),
                        DocumentUuid: No.DocumentUuid
                    )
                };
            _context.ProjectSchema = new ProjectSchema(
                _context.ApiSchemaDocument.FindProjectSchemaNode(new("ed-fi")) ?? new JsonObject(),
                NullLogger.Instance
            );
            _context.ResourceSchema = new ResourceSchema(
                _context.ProjectSchema.FindResourceSchemaNode(new("academicweeks")) ?? new JsonObject()
            );
            return _context;
        }

        [TestFixture]
        public class Given_A_Matching_Id_In_Body_And_Url : ValidateMatchingDocumentUuidsMiddlewareTests
        {
            private PipelineContext _context = No.PipelineContext();
            private readonly string id = Guid.NewGuid().ToString();

            [SetUp]
            public async Task Setup()
            {
                var jsonData = $$"""
                    {
                     "id": "{{id}}",
                     "weekIdentifier": "12345",
                     "schoolReference": {
                       "schoolId": 17012391,
                       "add": {
                            "test": "test"
                       }
                     },
                     "beginDate": "2023-09-11",
                     "endDate": "2023-09-11",
                     "totalInstructionalDays": 300,
                     "additionalField": "test"
                    }
                    """;
                var frontEndRequest = new FrontendRequest(
                    $"ed-fi/academicweeks/{id}",
                    Body: jsonData,
                    QueryParameters: [],
                    new TraceId("traceId")
                );
                _context = Context(frontEndRequest, RequestMethod.PUT);
                _context.ParsedBody = JsonNode.Parse(jsonData)!;
                _context.PathComponents = _context.PathComponents with
                {
                    DocumentUuid = new DocumentUuid(new(id))
                };

                await Middleware().Execute(_context, Next());
            }

            [Test]
            public void It_provides_no_response()
            {
                _context?.FrontendResponse.Should().Be(No.FrontendResponse);
            }
        }

        [TestFixture]
        public class Given_A_Different_Id_In_Body_And_Url : ValidateMatchingDocumentUuidsMiddlewareTests
        {
            private PipelineContext _context = No.PipelineContext();
            private readonly string id = Guid.NewGuid().ToString();
            private readonly string differentId = Guid.NewGuid().ToString();

            [SetUp]
            public async Task Setup()
            {
                var jsonData = $$"""
                    {
                     "id": "{{id}}",
                     "weekIdentifier": "12345",
                     "schoolReference": {
                       "schoolId": 17012391,
                       "add": {
                            "test": "test"
                       }
                     },
                     "beginDate": "2023-09-11",
                     "endDate": "2023-09-11",
                     "totalInstructionalDays": 300,
                     "additionalField": "test"
                    }
                    """;
                var frontEndRequest = new FrontendRequest(
                    $"ed-fi/academicweeks/{differentId}",
                    Body: jsonData,
                    QueryParameters: [],
                    new TraceId("traceId")
                );
                _context = Context(frontEndRequest, RequestMethod.PUT);
                _context.ParsedBody = JsonNode.Parse(jsonData)!;
                _context.PathComponents = _context.PathComponents with
                {
                    DocumentUuid = new DocumentUuid(new(differentId))
                };

                await Middleware().Execute(_context, Next());
            }

            [Test]
            public void It_provides_status_code_400()
            {
                _context?.FrontendResponse.StatusCode.Should().Be(400);
            }

            [Test]
            public void It_returns_message_body_with_error()
            {
                _context
                    .FrontendResponse.Body?.ToJsonString()
                    .Should()
                    .Contain("Request body id must match the id in the url.");
            }
        }

        [TestFixture]
        public class Given_A_Invalid_Guid_In_Body : ValidateMatchingDocumentUuidsMiddlewareTests
        {
            private PipelineContext _context = No.PipelineContext();
            private readonly string id = Guid.NewGuid().ToString();

            [SetUp]
            public async Task Setup()
            {
                var jsonData = $$"""
                    {
                     "id": "invalid-guid",
                     "weekIdentifier": "12345",
                     "schoolReference": {
                       "schoolId": 17012391,
                       "add": {
                            "test": "test"
                       }
                     },
                     "beginDate": "2023-09-11",
                     "endDate": "2023-09-11",
                     "totalInstructionalDays": 300,
                     "additionalField": "test"
                    }
                    """;
                var frontEndRequest = new FrontendRequest(
                    $"ed-fi/academicweeks/{id}",
                    Body: jsonData,
                    QueryParameters: [],
                    new TraceId("traceId")
                );
                _context = Context(frontEndRequest, RequestMethod.PUT);
                _context.ParsedBody = JsonNode.Parse(jsonData)!;
                _context.PathComponents = _context.PathComponents with
                {
                    DocumentUuid = new DocumentUuid(new(id))
                };

                await Middleware().Execute(_context, Next());
            }

            [Test]
            public void It_provides_error_response()
            {
                _context?.FrontendResponse.StatusCode.Should().Be(400);
            }

            [Test]
            public void It_returns_message_body_with_error()
            {
                _context
                    .FrontendResponse.Body?.ToJsonString()
                    .Should()
                    .Contain("Request body id must match the id in the url.");
            }
        }

        [TestFixture]
        public class Given_An_Empty_Id_In_Body : ValidateMatchingDocumentUuidsMiddlewareTests
        {
            private PipelineContext _context = No.PipelineContext();
            private readonly string id = Guid.NewGuid().ToString();

            [SetUp]
            public async Task Setup()
            {
                var jsonData = $$"""
                    {
                     "id": "",
                     "weekIdentifier": "12345",
                     "schoolReference": {
                       "schoolId": 17012391,
                       "add": {
                            "test": "test"
                       }
                     },
                     "beginDate": "2023-09-11",
                     "endDate": "2023-09-11",
                     "totalInstructionalDays": 300,
                     "additionalField": "test"
                    }
                    """;
                var frontEndRequest = new FrontendRequest(
                    $"ed-fi/academicweeks/{id}",
                    Body: jsonData,
                    QueryParameters: [],
                    new TraceId("traceId")
                );
                _context = Context(frontEndRequest, RequestMethod.PUT);
                _context.ParsedBody = JsonNode.Parse(jsonData)!;
                _context.PathComponents = _context.PathComponents with
                {
                    DocumentUuid = new DocumentUuid(new(id))
                };

                await Middleware().Execute(_context, Next());
            }

            [Test]
            public void It_provides_error_response()
            {
                _context?.FrontendResponse.StatusCode.Should().Be(400);
            }

            [Test]
            public void It_returns_message_body_with_error()
            {
                _context
                    .FrontendResponse.Body?.ToJsonString()
                    .Should()
                    .Contain("Request body id must match the id in the url.");
            }
        }
    }
}
