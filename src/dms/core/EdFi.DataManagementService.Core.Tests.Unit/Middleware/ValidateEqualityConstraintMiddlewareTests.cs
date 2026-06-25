// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Profile;
using EdFi.DataManagementService.Core.Validation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.UtilityService;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

public class ValidateEqualityConstraintMiddlewareTests
{
    public static Func<Task> Next()
    {
        return () => Task.CompletedTask;
    }

    internal static ApiSchemaDocuments SchemaDocuments()
    {
        var equalityConstraints = new EqualityConstraint[]
        {
            new(
                new JsonPath("$.classPeriods[*].classPeriodReference.schoolId"),
                new JsonPath("$.schoolReference.schoolId")
            ),
        };

        return new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("BellSchedule")
            .WithEqualityConstraints(equalityConstraints)
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocuments();
    }

    internal static IPipelineStep Middleware()
    {
        var equalityConstraintValidator = new EqualityConstraintValidator();
        return new ValidateEqualityConstraintMiddleware(NullLogger.Instance, equalityConstraintValidator);
    }

    internal RequestInfo Context(FrontendRequest frontendRequest, RequestMethod method)
    {
        RequestInfo _requestInfo = new(frontendRequest, method, No.ServiceProvider)
        {
            ApiSchemaDocuments = SchemaDocuments(),
            PathComponents = new(
                ProjectEndpointName: new("ed-fi"),
                EndpointName: new("bellSchedules"),
                DocumentUuid: No.DocumentUuid
            ),
        };
        _requestInfo.ProjectSchema = _requestInfo.ApiSchemaDocuments.FindProjectSchemaForProjectNamespace(
            new("ed-fi")
        )!;
        _requestInfo.ResourceSchema = new ResourceSchema(
            _requestInfo.ProjectSchema.FindResourceSchemaNodeByEndpointName(new("bellSchedules"))
                ?? new JsonObject()
        );
        return _requestInfo;
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Body : ValidateEqualityConstraintMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var jsonData = """

                {
                    "schoolReference": {
                      "schoolId": 255901001
                    },
                    "bellScheduleName": "Test Schedule",
                    "totalInstructionalTime": 325,
                    "classPeriods": [
                      {
                        "classPeriodReference": {
                          "classPeriodName": "01 - Traditional",
                          "schoolId": 255901001
                        }
                      },
                      {
                        "classPeriodReference": {
                          "classPeriodName": "02 - Traditional",
                          "schoolId": 255901001
                        }
                      }
                    ],
                    "dates": [],
                    "gradeLevels": []
                  }

                """;
            var frontEndRequest = new FrontendRequest(
                "ed-fi/bellschedules",
                Body: jsonData,
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
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
    public class Given_An_Invalid_Body_With_Not_Equal_School_Ids : ValidateEqualityConstraintMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            var jsonData = """

                {
                    "schoolReference": {
                      "schoolId": 1
                    },
                    "bellScheduleName": "Test Schedule",
                    "totalInstructionalTime": 325,
                    "classPeriods": [
                      {
                        "classPeriodReference": {
                          "classPeriodName": "01 - Traditional",
                          "schoolId": 2
                        }
                      },
                      {
                        "classPeriodReference": {
                          "classPeriodName": "02 - Traditional",
                          "schoolId": 2
                        }
                      }
                    ],
                    "dates": [],
                    "gradeLevels": []
                  }

                """;
            var frontEndRequest = new FrontendRequest(
                "ed-fi/bellschedules",
                Body: jsonData,
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);

            if (_requestInfo.FrontendRequest.Body != null)
            {
                var body = JsonNode.Parse(_requestInfo.FrontendRequest.Body);
                if (body != null)
                {
                    _requestInfo.ParsedBody = body;
                }
            }

            await Middleware().Execute(_requestInfo, Next());
        }

        [Test]
        public void It_returns_status_400()
        {
            _requestInfo?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_failures()
        {
            _requestInfo.FrontendResponse.Body?.ToJsonString().Should().Contain("Data Validation Failed");

            string response = JsonSerializer.Serialize(_requestInfo.FrontendResponse.Body, SerializerOptions);

            response
                .Should()
                .Contain(
                    "\"validationErrors\":{\"$.classPeriods[*].classPeriodReference.schoolId\":[\"All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '2', '1'\"],\"$.schoolReference.schoolId\":[\"All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '2', '1'\"]}"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Conflicting_Hidden_Collection_Stripped_By_A_Writable_Profile
        : ValidateEqualityConstraintMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            // DMS-1229: the raw submitted body carries a classPeriods collection whose
            // classPeriodReference.schoolId (2) conflicts with the resource-identity
            // schoolReference.schoolId (1). A writable profile hides classPeriods, so the shaper
            // strips it from the shaped write surface while preserving the identity reference.
            // Equality-constraint validation must run against the shaped body and ignore the hidden
            // conflict rather than reject the request.
            var rawBody = """
                {
                    "schoolReference": { "schoolId": 1 },
                    "bellScheduleName": "Test Schedule",
                    "totalInstructionalTime": 325,
                    "classPeriods": [
                      {
                        "classPeriodReference": {
                          "classPeriodName": "01 - Traditional",
                          "schoolId": 2
                        }
                      }
                    ],
                    "dates": [],
                    "gradeLevels": []
                }
                """;

            var shapedBody = """
                {
                    "schoolReference": { "schoolId": 1 },
                    "bellScheduleName": "Test Schedule",
                    "totalInstructionalTime": 325,
                    "dates": [],
                    "gradeLevels": []
                }
                """;

            var frontEndRequest = new FrontendRequest(
                "ed-fi/bellschedules",
                Body: rawBody,
                Form: null,
                Headers: [],
                QueryParameters: [],
                TraceId: new TraceId("traceId"),
                RouteQualifiers: []
            );
            _requestInfo = Context(frontEndRequest, RequestMethod.POST);
            _requestInfo.ParsedBody = JsonNode.Parse(rawBody)!;
            _requestInfo.BackendProfileWriteContext = new BackendProfileWriteContext(
                Request: new ProfileAppliedWriteRequest(
                    WritableRequestBody: JsonNode.Parse(shapedBody)!,
                    RootResourceCreatable: true,
                    RequestScopeStates: [],
                    VisibleRequestCollectionItems: []
                ),
                ProfileName: "TestProfile",
                CompiledScopeCatalog: [],
                StoredStateProjectionInvoker: null!
            );

            await Middleware()
                .Execute(
                    _requestInfo,
                    () =>
                    {
                        _nextCalled = true;
                        return Task.CompletedTask;
                    }
                );
        }

        [Test]
        public void It_continues_the_pipeline()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_provides_no_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }
}
