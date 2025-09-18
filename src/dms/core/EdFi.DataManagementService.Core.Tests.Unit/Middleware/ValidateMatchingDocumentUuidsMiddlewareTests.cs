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

        internal static ApiSchemaDocuments SchemaDocuments()
        {
            return new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();
        }

        internal static IPipelineStep Middleware()
        {
            var immutableIdentityValidator = new MatchingDocumentUuidsValidator();
            return new ValidateMatchingDocumentUuidsMiddleware(
                NullLogger.Instance,
                immutableIdentityValidator
            );
        }

        internal RequestInfo Context(FrontendRequest frontendRequest, RequestMethod method)
        {
            RequestInfo _requestInfo = new(frontendRequest, method)
            {
                ApiSchemaDocuments = SchemaDocuments(),
                PathComponents = new(
                    ProjectEndpointName: new("ed-fi"),
                    EndpointName: new("academicweeks"),
                    DocumentUuid: No.DocumentUuid
                ),
            };
            _requestInfo.ProjectSchema = _requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
                new("ed-fi")
            )!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("academicweeks"))
                    ?? new JsonObject()
            );
            return _requestInfo;
        }

        [TestFixture]
        [Parallelizable]
        public class Given_A_Matching_Id_In_Body_And_Url : ValidateMatchingDocumentUuidsMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();
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
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId")
                );
                _requestInfo = Context(frontEndRequest, RequestMethod.PUT);
                _requestInfo.ParsedBody = JsonNode.Parse(jsonData)!;
                _requestInfo.PathComponents = _requestInfo.PathComponents with
                {
                    DocumentUuid = new DocumentUuid(new(id)),
                };

                await Middleware().Execute(_requestInfo, Next());
            }

            [Test]
            public void It_provides_no_response()
            {
                _requestInfo?.FrontendResponse.Should().Be(No.FrontendResponse);
            }
        }

        [TestFixture]
        [Parallelizable]
        public class Given_A_Different_Id_In_Body_And_Url : ValidateMatchingDocumentUuidsMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();
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
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId")
                );
                _requestInfo = Context(frontEndRequest, RequestMethod.PUT);
                _requestInfo.ParsedBody = JsonNode.Parse(jsonData)!;
                _requestInfo.PathComponents = _requestInfo.PathComponents with
                {
                    DocumentUuid = new DocumentUuid(new(differentId)),
                };

                await Middleware().Execute(_requestInfo, Next());
            }

            [Test]
            public void It_provides_status_code_400()
            {
                _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
            }

            [Test]
            public void It_returns_message_body_with_error()
            {
                _requestInfo
                    .FrontendResponse.Body?.ToJsonString()
                    .Should()
                    .Contain("Request body id must match the id in the url.");
            }
        }

        [TestFixture]
        [Parallelizable]
        public class Given_A_Invalid_Guid_In_Body : ValidateMatchingDocumentUuidsMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();
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
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId")
                );
                _requestInfo = Context(frontEndRequest, RequestMethod.PUT);
                _requestInfo.ParsedBody = JsonNode.Parse(jsonData)!;
                _requestInfo.PathComponents = _requestInfo.PathComponents with
                {
                    DocumentUuid = new DocumentUuid(new(id)),
                };

                await Middleware().Execute(_requestInfo, Next());
            }

            [Test]
            public void It_provides_error_response()
            {
                _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
            }

            [Test]
            public void It_returns_message_body_with_error()
            {
                _requestInfo
                    .FrontendResponse.Body?.ToJsonString()
                    .Should()
                    .Contain("Request body id must match the id in the url.");
            }
        }

        [TestFixture]
        [Parallelizable]
        public class Given_An_Empty_Id_In_Body : ValidateMatchingDocumentUuidsMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();
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
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId")
                );
                _requestInfo = Context(frontEndRequest, RequestMethod.PUT);
                _requestInfo.ParsedBody = JsonNode.Parse(jsonData)!;
                _requestInfo.PathComponents = _requestInfo.PathComponents with
                {
                    DocumentUuid = new DocumentUuid(new(id)),
                };

                await Middleware().Execute(_requestInfo, Next());
            }

            [Test]
            public void It_provides_error_response()
            {
                _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
            }

            [Test]
            public void It_returns_message_body_with_error()
            {
                _requestInfo
                    .FrontendResponse.Body?.ToJsonString()
                    .Should()
                    .Contain("Request body id must match the id in the url.");
            }
        }
    }
}
