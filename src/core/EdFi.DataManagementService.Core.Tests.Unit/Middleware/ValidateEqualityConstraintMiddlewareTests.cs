// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
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
using static EdFi.DataManagementService.Core.UtilityService;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

public class ValidateEqualityConstraintMiddlewareTests
{
    public static Func<Task> Next()
    {
        return () => Task.CompletedTask;
    }

    internal static ApiSchemaDocument SchemaDocument()
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
            .ToApiSchemaDocument();
    }

    internal static IPipelineStep Middleware()
    {
        var equalityConstraintValidator = new EqualityConstraintValidator();
        return new ValidateEqualityConstraintMiddleware(NullLogger.Instance, equalityConstraintValidator);
    }

    internal PipelineContext Context(FrontendRequest frontendRequest, RequestMethod method)
    {
        PipelineContext _context =
            new(frontendRequest, method)
            {
                ApiSchemaDocument = SchemaDocument(),
                PathComponents = new(
                    ProjectNamespace: new("ed-fi"),
                    EndpointName: new("bellSchedules"),
                    DocumentUuid: No.DocumentUuid
                ),
            };
        _context.ProjectSchema = new ProjectSchema(
            _context.ApiSchemaDocument.FindProjectSchemaNode(new("ed-fi")) ?? new JsonObject(),
            NullLogger.Instance
        );
        _context.ResourceSchema = new ResourceSchema(
            _context.ProjectSchema.FindResourceSchemaNode(new("bellSchedules")) ?? new JsonObject()
        );
        return _context;
    }

    [TestFixture]
    public class Given_A_Valid_Body : ValidateEqualityConstraintMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

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
                QueryParameters: [],
                new TraceId("traceId")
            );
            _context = Context(frontEndRequest, RequestMethod.POST);
            await Middleware().Execute(_context, Next());
        }

        [Test]
        public void It_provides_no_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_An_Invalid_Body_With_Not_Equal_School_Ids : ValidateEqualityConstraintMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

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
                QueryParameters: [],
                new TraceId("traceId")
            );
            _context = Context(frontEndRequest, RequestMethod.POST);

            if (_context.FrontendRequest.Body != null)
            {
                var body = JsonNode.Parse(_context.FrontendRequest.Body);
                if (body != null)
                {
                    _context.ParsedBody = body;
                }
            }

            await Middleware().Execute(_context, Next());
        }

        [Test]
        public void It_returns_status_400()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_returns_message_body_with_failures()
        {
            _context.FrontendResponse.Body?.ToJsonString().Should().Contain("Data Validation Failed");

            string response = JsonSerializer.Serialize(_context.FrontendResponse.Body, SerializerOptions);

            response
                .Should()
                .Contain(
                    "\"validationErrors\":{\"$.classPeriods[*].classPeriodReference.schoolId\":[\"All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '2', '1'\"],\"$.schoolReference.schoolId\":[\"All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '2', '1'\"]}"
                );
        }
    }
}
