// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.ApiSchema;
using EdFi.DataManagementService.Api.Core.Middleware;
using EdFi.DataManagementService.Api.Core.Model;
using EdFi.DataManagementService.Api.Core.Validation;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Unit.Core.Middleware;

public class ValidateEqualityConstraintMiddlewareTests
{
    public static Func<Task> Next()
    {
        return () => Task.CompletedTask;
    }

    public static ApiSchemaDocument SchemaDocument()
    {
        var equalityConstraints = new EqualityConstraint[]
        {
            new(new JsonPath("$.classPeriods[*].classPeriodReference.schoolId"),
                new JsonPath("$.schoolReference.schoolId"))
        };

        return new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource("BellSchedule")
            .WithEqualityConstraints(equalityConstraints)
            .WithEndResource()
            .WithEndProject()
            .ToApiSchemaDocument();
    }

    public static IPipelineStep Middleware()
    {
        var equalityConstraintValidator = new EqualityConstraintValidator();
        return new ValidateEqualityConstraintMiddleware(NullLogger.Instance, equalityConstraintValidator);
    }

    public PipelineContext Context(FrontendRequest frontendRequest)
    {
        PipelineContext _context =
            new(frontendRequest)
            {
                ApiSchemaDocument = SchemaDocument(),
                PathComponents = new(
                    ProjectNamespace: new("ed-fi"),
                    EndpointName: new("bellSchedules"),
                    DocumentUuid: No.DocumentUuid
                )
            };
        _context.ProjectSchema = new ProjectSchema(
            _context.ApiSchemaDocument.FindProjectSchemaNode(new("ed-fi")) ?? new JsonObject(),
            NullLogger.Instance
        );
        _context.ResourceSchema = new ResourceSchema(
            _context.ProjectSchema.FindResourceSchemaNode(new("bellSchedules")) ?? new JsonObject(),
            NullLogger.Instance
        );
        return _context;
    }

    [TestFixture]
    public class Given_a_valid_body : ValidateEqualityConstraintMiddlewareTests
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
                RequestMethod.POST,
                "ed-fi/bellschedules",
                Body: JsonNode.Parse(jsonData),
                new TraceId("traceId")
            );
            _context = Context(frontEndRequest);
            await Middleware().Execute(_context, Next());
        }

        [Test]
        public void It_provides_no_response()
        {
            _context?.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_an_invalid_body_with_not_equal_school_ids : ValidateEqualityConstraintMiddlewareTests
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
                                     "schoolId": null
                                   }
                                 }
                               ],
                               "dates": [],
                               "gradeLevels": []
                             }

                           """;
            var frontEndRequest = new FrontendRequest(
                RequestMethod.POST,
                "ed-fi/bellschedules",
                Body: JsonNode.Parse(jsonData),
                new TraceId("traceId")
            );
            _context = Context(frontEndRequest);
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
            _context?.FrontendResponse.Body.Should().Contain("Data validation failed");
            _context?.FrontendResponse.Body.Should().Contain("Constraint failure: document paths $.classPeriods[*].classPeriodReference.schoolId and $.schoolReference.schoolId must have the same values");
        }
    }
}
