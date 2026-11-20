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
            RequestInfo _requestInfo = new(frontendRequest, method, No.ServiceProvider)
            {
                ApiSchemaDocuments = SchemaDocuments(),
                PathComponents = new(
                    ProjectEndpointName: new("ed-fi"),
                    EndpointName: new("staffs"),
                    Operation: ResourcePathOperation.Collection.Instance
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
        [SetCulture("en-US")]
        public class Given_Decimal_Range_Boundaries : ValidateDecimalMiddlewareTests
        {
            [TestCase("999.99")]
            [TestCase("-999.99")]
            public async Task It_continues_exactly_once_at_an_allowed_endpoint(string value)
            {
                var requestInfo = RequestWithValue(value);
                int nextCalls = 0;

                await Middleware()
                    .Execute(
                        requestInfo,
                        () =>
                        {
                            nextCalls++;
                            return Task.CompletedTask;
                        }
                    );

                nextCalls.Should().Be(1);
                requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
            }

            [TestCase("999.991")]
            [TestCase("-999.991")]
            public async Task It_rejects_an_outside_value_without_continuing(string value)
            {
                var requestInfo = RequestWithValue(value);
                int nextCalls = 0;

                await Middleware()
                    .Execute(
                        requestInfo,
                        () =>
                        {
                            nextCalls++;
                            return Task.CompletedTask;
                        }
                    );

                nextCalls.Should().Be(0);
                requestInfo.FrontendResponse.StatusCode.Should().Be(400);
                requestInfo.FrontendResponse.Headers.Should().BeEmpty();
                JsonNode
                    .DeepEquals(
                        requestInfo.FrontendResponse.Body,
                        JsonNode.Parse(
                            """
                            {
                              "detail":"Data validation failed. See 'validationErrors' for details.",
                              "type":"urn:ed-fi:api:bad-request:data-validation-failed",
                              "title":"Data Validation Failed",
                              "status":400,
                              "correlationId":"boundary-test",
                              "validationErrors":{
                                "$.yearsOfPriorProfessionalExperience":[
                                  "yearsOfPriorProfessionalExperience must be between -999.99 and 999.99."
                                ]
                              },
                              "errors":[]
                            }
                            """
                        )
                    )
                    .Should()
                    .BeTrue();
            }

            private RequestInfo RequestWithValue(string value)
            {
                string body = "{\"yearsOfPriorProfessionalExperience\":" + value + "}";
                var requestInfo = Context(
                    new FrontendRequest(
                        "ed-fi/staffs",
                        Body: body,
                        Form: null,
                        Headers: [],
                        QueryParameters: [],
                        TraceId: new("boundary-test"),
                        RouteQualifiers: []
                    ),
                    RequestMethod.POST
                );
                requestInfo.ParsedBody = JsonNode.Parse(body)!;
                return requestInfo;
            }
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
                    Form: null,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId"),
                    RouteQualifiers: []
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
                    Form: null,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId"),
                    RouteQualifiers: []
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

        /// <summary>
        /// A JSON number too large for <see cref="decimal" /> satisfies the resource's
        /// <c>"type": "number"</c> schema, so it reaches this middleware. Reading it must produce the
        /// same data-validation rejection as any other unusable decimal rather than an exception,
        /// which the pipeline would answer as a 500.
        /// </summary>
        [TestFixture]
        [Parallelizable]
        public class Given_A_Decimal_Too_Large_For_The_Type : ValidateDecimalMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();

            [SetUp]
            public async Task Setup()
            {
                var jsonData = """
                    {
                        "staffUniqueId": "staff11",
                        "yearsOfPriorProfessionalExperience": 1e400
                    }
                    """;
                var frontEndRequest = new FrontendRequest(
                    "ed-fi/staffs",
                    Body: jsonData,
                    Form: null,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId"),
                    RouteQualifiers: []
                );
                _requestInfo = Context(frontEndRequest, RequestMethod.POST);
                _requestInfo.ParsedBody = JsonNode.Parse(jsonData)!;
                await Middleware().Execute(_requestInfo, () => Task.CompletedTask);
            }

            [Test]
            public void It_provides_error_response()
            {
                _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
                _requestInfo
                    .FrontendResponse.Body?.ToJsonString()
                    .Should()
                    .Contain("Value is not a valid decimal.");
            }
        }

        /// <summary>
        /// An explicit null at a decimal path is rejected upstream by document validation for every
        /// resource the Ed-Fi schema declares, since none of them make a number nullable. Covered
        /// directly because the middleware must not depend on that: the read is the same one that has
        /// to survive an unusable value.
        /// </summary>
        [TestFixture]
        [Parallelizable]
        public class Given_A_Null_At_A_Decimal_Path : ValidateDecimalMiddlewareTests
        {
            private RequestInfo _requestInfo = No.RequestInfo();

            [SetUp]
            public async Task Setup()
            {
                var jsonData = """
                    {
                        "staffUniqueId": "staff11",
                        "yearsOfPriorProfessionalExperience": null
                    }
                    """;
                var frontEndRequest = new FrontendRequest(
                    "ed-fi/staffs",
                    Body: jsonData,
                    Form: null,
                    Headers: [],
                    QueryParameters: [],
                    TraceId: new TraceId("traceId"),
                    RouteQualifiers: []
                );
                _requestInfo = Context(frontEndRequest, RequestMethod.POST);
                _requestInfo.ParsedBody = JsonNode.Parse(jsonData)!;
                await Middleware().Execute(_requestInfo, () => Task.CompletedTask);
            }

            [Test]
            public void It_provides_error_response()
            {
                _requestInfo.FrontendResponse.StatusCode.Should().Be(400);
                _requestInfo
                    .FrontendResponse.Body?.ToJsonString()
                    .Should()
                    .Contain("Value is not a valid decimal.");
            }
        }
    }
}
