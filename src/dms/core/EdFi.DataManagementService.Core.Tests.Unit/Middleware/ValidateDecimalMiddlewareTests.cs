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
    [TestFixture]
    [Parallelizable]
    public class ValidateDecimalMiddlewareTests
    {
        internal static IPipelineStep Middleware()
        {
            var decimalValidator = new DecimalValidator();
            return new ValidateDecimalMiddleware(NullLogger.Instance, decimalValidator);
        }

        internal static ApiSchemaDocuments SchemaDocuments()
        {
            var decimalInfos = new DecimalValidationInfo[]
            {
                new(new JsonPath("$.yearsOfPriorProfessionalExperience"), 5, 2),
            };

            return new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Staff")
                .WithDecimalPropertyValidationInfos(decimalInfos)
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocuments();
        }

        internal RequestInfo Context(FrontendRequest frontendRequest, RequestMethod method)
        {
            RequestInfo _requestInfo = new(frontendRequest, method)
            {
                ApiSchemaDocuments = SchemaDocuments(),
                PathComponents = new(
                    ProjectEndpointName: new("ed-fi"),
                    EndpointName: new("staffs"),
                    DocumentUuid: No.DocumentUuid
                ),
            };
            _requestInfo.ProjectSchema = _requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
                new("ed-fi")
            )!;
            _requestInfo.ResourceSchema = new ResourceSchema(
                _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("staffs"))
                    ?? new JsonObject()
            );
            return _requestInfo;
        }

        [TestFixture]
        [Parallelizable]
        public class Given_A_Valid_Body : ValidateDecimalMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();

            [SetUp]
            public async Task Setup()
            {
                var jsonData = """

                    {
                        "staffUniqueId": "staff11",
                        "birthDate": "1976-08-19",
                        "firstName": "Barry",
                        "lastSurname": "Peterson",
                        "yearsOfPriorProfessionalExperience": 10,
                        "sexDescriptor":"uri://ed-fi.org/SexDescriptor#Female"
                    }

                    """;
                var frontEndRequest = new FrontendRequest(
                    "ed-fi/staffs",
                    Body: jsonData,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId")
                );
                _requestInfo = Context(frontEndRequest, RequestMethod.POST);
                _requestInfo.ParsedBody = JsonNode.Parse(jsonData)!;
                await Middleware().Execute(_requestInfo, () => Task.CompletedTask);
            }

            [Test]
            public void It_provides_no_response()
            {
                _requestInfo?.FrontendResponse.Should().Be(No.FrontendResponse);
            }
        }

        public class Given_An_Invalid_Decimal : ValidateDecimalMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();

            [SetUp]
            public async Task Setup()
            {
                var jsonData = """

                    {
                        "staffUniqueId": "staff11",
                        "birthDate": "1976-08-19",
                        "firstName": "Barry",
                        "lastSurname": "Peterson",
                        "yearsOfPriorProfessionalExperience": 100000,
                        "sexDescriptor":"uri://ed-fi.org/SexDescriptor#Female"
                    }

                    """;
                var frontEndRequest = new FrontendRequest(
                    "ed-fi/staffs",
                    Body: jsonData,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId")
                );
                _requestInfo = Context(frontEndRequest, RequestMethod.POST);
                _requestInfo.ParsedBody = JsonNode.Parse(jsonData)!;
                await Middleware().Execute(_requestInfo, () => Task.CompletedTask);
            }

            [Test]
            public void It_provides_error_response()
            {
                _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
                _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("Data Validation Failed");
                _requestInfo
                    .FrontendResponse.Body?.ToJsonString()
                    .Should()
                    .Contain("yearsOfPriorProfessionalExperience must be between -999.99 and 999.99.");
            }
        }
    }
}
