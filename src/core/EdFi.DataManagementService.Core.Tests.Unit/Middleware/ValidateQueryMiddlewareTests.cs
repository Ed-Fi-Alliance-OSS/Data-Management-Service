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
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
public class ValidateQueryMiddlewareTests
{
    internal static IPipelineStep Middleware()
    {
        return new ValidateQueryMiddleware(NullLogger.Instance);
    }

    [TestFixture]
    public class Given_Pipeline_Context_With_Wrong_Query_Parameters : ValidateQueryMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        [SetUp]
        public async Task Setup()
        {
            var queryParameters = new Dictionary<string, string>
            {
                { "offset", "I" },
                { "limit", "-1" },
                { "totalCount", "100" },
            };

            FrontendRequest frontendRequest =
                new(
                    Path: "/ed-fi/schools",
                    Body: null,
                    QueryParameters: queryParameters,
                    TraceId: new TraceId("")
                );
            _context = new(frontendRequest, RequestMethod.GET);
            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_should_send_bad_request()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_should_be_errors()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("The request could not be processed.");
        }

        [Test]
        public void It_should_be_offset_errors()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("Offset must be a numeric value greater than or equal to 0.");
        }

        [Test]
        public void It_should_be_limit_errors()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("Limit must be a numeric value greater than or equal to 0.");
        }

        [Test]
        public void It_should_be_total_count_errors()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("TotalCount must be a boolean value.");
        }
    }

    [TestFixture]
    public class Given_Pipeline_Context_With_Invalid_Type_Query_Parameters : ValidateQueryMiddlewareTests
    {
        private PipelineContext _context = No.PipelineContext();

        internal static ApiSchemaDocument DocRefSchemaDocument()
        {
            var result = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("AcademicWeek")
                .WithStartDocumentPathsMapping()
                .WithStartQueryFieldMapping()
                .WithQueryParamPathMapping("beginDate", "$.beginDate", "date")
                .WithQueryParamPathMapping("schoolId", "$.schoolId", "number")
                .WithQueryParamPathMapping("totalInstructionalDays", "$.totalInstructionalDays", "number")
                .WithQueryParamPathMapping("isRequired", "$.isRequired", "boolean")
                .WithQueryParamPathMapping("endDate", "$.endDate", "date-time")
                .WithQueryParamPathMapping("classStartTime", "$.classStartTime", "time")
                .WithEndDocumentPathsMapping()
                .WithEndResource()
                .WithEndProject()
                .ToApiSchemaDocument();

            return result;
        }

        internal PipelineContext DocRefContext(FrontendRequest frontendRequest, RequestMethod method)
        {
            PipelineContext docRefContext =
                new(frontendRequest, method)
                {
                    ApiSchemaDocument = DocRefSchemaDocument(),
                    PathComponents = new(
                        ProjectNamespace: new("ed-fi"),
                        EndpointName: new("academicWeeks"),
                        DocumentUuid: No.DocumentUuid
                    ),
                };
            docRefContext.ProjectSchema = new ProjectSchema(
                docRefContext.ApiSchemaDocument.FindProjectSchemaNode(new("ed-fi")) ?? new JsonObject(),
                NullLogger.Instance
            );
            docRefContext.ResourceSchema = new ResourceSchema(
                docRefContext.ProjectSchema.FindResourceSchemaNode(new("academicWeeks")) ?? new JsonObject()
            );

            if (docRefContext.FrontendRequest.Body != null)
            {
                var body = JsonNode.Parse(docRefContext.FrontendRequest.Body);
                if (body != null)
                {
                    docRefContext.ParsedBody = body;
                }
            }

            return docRefContext;
        }

        [SetUp]
        public async Task Setup()
        {
            var queryParameters = new Dictionary<string, string>
            {
                { "beginDate", "Word" },
                { "totalInstructionalDays", "Total" },
                { "schoolId", "School" },
                { "isRequired", "123"},
                { "endDate", "DateTime"},
                { "classStartTime", "Time"}
            };

            FrontendRequest frontendRequest =
                new(
                    Path: "/ed-fi/academicWeeks",
                    Body: null,
                    QueryParameters: queryParameters,
                    TraceId: new TraceId("")
                );

            _context = DocRefContext(frontendRequest, RequestMethod.GET);

            await Middleware().Execute(_context, NullNext);
        }

        [Test]
        public void It_should_send_bad_request()
        {
            _context?.FrontendResponse.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_should_be_beginDate_error()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("$.beginDate");

            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("is not valid for beginDate.");
        }

        [Test]
        public void It_should_be_totalInstructionalDays_error()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("$.totalInstructionalDays");

            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("is not valid for totalInstructionalDays.");
        }

        [Test]
        public void It_should_be_SchoolId_error()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("$.schoolId");

            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("is not valid for schoolId.");
        }

        [Test]
        public void It_should_validate_boolean()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("$.isRequired");

            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("is not valid for isRequired.");
        }

        [Test]
        public void It_should_be_endDate_error()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("$.endDate");

            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("is not valid for endDate.");
        }

        [Test]
        public void It_should_be_time_error()
        {
            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("$.classStartTime");

            _context
                .FrontendResponse.Body?.ToJsonString()
                .Should()
                .Contain("is not valid for classStartTime.");
        }
    }
}
